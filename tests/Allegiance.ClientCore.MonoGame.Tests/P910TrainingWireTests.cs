using System.Reflection;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.Training;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using static Allegiance.ClientCore.MonoGame.Tests.P910TrainingIntegrationTests;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("Training")]
public sealed class P910TrainingWireTests : IDisposable
{
    public P910TrainingWireTests() => TrainingClock.ResetForTests();
    public void Dispose() { TrainingController.EndMission(); TrainingClock.ResetForTests(); TrainingOverlay.Current = 0; }
    private sealed class Client : IDisposable
    {
        public readonly TrainingHost Host;
        public readonly ConnectionSession Connection = new();
        public readonly Game1 Game;
        public readonly ClientTrainingSite Site;
        public readonly TrainingSession Session;
        public readonly ProbeMission Mission;
        public Client(HudStyle style, TrainingHost.TrainingVariant? variant = null)
        {
            Host = TrainingHost.Start(variant ?? TrainingHost.BasicFlight);
            Game = Headless(Connection, style);
            Set(Game, "_embeddedHost", Host);
            Set(Game, "_commandCamera", new CommandCamera());
            Site = new(Game.CreateTrainingFacilities(variant == TrainingHost.PracticeArena ? 6 : 2));
            Mission = new(Site);
            Session = new(Site, Mission);
            Set(Game, "_trainingSession", Session);
            Host.SetActiveMission(Mission);
            Host.StartBackgroundTicker();
            Connection.Connect(Host.Port, "p910");
            Connection.SendLogonRequest(new LogonRequest { CharacterName = "p910", ZoneTicket = [1, 2, 3, 4],
                CDKey = "ABCDEFGHIJKL", FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1,
                SteamAuthTicket = new byte[16], DrmHash = "p910" });
            Assert.True(Connection.WaitForAck(15000).FValidated);
            Connection.SendPositionReq(0);
            Assert.True(SpinWait.SpinUntil(() => Site.HasPlayerShip, 15000));
            Host.Paused = true;
            Session.Start();
        }
        public void Dispose() { Session.DetachClientHooks(); Connection.Dispose(); Host.Dispose(); }
        public object Server => typeof(TrainingHost).GetField("_server", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Host)!;
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(8)] [InlineData(10)]
    public void Every_lesson_exposes_native_commander_identity_and_script_command_slots(int lesson)
    {
        Require.Artwork();
        var variant = lesson switch { 2 => TrainingHost.BasicFlight, 3 => TrainingHost.BasicWeaponry,
            4 => TrainingHost.EnemyEngagement, 5 => TrainingHost.CommandView, 6 => TrainingHost.PracticeArena,
            8 => TrainingHost.Nanite, _ => TrainingHost.FreeFlight };
        using var c = new Client(HudStyle.Modern, variant);
        var commander = c.Host.CommanderShip;
        Assert.Equal("Commander", commander.Name); Assert.Equal(412, commander.HullType.HullId);
        Assert.Equal(Ship.PilotTypeWingman, commander.PilotType); Assert.Equal((short?)0, commander.SideId);
        Assert.Equal(commander.Id, c.Site.CommanderId);
        Assert.True(SpinWait.SpinUntil(() => c.Connection.World.Ships.TryGetValue(commander.Id, out var entity)
            && entity.HullId == 412, 3000));
        Assert.Equal("Commander", c.Connection.World.Ships[commander.Id].CharacterName);
        Assert.Equal(412, c.Connection.World.Ships[commander.Id].HullId);
        if (lesson != 2)
        {
            Assert.Equal(c.Host.HomeStationId, commander.StationId);
            Assert.Equal(OrderChange.CidDoNothing, commander.CurrentCommandId);
            Assert.Equal(OrderChange.CidDoNothing, commander.AcceptedShipCommandId);
            Assert.False(commander.Autopilot);
        }
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic, false)] [InlineData(HudStyle.Modern, false)]
    [InlineData(HudStyle.Classic, true)] [InlineData(HudStyle.Modern, true)]
    public void Docked_wire_edge_calls_lesson_before_same_frame_hangar_decision(HudStyle style, bool finish)
    {
        Require.Artwork(); using var c = new Client(style);
        c.Mission.EndOnLanding = finish;
        var station = c.Host.Mission.GetStation((short)c.Host.HomeStationId)!;
        c.Host.Mission.Site!.DockWithStationEvent(c.Host.PlayerShip,
            new Allegiance.Core.Model.DockStation(c.Host.Mission.Map.Stations.Single(s => s.StationId == station.StationId)));
        Assert.True(SpinWait.SpinUntil(() => c.Site.PlayerDocked, 3000));
        c.Session.Advance();
        Assert.Equal(1, c.Mission.Landings);
        Assert.Equal(!finish, c.Session.AllowHangar);
        Assert.Equal(!finish, (bool)typeof(Game1).GetProperty("TrainingAllowsHangar", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c.Game)!);
        c.Session.Advance(); Assert.Equal(1, c.Mission.Landings);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Station_wire_event_retains_launcher_and_runs_defeat_policy_on_game_thread(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style);
        var station = c.Host.Mission.GetStation((short)c.Host.HomeStationId)!;
        // Real server station callback -> STATION_DESTROYED -> ClientWorld -> session.
        c.Host.Mission.Site!.KillStationEvent(station, c.Host.CommanderShip, float.MaxValue);
        Assert.True(SpinWait.SpinUntil(() => c.Connection.World.StationWasDestroyed(station.StationId), 3000));
        Assert.Equal(0, c.Mission.StationCalls);
        c.Session.Advance();
        Assert.Equal(1, c.Mission.StationCalls);
        Assert.Equal(new TargetRef(ObjectType.Station, station.StationId), c.Mission.Destroyed);
        Assert.Equal(new TargetRef(ObjectType.Ship, c.Host.CommanderShipId), c.Mission.Launcher);
        Assert.Contains(c.Site.Messages, m => m.Contains("stations have all been destroyed"));
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Local_station_hook_preserves_nonship_launcher_beyond_wire_field(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style);
        short probeType = c.Host.Mission.Core.ProbeTypes.Keys.First();
        c.Host.CreateProbe(4960, probeType, new(500, 0, 0), 1, c.Host.StartSectorId);
        var probe = c.Host.Mission.GetProbe(4960)!;
        var station = c.Host.Mission.GetStation((short)c.Host.HomeStationId)!;
        c.Host.Mission.Site!.KillStationEvent(station, probe, float.MaxValue);
        Assert.True(SpinWait.SpinUntil(() => c.Connection.World.StationWasDestroyed(station.StationId), 3000));
        c.Session.Advance();
        Assert.Equal(new TargetRef(ObjectType.Probe, probe.Id), c.Mission.Launcher);
        Assert.Equal(1, c.Mission.StationCalls);
        c.Session.Advance(); Assert.Equal(1, c.Mission.StationCalls);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Self_chat_cannot_send_an_order_past_the_training_receive_guard(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style);
        short self = (short)c.Host.PlayerShipId;
        short before = c.Host.PlayerShip.CurrentCommandId;
        c.Connection.SendChat("self order", ChatTarget.Individual, self, commandId: OrderChange.CidGoto,
            oidTarget: (short)c.Host.CommanderShipId, otTarget: (short)IgcObjectType.Ship);
        Assert.True(c.Site.ConsumeChat((int)ChatTarget.Individual));
        Assert.Equal(-1, c.Connection.World.Events.Chats.Last().OidTarget);
        Assert.Equal(before, c.Host.PlayerShip.CurrentCommandId);
        var buoy = new DataBuoyIGC([100, 200, 300], c.Host.StartSectorId, 0, 4961, true);
        c.Connection.SendChatBuoy("self goto", ChatTarget.Individual, self, buoy, commandId: OrderChange.CidGoto);
        Assert.True(c.Site.ConsumeChat((int)ChatTarget.Individual));
        Assert.Equal(-1, c.Connection.World.Events.ChatBuoys.Last().Cd.CommandID);
        Assert.Equal(before, c.Host.PlayerShip.CurrentCommandId);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Queued_order_keeps_native_training_exception_and_multiplayer_still_expires(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style);
        c.Site.CreateWaypoint(4901, new(1000, 0, 0), c.Host.StartSectorId, 0, true);
        c.Site.IssueCommand(c.Site.PlayerShip.Ref, TrainingMissionIds.CommandQueued, new(ObjectType.Buoy, 4901), TrainingMissionIds.CmdGoto);
        Assert.True(SpinWait.SpinUntil(() => c.Connection.World.MyQueuedCommand is not null, 3000));
        var expire = c.Server.GetType().GetMethod("AutoRejectExpiredQueuedCommands", BindingFlags.Instance | BindingFlags.NonPublic)!;
        double afterTimeout = c.Host.PlayerShip.QueuedAt + 30;
        expire.Invoke(c.Server, [afterTimeout]);
        Assert.Equal(OrderChange.CidGoto, c.Host.PlayerShip.QueuedCommandId);
        Assert.NotNull(c.Connection.World.MyQueuedCommand);
        c.Host.Mission.IsTraining = false;
        expire.Invoke(c.Server, [afterTimeout]);
        Assert.Equal(OrderChange.CidNone, c.Host.PlayerShip.QueuedCommandId);
        Assert.True(SpinWait.SpinUntil(() => c.Connection.World.MyQueuedCommand is null, 3000));
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Death_restore_clears_remote_view_cluster_and_restores_camera(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style, TrainingHost.EnemyEngagement);
        short remote = (short)c.Host.Mission.Sectors.First(s => s.Id != c.Host.StartSectorId).Id;
        c.Connection.World.Apply(new ViewClusterReply { ClusterId = remote });
        Assert.Equal(remote, c.Connection.World.ViewedClusterId);
        Set(c.Game, "_cameraMode", CameraMode.Cockpit);
        int enemyId = c.Host.CreateDrone("attacker", 4920, 412, 1, Ship.PilotTypeWingman);
        c.Host.PlayerShip.ReceiveDamage(0, 1e10f, c.Host.Mission.LastUpdate,
            c.Host.PlayerShip.Position, c.Host.PlayerShip.Position, c.Host.Drones[enemyId]);
        c.Session.Advance(); Assert.True(Get<bool>(c.Game, "_trainingDeathCameraActive"));
        c.Mission.RestoreShip();
        Assert.Null(c.Connection.World.ViewedClusterId);
        Assert.Equal(CameraMode.Cockpit, Get<CameraMode>(c.Game, "_cameraMode"));
        Assert.Equal(1f, c.Host.PlayerShip.Fraction);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Other_ship_death_reaches_lesson_once_from_local_simulation_hook(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style);
        int victim = c.Host.CreateDrone("victim", 4921, 412, 1, Ship.PilotTypeWingman);
        c.Host.Drones[victim].ReceiveDamage(0, 1e10f, c.Host.Mission.LastUpdate,
            c.Host.Drones[victim].Position, c.Host.Drones[victim].Position, c.Host.CommanderShip);
        Assert.Equal(0, c.Mission.GetKillCount()); // main-thread delivery
        c.Session.Advance(); Assert.Equal(1, c.Mission.GetKillCount());
        c.Session.Advance(); Assert.Equal(1, c.Mission.GetKillCount());
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Trainee_mine_drop_preserves_launcher_side_in_real_simulation(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style, TrainingHost.PracticeArena);
        Assert.True(c.Host.Mission.Core.TryBuildExpendableLauncher(59, out var part));
        c.Host.PlayerShip.MountPart(part, 5, 0, ExpandedPartData.FullyMounted, freeSpawn: true);
        c.Host.Paused = false;
        c.Connection.SendFireExpendable(EquipmentType.Dispenser);
        Assert.True(SpinWait.SpinUntil(() => c.Host.Mission.GetMines().Count > 0, 5000));
        c.Host.Paused = true;
        Assert.All(c.Host.Mission.GetMines(), mine => Assert.Equal(c.Host.PlayerShip.SideId, mine.SideId));
    }

    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Training_local_drone_default_orders_resolve_attack_defend_and_buoy_goto(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style, TrainingHost.PracticeArena);
        int drone = c.Host.CreateDrone("wing", 4910, 412, 0, Ship.PilotTypeWingman);
        int enemy = c.Host.CreateDrone("enemy", 4911, 412, 1, Ship.PilotTypeWingman);
        c.Site.CreateWaypoint(4912, new(1000, 0, 0), c.Host.StartSectorId, 0, true);
        foreach (var (type, id, expected) in new[]
        {
            (IgcObjectType.Ship, enemy, OrderChange.CidAttack),
            (IgcObjectType.Ship, c.Host.PlayerShipId, OrderChange.CidDefend),
            (IgcObjectType.Buoy, 4912, OrderChange.CidGoto),
        })
        {
            c.Connection.SendChat("order", ChatTarget.Individual, (short)drone, commandId: OrderChange.CidDefault,
                oidTarget: (short)id, otTarget: (short)type);
            Assert.Equal(expected, c.Host.Drones[drone].CurrentCommandId);
            Assert.Equal(expected, c.Host.Drones[drone].AcceptedShipCommandId);
            Assert.True(c.Site.ConsumeChat((int)ChatTarget.Individual));
        }
        var other = new ChatMessage { OidRecipient = (short)enemy, CommandID = OrderChange.CidDefault,
            OtTarget = (short)IgcObjectType.Ship, OidTarget = (short)drone };
        Assert.False(c.Host.HandleTrainingDroneChat(other)); // no enemy orders
    }
    [SkippableTheory, Trait("Requires", "Artwork"), Trait("Tier", "Live")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Local_drone_policy_handles_miner_builder_layer_group_and_imported_buoy(HudStyle style)
    {
        Require.Artwork(); using var c = new Client(style, TrainingHost.PracticeArena);
        var rocks = c.Host.Mission.GetAsteroids().ToArray();
        var mineRock = rocks.First(r => r.CanMineHe3);
        int minerId = c.Host.CreateDrone("miner", 4940, 412, 0, Ship.PilotTypeMiner);
        void Chat(int recipient, IgcObjectType type, short target, short expected, ChatTarget channel = ChatTarget.Individual)
        {
            c.Connection.SendChat("order", channel, (short)recipient, commandId: OrderChange.CidDefault,
                oidTarget: target, otTarget: (short)type);
            Assert.Equal(expected, c.Host.Drones[recipient].CurrentCommandId);
            Assert.Equal(expected, c.Host.Drones[recipient].AcceptedShipCommandId);
        }
        Chat(minerId, IgcObjectType.Asteroid, mineRock.Data.AsteroidId, OrderChange.CidMine);
        Chat(minerId, IgcObjectType.Asteroid, mineRock.Data.AsteroidId, OrderChange.CidGoto, ChatTarget.Group);
        int echoed = c.Connection.World.Events.Chats.Count;
        Chat(minerId, IgcObjectType.Asteroid, mineRock.Data.AsteroidId, OrderChange.CidGoto, ChatTarget.GroupNoEcho);
        Assert.Equal(echoed, c.Connection.World.Events.Chats.Count);
        Assert.False(c.Site.ConsumeChat((int)ChatTarget.GroupNoEcho));
        var buildType = c.Host.Mission.Core.StationTypes.Values.First(t => t.AabmBuild != 0 && rocks.Any(r => r.Data.HasCapability(t.AabmBuild)));
        var buildRock = rocks.First(r => r.Data.HasCapability(buildType.AabmBuild));
        int builderId = c.Host.CreateDrone("builder", 4941, 412, 0, Ship.PilotTypeBuilder);
        c.Host.Drones[builderId].BuilderStationType = buildType;
        Chat(builderId, IgcObjectType.Asteroid, buildRock.Data.AsteroidId, OrderChange.CidBuild);
        int layerId = c.Host.CreateDrone("layer", 4942, 412, 0, Ship.PilotTypeLayer);
        var buoy = new DataBuoyIGC([100, 200, 300], c.Host.StartSectorId, 0, 4943, true);
        c.Connection.SendChatBuoy("build", ChatTarget.Individual, (short)layerId, buoy, commandId: OrderChange.CidDefault);
        Assert.Equal(OrderChange.CidBuild, c.Host.Drones[layerId].CurrentCommandId);
        Assert.Equal(OrderChange.CidBuild, c.Host.Drones[layerId].AcceptedShipCommandId);
        Assert.NotNull(c.Host.Mission.GetBuoy(buoy.BuoyId));
    }

}
