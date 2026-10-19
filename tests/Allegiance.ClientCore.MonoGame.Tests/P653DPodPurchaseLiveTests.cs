using System;
using System.Linq;
using System.Threading;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("LiveNetwork")]
public sealed class P653DPodPurchaseLiveTests
{
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Hangar_launch_after_ejection_or_pod_death_buys_real_hull(bool killPod)
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(new ServerConfig { ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork),
            StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
            Games = [new GameConfig { Name = "P653D", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [25, 3], SideNames = ["A", "B"] }] });
        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "p653d-human");
        session.SendLogonRequest(new LogonRequest { CharacterName = "p653d-human", ZoneTicket = [1, 2, 3, 4],
            CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer,
            Time = 653, DwCookie = 653, CrcFileList = -1, SteamAuthTicket = new byte[16],
            SteamAuthTicketLength = 0, SteamID = 97700000000000653ul, DrmHash = "p653d" });
        void Wait(string phase, Func<bool> done)
        {
            for (int i = 0; i < 10000; i++)
            {
                server.TickSim();
                lock (session.Gate) if (done()) return;
                Thread.Sleep(1);
            }
            Assert.Fail($"P653D timeout: {phase}");
        }
        Wait("seat", () => session.World.MyShipId is not null);
        var ship = server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models.OfType<Ship>())
            .Single(s => s.Id == session.World.MyShipId);
        var mission = ship.Mission!;
        mission.Stage = MissionStage.Started;
        var hull = EconomyLoadoutTestSetup.Sell(mission, EconomyLoadoutTestSetup.WeaponHull(mission.Core));
        var gun = EconomyLoadoutTestSetup.Sell(mission, mission.Core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon && hull.CanMountPart(p, 0)).First()) with { Price = 0 };
        ((System.Collections.Generic.Dictionary<short, DataPartTypeIGC>)mission.Core.PartTypes)[gun.PartId] = gun;
        hull = hull with { Price = 0, PreferredPartsTypes = [gun.PartId] };
        ((System.Collections.Generic.Dictionary<short, DataHullTypeIGC>)mission.Core.HullTypes)[hull.HullId] = hull;
        session.SendBuyLoadout(hull.HullId, true);
        Wait("initial launch", () => ship.StationId is null && session.BuyLoadoutAcks.Any(a => a.FLaunch));
        // Lethal damage runs ReceiveDamage -> KillShipEvent -> EjectSurvivor.
        ship.ReceiveDamage(0, 1_000_000, mission.LastUpdate, ship.Position, Vector3.Zero, null);
        short podId = mission.Sides.Single(s => s.Id == ship.SideId).Civilization.Lifepod;
        Assert.Equal((short)444, podId); // clash43's Iron Coalition pod
        Assert.Equal(podId, ship.HullType.HullId);
        Assert.Null(ship.ParentShip);
        if (killPod)
        {
            // True death runs the production restart, which docks the new pod.
            ship.ReceiveDamage(0, 1_000_000, mission.LastUpdate, ship.Position, Vector3.Zero, null);
        }
        else
        {
            var home = mission.Sectors.SelectMany(s => s.StationRuntimes)
                .First(s => s.SideId == ship.SideId && s.Type.HasCapability(DataStationTypeIGC.SabmRestart));
            // Place the ejected pod at home; let the real sector dock/rescue pass run.
            var homeSector = mission.Sectors.Single(s => s.StationRuntimes.Contains(home));
            ship.Sector!.Remove(ship);
            mission.PlaceShip(ship, homeSector);
            ship.Position = DockTestPlacement.Inside(mission, home.Data, ship.HullType);
            ship.Velocity = Vector3.Zero;
        }
        Wait("pod dock", () => ship.StationId is not null);
        Console.WriteLine($"P653D pre-buy killPod={killPod} hull={ship.HullType.HullId} dead={ship.Dead} parent={ship.ParentShip?.Id} station={ship.StationId}");
        // Same ShopModel -> TryLaunch -> SendBuyLoadout used by Game1.Panes.cs:286-288.
        var catalog = new ShopCatalog([hull], [gun]);
        var shop = new ShopModel(catalog, podId, []);
        shop.CycleHullTo(hull.HullId);
        Assert.True(shop.TryLaunch());
        Assert.NotEmpty(shop.ChosenParts);
        session.ResetBuyLoadoutAcks();
        session.SendBuyLoadout(shop.EffectiveHullId, true, shop.ChosenParts);
        // Ejection also emits a pod-sync ACK with FLaunch=false. It can arrive
        // after Reset; wait for the requested real hull, not an unrelated ACK.
        Wait("purchase ack", () => session.BuyLoadoutAcks.Any(a => a.HullId == hull.HullId));
        var ack = session.BuyLoadoutAcks.Last(a => a.HullId == hull.HullId);
        Assert.True(ack.FBoughtEverything, $"pod purchase refused: dead={ship.Dead} parent={ship.ParentShip?.Id}");
        Assert.True(ack.FLaunch);
        Assert.Equal(hull.HullId, ack.HullId);
        Wait("human launched", () => ship.StationId is null && session.World.Ships[ship.Id].StationId == -1);
        Assert.False(ship.Dead);
        Assert.Null(ship.ParentShip);
        // Audit the P653 in-flight Dead/ParentShip gate after this same restart.
        session.SendSwapPart(EquipmentType.Weapon, 0, -1);
        Wait("post-restart unmount", () => ship.CargoHold.Any(p => p?.PartId == gun.PartId));
        session.SendSwapPart(EquipmentType.Weapon, -1, 0);
        Wait("post-restart mount", () => ship.Parts.Any(p => p.PartId == gun.PartId));
    }
}
