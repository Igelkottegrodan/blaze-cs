using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace BlazeCs;

/// <summary>
/// What the test host reports for one test assembly. Unsupported means it ran nothing (it doesn't know
/// this framework version), so `dotnet test` runs the project instead.
/// </summary>
sealed record TestHostResult(int Total, int Passed, int Failed, int Skipped, string? Error, string? Unsupported);

/// <summary>What a test host runs: a framework, an assembly, and the .runsettings it applies, if any.</summary>
sealed record TestHostRequest(string Framework, string Assembly, string? RunSettings);

/// <summary>
/// Runs the test projects whose inputs changed since they last passed in this daemon. xUnit (v2 and
/// v3), NUnit and MSTest projects run in test host processes, longest first, like `dotnet test` runs
/// projects side by side; the rest go through `dotnet test --no-build`. A host runs one assembly and
/// exits. Idle hosts wait already started for each runtime the test projects target, as many as run at
/// once, so a run doesn't wait for a runtime to start.
/// </summary>
sealed class TestRunner(SolutionBuilder builder) : IDisposable
{
    public const string ResultPrefix = "\u0001result:";

    /// <summary>
    /// Hosts running at once: tests parallelize inside an assembly too, so a few are enough.
    /// </summary>
    const int MaxHosts = 4;

    // Idle hosts by runtime version: a test project runs on the runtime it targets, as under `dotnet test`.
    // "" is the host's own runtime, for an assembly whose runtimeconfig.json can't be read.
    readonly Dictionary<string, List<Process>> _idleHosts = [];
    // Idle hosts to keep per runtime: as many as run at once, but no more than its hosted test projects.
    Dictionary<string, int> _targets = [];
    // Held while hosts start, so concurrent refills don't overshoot a target.
    readonly object _refilling = new();
    bool _disposed;
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
                Record(test.Unit, RunInHost(test.Request, log) ?? DotnetTest(test.Unit, log));
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
        var host = TakeHost(RuntimeVersion(assembly));
        try { host.StandardInput.WriteLine(JsonSerializer.Serialize(request)); }
        catch (IOException) { } // it died while idle: no result, so the run fails below

        TestHostResult? result = null;
        while (host.StandardOutput.ReadLine() is { } line)
        {
            // Test output may end mid-line, with the result after it.
            var start = line.IndexOf(ResultPrefix, StringComparison.Ordinal);
            if (start < 0)
            {
                output.Add(line);
                continue;
            }
            if (start > 0)
                output.Add(line[..start]);
            result = JsonSerializer.Deserialize<TestHostResult>(line[(start + ResultPrefix.Length)..])!;
            break;
        }
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

    /// <summary>
    /// Works out how many idle hosts each runtime needs from the test projects loaded, and starts the missing
    /// ones in the background: the daemon calls it after loading and after each command, so a run finds them
    /// ready.
    /// </summary>
    public void Prestart()
    {
        // Read now: the next command may change the units while the hosts start.
        var targets = builder.Units.Where(u => Framework(u) is not null && InHost(u.Unit, _ => { }, out _))
            .GroupBy(u => RuntimeVersion(builder.InstalledPath(u)))
            .ToDictionary(g => g.Key, g => Math.Min(MaxHosts, g.Count()));
        lock (_idleHosts)
            _targets = targets;
        Task.Run(() =>
        {
            foreach (var runtime in targets.Keys)
                Refill(runtime);
        });
    }

    /// <summary>Starts hosts until a runtime has all its idle ones.</summary>
    void Refill(string runtime)
    {
        lock (_refilling)
        {
            while (true)
            {
                lock (_idleHosts)
                {
                    if (_disposed || IdleHosts(runtime).Count >= _targets.GetValueOrDefault(runtime))
                        return;
                }
                var host = StartHost(runtime);
                lock (_idleHosts)
                {
                    if (!_disposed)
                    {
                        IdleHosts(runtime).Add(host);
                        continue;
                    }
                }
                Stop(host);
                return;
            }
        }
    }

    List<Process> IdleHosts(string runtime)
    {
        if (!_idleHosts.TryGetValue(runtime, out var idle))
            _idleHosts[runtime] = idle = [];
        return idle;
    }

    /// <summary>An idle host for a runtime, or a new one if none is ready; the pool refills in the background.</summary>
    Process TakeHost(string runtime)
    {
        Process? host = null;
        lock (_idleHosts)
        {
            var idle = IdleHosts(runtime);
            while (host is null && idle.Count > 0)
            {
                var candidate = idle[0];
                idle.RemoveAt(0);
                if (candidate.HasExited)
                    candidate.Dispose();
                else
                    host = candidate;
            }
        }
        _ = Task.Run(() => Refill(runtime));
        return host ?? StartHost(runtime);
    }

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

    Process StartHost(string runtime)
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
        // Tests run with the runtime's defaults, as under `dotnet test`, not the daemon's tuning.
        foreach (var variable in new[] { "DOTNET_gcServer", "DOTNET_TieredPGO", "DOTNET_TC_CallCountingDelayMs" })
            start.Environment.Remove(variable);
        // A host exits when its stdin closes, so the daemon dying any way takes every host with it.
        return Process.Start(start)!;
    }

    static void Stop(Process host)
    {
        try { host.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        host.Dispose();
    }

    public void Dispose()
    {
        lock (_idleHosts)
        {
            _disposed = true;
            foreach (var host in _idleHosts.Values.SelectMany(idle => idle))
                Stop(host);
            _idleHosts.Clear();
        }
        foreach (var config in _hostConfigs.Values)
            File.Delete(config);
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
