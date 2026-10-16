using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Pure-domain tests (no sockets, no server) for the P231 projectile tracking:
/// <see cref="ClientWorld"/>'s transient <see cref="ProjectileEntity"/> list fed
/// from the real <see cref="ProjectileInstance"/> (fmid 12) wire message, its
/// expiry pruner, and its velocity motion extrapolation. Messages are built
/// directly and fed through <see cref="ClientWorld.Apply"/>, exactly like the
/// other <c>ClientWorld*Tests</c> — the same shape as
/// <see cref="ClientWorldExportTests"/>. <see cref="ClientWorld.Apply"/> stamps
/// the local monotonic receipt instant, so each test captures it and asserts
/// relative to that instant (deterministic regardless of wall clock).
/// </summary>
public class ClientWorldProjectileTests
{
    private static ProjectileInstance Shot(
        short typeId = 4,
        float lifespan = 2.0f,
        Vector3? pos = null,
        Vector3? vel = null,
        Vector3? fwd = null,
        short launcherId = 0) => new()
    {
        TimeCreate = 555u,
        ProjectileTypeId = typeId,
        Position = pos ?? Vector3.Zero,
        Velocity = vel ?? new Vector3(0f, 0f, -1000f),
        Forward = fwd ?? new Vector3(0f, 0f, -1f),
        Lifespan = lifespan,
        LauncherId = launcherId,
    };

    /// <summary>A world with the local ship established (SET_CLUSTER assigns
    /// MyShipId and a Latest pose), so the P462 de-dup predicate path is live.</summary>
    private static ClientWorld WorldWithMyShip(short myShipId = 5) =>
        WorldWithMyShip(myShipId, out _);

    private static ClientWorld WorldWithMyShip(short myShipId, out Vector3 myPos)
    {
        var world = new ClientWorld();
        myPos = new Vector3(0f, 0f, 0f);
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromModel(
                myShipId,
                new Ship(myShipId, new DataHullTypeIGC(
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
                    ManuveringThrusterExteriorSound: 0, Hardpoints: []),
                    Vector3.Zero, Vector3.Zero, Orientation.Identity),
                1u),
            Cookie = 0x1,
        });
        return world;
    }

    // ── arrival into the transient list ────────────────────────────────────

    [Fact]
    public void ProjectileInstance_AppendsEntity_WithCapturedFields()
    {
        var world = new ClientWorld();
        world.Apply(Shot(pos: new Vector3(10f, 20f, 30f), vel: new Vector3(1f, 2f, -3f)));

        var p = Assert.Single(world.Projectiles);
        Assert.Equal((short)4, p.ProjectileTypeId);
        Assert.Equal(2.0f, p.Lifespan);
        Assert.Equal(10f, p.Position.X);
        Assert.Equal(20f, p.Position.Y);
        Assert.Equal(30f, p.Position.Z);
        Assert.Equal(1f, p.Velocity.X);
        Assert.Equal(2f, p.Velocity.Y);
        Assert.Equal(-3f, p.Velocity.Z);
    }

    [Fact]
    public void ProjectileInstances_AppendInArrivalOrder_NotKeyedById()
    {
        var world = new ClientWorld();
        world.Apply(Shot(typeId: 4));
        world.Apply(Shot(typeId: 7));

        Assert.Equal(2, world.Projectiles.Count);
        Assert.Equal((short)4, world.Projectiles[0].ProjectileTypeId);
        Assert.Equal((short)7, world.Projectiles[1].ProjectileTypeId);
    }

    [Fact]
    public void ReceiptInstant_IsStampedPerProjectile()
    {
        var world = new ClientWorld();
        world.Apply(Shot());
        world.Apply(Shot());

        long t0 = world.Projectiles[0].ReceivedLocalMs;
        long t1 = world.Projectiles[1].ReceivedLocalMs;
        Assert.True(t0 > 0);
        Assert.True(t1 >= t0);   // monotonic, second received at or after first
    }

    // ── expiry pruning ─────────────────────────────────────────────────────

    [Fact]
    public void Prune_RemovesExpired_KeepsAlive()
    {
        var world = new ClientWorld();
        world.Apply(Shot(lifespan: 1.0f));
        long t0 = world.Projectiles[0].ReceivedLocalMs;

        world.PruneExpiredProjectiles(t0 + 900);   // before the 1s lifetime
        Assert.Single(world.Projectiles);

        world.PruneExpiredProjectiles(t0 + 1000);  // at/after the 1s lifetime
        Assert.Empty(world.Projectiles);
    }

    [Fact]
    public void Prune_KeepsMultipleYoung_RemovesOnlyExpired()
    {
        var world = new ClientWorld();
        world.Apply(Shot(lifespan: 10.0f));   // young
        long young = world.Projectiles[^1].ReceivedLocalMs;
        world.Apply(Shot(lifespan: 0.5f));    // short-lived
        long old = world.Projectiles[^1].ReceivedLocalMs;

        // Middle ground: the short one is gone, the long one remains.
        world.PruneExpiredProjectiles(old + 510);
        Assert.Single(world.Projectiles);
        Assert.Equal((short)4, world.Projectiles[0].ProjectileTypeId);
        Assert.True(world.Projectiles[0].ReceivedLocalMs == young);
    }

    // ── motion extrapolation ───────────────────────────────────────────────

    [Fact]
    public void PositionAt_ExtrapolatesAlongVelocity()
    {
        var world = new ClientWorld();
        world.Apply(Shot(pos: new Vector3(10f, 20f, 30f), vel: new Vector3(0f, 0f, -100f)));
        long t0 = world.Projectiles[0].ReceivedLocalMs;

        var at = world.Projectiles[0].PositionAt(t0 + 2000);   // 2 s later
        Assert.Equal(10f, at.X, precision: 3);
        Assert.Equal(20f, at.Y, precision: 3);
        Assert.Equal(-170f, at.Z, precision: 3);               // 30 + (-100 * 2)
    }

    [Fact]
    public void PositionAt_DoesNotTravelBeforeReceipt()
    {
        var world = new ClientWorld();
        world.Apply(Shot(pos: new Vector3(1f, 2f, 3f), vel: new Vector3(0f, 0f, -50f)));
        long t0 = world.Projectiles[0].ReceivedLocalMs;

        var at = world.Projectiles[0].PositionAt(t0 - 5000);   // 5 s before receipt
        Assert.Equal(1f, at.X, precision: 3);
        Assert.Equal(2f, at.Y, precision: 3);
        Assert.Equal(3f, at.Z, precision: 3);
    }

    // ── P462 own-shot-echo de-dup (P542: per-own-shot) ──────────────────

    /// <summary>
    /// P462/P542 glue test: the <see cref="ClientWorld.OwnEchoDropPredicate"/> is
    /// consulted ONLY for a round LAUNCHED BY OUR OWN ship (LauncherId == MyShipId),
    /// and only then is a matching round dropped as our own echo. A foreign round —
    /// even one of the SAME type spawned right next to us (a teammate/enemy
    /// dogfighter flying the same hull + default gun as the player) — is NEVER our
    /// echo and is always kept, so other ships' shots stay visible. Combined with
    /// the pure de-dup rule in <c>OwnShotEcho.ShouldDropServerEcho</c> (tested in
    /// the MonoGame tests), this completes the "own echo dropped / foreign kept /
    /// unmatched falls back" contract.
    /// </summary>
    [Fact]
    public void OwnEchoPredicate_DropsOwnEcho_KeepsForeignEvenSameType()
    {
        var world = WorldWithMyShip(5);
        bool dropAny = true;
        world.OwnEchoDropPredicate = (typeId, spawn, serverPos, now) => dropAny;

        // Own echo (launched by us, id 5) with the predicate agreeing → dropped.
        world.Apply(Shot(typeId: 4, pos: new Vector3(10f, 0f, 0f), launcherId: 5));
        Assert.Empty(world.Projectiles);
        Assert.Equal(1, world.Diag.DroppedOwn);

        // FOREIGN round of the SAME type, launched by another ship id, spawned
        // right next to us — the predicate would say "drop", but because it is
        // NOT our shot the P542 fix keeps it (it must render as a visible tracer).
        world.Apply(Shot(typeId: 4, pos: new Vector3(10f, 0f, 0f), launcherId: 911));
        var p = Assert.Single(world.Projectiles);
        Assert.Equal((short)4, p.ProjectileTypeId);
        Assert.Equal((short)911, p.LauncherId);
        Assert.Equal(0, world.Diag.DroppedEnemy);
        Assert.Equal(1, world.Diag.ReceivedEnemy);
    }

    /// <summary>
    /// P542 FAILING-FIRST characterisation of the exact bug this task fixes: an
    /// ENEMY round of the SAME projectile type as our own, spawned adjacent to our
    /// ship, used to be dropped as "our own echo" (the predicate matched on
    /// type + radius + window with no check of WHO fired), so a bot flying the same
    /// hull + default gun as the player had its near-field shots VANISH. After the
    /// fix the round is kept (rendered). This is the before/after proof at unit
    /// level: run it against the pre-fix Apply (predicate consulted regardless of
    /// launcher) and it fails.
    /// </summary>
    [Fact]
    public void EnemyRound_OfSameType_AdjacentToUs_IsKept()
    {
        var world = WorldWithMyShip(5);
        // Simulate the real installer: the predicate reports "a matching local
        // round exists" for this type/position/window (as a same-type dogfight
        // does). Before P542 this meant the enemy's shot was dropped as our echo.
        world.OwnEchoDropPredicate = (typeId, spawn, serverPos, now) => true;

        world.Apply(Shot(typeId: 85, pos: new Vector3(12f, 0f, 0f), launcherId: 987));

        var p = Assert.Single(world.Projectiles);       // MUST be kept (not dropped)
        Assert.Equal((short)987, p.LauncherId);
        Assert.Equal(0, world.Diag.DroppedEnemy);
        Assert.Equal(1, world.Diag.ReceivedEnemy);
        Assert.Equal(1, world.Diag.TotalReceived);
    }

    /// <summary>
    /// P462 pre-fix characterisation: with no <see cref="ClientWorld.OwnEchoDropPredicate"/>
    /// installed (i.e. the current/pre-fix code path), the client world contributes
    /// NOTHING of its own when the trigger is pulled — the projectile list stays
    /// empty until the server's <c>PROJECTILE_INSTANCE</c> echo arrives. This is
    /// the exact latency the fix targets (shot + sound appearing at the trigger
    /// frame instead of a round trip later). Recorded in the P462 report.
    /// </summary>
    [Fact]
    public void PreFix_NoOwnRoundExistsBeforeServerEcho()
    {
        var world = WorldWithMyShip(5);
        Assert.Null(world.OwnEchoDropPredicate);   // pre-fix: no echo, no local round
        Assert.Empty(world.Projectiles);           // trigger pulled → nothing yet

        world.Apply(Shot(typeId: 4));              // ...until the server echo arrives
        Assert.Single(world.Projectiles);
    }
    [Fact]
    public void Probe_source_roundtrips_without_ship_id_collision_or_own_echo_drop()
    {
        var world = WorldWithMyShip(5);
        world.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        world.OwnEchoDropPredicate = (_, _, _, _) => true;
        var shot = Shot(launcherId: 5);
        Assert.Equal(54, shot.Encode().Length);
        Assert.Equal(IgcObjectType.Ship, ProjectileInstance.Decode(shot.Encode()).LauncherType);
        shot.LauncherType = IgcObjectType.Probe;
        var bytes = shot.Encode();
        Assert.Equal(56, bytes.Length);
        var decoded = ProjectileInstance.Decode(bytes);
        Assert.Equal(IgcObjectType.Probe, decoded.LauncherType);
        Assert.Equal((short)5, decoded.LauncherId);
        world.Apply(decoded);
        Assert.Equal(1, world.Diag.FromNeverPosed);
        world.Apply(new ExportProbe { Probe = new DataProbeExport(
            P0: new Vector3(100, 0, 0), Time0: 0, ProbeId: 5, ExportF: true,
            ProbetypeId: 30, SideId: 0, ClusterId: 1, ShipId: 99,
            OtTarget: -1, OidTarget: -1, CreateNow: true) });
        world.Apply(decoded);
        Assert.Equal(1, world.Diag.FromNeverPosed);
        Assert.Equal(1, world.Diag.ReceivedTeammate);
        Assert.Equal(0, world.Diag.ReceivedOwn);
        Assert.Equal(2, world.Projectiles.Count);
        Assert.All(world.Projectiles, p => Assert.Equal(IgcObjectType.Probe, p.LauncherType));
    }

}
