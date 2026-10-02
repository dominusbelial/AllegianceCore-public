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

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P488 — live-loopback proof that the server's BUY_LOADOUT applies the native
/// successor upgrade (<c>shipIGC.h:1004,1033</c>): a docked player who requests
/// a hull or part the side has out-teched is granted (and sees, in the
/// <c>BUY_LOADOUT_ACK</c>) the side's best available <b>successor</b> — the
/// furthest buyable version in the real PCore001 chain. Red on unfixed code,
/// where the requested item is granted verbatim and the ack echoes it.
/// </summary>
[Collection("LiveNetwork")]
public class SuccessorUpgradeLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Client Successor Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 948u, DwCookie = 0x488u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 98800000000000000ul, DrmHash = "succ488",
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
        var ship = new Ship(shipId, hull, StationPos(st) + new Vector3(20f, 0f, 0f), Vector3.Zero, Orientation.Identity)
        {
            SideId = st.SideId,
        };
        mission.PlaceShip(ship, sector);
        return (ship, sector);
    }

    private static readonly StaticCore RepresentativeCore =
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>Walk a hull's successor chain by id (independent of the code
    /// under test) to the leaf, so the expected granted hull is derived from the
    /// data, not duplicated from the implementation.</summary>
    private static short HullChainLeaf(StaticCore core, short baseId)
    {
        short cur = baseId;
        var seen = new HashSet<short> { cur };
        while (core.HullTypes.TryGetValue(cur, out var h) && h.SuccessorHullId is { } nx && nx >= 0 && seen.Add(nx))
            cur = nx;
        return cur;
    }

    /// <summary>⭐ Live: requesting a hull the side has out-teched is granted
    /// the SUCCESSOR hull, and the client (which adopts the BUY_LOADOUT_ACK
    /// verbatim) lands on it. Red before P488 (the ack echoed the requested
    /// hull, so the client stayed on it).</summary>
    [Fact]
    public void Buy_loadout_grants_the_successor_hull_when_the_side_has_outteched_it_Live()
    {
        // A group-0 player hull with a real (≥2-step) successor chain.
        var baseHull = RepresentativeCore.HullTypes.Values
            .First(h => h.GroupId >= 0 && h.SuccessorHullId >= 0);
        short leaf = HullChainLeaf(RepresentativeCore, baseHull.HullId);
        Assert.NotEqual(baseHull.HullId, leaf);

        using var server = new Srv();
        SetShipSpawner(server, (core, mission, shipId) => SpawnDocked(core, mission, shipId));
        server.Boot(Config());

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "succ_hull");
        session.SendLogonRequest(Request("succ_hull"));
        int join = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(join < 4000, "timed out waiting for ship assignment");
        short myId = session.World.MyShipId!.Value;
        Fund(server, myId, 1_000_000);
        DevelopAllTech(server);   // the side has out-teched the base hull

        session.SendPlayerReady(fReady: true, shipId: myId);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, "mission never reached Started");
        int docked = PumpUntil(server, () => session.World.Ships[myId].StationId != -1, 9000);
        Assert.True(docked < 9000, "ship never docked");

        // Request the BASE hull; the ACK must grant the successor.
        session.SendBuyLoadout(baseHull.HullId, fLaunch: false);

        int granted = PumpUntil(server, () => session.World.Ships[myId].HullId == leaf, 7000);
        Assert.True(granted < 7000,
            $"client never saw the successor hull {leaf} ('{RepresentativeCore.HullTypes[leaf].Name}') — got {session.World.Ships[myId].HullId} (base {baseHull.HullId})");
        Assert.Equal(leaf, session.World.Ships[myId].HullId);
        Console.WriteLine($"P488-LIVE-HULL: requested '{baseHull.Name}' ({baseHull.HullId}) -> granted successor '{RepresentativeCore.HullTypes[leaf].Name}' ({leaf}); client ShipEntity.HullId={session.World.Ships[myId].HullId}");
    }

    /// <summary>⭐ Live: requesting a part the side has out-teched mounts the
    /// SUCCESSOR part (no hull change — the part chain applies on its own,
    /// shipIGC.h:1033), and the client's loadout reflects it.</summary>
    [Fact]
    public void Buy_loadout_mounts_the_successor_part_when_the_side_has_outteched_it_Live()
    {
        // A real weapon part with a ≥2-step successor chain (e.g. PW Lancer 1).
        var basePart = RepresentativeCore.PartTypes.Values
            .First(p => p.EquipmentType == EquipmentType.Weapon && p.SuccessorPartId >= 0);
        short leaf = basePart.SuccessorPartId;
        var seen = new HashSet<short> { basePart.PartId };
        while (RepresentativeCore.PartTypes.TryGetValue(leaf, out var np) && np.SuccessorPartId is { } nx && nx >= 0 && seen.Add(nx))
            leaf = nx;
        Assert.NotEqual(basePart.PartId, leaf);

        using var server = new Srv();
        SetShipSpawner(server, (core, mission, shipId) => SpawnDocked(core, mission, shipId));
        server.Boot(Config());

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "succ_part");
        session.SendLogonRequest(Request("succ_part"));
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

        // Same hull (no hull swap), request the BASE part → successor mounts.
        short currentHull = session.World.Ships[myId].HullId;
        session.SendBuyLoadout(currentHull, fLaunch: false,
            parts: new[] { new ExpandedPartData(basePart.PartId, 20, 0, ExpandedPartData.FullyMounted) });

        int granted = PumpUntil(server, () =>
        {
            var lo = session.World.Ships[myId].Loadout;
            return lo != null && lo.Any(p => p.PartId == leaf);
        }, 7000);
        Assert.True(granted < 7000,
            $"client loadout never contained the successor part {leaf} — got [{string.Join(",", session.World.Ships[myId].Loadout?.Select(p => p.PartId.ToString()) ?? [])}] (requested {basePart.PartId})");
        Assert.Contains(session.World.Ships[myId].Loadout!, p => p.PartId == leaf);
        Assert.DoesNotContain(session.World.Ships[myId].Loadout!, p => p.PartId == basePart.PartId);
        Console.WriteLine($"P488-LIVE-PART: requested '{basePart.Name}' ({basePart.PartId}) -> mounted successor '{RepresentativeCore.PartTypes[leaf].Name}' ({leaf}); client loadout contains {leaf}");
    }
}
