using System.Text.Json;
using Microsoft.CodeAnalysis;

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

/// <summary>What the daemon learns from a `dotnet build` that ran with blaze-cs-logger.</summary>
/// <param name="Units">The csc invocations that ran: an incremental build runs csc only where something changed.</param>
/// <param name="Compiled">Every C# project (or target framework of one) the build ran CoreCompile for, whether csc ran
/// or the target was up to date, as <see cref="CompileUnit.Key"/>: the units the solution builds.</param>
/// <param name="Copies">Every file copy MSBuild made or confirmed up to date, source to destination.</param>
sealed record BuildLogInfo(IReadOnlyList<CompileUnit> Units, IReadOnlySet<string> Compiled, IReadOnlyList<(string From, string To)> Copies);

static class BuildLog
{
    /// <summary>The `dotnet build` switch that makes it write the file <see cref="Read"/> reads.</summary>
    public static string LoggerSwitch(string path) =>
        $"\"-logger:BlazeCs.Logger.CompileLogger,{Path.Combine(AppContext.BaseDirectory, "blaze-cs-logger.dll")};{path}\"";

    public static BuildLogInfo Read(string path, string sdkDirectory)
    {
        var record = JsonSerializer.Deserialize<BuildRecord>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"{path} is empty");
        // What belongs to the SDK is compared by real path: in a dotnet root assembled from symlinks (nix,
        // or one combining two SDKs) `dotnet --list-sdks` names the link, while MSBuild loads its targets
        // from where the link points.
        var realPaths = new Dictionary<string, string>(PathComparer.Instance);
        var dotnetRoot = Path.GetDirectoryName(Path.GetDirectoryName(RealPath(sdkDirectory, realPaths)))!;
        var units = record.Compiles.Select(compile =>
            new CompileUnit(UnitName(compile.ProjectFile, compile.TargetFramework), compile.ProjectFile,
                Path.GetDirectoryName(compile.ProjectFile)!, CompilerArguments(compile.CommandLine),
                SlowPathReason(compile.AfterCompile, dotnetRoot, realPaths)));
        var compiled = record.Compiled.Select(p => CompileUnit.KeyOf(p.ProjectFile, UnitName(p.ProjectFile, p.TargetFramework)));
        return new BuildLogInfo([.. units], compiled.ToHashSet(PathComparer.Instance), [.. record.Copies.Select(c => (c.From, c.To))]);
    }

    /// <summary>A unit's name: the project's, and the target framework it was built for, if it was given one.</summary>
    static string UnitName(string projectFile, string? targetFramework) =>
        Path.GetFileNameWithoutExtension(projectFile) + (string.IsNullOrEmpty(targetFramework) ? "" : $" ({targetFramework})");

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
    static string? SlowPathReason(IEnumerable<AfterCompileTask> afterCompile, string dotnetRoot, Dictionary<string, string> realPaths) =>
        afterCompile.FirstOrDefault(t => t.TargetFile is not { } file
                || !RealPath(file, realPaths).StartsWith(dotnetRoot + Path.DirectorySeparatorChar, PathComparer.Comparison)) is { } task
            ? $"target {task.Target} ({task.Task}) rewrites the assembly after compile"
            : null;

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
}
