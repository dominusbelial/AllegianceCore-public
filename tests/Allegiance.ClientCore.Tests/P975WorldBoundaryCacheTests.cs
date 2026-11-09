using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P975 (P971-03) — per-ship interpolation / render caches must retire with the
/// world they were built for.
///
/// Before the fix, <c>ClientWorld</c>'s <c>_remoteReckoners</c> and
/// <c>_remoteHermite</c> maps were removed only on individual ship departures and
/// survived <c>ResetCore</c>, <c>BeginNextMission</c> and <c>ClearTeamWorld</c>; the
/// Game1 <c>_shipRadiusById</c>/<c>_shipSideRgbById</c> maps were never cleared at
/// all. Rotating through matches with fresh ids grew them without bound, and a
/// reused id could render from the previous match's interpolation state or side
/// colour.
///
/// Native has no such caches to leak: a mission rotation destroys the client's
/// mission objects (entities, and the per-ship simulation state hanging off them)
/// wholesale — <c>clintlib.cpp:4193-4224</c> (OnQuitMission / BeginNewMission
/// rebuilds the world from the server's re-burst), and the interpolation itself is
/// per-frame/per-ship state inside the mission's ships, not a client-side id map.
///
/// These tests are behavioural (public API only): after each world boundary a
/// retired ship id must have NO interpolation state, and repeated rotations with
/// changing ids must not accumulate entries.
/// </summary>
public class P975WorldBoundaryCacheTests
{
    // Same synthetic pinned hull family the P448b/P813b tests use, so the flight
    // numbers are deterministic and no artwork corpus is needed.
    private static readonly DataHullTypeIGC Hull = new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 335f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.566f, 4.344f, 4.976f],
        TurnTorques: [5240f, 5010f, 4860f],
        Thrust: 49600f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 870f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 701, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 510f, HardpointOffset: 571,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static ClientWorld NewWorld(out Func<double> clock)
    {
        double t = 0.0;
        clock = () => t;
        var world = new ClientWorld { ReckonRemotes = true, HermiteRemotes = true };
        world.NowSeconds = () => t;
        world.RenderNowSeconds = () => t;
        world.RemoteShipHullResolver = (_, _) => (ShipDeadReckoner.HullStats.FromHull(Hull), 0f);
        return world;
    }

    private static Ship FlyingShip(short id, Vector3 position)
    {
        var o = Orientation.Identity;
        o.Yaw(0.088f);
        o.Renormalize();
        var s = new Ship(id, Hull, position, o.GetForward() * 60f, o);
        s.Controls = new ControlData(Yaw: 0.04f, Pitch: -0.03f, Roll: 0.02f, Throttle: 0.62f);
        return s;
    }

    private static void Seed(ClientWorld world, short id, uint tick, Vector3? position = null)
        => world.Apply(new SingleShipUpdate
        {
            OidTarget = id,
            Ship = ServerShipUpdate.FromModel(id, FlyingShip(id, position ?? new Vector3(id * 10f, 20f, 30f)), tick),
        });

    /// <summary>Count a per-ship cache: the private field before this task's fix,
    /// the world-owned container property after it (both are dictionaries with
    /// <c>Count</c>).</summary>
    private static int CacheCount(ClientWorld world, params string[] members)
    {
        foreach (var name in members)
        {
            var f = typeof(ClientWorld).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            object? v = f?.GetValue(world);
            v ??= typeof(ClientWorld).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(world);
            if (v is null) continue;
            if (v.GetType().GetProperty("Count")?.GetValue(v) is int n) return n;
        }
        throw new InvalidOperationException("no per-ship cache member named " + string.Join(" / ", members));
    }

    private static int ReckonerCount(ClientWorld w) => CacheCount(w, "_remoteReckoners");
    private static int HermiteCount(ClientWorld w) => CacheCount(w, "_remoteHermite");

    // ── tests ──────────────────────────────────────────────────────────────

    [Fact]
    public void PerShipInterpolationCaches_AreRetired_AtAllThreeWorldBoundaries()
    {
        var world = NewWorld(out _);
        foreach (short id in new short[] { 101, 102, 103 }) Seed(world, id, 520000);
        Assert.Equal(3, world.Ships.Count);
        Assert.True(ReckonerCount(world) >= 3, "three remote ships must have seeded dead-reckoners");
        Assert.True(HermiteCount(world) >= 3, "three remote ships must have seeded Hermite histories");

        // Boundary 1 — a fresh connection (ResetCore).
        world.Reset();
        Assert.Empty(world.Ships);
        Assert.Equal(0, ReckonerCount(world));      // P971-03: survived ResetCore before
        Assert.Equal(0, HermiteCount(world));

        // Boundary 2 — a mission rotation (BeginNextMission).
        foreach (short id in new short[] { 201, 202, 203 }) Seed(world, id, 520100);
        Assert.True(ReckonerCount(world) >= 3);
        world.BeginNextMission();
        Assert.Empty(world.Ships);
        Assert.Equal(0, ReckonerCount(world));      // P971-03: survived BeginNextMission before
        Assert.Equal(0, HermiteCount(world));

        // Boundary 3 — leave to the team lobby (ClearTeamWorld via own QUIT_SIDE).
        Seed(world, 301, 520200);
        world.Apply(new SetCluster { SectorId = 1, Cookie = 1, Ship = ServerShipUpdate.FromModel(301, FlyingShip(301, new Vector3(0f, 0f, 100f)), 520200) });
        Assert.Equal((short)301, world.MyShipId!.Value);
        world.Apply(new JoinSide { ShipId = 301, SideId = 0 });
        Seed(world, 302, 520201);
        Seed(world, 303, 520202);
        Assert.True(ReckonerCount(world) >= 2, $"expected the two remote ships' reckoners, saw {ReckonerCount(world)}");
        world.Apply(new QuitSide { ShipId = 301, Reason = QuitSideReason.SwitchingSides });
        Assert.Equal(0, ReckonerCount(world));      // P971-03: survived ClearTeamWorld before
        Assert.Equal(0, HermiteCount(world));
    }

    [Fact]
    public void RepeatedRotationsWithChangingIds_KeepTheCacheCountsBounded()
    {
        var world = NewWorld(out _);
        for (int round = 0; round < 6; round++)
        {
            for (short i = 0; i < 4; i++)
                Seed(world, (short)(200 + round * 10 + i), (uint)(520000 + round * 4 + i));
            Assert.Equal(4, world.Ships.Count);
            Assert.True(ReckonerCount(world) <= 4,
                $"round {round}: {ReckonerCount(world)} reckoner entries for 4 live ships — entries accumulate across matches");
            Assert.True(HermiteCount(world) <= 4,
                $"round {round}: {HermiteCount(world)} Hermite entries for 4 live ships — entries accumulate across matches");
            world.BeginNextMission();
            Assert.Equal(0, ReckonerCount(world));
        }
    }

    [Fact]
    public void ReusedShipId_GetsNoStaleInterpolationAfterABoundary()
    {
        var world = NewWorld(out var clock);
        var oldPos = new Vector3(1000f, 2000f, 3000f);
        Seed(world, 7, 520000, oldPos);
        Assert.True(world.TryGetRenderPose(7, clock(), out var stale, out _));
        Assert.True((stale - oldPos).Length() < 50f, "the seeded reckoner starts from its own authoritative pose");

        world.BeginNextMission();

        // P971-03: the next match's ship 7 must not inherit the previous match's
        // interpolation state — before the fix this returned the retired pose.
        Assert.False(world.TryGetRenderPose(7, clock(), out _, out _),
            "a reused ship id resolved a dead-reckoner from the retired match");
        Assert.False(world.TryGetHermiteRenderPose(7, clock(), out _, out _),
            "a reused ship id resolved a Hermite history from the retired match");

        // A fresh authoritative pose replaces the state wholesale.
        Seed(world, 7, 520900, new Vector3(-900f, -40f, 12f));
        Assert.True(world.TryGetRenderPose(7, clock(), out var fresh, out _));
        Assert.True(fresh.X < 0f && fresh.Y < 100f,
            $"reused id 7 still interpolates the retired match's pose (x={fresh.X}, y={fresh.Y})");
    }

    [Fact]
    public void Game1PerShipRenderMaps_LiveInTheWorldContainer_AndRetireWithIt()
    {
        // Game1 cannot be instantiated headlessly (it needs a GraphicsDevice), so
        // its per-ship render maps must be owned by the world the frame gathered
        // from: one container per world, replaced at every retirement boundary.
        // Reflection only, so this test still compiles and RUNS (and fails) on the
        // pre-fix tree where no container exists.
        var cachesProp = typeof(ClientWorld).GetProperty("Caches",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "ClientWorld has no per-world ship-cache container: Game1's per-ship render maps are still unbounded");
        var world = NewWorld(out _);
        var live = cachesProp.GetValue(world)!;
        var ct = live.GetType();
        var radius = ct.GetField("RenderRadiusById", BindingFlags.Instance | BindingFlags.Public)?.GetValue(live)
                     as Dictionary<short, float>
            ?? throw new InvalidOperationException("the world container does not own RenderRadiusById");
        var sideRgb = ct.GetField("RenderSideRgbById", BindingFlags.Instance | BindingFlags.Public)?.GetValue(live)
                      as Dictionary<short, (float R, float G, float B)>
            ?? throw new InvalidOperationException("the world container does not own RenderSideRgbById");
        var lodFar = ct.GetField("LodFarById", BindingFlags.Instance | BindingFlags.Public)?.GetValue(live)
                     as Dictionary<short, bool>
            ?? throw new InvalidOperationException("the world container does not own LodFarById");

        // The render maps write-through: a value written for an id must not survive
        // the world boundary (the review's stale side-colour half of P971-03).
        radius[(short)5] = 12.5f;
        sideRgb[(short)5] = (0.1f, 0.2f, 0.3f);
        lodFar[(short)5] = true;
        world.Reset();
        var retired = cachesProp.GetValue(world)!;
        Assert.NotSame(live, retired);   // retirement publishes a NEW container …
        // … and counts the retirement (diagnostics: one increment per boundary).
        if (ct.GetField("Retirements", BindingFlags.Instance | BindingFlags.Public)?.GetValue(retired) is int generation)
            Assert.True(generation > 0, "a retired world's cache container must record the retirement");
        var rt = retired.GetType();      // … whose maps are empty (the old one is garbage)
        Assert.Empty((Dictionary<short, float>)rt.GetField("RenderRadiusById", BindingFlags.Instance | BindingFlags.Public)!.GetValue(retired)!);
        Assert.Empty((Dictionary<short, (float R, float G, float B)>)rt.GetField("RenderSideRgbById", BindingFlags.Instance | BindingFlags.Public)!.GetValue(retired)!);
        Assert.Empty((Dictionary<short, bool>)rt.GetField("LodFarById", BindingFlags.Instance | BindingFlags.Public)!.GetValue(retired)!);
        // A retired container is never mutated again, so it cannot hand a value to
        // the new world: the id-5 entries below are the retired object's own copy.
        Assert.Equal(12.5f, radius[(short)5]);

        // …and Game1 no longer owns its own unbounded per-ship copies. Game1 needs a
        // GraphicsDevice to exist, and its assembly (MonoGame.Framework) is not
        // loaded by this test host, so the structural check is best-effort: when the
        // type is loadable, its per-ship maps must be gone (the world container owns
        // them now).
        Type? game1 = null;
        try { game1 = Type.GetType("Allegiance.ClientCore.MonoGame.Game1, Allegiance.ClientCore.Game"); }
        catch { /* MonoGame.Framework absent in this host */ }
        if (game1 is not null)
            foreach (var field in new[] { "_shipRadiusById", "_shipSideRgbById", "_shipLodFar" })
                Assert.Null(game1.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic));
    }
}
