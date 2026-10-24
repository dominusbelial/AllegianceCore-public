using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P787a — the launcher auto-reload on empty (the port of native
/// <c>BaseClient::Reload</c>'s ET_Magazine/ET_Dispenser/ET_ChaffLauncher
/// branch, <c>clintlib.cpp:3560-3667</c>), executed server-side at the native
/// trigger (the fire that empties the launcher). When a fire drives a missile /
/// chaff / mine / probe launcher to exactly zero, the ship pulls a spare
/// launcher of the same equipment type from its own cargo hold through the
/// EXISTING P192 per-entry applicator (<see cref="Ship.TryApplyReloadEntry"/>),
/// with native's selection rules: an empty launcher takes any mountable part of
/// that equipment type (dropping itself), otherwise it tops up from the same
/// part type. Success surfaces via <see cref="IIgcSite.AutoReloadTriggered"/>
/// (the server relays RELOAD(129)); failure drops the empty launcher and raises
/// <see cref="IIgcSite.LauncherReloadFailed"/> (the server relays DROP_PART(33)).
/// </summary>
[Collection("LiveNetwork")]
public class P787aLauncherReloadTests
{
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    private static IReadOnlyList<SideSpec> SideSpecs()
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        return [new SideSpec(0, "Team 1", civ.CivilizationId)];
    }

    private static RecordingIgcSite NewSite() => new();

    private static Mission NewStartedMission(out RecordingIgcSite site)
    {
        site = NewSite();
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, SideSpecs(), site);
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>A combat hull that can carry real launcher quantities. The hull's
    /// stock capacities are forced high so a mounted/spare launcher amount is not
    /// clamped below the test's expectations (launchertypeigc.h:130-145).</summary>
    private static DataHullTypeIGC CapaciousHull(StaticCore core) =>
        core.HullTypes.Values.First() with
        {
            CapacityMagazine = 200,
            CapacityDispenser = 200,
            CapacityChaffLauncher = 200,
        };

    private static Ship NewShip(Mission m, RecordingIgcSite site)
    {
        var ship = new Ship(901, CapaciousHull(m.Core), new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity)
        {
            Mission = m,
            SideId = 0,
        };
        m.PlaceShip(ship, m.Sectors[0]);
        return ship;
    }

    // ── part factories (construction-only launcher subtypes, no corpus record) ──
    private static DataPartTypeIGC MissileLauncherPart(StaticCore core, DataMissileTypeIGC mt, short partId)
        => new(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ml", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: partId, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.MissileLauncher(mt.ExpendableTypeId));

    private static DataPartTypeIGC ChaffLauncherPart(StaticCore core, DataChaffTypeIGC ct, short partId)
        => new(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "cl", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: partId, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.ChaffLauncher(ct.ExpendableTypeId));

    private static DataPartTypeIGC MineLauncherPart(StaticCore core, DataMineTypeIGC mt, short partId)
        => new(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "dl", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: partId, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.MineLauncher(mt.ExpendableTypeId));

    // A dumbfire missile (MaxLock == 0) fires with no command target / lock,
    // so the fire→empty→reload path is exercised with minimal fixture.
    private static DataMissileTypeIGC DumbfireMissile(StaticCore core) =>
        core.MissileTypes.Values
            .Where(m => m.MaxLock == 0f && m.InitialSpeed > 0f && m.Lifespan >= 1f)
            .OrderBy(m => m.ExpendableTypeId).First();

    private static DataMissileTypeIGC LockingMissile(StaticCore core) =>
        core.MissileTypes.Values
            .Where(m => m.MaxLock > 0f && m.InitialSpeed > 0f)
            .OrderBy(m => m.ExpendableTypeId).First();

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: firing a missile magazine down to exactly
    /// zero with a spare missile launcher of the SAME type in cargo auto-reloads
    /// (tops up) from the spare and raises AutoReloadTriggered so the server can
    /// relay RELOAD(129) — native FireMissile's reload-success leg
    /// (clintlib.cpp:3206-3228, BaseClient::Reload clintlib.cpp:3560-3667).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Firing_missiles_to_zero_top_up_from_same_type_cargo_spare()
    {
        Require.Artwork();
        var mission = NewStartedMission(out var site);
        var ship = NewShip(mission, site);
        var core = mission.Core;
        var mt = DumbfireMissile(core);

        ship.MountPart(MissileLauncherPart(core, mt, 710), 1, 0, 255);          // 1 round
        Assert.True(ship.TryMountCargoPart(MissileLauncherPart(core, mt, 712), 3, -1, 0)); // spare, 3 rounds

        bool fired = ship.TryFireMissile(mission.LastUpdate, clientRequest: true, requestedLock: 0f);
        Assert.True(fired, "the loaded launcher must fire");

        Assert.Equal(AutoReloadOutcome.Reloaded, ship.LastLauncherAutoReloadOutcome);
        var mag = ship.Parts.Single(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0);
        Assert.True(mag.Amount >= 3, $"the magazine must be topped up from the spare (got {mag.Amount})");
        Assert.Null(ship.CargoHold[Ship.CargoSlotForMount(-1)]);   // spare consumed entirely
        // The success leg rides AutoReloadTriggered (server relays RELOAD(129)).
        Assert.Contains(site.Calls, c => c.StartsWith("AutoReloadTriggered("));
    }

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: an empty missile launcher with NO spare of
    /// its equipment type in cargo is DROPPED the native way (empty launcher →
    /// plauncher->Terminate() + FM_CS_DROP_PART, clintlib.cpp:3685-3695) and the
    /// failure is surfaced via LauncherReloadFailed so the server can relay
    /// DROP_PART(33) and the own-ship client plays "Missiles depleted."
    /// (clintlib.cpp:3223-3226).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Firing_missiles_to_zero_with_no_cargo_spare_drops_the_empty_launcher()
    {
        Require.Artwork();
        var mission = NewStartedMission(out var site);
        var ship = NewShip(mission, site);
        var core = mission.Core;
        var mt = DumbfireMissile(core);

        ship.MountPart(MissileLauncherPart(core, mt, 714), 1, 0, 255);
        // NOTE: no spare in the cargo hold.

        bool fired = ship.TryFireMissile(mission.LastUpdate, clientRequest: true, requestedLock: 0f);
        Assert.True(fired);

        Assert.Equal(AutoReloadOutcome.Depleted, ship.LastLauncherAutoReloadOutcome);
        // The empty launcher is gone (native terminates it on reload failure).
        Assert.Null(ship.Parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0));
        Assert.Contains(site.Calls, c => c.StartsWith("LauncherReloadFailed("));
    }

    /// <summary>⭐ P787a scope 5 (bots) — the reload triggers from the AUTHORITATIVE
    /// sim fire path used by NPCs/bots too (clientRequest == false, the
    /// magazineigc.cpp:85-120 load-ready gate), not just the player wire path, so
    /// missile/chaff bots refill automatically with no bot-code change.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Bot_npc_path_missile_fire_also_triggers_launcher_reload()
    {
        Require.Artwork();
        var mission = NewStartedMission(out var site);
        var ship = NewShip(mission, site);
        var core = mission.Core;
        var mt = DumbfireMissile(core);

        ship.MountPart(MissileLauncherPart(core, mt, 721), 1, 0, 255);          // fully-mounted
        Assert.True(ship.TryMountCargoPart(MissileLauncherPart(core, mt, 724), 2, -1, 0));

        // NPC path: no clientRequest, strict load readiness, now just past the
        // load deadline (the dumbfire fire recipe from MissileFireTests).
        bool fired = ship.TryFireMissile(mission.LastUpdate + 0.002);
        Assert.True(fired, "a load-ready launcher must fire on the NPC path");

        Assert.Equal(AutoReloadOutcome.Reloaded, ship.LastLauncherAutoReloadOutcome);
        var mag = ship.Parts.Single(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0);
        Assert.True(mag.Amount >= 2, $"the bot's magazine must be topped up from the spare (got {mag.Amount})");
        Assert.Contains(site.Calls, c => c.StartsWith("AutoReloadTriggered("));
    }

    /// <summary>⭐ P787a scope 5 (bots) — the quantifiable bot benefit. A bomber
    /// with a 1-round magazine + a 3-round spare launcher in cargo sustains FOUR
    /// missile launches before running dry (1 initial + 3 from the auto-reloaded
    /// spare), whereas on master (no launcher auto-reload) the same bomber fires
    /// exactly ONCE and then drops its empty launcher — a 4× increase in sustained
    /// missiles-per-bomber from cargo alone, with zero bot-code change (the reload
    /// triggers from the shared fire path).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Bomber_with_cargo_spare_sustains_four_launches_versus_one_on_master()
    {
        Require.Artwork();
        var mission = NewStartedMission(out _);
        var ship = NewShip(mission, new RecordingIgcSite());
        var core = mission.Core;
        var mt = DumbfireMissile(core);

        ship.MountPart(MissileLauncherPart(core, mt, 733), 1, 0, 255);          // 1 round
        Assert.True(ship.TryMountCargoPart(MissileLauncherPart(core, mt, 729), 3, -1, 0)); // 3-round spare

        double now = mission.LastUpdate;
        int launches = 0;
        for (int i = 0; i < 8; i++)
        {
            var launcher = ship.Parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0);
            if (launcher is null) break;                     // dropped → no more launches
            now += 0.011;
            if (ship.TryFireMissile(now, clientRequest: true, requestedLock: 0f)) launches++;
        }

        // 1 initial + the 3-round spare fully topped up = 4 total launches before dry.
        Assert.Equal(4, launches);
        // The empty launcher is finally dropped (no more spare), the native
        // depletion outcome — the very edge master could not even reach.
        Assert.Equal(AutoReloadOutcome.Depleted, ship.LastLauncherAutoReloadOutcome);
        Assert.Null(ship.Parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0));
    }
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Empty_missile_launcher_picks_a_different_mountable_launcher_type()
    {
        Require.Artwork();
        var mission = NewStartedMission(out var site);
        var ship = NewShip(mission, site);
        var core = mission.Core;
        var dumb = DumbfireMissile(core);
        var lockTyp = LockingMissile(core);
        if (dumb.ExpendableTypeId == lockTyp.ExpendableTypeId) return;   // same-type fallback

        ship.MountPart(MissileLauncherPart(core, dumb, 716), 1, 0, 255);       // empty soon
        Assert.True(ship.TryMountCargoPart(MissileLauncherPart(core, lockTyp, 718), 2, -1, 0)); // different type

        bool fired = ship.TryFireMissile(mission.LastUpdate, clientRequest: true, requestedLock: 0f);
        Assert.True(fired);

        Assert.Equal(AutoReloadOutcome.Reloaded, ship.LastLauncherAutoReloadOutcome);
        var mag = ship.Parts.Single(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0);
        Assert.IsType<DataPartTypeIGC.MissileLauncher>(mag.Type.Type);
        Assert.Equal(lockTyp.ExpendableTypeId, ((DataPartTypeIGC.MissileLauncher)mag.Type.Type).ExpendableTypeId);
        Assert.Equal(2, mag.Amount);
    }

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: firing a chaff launcher to zero with a
    /// spare chaff launcher in cargo auto-reloads and raises AutoReloadTriggered
    /// (native FireExpendable clintlib.cpp:3382-3422).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Firing_chaff_to_zero_top_up_from_cargo_spare()
    {
        Require.Artwork();
        var mission = NewStartedMission(out var site);
        var ship = NewShip(mission, site);
        var core = mission.Core;
        var ct = core.ChaffTypes.Values.First();

        ship.MountPart(ChaffLauncherPart(core, ct, 730), 1, 0, 255);
        Assert.True(ship.TryMountCargoPart(ChaffLauncherPart(core, ct, 732), 2, -1, 0));

        var part = ship.Parts.Single(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.MountId == 0);
        var deployment = ship.FireDispenser(part, mission.LastUpdate, new Random(1));
        Assert.NotNull(deployment);

        Assert.Equal(AutoReloadOutcome.Reloaded, ship.LastLauncherAutoReloadOutcome);
        var launcher = ship.Parts.Single(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.MountId == 0);
        Assert.True(launcher.Amount >= 2, $"the chaff launcher must be topped up (got {launcher.Amount})");
        Assert.Contains(site.Calls, c => c.StartsWith("AutoReloadTriggered("));
    }

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: firing a mine dispenser to zero with a
    /// spare mine launcher in cargo auto-reloads (dispenser case,
    /// clintlib.cpp:3382-3422).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Firing_mine_dispenser_to_zero_top_up_from_cargo_spare()
    {
        Require.Artwork();
        var mission = NewStartedMission(out var site);
        var ship = NewShip(mission, site);
        var core = mission.Core;
        var mt = core.MineTypes.Values.First();

        ship.MountPart(MineLauncherPart(core, mt, 734), 1, 0, 255);
        Assert.True(ship.TryMountCargoPart(MineLauncherPart(core, mt, 736), 2, -1, 0));

        var part = ship.Parts.Single(p => p.EquipmentType == EquipmentType.Dispenser && p.MountId == 0);
        var deployment = ship.FireDispenser(part, mission.LastUpdate, new Random(2));
        Assert.NotNull(deployment);

        Assert.Equal(AutoReloadOutcome.Reloaded, ship.LastLauncherAutoReloadOutcome);
        var launcher = ship.Parts.Single(p => p.EquipmentType == EquipmentType.Dispenser && p.MountId == 0);
        Assert.True(launcher.Amount >= 2, $"the dispenser must be topped up (got {launcher.Amount})");
        Assert.Contains(site.Calls, c => c.StartsWith("AutoReloadTriggered("));
    }

    /// <summary>⭐ MUST-FAIL-IF-REMOVED: firing a chaff launcher to zero with NO
    /// spare drops the empty launcher and raises LauncherReloadFailed ("Chaff
    /// depleted.", clintlib.cpp:3407-3414).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Firing_chaff_to_zero_with_no_cargo_spare_drops_the_empty_launcher()
    {
        Require.Artwork();
        var mission = NewStartedMission(out var site);
        var ship = NewShip(mission, site);
        var core = mission.Core;
        var ct = core.ChaffTypes.Values.First();

        ship.MountPart(ChaffLauncherPart(core, ct, 738), 1, 0, 255);
        var part = ship.Parts.Single(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.MountId == 0);
        ship.FireDispenser(part, mission.LastUpdate, new Random(3));

        Assert.Equal(AutoReloadOutcome.Depleted, ship.LastLauncherAutoReloadOutcome);
        Assert.Null(ship.Parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.MountId == 0));
        Assert.Contains(site.Calls, c => c.StartsWith("LauncherReloadFailed("));
    }
}
