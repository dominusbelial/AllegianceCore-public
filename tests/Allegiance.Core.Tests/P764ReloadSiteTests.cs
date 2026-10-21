using Vector3 = Allegiance.Core.Collision.Vector3;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using ServerHost = global::Allegiance.Server.ServerHost;

namespace Allegiance.Core.Tests;

/// <summary>
/// P764 / SIM-0233 — the <see cref="IIgcSite.Reload"/> callback now has a real
/// implementation and a live caller. Native's depletion blocks ask the site
/// (<c>shipIGC.h:603-616</c> <c>pigc-&gt;Reload(this, NULL, ET_Weapon)</c> and
/// <c>shipIGC.h:636-650</c> <c>ET_Afterburner</c>); the only native
/// implementation with real logic is <c>BaseClient::Reload</c>
/// (<c>clintlib.cpp:3425-3556</c>: drain the LAST matching pack in the cargo
/// hold into the pool, possibly partially, terminating emptied packs), while
/// FedSrv inherits the <c>false</c> default (igc.h:4923). The port runs the
/// sim server-side, so <c>ServerHost.Reload</c> performs the pack drain (via
/// the P361/P412 machinery) and <see cref="Ship.SiteReload"/> routes the
/// zero-transition ask through the seam — a DEFAULT site (NullIgcSite) still
/// yields the native default <c>false</c> and the direct P361 fallback keeps
/// headless auto-reload intact.
/// </summary>
[Trait("Requires", "Artwork")]
public sealed class P764ReloadSiteTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static MissionMap SomeMap()
    {
        foreach (var path in Directory.EnumerateFiles(ArtRoot, "*.igc"))
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count > 0) return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    }

    private static Mission NewStartedMission(IIgcSite site)
    {
        var core = Core();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, SomeMap(), [new SideSpec(0, "Team 1", civ)], site);
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>A constructed ammo pack part (<c>c_packAmmo</c> = 0), the same
    /// shape AutoReloadOnEmptyTests mounts.</summary>
    private static DataPartTypeIGC AmmoPackType(short amount) => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ammo", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
        PartId: 910, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
        Type: new DataPartTypeIGC.Pack(PackType: 0, Amount: amount));

    private static Ship ShipWithAmmoPack(Mission mission, short packAmount)
    {
        var sector = mission.Sectors[0];
        var ship = new Ship(9401, mission.Core.HullTypes.Values.First(h => h.MaxAmmo > 0),
            new Vector3(10f, -20f, 30f), Vector3.Zero, Orientation.Identity)
        { SideId = 0 };
        mission.PlaceShip(ship, sector);
        ship.Ammo = 0;   // below max: the drain has room to fill
        ship.MountPart(AmmoPackType(packAmount), amount: packAmount, mountId: 0, fractionMounted: 255);
        Assert.True(ship.TrySwapPartWithCargo(EquipmentType.Pack, 0, -1),
            "the ammo pack must land in the cargo hold");
        return ship;
    }

    [SkippableFact]
    public void HostReload_WithAmmoPackInCargo_DrainsIt_AndReturnsTrue()
    {
        Require.Artwork();
        var host = new ServerHost();
        var mission = NewStartedMission(host);
        var ship = ShipWithAmmoPack(mission, packAmount: 2000);   // more than the hull has room for → a partial drain
        short maxAmmo = ship.HullType.MaxAmmo;

        // Native BaseClient::Reload tops the pool to max and LEAVES the rest
        // in the pack when the pack outlasts the room (clintlib.cpp:3510-3516).

        // Native BaseClient::Reload ET_Weapon (clintlib.cpp:3457-3530): the
        // pack tops the pool up (partially when the pack outlasts the room).
        Assert.True(host.Reload(ship, null!, EquipmentType.Weapon));
        Assert.Equal(maxAmmo, ship.Ammo);   // 50 ≥ the hull's room → full top-up
    }

    [SkippableFact]
    public void HostReload_WithoutAMatchingPack_ReturnsFalse_LikeTheNativeDefault()
    {
        Require.Artwork();
        var host = new ServerHost();
        var mission = NewStartedMission(host);
        var sector = mission.Sectors[0];
        var ship = new Ship(9402, mission.Core.HullTypes.Values.First(h => h.MaxAmmo > 0),
            new Vector3(10f, -20f, 30f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, sector);
        ship.Ammo = 0;

        // No pack in the hold → the drain finds nothing → false, the same
        // answer native's depletion block turns into "Ammo depleted."
        // (shipIGC.h:611-612; here relayed via AutoReloadTriggered(success:
        // false) → Server.HandleReload).
        Assert.False(host.Reload(ship, null!, EquipmentType.Weapon));
        Assert.Equal(0, ship.Ammo);
    }

    [SkippableFact]
    public void DepletionThroughTheSite_WithADefaultSite_FallsBackToTheDirectAttempt()
    {
        Require.Artwork();
        var mission = NewStartedMission(new NullIgcSite());
        var ship = ShipWithAmmoPack(mission, packAmount: 2000);   // more than the hull has room for → a partial drain

        // NullIgcSite keeps native igc.h:4923's default false, so SiteReload
        // falls back to the direct P361 attempt — headless missions keep
        // auto-reload (one attempt either way).
        Assert.True(ship.SiteReload(EquipmentType.Weapon, now: ship.LastUpdate + 10));
        Assert.Equal(ship.HullType.MaxAmmo, ship.Ammo);
    }

    [SkippableFact]
    public void MagazineAndDispenserTypes_KeepTheNativeDefaultFalse()
    {
        Require.Artwork();
        var host = new ServerHost();
        var mission = NewStartedMission(host);
        var sector = mission.Sectors[0];
        var ship = new Ship(9403, mission.Core.HullTypes.Values.First(),
            new Vector3(10f, -20f, 30f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, sector);

        // No port-side depletion caller exists for launcher reloads (native's
        // magazine/dispenser branch, clintlib.cpp:3557-3650), so those
        // equipment types keep igc.h:4923's default false.
        Assert.False(host.Reload(ship, null!, EquipmentType.Magazine));
        Assert.False(host.Reload(ship, null!, EquipmentType.Dispenser));
        Assert.False(host.Reload(ship, null!, EquipmentType.ChaffLauncher));
    }

    // ── P764b — the fallback must not ride the debounce ───────────────────

    [SkippableFact]
    public void SiteReload_WithoutAPack_RaisesExactlyOneDepletedTrigger()
    {
        // P764b — the pre-fix fallback re-entered TryAutoReloadAmmo whenever
        // ServerHost.Reload answered false (no pack); only the freshly
        // re-armed _timeReloadAmmo kept the second AutoReloadTriggered(false)
        // from firing. With `now` past the debounce that second attempt is
        // NOT debounced, so the pre-fix code raises the event twice; the
        // deadline-snapshot rule fires it exactly once.
        Require.Artwork();
        var host = new ServerHost();
        var mission = NewStartedMission(host);
        var sector = mission.Sectors[0];
        var ship = new Ship(9404, mission.Core.HullTypes.Values.First(h => h.MaxAmmo > 0),
            new Vector3(10f, -20f, 30f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, sector);
        ship.Ammo = 0;   // no pack in the hold → the drain finds nothing

        var triggers = new List<bool>();
        host.AutoReload += (_, _, success) => triggers.Add(success);

        Assert.False(ship.SiteReload(EquipmentType.Weapon, now: ship.LastUpdate + 10));
        Assert.Equal([false], triggers);   // exactly one AutoReloadTriggered(success: false)
        Assert.Equal(0, ship.Ammo);
    }

    [SkippableFact]
    public void SiteReload_WithAPack_RaisesExactlyOneSuccessTrigger()
    {
        // The success companion: the site's attempt drains the pack, answers
        // true, and SiteReload returns through the seam — still exactly one
        // AutoReloadTriggered(success: true).
        Require.Artwork();
        var host = new ServerHost();
        var mission = NewStartedMission(host);
        var ship = ShipWithAmmoPack(mission, packAmount: 2000);

        var triggers = new List<bool>();
        host.AutoReload += (_, _, success) => triggers.Add(success);

        Assert.True(ship.SiteReload(EquipmentType.Weapon, now: ship.LastUpdate + 10));
        Assert.Equal([true], triggers);
        Assert.Equal(ship.HullType.MaxAmmo, ship.Ammo);
    }

    [SkippableFact]
    public void SiteReload_WithADefaultSite_StillFallsBackToTheDirectAttempt()
    {
        // The deadline-snapshot rule keeps the DEFAULT-site fallback intact:
        // NullIgcSite.Reload is igc.h:4923's false and never touches the
        // deadlines, so the direct P361 attempt still fires (headless missions
        // keep auto-reload).
        Require.Artwork();
        var mission = NewStartedMission(new NullIgcSite());
        var ship = ShipWithAmmoPack(mission, packAmount: 2000);

        Assert.True(ship.SiteReload(EquipmentType.Weapon, now: ship.LastUpdate + 10));
        Assert.Equal(ship.HullType.MaxAmmo, ship.Ammo);
    }
}
