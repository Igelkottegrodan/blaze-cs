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

    /// <summary>Identifies the unit as a build names it: its project file, and the target framework in its name.</summary>
    public string Key => KeyOf(ProjectFile, Name);

    public static string KeyOf(string projectFile, string name) => projectFile + '|' + name;

    string? PathArgument(string prefix) =>
        Arguments.LastOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal)) is { } argument
            ? Path.GetFullPath(argument[prefix.Length..].Trim('"'), ProjectDirectory)
            : null;
}

/// <summary>What the daemon learns from a `dotnet build -bl` binlog.</summary>
/// <param name="Units">The csc invocations that ran: an incremental build runs csc only where something changed.</param>
/// <param name="Compiled">Every C# project (or target framework of one) the build ran CoreCompile for, whether csc ran
/// or the target was up to date, as <see cref="CompileUnit.Key"/>: the units the solution builds.</param>
/// <param name="Copies">Every file copy MSBuild made or confirmed up to date, source to destination.</param>
sealed record BuildLogInfo(IReadOnlyList<CompileUnit> Units, IReadOnlySet<string> Compiled, IReadOnlyList<(string From, string To)> Copies);

static partial class BuildLog
{
    public static BuildLogInfo Read(string binlogPath, string sdkDirectory)
    {
        var build = BinaryLog.ReadBuild(binlogPath);
        if (!build.Succeeded)
            throw new InvalidOperationException($"The build in {binlogPath} failed");

        // What belongs to the SDK is compared by real path: in a dotnet root assembled from symlinks (nix,
        // or one combining two SDKs) `dotnet --list-sdks` names the link, while MSBuild loads its targets
        // from where the link points.
        var realPaths = new Dictionary<string, string>(PathComparer.Instance);
        var dotnetRoot = Path.GetDirectoryName(Path.GetDirectoryName(RealPath(sdkDirectory, realPaths)))!;
        var units = new List<CompileUnit>();
        var compiled = new HashSet<string>(PathComparer.Instance);
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

            var name = Path.GetFileNameWithoutExtension(project.ProjectFile)
                + (string.IsNullOrEmpty(project.TargetFramework) ? "" : $" ({project.TargetFramework})");
            // Skipped as up to date, CoreCompile still has its node: that is how a project csc didn't run for shows.
            if (targets.Any(t => t.Name == "CoreCompile" && t.SourceFilePath?.EndsWith("Microsoft.CSharp.Core.targets", PathComparer.Comparison) == true))
                compiled.Add(CompileUnit.KeyOf(project.ProjectFile, name));

            var csc = targets.Where(t => t.Name == "CoreCompile")
                .SelectMany(t => t.Children.OfType<MSBuildTask>())
                .FirstOrDefault(t => t.Name == "Csc");
            if (csc?.CommandLineArguments is not { } commandLine)
                continue;

            var unit = new CompileUnit(name, project.ProjectFile, projectDirectory, CompilerArguments(commandLine), null);
            units.Add(unit with { SlowPathReason = SlowPathReason(targets, unit.AssemblyPath, dotnetRoot, realPaths) });
        }
        return new BuildLogInfo(units, compiled, copies);
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
    static string? SlowPathReason(List<Target> targets, string output, string dotnetRoot, Dictionary<string, string> realPaths)
    {
        var afterCompile = targets.SkipWhile(t => t.Name != "CoreCompile").Skip(1);
        foreach (var target in afterCompile)
        {
            if (target.SourceFilePath is { } file
                && RealPath(file, realPaths).StartsWith(dotnetRoot + Path.DirectorySeparatorChar, PathComparer.Comparison))
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

    /// <summary>The full path with every symlink in it resolved, as realpath(3) does; memoized in
    /// <paramref name="resolved"/>.</summary>
    static string RealPath(string path, Dictionary<string, string> resolved)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (resolved.TryGetValue(path, out var real))
            return real;
        // A root (`/`, `C:\`) is never a link, and Windows can't open a drive root as a file.
        if (Path.GetDirectoryName(path) is not { } parent)
            return resolved[path] = path;
        real = Path.Join(RealPath(parent, resolved), Path.GetFileName(path));
        if (new FileInfo(real).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            real = RealPath(target.FullName, resolved);
        return resolved[path] = real;
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
