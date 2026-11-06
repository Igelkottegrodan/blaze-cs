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
    public void RunsOnTargetRuntime() => Assert.That(
        Environment.Version.Major.ToString(),
        Is.EqualTo(typeof(WelcomeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "TargetRuntimeMajor").Value));
}
