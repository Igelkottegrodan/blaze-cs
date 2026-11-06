using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// For each kind of edit to a library, which of its dependents the daemon recompiles: none when the
/// reference assembly is unchanged, otherwise what <see cref="ApiSurface.ChangeFrom"/> decides.
/// "Dependents naming X" is only right if a dependent whose source never names X binds the same after the edit.
/// </summary>
public class ApiSurfaceTests
{
    const string None = "no dependent recompiles";
    const string Every = "every dependent recompiles";

    static string Naming(params string[] names) =>
        "only dependents naming " + string.Join(", ", names.Order(StringComparer.Ordinal)) + " recompile";

    static readonly Dictionary<string, (string Before, string After, string Expected)> Edits = new()
    {
        // The reference assembly doesn't change.
        ["edit a method body"] = ("public class Widget { public int Spin() => 1; }", "public class Widget { public int Spin() => 2; }", None),
        ["add a private method"] = ("public class Widget { }", "public class Widget { void Spin() { } }", None),
        ["add an internal method"] = ("public class Widget { }", "public class Widget { internal void Spin() { } }", None),

        // Members reached by name.
        ["add a method"] = ("public class Widget { }", "public class Widget { public void Spin() { } }", Naming("Spin")),
        ["remove a method"] = ("public class Widget { public void Spin() { } }", "public class Widget { }", Naming("Spin")),
        ["rename a method"] = ("public class Widget { public void Spin() { } }", "public class Widget { public void Turn() { } }", Naming("Spin", "Turn")),
        ["add an overload"] = ("public class Widget { public void Spin() { } }", "public class Widget { public void Spin() { } public void Spin(int times) { } }", Naming("Spin")),
        ["change a parameter default"] = ("public class Widget { public void Spin(int times = 1) { } }", "public class Widget { public void Spin(int times = 2) { } }", Naming("Spin")),
        ["change a const value"] = ("public class Widget { public const int Max = 1; }", "public class Widget { public const int Max = 2; }", Naming("Max")),
        ["add an internal method with InternalsVisibleTo"] = (
            "[assembly: InternalsVisibleTo(\"Consumer\")] public class Widget { }",
            "[assembly: InternalsVisibleTo(\"Consumer\")] public class Widget { internal void Spin() { } }",
            Naming("Spin")),
        ["make a public method internal"] = ("public class Widget { public void Spin() { } }", "public class Widget { internal void Spin() { } }", Naming("Spin")),
        ["make a private method protected"] = ("public class Widget { void Spin() { } }", "public class Widget { protected void Spin() { } }", Naming("Spin")),
        ["make a method virtual"] = ("public class Widget { public void Spin() { } }", "public class Widget { public virtual void Spin() { } }", Naming("Spin")),
        ["seal an override"] = (
            "public class Base { public virtual void Spin() { } } public class Widget : Base { public override void Spin() { } }",
            "public class Base { public virtual void Spin() { } } public class Widget : Base { public sealed override void Spin() { } }",
            Naming("Spin")),
        ["make a setter init-only"] = ("public class Widget { public int Size { get; set; } }", "public class Widget { public int Size { get; init; } }", Naming("Size")),
        ["make a return type nullable"] = ("public class Widget { public string Name() => \"\"; }", "public class Widget { public string? Name() => null; }", Naming("Name")),
        ["make a parameter nullable"] = ("public class Widget { public void Rename(string name) { } }", "public class Widget { public void Rename(string? name) { } }", Naming("Rename")),
        ["change a method's generic constraint"] = (
            "public class Widget { public void Put<T>(T item) where T : class { } }",
            "public class Widget { public void Put<T>(T item) where T : struct { } }",
            Naming("Put")),
        ["add an attribute to a method"] = ("public class Widget { public void Spin() { } }", "public class Widget { [Obsolete] public void Spin() { } }", Naming("Spin")),
        ["remove an attribute from a method"] = ("public class Widget { [Obsolete] public void Spin() { } }", "public class Widget { public void Spin() { } }", Naming("Spin")),
        ["add an enum member"] = ("public enum Color { Red }", "public enum Color { Red, Green }", Naming("Green")),
        ["remove an enum member"] = ("public enum Color { Red, Green }", "public enum Color { Red }", Naming("Green")),
        ["insert an enum member before others"] = ("public enum Color { Red, Green }", "public enum Color { Blue, Red, Green }", Naming("Blue", "Green", "Red")),
        ["add an extension method to an existing class"] = (
            "public class Widget { } public static class WidgetExtensions { }",
            "public class Widget { } public static class WidgetExtensions { public static void Polish(this Widget w) { } }",
            Naming("Polish")),

        // Types reached by name.
        ["rename a type"] = ("public class Widget { }", "public class Gadget { }", Naming("Gadget", "Widget")),
        ["add a nested type"] = ("public class Widget { }", "public class Widget { public class Part { } }", Naming("Part")),
        // Attributes are named without their suffix: [Tag] binds to TagAttribute.
        ["add an attribute class"] = ("public class Widget { }", "public class Widget { } public sealed class TagAttribute : Attribute { }", Naming("Tag", "TagAttribute")),
        ["remove an attribute class"] = ("public class Widget { } public sealed class TagAttribute : Attribute { }", "public class Widget { }", Naming("Tag", "TagAttribute")),

        // Extension members bind on the receiver, without their class's name.
        ["add a class with an extension method"] = (
            "public class Widget { }",
            "public class Widget { } public static class WidgetExtensions { public static void Polish(this Widget w) { } }",
            Naming("Polish", "WidgetExtensions")),
        ["remove a class with an extension method"] = (
            "public class Widget { } public static class WidgetExtensions { public static void Polish(this Widget w) { } }",
            "public class Widget { }",
            Naming("Polish", "WidgetExtensions")),
        ["add a member to an extension block"] = (
            "public class Widget { } public static class WidgetExtensions { extension(Widget w) { public void Polish() { } } }",
            "public class Widget { } public static class WidgetExtensions { extension(Widget w) { public void Polish() { } public bool Shiny => true; } }",
            Naming("Shiny")),
        ["add a class with an extension block"] = (
            "public class Widget { }",
            "public class Widget { } public static class WidgetExtensions { extension(Widget w) { public bool Shiny => true; } }",
            Naming("Shiny", "WidgetExtensions")),
        ["add an extension block"] = (
            "public class Widget { } public static class WidgetExtensions { }",
            "public class Widget { } public static class WidgetExtensions { extension(Widget w) { public bool Shiny => true; } }",
            Naming("Shiny")),
        ["change an extension block's receiver"] = (
            "public class Widget { } public class Gadget { } public static class WidgetExtensions { extension(Widget w) { public bool Shiny => true; } }",
            "public class Widget { } public class Gadget { } public static class WidgetExtensions { extension(Gadget g) { public bool Shiny => true; } }",
            Naming("Shiny")),

        // Reachable without any name in the dependent's source.
        ["add an operator"] = ("public class Widget { }", "public class Widget { public static Widget operator +(Widget a, Widget b) => a; }", Every),
        ["add an implicit conversion"] = ("public class Widget { }", "public class Widget { public static implicit operator int(Widget w) => 0; }", Every),
        ["add GetEnumerator"] = ("public class Widget { }", "public class Widget { public IEnumerator<int> GetEnumerator() { yield break; } }", Every),
        ["add Dispose"] = ("public ref struct Lease { }", "public ref struct Lease { public void Dispose() { } }", Every),
        ["add Deconstruct"] = ("public class Widget { }", "public class Widget { public void Deconstruct(out int x, out int y) => (x, y) = (0, 0); }", Every),
        ["add an indexer"] = ("public class Widget { }", "public class Widget { public int this[int i] => i; }", Every),
        ["add a constructor"] = ("public class Widget { }", "public class Widget { public Widget() { } public Widget(int size) { } }", Every),
        ["change an attribute's constructor"] = (
            "public sealed class TagAttribute : Attribute { public TagAttribute(int size) { } }",
            "public sealed class TagAttribute : Attribute { public TagAttribute(long size) { } }",
            Every),
        ["add a positional record parameter"] = ("public record Point(int X);", "public record Point(int X, int Y);", Every),
        ["add an extension GetEnumerator in a new class"] = (
            "public class Widget { }",
            "public class Widget { } public static class WidgetExtensions { public static IEnumerator<int> GetEnumerator(this Widget w) { yield break; } }",
            Every),
        ["add an extension operator"] = (
            "public class Widget { } public static class WidgetExtensions { }",
            "public class Widget { } public static class WidgetExtensions { extension(Widget) { public static Widget operator +(Widget a, Widget b) => a; } }",
            Every),
        ["add an interface member"] = ("public interface IShape { }", "public interface IShape { double Area(); }", Every),
        ["make a method abstract"] = (
            "public abstract class Widget { public virtual void Spin() { } }",
            "public abstract class Widget { public abstract void Spin(); }",
            Every),
        // `new()` and `Take(new())` construct a type without naming its required members.
        ["make a property required"] = ("public class Widget { public int Size { get; set; } }", "public class Widget { public required int Size { get; set; } }", Every),
        ["add a required property"] = ("public class Widget { }", "public class Widget { public required int Size { get; set; } }", Every),

        // A type's own shape reaches every use of it and of types derived from it.
        ["add an attribute to a type"] = ("public class Widget { }", "[Obsolete] public class Widget { }", Every),
        ["remove an attribute from a type"] = ("[Obsolete] public class Widget { }", "public class Widget { }", Every),
        ["change the base type"] = ("public class Base { } public class Other { } public class Widget : Base { }", "public class Base { } public class Other { } public class Widget : Other { }", Every),
        ["add an interface"] = ("public class Widget { }", "public class Widget : IComparable<Widget> { public int CompareTo(Widget? other) => 0; }", Every),
        ["change a private struct field"] = ("public struct Point { int x; }", "public struct Point { long x; }", Every),
        // Defensive copies of a readonly field (`w.Location.ToString()`) don't name the struct.
        ["make a struct readonly"] = ("public struct Point { public readonly int X; }", "public readonly struct Point { public readonly int X; }", Every),
        ["make a struct a ref struct"] = ("public struct Point { }", "public ref struct Point { }", Every),
        ["make a class sealed"] = ("public class Widget { }", "public sealed class Widget { }", Every),
        ["make a class abstract"] = ("public class Widget { }", "public abstract class Widget { }", Every),
        ["make a public type internal"] = ("public class Widget { }", "internal class Widget { }", Every),
        // Lambdas bind to a delegate without naming it: `w.OnSpin(x => x + 1)`.
        ["change a delegate's signature"] = ("public delegate void Handler(int x);", "public delegate void Handler(long x);", Every),
        // Variance converts a value without naming its type: `Take(Feed())` with Take(IFeed<object>), Feed() an IFeed<string>.
        ["remove variance from a type parameter"] = ("public interface IFeed<out T> { }", "public interface IFeed<T> { }", Every),
        ["change a type's generic constraint"] = ("public class Box<T> where T : class { }", "public class Box<T> where T : notnull { }", Every),
        // The surface can't tell oblivious from not-annotated; the image can.
        ["turn nullable annotations off for a member"] = (
            "public class Widget { public string Name() => \"\"; }",
            "public class Widget {\n#nullable disable\npublic string Name() => \"\";\n#nullable restore\n}",
            Every),

        // Qualified names and imports resolve differently everywhere.
        ["add a namespace"] = ("public class Widget { }", "public class Widget { } namespace Extra { public class Tool { } }", Every),
        ["add InternalsVisibleTo"] = ("public class Widget { }", "[assembly: InternalsVisibleTo(\"Consumer\")] public class Widget { }", Every),
        ["change the assembly version"] = (
            "[assembly: System.Reflection.AssemblyVersion(\"1.0.0.0\")] public class Widget { }",
            "[assembly: System.Reflection.AssemblyVersion(\"2.0.0.0\")] public class Widget { }",
            Every),
    };

    public static TheoryData<string> Cases => [.. Edits.Keys];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Verdict(string edit)
    {
        var (before, after, expected) = Edits[edit];
        Assert.Equal(expected, VerdictOf(before, after));
    }

    /// <summary>What the daemon does once the library is recompiled (SolutionBuilder.Publish and SurfaceChange).</summary>
    static string VerdictOf(string before, string after)
    {
        var then = Library.Compile(before);
        var now = Library.Compile(after);
        if (Library.ReferenceAssembly(then).AsSpan().SequenceEqual(Library.ReferenceAssembly(now)))
            return None;
        var change = new ApiSurface(now).ChangeFrom(new ApiSurface(then));
        return change.Everything ? Every : Naming([.. change.Names]);
    }
}
