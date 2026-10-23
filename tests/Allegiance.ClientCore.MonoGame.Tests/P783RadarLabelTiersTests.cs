using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P783 — the native radar label/stat tier table
/// (<c>WinTrek/radarimage.cpp:703-961</c>) pinned per contact kind across all
/// four LODs. Each test is one kind × every LOD; <see cref="RadarLabelModel.Spec"/>
/// is the single pure <c>kind × LOD × context → spec</c> function the resolver
/// and both HUD skins consume.
/// </summary>
public class P783RadarLabelTiersTests
{
    private static readonly RadarMode[] AllModes =
        { RadarMode.All, RadarMode.Default, RadarMode.Target, RadarMode.Minimal };

    private static RadarLabelSpec Spec(HudContact contact, RadarMode mode, bool commandCamera = false)
        => RadarLabelModel.Spec(contact, mode, commandCamera);

    private static void AssertSpec(HudContact contact, RadarMode mode,
        bool name, bool stats, bool range, bool commandCamera = false)
    {
        var spec = Spec(contact, mode, commandCamera);
        Assert.Equal((name, stats, range), (spec.Name, spec.Stats, spec.Range));
    }

    /// <summary>A ship contact with the flags the snapshot fills.</summary>
    private static HudContact Ship(
        bool self = false, bool sameSide = false, bool allied = false, bool enemy = false,
        bool onScreen = true, string? name = "Scout", int? range = 500, float? clip = null,
        float? hull = .8f, float? shield = .6f, float? fill = null, bool selected = false)
        => new HudContact(
            Kind: HudContactKind.Ship, Position: new(400, 300), OnScreen: onScreen,
            Opacity: 1f, SideColor: null, Selected: selected, BuildSpot: false,
            Name: name, Range: range, Shield: shield, Hull: hull, Fill: fill,
            IsEnemy: enemy, IsSelf: self, IsSameSide: sameSide, IsAllied: allied,
            ClipSeparation: clip);

    private static HudContact Pedestal(
        bool onScreen = true, int? range = 500, float? clip = null, bool selected = false)
        => new HudContact(
            Kind: HudContactKind.Station, Position: new(400, 300), OnScreen: onScreen,
            Opacity: 1f, SideColor: null, Selected: selected, BuildSpot: false,
            Name: "Pedestal", Range: range, Hull: .9f, Shield: .9f,
            IsPedestalStation: true, ClipSeparation: clip);

    private static HudContact Station(bool onScreen = true, int? range = 500, float? clip = null,
        bool selected = false)
        => new HudContact(
            Kind: HudContactKind.Station, Position: new(400, 300), OnScreen: onScreen,
            Opacity: 1f, SideColor: null, Selected: selected, BuildSpot: false,
            Name: "Garrison", Range: range, Hull: .9f, Shield: .7f,
            ClipSeparation: clip);

    private static HudContact Warp(int? range = 500, float? clip = null)
        => new HudContact(
            Kind: HudContactKind.Warp, Position: new(400, 300), OnScreen: true,
            Opacity: 1f, SideColor: null, Selected: false, BuildSpot: false,
            Name: "A-4 Aleph", Range: range, ClipSeparation: clip);

    private static HudContact Asteroid(string? name = "A-17", int? range = 500,
        float? clip = null, float? hull = .6f, float? fill = .3f)
        => new HudContact(
            Kind: HudContactKind.Asteroid, Position: new(400, 300), OnScreen: true,
            Opacity: 1f, SideColor: null, Selected: false, BuildSpot: false,
            Name: name, Range: range, Hull: hull, Fill: fill, ClipSeparation: clip);

    private static HudContact Missile(bool aimsAtOwnSide = false, int? range = 700,
        bool onScreen = true)
        => new HudContact(
            Kind: HudContactKind.Missile, Position: new(400, 300), OnScreen: onScreen,
            Opacity: 1f, SideColor: null, Selected: false, BuildSpot: false,
            Range: range, MissileTargetsOwnSide: aimsAtOwnSide,
            ClipSeparation: 0f);

    private static HudContact Probe(HudContactKind kind = HudContactKind.Probe,
        bool sameSide = false, bool allied = false, bool e = false, bool onScreen = true,
        int? range = 500, float? clip = null, float? hull = .5f)
        => new HudContact(
            Kind: kind, Position: new(400, 300), OnScreen: onScreen,
            Opacity: 1f, SideColor: null, Selected: false, BuildSpot: false,
            Name: kind == HudContactKind.Probe ? "Probe" : "Mine",
            Range: range, Hull: hull, IsEnemy: e, IsSameSide: sameSide, IsAllied: allied,
            ClipSeparation: clip);

    private static HudContact Treasure(bool useful = true, bool tech = false,
        int? range = 500, float? clip = null)
        => new HudContact(
            Kind: tech ? HudContactKind.Tech : HudContactKind.Treasure,
            Position: new(400, 300), OnScreen: true, Opacity: 1f, SideColor: null,
            Selected: false, BuildSpot: false, Name: "Powerup", Range: range,
            IsTech: tech, TreasureUseful: useful, ClipSeparation: clip);

    // ── Own ship (radarimage.cpp:740-745, :724-731) ──────────────────────────

    [Fact]
    public void Own_ship_tiers()
    {
        // The snapshot gives the own ship range 0 (native :626-628), so only the
        // name/stats terms are observable.
        var self = Ship(self: true, sameSide: true, range: 0, clip: 0f);
        AssertSpec(self, RadarMode.All, name: true, stats: true, range: false);
        AssertSpec(self, RadarMode.Default, name: true, stats: false, range: false);
        AssertSpec(self, RadarMode.Target, name: false, stats: false, range: false);
        AssertSpec(self, RadarMode.Minimal, name: false, stats: false, range: false);

        // A real (non-zero) range would still print in All/Default for self is
        // impossible natively; the tier itself stays range-free for self too.
        var label = RadarLabelModel.Resolve(self, RadarMode.All, commandCamera: false);
        Assert.Equal("Scout", label!.Value.Name);
        Assert.Equal(.8f, label.Value.Hull);
        Assert.Equal(.6f, label.Value.Shield);
        Assert.Null(RadarLabelModel.Resolve(self, RadarMode.Minimal, commandCamera: false));
    }

    // ── Friendly (exact own side) ship ───────────────────────────────────────

    [Fact]
    public void Friendly_ship_tiers()
    {
        var on = Ship(sameSide: true, onScreen: true, range: 500, clip: 400f);
        AssertSpec(on, RadarMode.All, name: true, stats: true, range: true);       // :742-744
        AssertSpec(on, RadarMode.Default, name: true, stats: false, range: false);  // :749-753
        AssertSpec(on, RadarMode.Target, name: false, stats: false, range: false);  // :760-766
        AssertSpec(on, RadarMode.Minimal, name: false, stats: false, range: false); // :724-731

        // All: an OFF-SCREEN same-side ship loses its range (the bracket carries
        // it), but keeps the label and the stats.
        var off = Ship(sameSide: true, onScreen: false, range: 500, clip: 400f);
        AssertSpec(off, RadarMode.All, name: true, stats: true, range: false);

        // Default beyond the clip and off screen: no label, no range (same side).
        var farOff = Ship(sameSide: true, onScreen: false, range: 2500, clip: 2000f);
        AssertSpec(farOff, RadarMode.Default, name: false, stats: false, range: false);
    }

    // ── Allied (not the exact own side) ship ─────────────────────────────────

    [Fact]
    public void Allied_ship_tiers()
    {
        // All :742-744 — only the EXACT own side loses the off-screen range.
        var off = Ship(allied: true, onScreen: false, range: 1500, clip: 1200f);
        AssertSpec(off, RadarMode.All, name: true, stats: true, range: true);
        // Default :749-755 — an ally gets stats (not the exact own side) and
        // keeps its range; off screen beyond the clip it loses only the NAME.
        AssertSpec(off, RadarMode.Default, name: false, stats: true, range: true);
        // Target :760-766 — allies leave the radar with the friendlies.
        AssertSpec(off, RadarMode.Target, name: false, stats: false, range: false);
        AssertSpec(off, RadarMode.Minimal, name: false, stats: false, range: false);
    }

    // ── Enemy ship ───────────────────────────────────────────────────────────

    [Fact]
    public void Enemy_ship_tiers()
    {
        var near = Ship(enemy: true, onScreen: true, range: 800, clip: 700f);
        AssertSpec(near, RadarMode.All, name: true, stats: true, range: true);
        AssertSpec(near, RadarMode.Default, name: true, stats: true, range: true);
        AssertSpec(near, RadarMode.Target, name: true, stats: true, range: true);   // :770-774
        AssertSpec(near, RadarMode.Minimal, name: false, stats: false, range: false);

        // Target: off screen => no stats; beyond the clip => no name, but the
        // bare range line survives (native never zeroes it on this path).
        var farOff = Ship(enemy: true, onScreen: false, range: 2500, clip: 2000f);
        AssertSpec(farOff, RadarMode.Target, name: false, stats: false, range: true);
        var nearOff = Ship(enemy: true, onScreen: false, range: 500, clip: 400f);
        AssertSpec(nearOff, RadarMode.Target, name: true, stats: false, range: true);
        // Default: on screen but beyond the clip keeps the name (:749).
        var nearOnFarRange = Ship(enemy: true, onScreen: true, range: 2500, clip: 2000f);
        AssertSpec(nearOnFarRange, RadarMode.Default, name: true, stats: true, range: true);
        // Default off screen beyond the clip: no name, but Default gives every
        // non-own-side ship stats (:754-755).
        AssertSpec(farOff, RadarMode.Default, name: false, stats: true, range: true);
    }

    // ── Station (pedestal and ordinary) ──────────────────────────────────────

    [Fact]
    public void Station_tiers()
    {
        var near = Station(onScreen: true, range: 600, clip: 500f);
        AssertSpec(near, RadarMode.All, name: true, stats: true, range: true);        // :796-800
        AssertSpec(near, RadarMode.Default, name: false, stats: false, range: true);  // :801-806
        AssertSpec(near, RadarMode.Target, name: false, stats: false, range: false);  // :807-813
        AssertSpec(near, RadarMode.Minimal, name: false, stats: false, range: false);

        // Default: off screen or beyond the clip drops the range (:803-804).
        var off = Station(onScreen: false, range: 600, clip: 500f);
        AssertSpec(off, RadarMode.Default, name: false, stats: false, range: false);
        var far = Station(onScreen: true, range: 2500, clip: 2000f);
        AssertSpec(far, RadarMode.Default, name: false, stats: false, range: false);
        // All keeps the label and stats on screen or not.
        AssertSpec(far, RadarMode.All, name: true, stats: true, range: true);

        // Pedestal stations take the fixed 1000 m tier in All/Default/Target
        // (:784-791): no label, no stats, range only within the clip. (Minimal
        // keeps its own earlier suppression, :724-731.)
        foreach (var mode in new[] { RadarMode.All, RadarMode.Default, RadarMode.Target })
        {
            AssertSpec(Pedestal(range: 2500, clip: 2000f), mode, name: false, stats: false, range: false);
            AssertSpec(Pedestal(range: 500, clip: 400f), mode, name: false, stats: false, range: true);
        }
        AssertSpec(Pedestal(range: 500, clip: 400f), RadarMode.Minimal, name: false, stats: false, range: false);
    }

    // ── Warp ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Warp_tiers()
    {
        var warp = Warp(range: 800, clip: 700f);
        AssertSpec(warp, RadarMode.All, name: true, stats: false, range: true);       // :954
        AssertSpec(warp, RadarMode.Default, name: true, stats: false, range: true);
        AssertSpec(warp, RadarMode.Target, name: false, stats: false, range: true);   // :955-956
        AssertSpec(warp, RadarMode.Minimal, name: false, stats: false, range: false);

        // The Target range clip uses the ADJUSTED separation, not the range line:
        // a range that LOOKS beyond 1000 m still prints when the surface
        // separation is inside it, and the other way round.
        var far = Warp(range: 1400, clip: 900f);
        AssertSpec(far, RadarMode.Target, name: false, stats: false, range: true);
        var past = Warp(range: 900, clip: 1200f);
        AssertSpec(past, RadarMode.Target, name: false, stats: false, range: false);
        Assert.NotNull(RadarLabelModel.Resolve(warp, RadarMode.Default, commandCamera: false));
    }

    // ── Asteroid (generic rock vs named special) ─────────────────────────────

    [Fact]
    public void Asteroid_tiers()
    {
        // Generic ('A...' or unnamed): never a name or a stat bar, only a
        // within-clip range line (:907-933).
        var generic = Asteroid(name: "A-17", range: 500, clip: 400f);
        AssertSpec(generic, RadarMode.All, name: false, stats: false, range: true);
        AssertSpec(generic, RadarMode.Default, name: false, stats: false, range: true);
        AssertSpec(generic, RadarMode.Target, name: false, stats: false, range: true);
        AssertSpec(generic, RadarMode.Minimal, name: false, stats: false, range: false);
        AssertSpec(generic with { ClipSeparation = 2000f, Range = 2500 },
            RadarMode.All, name: false, stats: false, range: false);
        AssertSpec(generic with { ClipSeparation = 2000f, Range = 2500 },
            RadarMode.Target, name: false, stats: false, range: false);
        AssertSpec(Asteroid(name: null), RadarMode.All, name: false, stats: false, range: true);

        // Named special rock: name + hull/ore stats in All only (:917-918),
        // a bare range in Default, and the Target clip (:939-943).
        var named = Asteroid(name: "Garr-special", range: 500, clip: 400f);
        AssertSpec(named, RadarMode.All, name: true, stats: true, range: true);
        AssertSpec(named, RadarMode.Default, name: false, stats: false, range: true);
        AssertSpec(named, RadarMode.Target, name: false, stats: false, range: true);
        AssertSpec(named, RadarMode.Minimal, name: false, stats: false, range: false);
        AssertSpec(named with { ClipSeparation = 2000f, Range = 2500 },
            RadarMode.Target, name: false, stats: false, range: false);
        // Default does NOT clip a named rock's range (native only tests the name
        // in All/Default; the 1000 m test sits in the generic branch).
        AssertSpec(named with { ClipSeparation = 2000f, Range = 2500 },
            RadarMode.Default, name: false, stats: false, range: true);

        var all = RadarLabelModel.Resolve(named, RadarMode.All, commandCamera: false);
        Assert.Equal("Garr-special", all!.Value.Name);
        Assert.Equal(.6f, all.Value.Hull);
        Assert.Equal(.3f, all.Value.Fill);
    }

    // ── Missile (aim tier) ───────────────────────────────────────────────────

    [Fact]
    public void Missile_tiers()
    {
        // A missile never has a name or a stat bar; All keeps the range line.
        var enemyAim = Missile(aimsAtOwnSide: false, range: 700);
        AssertSpec(enemyAim, RadarMode.All, name: false, stats: false, range: true);
        AssertSpec(enemyAim, RadarMode.Default, name: false, stats: false, range: false);
        AssertSpec(enemyAim, RadarMode.Target, name: false, stats: false, range: false);
        AssertSpec(enemyAim, RadarMode.Minimal, name: false, stats: false, range: false);

        // Outside All exactly the own-side-aimed missiles keep icon + range
        // (:821-830).
        var ownAim = Missile(aimsAtOwnSide: true, range: 700);
        AssertSpec(ownAim, RadarMode.All, name: false, stats: false, range: true);
        AssertSpec(ownAim, RadarMode.Default, name: false, stats: false, range: true);
        AssertSpec(ownAim, RadarMode.Target, name: false, stats: false, range: true);
        AssertSpec(ownAim, RadarMode.Minimal, name: false, stats: false, range: false);

        var label = RadarLabelModel.Resolve(ownAim, RadarMode.Target, commandCamera: false);
        Assert.Equal("700", label!.Value.RangeText);
        Assert.Null(label.Value.Name);
        Assert.Null(RadarLabelModel.Resolve(enemyAim, RadarMode.Target, commandCamera: false));
    }

    // ── Probe ────────────────────────────────────────────────────────────────

    [Fact]
    public void Probe_tiers()
    {
        // All: stats for every probe, a name only for an ON-SCREEN enemy, and
        // the same-side/ally range only when on screen or within the clip.
        var enemyOn = Probe(e: true, onScreen: true, range: 500, clip: 400f);
        AssertSpec(enemyOn, RadarMode.All, name: true, stats: true, range: true);
        var enemyOff = Probe(e: true, onScreen: false, range: 500, clip: 400f);
        AssertSpec(enemyOff, RadarMode.All, name: false, stats: true, range: true);
        var ownOffFar = Probe(sameSide: true, onScreen: false, range: 2500, clip: 2000f);
        AssertSpec(ownOffFar, RadarMode.All, name: false, stats: true, range: false);
        var ownOffNear = Probe(sameSide: true, onScreen: false, range: 500, clip: 400f);
        AssertSpec(ownOffNear, RadarMode.All, name: false, stats: true, range: true);
        var allyFar = Probe(allied: true, onScreen: true, range: 2500, clip: 2000f);
        AssertSpec(allyFar, RadarMode.All, name: false, stats: true, range: true);

        // Default: no label/stats; same-side/ally range clipped at 1000 m
        // regardless of on-screen state (:855-862).
        AssertSpec(enemyOn, RadarMode.Default, name: false, stats: false, range: true);
        AssertSpec(ownOffFar, RadarMode.Default, name: false, stats: false, range: false);
        AssertSpec(allyFar, RadarMode.Default, name: false, stats: false, range: false);

        // Target: only an enemy within the clip keeps a range (:864-871).
        AssertSpec(enemyOn, RadarMode.Target, name: false, stats: false, range: true);
        AssertSpec(enemyOn with { ClipSeparation = 2000f, Range = 2500 },
            RadarMode.Target, name: false, stats: false, range: false);
        AssertSpec(Probe(e: true, sameSide: true), RadarMode.Target, name: false, stats: false, range: false);
        AssertSpec(enemyOn, RadarMode.Minimal, name: false, stats: false, range: false);

        // The probe's real hull reading is the All-mode stat bar; the lifetime
        // fill is not on the wire (see the report).
        var label = RadarLabelModel.Resolve(enemyOn, RadarMode.All, commandCamera: false);
        Assert.Equal("Probe", label!.Value.Name);
        Assert.Equal(.5f, label.Value.Hull);
        Assert.Null(label.Value.Fill);
    }

    // ── Mine ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Mine_tiers()
    {
        // Native shares one code path for probes and mines (:834-875).
        var enemyOn = Probe(HudContactKind.Mine, e: true, onScreen: true, range: 500, clip: 400f);
        AssertSpec(enemyOn, RadarMode.All, name: true, stats: true, range: true);
        AssertSpec(enemyOn, RadarMode.Default, name: false, stats: false, range: true);
        AssertSpec(enemyOn, RadarMode.Target, name: false, stats: false, range: true);
        AssertSpec(enemyOn, RadarMode.Minimal, name: false, stats: false, range: false);

        var ownFar = Probe(HudContactKind.Mine, sameSide: true, onScreen: true, range: 2500, clip: 2000f);
        AssertSpec(ownFar, RadarMode.All, name: false, stats: true, range: true);
        AssertSpec(ownFar, RadarMode.Default, name: false, stats: false, range: false);
        AssertSpec(ownFar, RadarMode.Target, name: false, stats: false, range: false);
        // A mine has no hull stat; the fill bar is the lifetime fraction, which
        // the export does not carry, so the All block renders empty (report).
        var label = RadarLabelModel.Resolve(enemyOn, RadarMode.All, commandCamera: false);
        Assert.Equal("Mine", label!.Value.Name);
        Assert.Null(label.Value.Fill);
    }

    // ── Treasure and tech ────────────────────────────────────────────────────

    [Fact]
    public void Treasure_tiers()
    {
        // Useful treasure/tech: name in All only (:882), range everywhere except
        // the Target clip (:883-887), never a stat bar.
        foreach (var contact in new[] { Treasure(useful: true), Treasure(tech: true) })
        {
            AssertSpec(contact, RadarMode.All, name: true, stats: false, range: true);
            AssertSpec(contact, RadarMode.Default, name: false, stats: false, range: true);
            AssertSpec(contact, RadarMode.Target, name: false, stats: false, range: true);
            AssertSpec(contact, RadarMode.Minimal, name: false, stats: false, range: false);
            AssertSpec(contact with { ClipSeparation = 2000f, Range = 2500 },
                RadarMode.Target, name: false, stats: false, range: false);
            AssertSpec(contact with { ClipSeparation = 2000f, Range = 2500 },
                RadarMode.Default, name: false, stats: false, range: true);
        }

        // Not useful: native clears the icon/range outside All (:893-897), so the
        // only tier that can exist is the All-mode bare range line.
        var junk = Treasure(useful: false);
        AssertSpec(junk, RadarMode.All, name: false, stats: false, range: true);
        AssertSpec(junk, RadarMode.Default, name: false, stats: false, range: false);
        AssertSpec(junk, RadarMode.Target, name: false, stats: false, range: false);
        AssertSpec(junk, RadarMode.Minimal, name: false, stats: false, range: false);

        Assert.Equal("Powerup",
            RadarLabelModel.Resolve(Treasure(), RadarMode.All, commandCamera: false)!.Value.Name);
    }

    // ── Cross-cutting rules ─────────────────────────────────────────────────

    [Fact]
    public void Important_mask_forces_every_tier_in_every_lod()
    {
        // radarimage.cpp:704-712 — target/accepted/queued/highlight/threat/flag/
        // artifact short-circuit the per-kind switch entirely.
        foreach (var mode in AllModes)
        {
            AssertSpec(Ship(selected: true, enemy: true), mode, name: true, stats: true, range: true);
            AssertSpec(Station(selected: true), mode, name: true, stats: true, range: true);
            AssertSpec(Pedestal(selected: true), mode, name: true, stats: true, range: true);
        }
        // Native still zeroes the range in the command camera (:523-524).
        AssertSpec(Ship(selected: true), RadarMode.All, name: true, stats: true, range: false,
            commandCamera: true);
    }

    [Fact]
    public void Minimal_suppresses_every_non_important_label()
    {
        foreach (var contact in new[]
                 {
                     Ship(sameSide: true), Ship(enemy: true), Ship(self: true, range: 0),
                     Station(), Pedestal(), Warp(), Asteroid(), Missile(aimsAtOwnSide: true),
                     Probe(e: true), Probe(HudContactKind.Mine, e: true), Treasure(),
                 })
        {
            var spec = Spec(contact, RadarMode.Minimal);
            Assert.False(spec.Name, contact.Kind.ToString());
            Assert.False(spec.Stats, contact.Kind.ToString());
            Assert.False(spec.Range, contact.Kind.ToString());
            Assert.Null(RadarLabelModel.Resolve(contact, RadarMode.Minimal, commandCamera: false));
        }
    }

    [Fact]
    public void Clip_uses_the_adjusted_separation_not_the_printed_range()
    {
        // radarimage.cpp:626-636 — the range line prints the centre distance but
        // every rangeClipLabels test runs on the surface separation.
        var contact = Warp(range: 1400, clip: 900f);
        Assert.True(RadarLabelModel.WithinRangeClip(contact));
        AssertSpec(contact, RadarMode.Target, name: false, stats: false, range: true);

        var past = Warp(range: 900, clip: 1200f);
        Assert.False(RadarLabelModel.WithinRangeClip(past));
        AssertSpec(past, RadarMode.Target, name: false, stats: false, range: false);

        // Headless fixtures without a clip keep the pre-P783 range comparison.
        Assert.True(RadarLabelModel.WithinRangeClip(Warp(range: 900)));
        Assert.False(RadarLabelModel.WithinRangeClip(Warp(range: 1500)));
    }

    [Fact]
    public void Command_camera_removes_every_range_line_but_keeps_name_and_stats()
    {
        var enemy = Ship(enemy: true, onScreen: true, range: 800, clip: 700f);
        AssertSpec(enemy, RadarMode.All, name: true, stats: true, range: false,
            commandCamera: true);
        var warp = Warp();
        AssertSpec(warp, RadarMode.Default, name: true, stats: false, range: false,
            commandCamera: true);
    }

    [Fact]
    public void Clipped_range_never_reaches_the_drawn_range_line()
    {
        // Regression: the label cache used to publish a range line from the raw
        // contact range, which defeated every native clip in the renderer.
        var cache = new RadarLabelCache();
        var clipped = Warp(range: 900, clip: 1000f);
        var view = cache.Resolve(0, clipped, RadarMode.Target, commandCamera: false);
        Assert.Null(view.Label);            // name clipped, range clipped => no block
        Assert.Null(view.RangeLine);

        cache = new RadarLabelCache();
        var kept = Warp(range: 800, clip: 700f);
        view = cache.Resolve(0, kept, RadarMode.Target, commandCamera: false);
        Assert.Equal("800 m", view.RangeLine);
    }

    [Fact]
    public void Label_cache_rebuilds_when_the_contact_crosses_the_clip()
    {
        var cache = new RadarLabelCache();
        var near = Warp(range: 800, clip: 700f);
        Assert.Equal("800 m", cache.Resolve(0, near, RadarMode.Target, false).RangeLine);
        var far = near with { Range = 1200, ClipSeparation = 1100f };
        var view = cache.Resolve(0, far, RadarMode.Target, false);
        Assert.Null(view.Label);
        Assert.Null(view.RangeLine);
        // ...and back again.
        Assert.Equal("800 m", cache.Resolve(0, near, RadarMode.Target, false).RangeLine);
    }
}
