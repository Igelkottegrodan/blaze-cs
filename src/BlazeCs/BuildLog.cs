using System.Text.RegularExpressions;
using Microsoft.Build.Logging.StructuredLogger;
using Microsoft.CodeAnalysis;
using MSBuildTask = Microsoft.Build.Logging.StructuredLogger.Task;

namespace BlazeCs;

/// <summary>One csc invocation from a real build: a project, or one target framework of it.</summary>
/// <param name="SlowPathReason">Set when something after CoreCompile rewrites the assembly
/// (an IL weaver), so the daemon's own emit would not match and the project must go through MSBuild.</param>
sealed record CompileUnit(string Name, string ProjectFile, string ProjectDirectory, string[] Arguments, string? SlowPathReason)
{
    /// <summary>The assembly csc writes (/out:), which identifies the unit across builds.</summary>
    public string AssemblyPath => PathArgument("/out:")!;

    /// <summary>The reference assembly csc writes (/refout:), if any.</summary>
    public string? ReferenceAssemblyPath => PathArgument("/refout:");

    string? PathArgument(string prefix) =>
        Arguments.LastOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal)) is { } argument
            ? Path.GetFullPath(argument[prefix.Length..].Trim('"'), ProjectDirectory)
            : null;
}

/// <summary>What the daemon learns from a `dotnet build -bl` binlog.</summary>
/// <param name="Copies">Every file copy MSBuild made or confirmed up to date, source to destination.</param>
sealed record BuildLogInfo(IReadOnlyList<CompileUnit> Units, IReadOnlyList<(string From, string To)> Copies);

static partial class BuildLog
{
    public static BuildLogInfo Read(string binlogPath, string sdkDirectory)
    {
        var build = BinaryLog.ReadBuild(binlogPath);
        if (!build.Succeeded)
            throw new InvalidOperationException($"The build in {binlogPath} failed");

        var units = new List<CompileUnit>();
        var copies = new List<(string, string)>();
        foreach (var project in build.FindChildrenRecursive<Project>())
        {
            // A project shows up once per MSBuild invocation (restore, build, each target framework);
            // only invocations that ran their own targets matter here.
            var targets = project.Children.OfType<Target>().ToList();
            var projectDirectory = Path.GetDirectoryName(project.ProjectFile)!;

            foreach (var task in targets.SelectMany(t => t.Children.OfType<MSBuildTask>()))
            {
                if (task.Name is "Copy" or "CopyRefAssembly")
                    copies.AddRange(CopiesOf(task, projectDirectory));
            }

            var csc = targets.Where(t => t.Name == "CoreCompile")
                .SelectMany(t => t.Children.OfType<MSBuildTask>())
                .FirstOrDefault(t => t.Name == "Csc");
            if (csc?.CommandLineArguments is not { } commandLine)
                continue;

            var name = Path.GetFileNameWithoutExtension(project.ProjectFile)
                + (string.IsNullOrEmpty(project.TargetFramework) ? "" : $" ({project.TargetFramework})");
            var unit = new CompileUnit(name, project.ProjectFile, projectDirectory, CompilerArguments(commandLine), null);
            units.Add(unit with { SlowPathReason = SlowPathReason(targets, unit.AssemblyPath, sdkDirectory) });
        }
        return new BuildLogInfo(units, copies);
    }

    /// <summary>csc's arguments without the compiler executable in front.</summary>
    static string[] CompilerArguments(string commandLine)
    {
        var all = CommandLineParser.SplitCommandLineIntoArguments(commandLine, removeHashComments: false).ToList();
        var compiler = all.FindIndex(a => Path.GetFileNameWithoutExtension(a) == "csc");
        return all.Skip(compiler + 1).ToArray();
    }

    /// <summary>
    /// A task outside the SDK that runs after CoreCompile and is handed the freshly compiled assembly
    /// is a post-compile rewrite (Fody, XAML compilers). SDK targets only read it.
    /// </summary>
    static string? SlowPathReason(List<Target> targets, string output, string sdkDirectory)
    {
        var dotnetRoot = Path.GetFullPath(Path.Combine(sdkDirectory, "..", ".."));
        var afterCompile = targets.SkipWhile(t => t.Name != "CoreCompile").Skip(1);
        foreach (var target in afterCompile)
        {
            if (target.SourceFilePath is { } file && Path.GetFullPath(file).StartsWith(dotnetRoot, PathComparer.Comparison))
                continue;
            foreach (var task in target.Children.OfType<MSBuildTask>())
            {
                var mentionsAssembly = task.FindChildrenRecursive<BaseNode>()
                    .Any(n => n.ToString() is { } text && text.Contains(output, PathComparer.Comparison));
                if (mentionsAssembly)
                    return $"target {target.Name} ({task.Name}) rewrites the assembly after compile";
            }
        }
        return null;
    }

    static IEnumerable<(string, string)> CopiesOf(MSBuildTask task, string projectDirectory)
    {
        foreach (var message in task.FindChildrenRecursive<Message>())
        {
            var match = CopyMessage().Match(message.Text ?? "");
            if (match.Success)
                yield return (Path.GetFullPath(match.Groups[1].Value, projectDirectory),
                    Path.GetFullPath(match.Groups[2].Value, projectDirectory));
        }

        // CopyRefAssembly logs no paths when the destination is already current.
        if (task.Name == "CopyRefAssembly"
            && task.FindChildrenRecursive<Property>(p => p.Name == "SourcePath").FirstOrDefault() is { } source
            && task.FindChildrenRecursive<Property>(p => p.Name == "DestinationPath").FirstOrDefault() is { } destination)
        {
            yield return (Path.GetFullPath(source.Value, projectDirectory), Path.GetFullPath(destination.Value, projectDirectory));
        }
    }

    // "Copying file from "a" to "b".", "Did not copy from file "a" to file "b" because ...",
    // "Creating hard link to copy "a" to "b".", "Copying reference assembly from "a" to "b"."
    [GeneratedRegex("""(?:from|copy) (?:file )?"([^"]+)" to (?:file )?"([^"]+)"[ .]""")]
    private static partial Regex CopyMessage();
}
