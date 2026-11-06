using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// The restore check compares only what restore reaches: the spec MSBuild regenerates also lists a project only
/// a ReferenceOutputAssembly="false" reference leads to, which restore's own spec in obj/ leaves out.
/// </summary>
public class RestoreSpecTests
{
    static string Spec(string consumerReferences, params string[] projects) => $$"""
        {
          "format": 1,
          "restore": { "/s/Consumer/Consumer.csproj": {} },
          "projects": {
            "/s/Consumer/Consumer.csproj": {
              "restore": { "frameworks": { "net10.0": { "projectReferences": { {{consumerReferences}} } } } }
            }{{string.Concat(projects.Select(p => $$""", "{{p}}": { "version": "1.0.0" }"""))}}
          }
        }
        """.ReplaceLineEndings("\n");

    const string LibReference = "\"/s/Lib/Lib.csproj\": { \"projectPath\": \"/s/Lib/Lib.csproj\" }";

    [Fact]
    public void IgnoresAProjectRestoreDoesNotReach() =>
        Assert.True(RestoreSpec.Same(Spec(LibReference, "/s/Lib/Lib.csproj"), Spec(LibReference, "/s/Generator/Generator.csproj", "/s/Lib/Lib.csproj")));

    [Fact]
    public void SeesAReferencedProjectChange() =>
        Assert.False(RestoreSpec.Same(Spec(LibReference, "/s/Lib/Lib.csproj"),
            Spec(LibReference, "/s/Lib/Lib.csproj").Replace("\"version\": \"1.0.0\"", "\"version\": \"2.0.0\"")));

    [Fact]
    public void SeesANewReference() =>
        Assert.False(RestoreSpec.Same(Spec(LibReference, "/s/Lib/Lib.csproj"),
            Spec(LibReference + ", \"/s/Generator/Generator.csproj\": { \"projectPath\": \"/s/Generator/Generator.csproj\" }",
                "/s/Generator/Generator.csproj", "/s/Lib/Lib.csproj")));

    [Fact]
    public void IgnoresFormatting() =>
        Assert.True(RestoreSpec.Same(Spec(LibReference, "/s/Lib/Lib.csproj"), Spec(LibReference, "/s/Lib/Lib.csproj").Replace("\n", "").Replace("  ", "")));
}
