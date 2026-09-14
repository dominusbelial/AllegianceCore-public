using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// The client-side mirror of AllegianceCore's own
/// <c>TeammatePositionBroadcastTests</c>. That test proved the (once-read-only)
/// server's <c>BroadcastShipStates</c> teammate loop actually broadcasts a
/// same-side teammate's <c>SINGLE_SHIP_UPDATE</c> to observers (P85). This
/// repository's README previously listed "teammate ship movement (end-to-end)"
/// as <em>not yet covered</em> because a live two-ship test showed the server
/// only ever sent each ship's update to its own peer. That server fix has since
/// landed — so the client-side counterpart must now show the teammate's live
/// position streaming into <b>our own <see cref="ClientWorld"/> entity table</b>,
/// over multiple distinct observations, with the enemy never appearing.
/// </summary>
[Collection("LiveNetwork")]
public class TeammateMovementLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg Config(string name = "Teammate Position", int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = name, Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 713u, DwCookie = 0xB0Du,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 81300000000000000ul, DrmHash = "teammove",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ The restored end-to-end teammate-movement regression, from
    /// the client's perspective: a same-side teammate's commanded thrust must
    /// stream into <b>this</b> repo's <see cref="ClientWorld"/> (on the observer
    /// side) as real position changes over MULTIPLE observations — and the
    /// cross-side enemy must never enter the observer's entity table at all.
    /// 3 sessions / 2 teams (round-robin) guarantees a same-side pair + an enemy.
    /// Each pump iteration we sample the observer's entity table, collecting the
    /// distinct positions the teammate's <c>Latest</c> passes through, and we
    /// stop once the table has seen ≥ 3 distinct positions (an initial spawn +
    /// two real movements — proof of a live stream, not a single update).</summary>
    [Fact]
    public void TeammateMovement_StreamsIntoClientWorld_MultipleObservations_EnemyNeverPresent()
    {
        using var server = new Srv();
        server.Boot(Config());
        var sessions = new[] { new ConnectionSession(), new ConnectionSession(), new ConnectionSession() };
        string[] names = { "tmv_subject", "tmv_observer", "tmv_enemy" };
        try
        {
            for (int i = 0; i < sessions.Length; i++)
            {
                sessions[i].Connect(server.BoundPort, names[i]);
                sessions[i].SendLogonRequest(Request(names[i]));
            }
            PumpUntil(server, () => sessions.All(s => s.World.MyShipId is not null), 4000);
            Assert.True(sessions.All(s => s.World.MyShipId is not null), "all three sessions must be assigned a ship");

            // Derive side membership from each session's OWN ClientWorld (its
            // PlayerInfo.ISide) rather than server internals (server.Ships is
            // internal to the read-only Allegiance.Server assembly). A session's
            // own ship gets a PlayerInfo during the join burst.
            static short SideOf(ConnectionSession s) =>
                s.World.Ships[s.World.MyShipId!.Value].Player!.ISide;
            PumpUntil(server, () => sessions.All(s => s.World.Ships[s.World.MyShipId!.Value].Player is not null), 4000);
            Assert.True(sessions.All(s => s.World.Ships[s.World.MyShipId!.Value].Player is not null),
                "each session must have its own PlayerInfo before we can group sides");

            short[] ids = sessions.Select(s => s.World.MyShipId!.Value).ToArray();
            short[] sides = ids.Select((_, i) => SideOf(sessions[i])).ToArray();

            int subject = -1, observer = -1, enemy = -1;
            for (int i = 0; i < sessions.Length && observer < 0; i++)
                for (int j = 0; j < sessions.Length; j++)
                    if (j != i && sides[j] == sides[i]) { subject = i; observer = j; break; }
            Assert.True(subject >= 0, "round-robin must yield a same-side pair");
            for (int k = 0; k < sessions.Length; k++)
                if (k != subject && k != observer) { enemy = k; break; }
            Assert.True(enemy >= 0 && sides[enemy] != sides[subject],
                "3 sessions / 2 teams must leave one cross-side");

            short subjectId = ids[subject];
            short enemyId = ids[enemy];
            var obsWorld = sessions[observer].World;

            // P180/P188 gate: movement isn't integrated and SHIP_UPDATE input is
            // ignored until the mission is Started. 3 peers across 2 sides occupy
            // BOTH sides, so IsAllOccupiedSidesReady needs every peer ready —
            // ready all three, then observe the server-side Mission.Stage
            // (Server.Missions[0]) crossing into Started through the 15s countdown.
            foreach (var s in sessions)
                s.SendPlayerReady(fReady: true, shipId: s.World.MyShipId!.Value);
            int started = PumpUntil(server, () => server.Missions[0].Stage == MissionStage.Started, 2000);
            Assert.True(started < 2000, $"mission never reached Started after all-three ready-up (stage={server.Missions[0].Stage})");

            // Command real, sustained thrust on both the teammate and the enemy,
            // so both are genuinely "dirty" every tick.
            sessions[subject].CommandControls(new ControlData(0f, 0f, 0f, 0.7f), ShipControlState.ForwardButton);
            sessions[enemy].CommandControls(new ControlData(0f, 0f, 0f, 0.7f), ShipControlState.ForwardButton);

            // Sample the observer's entity table every pump iteration, recording
            // each distinct position the teammate's Latest passes through.
            var observedPositions = new List<Vector3>();
            int ticks = PumpUntil(server, () =>
            {
                if (obsWorld.Ships.TryGetValue(subjectId, out var e) && e.Latest is not null)
                {
                    var p = e.Latest.Position;
                    if (!observedPositions.Any(q => (q - p).Length() < 1e-3f))
                        observedPositions.Add(p);
                }
                return observedPositions.Count >= 3;
            }, 4000);

            Assert.True(ticks < 4000, $"teammate never streamed ≥ 3 distinct positions into ClientWorld (saw {observedPositions.Count})");
            Assert.True(observedPositions.Count >= 3, "need an initial spawn + two real movements to prove a live stream");
            Assert.True((observedPositions[^1] - observedPositions[0]).Length() > 0.01f,
                $"teammate's observed position must actually move under thrust (first={observedPositions[0]}, last={observedPositions[^1]})");

            // Same-side scope, asserted from OUR client model: the enemy never
            // enters the observer's entity table (server never even exports it
            // to a cross-side peer, so it can't get a SINGLE_SHIP_UPDATE either).
            Assert.DoesNotContain(enemyId, obsWorld.Ships.Keys);
        }
        finally
        {
            foreach (var s in sessions) s.Dispose();
        }
    }

    /// <summary>A real <see cref="ConnectionSession.SendChat"/> round trip: the
    /// server relays an Everyone-targeted chat to all connected peers, and the
    /// recipient's <see cref="ClientWorld.Events.Chats"/> must gain the message —
    /// plus the sender gets its own echo. Proves chat flows end-to-end into the
    /// event log, not just the sender.</summary>
    [Fact]
    public void ChatMessage_RelayedToPeers_LandsInReceiversEventLog()
    {
        using var server = new Srv();
        server.Boot(Config("Chat Relay", 0));

        using var sender = new ConnectionSession();
        using var receiver = new ConnectionSession();
        sender.Connect(server.BoundPort, "chat_sender");
        receiver.Connect(server.BoundPort, "chat_receiver");
        sender.SendLogonRequest(Request("chat_sender"));
        receiver.SendLogonRequest(Request("chat_receiver"));

        PumpUntil(server, () => sender.World.MyShipId is not null && receiver.World.MyShipId is not null, 4000);
        Assert.NotNull(sender.World.MyShipId);
        Assert.NotNull(receiver.World.MyShipId);

        sender.SendChat("hello from chat_sender");
        int pump = PumpUntil(server,
            () => receiver.World.Events.Chats.Any(c => c.Message == "hello from chat_sender"),
            4000);
        Assert.True(pump < 4000, "chat never reached the receiver's EventLog");

        var got = receiver.World.Events.Chats.Single(c => c.Message == "hello from chat_sender");
        Assert.Equal("hello from chat_sender", got.Message);
        Assert.Equal(sender.World.MyShipId!.Value, got.SidSender);   // relayed with the real sender ship id

        // Everyone-targeted chat is also echoed to the sender itself.
        int echoPump = PumpUntil(server,
            () => sender.World.Events.Chats.Any(c => c.Message == "hello from chat_sender"), 4000);
        Assert.True(echoPump < 4000, "sender's own echo never landed in its EventLog");
        Assert.Contains(sender.World.Events.Chats, c => c.Message == "hello from chat_sender");
    }
}
