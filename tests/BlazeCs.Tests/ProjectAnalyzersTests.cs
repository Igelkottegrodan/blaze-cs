using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// What <see cref="ProjectAnalyzers"/> reports for the files an edit touched: the analyzers' diagnostics in
/// those trees only, with the severity the project's .editorconfig gives each file, suppressors applied, and
/// an analyzer that throws reported as csc reports it.
/// </summary>
public sealed class ProjectAnalyzersTests : IDisposable
{
    readonly string _directory = Directory.CreateTempSubdirectory("blaze-cs-analyzers-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task ReportsOnTheGivenTreesOnly()
    {
        var (compilation, config) = Project("", ("A.cs", "class A { void Bad() { } }"), ("B.cs", "class B { void Bad() { } }"));
        var diagnostics = await Analyze([new BadMethodAnalyzer()], config, compilation, "A.cs");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("TST001", Path.Combine(_directory, "A.cs")), (diagnostic.Id, diagnostic.Location.SourceTree?.FilePath));
    }

    [Fact]
    public async Task TakesEachFilesSeverityFromEditorConfig()
    {
        var (compilation, config) = Project(
            "[*.cs]\ndotnet_diagnostic.TST001.severity = warning\n\n[B.cs]\ndotnet_diagnostic.TST001.severity = none\n",
            ("A.cs", "class A { void Bad() { } }"), ("B.cs", "class B { void Bad() { } }"));
        var diagnostics = await Analyze([new BadMethodAnalyzer()], config, compilation, "A.cs", "B.cs");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal((DiagnosticSeverity.Warning, Path.Combine(_directory, "A.cs")), (diagnostic.Severity, diagnostic.Location.SourceTree?.FilePath));
    }

    [Fact]
    public async Task AppliesSuppressors()
    {
        var (compilation, config) = Project("", ("A.cs", "class A { void Bad() { } }"));
        Assert.Empty(await Analyze([new BadMethodAnalyzer(), new BadMethodSuppressor()], config, compilation, "A.cs"));
    }

    [Fact]
    public async Task ReportsAnAnalyzerThatThrowsAsAD0001()
    {
        var (compilation, config) = Project("", ("A.cs", "class A { void Bad() { } }"));
        var diagnostics = await Analyze([new ThrowingAnalyzer()], config, compilation, "A.cs");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(("AD0001", DiagnosticSeverity.Warning), (diagnostic.Id, diagnostic.Severity));
    }

    /// <summary>A project of these files in the temporary directory, with this .editorconfig, as csc compiles it.</summary>
    (CSharpCompilation, ProjectConfig) Project(string editorConfig, params (string Name, string Source)[] files)
    {
        var editorConfigPath = Path.Combine(_directory, ".editorconfig");
        File.WriteAllText(editorConfigPath, "root = true\n\n" + editorConfig);
        var config = new ProjectConfig([editorConfigPath]);
        var compilation = CSharpCompilation.Create(
            "Project",
            files.Select(f => CSharpSyntaxTree.ParseText(f.Source, Library.ParseOptions, Path.Combine(_directory, f.Name))),
            Library.Framework,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithSyntaxTreeOptionsProvider(config.TreeOptions()));
        return (compilation, config);
    }

    async Task<ImmutableArray<Diagnostic>> Analyze(DiagnosticAnalyzer[] analyzers, ProjectConfig config, Compilation compilation, params string[] files)
    {
        var trees = files.Select(name => compilation.SyntaxTrees.Single(t => t.FilePath == Path.Combine(_directory, name))).ToList();
        var diagnostics = await new ProjectAnalyzers([.. analyzers], config.Options())
            .AnalyzeAsync(compilation, trees, [], TestContext.Current.CancellationToken);
        return [.. diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning)];
    }

    /// <summary>Reports every method named Bad: a syntax node action, which runs in the semantic pass.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    sealed class BadMethodAnalyzer : DiagnosticAnalyzer
    {
        public static readonly DiagnosticDescriptor Rule = new("TST001", "Bad method", "Method '{0}' is bad", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(c =>
            {
                var method = (MethodDeclarationSyntax)c.Node;
                if (method.Identifier.ValueText == "Bad")
                    c.ReportDiagnostic(Diagnostic.Create(Rule, method.Identifier.GetLocation(), method.Identifier.ValueText));
            }, SyntaxKind.MethodDeclaration);
        }
    }

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    sealed class BadMethodSuppressor : DiagnosticSuppressor
    {
        static readonly SuppressionDescriptor Rule = new("SPR001", "TST001", "Bad is fine here");

        public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => [Rule];

        public override void ReportSuppressions(SuppressionAnalysisContext context)
        {
            foreach (var diagnostic in context.ReportedDiagnostics)
                context.ReportSuppression(Suppression.Create(Rule, diagnostic));
        }
    }

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    sealed class ThrowingAnalyzer : DiagnosticAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [BadMethodAnalyzer.Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(_ => throw new InvalidOperationException("analyzer bug"), SyntaxKind.MethodDeclaration);
        }
    }
}
