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
    /// Whether the config files make a diagnostic an error in csc, for this file or globally, before the daemon
    /// lowers it (see <see cref="TreeOptions"/>).
    /// </summary>
    public bool MakesError(string? path, string diagnosticId) =>
        path is not null && For(path)?.TreeOptions.GetValueOrDefault(diagnosticId) == ReportDiagnostic.Error
        || Global?.TreeOptions.GetValueOrDefault(diagnosticId) == ReportDiagnostic.Error;

    /// <summary>
    /// Whether csc could report a diagnostic of this descriptor as a warning or an error on any of these files
    /// (or globally). Without /errorlog, csc runs no analyzer whose every diagnostic is hidden, info or off
    /// there (AnalyzerDriver's severity filter), and prints no hidden or info diagnostic. Per file, the
    /// severity is the first that applies: the id's in a config file (the file's, then the global one's), on
    /// the command line, the category's bulk setting, the bulk setting for all analyzer rules, then the
    /// descriptor's default. Bulk settings apply only to rules enabled by default and configurable, as in Roslyn.
    /// </summary>
    /// <param name="commandLine">The compilation's specific diagnostic options, unlowered.</param>
    public Func<DiagnosticDescriptor, bool> MayWarn(IEnumerable<string> paths, ImmutableDictionary<string, ReportDiagnostic> commandLine)
    {
        var global = Global;
        // Few distinct option sets cover the files: one per combination of matching sections.
        List<AnalyzerConfigOptionsResult?> scopes = [.. paths.Select(For).DistinctBy(r => (r?.TreeOptions, r?.AnalyzerOptions))];
        if (scopes.Count == 0)
            scopes.Add(null);

        ReportDiagnostic Severity(DiagnosticDescriptor descriptor, AnalyzerConfigOptionsResult? file)
        {
            var tags = descriptor.CustomTags.ToList();
            if (!tags.Contains(WellKnownDiagnosticTags.NotConfigurable))
            {
                if (file?.TreeOptions.TryGetValue(descriptor.Id, out var severity) == true
                    || global?.TreeOptions.TryGetValue(descriptor.Id, out severity) == true
                    || commandLine.TryGetValue(descriptor.Id, out severity) && severity != ReportDiagnostic.Default)
                    return severity;
                if (descriptor.IsEnabledByDefault && !tags.Contains(WellKnownDiagnosticTags.Compiler))
                {
                    foreach (var key in (string[])[$"dotnet_analyzer_diagnostic.category-{descriptor.Category}.severity", "dotnet_analyzer_diagnostic.severity"])
                    {
                        if ((file?.AnalyzerOptions.TryGetValue(key, out var value) == true || global?.AnalyzerOptions.TryGetValue(key, out value) == true)
                            && Bulk(value!) is { } bulk)
                            return bulk;
                    }
                }
            }
            return !descriptor.IsEnabledByDefault ? ReportDiagnostic.Suppress : descriptor.DefaultSeverity switch
            {
                DiagnosticSeverity.Error => ReportDiagnostic.Error,
                DiagnosticSeverity.Warning => ReportDiagnostic.Warn,
                DiagnosticSeverity.Info => ReportDiagnostic.Info,
                _ => ReportDiagnostic.Hidden,
            };
        }

        return descriptor => scopes.Any(file => Severity(descriptor, file) is ReportDiagnostic.Warn or ReportDiagnostic.Error);
    }

    /// <summary>A bulk severity setting's value, as .editorconfig spells it; null for default or an unknown one.</summary>
    static ReportDiagnostic? Bulk(string value) => value.Trim().ToLowerInvariant() switch
    {
        "error" => ReportDiagnostic.Error,
        "warning" => ReportDiagnostic.Warn,
        "suggestion" => ReportDiagnostic.Info,
        "silent" or "refactoring" => ReportDiagnostic.Hidden,
        "none" => ReportDiagnostic.Suppress,
        _ => null,
    };

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
