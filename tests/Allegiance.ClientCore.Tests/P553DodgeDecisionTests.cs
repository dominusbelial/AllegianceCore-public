using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.State;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P553 — the failing-first proof that the shared native dodge law dodges a
/// CROSS-SIDE enemy ship in combat (the measured root cause of the melee
/// ship-ship rams). Before P553 the shared feed
/// (<see cref="ClientAutopilotDodge.EnumerateObstacles"/>) marked every
/// different-side ship as <c>Ignore</c>, so a combat bot could never dodge the
/// enemy it was about to ram — the 5 "rock" collision deaths P539d reported in
/// Alderan were actually ship-ship rams (this task's live measurement). Native
/// players DO dodge enemy ships: the "trucker rules of road" ignore of a
/// different-side ship applies only to a DRONE pilot
/// (<c>pship-&gt;GetPilotType() &lt; c_ptPlayer</c>, <c>common.cpp:1679-1680</c>),
/// and the movers this feed serves (MCP player bots, the P524 player autopilot)
/// are player-type ships.
///
/// Pure (no live server), driving the SHARED feed exactly as the bot's
/// <c>TryDodgeGoal</c> does (asteroids + stations + known ships in the mover's
/// own cluster).
/// </summary>
public class P553DodgeDecisionTests
{
    private const short MyShipId = 7201;
    private const short MySide = 0;
    private const short EnemySide = 1;
    private const short Sector = 1;
    private const short EnemyShipId = 8111;
    private const short EnemyHullId = 777;

    private static DataHullTypeIGC MakeHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "fighter",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 470f, Signature: 0f, Speed: 130f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 51500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 778, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 514f, HardpointOffset: 714,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static ClientAutopilotDodge.MoverHull MyHull =>
        new(Length: 30f, Thrust: 51600f, Mass: 470f);

    /// <summary>Build a world with the own ship seated at the origin in sector 1
    /// and a CROSS-SIDE ship at <paramref name="enemyCenter"/> with the given
    /// velocity. The enemy gets its identity (HullId via EXPORT) and its live
    /// pose + same-cluster SectorId (via a 176 heavy update, which stamps every
    /// entry's SectorId to the local ship's sector — the P375 rule).</summary>
    private static ClientWorld WorldWithMeAndEnemy(in Vector3 enemyCenter, in Vector3 enemyVel)
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
        world.Apply(new ExportShip
        {
            HullId = EnemyHullId,
            ShipId = EnemyShipId,
            SideId = EnemySide,
            CharacterName = "enemy",
        });
        var enemy = new Ship(EnemyShipId, MakeHull(), enemyCenter, enemyVel, Orientation.Identity);
        world.Apply(new HeavyShipsUpdate
        {
            TimeReference = 1u,
            PositionReference = Vector3.Zero,
            HeavyShipEntries = [HeavyShipUpdateEntry.From(1u, Vector3.Zero, enemy, 1u)],
        });
        return world;
    }

    private static bool Dodge(ClientWorld world, in Vector3 pos, in Vector3 vel,
        ClientAutopilotDodge.SkipGoal? skip, out Vector3 goal)
        => ClientAutopilotDodge.TryComputeDodgeGoal(
            world, MyShipId, MySide, pos, vel, MyHull,
            dodgeStations: true,
            hullLengthById: _ => null,        // unresolvable hull → UnknownHullLength fallback (radius 15)
            stationRadiusByType: _ => null,
            skip, out goal);

    /// <summary>⭐ FAILING-FIRST (the core P553 root-cause gate): a CROSS-SIDE
    /// enemy ship dead-ahead with an imminent collision is a dodge obstacle.
    /// Before P553 the shared feed marked the enemy <c>Ignore</c>, so this
    /// returned <c>false</c> — the bot rammed the enemy and died (the measured
    /// ship-ship collision deaths in Alderan). After the fix the feed returns
    /// a dodge goal that pulls the bot off the collision line.</summary>
    [Fact]
    public void Dodge_avoids_a_cross_side_enemy_ship()
    {
        // Enemy dead-ahead on the +Z line at 120 u, mover cruising +Z at 120 m/s
        // → closing contact in ~0.9 s (well inside the imminence gate
        // t² ≤ 4·r/accel). A player must dodge it.
        var world = WorldWithMeAndEnemy(new Vector3(0f, 0f, 120f), Vector3.Zero);
        Assert.True(Dodge(world, Vector3.Zero, new Vector3(0f, 0f, 120f), skip: null, out var goal),
            "a cross-side enemy dead-ahead with an imminent collision must be dodged");

        // The dodge goal must pull the mover OFF the collision line (lateral X/Y).
        Assert.True(Math.Abs(goal.X) + Math.Abs(goal.Y) > 10f,
            $"dodge goal must be lateral to clear the enemy, got ({goal.X:F1},{goal.Y:F1},{goal.Z:F1})");
    }

    /// <summary>⭐ FAILING-FIRST (closed-loop, the "a combat bot steers around the
    /// enemy instead of ramming" proof): heading at a stand-off point with a
    /// cross-side enemy on the line, the shared dodge (fed from the mover's
    /// world) steers the mover around the enemy and still arrives — min distance
    /// to the enemy never overlaps its combined sphere. With the pre-P553
    /// enemy-ignored feed the same flight plows straight through the enemy.
    /// Native: a player dodge pre-empts the straight-line goto goal
    /// (<c>common.cpp:2000-2018</c>).</summary>
    [Theory]
    [InlineData(true)]     // dodge-enemies (fixed)  → clears the enemy, arrives
    [InlineData(false)]    // enemy-ignored (broken) → rams through it
    public void Combat_approach_clears_cross_side_enemy_instead_of_ramming(bool dodgeEnemies)
    {
        var enemyCenter = new Vector3(0f, 0f, 380f);
        const float enemyRadius = 15f;                    // 0.5·UnknownHullLength(30)
        const float shipRadius = 16f;                     // 0.5·30 + 1 (mover)
        const float combined = enemyRadius + shipRadius;
        const float cruiseSpeed = 120f;
        var target = new Vector3(0f, 0f, 820f);           // stand-off point beyond the enemy
        const float arrivalRadius = 80f;
        const float dt = 0.012f;

        var world = WorldWithMeAndEnemy(enemyCenter, Vector3.Zero);

        var pos = new Vector3(0f, 0f, 0f);
        var vel = new Vector3(0f, 0f, cruiseSpeed);
        float minDist = float.MaxValue;
        bool arrived = false;
        const int maxSteps = 6000;
        for (int i = 0; i < maxSteps && !arrived; i++)
        {
            var steer = target;
            // "dodge-enemies": consult the feed; "enemy-ignored": bypass the feed
            // entirely (equivalent to the pre-P553 feed that never returned a goal
            // for the enemy), so the mover flies the obstacle-blind line.
            if (dodgeEnemies && Dodge(world, pos, vel, skip: null, out var dodgeGoal))
                steer = dodgeGoal;
            var dir = (steer - pos).Normalize();
            vel = dir * cruiseSpeed;
            pos += vel * dt;
            minDist = MathF.Min(minDist, (pos - enemyCenter).Length());
            if ((target - pos).Length() <= arrivalRadius) arrived = true;
        }

        Console.WriteLine($"P553-SIM dodgeEnemies={dodgeEnemies}: arrived={arrived} minDist={minDist:F1} combined={combined:F1}");
        Assert.True(arrived, "the combat approach must reach the stand-off point either way");
        if (dodgeEnemies)
        {
            Assert.True(minDist >= combined,
                $"with the fixed feed the approach must clear the enemy (min {minDist:F1} vs combined {combined:F1})");
        }
        else
        {
            Assert.True(minDist < combined,
                $"without enemy-dodging the approach must intersect the enemy (min {minDist:F1} < {combined:F1})");
        }
    }

    /// <summary>Guard: the mover NEVER dodges itself, and a ship explicitly passed
    /// as the navigation skip (<c>pmodelSkip</c> — the ship it is flying to) is
    /// never dodged either. These must stay true after the cross-side change.</summary>
    [Fact]
    public void Dodge_never_considers_self_or_the_skipped_goal_ship()
    {
        // Self at origin; the enemy is the mover's navigation GOAL (SkipGoal).
        var world = WorldWithMeAndEnemy(new Vector3(0f, 0f, 120f), Vector3.Zero);
        Assert.False(
            Dodge(world, Vector3.Zero, new Vector3(0f, 0f, 120f),
                skip: new ClientAutopilotDodge.SkipGoal(ShipId: EnemyShipId), out _),
            "the ship the mover is navigating to (pmodelSkip) must not be dodged");
    }

    /// <summary>⭐ FAILING-FIRST: "when the dodge fires during combat, the dodge
    /// owns steering and throttle for that tick and the bot keeps firing only if
    /// native does" — the AllWeapons fire bit is cleared for the dodging tick,
    /// while the boost bit (afterburner) and every other state bit are kept.
    /// Before P553 the bot kept firing while evading (native suppresses fire
    /// during the dodge, shipIGC.cpp:2410/2485-2490).</summary>
    [Fact]
    public void Dodge_owns_the_combat_tick_and_suppresses_fire()
    {
        int firingAndBoosting = (int)ShipControlState.AllWeapons | (int)ShipControlState.AfterburnerButton;
        int result = BotManager.CombatStateWhileDodging(firingAndBoosting);
        Assert.True((result & (int)ShipControlState.AllWeapons) == 0, "fire must be suppressed while dodging");
        Assert.True((result & (int)ShipControlState.AfterburnerButton) != 0, "boost is kept while dodging");
        // Already-disarmed stays disarmed.
        Assert.True((BotManager.CombatStateWhileDodging(0) & (int)ShipControlState.AllWeapons) == 0);
    }

    /// <summary>Closed-loop: a combat bot with a ROCK on its stand-off path steers
    /// around it (rocks were always dodged — this guards the P489 contract against
    /// the cross-side change). The mover clears the rock and still reaches the
    /// stand-off point.</summary>
    [Fact]
    public void Combat_approach_clears_a_rock_on_the_stand_off_path()
    {
        var world = WorldWithMeAndRock(new Vector3(0f, 0f, 380f), rockRadius: 60);
        const float shipRadius = 16f;
        const float combined = 60f + shipRadius;
        const float cruiseSpeed = 120f;
        var target = new Vector3(0f, 0f, 830f);
        const float arrivalRadius = 90f;
        const float dt = 0.010f;

        var pos = new Vector3(0f, 0f, 0f);
        var vel = new Vector3(0f, 0f, cruiseSpeed);
        float minDist = float.MaxValue;
        bool arrived = false;
        for (int i = 0; i < 8000 && !arrived; i++)
        {
            var steer = target;
            if (Dodge(world, pos, vel, skip: null, out var dodgeGoal)) steer = dodgeGoal;
            var dir = (steer - pos).Normalize();
            vel = dir * cruiseSpeed;
            pos += vel * dt;
            minDist = MathF.Min(minDist, (pos - new Vector3(0f, 0f, 380f)).Length());
            if ((target - pos).Length() <= arrivalRadius) arrived = true;
        }
        Console.WriteLine($"P553-ROCK-SIM arrived={arrived} minDist={minDist:F1} combined={combined:F1}");
        Assert.True(arrived, "the combat approach must reach the stand-off point");
        Assert.True(minDist >= combined,
            $"the rock on the stand-off path must be cleared (min {minDist:F1} vs combined {combined:F1})");
    }

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
                AsteroidId: 506, HitPoints: 240f, Radius: rockRadius,
                ModelName: "rock", TextureName: "rock", IconName: "rock",
                ClusterId: Sector, Name: "blocker", Fraction: 255),
        });
        return world;
    }
}
