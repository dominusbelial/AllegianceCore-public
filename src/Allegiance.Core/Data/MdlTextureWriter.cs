using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// P824 — RGB565 quantisation for the native <c>.mdl</c> texture writer: the
/// exact inverse of the decoder's expansion (<see cref="MdlTextureDecoder"/>,
/// <c>round(code / max × 255)</c>, native <c>engine/modeler.cpp:715</c>
/// <c>ImportImageFactory::Read</c> → RGB565 masks 0xF800/0x07E0/0x001F).
/// </summary>
/// <remarks>
/// <b>Rounding rule.</b> A byte component is encoded to the 5-bit (red, blue)
/// or 6-bit (green) code whose <i>decoded</i> value is nearest to it; ties go
/// to the lower code. Because every decoded value maps back to itself under
/// "nearest decoded value", encode(decode(code)) == code for every code and
/// decode(encode(byte)) == byte for every byte the decoder can produce — the
/// property the native-file byte round trip relies on. (Plain
/// <c>round(c·max/255)</c> is NOT invertible: e.g. 4 would round to code 0,
/// which decodes back to 0.)
/// </remarks>
public static class Rgb565
{
    private static readonly byte[] CodeFor5 = BuildCodeLut(31);
    private static readonly byte[] CodeFor6 = BuildCodeLut(63);

    /// <summary>Decoded 8-bit value of each 5-bit code (decode rule of the port's
    /// <see cref="MdlTextureDecoder"/>: <c>round(code/31·255)</c>).</summary>
    public static byte[] Decoded5() => BuildValueTable(31);

    /// <summary>Decoded 8-bit value of each 6-bit green code.</summary>
    public static byte[] Decoded6() => BuildValueTable(63);

    /// <summary>Nearest 5-bit code for an 8-bit red/blue component (ties → lower code).</summary>
    public static byte Encode5(byte component) => CodeFor5[component];

    /// <summary>Nearest 6-bit code for an 8-bit green component (ties → lower code).</summary>
    public static byte Encode6(byte component) => CodeFor6[component];

    /// <summary>Pack one RGB pixel into a little-endian RGB565 word. The alpha
    /// byte is ignored — RGB565 has no alpha channel (the native files' alpha
    /// mask is 0 and the decoder always expands to opaque).</summary>
    public static ushort Pack(byte r, byte g, byte b) =>
        (ushort)((CodeFor5[r] << 11) | (CodeFor6[g] << 5) | CodeFor5[b]);

    private static byte[] BuildValueTable(int max) =>
        Enumerable.Range(0, max + 1).Select(v => (byte)MathF.Round(v / (float)max * 255f)).ToArray();

    private static byte[] BuildCodeLut(int max)
    {
        var values = BuildValueTable(max);
        var lut = new byte[256];
        for (int c = 0; c <= 255; c++)
        {
            int best = 0, bestDist = int.MaxValue;
            for (int v = 0; v <= max; v++)
            {
                int d = Math.Abs(values[v] - c);
                if (d < bestDist) { bestDist = d; best = v; }   // strict < → ties keep the lower code
            }
            lut[c] = (byte)best;
        }
        return lut;
    }
}

/// <summary>Options for <see cref="MdlTextureWriter.Write"/>.</summary>
/// <param name="FlipVertical">
/// <c>true</c> (default) = the input RGBA is in GPU order (top-left origin —
/// what a decoded <see cref="MdlTexture"/> under the decoder's 3-D convention
/// holds, and what a glTF/PNG image holds), so the writer stores it
/// bottom-row-first to match the native files' top-row-first picture order;
/// the decoder's <c>flipVertical:true</c> then reconstructs the input exactly.
/// <c>false</c> = flat 2-D HUD art: input row 0 is stored as file row 0 (the
/// decoder's <c>flipVertical:false</c> convention).
/// </param>
/// <param name="OrderedDither">Apply a 4×4 Bayer ordered dither before
/// quantising (one quantum of spread). Default off = plain nearest-code
/// rounding. Purely a visual option for new art; never used on round trips.</param>
/// <param name="Resize">Normalize the dimensions to the rule the native model
/// textures follow (power-of-two, 16–2048 per side) with the box filter of
/// <see cref="MdlTextureWriter.ResampleRgba"/>. Default on. When off, an
/// out-of-rule size throws.</param>
public sealed record MdlTextureWriteOptions(
    bool FlipVertical = true,
    bool OrderedDither = false,
    bool Resize = true);

/// <summary>
/// P824 — writes a compiled native texture unit: a <c>&lt;stem&gt;.mdl</c> file
/// holding a <c>BinarySurfaceInfo</c> + RGB565 pixel rows in native's exact
/// binary layout — the missing writer half of
/// <see cref="MdlTextureDecoder"/>.
/// </summary>
/// <remarks>
/// <para><b>Native layout</b> (verified byte-for-byte against the artwork
/// corpus, e.g. <c>fig30bmp.mdl</c>; native citations: the token stream is
/// <c>engine/mdlbin.cpp</c> <c>MDLBinaryNameSpace::ReadFile</c> /
/// <c>ReadObject</c>, the image object is <c>engine/modeler.cpp:715</c>
/// <c>ImportImageFactory::Read</c>, the structure copy is
/// <c>engine/mdl.h:37-50</c> <c>IBinaryReaderSite::GetStructure</c> — a raw
/// <c>sizeof(BinarySurfaceInfo)</c> copy, padding bytes included, of
/// <c>engine/enginep.h:313-322</c> — 8 dwords + the 1-byte colour key + 3 pad
/// bytes = 36 bytes on x86):</para>
/// <list type="bullet">
/// <item>magic 0x0DEBADF00, version 0x00010000, 1 namespace (<c>model</c>),
/// 1 import (namespace 0, <c>ImportImage</c> — modeler.cpp:707 registers it in
/// the <c>model</c> namespace), 1 export named <paramref name="stem"/>,
/// 0 definitions.</item>
/// <item>one object (index 0): <c>ObjectImport(0)</c>, <c>ObjectBinary</c>,
/// then the 36-byte stored <c>BinarySurfaceInfo</c>: sizeX, sizeY,
/// pitch = 4-byte-aligned sizeX·2 (the corpus' rows are 4-aligned), bitCount 16,
/// masks 0xF800/0x07E0/0x001F, alphaMask 0, the 1-byte colour key (0 — none of
/// the corpus' model textures is keyed) and 3 trailing bytes that native's
/// compiler (<c>mdlc</c>) leaves UNINITIALIZED (debug-build stack garbage,
/// mostly 0xCC). The reader copies and ignores those 3 bytes, so this writer
/// zeroes them — a native round trip therefore differs in exactly those 3
/// bytes per file (explained, decoded pixels identical).</item>
/// <item>pixel rows, TOP ROW FIRST, each row sizeX·2 bytes of little-endian
/// RGB565 + zero row padding up to pitch. The corpus' files hold exactly one
/// surface — no second surface / mip level behind the rows (file length =
/// header + pitch·sizeY + the 4-byte end token), so none are written.</item>
/// <item>a trailing 4-byte <c>ObjectEnd</c> token (0x00000000) — every corpus
/// texture ends with it; native's <c>ReadObject</c> loop consumes it to close
/// the single object.</item>
/// </list>
/// <para><b>Size rule</b> (corpus survey: every texture referenced by a model
/// .mdl — 776 files — is RGB565 with these masks; 767 of 776 are power-of-two
/// per side, min 16×16, max 2048×2048; the 9 outliers are backdrops/HUD-ish
/// art, not model textures): dimensions are normalized to power-of-two per
/// side, 16 ≤ side ≤ 2048 — rounded UP (never lose authored detail) except the
/// 2048 cap which rounds down — using the box filter
/// (<see cref="ResampleRgba"/>).</para>
/// </remarks>
public static class MdlTextureWriter
{
    internal const uint MdlMagic = 0x0DEBADF00u;    // == MdlTextureDecoder.MdlMagic
    internal const uint MdlVersion = 0x00010000u;

    private const uint ObjectImport = 9;            // engine/mdlbin.cpp Object* enum
    private const uint ObjectBinary = 7;

    /// <summary>RGB565 masks every corpus texture uses (native
    /// <c>engine/modeler.cpp:717-721</c> passes them to
    /// <c>GetPixelFormat</c>).</summary>
    public const uint RedMask = 0x0000F800u, GreenMask = 0x000007E0u, BlueMask = 0x0000001Fu;

    /// <summary>Smallest side observed on a model texture in the corpus
    /// (documentation only — the writer does not upscale below it).</summary>
    public const int MinDimension = 16;

    /// <summary>Largest side observed on a model texture in the corpus
    /// (ss27bmp et al. are 2048²).</summary>
    public const int MaxDimension = 2048;

    /// <summary>The size rule: each side power-of-two, capped at
    /// <see cref="MaxDimension"/> (rounding up, except the cap which rounds
    /// down). The corpus' model textures span 16–2048 per side
    /// (<see cref="MinDimension"/>–<see cref="MaxDimension"/>); smaller
    /// power-of-two sizes are format-legal and are left alone rather than
    /// inventing pixels.</summary>
    public static (int Width, int Height) NormalizeSize(int width, int height)
    {
        static int Norm(int n)
        {
            if (n <= 0) return MinDimension;
            if (n >= MaxDimension) return MaxDimension;
            if ((n & (n - 1)) == 0) return n;
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }
        return (Norm(width), Norm(height));
    }

    /// <summary>Area-average (box) resample: each destination pixel averages the
    /// source rectangle that maps onto it (exact coverage, no kernel), so
    /// downscaling is anti-aliased and a uniform image resamples to itself
    /// exactly. Used by <see cref="MdlTextureWriter.Write"/> when the size rule
    /// requires a resize; upscaling with the same filter stays blocky, which is
    /// the honest result for authored art.</summary>
    public static byte[] ResampleRgba(byte[] rgba, int srcW, int srcH, int dstW, int dstH)
    {
        if (rgba.Length != srcW * srcH * 4)
            throw new ArgumentException($"rgba length {rgba.Length} != {srcW}×{srcH}×4");
        var dst = new byte[dstW * dstH * 4];
        for (int dy = 0; dy < dstH; dy++)
        {
            int y0 = dy * srcH / dstH;
            int y1 = Math.Max(y0 + 1, (dy + 1) * srcH / dstH);
            for (int dx = 0; dx < dstW; dx++)
            {
                int x0 = dx * srcW / dstW;
                int x1 = Math.Max(x0 + 1, (dx + 1) * srcW / dstW);
                long r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int si = (y * srcW + x) * 4;
                        r += rgba[si]; g += rgba[si + 1]; b += rgba[si + 2]; a += rgba[si + 3];
                        n++;
                    }
                int di = (dy * dstW + dx) * 4;
                dst[di] = (byte)MathF.Round(r / (float)n);
                dst[di + 1] = (byte)MathF.Round(g / (float)n);
                dst[di + 2] = (byte)MathF.Round(b / (float)n);
                dst[di + 3] = (byte)MathF.Round(a / (float)n);
            }
        }
        return dst;
    }

    /// <summary>Write a compiled native texture unit. See the class doc for the
    /// byte layout and the meaning of the options.</summary>
    /// <param name="stem">Texture stem — the file's export/namespace name the
    /// hull's <c>TextureGeo</c> imports (e.g. <c>fig30bmp</c>); ASCII, no dots.</param>
    /// <param name="width">Input width in pixels.</param>
    /// <param name="height">Input height in pixels.</param>
    /// <param name="rgbaTopLeftOrigin">
    /// width×height×4 RGBA bytes, top-left origin (see
    /// <see cref="MdlTextureWriteOptions.FlipVertical"/>). The alpha byte is
    /// ignored (RGB565 has no alpha).</param>
    public static byte[] Write(string stem, int width, int height, byte[] rgbaTopLeftOrigin,
        MdlTextureWriteOptions? options = null)
    {
        var o = options ?? new MdlTextureWriteOptions();
        ValidateStem(stem);
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"texture size {width}×{height} must be positive");
        if (rgbaTopLeftOrigin.Length != width * height * 4)
            throw new InvalidDataException($"rgba length {rgbaTopLeftOrigin.Length} != {width}×{height}×4");

        int tw = width, th = height;
        var rgba = rgbaTopLeftOrigin;
        var (nw, nh) = NormalizeSize(width, height);
        if ((nw, nh) != (tw, th))
        {
            if (!o.Resize)
                throw new InvalidDataException(
                    $"texture size {width}×{height} violates the native size rule (power-of-two, {MinDimension}–{MaxDimension} per side); " +
                    "enable Resize or normalize the image first");
            rgba = ResampleRgba(rgba, width, height, nw, nh);
            tw = nw; th = nh;
        }

        int pitch = (tw * 2 + 3) & ~3;                  // rows are 4-byte aligned in the corpus
        var w = new MemoryStream();
        var buf = new byte[8];

        void U32(uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf, v);
            w.Write(buf.AsSpan(0, 4));
        }

        void CString(string s)
        {
            var bytes = new byte[(s.Length + 1 + 3) & ~3];  // strlen + NUL, padded to 4 (mdlbin.cpp:67-73 ReadString)
            Encoding.ASCII.GetBytes(s, bytes);
            w.Write(bytes);
        }

        // -- header + tables (engine/mdlbin.cpp ReadFile) --
        U32(MdlMagic);
        U32(MdlVersion);
        U32(1); U32(1); U32(1); U32(0);                 // namespaces, imports, exports, definitions
        CString("model");                               // ImportImage lives in the "model" namespace
        U32(0); CString("ImportImage");
        CString(stem);
        // -- the single object: ObjectImport(ImportImage) + ObjectBinary(structure + rows) --
        U32(0);                                         // object index 0
        U32(ObjectImport); U32(0);                      // import table index 0
        U32(ObjectBinary);
        // BinarySurfaceInfo (enginep.h:313-322), raw x86 layout, sizeof = 40:
        U32((uint)tw); U32((uint)th); U32((uint)pitch); U32(16);
        U32(RedMask); U32(GreenMask); U32(BlueMask); U32(0);
        w.WriteByte(0);                                 // m_bColorKey = false
        w.Write(new byte[3]);                           // trailing padding — uninitialized garbage in native files, ignored
        // -- rows, top row first --
        var row = new byte[pitch];
        var bayer = new uint[] { 0u, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };
        for (int fileRow = 0; fileRow < th; fileRow++)
        {
            int gpuRow = o.FlipVertical ? th - 1 - fileRow : fileRow;
            for (int x = 0; x < tw; x++)
            {
                int si = (gpuRow * tw + x) * 4;
                byte r = rgba[si], g = rgba[si + 1], b = rgba[si + 2];
                if (o.OrderedDither)
                {
                    // One quantum of spread ((max+1) codes across 255) phased
                    // over the 4×4 Bayer matrix, then the usual nearest-code rule.
                    float t = (bayer[(gpuRow % 4) * 4 + (x % 4)] + 0.5f) / 16f - 0.5f;
                    r = ClampByte(r + t * (255f / 31f));
                    g = ClampByte(g + t * (255f / 63f));
                    b = ClampByte(b + t * (255f / 31f));
                }
                BinaryPrimitives.WriteUInt16LittleEndian(row.AsSpan(x * 2), Rgb565.Pack(r, g, b));
            }
            // x ≥ tw: row padding bytes stay zero
            w.Write(row);
        }
        // Trailing ObjectEnd token (0) — the corpus files all end with it and
        // native's ReadObject loop needs it to close the object.
        U32(0);
        return w.ToArray();

        static byte ClampByte(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)MathF.Round(v);
    }

    /// <summary>The pitch rule: rows are padded to a 4-byte boundary
    /// (every corpus texture follows it; the decoder skips the pad via
    /// <c>pitch</c>).</summary>
    public static int PitchFor(int width) => (width * 2 + 3) & ~3;

    private static void ValidateStem(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
            throw new InvalidDataException("texture stem must be non-empty");
        if (stem.Length > 63)
            throw new InvalidDataException($"texture stem '{stem}' is too long (max 63)");
        foreach (var c in stem)
            if (c is < '0' or > 'z' || (c > 'Z' && c < '_') || (c > '9' && c < 'A') || c == '`')
                // allow 0-9 A-Z _ a-z only (native namespace names)
                throw new InvalidDataException($"texture stem '{stem}' must be ASCII alnum/underscore");
    }
}
