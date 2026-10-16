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
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public sealed class P655GameStateIntegrityTests
{
    private sealed class World : IDisposable
    {
        public readonly Srv Server = new();
        public readonly ServerHost Host = new();
        public readonly Mission Mission;
        public readonly Ship Ship, Mate, Enemy;
        public readonly ConnectionId Conn = new(1);
        public readonly List<(ConnectionId Conn, byte[] Message)> Output = [];
        public Sector Sector => Ship.Sector!;
        public Srv.LiveShip Live => Server.Ships.Single(l => l.Ship == Ship);
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
            Set("_config", new ServerConfig { LiveSettings = new LiveSettingsConfig() });
            Set("_missions", new List<Mission> { Mission });
            var sector = Mission.Sectors[0];
            var hull = core.HullTypes.Values.First(h => h.GroupId >= 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod)) with { Speed = 180, Thrust = 1000 };
            Ship = new Ship(100, hull, new Vector3(5000, 5000, 5000), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            Mate = new Ship(101, hull, new Vector3(6000, 5000, 5000), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            Enemy = new Ship(102, hull, Ship.Position + Ship.Orientation.GetForward() * 100, Vector3.Zero, Orientation.Identity) { SideId = 1 };
            foreach (var s in new[] { Ship, Mate, Enemy }) Mission.PlaceShip(s, sector);
            Server.TestSeedPeers([(Conn, Ship, sector), (new ConnectionId(2), Mate, sector), (new ConnectionId(3), Enemy, sector)]);
            Mission.Sides[0].LeaderShipId = Ship.Id;
            Server._testSendCapture = (c, pkt) => Output.AddRange(FedPacket.Unpack(pkt).Select(m => (c, m.ToArray())));
            Now(10);
        }
        public void Set(string name, object value) => typeof(Srv).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Server, value);
        public object? Call(string name, params object?[] args)
        {
            try { return typeof(Srv).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Server, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }
        public void Now(double now) { Set("_simNow", now); Ship.InitClock(now); Mate.InitClock(now); Enemy.InitClock(now); }
        public void Send(IFedMessage msg) => Call("ProcessDatagram", Conn, FedPacket.Pack([msg.Encode()]));
        public byte Cookie { get { var pu = Call("EnsurePeerUpdate", Conn)!; return (byte)pu.GetType().GetField("Cookie")!.GetValue(pu)!; } }
        public double Scale => (double)typeof(Srv).GetField("_timeScale", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Server)!;
        public ShipUpdate Pose(uint time, Vector3 p, Vector3? v = null, int state = 0) => new() { Time = time, Cookie = Cookie,
            Payload = ClientShipUpdate.FromPose(time, p, v ?? Vector3.Zero, Orientation.Identity, state, ControlData.Neutral, new float[3], 1) };
        public void Dispose() { Host.Transport!.Shutdown(); Server.Dispose(); }
    }

    [SkippableFact]
    public void H11_Packet_teleport_chain_and_speed_hack_fail_normal_pose_works()
    {
        using var w = new World();
        var start = w.Ship.Position;
        var chain = Enumerable.Range(1, 255).Select(i => w.Pose(10000, start + new Vector3(i * 19.5f, 0, 0)).Encode()).ToArray();
        w.Call("ProcessDatagram", w.Conn, FedPacket.Pack(chain));
        Assert.InRange((w.Ship.Position - start).Length(), 0, 20);
        w.Now(10.05);
        w.Send(w.Pose(10050, w.Ship.Position, new Vector3(1000, 0, 0)));
        Assert.InRange(w.Ship.Velocity.Length(), 0, 200);
        Assert.Contains(w.Output, o => FedMessageHeader.Read(o.Message).Fmid == ShipReset.Fmid);
        w.Now(10.1);
        var good = w.Ship.Position + new Vector3(1, 0, 0);
        w.Send(w.Pose(10100, good, new Vector3(10, 0, 0)));
        Assert.InRange((w.Ship.Position - good).Length(), 0, .01f);
        Assert.InRange(w.Ship.Velocity.Length(), 9, 11);
    }

    [SkippableFact]
    public void H11_Increasing_timestamps_cannot_walk_the_latency_tolerance_each_tick()
    {
        using var w = new World();
        var start = w.Ship.Position;
        for (int i = 0; i < 100; i++)
        {
            w.Now(10 + i * .05);
            w.Send(w.Pose((uint)(10000 + i * 50), w.Ship.Position + new Vector3(19.5f, 0, 0)));
        }
        Assert.InRange((w.Ship.Position - start).Length(), 0, w.Ship.MovementSpeedLimit() * 4.95f + 20); // 900m physical travel + bounded latency credit
    }

    [SkippableFact]
    public void H11_Legitimate_motion_after_several_seconds_of_packet_loss_is_accepted()
    {
        using var w = new World();
        w.Send(w.Pose(10000, w.Ship.Position));
        // The authoritative sim continued while packets were lost.
        w.Ship.Position += new Vector3(600, 0, 0);
        w.Now(14);
        w.Send(w.Pose(14000, w.Ship.Position, new Vector3(150, 0, 0)));
        Assert.DoesNotContain(w.Output, o => FedMessageHeader.Read(o.Message).Fmid == ShipReset.Fmid);
    }

    [SkippableFact]
    public void H11_Corrected_client_converges_after_server_moves_beyond_old_travel_anchor()
    {
        using var w = new World();
        w.Send(w.Pose(10000, w.Ship.Position));
        // While poses are rejected/lost, the authoritative ship keeps flying.
        // Its reset pose can be farther from the last accepted pose than the
        // bounded idle credit. A client applying that reset must recover.
        w.Ship.Position += new Vector3(1000, 0, 0);
        w.Now(20);
        w.Send(w.Pose(20000, w.Ship.Position + new Vector3(500, 0, 0)));
        Assert.Single(w.Output, o => FedMessageHeader.Read(o.Message).Fmid == ShipReset.Fmid);
        w.Output.Clear();
        w.Now(20.15);
        w.Send(w.Pose(20150, w.Ship.Position + new Vector3(1.8f, 0, 0), new Vector3(10, 0, 0)));
        Assert.Empty(w.Output);
        Assert.InRange(w.Ship.Velocity.Length(), 9, 11);
    }

    [SkippableFact]
    public void H11_Repeated_corrections_do_not_refill_client_travel_credit()
    {
        using var w = new World();
        var start = w.Ship.Position;
        for (int i = 0; i < 200; i++)
        {
            w.Now(10 + i * .01);
            // Alternate an accepted small step and a rejected teleport. Cookie
            // renewal must not mint another native 20m of distance each time.
            w.Send(w.Pose((uint)(10000 + i * 10), w.Ship.Position + new Vector3(i % 2 == 0 ? 19.5f : 500, 0, 0)));
        }
        Assert.InRange((w.Ship.Position - start).Length(), 0, w.Ship.MovementSpeedLimit() * 1.99f + 20);
        Assert.Contains(w.Output, o => FedMessageHeader.Read(o.Message).Fmid == ShipReset.Fmid);
    }

    [SkippableFact]
    public void H11_Afterburner_speed_limit_matches_side_scaled_flight_thrust()
    {
        using var w = new World();
        var factors = Enumerable.Repeat(1f, GlobalAttributeSet.Count).ToArray();
        factors[GlobalAttributeSet.Thrust] = 2;
        w.Mission.Sides[0].GlobalAttributes = new GlobalAttributeSet(factors);
        w.Ship.ChangeHull(w.Ship.HullType with { MaxFuel = 100 });
        w.Ship.SetFuel(100);
        var part = w.Mission.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Afterburner) with
        {
            PartMask = 0,
            Type = new DataPartTypeIGC.Afterburner(0, w.Ship.HullType.Thrust, 100, 100, 0, 0),
        };
        Assert.NotNull(w.Ship.MountPart(part, 1, 0, 255));
        w.Ship.StateM = ShipControlState.AfterburnerButton;
        // Native scales both booster and main-engine thrust by c_gaThrust;
        // their ratio stays one, so this legal boost is near twice cruise.
        float speed = w.Ship.EffectiveMaxSpeed() * 1.9f;
        w.Send(w.Pose(10000, w.Ship.Position, new Vector3(speed, 0, 0), ShipControlState.AfterburnerButton));
        Assert.Empty(w.Output);
        Assert.InRange(w.Ship.Velocity.Length(), speed - 1, speed + 1);
    }

    [SkippableFact]
    public void H11_First_pose_after_reposition_tolerates_the_native_clock_window()
    {
        using var w = new World();
        // First launch/warp sample can lag the new authoritative pose within
        // native's accepted clock window; catch-up must not exhaust initial credit.
        w.Send(w.Pose(9000, w.Ship.Position, new Vector3(150, 0, 0)));
        Assert.DoesNotContain(w.Output, o => FedMessageHeader.Read(o.Message).Fmid == ShipReset.Fmid);
    }

    [SkippableFact]
    public void H11_Fitted_afterburner_speed_and_coasting_are_legal_excess_speed_is_corrected()
    {
        using var w = new World();
        w.Ship.ChangeHull(w.Ship.HullType with { MaxFuel = 100 });
        w.Ship.SetFuel(100);
        float cruise = w.Ship.MovementSpeedLimit();
        var part = w.Mission.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Afterburner) with
        {
            PartMask = 0,
            Type = new DataPartTypeIGC.Afterburner(1, w.Ship.EffectiveThrust(), 1, 1, 0, 0),
        };
        Assert.NotNull(w.Ship.MountPart(part, 1, 0, 255));
        w.Ship.StateM = ShipControlState.AfterburnerButton;
        float boosted = w.Ship.MovementSpeedLimit();
        Assert.True(boosted > cruise);
        float speed = (cruise + boosted) / 2;
        w.Send(w.Pose(10000, w.Ship.Position, new Vector3(speed, 0, 0), ShipControlState.AfterburnerButton));
        Assert.True(w.Ship.Velocity.Length() > cruise);
        Assert.Empty(w.Output);
        w.Now(10.05);
        w.Send(w.Pose(10050, w.Ship.Position, new Vector3(boosted + 100, 0, 0), ShipControlState.AfterburnerButton));
        Assert.InRange(w.Ship.Velocity.Length(), cruise, boosted);
        Assert.Single(w.Output, o => FedMessageHeader.Read(o.Message).Fmid == ShipReset.Fmid);
        w.Output.Clear();
        w.Ship.SetFuel(0);
        w.Ship.StateM = 0;
        Assert.Equal(cruise, w.Ship.MovementSpeedLimit());
        w.Now(10.1);
        w.Send(w.Pose(10100, w.Ship.Position, w.Ship.Velocity)); // existing boost momentum is legal
        Assert.Empty(w.Output);
    }

    [SkippableFact]
    public void H11_Inactive_afterburner_does_not_authorize_boost_velocity()
    {
        using var w = new World();
        w.Ship.ChangeHull(w.Ship.HullType with { MaxFuel = 100 });
        w.Ship.SetFuel(100);
        float cruise = w.Ship.MovementSpeedLimit();
        var part = w.Mission.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Afterburner) with
        {
            PartMask = 0,
            Type = new DataPartTypeIGC.Afterburner(1, w.Ship.EffectiveThrust(), 1, 1, 0, 0),
        };
        Assert.NotNull(w.Ship.MountPart(part, 1, 0, 255));
        w.Send(w.Pose(10000, w.Ship.Position, new Vector3(cruise * 1.5f, 0, 0)));
        Assert.Equal(Vector3.Zero, w.Ship.Velocity);
        Assert.Contains(w.Output, o => FedMessageHeader.Read(o.Message).Fmid == ShipReset.Fmid);
    }

    [SkippableFact]
    public void H11_Delayed_coasting_snapshot_is_checked_at_server_time()
    {
        using var w = new World();
        var reportedPosition = w.Ship.Position;
        var reportedVelocity = new Vector3(450, 0, 0); // authorized launch/warp momentum above cruise
        var nowPosition = reportedPosition;
        var nowVelocity = reportedVelocity;
        w.Ship.CalculateShipPose(1, ref nowPosition, ref nowVelocity, Orientation.Identity, new float[3]);
        w.Ship.Position = nowPosition;
        w.Ship.Velocity = nowVelocity;
        Assert.True(nowVelocity.Length() < reportedVelocity.Length());
        w.Send(w.Pose(9000, reportedPosition, reportedVelocity));
        Assert.DoesNotContain(w.Output, o => FedMessageHeader.Read(o.Message).Fmid == ShipReset.Fmid);
        Assert.InRange(w.Ship.Velocity.Length(), nowVelocity.Length() - 1, nowVelocity.Length() + .01f);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void H11_Coasting_rounding_cannot_ratchet_authorized_momentum(bool diagonal)
    {
        using var w = new World();
        var direction = diagonal ? new Vector3(1, 1, 1).Normalize() : new Vector3(1, 0, 0);
        w.Ship.Velocity = direction * 300; // authorized launch momentum
        for (int i = 0; i < 20; i++)
        {
            w.Now(10 + i * .05);
            w.Send(w.Pose((uint)(10000 + i * 50), w.Ship.Position,
                direction * (w.Ship.Velocity.Length() + .25f)));
        }
        Assert.Empty(w.Output); // tolerate the compact wire rounding
        Assert.InRange(w.Ship.Velocity.Length(), 299.99f, 300.001f); // never amplify its allowance
    }

    [SkippableFact]
    public void H12_Docked_controls_and_tick_cannot_move_or_fire_launch_restores_motion()
    {
        using var w = new World();
        w.Ship.StationId = 1;
        var start = w.Ship.Position;
        w.Send(new ShipUpdate { Payload = ClientShipUpdate.FromControls(0, ShipControlState.WeaponsMask, new ControlData(0, 0, 0, 1)) });
        Assert.Equal(ControlData.Neutral, w.Ship.Controls);
        w.Ship.Controls = new ControlData(0, 0, 0, 1);
        w.Ship.StateM = ShipControlState.WeaponsMask;
        w.Ship.Velocity = new Vector3(100, 0, 0);
        w.Ship.Update(10.05);
        Assert.Equal(start, w.Ship.Position);
        w.Ship.StationId = null;
        w.Send(new ShipUpdate { Payload = ClientShipUpdate.FromControls(0, 0, new ControlData(0, 0, 0, 1)) });
        w.Ship.Update(10.1);
        Assert.True((w.Ship.Position - start).Length() > 0);
    }

    [SkippableFact]
    public void H12_Docked_missile_and_mine_requests_preserve_ammo_flying_requests_fire()
    {
        using var w = new World();
        w.Ship.ChangeHull(w.Ship.HullType with { CapacityMagazine = 1000, CapacityDispenser = 1000 });
        var launchers = w.Mission.Core.Launchers.Keys.Select(id =>
            w.Mission.Core.TryBuildExpendableLauncher(id, out var p) ? (DataPartTypeIGC?)p : null).Where(p => p.HasValue).Select(p => p!.Value).ToList();
        var missilePart = launchers.First(p => p!.Type is DataPartTypeIGC.MissileLauncher)!;
        var minePart = launchers.First(p => p!.Type is DataPartTypeIGC.MineLauncher)!;
        var missileId = ((DataPartTypeIGC.MissileLauncher)missilePart.Type).ExpendableTypeId;
        ((Dictionary<short, DataMissileTypeIGC>)w.Mission.Core.MissileTypes)[missileId] = w.Mission.Core.MissileTypes[missileId] with { MaxLock = 0 };
        w.Ship.MountPart(missilePart, 2, 0, 255);
        w.Ship.MountPart(minePart, 2, 0, 255);
        short MissileAmmo() => w.Ship.Parts.Single(p => p.EquipmentType == EquipmentType.Magazine).Amount;
        short MineAmmo() => w.Ship.Parts.Single(p => p.EquipmentType == EquipmentType.Dispenser).Amount;
        var missiles = MissileAmmo(); var mines = MineAmmo();
        w.Ship.StationId = 1;
        w.Send(new FireMissile());
        w.Send(new FireExpendableRequest { Et = EquipmentType.Dispenser });
        Assert.Equal(missiles, MissileAmmo()); Assert.Equal(mines, MineAmmo());
        Assert.Empty(w.Sector.Models.OfType<Missile>()); Assert.Empty(w.Sector.Models.OfType<Mine>());
        w.Ship.StationId = null;
        w.Send(new FireMissile());
        w.Send(new FireExpendableRequest { Et = EquipmentType.Dispenser });
        Assert.Equal(missiles - 1, MissileAmmo()); Assert.Equal(mines - 1, MineAmmo());
        Assert.Single(w.Sector.Models.OfType<Missile>()); Assert.Single(w.Sector.Models.OfType<Mine>());
    }

    [SkippableFact]
    public void H13_Started_defection_fails_lobby_switch_succeeds()
    {
        using var w = new World();
        w.Mission.Sides[1].AutoAccept = true;
        w.Send(new PositionReq { SideId = 1 });
        Assert.Equal((short)0, w.Ship.SideId);
        Assert.Contains(w.Output, o => FedMessageHeader.Read(o.Message).Fmid == DelPositionReq.Fmid && DelPositionReq.Decode(o.Message).Reason == DelPositionReqReason.NoDefections);
        w.Mission.Stage = MissionStage.NotStarted;
        w.Send(new PositionReq { SideId = 1 });
        Assert.Equal((short)1, w.Ship.SideId);
    }

    [SkippableFact]
    public void H13_Initial_selection_enforces_capacity_and_started_team_balance()
    {
        using var w = new World();
        w.Set("_config", new ServerConfig { Games = [new GameConfig { MaxPlayersPerTeam = 2, MaxTeamImbalance = 1 }] });
        w.Mission.Stage = MissionStage.NotStarted;
        Assert.Equal(DelPositionReqReason.TeamFull, w.Call("CheckPositionRequest", w.Mission, (short)0, null));
        Assert.Null(w.Call("CheckPositionRequest", w.Mission, (short)1, null));
        w.Set("_config", new ServerConfig { Games = [new GameConfig { MaxPlayersPerTeam = 10, MaxTeamImbalance = 1 }] });
        w.Mission.Stage = MissionStage.Started;
        Assert.Equal(DelPositionReqReason.TeamBalance, w.Call("CheckPositionRequest", w.Mission, (short)0, null));
        Assert.Null(w.Call("CheckPositionRequest", w.Mission, (short)1, null));
        w.Mission.Sides[1].Active = false;
        Assert.Equal(DelPositionReqReason.SideGone, w.Call("CheckPositionRequest", w.Mission, (short)1, null));
    }

    [SkippableFact]
    public void H13_Authenticated_harness_bypasses_native_balance_but_not_capacity()
    {
        using var w = new World();
        w.Set("_config", new ServerConfig { LiveSettings = new LiveSettingsConfig { TestHarness = new TestHarnessConfig { Enabled = true, Secret = "bot-secret", CharacterNamePrefix = "seed-" } }, Games = [new GameConfig { MaxPlayersPerTeam = 10, MaxTeamImbalance = 1 }] });
        Assert.Equal(DelPositionReqReason.TeamBalance, w.Call("CheckInitialPositionRequest", w.Conn, "seed-1", w.Mission, (short)0));
        w.Call("AuthenticatePrivileges", w.Conn, new LogonRequest { CDKey = "bot-secret" });
        Assert.Null(w.Call("CheckInitialPositionRequest", w.Conn, "seed-1", w.Mission, (short)0));
        Assert.Equal(DelPositionReqReason.TeamBalance, w.Call("CheckInitialPositionRequest", new ConnectionId(20), "seed-attacker", w.Mission, (short)0));
    }

    [SkippableFact]
    public void H13_Pending_approval_cannot_defect_after_start_but_lobby_approval_works()
    {
        using var w = new World();
        w.Mission.Sides[1].LeaderShipId = w.Enemy.Id;
        w.Mission.Stage = MissionStage.NotStarted;
        w.Send(new PositionReq { SideId = 1 });
        void Approve() => w.Call("ProcessDatagram", new ConnectionId(3), FedPacket.Pack([new PositionAck { ShipId = w.Ship.Id, SideId = 1, FAccepted = true }.Encode()]));
        w.Mission.Stage = MissionStage.Started;
        Approve();
        Assert.Equal((short)0, w.Ship.SideId);
        w.Mission.Stage = MissionStage.NotStarted;
        w.Send(new PositionReq { SideId = 1 });
        Approve();
        Assert.Equal((short)1, w.Ship.SideId);
    }

    [SkippableFact]
    public void H16_Configured_harness_bearer_is_required_even_for_an_MCP_name()
    {
        using var w = new World();
        w.Server.Config.LiveSettings!.TestHarness = new TestHarnessConfig { Enabled = true, Secret = "harness-secret", CharacterNamePrefix = "seed-", Money = 1000000 };
        Assert.Null(w.Call("HarnessGrantFor", w.Conn));
        w.Call("AuthenticatePrivileges", w.Conn, new LogonRequest { CharacterName = "seed-1", CDKey = "wrong" });
        Assert.Null(w.Call("HarnessGrantFor", w.Conn));
        w.Call("AuthenticatePrivileges", w.Conn, new LogonRequest { CharacterName = "seed-1", CDKey = "harness-secret" });
        Assert.Same(w.Server.Config.LiveSettings.TestHarness, w.Call("HarnessGrantFor", w.Conn));
    }

    [SkippableFact]
    public void H11_Server_reposition_rebases_travel_budget_without_a_correction()
    {
        using var w = new World();
        w.Send(w.Pose(10000, w.Ship.Position + new Vector3(5, 0, 0)));
        w.Ship.Position += new Vector3(10000, 0, 0);
        var pu = w.Call("EnsurePeerUpdate", w.Conn)!;
        w.Call("NextCookie", pu, true); // same rebase used by warp, launch and respawn SET_CLUSTER
        w.Now(10.05);
        w.Send(w.Pose(10050, w.Ship.Position));
        Assert.DoesNotContain(w.Output, o => FedMessageHeader.Read(o.Message).Fmid == ShipReset.Fmid);
        Assert.Equal(2L, pu.GetType().GetField("Accepted")!.GetValue(pu));
    }

    [SkippableFact]
    public void H16_Spoofed_names_do_not_grant_spectator_cheats_or_harness_money()
    {
        using var w = new World();
        w.Server.Config.LiveSettings!.AdminCheats = new AdminCheatsConfig { Enabled = true, AllowNames = ["seed-*"], Secret = "p655-secret" };
        w.Server.Config.LiveSettings.TestHarness = new TestHarnessConfig { Enabled = true, CharacterNamePrefix = "seed-", Money = 1000000 };
        Assert.False((bool)w.Call("IsSpectatorAdmin", w.Conn)!);
        Assert.Null(w.Call("HarnessGrantFor", w.Conn));
        var p = w.Ship.Position;
        w.Send(new ChatMessage { Message = $"relocate {w.Ship.Id} {w.Sector.Id} 1 2 3" });
        Assert.Equal(p, w.Ship.Position);
        w.Send(new ChatMessage { Message = "p655-secret timescale 0.5" });
        Assert.True((bool)w.Call("IsSpectatorAdmin", w.Conn)!); // secret-bearing command authenticates passive viewing
        Assert.Equal(.5, w.Scale);
    }

    [SkippableTheory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e999")]
    public void H17_Nonfinite_cheat_values_fail_valid_values_work(string value)
    {
        using var w = new World();
        w.Call("HandleTimescaleCheat", w.Conn, w.Ship, value);
        Assert.Equal(1, w.Scale);
        Assert.True(double.IsFinite(LiveSettingsConfig.ClampTimeScale(double.NaN)));
        var p = w.Ship.Position;
        w.Call("HandleRelocateCheat", w.Conn, w.Ship, $"100 {w.Sector.Id} {value} 2 3");
        Assert.Equal(p, w.Ship.Position);
        Assert.False(w.Server.RelocateShipToSector(w.Ship, w.Sector, p, Vector3.Zero));
        Assert.True(w.Server.RelocateShipToSector(w.Ship, w.Sector, new Vector3(100, 200, 300), new Vector3(0, 0, -1)));
        w.Call("HandleTimescaleCheat", w.Conn, w.Ship, "0.5");
        Assert.Equal(.5, w.Scale);
    }

    [SkippableFact]
    public void H25_Teammate_target_overwrite_and_hidden_lock_fail_visible_self_target_works()
    {
        using var w = new World();
        w.Mate.CommandTarget = w.Enemy;
        w.Send(new OrderChange { ShipID = w.Mate.Id, Command = OrderChange.CmdCurrent, ObjectID = -1 });
        Assert.Same(w.Enemy, w.Mate.CommandTarget);
        w.Mate.AcceptedShipTarget = w.Enemy;
        w.Mate.AcceptedShipCommandId = OrderChange.CidGoto;
        w.Send(new OrderChange { ShipID = w.Mate.Id, Command = OrderChange.CmdAccepted, ObjectID = -1 });
        Assert.Same(w.Enemy, w.Mate.AcceptedShipTarget);
        Assert.Equal(OrderChange.CidGoto, w.Mate.AcceptedShipCommandId);
        w.Send(new OrderChange { ShipID = w.Ship.Id, Command = OrderChange.CmdCurrent, ObjectID = w.Enemy.Id, ObjectType = IgcObjectType.Ship });
        // Native permits retaining/selecting a command target without current
        // detection (FedSrv/FedSrv.CPP:7335-7339; Igc/shipIGC.h:725-790).
        // The weapon lock, rather than the order slot, requires CanSee.
        Assert.Same(w.Enemy, w.Ship.CommandTarget);
        w.Ship.ChangeHull(w.Ship.HullType with { CapacityMagazine = 1000 });
        var launcher = w.Mission.Core.Launchers.Keys.Select(id =>
            w.Mission.Core.TryBuildExpendableLauncher(id, out var p) ? (DataPartTypeIGC?)p : null)
            .First(p => p?.Type is DataPartTypeIGC.MissileLauncher ml && w.Mission.Core.MissileTypes[ml.ExpendableTypeId].MaxLock > 0)!.Value;
        var missileId = ((DataPartTypeIGC.MissileLauncher)launcher.Type).ExpendableTypeId;
        var missile = w.Mission.Core.MissileTypes[missileId] with { LockAngle = 1, LockTime = 1, InitialSpeed = 1000, Lifespan = 10 };
        ((Dictionary<short, DataMissileTypeIGC>)w.Mission.Core.MissileTypes)[missileId] = missile;
        w.Ship.MountPart(launcher, 2, 0, 255);
        short Ammo() => w.Ship.Parts.Single(p => p.EquipmentType == EquipmentType.Magazine).Amount;
        // P651 resolves an uninitialized visibility entry from real scanners.
        // Establish the hidden state explicitly before testing its refusal.
        w.Enemy.SideVisibility.SetSideVisibility(0, false);
        w.Ship.UpdateMissileLock(missile, .5);
        Assert.Equal(0, w.Ship.MissileLock);
        w.Send(new FireMissile());
        Assert.Equal(2, Ammo());
        Assert.Empty(w.Sector.Models.OfType<Missile>());
        w.Enemy.SideVisibility.SetSideVisibility(0, true);
        w.Send(new OrderChange { ShipID = w.Ship.Id, Command = OrderChange.CmdCurrent, ObjectID = w.Enemy.Id, ObjectType = IgcObjectType.Ship });
        Assert.Same(w.Enemy, w.Ship.CommandTarget);
        w.Ship.UpdateMissileLock(missile, 10);
        Assert.Equal(1, w.Ship.MissileLock);
        w.Enemy.SideVisibility.SetSideVisibility(0, false);
        w.Send(new FireMissile()); // detection lost after acquiring a full lock
        Assert.Equal(2, Ammo());
        Assert.Empty(w.Sector.Models.OfType<Missile>());
        w.Ship.UpdateMissileLock(missile, .5);
        Assert.Equal(0, w.Ship.MissileLock);
        w.Enemy.SideVisibility.SetSideVisibility(0, true);
        w.Ship.UpdateMissileLock(missile, 10);
        w.Send(new FireMissile());
        Assert.Equal(1, Ammo());
        Assert.Single(w.Sector.Models.OfType<Missile>());
    }

    [SkippableFact]
    public void H25_Relayed_order_accepts_and_selects_specific_target_before_detection()
    {
        using var w = new World();
        Assert.False(w.Enemy.SeenBySide(0));
        // A teammate issues a real order via the chat packet/relay, exactly as
        // the MCP bot receives it. The recipient need not detect the target yet.
        w.Call("ProcessDatagram", new ConnectionId(2), FedPacket.Pack([new ChatMessage
        {
            ChatTarget = (int)ChatTarget.Individual,
            OidRecipient = w.Ship.Id,
            CommandID = OrderChange.CidGoto,
            OtTarget = (short)IgcObjectType.Ship,
            OidTarget = w.Enemy.Id,
            Message = "Go to my target",
        }.Encode()]));
        Assert.Equal(OrderChange.CidGoto, w.Ship.QueuedCommandId);
        Assert.Same(w.Enemy, w.Ship.QueuedShipTarget);
        Assert.Contains(w.Output, o => o.Conn == w.Conn && FedMessageHeader.Read(o.Message).Fmid == QueuedCommandUpdate.Fmid);

        // MCP sends CmdAccepted once. Native SetTarget/SetAccepted also write
        // these slots without a visibility gate (WinTrek/WinTrek.cpp:10682-10683).
        w.Send(new OrderChange { ShipID = w.Ship.Id, Command = OrderChange.CmdAccepted,
            CommandId = OrderChange.CidGoto, ObjectType = IgcObjectType.Ship, ObjectID = w.Enemy.Id });
        Assert.Equal(OrderChange.CidGoto, w.Ship.AcceptedShipCommandId);
        Assert.Same(w.Enemy, w.Ship.AcceptedShipTarget);
        w.Send(new OrderChange { ShipID = w.Ship.Id, Command = OrderChange.CmdCurrent,
            CommandId = OrderChange.CidGoto, ObjectType = IgcObjectType.Ship, ObjectID = w.Enemy.Id });
        Assert.Same(w.Enemy, w.Ship.CommandTarget);
        Assert.False(w.Enemy.SeenBySide(0));

        // The port's explicit queued-accept signal still promotes and clears
        // authoritatively; native clears its client-local queue after acceptance.
        w.Send(new OrderChange { ShipID = w.Ship.Id, Command = OrderChange.CmdQueued, CommandId = OrderChange.CidGoto });
        Assert.Equal(OrderChange.CidNone, w.Ship.QueuedCommandId);
        Assert.Null(w.Ship.QueuedShipTarget);
        Assert.Same(w.Enemy, w.Ship.AcceptedShipTarget);
    }

    [SkippableFact]
    public void H29_Pregame_and_nonfinite_buoys_fail_valid_started_buoy_relays()
    {
        using var w = new World();
        var buoy = new ChatBuoy { Cd = new ChatData { ChatTarget = (int)ChatTarget.Team }, Db = new DataBuoyIGC([1, 2, 3], w.Sector.Id, 0, 1, true), Message = "go" };
        w.Mission.Stage = MissionStage.Starting;
        w.Send(buoy);
        Assert.Empty(w.Output);
        w.Mission.Stage = MissionStage.Started;
        buoy.Db = new DataBuoyIGC([float.NaN, 2, 3], w.Sector.Id, 0, 1, true);
        w.Send(buoy);
        Assert.Empty(w.Output);
        buoy.Db = new DataBuoyIGC([1, 2, 3], w.Sector.Id, 0, 1, true);
        w.Send(buoy);
        Assert.Contains(w.Output, o => FedMessageHeader.Read(o.Message).Fmid == ChatBuoy.Fmid);
    }

    [SkippableFact]
    public void H31_Turret_milliseconds_work_replay_and_docked_parent_fail()
    {
        using var w = new World();
        w.Ship.ParentShip = w.Mate;
        w.Ship.TurretId = 0;
        var atu = new ActiveTurretUpdate { TimeUpdate = 10000, Atu = ClientActiveTurretUpdate.From(10000, new ControlData(.5f, 0, 0, 0), Orientation.Identity) };
        w.Send(atu);
        Assert.Equal(ShipControlState.WeaponsMask, w.Ship.StateM);
        w.Ship.StateM = 0;
        w.Send(atu);
        Assert.Equal(0, w.Ship.StateM);
        w.Mate.StationId = 1;
        atu.Atu = ClientActiveTurretUpdate.From(10001, new ControlData(.5f, 0, 0, 0), Orientation.Identity);
        w.Send(atu);
        Assert.Equal(0, w.Ship.StateM);
    }

    [SkippableFact]
    public void H31_Verbose_turret_flood_has_bounded_logs_and_still_applies_valid_input()
    {
        using var w = new World();
        w.Ship.ParentShip = w.Mate;
        w.Ship.TurretId = 0;
        bool previous = ConsoleLog.Verbose;
        ConsoleLog.Verbose = true;
        try
        {
            long before = Interlocked.Read(ref ConsoleLog.TotalWrites);
            for (uint time = 10000; time < 10205; time++)
                w.Send(new ActiveTurretUpdate { TimeUpdate = time, Atu = ClientActiveTurretUpdate.From(time, ControlData.Neutral, Orientation.Identity) });
            long writes = Interlocked.Read(ref ConsoleLog.TotalWrites) - before;
            Assert.InRange(writes, 1, 8);
            Assert.Equal(ShipControlState.WeaponsMask, w.Ship.StateM);
            var pu = w.Call("EnsurePeerUpdate", w.Conn)!;
            Assert.Equal(10204u, pu.GetType().GetField("LastClientTime")!.GetValue(pu));
        }
        finally { ConsoleLog.Verbose = previous; }
    }

    [SkippableFact]
    public void H33_Sector_and_side_broadcasts_stay_in_the_senders_mission()
    {
        using var w = new World();
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
        var second = Mission.Assemble(w.Mission.Core, map, [new SideSpec(0, "Other", 1), new SideSpec(1, "Other enemy", 3)], w.Host);
        var sector = second.Sectors.Single(s => s.Id == w.Sector.Id);
        var other = new Ship(200, w.Ship.HullType, w.Ship.Position, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        second.PlaceShip(other, sector);
        w.Set("_missions", new List<Mission> { w.Mission, second });
        w.Server.TestSeedPeers([(w.Conn, w.Ship, w.Sector), (new ConnectionId(2), w.Mate, w.Sector), (new ConnectionId(4), other, sector)]);
        w.Call("SendSectorWide", w.Sector, new InactiveTurretUpdate().Encode());
        Assert.Contains(w.Output, o => o.Conn == new ConnectionId(2));
        Assert.DoesNotContain(w.Output, o => o.Conn == new ConnectionId(4));
        w.Output.Clear();
        w.Call("BroadcastMoneyChangeSideWide", w.Mission, (short)0, 10, w.Ship.Id);
        Assert.Contains(w.Output, o => o.Conn == new ConnectionId(2));
        Assert.DoesNotContain(w.Output, o => o.Conn == new ConnectionId(4));
    }

    [SkippableFact]
    public void H36_Leader_rejection_without_pending_request_is_native_and_nonleader_fails()
    {
        using var w = new World();
        var req = new DelPositionReq { ShipId = w.Enemy.Id, SideId = 0, Reason = DelPositionReqReason.Rejected };
        w.Mission.Sides[0].LeaderShipId = w.Mate.Id;
        w.Send(req);
        Assert.Empty(w.Output);
        w.Mission.Sides[0].LeaderShipId = w.Ship.Id;
        w.Send(req);
        Assert.Contains(w.Output, o => FedMessageHeader.Read(o.Message).Fmid == DelPositionReq.Fmid);
    }

    [SkippableFact]
    public void H38_Teammate_may_command_same_side_NPC_but_not_enemy_NPC()
    {
        using var w = new World();
        w.Mission.Sides[0].LeaderShipId = w.Mate.Id;
        var mate = new Ship(110, w.Mate.HullType, w.Mate.Position, Vector3.Zero, Orientation.Identity) { IsNpc = true, SideId = 0, StationId = 1 };
        var enemy = new Ship(111, w.Enemy.HullType, w.Enemy.Position, Vector3.Zero, Orientation.Identity) { IsNpc = true, SideId = 1, StationId = 1 };
        w.Mission.PlaceShip(mate, w.Sector); w.Mission.PlaceShip(enemy, w.Sector);
        w.Send(new OrderChange { ShipID = enemy.Id, CommandId = OrderChange.CidDoNothing });
        Assert.False(enemy.StayDocked);
        w.Send(new OrderChange { ShipID = mate.Id, CommandId = OrderChange.CidDoNothing });
        Assert.True(mate.StayDocked);
    }
}
