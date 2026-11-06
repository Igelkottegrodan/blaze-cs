using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// What <see cref="ProjectAnalyzers"/> reports for the files an edit touched: the analyzers' diagnostics in
/// those trees only, with the severity the project's .editorconfig gives each file, suppressors applied, and
/// an analyzer that throws reported as csc reports it; after a body edit, what a full analysis would report.
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
        var analyzer = new BadMethodAnalyzer();
        var (full, incremental) = await AnalyzeEdit(analyzer, () => new BadMethodAnalyzer(),
            "class A\n{\n    void Other() { }\n\n    void Bad() { }\n}\n",
            "class A\n{\n    void Other() {\n        var x = 1;\n    }\n\n    void Bad() { }\n}\n",
            beforeEdit: analyzer.Visited.Clear);

        Assert.Equal(["Other"], analyzer.Visited.Order());
        Assert.Equal(full, incremental);
        Assert.Contains("(7,10): warning TST001", Assert.Single(incremental));
    }

    // A type's symbol-end action reports on a field from what a method's body does (as CA2213, IDE0044 and
    // IDISP002 do): the edit adds or removes a warning outside the member.
    [Theory]
    [InlineData("int _f;", "void M() { }", "void M() { _f = 1; }")]
    [InlineData("int _f;", "void M() { _f = 1; }", "void M() { }")]
    public async Task ABodyEditUpdatesWhatAnalyzersOfTheWholeTypeReportOutsideIt(string field, string before, string after)
    {
        var (full, incremental) = await AnalyzeEdit(new UnreadFieldAnalyzer(), () => new UnreadFieldAnalyzer(),
            $"class A\n{{\n    {field}\n\n    {before}\n}}\n", $"class A\n{{\n    {field}\n\n    {after}\n}}\n");
        Assert.Equal(full, incremental);
    }

    [Fact]
    public async Task AnAnalysisIsTheBaselineOnlyOnceCommitted()
    {
        const string before = "class A\n{\n    void Other() { }\n\n    void Bad() { }\n}\n";
        var (compilation, config) = Project("", ("A.cs", before));
        var analyzer = new BadMethodAnalyzer();
        var analyzers = new ProjectAnalyzers([analyzer], config.Options());
        var tree = compilation.SyntaxTrees.Single();
        await analyzers.AnalyzeAsync(compilation, [tree], [], TestContext.Current.CancellationToken);

        analyzer.Visited.Clear();
        var edited = tree.WithChangedText(SourceText.From(before.Replace("Other() { }", "Other() { var x = 1; }")));
        await analyzers.AnalyzeAsync(compilation.ReplaceSyntaxTree(tree, edited), [edited], [], TestContext.Current.CancellationToken);

        Assert.Equal(["Bad", "Other"], analyzer.Visited.Order());
    }

    /// <summary>
    /// What one analyzer reports on a file after an edit, starting from the committed analysis of the file before
    /// it, and what a fresh one reports.
    /// </summary>
    async Task<(string[] Full, string[] Incremental)> AnalyzeEdit(
        DiagnosticAnalyzer analyzer, Func<DiagnosticAnalyzer> fresh, string before, string after, Action? beforeEdit = null)
    {
        var (compilation, config) = Project("", ("A.cs", before));
        var analyzers = new ProjectAnalyzers([analyzer], config.Options());
        var tree = compilation.SyntaxTrees.Single();
        analyzers.Commit((await analyzers.AnalyzeAsync(compilation, [tree], [], TestContext.Current.CancellationToken)).Files);
        var edited = tree.WithChangedText(SourceText.From(after));
        Assert.NotNull(BodyEdit.Between(tree, edited, TestContext.Current.CancellationToken));

        beforeEdit?.Invoke();
        var compiled = compilation.ReplaceSyntaxTree(tree, edited);
        var incremental = await analyzers.AnalyzeAsync(compiled, [edited], [], TestContext.Current.CancellationToken);
        var full = await new ProjectAnalyzers([fresh()], config.Options()).AnalyzeAsync(compiled, [edited], [], TestContext.Current.CancellationToken);
        return (Formatted(full.Diagnostics), Formatted(incremental.Diagnostics));
    }

    [Fact]
    public void WarnsAboutAnAnalyzerAssemblyItCannotLoadAsCscDoes()
    {
        var path = Path.Combine(_directory, "NotAnAssembly.dll");
        File.WriteAllText(path, "not an assembly");
        var cache = new AnalyzerCache(Path.Combine(_directory, "shadow"));

        Assert.Empty(cache.Analyzers([path], suppressorsOnly: false));
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var warning = Assert.Single(cache.LoadFailures([path], options));
        Assert.Equal(("CS8034", DiagnosticSeverity.Warning), (warning.Id, warning.Severity));
        Assert.StartsWith($"Unable to load Analyzer assembly {path} : ", warning.GetMessage());
        Assert.Empty(cache.LoadFailures([path], options.WithSpecificDiagnosticOptions([new("CS8034", ReportDiagnostic.Suppress)])));
    }

    [Theory]
    [InlineData("a declaration", "class A\n{\n    void Renamed() { }\n\n    void Bad() { }\n}\n")]
    [InlineData("two members", "class A\n{\n    void Other() { var x = 1; }\n\n    void Bad() { var y = 2; }\n}\n")]
    [InlineData("a directive in a body", "class A\n{\n    void Other()\n    {\n#pragma warning disable TST001\n    }\n\n    void Bad() { }\n}\n")]
    public void OtherEditsAreNoBodyEdit(string edit, string after)
    {
        var before = CSharpSyntaxTree.ParseText("class A\n{\n    void Other() { }\n\n    void Bad() { }\n}\n", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(BodyEdit.Between(before, before.WithChangedText(SourceText.From(after)), TestContext.Current.CancellationToken) is null, edit);
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
        var result = await new ProjectAnalyzers([.. analyzers], config.Options())
            .AnalyzeAsync(compilation, trees, [], TestContext.Current.CancellationToken);
        return [.. result.Diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning)];
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

    /// <summary>Reports the fields of a type its methods never mention, from the type's symbol-end action.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    sealed class UnreadFieldAnalyzer : DiagnosticAnalyzer
    {
        static readonly DiagnosticDescriptor Rule = new("TST002", "Unused field", "Field '{0}' is never used", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolStartAction(start =>
            {
                var used = new System.Collections.Concurrent.ConcurrentDictionary<ISymbol, bool>(SymbolEqualityComparer.Default);
                start.RegisterOperationAction(c => used[((IFieldReferenceOperation)c.Operation).Field] = true, OperationKind.FieldReference);
                start.RegisterSymbolEndAction(end =>
                {
                    foreach (var field in ((INamedTypeSymbol)end.Symbol).GetMembers().OfType<IFieldSymbol>())
                    {
                        if (!field.IsImplicitlyDeclared && !used.ContainsKey(field))
                            end.ReportDiagnostic(Diagnostic.Create(Rule, field.Locations[0], field.Name));
                    }
                });
            }, SymbolKind.NamedType);
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
