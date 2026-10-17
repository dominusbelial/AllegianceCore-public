using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Sim;

public sealed partial class Ship
{
    private const short DroneInTransitSound = 1337; // Igc/sounds.h:390 (DEFSOUND ordinal)
    private int _wingmanCheckCooldown;
    private int _wingmanDodgeCooldown;
    private double? _targetFirstNotSeen;
    private bool _wingmanDodgedThisTick;
    private ControlData _wingmanControlsBeforePlot;
    private int _wingmanDodgeState;
    private Probe? _attackProbeTarget;
    private Model? _acceptedWingmanAttackTarget;
    private GotoOrder? _wingmanInterruptedGoto;
    private Model? ActiveWingmanTarget => (Model?)AttackTarget ?? _attackProbeTarget;

    /// <summary>The accepted attack survives a temporary goto used to find it
    /// (Igc/shipIGC.cpp:2707-2758). Direct AttackTarget writes remain plan writes.</summary>
    public void SetWingmanAttackOrder(Model target)
    {
        if (target is not Ship && target is not Probe) throw new ArgumentException("Expected ship or probe", nameof(target));
        SetGoto(null);
        RepairTarget = null; RepairStationTarget = null;
        PlanTarget = null; PlanCommandId = OrderChange.CidNone;
        AcceptedTarget = null; AcceptedCommandId = OrderChange.CidNone;
        MiningAsteroidTarget = null; LayerAsteroidTarget = null; BuilderAsteroidTarget = null;
        LayerBuildOrder = null; PickupTarget = null;
        DefendTarget = null;
        _acceptedWingmanAttackTarget = target;
        _wingmanInterruptedGoto = null;
        SetWingmanPlanAttack(target);
    }

    // Accepted replacement orders supersede the remembered attack, while a
    // temporary damage-retreat plan preserves it (Igc/shipIGC.h:766-787).
    private void ForgetAcceptedWingmanAttack()
    {
        if (!IsNpc) return;
        AttackTarget = null;
        _acceptedWingmanAttackTarget = null;
        _wingmanInterruptedGoto = null;
        _attackProbeTarget = null;
    }

    private void SetWingmanPlanAttack(Model? target)
    {
        AttackTarget = target as Ship;
        _attackProbeTarget = target as Probe;
        CommandTarget = target;
        DronePlanChanged();
    }

    // CshipIGC::CanSee, Igc/shipIGC.h:380-389: side knowledge OR own scanner.
    private bool CanSeeWingmanTarget(Model target)
    {
        if (Sector is not { } sector || SideId is not { } side) return false;
        bool local = target is Ship ship ? ship.Sector == sector && ship.StationId is null
            : sector.Models.Contains(target);
        if (!local) return false;
        if (EquipmentTarget.SideOf(target) == side || target.SeenBySide(side)) return true;
        if (target is Ship { Flag: not null }) return true;
        float signature = target is Ship s ? s.EffectiveSignature : ((Probe)target).ProbeType.Signature;
        float scale = Mission?.Sides.FirstOrDefault(s => s.Id == EquipmentTarget.SideOf(target))?
            .GlobalAttributes.Get(GlobalAttributeSet.Signature) ?? 1;
        if (scale > 0) signature /= scale;
        float radius = TargetRadius(target);
        // Range check first: most invisible targets are outside the scanner.
        var body = new VisibilityMath.SightBody(target.Position, radius);
        if (!VisibilityMath.InDetectRange(new(Position, 0.5f * HullType.Length), EffectiveScannerRange(), body, signature)) return false;
        foreach (var rock in sector.Mineable)
        {
            if (rock.Dead || rock.Data.Position is not { Length: 3 } p) continue;
            if (!VisibilityMath.LineOfSightExist(Position, body,
                [new(new(p[0], p[1], p[2]), rock.Radius)])) return false;
        }
        return true;
    }

    private static float TargetRadius(Model target) => target is Ship s ? 0.5f * s.HullType.Length : ((Probe)target).ProbeType.Radius;
    private static Vector3 TargetVelocity(Model target) => target is Ship s ? s.Velocity : Vector3.Zero;

    private bool IsWingmanEnemy(Model target) => SideId is not null && EquipmentTarget.SideOf(target) is { } side &&
        !IsFriendlySide(side) && EquipmentTarget.IsDamageable(target) &&
        (target is not Ship s || (!s.Dead && s.StationId is null));

    // ModelHasTargetPriority, Igc/shipIGC.cpp:2000-2027. Ordered first match,
    // ships before probes, rather than a nearest-target approximation.
    private bool HasWingmanPriority(Model target)
    {
        if (!IsWingmanEnemy(target) || !CanSeeWingmanTarget(target)) return false;
        if ((WingmanBehaviour & 0x80) != 0) return true;
        var dp = target.Position - Position;
        float distance = dp.Length();
        if (distance >= 800 || distance <= 0) return false;
        float relative = (Velocity - TargetVelocity(target)) * (dp / distance);
        return (relative > 0 && relative < 100) ||
            (distance < 550 && relative > -20 && relative < 150) ||
            (distance < 400 && relative > -100 && relative < 200);
    }

    private Model? FindWingmanPriorityTarget()
    {
        if (Sector is not { } sector) return null;
        foreach (var ship in sector.GetShipUpdateCandidates())
            if (ship != this && HasWingmanPriority(ship)) return ship;
        foreach (var probe in sector.Models.OfType<Probe>())
            if (HasWingmanPriority(probe)) return probe;
        return null;
    }

    // Igc/shipIGC.cpp:2707-2790: accepted attack near the destination mouth
    // becomes a cluster buoy; goto periodically reacquires or attacks priority.
    private void UpdateWingmanTravel(double now)
    {
        if (ActiveWingmanTarget is Ship target && target.Sector != Sector)
        {
            bool follow = false;
            if (ReferenceEquals(_acceptedWingmanAttackTarget, target) && target.Sector is { } destination &&
                !target.Dead && EffectiveScannerRange() > 100)
            {
                var nearest = destination.Warps.Where(w => w.Warp.Position is { Length: 3 })
                    .MinBy(w => (new Vector3(w.Warp.Position[0], w.Warp.Position[1], w.Warp.Position[2]) - target.Position).LengthSquared());
                if (nearest is not null && nearest.Destination == Sector && nearest.Warp.Position is { Length: 3 } p &&
                    (new Vector3(p[0], p[1], p[2]) - target.Position).LengthSquared() < 800 * 800)
                {
                    var acceptedOrder = _acceptedWingmanAttackTarget;
                    SetGoto(new(GotoKind.Cluster, destination.Id, Vector3.Zero));
                    _acceptedWingmanAttackTarget = acceptedOrder;
                    follow = true;
                }
            }
            if (!follow)
            {
                if (ReferenceEquals(_acceptedWingmanAttackTarget, target)) _acceptedWingmanAttackTarget = null;
                SetWingmanPlanAttack(null);
            }
        }
        if (GotoWaypoint is null) return;
        if (_wingmanCheckCooldown > 0) { _wingmanCheckCooldown--; return; }
        if (_acceptedWingmanAttackTarget is { } accepted && CanSeeWingmanTarget(accepted) && IsWingmanEnemy(accepted))
        {
            GotoWaypoint = null;
                SetWingmanPlanAttack(accepted);
        }
        if ((WingmanBehaviour & (0x40 | 0x80)) != 0 && _timeRanAway + 10 <= now &&
            FindWingmanPriorityTarget() is { } priority)
        {
            _wingmanInterruptedGoto = GotoWaypoint;
            GotoWaypoint = null;
            if ((WingmanBehaviour & 0x80) != 0)
            {
                _acceptedWingmanAttackTarget = priority;
                _wingmanInterruptedGoto = null;
                WingmanBehaviour &= ~0x80;
            }
            SetWingmanPlanAttack(priority);
            SendDroneChat(DroneInTransitSound, $"Attacking {priority.Id}");
        }
        _wingmanCheckCooldown = 100;
    }

    private void ReleaseWingmanTarget(Model target)
    {
        if (ReferenceEquals(_acceptedWingmanAttackTarget, target)) _acceptedWingmanAttackTarget = null;
        SetWingmanPlanAttack(null);
        if (_wingmanInterruptedGoto is { } previous)
        {
            GotoWaypoint = previous;
            _wingmanInterruptedGoto = null;
        }
    }

    // Igc/shipIGC.cpp:2410-2460: reduced lead skill through three seconds,
    // preserve turn/throttle through five, then investigate or drop the order.
    private bool PlotUnseenWingmanTarget(Model target, ref float shootSkill, ref int state)
    {
        if (CanSeeWingmanTarget(target)) return false;
        if (!_wingmanDodgedThisTick) state |= ShipControlState.ForwardButton;
        if (_targetFirstNotSeen is null) { _targetFirstNotSeen = LastUpdate; return false; }
        double elapsed = LastUpdate - _targetFirstNotSeen.Value;
        if (elapsed > 5)
        {
            if (ReferenceEquals(_acceptedWingmanAttackTarget, target))
            {
                float limit = (target.Position - Position).Length() * 1.5f;
                bool investigate = !Sector!.GetShipUpdateCandidates().Any(s =>
                    s != this && IsWingmanEnemy(s) && CanSeeWingmanTarget(s) && (s.Position - Position).Length() < limit);
                _acceptedWingmanAttackTarget = null;
                if (investigate)
                {
                    SetGoto(new(GotoKind.Waypoint, Sector.Id, target.Position));
                    WingmanBehaviour |= 0x80;
                }
                else ReleaseWingmanTarget(target);
            }
            else ReleaseWingmanTarget(target);
            return true;
        }
        if (elapsed > 3)
        {
            Controls = _wingmanControlsBeforePlot;
            StateM = (StateM & ShipControlState.SelectedWeaponMask) | state;
            return true;
        }
        shootSkill = 0.5f;
        return false;
    }

    private void PlotWingmanCombat(float dt)
    {
        int previousState = StateM;
        StateM &= ~(ShipControlState.WeaponsMask | ShipControlState.MissileFire | ShipControlState.MineFire);
        if (ActiveWingmanTarget is not { } target) return;
        // P699c: an idle combat plot must preserve the native docking/travel
        // controls computed by ApplyNpcAutopilot (common.cpp:1287-1540).
        StateM &= ~(ShipControlState.MoveButtonsMask | ShipControlState.AfterburnerButton);
        bool local = target is Ship s ? s.Sector == Sector : Sector?.Models.Contains(target) == true;
        if (!local || !IsWingmanEnemy(target)) { ReleaseWingmanTarget(target); return; }
        var weapon = Weapons.FirstOrDefault(w => w.MountId == 0);
        if (weapon is null) return;
        float skill = WingmanShootSkill;
        if (DefendTarget is null && _wingmanCheckCooldown > 0) _wingmanCheckCooldown--;
        int state = _wingmanDodgedThisTick ? _wingmanDodgeState : 0;
        if (PlotUnseenWingmanTarget(target, ref skill, ref state)) return;
        float speed = weapon.ProjectileType.Speed;
        if (weapon.Type.CAmmoPerShot != 0) speed *= GlobalAttributeFactor(GlobalAttributeSet.SpeedAmmo);
        var delta = TargetVelocity(target) * skill;
        if (!weapon.ProjectileType.AbsoluteF) delta -= Velocity;
        float t = SolveForImpact(target.Position - Position, delta, speed, TargetRadius(target), out var direction);
        if (direction.LengthSquared() < 1e-6f) return;
        float angle = TurnWingmanToFace(direction, dt, 0.75f);
        Controls = Controls with { Throttle = 0.5f + (MathF.PI - angle) / (2 * MathF.PI) };
        if (_wingmanDodgedThisTick) _wingmanDodgeCooldown = 100;
        else if (_wingmanDodgeCooldown > 0)
        {
            _wingmanDodgeCooldown--;
            state = previousState & ~(ShipControlState.WeaponsMask | ShipControlState.MissileFire | ShipControlState.MineFire);
        }
        else if (angle < MathF.PI * 0.5f)
        {
            state |= WingmanRangeState(target, t, weapon.GetLifespan(), previousState, LastUpdate + dt);
            // Igc/shipIGC.cpp:2578-2593. The magazine's normal update owns
            // mount, load, ammo, lock construction, and actual firing.
            if ((WingmanBehaviour & 2) != 0 && MissileLock >= 1 &&
                _parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0)?.Type.Type
                    is DataPartTypeIGC.MissileLauncher launcher &&
                Mission!.Core.MissileTypes.TryGetValue(launcher.ExpendableTypeId, out var missile))
            {
                var velocity = Orientation.TimesInverse(TargetVelocity(target));
                float transverse = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y);
                if (transverse < 20 + missile.MaxLock * 30)
                {
                    CommandTarget = target;
                    state |= ShipControlState.MissileFire;
                }
            }
        }
        else if (t <= weapon.GetLifespan() * 0.95f && (WingmanBehaviour & 1) != 0)
            state |= ShipControlState.LeftButton;
        if (angle <= 0.10f && t <= weapon.GetLifespan() * 0.95f) state |= ShipControlState.AllWeapons;
        StateM = (StateM & ShipControlState.SelectedWeaponMask) | state;
    }

    // Igc/shipIGC.cpp:2483-2576: every strict intercept-time and relative-speed
    // boundary is native. Manual thrust buttons coexist with base throttle.
    private int WingmanRangeState(Model target, float t, float life, int previous, double now)
    {
        float own = Orientation.GetForward() * Velocity;
        float theirs = Orientation.GetForward() * TargetVelocity(target);
        float relative = own - theirs;
        int state = 0;
        if (t < life * 0.45f)
        {
            if (relative < -20) state |= ShipControlState.ForwardButton;
            else if (relative > 20) state |= ShipControlState.BackwardButton;
            else Controls = Controls with { Throttle = theirs >= EffectiveMaxSpeed() ? 1 :
                theirs <= 0 ? -1 : 2 * theirs / EffectiveMaxSpeed() - 1 };
        }
        else if (t < life * 0.9f)
        {
            if (relative > 50) state |= ShipControlState.BackwardButton;
            else if (relative < 10)
            {
                state |= ShipControlState.ForwardButton;
                if (relative < -30 || (relative < -15 && (previous & ShipControlState.AfterburnerButton) != 0))
                    state |= ShipControlState.AfterburnerButton;
            }
        }
        else if (t < life * 1.5f)
        {
            if (relative > 120) state |= ShipControlState.BackwardButton;
            else if (relative < 80)
            {
                state |= ShipControlState.ForwardButton;
                if (relative < 20) state |= ShipControlState.AfterburnerButton;
            }
        }
        else if ((WingmanBehaviour & 0x40) != 0 && _timeRanAway + 10 <= now &&
                 FindWingmanPriorityTarget() is { } priority)
        {
            SetWingmanPlanAttack(priority);
            SendDroneChat(DroneInTransitSound, $"Switching target to {priority.Id}");
        }
        else state |= ShipControlState.AfterburnerButton;
        return state;
    }

    // Native Ignore for the no-target ripcord scan (Igc/common.cpp:1648-1682).
    private bool IgnoreRipcordShip(Ship other)
    {
        if (!IsFriendlySide(other.SideId)) return PilotType < PilotTypePlayer;
        if (other.Id < Id && PilotType < PilotTypePlayer && other.PilotType < PilotTypePlayer &&
            (other.StateM & ShipControlState.MiningMask) == 0 && !other.IsRipcordActive) return true;
        return (HullType.HasCapability(DataHullTypeIGC.HabmLifepod) && other.HullType.HasCapability(DataHullTypeIGC.HabmRescue)) ||
            (HullType.HasCapability(DataHullTypeIGC.HabmRescue) && other.HullType.HasCapability(DataHullTypeIGC.HabmLifepod)) ||
            (HullType.HasCapability(DataHullTypeIGC.HabmCarrier) && other.HullType.HasCapability(DataHullTypeIGC.HabmLandOnCarrier));
    }

    // Native getDirection, Igc/common.cpp:1580-1644, for the existing Dodge
    // escape vector. Keep only the dominant axis when it is twice the next.
    private int WingmanDodgeButtons(Vector3 away)
    {
        var local = Orientation.TimesInverse(away);
        Span<float> magnitudes = stackalloc float[] { MathF.Abs(local.X), MathF.Abs(local.Y), MathF.Abs(local.Z) };
        Span<int> buttons = stackalloc int[] {
            local.X >= 0 ? ShipControlState.RightButton : ShipControlState.LeftButton,
            local.Y >= 0 ? ShipControlState.UpButton : ShipControlState.DownButton,
            local.Z <= 0 ? ShipControlState.ForwardButton : ShipControlState.BackwardButton };
        for (int i = 0; i < 2; i++)
            for (int j = i + 1; j < 3; j++)
                if (magnitudes[j] > magnitudes[i])
                {
                    (magnitudes[i], magnitudes[j]) = (magnitudes[j], magnitudes[i]);
                    (buttons[i], buttons[j]) = (buttons[j], buttons[i]);
                }
        return magnitudes[0] > 2 * magnitudes[1] ? buttons[0] :
            magnitudes[0] > 2 * magnitudes[2] ? buttons[0] | buttons[1] : buttons[0] | buttons[1] | buttons[2];
    }

    // Igc/shipIGC.cpp:2249-2260: mining keeps facing the rock, with a roll,
    // kill throttle and no motion/weapon inputs. Only NPC mining owns controls.
    private void ApplyMiningControls(Asteroid rock, float dt)
    {
        var p = rock.Data.Position;
        var direction = new Vector3(p[0], p[1], p[2]) - Position;
        if (direction.LengthSquared() > 0) TurnWingmanToFace(direction.Normalize(), dt, 1);
        Controls = Controls with { Roll = 1, Throttle = -1 };
        StateM &= ShipControlState.SelectedWeaponMask | ShipControlState.MiningMask;
    }
}
