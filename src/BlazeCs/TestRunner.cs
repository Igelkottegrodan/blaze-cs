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
/// A Debug project's host, once it has run the project, stays up as the project's host: the next run sends it
/// the edit as a delta (hot reload, HotReload.cs) when the edit is one the runtime can apply, and the tests run
/// again in the same process; otherwise the project runs in a new host, as it would without, the output says why in
/// one line, and the new host stays up in the old one's place.
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
    // Hosts that Prestart stops, in the background, in order.
    Task _stopping = Task.CompletedTask;
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
    /// <param name="HotReload">Started so that it can take updates (DOTNET_MODIFIABLE_ASSEMBLIES): a Debug project's, when the engine loaded.</param>
    readonly record struct HostKey(string Framework, string Runtime, string? RunSettings, string Inputs, bool HotReload);

    sealed record HostTarget(ProjectCompiler Unit, string Assembly, HostKey Key);

    sealed record IdleHost(Process Process, HostKey Key);

    /// <summary>
    /// A host that ran its project and waits for an update (Protocol.cs): the inputs its tests last ran with
    /// (<see cref="Inputs(ProjectCompiler)"/>), and its hot reload session, which starts in the background.
    /// </summary>
    sealed class LiveHost(Process process, HostKey key, string inputs)
    {
        public Process Process => process;
        public HostKey Key => key;
        public string Inputs { get; set; } = inputs;
        /// <summary>Null until the solution's units hold what the host loaded (see SolutionBuilder.WhenSettled); its result is null if it didn't start.</summary>
        public Task<HotReloadSession?>? Session { get; set; }
        public string? Failure { get; set; }
        /// <summary>Set by End, under a lock on the host: a session that hasn't started by then never does.</summary>
        public bool Ended { get; set; }
    }

    // Live hosts by test assembly, one per test project at most, in place of its idle host.
    readonly Dictionary<string, LiveHost> _live = new(PathComparer.Instance);

    // The hot reload engine (HotReload.cs), loaded in the background from the solution's SDK, or why it's unavailable.
    readonly Task<(HotReloadEngine? Engine, string? Unavailable)> _engine = Task.Run(() =>
    {
        // Windows keeps a loaded assembly's file locked: a host that stays up would keep the build (blaze-cs's
        // copies, and any dotnet build) from replacing the project outputs next to its test assembly.
        string? unavailable = WindowsUnavailable;
        var engine = OperatingSystem.IsWindows() ? null : HotReloadEngine.Load(builder.SdkDirectory, out unavailable);
        Console.WriteLine($"{DateTime.Now:O} " + (engine is null ? $"hot reload is unavailable: {unavailable}" : "hot reload engine loaded"));
        // Snapshots of daemons that died without their Dispose.
        var roots = Path.Combine(builder.StateDirectory, "hotreload");
        try
        {
            foreach (var directory in Directory.Exists(roots) ? Directory.EnumerateDirectories(roots) : [])
            {
                if (int.TryParse(Path.GetFileName(directory), out var pid) && pid != Environment.ProcessId && !Running(pid))
                    Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return (engine, unavailable);
    });

    const string WindowsUnavailable = "on Windows, a test host that stays up keeps the project outputs it loaded locked, and builds couldn't replace them";

    static bool Running(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
    bool _reportedUnavailable;

    HotReloadEngine? Engine => _engine.Result.Engine;

    /// <summary>Where sessions copy the assemblies their hosts loaded, which edits replace on disk: one directory per daemon.</summary>
    string SnapshotRoot => Path.Combine(builder.StateDirectory, "hotreload", Environment.ProcessId.ToString());
    readonly ConcurrentDictionary<string, TimeSpan> _lastDuration = new(PathComparer.Instance);

    /// <summary>
    /// Each test project's last run in this daemon, by assembly, if it passed in a new host: the inputs it ran
    /// with and its result. A project without one (never run here, it failed, or its pass was hot reloaded) runs
    /// next time. A hot reloaded pass doesn't count: statics its tests read may have been computed by code
    /// edited since (a static readonly field's initializer calling it, a Lazy, a cache), so the next run checks
    /// it in a new host, where they start anew.
    /// </summary>
    readonly ConcurrentDictionary<string, (string Inputs, string Result)> _passed = new(PathComparer.Instance);
    readonly ConcurrentDictionary<string, (FileStamp Stamp, string Hash)> _hashes = new(PathComparer.Instance);

    /// <summary>
    /// What a test run reports: whether it passed, in a few words for the next unchanged run, and whether it
    /// ran in a host that took the edit as a delta.
    /// </summary>
    readonly record struct Outcome(bool Passed, string Result, bool HotReloaded = false);

    /// <param name="all">Run every test project, not only those whose inputs changed since they passed.</param>
    public int Run(bool all, Action<string> log)
    {
        var tests = builder.Units.Where(u => Framework(u) is not null).ToList();
        if (tests.Count == 0)
        {
            log("blaze-cs: no test projects");
            return 0;
        }
        if (_engine.Result.Unavailable is { } unavailable && !_reportedUnavailable)
        {
            _reportedUnavailable = true;
            log($"blaze-cs: hot reload is unavailable: {unavailable}");
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
            if (outcome is { Passed: true, HotReloaded: false })
                _passed[unit.Unit.AssemblyPath] = (inputs[unit], outcome.Result);
            else
            {
                _passed.TryRemove(unit.Unit.AssemblyPath, out _);
                if (!outcome.Passed)
                    Interlocked.Increment(ref failed);
            }
        }

        var hosted = affected.Select(u => (Unit: u, Applies: InHost(u.Unit, log, out var runSettings), RunSettings: runSettings))
            .Where(test => test.Applies).ToList();
        var inHost = new ConcurrentQueue<(ProjectCompiler Unit, TestHostRequest Request)>(hosted
            .Select(test => (test.Unit, new TestHostRequest(Framework(test.Unit)!, builder.InstalledPath(test.Unit), test.RunSettings)))
            .OrderByDescending(test => _lastDuration.GetValueOrDefault(test.Item2.Assembly)));
        var kept = new ConcurrentBag<(ProjectCompiler Unit, LiveHost Host)>();
        var workers = Enumerable.Range(0, Math.Min(MaxHosts, inHost.Count)).Select(_ => Task.Run(() =>
        {
            while (inHost.TryDequeue(out var test))
            {
                var outcome = RunHosted(test.Unit, test.Request, inputs[test.Unit], kept, log);
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
        try
        {
            Task.WaitAll(workers);
        }
        finally
        {
            // Also when a worker threw: the hosts that ran are kept, or ended if they can't be.
            foreach (var (unit, host) in kept)
                Keep(unit, host);
        }
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
    /// Runs a test project in a host: in its live host, with the edit since its last run as a delta, if the
    /// edit hot reloads (see <see cref="Plan"/>); otherwise in a host that loads it anew. Null if the host ran
    /// no tests, which `dotnet test` then settles. A host that can stay up after its run goes to
    /// <paramref name="kept"/>.
    /// </summary>
    Outcome? RunHosted(ProjectCompiler unit, TestHostRequest request, string inputs, ConcurrentBag<(ProjectCompiler, LiveHost)> kept, Action<string> log)
    {
        LiveHost? live;
        lock (_idleHosts)
            _live.Remove(request.Assembly, out live);
        if (live is not null)
            return HotReload(unit, request, live, inputs, kept, log);

        var stopwatch = Stopwatch.StartNew();
        var output = new List<string>();
        var key = Key(request.Framework, request.Assembly, request.RunSettings, unit.Debug);
        var (host, hotReload) = TakeHost(request.Assembly, key, replacing: false);
        // A host that died while idle gives no result, so the run fails below.
        var result = Send(host, request) ? ReadResult(host, output) : null;
        return Finish(unit, request, inputs, key, host, hotReload, result, stopwatch, output, kept, log);
    }

    /// <summary>
    /// After a run in a host that loaded the project: keeps the host as the project's live host if it can take
    /// the next edit, otherwise stops it, with why in the daemon log; then reports the run.
    /// </summary>
    Outcome? Finish(ProjectCompiler unit, TestHostRequest request, string inputs, HostKey key, Process host, bool hotReload, TestHostResult? result,
        Stopwatch stopwatch, List<string> output, ConcurrentBag<(ProjectCompiler, LiveHost)> kept, Action<string> log)
    {
        if (hotReload && result is { CannotHotReload: null })
            kept.Add((unit, new LiveHost(host, key, inputs)));
        else
        {
            var why = result is null ? "it exited mid-run"
                : !unit.Debug ? $"{unit.Unit.Name} is compiled with optimizations (Release), so its hosts never take updates"
                : !hotReload ? "it was started for no project, without DOTNET_MODIFIABLE_ASSEMBLIES"
                : result.CannotHotReload;
            // Without the engine, nothing stays up, and the first test command said so.
            if (Engine is not null)
                Console.WriteLine($"{DateTime.Now:O} hot reload: test host {host.Id} ({Path.GetFileName(request.Assembly)}) doesn't stay up: {why}");
            Stop(host);
        }
        result ??= new TestHostResult(0, 0, 0, 0, Error: "the test host exited mid-run", Unsupported: null);
        return Report(request.Assembly, result, stopwatch, output, log);
    }

    /// <summary>
    /// Logs a host's run of a test assembly in one piece, as `dotnet test` reports it, with "(hot reloaded)" after
    /// the summary of a run in a host that took the edit as a delta; null if it ran no tests.
    /// </summary>
    Outcome? Report(string assembly, TestHostResult result, Stopwatch stopwatch, List<string> output, Action<string> log, bool hotReloaded = false)
    {
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
            + $"Total: {result.Total,5}, Duration: {stopwatch.ElapsedMilliseconds} ms - {name}{(hotReloaded ? " (hot reloaded)" : "")}");
        lock (_hostConfigs)
            output.ForEach(log);
        return new Outcome(passed, result.Skipped > 0 ? $"{result.Passed} passed, {result.Skipped} skipped" : $"{result.Passed} passed", hotReloaded);
    }

    /// <summary>
    /// Runs a test project in its live host: sends it the edit since its last run as a delta and reports the run
    /// that follows, if the edit hot reloads (<see cref="Plan"/>) and the host applies it. Otherwise the project
    /// runs as it would without hot reload (<see cref="RunLive"/>): the live host and its session end, and the
    /// project runs in a new host, which can stay up for the next edit with a session of its own.
    /// </summary>
    Outcome? HotReload(ProjectCompiler unit, TestHostRequest request, LiveHost live, string inputs, ConcurrentBag<(ProjectCompiler, LiveHost)> kept, Action<string> log)
    {
        var stopwatch = Stopwatch.StartNew();
        var name = Path.GetFileName(request.Assembly);
        var pid = live.Process.Id;
        HotReloadSession? session = null;
        TestHostUpdate? update = null;
        string? reason;
        try
        {
            (session, update, reason) = Plan(live, inputs);
        }
        catch (Exception e)
        {
            // The engine, or a Workspaces API this blaze-cs was compiled against, failed: today's path settles it.
            Console.WriteLine($"{DateTime.Now:O} hot reload: {e}");
            reason = $"internal error: {e.Message}";
        }
        var key = Key(request.Framework, request.Assembly, request.RunSettings, unit.Debug);
        var hotReload = false;
        var output = new List<string>();
        var ended = false;
        HostRun run;
        try
        {
            run = RunLive(live.Process, update, reason, request, () =>
            {
                // Ended before its replacement starts: if starting that throws, the live host doesn't stay behind.
                ended = true;
                End(live);
                (var host, hotReload) = TakeHost(request.Assembly, key, replacing: true);
                return host;
            }, output, announce: !(update is null && reason == NothingChanged));
        }
        catch
        {
            if (!ended)
                End(live);
            throw;
        }
        if (run.Fallback is not null)
        {
            Console.WriteLine($"{DateTime.Now:O} hot reload: test host {pid} ({name}) ends, and {name} runs in a new host ({run.Host.Id}): {run.Fallback}");
            return Finish(unit, request, inputs, key, run.Host, hotReload, run.Result, stopwatch, output, kept, log);
        }

        var result = run.Result!;
        var keep = result.CannotHotReload is null;
        {
            var modules = session!.Modules.ToDictionary(module => module.Mvid);
            static string Types(TestHostDelta delta) => delta.UpdatedTypes?.Length is 1 ? "1 type" : $"{delta.UpdatedTypes?.Length ?? 0} types";
            var applied = string.Join(", ", update!.Deltas.Select(delta => $"{modules[delta.Module].Name} ({Types(delta)})"));
            foreach (var delta in update.Deltas)
                Console.WriteLine($"{DateTime.Now:O} hot reload: test host {pid} ({name}) applied a delta to module {modules[delta.Module].Name} "
                    + $"({delta.Module}): {Types(delta)} updated");
            output.Insert(0, $"blaze-cs: {name}: hot reloaded {applied} into its test host (pid {pid})");
            try
            {
                session.Commit();
            }
            catch (Exception e)
            {
                // The host runs the edit, but the session no longer knows what it runs: the next edit takes a new host.
                Console.WriteLine($"{DateTime.Now:O} hot reload: test host {pid} ({name}) ends after this run: committing its update failed: {e}");
                keep = false;
            }
        }
        if (keep)
        {
            live.Inputs = inputs;
            kept.Add((unit, live));
        }
        else
            End(live);
        return Report(request.Assembly, result, stopwatch, output, log, hotReloaded: true);
    }

    /// <summary>A run in a host: the host, its result (null if the host exited first), and why the run left the live host for a new one, if it did.</summary>
    internal sealed record HostRun(Process Host, TestHostResult? Result, string? Fallback);

    /// <summary>
    /// Runs a test assembly in its live host with the update that carries the edit since the host's last run,
    /// and returns that run. If the edit doesn't hot reload (no update, and <paramref name="reason"/> says why),
    /// or the host doesn't apply the update or run tests after it, the assembly, as rebuilt, runs in a host that
    /// <paramref name="restart"/> starts instead, and the output starts with a line that says so
    /// (<see cref="FallbackLine"/>) in place of the live host's, unless <paramref name="announce"/> is false. Ending
    /// the live host is the caller's.
    /// </summary>
    internal static HostRun RunLive(Process live, TestHostUpdate? update, string? reason, TestHostRequest request, Func<Process> restart, List<string> output,
        bool announce = true)
    {
        if (update is not null)
        {
            var result = Send(live, update) ? ReadResult(live, output) : null;
            reason = result is null ? "the host exited"
                : result.Unsupported
                // After an update, a run without tests means the framework didn't find them again; a new host settles it.
                ?? (result is { Total: 0, Error: null } ? "it found no tests after the update" : null);
            if (reason is null)
                return new(live, result, null);
        }
        reason ??= "it has no update";
        output.Clear();
        if (announce)
            output.Add(FallbackLine(request.Assembly, reason));
        var host = restart();
        return new(host, Send(host, request) ? ReadResult(host, output) : null, reason);
    }

    /// <summary>Why a rerun with nothing changed runs in a new host: no edit failed to hot reload, so the output says nothing of it.</summary>
    const string NothingChanged = "nothing its tests run with changed, and a new host starts their statics anew";

    /// <summary>The line that tells the user a test project's edit didn't hot reload, and why.</summary>
    internal static string FallbackLine(string assembly, string reason) =>
        $"blaze-cs: {Path.GetFileName(assembly)}: not hot reloaded: {reason.ReplaceLineEndings(" ")}; running it in a new test host";

    /// <summary>
    /// Whether an edit hot reloads into a live host, and the update that carries it; otherwise why not. The
    /// one place that decides it: only when what changed since the host's last run is assemblies of the
    /// session's modules, and the engine has a delta for each of them, with nothing to restart or rebuild.
    /// The engine only takes method body edits (HotReloadEngine.Capabilities), and asks for a restart when one
    /// would have no effect (a static field's initializer). With nothing changed (`test --all`, or a run that
    /// failed), the project runs in a new host: in the live one, its tests would see the statics they left.
    /// Statics carry over between hot reloaded runs too (#129). An edit that comes before the host's session
    /// is ready isn't held back for it: the project runs in a new host.
    /// </summary>
    (HotReloadSession? Session, TestHostUpdate? Update, string? Reason) Plan(LiveHost live, string inputs)
    {
        if (live.Process.HasExited)
            return (null, null, "the host has exited");
        var before = InputHashes(live.Inputs);
        var now = InputHashes(inputs);
        if (!before.Keys.ToHashSet(PathComparer.Instance).SetEquals(now.Keys))
            return (null, null, "the set of files its tests run with changed");
        var changed = now.Where(input => before[input.Key] != input.Value).Select(input => input.Key).ToList();
        if (changed.Count == 0)
            return (null, null, NothingChanged);

        // A session still starting isn't waited for: the project runs in a new host at once.
        Task<HotReloadSession?>? started;
        lock (live)
            started = live.Session;
        if (started is not { IsCompleted: true })
            return (null, null, "its hot reload session wasn't ready");
        if (started.Result is not { } session)
            return (null, null, live.Failure ?? "it has no hot reload session");
        var loaded = session.Modules.ToDictionary(module => module.Loaded, PathComparer.Instance);
        if (changed.FirstOrDefault(path => !loaded.ContainsKey(path)) is { } other)
            return (session, null, $"{Path.GetFileName(other)} changed");
        if (changed.Select(path => loaded[path]).FirstOrDefault(module => HotReloadSession.Resources(module.Loaded) != module.Resources) is { } resourced)
            return (session, null, $"{resourced.Name}'s resources changed, which a delta doesn't carry");

        var units = builder.Units.ToDictionary(u => u.Unit.AssemblyPath, PathComparer.Instance);
        // The runtime edits only modules compiled without optimizations: the engine isn't asked for the others.
        if (changed.Select(path => loaded[path]).FirstOrDefault(module => units.TryGetValue(module.Key, out var unit) && !unit.Debug) is { } optimized)
            return (session, null, $"{optimized.Name} is compiled with optimizations (Release)");
        var sources = new Dictionary<string, UnitSources>(PathComparer.Instance);
        foreach (var module in session.Modules)
        {
            if (!units.TryGetValue(module.Key, out var unit) || unit.Sources is not { } current)
                return (session, null, $"{module.Name} is no longer compiled in the daemon");
            sources[module.Key] = current;
        }
        var (updates, keys) = session.Changes(sources);
        if (updates.Status != "ReadyToApply")
            return (session, null, updates.Errors.FirstOrDefault() ?? $"the engine's status is {updates.Status}");
        if (updates.ProjectsToRestartOrRebuild > 0)
            return (session, null, "the engine asks for a restart or rebuild" + (updates.RudeEdits.FirstOrDefault() is { } rude ? $": {rude}" : ""));
        // A rude edit that comes as a warning with a delta anyway (ENC0118: a static field's initializer "might not
        // have any effect until the application is restarted") would run the tests on the old behavior and pass.
        if (updates.RudeEdits.FirstOrDefault() is { } warned)
            return (session, null, $"the engine reports a rude edit: {warned}");
        var updated = updates.Deltas.Select(delta => keys[delta.Project]).ToHashSet(PathComparer.Instance);
        if (changed.Select(path => loaded[path]).FirstOrDefault(module => !updated.Contains(module.Key)) is { } missed)
            return (session, null, $"{missed.Name} changed, and the engine has no delta for it");
        // Each delta names its baseline, which a host that hasn't loaded the module yet loads first: the file
        // its tests would load is the edited one, which no delta of this session applies to.
        var baselines = session.Modules.ToDictionary(module => module.Mvid, module => module.Snapshot);
        return (session, new TestHostUpdate([.. updates.Deltas.Select(delta =>
            new TestHostDelta(delta.Module, delta.Metadata, delta.IL, delta.Pdb, delta.UpdatedTypes, baselines.GetValueOrDefault(delta.Module)))]), null);
    }

    /// <summary>
    /// Keeps a host that ran its project as the project's live host, and starts its hot reload session once the
    /// units hold what the host loaded: at once, or after the first load's warm-up.
    /// </summary>
    void Keep(ProjectCompiler unit, LiveHost live)
    {
        var assembly = builder.InstalledPath(unit);
        lock (_idleHosts)
        {
            if (_disposed || _live.ContainsKey(assembly))
            {
                End(live);
                return;
            }
            _live[assembly] = live;
        }
        if (live.Session is not null)
            return; // a session that took this run's update
        // The host as the log names it (Stop disposes the process, after which it has no id).
        var host = $"test host {live.Process.Id} ({Path.GetFileName(assembly)})";
        Console.WriteLine($"{DateTime.Now:O} hot reload: {host} stays up for {unit.Unit.Name}");
        // The host's modules are copied now, before an edit replaces them on disk; the engine's work runs in the background.
        builder.WhenSettled(() =>
        {
            // A host ended in the meantime (Prestart, Dispose) gets no session, which nothing would end.
            lock (live)
            {
                if (live.Ended)
                    return;
                try
                {
                    var (modules, sources) = Capture(unit, live.Inputs);
                    live.Session = Task.Run(() =>
                    {
                        try
                        {
                            if (SessionDelay > TimeSpan.Zero)
                                Thread.Sleep(SessionDelay);
                            var session = HotReloadSession.Start(Engine!, modules, sources);
                            Console.WriteLine($"{DateTime.Now:O} hot reload: {host} has its session");
                            return session;
                        }
                        catch (Exception e)
                        {
                            live.Failure = $"its session didn't start: {e.Message}";
                            Console.WriteLine($"{DateTime.Now:O} hot reload: {host} has no session: {e}");
                            return null;
                        }
                    });
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or BadImageFormatException)
                {
                    live.Failure = e.Message;
                    live.Session = Task.FromResult<HotReloadSession?>(null);
                    Console.WriteLine($"{DateTime.Now:O} hot reload: {host} has no session: {e.Message}");
                }
            }
        });
        // Not settled: no session, so the next run doesn't wait for one.
        lock (live)
            live.Session ??= Task.FromResult<HotReloadSession?>(null);
    }

    /// <summary>
    /// BLAZE_CS_HOT_RELOAD_DELAY=&lt;milliseconds&gt; holds back each hot reload session's start that long, so
    /// the fixture check can send an edit before a live host's session is ready.
    /// </summary>
    static readonly TimeSpan SessionDelay = int.TryParse(Environment.GetEnvironmentVariable("BLAZE_CS_HOT_RELOAD_DELAY"), out var delay) && delay > 0
        ? TimeSpan.FromMilliseconds(delay) : TimeSpan.Zero;

    /// <summary>The inputs a run used (<see cref="Inputs(ProjectCompiler)"/>), by path.</summary>
    static Dictionary<string, string> InputHashes(string inputs) => inputs.Split('\n')
        .ToDictionary(line => line[..line.LastIndexOf(' ')], line => line[(line.LastIndexOf(' ') + 1)..], PathComparer.Instance);

    /// <summary>
    /// The modules a test project's host loaded, each copied as loaded, and their units' sources, which compiled
    /// to those bytes. Throws if a module on disk is no longer what the host's run used (<paramref name="inputs"/>):
    /// a session made from it would take the next build for what the host runs.
    /// </summary>
    (List<LoadedModule> Modules, Dictionary<string, UnitSources> Sources) Capture(ProjectCompiler test, string inputs)
    {
        var ran = InputHashes(inputs);
        var modules = new List<LoadedModule>();
        var sources = new Dictionary<string, UnitSources>(PathComparer.Instance);
        foreach (var (unit, path) in builder.RuntimeUnits(test))
        {
            if (sources.ContainsKey(unit.Unit.AssemblyPath))
                continue;
            // Under the unit's lock, which a compile served to a dotnet build holds from its sources to its
            // writes: the sources, the assembly it wrote and its PDB are of one compile.
            lock (unit)
            {
                // Reproduces first: a unit it compiles holds that compile's sources after it.
                if (!builder.Reproduces(unit) || unit.Sources is not { } current)
                    throw new InvalidOperationException($"{unit.Unit.Name}'s assembly on disk isn't what the daemon compiles from its sources");
                // The same before and after the copy: the copy has the bytes the run loaded, which are the
                // compile's (a served compile writes the assembly before MSBuild copies it next to the tests).
                // (The copy's own hash differs: its PDB path is rewritten.)
                bool Unchanged() => ran.TryGetValue(path, out var hash) && Hash(path) == hash && Hash(unit.AssemblyPath) == hash;
                if (!Unchanged())
                    throw new InvalidOperationException($"{unit.Unit.Name} changed on disk after the run, before its session started");
                var (snapshot, mvid, resources) = HotReloadSession.Snapshot(path, unit.PdbPath, SnapshotRoot);
                if (!Unchanged())
                    throw new InvalidOperationException($"{unit.Unit.Name} changed on disk after the run, before its session started");
                modules.Add(new LoadedModule(unit.Unit.AssemblyPath, unit.Unit.Name, unit.Unit.ProjectFile, path, mvid, snapshot, resources));
                sources[unit.Unit.AssemblyPath] = current;
            }
        }
        return (modules, sources);
    }

    /// <summary>Ends a live host and its session.</summary>
    void End(LiveHost live)
    {
        Task<HotReloadSession?>? session;
        lock (live)
        {
            live.Ended = true;
            session = live.Session;
        }
        Stop(live.Process);
        session?.ContinueWith(started =>
        {
            try { started.Result?.End(); }
            catch (Exception e) { Console.WriteLine($"{DateTime.Now:O} hot reload: ending a session: {e.Message}"); }
        }, TaskScheduler.Default);
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
            .Select(test => new HostTarget(test.Unit, test.Assembly, Key(test.Framework!, test.Assembly, test.RunSettings, test.Unit.Debug)))
            .ToList();
        var runtimes = hosted.Select(test => RuntimeVersion(test.Assembly)).ToHashSet();
        List<Process> stopped = [];
        List<LiveHost> ended = [];
        lock (_idleHosts)
        {
            _targets = targets;
            _spareRuntimes = runtimes;
            foreach (var (assembly, idle) in _idleHosts.ToList())
            {
                if (idle.Process.HasExited || !targets.Any(target => PathComparer.Instance.Equals(target.Assembly, assembly) && target.Key == idle.Key))
                {
                    _idleHosts.Remove(assembly);
                    stopped.Add(idle.Process);
                }
            }
            foreach (var (assembly, live) in _live.ToList())
            {
                if (live.Process.HasExited || !targets.Any(target => PathComparer.Instance.Equals(target.Assembly, assembly) && target.Key == live.Key))
                {
                    _live.Remove(assembly);
                    ended.Add(live);
                    Console.WriteLine($"{DateTime.Now:O} hot reload: test host {live.Process.Id} ({Path.GetFileName(assembly)}) ends: "
                        + (live.Process.HasExited ? "it exited" : $"its project is no longer among the {MaxIdleHosts} that get a host"));
                }
            }
            foreach (var (runtime, spare) in _spares.ToList())
            {
                if (spare.HasExited || !runtimes.Contains(runtime))
                {
                    _spares.Remove(runtime);
                    stopped.Add(spare);
                }
            }
            // Killing a process tree reads every process's /proc entry: in the background, not before the command's
            // exit code. Dispose waits for it.
            if (stopped.Count > 0 || ended.Count > 0)
            {
                _stopping = _stopping.ContinueWith(_ =>
                {
                    try
                    {
                        stopped.ForEach(Stop);
                        ended.ForEach(End);
                    }
                    catch (Exception e) { Console.WriteLine($"{DateTime.Now:O} stopping test hosts: {e}"); }
                }, TaskScheduler.Default);
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
                missing = _disposed ? [] : [.. _targets.Where(target => !_idleHosts.ContainsKey(target.Assembly) && !_live.ContainsKey(target.Assembly))];
            foreach (var target in missing)
            {
                var host = StartHost(target.Key.Runtime, (target.Key.Framework, target.Assembly, target.Key.RunSettings, WarmUp(target)), target.Key.HotReload);
                lock (_idleHosts)
                {
                    // A Prestart since may have dropped it.
                    if (!_disposed && _targets.Contains(target) && !_idleHosts.ContainsKey(target.Assembly) && !_live.ContainsKey(target.Assembly))
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
                var host = StartHost(runtime, null, hotReload: false);
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
    HostKey Key(string framework, string assembly, string? runSettings, bool debug) => new(framework, RuntimeVersion(assembly), runSettings,
        Hash(Path.ChangeExtension(assembly, ".deps.json")) + (runSettings is null ? "" : " " + Hash(runSettings)), debug && Engine is not null);

    /// <summary>
    /// The idle host prepared for a test project, if it was prepared from what the project has now; otherwise
    /// the spare on its runtime, replaced in the background, or a new host: a host started for no project
    /// loads everything when the run starts. Only a host prepared for the project can take updates after it.
    /// <paramref name="replacing"/> a live host whose edit didn't hot reload, a project that can hot reload takes
    /// a new host started for it, not the spare, so that the host stays up for the next edit.
    /// </summary>
    (Process Host, bool HotReload) TakeHost(string assembly, HostKey key, bool replacing)
    {
        IdleHost? idle;
        lock (_idleHosts)
            _idleHosts.Remove(assembly, out idle);
        if (idle is not null && idle.Key == key && !idle.Process.HasExited)
            return (idle.Process, key.HotReload);
        if (idle is not null)
            Stop(idle.Process);
        if (replacing && key.HotReload)
            return (StartHost(key.Runtime, (key.Framework, assembly, key.RunSettings, WarmUp: null), hotReload: true), true);
        Process? spare;
        lock (_idleHosts)
            _spares.Remove(key.Runtime, out spare);
        if (spare is null)
            return (StartHost(key.Runtime, null, hotReload: false), false);
        _ = Task.Run(RefillSpares);
        if (!spare.HasExited)
            return (spare, false);
        spare.Dispose();
        return (StartHost(key.Runtime, null, hotReload: false), false);
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

    /// <summary>The daemon's current directory as it started, which every host starts in.</summary>
    readonly string _workingDirectory = Environment.CurrentDirectory;

    /// <summary>
    /// Starts a host on a runtime; for a project (its framework, assembly, .runsettings and warm-up assembly),
    /// one that gets the project's run ready while it waits.
    /// </summary>
    Process StartHost(string runtime, (string Framework, string Assembly, string? RunSettings, string? WarmUp)? project, bool hotReload)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            // Hosts start beside a command, and MSBuild's in-process node moves the daemon's current directory
            // while it builds: each host gets the one the daemon started in, as it always has.
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        }.WithoutMSBuildEnvironment();
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
        foreach (var variable in new[] { "DOTNET_gcServer", "DOTNET_GCConserveMemory", "DOTNET_TieredPGO", "DOTNET_TC_CallCountingDelayMs" })
            start.Environment.Remove(variable);
        // DOTNET_MODIFIABLE_ASSEMBLIES=debug lets a host take updates (Protocol.cs). It makes the first run in a
        // host slower (on the fixture 50 to 120 ms, with the same methods compiled), so only hosts prepared for a
        // Debug project, which stay up after their run to take the next edit, get it.
        if (hotReload)
            start.Environment[TestHostProtocol.ModifiableAssemblies] = "debug";
        else
            start.Environment.Remove(TestHostProtocol.ModifiableAssemblies);
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
        List<LiveHost> live;
        lock (_idleHosts)
        {
            _disposed = true;
            idle = [.. _idleHosts.Values.Select(host => host.Process), .. _spares.Values];
            live = [.. _live.Values];
            _idleHosts.Clear();
            _spares.Clear();
            _live.Clear();
        }
        Task stopping;
        lock (_idleHosts)
            stopping = _stopping;
        stopping.Wait();
        foreach (var host in idle)
            Stop(host);
        foreach (var host in live)
            End(host);
        // A refill holds it while it compiles a warm-up assembly or starts a host.
        lock (_refilling)
        {
            foreach (var file in _hostConfigs.Values.Concat(_warmUpFiles))
                File.Delete(file);
        }
        try { Directory.Delete(SnapshotRoot, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // none, or the engine still has one open (Windows)
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
