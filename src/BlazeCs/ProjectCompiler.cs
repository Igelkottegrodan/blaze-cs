using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace BlazeCs;

/// <summary>The bytes one compile produced, before any of them reach the disk.</summary>
sealed record EmitOutput(byte[] Assembly, byte[]? Pdb, byte[]? Documentation, byte[]? ReferenceAssembly);

/// <summary>
/// One compile unit kept warm: parsed syntax trees, its Roslyn compilation and the generator driver's
/// caches. Built from csc's own arguments so that what it emits matches what csc would emit.
/// </summary>
sealed class ProjectCompiler
{
    readonly CSharpCommandLineArguments _args;
    readonly ProjectConfig _config;
    readonly HashSet<string> _embedded;
    readonly Dictionary<string, SourceFile> _sources = new(PathComparer.Instance);
    /// <summary>Source paths in compile order, which decides metadata order and so the output bytes.</summary>
    readonly List<string> _order;
    readonly Dictionary<string, FileStamp> _additionalFiles = new(PathComparer.Instance);
    readonly Dictionary<string, AdditionalText> _additionalTexts = new(PathComparer.Instance);
    readonly Dictionary<string, FileStamp> _inputs = new(PathComparer.Instance);
    readonly HashSet<string> _ignoredOnDisk;
    /// <summary>Extensions the SDK's globs turn into additional files: .razor and .cshtml under the Razor SDK.</summary>
    readonly string[] _globbedAdditional;
    readonly HashSet<string> _ignoredAdditionalOnDisk;
    readonly Func<CommandLineReference, MetadataReference> _resolveReference;
    CSharpCompilation _compilation;
    GeneratorDriver? _driver;

    public CompileUnit Unit { get; }
    /// <summary>How long the last Emit spent in source generators (the rest is binding and emit).</summary>
    public TimeSpan LastGeneratorTime { get; private set; }
    public string AssemblyPath { get; }
    public string? PdbPath { get; }
    public string? DocumentationPath => _args.DocumentationPath;
    public string? ReferenceAssemblyPath => _args.OutputRefFilePath;
    /// <summary>What dependents compile against: the reference assembly when there is one.</summary>
    public string ImagePath => ReferenceAssemblyPath ?? AssemblyPath;
    public IEnumerable<CommandLineReference> References => _args.MetadataReferences;
    public IEnumerable<string> AnalyzerPaths => _args.AnalyzerReferences.Select(a => a.FilePath);
    /// <summary>Config files whose change means csc's arguments are stale (handled by a real build).</summary>
    public IEnumerable<string> ConfigPaths => _args.AnalyzerConfigPaths;
    /// <summary>
    /// Set by Refresh when csc's arguments are out of date: a globbed additional file appeared or went
    /// away. Only MSBuild can list it, with the per-file metadata (TargetPath, CssScope) that the Razor
    /// generator reads from the .editorconfig MSBuild generates.
    /// </summary>
    public string? StaleArguments { get; private set; }

    public ProjectCompiler(CompileUnit unit, Func<CommandLineReference, MetadataReference> resolveReference, GeneratorCache generators)
    {
        Unit = unit;
        _resolveReference = resolveReference;
        _args = CSharpCommandLineParser.Default.Parse(unit.Arguments, unit.ProjectDirectory, sdkDirectory: null);
        var errors = _args.Errors.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException($"{unit.Name}: cannot read csc arguments: {string.Join("; ", errors)}");

        var outputName = _args.OutputFileName ?? throw new InvalidOperationException($"{unit.Name}: no /out:");
        AssemblyPath = _args.GetOutputFilePath(outputName);
        PdbPath = _args.EmitPdb && _args.EmitOptions.DebugInformationFormat != DebugInformationFormat.Embedded
            ? _args.GetPdbFilePath(outputName)
            : null;

        _config = new ProjectConfig(_args.AnalyzerConfigPaths);
        _embedded = _args.EmbeddedFiles.Select(f => f.Path).ToHashSet(PathComparer.Instance);

        var trees = new SyntaxTree[_args.SourceFiles.Length];
        Parallel.For(0, trees.Length, i =>
        {
            var file = SourceFile.Read(_args.SourceFiles[i].Path, this);
            lock (_sources)
                _sources[file.Path] = file;
            trees[i] = file.Tree;
        });
        _order = _args.SourceFiles.Select(f => f.Path).ToList();
        _ignoredOnDisk = DiscoverFiles([".cs"]).Where(path => !_sources.ContainsKey(path)).ToHashSet(PathComparer.Instance);

        foreach (var file in _args.AdditionalFiles)
        {
            _additionalFiles[file.Path] = FileStamp.Of(file.Path);
            _additionalTexts[file.Path] = FileText.Read(file.Path, _args.Encoding, _args.ChecksumAlgorithm);
        }
        _globbedAdditional = AnalyzerPaths.Any(p => Path.GetFileName(p) == "Microsoft.CodeAnalysis.Razor.Compiler.dll") ? [".razor", ".cshtml"] : [];
        _ignoredAdditionalOnDisk = DiscoverFiles(_globbedAdditional).Where(path => !_additionalFiles.ContainsKey(path)).ToHashSet(PathComparer.Instance);
        foreach (var resource in ResourcePaths())
            _inputs[resource] = FileStamp.Of(resource);

        _compilation = CSharpCompilation.Create(
            _args.CompilationName,
            trees,
            _args.MetadataReferences.Select(resolveReference),
            CompilationOptions());

        var sourceGenerators = generators.For(_args.AnalyzerReferences.Select(a => a.FilePath));
        if (sourceGenerators.Length > 0)
        {
            var baseDirectory = string.IsNullOrWhiteSpace(_args.GeneratedFilesOutputDirectory)
                ? _args.OutputDirectory
                : _args.GeneratedFilesOutputDirectory;
            _driver = CSharpGeneratorDriver.Create(
                sourceGenerators,
                _additionalTexts.Values,
                _args.ParseOptions,
                _config.GeneratorOptions(),
                DriverOptions(baseDirectory));
        }
    }

    /// <summary>
    /// csc also hands the driver its checksum algorithm, which lands in the PDB entries of generated
    /// files; Roslyn keeps that setter internal, so it is set the way csc's own assembly would.
    /// </summary>
    GeneratorDriverOptions DriverOptions(string? baseDirectory)
    {
        object options = new GeneratorDriverOptions(IncrementalGeneratorOutputKind.Host, trackIncrementalGeneratorSteps: false, baseDirectory: baseDirectory);
        typeof(GeneratorDriverOptions)
            .GetProperty("ChecksumAlgorithm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(options, _args.ChecksumAlgorithm);
        return (GeneratorDriverOptions)options;
    }

    CSharpCompilationOptions CompilationOptions()
    {
        var options = _args.CompilationOptions
            .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
            .WithXmlReferenceResolver(new XmlFileResolver(_args.BaseDirectory))
            .WithSourceReferenceResolver(new SourceFileResolver(ImmutableArray<string>.Empty, _args.BaseDirectory, _args.PathMap))
            .WithStrongNameProvider(new DesktopStrongNameProvider(_args.KeyFileSearchPaths))
            .WithSyntaxTreeOptionsProvider(_config.TreeOptions());

        // Warnings stay warnings here (see ProjectConfig.TreeOptions); `dotnet build` has the final say.
        // Diagnostic options never change the emitted bytes.
        if (options.GeneralDiagnosticOption == ReportDiagnostic.Error)
            options = options.WithGeneralDiagnosticOption(ReportDiagnostic.Default);
        return options.WithSpecificDiagnosticOptions(options.SpecificDiagnosticOptions
            .Select(o => KeyValuePair.Create(o.Key, o.Value == ReportDiagnostic.Error ? ReportDiagnostic.Warn : o.Value)));
    }

    /// <summary>Brings the compilation up to date with the disk; true if anything it compiles from changed.</summary>
    public bool Refresh()
    {
        var changed = false;
        var filesAddedOrRemoved = false;
        StaleArguments = null;
        var compilation = _compilation;

        foreach (var file in _sources.Values.ToList())
        {
            if (!File.Exists(file.Path))
            {
                _sources.Remove(file.Path);
                _order.Remove(file.Path);
                filesAddedOrRemoved = true;
                continue;
            }
            if (FileStamp.Of(file.Path) == file.Stamp)
                continue;

            var updated = SourceFile.Read(file.Path, this);
            if (updated.Tree.GetText().ContentEquals(file.Tree.GetText()))
            {
                // Touched, not changed: keep the tree the compilation already has.
                _sources[file.Path] = file with { Stamp = updated.Stamp };
                continue;
            }
            _sources[file.Path] = updated;
            compilation = compilation.ReplaceSyntaxTree(file.Tree, updated.Tree);
            changed = true;
        }

        foreach (var path in DiscoverFiles([".cs"]))
        {
            if (_sources.ContainsKey(path) || _ignoredOnDisk.Contains(path))
                continue;
            _sources[path] = SourceFile.Read(path, this);
            _order.Insert(GlobPosition(path), path);
            filesAddedOrRemoved = true;
        }

        if (filesAddedOrRemoved)
        {
            compilation = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(_order.Select(path => _sources[path].Tree));
            changed = true;
        }

        foreach (var (path, stamp) in _additionalFiles.ToList())
        {
            var current = FileStamp.Of(path);
            if (current == stamp)
                continue;
            if (!File.Exists(path))
            {
                StaleArguments = $"{Path.GetRelativePath(Unit.ProjectDirectory, path)} was deleted";
                changed = true;
                continue;
            }
            _additionalFiles[path] = current;
            var text = FileText.Read(path, _args.Encoding, _args.ChecksumAlgorithm);
            if (_driver is not null)
                _driver = _driver.ReplaceAdditionalText(_additionalTexts[path], text);
            _additionalTexts[path] = text;
            changed = true;
        }

        if (DiscoverFiles(_globbedAdditional).FirstOrDefault(p => !_additionalFiles.ContainsKey(p) && !_ignoredAdditionalOnDisk.Contains(p)) is { } added)
        {
            StaleArguments = $"{Path.GetRelativePath(Unit.ProjectDirectory, added)} is new";
            changed = true;
        }

        foreach (var (path, stamp) in _inputs.ToList())
        {
            var current = FileStamp.Of(path);
            if (current == stamp)
                continue;
            _inputs[path] = current;
            changed = true;
        }

        _compilation = compilation;
        return changed;
    }

    /// <summary>Picks up new images of the units this one references; true if any reference changed.</summary>
    public bool RefreshReferences()
    {
        var references = _args.MetadataReferences.Select(_resolveReference).ToList();
        if (references.SequenceEqual(_compilation.References))
            return false;
        _compilation = _compilation.WithReferences(references);
        return true;
    }

    /// <summary>
    /// The compilation as csc would emit it: generators run, embedded texts gathered. Dependents of an
    /// API change prepare first to check whether they need to emit at all.
    /// </summary>
    public sealed record Prepared(Compilation Compilation, List<EmbeddedText> EmbeddedTexts, ImmutableArray<Diagnostic> Diagnostics);

    public Prepared Prepare(CancellationToken cancellationToken = default)
    {
        var compilation = (Compilation)_compilation;
        var generatorDiagnostics = ImmutableArray<Diagnostic>.Empty;
        var embeddedTexts = _args.EmbeddedFiles
            .Select(f => _sources.TryGetValue(f.Path, out var source)
                ? EmbeddedText.FromSource(f.Path, source.Tree.GetText(cancellationToken))
                : EmbeddedFromDisk(f.Path))
            .ToList();

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_driver is not null)
        {
            var before = compilation.SyntaxTrees.Count();
            _driver = _driver.RunGeneratorsAndUpdateCompilation(compilation, out compilation, out generatorDiagnostics, cancellationToken);
            // csc embeds every generated file in the PDB: they exist nowhere else.
            embeddedTexts.AddRange(compilation.SyntaxTrees.Skip(before)
                .Select(tree => EmbeddedText.FromSource(tree.FilePath, tree.GetText(cancellationToken))));
        }
        LastGeneratorTime = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        return new Prepared(compilation, embeddedTexts, generatorDiagnostics);
    }

    /// <summary>Everything csc would write, into memory.</summary>
    public (EmitOutput? Output, ImmutableArray<Diagnostic> Diagnostics) Emit(Prepared prepared, CancellationToken cancellationToken = default)
    {
        var compilation = prepared.Compilation;
        using var assembly = new MemoryStream();
        using var pdb = PdbPath is null ? null : new MemoryStream();
        using var documentation = DocumentationPath is null ? null : new MemoryStream();
        using var referenceAssembly = ReferenceAssemblyPath is null ? null : new MemoryStream();
        using var win32Resources = Win32Resources(compilation);
        using var sourceLink = _args.SourceLink is null ? null : File.OpenRead(_args.SourceLink);

        var result = compilation.Emit(
            assembly,
            pdb,
            documentation,
            win32Resources,
            _args.ManifestResources,
            EmitOptions(),
            debugEntryPoint: null,
            sourceLink,
            prepared.EmbeddedTexts,
            referenceAssembly,
            cancellationToken: cancellationToken);

        var diagnostics = prepared.Diagnostics.AddRange(result.Diagnostics);
        if (!result.Success)
            return (null, diagnostics);
        return (new EmitOutput(assembly.ToArray(), pdb?.ToArray(), documentation?.ToArray(), referenceAssembly?.ToArray()), diagnostics);
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SyntaxTree, HashSet<string>> IdentifierCache = new();
    List<SourceText> _generated = [];

    /// <summary>
    /// Whether any source file, generated ones and doc comments included, contains one of these
    /// identifiers. A dependent that names none of the members an API change touched binds the same.
    /// </summary>
    public static bool Mentions(Compilation compilation, IReadOnlySet<string> names) =>
        compilation.SyntaxTrees.Any(tree => Identifiers(tree).Overlaps(names));

    static HashSet<string> Identifiers(SyntaxTree tree) =>
        IdentifierCache.GetValue(tree, t => t.GetRoot()
            .DescendantTokens(descendIntoTrivia: true)
            .Where(token => token.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken))
            .Select(token => token.ValueText)
            .ToHashSet(StringComparer.Ordinal));

    /// <summary>Indexes every tree's identifiers ahead of the first edit.</summary>
    public static void IndexIdentifiers(Compilation compilation)
    {
        foreach (var tree in compilation.SyntaxTrees)
            Identifiers(tree);
    }

    /// <summary>
    /// Whether the generators' output differs from the last preparation. Generators read referenced
    /// symbols freely, so a reference change that reaches generated code needs a full compile.
    /// </summary>
    public bool GeneratedChanged(Prepared prepared)
    {
        var generated = prepared.Compilation.SyntaxTrees.Skip(_compilation.SyntaxTrees.Count()).Select(t => t.GetText()).ToList();
        var changed = generated.Count != _generated.Count || generated.Zip(_generated).Any(pair => !pair.First.ContentEquals(pair.Second));
        _generated = generated;
        return changed;
    }

    EmitOptions EmitOptions()
    {
        var outputName = _args.OutputFileName!;
        var options = _args.EmitOptions
            .WithOutputNameOverride(outputName)
            .WithPdbFilePath(MapPath(_args.GetPdbFilePath(outputName)));
        if (_args.ParseOptions.Features.ContainsKey("pdb-path-determinism") && !string.IsNullOrEmpty(options.PdbFilePath))
            options = options.WithPdbFilePath(Path.GetFileName(options.PdbFilePath));
        return options;
    }

    EmbeddedText EmbeddedFromDisk(string path)
    {
        using var stream = File.OpenRead(path);
        return EmbeddedText.FromStream(path, stream, _args.ChecksumAlgorithm);
    }

    Stream? Win32Resources(Compilation compilation)
    {
        if (_args.Win32ResourceFile is { } file)
            return File.OpenRead(Path.GetFullPath(file, _args.BaseDirectory!));
        using var manifest = _args.Win32Manifest is { } m ? File.OpenRead(Path.GetFullPath(m, _args.BaseDirectory!)) : null;
        using var icon = _args.Win32Icon is { } i ? File.OpenRead(Path.GetFullPath(i, _args.BaseDirectory!)) : null;
        return compilation.CreateDefaultWin32Resources(versionResource: true, _args.NoWin32Manifest, manifest, icon);
    }

    /// <summary>csc's /pathmap prefix replacement (PathUtilities.NormalizePathPrefix).</summary>
    string MapPath(string path)
    {
        foreach (var (prefix, replacement) in _args.PathMap)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
                return replacement + path[prefix.Length..].Replace('\\', '/');
        }
        return path;
    }

    IEnumerable<string> ResourcePaths()
    {
        // ResourceDescription hides its file; the /resource: arguments still name it.
        foreach (var argument in Unit.Arguments)
        {
            if (!argument.StartsWith("/resource:", StringComparison.Ordinal) && !argument.StartsWith("/linkresource:", StringComparison.Ordinal))
                continue;
            var path = argument[(argument.IndexOf(':') + 1)..].Split(',')[0].Trim('"');
            yield return Path.GetFullPath(path, Unit.ProjectDirectory);
        }
    }

    /// <summary>
    /// Where MSBuild would put a file its default glob picks up: the glob's files come first, ordered by
    /// relative path ignoring case (so on every jellyfin project), then explicit and generated items.
    /// </summary>
    int GlobPosition(string path)
    {
        static string Key(string relative) => relative.ToLowerInvariant();
        var key = Key(Path.GetRelativePath(Unit.ProjectDirectory, path));
        var position = 0;
        foreach (var existing in _order)
        {
            var relative = Path.GetRelativePath(Unit.ProjectDirectory, existing);
            var inGlob = !relative.StartsWith("..", StringComparison.Ordinal) && !relative.StartsWith("obj" + Path.DirectorySeparatorChar, PathComparer.Comparison);
            if (!inGlob || string.CompareOrdinal(Key(relative), key) > 0)
                break;
            position++;
        }
        return position;
    }

    /// <summary>
    /// The files with these extensions that the SDK's default globs would pick up: everything under the
    /// project directory except bin/, obj/, hidden directories and nested projects.
    /// </summary>
    IEnumerable<string> DiscoverFiles(string[] extensions)
    {
        if (extensions.Length == 0)
            yield break;
        var pending = new Stack<string>([Unit.ProjectDirectory]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in extensions.SelectMany(extension => Directory.EnumerateFiles(directory, "*" + extension)))
                yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.')
                    || (directory == Unit.ProjectDirectory && name is "bin" or "obj")
                    || Directory.EnumerateFiles(child, "*.csproj").Any())
                    continue;
                pending.Push(child);
            }
        }
    }

    sealed record SourceFile(string Path, FileStamp Stamp, SyntaxTree Tree)
    {
        public static SourceFile Read(string path, ProjectCompiler owner)
        {
            var stamp = FileStamp.Of(path);
            using var stream = File.OpenRead(path);
            var text = SourceText.From(stream, owner._args.Encoding, owner._args.ChecksumAlgorithm,
                throwIfBinaryDetected: true, canBeEmbedded: owner._embedded.Contains(path));
            var tree = CSharpSyntaxTree.ParseText(text, owner._args.ParseOptions, path);
            return new SourceFile(path, stamp, tree);
        }
    }
}

/// <summary>A file as of when it was read: a different length or write time means it changed.</summary>
readonly record struct FileStamp(long Length, DateTime Modified)
{
    public static FileStamp Of(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc) : default;
    }
}
