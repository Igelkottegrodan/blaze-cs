using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;

namespace BlazeCs.Logger;

/// <summary>
/// Records what the daemon needs from a `dotnet build` (a BuildRecord) into the file given as its
/// parameter: `-logger:BlazeCs.Logger.CompileLogger,blaze-cs-logger.dll;build.json`, with
/// MSBUILDLOGTASKINPUTS=1 in the build's environment. MSBuild forwards every node's events to it and
/// calls it on one thread at a time.
/// </summary>
public sealed partial class CompileLogger : ILogger
{
    readonly Dictionary<(int Node, int Context), Project> _projects = [];
    readonly Dictionary<(int Node, int Context, int Task), RunningTask> _tasks = [];
    // The RunSettingsFilePath of each evaluation that sets one, by evaluation id. A project may be evaluated on
    // another node than the one that builds it, so this is read only once every event has arrived.
    readonly Dictionary<int, string> _runSettings = [];
    readonly List<CompileRecord> _compiles = [];
    // Each with the id of the evaluation its build ran from, which names its RunSettingsFilePath.
    readonly HashSet<(string ProjectFile, string? TargetFramework, int Evaluation)> _compiled = [];
    readonly List<CopyRecord> _copies = [];

    public LoggerVerbosity Verbosity { get; set; }

    /// <summary>The file to write.</summary>
    public string? Parameters { get; set; }

    public void Initialize(IEventSource eventSource)
    {
        if (string.IsNullOrEmpty(Parameters))
            throw new LoggerException("blaze-cs-logger needs the file to write: -logger:<type>,<assembly>;<file>");
        // Task inputs carry Csc's output path, CopyRefAssembly's paths, and what tasks after CoreCompile
        // are handed. IEventSource3.IncludeTaskInputs doesn't turn them on for the build's nodes; this does.
        if (Environment.GetEnvironmentVariable("MSBUILDLOGTASKINPUTS") != "1")
            throw new LoggerException("blaze-cs-logger needs MSBUILDLOGTASKINPUTS=1");

        // A property's evaluated value reaches a logger only in the evaluation's own event, and only if some
        // logger asks for evaluation properties; the console logger of `dotnet build` happens to, this makes
        // sure. MSBuild offers no way to name one property.
        (eventSource as IEventSource4)?.IncludeEvaluationPropertiesAndItems();
        eventSource.StatusEventRaised += (_, e) => EvaluationFinished(e);
        eventSource.ProjectStarted += (_, e) => _projects[ProjectKey(e.BuildEventContext)] = new Project(e);
        eventSource.ProjectFinished += (_, e) => ProjectFinished(e.BuildEventContext);
        eventSource.TargetStarted += (_, e) => TargetStarted(e);
        eventSource.TaskStarted += (_, e) => TaskStarted(e);
        eventSource.TaskFinished += (_, e) => TaskFinished(e.BuildEventContext);
        eventSource.MessageRaised += (_, e) => TaskEvent(e.BuildEventContext, e);
        eventSource.WarningRaised += (_, e) => TaskEvent(e.BuildEventContext, e);
        eventSource.ErrorRaised += (_, e) => TaskEvent(e.BuildEventContext, e);
    }

    public void Shutdown()
    {
        var compiled = _compiled.Select(p => new ProjectRecord(p.ProjectFile, p.TargetFramework, _runSettings.GetValueOrDefault(p.Evaluation)));
        File.WriteAllText(Parameters!, JsonSerializer.Serialize(new BuildRecord(_compiles, [.. compiled], _copies)));
    }

    void EvaluationFinished(BuildStatusEventArgs e)
    {
        if (e is not ProjectEvaluationFinishedEventArgs { Properties: { } properties } finished)
            return;
        // What an entry is depends on where the project was evaluated: forwarded from another node it is a
        // KeyValuePair, in this one a ProjectPropertyInstance, which MSBuild.Execution doesn't expose to
        // a logger but whose ToString is "name=value".
        foreach (var property in properties)
        {
            var (name, value) = property switch
            {
                DictionaryEntry entry => (entry.Key as string, entry.Value as string),
                KeyValuePair<string, string?> pair => (pair.Key, pair.Value),
                _ when property.ToString() is { } text && text.IndexOf('=') is var equals and > 0 => (text[..equals], text[(equals + 1)..]),
                _ => (null, null),
            };
            if (name == "RunSettingsFilePath" && !string.IsNullOrWhiteSpace(value) && finished.BuildEventContext is { } context)
            {
                _runSettings[context.EvaluationId] = Path.GetFullPath(value.Trim(), Path.GetDirectoryName(finished.ProjectFile) ?? "");
                return;
            }
        }
    }

    static (int, int) ProjectKey(BuildEventContext? c) => (c?.NodeId ?? 0, c?.ProjectContextId ?? 0);

    static (int, int, int) TaskKey(BuildEventContext c) => (c.NodeId, c.ProjectContextId, c.TaskId);

    void ProjectFinished(BuildEventContext? context)
    {
        if (_projects.Remove(ProjectKey(context), out var project) && project.CommandLine is { } commandLine)
            _compiles.Add(new CompileRecord(project.File, project.TargetFramework, commandLine, project.AfterCompile));
    }

    void TargetStarted(TargetStartedEventArgs e)
    {
        if (!_projects.TryGetValue(ProjectKey(e.BuildEventContext), out var project))
            return;
        var target = e.BuildEventContext!.TargetId;
        if (project.CoreCompile is not null)
            project.TargetsAfterCompile[target] = (e.TargetName, e.TargetFile);
        else if (e.TargetName == "CoreCompile")
        {
            project.CoreCompile = target;
            // Skipped as up to date, CoreCompile still starts: that is how a project csc didn't run for shows.
            if (e.TargetFile?.EndsWith("Microsoft.CSharp.Core.targets", PathComparer.Comparison) == true)
                _compiled.Add((project.File, project.TargetFramework, project.Evaluation));
        }
    }

    void TaskStarted(TaskStartedEventArgs e)
    {
        var context = e.BuildEventContext!;
        if (!_projects.TryGetValue(ProjectKey(context), out var project))
            return;
        var task = new RunningTask(project, e.TaskName)
        {
            Copies = e.TaskName is "Copy" or "CopyRefAssembly",
            Compiles = e.TaskName == "Csc" && context.TargetId == project.CoreCompile,
            AfterCompile = project.TargetsAfterCompile.TryGetValue(context.TargetId, out var target) ? target : null,
        };
        if (task.Copies || task.Compiles || task.AfterCompile is not null)
            _tasks[TaskKey(context)] = task;
    }

    void TaskFinished(BuildEventContext? context)
    {
        // CopyRefAssembly logs no paths when the destination is already current.
        if (context is not null && _tasks.Remove(TaskKey(context), out var task) && task is { Source: { } source, Destination: { } destination })
            _copies.Add(new CopyRecord(source, destination));
    }

    void TaskEvent(BuildEventContext? context, BuildEventArgs e)
    {
        if (context is null || context.TaskId == BuildEventContext.InvalidTaskId || !_tasks.TryGetValue(TaskKey(context), out var task))
            return;
        if (e is TaskParameterEventArgs parameter)
        {
            TaskParameter(task, parameter);
            return;
        }

        var text = e is TaskCommandLineEventArgs commandLine ? commandLine.CommandLine : e.Message;
        if (text is null)
            return;
        if (task.Compiles && e is TaskCommandLineEventArgs)
            task.Project.CommandLine = text;
        if (task.Copies && e is BuildMessageEventArgs && CopyMessage().Match(text) is { Success: true } match)
            _copies.Add(new CopyRecord(task.Project.FullPath(match.Groups[1].Value), task.Project.FullPath(match.Groups[2].Value)));
        Mentions(task, text);
    }

    void TaskParameter(RunningTask task, TaskParameterEventArgs e)
    {
        var first = e.Items is [var item, ..] ? ItemSpec(item) : null;
        if (task.Compiles && e.ParameterName == "OutputAssembly" && first is not null)
            task.Project.Output = task.Project.FullPath(first);
        if (task.Name == "CopyRefAssembly" && first is not null)
        {
            if (e.ParameterName == "SourcePath")
                task.Source = task.Project.FullPath(first);
            else if (e.ParameterName == "DestinationPath")
                task.Destination = task.Project.FullPath(first);
        }

        if (task.AfterCompile is null || task.Reported || task.Project.Output is null)
            return;
        foreach (var each in e.Items ?? Array.Empty<object>())
        {
            Mentions(task, ItemSpec(each));
            if (each is ITaskItem withMetadata)
            {
                foreach (var value in withMetadata.CloneCustomMetadata().Values)
                    Mentions(task, value as string);
            }
        }
    }

    static string? ItemSpec(object? item) => item is ITaskItem taskItem ? taskItem.ItemSpec : item?.ToString();

    /// <summary>
    /// A task after CoreCompile that is handed the freshly compiled assembly may rewrite it (Fody, XAML
    /// compilers); the daemon decides which of those it can't reproduce.
    /// </summary>
    static void Mentions(RunningTask task, string? text)
    {
        if (task is { AfterCompile: (var target, var file), Reported: false, Project.Output: { } output }
            && text is not null && text.Contains(output, PathComparer.Comparison))
        {
            task.Project.AfterCompile.Add(new AfterCompileTask(target, file, task.Name));
            task.Reported = true;
        }
    }

    /// <summary>One project build request: a project, or one target framework of it.</summary>
    sealed class Project(ProjectStartedEventArgs e)
    {
        public int Evaluation { get; } = e.BuildEventContext?.EvaluationId ?? BuildEventContext.InvalidEvaluationId;
        public string File { get; } = e.ProjectFile ?? "";
        public string? TargetFramework { get; } = e.GlobalProperties?.TryGetValue("TargetFramework", out var framework) == true ? framework : null;
        public int? CoreCompile { get; set; }
        public Dictionary<int, (string Name, string? File)> TargetsAfterCompile { get; } = [];
        public string? CommandLine { get; set; }
        /// <summary>The assembly csc writes.</summary>
        public string? Output { get; set; }
        public List<AfterCompileTask> AfterCompile { get; } = [];

        public string FullPath(string path) => Path.GetFullPath(path, Path.GetDirectoryName(File)!);
    }

    sealed class RunningTask(Project project, string name)
    {
        public Project Project => project;
        public string Name => name;
        public bool Copies { get; init; }
        public bool Compiles { get; init; }
        public (string Name, string? File)? AfterCompile { get; init; }
        public bool Reported { get; set; }
        public string? Source { get; set; }
        public string? Destination { get; set; }
    }

    // "Copying file from "a" to "b".", "Did not copy from file "a" to file "b" because ...",
    // "Creating hard link to copy "a" to "b".", "Copying reference assembly from "a" to "b"."
    [GeneratedRegex("""(?:from|copy) (?:file )?"([^"]+)" to (?:file )?"([^"]+)"[ .]""")]
    private static partial Regex CopyMessage();
}
