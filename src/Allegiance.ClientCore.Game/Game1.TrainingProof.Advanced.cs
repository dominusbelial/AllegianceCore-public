using Allegiance.ClientCore.Training;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;
using V = System.Numerics.Vector3;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private MouseState? _proofMouse;
    private V? _proofAimPoint;
    private (float Yaw, float Pitch) _proofMouseTurn;
    private bool _proofNeedsZoom;
    private string _proofClick = "";
    private long _proofClickAt;
    private long _proofClickCycle = -1;
    private int _proofClickX, _proofClickY;
    private MouseState ReadProofMouse() => _proofEnabled
        ? _proofMouse ?? new MouseState(GraphicsDevice.Viewport.Width / 2, GraphicsDevice.Viewport.Height / 2,
            0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released)
        : Mouse.GetState();

    private void ProofPressAction(string action, List<Keys> keys)
    {
        var binding = KeyOf(action);
        if (binding.Key == Keys.None) throw new InvalidOperationException($"Training proof requires a binding for {action}");
        keys.Add(binding.Key);
        if ((binding.Modifiers & InputModifiers.Shift) != 0) keys.Add(Keys.LeftShift);
        if ((binding.Modifiers & InputModifiers.Control) != 0) keys.Add(Keys.LeftControl);
        if ((binding.Modifiers & InputModifiers.Alt) != 0) keys.Add(Keys.LeftAlt);
    }

    private void ProofAimMouse(V target, List<Keys> keys, V? relativeVelocity = null)
    {
        _proofAimPoint = target;
        if (_minimapExpanded) { keys.Add(Keys.Tab); return; }
        if (!_mouseFlightEnabled) { keys.Add(Keys.M); return; }
        if (!TryOwnTrainingPose(out var position, out var forward, out var up, out var right)
            || _ownSim.TurnHull is not { } hull) return;
        // Fine aiming is ordinary mouse flight (effect/efimage.cpp:182-209),
        // including the shipping deadzone and response curve. Read the stick
        // to choose cursor deltas; never write controls or the ship's pose.
        (float Yaw, float Pitch) turn;
        if (relativeVelocity is { } motion)
            turn = TrainingProofPilot.Aim(target - position, motion, forward, up, right, _proofSampleSeconds, hull,
                correctionScale: _proofM4HuntMiner ? .5f : 1);
        else
        {
            var legacy = TrainingTurnTo.Compute(position, target, forward, up, right, MathF.Max(.1f, _trainingFrameDelta), hull);
            turn = (legacy.Yaw, legacy.Pitch);
        }
        float Stick(float control)
        {
            float response = _mouseFlight.LinearControls ? MathF.Abs(control) : MathF.Sqrt(MathF.Abs(control));
            return control == 0 ? 0 : MathF.CopySign(_mouseFlight.Deadzone + (1 - _mouseFlight.Deadzone) * response, control);
        }
        var current = _mouseFlight.Current;
        float pixels = _mouseFlight.Sensitivity * _mouseFlight.MouseSensitivity;
        int dx = (int)MathF.Round((current.Yaw - Stick(turn.Yaw)) / pixels);
        int dy = (int)MathF.Round((current.Pitch - Stick(turn.Pitch)) / pixels);
        if (_mouseFlight.InvertPitch) dy = -dy;
        var deflection = new V(current.Yaw - dx * pixels,
            current.Pitch - (_mouseFlight.InvertPitch ? -dy : dy) * pixels, 0);
        if (deflection.LengthSquared() > 1) deflection = V.Normalize(deflection);
        _proofMouseTurn = Flight.MouseFlightController.MapResponse(new(deflection.X, deflection.Y),
            _mouseFlight.Deadzone, _mouseFlight.LinearControls);
        _proofMouse = new MouseState(GraphicsDevice.Viewport.Width / 2 + dx, GraphicsDevice.Viewport.Height / 2 + dy,
            0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }

    private double _proofSampleAt;
    private float _proofSampleSeconds = .15f;
    private float _proofFrameSeconds;
    private readonly Allegiance.ClientCore.Flight.OwnShipSimulator _proofForecast = new();

    private void ProofConfigureForecast()
    {
        var ship = _embeddedHost!.PlayerShip;
        var stats = new Allegiance.ClientCore.Flight.ShipDeadReckoner.HullStats(ship.EffectiveThrust(), ship.EffectiveMaxSpeed(), ship.Mass,
            ship.HullType.SideMultiplier, ship.HullType.BackMultiplier,
            ship.EffectiveMaxTurnRate(0), ship.EffectiveMaxTurnRate(1), ship.EffectiveMaxTurnRate(2),
            ship.EffectiveTurnTorque(0), ship.EffectiveTurnTorque(1), ship.EffectiveTurnTorque(2));
        _proofForecast.SetHull(stats, 0);
    }

    private Allegiance.ClientCore.Flight.OwnShipSimulator ProofFiringPose(bool plannedTurn = false, List<Keys>? plannedKeys = null)
    {
        // Input is sampled before StepOwnShipSimulation advances this frame.
        // This separate predictor never moves the live trainee.
        ProofConfigureForecast();
        _proofForecast.Seed(_ownSim.Position, _ownSim.Velocity, _ownSim.Orientation, _ownSim.TurnRates);
        var controls = plannedTurn ? _ownSim.Controls with { Yaw = _proofMouseTurn.Yaw, Pitch = _proofMouseTurn.Pitch } : _ownSim.Controls;
        int state = _ownSim.StateM;
        if (plannedKeys is not null)
        {
            var keyboard = new KeyboardState(plannedKeys.ToArray());
            state &= ~(ShipControlState.MoveButtonsMask | ShipControlState.CoastButton);
            void Move(string action, int mask) { if (ActionDown(keyboard, action)) state |= mask; }
            Move("ThrustForward", ShipControlState.ForwardButton);
            Move("ThrustBackward", ShipControlState.BackwardButton);
            Move("ThrustLeft", ShipControlState.LeftButton);
            Move("ThrustRight", ShipControlState.RightButton);
            Move("ThrustUp", ShipControlState.UpButton);
            Move("ThrustDown", ShipControlState.DownButton);
            bool coast = _coastToggled;
            if (ActionDown(keyboard, "LockVector") && !_lockVectorKeyWasDown) coast = !coast;
            if (coast) state |= ShipControlState.CoastButton;
            controls = controls with { Throttle = -1 }; // hunter holds the ordinary stop-throttle key
        }
        _proofForecast.Step(_proofFrameSeconds, controls, state);
        return _proofForecast;
    }

    private float ProofShotAngleTo(V aimPoint, List<Keys>? plannedKeys = null)
    {
        var forward = ToSys(ProofFiringPose(plannedTurn: true, plannedKeys).Orientation.GetForward());
        var direction = V.Normalize(aimPoint - ToSys(_ownSim.Position));
        return MathF.Acos(Math.Clamp(V.Dot(forward, direction), -1, 1)) * 180 / MathF.PI;
    }
    private readonly Dictionary<string, (bool Down, double At)> _proofPulses = new();

    private bool ProofPulse(string action)
    {
        double now = _embeddedHost?.Mission.LastUpdate ?? _trainingClock;
        if (!_proofPulses.TryGetValue(action, out var pulse)) pulse = (false, double.NegativeInfinity);
        if (now - pulse.At >= .15) pulse = (!pulse.Down, now);
        _proofPulses[action] = pulse;
        return pulse.Down;
    }

    private void ProofMatchVelocity(V desired, List<Keys> keys, bool coast = true)
    {
        // Ordinary vector lock preserves the matched velocity between key
        // corrections. Stop throttle alone brakes on every neutral sample.
        if (_coastToggled != coast && ProofPulse("proof-vector-lock")) ProofPressAction("LockVector", keys);
        bool plannedCoast = _coastToggled;
        if (ActionDown(new KeyboardState(keys.ToArray()), "LockVector") && !_lockVectorKeyWasDown) plannedCoast = !plannedCoast;
        ProofConfigureForecast();
        int movement = TrainingProofPilot.ChooseThrust(_ownSim, _proofForecast, desired, _proofFrameSeconds, plannedCoast);
        void Press(int mask, string action)
        {
            if ((movement & mask) != 0) ProofPressAction(action, keys);
        }
        Press(ShipControlState.ForwardButton, "ThrustForward");
        Press(ShipControlState.BackwardButton, "ThrustBackward");
        Press(ShipControlState.RightButton, "ThrustRight");
        Press(ShipControlState.LeftButton, "ThrustLeft");
        Press(ShipControlState.UpButton, "ThrustUp");
        Press(ShipControlState.DownButton, "ThrustDown");
    }

    private void ProofClickAt(string name, int x, int y, bool right = false)
    {
        long now = Environment.TickCount64;
        if (_proofClick != name) { _proofClick = name; _proofClickAt = now; _proofClickCycle = -1; }
        int period = _trainingMissionId == 8 ? 200 : 600;
        int press = _trainingMissionId == 8 ? 60 : 250;
        long cycle = (now - _proofClickAt) / period;
        if (cycle != _proofClickCycle)
        {
            _proofClickCycle = cycle;
            _proofClickX = x; _proofClickY = y;
        }
        bool down = (now - _proofClickAt) % period < press;
        // A click keeps its press point through release. Tracking a moving
        // hull while holding the button would issue a rectangle selection.
        _proofMouse = new MouseState(_proofClickX, _proofClickY, 0, !right && down ? ButtonState.Pressed : ButtonState.Released,
            ButtonState.Released, right && down ? ButtonState.Pressed : ButtonState.Released,
            ButtonState.Released, ButtonState.Released);
    }

    private bool ProofClickPoint(string name, V position, bool right = false)
    {
        if (!TryOwnPose(out var pose, out var orientation, out _)
            || !TryPointerProjection(pose, orientation, out var project)
            || project(new(position.X, position.Y, position.Z)) is not { } p
            || _trainingMissionId == 8 && (MathF.Abs(p.Sx) > .9f || MathF.Abs(p.Sy) > .8f)) { _proofNeedsZoom = true; return false; }
        var vp = GraphicsDevice.Viewport;
        ProofClickAt(name, (int)((p.Sx + 1) * vp.Width / 2), (int)((1 - p.Sy) * vp.Height / 2), right);
        return true;
    }

    private void ProofDragPoint(string name, V position)
    {
        if (!TryOwnPose(out var pose, out var orientation, out _)
            || !TryPointerProjection(pose, orientation, out var project)
            || project(new(position.X, position.Y, position.Z)) is not { } p) { _proofNeedsZoom = true; return; }
        long now = Environment.TickCount64;
        if (_proofClick != name) { _proofClick = name; _proofClickAt = now; }
        long phase = (now - _proofClickAt) % 900;
        var vp = GraphicsDevice.Viewport;
        int x = (int)((p.Sx + 1) * vp.Width / 2);
        int y = (int)((1 - p.Sy) * vp.Height / 2);
        if (phase >= 250) y -= 40;
        _proofMouse = new MouseState(x, y, 0, ButtonState.Released, ButtonState.Released,
            phase < 500 ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released, ButtonState.Released);
    }

    private bool ProofClickModel(short id, bool right = false, TargetKind kind = TargetKind.Ship)
    {
        V position;
        lock (_session.Gate)
        {
            if (!ResolveTargetPosition(new(kind, id, "proof pick"), out var p)) return false;
            position = kind == TargetKind.Ship && _poses.TryGetValue(id, out var rendered) ? ToSys(rendered.Position) : ToSys(p);
        }
        return ProofClickPoint($"{kind}:{id}:{right}", position, right);
    }

    private void ProofClickVerb(short verb)
    {
        var vp = GraphicsDevice.Viewport;
        int column = verb - 1;
        int lo = column == 0 ? 0 : CommandBarLayout.VerbColumns[column - 1];
        int hi = CommandBarLayout.VerbColumns[column];
        ProofClickAt($"verb:{verb}", (int)CommandBarLayout.Left(vp.Width, 1) + CommandBarLayout.VerbsLeft + (lo + hi) / 2,
            (int)CommandBarLayout.Top(vp.Height, 1) + CommandBarLayout.VerbsTop + 20);
    }

    private void ProofClickSector(short sector)
    {
        lock (_session.Gate)
        {
            if (_session.World.Graph is not { } graph) return;
            var vp = GraphicsDevice.Viewport;
            var sites = graph.Clusters.Where(c => _session.World.Clusters.ContainsKey(c.Id))
                .Select(c => _session.World.Clusters[c.Id])
                .Select(c => new StarmapLayout.ClusterSite(c.ClusterId, c.Name, c.ScreenX, c.ScreenY)).ToArray();
            StarmapLayout layout;
            if (_minimapExpanded)
            {
                float w = MathF.Min(vp.Width * .8f, 1040), h = MathF.Min(vp.Height * .66f, 640);
                layout = StarmapLayout.Fit(sites, (vp.Width - w) / 2, (vp.Height - h) / 2, w, h, 26);
            }
            else layout = MinimapLayout.Build(sites, graph.Warps, CurrentOwnSector(), vp.Width, vp.Height,
                text => { var size = _hudSmall.MeasureString(text); return new(size.X, size.Y); }).Map;
            if (layout.TryGetPoint(sector, out var point)) ProofClickAt($"sector:{sector}", (int)point.X, (int)point.Y);
        }
    }

    private void ProofOrder(short recipient, short verb, short target, TargetKind kind)
    {
        if (_commandee?.Id != recipient || _commandGroup.Count != 0) { ProofClickModel(recipient); return; }
        if (_commandVerb != verb || LiveComposeState != NativeComposeState.Command) { ProofClickVerb(verb); return; }
        ProofClickModel(target, right: true, kind);
    }

    private int _proofM5SpaceSeen;
    private long _proofAdvancedStateAt;
    private void DriveMission5(ClientTrainingSite site, List<Keys> keys)
    {
        _proofMouse = null;
        string goal = _trainingSession!.Mission.ActiveGoalPath;
        if (Environment.TickCount64 >= _proofAdvancedStateAt)
        {
            _proofAdvancedStateAt = Environment.TickCount64 + 5000;
            Console.WriteLine($"[tproof] M5 state goal={goal} view={RenderSector(1050)} viewed={_session.World.ViewedClusterId} camera={_cameraMode} docked={_dockedActive} commandee={_commandee} verb={_commandVerb} composing={LiveComposeState} probes={_embeddedHost?.GetProbeCount()}");
            foreach (int layerId in new[] { 3007, 3008, 3009 })
                if (_embeddedHost!.Drones.TryGetValue(layerId, out var layer))
                    Console.WriteLine($"[tproof] layer {layerId} current={layer.CurrentCommandId} accepted={layer.AcceptedShipCommandId} target={layer.AcceptedShipTarget?.Id} position={layer.Position} plan={layer.LayerBuildOrder} expendable={layer.LayerLaidExpendableTypeId} dead={layer.Dead}");
        }
        int space = ProofCount(site, "Press the SPACEBAR");
        if (space > _proofM5SpaceSeen) { _proofM5SpaceSeen = space; ProofTap(Keys.Space); }
        var mission = (Mission5)_trainingSession.Mission;
        if (!int.TryParse(goal.Split('.')[0], out int major)) return;
        if (major == 1 && goal != "1.4")
        {
            int[] layers = [mission.Builder1Id, mission.Builder2Id, mission.Builder3Id];
            int id = layers.FirstOrDefault(id => _embeddedHost!.TrainingShipExists(id)
                && !_embeddedHost.HasCommand(id, TrainingMissionIds.CmdBuild));
            if (id == 0) return;
            if (_commandee?.Id != id) { ProofClickModel((short)id); return; }
            if (_commandVerb != OrderChange.CidBuild || LiveComposeState != NativeComposeState.Command)
            { ProofClickVerb(OrderChange.CidBuild); return; }
            var warp = site.Resolve(new(ObjectType.Warp, 1050));
            if (warp is not null)
            {
                if (id == mission.Builder1Id) ProofDragPoint($"deploy-height:{id}", warp.Position + new V(400, 500, 0));
                else ProofClickPoint($"deploy:{id}", warp.Position + new V(400, 500, 0), right: true);
            }
        }
        else if (major == 2)
        {
            if (RenderSector(1050) != 1051) { ProofClickSector(1051); return; }
            if (_minimapExpanded) { ProofTap(Keys.Tab); return; }
            int[] miners = [mission.Miner2Id, mission.Miner3Id, mission.Miner4Id];
            int id = miners.FirstOrDefault(id => !_embeddedHost!.HasCommand(id, TrainingMissionIds.CmdMine));
            if (id == 0) return;
            // Choose a visible rock as a player would. The authored cube is
            // random: its first He3 record can project beneath the minimap.
            // Keep clicks in the world viewport rather than clicking through
            // an interface panel or changing the simulation's order directly.
            if (!TryOwnPose(out var own, out var orientation, out _)
                || !TryPointerProjection(own, orientation, out var project)) return;
            var vp = GraphicsDevice.Viewport;
            float PickDistance(Allegiance.Core.Sim.Asteroid rock)
            {
                var position = rock.Data.Position;
                if (project(new(position[0], position[1], position[2])) is not { } p
                    || MathF.Abs(p.Sx) > .9f || MathF.Abs(p.Sy) > .8f) return float.PositiveInfinity;
                int x = (int)((p.Sx + 1) * vp.Width / 2), y = (int)((1 - p.Sy) * vp.Height / 2);
                if (y >= CommandBarLayout.Top(vp.Height, 1) - 8
                    || PointerOverStarmap(new MouseState(x, y, 0, ButtonState.Released,
                        ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released)))
                    return float.PositiveInfinity;
                return p.Sx * p.Sx + p.Sy * p.Sy;
            }
            var rock = _embeddedHost!.Mission.Sectors.First(s => s.Id == 1051).Mineable
                .Where(r => r.CanMineHe3).Select(r => (Rock: r, Distance: PickDistance(r)))
                .Where(r => float.IsFinite(r.Distance)).OrderBy(r => r.Distance).Select(r => r.Rock).FirstOrDefault();
            if (rock is null) { _proofNeedsZoom = true; return; }
            ProofOrder((short)id, OrderChange.CidMine, rock.Data.AsteroidId, TargetKind.Asteroid);
        }
        else if (major == 3)
        {
            foreach (int id in new[] { mission.Miner2Id, mission.Miner3Id, mission.Miner4Id })
                if (_embeddedHost!.TrainingShipExists(id) && !_embeddedHost.HasCommand(id, TrainingMissionIds.CmdGoto))
                { ProofOrder((short)id, OrderChange.CidGoto, 1052, TargetKind.Station); return; }
        }
        else if (major == 4 && _embeddedHost!.TrainingShipExists(mission.EnemyScoutId)
            && !_embeddedHost.HasCommand(mission.ScoutId, TrainingMissionIds.CmdAttack))
            ProofOrder((short)mission.ScoutId, OrderChange.CidAttack, (short)mission.EnemyScoutId, TargetKind.Ship);
        else if (major == 5 && _teamPaneVisible)
        {
            // Native advances on issued orders, before the drones arrive.
            // Wait here to observe all three deployments before leaving.
            if (_embeddedHost!.GetProbeCount() < 3) return;
            var roster = _teamPaneControls.BuildRoster(_session.World, HullNameForSort);
            int selected = _teamPaneControls.Selection;
            if (selected >= 0 && selected < roster.Count && roster[selected].ShipId == mission.Miner1Id)
                ProofTap(Keys.Y);
            else if (Environment.TickCount64 % 600 < 250) keys.Add(Keys.PageDown);
        }
    }

    private bool _proofNanSwapPressed;
    private bool _proofNaniteMountEarly = false;
    private bool _proofNaniteWithholdRepair = false;
    private int _proofLossDiversionMask;
    private bool _proofNaniteSacrifice = false;
    private bool _proofSacrificeCeased;
    private bool _proofSacrificeBombing;
    private long _proofRepairLogAt;
    private bool _proofEscortOrdersComplete;
    private bool _proofDefenseOrdersComplete;
    private float _proofRepairPending;

    private bool ProofBombardmentInFlight(Ship bomber, Station outpost)
    {
        // AllWeapons arms outside torpedo range. Observe an actual fixed-gun
        // round whose remaining flight can reach the station, giving the pilot
        // time to lower its repair reserve before that volley removes shields.
        lock (_session.Gate)
        {
            foreach (var round in _session.World.Projectiles)
            {
                if (round.LauncherId != bomber.Id) continue;
                var weapon = bomber.Weapons.FirstOrDefault(w => w.MountId < 2
                    && w.ProjectileType.ProjectileTypeId == round.ProjectileTypeId);
                if (weapon is null) continue;
                // The visual packet carries the base projectile lifetime.
                // Native weaponIGC.h:165-171 folds the energy-lifespan GA into
                // the actual shot; use that equipped weapon's complete range.
                float remaining = weapon.GetLifespan() - MathF.Max(0, (ClientNowMilliseconds - round.ReceivedLocalMs) / 1000f);
                float speed2 = round.Velocity.LengthSquared();
                if (remaining <= 0 || speed2 < 1) continue;
                var delta = outpost.Center - round.PositionAt(ClientNowMilliseconds);
                float time = Math.Clamp((delta * round.Velocity) / speed2, 0, remaining);
                if ((delta - round.Velocity * time).Length() <= outpost.Radius) return true;
            }
        }
        return false;
    }

    private float ProofIncomingRepair(Ship target)
    {
        // Observe the same transient rounds the renderer draws. A released
        // Space key cannot recall a slow Nanite round already on its way.
        // Budget those rounds before choosing another real fire input.
        var host = _embeddedHost!;
        float fraction = 0;
        void Round(int launcherId, short typeId, Allegiance.Core.Collision.Vector3 position,
            Allegiance.Core.Collision.Vector3 velocity, float lifespan)
        {
            if (!host.Mission.Core.ProjectileTypes.TryGetValue(typeId, out var type) || type.Power >= 0) return;
            var launcher = launcherId == host.PlayerShipId ? host.PlayerShip : host.Drones.GetValueOrDefault(launcherId);
            if (launcher is null || launcher.SideId != target.SideId) return;
            var delta = target.Position - position;
            var relative = velocity - target.Velocity;
            float speed2 = relative.LengthSquared();
            if (speed2 < 1) return;
            float time = (delta * relative) / speed2;
            if (time <= 0 || time > lifespan || (delta - relative * time).Length() > target.Radius) return;
            float armor = host.Mission.Core.Constants.DamageConstants[type.DamageType & 0x1f][target.HullType.DefenseType];
            fraction += -type.Power * launcher.GlobalAttributeFactor(Allegiance.Core.Model.GlobalAttributeSet.DamageGuns)
                * launcher.ExperienceMultiplier * armor / target.EffectiveMaxHitPoints();
        }
        lock (_session.Gate)
        {
            foreach (var round in _session.World.Projectiles)
                Round(round.LauncherId, round.ProjectileTypeId, round.PositionAt(ClientNowMilliseconds), round.Velocity, round.Lifespan);
        }
        foreach (var round in _ownShotEcho.GetActiveRounds(ClientNowMilliseconds))
            Round(host.PlayerShipId, round.ProjectileTypeId,
                round.Position + round.Velocity * ((ClientNowMilliseconds - round.EmittedLocalMs) / 1000f), round.Velocity, round.Lifespan);
        return fraction;
    }

    private void DriveMission8(ClientTrainingSite site, List<Keys> keys)
    {
        _proofMouse = null;
        string goal = _trainingSession!.Mission.ActiveGoalPath;
        var host = _embeddedHost;
        if (host is null) return;
        if (Environment.TickCount64 >= _proofAdvancedStateAt)
        {
            _proofAdvancedStateAt = Environment.TickCount64 + 5000;
            if (host.Drones.TryGetValue(8007, out var observedBomber))
                Console.WriteLine($"[tproof] bomber pos={observedBomber.Position} target={observedBomber.CommandTarget?.Id} targetType={observedBomber.CommandTarget?.GetType().Name} goto={observedBomber.GotoWaypoint} running={observedBomber.IsRunningAway} controls={observedBomber.Controls} state={observedBomber.StateM} outpost={host.GetFraction(new(ObjectType.Station, Mission8.EnemyOutpostStationId)):F4}");
            Console.WriteLine($"[tproof] M8 state goal={goal} sector={OwnSectorIdOr(-1)} pos={_ownSim.Position} nan={host.HasPartMounted(host.PlayerShipId, 190)} station={host.GetFraction(new(ObjectType.Station, Mission8.HomeStationId)):F4} miner={host.GetFraction(new(ObjectType.Ship, 8001)):F4} bomber={host.GetFraction(new(ObjectType.Ship, 8007)):F4} nanWingman={host.GetFraction(new(ObjectType.Ship, 8008)):F4} camera={_cameraMode} viewed={_session.World.ViewedClusterId} commandee={_commandee?.Id} verb={_commandVerb} map={_minimapExpanded}");
            foreach (int id in new[] { 8004, 8005, 8006 })
                if (host.Drones.TryGetValue(id, out var guard))
                    Console.WriteLine($"[tproof] guard={id} sector={guard.Sector?.Id} pos={guard.Position} defending={guard.DefendTarget?.Id} accepted={guard.AcceptedShipCommandId} target={guard.AcceptedShipTarget?.Id} dead={guard.Dead} state={guard.StateM} ammo={guard.Ammo} energy={guard.Energy:F1} guns={string.Join(',', guard.Parts.Where(p => p.EquipmentType == EquipmentType.Weapon).Select(p => p.PartId))}");
        }
        if (goal.StartsWith("1.", StringComparison.Ordinal) && !host.HasPartMounted(host.PlayerShipId, 190))
        {
            // Native prompt: Ctrl+1 goes through UpdateFlightParts and SWAP_PART.
            // Release after the echo so the same key cannot swap the gun back.
            if (_proofNaniteMountEarly || goal == "1.6")
            { keys.Add(Keys.LeftControl); keys.Add(Keys.D1); _proofNanSwapPressed = true; }
            return;
        }
        if (_proofNanSwapPressed) _proofNanSwapPressed = false;
        if (site.PlayerDocked)
        {
            // A player can enter a bay while following a nearby repair target.
            // Leave through the ordinary hangar launch binding.
            _proofTapKey = null; keys.Clear();
            if (Environment.TickCount64 % 800 < 300) keys.Add(Keys.L);
            return;
        }
        if (_proofNaniteWithholdRepair && goal.StartsWith("2.", StringComparison.Ordinal))
        {
            // Native permits commanding the friendly fighters. Withholding
            // repair alone can still leave a surviving miner. Divert the
            // defenders through real command-view picks, then send them back
            // to finish the encounter after the attackers kill the miner.
            bool minerAlive = host.TrainingShipExists(8001);
            short enemy = new short[] { 8002, 8003 }.FirstOrDefault(id => host.TrainingShipExists(id));
            var battle = host.Drones.TryGetValue(8001, out var miner) ? ToSys(miner.Position) : V.Zero;
            bool Away(Ship ship) => ship.AcceptedShipCommandId == OrderChange.CidGoto
                && ship.AcceptedShipTarget is Buoy point && V.Distance(ToSys(point.Position), battle) > 4000;
            if (minerAlive)
                foreach (short id in new short[] { 8004, 8005, 8006 })
                    if (host.Drones.TryGetValue(id, out var ordered) && Away(ordered))
                    {
                        var point = (Buoy)ordered.AcceptedShipTarget!;
                        int bit = 1 << (id - 8004);
                        if ((_proofLossDiversionMask & bit) == 0)
                            Console.WriteLine($"[tproof] LOSS_DIVERSION accepted defender={id} target={point.Id} position={point.Position}");
                        _proofLossDiversionMask |= bit;
                    }
            short defender = new short[] { 8004, 8005, 8006 }.FirstOrDefault(id =>
            {
                if (!host.TrainingShipExists(id) || !host.Drones.TryGetValue(id, out var ship)) return false;
                if (minerAlive)
                    return !Away(ship);
                return enemy != 0 && !(ship.AcceptedShipCommandId == OrderChange.CidAttack
                    && ship.AcceptedShipTarget is Ship foe && foe.Id is 8002 or 8003 && !foe.Dead);
            });
            if (defender != 0)
            {
                if (_cameraMode != CameraMode.Command) { keys.Add(Keys.F3); return; }
                // Cycling avoids overlapping defender and station pick icons.
                if (_commandee?.Id != defender || _commandGroup.Count != 0)
                {
                    if (Environment.TickCount64 % 600 < 250) ProofPressAction("CycleCommandee", keys);
                    return;
                }
                // Choose empty world space above the starmap and outside the
                // command bar. Read the same camera ray as the real input
                // handler, zooming normally until that pick is far away.
                int pickX = (int)(GraphicsDevice.Viewport.Width * .9f), pickY = (int)(GraphicsDevice.Viewport.Height * .46f);
                var cursor = new Microsoft.Xna.Framework.Vector2(pickX, pickY);
                if (minerAlive && (!_commandCamera.TryPickPosition(cursor, cursor, GraphicsDevice.Viewport, out var picked)
                    || V.Distance(new V(picked.X, picked.Y, picked.Z), battle) < 5000))
                { _proofNeedsZoom = true; return; }
                short verb = minerAlive ? OrderChange.CidGoto : OrderChange.CidAttack;
                if (_commandVerb != verb || LiveComposeState != NativeComposeState.Command)
                { ProofClickVerb(verb); return; }
                if (minerAlive) ProofClickAt("loss-diversion", pickX, pickY, right: true);
                else ProofClickModel(enemy, right: true);
                return;
            }
            if (_cameraMode == CameraMode.Command) { keys.Add(Keys.LeftAlt); keys.Add(Keys.F2); return; }
        }
        if (_proofNaniteMountEarly && goal.StartsWith("2.", StringComparison.Ordinal)
            && host.TrainingShipExists(8006) && host.TrainingShipExists(8003))
        {
            // Native initially sends every fighter after Solap. Split one
            // fighter onto Toast through a real order so both miner attackers
            // are occupied while the trainee supplies repair.
            if (!(host.HasCommand(8006, OrderChange.CidAttack) && host.Drones[8006].AcceptedShipTarget?.Id == 8003))
            {
                if (_cameraMode != CameraMode.Command) { keys.Add(Keys.F3); return; }
                ProofOrder(8006, OrderChange.CidAttack, 8003, TargetKind.Ship);
                return;
            }
            if (_cameraMode == CameraMode.Command) { keys.Add(Keys.LeftAlt); keys.Add(Keys.F2); return; }
        }
        bool defenseOrders = goal == "3.19" && !_proofDefenseOrdersComplete;
        if (!_proofNaniteWithholdRepair && !_proofNaniteSacrifice && (!_proofEscortOrdersComplete || defenseOrders)
            && goal.StartsWith("3.", StringComparison.Ordinal) && host.TrainingShipExists(8007))
        {
            // The player can command the existing friendly defenders. Use
            // ordinary picks and Goto orders to bring them along. Keep them
            // passive until the bomber takes the native required hull hit;
            // perfect early defense otherwise leaves native goal 3.18 waiting.
            short verb = defenseOrders ? OrderChange.CidDefend : OrderChange.CidGoto;
            int defender = new[] { 8004, 8005, 8006 }.Where(host.TrainingShipExists)
                .FirstOrDefault(id => !(host.HasCommand(id, verb) && host.Drones[id].AcceptedShipTarget?.Id == 8007));
            if (defender != 0)
            {
                if (_cameraMode != CameraMode.Command) { keys.Add(Keys.F3); return; }
                // The three fighter icons can overlap after the miner fight.
                // Select the actual recipient through native cycling before
                // picking the order target, as the loss pilot does above.
                if (_commandee?.Id != defender || _commandGroup.Count != 0)
                {
                    if (Environment.TickCount64 % 600 < 250) ProofPressAction("CycleCommandee", keys);
                    return;
                }
                ProofOrder((short)defender, verb, 8007, TargetKind.Ship);
                return;
            }
            if (_cameraMode == CameraMode.Command) { keys.Add(Keys.LeftAlt); keys.Add(Keys.F2); return; }
            _proofEscortOrdersComplete = true;
            if (defenseOrders) _proofDefenseOrdersComplete = true;
        }
        if (_proofNaniteSacrifice && !_proofSacrificeCeased && goal == "3.19"
            && host.Drones.TryGetValue(8007, out var sacrificialBomber))
        {
            var outpost = host.Mission.GetStation(Mission8.EnemyOutpostStationId)!;
            bool bombing = outpost.ShieldFraction < .95f || ProofBombardmentInFlight(sacrificialBomber, outpost);
            if (bombing && !_proofSacrificeBombing)
                Console.WriteLine($"[tproof] SACRIFICE bombardment-in-flight hull={sacrificialBomber.Fraction:F4} outpostShield={outpost.ShieldFraction:F4} sim={host.Mission.LastUpdate:F2}");
            _proofSacrificeBombing |= bombing;
            _proofRepairPending = ProofIncomingRepair(sacrificialBomber);
            // Keep a small hull reserve while enough torpedoes are in flight,
            // then release repair before the outpost falls. Both deaths remain
            // ordinary combat outcomes; the pilot only releases the fire key.
            if (_proofSacrificeBombing && outpost.ShieldFraction < .05f && sacrificialBomber.Fraction < .3f)
            {
                _proofSacrificeCeased = true;
                Console.WriteLine($"[tproof] SACRIFICE cease-repair hull={sacrificialBomber.Fraction:F4} pending={_proofRepairPending:F4} outpostShield={outpost.ShieldFraction:F4} outpostArmor={outpost.ArmorFraction:F4}");
            }
        }
        bool wingmanOrdering = (_proofNaniteSacrifice || _proofNaniteWithholdRepair) && goal == "3.19" && host.TrainingShipExists(8008)
            && !((host.HasCommand(8008, OrderChange.CidGoto) || host.HasCommand(8008, OrderChange.CidRepair))
                && host.Drones[8008].AcceptedShipTarget is EquipmentTarget { Body: Allegiance.Core.Sim.Station home }
                && home.StationId == Mission8.HomeStationId);
        if (wingmanOrdering)
        {
            // Keyboard commandee cycling avoids overlapping ship icons. Select
            // the known friendly base (any sector) and issue its native default
            // order: the repair wingman leaves for home while the trainee repairs.
            if (_commandee?.Id != 8008 || _commandGroup.Count != 0)
            {
                if (_proofNaniteSacrifice ? ProofPulse("sacrifice-commandee") : Environment.TickCount64 % 600 < 250)
                    ProofPressAction("CycleCommandee", keys);
            }
            else if (_autoTarget is not { Kind: TargetKind.Station, Id: Mission8.HomeStationId })
            {
                if (_proofNaniteSacrifice ? ProofPulse("sacrifice-base") : Environment.TickCount64 % 600 < 250)
                    ProofPressAction("TargetFriendlyBaseNearest", keys);
            }
            else if (_proofNaniteSacrifice ? ProofPulse("sacrifice-order") : Environment.TickCount64 % 600 < 250)
                ProofPressAction("IssueBuild", keys);
            if (_proofNaniteWithholdRepair) return;
        }
        if (_proofNaniteSacrifice && _cameraMode == CameraMode.Command)
        {
            if (_minimapExpanded) { keys.Add(Keys.Tab); return; }
            keys.Add(Keys.LeftAlt); keys.Add(Keys.F2); return;
        }
        V? target = null;
        bool routing = false;
        bool tendingWingman = false;
        int repairShip = 0;
        if (goal.StartsWith("1.", StringComparison.Ordinal))
            target = site.Resolve(new(ObjectType.Station, Mission8.HomeStationId))?.Position;
        else if (goal.StartsWith("2.", StringComparison.Ordinal))
        {
            target = site.Resolve(new(ObjectType.Ship, 8001))?.Position;
            repairShip = 8001;
        }
        else if (goal.StartsWith("3.", StringComparison.Ordinal))
        {
            // Meet at the authored buoy while the bomber leaves its spawn
            // bay; following its birth position can accidentally dock us.
            int subgoal = int.TryParse(goal.Split('.').ElementAtOrDefault(1), out var sub) ? sub : 0;
            if (subgoal <= 5) target = site.Resolve(new(ObjectType.Buoy, 8801))?.Position;
            else if (host.Drones.TryGetValue(8007, out var bomber) && bomber.Sector?.Id != OwnSectorIdOr(-1))
            {
                routing = true;
                if (ResolveTargetPosition(new(TargetKind.Cluster, bomber.Sector?.Id ?? 1051, "Bomber sector"), out var route))
                    target = ToSys(route);
            }
            else { target = site.Resolve(new(ObjectType.Ship, 8007))?.Position; repairShip = 8007; }
            float nanHealth = host.GetFraction(new(ObjectType.Ship, 8008));
            if (!_proofNaniteWithholdRepair && !_proofNaniteSacrifice && subgoal >= 14
                && subgoal < 19
                && host.GetFraction(new(ObjectType.Ship, 8007)) > .3f && nanHealth is > 0 and < .5f
                && host.Drones.TryGetValue(8008, out var nanWingman) && nanWingman.Sector?.Id == OwnSectorIdOr(-1))
            {
                target = ToSys(nanWingman.Position);
                tendingWingman = true;
                repairShip = 8008;
            }
        }
        if (target is not { } destination || !TryOwnTrainingPose(out var position, out _, out _, out _)) return;
        if (!wingmanOrdering && (_proofNaniteMountEarly || _proofNaniteSacrifice) && _autoTarget?.Id != repairShip)
        {
            if (repairShip == 8007) keys.Add(Keys.K); // ordinary friendly-bomber targeting
            else if (repairShip == 8001) keys.Add(Keys.H); // ordinary friendly-miner targeting
        }
        if ((_proofNaniteMountEarly || _proofNaniteSacrifice) && !routing && repairShip != 0
            && _session.World.Ships.TryGetValue((short)repairShip, out var contactShip) && contactShip.Latest is { } update)
            destination = ToSys(RemoteShipPose(_session.World, (short)repairShip, update, contactShip).Position);
        bool strictEscort = _proofNaniteMountEarly || _proofNaniteSacrifice;
        if (strictEscort) keys.Add(Keys.Back);
        bool formationFlight = _proofNaniteSacrifice && !routing && repairShip == 8007
            && host.Drones[8007].Sector?.Id == OwnSectorIdOr(-1);
        if (formationFlight)
        {
            var bomber = host.Drones[8007];
            var travel = ToSys(bomber.Velocity);
            var heading = travel.LengthSquared() > 1 ? V.Normalize(travel) : ToSys(bomber.Orientation.GetForward());
            // Establish the repair position before contact, behind and above
            // the bomber rather than crossing its hull as combat starts.
            var offsetError = destination - ToSys(_ownSim.Position) - heading * 200 + new V(0, 0, 100);
            var closing = offsetError * .2f;
            if (closing.LengthSquared() > 3600) closing = V.Normalize(closing) * 60;
            ProofMatchVelocity(travel + closing, keys);
        }
        else if (_proofNaniteSacrifice && routing)
        {
            // Arrive through the aleph near the bomber's speed. Full cruise
            // overtakes it before the first slow-frame reverse-thrust sample.
            var route = destination - position;
            ProofMatchVelocity(route.LengthSquared() > 1 ? V.Normalize(route) * 90 : V.Zero, keys);
        }
        V aimPoint = destination;
        bool repairInRange = true;
        V repairMotion = V.Zero;
        string leadTrace = "";
        if ((_proofNaniteMountEarly || _proofNaniteSacrifice) && Environment.TickCount64 >= _proofRepairLogAt
            && _session.World.MyShipId is { } mineId && _session.World.Ships.TryGetValue(mineId, out var mineShip))
            leadTrace = $" leadScene=missing ownUpdate={mineShip.Latest is not null} catalog={_shopCatalog is not null} loadout={mineShip.Loadout is not null} mount={_selectedMount} weapon={ResolveSelectedWeapon(mineShip).Found}";
        // Aim at the same native lead marker the HUD presents for a moving
        // repair target; pointing at its old center misses a slow nanite round.
        if (!routing && repairShip != 0 && TryGatherLeadScene(IgcObjectType.Ship, (short)repairShip, out var scene))
        {
            var input = scene.Input;
            if (_proofNaniteMountEarly || _proofNaniteSacrifice)
                input = input with { MyPosition = _ownSim.Position, MyVelocity = _ownSim.Velocity,
                    MyOrientation = _ownSim.Orientation, TargetPosition = new(destination.X, destination.Y, destination.Z),
                    TargetRadius = 0 }; // aim through the hull centre rather than graze its bounding sphere
            if (_proofNaniteSacrifice)
            {
                var projected = ProofFiringPose(plannedKeys: keys);
                input = input with { MyPosition = projected.Position, MyVelocity = projected.Velocity,
                    MyOrientation = projected.Orientation };
                repairMotion = ToSys(input.TargetVelocity - input.MyVelocity)
                    * (1000 / MathF.Max(1, (input.TargetPosition - input.MyPosition).Length()));
            }
            var lead = ClientLead.Solve(input);
            repairInRange = lead.GatePassed && lead.Solved && lead.InRange;
            if ((_proofNaniteMountEarly || _proofNaniteSacrifice) && Environment.TickCount64 >= _proofRepairLogAt)
                leadTrace = $" leadGate={lead.GatePassed} solved={lead.Solved} hit={lead.WillHit} inRange={lead.InRange} friendly={input.FriendlyOrAllied} power={input.ProjectilePower} targetError={(input.TargetPosition - host.Drones[repairShip].Position).Length():F1} ownError={(_ownSim.Position - host.PlayerShip.Position).Length():F1} velocity={input.TargetVelocity} hostVelocity={host.Drones[repairShip].Velocity}";
            if (lead.GatePassed && lead.Solved) aimPoint = ToSys(_ownSim.Position + lead.Direction * 1000);
        }
        if (_proofNaniteSacrifice) ProofAimMouse(aimPoint, keys, repairMotion);
        else if (_proofNaniteMountEarly) ProofAimMouse(aimPoint, keys);
        else SteerTowards(aimPoint, keys, .015f);
        float distance = V.Distance(position, destination);
        float aim = OwnAimDegreesTo(aimPoint);
        float followDistance = strictEscort ? (repairShip != 0 ? 150 : 300) : 180;
        if (_proofNaniteWithholdRepair && (goal.StartsWith("2.", StringComparison.Ordinal) || goal == "3.19"))
        {
            // Withholding repair must also keep our hull out of the incoming
            // fire. Following at repair range can shield the miner or bomber
            // and select survival instead. Back away through ordinary flight controls;
            // the native combat, timers and branch observer remain untouched.
            keys.Add(Keys.Back);
            if (distance < 1500 && aim < 10) keys.Add(Keys.S);
        }
        else if (!formationFlight && !(_proofNaniteSacrifice && routing)
            && (routing || distance > followDistance) && aim < 10) keys.Add(Keys.W);
        bool repairing = !(_proofNaniteWithholdRepair && !goal.StartsWith("1.", StringComparison.Ordinal));
        // Conserve energy while a friendly hull is already full. Firing at it
        // throughout the narration otherwise spends the repair reserve before
        // the authored attackers arrive.
        bool needsRepair = repairShip != 0 ? host.GetFraction(new(ObjectType.Ship, repairShip)) < .999f
            : goal.StartsWith("1.", StringComparison.Ordinal) && host.GetFraction(new(ObjectType.Station, Mission8.HomeStationId)) < 1;
        if (_proofNaniteSacrifice && repairShip == 8007 && goal == "3.19")
            needsRepair = host.GetFraction(new(ObjectType.Ship, repairShip)) + (_proofSacrificeBombing ? _proofRepairPending : 0)
                < (_proofSacrificeBombing ? .3f : .8f);
        if (_proofSacrificeCeased) needsRepair = false;
        // The timed announcements still run ordinary combat. Do not revert to
        // the generic full-repair policy after leaving combat goal 3.19.
        if (_proofNaniteSacrifice && goal is "3.20" or "3.21" or "3.22") needsRepair = false;
        float shotAngle = 3;
        if (repairShip != 0 && host.Drones.TryGetValue(repairShip, out var repairHull))
            shotAngle = MathF.Max(3, MathF.Asin(Math.Clamp(repairHull.Radius / MathF.Max(1, distance), 0, 1)) * 57.29578f * .75f);
        float shotAim = _proofNaniteSacrifice ? ProofShotAngleTo(aimPoint, keys) : aim;
        if (repairing && needsRepair && !routing && distance < 600 && shotAim < shotAngle
            && (!strictEscort || repairInRange) && (goal is not "3.18" || tendingWingman)) keys.Add(Keys.Space);
        if (Environment.TickCount64 >= _proofRepairLogAt)
        {
            _proofRepairLogAt = Environment.TickCount64 + 5000;
            Console.WriteLine($"[tproof] repair target={repairShip} distance={distance:F1} aim={aim:F2} cone={shotAngle:F2} firing={keys.Contains(Keys.Space)} pending={_proofRepairPending:F4} bombing={_proofSacrificeBombing} energy={host.PlayerShip.Energy:F1}{leadTrace}");
            if (host.Drones.TryGetValue(8008, out var support))
                Console.WriteLine($"[tproof] support pos={support.Position} target={support.CommandTarget?.Id} repair={support.RepairTarget?.Id} state={support.StateM} energy={support.Energy:F1}");
        }
        // Use the carried proximity mines through the normal dispenser key.
        // The native lesson explicitly equips them for defending repair work.
        if (!_proofNaniteWithholdRepair && !_proofNaniteSacrifice && host.Drones.Values.Any(s => !s.Dead && s.SideId == 1
            && s.Sector?.Id == OwnSectorIdOr(-1) && V.Distance(position, ToSys(s.Position)) < 550)
            && Environment.TickCount64 % 2500 < 500) keys.Add(Keys.N);
    }
}
