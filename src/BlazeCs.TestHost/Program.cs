using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace BlazeCs.TestHost;

/// <summary>
/// A single-use test host. The daemon starts it ahead of time, so the runtime is already up when a run
/// needs it; it reads one request line from stdin, runs that test assembly in-process, writes
/// the result as the last line and exits. Started for one test project (its framework, assembly and a
/// warm-up assembly as arguments), it gets that project's run ready while it waits (see Prepare). Stdin
/// closing at any point means the daemon is gone (it stopped or died), and the host exits: at once while
/// it runs tests, and after preparing while it waits. Test output goes to stdout. Exiting keeps every run's statics,
/// threads and console redirections apart, and lets the tests load into an ordinary (non-collectible)
/// context, where precompiled code is used: measured on the fixture, about twice as fast as a collectible one.
/// </summary>
static partial class Program
{
    const string ResultPrefix = "\u0001result:";

    static void Main(string[] args)
    {
        var protocol = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        // Started for a test project: <framework> <assembly> [--run-settings=<file>] [--warm-up=<assembly>].
        string? Option(string name) => args.FirstOrDefault(a => a.StartsWith(name, StringComparison.Ordinal))?[name.Length..];
        var prepared = args is [var framework, var assembly, ..] ? Prepare(framework, assembly, Option("--run-settings="), Option("--warm-up=")) : null;
        // End of input means the daemon stopped (or died) without needing this host.
        if (Console.ReadLine() is not { } request)
            return;
        WatchDaemon();
        var result = Run(JsonSerializer.Deserialize(request, ProtocolJson.Default.Request)!, prepared);
        Console.Out.Flush();
        protocol.WriteLine(ResultPrefix + JsonSerializer.Serialize(result, ProtocolJson.Default.Result));
    }

    /// <summary>
    /// Kills this process when stdin ends, on Linux and Windows alike: the daemon's end of the pipe closes with
    /// the daemon, however it died. The daemon sends nothing after the request, so this reads nothing else.
    /// The raw stream, since the tests may read Console.In.
    /// </summary>
    static void WatchDaemon() => new Thread(() =>
    {
        var stdin = Console.OpenStandardInput();
        while (stdin.Read(new byte[256]) > 0) { }
        Process.GetCurrentProcess().Kill();
    }) { IsBackground = true }.Start();

    /// <summary>
    /// Gets a test project's run ready before the daemon asks for it: creates the project's load context, loads
    /// into it every NuGet package its deps.json lists (the test framework, runner and adapters among them),
    /// and rehearses the framework on the warm-up assembly, a few tests the daemon compiled against the
    /// project's references, so the framework's own code is compiled before the project's tests need it.
    /// The project's outputs stay unloaded: an edit may rebuild them, so the run loads them as the build left
    /// them. Whatever fails here leaves less ready, and the run still uses this context: a second one would
    /// load every package again, and the proxies made for the first one's interfaces would stand in for the
    /// second's (DispatchProxy finds the interfaces' assemblies by name).
    /// </summary>
    static TestContext? Prepare(string framework, string assemblyPath, string? runSettings, string? warmUp)
    {
        var (output, error) = (Console.Out, Console.Error);
        TestContext? context = null;
        try
        {
            // Its environment variables are set before the framework starts, as vstest sets them on its testhost.
            var settings = Settings.Read(runSettings);
            var directory = EnterDirectory(assemblyPath);
            context = new TestContext(assemblyPath);
            _ = context.EnterContextualReflection(); // as for the run: the rehearsal loads by name too
            foreach (var package in PackageAssemblies(assemblyPath))
            {
                try { _ = context.LoadFromAssemblyName(package); }
                catch (Exception e) when (e is IOException or BadImageFormatException) { } // a run that needs it fails on it
            }
            if (warmUp is not null)
            {
                // Its results and output go nowhere: stdout carries the run's.
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
                _ = Runner(framework, context, context.LoadFromAssemblyPath(warmUp), directory, TextWriter.Null, settings)();
            }
        }
        catch (Exception) { }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
        return context;
    }

    /// <summary>The runtime assemblies of the NuGet packages a test project's deps.json lists.</summary>
    static List<AssemblyName> PackageAssemblies(string assemblyPath)
    {
        using var deps = JsonDocument.Parse(File.ReadAllBytes(Path.ChangeExtension(assemblyPath, ".deps.json")));
        var root = deps.RootElement;
        var libraries = root.GetProperty("libraries");
        var target = root.GetProperty("targets").GetProperty(root.GetProperty("runtimeTarget").GetProperty("name").GetString()!);
        return [.. target.EnumerateObject()
            .Where(library => libraries.GetProperty(library.Name).GetProperty("type").GetString() == "package")
            .SelectMany(library => library.Value.TryGetProperty("runtime", out var runtime) ? runtime.EnumerateObject() : [])
            .Select(asset => new AssemblyName(Path.GetFileNameWithoutExtension(asset.Name)))];
    }

    /// <summary>
    /// Tests find files relative to their output directory, as under `dotnet test`: through the current
    /// directory, or AppContext.BaseDirectory (WebApplicationFactory looks for deps.json there). Returns it.
    /// </summary>
    static string EnterDirectory(string assemblyPath)
    {
        var directory = Path.GetDirectoryName(assemblyPath)!;
        Environment.CurrentDirectory = directory;
        AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", directory + Path.DirectorySeparatorChar);
        return directory;
    }

    static Result Run(Request request, TestContext? prepared)
    {
        var (output, error) = (Console.Out, Console.Error);
        var assemblyPath = request.Assembly;
        Func<Result> run;
        try
        {
            var settings = Settings.Read(request.RunSettings);
            var directory = EnterDirectory(assemblyPath);
            var context = prepared?.AssemblyPath == assemblyPath ? prepared : new TestContext(assemblyPath);
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
            run = Runner(request.Framework, context, assembly, directory, output, settings);
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
    /// A framework's runner for one test assembly, ready to run. The directory is the test project's output
    /// directory, where xUnit v2's adapter is.
    /// </summary>
    static Func<Result> Runner(string framework, AssemblyLoadContext context, Assembly assembly, string directory, TextWriter output, Settings settings) => framework switch
    {
        "xunit3" => XunitV3(context, assembly, output),
        "xunit2" => VSTest(context, assembly, output, settings, context.LoadFromAssemblyPath(XunitV2Adapter(directory))
            .GetType("Xunit.Runner.VisualStudio.VsTestRunner", throwOnError: true)!),
        "nunit" => NUnit(context, assembly, output, settings),
        "mstest" => VSTest(context, assembly, output, settings, context.LoadFromAssemblyName(new AssemblyName("MSTest.TestAdapter"))
            .GetType("Microsoft.VisualStudio.TestPlatform.MSTest.TestAdapter.MSTestExecutor", throwOnError: true)!),
        _ => throw new NotSupportedException($"no runner for {framework}"),
    };

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
                // Its total includes explicit tests that weren't asked for, which `dotnet test` leaves out.
                return new Result(Count("total") - Count("not-run"), Count("passed"), Count("failed") + Count("errors"), Count("skipped"), null, null);
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
    static Func<Result> NUnit(AssemblyLoadContext context, Assembly assembly, TextWriter output, Settings settings)
    {
        var type = context.LoadFromAssemblyName(new AssemblyName("nunit.framework"))
            .GetType("NUnit.Framework.Api.FrameworkController", throwOnError: true)!;
        // TestParametersDictionary is how NUnit's VSTest adapter hands over TestRunParameters (TestContext.Parameters).
        var controllerSettings = new Hashtable
        {
            ["WorkDirectory"] = Path.GetDirectoryName(assembly.Location)!,
            ["TestParametersDictionary"] = settings.Parameters,
        };
        var controller = Activator.CreateInstance(type, [assembly, "0-", controllerSettings])!;
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
    /// A VSTest adapter's executor (MSTest's, xUnit v2's), as vstest's testhost runs it, with the run's
    /// .runsettings. Its run context and framework handle are proxies for the test's own copy of the VSTest
    /// object model, so the host doesn't reference it.
    /// </summary>
    static Func<Result> VSTest(AssemblyLoadContext context, Assembly assembly, TextWriter output, Settings runSettings, Type executorType)
    {
        var executor = Activator.CreateInstance(executorType)!;
        var objectModel = context.LoadFromAssemblyName(new AssemblyName("Microsoft.VisualStudio.TestPlatform.ObjectModel"));
        Type Adapter(string name) => objectModel.GetType($"Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter.{name}", throwOnError: true)!;
        var runTests = Adapter("ITestExecutor").GetMethod("RunTests", [typeof(IEnumerable<string>), Adapter("IRunContext"), Adapter("IFrameworkHandle")])
            ?? throw new MissingMethodException("ITestExecutor", "RunTests");
        var settings = Proxy.Create(Adapter("IRunSettings"), (method, _) =>
            method.Name == "get_SettingsXml" ? runSettings.Xml : null);
        var runContext = Proxy.Create(Adapter("IRunContext"), (method, _) => method.Name switch
        {
            "get_RunSettings" => settings,
            "get_TestRunDirectory" => Path.GetDirectoryName(assembly.Location),
            _ => null,
        });
        var recorder = new VSTestRecorder(output);
        var handle = Proxy.Create(Adapter("IFrameworkHandle"), recorder.Invoke);
        return () =>
        {
            runTests.Invoke(executor, [new[] { assembly.Location }, runContext, handle]);
            return recorder.Result();
        };
    }

    /// <summary>
    /// xUnit v2's VSTest adapter, which isn't in the test's deps.json: vstest finds adapters by file name.
    /// It is xunit.runner.visualstudio.testadapter.dll from xunit.runner.visualstudio 3 on, with "dotnetcore" in the name before.
    /// </summary>
    static string XunitV2Adapter(string directory) =>
        Directory.EnumerateFiles(directory, "xunit.runner.visualstudio*.testadapter.dll").FirstOrDefault()
            ?? throw new FileNotFoundException("no xunit.runner.visualstudio adapter next to the tests");

    /// <summary>Counts what a VSTest adapter records; tests may run in parallel, so from many threads.</summary>
    sealed class VSTestRecorder(TextWriter output)
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

    /// <summary>What to run: a framework (xunit3, xunit2, nunit, mstest), an assembly, and the .runsettings that applies, if any.</summary>
    internal sealed record Request(string Framework, string Assembly, string? RunSettings);

    internal sealed record Result(int Total, int Passed, int Failed, int Skipped, string? Error, string? Unsupported);

    /// <summary>The request and result, serialized by generated code: it needs much less compiled at run time than reflection.</summary>
    [JsonSerializable(typeof(Request))]
    [JsonSerializable(typeof(Result))]
    sealed partial class ProtocolJson : JsonSerializerContext;

    /// <summary>
    /// A .runsettings, of which the daemon only sends ones the host applies in full: RunConfiguration's
    /// EnvironmentVariables, set here for the whole (single-use) process as vstest sets them on its testhost,
    /// and TestRunParameters, which VSTest adapters read from the XML and NUnit gets as a dictionary.
    /// </summary>
    sealed record Settings(string Xml, Dictionary<string, string> Parameters)
    {
        public static Settings Read(string? path)
        {
            if (path is null)
                return new("<RunSettings><RunConfiguration /></RunSettings>", []);
            var xml = File.ReadAllText(path);
            var root = XElement.Parse(xml);
            foreach (var variable in root.Element("RunConfiguration")?.Element("EnvironmentVariables")?.Elements() ?? [])
                Environment.SetEnvironmentVariable(variable.Name.LocalName, variable.Value);
            var parameters = (root.Element("TestRunParameters")?.Elements("Parameter") ?? [])
                .ToDictionary(p => p.Attribute("name")?.Value ?? "", p => p.Attribute("value")?.Value ?? "");
            return new(xml, parameters);
        }
    }

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

        public string AssemblyPath => assemblyPath;

        protected override Assembly? Load(AssemblyName name) =>
            _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;

        protected override IntPtr LoadUnmanagedDll(string name) =>
            _resolver.ResolveUnmanagedDllToPath(name) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
