using System.Collections.Concurrent;
using System.IO;

namespace Allegiance.ClientCore.MonoGame.Assets;

/// <summary>
/// An <see cref="IAssetSource"/> decorator that records which files the client
/// actually opens during a run — the P498 asset manifest. It wraps a real
/// source (typically <see cref="AssetSources.Artwork"/>) and, for every
/// <see cref="OpenRead"/>, records the relative path and the number of bytes
/// read. <see cref="Exists"/> probes are tracked separately so the manifest can
/// distinguish "opened" from merely "checked". Used by the client's
/// <c>--asset-manifest</c> launch flag to produce the input for Android
/// packaging (P499): the true, far-below-2.2-GB subset of Artwork the client
/// touches.
/// </summary>
public sealed class RecordingAssetSource : IAssetSource
{
    private readonly IAssetSource _inner;
    private readonly ConcurrentDictionary<string, long> _opened = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _probed = new(StringComparer.Ordinal);

    public RecordingAssetSource(IAssetSource inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>Files actually opened, mapped to total bytes read (distinct
    /// relative paths, '/' separated).</summary>
    public IReadOnlyDictionary<string, long> OpenedBytes => _opened;

    /// <summary>Files whose existence was probed but not opened (read).</summary>
    public IReadOnlyCollection<string> ProbedOnly => _probed.Keys.ToArray();

    /// <summary>Number of distinct files opened (read).</summary>
    public int OpenedCount => _opened.Count;

    /// <summary>Total bytes read across every distinct opened file.</summary>
    public long TotalBytes => _opened.Values.Sum();

    public bool Exists(string relativePath)
    {
        _probed.TryAdd(relativePath, 0);
        return _inner.Exists(relativePath);
    }

    public Stream OpenRead(string relativePath)
    {
        Stream inner = _inner.OpenRead(relativePath);
        return new CountingStream(inner, bytes => _opened.AddOrUpdate(relativePath, bytes, (_, old) => old + bytes));
    }

    public IEnumerable<string> Enumerate(string directoryRelativePath) =>
        _inner.Enumerate(directoryRelativePath);

    /// <summary>
    /// Serialize the manifest as a stable JSON document: the artwork root, the
    /// distinct opened files with their byte sizes, the probed-but-unopened
    /// files, and the totals. Written by the client on exit under
    /// <c>--asset-manifest &lt;path&gt;</c>.
    /// </summary>
    public string ToManifestJson(string artworkRoot)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        sb.Append("  \"artworkRoot\": ");
        sb.Append(System.Text.Json.JsonSerializer.Serialize(artworkRoot));
        sb.AppendLine(",");
        sb.AppendLine("  \"files\": [");
        bool first = true;
        foreach (var (path, bytes) in _opened.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!first) sb.AppendLine(",");
            first = false;
            sb.Append("    { \"path\": ")
              .Append(System.Text.Json.JsonSerializer.Serialize(path))
              .Append(", \"bytes\": ").Append(bytes).Append(" }");
        }
        sb.AppendLine();
        sb.AppendLine("  ],");
        sb.Append("  \"openedCount\": ").Append(_opened.Count).AppendLine(",");
        sb.Append("  \"totalBytes\": ").Append(TotalBytes).AppendLine();
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>A stream that tallies bytes read and hands the running total to a
    /// callback on dispose (so whole-file reads register even if the caller does
    /// not drain to EOF).</summary>
    private sealed class CountingStream(Stream inner, Action<long> onBytes) : Stream
    {
        private long _read;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            _read += n;
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            _read += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                onBytes(_read);
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
