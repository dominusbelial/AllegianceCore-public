using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P1084EjectBlastTests
{
    private static readonly DataHullTypeIGC Fighter = default(DataHullTypeIGC) with
    { HullId = 1, Length = 24, HabmCapabilities = DataHullTypeIGC.HabmFighter,
        Mass = 1, MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1], Hardpoints = [], PmEquipment = new short[8] };
    private static readonly SimVec LastPosition = new(120, 30, -400), LastVelocity = new(0, 5, -100);
    private static ServerShipUpdate Pose() => ServerShipUpdate.FromModel(1,
        new Ship(1, Fighter, SimVec.Zero, SimVec.Zero, Orientation.Identity), 100);
    private static ClientWorld World()
    {
        var world = new ClientWorld { LifepodHullResolver = _ => 33 };
        world.Apply(new SetCluster { SectorId = 1, Ship = Pose() });
        world.Apply(new ExportShip { ShipId = 1, HullId = 1, SideId = 0 });
        return world;
    }
    private static void Eject(ClientWorld world, short id = 1)
    {
        world.Apply(new KillShip { ShipId = id, BDeathCredit = false });
        world.Apply(Allegiance.Core.Wire.Eject.ForSurvivor(id, new(999, 999, 999), SimVec.Zero, new(0, 0, -1)));
    }
    private static (ClientWorld World, EjectBlastCues Cues) Observed(DataHullTypeIGC? hull = null)
    {
        var world = World(); var cues = new EjectBlastCues();
        cues.BeginFrame(world, 1, 1000);
        cues.ObserveShip(1, LastPosition, LastVelocity, hull ?? Fighter);
        return (world, cues);
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Visible_fighter_eject_has_one_small_blast_at_the_last_render_pose(HudStyle style)
    {
        var settings = new HudSettings { Style = style };
        var (world, cues) = Observed(); Eject(world); cues.BeginFrame(world, 1, 1016);
        var blast = Assert.Single(cues.Explosions);
        Assert.Equal(1, blast.Value); Assert.Equal(ExplosionType.SmallShip, blast.Key.Type);
        Assert.Equal(LastPosition, blast.Key.Position); Assert.Equal(LastVelocity, blast.Key.Velocity);
        Assert.Equal(1016, blast.Key.ReceivedLocalMs); Assert.Equal(33, world.Ships[1].HullId);
        Assert.Empty(world.Explosions); Assert.Empty(world.TakeExplosionSoundEvents());
        var draw = new List<ExplosionEntity>(); cues.AppendExplosions(world, draw);
        Assert.Same(blast.Key, Assert.Single(draw));
        cues.BeginFrame(world, 1, 1032); Assert.Single(cues.Explosions);
        Assert.Equal(style, settings.Style);
    }

    [Fact]
    public void Non_fighter_eject_has_a_big_blast()
    {
        var (world, cues) = Observed(Fighter with { HabmCapabilities = 0, Length = 240 });
        Eject(world); cues.BeginFrame(world, 1, 1016);
        Assert.Equal(ExplosionType.BigShip, Assert.Single(cues.Explosions).Key.Type);
    }

    [Theory]
    [InlineData("credited")] [InlineData("not-drawn")] [InlineData("stale")]
    [InlineData("hidden")] [InlineData("other-sector")] [InlineData("unknown-ship")] [InlineData("unknown-hull")]
    public void Only_noncredited_previous_frame_visible_known_hulls_create_cues(string reason)
    {
        var world = World(); var cues = new EjectBlastCues(); cues.BeginFrame(world, 1, 1000);
        if (reason != "not-drawn") cues.ObserveShip(1, LastPosition, LastVelocity, reason == "unknown-hull" ? null : Fighter);
        if (reason == "stale") cues.BeginFrame(world, 1, 1016);
        if (reason == "hidden") world.Apply(new ShipDelete { ShipId = 1, Sdr = ShipDelete.SdrLeftSector });
        if (reason == "other-sector") world.Apply(new ShipStatusMessage { ShipId = 1, SectorId = 2, StationId = -1 });
        world.Apply(new KillShip { ShipId = (short)(reason == "unknown-ship" ? 99 : 1), BDeathCredit = reason == "credited" });
        cues.BeginFrame(world, 1, 1032);
        Assert.Empty(cues.Explosions);
        var sounds = new List<ExplosionSoundEntity>(); cues.TakeSoundEvents(world, sounds); Assert.Empty(sounds);
    }

    [Theory]
    [InlineData("mission")] [InlineData("sector")] [InlineData("view")] [InlineData("explicit")]
    public void View_boundaries_clear_blasts_sounds_and_old_observations(string boundary)
    {
        var (world, cues) = Observed(); Eject(world); cues.BeginFrame(world, 1, 1016);
        Assert.Single(cues.Explosions); cues.ObserveShip(1, LastPosition, LastVelocity, Fighter);
        if (boundary == "mission") world.Reset();
        if (boundary == "view") world.Apply(new SetCluster { SectorId = 1, Ship = Pose() });
        if (boundary == "explicit") cues.Reset();
        short sector = (short)(boundary == "sector" ? 2 : 1);
        cues.BeginFrame(world, sector, 1032);
        Assert.Empty(cues.Explosions);
        var sounds = new List<ExplosionSoundEntity>(); cues.TakeSoundEvents(world, sounds); Assert.Empty(sounds);
        Eject(world); cues.BeginFrame(world, sector, 1048); Assert.Empty(cues.Explosions);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Preset_duration_expires_the_cue_and_undrained_sound_at_the_boundary(bool fighter)
    {
        var (world, cues) = Observed(Fighter with { HabmCapabilities = (short)(fighter ? DataHullTypeIGC.HabmFighter : 0) });
        Eject(world); cues.BeginFrame(world, 1, 1016);
        var blast = Assert.Single(cues.Explosions).Key;
        long end = 1016 + (long)Math.Ceiling(ExplosionPresets.For(blast.Type).TotalDurationSeconds * 1000);
        cues.BeginFrame(world, 1, end - 1); Assert.Single(cues.Explosions);
        cues.BeginFrame(world, 1, end); Assert.Empty(cues.Explosions);
        var sounds = new List<ExplosionSoundEntity>(); cues.TakeSoundEvents(world, sounds); Assert.Empty(sounds);
    }

    [Theory]
    [InlineData(4, ExplosionSoundTier.Small)] [InlineData(10, ExplosionSoundTier.Small)]
    [InlineData(24, ExplosionSoundTier.Medium)] [InlineData(100, ExplosionSoundTier.Medium)]
    [InlineData(240, ExplosionSoundTier.Large)]
    public void Sound_drains_exactly_once_and_uses_old_hull_diameter_for_native_tier(float length, ExplosionSoundTier expected)
    {
        var (world, cues) = Observed(Fighter with { Length = length }); Eject(world); cues.BeginFrame(world, 1, 1016);
        var events = new List<ExplosionSoundEntity>(); cues.TakeSoundEvents(world, events);
        var request = Assert.Single(events); Assert.Equal(length, request.Scale); Assert.Equal(LastPosition, request.Position);
        var sound = Assert.Single(ExplosionSoundRouter.Resolve(events, 1, LastPosition));
        Assert.Equal(expected, sound.Tier); Assert.Equal(ExplosionSoundCurve.MaxVolume, sound.Volume);
        Assert.Empty(ExplosionSoundRouter.Resolve(events, 2, LastPosition));
        Assert.Empty(ExplosionSoundRouter.Resolve(events, 1, LastPosition + new SimVec(6000, 0, 0)));
        events.Clear(); cues.TakeSoundEvents(world, events); Assert.Empty(events);
        cues.BeginFrame(world, 1, 1032); cues.TakeSoundEvents(world, events); Assert.Empty(events);
    }

    [Theory]
    [InlineData(0, 0, 0, true)] [InlineData(1, 0, 0, false)]
    [InlineData(0, 2, 0, false)] [InlineData(0, 0, 1000, false)]
    public void Only_an_exact_overlapping_server_blast_replaces_visual_and_pending_sound(float offset, int sectorOffset, int timeOffset, bool replaced)
    {
        var (world, cues) = Observed(); Eject(world); cues.BeginFrame(world, 1, 1016);
        var server = new ExplosionEntity { Type = ExplosionType.SmallShip, ClusterId = (short)(1 + sectorOffset),
            Position = LastPosition + new SimVec(offset, 0, 0), ReceivedLocalMs = 1016 + timeOffset };
        world.Apply(new ExplosionEvent { Type = server.Type, ClusterId = server.ClusterId, Position = server.Position });
        world.Explosions[0].ReceivedLocalMs = server.ReceivedLocalMs;
        server = world.Explosions[0];
        var draw = new List<ExplosionEntity> { server }; cues.AppendExplosions(world, draw);
        Assert.Equal(replaced ? 1 : 2, draw.Count); Assert.Same(server, draw[0]);
        var sounds = new List<ExplosionSoundEntity>(); cues.TakeSoundEvents(world, sounds);
        Assert.Equal(replaced ? 0 : 1, sounds.Count);
    }
}
