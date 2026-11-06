using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BlazeCs.Tests;

/// <summary>Compiles small C# sources the way the SDK's csc does: deterministic, nullable on, C# 14.</summary>
static class Library
{
    public static readonly MetadataReference[] Framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => Path.GetFileName(path) is var name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard.dll" or "mscorlib.dll"))
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    /// <summary>As with GenerateDocumentationFile: doc comments are parsed, crefs bound.</summary>
    public static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp14, DocumentationMode.Diagnose);

    static readonly SyntaxTree Usings = CSharpSyntaxTree.ParseText(
        "global using System; global using System.Collections.Generic; global using System.Runtime.CompilerServices;",
        ParseOptions);

    /// <summary>A compilation of <paramref name="source"/> (with System usings) that must have no errors.</summary>
    public static CSharpCompilation Compile(string source, params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(
            "Lib",
            [Usings, CSharpSyntaxTree.ParseText(source, ParseOptions)],
            [.. Framework, .. references],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true, nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException($"{source}\n{string.Join("\n", errors)}");
        return compilation;
    }

    /// <summary>The reference assembly csc's /refout writes: the image dependents compile against.</summary>
    public static byte[] ReferenceAssembly(Compilation compilation)
    {
        using var assembly = new MemoryStream();
        using var reference = new MemoryStream();
        var result = compilation.Emit(assembly, metadataPEStream: reference);
        if (!result.Success)
            throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
        return reference.ToArray();
    }
}
