using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P522 — server-side parity leg of the undock false-red-flash fix. Native's
/// BUY_LOADOUT restock tops a bought hull's shield to full
/// (<c>FedSrv.CPP:12761-12764</c>, <c>pshield->SetFraction(1.0f)</c>), so a
/// freshly-bought hull always launches with full shields. The client-side
/// baseline reset (Game1 / DamageFlashTracker.ResetBaseline) already prevents
/// the undock false-flash regardless of the server value, but this proves the
/// server also matches native: after a hull swap via the real wire BUY_LOADOUT,
/// the ship's shield is back to 1.0 even if it was depleted beforehand.
/// Red when the P522 <c>SetShieldFraction(1.0f)</c> in the hull-change path
/// (Server.ShipCommands.cs) is removed.
/// </summary>
[Collection("LiveNetwork")]
public class P522ShieldParityLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P522 Shield Parity", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 922u, DwCookie = 0x522u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 95220000000000000ul, DrmHash = "p522shield",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    private static void Fund(Srv server, short shipId, int amount)
    {
        var field = typeof(Srv).GetField("_peerShips", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dict = (System.Collections.IDictionary)field.GetValue(server)!;
        foreach (System.Collections.DictionaryEntry e in dict)
        {
            var live = e.Value!;
            var ship = (Ship)live.GetType().GetProperty("Ship")!.GetValue(live)!;
            if (ship.Id != shipId) continue;
            var wallet = live.GetType().GetProperty("Wallet")!.GetValue(live)!;
            wallet.GetType().GetProperty("Money")!.SetValue(wallet, amount);
            return;
        }
        throw new InvalidOperationException($"peer ship {shipId} not found to fund");
    }

    private static void DevelopAllTech(Srv server)
    {
        var all = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());
        foreach (var s in server.Missions[0].Sides)
            s.DevelopmentTechs = all;
    }

    private static (Ship Ship, Sector Sector) SpawnDocked(StaticCore core, Mission mission, short shipId)
    {
        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
        var sector = mission.Sectors.First(s => s.Stations.Contains(st));
        var ship = new Ship(shipId, hull, DockTestPlacement.Inside(mission, st, hull), Vector3.Zero, Orientation.Identity)
        {
            SideId = st.SideId,
        };
        mission.PlaceShip(ship, sector);
        return (ship, sector);
    }

    private static readonly StaticCore RepresentativeCore =
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    private static short HullChainLeaf(StaticCore core, short baseId)
    {
        short cur = baseId;
        var seen = new HashSet<short> { cur };
        while (core.HullTypes.TryGetValue(cur, out var h) && h.SuccessorHullId is { } nx && nx >= 0 && seen.Add(nx))
            cur = nx;
        return cur;
    }

    /// <summary>⭐ Live: a hull swap via the real BUY_LOADOUT returns the ship's
    /// shield to full (native parity, FedSrv.CPP:12761-12764). The ship's
    /// shield is deliberately depleted to 0.4 after docking (restock has topped
    /// it) and a different hull is bought; the P522 fix is the ONLY thing that
    /// restores it to 1.0 — red when that line is removed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Buy_loadout_restores_full_shield_after_hull_swap_Live()
    {
        Require.Artwork();
        var baseHull = RepresentativeCore.HullTypes.Values
            .First(h => h.GroupId >= 0 && h.SuccessorHullId >= 0);
        short leaf = HullChainLeaf(RepresentativeCore, baseHull.HullId);
        Assert.NotEqual(baseHull.HullId, leaf);

        using var server = new Srv();
        SetShipSpawner(server, (core, mission, shipId) => SpawnDocked(core, mission, shipId));
        server.Boot(Config());

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "p522shield");
        session.SendLogonRequest(Request("p522shield"));
        int join = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(join < 4000, "timed out waiting for ship assignment");
        short myId = session.World.MyShipId!.Value;
        Fund(server, myId, 1_000_000);
        DevelopAllTech(server);

        session.SendPlayerReady(fReady: true, shipId: myId);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, "mission never reached Started");
        int docked = PumpUntil(server, () => session.World.Ships[myId].StationId != -1, 9000);
        Assert.True(docked < 9000, "ship never docked");

        // The dock auto-restock has topped the shield to full; wound it to 0.4
        // so the hull-buy path's full-shield reset is the ONLY restorer.
        var serverShip = server.Missions[0].Sectors
            .SelectMany(s => s.Models).OfType<Ship>()
            .First(s => s.Id == myId);
        serverShip.SetShieldFraction(0.4f);
        Assert.Equal(0.4f, serverShip.ShieldFraction, 3);

        // Buy the BASE hull; the side has out-teched it so the server grants
        // the SUCCESSOR (a genuinely different hull) → ChangeHull runs.
        session.SendBuyLoadout(baseHull.HullId, fLaunch: false);
        int granted = PumpUntil(server, () => serverShip.HullType.HullId == leaf, 7000);
        Assert.True(granted < 7000,
            $"server never swapped hull to successor {leaf} — got {serverShip.HullType.HullId}");
        Assert.Equal(leaf, serverShip.HullType.HullId);

        // Native tops the shield to full on BUY_LOADOUT; the P522 fix restores
        // the wounded 0.4 back to 1.0.
        Assert.Equal(1.0f, serverShip.ShieldFraction, 3);
        Console.WriteLine($"P522-SHIELD: hull swap {baseHull.HullId}->{leaf} restored ShieldFraction 0.4 -> {serverShip.ShieldFraction:F3} (native FedSrv.CPP:12761-12764)");
    }
}
