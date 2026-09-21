using System.Collections.Concurrent;

namespace Allegiance.Core;

/// <summary>
/// P318 (stability review finding H6) — verbosity-gated, non-blocking console
/// logging for the high-frequency per-tick/per-message diagnostic lines. The sim
/// thread must never block on stdout (a stalled pipe / journal / docker-logs
/// consumer is an independent full-freeze vector), so gated lines are enqueued
/// onto a bounded queue drained by a single background thread; when the consumer
/// is stalled the queue fills and further lines are <b>dropped</b> rather than
/// blocking the caller.
/// <para>
/// When <see cref="Verbose"/> is false (the production default — set by
/// <c>Server.Boot</c> from <c>ServerConfig.ConsoleVerbose</c>) the gated lines
/// are not written at all, removing both the review's measured console-write
/// volume and the freeze vector outright.
/// </para>
/// </summary>
public static class ConsoleLog
{
    /// <summary>Gate for the high-frequency lines; written at boot from config.</summary>
    public static volatile bool Verbose;

    private const int Capacity = 8192;
    private static BlockingCollection<string> _queue = new(Capacity);
    private static Thread _drain = StartDrain();

    // P318 test seams — the live proofs need to observe the gated-line volume the
    // sink actually emitted and to prove Write() never blocks even with a stalled
    // consumer. Kept internal (Core.Tests has InternalsVisibleTo).
    internal static long TotalWrites;       // every gated-line write attempt (== old per-tick volume)
    internal static long EmittedWrites;     // actually enqueued to the drain queue
    internal static long DroppedWrites;     // dropped because the consumer stalled (queue full)

    private static Thread StartDrain()
    {
        var t = new Thread(Drain) { IsBackground = true, Name = "console-log-drain" };
        t.Start();
        return t;
    }

    /// <summary>
    /// Emit one gated diagnostic line. No-op unless <see cref="Verbose"/> is true.
    /// Never blocks the caller: enqueues to the bounded queue and drops when the
    /// queue is full (a stalled stdout consumer), so the sim thread always returns
    /// immediately. Increments <see cref="TotalWrites"/> regardless of the gate so
    /// the pre-fix per-tick volume is observable for measurement.
    /// </summary>
    public static void Write(string line)
    {
        Interlocked.Increment(ref TotalWrites);
        if (!Verbose) return;
        if (_queue.TryAdd(line)) Interlocked.Increment(ref EmittedWrites);
        else Interlocked.Increment(ref DroppedWrites);
    }

    private static void Drain()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try { Console.WriteLine(line); }
            catch { /* a broken console must never kill the drain thread */ }
        }
    }

    /// <summary>Test seam: wait until the drain queue is empty (background thread
    /// has written everything enqueued so far to the current Console.Out).</summary>
    internal static void Flush(int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (_queue.Count > 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(1);
    }

    /// <summary>
    /// Test seam: reset the singleton sink cleanly so a test that flooded the queue
    /// (a verbose/stalled run) cannot bleed leftover lines into a later test's
    /// captured Console.Out. Completes the current queue, joins the drain thread
    /// (writing any backlog to the current Console.Out), clears the counters, and
    /// starts a fresh queue + drain. Call AFTER restoring Console.Out.
    /// </summary>
    internal static void ResetForTest(int joinMs = 8000)
    {
        Verbose = false;
        _queue.CompleteAdding();
        _drain.Join(Math.Min(joinMs, Math.Max(50, (int)(_queue.Count * 2 + 50))));
        _queue = new BlockingCollection<string>(Capacity);
        _drain = StartDrain();
        Interlocked.Exchange(ref TotalWrites, 0);
        Interlocked.Exchange(ref EmittedWrites, 0);
        Interlocked.Exchange(ref DroppedWrites, 0);
    }
}
