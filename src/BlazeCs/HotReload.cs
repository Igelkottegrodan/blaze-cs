using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace BlazeCs;

/// <summary>
/// Roslyn's Edit and Continue engine, as dotnet-watch drives it: the Features, Workspaces and
/// ExternalAccess.HotReload assemblies the solution's SDK ships next to dotnet-watch, the same Roslyn build
/// as the compiler the daemon loads from it. Its entry point, HotReloadService, is internal, so this shim
/// finds it and its members by reflection, after checking the assemblies are that build; anything it
/// doesn't find makes hot reload unavailable, and `blaze-cs test` runs as it does without it.
/// </summary>
sealed class HotReloadEngine
{
    const string ServiceType = "Microsoft.CodeAnalysis.ExternalAccess.HotReload.Api.HotReloadService";

    /// <summary>The engine's assemblies, all in dotnet-watch's directory.</summary>
    static readonly string[] Assemblies =
    [
        "Microsoft.CodeAnalysis.Workspaces", "Microsoft.CodeAnalysis.CSharp.Workspaces",
        "Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features",
        "Microsoft.CodeAnalysis.ExternalAccess.HotReload",
    ];

    /// <summary>
    /// What the runtime may apply, as dotnet-watch's agent reports it: "Baseline" is method body edits only,
    /// so the engine turns every other edit into a rude edit, which takes today's path.
    /// </summary>
    static readonly ImmutableArray<string> Capabilities = ["Baseline"];

    /// <summary>
    /// BLAZE_CS_DOTNET_WATCH=&lt;directory&gt; looks for the engine there instead of in the SDK, so the fixture
    /// check can see what happens when the SDK lacks it.
    /// </summary>
    const string DirectoryOverride = "BLAZE_CS_DOTNET_WATCH";

    readonly ConstructorInfo _create;
    readonly MethodInfo _start, _getUpdates, _commit, _discard, _end;
    readonly PropertyInfo _status, _projectUpdates, _transientDiagnostics, _persistentDiagnostics, _projectsToRestart, _projectsToRebuild;
    readonly FieldInfo _module, _project, _il, _metadata, _pdb, _updatedTypes;
    readonly Type _runningProject;
    readonly Lazy<Workspace> _workspace;

    HotReloadEngine(Type service, Assembly hotReload)
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type Nested(string name) => service.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic) ?? throw Missing($"{service.Name}.{name}");
        MethodInfo Method(string name, Type returns, params Type[] parameters) =>
            service.GetMethod(name, Instance, parameters) is { } method && method.ReturnType == returns ? method
            : throw Missing($"{returns.Name} {name}({string.Join(", ", parameters.Select(p => p.Name))})");

        var updates = Nested("Updates");
        var update = Nested("Update");
        _runningProject = Nested("RunningProjectInfo");
        var running = typeof(ImmutableDictionary<,>).MakeGenericType(typeof(ProjectId), _runningProject);
        _create = service.GetConstructor(Instance, [typeof(Microsoft.CodeAnalysis.Host.SolutionServices), typeof(Func<ValueTask<ImmutableArray<string>>>)])
            ?? throw Missing("its constructor (SolutionServices, capabilities)");
        _start = Method("StartSessionAsync", typeof(Task), typeof(Solution), typeof(CancellationToken));
        _getUpdates = Method("GetUpdatesAsync", typeof(Task<>).MakeGenericType(updates), typeof(Solution), running, typeof(CancellationToken));
        _commit = Method("CommitUpdate", typeof(void));
        _discard = Method("DiscardUpdate", typeof(void));
        _end = Method("EndSession", typeof(void));

        PropertyInfo Property(Type type, string name, Type? exactly = null) =>
            type.GetProperty(name, Instance) is { } property && (exactly is null || property.PropertyType == exactly) ? property : throw Missing($"{type.Name}.{name}");
        _status = Property(updates, "Status");
        _projectUpdates = Property(updates, "ProjectUpdates", typeof(ImmutableArray<>).MakeGenericType(update));
        _transientDiagnostics = Property(updates, "TransientDiagnostics");
        _persistentDiagnostics = Property(updates, "PersistentDiagnostics", typeof(ImmutableArray<Diagnostic>));
        _projectsToRestart = Property(updates, "ProjectsToRestart");
        _projectsToRebuild = Property(updates, "ProjectsToRebuild", typeof(ImmutableArray<ProjectId>));
        if (!Enum.GetNames(_status.PropertyType).ToHashSet().IsSupersetOf(["NoChangesToApply", "ReadyToApply", "Blocked"]))
            throw Missing("Status.ReadyToApply");

        FieldInfo Field(string name, Type type) =>
            update.GetField(name, Instance) is { } field && field.FieldType == type ? field : throw Missing($"Update.{name}");
        _module = Field("ModuleId", typeof(Guid));
        _project = Field("ProjectId", typeof(ProjectId));
        _il = Field("ILDelta", typeof(ImmutableArray<byte>));
        _metadata = Field("MetadataDelta", typeof(ImmutableArray<byte>));
        _pdb = Field("PdbDelta", typeof(ImmutableArray<byte>));
        _updatedTypes = Field("UpdatedTypes", typeof(ImmutableArray<int>));
        if (!_runningProject.IsValueType && _runningProject.GetConstructor(Type.EmptyTypes) is null)
            throw Missing("RunningProjectInfo()");

        // Composing the engine's services (MEF) takes a while, so the first session does it, not the load.
        _workspace = new(() => new AdhocWorkspace(MefHostServices.Create(
            Assemblies.Select(name => AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(name))))));
    }

    static MissingMemberException Missing(string what) => new($"HotReloadService has no {what}");

    /// <summary>
    /// Loads the engine from the SDK, or says why hot reload is unavailable. Must run after SdkRoslyn.Install,
    /// before anything touches a Workspaces type.
    /// </summary>
    public static HotReloadEngine? Load(string sdkDirectory, out string? unavailable)
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryOverride) is { Length: > 0 } given ? given : WatchDirectory(sdkDirectory);
        if (directory is null)
        {
            var watch = Path.Combine(sdkDirectory, "DotnetTools", "dotnet-watch");
            unavailable = Directory.Exists(watch)
                ? $"the SDK's dotnet-watch has no Microsoft.CodeAnalysis.ExternalAccess.HotReload.dll in {watch}"
                : $"the SDK has no dotnet-watch in {Path.Combine(sdkDirectory, "DotnetTools")}";
            return null;
        }
        if (Assemblies.FirstOrDefault(name => !File.Exists(Path.Combine(directory, name + ".dll"))) is { } missing)
        {
            unavailable = $"the SDK has no {missing}.dll in {directory}";
            return null;
        }
        // The compiler's own assemblies resolve from Roslyn/bincore first (SdkRoslyn); the engine's, and what
        // they need besides (System.Composition, Microsoft.DiaSymReader), from dotnet-watch.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var candidate = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
        try
        {
            return Check(directory, out unavailable);
        }
        catch (Exception e)
        {
            unavailable = $"its engine in {directory} doesn't load: {e.Message}";
            return null;
        }
    }

    /// <summary>The engine, if its assemblies are the compiler's Roslyn build and its service has the members this shim calls.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)] // touches Workspaces types, which only load once the files are known to be there
    static HotReloadEngine? Check(string directory, out string? unavailable)
    {
        var compiler = typeof(Compilation).Assembly.GetName();
        foreach (var name in Assemblies)
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(name));
            if (Unmatched(assembly, compiler) is { } reason)
            {
                unavailable = reason;
                return null;
            }
        }
        var hotReload = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Microsoft.CodeAnalysis.ExternalAccess.HotReload"));
        if (hotReload.GetType(ServiceType) is not { } service)
        {
            unavailable = $"{hotReload.GetName().Name} in {directory} has no {ServiceType}";
            return null;
        }
        try
        {
            unavailable = null;
            return new HotReloadEngine(service, hotReload);
        }
        catch (MissingMemberException e)
        {
            unavailable = $"{hotReload.GetName().Name} {hotReload.GetName().Version} isn't the one this blaze-cs knows: {e.Message}";
            return null;
        }
    }

    /// <summary>Why an engine assembly isn't of the compiler's Roslyn build, or null if it is.</summary>
    internal static string? Unmatched(Assembly assembly, AssemblyName compiler)
    {
        var name = assembly.GetName();
        return name.Version == compiler.Version ? null
            : $"{name.Name} {name.Version} is not of the compiler's Roslyn build ({compiler.Name} {compiler.Version})";
    }

    /// <summary>dotnet-watch's directory in an SDK: DotnetTools/dotnet-watch/&lt;version&gt;/tools/&lt;tfm&gt;/any.</summary>
    static string? WatchDirectory(string sdkDirectory)
    {
        var root = Path.Combine(sdkDirectory, "DotnetTools", "dotnet-watch");
        return !Directory.Exists(root) ? null : Directory.EnumerateDirectories(root)
            .SelectMany(version => Directory.Exists(Path.Combine(version, "tools")) ? Directory.EnumerateDirectories(Path.Combine(version, "tools")) : [])
            .Select(framework => Path.Combine(framework, "any"))
            .FirstOrDefault(any => File.Exists(Path.Combine(any, "Microsoft.CodeAnalysis.ExternalAccess.HotReload.dll")));
    }

    /// <summary>A new HotReloadService, for one session.</summary>
    internal object CreateService() => _create.Invoke([_workspace.Value.CurrentSolution.Services,
        (Func<ValueTask<ImmutableArray<string>>>)(() => ValueTask.FromResult(Capabilities))]);

    /// <summary>An empty solution of the engine's workspace, to add a session's projects to.</summary>
    internal Solution EmptySolution() => _workspace.Value.CurrentSolution;

    internal void Start(object service, Solution solution) => Await(_start.Invoke(service, [solution, CancellationToken.None]));

    internal EngineUpdates GetUpdates(object service, Solution solution, ProjectId running)
    {
        var runningProjects = typeof(ImmutableDictionary<,>).MakeGenericType(typeof(ProjectId), _runningProject);
        var empty = runningProjects.GetField("Empty")!.GetValue(null);
        var add = runningProjects.GetMethod("Add", [typeof(ProjectId), _runningProject])!;
        var projects = add.Invoke(empty, [running, Activator.CreateInstance(_runningProject)]);
        var task = (Task)_getUpdates.Invoke(service, [solution, projects, CancellationToken.None])!;
        Await(task);
        var updates = task.GetType().GetProperty("Result")!.GetValue(task)!;

        var diagnostics = new List<Diagnostic>();
        foreach (var (_, perProject) in Items(_transientDiagnostics.GetValue(updates)).OfType<ITuple>().Select(t => (t[0], t[1])))
            diagnostics.AddRange(Items(perProject).OfType<Diagnostic>());
        diagnostics.AddRange(Items(_persistentDiagnostics.GetValue(updates)).OfType<Diagnostic>());
        var deltas = Items(_projectUpdates.GetValue(updates)).Select(update => new EngineDelta(
            (Guid)_module.GetValue(update)!,
            (ProjectId)_project.GetValue(update)!,
            [.. (ImmutableArray<byte>)_metadata.GetValue(update)!],
            [.. (ImmutableArray<byte>)_il.GetValue(update)!],
            [.. (ImmutableArray<byte>)_pdb.GetValue(update)!],
            [.. (ImmutableArray<int>)_updatedTypes.GetValue(update)!])).ToList();
        return new EngineUpdates(
            _status.GetValue(updates)!.ToString()!,
            deltas,
            diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList(),
            Items(_projectsToRestart.GetValue(updates)).Cast<object>().Count() + Items(_projectsToRebuild.GetValue(updates)).Cast<object>().Count());
    }

    internal void Commit(object service) => _commit.Invoke(service, null);
    internal void Discard(object service) => _discard.Invoke(service, null);
    internal void End(object service) => _end.Invoke(service, null);

    /// <summary>The items of an ImmutableArray or dictionary, boxed; none for a default ImmutableArray.</summary>
    static IEnumerable<object> Items(object? collection) => collection switch
    {
        null => [],
        _ when collection.GetType().GetProperty("IsDefault") is { } isDefault && (bool)isDefault.GetValue(collection)! => [],
        IEnumerable items => items.Cast<object>(),
        _ => [],
    };

    static void Await(object? task)
    {
        try { ((Task)task!).GetAwaiter().GetResult(); }
        catch (TargetInvocationException e) when (e.InnerException is { } inner) { throw inner; }
    }
}

/// <summary>What the engine made of an edit: its status (NoChangesToApply, ReadyToApply, Blocked) and deltas, and why it can't apply some.</summary>
sealed record EngineUpdates(string Status, List<EngineDelta> Deltas, List<string> Errors, int ProjectsToRestartOrRebuild);

sealed record EngineDelta(Guid Module, ProjectId Project, byte[] Metadata, byte[] IL, byte[] Pdb, int[] UpdatedTypes);

/// <summary>
/// One module a test host has loaded from the solution: the unit's assembly path (its key), its name and
/// project file (the engine ignores a project without one), the path the host loaded it from and its MVID, and where the engine reads the bytes the host loaded, which the
/// build after an edit replaces on disk.
/// </summary>
sealed record LoadedModule(string Key, string Name, string ProjectFile, string Loaded, Guid Mvid, string Snapshot);

/// <summary>
/// A hot reload session for one test host: a Workspaces solution of the modules the host has loaded, as the
/// sources of the bytes it loaded, and the engine's session over it. Each edit gives the engine the units'
/// current sources; it returns deltas against what the host runs, which are committed once the host has
/// applied them. A session sees only its host's modules, so an edit elsewhere doesn't reach it.
/// </summary>
sealed class HotReloadSession
{
    readonly HotReloadEngine _engine;
    readonly object _service;
    readonly Dictionary<string, Project> _projects = new(PathComparer.Instance);
    readonly ProjectId _running;
    Solution _solution;
    Solution? _pending;
    Dictionary<string, Project>? _pendingProjects;

    /// <summary>A project in the session, by unit: its id, and its documents' ids and texts by path.</summary>
    sealed record Project(ProjectId Id, string Name, Dictionary<string, (DocumentId Id, SourceText Text)> Documents, UnitSources Sources);

    public IReadOnlyList<LoadedModule> Modules { get; }

    HotReloadSession(HotReloadEngine engine, IReadOnlyList<LoadedModule> modules, IReadOnlyDictionary<string, UnitSources> sources, string runningKey)
    {
        _engine = engine;
        Modules = modules;
        _solution = engine.EmptySolution();
        foreach (var module in modules)
        {
            var id = ProjectId.CreateNewId(module.Name);
            var project = new Project(id, module.Name, new(PathComparer.Instance), sources[module.Key]);
            var documents = project.Sources.Trees.Select(tree => Document(id, project, tree)).ToList();
            _solution = _solution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), module.Name, project.Sources.AssemblyName, LanguageNames.CSharp,
                    filePath: module.ProjectFile, outputFilePath: module.Key, compilationOptions: project.Sources.Options, parseOptions: project.Sources.ParseOptions, documents: documents,
                    metadataReferences: project.Sources.References)
                .WithCompilationOutputInfo(default(CompilationOutputInfo).WithAssemblyPath(module.Snapshot)));
            _projects[module.Key] = project;
        }
        _running = _projects[runningKey].Id;
        _service = engine.CreateService();
        engine.Start(_service, _solution);
    }

    /// <summary>Starts a session over these modules, whose units' sources are as given; the running one is the test project's.</summary>
    public static HotReloadSession Start(HotReloadEngine engine, IReadOnlyList<LoadedModule> modules, IReadOnlyDictionary<string, UnitSources> sources, string runningKey) =>
        new(engine, modules, sources, runningKey);

    static DocumentInfo Document(ProjectId project, Project owner, SyntaxTree tree)
    {
        var text = tree.GetText();
        var id = DocumentId.CreateNewId(project, tree.FilePath);
        owner.Documents[tree.FilePath] = (id, text);
        return DocumentInfo.Create(id, Path.GetFileName(tree.FilePath), loader: TextLoader.From(TextAndVersion.Create(text, VersionStamp.Create(), tree.FilePath)),
            filePath: tree.FilePath);
    }

    /// <summary>
    /// The engine's updates for the units' current sources, by unit key. Commit or Discard follows, after the
    /// host has applied them or not.
    /// </summary>
    public (EngineUpdates Updates, Dictionary<ProjectId, string> Keys) Changes(IReadOnlyDictionary<string, UnitSources> current)
    {
        var solution = _solution;
        var projects = new Dictionary<string, Project>(PathComparer.Instance);
        foreach (var (key, before) in _projects)
        {
            var now = current[key];
            var project = before with { Documents = new(before.Documents, PathComparer.Instance), Sources = now };
            var seen = new HashSet<string>(PathComparer.Instance);
            foreach (var tree in now.Trees)
            {
                seen.Add(tree.FilePath);
                if (!project.Documents.TryGetValue(tree.FilePath, out var document))
                {
                    solution = solution.AddDocument(Document(project.Id, project, tree));
                    continue;
                }
                var text = tree.GetText();
                if (!ReferenceEquals(text, document.Text) && !text.ContentEquals(document.Text))
                {
                    solution = solution.WithDocumentText(document.Id, text, PreservationMode.PreserveIdentity);
                    project.Documents[tree.FilePath] = (document.Id, text);
                }
            }
            foreach (var gone in project.Documents.Keys.Where(path => !seen.Contains(path)).ToList())
            {
                solution = solution.RemoveDocument(project.Documents[gone].Id);
                project.Documents.Remove(gone);
            }
            if (!now.References.SequenceEqual(before.Sources.References))
                solution = solution.WithProjectMetadataReferences(project.Id, now.References);
            if (!ReferenceEquals(now.Options, before.Sources.Options))
                solution = solution.WithProjectCompilationOptions(project.Id, now.Options);
            if (!ReferenceEquals(now.ParseOptions, before.Sources.ParseOptions))
                solution = solution.WithProjectParseOptions(project.Id, now.ParseOptions);
            projects[key] = project;
        }
        (_pending, _pendingProjects) = (solution, projects);
        return (_engine.GetUpdates(_service, solution, _running), projects.ToDictionary(p => p.Value.Id, p => p.Key));
    }

    /// <summary>The host applied the last changes: they are what it runs now.</summary>
    public void Commit()
    {
        _engine.Commit(_service);
        _solution = _pending!;
        foreach (var (key, project) in _pendingProjects!)
            _projects[key] = project;
        (_pending, _pendingProjects) = (null, null);
    }

    /// <summary>Ends the session; a pending update is discarded first.</summary>
    public void End()
    {
        try
        {
            if (_pending is not null)
                _engine.Discard(_service);
        }
        catch (Exception) { } // nothing pending after all
        _engine.End(_service);
    }

    /// <summary>
    /// A copy of an assembly a host loaded, and of its PDB, under the directory for its MVID; the MVID. The
    /// engine opens the PDB at the path the assembly's CodeView entry names before the one next to the
    /// assembly, and without checking it is the PDB of that build, so the copy's entry is pointed at its
    /// copied PDB: the PDB at the original path is the next build's.
    /// </summary>
    public static (string Path, Guid Mvid) Snapshot(string loaded, string? pdb, string root)
    {
        var bytes = File.ReadAllBytes(loaded);
        Guid mvid;
        var codeViews = new List<(int Offset, int Length, string Path)>();
        using (var pe = new PEReader(ImmutableArray.Create(bytes)))
        {
            var metadata = pe.GetMetadataReader();
            mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            foreach (var entry in pe.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.CodeView))
            {
                // "RSDS", the PDB id (16 bytes) and age (4), then the path, UTF-8 and zero-terminated.
                const int PathStart = 24;
                codeViews.Add((entry.DataPointer + PathStart, entry.DataSize - PathStart, pe.ReadCodeViewDebugDirectoryData(entry).Path));
            }
        }
        var directory = Path.Combine(root, mvid.ToString("N"));
        var snapshot = Path.Combine(directory, Path.GetFileName(loaded));
        lock (Snapshots)
        {
            if (!Snapshots.Add(snapshot))
                return (snapshot, mvid);
            Directory.CreateDirectory(directory);
            foreach (var (offset, length, original) in codeViews)
            {
                var name = Path.GetFileName(original.Replace('\\', '/'));
                // The PDB next to the loaded copy is the one built with it; obj/'s may be newer.
                if (new[] { Path.Combine(Path.GetDirectoryName(loaded)!, name), pdb }.FirstOrDefault(File.Exists) is { } source)
                    File.Copy(source, Path.Combine(directory, name), overwrite: true);
                // The copy's own path if it fits in the entry, or else the root's (C:\, /) file of that name, which
                // doesn't exist, so the engine goes on to the PDB next to the copy.
                var target = new[] { Path.Combine(directory, name), Path.Combine(Path.GetPathRoot(directory)!, name) }
                    .FirstOrDefault(path => System.Text.Encoding.UTF8.GetByteCount(path) < length && (path.StartsWith(directory, StringComparison.Ordinal) || !File.Exists(path)))
                    ?? throw new InvalidOperationException($"{Path.GetFileName(loaded)}'s PDB path is too short to point at a copy");
                Array.Clear(bytes, offset, length);
                System.Text.Encoding.UTF8.GetBytes(target, bytes.AsSpan(offset));
            }
            File.WriteAllBytes(snapshot, bytes);
        }
        return (snapshot, mvid);
    }

    static readonly HashSet<string> Snapshots = new(PathComparer.Instance);
}
