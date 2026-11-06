using System.Diagnostics;

namespace BlazeCs;

/// <summary>
/// Gives the memory a build grew the GC heap to back to the OS once the daemon has been idle for a few
/// seconds: an aggressive collection compacts the heap and decommits what it freed, so the daemon's RSS
/// falls to what it holds (jellyfin: from 1.4 GB to about 0.75 GB) and grows again during the next build.
/// The server GC on many cores otherwise keeps the peak committed between builds. It runs off the critical
/// path: a command cancels a scheduled collection, and one that arrives during a collection waits only for
/// it (a blocking collection stops every managed thread anyway).
/// </summary>
sealed class IdleCollector(TimeSpan delay, Action collect)
{
    /// <summary>After the command that went idle; long enough that a quick edit-build cycle skips it.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(3);

    /// <summary>Held during a collection, so a command that starts during one waits for it to end.</summary>
    readonly object _lock = new();

    /// <summary>The scheduled collection, if any: one that hasn't started yet.</summary>
    CancellationTokenSource? _pending;

    /// <summary>A command starts: no collection runs until it ends, and one already running ends first.</summary>
    public void Busy()
    {
        lock (_lock)
        {
            _pending?.Cancel();
            _pending = null;
        }
    }

    /// <summary>
    /// A command ended: collect once the daemon has been idle for the delay, after the work it left running
    /// (the first load's warm-up, the background analysis) has finished, unless another command starts first.
    /// </summary>
    public void Idle(Task? running = null)
    {
        CancellationToken token;
        lock (_lock)
        {
            _pending?.Cancel();
            _pending = new CancellationTokenSource();
            token = _pending.Token;
        }
        _ = CollectWhenIdle(running, token);
    }

    async Task CollectWhenIdle(Task? running, CancellationToken token)
    {
        try
        {
            if (running is not null)
                await running.WaitAsync(token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        lock (_lock)
        {
            if (token.IsCancellationRequested)
                return;
            _pending = null;
            collect();
        }
    }

    /// <summary>
    /// The daemon's collection, logged as one line other tools parse (see the README): when it ran, what it
    /// took, and the heap it left, which after a compacting full collection is the live size.
    /// <c>{time:O} blaze-cs: idle collection took {ms} ms; heap {MB} MB, committed {MB} MB, working set {MB} MB (was {MB} MB)</c>
    /// </summary>
    public static void CollectAndLog()
    {
        var before = Environment.WorkingSet;
        var stopwatch = Stopwatch.StartNew();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var took = stopwatch.ElapsedMilliseconds;
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        Console.WriteLine($"{DateTime.Now:O} blaze-cs: idle collection took {took} ms; heap {Megabytes(info.HeapSizeBytes)} MB, "
            + $"committed {Megabytes(info.TotalCommittedBytes)} MB, working set {Megabytes(Environment.WorkingSet)} MB (was {Megabytes(before)} MB)");
    }

    static long Megabytes(long bytes) => bytes / (1024 * 1024);
}
