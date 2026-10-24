using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P786 (PAR-5) — HUD wire data: the values the native client HUD reads
/// (signature cloak ramp, launcher arming fraction, probe/mine lifetime fill,
/// missile model radius, per-side ore seen, per-target scanner CanSee) now
/// reach or are computed from the data the client already receives. Pure model
/// rules only — no GraphicsDevice.
/// </summary>
public class P786HudWireDataTests
{
    // ── Signature percentage: the live GetCloaking() ramp ───────────────────

    private static DataPartTypeIGC Part(short id, string name, DataPartTypeIGC.Subtype type, short partMask = 1) =>
        new(Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: name, Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 1f, Signature: 0f,
            PartId: id, SuccessorPartId: -1, PartMask: partMask, InventoryLineMdl: "", Type: type);

    private static DataHullTypeIGC Hull(float signature = 200f, float scannerRange = 800f) => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "Fig", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 100f, Signature: signature, Speed: 200f,
        MaxTurnRates: [4f, 4f, 4f], TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 50000f, SideMultiplier: 1f, BackMultiplier: 1f, ScannerRange: scannerRange,
        MaxFuel: 100f, Ecm: 0f, Length: 20f, MaxEnergy: 200f, RechargeRate: 5f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 200, HullId: 999, SuccessorHullId: -1,
        MaxWeapons: 3, MaxFixedWeapons: 2, HitPoints: 500f, HardpointOffset: 540,
        DefenseType: 0, CapacityMagazine: 1, CapacityDispenser: 1, CapacityChaffLauncher: 1,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "",
        PmEquipment: [1, 1, 1, 1, 1, 1, 1, 1],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0,
        Hardpoints: [new(0, 0, "", "1", 1, true), new(0, 0, "", "2", 1, true)]);

    [Fact]
    public void Signature_percent_multiplies_by_the_live_cloak_ramp()
    {
        // shipIGC.h:268-270 — GetSignature() = accumulated · m_cloaking. Hull
        // 200 / side GA 200 = 100% fully visible; the cloak ramp at 0.4 lands
        // at 40%, not the engaged end state 0 the pre-P786 code produced.
        float? fullyVisible = OwnStatusData.SignaturePercent(
            200f, null, _ => null, firing: false, groupFire: false, selectedMount: 0,
            afterburnerActive: false, cloakActive: false, sideSignature: 200f, cloaking: 1f);
        Assert.Equal(100f, fullyVisible!.Value, 2);

        float? ramping = OwnStatusData.SignaturePercent(
            200f, null, _ => null, firing: false, groupFire: false, selectedMount: 0,
            afterburnerActive: true, cloakActive: true, sideSignature: 200f, cloaking: 0.4f);
        Assert.Equal(40f, ramping!.Value, 2);
    }

    [Fact]
    public void Signature_percent_falls_back_to_the_engaged_end_state_without_a_stream_sample()
    {
        // No OwnShipCloakUpdate(220) sample yet + the cloak bit set → the
        // pre-P786 engaged behaviour (factor 0) is the documented fallback.
        float? engaged = OwnStatusData.SignaturePercent(
            200f, null, _ => null, firing: false, groupFire: false, selectedMount: 0,
            afterburnerActive: false, cloakActive: true, sideSignature: 200f, cloaking: null);
        Assert.Equal(0f, engaged!.Value, 2);
    }

    // ── Probe/mine lifetime fill (radarimage.cpp:1073-1082) ─────────────────

    [Fact]
    public void Probe_mine_time_fraction_runs_down_and_clamps()
    {
        // probeigc.h:271-279 / mineigc.h:183-191 — (expire − now)/lifespan.
        // A 60 s probe deployed 10 s ago fills 50/60.
        float? f = HudWireValues.ProbeMineTimeFraction(1000.0, 60.0, 1010.0);
        Assert.Equal(50f / 60f, f!.Value, 4);

        // Expired → 0; not yet started (clock skew) → 1; no lifespan → null.
        Assert.Equal(0f, HudWireValues.ProbeMineTimeFraction(1000.0, 60.0, 2000.0)!.Value, 4);
        Assert.Equal(1f, HudWireValues.ProbeMineTimeFraction(1000.0, 60.0, 900.0)!.Value, 4);
        Assert.Null(HudWireValues.ProbeMineTimeFraction(1000.0, 0.0, 1010.0));
    }

    // ── Asteroid ore fill in native shipload units ──────────────────────────

    [Fact]
    public void Ore_seen_fill_uses_two_shiploads_as_full()
    {
        // radarimage.cpp:1051-1069 — fill = oreSeen/(2·capacity), drawn only
        // when oreSeen != 0. capacity 90 (the native c_fCapacityHe3 default):
        // a rock at 60% of 300 ore carries 180 ore = exactly 2 shiploads → 1.
        Assert.Equal(1f, HudWireValues.OreSeenFill(0.6f, 300f, 90f)!.Value, 3);
        // 90 ore = 1 shipload → 0.5.
        Assert.Equal(0.5f, HudWireValues.OreSeenFill(0.3f, 300f, 90f)!.Value, 3);
        // No side-known ore → no bar, exactly like native's if (ore != 0).
        Assert.Null(HudWireValues.OreSeenFill(0f, 300f, 90f));
    }

    [Fact]
    public void Miner_capacity_multiplies_the_side_attribute()
    {
        // radarimage.cpp:576-577 — FloatConstants[c_fcidCapacityHe3] × GA.
        Assert.Equal(180f, HudWireValues.MinerCapacityHe3(90f, 2f), 3);
        // The core omitted the constant → native's c_fCapacityHe3 = 90 default.
        Assert.Equal(90f, HudWireValues.MinerCapacityHe3(null, 1f), 3);
    }

    // ── Launcher arming fraction (consoledata.cpp:1189-1202) ────────────────

    [Fact]
    public void Launcher_armed_fraction_ramps_from_the_fire_clock()
    {
        const double loadTime = 4.0;
        double now = 100.0;
        // magazineigc.h:156-165 — 1 − max(0, (timeLoaded − now)/loadTime).
        Assert.Equal(1f, HudWireValues.LauncherArmedFraction(1f, now - 10.0, loadTime, now), 3);
        Assert.Equal(0.5f, HudWireValues.LauncherArmedFraction(1f, now + 2.0, loadTime, now), 3);
        Assert.Equal(0f, HudWireValues.LauncherArmedFraction(1f, now + 9.0, loadTime, now), 3);
        // Still mounting → 0 (GetMountedFraction() < 1 branch).
        Assert.Equal(0f, HudWireValues.LauncherArmedFraction(0.5f, now - 10.0, loadTime, now), 3);
        // No clock ever set → steady-state armed.
        Assert.Equal(1f, HudWireValues.LauncherArmedFraction(1f, double.NegativeInfinity, loadTime, now), 3);
    }

    [Fact]
    public void Own_launcher_arming_replays_the_own_missile_fire()
    {
        var tracker = new OwnLauncherArming();
        var missile = Part(200, "Missile Launcher", new DataPartTypeIGC.MissileLauncher(500));
        tracker.Update(missilesFired: 0, dispenserDeployed: 0,
            [new ExpandedPartData(200, 10, 0, ExpandedPartData.FullyMounted)],
            _ => (EquipmentType.Magazine, 4.0), mountRate: 1.0, nowLocalSeconds: 100.0);
        // First sighting of the mounted launcher stamps the Activate clock
        // (magazineigc.h:73): now + 1/mountRate + loadTime → still arming.
        Assert.True(tracker.ArmedFraction(EquipmentType.Magazine, 1f) < 1f);

        // The own FIRE_MISSILE salvo (OwnMissilesFired 0 → 1) resets the clock
        // to now + loadTime (magazineigc.cpp:116) — the arming bar restarts.
        tracker.Update(missilesFired: 1, dispenserDeployed: 0,
            [new ExpandedPartData(200, 9, 0, ExpandedPartData.FullyMounted)],
            _ => (EquipmentType.Magazine, 4.0), mountRate: 1.0, nowLocalSeconds: 120.0);
        Assert.Equal(0f, tracker.ArmedFraction(EquipmentType.Magazine, 1f), 3);

        // loadTime later it is armed again.
        tracker.Update(missilesFired: 1, dispenserDeployed: 0,
            [new ExpandedPartData(200, 9, 0, ExpandedPartData.FullyMounted)],
            _ => (EquipmentType.Magazine, 4.0), mountRate: 1.0, nowLocalSeconds: 124.5);
        Assert.Equal(1f, tracker.ArmedFraction(EquipmentType.Magazine, 1f), 2);
    }

    [Fact]
    public void Own_launcher_arming_covers_the_dispenser_clock_separately()
    {
        var tracker = new OwnLauncherArming();
        var dispenser = Part(201, "Mine Dispenser", new DataPartTypeIGC.MineLauncher(501));
        tracker.Update(missilesFired: 0, dispenserDeployed: 0,
            [new ExpandedPartData(201, 5, 0, ExpandedPartData.FullyMounted)],
            _ => (EquipmentType.Dispenser, 6.0), mountRate: 1.0, nowLocalSeconds: 50.0);
        Assert.True(tracker.ArmedFraction(EquipmentType.Dispenser, 1f) < 1f);

        // The magazine clock is untouched (a dispenser deploy must not reset it).
        tracker.StampForTest(EquipmentType.Magazine, double.NegativeInfinity, 4.0, 50.0);
        Assert.Equal(1f, tracker.ArmedFraction(EquipmentType.Magazine, 1f), 3);
    }

    // ── Inventory pane: ReadyState 2 + the arming bar ────────────────────────

    private static readonly DataMissileTypeIGC MissileType = new(
        Color: [1f, 1f, 1f], Radius: 4f, Rotation: 0f, ModelName: "missile", TextureName: "tex",
        LoadTime: 4f, Lifespan: 6f, Signature: 1f, Price: 100, TimeToBuild: 0,
        LauncherModelName: "", LauncherIconName: "", LauncherName: "", LauncherDescription: "",
        LauncherGroupId: 0, LauncherTtbmRequired: [], LauncherTtbmEffects: [], LauncherSignature: 1f,
        LauncherMass: 10f, LauncherPartMask: 1, LauncherExpendableSize: 1, HitPoints: 10f,
        DefenseType: 0, ExpendableTypeId: 500, EabmCapabilities: 0, IconName: "ico",
        Acceleration: 50f, TurnRate: 1f, InitialSpeed: 100f, LockTime: 1f, ReadyTime: 0f,
        MaxLock: 1f, ChaffResistance: 0f, Dispersion: 0f, LockAngle: 0f,
        Power: 10f, BlastPower: 0f, BlastRadius: 0f, Width: 0f, DamageType: 0,
        Directional: false, LaunchSound: 0, AmbientSound: 0);

    [Fact]
    public void Mounted_armed_launcher_reads_arming_state_two()
    {
        // consoledata.cpp:1157-1177 case 2 — a mounted launcher with
        // GetArmedFraction < 1 is state 2, and the row carries the fraction.
        var catalog = new ShopCatalog(
            Hulls: [Hull()], Parts: [Part(200, "Missile Launcher", new DataPartTypeIGC.MissileLauncher(500))],
            StationTypes: null, MissileTypes: new Dictionary<short, DataMissileTypeIGC> { [500] = MissileType },
            MineTypes: null, ChaffTypes: null, LifepodEnduranceSeconds: 0, Launchers: null,
            ProjectileTypes: null, ProbeTypes: null, Developments: null, DroneTypes: null, Core: null);

        var pane = HudInventoryPaneBuilder.Build(
            catalog, Hull(), [new ExpandedPartData(200, 10, 0, ExpandedPartData.FullyMounted)], [],
            GlobalAttributeSet.Identity, 1f, 1f, 1f, 1f, groupFire: false, selectedMount: 0,
            selectedCargoMount: -1, ownShipName: "me",
            armedFraction: static (et, mounted) => HudWireValues.LauncherArmedFraction(
                mounted, 100.0 + 2.0, 4.0, 100.0));   // fired 1 s ago of a 4 s load

        var row = pane.Rows.Single(r => r.Kind == HudInventoryRowKind.Magazine);
        Assert.Equal(2, row.ReadyState);
        Assert.NotNull(row.ArmedFraction);
        // timeLoaded = now + 2 s of a 4 s load → half remaining.
        Assert.Equal(0.5f, row.ArmedFraction!.Value, 3);
    }

    [Fact]
    public void Loaded_launcher_reads_ready_and_chaff_never_arms()
    {
        var chaff = Part(202, "Chaff Launcher", new DataPartTypeIGC.ChaffLauncher(502));
        var catalog = new ShopCatalog(
            Hulls: [Hull()], Parts: [Part(200, "Missile Launcher", new DataPartTypeIGC.MissileLauncher(500)), chaff],
            StationTypes: null, MissileTypes: new Dictionary<short, DataMissileTypeIGC> { [500] = MissileType },
            MineTypes: null, ChaffTypes: null, LifepodEnduranceSeconds: 0, Launchers: null,
            ProjectileTypes: null, ProbeTypes: null, Developments: null, DroneTypes: null, Core: null);

        // The callback reports a live arming ramp for every launcher: the
        // magazine honours it (state 2 + the fraction), while the chaff row
        // never arms (IlauncherIGC::IsLauncher excludes chaff, igc.h:3993-3999).
        var pane = HudInventoryPaneBuilder.Build(
            catalog, Hull(),
            [new ExpandedPartData(200, 10, 0, ExpandedPartData.FullyMounted),
             new ExpandedPartData(202, 5, 0, ExpandedPartData.FullyMounted)],
            [], GlobalAttributeSet.Identity, 1f, 1f, 1f, 1f, groupFire: false, selectedMount: 0,
            selectedCargoMount: -1, ownShipName: "me",
            armedFraction: static (_, _) => 0.25f);

        var magazine = pane.Rows.Single(r => r.Kind == HudInventoryRowKind.Magazine);
        Assert.Equal(2, magazine.ReadyState);
        Assert.Equal(0.25f, magazine.ArmedFraction!.Value, 3);
        var chaffRow = pane.Rows.Single(r => r.Kind == HudInventoryRowKind.Chaff);
        Assert.Equal(3, chaffRow.ReadyState);
        Assert.Null(chaffRow.ArmedFraction);
    }

    // ── Client-side CanSee (shipIGC.h:380-389) ───────────────────────────────

    private static VisibilityMath.SightBody Body(float x, float y, float z, float radius) =>
        new(new Vector3(x, y, z), radius);

    private static bool CanSee(
        bool sameSide = false, bool unknown = false, bool notVisible = false, bool hasIdentity = true,
        short ownSector = 10, short targetSector = 10,
        Vector3? ownPos = null, Vector3? ownNose = null, float ownRadius = 10f, float scannerRange = 800f,
        Vector3? targetPos = null, float targetRadius = 10f, float targetSignature = 1f,
        float targetSideGa = 1f,
        VisibilityMath.SightBody[]? occluders = null,
        TargetVisibility.SideScanner[]? sideScanners = null)
    {
        return TargetVisibility.CanSeeShip(
            sameSide, unknown, notVisible, hasIdentity, ownSector, targetSector,
            ownPos ?? new Vector3(0f, 0f, 0f), ownNose ?? new Vector3(0f, 0f, -1f), ownRadius, scannerRange,
            targetPos ?? new Vector3(0f, 0f, -300f), targetRadius, targetSignature, targetSideGa,
            occluders ?? [], sideScanners);
    }

    [Fact]
    public void CanSee_ship_legs_match_native()
    {
        // shipIGC.h:383 — the same side is always seen.
        Assert.True(CanSee(sameSide: true, unknown: true, notVisible: true));
        // A roster-only ship (no EXPORT identity) has no client model — native's
        // ThingSite leg reads false (modelIGC.h:120-124).
        Assert.False(CanSee(unknown: true));
        Assert.False(CanSee(notVisible: true));
        Assert.False(CanSee(hasIdentity: false));
        // InScannerRange requires the same cluster (shipIGC.h:344-346).
        Assert.False(CanSee(targetSector: 11));
        // Within scanner range with line of sight → seen.
        Assert.True(CanSee());
        // Beyond scanner range → not (margin = 800·1/1 + radii < 2000).
        Assert.False(CanSee(targetPos: new Vector3(0f, 0f, -2000f)));
        // The native backward z-dilation (shipIGC.h:358-360): a target BEHIND
        // the own ship gets its delta z dilated 4× — 400 m behind is out of
        // reach (400·4 = 1600 > 820) while the same distance ahead is not.
        Assert.False(CanSee(targetPos: new Vector3(0f, 0f, 400f)));
        Assert.True(CanSee(targetPos: new Vector3(0f, 0f, -400f)));
    }

    [Fact]
    public void CanSee_respects_asteroid_occlusion()
    {
        // An asteroid blocking the ray (common.cpp:2324-2398) hides the target.
        Assert.False(CanSee(occluders: [Body(0f, 0f, -150f, 80f)]));
        // A rock off to the side does not.
        Assert.True(CanSee(occluders: [Body(500f, 0f, -150f, 20f)]));
    }

    // ── SeenBySide: the client's side scanners (trekigc.cpp:2057-2120) ─────

    private static TargetVisibility.SideScanner ShipScanner(float z, float scannerRange = 800f,
        Vector3? nose = null, float radius = 10f) =>
        new(new Vector3(0f, 0f, z), radius, scannerRange, IsShip: true, nose ?? new Vector3(0f, 0f, -1f));

    private static TargetVisibility.SideScanner StaticScanner(float z, float radius, float scannerRange) =>
        new(new Vector3(0f, 0f, z), radius, scannerRange, IsShip: false, default);

    [Fact]
    public void CanSee_side_leg_teammate_scanner_sees_what_the_own_ship_cannot()
    {
        // trekigc.cpp:2098-2113 — the hard-way branch: any scanner of the
        // client's side in the target's cluster that has the model in ITS
        // InScannerRange marks the side as seeing it. The enemy is 2000 m out
        // — far beyond the own ship's 800 m hull scanner — but 200 m ahead of
        // a teammate flying the same hull: hull/shield/speed must show.
        var far = new Vector3(0f, 0f, -2000f);
        Assert.False(CanSee(targetPos: far));   // own leg alone (pre-P786B result)
        Assert.True(CanSee(targetPos: far, sideScanners: [ShipScanner(-1800f)]));
    }

    [Fact]
    public void CanSee_side_leg_does_not_need_the_own_ship_in_the_target_sector()
    {
        // Claude review: SeenBySide (modelIGC.h:120-124, trekigc.cpp:2057-2120) is the side's
        // view, independent of where the own ship is — a target picked in another sector
        // (command view) is seen when a friendly scanner THERE sees it, and not otherwise.
        Assert.False(CanSee(ownSector: 10, targetSector: 11));
        Assert.True(CanSee(ownSector: 10, targetSector: 11, sideScanners: [ShipScanner(-100f)]));
        Assert.True(CanSee(ownSector: -1, targetSector: 11, sideScanners: [ShipScanner(-100f)]));   // own ship docked
        Assert.False(CanSee(ownSector: 10, targetSector: -1, sideScanners: [ShipScanner(-100f)]));   // target gone
    }

    [Fact]
    public void CanSee_side_leg_friendly_probe_scanner_is_a_plain_radial_test()
    {
        // probeigc.h:163-193 — a probe scanner runs the same margin over a
        // PLAIN radial test (probeigc.h:176), NOT the ship's local-frame
        // backward z-dilation (shipIGC.h:358-360). A friendly probe 700 m
        // "behind" the target (relative to the nose axis a ship would use)
        // still sees it — the same spot held by a ship scanner would not:
        // lz = −700 → dilated ×4 = 2800 > 820. (A station scanner,
        // stationIGC.h:308-333, shares this plain radial rule.)
        var far = new Vector3(0f, 0f, -2000f);
        var probe = StaticScanner(-2700f, radius: 5f, scannerRange: 1000f);
        var shipAtSameSpot = ShipScanner(-2700f, scannerRange: 1000f);
        Assert.True(CanSee(targetPos: far, sideScanners: [probe]));
        Assert.False(CanSee(targetPos: far, sideScanners: [shipAtSameSpot]));
    }

    [Fact]
    public void CanSee_side_leg_los_blocked_for_the_only_spotter_hides()
    {
        // The only spotter's LineOfSightExist (common.cpp:2324-2398) is
        // blocked by an asteroid → the side does NOT see the target.
        var far = new Vector3(0f, 0f, -2000f);
        Assert.False(CanSee(targetPos: far, sideScanners: [ShipScanner(-1800f)],
            occluders: [Body(0f, 0f, -1900f, 80f)]));
        // A rock off the teammate→target ray leaves the sighting intact.
        Assert.True(CanSee(targetPos: far, sideScanners: [ShipScanner(-1800f)],
            occluders: [Body(500f, 0f, -1900f, 20f)]));
    }

    [Fact]
    public void CanSee_hidden_outside_every_friendly_scanner()
    {
        // Outside the own scanner AND every friendly scanner in the cluster →
        // hidden, even with EXPORT identity (the pre-P786B code showed
        // hull/shield for any identified ship still present).
        var far = new Vector3(0f, 0f, -2000f);
        Assert.False(CanSee(targetPos: far,
            sideScanners: [ShipScanner(5000f), StaticScanner(3000f, 10f, 800f)]));
        // No scanner list at all (null) — the pre-P786B shape — also hidden.
        Assert.False(CanSee(targetPos: far, sideScanners: null));
    }

    // ── Target pane consumption (consoledata.cpp:202-277,449) ───────────────

    private const short Sector = 10;

    private static ServerShipUpdate Pose(short id, Vector3 pos) =>
        ServerShipUpdate.FromValues(id, 0, new ControlData(0f, 0f, 0f, 1f), 1u,
            pos, Orientation.Identity, new Vector3(0f, 0f, 0f), [0f, 0f, 0f], 0.7f, 0.6f, 1f, 1f, 1f, 0f, 1f);

    private static ClientWorld CanSeeWorld()
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster { SectorId = Sector, Ship = Pose(1, new Vector3(0f, 0f, 0f)) });
        w.Apply(new JoinedMission { ShipId = 1 });   // the own-ship identity CanSee reads
        // The server's own-ship dummy EXPORT (ShipId NA) is the only wire source
        // of the player's own side (Apply(ExportShip)) — the fixture's side seed.
        w.Apply(new ExportShip { ShipId = -1, SideId = 1 });
        w.Apply(new ExportCluster
        {
            Cluster = new DataClusterIGC(0, [0f, 1f, 0f], 0, 0f, 0f, Sector, 0, 0, "Alystra", "", "", 0, 0, 0, true, false),
        });
        // Own ship (side 1, hull 999) + enemy (side 2, hull 998) 300 m ahead.
        w.Apply(new ExportShip { ShipId = 1, SideId = 1, CharacterName = "me", HullId = 999 });
        w.Apply(new ExportShip { ShipId = 2, SideId = 2, CharacterName = "foe", HullId = 998 });
        w.Apply(new SingleShipUpdate { OidTarget = 1, Ship = Pose(1, new Vector3(0f, 0f, 0f)) });
        w.Apply(new SingleShipUpdate { OidTarget = 2, Ship = Pose(2, new Vector3(0f, 0f, -300f)) });
        // SHIP_STATUS(90) carries the last-seen HullID (native ShipStatus
        // m_hullID → PlayerInfo::LastSeenShipType) — the own and enemy hulls.
        w.Apply(new ShipStatusMessage { ShipId = 1, SectorId = Sector, StationId = -1, HullId = 999 });
        w.Apply(new ShipStatusMessage { ShipId = 2, SectorId = Sector, StationId = -1, HullId = 998 });
        return w;
    }

    private static IReadOnlyDictionary<short, DataHullTypeIGC> Hulls() => new Dictionary<short, DataHullTypeIGC>
    {
        [999] = Hull(signature: 200f, scannerRange: 800f),
        [998] = Hull(signature: 1f, scannerRange: 800f) with { HullId = 998, Name = "Enemy Fig" },
    };

    [Fact]
    public void Target_hull_and_shield_follow_the_scanner_gate()
    {
        var w = CanSeeWorld();
        var pane = TargetPaneModel.Build(w, IgcObjectType.Ship, 2, hullTypes: Hulls());
        // 300 m inside an 800·1 + radii scanner range → native CanSee true, so
        // the hull (0.7) and shield (0.6) read.
        Assert.Equal(0.7f, pane.HullFraction!.Value, 3);
        Assert.Equal(0.6f, pane.ShieldFraction!.Value, 3);

        // Park the enemy beyond scanner range: consoledata.cpp:202-212 and
        // :252-270 zero both (the port formerly streamed them because the ship
        // is still in the sector).
        w.Apply(new SingleShipUpdate { OidTarget = 2, Ship = Pose(2, new Vector3(0f, 0f, -2000f)) });
        var far = TargetPaneModel.Build(w, IgcObjectType.Ship, 2, hullTypes: Hulls());
        Assert.Equal(0f, far.HullFraction!.Value, 3);
        Assert.Equal(0f, far.ShieldFraction!.Value, 3);
        // Speed reads -1 for an unseen target (consoledata.cpp:136-150).
        Assert.Equal(-1f, far.Speed!.Value, 3);
    }

    [Fact]
    public void Target_ore_reads_native_shipload_fill_units()
    {
        var w = CanSeeWorld();
        // An He3 rock at 30% of 300 ore (= 90 ore = 1 shipload of 90): native
        // fill = oreSeen/(2·capacity) = 0.5 (radarimage.cpp:1051-1069).
        w.Apply(new ExportAsteroid
        {
            Asteroid = new DataAsteroidIGC(
                Signature: 1f, Position: [0f, 0f, -200f], Up: [0f, 1f, 0f], Forward: [0f, 0f, 1f],
                Rotation: [0f, 0f, 0f, 0f], Ore: 90f, OreMax: 300f, AabmCapabilities: 1,
                AsteroidId: 20, HitPoints: 1000f, Radius: 50, ModelName: "rock", TextureName: "",
                IconName: "ico", ClusterId: Sector, Name: "A He3", Fraction: 240),
        });
        w.Apply(new AsteroidsUpdate
        {
            Entries = [new AsteroidStateEntry(20, CompactOrientation.Identity, 90, 240)],
        });

        var pane = TargetPaneModel.Build(w, IgcObjectType.Asteroid, 20, hullTypes: Hulls(),
            minerCapacityHe3: 90f);
        Assert.Equal(0.5f, pane.OreFraction!.Value, 3);
    }

    // ── Target pane side leg (trekigc.cpp:2057-2120 → consoledata CanSee) ──

    private static HeavyShipsUpdate TeammateTelemetry(short shipId, Vector3 pos)
    {
        uint t = 100u;
        var entry = HeavyShipUpdateEntry.From(
            t, pos, shipId, stateM: 0, new ControlData(0f, 0f, 0f, 1f),
            t, pos, Orientation.Identity, new Vector3(0f, 0f, 0f),
            new[] { 0f, 0f, 0f }, hullFrac: 1f, shieldFrac: 1f, oreFrac: 0f);
        // CompactPosition is a signed-sqrt DELTA from PositionReference
        // (igc.h:2198-2240, ~518-unit reach) — the server uses the recipient's
        // own position as the reference, so the test pins the same ship here.
        return new HeavyShipsUpdate
        {
            TimeReference = t, PositionReference = pos,
            HeavyShipEntries = [entry], BpTargetHull = 255, BpTargetShield = 255,
        };
    }

    private static DataProbeTypeIGC ProbeType(short id, float radius, float scannerRange) => new(
        Color: [], Radius: radius, Rotation: 0f, ModelName: "", TextureName: "",
        LoadTime: 0f, Lifespan: 60f, Signature: 0f, Price: 0, TimeToBuild: 0,
        LauncherModelName: "", LauncherIconName: "", LauncherName: "", LauncherDescription: "",
        LauncherGroupId: 0, LauncherTtbmRequired: [], LauncherTtbmEffects: [],
        LauncherSignature: 0f, LauncherMass: 0f, LauncherPartMask: 0, LauncherExpendableSize: 0,
        HitPoints: 100f, DefenseType: 0, ExpendableTypeId: id, EabmCapabilities: 0, IconName: "",
        ScannerRange: scannerRange, DtimeBurst: 0f, Dispersion: 0f, Accuracy: 0f,
        Ammo: 0, ProjectileTypeId: 0, AmbientSound: 0, DtRipcord: 0f);

    [Fact]
    public void Target_hull_shows_when_a_teammate_scanner_sees_the_enemy()
    {
        var w = CanSeeWorld();
        // Park the enemy 2000 m out: outside the own ship's 800 m scanner →
        // the pane hides hull/shield (the pre-P786B regression the side leg
        // fixes for the TEAMMATE case below).
        w.Apply(new SingleShipUpdate { OidTarget = 2, Ship = Pose(2, new Vector3(0f, 0f, -2000f)) });
        var hidden = TargetPaneModel.Build(w, IgcObjectType.Ship, 2, hullTypes: Hulls());
        Assert.Equal(0f, hidden.HullFraction!.Value, 3);

        // A side-1 teammate flies 200 m in front of the enemy (hull 999 →
        // 800 m scanner): native's client-side SeenBySide (trekigc.cpp:2098-2113)
        // marks the side as seeing it → consoledata.cpp:202-212/252-270 read.
        w.Apply(new ExportShip { ShipId = 3, SideId = 1, CharacterName = "wing", HullId = 999 });
        w.Apply(TeammateTelemetry(3, new Vector3(0f, 0f, -1800f)));
        var seen = TargetPaneModel.Build(w, IgcObjectType.Ship, 2, hullTypes: Hulls());
        Assert.Equal(0.7f, seen.HullFraction!.Value, 3);
        Assert.Equal(0.6f, seen.ShieldFraction!.Value, 3);
    }

    [Fact]
    public void Target_hull_shows_when_a_friendly_probe_scanner_sees_the_enemy()
    {
        var w = CanSeeWorld();
        w.Apply(new SingleShipUpdate { OidTarget = 2, Ship = Pose(2, new Vector3(0f, 0f, -2000f)) });
        // A side-1 probe sits 700 m "behind" the enemy on the nose axis — a
        // ship scanner there would be dilated out (see the probe rule test);
        // probeigc.h:176 is a plain radial: 700 < 5 + 10 + 1000·1.
        w.Apply(new ExportProbe
        {
            Probe = new DataProbeExport(
                P0: new Vector3(0f, 0f, -2700f), Time0: 0, ProbeId: 5, ExportF: true,
                ProbetypeId: 7, SideId: 1, ClusterId: Sector, ShipId: 1,
                OtTarget: -1, OidTarget: -1, CreateNow: true),
        });
        var probeTypes = new Dictionary<short, DataProbeTypeIGC> { [7] = ProbeType(7, radius: 5f, scannerRange: 1000f) };
        var seen = TargetPaneModel.Build(w, IgcObjectType.Ship, 2, hullTypes: Hulls(), probeTypes: probeTypes);
        Assert.Equal(0.7f, seen.HullFraction!.Value, 3);

        // Without the probe-type catalogue the probe's scanner range is
        // unknowable and it cannot spot for the side.
        var noCatalog = TargetPaneModel.Build(w, IgcObjectType.Ship, 2, hullTypes: Hulls());
        Assert.Equal(0f, noCatalog.HullFraction!.Value, 3);
    }
}
