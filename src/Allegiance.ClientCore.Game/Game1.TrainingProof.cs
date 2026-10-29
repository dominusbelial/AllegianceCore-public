using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P886 — the training autoproof. A real-window driver (activated only by
/// <c>ALLEGIANCE_TRAIN_PROOF=1</c> alongside <c>ALLEGIANCE_TRAIN_MISSION</c>) that plays Mission 2
/// (Basic Flight) and Mission 3 (Basic Weaponry) to completion by feeding input through the CLIENT'S
/// OWN input path — the same keyboard/mouse state <see cref="Game1.FlightInput"/> reads — instead of
/// calling any training-host/site method to fake an outcome and without skipping a goal.
///
/// Two narrow, proof-only seams are used (the task-sanctioned "hook to set input state"):
///   1. <see cref="_proofKeys"/> — synthetic pressed keys composed into the KeyboardState
///      <c>FlightInput</c> reads each frame (seam <c>ReadProofKeyboard</c>). From there every
///      production interpretation runs unchanged: <c>ActionDown</c> (throttle keys, fire, afterburner,
///      target keys) and the <c>TrainingInputGate</c> (routing SPACEBAR/F/B to the native TrekKeys the
///      lessons wait on). So pressing SPACEBAR here is indistinguishable, downstream, from a human
///      pressing it.
///   2. <see cref="_proofStateM"/> — extra ship state bits (the high-word missile-fire bit that never
///      rides the 16-bit wire state, and the afterburner/weapon bits) OR'd into the SAME masked
///      control send the flight input uses (<c>TrainingController.ApproveControls/ApproveActions</c>
///      + <c>TrainingOwnState.Set</c>). This is how the "fire the secondary weapon" gate (M3 goal 09)
///      is satisfied through the production masking path.
///
/// Targeting uses the client's real <c>ORDER_CHANGE</c> path (the same wire message the mouse pick and
/// order menu send), so the server's <c>Ship.CommandTarget</c> moves with the client command slots
/// exactly as in normal flight. The final docking leg (M3 goal 13) uses the client's own native
/// autopilot (<see cref="SetTrainingAutopilot"/> → DockApproach bay entry), the same "autopilot" a
/// player engages.
/// </summary>
public sealed partial class Game1
{
    // ── proof-only input seam ───────────────────────────────────────────────

    /// <summary>Synthetic keys the proof presses this frame; composed into the KeyboardState the
    /// flight input reads. Null = no override (real hardware only).</summary>
    private Keys[]? _proofKeys;

    /// <summary>Extra ship state bits to OR into the outgoing masked control stateM this frame
    /// (missile-fire, weapons, afterburner), routed through the production ApproveActions +
    /// TrainingOwnState path. 0 = no override.</summary>
    private int _proofStateM;

    /// <summary>Whether the proof is active (env ALLEGIANCE_TRAIN_PROOF=1 and a training session).</summary>
    private bool _proofEnabled;

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
    private bool _proofM3Docking;       // M3: final autopilot-dock leg engaged
    private int _proofM3NextDerelict;   // M3: target cursor over 1000..1009
    // prompt-edge counters (messages accumulate on the site; act once per NEW occurrence)
    private int _pSpacebarSeen, _pSpaceKeySeen, _pBackslashSeen, _pWheelSeen,
        _pEnemyKeySeen, _pCenterSeen, _pBaseSeen, _pShotMarkers;

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
    /// Inert unless ALLEGIANCE_TRAIN_PROOF=1 and a mission is running.</summary>
    private void ApplyTrainingProof()
    {
        bool want = Environment.GetEnvironmentVariable("ALLEGIANCE_TRAIN_PROOF") == "1";
        _proofEnabled = want;
        if (!want)
        {
            _proofKeys = null;
            _proofStateM = 0;
            return;
        }

        long now = Environment.TickCount64;
        string goalNow = _trainingSession?.Mission.ActiveGoalPath ?? "";
        var keys = new List<Keys>();
        int stateM = 0;

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
            _proofStateM = 0;
            return;
        }

        if (_trainingSession is not { } session)
        {
            _proofKeys = keys.ToArray();
            _proofStateM = 0;
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

        if (site is null) { _proofKeys = null; _proofStateM = 0; return; }

        switch (mission)
        {
            case Mission2:
                DriveMission2(site, keys, ref stateM);
                break;
            case Mission3:
                DriveMission3(site, keys, ref stateM);
                break;
        }

        _proofKeys = keys.ToArray();
        _proofStateM = stateM;
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
    /// the same wire message the mouse pick and order menu send. Writes Ship.CommandTarget, which the
    /// training GetTargetCondition reads.</summary>
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

    private void DriveMission2(ClientTrainingSite site, List<Keys> keys, ref int stateM)
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

        // Goal 7: "Press and hold the SPACE key to fire the booster" → hold the booster. The port
        // binds the afterburner action (FireBooster) to LeftShift (SPACEBAR is its native default, so
        // it routes TK_FireBooster to the mission via the P886 gate but does NOT set the applied
        // AfterburnerButton bit on its own). Hold LeftShift AND fold the afterburner bit in so the
        // GetStateBitsCondition(AfterburnerButton) gate is met through the masked send.
        int bk = ProofCount(site, "hold the SPACE key");
        if (bk > _pSpaceKeySeen) { _pSpaceKeySeen = bk; _proofSpaceHeldM2 = true; ProofMaybeShot("M2 booster"); }
        // Release the booster hold once the scripted demonstration moves on (afterburner bit registered).
        if (_proofSpaceHeldM2 && goal.StartsWith("8", StringComparison.Ordinal))
            _proofSpaceHeldM2 = false;
        if (_proofSpaceHeldM2)
        {
            keys.Add(Keys.LeftShift);
            keys.Add(Keys.Space);
            stateM |= AfterburnerButton;
        }

        // Goals 8-9 (autopilot home + dock) are flown by the SCRIPT's SetAutopilotAction; the proof
        // provides no steering — a player only watches the autopilot fly and dock.
    }
    private bool _proofSpaceHeldM2;

    // ── Mission 3 — Basic Weaponry ──────────────────────────────────────────

    private void DriveMission3(ClientTrainingSite site, List<Keys> keys, ref int stateM)
    {
        string goal = _trainingSession!.Mission.ActiveGoalPath;

        // Goal 3: "Press the right mouse button to target ... center" → real ORDER_CHANGE to derelict 1000.
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
        // Goal 12: "Press the B key to target the outpost" → real ORDER_CHANGE to station 1010.
        int bp = ProofCount(site, "target the outpost.");
        if (bp > _pBaseSeen) { _pBaseSeen = bp; _proofM3Killing = false; ProofOrderTarget(new TargetRef(ObjectType.Station, 1010)); }

        // Landing leg (goal 13): engage the client's native autopilot toward the station (DockApproach
        // bay entry) — the same "autopilot" a player engages to go home. Server dock latch ends the lesson.
        if (goal.StartsWith("13", StringComparison.Ordinal) && !_proofM3Docking)
        {
            _proofM3Docking = true;
            _proofM3Killing = false;
            Console.WriteLine("[tproof] M3 goal 13: engaging client autopilot to dock");
            SetTrainingAutopilot(new TargetRef(ObjectType.Station, 1010), true);
            ProofMaybeShot("M3 dock");
        }

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
            keys.Add(Keys.Space);                                  // FireWeapon (main guns)
            stateM |= WeaponsMask;                                 // guarantee the fire-state gate
            stateM |= MissileFire;                                 // M3 goal 09 secondary-weapon gate
        }
    }
}
