using System.Buffers.Binary;
using System.IO.Compression;
using System.Threading.Channels;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Capture;

/// <summary>
/// P306 — off-thread PNG frame saver for the headless video capture.
///
/// Rendering every captured frame to disk synchronously is the single largest
/// cost in the capture loop (1280x720 PNG encode measured ~50ms/frame on this
/// box), and if it runs on the render thread it craters the very client FPS
/// the stress test is trying to measure. So the render thread does only the
/// cheap GPU readback (<see cref="EnqueueFromTarget"/>, a GetData into a
/// reusable Color[]), then hands the pixel buffer to a dedicated background
/// thread that encodes the PNG and writes the file. The render loop therefore
/// pays GetData readback but never the PNG encode, keeping renderMeanFps an
/// honest measure of N-ship render cost rather than disk-write cost.
///
/// PNG bytes are produced by a small built-in encoder (signature + IHDR + a
/// zlib-filtered IDAT + IEND with correct CRC32/Adler32) so the worker needs
/// no GPU texture handle — everything is pure CPU on the already-read-back
/// Color[]. A bounded channel prevents unbounded buffering if the encoder ever
/// falls behind the renderer (then frames are dropped, never deadlocked).
/// </summary>
public sealed class CapturePngSaver : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly Channel<(Color[] Pixels, string Path)> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public CapturePngSaver(int width, int height)
    {
        _width = width;
        _height = height;
        // Bounded so a slow encoder drops frames instead of growing unbounded.
        _queue = Channel.CreateBounded<(Color[], string)>(new BoundedChannelOptions(24)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        _worker = Task.Factory.StartNew(Loop, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private async Task Loop()
    {
        // Kept BELOW normal so the PNG encoder never steals CPU from the render
        // thread — renderMeanFps must measure N-ship render cost, not encoder cost.
        try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal; } catch { }
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
        {
            while (reader.TryRead(out var job))
            {
                try
                {
                    byte[] png = Encode(job.Pixels, _width, _height);
                    await File.WriteAllBytesAsync(job.Path, png, _cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // best-effort: a failed frame write must never crash capture
                }
            }
        }
    }

    /// <summary>Read the current offscreen target back into <paramref name="scratch"/>
    /// and enqueue it for background PNG persistence. <paramref name="scratch"/> must be
    /// a Color[] of at least Width*Height and is owned exclusively by the render thread.
    /// Returns true if the frame was accepted for writing (false if the bounded queue
    /// is full — the frame is dropped, never blocking the renderer).</summary>
    public bool EnqueueFromTarget(RenderTarget2D target, Color[] scratch, string path)
    {
        target.GetData<Color>(0, new Rectangle(0, 0, _width, _height), scratch, 0, _width * _height);
        var copy = new Color[_width * _height];
        Array.Copy(scratch, copy, scratch.Length);
        return _queue.Writer.TryWrite((copy, path));
    }

    /// <summary>
    /// P417 — expose the pure RGBA→PNG encoder so the standalone screenshot
    /// feature (<see cref="ScreenShot"/>) can reuse it. Synchronous (no
    /// channel): a one-shot screenshot wants the bytes written now, unlike the
    /// streaming capture path.
    /// </summary>
    public static byte[] Encode(Color[] px, int w, int h)
    {
        int stride = 1 + w * 4;               // filter byte + RGBA row
        int rawLen = stride * h;
        byte[] raw = new byte[rawLen];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;                      // filter: None
            int baseIdx = y * w;
            for (int x = 0; x < w; x++)
            {
                Color c = px[baseIdx + x];
                raw[o++] = c.R;
                raw[o++] = c.G;
                raw[o++] = c.B;
                raw[o++] = c.A;
            }
        }

        byte[] idat = Zlib(raw);

        // IHDR data (13 bytes)
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // color type: RGBA
        ihdr[10] = 0;   // compression
        ihdr[11] = 0;   // filter
        ihdr[12] = 0;   // interlace

        using var ms = new MemoryStream();
        ms.WriteByte(0x89); ms.WriteByte(0x50); ms.WriteByte(0x4E); ms.WriteByte(0x47);
        ms.WriteByte(0x0D); ms.WriteByte(0x0A); ms.WriteByte(0x1A); ms.WriteByte(0x0A);
        WriteChunk(ms, "IHDR", ihdr);
        WriteChunk(ms, "IDAT", idat);
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> typeBytes = stackalloc byte[4];
        typeBytes[0] = (byte)type[0]; typeBytes[1] = (byte)type[1];
        typeBytes[2] = (byte)type[2]; typeBytes[3] = (byte)type[3];
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(typeBytes);
        crc = Crc32(crc, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> data) => Crc32(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1u)));
        }
        return crc;
    }

    /// <summary>Wrap raw deflate in a zlib stream with a correct Adler32 checksum.</summary>
    private static byte[] Zlib(byte[] raw)
    {
        using var comp = new MemoryStream();
        using (var ds = new DeflateStream(comp, CompressionLevel.Fastest, leaveOpen: true))
            ds.Write(raw, 0, raw.Length);

        byte[] deflated = comp.ToArray();
        byte[] zlib = new byte[deflated.Length + 6];
        zlib[0] = 0x78;                       // CMF: deflate, 32K window
        zlib[1] = 0x9C;                       // FLG: default compression
        Array.Copy(deflated, 0, zlib, 2, deflated.Length);
        uint a1 = 1, a2 = 0;
        foreach (byte b in raw)
        {
            a1 = (a1 + b) % 65521;
            a2 = (a2 + a1) % 65521;
        }
        uint adler = (a2 << 16) | a1;
        BinaryPrimitives.WriteUInt32BigEndian(zlib.AsSpan(zlib.Length - 4), adler);
        return zlib;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); _worker.Wait(2000); } catch { }
        _cts.Dispose();
    }
}
