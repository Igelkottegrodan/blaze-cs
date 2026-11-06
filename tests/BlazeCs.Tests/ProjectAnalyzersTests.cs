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

    [Fact]
    public async Task ABodyEditAnalyzesOnlyItsMemberAndMovesTheRest()
    {
        const string before = "class A\n{\n    void Other() { }\n\n    void Bad() { }\n}\n";
        const string after = "class A\n{\n    void Other() {\n        var x = 1;\n    }\n\n    void Bad() { }\n}\n";
        var (compilation, config) = Project("", ("A.cs", before));
        var analyzer = new BadMethodAnalyzer();
        var analyzers = new ProjectAnalyzers([analyzer], config.Options());
        await analyzers.AnalyzeAsync(compilation, [.. compilation.SyntaxTrees], [], TestContext.Current.CancellationToken);
        var oldTree = compilation.SyntaxTrees.Single();
        var newTree = oldTree.WithChangedText(Microsoft.CodeAnalysis.Text.SourceText.From(after));
        Assert.NotNull(BodyEdit.Between(oldTree, newTree, TestContext.Current.CancellationToken));

        analyzer.Visited.Clear();
        var edited = compilation.ReplaceSyntaxTree(oldTree, newTree);
        var incremental = await analyzers.AnalyzeAsync(edited, [newTree], [], TestContext.Current.CancellationToken);
        var full = await new ProjectAnalyzers([new BadMethodAnalyzer()], config.Options()).AnalyzeAsync(edited, [newTree], [], TestContext.Current.CancellationToken);

        Assert.Equal(["Other"], analyzer.Visited.Order());
        Assert.Equal(Formatted(full), Formatted(incremental));
        Assert.Contains("(7,10): warning TST001", Assert.Single(Formatted(incremental)));
    }

    [Theory]
    [InlineData("a declaration", "class A\n{\n    void Renamed() { }\n\n    void Bad() { }\n}\n")]
    [InlineData("two members", "class A\n{\n    void Other() { var x = 1; }\n\n    void Bad() { var y = 2; }\n}\n")]
    [InlineData("a directive in a body", "class A\n{\n    void Other()\n    {\n#pragma warning disable TST001\n    }\n\n    void Bad() { }\n}\n")]
    public void OtherEditsAreNoBodyEdit(string edit, string after)
    {
        var before = CSharpSyntaxTree.ParseText("class A\n{\n    void Other() { }\n\n    void Bad() { }\n}\n", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(BodyEdit.Between(before, before.WithChangedText(Microsoft.CodeAnalysis.Text.SourceText.From(after)), TestContext.Current.CancellationToken) is null, edit);
    }

    static string[] Formatted(ImmutableArray<Diagnostic> diagnostics) =>
        [.. diagnostics.Select(d => CSharpDiagnosticFormatter.Instance.Format(d)).Order(StringComparer.Ordinal)];

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

        /// <summary>The methods it was run on.</summary>
        public System.Collections.Concurrent.ConcurrentQueue<string> Visited { get; } = new();

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(c =>
            {
                var method = (MethodDeclarationSyntax)c.Node;
                Visited.Enqueue(method.Identifier.ValueText);
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
