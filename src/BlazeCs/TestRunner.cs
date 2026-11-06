using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace BlazeCs;

/// <summary>
/// Runs the test projects whose inputs changed since they last passed in this daemon. xUnit (v2 and
/// v3), NUnit and MSTest projects run in test host processes, longest first, like `dotnet test` runs
/// projects side by side; the rest go through `dotnet test --no-build`. A host runs one assembly and
/// exits. Idle hosts wait for the most recently run test projects, one each and at most MaxIdleHosts,
/// each on the runtime its project targets and with that project's run made ready (the test host's
/// Prepare: its NuGet packages loaded, its framework rehearsed), so a run of one of them starts neither a
/// runtime nor the framework cold. A spare host, started for no project, waits on each runtime the
/// hosted projects target: a project without a host of its own takes it, so its run starts no runtime.
/// </summary>
sealed class TestRunner(SolutionBuilder builder) : IDisposable
{
    /// <summary>
    /// Hosts running at once: tests parallelize inside an assembly too, so a few are enough.
    /// </summary>
    const int MaxHosts = 4;

    /// <summary>
    /// Idle hosts kept: as many as run at once. One holds its project's packages and compiled framework code,
    /// about 80 MB on jellyfin, most of it file mappings.
    /// </summary>
    const int MaxIdleHosts = MaxHosts;

    // Idle hosts by test assembly, each prepared for that project.
    readonly Dictionary<string, IdleHost> _idleHosts = new(PathComparer.Instance);
    // The projects that keep one, as of the last Prestart: the most recently run first.
    List<HostTarget> _targets = [];
    // Spare hosts by runtime version, and the runtimes that keep one, as of the last Prestart.
    readonly Dictionary<string, Process> _spares = [];
    HashSet<string> _spareRuntimes = [];
    // Hosted test projects by their last run in this daemon, by assembly, the most recent first.
    readonly List<string> _recent = [];
    // Held while hosts start, so concurrent refills don't start two for one project.
    readonly object _refilling = new();
    bool _disposed;
    // Warm-up assemblies by test assembly, with the key each was compiled for, and every one written, to delete.
    readonly Dictionary<string, (HostKey Key, string? Path)> _warmUps = new(PathComparer.Instance);
    readonly List<string> _warmUpFiles = [];

    /// <summary>
    /// What a host is prepared from: its project's framework, runtime and .runsettings, and a hash of its
    /// deps.json (the packages it loads) and .runsettings. A host whose key changed is stopped unused.
    /// </summary>
    readonly record struct HostKey(string Framework, string Runtime, string? RunSettings, string Inputs);

    sealed record HostTarget(ProjectCompiler Unit, string Assembly, HostKey Key);

    sealed record IdleHost(Process Process, HostKey Key);
    readonly ConcurrentDictionary<string, TimeSpan> _lastDuration = new(PathComparer.Instance);

    /// <summary>
    /// Each test project's last run in this daemon, by assembly, if it passed: the inputs it ran with and
    /// its result. A project without one (never run here, or it failed) runs next time.
    /// </summary>
    readonly ConcurrentDictionary<string, (string Inputs, string Result)> _passed = new(PathComparer.Instance);
    readonly ConcurrentDictionary<string, (FileStamp Stamp, string Hash)> _hashes = new(PathComparer.Instance);

    /// <summary>What a test run reports: whether it passed, and in a few words for the next unchanged run.</summary>
    readonly record struct Outcome(bool Passed, string Result);

    /// <param name="all">Run every test project, not only those whose inputs changed since they passed.</param>
    public int Run(bool all, Action<string> log)
    {
        var tests = builder.Units.Where(u => Framework(u) is not null).ToList();
        if (tests.Count == 0)
        {
            log("blaze-cs: no test projects");
            return 0;
        }
        var inputs = tests.AsParallel().ToDictionary(u => u, Inputs);
        var unchanged = all ? [] : tests
            .Where(u => _passed.TryGetValue(u.Unit.AssemblyPath, out var last) && last.Inputs == inputs[u])
            .ToList();
        if (unchanged.Count > 0)
            log("blaze-cs: unchanged since last run: "
                + string.Join(", ", unchanged.Select(u => $"{u.Unit.Name} ({_passed[u.Unit.AssemblyPath].Result})")));
        var affected = tests.Except(unchanged).ToList();

        var failed = 0;
        void Record(ProjectCompiler unit, Outcome outcome)
        {
            if (outcome.Passed)
                _passed[unit.Unit.AssemblyPath] = (inputs[unit], outcome.Result);
            else
            {
                _passed.TryRemove(unit.Unit.AssemblyPath, out _);
                Interlocked.Increment(ref failed);
            }
        }

        var hosted = affected.Select(u => (Unit: u, Applies: InHost(u.Unit, log, out var runSettings), RunSettings: runSettings))
            .Where(test => test.Applies).ToList();
        var inHost = new ConcurrentQueue<(ProjectCompiler Unit, TestHostRequest Request)>(hosted
            .Select(test => (test.Unit, new TestHostRequest(Framework(test.Unit)!, builder.InstalledPath(test.Unit), test.RunSettings)))
            .OrderByDescending(test => _lastDuration.GetValueOrDefault(test.Item2.Assembly)));
        var workers = Enumerable.Range(0, Math.Min(MaxHosts, inHost.Count)).Select(_ => Task.Run(() =>
        {
            while (inHost.TryDequeue(out var test))
            {
                var outcome = RunInHost(test.Request, log);
                if (outcome is not null)
                {
                    lock (_recent)
                    {
                        _recent.Remove(test.Request.Assembly);
                        _recent.Insert(0, test.Request.Assembly);
                    }
                }
                Record(test.Unit, outcome ?? DotnetTest(test.Unit, log));
            }
        })).ToArray();

        foreach (var unit in affected.Except(hosted.Select(test => test.Unit)))
            Record(unit, DotnetTest(unit, log));
        Task.WaitAll(workers);
        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// What a test project runs with: the solution's assemblies in its runtime closure, each by a hash of
    /// its code (PeIdentity: a comment-only edit changes only the debug identity), its .deps.json, which
    /// lists the packages it loads, and its .runsettings, which the host applies.
    /// </summary>
    string Inputs(ProjectCompiler unit) => string.Join("\n", builder.RuntimeClosure(unit)
        .Append(Path.ChangeExtension(builder.InstalledPath(unit), ".deps.json"))
        .Concat(builder.RunSettingsFile(unit.Unit) is { } runSettings ? new[] { runSettings } : [])
        .Order(PathComparer.Instance)
        .Select(path => $"{path} {Hash(path)}"));

    /// <summary>A file's hash, read again only when its length or write time changed.</summary>
    string Hash(string path)
    {
        var stamp = FileStamp.Of(path);
        if (_hashes.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            return cached.Hash;
        var hash = "missing";
        if (stamp != default)
        {
            var bytes = File.ReadAllBytes(path);
            try { bytes = PeIdentity.Masked(bytes); }
            catch (Exception e) when (e is BadImageFormatException or InvalidOperationException) { } // not an assembly
            hash = Convert.ToHexString(SHA256.HashData(bytes));
        }
        _hashes[path] = (stamp, hash);
        return hash;
    }

    /// <summary>
    /// Whether a project runs in a host, and the .runsettings the host applies for it. The host applies one
    /// that sets only RunConfiguration's EnvironmentVariables and TestRunParameters; a project whose
    /// .runsettings (the build's evaluated RunSettingsFilePath, which is what `dotnet test` applies) has
    /// anything else, or can't be read, stays on `dotnet test`.
    /// </summary>
    bool InHost(CompileUnit unit, Action<string> log, out string? runSettings)
    {
        var file = builder.RunSettingsFile(unit);
        runSettings = file is not null && HostApplies(file) ? file : null;
        if (file is null || runSettings is not null)
            return true;
        log($"blaze-cs: {unit.Name} has a .runsettings, which the test host doesn't apply; running dotnet test");
        return false;
    }

    /// <summary>
    /// Whether a .runsettings holds only what the host applies: RunConfiguration with EnvironmentVariables,
    /// and TestRunParameters with Parameter elements.
    /// </summary>
    static bool HostApplies(string path)
    {
        try
        {
            var root = XElement.Load(path);
            return root.Name == "RunSettings" && !root.HasAttributes && root.Elements().All(section => section.Name.LocalName switch
            {
                "RunConfiguration" => !section.HasAttributes && section.Elements().All(e => e.Name == "EnvironmentVariables" && !e.HasAttributes),
                "TestRunParameters" => !section.HasAttributes && section.Elements().All(e => e.Name == "Parameter"),
                _ => false,
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
    }

    Outcome DotnetTest(ProjectCompiler unit, Action<string> log) =>
        builder.RunDotnet($"test --no-build \"{unit.Unit.ProjectFile}\"", log) == 0 ? new(true, "passed") : new(false, "failed");

    /// <summary>
    /// Runs one assembly in a host and logs its output in one piece; null if the host ran no tests, which
    /// `dotnet test` then settles.
    /// </summary>
    Outcome? RunInHost(TestHostRequest request, Action<string> log)
    {
        var stopwatch = Stopwatch.StartNew();
        var output = new List<string>();
        var assembly = request.Assembly;
        var host = TakeHost(assembly, Key(request.Framework, assembly, request.RunSettings));
        // A host that died while idle gives no result, so the run fails below.
        var result = Send(host, request) ? ReadResult(host, output) : null;
        Stop(host);
        result ??= new TestHostResult(0, 0, 0, 0, Error: "the test host exited mid-run", Unsupported: null);
        _lastDuration[assembly] = stopwatch.Elapsed;

        var name = Path.GetFileName(assembly);
        // An empty run proves nothing: the host may have missed tests that dotnet test would find.
        var fallback = result.Unsupported ?? (result is { Total: 0, Error: null } ? "the test host found no tests" : null);
        if (fallback is not null)
        {
            lock (_hostConfigs)
            {
                output.ForEach(log);
                log($"blaze-cs: {name}: {fallback}; running dotnet test");
            }
            return null;
        }

        var passed = result.Error is null && result.Failed == 0;
        if (result.Error is not null)
            output.Add($"blaze-cs: {name}: {result.Error}");
        // As `dotnet test` labels it: a run that only skipped tests is "Skipped!".
        var label = !passed ? "Failed!" : result is { Passed: 0, Skipped: > 0 } ? "Skipped!" : "Passed!";
        output.Add($"{label}  - Failed: {result.Failed,5}, Passed: {result.Passed,5}, Skipped: {result.Skipped,5}, "
            + $"Total: {result.Total,5}, Duration: {stopwatch.ElapsedMilliseconds} ms - {name}");
        lock (_hostConfigs)
            output.ForEach(log);
        return new Outcome(passed, result.Skipped > 0 ? $"{result.Passed} passed, {result.Skipped} skipped" : $"{result.Passed} passed");
    }

    /// <summary>Writes a message to a host (Protocol.cs); false if it has died.</summary>
    internal static bool Send(Process host, TestHostMessage message)
    {
        try
        {
            host.StandardInput.WriteLine(JsonSerializer.Serialize(message));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>A host's output up to its next result, added to <paramref name="output"/>, and that result; null if it exits first.</summary>
    internal static TestHostResult? ReadResult(Process host, List<string> output)
    {
        while (host.StandardOutput.ReadLine() is { } line)
        {
            // Test output may end mid-line, with the result after it.
            var start = line.IndexOf(TestHostProtocol.ResultPrefix, StringComparison.Ordinal);
            if (start < 0)
            {
                output.Add(line);
                continue;
            }
            if (start > 0)
                output.Add(line[..start]);
            return JsonSerializer.Deserialize<TestHostResult>(line[(start + TestHostProtocol.ResultPrefix.Length)..])!;
        }
        return null;
    }

    /// <summary>
    /// Works out which test projects keep an idle host, the most recently run first, and starts the missing
    /// hosts in the background, stopping those no longer wanted or prepared from what has changed since: the
    /// daemon calls it after loading and after each command, so a run finds its host ready.
    /// </summary>
    public void Prestart()
    {
        // Read now: the next command may change the units while the hosts start. Projects not run in this
        // daemon yet come after the others, in the solution's order.
        List<string> recent;
        lock (_recent)
            recent = [.. _recent];
        var hosted = builder.Units
            .Select(u => (Unit: u, Assembly: builder.InstalledPath(u), Framework: Framework(u)))
            .Where(test => test.Framework is not null)
            .OrderBy(test => recent.IndexOf(test.Assembly) is var index and >= 0 ? index : int.MaxValue)
            .Select(test => (test.Unit, test.Assembly, test.Framework, Applies: InHost(test.Unit.Unit, _ => { }, out var runSettings), RunSettings: runSettings))
            .Where(test => test.Applies)
            .ToList();
        var targets = hosted.Take(MaxIdleHosts)
            .Select(test => new HostTarget(test.Unit, test.Assembly, Key(test.Framework!, test.Assembly, test.RunSettings)))
            .ToList();
        var runtimes = hosted.Select(test => RuntimeVersion(test.Assembly)).ToHashSet();
        lock (_idleHosts)
        {
            _targets = targets;
            _spareRuntimes = runtimes;
            foreach (var (assembly, idle) in _idleHosts.ToList())
            {
                if (idle.Process.HasExited || !targets.Any(target => PathComparer.Instance.Equals(target.Assembly, assembly) && target.Key == idle.Key))
                {
                    _idleHosts.Remove(assembly);
                    Stop(idle.Process);
                }
            }
            foreach (var (runtime, spare) in _spares.ToList())
            {
                if (spare.HasExited || !runtimes.Contains(runtime))
                {
                    _spares.Remove(runtime);
                    Stop(spare);
                }
            }
        }
        Task.Run(Refill);
    }

    /// <summary>
    /// Starts a host for each project that should have one and hasn't, the most recently run first, then the
    /// missing spares.
    /// </summary>
    void Refill()
    {
        lock (_refilling)
        {
            List<HostTarget> missing;
            lock (_idleHosts)
                missing = _disposed ? [] : [.. _targets.Where(target => !_idleHosts.ContainsKey(target.Assembly))];
            foreach (var target in missing)
            {
                var host = StartHost(target.Key.Runtime, (target.Key.Framework, target.Assembly, target.Key.RunSettings, WarmUp(target)));
                lock (_idleHosts)
                {
                    // A Prestart since may have dropped it.
                    if (!_disposed && _targets.Contains(target) && !_idleHosts.ContainsKey(target.Assembly))
                    {
                        _idleHosts[target.Assembly] = new IdleHost(host, target.Key);
                        continue;
                    }
                }
                Stop(host);
                if (_disposed)
                    return;
            }
            RefillSpares();
        }
    }

    /// <summary>Starts a spare host for each runtime that should have one and hasn't.</summary>
    void RefillSpares()
    {
        lock (_refilling)
        {
            List<string> missing;
            lock (_idleHosts)
                missing = _disposed ? [] : [.. _spareRuntimes.Where(runtime => !_spares.ContainsKey(runtime))];
            foreach (var runtime in missing)
            {
                var host = StartHost(runtime, null);
                lock (_idleHosts)
                {
                    if (!_disposed && _spareRuntimes.Contains(runtime) && _spares.TryAdd(runtime, host))
                        continue;
                }
                Stop(host);
                if (_disposed)
                    return;
            }
        }
    }

    /// <summary>What a host for a test project is prepared from, as of now.</summary>
    HostKey Key(string framework, string assembly, string? runSettings) => new(framework, RuntimeVersion(assembly), runSettings,
        Hash(Path.ChangeExtension(assembly, ".deps.json")) + (runSettings is null ? "" : " " + Hash(runSettings)));

    /// <summary>
    /// The idle host prepared for a test project, if it was prepared from what the project has now; otherwise
    /// the spare on its runtime, replaced in the background, or a new host: a host started for no project
    /// loads everything when the run starts.
    /// </summary>
    Process TakeHost(string assembly, HostKey key)
    {
        IdleHost? idle;
        lock (_idleHosts)
            _idleHosts.Remove(assembly, out idle);
        if (idle is not null && idle.Key == key && !idle.Process.HasExited)
            return idle.Process;
        if (idle is not null)
            Stop(idle.Process);
        Process? spare;
        lock (_idleHosts)
            _spares.Remove(key.Runtime, out spare);
        if (spare is null)
            return StartHost(key.Runtime, null);
        _ = Task.Run(RefillSpares);
        if (!spare.HasExited)
            return spare;
        spare.Dispose();
        return StartHost(key.Runtime, null);
    }

    /// <summary>
    /// A few tests in a project's framework, compiled against the project's own references (so against its
    /// version of the framework), which a host started for the project runs while it waits, so the
    /// framework's code is compiled before the project's tests need it; null if they don't compile. Compiled
    /// again when the host's key changes. Only Refill calls it.
    /// </summary>
    string? WarmUp(HostTarget target)
    {
        if (_warmUps.TryGetValue(target.Assembly, out var cached) && cached.Key == target.Key)
            return cached.Path;
        // Named as its file: MSTest loads a test assembly by its file's name.
        var name = $"blaze-cs-warmup-{Environment.ProcessId}-{_warmUpFiles.Count}";
        // No CSharpCompilationOptions constructor: its parameters differ between the Roslyn this is built
        // against and an older SDK's.
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(SourceText.From(WarmUpSource(target.Key.Framework)))],
            target.Unit.MetadataReferences);
        compilation = compilation.WithOptions(compilation.Options.WithOutputKind(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        string? path = null;
        if (compilation.Emit(image).Success)
        {
            path = Path.Combine(Path.GetTempPath(), name + ".dll");
            File.WriteAllBytes(path, image.ToArray());
            _warmUpFiles.Add(path);
        }
        _warmUps[target.Assembly] = (target.Key, path);
        return path;
    }

    /// <summary>A plain test and an async one with data, in each framework's attributes.</summary>
    static string WarmUpSource(string framework) => framework switch
    {
        "xunit3" or "xunit2" => """
            using Xunit;

            public class WarmUp
            {
                [Fact]
                public void Fact() => Assert.Equal(2, 1 + 1);

                [Theory]
                [InlineData(1)]
                [InlineData(2)]
                public async System.Threading.Tasks.Task Theory(int value)
                {
                    await System.Threading.Tasks.Task.Yield();
                    Assert.True(value > 0);
                }
            }
            """,
        "nunit" => """
            using NUnit.Framework;

            public class WarmUp
            {
                [Test]
                public void Test() => Assert.That(1 + 1, Is.EqualTo(2));

                [TestCase(1)]
                [TestCase(2)]
                public async System.Threading.Tasks.Task Case(int value)
                {
                    await System.Threading.Tasks.Task.Yield();
                    Assert.That(value, Is.GreaterThan(0));
                }
            }
            """,
        "mstest" => """
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            [TestClass]
            public class WarmUp
            {
                [TestMethod]
                public void Method() => Assert.AreEqual(2, 1 + 1);

                [TestMethod]
                [DataRow(1)]
                [DataRow(2)]
                public async System.Threading.Tasks.Task Row(int value)
                {
                    await System.Threading.Tasks.Task.Yield();
                    Assert.IsTrue(value > 0);
                }
            }
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(framework), framework, null),
    };

    /// <summary>
    /// The runtime a test assembly targets, from its runtimeconfig.json: "10.0.0" for a release, the exact
    /// version for a preview (as a preview SDK writes it); "" (the host's own) when that can't be read.
    /// </summary>
    static string RuntimeVersion(string assembly)
    {
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(assembly, ".runtimeconfig.json")));
            var options = config.RootElement.GetProperty("runtimeOptions");
            IEnumerable<JsonElement> frameworks = options.TryGetProperty("frameworks", out var many) ? many.EnumerateArray()
                : options.TryGetProperty("framework", out var one) ? [one] : [];
            foreach (var framework in frameworks)
            {
                if (framework.GetProperty("name").GetString() == "Microsoft.NETCore.App")
                {
                    var version = framework.GetProperty("version").GetString()!;
                    return version.Contains('-') ? version : $"{Version.Parse(version).Major}.0.0";
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException) { }
        return "";
    }

    readonly Dictionary<string, string> _hostConfigs = [];

    /// <summary>
    /// A runtimeconfig.json that asks for one version of the runtime and ASP.NET Core, which the host then
    /// runs on (the latest installed patch of it, as for a project of that target framework).
    /// </summary>
    string HostConfig(string runtime)
    {
        lock (_hostConfigs)
        {
            if (!_hostConfigs.TryGetValue(runtime, out var path))
            {
                path = Path.Combine(Path.GetTempPath(), $"blaze-cs-testhost-{Environment.ProcessId}-{_hostConfigs.Count}.runtimeconfig.json");
                File.WriteAllText(path, $$"""
                    { "runtimeOptions": { "tfm": "net{{Version.Parse(runtime.Split('-')[0]).Major}}.0", "frameworks": [
                        { "name": "Microsoft.NETCore.App", "version": "{{runtime}}" },
                        { "name": "Microsoft.AspNetCore.App", "version": "{{runtime}}" } ] } }
                    """);
                _hostConfigs[runtime] = path;
            }
            return path;
        }
    }

    /// <summary>
    /// Starts a host on a runtime; for a project (its framework, assembly, .runsettings and warm-up assembly),
    /// one that gets the project's run ready while it waits.
    /// </summary>
    Process StartHost(string runtime, (string Framework, string Assembly, string? RunSettings, string? WarmUp)? project)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        var host = Path.Combine(AppContext.BaseDirectory, "blaze-cs-testhost.dll");
        if (runtime.Length == 0)
            start.ArgumentList.Add(host);
        else
        {
            foreach (var argument in new[] { "exec", "--runtimeconfig", HostConfig(runtime), host })
                start.ArgumentList.Add(argument);
        }
        if (project is var (framework, assembly, runSettings, warmUp))
        {
            start.ArgumentList.Add(framework);
            start.ArgumentList.Add(assembly);
            if (runSettings is not null)
                start.ArgumentList.Add($"--run-settings={runSettings}");
            if (warmUp is not null)
                start.ArgumentList.Add($"--warm-up={warmUp}");
        }
        // Tests run with the runtime's defaults, as under `dotnet test`, not the daemon's tuning.
        foreach (var variable in new[] { "DOTNET_gcServer", "DOTNET_TieredPGO", "DOTNET_TC_CallCountingDelayMs" })
            start.Environment.Remove(variable);
        // Not DOTNET_MODIFIABLE_ASSEMBLIES=debug, which lets a host take updates (Protocol.cs): nothing sends
        // any yet, and on the fixture it makes each run 50 to 120 ms slower, with the same methods compiled.
        // A host exits when its stdin closes, so the daemon dying any way takes every host with it.
        return Process.Start(start)!;
    }

    /// <summary>
    /// Kills a host, and once the runner is disposed, waits until it is gone: Windows keeps the files it has
    /// loaded, which Dispose deletes. A run doesn't wait for its host to go.
    /// </summary>
    void Stop(Process host)
    {
        try
        {
            host.Kill(entireProcessTree: true);
            if (_disposed)
                host.WaitForExit();
        }
        catch (InvalidOperationException) { }
        host.Dispose();
    }

    public void Dispose()
    {
        List<Process> idle;
        lock (_idleHosts)
        {
            _disposed = true;
            idle = [.. _idleHosts.Values.Select(host => host.Process), .. _spares.Values];
            _idleHosts.Clear();
            _spares.Clear();
        }
        foreach (var host in idle)
            Stop(host);
        // A refill holds it while it compiles a warm-up assembly or starts a host.
        lock (_refilling)
        {
            foreach (var file in _hostConfigs.Values.Concat(_warmUpFiles))
                File.Delete(file);
        }
    }

    /// <summary>
    /// The test framework a project references: xunit3, xunit2, nunit or mstest.
    /// </summary>
    static string? Framework(ProjectCompiler unit)
    {
        var references = unit.References.Select(r => Path.GetFileName(r.Reference)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (references.Contains("xunit.v3.core.dll"))
            return "xunit3";
        if (references.Contains("nunit.framework.dll"))
            return "nunit";
        // MSTest's framework assembly is MSTest.TestFramework.dll from v4 on, and the longer name before.
        if (references.Contains("MSTest.TestFramework.dll") || references.Contains("Microsoft.VisualStudio.TestPlatform.TestFramework.dll"))
            return "mstest";
        if (references.Contains("xunit.core.dll"))
            return "xunit2";
        return null;
    }
}
