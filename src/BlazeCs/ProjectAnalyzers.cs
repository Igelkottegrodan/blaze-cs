using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace BlazeCs;

/// <summary>
/// One project's diagnostic analyzers as csc runs them: every analyzer and diagnostic suppressor its
/// /analyzer: assemblies hold (only the suppressors under /skipanalyzers+, which RunAnalyzers=false passes),
/// with the options its .editorconfig and global config files give each file. Kept with the unit, with what
/// they reported for each file at its last written build; each analysis runs on the compilation it is handed.
/// </summary>
sealed class ProjectAnalyzers(ImmutableArray<DiagnosticAnalyzer> analyzers, AnalyzerConfigOptionsProvider options)
{
    /// <summary>Whether any of them can report a diagnostic: suppressors alone only silence them.</summary>
    public bool CanReport { get; } = analyzers.Any(a => a is not DiagnosticSuppressor);

    /// <summary>
    /// What each file reported at its last written build, by path: <see cref="Commit"/> sets it, so an analysis
    /// whose compile wasn't written never becomes the baseline of the next.
    /// </summary>
    readonly ConcurrentDictionary<string, FileAnalysis> _byFile = new(PathComparer.Instance);

    /// <summary>
    /// Analyzers whose diagnostics outside a member can depend on that member's body: those with symbol-start
    /// actions (a type's end action reports on its fields from what its methods do, as CA2213 and IDISP002 do)
    /// or semantic model actions (unused usings). They analyze whole files; the rest, after a body edit, only
    /// the member. Sorted on the first analysis, from the actions each registers.
    /// </summary>
    Task<(ImmutableArray<DiagnosticAnalyzer> WholeFile, ImmutableArray<DiagnosticAnalyzer> PerMember)>? _kinds;

    /// <summary>
    /// A file's analysis: the semantic diagnostics of the per-member analyzers, which a body edit updates for
    /// the member alone, and the rest (syntax, and the whole-file analyzers'), which every analysis replaces.
    /// Held detached from the compilation they came from (see <see cref="Detached"/>).
    /// </summary>
    public sealed record FileAnalysis(SyntaxTree Tree, ImmutableArray<Diagnostic> PerMember, ImmutableArray<Diagnostic> WholeFile);

    /// <summary>An analysis: what to report, and the files' analyses to <see cref="Commit"/> once its compile is written.</summary>
    public sealed record Result(ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<FileAnalysis> Files);

    /// <summary>
    /// What the analyzers report on these trees of the compilation, syntax and semantic, with the severities
    /// the project's options give each tree and its suppressors applied, as csc would report them for these
    /// files. Roslyn skips every analyzer whose rules are all off for these trees. Diagnostics reported only
    /// at compilation end (unused members and the like) are not among them, nor ones an analyzer reports in
    /// another file than the one it was analyzing.
    /// <para>
    /// A file whose edit since its last committed analysis stays inside one member's body (see
    /// <see cref="BodyEdit"/>) has the per-member analyzers run on that member only: the rest of the file keeps
    /// what they reported then, moved to where it now is. Everything else runs on the whole file.
    /// </para>
    /// </summary>
    public async Task<Result> AnalyzeAsync(
        Compilation compilation, IReadOnlyCollection<SyntaxTree> trees, ImmutableArray<AdditionalText> additionalFiles, CancellationToken cancellationToken = default)
    {
        if (!CanReport || trees.Count == 0)
            return new([], []);
        // An analyzer that throws is reported as csc reports it (AD0001), not as a failed build.
        var failures = new ConcurrentDictionary<string, Diagnostic>();
        var withAnalyzers = compilation.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(
            new AnalyzerOptions(additionalFiles, options),
            onAnalyzerException: (_, _, diagnostic) => failures.TryAdd(diagnostic.ToString(), diagnostic),
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false));
        var (wholeFile, perMember) = await LazyInitializer.EnsureInitialized(ref _kinds, () => Sort(withAnalyzers)).ConfigureAwait(false);
        var files = await Task.WhenAll(trees.Select(async tree =>
        {
            var edit = _byFile.TryGetValue(tree.FilePath, out var last) ? BodyEdit.Between(last.Tree, tree, cancellationToken) : null;
            var model = withAnalyzers.Compilation.GetSemanticModel(tree);
            // All at once: each forks the compilation. Syntax runs on the whole tree, as its tree actions do
            // even for a span (StyleCop's, on a 3,000-line file, as long as the semantic pass on one member).
            var syntax = withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, cancellationToken);
            var whole = wholeFile.IsEmpty ? Task.FromResult(ImmutableArray<Diagnostic>.Empty)
                : withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, filterSpan: null, wholeFile, cancellationToken);
            var member = perMember.IsEmpty ? Task.FromResult(ImmutableArray<Diagnostic>.Empty)
                : withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, edit?.NewMember, perMember, cancellationToken);
            var found = Detached(await member.ConfigureAwait(false), location => location);
            if (edit is not null)
                found = [.. found.Where(d => d.Location.SourceTree != tree || edit.NewMember.Contains(d.Location.SourceSpan.Start)),
                    .. Detached([.. last!.PerMember.Where(d => d.Location.SourceTree != last.Tree || !edit.OldMember.Contains(d.Location.SourceSpan.Start))],
                        location => edit.Moved(location, last.Tree, tree))];
            return new FileAnalysis(tree, found,
                Detached((await syntax.ConfigureAwait(false)).AddRange(await whole.ConfigureAwait(false)), location => location));
        })).ConfigureAwait(false);
        return new([.. files.SelectMany(f => f.PerMember.Concat(f.WholeFile)), .. failures.Values], [.. files]);
    }

    /// <summary>The build wrote the compile these analyses were of: they are the files' baselines now.</summary>
    public void Commit(IEnumerable<FileAnalysis> files)
    {
        foreach (var file in files)
            _byFile[file.Tree.FilePath] = file;
    }

    /// <summary>A file the project no longer compiles.</summary>
    public void Forget(string path) => _byFile.TryRemove(path, out _);

    /// <summary>
    /// Sorts the analyzers once, never cancelled (an abandoned analysis would leave it so for good). If the
    /// actions can't be read, every analyzer analyzes whole files.
    /// </summary>
    async Task<(ImmutableArray<DiagnosticAnalyzer>, ImmutableArray<DiagnosticAnalyzer>)> Sort(CompilationWithAnalyzers withAnalyzers)
    {
        var reporting = analyzers.Where(a => a is not DiagnosticSuppressor).ToImmutableArray();
        try
        {
            var wholeFile = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
            var perMember = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
            foreach (var analyzer in reporting)
            {
                var actions = await withAnalyzers.GetAnalyzerTelemetryInfoAsync(analyzer, CancellationToken.None).ConfigureAwait(false);
                (actions.SymbolStartActionsCount > 0 || actions.SemanticModelActionsCount > 0 ? wholeFile : perMember).Add(analyzer);
            }
            return (wholeFile.ToImmutable(), perMember.ToImmutable());
        }
        catch (Exception e)
        {
            Console.WriteLine($"{DateTime.Now:O} analyzers' actions unreadable, so they analyze whole files: {e}");
            return (reporting, []);
        }
    }

    /// <summary>
    /// The diagnostics as they print, holding nothing of the compilation they came from: the message formatted
    /// (its arguments may be symbols), source locations only (a metadata location holds its assembly's symbols),
    /// each moved as <paramref name="move"/> says.
    /// </summary>
    static ImmutableArray<Diagnostic> Detached(ImmutableArray<Diagnostic> diagnostics, Func<Location, Location> move) =>
        [.. diagnostics.Select(diagnostic =>
        {
            var descriptor = diagnostic.Descriptor;
            var message = diagnostic.GetMessage(CultureInfo.CurrentUICulture).Replace("{", "{{").Replace("}", "}}");
            var formatted = new DiagnosticDescriptor(descriptor.Id, descriptor.Title.ToString(CultureInfo.CurrentUICulture), message, descriptor.Category,
                descriptor.DefaultSeverity, descriptor.IsEnabledByDefault, description: null, descriptor.HelpLinkUri, [.. descriptor.CustomTags]);
            return Diagnostic.Create(formatted, diagnostic.Location.IsInSource ? move(diagnostic.Location) : Location.None, diagnostic.Severity,
                diagnostic.AdditionalLocations.Where(l => l.IsInSource).Select(move), diagnostic.Properties);
        })];
}

/// <summary>
/// An edit between two versions of a file that changed only the inside of one member's body: a method's,
/// constructor's, operator's or accessor's block, or an expression body. Such an edit declares nothing new,
/// so what per-member analyzers reported elsewhere in the file still holds, at a moved position after the
/// member. (Analyzers that see across members run on the whole file: <see cref="ProjectAnalyzers"/>.)
/// </summary>
sealed record BodyEdit(TextSpan OldMember, TextSpan NewMember)
{
    int Delta => NewMember.Length - OldMember.Length;

    /// <summary>The body edit that turns <paramref name="old"/> into <paramref name="new"/>, or null if the edit is anything else.</summary>
    public static BodyEdit? Between(SyntaxTree old, SyntaxTree @new, CancellationToken cancellationToken)
    {
        var (oldText, newText) = (old.GetText(cancellationToken), @new.GetText(cancellationToken));
        // The changed region: what lies between the texts' common start and common end.
        var shorter = Math.Min(oldText.Length, newText.Length);
        var prefix = 0;
        while (prefix < shorter && oldText[prefix] == newText[prefix])
            prefix++;
        var suffix = 0;
        while (suffix < shorter - prefix && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix])
            suffix++;
        var oldChange = TextSpan.FromBounds(prefix, oldText.Length - suffix);
        var newChange = TextSpan.FromBounds(prefix, newText.Length - suffix);
        // A directive (#pragma, #if, #nullable) reaches past the member.
        if (oldText.ToString(oldChange).Contains('#') || newText.ToString(newChange).Contains('#'))
            return null;
        var oldMember = MemberAround(old.GetRoot(cancellationToken), oldChange);
        var newMember = MemberAround(@new.GetRoot(cancellationToken), newChange);
        return oldMember is not null && newMember is not null && oldMember.RawKind == newMember.RawKind && oldMember.SpanStart == newMember.SpanStart
            ? new BodyEdit(oldMember.Span, newMember.Span)
            : null;
    }

    /// <summary>The member whose body holds the whole change, or null.</summary>
    static MemberDeclarationSyntax? MemberAround(SyntaxNode root, TextSpan change)
    {
        for (var node = root.FindNode(change, getInnermostNodeForTie: true); node is not null; node = node.Parent)
        {
            if (node is BlockSyntax or ArrowExpressionClauseSyntax && node.Span.Contains(change)
                && node.Parent is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or BasePropertyDeclarationSyntax)
                return node.Parent.FirstAncestorOrSelf<MemberDeclarationSyntax>();
            if (node is MemberDeclarationSyntax)
                return null;
        }
        return null;
    }

    /// <summary>A location in the old tree, outside the member, at its place in the new one.</summary>
    public Location Moved(Location location, SyntaxTree old, SyntaxTree @new) => location.SourceTree != old ? location
        : Location.Create(@new, location.SourceSpan.Start >= OldMember.End ? new TextSpan(location.SourceSpan.Start + Delta, location.SourceSpan.Length) : location.SourceSpan);
}

/// <summary>
/// Analyzer assemblies by path, shared by every unit: the source generators and the diagnostic analyzers
/// each holds, one instance of each for every unit that names the assembly, as csc's server shares them.
/// Read once, and again after the file changes: an analyzer project in the solution was built again. What
/// failed to load is kept with the assembly, for each unit that names it to report as csc does.
/// </summary>
sealed class AnalyzerCache(string shadowDirectory)
{
    readonly AnalyzerLoader _loader = new(shadowDirectory);
    readonly ConcurrentDictionary<string, Entry> _byPath = new(PathComparer.Instance);

    sealed record Entry(FileStamp Stamp, AnalyzerFileReference Reference, ConcurrentDictionary<string, Diagnostic> Failures);

    public ImmutableArray<ISourceGenerator> Generators(IEnumerable<string> analyzerPaths) =>
        [.. analyzerPaths.Distinct(PathComparer.Instance).SelectMany(path => Load(path).Reference.GetGenerators(LanguageNames.CSharp))];

    /// <param name="suppressorsOnly">csc's /skipanalyzers+: only the diagnostic suppressors run.</param>
    public ImmutableArray<DiagnosticAnalyzer> Analyzers(IEnumerable<string> analyzerPaths, bool suppressorsOnly) =>
        [.. analyzerPaths.Distinct(PathComparer.Instance)
            .SelectMany(path => Load(path).Reference.GetAnalyzers(LanguageNames.CSharp))
            .Where(analyzer => !suppressorsOnly || analyzer is DiagnosticSuppressor)];

    /// <summary>
    /// The warnings csc gives for what these assemblies failed to load or create so far (CS8032, CS8034,
    /// CS8850, CS9057), without the ones the options turn off.
    /// </summary>
    public IEnumerable<Diagnostic> LoadFailures(IEnumerable<string> analyzerPaths, CompilationOptions options) =>
        analyzerPaths.Distinct(PathComparer.Instance)
            .SelectMany(path => _byPath.TryGetValue(path, out var entry) ? entry.Failures.Values : [])
            .Where(d => !options.SpecificDiagnosticOptions.TryGetValue(d.Id, out var report) || report != ReportDiagnostic.Suppress);

    /// <summary>The assembly's reference, which creates each generator and analyzer once.</summary>
    Entry Load(string path)
    {
        var stamp = FileStamp.Of(path);
        if (_byPath.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            return cached;
        var entry = new Entry(stamp, new AnalyzerFileReference(path, _loader), new());
        entry.Reference.AnalyzerLoadFailed += (_, e) =>
        {
            if (LoadFailure(path, e) is { } diagnostic)
                entry.Failures.TryAdd(diagnostic.GetMessage(CultureInfo.InvariantCulture), diagnostic);
        };
        _byPath[path] = entry;
        return entry;
    }

    /// <summary>The warning csc gives for a load failure (CommandLineArguments.ResolveAnalyzersFromArguments), with its message.</summary>
    static Diagnostic? LoadFailure(string path, AnalyzerLoadFailureEventArgs e)
    {
        var detail = e.Exception?.Message ?? e.Message;
        var (id, message) = e.ErrorCode switch
        {
            AnalyzerLoadFailureEventArgs.FailureErrorCode.UnableToLoadAnalyzer => ("CS8034", $"Unable to load Analyzer assembly {path} : {detail}"),
            AnalyzerLoadFailureEventArgs.FailureErrorCode.UnableToCreateAnalyzer => ("CS8032", $"An instance of analyzer {e.TypeName} cannot be created from {path} : {detail}."),
            AnalyzerLoadFailureEventArgs.FailureErrorCode.ReferencesFramework => ("CS8850", $"The assembly '{path}' containing type '{e.TypeName}' references .NET Framework, which is not supported."),
            AnalyzerLoadFailureEventArgs.FailureErrorCode.ReferencesNewerCompiler => ("CS9057",
                $"The analyzer assembly '{path}' references version '{e.ReferencedCompilerVersion}' of the compiler, which is newer than the currently running version '{typeof(AnalyzerFileReference).Assembly.GetName().Version}'."),
            // NoAnalyzers: an assembly of generators only has none, and csc's own check for it is narrower.
            _ => (null, null),
        };
        return id is null ? null
            : Diagnostic.Create(id, "Compiler", message!.Replace("{", "{{").Replace("}", "}}"), DiagnosticSeverity.Warning, DiagnosticSeverity.Warning, isEnabledByDefault: true, warningLevel: 1);
    }
}
