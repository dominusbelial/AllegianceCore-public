using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Tools.McpSpawner;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P539 — prove the pure P539 dogfight decision helpers against synthetic
/// geometry (no server) and, where noted, against the REAL PCore001.igc corpus:
/// lead-aimed fire in a cone, fire-gating by range+cone, stand-off steering
/// that never points at the target's position, the dogfight default weapon
/// (not the dispersion-0 test gun), and the lifepod "go dock over combat"
/// decision.
/// </summary>
public class P539CombatDecisionTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static StaticCore RealCore() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    // ── Lead-aim: a crossing target needs a lead ≠ its current position ──

    [Fact]
    public void CrossingTarget_LeadDirectionDiffersFromRawBearing_AndHitsIntercept()
    {
        // Bot parked at origin; target dead ahead (+Z) but crossing at +120 u/s.
        // A 600 u/s round aimed at the target's CURRENT position trails it, so
        // the lead solve must point ahead (+X) and its round-path must reach the
        // target's future position.
        Vector3 myPos = new(0f, 0f, 0f), myVel = new(0f, 0f, 0f);
        Vector3 targetPos = new(0f, 0f, 100f), targetVel = new(120f, 0f, 0f);
        float radius = 8f, speed = 600f;

        bool ok = BotManager.TrySolveLead(myPos, myVel, targetPos, targetVel, radius, speed, true, out var aim, out var t);
        Assert.True(ok, "an intercept must exist for a reachable crossing target");
        Assert.True(t > 0f);

        Vector3 raw = targetPos - myPos;
        raw /= raw.Length();                                   // raw bearing = straight ahead (+Z)
        Assert.True(MathF.Abs(raw.X) < 1e-4f && raw.Z > 0.99f, "sanity: raw bearing is straight ahead");

        Assert.True(aim.X > 0.02f, $"lead must carry a lateral (+X) component for a crossing target (got {aim.X})");
        Assert.True(MathF.Abs(aim.X - raw.X) > 1e-4f, "the lead direction must differ from the raw current-position bearing");

        // The round fired along `aim` at `speed` for `t` lands on the target's
        // future position (within its radius).
        Vector3 roundEndpoint = myPos + aim * (t * speed);
        Vector3 targetFuture = targetPos + targetVel * t;
        Assert.True((roundEndpoint - targetFuture).Length() < radius + 4f,
            $"round must meet the target's future position (gap {(roundEndpoint - targetFuture).Length():F1}u)");
    }

    // ── (P558) NOTE: the P539 Fire_On/Off and StandOffGoal_KeepsStandOffRing
    // tests were RETIRED with the P539 stand-off ring / DecideFire helpers they
    // exercised. The native fire gate and spacing law they covered are now
    // proven by P558NativeWingmanAttackTests (fire gate = angle ≤ 0.10 rad AND
    // t ≤ lifespan·0.95; spacing = the lead-time distance bands + back-off). ──

    // ── Dogfight default weapon is NOT the dispersion-0 test gun ──

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void DogfightDefaultWeapon_IsNotTheDispersionZeroTestGun_AndMountsHull()
    {
        Require.Artwork();
        var fightHull = Catalog.DefaultFighterHull();
        var dogfight = Catalog.DogfightWeaponPart(fightHull.Id);
        var testGun = Catalog.DefaultWeaponPart();

        Assert.NotEqual(testGun.Id, dogfight.Id);

        var core = RealCore();
        var hull = core.HullTypes[fightHull.Id];
        var part = core.PartTypes[dogfight.Id];
        Assert.True(hull.CanMountPart(in part), "the dogfight default must actually mount the hull (server ApplyLoadout gate)");
        var w = (DataPartTypeIGC.Weapon)part.Type;
        Assert.True(core.ProjectileTypes[w.ProjectileTypeId].Power > 0f, "the dogfight default must deal real damage");
        Assert.True(w.EnergyPerShot <= hull.MaxEnergy, "the dogfight default must be energy-affordable on the hull");
    }

    // ── Lifepod: a pod-hull bot goes dock, a fighter fights ──

    [Fact]
    public void LifepodHull_PicksGoDockOverCombat()
    {
        Assert.True(BotManager.DecidePodBehavior(ownHullIsLifepod: true));
        Assert.False(BotManager.DecidePodBehavior(ownHullIsLifepod: false));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void LifepodHullIds_DetectedViaRealCapability()
    {
        Require.Artwork();
        var core = RealCore();
        var pods = core.HullTypes.Values.Where(h => h.HasCapability(DataHullTypeIGC.HabmLifepod)).ToList();
        Assert.NotEmpty(pods);
        foreach (var h in pods)
            Assert.True(Catalog.IsLifepodHull(h.HullId), $"hull {h.HullId} ('{h.Name}') must read as a lifepod");
        var fighter = core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod));
        Assert.False(Catalog.IsLifepodHull(fighter.HullId), $"hull {fighter.HullId} ('{fighter.Name}') must NOT read as a lifepod");
    }
}
