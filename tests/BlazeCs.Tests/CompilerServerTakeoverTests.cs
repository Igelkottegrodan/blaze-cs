using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// Who serves the SDK's compiler-server pipe when several daemons want it: each <see cref="CompilerServer"/> here
/// stands for a daemon, on the pipe of a directory of its own, so no real SDK's pipe is touched.
/// </summary>
public sealed class CompilerServerTakeoverTests : IDisposable
{
    readonly string _directory = Directory.CreateTempSubdirectory("blaze-cs-takeover-").FullName;
    readonly List<CompilerServer> _servers = [];

    CompilerServer Daemon()
    {
        // Short, so the one that loses the race gives up quickly; it serves no compiles.
        var server = new CompilerServer(_directory, "hash", "dotnet", _ => null) { TakeoverTimeout = TimeSpan.FromSeconds(3) };
        _servers.Add(server);
        return server;
    }

    [Fact]
    public async Task OneOfTwoDaemonsTakingThePipeAtOnceGetsIt()
    {
        var holder = Daemon();
        await holder.Take().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(holder.IsListening);

        // Both ask the holder to shut down, and the holder answers both; only the one that then owns the mutex listens.
        CompilerServer[] contenders = [Daemon(), Daemon()];
        await Task.WhenAll(contenders.Select(c => c.Take())).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.False(holder.IsListening);
        var listening = Assert.Single(contenders, c => c.IsListening);
        Assert.True(CompilerServer.Held(listening.Name));

        // Once it gives the pipe up, no one holds it: a client starts the SDK's server.
        listening.Dispose();
        Assert.False(CompilerServer.Held(listening.Name));
    }

    [Fact]
    public async Task ADaemonTakesThePipeBackFromAnother()
    {
        var first = Daemon();
        await first.Take().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var second = Daemon();
        await second.Take().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(first.IsListening);
        Assert.True(second.IsListening);

        await first.Take().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.True(first.IsListening);
        Assert.False(second.IsListening);
    }

    public void Dispose()
    {
        foreach (var server in _servers)
            server.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
