using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P883MissionCookieTests
{
    private static ServerConfig Config() => new()
    {
        ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        Reconnect = new ReconnectConfig { Enabled = true, GraceSeconds = 30 },
        Games = [Game("First", 2), Game("Second", 3)],
    };
    private static GameConfig Game(string name, int teams) => new()
    {
        Name = name, Map = teams == 3 ? "XXV_simple.igc" : "m2xspiderw.igc", Teams = teams,
        CivIds = Enumerable.Repeat(-1, teams).ToArray(),
        SideNames = Enumerable.Range(0, teams).Select(i => name + i).ToArray(),
    };
    private static void Pump(Srv server, Func<bool> condition)
    {
        for (int i = 0; i < 3000; i++)
        {
            server.TickSim();
            if (condition()) return;
            Thread.Sleep(1);
        }
        Assert.True(condition(), "timed out waiting for wire state");
    }
    private static ConnectionSession Logon(Srv server, string name, uint cookie, uint token = 0)
    {
        var client = new ConnectionSession();
        client.Connect(server.BoundPort, name);
        client.SendLogonRequest(new LogonRequest { CharacterName = name, FedsrvVer = LogonRequest.MsgVer,
            DwCookie = cookie, ResumeToken = token, CrcFileList = -1 });
        Pump(server, () => client.Ack is not null);
        Assert.True(client.Ack!.FValidated);
        return client;
    }
    private static ConnectionSession Seat(Srv server, string name, uint cookie, uint token = 0, short side = 0)
    {
        var client = Logon(server, name, cookie, token);
        Pump(server, () => client.World.SideRoster is not null);
        client.SendPositionReq(side); // native side choice is POSITIONREQ.iSide, Messages.h:320-323
        Pump(server, () => client.World.MyShipId is not null);
        return client;
    }
    private static Mission MissionOf(Srv server, ConnectionSession client) =>
        server.Ships.Single(s => s.Ship.Id == client.Ack!.ShipID).Ship.Mission!;

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Two_games_select_by_cookie_and_NA_selects_first_with_isolated_worlds()
    {
        Require.Artwork();
        using var server = new Srv(); server.Boot(Config());
        var first = server.Missions[0]; var second = server.Missions[1];
        Assert.NotEqual(first.Cookie, second.Cookie);
        using var a = Seat(server, "CookiePilot", first.Cookie);
        using var b = Seat(server, "CookiePilot", second.Cookie); // name uniqueness is per mission
        using var legacy = Seat(server, "Legacy", uint.MaxValue);
        Assert.Same(first, MissionOf(server, a)); Assert.Same(second, MissionOf(server, b));
        Assert.Same(first, MissionOf(server, legacy));
        Assert.Equal(first.Cookie, a.World.Mission!.Cookie);
        Assert.Equal(second.Cookie, b.World.JoinedMissionCookie);
        Assert.Equal(2, a.World.SideRoster!.Sides.Length); Assert.Equal(3, b.World.SideRoster!.Sides.Length);
        Assert.DoesNotContain(b.Ack!.ShipID, a.World.Ships.Keys);
        Assert.DoesNotContain(a.Ack!.ShipID, b.World.Ships.Keys);
        // The actual cluster exports, not just the join metadata, come from the chosen map.
        Assert.Equal(second.Sectors.Select(s => s.Id).Order(), b.World.Clusters.Keys.Order());
        first.Stage = second.Stage = MissionStage.Started;
        for (int i = 0; i < 50; i++) { server.TickSim(); Thread.Sleep(1); }
        Assert.DoesNotContain(b.Ack!.ShipID, a.World.Ships.Keys);
        Assert.DoesNotContain(a.Ack!.ShipID, b.World.Ships.Keys);
        Assert.Equal(first.Sectors.Select(s => s.Id).Order(), a.World.Clusters.Keys.Order());
        Assert.Equal(second.Sectors.Select(s => s.Id).Order(), b.World.Clusters.Keys.Order());
        a.SendChat("P883-first-game-only");
        Pump(server, () => legacy.World.Events.Chats.Any(c => c.Message == "P883-first-game-only"));
        for (int i = 0; i < 20; i++) { server.TickSim(); Thread.Sleep(1); }
        Assert.DoesNotContain(b.World.Events.Chats, c => c.Message == "P883-first-game-only");
        Assert.Equal(new[] { first.Cookie, second.Cookie }, server.LiveMissions.Select(m => m.Cookie));
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(false)] [InlineData(true)]
    public void Missing_and_finished_missions_refuse_on_native_DELPOSITIONREQ(bool finished)
    {
        Require.Artwork();
        using var server = new Srv(); server.Boot(Config());
        var mission = server.Missions[1];
        if (finished) mission.Stage = MissionStage.Over;
        using var client = Logon(server, "Refused", finished ? mission.Cookie : 0xFF883u);
        Pump(server, () => client.World.MyPositionRequestResult is not null);
        Assert.Equal(finished ? DelPositionReqReason.ServerPaused : DelPositionReqReason.NoMission,
            client.World.MyPositionRequestResult!.Value.Reason);
        Assert.Equal(-1, client.World.MyPositionRequestResult.Value.SideId);
        Assert.Empty(server.Ships); Assert.Null(client.World.SideRoster); Assert.Null(client.World.MyShipId);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Provisioning_appends_while_configured_bot_keeps_its_world_and_creator_joins_cookie()
    {
        Require.Artwork();
        using var server = new Srv(); server.Boot(Config());
        var configured = server.Missions[0];
        using var bot = Seat(server, "mcp-P883", uint.MaxValue);
        configured.Stage = MissionStage.Started;
        Assert.True(server.CanHostPlayerGame());
        var parameters = new MissionParamsMsg { SzIgcStaticFile = "PCore001.igc", NTems = 2, IGoalConquestPercentage = 100,
            IGoalTerritoryPercentage = 100, NMaxPlayersPerTeam = 10, NTotalMaxPlayersPerGame = 20 };
        var created = server.ProvisionPlayerGame(new ClaimedGame("p883", "host", "PCore001.igc",
            "m2xspiderw.igc", "Wizard", Convert.ToBase64String(parameters.Encode()), "Claimed"));
        Assert.NotNull(created); Assert.Same(configured, server.Missions[0]);
        Assert.Same(created, server.Missions[2]); Assert.NotEqual(configured.Cookie, created.Cookie);
        using var creator = Logon(server, "Creator", created.Cookie);
        Pump(server, () => creator.World.SideRoster is not null);
        for (int i = 0; i < 250; i++) server.TickSim(); // a human may spend >10s choosing a side
        Assert.Contains(created, server.Missions);
        creator.SendPositionReq(0); Pump(server, () => creator.World.MyShipId is not null);
        Assert.Same(created, MissionOf(server, creator)); Assert.Same(configured, MissionOf(server, bot));
        Assert.DoesNotContain(creator.Ack!.ShipID, bot.World.Ships.Keys);
        using var legacy = Seat(server, "NextBot", uint.MaxValue, side: 1);
        Assert.Same(configured, MissionOf(server, legacy));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Reset_retains_cookie_and_pending_peers_follow_the_rebuilt_world()
    {
        Require.Artwork();
        using var server = new Srv(); server.Boot(Config());
        var old = server.Missions[1]; var cookie = old.Cookie;
        using var pending = Logon(server, "Pending", cookie);
        typeof(Srv).GetMethod("RotateMissionAfterGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [old]);
        var fresh = server.Missions[1]; Assert.NotSame(old, fresh); Assert.Equal(cookie, fresh.Cookie);
        pending.SendPositionReq(0); Pump(server, () => pending.World.MyShipId is not null);
        Assert.Same(fresh, MissionOf(server, pending)); Assert.Equal(cookie, pending.World.JoinedMissionCookie);
        using var late = Seat(server, "AfterReset", cookie); Assert.Same(fresh, MissionOf(server, late));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Resume_token_is_independent_of_mission_cookie_and_same_names_resume_per_game()
    {
        Require.Artwork();
        using var server = new Srv(); server.Boot(Config());
        uint first = server.Missions[0].Cookie, second = server.Missions[1].Cookie;
        var a = Seat(server, "Same", first, 0x883A); var aid = a.Ack!.ShipID;
        var b = Seat(server, "Same", second, 0x883B); var bid = b.Ack!.ShipID;
        a.Dispose(); b.Dispose(); Pump(server, () => server.ReconnectParkedShipCount == 2);
        using var resumedB = Logon(server, "Same", second, 0x883B);
        using var resumedA = Logon(server, "Same", first, 0x883A);
        Pump(server, () => resumedA.World.JoinedMissionCookie is not null && resumedB.World.JoinedMissionCookie is not null);
        Assert.Equal(aid, resumedA.Ack!.ShipID); Assert.Equal(bid, resumedB.Ack!.ShipID);
        Assert.Equal(first, resumedA.World.JoinedMissionCookie); Assert.Equal(second, resumedB.World.JoinedMissionCookie);
        Assert.Equal(2, server.ReconnectResumedTotal);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mission_password_gate_runs_after_cookie_lookup()
    {
        Require.Artwork();
        using var server = new Srv(); server.Boot(Config());
        var mission = server.Missions[1];
        var parameters = (MissionParamsMsg)typeof(Srv).GetMethod("ParamsOf", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(server, [mission])!;
        parameters.StrGamePassword = "P883-test";
        using var denied = Logon(server, "WrongPassword", mission.Cookie);
        Pump(server, () => denied.World.MyPositionRequestResult is not null);
        Assert.Equal(DelPositionReqReason.BadPassword, denied.World.MyPositionRequestResult!.Value.Reason);
        denied.SendPositionReq(0); // refusing at logon cannot be bypassed at side selection
        for (int i = 0; i < 20; i++) { server.TickSim(); Thread.Sleep(1); }
        Assert.Empty(server.Ships);
        using var allowed = new ConnectionSession(); allowed.Connect(server.BoundPort, "Allowed");
        allowed.SendLogonRequest(new LogonRequest { CharacterName = "Allowed", FedsrvVer = LogonRequest.MsgVer,
            DwCookie = mission.Cookie, MissionPassword = "P883-test" });
        Pump(server, () => allowed.World.SideRoster is not null);
        allowed.SendPositionReq(0); Pump(server, () => allowed.World.MyShipId is not null);
        Assert.Same(mission, MissionOf(server, allowed));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Research_showcase_refuses_provisioning_even_with_spare_capacity()
    {
        Require.Artwork();
        var config = Config();
        config.MaxGames = 3;
        using var server = new Srv(); server.Boot(config);
        config.Showcase.TechMode = "research";
        Assert.False(server.CanHostPlayerGame());
        Assert.Null(server.ProvisionPlayerGame(new ClaimedGame("research", "s", "PCore001.igc",
            "m2xspiderw.igc", "Research refusal", "", "Claimed")));
        Assert.Equal(2, server.Missions.Count);
    }

    [Fact]
    public void Resume_extension_preserves_native_LOGONREQ_offsets_and_is_optional()
    {
        var req = new LogonRequest { CharacterName = "Codec", DwCookie = 0x883, FedsrvVer = LogonRequest.MsgVer };
        var native = req.Encode(); req.ResumeToken = 0x12345678;
        var extended = req.Encode();
        Assert.Equal(native.Length + 8, extended.Length);
        Assert.Equal(native.AsSpan(2).ToArray(), extended.AsSpan(2, native.Length - 2).ToArray());
        Assert.Equal(0x883u, BitConverter.ToUInt32(extended, 28));
        Assert.Equal(req.ResumeToken, LogonRequest.Decode(extended).ResumeToken);
        Assert.Equal(0u, LogonRequest.Decode(native).ResumeToken);
        Assert.Equal(uint.MaxValue, new LogonRequest().DwCookie);
    }
}
