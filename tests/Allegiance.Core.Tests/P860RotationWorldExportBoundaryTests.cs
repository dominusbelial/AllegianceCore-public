using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P860 — root cause of the recurring P480 rotation failure
/// <i>"station 1052 was exported to a side that had not seen it"</i>.
/// <para><b>What actually happens.</b> Nothing is leaked. The recurring failure is
/// the P480 harness attributing an <c>EXPORT(26)/OT_station</c> to the wrong
/// world:</para>
/// <list type="number">
/// <item>The test seats four bots. The default spawner assigns a side by peer
/// count (<c>_peerShips.Count % Sides.Count</c>, <c>SpawnDefaultShip</c>), so the
/// 2nd and 3rd peers are spawned on side 1 and side 0 respectively — and each
/// receives the <b>side it was spawned on</b>'s garrison in its own first join
/// burst: native's join sync exports a station only to a side that has seen it
/// (<c>FedSrv/fsmission.cpp:4215-4222</c>), and a side always sees its own station
/// (<c>FedSrv/FedSrv.CPP:10904-10912</c>; <c>Igc/common.cpp:3004-3017</c> makes
/// every station invisible to every other side at creation).</item>
/// <item>The test then <c>TryJoinSide</c>s those peers onto their final sides, so
/// the 2nd peer (final side 0) legitimately holds side 1's garrison (station 1052,
/// Quaoar) from before the switch.</item>
/// <item>The rotation rebuilds the SAME static map, so the finished and the fresh
/// mission use the <b>same station ids</b>. A received station export therefore
/// cannot be attributed to a world by its content — only by its position in the
/// connection's byte stream. The old harness cleared the accumulators on the test
/// thread, which does not order itself against the socket/publisher, so a
/// pre-rotation export could be appended after that clear and be measured as if it
/// were part of the rotated mission's burst, where its side has (correctly) never
/// seen the enemy garrison.</item>
/// </list>
/// <para><b>Native intent for the boundary.</b> Native queues <c>GAME_OVER</c> to
/// the mission group and only then resets the mission
/// (<c>FedSrv/fsmission.cpp:3437</c> <c>QueueGameoverMessage</c>, <c>:3490</c>
/// <c>SetStage(STAGE_NOTSTARTED)</c>), and it re-sends no object sync at a reset at
/// all (<c>Igc/missionigc.cpp:3804-3828</c> <c>ResetMission</c>, plus
/// <c>GenerateMission</c> from <c>:3830</c>) — its client simply keeps the objects
/// it already has. The port re-sends the fresh world's exports to migrated peers
/// (P480), so it owns a boundary, and that boundary is the game-over message:
/// <see cref="FltBot.ResetWorldExportsOnGameOver"/> performs the clear inside the
/// receive loop at exactly that point of the stream.</para>
/// </summary>
[Collection("LiveNetwork")]
public class P860RotationWorldExportBoundaryTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // XXV_simple: 13 sectors, one station per side — side 0's Garrison (1051) in
    // Ceto (1053), side 1's Garrison (1052) in Quaoar (1061).
    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Rotation Keep Players", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 941u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9430000000000000000ul, DrmHash = "keep",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

    private static short Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, $"{name} did not seat");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(x => x.Ship.Id == shipId).Ship;

    /// <summary>Whether <paramref name="mission"/>'s station <paramref name="stationId"/>
    /// is visible to <paramref name="sideId"/> (the port's <c>SeenBySide</c>, the
    /// same per-side flag native's join export reads).</summary>
    private static bool SeesStation(Mission mission, short stationId, short sideId) =>
        mission.Sectors.SelectMany(s => s.StationRuntimes)
            .Any(rt => rt.StationId == stationId && rt.SeenBySide(sideId));

    private static string Describe(Mission mission, short sideId) => string.Join("; ",
        mission.Sectors.SelectMany(s => s.StationRuntimes)
            .Select(rt => $"station {rt.StationId} '{(string?)rt.Data.Name}' side {rt.SideId} cluster {rt.Data.ClusterId} " +
                          $"seenBySide{sideId}={rt.SeenBySide(sideId)}"));

    /// <summary>
    /// The shared setup: four bots, two per side, with two of them seated by the
    /// default spawner on the other side first (round robin) and then switched.
    /// Returns the two switched peers' final sides plus everything the assertions
    /// need. Deliberately no world-export clear: the pre-rotation world is left in
    /// the accumulators so the boundary itself is what separates the worlds.
    /// </summary>
    private sealed class Scenario : IDisposable
    {
        public required Srv Server { get; init; }
        public required FltBot A0 { get; init; }
        public required FltBot A1 { get; init; }
        public required FltBot B0 { get; init; }
        public required FltBot B1 { get; init; }
        public required short Side0 { get; init; }
        public required short Side1 { get; init; }
        public required Mission OldMission { get; init; }
        public required HashSet<short> OldShipIds { get; init; }
        public required (FltBot Bot, short Side)[] Bots { get; init; }

        public void Dispose()
        {
            A0.Dispose(); A1.Dispose(); B0.Dispose(); B1.Dispose();
            Server.Dispose();
        }
    }

    private static Scenario Boot()
    {
        var server = new Srv();
        server.Boot(Config());
        var a0 = new FltBot();
        var a1 = new FltBot();
        var b0 = new FltBot();
        var b1 = new FltBot();
        try
        {
            short side0 = server.Missions[0].Sides[0].Id;
            short side1 = server.Missions[0].Sides[1].Id;

            // Seating order matters: the default spawner's round robin puts peer 1
            // and peer 3 on side 0, peers 2 and 4 on side 1 (SpawnDefaultShip:
            // `mission.Sides[_peerShips.Count % mission.Sides.Count]`), and each
            // bot's first join burst exports ITS THEN-SIDE's garrison.
            short a0Id = Seat(server, a0, "KA0");
            short a1Id = Seat(server, a1, "KA1");
            short b0Id = Seat(server, b0, "KB0");
            short b1Id = Seat(server, b1, "KB1");
            Assert.Equal(side0, ShipOf(server, a0Id).SideId);
            Assert.Equal(side1, ShipOf(server, a1Id).SideId);   // spawned on the ENEMY home
            Assert.Equal(side0, ShipOf(server, b0Id).SideId);   // spawned on the ENEMY home
            Assert.Equal(side1, ShipOf(server, b1Id).SideId);

            Assert.True(server.TryJoinSide(ShipOf(server, a0Id), side0));
            Assert.True(server.TryJoinSide(ShipOf(server, a1Id), side0));   // switches 1 -> 0
            Assert.True(server.TryJoinSide(ShipOf(server, b0Id), side1));   // switches 0 -> 1
            Assert.True(server.TryJoinSide(ShipOf(server, b1Id), side1));

            var bots = new (FltBot Bot, short Side)[] { (a0, side0), (a1, side0), (b0, side1), (b1, side1) };
            var oldShipIds = bots.Select(p => p.Bot.ShipAssignment!.Ship.ShipId).ToHashSet();

            Flush(server);
            return new Scenario
            {
                Server = server, A0 = a0, A1 = a1, B0 = b0, B1 = b1,
                Side0 = side0, Side1 = side1, OldMission = server.Missions[0],
                OldShipIds = oldShipIds, Bots = bots,
            };
        }
        catch
        {
            a0.Dispose(); a1.Dispose(); b0.Dispose(); b1.Dispose();
            server.Dispose();
            throw;
        }
    }

    /// <summary>Drive a real #draw → OfferDraw → AcceptDraw → game-over chain (all
    /// four voting), exactly like the P480 test, so the rotation + migration run.</summary>
    private static void DriveDrawToGameOver(Scenario s)
    {
        foreach (var (bot, _) in s.Bots)
        {
            bot.ResetGameOvers();
            bot.ResetMissionStages();
            bot.ResetBallotSignals();
        }

        // A draw ballot only exists in a started game (native's ballot gate).
        s.Server.Missions[0].Stage = MissionStage.Started;
        s.B0.SendChat("#draw");
        int offer = PumpUntil(s.Server,
            () => s.B1.Ballots.Any(x => x.OtInitiator == (short)IgcObjectType.Ship), 800);
        Assert.True(offer < 800, "Bravo teammate did not receive the offer-draw ballot");
        s.B1.SendVote(s.B1.Ballots.Single(x => x.OtInitiator == (short)IgcObjectType.Ship).BallotId, true);

        int acc = PumpUntil(s.Server,
            () => s.A0.Ballots.Any(x => x.OtInitiator == (short)IgcObjectType.Side)
               && s.A1.Ballots.Any(x => x.OtInitiator == (short)IgcObjectType.Side), 800);
        Assert.True(acc < 800, "Alpha bots did not receive the accept-draw ballot");
        s.A0.SendVote(s.A0.Ballots.Single(x => x.OtInitiator == (short)IgcObjectType.Side).BallotId, true);
        s.A1.SendVote(s.A1.Ballots.Single(x => x.OtInitiator == (short)IgcObjectType.Side).BallotId, true);

        int ended = PumpUntil(s.Server, () => s.A0.GameOvers.Count >= 1, 800);
        Assert.True(ended < 800, "game-over did not fire after the accept-draw passed");
        Assert.NotSame(s.OldMission, s.Server.Missions[0]);
    }

    /// <summary>Wait for one migrated peer's fresh-joiner burst to have fully landed
    /// (the same synchronisation the P480 test uses).</summary>
    private static Mission AwaitRotatedBurst(Scenario s, FltBot bot)
    {
        var newMission = s.Server.Missions[0];
        int landed = PumpUntil(s.Server,
            () => bot.MissionStages.Any(m => m.Stage == (int)MissionStage.NotStarted)
               && bot.ShipAssignment is not null && !s.OldShipIds.Contains(bot.ShipAssignment!.Ship.ShipId)
               && bot.ClusterExports.Count >= newMission.Sectors.Count
               && bot.StationExports.Count >= 1,
            2000);
        Assert.True(landed < 2000, "the migrated peer's fresh-joiner burst did not land");
        return newMission;
    }

    /// <summary>
    /// ⭐ P860 — the deterministic reproduction of the ordering behind the recurring
    /// failure, with no load involved.
    /// <list type="number">
    /// <item><b>Ordering exists (precondition).</b> The peer the spawner seats on
    /// side 1 and the test then moves to side 0 holds side 1's garrison export from
    /// before the switch — a station its current side has never seen. Deterministic:
    /// it is in that peer's very first join burst, before any switch.</item>
    /// <item><b>Only the boundary may separate the worlds.</b> The accumulators are
    /// NOT cleared on the test thread; the only reset is the stream-ordered
    /// game-over boundary, exactly native's order
    /// (<c>Fsmission.cpp:3437</c> then <c>:3490</c>).</item>
    /// <item><b>Invariant.</b> Every station export the peer holds after the
    /// boundary is one its side has seen in the rotated mission.</item>
    /// </list>
    /// Without the boundary (a caller-thread clear before the draw, or no clear at
    /// all) the stale pre-rotation export is still there and this test fails — that
    /// is the gated failure verbatim: <i>"station 1052 was exported to a side that
    /// had not seen it"</i>.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Stale_export_from_before_the_rotation_is_not_attributed_to_the_new_mission()
    {
        Require.Artwork();
        using var s = Boot();

        // (1) The ordering exists — deterministic, no load: the peer the spawner
        // seated on side 1 received side 1's own garrison (station 1052, Quaoar)
        // before the test switched it to side 0, and side 0 has never seen it.
        var stale = s.A1.StationExports.Where(st => !SeesStation(s.OldMission, st.Station.StationId, s.Side0)).ToArray();
        Assert.True(stale.Length > 0,
            "P860 precondition: the side-switching peer must hold an export from the side it came from " +
            $"({Describe(s.OldMission, s.Side0)}); received: " +
            string.Join(" | ", s.A1.StationExports.Select(e => $"{e.Station.StationId}/side {e.Station.SideId}@{e.Station.ClusterId}")));
        Assert.Contains(stale, st => st.Station.StationId == (short)1052);   // side 1's garrison

        // (2) The only reset is the ordered boundary: game-over ends the old world.
        foreach (var (bot, _) in s.Bots)
            bot.ResetWorldExportsOnGameOver();

        DriveDrawToGameOver(s);

        // (3) Every station export held after the boundary belongs to the new world
        // and is one the recipient's side has seen there.
        foreach (var (bot, side) in s.Bots)
        {
            var newMission = AwaitRotatedBurst(s, bot);
            var botSide = ShipOf(s.Server, bot.ShipAssignment!.Ship.ShipId).SideId!.Value;
            Assert.Equal(side, botSide);
            Assert.All(bot.StationExports, st => Assert.True(
                SeesStation(newMission, st.Station.StationId, botSide),
                $"P860: station {st.Station.StationId} was exported to a side that had not seen it in the rotated mission " +
                $"(export '{st.Station.Name}' side {st.Station.SideId} cluster {st.Station.ClusterId}; recipient side {botSide}, " +
                $"stage {newMission.Stage}); rotated [{Describe(newMission, botSide)}]; received: " +
                string.Join(" | ", bot.StationExports.Select(e => $"{e.Station.StationId}/side {e.Station.SideId}@{e.Station.ClusterId}"))));
        }
    }

    /// <summary>
    /// ⭐ P860 — the fog-leak invariant at its source, machine-checked over the
    /// rotation itself. Every <c>EXPORT(26)/OT_station</c> the server puts on the
    /// wire after the finished mission is drained belongs to a station the
    /// recipient's side sees in the rotated mission — native's gate
    /// (<c>Fsmission.cpp:4215-4222</c>, re-checked for the live object in
    /// <c>ShowObject</c>, <c>FedSrv.CPP:10750-10778</c>). Captured from the server's
    /// own send sink, so it does not depend on any bot's receive timing.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rotation_exports_no_station_to_a_side_that_has_not_seen_it()
    {
        Require.Artwork();
        using var s = Boot();

        // Drain everything already queued (the pre-rotation world) so the capture
        // below contains only what the rotation and its migration put on the wire.
        s.Server.FlushBroadcastSnapshots();
        var frames = new List<(ConnectionId Peer, byte[] Bytes)>();
        var gate = new object();
        s.Server._testSendCapture = (conn, bytes) => { lock (gate) frames.Add((conn, bytes.ToArray())); };
        try
        {
            DriveDrawToGameOver(s);
            foreach (var (bot, _) in s.Bots) AwaitRotatedBurst(s, bot);
            s.Server.FlushBroadcastSnapshots();
        }
        finally
        {
            s.Server._testSendCapture = null;
        }

        var newMission = s.Server.Missions[0];

        // Resolve each connection to the ship it was launched with (the burst's own
        // SET_CLUSTER carries it), then to the side that ship sits on.
        var ownShipByPeer = new Dictionary<ConnectionId, short>();
        var stationExports = new List<(ConnectionId Peer, short StationId, short StationSide, short ClusterId)>();
        foreach (var (peer, bytes) in frames)
        {
            byte[][] messages;
            try { messages = FedPacket.Unpack(bytes); }
            catch (FedCrcException) { continue; }
            foreach (var m in messages)
            {
                if (m.Length < 4) continue;
                ushort fmid = FedMessageHeader.Read(m).Fmid;
                if (fmid == SetCluster.Fmid)
                    ownShipByPeer[peer] = SetCluster.Decode(m).Ship.ShipId;
                else if (fmid == ExportStation.Fmid && m.Length >= 12
                         && BitConverter.ToInt32(m.AsSpan(8, 4)) == ExportStation.OtStation)
                {
                    var st = ExportStation.Decode(m);
                    stationExports.Add((peer, st.Station.StationId, st.Station.SideId, st.Station.ClusterId));
                }
            }
        }

        Assert.NotEmpty(stationExports);   // the rotation does re-send station identity
        foreach (var (peer, stationId, stationSide, clusterId) in stationExports)
        {
            Assert.True(ownShipByPeer.TryGetValue(peer, out var ownShip),
                $"P860: no SET_CLUSTER captured for peer {peer} — cannot resolve its side");
            short recipientSide = ShipOf(s.Server, ownShip).SideId!.Value;
            Assert.True(SeesStation(newMission, stationId, recipientSide),
                $"P860 FOG LEAK: station {stationId} (side {stationSide}, cluster {clusterId}) was exported to peer {peer} " +
                $"on side {recipientSide}, which has not seen it in the rotated mission " +
                $"[{Describe(newMission, recipientSide)}]");
        }
    }
}
