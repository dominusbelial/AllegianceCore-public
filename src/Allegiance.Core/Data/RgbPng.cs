using System.Buffers.Binary;
using System.IO.Compression;

namespace Allegiance.Core.Imaging;

/// <summary>One decoded PNG: 8-bit-per-channel RGBA, top-left origin.</summary>
public sealed record PngImage(int Width, int Height, byte[] Rgba);

/// <summary>
/// P824 — minimal PNG codec for the authoring pipeline (no external
/// dependencies): <see cref="Encode"/> writes 8-bit RGBA with filter 0 rows;
/// <see cref="Decode"/> reads 8-bit RGB (colour type 2) and RGBA (colour type
/// 6), all five scanline filters, non-interlaced — the subset Blender writes
/// for base-colour images and the subset the glTF exporter needs. PNG is a
/// W3C Recommendation: <c>RFC 2083</c>-style chunk stream, zlib IDAT
/// (<see cref="ZLibStream"/>), CRC-32 per chunk.
/// </summary>
public static class RgbPng
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // -- encode --------------------------------------------------------------

    public static byte[] Encode(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"PNG size {width}×{height} must be positive");
        if (rgba.Length != width * height * 4)
            throw new InvalidDataException($"rgba length {rgba.Length} != {width}×{height}×4");

        // Raw scanlines: filter byte 0 + RGBA row.
        var raw = new byte[height * (1 + width * 4)];
        for (int y = 0; y < height; y++)
        {
            raw[y * (1 + width * 4)] = 0;               // filter: None
            Buffer.BlockCopy(rgba, y * width * 4, raw, y * (1 + width * 4) + 1, width * 4);
        }

        using var ms = new MemoryStream();
        ms.Write(Signature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;    // bit depth
        ihdr[9] = 6;    // colour type: RGBA
        ihdr[10] = 0;   // deflate
        ihdr[11] = 0;   // adaptive filtering
        ihdr[12] = 0;   // not interlaced
        WriteChunk(ms, "IHDR"u8, ihdr);

        var idat = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);
        WriteChunk(ms, "IDAT"u8, idat.ToArray());

        WriteChunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, Crc32(type, data));
        s.Write(crcBuf);
    }

    // -- decode --------------------------------------------------------------

    public static PngImage Decode(byte[] png, int maxDimension = int.MaxValue, bool texture = false)
    {
        if (png.Length < Signature.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            throw new InvalidDataException("not a PNG (bad signature)");
        int pos = Signature.Length;
        (int width, int height, int channels) = (0, 0, 0);
        using var idat = new MemoryStream(png.Length);
        bool seenEnd = false;
        Span<byte> typeBuf = stackalloc byte[4];   // CA2014: hoisted out of the chunk loop
        while (pos + 8 <= png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
            uint type = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 4));
            if (len < 0 || (long)pos + 12 + len > png.Length)
                throw new InvalidDataException($"truncated PNG: chunk at 0x{pos:X} claims {len} bytes past the end");
            var chunk = png.AsSpan(pos + 8, len);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + len));
            // PNG CRC covers chunk-type + chunk-data.
            BinaryPrimitives.WriteUInt32BigEndian(typeBuf, type);
            if (Crc32(typeBuf, chunk) != crc)
                throw new InvalidDataException($"PNG chunk at 0x{pos:X} fails its CRC (corrupt file)");
            pos += 12 + len;

            switch (type)
            {
                case 0x49484452: // IHDR
                    if (len != 13) throw new InvalidDataException("PNG IHDR must be 13 bytes");
                    width = BinaryPrimitives.ReadInt32BigEndian(chunk);
                    height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]);
                    int depth = chunk[8], colour = chunk[9], compression = chunk[10],
                        filterMethod = chunk[11], interlace = chunk[12];
                    if (width <= 0 || height <= 0)
                        throw new InvalidDataException($"PNG size {width}×{height} must be positive");
                    if (width > maxDimension || height > maxDimension || (long)width * height > int.MaxValue / 4)
                        throw new InvalidDataException("PNG dimensions exceed the decode budget");
                    if (depth != 8)
                        throw new InvalidDataException($"unsupported PNG bit depth {depth} (only 8)");
                    if (compression != 0 || filterMethod != 0)
                        throw new InvalidDataException("unsupported PNG compression/filter method");
                    if (interlace != 0)
                        throw new InvalidDataException("interlaced PNG not supported");
                    channels = colour switch
                    {
                        2 => 3,     // RGB
                        6 => 4,     // RGBA
                        0 => throw new InvalidDataException("greyscale PNG not supported"),
                        3 => throw new InvalidDataException("palette PNG not supported"),
                        4 => throw new InvalidDataException("greyscale+alpha PNG not supported"),
                        _ => throw new InvalidDataException($"unknown PNG colour type {colour}"),
                    };
                    break;
                case 0x49444154: idat.Write(chunk); break; // IDAT
                case 0x74524E53 when texture: // tRNS needs the platform transparency decoder.
                    throw new InvalidDataException("PNG colour-key transparency requires platform decoding");
                case 0x49454E44: seenEnd = true; break;            // IEND
            }
        }
        if (!seenEnd) throw new InvalidDataException("truncated PNG (no IEND)");
        if (width == 0) throw new InvalidDataException("PNG has no IHDR");
        if (idat.Length == 0) throw new InvalidDataException("PNG has no IDAT");

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        int stride = width * channels;
        var rgba = new byte[width * height * 4];
        var prior = new byte[stride];
        var row = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int ft = zlib.ReadByte();
            try { zlib.ReadExactly(row); }
            catch (EndOfStreamException ex) { throw new InvalidDataException("truncated PNG scanline", ex); }
            switch (ft)
            {
                case 0: break;
                case 1: for (int i = channels; i < stride; i++) row[i] = (byte)(row[i] + row[i - channels]); break;
                case 2: for (int i = 0; i < stride; i++) row[i] = (byte)(row[i] + prior[i]); break;
                case 3:
                    for (int i = 0; i < stride; i++)
                    {
                        int left = i >= channels ? row[i - channels] : 0;
                        row[i] = (byte)(row[i] + ((left + prior[i]) >> 1));
                    }
                    break;
                case 4:
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= channels ? row[i - channels] : 0;   // left
                        int b = prior[i];                                 // up
                        int c = i >= channels ? prior[i - channels] : 0;  // up-left
                        int p = a + b - c;
                        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        int pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                        row[i] = (byte)(row[i] + pred);
                    }
                    break;
                default:
                    throw new InvalidDataException($"unknown PNG row filter {ft}");
            }

            for (int x = 0; x < width; x++)
            {
                int di = (y * width + x) * 4, si = x * channels;
                rgba[di] = row[si];
                rgba[di + 1] = row[si + 1];
                rgba[di + 2] = row[si + 2];
                rgba[di + 3] = channels == 4 ? row[si + 3] : (byte)255;
                // DesktopGL Texture2D.FromStream clears RGB only at zero alpha.
                if (texture && rgba[di + 3] == 0) rgba[di] = rgba[di + 1] = rgba[di + 2] = 0;
            }
            (row, prior) = (prior, row);
        }
        return new PngImage(width, height, rgba);
    }

    // -- CRC-32 (ISO 3309, the PNG polynomial 0xEDB88320) --------------------

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var v in a) crc = CrcTable[(crc ^ v) & 0xFF] ^ (crc >> 8);
        foreach (var v in b) crc = CrcTable[(crc ^ v) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
