using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Xml.Linq;

namespace BlazeCs.TestHost;

/// <summary>
/// A single-use test host. The daemon starts it ahead of time, so the runtime is already up when a run
/// needs it; it reads one "framework assembly" line from stdin, runs that test assembly in-process, writes
/// the result as the last line and exits. Test output goes to stdout. Exiting keeps every run's statics,
/// threads and console redirections apart, and lets the tests load into an ordinary (non-collectible)
/// context, where precompiled code is used: measured on the fixture, about twice as fast as a collectible one.
/// </summary>
static class Program
{
    const string ResultPrefix = "\u0001result:";

    static void Main()
    {
        var protocol = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        // End of input means the daemon stopped (or died) without needing this host.
        if (Console.ReadLine() is not { } request)
            return;
        var separator = request.IndexOf(' ');
        var result = Run(request[..separator], request[(separator + 1)..]);
        Console.Out.Flush();
        protocol.WriteLine(ResultPrefix + JsonSerializer.Serialize(result));
    }

    static Result Run(string framework, string assemblyPath)
    {
        var (output, error) = (Console.Out, Console.Error);
        Func<Result> run;
        try
        {
            // Tests find files relative to their output directory, as under `dotnet test`: through the
            // current directory, or AppContext.BaseDirectory (WebApplicationFactory looks for deps.json there).
            var directory = Path.GetDirectoryName(assemblyPath)!;
            Environment.CurrentDirectory = directory;
            AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", directory + Path.DirectorySeparatorChar);
            var context = new TestContext(assemblyPath);
            _ = context.EnterContextualReflection(); // for the rest of this process
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            // A reference this host can't load (an assembly built for a newer runtime) would surface as an
            // error from deep inside a framework's runner, or not at all.
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                try { _ = context.LoadFromAssemblyName(reference); }
                catch (Exception e) when (e is IOException or BadImageFormatException)
                {
                    return new Result(0, 0, 0, 0, $"the test host (.NET {Environment.Version}) can't load {reference.FullName}: {e.Message}", null);
                }
            }
            run = framework switch
            {
                "xunit3" => XunitV3(context, assembly, output),
                "nunit" => NUnit(context, assembly, output),
                "mstest" => MSTest(context, assembly, output),
                _ => throw new NotSupportedException($"no runner for {framework}"),
            };
        }
        catch (Exception e)
        {
            // Nothing ran: the daemon falls back to `dotnet test`.
            var cause = Unwrap(e);
            return new Result(0, 0, 0, 0, null, $"{cause.GetType().Name}: {cause.Message}");
        }

        try
        {
            return run();
        }
        catch (Exception e)
        {
            return new Result(0, 0, 0, 0, Unwrap(e).ToString(), null);
        }
        finally
        {
            // MSTest leaves its own writers installed.
            Console.SetOut(output);
            Console.SetError(error);
        }
    }

    /// <summary>
    /// xUnit's in-process runner, which takes the test assembly explicitly: the assembly's own Main would
    /// run whatever Assembly.GetEntryAssembly() is, this host. Totals come from its XML report.
    /// </summary>
    static Func<Result> XunitV3(AssemblyLoadContext context, Assembly assembly, TextWriter output)
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"blaze-cs-{Environment.ProcessId}.xml");
        var runnerType = context.LoadFromAssemblyName(new AssemblyName("xunit.v3.runner.inproc.console"))
            .GetType("Xunit.Runner.InProc.SystemConsole.ConsoleRunner", throwOnError: true)!;
        string[] arguments = ["-noLogo", "-noColor", "-xml", reportPath];
        var runner = (IDisposable)Activator.CreateInstance(runnerType, [arguments, assembly])!;
        var entryPoint = runnerType.GetMethod("EntryPoint") ?? throw new MissingMethodException(runnerType.FullName, "EntryPoint");
        return () =>
        {
            using (runner)
            {
                // Not Console.In: this process's stdin is the daemon's channel.
                ((Task<int>)entryPoint.Invoke(runner, [TextReader.Null, output])!).GetAwaiter().GetResult();
            }
            try
            {
                var report = XDocument.Load(reportPath).Root!.Element("assembly")
                    ?? throw new InvalidOperationException("xUnit wrote no results");
                int Count(string name) => int.Parse(report.Attribute(name)?.Value ?? "0");
                return new Result(Count("total"), Count("passed"), Count("failed") + Count("errors"), Count("skipped"), null, null);
            }
            finally
            {
                File.Delete(reportPath);
            }
        };
    }

    /// <summary>
    /// NUnit's FrameworkController, which its engine and VSTest adapter drive too: RunTests returns the
    /// results as XML (a test-suite with totals, and a test-case per test with any failure and output).
    /// </summary>
    static Func<Result> NUnit(AssemblyLoadContext context, Assembly assembly, TextWriter output)
    {
        var type = context.LoadFromAssemblyName(new AssemblyName("nunit.framework"))
            .GetType("NUnit.Framework.Api.FrameworkController", throwOnError: true)!;
        var settings = new Hashtable { ["WorkDirectory"] = Path.GetDirectoryName(assembly.Location)! };
        var controller = Activator.CreateInstance(type, [assembly, "0-", settings])!;
        var load = type.GetMethod("LoadTests", Type.EmptyTypes) ?? throw new MissingMethodException(type.FullName, "LoadTests");
        var runTests = type.GetMethod("RunTests", [typeof(string)]) ?? throw new MissingMethodException(type.FullName, "RunTests");
        return () =>
        {
            load.Invoke(controller, []);
            var suite = XElement.Parse((string)runTests.Invoke(controller, ["<filter/>"])!);
            foreach (var test in suite.Descendants("test-case").Where(t => t.Attribute("result")?.Value == "Failed"))
            {
                var failure = test.Element("failure");
                ReportFailure(output, test.Attribute("fullname")?.Value, failure?.Element("message")?.Value,
                    failure?.Element("stack-trace")?.Value, test.Element("output")?.Value);
            }
            int Count(string name) => int.Parse(suite.Attribute(name)?.Value ?? "0");
            // As the VSTest adapter does by default: warnings and inconclusive results count as skipped.
            return new Result(Count("total"), Count("passed"), Count("failed"),
                Count("skipped") + Count("inconclusive") + Count("warnings"), null, null);
        };
    }

    /// <summary>
    /// MSTest's VSTest executor, as vstest's testhost runs it. Its run context and framework handle are
    /// proxies for the test's own copy of the VSTest object model, so the host doesn't reference it.
    /// </summary>
    static Func<Result> MSTest(AssemblyLoadContext context, Assembly assembly, TextWriter output)
    {
        var executor = Activator.CreateInstance(context.LoadFromAssemblyName(new AssemblyName("MSTest.TestAdapter"))
            .GetType("Microsoft.VisualStudio.TestPlatform.MSTest.TestAdapter.MSTestExecutor", throwOnError: true)!)!;
        var objectModel = context.LoadFromAssemblyName(new AssemblyName("Microsoft.VisualStudio.TestPlatform.ObjectModel"));
        Type Adapter(string name) => objectModel.GetType($"Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter.{name}", throwOnError: true)!;
        var runTests = Adapter("ITestExecutor").GetMethod("RunTests", [typeof(IEnumerable<string>), Adapter("IRunContext"), Adapter("IFrameworkHandle")])
            ?? throw new MissingMethodException("ITestExecutor", "RunTests");
        var settings = Proxy.Create(Adapter("IRunSettings"), (method, _) =>
            method.Name == "get_SettingsXml" ? "<RunSettings><RunConfiguration /></RunSettings>" : null);
        var runContext = Proxy.Create(Adapter("IRunContext"), (method, _) => method.Name switch
        {
            "get_RunSettings" => settings,
            "get_TestRunDirectory" => Path.GetDirectoryName(assembly.Location),
            _ => null,
        });
        var recorder = new MSTestRecorder(output);
        var handle = Proxy.Create(Adapter("IFrameworkHandle"), recorder.Invoke);
        return () =>
        {
            runTests.Invoke(executor, [new[] { assembly.Location }, runContext, handle]);
            return recorder.Result();
        };
    }

    /// <summary>Counts what MSTest records; tests run in parallel, so from many threads.</summary>
    sealed class MSTestRecorder(TextWriter output)
    {
        int _total, _passed, _failed, _skipped;

        public Result Result() => new(_total, _passed, _failed, _skipped, null, null);

        public object? Invoke(MethodInfo method, object?[] arguments)
        {
            if (method.Name == "RecordResult")
                Record(arguments[0]!);
            else if (method.Name == "SendMessage" && arguments[0]?.ToString() is "Warning" or "Error")
            {
                lock (output)
                    output.WriteLine($"  {arguments[0]}: {arguments[1]}");
            }
            return null;
        }

        void Record(object result)
        {
            Interlocked.Increment(ref _total);
            switch (Get(result, "Outcome")?.ToString())
            {
                case "Passed":
                    Interlocked.Increment(ref _passed);
                    break;
                case "Failed":
                    Interlocked.Increment(ref _failed);
                    var stdout = ((IEnumerable)Get(result, "Messages")!).Cast<object>()
                        .Where(m => Get(m, "Category") as string == "StdOutMsgs").Select(m => Get(m, "Text") as string);
                    ReportFailure(output, Get(result, "DisplayName") as string ?? Get(Get(result, "TestCase")!, "FullyQualifiedName") as string,
                        Get(result, "ErrorMessage") as string, Get(result, "ErrorStackTrace") as string, string.Concat(stdout));
                    break;
                default: // skipped, not found, none
                    Interlocked.Increment(ref _skipped);
                    break;
            }
        }

        static object? Get(object target, string property) => target.GetType().GetProperty(property)!.GetValue(target);
    }

    /// <summary>A failed test as `dotnet test` prints it: name, message, stack trace and its output.</summary>
    static void ReportFailure(TextWriter output, string? name, string? message, string? stackTrace, string? stdout)
    {
        lock (output)
        {
            output.WriteLine($"  Failed {name}");
            foreach (var (heading, text) in new[] { ("Error Message", message), ("Stack Trace", stackTrace), ("Standard Output Messages", stdout) })
            {
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                output.WriteLine($"  {heading}:");
                foreach (var line in text.Trim().Split('\n'))
                    output.WriteLine($"   {line.TrimEnd('\r')}");
            }
        }
    }

    static Exception Unwrap(Exception e) => e is TargetInvocationException { InnerException: { } inner } ? inner : e;

    sealed record Result(int Total, int Passed, int Failed, int Skipped, string? Error, string? Unsupported);

    /// <summary>Implements an interface from the test's load context by handing each call to a function.</summary>
    class Proxy : DispatchProxy
    {
        Func<MethodInfo, object?[], object?> _handler = null!;

        public static object Create(Type interfaceType, Func<MethodInfo, object?[], object?> handler)
        {
            var proxy = DispatchProxy.Create(interfaceType, typeof(Proxy));
            ((Proxy)proxy)._handler = handler;
            return proxy;
        }

        // Members the handler doesn't answer return their type's default (false, 0, null).
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) =>
            _handler(method!, arguments ?? []) ?? (method!.ReturnType is { IsValueType: true } type && type != typeof(void)
                ? Activator.CreateInstance(type) : null);
    }

    /// <summary>
    /// Resolves the test project's own dependencies from its deps.json; the shared frameworks (.NET,
    /// ASP.NET Core) come from this process.
    /// </summary>
    sealed class TestContext(string assemblyPath) : AssemblyLoadContext($"tests:{assemblyPath}")
    {
        readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

        protected override Assembly? Load(AssemblyName name) =>
            _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;

        protected override IntPtr LoadUnmanagedDll(string name) =>
            _resolver.ResolveUnmanagedDllToPath(name) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
