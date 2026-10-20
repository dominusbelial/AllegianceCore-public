using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace Allegiance.ClientCore.MonoGame.Diagnostics;

/// <summary>
/// P749 — player crash safety. Guarantees that a run which ends in an
/// unhandled exception still leaves a diagnostic trail, and that every normal
/// run keeps a rolling console log, even when the client is launched from a
/// launcher (Finder, a .desktop shortcut, play.bat) where stdout/stderr are
/// discarded.
///
/// <list type="bullet">
///   <item><b>Ring buffer:</b> the last ~<see cref="RingCapacity"/> console
///     lines are kept so a crash report can show what led up to the fault.</item>
///   <item><b>Rolling <c>client.log</c>:</b> console output is mirrored to
///     <c>&lt;data-dir&gt;/logs/client.log</c>, rotated (renamed to
///     <c>client.log.1</c>) once it grows past <see cref="RollingCapBytes"/>,
///     so a normal run leaves a bounded record of its console.</item>
///   <item><b>Crash report:</b> an unhandled exception (AppDomain + the
///     MonoGame loop, via <see cref="Install"/>) writes
///     <c>&lt;data-dir&gt;/logs/crash-&lt;UTC time&gt;.txt</c> with version,
///     commit, OS, GPU, current mode, the exception and the ring buffer.</item>
/// </list>
///
/// The per-user data directory follows the client's existing convention
/// (<see cref="PlayerConfig.DirectoryPath"/>): macOS
/// <c>~/Library/Application Support/AllegianceCore</c>, Windows
/// <c>%LOCALAPPDATA%\AllegianceCore</c>, Linux
/// <c>~/.local/share/AllegianceCore</c>. Logs live under its
/// <c>logs/</c> subdirectory.
/// </summary>
public static class CrashLog
{
    /// <summary>How many console lines the crash report's ring buffer keeps.</summary>
    public const int RingCapacity = 220;   // ~200 lines plus slack

    /// <summary>Rotate client.log once it exceeds this many bytes (~2 MB).</summary>
    public const long RollingCapBytes = 2 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Queue<string> Ring = new();
    private static long _ringChars;
    private static TextWriter? _originalOut;
    private static TextWriter? _originalError;
    private static TeeTextWriter? _teeOut;
    private static TeeTextWriter? _teeError;
    private static StreamWriter? _rolling;
    private static string? _rollingPath;
    private static long _rollingBytes;
    private static volatile string _mode = "starting";
    private static volatile string _gpu = "";
    private static int _installed;

    /// <summary>Override the data directory (tests). Null = platform default.</summary>
    public static string? DataDirectoryOverride;

    /// <summary>The per-user AllegianceCore data directory (player.json lives here).</summary>
    public static string DataDirectory =>
        DataDirectoryOverride ?? PlayerConfig.DirectoryPath;

    /// <summary>Where crash reports and client.log are written.</summary>
    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Record the client's current activity for the crash header.</summary>
    public static void SetMode(string mode) => _mode = mode;

    /// <summary>Internal test seam: push a line into the crash ring WITHOUT the
    /// console redirect, so unit tests exercise the ring/header plumbing without
    /// touching the real app-data directory or redirecting the runner's console.</summary>
    internal static void PushLineForTest(string line)
    {
        lock (Gate)
        {
            _ringChars += line.Length;
            Ring.Enqueue(line);
            while (Ring.Count > RingCapacity || _ringChars > RingCapacity * 512)
                _ringChars -= Ring.Dequeue().Length;
        }
    }

    /// <summary>Record the GPU name for the crash header (best effort, set after device init).</summary>
    public static void SetGpu(string gpu) { if (!string.IsNullOrWhiteSpace(gpu)) _gpu = gpu; }

    /// <summary>
    /// Install the crash/console plumbing. Called once, as early as possible in
    /// <c>Program.Main</c> (before argument parsing that can throw). Safe to
    /// call more than once.
    /// </summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        _originalOut = Console.Out;
        _originalError = Console.Error;
        // Tee console output to both the original sink (when present) and our
        // ring buffer + rolling file. Console.SetOut must wrap the ORIGINAL
        // writer, not itself.
        _teeOut = new TeeTextWriter(_originalOut, AppendSink);
        _teeError = new TeeTextWriter(_originalError, AppendSink);
        Console.SetOut(_teeOut);
        Console.SetError(_teeError);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash(e.ExceptionObject as Exception, e.IsTerminating
                ? "AppDomain.UnhandledException (terminating)"
                : "AppDomain.UnhandledException");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };
    }

    /// <summary>
    /// Ensure the rolling client.log writer is open and returns the path it
    /// is writing to. Lazily creates the logs directory.
    /// </summary>
    public static string EnsureRolling()
    {
        lock (Gate)
        {
            if (_rolling is not null) return _rollingPath!;
            Directory.CreateDirectory(LogsDirectory);
            _rollingPath = Path.Combine(LogsDirectory, "client.log");
            _rollingBytes = 0;
            _rolling = new StreamWriter(new FileStream(_rollingPath,
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            { AutoFlush = true };
            return _rollingPath;
        }
    }

    private static void AppendSink(string text)
    {
        lock (Gate)
        {
            // Ring buffer: keep the last RingCapacity lines (approximate by
            // character budget + line count).
            if (text.Length > 0)
            {
                _ringChars += text.Length;
                Ring.Enqueue(text);
                while (Ring.Count > RingCapacity || _ringChars > RingCapacity * 512)
                    _ringChars -= Ring.Dequeue().Length;
            }

            EnsureRolling();
            _rolling!.Write(text);
            _rollingBytes += text.Length;
            if (_rollingBytes > RollingCapBytes) Rotate();
        }
    }

    private static void Rotate()
    {
        _rolling?.Close();
        string path = _rollingPath!;
        string old = path + ".1";
        try { File.Delete(old); File.Move(path, old); } catch { /* best effort */ }
        _rollingBytes = 0;
        _rolling = new StreamWriter(new FileStream(path,
            FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        { AutoFlush = true };
    }

    /// <summary>
    /// Write a crash report for <paramref name="exception"/> and return the
    /// file path. Public so tests can drive it with a fake exception. Also
    /// flushes the rolling log so no console output is lost.
    /// </summary>
    public static string WriteCrash(Exception? exception, string handler)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(LogsDirectory);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff");
            string path = Path.Combine(LogsDirectory, $"crash-{stamp}.txt");
            var sb = new StringBuilder();
            sb.AppendLine("AllegianceCore crash report");
            sb.AppendLine("===========================");
            sb.AppendLine($"Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Handler: {handler}");
            sb.AppendLine($"Version: {Version}");
            sb.AppendLine($"Commit: {Commit}");
            sb.AppendLine($"OS: {Os}");
            sb.AppendLine($"GPU: {(string.IsNullOrEmpty(_gpu) ? "(not captured)" : _gpu)}");
            sb.AppendLine($"Mode: {_mode}");
            sb.AppendLine($"Process: {Environment.ProcessPath ?? "(unknown)"} pid={Environment.ProcessId}");
            sb.AppendLine($"Framework: {Environment.Version}");
            sb.AppendLine();
            sb.AppendLine("Exception:");
            sb.AppendLine(exception?.ToString() ?? "(null exception object)");
            sb.AppendLine();
            sb.AppendLine($"--- last ~{Ring.Count} console lines ---");
            foreach (var line in Ring) sb.Append(line);
            sb.AppendLine();
            try { File.WriteAllText(path, sb.ToString()); }
            catch { /* logging must never itself crash the crash handler */ }
            try { _rolling?.Flush(); } catch { }
            return path;
        }
    }

    private static string Version =>
        typeof(CrashLog).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static string Commit
    {
        get
        {
            var attr = typeof(CrashLog).Assembly.GetCustomAttributesData()
                .FirstOrDefault(a => a.AttributeType == typeof(AssemblyInformationalVersionAttribute));
            string? v = attr?.ConstructorArguments.FirstOrDefault().Value?.ToString();
            string? candidate = v?.Split('+').ElementAtOrDefault(1);
            return candidate is { Length: >= 7 and <= 64 } && candidate.All(char.IsAsciiHexDigit)
                ? candidate.ToLowerInvariant() : "unknown";
        }
    }

    private static string Os => OperatingSystem.IsWindows() ? $"Windows {Environment.OSVersion.Version}"
        : OperatingSystem.IsMacOS() ? $"macOS {Environment.OSVersion.Version}"
        : OperatingSystem.IsLinux() ? $"Linux {Environment.OSVersion.Version}"
        : RuntimeInformation_Os();

    private static string RuntimeInformation_Os()
    {
        try { return System.Runtime.InteropServices.RuntimeInformation.OSDescription; }
        catch { return "Unknown"; }
    }

    /// <summary>A <see cref="TextWriter"/> that forwards to a real sink while
    /// mirroring every write into the crash log's ring buffer + rolling file.</summary>
    private sealed class TeeTextWriter(TextWriter? inner, Action<string> sink) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { sink(value.ToString()); inner?.Write(value); }
        public override void Write(string? value) { sink(value ?? ""); inner?.Write(value); }
        public override void WriteLine(string? value) { sink((value ?? "") + NewLine); inner?.WriteLine(value); }
        public override void Flush() { try { inner?.Flush(); } catch { } }
    }
}
