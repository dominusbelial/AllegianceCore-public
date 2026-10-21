using System;
using System.IO;
using System.Text.RegularExpressions;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-13 — the radar overlay brackets are sized from each contact's projected
/// screen radius instead of the old fixed 15 px (<c>radarimage.cpp:307-319</c>):
/// <list type="bullet">
/// <item><c>radiusObject = pts-&gt;GetScreenRadius()</c>, capped at
/// <c>sqrt2*80</c> (<c>radarimage.cpp:307-312</c>);</item>
/// <item><c>radiusBracket = 1.05*radiusObject</c>, clipped to
/// <c>distanceToEdge + c_flBorderSide</c> so the bracket stays on screen
/// (<c>radarimage.cpp:314-319</c>; <c>radarimage.h:20</c>);</item>
/// <item><c>GetScreenRadius()</c> is the angular projection of the object's world
/// radius, floored at <c>CmdViewMaxIconScreenSize</c> = 7 px
/// (<c>WinTrek/WinTrek.cpp:7928-7941</c>; <c>WinTrek/cmdview.h:56</c>), and an
/// off-screen blip is forced to that 7 px (<c>WinTrek.cpp:7876-7878</c>).</item>
/// </list>
/// All rules are pure and headless; the production wiring is guarded by
/// <see cref="Production_fills_each_contacts_native_bracket_inputs"/>.
/// </summary>
public class Hud13RadarBracketTests
{
    // ── RadarBracketRenderer.BracketRadius (radarimage.cpp:307-319) ────────

    [Fact]
    public void Bracket_radius_is_1_05_times_the_projected_screen_radius()
    {
        // radiusBracket = 1.05f * radiusObject (radarimage.cpp:314).
        Assert.Equal(15.75f, RadarBracketRenderer.BracketRadius(15f, 10_000f), 3);
        Assert.Equal(21f, RadarBracketRenderer.BracketRadius(20f, 10_000f), 3);
        // The floor every contact reports (7 px) still yields a 7.35 px bracket.
        Assert.Equal(7.35f, RadarBracketRenderer.BracketRadius(7f, 10_000f), 3);
    }

    [Fact]
    public void Bracket_radius_caps_the_object_radius_at_sqrt2_times_80()
    {
        // static const float maxRadius = sqrt2 * 80.0f (radarimage.cpp:308-312).
        const float cap = 1.41421356f * 80f;
        Assert.Equal(cap * 1.05f, RadarBracketRenderer.BracketRadius(500f, 100_000f), 2);
        Assert.Equal(cap * 1.05f, RadarBracketRenderer.BracketRadius(cap, 100_000f), 2);
        // Just under the cap is untouched (no cap-then-scale double count).
        Assert.Equal(100f * 1.05f, RadarBracketRenderer.BracketRadius(100f, 100_000f), 2);
    }

    [Fact]
    public void Bracket_radius_clips_to_the_edge_plus_the_native_border_side()
    {
        // if (radiusBracket > distanceToEdge + c_flBorderSide)
        //     radiusBracket = distanceToEdge + c_flBorderSide;   (radarimage.cpp:316-319)
        Assert.Equal(10f, RadarBracketRenderer.BracketRadius(100f, 0f), 3);       // edge-pinned blip
        Assert.Equal(40f, RadarBracketRenderer.BracketRadius(100f, 30f), 3);      // 30 + c_flBorderSide
        // Native's edge-pinned icon radius (7 px) scales to 7.35, still inside
        // the 10 px border gutter, so it is not clipped.
        Assert.Equal(7.35f, RadarBracketRenderer.BracketRadius(7f, 0f), 3);
        // Exactly at the clip boundary the smaller (unclipped) radius wins.
        Assert.Equal(10.5f, RadarBracketRenderer.BracketRadius(10f, 0.5f), 3);
        // A negative/NaN-free guard: a degenerate distance never expands the clip.
        Assert.Equal(10f, RadarBracketRenderer.BracketRadius(100f, -50f), 3);
    }

    [Fact]
    public void Contact_bracket_uses_the_projection_and_keeps_an_edge_contact_on_screen()
    {
        // Uniform projection scale: pxPerWorld = height/2 * |M22| / distance.
        const float height = 1080f, m22 = 1f / 0.4663f;   // ~50° vertical FOV
        // A 25 m-radius ship at 300 m: 25 * 540 * 2.1445 / 300 ≈ 96.5 px.
        float near = RadarScreenRadius.Project(25f, 300f, height, m22);
        Assert.Equal(25f * (height * 0.5f * m22 / 300f), near, 3);

        // Near/large beats far/small — the whole point of the row.
        Assert.True(near > RadarScreenRadius.Project(25f, 3000f, height, m22));
        Assert.True(RadarScreenRadius.Project(60f, 3000f, height, m22)
                  > RadarScreenRadius.Project(10f, 3000f, height, m22));

        // Native floor (WinTrek.cpp:7931-7941) and cap (radarimage.cpp:307-312).
        Assert.Equal(RadarScreenRadius.MinIconScreenRadius,
            RadarScreenRadius.Project(1f, 100_000f, height, m22), 3);
        Assert.Equal(RadarScreenRadius.MaxObjectScreenRadius,
            RadarScreenRadius.Project(10_000f, 10f, height, m22), 3);

        // An off-screen contact takes native's fixed 7 px icon radius
        // (WinTrek.cpp:7876-7878) and its clamped point's edge distance, so the
        // bracket clip keeps it inside the viewport.
        var edge = RadarScreenRadius.ForContact(
            onScreen: false, worldRadius: 500f, distanceToEye: 100f,
            viewportWidth: 1920f, viewportHeight: height, projectionM22: m22,
            screenX: 14f, screenY: 540f);
        Assert.Equal(7f, edge.ScreenRadius, 3);
        Assert.Equal(14f, edge.DistanceToEdge, 3);
        float bracket = RadarBracketRenderer.BracketRadius(edge.ScreenRadius, edge.DistanceToEdge);
        Assert.Equal(7.35f, bracket, 3);
        Assert.True(bracket < edge.DistanceToEdge, "an edge-clamped bracket must stay on screen");

        // An on-screen contact at the viewport centre reports the larger
        // distance-to-edge of the two axes.
        var centre = RadarScreenRadius.ForContact(
            onScreen: true, worldRadius: 25f, distanceToEye: 300f,
            viewportWidth: 1920f, viewportHeight: height, projectionM22: m22,
            screenX: 960f, screenY: 540f);
        Assert.Equal(540f, centre.DistanceToEdge, 3);
        Assert.Equal(near, centre.ScreenRadius, 3);
    }

    [Fact]
    public void Contact_without_a_projection_keeps_the_legacy_fixed_radius()
    {
        // Snapshot callers that do not fill the projection fall back to the
        // pre-HUD-13 radius (15 px); the production snapshot always fills it.
        var bare = new RadarBracketRenderer.RadarContact(
            RadarBracketRenderer.RadarContactType.Ship, new(200, 200), true, 1f);
        Assert.Equal(RadarBracketRenderer.FallbackBracketRadius,
            RadarBracketRenderer.BracketRadiusFor(bare), 3);

        var projected = bare with { ScreenRadius = 40f, DistanceToEdge = 500f };
        Assert.Equal(42f, RadarBracketRenderer.BracketRadiusFor(projected), 3);

        var clipped = bare with { ScreenRadius = 40f, DistanceToEdge = 12f };
        Assert.Equal(22f, RadarBracketRenderer.BracketRadiusFor(clipped), 3);
    }

    [Fact]
    public void Per_frame_contact_bracket_fill_does_not_allocate()
    {
        // The native sizing inputs are two floats carried inside the reused
        // HudContact struct (HUD-11 HudSnapshotBuffers); filling them must not
        // allocate per contact per frame.
        static long BytesPerFrame(int count)
        {
            var screenRadius = new float[count];
            var distanceToEdge = new float[count];
            const float height = 1080f, m22 = 1f / 0.4663f;
            void Frame()
            {
                for (int i = 0; i < count; i++)
                {
                    var b = RadarScreenRadius.ForContact(
                        onScreen: (i & 1) == 0, worldRadius: 10f + (i % 60), distanceToEye: 100f + (i % 3000),
                        viewportWidth: 1920f, viewportHeight: height, projectionM22: m22,
                        screenX: 40f + (i % 1800), screenY: 40f + (i % 1000));
                    screenRadius[i] = RadarBracketRenderer.BracketRadius(b.ScreenRadius, b.DistanceToEdge);
                    distanceToEdge[i] = b.DistanceToEdge;
                }
            }
            for (int warm = 0; warm < 20; warm++) Frame();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < 200; frame++) Frame();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(screenRadius);
            GC.KeepAlive(distanceToEdge);
            return bytes / 200;
        }

        long small = BytesPerFrame(4);
        long large = BytesPerFrame(4000);
        Assert.True(large < small + 4096,
            $"per-frame bracket fill allocation scaled with contacts: 4 contacts {small} B, 4000 contacts {large} B");
    }

    // ── Production wiring ─────────────────────────────────────────────────

    private static string Src(params string[] relative)
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string path = Path.Combine("src", "Allegiance.ClientCore.Game");
        foreach (var part in relative) path = Path.Combine(path, part);
        return File.ReadAllText(Path.Combine(root, path));
    }

    [Fact]
    public void A_fully_transparent_decoded_surface_counts_as_missing_art()
    {
        // HUD-13 — the artwork tree's enemybmp.mdl decodes to an all-black,
        // alpha-0 surface; drawing it would silently show nothing, so the loader
        // treats it as absent and the renderer keeps its procedural fallback.
        Assert.True(StatusTextures.IsFullyTransparent(
            new[] { new Microsoft.Xna.Framework.Color(0, 0, 0, 0), new Microsoft.Xna.Framework.Color(0, 0, 0, 0) }));
        Assert.False(StatusTextures.IsFullyTransparent(
            new[] { new Microsoft.Xna.Framework.Color(0, 0, 0, 0), new Microsoft.Xna.Framework.Color(1, 2, 3, 255) }));
    }

    [Fact]
    public void Production_fills_each_contacts_native_bracket_inputs()
    {
        // Game1.DrawRadar fills ScreenRadius/DistanceToEdge for every contact
        // kind from the shared projection, and the renderer consumes them
        // instead of a fixed 15 px bracket radius.
        string radar = Src("Game1.Rendering.cs");
        Assert.Contains("RadarScreenRadius.ForContact(", radar);
        Assert.Contains("site.Placement == RadarBracketLayout.RadarPlacement.OnScreen", radar);
        Assert.Contains("ScreenRadius: shipBracket.ScreenRadius", radar);
        Assert.Contains("DistanceToEdge: shipBracket.DistanceToEdge", radar);
        Assert.Contains("ScreenRadius: stationBracket.ScreenRadius", radar);
        Assert.Contains("ScreenRadius: warpBracket.ScreenRadius", radar);
        Assert.Contains("ScreenRadius: asteroidBracket.ScreenRadius", radar);

        string renderer = Src(Path.Combine("Rendering", "RadarBracketRenderer.cs"));
        Assert.Contains("float bracketRadius = BracketRadiusFor(c);", renderer);
        Assert.DoesNotContain("const float bracketRadius = 15f", renderer);
        // Every native bracket surface is drawn with that per-contact radius.
        Assert.DoesNotMatch(new Regex(@"DrawExpandedBlip\([^)]*,\s*15f\)"), renderer);
        // The nearest-enemy and selected-target surfaces are the native art.
        Assert.Contains("_artEnemy", renderer);
        Assert.Contains("_artTargeted", renderer);

        string status = Src(Path.Combine("Rendering", "StatusTextures.cs"));
        Assert.Contains("TryDecodeMdl(gd, assets, \"enemybmp\")", status);
        Assert.Contains("TryDecodeMdl(gd, assets, \"targetedbmp\")", status);
        Assert.Contains("TryDecodeMdl(gd, assets, \"Targetedbmp\")", status);
    }
}
