using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class RadarLabelModelTests
{
    private static HudContact Ship(
        bool onScreen = true, int? range = 500, string? name = "Scout",
        float? hull = 0.8f, float? shield = 0.6f, float? fill = null,
        bool isEnemy = false, bool isSameSide = false, bool isSelf = false,
        bool selected = false, bool accepted = false, bool queued = false,
        bool threat = false, bool flag = false, bool artifact = false, bool highlight = false)
        => new(HudContactKind.Ship, new(400, 300), onScreen, 1f, null, selected, false,
            name, range, shield, hull, fill, isEnemy, isSelf, isSameSide,
            accepted, queued, threat, false, false, flag, artifact, false, highlight, false);

    [Fact]
    public void Important_mask_forces_label_and_stats_even_in_minimal()
    {
        var contact = Ship(selected: true, name: "Scout", range: 700, hull: .5f, shield: .4f);
        var label = RadarLabelModel.Resolve(contact, RadarMode.Minimal, commandCamera: false);
        Assert.NotNull(label);
        Assert.Equal("Scout", label!.Value.Name);
        Assert.Equal("700", label.Value.RangeText);
        Assert.Equal(.5f, label.Value.Hull);
        Assert.Equal(.4f, label.Value.Shield);
    }

    [Fact]
    public void All_mode_labels_every_ship_and_suppresses_range_for_offscreen_same_side()
    {
        var enemy = Ship(isEnemy: true, onScreen: true, range: 900);
        var label = RadarLabelModel.Resolve(enemy, RadarMode.All, commandCamera: false);
        Assert.Equal("900", label!.Value.RangeText);
        Assert.NotNull(label.Value.Name);

        var sameSideOffScreen = Ship(isSameSide: true, onScreen: false, range: 900);
        var suppressed = RadarLabelModel.Resolve(sameSideOffScreen, RadarMode.All, commandCamera: false);
        Assert.Null(suppressed!.Value.RangeText);
        Assert.NotNull(suppressed.Value.Name);
    }

    [Fact]
    public void Default_mode_stats_for_enemy_and_no_range_for_same_side()
    {
        var enemy = Ship(isEnemy: true, onScreen: true, range: 900, hull: .5f, shield: .4f);
        var label = RadarLabelModel.Resolve(enemy, RadarMode.Default, commandCamera: false);
        Assert.NotNull(label);
        Assert.Equal("900", label!.Value.RangeText);
        Assert.Equal(.5f, label.Value.Hull);

        var friendly = Ship(isSameSide: true, onScreen: true, range: 900);
        var friendlyLabel = RadarLabelModel.Resolve(friendly, RadarMode.Default, commandCamera: false);
        Assert.Null(friendlyLabel!.Value.RangeText);
        Assert.Null(friendlyLabel.Value.Hull);   // no stats for same-side ships in Default
    }

    [Fact]
    public void Target_mode_hides_friendly_and_keeps_enemy_within_1000()
    {
        var friendly = Ship(isSameSide: true, onScreen: true, range: 500, name: "Friendly");
        var friendlyLabel = RadarLabelModel.Resolve(friendly, RadarMode.Target, commandCamera: false);
        Assert.Null(friendlyLabel);   // friendly/allied ships are hidden in Target LOD

        var enemy = Ship(isEnemy: true, onScreen: true, range: 500, hull: .5f);
        var enemyLabel = RadarLabelModel.Resolve(enemy, RadarMode.Target, commandCamera: false);
        Assert.NotNull(enemyLabel);
        Assert.Equal("500", enemyLabel!.Value.RangeText);
        Assert.Equal(.5f, enemyLabel.Value.Hull);
    }

    [Fact]
    public void Command_camera_suppresses_range_like_native()
    {
        var contact = Ship(selected: true, range: 700);
        var label = RadarLabelModel.Resolve(contact, RadarMode.All, commandCamera: true);
        Assert.NotNull(label);
        Assert.Null(label!.Value.RangeText);
    }

    [Fact]
    public void Stats_sentinel_above_one_suppresses_the_bar()
    {
        var contact = Ship(selected: true, hull: 2f, shield: 2f, fill: 2f);
        var label = RadarLabelModel.Resolve(contact, RadarMode.All, commandCamera: false);
        Assert.NotNull(label);
        Assert.Null(label!.Value.Hull);
        Assert.Null(label.Value.Shield);
        Assert.Null(label.Value.Fill);
    }

    [Fact]
    public void Station_labels_follow_all_default_target_tiers()
    {
        var station = new HudContact(HudContactKind.Station, new(400, 300), true, 1f, null, false, false,
            "Garrison", 1200, .5f, .8f, null);
        var all = RadarLabelModel.Resolve(station, RadarMode.All, false);
        Assert.True(all is not null, all?.ToString() ?? "<null>");
        Assert.Equal("Garrison", all!.Value.Name);
        Assert.Equal("1200", all.Value.RangeText);
        // Default shows a bare range line only for an on-screen station within
        // 1000 m (radarimage.cpp:802-806); farther stations drop the label.
        var near = station with { Range = 600 };
        var def = RadarLabelModel.Resolve(near, RadarMode.Default, false);
        Assert.NotNull(def);
        Assert.Null(def!.Value.Name);
        Assert.Equal("600", def.Value.RangeText);
        Assert.Null(RadarLabelModel.Resolve(station, RadarMode.Default, false));
        Assert.Null(RadarLabelModel.Resolve(station, RadarMode.Target, false));
    }

    [Fact]
    public void Warp_labels_in_all_and_default_and_range_only_in_target()
    {
        var warp = new HudContact(HudContactKind.Warp, new(400, 300), true, 1f, null, false, false,
            "A-4 Aleph", 600, null, null, null);
        Assert.Equal("A-4 Aleph", RadarLabelModel.Resolve(warp, RadarMode.All, false)!.Value.Name);
        Assert.Equal("A-4 Aleph", RadarLabelModel.Resolve(warp, RadarMode.Default, false)!.Value.Name);
        var target = RadarLabelModel.Resolve(warp, RadarMode.Target, false);
        Assert.NotNull(target);
        Assert.Null(target!.Value.Name);
        Assert.Equal("600", target.Value.RangeText);
        var farWarp = warp with { Range = 1500 };
        Assert.Null(RadarLabelModel.Resolve(farWarp, RadarMode.Target, false));
    }

    [Fact]
    public void Rock_labels_follow_native_generic_and_named_tiers()
    {
        var rock = new HudContact(HudContactKind.Asteroid, new(400, 300), true, 1f, null, false, false,
            "Garr-special", 500, null, .7f, .4f);
        var all = RadarLabelModel.Resolve(rock, RadarMode.All, false);
        Assert.NotNull(all);
        Assert.Equal("Garr-special", all!.Value.Name);
        Assert.Equal(.7f, all.Value.Hull);
        // Named special rocks outside All keep only a bare range line, exactly
        // like native's Default/Target asteroid tiers (radarimage.cpp:909-928).
        foreach (var mode in new[] { RadarMode.Default, RadarMode.Target })
        {
            var label = RadarLabelModel.Resolve(rock, mode, false);
            Assert.NotNull(label);
            Assert.Null(label!.Value.Name);
            Assert.Equal("500", label.Value.RangeText);
        }
        var generic = rock with { Name = "A-17" };
        var genericLabel = RadarLabelModel.Resolve(generic, RadarMode.All, false);
        Assert.NotNull(genericLabel);
        Assert.Null(genericLabel!.Value.Name);
        Assert.Equal("500", genericLabel.Value.RangeText);
    }
}
