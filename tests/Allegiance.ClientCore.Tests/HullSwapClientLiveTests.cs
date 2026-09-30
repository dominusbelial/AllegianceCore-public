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
/// P335 — Stage 2 live proof, CLIENT side. P334 made the server's
/// BUY_LOADOUT actually swap the ship's simulated hull (Ship.ChangeHull) and
/// reply with a BUY_LOADOUT_ACK whose HullId echo and part tail are the REAL
/// post-swap state (including the CanMount drop of non-fitting parts). This is
/// the client-facing companion to Core.Tests' HullSwapLiveTests: it drives a
/// real <see cref="ConnectionSession"/> over loopback and asserts that
/// <see cref="ClientWorld"/>'s own <see cref="ShipEntity"/> — the thing the
/// MonoGame renderer draws and the HUD reads — lands on the REAL new hull, not
/// the old one, and reflects the real (post-drop) mounted parts.
///
/// This is what the task's scope item 1/2 call for: confirming the client's
/// wire handling applies the server's authoritative report rather than an
/// optimistic guess. Because the client adopts the ACK's HullId/Parts verbatim
/// (ClientWorld.Apply(BuyLoadoutAck)), the rendered model and loadout both
/// follow the server's real hull swap.
/// </summary>
[Collection("LiveNetwork")]
public class HullSwapClientLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Client Hull Swap Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 933u, DwCookie = 0x335u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 93500000000000000ul, DrmHash = "hullswapcli",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    // ShipSpawner is internal (friend-gated) — reach it via reflection, exactly
    // as DockLoadoutLiveTests does (same coordinate/offset recipe).
    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    /// <summary>P441 — fund a peer's wallet via reflection (Server.Ships is
    /// internal and invisible to this assembly).</summary>
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

    /// <summary>P441 — a fully-researched side (every tech bit set) so the docked
    /// garrison sells the different/gated hull these swaps buy — a legitimate
    /// late-game purchase that passes the tech gate without touching the rule.</summary>
    private static void DevelopAllTech(Srv server)
    {
        var all = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());
        foreach (var s in server.Missions[0].Sides)
            s.DevelopmentTechs = all;
    }

    /// <summary>Seat the peer's ship inside a friendly station's DockRadius (the
    /// dock arrangement DockDockingTests/DockLoadoutLiveTests use).</summary>
    private static (Ship Ship, Sector Sector) SpawnDocked(
        StaticCore core, Mission mission, short shipId, out short stationId)
    {
        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
        var sector = mission.Sectors.First(s => s.Stations.Contains(st));
        var ship = new Ship(shipId, hull, StationPos(st) + new Vector3(20f, 0f, 0f), Vector3.Zero, Orientation.Identity)
        {
            SideId = st.SideId,
        };
        mission.PlaceShip(ship, sector);
        stationId = st.StationId;
        return (ship, sector);
    }

    private static readonly StaticCore RepresentativeCore =
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>⭐ Live proof of the client half of the swap: after a real docked
    /// launch naming a genuinely different hull, the CLIENT's ShipEntity.HullId
    /// — the value the renderer resolves the model from and that the shop's
    /// "current ship" reads — is the REAL new hull, not the old one. Red before
    /// the ACK's authoritative hull is applied.</summary>
    [Fact]
    public void Hull_swap_updates_the_clients_ship_entity_to_the_real_new_hull_Live()
    {
        using var server = new Srv();
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var r = SpawnDocked(core, mission, shipId, out _);
            return r;
        });
        server.Boot(Config());

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "cli_swap");
        session.SendLogonRequest(Request("cli_swap"));

        int join = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(join < 4000, "timed out waiting for ship assignment");
        short myId = session.World.MyShipId!.Value;
        Fund(server, myId, 1_000_000);   // P441: fund the swap
        DevelopAllTech(server);          // P441: developed side sells the new hull

        session.SendPlayerReady(fReady: true, shipId: myId);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, $"mission never reached Started (stage={server.Missions[0].Stage})");

        int docked = PumpUntil(server, () => session.World.Ships[myId].StationId != -1, 8000);
        Assert.True(docked < 8000, "ship never docked before launch");

        short hullA = session.World.Ships[myId].HullId;
        Assert.NotEqual((short)0, hullA);
        // A genuinely different hull (highest HullId vs the spawn's lowest).
        var targetHull = RepresentativeCore.HullTypes.Values.OrderByDescending(h => h.HullId).First();
        Assert.NotEqual(hullA, targetHull.HullId);

        // The Hangar "Launch" click: BUY_LOADOUT(fLaunch=true) with the new hull.
        session.SendBuyLoadout(targetHull.HullId, fLaunch: true);

        // The client adopts the ACK's authoritative hull AND the server undocks.
        int swapped = PumpUntil(server,
            () => session.World.Ships[myId].HullId == targetHull.HullId &&
                  session.World.Ships[myId].StationId == -1,
            8000);
        Assert.True(swapped < 8000,
            $"client ShipEntity never reached the new hull {targetHull.HullId} (HullId={session.World.Ships[myId].HullId}, StationId={session.World.Ships[myId].StationId})");
        Assert.Equal(targetHull.HullId, session.World.Ships[myId].HullId);
    }

    /// <summary>⭐ Live proof of the client half of the mounted-part policy: swap
    /// into a hull that can't host a requested weapon — the server (P334) DROPS
    /// it — and the client's ShipEntity.Loadout reflects the real (post-drop)
    /// mounted set, never a UI state promising the dropped part.</summary>
    [Fact]
    public void Hull_swap_into_an_incompatible_hull_drops_the_part_on_the_clients_loadout_Live()
    {
        // A (part, fits-hull, nonfits-hull) triple from the REAL core, found by
        // scan (independent of the spawn hull): the part mounts on fitHull but
        // not on badHull, so swapping onto badHull must drop it server-side.
        DataPartTypeIGC droppingPart = default;
        DataHullTypeIGC badHull = default;
        bool found = false;
        foreach (var p in RepresentativeCore.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon))
        {
            DataHullTypeIGC? b = null;
            foreach (var h in RepresentativeCore.HullTypes.Values)
                if (!h.CanMountPart(p)) { b ??= h; }
            if (b.HasValue)
            {
                droppingPart = p;
                badHull = b.Value;
                found = true;
                break;
            }
        }
        Assert.True(found, "no (part, nonfitting hull) pair exists in PCore001");

        using var server = new Srv();
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var r = SpawnDocked(core, mission, shipId, out _);
            return r;
        });
        server.Boot(Config());

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "cli_drop");
        session.SendLogonRequest(Request("cli_drop"));

        int join = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(join < 4000, "timed out waiting for ship assignment");
        short myId = session.World.MyShipId!.Value;
        Fund(server, myId, 1_000_000);   // P441: fund the swap
        DevelopAllTech(server);          // P441: developed side sells the new hull

        session.SendPlayerReady(fReady: true, shipId: myId);
        int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
        Assert.True(started < 2000, $"mission never reached Started (stage={server.Missions[0].Stage})");

        int docked = PumpUntil(server, () => session.World.Ships[myId].StationId != -1, 8000);
        Assert.True(docked < 8000, "ship never docked before launch");
        short hullA = session.World.Ships[myId].HullId;
        Assert.NotEqual(hullA, badHull.HullId);   // the swap is genuinely a hull change

        // Launch with the new (incompatible) hull + the weapon it can't mount.
        session.SendBuyLoadout(badHull.HullId, fLaunch: true,
            parts: new[] { new ExpandedPartData(droppingPart.PartId, 20, 0, ExpandedPartData.FullyMounted) });

        int swapped = PumpUntil(server,
            () => session.World.Ships[myId].HullId == badHull.HullId && session.World.Ships[myId].StationId == -1,
            8000);
        Assert.True(swapped < 8000, "client never reached the incompatible hull / undocked");

        // The client's mounted-part tail is the REAL post-drop set: the weapon
        // the new hull can't host is NOT there (it was dropped server-side).
        Assert.Equal(badHull.HullId, session.World.Ships[myId].HullId);
        var loadout = session.World.Ships[myId].Loadout;
        Assert.NotNull(loadout);
        Assert.DoesNotContain(loadout!, p => p.PartId == droppingPart.PartId);
    }
}
