using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

public sealed class P718InterfaceMessageTests
{
    internal static void Feed(ConnectionSession session, params IFedMessage[] messages) =>
        ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack(messages.Select(m => m.Encode()).ToArray()));
    internal static void Seed(ConnectionSession session)
    {
        Feed(session, new MissionDef { Stage = MissionStageMessage.StageStarted },
            new SideRoster { Sides = [new(0, 1, 2, "Alpha"), new(1, 3, 1, "Bravo")] },
            new SetCluster { SectorId = 1, Ship = P675MissionDiscoveryTests.Pose(10) },
            new ExportShip { ShipId = -1, SideId = 0 },
            new PlayerInfo { ShipId = 10, ISide = 0, CharacterName = "Me" },
            new PlayerInfo { ShipId = 11, ISide = 0, CharacterName = "Scout" },
            new PlayerInfo { ShipId = 12, ISide = 1, CharacterName = "Enemy" });
    }
    private static ExportAsteroid Rock(short sector = 1) => new() { Asteroid = new DataAsteroidIGC(
        1, [1, 2, 3], [0, 1, 0], [0, 0, 1], [0, 0, 0], 1, 1, 0x20, 50, 100, 100, "", "", "", sector, "Si1", 255) };

    [Fact]
    public void Civilization_echo_updates_roster_and_random_choice_without_losing_side_metadata()
    {
        using var session = new ConnectionSession(); Seed(session);
        var old = session.World.SideRoster!;
        Feed(session, new ChangeTeamCiv { SideId = 0, CivId = 5, Random = true });
        Assert.Equal(new ClientWorld.SideCivilization(5, true), session.World.SideCivilizations[0]);
        Assert.Equal(old.Sides[0] with { CivId = 5 }, session.World.SideRoster!.Sides[0]);
        Assert.Equal(old.Sides[1], session.World.SideRoster.Sides[1]);
        Assert.Equal((short)1, old.Sides[0].CivId); // immutable UI snapshot
        Feed(session, new ChangeTeamCiv { SideId = 0, CivId = 3, Random = false });
        Assert.Equal(new ClientWorld.SideCivilization(3, false), session.World.SideCivilizations[0]);
        Assert.DoesNotContain(ChangeTeamCiv.Fmid, session.UnknownFmids);
    }

    [Fact]
    public void Probe_updates_require_enter_game_and_current_flight_sector_and_iterate_backwards()
    {
        using var session = new ConnectionSession(); Seed(session);
        Feed(session, new ExportProbe { Probe = new DataProbeExport { ProbeId = 30, ClusterId = 1 } },
            new ExportProbe { Probe = new DataProbeExport { ProbeId = 31, ClusterId = 2 } });
        var update = new ProbesUpdate { Entries = [new(30, 120), new(30, 60), new(31, 0), new(99, 0)] };
        Feed(session, update);
        Assert.Equal(1f, session.World.Probes[30].Fraction);
        Feed(session, new EnterGame(), update);
        Assert.Equal(.5f, session.World.Probes[30].Fraction); // first duplicate wins, native reverse loop
        Assert.Equal(1f, session.World.Probes[31].Fraction);
        Assert.False(session.World.Probes.ContainsKey(99));
        Assert.DoesNotContain(ProbesUpdate.Fmid, session.UnknownFmids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Asteroid_termination_removes_health_and_preserves_conversion_effect_only(bool explode)
    {
        using var session = new ConnectionSession(); Seed(session);
        Feed(session, Rock(), new AsteroidsUpdate { Entries = [new(50, default, 10, 120)] },
            new ExportBuildingEffect { Effect = new DataBuildingEffectExport { AsteroidId = 50, ClusterId = 1 } });
        var dead = new AsteroidDestroyed { ClusterId = 1, AsteroidId = 50, ExplodeF = explode };
        Feed(session, dead); Assert.True(session.World.Asteroids.ContainsKey(50));
        Feed(session, new EnterGame(), new AsteroidDestroyed { ClusterId = 2, AsteroidId = 50, ExplodeF = true });
        Assert.True(session.World.Asteroids.ContainsKey(50));
        Feed(session, dead, dead); // stale repeat must not double-explode
        Assert.Empty(session.World.Asteroids); Assert.Empty(session.World.AsteroidLive);
        Assert.Equal(!explode, session.World.BuildingEffects.ContainsKey(50));
        Assert.Equal(explode ? 1 : 0, session.World.Explosions.Count);
        if (explode) Assert.Equal(ExplosionType.Asteroid, session.World.Explosions[0].Type);
        Assert.DoesNotContain(AsteroidDestroyed.Fmid, session.UnknownFmids);
    }

    [Fact]
    public void Wing_echo_notifies_only_commanded_self_and_does_not_create_unknown_or_enemy_roster_members()
    {
        using var session = new ConnectionSession(); Seed(session);
        Feed(session, new SetWingId { ShipID = 10, WingId = 5 }, new SetWingId { ShipID = 11, WingId = 3, BCommanded = true },
            new SetWingId { ShipID = 12, WingId = 4 }, new SetWingId { ShipID = 99, WingId = 2 });
        Assert.Equal(5, session.World.Ships[10].WingId); Assert.Equal(3, session.World.Ships[11].WingId);
        Assert.Equal(0, session.World.Ships[12].WingId); Assert.False(session.World.Ships.ContainsKey(99));
        Assert.Empty(session.World.TakeFeedbackLines());
        Feed(session, new SetWingId { ShipID = 10, WingId = 9, BCommanded = true },
            new SetWingId { ShipID = 10, WingId = 10, BCommanded = true });
        var line = Assert.Single(session.World.TakeFeedbackLines());
        Assert.Equal("You have been assigned to wing echo", line.Text); Assert.True(line.IsCritical);
        Assert.Equal(9, session.World.Ships[10].WingId);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Commanded_wing_assignment_notifies_joined_self_before_launch()
    {
        Require.Artwork();
        using var session = new ConnectionSession();
        // Joining establishes membership before SET_CLUSTER assigns a launch pose.
        Feed(session, new MissionDef { SzIGCStaticFile = "PCore001" }, new JoinedMission { ShipId = 10 },
            new PlayerInfo { ShipId = 10, ISide = 0, CharacterName = "Me" },
            new SetWingId { ShipID = 10, WingId = 3, BCommanded = true });
        Assert.Null(session.World.MyShipId);
        Assert.Equal<short?>(10, session.World.JoinedShipId);
        Assert.Equal(3, session.World.Ships[10].WingId);
        var line = Assert.Single(session.World.TakeFeedbackLines());
        Assert.Equal("You have been assigned to wing escort", line.Text);
        Assert.True(line.IsCritical);
        Feed(session, new SetWingId { ShipID = 10, WingId = 5 });
        Assert.Equal(5, session.World.Ships[10].WingId);
        Assert.Empty(session.World.TakeFeedbackLines());
    }

    [Fact]
    public void Quit_side_clears_team_privileges_and_money_preserves_ready_and_join_reseats_identity()
    {
        using var session = new ConnectionSession(); Seed(session);
        Feed(session, new EnterGame(), new PlayerReady { ShipId = 11, FReady = false },
            new SetTeamLeader { SideId = 0, ShipId = 11 }, new SetMissionOwner { SideId = 0, ShipId = 11 },
            new SetMoney { ShipId = 11, Money = 999 },
            new QuitSide { ShipId = 11, Reason = QuitSideReason.SwitchingSides, Message = "switch" });
        var w = session.World;
        Assert.Equal(-2, w.Ships[11].SideId); Assert.Equal(-2, w.Ships[11].Player!.ISide);
        Assert.True(w.Ships[11].IsGone); Assert.Null(w.TeamLeaderShipIdFor(0)); Assert.Null(w.MissionOwnerBroadcast);
        Assert.Equal(0, w.WalletByShipId[11]); Assert.False(w.Roster.ReadyByShipId[11]);
        Assert.Equal(new ClientWorld.Departure(11, QuitSideReason.SwitchingSides, "switch", false), w.LastDeparture);
        Assert.Contains(w.Events.Chats, c => c.Message == "Scout is switching sides.");
        Feed(session, new JoinSide { ShipId = 11, SideId = 1 });
        Assert.Equal(1, w.Ships[11].SideId); Assert.Equal(1, w.Ships[11].Player!.ISide);
        Assert.True(w.InGame); Assert.DoesNotContain(QuitSide.Fmid, session.UnknownFmids);
    }

    [Fact]
    public void Own_quit_side_clears_sector_contents_but_keeps_mission_players_and_can_reenter()
    {
        using var session = new ConnectionSession(); Seed(session);
        Feed(session, new EnterGame(), Rock(), new ExportProbe { Probe = new DataProbeExport { ProbeId = 30, ClusterId = 1 } },
            new QuitSide { ShipId = 10, Reason = QuitSideReason.SwitchingSides });
        var w = session.World;
        Assert.False(w.InGame); Assert.Equal<short?>(-2, w.MySideId); Assert.NotNull(w.Mission);
        Assert.Equal(3, w.Ships.Count); Assert.Empty(w.Asteroids); Assert.Empty(w.Probes);
        Assert.All(w.Ships.Values, s => { Assert.Null(s.PoseSectorId); Assert.Null(s.Latest); });
        Assert.NotNull(w.SideRoster); Assert.False(session.Lost);
        Feed(session, new JoinSide { ShipId = 10, SideId = 1 }, new EnterGame(),
            new SetCluster { SectorId = 2, Ship = P675MissionDiscoveryTests.Pose(10) });
        Assert.True(w.InGame); Assert.Equal<short?>(1, w.MySideId); Assert.Equal(2, w.Ships[10].SectorId);
    }

    [Fact]
    public void Remote_quit_mission_removes_player_requests_and_ready_without_touching_other_players()
    {
        using var session = new ConnectionSession(); Seed(session);
        session.World.SetPilotMuted(11, true);
        session.World.SetPilotMuted(12, true);
        Feed(session, new PlayerReady { ShipId = 11, FReady = true }, new PositionReqBroadcast { SideId = 1, ShipId = 11 },
            new PositionReqBroadcast { SideId = 0, ShipId = 12 },
            new QuitMission { ShipId = 11, Reason = QuitSideReason.LinkDead, Message = "gone" });
        var w = session.World;
        Assert.False(w.Ships.ContainsKey(11)); Assert.False(w.Roster.ReadyByShipId.ContainsKey(11));
        Assert.False(w.IsPilotMuted(11)); Assert.True(w.IsPilotMuted(12));
        Assert.Equal(new ClientWorld.PositionRequest(0, 12), Assert.Single(w.PendingPositionRequests));
        Assert.Equal(new ClientWorld.Departure(11, QuitSideReason.LinkDead, "gone", true), w.LastDeparture);
        Assert.True(w.Ships.ContainsKey(10)); Assert.False(session.Lost);
        Assert.DoesNotContain(QuitMission.Fmid, session.UnknownFmids);
    }

    [Fact]
    public void Own_quit_mission_clears_world_signals_loss_and_stops_remaining_packet_messages()
    {
        using var session = new ConnectionSession(); Seed(session); int losses = 0; session.SessionLost += () => losses++;
        Feed(session, new EnterGame(), new QuitMission { ShipId = 10, Reason = (QuitSideReason)2, Message = "removed" },
            new PlayerInfo { ShipId = 99, CharacterName = "must not return" });
        Assert.True(session.Lost); Assert.Equal(1, losses); Assert.Empty(session.World.Ships);
        Assert.Null(session.World.Mission); Assert.Null(session.World.MyShipId); Assert.False(session.World.InGame);
        Assert.Equal("removed", session.World.LastDeparture!.Value.Message);
        Assert.Equal((QuitSideReason)2, session.World.OwnMissionDeparture!.Reason);
        Assert.Equal("removed", session.World.OwnMissionDeparture.Message);
        session.World.Reset();
        Assert.Null(session.World.OwnMissionDeparture);
    }

    [Fact]
    public void Enter_game_barrier_preserves_stage_and_inventory_game_state_replaces_every_side_and_resets()
    {
        using var session = new ConnectionSession(); Seed(session);
        Feed(session, new BuyLoadoutAck { HullId = 301, Parts = [new(1, 20, 0, 0)] },
            new GameState { Conquest = [1, 2, 3, 4, 5, 6], Territory = [7, 8, 9, 10, 11, 12],
                NFlags = [13, 14, 15, 16, 17, 18], NArtifacts = [19, 20, 21, 22, 23, 24] }, new EnterGame());
        var w = session.World;
        Assert.True(w.InGame); Assert.Equal(MissionStageMessage.StageStarted, w.Mission!.Stage);
        Assert.Single(w.Ships[10].Loadout!); Assert.Equal(6, w.SideGameStates.Count);
        Assert.Equal(new ClientWorld.SideGameState(6, 12, 18, 24), w.SideGameStates[5]);
        Feed(session, new GameState()); Assert.All(w.SideGameStates.Values, s => Assert.Equal(default, s));
        Assert.DoesNotContain(EnterGame.Fmid, session.UnknownFmids); Assert.DoesNotContain(GameState.Fmid, session.UnknownFmids);
        w.BeginNextMission(); Assert.False(w.InGame); Assert.Empty(w.SideGameStates); Assert.Empty(w.SideCivilizations);
        Feed(session, new EnterGame(), new ChangeTeamCiv { SideId = 0, CivId = 1 });
        w.Reset(); Assert.False(w.InGame); Assert.Empty(w.SideGameStates); Assert.Empty(w.SideCivilizations); Assert.Null(w.LastDeparture);
    }
}
