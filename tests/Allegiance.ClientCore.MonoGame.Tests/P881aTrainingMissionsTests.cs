using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.Training;
using Allegiance.ClientCore.Flight;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework.Input;
using static Allegiance.ClientCore.Training.TrainingMissionIds;
using Coll = Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P881a — Basic Flight and Basic Weaponry through the REAL production path: a real embedded
/// <see cref="TrainingHost"/> (server + authored map), an ordinary loopback
/// <see cref="ConnectionSession"/> join with ordinary replication, the production
/// <see cref="ClientTrainingSite"/> over the replicated world, the production
/// <see cref="TrainingSession"/> pump, and real wire inputs (ORDER_CHANGE target picks, the real
/// training key gate, control updates). No host-side outcome injection: derelicts die from real
/// projectiles, the target is the server's real <c>Ship.CommandTarget</c>, and the lessons end by
/// the server docking the trainee.
/// </summary>
[Collection("Training")]
public sealed class P881aTrainingMissionsTests : IDisposable
{
    public P881aTrainingMissionsTests() => TrainingClock.ResetForTests();

    public void Dispose()
    {
        TrainingClock.ResetForTests();
        TrainingController.EndMission();
        TrainingOwnState.Reset();
    }

    /// <summary>One real loopback trainee attached to a fresh embedded training host.</summary>
    private sealed class Trainee : IDisposable
    {
        public readonly TrainingHost Host;
        public readonly ConnectionSession Connection = new();
        public readonly ClientTrainingSite Site;
        public readonly TrainingSession Session;
        public readonly TrainingMission Mission;
        public readonly ActionMap Map = new(InputActionCatalog.Definitions);
        public readonly TrainingInputGate Gate;
        public bool Eyeball;
        public Coll.Vector3? BayApproach;
        public Coll.Vector3 BayEntry;
        public bool BayAligned;
        private TargetRef? _scriptedAutopilotTarget;

        public Trainee(TrainingHost.TrainingVariant variant, int missionId)
        {
            Host = TrainingHost.Start(variant);
            Site = new ClientTrainingSite(new ClientTrainingFacilities
            {
                World = () => Connection.World,
                Session = Connection,
                Host = Host,
                SetEyeball = shown => Eyeball = shown,
                OwnAutopilot = variant == TrainingHost.EnemyEngagement
                    ? (target, on) => _scriptedAutopilotTarget = on ? target : null : null,
                ClockSeconds = () => Host.Mission.LastUpdate,
                // The client's real own-ship kinematics: the replicated own pose supplies the
                // attitude and the training host's interceptor supplies the hull stats. In the
                // shipped client these come from _ownSim (Flight.OwnShipSimulator.TurnHull).
                OwnOrientation = () => Connection.World.MyShipId is { } me
                    && Connection.World.Ships.TryGetValue(me, out var mine) && mine.Latest is { } p
                        ? (ToNum(p.Orientation.GetForward()), ToNum(p.Orientation.GetUp()), ToNum(p.Orientation.GetRight()))
                        : null,
                OwnHullKinematics = () =>
                {
                    var ship = Host.PlayerShip;
                    return new TrainingTurnTo.TurnHull(
                        Mass: ship.Mass,
                        TorqueMultiplier: ShipKinematics.GetTorqueMultiplier(ship.Velocity, ship.EffectiveMaxSpeed()),
                        TurnTorqueYaw: ship.EffectiveTurnTorque(ControlData.AxisYaw),
                        TurnTorquePitch: ship.EffectiveTurnTorque(ControlData.AxisPitch),
                        MaxTurnRateYaw: ship.EffectiveMaxTurnRate(ControlData.AxisYaw),
                        MaxTurnRatePitch: ship.EffectiveMaxTurnRate(ControlData.AxisPitch),
                        CurrentTurnRateYaw: ship.TurnRates[ControlData.AxisYaw],
                        CurrentTurnRatePitch: ship.TurnRates[ControlData.AxisPitch]);
                },
            });
            Mission = Game1.CreateTrainingMission(missionId, Site);
            Session = new TrainingSession(Site, Mission);
            Gate = new TrainingInputGate(Session.RecordKeyPress);
            Map.ActionFilter = Gate.Approves;
            Host.StartBackgroundTicker();
            Connection.Connect(Host.Port, "p881a-trainee");
            Connection.SendLogonRequest(new LogonRequest
            {
                CharacterName = "p881a-trainee", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL",
                FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1,
                SteamAuthTicket = new byte[16], DrmHash = "p881a",
            });
            Assert.True(Connection.WaitForAck(15000).FValidated, "training logon did not validate");
            Connection.SendPositionReq(0);
            var watch = Stopwatch.StartNew();
            while (!Site.HasPlayerShip && watch.Elapsed < TimeSpan.FromSeconds(20)) Thread.Sleep(5);
            Assert.True(Site.HasPlayerShip, "trainee pose never replicated in space");
            // The production Game1 pump starts the script once the own ship has a replicated pose.
            TrainingController.BeginMission();
            Session.Start();
        }

        public ClientWorld World => Connection.World;
        public short MyId => World.MyShipId!.Value;

        public ServerShipUpdate MyPose => World.Ships[MyId].Latest!;

        public void Press(Keys key)
        {
            Map.SetBinding("FireBooster", new(key, InputModifiers.None), true);
            Gate.Update(new(key), Map);
            Map.SetBinding("FireBooster", new(Keys.Space, InputModifiers.None), true);
            Gate.Update(default, Map);
        }

        /// <summary>Press a real translated action (the training input gate forwards its native
        /// TrekKey to the mission, exactly like the live flight path).</summary>
        public void PressAction(string action, Keys key)
        {
            Map.SetBinding(action, new(key, InputModifiers.None), true);
            Gate.Update(new(key), Map);
            Gate.Update(default, Map);
        }

        /// <summary>Send a real controls update through the wire (the same message
        /// Game1.FlightInput sends), after the production training masks.</summary>
        public void Controls(ControlData controls, int stateM = 0)
        {
            var masked = TrainingController.ApproveControls(controls);
            int maskedState = TrainingController.ApproveActions(stateM);
            // Game1 applies scripted autopilot AFTER the lesson's manual-input mask.
            if (_scriptedAutopilotTarget is { } ap && Site.Resolve(ap) is { } point)
            {
                var pose = MyPose;
                masked = AutopilotSteering.Pursue(pose.Position, pose.Orientation,
                    new Coll.Vector3(point.Position.X, point.Position.Y, point.Position.Z));
            }
            TrainingOwnState.Set(masked, maskedState);
            Connection.CommandControls(masked, maskedState);
        }

        /// <summary>Real ORDER_CHANGE target selection (native SetTarget).</summary>
        public void Target(ObjectType type, int id, short commandId = OrderChange.CidNone)
        {
            var ot = type switch
            {
                ObjectType.Ship => IgcObjectType.Ship,
                ObjectType.Station => IgcObjectType.Station,
                ObjectType.Asteroid => IgcObjectType.Asteroid,
                ObjectType.Probe => IgcObjectType.Probe,
                ObjectType.Buoy => IgcObjectType.Buoy,
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };
            Connection.SendOrderChange(MyId, commandId, OrderChange.CmdCurrent, ot, (short)id);
        }

        /// <summary>One production frame: send controls, pump replication/mission, return true when complete.</summary>
        public bool Advance(ControlData controls, int stateM = 0)
        {
            Controls(controls, stateM);
            Thread.Sleep(2);
            return Session.Advance();
        }

        public IReadOnlyList<string> Messages => Site.Messages;

        public int Count(string fragment) => Messages.Count(m => m.Contains(fragment, StringComparison.Ordinal));

        public void Dispose()
        {
            Map.ActionFilter = null;
            Connection.Dispose();
            Host.Dispose();
        }
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Mission2_completes_in_a_real_loopback_client_by_flying_and_docking()
    {
        Require.Artwork();
        using var t = new Trainee(TrainingHost.BasicFlight, Tm2BasicFlight);

        // Goal 2's pointing demo is flown by TurnToAction through the production TurnToFace
        // binding; the scripted turn needs the own hull kinematics (checked separately). The
        // trainee keeps the nose on the commander until the script releases the controls.
        var deadline = Stopwatch.StartNew();
        float throttle = 0f;
        bool booster = false;
        int spacebarSeen = 0, backslashSeen = 0, wheelSeen = 0, spaceKeySeen = 0;
        while (!t.Session.Finished && deadline.Elapsed < TimeSpan.FromSeconds(240))
        {
            var pose = t.MyPose;
            var controls = t.Mission.ActiveGoalPath.StartsWith("2", StringComparison.Ordinal)
                ? AutopilotSteering.Pursue(pose.Position, pose.Orientation, t.Host.CommanderShip.Position)
                : new ControlData(0f, 0f, 0f, throttle);
            if (t.Count("Press the SPACEBAR") > spacebarSeen) { spacebarSeen++; t.PressAction("FireBooster", Keys.Space); }
            if (t.Count("BACKSLASH") > backslashSeen) { backslashSeen++; throttle = 1f; }
            if (t.Count("scroll wheel") > wheelSeen) { wheelSeen++; throttle = 0.3f; }
            if (t.Count("SPACE key") > spaceKeySeen) { spaceKeySeen++; booster = true; }
            var stateM = booster ? AfterburnerButton : 0;
            t.Advance(controls, stateM);
        }

        Assert.True(t.Session.Finished, $"mission did not complete (goal {t.Mission.ActiveGoalPath})");
        Assert.True(t.Session.Completed, "mission should report completion after the real landing");
        // The landing was the server's real dock latch, not a script call.
        Assert.True(t.Host.PlayerShip.StationId is not null, "trainee never docked at the home station");
        Assert.Equal((short)1010, t.Host.PlayerShip.StationId);
        Assert.True(t.Messages.Any(m => m.Contains("BACKSLASH")), "throttle lesson never ran");
    }

    [Fact]
    public void Production_Game1_facilities_bind_turn_kinematics_orders_autopilot_camera_and_panes()
    {
        // The training script's TurnToAction (M2 goal 2, M3 goals 5/7/8), own-ship orders,
        // SetAutopilotAction, SetDisplayModeAction and ShowPaneAction must all reach real client
        // facilities. A missing wire-up silently degrades each to a logged no-op — this fails if
        // any of them regresses.
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        var session = new ConnectionSession();
        var sim = new Allegiance.ClientCore.Flight.OwnShipSimulator();
        Set(game, "_session", session);
        Set(game, "_ownSim", sim);
        var facilities = game.CreateTrainingFacilities(Tm2BasicFlight);
        Assert.Null(facilities.OwnHullKinematics!.Invoke());   // no hull resolved yet
        sim.SetHull(new Allegiance.ClientCore.Flight.ShipDeadReckoner.HullStats(
            Thrust: 50000f, MaxSpeed: 100f, Mass: 10000f, SideMultiplier: 1f, BackMultiplier: 1f,
            MaxTurnRateYaw: 2f, MaxTurnRatePitch: 2f, MaxTurnRateRoll: 2f,
            TurnTorqueYaw: 40000f, TurnTorquePitch: 40000f, TurnTorqueRoll: 40000f), 0f);
        Assert.NotNull(facilities.OwnHullKinematics!.Invoke());
        Assert.NotNull(facilities.OwnOrder);
        Assert.NotNull(facilities.OwnAutopilot);
        Assert.NotNull(facilities.SetCameraMode);
        Assert.NotNull(facilities.ShowPane);
        session.Dispose();
    }

    private static System.Numerics.Vector3 ToNum(Coll.Vector3 v) => new(v.X, v.Y, v.Z);

    private static void Set(Game1 game, string name, object value) =>
        typeof(Game1).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, value);

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Mission3_a_real_client_pick_writes_the_server_ship_command_target()
    {
        Require.Artwork();
        using var t = new Trainee(TrainingHost.BasicWeaponry, Tm3BasicWeaponry);

        // A real ORDER_CHANGE target pick writes the server ship's c_cmdCurrent slot — the state
        // GetTargetCondition/CurrentTarget read (gettargetcondition.cpp:52). The old host
        // dictionary would not see a client pick at all.
        t.Target(ObjectType.Ship, 1000);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5)
            && t.Host.GetCommandTarget(t.Host.PlayerShipId) is not { Type: ObjectType.Ship, Id: 1000 })
            Thread.Sleep(5);
        Assert.Equal(new TargetRef(ObjectType.Ship, 1000), t.Host.GetCommandTarget(t.Host.PlayerShipId));
        Assert.Same(t.Host.Drones[1000], t.Host.PlayerShip.CommandTarget);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Mission3_completes_in_a_real_loopback_client_with_real_guns_and_a_real_landing()
    {
        Require.Artwork();
        using var t = new Trainee(TrainingHost.BasicWeaponry, Tm3BasicWeaponry);
        Assert.True(t.Host.Drones.Count >= 10, "Mission 3 seeds ten real derelict hulls");

        // The pipeline the client uses: real prompt-driven input, real control sends, real
        // ORDER_CHANGE target picks, real projectiles and the server's real dock latch.
        int nextDerelict = 1000;
        int spacebarSeen = 0, spaceKeySeen = 0, centerSeen = 0, enemyKeySeen = 0, baseSeen = 0;
        bool killing = false, landing = false;
        var deadline = Stopwatch.StartNew();
        while (!t.Session.Finished && deadline.Elapsed < TimeSpan.FromSeconds(240))
        {
            string goal = t.Mission.ActiveGoalPath;
            int spacebar = t.Count("Press the SPACEBAR");
            int spaceKey = t.Count("Press the SPACE key");
            int center = t.Count("right mouse button to target");
            int enemyKey = t.Count("Target an enemy using the F key");
            int basePrompt = t.Count("target the outpost.");
            if (center > centerSeen) { centerSeen++; killing = true; t.Target(ObjectType.Ship, nextDerelict); }
            if (enemyKey > enemyKeySeen) { enemyKeySeen++; t.PressAction("TargetEnemy", Keys.F); }
            if (basePrompt > baseSeen) { baseSeen++; t.Target(ObjectType.Station, 1010); killing = false; landing = true; }
            if (spacebar > spacebarSeen) { spacebarSeen++; t.Press(Keys.Space); }
            // Goal 10's go-home SPACE. Native leaves this to the player (mission3.cpp:1090: "when you are
            // ready to go home") and a human steers round survivors. This headless shooting fixture
            // still clears all ten before its manual bay approach; the four real-window proofs
            // independently demonstrate landing with seven survivors (P892d).
            if (spaceKey > spaceKeySeen && nextDerelict > 1009) { spaceKeySeen++; t.Press(Keys.Space); }
            if (t.Count("Land in a green hangar") > 0) { killing = false; landing = true; }

            // After a target dies the real client radar re-targets the next object of the same
            // type (trekigc.cpp:2936-2966); the harness sends that same ORDER_CHANGE.
            if (killing && nextDerelict <= 1009 && t.Host.IsDestroyed(nextDerelict))
            {
                nextDerelict++;
                if (nextDerelict <= 1009) t.Target(ObjectType.Ship, nextDerelict);
            }

            // Fire from the weapon lesson onwards (native asks for a shot in goal 5, then keeps the
            // player firing through the kills, INCLUDING goals 10/11's derelict-destruction phase —
            // mission3.cpp:1069-1096: all controls enabled, "destroy them"); flying the earlier
            // lessons with the trigger held would empty the magazine before the first target dies.
            int major = int.TryParse(goal.Split('.')[0], out var m) ? m : 0;
            bool firing = killing && (goal == "5.9" || major is 6 or 8 or 9 or 10 or 11);
            ControlData controls;
            int stateM = 0;
            if (landing)
            {
                var home = t.Host.Mission.Sectors.SelectMany(s => s.StationRuntimes)
                    .Single(s => s.StationId == 1010);
                var bayCommand = ManualBayLanding(t, home);
                controls = bayCommand.Controls;
                stateM = bayCommand.StateM;
            }
            else
            {
                var pose = t.MyPose;
                Coll.Vector3 aim = t.Host.Drones.TryGetValue(nextDerelict, out var target)
                    ? target.Position
                    : new Coll.Vector3(t.Host.StationPosition.X, t.Host.StationPosition.Y, t.Host.StationPosition.Z);
                // The derelicts sit 400-580 m from the start point: the native lesson is aiming
                // practice from a near-stationary ship. Turn the nose onto the target and hold
                // position (kill throttle), exactly a player tracking a slow target.
                var pursue = AutopilotSteering.Pursue(pose.Position, pose.Orientation, aim);
                controls = new ControlData(pursue.Yaw, pursue.Pitch, 0f, -1f);
                // Fire in the native player's situation: nose on the target and inside gun range.
                var toTarget = aim - pose.Position;
                float range = toTarget.Length();
                float aimDot = range > 1e-3f ? pose.Orientation.GetForward() * (toTarget / range) : 0f;
                if (firing && range < 900f && aimDot > 0.99f) stateM = WeaponsMask | MissileFire;
            }
            t.Advance(controls, stateM);
        }

        Assert.True(t.Session.Finished, $"mission did not complete (goal {t.Mission.ActiveGoalPath})");
        Assert.True(t.Session.Completed, "mission should report completion");
        Assert.True(nextDerelict >= 1010, $"all ten derelicts should have died to real projectiles (next {nextDerelict})");
        Assert.True(t.Host.PlayerShip.StationId is not null, "trainee never docked at the outpost");
        Assert.True(spacebarSeen >= 2 && spaceKeySeen >= 2, "both SPACEBAR and SPACE-key acknowledgements fired");
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Mission4_completes_with_real_transit_guns_constructor_and_landing()
    {
        Require.Artwork();
        using var t = new Trainee(TrainingHost.EnemyEngagement, Tm4EnemyEngagement);
        Assert.False(t.Session.RecordKeyPress(KeyToggleAutoPilot)); // native command-view gate
        var commandKeys = new Allegiance.ClientCore.MonoGame.Flight.CommandActionInput();
        var clock = Stopwatch.StartNew();
        string last = "";
        int spaceSeen = 0;
        short? pickedMiner = null;
        double logAt = 0;
        bool chatted = false, cloaked = false, transited = false, scoutSeen = false;
        while (!t.Session.Finished && clock.Elapsed.TotalSeconds < 900)
        {
            string goal = t.Mission.ActiveGoalPath;
            if (goal != last)
            { Console.WriteLine($"[p892d] M4 {goal} sector={t.Host.PlayerShip.Sector?.Id} pos={t.Host.PlayerShip.Position}"); last = goal; }
            if (goal == "2.3" && !chatted)
            {
                t.Connection.SendChat("x", ChatTarget.Everyone);
                t.Mission.RecordChat(ChatEveryone); // actual send event, Game1.Session.cs SendChatLine
                chatted = true;
            }
            int space = t.Count("Press the SPACEBAR");
            if (space > spaceSeen) { spaceSeen = space; t.Press(Keys.Space); }
            if (clock.Elapsed.TotalSeconds > logAt)
            {
                logAt = clock.Elapsed.TotalSeconds + 5;
                var q = t.World.MyQueuedCommand;
                if (t.Host.Drones.TryGetValue(1105, out var constructor))
                    Console.WriteLine($"[p892d] constructor pos={constructor.Position} velocity={constructor.Velocity} fraction={constructor.Fraction} stationType={constructor.BuilderStationType?.StationTypeId} rock={constructor.BuilderAsteroidTarget?.Data.AsteroidId} rockPos={constructor.BuilderAsteroidTarget?.Data.Position[0]},{constructor.BuilderAsteroidTarget?.Data.Position[1]},{constructor.BuilderAsteroidTarget?.Data.Position[2]} building={constructor.IsBuilding} runaway={constructor.IsRunningAway} plan={constructor.PlanCommandId} goto={constructor.GotoWaypoint}");
                Console.WriteLine($"[p892d] state goal={goal} pos={t.Host.PlayerShip.Position} vel={t.Host.PlayerShip.Velocity} q={q} accepted={t.Host.PlayerShip.AcceptedShipCommandId} buoys={t.World.CommandBuoys.Count} legal={(q is { } queued && t.World.LegalQueuedCommand(queued))} ammo={t.Host.PlayerShip.Ammo} weapons={t.Host.PlayerShip.Weapons.Count} drones={string.Join(';', t.Host.Drones.Values.Select(d => $"{d.Id}:{d.Fraction:F2}@{d.Sector?.Id}"))}");
            }
            if (t.World.MyQueuedCommand is not null)
            {
                var key = new KeyboardState(Keys.OemOpenBrackets);
                t.Gate.Update(key, t.Map);
                var result = commandKeys.Step(key, false, t.Map, t.World);
                if (result.Request is { } req) t.Connection.SendOrderChange(req.ShipID, req.CommandId, req.Command, req.ObjectType, req.ObjectID);
                if (result.ChangeSelection && result.Selection is { } selected)
                {
                    t.World.SetCurrentCommand(selected);
                    t.Connection.SendOrderChange(t.MyId, selected.CommandId, OrderChange.CmdCurrent, selected.TargetType, selected.TargetId);
                }
                t.Gate.Update(default, t.Map);
                commandKeys.Step(default, false, t.Map, t.World);
            }
            if (t.Count("G key to activate") > 0 && !cloaked)
            { t.PressAction("ToggleCloak", Keys.G); cloaked = true; }
            var ship = t.Host.PlayerShip;
            if (ship.Sector?.Id == 1031) transited = true;
            scoutSeen |= t.Host.Drones.ContainsKey(1100);
            int state = 0;
            ControlData controls = ControlData.Neutral;
            Coll.Vector3? aim = null;
            bool fire = false;
            Ship? combatTarget = null;
            if (goal.StartsWith("3.") && t.Host.Waypoints.TryGetValue(700, out var wp))
                aim = new(wp.Position.X, wp.Position.Y, wp.Position.Z);
            if (goal.StartsWith("5.") && ship.Sector?.Warps.FirstOrDefault(w => w.Warp.WarpId == 1030) is { } warp)
                aim = warp.Position;
            if (goal.StartsWith("7."))
            {
                if (pickedMiner is null && t.Count("Target an enemy miner") > 0)
                {
                    t.PressAction("TargetEnemy", Keys.F);
                    var pick = t.Host.Drones.Values.Where(d => d.Id is >= 1101 and <= 1104 && !d.Dead && d.Sector == ship.Sector)
                        .OrderBy(d => (d.Position - ship.Position).LengthSquared()).FirstOrDefault();
                    if (pick is not null) { pickedMiner = pick.Id; t.Target(ObjectType.Ship, pick.Id); }
                }
                if (pickedMiner is { } id && t.Host.Drones.TryGetValue(id, out var miner) && miner.Sector == ship.Sector)
                { aim = miner.Position; combatTarget = miner; fire = goal == "7.7"; }
            }
            if (goal.StartsWith("8.") && t.Host.Drones.TryGetValue(1105, out var builder))
            {
                if (builder.Sector != ship.Sector && ship.Sector?.FindPathToward(builder.Sector!) is { } route) aim = route.Position;
                else aim = builder.Position;
                if (t.Host.Drones.TryGetValue(1100, out var scout) && !scout.Dead && scout.Sector == ship.Sector)
                { aim = scout.Position; combatTarget = scout; fire = goal == "8.7"; }
            }
            if (goal.StartsWith("10."))
            {
                var station = ship.Sector?.StationRuntimes.FirstOrDefault(st => st.SideId == ship.SideId);
                if (station is not null)
                {
                    var command = ManualBayLanding(t, station);
                    controls = command.Controls; state = command.StateM;
                }
            }
            else if (aim is { } point)
            {
                var pose = t.MyPose;
                float distance = (point - pose.Position).Length();
                var leadPoint = point;
                if (fire && ship.Weapons.FirstOrDefault() is { } weapon)
                {
                    var target = combatTarget;
                    if (target is not null && LeadSolver.SolveForLead(new LeadSolver.Input(
                        pose.Position, ship.Velocity, point, target.Velocity,
                        (ship.Mission?.WeaponMuzzles?.Position(ship.HullType, weapon.MountId) ?? Coll.Vector3.Zero) * pose.Orientation,
                        weapon.ProjectileType.Speed, weapon.ProjectileType.AbsoluteF, target.Radius), out var lead) < LeadSolver.NoSolutionTime)
                        leadPoint = pose.Position + lead * Math.Max(distance, 100);
                }
                var steer = AutopilotSteering.Pursue(pose.Position, pose.Orientation, leadPoint);
                controls = steer with { Throttle = fire && distance < 600 ? -1f : steer.Throttle };
                float cosine = distance > .001f ? pose.Orientation.GetForward() * ((leadPoint - pose.Position).Normalize()) : 1f;
                var keys = fire && cosine > .995f && distance < 1000 ? new KeyboardState(Keys.Space) : default;
                var input = Allegiance.ClientCore.MonoGame.Flight.FlightInputState.None with
                { Fire = t.Map.IsDownMostSpecific(keys, "FireWeapon"), GroupFire = true };
                state = Allegiance.ClientCore.MonoGame.Flight.FlightInput.Translate(input).StateM;
            }
            t.Advance(controls, state);
        }
        Console.WriteLine($"[p892d] M4 final={t.Mission.ActiveGoalPath} complete={t.Session.Completed} transit={transited} scout={scoutSeen} dock={t.Host.PlayerShip.StationId}");
        Assert.True(t.Session.Completed, $"M4 stalled at {t.Mission.ActiveGoalPath}; position={t.Host.PlayerShip.Position}; sector={t.Host.PlayerShip.Sector?.Id}");
        Assert.True(transited);
        Assert.True(cloaked);
        Assert.True(scoutSeen);
        Assert.True(t.Host.IsDestroyed(1100));
        Assert.True(t.Host.IsDestroyed(1105));
        Assert.NotNull(t.Host.PlayerShip.StationId);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Mission2_station_and_buoy_targets_and_autopilot_resolve_through_the_production_site()
    {
        Require.Artwork();
        using var t = new Trainee(TrainingHost.BasicFlight, Tm2BasicFlight);

        // Station 1010 resolves from the replicated EXPORT records (the M2 home-station target).
        var station = t.Site.Resolve(new TargetRef(ObjectType.Station, 1010));
        Assert.NotNull(station);
        Assert.Equal(1010, station!.SectorId);

        // The mission's CreateWaypointAction lands a real host waypoint the client resolves as a
        // buoy (mission2 goal 2's radar demo).
        t.Site.CreateWaypoint(950, new System.Numerics.Vector3(-200f, -200f, 400f), 1010, 0, true);
        var buoy = t.Site.Resolve(new TargetRef(ObjectType.Buoy, 950));
        Assert.NotNull(buoy);
        Assert.Equal(new System.Numerics.Vector3(-200f, -200f, 400f), buoy!.Position);

        // SetAutopilotAction writes the real ship latch the GetAutopilotCondition reads.
        Assert.False(t.Site.GetAutopilot(t.Site.PlayerShip.Ref));
        t.Site.SetAutopilot(t.Site.PlayerShip.Ref, true);
        Assert.True(t.Site.GetAutopilot(t.Site.PlayerShip.Ref));
        Assert.True(t.Host.PlayerShip.Autopilot);
        t.Site.SetAutopilot(t.Site.PlayerShip.Ref, false);
        Assert.False(t.Site.GetAutopilot(t.Site.PlayerShip.Ref));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")]
    public void Mission3_equipment_actions_mutate_the_real_ship()
    {
        Require.Artwork();
        using var t = new Trainee(TrainingHost.BasicWeaponry, Tm3BasicWeaponry);
        var ship = t.Host.PlayerShip;

        // ToggleWeapon selects the real firing mount (toggleweaponaction.cpp:41-47).
        t.Site.ToggleWeapon(1);
        Assert.Equal(1, ship.StateM & ShipControlState.SelectedWeaponMask);

        // Burn resources, then RepairRearm heals and refills the real pools and cargo
        // (repairrearmaction.cpp:37-74).
        ship.Ammo = 0;
        ship.Energy = 0;
        ship.SetFuel(0);
        t.Site.RepairAndRearm();
        Assert.Equal(ship.HullType.MaxAmmo, ship.Ammo);
        Assert.Equal(ship.EffectiveMaxEnergy(), ship.Energy, 3);
        Assert.Equal(ship.HullType.MaxFuel, ship.Fuel, 3);
        Assert.Contains(ship.CargoHold, p => p?.PartId == 150);
        Assert.Contains(ship.CargoHold, p => p?.PartId == 24);

        // SwapWeapon loads a cargo launcher into the mount through the real cargo-aware swap
        // (swapweaponaction.cpp:25-32 → Ship.TrySwapPartWithCargo).
        Assert.Contains(ship.CargoHold, p => p?.EquipmentType == EquipmentType.Magazine);
        t.Site.SwapWeapon((int)EquipmentType.Magazine, 0);
        Assert.Contains(ship.Parts, p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0);

        // DoDamageAction moves the real shield/hull gauges (dodamageaction.cpp:26-50).
        float shieldBefore = ship.ShieldFraction;
        t.Site.DealDamage(false);
        Assert.True(ship.ShieldFraction < shieldBefore || shieldBefore <= 0.2f);
    }

    [Fact]
    public void Mission3_target_enemy_condition_consumes_the_native_trek_key()
    {
        using var connection = new ConnectionSession();
        var site = new ClientTrainingSite(new ClientTrainingFacilities
        {
            World = () => connection.World,
            Session = connection,
        });
        var mission = new Mission3(site);
        var map = new ActionMap(InputActionCatalog.Definitions);
        var gate = new TrainingInputGate(mission.RecordKeyPress);
        map.ActionFilter = gate.Approves;
        map.SetBinding("TargetEnemy", new(Keys.F, InputModifiers.None), true);
        gate.Update(new(Keys.F), map);   // TargetEnemy binding = TK_TargetEnemy (80)
        Assert.True(site.ConsumeKey(TrainingMissionIds.KeyTargetEnemy));
        Assert.Equal(80, TrainingMissionIds.KeyTargetEnemy);
    }

    /// <summary>P885 — the Mission 4 equivalent: the production input gate routes the real cloak and
    /// target-enemy bindings to the mission's native TrekKey numbers (training/mission4.cpp:707,798
    /// gate on GetKeyCondition(TK_ToggleCloak=94, igc.h:317) and GetKeyCondition(TK_TargetEnemy=80,
    /// igc.h:303)).</summary>
    [Fact]
    public void Mission4_cloak_and_target_enemy_bindings_deliver_the_native_trek_keys()
    {
        using var connection = new ConnectionSession();
        var site = new ClientTrainingSite(new ClientTrainingFacilities
        {
            World = () => connection.World,
            Session = connection,
        });
        var mission = new Mission4(site);
        var map = new ActionMap(InputActionCatalog.Definitions);
        var gate = new TrainingInputGate(mission.RecordKeyPress);
        map.ActionFilter = gate.Approves;
        map.SetBinding("ToggleCloak", new(Keys.G, InputModifiers.None), true);
        map.SetBinding("TargetEnemy", new(Keys.F, InputModifiers.None), true);
        gate.Update(new(Keys.G), map);   // ToggleCloak binding = TK_ToggleCloak (94)
        gate.Update(new(Keys.F), map);   // TargetEnemy binding = TK_TargetEnemy (80)
        Assert.True(site.ConsumeKey(TrainingMissionIds.KeyToggleCloak));
        Assert.True(site.ConsumeKey(TrainingMissionIds.KeyTargetEnemy));
        Assert.Equal(94, TrainingMissionIds.KeyToggleCloak);
        Assert.Equal(80, TrainingMissionIds.KeyTargetEnemy);
        Assert.False(site.ConsumeKey(111) || site.ConsumeKey(112),
            "the retired invented ids must never be delivered");
    }

    [Fact]
    public void Training_camera_ids_map_to_the_port_cockpit_not_command()
    {
        // P886 regression: native cmCockpit=0 (Treki.h:108-126) but the port CameraMode enum has
        // Command=0 / Cockpit=5 (CameraMode.cs). The old raw cast `(CameraMode)nativeId` put every
        // training mission into Command view, whose CommandCameraActive path (FlightInput.cs:2431)
        // freezes flight controls — the M2/M3 throttle/fire lessons could never complete in a real
        // window. The mapping must translate native cmCockpit → Cockpit.
        var cockpit = Game1.TrainingCameraMode(TrainingMissionIds.CameraCockpit);
        Assert.Equal(Rendering.CameraMode.Cockpit, cockpit);
        Assert.NotEqual(Rendering.CameraMode.Command, cockpit);
        Assert.Equal(Rendering.CameraMode.ExternalOrbit, Game1.TrainingCameraMode(TrainingMissionIds.CameraExternalOrbit));
        Assert.Equal(Rendering.CameraMode.ExternalStation, Game1.TrainingCameraMode(TrainingMissionIds.CameraExternalStation));
    }

    [Fact]
    public void Spacebar_routes_TK_FireBooster_through_the_real_default_map()
    {
        // P886 regression: native binds SPACEBAR → TK_FireBooster (the key the M2/M3 "Press the
        // SPACEBAR" prompts wait on). The port rebinds Space→FireWeapon and afterburner→LeftShift for
        // normal flight, so a real-window training run stalled at the SPACEBAR gate because the filter
        // only honoured the (rebound) current binding and routed TK_FireWeapon (55) instead. The
        // training key filter must ALSO recognise the action's NATIVE-default key (the 5th catalog
        // arg), so SPACEBAR still reaches TK_FireBooster (50). This test uses the REAL default map —
        // no explicit rebind — so it fails before the fix and passes after.
        var map = new ActionMap(InputActionCatalog.Definitions);
        var received = new List<int>();
        var gate = new TrainingInputGate(key => { received.Add(key); return true; });
        map.ActionFilter = gate.Approves;
        var ks = new KeyboardState(Keys.Space);
        gate.Update(ks, map);
        gate.Update(default, map);   // release so the press re-edges
        Assert.Contains(KeyFireBooster, received);
    }

    [Fact]
    public void RemainderTargetKeys_honor_native_default_key_only_under_training()
    {
        // P892b — the B-key fix. The port's shipped default map (InputActionCatalog.Definitions)
        // leaves TargetFriendlyBase / TargetEnemy UNBOUND (U()) — plain F/B are rebound to
        // ToggleCloak / Scoreboard — so a real-window tap of the native prompt keys F/B never
        // reached SelectRemainderTarget → CommitTargetForPlayer (zero [p642] edges, P886c), and M3
        // goal 12's GetTargetCondition(OT_station,1010) could not advance. HandleRemainderTargetKeys
        // must ALSO honour each action's native-default key (defaultinputmap.mdl: TK_TargetFriendlyBase
        // = B, TK_TargetEnemy = F) while a training mission runs — mirroring TrainingInputGate — and
        // must NOT change normal flight (training=false stays binding-only).
        var map = new ActionMap(InputActionCatalog.Definitions);   // shipped default bindings
        // FAILS BEFORE THE FIX: with only the binding consulted, B is down but TargetFriendlyBase is
        // unbound → false. After the fix the native-default fallback (training=true) makes it true.
        Assert.True(Game1.RemainderTargetKeyDown(map, new KeyboardState(Keys.B), "TargetFriendlyBase", training: true));
        Assert.True(Game1.RemainderTargetKeyDown(map, new KeyboardState(Keys.F), "TargetEnemy", training: true));
        // Normal flight is untouched: the same keys without a training mission stay binding-only.
        Assert.False(Game1.RemainderTargetKeyDown(map, new KeyboardState(Keys.B), "TargetFriendlyBase", training: false));
        Assert.False(Game1.RemainderTargetKeyDown(map, new KeyboardState(Keys.F), "TargetEnemy", training: false));
        // A genuinely-bound action is recognised regardless of the training flag.
        Assert.True(Game1.RemainderTargetKeyDown(map, new KeyboardState(Keys.T), "Target", training: false));
        // The mission's key filter still wins: a key the lesson rejected must not select a target.
        map.ActionFilter = action => action != "TargetFriendlyBase";
        Assert.False(Game1.RemainderTargetKeyDown(map, new KeyboardState(Keys.B), "TargetFriendlyBase", training: true));
    }

    [Fact]
    public void Native_target_keys_do_not_fire_the_ports_action_on_the_same_key_under_training()
    {
        Assert.True(Game1.YieldsToTrainingTarget(new KeyboardState(Keys.B), "Scoreboard", training: true));
        Assert.True(Game1.YieldsToTrainingTarget(new KeyboardState(Keys.F), "ToggleCloak", training: true));
        Assert.False(Game1.YieldsToTrainingTarget(new KeyboardState(Keys.B), "Scoreboard", training: false));
        Assert.False(Game1.YieldsToTrainingTarget(new KeyboardState(Keys.F), "ToggleCloak", training: false));
    }

    [Fact]
    public void Training_locks_manual_controls_without_locking_scripted_autopilot()
    {
        TrainingController.BeginMission();
        Array.Fill(TrainingControlState.AllowableControlScalars, 0f);
        TrainingControlState.AllowableActionMask = 0;
        var manual = new Allegiance.ClientCore.MonoGame.Flight.FlightCommand(new(.7f, .8f, .3f, 1f), WeaponsMask);
        var automatic = new Allegiance.ClientCore.MonoGame.Flight.FlightCommand(new(.2f, -.3f, 0, 1f), ShipControlState.ForwardButton);
        var locked = Game1.ComposeTrainingFlight(manual, null);
        Assert.Equal(ControlData.Neutral, locked.Controls);
        Assert.Equal(0, locked.StateM);
        Assert.Equal(automatic, Game1.ComposeTrainingFlight(manual, automatic));
        Assert.Equal(manual.Controls, TrainingControlState.InputControls);
    }

    [Fact]
    public void Training_dead_ship_is_absent_and_waypoint_order_accepts_the_native_key()
    {
        using var connection = new ConnectionSession();
        var world = connection.World;
        var pose = ServerShipUpdate.FromValues(11, 0, default, 0, Coll.Vector3.Zero,
            Orientation.Identity, Coll.Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1);
        world.Apply(new SetCluster { SectorId = 1030, Ship = pose });
        world.Apply(new ExportShip { ShipId = 11, SideId = 0, CharacterName = "trainee" });
        var site = new ClientTrainingSite(new ClientTrainingFacilities { World = () => world, Session = connection });
        var target = new TargetRef(ObjectType.Ship, 11);
        Assert.NotNull(site.Resolve(target));
        world.Apply(ShipDelete.ForKilledShip(11));
        Assert.NotNull(world.Ships[11].Latest); // the retained receipt must not look like a live IGC model
        Assert.Null(site.Resolve(target));
        Assert.True(site.HasCommand(target, CmdGoto)); // native GetCommandCondition: absent => true

        world.Apply(new SetCluster { SectorId = 1030, Ship = pose });
        site.CreateWaypoint(700, new(800, 100, 0), 1030, 0, true);
        world.Apply(new QueuedCommandUpdate { CommandId = OrderChange.CidGoto,
            TargetType = IgcObjectType.Buoy, TargetId = 700 });
        var map = new ActionMap(InputActionCatalog.Definitions);
        TrainingController.BeginMission();
        var input = new Allegiance.ClientCore.MonoGame.Flight.CommandActionInput();
        var result = input.Step(new(Keys.OemOpenBrackets), false, map, world);
        Assert.Equal(OrderChange.CmdQueued, result.Request!.Command);
        Assert.Equal(700, world.MyAcceptedCommand!.Value.TargetId);
        Assert.Equal(Game1.TargetKind.Buoy, Game1.CommandSelection(world, result.Selection)!.Value.Kind);
        Assert.Null(world.MyQueuedCommand);
    }

    [Fact]
    public void Training_G_only_cloaks_and_P_only_toggles_autopilot()
    {
        Assert.True(Game1.YieldsToTrainingTarget(new(Keys.OemOpenBrackets), "PrevWeapon", true));
        Assert.False(Game1.YieldsToTrainingTarget(new(Keys.OemOpenBrackets), "PrevWeapon", false));
        Assert.False(TrainingInputGate.NativeDefaultKeyDown(new(Keys.B), "TargetFriendlyBasePrev"));
        Assert.True(TrainingInputGate.NativeDefaultKeyDown(new(Keys.LeftControl, Keys.B), "TargetFriendlyBasePrev"));
        Assert.False(TrainingInputGate.NativeDefaultKeyDown(new(Keys.LeftControl, Keys.B), "TargetFriendlyBase"));
        var map = new ActionMap(InputActionCatalog.Definitions);
        var recorded = new List<int>();
        var gate = new TrainingInputGate(k => { recorded.Add(k); return true; });
        map.ActionFilter = gate.Approves;
        gate.Update(new(Keys.Insert), map);
        Assert.Contains(40, recorded);
        Assert.DoesNotContain(46, recorded); // INSERT accepts; it must not also strafe up
        gate.Update(default, map);
        recorded.Clear();
        gate.Update(new(Keys.F), map);
        Assert.Contains(KeyTargetEnemy, recorded);
        Assert.DoesNotContain(KeyToggleCloak, recorded);
        gate.Update(default, map);
        recorded.Clear();
        gate.Update(new(Keys.G), map);
        Assert.True(Game1.CloakKeyDown(map, new(Keys.G), true));
        Assert.False(Game1.AutopilotKeyDown(map, new(Keys.G), true));
        Assert.DoesNotContain(DefaultInputMap.TkNumber("TK_ToggleAutoPilot")!.Value, recorded);
        gate.Update(default, map);
        gate.Update(new(Keys.P), map);
        Assert.True(Game1.AutopilotKeyDown(map, new(Keys.P), true));
        Assert.False(Game1.CloakKeyDown(map, new(Keys.P), true));
        Assert.Contains(DefaultInputMap.TkNumber("TK_ToggleAutoPilot")!.Value, recorded);
        Assert.True(Game1.AutopilotKeyDown(map, new(Keys.G), false));
        Assert.False(Game1.AutopilotKeyDown(map, new(Keys.P), false));
        map.ActionFilter = _ => false;
        Assert.False(Game1.AutopilotKeyDown(map, new(Keys.P), true));
    }

    [Fact]
    public void Cloak_engages_on_the_native_default_G_key_only_under_training()
    {
        // P892c — M4 cloak. Native's cloak is G (InputActionCatalog.cs:126, catalogue 5th arg), but
        // the port's shipped default map binds ToggleCloak to F, and F stands down as the TargetEnemy
        // key under training (YieldsToTrainingTarget). So before the fix a real G press recorded
        // TK_ToggleCloak for the mission but never ENGAGED the cloak. CloakKeyDown honours the
        // native-default key under training so G really cloaks, while F keeps standing down.
        var map = new ActionMap(InputActionCatalog.Definitions);   // shipped default: ToggleCloak→F, native default G
        // G actually cloaks under training (the M4 prompt key).
        Assert.True(Game1.CloakKeyDown(map, new KeyboardState(Keys.G), training: true));
        // F still stands down for TargetEnemy under training — it must NOT cloak.
        Assert.False(Game1.CloakKeyDown(map, new KeyboardState(Keys.F), training: true));
        // Normal flight is untouched: G is unbound (binding-only), F is the cloak binding.
        Assert.False(Game1.CloakKeyDown(map, new KeyboardState(Keys.G), training: false));
        Assert.True(Game1.CloakKeyDown(map, new KeyboardState(Keys.F), training: false));
        // A user who rebinds ToggleCloak to G gets a real cloak from G in both modes.
        var rebound = new ActionMap(InputActionCatalog.Definitions);
        rebound.SetBinding("ToggleCloak", new(Keys.G, InputModifiers.None), true);
        Assert.True(Game1.CloakKeyDown(rebound, new KeyboardState(Keys.G), training: true));
        Assert.True(Game1.CloakKeyDown(rebound, new KeyboardState(Keys.G), training: false));
        // The mission's key filter still wins: a rejected cloak key must not engage the cloak.
        map.ActionFilter = action => action != "ToggleCloak";
        Assert.False(Game1.CloakKeyDown(map, new KeyboardState(Keys.G), training: true));
    }

    // Headless controls cross the compact wire. Fly with digital keyboard inputs, as the
    // real-window driver does, so tiny analog pivot commands cannot round to zero at a bay.
    private static Allegiance.ClientCore.MonoGame.Flight.FlightCommand ManualBayLanding(Trainee t, Station station)
    {
        var pose = t.MyPose;
        var ship = t.Host.PlayerShip;
        var geometry = ship.Sector!.StationLandingBays!.Bay(station.Type)!;
        Assert.NotEmpty(geometry.LandSlots);
        if (t.BayApproach is null)
        {
            var bay = geometry.LandSlots.Select(slot => slot.Planes[0])
                .Select(plane => (Center: station.Center + plane.Position * station.Orientation,
                    Direction: plane.Direction.Normalize() * station.Orientation))
                .OrderBy(b => (b.Center + b.Direction * (station.Radius + 200) - pose.Position).LengthSquared()).First();
            t.BayApproach = bay.Center + bay.Direction * (station.Radius + 200);
            t.BayEntry = bay.Center - bay.Direction * 100;
            Console.WriteLine($"[p892d] manual bay approach={t.BayApproach} entry={t.BayEntry}");
        }
        if (!t.BayAligned && (t.BayApproach.Value - pose.Position).Length() < 35)
        { t.BayAligned = true; Console.WriteLine("[p892d] manual bay aligned; entering"); }
        var point = t.BayAligned ? t.BayEntry : t.BayApproach.Value;
        var delta = point - pose.Position;
        float distance = delta.Length();
        var direction = distance > .001f ? delta / distance : pose.Orientation.GetForward();
        float cosine = pose.Orientation.GetForward() * direction;
        var body = pose.Orientation.TimesInverse(Coll.Vector3.CrossProduct(pose.Orientation.GetForward(), direction));
        float speed = ship.Velocity.Length();
        float stopping = DockApproach.StoppingDistance(speed, ship.EffectiveThrust(), ship.Mass,
            ship.EffectiveMaxSpeed(), ship.HullType.BackMultiplier);
        var input = Allegiance.ClientCore.MonoGame.Flight.FlightInputState.None with
        {
            YawLeft = cosine < -.999f || body.Y > .015f, YawRight = cosine >= -.999f && body.Y < -.015f,
            PitchUp = -body.X > .015f, PitchDown = -body.X < -.015f,
            Throttle = !t.BayAligned && distance > Math.Max(400, stopping + 200) && cosine > .98f ? 1f : -1f,
            ThrottleForward = speed < 12 && cosine > .985f,
        };
        return Allegiance.ClientCore.MonoGame.Flight.FlightInput.Translate(input);
    }

}
