using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Rendering;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P456 loopback proof for the compose-recipient default. A real
/// <see cref="ConnectionSession"/> — the exact client type <c>SendChatLine</c>
/// uses — composes its default chat (now <see cref="ChatTargetLogic.DefaultTarget"/>
/// = Team) and sends it over loopback TCP. The real server routes it through the
/// shared <c>SelectChatTargets</c>: the same-side teammate's
/// <see cref="Allegiance.ClientCore.State.EventLog.Chats"/> gains the line carrying
/// <c>ChatTarget=Team</c> (the FM_CS_CHATMESSAGE(23) that went out), and the
/// cross-side enemy's log stays empty — the whole point of defaulting to Team.
/// </summary>
[Collection("LiveNetwork")]
public class ChatTargetComposeLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config(string name = "P456 Chat Default", int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        Games =
        [
            new Gm { Name = name, Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 446u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 90200000000000000ul, DrmHash = "p456chat",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ A fresh compose defaults to Team, and a Team-targeted chat sent
    /// by the composing client over the loopback reaches its teammate's log with
    /// ChatTarget=Team while the enemy-side peer's log stays clean.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ComposeDefault_Team_SendsToTeammate_NotEnemy()
    {
        Require.Artwork();
        // The client-side decision under test: a fresh compose sends to Team.
        Assert.Equal(ChatTarget.Team, ChatTargetLogic.DefaultTarget);

        using var server = new Srv();
        server.Boot(Config());

        var sessions = new[] { new ConnectionSession(), new ConnectionSession(), new ConnectionSession() };
        string[] names = { "p456_sender", "p456_teammate", "p456_enemy" };
        try
        {
            for (int i = 0; i < sessions.Length; i++)
            {
                sessions[i].Connect(server.BoundPort, names[i]);
                sessions[i].SendLogonRequest(Request(names[i]));
            }
            PumpUntil(server, () => sessions.All(s => s.World.MyShipId is not null), 8000);
            Assert.True(sessions.All(s => s.World.MyShipId is not null), "all three sessions must be assigned a ship");

            // Side membership from each session's own ClientWorld PlayerInfo.
            static short SideOf(ConnectionSession s) =>
                s.World.Ships[s.World.MyShipId!.Value].Player!.ISide;
            PumpUntil(server, () => sessions.All(s => s.World.Ships[s.World.MyShipId!.Value].Player is not null), 8000);
            Assert.True(sessions.All(s => s.World.Ships[s.World.MyShipId!.Value].Player is not null),
                "each session needs its own PlayerInfo before we can group sides");

            short[] sides = sessions.Select(SideOf).ToArray();
            // Round-robin seating (3 peers / 2 teams) always gives the sender a
            // same-side teammate (pigeonhole) and leaves exactly one cross-side.
            var sender = sessions[0];
            short senderSide = sides[0];
            int teammateIdx = -1, enemyIdx = -1;
            for (int i = 1; i < sessions.Length; i++)
            {
                if (sides[i] == senderSide) teammateIdx = i;
                else enemyIdx = i;
            }
            Assert.True(teammateIdx >= 0, "round-robin must give the sender a same-side teammate");
            Assert.True(enemyIdx >= 0 && sides[enemyIdx] != senderSide, "3 peers / 2 teams must leave one cross-side");
            var teammate = sessions[teammateIdx];
            var enemy = sessions[enemyIdx];

            // The compose path now sends its current target (_chatTarget seeded
            // from DefaultTarget = Team). Send exactly that over the loopback.
            sender.SendChat("TEAM FROM CLIENT", ChatTargetLogic.DefaultTarget);

            // The same-side teammate receives it, carrying ChatTarget=Team — the
            // FM_CS_CHATMESSAGE(23) that went out kept the compose default.
            int pump = PumpUntil(server,
                () => teammate.World.Events.Chats.Any(c => c.Message == "TEAM FROM CLIENT"), 8000);
            Assert.True(pump < 8000, "Team chat never reached the teammate's EventLog.Chats");
            var got = teammate.World.Events.Chats.Single(c => c.Message == "TEAM FROM CLIENT");
            Assert.Equal((int)ChatTarget.Team, got.ChatTarget);
            Assert.Equal(sender.World.MyShipId!.Value, got.SidSender);

            // Sender's own echo lands too (SelectChatTargets includes the sender
            // in the same-side set), matching SendChat's relay behaviour.
            PumpUntil(server, () => sender.World.Events.Chats.Any(c => c.Message == "TEAM FROM CLIENT"), 8000);
            Assert.Contains(sender.World.Events.Chats, c => c.Message == "TEAM FROM CLIENT");

            // ⭐ The cross-side enemy never sees it — the whole point of the
            // Team default (previously Everyone leaked the line to the enemy).
            // Delivery to the teammate and the enemy's pass-over happen in the
            // same synchronous relay tick, so this is asserted once the teammate
            // has it.
            Assert.DoesNotContain(enemy.World.Events.Chats, c => c.Message == "TEAM FROM CLIENT");
            Console.WriteLine($"LIVE-P456: sender side {senderSide}, teammate side {sides[teammateIdx]}, " +
                              $"enemy side {sides[enemyIdx]}; teammate got Team chat, enemy did not — RESULT PASS");
        }
        finally
        {
            foreach (var s in sessions) s.Dispose();
        }
    }
}
