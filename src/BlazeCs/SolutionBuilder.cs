using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace BlazeCs;

/// <summary>
/// A solution held compiled in memory. A build refreshes every unit from disk, recompiles the ones whose
/// sources changed and, in dependency order, the ones whose references' public API changed, then writes
/// the same files `dotnet build` would. Anything it can't do alone goes through a real `dotnet build`.
/// </summary>
sealed class SolutionBuilder
{
    readonly string _solutionPath;
    readonly string _solutionDirectory;
    readonly string _stateDirectory;
    readonly string _sdkDirectory;
    readonly GeneratorCache _generators;
    readonly ConcurrentDictionary<(string, MetadataReferenceProperties), MetadataReference> _external = new();

    List<ProjectCompiler> _units = [];
    Dictionary<ProjectCompiler, List<ProjectCompiler>> _dependencies = [];
    Dictionary<ProjectCompiler, List<ProjectCompiler>> _dependents = [];
    Dictionary<string, HashSet<string>> _copies = new(PathComparer.Instance);
    Dictionary<string, string> _imagePaths = new(PathComparer.Instance);
    Dictionary<string, ProjectCompiler> _producers = new(PathComparer.Instance);
    Dictionary<string, Image> _images = new(PathComparer.Instance);
    HashSet<ProjectCompiler> _analyzerUnits = [];
    Dictionary<string, DateTime> _msbuildInputs = new(PathComparer.Instance);
    /// <summary>
    /// Units the last build left failed (null: compile) or blocked (the work they were blocked with): their
    /// outputs are stale until they run again.
    /// </summary>
    Dictionary<ProjectCompiler, ApiChange?> _retry = [];

    public SolutionBuilder(string solutionPath, string sdkDirectory)
    {
        _solutionPath = solutionPath;
        _solutionDirectory = Path.GetDirectoryName(solutionPath)!;
        _stateDirectory = Path.Combine(_solutionDirectory, ".blaze-cs");
        _sdkDirectory = sdkDirectory;
        Directory.CreateDirectory(_stateDirectory);
        _generators = new GeneratorCache(Path.Combine(_stateDirectory, "shadow"));
    }

    public IReadOnlyList<ProjectCompiler> Units => _units;

    UnitsFile UnitsCache => new(Path.Combine(_stateDirectory, "units.json"));

    /// <summary>
    /// Runs `dotnet build` and loads what it did. csc only logs its arguments when it runs, so with no
    /// cached arguments the first build compiles everything; later ones are incremental. A unit already
    /// held whose arguments didn't change stays as it is, with its parsed sources and generator caches;
    /// only the others are built from the arguments, and every unit takes the images the build wrote.
    /// </summary>
    /// <param name="fresh">Ignore the cached arguments and compile everything.</param>
    /// <param name="warmUpLater">
    /// Return once loaded and leave the warm-up running: the next build waits for it (see <see cref="Settle"/>).
    /// </param>
    public bool Load(Action<string> log, bool fresh = false, bool warmUpLater = false)
    {
        var cached = fresh ? [] : UnitsCache.Read();
        var binlog = Path.Combine(_stateDirectory, "build.binlog");
        var arguments = $"build \"{_solutionPath}\" -nologo -v:q -clp:ErrorsOnly -bl:\"{binlog}\"" + (cached.Count == 0 ? " --no-incremental" : "");
        if (RunDotnet(arguments, log) != 0)
            return false;
        _retry = [];

        var stopwatch = Stopwatch.StartNew();
        var info = BuildLog.Read(binlog, _sdkDirectory);
        var read = stopwatch.ElapsedMilliseconds;
        // An incremental build logs csc only for the projects it compiled, so the cache holds the rest. One
        // the build didn't run CoreCompile for any more (removed from the solution, a target framework
        // dropped) is no unit: it is dropped here, before anything is read for it.
        var units = cached.Where(u => info.Compiled.Contains(u.Key)).ToDictionary(u => u.AssemblyPath, PathComparer.Instance);
        foreach (var unit in info.Units)
            units[unit.AssemblyPath] = unit;

        // A project the build did run CoreCompile for but csc not (it was removed and is back, with its
        // obj/ up to date) has no arguments yet: compile everything once to log them all.
        var keys = units.Values.Select(u => u.Key).ToHashSet(PathComparer.Instance);
        if (!fresh && info.Compiled.FirstOrDefault(key => !keys.Contains(key)) is { } missing)
        {
            log($"blaze-cs: {missing[(missing.IndexOf('|') + 1)..]} has no cached compiler arguments; running dotnet build --no-incremental");
            return Load(log, fresh: true);
        }
        UnitsCache.Write(units.Values);

        _copies = new(PathComparer.Instance);
        foreach (var (from, to) in info.Copies)
        {
            if (from != to)
                (_copies.TryGetValue(from, out var set) ? set : _copies[from] = new(PathComparer.Instance)).Add(to);
        }

        // Every copy of a unit's assembly or reference assembly stands for the unit: dependents compile
        // against its image (the reference assembly when there is one; both carry the same public API).
        _imagePaths = new(PathComparer.Instance);
        foreach (var unit in units.Values)
        {
            var image = unit.ReferenceAssemblyPath ?? unit.AssemblyPath;
            foreach (var path in Closure(unit.AssemblyPath).Concat(unit.ReferenceAssemblyPath is { } r ? Closure(r) : []))
                _imagePaths[path] = image;
        }

        // The images as the build left them. One with the same bytes keeps its object, and with it the
        // MetadataReferences the units compiled against it hold, so those units see no reference change.
        _images = units.Values.AsParallel()
            .Select(unit => unit.ReferenceAssemblyPath ?? unit.AssemblyPath)
            .Select(path => (Path: path, Image: Image.Read(path, _images.GetValueOrDefault(path))))
            .ToDictionary(i => i.Path, i => i.Image, PathComparer.Instance);

        var held = _units.ToDictionary(c => c.Unit.AssemblyPath, PathComparer.Instance);
        var compilers = units.Values.AsParallel()
            .Select(unit => held.TryGetValue(unit.AssemblyPath, out var kept) && kept.Matches(unit)
                ? kept
                : new ProjectCompiler(unit, ResolveReference, _generators))
            .ToList();
        var byImage = compilers.ToDictionary(c => c.ImagePath, PathComparer.Instance);
        _producers = _imagePaths.ToDictionary(p => p.Key, p => byImage[p.Value], PathComparer.Instance);
        _dependencies = compilers.ToDictionary(c => c, c => c.References
            .Select(r => _producers.GetValueOrDefault(r.Reference))
            .OfType<ProjectCompiler>()
            .Distinct()
            .ToList());
        _dependents = compilers.ToDictionary(c => c, c => compilers.Where(d => _dependencies[d].Contains(c)).ToList());
        _units = TopologicalOrder(compilers);
        _analyzerUnits = compilers.SelectMany(c => c.AnalyzerPaths)
            .Select(path => _producers.GetValueOrDefault(path))
            .OfType<ProjectCompiler>()
            .ToHashSet();
        _msbuildInputs = MSBuildInputs();
        foreach (var gone in _surfaces.Keys.Concat(_lastDuration.Keys).Except(compilers).ToList())
        {
            _surfaces.TryRemove(gone, out _);
            _lastDuration.TryRemove(gone, out _);
        }
        // The files the daemon wrote or verified for a unit that left the solution.
        foreach (var gone in held.Values.Except(compilers))
        {
            foreach (var path in new[] { gone.AssemblyPath, gone.PdbPath, gone.ReferenceAssemblyPath }.OfType<string>())
                _published.TryRemove(path, out _);
        }

        // A kept unit needs no compile to be trusted while its assembly on disk is still the bytes the
        // daemon last emitted for it: whatever the build did, the unit reproduces that file. One whose
        // inputs, references or file changed compiles and is compared, like a new unit.
        var kept = held.Values.Intersect(compilers).ToHashSet();
        bool NeedsCompile(ProjectCompiler unit) =>
            !kept.Contains(unit)
            || (unit.Realign() | unit.RefreshReferences()) // both run; each brings the unit up to date
            || !(_published.TryGetValue(unit.AssemblyPath, out var last) && Holds(unit.AssemblyPath, last.Bytes));
        var verify = _units.AsParallel().Where(NeedsCompile).ToList();

        log($"blaze-cs: loaded {_units.Count} projects in {stopwatch.ElapsedMilliseconds} ms (build log {read} ms"
            + (kept.Count > 0 ? $"; {kept.Count} kept warm)" : ")"));
        var logged = info.Units.Select(u => u.AssemblyPath).ToHashSet(PathComparer.Instance);
        if (warmUpLater)
        {
            // Its lines go to the next command, which reports them as this one would have.
            log("blaze-cs: warming up in the background; the next command waits for it");
            _warmUp = Task.Run(() =>
            {
                var lines = new List<string>();
                var stale = StaleAfterWarmUp(lines.Add, verify, logged, fresh);
                foreach (var line in lines)
                    Console.WriteLine($"{DateTime.Now:O} {line}");
                return new WarmedUp(lines, stale);
            });
            return true;
        }
        return !StaleAfterWarmUp(log, verify, logged, fresh) || Load(log, fresh: true);
    }

    /// <summary>The first load's warm-up, which runs on after the command that started the daemon returns.</summary>
    Task<WarmedUp>? _warmUp;

    /// <summary>A warm-up's report, and whether it found cached arguments stale.</summary>
    sealed record WarmedUp(List<string> Lines, bool Stale);

    /// <summary>The load that warm-up called for, run by each command until it succeeds, as the first load is.</summary>
    Func<Action<string>, bool>? _reload;

    /// <summary>
    /// Waits for the warm-up the first load left running and reports it as part of this command, then runs
    /// the load it calls for: with fresh arguments if it found them stale, or again if it failed. Nothing
    /// else touches the units until it is done.
    /// </summary>
    bool Settle(Action<string> log)
    {
        if (_warmUp is { } warmUp)
        {
            _warmUp = null;
            try
            {
                var warmedUp = warmUp.GetAwaiter().GetResult();
                foreach (var line in warmedUp.Lines)
                    log(line);
                if (warmedUp.Stale)
                    _reload = log => Load(log, fresh: true);
            }
            catch (Exception e)
            {
                // Units it didn't get to verify have nothing on record, so the load compiles them again.
                Console.WriteLine($"{DateTime.Now:O} {e}");
                log($"blaze-cs: internal error in the warm-up: {e.Message} (details in .blaze-cs/daemon.log); running dotnet build");
                _reload = log => Load(log);
            }
        }
        if (_reload is not { } reload)
            return true;
        if (!reload(log))
            return false;
        _reload = null;
        return true;
    }

    /// <summary>
    /// Warms up these units, then checks their arguments. A unit this build didn't compile keeps the
    /// arguments csc last ran with under the daemon. If a build outside it (an IDE's, `dotnet build`)
    /// compiled the project since, with other sources, they are stale and the daemon can't reproduce the
    /// assembly: the solution must load again with everything compiled once, to log them all.
    /// </summary>
    /// <returns>Whether the solution must load again with fresh arguments.</returns>
    bool StaleAfterWarmUp(Action<string> log, IReadOnlyCollection<ProjectCompiler> units, HashSet<string> logged, bool fresh)
    {
        var mismatched = WarmUp(log, units);
        // The build log's tree and the images just read are garbage now, hundreds of MB per load. The
        // server GC on many cores lets that pile up over several loads (jellyfin: 1.7 GB to 3.1 GB RSS)
        // before it collects; collected here, after a step MSBuild or the warm-up already made slow, it
        // costs ~0.1 s.
        GC.Collect();
        if (fresh || mismatched.FirstOrDefault(u => !logged.Contains(u.Unit.AssemblyPath)) is not { } stale)
            return false;
        log($"blaze-cs: {stale.Unit.Name}: its cached compiler arguments are stale; running dotnet build --no-incremental");
        return true;
    }

    /// <summary>
    /// Compiles these units once into memory: JITs the compiler, fills the generator caches, and checks
    /// that the daemon's output is byte-for-byte what `dotnet build` just wrote. The other units are
    /// kept warm and already known to match.
    /// </summary>
    /// <returns>The units whose output differs.</returns>
    List<ProjectCompiler> WarmUp(Action<string> log, IReadOnlyCollection<ProjectCompiler> units)
    {
        var stopwatch = Stopwatch.StartNew();
        var mismatches = new ConcurrentDictionary<ProjectCompiler, string>();
        Parallel.ForEach(units, unit =>
        {
            var prepared = unit.Prepare();
            // The baselines the first edit is compared against.
            if (_dependents[unit].Count > 0)
                _surfaces[unit] = new ApiSurface(prepared.Compilation);
            unit.GeneratedChanged(prepared);
            ProjectCompiler.IndexIdentifiers(prepared.Compilation);
            var (output, diagnostics) = unit.Emit(prepared);
            if (output is null)
                mismatches[unit] = $"{unit.Unit.Name}: {diagnostics.First(d => d.Severity == DiagnosticSeverity.Error)}";
            else if (!Holds(unit.AssemblyPath, output.Assembly))
                mismatches[unit] = $"{unit.Unit.Name}: emitted assembly differs from {unit.AssemblyPath}";
        });
        log($"blaze-cs: warmed up in {stopwatch.ElapsedMilliseconds} ms; {_units.Count - mismatches.Count}/{_units.Count} assemblies identical to dotnet build"
            + (units.Count < _units.Count ? $" ({units.Count} compiled)" : ""));
        foreach (var mismatch in mismatches.Values.Order())
            log($"blaze-cs: warning: {mismatch}");
        return [.. mismatches.Keys];
    }

    public bool Build(Action<string> log)
    {
        if (!Settle(log))
            return new BuildResult(false, []);
        var stopwatch = Stopwatch.StartNew();
        if (ChangedMSBuildInput() is { } input)
        {
            log($"blaze-cs: {Path.GetRelativePath(_solutionDirectory, input)} changed, running dotnet build");
            return Load(log);
        }

        var changed = _units.AsParallel().Where(u => u.Refresh()).ToHashSet();
        var scanned = stopwatch.ElapsedMilliseconds;
        if (changed.FirstOrDefault(u => u.Unit.SlowPathReason is not null || u.StaleArguments is not null || _analyzerUnits.Contains(u)) is { } special)
        {
            log($"blaze-cs: {special.Unit.Name}: {special.Unit.SlowPathReason ?? special.StaleArguments ?? "it is an analyzer other projects load"}; running dotnet build");
            return Load(log);
        }

        // What the last build left failed or blocked runs again with the work it had, so a build fails the
        // same way until the cause is fixed, and writes the stale outputs once it is.
        var work = new Dictionary<ProjectCompiler, ApiChange?>(_retry);
        foreach (var unit in changed)
            work[unit] = null;

        // Dataflow: every unit compiles as soon as its inputs are known, against the images its references
        // have at that moment, and runs once more if a reference's API changes mid-compile. A dependent of
        // an API change first checks whether it can be affected at all (ApiSurface) and skips if not.
        var run = new BuildRun(this);
        foreach (var (unit, change) in work.OrderByDescending(w => LastDuration(w.Key)))
            run.Schedule(unit, change);
        run.Wait();

        var output = run.Diagnostics.SelectMany(r => r.Value
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d => $"{d} [{r.Key.Unit.ProjectFile}]"));
        foreach (var line in output.Order())
            log(line);
        var rebuilt = run.Diagnostics.Keys.ToList();
        _retry = new(run.Blocked);
        foreach (var unit in run.Failed.Keys)
            _retry[unit] = null;
        if (_retry.Count > 0)
        {
            if (run.Blocked.Count > 0)
                log($"blaze-cs: blocked by a failed reference: {string.Join(", ", run.Blocked.Keys.Select(u => u.Unit.Name).Order())}");
            log($"blaze-cs: build failed in {stopwatch.ElapsedMilliseconds} ms ({run.Failed.Count} project(s) failed, {run.Blocked.Count} blocked)");
            return false;
        }
        var times = run.Times;
        var unaffected = run.Unaffected.Count == 0 ? "" : $"; {run.Unaffected.Count} dependents checked and unaffected";
        log(rebuilt.Count == 0
            ? $"blaze-cs: up to date ({stopwatch.ElapsedMilliseconds} ms)"
            : $"blaze-cs: rebuilt {string.Join(", ", rebuilt.Select(u => u.Unit.Name).Order())} in {stopwatch.ElapsedMilliseconds} ms "
              + $"(scan {scanned} ms; {run.Compiles} compiles; summed over projects: compile {times.Values.Sum(t => t.Compile.TotalMilliseconds):F0} ms, "
              + $"write {times.Values.Sum(t => t.Write.TotalMilliseconds):F0} ms{unaffected})");
        if (times.Count > 1)
            log("blaze-cs: slowest: " + string.Join(", ", times.OrderByDescending(t => t.Value.Compile).Take(5)
                .Select(t => $"{t.Key.Unit.Name} {t.Value.Compile.TotalMilliseconds:F0} ms (generators {t.Value.Generators.TotalMilliseconds:F0})")));
        return true;
    }

    readonly ConcurrentDictionary<ProjectCompiler, TimeSpan> _lastDuration = new();

    TimeSpan LastDuration(ProjectCompiler unit) => _lastDuration.GetValueOrDefault(unit);

    /// <summary>One build's compiles, scheduled as their inputs become ready.</summary>
    sealed class BuildRun(SolutionBuilder builder)
    {
        readonly ConcurrentDictionary<ProjectCompiler, Slot> _slots = new();
        // Slowest first, a few at a time: Roslyn parallelizes inside each compile, so the long compiles
        // that decide the wall time get more cores than they would sharing with every small project.
        readonly PriorityQueue<ProjectCompiler, TimeSpan> _ready = new();
        readonly int _limit = Math.Max(2, Environment.ProcessorCount / 4);
        readonly TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _running;
        int _outstanding;
        int _compiles;

        public ConcurrentDictionary<ProjectCompiler, ImmutableArray<Diagnostic>> Diagnostics { get; } = new();
        public ConcurrentDictionary<ProjectCompiler, (TimeSpan Compile, TimeSpan Generators, TimeSpan Write)> Times { get; } = new();
        /// <summary>Units whose compile or write failed; their diagnostics say why.</summary>
        public ConcurrentDictionary<ProjectCompiler, bool> Failed { get; } = new();
        /// <summary>Units held back because a reference failed or is blocked, with their work, after the build.</summary>
        public IReadOnlyDictionary<ProjectCompiler, ApiChange?> Blocked => _blocked;
        /// <summary>Dependents of an API change that were checked and compile to the same bytes.</summary>
        public ConcurrentDictionary<ProjectCompiler, bool> Unaffected { get; } = new();
        public int Compiles => _compiles;

        /// <summary>A unit's pending work: forced (its own sources changed), or API changes to check against.</summary>
        sealed class Slot
        {
            public bool Running;
            public bool Forced;
            public ApiChange? Change;

            public bool HasWork => Forced || Change is not null;
        }

        /// <param name="change">Null when the unit's own inputs changed and it must compile.</param>
        public void Schedule(ProjectCompiler unit, ApiChange? change = null)
        {
            var slot = _slots.GetOrAdd(unit, _ => new Slot());
            lock (slot)
            {
                if (change is null)
                    slot.Forced = true;
                else
                    slot.Change = slot.Change is null ? change : slot.Change.Union(change);
                if (slot.Running)
                    return;
                slot.Running = true;
            }
            Interlocked.Increment(ref _outstanding);
            lock (_ready)
                _ready.Enqueue(unit, -builder.LastDuration(unit));
            Pump();
        }

        void Pump()
        {
            lock (_ready)
            {
                while (_running < _limit && _ready.TryDequeue(out var unit, out _))
                {
                    _running++;
                    ThreadPool.QueueUserWorkItem(_ => Loop(unit, _slots[unit]));
                }
            }
        }

        public void Wait()
        {
            if (Volatile.Read(ref _outstanding) > 0)
                _idle.Task.GetAwaiter().GetResult();
        }

        void Loop(ProjectCompiler unit, Slot slot)
        {
            try
            {
                while (true)
                {
                    bool forced;
                    ApiChange? change;
                    lock (slot)
                    {
                        if (!slot.HasWork)
                        {
                            slot.Running = false;
                            break;
                        }
                        (forced, change) = (slot.Forced, slot.Change);
                        (slot.Forced, slot.Change) = (false, null);
                    }
                    CompileOnce(unit, forced ? null : change);
                }
            }
            catch (Exception e)
            {
                Failed[unit] = true;
                Diagnostics[unit] = [Diagnostic.Create("BLAZE001", "blaze-cs", $"internal error: {e}", DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0)];
                lock (slot)
                    slot.Running = false;
            }
            finally
            {
                lock (_ready)
                    _running--;
                Pump();
                if (Interlocked.Decrement(ref _outstanding) == 0)
                    _idle.TrySetResult();
            }
        }

        void CompileOnce(ProjectCompiler unit, ApiChange? change)
        {
            lock (_blocked)
            {
                if (Blocks(unit))
                {
                    Block(unit, change);
                    return;
                }
                // A unit whose last compile failed has no output to keep: whatever changed may fix it.
                if (Failed.ContainsKey(unit))
                    change = null;
            }

            var started = Stopwatch.GetTimestamp();
            unit.RefreshReferences();
            var prepared = unit.Prepare();
            var generatedChanged = unit.GeneratedChanged(prepared);
            if (change is { Everything: false } && !generatedChanged && !ProjectCompiler.Mentions(prepared.Compilation, change.Names))
            {
                Unaffected[unit] = true;
                return;
            }
            Unaffected.TryRemove(unit, out _);
            Interlocked.Increment(ref _compiles);

            // Dependents need the new surface only if the image changes, but then they need it at once:
            // build it while the emit runs.
            var surface = builder._dependents[unit].Count > 0 ? Task.Run(() => new ApiSurface(prepared.Compilation)) : null;
            var (emitted, diagnostics) = unit.Emit(prepared);
            var compiled = Stopwatch.GetElapsedTime(started);
            builder._lastDuration[unit] = compiled;
            if (emitted is null)
            {
                Diagnostics[unit] = diagnostics;
                Failed[unit] = true;
                Times[unit] = (compiled, unit.LastGeneratorTime, TimeSpan.Zero);
                return;
            }

            // This unit may have compiled against a reference's image whose files are still being written;
            // it publishes nothing until they are, and if they fail it is blocked like a failed compile.
            foreach (var dependency in builder._dependencies[unit])
                _written.GetValueOrDefault(dependency)?.Wait();
            List<(ProjectCompiler Unit, ApiChange? Change)> unblocked;
            lock (_blocked)
            {
                if (Blocks(unit))
                {
                    Block(unit, change);
                    return;
                }
                Diagnostics[unit] = diagnostics;
                Failed.TryRemove(unit, out _);
                // The dependents this unit held back run again, with the work they were blocked with.
                unblocked = [.. builder._dependents[unit].Where(_blocked.ContainsKey).Select(d => (d, _blocked[d]))];
                foreach (var (dependent, _) in unblocked)
                    _blocked.Remove(dependent);
            }

            // Dependents start on the new image and API change while this unit's files are written.
            // Pending before the image is published, so no dependent that sees the image misses the wait.
            started = Stopwatch.GetTimestamp();
            var written = new TaskCompletionSource();
            _written[unit] = written.Task;
            try
            {
                try
                {
                    if (builder.PublishImage(unit, emitted) && surface is not null)
                        ScheduleDependents(unit, builder.SurfaceChange(unit, surface.Result));
                }
                finally
                {
                    foreach (var (dependent, blockedChange) in unblocked)
                        Schedule(dependent, blockedChange);
                }
                builder.Write(unit, emitted);
            }
            catch
            {
                Failed[unit] = true;
                throw;
            }
            finally
            {
                written.SetResult();
            }
            Times[unit] = (compiled, unit.LastGeneratorTime, Stopwatch.GetElapsedTime(started));
        }

        /// <summary>Each unit's latest output writes, done once its files are on disk or have failed.</summary>
        readonly ConcurrentDictionary<ProjectCompiler, Task> _written = new();

        /// <summary>
        /// Units held back by a failed or blocked reference, with the work they had (null: compile). Changed
        /// only under its own lock, like the moves out of <see cref="Failed"/>, so a unit that recovers sees
        /// every dependent it blocked and runs it again.
        /// </summary>
        readonly Dictionary<ProjectCompiler, ApiChange?> _blocked = [];

        bool Blocks(ProjectCompiler unit) => builder._dependencies[unit].Any(d => Failed.ContainsKey(d) || _blocked.ContainsKey(d));

        void Block(ProjectCompiler unit, ApiChange? change)
        {
            if (_blocked.TryGetValue(unit, out var saved))
                change = saved is null || change is null ? null : saved.Union(change);
            _blocked[unit] = change;
            Failed.TryRemove(unit, out _);
            // Errors from a compile against a reference that has since failed say nothing about this unit.
            Diagnostics.TryRemove(unit, out _);
        }

        void ScheduleDependents(ProjectCompiler unit, ApiChange change)
        {
            foreach (var dependent in builder._dependents[unit].OrderByDescending(builder.LastDuration))
                Schedule(dependent, change);
        }
    }

    readonly ConcurrentDictionary<ProjectCompiler, ApiSurface> _surfaces = new();

    /// <summary>What changed in a unit's API since its last image, which has just changed.</summary>
    ApiChange SurfaceChange(ProjectCompiler unit, ApiSurface surface)
    {
        var change = _surfaces.TryGetValue(unit, out var before) ? surface.ChangeFrom(before) : ApiChange.All;
        _surfaces[unit] = surface;
        return change;
    }

    /// <summary>Makes a unit's new image the one dependents compile against; true if its public API changed.</summary>
    bool PublishImage(ProjectCompiler unit, EmitOutput output)
    {
        // Dependents compile against exactly the bytes MSBuild would hand them: their PDBs record each
        // reference's MVID, timestamp and size, so any other image (even one that binds the same, like a
        // metadata-only emit) would make their output differ from `dotnet build`'s.
        var image = ImageBytes(unit, output);
        lock (_images)
        {
            if (_images.TryGetValue(unit.ImagePath, out var current) && current.Bytes.AsSpan().SequenceEqual(image))
                return false;
            _images[unit.ImagePath] = new Image(image);
        }
        return true;
    }

    static byte[] ImageBytes(ProjectCompiler unit, EmitOutput output) =>
        unit.ReferenceAssemblyPath is null ? output.Assembly : output.ReferenceAssembly!;

    /// <summary>Writes a unit's outputs where csc and MSBuild's copies would.</summary>
    void Write(ProjectCompiler unit, EmitOutput output)
    {
        Write(unit.AssemblyPath, output.Assembly);
        if (unit.PdbPath is { } pdb && output.Pdb is { } pdbBytes)
            Write(pdb, pdbBytes);
        if (unit.DocumentationPath is { } documentation && output.Documentation is { } documentationBytes)
            Write(Path.GetFullPath(documentation, unit.Unit.ProjectDirectory), documentationBytes);
        // Like MSBuild's CopyRefAssembly: an unchanged reference assembly is left untouched (Write skips a
        // file that holds its bytes), so nothing that compiles against it looks out of date.
        if (unit.ReferenceAssemblyPath is { } reference)
            Write(reference, ImageBytes(unit, output));
    }

    /// <summary>Writes an output and its copies, unless it already holds these bytes.</summary>
    void Write(string path, byte[] bytes)
    {
        if (Holds(path, bytes))
            return;
        void Replace(string target)
        {
            // Replace rather than overwrite: a running test host may have the old file mapped.
            var temporary = target + ".blaze-cs.tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, overwrite: true);
        }
        // The path itself last: while it holds the bytes, so does every copy, and a write that failed
        // partway is done again in full.
        Parallel.ForEach(Closure(path).Skip(1), Replace);
        Replace(path);
        var info = new FileInfo(path);
        _published[path] = new Published(bytes, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>
    /// Whether a file holds these bytes. The disk is only read when something else wrote the file since
    /// the daemon last wrote or verified it; if it holds them, that is remembered with the file's stamp.
    /// </summary>
    bool Holds(string path, byte[] bytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return false;
        if (_published.TryGetValue(path, out var last) && last.Length == info.Length && last.Modified == info.LastWriteTimeUtc)
            return last.Bytes.AsSpan().SequenceEqual(bytes);
        if (info.Length != bytes.Length || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            return false;
        _published[path] = new Published(bytes, info.Length, info.LastWriteTimeUtc);
        return true;
    }

    /// <summary>What the daemon last wrote to or verified in each file, with the file's stamp as of then.</summary>
    readonly ConcurrentDictionary<string, Published> _published = new(PathComparer.Instance);

    sealed record Published(byte[] Bytes, long Length, DateTime Modified);

    /// <summary>Where the build puts a unit's assembly for running: its copy in the project's bin/.</summary>
    public string InstalledPath(ProjectCompiler unit)
    {
        var bin = Path.Combine(unit.Unit.ProjectDirectory, "bin") + Path.DirectorySeparatorChar;
        return Closure(unit.AssemblyPath).FirstOrDefault(p => p.StartsWith(bin, PathComparer.Comparison)) ?? unit.AssemblyPath;
    }

    /// <summary>
    /// The solution's assemblies a test project runs with: its own, and every unit's that MSBuild copied
    /// next to it.
    /// </summary>
    public IEnumerable<string> RuntimeClosure(ProjectCompiler unit)
    {
        var directory = Path.GetDirectoryName(InstalledPath(unit));
        return _units.SelectMany(u => Closure(u.AssemblyPath))
            .Where(path => PathComparer.Instance.Equals(Path.GetDirectoryName(path), directory));
    }

    /// <summary>A path and every place MSBuild copies it to, transitively.</summary>
    IEnumerable<string> Closure(string path)
    {
        var seen = new HashSet<string>(PathComparer.Instance) { path };
        var pending = new Queue<string>([path]);
        while (pending.TryDequeue(out var current))
        {
            yield return current;
            foreach (var next in _copies.GetValueOrDefault(current) ?? [])
            {
                if (seen.Add(next))
                    pending.Enqueue(next);
            }
        }
    }

    MetadataReference ResolveReference(CommandLineReference reference)
    {
        if (!_imagePaths.TryGetValue(reference.Reference, out var imagePath))
            return _external.GetOrAdd((reference.Reference, reference.Properties),
                key => MetadataReference.CreateFromFile(key.Item1, key.Item2));

        // Another unit's output: compile against the image the daemon last produced, which starts as
        // the file the real build wrote.
        Image? image;
        lock (_images)
        {
            if (!_images.TryGetValue(imagePath, out image))
                _images[imagePath] = image = new Image(File.ReadAllBytes(imagePath));
        }
        return image.Reference(reference.Properties, reference.Reference);
    }

    /// <summary>A unit's current public surface, with one MetadataReference per way it is referenced.</summary>
    sealed class Image(byte[] bytes)
    {
        readonly ConcurrentDictionary<MetadataReferenceProperties, MetadataReference> _references = new();
        public byte[] Bytes => bytes;
        public MetadataReference Reference(MetadataReferenceProperties properties, string path) =>
            _references.GetOrAdd(properties, p => MetadataReference.CreateFromImage(bytes, p, filePath: path));

        /// <summary>The image on disk: <paramref name="current"/> itself if it still holds the same bytes.</summary>
        public static Image Read(string path, Image? current)
        {
            var bytes = File.ReadAllBytes(path);
            return current is not null && current.Bytes.AsSpan().SequenceEqual(bytes) ? current : new Image(bytes);
        }
    }

    List<ProjectCompiler> TopologicalOrder(List<ProjectCompiler> units)
    {
        var ordered = new List<ProjectCompiler>();
        var state = new Dictionary<ProjectCompiler, bool>();
        void Visit(ProjectCompiler unit)
        {
            if (state.TryGetValue(unit, out var done))
            {
                if (!done)
                    throw new InvalidOperationException($"Reference cycle through {unit.Unit.Name}");
                return;
            }
            state[unit] = false;
            foreach (var dependency in _dependencies[unit])
                Visit(dependency);
            state[unit] = true;
            ordered.Add(unit);
        }
        foreach (var unit in units.OrderBy(u => u.Unit.Name, StringComparer.Ordinal))
            Visit(unit);
        return ordered;
    }

    /// <summary>
    /// Files that decide csc's arguments: project files, MSBuild imports, config. A change to any of
    /// them makes the cached arguments stale. Scoped CSS too: MSBuild rewrites and bundles it into static
    /// web assets, and a new or deleted one changes a component's scope in the generated .editorconfig.
    /// </summary>
    Dictionary<string, DateTime> MSBuildInputs()
    {
        var inputs = new Dictionary<string, DateTime>(PathComparer.Instance);
        var pending = new Stack<string>([_solutionDirectory]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".csproj", StringComparison.Ordinal) || name.EndsWith(".props", StringComparison.Ordinal)
                    || name.EndsWith(".targets", StringComparison.Ordinal) || name.EndsWith(".editorconfig", StringComparison.Ordinal)
                    || name.EndsWith(".globalconfig", StringComparison.Ordinal) || name.EndsWith(".sln", StringComparison.Ordinal)
                    || name.EndsWith(".slnx", StringComparison.Ordinal)
                    || name.EndsWith(".razor.css", StringComparison.Ordinal) || name.EndsWith(".cshtml.css", StringComparison.Ordinal)
                    || name is "global.json" or "nuget.config" or "NuGet.Config" or "packages.lock.json")
                    inputs[file] = File.GetLastWriteTimeUtc(file);
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (!name.StartsWith('.') && name is not ("bin" or "obj" or "node_modules"))
                    pending.Push(child);
            }
        }
        return inputs;
    }

    string? ChangedMSBuildInput()
    {
        var current = MSBuildInputs();
        return current.Keys.Union(_msbuildInputs.Keys, PathComparer.Instance)
            .FirstOrDefault(path => current.GetValueOrDefault(path) != _msbuildInputs.GetValueOrDefault(path));
    }

    /// <summary>Runs the real `dotnet` with its output streamed to the client.</summary>
    public int RunDotnet(string arguments, Action<string> log)
    {
        var start = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = _solutionDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) log(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>csc arguments of every unit ever built, since an incremental build only logs the ones it ran.</summary>
    sealed class UnitsFile(string path)
    {
        public List<CompileUnit> Read() =>
            File.Exists(path) ? JsonSerializer.Deserialize<List<CompileUnit>>(File.ReadAllText(path)) ?? [] : [];

        public void Write(IEnumerable<CompileUnit> units) =>
            File.WriteAllText(path, JsonSerializer.Serialize(units.OrderBy(u => u.AssemblyPath, StringComparer.Ordinal)));
    }
}

/// <summary>
/// Source generators by analyzer path, shared by every unit; analyzers are skipped. Loaded once, and
/// again after the file changes: a generator project in the solution was built again.
/// </summary>
sealed class GeneratorCache(string shadowDirectory)
{
    readonly AnalyzerLoader _loader = new(shadowDirectory);
    readonly ConcurrentDictionary<string, (FileStamp Stamp, ImmutableArray<ISourceGenerator> Generators)> _byPath = new(PathComparer.Instance);

    public ImmutableArray<ISourceGenerator> For(IEnumerable<string> analyzerPaths) =>
        analyzerPaths.Distinct(PathComparer.Instance).SelectMany(path => Load(path)).ToImmutableArray();

    ImmutableArray<ISourceGenerator> Load(string path)
    {
        var stamp = FileStamp.Of(path);
        if (_byPath.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            return cached.Generators;
        var generators = new AnalyzerFileReference(path, _loader).GetGenerators(LanguageNames.CSharp);
        _byPath[path] = (stamp, generators);
        return generators;
    }
}
