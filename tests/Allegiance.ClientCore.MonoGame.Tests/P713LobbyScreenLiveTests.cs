using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("LiveNetwork")]
public sealed class P713LobbyScreenLiveTests
{
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Non_owner_commander_screen_toggles_real_team_ready_and_readies_self()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(new ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc",
            Port = 0, UpdatesPerSecond = 20,
            Games = [new GameConfig { Name = "P713 lobby", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        void Drain(Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 10)
            {
                if (done()) return;
                server.TickSim(); Thread.Sleep(1);
            }
            Assert.Fail("Expected lobby wire transition did not arrive");
        }
        void Seat(ConnectionSession session, string name, short side)
        {
            session.Connect(server.BoundPort, name);
            session.SendLogonRequest(new LogonRequest
            {
                CharacterName = name, FedsrvVer = LogonRequest.MsgVer, ZoneTicket = [1, 2, 3, 4],
                CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", Time = 1, DwCookie = uint.MaxValue,
                CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = 71301,
            });
            session.SendPositionReq(side);
            Drain(() => { lock (session.Gate) return session.World.MyShipId is not null; });
        }
        using var owner = new ConnectionSession();
        using var commander = new ConnectionSession();
        using var teammate = new ConnectionSession();
        Seat(owner, "P713 owner", 0);
        Seat(commander, "P713 commander", 1);
        Seat(teammate, "P713 teammate", 1);
        Drain(() => { lock (commander.Gate) return commander.World.IsTeamLeader && commander.World.MissionOwnerBroadcast is not null; });
        Assert.False(commander.World.AmMissionOwner);
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Game1).GetField("_session", hidden)!.SetValue(game, commander);
        void Click() => typeof(Game1).GetMethod("ToggleTeamReady", hidden)!.Invoke(game, null);
        bool Forced(bool value)
        {
            lock (commander.Gate)
            lock (teammate.Gate)
                return commander.World.ForceReadyBySideId.TryGetValue(1, out var f) && f == value &&
                    teammate.World.ForceReadyBySideId.TryGetValue(1, out var t) && t == value &&
                    commander.World.TeamReadyBySideId.TryGetValue(1, out var r) && r == value;
        }
        Click();
        Drain(() => Forced(true));
        short id = commander.World.MyShipId!.Value;
        Drain(() => { lock (teammate.Gate) return teammate.World.Roster.ReadyByShipId.TryGetValue(id, out var r) && r; });
        Assert.True((bool)typeof(Game1).GetField("_ownReady", hidden)!.GetValue(game)!);
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage); // other team is unready
        var gathered = ReadyCheckRoster.Gather(commander.World, commander.Gate, true);
        var rows = ReadyCheckLayout.Build(gathered.SideName, true, gathered.Players, gathered.AggregateReady,
            true, LobbyScreenState.ToggleTeamReady(commander.World, true) is not null, launchCountdown: 7);
        Assert.Contains(rows, r => r.Text == "O TOGGLE TEAM READY");
        Assert.Contains(rows, r => r.Text.Contains("(FORCED)"));
        Assert.Contains(rows, r => r.Text == "LAUNCHING IN 7");
        using var late = new ConnectionSession();
        Seat(late, "P713 late", 1);
        Drain(() =>
        {
            lock (late.Gate)
                return late.World.TeamLeaderShipIdFor(1) == id &&
                    late.World.ForceReadyBySideId.TryGetValue(1, out var forced) && forced &&
                    late.World.TeamReadyBySideId.TryGetValue(1, out var ready) && ready &&
                    late.World.Roster.ReadyByShipId.TryGetValue(id, out var selfReady) && selfReady;
        });
        Click();
        Drain(() => Forced(false)); // teammate remains unready, so aggregate clears too
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);
        short teammateId = teammate.World.MyShipId!.Value;
        // The following successful self echo fences the refused same-side
        // retag on the same ordered TCP stream, without an arbitrary sleep.
        teammate.SendWingId(id, 6);
        teammate.SendWingId(teammateId, 2);
        Drain(() => { lock (teammate.Gate) return teammate.World.Ships[teammateId].WingId == 2; });
        lock (teammate.Gate) Assert.Equal(0, teammate.World.Ships[id].WingId);
        commander.SendWingId(teammateId, 3);
        Drain(() =>
        {
            lock (teammate.Gate)
                return teammate.World.Ships[teammateId].WingId == 3 &&
                    teammate.World.FeedbackLines.Any(l => l.Text == "You have been assigned to wing escort" && l.IsCritical);
        });
        using var wingJoiner = new ConnectionSession();
        Seat(wingJoiner, "P713 wings", 1);
        Drain(() => { lock (wingJoiner.Gate) return wingJoiner.World.Ships.TryGetValue(teammateId, out var member) && member.WingId == 3; });
        Assert.DoesNotContain(wingJoiner.World.FeedbackLines, l => l.Text.StartsWith("You have been assigned to wing"));
        // The same screen handler is inert for a regular pilot.
        typeof(Game1).GetField("_session", hidden)!.SetValue(game, teammate);
        typeof(Game1).GetField("_ownReady", hidden)!.SetValue(game, false);
        Click();
        Assert.False((bool)typeof(Game1).GetField("_ownReady", hidden)!.GetValue(game)!);
        Assert.DoesNotContain(ForceTeamReady.Fmid, commander.UnknownFmids);
        Assert.DoesNotContain(SetTeamLeader.Fmid, commander.UnknownFmids);
        Assert.DoesNotContain(SetWingId.Fmid, teammate.UnknownFmids);
    }
}
