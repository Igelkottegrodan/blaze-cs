using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// exits, and a few wait already started, so a run doesn't wait for a runtime to start.
/// </summary>
sealed partial class TestRunner(SolutionBuilder builder) : IDisposable
{
    public const string ResultPrefix = "\u0001result:";

    /// <summary>
    /// Hosts running at once: tests parallelize inside an assembly too, so a few are enough.
    /// </summary>
    const int MaxHosts = 4;

    // Idle hosts by runtime version: a test project runs on the runtime it targets, as under `dotnet test`.
    readonly ConcurrentDictionary<string, ConcurrentBag<Process>> _idleHosts = [];
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
        .Concat(RunSettingsFiles(unit.Unit).OfType<string>())
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
    /// that sets only RunConfiguration's EnvironmentVariables and TestRunParameters; a project with any other
    /// .runsettings, or one that can't be resolved, stays on `dotnet test`. Only the project file and its
    /// directory are checked: any .runsettings there, and a RunSettingsFilePath in the project file.
    /// </summary>
    static bool InHost(CompileUnit unit, Action<string> log, out string? runSettings)
    {
        var files = RunSettingsFiles(unit);
        runSettings = files is [var file] && file is not null && HostApplies(file) ? file : null;
        if (files.Count == 0 || runSettings is not null)
            return true;
        log($"blaze-cs: {unit.Name} has a .runsettings, which the test host doesn't apply; running dotnet test");
        return false;
    }

    /// <summary>
    /// The .runsettings files that may apply to a project, null for a RunSettingsFilePath this can't resolve
    /// (a property other than the project's directory in it).
    /// </summary>
    static List<string?> RunSettingsFiles(CompileUnit unit)
    {
        var directory = unit.ProjectDirectory;
        var files = Directory.EnumerateFiles(directory, "*.runsettings").ToHashSet(PathComparer.Instance);
        var project = File.ReadAllText(unit.ProjectFile);
        var settings = RunSettingsFilePath().Matches(project);
        var unresolved = settings.Count == 0 && project.Contains("RunSettingsFilePath", StringComparison.Ordinal);
        foreach (Match setting in settings)
        {
            var value = setting.Groups[1].Value.Trim()
                .Replace("$(MSBuildProjectDirectory)", directory)
                .Replace("$(MSBuildThisFileDirectory)", directory + Path.DirectorySeparatorChar);
            if (value.Contains("$(", StringComparison.Ordinal))
                unresolved = true;
            else
                files.Add(Path.GetFullPath(value, directory));
        }
        return [.. files, .. unresolved ? new string?[] { null } : []];
    }

    [GeneratedRegex(@"<RunSettingsFilePath\b[^>]*>(.*?)</RunSettingsFilePath>", RegexOptions.Singleline)]
    private static partial Regex RunSettingsFilePath();

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

    /// <summary>An idle host for a runtime, and a new one started in its place for the next run.</summary>
    Process TakeHost(string? runtime)
    {
        var idleHosts = _idleHosts.GetOrAdd(runtime ?? "", _ => []);
        Process? host = null;
        while (host is null && idleHosts.TryTake(out var idle))
        {
            if (idle.HasExited)
                idle.Dispose();
            else
                host = idle;
        }
        host ??= StartHost(runtime);
        idleHosts.Add(StartHost(runtime));
        return host;
    }

    /// <summary>
    /// The runtime a test assembly targets, from its runtimeconfig.json: "10.0.0" for a release, the exact
    /// version for a preview (as a preview SDK writes it); null (the host's own) when that can't be read.
    /// </summary>
    static string? RuntimeVersion(string assembly)
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
        return null;
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

    Process StartHost(string? runtime)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        var host = Path.Combine(AppContext.BaseDirectory, "blaze-cs-testhost.dll");
        if (runtime is null)
            start.ArgumentList.Add(host);
        else
        {
            foreach (var argument in new[] { "exec", "--runtimeconfig", HostConfig(runtime), host })
                start.ArgumentList.Add(argument);
        }
        // Tests run with the runtime's defaults, as under `dotnet test`, not the daemon's tuning.
        foreach (var variable in new[] { "DOTNET_gcServer", "DOTNET_TieredPGO", "DOTNET_TC_CallCountingDelayMs" })
            start.Environment.Remove(variable);
        // An idle host exits when its stdin closes, so the daemon exiting any way takes them with it.
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
        foreach (var idleHosts in _idleHosts.Values)
        {
            while (idleHosts.TryTake(out var host))
                Stop(host);
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
