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

    // Lib's static field and resource, which the hot reload check edits.
    [Fact]
    public void Exclaims() => Assert.Equal("Hi!", Lib.Salutation.Exclaim("Hi"));

    [Fact]
    public void ReadsGreeting() => Assert.Equal("Hello", Lib.Salutation.Greeting());

    [Fact]
    public void Computes() => Assert.Equal(1, Lib.Salutation.Value);
}

// Uses nothing of Lib, so a hot reloaded edit there runs only WelcomeTests, and lists this class as unchanged.
public class RuntimeTests
{
    [Fact]
    public void RunsOnTargetRuntime() => Assert.Equal(
        typeof(RuntimeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "TargetRuntimeMajor").Value,
        Environment.Version.Major.ToString());
}
