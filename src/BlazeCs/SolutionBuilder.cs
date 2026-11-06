using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace BlazeCs;

/// <summary>Where a build's output went, so `test` knows which test projects it touched.</summary>
sealed record BuildResult(bool Success, IReadOnlyCollection<ProjectCompiler> Changed);

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
    /// cached arguments the first build compiles everything; later ones are incremental.
    /// </summary>
    /// <param name="fresh">Ignore the cached arguments and compile everything.</param>
    public bool Load(Action<string> log, bool fresh = false)
    {
        var cached = fresh ? [] : UnitsCache.Read();
        var binlog = Path.Combine(_stateDirectory, "build.binlog");
        var arguments = $"build \"{_solutionPath}\" -nologo -v:q -clp:ErrorsOnly -bl:\"{binlog}\"" + (cached.Count == 0 ? " --no-incremental" : "");
        if (RunDotnet(arguments, log) != 0)
            return false;

        var stopwatch = Stopwatch.StartNew();
        var info = BuildLog.Read(binlog, _sdkDirectory);
        var units = cached.ToDictionary(u => u.AssemblyPath, PathComparer.Instance);
        foreach (var unit in info.Units)
            units[unit.AssemblyPath] = unit;
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
        _images = new(PathComparer.Instance);

        var compilers = units.Values.AsParallel()
            .Select(unit => new ProjectCompiler(unit, ResolveReference, _generators))
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

        log($"blaze-cs: loaded {_units.Count} projects in {stopwatch.ElapsedMilliseconds} ms");
        var mismatched = WarmUp(log);

        // A unit this build didn't compile keeps the arguments csc last ran with under the daemon. If a
        // build outside it (an IDE's, `dotnet build`) compiled the project since, with other sources, they
        // are stale and the daemon can't reproduce the assembly: compile everything once to log them all.
        var logged = info.Units.Select(u => u.AssemblyPath).ToHashSet(PathComparer.Instance);
        if (!fresh && mismatched.FirstOrDefault(u => !logged.Contains(u.Unit.AssemblyPath)) is { } stale)
        {
            log($"blaze-cs: {stale.Unit.Name}: its cached compiler arguments are stale; running dotnet build --no-incremental");
            return Load(log, fresh: true);
        }
        return true;
    }

    /// <summary>
    /// Compiles every unit once into memory: JITs the compiler, fills the generator caches, and checks
    /// that the daemon's output is byte-for-byte what `dotnet build` just wrote.
    /// </summary>
    /// <returns>The units whose output differs.</returns>
    List<ProjectCompiler> WarmUp(Action<string> log)
    {
        var stopwatch = Stopwatch.StartNew();
        var mismatches = new ConcurrentDictionary<ProjectCompiler, string>();
        Parallel.ForEach(_units, unit =>
        {
            var prepared = unit.Prepare();
            // The baselines the first edit is compared against.
            if (_dependents[unit].Count > 0)
                _surfaces[unit] = new ApiSurface(prepared.Compilation);
            unit.GeneratedChanged(prepared);
            unit.IndexIdentifiers(prepared);
            var (output, diagnostics) = unit.Emit(prepared);
            if (output is null)
                mismatches[unit] = $"{unit.Unit.Name}: {diagnostics.First(d => d.Severity == DiagnosticSeverity.Error)}";
            else if (!output.Assembly.AsSpan().SequenceEqual(File.ReadAllBytes(unit.AssemblyPath)))
                mismatches[unit] = $"{unit.Unit.Name}: emitted assembly differs from {unit.AssemblyPath}";
        });
        log($"blaze-cs: warmed up in {stopwatch.ElapsedMilliseconds} ms; {_units.Count - mismatches.Count}/{_units.Count} assemblies identical to dotnet build");
        foreach (var mismatch in mismatches.Values.Order())
            log($"blaze-cs: warning: {mismatch}");
        return [.. mismatches.Keys];
    }

    public BuildResult Build(Action<string> log)
    {
        var stopwatch = Stopwatch.StartNew();
        if (ChangedMSBuildInput() is { } input)
        {
            log($"blaze-cs: {Path.GetRelativePath(_solutionDirectory, input)} changed, running dotnet build");
            return new BuildResult(Load(log), _units);
        }

        var changed = _units.AsParallel().Where(u => u.Refresh()).ToHashSet();
        var scanned = stopwatch.ElapsedMilliseconds;
        if (changed.FirstOrDefault(u => u.Unit.SlowPathReason is not null || u.StaleArguments is not null || _analyzerUnits.Contains(u)) is { } special)
        {
            log($"blaze-cs: {special.Unit.Name}: {special.Unit.SlowPathReason ?? special.StaleArguments ?? "it is an analyzer other projects load"}; running dotnet build");
            return new BuildResult(Load(log), Downstream(changed));
        }

        // Dataflow: every unit compiles as soon as its inputs are known, against the images its references
        // have at that moment, and runs once more if a reference's API changes mid-compile. A dependent of
        // an API change first checks whether it can be affected at all (ApiSurface) and skips if not.
        var run = new BuildRun(this);
        foreach (var unit in changed.OrderByDescending(LastDuration))
            run.Schedule(unit);
        run.Wait();

        var output = run.Diagnostics.SelectMany(r => r.Value
            .Where(d => d.Severity >= DiagnosticSeverity.Warning)
            .Select(d => $"{d} [{r.Key.Unit.ProjectFile}]"));
        foreach (var line in output.Order())
            log(line);
        var rebuilt = run.Diagnostics.Keys.ToList();
        if (!run.Failed.IsEmpty)
        {
            log($"blaze-cs: build failed in {stopwatch.ElapsedMilliseconds} ms ({run.Failed.Count} project(s) failed or blocked)");
            return new BuildResult(false, rebuilt);
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
        return new BuildResult(true, rebuilt);
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
        public ConcurrentDictionary<ProjectCompiler, bool> Failed { get; } = new();
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
            if (builder._dependencies[unit].Any(Failed.ContainsKey))
            {
                Failed[unit] = true;
                return;
            }

            var started = Stopwatch.GetTimestamp();
            unit.RefreshReferences();
            var prepared = unit.Prepare();
            var generatedChanged = unit.GeneratedChanged(prepared);
            if (change is { Everything: false } && !generatedChanged && !unit.Mentions(prepared, change.Names))
            {
                Unaffected[unit] = true;
                return;
            }
            Unaffected.TryRemove(unit, out _);
            Interlocked.Increment(ref _compiles);

            var (emitted, diagnostics) = unit.Emit(prepared);
            var compiled = Stopwatch.GetElapsedTime(started);
            builder._lastDuration[unit] = compiled;
            Diagnostics[unit] = diagnostics;
            if (emitted is null)
            {
                Failed[unit] = true;
                Times[unit] = (compiled, unit.LastGeneratorTime, TimeSpan.Zero);
                return;
            }
            Failed.TryRemove(unit, out _);

            started = Stopwatch.GetTimestamp();
            if (builder.Publish(unit, emitted) && builder._dependents[unit].Count > 0)
                ScheduleDependents(unit, builder.SurfaceChange(unit, prepared));
            Times[unit] = (compiled, unit.LastGeneratorTime, Stopwatch.GetElapsedTime(started));
        }

        void ScheduleDependents(ProjectCompiler unit, ApiChange change)
        {
            foreach (var dependent in builder._dependents[unit].OrderByDescending(builder.LastDuration))
                Schedule(dependent, change);
        }
    }

    readonly ConcurrentDictionary<ProjectCompiler, ApiSurface> _surfaces = new();

    /// <summary>
    /// What changed in a unit's API since its last image. The image bytes are the ground truth: if they
    /// changed but the surface shows nothing, something it doesn't model changed and every dependent
    /// recompiles.
    /// </summary>
    ApiChange SurfaceChange(ProjectCompiler unit, ProjectCompiler.Prepared prepared)
    {
        var surface = new ApiSurface(prepared.Compilation);
        var change = _surfaces.TryGetValue(unit, out var before) ? surface.ChangeFrom(before) : ApiChange.All;
        _surfaces[unit] = surface;
        return change is { Everything: false, Names.IsEmpty: true } ? ApiChange.All : change;
    }

    /// <summary>Writes a unit's outputs where csc and MSBuild's copies would; true if its public API changed.</summary>
    bool Publish(ProjectCompiler unit, EmitOutput output)
    {
        Write(unit.AssemblyPath, output.Assembly);
        if (unit.PdbPath is { } pdb && output.Pdb is { } pdbBytes)
            Write(pdb, pdbBytes);
        if (unit.DocumentationPath is { } documentation && output.Documentation is { } documentationBytes)
            Write(Path.GetFullPath(documentation, unit.Unit.ProjectDirectory), documentationBytes);

        // Dependents compile against exactly the bytes MSBuild would hand them: their PDBs record each
        // reference's MVID, timestamp and size, so any other image (even one that binds the same, like a
        // metadata-only emit) would make their output differ from `dotnet build`'s.
        var image = unit.ReferenceAssemblyPath is null ? output.Assembly : output.ReferenceAssembly!;
        lock (_images)
        {
            if (_images.TryGetValue(unit.ImagePath, out var current) && current.Bytes.AsSpan().SequenceEqual(image))
                return false;
            _images[unit.ImagePath] = new Image(image);
        }
        // Like MSBuild's CopyRefAssembly: an unchanged reference assembly is left untouched, so nothing
        // that compiles against it looks out of date.
        if (unit.ReferenceAssemblyPath is { } reference)
            Write(reference, image);
        return true;
    }

    /// <summary>
    /// Writes an output and its copies, unless it already holds these bytes. What the daemon last wrote
    /// is remembered with the file's stamp, so the disk is only read when something else wrote it since.
    /// </summary>
    void Write(string path, byte[] bytes)
    {
        var info = new FileInfo(path);
        var unchanged = _published.TryGetValue(path, out var last) && last.Length == info.Length
            && last.Modified == (info.Exists ? info.LastWriteTimeUtc : default)
                ? last.Bytes.AsSpan().SequenceEqual(bytes)
                : info.Exists && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
        if (!unchanged)
        {
            Parallel.ForEach(Closure(path), target =>
            {
                // Replace rather than overwrite: a running test host may have the old file mapped.
                var temporary = target + ".blaze-cs.tmp";
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, target, overwrite: true);
            });
            info.Refresh();
        }
        _published[path] = new Published(bytes, info.Length, info.LastWriteTimeUtc);
    }

    readonly ConcurrentDictionary<string, Published> _published = new(PathComparer.Instance);

    sealed record Published(byte[] Bytes, long Length, DateTime Modified);

    /// <summary>Where the build puts a unit's assembly for running: its copy in the project's bin/.</summary>
    public string InstalledPath(ProjectCompiler unit)
    {
        var bin = Path.Combine(unit.Unit.ProjectDirectory, "bin") + Path.DirectorySeparatorChar;
        return Closure(unit.AssemblyPath).FirstOrDefault(p => p.StartsWith(bin, PathComparer.Comparison)) ?? unit.AssemblyPath;
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
    }

    /// <summary>Every unit that compiles against one of these, directly or not, and the units themselves.</summary>
    public IReadOnlyCollection<ProjectCompiler> Downstream(IEnumerable<ProjectCompiler> units)
    {
        var result = units.ToHashSet();
        foreach (var unit in _units)
        {
            if (_dependencies[unit].Any(result.Contains))
                result.Add(unit);
        }
        return result;
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
