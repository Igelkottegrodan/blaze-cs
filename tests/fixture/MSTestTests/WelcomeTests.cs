using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MSTestTests;

[TestClass]
public sealed class WelcomeTests
{
    [TestMethod]
    public void GreetsByName() => Assert.StartsWith("Hello", Consumer.Welcome.For("you"));

    [TestMethod]
    public void ListsGreeterMembers() => Assert.Contains("Greet", Consumer.Welcome.LibMembers);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void AppliesRunSettings()
    {
        Assert.AreEqual("from runsettings", Environment.GetEnvironmentVariable("BLAZE_CS_FIXTURE_VARIABLE"));
        Assert.AreEqual("from runsettings", TestContext.Properties["fixtureParameter"]);
    }
}

// Uses nothing of Lib, so a hot reloaded edit there runs only WelcomeTests, and lists this class as unchanged.
[TestClass]
public sealed class RuntimeTests
{
    [TestMethod]
    public void RunsOnTargetRuntime() => Assert.AreEqual(
        typeof(RuntimeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "TargetRuntimeMajor").Value,
        Environment.Version.Major.ToString());
}
