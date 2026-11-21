using System.Security.Cryptography;
using Allegiance.ClientCore.Browser.Assets;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.Browser.Tests;

public sealed class P1066BrowserSceneTests
{
    private static readonly DataHullTypeIGC Hull = default(DataHullTypeIGC) with
    { HullId = 1, Length = 24, RipcordSpeed = 15, Mass = 1, MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1], Hardpoints = [], PmEquipment = new short[8] };
    private static ServerShipUpdate Pose() => ServerShipUpdate.FromModel(1,
        new Ship(1, Hull, SimVec.Zero, new(0, 0, -100), Orientation.Identity), 100);
    private static ClientWorld World()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = Pose() });
        world.Apply(new ExportShip { ShipId = 1, HullId = 1, SideId = 0, CharacterName = "Pilot" });
        world.Apply(new ExportWarp { Warp = new(10, 20, 25, "plnt19", "", "Gate", [0, 0, -100], [0, 0, 1], [0, 0, 0, 0], 1, 1) });
        world.Apply(new ExportWarp { Warp = new(20, 10, 25, "plnt19", "", "Partner", [0, 0, 100], [0, 0, -1], [0, 0, 0, 0], 1, 2) });
        return world;
    }

    [Fact]
    public void First_spawn_has_no_flash_a_real_sector_edge_bursts_once_and_session_reset_clears_it()
    {
        var world = World(); var own = world.Ships[1]; var effects = new BrowserSceneEffects();
        effects.BeginFrame(world, own, 1 / 60f, 1);
        Assert.Equal(0, effects.EndFrame(own, SimVec.Zero, SimVec.Zero, Orientation.Identity, 12));
        Assert.False(effects.Flash.JustTransited);
        world.Apply(new SetCluster { SectorId = 2, Ship = Pose() });
        effects.BeginFrame(world, own, 1 / 60f, 2);
        effects.EndFrame(own, SimVec.Zero, SimVec.Zero, Orientation.Identity, 12);
        Assert.True(effects.Flash.JustTransited);
        effects.BeginFrame(world, own, 1 / 60f, 2.05);
        effects.FinishParticles(1 / 60f);
        Assert.NotEmpty(effects.Sparks);
        Assert.True(effects.EndFrame(own, SimVec.Zero, SimVec.Zero, Orientation.Identity, 12) > 0);
        Assert.False(effects.Flash.JustTransited);
        effects.Reset();
        Assert.Empty(effects.Sparks); Assert.False(effects.Flash.Active); Assert.Equal(0, effects.Pulses.ActiveCount);
    }

    [Fact]
    public void Drawn_approaching_ship_gets_both_strobes_and_native_sparks_and_transit_signals_are_drained()
    {
        var world = World(); var own = world.Ships[1]; var effects = new BrowserSceneEffects();
        effects.BeginFrame(world, own, 1 / 60f, .1);
        effects.GatherShip(world, own, SimVec.Zero, new(0, 0, -100), Orientation.Identity, Hull, 1 / 60f, 0);
        effects.FinishParticles(1 / 60f);
        Assert.NotEmpty(effects.Strobes); Assert.NotEmpty(effects.Sparks);
        world.SignalAlephTransit(10, 1, .1);
        effects.BeginFrame(world, own, 1 / 60f, .11);
        Assert.Equal(1, effects.Pulses.ActiveCount);
        Assert.Empty(world.DrainAlephTransits());
        Assert.Single(effects.Warps); Assert.Equal((short)10, effects.Warps[0].WarpId);
    }

    [Fact]
    public void New_view_does_not_replay_a_remote_ships_old_sector_history_as_a_fresh_arrival()
    {
        var world = World(); var own = world.Ships[1]; var effects = new BrowserSceneEffects();
        world.Apply(new ExportShip { ShipId = 2, HullId = 1, SideId = 0, CharacterName = "Other" });
        var other = world.Ships[2];
        void Set(string property, object value) => typeof(ShipEntity).GetProperty(property)!.SetValue(other, value);
        Set("StatusSectorKnown", true); Set("SectorId", (short)1);
        effects.BeginFrame(world, own, 1 / 60f, .1);
        effects.GatherShip(world, other, SimVec.Zero, SimVec.Zero, Orientation.Identity, Hull, 1 / 60f, 0);
        world.Apply(new SetCluster { SectorId = 2, Ship = Pose() });
        Set("SectorId", (short)2);
        effects.BeginFrame(world, own, 1 / 60f, .2);
        effects.Pulses.Reset(); // Test the remote sighting separately from own wire transit signals.
        effects.GatherShip(world, other, SimVec.Zero, SimVec.Zero, Orientation.Identity, Hull, 1 / 60f, 0);
        Assert.Equal(0, effects.Pulses.ActiveCount);
    }

    [Fact]
    public void Returning_from_camera_culling_uses_wire_velocity_instead_of_an_old_displacement_over_one_frame()
    {
        var world = World(); var own = world.Ships[1]; var effects = new BrowserSceneEffects();
        var velocity = new SimVec(0, 0, -100); var orientation = Orientation.Identity;
        effects.BeginFrame(world, own, 1 / 60f, .1);
        effects.GatherShip(world, own, new(0, 0, 50), velocity, orientation, Hull, 1 / 60f, 0);
        effects.FinishParticles(1 / 60f);
        effects.BeginFrame(world, own, 1 / 60f, .12); // ship is camera-culled this frame
        effects.FinishParticles(1 / 60f);
        effects.BeginFrame(world, own, 1 / 60f, .14);
        effects.Particles.Reset(); // inspect only the returning ship's new sparks
        effects.GatherShip(world, own, SimVec.Zero, velocity, orientation, Hull, 1 / 60f, 0);
        effects.FinishParticles(1 / 60f);
        Assert.NotEmpty(effects.Sparks);
        Assert.All(effects.Sparks, spark => Assert.Equal(-velocity, spark.Acceleration));
    }

    [Fact]
    public void Warm_browser_effect_preparation_including_live_sparks_allocates_zero_bytes()
    {
        var world = World(); var own = world.Ships[1]; var effects = new BrowserSceneEffects();
        var orientation = Orientation.Identity; double now = 0;
        for (int i = 0; i < 300; i++) Step();
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Step();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - bytes);
        void Step()
        {
            now += 1 / 60d;
            effects.BeginFrame(world, own, 1 / 60f, now);
            effects.GatherShip(world, own, SimVec.Zero, new(0, 0, -100), orientation, Hull, 1 / 60f, 0);
            effects.FinishParticles(1 / 60f);
            effects.EndFrame(own, SimVec.Zero, SimVec.Zero, orientation, 12);
        }
    }

    [Fact]
    public void Effect_load_setter_changes_only_browser_dust_budget()
    {
        // The setter is independent of graphics/session construction.
        var game = (Game1)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        game.SetEffectLoad(true); Assert.True(game.FullEffectLoad);
        game.SetEffectLoad(false); Assert.False(game.FullEffectLoad);
    }

    [Fact]
    public void Scene_closure_accepts_only_the_named_visual_config_and_retains_pack_bounds()
    {
        StagedAsset Asset(string name, byte value)
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData([value]));
            return new(name, "packs/" + hash, 1, hash, "application/octet-stream", "test original", []);
        }
        var resident = new StagedManifest(2, [Asset("core.igc", 1)]);
        var scene = Asset("giant-objects.json", 2);
        new BattleManifest(1, [scene], [new("scene-b1", [scene.Source])]).Validate(resident);
        Assert.Throws<InvalidDataException>(() => (new BattleManifest(1, [scene], [new("scene-giants", [scene.Source])])
            { SceneGiantSectors = [1057, 1057] }).Validate(resident));
        Assert.Throws<InvalidDataException>(() => (new BattleManifest(1, [scene], [new("scene-giants", [scene.Source])])
            { SceneGiantSectors = [-1] }).Validate(resident));
        var invalid = Asset("server.json", 3);
        Assert.Throws<InvalidDataException>(() => new BattleManifest(1, [invalid], [new("scene-b1", [invalid.Source])]).Validate(resident));
        var assets = Enumerable.Range(0, 129).Select(i => Asset("painting-" + i + ".png", 4)).ToArray();
        var sources = assets.Select(a => a.Source).ToArray();
        new BattleManifest(1, assets, [new("scene-b1", sources)]).Validate(resident);
        Assert.Throws<InvalidDataException>(() => new BattleManifest(1, assets, [new("hull-1", sources)]).Validate(resident));
    }

    [Fact]
    public void Pack_png_declared_as_image_png_is_refused_at_manifest_load_not_retried_in_flight()
    {
        // The lobby serves every extensionless packs/<sha256> file as application/octet-stream
        // (BrowserPreviewHosting), and the stager compares that header with the declared type.
        // An image/png pack (e_tele.png in scene-b1) used to fail its fetch on every retry.
        string hash = Convert.ToHexStringLower(SHA256.HashData([5]));
        var resident = new StagedManifest(2, [new("core.igc", "packs/" + Convert.ToHexStringLower(SHA256.HashData([1])), 1,
            Convert.ToHexStringLower(SHA256.HashData([1])), "application/octet-stream", "test original", [])]);
        var png = new StagedAsset("e_tele.png", "packs/" + hash, 1, hash, "image/png", "test original", []);
        Assert.Throws<InvalidDataException>(() => new BattleManifest(1, [png], [new("scene-b1", [png.Source])]).Validate(resident));
        new BattleManifest(1, [png with { ContentType = "application/octet-stream" }], [new("scene-b1", [png.Source])]).Validate(resident);
    }
}
