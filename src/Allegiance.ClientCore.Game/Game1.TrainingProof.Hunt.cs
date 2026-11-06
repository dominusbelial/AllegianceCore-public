using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;
using V = System.Numerics.Vector3;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private bool _proofHuntCommitted;
    private bool _proofHuntPreparing;
    private readonly Dictionary<int, double> _proofHuntRetryAfter = new();

    private void DriveMinerHunt(List<Keys> keys)
    {
        var host = _embeddedHost!;
        _proofTapKey = null;
        keys.Remove(Keys.F);
        keys.Add(Keys.Back);
        var exit = _trainingSession!.Site.Resolve(new(Allegiance.ClientCore.Training.ObjectType.Warp, 1032))!.Position;
        if (_proofHuntCommitted && host.Drones.TryGetValue(_proofM4Quarry, out var retreating)
            && !retreating.Dead && retreating.Sector?.Id != 1031)
        {
            // Native mission4.cpp:821-824 accepts ANY miner's death. Keep the
            // local aleph selected and try another Mars contact after a retreat.
            _proofHuntRetryAfter[_proofM4Quarry] = host.Mission.LastUpdate + 90;
            Console.WriteLine($"[tproof] HUNT disengage target={_proofM4Quarry} hull={retreating.Fraction:F4} exitDistance={(ToSys(retreating.Position) - exit).Length():F1}");
            _proofHuntCommitted = false;
            _proofHuntPreparing = false;
        }
        if (!_proofHuntCommitted)
        {
            if (!_cloakToggled) keys.Add(Keys.G);
            // Keep a Mars aleph selected while closing. Native lesson 7 tests
            // the CURRENT target's sector, so picking a returning miner on the
            // far side of Mars commits to its departure before we can engage.
            if (_autoTarget?.Kind != TargetKind.Warp)
            {
                if (ProofPulse("hunt-warp")) ProofPressAction("TargetWarp", keys);
                return;
            }
            if (!_proofHuntPreparing) _proofM4Quarry = Enumerable.Range(1101, 4)
                .Where(id => host.TrainingShipExists(id) && host.Drones[id].Sector?.Id == 1031)
                .Where(id => host.Mission.LastUpdate >= _proofHuntRetryAfter.GetValueOrDefault(id)
                    && (ToSys(host.Drones[id].Position) - exit).Length() > 1000)
                .OrderBy(id => (host.Drones[id].Position - _ownSim.Position).Length()
                    + host.Drones[id].Velocity.Length() * 20).FirstOrDefault();
            if (_proofM4Quarry == 0)
            {
                if (_coastToggled) ProofPressAction("LockVector", keys);
                return;
            }
        }
        var quarry = host.Drones[_proofM4Quarry];
        var position = quarry.Position;
        if (_session.World.Ships.TryGetValue((short)_proofM4Quarry, out var contact) && contact.Latest is { } update)
            position = RemoteShipPose(_session.World, (short)_proofM4Quarry, update, contact).Position;
        var delta = ToSys(position - _ownSim.Position);
        float distance = delta.Length();
        // Close along the arrival bearing and retain a short radial standoff.
        // Fixed points on the exit ray or behind the hull can put this approach
        // inside a neighboring mining rock or across the retreating contact.
        var player = host.PlayerShip;
        if (!_proofHuntCommitted && MathF.Abs(distance - 150) < 60 && _ownSim.Velocity.Length() < 35 && quarry.Velocity.Length() < 3)
            _proofHuntPreparing = true;
        float braking = player.EffectiveThrust() / player.Mass
            * MathF.Min(player.HullType.BackMultiplier, player.HullType.SideMultiplier) * .5f;
        var desiredVelocity = _proofHuntPreparing && !_proofHuntCommitted ? V.Zero
            : TrainingProofPilot.FollowVelocity(delta, ToSys(quarry.Velocity), 150, 250, _proofSampleSeconds, braking);
        // Do not chase through the exit. Limit inward velocity early enough
        // for ordinary reverse thrust to brake, while retaining tangential
        // motion around the aleph when another mining contact is beyond it.
        var fromExit = ToSys(_ownSim.Position) - exit;
        float exitDistance = fromExit.Length();
        if (exitDistance > 1)
        {
            var outward = fromExit / exitDistance;
            float radial = V.Dot(desiredVelocity, outward);
            float minimumRadial = (500 - exitDistance) / MathF.Max(4, _proofSampleSeconds + 3);
            if (radial < minimumRadial) desiredVelocity += outward * (minimumRadial - radial);
        }
        ProofMatchVelocity(desiredVelocity, keys, coast: _proofHuntCommitted);
        V aimPoint = ToSys(position);
        V relative = ToSys(quarry.Velocity - _ownSim.Velocity);
        if (!_proofHuntCommitted && !_proofHuntPreparing && desiredVelocity.LengthSquared() > 1)
        {
            // Navigate around the aleph toward the interception point. Pointing
            // at the quarry while asking for an unrelated strafe can stall on
            // training's reserved Insert/AcceptCommand key.
            aimPoint = ToSys(_ownSim.Position) + V.Normalize(desiredVelocity) * 1000;
            relative = V.Zero;
        }
        bool inRange = false;
        if (_proofHuntCommitted && TryGatherLeadScene(Allegiance.Core.IgcObjectType.Ship, (short)_proofM4Quarry, out var scene))
        {
            var projected = ProofFiringPose(plannedKeys: keys);
            var input = scene.Input with { MyPosition = projected.Position, MyVelocity = projected.Velocity,
                MyOrientation = projected.Orientation, TargetPosition = position, TargetRadius = 0 };
            var lead = ClientLead.Solve(input);
            inRange = lead.GatePassed && lead.Solved && lead.InRange;
            if (lead.GatePassed && lead.Solved)
            {
                aimPoint = ToSys(_ownSim.Position + lead.Direction * 1000);
                // The sight-line feed-forward must use the same emission
                // velocity as the lead. Old pre-thrust velocity keeps turning
                // after the new frame has already matched the fleeing miner.
                relative = ToSys(input.TargetVelocity - projected.Velocity)
                    * (1000 / MathF.Max(1, (position - projected.Position).Length()));
            }
        }
        // Opening fire at long range starts the retreat while dispersion and
        // closing thrust still waste the narrow kill window. Settle under
        // cloak first, then use the authored guns and carried EMP together.
        bool covered = false, armed = false;
        if (!_proofHuntCommitted && _proofHuntPreparing)
        {
            lock (_session.Gate)
                foreach (var mine in _session.World.Mines.Values)
                    if (mine.LauncherShipId == player.Id && mine.ClusterId == 1031
                        && host.Mission.Core.MineTypes.TryGetValue(mine.MinetypeId, out var type)
                        && (mine.Position - position).Length() < type.MineRadius - 25)
                    {
                        covered = true;
                        // Native mineigc.h:75-82: one-second arming delay;
                        // stationary miners are immune until they move.
                        armed |= host.Mission.LastUpdate >= mine.Time0ServerSeconds + Allegiance.Core.Sim.Mine.ArmTimeSeconds;
                    }
            if (!covered)
            {
                // FedSrv.CPP:5646-5774 drops a stationary mine BEHIND the
                // layer by its radius + half the hull length + five. Face
                // away so that ordinary N lays the field over the miner.
                aimPoint = ToSys(_ownSim.Position) - V.Normalize(delta) * 1000;
                relative = V.Zero;
            }
        }
        ProofAimMouse(aimPoint, keys, relative);
        if (!_proofHuntCommitted && _proofHuntPreparing && OwnAimDegreesTo(aimPoint) < 5)
        {
            if (!covered && _ownSim.Velocity.LengthSquared() < 1 && player.Velocity.LengthSquared() < 1
                && ProofPulse("hunt-mine")) keys.Add(Keys.N);
            if (armed)
            {
                _proofHuntCommitted = true;
                Console.WriteLine($"[tproof] HUNT commit target={_proofM4Quarry} distance={distance:F1} speed={quarry.Velocity.Length():F1} mineReady=True sim={host.Mission.LastUpdate:F2}");
            }
        }
        // F was consumed by native goal 7.5. The kill goal explicitly accepts
        // an unselected miner, so leave the Mars aleph selected while shooting
        // its visible hull; cycling a contact after it leaves ends the lesson.
        if (_proofHuntCommitted && _cloakToggled) keys.Add(Keys.G);
        if (_proofHuntCommitted && !_cloakToggled && inRange && ProofShotAngleTo(aimPoint, keys) < 10)
        {
            keys.Add(Keys.Space);
        }
        if (Environment.TickCount64 >= _proofM4CombatLogAt)
        {
            _proofM4CombatLogAt = Environment.TickCount64 + 1000;
            Console.WriteLine($"[tproof] HUNT target={_proofM4Quarry} committed={_proofHuntCommitted} distance={distance:F1} aim={OwnAimDegreesTo(aimPoint):F2} firing={keys.Contains(Keys.Space)} range={inRange} ownError={(_ownSim.Position - player.Position).Length():F1} hull={quarry.Fraction:F4} shield={quarry.ShieldFraction:F4} energy={player.Energy:F1} ammo={player.Ammo}");
        }
    }
}
