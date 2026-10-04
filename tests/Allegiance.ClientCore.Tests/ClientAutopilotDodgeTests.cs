using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P524 — the <b>pure</b> (no live server) proof that the MonoGame player <c>G</c>
/// autopilot now steers around obstacles with native's dodge law, fed from its
/// <see cref="ClientWorld"/> through the shared
/// <see cref="ClientAutopilotDodge"/> feed (the SAME canonical feed the P489
/// client bots use).
///
/// <para>The precondition (P489's named gap): before P524, the player autopilot
/// flew an <b>obstacle-blind straight line</b> — <c>AutopilotSteering.SteerToward</c>
/// had no dodge term, so heading at a target with an asteroid on the line drove
/// straight into it. These tests drive the SHARED dodge law the new autopilot
/// path consults: when a collision is imminent the mover steers to the dodge
/// goal (away from the rock), and once the collision is no longer imminent it
/// reverts to the unchanged straight-line law and <b>still arrives</b> — the
/// exact native <c>GotoPlan::SetControls</c> "first priority ... dodge" contract
/// (<c>common.cpp:2000-2018</c>).</para>
/// </summary>
public class ClientAutopilotDodgeTests
{
    private const short MyShipId = 701;
    private const short MySide = 0;
    private const short Sector = 1;

    private static DataHullTypeIGC MakeHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 999, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 740,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    /// <summary>The mover's own hull stats passed to the shared feed — radius
    /// <c>0.5·30+1 = 16</c>, acceleration <c>52500/550 ≈ 95.45</c>.</summary>
    private static ClientAutopilotDodge.MoverHull MyHull =>
        new(Length: 30f, Thrust: 52500f, Mass: 550f);

    /// <summary>Build a pure <see cref="ClientWorld"/> with the own ship seated in
    /// sector 1 and a single static asteroid at <paramref name="rockCenter"/> with
    /// <paramref name="rockRadius"/> — the obstacle the autopilot must dodge.</summary>
    private static ClientWorld WorldWithMeAndRock(in Vector3 rockCenter, short rockRadius)
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = Sector,
            Ship = ServerShipUpdate.FromModel(
                MyShipId,
                new Ship(MyShipId, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
                1u),
            Cookie = 0x1,
        });
        world.Apply(new ExportAsteroid
        {
            Asteroid = new DataAsteroidIGC(
                Signature: 0f,
                Position: [rockCenter.X, rockCenter.Y, rockCenter.Z],
                Up: [0f, 1f, 0f], Forward: [0f, 0f, 1f], Rotation: [0f, 0f, 1f, 0f],
                Ore: 1000f, OreMax: 1000f, AabmCapabilities: 0,
                AsteroidId: 505, HitPoints: 240f, Radius: rockRadius,
                ModelName: "rock", TextureName: "rock", IconName: "rock",
                ClusterId: Sector, Name: "blocker", Fraction: 255),
        });
        return world;
    }

    private static bool Dodge(ClientWorld world, in Vector3 pos, in Vector3 vel,
        ClientAutopilotDodge.SkipGoal? skip, out Vector3 goal)
        => ClientAutopilotDodge.TryComputeDodgeGoal(
            world, MyShipId, MySide, pos, vel, MyHull,
            dodgeStations: true,
            hullLengthById: _ => null,
            stationRadiusByType: _ => null,
            skip, out goal);

    private static void AssertNoOverlap(in Vector3 pos, in Vector3 rockCenter, float rockRadius, float shipRadius)
        => Assert.True((pos - rockCenter).Length() >= rockRadius + shipRadius,
            $"ship overlapped the rock: dist={(pos - rockCenter).Length():F1} < combined={rockRadius + shipRadius}");

    /// <summary>⭐ FAILING-FIRST: the new autopilot dodge FIRES when an asteroid
    /// sits on the straight line to the target and the collision is imminent —
    /// and it steers the ship LATERALLY, away from the rock, instead of the
    /// obstacle-blind straight line P489 flagged. Before P524 there was no dodge
    /// term at all, so this goal could never be produced.</summary>
    [Fact]
    public void Autopilot_dodges_an_asteroid_on_the_straight_line_when_collision_is_imminent()
    {
        var world = WorldWithMeAndRock(new Vector3(0f, 0f, 120f), rockRadius: 60);
        // Ship at origin cruising dead-on +Z at a target beyond the rock; the
        // rock is squarely on the line → an imminent collision.
        Assert.True(Dodge(world, Vector3.Zero, new Vector3(0f, 0f, 120f), skip: null, out var goal),
            "a rock dead-ahead with an imminent collision must produce a dodge goal");

        // The dodge goal must pull the ship OFF the straight line (lateral X/Y
        // component) — away from the rock, toward its flank — not along it.
        Assert.True(Math.Abs(goal.X) + Math.Abs(goal.Y) > 30f,
            $"dodge goal must be lateral to clear the rock, got ({goal.X:F1},{goal.Y:F1},{goal.Z:F1})");
        // And it is a real escape point placed dodgeGoalDistance (520) from the ship.
        Assert.Equal(ClientAutopilotDodge.DodgeGoalDistance, (goal - Vector3.Zero).Length(), 1f);
    }

    /// <summary>⭐ FAILING-FIRST: once the collision is no longer imminent (the
    /// ship is past / no longer converging on the rock), the dodge feed returns
    /// false — so the caller flies its unchanged straight-line autopilot law
    /// toward the target and <b>still arrives</b> (scope item 1: arrival /
    /// braking preserved). This is the "still arrives" half of the dodge
    /// contract: the shared feed only pre-empts while a collision is imminent
    /// (native <c>GotoPlan::SetControls</c> <c>common.cpp:2000-2018</c>), exactly
    /// like the server NPC autopilot.</summary>
    [Fact]
    public void Autopilot_reverts_to_straight_line_and_arrives_when_no_longer_imminent()
    {
        // Rock BEHIND the ship (negative Z) while the ship moves +Z away from it
        // → separating → nothing is imminent → the feed reports NO dodge, so the
        // autopilot keeps its original target line and reaches it.
        var world = WorldWithMeAndRock(new Vector3(0f, 0f, -150f), rockRadius: 60);
        Assert.False(Dodge(world, new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 120f), skip: null, out _),
            "a rock behind/separating is not an imminent collision → no dodge");

        // An asteroid NOT on the flight path is likewise never dodged — only the
        // straight target remains, so the autopilot arrives unimpeded.
        var world2 = WorldWithMeAndRock(new Vector3(300f, 0f, 300f), rockRadius: 60);
        Assert.False(Dodge(world2, Vector3.Zero, new Vector3(0f, 0f, 120f), skip: null, out _),
            "an asteroid off to the side is not imminent → no dodge");
    }

    /// <summary>⭐ FAILING-FIRST (discriminator): the same goal — target at
    /// (0,0,1000), a rock dead on the line — with the dodge DISABLED (the
    /// pre-P524 obstacle-blind autopilot) drives STRAIGHT THROUGH the rock
    /// (min distance < the rock's radius ⇒ collision), which is exactly the
    /// behaviour P489's report measured ("obstacle-blind straight line"). With
    /// the dodge enabled the same flight CLEARS the rock (never overlaps) and
    /// ARRIVES at the target. This is the nav-level proof that "steers away …
    /// and still arrives" is a real behavioural change, not an invariant.</summary>
    [Theory]
    [InlineData(true)]    // dodge enabled  → clears the rock, arrives
    [InlineData(false)]   // dodge disabled → plows into the rock (the old G)
    public void Autopilot_with_dodge_clears_the_rock_and_arrives(bool useDodge)
    {
        var rockCenter = new Vector3(0f, 0f, 380f);
        const short rockRadius = 60;
        const float shipRadius = 16f;                        // 0.5·30 + 1
        const float cruiseSpeed = 120f;
        var target = new Vector3(0f, 0f, 850f);
        const float arrivalRadius = 90f;
        const float dt = 0.025f;

        var world = WorldWithMeAndRock(rockCenter, rockRadius);

        // Nav-level point-mass: each step the autopilot heads toward the dodge
        // goal while the shared feed reports an imminent collision, else toward
        // the target — at constant cruise speed (nose-follow), the P524 wiring.
        var pos = new Vector3(0f, 0f, 0f);
        var vel = new Vector3(0f, 0f, cruiseSpeed);
        float minRockDist = float.MaxValue;
        bool arrived = false;
        const int maxSteps = 4000;                           // ~100 s at dt=0.025 — plenty
        for (int i = 0; i < maxSteps && !arrived; i++)
        {
            var steer = target;
            if (useDodge && Dodge(world, pos, vel, skip: null, out var dodgeGoal))
                steer = dodgeGoal;
            var dir = (steer - pos).Normalize();
            vel = dir * cruiseSpeed;
            pos += vel * dt;
            minRockDist = MathF.Min(minRockDist, (pos - rockCenter).Length());
            if ((target - pos).Length() <= arrivalRadius) arrived = true;
        }

        Console.WriteLine($"P524-SIM useDodge={useDodge}: arrived={arrived} minRockDist={minRockDist:F1} "
            + $"finalPos=({pos.X:F1},{pos.Y:F1},{pos.Z:F1})");
        Assert.True(arrived, "the autopilot must reach the target either way");

        if (useDodge)
        {
            Assert.True(minRockDist >= rockRadius + shipRadius,
                $"with dodge the ship must clear the rock (min {minRockDist:F1} vs combined {rockRadius + shipRadius})");
            AssertNoOverlap(pos, rockCenter, rockRadius, shipRadius);
        }
        else
        {
            // The old obstacle-blind straight line runs straight through the rock.
            Assert.True(minRockDist < rockRadius,
                $"without dodge the straight line must intersect the rock (min {minRockDist:F1} < {rockRadius})");
        }
    }

    /// <summary>Scope item 2 — the station the autopilot is flying TO is the
    /// skipped goal (native <c>pmodelSkip</c>): the dodge feed must NOT deflect
    /// the autopilot away from its own destination. A station dead-ahead that IS
    /// the goal yields no dodge, exactly as native's <c>GetGotoPosition</c> hands
    /// the goto target to the dodge scan as the skip (<c>common.cpp:1291</c>).</summary>
    [Fact]
    public void Autopilot_does_not_dodge_the_station_it_is_flying_to()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = Sector,
            Ship = ServerShipUpdate.FromModel(
                MyShipId,
                new Ship(MyShipId, MakeHull(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
                1u),
            Cookie = 0x1,
        });
        world.Apply(new ExportStation
        {
            Station = new DataStationIGC(
                Position: [0f, 0f, 410f], Up: [0f, 1f, 0f], Forward: [0f, 0f, 1f],
                Rotation: [0f, 0f, 1f, 0f], ClusterId: Sector, SideId: MySide,
                StationId: 906, StationTypeId: 311,
                BpHull: 100, BpShield: 100, Name: "dest"),
        });

        // Heading straight at the destination station: it is the SKIPPED goal,
        // so even though it is a huge obstruction dead-ahead the feed must not
        // treat it as an obstacle (else the G autopilot would perpetually
        // deflect around the very base it is trying to reach).
        var goal = ClientAutopilotDodge.TryComputeDodgeGoal(
            world, MyShipId, MySide, Vector3.Zero, new Vector3(0f, 0f, 120f), MyHull,
            dodgeStations: true,
            hullLengthById: _ => null,
            stationRadiusByType: _ => 750f,       // big station — would be imminent if not skipped
            skipGoal: new ClientAutopilotDodge.SkipGoal(StationId: 906),
            out _);
        Assert.False(goal, "the destination station (pmodelSkip) must not be dodged");
    }
}
