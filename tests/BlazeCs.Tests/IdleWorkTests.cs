using System.Diagnostics;
using Xunit;

namespace BlazeCs.Tests;

/// <summary>
/// The background run a command never waits for (<see cref="IdleWork{TKey, TResult}"/>): it starts after the
/// delay, a command cancels it at once and keeps only what finished, and what it didn't finish runs at the next
/// idle.
/// </summary>
public sealed class IdleWorkTests
{
    static readonly TimeSpan Short = TimeSpan.FromMilliseconds(10);
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    static IdleWork<string, int> Work(TimeSpan delay) => new("test run", delay, key => key);

    static (string, Func<CancellationToken, Task<int>>) Item(string key, int result) => (key, _ => Task.FromResult(result));

    [Fact]
    public async Task ReportsWhatFinishedOnce()
    {
        var work = Work(Short);
        work.Start([Item("a", 1), Item("b", 2)]);
        await work.Running.WaitAsync(Patience, TestContext.Current.CancellationToken);

        work.Cancel();
        Assert.Equal([("a", 1), ("b", 2)], work.Take().Select(d => (d.Key, d.Result)));
        Assert.Empty(work.Take());
    }

    [Fact]
    public async Task WhatFinishedWaitsForACommandThatTakesIt()
    {
        var work = Work(Short);
        work.Start([Item("a", 1)]);
        await work.Running.WaitAsync(Patience, TestContext.Current.CancellationToken);
        // A command that reports nothing (stop) only cancels; the next idle's run adds to what is kept.
        work.Cancel();
        work.Start([Item("b", 2)]);
        await work.Running.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], work.Take().Select(d => d.Key));
    }

    [Fact]
    public async Task ACommandBeforeTheDelayCancelsTheRun()
    {
        var work = Work(TimeSpan.FromSeconds(30));
        var ran = false;
        work.Start([("a", _ => { ran = true; return Task.FromResult(1); })]);

        work.Cancel();
        await work.Running.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.False(ran);
        Assert.Empty(work.Take());
    }

    [Fact]
    public async Task ACommandDuringAnItemDoesNotWaitForIt()
    {
        var work = Work(Short);
        var started = new TaskCompletionSource();
        work.Start([Item("a", 1), ("b", async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 2;
        })]);
        await started.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        work.Cancel();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100), $"Cancel took {stopwatch.ElapsedMilliseconds} ms");
        await work.Running.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Equal(["a"], work.Take().Select(d => d.Key));
    }

    [Fact]
    public async Task AResultThatArrivesAfterTheCommandIsDropped()
    {
        var work = Work(Short);
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        // An item that doesn't check for cancellation finishes after the command started.
        work.Start([("a", async _ =>
        {
            started.SetResult();
            await release.Task;
            return 1;
        })]);
        await started.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        work.Cancel();
        release.SetResult();
        await work.Running.WaitAsync(Patience, TestContext.Current.CancellationToken);
        Assert.Empty(work.Take());
    }

    [Fact]
    public async Task TheNextRunStartsOnceTheCancelledOneHasStopped()
    {
        var work = Work(Short);
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var stopped = false;
        work.Start([("a", async _ =>
        {
            started.SetResult();
            await release.Task;
            stopped = true;
            return 1;
        })]);
        await started.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        work.Cancel();

        // The item the command interrupted runs again at the next idle, not beside the one still leaving.
        var overlapped = true;
        work.Start([("a", _ => { overlapped = !stopped; return Task.FromResult(2); })]);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        release.SetResult();
        await work.Running.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.False(overlapped);
        Assert.Equal([("a", 2)], work.Take().Select(d => (d.Key, d.Result)));
    }
}
