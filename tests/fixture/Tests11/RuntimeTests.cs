using System.Reflection;
using Xunit;

namespace Tests11;

public class RuntimeTests
{
    const int Edit = 0;

    [Fact]
    public void RunsOnTargetRuntime() => Assert.Equal(
        typeof(RuntimeTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "TargetRuntimeMajor").Value,
        Environment.Version.Major.ToString());
}
