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
            // P803 — a boarded gunner/observer is docked exactly while its CARRIER
            // is (native GetSourceShip): its own record can keep the old station id.
            if (world.MyShipId is { } me2 && world.Ships.TryGetValue(me2, out var self)
                && self.ParentId >= 0 && world.Ships.TryGetValue(self.ParentId, out var carrier))
                docked = carrier.StationId != -1;
            if (docked) _auto.NoteDocked();
            else _auto.NoteUndocked();
            // P803 — with a delayed launch or a board request the launch clock
            // starts only once actually AIRBORNE: the client enters Flying at mission
            // start while still docked, which would otherwise mark it launched and
            // suppress the delayed LaunchHull (and start the gunner's shots before
            // its carrier has launched). Other proofs keep the old behaviour.
            bool waitForAirborne = _autoPlan.AutoLaunchAfterSeconds > 0 || _autoPlan.AutoBoard.Length > 0;
            if (_state == ClientState.Flying && (!docked || !waitForAirborne))
                _auto.NoteLaunch();
            airborne = _state == ClientState.Flying && !docked;
        }

        _auto.Tick(nowSec);

        // P803 — --auto-accept is re-asserted every 5 s while still docked and
        // unlaunched: a stray AutoAcceptToggle (U) in the ready-check UI must not
        // leave a second auto client's join request pending forever.
        if (_autoPlan.AutoAccept && !_launchSent && Environment.TickCount64 >= _autoAcceptNextAtMs)
        {
            short? aaSideRead;
            lock (_session.Gate) aaSideRead = _session.World.MySideId;
            if (aaSideRead is { } aaSide && aaSide >= 0)
            {
                _session.SendAutoAccept(aaSide, true);
                _autoAcceptNextAtMs = Environment.TickCount64 + 5000;
            }
        }

        // P803 — the --auto-board retry drain. Runs here (docked included)
        // because boarding needs BOTH ships docked: the airborne-only pending
        // slots in ApplyAutoProofFlying would never fire it.
        if (_pendingBoardTarget is { Length: > 0 } boardName)
        {
            bool boarded = false;
            short? targetId = null;
            lock (_session.Gate)
            {
                if (_session.World.MyShipId is { } me
                    && _session.World.Ships.TryGetValue(me, out var mine)
                    && mine.ParentId >= 0)
                {
                    boarded = true;   // already aboard someone
                }
                else
                {
                    foreach (var s in _session.World.Ships.Values)
                    {
                        // P803 — name match is enough: while both ships are
                        // DOCKED the same-side EXPORT may not have flowed yet,
                        // but PLAYERINFO seeds every teammate's CharacterName
                        // (native appmsg.cpp:2280), which is the identity the
                        // board request needs (BOARD resolves on the boarder's
                        // own side server-side, FedSrv.CPP:6404-6409).
                        if (s.CharacterName is { Length: > 0 } name
                            && string.Equals(name, boardName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetId = s.ShipId;
                            break;
                        }
                    }
                }
            }
            if (boarded)
            {
                Console.WriteLine($"[autoproof] auto-board: parent link observed — done ({boardName})");
                _pendingBoardTarget = null;
            }
            else if (targetId is { } tid && Environment.TickCount64 >= _boardNextAttemptAtMs)
            {
                _session.SendBoardShip(tid);
                _boardNextAttemptAtMs = Environment.TickCount64 + 1000;
                if (++_boardAttempts > 40)
                {
                    Console.Error.WriteLine($"[autoproof] auto-board: no parent link after {_boardAttempts} tries — giving up");
                    _pendingBoardTarget = null;
                }
            }
        }

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
                    // P803 — the seated side leader also turns auto-accept on.
                    short? aaSideRead;
                    lock (_session.Gate) aaSideRead = _session.World.MySideId;
                    if (_autoPlan.AutoAccept && aaSideRead is { } aaSide && aaSide >= 0)
                    {
                        _session.SendAutoAccept(aaSide, true);
                        Console.WriteLine($"[autoproof] auto-accept ON for side {aaSide}");
                    }
                    break;
                case LiveProof.AutoActionKind.LaunchHull:
                    // Launched from ApplyAutoProofDocked once docked; the machine's
                    // emission is the sequencing signal — P803: it is now the GATE,
                    // so --auto-launch-after really delays the launch (the pilot
                    // stays docked while a gunner boards).
                    _autoLaunchDue = true;
                    break;
                case LiveProof.AutoActionKind.GotoSector:
                    _pendingGotoSector = a.StrArg;
                    break;
                case LiveProof.AutoActionKind.SelectTarget:
                    _pendingTarget = (LiveProof.AutoTargetKind)a.IntArg;
                    _pendingTargetEngage = true;   // P805b — the classic --auto-target path keeps flying to the target
                    break;
                case LiveProof.AutoActionKind.SelectTargetSeq:
                    // P805b — a scripted --auto-target-seq selection: select only.
                    // The target pane preview needs same-cluster, not proximity,
                    // and an autopilot toward an aleph would transit it.
                    _pendingTarget = (LiveProof.AutoTargetKind)a.IntArg;
                    _pendingTargetEngage = false;
                    break;
                case LiveProof.AutoActionKind.Stop:
                    // P805b — the Backspace TK_ThrottleZero preset (WinTrek.cpp:10939-10960).
                    _throttle = Flight.FlightInput.PresetValue(Flight.FlightInput.ThrottlePreset.Zero);
                    Console.WriteLine($"[autoproof] throttle ZERO (hold station) at t={_auto?.SecondsSinceLaunch:0.0}s");
                    break;
                case LiveProof.AutoActionKind.DropProbe:
                    // P805b — the N key's real dispenser fire (TK_DropMine,
                    // trekinput.cpp:1424): FIRE_EXPENDABLE with the shared
                    // Dispenser mount (a probe or mine dispenser at mount 0).
                    _session.SendFireExpendable(Allegiance.Core.EquipmentType.Dispenser);
                    Console.WriteLine($"[autoproof] drop probe/mine (Dispenser) at t={_auto?.SecondsSinceLaunch:0.0}s");
                    break;
                case LiveProof.AutoActionKind.SummonEnemy:
                    // P805b — place an enemy in view (TrySummonEnemy, drained in
                    // ApplyAutoProofFlying until an enemy is observed in-sector).
                    _pendingSummonEnemy = true;
                    _summonEnemyDistance = a.IntArg > 0 ? a.IntArg : 500;
                    Console.WriteLine($"[autoproof] summon enemy to {_summonEnemyDistance} m ahead");
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
                case LiveProof.AutoActionKind.BoardPilot:
                    // P803 — buffered; drained EVERY frame (docked included)
                    // with retries: both ships must be docked and the mission
                    // started before BOARD_SHIP can succeed.
                    Console.WriteLine($"[autoproof] auto-board → {a.StrArg} (retrying until parented)");
                    _pendingBoardTarget = a.StrArg;
                    _boardAttempts = 0;
                    _boardNextAttemptAtMs = 0;
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
    private bool _pendingTargetEngage = true;   // P805b — false for the scripted sequence
    private long _pendingTargetRetryAtMs;
    private string? _pendingTargetClass;   // P623 — "rel:cls[:dir]" class target spec
    private bool _pendingSummonEnemy;      // P805b — relocate an enemy into view
    private int _summonEnemyDistance = 500;
    private long _summonEnemyRetryAtMs;
    private bool _pendingHeadToDock;
    private string? _pendingBoardTarget;   // P803 — --auto-board: pilot name to board
    private int _boardAttempts;
    private long _boardNextAttemptAtMs;
    private long _autoAcceptNextAtMs;
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
    private bool _autoPreBuySent;
    private bool _autoLaunchDue;

    private void ApplyAutoProofDocked(Rendering.ShopModel shop)
    {
        if (!_launchSent)
        {
            // P803 — a delayed launch with a named hull buys that hull while still
            // DOCKED (native BUY_LOADOUT fLaunch=false), so a second client can board
            // its turret seats before the carrier launches (BoardShip needs both
            // ships docked and seats on the docked hull, FedSrv.CPP:2322-2345).
            if (!_autoPreBuySent && _autoPlan.AutoLaunchAfterSeconds > 0 && _autoPlan.LaunchHull.Length > 0)
            {
                _autoPreBuySent = true;
                SelectAutoLaunchHull(shop);
                _session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: false, parts: shop.ChosenParts);
                Console.WriteLine($"[autoproof] auto-launch pre-buy (docked) hull {shop.EffectiveHullId} + {shop.ChosenParts.Count} part(s)");
            }
            int aboard = 0;
            if (_autoPlan.AutoLaunchWhenBoarded > 0)
                lock (_session.Gate)
                    if (_session.World.MyShipId is { } meC)
                        aboard = _session.World.Ships.Values.Count(sh => sh.ParentId == meC);
            bool boardedEnough = _autoPlan.AutoLaunchWhenBoarded > 0 && aboard >= _autoPlan.AutoLaunchWhenBoarded;
            if (_autoPlan.AutoLaunchRequested && !_autoLaunchApplied
                && (_autoLaunchDue || boardedEnough || _autoPlan.AutoLaunchAfterSeconds <= 0))
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
        SelectAutoLaunchHull(shop);
        // P805b — fill the Dispenser socket with a real probe launcher when the
        // proof asked for it (the debug arena's spawned hull defaults to chaff).
        if (_autoPlan.AutoCarryProbe) AutoMountProbeDispenser(shop);
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

    /// <summary>
    /// P805b — the real docked-shop navigation a player performs to carry a probe:
    /// the PARTS tab, the hull's Dispenser socket, then the first PROBE launcher the
    /// station offers (native BuyPartMenu lists exactly the type-compatible,
    /// <c>CanMount</c> parts, load.cpp:1680-1751; the list is price-ordered, so the
    /// cheapest probe dispenser is chosen deterministically). Uses only the
    /// production <see cref="ShopModel"/> navigation the touch/keyboard paths use.
    /// </summary>
    private void AutoMountProbeDispenser(Rendering.ShopModel shop)
    {
        shop.SwitchTab(Rendering.ShopTab.Parts);
        var slots = shop.Slots;
        int dispenser = -1;
        for (int i = 0; i < slots.Count; i++)
            if (slots[i].Type == Allegiance.Core.EquipmentType.Dispenser) { dispenser = i; break; }
        if (dispenser < 0)
        {
            Console.WriteLine("[autoproof] --auto-carry-probe: the effective hull has no Dispenser socket — launching as-is");
            shop.SwitchTab(Rendering.ShopTab.Hulls);
            return;
        }
        shop.MoveCursor(dispenser - shop.Cursor);
        shop.Activate();                       // open the Dispenser slot
        var offers = shop.OpenSlotParts;
        int pick = -1;
        for (int i = 0; i < offers.Count; i++)
            if (offers[i].Part.Type is Allegiance.Core.DataPartTypeIGC.ProbeLauncher) { pick = i; break; }
        if (pick < 0)
        {
            Console.WriteLine("[autoproof] --auto-carry-probe: no probe dispenser offered for this hull — launching as-is");
            shop.Back();
            shop.SwitchTab(Rendering.ShopTab.Hulls);
            return;
        }
        shop.MoveCursor(pick - shop.Cursor);
        shop.Activate();                       // fill the slot + close it
        Console.WriteLine($"[autoproof] --auto-carry-probe: mounted '{offers[pick].Part.Name}' (part {offers[pick].Part.PartId}) in the Dispenser socket");
        shop.SwitchTab(Rendering.ShopTab.Hulls);
    }

    private void SelectAutoLaunchHull(Rendering.ShopModel shop)
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

    /// <summary>P805b — the mouse is centred once after launch so the cockpit
    /// head-look (P624) starts at zero in a scripted run: the target-pane preview's
    /// view direction comes from the frame's camera, and native's preview ignores
    /// head-look entirely (WinTrek.cpp:426 vs :438), so an un-centred pointer made
    /// the same run frame the same model differently. Mirrors the command-camera
    /// centring below.</summary>
    private bool _autoMouseCentered;

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
        if (!_autoMouseCentered && (_auto?.SecondsSinceLaunch ?? 0) >= 0.5)
        {
            Microsoft.Xna.Framework.Input.Mouse.SetPosition(
                GraphicsDevice.Viewport.Width / 2, GraphicsDevice.Viewport.Height / 2);
            _autoMouseCentered = true;
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
            // way) is retried every 2 s instead of being dropped. P805b — the
            // same retry covers a probe/treasure that has not arrived yet (the
            // dispenser drop and the mission's treasure spawn both land a little
            // after the sequence's nominal second).
            if (tk != LiveProof.AutoTargetKind.None && !AutoFrameTarget(tk, _pendingTargetEngage))
            {
                _pendingTarget = tk;
                _pendingTargetRetryAtMs = Environment.TickCount64 + 2000;
            }
        }
        if (_pendingSummonEnemy && Environment.TickCount64 >= _summonEnemyRetryAtMs)
        {
            // Re-asserted every 4 s until an enemy is observed in our sector: the
            // bot may still be docking at the first ask, and re-relocating to the
            // same anchor is a no-op once it is there. Stops as soon as it lands so
            // the captured frame is not re-teleported under the crosshair.
            _summonEnemyRetryAtMs = Environment.TickCount64 + 4000;
            if (TrySummonEnemy(_summonEnemyDistance))
            {
                _pendingSummonEnemy = false;
                Console.WriteLine("[autoproof] summon-enemy: an enemy is in our sector — summon settled");
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
    /// (the same machines T/G use), then (when <paramref name="approach"/> is set)
    /// engage the autopilot toward it so it stays framed as the ship drifts.
    /// P805b — probe and treasure are the remaining native ThingGeo preview
    /// kinds (row 409); the scripted <c>--auto-target-seq</c> path selects them
    /// with <paramref name="approach"/> false so a pane-preview run holds
    /// station.</summary>
    private bool AutoFrameTarget(LiveProof.AutoTargetKind kind, bool approach = true)
    {
        if (!TryOwnPose(out var pos, out _, out _))
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
                                          ShipRoleBits, StationRoleBits,
                                          world.Probes, world.Treasures);
        }

        short? mySide = null;
        lock (_session.Gate) mySide = _session.World.MySideId;

        // P805b — an explicit treasure CODE filter (--auto-treasure part): the
        // mission's treasure set is random, so "the nearest treasure" is not the
        // art the proof asked for. Snapshot the wanted ids in the same gather.
        HashSet<short>? wantedTreasures = null;
        if (kind == LiveProof.AutoTargetKind.Treasure && _autoPlan.AutoTreasureCode is { } wantedCode)
        {
            wantedTreasures = new HashSet<short>();
            int total = 0;
            // P805b — for a PART treasure, prefer the spawned plain part whose native
            // model is the MOST COMPACT (smallest max vertex radius). Native normalises
            // every preview by 1/m_radiusMesh (effect/thinggeo.cpp:367-381,
            // 1185-1193), so a compact model is the one that actually fills the 96 px
            // pane; a slim launcher/gun model would be technically correct but read as
            // empty. The choice is deterministic for a given spawn set, and both kinds
            // resolve through the same native art path.
            short compactId = -1;
            float compactRadius = float.MaxValue;
            lock (_session.Gate)
                foreach (var (tid, te) in _session.World.Treasures)
                {
                    total++;
                    if (te.Code != wantedCode) continue;
                    bool plainPart = wantedCode != Allegiance.Core.TreasureCode.Part
                        || _shopCatalog?.Core?.PartTypes.ContainsKey(te.PayloadId) == true;
                    if (!plainPart) continue;
                    if (wantedCode != Allegiance.Core.TreasureCode.Part)
                    {
                        wantedTreasures.Add(tid);
                        continue;
                    }
                    var core = _shopCatalog?.Core;
                    if (core?.PartTypes.TryGetValue(te.PayloadId, out var partType) != true) continue;
                    var model = partType.ModelName;
                    float r = _nativeTreasureRenderer.MeshFor(model)?.MaxVertexRadius ?? float.MaxValue;
                    if (r < compactRadius) { compactRadius = r; compactId = tid; }
                }
            if (compactId >= 0) wantedTreasures.Add(compactId);
            Console.WriteLine($"[autoproof] auto-target Treasure: {wantedTreasures.Count}/{total} in sector match {wantedCode}"
                + (compactId >= 0 ? $" (chose #{compactId}, mesh radius {compactRadius:0.###})" : ""));
        }

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
                LiveProof.AutoTargetKind.Probe => o.Kind == TargetKind.Probe,
                LiveProof.AutoTargetKind.Treasure => o.Kind == TargetKind.Treasure
                    && (wantedTreasures is null || wantedTreasures.Contains(o.Id)),
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
            // An asteroid is a static rock, a probe is inert and a treasure is a
            // pickup: select them (the pane needs no framing) but never fly at
            // them — matching the pre-P805b asteroid rule.
            bool staticKind = kind is LiveProof.AutoTargetKind.Asteroid
                or LiveProof.AutoTargetKind.Probe or LiveProof.AutoTargetKind.Treasure;
            if (approach && !staticKind)
                EngageAutopilotToward(_autoTarget.Value, "autoproof: frame target");
            string extra = "";
            if (kind == LiveProof.AutoTargetKind.Treasure)
            {
                string code;
                lock (_session.Gate)
                    code = _session.World.Treasures.TryGetValue(b.Id, out var te) ? te.Code.ToString() : "?";
                extra = $", treasure code {code}";
            }
            Console.WriteLine($"[autoproof] auto-target {kind} -> {b.Name} (kind {b.Kind}{extra})");
            return true;
        }
        else
        {
            Console.WriteLine($"[autoproof] auto-target {kind}: none in sector — leaving default targeting");
            return false;
        }
    }

    /// <summary>
    /// P805b — place an enemy ship in view for the selection-box proof. The port's
    /// privileged <c>relocate</c> chat cheat (a PORT EXTENSION; native has no
    /// relocate, Server/Server.AdminCheats.cs) moves a ship the privileged pilot
    /// controls or launched — here an MCP-harness (testHarness) bot on the
    /// opposite side. <see cref="Server"/> sends that bot's owner a SET_CLUSTER; the
    /// proof client learns the new sector/pose from the per-cluster
    /// HEAVY_SHIPS_UPDATE (ClientWorld P375), so a normal <--auto-target enemy>
    /// selection frames it. Retried by the caller until an airborne enemy exists.
    /// </summary>
    private bool TrySummonEnemy(int distance)
    {
        short mySector;
        Allegiance.Core.Collision.Vector3 pos;
        Allegiance.Core.Collision.Vector3 fwd;
        var targets = new List<(short Id, string Name)>();
        bool alreadyHere = false;
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is not { } mine || !world.Ships.TryGetValue(mine, out var own)
                || own.Latest is not { } ownUpd)
                return false;
            mySector = own.SectorId;
            pos = ownUpd.Position;
            fwd = ownUpd.Orientation.GetForward();
            short? mySide = world.MySideId;
            foreach (var (id, s) in world.Ships)
            {
                // P805b — identity is NOT required: an enemy the pilot has not
                // seen yet is known from PLAYERINFO (P775) with its side and
                // name, which is all the relocate request needs (the server
                // resolves the ship and refuses anything this pilot does not
                // control). Station/sector are also unknown before the first
                // status, so they are not filtered on — a docked target is
                // refused server-side and the caller retries.
                if (id == mine || s.SideId == mySide || s.IsDead) continue;
                targets.Add((id, s.CharacterName ?? $"ship {id}"));
                // A REAL in-cluster pose (HEAVY_SHIPS_UPDATE stamps the pose's
                // sector) — a PLAYERINFO-only enemy's default sector 0 is not
                // evidence it is here.
                if (s.Latest is not null && s.PoseSectorId == mySector && s.StationId == -1)
                    alreadyHere = true;
            }
        }
        if (targets.Count == 0)
        {
            int ships = 0, enemies = 0;
            lock (_session.Gate)
            {
                ships = _session.World.Ships.Count;
                enemies = _session.World.Ships.Values.Count(s => s.SideId != _session.World.MySideId);
            }
            Console.WriteLine($"[autoproof] summon-enemy: no enemy in the world to summon yet "
                + $"({ships} ship(s) known, {enemies} on another side) — retrying");
            return false;
        }
        if (alreadyHere) return true;   // already in view; nothing to move

        // Place it ahead of the pilot's own nose, at the requested distance. The
        // server refuses ships this pilot does not control (a plain NPC miner is
        // refused; the MCP-harness bot is accepted), so ask about every airborne
        // enemy and let the server pick — the accepted one is the one that then
        // arrives in our sector via HEAVY_SHIPS_UPDATE (ClientWorld P375).
        var p = pos + fwd * distance;
        foreach (var (id, name) in targets)
        {
            var line = $"relocate {id} {mySector} {F(p.X)} {F(p.Y)} {F(p.Z)}";
            _session.SendChat(line);
            Console.WriteLine($"[autoproof] summon-enemy: chat '{line}' (enemy {name} #{id})");
        }
        return true;

        static string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
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
