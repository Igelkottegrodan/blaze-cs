using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace BlazeCs;

/// <summary>
/// The .editorconfig and .globalconfig files of one project, answering what csc's internal
/// providers answer: options for generators and analyzers, and per-file severities for compiler and
/// analyzer warnings.
/// </summary>
sealed class ProjectConfig
{
    readonly AnalyzerConfigSet? _set;
    readonly ConcurrentDictionary<string, AnalyzerConfigOptionsResult> _byPath = new(PathComparer.Instance);

    public ProjectConfig(IEnumerable<string> configPaths)
    {
        var configs = configPaths
            .Select(path => AnalyzerConfig.Parse(SourceText.From(File.ReadAllText(path)), path))
            .ToList();
        _set = configs.Count == 0 ? null : AnalyzerConfigSet.Create(configs);
    }

    public AnalyzerConfigOptionsResult? For(string path) =>
        _set is null ? null : _byPath.GetOrAdd(path, p => _set.GetOptionsForSourcePath(p));

    public AnalyzerConfigOptionsResult? Global => _set?.GlobalConfigOptions;

    /// <summary>The options generators and analyzers read, per file and global.</summary>
    public AnalyzerConfigOptionsProvider Options() => new OptionsProvider(this);

    /// <summary>
    /// Severities from config files, with errors lowered to warnings: the fast path never fails a
    /// build over a warning, because not all that might silence it runs (suppressors see only the
    /// analyzer diagnostics of edited files, not compiler warnings).
    /// </summary>
    public SyntaxTreeOptionsProvider TreeOptions() => new TreeOptionsProvider(this);

    sealed class OptionsProvider(ProjectConfig config) : AnalyzerConfigOptionsProvider
    {
        // csc reads global options as the options of the empty path.
        readonly AnalyzerConfigOptions _global = Options(config.For(string.Empty));

        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options(config.For(tree.FilePath));
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options(config.For(textFile.Path));

        static AnalyzerConfigOptions Options(AnalyzerConfigOptionsResult? result) =>
            new DictionaryOptions(result?.AnalyzerOptions ?? ImmutableDictionary<string, string>.Empty);
    }

    sealed class DictionaryOptions(ImmutableDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => values.TryGetValue(key, out value);
        public override IEnumerable<string> Keys => values.Keys;
    }

    sealed class TreeOptionsProvider(ProjectConfig config) : SyntaxTreeOptionsProvider
    {
        public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken) =>
            config.For(tree.FilePath)?.AnalyzerOptions.TryGetValue("generated_code", out var value) == true
                ? (bool.TryParse(value, out var generated) && generated ? GeneratedKind.MarkedGenerated : GeneratedKind.NotGenerated)
                : GeneratedKind.Unknown;

        public override bool TryGetDiagnosticValue(SyntaxTree tree, string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity) =>
            Lowered(config.For(tree.FilePath)?.TreeOptions, diagnosticId, out severity);

        public override bool TryGetGlobalDiagnosticValue(string diagnosticId, CancellationToken cancellationToken, out ReportDiagnostic severity) =>
            Lowered(config.Global?.TreeOptions, diagnosticId, out severity);

        static bool Lowered(ImmutableDictionary<string, ReportDiagnostic>? options, string id, out ReportDiagnostic severity)
        {
            if (options is null || !options.TryGetValue(id, out severity))
            {
                severity = default;
                return false;
            }
            if (severity == ReportDiagnostic.Error)
                severity = ReportDiagnostic.Warn;
            return true;
        }
    }
}

/// <summary>
/// An /additionalfile: read from disk, as csc does, with csc's encoding and checksum algorithm. Generators
/// see the checksum: Razor writes it into a #pragma checksum, and so into the PDB and the assembly's PDB ID.
/// </summary>
sealed class FileText(string path, SourceText text) : AdditionalText
{
    public override string Path => path;
    public override SourceText GetText(CancellationToken cancellationToken = default) => text;

    public static FileText Read(string path, System.Text.Encoding? encoding, SourceHashAlgorithm checksumAlgorithm)
    {
        using var stream = File.OpenRead(path);
        return new(path, SourceText.From(stream, encoding, checksumAlgorithm));
    }
}
