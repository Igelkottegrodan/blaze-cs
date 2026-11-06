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
    /// Roslyn of SDK 10.0.4xx, but runs on that of 10.0.100 (Roslyn 5.0), the oldest SDK 10 and the one the
    /// fixture check tests in CI; an SDK before 10 has an older Roslyn that fails with MissingMethod and
    /// TypeLoad errors. The client checks this before it starts a daemon, so the message reaches the
    /// user's terminal.
    /// </summary>
    public static string? Unsupported(string solutionDir)
    {
        try
        {
            var version = Run(solutionDir, "--version").Trim();
            if (int.TryParse(version.Split('.')[0], out var major) && major < MinimumMajor)
                return $"needs .NET SDK {MinimumMajor}.0.100 or later; this solution's global.json selects SDK {version}. " +
                    $"Pin {MinimumMajor}.0.100 or later (or set rollForward) to use blaze-cs.";
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
        // MSBuild lives at the SDK's root, as MSBuildLocator.RegisterMSBuildPath would find it. Its own assemblies
        // resolve from there from the start, so no method that mentions an MSBuild type can be compiled before
        // they can load (see MSBuildHost); the rest of the root (NuGet, Newtonsoft.Json and the like) only once
        // MSBuild has started, so nothing else, such as a generator missing a dependency, finds it there before.
        // After Roslyn's handler: the root has its own copy of the compiler, which must not answer first.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name is not { } simple || !(s_msbuildStarted || simple.StartsWith("Microsoft.Build", StringComparison.Ordinal)))
                return null;
            var candidate = Path.Combine(sdkDir, simple + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
        s_sdkDirectory = sdkDir;
        return sdkDir;
    }

    static string? s_sdkDirectory;
    static volatile bool s_msbuildStarted;

    /// <summary>
    /// The environment MSBuild finds itself and its SDKs by, as MSBuildLocator sets it, and DOTNET_HOST_PATH as
    /// `dotnet build` sets it for tasks that start `dotnet`. Set once, before MSBuild first runs: its in-process
    /// node restores the environment it saw when a build started, so nothing may set a variable during one.
    /// Every process the daemon starts goes without them (<see cref="WithoutMSBuildEnvironment"/>): a child
    /// `dotnet build` must pick its own SDK, which a global.json edit may have changed.
    /// </summary>
    static readonly List<string> s_msbuildVariables = [];

    /// <summary>
    /// Lets the SDK's MSBuild load in this process, with the rest of the SDK root, and sets its environment:
    /// what MSBuildLocator.RegisterMSBuildPath does. Returns the SDK directory.
    /// </summary>
    public static string StartMSBuild()
    {
        var sdkDir = s_sdkDirectory ?? throw new InvalidOperationException("Roslyn was not installed from an SDK");
        if (s_msbuildStarted)
            return sdkDir;
        if (!File.Exists(Path.Combine(sdkDir, "MSBuild.dll")))
            throw new InvalidOperationException($"No MSBuild in {sdkDir}");
        lock (s_msbuildVariables)
        {
            void Set(string name, string value)
            {
                Environment.SetEnvironmentVariable(name, value);
                s_msbuildVariables.Add(name);
            }
            Set("MSBUILD_EXE_PATH", Path.Combine(sdkDir, "MSBuild.dll"));
            Set("MSBuildExtensionsPath", sdkDir);
            Set("MSBuildSDKsPath", Path.Combine(sdkDir, "Sdks"));
            if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is null && Environment.ProcessPath is { } host
                && Path.GetFileNameWithoutExtension(host) == "dotnet")
                Set("DOTNET_HOST_PATH", host);
        }
        s_msbuildStarted = true;
        return sdkDir;
    }

    /// <summary>Removes what <see cref="StartMSBuild"/> set from a child process's environment.</summary>
    public static ProcessStartInfo WithoutMSBuildEnvironment(this ProcessStartInfo start)
    {
        lock (s_msbuildVariables)
        {
            foreach (var name in s_msbuildVariables)
                start.Environment.Remove(name);
        }
        return start;
    }

    /// <summary>Simple names of the compiler's own assemblies, which analyzers must share with the daemon.</summary>
    public static IReadOnlySet<string> CompilerAssemblies { get; private set; } = new HashSet<string>();

    /// <summary>
    /// The runtime configuration the SDK's own csc runs under. Roslyn records the runtime it ran on in
    /// every PDB, so the daemon must run on that runtime too to emit the same bytes.
    /// </summary>
    public static string CompilerRuntimeConfig(string solutionDir) =>
        Path.Combine(FindSdkDirectory(solutionDir), "Roslyn", "bincore", "csc.runtimeconfig.json");

    /// <summary>The SDK `dotnet build` would pick in this directory, honouring global.json.</summary>
    internal static string FindSdkDirectory(string solutionDir)
    {
        var version = Run(solutionDir, "--version").Trim();
        foreach (var line in Run(solutionDir, "--list-sdks").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
                $"dotnet {arguments} exited with {process.ExitCode}: {error.Split('\n', StringSplitOptions.TrimEntries).FirstOrDefault(l => l.StartsWith("Requested SDK", StringComparison.Ordinal)) ?? error.Trim()}");
        return output;
    }
}

/// <summary>
/// Loads analyzer assemblies (generators) one load context per directory, so each package keeps its
/// own dependencies, while Roslyn itself and the framework stay shared with the daemon. Each assembly
/// loads from a copy under the shadow directory: Windows locks a loaded file, and a generator project in
/// the solution must stay buildable while the daemon holds its generator. Once a file a context copied
/// changes, the directory gets a new context, so the new build loads.
/// </summary>
sealed class AnalyzerLoader : Microsoft.CodeAnalysis.IAnalyzerAssemblyLoader
{
    readonly Dictionary<string, DirectoryContext> _contexts = new(PathComparer.Instance);
    readonly string _shadowDirectory;
    int _created;

    /// <param name="shadowRoot">Shared by daemons of one solution; each copies into its own subdirectory
    /// and removes the ones earlier daemons left.</param>
    public AnalyzerLoader(string shadowRoot)
    {
        if (Directory.Exists(shadowRoot))
        {
            foreach (var stale in Directory.EnumerateDirectories(shadowRoot))
            {
                try { Directory.Delete(stale, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // still loaded by a daemon on its way out
            }
        }
        _shadowDirectory = Path.Combine(shadowRoot, Environment.ProcessId.ToString());
    }

    public void AddDependencyLocation(string fullPath) { }

    public Assembly LoadFromPath(string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath)!;
        DirectoryContext? context;
        lock (_contexts)
        {
            if (!_contexts.TryGetValue(directory, out context) || context.Stale)
                _contexts[directory] = context = new DirectoryContext(directory, Path.Combine(_shadowDirectory, (_created++).ToString()));
        }
        return context.LoadCopy(fullPath);
    }

    static readonly HashSet<string> FrameworkAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(Path.GetFileNameWithoutExtension)
        .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

    sealed class DirectoryContext(string directory, string shadow) : AssemblyLoadContext($"analyzers:{directory}")
    {
        readonly Dictionary<string, FileStamp> _copied = new(PathComparer.Instance);

        /// <summary>Whether a file it copied has changed since.</summary>
        public bool Stale
        {
            get { lock (_copied) return _copied.Any(entry => FileStamp.Of(entry.Key) != entry.Value); }
        }

        public Assembly LoadCopy(string path)
        {
            var copy = Path.Combine(shadow, Path.GetFileName(path));
            lock (_copied)
            {
                if (!_copied.ContainsKey(path))
                {
                    Directory.CreateDirectory(shadow);
                    _copied[path] = FileStamp.Of(path);
                    File.Copy(path, copy);
                }
            }
            return LoadFromAssemblyPath(copy);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            // The compiler and the framework are shared: generators must see the same ISourceGenerator
            // and ImmutableArray types the driver uses, even when a package ships its own copies.
            // (Name prefixes are not enough: the Razor generator is Microsoft.CodeAnalysis.Razor.Compiler.)
            if (name.Name is { } simple && (SdkRoslyn.CompilerAssemblies.Contains(simple) || FrameworkAssemblies.Contains(simple)))
                return null;

            var candidate = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(candidate) ? LoadCopy(candidate) : null;
        }
    }
}
