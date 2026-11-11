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

/// <summary>
/// P996 — END TO END: the rig's lifepod respawn loop, closed on both sides.
///
/// <para>Server timeline that started this task: podded → pod killed → the player
/// respawns DOCKED as a Lifepod (native FedSrv.CPP:12030-12065) → the hangar
/// Launch button sends <c>BUY_LOADOUT(lifepod hull, 0 parts, fLaunch)</c> → 6 ms
/// later the ship dies of oxygen at a deadline left over from the previous life
/// ("[oxygen] lifepod ship 8110 oxygen exhausted at sim-t 1030.7 (deadline
/// 926.25)") → docked pod again, forever.</para>
///
/// <para>This test drives the REAL client session + REAL shop model + REAL wire
/// against a real loopback server: launch a real hull, get podded, let the pod
/// die, and then reproduce the client's docked hangar exactly as
/// <c>Game1.Panes</c> builds it. Two invariants together break the loop:
/// (1) the client restored the last loadout (native <c>RestoreLoadout</c>,
/// <c>clintlib.h:1876-1886</c>) so the launch request carries the REAL hull — not
/// the pod; (2) the relaunched ship carries no lifepod oxygen deadline at all
/// (native only arms it on a pod hull swap into space, <c>shipIGC.cpp:1408</c>,
/// and a berthed pod is out of its cluster, <c>shipIGC.h:441-457</c>), so a stale
/// clock can never kill it.</para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P996LifepodHangarRestoreLiveTests
{
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hangar_after_pod_death_restores_the_last_loadout_and_launches_the_real_ship()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(new ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork),
            StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
            SideSelectAutoSeatSeconds = 30,   // P763: this client never sends POSITIONREQ
            Games = [new GameConfig { Name = "P996", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [25, 3], SideNames = ["A", "B"] }],
        });
        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "p996-human");
        session.SendLogonRequest(new LogonRequest
        {
            CharacterName = "p996-human", ZoneTicket = [1, 2, 3, 6],
            CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer,
            Time = 996, DwCookie = uint.MaxValue, CrcFileList = -1, SteamAuthTicket = new byte[16],
            SteamAuthTicketLength = 0, SteamID = 97700000000000996ul, DrmHash = "p996",
        });
        void Wait(string phase, Func<bool> done)
        {
            for (int i = 0; i < 10000; i++)
            {
                server.TickSim();
                lock (session.Gate) if (done()) return;
                Thread.Sleep(1);
            }
            Assert.Fail($"P996 timeout: {phase}");
        }
        Wait("seat", () => session.World.MyShipId is not null);
        var ship = server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models.OfType<Ship>())
            .Single(s => s.Id == session.World.MyShipId);
        var mission = ship.Mission!;
        mission.Stage = MissionStage.Started;
        // The MonoGame client wires this resolver on connect (Game1.Lifecycle:401);
        // wire it here so the client world can classify the pod hull the same way.
        session.World.LifepodHullResolver = sideId =>
            mission.Sides.FirstOrDefault(s => s.Id == sideId)?.Civilization.Lifepod;

        // A free, sellable real hull with a gun — so the purchase is grantable and
        // the restored loadout has a part to carry.
        var hull = EconomyLoadoutTestSetup.Sell(mission, EconomyLoadoutTestSetup.WeaponHull(mission.Core));
        var gun = EconomyLoadoutTestSetup.Sell(mission, mission.Core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon && hull.CanMountPart(p, 0)).First()) with { Price = 0 };
        ((System.Collections.Generic.Dictionary<short, DataPartTypeIGC>)mission.Core.PartTypes)[gun.PartId] = gun;
        hull = hull with { Price = 0, PreferredPartsTypes = [gun.PartId] };
        ((System.Collections.Generic.Dictionary<short, DataHullTypeIGC>)mission.Core.HullTypes)[hull.HullId] = hull;

        // ── first life: buy + launch the real hull WITH its gun (so the saved
        // loadout the hangar restores has a real part to carry) ───────────────
        var mountedGun = new ExpandedPartData(gun.PartId, 40, 0, ExpandedPartData.FullyMounted);
        session.SendBuyLoadout(hull.HullId, true, [mountedGun]);
        Wait("initial launch", () => ship.StationId is null && session.BuyLoadoutAcks.Any(a => a.FLaunch));
        Assert.Contains(ship.Parts, p => p.PartId == gun.PartId);
        Wait("client saved the purchased loadout", () =>
            session.World.OwnSavedLoadout is { } saved && saved.HullId == hull.HullId && saved.Parts.Count > 0);

        // ── podded, then a real pod death → DOCKED Lifepod (FedSrv.CPP:12030-12065)
        short podHull = mission.Sides.Single(s => s.Id == ship.SideId).Civilization.Lifepod;
        ship.ReceiveDamage(0, 1_000_000, mission.LastUpdate, ship.Position, Vector3.Zero, null);
        Assert.Equal(podHull, ship.HullType.HullId);
        Assert.Null(ship.ParentShip);
        ship.ReceiveDamage(0, 1_000_000, mission.LastUpdate, ship.Position, Vector3.Zero, null);
        Wait("pod dock", () => ship.StationId is not null && session.World.OwnShipDockedInLifepod);
        Assert.Equal(podHull, ship.HullType.HullId);
        // ⭐ server invariant 1: a berthed pod carries NO oxygen clock.
        Assert.Null(ship.LifepodTimeToDie);

        // ── the docked hangar, built exactly as Game1.Panes does ──────────────
        short currentHull;
        ClientCore.State.ClientWorld.SavedLoadout? savedLoadout;
        lock (session.Gate)
        {
            currentHull = session.World.Ships[session.World.MyShipId!.Value].HullId;
            savedLoadout = session.World.OwnSavedLoadout;
        }
        Assert.Equal(podHull, currentHull);   // the ship really is berthed in the pod hull
        var savedShip = Assert.IsType<ClientCore.State.ClientWorld.SavedLoadout>(savedLoadout);
        Assert.Equal(hull.HullId, savedShip.HullId);
        var shop = new ShopModel(new ShopCatalog([hull], [gun]), currentHull, []);
        Assert.True(shop.RestoreSavedLoadout(savedShip.HullId, savedShip.Parts));
        // ⭐ the hangar selection (and therefore the Launch request) is the real hull
        Assert.Equal(hull.HullId, shop.EffectiveHullId);
        Assert.Contains(shop.ChosenParts, p => p.PartId == gun.PartId);

        // ── Launch: the SAME request the rig sent — but with the restored hull ─
        session.ResetBuyLoadoutAcks();
        session.SendBuyLoadout(shop.EffectiveHullId, true, shop.ChosenParts);
        Wait("purchase ack for the restored hull", () => session.BuyLoadoutAcks.Any(a => a.HullId == hull.HullId));
        var ack = session.BuyLoadoutAcks.Last(a => a.HullId == hull.HullId);
        Assert.True(ack.FLaunch);
        Assert.Equal(hull.HullId, ack.HullId);
        Wait("launched into the real hull", () => ship.StationId is null && !ship.Dead);
        Assert.Equal(hull.HullId, ship.HullType.HullId);
        Assert.False(ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod));

        // ⭐ server invariant 2: the ship relaunched after a pod death has NO
        // deadline at all, and stays alive well past the stale one.
        Assert.Null(ship.LifepodTimeToDie);
        for (int i = 0; i < 60; i++)
        {
            server.TickSim();
            Assert.False(ship.Dead, $"the relaunched ship died at tick {i} — KillingHit={ship.KillingHit.Kind}");
            Assert.NotEqual(HitKind.Oxygen, ship.KillingHit.Kind);
        }
        Wait("client sees the relaunched hull", () =>
            session.World.Ships[session.World.MyShipId!.Value].HullId == hull.HullId);
        Assert.False(session.World.OwnShipDockedInLifepod);
        Console.WriteLine($"[p996] hangar in pod hull {podHull} restored hull {shop.EffectiveHullId} + "
            + $"{shop.ChosenParts.Count} part(s); launched, LifepodTimeToDie=null, survived 60 ticks after the pod death");
    }
}
