using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace BlazeCs;

/// <summary>What the test host reports for one test assembly.</summary>
sealed record TestHostResult(int Total, int Passed, int Failed, int Skipped, bool Unloaded, string? Error);

/// <summary>
/// Runs the test projects a build affected. xUnit v3 projects run in a small pool of warm test host
/// processes, longest first, like `dotnet test` runs projects side by side; other frameworks go
/// through `dotnet test --no-build`.
/// </summary>
sealed class TestRunner(SolutionBuilder builder) : IDisposable
{
    public const string ResultPrefix = "\u0001result:";

    /// <summary>
    /// One assembly per host at a time: the current directory, AppContext.BaseDirectory and the console
    /// are per process. Tests parallelize inside an assembly too, so a few hosts are enough.
    /// </summary>
    const int MaxHosts = 4;

    readonly ConcurrentBag<Process> _idleHosts = [];
    readonly ConcurrentDictionary<string, TimeSpan> _lastDuration = new(StringComparer.Ordinal);

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

        var failed = 0;
        var inHost = new ConcurrentQueue<string>(affected
            .Where(u => Framework(u) == "xunit3")
            .Select(builder.InstalledPath)
            .OrderByDescending(path => _lastDuration.GetValueOrDefault(path)));
        var workers = Enumerable.Range(0, Math.Min(MaxHosts, inHost.Count)).Select(_ => Task.Run(() =>
        {
            while (inHost.TryDequeue(out var assembly))
            {
                if (!RunInHost(assembly, log))
                    Interlocked.Increment(ref failed);
            }
        })).ToArray();

        foreach (var unit in affected.Where(u => Framework(u) != "xunit3"))
        {
            if (builder.RunDotnet($"test --no-build \"{unit.Unit.ProjectFile}\"", log) != 0)
                Interlocked.Increment(ref failed);
        }
        Task.WaitAll(workers);
        return failed > 0 ? 1 : 0;
    }

    /// <summary>Runs one assembly in an idle host and logs its output in one piece; true if it passed.</summary>
    bool RunInHost(string assembly, Action<string> log)
    {
        var stopwatch = Stopwatch.StartNew();
        var output = new List<string>();
        var host = _idleHosts.TryTake(out var idle) && !idle.HasExited ? idle : StartHost();
        host.StandardInput.WriteLine(assembly);

        TestHostResult? result = null;
        while (host.StandardOutput.ReadLine() is { } line)
        {
            if (!line.StartsWith(ResultPrefix, StringComparison.Ordinal))
            {
                output.Add(line);
                continue;
            }
            result = JsonSerializer.Deserialize<TestHostResult>(line[ResultPrefix.Length..])!;
            break;
        }

        // A test that keeps its load context alive (a leaked thread, a static event) would let the next
        // assembly see stale state, so that host is replaced rather than reused.
        if (result is { Unloaded: true })
            _idleHosts.Add(host);
        else
            Stop(host);
        result ??= new TestHostResult(0, 0, 0, 0, Unloaded: false, Error: "the test host exited mid-run");
        _lastDuration[assembly] = stopwatch.Elapsed;

        var passed = result.Error is null && result.Failed == 0;
        if (result.Error is not null)
            output.Add($"blaze-cs: {Path.GetFileName(assembly)}: {result.Error}");
        output.Add($"{(passed ? "Passed!" : "Failed!")} - Failed: {result.Failed,5}, Passed: {result.Passed,5}, Skipped: {result.Skipped,5}, "
            + $"Total: {result.Total,5}, Duration: {stopwatch.ElapsedMilliseconds} ms - {Path.GetFileName(assembly)}");
        lock (_idleHosts)
            output.ForEach(log);
        return passed;
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

    static string? Framework(ProjectCompiler unit)
    {
        var references = unit.References.Select(r => Path.GetFileName(r.Reference)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (references.Contains("xunit.v3.core.dll"))
            return "xunit3";
        // MSTest's framework assembly is MSTest.TestFramework.dll from v4 on, and the longer name before.
        if (references.Contains("xunit.core.dll") || references.Contains("nunit.framework.dll")
            || references.Contains("MSTest.TestFramework.dll") || references.Contains("Microsoft.VisualStudio.TestPlatform.TestFramework.dll"))
            return "vstest";
        return null;
    }
}
