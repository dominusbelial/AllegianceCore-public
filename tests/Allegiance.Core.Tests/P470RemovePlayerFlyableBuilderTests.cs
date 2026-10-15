using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P470 — the player cannot fly or buy a drone-only hull. Native's hangar lists
/// a hull only when its (signed) group id is non-negative
/// (load.cpp:1603-1606, clintlib.h:1858); the port decodes
/// <see cref="DataHullTypeIGC.GroupId"/> as a signed byte, and the real
/// PCore001 constructor/miner/lifepod/recon/rescue drone hulls all carry
/// GroupId = -1 (raw 0xFF). The server's BUY_LOADOUT hull-acceptance gate
/// (the P441 gate, extended by P470) refuses such a hull the same way it
/// refuses an unsellable one.
/// <list type="number">
/// <item>A BUY_LOADOUT naming a real Constructor hull is REFUSED — the ship
/// stays on its pre-purchase hull (this is the test that fails on the unfixed
/// code, where buying a constructor succeeds and swaps the ship's hull).</item>
/// <item>A BUY_LOADOUT naming a normal player hull still succeeds — the ship
/// swaps to it, proving the GroupId gate is not over-broad.</item>
/// </list>
/// Both run UNDOCKED (station-less sector), so P441's docked-only tech gate is
/// not in play — the only gate under test is the P470 GroupId acceptance rule.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P470RemovePlayerFlyableBuilderTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P470 No Player Builder", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 0470u, DwCookie = 0x470u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 4700000000000000047ul, DrmHash = "p470noplayerbldr",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship LiveShip(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    private static void Fund(Srv server, short shipId, int amount) =>
        server.Ships.First(s => s.Ship.Id == shipId).Wallet.Money = amount;

    /// <summary>Boot an in-process server and one seated bot on the friendly
    /// side, spawned UNDOCKED in a station-less sector (no dock, so P441's
    /// docked-only tech gate is inert — only the P470 GroupId gate applies).
    /// Returns the server, the spawned peer ship and the core.</summary>
    private static (Srv Server, Ship Peer, StaticCore Core) BootDocked(out FltBot bot)
    {
        var server = new Srv();
        StaticCore? core = null;
        Ship? peer = null;
        short? peerSide = null;
        server.ShipSpawner = (_, m, id) =>
        {
            core = m.Core;
            peerSide = m.Sides[0].Id;
            var station = m.Sectors.SelectMany(s => s.Stations).First(st => st.SideId == peerSide.Value);
            var stationless = m.Sectors.First(s => s.Stations.Contains(station));
            // Spawn on a distinctive normal hull (the most expensive) so the
            // "buy a normal hull" leg provably changes the hull.
            var hull = m.Core.HullTypes.Values.OrderByDescending(h => h.Price).First();
            peer = new Ship(id, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = peerSide.Value, StationId = station.StationId };
            m.PlaceShip(peer, stationless);
            return (peer, stationless);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby (P180 pattern)
        bot = new FltBot();
        var b = bot;                       // `bot` is an out param — unusable inside a lambda
        b.Connect(server.BoundPort, "p470_buyer");
        b.SendLogonRequest(Request("p470_buyer"));
        Pump(server, () => peer is not null && b.ShipAssignment is not null, 4000);

        Assert.NotNull(core); Assert.NotNull(peer); Assert.NotNull(peerSide);
        // The peer must spawn UNDOCKED (station-less sector ⇒ no dock latch).
        Assert.NotNull(peer!.StationId);
        return (server!, peer!, core!);
    }

    /// <summary>⭐ Discriminating leg — a player BUY_LOADOUT for a real
    /// Constructor hull (GroupId = -1, c_habmBuilder) is REFUSED: the ship
    /// stays on its pre-purchase hull. Fails on the unfixed code, where buying
    /// the constructor succeeds and the ship swaps to it.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void BuyLoadout_for_a_constructor_hull_is_refused()
    {
        Require.Artwork();
        var (srv, peer, core) = BootDocked(out var bot);
        using (srv)
        using (bot)
        {
            // A real Constructor/Builder hull straight from the core. It must be
            // a genuine drone-only hull by the P470 rule (GroupId < 0) — otherwise
            // this isn't testing the gate that matters.
            var constructor = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmBuilder));
            Assert.True(constructor.GroupId < 0,
                $"constructor '{constructor.Name}' (HullId {constructor.HullId}) must carry a negative (signed) group id — got {constructor.GroupId}");

            short beforeHull = peer.HullType.HullId;
            Assert.NotEqual(constructor.HullId, beforeHull);

            Fund(srv, peer.Id, 1_000_000);   // plenty of money — the ONLY gate is the P470 hull rule

            bot.SendBuyLoadout(constructor.HullId, fLaunch: false);
            Pump(srv, () => false, 1500);    // allow the datagram + refusal to settle

            // Refused the same way P441 refuses an unsellable hull: ship unchanged.
            Assert.Equal(beforeHull, LiveShip(srv, peer.Id).HullType.HullId);
            Assert.NotEqual(constructor.HullId, LiveShip(srv, peer.Id).HullType.HullId);
            Console.WriteLine($"P470-REJECT: BUY_LOADOUT for drone-only Constructor '{constructor.Name}' (HullId {constructor.HullId}, group {constructor.GroupId}) refused — ship stays on hull {beforeHull}");
        }
    }

    /// <summary>⭐ Control leg — a normal player hull (GroupId >= 0) is still
    /// buyable: the ship swaps to it, so the GroupId gate is not over-broad.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_normal_player_hull_is_still_buyable()
    {
        Require.Artwork();
        var (srv, peer, core) = BootDocked(out var bot);
        using (srv)
        using (bot)
        {
            // A cheap normal (GroupId >= 0) hull distinct from the spawn hull.
            var normal = core.HullTypes.Values
                .Where(h => h.GroupId >= 0 && h.HullId != peer.HullType.HullId)
                .OrderBy(h => h.Price).First();
            Assert.True(normal.GroupId >= 0, $"'{normal.Name}' should be a player hull (group >= 0), got {normal.GroupId}");

            normal = EconomyLoadoutTestSetup.Sell(peer.Mission!, normal);
            Fund(srv, peer.Id, 1_000_000);

            bot.SendBuyLoadout(normal.HullId, fLaunch: false);
            Pump(srv, () => LiveShip(srv, peer.Id).HullType.HullId == normal.HullId, 3000);

            Assert.Equal(normal.HullId, LiveShip(srv, peer.Id).HullType.HullId);
            Console.WriteLine($"P470-BUY-OK: BUY_LOADOUT for normal player hull '{normal.Name}' (HullId {normal.HullId}, group {normal.GroupId}) accepted — ship swapped");
        }
    }
}
