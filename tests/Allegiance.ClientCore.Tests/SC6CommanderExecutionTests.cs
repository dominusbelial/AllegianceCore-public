using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    [SkippableFact]
    public void SC6_Commanded_repair_precedes_autonomous_station_priority_then_releases_healed_target()
    {
        Require.Artwork(); using var reads = new DefenseLiveReads(); using var cs = Session(true);
        DefenseMap(cs.World);
        var core = Catalog.RepresentativeCore(); var state = new ResearchState(new(core, 25));
        var selected = TechLoadoutSelector.SelectSupport(core, 25, state.Techs, state.Tree.Civ.InitialStationTypeId, 0)!;
        var bot = Bot(cs); Set(bot, "Role", "support"); Call("ApplyTechLoadout", null, bot, selected);
        DefenseNoSocketSender(bot);
        void Hull(byte hull)
        {
            var bytes = new byte[ServerShipUpdate.Width]; var writer = new WireWriter(bytes); Pose(2, 100).EncodeBody(ref writer); bytes[40] = hull;
            var reader = new WireReader(bytes);
            cs.World.Apply(new SingleShipUpdate { OidTarget = 2, Ship = ServerShipUpdate.DecodeBody(ref reader) });
        }
        cs.World.Apply(new ExportShip { ShipId = 2, SideId = 0, HullId = 15 });
        cs.World.Apply(new ShipStatusMessage { ShipId = 2, HullId = 15, SectorId = 1, StationId = -1, ParentId = -1 });
        Hull(120);
        cs.World.Apply(new StationsUpdate { Entries = [new(100, 120, 240)] });
        var manager = Manager(bot);
        Set(bot, "OrderedCommand", new QueuedCommandState(OrderChange.CidRepair, IgcObjectType.Ship, 2));
        Set(bot, "OrderedTargetId", (short?)9);
        Assert.True((bool)Call("TryDriveSupport", manager, bot, 10.0)!);
        Assert.Equal((short?)2, Get<short?>(bot, "TargetId"));
        Assert.Null(Get<short?>(bot, "OrderedTargetId"));
        Hull(240);
        Assert.True((bool)Call("TryDriveSupport", manager, bot, 11.0)!);
        Assert.Null(Get<QueuedCommandState?>(bot, "OrderedCommand"));
        Assert.Equal((short?)100, Get<short?>(bot, "TargetId"));
        Set(bot, "OrderedCommand", new QueuedCommandState(OrderChange.CidRepair, IgcObjectType.Station, 100));
        Assert.True((bool)Call("DriveCommanderOrder", manager, bot, 12.0)!);
        cs.World.Apply(new ExportStation { Station = Station(100, 1, 1) });
        Assert.False((bool)Call("DriveCommanderOrder", manager, bot, 13.0)!);
        Assert.Null(Get<QueuedCommandState?>(bot, "OrderedCommand"));
    }

    [SkippableFact]
    public void SC6_Observed_targets_exclude_hidden_dead_docked_and_pod_ships()
    {
        Require.Artwork(); using var cs = Session(); DefenseMap(cs.World);
        void Enemy(short id, short hull = 15)
        {
            DefenseEnemy(cs.World, id, hull);
            var bytes = new byte[ServerShipUpdate.Width]; var writer = new WireWriter(bytes); Pose(id, 100).EncodeBody(ref writer); bytes[40] = 240;
            var reader = new WireReader(bytes);
            cs.World.Apply(new SingleShipUpdate { OidTarget = id, Ship = ServerShipUpdate.DecodeBody(ref reader) });
            cs.World.Apply(new ShipStatusMessage { ShipId = id, HullId = hull, SectorId = 1, StationId = -1, ParentId = -1 });
        }
        Enemy(2); Enemy(3);
        cs.World.Apply(new KillShip { ShipId = 3, BDeathCredit = true });
        Enemy(4);
        cs.World.Apply(new ShipStatusMessage { ShipId = 4, HullId = 15, SectorId = 1, StationId = 100, ParentId = -1 });
        Enemy(5); cs.World.Apply(new ShipDelete { ShipId = 5, Sdr = ShipDelete.SdrLeftSector });
        Enemy(6, Catalog.RepresentativeCore().HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod)).HullId);
        var targets = BotManager.ObserveTacticalTargets(cs.World, 0);
        Assert.Contains(targets, t => t.Type == IgcObjectType.Ship && t.Id == 2 && !t.Friendly);
        Assert.DoesNotContain(targets, t => t.Type == IgcObjectType.Ship && t.Id is 3 or 4 or 5 or 6);
        Assert.Contains(targets, t => t.Type == IgcObjectType.Station && t.Id == 100 && t.Friendly);
    }
}
