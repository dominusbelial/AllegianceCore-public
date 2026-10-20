using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P656cFinaleLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class Rig : IDisposable
    {
        internal readonly Srv Server = new();
        internal readonly ConnectionSession Client = new();
        internal Mission Mission => Server.Missions[0];
        internal short ShipId => Client.World.MyShipId!.Value;
        internal Rig(short side)
        {
            Server.ShipSpawner = (core, mission, id) =>
                {
                    var station = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId == side);
                    var sector = mission.Sectors.First(s => s.Stations.Contains(station));
                    var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
                    var ship = new Ship(id, hull,
                        DockTestPlacement.Inside(mission, station, hull),
                        Vector3.Zero, Orientation.Identity) { SideId = side };
                    mission.PlaceShip(ship, sector);
                    return (ship, sector);
                };
            Server.Boot(new()
            {
                ArtPath = Catalog.ArtRoot, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
                SideSelectAutoSeatSeconds = 30,   // P763: these clients never send POSITIONREQ
                LiveSettings = new() { TestUnlock = new() { Enabled = true, AllTech = true, Money = 1_000_000 } },
                Games = [new() { Name = "P656c clash43", Map = "XXV_simple.igc", Teams = 2,
                    CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] }],
            });
            Mission.Stage = MissionStage.Started;
            Client.Connect(Server.BoundPort, "p656c");
            Client.SendLogonRequest(new()
            {
                CharacterName = "p656c", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL",
                MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer, Time = 656,
                DwCookie = 656, CrcFileList = -1, SteamAuthTicket = new byte[16],
                SteamAuthTicketLength = 0, SteamID = 65600000000000000ul, DrmHash = "p656c",
            });
            Pump(() => Client.World.MyShipId is { } id && Client.World.MySideId == side
                && Client.World.Ships[id].StationId >= 0
                && Client.World.DevelopmentTechsFor(side) is not null
                && Client.World.Stations.Values.Any(s => s.SideId != side && s.SideId >= 0));
        }
        internal void Pump(Func<bool> done)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                Server.TickSim();
                lock (Client.Gate) if (done()) return;
                Thread.Sleep(1);
            }
            Assert.Fail("P656c loopback condition timed out");
        }
        public void Dispose() { Client.Dispose(); Server.Dispose(); }
    }

    private ShowcaseFinale.StrikeLoadout Select(Rig rig, short side)
    {
        var botType = typeof(BotManager).GetNestedType("ActiveBot", BindingFlags.NonPublic)!;
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var bot = Activator.CreateInstance(botType, flags, null,
            ["p656c", (short)23, "Stealth Bomber", side, side, "combat", 0.0], null)!;
        botType.GetField("Cs", flags)!.SetValue(bot, rig.Client);
        botType.GetField("ShipId", flags)!.SetValue(bot, rig.ShipId);
        DataStationIGC target;
        lock (rig.Client.Gate)
            target = rig.Client.World.Stations.Values.First(s => s.SideId >= 0 && s.SideId != side);
        var type = Catalog.RepresentativeCore().StationTypes[target.StationTypeId];
        var chosen = (ShowcaseFinale.StrikeLoadout?)typeof(BotManager)
            .GetMethod("SelectFinaleLoadout", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [bot, type, null]);
        var serverSide = rig.Mission.Sides.Single(s => s.Id == side);
        output.WriteLine($"side {side}, civ {serverSide.Civilization.CivilizationId}: "
            + $"server techs {serverSide.DevelopmentTechs.PopCount}, client techs {rig.Client.World.DevelopmentTechsFor(side)?.PopCount}, "
            + $"target {target.StationTypeId}; selected {chosen?.HullId} {chosen?.HullName}");
        Assert.NotNull(chosen);
        // P704 also considers a fixed anti-ship aim gun plus station missiles.
        // This beats the old heavy-bomber-only search on this real target.
        Assert.Equal((short)23, chosen.HullId);
        Assert.Equal("Stealth Bomber", chosen.HullName);
        Assert.Contains(chosen.Parts, p => p.PartId == 601);
        var previousVolley = ShowcaseFinale.PartDamage(rig.Mission.Core, 76, type) * 2
            + ShowcaseFinale.PartDamage(rig.Mission.Core, 144, type);
        Assert.True(chosen.Volley.Score(type) > previousVolley.Score(type));
        Assert.Equal(Showcase.HullRole.Bomber, Showcase.ClassifyHull(rig.Mission.Core.HullTypes[chosen.HullId]));
        Assert.False(rig.Mission.Core.HullTypes[chosen.HullId].HasCapability(DataHullTypeIGC.HabmCarrier));
        return chosen;
    }

    [SkippableTheory]
    [InlineData((short)0)] [InlineData((short)1)]
    public void Rig_allTech_client_selects_the_best_legal_bomber(short side)
    {
        Require.Artwork();
        using var rig = new Rig(side);
        Select(rig, side);
        Assert.Equal(rig.Mission.Sides.Single(s => s.Id == side).DevelopmentTechs,
            rig.Client.World.DevelopmentTechsFor(side));
        var serverSide = rig.Mission.Sides.Single(s => s.Id == side);
        var initial = rig.Client.World.InitialTechsFor(side);
        Assert.Equal(serverSide.InitialTechs, initial);
        Assert.NotEqual(rig.Client.World.DevelopmentTechsFor(side), initial);
        // P560's layer-drone investment ids must survive the shop-tech fix.
        var views = BucketCatalog.Describe(rig.Mission.Core, initial!.Value,
            rig.Client.World.GlobalAttributesFor(side));
        Assert.Equal(serverSide.Buckets.Select(b => (b.Id, b.ItemType)), views.Select(v => (v.Id, v.ItemType)));
    }

    [SkippableTheory]
    [InlineData((short)0)] [InlineData((short)1)]
    public void Rig_allTech_finale_buy_loadout_is_fully_accepted(short side)
    {
        Require.Artwork();
        using var rig = new Rig(side);
        var chosen = Select(rig, side);
        rig.Client.ResetBuyLoadoutAcks();
        rig.Client.SendBuyLoadout(chosen.HullId, fLaunch: true, chosen.Parts);
        rig.Pump(() => rig.Client.BuyLoadoutAcks.Count > 0);
        var ack = Assert.Single(rig.Client.BuyLoadoutAcks);
        Assert.True(ack.FBoughtEverything);
        Assert.True(ack.FLaunch);
        Assert.Equal(chosen.HullId, ack.HullId);
        Assert.Equal(chosen.Parts.OrderBy(p => p.PartId).ThenBy(p => p.MountId),
            ack.Parts.OrderBy(p => p.PartId).ThenBy(p => p.MountId));
        output.WriteLine($"BUY_LOADOUT accepted: hull {ack.HullId}, full grant, parts "
            + string.Join(", ", ack.Parts.Select(p => $"{p.PartId}@{p.MountId} x{p.Amount}")));
        var ship = rig.Server.Ships.Single(s => s.Ship.Id == rig.ShipId).Ship;
        Assert.Equal(chosen.HullId, ship.HullType.HullId);
        Assert.Null(ship.StationId);
    }
}

public sealed class P656cTechStateTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Initial_bucket_mask_survives_research_and_clears_on_new_world(bool nextMission)
    {
        var world = new Allegiance.ClientCore.State.ClientWorld();
        world.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        var initial = new byte[TechTreeBitMask.ByteCount]; initial[0] = 1;
        var current = new byte[TechTreeBitMask.ByteCount]; current[0] = 3;
        world.Apply(new CreateBuckets { TtbmInitial = initial, TtbmDevelopments = current });
        Assert.Equal(TechTreeBitMask.FromBytes(initial), world.InitialTechsFor(0));
        Assert.Equal(TechTreeBitMask.FromBytes(current), world.DevelopmentTechsFor(0));
        current[0] = 7;
        world.Apply(new SideTechChange { SideId = 0, TtbmDevelopments = current });
        Assert.Equal(TechTreeBitMask.FromBytes(current), world.DevelopmentTechsFor(0));
        Assert.Equal(TechTreeBitMask.FromBytes(initial), world.InitialTechsFor(0));
        Assert.Null(world.InitialTechsFor(1));
        if (nextMission) world.BeginNextMission(); else world.Reset();
        Assert.Null(world.InitialTechsFor(0));
        Assert.Null(world.DevelopmentTechsFor(0));
    }
}
