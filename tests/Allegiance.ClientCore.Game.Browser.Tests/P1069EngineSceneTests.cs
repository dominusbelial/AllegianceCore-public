using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.Browser.Tests;

/// <summary>P1069 B2 — the shared engine-effect preparation, the browser gather,
/// the reduced-effect ordering, reset and the budgeted hull upload queue.
/// Compiled against the real browser KNI scene helpers.</summary>
public sealed class P1069EngineSceneTests
{
    private static readonly DataHullTypeIGC Hull = default(DataHullTypeIGC) with
    {
        HullId = 1, ModelName = "fig21", Length = 24, Mass = 1, MaxTurnRates = [1, 1, 1],
        TurnTorques = [1, 1, 1], Hardpoints = [], PmEquipment = new short[8],
    };
    private static readonly DataHullTypeIGC Pod = default(DataHullTypeIGC) with
    {
        HullId = 2, ModelName = "utl203", Length = 6, Mass = 1, MaxTurnRates = [1, 1, 1],
        TurnTorques = [1, 1, 1], Hardpoints = [], PmEquipment = new short[8],
        HabmCapabilities = DataHullTypeIGC.HabmLifepod,
    };
    private static Ship Fixture(short id, SimVec position, int stateM = 0, float throttle = 0)
        => new(id, Hull, position, new(0, 0, -100), Orientation.Identity)
        { StateM = stateM, Controls = new ControlData(0, 0, 0, throttle) };
    private static ServerShipUpdate Pose(short id, SimVec position, int stateM = 0, float throttle = 0)
        => ServerShipUpdate.FromModel(id, Fixture(id, position, stateM, throttle), 100);

    private static ClientWorld World(int sectorShips = 2, int offSector = 0, int docked = 0, int dead = 0)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = Pose(1, SimVec.Zero) });
        world.Apply(new ExportShip { ShipId = 1, HullId = 1, SideId = 0, CharacterName = "Pilot" });
        var ships = new List<Ship>();
        short id = 2;
        for (int i = 0; i < sectorShips - 1; i++, id++)
        {
            world.Apply(new ExportShip { ShipId = id, HullId = 1, SideId = (short)(i % 2), CharacterName = "Ship" + id });
            ships.Add(Fixture(id, new SimVec(100 * id, 0, 0)));
        }
        if (ships.Count > 0) world.Apply(HeavyShipsUpdate.FromShips(101, SimVec.Zero, ships.ToArray()));
        for (int i = 0; i < offSector; i++, id++)
        {
            world.Apply(new ExportShip { ShipId = id, HullId = 1, SideId = 1, CharacterName = "Far" + id });
            world.Apply(new ShipStatusMessage { ShipId = id, HullId = 1, SectorId = 9, StationId = -1, ParentId = -1, StateTime = 200 });
        }
        for (int i = 0; i < docked; i++, id++)
        {
            world.Apply(new ExportShip { ShipId = id, HullId = 1, SideId = 1, CharacterName = "Dock" + id });
            var ship = world.Ships[id];
            typeof(ShipEntity).GetProperty("StationId")!.SetValue(ship, (short)4);
            typeof(ShipEntity).GetProperty("Latest")!.SetValue(ship, Pose(id, new SimVec(200 * id, 0, 0)));
        }
        for (int i = 0; i < dead; i++, id++)
        {
            world.Apply(new ExportShip { ShipId = id, HullId = 1, SideId = 1, CharacterName = "Dead" + id });
            typeof(ShipEntity).GetProperty("IsDead")!.SetValue(world.Ships[id], true);
        }
        return world;
    }

    private static BattleShipScene Scene() => new(new Dictionary<short, DataHullTypeIGC> { [1] = Hull, [2] = Pod });

    [Fact]
    public void Engine_gather_covers_only_the_drawn_current_sector_roster_and_includes_stand_ins()
    {
        var world = World(sectorShips: 4, offSector: 3, docked: 2, dead: 2);
        var scene = Scene();
        scene.Gather(world, 1, null);
        Assert.Equal(4, scene.Ships.Count);
        Assert.DoesNotContain(scene.Ships, s => s.StationId >= 0 || s.IsDead || s.SectorId != 1);

        var effects = new BrowserSceneEffects();
        effects.BeginFrame(world, world.Ships[1], 1 / 60f, .1);
        foreach (var ship in scene.Ships)
        {
            var pose = ship.Latest!;
            // No mesh bytes anywhere in this fixture: every hull is a stand-in and
            // still supplies a radius-based aft anchor.
            effects.GatherEngines(world, ship, pose.Position, pose.Velocity,
                pose.Orientation, Hull, 1 / 60f, null);
        }
        Assert.Equal(4, effects.Glows.Count);
        Assert.Equal(4, effects.Motion.RibbonCount);
        Assert.All(effects.Glows, glow => Assert.True(glow.HalfExtent > 0));
        // Every glow's anchor is the radius-based aft centre (no native frame).
        var view = effects.Glows[0];
        var expected = scene.Ships[0].Latest!.Position + (Orientation.Identity.GetBackward() *
            (Hull.Length * .5f * EngineNozzleGlowRenderer.AftCenterFraction));
        Assert.Equal(expected, view.Position);

        effects.FinishParticles(1 / 60f);
        Assert.Empty(effects.EngineTrailShips);   // no thrust input yet
    }

    [Fact]
    public void Thrust_ramp_and_glow_math_match_the_pre_extraction_desktop_formulas()
    {
        const float onRate = 6f, offRate = 3f, dt = 1 / 60f;
        float previous = 0f;
        // Desktop UpdateEngineTrail (Game1.Rendering.cs:141-153) before extraction.
        for (int i = 0; i < 200; i++)
        {
            float mounted = (float)ShipEffectRules.ThrustPower(true, true, previous, dt, onRate, offRate);
            Assert.Equal(mounted, NativeScene.ThrustPower(true, true, previous, dt), 6);
            previous = mounted;
        }
        Assert.Equal(1f, previous, 6);
        for (int i = 0; i < 200; i++)
        {
            float released = ShipEffectRules.ThrustPower(true, false, previous, dt, onRate, offRate);
            Assert.Equal(released, NativeScene.ThrustPower(true, false, previous, dt), 6);
            previous = released;
        }
        Assert.Equal(0f, previous, 6);
        // No mounted afterburner pins the power to zero (native WinTrek.cpp:8005-8006).
        Assert.Equal(0f, NativeScene.ThrustPower(false, true, 1f, dt));

        foreach (float throttle in new[] { -1f, -.5f, 0f, .25f, 1f })
        foreach (int stateM in new[] { 0, ShipControlState.AfterburnerButton })
        foreach (bool mounted in new[] { true, false })
            Assert.Equal(ShipEffectRules.NozzleGlow(mounted, (stateM & ShipControlState.AfterburnerButton) != 0, throttle),
                NativeScene.NozzleGlow(mounted, stateM, throttle), 6);

        for (int i = 0; i <= 100; i++)
        {
            float value = i / 100f;
            var (r, g, b, scale) = EngineNozzleGlow.Compute(value);
            var glow = NativeScene.GlowView(new SimVec(1, 2, 3), Orientation.Identity, 12, value, null);
            Assert.Equal(new Vector3(r, g, b), glow.Rgb);
            Assert.Equal(scale * 12 * EngineNozzleGlowRenderer.GlowSizeFactor, glow.HalfExtent, 6);
            Assert.Equal(new SimVec(1, 2, 3 + 12 * EngineNozzleGlowRenderer.AftCenterFraction), glow.Position);
        }
    }

    [Fact]
    public void Native_aft_frame_offset_uses_the_ry_pi_flip_and_scale_and_falls_back_without_a_frame()
    {
        var frames = new[]
        {
            new MdlFrame { Name = "hull", Position = [1f, 2f, 3f], Forward = [0f, 0f, 1f], Up = [0f, 1f, 0f] },
            new MdlFrame { Name = "thrust", Position = [1f, 2f, 3f], Forward = [0f, 0f, 1f], Up = [0f, 1f, 0f] },
        };
        var orientation = new Orientation(); orientation.Set(new SimVec(0, 0, -1));
        var offset = NativeScene.AftWorldOffset(frames, 2.5f, orientation);
        Assert.NotNull(offset);
        // RY(π) negates X and Z, then the uniform scale and the ship orientation.
        var flipped = new Vector3(-1f, 2f, -3f) * 2.5f;
        var expected = Vector3.Transform(flipped, PoseConverter.ToXnaRotation(orientation));
        Assert.Equal(new SimVec(expected.X, expected.Y, expected.Z), offset!.Value);
        var noThrust = new[] { frames[0] };
        Assert.Null(NativeScene.AftWorldOffset(noThrust, 2.5f, orientation));
        Assert.Null(NativeScene.AftWorldOffset(null, 2.5f, orientation));
    }

    [Fact]
    public void Ribbon_anchor_right_and_colour_match_the_native_set_anchor_inputs()
    {
        var world = World(sectorShips: 1);
        world.Apply(new SideRoster { Sides = [new SideRoster.SideRecord(0, 1, 1, "Alpha", .2f, .4f, .6f)] });
        var effects = new BrowserSceneEffects();
        var own = world.Ships[1];
        var orientation = new Orientation(); orientation.Set(new SimVec(0, 0, -1));
        effects.BeginFrame(world, own, 1 / 60f, .1);
        effects.GatherEngines(world, own, new SimVec(10, 20, 30), SimVec.Zero, orientation, Hull, 1 / 60f);
        effects.BeginFrame(world, own, 0.6f, .7);
        effects.GatherEngines(world, own, new SimVec(12, 20, 40), SimVec.Zero, orientation, Hull, 0.6f);
        var strip = Assert.Single(effects.Motion.Enumerate());
        Assert.Equal(new SimVec(12, 20, 40), strip.Centres[0]);
        Assert.Equal(orientation.GetRight() * (Hull.Length * .5f * NativeScene.TrailWidthFraction), strip.Rights[0]);
        Assert.Equal((.2f, .4f, .6f), (strip.R, strip.G, strip.B));
        Assert.Equal(1, strip.Sections);
        // Native UpdateColors taper: head bright, tail transparent (efgeo.cpp:570-592).
        Assert.Equal(1f, ShipMotionTrailRibbons.BrightnessFor(0, 0));
        Assert.Equal(0f, ShipMotionTrailRibbons.BrightnessFor(0, ShipMotionTrailRibbons.MaxSections));
    }

    [Fact]
    public void Reduced_effect_load_turns_exhaust_off_first_and_keeps_glow_and_ribbons()
    {
        var world = World(sectorShips: 2);
        var own = world.Ships[1];
        var thrusting = Pose(1, SimVec.Zero, ShipControlState.AfterburnerButton, 1f);
        world.Apply(new SetCluster { SectorId = 1, Ship = thrusting });
        own = world.Ships[1];
        var effects = new BrowserSceneEffects();
        void Frame(double now)
        {
            effects.BeginFrame(world, own, 1 / 60f, now);
            effects.GatherEngines(world, own, SimVec.Zero, new(0, 0, -100), Orientation.Identity, Hull, 1 / 60f);
            for (int i = 0; i < effects.EngineTrailShips.Count; i++)
                effects.Engines.Emit(effects.EngineTrailShips[i], 1 / 60f, new RandomTrailSource(1));
        }
        for (int i = 0; i < 12; i++) Frame(.1 + i / 60d);
        Assert.True(effects.Engines.ActiveCount > 0);
        Assert.NotEmpty(effects.Glows);

        effects.FullEffectLoad = false;
        Frame(.4);
        // Exhaust is cleared on the first reduced frame and nothing new emits.
        Assert.Equal(0, effects.Engines.ActiveCount);
        Assert.Empty(effects.EngineTrailShips);
        // Glow and ribbon stay.
        Assert.NotEmpty(effects.Glows);
        Assert.Equal(1, effects.Motion.RibbonCount);
        for (int i = 0; i < 20; i++) Frame(.5 + i / 60d);
        Assert.Equal(0, effects.Engines.ActiveCount);

        effects.FullEffectLoad = true;
        for (int i = 0; i < 12; i++) Frame(1 + i / 60d);
        Assert.True(effects.Engines.ActiveCount > 0);
    }

    [Fact]
    public void Reset_clears_pools_ramp_and_ribbon_history_on_a_session_or_view_change()
    {
        var world = World(sectorShips: 1);
        var own = world.Ships[1];
        var thrusting = Pose(1, SimVec.Zero, ShipControlState.AfterburnerButton, 1f);
        world.Apply(new SetCluster { SectorId = 1, Ship = thrusting });
        var effects = new BrowserSceneEffects();
        for (int i = 0; i < 20; i++)
        {
            effects.BeginFrame(world, world.Ships[1], 1 / 60f, .1 + i / 60d);
            effects.GatherEngines(world, world.Ships[1], SimVec.Zero, new(0, 0, -100), Orientation.Identity, Hull, 1 / 60f);
            for (int j = 0; j < effects.EngineTrailShips.Count; j++)
                effects.Engines.Emit(effects.EngineTrailShips[j], 1 / 60f, new RandomTrailSource(1));
        }
        Assert.True(effects.Engines.ActiveCount > 0);
        effects.Reset();
        Assert.Equal(0, effects.Engines.ActiveCount);
        Assert.Equal(0, effects.Motion.RibbonCount);
        Assert.Empty(effects.Glows);
        Assert.Empty(effects.EngineTrailShips);

        // A new view clears the pools through BeginFrame too.
        effects.BeginFrame(world, world.Ships[1], 1 / 60f, 2);
        effects.GatherEngines(world, world.Ships[1], SimVec.Zero, new(0, 0, -100), Orientation.Identity, Hull, 1 / 60f);
        for (int j = 0; j < effects.EngineTrailShips.Count; j++)
            effects.Engines.Emit(effects.EngineTrailShips[j], 1 / 60f, new RandomTrailSource(1));
        Assert.True(effects.Engines.ActiveCount > 0);
        world.Apply(new SetCluster { SectorId = 2, Ship = Pose(1, SimVec.Zero, ShipControlState.AfterburnerButton, 1f) });
        effects.BeginFrame(world, world.Ships[1], 1 / 60f, 2.1);
        Assert.Equal(0, effects.Engines.ActiveCount);
        // The ramp restarted: one frame of boost is one on-rate step, not full power.
        effects.GatherEngines(world, world.Ships[1], SimVec.Zero, new(0, 0, -100), Orientation.Identity, Hull, 1 / 60f);
        Assert.Equal(NativeScene.ThrustPowerOnRate / 60f, effects.EngineTrailShips[0].ThrustPower, 6);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(20)]
    public void Warm_engine_preparation_for_thrusting_ships_allocates_no_steady_state_bytes(int ships)
    {
        var world = World(sectorShips: ships);
        var scene = Scene();
        var effects = new BrowserSceneEffects();
        var own = world.Ships[1];
        scene.Gather(world, 1, null);
        var hull = Hull;
        // Wire Orientation decoding allocates on every access and belongs to the
        // existing prediction path (P1066 benchmark note); resolve it before warm.
        var thrusting = scene.Ships
            .Select(s =>
            {
                var pose = Pose(s.ShipId, s.Latest!.Position, ShipControlState.AfterburnerButton, 1f);
                return (pose.Position, pose.Velocity, pose.Orientation);
            }).ToArray();
        var random = new RandomTrailSource(3);
        double now = 0;
        for (int i = 0; i < 300; i++) Step();
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Step();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - bytes);

        void Step()
        {
            now += 1 / 60d;
            effects.BeginFrame(world, own, 1 / 60f, now);
            for (int i = 0; i < scene.Ships.Count; i++)
            {
                var pose = thrusting[i];
                effects.GatherEngines(world, scene.Ships[i], pose.Position, pose.Velocity,
                    pose.Orientation, hull, 1 / 60f);
            }
            for (int i = 0; i < effects.EngineTrailShips.Count; i++)
                effects.Engines.Emit(effects.EngineTrailShips[i], 1 / 60f, random);
        }
    }

    [Fact]
    public void Hull_upload_queue_spreads_promotions_and_keeps_the_stand_in_until_uploaded()
    {
        var ready = new HashSet<short>(); var uploads = new List<short>(); bool readable = true; long clock = 0;
        var queue = new BrowserHullUploads(ready.Contains, id => { uploads.Add(id); return readable; }, perFrameBudget: 1);
        // Bytes arrive for three hulls in the same frame; the roster observes them.
        ready.UnionWith([7, 8, 9]);
        foreach (short id in new short[] { 7, 8, 9 }) queue.Observe(id, clock += 16);
        Assert.Equal(3, queue.PendingCount);
        Assert.False(queue.Uploaded(7));

        Assert.Equal(1, queue.Step(clock += 16));   // one hull per frame, never all three
        Assert.Equal(new short[] { 7 }, uploads);
        Assert.True(queue.Uploaded(7));
        Assert.False(queue.Uploaded(8));
        Assert.Equal(2, queue.PendingCount);
        Assert.Equal(1, queue.Step(clock += 16));
        Assert.Equal(1, queue.Step(clock += 16));
        Assert.Equal(3, queue.UploadedCount);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, queue.Step(clock));
        // Re-observing an uploaded hull never queues it again.
        queue.Observe(7, clock);
        Assert.Equal(0, queue.PendingCount);

        // Promotion→first-draw delay is recorded once, with the worst kept.
        // Hull 7's bytes arrived at clock 16; hull 8's at 32.
        clock += 250;
        Assert.Equal(330, queue.NoteDrawn(7, clock));
        Assert.Equal(330, queue.LastFirstDrawMs);
        Assert.Equal(0, queue.NoteDrawn(7, clock + 10));
        clock += 40;
        Assert.Equal(354, queue.NoteDrawn(8, clock));
        Assert.Equal(354, queue.MaxFirstDrawMs);

        // An unreadable model ends the wait; the ship keeps its stand-in forever.
        readable = false;
        ready.Add(11);
        queue.Observe(11, clock);
        Assert.Equal(1, queue.Step(clock));
        Assert.True(queue.Uploaded(11));
        readable = true;
        queue.Reset();
        Assert.False(queue.Uploaded(7));
        Assert.Equal(0, queue.UploadedCount);
        Assert.Equal(0, queue.MaxFirstDrawMs);
    }

    [Fact]
    public void Hull_upload_budget_above_one_still_bounds_the_per_frame_decodes()
    {
        var ready = new HashSet<short> { 1, 2, 3, 4, 5 };
        int calls = 0;
        var queue = new BrowserHullUploads(ready.Contains, _ => { calls++; return true; }, perFrameBudget: 2);
        for (short id = 1; id <= 5; id++) queue.Observe(id, 0);
        Assert.Equal(2, queue.Step(0));
        Assert.Equal(2, calls);
        Assert.Equal(3, queue.PendingCount);
        Assert.Equal(2, queue.Step(0));
        Assert.Equal(1, queue.Step(0));
        Assert.Equal(5, calls);
    }
}
