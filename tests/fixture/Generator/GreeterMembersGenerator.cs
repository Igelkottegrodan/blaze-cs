using System.Linq;
using Microsoft.CodeAnalysis;

namespace Generator;

/// <summary>
/// Emits the names of Lib.Greeter's public methods, so adding one changes the generated code of a
/// project that never names the new method.
/// </summary>
[Generator]
public sealed class GreeterMembersGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var names = context.CompilationProvider.Select((compilation, _) =>
            compilation.GetTypeByMetadataName("Lib.Greeter") is { } greeter
                ? string.Join(",", greeter.GetMembers().OfType<IMethodSymbol>()
                    .Where(m => m.DeclaredAccessibility == Accessibility.Public && m.MethodKind == MethodKind.Ordinary)
                    .Select(m => m.Name).OrderBy(n => n))
                : "");
        context.RegisterSourceOutput(names, (output, value) => output.AddSource("GreeterMembers.g.cs",
            $"namespace Consumer;\n\ninternal static class GreeterMembers\n{{\n    public const string Names = \"{value}\";\n}}\n"));
    }
}
