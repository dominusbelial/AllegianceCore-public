using System.Diagnostics;
using System.Runtime.InteropServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using System.Collections.Generic;
// Disambiguate the LoadoutChange KIND enum (Sim) from the Allegiance.Core.Wire.LoadoutChange
// codec — both are in scope here; the enum is the natural meaning in the
// gameplay broadcast handler, the wire codec is reached as Allegiance.Core.Wire.LoadoutChange.
using LoadoutChange = Allegiance.Core.Sim.LoadoutChange;

namespace Allegiance.Server;

/// <summary>
/// Per-tick broadcast passes of <see cref="Server"/>
/// (P327 stage-1 split from <c>Server.cs</c>). Partial class — shares the <c>Server</c>
/// type, so private field/method access across the split files is unaffected.
/// </summary>
public sealed partial class Server
{
    private readonly PodTripTelemetry _podTrips = new(Console.WriteLine);
    internal PodTripTelemetry PodTrips => _podTrips;

    // P606 — per-peer coalesce keys for the latest-wins streaming broadcasts.
    // Each key is a distinct "message kind": within one peer's send queue only
    // the NEWEST pending frame per key is retained (older tick's snapshot is
    // superseded in place), so a slow client's backlog stays small and current
    // instead of accumulating a stale copy of every per-tick broadcast. These
    // map to native's FM_NOT_GUARANTEED stream sends (Messages.cpp:548-563;
    // HEAVY/LIGHT_SHIPS_UPDATE FedSrv.CPP:4189-4196; STATIONS_UPDATE
    // FedSrv.CPP:8271) — the exact ship-state traffic P606 routes best-effort.
    // Distinct keys keep distinct message kinds independent (never coalesced
    // into one another), and all are sent non-guaranteed so they shed first
    // under pressure. Anything not listed here (exports, status, chat, kills,
    // joins, events, logon) stays guaranteed and strictly ordered.
    private const int CoalesceShipStates = 1;   // FM_S_SINGLE_SHIP_UPDATE(78) own + teammate position stream
    private const int CoalesceShipsUpdate = 2;  // FM_S_HEAVY/LIGHT_SHIPS_UPDATE(175/176) per-peer
    private const int CoalesceStations  = 3;    // FM_S_STATIONS_UPDATE(57) per-side
    private const int CoalesceAsteroids = 4;    // FM_S_ASTEROIDS_UPDATE(179) per-side
    private const int CoalesceProbes    = 5;    // FM_S_PROBES_UPDATE(92) per-side
    private const int CoalesceMissiles  = 6;    // FM_S_MISSILE_UPDATE(205) per-side
    private const int CoalesceMissileLock  = 7;   // FM_S_MISSILE_LOCK_UPDATE(204) owner-only
    private const int CoalesceMissileTrack = 8;   // FM_S_MISSILE_TRACK_WARNING(208) owner-only
    private const int CoalesceOwnMining    = 9;   // FM_S_OWN_SHIP_MINING_UPDATE(206) owner-only
    // Key 10 is retired (P773: the port-only kill-bonus update, fmid 212);
    // do NOT renumber the other keys.
    private const int CoalesceOwnCloak     = 13; // FM_S_OWN_SHIP_CLOAK_UPDATE(220) owner-only (HUD-12)
    // P608 — the OWNER's own-ship SingleShipUpdate(78) position stream, on its
    // OWN coalesce key, deliberately DISTINCT from CoalesceShipStates (=1, which
    // carries the per-tick teammate position batch). These are sent back-to-back
    // in the same tick (own first, teammate batch second) for the SAME peer; if
    // they shared a key, the teammate batch would supersede the owner's own pose
    // IN PLACE (TcpTransport.PeerSender.Enqueue latest-wins) and the owner's own
    // ship state would be coalesced away before it ever reached the pool writer.
    // A bot steers purely from its own pose via TryCsPose (no local prediction,
    // unlike the native client) and flies on a stale pose — the pool-mode
    // "wandering" of P608. Own-ship is ALSO sent guaranteed (never shed under
    // capacity) but still coalesce-keyed (latest-wins against itself → bounded),
    // matching native: the own ship's SINGLE_SHIP_UPDATE is sent FM_GUARANTEED
    // (fsship.cpp:544) and guaranteed DirectPlay messages use
    // DPNSEND_GUARANTEED|COALESCE, never timing out (Messages.cpp:548-563).
    private const int CoalesceOwnShip = 12;  // FM_S_SINGLE_SHIP_UPDATE(78) OWN ship only, guaranteed

    // P606 — the disconnect-summary cadence (ticks between window summaries).
    // ~5 s at the default 20 updates/s, roughly matching the profiler window.
    private const long DisconnectSummaryTicks = 100;

    // P374 — per-side last-sent sector asset mask (ClusterWarnings), used to
    // edge-detect warning-level / secured / lost transitions for the client
    // cluster-warning system. Keyed [sideId][sectorId] = last AssetMask sent to
    // that side. Missing sector entries default to 0 (no threat), so the first
    // non-zero mask for a sector is broadcast the tick a side first sees it.
    private readonly Dictionary<short, Dictionary<short, short>> _lastSectorAssetMaskBySide = new();

    /// <summary>Pump transport, advance sim by exactly one period, tick every mission.</summary>
    private void SimPass()
    {
        PrintRequestedSkills();
        if (SinglePlayerPaused)
        {
            // Keep transport liveness running across an arbitrarily long pause.
            // Dispatch admits only PING while the local world is frozen.
            Step("pausedDrain", () => _host.Drain(HandleTransportEvent));
            return;
        }
        // P600 — the whole-SimPass wall time is the profiler's "tick" reference.
        // Closed by EndTick at the bottom of the method, folding per-bucket
        // samples into the report window. No-op when the profiler is off.
        TickProfiler.EnterTick();
        // P314 — every stage runs inside its own exception barrier (Step): a
        // bad tick degrades gracefully (log + skip that stage) instead of
        // propagating to the top-level catch and killing the server. Stages are
        // independent; one failure never cascades into its siblings.
        BeginBroadcastSnapshot();
        var inboundTrace = _host.InboundTrace;
        double drainMs = 0;
        Step("drain", () =>
        {
            long started = inboundTrace is not null ? Stopwatch.GetTimestamp() : 0;
            _host.Drain(HandleTransportEvent);
            if (inboundTrace is not null) drainMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        });
        if (inboundTrace is not null)
            Step("inboundTrace", () => inboundTrace.EndTick(TickIndex, drainMs));
        BroadcastBeforeMissionUpdate();
        lock (_statsLock) _simNow += EffectiveSimPeriod;   // P612 — period × sim.timeScale for slow motion
        double now;
        lock (_statsLock) now = _simNow;
        Step("update", () =>
        {
            // Iterate a SNAPSHOT, not _missions itself: a mission can end during
            // its own Update (native fires CheckForVictoryByFlags inside
            // fsmission.cpp's mission sweep), and DeclareGameOver →
            // RotateMissionAfterGameOver swaps _missions[idx] in place. A C#
            // List<T> enumerator is invalidated by an indexer set (the version
            // guard), so direct iteration would throw "Collection was modified"
            // the next MoveNext — P439's comment that index-swap keeps the
            // enumerator valid is incorrect for List<T>. A snapshot decouples
            // the loop from the rotation; cost is one tiny array per tick.
            foreach (var m in _missions.ToArray()) m.Update(now);
        });
        // P590 — the proactive pod-pickup assignment pass. Each tick, every
        // flying friendly pod that no rescue-capable ship is already diving for
        // gets an idle same-side rescue-capable diverter assigned (PickupTarget),
        // so bots come to pick up pods unprompted (scope 3). Runs right after
        // the mission tick so the assignment steers from the next ship OnUpdate.
        Step("podPickupAssign", () => ResolvePodPickupAssignments());
        // P424 — auto-reject real players' pending queued orders whose 15s
        // inaction window elapsed this tick (native's client update-loop check
        // at WinTrek.cpp:7686-7687, c_dtRejectQueuedCommand=15.0f). Server-side
        // because this port's real-player queued slot lives server-side.
        Step("queuedCommandAutoReject", () => AutoRejectExpiredQueuedCommands(now));
        // P381 — auto-undock docked NPC drones that have sat docked long enough
        // (native CclusterIGC::Update's per-tick launch loop, clusterIGC.cpp:106-
        // 140). Runs right after the mission tick so a drone that just finished
        // its dock-sell in THIS tick is eligible from the next.
        Step("npcAutoLaunch", () => RunNpcAutoLaunch(now));
        // P188 — advance any STAGE_STARTING start-countdowns: a mission whose
        // recorded target start time has passed crosses into STAGE_STARTED and
        // broadcasts ENTER_GAME (the deferred form of P180's auto-start /
        // START_GAME's manual trigger). Runs right after the mission tick so the
        // transition lands the same tick its deadline elapses.
        Step("startCountdowns", () => AdvanceStartCountdowns(now));
        // P598 — advance any auto-restart (bAutoRestart) deadlines: a
        // NotStarted mission armed after a game-over rotation whose restart
        // deadline has passed enters the start countdown without a ready-up
        // (native's unattended restart, fsmission.cpp:3539-3546).
        Step("researchFinales", () => AdvanceResearchFinales(now));
        Step("lastBaseObjectives", () => AdvanceLastBaseObjectives(now));
        Step("autoRestarts", () => AdvanceAutoRestarts(now));
        // P765 — drain the deferred game-over migration (fresh-joiner join bursts
        // to migrated peers) a bounded slice per tick, so the game-over tick and
        // every following tick stay under the per-tick budget instead of one tick
        // carrying the whole superlinear join-burst cost (the second half of the
        // P765 ~0.7 s stall; the first, the ~0.5 s plan-selection re-run, is gone
        // via the SelectShowcasePlans cache). Cheap no-op when nothing pending.
        Step("gameOverMigration", () => DrainPendingRotation());
        // P660: refresh the absolute sim-clock sample during both timer phases.
        // This PING broadcast is a port adaptation; native SET_START_TIME stays unchanged.
        if (TickIndex % Math.Max(1, _config.UpdatesPerSecond) == 0)
            Step("countdownClock", () =>
            {
                var clock = FedPacket.Pack(new[] { new Ping { TimeServer = (uint)(now * 1000) }.Encode() });
                foreach (var m in _missions)
                    if (_autoRestartDeadline.ContainsKey(m) || _startCountdownDeadline.ContainsKey(m))
                        SendToMissionPeers(m, clock);
            });
        // P281/P763 — the side-select drain: seat ready pending peers in logon
        // order (deterministic). A silent peer is auto-seated only when the
        // opt-in SideSelectAutoSeatSeconds adaptation is enabled (default off).
        Step("sideSelection", () => AdvanceSideSelection(now));
        // P813c (EXPERIMENTAL) — expire parked reconnect-grace seats: a peer that
        // did not come back inside its window gets exactly the native drop
        // removal the flag-off path ran immediately (FedSrv.CPP:4549-4566). Cheap
        // no-op (an empty table) with the feature off.
        Step("reconnectGrace", AdvanceReconnectGrace);
        // P187 — resolve pending ballots this tick (the port of native
        // CFSMission::Update's ballot loop, fsmission.cpp:2585-2592): any
        // ballot whose expiration passed or whose votes are all in is tallied
        // and resolved (pass/fail), its type effect applied, then removed.
        // Runs on the sim thread right after the mission tick so a ballot
        // created during this tick's transport drain is resolved this same
        // tick if it already has every vote (e.g. an initiator-only side).
        Step("ballots", () => { foreach (var m in _missions) ResolveBallots(m, now); });
        Step("periodicRules", () => AdvancePeriodicRules(now));
        lock (_statsLock)
        {
            _loopIterations++;
            _liveShipCount = _peerShips.Count;
            // P761 — refresh the pilot-name snapshot alongside the count (both
            // read off-thread by the lobby heartbeat; never enumerate
            // _peerNames outside this lock). Same keys as _peerShips.
            _liveShipNames = new List<string>(_peerNames.Values);
            // P806 — the game settings the directory publishes for the
            // server-list filters (native MissionInfo, read off-thread by the
            // lobby heartbeat like the two snapshots above).
            _liveGameParams = _missions.Count > 0 ? LobbyGameParams.FromMission(_missions[0]) : null;
        }
        // P112 — broadcast ripcord aborts raised during this tick's per-tick
        // revalidation pass (Sector.ResolveRipcordCountdowns). The sim cleared
        // each ripper's ripcord state; here the host emits RIPCORD_ABORTED(187)
        // to everyone flying in the ripper's own sector, reusing P111's
        // SendRipcordSectorWide helper (FedSrv.CPP:13427-13434, the native's
        // GetGroupSectorFlying broadcast). Runs on the sim thread, so it may
        // send without locking, exactly like the request handler.
        Step("ripcordAborts", () =>
        {
            foreach (var m in _missions)
                foreach (var sector in m.Sectors)
                    foreach (var rip in sector.DrainRipcordAborts())
                        if (rip.Sector is { } ripSec)
                            SendRipcordSectorWide(ripSec,
                                new RipcordAborted { ShipidRipcord = rip.Id }.Encode());
        });
        // P113/P754b — the ripcord teleports a DEFERRING site left for the drain.
        // Server.PerformRipcordTeleport is the port of UseRipcord
        // (FedSrv.CPP:13440-13500): it relocates the ripper (station/ship anchor),
        // pays the energy cost, broadcasts RIPCORD_ABORTED on
        // energy-insufficiency, and clears the ripcord state. The production host
        // no longer defers: ServerHost.UseRipcord performs it inside the callback
        // (wired to this same method in Server.Lifecycle), exactly as native runs
        // it in CshipIGC::Update. This step remains the native false-at-zero
        // retry path for a site that answers false (shipIGC.cpp:288-289).
        Step("ripcordTeleports", () =>
        {
            foreach (var m in _missions)
                foreach (var sector in m.Sectors)
                    foreach (var rip in sector.DrainRipcordTeleports())
                        PerformRipcordTeleport(rip);
        });
        // P115 — perform the rescues this tick's proximity pass found
        // (Sector.ResolveRescueEvents). Server.PerformRescue is the port of
        // RescueShipEvent (FedSrv.CPP:13033-13074): it sends PLAYER_RESCUED(16)
        // to both ships' connections, then places the rescued ship at the
        // nearest friendly station. Runs on the sim thread before the broadcast
        // below so the docked rescuee's state is picked up by the same tick's
        // SINGLE_SHIP_UPDATE / SHIP_STATUS.
        Step("rescues", () =>
        {
            foreach (var m in _missions)
                foreach (var sector in m.Sectors)
                    foreach (var (rescuee, rescuer) in sector.DrainRescues())
                        PerformRescue(rescuee, rescuer);
        });
        // P301 — perform the station-side pod rescues this tick's proximity
        // pass found (Sector.ResolveStationRescueEvents). A drifting lifepod
        // that reached a same-side c_sabmRescue station is docked there (the
        // rescue-half of native shipIGC.cpp:862 → RescueShipEvent(pod, NULL)),
        // which cancels the P279 oxygen timer. Unlike the P115 ship rescue
        // there is NO PLAYER_RESCUED wire — native's NULL-rescuer branch skips
        // it (FedSrv.CPP:13033-13057). Runs on the sim thread before the
        // broadcast below so the docked rescuee's state is picked up by the
        // same tick's SINGLE_SHIP_UPDATE / SHIP_STATUS.
        Step("stationRescues", () =>
        {
            foreach (var m in _missions)
                foreach (var sector in m.Sectors)
                    foreach (var (rescuee, station) in sector.DrainStationRescues())
                        PerformStationRescue(rescuee, station);
        });
        Step("podTrips", () => _podTrips.Sample(now, _missions));
        // P754b — carrier relaunches are NOT drained any more: the landing is
        // performed inside the site callback itself (ServerHost.LandOnCarrierEvent
        // → Server.PerformRelaunch), which is exactly where native runs it
        // (FedSrv.CPP:12814-13031, reached from the collision pass). The sector's
        // DrainCarrierLandings queue now only feeds mission-less detection
        // fixtures (P116).
        // P123 — perform the cash-treasure pickups this tick's proximity pass
        // found (Sector.ResolveTreasurePickups). Server.PerformTreasurePickup
        // is the port of the c_tcCash branch of HitTreasureEvent followed by
        // KillTreasureEvent (FedSrv.CPP:13589-13616, 12493-12509): credit the
        // picking ship's side (pooled Side.Money), broadcast MONEY_CHANGE
        // side-wide, then destroy the treasure + broadcast DESTROY_TREASURE
        // whole-mission. Runs on the sim thread before the broadcast below.
        Step("treasurePickups", () =>
        {
            foreach (var m in _missions)
                foreach (var sector in m.Sectors)
                    foreach (var (ship, treasure) in sector.DrainTreasurePickups())
                        PerformTreasurePickup(ship, treasure);
        });
        // P164 — broadcast the building-effect interruptions this tick's pass
        // found (Sector.ResolveBuildingEffects). Server.BroadcastBuildingEffectDestroyed
        // is the port of KillBuildingEffectEvent's wire half
        // (FedSrv.CPP:12484-12492): the target asteroid's id goes out
        // whole-mission (GetGroupRealSides) to every peer, mirroring the
        // DESTROY_TREASURE broadcast exactly. Runs on the sim thread before the
        // broadcast below.
        Step("buildingEffectInterruptions", () =>
        {
            foreach (var m in _missions)
                foreach (var sector in m.Sectors)
                    foreach (var astId in sector.DrainBuildingEffectInterruptions())
                        BroadcastBuildingEffectDestroyed(astId, m);
        });
        // P294 — broadcast the building-effect creations this tick's
        // StartBuildingEffect registered (a Builder committed to a new
        // construction). Server.BroadcastBuildingEffectCreated is the port of
        // FedSrvSiteBase::CreateBuildingEffect's broadcast half
        // (FedSrv.CPP:13733-13767): the fresh effect goes out as an
        // EXPORT(26)/OT_buildingEffect, the mirror image of the interruption
        // drain just above. Runs on the sim thread before the broadcast below.
        Step("buildingEffectCreations", () =>
        {
            foreach (var m in _missions)
                foreach (var sector in m.Sectors)
                    foreach (var effect in sector.DrainBuildingEffectCreations())
                        BroadcastBuildingEffectCreated(effect);
        });
        // P349 — cross-side ship identity EXPORT(26) delivery, redesigned from
        // P346's edge-triggered transition-QUEUE into a per-tick catch-up SWEEP
        // that mirrors the same-side [teamexport]'s own loop shape
        // (BroadcastShipStates): every connected peer, every ship its side
        // currently sees (P132's live SeenBySide result — the visibility
        // DETERMINATION is untouched), send the EXPORT iff a per-recipient
        // `_crossSideExportSent[(peer, ship)]` latch isn't set yet. One
        // mechanism covers BOTH "ship just became visible" (the sweep picks it
        // up the tick the P132 sweep flips SeenBySide true) AND "peer just
        // joined an already-informed side" (the sweep catches the newcomer up on
        // its very next tick) — the two cases P346's one-shot queue could not
        // reconcile at once. The transition queue is subsumed: it only fired on
        // the false→true edge to whichever peers happened to be connected at
        // that instant and then forgot late joiners, which is exactly the
        // vizfight `roster` failure (all 72 bots became mutually visible before
        // the human spectator connected). Runs after Mission.Update so the
        // latched SeenBySide flags reflect this tick's sweep.
        StartBroadcastProjectionMeasurement();
        Step("crossSideExportSweep", () => BroadcastAfterMissionUpdate(SweepCrossSideExports));
        // P453 — cross-side MINE EXPORT(26)/OT_mine delivery, the mine analogue
        // of the ship sweep just above. Mines joined the P132 visibility sweep
        // as side-visible targets (they are c_mtSeenBySide|c_mtPredictable
        // static objects in native, mineigc.cpp:103-107), so an enemy side's
        // scanner net now spots them; this per-(peer, mine) latch sends each
        // seeing side's seated peers the EXPORT exactly once, catching up late
        // joiners the same way the ship sweep does. The laying side's immediate
        // export (BroadcastExportMine) is unchanged. Runs after Mission.Update
        // so the latched SeenBySide flags reflect this tick's sweep.
        Step("crossSideMineExportSweep", SweepCrossSideMineExports);
        // P797 — cross-side DISCOVERY of the static objects native gates per side:
        // alephs, asteroids (+ their building effects), probes and treasures. The
        // static sibling of the two sweeps above, driven by the same latched
        // SeenBySide flags (which now include warps, and latch for every
        // c_mtPredictable object per FedSrv.CPP:11124-11126). Reads this tick's
        // post-Mission.Update flags and sends each newly-seen object + its
        // OBJECT_SPOTTED(6) to the seeing side's peers exactly once. Stations keep
        // their own P747 identity path (ExportDiscoveredStations); mines theirs
        // (P453).
        Step("crossSideDiscoverySweep", SweepCrossSideDiscoveryExports);
        // P493 — cross-side SHIP_STATUS(90) delivery of an enemy ship's
        // LAST-KNOWN sector/state to a side that comes to see it through its
        // scanner net (probes/ships/scan range, the P132/P270/P476 rules), and
        // the HIDDEN transition when that side loses sight — the port of native
        // ShowObject→ShipStatusSpotted / HideObject→ShipStatusHidden
        // (FedSrv.CPP:10838/10870, fsship.cpp:237/287). Runs after Mission.Update
        // AND after the cross-side EXPORT sweep (which precedes every status, so
        // a freshly-spotted enemy's identity EXPORT lands before its status —
        // native's ExportObject-then-status ordering). Edge-driven: a status is
        // sent only when a side's visibility of a ship CHANGES (plus a slow
        // catch-up refresh), never every tick the ship stays visible.
        Step("crossSideShipStatusSweep", () => BroadcastAfterMissionUpdate(SweepCrossSideShipStatuses));
        // P761b — relay every ship's changed c_cmdAccepted slot to its side
        // group (native FedSrvSiteBase::CommandChangedEvent,
        // FedSrv.CPP:13695-13732). The client stores it on the ship entity, which
        // is what the team pane's OBJECTIVE column sorts by
        // (CurrentCommandText, WinTrek/teampane.cpp:1528-1534, 1660-1676).
        Step("acceptedCommandSweep", SweepAcceptedCommands);
        // HUD-17 — recompute each own-side ship's Detected bit from the native
        // rule (ShipDetection.IsDetectedByEnemy: an enemy, i.e. non-own,
        // non-allied, side's known SeenBySide status) and mark the ships whose
        // bit changed this tick. BroadcastShipStates consumes the mark and
        // re-sends that ship's status immediately, mirroring native's
        // per-frame status delta (FedSrv/FedSrv.CPP:3663-3700: on odd frames,
        // only a CHANGED ShipStatus is sent). Runs before broadcastShipStates,
        // after Mission.Update, so SeenBySide reflects this tick's sweep.
        Step("shipDetection", () => BroadcastAfterMissionUpdate(SweepShipDetection));
        // Step 3: after the mission tick, broadcast every peer ship that moved.
        // Runs on the sim thread, so "only Mission.Update moves ships" holds —
        // this is purely an observation send of the authoritative state.
        // P314: the concretely-reachable crash lived HERE — a teammate's
        // ExportShip.Encode() throws past 255 parts, and BroadcastShipStates
        // used to let that escape and kill the server. The barrier + the
        // MountPart part-count cap (Ship.cs) close it.
        Step("broadcastShipStates", () => BroadcastAfterMissionUpdate(() => BroadcastShipStates((uint)TickIndex)));
        // P133 — periodic per-side STATIONS_UPDATE(57): for each side with
        // seated peers, batch every station that side currently sees (P132's
        // live per-tick sweep) into one message and send it to that side's
        // connections only. Runs every tick, the same per-tick pass shape the
        // port's convention settles for the now-unblocked PROBES_UPDATE(92) /
        // ASTEROIDS_UPDATE(179) siblings.
        Step("broadcastStations", () => BroadcastStationsUpdate());
        // P134 — periodic per-side ASTEROIDS_UPDATE(179): for each side with
        // seated peers, batch every asteroid that side currently sees (P132's
        // live sweep, extended in P134 to score asteroids as targets) into one
        // message and send it to that side's connections only. The per-side
        // sibling of STATIONS_UPDATE above, same every-tick cadence.
        Step("broadcastAsteroids", () => BroadcastAsteroidsUpdate());
        // P135 — periodic per-side PROBES_UPDATE(92): for each side with
        // seated peers, batch every probe that side currently sees (P132's
        // live sweep already scores probes — they are Models with a SideId,
        // unlike the asteroids P134 had to add) into one message and send it to
        // that side's connections only. The third and last of the three
        // periodic per-side syncs P131/P132 unblocked, closing the cluster.
        Step("broadcastProbes", () =>
        {
            BroadcastProbesUpdate();
            // P324 (M4): the per-side object syncs ran — consume this SimPass's
            // join-dirty markers so next SimPass starts with a clean slate (only
            // future joins or the cadence re-arm them).
            _objectSyncDirtySides.Clear();
        });
        // P139 — periodic PER-PLAYER LIGHT/HEAVY_SHIPS_UPDATE (175/176): the
        // consumer of P138's per-cluster candidate list. For each seated peer,
        // resolve their real command target (Ship.CommandTarget) — a ship target
        // fills the FIRST heavy slot and drives the bpTargetHull/bpTargetShield
        // scalars — then broadcast every remaining candidate in the peer's
        // cluster (minus their own ship) as heavy records to that peer's OWN
        // connection only. Per-player, not per-side: this family's send target
        // is each individual connection (contrast the STATIONS/ASTEROIDS/PROBES
        // per-side broadcasts above). Runs every tick, the port's established
        // per-tick cadence; see BroadcastShipsUpdate for the heavy/light split
        // decision.
        Step("broadcastShipsUpdate", () => BroadcastAfterMissionUpdate(() => BroadcastShipsUpdate((uint)TickIndex)));
        // P374 — per-side, change-driven sector-threat (ClusterWarning) sync:
        // for each side with seated peers, recompute each sector's friendly/enemy
        // asset mask from data this server already tracks (Sector._models ships'
        // PilotType/HullType capabilities + StationRuntimes side) and send a
        // ClusterWarningUpdate(214) to that side's members only when that side's
        // view of the sector changed (warning level and/or secured/lost). Native
        // computes this client-side each frame; this renderer-only client has no
        // static core, so the server streams it (see ClusterWarningUpdate).
        Step("clusterWarnings", () => BroadcastSectorThreats());
        Step("shipWarnings", () => BroadcastShipWarnings());
        // P230 — per-tick, OWNER-ONLY MissileLockUpdate(204) of each armed peer's
        // missile-lock quality + target, so the MonoGame client can render the
        // lock tone/tips/target-box (native's client computed the lock locally and
        // needed no wire; this renderer-only client has no static core, so P230
        // exposes the server's authoritative lock to the owner — see
        // docs/MISSILE_LOCK_FEEDBACK_P230.md for the A-vs-B decision).
        Step("broadcastMissileLock", () => BroadcastMissileLock());
        // P234 — per-tick, same-side MissileUpdate(205) of every in-flight
        // missile's current position/heading, so the MonoGame client can place a
        // real missile model at the missile's actual location. Native's client
        // simulated missiles locally and needed no ongoing position message; this
        // renderer-only client has no static core, so P234 streams the server's
        // authoritative state (see docs/MISSILE_RENDERING_P234.md for the A-vs-B
        // decision). Per-side batched like the PROBES/ASTEROIDS/STATIONS syncs.
        Step("broadcastMissiles", () => BroadcastMissilesUpdate());
        // P255 — per-tick, OWNER-ONLY OwnShipMiningUpdate(206) carrying WHICH
        // asteroid the peer's own ship is currently drilling (NA when not
        // mining), so the MonoGame client can orient its own hull toward the
        // rock it is mining. Complements the faithful miningMaskIGC state bit
        // riding the same ship's SingleShipUpdate(78) (BroadcastShipStates);
        // native never transmits the rock id (its client infers it locally),
        // which this renderer-only client cannot — see OwnShipMiningUpdate's
        // doc comment for the P230/P234 owner-only-extension precedent.
        Step("broadcastOwnShipMining", () => BroadcastOwnShipMining());
        // P773 — the port-only per-tick kill-bonus update (fmid 212) owner-only
        // stream was removed: native has no such message; the client derives the
        // own kill bonus from the own ship's experience (PLAYERINFO.fExperience
        // seed + KILL_SHIP updates), exactly as native does.
        // HUD-12 — per-tick, OWNER-ONLY OwnShipCloakUpdate(220) carrying the
        // own ship's continuous GetCloaking() visible fraction, so the HUD can
        // show the native cloak strength (100·(1−Cloaking), consoledata.cpp:480-489)
        // and keep the cloak icon lit through the decloak ramp
        // (IsCloaked = Cloaking < 1, consoledata.cpp:563-575). Native's client
        // simulated this locally; this renderer-only client cannot.
        Step("broadcastOwnShipCloak", () => BroadcastOwnShipCloak());
        // P264 — per-tick, OWNER-ONLY MissileTrackWarningUpdate(208) telling each
        // peer whether an in-sector ENEMY missile's target is its own ship (plus
        // the best lock among them), so the MonoGame client can play the incoming-
        // missile warning loop. Native's client walked its own IGC sim to arm it
        // (trekigc.cpp:5025-5071); this renderer-only client has no static core
        // and the missile wire (70/205/83) carries no target, so the server flags
        // "this missile's target is me" explicitly (see the codec doc for the
        // A-vs-B decision).
        Step("broadcastMissileTrackWarning", () => BroadcastMissileTrackWarning());
        // P774 — the port-only periodic score-roster snapshot (209) /
        // patch (219) stream was removed: native has no
        // roster message. The client derives the roster from PLAYERINFO(10)
        // seeds (HUD-20: every mission ship to every mission peer,
        // FedSrv.CPP:2129-2134) plus KILL_SHIP(105) counter updates
        // (appmsg.cpp:2766-2773,2805-2808), exactly as native's client does.
        // P804 — the port-only P266 lead-indicator stream (210) is retired:
        // native has no such message, and the client now solves the lead itself
        // from the IGC state it holds (RenderLeadIndicator, indicator.cpp:183-405;
        // the shared Core LeadSolver). No native wire field was involved, so
        // nothing is kept "for compatibility".
        CompleteBroadcastSnapshot();
        // P606 — per-window disconnect-reason summary. Drains the transport's
        // disconnect counters (throttled to ~the profiler report cadence) so the
        // log carries a rolling "disconnects by reason" count. Every disconnect
        // is logged individually by the transport with its reason; this is the
        // aggregated window view.
        Step("disconnectSummary", () =>
        {
            if ((long)TickIndex % DisconnectSummaryTicks != 0) return;
            if (_host.Transport is { } tcp)
            {
                var counts = tcp.DrainDisconnectCounts();
                if (counts.Count > 0)
                {
                    string body = string.Join(", ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
                    Console.WriteLine($"[disconnect-summary] window: {body}");
                }
            }
            EmitBroadcastSnapshotMetrics();
            // P813c — the bounded reconnect-grace telemetry line for the rig A/B
            // (one line per window; silent with the feature off).
            EmitReconnectSummary();
            // P670 — simulation payload offers versus actual transport enqueue / socket acceptance.
            // Transport counters include framing; offers do not. Use measured wall time.
            long bytes = Interlocked.Exchange(ref _egressPayloadOfferedBytes, 0);
            long packets = Interlocked.Exchange(ref _egressPacketsOffered, 0);
            long stamp = System.Diagnostics.Stopwatch.GetTimestamp();
            double windowSecs = Math.Max(1e-9, (stamp - _egressWindowStarted) / (double)System.Diagnostics.Stopwatch.Frequency);
            _egressWindowStarted = stamp;
            long totalEnqueued = _host.Transport?.TotalEnqueuedBytes ?? 0;
            long enqueued = totalEnqueued - _lastEgressEnqueuedBytes;
            _lastEgressEnqueuedBytes = totalEnqueued;
            long totalWritten = _host.Transport?.TotalWrittenBytes ?? 0;
            long written = totalWritten - _lastEgressWrittenBytes;
            _lastEgressWrittenBytes = totalWritten;
            int peers = _peerShips.Count;
            double perPeerBps = peers > 0 ? enqueued / windowSecs / peers : 0d;
            // P604b — GC collections this window, so an allocation-burst-induced
            // tick hitch can be tied to a GC pause (Gen0/1/2 counts are monotonic
            // per-process; the delta over the window is what matters).
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            // P619 — total managed bytes allocated this window (÷100 ticks ≈
            // bytes/tick), for the port-mode allocation A/B.
            long alloc = GC.GetTotalAllocatedBytes();
            long logDrops = ConsoleLog.DroppedWrites;
            Console.WriteLine($"[egress] window: payloadOfferedBytes={bytes} packetsOffered={packets} enqueuedBytes={enqueued} writtenBytes={written} peers={peers} windowSecs={windowSecs:0.###} enqueued-per-peer-bps={perPeerBps:0.0} written-per-peer-bps={(peers > 0 ? written / windowSecs / peers : 0):0.0} gc={{gen0={g0 - _lastGc0},gen1={g1 - _lastGc1},gen2={g2 - _lastGc2}}} allocMB={(alloc - _lastAllocatedBytes) / (1024.0 * 1024.0):0.##} logDrops={logDrops - _lastLogDrops}");
            _lastGc0 = g0; _lastGc1 = g1; _lastGc2 = g2; _lastAllocatedBytes = alloc; _lastLogDrops = logDrops;
        });
        // P507 — every ~10s, a per-peer acceptance summary of the native
        // own-ship model (accepted / rejected / out-of-date / invalid counts +
        // mean reject distance), so a live playtest can see it working at a
        // glance. Skips a peer that saw no updates since the last summary.
        Step("shipUpdateSummaries", () =>
        {
            foreach (var kv in _peerShipUpdate)
            {
                var pu = kv.Value;
                if (now - pu.LastSummaryAt < 10.0) continue;
                pu.LastSummaryAt = now;
                if (pu.Accepted + pu.Rejected + pu.OutOfDate + pu.Invalid
                    + pu.DroppedCookie + pu.DroppedNoShip + pu.DroppedPreGame
                    + pu.DroppedBoarded + pu.DroppedNotInCluster == 0) continue;
                string mean = pu.RejectCount > 0 ? (pu.RejectDistanceSum / pu.RejectCount).ToString("0.##") : "n/a";
                Console.WriteLine($"[ctrlak] peer {kv.Key} ship-update summary: corrections-total={pu.TotalCorrections} replay={pu.DroppedReplay} same-tick={pu.DroppedSameTick} accepted={pu.Accepted} rejected={pu.Rejected} out-of-date={pu.OutOfDate} invalid={pu.Invalid} mean-reject-dist={mean}m (cookie 0x{pu.Cookie:X2})"
                    + $" reject-reasons[speed={pu.RejectedSpeed} envelope={pu.RejectedEnvelope} travel={pu.RejectedTravel}]"
                    + (pu.DroppedCookie + pu.DroppedNoShip + pu.DroppedPreGame + pu.DroppedBoarded + pu.DroppedNotInCluster > 0
                        ? $" dropped[cookie={pu.DroppedCookie} noship={pu.DroppedNoShip} pregame={pu.DroppedPreGame} boarded={pu.DroppedBoarded} notincluster={pu.DroppedNotInCluster}]"
                        : ""));
            }
        });
        // P600 — close the tick's wall-time reference and roll the profiler window.
        TickProfiler.EndTick();
        if (Interlocked.Exchange(ref _updateAgeReportRequested, 0) != 0)
            EmitUpdateAgeWindow();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // P590 — proactive pod-pickup assignment (bots come to pods unprompted)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>P590 — the per-tick proactive pod-pickup assignment pass (Scope 3:
    /// \"bots pick up pods\"). For every flying friendly <c>c_habmLifepod</c> pod in
    /// an in-flight mission that no rescue-capable ship is already diving for, the
    /// nearest idle same-side rescue-capable AI (<c>IsNpc</c>) ship within
    /// <see cref=\"PodPickupRange\"/> is assigned to it (<c>Ship.PickupTarget</c> =
    /// the pod), at most one diverter per pod. The assigned ship's
    /// <c>ApplyNpcAutopilot</c> then steers it to the pod and the existing
    /// <c>ResolveRescueEvents</c> contact pass performs the rescue; the goal
    /// self-clears when the pod is rescued/docked/gone, returning the diverter to
    /// its task cycle.</para>
    /// <para><b>Idle + combat-priority gate.</b> Only an <em>idle</em> diverter is
    /// assigned — a ship that is goto-ing, mining, building, attacking or
    /// defending keeps its task (no active goal slot is occupied), matching the
    /// brief's \"don't abandon a dogfight\" rule and the native drone-default-order
    /// precedence. <em>AI-driven only</em>: a client-driven peer's flight is the
    /// controller's, so the pass never autopilots one (its pickup order still
    /// reaches it via the c_cidPickup relay, part 2).</para>
    /// <para><b>At most one diverter per pod.</b> A pod already being dived for
    /// (<c>PickupTarget == pod</c> on any ship) is skipped, so two bots never
    /// swarm the same pod.</summary>
    private void ResolvePodPickupAssignments()
    {
        foreach (var m in _missions)
        {
            if (m.Stage != MissionStage.Started) continue;
            foreach (var sector in m.Sectors)
            {
                var ships = sector.GetShipUpdateCandidates();
                // Flying friendly pods: alive, not docked, lifepod, sided.
                foreach (var pod in ships)
                {
                    if (pod.SideId is not { } podSide) continue;
                    if (pod.Dead || pod.StationId is not null) continue;
                    if (!pod.HullType.HasCapability(DataHullTypeIGC.HabmLifepod)) continue;

                    // At most one diverter per pod (already claimed?).
                    bool claimed = false;
                    foreach (var s in ships)
                        if (!ReferenceEquals(s, pod) && ReferenceEquals(s.PickupTarget, pod)) { claimed = true; break; }
                    if (claimed) continue;

                    // Nearest idle, same-side, rescue-capable AI diverter in range.
                    Ship? best = null;
                    float bestD2 = PodPickupRange * PodPickupRange;
                    foreach (var s in ships)
                    {
                        if (ReferenceEquals(s, pod)) continue;
                        if (!s.IsNpc) continue;                     // AI-driven only
                        if (s.Dead || s.StationId is not null) continue;
                        if (s.SideId != podSide) continue;
                        if (!s.HullType.HasCapability(DataHullTypeIGC.HabmRescue)) continue;
                        if (!IsPickupIdle(s)) continue;             // combat/task priority
                        float dx = s.Position.X - pod.Position.X;
                        float dy = s.Position.Y - pod.Position.Y;
                        float dz = s.Position.Z - pod.Position.Z;
                        float d2 = dx * dx + dy * dy + dz * dz;
                        if (d2 > bestD2) continue;
                        bestD2 = d2;
                        best = s;
                    }
                    if (best is null) continue;

                    best.PickupTarget = pod;
                    Console.WriteLine($"[pickup] proactive: pod {pod.Id} (side {podSide}) <- diverter {best.Id} (rescue-capable AI, {MathF.Sqrt(bestD2):0}u) assigned");
                }
            }
        }
    }

    /// <summary>P590 — is <paramref name=\"s\"/> idle enough to divert to a pod?
    /// False while any higher-priority NPC goal slot is occupied (goto / station
    /// plan / mine / layer / attack / defend / an existing pickup) — the combat-
    /// and-task priority rule. A ship actively attacking (engaged) never abandons
    /// that dogfight for a pod.</summary>
    private static bool IsPickupIdle(Ship s)
        => s.GotoWaypoint is null
        && s.PlanTarget is null
        && s.MiningAsteroidTarget is null
        && s.LayerAsteroidTarget is null
        && s.AttackTarget is null
        && s.DefendTarget is null
        && s.PickupTarget is null;

    /// <summary>P590 — the proactive pickup assignment range (world units). A
    /// rescue-capable AI diverter must be within this distance of a pod to be
    /// assigned to it. Generous (several km) so a pod in an active sector is
    /// picked up promptly; the brief's \"within N km\" knob.</summary>
    private const float PodPickupRange = 5000f;

    /// <summary>
    /// P603 — dispatcher for the per-peer ship-state broadcast. OFF by default:
    /// delegates straight to the original serial builder
    /// (<see cref="BroadcastShipStatesSerial"/>, byte-for-byte today's behaviour).
    /// When <c>broadcast.parallelBuild</c> is on, runs the snapshot / parallel
    /// build / enqueue split (<see cref="BroadcastShipStatesParallel"/>), whose
    /// per-peer payloads are byte-identical to serial mode.
    /// </summary>
    private void BroadcastShipStates(uint tickIndex)
    {
        if (_broadcastParallelBuild)
            BroadcastShipStatesParallel(tickIndex);
        else
            BroadcastShipStatesSerial(tickIndex);
    }

    /// <summary>For every peer ship that changed position since its last
    /// broadcast, send that peer a <c>FM_S_SINGLE_SHIP_UPDATE(78)</c> carrying the raw
    /// authoritative position/velocity/orientation, timestamped with the tick
    /// index (the uint <c>Time</c> slot). No broadcast when a ship has not
    /// moved (zero thrust ⇒ silence), matching the C++ dirty-state update.
    /// Also re-sends <c>FM_S_SHIP_STATUS(90)</c> for a peer's own ship on a
    /// sector change (immediate) or periodic refresh (see
    /// <see cref="StatusRefreshTicks"/>), and — P58/P62 — periodically re-sends
    /// <c>FM_S_SHIP_STATUS(90)</c> about each same-side peer ship to that peer
    /// (see <see cref="TeammateStatusRefreshTicks"/>), preceded once per
    /// (recipient, subject) pair by a real <c>FM_S_EXPORT(26)</c> so the
    /// teammate ship object exists client-side at all, so a peer's client
    /// builds a full picture of its own side, matching the reference's
    /// per-player status send (<c>fsmission.cpp:4303-4323</c>).
    /// <para><b>P603.</b> This is the SERIAL, byte-for-byte original (default
    /// path). It is invoked unchanged whenever <c>broadcast.parallelBuild</c>
    /// is off; the parallel twin is <see cref="BroadcastShipStatesParallel"/>.</para></summary>
    private void BroadcastShipStatesSerial(uint tickIndex)
    {
        using (TickProfiler.Enter("bss.setup"))
        {
            BroadcastShipStatesSerialInner(tickIndex);
        }
    }

    /// <summary>
    /// P604b — the body of <see cref="BroadcastShipStatesSerial"/> split out so
    /// the per-tick sub-bucket profiler can time the setup (snapshot + side/NPC
    /// grouping) separately from the per-peer mate walk, to isolate the
    /// ~5 s periodic hitch. Kept byte-identical; the split is purely so the
    /// <c>bss.setup</c> / <c>bss.mates</c> buckets are measurable.
    /// </summary>
    private void BroadcastShipStatesSerialInner(uint tickIndex)
    {
        // One snapshot reused for both the own-ship walk and the (P58)
        // same-side teammate walk below, so neither mutates mid-iteration.
        var peers = SnapshotBroadcastPeers();
        // P317 — group peers by side once per tick so each recipient's
        // same-side teammate walk scans only its own side instead of every
        // peer (the old `foreach tv in peers { if different side continue; }`
        // was an O(P²) filtering scan). Members of each list keep `peers`
        // order, so the effective per-side subject order is unchanged.
        var sameSide = GatherBroadcastMates(peers);
        // P317 — one encoded SingleShipUpdate per MOVING subject per tick,
        // shared across every same-side recipient (the bytes for subject A's
        // position are identical for all of A's teammates, so re-encoding per
        // (recipient, subject) pair was pure waste — O(P²) allocations down to
        // O(P)). Lazily created; freed next tick.
        Dictionary<short, BroadcastMessage>? positionEncode = null;
        // P366 — same-side NPC ships grouped by (mission, side), built once per
        // tick so the per-recipient pass below doesn't re-scan the whole model
        // list for each peer (the same one-per-tick costing as the P346 cross-side
        // sweep). These are the AI bot teammates a team leader must be able to
        // see, select and command to dock/launch — invisible to peers today.
        Dictionary<(Mission Mission, short Side), List<Ship>>? sameSideNpcBySide = null;
        // P366 — gate the per-tick scan to in-flight missions (Started/Starting),
        // mirroring SweepCrossSideExports:792's precondition; same-side bots only
        // matter for an in-mission team-leader command, and skipping
        // NotStarted/empty missions keeps the scan off the pre-game tick path.
        foreach (var m in _missions)
        {
            if (!IncludeShipBroadcastMission(m)) continue;
            if (m.Stage != MissionStage.Started && m.Stage != MissionStage.Starting) continue;
            foreach (var sec in m.Sectors)
                foreach (var mdl in sec.Models)
                    if (mdl is Ship npc && npc.IsNpc && npc.SideId is { } npcSide)
                    {
                        sameSideNpcBySide ??= new();
                        if (!sameSideNpcBySide.TryGetValue((m, npcSide), out var list)) { list = new(); sameSideNpcBySide[(m, npcSide)] = list; }
                        list.Add(npc);
                    }
        }
        foreach (var kv in peers)
        {
            var live = kv.Value;
            var ship = live.Ship;
            // Position OR orientation dirty — a pure rotation (roll/pitch/yaw
            // with no thrust) changes no position at all, so the position-only
            // check alone would never re-broadcast it to the owning peer (see
            // LiveShip's doc remarks). Forward catches pitch/yaw; up catches
            // roll (which leaves forward unchanged).
            var fwd = ship.Orientation.GetForward();
            var up = ship.Orientation.GetUp();
            // P255 — the port's per-tick "mining this tick" signal: the rock the
            // mining pass (Sector.ResolveMining) set on the ship this tick (P254
            // MiningAsteroid), reset to null the moment no rock qualifies (moves /
            // leaves proximity / bag full). Faithful to native's per-tick
            // miningMaskIGC maintenance (shipIGC.cpp:2206-2256); unlike the
            // latched IsMining transition flag this one properly CLEARS, so the
            // client's mining cue drops the instant mining stops.
            bool miningNow = ship.MiningAsteroid is not null;
            // P332 — `PositionEverBroadcast` forces the VERY FIRST SingleShipUpdate
            // to the owning peer regardless of motion: LastBroadcast* is seeded
            // from the ship's own real spawn values, so a ship that never moves
            // would otherwise show a permanent zero diff against every dirty term
            // below and be skipped forever (invisible even to its own client).
            if (!live.PositionEverBroadcast
                || (ship.Position - live.LastBroadcastPos).Length() >= DirtyEpsilon
                || (fwd - live.LastBroadcastForward).Length() >= DirtyEpsilon
                || (up - live.LastBroadcastUp).Length() >= DirtyEpsilon
                || miningNow != live.LastBroadcastMining
                // P391 — shield regeneration (P389) climbs a stationary ship's
                // ShieldFraction, and that fraction rides this own-ship
                // SingleShipUpdate (ServerShipUpdate.FromModel byte 41). Without
                // this term a ship sitting still (recovering from a fight, docked
                // idle) NEVER re-trips the four motion/mining terms, so the
                // owner's own HUD shield reading went stale for as long as the
                // ship didn't move. Float comparison needs an epsilon (see
                // ShieldFractionEpsilon); an unshielded ship is ShieldFraction≡0
                // so |0−0| never fires — a strict no-op for them.
                || MathF.Abs(ship.ShieldFraction - live.LastBroadcastShieldFraction) >= ShieldFractionEpsilon)
            {
                // P606 — the own-ship position stream is latest-wins traffic:
                // coalesce per peer so a slow client keeps only the newest
                // pending snapshot, never one stale copy per tick.
                // P608 — on its OWN coalesce key (CoalesceOwnShip), NOT
                // CoalesceShipStates, and GUARANTEED: the teammate position batch
                // sent later in this same walk must never supersede the owner's
                // own pose (they shared key 1 in P606, so the batch overwrote the
                // own pose in place and bots flew on a stale pose), and the own
                // pose must never be shed under load. Still latest-wins against
                // itself, so the backlog stays bounded (P606 intact).
                SendPeerCoalescing(kv.Key, PackOwnPose(ship, tickIndex, miningNow), CoalesceOwnShip, guaranteed: true);
                _peerShips[kv.Key] = live with { LastBroadcastPos = ship.Position, LastBroadcastForward = fwd, LastBroadcastUp = up, LastBroadcastShieldFraction = ship.ShieldFraction, LastBroadcastMining = miningNow, PositionEverBroadcast = true };
            }

            // FM_S_SHIP_STATUS(90) sector-change + periodic refresh hook. The
            // strategy's sweep chain is driven by these status frames; resend
            // when the ship crosses a sector (ShipWarped, appmsg.cpp:2723-2729)
            // and periodically so a post-attach strategy receives one (see
            // StatusRefreshTicks). `_lastStatusSector`/`_lastStatusTick` are
            // sim-thread only, same as the rest of this broadcast.
            short sentSector = _lastStatusSector.TryGetValue(kv.Key, out var s0) ? s0 : (short)-2;
            long lastTick = _lastStatusTick.TryGetValue(kv.Key, out var t0) ? t0 : -1;
            bool sectorChanged = live.Sector.Id != sentSector;
            bool refreshDue = lastTick < 0 || (long)tickIndex - lastTick >= StatusRefreshTicks;
            if (sectorChanged || refreshDue || _shipDetectedChanged.Contains(ship.Id))
            {
                // HUD-17 — the third term is native's changed-status delta
                // (FedSrv/FedSrv.CPP:3693-3700): the ship swept a new Detected
                // bit this tick, so its own-side status goes out immediately
                // instead of waiting for the periodic refresh.
                SendPeer(kv.Key, PackStatus(ship.Id, live));
                if (ConsoleLog.Verbose) ConsoleLog.Write($"[shipstatus] peer {kv.Key} " +
                    (sectorChanged ? $"sector change {sentSector} -> {live.Sector.Id}" : $"refresh (sector {live.Sector.Id})"));
                _lastStatusSector[kv.Key] = live.Sector.Id;
                _lastStatusTick[kv.Key] = tickIndex;
            }

            // P48: CLUSTERINFO(200) periodic refresh -- the exact same reason
            // SHIP_STATUS needs one, per this method's own doc comment above:
            // StrategyBase.cs only wires up MessageReceiver.OnAppMessage
            // inside Attach(), which itself only runs after ship assignment
            // -- so the join burst's one-time CLUSTERINFO send (this file,
            // the peer-join handler) is *always* processed by the native
            // client's own internal (pre-managed-subscription) message pump
            // and never reaches the managed handler. Confirmed directly:
            // an unconditional raw-fmid diagnostic across a whole 20s trial
            // showed OnAppMessage only ever receiving fmid 78/90 (the two
            // periodically-resent messages) -- never once 200. A longer
            // interval than StatusRefreshTicks is fine (CLUSTERINFO is
            // static mission data, 4204 B vs SHIP_STATUS's ~24 B, and only
            // needs to land once after the managed subscription exists).
            long lastClusterInfoTick = _lastClusterInfoTick.TryGetValue(kv.Key, out var ci0) ? ci0 : -1;
            if (lastClusterInfoTick < 0 || (long)tickIndex - lastClusterInfoTick >= ClusterInfoRefreshTicks)
            {
                if (_clusterInfoBytes is { } bytes)
                {
                    SendPeer(kv.Key, PackBroadcast(bytes));
                    _lastClusterInfoTick[kv.Key] = tickIndex;
                }
            }

            // P58: same-side TEAMMATE status broadcast. The reference's
            // per-player status send loops EVERY ship in the mission with NO
            // self-filter (CFSMission, fsmission.cpp:4303-4323), so its client
            // builds a full picture of every ship it knows about; this server
            // previously only ever sent each peer ITS OWN ship's status, so a
            // commander client could never learn a docked miner teammate even
            // existed — the exact gap CommanderResearchAndExpand.
            // UndockMinersAndStartMining hits today. Scope: same-side only (a
            // deliberate cut vs the reference's unrestricted visibility),
            // throttled per (recipient,subject) pair. The peer's own ship is
            // skipped (handled above). Reuses MakeOwnShipStatus unchanged —
            // it already accepts an arbitrary (shipId, live) pair, not just the
            // receiver's own.
            if (live.Ship.SideId is { } recipientSide && sameSide.TryGetValue(recipientSide, out var mates))
            {
                // P604b — per-recipient phase gate for the periodic same-side
                // TEAMMATE SHIP_STATUS(90) refresh. Before this, the per-pair
                // check (`tickIndex - last >= TeammateStatusRefreshTicks`) fired
                // for EVERY (recipient, subject) pair on the same aligned tick —
                // every peer's `last` is seeded to ~the same join-burst tick, so
                // every 100th tick sent O(P²) SHIP_STATUS packets at once. That
                // synchronized O(P²) burst was the ~5 s hitch (P604b; measured
                // as a ~36 ms bss.mates max on this workstation, ~1.2 s on the
                // weaker/shared 221 box). Staggering the refresh PER RECIPIENT —
                // each recipient refreshes ALL of its mates on its own
                // phase-offset tick — keeps every pair's ~5 s cadence while
                // bounding the per-tick burst to ~P²/interval sends. Phase =
                // the recipient's ConnectionId modulo the interval, so the
                // ~P/interval recipients due per tick are spread (never the
                // whole fleet at once).
                if (!_nextTeammateRefreshTick.TryGetValue(kv.Key, out var rt))
                {
                    rt = (long)tickIndex + (int)(kv.Key.Value % TeammateStatusRefreshTicks);
                    _nextTeammateRefreshTick[kv.Key] = rt;
                }
                bool recipientRefreshDue = (long)tickIndex >= rt;
                if (recipientRefreshDue)
                    _nextTeammateRefreshTick[kv.Key] = (long)tickIndex + TeammateStatusRefreshTicks;

                // P317 — batch every dirty teammate SingleShipUpdate for this
                // recipient into ONE FedPacket at the end of the walk (instead
                // of one packet per (recipient, subject) pair per tick, which
                // at 150 same-side ships is ~22k packets/tick). Lazily created
                // so a recipient with no dirty mates allocates nothing.
                List<BroadcastMessage>? posBatch = null;
                using (TickProfiler.Enter("bss.mates"))
                foreach (var tv in mates)
                {
                    if (ReferenceEquals(tv.Value.Ship, ship)) continue;   // own ship, handled above
                    short teammateId = tv.Value.Ship.Id;

                    // P62: one-shot real ExportShip(26) about this teammate's
                    // ship, sent to the recipient before any SHIP_STATUS about
                    // the same subject. This is what makes the client's object
                    // model learn a teammate ship exists at all: FM_S_EXPORT is
                    // the object-CREATION message (appmsg.cpp:80-105 →
                    // CreateObject(OT_ship) news a CshipIGC), whereas GetShip()
                    // (SHIP_STATUS's lookup) can only update an object the
                    // client already has. Until this export, every already-
                    // connected peer is only ever told about its OWN ship
                    // (the server's single join-burst export, Server.cs:~812),
                    // so a teammate's P58 SHIP_STATUS had nothing to attach to.
                    //
                    // ShipId here is the REAL teammate id (NOT the -1/NA that
                    // the own-ship export uses as its "this is my own dummy"
                    // trick) so a genuinely new, separate client-side object
                    // is created. baseObjectID=-1 stays unchanged (docked
                    // carriage — which FM_S_DOCKED already handles — is
                    // explicitly out of scope here). Parts carries whatever
                    // the teammate has mounted AT THIS MOMENT (P61/ApplyLoadout's
                    // authoritative Ship.Parts) — see PartData's own doc comment
                    // for why the real client's ReInitialize (shipIGC.cpp:150-160)
                    // can realize a teammate's already-mounted loadout from this
                    // same EXPORT, closing the gap `BuyLoadoutAck` (owner-only,
                    // Server.cs ApplyLoadout) leaves for everyone else.
                    //
                    // P62 used to be followed here by an inline P63 PLAYERINFO
                    // about the same teammate, and claimed the EXPORT-before-
                    // PLAYERINFO order mattered (a second CreateObject on the
                    // same id taking the else-branch, appmsg.cpp:2276-2279).
                    // That ordering was an AllegianceInterop concern; the port
                    // client upserts either way (ClientWorld.Apply(PlayerInfo)
                    // Upserts), and NATIVE never EXPORTs OT_ship to other
                    // clients at all (grep ExportObj( FedSrv: only static
                    // types/sides/stations) — native creates the ship FROM
                    // PLAYERINFO (appmsg.cpp:2264). P775 removed the P63 send:
                    // PLAYERINFO goes only at the three native moments (mission
                    // join joiner→mission / mission→joiner, drone creation),
                    // never on first sight.
                    if (!_teammateExportSent.Contains((kv.Key, teammateId)) &&
                        _peerNames.TryGetValue(tv.Key, out var peerName))
                    {
                        var initialParts = EnsureLoadoutSnapshot(tv.Value.Ship);
                        var teamExport = new ExportShip
                        {
                            ObjectType = ExportShip.OtShip,
                            CharacterName = peerName,
                            HullId = tv.Value.Ship.HullType.HullId,
                            ShipId = teammateId,                    // real id — creates a new client-side object
                            SideId = recipientSide,                 // the teammate's side (== recipient's, same-side only)
                            PilotType = ExportShip.PilotTypePlayer,
                            Parts = initialParts,
                        };
                        SendPeer(kv.Key, PackBroadcast(Message(teamExport)));
                        if (ConsoleLog.Verbose) ConsoleLog.Write($"[teamexport] peer {kv.Key} <- ship {teammateId} '{peerName}' hull {teamExport.HullId} nParts {initialParts.Count}");   // P619c: per-pair one-shot, gated (join-burst volume)
                        _teammateExportSent.Add((kv.Key, teammateId));
                        _teammateLoadoutVersionSent[(kv.Key, teammateId)] = tv.Value.Ship.LoadoutVersion;
                    }

                    // P58 teammate STATUS broadcast — gated so it only proceeds
                    // once THIS pair's EXPORT has been sent (this tick or an
                    // earlier one): a status for an object the client doesn't
                    // have yet is a no-op at best, so a brand-new pair's status
                    // must never race ahead of its own export within a pass.
                    // If the name lookup above failed (so no export was/will be
                    // sent), the status is skipped too, keeping the two in step.
                    if (_teammateExportSent.Contains((kv.Key, teammateId)))
                    {
                        // P604b — per-recipient gate: this pair refreshes when
                        // the RECIPIENT's phase is due (recipientRefreshDue),
                        // or on the pair's very first send (lastTeammateTick<0,
                        // a just-exported new mate, caught up immediately).
                        long lastTeammateTick = _lastTeammateStatusTick.TryGetValue((kv.Key, teammateId), out var tt0) ? tt0 : -1;
                        if (lastTeammateTick < 0 || recipientRefreshDue || _shipDetectedChanged.Contains(teammateId))
                        {
                            // HUD-17 — a Detected-bit change re-sends the
                            // teammate status on the spot (native's per-frame
                            // delta, FedSrv/FedSrv.CPP:3693-3700), not after up
                            // to TeammateStatusRefreshTicks.
                            SendPeer(kv.Key, PackStatus(teammateId, tv.Value));
                            if (ConsoleLog.Verbose) ConsoleLog.Write($"[teamshipstatus] peer {kv.Key} <- ship {teammateId} sector {tv.Value.Sector.Id}" +
                                (tv.Value.Ship.StationId is not null ? $" DOCKED@{tv.Value.Ship.StationId}" : " flying"));
                            _lastTeammateStatusTick[(kv.Key, teammateId)] = tickIndex;
                        }

                        // P85: teammate POSITION broadcast — the actual visibility
                        // gap this whole block left open. EXPORT/PLAYERINFO/STATUS
                        // above let a teammate's ship exist and report its coarse
                        // structural state (sector/dock), but SINGLE_SHIP_UPDATE (the
                        // message that actually carries position/velocity/orientation,
                        // same as the own-ship send at the top of this method) was
                        // never sent for anyone but a peer's own ship — confirmed live
                        // (AllegianceClientCore P3): a teammate's ship was visible and
                        // correctly statused, but never observed to move. Reuse the
                        // identical dirty gate the own-ship send above uses (position
                        // OR orientation — see that block's remarks on why a pure
                        // rotation needs the orientation check too) — `tv.Value`'s own
                        // LastBroadcast* fields, read from this tick's frozen `peers`
                        // snapshot, which is safe regardless of iteration order since
                        // nothing this tick has mutated `peers` itself — so a
                        // teammate's position is broadcast to a given recipient
                        // exactly as often as it's broadcast to its own peer, no more.
                        // P332 — guarantee each (recipient, subject) pair's FIRST
                        // position send even for a stationary ship, independent of
                        // the shared motion terms below. The teammate path reads the
                        // subject's OWN LastBroadcast* (maintained by the own-ship
                        // send at the top of this method), so a ship placed once and
                        // never moved after spawn would otherwise sit permanently
                        // clean and a viewer joining AFTER that window would never
                        // see it. Keeping the once-guarantee PER (recipient, subject)
                        // pair — not per ship — is exactly what lets a later-joining
                        // viewer still get the position; the per-tick motion terms
                        // below still govern every repeat send unchanged.
                        // P604 — native ship-update model: the per-tick teammate
                        // posBatch SingleShipUpdate(78) stream is NOT sent. Remote
                        // ship positions travel ONLY through the native
                        // deviation-ranked HEAVY/LIGHT_SHIPS_UPDATE budget
                        // (BroadcastShipsUpdate) plus the P608 own-ship stream —
                        // the client/ bot ClientWorld already consumes 176 as a
                        // first-class pose source (see ClientWorld.Apply(HeavyShipsUpdate)),
                        // dead-reckoning smoothly between the sparser updates. In
                        // "port" mode (the default) this block is unchanged: every
                        // dirty same-side teammate's position is batched and sent
                        // per tick.
                        if (!_nativeShipUpdates && !_broadcastInterestTiersEnabled)
                        {
                            bool firstPos = !_teammatePositionSent.Contains((kv.Key, teammateId));
                            if (firstPos
                                || (tv.Value.Ship.Position - tv.Value.LastBroadcastPos).Length() >= DirtyEpsilon
                                || (tv.Value.Ship.Orientation.GetForward() - tv.Value.LastBroadcastForward).Length() >= DirtyEpsilon
                                || (tv.Value.Ship.Orientation.GetUp() - tv.Value.LastBroadcastUp).Length() >= DirtyEpsilon)
                            {
                                // P317 — encode this subject's position ONCE per tick
                                // and share the byte[] across every same-side recipient
                                // (the bytes are identical for each), then defer the
                                // reference into this recipient's batched packet.
                                if (positionEncode is null || !positionEncode.TryGetValue(teammateId, out var enc))
                                {
                                    var teamMove = new SingleShipUpdate
                                    {
                                        OtTarget = 0,                          // OT_ship
                                        OidTarget = teammateId,
                                        Ship = CaptureShipUpdate(teammateId, tv.Value.Ship, tickIndex),
                                        BIsRipcording = tv.Value.Ship.IsRipcordActive,   // P788 — native bIsRipcording = fRipcordActive() (FedSrv/fsship.cpp:542,807)
                                    };
                                    enc = Message(teamMove);
                                    (positionEncode ??= new())[teammateId] = enc;
                                }
                                (posBatch ??= new()).Add(enc);
                                // P332 — record that this (recipient, subject) pair has
                                // now had its position broadcast, so only the very first
                                // send was forced; every later one is governed solely by
                                // the motion terms above. Idempotent for moving ships
                                // (a Set) — repeats keep flowing on the motion terms.
                                _teammatePositionSent.Add((kv.Key, teammateId));
                            }
                        }

                        // Teammate LOADOUT re-export: the initial EXPORT above only
                        // captures whatever was mounted at export time; a teammate
                        // who buys a new loadout afterward (ApplyLoadout mutating
                        // this same Ship.Parts) would otherwise never be seen again
                        // by an already-exported recipient — BuyLoadoutAck is
                        // owner-only (SendPeer(from) in ApplyLoadout), so re-sending
                        // this same EXPORT is the only wire path a non-owning peer
                        // has to learn the change (see ExportShip's own doc comment:
                        // CshipIGC::ReInitialize handles a re-send for an id the
                        // client already owns exactly like a fresh creation). Gated
                        // on real content change, not a tick cadence — a loadout
                        // changes rarely, so there is no periodic-refresh case to
                        // cover here the way SHIP_STATUS/position need one.
                        long currentVersion = tv.Value.Ship.LoadoutVersion;
                        long sentVersion = _teammateLoadoutVersionSent.TryGetValue((kv.Key, teammateId), out var sv) ? sv : -1;
                        if (currentVersion != sentVersion &&
                            _peerNames.TryGetValue(tv.Key, out var currentPeerName))
                        {
                            var currentParts = EnsureLoadoutSnapshot(tv.Value.Ship);
                            var loadoutExport = new ExportShip
                            {
                                ObjectType = ExportShip.OtShip,
                                CharacterName = currentPeerName,
                                HullId = tv.Value.Ship.HullType.HullId,
                                ShipId = teammateId,
                                SideId = recipientSide,
                                PilotType = ExportShip.PilotTypePlayer,
                                Parts = currentParts,
                            };
                            SendPeer(kv.Key, PackBroadcast(Message(loadoutExport)));
                            if (ConsoleLog.Verbose) ConsoleLog.Write($"[teamloadout] peer {kv.Key} <- ship {teammateId} loadout changed, nParts {currentParts.Count}");   // P619c: one aggregate line per change, gated
                            _teammateLoadoutVersionSent[(kv.Key, teammateId)] = currentVersion;
                        }
                    }
                }
                if (posBatch is { Count: > 0 })
                {
                    if (posBatch.Count <= FedPacket.MaxMessagesPerPacket)
                        SendPeerCoalescing(kv.Key, PackBroadcast(CollectionsMarshal.AsSpan(posBatch)), CoalesceShipStates);
                    else // defensive: a >256-message packet would be rejected by this port's Unpack cap
                        foreach (var chunk in posBatch.Chunk(FedPacket.MaxMessagesPerPacket))
                            SendPeerCoalescing(kv.Key, PackBroadcast(chunk), CoalesceShipStates);
                }

                // P366 — same-side NPC (AI bot teammate) identity + status, so a
                // team leader's client can see, select and command its own side's
                // drones to dock/launch. The P62 teammate path covers only real
                // peers and the P346 cross-side export excludes the ship's own
                // side, so same-side NPCs are invisible to peers today — this is
                // the missing same-side leg. One-shot EXPORT (identity: SideId/
                // HullId/name) mirrors P62; periodic SHIP_STATUS mirrors P58 and
                // gives the client the NPC's real docked state for the Dock/Launch
                // label. Position still arrives via the usual HEAVY_SHIPS_UPDATE
                // broadcast, unchanged.
                if (ship.Mission is { } npcMission && recipientSide is { } npcSide &&
                    sameSideNpcBySide is not null && sameSideNpcBySide.TryGetValue((npcMission, npcSide), out var npcs))
                {
                    foreach (var npc in npcs)
                    {
                        if (_sameSideNpcExportSent.Add((kv.Key, npc.Id)))
                        {
                            var npcExport = new ExportShip
                            {
                                ObjectType = ExportShip.OtShip,
                                CharacterName = PeerName(npc) ?? $"Ship {npc.Id}",
                                HullId = npc.HullType.HullId,
                                ShipId = npc.Id,
                                SideId = npcSide,                       // the NPC's own side (== recipient's, same-side)
                                PilotType = ExportShip.PilotTypePlayer,
                                Parts = EnsureLoadoutSnapshot(npc),
                            };
                            SendPeer(kv.Key, PackBroadcast(Message(npcExport)));
                            if (ConsoleLog.Verbose) ConsoleLog.Write($"[samestidenpc] peer {kv.Key} <- NPC ship {npc.Id} side {npcSide}");   // P619c: per-pair one-shot, gated
                        }

                        long lastNpcTick = _lastSameSideNpcStatusTick.TryGetValue((kv.Key, npc.Id), out var nt0) ? nt0 : -1;
                        // P604b — gate on the recipient's phase too: the NPC
                        // status refresh is the same aligned-100 anti-pattern as
                        // the teammate refresh, so it rides the same per-recipient
                        // phase to never burst fleet-wide (first send still
                        // immediate via lastNpcTick<0).
                        if (lastNpcTick < 0 || recipientRefreshDue || _shipDetectedChanged.Contains(npc.Id))
                        {
                            SendPeer(kv.Key, PackBroadcast(Message(MakeNpcShipStatus(npc))));
                            ConsoleLog.Write($"[samestidenpcstatus] peer {kv.Key} <- NPC ship {npc.Id} sector {npc.Sector?.Id}" +
                                (npc.StationId is not null ? $" DOCKED@{npc.StationId}" : " flying"));
                            _lastSameSideNpcStatusTick[(kv.Key, npc.Id)] = tickIndex;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// P603 — parallel twin of <see cref="BroadcastShipStatesSerial"/>, enabled
    /// by <c>broadcast.parallelBuild</c>. Produces <b>byte-identical per-peer
    /// payloads</b> to the serial builder while spreading the per-peer payload
    /// construction across cores — the #1 lever P600 identified
    /// (<c>broadcastShipStates</c> is ~58% of the tick at 241 players, ~O(N²)).
    ///
    /// <para><b>Phases (all within one tick, sim thread waits):</b></para>
    /// <list type="bullet">
    /// <item><b>(a) Serial snapshot phase (sim thread):</b> freeze the per-tick
    /// inputs the builders read into read-only shared structures: the <c>peers</c>
    /// array, the per-side <c>sameSide</c> grouping, the per-(mission,side) NPC
    /// grouping <c>sameSideNpcBySide</c>, a per-ship prebuilt <c>positionEncode</c>
    /// (the byte-identical SingleShipUpdate every same-side recipient reuses),
    /// and the loadout-snapshot cache prewarmed for every ship the build can
    /// touch. The per-peer latch dictionaries are NOT copied — they are read
    /// concurrently (multiple readers of a Dictionary are safe; no worker writes
    /// them), and the writes each worker would have made are recorded as
    /// per-peer <b>effects</b> applied serially in phase (c).</item>
    /// <item><b>(b) Parallel build phase:</b> <c>Parallel.For</c> over peers (bounded
    /// degree <see cref="Server._broadcastMaxParallelism"/>); each worker builds its
    /// peer's ordered packet list from the snapshot + read-only latches only. No
    /// worker touches mutable world state, writes a shared collection, or sends —
    /// it only appends to its own per-peer result (packets + effects + buffered
    /// log lines). Within-peer Add-then-Contains latch decisions (e.g.
    /// teamexport before playerinfo/status) use a per-worker local overlay so the
    /// peer's own writes are visible to itself exactly as in serial.</item>
    /// <item><b>(c) Serial apply phase (sim thread):</b> walk peers in
    /// <c>_peerShips</c> order, enqueue each peer's packets via
    /// <see cref="SendPeer"/> in the exact serial order (so per-peer wire order
    /// — and the global capture stream — are unchanged), apply the peer's latch /
    /// <c>_peerShips</c> effects, and emit its buffered logs.</item>
    /// </list>
    ///
    /// <para><b>Byte-identity argument.</b> Every decision in the serial builder
    /// reads (i) the frozen <c>peers</c> snapshot, (ii) per-peer / per-(peer,mate) /
    /// per-(peer,npc) latch keys that are <b>disjoint across recipients</b>, and
    /// (iii) read-only world state (quiescent during the broadcast). Because the
    /// latch keys are per-recipient, no peer's writes influence another peer's
    /// decisions within the tick — so building peers in parallel and applying each
    /// peer's effects serially afterwards yields the exact same decisions, packet
    /// counts, packet order and final latch state as serial. The byte-identity
    /// test harness (see docs/reports/P603…) exercises this over seeded worlds.
    /// The shared per-ship caches (<c>positionEncode</c>, loadout snapshot) are
    /// pure functions of the frozen world, so precomputing them yields identical
    /// bytes.</para>
    /// </summary>
    private void BroadcastShipStatesParallel(uint tickIndex)
    {
        // ── (a) SERIAL SNAPSHOT PHASE ────────────────────────────────────────
        // P619 — the snapshot/build/apply phases become visible profiler buckets
        // in parallel mode (the serial twin exposes bss.setup/bss.mates; the
        // parallel twin previously exposed NONE of its internals). The phase
        // locals escape the snapshot scope so the build/apply phases consume them.
        KeyValuePair<ConnectionId, LiveShip>[] peers;
        Dictionary<short, List<KeyValuePair<ConnectionId, LiveShip>>> sameSide;
        Dictionary<(Mission, short), List<Ship>>? sameSideNpcBySide;
        Dictionary<short, BroadcastMessage> positionEncode;
        using (TickProfiler.Enter("bss.snapshot"))
        {
        peers = SnapshotBroadcastPeers();
        if (peers.Length == 0) return;

        // Per-side same-side grouping, in `peers` order (mirror of serial :502-510).
        sameSide = new Dictionary<short, List<KeyValuePair<ConnectionId, LiveShip>>>();
        foreach (var gkv in peers)
        {
            if (gkv.Value.Ship.SideId is { } s)
            {
                if (!sameSide.TryGetValue(s, out var list)) { list = new(); sameSide[s] = list; }
                list.Add(gkv);
            }
        }

        // P604b — pre-initialise each peer's periodic teammate-status refresh
        // phase in the serial snapshot so the parallel workers only READ
        // `_nextTeammateRefreshTick` (no shared write in the build phase). Phase
        // = the recipient's ConnectionId modulo the interval, so the ~P/interval
        // recipients due per tick are spread — the synchronized O(P²) refresh
        // burst this staggers (see the serial builder's comment) never forms.
        foreach (var gkv in peers)
            if (!_nextTeammateRefreshTick.ContainsKey(gkv.Key))
                _nextTeammateRefreshTick[gkv.Key] = (long)tickIndex + (int)(gkv.Key.Value % TeammateStatusRefreshTicks);

        // Per-(mission, side) same-side NPC grouping, built once (mirror of serial
        // :527-538). Shared read-only across workers.
        sameSideNpcBySide = null;
        foreach (var m in _missions)
        {
            if (!IncludeShipBroadcastMission(m)) continue;
            if (m.Stage != MissionStage.Started && m.Stage != MissionStage.Starting) continue;
            foreach (var sec in m.Sectors)
                foreach (var mdl in sec.Models)
                    if (mdl is Ship npc && npc.IsNpc && npc.SideId is { } npcSide)
                    {
                        sameSideNpcBySide ??= new();
                        if (!sameSideNpcBySide.TryGetValue((m, npcSide), out var list)) { list = new(); sameSideNpcBySide[(m, npcSide)] = list; }
                        list.Add(npc);
                    }
        }

        // Prewarm the loadout-snapshot cache for every ship the build can touch
        // (all peer ships + all same-side NPCs), so the parallel workers only ever
        // READ it (no shared write in the build phase).
        foreach (var kv in peers) EnsureLoadoutSnapshot(kv.Value.Ship);
        if (sameSideNpcBySide is not null)
            foreach (var lst in sameSideNpcBySide.Values)
                foreach (var npc in lst) EnsureLoadoutSnapshot(npc);

        // Prebuild the shared per-ship position encode (mirror of the lazy cache
        // in serial :812-823 — byte-identical for every same-side recipient).
        // P604 — native ship-update model: the teammate posBatch SingleShipUpdate
        // stream is suppressed, so this O(P) per-tick encode cache is pure waste
        // and is skipped (an empty dict is passed; the parallel build never
        // indexes it because its posBatch block is gated on !_nativeShipUpdates).
        positionEncode = new Dictionary<short, BroadcastMessage>(peers.Length);
        if (!_nativeShipUpdates && !_broadcastInterestTiersEnabled)
        {
            foreach (var kv in peers)
            {
                var ship = kv.Value.Ship;
                positionEncode[ship.Id] = Message(new SingleShipUpdate
                {
                    OtTarget = 0,                                // OT_ship
                    OidTarget = ship.Id,
                    Ship = CaptureShipUpdate(ship.Id, ship, tickIndex),
                    BIsRipcording = ship.IsRipcordActive,   // P788 — native bIsRipcording = fRipcordActive() (FedSrv/fsship.cpp:542,807)
                });
            }
        }
        }

        // ── (b) PARALLEL BUILD PHASE ─────────────────────────────────────────
        BroadcastPeerBuild[] results;
        using (TickProfiler.Enter("bss.build"))
        {
            results = new BroadcastPeerBuild[peers.Length];
            var po = new ParallelOptions { MaxDegreeOfParallelism = _broadcastMaxParallelism };
            // P619 — when slow-tick capture is armed, time each worker and feed
            // the max into the capture; otherwise (the common case) `slow` is
            // false and the body is exactly the pre-P619 build with zero extra
            // per-worker cost beyond a null-vs-stopwatch branch.
            bool slow = TickProfiler.SlowTickCaptureActive;
            Parallel.For(0, peers.Length, po, i =>
            {
                var sw = slow ? Stopwatch.StartNew() : null;
                results[i] = BuildPeerShipStates(peers, sameSide, sameSideNpcBySide, positionEncode, tickIndex, i);
                if (slow) TickProfiler.RegisterWorker(sw!.Elapsed.TotalMilliseconds);
            });
        }

        // ── (c) SERIAL APPLY PHASE (enqueue + effects + logs, in peer order) ──
        using (TickProfiler.Enter("bss.apply"))
        for (int i = 0; i < results.Length; i++)
        {
            var conn = peers[i].Key;
            var r = results[i];
            // P606/P608 — per packet: ordinary ordered traffic via SendPeer,
            // latest-wins stream frames via SendPeerCoalescing (guaranteed flag
            // honoured), in the exact build order so wire order matches the
            // serial builder.
            foreach (var (pkt, key, guaranteed) in r.Packets)
            {
                if (key == 0) SendPeer(conn, pkt);
                else SendPeerCoalescing(conn, pkt, key, guaranteed);
            }
            foreach (var line in r.Logs) Console.WriteLine(line);
            foreach (var line in r.VerboseLogs) ConsoleLog.Write(line);   // P619b — verbose-gated, non-blocking
            ApplyBroadcastPeerEffects(conn, r);
        }
    }

    /// <summary>
    /// P603 — per-peer result of the parallel build: the ordered packet list for
    /// this peer, its buffered (unconditional) log lines, and the effect deltas
    /// to apply serially (the latches / <c>_peerShips</c> write the serial builder
    /// performs inline). Each peer is built by exactly one worker, so these are
    /// per-peer-disjoint and safe to apply in <c>_peerShips</c> order afterwards.
    /// </summary>
    private sealed class BroadcastPeerBuild
    {
        // P606/P608 — ordered packets, each tagged with its coalesce key (0 =
        // ordinary ordered traffic; non-zero = latest-wins stream frame) and
        // whether it is guaranteed. Keeps the exact per-peer build order so the
        // parallel path stays byte-identical to serial even while routing the
        // position stream through coalescing; the owner's own-ship frame is
        // marked guaranteed so it is never shed under capacity.
        public List<(BroadcastPacket Packet, int Key, bool Guaranteed)> Packets = new();
        public List<string> Logs = new();             // unconditional log lines, ordered
        // P619b — the high-frequency per-pair diagnostics ([shipstatus],
        // [teamshipstatus], [samestidenpcstatus]) the serial builder routes through
        // the verbose-gated, non-blocking ConsoleLog.Write (P318). Kept apart from
        // Logs so the parallel apply phase never writes them synchronously to stdout
        // (~4,000 lines/s at 390 players stalled bss.apply 3.5 s per window on 221).
        public List<string> VerboseLogs = new();
        public LiveShip? NewLiveShip;                 // own-ship update, or null
        public bool SetStatus; public short StatusSector; public long StatusTick;
        public bool SetClusterInfo; public long ClusterInfoTick;
        public readonly HashSet<short> ExportSent = new();       // -> _teammateExportSent
        public readonly HashSet<short> PositionSent = new();     // -> _teammatePositionSent
        public readonly HashSet<short> NpcExportSent = new();    // -> _sameSideNpcExportSent
        public readonly Dictionary<short, long> TeammateStatusTick = new(); // -> _lastTeammateStatusTick
        public readonly Dictionary<short, long> LoadoutVersionSet = new(); // -> _teammateLoadoutVersionSent
        public readonly Dictionary<short, long> NpcStatusTick = new();     // -> _lastSameSideNpcStatusTick
        // P604b — per-recipient advance of `_nextTeammateRefreshTick` when this
        // peer's periodic teammate-status refresh fired this tick (null = no
        // change). Applied serially with the other per-peer effects.
        public long? NextTeammateRefreshTick;
    }

    /// <summary>
    /// P603 — apply one peer's build effects serially: enqueue-independent latch
    /// writes and the <c>_peerShips</c> update, exactly the final shared state the
    /// serial builder leaves. Keyed by this peer (ConnectionId), so no effect here
    /// collides with any other peer's.
    /// </summary>
    private void ApplyBroadcastPeerEffects(ConnectionId conn, BroadcastPeerBuild r)
    {
        if (r.NewLiveShip is { } nl) _peerShips[conn] = nl;
        if (r.SetStatus) { _lastStatusSector[conn] = r.StatusSector; _lastStatusTick[conn] = r.StatusTick; }
        if (r.SetClusterInfo) _lastClusterInfoTick[conn] = r.ClusterInfoTick;
        foreach (var mate in r.ExportSent) _teammateExportSent.Add((conn, mate));
        foreach (var mate in r.PositionSent) _teammatePositionSent.Add((conn, mate));
        foreach (var npc in r.NpcExportSent) _sameSideNpcExportSent.Add((conn, npc));
        foreach (var (mate, t) in r.TeammateStatusTick) _lastTeammateStatusTick[(conn, mate)] = t;
        foreach (var (mate, v) in r.LoadoutVersionSet) _teammateLoadoutVersionSent[(conn, mate)] = v;
        foreach (var (npc, t) in r.NpcStatusTick) _lastSameSideNpcStatusTick[(conn, npc)] = t;
        if (r.NextTeammateRefreshTick is { } nrt) _nextTeammateRefreshTick[conn] = nrt;
    }

    /// <summary>
    /// P603 — build ONE peer's ship-state broadcast from the frozen snapshot +
    /// read-only shared latches, returning the ordered packets + effects. Runs on
    /// a pool worker (see <see cref="BroadcastShipStatesParallel"/>); it must not
    /// write any shared collection, send, or mutate world state. Local overlay
    /// HashSets make a peer's own Add-then-Contains latch decisions visible to
    /// itself exactly as serial's inline writes are.
    /// </summary>
    private BroadcastPeerBuild BuildPeerShipStates(
        KeyValuePair<ConnectionId, LiveShip>[] peers,
        Dictionary<short, List<KeyValuePair<ConnectionId, LiveShip>>> sameSide,
        Dictionary<(Mission, short), List<Ship>>? sameSideNpcBySide,
        Dictionary<short, BroadcastMessage> positionEncode,
        uint tickIndex, int idx)
    {
        var kv = peers[idx];
        var conn = kv.Key;
        var live = kv.Value;
        var ship = live.Ship;
        var r = new BroadcastPeerBuild();

        // Local overlays: a peer's own adds, consulted alongside the shared sets
        // so this peer sees its writes the way serial's inline latches do.
        var localExportSent = new HashSet<short>();
        var localPositionSent = new HashSet<short>();

        var fwd = ship.Orientation.GetForward();
        var up = ship.Orientation.GetUp();
        bool miningNow = ship.MiningAsteroid is not null;
        if (!live.PositionEverBroadcast
            || (ship.Position - live.LastBroadcastPos).Length() >= DirtyEpsilon
            || (fwd - live.LastBroadcastForward).Length() >= DirtyEpsilon
            || (up - live.LastBroadcastUp).Length() >= DirtyEpsilon
            || miningNow != live.LastBroadcastMining
            || MathF.Abs(ship.ShieldFraction - live.LastBroadcastShieldFraction) >= ShieldFractionEpsilon)
        {
            var msg = new SingleShipUpdate
            {
                OtTarget = 0,
                OidTarget = ship.Id,
                Ship = CaptureShipUpdate(ship.Id, ship, tickIndex, miningNow ? ShipControlState.MiningMask : 0, carryEndurance: true),
                BIsRipcording = ship.IsRipcordActive,   // P788 — native bIsRipcording = fRipcordActive() (FedSrv/fsship.cpp:542,807)
            };
            r.Packets.Add((PackBroadcast(Message(msg)), CoalesceOwnShip, true));
            r.NewLiveShip = live with
            {
                LastBroadcastPos = ship.Position,
                LastBroadcastForward = fwd,
                LastBroadcastUp = up,
                LastBroadcastShieldFraction = ship.ShieldFraction,
                LastBroadcastMining = miningNow,
                PositionEverBroadcast = true,
            };
        }

        short sentSector = _lastStatusSector.TryGetValue(conn, out var s0) ? s0 : (short)-2;
        long lastTick = _lastStatusTick.TryGetValue(conn, out var t0) ? t0 : -1;
        bool sectorChanged = live.Sector.Id != sentSector;
        bool refreshDue = lastTick < 0 || (long)tickIndex - lastTick >= StatusRefreshTicks;
        if (sectorChanged || refreshDue || _shipDetectedChanged.Contains(ship.Id))
        {
            r.Packets.Add((PackStatus(ship.Id, live), 0, false));
            if (ConsoleLog.Verbose) r.VerboseLogs.Add($"[shipstatus] peer {conn} " +
                (sectorChanged ? $"sector change {sentSector} -> {live.Sector.Id}" : $"refresh (sector {live.Sector.Id})"));
            r.SetStatus = true; r.StatusSector = live.Sector.Id; r.StatusTick = tickIndex;
        }

        long lastClusterInfoTick = _lastClusterInfoTick.TryGetValue(conn, out var ci0) ? ci0 : -1;
        if (lastClusterInfoTick < 0 || (long)tickIndex - lastClusterInfoTick >= ClusterInfoRefreshTicks)
        {
            if (_clusterInfoBytes is { } bytes)
            {
                r.Packets.Add((PackBroadcast(bytes), 0, false));
                r.SetClusterInfo = true; r.ClusterInfoTick = tickIndex;
            }
        }

        if (live.Ship.SideId is { } recipientSide && sameSide.TryGetValue(recipientSide, out var mates))
        {
            // P604b — per-recipient phase gate (mirrors the serial builder's).
            // The snapshot phase pre-initialises `_nextTeammateRefreshTick` for
            // every peer, so this worker only READS it; the advance is recorded
            // as a per-peer effect (NextTeammateRefreshTick) applied serially.
            long rt = _nextTeammateRefreshTick.TryGetValue(conn, out var rv)
                ? rv : (long)tickIndex + (int)(conn.Value % TeammateStatusRefreshTicks);
            bool recipientRefreshDue = (long)tickIndex >= rt;
            if (recipientRefreshDue) r.NextTeammateRefreshTick = (long)tickIndex + TeammateStatusRefreshTicks;

            List<BroadcastMessage>? posBatch = null;
            foreach (var tv in mates)
            {
                if (ReferenceEquals(tv.Value.Ship, ship)) continue;   // own ship, handled above
                short teammateId = tv.Value.Ship.Id;

                // P62 teamexport (one-shot per (recipient, mate))
                if (!_teammateExportSent.Contains((conn, teammateId))
                    && !localExportSent.Contains(teammateId)
                    && _peerNames.TryGetValue(tv.Key, out var peerName))
                {
                    var initialParts = ReadLoadoutSnapshotOrCompute(tv.Value.Ship);
                    var teamExport = new ExportShip
                    {
                        ObjectType = ExportShip.OtShip,
                        CharacterName = peerName,
                        HullId = tv.Value.Ship.HullType.HullId,
                        ShipId = teammateId,
                        SideId = recipientSide,
                        PilotType = ExportShip.PilotTypePlayer,
                        Parts = initialParts,
                    };
                    r.Packets.Add((PackBroadcast(Message(teamExport)), 0, false));
                    if (ConsoleLog.Verbose) r.VerboseLogs.Add($"[teamexport] peer {conn} <- ship {teammateId} '{peerName}' hull {teamExport.HullId} nParts {initialParts.Count}");   // P619c: gated
                    localExportSent.Add(teammateId);
                    r.ExportSent.Add(teammateId);
                    r.LoadoutVersionSet[teammateId] = tv.Value.Ship.LoadoutVersion;
                }

                if (_teammateExportSent.Contains((conn, teammateId)) || localExportSent.Contains(teammateId))
                {
                    long lastTeammateTick = _lastTeammateStatusTick.TryGetValue((conn, teammateId), out var tt0) ? tt0 : -1;
                    if (lastTeammateTick < 0 || recipientRefreshDue || _shipDetectedChanged.Contains(teammateId))
                    {
                        r.Packets.Add((PackStatus(teammateId, tv.Value), 0, false));
                        if (ConsoleLog.Verbose) r.VerboseLogs.Add($"[teamshipstatus] peer {conn} <- ship {teammateId} sector {tv.Value.Sector.Id}" +
                            (tv.Value.Ship.StationId is not null ? $" DOCKED@{tv.Value.Ship.StationId}" : " flying"));
                        r.TeammateStatusTick[teammateId] = tickIndex;
                    }

                    // P604 — native ship-update model: no per-tick teammate
                    // posBatch SingleShipUpdate(78) stream; remote positions flow
                    // only through the native HEAVY/LIGHT budget. "port" mode
                    // keeps this block unchanged.
                    if (!_nativeShipUpdates && !_broadcastInterestTiersEnabled)
                    {
                        bool firstPos = !_teammatePositionSent.Contains((conn, teammateId)) && !localPositionSent.Contains(teammateId);
                        if (firstPos
                            || (tv.Value.Ship.Position - tv.Value.LastBroadcastPos).Length() >= DirtyEpsilon
                            || (tv.Value.Ship.Orientation.GetForward() - tv.Value.LastBroadcastForward).Length() >= DirtyEpsilon
                            || (tv.Value.Ship.Orientation.GetUp() - tv.Value.LastBroadcastUp).Length() >= DirtyEpsilon)
                        {
                            // Prebuilt shared position encode (byte-identical per subject).
                            (posBatch ??= new()).Add(positionEncode[teammateId]);
                            localPositionSent.Add(teammateId);
                            r.PositionSent.Add(teammateId);
                        }
                    }

                    long currentVersion = tv.Value.Ship.LoadoutVersion;
                    // P603 — the teamexport above records this mate's loadout
                    // version as a PENDING effect (r.LoadoutVersionSet), not yet in
                    // the shared dict. Mirror serial's immediate-write semantics by
                    // consulting the pending effect too, so a just-exported mate is
                    // NOT re-exported as a "loadout changed" (currentVersion != -1).
                    long sentVersion = _teammateLoadoutVersionSent.TryGetValue((conn, teammateId), out var sv)
                        ? sv
                        : (r.LoadoutVersionSet.TryGetValue(teammateId, out var pv) ? pv : -1);
                    if (currentVersion != sentVersion &&
                        _peerNames.TryGetValue(tv.Key, out var currentPeerName))
                    {
                        var currentParts = ReadLoadoutSnapshotOrCompute(tv.Value.Ship);
                        var loadoutExport = new ExportShip
                        {
                            ObjectType = ExportShip.OtShip,
                            CharacterName = currentPeerName,
                            HullId = tv.Value.Ship.HullType.HullId,
                            ShipId = teammateId,
                            SideId = recipientSide,
                            PilotType = ExportShip.PilotTypePlayer,
                            Parts = currentParts,
                        };
                        r.Packets.Add((PackBroadcast(Message(loadoutExport)), 0, false));
                        if (ConsoleLog.Verbose) r.VerboseLogs.Add($"[teamloadout] peer {conn} <- ship {teammateId} loadout changed, nParts {currentParts.Count}");   // P619c: gated, aggregate per change
                        r.LoadoutVersionSet[teammateId] = currentVersion;
                    }
                }
            }
            if (posBatch is { Count: > 0 })
            {
                if (posBatch.Count <= FedPacket.MaxMessagesPerPacket)
                    r.Packets.Add((PackBroadcast(CollectionsMarshal.AsSpan(posBatch)), CoalesceShipStates, false));
                else
                    foreach (var chunk in posBatch.Chunk(FedPacket.MaxMessagesPerPacket))
                        r.Packets.Add((PackBroadcast(chunk), CoalesceShipStates, false));
            }

            // P366 same-side NPC identity + status
            if (ship.Mission is { } npcMission && recipientSide is { } npcSide &&
                sameSideNpcBySide is not null && sameSideNpcBySide.TryGetValue((npcMission, npcSide), out var npcs))
            {
                // Local overlay mirroring serial's `_sameSideNpcExportSent.Add()`
                // (which dedups within a peer's own walk via its bool return): in
                // parallel the shared set can't be written mid-walk, so this peer's
                // own adds must be visible to itself, exactly as in serial.
                var localNpcExportSent = new HashSet<short>();
                foreach (var npc in npcs)
                {
                    if (!_sameSideNpcExportSent.Contains((conn, npc.Id)) && !localNpcExportSent.Contains(npc.Id))
                    {
                        var npcExport = new ExportShip
                        {
                            ObjectType = ExportShip.OtShip,
                            CharacterName = PeerName(npc) ?? $"Ship {npc.Id}",
                            HullId = npc.HullType.HullId,
                            ShipId = npc.Id,
                            SideId = npcSide,
                            PilotType = ExportShip.PilotTypePlayer,
                            Parts = ReadLoadoutSnapshotOrCompute(npc),
                        };
                        r.Packets.Add((PackBroadcast(Message(npcExport)), 0, false));
                        if (ConsoleLog.Verbose) r.VerboseLogs.Add($"[samestidenpc] peer {conn} <- NPC ship {npc.Id} side {npcSide}");   // P619c: gated
                        localNpcExportSent.Add(npc.Id);
                        r.NpcExportSent.Add(npc.Id);
                    }

                    long lastNpcTick = _lastSameSideNpcStatusTick.TryGetValue((conn, npc.Id), out var nt0) ? nt0 : -1;
                    // P604b — gate on the recipient's phase (mirror of serial).
                    if (lastNpcTick < 0 || recipientRefreshDue || _shipDetectedChanged.Contains(npc.Id))
                    {
                        r.Packets.Add((PackBroadcast(Message(MakeNpcShipStatus(npc))), 0, false));
                        if (ConsoleLog.Verbose) r.VerboseLogs.Add($"[samestidenpcstatus] peer {conn} <- NPC ship {npc.Id} sector {npc.Sector?.Id}" +
                            (npc.StationId is not null ? $" DOCKED@{npc.StationId}" : " flying"));
                        r.NpcStatusTick[npc.Id] = tickIndex;
                    }
                }
            }
        }
        return r;
    }

    /// <summary>
    /// P603 — read-only loadout snapshot for the parallel build: returns the
    /// prewarmed cached snapshot only, and never writes the shared cache. The
    /// caller prewarms the cache for every ship the build can touch, so a miss is
    /// a defensive fallback that computes locally (safe, just not cached).
    /// </summary>
    private IReadOnlyList<PartData> ReadLoadoutSnapshotOrCompute(Ship ship)
        => _teammateLoadoutCache.TryGetValue(ship.Id, out var c) && c.Version == ship.LoadoutVersion
            ? c.Snapshot
            : SnapshotParts(ship);

    /// <summary>Build a peer's own-ship SHIP_STATUS(90) from its live record:
    /// docked (carrying the real StationId + State=c_ssDocked) when the ship is
    /// docked, else flying free with StationID/ParentID NA. HUD-17: the
    /// detected/unknown bits are the real native values — <c>Unknown=false</c>
    /// (this port never marks a ship's own-side copy unknown) and
    /// <c>Detected = ShipDetection.IsDetectedByEnemy(ship)</c>, the ship's own
    /// side's copy of native <c>ShipStatus::GetDetected()</c> (<c>FedSrv/
    /// fsship.cpp:237-335</c>; the same value for its own ship and for every
    /// teammate that receives it — native's per-side status is per SUBJECT, not
    /// per recipient).
    /// P108: a <b>boarded</b> ship (ParentShip set) overrides with
    /// State=c_ssTurret/c_ssObserver, ParentID = the carrier's id, and
    /// HullID = NA (riding inside another hull — the native
    /// <c>pss->SetHullID(NA)</c>, FedSrv.CPP:2454). Because every status path
    /// (own-ship periodic refresh, sector change, and the P58 teammate walk)
    /// funnels through this one method, a board/leave transition is reflected
    /// on the wire by the same per-tick mechanism those paths already use — no
    /// new success-ack message is needed (the task's confirmed scoping win).</summary>
    private static ShipStatusMessage MakeOwnShipStatus(short shipId, LiveShip live)
    {
        var ship = live.Ship;
        bool docked = ship.StationId is not null;
        bool detected = ShipDetection.IsDetectedByEnemy(ship);
        if (ship.ParentShip is { } parent)
        {
            // Boarded — riding inside the carrier's hull (occupancy only; the
            // turret aim/fire binding is a later P109 task).
            return new()
            {
                ShipId = shipId,
                HullId = -1,                                      // NA — riding another hull (pss->SetHullID(NA), FedSrv.CPP:2454)
                SectorId = live.Sector.Id,
                StationId = docked ? ship.StationId!.Value : (short)-1,
                ParentId = parent.Id,                             // the carrier (pss->SetParentID(carrier), FedSrv.CPP:2453)
                State = ship.TurretId is null
                    ? ShipStatusMessage.StateObserver            // c_ssObserver (igc.h:4393)
                    : ShipStatusMessage.StateTurret,             // c_ssTurret   (igc.h:4395)
                Unknown = false,
                Detected = detected,                              // own side's copy (fsship.cpp:318/321-334)
                StateTime = 0,
            };
        }
        return new()
        {
            ShipId = shipId,
            HullId = ship.HullType.HullId,
            SectorId = live.Sector.Id,
            StationId = docked ? ship.StationId!.Value : (short)-1,  // NA when flying free
            ParentId = -1,                          // NA — no docked/turreted children (ShipStatusDocked sets ParentID=NA, fsship.cpp:376)
            State = docked ? ShipStatusMessage.StateDocked : ShipStatusMessage.StateFlying,  // c_ssDocked/c_ssFlying (igc.h:4390,4394)
            Unknown = false,                 // this port never hides a ship from its own side
            Detected = detected,             // HUD-17: real own-side bit (fsship.cpp:237-335)
            StateTime = 0,                   // sim clock unused (no dTime source here)
        };
    }

    /// <summary>The teammate ExportShip PartData[] tail as it is right now,
    /// mount order — see <see cref="PartData"/>'s own doc comment for why this
    /// drops <c>MountedPart.FractionMounted</c> (a requester-only, BUY_LOADOUT-
    /// specific concern; an already-mounted part observed via EXPORT has none).
    /// Include signed cargo mounts too, so a refreshed EXPORT cannot erase the
    /// cargo learned from LOADOUT_CHANGE or needed by a later SWAP_PART.</summary>
    private static IReadOnlyList<PartData> SnapshotParts(Ship ship) =>
        ship.ExportInventoryForAck().Select(p => new PartData(p.PartId, p.Amount, p.MountId)).ToArray();

    /// <summary>P366 — build a same-side NPC's FM_S_SHIP_STATUS(90) so a team
    /// leader's client learns the drone's real docked state (drives the
    /// Dock/Launch label). Same field conventions as
    /// <see cref="MakeOwnShipStatus"/>'s flying/docked branch, but built
    /// directly from a <see cref="Ship"/> + its own <c>Sector</c> (an NPC has no
    /// <c>LiveShip</c> record). Boarded state is not modelled for NPCs (no turret
    /// binding), matching the port's single-<c>IsNpc</c>-bool simplification.
    /// HUD-17: <c>Detected</c> is the same real own-side bit
    /// (<see cref="ShipDetection.IsDetectedByEnemy"/>).</summary>
    private static ShipStatusMessage MakeNpcShipStatus(Ship ship)
    {
        bool docked = ship.StationId is not null;
        return new()
        {
            ShipId = ship.Id,
            HullId = ship.HullType.HullId,
            SectorId = ship.Sector?.Id ?? 0,
            StationId = docked ? ship.StationId!.Value : (short)-1,   // NA when flying free
            ParentId = -1,
            State = docked ? ShipStatusMessage.StateDocked : ShipStatusMessage.StateFlying,
            Unknown = false,
            Detected = ShipDetection.IsDetectedByEnemy(ship),          // HUD-17: real own-side bit
            StateTime = 0,
        };
    }

    /// <summary>P317 — the subject ship's current loadout snapshot, served from
    /// the per-ship cache and recomputed (via <see cref="SnapshotParts"/>) only
    /// when the cache is stale (the ship's <see cref="Ship.LoadoutVersion"/>
    /// differs from the cached one). Because the version is bumped only when the
    /// loadout actually mutates, this runs at most once per ship per actual
    /// loadout change — never once per (recipient, subject) pair per tick.</summary>
    private IReadOnlyList<PartData> EnsureLoadoutSnapshot(Ship ship)
    {
        long v = ship.LoadoutVersion;
        if (_teammateLoadoutCache.TryGetValue(ship.Id, out var c) && c.Version == v)
            return c.Snapshot;
        var snap = SnapshotParts(ship);
        _teammateLoadoutCache[ship.Id] = (v, snap);
        return snap;
    }

    /// <summary>
    /// P349 — per-tick catch-up sweep for the cross-side ship identity
    /// <c>EXPORT(26)</c>. Replaces P346's edge-triggered transition-queue
    /// (<c>DrainCrossSideExports</c>) with the same "per-(peer, ship) haven't
    /// told YOU yet" latch pattern the same-side [teamexport] uses
    /// (BroadcastShipStates), so a peer that connects AFTER a ship already
    /// became cross-side visible to its side is still caught up on its very
    /// next tick. This is the identity half of cross-side visibility: the
    /// periodic HEAVY/LIGHT_SHIPS_UPDATE (P139) carries a foreign ship's live
    /// telemetry but no HullId/name, so without this EXPORT a client decoding
    /// 176/175 still could not render the model.
    /// <para><b>Mirror of the same-side [teamexport].</b> Builds the exact
    /// <see cref="ExportShip"/>/EXPORT wire shape the same-side path sends for
    /// a newly-known teammate — real HullId, the ship's real name, the
    /// mounted-parts snapshot — never a new leaner message. The two differences
    /// are the audience (the seeing SIDE's connections, not the same-side team)
    /// and the gate (this ship's latched <c>SeenBySide</c> for that side, not the
    /// same-side join/spawn path).</para>
    /// <para><b>Send target — per-side sensor net, per-recipient latch.</b> A
    /// ship seen by a side is told to EVERY peer on that side (the side's
    /// scanner net is shared — a fleet member anywhere inherits the side's
    /// awareness), but each (peer, ship) pair latches independently in
    /// <c>_crossSideExportSent</c>: multiple peers share a side (vizfight's
    /// side 0 = Bios = the human + several Bios bots), and each individual
    /// connection needs its own "have I told THIS peer" mark. Never whole-mission
    /// and never the ship's own side (which already has its own-side export).
    /// <c>SideId</c> on the wire is the <em>exported ship's own</em> side (the
    /// real faction identity the client colours/reasons over), not the seeing
    /// side.</para>
    /// <para><b>Why no transition queue anymore.</b> The one-shot queue fired
    /// on the false→true edge to whichever peers happened to be connected at
    /// that exact tick, then discarded the event forever — so it forgot a peer
    /// who joined after visibility was established (the vizfight `roster`
    /// failure). The per-tick sweep reads the latched <c>SeenBySide</c> state
    /// directly, so it covers the "newly visible" and "newly joined" cases with
    /// one mechanism — keeping both would be redundant, not complementary.</para>
    /// </summary>
    private void SweepCrossSideExports()
    {
        PruneEmissionPoses();
        // Build the per-side audience once per tick: the side's seated connections
        // AND the sectors that side has a seated peer in (a peer only receives
        // ship telemetry for ships in its own cluster — see BroadcastShipsUpdate).
        _crossSideExportGeneration++;
        var peers = SnapshotBroadcastPeers();
        Dictionary<short, (List<ConnectionId> Conns, HashSet<Sector> Sectors)> bySide = new();
        foreach (var kv in peers)
        {
            if (kv.Value.Ship.SideId is not { } s) continue;   // unseated (team-lobby) peers see nothing cross-side
            if (!bySide.TryGetValue(s, out var e)) { e = (new List<ConnectionId>(), new HashSet<Sector>()); bySide[s] = e; }
            e.Conns.Add(kv.Key);
            e.Sectors.Add(kv.Value.Sector);
        }
        if (bySide.Count == 0) return;   // nobody seated on a real side

        // P369 — on the slow identity-refresh cadence, the already-latched
        // (peer, ship) identity is re-sent too (below) so a single missed EXPORT
        // self-heals. P604b — the re-send is phase-staggered per (conn, ship)
        // (below) instead of a global `TickIndex % interval == 0` (which fired
        // for EVERY latched pair at once — an O(peers × ships) EXPORT re-send
        // burst every 30 s, measured as a ~42 ms crossSideExportSweep spike on
        // this workstation and amplified on 221).

        foreach (var m in _missions)
        {
            // The P132 visibility sweep only runs once the mission is
            // Started/Starting (Mission.Update early-returns otherwise), so the
            // flags we read are meaningful only then. Defensive gate, same
            // precondition the cross-side machinery has always assumed.
            if (!IncludeShipBroadcastMission(m)) continue;
            if (m.Stage != MissionStage.Started && m.Stage != MissionStage.Starting) continue;

            foreach (var sector in m.Sectors)
                foreach (var model in sector.Models)
                {
                    if (model is not Ship ship) continue;
                    if (_crossSideExportPackets.TryGetValue(ship.Id, out var cached) && cached.Ship == ship)
                        cached.Generation = _crossSideExportGeneration;
                    short? ownSide = ship.SideId;
                    foreach (var (sideId, e) in bySide)
                    {
                        if (ownSide == sideId) continue;          // own side — the same-side [teamexport] path

                        // P369 — identity gate is ALIGNED WITH the telemetry gate
                        // (BroadcastShipsUpdate.peerSees), NOT the stricter P132
                        // detection radius. The client is a pure renderer with no
                        // local IGC sim: it renders EVERY non-cloaked ship in its
                        // own cluster whose telemetry it receives, so it must have
                        // that ship's identity (hull/side) too — otherwise a ship
                        // it is actively drawing has no model (hull=0, side=0 ->
                        // the wrong/fallback model, forever). The two conditions
                        // below are exactly the two that decide whether the heavy
                        // telemetry reaches this side's peers:
                        //   • a seated peer of this side is in the ship's sector,
                        //   • the ship is not a cloak-hidden enemy (P270 drops
                        //     those from the peer's telemetry too).
                        // This keeps the identity one-shot per (peer, ship) (never
                        // whole-mission, never per-tick) while guaranteeing a
                        // rendered cross-side ship always carries its real model.
                        if (!e.Sectors.Contains(sector)) continue;              // no telemetry here -> no identity needed
                        if (ship.IsCloaked && !ship.SeenBySide(sideId)) continue; // cloak-hidden -> no telemetry (P270) -> none here

                        foreach (var conn in e.Conns)
                        {
                            if (_crossSideExportSent.Add((conn, ship.Id)))
                            {
                                SendCrossSideExport(conn, ship, sideId);
                                // P775 — no PLAYERINFO here: native never sends one
                                // on sighting (SendPlayerInfo's three call sites are
                                // mission join ×2 and drone creation, FedSrv.CPP:2056-2135).
                            }
                            // P369 — self-heal: on the slow refresh cadence,
                            // re-send an identity we already sent once (still
                            // only for ships whose telemetry reaches this side and
                            // peers still seated — never per-tick spam), so a peer
                            // that never landed that one EXPORT re-learns the
                            // ship's real SideId instead of staying at 0.
                            // P604b — phase-staggered per (conn, ship): each
                            // latched pair re-exports once per interval on its own
                            // phase slot, so the self-heal cadence is unchanged but
                            // the fleet-wide O(P²) re-export burst is gone.
                            else if (((long)TickIndex + ((uint)conn.Value + (uint)ship.Id) % CrossSideIdentityRefreshTicks)
                                        % CrossSideIdentityRefreshTicks == 0)
                                SendCrossSideExport(conn, ship, sideId);
                        }
                    }
                }
        }
        foreach (var cached in _crossSideExportPackets)
            if (cached.Value.Generation != _crossSideExportGeneration) _crossSideExportPackets.Remove(cached.Key);
    }

    /// <summary>HUD-20 — build one PLAYERINFO(10) the way native
    /// <c>SendPlayerInfo</c> fills it (<c>FedSrv/FedSrv.CPP:2079-2105</c>):
    /// <c>fExperience</c> is the ship's live experience / kill-streak
    /// (<c>GetIGCShip()-&gt;GetExperience()</c>, answered by the port's
    /// <see cref="Ship.KillBonus"/>), <c>pilotType</c> the ship's real pilot type,
    /// and <c>dtidDrone</c> NA for a human / the drone type id for a drone
    /// (<c>:2088-2091</c>). Money stays 0: the native client asserts
    /// <c>GetMoney()==0</c> in <c>AddPlayerToSide</c> (<c>clintlib.cpp:4109</c>).
    /// <paramref name="kills"/>/<paramref name="deaths"/>/<paramref name="ejections"/>
    /// are the player's mission score where one exists (native
    /// <c>GetKills()/GetDeaths()/GetEjections()</c>, <c>:2085-2087</c>), 0 for a
    /// drone. P774 — the persist list rides along the way native fills it
    /// (FedSrv.CPP:2060-2077): one NA entry carrying the player's rank
    /// (<c>:952</c>), rank 0 for a drone whose persist is reset
    /// (<c>:2073-2077</c>, igc.h:5449-5455). Score/combat rating stay 0 — no
    /// stats DB (documented simplification).</summary>
    private PlayerInfo BuildPlayerInfo(Ship ship, short isSide, string? characterName,
        int kills = 0, int deaths = 0, int ejections = 0, short rank = 0) => new()
    {
        CharacterName = characterName ?? PeerName(ship) ?? $"Ship {ship.Id}",
        Money = 0,
        ISide = isSide,
        ShipId = ship.Id,
        PilotType = ship.PilotType,
        // Native never has a type-less drone: dtidDrone is the drone type's
        // object id (FedSrv.CPP:2088-2091). The port's SpawnNpcShip(droneTypeId:
        // null) fallback carries -1, which on the wire is NA — the HUMAN value —
        // so an NPC must never encode NA; 0 is the stock/unknown drone id and
        // still tells the client (and the rig) this is a drone, not a player.
        DtidDrone = ship.IsNpc ? (ship.DroneTypeId >= 0 ? ship.DroneTypeId : (short)0) : PlayerInfo.NA,
        FExperience = ship.KillBonus,
        NKills = (short)Math.Clamp(kills, short.MinValue, short.MaxValue),
        NDeaths = (short)Math.Clamp(deaths, short.MinValue, short.MaxValue),
        NEjections = (short)Math.Clamp(ejections, short.MinValue, short.MaxValue),
        // P774 — the NA persist entry (native FedSrv.CPP:952 for a player;
        // a drone's persist is reset, :2073-2077 + igc.h:5449-5455). The port
        // sends ONLY the NA entry of native's NA-plus-per-civ list: every
        // native client rank reader uses NA (teampane.cpp:308,1866).
        PersistScores = [new(0f, 0f, ship.IsNpc ? (short)0 : rank, PlayerInfo.NA)],
    };

    /// <summary>P775 — announce ONE subject ship to its whole mission at the
    /// native moment, the exact port counterpart of native's whole-mission
    /// SendPlayerInfo audience <c>GetGroupMission()</c>
    /// (<c>FedSrv.CPP:2129-2131</c>): every connection in the mission, regardless
    /// of sector or visibility. Builds the PLAYERINFO <b>once</b> (kills/deaths/
    /// ejections/rank from the owning <see cref="LiveShip"/>.PlayerScore when the
    /// ship is a peer, otherwise 0 — native GetKills()/GetDeaths()/GetEjections(),
    /// <c>FedSrv.CPP:2085-2087</c>, and the NA persist entry <c>:952</c>), packs it
    /// <b>once</b>, and sends that packet to every <c>_peerShips</c> entry whose
    /// Ship.Mission is the ship's mission — skipping the ship's own connection
    /// (the joiner's own side carrier is the dedicated F send,
    /// <c>Server.Lobby.cs</c>) and any (recipient, ship) pair already in
    /// <c>_playerInfoSent</c>, adding each pair it sends. It never clears
    /// latches itself: a genuinely new ship goes through
    /// <see cref="AnnounceFreshShip"/>, and the <c>CompleteJoinAndLaunch</c>
    /// catch-all must stay a no-op for an already-announced joiner. All sends
    /// stay on the sim thread, so the P774 Fix 6 ordering (PLAYERINFO never
    /// overtakes its KILL_SHIP) holds by construction.</summary>
    private void AnnouncePlayerToMission(Ship ship, short isSide, string? characterName = null)
    {
        if (ship.Mission is null) return;
        var (kills, deaths, ejections, rank) = MissionScoreOf(ship);
        var packet = PackBroadcast(Message(BuildPlayerInfo(ship, isSide, characterName,
            kills, deaths, ejections, rank)));
        foreach (var (conn, live) in _peerShips)
        {
            if (ReferenceEquals(live.Ship, ship)) continue;                 // the subject's own connection
            if (!ReferenceEquals(live.Ship.Mission, ship.Mission)) continue;
            if (!_playerInfoSent.Add((conn, ship.Id))) continue;
            SendPeer(conn, packet);
            if (ConsoleLog.Verbose) ConsoleLog.Write($"[playerinfo-announce] peer {conn} <- ship {ship.Id} side {isSide}");
        }
    }

    /// <summary>P775 — announce a genuinely NEW ship (a freshly allocated id:
    /// <c>SpawnNpcShip</c>/<c>TrainingHost.CreateDrone</c> via
    /// <see cref="AnnounceNewShip"/>, the side-select join and the rotation
    /// reseat). <c>AllocateShipId</c> reuses ids once a ship is gone, and a
    /// removal path may leave (recipient, id) entries behind (e.g. a
    /// constructor that became a station), so the id's stale entries are
    /// dropped first — otherwise the new ship would silently reach nobody who
    /// had latched the old one. O(latch size), once per new ship; never on the
    /// per-tick path.</summary>
    private void AnnounceFreshShip(Ship ship, short isSide, string? characterName = null)
    {
        _playerInfoSent.RemoveWhere(e => e.ShipId == ship.Id);
        AnnouncePlayerToMission(ship, isSide, characterName);
    }

    /// <summary>P775 — the mission→joiner form (native moment (2),
    /// <c>fsmission.cpp:3958-3968</c>): latch-gate ONE (recipient, ship) pair and
    /// append the PLAYERINFO to the caller's message list so the whole join
    /// burst rides ONE packet. Returns false when the pair was already latched
    /// (nothing appended).</summary>
    private bool AppendPlayerInfoForRecipient(ConnectionId recipient, Ship ship, short isSide,
        string? characterName, List<byte[]> sink)
    {
        if (!_playerInfoSent.Add((recipient, ship.Id))) return false;
        var (kills, deaths, ejections, rank) = MissionScoreOf(ship);
        sink.Add(BuildPlayerInfo(ship, isSide, characterName, kills, deaths, ejections, rank).Encode());
        return true;
    }

    /// <summary>P775 — the mission kills/deaths/ejections/rank native puts in a
    /// human ship's PLAYERINFO (<c>FedSrv.CPP:2085-2087,952</c>): the owning
    /// peer's score, 0 for a drone.</summary>
    private (int Kills, int Deaths, int Ejections, short Rank) MissionScoreOf(Ship ship)
    {
        if (ship.IsNpc) return (0, 0, 0, 0);
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, ship))
                return (kv.Value.PlayerScore.Kills, kv.Value.PlayerScore.Deaths,
                    kv.Value.PlayerScore.Ejections, kv.Value.PlayerScore.Rank);
        return (0, 0, 0, 0);
    }

    /// <summary>P349 — send ONE cross-side <c>EXPORT(26)</c> about
    /// <paramref name="ship"/> to a single recipient <paramref name="conn"/> on
    /// the seeing side <paramref name="seeingSide"/>. Caller has already latched
    /// <c>(conn, ship.Id)</c> in <c>_crossSideExportSent</c>.</summary>
    private void SendCrossSideExport(ConnectionId conn, Ship ship, short seeingSide)
    {
        SendPeer(conn, CrossSideExportPacket(ship, seeingSide));
        if (ConsoleLog.Verbose) ConsoleLog.Write($"[xexport] side {seeingSide} <- peer {conn} ship {ship.Id} hull {ship.HullType.HullId}");
    }

    /// <summary>
    /// P453 — per-tick catch-up sweep for the cross-side MINE identity
    /// <c>EXPORT(26)</c>/<c>OT_mine</c>, the mine mirror of
    /// <see cref="SweepCrossSideExports"/> (P349). Mines joined the P132
    /// visibility sweep as side-visible targets (native mines are created with
    /// <c>c_mtSeenBySide | c_mtPredictable | c_mtHitable | c_mtStatic</c> and
    /// take part in <c>UpdateSideVisibility</c> as targets, mineigc.cpp:103-107),
    /// so an enemy side's scanner net now spots a foreign field the moment one
    /// of its scanners comes in range with line of sight. Native then exports
    /// the newly-visible predictable object to everyone on the seeing side
    /// (<c>ThingSiteImpl::ShowObject</c>, FedSrv.CPP:10749-10778). This sweep
    /// is the port of that side: for every mine, for every side other than its
    /// owner whose latched <c>SeenBySide</c> is now true, send each of that
    /// side's seated peers the SAME <c>ExportMine</c>/OT_mine
    /// <see cref="BroadcastExportMine"/> builds — latched once per (peer, mine)
    /// in <c>_crossSideMineExportSent</c> so it fires exactly once and catches
    /// up a peer who joins an already-informed side on its next tick.
    /// <para><b>Audience.</b> Per-side sensor-net, per-recipient latch — exactly
    /// the ship sweep's model (a side's scanner net is shared: one scanner
    /// anywhere makes the whole side aware, and every seated peer of that side
    /// is told). Never the mine's own side (that got the deploy export at
    /// creation via <see cref="BroadcastExportMine"/>), never whole-mission.
    /// The wire <c>sideID</c> is the mine's OWN (owner) side — the faction that
    /// laid the field, which the client reasons over — not the seeing side.
    /// A mine is a static object with no per-cluster telemetry channel, so the
    /// export is gated on <c>SeenBySide</c> alone (no seated-peer-in-sector
    /// requirement, unlike the ship sweep which must align with the telemetry
    /// gate); the client's mine renderer filters by the viewer's own sector.</para>
    /// <para><b>Destroy reach.</b> The same <c>_crossSideMineExportSent</c>
    /// latch is read by <see cref="BroadcastMineDestroyed"/> (Server.Combat.cs)
    /// so every peer that was sent the mine also receives the
    /// <c>MINE_DESTROYED(84)</c> — no enemy client keeps a phantom field.</para>
    /// </summary>
    private void SweepCrossSideMineExports()
    {
        // Per-side seated audience, exactly the ship sweep builds.
        var peers = SnapshotBroadcastPeers();
        Dictionary<short, List<ConnectionId>> bySide = new();
        foreach (var kv in peers)
        {
            if (kv.Value.Ship.SideId is not { } s) continue;   // unseated (team-lobby) peers see nothing cross-side
            if (!bySide.TryGetValue(s, out var list)) { list = new(); bySide[s] = list; }
            list.Add(kv.Key);
        }
        if (bySide.Count == 0) return;   // nobody seated on a real side

        foreach (var m in _missions)
        {
            if (m.Stage != MissionStage.Started && m.Stage != MissionStage.Starting) continue;
            foreach (var sector in m.Sectors)
                foreach (var model in sector.Models)
                {
                    if (model is not Mine mine) continue;
                    if (mine.SideId is not { } ownerSide) continue;   // sideless mine: no owner-side rule, not exported cross-side
                    foreach (var (sideId, conns) in bySide)
                    {
                        if (sideId == ownerSide) continue;            // own side — the deploy-time BroadcastExportMine path
                        if (!mine.SeenBySide(sideId)) continue;       // not spotted by this side's scanner net yet
                        foreach (var conn in conns)
                            if (PeerInMission(conn, mine.OwnerSector) && _crossSideMineExportSent.Add((conn, mine.Id)))
                                SendCrossSideMineExport(conn, mine, sideId);
                    }
                }
        }
    }

    /// <summary>P453 — send ONE cross-side <c>EXPORT(26)</c>/<c>OT_mine</c> about
    /// <paramref name="mine"/> to a single recipient <paramref name="conn"/> on
    /// the seeing side <paramref name="seeingSide"/>. Caller has already latched
    /// <c>(conn, mine.Id)</c> in <c>_crossSideMineExportSent</c>. Builds the
    /// identical <see cref="ExportMine"/> wire shape <see cref="BroadcastExportMine"/>
    /// uses (a fresh export with the current tick's <c>timeCreate</c>), with the
    /// mine's OWN owner side on the wire (faction identity) and the real laying
    /// ship's id as launcher.</summary>
    private void SendCrossSideMineExport(ConnectionId conn, Mine mine, short seeingSide)
    {
        short launcherId = (mine.Launcher as Ship)?.Id ?? -1;   // NA when no ship launcher (mirrors native mineigc.cpp:175)
        var exp = ExportMine.FromMine(
            mine,
            (uint)TickIndex,
            launcherId: launcherId,
            sideId: mine.SideId ?? seeingSide);
        // Discovery belongs to the mine, independently of its historical
        // launcher (FedSrv.CPP:10754-10784). If the live layer is in this view,
        // order its pose before the newly discovered field as well.
        if (mine.Launcher is Ship layer && EmissionVisible(conn, layer, mine.OwnerSector)
            && layer.Fraction > 0 && layer.StationId is null && layer.ParentShip is null)
            EnsureEmissionPose(conn, layer, mine.OwnerSector);
        SendPeer(conn, PackBroadcast(new[] { Message(exp) }));
        if (ConsoleLog.Verbose) ConsoleLog.Write($"[xexport] side {seeingSide} <- peer {conn} mine {mine.Id} (type {mine.MineType.ExpendableTypeId}) @ {mine.Position}");   // P619c: gated
    }

    /// <summary>
    /// P797 — per-tick catch-up sweep for the cross-side identity of the static
    /// <b>predictable</b> objects native exports per side: alephs, asteroids,
    /// probes and treasures. The static mirror of
    /// <see cref="SweepCrossSideExports"/> (P349) and
    /// <see cref="SweepCrossSideMineExports"/> (P453), and the mid-game half of
    /// this task's join-export gate.
    /// <para><b>Native rule.</b> A static object carries
    /// <c>c_mtSeenBySide | c_mtPredictable</c> (<c>warpIGC.cpp:35</c>,
    /// <c>asteroidigc.cpp:36</c>, <c>probeigc.cpp:115</c>,
    /// <c>treasureIGC.cpp:167</c>), so when a side's scanner first sees it
    /// <c>ThingSiteImpl::UpdateSideVisibility</c> calls <c>ShowObject</c>
    /// (<c>FedSrv/FedSrv.CPP:10944-10950</c>), which sets the recipient side's
    /// <c>FM_GUARANTEED</c> default recipient and
    /// <c>ExportObj(pmodel, type, NULL)</c> to that whole side
    /// (<c>:10766-10775</c>: "Tell everyone on the side about the newly visible
    /// static object"), then emits <c>OBJECT_SPOTTED</c>(6) for a station, an
    /// aleph or a special (non-plain-buildable) asteroid (<c>:11055-11062</c>).
    /// The flag never clears again for a predictable object
    /// (<c>:11124-11126</c>), so the export happens once per object per side; the
    /// per-peer latch <c>_crossSideDiscoverySent</c> (not a per-side one) is what
    /// lets a peer that joins LATER be caught up by its own join export while a
    /// peer that was present receives exactly one copy.
    /// <para><b>Audience.</b> Per side, all seated peers of that side (the side's
    /// scanner net is shared: one scout's eyes are the whole side's), never the
    /// mission, never a peer of another side.
    /// <para><b>Cost.</b> One <c>SeenBySide</c> read per object per side per tick
    /// (the same scan the ASTEROIDS/PROBES update passes already do) plus one
    /// latch probe per (newly-seen object, peer of the seeing side). Nothing is
    /// encoded or sent for an object the side already knows, so a steady tick
    /// costs no packet work at all.
    /// </summary>
    private void SweepCrossSideDiscoveryExports()
    {
        var peers = SnapshotBroadcastPeers();
        Dictionary<short, List<ConnectionId>> bySide = new();
        foreach (var kv in peers)
        {
            if (kv.Value.Ship.SideId is not { } s) continue;   // unseated (team-lobby) peers see nothing side-gated
            if (!bySide.TryGetValue(s, out var list)) { list = new(); bySide[s] = list; }
            list.Add(kv.Key);
        }
        if (bySide.Count == 0) return;   // nobody seated on a real side

        foreach (var m in _missions)
        {
            // The P132 sweep only runs once the mission is Started/Starting, so
            // the latched SeenBySide flags are meaningful only then (the same
            // precondition the ship/mine sweeps assume).
            if (m.Stage != MissionStage.Started && m.Stage != MissionStage.Starting) continue;

            foreach (var sector in m.Sectors)
            {
                foreach (var (sideId, conns) in bySide)
                {
                    foreach (var warp in sector.Warps)
                    {
                        if (!warp.SeenBySide(sideId)) continue;
                        byte[]? packet = null;
                        int told = 0;
                        foreach (var conn in conns)
                            if (_crossSideDiscoverySent.Add((conn, (byte)IgcObjectType.Warp, warp.Warp.WarpId)))
                            {
                                packet ??= DiscoveryPacket(ExportWarp.FromWarp(warp).Encode(), warp);
                                SendPeer(conn, packet);
                                told++;
                            }
                        if (told > 0)
                            Console.WriteLine($"[discover] side {sideId} aleph {warp.Warp.WarpId} '{warp.Warp.Name}' sector {sector.Id} -> {told} peer(s)");   // P797 rig probe: one line per newly seen aleph
                    }

                    foreach (var rock in sector.Mineable)
                    {
                        if (!rock.SeenBySide(sideId)) continue;
                        byte[]? packet = null;
                        int told = 0;
                        foreach (var conn in conns)
                            if (_crossSideDiscoverySent.Add((conn, (byte)IgcObjectType.Asteroid, rock.Data.AsteroidId)))
                            {
                                packet ??= AsteroidDiscoveryPacket(conn, rock);
                                SendPeer(conn, packet);
                                told++;
                            }
                        if (told > 0)
                            Console.WriteLine($"[discover] side {sideId} asteroid {rock.Data.AsteroidId} '{rock.Data.Name}' sector {sector.Id} -> {told} peer(s)");   // P797 rig probe
                    }

                    foreach (var model in sector.Models)
                    {
                        if (model is Probe probe)
                        {
                            if (!probe.SeenBySide(sideId)) continue;
                            byte[]? packet = null;
                            int told = 0;
                            foreach (var conn in conns)
                                if (_crossSideDiscoverySent.Add((conn, (byte)IgcObjectType.Probe, probe.Id)))
                                {
                                    packet ??= ProbeDiscoveryPacket(probe);
                                    SendPeer(conn, packet);
                                    told++;
                                }
                            if (told > 0)
                                Console.WriteLine($"[discover] side {sideId} probe {probe.Id} sector {sector.Id} -> {told} peer(s)");   // P797 rig probe
                        }
                        else if (model is Treasure treasure)
                        {
                            // Native's OBJECT_SPOTTED gate excludes treasure
                            // (FedSrv.CPP:11051-11054: station / warp / special
                            // asteroid only), so this is the ExportObj half alone.
                            // The creation broadcast (SendTreasureSpawnSectorWide /
                            // BroadcastGeneratedTreasure) and the post-SET_CLUSTER
                            // snapshot latch this same mark, so no peer is told
                            // twice about one treasure.
                            if (!treasure.SeenBySide(sideId)) continue;
                            byte[]? packet = null;
                            int told = 0;
                            foreach (var conn in conns)
                                if (_crossSideDiscoverySent.Add((conn, (byte)IgcObjectType.Treasure, treasure.Id)))
                                {
                                    packet ??= FedPacket.Pack(new[] { new TreasureSpawn { Data = treasure.Data }.Encode() });
                                    SendPeer(conn, packet);
                                    told++;
                                }
                            if (told > 0)
                                Console.WriteLine($"[discover] side {sideId} treasure {treasure.Id} sector {sector.Id} -> {told} peer(s)");   // P797 rig probe
                        }
                    }
                }
            }
        }
    }

    /// <summary>P797 — one aleph's discovery packet: the <c>EXPORT(26)</c>/OT_warp
    /// byte-mate of the join export, followed by the <c>OBJECT_SPOTTED</c>(6)
    /// announcement native emits alongside it (<c>FedSrv.CPP:11055-11062</c>;
    /// alephs are always in that gate).</summary>
    private static byte[] DiscoveryPacket(byte[] export, ResolvedWarp warp)
    {
        var (otSpotter, oidSpotter) = warp.SideVisibility.LastSpotter;
        var spotted = new ObjectSpotted
        {
            OtObject = (short)IgcObjectType.Warp,
            OidObject = warp.Warp.WarpId,
            OtSpotter = otSpotter,
            OidSpotter = oidSpotter,
        };
        return FedPacket.Pack(new[] { export, spotted.Encode() });
    }

    /// <summary>P797 — one asteroid's discovery packet: the <c>EXPORT(26)</c>/
    /// OT_asteroid plus (a) the building effect riding on it, exactly as native's
    /// join export sends <c>ExportObj(pbe, OT_buildingEffect)</c> when it exists
    /// (<c>fsmission.cpp:4206-4212</c>) and (b) the <c>OBJECT_SPOTTED</c> for a
    /// special (non-plain-buildable) asteroid
    /// (<c>(capabilities &amp; ~c_aabmBuildable) != 0</c>, <c>FedSrv.CPP:11051-11054</c>,
    /// <c>c_aabmBuildable = 0x08</c> at <c>igc.h:765</c>).</summary>
    private byte[] AsteroidDiscoveryPacket(ConnectionId conn, Asteroid rock)
    {
        var messages = new List<byte[]>(3) { ExportAsteroid.FromAsteroid(rock.Data).Encode() };
        // Native ShowObject sends the building effect along with a newly spotted
        // rock (FedSrv.CPP:10819-10825) — but the live creation broadcast may
        // already have told this peer, so the shared per-(peer, rock) latch keeps
        // the native "create once" semantics.
        if (rock.BuildingEffect is { } effect
            && _buildingEffectExportSent.Add((conn, rock.Data.AsteroidId)))
            messages.Add(BuildingEffectExport(effect).Encode());
        if ((rock.Data.AabmCapabilities & ~0x08) != 0)
        {
            var (otSpotter, oidSpotter) = rock.SideVisibility.LastSpotter;
            messages.Add(new ObjectSpotted
            {
                OtObject = (short)IgcObjectType.Asteroid,
                OidObject = rock.Data.AsteroidId,
                OtSpotter = otSpotter,
                OidSpotter = oidSpotter,
            }.Encode());
        }
        return FedPacket.Pack(messages);
    }

    /// <summary>P797 — one probe's discovery packet: the <c>EXPORT(26)</c>/OT_probe
    /// the client needs to instantiate a foreign static probe (PROBES_UPDATE(92)
    /// carries health only — it has no type/position). Native's OBJECT_SPOTTED
    /// gate does not cover probes, so this is the export alone.</summary>
    private byte[] ProbeDiscoveryPacket(Probe probe)
    {
        short launcherId = (probe.Launcher as Ship)?.Id ?? -1;   // NA when no launcher ship (mirrors native probeigc.cpp)
        // timeCreate uses the port's established tick-index convention (the same
        // value BroadcastExportMine/the P453 cross-side mine sweep send); the
        // native ms clock is unused across the port's exports.
        var export = ExportProbe.FromProbe(probe, (uint)TickIndex, launcherId, probe.SideId ?? -1);
        return FedPacket.Pack(new[] { export.Encode() });
    }

    /// <summary>HUD-17 — recompute every own-side ship's <c>Detected</c> bit for
    /// the current tick and record which ships changed, so the status broadcast
    /// can re-send exactly those (native's changed-status delta,
    /// <c>FedSrv/FedSrv.CPP:3663-3700</c>: even frames send nothing, odd frames
    /// send every side's changed <c>ShipStatus</c>).
    /// <para><b>Coverage.</b> The two ship populations the own-side status paths
    /// serve: every peer ship (<c>_peerShips</c>, regardless of mission stage —
    /// its own status is refreshed from the join burst on) and every same-side
    /// NPC the teammate path serves (an <see cref="Ship.IsNpc"/> ship of a
    /// Started/Starting mission, the same scan <c>BroadcastShipStates</c> builds
    /// its per-(mission, side) NPC grouping from).</para>
    /// <para><b>Cost.</b> One <see cref="ShipDetection.IsDetectedByEnemy"/> call
    /// per covered ship (one indexed <see cref="Mission.Sides"/> loop with a
    /// dictionary lookup per side) plus one dictionary compare per ship. The
    /// change set is cleared in place, never reallocated; both collections are
    /// permanent fields. Bounded locally: at 400 ships and 2 sides about 800
    /// lookups per tick and no allocation.</para>
    /// </summary>
    private void SweepShipDetection()
    {
        _shipDetectedChanged.Clear();

        // Peer ships: the own-ship and teammate status paths both source these.
        foreach (var kv in _peerShips)
            if (IncludeShipBroadcastMission(kv.Value.Ship.Mission)) UpdateShipDetection(kv.Value.Ship);

        // Same-side NPCs: the P366 path's population. Scanned exactly like
        // BroadcastShipStates' sameSideNpcBySide builder (in-flight missions
        // only), but walked in place so the sweep allocates nothing.
        for (int mi = 0; mi < _missions.Count; mi++)
        {
            var m = _missions[mi];
            if (!IncludeShipBroadcastMission(m)) continue;
            if (m.Stage != MissionStage.Started && m.Stage != MissionStage.Starting) continue;
            // Index loops, not foreach: Sectors/Models are IReadOnlyList<T>, whose
            // enumerator is boxed by foreach (80 B/sector/tick) — the same
            // no-enumerator-boxing rule the side loop follows.
            var sectors = m.Sectors;
            for (int si = 0; si < sectors.Count; si++)
            {
                var models = sectors[si].Models;
                for (int i = 0; i < models.Count; i++)
                    if (models[i] is Ship { IsNpc: true } npc)
                        UpdateShipDetection(npc);
            }
        }
    }

    /// <summary>HUD-17 — one ship's detection compare/update: recompute the
    /// native rule, store it, and mark the id when it differs from the last
    /// value written to a status frame. A missing entry reads as
    /// <c>false</c> (the fresh native <c>ShipStatus</c> default and today's
    /// pre-first-sweep state), so a ship whose very first value is <c>false</c>
    /// is not treated as a spurious change.</summary>
    private void UpdateShipDetection(Ship ship)
    {
        bool detected = ShipDetection.IsDetectedByEnemy(ship);
        if (_shipDetected.TryGetValue(ship.Id, out var previous))
        {
            if (previous == detected) return;
        }
        else if (!detected)
        {
            _shipDetected[ship.Id] = false;
            return;
        }
        _shipDetected[ship.Id] = detected;
        _shipDetectedChanged.Add(ship.Id);
    }

    /// <summary>P493 — per-tick edge sweep that delivers an enemy ship's
    /// LAST-KNOWN sector/state (<c>SHIP_STATUS(90)</c>) to a side whose scanner
    /// net has come to see it, and the hidden transition when it loses sight —
    /// the port of native <c>ShowObject</c>→<c>ShipStatusSpotted</c> /
    /// <c>HideObject</c>→<c>ShipStatusHidden</c> (FedSrv.CPP:10838/10870,
    /// fsship.cpp:237/287), minus the formal per-side <c>SideVisibility</c>
    /// Show/Hide state machine this port already collapses into the latched
    /// <c>SeenBySide</c> flags of <c>Sector.ResolveCrossSideVisibility</c>
    /// (P132) and its P270/P476 refinements.
    /// <para><b>Trigger &amp; cadence.</b> A status is sent only on the
    /// false→true ("spotted") / true→false ("hidden") EDGE of a ship's
    /// <c>SeenBySide(sideId)</c>, tracked per (seeing-side, ship) — never every
    /// tick the ship stays visible (the native trigger points are the single
    /// Show/Hide transition; fsship.cpp ShipStatusRecalculate only re-fires for
    /// currently-seen sides on discrete events). A slow catch-up refresh
    /// (<see cref="CrossSideStatusRefreshTicks"/>) re-sends a currently-seen
    /// enemy's spotted status so a peer that joins an already-informed side (or
    /// misses an edge packet) still learns the last-known sector.</para>
    /// <para><b>Audience.</b> Per-side sensor-net: when a side comes to see an
    /// enemy ship, EVERY seated peer of that side receives the status regardless
    /// of which sector that peer sits in — a probe or scout anywhere makes the
    /// whole side aware, the "eyes for the team" behaviour the task names. This
    /// deliberately does NOT reuse the P375 <c>CrossSideViewers</c> seated-peer-
    /// in-subject-sector gate (that gate exists for live per-tick telemetry; a
    /// coarse last-known sector status is exactly the cross-sector info a fleet
    /// member is owed). The ship's OWN side is skipped (the same-side
    /// teammate path already serves it).</para>
    /// <para><b>Coverage.</b> Any <see cref="Ship"/> in any sector — peer-owned
    /// or NPC — is eligible; native maintains ship status for every ship's
    /// <c>CFSShip</c> (including predator/NPC ships), and the port already gives
    /// same-side NPCs a status (<c>MakeNpcShipStatus</c>, P366). A cloak-hidden
    /// enemy (P270) drops out because its <c>SeenBySide</c> is false via the
    /// P132 sweep's shrunken effective signature.</para>
    /// </summary>
    private void SweepCrossSideShipStatuses()
    {
        // Audience: the seated peers grouped by their ship's side.
        var connsBySide = GatherSideConnections();
        if (connsBySide.Count == 0) return;   // nobody seated on a real side

        foreach (var m in _missions)
        {
            // The P132 visibility sweep only runs once the mission is
            // Started/Starting (Mission.Update early-returns otherwise), so the
            // SeenBySide flags we read are meaningful only then — the same
            // precondition the cross-side export sweeps assume.
            if (!IncludeShipBroadcastMission(m)) continue;
            if (m.Stage != MissionStage.Started && m.Stage != MissionStage.Starting) continue;

            foreach (var sector in m.Sectors)
                foreach (var model in sector.Models)
                {
                    if (model is not Ship ship) continue;
                    if (ship.SideId is not { } ownSide) continue;   // sideless ships have no faction to be seen as
                    short shipSector = ship.Sector?.Id ?? sector.Id;

                    foreach (var (sideId, conns) in connsBySide)
                    {
                        if (sideId == ownSide) continue;            // own side — the same-side teammate path
                        bool nowSeen = ship.SeenBySide(sideId);     // latched P132/P270/P476 sweep result this tick

                        var key = (sideId, ship.Id);
                        bool wasSeen = _crossSideShipStatusSeen.TryGetValue(key, out var w) && w;

                        if (nowSeen != wasSeen)
                        {
                            // A genuine visibility edge: fire the spotted/hidden
                            // transition exactly once, native Show/Hide style.
                            _crossSideShipStatusSeen[key] = nowSeen;
                            bool spotted = nowSeen;
                            foreach (var conn in conns)
                            {
                                SendPeer(conn, PackCrossSideStatus(ship, shipSector, spotted));
                                _crossSideShipStatusTick[(conn, ship.Id)] = TickIndex;
                            }
                            if (ConsoleLog.Verbose) ConsoleLog.Write($"[{ (spotted ? "xspot" : "xhidd") }] side {sideId} {(spotted ? "spots" : "loses")} enemy ship {ship.Id} sector {shipSector}");
                        }
                        else if (nowSeen)
                        {
                            // Still visible, no edge: only the slow catch-up
                            // refresh re-sends (late-joiner / lost-packet heal),
                            // never per-tick.
                            foreach (var conn in conns)
                            {
                                long last = _crossSideShipStatusTick.TryGetValue((conn, ship.Id), out var l) ? l : -1;
                                // P604b — phase-stagger the slow catch-up refresh
                                // per (conn, ship): it was `TickIndex - last >= 100`
                                // for EVERY (conn, seen-enemy) pair on the same
                                // aligned 100th tick — an O(peers × enemies)
                                // SHIP_STATUS re-send burst that compounded the
                                // ~5 s hitch on 221. Each pair now re-sends once
                                // per interval on its own phase slot (phase from
                                // the stable (conn, ship) ids), so the fleet-wide
                                // simultaneity is gone while the self-heal cadence
                                // (re-learn within one interval) is preserved.
                                long phase = ((uint)conn.Value + (uint)ship.Id) % CrossSideStatusRefreshTicks;
                                bool phasedDue = last < 0 ||
                                    (((long)TickIndex + phase) % CrossSideStatusRefreshTicks == 0);
                                if (phasedDue)
                                {
                                    SendPeer(conn, PackCrossSideStatus(ship, shipSector, spotted: true));
                                    _crossSideShipStatusTick[(conn, ship.Id)] = TickIndex;
                                }
                            }
                        }
                    }
                }
        }
    }

    /// <summary>P493 — build a cross-side <c>SHIP_STATUS(90)</c> about an enemy
    /// <see cref="Ship"/> for the side that has just come to (or just lost) sight
    /// of it. Faithful to native <c>CFSShip::ShipStatusSpotted</c>
    /// (fsship.cpp:237-260): state=c_ssFlying, parent NA, hull = the ship's hull,
    /// sector = the ship's current cluster. The HIDDEN variant mirrors
    /// <c>ShipStatusHidden</c> (fsship.cpp:287-291): the same last-known fields
    /// are preserved (native <c>SetUnknown</c> only flips the unknown bit,
    /// keeping hull/sector/state), with <c>Unknown=true</c> signalling the client
    /// no longer knows where the enemy is. <c>Detected</c> is false for both —
    /// the seeing side's own copy never has it set (only the ship's OWN side
    /// copy is marked detected by ShipStatusSpotted, a different concern).</summary>
    private static ShipStatusMessage MakeCrossSideShipStatus(Ship ship, short shipSector, bool spotted)
    {
        bool docked = ship.StationId is not null;
        return new()
        {
            ShipId = ship.Id,
            HullId = ship.HullType.HullId,
            SectorId = shipSector,                                  // the sector where the side's scanner net sees it (last-known)
            StationId = docked ? ship.StationId!.Value : (short)-1, // NA when flying free
            ParentId = -1,                                          // NA — ShipStatusSpotted sets ParentID=NA (fsship.cpp:257)
            State = docked ? ShipStatusMessage.StateDocked : ShipStatusMessage.StateFlying,  // c_ssFlying (fsship.cpp:256)
            Unknown = !spotted,                                     // hidden => SetUnknown (fsship.cpp:290)
            Detected = false,                                       // the seeing side's copy is not marked detected
            StateTime = 0,                                          // sim clock unused (established port convention)
        };
    }

    /// <summary>P346 — the display name of a ship's owning peer, if it has one
    /// (searched by reference in <c>_peerShips</c>, the same ownership-resolution
    /// pattern Server.Docking.cs uses); null for an ownerless NPC so the caller
    /// falls back to a stable label.</summary>
    private string? PeerName(Ship ship)
    {
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, ship))
                return _peerNames.TryGetValue(kv.Key, out var n) ? n : null;
        return null;
    }

    /// <summary>Legacy cross-side audience for collision/side notifications:
    /// a side with a seated peer in this sector, excluding cloak-hidden enemies.
    /// Fire and deployment use BroadcastShipEmission instead, which also checks
    /// each recipient's sector and orders the source pose before its effect.</summary>
    private IEnumerable<(ConnectionId Conn, short SeeingSide)> CrossSideViewers(Ship subject, Sector subjectSector)
    {
        short? subjectSide = subject.SideId;
        if (subjectSide is null) yield break;

        // Group the seated peers by side with each side's seated sectors — the
        // exact per-side audience SweepCrossSideExports builds once per tick.
        var bySide = new Dictionary<short, (List<ConnectionId> Conns, HashSet<Sector> Sectors)>();
        foreach (var kv in _peerShips)
        {
            if (kv.Value.Ship.SideId is not { } s) continue;      // unseated (team-lobby) peers see nothing cross-side
            if (s == subjectSide.Value) continue;                  // the subject's own side — handled by the same-side caller path
            if (!bySide.TryGetValue(s, out var e)) { e = (new List<ConnectionId>(), new HashSet<Sector>()); bySide[s] = e; }
            e.Conns.Add(kv.Key);
            e.Sectors.Add(kv.Value.Sector);
        }

        foreach (var (sideId, e) in bySide)
        {
            if (!e.Sectors.Contains(subjectSector)) continue;                    // no seated peer of this side in the shooter's sector -> no telemetry -> no visibility
            if (subject.IsCloaked && !subject.SeenBySide(sideId)) continue;       // cloak-hidden enemy -> no telemetry (P270) -> none here
            foreach (var conn in e.Conns)
                yield return (conn, sideId);
        }
    }

    /// <summary>Shared by Tick() and (optionally) a caller wishing to hold real
    /// cadence on top of its own N sim advances. Sleeps the remainder of the
    /// period, or logs an overrun if the pass cost already exceeded it.</summary>
    private void SyncCadence(double cost)
    {
        double budget = _period - cost;
        if (budget > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(budget));
        }
        else
        {
            lock (_statsLock) _overruns++;
            Console.WriteLine($"[overrun] tick cost {cost * 1000:0.##} ms exceeded period {_period * 1000:0.##} ms " +
                              $"by {(cost - _period) * 1000:0.##} ms (sim continues at full rate)");
        }
    }

    /// <summary>
    /// P133 — the periodic per-side <c>STATIONS_UPDATE</c>(57) broadcast, port of
    /// the native per-cluster-per-side batch in <c>fsship.cpp:820-845</c>+. For
    /// each side that currently has a seated peer it gathers every station in the
    /// mission that side <b>currently sees</b> — reading the <em>live</em>
    /// <see cref="Station.SeenBySide"/> flags P132's per-tick sweep maintains
    /// (never re-deriving visibility independently) — batches them into one
    /// <see cref="Wire.StationsUpdate"/> and sends it to that side's connections
    /// only, gated on <c>if (nStations != 0)</c> exactly as the native snippet.
    /// <list type="bullet">
    /// <item><b>Per-side, not whole-mission.</b> Each side receives only the
    /// stations it can currently see (contrast the whole-mission audiences of
    /// <c>ASTEROID_MINED</c>(198)/<c>STATION_DESTROYED</c>(59)/<c>STATION_CAPTURE</c>(58)).
    /// The send target is <c>pside</c>'s own connection group — here, the seated
    /// peers whose <c>SideId</c> matches (the same side-wide audience
    /// <see cref="BroadcastMoneyChangeSideWide"/> selects, Server.cs:3028-3039).</item>
    /// <item><b>Cadence.</b> Runs every <see cref="SimPass"/> (every tick), the
    /// port's established per-tick-pass convention (P132 §3 rejected inventing a
    /// new update-frequency mechanism; the native every-N delta is a throttle, not
    /// a semantic gate). Because it runs after <c>Mission.Update</c>, each tick's
    /// broadcast reflects the just-computed visibility sweep and the latest
    /// <see cref="Station.ArmorFraction"/>/<see cref="Station.ShieldFraction"/>
    /// (P125) — a station's fractions changing this tick are carried in the next
    /// tick's message. It is periodic per native, <b>not</b> a one-time join-sync
    /// like the <c>CREATE_BUCKETS</c>/<c>SET_MONEY</c>/<c>SET_BRIEFING_TEXT</c> trio.</item>
    /// <item><b>Batching scope.</b> Aggregates across all missions/sectors into
    /// one per-side message. <c>STATIONS_UPDATE</c> carries no clusterID and the
    /// client indexes stations by id, so one combined per-side list is correct
    /// (native's per-cluster split is an iteration artifact, not a wire need).</item>
    /// </list>
    /// Runs on the sim thread (same one that owns <c>_peerShips</c> and the
    /// visibility sweep), so no extra locking.
    /// </summary>
    private void BroadcastStationsUpdate()
    {
        // Side universe = the sides with seated peers (the only sides that have
        // anyone to send to). Group the peer connections by their ship's side in
        // one pass over a stable snapshot.
        var connsBySide = GatherSideConnections();
        if (connsBySide.Count == 0) return;

        // P324 (M4): cadence-throttled. The per-side batch is re-encoded and
        // re-sent only every c_nPassesPerUpdate (10) ticks — the faithful native
        // cadence for these per-side object syncs (native sends them to a flying
        // player on cluster entry, FedSrv fsship.cpp:817-849; to a docked player
        // every 0.75 s) — unless a peer newly joined the side (forced immediate).
        // On a quiet tick (not a cadence tick and no join) the whole gather is
        // skipped, not just the encode/send.
        bool cadenceDue = (TickIndex % Sector.c_nPassesPerUpdate) == 0;
        if (!cadenceDue && _objectSyncDirtySides.Count == 0) return;   // P324 (M4): nothing due this tick

        // All runtime stations in the mission, once — read off the live sweep's
        // subject collection (Sector.StationRuntimes), never re-derived.
        var allStations = _missions
            .SelectMany(m => m.Sectors)
            .SelectMany(s => s.StationRuntimes)
            .ToArray();

        foreach (var (sideId, conns) in connsBySide)
        {
            if (!cadenceDue && !_objectSyncDirtySides.Contains(sideId))
                continue;                              // P324 (M4): not this side's cadence tick

            // Only stations the side currently sees (P132's live SeenBySide).
            var visible = allStations.Where(st => st.SeenBySide(sideId)).ToArray();
            if (visible.Length == 0) continue;              // native: `if (nStations != 0)`
            ExportDiscoveredStations(sideId, conns, visible);
            var msg = StationsUpdate.FromStations(visible);
            var packet = PackBroadcast(Message(msg));
            foreach (var t in conns) SendPeerCoalescing(t, packet, CoalesceStations);

            Console.WriteLine($"[stations] side {sideId} sees {visible.Length} station(s) " +
                              $"=> STATIONS_UPDATE(57) -> {conns.Count} peer(s) [PER SIDE]");
        }
    }

    /// <summary>
    /// P803 (SIM-0470) — the <c>rgActiveTurretUpdates</c> fill (native
    /// FedSrv.CPP:4049-4071): walk this cluster's boarded gunner ships, keep
    /// those with a stored <see cref="ClientActiveTurretUpdate"/> whose time is
    /// within ±1.5 s of the mission clock (the FedSrv.CPP:3783-3790 freshness
    /// gate), skip the recipient's own ship (FedSrv.CPP:4058 — a gunner never
    /// sees its own relay), and copy <c>atu.controls</c>/<c>atu.orientation</c>
    /// intact with the ship id and a CompactTime delta from
    /// <paramref name="timeRef"/> (FedSrv.CPP:4064-4068). The stored wire time
    /// is P655's server milliseconds; this message's time domain is the tick
    /// index (every <see cref="HeavyShipUpdateEntry"/> carries tickIndex), so
    /// the instant is converted into that domain before the CompactTime delta
    /// is taken. The native walk only considers PLAYERS' gunner ships
    /// (FedSrv.CPP:3774 <c>IsPlayer()</c>) — this port has no boarding drones,
    /// so every boarded ship here qualifies.
    /// </summary>
    private ServerActiveTurretUpdate[] CollectActiveTurretRelay(Sector sector, Ship self, uint timeRef)
    {
        List<ServerActiveTurretUpdate>? acts = null;
        double missionNow = self.Mission?.LastUpdate ?? 0.0;
        double msToTicks = Math.Max(1, _config.UpdatesPerSecond) / 1000.0;
        foreach (var model in sector.Models)
        {
            if (model is not Ship gunner || gunner.ParentShip is null || gunner.TurretId is null) continue;
            if (gunner.IsNpc || gunner.Dead || gunner.StationId is not null || gunner.ParentShip.StationId is not null) continue;
            if ((gunner.StateM & ShipControlState.WeaponsMask) == 0) continue;
            if (ReferenceEquals(gunner, self)) continue;            // FedSrv.CPP:4058
            if (!_lastTurretUpdates.TryGetValue(gunner.Id, out var stu)) continue;
            double dt = stu.TimeMs / 1000.0 - missionNow;
            if (dt is < -1.5 or > 1.5) continue;                    // FedSrv.CPP:3786-3789
            uint timeTicks = (uint)Math.Round(stu.TimeMs * msToTicks);
            (acts ??= new List<ServerActiveTurretUpdate>()).Add(
                ServerActiveTurretUpdate.From(gunner.Id, timeRef, timeTicks, stu.Controls, stu.Orientation));
        }
        return acts?.ToArray() ?? [];
    }

    /// <summary>
    /// P134 — the periodic per-side ASTEROIDS_UPDATE (179) broadcast, port of
    /// the native per-cluster-per-side batch in fsship.cpp:883-915 — the direct
    /// sibling of the STATIONS_UPDATE batch (fsship.cpp:817-849, P133). For each
    /// side that currently has a seated peer it gathers every asteroid in the
    /// mission that side currently sees — reading the live
    /// <see cref="Asteroid.SeenBySide"/> flags P132's per-tick sweep maintains
    /// (extended in P134 so asteroids are scored as visibility targets, exactly
    /// the c_mtSeenBySide role native gives a rock at asteroidigc.cpp:36; never
    /// re-derived here) — batches them into one
    /// <see cref="Wire.AsteroidsUpdate"/> and sends it to that side's
    /// connections only, gated on if (nAsteroids != 0) exactly as the native
    /// snippet.
    /// <list type="bullet">
    /// <item><b>Per-side, not whole-mission.</b> Each side receives only the
    /// asteroids it can currently see (contrast the whole-mission audiences of
    /// ASTEROID_MINED(198)/ASTEROID_DESTROYED(86)). The send target is pside's
    /// own connection group — the seated peers whose SideId matches, the same
    /// selection BroadcastStationsUpdate and BroadcastMoneyChangeSideWide use.</item>
    /// <item><b>Cadence.</b> Runs every SimPass (every tick), the port's
    /// established per-tick-pass convention, mirroring BroadcastStationsUpdate
    /// and the P132 §3 finding (the native every-10 throttle is not imported).
    /// Because it runs after Mission.Update, each tick's broadcast reflects the
    /// just-computed visibility sweep and the latest Asteroid.OreRemaining /
    /// Asteroid.Fraction (P102/P55) — a rock's ore changing this tick (real
    /// mining) is carried in the next tick's message. It is periodic per native,
    /// not a one-time join-sync.</item>
    /// <item><b>Batching scope.</b> Aggregates across all missions/sectors into
    /// one per-side message. ASTEROIDS_UPDATE carries no clusterID and the
    /// client indexes asteroids by id, so one combined per-side list is correct
    /// (native's per-cluster split is an iteration artifact, not a wire need) —
    /// the identical rationale P133 recorded for STATIONS_UPDATE.</item>
    /// </list>
    /// Runs on the sim thread (same one that owns _peerShips and the visibility
    /// sweep), so no extra locking.
    /// </summary>
    private void BroadcastAsteroidsUpdate()
    {
        // Side universe = the sides with seated peers (the only sides that have
        // anyone to send to). Group the peer connections by their ship's side in
        // one pass over a stable snapshot.
        var connsBySide = GatherSideConnections();
        if (connsBySide.Count == 0) return;

        // P324 (M4): cadence-throttled — see BroadcastStationsUpdate. The per-side
        // batch is re-encoded/re-sent only every c_nPassesPerUpdate (10) ticks
        // (faithful native cadence; native's own every-10 UpdateSeenBySide means
        // the seen-set changes no faster than this anyway), forced immediately on
        // a peer newly joining the side. Quiet ticks skip the gather entirely.
        bool cadenceDue = (TickIndex % Sector.c_nPassesPerUpdate) == 0;
        if (!cadenceDue && _objectSyncDirtySides.Count == 0) return;   // P324 (M4): nothing due this tick

        // All runtime asteroids in the mission, once — read off the live sweep's
        // subject collection (Sector.Mineable), never re-derived.
        var allAsteroids = _missions
            .SelectMany(m => m.Sectors)
            .SelectMany(s => s.Mineable)
            .ToArray();

        foreach (var (sideId, conns) in connsBySide)
        {
            if (!cadenceDue && !_objectSyncDirtySides.Contains(sideId))
                continue;                              // P324 (M4): not this side's cadence tick

            // Only asteroids the side currently sees (P132's live SeenBySide,
            // now scoring asteroids as targets — P134).
            var visible = allAsteroids.Where(a => a.SeenBySide(sideId)).ToArray();
            if (visible.Length == 0) continue;              // native: if (nAsteroids != 0)
            var msg = AsteroidsUpdate.FromAsteroids(visible);
            var packet = PackBroadcast(Message(msg));
            foreach (var t in conns) SendPeerCoalescing(t, packet, CoalesceAsteroids);

            Console.WriteLine($"[asteroids] side {sideId} sees {visible.Length} asteroid(s) " +
                              $"=> ASTEROIDS_UPDATE(179) -> {conns.Count} peer(s) [PER SIDE]");
        }
    }

    /// <summary>
    /// P135 — the periodic per-side PROBES_UPDATE (92) broadcast, port of the
    /// native per-cluster-per-side batch in fsship.cpp:851-881 — the direct
    /// sibling of the STATIONS_UPDATE batch (fsship.cpp:817-849, P133) and the
    /// ASTEROIDS_UPDATE batch (fsship.cpp:883-915, P134), and the simplest of
    /// the three (a ProbeState is just id + health fraction, Messages.h:635-642).
    /// For each side that currently has a seated peer it gathers every probe in
    /// the mission that side currently sees — reading the live
    /// <see cref="Probe.SeenBySide"/> flags P132's per-tick sweep maintains
    /// (<b>with no P134-style extension needed</b>: probes are real Models that
    /// carry a <c>SideId</c>, so the sweep's own probe case scores them as both
    /// targets and scanners for their own side, Sector.cs:2133-2136 — never
    /// re-derived here) — batches them into one <see cref="Wire.ProbesUpdate"/>
    /// and sends it to that side's connections only, gated on if (nProbes != 0)
    /// exactly as the native snippet.
    /// <list type="bullet">
    /// <item><b>Per-side, not whole-mission.</b> Each side receives only the
    /// probes it can currently see (contrast the whole-mission audiences of
    /// PROBE_DESTROYED(85)/EXPORT(26)). The send target is pside's own
    /// connection group — the seated peers whose SideId matches, the same
    /// selection BroadcastStationsUpdate and BroadcastAsteroidsUpdate use.</item>
    /// <item><b>Cadence.</b> Runs every SimPass (every tick), the port's
    /// established per-tick-pass convention, mirroring its two siblings (the
    /// native every-10 throttle is not imported). Because it runs after
    /// Mission.Update, each tick's broadcast reflects the just-computed
    /// visibility sweep and the latest Probe.Fraction (P100) — a probe's health
    /// changing this tick (real combat damage) is carried in the next tick's
    /// message.</item>
    /// <item><b>Batching scope.</b> Aggregates across all missions/sectors into
    /// one per-side message. PROBES_UPDATE carries no clusterID and the client
    /// indexes probes by id, so one combined per-side list is correct (native's
    /// per-cluster split is an iteration artifact, not a wire need) — the
    /// identical rationale P133/P134 recorded for its siblings.</item>
    /// </list>
    /// Runs on the sim thread (same one that owns _peerShips and the visibility
    /// sweep), so no extra locking.
    /// </summary>
    private void BroadcastProbesUpdate()
    {
        // Side universe = the sides with seated peers (the only sides that have
        // anyone to send to). Group the peer connections by their ship's side in
        // one pass over a stable snapshot.
        var connsBySide = GatherSideConnections();
        if (connsBySide.Count == 0) return;

        // P324 (M4): cadence-throttled — see BroadcastStationsUpdate. The per-side
        // batch is re-encoded/re-sent only every c_nPassesPerUpdate (10) ticks,
        // forced immediately on a peer newly joining the side. Probes broadcast is
        // the last of the three throttled per-side object syncs, so it clears the
        // join-dirty set after consuming it (next SimPass starts clean). Quiet
        // ticks skip the gather entirely.
        bool cadenceDue = (TickIndex % Sector.c_nPassesPerUpdate) == 0;
        if (!cadenceDue && _objectSyncDirtySides.Count == 0) return;   // P324 (M4): nothing due this tick

        // All runtime probes in the mission, once — read off the live sweep's
        // subject collection (Sector.Models, where the Probe case lives), never
        // re-derived.
        var allProbes = _missions
            .SelectMany(m => m.Sectors)
            .SelectMany(s => s.Models)
            .OfType<Probe>()
            .ToArray();

        foreach (var (sideId, conns) in connsBySide)
        {
            if (!cadenceDue && !_objectSyncDirtySides.Contains(sideId))
                continue;                              // P324 (M4): not this side's cadence tick

            // Only probes the side currently sees (P132's live SeenBySide,
            // already scoring probes as targets — they are Models with a SideId).
            var visible = allProbes.Where(p => p.SeenBySide(sideId)).ToArray();
            if (visible.Length == 0) continue;              // native: if (nProbes != 0)
            var msg = ProbesUpdate.FromProbes(visible);
            var packet = PackBroadcast(Message(msg));
            foreach (var t in conns) SendPeerCoalescing(t, packet, CoalesceProbes);

            Console.WriteLine($"[probes] side {sideId} sees {visible.Length} probe(s) " +
                              $"=> PROBES_UPDATE(92) -> {conns.Count} peer(s) [PER SIDE]");
        }
    }

    /// <summary>
    /// P139 — the periodic <b>per-player</b> LIGHT/HEAVY_SHIPS_UPDATE (175/176)
    /// broadcast, the consumer of P138's per-cluster candidate list. Port of the
    /// native per-player assembly in <c>SendShipUpdates</c>
    /// (<c>FedSrv.CPP:4029-4207</c>). For each connected, seated peer:
    /// <list type="number">
    /// <item><b>Resolve the peer's real command target.</b>
    /// <see cref="Ship.CommandTarget"/> (the port's
    /// <c>m_commandTargets[c_cmdCurrent]</c>, <c>shipIGC.h:2624</c>) — the same
    /// primitive the weapon-aim slice already reads, reused here rather than a
    /// new targeting system. If the target is a ship in the peer's own cluster
    /// (non-self, non-boarded) it fills the <b>first heavy slot</b> and drives
    /// the <c>bpTargetHull</c>/<c>bpTargetShield</c> scalars
    /// (<c>FedSrv.CPP:4075-4124</c>); a probe target sets <c>bpTargetHull</c>
    /// only, no heavy slot.</item>
    /// <item><b>Assemble the heavy list under the P219 budget.</b> The candidate
    /// set from <see cref="Sector.GetShipUpdateCandidates"/> (P138 —
    /// cluster-membership, any side), minus the peer's own ship (its state already
    /// rides <c>SingleShipUpdate</c>/<c>ServerShipUpdate</c>, so it must not
    /// double-send here; native skips <c>pfsShip != pfsPlayer</c>,
    /// <c>FedSrv.CPP:4151</c>) and minus the promoted target, quantized as heavy
    /// records. With <c>≤ 2</c> candidates every one goes HEAVY (native's
    /// small-candidate fast path, <c>FedSrv.CPP:3860-3864</c>); beyond that the
    /// candidates are sorted by their staleness (<see cref="Ship.Deviation"/>,
    /// <c>CFSShip::m_deviation</c>) and only a fixed quota of
    /// <c>cHeavyPerInterval + cLightPerInterval = 4</c> per peer per tick is sent —
    /// the deviation/staleness budget P139 named as its follow-up (see
    /// <c>SHIP_UPDATES_BROADCAST_P219.md</c>; the split's history in the P139
    /// doc's §2). The LIGHT tier is folded into heavy (no
    /// <c>LIGHT_SHIPS_UPDATE</c> wiring exists yet), so the total is still bounded.
    /// Skipped ships accrue deviation and rise to the front next tick.</item>
    /// <item><b>Scalar region.</b> <c>timeReference</c> = the tick index
    /// (<c>g.timeNow</c>, FedSrv.CPP:3996); <c>positionReference</c> = the peer's
    /// own ship position (FedSrv.CPP:4128-4133 — the peer is in this cluster by
    /// construction); <c>bpTargetHull</c>/<c>bpTargetShield</c> per the
    /// target-type switch; the six <c>fractions</c> bytes are the <b>recipient's
    /// own</b> fractions (FedSrv.CPP:4044-4047, <c>ExportFractions</c>).</item>
    /// <item><b>Send to the peer's OWN connection only</b> via
    /// <see cref="SendPeer"/> — per-player, not per-side (this family's
    /// send target is each individual connection, unlike STATIONS/ASTEROIDS/
    /// PROBES which are per-side batches). HEAVY is sent every tick to every
    /// seated peer (native: "Send the heavy ship update whenever the player is
    /// flying since it has hitpoint information", FedSrv.CPP:4179-4180). LIGHT
    /// is <b>not</b> emitted: native forwards it only when non-empty
    /// (FedSrv.CPP:4183-4188), and with the every-heavy decision it is always
    /// empty. The active-turret relay blob (P137) is left empty — the port
    /// stores no per-ship <c>ClientActiveTurretUpdate</c> to relay (deferred,
    /// see §3 of the MD).</item>
    /// </list>
    /// Runs on the sim thread (same one that owns <c>_peerShips</c> and the
    /// visibility sweep), after <c>Mission.Update</c>, so every tick's message
    /// reflects the just-computed candidate list — same cadence as the
    /// STATIONS/ASTEROIDS/PROBES siblings.
    /// </summary>
    private void BroadcastShipsUpdate(uint tickIndex)
    {
        _inactiveTurretRelays.Clear();
        var peers = SnapshotBroadcastPeers();
        if (_broadcastInterestTiersEnabled) CaptureInterestSnapshot(peers);
        try
        {
            // Allocate viewer state on the sim thread before sector workers read it.
            bool trackCadence = _broadcastInterestTiersEnabled || _minUpdateHz > 0 || TickProfiler.Instance is not null;
            if (trackCadence)
                foreach (var kv in peers)
                    if (!_viewerPoseCadence.ContainsKey(kv.Key))
                        _viewerPoseCadence.Add(kv.Key, new ViewerPoseCadence());
            double poseNow = UpdateCadenceNow;

            // P603 — default (broadcast.parallelBuild off): the original serial
            // per-peer loop, byte-for-byte.
            if (!_broadcastParallelBuild)
            {
                foreach (var kv in peers) BroadcastShipsUpdatePeer(kv, tickIndex, trackCadence, poseNow);
                return;
            }

            // P603 — parallel across SECTORS, not across peers. The per-peer body
            // mutates each candidate ship's `Deviation` staleness accumulator, and a
            // later peer IN THE SAME SECTOR reads that mutated value back this same
            // tick (it orders its own serve/bump by Deviation). That is cross-peer,
            // order-dependent shared mutable state — not safely parallelisable across
            // peers while staying byte-identical. Peers in DIFFERENT sectors touch
            // disjoint candidate lists / Deviation sets, so they are fully independent:
            // partition peers by sector (preserving `_peerShips` order within each)
            // and run each sector's peers serially on one worker. For the single-
            // sector (concentrated-fight) case this yields no speedup — documented in
            // docs/reports/P603_PARALLEL_BROADCAST_BUILD.md; broadcastShipStates (the
            // dominant O(N²) bucket) is the per-peer-parallel one.
            var bySector = new Dictionary<Sector, List<KeyValuePair<ConnectionId, LiveShip>>>();
            foreach (var kv in peers)
            {
                if (!bySector.TryGetValue(kv.Value.Sector, out var list)) { list = new(); bySector[kv.Value.Sector] = list; }
                list.Add(kv);
            }
            var po = new ParallelOptions { MaxDegreeOfParallelism = _broadcastMaxParallelism };
            Parallel.ForEach(bySector, po, grp =>
            {
                foreach (var kv in grp.Value) BroadcastShipsUpdatePeer(kv, tickIndex, trackCadence, poseNow);
            });
        }
        finally { _interestSnapshot.Clear(); _inactiveTurretRelays.Clear(); }
    }

    /// <summary>
    /// P603 — build and send ONE peer's <c>HEAVY_SHIPS_UPDATE(176)</c> (the body of
    /// the former inline loop). Runs in serial <c>_peerShips</c> order in default
    /// mode; in parallel mode it runs serially within each sector (one worker per
    /// sector) so the per-ship <c>Deviation</c> staleness accumulator — cross-peer,
    /// order-dependent shared state read back by same-sector peers this tick — is
    /// preserved exactly.
    /// </summary>
    private void BroadcastShipsUpdatePeer(KeyValuePair<ConnectionId, LiveShip> kv, uint tickIndex, bool trackCadence, double poseNow)
    {
        if (_broadcastInterestTiersEnabled && ReuseBroadcastBuffers)
        {
            var self = kv.Value.Ship;
            var sector = BroadcastSectorFor(kv.Key, kv.Value.Sector);
            bool admin = IsSpectatorAdmin(kv.Key) && _spectateSector.ContainsKey(kv.Key);
            // P762 / SIM-0471 — the full native target-type switch, shared by
            // both broadcast paths (FedSrv.CPP:4079-4124).
            ResolveTargetFractions(self, self.CommandTarget, sector,
                out Ship? target, out byte hull, out byte shield);
            BroadcastTierPose(kv.Key, self, target, sector, admin, tickIndex, poseNow, hull, shield);
            return;
        }
        BroadcastShipsUpdatePeerLegacy(kv, tickIndex, trackCadence, poseNow);
    }

    private void BroadcastShipsUpdatePeerLegacy(KeyValuePair<ConnectionId, LiveShip> kv, uint tickIndex, bool trackCadence, double poseNow)
    {
        var self = kv.Value.Ship;
        // P612 — an admin spectator's broadcast serves the sector its free camera
        // is viewing (ANY sector, all ships, side visibility ignored); everyone
        // else keeps their own-sector view.
        bool adminSpectator = HasAdminView(kv.Key);
        var sector = BroadcastSectorFor(kv.Key, kv.Value.Sector);

            // P270 — data-level cloak invisibility: when a ship is actually
            // CLOAKED, an enemy peer that cannot detect it (the P269 cross-side
            // sweep's SeenBySide, driven by the cloaked EffectiveSignature) must
            // not receive its update at all — not a transparent render it could
            // still see. The port's MonoGame client is a PURE renderer with no
            // client-side IGC sim to cull ships, so the server culls on its
            // behalf — the faithful counterpart to native transmitting every
            // cluster ship (SendShipUpdates, FedSrv.CPP:4150-4165) and letting
            // the real client decide visibility in its own local sim. Same-side
            // ships are always seen (ResolveCrossSideVisibility's own-side rule),
            // so a cloaked TEAMMATE still reaches its side (rendered transparent);
            // a fully-cloaked enemy collapses its detection radius and drops out
            // of this peer's update. NON-cloaked ships are deliberately
            // UNCHANGED (always sent) — broadening cross-side fog-of-war to all
            // ships is a separate concern, out of P270's scope.
            // P612 — an ADMIN spectator sees EVERY ship in the viewed sector,
            // including cloaked/undetected enemies (side visibility ignored).
            bool peerSees(Ship c) => ShipPoseVisible(self, c, adminSpectator);

            // 1. Resolve the peer's command target within its own cluster
            //    (native pmodelTarget->GetCommandTarget(c_cmdCurrent) then the
            //    OT_-switch, FedSrv.CPP:4081-4124).
            // P762 / SIM-0471 — native's per-target-type fraction switch:
            // ship (source ship) hull+shield, station hull+shield, probe/
            // asteroid/missile hull only; 255 everywhere when the target is
            // absent or not in the player's cluster (FedSrv.CPP:4079-4124).
            ResolveTargetFractions(self, self.CommandTarget, sector,
                out Ship? targetShip, out byte bpTargetHull, out byte bpTargetShield);

            // 3. Scalar region: positionReference = own ship position (peer is
            //    in this cluster by construction); fractions = own fractions
            //    (FedSrv.CPP:4044-4047, 4128-4133).
            Vector3 posRef = self.Position;

            // 2. Assemble heavy entries: promoted target first, then candidates
            //    minus self minus target (FedSrv.CPP:4141-4170). P219 added the
            //    deviation/staleness budget below — native FedSrv sorts the
            //    candidates by each ship's accumulated m_deviation and serves a
            //    fixed heavy/light quota per tick. See the budget block for the
            //    exact constants, fast path, and staleness bookkeeping.
            //
            //    The promoted target (below) always occupies the first heavy
            //    slot regardless of its own deviation (FedSrv.CPP:4141-4146) —
            //    P139's target-lock promotion is untouched and simply consumes
            //    one heavy budget slot before the remaining candidates compete.
            var entries = new List<HeavyShipUpdateEntry>();
            // P604 — native mode: the light-tier ships (those the deviation
            // budget would serve at reduced cadence) go in their own LIGHT(175)
            // list instead of being folded into heavy. Port mode never populates
            // this (P219 folds the light tier into heavy).
            List<LightShipUpdateEntry>? lightEntries = null;
            if (targetShip is not null && peerSees(targetShip))
                entries.Add(CaptureHeavyUpdate(tickIndex, posRef, targetShip, tickIndex));

            // P219 — deviation/staleness-based ship-update budget
            // (FedSrv.CPP:3844-3950). The candidate list is exactly the
            // per-cluster selection P138 built (<c>Sector.GetShipUpdateCandidates</c>,
            // which includes the peer's own ship and any target — both excluded
            // here as in native's assembly loop, FedSrv.CPP:4148-4154).
            var all = sector.GetShipUpdateCandidates();
            ViewerPoseCadence? cadence = trackCadence ? _viewerPoseCadence[kv.Key] : null;
            cadence?.Begin(_broadcastInterestTiersEnabled ? all.Where(peerSees).ToList() : all,
                self, sector, adminSpectator || _broadcastInterestTiersEnabled, poseNow,
                TickProfiler.Instance is not null, _broadcastInterestTiersEnabled ? ClassifyInterest : null);

            // Fixed quotas — native's REAL, shipped values (the "w0dk4 Bandwidth
            // Patch", FedSrv.CPP:3844-3845). The disabled bandwidth-adaptive
            // sizing (<c>pfsPlayer->GetBandwidth()</c>, FedSrv.CPP:3841-3843) is
            // DEAD CODE in the reference checkout and never shipped — do NOT
            // reintroduce it (this port has no per-player bandwidth concept).
            const int cHeavyPerInterval = 2;   // FedSrv.CPP:3844
            const int cLightPerInterval = 2;   // FedSrv.CPP:3845

            if (_broadcastInterestTiersEnabled)
            {
                // Classification uses this tick's copied interest snapshot.
                // Only detached shared records leave this projection thread. Native
                // intent: target first (FedSrv.CPP:4141-4146), deviation descending
                // inside each tier (FedSrv.CPP:3614-3621,3923). Rate quotas replace
                // the era's 2+2 cap; no presence-only record satisfies a pose rate.
                // P813a — a far (T1/T2) ship that has drifted past PromotionEpsilon
                // since its last served pose is promoted (big change) and served
                // NOW, so a manoeuvring distant ship is caught up within one tick.
                foreach (var c in all.Where(c => c != self && c != targetShip && peerSees(c))
                    .OrderBy(c => ClassifyInterest(self, c)).ThenByDescending(c => c.Deviation))
                {
                    int tier = ClassifyInterest(self, c);
                    double hz = InterestRate(tier);
                    bool bigChange = cadence!.Promotable(c, tier, _promotionEpsilonSq, poseNow);
                    if (tier == 0 || cadence.TierDue(c.Id, poseNow, hz, _config.UpdatesPerSecond, tickIndex, self.Id) || bigChange)
                    {
                        entries.Add(CaptureHeavyUpdate(tickIndex, posRef, c, tickIndex));
                        c.Deviation = 0;
                        cadence.MarkPosePosition(c.Id, c.Position, c.Velocity, poseNow);
                        cadence.MessagesByTier[tier]++;
                        if (bigChange) cadence.Promotions++;
                    }
                    else c.Deviation += 1;
                }
            }
            else if (all.Count <= 2)
            {
                // Small-candidate fast path (FedSrv.CPP:3860-3864): with <= 2
                // candidates native sends every candidate as a heavy update with
                // no sort and no budget — exactly this port's pre-P219 behavior,
                // so small/typical sessions (a handful of same-sector ships) are
                // unchanged. Everything served here gets its staleness reset
                // (native's unconditional reset pass, FedSrv.CPP:3937-3940).
                foreach (var c in all)
                {
                    if (ReferenceEquals(c, self)) continue;
                    if (!peerSees(c)) continue;   // P270 — invisible enemy: no data
                    if (targetShip is not null && ReferenceEquals(c, targetShip)) continue;
                    entries.Add(CaptureHeavyUpdate(tickIndex, posRef, c, tickIndex));
                    c.Deviation = 0f;   // served → reset
                }
            }
            else
            {
                // Budget path: sort the non-self, non-target candidates by
                // staleness DESCENDING (most-stale first — native CmpShipDeviation,
                // FedSrv.CPP:3614-3621, 3923) and serve the fixed heavy quota PLUS
                // the light quota.
                //
                // P604 — two flavours:
                //  * port mode (default): the light tier is folded into the heavy
                //    list (P219 scope call — no LIGHT wiring then) so every served
                //    ship gets a full pose; total is bounded to heavy+light = 4,
                //    which fixes the O(peers²) cost. Deterministic +1.00 skip bump.
                //  * native mode: the real two-tier split — the top heavy-quota
                //    ships are served as HEAVY(176) (reset deviation to 0), the
                //    next light-quota ships as LIGHT(175) (presence only, no pose;
                //    deviation +0.70 so they rotate back to heavy), the rest
                //    skipped (+1.00). Matches native's deviation bands exactly
                //    (FedSrv.CPP:3937-3950) at native-sized bytes/sec
                //    (cbAvailable = 1600·c_dsecUpdateClient, FedSrv.CPP:3836).
                //    Native's proportional light count, (cShipUpdates*2)/cShips,
                //    FedSrv.CPP:3870, converges to cLightPerInterval=2 at
                //    realistic cluster sizes, so the fixed quota is a faithful,
                //    simpler stand-in (the P219 reasoning, kept here).
                var others = all
                    .Where(c => !ReferenceEquals(c, self)
                             && peerSees(c)                                   // P270 — invisible enemy: no data
                             && (targetShip is null || !ReferenceEquals(c, targetShip)))
                    .OrderByDescending(c => c.Deviation)   // stable sort; native's qsort tie order is not load-bearing
                    .ToList();

                // The promoted target consumes one heavy slot (FedSrv.CPP:4141-4146),
                // so the remaining candidates compete for the rest of the heavy
                // budget plus the light budget.
                //
                // P604b/P632 — density-aware heavy quota (BOTH modes). Native's
                // fixed cHeavyPerInterval=2 assumes the client's LOCAL IGC SIM
                // interpolates every ship every frame between the sparse heavy
                // updates. This port's client is a PURE RENDERER: it has no local
                // sim, so a remote ship's pose is bridged solely by the dead-
                // reckoner's kinematic replay — which is CAPTURED at
                // ExtrapolationCap = 1.5 s (ShipDeadReckoner.cs:64, native's
                // ±1.5 s ProcessShipUpdate window, shipIGC.cpp:3113-3115): beyond
                // it the client HOLDS the pose (a visible freeze). With the fixed
                // budget of 2 heavy/tick, a ship among `others` is re-fed every
                // ~others/2 ticks — at ~195 in-sector ships that is ~4.9 s ≫ 1.5 s,
                // which is the "ships disconnecting / not looking good" freeze on
                // the 221 195v195 staging run. So at density the port's sim
                // genuinely "needs more": the heavy quota is raised so every other
                // ship is re-fed within the dead-reckoner's smooth horizon
                // (≥ 1 heavy per DeadReckonExtrapolationTicks). At typical density
                // (others ≤ 60) this equals native's 2 — byte-identical there; only
                // high density scales.
                //
                // P632 — the density raise must apply to PORT mode too, for
                // CROSS-SIDE (enemy) ships. Port mode's per-tick teammate posBatch
                // covers only SAME-SIDE ships; an enemy's live pose reaches a bot
                // ONLY through this HEAVY budget (there is no enemy posBatch). The
                // pre-P632 port branch hardcoded cHeavyPerInterval=2, so ~195
                // co-located enemies were each re-fed only every ~49 ticks ≫ 30 →
                // enemy poses froze and a 195v195 port fight produced 0 kills
                // (measured at BOTH 9194ddec and e1175e1b — not a range regression;
                // P604b's native-only gate left port-mode enemies starved).
                int targetSlot = targetShip is not null ? 1 : 0;
                int scaledHeavy = Math.Max(cHeavyPerInterval,
                    (int)Math.Ceiling(Math.Max(1, others.Count) / (double)DeadReckonExtrapolationTicks) + targetSlot);
                int othersHeavy = Math.Max(0, scaledHeavy - targetSlot);
                int servedLimit = othersHeavy + cLightPerInterval;

                int i = 0;
                foreach (var c in others)
                {
                    if (i < othersHeavy)
                    {
                        entries.Add(CaptureHeavyUpdate(tickIndex, posRef, c, tickIndex));
                        c.Deviation = 0f;      // heavy-served → reset (FedSrv.CPP:3939-3940)
                    }
                    else if (i < servedLimit)
                    {
                        if (_nativeShipUpdates)
                        {
                            // native light tier: presence-only LIGHT(175) record; bump
                            // +0.70 so it rises back to heavy sooner than a skipped ship
                            // (FedSrv.CPP:3943-3948; deterministic, no live RNG jitter).
                            (lightEntries ??= new()).Add(CaptureLightUpdate(c));
                            c.Deviation += 0.70f;
                        }
                        else
                        {
                            // port mode: the light tier is folded into heavy (P219),
                            // so every served ship gets a full pose and a reset.
                            entries.Add(CaptureHeavyUpdate(tickIndex, posRef, c, tickIndex));
                            c.Deviation = 0f;
                        }
                    }
                    else
                    {
                        c.Deviation += 1.00f;  // skipped → bump to the front next tick
                                               // (FedSrv.CPP:3944-3948)
                    }
                    i++;
                }
            }

            // P659 — preserve ranking, target promotion and deviation exactly.
            // Only HEAVY carries a pose (native igc.h:2423-2442). Append a
            // separate fair sweep; LIGHT presence never resets the pose clock.
            if (cadence is not null)
            {
                foreach (var entry in entries) cadence.MarkPose(entry.ShipId, poseNow);
                if (!_broadcastInterestTiersEnabled) cadence.AppendFloor(entries, tickIndex, posRef, poseNow, _minUpdateHz,
                    Math.Max(1, _config.UpdatesPerSecond), _broadcastSnapshotEnabled, CaptureHeavyUpdate);
            }

            var heavy = new HeavyShipsUpdate
            {
                TimeReference = tickIndex,
                PositionReference = posRef,
                HeavyShipEntries = entries.ToArray(),
                // Native FedSrv.CPP:3783-3794,4049-4071: only fresh active
                // updates relay; stopped gunners travel in the sibling LIGHT.
                ActiveTurretUpdates = CollectActiveTurretRelay(sector, self, tickIndex),
                BpTargetHull = bpTargetHull,
                BpTargetShield = bpTargetShield,
                FractionsHull = Percent(self.Fraction),
                FractionsShield = Percent(self.ShieldFraction),
                FractionsAmmo = 240,
                FractionsFuel = 240,
                FractionsEnergy = 240,
                FractionsOre = Percent(self.EffectiveMineCapacity() <= 0f ? 0f : self.Ore / self.EffectiveMineCapacity()),
            };

            // 4. Per-player send of HEAVY (always — it carries the owner's own
            //    fractions, native FedSrv.CPP:4179-4180). P604 native mode
            //    appends the LIGHT(175) message into the SAME coalesced frame
            //    when the light tier was populated, exactly native's "copy the
            //    light ship update to the end of the heavy ship update"
            //    (FedSrv.CPP:4183-4188) — one packet, one latest-wins frame, so
            //    CoalesceShipsUpdate keeps both halves in lockstep.
            // P606 — the per-peer HEAVY/LIGHT_SHIPS_UPDATE stream is the biggest
            // N² traffic; coalesce per peer (latest-wins), matching native's
            // FM_NOT_GUARANTEED send (FedSrv.CPP:4189-4196).
            var inactiveTurrets = CollectInactiveTurretRelay(sector);
            bool hasLight = lightEntries is { Count: > 0 } || inactiveTurrets.Length > 0;
            BroadcastMessage[] frames = hasLight
                ? new[] { Message(heavy), Message(new LightShipsUpdate
                    { LightShipEntries = lightEntries?.ToArray() ?? [], InactiveTurretShipIds = inactiveTurrets }) }
                : new[] { Message(heavy) };
            var packet = PackBroadcast(frames);
            SendPeerCoalescing(kv.Key, packet, CoalesceShipsUpdate);
            if (cadence is not null) cadence.PacketBytes += HeavyShipsUpdate.FixedSize
                + entries.Count * HeavyShipUpdateEntry.Width + FedPacket.CrcWidth
                + (hasLight
                    ? LightShipsUpdate.FixedSize + (lightEntries?.Count ?? 0) * LightShipUpdateEntry.Size + inactiveTurrets.Length * 2 : 0);
            if (ConsoleLog.Verbose) ConsoleLog.Write($"[ships] peer {kv.Key} side {self.SideId?.ToString() ?? "-"} " +
                              $"target={(targetShip?.Id.ToString() ?? "none")} " +
                              $"=> HEAVY_SHIPS_UPDATE(176) x{entries.Count}" +
                              (lightEntries is { Count: > 0 } ? $" + LIGHT_SHIPS_UPDATE(175) x{lightEntries.Count}" : "") +
                              " [PER PLAYER]");
    }

    /// <summary>
    /// Whether a command target <paramref name="target"/> resides in
    /// <paramref name="sector"/> — the port of native
    /// <c>pmodelTarget->GetCluster() == pcluster</c> (FedSrv.CPP:4082). Because
    /// this port's <see cref="Ship.CommandTarget"/> is a <c>Model?</c>, the only
    /// reachable target types are <see cref="Model"/> subclasses; their sector
    /// back-refs are read where the model tracks one (<see cref="Ship.Sector"/>,
    /// <see cref="Probe.OwnerSector"/>, <see cref="Missile.OwnerSector"/>), and
    /// anything else falls back to membership in the sector's model list.
    /// </summary>
    private static bool TargetInCluster(Model? target, Sector sector)
    {
        if (target is null) return false;
        switch (target)
        {
            case Ship ship: return ship.Sector == sector;
            case Probe probe: return probe.OwnerSector == sector;
            case Missile missile: return missile.OwnerSector == sector;
            // P762 — a station/asteroid/warp command target is an
            // EquipmentTarget wrapper (the port's ImodelIGC for static bodies)
            // carrying its owning sector.
            case EquipmentTarget equipment: return equipment.OwnerSector == sector;
            default: return sector.Models.Contains(target);
        }
    }

    /// <summary>
    /// P762 / SIM-0471 — the native per-target-type target-fraction switch
    /// (<c>SendShipUpdates</c>, <c>FedSrv/FedSrv.CPP:4079-4124</c>).
    /// <c>bpTargetHull</c> defaults to 255 ("unknown / no target"); only a
    /// command target <b>in the player's own cluster</b> contributes:
    /// <list type="bullet">
    /// <item><b>OT_ship</b> — the target's <c>GetSourceShip()</c> (root parent);
    /// skip when that source is the player itself. Hull always, shield only
    /// when the source has a mounted shield part (<c>GetMountedPart(ET_Shield,
    /// 0)</c> non-null). The source ship fills the first heavy slot.</item>
    /// <item><b>OT_station</b> — hull + shield fractions.</item>
    /// <item><b>OT_probe / OT_asteroid / OT_missile</b> — hull (damage) fraction
    /// only, shield stays 255.</item>
    /// <item>Anything else (warp, mine, no target) — both stay 255.</item>
    /// </list>
    /// Stations/asteroids arrive as <see cref="EquipmentTarget"/> wrappers in
    /// this port (static bodies are not <see cref="Model"/> subclasses); the
    /// wrapper exposes the concrete body and its sector.
    /// </summary>
    private static void ResolveTargetFractions(Ship self, Model? target, Sector sector,
        out Ship? targetShip, out byte hull, out byte shield)
    {
        targetShip = null;
        hull = 255;     // FedSrv.CPP:4079 bpTargetHull.SetChar(255)
        shield = 255;
        if (target is null || !TargetInCluster(target, sector)) return;

        switch (target)
        {
            case Ship ship:
            {
                // FedSrv.CPP:4086-4104: GetSourceShip() collapses a turret/child
                // to its root ship; a player targeting its own source ship is
                // skipped (no heavy slot, both bytes stay 255).
                Ship source = ship;
                while (source.ParentShip is { } parent) source = parent;
                if (ReferenceEquals(source, self)) return;
                targetShip = source;
                hull = Percent(source.Fraction);
                if (source.HasMountedShield) shield = Percent(source.ShieldFraction);
                break;
            }
            case EquipmentTarget { Body: Station station }:
                hull = Percent(station.ArmorFraction);      // CstationIGC::GetFraction
                shield = Percent(station.ShieldFraction);
                break;
            case EquipmentTarget { Body: Asteroid asteroid }:
                hull = Percent(asteroid.Fraction);          // CasteroidIGC::GetFraction
                break;
            case Probe probe:
                hull = Percent(probe.Fraction);
                break;
            case Missile missile:
                hull = Percent(missile.Fraction);
                break;
        }
    }

    /// <summary>Fraction 0..1 → div-240 byte, matching native
    /// <c>BytePercentage</c> (Utility.h:1307, 1342-1358) exactly — the same
    /// conversion the port's codecs bake in (<c>CompactQuant.Percent</c>,
    /// inaccessible from this assembly). 255 is the reserved "no target"
    /// sentinel and is never produced here (callers pass the raw 255 when
    /// unpresent).</summary>
    private static byte Percent(float f)
    {
        double v = (double)f * 240.0 + 0.5;
        if (v < 0.0) return 0;
        if (v >= 241.0) return 240;
        return (byte)v;
    }

    /// <summary>
    /// P230 — per-tick, owner-only <c>MISSILE_LOCK_UPDATE(204)</c> of each armed
    /// peer's missile-lock quality + target, the wire half of the lock-feedback
    /// architecture decision documented in <c>docs/MISSILE_LOCK_FEEDBACK_P230.md</c>.
    /// Native's client ran its own local IGC simulation, so it read
    /// <c>magazine-&gt;GetLock()</c> directly with no wire message; this port's
    /// MonoGame client is a pure renderer with no static core, so the server must
    /// expose the P226-computed <see cref="Ship.MissileLock"/> to the owner. The
    /// message goes ONLY to the owning player (each peer receives its own lock),
    /// the same per-owner convention as <see cref="BroadcastShipStates"/>'s
    /// own-ship SINGLE_SHIP_UPDATE. Only a ship that actually carries a mounted
    /// missile launcher with ammo generates a send (without one MissileLock is
    /// always 0 and there is no feedback to render) — the same predicate
    /// <c>Ship.UpdateMountedMissileLock</c>/<c>TryFireMissile</c> use, so armed
    /// ships (the interesting case) stream every tick while unarmed ships (the
    /// norm) produce no traffic.
    /// </summary>
    private void BroadcastMissileLock()
    {
        foreach (var kv in _peerShips)
        {
            var ship = kv.Value.Ship;
            var part = ship.Parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0);
            if (part is not { Type.Type: DataPartTypeIGC.MissileLauncher ml }) continue;
            if (ship.Mission is not { } mission) continue;
            if (!mission.Core.MissileTypes.TryGetValue(ml.ExpendableTypeId, out var missileType)) continue;

            var target = ship.CommandTarget;
            short targetShipId = target?.Id ?? -1;
            Vector3 targetPos = target?.Position ?? Vector3.Zero;
            bool ready = part.Amount > 0 && part.MountedFraction >= 1f && mission.LastUpdate > part.TimeLoaded;
            var msg = MissileLockUpdate.From(ship.Id, targetShipId, ship.MissileLock, targetPos,
                missileType.ModelName, target is null ? IgcObjectType.Ship : EquipmentTarget.TypeOf(target), ready);
            SendPeerCoalescing(kv.Key, PackBroadcast(Message(msg)), CoalesceMissileLock);
            ConsoleLog.Write($"[missilelock] peer {kv.Key} ship {ship.Id} target {(targetShipId == -1 ? "none" : targetShipId.ToString())} lock {ship.MissileLock:F3} \"{missileType.ModelName}\" -> MISSILE_LOCK_UPDATE(204)");
        }
    }

    /// <summary>
    /// P264 — per-tick, OWNER-ONLY incoming-missile warning stream: for each
    /// seated peer, tell it whether an in-sector ENEMY missile's target is its
    /// own ship, and the best lock fraction among such missiles. This is the
    /// port of native's client-side warning check (<c>WinTrek/trekigc.cpp:5025-5071</c>),
    /// which walked the player's cluster and armed <c>missileLockSound</c> for any
    /// <c>OT_missile</c> whose <c>GetTarget() == trekClient.GetShip()->GetSourceShip()</c>,
    /// computing <c>fBestLock = max(GetLock())</c> for the pitch ramp
    /// (<c>SetPitch(0.75 + fBestLock/2)</c>, <c>trekigc.cpp:5065</c>).
    /// <para/>
    /// This port's MonoGame client is a pure renderer with no static core, and
    /// the missile wire it receives (<c>FIRE_MISSILE(70)</c>, <c>MISSILE_UPDATE(205)</c>,
    /// <c>MISSILE_DESTROYED(83)</c>) carries position/heading but never a target
    /// — so it cannot derive "this missile's target is me". The server holds the
    /// real in-flight <see cref="Missile"/> objects (with their real
    /// <see cref="Missile.Target"/> / <see cref="Missile.LockFraction"/>), so it
    /// computes the warning and streams it to the TARGET's owner only (the same
    /// owner-only per-tick convention as <see cref="BroadcastMissileLock"/> and
    /// <see cref="BroadcastOwnShipMining"/>). Because the target is a ship in the
    /// same sector (homing drops a missile's target when it leaves the sector,
    /// <c>Missile.OnUpdate</c>), iterating the target's sector models for missiles
    /// targeting it reproduces native's in-cluster walk.
    /// </summary>
    private void BroadcastMissileTrackWarning()
    {
        foreach (var kv in _peerShips)
        {
            var ship = kv.Value.Ship;
            // HUD-10 — native compares the missile's target with
            // trekClient.GetShip()->GetSourceShip() (trekigc.cpp:5047-5050), so a
            // gunner's warning follows the CARRIER (the parent ship), not the
            // child turret. GetSourceShip() returns m_pshipParent ?: this
            // (Igc/shipIGC.h:1481-1484).
            var source = ship.ParentShip ?? ship;
            bool tracked = false;
            float bestLock = 0f;

            // Native arms only from missiles in the PLAYER's cluster
            // (pcluster = GetCluster(), trekigc.cpp:5028-5050). The port's
            // cluster == sector; the source ship's sector is the cluster the
            // gunner is watching. A missile homing on the source is in it.
            if (source.Sector is { } sector)
            {
                for (int i = 0; i < sector.Models.Count; i++)
                {
                    var model = sector.Models[i];
                    if (model is Missile mis && mis.Target == source)
                    {
                        tracked = true;
                        if (mis.LockFraction > bestLock)
                            bestLock = mis.LockFraction;
                    }
                }
            }

            SendPeerCoalescing(kv.Key, PackMissileTrack(tracked, bestLock), CoalesceMissileTrack);
            if (tracked)
                ConsoleLog.Write($"[missiletrack] peer {kv.Key} ship {ship.Id} (source {source.Id}) TRACKED by in-sector missile(s) bestLock {bestLock:F3} -> MISSILE_TRACK_WARNING(208)");
        }
    }

    /// <summary>
    /// P255 — per-tick, OWNER-ONLY mining-target stream: for each seated peer,
    /// send <c>OwnShipMiningUpdate(206)</c> naming the rock its own ship is
    /// currently drilling (per-tick <c>Ship.MiningAsteroid</c>, the same signal
    /// that stamps the faithful <c>miningMaskIGC</c> bit into its
    /// <c>SingleShipUpdate(78)</c> — see <see cref="BroadcastShipStates"/>), or
    /// an NA/NA not-mining message when it is not mining. Sent every tick (like
    /// <see cref="BroadcastMissileLock"/>), so start/stop/rock-change all reach
    /// the client without change-tracking, at a cost of 8 bytes per peer per
    /// tick. The renderer-only MonoGame client uses the rock id to orient its
    /// own hull toward the mine target (native infers that locally and never
    /// transmits it — see <c>Wire.OwnShipMiningUpdate</c> for the A-vs-B).
    /// </summary>
    private void BroadcastOwnShipMining()
    {
        foreach (var kv in _peerShips)
        {
            var ship = kv.Value.Ship;
            SendPeerCoalescing(kv.Key, PackMining(ship.MiningAsteroid), CoalesceOwnMining);
        }
    }

    /// <summary>
    /// HUD-12 — per-tick, OWNER-ONLY cloak stream: for each seated peer, send
    /// <c>OwnShipCloakUpdate(220)</c> with its own ship's live
    /// <see cref="Ship.Cloaking"/> (the native <c>GetCloaking()</c> visible
    /// fraction, <c>Igc/cloakigc.cpp:121</c>). Sent every tick, always present —
    /// no change-tracking (8 bytes per peer per tick); the client derives the
    /// native strength percentage and <c>IsCloaked</c> from it
    /// (<c>WinTrek/consoledata.cpp:480-489,563-575</c>).
    /// </summary>
    private void BroadcastOwnShipCloak()
    {
        foreach (var kv in _peerShips)
        {
            SendPeerCoalescing(kv.Key, PackCloak(kv.Value.Ship), CoalesceOwnCloak);
        }
    }

    /// <summary>HUD-12b — the cloak fraction the owner's HUD reads. Native
    /// <c>ModelData::GetCloaking()</c> uses <c>GetShip()</c>, which is
    /// <c>GetSourceShip()</c> (<c>WinTrek/consoledata.h:144-146</c>), and its
    /// visibility gate accepts the own ship's parent as well
    /// (<c>consoledata.cpp:480-489</c>). A turret gunner's own ship is the child,
    /// so the ramp that matters is the parent carrier's.</summary>
    internal static float OwnShipCloaking(Ship ship) => ship.ParentShip?.Cloaking ?? ship.Cloaking;

    /// <summary>Per-viewer missile tracking in the viewed sector. Tracking can
    /// hydrate a late viewer, so it orders the source identity/pose first and
    /// records the viewer for guaranteed destruction cleanup.</summary>
    private void BroadcastMissilesUpdate()
    {
        // Gather once: do not rescan every ship/model for every viewer, or
        // allocate per-viewer record lists in sectors with no missiles.
        var bySector = new Dictionary<Sector, List<Missile>>();
        foreach (var mission in _missions)
            foreach (var sector in mission.Sectors)
                foreach (var model in sector.Models)
                    if (model is Missile missile)
                    {
                        if (!bySector.TryGetValue(sector, out var list)) bySector[sector] = list = new();
                        list.Add(missile);
                    }
        // A late viewer can hydrate a missile from tracking alone. Give that
        // path the same reliable source dependency as a launch, per recipient.
        foreach (var peer in _peerShips)
        {
            var sector = BroadcastSectorFor(peer.Key, peer.Value.Sector);
            if (!bySector.TryGetValue(sector, out var missiles)) continue;
            var records = new List<MissileStateRecord>(missiles.Count);
            foreach (var missile in missiles)
            {
                if (missile.Launcher is not Ship source) continue;
                bool known = _missileViewers.TryGetValue(missile, out var viewers) && viewers.Contains(peer.Key);
                if (EmissionVisible(peer.Key, source, sector))
                {
                    if (source.Fraction > 0 && source.StationId is null && source.ParentShip is null)
                        EnsureEmissionPose(peer.Key, source, sector);
                    else if (!known) continue;
                }
                else if (!known) continue;
                // Once launched, an already-known missile remains an independent
                // object if the launcher cloaks, dies or leaves this sector.
                if (viewers is null) _missileViewers[missile] = viewers = new();
                viewers.Add(peer.Key);
                records.Add(new(missile.Id, missile.MissileType.ExpendableTypeId,
                    sector.Id, missile.Position, missile.Forward));
            }
            if (records.Count == 0) continue;
            var packet = PackBroadcast(Message(new MissileUpdate { Records = records.ToArray() }));
            SendPeerCoalescing(peer.Key, packet, CoalesceMissiles);
        }
    }

    /// <summary>
    /// P374 — per-side, change-driven sector-threat (ClusterWarning) sync. For
    /// each side with seated peers, recompute every sector's friendly/enemy
    /// asset mask from data this server already tracks (the sector's physical
    /// <see cref="Ship"/> models' PilotType/hull capabilities + its runtime
    /// <see cref="Station"/> sides) and send a <see cref="ClusterWarningUpdate"/>
    /// to that side's members only when that side's view of the sector changed
    /// — a warning-level change and/or a sector-secured/lost station transition.
    /// This is the server-authoritative port of native's client-side
    /// <c>ClusterSiteImpl::UpdateClusterWarnings</c> (trekigc.cpp:327-383), which
    /// this renderer-only client cannot reproduce (no static core, no cross-
    /// sector enemy knowledge). Runs on the sim thread after <c>Mission.Update</c>
    /// so the mask reflects this tick's membership.
    /// </summary>
    private void BroadcastSectorThreats()
    {
        // Audience = the sides with seated peers (same as the per-side object
        // syncs). Group connections by their ship's side in one pass over a
        // stable snapshot.
        var connsBySide = GatherSideConnections();
        if (connsBySide.Count == 0) return;

        // Drop per-side state for sides that no longer have a seated peer, so a
        // re-populated side re-learns the current threats from a clean slate
        // (otherwise a threat that predates the reconnect would never be reported).
        foreach (var sid in _lastSectorAssetMaskBySide.Keys.Where(s => !connsBySide.ContainsKey(s)).ToList())
            _lastSectorAssetMaskBySide.Remove(sid);

        foreach (var (sideId, conns) in connsBySide)
        {
            if (!_lastSectorAssetMaskBySide.TryGetValue(sideId, out var perSector))
                _lastSectorAssetMaskBySide[sideId] = perSector = new Dictionary<short, short>();

            foreach (var m in _missions)
                foreach (var sector in m.Sectors)
                {
                    short am = ComputeSectorAssetMask(sector, sideId);
                    short last = perSector.TryGetValue(sector.Id, out var l) ? l : (short)0;
                    if (last == am) continue;          // nothing changed this tick

                    bool hasF = (am & ClusterWarnings.AmStation) != 0;
                    bool hasE = (am & ClusterWarnings.AmEnemyStation) != 0;
                    bool lastF = (last & ClusterWarnings.AmStation) != 0;
                    bool lastE = (last & ClusterWarnings.AmEnemyStation) != 0;
                    bool secured = (hasF && !hasE) && !(lastF && !lastE);   // gained a friendly station, none enemy
                    bool lost = !hasF && lastF;                             // lost its last friendly station

                    var msg = ClusterWarningUpdate.From(
                        sector.Id,
                        ClusterWarnings.GetWarning(am, bInvulnerableStations: false),
                        secured, lost);
                    var packet = PackBroadcast(Message(msg));
                    foreach (var t in conns) SendPeer(t, packet);

                    ConsoleLog.Write($"[clusterWarn] side {sideId} '{sector.Name}' -> {ClusterWarnings.GetWarningText(msg.Warning)}" +
                                     (secured ? " (SECURED)" : "") + (lost ? " (LOST)" : ""));

                    perSector[sector.Id] = am;
                }
        }
    }

    /// <summary>
    /// P374 — the friendly/enemy asset mask of one sector from one side's
    /// perspective. Friendly = same side (this port has no alliance concept, so
    /// the allied leg collapses to own-side, the established simplification).
    /// Only physically-present, flying, unboarded ships count (native MoveShip
    /// gates on <c>ss.GetState() == c_ssFlying</c> and skips unknown ships), and
    /// only non-dead runtime stations contribute their ownership bit.
    /// </summary>
    private static short ComputeSectorAssetMask(Sector sector, short sideId)
    {
        short am = 0;
        foreach (var model in sector.Models)
        {
            if (model is not Ship ship) continue;
            if (ship.Dead || ship.StationId is not null || ship.ParentShip is not null) continue;
            am |= ClusterWarnings.GetAssetMask(ship, ship.SideId == sideId);
        }
        foreach (var st in sector.StationRuntimes)
        {
            if (st.Dead) continue;
            am |= st.SideId == sideId ? ClusterWarnings.AmStation : ClusterWarnings.AmEnemyStation;
        }
        return am;
    }
}
