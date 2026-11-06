using Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ComponentTests;

public class GreetingTests
{
    [Fact]
    public async Task RendersGreeting() => Assert.Contains("Hello", await Render(new() { [nameof(Greeting.Name)] = "reader" }));

    // Badge.razor.css is scoped: its elements carry the b-... attribute the CSS bundle selects on.
    [Fact]
    public async Task ScopesBadgeCss() => Assert.Matches("<span class=\"badge\" b-[a-z0-9]{10}>", await Render(new() { [nameof(Greeting.Name)] = "css" }));

    static async Task<string> Render(Dictionary<string, object?> parameters)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Greeting>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }
}
