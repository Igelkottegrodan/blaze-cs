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

    [TestMethod]
    public void RunsOnTargetRuntime() => Assert.AreEqual(
        typeof(WelcomeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "TargetRuntimeMajor").Value,
        Environment.Version.Major.ToString());
}
