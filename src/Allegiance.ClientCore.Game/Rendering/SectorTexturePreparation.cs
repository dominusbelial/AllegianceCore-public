using System.Buffers.Binary;
using System.Diagnostics;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Allegiance.Core.Imaging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

// The same source can appear in several models or sectors. Planet posters use a
// different colour key. Each renderer still owns and disposes its resident cache.
internal readonly record struct SectorTexture(string Stem, bool Poster, Dictionary<string, Texture2D> Cache)
{
    public bool SamePixels(SectorTexture other) => Poster == other.Poster
        && StringComparer.OrdinalIgnoreCase.Equals(Stem, other.Stem);
}

internal enum WarmTextureResult { Pending, Resident, Created, Deferred }

internal sealed class SectorTexturePreparation(IAssetSource assets) : IDisposable
{
    internal const int MaxJobs = 2;
    internal const int MaxSourceBytes = 12 << 20;
    internal const int MaxRgbaBytes = 16 << 20;
    internal const int FrameBytes = 4 << 20;
    private const int SliceBytes = 512 << 10;

    // Only the game thread touches this list, the upload cursors and GPU objects.
    // The worker gets just a source, a name and a colour-key flag.
    private sealed record Prepared(MdlTexture Texture, int SourceBytes, double DecodeMs, double ConvertMs);
    private sealed class Job(SectorTexture source, Task<Prepared?> pixels)
    {
        public readonly SectorTexture Source = source;
        public Task<Prepared?> Pixels = pixels;
        public Texture2D? Partial;
        public int Row;
        public double UploadMs;
        public bool Retired;
    }

    private readonly List<Job> _jobs = new(MaxJobs);
    private int _bytesLeft;
    private long _frameStart;
    private double _frameMs;
    private bool _closed;

    public void BeginFrame(double milliseconds)
    {
        _frameStart = Stopwatch.GetTimestamp();
        _frameMs = milliseconds;
        _bytesLeft = FrameBytes;
    }

    internal int UploadedThisFrame => FrameBytes - _bytesLeft;
    internal bool IsPrepared(SectorTexture source) => Find(source)?.Pixels.IsCompleted == true;

    public bool HasTime => Stopwatch.GetElapsedTime(_frameStart).TotalMilliseconds < _frameMs;

    private Job? Find(SectorTexture source)
    {
        foreach (var job in _jobs)
            if (!job.Retired && job.Source.SamePixels(source)) return job;
        return null;
    }

    // Offer in queue order. If promotion changes the head while both slots are
    // occupied, retire a lower-priority job. Its reservation lasts until exit.
    public bool Offer(SectorTexture source, bool first)
    {
        if (_closed) return false;
        if (source.Cache.ContainsKey(source.Stem) || Find(source) is not null) return true;
        _jobs.RemoveAll(static job => job.Retired && job.Pixels.IsCompleted);
        if (_jobs.Count == MaxJobs)
        {
            if (!first) return false;
            var victim = _jobs[^1];
            Retire(victim);
            if (!victim.Pixels.IsCompleted) return false;
            _jobs.Remove(victim);
        }
        string stem = source.Stem;
        bool poster = source.Poster;
        var input = assets;
        _jobs.Add(new Job(source, Task.Run(() => Decode(input, stem, poster))));
        return true;
    }

    private static Prepared? Decode(IAssetSource input, string stem, bool poster)
    {
        try
        {
            long start = Stopwatch.GetTimestamp();
            string path = stem + ".mdl";
            MdlTexture pixels;
            int sourceBytes;
            bool colourKey = poster;
            if (input.Exists(path))
            {
                using var stream = input.OpenRead(path);
                if (!stream.CanSeek) return null;
                long length = stream.Length;
                if (length is < 4 or > MaxSourceBytes) return null;
                Span<byte> magic = stackalloc byte[4];
                stream.ReadExactly(magic);
                bool binary = BinaryPrimitives.ReadUInt32LittleEndian(magic) == MdlTextureDecoder.MdlMagic;
                if (!binary && length > 16 * 1024) return null;
                byte[] encoded = new byte[(int)length];
                magic.CopyTo(encoded);
                stream.ReadExactly(encoded.AsSpan(4));
                sourceBytes = encoded.Length;
                if (binary)
                    pixels = MdlTextureDecoder.Decode(encoded, flipVertical: false, MaxRgbaBytes);
                else
                {
                    string imported = NativeStationTexture.ImportedImagePath(encoded)!;
                    pixels = ReadPng(input, imported, out int pngBytes);
                    sourceBytes += pngBytes;
                }
            }
            else if (poster)
            {
                // A bare sibling PNG goes straight through FromStream in PlanetPoster.
                // Only an image namespace gets the additional near-black colour key.
                var (_, png) = HdSkyGuard.NativePlanetTextureStems(stem);
                pixels = ReadPng(input, png + ".png", out sourceBytes);
                colourKey = false;
            }
            else return null;
            long decoded = Stopwatch.GetTimestamp();
            if (colourKey) MdlTextureDecoder.WritePremultipliedRgba(pixels.Rgba, pixels.Rgba, 10);
            return new(pixels, sourceBytes, Stopwatch.GetElapsedTime(start, decoded).TotalMilliseconds,
                colourKey ? Stopwatch.GetElapsedTime(decoded).TotalMilliseconds : 0);
        }
        catch (Exception) { return null; } // First use retains the renderer's miss/retry policy.
    }

    private static MdlTexture ReadPng(IAssetSource input, string path, out int bytes)
    {
        if (!input.Exists(path)) throw new FileNotFoundException(path);
        using var stream = input.OpenRead(path);
        long length = stream.CanSeek ? stream.Length : 0;
        if (length is < 1 or > MaxSourceBytes)
            throw new InvalidDataException("PNG exceeds the source budget");
        bytes = (int)length;
        var encoded = new byte[bytes];
        stream.ReadExactly(encoded);
        var image = RgbPng.Decode(encoded, maxDimension: 2048, texture: true);
        return new(image.Width, image.Height, image.Rgba);
    }

    public WarmTextureResult Upload(GraphicsDevice device, SectorTexture source, bool required)
    {
        if (source.Cache.ContainsKey(source.Stem)) return WarmTextureResult.Resident;
        var job = Find(source);
        if (job is null || (!required && !job.Pixels.IsCompleted)) return WarmTextureResult.Pending;
        // A draw that beat preparation joins exactly its own decode. No worker
        // needs a graphics or session lock, or calls back onto the game thread.
        long waitStart = Stopwatch.GetTimestamp();
        if (job.Pixels.GetAwaiter().GetResult() is not { } prepared)
        {
            _jobs.Remove(job);
            return WarmTextureResult.Deferred;
        }
        double waitMs = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        try
        {
            var pixels = prepared.Texture;
            int stride = checked(pixels.Width * 4);
            if (stride > FrameBytes && !required)
            {
                _jobs.Remove(job);
                return WarmTextureResult.Deferred;
            }
            while (job.Row < pixels.Height)
            {
                int rows = pixels.Height - job.Row;
                if (!required)
                {
                    if (!HasTime || _bytesLeft < stride) return WarmTextureResult.Pending;
                    rows = Math.Min(rows, Math.Min(_bytesLeft, Math.Max(stride, SliceBytes)) / stride);
                }
                long start = Stopwatch.GetTimestamp();
                job.Partial ??= new Texture2D(device, pixels.Width, pixels.Height, false, SurfaceFormat.Color);
                int bytes = rows * stride;
                job.Partial.SetData(0, new Rectangle(0, job.Row, pixels.Width, rows),
                    pixels.Rgba, job.Row * stride, bytes);
                job.UploadMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                job.Row += rows;
                if (!required) _bytesLeft -= bytes;
            }
            double total = prepared.DecodeMs + prepared.ConvertMs + job.UploadMs;
            if (AssetLoadLog.ShouldLog(total))
                AssetLoadLog.Note(source.Poster ? "planet poster" : "rock texture", source.Stem, total,
                    FormattableString.Invariant($"decode={prepared.DecodeMs:F3} ms/{prepared.SourceBytes} B convert={prepared.ConvertMs:F3} ms upload={job.UploadMs:F3} ms/{pixels.ByteCount} B required={required} wait={waitMs:F3} ms"));
            source.Cache.Add(source.Stem, job.Partial!);
            _jobs.Remove(job);
            return WarmTextureResult.Created;
        }
        catch
        {
            Retire(job);
            _jobs.Remove(job);
            return WarmTextureResult.Deferred;
        }
    }

    private static void Retire(Job job)
    {
        if (job.Retired) return;
        job.Retired = true;
        // Drop the result even if the pilot stays in the menu after a reset.
        // Retain just completion for capacity accounting and shutdown.
        job.Pixels = Discard(job.Pixels);
        job.Partial?.Dispose();
        job.Partial = null;
    }

    private static async Task<Prepared?> Discard(Task<Prepared?> pixels)
    {
        await pixels.ConfigureAwait(false);
        return null;
    }

    public void Reset()
    {
        foreach (var job in _jobs) Retire(job);
        _jobs.RemoveAll(static job => job.Pixels.IsCompleted);
    }

    public void Dispose()
    {
        _closed = true;
        Reset();
        foreach (var job in _jobs) job.Pixels.GetAwaiter().GetResult();
        _jobs.Clear();
    }
}
