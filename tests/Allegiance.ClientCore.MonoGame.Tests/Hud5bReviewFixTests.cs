using Allegiance.ClientCore.MonoGame.Rendering;
using NumericsVec2 = System.Numerics.Vector2;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-5b — the Claude review fix round for HUD-5's radar overlays. Each test
/// pins one native rule the review found the port violating:
/// <list type="bullet">
/// <item>the friendly detection eye (radarimage.cpp:1006-1019),</item>
/// <item>no accepted/queued order brackets + the native brightness step
/// (radarimage.cpp:352, 1094-1096),</item>
/// <item>the nearest-enemy lookup only while bRangeLabels is on
/// (radarimage.cpp:523-524, 582-583; Igc/common.cpp:470-630),</item>
/// <item>the allocation-free label/range-string caches.</item>
/// </list>
/// </summary>
public class Hud5bReviewFixTests
{
    private static HudContact Ship(
        string? name = "Scout", int? range = 700, float? hull = .8f, float? shield = .6f, bool isEnemy = false,
        bool isSameSide = true, bool isSelf = false, bool selected = false,
        bool accepted = false, bool queued = false, bool threat = false,
        bool flag = false, bool artifact = false, bool eye = false,
        int? ripcordSeconds = null, int? commandId = null, bool commandAccepted = false)
        => new(HudContactKind.Ship, new(400, 300), true, 1f, null, selected, false,
            name, range, shield, hull, null, isEnemy, isSelf, isSameSide,
            accepted, queued, threat, false, false, flag, artifact, false, false, false,
            commandId, commandAccepted, ripcordSeconds, eye);

    // ── 1. Detection eye ────────────────────────────────────────────────────

    [Fact]
    public void Detection_eye_needs_a_different_same_side_player_ship()
    {
        // Native radarimage.cpp:1006-1019: pship != pshipSource,
        // GetPilotType() >= c_ptPlayer, GetSide() == psideMine, GetDetected().
        Assert.True(RadarLabelModel.ShowDetectionEye(
            isSelf: false, sameSide: true, playerPiloted: true, detected: true));

        Assert.False(RadarLabelModel.ShowDetectionEye(
            isSelf: true, sameSide: true, playerPiloted: true, detected: true));   // own ship
        Assert.False(RadarLabelModel.ShowDetectionEye(
            isSelf: false, sameSide: false, playerPiloted: true, detected: true)); // enemy ship
        Assert.False(RadarLabelModel.ShowDetectionEye(
            isSelf: false, sameSide: true, playerPiloted: false, detected: true)); // NPC drone
    }

    [Fact]
    public void Detection_eye_stays_off_while_detection_is_not_simulated()
    {
        // The server hard-codes Detected = true for every teammate
        // (Server.Broadcast.cs:1654,1667,1700). The client has no scanner, so the
        // reading is unknown (null) and the eye must NOT light up from that
        // constant — HUD-5 shipped a permanent '*' before this fix.
        Assert.False(RadarLabelModel.ShowDetectionEye(
            isSelf: false, sameSide: true, playerPiloted: true, detected: null));

        // A contact built the way the production radar snapshot does it carries
        // no eye, so the resolved label has none either.
        var contact = Ship(eye: false);
        var label = RadarLabelModel.Resolve(contact, RadarMode.Default, commandCamera: false);
        Assert.NotNull(label);
        Assert.False(label!.Value.Eye);
        Assert.DoesNotContain('*', RadarLabelCache.FormatNameLine(label.Value) ?? string.Empty);
    }

    // ── 2. Accepted/queued order brackets ───────────────────────────────────

    [Fact]
    public void Accepted_and_queued_orders_draw_no_overlay_bracket()
    {
        // Native draws brackets only for the enemy/me/subject/flag/artifact/
        // rip/targeted/last-fired masks (radarimage.cpp:352-357).
        var accepted = new RadarBracketRenderer.RadarContact(
            RadarBracketRenderer.RadarContactType.Ship, new NumericsVec2(10, 10), true, 1f,
            AcceptedOrder: true);
        var queued = accepted with { AcceptedOrder = false, QueuedOrder = true };
        Assert.False(RadarBracketRenderer.HasOverlayBracket(accepted));
        Assert.False(RadarBracketRenderer.HasOverlayBracket(queued));

        // The native bracket surfaces still draw.
        Assert.True(RadarBracketRenderer.HasOverlayBracket(accepted with { NearestEnemy = true }));
        Assert.True(RadarBracketRenderer.HasOverlayBracket(accepted with { CarriedFlag = true }));
        Assert.True(RadarBracketRenderer.HasOverlayBracket(accepted with { Threat = true }));

        // And the HudContact-side predicate agrees (the HUD coverage marker must
        // not claim an accepted/queued bracket either).
        Assert.False(RadarLabelModel.HasOverlayBracket(Ship(accepted: true)));
        Assert.False(RadarLabelModel.HasOverlayBracket(Ship(queued: true)));
    }

    [Fact]
    public void Order_state_changes_bracket_and_label_brightness_like_native()
    {
        // Native colorOther (radarimage.cpp:1094-1096): the important masks keep
        // full colour, a queued order dims to 0.75, everything else to 0.5.
        Assert.Equal(1f, RadarLabelModel.BracketBrightness(importantMask: true, queued: false));
        Assert.Equal(1f, RadarLabelModel.BracketBrightness(importantMask: true, queued: true));
        Assert.Equal(0.75f, RadarLabelModel.BracketBrightness(importantMask: false, queued: true));
        Assert.Equal(0.5f, RadarLabelModel.BracketBrightness(importantMask: false, queued: false));

        Assert.Equal(1f, RadarLabelModel.BracketBrightness(Ship(accepted: true)));
        Assert.Equal(1f, RadarLabelModel.BracketBrightness(Ship(selected: true)));
        Assert.Equal(1f, RadarLabelModel.BracketBrightness(Ship(threat: true)));
        Assert.Equal(0.75f, RadarLabelModel.BracketBrightness(Ship(queued: true)));
        Assert.Equal(0.5f, RadarLabelModel.BracketBrightness(Ship()));

        // The order still reaches the pilot through the label: the accepted
        // order forces the label line even in a mode that would hide it.
        var accepted = RadarLabelModel.Resolve(Ship(accepted: true, commandId: 7, commandAccepted: true),
            RadarMode.Default, commandCamera: false);
        Assert.NotNull(accepted);
        Assert.Equal(7, accepted!.Value.CommandId);
        Assert.True(accepted.Value.CommandAccepted);
        Assert.Equal("A7 Scout", RadarLabelCache.FormatNameLine(accepted.Value));

        var queued = RadarLabelModel.Resolve(Ship(queued: true, commandId: 7),
            RadarMode.Default, commandCamera: false);
        Assert.Equal("Q7 Scout", RadarLabelCache.FormatNameLine(queued!.Value));
    }

    // ── 3. Nearest enemy via FindTarget(c_ttShip|c_ttEnemy|c_ttNearest) ─────

    [Fact]
    public void Nearest_enemy_is_the_closest_visible_enemy_and_ignores_friends()
    {
        var candidates = new[]
        {
            new RadarLabelModel.NearestEnemyCandidate(1, Enemy: false, DistanceSquared: 10f),   // friend, closest
            new RadarLabelModel.NearestEnemyCandidate(2, Enemy: true, DistanceSquared: 900f),
            new RadarLabelModel.NearestEnemyCandidate(3, Enemy: true, DistanceSquared: 400f),   // nearest enemy
        };
        Assert.Equal((short?)3, RadarLabelModel.NearestEnemy(candidates));
        Assert.Null(RadarLabelModel.NearestEnemy(Array.Empty<RadarLabelModel.NearestEnemyCandidate>()));
        Assert.Null(RadarLabelModel.NearestEnemy(new[]
        {
            new RadarLabelModel.NearestEnemyCandidate(1, Enemy: false, DistanceSquared: 1f),
        }));
    }

    [Fact]
    public void Nearest_enemy_keeps_the_first_candidate_on_a_tie()
    {
        // Native replaces the current best only on `d < distance`
        // (Igc/common.cpp:608,621), so equal distances keep the first ship.
        var candidates = new[]
        {
            new RadarLabelModel.NearestEnemyCandidate(4, Enemy: true, DistanceSquared: 250f),
            new RadarLabelModel.NearestEnemyCandidate(5, Enemy: true, DistanceSquared: 250f),
        };
        Assert.Equal((short?)4, RadarLabelModel.NearestEnemy(candidates));
    }

    [Fact]
    public void Nearest_enemy_lookup_is_gated_by_bRangeLabels()
    {
        // native bRangeLabels = !CommandCamera (radarimage.cpp:523-524) and
        // pmodelEnemy is NULL when it is off (radarimage.cpp:582-583).
        Assert.True(RadarLabelModel.RangeLabelsEnabled(commandCamera: false));
        Assert.False(RadarLabelModel.RangeLabelsEnabled(commandCamera: true));

        // The command camera also suppresses the range text it gates.
        var label = RadarLabelModel.Resolve(Ship(selected: true), RadarMode.All, commandCamera: true);
        Assert.NotNull(label);
        Assert.Null(label!.Value.RangeText);
    }

    // ── 4. Allocation-free label hot path ──────────────────────────────────

    [Fact]
    public void Range_strings_are_cached_by_rounded_value()
    {
        Assert.Same(RadarLabelModel.RangeText(1234), RadarLabelModel.RangeText(1234));
        Assert.Same(RadarLabelModel.RangeLineText(1234), RadarLabelModel.RangeLineText(1234));
        Assert.Equal("1234 m", RadarLabelModel.RangeLineText(1234));
        Assert.False(ReferenceEquals(RadarLabelModel.RangeText(1234), RadarLabelModel.RangeText(1235)));
    }

    [Fact]
    public void Label_cache_reuses_the_block_and_only_refreshes_a_moved_range()
    {
        var cache = new RadarLabelCache();
        var contact = Ship(name: "Scout", range: 700);
        var first = cache.Resolve(0, contact, RadarMode.All, commandCamera: false);
        Assert.NotNull(first.Label);
        Assert.Equal("Scout", first.NameLine);
        Assert.Equal("700 m", first.RangeLine);

        // Same contact, different projected pixel: nothing about the label moved,
        // so the whole block (and every string in it) is reused.
        var moved = contact with { Position = new HudPoint(900, 700), Opacity = .4f };
        var second = cache.Resolve(0, moved, RadarMode.All, commandCamera: false);
        Assert.Equal(first, second);
        Assert.Same(first.NameLine, second.NameLine);
        Assert.Same(first.RangeLine, second.RangeLine);
        Assert.Same(first.Drawn, second.Drawn);

        // A one-metre change refreshes only the range line — the name line stays
        // the very same instance.
        var closer = moved with { Range = 699 };
        var third = cache.Resolve(0, closer, RadarMode.All, commandCamera: false);
        Assert.Same(first.NameLine, third.NameLine);
        Assert.Equal("699 m", third.RangeLine);

        // A name change rebuilds the line.
        var renamed = closer with { Name = "Wingman" };
        var fourth = cache.Resolve(0, renamed, RadarMode.All, commandCamera: false);
        Assert.Equal("Wingman", fourth.NameLine);
        Assert.NotSame(first.NameLine, fourth.NameLine);
    }

    [Fact]
    public void Label_cache_rebuilds_when_a_range_appears_where_there_was_none()
    {
        // Command camera suppresses range; leaving it must not leave the slot on
        // the stale "no label" fast path.
        var cache = new RadarLabelCache();
        // A same-side ship in Default has its range zeroed natively
        // (radarimage.cpp:748-752), so use an enemy contact: its range survives.
        var noRange = Ship(name: null, range: null, hull: null, shield: null,
            isEnemy: true, isSameSide: false);
        Assert.Null(cache.Resolve(0, noRange, RadarMode.Default, commandCamera: true).Label);
        var withRange = noRange with { Range = 800 };
        var view = cache.Resolve(0, withRange, RadarMode.Default, commandCamera: false);
        Assert.NotNull(view.Label);
        Assert.Equal("800 m", view.RangeLine);
    }

    [Fact]
    public void Label_cache_rebuilds_when_a_different_contact_reuses_the_slot()
    {
        var cache = new RadarLabelCache();
        cache.Resolve(0, Ship(name: "Scout", range: 700), RadarMode.All, commandCamera: false);
        var station = new HudContact(HudContactKind.Station, new(400, 300), true, 1f, null, false, false,
            "Garrison", 900, .5f, .8f, null);
        var view = cache.Resolve(0, station, RadarMode.All, commandCamera: false);
        Assert.Equal("Garrison", view.Label!.Value.Name);
        Assert.Equal("900 m", view.RangeLine);
    }

    [Fact]
    public void Printable_returns_the_same_instance_for_clean_text()
    {
        var clean = "Scout 1234 m";
        Assert.Same(clean, HudFormat.Printable(clean));
        Assert.Equal("S?out", HudFormat.Printable("S\u0001out"));
        Assert.Equal(clean, HudFormat.Printable(clean));
    }
}
