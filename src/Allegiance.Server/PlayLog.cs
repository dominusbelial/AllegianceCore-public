using System.Text.Json;
using System.Threading.Channels;

namespace Allegiance.Server;

/// <summary>Port analytics only; no simulation, admission or identity authority.</summary>
public sealed class PlayLogConfig
{
    public bool Enabled { get; set; }
    public string Path { get; set; } = "data/plays.jsonl";
    public string[] BotNames { get; set; } = Allegiance.Core.Identity.BotNameRules.Defaults();
    public int QueueCapacity { get; set; } = 1024;
    public long MaxFileBytes { get; set; } = 64 * 1024 * 1024;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path) || System.IO.Path.GetFileNameWithoutExtension(Path).Length == 0
            || !Path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || Path.IndexOfAny(['\0', '*', '?']) >= 0)
            throw new InvalidDataException("playLog.path must name a .jsonl file");
        try { _ = System.IO.Path.GetFullPath(Path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("playLog.path is invalid", ex); }
        if (QueueCapacity is < 1 or > 65536) throw new InvalidDataException("playLog.queueCapacity must be 1..65536");
        if (MaxFileBytes is < 1024 or > 1073741824) throw new InvalidDataException("playLog.maxFileBytes must be 1024..1073741824");
        if (BotNames is null || BotNames.Length > 128 || BotNames.Any(p => string.IsNullOrWhiteSpace(p)
            || p.Length > 128 || p == "*" || p[..^1].Contains('*')))
            throw new InvalidDataException("playLog.botNames requires exact names or trailing-* prefixes (not *)");
    }

    public bool IsBot(string name) => Allegiance.Core.Identity.BotNameRules.IsBot(BotNames, name);
}

internal sealed record PlayCiv(short Side, int Id, string Name);
internal abstract record PlayLogEntry(DateTimeOffset End);
internal sealed record PlaySessionEntry(DateTimeOffset Start, DateTimeOffset End, double Seconds,
    string Callsign, string Kind, string Client, string MatchId, PlayCiv[] Civs, PlayCiv? SideCiv,
    int Kills, int Deaths, int Ejects, bool Launched) : PlayLogEntry(End);
internal sealed record PlayMatchEntry(string MatchId, DateTimeOffset Start, DateTimeOffset End,
    PlayCiv[] Civs, string Map, int Humans, int Bots, short? Winner) : PlayLogEntry(End)
{
    public string Type => "match";
}

/// <summary>One consumer owns all disk I/O. TryWrite never waits on the tick thread.
/// At the monthly byte cap records are dropped (no unbounded spill files).
/// Flushes each complete line; graceful shutdown drains the queue.</summary>
internal sealed class PlayLogWriter : IDisposable
{
    private readonly Channel<PlayLogEntry> _queue;
    private readonly Task _worker;
    private readonly string _directory;
    private readonly string _stem;
    private readonly long _maxBytes;
    private long _dropped, _failed, _written;
    private int _disposed;
    internal long Dropped => Interlocked.Read(ref _dropped);
    internal long Failed => Interlocked.Read(ref _failed);
    internal long Written => Interlocked.Read(ref _written);

    internal PlayLogWriter(PlayLogConfig config)
    {
        config.Validate();
        string path = System.IO.Path.GetFullPath(config.Path);
        _directory = System.IO.Path.GetDirectoryName(path)!;
        _stem = System.IO.Path.GetFileNameWithoutExtension(path);
        _maxBytes = config.MaxFileBytes;
        _queue = Channel.CreateBounded<PlayLogEntry>(new BoundedChannelOptions(config.QueueCapacity)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        _worker = Task.Run(WriteAsync);
    }

    internal bool TryWrite(PlayLogEntry entry)
    {
        if (_queue.Writer.TryWrite(entry)) return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    private async Task WriteAsync()
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync())
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var utcNow = DateTimeOffset.UtcNow;
                var oldest = new DateTimeOffset(utcNow.Year, utcNow.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(-11);
                foreach (var file in Directory.EnumerateFiles(_directory, _stem + "-????-??.jsonl"))
                {
                    string month = System.IO.Path.GetFileNameWithoutExtension(file)[(_stem.Length + 1)..];
                    if (DateTimeOffset.TryParseExact(month, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var date) && date < oldest) File.Delete(file);
                }
                if (entry.End < oldest) { Interlocked.Increment(ref _dropped); continue; }
                string fileName = System.IO.Path.Combine(_directory, $"{_stem}-{entry.End.UtcDateTime:yyyy-MM}.jsonl");
                byte[] bytes = entry switch
                {
                    PlaySessionEntry session => JsonSerializer.SerializeToUtf8Bytes(session, ServerJsonContext.Default.PlaySessionEntry),
                    PlayMatchEntry match => JsonSerializer.SerializeToUtf8Bytes(match, ServerJsonContext.Default.PlayMatchEntry),
                    _ => throw new InvalidOperationException("Unknown play log record")
                };
                await using var stream = new FileStream(fileName, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
                if (stream.Length + bytes.Length + 1 > _maxBytes) { Interlocked.Increment(ref _dropped); continue; }
                await stream.WriteAsync(bytes);
                await stream.WriteAsync(new byte[] { (byte)'\n' });
                await stream.FlushAsync();
                Interlocked.Increment(ref _written);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                long failures = Interlocked.Increment(ref _failed);
                Interlocked.Increment(ref _dropped);
                // Do not echo paths or exception text into analytics diagnostics.
                if (failures == 1 || failures % 100 == 0) Console.Error.WriteLine($"[playlog] write failures={failures}; dropped={Dropped}");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        _worker.GetAwaiter().GetResult();
        Console.WriteLine($"[playlog] written={Written} dropped={Dropped} writeFailures={Failed}");
    }
}
