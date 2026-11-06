using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// Which analyzer diagnostics csc could report as a warning or an error (<see cref="ProjectConfig.MayWarn"/>):
/// csc without /errorlog runs no analyzer whose every diagnostic is hidden, info or off, and a compile through
/// the daemon's pipe runs the same analyzers.
/// </summary>
public sealed class AnalyzersCscRunsTests : IDisposable
{
    readonly string _directory = Directory.CreateTempSubdirectory("blaze-cs-config-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    static DiagnosticDescriptor Rule(DiagnosticSeverity severity, bool enabledByDefault = true, params string[] tags) =>
        new("XY0001", "title", "message", "Usage", severity, enabledByDefault, customTags: tags);

    /// <param name="sections">.editorconfig lines for A.cs.</param>
    /// <param name="b">.editorconfig lines for B.cs, a second file of the project.</param>
    Func<DiagnosticDescriptor, bool> MayWarn(string sections = "", string b = "", string? global = null, ImmutableDictionary<string, ReportDiagnostic>? commandLine = null)
    {
        var editorConfig = Path.Combine(_directory, ".editorconfig");
        File.WriteAllText(editorConfig, $"root = true\n\n[A.cs]\n{sections}\n\n[B.cs]\n{b}\n");
        List<string> paths = [editorConfig];
        if (global is not null)
        {
            paths.Add(Path.Combine(_directory, "project.globalconfig"));
            File.WriteAllText(paths[^1], $"is_global = true\n{global}\n");
        }
        return new ProjectConfig(paths).MayWarn([Path.Combine(_directory, "A.cs"), Path.Combine(_directory, "B.cs")],
            commandLine ?? ImmutableDictionary<string, ReportDiagnostic>.Empty);
    }

    [Fact]
    public void DefaultSeverity()
    {
        Assert.True(MayWarn()(Rule(DiagnosticSeverity.Warning)));
        Assert.True(MayWarn()(Rule(DiagnosticSeverity.Error)));
        Assert.False(MayWarn()(Rule(DiagnosticSeverity.Info)));
        Assert.False(MayWarn()(Rule(DiagnosticSeverity.Hidden)));
        Assert.False(MayWarn()(Rule(DiagnosticSeverity.Warning, enabledByDefault: false)));
    }

    [Fact]
    public void IdSeverityInAConfigFile()
    {
        Assert.False(MayWarn("dotnet_diagnostic.XY0001.severity = suggestion", "dotnet_diagnostic.XY0001.severity = none")(Rule(DiagnosticSeverity.Warning)));
        Assert.True(MayWarn("dotnet_diagnostic.XY0001.severity = warning")(Rule(DiagnosticSeverity.Info)));
        Assert.True(MayWarn("dotnet_diagnostic.XY0001.severity = error")(Rule(DiagnosticSeverity.Warning, enabledByDefault: false)));
        Assert.True(MayWarn(global: "dotnet_diagnostic.XY0001.severity = warning")(Rule(DiagnosticSeverity.Hidden)));
    }

    /// <summary>One file where it can warn is enough: csc runs the analyzer for the whole compilation.</summary>
    [Fact]
    public void AnyFileOfTheProject()
    {
        Assert.True(MayWarn("dotnet_diagnostic.XY0001.severity = none", "dotnet_diagnostic.XY0001.severity = warning")(Rule(DiagnosticSeverity.Info)));
    }

    [Fact]
    public void BulkSeverities()
    {
        Assert.True(MayWarn("dotnet_analyzer_diagnostic.category-Usage.severity = warning")(Rule(DiagnosticSeverity.Info)));
        Assert.False(MayWarn("dotnet_analyzer_diagnostic.category-Design.severity = warning")(Rule(DiagnosticSeverity.Info)));
        Assert.True(MayWarn("dotnet_analyzer_diagnostic.severity = error")(Rule(DiagnosticSeverity.Hidden)));
        Assert.False(MayWarn("dotnet_analyzer_diagnostic.severity = none", "dotnet_analyzer_diagnostic.severity = suggestion")(Rule(DiagnosticSeverity.Warning)));
        // The category's setting before the one for all rules, and the id's before both.
        Assert.False(MayWarn("dotnet_analyzer_diagnostic.category-Usage.severity = none\ndotnet_analyzer_diagnostic.severity = error",
            "dotnet_analyzer_diagnostic.category-Usage.severity = none")(Rule(DiagnosticSeverity.Warning)));
        Assert.True(MayWarn("dotnet_diagnostic.XY0001.severity = warning\ndotnet_analyzer_diagnostic.severity = none")(Rule(DiagnosticSeverity.Info)));
        // Not for rules off by default, nor for ones that aren't configurable.
        Assert.False(MayWarn("dotnet_analyzer_diagnostic.severity = error")(Rule(DiagnosticSeverity.Warning, enabledByDefault: false)));
        Assert.False(MayWarn("dotnet_analyzer_diagnostic.severity = error")(Rule(DiagnosticSeverity.Info, true, WellKnownDiagnosticTags.NotConfigurable)));
    }

    [Fact]
    public void CommandLine()
    {
        var warnAsError = ImmutableDictionary<string, ReportDiagnostic>.Empty.Add("XY0001", ReportDiagnostic.Error);
        var noWarn = ImmutableDictionary<string, ReportDiagnostic>.Empty.Add("XY0001", ReportDiagnostic.Suppress);
        Assert.True(MayWarn(commandLine: warnAsError)(Rule(DiagnosticSeverity.Info)));
        Assert.False(MayWarn(commandLine: noWarn)(Rule(DiagnosticSeverity.Warning)));
        // A config file's severity for the id comes first.
        Assert.True(MayWarn("dotnet_diagnostic.XY0001.severity = warning", commandLine: noWarn)(Rule(DiagnosticSeverity.Warning)));
    }
}
