using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Unit tests for the <c>.mdl</c> TEXTURE decoder (the port's mesh-half
/// <c>MdlFile</c> was already tested; this is the missing texture/BinarySurfaceInfo
/// half) and the gauge-kind mapping. All fixtures are built synthetically so no
/// artwork tree or graphics device is required; one test additionally validates
/// against the REAL ship gauges when the artwork is present on this host.
/// </summary>
public class MdlTextureDecoderTests
{
    [Fact]
    public void Decode_reads_rgb565_rows_and_flips_vertically()
    {
        // A minimal, hand-built .mdl whose BinarySurfaceInfo wraps a 2x2 RGB565
        // surface. File pixel order (row 0 first): red, green / blue, white.
        var blob = BuildFixture();

        var tex = MdlTextureDecoder.Decode(blob);

        Assert.Equal(2, tex.Width);
        Assert.Equal(2, tex.Height);

        // After the reference's vertical flip, the file's TOP row (srcRow 0)
        // becomes the decoded BOTTOM row: so top row = file row 1 (blue, white),
        // bottom row = file row 0 (red, green).
        AssertColor(tex, 0, 0, 0, 0, 255);      // top-left  = file (0,1) blue
        AssertColor(tex, 1, 0, 255, 255, 255);  // top-right = file (1,1) white
        AssertColor(tex, 0, 1, 255, 0, 0);      // bottom-left = file (0,0) red
        AssertColor(tex, 1, 1, 0, 255, 0);      // bottom-right = file (1,0) green
    }

    [Fact]
    public void Decode_rejects_bad_magic()
    {
        var blob = BuildFixture();
        blob[0] = 0xAA; // corrupt the magic
        Assert.Throws<InvalidDataException>(() => MdlTextureDecoder.Decode(blob));
    }

    [Fact]
    public void ToPremultipliedRgba_keys_near_black_surround_to_transparent()
    {
        // 2x1: white pixel and pure-black pixel.
        var tex = new MdlTexture(2, 1, new byte[]
        {
            255, 255, 255, 255,   // white, opaque
            0, 0, 0, 255,          // black, opaque in the raw decode
        });

        var rgba = MdlTextureDecoder.ToPremultipliedRgba(tex);

        // White pixel survives (alpha 255).
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, rgba[0..4]);
        // Near-black surround is keyed to fully transparent (premultiplied 0,0,0,0).
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, rgba[4..8]);
    }

    [Fact]
    public void Kind_stems_and_labels_map_the_six_core_gauges()
    {
        Assert.Equal("HullGaugebmp", GaugeTextureSource.MdlStemFor(GaugeKind.Hull));
        Assert.Equal("ShieldGaugebmp", GaugeTextureSource.MdlStemFor(GaugeKind.Shield));
        Assert.Equal("EnergyGaugebmp", GaugeTextureSource.MdlStemFor(GaugeKind.Energy));
        Assert.Equal("FuelGaugebmp", GaugeTextureSource.MdlStemFor(GaugeKind.Fuel));
        Assert.Equal("AmmoGaugebmp", GaugeTextureSource.MdlStemFor(GaugeKind.Ammo));
        Assert.Equal("ThrustGaugebmp", GaugeTextureSource.MdlStemFor(GaugeKind.Thrust));

        foreach (GaugeKind kind in Enum.GetValues<GaugeKind>())
            Assert.False(string.IsNullOrWhiteSpace(GaugeTextureSource.LabelFor(kind)));
    }

    [Fact]
    public void Decodes_the_real_hull_gauge_when_artwork_is_present()
    {
        string path = GaugeTextureSource.PathFor(GaugeKind.Hull);
        if (!File.Exists(path))
            return; // artwork not present on this host — nothing to assert

        var tex = MdlTextureDecoder.Decode(File.ReadAllBytes(path));

        Assert.Equal(128, tex.Width);
        Assert.Equal(128, tex.Height);
        // The gauge must have actual lit (non-keyed) content, else it's blank.
        var rgba = MdlTextureDecoder.ToPremultipliedRgba(tex);
        Assert.Contains(rgba.Chunk(4), px => px[3] == 255);
    }

    // ---- fixture helpers ---------------------------------------------------

    private static void AssertColor(MdlTexture tex, int x, int y, byte r, byte g, byte b)
    {
        int i = (y * tex.Width + x) * 4;
        Assert.Equal(new byte[] { r, g, b, 255 }, tex.Rgba[i..(i + 4)]);
    }

    private static byte[] BuildFixture()
    {
        var b = new List<byte>();
        W32(b, 0x0DEBADF00u);  // magic
        W32(b, 0x00010000u);   // version
        W32(b, 0);             // countNamespaces
        W32(b, 0);             // countImports
        W32(b, 0);             // countExports
        W32(b, 0);             // countDefinitions
        W32(b, 0);             // definition index
        W32(b, 9);             // ObjectImport token
        W32(b, 0);             // import index
        W32(b, 7);             // ObjectBinary token
        W32(b, 2);             // sizeX
        W32(b, 2);             // sizeY
        W32(b, 4);             // pitch
        W32(b, 16);            // bitcount (RGB565)
        W32(b, 0x0000F800u);   // redMask
        W32(b, 0x000007E0u);   // greenMask
        W32(b, 0x0000001Fu);   // blueMask
        W32(b, 0);             // alphaMask
        b.AddRange(new byte[] { 0, 0, 0, 0 }); // 1-byte colorkey + 3 pad
        W16(b, 0xF800); // (0,0) red
        W16(b, 0x07E0); // (1,0) green
        W16(b, 0x001F); // (0,1) blue
        W16(b, 0xFFFF); // (1,1) white
        return b.ToArray();
    }

    private static void W32(List<byte> b, uint v)
    {
        b.Add((byte)v);
        b.Add((byte)(v >> 8));
        b.Add((byte)(v >> 16));
        b.Add((byte)(v >> 24));
    }

    private static void W16(List<byte> b, ushort v)
    {
        b.Add((byte)v);
        b.Add((byte)(v >> 8));
    }
}
