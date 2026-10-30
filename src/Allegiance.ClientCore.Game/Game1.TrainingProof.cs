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
/// Opt-in real-window training driver. Keyboard state and text events enter the production
/// input path; the driver observes goals and replicated geometry and never sets lesson outcomes.
/// Target-center/F/B pick targets through the normal client handlers. Flight, gunfire, cloak,
/// order acceptance and manual bay entry use player keys. See P892_REPORT.md for receipts.
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
    private const float _proofKillDeadband = 2.0f; // P886c: stop turning once within ~2° of target
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
        // post-mission screen P881a uses, then exit the proof after a short capture interval.
        if (_trainingSlideshow is { } slide)
        {
            if (_trainingSlideshowIsPost)
            {
                if (_proofPostAt == 0) _proofPostAt = now;
                if (now - _proofPostAt > 2000) { Console.WriteLine("[tproof] post slideshow reached; exiting proof"); Exit(); }
            }
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
            case Mission4:
                DriveMission4(site, keys);
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

    private long _proofPostAt;
    private int _proofAcceptSeen, _proofCloakSeen, _proofM4SpaceSeen;
    private long _proofChatAt;
    private string _proofM4LastActionGoal = "";
    private long _proofStateAt;

    // Mission 4 uses the player's keyboard and text-input events. Scripted orders/autopilot
    // remain mission actions (mission4.cpp:333-372); the proof never calls those actions.
    private void DriveMission4(ClientTrainingSite site, List<Keys> keys)
    {
        string goal = _trainingSession!.Mission.ActiveGoalPath;
        long now = Environment.TickCount64;
        if (now >= _proofStateAt)
        {
            _proofStateAt = now + 5000;
            Console.WriteLine($"[tproof] M4 state goal={goal} sector={OwnSectorIdOr(-1)} pos={_ownSim.Position} target={_autoTarget} queued={_session.World.MyQueuedCommand} autopilot={_autoPilotEngaged} cloak={_cloakToggled} hostCloaking={_embeddedHost?.PlayerShip.Cloaking:F3}");
        }
        if (goal == "2.3" && now >= _proofChatAt)
        {
            _proofChatAt = now + 350;
            if (IsBriefingVisible(out _) || !_chatActive) keys.Add(Keys.Enter);
            else if (_chatTarget != ChatTarget.Everyone) keys.Add(Keys.Tab);
            else if (_chatBuffer.Length == 0)
                OnTextInput(this, new Microsoft.Xna.Framework.TextInputEventArgs('x', Keys.X));
            else keys.Add(Keys.Enter);
        }
        int space = ProofCount(site, "Press the SPACEBAR");
        if (space > _proofM4SpaceSeen) { _proofM4SpaceSeen = space; ProofTap(Keys.Space); }
        int accept = ProofCount(site, "key to accept the command.");
        if (accept > _proofAcceptSeen)
        { _proofAcceptSeen = accept; ProofTap(Keys.OemOpenBrackets); ProofMaybeShot("M4 accept command"); }
        int cloak = ProofCount(site, "G key to activate your cloak");
        if (cloak > _proofCloakSeen)
        { _proofCloakSeen = cloak; ProofTap(Keys.G); ProofMaybeShot("M4 cloak G"); }
        if (goal == "5.5" && site.Resolve(new TargetRef(ObjectType.Warp, 1030)) is { } warp)
        {
            SteerTowards(warp.Position, keys);
            if (_proofM4LastActionGoal != goal)
            { _proofM4LastActionGoal = goal; ProofTap(Keys.OemBackslash); }
        }
        if (goal.StartsWith("7.", StringComparison.Ordinal) && ProofCount(site, "Target an enemy miner") > 0
            && _proofM4LastActionGoal != "miner")
        { _proofM4LastActionGoal = "miner"; ProofTap(Keys.F); }
        if (goal is "7.7" or "8.7")
        {
            keys.Add(Keys.Back); // kill the previous cruise setting while using held approach thrust
            if (_cloakToggled) keys.Add(Keys.G);
            if (_autoTarget is { } target && ResolveTargetPosition(target, out var aim))
            {
                var aimPoint = ToSys(aim);
                if (target.Kind == TargetKind.Ship && TryGatherLeadScene(Allegiance.Core.IgcObjectType.Ship, target.Id, out var scene))
                {
                    var lead = Allegiance.ClientCore.MonoGame.Rendering.ClientLead.Solve(scene.Input);
                    if (lead.GatePassed) aimPoint = ToSys(_ownSim.Position + lead.Direction * 1000f);
                }
                SteerTowards(aimPoint, keys, .01f);
                if (OwnAimDegreesTo(aimPoint) < 2f) keys.Add(Keys.Space);
                if ((_ownSim.Position - aim).LengthSquared() > 700f * 700f) keys.Add(Keys.W);
            }
        }
        if (goal.StartsWith("8.", StringComparison.Ordinal)
            && _session.World.MyAcceptedCommand is { TargetType: IgcObjectType.Ship, TargetId: 1105 })
        {
            // A miner may carry us through its aleph just before the constructor order arrives.
            // Return to the lesson's Mars sector even while the remote constructor has no pose.
            keys.Add(Keys.Back);
            Vector3? escortTarget = null;
            bool routing = OwnSectorIdOr(-1) != 1031;
            if (routing && ResolveTargetPosition(new AutopilotTarget(TargetKind.Cluster, 1031, "Mars"), out var route))
                escortTarget = ToSys(route);
            else if (!routing && _session.World.Ships.TryGetValue(1105, out var builder) && builder.Latest is { } bp)
                escortTarget = ToSys(bp.Position);
            if (escortTarget is { } destination)
            {
                SteerTowards(destination, keys);
                if (OwnAimDegreesTo(destination) < 15f && TryOwnTrainingPose(out var escortPosition, out _, out _, out _)
                    && (routing || (destination - escortPosition).LengthSquared() > 600f * 600f)) keys.Add(Keys.W);
            }
        }
        if (goal.StartsWith("10.", StringComparison.Ordinal))
        {
            if (_proofM4LastActionGoal != "land")
            {
                _proofM4LastActionGoal = "land";
                _proofLandingStationId = _session.World.Stations.Values
                    .Where(st => st.ClusterId == OwnSectorIdOr(-1) && st.SideId == 0)
                    .Select(st => (int)st.StationId).FirstOrDefault();
                _proofThrottleSetFull = false; _proofSlowDone = false; _proofBrakeDone = false;
                ProofTap(Keys.B);
            }
            // Native permits manual landing; use the same key-only bay approach as M3.
            DriveManualLanding(keys);
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
        long nowMs = Environment.TickCount64;

        // Goal 3: use the ordinary target-center key for the hull under the reticle.
        // It reaches the same SetTarget path as the prompted right mouse pick.
        int cen = ProofCount(site, "right mouse button to target");
        if (cen > _pCenterSeen)
        {
            _pCenterSeen = cen;
            _proofM3Killing = true;
            _proofM3NextDerelict = 1000;
            ProofTap(Keys.OemQuotes); // real target-center key
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
        //
        // P892b — root-caused and fixed: the port's shipped default map leaves TargetFriendlyBase
        // unbound (B is rebound to Scoreboard), so a real-window B tap never reached
        // SelectRemainderTarget → CommitTargetForPlayer (zero [p642] edges, P886c). HandleRemainder
        // TargetKeys now ALSO honours the action's native-default key while a training mission runs
        // (RemainderTargetKeyDown, Game1.TargetCommands.cs) — so B selects and commits station 1010 by
        // itself; there is no direct target/order workaround.
        int bp = ProofCount(site, "target the outpost.");
        if (bp > _pBaseSeen)
        {
            _pBaseSeen = bp; _proofM3Killing = false;
            ProofTap(Keys.B);
            ProofMaybeShot("M3 B target outpost");
        }

        // Landing leg (goal 13): fly HOME MANUALLY with the steering/throttle keys. Native disables
        // the autopilot key during M3 (command view is off), so a player (and this proof) must fly
        // in by hand. The server's real dock latch ends the lesson.
        if (goal.StartsWith("13", StringComparison.Ordinal) && !_proofM3Docking)
        {
            Console.WriteLine($"[tproof] M3 B result: host current target={_embeddedHost?.GetCommandTarget(_embeddedHost.PlayerShipId)}");
            _proofM3Docking = true;
            _proofM3Killing = false;
            _proofThrottleSetFull = false;
            _proofSlowDone = false;
            Console.WriteLine($"[tproof] M3 goal 13: flying home manually (keys); survivors={_embeddedHost?.Drones.Values.Count(d => !d.Dead && d.Fraction > 0 && d.Sector is not null)}");
            ProofMaybeShot("M3 manual fly-home");
        }
        if (_proofM3Docking)
            DriveManualLanding(keys);

        // P886b — M3 pointing goals that have NO TurnToAction auto-turn (goal 7.9 "face the new
        // target", goal 8.7 "face the next hull") require the PLAYER to turn the nose onto the
        // current target by hand — the proof must steer with the real yaw/pitch keys exactly like a
        // player.
        //
        // Track kill derelicts with the same local pose the player sees. P890
        // restores that pose's authority at the host, including training's origin.
        // Keep a small deadband and burst discipline for the low-frame-rate proof.
        bool pointing7_8 = goal is "7.9" or "8.7";
        int kmajor = int.TryParse(goal.Split('.')[0], out var km) ? km : 0;
        bool killPhase = _proofM3Killing && (kmajor is 6 or 8 or 9);
        if (pointing7_8 && TryGetM3AimTarget(out var aimPt))
            SteerTowards(aimPt, keys);
        else if (killPhase && TryGetKillDerelictPosition(out var kpos))
        {
            if (OwnAimDegreesTo(kpos) > _proofKillDeadband)
                SteerTowards(kpos, keys, deadband: .025f);
        }

        // Shooting phase: fire only during the actual fire-gate/kill goals (conserves the weapon
        // energy/ammo between the two demonstrations, exactly like the headless driver). Re-target
        // the next live derelict after a kill with the native F targeting key.
        int major = int.TryParse(goal.Split('.')[0], out var m) ? m : 0;
        // Proof policy: preserve the 600 ms fire / 300 ms rest duty cycle to
        // conserve resources while steering through the real keyboard path.
        // P890 corrects pose authority; the gun does not apply a recoil impulse.
        // Goal 5.9 only needs a WeaponsMask once, so that demonstration always fires.
        bool inKillGoal = _proofM3Killing && (major is 6 or 8 or 9);
        bool isFireDemo = _proofM3Killing && goal == "5.9";
        long burstPhase = nowMs % 900;
        bool burstOn = burstPhase < 600;
        bool firing = isFireDemo || (inKillGoal && burstOn);

        if (_autoTarget is { Kind: TargetKind.Ship, Id: >= 1000 and <= 1009 } selected)
            _proofM3NextDerelict = selected.Id;
        if (_proofM3Killing && _embeddedHost is { } host && _proofM3NextDerelict >= 1000 && _proofM3NextDerelict <= 1009
            && host.IsDestroyed(_proofM3NextDerelict))
        {
            int next = _proofM3NextDerelict + 1;
            while (next <= 1009 && host.IsDestroyed(next)) next++;
            if (next <= 1009)
            {
                _proofM3NextDerelict = next;
                ProofTap(Keys.F); // native target-enemy selection
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
    /// the outpost bearing (yaw/pitch from the local pose, exactly the player's manual flight),
    /// holds cruise throttle to approach, and eases off as it closes so it drifts into the dock bay.
    /// The server's real dock latch (radius or InGarage geometry) completes the mission.</summary>
    private void DriveManualLanding(List<Keys> keys)
    {
        if (!TryGetTrainingOutpost(out var target)) return;
        // Aim for an actual green bay, rather than the station centre. Resolve
        // its existing art geometry just as the ordinary dock planner does
        // (Igc/common.cpp:1532-1536); this proof still supplies only player keys.
        if (_proofDockApproach is null && TryOwnTrainingPose(out var own, out _, out _, out _))
        {
            lock (_session.Gate)
            {
                if (_session.World.Stations.TryGetValue((short)_proofLandingStationId, out var station)
                    && _shopCatalog?.StationTypes is { } types
                    && types.TryGetValue(station.StationTypeId, out var type)
                    && StationDockGeometry(type) is { LandSlots.Count: > 0 } geom)
                {
                    var orientation = new Orientation();
                    if (station.Forward is { Length: 3 } f && station.Up is { Length: 3 } u)
                        orientation.Set(Vec(f), Vec(u));
                    var bay = geom.LandSlots.Select(slot => slot.Planes[0])
                        .Select(plane => (Center: target + ToSys(plane.Position * orientation),
                            Direction: ToSys(plane.Direction.Normalize() * orientation)))
                        .OrderBy(b => (b.Center + b.Direction * (type.Radius + 200) - own).LengthSquared()).First();
                    _proofDockApproach = bay.Center + bay.Direction * (type.Radius + 200);
                    _proofDockEntry = bay.Center - bay.Direction * 100;
                    Console.WriteLine($"[tproof] M3 landing: green-bay approach {_proofDockApproach} entry {_proofDockEntry}");
                }
            }
        }
        if (_proofDockApproach is { } approach && TryOwnTrainingPose(out var current, out _, out _, out _))
        {
            if (!_proofDockLinedUp && (approach - current).Length() < 35)
            {
                _proofDockLinedUp = true;
                ProofTap(Keys.Back);
                Console.WriteLine("[tproof] M3 landing: lined up outside green bay; entering with held thrust");
            }
            target = _proofDockLinedUp ? _proofDockEntry : approach;
        }
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
        // ThrottleZero actually stops; it does not select a crawl. Continue
        // with the player's held-thrust key after braking, so the proof cannot
        // park just outside the real hangar/docking gate indefinitely.
        if (_proofBrakeDone && _ownSim.Velocity.Length() < 12f && OwnAimDegreesTo(target) < 10f)
            keys.Add(Keys.W);
    }
    private int _proofLandingStationId = 1010;
    private bool _proofBrakeDone;
    private Vector3? _proofDockApproach;
    private Vector3 _proofDockEntry;
    private bool _proofDockLinedUp;

    /// <summary>Aim error from the same local pose used by the camera and training gates.</summary>
    private float OwnAimDegreesTo(System.Numerics.Vector3 target)
    {
        if (!TryOwnTrainingPose(out var pos, out var fwd, out _, out _)) return 999f;
        var delta = target - pos;
        float dist = delta.Length();
        if (dist < 1e-3f) return 0f;
        return MathF.Acos(Math.Clamp(System.Numerics.Vector3.Dot(fwd, delta / dist), -1f, 1f)) * 57.29578f;
    }

    /// <summary>Steer the own ship's nose onto a bearing with the real yaw/pitch keys, from the
    /// local pose — the same manual flight a player uses to track a target.</summary>
    private void SteerTowards(System.Numerics.Vector3 target, List<Keys> keys, float deadband = .040f)
    {
        if (!TryOwnTrainingPose(out var pos, out var fwd, out var up, out var right)) return;
        var delta = target - pos;
        float dist = delta.Length();
        if (dist < 1e-3f) return;
        var dn = delta / dist;
        float cosTurn = Math.Clamp(System.Numerics.Vector3.Dot(fwd, dn), -1f, 1f);
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

    /// <summary>The own ship's local simulated pose, in system coords (the same source the training
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

    /// <summary>The current M3 kill derelict's centre in system coords — the straight-firing
    /// main-gun/machine-gun aim point the proof keeps the nose on during the kill goals.</summary>
    private bool TryGetKillDerelictPosition(out Vector3 target)
    {
        target = default;
        if (_autoTarget is { Kind: TargetKind.Ship } at && ResolveTargetPosition(at, out var picked))
        { target = ToSys(picked); return true; }
        return false;
    }

    /// <summary>The M3 outpost (station 1010) centre in system coords.</summary>
    private bool TryGetTrainingOutpost(out Vector3 target)
    {
        target = default;
        if (_session.World.Stations.TryGetValue((short)_proofLandingStationId, out var st) && st.Position is { Length: 3 } p)
        {
            target = new Vector3(p[0], p[1], p[2]);
            return true;
        }
        return false;
    }
}
