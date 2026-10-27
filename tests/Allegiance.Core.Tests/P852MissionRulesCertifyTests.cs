using System.Reflection;
using System.Runtime.ExceptionServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public sealed class P852MissionRulesCertifyTests
{
    // P852 — certification tests for the "FedSrv mission rules (event-driven):
    // sides — join requests, locks, ready, alliances" batch (SIM-0542..0566,
    // SIM-0572/0573, SIM-0683/0684). Each test pins a native body branch the
    // row certification found divergent and fixed, or (where the row was
    // already equal) the row's main contract.

    private sealed class World : IDisposable
    {
        public readonly Srv Server = new();
        public readonly ServerHost Host = new();
        public readonly Mission Mission;
        public readonly Ship Ship, Mate, Enemy;
        public readonly ConnectionId Conn = new(1);
        public readonly List<(ConnectionId Conn, byte[] Message)> Output = [];

        public World()
        {
            Require.Artwork();
            var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
            var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
            Mission = Mission.Assemble(core, map, [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], Host);
            Mission.Stage = MissionStage.Started;
            Host.Transport = new TcpTransport(Host);
            typeof(TcpTransport).GetField("_isHost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Host.Transport, true);
            Set("_host", Host);
            Set("_config", new ServerConfig { LiveSettings = new LiveSettingsConfig(), Games = [new GameConfig()] });
            Set("_missions", new List<Mission> { Mission });
            var sector = Mission.Sectors[0];
            var hull = core.HullTypes.Values.First(h => h.GroupId >= 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod)) with { Speed = 180, Thrust = 1000 };
            Ship = new Ship(100, hull, new Vector3(5000, 5000, 5000), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            Mate = new Ship(101, hull, new Vector3(6000, 5000, 5000), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            Enemy = new Ship(102, hull, Ship.Position + Ship.Orientation.GetForward() * 100, Vector3.Zero, Orientation.Identity) { SideId = 1 };
            foreach (var s in new[] { Ship, Mate, Enemy }) Mission.PlaceShip(s, sector);
            Seed(Ship, Mate, Enemy);
            Mission.Sides[0].LeaderShipId = Ship.Id;
            Server._testSendCapture = (c, pkt) => Output.AddRange(FedPacket.Unpack(pkt).Select(m => (c, m.ToArray())));
            Now(10);
        }

        public void Seed(params Ship[] ships)
        {
            var sector = Mission.Sectors[0];
            var peers = ships.Select((s, i) => (new ConnectionId((uint)(i + 1)), s, sector)).ToList();
            Server.TestSeedPeers(peers);
        }

        public ConnectionId ConnOf(Ship ship) => ship == Ship ? Conn : ship == Mate ? new ConnectionId(2) : new ConnectionId(3);

        public void Set(string name, object value) => typeof(Srv).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Server, value);
        public object? Get(string name) => typeof(Srv).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Server);
        public object? Call(string name, params object?[] args)
        {
            try { return typeof(Srv).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)!
                .Invoke(Server, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }
        public void Now(double now) { Set("_simNow", now); Ship.InitClock(now); Mate.InitClock(now); Enemy.InitClock(now); }
        public void Send(ConnectionId from, IFedMessage msg) => Call("ProcessDatagram", from, FedPacket.Pack([msg.Encode()]));
        // Distinct message BYTES: _testSendCapture fires once per recipient
        // peer, so a whole-mission broadcast appears once per peer.
        public int CountFmid(ushort fmid, Func<byte[], bool>? match = null) =>
            Output.Where(o => FedMessageHeader.Read(o.Message).Fmid == fmid
                && (match is null || match(o.Message)))
                .Select(o => Convert.ToHexString(o.Message)).Distinct().Count();
        public void Dispose() { Host.Transport!.Shutdown(); Server.Dispose(); }
    }

    private static void SetRank(Srv server, Ship ship, short rank)
    {
        var live = server.Ships.First(l => l.Ship == ship);
        live.PlayerScore.Rank = rank;
    }

    // ── SIM-0543 — CheckPositionRequest: the no-defections gates ────────────

    /// <summary>P852 — native's no-defections refusals are gated on
    /// MissionParams::bAllowDefections, whose DEFAULT is true
    /// (Igc/igc.h:1134, 1255): with defections allowed (the default), a player
    /// seated on a real side may switch sides once the mission is started
    /// (fsmission.cpp:4583-4590 refuses only when !bAllowDefections). The port
    /// previously refused EVERY started switch (no config knob) — a behaviour
    /// divergence fixed by GameConfig.AllowDefections (default true). The
    /// explicit SIDE_TEAMLOBBY exemption on both native gates (:4585, :4588)
    /// means a lobby-parked switcher is never refused on this branch.</summary>
    [SkippableFact]
    public void D1_Started_switch_allowed_by_default_refused_when_defections_disabled()
    {
        using var w = new World();
        Assert.Equal(MissionStage.Started, w.Mission.Stage);
        Assert.Equal((short)0, w.Ship.SideId);
        // Default config (AllowDefections=true): the leaderless side 1 accepts
        // the switcher (RequestPosition's leaderless fast path, :4757).
        w.Send(w.Conn, new PositionReq { SideId = 1 });
        Assert.Equal((short)1, w.Ship.SideId);

        using var w2 = new World();
        w2.Set("_config", new ServerConfig
        {
            LiveSettings = new LiveSettingsConfig(),
            Games = [new GameConfig { AllowDefections = false }],
        });
        w2.Send(w2.Conn, new PositionReq { SideId = 1 });
        Assert.Equal((short)0, w2.Ship.SideId);
        Assert.Equal(1, w2.CountFmid(DelPositionReq.Fmid, m => DelPositionReq.Decode(m).Reason == DelPositionReqReason.NoDefections));
    }

    /// <summary>P852 — native's squad-game join gates (fsmission.cpp:4708-4721):
    /// an OCCUPIED side refuses non-members with DPR_WrongSquad (a side with no
    /// squad accepts everyone — GetIsMemberOfSquad(NA) is true, fsship.cpp:
    /// 1359-1362); an EMPTY side refuses players who cannot lead any squad
    /// (GetPreferredSquadToLead() == NA → DPR_CantLeadSquad, fsship.cpp:
    /// 1397-1417). The port previously had neither gate.</summary>
    [SkippableFact]
    public void D2_SquadGame_join_gates_WrongSquad_and_CantLeadSquad()
    {
        using var w = new World();
        // 0x7fff disables the rank-balance gate so the squad gates are the ones
        // under test (a Started world with default auto-balance would refuse
        // the cross-side request as DPR_TeamBalance first — native order).
        w.Set("_config", new ServerConfig
        {
            LiveSettings = new LiveSettingsConfig(),
            Games = [new GameConfig { MaxTeamImbalance = 0x7fff }],
        });
        w.Mission.SquadGame = true;
        w.Mission.Sides[0].SquadId = "sq1";
        // Occupied side 0 (Ship+Mate), requester not a member → WrongSquad.
        Assert.Equal(DelPositionReqReason.WrongSquad,
            w.Call("CheckPositionRequest", w.Mission, (short)0, w.Enemy));
        // A verified member passes the membership gate (capacity still gates
        // separately — here capacity is fine, so null).
        w.Set("_peerIdentities", new Dictionary<ConnectionId, JoinIdentity>
        {
            [new ConnectionId(3)] = new JoinIdentity("iss", "aud", "sub", "Ally", 0, 0, "jti",
                [new SquadClaim("sq1", "Squad One", CanLead: false)]),
        });
        Assert.Null(w.Call("CheckPositionRequest", w.Mission, (short)0, w.Enemy));

        // Empty side 1: a squadless requester cannot lead it → CantLeadSquad.
        using var w2 = new World();
        w2.Set("_config", new ServerConfig
        {
            LiveSettings = new LiveSettingsConfig(),
            Games = [new GameConfig { MaxTeamImbalance = 0x7fff }],
        });
        w2.Mission.SquadGame = true;
        Assert.Equal(DelPositionReqReason.CantLeadSquad,
            w2.Call("CheckPositionRequest", w2.Mission, (short)1, w2.Enemy));
        // With a leadable squad claim the gate passes.
        w2.Set("_peerIdentities", new Dictionary<ConnectionId, JoinIdentity>
        {
            [new ConnectionId(3)] = new JoinIdentity("iss", "aud", "sub", "Ally", 0, 0, "jti",
                [new SquadClaim("sq9", "Leaders", CanLead: true)]),
        });
        Assert.Null(w2.Call("CheckPositionRequest", w2.Mission, (short)1, w2.Enemy));
    }

    // ── SIM-0544 — RequestPosition: the switcher leaves their side first ────

    /// <summary>P852 — native RequestPosition removes the switcher from their
    /// CURRENT side (RemovePlayerFromSide QSR_SwitchingSides) BEFORE the
    /// request pends (fsmission.cpp:4749-4751): the player waits in the team
    /// lobby, off their old side, until the leader answers (native
    /// SetSide(this, GetSide(SIDE_TEAMLOBBY)), fsmission.cpp:1123). The port
    /// previously kept the requester seated on their old side while pending —
    /// the known SIM-0608 gap. Now the pending branch parks the ship on
    /// SIDE_TEAMLOBBY (-2) via MoveToTeamLobby (real succession + payday +
    /// QUIT_SIDE(126)), and the eventual accept seats them WITHOUT a second
    /// QUIT_SIDE (native skips RemovePlayerFromSide when already in the lobby,
    /// :4748-4750).</summary>
    [SkippableFact]
    public void R1_Pending_switch_parks_requester_in_team_lobby_then_accept_seats()
    {
        using var w = new World();
        w.Mission.Stage = MissionStage.NotStarted;
        w.Mission.Sides[0].AutoAccept = false;   // led side, approval required
        w.Send(w.ConnOf(w.Enemy), new PositionReq { SideId = 0 });
        // Parked in the team lobby, off side 1.
        Assert.Equal((short)-2, w.Enemy.SideId);
        Assert.Equal(1, w.CountFmid(QuitSide.Fmid, m => { var q = QuitSide.Decode(m); return q.ShipId == 102 && q.Reason == QuitSideReason.SwitchingSides; }));
        Assert.Equal(1, w.CountFmid(PositionReqBroadcast.Fmid, m => { var p = PositionReqBroadcast.Decode(m); return p.ShipId == 102 && p.SideId == 0; }));

        // The side-0 leader accepts: the parked requester is seated, with no
        // second QUIT_SIDE (native AddPlayerToSide path, no RemoveFromSide).
        w.Send(w.Conn, new PositionAck { ShipId = 102, SideId = 0, FAccepted = true });
        Assert.Equal((short)0, w.Enemy.SideId);
        Assert.Equal(1, w.CountFmid(QuitSide.Fmid, m => { var q = QuitSide.Decode(m); return q.ShipId == 102 && q.Reason == QuitSideReason.SwitchingSides; }));
        Assert.Equal(1, w.CountFmid(JoinSide.Fmid, m => { var j = JoinSide.Decode(m); return j.ShipId == 102 && j.SideId == 0; }));
    }

    // ── SIM-0548 — RejectSideJoinRequests / SetAutoAccept-on at last exit ───

    /// <summary>P852 — native RemovePlayerFromSide's last-player rules
    /// (fsmission.cpp:930-952, 1155-1158): when the side's LAST player (the
    /// team leader) leaves BEFORE the game starts, the side turns auto-accept
    /// ON — AUTO_ACCEPT(52) broadcast (:934-941) plus
    /// SetAutoAccept(psideOld, true) at :1158, whose sweep accepts every
    /// pending requester. The port previously left the pending requesters
    /// hanging on a leaderless side.</summary>
    [SkippableFact]
    public void R2_Leader_leaves_side_pre_game_turns_autoaccept_on_and_seats_pending()
    {
        using var w = new World();
        w.Mission.Stage = MissionStage.NotStarted;
        w.Server.TestSeedPeers(
            [(w.Conn, w.Ship, w.Mission.Sectors[0]), (new ConnectionId(3), w.Enemy, w.Mission.Sectors[0])]);
        w.Mission.Sides[0].AutoAccept = false;
        w.Send(new ConnectionId(3), new PositionReq { SideId = 0 });
        Assert.Equal((short)-2, w.Enemy.SideId);   // parked pending

        // The side-0 leader switches away, emptying side 0 pre-game.
        w.Send(w.Conn, new PositionReq { SideId = 1 });
        Assert.True(w.Mission.Sides[0].AutoAccept, "empty side pre-game turns auto-accept ON (fsmission.cpp:934-941,1158)");
        // The pending requester was accepted onto the emptied side.
        Assert.Equal((short)0, w.Enemy.SideId);
        Assert.Equal(1, w.CountFmid(AutoAccept.Fmid, m => { var aa = AutoAccept.Decode(m); return aa.SideId == 0 && aa.FAutoAccept; }));
    }

    /// <summary>P852 — once the mission is running, the same last-player exit
    /// "implicitly rejects" the side's pending join requests
    /// (fsmission.cpp:945-951 RejectSideJoinRequests → NotifyPlayerBoot:
    /// DELPOSITIONREQ(DPR_Rejected) whole-mission). The port previously left
    /// them pending forever.</summary>
    [SkippableFact]
    public void R3_Leader_leaves_side_mid_game_rejects_pending()
    {
        using var w = new World();
        // 0x7fff disables the rank-balance gate so the mid-game switch is
        // decided by the defections/empty-side rules under test here.
        w.Set("_config", new ServerConfig
        {
            LiveSettings = new LiveSettingsConfig(),
            Games = [new GameConfig { MaxTeamImbalance = 0x7fff }],
        });
        w.Mission.Sides[0].AutoAccept = false;
        w.Send(new ConnectionId(3), new PositionReq { SideId = 0 });
        Assert.Equal((short)-2, w.Enemy.SideId);
        var pending = (System.Collections.IDictionary)w.Get("_pendingJoinRequests")!;
        Assert.True(pending.Count == 1, "one pending join request");

        // Drop Mate so Ship is side 0's last player, then the leader switches
        // away mid-game (defections allowed by default), emptying side 0.
        w.Server.TestSeedPeers(
            [(w.Conn, w.Ship, w.Mission.Sectors[0]), (new ConnectionId(3), w.Enemy, w.Mission.Sectors[0])]);
        w.Send(w.Conn, new PositionReq { SideId = 1 });
        Assert.Empty(pending);
        Assert.Equal(1, w.CountFmid(DelPositionReq.Fmid, m => { var d = DelPositionReq.Decode(m); return d.ShipId == 102 && d.Reason == DelPositionReqReason.Rejected; }));
    }

    // ── SIM-0542 — PickNewSide: rank-sum default + imbalance override ───────

    /// <summary>P852 — the auto-pick implements native PickNewSide
    /// (fsmission.cpp:4454-4524): the DEFAULT choice is the candidate side
    /// with the LOWEST RANK SUM (floored at 1 per player, :1352-1369), and the
    /// fewest-players side wins only through the iMaxImbalance override
    /// (:4506-4516). The port previously always picked the fewest-players
    /// side, ignoring rank.</summary>
    [SkippableFact]
    public void P1_AutoPick_lowest_rank_sum_then_imbalance_override()
    {
        using var w = new World();
        w.Mission.Stage = MissionStage.NotStarted;
        w.Set("_config", new ServerConfig
        {
            LiveSettings = new LiveSettingsConfig(),
            Games = [new GameConfig { MaxTeamImbalance = 0x7fff }],   // override disabled
        });
        // Side 0: Ship+Mate+extra rank-1 players (sum 3); side 1: Enemy rank 5.
        SetRank(w.Server, w.Mate, 1);
        var extra = new Ship(103, w.Mate.HullType, new Vector3(7000, 5000, 5000), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        w.Mission.PlaceShip(extra, w.Mission.Sectors[0]);
        w.Server.TestSeedPeers([(w.Conn, w.Ship, w.Mission.Sectors[0]), (new ConnectionId(2), w.Mate, w.Mission.Sectors[0]),
            (new ConnectionId(3), w.Enemy, w.Mission.Sectors[0]), (new ConnectionId(4), extra, w.Mission.Sectors[0])]);
        SetRank(w.Server, extra, 1);
        SetRank(w.Server, w.Enemy, 5);
        // Side 0 has THREE players (rank sum 1) vs side 1 (one player, rank sum
        // 5): the LOWEST RANK SUM wins → side 0, even though side 1 has the
        // fewest players (native default choice, fsmission.cpp:4494).
        object?[] args = [w.Mission, new ConnectionId(9), "newbie", new DelPositionReqReason()];
        short picked = (short)(w.Call("AutoPickSide", args)!);
        Assert.Equal((short)0, picked);

        // With iMaxImbalance = 1 the override fires: seating on side 0 (3
        // players) would give 3+1 > fewest(1) + 1 → the fewest-players side 1
        // wins instead (fsmission.cpp:4513-4515).
        w.Set("_config", new ServerConfig
        {
            LiveSettings = new LiveSettingsConfig(),
            Games = [new GameConfig { MaxTeamImbalance = 1 }],
        });
        picked = (short)(w.Call("AutoPickSide", args)!);
        Assert.Equal((short)1, picked);
    }

    // ── SIM-0556 — RandomizeSides: auto-accept OFF sweep ────────────────────

    /// <summary>P852 — native RandomizeSides turns auto-accept OFF on every
    /// side that has it on before moving anyone (KGJV change, fsmission.cpp:
    /// 5142-5160), broadcasting AUTO_ACCEPT(52) fAutoAccept=false. The port's
    /// rebalance previously left auto-accept on everywhere.</summary>
    [SkippableFact]
    public void F1_RebalanceSides_turns_autoaccept_off_whole_mission()
    {
        using var w = new World();
        w.Mission.Stage = MissionStage.NotStarted;
        Assert.True(w.Mission.Sides[0].AutoAccept);
        Assert.True(w.Mission.Sides[1].AutoAccept);
        w.Call("RebalanceSides", w.Mission);
        Assert.False(w.Mission.Sides[0].AutoAccept);
        Assert.False(w.Mission.Sides[1].AutoAccept);
        Assert.Equal(2, w.CountFmid(AutoAccept.Fmid, m => !AutoAccept.Decode(m).FAutoAccept));
    }

    // ── SIM-0566 — CheckForSideAllReady: nMinPlayersPerTeam floor ───────────

    /// <summary>P852 — CheckForSideAllReady forces a side NOT ready while it
    /// has fewer players than nMinPlayersPerTeam (fsmission.cpp:5501-5503).
    /// The port hardcoded a 1-player floor; GameConfig.MinPlayersPerTeam now
    /// carries native's parameter (default 1, igc.h:1333).</summary>
    [SkippableFact]
    public void Y1_MinPlayersPerTeam_gates_side_readiness()
    {
        using var w = new World();
        w.Set("_config", new ServerConfig
        {
            LiveSettings = new LiveSettingsConfig(),
            Games = [new GameConfig { MinPlayersPerTeam = 2, AutoStart = false }],
        });
        w.Mission.Stage = MissionStage.NotStarted;
        w.Send(w.Conn, new PlayerReady { ShipId = 100, FReady = true });
        Assert.False(w.Mission.Sides[0].Ready, "1 of MinPlayersPerTeam=2 players: side stays not-ready (fsmission.cpp:5501-5503)");
        w.Send(new ConnectionId(2), new PlayerReady { ShipId = 101, FReady = true });
        Assert.True(w.Mission.Sides[0].Ready);

        using var w2 = new World();
        w2.Mission.Stage = MissionStage.NotStarted;
        w2.Server.TestSeedPeers(
            [(w2.Conn, w2.Ship, w2.Mission.Sectors[0]), (new ConnectionId(3), w2.Enemy, w2.Mission.Sectors[0])]);
        w2.Send(w2.Conn, new PlayerReady { ShipId = 100, FReady = true });
        Assert.True(w2.Mission.Sides[0].Ready, "default MinPlayersPerTeam=1 keeps native default behaviour (igc.h:1333)");
    }

    // ── SIM-0562 — DeactivateSide: rgfReady reset + allied winner naming ────

    /// <summary>P852 — native DeactivateSide clears the side's ready flag
    /// alongside its active flag (rgfActive = rgfReady = false,
    /// fsmission.cpp:5326-5327), and when the surviving side has allies the
    /// game-over reason names them ("A & B won by outlasting all other sides.",
    /// fsmission.cpp:5396-5412; AlliedSides is alliance-group equality,
    /// igc.h:4296-4303). The port previously kept a stale Ready=true on the
    /// eliminated side and never named allies.</summary>
    [SkippableFact]
    public void Y2_DeactivateSide_clears_ready_and_names_allied_winner()
    {
        using var w = new World();
        w.Mission.Sides[0].AllyGroup = 0;
        w.Mission.Sides[1].AllyGroup = 0;
        w.Mission.Sides[1].Ready = true;
        // Stage=Over makes the victory check (and its heavy DeclareGameOver
        // tail, which needs a booted server's matchup/rotation state) return
        // early — DeactivateSide's own flag writes still run.
        w.Mission.Stage = MissionStage.Over;
        w.Call("DeactivateSide", w.Mission, w.Mission.Sides[1]);
        Assert.False(w.Mission.Sides[1].Ready, "rgfReady[sideid] = false (fsmission.cpp:5326-5327)");
        // The allied-winner naming (fsmission.cpp:5396-5412) is the shared
        // reason builder CheckForVictoryByInactiveSidesAfterDeactivation feeds
        // DeclareGameOver; pinned here directly: the winner AND its allies are
        // named (AlliedSides = alliance-group equality, igc.h:4296-4303).
        string reason = (string)w.Call("AlliedWinnerReason", w.Mission, w.Mission.Sides[0])!;
        Assert.Contains("Alpha & Bravo", reason);
        Assert.Contains("won by outlasting all other sides.", reason);
        // A solo winner names only itself (native's else branch, :5413-5414).
        w.Mission.Sides[1].AllyGroup = null;
        w.Mission.Sides[0].AllyGroup = null;
        Assert.Equal("Alpha won by outlasting all other sides.",
            (string)w.Call("AlliedWinnerReason", w.Mission, w.Mission.Sides[0])!);
    }
}
