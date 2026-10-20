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
/// Live integration proof for <see cref="ConnectionSession.SendPlayerReady"/> —
/// the outbound half of PLAYER_READY(45). Per the message's own header comment
/// the server relays it "to everyone on team" — and <c>Server.cs</c>'s handler
/// relays the announce to every peer sharing the sender's side, <b>including
/// the sender itself</b> (the native client waits
/// for exactly that relay before updating its own roster entry — it never
/// applies its own click locally, <c>appmsg.cpp:2297-2312</c>).
///
/// To guarantee a same-side pair we seed the server with the same ShipSpawner
/// DockDockingTests uses (a friendly station's side), so both peers land on the
/// same side. The assertion reads this repo's own <see cref="ClientWorld"/>:
/// after A announces ready, BOTH peers' rosters show A's ship ready (the
/// same-side relay), while the echo history still captures it as an event.
/// </summary>
[Collection("LiveNetwork")]
public class PlayerReadyLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        Games =
        [
            new Gm { Name = "Ready Live Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 801u, DwCookie = 0x321u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 83100000000000000ul, DrmHash = "readyok",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // The ShipSpawner hook is `internal` (friend-gated to Allegiance.Core.Tests
    // and the tools) — unreachable directly, so reach it through reflection, the
    // same recipe DockLoadoutLiveTests uses. Seeding it places every joining ship
    // on the same friendly station's side → a guaranteed same-side pair.
    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    private static (Ship Ship, Sector Sector) SpawnOnFriendlyStationSide(
        StaticCore core, Mission mission, short shipId, out short sideId)
    {
        var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
        var sector = mission.Sectors.First(s => s.Stations.Contains(st));
        var ship = new Ship(shipId, hull, new Vector3(50f, 0f, 0f), Vector3.Zero, Orientation.Identity)
        {
            SideId = st.SideId,
        };
        mission.PlaceShip(ship, sector);
        sideId = st.SideId;
        return (ship, sector);
    }

    /// <summary>⭐ The outbound round trip: A announces ready; the same-side
    /// relay updates BOTH peers' <see cref="ClientWorld.Roster"/> for A's ship,
    /// and each still records it in the append-only echo history.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SendPlayerReady_RelaysToWholeTeam_UpdatesBothRosters()
    {
        Require.Artwork();
        using var server = new Srv();
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var r = SpawnOnFriendlyStationSide(core, mission, shipId, out _);
            return r;
        });
        server.Boot(Config());

        using var a = new ConnectionSession();
        using var b = new ConnectionSession();
        a.Connect(server.BoundPort, "rl_a");
        b.Connect(server.BoundPort, "rl_b");
        a.SendLogonRequest(Request("rl_a"));
        b.SendLogonRequest(Request("rl_b"));

        int joined = PumpUntil(server, () => a.World.MyShipId is not null && b.World.MyShipId is not null, 4000);
        Assert.True(joined < 4000, "both peers must be assigned a ship");
        short aId = a.World.MyShipId!.Value;
        short bId = b.World.MyShipId!.Value;
        Assert.NotEqual(aId, bId);                       // two distinct ships …

        // … and they share a side (the seeded spawner guarantees it).
        Assert.Equal(a.World.Ships[aId].SideId, b.World.Ships[bId].SideId);

        // A marks itself ready. The relay should update the roster on BOTH peers.
        a.SendPlayerReady(fReady: true, shipId: aId);

        int relayed = PumpUntil(server,
            () => a.World.Roster.ReadyByShipId.TryGetValue(aId, out var r1) && r1 &&
                  b.World.Roster.ReadyByShipId.TryGetValue(aId, out var r2) && r2,
            4000);
        Assert.True(relayed < 4000, "ready relay never updated both peers' rosters");

        // Current-state: A is ready on both sides of the wire.
        Assert.True(a.World.Roster.ReadyByShipId[aId]);
        Assert.True(b.World.Roster.ReadyByShipId[aId]);
        // B's own roster stays absent — it never announced (the map only holds
        // ships that toggled; TryGetValue absence reads as "not ready").
        Assert.DoesNotContain(bId, b.World.Roster.ReadyByShipId.Keys);

        // History half is preserved too — A's announce is an echoed event.
        Assert.Contains(a.World.Events.PlayerReadyEchoes, e => e.ShipId == aId && e.FReady);
        Assert.Contains(b.World.Events.PlayerReadyEchoes, e => e.ShipId == aId && e.FReady);
    }

    /// <summary>A later not-ready toggle flips the roster flag but the history
    /// keeps both announces — the roster is current state, not a log.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SendPlayerReady_NotReadyToggle_FlipsRoster_KeepsHistory()
    {
        Require.Artwork();
        using var server = new Srv();
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var r = SpawnOnFriendlyStationSide(core, mission, shipId, out _);
            return r;
        });
        server.Boot(Config());

        using var a = new ConnectionSession();
        a.Connect(server.BoundPort, "rl_toggle");
        a.SendLogonRequest(Request("rl_toggle"));

        int joined = PumpUntil(server, () => a.World.MyShipId is not null, 4000);
        Assert.True(joined < 4000, "must be assigned a ship");
        short aId = a.World.MyShipId!.Value;

        a.SendPlayerReady(fReady: true, shipId: aId);
        int ready = PumpUntil(server, () => a.World.Roster.ReadyByShipId.TryGetValue(aId, out var r) && r, 4000);
        Assert.True(ready < 4000, "ready=true never reflected in own roster");
        Assert.Single(a.World.Roster.ReadyByShipId);

        // Now toggle back to not-ready: the roster FLIPS (single entry, now false),
        // while the echo log gains a SECOND announce.
        a.SendPlayerReady(fReady: false, shipId: aId);
        int flip = PumpUntil(server, () => a.World.Roster.ReadyByShipId.TryGetValue(aId, out var r) && !r, 4000);
        Assert.True(flip < 4000, "ready=false never reflected in own roster");
        Assert.Single(a.World.Roster.ReadyByShipId);           // still one entry (map, not a log)
        Assert.False(a.World.Roster.ReadyByShipId[aId]);
        Assert.Equal(2, a.World.Events.PlayerReadyEchoes.Count); // history gained the second toggle
    }
}
