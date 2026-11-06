using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace BlazeCs.TestHost;

/// <summary>
/// A test host. The daemon starts it ahead of time, so the runtime is already up when a run needs it; it
/// reads a request from stdin, runs that test assembly in-process and writes the result as the last line
/// (Protocol.cs has the whole exchange). Started for one test project (its framework, assembly and a warm-up
/// assembly as arguments), it gets that project's run ready while it waits (see Prepare). Then, if the
/// runtime lets it apply updates, it stays up: each update the daemon sends is applied to the assemblies it
/// has loaded, and the same test assembly runs again, with its tests discovered anew; otherwise, or on an
/// exit message, it exits. Stdin closing at any point means the daemon is gone (it stopped or died), and the
/// host exits: at once while it runs tests, and after preparing while it waits. Test output goes to stdout.
/// A host runs one test assembly: exiting keeps every assembly's statics, threads and console redirections
/// apart, and lets the tests load into an ordinary (non-collectible) context, where precompiled code is used
/// (measured on the fixture, about twice as fast as a collectible one) and updates can be applied.
/// </summary>
static partial class Program
{
    static void Main(string[] args)
    {
        var protocol = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var daemon = new Daemon();
        // Started for a test project: <framework> <assembly> [--run-settings=<file>] [--warm-up=<assembly>].
        string? Option(string name) => args.FirstOrDefault(a => a.StartsWith(name, StringComparison.Ordinal))?[name.Length..];
        var prepared = args is [var framework, var assembly, ..] ? Prepare(framework, assembly, Option("--run-settings="), Option("--warm-up=")) : null;
        var first = daemon.Next();
        if (first is not TestHostRequest request)
        {
            Refuse(protocol, "a run", first);
            return;
        }
        var loaded = Load(request, prepared, out var result);
        string[]? classes = null; // all
        while (true)
        {
            result ??= RunTests(request.Framework, loaded!, classes);
            result = result with { CannotHotReload = result.CannotHotReload ?? CannotHotReload(loaded, result) };
            Report(protocol, result);
            if (result.CannotHotReload is not null)
                Exit();
            var next = daemon.Next();
            switch (next)
            {
                case TestHostUpdate update:
                    // Applied, it runs the tests again; otherwise it reports that it ran nothing, and exits.
                    result = Apply(update, loaded!.Context) is { } failure ? new TestHostResult(0, 0, 0, 0, null, failure, failure) : null;
                    classes = update.Classes;
                    break;
                case TestHostExit:
                    Exit();
                    break;
                default:
                    Refuse(protocol, "an update or exit", next);
                    break;
            }
        }
    }

    static void Report(StreamWriter protocol, TestHostResult result)
    {
        Console.Out.Flush();
        protocol.WriteLine(TestHostProtocol.ResultPrefix + JsonSerializer.Serialize(result, ProtocolJson.Default.TestHostResult));
    }

    /// <summary>Reports a message out of turn, or one it couldn't read, as an error, and exits.</summary>
    [DoesNotReturn]
    static void Refuse(StreamWriter protocol, string expected, TestHostMessage message)
    {
        var error = $"the test host expected {expected} from the daemon, and got " + (message is Unreadable unreadable
            ? $"a line it couldn't read ({unreadable.Reason})" : $"a {message.GetType().Name} message");
        Report(protocol, new TestHostResult(0, 0, 0, 0, error, null, error));
        Exit();
    }

    /// <summary>
    /// Ends this process at once. Returning from Main would leave it running as long as a foreground thread
    /// that a test started does; a host's work ends with the daemon's message or its last result.
    /// </summary>
    [DoesNotReturn]
    static void Exit()
    {
        Console.Out.Flush();
        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite); // until the kill lands
        throw new UnreachableException();
    }

    /// <summary>Why this host can't take an update after a run, or null if it can.</summary>
    static string? CannotHotReload(Loaded? loaded, TestHostResult result) =>
        loaded is null || result.Unsupported is not null ? "it ran no tests"
        : !MetadataUpdater.IsSupported ? $"the runtime applies no updates here (it needs {TestHostProtocol.ModifiableAssemblies}=debug)"
        : null;

    /// <summary>A line from the daemon that isn't a message, which the host reports and exits on.</summary>
    sealed record Unreadable(string Reason) : TestHostMessage;

    /// <summary>
    /// The daemon's messages, read from stdin on a thread of their own, so that stdin closing is seen at once,
    /// on Linux and Windows alike: the daemon's end of the pipe closes with the daemon, however it died. The
    /// host then exits at once, unless it is preparing, which it finishes first. Once it has taken a message
    /// it is busy with it, and the result would go nowhere. The raw stream: Console.In is emptied, so tests
    /// that read it don't take the daemon's lines.
    /// </summary>
    sealed class Daemon
    {
        readonly BlockingCollection<TestHostMessage> _messages = [];
        readonly object _gate = new();
        bool _busy, _ended;

        public Daemon()
        {
            Console.SetIn(TextReader.Null);
            new Thread(() =>
            {
                // The serializers' code and metadata (the messages' about 120 methods), compiled while the host
                // waits rather than when the request comes.
                _ = JsonSerializer.Deserialize("""{"kind":"exit"}""", ProtocolJson.Default.TestHostMessage);
                _ = JsonSerializer.Serialize(new TestHostResult(0, 0, 0, 0, null, null), ProtocolJson.Default.TestHostResult);
                using var stdin = new StreamReader(Console.OpenStandardInput());
                while (stdin.ReadLine() is { } line)
                    _messages.Add(Read(line));
                lock (_gate)
                {
                    _ended = true;
                    if (_busy)
                        Exit();
                }
                _messages.CompleteAdding();
            }) { IsBackground = true }.Start();
        }

        static TestHostMessage Read(string line)
        {
            try
            {
                return JsonSerializer.Deserialize(line, ProtocolJson.Default.TestHostMessage) ?? new Unreadable("null");
            }
            catch (Exception e) when (e is JsonException or NotSupportedException)
            {
                return new Unreadable(e.Message);
            }
        }

        /// <summary>
        /// The next message, after which the host is busy until it asks for another. Exits if stdin has
        /// ended, even with a message left: the daemon that sent it is gone.
        /// </summary>
        public TestHostMessage Next()
        {
            lock (_gate)
                _busy = false;
            _ = _messages.TryTake(out var message, Timeout.Infinite);
            lock (_gate)
            {
                // Taking nothing means stdin has ended.
                if (_ended)
                    Exit();
                _busy = true;
            }
            return message!;
        }
    }

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
                _ = Runner(framework, context, context.LoadFromAssemblyPath(warmUp), directory, TextWriter.Null, settings, null)();
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

    /// <summary>A test assembly loaded in its context, with the .runsettings it runs with.</summary>
    sealed record Loaded(TestContext Context, Assembly Assembly, Settings Settings);

    /// <summary>Loads the request's test assembly, or reports why it can't run.</summary>
    static Loaded? Load(TestHostRequest request, TestContext? prepared, out TestHostResult? failure)
    {
        var assemblyPath = request.Assembly;
        failure = null;
        try
        {
            var settings = Settings.Read(request.RunSettings);
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
                    failure = new TestHostResult(0, 0, 0, 0, $"the test host (.NET {Environment.Version}) can't load {reference.FullName}: {e.Message}", null);
                    return null;
                }
            }
            return new Loaded(context, assembly, settings);
        }
        catch (Exception e)
        {
            // Nothing ran: the daemon falls back to `dotnet test`.
            failure = NothingRan(e);
            return null;
        }
    }

    static TestHostResult NothingRan(Exception e)
    {
        var cause = Unwrap(e);
        return new TestHostResult(0, 0, 0, 0, null, $"{cause.GetType().Name}: {cause.Message}");
    }

    /// <summary>
    /// Runs a loaded test assembly's tests with a new runner, which discovers them anew: after an update, the
    /// assembly's current methods and types. With classes, only those classes' tests run (none if it is empty).
    /// </summary>
    static TestHostResult RunTests(string framework, Loaded loaded, string[]? classes)
    {
        if (classes is [])
            return new TestHostResult(0, 0, 0, 0, null, null, Classes: []);
        var (output, error) = (Console.Out, Console.Error);
        Func<TestHostResult> run;
        try
        {
            var directory = EnterDirectory(loaded.Assembly.Location);
            run = Runner(framework, loaded.Context, loaded.Assembly, directory, output, loaded.Settings, classes?.ToHashSet(StringComparer.Ordinal));
        }
        catch (Exception e)
        {
            return NothingRan(e);
        }

        try
        {
            return run();
        }
        catch (Exception e)
        {
            return new TestHostResult(0, 0, 0, 0, Unwrap(e).ToString(), null);
        }
        finally
        {
            // MSTest leaves its own writers installed.
            Console.SetOut(output);
            Console.SetError(error);
        }
    }

    /// <summary>
    /// Applies an update's deltas to the loaded assemblies they name, then calls every loaded assembly's
    /// metadata update handlers (the runtime's reflection caches among them) as dotnet-watch's agent does,
    /// so the next run's reflection sees the updated types. An assembly the tests haven't loaded yet is loaded
    /// into their context from the delta's baseline first: the file on disk may be newer than the baseline,
    /// and a delta only applies to the module it was made against. Returns why it couldn't, or null.
    /// </summary>
    static string? Apply(TestHostUpdate update, TestContext context)
    {
        if (update.Deltas.Length == 0)
            return "the test host couldn't apply the update: it has no deltas";
        List<Type>? updatedTypes = [];
        try
        {
            foreach (var delta in update.Deltas)
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && a.ManifestModule.ModuleVersionId == delta.Module);
                if (assembly is null && delta.Baseline is not null)
                {
                    // Throws if the context has loaded another build of it, which no delta made here applies to.
                    assembly = context.LoadFromAssemblyPath(delta.Baseline);
                    if (assembly.ManifestModule.ModuleVersionId != delta.Module)
                        return $"the test host couldn't apply the update: {delta.Baseline} is module {assembly.ManifestModule.ModuleVersionId}, not {delta.Module}";
                }
                if (assembly is null)
                    return $"the test host couldn't apply the update: it has loaded no assembly with module {delta.Module}";
                MetadataUpdater.ApplyUpdate(assembly, delta.Metadata, delta.IL, delta.Pdb);
                if (delta.UpdatedTypes is null)
                    updatedTypes = null;
                else
                {
                    foreach (var token in delta.UpdatedTypes)
                    {
                        try { updatedTypes?.Add(assembly.ManifestModule.ResolveType(token)); }
                        catch (ArgumentException) { }
                    }
                }
            }
        }
        catch (Exception e)
        {
            var cause = Unwrap(e);
            return $"the test host couldn't apply the update: {cause.GetType().Name}: {cause.Message}";
        }
        CallUpdateHandlers(updatedTypes?.ToArray());
        return null;
    }

    /// <summary>
    /// Calls ClearCache, then UpdateApplication, on each [MetadataUpdateHandler] type of every loaded assembly,
    /// as dotnet-watch's agent does (MetadataUpdateHandlerInvoker in dotnet/sdk): each assembly's references
    /// before it (CoreLib's reflection cache first, a test framework's before the tests'), the attribute
    /// matched by name. Null types means any may have changed. A handler that fails leaves its own cache
    /// stale, which the run that follows shows.
    /// </summary>
    static void CallUpdateHandlers(Type[]? types)
    {
        var handlers = DependenciesFirst(AppDomain.CurrentDomain.GetAssemblies()).SelectMany(UpdateHandlers).ToList();
        foreach (var name in new[] { "ClearCache", "UpdateApplication" })
        {
            foreach (var handler in handlers)
            {
                try { handler.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, [typeof(Type[])])?.Invoke(null, [types]); }
                catch (Exception) { }
            }
        }
    }

    static List<Type> UpdateHandlers(Assembly assembly)
    {
        try
        {
            return [.. assembly.GetCustomAttributesData()
                .Where(a => a.AttributeType.FullName == "System.Reflection.Metadata.MetadataUpdateHandlerAttribute")
                .Select(a => a.ConstructorArguments is [{ Value: Type handler }] ? handler : null)
                .OfType<Type>()];
        }
        catch (Exception) // an attribute whose type doesn't load
        {
            return [];
        }
    }

    /// <summary>
    /// Assemblies ordered so that each comes after those it references, by simple name, the first loaded of a
    /// name standing for all (the agent's TopologicalSort).
    /// </summary>
    static List<Assembly> DependenciesFirst(Assembly[] assemblies)
    {
        static string? Name(Assembly assembly)
        {
            try { return assembly.GetName().Name; }
            catch (Exception) { return null; }
        }
        var byName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies)
        {
            if (Name(assembly) is { } name)
                byName.TryAdd(name, assembly);
        }
        var sorted = new List<Assembly>(assemblies.Length);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(Assembly assembly)
        {
            if (Name(assembly) is not { } name || !visited.Add(name))
                return;
            AssemblyName[] references;
            try { references = assembly.GetReferencedAssemblies(); }
            catch (Exception) { references = []; }
            foreach (var reference in references)
            {
                if (reference.Name is { } referenceName && byName.TryGetValue(referenceName, out var dependency))
                    Visit(dependency);
            }
            sorted.Add(assembly);
        }
        foreach (var assembly in assemblies)
            Visit(assembly);
        return sorted;
    }

    /// <summary>
    /// A framework's runner for one test assembly, ready to run: every test, or only the tests of the given
    /// classes, through the framework's own filter. The directory is the test project's output directory, where
    /// xUnit v2's adapter is.
    /// </summary>
    static Func<TestHostResult> Runner(string framework, AssemblyLoadContext context, Assembly assembly, string directory, TextWriter output, Settings settings,
        IReadOnlySet<string>? classes) => framework switch
    {
        "xunit3" => XunitV3(context, assembly, output, classes),
        "xunit2" => VSTest(context, assembly, output, settings, context.LoadFromAssemblyPath(XunitV2Adapter(directory))
            .GetType("Xunit.Runner.VisualStudio.VsTestRunner", throwOnError: true)!, classes),
        "nunit" => NUnit(context, assembly, output, settings, classes),
        "mstest" => VSTest(context, assembly, output, settings, context.LoadFromAssemblyName(new AssemblyName("MSTest.TestAdapter"))
            .GetType("Microsoft.VisualStudio.TestPlatform.MSTest.TestAdapter.MSTestExecutor", throwOnError: true)!, classes),
        _ => throw new NotSupportedException($"no runner for {framework}"),
    };

    /// <summary>
    /// xUnit's in-process runner, which takes the test assembly explicitly: the assembly's own Main would
    /// run whatever Assembly.GetEntryAssembly() is, this host. Classes go to its -class option, which takes a
    /// class's full name, a nested class's with a '+'. Totals come from its XML report, and each test's class
    /// from its test element's type; an error outside a test (a fixture's, a collection's) leaves the classes
    /// unaccounted for.
    /// </summary>
    static Func<TestHostResult> XunitV3(AssemblyLoadContext context, Assembly assembly, TextWriter output, IReadOnlySet<string>? classes)
    {
        var reportPath = Path.Combine(Path.GetTempPath(), $"blaze-cs-{Environment.ProcessId}.xml");
        var runnerType = context.LoadFromAssemblyName(new AssemblyName("xunit.v3.runner.inproc.console"))
            .GetType("Xunit.Runner.InProc.SystemConsole.ConsoleRunner", throwOnError: true)!;
        string[] arguments = ["-noLogo", "-noColor", "-xml", reportPath, .. classes?.Order(StringComparer.Ordinal).SelectMany(name => new[] { "-class", name }) ?? []];
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
                var byClass = Count("errors") > 0 ? null : ByClass(report.Descendants("test").Select(test => (test.Attribute("type")?.Value, test.Attribute("result")?.Value switch
                {
                    "Pass" => "Passed",
                    "Fail" => "Failed",
                    "Skip" => "Skipped",
                    _ => "NotRun", // an explicit test that wasn't asked for
                })));
                // Its total includes explicit tests that weren't asked for, which `dotnet test` leaves out.
                return new TestHostResult(Count("total") - Count("not-run"), Count("passed"), Count("failed") + Count("errors"), Count("skipped"), null, null,
                    Classes: byClass);
            }
            finally
            {
                File.Delete(reportPath);
            }
        };
    }

    /// <summary>
    /// NUnit's FrameworkController, which its engine and VSTest adapter drive too: RunTests returns the
    /// results as XML (a test-suite with totals, and a test-case per test with its class, any failure and
    /// output). Classes go to its filter's class elements, which match a test's class name.
    /// </summary>
    static Func<TestHostResult> NUnit(AssemblyLoadContext context, Assembly assembly, TextWriter output, Settings settings, IReadOnlySet<string>? classes)
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
            var filter = classes is null ? new XElement("filter")
                : new XElement("filter", new XElement("or", classes.Order(StringComparer.Ordinal).Select(name => new XElement("class", name))));
            var suite = XElement.Parse((string)runTests.Invoke(controller, [filter.ToString(SaveOptions.DisableFormatting)])!);
            foreach (var test in suite.Descendants("test-case").Where(t => t.Attribute("result")?.Value == "Failed"))
            {
                var failure = test.Element("failure");
                ReportFailure(output, test.Attribute("fullname")?.Value, failure?.Element("message")?.Value,
                    failure?.Element("stack-trace")?.Value, test.Element("output")?.Value);
            }
            int Count(string name) => int.Parse(suite.Attribute(name)?.Value ?? "0");
            var byClass = ByClass(suite.Descendants("test-case").Select(test => (test.Attribute("classname")?.Value, test.Attribute("result")?.Value switch
            {
                "Passed" or "Failed" => test.Attribute("result")!.Value,
                _ => "Skipped", // as below
            })));
            // As the VSTest adapter does by default: warnings and inconclusive results count as skipped.
            return new TestHostResult(Count("total"), Count("passed"), Count("failed"),
                Count("skipped") + Count("inconclusive") + Count("warnings"), null, null, Classes: byClass);
        };
    }

    /// <summary>
    /// A VSTest adapter's executor (MSTest's, xUnit v2's), as vstest's testhost runs it, with the run's
    /// .runsettings. Its run context and framework handle are proxies for the test's own copy of the VSTest
    /// object model, so the host doesn't reference it. Classes go to the run context's test case filter, which
    /// both adapters ask for (`dotnet test --filter` reaches them that way): it matches a test whose fully
    /// qualified name is one of their names, a dot and a method name, the class a result is counted under.
    /// </summary>
    static Func<TestHostResult> VSTest(AssemblyLoadContext context, Assembly assembly, TextWriter output, Settings runSettings, Type executorType,
        IReadOnlySet<string>? classes)
    {
        var executor = Activator.CreateInstance(executorType)!;
        var objectModel = context.LoadFromAssemblyName(new AssemblyName("Microsoft.VisualStudio.TestPlatform.ObjectModel"));
        Type Adapter(string name) => objectModel.GetType($"Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter.{name}", throwOnError: true)!;
        var runTests = Adapter("ITestExecutor").GetMethod("RunTests", [typeof(IEnumerable<string>), Adapter("IRunContext"), Adapter("IFrameworkHandle")])
            ?? throw new MissingMethodException("ITestExecutor", "RunTests");
        var settings = Proxy.Create(Adapter("IRunSettings"), (method, _) =>
            method.Name == "get_SettingsXml" ? runSettings.Xml : null);
        var filter = classes is null ? null : Proxy.Create(Adapter("ITestCaseFilterExpression"), (method, arguments) => method.Name switch
        {
            "get_TestCaseFilterValue" => "blaze-cs: " + string.Join("|", classes),
            "MatchTestCase" => classes.Contains(VSTestRecorder.Class(arguments[0]!)),
            _ => null,
        });
        var runContext = Proxy.Create(Adapter("IRunContext"), (method, _) => method.Name switch
        {
            "get_RunSettings" => settings,
            "get_TestRunDirectory" => Path.GetDirectoryName(assembly.Location),
            "GetTestCaseFilter" => filter,
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
        readonly ConcurrentQueue<(string? Class, string Outcome)> _outcomes = [];

        public TestHostResult Result() => new(_total, _passed, _failed, _skipped, null, null, Classes: ByClass(_outcomes));

        /// <summary>A test case's class: its fully qualified name up to the method's name, which both adapters append after a dot.</summary>
        public static string Class(object testCase) => Get(testCase, "FullyQualifiedName") is string name && name.LastIndexOf('.') is var dot and > 0 ? name[..dot] : "";

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
            var outcome = Get(result, "Outcome")?.ToString();
            _outcomes.Enqueue((Class(Get(result, "TestCase")!), outcome is "Passed" or "Failed" ? outcome : "Skipped"));
            switch (outcome)
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

    /// <summary>
    /// A run's counts by test class, from each test's class and outcome (Passed, Failed or Skipped; anything else,
    /// such as an explicit test that wasn't asked for, isn't counted); null if a test has no class.
    /// </summary>
    static Dictionary<string, TestCounts>? ByClass(IEnumerable<(string? Class, string Outcome)> tests)
    {
        var classes = new Dictionary<string, TestCounts>(StringComparer.Ordinal);
        foreach (var (name, outcome) in tests)
        {
            TestCounts? counts = outcome switch
            {
                "Passed" => new(1, 0, 0),
                "Failed" => new(0, 1, 0),
                "Skipped" => new(0, 0, 1),
                _ => null,
            };
            if (counts is null)
                continue;
            if (string.IsNullOrEmpty(name))
                return null;
            classes[name] = classes.TryGetValue(name, out var sum) ? sum + counts : counts;
        }
        return classes;
    }

    static Exception Unwrap(Exception e) => e is TargetInvocationException { InnerException: { } inner } ? inner : e;

    /// <summary>The protocol's messages, serialized by generated code: it needs much less compiled at run time than reflection.</summary>
    [JsonSerializable(typeof(TestHostMessage))]
    [JsonSerializable(typeof(TestHostResult))]
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
