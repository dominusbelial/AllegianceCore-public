using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Live integration proof for the two new capabilities, against a real
/// in-process server over real loopback TCP:
///
///  1. WORLD POPULATION — a <see cref="ConnectionSession"/>'s join busts the
///     real <c>EXPORT(26)</c> object table (clusters/stations/warps/asteroids)
///     plus <c>CLUSTERINFO(200)</c>, so <see cref="ClientWorld"/> actually knows
///     what the universe looks like, not just where "my ship" is.
///
///  2. TEAMMATE VISIBILITY — three peers join a 2-team map (default round-robin
///     seating ⇒ a same-side pair exists by pigeonhole, the same premise as
///     ShipStatusServerTests' P58/P62/P63). The peer's <see cref="ClientWorld"/>
///     must learn a same-side teammate's ship id from its EXPORT/PLAYERINFO — a
///     <see cref="ShipEntity"/> that is NOT <see cref="ShipEntity.IsMine"/>,
///     whose <see cref="ShipEntity.SideId"/> matches the side both peers report
///     for themselves, and whose <see cref="ShipEntity.CharacterName"/> is the
///     teammate's real logon name.
///
/// NOTE: the server's authoritative side map (<c>Server.Ships</c>,
/// <c>Server.ShipSpawner</c>) is <c>internal</c> to Allegiance.Core.Tests, and
/// this test assembly must not depend on those internals — so "same side" is
/// established from the clients' own self-reported side ids (each peer's own
/// join-burst PLAYERINFO, real id + real ISide), the honest observable
/// equivalent. The server's own-ship EXPORT deliberately carries ShipId =
/// NA(-1), so a peer's self-side cannot come from that (teammate exports only);
/// hence self-side is read from the own PlayerInfo.
/// </summary>
[Collection("LiveNetwork")]
public class WorldPopulationLiveTests
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
            // m2xspiderw.igc: a real map the sibling tests prove carries ~13
            // clusters, stations, warps AND ~134 asteroids (XXV_simple has no
            // asteroids, so world-population coverage needs this one).
            // P797 — ShowHomeSector is the native mission option that reveals every
            // model in a cluster where the side owns a station
            // (Igc/mapmakerigc.cpp:969-997). This test's subject is the join
            // burst's world population, and asteroids are per side now, so the
            // home cluster's rocks are what a fresh side legitimately knows.
            new Gm { ShowHomeSector = true, Name = "World Population", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 047u, DwCookie = 0x647u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 04900000000000000ul, DrmHash = "worldpop",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>The peer's self-side id, derived from its own join-burst
    /// PLAYERINFO(10) (real ship id + real ISide — the P63 own-player send).
    /// Null until that PlayerInfo has landed on the real-id ship entity.</summary>
    private static short? SelfSide(ClientWorld world) =>
        world.MyShipId is { } myId && world.Ships.TryGetValue(myId, out var me)
            ? me.Player?.ISide
            : null;

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PeersLearnSameSideTeammateShip_AndWorldPopulation_FromRealServer()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        // P797 TEST FIXTURE: asteroids are exported per side now (native
        // fsmission.cpp:4206), and this map's 134 rocks live in NON-home sectors,
        // so a fresh side legitimately knows none of them. This test's subject is
        // the join-burst world-population plumbing, so start the mission (the
        // visibility sweep only runs on a started cluster, clusterIGC.cpp:255) and
        // reveal the map's rocks to both sides — the native SetSideVisibility
        // grant — then let the P797 discovery sweep deliver them. P798 replaces
        // this with real scouting.
        foreach (var m in server.Missions)
        {
            m.Stage = MissionStage.Started;
            foreach (var side in m.Sides)
                foreach (var sector in m.Sectors)
                    foreach (var rock in sector.Mineable)
                        rock.SideVisibility.SetSideVisibility(side.Id, true);
        }

        string[] names = { "wp_one", "wp_two", "wp_three" };
        var sessions = new ConnectionSession[3];
        for (int i = 0; i < sessions.Length; i++)
        {
            sessions[i] = new ConnectionSession();
            sessions[i].Connect(server.BoundPort, names[i]);
            sessions[i].SendLogonRequest(Request(names[i]));
        }

        // Everyone joins and has its own ship id + own PLAYERINFO (real ISide).
        int join = PumpUntil(server, () => sessions.All(s => SelfSide(s.World) is not null), 8000);
        Assert.True(join < 8000, $"some peer never learned its own PlayerInfo: " +
            string.Join(", ", sessions.Select(s => s.World.MyShipId)));
        short[] ids = sessions.Select(s => s.World.MyShipId!.Value).ToArray();
        short[] selfSides = sessions.Select(s => SelfSide(s.World)!.Value).ToArray();
        Assert.Equal(3, ids.Distinct().Count());   // three distinct ships

        // Round-robin 3 peers / 2 teams ⇒ a same-side pair exists by pigeonhole.
        // Find it from the clients' own self-reported sides.
        int a = -1, b = -1;
        for (int i = 0; i < sessions.Length && a < 0; i++)
            for (int j = i + 1; j < sessions.Length; j++)
                if (selfSides[i] == selfSides[j]) { a = i; b = j; break; }
        Assert.True(a >= 0, "3 peers / 2 teams must yield a same-side pair");
        short commonSide = selfSides[a];
        Assert.Equal(commonSide, selfSides[b]);   // genuinely same side

        var obs = sessions[a];
        var sub = sessions[b];

        // The observation peer must learn the subject's ship (its EXPORT +
        // PLAYERINFO), while ALSO picking up the real map's world population.
        int learned = PumpUntil(server,
            () => obs.World.Ships.ContainsKey(ids[b])
                  && obs.World.Ships[ids[b]].Player is not null
                  && obs.World.Clusters.Count > 0
                  && obs.World.Stations.Count > 0
                  && obs.World.Warps.Count > 0
                  && obs.World.Asteroids.Count > 0
                  && obs.World.Graph is not null,
            8000);
        var subShip = obs.World.Ships.GetValueOrDefault(ids[b]);
        Assert.True(learned < 8000,
            $"observation peer timed out — sawB={obs.World.Ships.ContainsKey(ids[b])} subPl={(subShip?.Player is not null)} " +
            $"C={obs.World.Clusters.Count} S={obs.World.Stations.Count} W={obs.World.Warps.Count} " +
            $"A={obs.World.Asteroids.Count} G={(obs.World.Graph?.Clusters.Count)}/{obs.World.Graph?.Warps.Count} " +
            $"myId={ids[a]} subId={ids[b]} mySide={selfSides[a]} subSide={selfSides[b]}");

        // ── teammate visibility ──────────────────────────────────────────
        var teammate = obs.World.Ships[ids[b]];
        Assert.False(teammate.IsMine, "the subject ship is NOT mine");
        Assert.Equal(commonSide, teammate.SideId);            // same side as both report
        Assert.Equal(names[b], teammate.CharacterName);       // subject's real logon name
        Assert.NotNull(teammate.Player);                      // PLAYERINFO(10) attached
        Assert.Equal(names[b], teammate.Player.CharacterName);
        Assert.Equal(commonSide, teammate.Player.ISide);

        // My own ship stays correctly flagged in the same world. (Its entity
        // SideId is never set by a real-id EXPORT — the server's own-ship EXPORT
        // is the NA(-1) placeholder, skipped — so assert via the own PlayerInfo.)
        Assert.True(obs.World.Ships[ids[a]].IsMine);
        var own = obs.World.Ships[ids[a]].Player!;
        Assert.NotNull(own);
        Assert.Equal(commonSide, own.ISide);

        // Mutual: the subject learns the observer too.
        int mutual = PumpUntil(server, () => sub.World.Ships.ContainsKey(ids[a]), 8000);
        Assert.True(mutual < 8000, "subject never learned observer's ship");
        Assert.False(sub.World.Ships[ids[a]].IsMine);

        // ── world population ────────────────────────────────────────────
        // Honest count > 0 — the real m2xspiderw map has at least one of each
        // (and ~134 asteroids, unlike XXV_simple).
        Assert.NotEmpty(obs.World.Clusters);
        Assert.NotEmpty(obs.World.Stations);
        Assert.NotEmpty(obs.World.Warps);
        Assert.NotEmpty(obs.World.Asteroids);
        Assert.NotNull(obs.World.Graph);
        Assert.NotEmpty(obs.World.Graph.Clusters);
        Assert.NotEmpty(obs.World.Graph.Warps);

        // Spot-check key integrity: each dict keyed by its own object's id.
        Assert.Equal(obs.World.Clusters.Keys.First(),
            obs.World.Clusters.Values.First().ClusterId);
        Assert.Equal(obs.World.Stations.Keys.First(),
            obs.World.Stations.Values.First().StationId);
        Assert.Equal(obs.World.Warps.Keys.First(),
            obs.World.Warps.Values.First().WarpId);
        Assert.Equal(obs.World.Asteroids.Keys.First(),
            obs.World.Asteroids.Values.First().AsteroidId);
    }
}
