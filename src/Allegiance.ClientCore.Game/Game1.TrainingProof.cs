using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P886/P886b — the training autoproof. A real-window driver (activated only by
/// <c>ALLEGIANCE_TRAIN_PROOF=1</c> alongside <c>ALLEGIANCE_TRAIN_MISSION</c>) that plays Mission 2
/// (Basic Flight) and Mission 3 (Basic Weaponry) to completion by feeding input through the CLIENT'S
/// OWN input path — the same keyboard/mouse state <see cref="Game1.FlightInput"/> reads — instead of
/// calling any training-host/site method to fake an outcome and without skipping a goal.
///
/// P886b — REAL INPUT ONLY. The earlier draft OR'd ship-state bits (weapons/missile/afterburner)
/// straight into the masked control send; that injection is removed. Weapons and missiles are now
/// fired by HOLDING the keys a player holds (Space = FireWeapon, Left Ctrl = FireMissile) so those
/// state bits come from the flight input's own key reading (FlightInput.Translate folds them into
/// the masked stateM). If a gate then fails, that is a real finding — see P886_REPORT.md.
///
/// One narrow, proof-only input seam remains (the task-sanctioned "hook to set input state"):
/// <see cref="_proofKeys"/> — synthetic pressed keys composed into the KeyboardState
/// <c>FlightInput</c> reads each frame (seam <c>ReadProofKeyboard</c>). From there every
/// production interpretation runs unchanged: <c>ActionDown</c> (throttle keys, fire, afterburner,
/// target, autopilot keys) and the <c>TrainingInputGate</c> (routing SPACEBAR/F/B/G to the native
/// TrekKeys the lessons wait on). So pressing SPACEBAR here is indistinguishable, downstream, from a
/// human pressing it.
///
/// Targeting: M3 goal 3 (right mouse) uses the real ORDER_CHANGE pick — the same wire message a
/// pointer right-click issues (Game1.Targeting.CommitTargetForPlayer →
/// Game1.Orders.SendCurrentCommandToServer, WinTrek.cpp:8823-8852); M3 goal 12 (the B key) taps B
/// through the real targeting-key path, which now commits to the server like native
/// (TK_TargetFriendlyBase → SetTarget(m,c_cidDefault), WinTrek.cpp:10149/9024). The final docking
/// leg (M3 goal 13) is FLOWN IN MANUALLY with the steering/throttle keys — native disables the
/// autopilot key during M3 (command view off), so the player (and the proof) fly home by hand.
/// </summary>
public sealed partial class Game1
{
    // ── proof-only input seam ───────────────────────────────────────────────

    /// <summary>Synthetic keys the proof presses this frame; composed into the KeyboardState the
    /// flight input reads. Null = no override (real hardware only).</summary>
    private Keys[]? _proofKeys;

    /// <summary>Whether the proof is active. Read ONCE at startup from ALLEGIANCE_TRAIN_PROOF=1
    /// (P886b fix #4 — the per-frame env read is gone); the proof is inert otherwise.</summary>
    private readonly bool _proofEnabled =
        Environment.GetEnvironmentVariable("ALLEGIANCE_TRAIN_PROOF") == "1";

    // per-frame driver bookkeeping
    private string _proofLastGoal = "";
    private bool _proofLogComplete;
    // A discrete key press is HELD (not a short blip) from when the proof decides to press it until
    // the step it serves advances — robust to the low frame rate of an offscreen render, so the
    // training gate reliably sees the down→up edge and the mission's GetKeyCondition consumes it.
    private long _proofTapStartMs;
    private Keys? _proofTapKey;
    private string _proofTapTriggerGoal = "";
    private bool _proofM3Killing;       // M3: in the shooting/targeting phase
    private bool _proofM3Docking;       // M3: final manual fly-home leg engaged
    private int _proofM3NextDerelict;   // M3: target cursor over 1000..1009
    // prompt-edge counters (messages accumulate on the site; act once per NEW occurrence)
    private int _pSpacebarSeen, _pSpaceKeySeen, _pBackslashSeen, _pWheelSeen,
        _pEnemyKeySeen, _pCenterSeen, _pBaseSeen, _pShotMarkers;
    // M3 manual fly-home (goal 13) bookkeeping
    private bool _proofThrottleSetFull; // the FULL-throttle key was tapped for the landing approach
    private bool _proofSlowDone;        // near the outpost, throttle was eased off for the bay

    /// <summary>Compose the proof's synthetic keys into the real keyboard state — the single choke
    /// point from which <see cref="Game1.FlightInput"/> reads its KeyboardState every frame. When the
    /// proof is inactive this returns the raw hardware state unchanged (no behavioural change).</summary>
    private KeyboardState ReadProofKeyboard()
    {
        var real = Keyboard.GetState();
        if (!_proofEnabled || _proofKeys is not { Length: > 0 } pk)
            return real;
        var set = new HashSet<Keys>(real.GetPressedKeys());
        foreach (var k in pk) set.Add(k);
        var arr = new Keys[set.Count];
        set.CopyTo(arr);
        return new KeyboardState(arr);
    }

    /// <summary>Per-frame driver. Called from <see cref="UpdateTraining"/> every frame (before the
    /// mission pump) so the synthetic input it writes is consumed by the same frame's flight input.
    /// Inert unless ALLEGIANCE_TRAIN_PROOF=1 (read once at startup) and a mission is running.</summary>
    private void ApplyTrainingProof()
    {
        if (!_proofEnabled)
        {
            _proofKeys = null;
            return;
        }

        long now = Environment.TickCount64;
        string goalNow = _trainingSession?.Mission.ActiveGoalPath ?? "";
        var keys = new List<Keys>();

        // A discrete key press (SPACEBAR/F/B/backslash/Enter) is HELD until the step it serves
        // advances — robust to the low frame rate of an offscreen render (a short blip can vanish
        // inside one long frame). The training gate sees the down→up edge and the mission's
        // GetKeyCondition consumes it; the key releases when the goal moves on (or after a generous
        // timeout, so a stuck hold never spins the afterburner/fire forever).
        if (_proofTapKey is { } tk)
        {
            bool slideshowShowing = _trainingSlideshow is not null;
            bool advanced = slideshowShowing ? _trainingSlideshow!.Dismissed
                : (goalNow != _proofTapTriggerGoal && goalNow != "");
            bool timedOut = now - _proofTapStartMs > 8000;
            if (advanced || timedOut) _proofTapKey = null;
            else keys.Add(tk);
        }

        // Slide show (pre-mission): a player advances it with any key — hold ENTER until it is
        // dismissed. Post slideshows are intentionally NOT advanced so the run rests on the
        // post-mission screen P881a uses (the --exit-after bound then exits).
        if (_trainingSlideshow is { } slide)
        {
            if (!slide.Dismissed && !_trainingSlideshowIsPost && _proofTapKey is null)
            {
                ProofTap(Keys.Enter);
                Console.WriteLine("[tproof] slideshow: holding Enter to advance");
            }
            _proofKeys = keys.ToArray();
            return;
        }

        if (_trainingSession is not { } session)
        {
            _proofKeys = keys.ToArray();
            return;
        }

        var mission = session.Mission;
        var site = session.Site as ClientTrainingSite;
        string goal = mission.ActiveGoalPath;

        if (goal != _proofLastGoal)
        {
            _proofLastGoal = goal;
            Console.WriteLine($"[tproof] goal {goal}");
        }
        if (!_proofLogComplete && session.Finished)
        {
            _proofLogComplete = true;
            Console.WriteLine($"[tproof] mission FINISHED (completed={session.Completed}) goal {goal}");
        }

        if (site is null) { _proofKeys = null; return; }

        switch (mission)
        {
            case Mission2:
                DriveMission2(site, keys);
                break;
            case Mission3:
                DriveMission3(site, keys);
                break;
        }

        _proofKeys = keys.ToArray();
    }

    private int ProofCount(ClientTrainingSite site, string fragment)
        => site.Messages.Count(m => m.Contains(fragment, StringComparison.Ordinal));

    private void ProofTap(Keys key)
    {
        _proofTapKey = key;
        _proofTapTriggerGoal = _trainingSession?.Mission.ActiveGoalPath ?? "";
        _proofTapStartMs = Environment.TickCount64;
    }

    /// <summary>Send a real ORDER_CHANGE target pick (the native SetTarget path) to the server ship —
    /// the same wire message a pointer right-click on the object issues (Game1.Targeting.cs:864-871
    /// CommitTargetForPlayer → Game1.Orders.cs:185 SendCurrentCommandToServer → SendCurrentCommand,
    /// WinTrek.cpp:8823-8852). P886b keeps this ONLY for M3 goal 3 (the right-mouse pick, review fix
    /// #5); goal 12 now taps the B key through the real targeting-key path instead.</summary>
    private void ProofOrderTarget(TargetRef t)
    {
        if (_session.World.MyShipId is not { } myId) return;
        var ot = t.Type switch
        {
            ObjectType.Ship => IgcObjectType.Ship,
            ObjectType.Station => IgcObjectType.Station,
            ObjectType.Asteroid => IgcObjectType.Asteroid,
            ObjectType.Probe => IgcObjectType.Probe,
            ObjectType.Buoy => IgcObjectType.Buoy,
            _ => (IgcObjectType)(-1),
        };
        if ((int)ot < 0) return;
        _session.SendOrderChange(myId, OrderChange.CidNone, OrderChange.CmdCurrent, ot, (short)t.Id);
        Console.WriteLine($"[tproof] ORDER_CHANGE target {t.Type} #{t.Id}");
    }

    private void ProofMaybeShot(string marker)
    {
        // Capture a handful of lesson milestones for the report's screenshot evidence.
        if (_pShotMarkers < 8)
        {
            _pShotMarkers++;
            ScheduleAutoshot(-1);
            Console.WriteLine($"[tproof] autoshot at milestone: {marker}");
        }
    }

    // ── Mission 2 — Basic Flight ────────────────────────────────────────────

    private void DriveMission2(ClientTrainingSite site, List<Keys> keys)
    {
        string goal = _trainingSession!.Mission.ActiveGoalPath;

        // Goal 3 & 6: "Press the SPACEBAR" → tap SPACE (FireBooster action → TK_FireBooster(50)).
        int sp = ProofCount(site, "Press the SPACEBAR");
        if (sp > _pSpacebarSeen) { _pSpacebarSeen = sp; ProofTap(Keys.Space); ProofMaybeShot("M2 SPACEBAR"); }

        // Goal 4: "Press BACKSLASH ... for full thrust" → tap BACKSLASH (ThrottleFull) → throttle 1.0.
        int bs = ProofCount(site, "BACKSLASH");
        if (bs > _pBackslashSeen) { _pBackslashSeen = bs; ProofTap(Keys.OemBackslash); ProofMaybeShot("M2 throttle full"); }

        // Goal 5: "Use your scroll wheel ... to adjust thrust" → step the throttle down (not == 1.0).
        int wh = ProofCount(site, "scroll wheel");
        if (wh > _pWheelSeen) { _pWheelSeen = wh; ProofTap(Keys.OemMinus); }

        // Goal 7: "Press and hold the SPACE key to fire the booster" → hold the booster through REAL
        // key input. The FireBooster action is rebound to LeftShift for normal flight; the training
        // gate ALSO recognises its native-default key (SPACEBAR), so holding Space routes
        // TK_FireBooster to the mission AND holding LeftShift sets the applied AfterburnerButton bit
        // through the flight input's own key reading (P886b: no injected bit — FlightInput.Translate
        // folds Afterburner → AfterburnerButton from the held key).
        int bk = ProofCount(site, "hold the SPACE key");
        if (bk > _pSpaceKeySeen) { _pSpaceKeySeen = bk; _proofSpaceHeldM2 = true; ProofMaybeShot("M2 booster"); }
        // Release the booster hold once the scripted demonstration moves on (afterburner bit registered).
        if (_proofSpaceHeldM2 && goal.StartsWith("8", StringComparison.Ordinal))
            _proofSpaceHeldM2 = false;
        if (_proofSpaceHeldM2)
        {
            keys.Add(Keys.LeftShift);   // FireBooster binding → AfterburnerButton bit (real path)
            keys.Add(Keys.Space);       // native default → TK_FireBooster to the mission (gate)
        }

        // Goals 8-9 (autopilot home + dock) are flown by the SCRIPT's SetAutopilotAction; the proof
        // provides no steering — a player only watches the autopilot fly and dock.
    }
    private bool _proofSpaceHeldM2;

    // ── Mission 3 — Basic Weaponry ──────────────────────────────────────────

    private void DriveMission3(ClientTrainingSite site, List<Keys> keys)
    {
        string goal = _trainingSession!.Mission.ActiveGoalPath;

        // Goal 3: "Press the right mouse button to target ... center" → real ORDER_CHANGE to
        // derelict 1000 (the same wire message a pointer right-click sends — review fix #5).
        int cen = ProofCount(site, "right mouse button to target");
        if (cen > _pCenterSeen)
        {
            _pCenterSeen = cen;
            _proofM3Killing = true;
            _proofM3NextDerelict = 1000;
            ProofOrderTarget(new TargetRef(ObjectType.Ship, 1000));
            ProofMaybeShot("M3 target first derelict");
        }
        // Goal 3 & 4: "Press the SPACEBAR" → tap SPACE.
        int sp = ProofCount(site, "Press the SPACEBAR");
        if (sp > _pSpacebarSeen) { _pSpacebarSeen = sp; ProofTap(Keys.Space); }
        // Goal 10 & 11: "Press the SPACE key ..." → tap SPACE.
        int sk = ProofCount(site, "Press the SPACE key");
        if (sk > _pSpaceKeySeen) { _pSpaceKeySeen = sk; ProofTap(Keys.Space); }
        // Goal 7: "Target an enemy using the F key" → tap F (TargetEnemy action → TK_TargetEnemy(80)).
        int ek = ProofCount(site, "Target an enemy using the F key");
        if (ek > _pEnemyKeySeen) { _pEnemyKeySeen = ek; ProofTap(Keys.F); ProofMaybeShot("M3 F target-enemy"); }
        // Goal 12: "Press the B key to target the outpost" → tap B through the real targeting-key
        // path (TK_TargetFriendlyBase). B's selection now commits to the server CommandTarget like
        // native SetTarget (P886b fix — see SelectRemainderTarget), so GetTargetCondition(OT_station,
        // 1010) is met by the real key, not an ORDER_CHANGE.
        int bp = ProofCount(site, "target the outpost.");
        if (bp > _pBaseSeen) { _pBaseSeen = bp; _proofM3Killing = false; ProofTap(Keys.B); ProofMaybeShot("M3 B target outpost"); }

        // Landing leg (goal 13): fly HOME MANUALLY with the steering/throttle keys. Native disables
        // the autopilot key during M3 (command view is off), so a player (and this proof) must fly
        // in by hand. The server's real dock latch ends the lesson.
        if (goal.StartsWith("13", StringComparison.Ordinal) && !_proofM3Docking)
        {
            _proofM3Docking = true;
            _proofM3Killing = false;
            _proofThrottleSetFull = false;
            _proofSlowDone = false;
            Console.WriteLine("[tproof] M3 goal 13: flying home manually (keys)");
            ProofMaybeShot("M3 manual fly-home");
        }
        if (_proofM3Docking)
            DriveManualLanding(keys);

        // P886b — M3 pointing goals that have NO TurnToAction auto-turn (goal 7.9 "face the new
        // target", goal 8.7 "face the next hull") require the PLAYER to turn the nose onto the
        // current target by hand — the proof must steer with the real yaw/pitch keys exactly like a
        // Steer ONLY for these manual-facing goals: during the kill phase
        // the derelicts are static and close, and natural gunfire (plus the missile rain) destroys
        // them reliably without steering — and steering there turned the nose onto a stale reticle
        // pick and stalled kills. TurnToAction-goals (5.3/5.5) override these axes anyway.
        bool pointing7_8 = goal is "7.9" or "8.7";
        if (pointing7_8 && TryGetM3AimTarget(out var aim))
            SteerTowards(aim, keys);

        // Shooting phase: fire only during the actual fire-gate/kill goals (conserves the weapon
        // energy/ammo between the two demonstrations, exactly like the headless driver). Re-target
        // the next live derelict after a kill via real ORDER_CHANGE (native radar re-targets the
        // next same-type object; this is that pick).
        int major = int.TryParse(goal.Split('.')[0], out var m) ? m : 0;
        bool firing = _proofM3Killing && (goal == "5.9" || major is 6 or 8 or 9);

        if (_proofM3Killing && _embeddedHost is { } host && _proofM3NextDerelict >= 1000 && _proofM3NextDerelict <= 1009
            && host.IsDestroyed(_proofM3NextDerelict))
        {
            int next = _proofM3NextDerelict + 1;
            while (next <= 1009 && host.IsDestroyed(next)) next++;
            if (next <= 1009)
            {
                _proofM3NextDerelict = next;
                ProofOrderTarget(new TargetRef(ObjectType.Ship, next));
                ProofMaybeShot($"M3 kill → target {next}");
            }
        }

        if (firing)
        {
            // P886b — fire through REAL key input: hold Space (FireWeapon → WeaponsMask) and Left
            // Ctrl (FireMissile → missileFireIGC bit AND the wire FIRE_MISSILE launch request), both
            // folded by the flight input's own key reading. No injected WeaponsMask/MissileFire.
            keys.Add(Keys.Space);         // FireWeapon → WeaponsMask (the M3 main-gun fire gate)
            keys.Add(Keys.LeftControl);   // FireMissile → MissileFire bit + wire launch (M3 goal 09)
        }
    }

    /// <summary>M3 goal 13 — fly the ship home with real steering/throttle keys. Steers the nose onto
    /// the outpost bearing (yaw/pitch from the replicated pose, exactly the player's manual flight),
    /// holds cruise throttle to approach, and eases off as it closes so it drifts into the dock bay.
    /// The server's real dock latch (radius or InGarage geometry) completes the mission.</summary>
    private void DriveManualLanding(List<Keys> keys)
    {
        if (!TryGetTrainingOutpost(out var target)) return;
        SteerTowards(target, keys);

        if (!TryOwnTrainingPose(out var pos, out _, out _, out _)) return;
        float dist = (target - pos).Length();

        // Cruise throttle: FULL for the approach (one tap sets the persistent throttle), then step
        // it down as we close so the ship glides into the bay instead of blowing past the dock radius.
        if (!_proofThrottleSetFull)
        {
            _proofThrottleSetFull = true;
            ProofTap(Keys.OemBackslash);   // ThrottleFull
            Console.WriteLine($"[tproof] M3 landing: cruise throttle FULL at {dist:F0}m");
        }
        if (!_proofSlowDone && dist < 320f)
        {
            _proofSlowDone = true;
            ProofTap(Keys.OemMinus);       // ThrottleDown (−20%)
            Console.WriteLine($"[tproof] M3 landing: easing throttle at {dist:F0}m");
        }
        // If the dock latch hasn't caught us inside the radius, brake to a crawl and keep the nose on.
        if (dist < 240f && _embeddedHost?.PlayerShip is { } ps && ps.StationId is null && !_proofBrakeDone)
        {
            _proofBrakeDone = true;
            ProofTap(Keys.Back);       // ThrottleZero (brake)
            Console.WriteLine($"[tproof] M3 landing: braking at {dist:F0}m");
        }
    }
    private bool _proofBrakeDone;

    /// <summary>Steer the own ship's nose onto a bearing with the real yaw/pitch keys, from the
    /// replicated pose — the same manual flight a player uses to track a target.</summary>
    private void SteerTowards(System.Numerics.Vector3 target, List<Keys> keys)
    {
        if (!TryOwnTrainingPose(out var pos, out var fwd, out var up, out var right)) return;
        var delta = target - pos;
        float dist = delta.Length();
        if (dist < 1e-3f) return;
        var dn = delta / dist;
        float cosTurn = Math.Clamp(System.Numerics.Vector3.Dot(fwd, dn), -1f, 1f);
        const float deadband = 0.040f;   // ~2.3°
        if (cosTurn > -0.999f)
        {
            var backward = -fwd;
            var twist = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(backward, delta));
            float yawSign = -System.Numerics.Vector3.Dot(twist, up);
            float pitchSign = System.Numerics.Vector3.Dot(twist, right);
            if (yawSign > deadband) keys.Add(Keys.Left);
            else if (yawSign < -deadband) keys.Add(Keys.Right);
            if (pitchSign > deadband) keys.Add(Keys.Up);
            else if (pitchSign < -deadband) keys.Add(Keys.Down);
        }
        else
        {
            keys.Add(Keys.Left);   // target behind — turn to bring it in front
        }
    }

    /// <summary>The current M3 aim target (system coords): the player's selected target (F / B
    /// selections route through _autoTarget), else the next live derelict, else the outpost.</summary>
    private bool TryGetM3AimTarget(out System.Numerics.Vector3 target)
    {
        target = default;
        if (_autoTarget is { } at && ResolveTargetPosition(at, out var p))
        {
            target = ToSys(p);
            return true;
        }
        if (_proofM3NextDerelict is >= 1000 and <= 1009
            && _session.World.Ships.TryGetValue((short)_proofM3NextDerelict, out var ship) && ship.Latest is { } up)
        {
            target = ToSys(up.Position);
            return true;
        }
        return TryGetTrainingOutpost(out target);
    }

    /// <summary>The own ship's replicated pose, in system coords (the same source the training
    /// TurnToFace and the HUD use).</summary>
    private bool TryOwnTrainingPose(out Vector3 pos, out Vector3 fwd, out Vector3 up, out Vector3 right)
    {
        pos = fwd = up = right = default;
        if (!_ownSim.HaveHull) return false;
        var o = _ownSim.Orientation;
        pos = ToSys(_ownSim.Position);
        fwd = ToSys(o.GetForward());
        up = ToSys(o.GetUp());
        right = ToSys(o.GetRight());
        return true;
    }

    /// <summary>The M3 outpost (station 1010) centre in system coords.</summary>
    private bool TryGetTrainingOutpost(out Vector3 target)
    {
        target = default;
        if (_session.World.Stations.TryGetValue(1010, out var st) && st.Position is { Length: 3 } p)
        {
            target = new Vector3(p[0], p[1], p[2]);
            return true;
        }
        return false;
    }
}
