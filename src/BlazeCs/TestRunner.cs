using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace BlazeCs;

/// <summary>
/// What the test host reports for one test assembly. Unsupported means it ran nothing (it doesn't know
/// this framework version), so `dotnet test` runs the project instead.
/// </summary>
sealed record TestHostResult(int Total, int Passed, int Failed, int Skipped, string? Error, string? Unsupported);

/// <summary>
/// Runs the test projects a build affected. xUnit v3, NUnit and MSTest projects run in test host
/// processes, longest first, like `dotnet test` runs projects side by side; the rest go through
/// `dotnet test --no-build`. A host runs one assembly and exits, and a few wait already started, so a
/// run doesn't wait for a runtime to start.
/// </summary>
sealed class TestRunner(SolutionBuilder builder) : IDisposable
{
    public const string ResultPrefix = "\u0001result:";

    /// <summary>
    /// Hosts running at once: tests parallelize inside an assembly too, so a few are enough.
    /// </summary>
    const int MaxHosts = 4;

    readonly ConcurrentBag<Process> _idleHosts = [];
    readonly ConcurrentDictionary<string, TimeSpan> _lastDuration = new(PathComparer.Instance);

    public int Run(IReadOnlyCollection<ProjectCompiler> changed, Action<string> log)
    {
        var testUnits = builder.Units.Where(u => Framework(u) is not null).ToList();
        // Nothing changed means the user wants the tests run, not skipped.
        var affected = changed.Count == 0 ? testUnits : builder.Downstream(changed).Intersect(testUnits).ToList();
        if (affected.Count == 0)
        {
            log("blaze-cs: no test projects depend on what changed");
            return 0;
        }

        var hosted = affected.Where(u => InHost(u, log)).ToList();
        var failed = 0;
        var inHost = new ConcurrentQueue<(ProjectCompiler Unit, string Assembly)>(hosted
            .Select(u => (u, builder.InstalledPath(u)))
            .OrderByDescending(test => _lastDuration.GetValueOrDefault(test.Item2)));
        var workers = Enumerable.Range(0, Math.Min(MaxHosts, inHost.Count)).Select(_ => Task.Run(() =>
        {
            while (inHost.TryDequeue(out var test))
            {
                if (!(RunInHost(Framework(test.Unit)!, test.Assembly, log) ?? DotnetTest(test.Unit, log)))
                    Interlocked.Increment(ref failed);
            }
        })).ToArray();

        foreach (var unit in affected.Except(hosted))
        {
            if (!DotnetTest(unit, log))
                Interlocked.Increment(ref failed);
        }
        Task.WaitAll(workers);
        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// Whether a project runs in a host. A .runsettings would go unapplied there, so those projects (and
    /// xUnit v2's) stay on `dotnet test`. Only the project file and its directory are checked.
    /// </summary>
    static bool InHost(ProjectCompiler unit, Action<string> log)
    {
        if (Framework(unit) == "vstest")
            return false;
        if (Directory.EnumerateFiles(unit.Unit.ProjectDirectory, "*.runsettings").Any()
            || File.ReadAllText(unit.Unit.ProjectFile).Contains("RunSettingsFilePath", StringComparison.Ordinal))
        {
            log($"blaze-cs: {unit.Unit.Name} has a .runsettings, which the test host doesn't apply; running dotnet test");
            return false;
        }
        return true;
    }

    bool DotnetTest(ProjectCompiler unit, Action<string> log) =>
        builder.RunDotnet($"test --no-build \"{unit.Unit.ProjectFile}\"", log) == 0;

    /// <summary>
    /// Runs one assembly in a host and logs its output in one piece: true if it passed, null if the host
    /// ran no tests, which `dotnet test` then settles.
    /// </summary>
    bool? RunInHost(string framework, string assembly, Action<string> log)
    {
        var stopwatch = Stopwatch.StartNew();
        var output = new List<string>();
        var host = TakeHost();
        try { host.StandardInput.WriteLine($"{framework} {assembly}"); }
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
            lock (_idleHosts)
            {
                output.ForEach(log);
                log($"blaze-cs: {name}: {fallback}; running dotnet test");
            }
            return null;
        }

        var passed = result.Error is null && result.Failed == 0;
        if (result.Error is not null)
            output.Add($"blaze-cs: {name}: {result.Error}");
        output.Add($"{(passed ? "Passed!" : "Failed!")}  - Failed: {result.Failed,5}, Passed: {result.Passed,5}, Skipped: {result.Skipped,5}, "
            + $"Total: {result.Total,5}, Duration: {stopwatch.ElapsedMilliseconds} ms - {name}");
        lock (_idleHosts)
            output.ForEach(log);
        return passed;
    }

    /// <summary>An idle host, and a new one started in its place for the next run.</summary>
    Process TakeHost()
    {
        Process? host = null;
        while (host is null && _idleHosts.TryTake(out var idle))
        {
            if (idle.HasExited)
                idle.Dispose();
            else
                host = idle;
        }
        host ??= StartHost();
        _idleHosts.Add(StartHost());
        return host;
    }

    static Process StartHost()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "blaze-cs-testhost.dll"));
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
        while (_idleHosts.TryTake(out var host))
            Stop(host);
    }

    /// <summary>
    /// The test framework a project references: xunit3, nunit or mstest run in a host; vstest (xUnit v2)
    /// goes through `dotnet test`.
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
            return "vstest";
        return null;
    }
}
