using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// Which project file edits the daemon takes in process (<see cref="ProjectFileEdit"/>): only those that change
/// what csc gets. Anything else the build does (copies, runtimeconfig, apphost, targets, restore) needs dotnet build.
/// </summary>
public class ProjectFileEditTests
{
    // LF on every checkout: a Windows checkout of this file has CRLF, and the edits below name LF.
    static readonly string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="../Lib/Lib.csproj" />
          </ItemGroup>
        </Project>
        """.ReplaceLineEndings("\n");

    static string Edit(string from, string to)
    {
        Assert.Contains(from, Project);
        return Project.Replace(from, to);
    }

    static string? After(string from, string to) => ProjectFileEdit.MoreThanCompilerInput(Project, Edit(from, to), out _);

    const string End = "</Project>";

    [Theory]
    [InlineData("a comment", End, "  <!-- edited -->\n</Project>")]
    [InlineData("whitespace", "  <PropertyGroup>", "\n\n  <PropertyGroup>   ")]
    [InlineData("a define", "<Nullable>enable</Nullable>", "<Nullable>enable</Nullable>\n    <DefineConstants>$(DefineConstants);FLAG</DefineConstants>")]
    [InlineData("a compiler property changed", "<Nullable>enable</Nullable>", "<Nullable>disable</Nullable>")]
    [InlineData("a compiler property removed", "<Nullable>enable</Nullable>", "")]
    [InlineData("a group of compiler properties", End, "  <PropertyGroup Condition=\"'$(Configuration)' == 'Release'\"><LangVersion>preview</LangVersion><NoWarn>$(NoWarn);CS1591</NoWarn></PropertyGroup>\n</Project>")]
    [InlineData("a Compile item", End, "  <ItemGroup><Compile Include=\"Generated/*.txt\" /></ItemGroup>\n</Project>")]
    [InlineData("a Using and an AdditionalFiles item", End, "  <ItemGroup><Using Include=\"System.Text\" /><AdditionalFiles Include=\"stylecop.json\" /></ItemGroup>\n</Project>")]
    [InlineData("a compiler property moved under a condition", "  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <Nullable>enable</Nullable>",
        "  <PropertyGroup Condition=\"'$(Configuration)' == 'Debug'\"><Nullable>enable</Nullable></PropertyGroup>\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>")]
    public void TakesAnEditThatChangesOnlyCscInput(string _, string from, string to) =>
        Assert.Null(After(from, to));

    [Theory]
    [InlineData("a copied file", End, "  <ItemGroup><None Update=\"data.txt\" CopyToOutputDirectory=\"PreserveNewest\" /></ItemGroup>\n</Project>",
        "<None Update=\"data.txt\">")]
    [InlineData("a copied Compile item", End, "  <ItemGroup><Compile Update=\"Data.cs\"><CopyToOutputDirectory>Always</CopyToOutputDirectory></Compile></ItemGroup>\n</Project>",
        "<Compile Update=\"Data.cs\">")]
    [InlineData("a runtimeconfig setting", "<Nullable>enable</Nullable>", "<Nullable>enable</Nullable><InvariantGlobalization>true</InvariantGlobalization>",
        "<InvariantGlobalization>")]
    [InlineData("an apphost", "<Nullable>enable</Nullable>", "<Nullable>enable</Nullable><OutputType>Exe</OutputType>", "<OutputType>")]
    [InlineData("a package", End, "  <ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup>\n</Project>",
        "<PackageReference Include=\"xunit\">")]
    [InlineData("a reference changed", "../Lib/Lib.csproj", "../Other/Other.csproj", "<ProjectReference Include=\"../Other/Other.csproj\">")]
    [InlineData("a target", End, "  <Target Name=\"Weave\" AfterTargets=\"CoreCompile\"><Exec Command=\"weave\" /></Target>\n</Project>",
        "<Target Name=\"Weave\">")]
    [InlineData("a pre-build step", "<Nullable>enable</Nullable>", "<Nullable>enable</Nullable><PreBuildEvent>echo hi</PreBuildEvent>", "<PreBuildEvent>")]
    public void FallsBackOnAnythingElse(string _, string from, string to, string expected) =>
        Assert.Contains(expected, After(from, to));

    [Fact]
    public void FallsBackOnACompilerPropertyTheFileReadsElsewhere()
    {
        var before = Edit("<Nullable>enable</Nullable>", "<Nullable>enable</Nullable><DefineConstants>A</DefineConstants><Marker>$(DefineConstants)</Marker>");
        var after = before.Replace("<DefineConstants>A</DefineConstants>", "<DefineConstants>B</DefineConstants>");
        Assert.Equal("$(DefineConstants), which the project file reads elsewhere", ProjectFileEdit.MoreThanCompilerInput(before, after, out _));
    }

    // The daemon looks for these in the project's imports, which may read a compiler setting for more than csc.
    [Fact]
    public void NamesHowTheChangedSettingsAreRead()
    {
        var after = Edit("<Nullable>enable</Nullable>", "<Nullable>disable</Nullable><DefineConstants>FLAG</DefineConstants>")
            .Replace(End, "  <ItemGroup><Compile Include=\"Extra.txt\" /></ItemGroup>\n</Project>");
        Assert.Null(ProjectFileEdit.MoreThanCompilerInput(Project, after, out var changed));
        Assert.Equal(["$(DefineConstants", "$(Nullable", "@(Compile"], changed);
        Assert.Null(ProjectFileEdit.MoreThanCompilerInput(Project, Project.Replace(End, "  <!-- edited -->\n</Project>"), out changed));
        Assert.Empty(changed);
    }

    [Theory]
    [InlineData("a copy item under a condition", "<ItemGroup Condition=\"$(DefineConstants.Contains('COPY'))\"><None Include=\"data.txt\" CopyToOutputDirectory=\"Always\" /></ItemGroup>", "$(DefineConstants")]
    [InlineData("a target after CoreCompile", "<Target Name=\"Weave\" AfterTargets=\"CoreCompile\" Condition=\"'$(Optimize)' == 'true'\" />", "$(Optimize")]
    [InlineData("a target that reads the sources", "<Target Name=\"Lint\" BeforeTargets=\"CoreCompile\"><Exec Command=\"lint @(Compile)\" /></Target>", "@(Compile")]
    [InlineData("a define appended to", "<PropertyGroup><DefineConstants>$(DefineConstants);SHARED</DefineConstants><NoWarn>$(NoWarn);CS1591</NoWarn></PropertyGroup>", null)]
    [InlineData("a comment", "<!-- $(DefineConstants) $(Optimize) -->", null)]
    public void FindsAnImportThatReadsAChangedSetting(string _, string import, string? expected) =>
        Assert.Equal(expected, ProjectFileEdit.ReadBeyondCompilerInput($"<Project>{import}</Project>", ["$(DefineConstants", "$(Optimize", "@(Compile"]));

    [Fact]
    public void FallsBackOnAFileThatDoesNotParse() =>
        Assert.StartsWith("the file doesn't parse", ProjectFileEdit.MoreThanCompilerInput(Project, Project.Replace(End, ""), out _));
}
