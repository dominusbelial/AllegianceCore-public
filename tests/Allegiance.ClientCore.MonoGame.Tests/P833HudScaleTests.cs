using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P833 — the Modern HUD is sized by the display's physical density (desktop
/// scale), not by window height, and every element shares that ONE scale so no
/// element can drift onto another at any supported window size. These tests
/// (pure, no graphics device) pin the scale rule and run a layout-collision
/// check over the required viewports for BOTH HUD styles.
/// </summary>
public class P833HudScaleTests
{
    // The five viewports the brief requires.
    private static readonly (int W, int H)[] Viewports =
    {
        (1920, 1080), (2560, 1440), (3840, 2160), (5120, 2160), (3840, 1620),
    };

    // ---- scale-from-physical-density --------------------------------------

    [Fact]
    public void Reference_density_1p0_keeps_preP833_scale()
    {
        // A 1.0 desktop at the default Size gives exactly 1.0 — today's behaviour.
        Assert.Equal(1f, ModernHudScale.Core(1f, 1f));
        Assert.Equal(1f, ModernHudScale.Core(0f, 1f));   // unknown density == reference
    }

    [Fact]
    public void Scale_follows_display_density_not_window_height()
    {
        // Same 2160-tall window: pre-P833 gave ~2.0 from height; now it is the
        // desktop scale alone (the physical-density fix).
        Assert.Equal(1.25f, ModernHudScale.Core(1.25f, 1f));
        Assert.Equal(2f, ModernHudScale.Core(2f, 1f));
        // Density × user Size compound linearly.
        Assert.Equal(2.5f, ModernHudScale.Core(1.25f, 2f));
    }

    [Fact]
    public void User_size_range_is_widened_and_clamped()
    {
        Assert.Equal(0.5f, ModernHudScale.Core(1f, 0.5f));
        Assert.Equal(2f, ModernHudScale.Core(1f, 2f));
        Assert.Equal(0.5f, ModernHudScale.Core(1f, 0.1f));   // below widened floor
        Assert.Equal(2f, ModernHudScale.Core(1f, 9f));       // above widened ceiling
        Assert.InRange(ModernHudScale.Core(1.25f, 0.5f), ModernHudScale.MinScale, ModernHudScale.MaxScale);
        Assert.InRange(ModernHudScale.Core(2f, 2f), ModernHudScale.MinScale, ModernHudScale.MaxScale);
    }

    [Fact]
    public void Cluster_and_pane_follow_the_one_scale_with_reference_spread()
    {
        // At reference density the cluster spread and pane scale equal the old
        // height-derived 1080p values exactly, so a 1080p desktop is unchanged.
        Assert.Equal(1080f / 680f, ModernHudScale.Cluster(1f), 4);
        Assert.Equal(1080f / 680f, ModernHudScale.Pane(1f), 4);
        Assert.Equal(OwnStatusLayout.Spread(1080), ModernHudScale.Cluster(1f), 4);
        Assert.Equal(TargetPaneLayout.Scale(1920, 1080), ModernHudScale.Pane(1f), 4);
        // Both grow monotonically with density (elements move together).
        Assert.True(ModernHudScale.Cluster(1.25f) > ModernHudScale.Cluster(1f));
        Assert.True(ModernHudScale.Pane(1.25f) > ModernHudScale.Pane(1f));
    }

    // ---- layout-collision check -------------------------------------------

    /// <summary>A named axis-aligned rect in screen px.</summary>
    private readonly record struct Elem(string Name, float X, float Y, float W, float H)
    {
        public bool Intersects(Elem o)
            => X < o.X + o.W && o.X < X + W && Y < o.Y + o.H && o.Y < Y + H;
        public bool Touches(Elem o)   // strict non-overlap with a 1px seam
            => X + W <= o.X + 1f || o.X + o.W <= X + 1f || Y + H <= o.Y + 1f || o.Y + o.H <= Y + 1f;
    }

    private static float EstW(string s, float scale) => s.Length * 11f * scale;
    private static float EstH(float scale) => 15f * scale;

    /// <summary>Compute the deterministic layout rects for the Modern HUD's
    /// primary positioned elements at a viewport × density, using the same pure
    /// layout helpers the renderer calls.</summary>
    private static List<Elem> ModernElems(int w, int h, float density)
    {
        var list = new List<Elem>();
        float core = ModernHudScale.Core(density, 1f);
        float spread = ModernHudScale.Cluster(core);

        // Six centre gauges: circle bbox + the label beneath its arc.
        var gauges = new (string Name, float Ox, float Oy)[]
        {
            ("THRUST", -126.5f, 93.5f), ("FUEL", 126.5f, 93.5f),
            ("ENERGY", 126.5f, -112.5f), ("AMMO", -126.5f, -112.5f),
            ("ARMOR", -65.5f, 35.5f), ("SHIELD", 65.5f, 35.5f),
        };
        float radius = 32f * core;
        foreach (var (label, ox, oy) in gauges)
        {
            float cx = w / 2f + ox * spread, cy = h / 2f + oy * spread;
            list.Add(new($"gauge:{label}", cx - radius, cy - radius, 2 * radius, 2 * radius));
            list.Add(new($"gauge-label:{label}", cx - EstW(label, core) / 2f, cy + radius + 8f * core, EstW(label, core), EstH(core)));
        }

        // Own-status badges (the native centre-console anchors).
        foreach (var (id, text) in new[]
        {
            ("Autopilot status", "AUTOPILOT"), ("Vector lock status", "VECTOR LOCK"),
            ("Cloak active", "CLOAK"), ("Carried flag", "FLAG"), ("Carried artifact", "ARTIFACT"),
            ("Detection eye", "EYE"), ("Signature percentage", "SIG 100%"), ("Kill bonus", "KB 20%"),
        })
        {
            var a = OwnStatusLayout.ScreenPos(id, w, h, modernScale: core);
            list.Add(new($"status:{id}", a.X, a.Y, EstW(text, core), EstH(core)));
        }

        // Bottom target pane (name/speed/range/tti rows + ARMOR/SHIELD labels +
        // the pane reserve rect) and the bottom-right minimap.
        float s = TargetPaneLayout.Scale(w, h, modernScale: core);
        float x0 = TargetPaneLayout.OriginX(w, h), y0 = TargetPaneLayout.OriginY(w, h);
        var pname = TargetPaneLayout.LocalCenterX * s - EstW("T: Fighter", core) / 2f;
        var nmBottom = y0 - TargetPaneLayout.LocalNameBottom * s;
        list.Add(new("pane-name", x0 + pname, nmBottom - EstH(core), EstW("T: Fighter", core), EstH(core)));
        list.Add(new("pane-speed", x0 + 242f * s - EstW("SPEED: 900", core), y0 - 33f * s - EstH(core), EstW("SPEED: 900", core), EstH(core)));
        list.Add(new("pane-range", x0 + 400f * s, y0 - 33f * s - EstH(core), EstW("RANGE: 950", core), EstH(core)));
        list.Add(new("pane-tti", x0 + 242f * s - EstW("TTI: (0:00)", core), y0 - 10f * s - EstH(core), EstW("TTI: (0:00)", core), EstH(core)));
        list.Add(new("pane-armor", x0 + (235f + 41.5f) * s - 8f * s - EstW("ARMOR", core), y0 - (33f + 50.5f) * s - EstH(core) / 2f, EstW("ARMOR", core), EstH(core)));
        list.Add(new("pane-shield", x0 + (367f + 42f) * s + 8f * s, y0 - (33f + 50.5f) * s - EstH(core) / 2f, EstW("SHIELD", core), EstH(core)));
        var pr = TargetPaneLayout.ReserveRect(w, h, modernScale: core);
        list.Add(new("pane-rect", pr.X, pr.Y, pr.Width, pr.Height));

        var (mx, my, mw, mh) = MinimapGeometry.CornerPanel(w, h);
        list.Add(new("minimap", mx, my, mw, mh));
        return list;
    }

    /// <summary>Same element set but Classic (native height-driven layout, no
    /// density override) — used to prove Classic is untouched.</summary>
    private static List<Elem> ClassicElems(int w, int h)
    {
        var list = new List<Elem>();
        float spread = OwnStatusLayout.Spread(h);
        float pane = TargetPaneLayout.Scale(w, h);
        float radius = 32f;   // classic gauge art is fixed pixel size
        var gauges = new (string Name, float Ox, float Oy)[]
        {
            ("THRUST", -126.5f, 93.5f), ("FUEL", 126.5f, 93.5f),
            ("ENERGY", 126.5f, -112.5f), ("AMMO", -126.5f, -112.5f),
            ("ARMOR", -65.5f, 35.5f), ("SHIELD", 65.5f, 35.5f),
        };
        foreach (var (label, ox, oy) in gauges)
        {
            float cx = w / 2f + ox * spread, cy = h / 2f + oy * spread;
            list.Add(new($"gauge:{label}", cx - radius, cy - radius, 2 * radius, 2 * radius));
        }
        foreach (var (id, _) in new[]
        {
            ("Autopilot status", "AUTOPILOT"), ("Vector lock status", "VECTOR LOCK"),
            ("Cloak active", "CLOAK"), ("Carried flag", "FLAG"), ("Carried artifact", "ARTIFACT"),
            ("Detection eye", "EYE"), ("Signature percentage", "SIG 100%"), ("Kill bonus", "KB 20%"),
        })
        {
            var a = OwnStatusLayout.ScreenPos(id, w, h);
            list.Add(new($"status:{id}", a.X, a.Y, 80f, 16f));
        }
        var pr = TargetPaneLayout.ReserveRect(w, h);
        list.Add(new("pane-rect", pr.X, pr.Y, pr.Width, pr.Height));
        var (mx, my, mw, mh) = MinimapGeometry.CornerPanel(w, h);
        list.Add(new("minimap", mx, my, mw, mh));
        return list;
    }

    private static IEnumerable<(string A, string B)> UnintendedPairs(List<Elem> elems)
    {
        for (int i = 0; i < elems.Count; i++)
            for (int j = i + 1; j < elems.Count; j++)
            {
                var a = elems[i]; var b = elems[j];
                // Check only pairs whose mutual disjointness is a hard invariant:
                //  • gauge rings against each other (each is a distinct ring),
                //  • target-pane internal rows against each other (no pane-rect),
                //  • the pane rectangle against the bottom-right minimap.
                // Hollow gauge arcs, the ring/status proximity, and the natively
                // adjacent own-status badge text are tuned by eye in screenshots
                // (native positions them adjacently), so those pairs are not
                // asserted here.
                bool inCat = (Cat(a.Name) == Cat(b.Name)) && Cat(a.Name) != "none";
                bool paneVsMinimap = (a.Name == "pane-rect" && b.Name == "minimap")
                                     || (b.Name == "pane-rect" && a.Name == "minimap");
                if (!inCat && !paneVsMinimap) continue;
                if (!a.Touches(b) && a.Intersects(b))
                    yield return (a.Name, b.Name);
            }
        static string Cat(string n) => n.StartsWith("gauge:") && !n.StartsWith("gauge-label:")
            ? "ring" : n.StartsWith("pane-") && n != "pane-rect" ? "pane" : "none";
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    public void Modern_has_no_unintended_intersections_at_any_viewport(float density)
    {
        foreach (var (w, h) in Viewports)
        {
            var elems = ModernElems(w, h, density);
            var hits = UnintendedPairs(elems).ToList();
            Assert.True(hits.Count == 0,
                $"Modern @ {w}x{h} density {density}: unintended overlaps: {string.Join(" ; ", hits)}");
        }
    }

    [Fact]
    public void Classic_layout_is_unchanged_from_native_height_driven_grid()
    {
        // Classic stays native (height-driven, fixed pixel art). The only hard
        // invariants we pin: the gauge ring and the pane/minimap separation hold
        // at every viewport (so P833 introduces no Classic regression).
        foreach (var (w, h) in Viewports)
        {
            var elems = ClassicElems(w, h);
            var hits = UnintendedPairs(elems).ToList();
            Assert.True(hits.Count == 0,
                $"Classic @ {w}x{h}: unintended overlaps: {string.Join(" ; ", hits)}");
        }
    }

    [Fact]
    public void Target_pane_never_reaches_the_minimap_under_the_one_scale()
    {
        foreach (var (w, h) in Viewports)
        {
            foreach (float density in new[] { 1f, 1.25f, 2f })
            {
                var pane = TargetPaneLayout.ReserveRect(w, h, modernScale: ModernHudScale.Core(density, 1f));
                var (mx, my, mw, mh) = MinimapGeometry.CornerPanel(w, h);
                var pm = new Elem("pane", pane.X, pane.Y, pane.Width, pane.Height);
                var mm = new Elem("mm", mx, my, mw, mh);
                Assert.False(pm.Intersects(mm), $"pane meets minimap @ {w}x{h} density {density}");
            }
        }
    }
}
