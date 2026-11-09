using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Xna.Framework;

namespace HudProof;

/// <summary>P980 — lossless RGBA8 PNG read/write on the CPU.
///
/// The golden contract is DECODED pixels, not PNG bytes, so both sides of a
/// comparison must decode identically. MonoGame's <c>Texture2D.FromStream</c>
/// decodes through the GPU: comparing 1764 combinations (two 4K images each)
/// exhausted the Intel/Mesa execbuf memory and aborted the run. Decoding in
/// managed code removes the GPU from the golden path entirely and keeps the
/// hashes reproducible on any host.
///
/// Scope: exactly what <c>Texture2D.SaveAsPng</c> produces and what the tool
/// writes — 8-bit, non-interlaced, colour type 6 (RGBA) or 2 (RGB, alpha forced
/// to 255). Anything else fails loudly instead of being compared loosely.
/// </summary>
public static class PngCodec
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static Color[] Decode(string path, int expectedWidth, int expectedHeight)
    {
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < 8 || !file.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("not a PNG: " + path);
        int width = 0, height = 0;
        byte bitDepth = 0, colorType = 0, interlace = 0;
        using var idat = new MemoryStream();
        int position = 8;
        while (position + 8 <= file.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(position, 4));
            string type = Encoding.ASCII.GetString(file, position + 4, 4);
            int data = position + 8;
            if (data + length > file.Length) throw new InvalidDataException("truncated PNG chunk: " + path);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(data, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(data + 4, 4));
                    bitDepth = file[data + 8];
                    colorType = file[data + 9];
                    interlace = file[data + 12];
                    break;
                case "IDAT":
                    idat.Write(file, data, length);
                    break;
                case "IEND":
                    position = file.Length;
                    break;
            }
            position = data + length + 4;   // skip the CRC
        }
        if (bitDepth != 8 || interlace != 0 || colorType is not (6 or 2))
            throw new InvalidDataException($"unsupported PNG format (depth {bitDepth}, colour {colorType}, interlace {interlace}): {path}");
        if (width != expectedWidth || height != expectedHeight)
            throw new InvalidDataException($"golden size {width}x{height} != {expectedWidth}x{expectedHeight}: {path}");
        int channels = colorType == 6 ? 4 : 3;
        int stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var inflate = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < raw.Length)
            {
                int chunk = inflate.Read(raw, read, raw.Length - read);
                if (chunk <= 0) throw new InvalidDataException("short PNG image data: " + path);
                read += chunk;
            }
        }
        var pixels = new Color[width * height];
        var previous = new byte[stride];
        var line = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            Buffer.BlockCopy(raw, y * (stride + 1) + 1, line, 0, stride);
            Unfilter(filter, line, previous, channels);
            for (int x = 0; x < width; x++)
            {
                int i = x * channels;
                pixels[y * width + x] = channels == 4
                    ? new Color(line[i], line[i + 1], line[i + 2], line[i + 3])
                    : new Color(line[i], line[i + 1], line[i + 2], (byte)255);
            }
            (previous, line) = (line, previous);
        }
        return pixels;
    }

    private static void Unfilter(int filter, byte[] line, byte[] previous, int bpp)
    {
        switch (filter)
        {
            case 0: break;
            case 1: // Sub
                for (int i = bpp; i < line.Length; i++) line[i] += line[i - bpp];
                break;
            case 2: // Up
                for (int i = 0; i < line.Length; i++) line[i] += previous[i];
                break;
            case 3: // Average
                for (int i = 0; i < line.Length; i++)
                    line[i] += (byte)(((i >= bpp ? line[i - bpp] : 0) + previous[i]) / 2);
                break;
            case 4: // Paeth
                for (int i = 0; i < line.Length; i++)
                {
                    int left = i >= bpp ? line[i - bpp] : 0, up = previous[i], upLeft = i >= bpp ? previous[i - bpp] : 0;
                    int p = left + up - upLeft;
                    int pa = Math.Abs(p - left), pb = Math.Abs(p - up), pc = Math.Abs(p - upLeft);
                    line[i] += (byte)(pa <= pb && pa <= pc ? left : pb <= pc ? up : upLeft);
                }
                break;
            default:
                throw new InvalidDataException("unknown PNG filter " + filter);
        }
    }

    /// <summary>Lossless RGBA8 encode with filter type 0 (the baseline writer).</summary>
    public static void Encode(string path, Color[] pixels, int width, int height)
    {
        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * (stride + 1);
            raw[row] = 0;
            for (int x = 0; x < width; x++)
            {
                var c = pixels[y * width + x];
                int i = row + 1 + x * 4;
                raw[i] = c.R; raw[i + 1] = c.G; raw[i + 2] = c.B; raw[i + 3] = c.A;
            }
        }
        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        using var file = File.Create(path);
        file.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // RGBA
        header[10] = 0;  // deflate
        header[11] = 0;  // adaptive filtering
        header[12] = 0;  // no interlace
        WriteChunk(file, "IHDR", header);
        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in a) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (byte value in b) crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
