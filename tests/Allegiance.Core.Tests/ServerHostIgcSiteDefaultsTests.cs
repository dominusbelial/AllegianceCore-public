using Vector3 = Allegiance.Core.Collision.Vector3;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using ServerHost = global::Allegiance.Server.ServerHost;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P751 — unit proofs that <see cref="Allegiance.Server.ServerHost"/> overrides
/// several <see cref="IIgcSite"/> defaults to match native semantics.
/// </summary>
[Trait("Requires", "Artwork")]
public sealed class ServerHostIgcSiteDefaultsTests
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

    private static Mission NewStartedMission()
    {
        var core = Core();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, SomeMap(), [new SideSpec(0, "Team 1", civ), new SideSpec(1, "Team 2", civ)], new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>⭐ <see cref="IIgcSite.GetCluster"/> matches native
    /// <c>CanSee</c> (shipIGC.h:380-388): own side OR SeenBySide OR in scanner
    /// range. Returns the model's sector when visible, null otherwise.</summary>
    [SkippableFact]
    public void GetCluster_returns_model_sector_when_seen_and_null_when_hidden()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var core = Core();
        var hull = core.HullTypes.Values.OrderByDescending(h => h.HitPoints).First();

        var observer = new Ship(1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(observer, sector);

        // Close enemy: visible immediately via InScannerRange.
        var closeEnemy = new Ship(2, hull, new Vector3(0f, 0f, 100f), Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(closeEnemy, sector);

        // Distant enemy: not in scanner range and with no SeenBySide state.
        var distantEnemy = new Ship(3, hull, new Vector3(10000f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(distantEnemy, sector);

        var host = new ServerHost();

        Assert.Equal(sector, host.GetCluster(observer, closeEnemy));
        Assert.Null(host.GetCluster(observer, distantEnemy));

        // Mark the distant enemy SeenBySide; now GetCluster returns its sector.
        distantEnemy.SideVisibility.SetSideVisibility(0, true);
        Assert.Equal(sector, host.GetCluster(observer, distantEnemy));
    }

    /// <summary>⭐ A docked observer (no sector) still sees own-side models;
    /// enemy models remain hidden without a visibility sweep
    /// (shipIGC.h:380-388).</summary>
    [SkippableFact]
    public void GetCluster_docked_observer_sees_own_side_not_enemy()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var core = Core();
        var hull = core.HullTypes.Values.OrderByDescending(h => h.HitPoints).First();

        var observer = new Ship(10, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(observer, sector);
        observer.Sector = null; // docked

        var friendly = new Ship(11, hull, new Vector3(0f, 0f, 100f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(friendly, sector);
        var enemy = new Ship(12, hull, new Vector3(0f, 0f, 200f), Vector3.Zero, Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(enemy, sector);

        var host = new ServerHost();
        Assert.Equal(sector, host.GetCluster(observer, friendly));
        Assert.Null(host.GetCluster(observer, enemy));
    }

    /// <summary>⭐ An own-side model is visible without any visibility sweep
    /// (shipIGC.h:382).</summary>
    [SkippableFact]
    public void GetCluster_own_side_is_always_visible()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var core = Core();
        var hull = core.HullTypes.Values.OrderByDescending(h => h.HitPoints).First();

        var observer = new Ship(20, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(observer, sector);
        var friendly = new Ship(21, hull, new Vector3(0f, 0f, 500f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(friendly, sector);

        var host = new ServerHost();
        Assert.Equal(sector, host.GetCluster(observer, friendly));
    }

    /// <summary>⭐ A warp equipment target is visible to everyone regardless of
    /// side or scanner range (EquipmentTarget.CanSee).</summary>
    [SkippableFact]
    public void GetCluster_warp_is_always_visible()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors.FirstOrDefault(s => s.Warps.Count > 0);
        Skip.If(sector is null, "map has no warps");
        var core = Core();
        var hull = core.HullTypes.Values.OrderByDescending(h => h.HitPoints).First();

        var observer = new Ship(30, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(observer, sector);
        var warp = sector.Warps[0];
        var target = EquipmentTarget.Resolve(sector, IgcObjectType.Warp, warp.Warp.WarpId);
        Assert.NotNull(target);

        var host = new ServerHost();
        Assert.Equal(sector, host.GetCluster(observer, target!));
    }

    /// <summary>⭐ A treasure inside scanner range is visible via the
    /// <c>InScannerRange</c> branch of <c>CanSee</c> (shipIGC.h:384).</summary>
    [SkippableFact]
    public void GetCluster_treasure_in_scanner_range_is_visible()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var core = Core();
        var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();

        var observer = new Ship(40, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(observer, sector);

        var data = new DataTreasureIGC(
            P0: [0f, 0f, 10f], V0: [0f, 0f, 0f],
            Lifespan: 60f, Time0: 0, ObjectId: 41, TreasureId: 1,
            ClusterId: sector.Id, Amount: 1, TreasureCode: TreasureCode.Cash, CreateNow: true);
        var treasure = new Treasure(41, sector, data, mission.LastUpdate, mission);
        sector.Add(treasure);

        var host = new ServerHost();
        Assert.Equal(sector, host.GetCluster(observer, treasure));
    }

    /// <summary>⭐ <see cref="IIgcSite.GetRipcordCluster"/> returns the ship's
    /// sector when its hull has the requested capability, otherwise null — native
    /// default (igc.h:4915-4921).</summary>
    [SkippableFact]
    public void GetRipcordCluster_returns_sector_only_with_capability()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var core = Core();
        var ripcordHull = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmIsRipcordTarget));
        var nonRipcordHull = core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmIsRipcordTarget));

        var capable = new Ship(4, ripcordHull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(capable, sector);
        var incapable = new Ship(5, nonRipcordHull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(incapable, sector);

        var host = new ServerHost();
        Assert.Equal(sector, host.GetRipcordCluster(capable));
        Assert.Null(host.GetRipcordCluster(incapable));
    }

    /// <summary>⭐ <see cref="IIgcSite.Reload"/> returns false — native default
    /// (igc.h:4923). The port's reload path uses <see cref="IIgcSite.AutoReloadTriggered"/>.</summary>
    [SkippableFact]
    public void Reload_returns_false()
    {
        var core = Core();
        var hull = core.HullTypes.Values.First();
        var ship = new Ship(6, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        var host = new ServerHost();
        Assert.False(host.Reload(ship, null!, EquipmentType.Weapon));
    }
}
