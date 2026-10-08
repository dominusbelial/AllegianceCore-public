using System;
using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.LiveProof;

/// <summary>A one-shot intention the <see cref="AutoProofMachine"/> hands to the
/// client. Because the machine's <see cref="ConsumeDue"/> dequeues each action
/// exactly once, a caller that performs the returned actions faithfully never
/// repeats them (safe to call every frame).</summary>
public readonly record struct AutoAction(AutoActionKind Kind, int IntArg = 0, string StrArg = "", int ShotIndex = -1);

/// <summary>The kinds of automated step a live proof can ask the client to do.
/// Every one is something a real player does; none touch the wire beyond the
/// player-side messages (side request, ready, loadout buy, chat-goto buoy).</summary>
public enum AutoActionKind
{
    SetCamera,      // IntArg unused; StrArg = "chase" | "cockpit"
    HideHud,        // minimal HUD-off toggle so the shot is clean geometry
    PickSide,       // IntArg = side id to request (SendPositionReq)
    SendReady,      // SendPlayerReady(true) once seated
    LaunchHull,     // in the hangar: select StrArg hull (or keep current) + launch
    GotoSector,     // P541 starmap goto to StrArg sector (name or id)
    SelectTarget,   // IntArg = (int)AutoTargetKind — frame nearest of that kind
    Autoshot,       // real-backbuffer screenshot; ShotIndex = in-flight schedule index
    HeadToDock,     // engage autopilot to the home friendly station (server docks us)
    SweepHull,      // docked hangar preview: IntArg = hull step (0 = shot current, >0 = cycle+hshot)
    Suicide,        // P584 — send the suicide (K key) so death/eject HUD art can be photographed
    Exit,           // request the client to exit
}

/// <summary>
/// P572 — the pure automation state machine that turns a parsed
/// <see cref="AutoProofPlan"/> into a time-ordered stream of one-shot
/// <see cref="AutoAction"/>s for a normal windowed client. It owns ONLY the
/// sequencing / timing decisions (phase gating, the after-launch shot schedule,
/// the dock-preview sweep pacing, the dock/exit deadlines) — all of which are
/// pure and unit-testable. The actual game actions (pressing side, sending
/// ready, engaging the autopilot, cycling a hull) are performed by the client
/// when it drains <see cref="ConsumeDue"/> each frame; it can't drift because
/// actions are consumed exactly once.
/// </summary>
public sealed class AutoProofMachine
{
    /// <summary>ShotIndex sentinel for the P584 post-kill autoshot (photographs
    /// the death banner / eject-pod panel right after the deterministic suicide).
    /// Any negative value outside the in-flight schedule range (<c>0..N</c>)
    /// signals "not part of the timed schedule".</summary>
    public const int PostKillShotIndex = -2;

    private readonly AutoProofPlan _plan;
    private readonly Queue<AutoAction> _due = new();

    // observed-world latches (fed by the client)
    private bool _seated;
    private bool _docked;
    private bool _launched;
    private double _now;
    private double? _launchAt;

    // emission latches (ensure one-shot)
    private bool _readyEmitted;
    private bool _launchEmitted;
    private bool _gotoEmitted;
    private bool _targetEmitted;
    private bool _dockHeadingEmitted;
    private bool _sweepStarted;
    private bool _sweepDone;
    private bool _exitEmitted;
    private int _nextSuicideIndex;
    private bool _postKillShotDone;
    private double _lastSuicideSentAt = double.NegativeInfinity;
    private int _shotIndex;
    private int _sweepStep;
    private double _lastSweepAt = double.NegativeInfinity;

    /// <summary>The plan driving this machine (the parsed flags).</summary>
    public AutoProofPlan Plan => _plan;

    /// <summary>Whether the machine believes the local ship is airborne.</summary>
    public bool Launched => _launched;

    /// <summary>Seconds since launch (0 before launch / negative never).</summary>
    public double SecondsSinceLaunch
        => _launched ? Math.Max(0.0, _now - (_launchAt ?? _now)) : 0.0;

    public AutoProofMachine(AutoProofPlan plan)
    {
        _plan = plan;
        // Deterministic initial actions (always available regardless of world).
        _due.Enqueue(new AutoAction(AutoActionKind.SetCamera, StrArg: plan.Camera));
        if (plan.HideHud)
            _due.Enqueue(new AutoAction(AutoActionKind.HideHud));
        if (plan.AutoSide is { } side)
            _due.Enqueue(new AutoAction(AutoActionKind.PickSide, IntArg: side));
    }

    // ── world-observation feeds (called by the client) ────────────────────────

    /// <summary>Advance the machine's clock. Call once per client update.</summary>
    public void Tick(double nowSeconds) => _now = nowSeconds;

    /// <summary>The local ship has been seated (MyShipId set).</summary>
    public void NoteSeated() => _seated = true;

    /// <summary>The client has docked (ShipEntity.StationId != -1).</summary>
    public void NoteDocked()
    {
        _docked = true;
        // Kick off the hangar-preview sweep the first time we are docked with a
        // sweep requested; latch so a later re-dock doesn't restart it.
        if (!_sweepStarted && _plan.DockSweepCount > 0)
        {
            _sweepStarted = true;
            _sweepStep = 0;
            _lastSweepAt = _now;
            _due.Enqueue(new AutoAction(AutoActionKind.SweepHull, IntArg: 0));
        }
    }

    /// <summary>The client has undocked (back to flight).</summary>
    public void NoteUndocked() => _docked = false;

    /// <summary>The client entered flight (mission started / launched from the
    /// hangar). Starts the after-launch clock.</summary>
    public void NoteLaunch()
    {
        if (_launched) return;
        _launched = true;
        _launchAt = _now;
    }

    // ── draining ──────────────────────────────────────────────────────────────

    /// <summary>Return every action that became due since the last drain.
    /// Computes newly-due actions FIRST, then drains, so anything that became
    /// due because of the events fed this frame (seating, docking, the clock)
    /// is returned on this very call. Call each frame; performing each returned
    /// action once is sufficient (they never re-emit).</summary>
    public List<AutoAction> ConsumeDue()
    {
        Compute();   // enqueue anything newly due from the current clock + state
        var due = new List<AutoAction>(_due.Count);
        while (_due.Count > 0)
            due.Add(_due.Dequeue());
        return due;
    }

    /// <summary>Recompute what is newly due from the current clock + world state.</summary>
    private void Compute()
    {
        // Ready, once seated (and only if auto-ready was requested).
        if (_plan.AutoReady && _seated && !_readyEmitted)
        {
            _readyEmitted = true;
            _due.Enqueue(new AutoAction(AutoActionKind.SendReady));
        }

        // A docked ship that never launched: auto-launch fires when docked.
        if (!_launched && _plan.AutoLaunchRequested && _docked && !_launchEmitted)
        {
            _launchEmitted = true;
            _due.Enqueue(new AutoAction(AutoActionKind.LaunchHull, StrArg: _plan.LaunchHull));
        }

        // Pace the dock-preview sweep once it has started (independent of the
        // launch clock — a freshly docked ship can sweep without having flown).
        if (_sweepStarted && !_sweepDone && _docked)
        {
            while (_sweepStep + 1 < _plan.DockSweepCount
                   && _now - _lastSweepAt >= _plan.SweepIntervalSeconds)
            {
                _sweepStep++;
                _lastSweepAt = _now;
                _due.Enqueue(new AutoAction(AutoActionKind.SweepHull, IntArg: _sweepStep));
            }
            if (_sweepStep + 1 >= _plan.DockSweepCount && _now - _lastSweepAt >= _plan.SweepIntervalSeconds)
                _sweepDone = true;
        }

        if (!_launched)
            return;

        double t = SecondsSinceLaunch;

        // Goto + framing target, once after launch.
        if (_plan.GotoSector.Length > 0 && !_gotoEmitted)
        {
            _gotoEmitted = true;
            _due.Enqueue(new AutoAction(AutoActionKind.GotoSector, StrArg: _plan.GotoSector));
        }
        if (_plan.Target != AutoTargetKind.None && !_targetEmitted)
        {
            _targetEmitted = true;
            _due.Enqueue(new AutoAction(AutoActionKind.SelectTarget, IntArg: (int)_plan.Target));
        }

        // Timed after-launch autoshots.
        while (_shotIndex < _plan.AutoshotSeconds.Length
               && t >= _plan.AutoshotSeconds[_shotIndex])
        {
            _due.Enqueue(new AutoAction(AutoActionKind.Autoshot,
                IntArg: (int)(t * 1000.0), ShotIndex: _shotIndex));
            _shotIndex++;
        }

        // P584 — deterministic self-kill(s) (K key) to photograph the death/eject
        // HUD art. Each scheduled suicide fires once (multiple times let a run
        // eject into the pod, then destroy the pod → ghost → death banner), then
        // a real-backbuffer shot is taken once the ghost/pod state has settled
        // (~2.5 s after the last one).
        while (_nextSuicideIndex < _plan.AutoSuicideAt.Length
               && t >= _plan.AutoSuicideAt[_nextSuicideIndex])
        {
            _lastSuicideSentAt = _now;
            _nextSuicideIndex++;
            _due.Enqueue(new AutoAction(AutoActionKind.Suicide));
        }
        if (_nextSuicideIndex > 0 && _nextSuicideIndex >= _plan.AutoSuicideAt.Length
            && !_postKillShotDone && _now - _lastSuicideSentAt >= 2.5)
        {
            _postKillShotDone = true;
            _due.Enqueue(new AutoAction(AutoActionKind.Autoshot,
                IntArg: (int)(t * 1000.0), ShotIndex: PostKillShotIndex));
        }

        // Head to dock at the deadline (only if not already docked).
        if (_plan.AutoDockAfter is { } dockAt
            && t >= dockAt && !_docked && !_dockHeadingEmitted)
        {
            _dockHeadingEmitted = true;
            _due.Enqueue(new AutoAction(AutoActionKind.HeadToDock));
        }

        // Exit deadline.
        if (_plan.ExitAfter is { } exitAt && t >= exitAt && !_exitEmitted)
        {
            _exitEmitted = true;
            _due.Enqueue(new AutoAction(AutoActionKind.Exit));
        }
    }

    /// <summary>Whether the in-flight autoshot at the given index has fired yet —
    /// lets the client choose a stable filename (index+time) even across drains.</summary>
    public bool ShotFired(int index) => index < _shotIndex;
}
