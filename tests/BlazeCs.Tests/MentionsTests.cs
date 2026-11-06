using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// Whether a dependent names an identifier an API change touched (<see cref="ProjectCompiler.Mentions"/>):
/// anywhere the compiler binds it, doc-comment crefs and generated files included.
/// </summary>
public class MentionsTests
{
    static readonly MetadataReference Lib = Library.Compile(
        "public class Widget { public void Spin() { } } public sealed class TagAttribute : Attribute { }").ToMetadataReference();

    static readonly Dictionary<string, (string Source, string Name, bool Expected)> Sources = new()
    {
        ["a call"] = ("class C { void M(Widget w) => w.Spin(); }", "Spin", true),
        ["a cref in a doc comment"] = ("/// <summary>Calls <see cref=\"Widget.Spin\"/>.</summary>\nclass C { }", "Spin", true),
        ["a cref to a type in a doc comment"] = ("/// <seealso cref=\"Widget\"/>\nclass C { }", "Widget", true),
        ["nameof"] = ("class C { string N => nameof(Widget.Spin); }", "Spin", true),
        ["a verbatim identifier"] = ("class C { void M(Widget w) => w.@Spin(); }", "Spin", true),
        ["a unicode escape"] = ("class C { void M(Widget w) => w.Sp\\u0069n(); }", "Spin", true),
        ["an attribute by its short name"] = ("[Tag] class C { }", "Tag", true),
        ["only an ordinary comment"] = ("// Spin\nclass C { }", "Spin", false),
        ["only a string"] = ("class C { string S => \"Spin\"; }", "Spin", false),
        ["only an excluded #if region"] = ("class C {\n#if NEVER\nvoid M(Widget w) => w.Spin();\n#endif\n}", "Spin", false),
    };

    public static TheoryData<string> Cases => [.. Sources.Keys];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Mentions(string source)
    {
        var (text, name, expected) = Sources[source];
        Assert.Equal(expected, ProjectCompiler.Mentions(Library.Compile(text, Lib), new HashSet<string> { name }));
    }

    [Fact]
    public void GeneratedTreesCount()
    {
        var compilation = Library.Compile("class C { }", Lib);
        CSharpGeneratorDriver.Create(new SpinGenerator().AsSourceGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var diagnostics, TestContext.Current.CancellationToken);

        Assert.Empty(diagnostics);
        Assert.False(ProjectCompiler.Mentions(compilation, new HashSet<string> { "Spin" }));
        Assert.True(ProjectCompiler.Mentions(generated, new HashSet<string> { "Spin" }));
    }

    sealed class SpinGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context) =>
            context.RegisterPostInitializationOutput(output =>
                output.AddSource("Spinner.g.cs", "static class Spinner { public static void Go(Widget w) => w.Spin(); }"));
    }
}
