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
/// caches the parsed XML of every import, which makes later evaluations fast.
/// </summary>
sealed class MSBuildHost
{
    /// <summary>
    /// The head of CoreBuildDependsOn, up to and including Compile, without the build events: Compile alone
    /// skips PrepareResources, and with it every /resource: argument. First the check a real build runs before
    /// BeforeBuild, with the SDK's checks that run before it (CheckForDuplicateItems): run first, they see the
    /// items as evaluated, not those a target before CoreCompile adds.
    /// </summary>
    static readonly string[] CompileTargets = ["_CheckForInvalidConfigurationAndPlatform", "BuildOnlySettings", "PrepareForBuild", "ResolveReferences", "PrepareResources", "ResolveKeySource", "Compile"];

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

    readonly ProjectCollection _collection = new();
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

    /// <summary>One unit to evaluate again: its project file and the global properties its last real build had.</summary>
    public sealed record Request(string Key, string ProjectFile, IReadOnlyDictionary<string, string> GlobalProperties);

    /// <summary>A unit's csc arguments from the pass, and its evaluated RunSettingsFilePath as a full path, if any.</summary>
    public sealed record Evaluated(string[] Arguments, string? RunSettingsFilePath);

    /// <summary>
    /// Evaluates these units again, after <paramref name="changedFiles"/> changed on disk, and runs their
    /// design-time compile pass. Returns each unit's new arguments by key, or why the edit needs a real
    /// build: a restore, or a pass that failed.
    /// </summary>
    public (Dictionary<string, Evaluated>? Units, string? Fallback) Pass(IReadOnlyList<Request> requests, IEnumerable<string> changedFiles)
    {
        // The collection caches every file it parsed and never looks at the disk again on its own.
        foreach (var file in changedFiles)
            ProjectRootElement.TryOpen(file, _collection)?.Reload(throwIfUnsavedChanges: false);
        // A new context per pass: one caches what it saw of the file system.
        var context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);
        ProjectInstance Evaluate(string projectFile, IDictionary<string, string> globalProperties) =>
            ProjectInstance.FromFile(projectFile, new ProjectOptions
            {
                GlobalProperties = globalProperties,
                ProjectCollection = _collection,
                EvaluationContext = context,
            });

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
            foreach (var request in requests)
            {
                var globalProperties = new Dictionary<string, string>(request.GlobalProperties);
                foreach (var (property, value) in DesignTime)
                    globalProperties[property] = value;
                var project = Evaluate(request.ProjectFile, globalProperties);
                var result = manager.BuildRequest(new BuildRequestData(project, CompileTargets, null, BuildRequestDataFlags.ProvideProjectStateAfterBuild));
                var name = Path.GetFileNameWithoutExtension(request.ProjectFile);
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
    /// NuGet's own no-op check: the restore spec regenerated from the project now, compared byte for byte with
    /// the one the last restore wrote to obj/. They differ when a package, a project reference, a target
    /// framework, a source or anything else restore reads has changed.
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
        return File.ReadAllBytes(regenerated).AsSpan().SequenceEqual(File.ReadAllBytes(restored))
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
