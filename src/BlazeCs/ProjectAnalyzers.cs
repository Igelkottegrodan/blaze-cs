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
/// they last reported for each file it analyzed; each analysis runs on the compilation it is handed.
/// </summary>
sealed class ProjectAnalyzers(ImmutableArray<DiagnosticAnalyzer> analyzers, AnalyzerConfigOptionsProvider options)
{
    /// <summary>Whether any of them can report a diagnostic: suppressors alone only silence them.</summary>
    public bool CanReport { get; } = analyzers.Any(a => a is not DiagnosticSuppressor);

    /// <summary>Each analyzed file's tree as last analyzed, and what the analyzers reported in it, by path.</summary>
    readonly ConcurrentDictionary<string, (SyntaxTree Tree, ImmutableArray<Diagnostic> Diagnostics)> _lastByFile = new(PathComparer.Instance);

    /// <summary>
    /// What the analyzers report on these trees of the compilation, syntax and semantic, with the severities
    /// the project's options give each tree and its suppressors applied, as csc would report them for these
    /// files. Roslyn skips every analyzer whose rules are all off for these trees. Diagnostics reported only
    /// at compilation end (unused members and the like) are not among them, nor ones an analyzer reports in
    /// another file than the one it was analyzing.
    /// <para>
    /// A file whose edit since its last analysis stays inside one member's body (see <see cref="BodyEdit"/>)
    /// has only that member analyzed again: the rest of the file reports what it reported then, moved to
    /// where it now is. Others are analyzed whole.
    /// </para>
    /// </summary>
    public async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        Compilation compilation, IReadOnlyCollection<SyntaxTree> trees, ImmutableArray<AdditionalText> additionalFiles, CancellationToken cancellationToken = default)
    {
        if (!CanReport || trees.Count == 0)
            return [];
        // An analyzer that throws is reported as csc reports it (AD0001), not as a failed build.
        var failures = new ConcurrentDictionary<string, Diagnostic>();
        var withAnalyzers = compilation.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(
            new AnalyzerOptions(additionalFiles, options),
            onAnalyzerException: (_, _, diagnostic) => failures.TryAdd(diagnostic.ToString(), diagnostic),
            concurrentAnalysis: true,
            logAnalyzerExecutionTime: false));
        var perTree = await Task.WhenAll(trees.Select(async tree =>
        {
            var edit = _lastByFile.TryGetValue(tree.FilePath, out var last) ? BodyEdit.Between(last.Tree, tree, cancellationToken) : null;
            var span = edit?.NewMember;
            // Both at once: each forks the compilation, and syntax-tree actions run over the whole tree
            // even for a span (StyleCop's, on a 3,000-line file, as long as the semantic pass on one member).
            var syntax = withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, span, cancellationToken);
            var semantic = withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(withAnalyzers.Compilation.GetSemanticModel(tree), span, cancellationToken);
            var found = (await syntax.ConfigureAwait(false)).AddRange(await semantic.ConfigureAwait(false));
            if (edit is not null)
                found = [.. found.Where(d => d.Location.SourceTree != tree || edit.NewMember.Contains(d.Location.SourceSpan.Start)),
                    .. last.Diagnostics.Where(d => d.Location.SourceTree == last.Tree && !edit.OldMember.Contains(d.Location.SourceSpan.Start))
                        .Select(d => edit.Moved(d, last.Tree, tree))];
            _lastByFile[tree.FilePath] = (tree, found);
            return found;
        })).ConfigureAwait(false);
        return [.. perTree.SelectMany(d => d), .. failures.Values];
    }
}

/// <summary>
/// An edit between two versions of a file that changed only the inside of one member's body: a method's,
/// constructor's, operator's or accessor's block, or an expression body. Such an edit declares nothing new,
/// so what analyzers reported elsewhere in the file still holds, at a moved position after the member.
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

    /// <summary>
    /// A diagnostic from the old tree, outside the member, at its place in the new one. Its message is kept as
    /// it was formatted: the arguments it was formatted from aren't public.
    /// </summary>
    public Diagnostic Moved(Diagnostic diagnostic, SyntaxTree old, SyntaxTree @new)
    {
        Location Move(Location location) => location.SourceTree != old ? location
            : Location.Create(@new, location.SourceSpan.Start >= OldMember.End ? new TextSpan(location.SourceSpan.Start + Delta, location.SourceSpan.Length) : location.SourceSpan);
        var descriptor = diagnostic.Descriptor;
        var message = diagnostic.GetMessage(CultureInfo.CurrentUICulture).Replace("{", "{{").Replace("}", "}}");
        var formatted = new DiagnosticDescriptor(descriptor.Id, descriptor.Title, message, descriptor.Category, descriptor.DefaultSeverity,
            descriptor.IsEnabledByDefault, descriptor.Description, descriptor.HelpLinkUri, [.. descriptor.CustomTags]);
        return Diagnostic.Create(formatted, Move(diagnostic.Location), diagnostic.Severity, diagnostic.AdditionalLocations.Select(Move), diagnostic.Properties);
    }
}

/// <summary>
/// Analyzer assemblies by path, shared by every unit: the source generators and the diagnostic analyzers
/// each holds, one instance of each for every unit that names the assembly, as csc's server shares them.
/// Read once, and again after the file changes: an analyzer project in the solution was built again.
/// </summary>
sealed class AnalyzerCache(string shadowDirectory)
{
    readonly AnalyzerLoader _loader = new(shadowDirectory);
    readonly ConcurrentDictionary<string, (FileStamp Stamp, AnalyzerFileReference Reference)> _byPath = new(PathComparer.Instance);

    public ImmutableArray<ISourceGenerator> Generators(IEnumerable<string> analyzerPaths) =>
        [.. analyzerPaths.Distinct(PathComparer.Instance).SelectMany(path => Load(path).GetGenerators(LanguageNames.CSharp))];

    /// <param name="suppressorsOnly">csc's /skipanalyzers+: only the diagnostic suppressors run.</param>
    public ImmutableArray<DiagnosticAnalyzer> Analyzers(IEnumerable<string> analyzerPaths, bool suppressorsOnly) =>
        [.. analyzerPaths.Distinct(PathComparer.Instance)
            .SelectMany(path => Load(path).GetAnalyzers(LanguageNames.CSharp))
            .Where(analyzer => !suppressorsOnly || analyzer is DiagnosticSuppressor)];

    /// <summary>The assembly's reference, which creates each generator and analyzer once.</summary>
    AnalyzerFileReference Load(string path)
    {
        var stamp = FileStamp.Of(path);
        if (_byPath.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            return cached.Reference;
        var reference = new AnalyzerFileReference(path, _loader);
        _byPath[path] = (stamp, reference);
        return reference;
    }
}
