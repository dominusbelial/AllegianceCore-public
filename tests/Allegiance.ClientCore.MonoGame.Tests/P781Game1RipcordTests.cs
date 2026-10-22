using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P781 — the Game1-level halves of the ripcord lead-up, driven headless through
/// the REAL production methods (<c>RipcordStatusLabel</c>,
/// <c>UpdateOwnShipSimulation</c>) on an uninitialised Game1 (the P757 headless
/// pattern). Written against reflection only, so the same file compiles and runs
/// on the pre-P781 base — that is how the fail-first results in the P781 report
/// were taken.
/// </summary>
public class P781Game1RipcordTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const short Me = 5;
    private const short HullId = 509;
    private const float RipcordSeconds = 3f;
    private static readonly float[] SpinRates = [0.8f, -0.3f, 0.5f];

    private static readonly DataHullTypeIGC Hull = new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 535f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.666f, 4.444f, 5.022f],
        TurnTorques: [5100f, 4900f, 4700f],
        Thrust: 52800f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 950f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: RipcordSeconds, RipcordCost: 0f, MaxAmmo: 0, HullId: HullId, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 515f, HardpointOffset: 563,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static void Set(object target, string name, object? value) =>
        (target.GetType().GetField(name, Hidden) ?? throw new MissingFieldException(name)).SetValue(target, value);
    private static T Get<T>(object target, string name) =>
        (T)(target.GetType().GetField(name, Hidden) ?? throw new MissingFieldException(name)).GetValue(target)!;
    private static object? Call(Game1 game, string name, params object?[] args) =>
        (typeof(Game1).GetMethod(name, Hidden) ?? throw new MissingMethodException(name)).Invoke(game, args);

    private static ServerShipUpdate Pose(short sector = 1) =>
        ServerShipUpdate.FromValues(
            Me, stateM: 0, new ControlData(0f, 0f, 0f, 1f), time: 1u,
            new Vector3(100f, 0f, 0f), Orientation.Identity, Orientation.Identity.GetForward() * 80f,
            turnRates: (float[])SpinRates.Clone(),
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f);

    /// <summary>An uninitialised Game1 wired with exactly the members the two
    /// production methods read, plus a flying own ship of a hull whose
    /// ripcord countdown is <see cref="RipcordSeconds"/>.</summary>
    private static (Game1 Game, ConnectionSession Session, ClientWorld World) Headless()
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        var session = new ConnectionSession();
        var world = session.World;
        world.Apply(new SetCluster { SectorId = 1, Ship = Pose() });
        world.Apply(new ShipStatusMessage
        {
            ShipId = Me, HullId = HullId, SectorId = 1, StationId = -1, ParentId = -1,
            State = ShipStatusMessage.StateFlying,
        });
        Set(game, "_session", session);
        Set(game, "_shopCatalog", new ShopCatalog([Hull], []));
        Set(game, "_ripcordCountdownSound", new Audio.RipcordCountdownSound());
        Set(game, "_ownSim", new OwnShipSimulator());
        Set(game, "_flashTracker", new Flight.DamageFlashTracker());
        Set(game, "_ownSimHullId", (short)-1);
        Set(game, "_ownSimLastSectorId", (short)-1);
        return (game, session, world);
    }

    /// <summary>Activate the own ripcord as if the RIPCORD_ACTIVATE arrived
    /// <paramref name="secondsAgo"/> seconds ago on the client clock
    /// (Game1.ClientNowMilliseconds = Environment.TickCount64 outside a replay).</summary>
    private static void ActivateOwn(ClientWorld world, double secondsAgo)
    {
        world.NowSeconds = () => Environment.TickCount64 / 1000.0 - secondsAgo;
        world.Apply(new RipcordActivate
        {
            ShipidRipcord = Me, OtRipcord = (short)IgcObjectType.Station, OidRipcord = 100, SidRipcord = 2,
        });
        world.NowSeconds = () => Environment.TickCount64 / 1000.0;
    }

    private static void Frame(Game1 game, ClientWorld world)
    {
        var mine = world.Ships[Me];
        Call(game, "UpdateOwnShipSimulation", world, mine, mine.Latest!);
    }

    // ── the 60 Hz emission clock (deliberate deviation from per-frame) ──────

    private static (Game1 Game, WarpTransitParticles Pool) EmitterGame()
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        var pools = new FlightEffectPools();
        Set(game, "_flightEffects", pools);
        Set(game, "_trailRandom", new RandomTrailSource(781));
        var emitters = new TeleportEmitter[64];
        emitters[0] = new TeleportEmitter(7, new Vector3(0f, 0f, 0f), new Vector3(50f, 0f, 0f),
            new Vector3(0f, 0f, -1f), new Vector3(1f, 0f, 0f), Radius: 10f, TimeUntilTeleport: 0f);
        Set(game, "_teleportEmitters", emitters);
        Set(game, "_teleportEmitterCount", 1);
        return (game, pools.Warp);
    }

    [Theory]
    [InlineData(144)]
    [InlineData(60)]
    [InlineData(30)]
    public void Lead_up_density_is_the_native_60_fps_density_at_any_frame_rate(int fps)
    {
        // One full-burst emitter (t = 0 → 9 sparks per native call) for 0.25 s
        // of frames, no aging: 15 emission ticks x 9 sparks whatever the fps.
        var (game, pool) = EmitterGame();
        int frames = fps / 4;
        for (int i = 0; i < frames; i++)
            Call(game, "EmitTeleportLeadup", 1f / fps);
        Assert.InRange(pool.ActiveCount, 14 * 9, 15 * 9);
    }

    [Fact]
    public void A_long_hitch_emits_at_most_four_ticks_never_a_burst()
    {
        var (game, pool) = EmitterGame();
        Call(game, "EmitTeleportLeadup", 2.0f);        // a 2 s stall
        Assert.Equal(4 * 9, pool.ActiveCount);
    }

    // ── the HUD self-expiry no longer ends the ripcord state at 0 ────────────

    [Fact]
    public void Hud_label_drops_at_zero_but_the_ripcord_state_survives_one_second_past_it()
    {
        var (game, _, world) = Headless();
        ActivateOwn(world, secondsAgo: RipcordSeconds + 1.0);      // left = −1 s

        Assert.Null(Call(game, "RipcordStatusLabel"));            // "Ripcord in: n" is gone at 0
        // Native pins the countdown at 0 and keeps the model (frozen flight)
        // until the server moves the ship (shipIGC.cpp:286-294); the pre-P781
        // HUD cleared the whole state here.
        Assert.True(world.RipcordActive, "ripcord state must survive the countdown's zero (native pin)");
    }

    [Fact]
    public void Hud_safety_expiry_clears_the_state_after_the_two_second_grace()
    {
        var (game, _, world) = Headless();
        ActivateOwn(world, secondsAgo: RipcordSeconds + 2.1);      // left = −2.1 s

        Assert.Null(Call(game, "RipcordStatusLabel"));
        Assert.False(world.RipcordActive, "the port-only 2 s safety expiry must clear a teleport that never arrived");
    }

    [Fact]
    public void Hud_label_still_counts_down_before_zero()
    {
        var (game, _, world) = Headless();
        ActivateOwn(world, secondsAgo: 0.5);                        // left = 2.5 s
        Assert.Equal("Ripcord in: 3", Call(game, "RipcordStatusLabel"));   // native (int)(left + 1.0)
        Assert.True(world.RipcordActive);
    }

    // ── the own ship keeps flying the frozen model (no freeze, no snap) ──────

    [Fact]
    public void Own_ship_is_not_frozen_and_keeps_spinning_at_its_activation_rates_during_the_countdown()
    {
        var (game, _, world) = Headless();
        Frame(game, world);                                         // first sight: seed + hull
        var sim = Get<OwnShipSimulator>(game, "_ownSim");
        Assert.True(sim.HaveHull);
        Assert.False(sim.Frozen);

        ActivateOwn(world, secondsAgo: 0.0);
        Frame(game, world);                                         // the activation frame
        Assert.False(sim.Frozen, "a ripcording ship must keep flying (pre-P781: frozen at the server pose)");

        var before = sim.Orientation.GetForward();
        float[] ratesAtActivation = sim.TurnRates;   // the wire-quantised seed (~0.79, -0.30, 0.50)
        Assert.True(MathF.Abs(ratesAtActivation[0]) > 0.5f);
        float speedBefore = sim.Velocity.Length();
        var fullStick = new ControlData(Yaw: -1f, Pitch: 1f, Roll: -1f, Throttle: 1f);
        for (int i = 0; i < 20; i++)
        {
            sim.Step(0.05f, fullStick, 0);
            Frame(game, world);
        }
        var after = sim.Orientation.GetForward();
        float turnedDeg = MathF.Acos(Math.Clamp(before * after, -1f, 1f)) * 180f / MathF.PI;
        Assert.True(turnedDeg > 10f, $"the own pose must keep rotating during the countdown (turned {turnedDeg:0.0} deg)");
        // Frozen rates (no ramp toward the opposite full stick) and drag-only speed.
        Assert.Equal(ratesAtActivation[0], sim.TurnRates[0], 5);
        Assert.Equal(ratesAtActivation[1], sim.TurnRates[1], 5);
        Assert.Equal(ratesAtActivation[2], sim.TurnRates[2], 5);
        Assert.True(sim.Velocity.Length() < speedBefore, "thrust is off while ripcording: the ship coasts down");
    }

    [Fact]
    public void Own_sector_change_ends_the_ripcord_state_like_native_SetCluster()
    {
        var (game, _, world) = Headless();
        Frame(game, world);
        ActivateOwn(world, secondsAgo: 0.5);
        Frame(game, world);
        Assert.True(world.RipcordActive);

        // The port's ripcord teleport sends the owner no SET_CLUSTER — the client
        // learns the new sector from SHIP_STATUS. Native SetCluster drops the
        // model (shipIGC.h:200-210), so the landed ship must fly normally again.
        world.Apply(new ShipStatusMessage
        {
            ShipId = Me, HullId = HullId, SectorId = 2, StationId = -1, ParentId = -1,
            State = ShipStatusMessage.StateFlying, StateTime = 10,
        });
        Frame(game, world);
        Assert.False(world.RipcordActive, "the own sector change must clear the ripcord (frozen-flight) state");
    }
}
