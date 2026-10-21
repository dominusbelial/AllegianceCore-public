using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class Hud4RadarTests
{
    private static HudFrame Frame => new(0.8f, 0.6f, 0.4f, 0.3f, 0.2f, 1f,
        120f, -1f, false, false, true, 0, false, 0f, false, false,
        0f, 0f, Array.Empty<WeaponStatusRow>(), false, 0);

    [Fact]
    public void Radar_contact_snapshot_keeps_every_kind_and_extra_fields()
    {
        var contacts = new[]
        {
            new HudContact(HudContactKind.Ship, new(1, 2), true, 1f, new(.1f, .2f, .3f), true, false),
            new HudContact(HudContactKind.Missile, new(3, 4), false, 1f, null, false, false),
            new HudContact(HudContactKind.Probe, new(5, 6), true, 1f, new(.4f, .5f, .6f), false, false),
            new HudContact(HudContactKind.Mine, new(7, 8), true, 1f, null, false, false),
            new HudContact(HudContactKind.Treasure, new(9, 10), true, 1f, null, false, false),
            new HudContact(HudContactKind.Tech, new(11, 12), true, 1f, null, false, false, IsTech: true),
            new HudContact(HudContactKind.Ship, new(13, 14), true, 1f, null, false, false, IsSelf: true),
        };
        var model = HudModel.Create(Frame, new() { Contacts = contacts, RoundRadar = true });
        Assert.Equal(7, model.Context.Contacts.Count);
        Assert.Equal(2, model.Elements.Single(e => e.Id == "Radar ship contacts").Value);
        Assert.Equal(1, model.Elements.Single(e => e.Id == "Radar missile contacts").Value);
        Assert.Equal(2, model.Elements.Single(e => e.Id == "Radar probe and mine contacts").Value);
        Assert.Equal(2, model.Elements.Single(e => e.Id == "Radar treasure and tech contacts").Value);
        Assert.Equal(1, model.Elements.Single(e => e.Id == "Radar viewport and round border").Value);
        Assert.True(model.Context.RoundRadar);
        Assert.True(model.Context.Contacts[6].IsSelf);
        Assert.True(model.Context.Contacts[5].IsTech);
    }

    [Fact]
    public void Lock_fraction_and_target_screen_radius_are_retained()
    {
        var model = HudModel.Create(Frame, new()
        {
            Lock = new(new(100, 200), MissileLockFeedback.LockState.Partial, 0, .4f, 22f),
            Target = new(new(300, 400), false, "rock", null, null, null, .5f, SideColor: new(.9f, .1f, .1f), ScreenRadius: 34f),
        });
        var lockElement = model.Elements.Single(e => e.Id == "Missile lock flower");
        Assert.True(lockElement.Visible);
        Assert.Equal(22f, model.Context.Lock!.Value.ScreenRadius);
        Assert.Equal(.4f, model.Context.Lock!.Value.Fraction);
        Assert.Equal(34f, model.Context.Target!.Value.ScreenRadius);
        Assert.Equal(new(.9f, .1f, .1f), model.Context.Target!.Value.SideColor);
    }

    [Fact]
    public void Lock_flower_sizing_follows_native_continuous_lock_rule()
    {
        // radarimage.cpp:308-332 — radiusBracket = 1.05×radius, clipped to the
        // screen edge; partial radius = base + 40×(1−lock).
        const float far = 1000f;
        Assert.Equal(34f * 1.05f + 40f * .6f, LockBracketStyle.HalfSizeForFraction(
            MissileLockFeedback.LockState.Partial, .4f, 34f, far));
        Assert.Equal(34f * 1.05f, LockBracketStyle.HalfSizeForFraction(
            MissileLockFeedback.LockState.Full, 1f, 34f, far));
        Assert.Equal(0f, LockBracketStyle.HalfSizeForFraction(
            MissileLockFeedback.LockState.None, 0f, 34f, far));
        // The base radius clips to the screen edge (distanceToEdge + border).
        Assert.Equal(30f + LockBracketStyle.BorderSide, LockBracketStyle.HalfSizeForFraction(
            MissileLockFeedback.LockState.Full, 1f, 100f, 30f));
        // Unknown projected radius falls back to the established fixed base.
        Assert.Equal(LockBracketStyle.BaseHalfSize + 40f, LockBracketStyle.HalfSizeForFraction(
            MissileLockFeedback.LockState.Partial, 0f, null, far));
    }

    [Theory]
    [InlineData(TreasureCode.Cash, RadarTreasureUse.UsefulPart)]
    [InlineData(TreasureCode.Flag, RadarTreasureUse.UsefulPart)]
    [InlineData(TreasureCode.Powerup, RadarTreasureUse.UsefulPart)]
    [InlineData(TreasureCode.Development, RadarTreasureUse.UsefulPart)]
    [InlineData(TreasureCode.Part, RadarTreasureUse.UsefulTech)]          // un-researched part → tech icon
    public void Radar_treasure_use_matches_native_switch(TreasureCode code, RadarTreasureUse expected)
    {
        bool effectsSatisfied = expected != RadarTreasureUse.UsefulTech;
        var use = RadarTreasure.Usefulness(code, code == TreasureCode.Part ? EquipmentType.Weapon : null,
            effectsSatisfied, packUseful: true, launcherMountable: true);
        Assert.Equal(expected, use);
    }

    [Fact]
    public void Radar_treasure_non_launcher_researched_part_is_not_useful()
    {
        // radarimage.cpp:503-508 — a researched non-launcher part (e.g. a weapon)
        // gets no useful-part icon; only a launcher type the hull can mount does.
        Assert.Equal(RadarTreasureUse.NotUseful,
            RadarTreasure.Usefulness(TreasureCode.Part, EquipmentType.Weapon, true, true, true));
        Assert.Equal(RadarTreasureUse.UsefulPart,
            RadarTreasure.Usefulness(TreasureCode.Part, EquipmentType.Magazine, true, true, true));
        Assert.Equal(RadarTreasureUse.NotUseful,
            RadarTreasure.Usefulness(TreasureCode.Part, EquipmentType.Magazine, true, true, launcherMountable: false));
    }

    [Fact]
    public void Radar_treasure_pack_is_useful_only_when_relevant()
    {
        Assert.Equal(RadarTreasureUse.UsefulPart,
            RadarTreasure.Usefulness(TreasureCode.Part, EquipmentType.Pack, true, packUseful: true));
        Assert.Equal(RadarTreasureUse.NotUseful,
            RadarTreasure.Usefulness(TreasureCode.Part, EquipmentType.Pack, true, packUseful: false));
    }
}
