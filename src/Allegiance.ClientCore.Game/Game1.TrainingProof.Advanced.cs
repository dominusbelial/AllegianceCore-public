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
    private bool _proofNeedsZoom;
    private string _proofClick = "";
    private long _proofClickAt;
    private long _proofClickCycle = -1;
    private int _proofClickX, _proofClickY;
    private MouseState ReadProofMouse() => _proofEnabled
        ? _proofMouse ?? new MouseState(GraphicsDevice.Viewport.Width / 2, GraphicsDevice.Viewport.Height / 2,
            0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released)
        : Mouse.GetState();

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
    private bool _proofNaniteSacrifice = false;
    private bool _proofSacrificeCeased;
    private long _proofRepairLogAt;
    private bool _proofEscortOrdersComplete;
    private bool _proofDefenseOrdersComplete;
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
            Console.WriteLine($"[tproof] M8 state goal={goal} sector={OwnSectorIdOr(-1)} pos={_ownSim.Position} nan={host.HasPartMounted(host.PlayerShipId, 190)} station={host.GetFraction(new(ObjectType.Station, Mission8.HomeStationId)):F4} miner={host.GetFraction(new(ObjectType.Ship, 8001)):F4} bomber={host.GetFraction(new(ObjectType.Ship, 8007)):F4} nanWingman={host.GetFraction(new(ObjectType.Ship, 8008)):F4}");
            foreach (int id in new[] { 8004, 8005, 8006 })
                if (host.Drones.TryGetValue(id, out var guard))
                    Console.WriteLine($"[tproof] guard={id} sector={guard.Sector?.Id} pos={guard.Position} defending={guard.DefendTarget?.Id} accepted={guard.AcceptedShipCommandId} target={guard.AcceptedShipTarget?.Id} dead={guard.Dead}");
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
        bool defenseOrders = goal == "3.19" && !_proofDefenseOrdersComplete;
        if (!_proofNaniteWithholdRepair && (!_proofEscortOrdersComplete || defenseOrders)
            && goal.StartsWith("3.", StringComparison.Ordinal) && host.TrainingShipExists(8007))
        {
            // The player can command the existing friendly defenders. Use
            // ordinary picks and Goto orders to bring them along. Keep them
            // passive until the bomber takes the native required hull hit;
            // perfect early defense otherwise leaves native goal 3.18 waiting.
            short verb = defenseOrders ? OrderChange.CidDefend : OrderChange.CidGoto;
            int defender = new[] { 8004, 8005, 8006 }.Where(host.TrainingShipExists).Take(defenseOrders ? 2 : 3)
                .FirstOrDefault(id => !(host.HasCommand(id, verb) && host.Drones[id].AcceptedShipTarget?.Id == 8007));
            if (defender != 0)
            {
                if (_cameraMode != CameraMode.Command) { keys.Add(Keys.F3); return; }
                ProofOrder((short)defender, verb, 8007, TargetKind.Ship);
                return;
            }
            if (_cameraMode == CameraMode.Command) { keys.Add(Keys.LeftAlt); keys.Add(Keys.F2); return; }
            _proofEscortOrdersComplete = true;
            if (defenseOrders) _proofDefenseOrdersComplete = true;
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
                && subgoal < 19 && host.GetFraction(new(ObjectType.Ship, 8007)) > .3f && nanHealth > 0
                && host.Drones.TryGetValue(8008, out var nanWingman) && nanWingman.Sector?.Id == OwnSectorIdOr(-1))
            {
                target = ToSys(nanWingman.Position);
                tendingWingman = true;
                repairShip = 8008;
            }
        }
        if (target is not { } destination || !TryOwnTrainingPose(out var position, out _, out _, out _)) return;
        V aimPoint = destination;
        // Aim at the same native lead marker the HUD presents for a moving
        // repair target; pointing at its old center misses a slow nanite round.
        if (!routing && repairShip != 0 && TryGatherLeadScene(IgcObjectType.Ship, (short)repairShip, out var scene))
        {
            var lead = ClientLead.Solve(scene.Input);
            if (lead.GatePassed && lead.Solved) aimPoint = ToSys(_ownSim.Position + lead.Direction * 1000);
        }
        SteerTowards(aimPoint, keys, .015f);
        float distance = V.Distance(position, destination);
        float aim = OwnAimDegreesTo(aimPoint);
        if ((routing || distance > 180) && aim < 10) keys.Add(Keys.W);
        bool repairing = !(_proofNaniteWithholdRepair && !goal.StartsWith("1.", StringComparison.Ordinal));
        // Conserve energy while a friendly hull is already full. Firing at it
        // throughout the narration otherwise spends the repair reserve before
        // the authored attackers arrive.
        bool needsRepair = repairShip != 0 ? host.GetFraction(new(ObjectType.Ship, repairShip)) < .999f
            : goal.StartsWith("1.", StringComparison.Ordinal) && host.GetFraction(new(ObjectType.Station, Mission8.HomeStationId)) < 1;
        if (_proofNaniteSacrifice && repairShip == 8007 && goal == "3.19"
            && host.Drones.TryGetValue(8007, out var attackingBomber))
            _proofSacrificeCeased |= (attackingBomber.StateM & ShipControlState.WeaponsMask) != 0;
        if (_proofSacrificeCeased) needsRepair = false;
        float shotAngle = 3;
        if (repairShip != 0 && host.Drones.TryGetValue(repairShip, out var repairHull))
            shotAngle = MathF.Max(3, MathF.Asin(Math.Clamp(repairHull.Radius / MathF.Max(1, distance), 0, 1)) * 57.29578f * .75f);
        if (repairing && needsRepair && !routing && distance < 600 && aim < shotAngle && (goal is not "3.18" || tendingWingman)) keys.Add(Keys.Space);
        if (Environment.TickCount64 >= _proofRepairLogAt)
        {
            _proofRepairLogAt = Environment.TickCount64 + 5000;
            Console.WriteLine($"[tproof] repair target={repairShip} distance={distance:F1} aim={aim:F2} cone={shotAngle:F2} firing={keys.Contains(Keys.Space)} energy={host.PlayerShip.Energy:F1}");
            if (host.Drones.TryGetValue(8008, out var support))
                Console.WriteLine($"[tproof] support pos={support.Position} target={support.CommandTarget?.Id} repair={support.RepairTarget?.Id} state={support.StateM} energy={support.Energy:F1}");
        }
        // Use the carried proximity mines through the normal dispenser key.
        // The native lesson explicitly equips them for defending repair work.
        if (!_proofNaniteWithholdRepair && host.Drones.Values.Any(s => !s.Dead && s.SideId == 1
            && s.Sector?.Id == OwnSectorIdOr(-1) && V.Distance(position, ToSys(s.Position)) < 550)
            && Environment.TickCount64 % 2500 < 500) keys.Add(Keys.N);
    }
}
