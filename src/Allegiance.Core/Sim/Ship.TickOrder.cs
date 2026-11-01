using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Sim;

public sealed partial class Ship
{

    // P906 / SIM-0062. Native clusterIGC.cpp:170-231 separates these three
    // ship-list walks from the ordinary model Update walk at :241-253.
    internal void PreplotMove(double now)
    {
        if (!CanPlotMove(now)) return;
        if (ParentShip is not null) return;
        PreplotMoveCore(now);
        ValidateNpcPlan(now);
    }

    internal bool PlotMove(double now) => CanPlotMove(now) && ParentShip is null
        && PlotMoveCore(now, now - LastUpdate);

    internal void ExecutePlottedMove(double now)
    {
        if (!CanPlotMove(now)) return;
        if (ParentShip is null) ExecuteMove(LastUpdate, now);
    }

    private bool CanPlotMove(double now) => now > LastUpdate && !Dead
        && StationId is null && ParentShip?.StationId is null;

    private void PreplotMoveCore(double now)
    {
        // P528b — release the remembered departure rock once the ship has clearly
        // left it, BEFORE the autopilot/dodge scan reads it this tick (so a miner
        // that has pulled clear resumes normal collision avoidance; one that has
        // not, still exempts the rock it is departing).
        UpdateMineDeparture();

        if (IsNpc && !_droneClockInitialized)
        {
            _droneClockInitialized = true;
            _timeRanAway = LastUpdate; // native initializes with creation time (shipIGC.cpp:189)
            _lastComplaint = LastUpdate; // m_timeLastComplaint = now, shipIGC.cpp:187
        }
        UpdateDamageRetreat(now);
        TryTeleportUnload();
        TryStartBuilderBuild(now);

        // P767 / SIM-0100 — the port of CshipIGC::PreplotShipMove's
        // getting-ammo clear (shipIGC.cpp:1713-1720): autopilot on (every
        // plotted NPC here is), no parent ship, not building, and the ammo has
        // arrived (GetAmmo() > 0) → drop the latch and the resupply plan
        // (SetCommand(c_cmdPlan, NULL, c_cidNone)) so the accepted order or
        // the default decision resumes. Docked ships returned at the top of
        // OnUpdate, so the latch survives a docked mid-resupply and clears on
        // the first UNDOCKED pre-plot — undock happens first (a station-dock
        // restock refills the pool while docked; see ServerHost's
        // DockWithStationEvent resupply refill). A dead resupply carrier also
        // drops the latch: there is nothing left to fly to.
        if (_isGettingAmmo && ParentShip is null && !IsBuilding)
        {
            // An unreachable carrier (dead, gone from space, or changed side)
            // makes native's plan illegal; SetCommand then clears the latch.
            if (_gettingAmmoCarrier is { } gc && (gc.Dead || gc.Sector is null || gc.SideId != SideId))
            {
                _gettingAmmoCarrier = null;
                _isGettingAmmo = false;
            }
            else if (Ammo > 0)
            {
                _isGettingAmmo = false;                 // m_bGettingAmmo = false (shipIGC.cpp:1717)
                _gettingAmmoCarrier = null;
                PlanTarget = null;                      // SetCommand(c_cmdPlan, NULL, c_cidNone) (shipIGC.cpp:1718)
                PlanCommandId = OrderChange.CidNone;
            }
        }

    }

    // Native DoNothing is legal with or without a target (shipIGC.h:1606-1616),
    // so PreplotShipMove never replaces it with a default order (:1934-1943).
    // Training uses the real command slots; preserve its explicitly idle tableau.
    private bool HoldsTrainingOrder => Mission?.IsTraining == true && IsNpc && !IsRunningAway
        && AcceptedShipCommandId == OrderChange.CidDoNothing;

    private bool PlotMoveCore(double now, double dt)
    {
        if (HoldsTrainingOrder)
        {
            Controls = ControlData.Neutral;
            StateM &= ShipControlState.SelectedWeaponMask;
            return false;
        }
        // P155 — compute Controls for IsNpc ships with a reachable PlanTarget
        // BEFORE the flight model reads them (ExecuteMove derives all motion
        // from Controls). Real player ships (IsNpc false) are never touched.
        if (IsNpc && (PilotType == PilotTypeWingman || PilotType == PilotTypeCheatPlayer) && !IsRunningAway)
            UpdateWingmanTravel(now);
        _wingmanControlsBeforePlot = Controls;
        ApplyNpcAutopilot(now);
        // P762 / SIM-0142-SIM-0471-adjacent (row 7) — native PlotShipMove's
        // builder block (shipIGC.cpp:2262-2300): a builder in the BUILD state
        // holds yaw/pitch 0, roll 1, throttle -0.8; while DRILLING it holds the
        // same attitude with throttle 0 only while aligned within 50 units² of
        // the rock's rotation axis and inside r_ship + r_rock + 25. Off-track
        // the native clears the state mask and re-approaches; this port leaves
        // the existing autopilot steer in place, which performs that re-approach.
        ApplyBuilderDrillingControls();

        // SIM-0110 — native resets the shared complaint interval whenever the
        // autopilot drone has a plan again (shipIGC.cpp:2351-2352), so a drone
        // that was idle long enough to inflate its backoff restarts at 30 s
        // the next time it runs out of work.
        if (IsNpc && HasActivePlan)
            _complaintInterval = 30f;

        // P162 — the Wingman's per-tick combat leg (validate AttackTarget, then
        // decide whether to hold the trigger). Must run BEFORE the weapon loop
        // further down so this tick's fire-selection bit is already in StateM
        // when Weapon.Update reads IsWeaponSelected. Gates to Wingman NPCs only
        // — a real player ship's StateM is wire-owned and must never be touched.
        //
        // P166 — a defending Wingman's target life is owned by
        // UpdateWingmanDefend, run FIRST so this tick's fire decision in
        // UpdateWingmanCombat reflects any just-made acquire/switch/return-to-
        // escort (a returned-to-escort Wingman releases the trigger; a switched
        // one aims at the new threat).
        if (IsNpc && (PilotType == PilotTypeWingman || PilotType == PilotTypeCheatPlayer) && !IsRunningAway)
        {
            // P767 / SIM-0100 — a getting-ammo wingman is on a goto plan and
            // skips the in-range target scan (native shipIGC.cpp:2752-2762:
            // the c_cidGoto plan branch with m_bGettingAmmo only burns the
            // afterburner); the combat plot would also wipe the
            // AfterburnerButton this tick's autopilot just set. The rest of
            // the tick (ExecuteMove, parts) still runs.
            if (!_isGettingAmmo)
            {
                if (DefendTarget is not null)
                    UpdateWingmanDefend();
                if (HasRepairOrder) UpdateWingmanRepair(now, (float)dt);
                else UpdateWingmanCombat((float)dt);
            }
        }

        // Native layer completion consumes the drone at timeStart BEFORE
        // movement (shipIGC.cpp:2795-2828). Legacy arrival remains below.
        if (IsNpc && Mission?.NativeGotoPlanEnabled == true && TryPerformLayerLay(now - dt)) return true;

        return false;
    }

    private void ValidateNpcPlan(double now)
    {
        if (HoldsTrainingOrder) return;
        // SIM-0101 — NPC plan legality: every tick revalidate the Plan slot
        // and, if it is illegal, recover a legal Accepted order or fall through
        // to the default-order/take-cover path (shipIGC.cpp:1934-1945). Native
        // PreplotShipMove gates this whole block on m_bAutopilot
        // (clusterIGC.cpp:192 → shipIGC.cpp:1706), so real player ships
        // (IsNpc false) never evaluate it. P151/P403 — a running-away ship or
        // an active GotoWaypoint suppress this revalidation.
        if (IsNpc && !IsRunningAway && GotoWaypoint is null)
        {
            // If the current Plan slot is illegal, clear it so the recovery
            // path below can promote a legal Accepted order or pick a default.
            if (PlanTarget is { } plan && !IsLegalCommand(PlanCommandId, plan))
            {
                PlanTarget = null;
                PlanCommandId = OrderChange.CidNone;
            }

            // P767b — the accepted-order promotion is skipped while the
            // getting-ammo latch holds: the resupply trip owns the plan slot
            // (native's wingman branch writes c_cmdPlan ONLY, clintlib.cpp:3711
            // — the accepted slot resumes AFTER the trip, shipIGC.cpp:1936-1939).
            // Promoting it mid-trip would call DronePlanChanged and clear the
            // latch + carrier on the next tick.
            if (PlanTarget is null && !_isGettingAmmo && AcceptedTarget is { } accepted &&
                IsLegalCommand(AcceptedCommandId, accepted))       // LegalCommand(Accepted), shipIGC.cpp:1936
            {
                PlanTarget = accepted;
                PlanCommandId = AcceptedCommandId;                 // SetCommand(c_cmdPlan, target, cid), shipIGC.cpp:1939
            }
            // P767c — the default-order decision is skipped while the
            // getting-ammo latch holds too. Native only enters this recovery
            // when the plan slot is ILLEGAL (shipIGC.cpp:1934:
            // !LegalCommand(m_commandIDs[c_cmdPlan], …)); while getting ammo
            // the plan IS the resupply goto — the carrier ship or the reload
            // station (clintlib.cpp:3711-3712) — so neither the accepted-order
            // promotion (shipIGC.cpp:1936-1939) nor PickDefaultOrder (:1943)
            // runs mid-trip. The port's carrier leg carries its goto in
            // PlanCommandId with a null PlanTarget, which reads as "no plan"
            // here, so this latch guard is what preserves native's shape: a
            // visible enemy must not pull the wingman off the trip
            // (DecideWingmanDefaultOrder → SetWingmanAttackOrder →
            // DronePlanChanged would clear the latch and the carrier).
            else if (PlanTarget is null && !_isGettingAmmo)
            {
                // P160 — the mining/selling/contention machinery is
                // Miner-specific. Only a ship whose drone-type pilot type is
                // Miner runs the autonomous miner decision cycle; every other
                // pilot type (Wingman/Layer/Builder/Carrier, or any
                // not-yet-implemented type) simply does nothing autonomous —
                // the faithful stand-by matching native's PickDefaultOrder
                // fallthrough for a type with no case: fGaveOrder stays false →
                // SetCommand(c_cmdAccepted, NULL, c_cidNone), shipIGC.h:2458-2459.
                // MiningAsteroidTarget stays null for a non-miner, so the
                // take-cover gate below is unaffected.
                if (PilotType == PilotTypeMiner)
                {
                    // P156 — the miner decision cycle. A mining target is stale once
                    // the bag is full (ResolveMining stops accruing at capacity), so
                    // clear it to let the cycle switch to the sell leg.
                    // P528b — before clearing, remember the rock we are leaving so
                    // the dodge scan keeps ignoring it until the ship has departed
                    // (otherwise the just-emptied MiningAsteroidTarget exemption
                    // vanishes and the huge rock traps the full miner in a dodge
                    // orbit — the P528b regression).
                    if (Ore >= EffectiveMineCapacity() && MiningAsteroidTarget is not null)
                    {
                        _departingMineAsteroid = MiningAsteroidTarget;
                        MiningAsteroidTarget = null;
                    }

                    // Decide mine-or-sell only when fully idle (no station Plan and
                    // no active mining target) and undocked. Once it sets a target,
                    // later ticks are no longer idle, so it does not churn.
                    // P403 — an active goto also counts as non-idle.
                    if (MiningAsteroidTarget is null && StationId is null && GotoWaypoint is null)
                        DecideMiningOrSelling();
                }
                else if (PilotType == PilotTypeLayer)
                {
                    // P161 — the Layer default-order decision, the port of native
                    // PickDefaultOrder's c_ptLayer case (shipIGC.h:2417-2434).
                    // Like the Miner cycle above, decide only when fully idle (no
                    // Layer target) and undocked. Once it commits to a rock it is
                    // no longer idle, so it does not churn; it travels to and
                    // parks at the lowest-contention asteroid (does not deploy).
                    // Separate slot from the miner's, so the two cycles never
                    // interfere even for ships on the same side.
                    // P403 — an active goto also counts as non-idle.
                    // P591 — a commanded build/lay order (LayerBuildOrder) also
                    // counts as non-idle: the Layer flies to its lay point
                    // instead of parking at a rock.
                    if (LayerAsteroidTarget is null && LayerBuildOrder is null && StationId is null && GotoWaypoint is null)
                        DecideLayerDefaultOrder();
                }
                else if (PilotType == PilotTypeWingman)
                {
                    // P162 — the Wingman default-order decision, the port of native
                    // PickDefaultOrder's c_ptWingman case (shipIGC.h:2436-2455).
                    // Like the Miner/Layer cycles above, decide only when fully
                    // idle (no AttackTarget) and undocked. Once it commits to an
                    // enemy it is no longer idle, so it does not churn; it pursues
                    // and attacks that enemy (the per-tick fire/steering legs run
                    // earlier in this tick — UpdateWingmanCombat and the combat
                    // goal in ApplyNpcAutopilot). Separate slot from the
                    // miner's/layer's, so the three cycles never interfere even
                    // for ships on the same side.
                    //
                    // P166 — a DEFENDING Wingman (DefendTarget set) is bypassed
                    // here: its target life is owned by the per-tick
                    // UpdateWingmanDefend leg (run earlier in this tick), which
                    // acquires threats near the charge, never the plain nearest-
                    // to-itself default order — otherwise the idle re-pick would
                    // make it abandon its post and chase a distant enemy, and
                    // would instantly re-engage the very foe UpdateWingmanDefend
                    // just broke off from to escort the charge.
                    // P403 — an active goto also counts as non-idle.
                    if (!HasRepairOrder && DefendTarget is null && ActiveWingmanTarget is null && StationId is null && GotoWaypoint is null)
                        DecideWingmanDefaultOrder();
                }
                else if (PilotType == PilotTypeBuilder)
                {
                    // P165 — the Builder default-order decision, the port of
                    // native PickDefaultOrder's c_ptBuilder case
                    // (shipIGC.h:2388-2415). Like the Miner/Layer/Wingman cycles
                    // above, decide only when not already building (IsBuilding —
                    // a committed build makes the ship non-idle) and undocked.
                    // Once it registers a build it is no longer idle (the
                    // mechanic sets IsBuilding), so it does not churn; the 40 s
                    // construction runs to completion in Sector's per-tick
                    // resolver (StartBuildingEffect / ResolveBuildingEffects).
                    // P403 — an active goto also counts as non-idle.
                    if (!IsBuilding && BuilderAsteroidTarget is null && StationId is null && GotoWaypoint is null)
                        DecideBuilderDefaultOrder();
                }

                // P151 take-cover: only when STILL fully idle after the decision
                // cycle (a just-picked mining/layer trip isn't instantly
                // preempted), preserving mutual exclusion. Ordering is
                // The miner default cycle checks hide first; active work is
                // interrupted by UpdateDamageRetreat before movement (P652).
                //
                // P160 — deliberately NOT gated on PilotType: native's take-cover
                // applies to any damaged NPC regardless of pilot type
                // (TryTakeCover, shipIGC.h:2230-2236; this port's own call site
                // was already outside the miner-only scope). Only the
                // mining/selling and layer blocks above are pilot-type-specific.
                //
                // P161 — the idle gate now also demands a null LayerAsteroidTarget
                // so a Layer's just-chosen rock isn't instantly preempted by
                // take-cover, exactly as a Miner's isn't. (Harmless for a Miner —
                // its LayerAsteroidTarget is always null.)
                //
                // P162 — the same protection extends to a Wingman's just-chosen
                // AttackTarget: a ship actively pursuing an enemy is not "idle"
                // and is not diverted into take-cover by it. (Harmless for the
                // others — their AttackTarget is always null.)
                //
                // P165 — likewise extended to a Builder's IsBuilding: a ship
                // mid-construction is not "idle" either. Without this, a
                // damaged Builder could fly off to take cover while its
                // 40-second build silently continues in Sector's resolver
                // (ResolveBuildingEffects only checks Builder.Dead, not
                // position/Controls) — a real inconsistency with the pattern
                // every other busy pilot type already follows here. (Harmless
                // for the others — their IsBuilding is always false.)
                //
                // P166 — and a defending Wingman's DefendTarget: a Wingman
                // escorting its charge (even with no AttackTarget at that
                // instant) is not "idle" and must not be diverted into take-
                // cover, any more than an actively-engaging one. (Harmless for
                // the others — their DefendTarget is always null.)
                //
                // P403 — an active GotoWaypoint is likewise not idle: a ship
                // under a player-issued goto is commanded and must not be
                // diverted into take-cover.
                if (PilotType == PilotTypeMiner && MiningAsteroidTarget is null && LayerAsteroidTarget is null && AttackTarget is null && DefendTarget is null && !IsBuilding && BuilderAsteroidTarget is null && PlanTarget is null && GotoWaypoint is null && LayerBuildOrder is null)
                    TryTakeCover();                                // PickDefaultOrder take-cover, shipIGC.cpp:1943

                // SIM-0110: native PlotShipMove complains once per backoff window
                // when an autopilot drone has no plan (shipIGC.cpp:2308-2350). The
                // gate is native's m_pilotType < c_ptCarrier (not c_ptPlayer).
                if (!HasActivePlan && StationId is null && ParentShip is null && PilotType < PilotTypeCarrier)
                    ComplainIdle(now);
            }
        }

    }
}
