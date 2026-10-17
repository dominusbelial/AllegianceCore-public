using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P457 — the quick-chat order-issuing wire proof: a real command chat (a legal
/// <c>CommandID != c_cidNone</c> carrying its target) relayed to Team queues a
/// real queued order on each same-side real-player recipient (excluding the
/// sender), via the P424 path. Three bots seat round-robin onto two sides
/// (Server.cs drain order, which races — so never assume WHO lands where), so we
/// group by side: the two same-side peers are the sender + teammate, the solo
/// peer is the enemy-side target of "attack my target".
/// </summary>
[Collection("LiveNetwork")]
public sealed class LiveQuickChatOrderWireTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P457 QuickChat Order", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 957u, DwCookie = 0x957u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 75600000000000000ul, DrmHash = "p457quickchat",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short? SideOf(Srv server, FltBot bot) =>
        server.Ships.First(l => l.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship.SideId;

    private static short ShipId(FltBot bot) => bot.ShipAssignment!.Ship.ShipId;

    /// <summary>⭐ Primary proof: A issues "attack my target" (cidAttack) with C as
    /// the target, to Team. B (same-side real player, not the sender) receives the
    /// queued order naming C; the sender A and the enemy-side C receive nothing.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void AttackMyTarget_Team_QueuesOnTeammate_NotSenderOrEnemy()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var mm in server.Missions) mm.Stage = MissionStage.Started;

        var a = new FltBot(); var b = new FltBot(); var c = new FltBot();
        a.Connect(server.BoundPort, "p457_a");
        b.Connect(server.BoundPort, "p457_b");
        c.Connect(server.BoundPort, "p457_c");
        a.SendLogonRequest(Request("PLAYERA"));
        b.SendLogonRequest(Request("PLAYERB"));
        c.SendLogonRequest(Request("PLAYERC"));

        int ticks = PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null && c.ShipAssignment is not null, 4000);
        Assert.True(ticks < 4000, "not all three bots were assigned a ship");

        // Group the three peers by side: the two same-side peers are the sender +
        // teammate; the solo peer is the enemy-side target (round-robin guarantees
        // one side has exactly two members and the other one — LiveChatWireTests
        // establishes the same shape, Server.cs:523-529).
        var groups = new[] { a, b, c }.GroupBy(x => SideOf(server, x)).ToList();
        Assert.Equal(2, groups.Count);
        var majority = groups.First(g => g.Count() == 2).ToArray();
        var minority = groups.First(g => g.Count() == 1).ToArray();

        var sender = majority[0];                       // A
        var teammate = majority[1];                     // B (same side)
        var target = minority[0];                       // C (enemy side)
        short targetId = ShipId(target);

        // ── A issues "attack my target" (cidAttack) with C as target, to Team ──
        sender.ResetLeadershipSignals(); teammate.ResetLeadershipSignals(); target.ResetLeadershipSignals();
        sender.SendChat("", ChatTarget.Team, voiceOver: 10, absentMessage: true,
            commandId: OrderChange.CidAttack, oidTarget: targetId, otTarget: (short)IgcObjectType.Ship);

        PumpUntil(server, () => teammate.QueuedCommandUpdates.Any(u => u.HasPending && u.CommandId == OrderChange.CidAttack && u.TargetId == targetId), 2000);
        Assert.Contains(teammate.QueuedCommandUpdates,
            u => u.HasPending && u.CommandId == OrderChange.CidAttack
              && u.TargetType == IgcObjectType.Ship && u.TargetId == targetId);
        PumpUntil(server, () => teammate.Chats.Any(c => c.VoiceOver == 10), 2000);
        var sonic = Assert.Single(teammate.Chats, c => c.VoiceOver == 10);
        Assert.True(sonic.MessageIsAbsent);
        // The sender and the enemy-side player receive no queued order.
        Assert.DoesNotContain(sender.QueuedCommandUpdates, u => u.HasPending);
        Assert.DoesNotContain(target.QueuedCommandUpdates, u => u.HasPending);
        Console.WriteLine($"P457-WIRE: A({ShipId(sender)}, side {SideOf(server, sender)}) \"attack my target\" (target C#{targetId}) to Team -> B({ShipId(teammate)}, side {SideOf(server, teammate)}) queued cidAttack target C#{targetId}; sender A and enemy C got none");
    }

    /// <summary>A command chat on <see cref="ChatTarget.Everyone"/> is never
    /// queued anywhere (native FedSrv.CPP:3385-3391) — the voice relays, but no
    /// player gets a queued order.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Everyone_CommandChat_QueuesNobody()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var mm in server.Missions) mm.Stage = MissionStage.Started;

        var a = new FltBot(); var b = new FltBot(); var c = new FltBot();
        a.Connect(server.BoundPort, "p457_ea"); b.Connect(server.BoundPort, "p457_eb"); c.Connect(server.BoundPort, "p457_ec");
        a.SendLogonRequest(Request("EA")); b.SendLogonRequest(Request("EB")); c.SendLogonRequest(Request("EC"));
        int ticks = PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null && c.ShipAssignment is not null, 4000);
        Assert.True(ticks < 4000, "not all three bots were assigned a ship");

        short targetId = ShipId(c);
        a.ResetLeadershipSignals(); b.ResetLeadershipSignals(); c.ResetLeadershipSignals();
        a.SendChat("everyone attack", ChatTarget.Everyone,
            commandId: OrderChange.CidAttack, oidTarget: targetId, otTarget: (short)IgcObjectType.Ship);

        // A short flush lets any (wrong) queued delivery land before we assert absence.
        PumpUntil(server, () => false, 80);

        Assert.DoesNotContain(a.QueuedCommandUpdates, u => u.HasPending);
        Assert.DoesNotContain(b.QueuedCommandUpdates, u => u.HasPending);
        Assert.DoesNotContain(c.QueuedCommandUpdates, u => u.HasPending);
        Console.WriteLine("P457-WIRE: Everyone command chat queued nothing on A/B/C");
    }
}
