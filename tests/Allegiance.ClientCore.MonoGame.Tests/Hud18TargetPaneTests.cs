using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-18 — target-pane fidelity: the client-side weapon-range flag
/// (<c>WinTrek/indicator.cpp:183-228</c>), the LOCATION: sector fallback
/// (<c>Artwork/dialog.mdl:4053-4060</c>, <c>WinTrek/consoledata.cpp:70-87</c>)
/// and the GlassHUD time-to-intercept text (<c>Artwork/dialog.mdl:1280-1355</c>).
/// Pure rules only — no GraphicsDevice (`TargetWeaponRange`, `TargetTti`,
/// `TargetPaneModel`, `SelectedMountWeapon`).
/// </summary>
public class Hud18TargetPaneTests
{
    private const short Sector = 10;
    private const short OtherSector = 11;

    // ── TargetWeaponRange decision table ────────────────────────────────────

    /// <summary>
    /// Base input: own ship at the origin facing -Z (Orientation.Identity),
    /// target straight ahead at <paramref name="targetDistance"/> metres, a
    /// damaging absolute-energy projectile with a 0.5 s lifespan at 1200 m/s
    /// (native pulse-laser-like numbers). Every case flips exactly one input.
    /// </summary>
    private static TargetWeaponRangeInput Input(
        bool sameCluster = true,
        bool canSee = true,
        bool hasWeapon = true,
        bool chaseCamera = false,
        bool friendlyOrAllied = false,
        float power = 10f,
        bool absoluteF = true,
        bool ammoPerShot = false,
        bool energyWeapon = false,
        float speed = 1200f,
        float lifespan = 0.5f,
        float speedAmmoGa = 1f,
        float lifespanEnergyGa = 1f,
        float targetRadius = 0f,
        float targetDistance = 500f,
        float myVelocityZ = 0f,
        float targetVelocityZ = 0f) =>
        new(
            SameCluster: sameCluster,
            CanSee: canSee,
            HasWeapon: hasWeapon,
            ChaseCamera: chaseCamera,
            FriendlyOrAllied: friendlyOrAllied,
            ProjectilePower: power,
            AbsoluteF: absoluteF,
            AmmoPerShot: ammoPerShot,
            EnergyWeapon: energyWeapon,
            ProjectileSpeed: speed,
            ProjectileLifespan: lifespan,
            SpeedAmmoGa: speedAmmoGa,
            LifespanEnergyGa: lifespanEnergyGa,
            MyPosition: default,
            MyOrientation: Orientation.Identity,
            MyVelocity: new Vector3(0f, 0f, myVelocityZ),
            MountPosition: default,
            TargetRadius: targetRadius,
            TargetPosition: new Vector3(0f, 0f, -targetDistance),
            TargetVelocity: new Vector3(0f, 0f, targetVelocityZ));

    [Fact]
    public void Damaging_weapon_on_a_sideless_target_in_range_is_true()
    {
        // The pane's former flag only ever existed for SHIP targets
        // (Server.Broadcast.cs:3206-3250); an asteroid/aleph has no side, so
        // FriendlyOrAllied is false and a damaging weapon applies
        // (indicator.cpp:217). t = 500/1200 = 0.417 <= 0.5.
        Assert.True(TargetWeaponRange.IsInRange(Input()));

        // Out of weapon range: 700/1200 = 0.583 > 0.5.
        Assert.False(TargetWeaponRange.IsInRange(Input(targetDistance: 700f)));
    }

    [Fact]
    public void Flag_ignores_any_lead_indicator_capability()
    {
        // There is deliberately no capability input: native sets the in-range
        // flag at indicator.cpp:221-228 BEFORE the c_habmLeadIndicator/relay
        // gate at :286-320. The model likewise keeps an explicitly supplied
        // flag even when no lead message exists at all.
        var w = World();
        AddStation(w, 100, Sector, -1, (300f, 0f, 0f));
        Assert.False(w.LeadIndicator.HasLead);   // no server lead message
        var pane = TargetPaneModel.Build(w, IgcObjectType.Station, 100, inRange: true);
        Assert.True(pane.InRange);
        Assert.False(pane.IsShip);
    }

    [Theory]
    // Healing weapon (Power < 0) on an enemy → false; on a friend/ally → true;
    // in the chase camera the friendly test is skipped entirely
    // (indicator.cpp:214-218).
    [InlineData(true, false, -1f, true)]
    [InlineData(true, true, -1f, true)]
    [InlineData(false, true, -1f, true)]
    // Damaging weapon on a friend → false; chase camera → true.
    [InlineData(true, false, 10f, false)]
    [InlineData(true, true, 10f, true)]
    // Damaging weapon on a non-friend → true; healing on a non-friend → false.
    [InlineData(false, false, 10f, true)]
    [InlineData(false, false, -1f, false)]
    public void Healing_and_friendly_gate_matches_indicator_cpp(
        bool friendly, bool chaseCamera, float power, bool expected)
    {
        Assert.Equal(expected, TargetWeaponRange.IsInRange(
            Input(friendlyOrAllied: friendly, chaseCamera: chaseCamera, power: power)));
    }

    [Fact]
    public void Same_cluster_and_can_see_are_required()
    {
        Assert.True(TargetWeaponRange.IsInRange(Input()));
        Assert.False(TargetWeaponRange.IsInRange(Input(sameCluster: false)));
        Assert.False(TargetWeaponRange.IsInRange(Input(canSee: false)));
        Assert.False(TargetWeaponRange.IsInRange(Input(hasWeapon: false)));
    }

    [Fact]
    public void Ammo_weapon_speed_is_scaled_by_the_speed_ammo_ga()
    {
        // 700 m at 600 m/s = 1.167 s > 1.0 s lifespan (no GA).
        var ammo = Input(ammoPerShot: true, absoluteF: true, speed: 600f, lifespan: 1f,
            targetDistance: 700f, speedAmmoGa: 1f);
        Assert.False(TargetWeaponRange.IsInRange(ammo));

        // Same shot with c_gaSpeedAmmo = 1.5 → 900 m/s → 0.778 s <= 1.0 s.
        Assert.True(TargetWeaponRange.IsInRange(ammo with { SpeedAmmoGa = 1.5f }));

        // An ENERGY weapon never reads c_gaSpeedAmmo (common.cpp:141 tests
        // GetAmmoPerShot(), weaponIGC.h:165-171 tests energyPerShot).
        Assert.False(TargetWeaponRange.IsInRange(new TargetWeaponRangeInput(
            SameCluster: true, CanSee: true, HasWeapon: true, ChaseCamera: false,
            FriendlyOrAllied: false, ProjectilePower: 10f, AbsoluteF: true,
            AmmoPerShot: false, EnergyWeapon: true, ProjectileSpeed: 600f,
            ProjectileLifespan: 1f, SpeedAmmoGa: 10f, LifespanEnergyGa: 1f,
            MyPosition: default, MyOrientation: Orientation.Identity,
            MyVelocity: default, MountPosition: default, TargetRadius: 0f,
            TargetPosition: new Vector3(0f, 0f, -700f), TargetVelocity: default)));
    }

    [Fact]
    public void Energy_weapon_lifespan_is_scaled_by_the_lifespan_energy_ga()
    {
        // 700 m at 1200 m/s = 0.583 s > 0.5 s lifespan (no GA).
        var energy = Input(energyWeapon: true, speed: 1200f, lifespan: 0.5f,
            targetDistance: 700f, lifespanEnergyGa: 1f);
        Assert.False(TargetWeaponRange.IsInRange(energy));

        // c_gaLifespanEnergy = 1.2 → 0.6 s lifespan → in range.
        Assert.True(TargetWeaponRange.IsInRange(energy with { LifespanEnergyGa = 1.2f }));

        // An AMMO weapon never reads c_gaLifespanEnergy.
        Assert.False(TargetWeaponRange.IsInRange(
            energy with { EnergyWeapon = false, AmmoPerShot = true, LifespanEnergyGa = 10f }));
    }

    [Fact]
    public void Absolute_f_uses_only_the_target_velocity()
    {
        // Own ship closing at 500 m/s, target static at 1000 m, 600 m/s
        // projectile, 1 s lifespan.
        var absolute = Input(speed: 600f, lifespan: 1f, targetDistance: 1000f,
            myVelocityZ: -500f);
        // absoluteF: deltaV = target velocity = 0 → t = 1000/600 = 1.667 > 1.
        Assert.False(TargetWeaponRange.IsInRange(absolute));
        // Relative: deltaV = -myVelocity → closing 1100 m/s → t = 0.909 <= 1.
        Assert.True(TargetWeaponRange.IsInRange(absolute with { AbsoluteF = false }));

        // And the target's own velocity is what absoluteF substitutes in.
        // Target receding at +200 (away from us) → t = 1000/(600-200)=2.5.
        Assert.False(TargetWeaponRange.IsInRange(absolute with { TargetVelocity = new Vector3(0f, 0f, 200f) }));

        // With a follow (non-absolute) projectile the own velocity is
        // subtracted too: deltaV = (0,0,200) - (0,0,-500) = (0,0,700), so the
        // solve sees a 700 m/s relative closing on a 600 m/s projectile and
        // t = 1000/1300 = 0.769 <= 1 → in range. The point pinned here is that
        // absoluteF never sees my velocity, while the relative branch does.
        Assert.True(TargetWeaponRange.IsInRange(
            absolute with { TargetVelocity = new Vector3(0f, 0f, 200f), AbsoluteF = false }));
    }

    [Fact]
    public void No_solution_sentinel_is_out_of_range()
    {
        // Positive baseline so this test cannot pass on a blanket "false".
        Assert.True(TargetWeaponRange.IsInRange(Input()));
        // deltaV == 0 and speed == 0 makes a == 0 in solveForImpact, which
        // returns the native no-solution sentinel FLT_MAX/500000000
        // (Igc/common.cpp:86) — far above any real weapon lifespan.
        float sentinel = NavMath.SolveForImpact(new Vector3(0f, 0f, -500f), default, 0f, 0f, out _);
        Assert.True(sentinel > 1e28f);
        // NB: sentinel - 1f == sentinel at this magnitude (float ULP is ~5e22),
        // so half the sentinel is the meaningful "below the sentinel" value.
        Assert.False(TargetWeaponRange.IsInRange(Input(speed: 0f, lifespan: sentinel * 0.5f)));
        // And every real lifespan (pulse laser 0.5 s here) is far below it.
        Assert.False(TargetWeaponRange.IsInRange(Input(speed: 0f, lifespan: 0.5f)));
    }

    [Fact]
    public void Selected_mount_not_mount_zero_decides()
    {
        // Native GetWeapon() = GetMountedPart(ET_Weapon, m_selectedWeapon)
        // (clintlib/clintlib.cpp:1585-1588).
        DataPartTypeIGC Part(short id, DataPartTypeIGC.Weapon weapon) => new(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: $"part {id}",
            Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f,
            Signature: 0f, PartId: id, SuccessorPartId: -1, PartMask: 0,
            InventoryLineMdl: "", Type: weapon);
        var mountZero = new DataPartTypeIGC.Weapon(0f, 0f, 0f, 0f, 1, 1, -1, -1, -1);
        var mountOne = new DataPartTypeIGC.Weapon(0f, 0f, 6.5f, 0f, 0, 66, -1, -1, -1);
        var parts = new[] { Part(10, mountZero), Part(11, mountOne) };
        IReadOnlyList<ExpandedPartData> loadout =
        [
            new ExpandedPartData(10, 100, 0, ExpandedPartData.FullyMounted),
            new ExpandedPartData(11, 100, 1, ExpandedPartData.FullyMounted),
        ];

        Assert.Equal((short)1, SelectedMountWeapon.Resolve(loadout, parts, 0)!.ProjectileTypeId);
        Assert.Equal((short)66, SelectedMountWeapon.Resolve(loadout, parts, 1)!.ProjectileTypeId);
        Assert.Null(SelectedMountWeapon.Resolve(loadout, parts, 2));
        Assert.Null(SelectedMountWeapon.Resolve(null, parts, 0));
    }

    // ── TargetTti golden cases ──────────────────────────────────────────────

    [Fact]
    public void Tti_golden_cases_from_the_mdl_arithmetic()
    {
        // diff 0 → outer SwitchString(0) → "infinite" (dialog.mdl:1319,1325).
        Assert.Equal("infinite", TargetTti.Text(500f, 100f, 100f));
        // |allseconds| >= 1800 → Min(abs,1800) == 1800 → "infinite".
        Assert.Equal("infinite", TargetTti.Text(180_000f, 100f, 0f));
        // 7500 / (100 - 0) = 75 s → mod 15, minutes (75-15)/60 = 1, ":"+15.
        Assert.Equal("1:15", TargetTti.Text(7500f, 100f, 0f));
        // 500 / 100 = 5 s → max(9.4999,5) == 9.4999 → ":0"+NumberString(5).
        Assert.Equal("0:05", TargetTti.Text(500f, 100f, 0f));
        // 960 / 100 = 9.6 s → Max(9.4999,9.6)=9.6 ≠ 9.4999 →
        // ":"+NumberString(9.6) = (int)(9.6+0.5) = 10 → "0:10".
        Assert.Equal("0:10", TargetTti.Text(960f, 100f, 0f));
        // Receding: 7500 / (0 - 100) = -75 → abs 75 → 1:15, then
        // Max(0,-75)=0 → parenthesised → "(1:15)".
        Assert.Equal("(1:15)", TargetTti.Text(7500f, 0f, 100f));
        // Range -1 (another sector) with a positive speed difference:
        // -1/100 = -0.01 → abs .01, mod .01, minutes 0, seconds .01 →
        // Max(9.4999,.01)==9.4999 → ":0"+NumberString(.01)= ":00" → "0:00";
        // Max(0,-0.01)==0 → "(0:00)". Native prints the same tiny-value text.
        Assert.Equal("(0:00)", TargetTti.Text(-1f, 100f, 0f));
        Assert.Equal("0:00", TargetTti.Text(-1f, 0f, 100f));
        // Target speed -1 (unseen dynamic): 500 / (100 - (-1)) = 4.95 s → "0:05".
        Assert.Equal("0:05", TargetTti.Text(500f, 100f, -1f));
        // 59.7 s → mod 59.7, min(mod,59) = 59, minutes (59.7-59.7)/60 = 0 →
        // Max(9.4999,59)=59 ≠ 9.4999 → ":"+NumberString(59) = ":59" → "0:59".
        Assert.Equal("0:59", TargetTti.Text(5970f, 100f, 0f));
        // 1799 s is still below the 1800 cap: 29:59.
        Assert.Equal("29:59", TargetTti.Text(179_900f, 100f, 0f));
    }

    [Fact]
    public void Steady_in_range_and_tti_paths_allocate_nothing()
    {
        var input = Input(targetDistance: 500f);
        // Warm the JIT + TargetTti's string cache first.
        for (int i = 0; i < 64; i++)
        {
            TargetWeaponRange.IsInRange(input);
            TargetTti.Text(7500f, 100f, 0f);
        }

        bool inRange = false;
        string? tti = null;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 1000; frame++)
        {
            inRange = TargetWeaponRange.IsInRange(input);
            tti = TargetTti.Text(7500f, 100f, 0f);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(inRange);
        Assert.Equal("1:15", tti);
        Assert.Equal(0, allocated);
    }

    // ── TargetPaneModel: LOCATION: sector fallback + TTI ────────────────────

    private static ServerShipUpdate Pose(short id, (float x, float y, float z) pos,
        (float x, float y, float z) vel, float hull = 1f, float shield = 1f) =>
        ServerShipUpdate.FromValues(id, 0, new ControlData(0f, 0f, 0f, 1f), 1u,
            new Vector3(pos.x, pos.y, pos.z), Orientation.Identity,
            new Vector3(vel.x, vel.y, vel.z), [0f, 0f, 0f], hull, shield, 1f, 1f, 1f, 0f, 1f);

    private static ClientWorld World()
    {
        var w = new ClientWorld();
        w.Apply(new SetCluster { SectorId = Sector, Ship = Pose(1, (0f, 0f, 0f), (0f, 0f, 0f)) });
        AddCluster(w, Sector, "Alystra");
        AddCluster(w, OtherSector, "Ganymede");
        return w;
    }

    private static void AddCluster(ClientWorld w, short id, string name) =>
        w.Apply(new ExportCluster
        {
            Cluster = new DataClusterIGC(0, [0f, 1f, 0f], 0, 0f, 0f, id, 0, 0,
                name, "", "", 0, 0, 0, true, false),
        });

    private static void AddShip(ClientWorld w, short id, short sector,
        (float x, float y, float z) pos, (float x, float y, float z) vel, short side = 1)
    {
        w.Apply(new SingleShipUpdate { OidTarget = id, Ship = Pose(id, pos, vel) });
        w.Apply(new ShipStatusMessage { ShipId = id, SectorId = sector, StationId = -1 });
        w.Apply(new ExportShip { ShipId = id, SideId = side, CharacterName = $"ship {id}", HullId = 501 });
    }

    private static void AddStation(ClientWorld w, short id, short cluster, short side,
        (float x, float y, float z) pos)
    {
        w.Apply(new ExportStation
        {
            Station = new DataStationIGC(
                [pos.x, pos.y, pos.z], [0f, 1f, 0f], [0f, 0f, 1f], [0f, 0f, 0f, 0f],
                cluster, side, id, 1, 255, 255, $"station {id}"),
        });
    }

    [Fact]
    public void Other_sector_target_gets_the_location_sector_name()
    {
        var w = World();
        AddStation(w, 101, OtherSector, -1, (900f, 0f, 0f));

        var pane = TargetPaneModel.Build(w, IgcObjectType.Station, 101);
        Assert.Null(pane.Distance);
        // Native GetSectorName falls back to the model's cluster for a known
        // static (consoledata.cpp:70-87); RANGE would be -1 here.
        Assert.Equal("Ganymede", pane.SectorName);
    }

    [Fact]
    public void Hidden_enemy_ship_uses_its_last_ship_status_sector()
    {
        var w = World();
        AddShip(w, 400, OtherSector, (900f, 0f, 0f), (0f, 0f, 0f), side: 2);
        // Out of view / other sector: the last SHIP_STATUS sector is the
        // native PlayerInfo::LastSeenSector() fallback (consoledata.cpp:76-85).
        w.Apply(ShipDelete.ForShipLeftSector(400));

        var pane = TargetPaneModel.Build(w, IgcObjectType.Ship, 400);
        Assert.Null(pane.Distance);
        Assert.Equal("Ganymede", pane.SectorName);
    }

    [Fact]
    public void Ship_never_statused_reports_unknown_not_the_default_sector_zero()
    {
        var w = World();
        // EXPORT(26) alone: no SHIP_STATUS(90), no SET_CLUSTER, so SectorId is
        // still the C# default 0 — which IS a real cluster id in some missions
        // (Alystra here). The status-sector-known flag must keep it "unknown".
        w.Apply(new ExportShip { ShipId = 500, SideId = 2, CharacterName = "ghost", HullId = 501 });
        Assert.False(w.Ships[500].StatusSectorKnown);

        var pane = TargetPaneModel.Build(w, IgcObjectType.Ship, 500);
        Assert.Null(pane.Distance);
        Assert.Equal("unknown", pane.SectorName);
    }

    [Fact]
    public void Same_cluster_target_keeps_range_and_has_no_sector_text()
    {
        var w = World();
        AddStation(w, 100, Sector, -1, (300f, 0f, 0f));

        var pane = TargetPaneModel.Build(w, IgcObjectType.Station, 100, ownSpeed: 100f);
        Assert.NotNull(pane.Distance);
        Assert.Equal(300.0, pane.Distance!.Value, 1);
        Assert.Null(pane.SectorName);
        // TTI uses the same-cluster range and the scalar speeds: 300/100 = 3 s.
        Assert.Equal("0:03", pane.TimeToIntercept);
    }

    [Fact]
    public void Tti_is_null_while_docked()
    {
        var w = World();
        AddStation(w, 100, Sector, -1, (300f, 0f, 0f));
        // Native SwitchImage(GetPercentHitPoints(MyStation) != 0, ...) hides
        // the whole TTI group while docked (dialog.mdl:1327-1330).
        w.Apply(new ShipStatusMessage
        {
            ShipId = 1, SectorId = Sector, StationId = 100, State = ShipStatusMessage.StateDocked,
        });

        var pane = TargetPaneModel.Build(w, IgcObjectType.Station, 100, ownSpeed: 100f);
        Assert.Null(pane.TimeToIntercept);

        // Undocked again: 300 m / 100 mps = 3 s, so the group comes back.
        w.Apply(new ShipStatusMessage
        {
            ShipId = 1, SectorId = Sector, StationId = -1, State = ShipStatusMessage.StateFlying,
        });
        var flying = TargetPaneModel.Build(w, IgcObjectType.Station, 100, ownSpeed: 100f);
        Assert.Equal("0:03", flying.TimeToIntercept);
    }

    [Fact]
    public void Hud_elements_report_location_and_tti_rows()
    {
        var frame = new HudFrame(0.8f, 0.6f, 0.4f, 0.3f, 0.2f, 1f,
            120f, -1f, false, false, true, 0, false, 0f, false, false,
            0f, 0, 0f, Array.Empty<WeaponStatusRow>(), false, 0);
        var pane = new HudTargetPane(true, "station 101", "Station", null,
            TargetPaneModel.Neutral, 0f, 0f, null, 0f, null, null, false, false,
            SectorName: "Ganymede", TimeToIntercept: "0:05");
        var model = HudModel.Create(frame, new() { TargetPane = pane });

        var range = model.Elements.Single(e => e.Id == "Target range or sector");
        Assert.True(range.Available);
        Assert.True(range.Visible);
        Assert.Equal("LOCATION:Ganymede", range.Text);   // no space after the colon
        Assert.Null(range.Value);

        var tti = model.Elements.Single(e => e.Id == "Target time to intercept");
        Assert.True(tti.Available);
        Assert.True(tti.Visible);
        Assert.Equal("TTI: 0:05", tti.Text);
        Assert.Equal(new HudPlacement(HudAnchor.Bottom, 242f, 10f, true), tti.Placement);
    }
}
