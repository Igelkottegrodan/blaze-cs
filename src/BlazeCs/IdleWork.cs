using System.Diagnostics;

namespace BlazeCs;

/// <summary>
/// Work the daemon does only while no command runs, one item at a time: it starts a while after a command
/// ends, and the next command cancels it. A command never waits for it: Cancel returns at once, and an item
/// still running stops at its next cancellation check, its result dropped. What finished stays until a command
/// that reports it takes it. Items it didn't finish are the caller's to start again at the next idle.
/// </summary>
/// <param name="label">What the daemon log calls a run, such as "background analysis".</param>
sealed class IdleWork<TKey, TResult>(string label, TimeSpan delay, Func<TKey, string> name) where TKey : notnull
{
    /// <summary>An item's result, with when it finished and how long it took.</summary>
    public sealed record Done(TKey Key, TResult Result, DateTime Finished, TimeSpan Took);

    readonly object _lock = new();
    readonly List<Done> _done = [];
    CancellationTokenSource? _cancel;

    /// <summary>
    /// The run the last <see cref="Start"/> began, from its delay to its end or cancellation; complete when
    /// none is pending. Work that should follow it (an idle collection) can wait for it.
    /// </summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Runs these items, in order, once the delay has passed without a command, after the previous run has
    /// stopped. Replaces any run still pending.
    /// </summary>
    public void Start(IReadOnlyList<(TKey Key, Func<CancellationToken, Task<TResult>> Work)> items)
    {
        lock (_lock)
        {
            _cancel?.Cancel();
            _cancel = items.Count == 0 ? null : new CancellationTokenSource();
            Running = _cancel is null ? Task.CompletedTask : Run(Running, items, _cancel.Token);
        }
    }

    /// <summary>A command starts: the run stops, without waiting for the item it was running.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _cancel?.Cancel();
            _cancel = null;
        }
    }

    /// <summary>What the runs finished since the last call, in the order they finished, once.</summary>
    public List<Done> Take()
    {
        lock (_lock)
        {
            var done = _done.ToList();
            _done.Clear();
            return done;
        }
    }

    async Task Run(Task previous, IReadOnlyList<(TKey Key, Func<CancellationToken, Task<TResult>> Work)> items, CancellationToken token)
    {
        // Two runs never overlap: a cancelled one may still be leaving an item.
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ForceYielding);
        var finished = 0;
        var started = false;
        var run = Stopwatch.StartNew();
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            started = true;
            Console.WriteLine($"{DateTime.Now:O} blaze-cs: {label} started: {string.Join(", ", items.Select(i => name(i.Key)))}");
            run.Restart();
            foreach (var (key, work) in items)
            {
                var itemStarted = Stopwatch.GetTimestamp();
                var result = await Task.Run(() => work(token), token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (token.IsCancellationRequested)
                        break;
                    _done.Add(new Done(key, result, DateTime.Now, Stopwatch.GetElapsedTime(itemStarted)));
                }
                finished++;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            Console.WriteLine($"{DateTime.Now:O} blaze-cs: {label} failed: {e}");
            return;
        }
        if (finished == items.Count)
            Console.WriteLine($"{DateTime.Now:O} blaze-cs: {label} finished in {run.ElapsedMilliseconds} ms: {string.Join(", ", items.Select(i => name(i.Key)))}");
        else if (started)
            Console.WriteLine($"{DateTime.Now:O} blaze-cs: {label} cancelled by a command after {run.ElapsedMilliseconds} ms, {finished} of {items.Count} done");
    }
}
