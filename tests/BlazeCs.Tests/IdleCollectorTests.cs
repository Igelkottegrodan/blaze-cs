using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// When the daemon collects after a command (<see cref="IdleCollector"/>): once it has been idle for the
/// delay and the work it was told to wait for has finished, never while a command runs, and a command that
/// arrives during a collection waits only for that collection.
/// </summary>
public class IdleCollectorTests
{
    static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How long a check that nothing happened waits, and how long after a collection a second one, if
    /// scheduled, would have run.
    /// </summary>
    static readonly TimeSpan Settled = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// How long a check waits for something that should happen: a loaded CI runner (Windows, with the other
    /// tests in parallel) can take longer than <see cref="Settled"/> to run a timer's continuation.
    /// </summary>
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    int _collections;

    /// <summary>Waits until a collection has run, then for <see cref="Settled"/>, and checks there was only one.</summary>
    async Task CollectedOnce()
    {
        var deadline = DateTime.UtcNow + Patience;
        while (Volatile.Read(ref _collections) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        await Task.Delay(Settled, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref _collections));
    }

    IdleCollector Collector() => new(Delay, () => Interlocked.Increment(ref _collections));

    [Fact]
    public async Task CollectsOnceIdleForTheDelay()
    {
        var collector = Collector();
        collector.Idle();
        Assert.Equal(0, _collections);

        await CollectedOnce();
    }

    [Fact]
    public async Task ACommandBeforeTheDelayCancelsIt()
    {
        var collector = Collector();
        collector.Idle();
        collector.Busy();

        await Task.Delay(Settled, TestContext.Current.CancellationToken);
        Assert.Equal(0, _collections);
    }

    [Fact]
    public async Task IdleAgainCollectsOnce()
    {
        var collector = Collector();
        collector.Idle();
        collector.Idle();

        await CollectedOnce();
    }

    [Fact]
    public async Task WaitsForTheWorkStillRunning()
    {
        var collector = Collector();
        var warmUp = new TaskCompletionSource();
        collector.Idle(warmUp.Task);

        await Task.Delay(Settled, TestContext.Current.CancellationToken);
        Assert.Equal(0, _collections);

        warmUp.SetException(new InvalidOperationException("a failed warm-up is still finished"));
        await CollectedOnce();
    }

    [Fact]
    public async Task ACommandDuringTheWaitForWorkCancelsIt()
    {
        var collector = Collector();
        var warmUp = new TaskCompletionSource();
        collector.Idle(warmUp.Task);
        collector.Busy();
        warmUp.SetResult();

        await Task.Delay(Settled, TestContext.Current.CancellationToken);
        Assert.Equal(0, _collections);
    }

    [Fact]
    public async Task ACommandDuringACollectionWaitsForItOnly()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var collector = new IdleCollector(Delay, () =>
        {
            started.Set();
            release.Wait();
        });
        collector.Idle();
        Assert.True(started.Wait(Patience, TestContext.Current.CancellationToken));

        var busy = Task.Run(collector.Busy, TestContext.Current.CancellationToken);
        await Task.Delay(Delay, TestContext.Current.CancellationToken);
        Assert.False(busy.IsCompleted);

        release.Set();
        await busy.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }
}
