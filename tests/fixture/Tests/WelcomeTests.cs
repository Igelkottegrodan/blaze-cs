using Xunit;

namespace Tests;

public class WelcomeTests
{
    [Fact]
    public void GreetsByName() => Assert.StartsWith("Hello", Consumer.Welcome.For("you"));

    [Fact]
    public void ListsGreeterMembers() => Assert.Contains("Greet", Consumer.Welcome.LibMembers);
}
