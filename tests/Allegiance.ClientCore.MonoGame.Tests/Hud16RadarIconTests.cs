using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.TestInfra;
using Xunit;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-16 — the native per-contact radar icons:
/// <list type="bullet">
/// <item>every contact's icon is its model's IGC <c>iconName</c> + <c>"bmp"</c>
/// (<c>WinTrek/trekigc.cpp:2731-2744</c>), from the hull type
/// (<c>hullTypeIGC.cpp:51</c>), station type (<c>stationIGC.cpp:240-244</c>),
/// <c>AsteroidDef.iconName</c> (<c>asteroidigc.cpp:35</c>),
/// <c>warpDef.iconName</c> (<c>warpIGC.cpp:34</c>), probe
/// (<c>probeigc.cpp:114</c>), mine (<c>mineigc.cpp:103</c>) and missile
/// (<c>missileigc.cpp:50,60</c>) type;</item>
/// <item>an empty name means NO icon (<c>trekigc.cpp:2740-2742</c>);</item>
/// <item>treasure/tech uses the treasure's own icon with the
/// <c>icontechbmp</c> override (<c>radarimage.cpp:224,719,890</c>);</item>
/// <item>placement is <c>directionCenter * min(distanceToEdge, radiusBracket)</c>
/// for the on-screen-large state and 0 otherwise
/// (<c>radarimage.cpp:382-409</c>), drawn additively in <c>colorIcon</c>
/// (<c>radarimage.cpp:417-420,678-685</c>).</item>
/// </list>
/// The rules are pure; the wiring guards at the end pin that the production
/// renderers/gather actually use them.
/// </summary>
public class Hud16RadarIconTests
{
    private readonly ITestOutputHelper _output;
    public Hud16RadarIconTests(ITestOutputHelper output) => _output = output;

    // ── icon name resolution (trekigc.cpp:2731-2744) ────────────────────────

    [Fact]
    public void Icon_name_becomes_the_bmp_surface_stem_and_an_empty_name_means_no_icon()
    {
        // native: LoadSurface(ZString(szName) + "bmp", true)
        Assert.Equal("dn_hifigbmp", RadarIconNameCache.SurfaceStem("dn_hifig"));
        Assert.Equal("meteoriconbmp", RadarIconNameCache.SurfaceStem("meteoricon"));
        // native: if (szName && szName[0] != '\0') … else NULL
        Assert.Null(RadarIconNameCache.SurfaceStem(""));
        Assert.Null(RadarIconNameCache.SurfaceStem(null));
    }

    [Fact]
    public void Ship_icon_comes_from_its_hull_type()
    {
        // hullTypeIGC.cpp:51 — m_poIcon = LoadRadarIcon(m_data->iconName).
        var cache = new RadarIconNameCache(Catalog(
            hulls: [Hull(15, "dn_hifig"), Hull(33, "ejectpod"), Hull(44, "")]));
        Assert.Equal("dn_hifigbmp", cache.Ship(15));
        Assert.Equal("ejectpodbmp", cache.Ship(33));
        // An empty hull iconName is no icon (native NULL), not a made-up stem.
        Assert.Null(cache.Ship(44));
        // An unknown hull id is no icon either.
        Assert.Null(cache.Ship(999));
    }

    [Fact]
    public void Station_missile_probe_and_mine_icons_come_from_their_type_tables()
    {
        var catalog = Catalog(
            stations: [Station(3, "iconsy"), Station(500, "station")],
            missiles: [Missile(32, "missle"), Missile(28, "nervegas")],
            probes: [Probe(15, "probe"), Probe(2, "pickupprobe")],
            mines: [Mine(1, "minefield")]);
        var cache = new RadarIconNameCache(catalog);
        Assert.Equal("iconsybmp", cache.Station(3));
        Assert.Equal("stationbmp", cache.Station(500));
        Assert.Equal("misslebmp", cache.Missile(32));
        Assert.Equal("nervegasbmp", cache.Missile(28));
        Assert.Equal("probebmp", cache.Probe(15));
        Assert.Equal("pickupprobebmp", cache.Probe(2));
        Assert.Equal("minefieldbmp", cache.Mine(1));
        Assert.Null(cache.Station(999));
        Assert.Null(cache.Missile(999));
        Assert.Null(cache.Probe(999));
        Assert.Null(cache.Mine(999));
    }

    [Fact]
    public void Asteroid_and_warp_icons_come_from_the_instances_wire_icon_name()
    {
        // asteroidigc.cpp:35 (AsteroidDef.iconName) and warpIGC.cpp:34
        // (warpDef.iconName) — the port's wire carries both names verbatim.
        var cache = new RadarIconNameCache(null);
        Assert.Equal("meteoriconbmp", cache.Asteroid("meteoricon"));
        Assert.Equal("heliumrockbmp", cache.Asteroid("heliumrock"));
        Assert.Equal("alephbmp", cache.Warp("aleph"));
        Assert.Null(cache.Asteroid(""));
        Assert.Null(cache.Asteroid(null));
        Assert.Null(cache.Warp(""));
        Assert.Null(cache.Warp(null));
    }

    [Fact]
    public void Treasure_uses_its_own_icon_and_tech_overrides_with_icontechbmp()
    {
        // treasureIGC.cpp:118-155 fixes cash/powerup/flag/artifact icons; a
        // part treasure uses the buyable's icon name.
        var catalog = Catalog(parts:
        [
            Part(77, "ammo_icon"),
            Part(78, ""),
        ]);
        var cache = new RadarIconNameCache(catalog);
        Assert.Equal("cashiconbmp", cache.Treasure(TreasureCode.Cash, 0, isTech: false));
        Assert.Equal("iconpubmp", cache.Treasure(TreasureCode.Powerup, 0, isTech: false));
        Assert.Equal("flagbmp", cache.Treasure(TreasureCode.Flag, 0, isTech: false));
        Assert.Equal("articonbmp", cache.Treasure(TreasureCode.Flag, Side.ArtifactSentinelFlag, isTech: false));
        Assert.Equal("ammo_iconbmp", cache.Treasure(TreasureCode.Part, 77, isTech: false));
        // An empty part icon name is no icon (native NULL) …
        Assert.Null(cache.Treasure(TreasureCode.Part, 78, isTech: false));
        // … and an unresolvable id falls back to the port's gem, which has no
        // native icon either.
        Assert.Null(cache.Treasure(TreasureCode.Part, 12345, isTech: false));
        // A useful TECH treasure overrides with the shared native surface
        // (radarimage.cpp:224,719,890) — even when the part's own icon exists.
        Assert.Equal(RadarIconNameCache.TechStem, cache.Treasure(TreasureCode.Part, 77, isTech: true));
        Assert.Equal("icontechbmp", cache.Treasure(TreasureCode.Development, 5, isTech: true));
    }

    // ── placement (radarimage.cpp:382-409) ─────────────────────────────────

    [Fact]
    public void On_screen_icon_is_pulled_inward_by_the_clamped_bracket_radius()
    {
        // A blip right of centre: directionCenter points back toward the image
        // centre, so the icon moves LEFT (inward) by min(distanceToEdge, radiusBracket).
        var right = RadarIconPlacement.Offset(onScreenLarge: true, screenX: 1200f, screenY: 540f,
            viewportWidth: 1920f, viewportHeight: 1080f, distanceToEdge: 200f, radiusBracket: 50f);
        Assert.True(right.X < 0f, $"expected an inward (left) offset, got {right}");
        Assert.Equal(50f, right.Length(), 3);   // radiusBracket < distanceToEdge

        // The distance clamp wins when the blip is closer to the edge.
        var near = RadarIconPlacement.Offset(true, 1900f, 540f, 1920f, 1080f, distanceToEdge: 10f, radiusBracket: 50f);
        Assert.Equal(10f, near.Length(), 3);

        // Symmetry: a blip left of centre moves right.
        var left = RadarIconPlacement.Offset(true, 500f, 540f, 1920f, 1080f, 200f, 50f);
        Assert.True(left.X > 0f);
        Assert.Equal(50f, left.Length(), 3);

        // Native image space is y-up (camera.cpp:174), so the reference point
        // positionObject.Y - H/5 sits H/5 ABOVE the centre: screen y = 540 - 216 = 324.
        // A blip above the reference moves DOWN toward it...
        var above = RadarIconPlacement.Offset(true, 960f, 100f, 1920f, 1080f, 200f, 20f);
        Assert.True(above.Y > 0f);
        // ...and a blip between the reference and the centre moves UP toward it
        // (Claude review: the WIP put the reference below centre and moved it down).
        var between = RadarIconPlacement.Offset(true, 960f, 450f, 1920f, 1080f, 200f, 20f);
        Assert.True(between.Y < 0f, $"expected an upward offset, got {between}");
    }

    [Fact]
    public void Round_radar_uses_the_image_centre_as_the_reference()
    {
        // radarimage.cpp:363-365 — no H/5 shift in round-radar mode.
        var between = RadarIconPlacement.Offset(true, 960f, 450f, 1920f, 1080f, 200f, 20f, roundRadar: true);
        Assert.True(between.Y > 0f, $"expected a downward offset toward the centre, got {between}");
    }

    [Fact]
    public void Every_state_other_than_on_screen_large_draws_the_icon_at_the_blip()
    {
        // radarimage.cpp:403-406 — positionIcon = (0,0) unless
        // ucRadarState == c_ucRadarOnScreenLarge. The port's edge-clamped
        // (off-screen) contacts are that branch.
        var off = RadarIconPlacement.Offset(onScreenLarge: false, 1919f, 1079f,
            1920f, 1080f, distanceToEdge: 1f, radiusBracket: 7.35f);
        Assert.Equal(Microsoft.Xna.Framework.Vector2.Zero, off);
    }

    [Fact]
    public void Dead_centre_blip_points_the_icon_straight_down()
    {
        // fLengthSquared < 1 → directionCenter = (0,-1) in y-up image space
        // (radarimage.cpp:369-372), i.e. straight down on screen. Centre the blip
        // on the reference point, H/5 above the image centre: screenY = 540 - 216.
        var offset = RadarIconPlacement.Offset(true, 960f, 540f - 1080f / 5f,
            1920f, 1080f, distanceToEdge: 100f, radiusBracket: 30f);
        Assert.Equal(0f, offset.X, 3);
        Assert.Equal(30f, offset.Y, 3);
    }

    [Fact]
    public void Icon_colour_is_the_side_colour_red_while_flashing_and_white_when_neutral()
    {
        // radarimage.cpp:678-685 + s_colorNeutral = white (radarimage.cpp:1471):
        // color = pside ? (flash ? Red : pside->GetColor()) : s_colorNeutral.
        var side = new Microsoft.Xna.Framework.Color(0.2f, 0.4f, 0.6f);
        Assert.Equal(side, RadarBracketRenderer.NativeIconColour(side, threatFlash: false));
        Assert.Equal(Microsoft.Xna.Framework.Color.Red,
            RadarBracketRenderer.NativeIconColour(side, threatFlash: true));
        Assert.Equal(Microsoft.Xna.Framework.Color.White,
            RadarBracketRenderer.NativeIconColour(null, threatFlash: false));
        // A neutral object never flashes (it has no damage bucket), but the
        // literal native rule keeps the neutral constant.
        Assert.Equal(Microsoft.Xna.Framework.Color.White,
            RadarBracketRenderer.NativeIconColour(null, threatFlash: true));
    }

    // ── per-frame allocation (brief: 0 bytes over N frames) ────────────────

    [Fact]
    public void Per_frame_icon_name_resolution_allocates_nothing()
    {
        var catalog = Catalog(
            hulls: [Hull(15, "dn_hifig")],
            stations: [Station(3, "iconsy")],
            missiles: [Missile(32, "missle")],
            probes: [Probe(15, "probe")],
            mines: [Mine(1, "minefield")],
            parts: [Part(77, "ammo_icon")]);
        var cache = new RadarIconNameCache(catalog);

        void Frame()
        {
            // The exact per-contact resolution the radar gather performs.
            _ = cache.Ship(15);
            _ = cache.Station(3);
            _ = cache.Missile(32);
            _ = cache.Probe(15);
            _ = cache.Mine(1);
            _ = cache.Asteroid("meteoricon");
            _ = cache.Warp("aleph");
            _ = cache.Treasure(TreasureCode.Cash, 0, isTech: false);
            _ = cache.Treasure(TreasureCode.Part, 77, isTech: true);
        }

        for (int warm = 0; warm < 50; warm++) Frame();   // memoises the wire names
        // Best of three windows: a one-off runtime allocation (tiered JIT recompiling the
        // loop under full-suite load, 208 B once seen in the fast tier) must not fail the
        // test, while a real per-frame allocation shows up in every window.
        long allocated = long.MaxValue;
        for (int window = 0; window < 3; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < 1000; frame++) Frame();
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        _output.WriteLine($"HUD16 resolution allocation over 1000 frames: {allocated} B");
        Assert.Equal(0, allocated);
    }

    // ── real artwork (skips on a clean clone without the art tree) ─────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Every_core_contact_icon_resolves_to_a_real_non_blank_surface()
    {
        Require.Artwork();
        string art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(art, "PCore001.igc")));
        var catalog = ShopCatalog.FromCore(core);
        Assert.NotNull(catalog);
        var cache = new RadarIconNameCache(catalog, catalog!.Hulls.ToDictionary(h => h.HullId));

        var stems = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var hull in core.HullTypes.Values) Add(stems, cache.Ship(hull.HullId), $"hull {hull.HullId}");
        foreach (var (id, station) in core.StationTypes) Add(stems, cache.Station(id), $"station {id}");
        foreach (var (id, missile) in core.MissileTypes) Add(stems, cache.Missile(id), $"missile {id}");
        foreach (var (id, probe) in core.ProbeTypes) Add(stems, cache.Probe(id), $"probe {id}");
        foreach (var (id, mine) in core.MineTypes) Add(stems, cache.Mine(id), $"mine {id}");
        for (int i = 0; i < NativeAsteroidTypes.Count; i++)
            Add(stems, cache.Asteroid(NativeAsteroidTypes.GetTypeDefaults(i).IconName),
                $"asteroid row {i}");
        Add(stems, cache.Treasure(TreasureCode.Cash, 0, false), "cash treasure");
        Add(stems, cache.Treasure(TreasureCode.Powerup, 0, false), "powerup treasure");
        Add(stems, cache.Treasure(TreasureCode.Flag, 0, false), "flag treasure");
        Add(stems, cache.Treasure(TreasureCode.Flag, Side.ArtifactSentinelFlag, false),
            "artifact treasure");
        Add(stems, cache.Treasure(TreasureCode.Part, 1, true), "tech treasure override");

        // Map warps (warpDef.iconName) — m2scatter is the shipped mission.
        var map = IgcReader.ParseFile(Path.Combine(art, "m2scatter.igc"));
        foreach (var record in map.Records)
        {
            if (record.Type != IgcObjectType.Warp || record.Size != DataWarpIGC.Size) continue;
            var warp = DataWarpIGC.Decode(map.Body.Span.Slice(record.PayloadOffset, record.Size));
            Add(stems, cache.Warp(warp.IconName), $"warp {warp.WarpId}");
        }

        // The checkout's filesystem is case-sensitive while native's was not
        // ('aleph' lives on disk as 'Alephbmp.mdl'), so index one time.
        var files = Directory.GetFiles(art, "*.mdl")
            .ToDictionary(f => Path.GetFileName(f), f => f, StringComparer.OrdinalIgnoreCase);
        int decoded = 0;
        foreach (var stem in stems)
        {
            Assert.True(files.TryGetValue(stem + ".mdl", out var path),
                $"missing native radar icon art: {stem}.mdl");
            var tex = MdlTextureDecoder.Decode(File.ReadAllBytes(path), flipVertical: false);
            var rgba = MdlTextureDecoder.ToPremultipliedRgba(tex);
            var pixels = new Microsoft.Xna.Framework.Color[tex.Width * tex.Height];
            for (int i = 0; i < pixels.Length; i++)
            {
                int b = i * 4;
                pixels[i] = new Microsoft.Xna.Framework.Color(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
            }
            // HUD-13 found native bracket art shipped blank; the contact icons
            // must not be, or every contact would silently lose its icon.
            Assert.False(RadarIconTextures.IsBlank(pixels), $"{stem}.mdl is fully transparent");
            // Native draws the surface 1:1 (DrawImage3D, radarimage.cpp:419);
            // the core's icons are the 16x16 contact glyphs, with the two
            // 'dericon' drone hulls at 8x8.
            Assert.True(tex.Width is 8 or 16 && tex.Height == tex.Width,
                $"{stem}.mdl is {tex.Width}x{tex.Height}, expected an 8 or 16 px square");
            _output.WriteLine($"  {stem,-24} {tex.Width}x{tex.Height}");
            decoded++;
        }
        _output.WriteLine($"HUD16 native radar icons decoded: {decoded} stems ({string.Join(", ", stems)})");
        Assert.True(decoded > 100);
    }

    private void Add(SortedSet<string> stems, string? stem, string what)
    {
        if (stem is null) return;   // native NULL icon → no surface, not a failure
        if (stems.Add(stem)) _output.WriteLine($"  {stem,-24} ({what})");
    }

    // ── wiring guards (no graphics device in unit tests) ───────────────────

    private static string Src(string relative)
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        return File.ReadAllText(Path.Combine(root, "src", "Allegiance.ClientCore.Game", relative));
    }

    [Fact]
    public void The_gather_resolves_a_native_icon_stem_for_every_contact_kind()
    {
        var gather = Src(Path.Combine("Game1.Rendering.cs"));
        Assert.Contains("var iconNames = _radarIconNames;", gather);
        // Own ship + every other ship use the HULL type icon.
        Assert.Contains("IconStem: iconNames?.Ship(ownEnt.HullId)", gather);
        Assert.Contains("IconStem: iconNames?.Ship(ship.HullId)", gather);
        Assert.Contains("IconStem: iconNames?.Station(st.StationTypeId)", gather);
        Assert.Contains("IconStem: iconNames?.Warp(wp.IconName)", gather);
        Assert.Contains("IconStem: iconNames?.Missile(ms.TypeId)", gather);
        Assert.Contains("IconStem: iconNames?.Probe(pb.ProbetypeId)", gather);
        Assert.Contains("IconStem: iconNames?.Mine(mine.MinetypeId)", gather);
        Assert.Contains("IconStem: iconNames?.Asteroid(ast.IconName)", gather);
        // Treasure/tech: the native icontechbmp override.
        Assert.Contains("IconStem: iconNames?.Treasure(te.Code, te.PayloadId, isTech)", gather);
        // The carrier: HudContact carries the resolved stem to the renderer.
        var model = Src(Path.Combine("Rendering", "HudModel.cs"));
        Assert.Contains("string? IconStem = null", model);
    }

    [Fact]
    public void The_renderer_draws_the_native_icon_additively_at_the_native_position()
    {
        var renderer = Src(Path.Combine("Rendering", "RadarBracketRenderer.cs"));
        // A separate additive pass, like native's BlendModeAdd (radarimage.cpp:418).
        Assert.Contains("private void DrawNativeIcons(IReadOnlyList<RadarContact> contacts, Viewport viewport)", renderer);
        Assert.Contains("_sb.Begin(SpriteSortMode.Deferred, BlendState.Additive)", renderer);
        // Placement through the pure native rule.
        Assert.Contains("RadarIconPlacement.Offset(c.OnScreen, c.ScreenPoint.X, c.ScreenPoint.Y", renderer);
        // Icon tint is colorIcon.
        Assert.Contains("NativeIconColour(c.SideColour, c.ThreatFlash)", renderer);
        // The procedural shape is now the fallback only.
        Assert.Contains("bool hasIcon = _icons?.Peek(c.IconStem) is not null;", renderer);
        Assert.Contains("if (!hasIcon)", renderer);
        // 1:1 pixel size, centred (native DrawImage3D(…, true)).
        Assert.Contains("new Vector2(texture.Width * 0.5f, texture.Height * 0.5f)", renderer);
        // Loader/setup wiring.
        var lifecycle = Src("Game1.Lifecycle.cs");
        Assert.Contains("new Rendering.RadarIconNameCache(_shopCatalog, _hullById)", lifecycle);
        Assert.Contains("Rendering.RadarIconTextures.Load(GraphicsDevice, _assets, _radarIconNames)", lifecycle);
        Assert.Contains("_radarRenderer.SetRadarIcons(_radarIconTextures)", lifecycle);
        Assert.Contains("_modernHudRenderer.SetRadarIcons(_radarIconTextures)", lifecycle);
        var modern = Src(Path.Combine("Rendering", "ModernHudRenderer.cs"));
        Assert.Contains("public void SetRadarIcons(RadarIconTextures? icons) => _radar.SetRadarIcons(icons);", modern);
        // The loader detects blank art instead of drawing nothing silently.
        var textures = Src(Path.Combine("Rendering", "RadarIconTextures.cs"));
        Assert.Contains("public static bool IsBlank(ReadOnlySpan<Color> pixels)", textures);
        Assert.Contains("if (IsBlank(colors))", textures);
        Assert.Contains("if (IsBlank(pixels))", textures);
        // No per-frame string building on the draw path (stems are resolved in
        // the gather; the renderer only looks them up).
        var draw = Method(renderer, "private void DrawNativeIcon(");
        Assert.DoesNotMatch(new Regex("\\$?\"[^\"]*\"\\s*\\+"), draw);
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing " + signature);
        int open = source.IndexOf('{', start), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }
        throw new InvalidOperationException("unterminated " + signature);
    }

    // ── test fixtures (records with only the fields under test) ────────────

    private static DataHullTypeIGC Hull(short id, string icon) =>
        default(DataHullTypeIGC) with { HullId = id, IconName = icon };

    private static DataStationTypeIGC Station(short id, string icon) =>
        default(DataStationTypeIGC) with { StationTypeId = id, IconName = icon };

    private static DataMissileTypeIGC Missile(short id, string icon) =>
        default(DataMissileTypeIGC) with { ExpendableTypeId = id, IconName = icon };

    private static DataProbeTypeIGC Probe(short id, string icon) =>
        default(DataProbeTypeIGC) with { ExpendableTypeId = id, IconName = icon };

    private static DataMineTypeIGC Mine(short id, string icon) =>
        default(DataMineTypeIGC) with { ExpendableTypeId = id, IconName = icon };

    private static DataPartTypeIGC Part(short id, string icon) =>
        default(DataPartTypeIGC) with { PartId = id, IconName = icon, Name = "part" };

    private static ShopCatalog Catalog(
        DataHullTypeIGC[]? hulls = null,
        DataPartTypeIGC[]? parts = null,
        DataStationTypeIGC[]? stations = null,
        DataMissileTypeIGC[]? missiles = null,
        DataMineTypeIGC[]? mines = null,
        DataProbeTypeIGC[]? probes = null) =>
        new(hulls ?? [], parts ?? [],
            StationTypes: stations?.ToDictionary(s => s.StationTypeId),
            MissileTypes: missiles?.ToDictionary(m => m.ExpendableTypeId),
            MineTypes: mines?.ToDictionary(m => m.ExpendableTypeId),
            ProbeTypes: probes?.ToDictionary(p => p.ExpendableTypeId));
}
