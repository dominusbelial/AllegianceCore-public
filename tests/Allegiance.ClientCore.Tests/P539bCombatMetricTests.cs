using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;
using Allegiance.Tools.McpSpawner;
using Boh = Allegiance.Tools.McpSpawner.BotManager;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P539b — failing-first UNIT tests for the corrected combat metric and the
/// tech-level loadout:
/// <list type="bullet">
/// <item>hit attribution — a hit fired by a TEAMMATE is never counted for this
/// bot; a hit on a FRIENDLY is counted as friendly, not enemy; enemy hits split
/// shield/hull.</item>
/// <item>shots — come from REAL fired rounds (PROJECTILE_INSTANCE echoes
/// carrying the bot's own ship as launcher), not a fire decision.</item>
/// <item>tech level — level 0 == the hull's native preferred parts; level N
/// advances each preferred part N successor steps and stops at an unmountable
/// successor / chain end.</item>
/// <item>the friendly-in-line-of-fire gate holds fire when a teammate is in
/// the cone.</item>
/// </list>
/// </summary>
public class P539bCombatMetricTests
{
    // ── Hit attribution (pure BucketHits) ─────────────────────────────────

    /// <summary>A hit fired by a TEAMMATE is never counted for this bot — the
    /// P539 metric bug (every bot counted every sector-wide hit as its own).</summary>
    [Fact]
    public void Hit_By_Teammate_Is_Not_Counted_For_ThisBot()
    {
        short me = 9100, mySide = 1;
        var hits = new[]
        {
            (SourceId: (short)9101, TargetId: (short)9200, Absorbed: false, TargetSide: (short?)2), // teammate's hit on an enemy
            (SourceId: (short)9101, TargetId: (short)9300, Absorbed: false, TargetSide: (short?)1), // teammate's hit on our friendly
        };
        var b = Boh.BucketHits(me, mySide, hits);
        Assert.Equal(0, b.EnemyShield + b.EnemyHull);   // no enemy hits attributed to us
        Assert.Equal(0, b.Friendly);                    // no friendly hits either
        Assert.Equal(0, b.None);
    }

    /// <summary>A hit WE cause on a FRIENDLY ship is counted as FRIENDLY (friendly
    /// fire), never as an enemy hit — the P539b enemy/friendly/none split.</summary>
    [Fact]
    public void Hit_On_Friendly_Is_Friendly_Not_Enemy()
    {
        short me = 9100, mySide = 1;
        var hits = new[]
        {
            (SourceId: (short)9100, TargetId: (short)9102, Absorbed: false, TargetSide: (short?)1), // our hit on our own side
        };
        var b = Boh.BucketHits(me, mySide, hits);
        Assert.Equal(1, b.Friendly);
        Assert.Equal(0, b.EnemyShield + b.EnemyHull);
        Assert.Equal(0, b.None);
    }

    /// <summary>Enemy hits split by shield vs hull; a hit on a no-side/no-ship
    /// target (-1) is counted as none.</summary>
    [Fact]
    public void Enemy_Hits_Split_ShieldHull_And_None()
    {
        short me = 9100, mySide = 1;
        var hits = new[]
        {
            (SourceId: (short)9100, TargetId: (short)9200, Absorbed: true, TargetSide: (short?)2),   // enemy shield hit
            (SourceId: (short)9100, TargetId: (short)9201, Absorbed: false, TargetSide: (short?)2),  // enemy hull hit
            (SourceId: (short)9100, TargetId: (short)-1, Absorbed: false, TargetSide: (short?)null), // asteroid/no ship
        };
        var b = Boh.BucketHits(me, mySide, hits);
        Assert.Equal(1, b.EnemyShield);
        Assert.Equal(1, b.EnemyHull);
        Assert.Equal(1, b.None);
        Assert.Equal(2, b.EnemyShield + b.EnemyHull);   // ShipHits numerator = both enemy damaging hits
        Assert.Equal(0, b.Friendly);
    }

    // ── Shots come from REAL fired rounds (PROJECTILE_INSTANCE), not a decision ─

    private static ProjectileInstance Shot(short launcherId) => new()
    {
        TimeCreate = 999u,
        ProjectileTypeId = 4,
        Position = Vector3.Zero,
        Velocity = new Vector3(0f, 0f, -1000f),
        Forward = new Vector3(0f, 0f, -1f),
        Lifespan = 2f,
        LauncherId = launcherId,
    };

    /// <summary>Counting shots from a ClientWorld's drained projectile instances
    /// and filtering to LauncherId == myShipId yields ONLY this bot's real fired
    /// rounds — a foreign round (teammate/enemy) is present in the world but is
    /// never counted as this bot's shot. This is the "shots come from real fire
    /// events, not decisions" guarantee: a fire DECISION (P539's ShotsFired++)
    /// would count rounds the server never actually created.</summary>
    [Fact]
    public void Shots_ComeFrom_RealProjectileInstances_FilteredToOurLauncher()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster
        {
            SectorId = 1,
            Ship = ServerShipUpdate.FromModel(
                9100,
                new Ship(9100, new DataHullTypeIGC(
                    Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
                    Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
                    Mass: 550f, Signature: 0f, Speed: 120f,
                    MaxTurnRates: [4.5f, 4.5f, 4.5f], TurnTorques: [5000f, 5000f, 5000f],
                    Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
                    MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
                    RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 7788, SuccessorHullId: -1,
                    MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 100f, HardpointOffset: 540,
                    DefenseType: 1, CapacityMagazine: 1, CapacityDispenser: 1, CapacityChaffLauncher: 1,
                    PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "tex",
                    PmEquipment: new short[8], InteriorSound: 0, ExteriorSound: 0,
                    MainThrusterInteriorSound: 0, MainThrusterExteriorSound: 0,
                    ManuveringThrusterInteriorSound: 0, ManuveringThrusterExteriorSound: 0,
                    Hardpoints: []),
                    Vector3.Zero, Vector3.Zero, Orientation.Identity),
                1u),
            Cookie = 0x1,
        });

        // Three real rounds enter the world: 2 launched by US (9100), 1 by a teammate.
        world.Apply(Shot(9100));
        world.Apply(Shot(9100));
        world.Apply(Shot(9109));

        var drained = world.TakeNewProjectiles();
        long myShots = drained.Count(p => p.LauncherId == 9100);
        Assert.Equal(2, myShots);                 // only OUR real rounds count as our shots
        Assert.Equal(3, drained.Count);           // the world did receive all three
        // Re-drain returns nothing new (drain-then-forget lifecycle).
        Assert.Empty(world.TakeNewProjectiles());
    }

    // ── Tech level resolution (real corpus) ────────────────────────────────

    private static StaticCore Core() => Catalog.RepresentativeCore();

    /// <summary>Level 0 == the hull's own native preferred parts: the Adv Scout's
    /// preferred gun mounts at level 0, and — after P555's native-alignment fix —
    /// its preferred shield (Sm Shield 1) MOUNTS too, advancing Sm Shield 1 (21)
    /// → Sm Shield 2 (40) → Sm Shield 3 (42). Pre-P555 the garbled shield socket
    /// made a native-faithful bot fly unshielded ("shield-invincible bots");
    /// post-P555 it flies its real preferred shield.</summary>
    [Fact]
    public void TechLevel0_IsNativePreferred_GunAndShieldMount()
    {
        var core = Core();
        // Adv Scout hull id 13; PW Gat Gun 1 = 33; Sm Shield 1 = 21.
        Assert.Equal((short)33, Catalog.ResolvePreferredAtLevel(core, 33, 13, 0));
        Assert.Equal((short)21, Catalog.ResolvePreferredAtLevel(core, 21, 13, 0));   // Sm Shield 1 MOUNTS (P555)
        Assert.Equal((short)42, Catalog.ResolvePreferredAtLevel(core, 21, 13, 2));   // advances to Sm Shield 3
    }

    /// <summary>Each preferred part advances N steps along its native successor
    /// chain: PW Gat Gun 1 (33) → PW Gat Gun 2 (71) at level 1 → PW Gat Gun 3 (72)
    /// at level 2, and STOPS at the chain end (72 has no successor, so level 3
    /// stays 72).</summary>
    [Fact]
    public void TechLevel_Advances_PreferredPart_Along_SuccessorChain()
    {
        var core = Core();
        Assert.Equal((short)71, Catalog.ResolvePreferredAtLevel(core, 33, 13, 1));
        Assert.Equal((short)72, Catalog.ResolvePreferredAtLevel(core, 33, 13, 2));
        Assert.Equal((short)72, Catalog.ResolvePreferredAtLevel(core, 33, 13, 3));   // chain-end clamp
    }

    /// <summary>The built level-N loadout reflects the advanced parts: level 0
    /// carries PW Gat Gun 1 + Sm Shield 1; level 2 carries PW Gat Gun 3 + Sm
    /// Shield 3 (the successor chain, P555-corrected).</summary>
    [Fact]
    public void NativePreferredLoadout_Levels_ReflectAdvancedGun_AndShield()
    {
        var lvl0 = Catalog.NativePreferredLoadout(13, "Adv Scout", 0);
        var lvl2 = Catalog.NativePreferredLoadout(13, "Adv Scout", 2);
        Assert.Contains(lvl0, p => p.PartId == 33);
        Assert.Contains(lvl2, p => p.PartId == 72);
        Assert.Contains(lvl0, p => p.PartId == 21);   // Sm Shield 1 — P555: now mounts
        Assert.Contains(lvl2, p => p.PartId == 42);   // Sm Shield 3 — advanced
        Assert.DoesNotContain(lvl0, p => p.PartId == 42);  // not yet at level 0
        Assert.DoesNotContain(lvl2, p => p.PartId == 21);  // superseded at level 2
    }

    // ── Friendly-in-line-of-fire gate ──────────────────────────────────────

    /// <summary>The gate HOLDS FIRE when a same-side teammate sits in the aim
    /// cone ahead, and does NOT hold fire for an enemy (different side) in that
    /// spot or for a teammate outside the cone / beyond reach.</summary>
    [Fact]
    public void FriendlyInCone_HoldsFire_WhenTeammateInCone()
    {
        var myPos = Vector3.Zero;
        var aim = new Vector3(0f, 0f, -1f);            // shooting "forward" (-Z)
        float reach = 800f;
        short mySide = 1;

        var inCone = new[] { (Pos: new Vector3(0f, 0f, -300f), Id: (short)9501, Side: (short)1) };
        Assert.True(Boh.FriendlyInCone(myPos, aim, reach, inCone, mySide), "a teammate straight ahead in range must block fire");

        var enemyAhead = new[] { (Pos: new Vector3(0f, 0f, -300f), Id: (short)9501, Side: (short)2) };
        Assert.False(Boh.FriendlyInCone(myPos, aim, reach, enemyAhead, mySide), "an ENEMY in the same spot must NOT block fire");

        var teammateWide = new[] { (Pos: new Vector3(0f, 400f, -300f), Id: (short)9501, Side: (short)1) };
        Assert.False(Boh.FriendlyInCone(myPos, aim, reach, teammateWide, mySide), "a teammate out of the narrow cone must not block fire");

        var teammateFar = new[] { (Pos: new Vector3(0f, 0f, -5000f), Id: (short)9501, Side: (short)1) };
        Assert.False(Boh.FriendlyInCone(myPos, aim, reach, teammateFar, mySide), "a teammate beyond the weapon's reach must not block fire");
    }
}
