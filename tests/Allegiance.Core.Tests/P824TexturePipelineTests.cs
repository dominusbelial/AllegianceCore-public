using System.Buffers.Binary;
using System.IO;
using System.Text;
using Allegiance.Core;
using Allegiance.TestInfra;
using Allegiance.Tools.MdlToGlb;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P824 — the texture half of the authoring pipeline: the native
/// <c>&lt;stem&gt;.bmp.mdl</c> writer (<see cref="MdlTextureWriter"/> — the
/// inverse of <see cref="MdlTextureDecoder"/>'s decode), RGB565 quantisation,
/// the power-of-two size rule, the PNG codec the glTF base-colour embedding
/// uses, and the GLB texture round trip. Fixtures are synthetic (built in
/// code); the native-artwork byte round trips are corpus-backed and skip on a
/// clean clone.
/// </summary>
public class P824TexturePipelineTests
{
    private const string Stem = "hullbmp";

    // ---- RGB565 quantisation -------------------------------------------------

    [Fact]
    public void Rgb565_quantisation_is_the_exact_inverse_of_the_decoder_expansion()
    {
        // The decoder expands with round(code/max*255); the encoder must map
        // every byte the decoder can produce back to its own code, so a decode →
        // encode round trip is lossless (the native-file byte round trip relies
        // on it).
        foreach (var (table, encode, max) in new (byte[] table, Func<byte, byte> encode, int max)[]
                 {
                     (Rgb565.Decoded5(), Rgb565.Encode5, 31),
                     (Rgb565.Decoded6(), Rgb565.Encode6, 63),
                 })
        {
            Assert.Equal(max + 1, table.Length);
            foreach (var code in table.Select((v, i) => (v, i)))
                Assert.Equal(code.i, encode(code.v));
        }
    }

    [Fact]
    public void Rgb565_decoded_values_match_the_decoder_formula_and_are_tie_broken_downward()
    {
        // round(code/31*255): code 0 → 0, code 31 → 255. Ties (a byte exactly
        // between two decoded values, e.g. 4 between decoded(0)=0 and
        // decoded(1)=8) go to the LOWER code — the documented rounding rule.
        Assert.Equal(0, Rgb565.Decoded5()[0]);
        Assert.Equal(255, Rgb565.Decoded5()[31]);
        Assert.Equal(0, Rgb565.Encode5(4));          // 4 is equidistant from 0 and 8 → 0
        Assert.Equal(1, Rgb565.Encode5(5));          // 5 is closer to 8
        Assert.Equal(31, Rgb565.Encode5(255));
    }

    // ---- writer: byte layout ---------------------------------------------------

    [Fact]
    public void Writer_emits_native_byte_layout_for_a_2x2_texture()
    {
        // GPU order (top-left origin): red, green / blue, white → file rows are
        // stored top-row-first, so the file's FIRST row is the GPU BOTTOM row
        // (blue, white) — the decoder's flipVertical:true convention.
        var rgba = new byte[]
        {
            255, 0, 0, 255, 0, 255, 0, 255,       // GPU row 0: red, green
            0, 0, 255, 255, 255, 255, 255, 255,   // GPU row 1: blue, white
        };
        var bytes = MdlTextureWriter.Write(Stem, 2, 2, rgba);

        var expected = new List<byte>();
        void W32(uint v) => expected.AddRange(BitConverter.GetBytes(v));
        void W16(ushort v) => expected.AddRange(BitConverter.GetBytes(v));
        W32(0x0DEBADF00u);                       // magic (engine/mdlbin.cpp MDLMagic)
        W32(0x00010000u);                        // version
        W32(1); W32(1); W32(1); W32(0);          // namespaces, imports, exports, definitions
        expected.AddRange(new byte[] { 0x6D, 0x6F, 0x64, 0x65, 0x6C, 0, 0, 0 });  // "model" padded to 8
        W32(0);                                  // import namespace index
        expected.AddRange(new byte[] { 0x49, 0x6D, 0x70, 0x6F, 0x72, 0x74, 0x49, 0x6D, 0x61, 0x67, 0x65, 0 });  // "ImportImage" padded to 12
        expected.AddRange(new byte[] { 0x68, 0x75, 0x6C, 0x6C, 0x62, 0x6D, 0x70, 0 });  // "hullbmp" + NUL, padded to 8
        W32(0);                                  // object index 0
        W32(9); W32(0);                          // ObjectImport, import index 0
        W32(7);                                  // ObjectBinary
        W32(2); W32(2); W32(4); W32(16);         // sizeX, sizeY, pitch, bitCount
        W32(0xF800); W32(0x07E0); W32(0x001F); W32(0);   // RGB565 masks, alpha 0
        expected.AddRange(new byte[] { 0, 0, 0, 0 });    // colour key 0 + 3 pad bytes
        // file row 0 = GPU bottom row (blue, white), row 1 = GPU top row (red, green)
        W16(0x001F); W16(0xFFFF);
        W16(0xF800); W16(0x07E0);
        W32(0);                                  // trailing ObjectEnd token

        Assert.Equal(expected, bytes);

        // And the port's decoder reads it back to exactly the input RGBA.
        var decoded = MdlTextureDecoder.Decode(bytes);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(rgba, decoded.Rgba);
    }

    [Fact]
    public void Writer_flipFalse_keeps_gpu_row_order_for_2d_hud_art()
    {
        var rgba = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 };
        var bytes = MdlTextureWriter.Write(Stem, 2, 2, rgba, new MdlTextureWriteOptions(FlipVertical: false));

        var decoded = MdlTextureDecoder.Decode(bytes, flipVertical: false);
        Assert.Equal(rgba, decoded.Rgba);
    }

    [Fact]
    public void Decoder_skips_row_padding_on_native_odd_width_files()
    {
        // The corpus' HUD art has odd widths (e.g. 91×80, pitch 184): rows are
        // padded to a 4-byte boundary. The writer's size rule keeps new art on
        // power-of-two sizes (so Write never emits padding), but the decoder
        // must still skip the padding of native files. Hand-build a 3×2 file
        // with pitch 8 (2 px + 2 pad bytes per row) and check the decode.
        var b = new List<byte>();
        void W32(uint v) => b.AddRange(BitConverter.GetBytes(v));
        void W16(ushort v) => b.AddRange(BitConverter.GetBytes(v));
        W32(0x0DEBADF00u); W32(0x00010000u); W32(0); W32(0); W32(0); W32(0);
        W32(0); W32(9); W32(0); W32(7);          // definition idx + ObjectImport + idx + ObjectBinary
        W32(2); W32(2); W32(8); W32(16);
        W32(0xF800); W32(0x07E0); W32(0x001F); W32(0);
        b.AddRange(new byte[] { 0, 0, 0, 0 });
        W16(0xF800); W16(0x07E0); b.AddRange(new byte[] { 0xAA, 0xAA, 0xAA, 0xAA });   // row 0 + 4 pad bytes (pitch 8)
        W16(0x001F); W16(0xFFFF); b.AddRange(new byte[] { 0xAA, 0xAA, 0xAA, 0xAA });   // row 1 + 4 pad bytes
        W32(0);                                                            // ObjectEnd

        var tex = MdlTextureDecoder.Decode(b.ToArray(), flipVertical: false);
        Assert.Equal(2, tex.Width);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 }, tex.Rgba);
        Assert.Equal(8, MdlTextureWriter.PitchFor(3));
        Assert.Equal(184, MdlTextureWriter.PitchFor(91));   // the corpus' 91×80 art
    }

    [Fact]
    public void Writer_round_trips_every_rgb565_representable_rgba_input()
    {
        // decode(write(rgba)) is exact for every RGB565-representable input
        // (what a decoded native texture holds); arbitrary authored colours
        // quantise to the nearest representable one, pinned by the quantisation
        // tests above.
        const int size = 16;
        var rgba = new byte[size * size * 4];
        var rng = new Random(824);
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = Rgb565.Decoded5()[rng.Next(32)];
            rgba[i + 1] = Rgb565.Decoded6()[rng.Next(64)];
            rgba[i + 2] = Rgb565.Decoded5()[rng.Next(32)];
            rgba[i + 3] = 255;
        }
        foreach (var flip in new[] { true, false })
        {
            var bytes = MdlTextureWriter.Write(Stem, size, size, rgba, new MdlTextureWriteOptions(FlipVertical: flip));
            var decoded = MdlTextureDecoder.Decode(bytes, flipVertical: flip);
            Assert.Equal(size, decoded.Width);
            Assert.Equal(rgba, decoded.Rgba);
        }
    }

    // ---- writer: malformed input ----------------------------------------------

    [Fact]
    public void Writer_rejects_bad_rgba_length()
    {
        Assert.Throws<InvalidDataException>(() => MdlTextureWriter.Write(Stem, 2, 2, new byte[7]));
    }

    [Fact]
    public void Writer_rejects_bad_stems()
    {
        var rgba = new byte[2 * 2 * 4];
        Assert.Throws<InvalidDataException>(() => MdlTextureWriter.Write("", 2, 2, rgba));
        Assert.Throws<InvalidDataException>(() => MdlTextureWriter.Write("hull bmp", 2, 2, rgba));
        Assert.Throws<InvalidDataException>(() => MdlTextureWriter.Write("hull.bmp", 2, 2, rgba));
    }

    [Fact]
    public void Writer_rejects_non_normalizable_sizes_when_resize_is_off()
    {
        var rgba = new byte[20 * 20 * 4];
        Assert.Throws<InvalidDataException>(() =>
            MdlTextureWriter.Write(Stem, 20, 20, rgba, new MdlTextureWriteOptions(Resize: false)));
    }

    // ---- size rule ------------------------------------------------------------

    [Theory]
    [InlineData(16, 16, 16, 16)]
    [InlineData(15, 1, 16, 1)]
    [InlineData(17, 32, 32, 32)]
    [InlineData(100, 100, 128, 128)]
    [InlineData(2048, 2048, 2048, 2048)]
    [InlineData(3000, 1025, 2048, 2048)]
    [InlineData(64, 9, 64, 16)]
    public void NormalizeSize_rounds_up_to_power_of_two_and_caps_at_2048(int w, int h, int ew, int eh)
        => Assert.Equal((ew, eh), MdlTextureWriter.NormalizeSize(w, h));

    [Fact]
    public void Resample_of_a_uniform_image_is_the_same_uniform_image()
    {
        var rgba = new byte[32 * 32 * 4];
        Array.Fill(rgba, (byte)137);
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        var outRgba = MdlTextureWriter.ResampleRgba(rgba, 32, 32, 16, 16);
        Assert.Equal(16 * 16 * 4, outRgba.Length);
        Assert.All(outRgba.Chunk(4), px => Assert.Equal(new byte[] { 137, 137, 137, 255 }, px));
    }

    [Fact]
    public void Writer_resizes_non_power_of_two_input_to_the_rule()
    {
        // 20×20 → 32×32 (rounded up); the written file's structure says 32×32.
        var rgba = new byte[20 * 20 * 4];
        Array.Fill(rgba, (byte)16);        // 16 is an RGB565 fixed point (decoded(2)=decoded(4)=16)
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
        var bytes = MdlTextureWriter.Write(Stem, 20, 20, rgba);

        var decoded = MdlTextureDecoder.Decode(bytes);
        Assert.Equal(32, decoded.Width);
        Assert.Equal(32, decoded.Height);
        // The uniform colour survives the box resample + quantisation.
        Assert.All(decoded.Rgba.Chunk(4), px => Assert.Equal(new byte[] { 16, 16, 16, 255 }, px));
    }

    // ---- PNG codec --------------------------------------------------------------

    [Fact]
    public void Png_codec_round_trips_random_rgba()
    {
        const int w = 13, h = 7;
        var rgba = new byte[w * h * 4];
        var rng = new Random(824);
        rng.NextBytes(rgba);
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;

        var png = PngCodec.Encode(w, h, rgba);
        var decoded = PngCodec.Decode(png);
        Assert.Equal((w, h), (decoded.Width, decoded.Height));
        Assert.Equal(rgba, decoded.Rgba);
    }

    [Fact]
    public void Png_decoder_undoes_all_five_row_filters()
    {
        // Hand-build raw scanlines with each filter type; the decoded image
        // must be the pre-filter pixel data for all of them.
        const int w = 4, h = 5;
        var pixels = new byte[w * h * 3];
        var rng = new Random(1);
        rng.NextBytes(pixels);

        var raw = new List<byte>();
        for (int y = 0; y < h; y++)
        {
            raw.Add((byte)(y % 5));                       // filters 0..4 in turn
            var row = pixels.AsSpan(y * w * 3, w * 3).ToArray();
            var orig = pixels.AsSpan(y * w * 3, w * 3).ToArray();
            switch (y % 5)
            {
                case 0: break;                                            // None
                case 1: for (int i = 3; i < row.Length; i++) row[i] = (byte)(orig[i] - orig[i - 3]); break;  // Sub (bpp=3)
                case 2: if (y > 0) for (int i = 0; i < row.Length; i++) row[i] = (byte)(orig[i] - pixels[(y - 1) * w * 3 + i]); break; // Up
                case 3: for (int i = 0; i < row.Length; i++)
                        {
                            int left = i >= 3 ? orig[i - 3] : 0;
                            int up = y > 0 ? pixels[(y - 1) * w * 3 + i] : 0;
                            row[i] = (byte)(orig[i] - ((left + up) >> 1));
                        }
                        break;
                case 4: for (int i = 0; i < row.Length; i++)
                        {
                            int a = i >= 3 ? orig[i - 3] : 0;
                            int b = y > 0 ? pixels[(y - 1) * w * 3 + i] : 0;
                            int c = i >= 3 && y > 0 ? pixels[(y - 1) * w * 3 + i - 3] : 0;
                            int p = a + b - c;
                            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                            int pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                            row[i] = (byte)(orig[i] - pred);
                        }
                        break;
            }
            raw.AddRange(row);
        }
        var png = BuildPng(w, h, colourType: 2, raw: raw.ToArray());
        var decoded = PngCodec.Decode(png);
        Assert.Equal((w, h), (decoded.Width, decoded.Height));
        Assert.Equal(255, decoded.Rgba[3]);               // RGB expands to opaque
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int si = (y * w + x) * 3, di = (y * w + x) * 4;
                Assert.Equal(pixels[si..(si + 3)], decoded.Rgba[di..(di + 3)]);
            }
    }

    [Fact]
    public void Png_decoder_rejects_malformed_input()
    {
        var good = PngCodec.Encode(2, 2, new byte[2 * 2 * 4]);
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode([0x12, 0x34]));                 // bad signature
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(good[..20]));                   // truncated
        var corrupt = good.ToArray();
        corrupt[corrupt.Length - 8] ^= 0xFF;      // break the last chunk's CRC
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(corrupt));
        var palette = BuildPng(2, 2, colourType: 3, raw: new byte[2 * (1 + 2)]);                  // palette unsupported
        Assert.Throws<InvalidDataException>(() => PngCodec.Decode(palette));
    }

    /// <summary>Hand-assemble a PNG (IHDR + one IDAT + IEND) with an arbitrary
    /// colour type and raw scanline stream, for filter and rejection tests.</summary>
    private static byte[] BuildPng(int w, int h, int colourType, byte[] raw)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = (byte)colourType;
        WriteChunk(ms, "IHDR"u8, ihdr);
        var idat = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw);
        WriteChunk(ms, "IDAT"u8, idat.ToArray());
        WriteChunk(ms, "IEND"u8, []);
        return ms.ToArray();
    }

    private static uint Crc(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var span in new[] { a.ToArray(), b.ToArray() })
            foreach (var v in span)
            {
                crc ^= v;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        return crc ^ 0xFFFFFFFFu;
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, byte[] data)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(type, data));
        s.Write(crc);
    }

    // ---- GLB texture embedding (export → re-import → native texture) -----------

    private static GlbExportModel SyntheticModel() => new()
    {
        Name = "testhull",
        Meshes =
        [
            new GlbMesh("testhull_hullbmp_0", "hullbmp",
                [0f, 0f, 1f, 1f, 0f, 0f, 0f, 1f, 0f],
                [0f, 0f, 1f, 0f, 0f, 1f],
                [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
                [0, 1, 2]),
        ],
        Mounts = [],
        Lights = [],
        LodPixelRadii = [],
        TextureStems = ["hullbmp"],
        Collision = null,
    };

    [Fact]
    public void Glb_export_embeds_the_texture_and_reimport_recovers_it()
    {
        const int size = 16;
        var rgba = TestTextureGenerator.BuildRgba(size);
        var model = SyntheticModel() with
        {
            Images = [new GlbImage("hullbmp", size, size, PngCodec.Encode(size, size, rgba))],
        };

        using var glbStream = new MemoryStream();
        GlbWriter.Write(model, glbStream);
        glbStream.Position = 0;
        var reimported = GlbReader.Read(glbStream, null);

        // Stem comes from the material (the exporter names it after the stem).
        var image = Assert.Single(reimported.Images!);
        Assert.Equal("hullbmp", image.Stem);
        Assert.Equal(size, image.Width);
        var decodedPng = PngCodec.Decode(image.Png);
        Assert.Equal(rgba, decodedPng.Rgba);

        // PNG → native texture unit → decode: pixel-exact, top-left origin
        // preserved through the writer's 3-D flip convention.
        var texBytes = MdlTextureWriter.Write(image.Stem, image.Width, image.Height, decodedPng.Rgba);
        var tex = MdlTextureDecoder.Decode(texBytes);
        Assert.Equal(rgba, tex.Rgba);
    }

    [Fact]
    public void Glb_reader_derives_the_texture_stem_from_the_material_name()
    {
        // Mesh named WITHOUT the "<stem>_<texstem>_<i>" convention; the
        // material name carries the stem (what Blender-authored GLBs look like).
        const int size = 16;
        var model = SyntheticModel() with
        {
            Meshes = [new GlbMesh("Hull", "hullbmp", [0f, 0f, 1f, 1f, 0f, 0f, 0f, 1f, 0f],
                [0f, 0f, 1f, 0f, 0f, 1f], [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f], [0, 1, 2])],
            Images = [new GlbImage("hullbmp", size, size, PngCodec.Encode(size, size, TestTextureGenerator.BuildRgba(size)))],
        };
        using var glbStream = new MemoryStream();
        GlbWriter.Write(model, glbStream);
        glbStream.Position = 0;
        var reimported = GlbReader.Read(glbStream, null);
        Assert.Equal("hullbmp", Assert.Single(reimported.Meshes).TextureStem);
    }

    [Fact]
    public void Import_rejects_a_textured_mesh_without_a_base_colour_image()
    {
        var model = SyntheticModel();        // textured mesh, Images = null
        Assert.Throws<InvalidDataException>(() => GlbImporter.ValidateTextureImages(model));

        // With the image present the check passes.
        GlbImporter.ValidateTextureImages(model with
        {
            Images = [new GlbImage("hullbmp", 16, 16, PngCodec.Encode(16, 16, TestTextureGenerator.BuildRgba(16)))],
        });
    }

    // ---- new-art UV proof (built-in test texture) ------------------------------

    [Fact]
    public void Proof_texture_markers_land_where_the_test_hull_uvs_say()
    {
        // P823's hand-built test hull (GlbToMdlTests.NewArtModel's octahedron):
        // nose UV (0,0), +x UV (1,0), +y UV (0,1), tail UV (1,1) — all four GPU
        // corners. Generate the proof texture, write it as a native texture
        // unit, decode it through the port's real decoder (the renderer's
        // flipVertical:true path) and sample nearest at each vertex UV: the
        // marker must be exactly where the UVs point.
        const int size = 128;
        var rgba = TestTextureGenerator.BuildRgba(size);
        var bytes = MdlTextureWriter.Write(Stem, size, size, rgba);
        var tex = MdlTextureDecoder.Decode(bytes);   // flipVertical:true = what NativeShipRenderer uploads

        static (int x, int y) Texel(float u, float v, int size) =>
            (Math.Clamp((int)MathF.Round(u * size), 0, size - 1),
             Math.Clamp((int)MathF.Round(v * size), 0, size - 1));
        static byte[] At(byte[] rgba, int size, float u, float v)
        {
            var (x, y) = Texel(u, v, size);
            int o = (y * size + x) * 4;
            return rgba[o..(o + 4)];
        }

        var (mr, mg, mb) = TestTextureGenerator.TopLeftMarker;
        Assert.Equal(new byte[] { mr, mg, mb, 255 }, At(tex.Rgba, size, 0f, 0f));      // nose → TL magenta
        (mr, mg, mb) = TestTextureGenerator.TopRightMarker;
        Assert.Equal(new byte[] { mr, mg, mb, 255 }, At(tex.Rgba, size, 1f, 0f));      // +x → TR yellow
        (mr, mg, mb) = TestTextureGenerator.BottomLeftMarker;
        Assert.Equal(new byte[] { mr, mg, mb, 255 }, At(tex.Rgba, size, 0f, 1f));      // +y → BL cyan
        (mr, mg, mb) = TestTextureGenerator.BottomRightMarker;
        Assert.Equal(new byte[] { mr, mg, mb, 255 }, At(tex.Rgba, size, 1f, 1f));      // tail → BR green

        // The checker between the markers stays within one quantisation
        // quantum of the authored greys (96/160 are not RGB565 fixed points —
        // only the corner markers 0/255 are; the quantisation test pins the
        // exact nearest-code rule, here we pin the visual fidelity bound).
        var mid = At(tex.Rgba, size, 0.5f, 0.5f);
        Assert.True(Math.Abs(mid[0] - 96) <= 9 && Math.Abs(mid[1] - 96) <= 9 && Math.Abs(mid[2] - 96) <= 9,
            $"checker centre decoded to ({mid[0]},{mid[1]},{mid[2]})");
    }

    // ---- native-artwork byte round trips (corpus-backed) -----------------------

    public static TheoryData<string> RoundTripStems => new()
    {
        "fig30bmp", "bom04bmp", "ss27bmp", "ss27insidebmp", "cap01bmp",
        "fig07bmp", "acs01bmp", "artifactbmp", "bom30bmp", "fig405bmp",
        "fig403bmp", "belt_omnibmp",
    };

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [MemberData(nameof(RoundTripStems))]
    public void Native_texture_files_survive_decode_write_byte_identically(string stem)
    {
        Require.Artwork();
        var artRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var path = Path.Combine(artRoot, stem + ".mdl");
        Skip.IfNot(File.Exists(path), $"artwork has no {stem}.mdl");

        var original = File.ReadAllBytes(path);
        var tex = MdlTextureDecoder.Decode(original);            // 3-D convention (GPU order)
        var written = MdlTextureWriter.Write(stem, tex.Width, tex.Height, tex.Rgba);

        // Byte-identical, except at most the 3 uninitialised BinarySurfaceInfo
        // padding bytes mdlc leaves in the file (the writer zeroes them; the
        // reader ignores them via the raw sizeof copy — engine/mdl.h:37-50).
        // The colour-key byte is 0 in every corpus texture and every corpus
        // texture ends with the same 4-byte ObjectEnd token the writer emits,
        // so nothing else may differ.
        int pitch = MdlTextureWriter.PitchFor(tex.Width);
        int structEnd = original.Length - pitch * tex.Height - 4;   // − trailing ObjectEnd
        int padStart = structEnd - 3;
        int diffCount = 0;
        var diffs = new List<int>();
        for (int i = 0; i < Math.Min(original.Length, written.Length); i++)
            if (original[i] != written[i])
            {
                diffCount++;
                diffs.Add(i);
            }
        Assert.True(original.Length == written.Length,
            $"{stem}: length {original.Length} vs written {written.Length}");
        Assert.True(diffCount <= 3 && diffs.All(i => i >= padStart && i < structEnd),
            $"{stem}: {diffCount} differing bytes outside the BinarySurfaceInfo pad [{padStart},{structEnd}): " +
            string.Join(",", diffs.Take(8).Select(i => $"0x{i:X}")));
        if (diffCount > 0)
            Console.WriteLine($"{stem}: {diffCount} explained pad bytes differ (uninitialised garbage in the native file)");

        // Decoded pixels identical (belt and braces — implied by the byte test).
        var decodedOriginal = MdlTextureDecoder.Decode(original);
        var decodedWritten = MdlTextureDecoder.Decode(written);
        Assert.Equal(decodedOriginal.Rgba, decodedWritten.Rgba);
    }
}
