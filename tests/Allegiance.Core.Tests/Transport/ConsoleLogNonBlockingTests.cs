using System.Diagnostics;
using System.Text;
using Allegiance.Core;

namespace Allegiance.Core.Tests.Transport;

/// <summary>
/// P619c scope 2 — the bounded, queued NON-BLOCKING console sink. The P318
/// tests proved the <b>gated</b> <c>ConsoleLog.Write</c> lines never block a
/// stalled stdout consumer. These tests extend that to the <b>plain</b>
/// <c>Console.WriteLine</c> path: after <see cref="ConsoleLog.Install"/> the
/// process <c>Console.Out</c>/<c>Console.Error</c> are redirected to a queued
/// writer sharing the one bounded queue + drain thread, so a bare
/// <c>Console.WriteLine</c> on the sim/broadcast path costs an enqueue, never
/// I/O — even when the underlying stdout consumer is completely stalled.
/// <para>
/// Pure unit tests (no server, no sockets) that mutate the GLOBAL console
/// (<c>Console.Out</c>/<c>Console.Error</c>) and the shared drain, so they are
/// parked in the serialized <c>LiveNetwork</c> collection alongside the other
/// console-capturing suites (TickProfilerTests, GcBootLineTests, the P318
/// tests) to avoid racing their captured output.
/// </para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class ConsoleLogNonBlockingTests
{
    /// <summary>A TextWriter that sleeps on every write — simulates a slow/stalled
    /// stdout consumer (a pipe/journal/docker-logs reader not keeping up).</summary>
    /// <summary>A thread-safe capture sink. ConsoleLog is a static, process-wide logger with
    /// one background drain thread; while a test points it at a sink, OTHER tests running in
    /// parallel (anything that boots a Server) also write through it, so the drain thread can
    /// append while the test reads. A plain StringWriter/StringBuilder is not thread-safe and
    /// threw ArgumentOutOfRangeException from StringBuilder.ToString() on GitHub CI
    /// (2026-10-13). Every write and the read take the same lock.</summary>
    private sealed class LockedStringWriter : StringWriter
    {
        private readonly object _gate = new();
        public override void Write(char value) { lock (_gate) base.Write(value); }
        public override void Write(string? value) { lock (_gate) base.Write(value); }
        public override void Write(char[] buffer, int index, int count) { lock (_gate) base.Write(buffer, index, count); }
        public override void WriteLine(string? value) { lock (_gate) base.WriteLine(value); }
        public override string ToString() { lock (_gate) return base.ToString(); }
    }

    private sealed class StallingWriter : TextWriter
    {
        private readonly int _perWriteMs;
        public StallingWriter(int perWriteMs) => _perWriteMs = perWriteMs;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(string? value) => Thread.Sleep(_perWriteMs);
        public override void Write(char[] buffer, int index, int count) => Thread.Sleep(_perWriteMs);
        public override void WriteLine(string? value) => Thread.Sleep(_perWriteMs);
    }

    /// <summary>
    /// ⭐ With <see cref="ConsoleLog.Install"/> active, a stalled stdout consumer
    /// cannot block a plain <c>Console.WriteLine</c>: the redirect makes every
    /// call an enqueue, the drain thread blocks alone, and the bounded queue
    /// DROPS rather than stalls the caller. Written thousands of lines through a
    /// writer that sleeps 20 ms per write, the whole loop must complete in far
    /// less than (lines × 20 ms) and drop lines instead of blocking.
    /// </summary>
    [Fact]
    [Trait("Tier", "Live")]
    public void Installed_plain_WriteLine_never_blocks_a_stalled_consumer_and_drops()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        // Gate off so only the redirected plain-WriteLine traffic is in play.
        ConsoleLog.Verbose = false;
        long droppedBefore;
        long drops;
        try
        {
            ConsoleLog.Install();
            // Point the drain at a consumer that sleeps 20 ms per write — vastly
            // slower than it can be fed — so the drain blocks permanently.
            ConsoleLog.PointSinkForTest(new StallingWriter(perWriteMs: 20));

            const int Lines = 200_000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Lines; i++)
                Console.WriteLine($"P619c plain nonblocking line {i}");
            long wallMs = sw.ElapsedMilliseconds;
            ConsoleLog.Flush(timeoutMs: 100);          // drain is blocked; short wait only

            // Non-blocking: 200k enqueues complete in bounded time, nowhere near
            // 200k × 20 ms = 4000 s a synchronous path would take.
            Assert.True(wallMs < 5_000,
                $"redirected Console.WriteLine must be an enqueue, not I/O (took {wallMs}ms for {Lines} lines against a 20ms/write stall)");

            // Dropped: with the drain blocked, the bounded queue filled and the
            // remainder were dropped and counted (never blocked).
            droppedBefore = ConsoleLog.DroppedWrites;
            drops = droppedBefore;
            Assert.True(drops > 0,
                $"a stalled 20ms/write consumer must fill the bounded queue and drop lines (dropped {drops} of {Lines})");
        }
        finally
        {
            ConsoleLog.Uninstall();
            // PointSinkForTest changes the installed writer's passthrough;
            // Uninstall therefore restores the stalling sink, not these originals.
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            ConsoleLog.ResetForTest();
        }
    }

    /// <summary>
    /// Uninstalling restores the original synchronous <c>Console.Out</c>, so a
    /// caller after shutdown is back to normal blocking stdout (the drain queue
    /// is no longer in the write path).
    /// </summary>
    [Fact]
    public void Uninstall_restores_the_original_Console_Out()
    {
        try
        {
            Assert.False(ConsoleLog.IsInstalled);
            ConsoleLog.Install();
            Assert.True(ConsoleLog.IsInstalled);
            ConsoleLog.Uninstall();
            Assert.False(ConsoleLog.IsInstalled);
            // A write after uninstall must NOT route through the queue — a
            // behaviour check that sidesteps SyncTextWriter reference identity:
            // write synchronously to a capture writer and confirm it appears
            // without any Shutdown/Flush.
            var sink = new LockedStringWriter();
            Console.SetOut(sink);
            Console.WriteLine("post-uninstall-direct");
            Assert.Contains("post-uninstall-direct", sink.ToString());
        }
        finally
        {
            ConsoleLog.Uninstall();
            ConsoleLog.ResetForTest();
        }
    }

    /// <summary>
    /// Shutdown drains every already-enqueued line to the sink and restores the
    /// synchronous console — nothing buffered is lost on process exit. Enqueue
    /// a batch against a FAST counting sink, then Shutdown, and every line must
    /// have reached the sink.
    /// </summary>
    [Fact]
    public void Shutdown_flushes_queued_lines_to_the_sink_and_restores_console()
    {
        var sink = new LockedStringWriter();
        try
        {
            ConsoleLog.Install();
            ConsoleLog.PointSinkForTest(sink);
            Console.WriteLine("flush-me-1");
            Console.WriteLine("flush-me-2");
            Console.WriteLine("flush-me-3");
            ConsoleLog.Shutdown();          // drains the queue, then uninstalls

            // Drain completed: all three lines reached the sink.
            var text = sink.ToString();
            Assert.Contains("flush-me-1", text);
            Assert.Contains("flush-me-2", text);
            Assert.Contains("flush-me-3", text);
            Assert.False(ConsoleLog.IsInstalled);   // console restored to sync
        }
        finally
        {
            ConsoleLog.Uninstall();
            ConsoleLog.ResetForTest();
        }
    }

    /// <summary>
    /// Multi-line payloads (embedded \n) are still delivered as distinct complete
    /// lines, and a trailing unterminated line is flushed by <see cref="TextWriter.Flush"/>.
    /// </summary>
    [Fact]
    public void Queued_writer_splits_multiline_and_flushes_trailing_partial_line()
    {
        var sink = new LockedStringWriter();
        try
        {
            ConsoleLog.Install();
            ConsoleLog.PointSinkForTest(sink);
            // P647 — tag the payload so the assertions ignore unrelated lines that
            // OTHER parallel tests write to the process-global Console.Out (which
            // Install redirects into this same sink). The gated behaviour under test
            // — multiline splitting + trailing-partial flush — is proven on the
            // uniquely-marked lines alone.
            Console.Out.Write("QWM:one\nQWM:two\nQWM:three");   // two complete + one partial
            Console.Out.Flush();                                 // pushes the partial "QWM:three"
            ConsoleLog.Shutdown();
            var text = sink.ToString();
            // The three MARKED lines arrive as distinct complete lines, in order,
            // including the trailing unterminated one (flush proves itself).
            var marked = text.Split('\n').Where(l => l.StartsWith("QWM:")).ToArray();
            Assert.Equal(3, marked.Length);
            Assert.Equal(new[] { "QWM:one", "QWM:two", "QWM:three" }, marked);
        }
        finally
        {
            ConsoleLog.Uninstall();
            ConsoleLog.ResetForTest();
        }
    }

    /// <summary>
    /// P633 — the [p433-diag] / per-bot per-pass diag lines are routed through
    /// <see cref="ConsoleLog.Write"/>, so the verbosity gate (OFF by default in
    /// the bot harness) SILENCES them entirely (they are counted but never
    /// enqueued), and flipping Verbose on re-enables them — without touching the
    /// synchronous lifecycle lines (plain Console.WriteLine is unaffected by the
    /// gate). This is the failing-first proof for item 5's log hygiene.
    /// </summary>
    [Fact]
    public void Verbose_gate_silences_and_reenables_routed_diag_lines()
    {
        var sink = new LockedStringWriter();
        try
        {
            ConsoleLog.ResetForTest();
            Assert.False(ConsoleLog.Verbose);      // default OFF (production bot default)
            ConsoleLog.PointSinkForTest(sink);

            // OFF: every Write is counted (observability) but NONE is emitted.
            ConsoleLog.Verbose = false;
            long totBefore = ConsoleLog.TotalWrites, emBefore = ConsoleLog.EmittedWrites;
            for (int i = 0; i < 100; i++) ConsoleLog.Write($"[p433-diag] bot b{i} ...");
            ConsoleLog.Flush();
            Assert.Equal(100, ConsoleLog.TotalWrites - totBefore);   // still measured
            Assert.Equal(0, ConsoleLog.EmittedWrites - emBefore);    // nothing emitted
            // P647 — previously asserted the global sink is empty (Length == 0). That is
            // wrong under full-suite load: OTHER tests running in parallel write to the
            // process-global Console.Out, which PointSinkForTest has pointed at this
            // sink, so unrelated lines appear. The gated lines are identified by the
            // [p433-diag] marker — assert none of THOSE reached the wire (the actual
            // behaviour under test), ignoring unrelated parallel console noise.
            Assert.Equal(0, sink.ToString().Split('\n').Count(l => l.Contains("[p433-diag]")));

            // ON: the same routed lines ARE emitted.
            ConsoleLog.Verbose = true;
            long emBefore2 = ConsoleLog.EmittedWrites;
            ConsoleLog.Write("[p433-diag] bot verbose line");
            ConsoleLog.Flush();
            Assert.Equal(1, ConsoleLog.EmittedWrites - emBefore2);
            Assert.Contains("[p433-diag]", sink.ToString());
        }
        finally
        {
            ConsoleLog.Uninstall();
            ConsoleLog.ResetForTest();
        }
    }

    /// <summary>A sink that checks every emitted line is one whole line written by one
    /// caller (no interleaving, no torn halves). Thread-safe: the drain thread writes.</summary>
    private sealed class LineCheckingWriter : TextWriter
    {
        private readonly object _gate = new();
        public long Lines, Torn;
        public string? FirstTorn;
        public override Encoding Encoding => Encoding.UTF8;
        public override void WriteLine(string? value)
        {
            bool ok = value is { Length: 64 } && (value.StartsWith("OUT-") || value.StartsWith("ERR-"))
                && value.AsSpan(4).IndexOfAnyExcept(value[0] == 'O' ? 'o' : 'e') < 0;
            lock (_gate)
            {
                Lines++;
                if (!ok) { Torn++; FirstTorn ??= value; }
            }
        }
        public override void Write(string? value) => WriteLine(value);
    }

    /// <summary>
    /// P829 — <see cref="ConsoleLog.Install"/> puts ONE queued writer behind both
    /// <c>Console.Out</c> and <c>Console.Error</c>; .NET's per-stream <c>SyncTextWriter</c>
    /// locks do not serialise the two, so concurrent stdout + stderr writers must not tear
    /// the shared partial-line buffer. On master this threw
    /// <c>ArgumentOutOfRangeException ('chunkLength')</c> from <c>StringBuilder.ToString</c>
    /// (it killed a rig bot worker: stall-watchdog stderr vs bot stdout) or emitted torn lines.
    /// </summary>
    [Fact]
    [Trait("Tier", "Live")]
    public void Installed_concurrent_stdout_and_stderr_writers_never_tear_lines()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        ConsoleLog.Verbose = false;
        var sink = new LineCheckingWriter();
        Exception? failure = null;
        try
        {
            ConsoleLog.Install();
            ConsoleLog.PointSinkForTest(sink);
            string outLine = "OUT-" + new string('o', 60), errLine = "ERR-" + new string('e', 60);
            const int PerThread = 100_000;
            void Hammer(Func<TextWriter> stream, string line)
            {
                try
                {
                    var w = stream();
                    for (int i = 0; i < PerThread; i++)
                    {
                        // Mix the single-call TextWriter overloads real callers use (a line
                        // split across several calls by its caller can interleave on any console).
                        switch (i & 3)
                        {
                            case 0: w.WriteLine(line); break;
                            case 1: w.Write(line + "\n"); break;
                            case 2: w.WriteLine(line.ToCharArray()); break;
                            default: w.WriteLine($"{line}"); break;
                        }
                    }
                }
                catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
            }
            var a = new Thread(() => Hammer(() => Console.Out, outLine));
            var b = new Thread(() => Hammer(() => Console.Error, errLine));
            a.Start(); b.Start(); a.Join(); b.Join();
            ConsoleLog.Flush();
        }
        finally
        {
            ConsoleLog.Uninstall();
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            ConsoleLog.ResetForTest();
        }
        Assert.Null(failure);
        Assert.True(sink.Lines > 0, "nothing reached the sink");
        Assert.True(sink.Torn == 0, $"{sink.Torn}/{sink.Lines} torn lines, first: '{sink.FirstTorn}'");
    }
}
