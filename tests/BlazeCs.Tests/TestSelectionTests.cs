using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// Which test classes a hot reload delta's updated types can reach (TestSelection): a library, Lib, a library
/// that uses it, Mid, and a test assembly, Tests, compiled here; the delta is the updated types' tokens.
/// </summary>
public class TestSelectionTests
{
    const string Lib = """
        using System.Linq;
        using System.Threading.Tasks;

        namespace Lib;

        public static class Greeter
        {
            public static string Greet(string name) => "Hello, " + name;
        }

        public static class Numbers
        {
            public static int Two() => 2;

            public static int Doubled(int[] values) => values.Select(value => value * 2).Sum();

            public static async Task<int> LaterAsync()
            {
                await Task.Yield();
                return 1;
            }
        }

        public interface IShape
        {
            double Area();
        }

        public sealed class Square : IShape
        {
            public double Area() => 4;
        }

        public static class Shapes
        {
            public static IShape Make() => new Square();
        }

        public class Animal
        {
            public string Name() => "animal";
        }

        public sealed class Dog : Animal;

        public static class Choices
        {
            // A switch instruction, whose jump table the IL reader must skip, before the call it must see.
            public static string Pick(int value)
            {
                var word = value switch { 0 => "zero", 1 => "one", 2 => "two", 3 => "three", _ => "many" };
                return Greeter.Greet(word);
            }
        }

        public static class Box<T>
        {
            public static T Same(T value) => value;
        }
        """;

    const string Mid = """
        namespace Mid;

        public static class Welcome
        {
            public static string For(string who) => Lib.Greeter.Greet(who);
        }
        """;

    const string Tests = """
        using System;

        namespace Tests;

        public class WelcomeTests
        {
            public void Greets() => Mid.Welcome.For("you");
        }

        public class NumberTests
        {
            public void Doubles() => Lib.Numbers.Doubled([1, 2]);
        }

        public class LocalTests
        {
            public void Adds() => Math.Max(1, 2);

            public class Nested
            {
                public void Greets() => Lib.Greeter.Greet("nested");
            }
        }

        public class ShapeTests
        {
            public void Measures() => Lib.Shapes.Make().Area();
        }

        public class ChoiceTests
        {
            public void Picks() => Lib.Choices.Pick(2);
        }

        public class ByNameTests
        {
            public void Loads() => Type.GetType("Lib.Numbers, Lib");
        }

        public abstract class TestBase
        {
            protected static string Greeting() => "hi";
        }

        public class DerivedTests : TestBase, IDisposable
        {
            public void Greets() => Greeting();

            public void Dispose() { }
        }
        """;

    static readonly string[] Classes = ["Tests.WelcomeTests", "Tests.NumberTests", "Tests.LocalTests", "Tests.LocalTests+Nested", "Tests.ShapeTests",
        "Tests.ChoiceTests", "Tests.ByNameTests", "Tests.DerivedTests"];

    [Fact]
    public void ABodyEditRunsTheClassesThatReachTheTypeThroughOtherAssemblies()
    {
        using var solution = new Solution(("Lib", Lib), ("Mid", Mid), ("Tests", Tests));
        // ByNameTests looks up types by name, so it runs whatever changed. A nested class is part of its declaring
        // class: LocalTests runs with LocalTests+Nested, which greets.
        Assert.Equal(["Tests.ByNameTests", "Tests.ChoiceTests", "Tests.LocalTests", "Tests.LocalTests+Nested", "Tests.WelcomeTests"], solution.Select("Lib", "Lib.Greeter").Classes?.Order());
        Assert.Equal(["Tests.ByNameTests", "Tests.NumberTests"], solution.Select("Lib", "Lib.Numbers").Classes?.Order());
    }

    /// <summary>A lambda's or an async method's type is judged as the type that declares it, a plain static class here.</summary>
    [Fact]
    public void AnEditInALambdaOrAnAsyncMethodRunsTheClassesThatReachItsDeclaringType()
    {
        using var solution = new Solution(("Lib", Lib), ("Mid", Mid), ("Tests", Tests));
        Assert.Equal(["Tests.ByNameTests", "Tests.NumberTests"], solution.Select("Lib", "Lib.Numbers+<>c").Classes?.Order());
        Assert.Equal(["Tests.ByNameTests", "Tests.NumberTests"], solution.Select("Lib", "Lib.Numbers+<LaterAsync>d__2").Classes?.Order());
    }

    /// <summary>The tests use Square only through IShape: code may reach an implementation without naming it.</summary>
    [Fact]
    public void AnEditBehindAnInterfaceRunsEveryTest()
    {
        using var solution = new Solution(("Lib", Lib), ("Mid", Mid), ("Tests", Tests));
        var selection = solution.Select("Lib", "Lib.Square");
        Assert.Null(selection.Classes);
        Assert.Equal("the delta updates Square (Lib): it implements IShape", selection.Everything);
    }

    [Fact]
    public void AnEditToABaseClassRunsEveryTest()
    {
        using var solution = new Solution(("Lib", Lib), ("Mid", Mid), ("Tests", Tests));
        Assert.Equal("the delta updates Animal (Lib): Dog (Lib) derives from it", solution.Select("Lib", "Lib.Animal").Everything);
        Assert.Equal("the delta updates Dog (Lib): it derives from Animal", solution.Select("Lib", "Lib.Dog").Everything);
    }

    [Fact]
    public void AnEditToAGenericTypeRunsEveryTest()
    {
        using var solution = new Solution(("Lib", Lib), ("Mid", Mid), ("Tests", Tests));
        Assert.Equal("the delta updates Box`1 (Lib): it is generic", solution.Select("Lib", "Lib.Box`1").Everything);
    }

    /// <summary>A test class and its base classes are found by the framework, which the selection filters on: the rules don't apply to them.</summary>
    [Fact]
    public void AnEditToATestClassOrItsBaseRunsTheClassesThatReachIt()
    {
        using var solution = new Solution(("Lib", Lib), ("Mid", Mid), ("Tests", Tests));
        Assert.Equal(["Tests.ByNameTests", "Tests.DerivedTests"], solution.Select("Tests", "Tests.TestBase").Classes?.Order());
        Assert.Equal(["Tests.ByNameTests", "Tests.DerivedTests"], solution.Select("Tests", "Tests.DerivedTests").Classes?.Order());
    }

    /// <summary>Code an assembly attribute names (an assembly fixture), or MSTest's assembly initialize, runs around every test.</summary>
    [Theory]
    [InlineData("""
        [assembly: Tests.FixtureAttribute(typeof(Mid.Welcome))]

        namespace Tests;

        public sealed class FixtureAttribute(System.Type type) : System.Attribute;
        """, "Welcome (Mid)")]
    [InlineData("""
        namespace Tests;

        public sealed class AssemblyInitializeAttribute : System.Attribute;

        public class Setup
        {
            [AssemblyInitialize]
            public static void Start() => Mid.Welcome.For("all");
        }
        """, "Setup (Tests)")]
    public void AnEditCodeThatRunsAroundEveryTestReachesRunsEveryTest(string setup, string root)
    {
        using var solution = new Solution(("Lib", Lib), ("Mid", Mid), ("Tests", Tests + NextFile + setup));
        Assert.Equal($"{root} runs around every test and can reach what the delta updates", solution.Select("Lib", "Lib.Greeter").Everything);
    }

    /// <summary>Every method body of a large assembly reads: Roslyn's, with switches (jump tables) and every kind of operand.</summary>
    [Fact]
    public void ReadsALargeAssembly()
    {
        var roslyn = typeof(CSharpCompilation).Assembly.Location;
        var selection = TestSelection.Select(roslyn, [roslyn], [], []);
        Assert.Equal((0, null), (selection.Classes?.Count, selection.Everything));
    }

    [Fact]
    public void ADeltaThatDoesNotSayWhatItUpdatedRunsEveryTest()
    {
        using var solution = new Solution(("Lib", Lib), ("Mid", Mid), ("Tests", Tests));
        var selection = TestSelection.Select(solution.Path("Tests"), solution.Paths, [(solution.Path("Lib"), null)], Classes);
        Assert.Equal("the delta to Lib doesn't say which types it updated", selection.Everything);
    }

    /// <summary>A test class the framework names after no type of the test assembly (a generic or parameterized class) can't be traced.</summary>
    [Fact]
    public void ATestClassNamedAfterNoTypeRunsEveryTest()
    {
        using var solution = new Solution(("Lib", Lib), ("Mid", Mid), ("Tests", Tests));
        var selection = TestSelection.Select(solution.Path("Tests"), solution.Paths, [(solution.Path("Lib"), solution.Tokens("Lib", "Lib.Greeter"))],
            [.. Classes, "Tests.GenericTests`1[[System.Int32, System.Private.CoreLib]]"]);
        Assert.Equal("the framework names a test class Tests.GenericTests`1[[System.Int32, System.Private.CoreLib]], and Tests has no type of that name", selection.Everything);
    }

    /// <summary>Separates an assembly's source files.</summary>
    const string NextFile = "\n// next file\n";

    /// <summary>Assemblies compiled in order, each referencing those before it, into a directory of their own.</summary>
    sealed class Solution : IDisposable
    {
        readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("blaze-cs-selection-");
        readonly List<string> _paths = [];

        public Solution(params (string Name, string Source)[] assemblies)
        {
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(System.IO.Path.PathSeparator)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToList();
            foreach (var (name, source) in assemblies)
            {
                var compilation = CSharpCompilation.Create(name, source.Split(NextFile).Select(file => CSharpSyntaxTree.ParseText(file)), references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));
                var path = System.IO.Path.Combine(_directory.FullName, name + ".dll");
                var emitted = compilation.Emit(path);
                Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
                references.Add(MetadataReference.CreateFromFile(path));
                _paths.Add(path);
            }
        }

        public IReadOnlyCollection<string> Paths => _paths;

        public string Path(string assembly) => _paths.Single(path => System.IO.Path.GetFileNameWithoutExtension(path) == assembly);

        /// <summary>The TypeDef tokens of an assembly's types, by full name.</summary>
        public int[] Tokens(string assembly, params string[] types)
        {
            using var pe = new PEReader(ImmutableArray.Create(File.ReadAllBytes(Path(assembly))));
            var reader = pe.GetMetadataReader();
            string Name(TypeDefinitionHandle handle)
            {
                var type = reader.GetTypeDefinition(handle);
                return type.GetDeclaringType() is { IsNil: false } declaring ? $"{Name(declaring)}+{reader.GetString(type.Name)}"
                    : $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";
            }
            var tokens = reader.TypeDefinitions.ToDictionary(Name, handle => MetadataTokens.GetToken(handle));
            return [.. types.Select(type => tokens[type])];
        }

        /// <summary>The selection for a delta to these types of an assembly, its own baseline.</summary>
        public TestSelection.Selection Select(string assembly, params string[] types) =>
            TestSelection.Select(Path("Tests"), _paths, [(Path(assembly), Tokens(assembly, types))], Classes);

        public void Dispose()
        {
            try { _directory.Delete(recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
