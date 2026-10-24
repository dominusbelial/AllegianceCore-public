using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P572 — the client-side automation glue that turns the pure
/// <see cref="LiveProof.AutoProofMachine"/> (sequencing + timing) into real
/// game actions in a NORMAL WINDOWED client. Partial class sharing the
/// <c>Game1</c> type. Nothing here is off by default, and everything is
/// client-only: requesting a side, toggling ready, buying a loadout, issuing a
/// chat-goto buoy and engaging the client's own autopilot are exactly the
/// player actions a human would take to frame a live proof.
/// </summary>
public sealed partial class Game1
{
    // ── per-frame feed + drain (called from Update before any handler) ────────

    /// <summary>Feed the world observations into the machine, advance its clock,
    /// and route every action that came due into the per-purpose pending slots
    /// the state handlers consume. Actions are consumed from the machine exactly
    /// once, so the same shot / goto / step never re-fires.</summary>
    private void FeedAutoProofObservations()
    {
        if (_auto is null)
            return;

        double nowSec = Environment.TickCount64 / 1000.0;
        bool airborne = false;

        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is not null)
                _auto.NoteSeated();
            bool docked = world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var ent) && ent.StationId != -1;
            if (docked) _auto.NoteDocked();
            else _auto.NoteUndocked();
            if (_state == ClientState.Flying)
                _auto.NoteLaunch();
            airborne = _state == ClientState.Flying && !docked;
        }

        _auto.Tick(nowSec);

        // HUD-4b — --auto-graphics-settings: keep the settings panel open from
        // the first AIRBORNE frame (drawn in EndDraw, so it must already be
        // visible on the frame BEFORE the autoshot flush). Gated on the own
        // ship's StationId == -1 so the modal panel does not swallow the docked
        // hangar/undock dispatch that UpdateGraphicsSettings would otherwise
        // skip (it early-returns the whole Update while visible).
        if (_auto.Plan.AutoGraphicsSettings && airborne)
            _graphicsSettingsVisible = true;

        // P612 — drive the spectator cameras (--camera freecam/orbit) once the
        // ship is airborne so the timed autoshots capture moving frames.
        DriveAutoProofSpectator(0.033);

        foreach (var a in _auto.ConsumeDue())
        {
            switch (a.Kind)
            {
                case LiveProof.AutoActionKind.SetCamera:
                case LiveProof.AutoActionKind.HideHud:
                    // Applied once in the constructor (camera + HUD are static
                    // for the run); nothing further to do.
                    break;
                case LiveProof.AutoActionKind.SetRadarMode:
                    // P786 — pin the radar LOD for a real-window proof frame
                    // (the FLIGHTKEYS proof cycles modes only right after
                    // launch, before a --auto-goto flight arrives).
                    _radarMode = a.StrArg.ToLowerInvariant() switch
                    {
                        "all" => RadarMode.All,
                        "target" => RadarMode.Target,
                        "minimal" or "none" => RadarMode.Minimal,
                        _ => RadarMode.Default,
                    };
                    Console.WriteLine($"[autoproof] radar mode → {_radarMode}");
                    break;
                case LiveProof.AutoActionKind.PickSide:
                    // Wired directly from the plan in UpdateSideSelect (roster
                    // readiness is its own gate); nothing to buffer here.
                    break;
                case LiveProof.AutoActionKind.SendReady:
                    // Wired directly from the plan in UpdateReadyCheck (seating
                    // is its own gate); nothing to buffer here.
                    break;
                case LiveProof.AutoActionKind.LaunchHull:
                    // Launched directly from the plan in HandleDockedScreen
                    // (ApplyAutoProofDocked → TryAutoLaunch) once docked; the
                    // machine's emission is the sequencing signal, not the action.
                    break;
                case LiveProof.AutoActionKind.GotoSector:
                    _pendingGotoSector = a.StrArg;
                    break;
                case LiveProof.AutoActionKind.SelectTarget:
                    _pendingTarget = (LiveProof.AutoTargetKind)a.IntArg;
                    break;
                case LiveProof.AutoActionKind.TargetClass:
                    // P623 — frame a CLASS-filtered target once airborne
                    // (proves the class bracket in a real window).
                    _pendingTargetClass = a.StrArg;
                    break;
                case LiveProof.AutoActionKind.Autoshot:
                    ScheduleAutoshot(a.ShotIndex);
                    break;
                case LiveProof.AutoActionKind.HeadToDock:
                    _pendingHeadToDock = true;
                    break;
                case LiveProof.AutoActionKind.Suicide:
                    // P584 — the deterministic self-kill (the client's own K-key
                    // handler sends the same SendSuicide; this is that path on a
                    // timer so the death/eject HUD art can be photographed).
                    _session.SendSuicide();
                    Console.WriteLine($"[autoproof] auto-suicide at t={_auto?.SecondsSinceLaunch:0.0}s");
                    break;
                case LiveProof.AutoActionKind.SweepHull:
                    _pendingSweepStep = a.IntArg;
                    break;
                case LiveProof.AutoActionKind.Exit:
                    _pendingExit = true;
                    break;
            }
        }

        if (_pendingExit)
        {
            _pendingExit = false;
            Console.WriteLine("[autoproof] --exit-after reached; exiting");
            Exit();
        }
    }

    private string? _pendingGotoSector;
    private LiveProof.AutoTargetKind? _pendingTarget;
    private long _pendingTargetRetryAtMs;
    private string? _pendingTargetClass;   // P623 — "rel:cls[:dir]" class target spec
    private bool _pendingHeadToDock;
    private bool _pendingExit;

    // ── side select ───────────────────────────────────────────────────────────

    /// <summary>--auto-side: request the side once the selectable roster is
    /// known (the server only accepts POSITIONREQ against a listed active side).
    /// No fallback: with the port's side-select auto-seat off (the default,
    /// matching native), a client that never sends POSITIONREQ stays at side
    /// select — pass --auto-side for unattended proofs.</summary>
    private void ApplyAutoProofSideSelect()
    {
        if (_autoPlan.AutoSide is not { } wantSide || _autoSideDone)
            return;
        var selectable = Rendering.SideSelectLayout.SelectableSideIds(
            _session.World.SideRoster, _session.World.IsSideActive);
        if (selectable.Contains((short)wantSide))
        {
            _autoSideDone = true;
            _session.SendPositionReq((short)wantSide);
            Console.WriteLine($"[autoproof] auto-side -> side {wantSide}");
        }
    }

    // ── ready check ───────────────────────────────────────────────────────────

    /// <summary>--auto-ready: send PLAYER_READY(true) once seated.</summary>
    private void ApplyAutoProofReadyCheck()
    {
        if (!_autoPlan.AutoReady || _autoReadyDone)
            return;
        if (_session.World.MyShipId is { } pid)
        {
            _autoReadyDone = true;
            _session.SendPlayerReady(true, pid);
            Console.WriteLine($"[autoproof] auto-ready PLAYER_READY ship {pid}");
        }
    }

    // ── docked (hangar) ───────────────────────────────────────────────────────

    /// <summary>--auto-launch + the dock-preview sweep, applied inside
    /// HandleDockedScreen's shop branch (so the real ShopModel + BuyLoadout are
    /// used). Launch selects the requested hull (blank = keep the current hull)
    /// and sends BUY_LOADOUT(fLaunch=true); the sweep steps through hull
    /// previews with a real-backbuffer shot each.</summary>
    private void ApplyAutoProofDocked(Rendering.ShopModel shop)
    {
        if (!_launchSent)
        {
            if (_autoPlan.AutoLaunchRequested && !_autoLaunchApplied)
            {
                _autoLaunchApplied = true;
                TryAutoLaunch(shop);
                return;
            }
            if (_pendingSweepStep is { } step)
            {
                _pendingSweepStep = null;
                DoDockSweepStep(step, shop);
            }
        }
    }

    private void TryAutoLaunch(Rendering.ShopModel shop)
    {
        // Select the requested hull (empty = keep the current hull + parts).
        if (_autoPlan.LaunchHull.Length > 0)
        {
            short? target = ResolveHullId(_autoPlan.LaunchHull, shop);
            if (target is { } th && th != shop.EffectiveHullId)
            {
                // Cycle through the buyable list until EffectiveHullId == target
                // (CycleHull wraps; the loop is bounded by the list length).
                for (int guard = 0; guard <= shop.BuyableHulls.Count && shop.EffectiveHullId != th; guard++)
                    shop.CycleHull(true);
            }
        }
        if (!shop.TryLaunch())
        {
            Console.WriteLine($"[autoproof] auto-launch BLOCKED: {shop.BlockReason}");
            return;
        }
        _session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true, parts: shop.ChosenParts);
        _launchSent = true;
        _throttle = FlightInput.DefaultThrottle;
        Console.WriteLine($"[autoproof] auto-launch BUY_LOADOUT(fLaunch) hull {shop.EffectiveHullId} + {shop.ChosenParts.Count} part(s)");
    }

    private void DoDockSweepStep(int step, Rendering.ShopModel shop)
    {
        // Step 0 = shot the current preview; step > 0 = cycle to the next hull
        // and shot it. Cycled hulls rebase the loadout, exactly like the human
        // Q/E hull-cycle keys.
        if (step > 0)
        {
            shop.CycleHull(true);
            Console.WriteLine($"[autoproof] dock sweep hull {step}: {shop.EffectiveHullId}");
        }
        ScheduleAutoshot(-1, dockSweepStep: step);
    }

    private short? ResolveHullId(string token, Rendering.ShopModel shop)
    {
        if (LiveProof.AutoProofPlan.IsNumericToken(token)
            && short.TryParse(token, out var num))
            return num;
        // Match by name (case-insensitive) against the offered hulls.
        foreach (var h in shop.BuyableHulls)
            if (string.Equals(h.Name, token, StringComparison.OrdinalIgnoreCase))
                return h.HullId;
        Console.WriteLine($"[autoproof] --auto-launch hull '{token}' not found in the offered catalogue — keeping the current hull");
        return null;
    }

    // ── flying ────────────────────────────────────────────────────────────────

    /// <summary>--auto-goto / --auto-target / --auto-dock-after, applied in
    /// UpdateFlying (only meaningful airborne, after the hangar branch).</summary>
    private int _autoFlightPartsStage;
    private void ApplyAutoFlightParts()
    {
        double time = _auto?.SecondsSinceLaunch ?? 0;
        var inventory = OwnInventory();
        if (_autoFlightPartsStage == 0 && time >= 1 && inventory.Any(p => p.MountId == 0))
        {
            ToggleFixedWeapon(0);
            _autoFlightPartsStage = 1;
        }
        if (_autoFlightPartsStage == 1 && time >= 3)
        {
            var weapon = inventory.FirstOrDefault(p => p.MountId == 0 && ResolveFlightPart(p.PartId)?.EquipmentType == Allegiance.Core.EquipmentType.Weapon);
            if (weapon.PartId == 0) return;
            _inventorySelection = weapon;
            ChangeFlightPart(Allegiance.Core.FlightPartAction.Unmount);
            _autoFlightPartsStage = 2;
        }
        if (_autoFlightPartsStage == 2 && time >= 5)
        {
            var spare = inventory.FirstOrDefault(p => p.MountId >= 128 && ResolveFlightPart(p.PartId)?.EquipmentType == Allegiance.Core.EquipmentType.Weapon);
            if (spare.PartId == 0) return; // wait for real guaranteed echo
            _inventorySelection = spare;
            ChangeFlightPart(Allegiance.Core.FlightPartAction.Mount);
            _autoFlightPartsStage = 3;
        }
        if (_autoFlightPartsStage == 3 && time >= 7 && inventory.Any(p => p.MountId == 0 && ResolveFlightPart(p.PartId)?.EquipmentType == Allegiance.Core.EquipmentType.Weapon))
        {
            Console.WriteLine("[p641] in-flight mount CONFIRMED by server echo");
            _autoFlightPartsStage = 4;
        }
        if (_autoFlightPartsStage == 4 && time >= 10) { _boosterToggled = true; _autoFlightPartsStage = 5; }
        if (_autoFlightPartsStage == 5 && time >= 13) { _boosterToggled = false; _autoFlightPartsStage = 6; }
    }

    private bool _autoCommandOrientationReady;
    private bool _autoCommandSubjectReady;
    private int _autoParityStage;
    private void ApplyAutoParityCommands()
    {
        double seconds = _auto?.SecondsSinceLaunch ?? 0;
        if (_autoParityStage == 0 && seconds >= 1)
        {
            SelectRemainderTarget("TargetWarp", StepDirection.Nearest);
            if (_autoTarget is not { Kind: TargetKind.Warp } warp) return;
            EngageAutopilotToward(warp, "p642 warp frame:");
            _autoParityStage = 1;
        }
        if (_autoParityStage == 1 && seconds >= 6) { ApplyConsoleMode("ConModeInventory"); _autoParityStage = 2; }
        if (_autoParityStage == 2 && seconds >= 10) { ApplyConsoleMode("ConModeInventory"); ApplyConsoleMode("ConModeGameState"); _autoParityStage = 3; }
        if (_autoParityStage == 3 && seconds >= 14) { ApplyConsoleMode("ConModeMiniTeam"); _autoParityStage = 4; }
        if (_autoParityStage == 4 && seconds >= 18) { ApplyConsoleMode("TeamPane"); _autoParityStage = 5; }
        if (_autoParityStage == 5 && seconds >= 22) { ApplyConsoleMode("ConModeCombat"); _autoParityStage = 6; }
        if (_autoParityStage == 6 && seconds >= 26) { ApplyConsoleMode("ViewExternal"); _autoParityStage = 7; }
        if (_autoParityStage == 7 && seconds >= 30)
        {
            _flightZoom.Step(.5f, true, false, false);
            Console.WriteLine($"[p642] external zoom={_flightZoom.ExternalDistance}");
            _autoParityStage = 8;
        }
        if (_autoParityStage == 8 && seconds >= 34)
        {
            ApplyConsoleMode("ViewCommand");
            _cameraMode = CameraMode.Command;
            _commandCentered = false;
            _commandGridVisible = true;
            _autoParityStage = 9;
        }
        if (_autoParityStage == 9 && seconds >= 38) { ToggleCommandGrid(); _autoParityStage = 10; }
        if (_autoParityStage == 10 && seconds >= 42) { ToggleMainMenu(); _autoParityStage = 11; }
    }
    private bool _autoHighlightSent;
    private bool _autoHighlightConfirmed;
    private short _autoHighlightId;
    private bool _autoInventoryOpened;
    private void ApplyAutoProofFlying()
    {
        if (_autoPlan.AutoFlightParts) ApplyAutoFlightParts();
        if (_autoPlan.AutoParityCommands) ApplyAutoParityCommands();
        // HUD-7 — open the native flight equipment/inventory pane (F4) once,
        // then leave it open for the scheduled autoshots.
        if (_autoPlan.AutoInventory && !_autoInventoryOpened && (_auto?.SecondsSinceLaunch ?? 0) >= 1)
        {
            ApplyConsoleMode("ConModeInventory");
            _autoInventoryOpened = true;
        }
        if (!_autoCommandOrientationReady && _autoPlan.Camera is "command" or "commandtd")
        {
            _commandCamera.TopDown = _autoPlan.Camera == "commandtd";
            _autoCommandOrientationReady = true;
            Microsoft.Xna.Framework.Input.Mouse.SetPosition(GraphicsDevice.Viewport.Width / 2, GraphicsDevice.Viewport.Height / 2);
        }
        if (!_autoCommandSubjectReady && _autoPlan.Camera is "dronefollow" or "dronecockpit")
        {
            if (_commandee is null) SelectCommandee(false);
            if (TryCommandedPose(out _, out _, out _))
            {
                _cameraMode = _autoPlan.Camera == "dronefollow" ? CameraMode.DroneFollow : CameraMode.DroneCockpit;
                _autoCommandSubjectReady = true;
                Console.WriteLine($"[autoproof] commanded follow subject={_commandee?.Id} camera={_cameraMode}");
            }
        }
        if (!_autoHighlightSent && _autoPlan.AutoHighlightSector.Length > 0)
        {
            short? id = _autoPlan.AutoHighlightSector == "current" ? CurrentOwnSector() : ResolveSectorId(_autoPlan.AutoHighlightSector);
            if (id is { } sector)
            {
                _session.SendHighlightCluster(sector, true);
                _autoHighlightSent = true;
                _autoHighlightId = sector;
                _minimapExpanded = true;
                Console.WriteLine($"[autoproof] sent HIGHLIGHT_CLUSTER sector={sector}; awaiting real server echo");
            }
        }
        if (_autoHighlightSent && !_autoHighlightConfirmed)
        {
            lock (_session.Gate)
                if (_session.World.HighlightedClusters.Contains(_autoHighlightId))
                {
                    _autoHighlightConfirmed = true;
                    Console.WriteLine($"[autoproof] received HIGHLIGHT_CLUSTER sector={_autoHighlightId}; map/HUD mark active");
                }
        }
        if (_pendingGotoSector is { Length: > 0 } gotoName)
        {
            _pendingGotoSector = null;
            if (ResolveSectorId(gotoName) is { } sid)
            {
                IssueStarmapGoto(sid);
                Console.WriteLine($"[autoproof] auto-goto -> sector {sid}");
            }
        }
        if (_pendingTarget is { } tk && Environment.TickCount64 >= _pendingTargetRetryAtMs)
        {
            _pendingTarget = null;
            // A ship target that is not in the sector yet (a fleet still on its
            // way) is retried every 2 s instead of being dropped.
            if (tk != LiveProof.AutoTargetKind.None && !AutoFrameTarget(tk)
                && tk is LiveProof.AutoTargetKind.Enemy or LiveProof.AutoTargetKind.Friendly)
            {
                _pendingTarget = tk;
                _pendingTargetRetryAtMs = Environment.TickCount64 + 2000;
            }
        }
        if (_pendingTargetClass is { Length: > 0 } clsSpec)
        {
            _pendingTargetClass = null;
            AutoFrameClassTarget(clsSpec);
        }
        if (_pendingHeadToDock)
        {
            _pendingHeadToDock = false;
            if (!_autoDockHeading)
            {
                _autoDockHeading = true;
                HeadToHomeStation();
            }
        }
    }

    private short? CurrentOwnSector()
    {
        lock (_session.Gate)
            return _session.World.MyShipId is { } me && _session.World.Ships.TryGetValue(me, out var ship) ? ship.SectorId : null;
    }

    private short? ResolveSectorId(string token)
    {
        lock (_session.Gate)
        {
            if (LiveProof.AutoProofPlan.IsNumericToken(token)
                && short.TryParse(token, out var num))
                return num;
            foreach (var (cid, dc) in _session.World.Clusters)
                if (string.Equals(dc.Name, token, StringComparison.OrdinalIgnoreCase))
                    return cid;
        }
        Console.WriteLine($"[autoproof] --auto-goto sector '{token}' not found — skipping goto");
        return null;
    }

    /// <summary>--auto-target: frame the nearest object of the requested kind
    /// by reusing the client's own real candidate gather + relation filters
    /// (the same machines T/G use), then engage the autopilot toward it so it
    /// stays framed as the ship drifts.</summary>
    private bool AutoFrameTarget(LiveProof.AutoTargetKind kind)
    {
        if (!TryOwnPose(out var pos, out var orient, out _))
            return false;

        List<CandidateObject> objs;
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is not { } me
                || !world.Ships.TryGetValue(me, out var own))
                return false;
            objs = GatherSectorCandidates(world.Ships, world.Warps, world.Stations,
                                          world.Asteroids, me, own.SectorId,
                                          ShipRoleBits, StationRoleBits);
        }

        short? mySide = null;
        lock (_session.Gate) mySide = _session.World.MySideId;

        CandidateObject? best = null;
        float bestDsq = float.MaxValue;
        foreach (var o in objs)
        {
            bool kindOk = kind switch
            {
                LiveProof.AutoTargetKind.Enemy => o.Kind == TargetKind.Ship && o.SideId != mySide,
                LiveProof.AutoTargetKind.Friendly => o.Kind == TargetKind.Ship && o.SideId == mySide,
                LiveProof.AutoTargetKind.Station => o.Kind == TargetKind.Station,
                LiveProof.AutoTargetKind.Aleph => o.Kind == TargetKind.Warp,
                LiveProof.AutoTargetKind.Asteroid => o.Kind == TargetKind.Asteroid,
                _ => false,
            };
            if (!kindOk) continue;
            var d = o.Position - pos;
            float dsq = d.LengthSquared();
            if (dsq < bestDsq) { bestDsq = dsq; best = o; }
        }

        if (best is { } b)
        {
            _autoTarget = new AutopilotTarget(b.Kind, b.Id, b.Name);
            _autoTargetAtEngage = b.Position;
            // HUD-8b — a scripted target selection is still native SetTarget: write
            // c_cmdCurrent so the server's Ship.CommandTarget drives the lead
            // predictor and the red order arrow reads the current target command.
            SendCurrentCommandToServer(_autoTarget.Value);
            // An asteroid is a static rock and a collision hazard: select it (the
            // pane needs no framing) but do not fly into it.
            if (kind != LiveProof.AutoTargetKind.Asteroid)
                EngageAutopilotToward(_autoTarget.Value, "autoproof: frame target");
            Console.WriteLine($"[autoproof] auto-target {kind} -> {b.Name} (kind {b.Kind})");
            return true;
        }
        else
        {
            Console.WriteLine($"[autoproof] auto-target {kind}: none in sector — leaving default targeting");
            return false;
        }
    }

    /// <summary>P623 — --auto-target-class: drive the real class-filter handler
    /// (<see cref="SelectClassForKey"/>/<see cref="SelectMajorBaseForKey"/>) for a
    /// <c>"rel:cls[:dir]"</c> spec so a real-window autoshot proves the class
    /// bracket. Then autopilot toward it (like <see cref="AutoFrameTarget"/>) so it
    /// stays framed as the ship drifts. cls may be a ship class
    /// (bomber/fighter/miner/constructor/lifepod) or majorbase; dir is
    /// nearest|next|previous (default nearest).</summary>
    private void AutoFrameClassTarget(string spec)
    {
        var parts = spec.Split(':');
        if (parts.Length < 2) { Console.WriteLine($"[autoproof] --auto-target-class: expected rel:cls[:dir], got '{spec}'"); return; }
        string relTok = parts[0].Trim().ToLowerInvariant();
        string clsTok = parts[1].Trim().ToLowerInvariant();
        var dir = StepDirection.Nearest;
        if (parts.Length > 2)
        {
            dir = parts[2].Trim().ToLowerInvariant() switch
            {
                "next" => StepDirection.Next,
                "previous" or "prev" => StepDirection.Previous,
                _ => StepDirection.Nearest,
            };
        }
        var rel = relTok switch
        {
            "enemy" => TargetRelation.Enemy,
            "friendly" or "ally" => TargetRelation.Friendly,
            _ => TargetRelation.Enemy,
        };
        if (clsTok == "majorbase")
        {
            SelectMajorBaseForKey(rel, $"{relTok} major base", dir);
        }
        else
        {
            var cls = clsTok switch
            {
                "bomber" => TargetClass.Bomber,
                "fighter" => TargetClass.Fighter,
                "miner" => TargetClass.Miner,
                "constructor" => TargetClass.Constructor,
                "lifepod" => TargetClass.Lifepod,
                _ => TargetClass.Bomber,
            };
            SelectClassForKey(rel, cls, $"{relTok} {clsTok}", dir);
        }
        // Tightly-coupled proof shot: capture the frame the INSTANT the class
        // target is selected (the timed schedule can miss a fast-moving native
        // miner that warps away between selection and the next scheduled shot).
        ScheduleAutoshot(-1);   // ShotIndex -1 = outside the timed schedule
        // Engage the autopilot toward the (just-selected) target so it stays framed.
        if (_autoTarget is { } t && IsUsableAutopilotTarget(t, MyShipIdSafe()))
            EngageAutopilotToward(t, "autoproof: frame class target");
    }

    private short MyShipIdSafe()
    {
        lock (_session.Gate) return _session.World.MyShipId ?? -1;
    }

    /// <summary>--auto-dock-after: autopilot to the nearest friendly station so
    /// the server's real dock collision (Sector.ResolveDockCollisions) docks us;
    /// the machine's sweep then photographs the hangar previews.</summary>
    private void HeadToHomeStation()
    {
        if (!TryOwnPose(out var pos, out _, out _))
            return;
        short? me = null;
        short mySector = -1;
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is { } m && world.Ships.TryGetValue(m, out var own))
            {
                me = m;
                mySector = own.SectorId;
            }
        }

        SimVec? bestPos = null;
        short bestId = -1; string bestName = "";
        float bestDsq = float.MaxValue;
        lock (_session.Gate)
        {
            foreach (var (sid, st) in _session.World.Stations)
            {
                if (st.ClusterId != mySector) continue;   // prefer our own sector's station
                var spos = Vec(st.Position);
                var d = spos - pos;
                float dsq = d.LengthSquared();
                if (dsq < bestDsq) { bestDsq = dsq; bestId = sid; bestName = st.Name; bestPos = spos; }
            }
        }
        if (bestId < 0)
        {
            Console.WriteLine("[autoproof] --auto-dock-after: no station in my sector to dock at");
            return;
        }
        _autoTarget = new AutopilotTarget(TargetKind.Station, bestId, bestName);
        _autoTargetAtEngage = bestPos;
        EngageAutopilotToward(_autoTarget.Value, "autoproof: head to dock");
        Console.WriteLine($"[autoproof] auto-dock: autopilot to station {bestName} (#{bestId})");
    }

    // ── autoshots (real backbuffer, valid PNG) ────────────────────────────────

    /// <summary>Queue a real-backbuffer screenshot at the next Draw end with a
    /// descriptive name (time + sector + camera). <paramref name="shotIndex"/>
    /// &gt;= 0 = the in-flight schedule; <paramref name="dockSweepStep"/> &gt;= 0
    /// labels a hangar-preview sweep shot.</summary>
    private void ScheduleAutoshot(int shotIndex = -1, int dockSweepStep = -1)
    {
        // P577b — --auto-credits: open the in-game Credits/Licences overlay so
        // the real-backbuffer shot (flushed at Draw end, after the overlay
        // draws top-most) captures the updated credit text.
        if (_autoPlan.AutoCredits) _creditsVisible = true;

        // P639 — --auto-screen: force one of the three screen overlays open so
        // the real-backbuffer shot captures the leaderboard / character sheet /
        // team pane. Mirrors the B/P/Ctrl+T toggles' visible flags; only the
        // requested one is forced (independent flags, so they compose cleanly).
        switch (_autoPlan.AutoScreen)
        {
            case LiveProof.AutoScreenKind.Leaderboard: _scoreboardVisible = true; break;
            case LiveProof.AutoScreenKind.CharSheet: _pilotInfoVisible = true; break;
            case LiveProof.AutoScreenKind.TeamPane: _teamPaneVisible = true; break;
        }

        // P570 — dismiss the briefing popup first so the real-backbuffer proof
        // frame isn't obscured by it (the briefing only auto-dismisses in the
        // offscreen-capture path; mirror what ALLEGIANCE_AUTOSHOT does).
        if (IsBriefingVisible(out _)) DismissBriefing();

        double t = _auto?.SecondsSinceLaunch ?? 0.0;
        string camera = _cameraMode switch
        {
            CameraMode.Command => _commandCamera.TopDown ? "commandtd" : "command",
            CameraMode.DroneFollow => "dronefollow",
            CameraMode.DroneCockpit => "dronecockpit",
            CameraMode.Cockpit => "cockpit",
            CameraMode.External => "external",
            CameraMode.FreeCam => "freecam",
            CameraMode.OrbitCam => "orbitcam",
            _ => "chase",
        };
        string sector = CurrentSectorName();
        string tag = shotIndex == LiveProof.AutoProofMachine.PostKillShotIndex
            ? "postkill"
            : dockSweepStep >= 0 ? $"sweep{dockSweepStep:00}" : $"t{t:000.0}";
        _autoShotName = $"proof_{tag}_s{sector}_c{camera}";
        _autoShotScheduled = true;
        Console.WriteLine($"[autoproof] autoshot queued: {_autoShotName} (t={t:0.0}s after launch)");
    }

    private string CurrentSectorName()
    {
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var own)
                && world.Clusters.TryGetValue(own.SectorId, out var dc))
                return SanitizeFileName(dc.Name);
        }
        return "unknown";
    }

    private static string SanitizeFileName(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.Length == 0 ? "sector" : sb.ToString();
    }

    /// <summary>HUD-5b — the radar snapshot summary for evidence logging
    /// (null unless ALLEGIANCE_RADAR_PROOF=1).</summary>
    private string? RadarProofLine() => _radarProofEnabled ? _radarProofLine : null;

    /// <summary>Write the queued autoshot. Called from the Draw-end hook where
    /// the real backbuffer holds the completed frame. Writes a VALID PNG via
    /// SaveAsPng (not the P566-bad-CRC encoder) so the file opens in a normal
    /// viewer.</summary>
    private void FlushScheduledAutoshot()
    {
        if (!_autoShotScheduled)
            return;
        _autoShotScheduled = false;
        try
        {
            var gd = GraphicsDevice;
            int w = gd.PresentationParameters.BackBufferWidth;
            int h = gd.PresentationParameters.BackBufferHeight;
            var px = new Color[w * h];
            gd.GetBackBufferData<Color>(px);
            string dir = _autoshotDir.Length > 0 ? _autoshotDir : "screenshots";
            Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"{_autoShotName}.png");
            using var tex = new Texture2D(gd, w, h, false, SurfaceFormat.Color);
            tex.SetData(px);
            using var fs = System.IO.File.Create(path);
            tex.SaveAsPng(fs, w, h);
            Console.WriteLine($"[autoproof] screenshot saved: {path}");
            if (RadarProofLine() is { } radarProof)
                Console.WriteLine($"[autoproof] radar on that frame: {radarProof}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[autoproof] autoshot FAILED: {ex.Message}");
        }
    }
}
