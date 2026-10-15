using Allegiance.Core;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class ScoreRosterLiveTests
{
    private static void Pump(Srv server, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < deadline) { server.TickSim(); Thread.Sleep(2); }
        Assert.True(done(), "Timed out waiting for scoreboard/session state");
    }

    private static ConnectionSession Seat(Srv server, string name)
    {
        var session = new ConnectionSession();
        session.Connect(server.BoundPort, name);
        session.SendLogonRequest(new LogonRequest { CharacterName = name, FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1 });
        Pump(server, () => session.Ack is not null);
        session.SendPositionReq(-1);
        Pump(server, () => { lock (session.Gate) return session.World.MyShipId is not null; });
        return session;
    }

    private static ScoreRosterEntry[] Roster(ConnectionSession session)
    { lock (session.Gate) return session.World.ScoreRoster.ToArray(); }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Human_scoreboard_tracks_real_damage_joins_leaves_rejoin_and_periodic_healing_harness_receives_none()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(new ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
            Games = [new GameConfig { Name = "P672", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }],
        });
        server.Missions[0].Stage = MissionStage.Started;
        using var a = Seat(server, "Ace");
        using var b = Seat(server, "Bee");
        using var c = Seat(server, "Cee");
        using var harness = Seat(server, "mcp-p672");
        var fmids = new List<ushort>();
        server._testSendCapture = (_, packet) => fmids.AddRange(FedPacket.Unpack(packet).Select(m => FedMessageHeader.Read(m).Fmid));
        Pump(server, () => Roster(a).Length == 4 && Roster(b).Length == 4);
        Assert.Empty(Roster(harness));
        var attacker = server.Ships.Single(s => s.Ship.Id == a.World.MyShipId).Ship;
        var victim = server.Ships.Single(s => s.Ship.Id == b.World.MyShipId).Ship;
        var victimScore = server.Ships.Single(s => ReferenceEquals(s.Ship, victim)).PlayerScore;
        var attackerScore = server.Ships.Single(s => ReferenceEquals(s.Ship, attacker)).PlayerScore;
        // Same real-damage fixture as PlayerScoreCombatScoringTests: a side
        // without a lifepod makes lethal hull damage a death rather than eject.
        victim.SideId = 774;
        victim.ReceiveDamage(0, 1e9f, server.SimNow, victim.Position, Vector3.Zero, attacker);
        Assert.Equal(1, victimScore.Deaths);
        Assert.Equal(1, attackerScore.Kills);
        Pump(server, () => Roster(a).Any(e => e.CharacterName == "Ace" && e.Kills == 1)
            && Roster(c).Any(e => e.CharacterName == "Bee" && e.Deaths == 1));
        Assert.Contains((ushort)219, fmids);
        var ace = Roster(a).Single(e => e.CharacterName == "Ace");
        Assert.Equal(attackerScore.Score, ace.Score);
        Assert.Equal(attackerScore.CombatRating, ace.CombatRating);
        Assert.Equal(attackerScore.Rank, ace.Rank);
        c.SendLogoff();
        Pump(server, () => Roster(a).Length == 3 && Roster(a).All(e => e.CharacterName != "Cee"));
        using var rejoined = Seat(server, "Cee");
        Pump(server, () => Roster(rejoined).Length == 4 && Roster(rejoined).Any(e => e.CharacterName == "Ace" && e.Kills == 1));
        // Corrupt a same-size row to simulate a missed patch; no score change
        // follows, so ONLY a periodic full snapshot can repair this discrepancy.
        lock (a.Gate) a.World.Apply(new ScoreRosterUpdate { Entries = Roster(a).Select(e => e with { Kills = -99 }).ToArray() });
        Pump(server, () => Roster(a).Any(e => e.CharacterName == "Ace" && e.Kills == 1));
        Assert.Empty(Roster(harness));
        Assert.DoesNotContain(harness.UnknownFmids, f => f is 209 or 219);
        // A stage transition immediately replaces a bad baseline, without
        // waiting another 30 seconds. The scoreboard remains live in the lobby.
        lock (a.Gate) a.World.Apply(new ScoreRosterUpdate { Entries = [] });
        server.Missions[0].Stage = MissionStage.NotStarted;
        Pump(server, () => Roster(a).Length == 4);
        lock (a.Gate) a.World.Apply(new ScoreRosterUpdate { Entries = [] });
        server.Missions[0].Stage = MissionStage.Started;
        Pump(server, () => Roster(a).Length == 4);
        server._testSendCapture = null;
    }
}
