using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace BlazeCs;

/// <summary>
/// One project's diagnostic analyzers as csc runs them: every analyzer and diagnostic suppressor its
/// /analyzer: assemblies hold (only the suppressors under /skipanalyzers+, which RunAnalyzers=false passes),
/// with the options its .editorconfig and global config files give each file. Kept with the unit; each
/// analysis runs on the compilation it is handed, so the same analyzers serve every edit.
/// </summary>
sealed class ProjectAnalyzers(ImmutableArray<DiagnosticAnalyzer> analyzers, AnalyzerConfigOptionsProvider options)
{
    /// <summary>Whether any of them can report a diagnostic: suppressors alone only silence them.</summary>
    public bool CanReport { get; } = analyzers.Any(a => a is not DiagnosticSuppressor);

    /// <summary>
    /// What the analyzers report on these trees of the compilation, syntax and semantic, with the severities
    /// the project's options give each tree and its suppressors applied, as csc would report them for these
    /// files. Roslyn skips every analyzer whose rules are all off for these trees. Diagnostics reported only
    /// at compilation end (unused members and the like) are not among them, nor ones an analyzer reports in
    /// another file than the one it was analyzing.
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
            var syntax = await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, cancellationToken).ConfigureAwait(false);
            var model = withAnalyzers.Compilation.GetSemanticModel(tree);
            var semantic = await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, filterSpan: null, cancellationToken).ConfigureAwait(false);
            return syntax.AddRange(semantic);
        })).ConfigureAwait(false);
        return [.. perTree.SelectMany(d => d), .. failures.Values];
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
