using System.Diagnostics;
using System.Runtime.InteropServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
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
    /// <summary>Pump transport, advance sim by exactly one period, tick every mission.</summary>
    private void SimPass()
    {
        // P314 — every stage runs inside its own exception barrier (Step): a
        // bad tick degrades gracefully (log + skip that stage) instead of
        // propagating to the top-level catch and killing the server. Stages are
        // independent; one failure never cascades into its siblings.
        Step("drain", () => _host.Drain(HandleTransportEvent));
        lock (_statsLock) _simNow += _period;
        double now;
        lock (_statsLock) now = _simNow;
        Step("update", () => { foreach (var m in _missions) m.Update(now); });
        // P188 — advance any STAGE_STARTING start-countdowns: a mission whose
        // recorded target start time has passed crosses into STAGE_STARTED and
        // broadcasts ENTER_GAME (the deferred form of P180's auto-start /
        // START_GAME's manual trigger). Runs right after the mission tick so the
        // transition lands the same tick its deadline elapses.
        Step("startCountdowns", () => AdvanceStartCountdowns(now));
        // P281 — the side-select drain: seat ready/timed-out pending peers in
        // logon order (deterministic), incl. the auto-assign timeout.
        Step("sideSelection", () => AdvanceSideSelection(now));
        // P187 — resolve pending ballots this tick (the port of native
        // CFSMission::Update's ballot loop, fsmission.cpp:2585-2592): any
        // ballot whose expiration passed or whose votes are all in is tallied
        // and resolved (pass/fail), its type effect applied, then removed.
        // Runs on the sim thread right after the mission tick so a ballot
        // created during this tick's transport drain is resolved this same
        // tick if it already has every vote (e.g. an initiator-only side).
        Step("ballots", () => { foreach (var m in _missions) ResolveBallots(m, now); });
        lock (_statsLock) { _loopIterations++; _liveShipCount = _peerShips.Count; }
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
                            SendRipcordSectorWide(ripSec.Id,
                                new RipcordAborted { ShipidRipcord = rip.Id }.Encode());
        });
        // P113 — perform the ripcord teleports whose countdowns reached zero
        // this tick (Sector.ResolveRipcordCountdowns). Server.PerformRipcordTeleport
        // is the port of UseRipcord (FedSrv.CPP:13440-13500): it relocates the
        // ripper (station/ship anchor), pays the energy cost, broadcasts
        // RIPCORD_ABORTED on energy-insufficiency, and clears the ripcord state.
        // Runs on the sim thread before the broadcast below so the position
        // delta is picked up by the same tick's SINGLE_SHIP_UPDATE.
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
        // P117 — perform the real relaunch this tick's carrier-landing pass
        // found (Sector.ResolveCarrierLandings). Server.PerformRelaunch is the
        // port of LandOnCarrierEvent (FedSrv.CPP:12808-13031): restore the
        // lander's ship-level energy/ammo pools, reposition it at a real
        // launch slot (or the fallback backward offset when the carrier has no
        // launch frames), and broadcast RELAUNCH_SHIP(192) sector-wide. Runs
        // on the sim thread before the broadcast below so the lander's new
        // position/attitude is picked up by the same tick's
        // SINGLE_SHIP_UPDATE.
        Step("carrierRelaunches", () =>
        {
            foreach (var m in _missions)
                foreach (var sector in m.Sectors)
                    foreach (var (carrier, lander) in sector.DrainCarrierLandings())
                        PerformRelaunch(lander, carrier);
        });
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
                        BroadcastBuildingEffectDestroyed(astId);
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
        Step("crossSideExportSweep", SweepCrossSideExports);
        // Step 3: after the mission tick, broadcast every peer ship that moved.
        // Runs on the sim thread, so "only Mission.Update moves ships" holds —
        // this is purely an observation send of the authoritative state.
        // P314: the concretely-reachable crash lived HERE — a teammate's
        // ExportShip.Encode() throws past 255 parts, and BroadcastShipStates
        // used to let that escape and kill the server. The barrier + the
        // MountPart part-count cap (Ship.cs) close it.
        Step("broadcastShipStates", () => BroadcastShipStates((uint)TickIndex));
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
        Step("broadcastShipsUpdate", () => BroadcastShipsUpdate((uint)TickIndex));
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
        // P298 — per-tick, OWNER-ONLY OwnShipKillBonusUpdate(212) carrying the
        // peer's own ship's live Kill Bonus (P278 readout) so the renderer-only
        // MonoGame client can draw a real KB gauge. Native replicated this as
        // fExperience in PLAYERINFO(10) to a local ship replica; this client has
        // no replica, so the server streams it — see OwnShipKillBonusUpdate's
        // doc for the P230/P255/P266 owner-only-extension precedent.
        Step("broadcastOwnShipKillBonus", () => BroadcastOwnShipKillBonus());
        // P264 — per-tick, OWNER-ONLY MissileTrackWarningUpdate(208) telling each
        // peer whether an in-sector ENEMY missile's target is its own ship (plus
        // the best lock among them), so the MonoGame client can play the incoming-
        // missile warning loop. Native's client walked its own IGC sim to arm it
        // (trekigc.cpp:5025-5071); this renderer-only client has no static core
        // and the missile wire (70/205/83) carries no target, so the server flags
        // "this missile's target is me" explicitly (see the codec doc for the
        // A-vs-B decision).
        Step("broadcastMissileTrackWarning", () => BroadcastMissileTrackWarning());
        // P265 — periodic, owner-only ScoreRosterUpdate(209) of the real
        // in-mission player roster (name/side/kills/deaths from P193 PlayerScore),
        // throttled to ~2 s so the MonoGame client's scoreboard overlay shows real
        // live-updated numbers (see BroadcastScoreRoster).
        Step("broadcastScoreRoster", () => BroadcastScoreRoster());
        // P266 — per-tick, OWNER-ONLY LeadIndicatorUpdate(210) of the local
        // ship's predictive lead-aim point (where to fire to hit a selected moving
        // target), computed from the real solveForLead physics (Ship.SolveLead) and
        // gated by the c_habmLeadIndicator hull capability / relay scanner
        // exception. Native's client solved this locally (indicator.cpp:199-360,
        // common.cpp:121-149); this renderer-only client has no static core, so
        // the server computes and streams it (see BroadcastLeadIndicator).
        Step("broadcastLeadIndicator", () => BroadcastLeadIndicator());
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
    /// per-player status send (<c>fsmission.cpp:4303-4323</c>).</summary>
    private void BroadcastShipStates(uint tickIndex)
    {
        // One snapshot reused for both the own-ship walk and the (P58)
        // same-side teammate walk below, so neither mutates mid-iteration.
        var peers = _peerShips.ToArray();
        // P317 — group peers by side once per tick so each recipient's
        // same-side teammate walk scans only its own side instead of every
        // peer (the old `foreach tv in peers { if different side continue; }`
        // was an O(P²) filtering scan). Members of each list keep `peers`
        // order, so the effective per-side subject order is unchanged.
        var sameSide = new Dictionary<short, List<KeyValuePair<ConnectionId, LiveShip>>>();
        foreach (var gkv in peers)
        {
            if (gkv.Value.Ship.SideId is { } s)
            {
                if (!sameSide.TryGetValue(s, out var list)) { list = new(); sameSide[s] = list; }
                list.Add(gkv);
            }
        }
        // P317 — one encoded SingleShipUpdate per MOVING subject per tick,
        // shared across every same-side recipient (the bytes for subject A's
        // position are identical for all of A's teammates, so re-encoding per
        // (recipient, subject) pair was pure waste — O(P²) allocations down to
        // O(P)). Lazily created; freed next tick.
        Dictionary<short, byte[]>? positionEncode = null;
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
                || miningNow != live.LastBroadcastMining)
            {
                var msg = new SingleShipUpdate
                {
                    OtTarget = 0,                          // OT_ship
                    OidTarget = ship.Id,
                    Ship = ServerShipUpdate.FromModel(ship.Id, ship, tickIndex, miningNow ? ShipControlState.MiningMask : 0),
                    BIsRipcording = false,
                };
                SendPeer(kv.Key, FedPacket.Pack(new[] { msg.Encode() }));
                _peerShips[kv.Key] = live with { LastBroadcastPos = ship.Position, LastBroadcastForward = fwd, LastBroadcastUp = up, LastBroadcastMining = miningNow, PositionEverBroadcast = true };
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
            if (sectorChanged || refreshDue)
            {
                SendPeer(kv.Key, FedPacket.Pack(new[] { MakeOwnShipStatus(ship.Id, live).Encode() }));
                ConsoleLog.Write($"[shipstatus] peer {kv.Key} " +
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
                    SendPeer(kv.Key, FedPacket.Pack(new[] { bytes }));
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
                // P317 — batch every dirty teammate SingleShipUpdate for this
                // recipient into ONE FedPacket at the end of the walk (instead
                // of one packet per (recipient, subject) pair per tick, which
                // at 150 same-side ships is ~22k packets/tick). Lazily created
                // so a recipient with no dirty mates allocates nothing.
                List<byte[]>? posBatch = null;
                foreach (var tv in mates)
                {
                    if (ReferenceEquals(tv.Value.Ship, ship)) continue;   // own ship, handled above
                    short teammateId = tv.Value.Ship.Id;

                    // P62: one-shot real ExportShip(26) about this teammate's
                    // ship, sent to the recipient BEFORE any SHIP_STATUS about
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
                        SendPeer(kv.Key, FedPacket.Pack(new[] { teamExport.Encode() }));
                        Console.WriteLine($"[teamexport] peer {kv.Key} <- ship {teammateId} '{peerName}' hull {teamExport.HullId} nParts {initialParts.Count}");
                        _teammateExportSent.Add((kv.Key, teammateId));
                        _teammateLoadoutVersionSent[(kv.Key, teammateId)] = tv.Value.Ship.LoadoutVersion;
                    }

                    // P63: one-shot real PLAYERINFO(10) about this same teammate's
                    // ship — sent immediately after that pair's EXPORT (P62) and
                    // BEFORE any SHIP_STATUS about the same subject, extending P62's
                    // gating chain rather than building a competing one. Rationale
                    // (native source, not guessed): a ship's "private data" — the
                    // pointer IshipIGCWrapper::GetPlayerInfo()
                    // (AllegianceInterop/igcWrapper.cpp:1980-1982) dereferences — is
                    // set in EXACTLY one place, the FM_S_PLAYERINFO(10) handler's
                    // pPlayerInfo->SetShip(pship) (appmsg.cpp:2279), via
                    // pship->SetPrivateData((DWORD)this) (clintlib.h:311-314). Without
                    // this message a same-side ship gets an EXPORT object (P62) but
                    // never a PlayerInfo, so GetPlayerInfo() returns a wrapper built
                    // around a null/garbage native pointer, and
                    // UndockMinersAndStartMining's p.GetPlayerInfo().LastSeenState()
                    // (CommanderResearchAndExpand.cs:1558) dereferences it: an
                    // AccessViolationException, silently swallowed by OnAppMessage's
                    // blanket SEH handler (NativeClient.cpp:29-45) — no C# exception,
                    // pump keeps working, diagnostic never prints.
                    //
                    // Ordering: placed after the EXPORT (P62) above and before the
                    // STATUS below. When PLAYERINFO's handler runs, CreateObject
                    // (appmsg.cpp:2264) FAILS because the id exists (from EXPORT) and
                    // it takes the else branch — GetShip(shipID) (appmsg.cpp:2276),
                    // resolving the REAL ship EXPORT created, and calling
                    // SetShip on it (:2279). If PLAYERINFO came first it would create
                    // its own separate throwaway ship and attach to THAT — silently
                    // wrong, hence worth getting right, not something a test catches
                    // by accident. Fields match the existing own-player send
                    // (Server.cs:946-954) exactly except ShipId = the teammate's real
                    // id (NOT the -1/NA own-ship trick — that NA is a deliberate,
                    // documented dummy adoption specific to the self-referential own
                    // ship; there is no dummy for a teammate, so a real, separate
                    // object must be created). Money stays 0 (codec docstring: the
                    // native client asserts GetMoney()==0 in AddPlayerToSide,
                    // clintlib.cpp:4109). ISide = the teammate's own SideId (==
                    // recipient's, same-side only); CharacterName from `_peerNames`.
                    if (_teammateExportSent.Contains((kv.Key, teammateId)) &&
                        !_teammatePlayerInfoSent.Contains((kv.Key, teammateId)) &&
                        _peerNames.TryGetValue(tv.Key, out var teammateName))
                    {
                        var teamInfo = new PlayerInfo
                        {
                            CharacterName = teammateName,
                            Money = 0,                       // flat 0 — satisfies AddPlayerToSide's assert
                            ISide = recipientSide,           // the teammate's own side (== recipient's, same-side only)
                            ShipId = teammateId,             // REAL id — the ship the EXPORT created
                            PilotType = PlayerInfo.PilotTypePlayer,
                        };
                        SendPeer(kv.Key, FedPacket.Pack(new[] { teamInfo.Encode() }));
                        Console.WriteLine($"[teamplayerinfo] peer {kv.Key} <- ship {teammateId} '{teammateName}' side {recipientSide}");
                        _teammatePlayerInfoSent.Add((kv.Key, teammateId));
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
                        long lastTeammateTick = _lastTeammateStatusTick.TryGetValue((kv.Key, teammateId), out var tt0) ? tt0 : -1;
                        if (lastTeammateTick < 0 || (long)tickIndex - lastTeammateTick >= TeammateStatusRefreshTicks)
                        {
                            SendPeer(kv.Key, FedPacket.Pack(new[] { MakeOwnShipStatus(teammateId, tv.Value).Encode() }));
                            ConsoleLog.Write($"[teamshipstatus] peer {kv.Key} <- ship {teammateId} sector {tv.Value.Sector.Id}" +
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
                                    Ship = ServerShipUpdate.FromModel(teammateId, tv.Value.Ship, tickIndex),
                                    BIsRipcording = false,
                                };
                                enc = teamMove.Encode();
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
                            SendPeer(kv.Key, FedPacket.Pack(new[] { loadoutExport.Encode() }));
                            Console.WriteLine($"[teamloadout] peer {kv.Key} <- ship {teammateId} loadout changed, nParts {currentParts.Count}");
                            _teammateLoadoutVersionSent[(kv.Key, teammateId)] = currentVersion;
                        }
                    }
                }
                if (posBatch is { Count: > 0 })
                {
                    if (posBatch.Count <= FedPacket.MaxMessagesPerPacket)
                        SendPeer(kv.Key, FedPacket.Pack(posBatch));
                    else // defensive: a >256-message packet would be rejected by this port's Unpack cap
                        foreach (var chunk in posBatch.Chunk(FedPacket.MaxMessagesPerPacket))
                            SendPeer(kv.Key, FedPacket.Pack(chunk));
                }
            }
        }
    }

    /// <summary>Build a peer's own-ship SHIP_STATUS(90) from its live record:
    /// docked (carrying the real StationId + State=c_ssDocked) when the ship is
    /// docked, else flying free with StationID/ParentID NA; detected/unknown
    /// fixed per the simplification documented at the join-burst send site.
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
                Detected = true,
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
            Unknown = false,                 // no scanning system yet
            Detected = true,                 // no scanning system yet
            StateTime = 0,                   // sim clock unused (no dTime source here)
        };
    }

    /// <summary>The teammate ExportShip PartData[] tail as it is right now,
    /// mount order — see <see cref="PartData"/>'s own doc comment for why this
    /// drops <c>MountedPart.FractionMounted</c> (a requester-only, BUY_LOADOUT-
    /// specific concern; an already-mounted part observed via EXPORT has none).</summary>
    private static IReadOnlyList<PartData> SnapshotParts(Ship ship) =>
        ship.Parts.Select(p => new PartData(p.PartId, p.Amount, p.MountId)).ToArray();

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
        // Build the per-side peer audience once per tick: the seeing side's
        // sensor net is shared, so every seated peer on a seeing side is a
        // candidate recipient for each ship that side sees.
        var peers = _peerShips.ToArray();
        Dictionary<short, List<ConnectionId>>? peersBySide = null;
        foreach (var kv in peers)
        {
            if (kv.Value.Ship.SideId is not { } s) continue;   // unseated (team-lobby) peers see nothing cross-side
            peersBySide ??= new();
            if (!peersBySide.TryGetValue(s, out var list)) { list = new(); peersBySide[s] = list; }
            list.Add(kv.Key);
        }
        if (peersBySide is null) return;   // nobody seated on a real side

        foreach (var m in _missions)
        {
            // The P132 visibility sweep only runs once the mission is
            // Started/Starting (Mission.Update early-returns otherwise), so the
            // SeenBySide flags we read are meaningful only then. Defensive gate,
            // same precondition the cross-side machinery has always assumed.
            if (m.Stage != MissionStage.Started && m.Stage != MissionStage.Starting) continue;

            foreach (var sector in m.Sectors)
                foreach (var model in sector.Models)
                {
                    if (model is not Ship ship) continue;
                    short? ownSide = ship.SideId;
                    foreach (var (sideId, conns) in peersBySide)
                    {
                        if (ownSide == sideId) continue;          // own side — the same-side [teamexport] path
                        if (!ship.SeenBySide(sideId)) continue;   // not visible to this side's sensor net
                        foreach (var conn in conns)
                            if (_crossSideExportSent.Add((conn, ship.Id)))
                                SendCrossSideExport(conn, ship, sideId);
                    }
                }
        }
    }

    /// <summary>P349 — send ONE cross-side <c>EXPORT(26)</c> about
    /// <paramref name="ship"/> to a single recipient <paramref name="conn"/> on
    /// the seeing side <paramref name="seeingSide"/>. Caller has already latched
    /// <c>(conn, ship.Id)</c> in <c>_crossSideExportSent</c>.</summary>
    private void SendCrossSideExport(ConnectionId conn, Ship ship, short seeingSide)
    {
        string name = PeerName(ship) ?? $"Ship {ship.Id}";
        var exp = new ExportShip
        {
            ObjectType = ExportShip.OtShip,
            CharacterName = name,
            HullId = ship.HullType.HullId,
            ShipId = ship.Id,
            SideId = ship.SideId ?? seeingSide,   // the exported ship's real side (faction identity)
            PilotType = ExportShip.PilotTypePlayer,
            Parts = EnsureLoadoutSnapshot(ship),
        };
        SendPeer(conn, FedPacket.Pack(new[] { exp.Encode() }));
        Console.WriteLine($"[xexport] side {seeingSide} <- peer {conn} ship {ship.Id} '{name}' hull {exp.HullId}");
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
    /// P265 — periodically broadcast <see cref="Wire.ScoreRosterUpdate"/>(209)
    /// to every connected peer, carrying the real in-mission roster
    /// (name / side / kills / deaths) from each live player's P193
    /// <c>PlayerScore</c>. This is the wire that lets the renderer-only
    /// MonoGame client draw the scoreboard overlay with live numbers.
    /// <para><b>Why this message and not PLAYERINFO(10).</b> FM_S_PLAYERINFO is
    /// a same-side, one-shot join-time identity snapshot whose stats fields the
    /// sender leaves at 0 (<see cref="PlayerInfo"/>) — it is never resent and
    /// carries no score. The per-tick SHIP_UPDATE carries physics, not scores.
    /// Neither can surface P215's real Kills/Deaths, so this is the smallest
    /// dedicated addition: a periodic whole-mission roster snapshot.</para>
    /// <para><b>Filters (mirror P194's endgame rules where applicable).</b>
    /// Only peers whose ship is on a real side are included (SideId == null is
    /// the port's team-lobby analogue, excluded exactly as native excludes
    /// SIDE_TEAMLOBBY). Unlike P194's game-over list there is no
    /// "played this mission" concern to approximate — every seated peer on a
    /// real side is shown.</para>
    /// <para><b>Audience.</b> Sent owner-only (each peer gets its own copy of
    /// the same whole-mission roster). Contained to each peer's own mission, so
    /// multi-world setups stay coherent.</para>
    /// </summary>
    private void BroadcastScoreRoster()
    {
        uint tickIndex = (uint)TickIndex;
        // Batch all peers into a per-mission roster map, then send each mission's
        // list to its peers. This handles multi-mission worlds in one pass.
        foreach (var missionGroup in _peerShips.GroupBy(kv => kv.Value.Ship.Mission))
        {
            var mission = missionGroup.Key;
            if (mission is null) continue;

            var entries = new List<ScoreRosterEntry>();
            foreach (var kv in missionGroup)
            {
                var live = kv.Value;
                if (live.Ship.SideId is not { } sid)
                    continue;                              // no real side → team-lobby analogue
                string name = _peerNames.TryGetValue(kv.Key, out var n) ? n : $"Ship {live.Ship.Id}";
                entries.Add(new ScoreRosterEntry(
                    CharacterName: name,
                    SideId: sid,
                    Kills: (short)Math.Clamp(live.PlayerScore.Kills, short.MinValue, short.MaxValue),
                    Deaths: (short)Math.Clamp(live.PlayerScore.Deaths, short.MinValue, short.MaxValue),
                    Rank: live.PlayerScore.Rank,
                    Score: live.PlayerScore.Score,
                    CombatRating: live.PlayerScore.CombatRating));
            }

            var targets = missionGroup.Select(kv => kv.Key).ToArray();

            // Throttle: only send once per ScoreRosterRefreshTicks per mission,
            // keyed on the mission so each mission's roster refreshes together.
            long lastTick = _lastScoreRosterTick.TryGetValue(mission, out var lt0) ? lt0 : -1;
            if (lastTick >= 0 && (long)tickIndex - lastTick < ScoreRosterRefreshTicks)
                continue;

            var roster = new ScoreRosterUpdate { Entries = entries.ToArray() };
            byte[] packet = FedPacket.Pack(new[] { roster.Encode() });
            Console.WriteLine($"[scoreroster] mission #{_missions.IndexOf(mission)} -> {entries.Count} player(s) to {targets.Length} peer(s)");
            foreach (var t in targets)
                SendPeer(t, packet);
            _lastScoreRosterTick[mission] = tickIndex;
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
        var peers = _peerShips.ToArray();
        var connsBySide = new Dictionary<short, List<ConnectionId>>();
        foreach (var kv in peers)
        {
            if (kv.Value.Ship.SideId is { } sid)
            {
                if (!connsBySide.TryGetValue(sid, out var list))
                    connsBySide[sid] = list = [];
                list.Add(kv.Key);
            }
        }
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
            var msg = StationsUpdate.FromStations(visible);
            byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
            foreach (var t in conns) SendPeer(t, packet);

            Console.WriteLine($"[stations] side {sideId} sees {visible.Length} station(s) " +
                              $"=> STATIONS_UPDATE(57) -> {conns.Count} peer(s) [PER SIDE]");
        }
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
        var peers = _peerShips.ToArray();
        var connsBySide = new Dictionary<short, List<ConnectionId>>();
        foreach (var kv in peers)
        {
            if (kv.Value.Ship.SideId is { } sid)
            {
                if (!connsBySide.TryGetValue(sid, out var list))
                    connsBySide[sid] = list = [];
                list.Add(kv.Key);
            }
        }
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
            byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
            foreach (var t in conns) SendPeer(t, packet);

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
        var peers = _peerShips.ToArray();
        var connsBySide = new Dictionary<short, List<ConnectionId>>();
        foreach (var kv in peers)
        {
            if (kv.Value.Ship.SideId is { } sid)
            {
                if (!connsBySide.TryGetValue(sid, out var list))
                    connsBySide[sid] = list = [];
                list.Add(kv.Key);
            }
        }
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
            byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
            foreach (var t in conns) SendPeer(t, packet);

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
        var peers = _peerShips.ToArray();
        foreach (var kv in peers)
        {
            var self = kv.Value.Ship;
            var sector = kv.Value.Sector;

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
            bool peerSees(Ship c) => !c.IsCloaked || self.SideId is not { } sd || c.SeenBySide(sd);

            // 1. Resolve the peer's command target within its own cluster
            //    (native pmodelTarget->GetCommandTarget(c_cmdCurrent) then the
            //    OT_-switch, FedSrv.CPP:4081-4124).
            Ship? targetShip = null;
            byte bpTargetHull = 255;    // 255 = "no target" (FedSrv.CPP:4079, appmsg.cpp:742)
            byte bpTargetShield = 255;
            var target = self.CommandTarget;
            if (target is not null && TargetInCluster(target, sector))
            {
                switch (target)
                {
                    case Ship ts:
                        // OT_ship, FedSrv.CPP:4086-4104: only a non-self,
                        // non-boarded source ship occupies the first heavy slot.
                        // The pfsTarget->GetHasUpdate() gate is skipped — P138
                        // drops the recent-update timestamp gate, so every
                        // candidate counts as having an update.
                        if (ts != self && ts.ParentShip is null)
                        {
                            bpTargetHull = Percent(ts.Fraction);
                            bpTargetShield = Percent(ts.ShieldFraction);
                            targetShip = ts;
                        }
                        break;
                    case Probe probe:
                        // OT_probe (also OT_asteroid/OT_missile shape,
                        // FedSrv.CPP:4117-4122): single fraction for hull, no
                        // shield, no heavy slot. OT_station/OT_asteroid are
                        // structurally unreachable here — CommandTarget is
                        // Model? and Station/Asteroid are not Model subclasses
                        // (see §2 of the MD); a Missile target has no damage
                        // fraction concept in this port.
                        bpTargetHull = Percent(probe.Fraction);
                        break;
                }
            }

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
            if (targetShip is not null && peerSees(targetShip))
                entries.Add(HeavyShipUpdateEntry.From(tickIndex, posRef, targetShip, tickIndex));

            // P219 — deviation/staleness-based ship-update budget
            // (FedSrv.CPP:3844-3950). The candidate list is exactly the
            // per-cluster selection P138 built (<c>Sector.GetShipUpdateCandidates</c>,
            // which includes the peer's own ship and any target — both excluded
            // here as in native's assembly loop, FedSrv.CPP:4148-4154).
            var all = sector.GetShipUpdateCandidates().ToList();

            // Fixed quotas — native's REAL, shipped values (the "w0dk4 Bandwidth
            // Patch", FedSrv.CPP:3844-3845). The disabled bandwidth-adaptive
            // sizing (<c>pfsPlayer->GetBandwidth()</c>, FedSrv.CPP:3841-3843) is
            // DEAD CODE in the reference checkout and never shipped — do NOT
            // reintroduce it (this port has no per-player bandwidth concept).
            const int cHeavyPerInterval = 2;   // FedSrv.CPP:3844
            const int cLightPerInterval = 2;   // FedSrv.CPP:3845

            if (all.Count <= 2)
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
                    entries.Add(HeavyShipUpdateEntry.From(tickIndex, posRef, c, tickIndex));
                    c.Deviation = 0f;   // served → reset
                }
            }
            else
            {
                // Budget path: sort the non-self, non-target candidates by
                // staleness DESCENDING (most-stale first — native CmpShipDeviation,
                // FedSrv.CPP:3614-3621, 3923) and serve the fixed heavy quota PLUS
                // the light quota. This port has no LIGHT_SHIPS_UPDATE wiring
                // (P139 deferred it), so per the P219 scope call the "light" tier
                // is folded into the heavy list — the total sent per peer per tick
                // is still bounded to cHeavyPerInterval + cLightPerInterval = 4,
                // which is what fixes the O(peers²) per-tick broadcast cost.
                // (Native's proportional light count, (cShipUpdates*2)/cShips,
                // FedSrv.CPP:3870, converges to 2 at realistic cluster sizes, so
                // the fixed quota is a faithful, simpler stand-in.)
                var others = all
                    .Where(c => !ReferenceEquals(c, self)
                             && peerSees(c)                                   // P270 — invisible enemy: no data
                             && (targetShip is null || !ReferenceEquals(c, targetShip)))
                    .OrderByDescending(c => c.Deviation)   // stable sort; native's qsort tie order is not load-bearing
                    .ToList();

                // The promoted target consumes one heavy slot (FedSrv.CPP:4141-4146),
                // so the remaining candidates compete for the rest of the heavy
                // budget plus the light budget.
                int othersHeavy = cHeavyPerInterval - (targetShip is not null ? 1 : 0);
                int servedLimit = othersHeavy + cLightPerInterval;   // ≤ 4 other ships per tick

                int i = 0;
                foreach (var c in others)
                {
                    if (i < servedLimit)
                    {
                        entries.Add(HeavyShipUpdateEntry.From(tickIndex, posRef, c, tickIndex));
                        c.Deviation = 0f;      // served → reset (FedSrv.CPP:3939-3940)
                    }
                    else
                    {
                        c.Deviation += 1.00f;  // skipped → bump to the front next tick
                                               // (FedSrv.CPP:3944-3948; deterministic +1.00, no
                                               // live RNG jitter and no light-tier +0.70 — see P219)
                    }
                    i++;
                }
            }

            var heavy = new HeavyShipsUpdate
            {
                TimeReference = tickIndex,
                PositionReference = posRef,
                HeavyShipEntries = entries.ToArray(),
                ActiveTurretUpdates = [],              // turret relay deferred (see §3)
                BpTargetHull = bpTargetHull,
                BpTargetShield = bpTargetShield,
                FractionsHull = Percent(self.Fraction),
                FractionsShield = Percent(self.ShieldFraction),
                FractionsAmmo = 240,
                FractionsFuel = 240,
                FractionsEnergy = 240,
                FractionsOre = Percent(Ship.MineCapacity <= 0f ? 0f : self.Ore / Ship.MineCapacity),
            };

            // 4. Per-player send of HEAVY only (LIGHT is never populated/sent).
            SendPeer(kv.Key, FedPacket.Pack(new[] { heavy.Encode() }));
            ConsoleLog.Write($"[ships] peer {kv.Key} side {self.SideId?.ToString() ?? "-"} " +
                              $"target={(targetShip?.Id.ToString() ?? "none")} " +
                              $"=> HEAVY_SHIPS_UPDATE(176) x{entries.Count} [PER PLAYER]");
        }
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
            default: return sector.Models.Contains(target);
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
            if (part.Amount <= 0) continue;
            if (ship.Mission is not { } mission) continue;
            if (!mission.Core.MissileTypes.TryGetValue(ml.ExpendableTypeId, out var missileType)) continue;

            // Only a Ship command target can build lock (UpdateMissileLock's target
            // gate); express "no lock target" as targetShipId = NA(-1) + zero pos.
            short targetShipId = -1;
            Vector3 targetPos = Vector3.Zero;
            if (ship.CommandTarget is Ship target)
            {
                targetShipId = target.Id;
                targetPos = target.Position;
            }

            var msg = MissileLockUpdate.From(ship.Id, targetShipId, ship.MissileLock, targetPos, missileType.ModelName);
            SendPeer(kv.Key, FedPacket.Pack(new[] { msg.Encode() }));
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
            bool tracked = false;
            float bestLock = 0f;

            // Native arms only from missiles in the PLAYER's cluster
            // (pcluster = GetCluster(), trekigc.cpp:5028-5050). The port's
            // cluster == sector; a missile whose Target is this ship is by
            // construction in its sector.
            if (ship.Sector is { } sector)
            {
                foreach (var model in sector.Models)
                {
                    if (model is Missile mis && mis.Target == ship)
                    {
                        tracked = true;
                        if (mis.LockFraction > bestLock)
                            bestLock = mis.LockFraction;
                    }
                }
            }

            var msg = MissileTrackWarningUpdate.From(tracked, bestLock);
            SendPeer(kv.Key, FedPacket.Pack(new[] { msg.Encode() }));
            if (tracked)
                ConsoleLog.Write($"[missiletrack] peer {kv.Key} ship {ship.Id} TRACKED by in-sector missile(s) bestLock {bestLock:F3} -> MISSILE_TRACK_WARNING(208)");
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
            var msg = OwnShipMiningUpdate.FromMining(ship.MiningAsteroid);
            SendPeer(kv.Key, FedPacket.Pack(new[] { msg.Encode() }));
        }
    }

    /// <summary>
    /// P298 — per-tick, OWNER-ONLY Kill Bonus stream: for each seated peer, send
    /// <c>OwnShipKillBonusUpdate(212)</c> carrying its own ship's live P278 Kill
    /// Bonus (the <c>50·c/(c+4)</c> readout and the raw streak count), so the
    /// renderer-only MonoGame client can draw a real KB gauge. Sent every tick
    /// (like <see cref="BroadcastOwnShipMining"/>) — always present, no
    /// change-tracking, at a cost of 10 bytes per peer per tick. Native
    /// delivered the same value to a local ship replica via PLAYERINFO.fExperience
    /// (appmsg.cpp:2280) and read it back with GetExperienceMultiplier()
    /// (WinTrek/console.cpp:826); this client has no replica, hence the stream.
    /// </summary>
    private void BroadcastOwnShipKillBonus()
    {
        foreach (var kv in _peerShips)
        {
            var msg = OwnShipKillBonusUpdate.FromShip(kv.Value.Ship);
            SendPeer(kv.Key, FedPacket.Pack(new[] { msg.Encode() }));
        }
    }

    /// <summary>
    /// P266 — per-tick, OWNER-ONLY predictive lead-aim stream: for each seated
    /// peer, solve where its mounted weapon must point to intercept its selected
    /// (command) moving target and stream the lead point + range/hit-probability
    /// flags as <see cref="Wire.LeadIndicatorUpdate"/>(210). This is the
    /// renderer-only client's answer to a feature native's client performed
    /// entirely locally: <c>RenderLeadIndicator</c> (<c>WinTrek/indicator.cpp:199-360</c>)
    /// read <c>GetCommandTarget(c_cmdCurrent)</c>, solved <c>solveForLead</c>
    /// (<c>Igc/common.cpp:121-149</c>) from the local sim's velocities and the
    /// mounted weapon's projectile speed, and drew the reticle. This port's
    /// MonoGame client is a pure renderer with no static core and no ship/weapon
    /// physics, so the server — which owns the real <see cref="Ship.CommandTarget"/>
    /// (c_cmdCurrent), live velocities and the <see cref="Weapon"/>'s projectile
    /// type — computes it here and pushes the result to the owning peer only
    /// (the established owner-only per-tick convention of
    /// <see cref="BroadcastMissileLock"/>/<see cref="BroadcastOwnShipMining"/>/
    /// <see cref="BroadcastMissileTrackWarning"/>).
    /// <para/>
    /// Faithfulness of the math is inherited from <see cref="Ship.SolveLead"/>
    /// (a faithful port of <c>solveForLead</c> → <c>solveForImpact</c>). The
    /// gating also mirrors native: the lead reticle is granted only when the
    /// shooter's own hull carries <c>c_habmLeadIndicator</c> (0x800) OR a
    /// friendly station with <c>c_sabmRemoteLeadIndicator</c> / friendly
    /// non-boarded ship with <c>c_habmRemoteLeadIndicator</c> has the target in
    /// scanner range — <see cref="CanShowLeadIndicator"/>. In-range
    /// (<c>t &lt;= weapon.GetLifespan()</c>) and the green→red hit-probability
    /// (the angular ramp of <see cref="Ship.LeadHitProbability"/>) ride the same
    /// message. When there is no command target, no weapon, or no grant, the
    /// message carries <c>TargetShipId = NA</c> so the client clears any stale
    /// reticle (mirroring <see cref="BroadcastOwnShipMining"/>'s always-send).
    /// </summary>
    private void BroadcastLeadIndicator()
    {
        foreach (var kv in _peerShips)
        {
            var ship = kv.Value.Ship;

            short targetShipId = -1;
            bool inRange = false;
            float green = 0f;
            Vector3 leadWorld = Vector3.Zero;
            Vector3 targetPos = Vector3.Zero;

            // The lead needs (a) a Ship command target (c_cmdCurrent; only a ship
            // has meaningful lead geometry), (b) that target in the shooter's own
            // cluster (the CanSee stand-in this port uses everywhere, cf
            // Weapon.cs:241) and alive, and (c) a mounted weapon whose projectile
            // the solve can fly (native requires `w`, indicator.cpp:207).
            if (ship.CommandTarget is Ship target &&
                !target.Dead &&
                !ReferenceEquals(target.Sector, null) &&
                ReferenceEquals(target.Sector, ship.Sector) &&
                ship.Weapons.FirstOrDefault() is { } weapon)
            {
                targetPos = target.Position;

                // Grant the indicator by hull capability or friendly-relay scanner
                // (native indicator.cpp:254-315).
                if (CanShowLeadIndicator(ship, target))
                {
                    float t = ship.SolveLead(target, weapon, 1.0f, out Vector3 dir);
                    float dist = (target.Position - ship.Position).Length();

                    // The reticle lives along the solved aim direction at the
                    // target's range — the screen home of the predictive lead,
                    // laterally offset from the target's own centre.
                    leadWorld = ship.Position + dir * dist;

                    // Native's in-range flag: t <= weapon lifespan (indicator.cpp:224).
                    inRange = t <= weapon.GetLifespan();

                    // Green→red hit-probability from the angle between the ship's
                    // forward aim (the firing solution) and the lead direction.
                    float angRadius = MathF.Atan2(0.5f * target.HullType.Length, dist);
                    green = Ship.LeadHitProbability(ship.Orientation.GetForward(), dir, angRadius);

                    targetShipId = target.Id;
                }
            }

            var msg = LeadIndicatorUpdate.From(ship.Id, targetShipId, inRange, green, leadWorld, targetPos);
            SendPeer(kv.Key, FedPacket.Pack(new[] { msg.Encode() }));

            if (targetShipId != -1)
                Console.WriteLine($"[lead] peer {kv.Key} ship {ship.Id} target {targetShipId} " +
                                  $"inRange {(inRange ? 1 : 0)} green {green:F2} -> LEAD_INDICATOR_UPDATE(210)");
        }
    }

    /// <summary>
    /// P266 — the lead-indicator visibility gate, mirroring native's
    /// <c>RenderLeadIndicator</c> (<c>WinTrek/indicator.cpp:254-315</c>):
    /// returns true when the <paramref name="shooter"/>'s own hull carries
    /// <c>c_habmLeadIndicator</c> (0x800), or failing that when a FRIENDLY (same
    /// side) relay — a station with <c>c_sabmRemoteLeadIndicator</c> or a
    /// non-boarded ship with <c>c_habmRemoteLeadIndicator</c> — has the target in
    /// its scanner range (reusing the P131/P132 <see cref="VisibilityMath"/>). The
    /// native code walks the cluster's stations (<c>indicator.cpp:280-294</c>) and
    /// ships (<c>:294-315</c>); here the cluster is the target's sector.
    /// </summary>
    private bool CanShowLeadIndicator(Ship shooter, Ship target)
    {
        if (shooter.HullType.HasCapability(DataHullTypeIGC.HabmLeadIndicator))
            return true;

        if (target.Sector is not { } cluster) return false;
        short? targetCluster = cluster.Id;

        var targetBody = new VisibilityMath.SightBody(target.Position, 0.5f * target.HullType.Length);
        float targetSignature = target.HullType.Signature;

        // Cluster's asteroid occluders, built once (native iterates
        // pcluster->GetAsteroids(), FedSrv.CPP:10910 + common.cpp:2324-2398).
        var occluders = new List<VisibilityMath.SightBody>();
        foreach (var a in cluster.Mineable)
            if (a.Data.Position is { Length: 3 } ap)
                occluders.Add(new VisibilityMath.SightBody(new Vector3(ap[0], ap[1], ap[2]), a.Radius));

        // Friendly relay stations (indicator.cpp:280-294).
        foreach (var st in cluster.StationRuntimes)
        {
            if (st.SideId != shooter.SideId) continue;
            if (!st.Type.HasCapability(DataStationTypeIGC.SabmRemoteLeadIndicator)) continue;
            if (VisibilityMath.InScannerRange(
                    new VisibilityMath.SightBody(st.Center, st.Radius), st.Type.ScannerRange, targetCluster,
                    targetBody, targetSignature, targetCluster, occluders))
                return true;
        }

        // Friendly relay ships — same side, not boarded (ParentShip == NULL),
        // carrying the relay hull bit (indicator.cpp:294-315).
        foreach (var m in cluster.Models)
        {
            if (m is not Ship rs) continue;
            if (rs.SideId != shooter.SideId) continue;
            if (rs.ParentShip is not null) continue;
            if (!rs.HullType.HasCapability(DataHullTypeIGC.HabmRemoteLeadIndicator)) continue;
            if (VisibilityMath.InScannerRange(
                    new VisibilityMath.SightBody(rs.Position, 0.5f * rs.HullType.Length), rs.HullType.ScannerRange, targetCluster,
                    targetBody, targetSignature, targetCluster, occluders))
                return true;
        }

        return false;
    }

    /// <summary>
    /// P234 — the periodic half of missile observation: broadcast every
    /// in-flight missile's current position/heading to the peers that can see
    /// it, once per sim tick (the same per-tick cadence as the STATIONS/
    /// ASTEROIDS/PROBES syncs and <see cref="BroadcastMissileLock"/>). This is
    /// the <em>ongoing position</em> stream a renderer-only client needs because
    /// it cannot simulate the missile's homing/turning locally (see
    /// <see cref="Wire.MissileUpdate"/> and docs/MISSILE_RENDERING_P234.md).
    /// <para/>
    /// Audience: <b>same-side only</b>, grouped per side like
    /// <see cref="BroadcastProbesUpdate"/> — a peer sees the missiles launched
    /// by ships on its own side (cross-side visibility is not attempted,
    /// matching <see cref="BroadcastFireMissile"/>). This is deliberately a hair
    /// <em>broader</em> than creation's export-sent micro-gate (which only
    /// reaches teammates who already hold the launcher's ExportShip): a peer who
    /// missed the one-shot FIRE_MISSILE(70) can hydrate the missile straight
    /// from this per-tick update, because ClientWorld upserts a missile entity
    /// from a MissileUpdate record for an id it has not seen before. Bandwidth is
    /// negligible (few missiles in flight per side).
    /// <para/>
    /// Runs on the sim thread (after the mission tick), so no extra locking is
    /// needed — mirroring <see cref="BroadcastMissileLock"/> and the per-side
    /// object syncs. Each record carries the missile's current <c>Position</c> /
    /// <c>Forward</c> plus its id, type and cluster so the client can both pick
    /// the model and sector-filter it like asteroids/warps.
    /// </summary>
    private void BroadcastMissilesUpdate()
    {
        // Side universe = the sides with seated peers (the only sides that have
        // anyone to send to). Group the peer connections by their ship's side in
        // one pass over a stable snapshot.
        var peers = _peerShips.ToArray();
        var connsBySide = new Dictionary<short, List<ConnectionId>>();
        foreach (var kv in peers)
        {
            if (kv.Value.Ship.SideId is { } sid)
            {
                if (!connsBySide.TryGetValue(sid, out var list))
                    connsBySide[sid] = list = [];
                list.Add(kv.Key);
            }
        }
        if (connsBySide.Count == 0) return;

        // All in-flight missiles across every mission, once — read off the live
        // sector model lists (the Missile case), never re-derived.
        var allMissiles = _missions
            .SelectMany(m => m.Sectors)
            .SelectMany(s => s.Models)
            .OfType<Missile>()
            .ToArray();

        foreach (var (sideId, conns) in connsBySide)
        {
            var visible = allMissiles
                .Where(m => (m.Launcher as Ship)?.SideId == sideId)
                .Select(m => new MissileStateRecord(m.Id, m.MissileType.ExpendableTypeId, m.OwnerSector.Id, m.Position, m.Forward))
                .ToArray();
            if (visible.Length == 0) continue;              // no missiles on this side this tick

            var msg = new MissileUpdate { Records = visible };
            byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
            foreach (var t in conns) SendPeer(t, packet);

            ConsoleLog.Write($"[missile-update] side {sideId} sees {visible.Length} missile(s) " +
                              $"=> MISSILE_UPDATE(205) -> {conns.Count} peer(s) [PER SIDE]");
        }
    }
}
