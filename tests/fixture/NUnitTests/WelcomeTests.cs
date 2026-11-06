using System.Reflection;
using NUnit.Framework;

namespace NUnitTests;

public class WelcomeTests
{
    [Test]
    public void GreetsByName() => Assert.That(Consumer.Welcome.For("you"), Does.StartWith("Hello"));

    [Test]
    public void ListsGreeterMembers() => Assert.That(Consumer.Welcome.LibMembers, Does.Contain("Greet"));

    [Test]
    public void AppliesRunSettings()
    {
        Assert.That(Environment.GetEnvironmentVariable("BLAZE_CS_FIXTURE_VARIABLE"), Is.EqualTo("from runsettings"));
        Assert.That(TestContext.Parameters["fixtureParameter"], Is.EqualTo("from runsettings"));
    }
}

// Uses nothing of Lib, so a hot reloaded edit there runs only WelcomeTests, and lists this class as unchanged.
public class RuntimeTests
{
    [Test]
    public void RunsOnTargetRuntime() => Assert.That(
        Environment.Version.Major.ToString(),
        Is.EqualTo(typeof(RuntimeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "TargetRuntimeMajor").Value));
}
