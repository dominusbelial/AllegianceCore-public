using System.Buffers.Binary;

// Lives in Allegiance.Core (moved there in P824 so the authoring pipeline in
// tools/mdl-to-glb can decode AND write textures headless — MdlFile, the mesh
// half of the format, is in this assembly/namespace already). The old
// Allegiance.ClientCore.MonoGame.Rendering location is gone; consumers import
// Allegiance.Core.

namespace Allegiance.Core;

/// <summary>
/// A single decoded texture from a <c>.mdl</c> file's <c>BinarySurfaceInfo</c>
/// payload: width/height plus raw RGBA (non-premultiplied, alpha always 255 —
/// RGB565 has no alpha). Pure data, no MonoGame types on the surface, so the
/// decoder is unit-testable headless.
/// </summary>
public sealed record MdlTexture(int Width, int Height, byte[] Rgba)
{
    /// <summary>The number of RGBA bytes (width × height × 4).</summary>
    public int ByteCount => Width * Height * 4;
}

/// <summary>
/// Faithful C# port of the texture half of the <c>.mdl</c> binary-surface
/// format (native: <c>engine/enginep.h:313</c> <c>BinarySurfaceInfo</c>,
/// <c>engine/modeler.cpp:715</c> <c>ImportImageFactory::Read</c>,
/// <c>engine/mdlbin.cpp</c> token stream) — the exact recipe proven in
/// <c>~/Development/allegiance-tools/reference/decode_texture.py</c> (and the
/// <c>allegiance-mdl-model-format</c> wiki page). The port already had the MESH
/// half (<see cref="Allegiance.Core.MdlFile"/>, <c>MeshGeo</c>/<c>FrameData</c>);
/// this is the missing TEXTURE half (<c>BinarySurfaceInfo</c> + RGB565 rows).
/// <para>
/// Native supplies file rows and pitch unchanged (engine/modeler.cpp:723-733)
/// and preserves mesh UVs (engine/device3d.cpp:2245-2246). Mesh materials and
/// top-left-origin HUD images must explicitly pass <c>flipVertical:false</c>.
/// The historical default <c>true</c> is retained for existing image/decal
/// consumers: those generate their own UVs (engine/context.cpp:237-240,
/// 659-660). It is not a general 3-D texture convention (P916).
/// </para>
/// </summary>
public static class MdlTextureDecoder
{
    public const uint MdlMagic = 0x0DEBADF00u;

    /// <summary>
    /// Parse a <c>.mdl</c> texture from raw bytes. With the default
    /// <paramref name="flipVertical"/> of <c>true</c> the pixel rows are
    /// mirrored top-to-bottom (legacy image/decal convention). Pass
    /// <c>false</c> for native mesh materials and top-left-origin HUD images.
    /// </summary>
    public static MdlTexture Decode(byte[] data, bool flipVertical = true)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (data.Length < 24)
            throw new InvalidDataException("truncated .mdl texture (no header)");

        int pos = 0;
        if (ReadU32(data, ref pos) != MdlMagic)
            throw new InvalidDataException("bad .mdl texture magic (expected 0x0DEBADF00)");
        _ = ReadU32(data, ref pos); // version

        int countNamespaces = (int)ReadU32(data, ref pos);
        int countImports = (int)ReadU32(data, ref pos);
        int countExports = (int)ReadU32(data, ref pos);
        int countDefinitions = (int)ReadU32(data, ref pos);

        for (int i = 0; i < countNamespaces; i++)
            SkipCString(data, ref pos);
        for (int i = 0; i < countImports; i++)
        {
            pos += 4; // import library index
            SkipCString(data, ref pos);
        }
        for (int i = 0; i < countExports; i++)
            SkipCString(data, ref pos);

        // The object-graph prologue preceding the BinarySurfaceInfo: a definition
        // index, an ObjectImport token (9), an import index, and an ObjectBinary
        // token (7) — four 32-bit words the reference skips blindly.
        pos += 4; // definition index
        pos += 4; // ObjectImport token
        pos += 4; // import index
        pos += 4; // ObjectBinary token

        int sizeX = (int)ReadI32(data, ref pos);
        int sizeY = (int)ReadI32(data, ref pos);
        int pitch = (int)ReadI32(data, ref pos);   // rows are pitch-padded, not packed
        int bitcount = (int)ReadI32(data, ref pos);
        uint redMask = ReadU32(data, ref pos);
        uint greenMask = ReadU32(data, ref pos);
        uint blueMask = ReadU32(data, ref pos);
        _ = ReadU32(data, ref pos); // alphaMask
        pos += 4; // remaining BinarySurfaceInfo tail: 1-byte colorkey + 3 pad

        if (bitcount != 16)
            throw new InvalidDataException(
                $"only RGB565 (bitcount=16) handled, got bitcount={bitcount}");
        if (sizeX <= 0 || sizeY <= 0)
            throw new InvalidDataException($"invalid texture size {sizeX}x{sizeY}");

        int pixelCount = sizeX * sizeY;
        // Native consumes pitch·sizeY bytes (modeler.cpp:724 MovePointer) — the
        // rows are pitch-padded, not packed, when 2·sizeX isn't 4-aligned
        // (the corpus' odd-width HUD art, e.g. 91×80/pitch 184).
        if (pos + (long)pitch * sizeY > data.Length)
            throw new InvalidDataException("truncated .mdl texture (pixel data)");

        var rgba = new byte[pixelCount * 4];
        int pixelBase = pos;
        for (int srcRow = 0; srcRow < sizeY; srcRow++)       // row 0 = top of the file's pixel rows
        {
            int rowBase = pixelBase + srcRow * pitch;
            for (int x = 0; x < sizeX; x++)
            {
                ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(rowBase + x * 2));
                // Retain the legacy row reversal only when explicitly requested
                // (or by the historical default). Mesh/HUD callers use false.
                int dstRow = flipVertical ? sizeY - 1 - srcRow : srcRow;
                int di = (dstRow * sizeX + x) * 4;
                rgba[di] = (byte)MathF.Round(((v & redMask) >> 11) / 31f * 255f);
                rgba[di + 1] = (byte)MathF.Round(((v & greenMask) >> 5) / 63f * 255f);
                rgba[di + 2] = (byte)MathF.Round((v & blueMask) / 31f * 255f);
                rgba[di + 3] = 255;
            }
        }

        return new MdlTexture(sizeX, sizeY, rgba);
    }

    /// <summary>
    /// Convert a decoded opaque texture into the PREMULTIPLIED, transparent-background
    /// form the HUD renders. The original gauge bitmaps are authored with a black
    /// surround (there is no alpha channel); the classic HUD sits over a dark
    /// backdrop so that black reads as invisible. Drawing that opaque black box over
    /// this port's 3-D scene would smear the view, so the near-black surround is
    /// keyed to alpha 0 (pixels whose luminance is ≤ <paramref name="keyBelow"/>),
    /// and surviving pixels are premultiplied for <c>BlendState.AlphaBlend</c>.
    /// Pure and testable; returns RGBA bytes (r,g,b,a per pixel).
    /// </summary>
    public static byte[] ToPremultipliedRgba(MdlTexture tex, int keyBelow = 40)
    {
        var src = tex.Rgba;
        var outBuf = new byte[tex.ByteCount];
        for (int i = 0; i < src.Length; i += 4)
        {
            byte r = src[i], g = src[i + 1], b = src[i + 2];
            int lum = (r * 299 + g * 587 + b * 114) / 1000; // Rec.601 luminance
            if (lum <= keyBelow)
            {
                outBuf[i] = outBuf[i + 1] = outBuf[i + 2] = outBuf[i + 3] = 0;
            }
            else
            {
                outBuf[i] = r;
                outBuf[i + 1] = g;
                outBuf[i + 2] = b;
                outBuf[i + 3] = 255;
            }
        }
        return outBuf;
    }

    private static uint ReadU32(byte[] d, ref int pos)
    {
        Ensure(d, pos, 4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos));
        pos += 4;
        return v;
    }

    private static int ReadI32(byte[] d, ref int pos)
    {
        Ensure(d, pos, 4);
        int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(pos));
        pos += 4;
        return v;
    }

    private static ushort ReadU16(byte[] d, ref int pos)
    {
        Ensure(d, pos, 2);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos));
        pos += 2;
        return v;
    }

    private static void SkipCString(byte[] d, ref int pos)
    {
        int start = pos;
        int end = Array.IndexOf(d, (byte)0, start);
        if (end < 0)
            throw new InvalidDataException($"unterminated cstring in .mdl at 0x{start:x}");
        int length = end - start + 1;     // strlen + NUL
        pos = start + ((length + 3) & ~3); // padded to 4
    }

    private static void Ensure(byte[] d, int pos, int count)
    {
        if (pos < 0 || pos + count > d.Length)
            throw new InvalidDataException("truncated .mdl texture");
    }
}
