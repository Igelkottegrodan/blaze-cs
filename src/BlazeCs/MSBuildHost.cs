using System.Runtime.CompilerServices;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Construction;

namespace BlazeCs;

/// <summary>
/// The SDK's own MSBuild, hosted in the daemon: after a project file edit, it evaluates the units of the
/// edited projects again and runs the design-time compile pass, which gives csc's new arguments without
/// running csc or `dotnet build` (docs/research/msbuild-in-process.md). Every MSBuild type the daemon uses is
/// in this class. Started on the first project file edit, and kept for the daemon's lifetime: its collection
/// caches the parsed XML of every import, which makes later evaluations fast. The cache never looks at the disk
/// again on its own: each pass reloads the files it knows changed, and the daemon resets it after a dotnet
/// build, which may have rewritten any of them (restore's obj/*.nuget.g.props, say).
/// </summary>
sealed class MSBuildHost
{
    /// <summary>
    /// The targets a real build runs up to and including Compile, from the project's own BuildDependsOn (up to
    /// CoreBuild) and CoreBuildDependsOn (up to Compile), so a target a project or an import puts in front of
    /// them runs too. Compile alone skips PrepareResources, and with it every /resource: argument, and
    /// BeforeBuild, with any target hooked to it. The pre-build event is among them, but a project with one falls
    /// back first. First the check a real build runs before BeforeBuild, with the SDK's checks that run before
    /// it (CheckForDuplicateItems): run first, they see the items as evaluated, not those a target before
    /// CoreCompile adds. Null when the lists don't lead to Compile.
    /// </summary>
    static string[]? CompileTargets(ProjectInstance project)
    {
        static string[] Targets(string list) => list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string[] build = Targets(project.GetPropertyValue("BuildDependsOn")), core = Targets(project.GetPropertyValue("CoreBuildDependsOn"));
        var coreBuild = Array.FindIndex(build, t => t.Equals("CoreBuild", StringComparison.OrdinalIgnoreCase));
        var compile = Array.FindIndex(core, t => t.Equals("Compile", StringComparison.OrdinalIgnoreCase));
        return coreBuild < 0 || compile < 0 ? null : ["_CheckForInvalidConfigurationAndPlatform", .. build[..coreBuild], .. core[..(compile + 1)]];
    }

    /// <summary>Added to the global properties of the unit's real build for the design-time pass.</summary>
    static readonly Dictionary<string, string> DesignTime = new()
    {
        // The Csc task hands back its arguments in CscCommandLineArgs, without running csc.
        ["SkipCompilerExecution"] = "true",
        ["ProvideCommandLineArgs"] = "true",
        // References are asked for their target path, not built.
        ["BuildProjectReferences"] = "false",
        // Makes CoreCompile out of date, so Csc runs even when the outputs are newer than the inputs; the
        // same property Visual Studio's design-time builds set.
        ["NonExistentFile"] = @"__NonExistentSubDir__\__NonExistentFile__",
    };

    ProjectCollection _collection = new();

    /// <summary>Every file a pass evaluated, with its imports: a pass reloads those that changed since.</summary>
    readonly HashSet<string> _evaluated = new(PathComparer.Instance);
    readonly string _restoreSpecDirectory;

    MSBuildHost(string stateDirectory)
    {
        _restoreSpecDirectory = Path.Combine(stateDirectory, "restore");
    }

    /// <summary>Loads MSBuild from the solution's SDK and creates the host.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static MSBuildHost Start(string stateDirectory)
    {
        SdkRoslyn.StartMSBuild();
        return Create(stateDirectory);
    }

    // Separate from Start, so MSBuild's environment is set before anything in MSBuild runs.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static MSBuildHost Create(string stateDirectory) => new(stateDirectory);

    /// <summary>Drops every file the collection parsed: after a dotnet build, any of them may have changed.</summary>
    public void Reset()
    {
        _collection.Dispose();
        _collection = new();
        _evaluated.Clear();
    }

    /// <summary>
    /// One unit to evaluate again: its project file, the global properties its last real build had, and how the
    /// edit's changed compiler settings are read (<see cref="ProjectFileEdit.MoreThanCompilerInput"/>): an import
    /// that reads one does more with it than csc, and the edit falls back.
    /// </summary>
    public sealed record Request(string Key, string ProjectFile, IReadOnlyDictionary<string, string> GlobalProperties, IReadOnlyList<string> ChangedSettings);

    /// <summary>A unit's csc arguments from the pass, and its evaluated RunSettingsFilePath as a full path, if any.</summary>
    public sealed record Evaluated(string[] Arguments, string? RunSettingsFilePath);

    /// <summary>
    /// Evaluates these units again, after <paramref name="changedFiles"/> changed on disk, and runs their
    /// design-time compile pass. Returns each unit's new arguments by key, or why the edit needs a real
    /// build: a restore, or a pass that failed.
    /// </summary>
    public (Dictionary<string, Evaluated>? Units, string? Fallback) Pass(IReadOnlyList<Request> requests, IEnumerable<string> changedFiles)
    {
        // The collection caches every file it parsed and never looks at the disk again on its own. A file it
        // parsed through a reference is reloaded only if the daemon saw it change; every import of a project
        // evaluated here, from wherever, also if its write time changed.
        if (_evaluated.Any(file => !File.Exists(file)))
            Reset();
        foreach (var file in changedFiles)
            ProjectRootElement.TryOpen(file, _collection)?.Reload(throwIfUnsavedChanges: false);
        foreach (var file in _evaluated)
        {
            if (ProjectRootElement.TryOpen(file, _collection) is { } root
                && root.LastWriteTimeWhenRead.ToUniversalTime() != File.GetLastWriteTimeUtc(file))
                root.Reload(throwIfUnsavedChanges: false);
        }
        // A new context per pass: one caches what it saw of the file system.
        var context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);
        ProjectInstance Evaluate(string projectFile, IDictionary<string, string> globalProperties)
        {
            var project = ProjectInstance.FromFile(projectFile, new ProjectOptions
            {
                GlobalProperties = globalProperties,
                ProjectCollection = _collection,
                EvaluationContext = context,
            });
            _evaluated.Add(project.FullPath);
            _evaluated.UnionWith(project.ImportPaths);
            return project;
        }

        var errors = new FirstError();
        // A BuildManager of its own each time: the default one keeps the last build's project instances alive.
        using var manager = new BuildManager("blaze-cs");
        manager.BeginBuild(new BuildParameters(_collection)
        {
            // MSBuild's in-process node only: other nodes start cold, evaluate again, and outlive the pass.
            MaxNodeCount = 1,
            DisableInProcNode = false,
            EnableNodeReuse = false,
            Loggers = [errors],
        });
        try
        {
            // Per project, not per target framework: restore is the outer build's.
            foreach (var request in requests.DistinctBy(r => r.ProjectFile, PathComparer.Instance))
            {
                if (RestoreSpecChanged(manager, request, Evaluate, errors) is { } reason)
                    return (null, $"{Path.GetFileNameWithoutExtension(request.ProjectFile)}: {reason}");
            }

            var units = new Dictionary<string, Evaluated>(PathComparer.Instance);
            var importTexts = new Dictionary<string, string>(PathComparer.Instance);
            foreach (var request in requests)
            {
                var globalProperties = new Dictionary<string, string>(request.GlobalProperties);
                foreach (var (property, value) in DesignTime)
                    globalProperties[property] = value;
                var project = Evaluate(request.ProjectFile, globalProperties);
                var name = Path.GetFileNameWithoutExtension(request.ProjectFile);
                // A real build runs it before ResolveReferences; the pass doesn't run what it may do. The SDK's own
                // targets hooked to it (_BlockWinMDsOnUnsupportedTFMs) are no pre-build step.
                var sdk = Path.TrimEndingDirectorySeparator(project.GetPropertyValue("MSBuildExtensionsPath")) + Path.DirectorySeparatorChar;
                if (project.GetPropertyValue("PreBuildEvent").Trim().Length > 0
                    || project.Targets.Values.Any(t => !t.FullPath.StartsWith(sdk, PathComparer.Comparison)
                        && (t.BeforeTargets.Contains("PreBuildEvent", StringComparison.OrdinalIgnoreCase)
                            || t.AfterTargets.Contains("PreBuildEvent", StringComparison.OrdinalIgnoreCase))))
                    return (null, $"{name}: it has a pre-build step");
                // A compiler setting an import reads may also add a copied item, or turn on a target after CoreCompile.
                foreach (var import in project.ImportPaths.Where(path => !path.StartsWith(sdk, PathComparer.Comparison)))
                {
                    if (!importTexts.TryGetValue(import, out var text))
                        importTexts[import] = text = File.Exists(import) ? File.ReadAllText(import) : "";
                    if (ProjectFileEdit.ReadBeyondCompilerInput(text, request.ChangedSettings) is { } read)
                        return (null, $"{name}: the edit to {Path.GetFileName(request.ProjectFile)} changes more than csc's input ({read}), which {Path.GetFileName(import)} reads)");
                }
                if (CompileTargets(project) is not { } targets)
                    return (null, $"{name}: its BuildDependsOn and CoreBuildDependsOn don't lead to Compile");
                var result = manager.BuildRequest(new BuildRequestData(project, targets, null, BuildRequestDataFlags.ProvideProjectStateAfterBuild));
                if (result.OverallResult != BuildResultCode.Success)
                    return (null, $"{name}: the design-time build failed: {errors.Text ?? result.Exception?.Message ?? "no error logged"}");
                var state = result.ProjectStateAfterBuild ?? project;
                string[] arguments = [.. state.GetItems("CscCommandLineArgs").Select(item => item.EvaluatedInclude)];
                if (arguments.Length == 0)
                    return (null, $"{name}: the design-time build gave no compiler arguments");
                var runSettings = state.GetPropertyValue("RunSettingsFilePath").Trim();
                units[request.Key] = new Evaluated(arguments,
                    runSettings.Length == 0 ? null : Path.GetFullPath(runSettings, Path.GetDirectoryName(request.ProjectFile)!));
            }
            return (units, null);
        }
        finally
        {
            manager.EndBuild();
        }
    }

    /// <summary>
    /// NuGet's own no-op check: the restore spec regenerated from the project now, compared with the one the
    /// last restore wrote to obj/ (<see cref="RestoreSpec.Same"/>). They differ when a package, a project
    /// reference, a target framework, a source or anything else restore reads has changed.
    /// </summary>
    string? RestoreSpecChanged(BuildManager manager, Request request, Func<string, IDictionary<string, string>, ProjectInstance> evaluate, FirstError errors)
    {
        var globalProperties = new Dictionary<string, string>(request.GlobalProperties);
        globalProperties.Remove("TargetFramework");
        var project = evaluate(request.ProjectFile, globalProperties);
        var restored = Path.Combine(Path.GetFullPath(project.GetPropertyValue("MSBuildProjectExtensionsPath"), project.Directory),
            Path.GetFileName(request.ProjectFile) + ".nuget.dgspec.json");
        if (!File.Exists(restored))
            return $"it has no restore spec ({Path.GetFileName(restored)}), so restore inputs may have changed";

        Directory.CreateDirectory(_restoreSpecDirectory);
        var regenerated = Path.Combine(_restoreSpecDirectory, Path.GetFileName(restored));
        globalProperties["RestoreGraphOutputPath"] = regenerated;
        globalProperties["RestoreRecursive"] = "false";
        var result = manager.BuildRequest(new BuildRequestData(evaluate(request.ProjectFile, globalProperties), ["GenerateRestoreGraphFile"]));
        if (result.OverallResult != BuildResultCode.Success || !File.Exists(regenerated))
            return $"regenerating its NuGet restore spec failed: {errors.Text ?? result.Exception?.Message ?? "no error logged"}";
        return RestoreSpec.Same(File.ReadAllText(restored), File.ReadAllText(regenerated))
            ? null
            : "restore inputs changed (its NuGet restore spec differs)";
    }

    /// <summary>The first error a build logged, as "code: message".</summary>
    sealed class FirstError : ILogger
    {
        public string? Text { get; private set; }
        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Quiet;
        public string? Parameters { get; set; }

        public void Initialize(IEventSource eventSource) =>
            eventSource.ErrorRaised += (_, e) => Text ??= $"{e.Code}: {e.Message?.Split('\n')[0].Trim()}";

        public void Shutdown() { }
    }
}
