using System.Collections.Concurrent;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// P318 (stability review finding H6) + P619c — verbosity-gated, non-blocking
/// console logging for the high-frequency per-tick/per-message diagnostic lines,
/// extended in P619c into the SINGLE bounded, queued sink for ALL process
/// console output (see <see cref="Install"/>). The sim thread must never block
/// on stdout (a stalled pipe / journal / docker-logs consumer is an independent
/// full-freeze vector), so:
/// <list type="bullet">
/// <item>Gated lines are enqueued onto a bounded queue drained by one background
/// thread; when the consumer is stalled the queue fills and further lines are
/// <b>dropped</b> rather than blocking the caller.</item>
/// <item><see cref="Install"/> (called at server process startup) redirects
/// <c>Console.Out</c>/<c>Console.Error</c> to a queued writer bound to the SAME
/// queue, so a bare <c>Console.WriteLine</c> anywhere on the sim/broadcast/
/// transport paths costs an enqueue, never I/O.</item>
/// </list>
/// When <see cref="Verbose"/> is false (the production default — set by
/// <c>Server.Boot</c> from <c>ServerConfig.ConsoleVerbose</c>) the gated lines
/// are not written at all, removing both the measured console-write volume and
/// the freeze vector outright.
/// </summary>
public static class ConsoleLog
{
    /// <summary>Gate for the high-frequency lines; written at boot from config.</summary>
    public static volatile bool Verbose;

    private const int Capacity = 8192;
    private static BlockingCollection<string> _queue = new(Capacity);
    private static Thread _drain = StartDrain();
    // P619c — the queued writer currently installed over Console.Out/Error (null
    // until Install()). Its Passthrough is where the drain emits, so a redirected
    // Console.Out never recurses into the queue.
    private static QueuedConsoleWriter? _writer;
    private static volatile bool _installed;

    /// <summary>P619c test seam: true while the queued writer is installed over
    /// <c>Console.Out</c>/<c>Console.Error</c>.</summary>
    internal static bool IsInstalled => _writer is not null;

    // P318 test seams — the live proofs need to observe the gated-line volume the
    // sink actually emitted and to prove Write() never blocks even with a stalled
    // consumer. Kept internal (Core.Tests has InternalsVisibleTo).
    internal static long TotalWrites;       // every gated-line write attempt (== old per-tick volume)
    internal static long EmittedWrites;     // actually enqueued to the drain queue
    // P619c — dropped because the consumer stalled (queue full). Public so the
    // server's [egress] window line can report the per-window drop count.
    public static long DroppedWrites;

    /// <summary>
    /// P619c — install the bounded, queued writer as <c>Console.Out</c> /
    /// <c>Console.Error</c>. Called once at server process startup
    /// (<c>ServerMain.RunAsync</c>), NOT in <c>Server.Boot</c>, so in-process
    /// test servers that capture <c>Console.Out</c> are untouched. After this,
    /// every <c>Console.WriteLine</c> (plain or via <c>ConsoleLog.Write</c>)
    /// costs an enqueue on the caller's thread and the single background drain
    /// thread performs the actual I/O in batches. The drain emits to the
    /// pre-redirect <c>Console.Out</c> (captured here), so it never reads its
    /// own queue back.
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        var w = new QueuedConsoleWriter(_queue, Console.Out);
        _writer = w;
        // P829 — ONE synchronised wrapper for both streams. Console.SetOut/SetError
        // wrap a plain writer separately (two SyncTextWriters, two locks), and
        // TextWriter.WriteLine(string) is Write(value) + Write(newline): with two
        // locks a stdout and a stderr line interleave mid-line. Synchronized() of an
        // already-synchronised writer returns it as is, so both streams share a lock.
        var shared = TextWriter.Synchronized(w);
        Console.SetOut(shared);
        Console.SetError(shared);
    }

    /// <summary>P619c — undo <see cref="Install"/>: restore the original
    /// <c>Console.Out</c>/<c>Console.Error</c>. Test seam (harmless in prod; a
    /// server never uninstalls).</summary>
    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        if (_writer is { } w)
        {
            Console.SetOut(w.Passthrough);
            Console.SetError(w.Passthrough);
            _writer = null;
        }
    }

    /// <summary>
    /// P619c — test seam to point the drain's emission target at an arbitrary
    /// writer (e.g. a stalling writer to prove non-blocking, or a counting writer
    /// to measure volume). When installed, rewrites the queued writer's
    /// Passthrough; when not installed, acts on the current <c>Console.Out</c>.
    /// </summary>
    internal static void PointSinkForTest(TextWriter sink)
    {
        if (_writer is { } w) w.Passthrough = sink;
        else Console.SetOut(sink);
    }

    /// <summary>
    /// P619c — flush on shutdown: signal the drain to stop consuming and wait for
    /// it to write every already-enqueued line to the sink (bounded by
    /// <paramref name="joinMs"/>). Safe to call whether or not
    /// <see cref="Install"/> was used. Also restores the unredirected console so
    /// any process-exit trace after this writes synchronously and reliably.
    /// </summary>
    public static void Shutdown(int joinMs = 5000)
    {
        if (_drain.IsAlive)
        {
            try { _queue.CompleteAdding(); } catch { /* already completed */ }
            _drain.Join(joinMs);
        }
        Uninstall();
    }

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

    /// <summary>
    /// P654 (H-27) — make an attacker-supplied string safe to embed in a log
    /// line: truncate to <paramref name="maxLen"/> chars and escape every
    /// control/format character (CR/LF/ANSI-ESC and friends) as a literal
    /// <c>\uXXXX</c>, so a hostile 65 KB chat text or a rogue character name
    /// cannot forge log lines, inject ANSI escapes, or blow up memory/disk with
    /// one giant line. Use around any client-supplied string before it reaches
    /// <c>Console.WriteLine</c> / <c>ConsoleLog.Write</c>.
    /// </summary>
    public static string Sanitize(string? s, int maxLen = 128)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        if (s.Length > maxLen) s = s.Substring(0, maxLen) + "\u2026";
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                sb.Append($"\\u{(int)c:X4}");
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static void Drain()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                // Emit to the installed writer's Passthrough when we are
                // redirected (so the drain NEVER reads its own queue back — note
                // Console.Out wraps the writer in a SyncTextWriter, so we cannot
                // test `Console.Out is QueuedConsoleWriter`; we consult _writer).
                // When not installed (tests capture Console.Out directly), emit to
                // the CURRENT Console.Out so tests that SetOut a counter see it.
                var w = _writer;
                if (w is not null) w.Passthrough.WriteLine(line);
                else Console.Out.WriteLine(line);
            }
            catch { /* a broken console must never kill the drain thread */ }
        }
    }

    /// <summary>Test seam: wait until the drain queue is empty (background thread
    /// has written everything enqueued so far to the current sink).</summary>
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
    /// (writing any backlog to the current sink), clears the counters, and starts
    /// a fresh queue + drain. Call AFTER restoring/uninstalling Console.Out.
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

    /// <summary>
    /// P619c — a <c>TextWriter</c> that fans every line out to the shared bounded
    /// queue instead of doing synchronous I/O, used to replace
    /// <c>Console.Out</c>/<c>Console.Error</c> at startup. Each complete line is
    /// enqueued once; when the queue is full the line is dropped and counted
    /// (never blocks the caller). Partial lines (a Write without a trailing
    /// newline) are buffered until the next newline or <see cref="Flush"/>.
    /// </summary>
    /// <para>P829 — this ONE instance sits behind both <c>Console.Out</c> and
    /// <c>Console.Error</c>. <see cref="Install"/> now shares one synchronised wrapper
    /// between them; the partial-line buffer is also guarded by the writer's own lock so
    /// no caller path (a direct <c>SetOut</c> of this writer, a test seam) can tear it:
    /// unguarded, a bot host's stall-watchdog thread (stderr) racing bot logging
    /// (stdout) corrupted the <c>StringBuilder</c> and killed the process with
    /// <c>ArgumentOutOfRangeException ('chunkLength')</c> (rig batch 35, 2026-10-26).</para>
    private sealed class QueuedConsoleWriter : TextWriter
    {
        private readonly BlockingCollection<string> _queue;
        private readonly StringBuilder _buf = new();
        private readonly object _gate = new();
        public TextWriter Passthrough;

        public QueuedConsoleWriter(BlockingCollection<string> queue, TextWriter passthrough)
        {
            _queue = queue;
            Passthrough = passthrough;
        }

        public override Encoding Encoding => Console.OutputEncoding;

        private void Emit()
        {
            string line = _buf.ToString();
            _buf.Clear();
            if (_queue.TryAdd(line)) Interlocked.Increment(ref EmittedWrites);
            else Interlocked.Increment(ref DroppedWrites);
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            lock (_gate) WriteLocked(value);
        }

        private void WriteLocked(string value)
        {
            int start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\n')
                {
                    _buf.Append(value, start, i - start);
                    Emit();
                    start = i + 1;
                }
                // '\r' is ignored (LF-terminated lines; CRLF collapses to LF).
            }
            if (start < value.Length) _buf.Append(value, start, value.Length - start);
        }

        public override void Write(char value)
        {
            lock (_gate)
            {
                if (value == '\n') Emit();
                else if (value != '\r') _buf.Append(value);
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (_gate) WriteLocked(buffer, index, count);
        }

        private void WriteLocked(char[] buffer, int index, int count)
        {
            int segStart = index;
            for (int i = index; i < index + count; i++)
            {
                if (buffer[i] == '\n')
                {
                    _buf.Append(buffer, segStart, i - segStart);
                    Emit();
                    segStart = i + 1;
                }
            }
            if (segStart < index + count) _buf.Append(buffer, segStart, index + count - segStart);
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            lock (_gate)
                for (int i = 0; i < buffer.Length; i++) Write(buffer[i]);
        }

        public override void WriteLine() => Write('\n');

        public override void Flush()
        {
            lock (_gate)
                if (_buf.Length > 0) Emit();   // push any trailing unterminated line
        }
    }
}
