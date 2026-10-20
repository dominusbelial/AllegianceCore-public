using System.Reflection;
using System.Runtime.ExceptionServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using FltBot = global::Allegiance.Bot.Bot;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P762 — SIM-0142 FAllReady/bAutoStart and the native moderator/boot
/// moderation row (FedSrv/fsmission.cpp:4375-4445; FedSrv/FedSrv.CPP:6749-6818,
/// 8223-8226).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P762LobbyModerationTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private static StaticCore Core() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
    private static MissionMap Map() => MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));

    private sealed class World : IDisposable
    {
        public readonly Srv Server = new();
        public readonly ServerHost Host = new();
        public readonly Mission Mission;
        public readonly Sector Sector;
        public readonly List<(ConnectionId Conn, byte[] Message)> Output = [];
        public readonly Ship A, B, C;
        public readonly ConnectionId ConnA = new(1), ConnB = new(2), ConnC = new(3);

        public World()
        {
            Require.Artwork();
            var core = Core();
            Mission = Mission.Assemble(core, Map(),
                [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], Host);
            Mission.Stage = MissionStage.NotStarted;
            Host.Transport = new TcpTransport(Host);
            typeof(TcpTransport).GetField("_isHost", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Host.Transport, true);
            Set("_host", Host);
            Set("_config", new Cfg
            {
                LiveSettings = new LiveSettingsConfig(),
                Games = [new Gm { Name = "P762", Teams = 2, AllowEmptyTeams = false }],
            });
            Set("_missions", new List<Mission> { Mission });
            Sector = Mission.Sectors[0];
            var hull = core.HullTypes.Values.First(h => h.GroupId >= 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod));
            A = new Ship(101, hull, new Vector3(1000, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            B = new Ship(102, hull, new Vector3(1200, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            C = new Ship(103, hull, new Vector3(1400, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 1 };
            foreach (var s in new[] { A, B, C }) Mission.PlaceShip(s, Sector);
            Mission.Sides[0].LeaderShipId = A.Id;
            Server.TestSeedPeers([(ConnA, A, Sector), (ConnB, B, Sector), (ConnC, C, Sector)]);
            Server._testSendCapture = (c, p) => Output.AddRange(FedPacket.Unpack(p).Select(m => (c, m.ToArray())));
            Set("_simNow", 10d);
        }

        public Cfg Config => (Cfg)typeof(Srv).GetField("_config",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Server)!;
        public Gm Game => Config.Games[0];
        public void Set(string name, object value) =>
            typeof(Srv).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Server, value);
        public object? Call(string name, params object?[] args)
        {
            try { return typeof(Srv).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Server, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }
        public bool Ready() => (bool)Call("FAllReady", Mission)!;
        public void SetRequest(ConnectionId conn, string name) =>
            SetRequest(conn, name, "MOD-" + name);
        public void SetRequest(ConnectionId conn, string name, string cdKey, ulong steamId = 424242) =>
            ((Dictionary<ConnectionId, LogonRequest>)typeof(Srv).GetField("_peerRequests",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Server)!)[conn] = new LogonRequest
            { CharacterName = name, CDKey = cdKey, SteamID = steamId };
        public int RankThreshold() => (int)Call("GetRankThreshold", Mission)!;
        public bool HasPeer(Ship ship) => Server.Ships.Any(l => ReferenceEquals(l.Ship, ship));
        public void Dispose() { Host.Transport!.Shutdown(); Server.Dispose(); }
    }

    // ── FAllReady policy (fsmission.cpp:4375-4445) ─────────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void FAllReady_min_and_max_player_counts()
    {
        using var w = new World();
        w.Mission.Sides[0].Ready = true; w.Mission.Sides[1].Ready = true;
        w.Game.MinimumPlayersPerTeam = 1; w.Game.MaxPlayersPerTeam = 100; w.Game.MaxTeamImbalance = 0x7fff;
        Assert.True(w.Ready(), "1v1 within min/max must be ready");

        w.Game.MinimumPlayersPerTeam = 2;
        Assert.False(w.Ready(), "a side below nMinPlayersPerTeam must block");

        w.Game.MinimumPlayersPerTeam = 1; w.Game.MaxPlayersPerTeam = 1;
        Assert.False(w.Ready(), "a side above nMaxPlayersPerTeam must block");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void FAllReady_player_count_imbalance()
    {
        using var w = new World();
        // Move B to side 0 so the sides are 2 v 1.
        w.B.SideId = 0;
        w.Mission.Sides[0].Ready = true; w.Mission.Sides[1].Ready = true;
        w.Game.MinimumPlayersPerTeam = 1; w.Game.MaxPlayersPerTeam = 100; w.Game.MaxTeamImbalance = 0;
        Assert.False(w.Ready(), "min + iMaxImbalance < max must block");

        w.Game.MaxTeamImbalance = 1;
        Assert.True(w.Ready(), "a one-player difference is allowed at iMaxImbalance=1");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void FAllReady_rank_spread_only_when_rank_balancing_is_enabled()
    {
        using var w = new World();
        w.B.SideId = 0;
        w.Mission.Sides[0].Ready = true; w.Mission.Sides[1].Ready = true;
        w.Game.MinimumPlayersPerTeam = 1; w.Game.MaxPlayersPerTeam = 100; w.Game.MaxTeamImbalance = 0x7ffe;
        w.Server.Ships.Single(l => ReferenceEquals(l.Ship, w.A)).PlayerScore.Rank = 100;
        w.Server.Ships.Single(l => ReferenceEquals(l.Ship, w.B)).PlayerScore.Rank = 100;
        w.Server.Ships.Single(l => ReferenceEquals(l.Ship, w.C)).PlayerScore.Rank = 1;
        Assert.False(w.Ready(), "rank spread above GetRankThreshold must block in 0x7ffe mode");

        w.Server.Ships.Single(l => ReferenceEquals(l.Ship, w.C)).PlayerScore.Rank = 100;
        Assert.True(w.Ready(), "equal rank sums pass the rank-balance clause");

        // The rank clause is disabled outside 0x7ffe.
        w.Server.Ships.Single(l => ReferenceEquals(l.Ship, w.C)).PlayerScore.Rank = 1;
        w.Game.MaxTeamImbalance = 0x7fff;
        Assert.True(w.Ready(), "rank spread is ignored when rank balancing is off");
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void FAllReady_allow_empty_teams_skips_inactive_empty_teams()
    {
        using var w = new World();
        w.Mission.Sides[0].Ready = true;
        w.Game.AllowEmptyTeams = true; w.Game.MinimumPlayersPerTeam = 1;
        w.Game.MaxPlayersPerTeam = 100; w.Game.MaxTeamImbalance = 0x7fff;
        // C is on side 1; remove it from the count to make side 1 empty.
        w.C.SideId = 0;
        w.Server.TestSeedPeers([(w.ConnA, w.A, w.Sector), (w.ConnB, w.B, w.Sector), (w.ConnC, w.C, w.Sector)]);
        w.Mission.Sides[0].Ready = true; w.Mission.Sides[1].Ready = false;

        w.Mission.Sides[1].Active = true;
        Assert.False(w.Ready(), "an active empty team counts as below the minimum");
        w.Mission.Sides[1].Active = false;
        Assert.True(w.Ready(), "bAllowEmptyTeams skips an inactive empty team");
    }

    /// <summary>
    /// P762b — native FAllReady returns true after the balance check
    /// (fsmission.cpp:4432), so the "every side ready" loop at :4435-4444 is
    /// unreachable dead code. FAllReady must therefore ignore side.Ready
    /// entirely; the port's ready-up gate is the separate P180 policy in
    /// TryAutoStart. A balanced mission with every side UNREADY is ready by
    /// this native predicate.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void FAllReady_is_the_native_balance_check_only_and_ignores_side_ready_flags()
    {
        using var w = new World();
        w.Game.AllowEmptyTeams = false; w.Game.MinimumPlayersPerTeam = 1;
        w.Game.MaxPlayersPerTeam = 100; w.Game.MaxTeamImbalance = 0x7fff;
        w.Mission.Sides[0].Ready = false; w.Mission.Sides[1].Ready = false;
        Assert.True(w.Ready(), "native FAllReady is balance-only: unready sides do not veto it");

        // Still a real balance gate.
        w.B.SideId = 0;   // 2 v 1
        w.Game.MaxTeamImbalance = 0;
        Assert.False(w.Ready(), "the native balance clauses still reject an imbalanced mission");
    }

    /// <summary>
    /// P762b — native GetRankThreshold divides the rank sum by the mission's
    /// WHOLE ship list (fsmission.cpp:1405), which includes docked ships (they
    /// leave their cluster list on dock, not the mission list). Docking two of
    /// the three ships must not change the divisor.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GetRankThreshold_divides_by_the_whole_mission_ship_list_including_docked_ships()
    {
        using var w = new World();
        w.Server.Ships.Single(l => ReferenceEquals(l.Ship, w.A)).PlayerScore.Rank = 100;
        w.Server.Ships.Single(l => ReferenceEquals(l.Ship, w.B)).PlayerScore.Rank = 0;
        w.Server.Ships.Single(l => ReferenceEquals(l.Ship, w.C)).PlayerScore.Rank = 0;
        // 100 + (100/3)^2 / (100 + 100/3) = 100 + 33^2/133 = 108.
        Assert.Equal(108, w.RankThreshold());

        // Dock B and C: they leave the flying-ship accessor but stay in the
        // mission. The native divisor is unchanged, so the threshold stays 108.
        w.B.StationId = 1;
        w.C.StationId = 1;
        Assert.DoesNotContain(w.B, w.Sector.GetShipUpdateCandidates());
        Assert.DoesNotContain(w.C, w.Sector.GetShipUpdateCandidates());
        Assert.Equal(108, w.RankThreshold());
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void AutoStart_gate_starts_countdown_only_when_bAutoStart_is_set()
    {
        using var w = new World();
        w.Game.AllowEmptyTeams = false; w.Game.MinimumPlayersPerTeam = 1;
        w.Game.MaxPlayersPerTeam = 100; w.Game.MaxTeamImbalance = 0x7fff;
        w.Game.StartCountdownSeconds = 7;
        w.Mission.Sides[0].Ready = true; w.Mission.Sides[1].Ready = true;

        w.Game.AutoStart = false;
        w.Call("TryAutoStart", w.Mission);
        Assert.Equal(MissionStage.NotStarted, w.Mission.Stage);

        w.Game.AutoStart = true;
        w.Call("TryAutoStart", w.Mission);
        Assert.Equal(MissionStage.Starting, w.Mission.Stage);
        Assert.Equal(17d, ((Dictionary<Mission, double>)typeof(Srv).GetField("_startCountdownDeadline",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(w.Server)!)[w.Mission]);
    }

    // ── moderation (FedSrv.CPP:6749-6818) ──────────────────────────────────

    private static QuitMission Quit(short shipId, QuitSideReason reason, string message = "") =>
        new() { ShipId = shipId, Reason = reason, Message = message };

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Ordinary_self_quit_is_accepted()
    {
        using var w = new World();
        w.Call("HandleQuitMission", w.ConnA, Quit(w.A.Id, QuitSideReason.Quit));
        Assert.False(w.HasPeer(w.A));
        Assert.True(w.HasPeer(w.B));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Only_a_side_leader_can_boot_a_teammate_and_only_the_leader_boot_reason()
    {
        using var w = new World();
        // B is not the leader (A is): rejected.
        w.Call("HandleQuitMission", w.ConnB, Quit(w.A.Id, QuitSideReason.LeaderBooted));
        Assert.True(w.HasPeer(w.A));

        // A (leader) booting C on the other side: rejected (not a teammate).
        w.Call("HandleQuitMission", w.ConnA, Quit(w.C.Id, QuitSideReason.LeaderBooted));
        Assert.True(w.HasPeer(w.C));

        // Wrong reason for a boot: rejected by the TrapHackBoot check.
        w.Call("HandleQuitMission", w.ConnA, Quit(w.B.Id, QuitSideReason.Quit));
        Assert.True(w.HasPeer(w.B));

        // Leader boot of a teammate: accepted, with QSR_LeaderBooted delivered.
        w.Call("HandleQuitMission", w.ConnA, Quit(w.B.Id, QuitSideReason.LeaderBooted));
        Assert.False(w.HasPeer(w.B));
        Assert.Contains(w.Output, o => FedMessageHeader.Read(o.Message).Fmid == QuitMission.Fmid
            && QuitMission.Decode(o.Message).ShipId == w.B.Id
            && QuitMission.Decode(o.Message).Reason == QuitSideReason.LeaderBooted);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Leader_boot_is_refused_when_team_settings_are_locked()
    {
        using var w = new World();
        w.Game.LockTeamSettings = true;
        w.Call("HandleQuitMission", w.ConnA, Quit(w.B.Id, QuitSideReason.LeaderBooted));
        Assert.True(w.HasPeer(w.B));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Configured_moderator_may_remove_any_player_with_reason_and_message()
    {
        using var w = new World();
        w.SetRequest(w.ConnA, "admin");
        w.Config.ModeratorAccounts = ["MOD-admin"];
        w.Call("HandleQuitMission", w.ConnA,
            Quit(w.C.Id, QuitSideReason.AdminBooted, "You have been banned from this game by an administrator."));
        Assert.False(w.HasPeer(w.C));
        var quit = Assert.Single(w.Output
            .Where(o => o.Conn == w.ConnC && FedMessageHeader.Read(o.Message).Fmid == QuitMission.Fmid)
            .Select(o => QuitMission.Decode(o.Message)));
        Assert.Equal(QuitSideReason.AdminBooted, quit.Reason);
        Assert.Equal("You have been banned from this game by an administrator.", quit.Message);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Loopback_moderator_ban_removes_the_target_and_delivers_the_admin_notice()
    {
        Require.Artwork();
        var config = new Cfg
        {
            ArtPath = ArtRoot, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
            ModeratorAccounts = ["MOD-KEY"],
            Games = [new Gm { Name = "P762 mod", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }],
        };
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(config);
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        using var mod = new FltBot();
        using var target = new FltBot();
        mod.Connect(server.BoundPort, "moderator");
        mod.SendLogonRequest(Logon("moderator", "MOD-KEY", 9001));
        target.Connect(server.BoundPort, "target");
        target.SendLogonRequest(Logon("target", "PLAIN-KEY", 9002));
        int Pump(Func<bool> done) { int n = 0; while (!done() && n < 4000) { server.TickSim(); n++; Thread.Sleep(1); } return n; }
        Assert.True(Pump(() => mod.ShipAssignment is not null && target.ShipAssignment is not null) < 4000);
        short targetId = target.ShipAssignment!.Ship.ShipId;

        mod.SendQuitMission(targetId, QuitSideReason.AdminBooted,
            "You have been banned from this game by an administrator.");
        Assert.True(Pump(() => target.QuitMissions.Count > 0) < 4000, "target must receive its QUIT_MISSION(124)");
        var quit = target.QuitMissions.Last();
        Assert.Equal(QuitSideReason.AdminBooted, quit.Reason);
        Assert.Equal("You have been banned from this game by an administrator.", quit.Message);
        Assert.True(Pump(() => server.Ships.All(l => l.Ship.Id != targetId)) < 4000,
            "the moderator ban must remove the target from the mission");
        Console.WriteLine($"P762-MOD-LOOPBACK: target ship {targetId} removed with QSR_AdminBooted + message");
    }

    private static LogonRequest Logon(string name, string cdKey, ulong steamId) => new()
    {
        CharacterName = name, ZoneTicket = [1, 2, 3, 4], CDKey = cdKey, MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 762u, DwCookie = 0x762u, CrcFileList = -1,
        SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0, SteamID = steamId, DrmHash = "p762mod",
    };

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_moderator_cannot_use_the_administrator_boot_reason()
    {
        using var w = new World();
        w.SetRequest(w.ConnA, "plain");
        w.Config.ModeratorAccounts = ["MOD-someone-else"];
        w.Call("HandleQuitMission", w.ConnA, Quit(w.C.Id, QuitSideReason.AdminBooted, "nope"));
        Assert.True(w.HasPeer(w.C));
    }

    /// <summary>
    /// P762b security — LOGONREQ.CharacterName and SteamID are self-asserted
    /// (the port validates no Steam ticket / ASGS account), so a spoofed
    /// character name must never grant moderator rights. Only the configured
    /// bearer secret presented in LOGONREQ.CDKey is a credential (the P655
    /// model); names and Steam IDs are never consulted.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Spoofed_character_name_and_steam_id_do_not_grant_moderator_rights()
    {
        using var w = new World();
        // Claim both a configured character name and a configured Steam ID, but
        // present a different CD key: no moderator rights.
        w.SetRequest(w.ConnA, "MOD-admin", "PLAIN-KEY", 424242);
        w.Config.ModeratorAccounts = ["MOD-admin", "424242"];
        w.Call("HandleQuitMission", w.ConnA, Quit(w.C.Id, QuitSideReason.AdminBooted, "spoof"));
        Assert.True(w.HasPeer(w.C), "spoofed name/SteamID must not remove the target");

        // The same peer presenting the configured secret DOES get the rights.
        w.SetRequest(w.ConnA, "any-name", "MOD-admin");
        w.Call("HandleQuitMission", w.ConnA, Quit(w.C.Id, QuitSideReason.AdminBooted, "real secret"));
        Assert.False(w.HasPeer(w.C), "the configured bearer secret must still work");
    }
}
