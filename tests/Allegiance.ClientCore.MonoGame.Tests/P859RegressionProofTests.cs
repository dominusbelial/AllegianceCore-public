using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P859 — fail-first proofs for the two Live-tier regressions the fast tier misses.
///
/// <para><b>1. Pod autopilot home (P699 human).</b> Bisected to 7734a197 (P797):
/// the product was right — native <c>FindPath</c> (<c>Igc/common.cpp:894-1009</c>)
/// only expands hops through alephs the ship can see (<c>shipIGC.h:380-390</c>) and
/// the server only exports alephs the side has seen
/// (<c>fsmission.cpp:4175-4186</c>) — but the P699 fixture never seeded the map, so
/// its client knew one aleph pair and the two-jump route was (correctly)
/// unroutable. The fixture now sets <c>ShowMap</c> like the sibling P540b pod
/// fixture; these fast facts pin the rule the fixture depends on, so a later
/// "fix" cannot make the client route through alephs a side has not discovered
/// (which would silently re-break native map discovery to make a fixture pass).</para>
///
/// <para><b>2. Modern HUD overlap.</b> Bisected to aec8de11 (P833): replacing the
/// window-height text factor with the display-density one lost the upper bound a
/// small window relied on, so at 1280×720 with Size 1.5 the Modern radar label
/// block (582×83 px) had no free zone left and
/// <see cref="HudTextLayout"/>'s native-anchor overflow dropped it onto the
/// centred ripcord countdown. <see cref="ModernHudScale.Core(float,float,float)"/>
/// caps the density scale at the authored window budget; the HUD-2 live proof
/// (<c>ModernHudTests.Both_skins_render_Hud1_scenarios…</c>) is the geometry
/// proof, and these facts pin the scale rule itself.</para>
/// </summary>
public class P859RegressionProofTests
{
    // ── 1. Autopilot routes only over DISCOVERED alephs (native FindPath/CanSee) ──

    private const short Start = 1051, Hinge = 1052, Home = 1053;

    /// <summary>One aleph pair: 200 in <paramref name="from"/> facing its partner
    /// 201 in <paramref name="to"/> (native: a warp's DestinationId is the PARTNER
    /// warp id, warpIGC.h:129-141).</summary>
    private static void AddPair(ClientWorld world, short from, short to, short firstId, float x)
    {
        world.Apply(new ExportWarp
        {
            Warp = new DataWarpIGC(firstId, (short)(firstId + 1), 100, "", "", "Aleph",
                [x, 0, 0], [0, 0, 1], [0, 0, 0], 1f, from),
        });
        world.Apply(new ExportWarp
        {
            Warp = new DataWarpIGC((short)(firstId + 1), firstId, 100, "", "", "Aleph",
                [x, 100, 0], [0, 0, 1], [0, 0, 0], 1f, to),
        });
    }

    /// <summary>The P797 symptom: a client that has only discovered its own
    /// sector's aleph pair (what the unscouted P699 fixture received) has ONE
    /// edge, so the two-jump route home is unreachable and the autopilot refuses
    /// to engage — exactly as native FindPath returns NULL when no hop's aleph is
    /// visible (Igc/common.cpp:918-1000).</summary>
    [Fact]
    public void Autopilot_refuses_a_route_through_undiscovered_alephs()
    {
        var world = new ClientWorld();
        AddPair(world, Start, Hinge, 200, 0f);        // the start sector's own mouth

        var warps = Game1.AllWarps(world);
        Assert.Equal(2, warps.Count);                  // only the discovered pair (both mouths)
        Assert.DoesNotContain(warps, w => w.DestSector == Home);
        Assert.False(Game1.TryResolveAutopilotGoal(Start, Home, null, warps, out _, out var routed));
        Assert.False(routed);
    }

    /// <summary>With the whole route discovered (the fixture's <c>ShowMap</c> seed,
    /// and native's steady state once the side has scanned the alephs), the same
    /// call resolves the FIRST hop — the mouth of the start sector's aleph toward
    /// the hinge sector — and reports it as routed (native FindPath returns
    /// <c>path.pwarpStart</c>, Igc/common.cpp:1000-1005).</summary>
    [Fact]
    public void Autopilot_routes_the_two_jump_route_when_the_alephs_are_discovered()
    {
        var world = new ClientWorld();
        AddPair(world, Start, Hinge, 200, 11f);        // start → hinge
        AddPair(world, Hinge, Home, 202, 22f);         // hinge → home (second hop)

        var warps = Game1.AllWarps(world);
        Assert.Equal(4, warps.Count);                  // two discovered pairs, both directions
        Assert.True(Game1.TryResolveAutopilotGoal(Start, Home, null, warps, out var goal, out var routed));
        Assert.True(routed);
        // The goal is the start sector's own aleph, never the far target's
        // coordinates (the P540 "fly at a foreign sector" bug).
        Assert.Equal(11f, goal.X);
        Assert.Equal(0f, goal.Y);
    }

    /// <summary>A dangling edge (partner export not received yet) is skipped, never
    /// guessed: a warp id is not a sector id (P540c, warpIGC.h:129-141).</summary>
    [Fact]
    public void Warp_without_its_partner_is_not_a_route_edge()
    {
        var world = new ClientWorld();
        world.Apply(new ExportWarp
        {
            Warp = new DataWarpIGC(200, 201, 100, "", "", "Aleph", [5, 0, 0], [0, 0, 1], [0, 0, 0], 1f, Start),
        });
        Assert.Empty(Game1.AllWarps(world));
    }

    // ── 2. Modern HUD text scale never exceeds the window's authored budget ──

    /// <summary>The regression case: 1280×720 with Size 1.5. P833's density rule
    /// alone says 1.5; the authored ratio (1.5 scale units per 1080 px of window
    /// height) only allows 1.0, which is the pre-P833 value at that window. Classic
    /// is unaffected (native fixed pixels).</summary>
    [Fact]
    public void Modern_scale_is_capped_to_the_window_height_budget()
    {
        Assert.Equal(1f, ModernHudScale.Core(1f, 1.5f, 720f), 4);   // 1.5 → capped at 1.0
        Assert.Equal(1f, ModernHudScale.Core(1f, 2f, 720f), 4);     // 2.0 → capped at 1.0
        Assert.Equal(1f, ModernHudScale.Core(1f, 1f, 720f), 4);     // unchanged: below the cap
        Assert.Equal(0.5f, ModernHudScale.Core(1f, 0.5f, 720f), 4);
        Assert.Equal(1.5f, ModernHudScale.Core(1f, 1.5f, 1080f), 4);
        // The cap is an UPPER bound only: it can only ever lower the density scale.
        Assert.Equal(ModernHudScale.WindowFactor(720f), ModernHudScale.Core(1f, 2f, 720f), 4);
        Assert.True(ModernHudScale.Core(1f, 2f, 720f) <= ModernHudScale.Core(1f, 2f));
    }

    /// <summary>P833's fix is untouched wherever the window has room for the
    /// density it reports: a 1.25 desktop still gets 1.25× at 1080p and on taller
    /// windows (no window-height inflation — P833's whole point), and a 1.0 desktop
    /// still gets exactly its Size.</summary>
    [Fact]
    public void Modern_scale_keeps_P833_behaviour_where_the_window_has_room()
    {
        foreach (float height in new[] { 1080f, 1440f, 2160f })
        {
            Assert.Equal(1f, ModernHudScale.Core(1f, 1f, height), 4);
            Assert.Equal(1.25f, ModernHudScale.Core(1.25f, 1f, height), 4);
            Assert.Equal(1.5f, ModernHudScale.Core(1f, 1.5f, height), 4);
        }
        Assert.Equal(2.5f, ModernHudScale.Core(1.25f, 2f, 2160f), 4);
        Assert.Equal(2f, ModernHudScale.Core(1f, 2f, 2160f), 4);
    }

    /// <summary>Both skins' text never exceeds the authored budget: Classic is fixed
    /// at 1× native pixels (<c>HudRenderer.TextScale</c>) and Modern is bounded by
    /// the same ratio, so the HUD-2 no-overlap contract holds in both styles at
    /// every supported window and Size.</summary>
    [Fact]
    public void Both_skins_stay_within_the_authored_text_budget()
    {
        foreach (float height in new[] { 720f, 1080f, 1440f, 2160f })
        foreach (float size in new[] { 0.5f, 0.75f, 1f, 1.5f, 2f })
        foreach (float density in new[] { 1f, 1.25f, 2f })
        {
            float modern = ModernHudScale.Core(density, size, height);
            Assert.InRange(modern, ModernHudScale.MinScale, ModernHudScale.MaxScale);
            // The window budget is a hard ceiling, and the density rule is the other.
            Assert.True(modern <= ModernHudScale.WindowFactor(height) + 1e-4f, $"{height} {size} {density}");
            Assert.True(modern <= ModernHudScale.Core(density, size) + 1e-4f, $"{height} {size} {density}");
            // The 1080p reference configuration is exactly 1.0 — unchanged.
            if (height == 1080f && size == 1f && density == 1f) Assert.Equal(1f, modern, 4);
        }
    }
}
