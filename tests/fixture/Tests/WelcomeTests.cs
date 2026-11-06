using System.Reflection;
using Xunit;

namespace Tests;

public class WelcomeTests
{
    [Fact]
    public void GreetsByName() => Assert.StartsWith("Hello", Consumer.Welcome.For("you"));

    [Fact]
    public void ListsGreeterMembers() => Assert.Contains("Greet", Consumer.Welcome.LibMembers);

    // Not run unless asked for: xUnit's report counts it in its total, `dotnet test` doesn't.
    [Fact(Explicit = true)]
    public void OnlyWhenAsked() { }

    [Fact]
    public void RunsOnTargetRuntime() => Assert.Equal(
        typeof(WelcomeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "TargetRuntimeMajor").Value,
        Environment.Version.Major.ToString());
}
