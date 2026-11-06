using System.Reflection;
using Xunit;

namespace XunitV2Tests;

public class WelcomeTests
{
    [Fact]
    public void GreetsByName() => Assert.StartsWith("Hello", Consumer.Welcome.For("you"));

    [Fact]
    public void ListsGreeterMembers() => Assert.Contains("Greet", Consumer.Welcome.LibMembers);
}

// Uses nothing of Lib, so a hot reloaded edit there runs only WelcomeTests, and lists this class as unchanged.
public class RuntimeTests
{
    [Fact]
    public void RunsOnTargetRuntime() => Assert.Equal(
        typeof(RuntimeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "TargetRuntimeMajor").Value,
        Environment.Version.Major.ToString());
}
