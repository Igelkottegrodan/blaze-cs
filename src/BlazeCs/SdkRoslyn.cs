using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace BlazeCs;

/// <summary>
/// Loads Microsoft.CodeAnalysis from the solution's own SDK instead of shipping a copy, so the
/// daemon compiles with the exact compiler `dotnet build` uses and emits the same bytes.
/// Must run before any method that touches a Roslyn type is JIT-compiled.
/// </summary>
static class SdkRoslyn
{
    /// <summary>
    /// Why this solution's SDK cannot be used, or null if it can. The daemon is compiled against the
    /// Roslyn of SDK 10; an older SDK's Roslyn would fail with MissingMethod and TypeLoad errors. The
    /// client checks this before it starts a daemon, so the message reaches the user's terminal.
    /// </summary>
    public static string? Unsupported(string solutionDir)
    {
        try
        {
            var version = Run(solutionDir, "--version").Trim();
            if (int.TryParse(version.Split('.')[0], out var major) && major < MinimumMajor)
                return $"needs .NET SDK {MinimumMajor} or later; this solution's global.json selects SDK {version}. " +
                    $"Pin {MinimumMajor}.0.x (or set rollForward) to use blaze-cs.";
            return null;
        }
        catch (InvalidOperationException e)
        {
            return $"could not select a .NET SDK for this solution; check its global.json against `dotnet --list-sdks` ({e.Message})";
        }
    }

    const int MinimumMajor = 10;

    public static string Install(string solutionDir)
    {
        var sdkDir = FindSdkDirectory(solutionDir);
        var bincore = Path.Combine(sdkDir, "Roslyn", "bincore");
        if (!File.Exists(Path.Combine(bincore, "Microsoft.CodeAnalysis.dll")))
            throw new InvalidOperationException($"No Roslyn compiler in {bincore}");

        CompilerAssemblies = Directory.EnumerateFiles(bincore, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var candidate = Path.Combine(bincore, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
        return sdkDir;
    }

    /// <summary>Simple names of the compiler's own assemblies, which analyzers must share with the daemon.</summary>
    public static IReadOnlySet<string> CompilerAssemblies { get; private set; } = new HashSet<string>();

    /// <summary>The SDK `dotnet build` would pick in this directory, honouring global.json.</summary>
    static string FindSdkDirectory(string solutionDir)
    {
        var version = Run(solutionDir, "--version").Trim();
        foreach (var line in Run(solutionDir, "--list-sdks").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "10.0.403 [/path/to/sdk]"
            var space = line.IndexOf(' ');
            if (space > 0 && line[..space] == version)
                return Path.Combine(line[(space + 2)..^1].Trim(), version);
        }
        throw new InvalidOperationException($"dotnet reported SDK {version} but did not list it");
    }

    static string Run(string workingDirectory, string arguments)
    {
        var start = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"dotnet {arguments} exited with {process.ExitCode}: {error.Split('\n').FirstOrDefault(l => l.StartsWith("Requested SDK", StringComparison.Ordinal)) ?? error.Trim()}");
        return output;
    }
}

/// <summary>
/// Loads analyzer assemblies (generators) one load context per directory, so each package keeps its
/// own dependencies, while Roslyn itself and the framework stay shared with the daemon.
/// </summary>
sealed class AnalyzerLoader : Microsoft.CodeAnalysis.IAnalyzerAssemblyLoader
{
    readonly Dictionary<string, DirectoryContext> _contexts = new(StringComparer.Ordinal);

    public void AddDependencyLocation(string fullPath) { }

    public Assembly LoadFromPath(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath)!;
        DirectoryContext? context;
        lock (_contexts)
        {
            if (!_contexts.TryGetValue(directory, out context))
                _contexts[directory] = context = new DirectoryContext(directory);
        }
        return context.LoadFromAssemblyPath(fullPath);
    }

    static readonly HashSet<string> FrameworkAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(Path.GetFileNameWithoutExtension)
        .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

    sealed class DirectoryContext(string directory) : AssemblyLoadContext($"analyzers:{directory}")
    {
        protected override Assembly? Load(AssemblyName name)
        {
            // The compiler and the framework are shared: generators must see the same ISourceGenerator
            // and ImmutableArray types the driver uses, even when a package ships its own copies.
            // (Name prefixes are not enough: the Razor generator is Microsoft.CodeAnalysis.Razor.Compiler.)
            if (name.Name is { } simple && (SdkRoslyn.CompilerAssemblies.Contains(simple) || FrameworkAssemblies.Contains(simple)))
                return null;

            var candidate = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
