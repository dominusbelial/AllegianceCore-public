using System.Diagnostics;
using System.Runtime.InteropServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>
/// Side-select, side-alliance, ballot/voting, countdown, leader-succession and
/// game-over members of <see cref="Server"/> (P329 stage-3 split from
/// <c>Server.cs</c>). Partial class — shares the <c>Server</c> type, so
/// private field/method access across the split files is unaffected.
/// </summary>
public sealed partial class Server
{

// ── event processing (simulation thread) ───────────────────────────────

    /// <summary>
    /// P178 — win-by-elimination check, the port of native
    /// <c>CFSMission::CheckForVictoryByInactiveSides</c>
    /// (<c>FedSrv/fsmission.cpp:3767-3788</c>), invoked from the
    /// &quot;a side just went empty of players&quot; site (<c>fsmission.cpp:5371-5427</c>).
    /// This port's hook for that site is <see cref="ReleasePeer"/>: a peer's
    /// departure is the one moment a side can lose its last player.
    /// <para>
    /// For every side of <paramref name="mission"/> that still has at least one
    /// seated player — a player-piloted ship, which is exactly what
    /// <c>_peerShips</c> holds (NPCs never appear there; there is no ghost
    /// concept, mirroring <c>HasPlayers</c>' <c>ISPLAYER &amp;&amp; !IsGhost</c>,
    /// <c>fsmission.cpp:1422-1431</c>) — count the qualifying sides. More than
    /// one still having players: no decision yet, game continues. Exactly one:
    /// that side wins. Zero: a draw (&quot;All sides were destroyed&quot;).
    /// Scoped to THIS mission only (a server hosts multiple missions, cf.
    /// <c>ServerConfig.Games</c> being a list): only peers whose ship belongs
    /// to <paramref name="mission"/> are counted, and only those peers are
    /// recipients of the <c>GAME_OVER</c>(62) broadcast.</para>
    /// <para>
    /// Never runs when <paramref name="mission"/> is already
    /// <see cref="MissionStage.Over"/> (idempotency: an ended mission must not
    /// re-broadcast on a later disconnect). On a decision it flips
    /// <c>Stage</c> to <see cref="MissionStage.Over"/> — which immediately arms
    /// the scattered <c>mission.Stage != MissionStage.Started</c> gates (e.g.
    /// <see cref="HandleSuicide"/> refuses when not <c>Started</c>) — then builds
    /// and broadcasts <see cref="Wire.GameOver"/> to every peer still connected
    /// to that mission. The reason string and winner follow native
    /// (<c>fsmission.cpp:5400-5418</c>); the allied-name-list branch
    /// (<c>:5398-5418</c>) is N/A (no alliance concept in this port).</para>
    /// </summary>
    private void CheckForVictoryByInactiveSides(Mission mission)
    {
        if (mission.Stage == MissionStage.Over)
            return;                                  // idempotent: already decided

        // P388 — debug mode is an UNATTENDED autonomous test arena: zero
        // players is the designed steady state (P379/P381/P382/P387). When it
        // fires, the empty-lobby population check below would end the mission
        // the instant the last real client disconnects — defeating the whole
        // point. Skip it; only a genuine combat/conquest outcome (which goes
        // through DeclareGameOver via OnVictoryByStationsComputed, untouched)
        // may end a debug-mode mission. Matches the existing
        // _config.LiveSettings?.DebugMode == true access convention used at
        // Server.Lifecycle.cs:475 and :664.
        if (_config.LiveSettings?.DebugMode == true)
        {
            // Log only when this skip actually prevents an outcome that would
            // otherwise have fired (≤1 side with players).
            var guardSideIds = new HashSet<short>();
            foreach (var kv in _peerShips)
                if (ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId is { } sid)
                    guardSideIds.Add(sid);
            if (guardSideIds.Count <= 1)
                Console.WriteLine("[debugmode] skipping win-by-elimination check (0 players is expected in the unattended test arena)");
            return;
        }

        // Post-departure tally (the departing peer is already out of _peerShips).
        var playerSideIds = new HashSet<short>();
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId is { } sid)
                playerSideIds.Add(sid);
        // P455 — consume Side.Active exactly as native's
        // `HasPlayers(pside,false) && pside->GetActiveF()` does
        // (fsmission.cpp:3776): a deactivated side, even one with a lingering
        // ship, is not alive. Behavior-preserving for the peer-departure path
        // (sides only become inactive via DeactivateSide, which removes their
        // players first), kept so this check and DeactivateSide's agree.
        playerSideIds.RemoveWhere(id => mission.Sides.FirstOrDefault(s => s.Id == id) is not { Active: true });

        if (playerSideIds.Count > 1)
            return;                                  // ≥2 sides alive — game continues

        // Exactly one side with players → it wins; zero → draw.
        short winnerId = GameOver.NA;
        string reason = "All sides were destroyed";
        if (playerSideIds.Count == 1)
        {
            winnerId = playerSideIds.First();
            var winnerSide = mission.Sides.FirstOrDefault(s => s.Id == winnerId);
            string winnerName = winnerSide?.Name ?? $"side{winnerId}";
            reason = $"{winnerName} won by outlasting all other sides.";
        }

        // Decide the outcome first so the Stage==Over gates are live for any
        // subsequent gameplay message before we broadcast the end.
        DeclareGameOver(mission, winnerId, reason);
    }

    /// <summary>
    /// P455 — the server half of the station-elimination win, driven by the
    /// <see cref="ServerHost.SideLostRestartStationHook"/> seam the world missions
    /// raise after a side loses a restart-capable station to a kill or a capture
    /// (<see cref="Sector.ResolveCombatCollisions"/> /
    /// <see cref="Sector.ResolveDockCollisions"/>). This is the port of native's
    /// station-elimination block in <c>CheckForVictoryByStationKill</c>
    /// (<c>fsmission.cpp:3698-3714</c>, "Elimiation of all restart stations of
    /// all other sides is always a win"):
    /// <list type="number">
    /// <item>Walk the mission's station universe; if <paramref name="sideId"/>
    /// still owns a restart-capable station (capability
    /// <see cref="DataStationTypeIGC.SabmRestart"/>), it is <b>not</b> eliminated
    /// and nothing happens (the just-lost station is already removed from the
    /// kill path / re-sided to the captor on the capture path, so this walk sees
    /// the true remaining set — native's <c>pstationKilled</c> exclusion is
    /// naturally satisfied).</item>
    /// <item>If the side owns none and is still <see cref="Side.Active"/>, run
    /// <see cref="DeactivateSide"/> — the side is eliminated.</item>
    /// </list>
    /// Keep the conquest/territory recompute
    /// (<see cref="Mission.RecomputeConquestAndTerritory"/>) as-is — that path is
    /// gated on <c>ConquestEnabled</c>/<c>TerritoryEnabled</c>, so elimination
    /// must be its own, unconditional check. Runs on the sim thread (inside the
    /// sector pass that performed the kill/capture), the same one that owns
    /// <c>_peerShips</c>.</summary>
    private void OnSideLostRestartStation(Mission mission, short sideId)
    {
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null || !side.Active)
            return;                                   // unknown or already-inactive side

        // Does the side still own a restart-capable station? Walk the mission's
        // station universe (Sim.Station, the same source RecomputeConquestAndTerritory
        // tallies). The just-lost station is already gone (kill removes it;
        // capture re-sides it), so the walk is the true remaining set.
        foreach (var sector in mission.Sectors)
            foreach (var st in sector.StationRuntimes)
                if (st.SideId == sideId && st.Type.HasCapability(DataStationTypeIGC.SabmRestart))
                    return;                           // still owns a restart station — not eliminated

        DeactivateSide(mission, side);
    }

    /// <summary>
    /// P455 — eliminate a side that lost its last restart station: the port of
    /// native <c>CFSMission::DeactivateSide</c> (<c>fsmission.cpp:5318-5427</c>),
    /// kept in native order. A side is deactivated when the elimination check
    /// (<see cref="OnSideLostRestartStation"/>) finds it owns no restart-capable
    /// station. Steps:
    /// <list type="number">
    /// <item><c>side.Active = false</c> (<c>:5325</c>).</item>
    /// <item>Every ship of the side, before any kill: terminate all parts and
    /// set ammo and fuel to 0 (<c>:5344-5354</c>). The loadout is cleared via
    /// <see cref="Ship.ClearLoadout"/> (the port's canonical part-termination;
    /// its LOADOUT_CHANGE side broadcast is harmless and accurate), so a
    /// subsequent kill drops no loot — the same reason native terminates first.</item>
    /// <item>Kill every NPC ship (pilot type &lt; <see cref="Ship.PilotTypePlayer"/>)
    /// through the real kill path (<c>mission.Site.KillShipEvent</c> →
    /// <see cref="Server.Combat"/>'s <c>BroadcastKillShip</c>), so a real
    /// <c>KILL_SHIP(105)</c> goes out as for any death (<c>:5356-5359</c>).</item>
    /// <item>Players: force-kill any in a cluster with no parent ship through the
    /// same kill path with native's negative-damage "forced" intent
    /// (<c>:5362-5364</c>), then <see cref="Ship.ClearLoadout"/>'d / ammo-fuel
    /// zeroed state plus the <see cref="FinalizeDeadShip"/> removal stand in for
    /// native's <c>Reset(false)</c> + <c>ShipStatusExit()</c> (<c>:5365-5366</c>),
    /// leaving the eliminated side's players unable to respawn (the respawn gate
    /// in <c>BroadcastKillShip</c> honours <see cref="Side.Active"/>).</item>
    /// <item>Broadcast <c>SIDE_INACTIVE(68)</c> with <c>bActive=false</c>,
    /// <c>bChangeAET=false</c>, <c>bAET=false</c> to the whole mission
    /// (<c>:5371-5376</c>).</item>
    /// <item>Send the <c>"&lt;Side name&gt; is no more."</c> chat to everyone
    /// (<c>:5377</c>).</item>
    /// <item>Run the win-by-elimination check
    /// (<see cref="CheckForVictoryByInactiveSidesAfterDeactivation"/>,
    /// <c>:5378-5419</c>).</item>
    /// </list>
    /// Runs on the sim thread (same one that owns <c>_peerShips</c>), so no extra
    /// locking. The side-only game-over screen (native <c>:5420-5426</c>, 3+ sides)
    /// and the client's SIDE_INACTIVE handling are P455b — not built here.</summary>
    private void DeactivateSide(Mission mission, Side side)
    {
        short sideId = side.Id;
        side.Active = false;                          // fsmission.cpp:5325

        // Snapshot the side's ships across every sector (native pside->GetShips()).
        // Snapshot BEFORE any kill removes them from the sector's _models.
        var sideShips = mission.Sectors
            .SelectMany(s => s.Models)
            .OfType<Ship>()
            .Where(sh => sh.SideId == sideId)
            .ToArray();

        // 1. Every ship, BEFORE any kill: terminate parts + ammo=0 + fuel=0
        //    (fsmission.cpp:5344-5354).
        foreach (var sh in sideShips)
        {
            sh.ClearLoadout();
            sh.Ammo = 0;
            sh.SetFuel(0f);
        }

        // 2. Kill every NPC ship through the real kill path (KILL_SHIP(105)).
        foreach (var sh in sideShips)
            if (sh.PilotType < Ship.PilotTypePlayer)
                mission.Site.KillShipEvent(mission.LastUpdate, sh, null, 1.0f, sh.Position, Vector3.Zero);

        // 3. Players: force-kill in-flight (in a cluster, no parent) with native's
        //    negative-damage "forced" intent; Reset/ShipStatusExit equivalent for
        //    every player ship, leaving them unable to respawn.
        foreach (var sh in sideShips)
        {
            if (sh.PilotType < Ship.PilotTypePlayer) continue;
            if (sh.Sector is not null && sh.ParentShip is null)
                mission.Site.KillShipEvent(mission.LastUpdate, sh, null, -1.0f, sh.Position, Vector3.Zero);
            else
                FinalizeDeadShip(sh);   // docked/absent player — Reset+ShipStatusExit equivalent
        }

        // 4. Broadcast SIDE_INACTIVE(68) bActive=false, bChangeAET=false, bAET=false
        //    to the whole mission (fsmission.cpp:5371-5376).
        var si = new SideInactive { SideId = sideId, BActive = false, BChangeAet = false, BAet = false };
        int nSi = SendToMissionPeers(mission, FedPacket.Pack(new[] { si.Encode() }));
        Console.WriteLine($"[sideinactive] side {sideId} deactivated (lost last restart station) => SIDE_INACTIVE(68) bActive=false -> {nSi} peer(s)");

        // 5. Chat "<Side name> is no more." to everyone (fsmission.cpp:5377).
        var chat = new ChatMessage { Message = $"{side.Name} is no more.", ChatTarget = (int)ChatTarget.Everyone };
        int nChat = SendToMissionPeers(mission, FedPacket.Pack(new[] { chat.Encode() }));
        Console.WriteLine($"[sideinactive] side {sideId} elimination chat \"{side.Name} is no more.\" -> {nChat} peer(s)");

        // 6. Win-by-elimination check (fsmission.cpp:5378-5419).
        CheckForVictoryByInactiveSidesAfterDeactivation(mission);

        // 7. Side-only game-over (native fsmission.cpp:5420-5426). When the
        //    win check above did NOT end the game (2+ sides still alive — no
        //    winner declared, so mission.Stage is unchanged) but the eliminated
        //    side still has ships, native sends the FULL game-over screen ONLY
        //    to that side's group (`CFSSide::FromIGC(pside)->GetGroup()`), while
        //    everyone else plays on. In this port every still-connected peer of
        //    the deactivated side survives in _peerShips (DeactivateSide's kills
        //    finalize them but do not disconnect them — they just cannot respawn),
        //    so "the side still has ships" reduces to "the side still has a
        //    connected peer", exactly native's `pships->n()` after the kills.
        //    The port's GetGroup() analogue is _peerShips filtered to this
        //    mission + sideId. QueueGameoverMessage's reason is
        //    "Your side has been eliminated." (no mission-level reason is set
        //    here) and iSideWinner is NA (m_psideWon == NULL in this branch).
        //    Do NOT flip the stage and do NOT trigger P439 rotation — the
        //    mission is not over for the other sides.
        if (mission.Stage != MissionStage.Over)
        {
            var eliminatedPeers = _peerShips
                .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == sideId)
                .Select(kv => kv.Key)
                .ToArray();
            if (eliminatedPeers.Length > 0)
                SendSideOnlyGameOver(mission, GameOver.NA, "Your side has been eliminated.", eliminatedPeers);
        }
    }

    /// <summary>
    /// P455 — the win-by-elimination check run right after
    /// <see cref="DeactivateSide"/> (native's
    /// <c>CheckForVictoryByInactiveSides</c> call at <c>fsmission.cpp:5378-5419</c>).
    /// Unlike the peer-departure check (<see cref="CheckForVictoryByInactiveSides"/>),
    /// it has <b>no</b> P388 debug-mode skip: station elimination is a genuine
    /// combat outcome, so even in debug mode (the unattended 0-player arena) it
    /// ends the game. In debug mode the winner test drops the <c>HasPlayers</c>
    /// half and uses <see cref="Side.Active"/> alone, so a 0-player arena can
    /// still produce a winner; in production it uses native's
    /// <c>HasPlayers &amp;&amp; GetActiveF()</c>. Zero alive sides → draw
    /// (<c>"All sides were destroyed"</c>); exactly one → that side wins
    /// (<c>"&lt;name&gt; won by outlasting all other sides."</c>); more → the game
    /// continues. Declares via the single shared <see cref="DeclareGameOver"/>
    /// path (which also runs P439's mission rotation).</summary>
    private void CheckForVictoryByInactiveSidesAfterDeactivation(Mission mission)
    {
        if (mission.Stage == MissionStage.Over)
            return;                                   // idempotent: already decided

        bool debug = _config.LiveSettings?.DebugMode == true;

        List<Side> alive;
        if (debug)
        {
            // Active alone — the 0-player unattended arena (P388) still crowns a winner.
            alive = mission.Sides.Where(s => s.Active).ToList();
        }
        else
        {
            // Native HasPlayers && GetActiveF (fsmission.cpp:3776).
            var playerSideIds = new HashSet<short>();
            foreach (var kv in _peerShips)
                if (ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId is { } sid)
                    playerSideIds.Add(sid);
            alive = mission.Sides.Where(s => s.Active && playerSideIds.Contains(s.Id)).ToList();
        }

        if (alive.Count > 1)
            return;                                   // ≥2 sides alive — game continues

        short winnerId = GameOver.NA;
        string reason = "All sides were destroyed";
        if (alive.Count == 1)
        {
            winnerId = alive[0].Id;
            string winnerName = alive[0].Name ?? $"side{alive[0].Id}";
            reason = $"{winnerName} won by outlasting all other sides.";
        }
        DeclareGameOver(mission, winnerId, reason);
    }

/// <summary>
    /// Shared end-of-mission declaration, used by <b>both</b> the P178
    /// win-by-elimination path (<see cref="CheckForVictoryByInactiveSides"/>) and
    /// the P191 conquest/territory wins
    /// (<see cref="OnVictoryByStationsComputed"/>): flip the mission to
    /// <see cref="MissionStage.Over"/> (arming the scattered
    /// <c>mission.Stage != MissionStage.Started</c> gates, e.g.
    /// <see cref="HandleSuicide"/>), then build and broadcast
    /// <see cref="Wire.GameOver"/> to every peer still connected to that mission
    /// (whole-mission audience scoped to THIS mission — only peers whose ship
    /// belongs to it are recipients, cf. <c>ServerConfig.Games</c> being a list).
    /// Reusing this single path is the explicit P191 instruction — the
    /// conquest/territory wins must not invent a second game-over mechanism.
    /// </summary>
    private void DeclareGameOver(Mission mission, short winnerId, string reason)
    {
        mission.Stage = MissionStage.Over;

        var gameOver = GameOver.ForMission(mission, winnerId, reason);
        byte[] packet = FedPacket.Pack(new[] { gameOver.Encode() });

        // Whole-mission audience, scoped to THIS mission: every peer whose ship
        // belongs to it (the mission-scoped sibling of the whole-mission idiom
        // BroadcastAsteroidDestroyed/BroadcastStationDestroyed use, Server.cs).
        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
            .Select(kv => kv.Key)
            .ToArray();

        Console.WriteLine($"[gameover] mission #{_missions.IndexOf(mission)} -> Stage Over, " +
                          $"iSideWinner={(winnerId == GameOver.NA ? "NA(draw)" : winnerId.ToString())}, " +
                          $"{targets.Length} peer(s)");
        foreach (var t in targets)
            SendPeer(t, packet);

        // P194 — GAME_OVER_PLAYERS(63): per-player endgame broadcast, an
        // addition fired at the exact same shared hook, not a replacement. It
        // rides the same whole-mission audience and reports P193's PlayerScore
        // for every currently-connected peer on a REAL side.
        BroadcastGameOverPlayers(mission);

        // P439 — rotate the finished mission to a freshly-assembled NotStarted
        // one so the server is joinable again (the only way it ever leaves Over
        // today was a full server recreation). Native does this in two steps —
        // ResetMission (missionigc.cpp:3804-3828) then a regenerate at the next
        // StartCountdown (missionigc.cpp:3830+) — but the port generates at
        // assembly, so the rotation collapses the pair into one rebuild. Runs
        // on the shared path so every end state (draw, win-by-elimination,
        // conquest/prosperity/artifact) rotates identically.
        RotateMissionAfterGameOver(mission);
    }

    /// <summary>
    /// P439 — replace a just-finished <paramref name="mission"/> with a freshly
    /// assembled <see cref="MissionStage.NotStarted"/> mission at the SAME index
    /// of <c>_missions</c>, so every new joiner / reconnect lands on a pristine
    /// world (they are routed to <c>_missions[0]</c> / their mission via the
    /// existing join path, which reports the mission's REAL stage — P309).
    /// Called only from <see cref="DeclareGameOver"/>, the single shared
    /// game-over path, after GAME_OVER(62) and GAME_OVER_PLAYERS(63) are on the
    /// wire (native's order: send game-over first, fsmission.cpp:3427-3429,
    /// then reset, then SetStage, :3490).
    /// <para><b>Already-connected peers are migrated, not orphaned (P480).</b>
    /// Native's <c>Vacate()</c> + <c>Reset(true)</c> (fsmission.cpp:2604-2622)
    /// keeps every player connected and seated on the same side with a fresh
    /// ship, so after the rotation the finished mission's still-connected peers
    /// are re-seated into the fresh world and re-sent the fresh-joiner join burst
    /// (see <see cref="MigrateConnectedPeersIntoRotatedMission"/>). P439's
    /// original "keep GAME_OVER screen, must reconnect" stance and its orphan
    /// handling are therefore now dead code and removed; the ship-id allocator
    /// safety it established (<c>IsShipIdLive</c> checking <c>_peerShips</c>
    /// before <c>_missions</c>) is retained. The defensive <c>idx &lt; 0</c>
    /// guard below still stands.</para>
    /// <para><b>Structural safety:</b> swapping by index (not Add/Remove) keeps
    /// the <c>_missions</c> list length constant, so game-over fired from inside
    /// a <c>foreach (var m in _missions)</c> sim step (e.g. conquest during
    /// <c>m.Update</c>) cannot invalidate the enumerator.</para>
    /// </summary>
    private void RotateMissionAfterGameOver(Mission mission)
    {
        int idx = _missions.IndexOf(mission);
        if (idx < 0)
        {
            // Defensive: the mission must be in _missions at game-over. If it is
            // not (shouldn't happen), there is nothing to rotate onto and the old
            // game-over broadcast already went out.
            Console.WriteLine("[gameover] P439: finished mission is no longer in _missions — skipping rotation");
            return;
        }

        // Broadcast MISSION_STAGE(37) Over to the OLD mission's peers, so
        // already-connected clients get the stage native would give them
        // (SetStage always broadcasts MISSION_STAGE, fsmission.cpp:1750-1768).
        // Done before the swap so _missions.IndexOf(mission) in the broadcast's
        // log is still the real index.
        BroadcastMissionStageToMission(mission, MissionStage.Over);

        // Clear the old mission's per-mission state so nothing keyed by the
        // discarded Mission leaks into (or keeps alive) the old world: the
        // start-countdown deadline, pending ballots, the MapGen tag and the
        // score-roster throttle.
        _startCountdownDeadline.Remove(mission);
        _ballots.Remove(mission);
        _mapGenMissions.Remove(mission);
        _lastScoreRosterTick.Remove(mission);

        // Rebuild the world from the SAME config that made the original (native
        // regenerates on the next start; this port assembles now). _config.Games
        // and _missions are index-aligned (one mission per game).
        var fresh = AssembleGameMission(_config.Games[idx], _core, _landingBays, _weaponMuzzles);
        _missions[idx] = fresh;

        Console.WriteLine($"[gameover] P439: mission #{idx} finished -> rebuilt fresh STAGE_NOTSTARTED " +
                          $"({fresh.Sectors.Count} sectors, {fresh.Sides.Count} sides, {fresh.Warps.Count} warps, " +
                          $"{fresh.Sectors.Sum(s => s.Stations.Count)} stations, " +
                          $"{fresh.Sectors.Sum(s => s.Asteroids.Count)} asteroids); new joiners land here NotStarted");

        // Match boot-time ordering (Lifecycle.cs): SpawnInitialMiners runs once
        // _missions is assigned, then the P387 debug-mode auto-start follows.
        SpawnInitialMiners(fresh);
        if (_config.LiveSettings?.DebugMode == true)
            TryAutoStart(fresh);   // P387: rebuilt debug mission auto-starts, exactly as at boot

        // P480 — migrate the old mission's still-connected peers into the fresh
        // mission, exactly as native Vacate()+Reset(true) leaves them: still
        // connected, seated on the same side id, with a fresh docked/lobby ship,
        // ready cleared and wallet reset, and each re-sent the join-burst a fresh
        // joiner of a NotStarted mission receives (so their client re-learns the
        // new world's clusters/stations/exports and the NotStarted stage).
        MigrateConnectedPeersIntoRotatedMission(mission, fresh);
    }

    /// <summary>
    /// P480 — migrate the finished <paramref name="oldMission"/>'s still-connected
    /// peers into the freshly-assembled <paramref name="fresh"/> mission, mirroring
    /// exactly how native <c>Vacate()</c> + <c>Reset(true)</c> (fsmission.cpp:2604-2622)
    /// leave a player after game-over, then the port's own rotation:
    /// <list type="bullet">
    /// <item><b>Still connected</b> — no disconnect, no reconnect; the same
    /// connection stays and is given a fresh ship.</item>
    /// <item><b>Seated on the same side id</b> — each peer is re-seated via
    /// <see cref="TryJoinSide"/> onto the side it had in the OLD mission, in an
    /// order that preserves the old leader (first joiner of a side becomes its
    /// leader) and the old mission owner.</item>
    /// <item><b>A fresh docked/lobby-state ship</b> — a brand-new ship object in
    /// the new world via <see cref="AssignShip"/>/<see cref="SpawnDefaultShip"/>
    /// (native Reset(true) gives the player a reset ship; old drones are not
    /// carried over because native deletes them in <c>Vacate()</c>).</item>
    /// <item><b>Ready cleared and wallet reset</b> — <c>_playerReady</c> entry
    /// removed and the fresh <see cref="PlayerWallet"/> starts at 0 (native
    /// resets <c>rgfReady</c>/<c>rgfMoney</c> at fsmission.cpp:3493-3521;
    /// P443 then seeds + pays starting money at the next real start).</item>
    /// <item><b>Leadership / ownership re-established</b> — re-seating re-runs
    /// the side-leader + mission-owner election (native AddPlayerToSide), so the
    /// same leader stays leader and the same owner side stays owner.</item>
    /// <item><b>Old-mission per-peer latches cleared</b> — see
    /// <see cref="ClearPeerJoinLatches"/>, so the new world's identity messages
    /// (EXPORT / PLAYERINFO) actually get re-sent to every migrated peer.</item>
    /// <item><b>Re-sent the fresh-joiner join burst</b> — each migrated peer is
    /// re-driven through <see cref="CompleteJoinAndLaunch"/>, THE existing
    /// join-burst path, so it receives MISSION_STAGE NotStarted, the new world's
    /// clusters/stations/EXPORTs, its side/leader/ready info and its launch —
    /// the identical burst a fresh joiner of a NotStarted mission gets. No
    /// parallel burst is hand-built.</item>
    /// </list>
    /// Side mapping is 1:1 (the new mission is assembled from the same
    /// <c>GameConfig</c>), but this is VERIFIED not assumed: every old side id
    /// must exist in the fresh mission or that side's peers are left unseated
    /// (logged, never guessed). Runs on the sim thread (same one that owns
    /// <c>_peerShips</c>), like the surrounding rotation.</summary>
    private void MigrateConnectedPeersIntoRotatedMission(Mission oldMission, Mission fresh)
    {
        // Side 1:1 verification (the new mission is assembled from the same
        // GameConfig, but confirm rather than assume).
        foreach (var os in oldMission.Sides)
            if (!fresh.Sides.Any(s => s.Id == os.Id))
                Console.WriteLine($"[reseat] WARNING: fresh mission lacks old side {os.Id} — its peers will be left unseated");

        var migrants = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, oldMission))
            .Select(kv => (Conn: kv.Key, Live: kv.Value))
            .ToArray();
        if (migrants.Length == 0)
        {
            Console.WriteLine("[reseat] P480: no connected peers to migrate (all had disconnected)");
            return;
        }

        var freshSideIds = fresh.Sides.Select(s => s.Id).ToHashSet();

        // Which peers were their side's leader / which side owned the mission in
        // the OLD world, so the re-seat can preserve both.
        var oldLeaderConns = oldMission.Sides
            .Where(s => s.LeaderShipId is { } lid && lid >= 0)
            .Select(s => (Side: s.Id, Leader: _peerShips.Where(kv => kv.Value.Ship.Id == s.LeaderShipId)
                                .Select(kv => kv.Key).FirstOrDefault()))
            .Where(t => t.Leader != default)
            .ToDictionary(t => t.Side, t => t.Leader);
        short? oldOwnerSide = oldMission.MissionOwnerSideId;

        // Phase 1 — for each migrant, mint a fresh ship in the new mission,
        // register it (so later phases' whole-mission broadcasts reach it), and
        // clear every per-peer latch keyed to the OLD mission.
        var prepared = new List<(ConnectionId Conn, LiveShip NewLive, short NewShipId, short SideId, LogonRequest Req, bool IsOldLeader)>();
        foreach (var (conn, live) in migrants)
        {
            short oldSide = live.Ship.SideId ?? -1;
            short newShipId = AllocateShipId();
            LogonRequest req = _peerRequests.TryGetValue(conn, out var r) ? r
                : new LogonRequest { CharacterName = _peerNames.TryGetValue(conn, out var nm) ? nm : $"Ship {live.Ship.Id}" };

            // A fresh, docked/lobby-state ship in the new mission (native
            // Reset(true)). oldSide is threaded into placement so the ship lands
            // in that side's home; reseating via TryJoinSide (phase 2) then
            // re-establishes leadership/bookkeeping. Uses the DEFAULT spawn path
            // (AssignMigratedShip), deliberately NOT the injected ShipSpawner:
            // the spawner is a join-time placement policy for NEW joiners; a
            // migrated peer is an EXISTING player being re-seated, so a test
            // spawner must not hijack (or crash on) its re-placement.
            var newLive = AssignMigratedShip(newShipId, oldSide != -1 ? oldSide : null);

            // Ready cleared + wallet reset (native fsmission.cpp:3493-3521): a
            // fresh PlayerWallet already starts at 0; remove the ready flag so
            // the peer must ready again before the next game can start.
            ClearPeerJoinLatches(conn, live.Ship.Id);
            _playerReady.Remove(conn);
            foreach (var pk in _pendingJoinRequests
                         .Where(k => k.Value.ShipId == live.Ship.Id).Select(k => k.Key).ToList())
                _pendingJoinRequests.Remove(pk);

            // Register the fresh ship NOW so later phases' broadcasts reach this
            // peer (its old mission no longer matches any whole-mission sweep).
            _peerShips[conn] = newLive;

            bool isOldLeader = oldLeaderConns.TryGetValue(oldSide, out var lc) && lc == conn;
            prepared.Add((conn, newLive, newShipId, oldSide, req, isOldLeader));
            Console.WriteLine($"[reseat] peer {conn} -> fresh mission: new ship {newShipId} (was {live.Ship.Id}) targeting side {oldSide}{(isOldLeader ? " (was leader)" : "")}");
        }

        // Phase 2 — re-seat every migrant onto its ORIGINAL side, preserving the
        // old leader (first joiner of a side becomes its leader) and the old
        // mission owner (owner-side peers re-seat first, so the owner side
        // reclaims ownership). Peers of a side missing from the fresh mission
        // (verified above) are left unseated.
        var ordered = prepared
            .Where(p => freshSideIds.Contains(p.SideId))
            .OrderBy(p => p.SideId == oldOwnerSide ? 0 : 1)
            .ThenByDescending(p => p.IsOldLeader)
            .ThenBy(p => p.SideId);
        foreach (var p in ordered)
        {
            if (TryJoinSide(p.NewLive.Ship, p.SideId))
                Console.WriteLine($"[reseat] peer {p.Conn} -> fresh mission side {p.SideId} (ship {p.NewShipId})");
            else
                Console.WriteLine($"[reseat] peer {p.Conn} -> could NOT re-seat on fresh side {p.SideId}");
        }

        // Phase 3 — re-send each migrated peer the join-burst a fresh joiner of a
        // NotStarted mission receives (MISSION_STAGE NotStarted, the new world's
        // clusters/stations/EXPORTs, its side/leader/ready info, launch). Reuses
        // the exact existing join-burst path; nothing parallel is hand-built.
        foreach (var p in prepared)
        {
            CompleteJoinAndLaunch(p.Conn, p.Req, p.NewShipId, (byte)(p.Req.DwCookie & 0xFF), p.NewLive);
            Console.WriteLine($"[reseat] peer {p.Conn} -> re-sent fresh-joiner join burst (ship {p.NewShipId}, stage {fresh.Stage})");
        }
    }

    /// <summary>
    /// P480 — clear every per-peer one-shot/time-windowed latch keyed to a
    /// migrated peer, so after a game-over rotation the new world's identity
    /// messages (<c>EXPORT(26)</c>, <c>PLAYERINFO(10)</c>, loadout/status sync)
    /// are genuinely re-sent to it about its (new) teammates instead of being
    /// suppressed by a stale <c>(recipient, oldShipId)</c> latch. Mirrors the
    /// per-peer release in <see cref="ReleasePeer"/>: every
    /// latch where <paramref name="conn"/> is the recipient OR
    /// <paramref name="oldShipId"/> is the subject is freed. This is belt-and-
    /// suspenders — the fresh ships carry fresh ids the latches never saw — but
    /// it is required by the task so no stale (recipient, id) key can ever
    /// silence a new-world identity send.</summary>
    private void ClearPeerJoinLatches(ConnectionId conn, short oldShipId)
    {
        _teammateExportSent.RemoveWhere(e => e.Recipient == conn || e.ShipId == oldShipId);
        _teammatePlayerInfoSent.RemoveWhere(e => e.Recipient == conn || e.ShipId == oldShipId);
        _crossSideExportSent.RemoveWhere(e => e.Recipient == conn || e.ShipId == oldShipId);
        _crossSideMineExportSent.RemoveWhere(e => e.Recipient == conn);
        _sameSideNpcExportSent.RemoveWhere(e => e.Recipient == conn || e.ShipId == oldShipId);
        foreach (var k in _lastSameSideNpcStatusTick
                     .Where(kv => kv.Key.Recipient == conn || kv.Key.ShipId == oldShipId)
                     .Select(kv => kv.Key).ToList())
            _lastSameSideNpcStatusTick.Remove(k);
        foreach (var k in _teammateLoadoutVersionSent
                     .Where(kv => kv.Key.Recipient == conn || kv.Key.ShipId == oldShipId)
                     .Select(kv => kv.Key).ToList())
            _teammateLoadoutVersionSent.Remove(k);
        _teammateLoadoutCache.Remove(oldShipId);
        foreach (var k in _lastTeammateStatusTick
                     .Where(kv => kv.Key.Item1 == conn || kv.Key.Item2 == oldShipId)
                     .Select(kv => kv.Key).ToList())
            _lastTeammateStatusTick.Remove(k);
        _lastStatusSector.Remove(conn);
        _lastStatusTick.Remove(conn);
        _lastClusterInfoTick.Remove(conn);
    }

    /// <summary>Build the per-player endgame roster for a mission (the port's
    /// stand-in for native's per-player <c>GAME_OVER_PLAYERS</c> list inside
    /// <c>QueueGameoverMessage</c>, fsmission.cpp:3094-3141): one
    /// <see cref="GameOverPlayerEntry"/> per currently-connected peer on a real
    /// side of the mission, in <c>_peerShips</c> order. Shared by the full
    /// whole-mission game-over (<see cref="BroadcastGameOverPlayers"/>) and the
    /// P455b side-only game-over (<see cref="SendSideOnlyGameOver"/>), whose
    /// only difference is the recipient set — native builds the SAME full player
    /// list in both cases (fsmission.cpp:3115-3141), addressing it to whoever
    /// should see the screen.</summary>
    private GameOverPlayerEntry[] BuildGameOverPlayerEntries(Mission mission)
    {
        var entries = new List<GameOverPlayerEntry>();
        foreach (var kv in _peerShips)
        {
            var live = kv.Value;
            if (!ReferenceEquals(live.Ship.Mission, mission))
                continue;                              // scope to THIS mission
            if (live.Ship.SideId is not { } sid)
                continue;                              // no real side → the team-lobby analogue
            string name = _peerNames.TryGetValue(kv.Key, out var n) ? n : $"Ship {live.Ship.Id}";
            entries.Add(new GameOverPlayerEntry(
                CharacterName: name,
                SideId: sid,
                Rank: live.PlayerScore.Rank,
                Score: live.PlayerScore.Score));
        }
        return entries.ToArray();
    }

    /// <summary>
    /// P455b — send the full game-over (<c>GAME_OVER(62)</c> +
    /// <c>GAME_OVER_PLAYERS(63)</c>) to a SPECIFIC set of peers only, with no
    /// mission-end and no P439 rotation. This is the port of native's
    /// side-only game-over (<c>fsmission.cpp:5420-5426</c>), where the
    /// eliminated side's group (<c>CFSSide::FromIGC(pside)->GetGroup()</c>) is
    /// the default recipient of <c>QueueGameoverMessage()</c>: only that side
    /// sees its game-over screen while every other side plays on. Reuses the
    /// same builders as <see cref="DeclareGameOver"/>'s whole-mission broadcast
    /// (<see cref="GameOver.ForMission"/> + <see cref="BuildGameOverPlayerEntries"/>);
    /// only the recipient set differs. It intentionally does NOT flip the
    /// mission stage and does NOT trigger P439's rotation, because the mission
    /// is not over for the other sides.</summary>
    private void SendSideOnlyGameOver(Mission mission, short winnerId, string reason, IReadOnlyList<ConnectionId> targets)
    {
        var gameOver = GameOver.ForMission(mission, winnerId, reason);
        byte[] goPacket = FedPacket.Pack(new[] { gameOver.Encode() });
        var players = new GameOverPlayers { Entries = BuildGameOverPlayerEntries(mission) };
        byte[] gpPacket = FedPacket.Pack(new[] { players.Encode() });

        Console.WriteLine($"[sidegameover] mission #{_missions.IndexOf(mission)} -> side-only GAME_OVER(62) \"{reason}\" + " +
                          $"GAME_OVER_PLAYERS(63) to {targets.Count} peer(s) on the eliminated side (mission NOT ended, no rotation)");
        foreach (var t in targets)
        {
            SendPeer(t, goPacket);
            SendPeer(t, gpPacket);
        }
    }

    /// <summary>
    /// P194 — broadcast <see cref="Wire.GameOverPlayers"/>(63) to every peer
    /// still connected to <paramref name="mission"/> (the same whole-mission
    /// audience <see cref="DeclareGameOver"/> already addresses GAME_OVER(62)
    /// to), carrying each connected player's name/side/P193 <c>PlayerScore</c>.
    /// This is the port of native's per-player endgame loop
    /// (<c>FedSrv/fsmission.cpp:3094-3141</c>, inside
    /// <c>CFSMission::QueueGameoverMessage</c>, itself invoked from
    /// <c>ProcessGameOver</c>).
    /// <para><b>Player filters (native's real ones, ported).</b> Native includes
    /// only players who have actually played (<c>GetTimePlayed() &gt; 0</c>) and
    /// are on a real side (<c>GetSide()-&gt;GetObjectID() != SIDE_TEAMLOBBY</c>,
    /// fsmission.cpp:3103). This port has no time-played counter at all (nothing
    /// in P193's <c>PlayerScore</c> tracks play time), so the "has actually
    /// played" filter collapses to "is a seated connection" — the task's allowed
    /// fallback when no equivalent concept exists. The real-side filter is
    /// ported: a peer whose ship has <c>SideId == null</c> is the closest
    /// analogue of native's team-lobby membership (this port has no explicit
    /// lobby side), and is excluded exactly as native excludes
    /// <c>SIDE_TEAMLOBBY</c>.</para>
    /// <para><b>Simplification, stated plainly:</b> native's second loop over
    /// <c>m_oldPlayers</c> (recently-disconnected-but-still-tracked players,
    /// fsmission.cpp:3115-3128) is out of scope — this port has no
    /// "keep a disconnected player's stats around briefly" mechanism, so a
    /// disconnected player simply isn't in the list here, whereas native would
    /// still include them if they'd played and disconnected recently.</para>
    /// <para><b>Chunking:</b> native splits the list into ≤50-player messages
    /// (fsmission.cpp:3131-3141); this port sends one message (judged unneeded
    /// at this port's scale — missions seat a handful of peers).</para>
    /// </summary>
    private void BroadcastGameOverPlayers(Mission mission)
    {
        var entries = BuildGameOverPlayerEntries(mission);
        var players = new GameOverPlayers { Entries = entries };
        byte[] packet = FedPacket.Pack(new[] { players.Encode() });

        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
            .Select(kv => kv.Key)
            .ToArray();

        Console.WriteLine($"[gameover-players] mission #{_missions.IndexOf(mission)} -> {entries.Length} player(s) to {targets.Length} peer(s)");
        foreach (var t in targets)
            SendPeer(t, packet);
    }

/// <summary>
    /// Handle a client <c>FM_C_SUICIDE(80)</c> for a peer that has a ship. This
    /// is the P103 port of <c>FedSrv.CPP:6335-6350</c> <c>case
    /// FM_C_SUICIDE</c>:
    /// <list type="bullet">
    /// <item><b>Gate: mission started.</b> The native refuses unless
    /// <c>pfsMission-&gt;GetStage() == STAGE_STARTED</c>
    /// (<c>FedSrv.CPP:6337</c>). We keep it — <see cref="Ship.Mission"/>
    /// must exist and be at <see cref="MissionStage.Started"/>.</item>
    /// <item><b>Gate: the ship is actually flying.</b> The native refuses
    /// unless <c>GetCluster() != NULL</c> (<c>FedSrv.CPP:6338</c>) — i.e. the
    /// ship is in a sector, not parked. In this port a placed-but-docked ship
    /// retains <see cref="Ship.Sector"/> (the dock seam sets
    /// <see cref="Ship.StationId"/> but does not clear Sector), so
    /// "has a cluster" faithfully means placed <b>and</b> not docked:
    /// <c>Sector != null &amp;&amp; StationId == null</c>. Docked/off-sector
    /// ships are a no-op — no kill, no broadcast — matching the native gate.</item>
    /// <item><b>Deliberate relaxation (documented, not a silent drop): the
    /// child-ship / cheat gate collapses.</b> Native also requires
    /// <c>GetParentShip() != NULL</c> (a boarded/turret gunner) <em>or</em>
    /// <c>CanCheat()</c> (admin). This port has <em>neither</em> — no
    /// boarding/child-ship concept (Phase 3 cluster E) and no admin/cheat
    /// concept at all. Following the P100 precedent (a gate collapses to
    /// always-true when its own prerequisite doesn't exist yet), any placed
    /// ship may suicide. Revisit only when boarding lands.</item>
    /// <item><b>Kill path reused, not rebuilt.</b> Triggering uses the exact
    /// seam a combat death's lethal branch raises
    /// (<c>Ship.ReceiveDamage</c> → <c>Mission.Site.KillShipEvent</c>,
    /// <c>Ship.cs:650</c>) — a direct <c>KillShipEvent</c> with a NULL
    /// launcher, precisely what the native suicide does (launcher NULL,
    /// amount 1.0). No parallel kill mechanism is introduced; the server's
    /// <see cref=\"ServerHost.ShipKilled\"/> wiring therefore broadcasts the
    /// usual <c>KILL_SHIP(105)</c> and the new <c>SHIP_DELETE(20)</c>
    /// alongside. As in native, the suicide itself does not drain the hull —
    /// the death notice is what a suicide produces.</item>
    /// </list>
    /// Runs on the sim thread (the dispatch is drained inside
    /// <see cref=\"Tick\"/>, like every other inbound handler), so it may call
    /// the kill seam and touch <c>_peerShips</c> without extra locking.
    /// </summary>
    private void HandleSuicide(ConnectionId from, LiveShip live)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[suicide] {from} ship {ship.Id}: " +
                              $"{(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — refused");
            return;
        }
        if (ship.Sector is null || ship.StationId is not null)
        {
            Console.WriteLine($"[suicide] {from} ship {ship.Id}: " +
                              $"{(ship.Sector is null ? "no cluster" : "docked")} — refused (gate: GetCluster(), FedSrv.CPP:6338)");
            return;
        }
        // Relaxed child/cheat gate (see doc comment): ANY placed ship may suicide.
        Console.WriteLine($"[suicide] {from} ship {ship.Id} — re-triggering the combat kill path (KillShipEvent, launcher NULL)");
        mission.Site.KillShipEvent(mission.LastUpdate, ship, null, 1.0f, ship.Position, Vector3.Zero);
    }

/// <summary>
    /// P180 — recompute one side's aggregate readiness after a player's own
    /// ready flag changed and, if it actually flipped, broadcast the change and
    /// (possibly) auto-start the mission. The port of native's
    /// <c>CheckForSideAllReady</c> (<c>fsmission.cpp:5492-5529</c>), reached
    /// from the <c>PLAYER_READY</c> relay handler as the analogue of native's
    /// <c>PlayerReadyChange</c> (<c>fsmission.cpp:5485-5489</c>).
    /// <para>The real aggregation rules (<c>fsmission.cpp:5501-5525</c>): a side
    /// with fewer than <c>nMinPlayersPerTeam</c> connected players is forced
    /// not-ready; a force-readied side is ready regardless; otherwise every
    /// connected player-piloted ship on the side must be individually ready.
    /// This port has <b>no</b> <c>nMinPlayersPerTeam</c> equivalent (confirmed
    /// absent from <c>GameConfig</c>/<c>ServerConfig</c>), so the readiness
    /// floor is <b>at least one connected player</b>, and there is no force-ready
    /// (that is the leader-only <c>FORCE_TEAM_READY</c>, out of scope). Each
    /// <c>_peerShips</c> entry is a player-piloted ship by construction (one
    /// owned ship per connected peer), so "every connected player on the side
    /// ready" reduces to: side has ≥1 peer AND every such peer's recorded
    /// ready flag is true.</para>
    /// <para><b>Fix (post-landing review):</b> scoped to <c>ship.Mission</c> —
    /// a server can host multiple concurrent missions
    /// (<c>ServerConfig.Games</c> is a list), and side ids are small integers
    /// assigned per-mission, not globally unique, so an unscoped scan would
    /// incorrectly fold another mission's same-numbered side into this one's
    /// readiness. Matches the per-mission scoping idiom P178's
    /// <c>CheckForVictoryByInactiveSides</c> already established.</para></summary>
    private void RecomputeSideReady(Ship ship)
    {
        if (ship.SideId is not { } sideId)
        {
            Console.WriteLine($"[ready] ship {ship.Id} has no side — side-readiness recompute skipped");
            return;
        }
        if (ship.Mission is not Mission mission)
            return;

        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null)
        {
            Console.WriteLine($"[ready] side {sideId} (for ship {ship.Id}) not found in mission — recompute aborted");
            return;
        }

        var peersOnSide = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();

        bool newReady;
        if (peersOnSide.Length == 0)
        {
            // Floor: <1 connected player → forced not-ready (this port's stand-in
            // for native's nMinPlayersPerTeam, fsmission.cpp:5501-5503).
            newReady = false;
        }
        else if (side.ForceReady)
        {
            // P184 — the team leader forced this side ready (FORCE_TEAM_READY(47)).
            // Bypass the per-player check entirely, exactly native
            // CheckForSideAllReady's force-ready branch (fsmission.cpp:5508-5509),
            // still subject to the >=1 connected-player floor above.
            newReady = true;
        }
        else
        {
            newReady = peersOnSide.All(k => _playerReady.TryGetValue(k, out var r) && r);
        }

        if (side.Ready != newReady)
        {
            side.Ready = newReady;
            BroadcastTeamReady(mission, sideId, newReady);

            // After any aggregate change, check whether the whole mission can now
            // start (Domingo's substituted auto-start policy — native leaves the
            // actual start decision to an external admin layer this port lacks).
            TryAutoStart(mission);
        }
    }

/// <summary>P180 — broadcast <c>TEAM_READY(46)</c> for a side whose aggregate
    /// ready flag just changed, to the WHOLE mission (every connected peer *on
    /// that mission*, regardless of side) — the same whole-mission
    /// <c>_peerShips</c> sweep idiom the <c>GAME_OVER</c>/<c>DESTROY_TREASURE</c>
    /// broadcasts use, matching native's <c>GetGroupMission()</c> audience in
    /// <c>SetReady</c> (<c>fsmission.cpp:5455-5462</c>). Only ever called when
    /// the flag actually flipped (the caller guards on <c>side.Ready !=
    /// newReady</c>), so no duplicate spam on every recompute — exactly
    /// native's <c>if (fReady != GetReady(iSide))</c> (<c>fsmission.cpp:5450</c>).
    /// <para><b>Fix (post-landing review):</b> takes <paramref name="mission"/>
    /// and filters to peers on that mission — a server can host multiple
    /// concurrent missions, and this message must not reach peers on an
    /// unrelated mission (their client would misinterpret an unrelated
    /// mission's side id as their own). Matches P178's established per-mission
    /// broadcast scoping.</para></summary>
    private void BroadcastTeamReady(Mission mission, short sideId, bool fReady)
    {
        var msg = new TeamReady { SideId = sideId, FReady = fReady };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = 0;
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;   // whole-mission, not whole-server
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[ready] side {sideId} aggregate readiness {fReady} -> TEAM_READY(46) whole-mission to {n} peer(s)");
    }

/// <summary>P180/P188 — the ready-up auto-start. If the mission is still
    /// <see cref="MissionStage.NotStarted"/> and every side that currently has
    /// at least one connected player is aggregate-ready, begin the real start
    /// countdown (P188): the mission enters <see cref="MissionStage.Starting"/>,
    /// a <c>SET_START_TIME(14)</c> countdown announcement is broadcast, and
    /// once the countdown elapses on a later tick it crosses into
    /// <see cref="MissionStage.Started"/> with the <c>ENTER_GAME(128)</c>
    /// broadcast (AdvanceStartCountdowns → CompleteStartCountdown →
    /// BroadcastEnterGameToMission). This is Domingo's deliberate substitute for
    /// native's external admin/Zone start trigger (<c>AdminGame.cpp</c>), which
    /// this port has ruled N/A — not a literal port of native, per the task's
    /// explicit decision. Native's own start always passes through
    /// <c>STAGE_STARTING</c> (<c>StartGame</c> → <c>StartCountdown</c>,
    /// fsmission.cpp:2231-2239), so routing the auto-start through the countdown
    /// is the faithful form; the P188 writeup records why this stage upgrades
    /// P180's originally-instant auto-start.
    /// <para><b>Fix (post-landing review):</b> the occupied-side check below
    /// is scoped to <paramref name="mission"/> — side ids are per-mission, not
    /// globally unique, so an unscoped scan could see another mission's
    /// same-numbered side's peers and wrongly conclude this mission's side is
    /// "occupied" (or wrongly skip starting because of an unrelated mission's
    /// unready side of the same id).</para></summary>
    private void TryAutoStart(Mission mission)
    {
        if (mission.Stage != MissionStage.NotStarted)
            return;   // already started (or over) — nothing to do
        if (!IsAllOccupiedSidesReady(mission))
            return;   // at least one occupied side is not yet ready

        // P188 — route the ready-up auto-start through the real countdown
        // stage instead of jumping straight to Started. The ENTER_GAME
        // broadcast now happens once the countdown elapses
        // (AdvanceStartCountdowns → CompleteStartCountdown →
        // BroadcastEnterGameToMission), not here; StartCountdown emits the
        // SET_START_TIME countdown announcement and sets Stage = Starting.
        Console.WriteLine("[ready] ALL occupied sides ready — mission auto-start begins countdown");
        StartCountdown(mission, BriefingCountdownSeconds);
    }

/// <summary>P180/P188 — is every side of <paramref name="mission"/> that
    /// currently has at least one connected player aggregate-ready? The port's
    /// own simplified readiness gate, shared by the ready-up auto-start
    /// (<see cref="TryAutoStart"/>) and the manual <c>START_GAME</c> trigger —
    /// a deliberate stand-in for native's full <c>FAllReady</c> (whose
    /// team-balance/rank logic needs <c>nMaxPlayersPerTeam</c>/a rank system
    /// this port lacks). A side with no players is irrelevant to the decision.</summary>
    private bool IsAllOccupiedSidesReady(Mission mission)
    {
        foreach (var side in mission.Sides)
        {
            bool hasPlayers = _peerShips.Any(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == side.Id);
            if (hasPlayers && !side.Ready)
                return false;   // at least one occupied side is not yet ready
        }
        return true;
    }

/// <summary>P188 — begin (or reset) the start countdown for a mission, the
    /// port of native <c>StartCountdown</c>'s wire+stage effect (fsmission.cpp:
    /// 1926-1938). Only valid from <see cref="MissionStage.NotStarted"/>: set
    /// <c>Stage = Starting</c>, record the double-seconds sim target
    /// (<c>now + countdownSeconds</c>), and broadcast <c>SET_START_TIME(14)</c>
    /// whole-mission. Native's remaining <c>StartCountdown</c> side-effects
    /// (<c>GenerateMission</c>, briefing-text broadcast, fsmission.cpp:1940-1980)
    /// are OUT OF SCOPE — this port's bucket/mission generation already happens
    /// once at <c>Mission.Assemble</c> and briefing text is a separate feature.
    /// The eventual <c>Starting → Started</c> transition + <c>ENTER_GAME</c>
    /// broadcast is driven by <see cref="AdvanceStartCountdowns"/> on a later
    /// tick once the target time passes.</summary>
    private void StartCountdown(Mission mission, float countdownSeconds)
    {
        if (mission.Stage != MissionStage.NotStarted)
            return;   // native allows re-starting from STAGE_STARTING too, but
                      // this port has no re-trigger path — refuse

        mission.Stage = MissionStage.Starting;
        double target = _simNow + countdownSeconds;
        _startCountdownDeadline[mission] = target;

        // P309 — native CFSMission::StartCountdown ends in
        // SetStage(STAGE_STARTING) (fsmission.cpp:1976), which broadcasts
        // MISSION_STAGE to the whole mission (fsmission.cpp:1759-1762). Tell
        // every already-joined peer the countdown began, so a peer that joined
        // during the earlier NotStarted hold learns the stage moved on.
        BroadcastMissionStageToMission(mission, MissionStage.Starting);

        var msg = new SetStartTime { TimeStart = (uint)(target * 1000.0) };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[start] mission #{_missions.IndexOf(mission)} countdown begun ({countdownSeconds}s) -> STAGE_STARTING, SET_START_TIME(14) whole-mission to {n} peer(s)");
    }

/// <summary>P188 — the per-tick countdown driver: any mission in
    /// <see cref="MissionStage.Starting"/> whose recorded target start time
    /// has passed crosses into <see cref="MissionStage.Started"/>, reusing the
    /// existing P180 <c>ENTER_GAME</c> broadcast (<see cref="BroadcastEnterGameToMission"/>)
    /// — the exact same Started-transition path the auto-start used to trigger
    /// directly, now deferred by the countdown. Runs on the sim thread right
    /// after the mission tick in <see cref="SimPass"/>.</summary>
    private void AdvanceStartCountdowns(double now)
    {
        foreach (var m in _missions)
        {
            if (m.Stage != MissionStage.Starting) continue;
            if (!_startCountdownDeadline.TryGetValue(m, out var target)) continue;
            if (now < target) continue;
            CompleteStartCountdown(m, now);
        }
    }

private void CompleteStartCountdown(Mission mission, double now)
    {
        mission.Stage = MissionStage.Started;
        _startCountdownDeadline.Remove(mission);

        // P443 — the real Started transition is where the game begins: seed each
        // side's starting treasury and run the immediate first payday (native
        // seeds money at game start, fsmission.cpp:1992-2001, then DoPayday right
        // after entering the game, fsmission.cpp:2278). Deliberately here (not on
        // every Started tick) so a test that flips Stage directly bypasses it —
        // matching native, where only an actual start seeds money. The started-
        // transition time also anchors the periodic cadence (NextPaydayTime =
        // now + 60s) so the next payday is exactly 60s later, never immediately
        // again and never 15s early from the Starting-phase arm.
        mission.ApplyStartingMoneyAndInitialPayday(now);

        Console.WriteLine($"[start] mission #{_missions.IndexOf(mission)} countdown elapsed -> STAGE_STARTED, broadcasting ENTER_GAME");
        BroadcastEnterGameToMission(mission);
        // P309 — native StartGame ends in SetStage(STAGE_STARTED)
        // (fsmission.cpp:2239), broadcasting MISSION_STAGE to the whole mission
        // (fsmission.cpp:1759-1762). Every already-joined peer — especially one
        // that joined during the pre-game hold and was told the real
        // NotStarted/Starting stage at join — must learn the game really started,
        // not just the next new joiner.
        BroadcastMissionStageToMission(mission, MissionStage.Started);
    }

/// <summary>P180 — broadcast <c>ENTER_GAME(128)</c> to every connected peer on
    /// the mission at the ready-up auto-start transition — the same whole-mission
    /// <c>_peerShips</c> sweep the other broadcasts use, matching native
    /// <c>StartGame</c>'s <c>SendMessages(GetGroupRealSides(), ...)</c> audience
    /// (<c>fsmission.cpp:2267-2269</c>). A <b>new</b> broadcast call, distinct
    /// from the per-joiner join-sync send (which is suppressed while the mission
    /// is NotStarted so this transition broadcast is the one that delivers
    /// ENTER_GAME to peers who joined before the start).
    /// <para><b>Fix (post-landing review):</b> takes <paramref name="mission"/>
    /// and filters to peers on that mission — a peer on an unrelated,
    /// still-<c>NotStarted</c> mission must not receive an ENTER_GAME that
    /// doesn't apply to them. Matches P178's established per-mission broadcast
    /// scoping.</para></summary>
    private void BroadcastEnterGameToMission(Mission mission)
    {
        var packet = FedPacket.Pack(new[] { new EnterGame().Encode() });
        int n = 0;
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;   // whole-mission, not whole-server
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[entergame] mission start -> ENTER_GAME(128) whole-mission to {n} peer(s)");
    }

/// <summary>P309 — broadcast <c>MISSION_STAGE(37)</c> to every connected peer
    /// on the mission (a whole-mission <c>_peerShips</c> sweep, the same audience
    /// as <see cref=\"BroadcastEnterGameToMission\"/>). This is the faithful port
    /// of native <c>CFSMission::SetStage</c>'s MISSION_STAGE broadcast
    /// (fsmission.cpp:1759-1762), which tells the whole mission whenever the
    /// stage changes. Without it a peer that joined during the pre-game hold —
    /// told the real NotStarted/Starting stage at join (P309) instead of the old
    /// hardcoded Started lie — would never learn the game actually started.</summary>
    private void BroadcastMissionStageToMission(Mission mission, MissionStage stage)
    {
        var packet = FedPacket.Pack(new[] { new MissionStageMessage { Stage = (int)stage }.Encode() });
        int n = 0;
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;   // whole-mission, not whole-server
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[stage] mission #{_missions.IndexOf(mission)} -> MISSION_STAGE(37) {stage} whole-mission to {n} peer(s)");
    }

/// <summary>P183 — send a pre-packed packet to every currently connected
    /// peer <em>on the given mission</em> (the same whole-mission
    /// <c>_peerShips</c> sweep idiom <see cref="BroadcastTeamReady"/> uses,
    /// matching native's <c>GetGroupMission()</c> audience) and return how many
    /// received it. Shared by all five leadership/join/quit broadcasts so the
    /// multi-mission scoping (the P180/P178 fixed bug — never regress an
    /// unscoped sweep) lives in one place.</summary>
    private int SendToMissionPeers(Mission mission, byte[] packet)
    {
        int n = 0;
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;   // whole-mission, not whole-server
            SendPeer(kv.Key, packet);
            n++;
        }
        return n;
    }

/// <summary>P183 — broadcast <c>JOIN_SIDE(125)</c> announcing that
    /// <paramref name="shipId"/> joined <paramref name="sideId"/>, to the whole
    /// mission (native <c>AddPlayerToSide</c>'s always-sent announcement,
    /// fsmission.cpp:621-624).</summary>
    private void BroadcastJoinSide(Mission mission, short shipId, short sideId)
    {
        var packet = FedPacket.Pack(new[] { new JoinSide { ShipId = shipId, SideId = sideId }.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[join] ship {shipId} -> side {sideId}: JOIN_SIDE(125) whole-mission to {n} peer(s)");
    }

/// <summary>P183 — broadcast <c>SET_TEAM_LEADER(121)</c> announcing
    /// <paramref name="shipId"/> is now <paramref name="sideId"/>'s team
    /// leader, to the whole mission (native fsmission.cpp:626-635 / 1082-1087).
    /// Also re-announces a side's existing leader when that side's leader is
    /// promoted to mission owner (native fsmission.cpp:1083-1086).</summary>
    private void BroadcastSetTeamLeader(Mission mission, short sideId, short shipId)
    {
        var packet = FedPacket.Pack(new[] { new SetTeamLeader { SideId = sideId, ShipId = shipId }.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[lead] side {sideId} new team leader ship {shipId}: SET_TEAM_LEADER(121) whole-mission to {n} peer(s)");
    }

/// <summary>P183 — broadcast <c>SET_MISSION_OWNER(123)</c> announcing
    /// <paramref name="shipId"/> (the leader of <paramref name="sideId"/>) is
    /// now the mission owner, to the whole mission (native fsmission.cpp:
    /// 637-643 / 1088-1094). A mission owner is always also that side's leader.</summary>
    private void BroadcastSetMissionOwner(Mission mission, short sideId, short shipId)
    {
        var packet = FedPacket.Pack(new[] { new SetMissionOwner { SideId = sideId, ShipId = shipId }.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[own] side {sideId} new mission owner ship {shipId}: SET_MISSION_OWNER(123) whole-mission to {n} peer(s)");
    }

/// <summary>P183 — broadcast <c>QUIT_MISSION(124)</c> announcing that
    /// <paramref name="shipId"/> left the mission entirely, to the whole
    /// mission. Emitted on the disconnect path (native
    /// <c>RemovePlayerFromMission</c>, fsmission.cpp:482-488).</summary>
    private void BroadcastQuitMission(Mission mission, short shipId, QuitSideReason reason)
    {
        var packet = FedPacket.Pack(new[] { new QuitMission { ShipId = shipId, Reason = reason }.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[quit] ship {shipId} left mission (reason {reason}): QUIT_MISSION(124) whole-mission to {n} peer(s)");
    }

/// <summary>P183 — broadcast <c>QUIT_SIDE(126)</c> announcing that
    /// <paramref name="shipId"/> left a side while staying in the mission, to
    /// the whole mission (native <c>RemovePlayerFromSide</c>, fsmission.cpp:
    /// 1130-1137). Emitted on the quit-half of a same-mission side switch.</summary>
    private void BroadcastQuitSide(Mission mission, short shipId, QuitSideReason reason)
    {
        var packet = FedPacket.Pack(new[] { new QuitSide { ShipId = shipId, Reason = reason }.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[quitside] ship {shipId} left side (reason {reason}): QUIT_SIDE(126) whole-mission to {n} peer(s)");
    }

// ═══════════════════════════ P187 — ballot / voting system ═══════════════════════════
    // Port of native CFSMission::m_ballots + the four Ballot subclasses
    // (fsmission.h:85-180, fsmission.cpp:5817-6194) and the #chat command
    // triggers (FedSrv.CPP:5170-5200). State lives per-mission on the Server
    // (the P184 pending-join-request convention), driven to resolution from
    // SimPass. See docs/BALLOT_SYSTEM_P187.md.

    /// <summary>Get (creating if absent) the pending-ballot list for a mission.</summary>
    private List<PendingBallot> BallotsOf(Mission mission)
    {
        if (!_ballots.TryGetValue(mission, out var list))
            _ballots[mission] = list = new List<PendingBallot>();
        return list;
    }

/// <summary>Whether a ballot of the given type is already in flight for the
    /// mission — the port of native CFSMission::HasBallots (fsmission.cpp:
    /// 1225-1235): only one in-flight ballot of each type at a time.</summary>
    private bool HasBallotOfType(Mission mission, BallotType type) =>
        BallotsOf(mission).Any(b => b.Type == type);

/// <summary>Native's ballot-command match: cbMessage &gt; strlen(prefix) and
    /// the text starts with the prefix, case-insensitively (FedSrv.CPP:5170-5200
    /// uses <c>_strnicmp</c> over the first strlen(prefix) bytes). On a .NET
    /// string (no trailing NUL) that is Length &gt;= prefix.Length and a
    /// case-insensitive StartsWith.</summary>
    private static bool IsBallotCommand(string message, string prefix) =>
        message.Length >= prefix.Length &&
        message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

/// <summary>Handle a chat line that may be a ballot command. Returns
    /// <c>true</c> iff the line WAS a ballot command — in which case it is NOT
    /// forwarded as chat, mirroring native setting <c>bForward = false</c>
    /// (FedSrv.CPP:5195-5199) whether the ballot was created or refused.
    /// Gates (native FedSrv.CPP:5164-5168): mission must be Started and the
    /// sender seated on a real side.</summary>
    private bool TryHandleBallotCommand(ConnectionId from, Ship ship, string message)
    {
        if (ship.Mission is not { } mission) return false;
        if (mission.Stage != MissionStage.Started) return false;
        if (ship.SideId is not { } sideId) return false;
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null) return false;

        if (IsBallotCommand(message, "#resign"))
        {
            if (!HasBallotOfType(mission, BallotType.Resign))
                CreateBallotFromChat(from, ship, BallotType.Resign);
            else
                SendBallotReply(from, ship.Id, "Resign vote already in progress...");
            return true;
        }

        if (IsBallotCommand(message, "#draw"))
        {
            if (!HasBallotOfType(mission, BallotType.OfferDraw))
                CreateBallotFromChat(from, ship, BallotType.OfferDraw);
            else
                SendBallotReply(from, ship.Id, "Draw vote already in progress...");
            return true;
        }

        if (IsBallotCommand(message, "#mutiny"))
        {
            // "leader cant mutiny" (FedSrv.CPP:5186): a side's leader mutinying
            // against themselves is silently refused — no ballot, no reply.
            if (side.LeaderShipId != ship.Id)
            {
                if (!HasBallotOfType(mission, BallotType.Mutiny))
                    CreateBallotFromChat(from, ship, BallotType.Mutiny);
                else
                    SendBallotReply(from, ship.Id, "Mutiny vote already in progress...");
            }
            return true;
        }

        return false;
    }

/// <summary>Private chat reply to one sender — the port of native
    /// SendChatf(NULL, CHAT_INDIVIDUAL_NOFILTER, shipID, NA, text)
    /// (FedSrv.CPP:5180-5197), sent only to the offending connection.</summary>
    private void SendBallotReply(ConnectionId to, short shipId, string text)
    {
        var msg = new ChatMessage
        {
            Message = text,
            SidSender = shipId,
            ChatTarget = (int)ChatTarget.IndividualNoFilter,
            OidRecipient = shipId,
        };
        SendPeer(to, FedPacket.Pack(new[] { msg.Encode() }));
    }

/// <summary>Create a player-initiated ballot (#resign/#draw/#mutiny) scoped
    /// to the proposer's own side — the port of the three native constructors'
    /// Ballot::Init(pfsInitiator, ...) (fsmission.cpp:5899-5940). Eligible
    /// voters are every seated player on the proposer's side; the proposer's
    /// own vote is recorded in favor after the BALLOT is announced.</summary>
    private void CreateBallotFromChat(ConnectionId from, Ship ship, BallotType type)
    {
        Mission mission = ship.Mission!;
        short sideId = ship.SideId!.Value;
        string name = _peerNames.TryGetValue(from, out var n) ? n : $"Ship {ship.Id}";

        var (proposal, text) = type switch
        {
            BallotType.Mutiny => ($"{name}'s proposal to mutiny", $"{name} has proposed to mutiny.  "),
            BallotType.Resign => ($"{name}'s proposal to resign", $"{name} has proposed resigning.  "),
            _                  => ($"{name}'s proposal to offer a draw", $"{name} has proposed offering a draw.  "),
        };

        var ballot = new PendingBallot
        {
            Id = _nextBallotId++,
            Type = type,
            InitiatorShipId = ship.Id,
            VotingSideId = sideId,
            Proposal = proposal,
            BallotText = text,
            Expiration = SimNow + 30.0,   // native c_fVoteDuration = 30.0f (fsmission.cpp:5894)
        };

        // Eligible: every seated player on the proposer's side (native iterates
        // the side's ships, fsmission.cpp:5907-5920).
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            if (kv.Value.Ship.SideId != sideId) continue;
            ballot.AddEligibleVoter(kv.Value.Ship.Id, sideId);
        }

        BallotsOf(mission).Add(ballot);
        BroadcastBallot(mission, ballot);

        // The proposer votes in favor (native CastVote(pfsInitiator, true), fsmission.cpp:5939).
        ballot.TryCastVote(ship.Id, sideId, true);
    }

/// <summary>Create the mission-wide AcceptDraw ballot after an OfferDraw
    /// passes — the port of native AcceptDrawBallot::Init(CFSSide*, ...)
    /// (fsmission.cpp:5990-6037). Eligible voters are every seated player on the
    /// OTHER sides (the offering side is excluded — it already agreed); the
    /// BALLOT is broadcast to the whole mission (GetGroupRealSides).</summary>
    private void CreateAcceptDrawBallot(Mission mission, short offeringSideId)
    {
        var offeringSide = mission.Sides.FirstOrDefault(s => s.Id == offeringSideId);
        string sideName = offeringSide?.Name ?? $"side{offeringSideId}";

        var ballot = new PendingBallot
        {
            Id = _nextBallotId++,
            Type = BallotType.AcceptDraw,
            InitiatorShipId = -1,
            VotingSideId = -1,                // mission-wide
            ExcludedSideId = offeringSideId,  // the offering side doesn't vote on acceptance
            Proposal = $"{sideName}'s offer of a draw",
            BallotText = $"{sideName} has offered a draw.  ",
            Expiration = SimNow + 30.0,
        };

        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            if (kv.Value.Ship.SideId is not { } sid) continue;
            if (sid == offeringSideId) continue;   // native: sideIDPlayer != sideIDInitiator
            ballot.AddEligibleVoter(kv.Value.Ship.Id, sid);
        }

        BallotsOf(mission).Add(ballot);
        BroadcastBallot(mission, ballot);
    }

/// <summary>Broadcast the BALLOT(17) announcement of a new ballot. A
    /// player-initiated ballot goes to the voting side's peers only (native
    /// sends to the side's group, fsmission.cpp:5937); the mission-wide
    /// AcceptDraw goes to the whole mission (GetGroupRealSides,
    /// fsmission.cpp:5997).</summary>
    private void BroadcastBallot(Mission mission, PendingBallot ballot)
    {
        var msg = new Ballot
        {
            BallotText = ballot.BallotText,
            OtInitiator = ballot.IsMissionWide ? (short)IgcObjectType.Side : (short)IgcObjectType.Ship,
            OidInitiator = ballot.IsMissionWide ? ballot.ExcludedSideId : ballot.InitiatorShipId,
            BallotId = (short)ballot.Id,
            TimeExpiration = (uint)(ballot.Expiration * 1000.0),  // double-seconds → native ms Time
            HideToLeader = ballot.Type == BallotType.Mutiny,      // KGJV #110 (fsmission.h:174): only mutiny hides the proposer
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        if (ballot.IsMissionWide)
        {
            int n = SendToMissionPeers(mission, packet);
            Console.WriteLine($"[ballot] #{ballot.Id} {ballot.Type} whole-mission -> {n} peer(s)");
        }
        else
        {
            int n = 0;
            foreach (var kv in _peerShips)
            {
                if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
                if (kv.Value.Ship.SideId != ballot.VotingSideId) continue;
                SendPeer(kv.Key, packet);
                n++;
            }
            Console.WriteLine($"[ballot] #{ballot.Id} {ballot.Type} side {ballot.VotingSideId} -> {n} peer(s)");
        }
    }

/// <summary>Advance every pending ballot of a mission one resolution step —
    /// the port of native CFSMission::Update's ballot loop (fsmission.cpp:
    /// 2585-2592). A ballot that should resolve now (expiration passed OR every
    /// vote in) is tallied, its pass/fail announced, its type effect applied on
    /// a pass, then dropped. A cancelled ballot is dropped unresolved.</summary>
    private void ResolveBallots(Mission mission, double now)
    {
        var list = BallotsOf(mission);
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var ballot = list[i];
            if (ballot.Canceled)
            {
                list.RemoveAt(i);
                continue;
            }
            if (!ballot.ShouldResolve(now)) continue;

            bool passed = ballot.HasPassed();
            AnnounceBallotResult(mission, ballot, passed);
            if (passed)
                ApplyBallotEffect(mission, ballot);
            list.RemoveAt(i);
        }
    }

/// <summary>Apply the OnPassed side effect of a passing ballot — the port of
    /// the four native OnPassed methods (fsmission.cpp:6100-6194). No
    /// CANCEL_BALLOT is emitted on resolution: the live native server never
    /// sends it (see the CancelBallot codec / the report).</summary>
    private void ApplyBallotEffect(Mission mission, PendingBallot ballot)
    {
        switch (ballot.Type)
        {
            case BallotType.Mutiny:    ApplyMutiny(mission, ballot);    break;
            case BallotType.Resign:    ApplyResign(mission, ballot);    break;
            case BallotType.OfferDraw: ApplyOfferDraw(mission, ballot); break;
            case BallotType.AcceptDraw:ApplyAcceptDraw(mission, ballot);break;
        }
    }

/// <summary>Mutiny passing transfers the side's leadership to the ballot's
    /// initiator — re-validated: the initiator must still be on that side when
    /// the vote closes (leadership could have changed mid-vote). Port of native
    /// MutinyBallot::OnPassed (fsmission.cpp:6117-6168), reusing the P183
    /// <c>Side.LeaderShipId</c> mutation + <c>BroadcastSetTeamLeader</c>. The
    /// native donation-transfer/wing bookkeeping is N/A here (no donations,
    /// no squads in this port).</summary>
    private void ApplyMutiny(Mission mission, PendingBallot ballot)
    {
        if (mission.Stage != MissionStage.Started) return;
        short sideId = ballot.VotingSideId;
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null) return;

        // The initiator must still be on the side when the vote closes (native
        // re-checks pfssNewLeader->GetSide() == m_pside).
        var initiator = _peerShips.Values
            .Select(l => l.Ship)
            .FirstOrDefault(s => s.Id == ballot.InitiatorShipId
                              && ReferenceEquals(s.Mission, mission)
                              && s.SideId == sideId);
        if (initiator is null) return;

        if (side.LeaderShipId == initiator.Id) return;   // already leader — nothing to do

        // P197 — the deposed leader's wallet passes to the new leader as a
        // one-time gift, and the deposed leader's future donations point at the
        // new leader — the port of native MutinyBallot::OnPassed
        // (fsmission.cpp:6121-6136). ORDER MATTERS (and is read from the live
        // function): clear the new leader's own donate target silently first
        // (bSend=false), capture the old leader's wallet, then re-point the OLD
        // leader at the new leader with that wallet as the gift (bSend=true →
        // AUTODONATE(116) side-wide). Native retargets ONLY the deposed leader
        // here — it does NOT loop over the side re-pointing every other donor
        // onto the new leader (that retarget-everyone loop only runs when a
        // player LEAVES a side, fsmission.cpp:1106-1115, not on mutiny). This
        // is a deliberate divergence from the task brief's "retarget every
        // prior donor" phrasing, which does not match the verified native code.
        var oldLeaderShip = LeaderShip(mission, sideId);
        var oldLeaderLive = oldLeaderShip is null ? null : LiveOf(oldLeaderShip);
        var newLeaderLive = LiveOf(initiator);
        if (oldLeaderLive is not null && newLeaderLive is not null)
        {
            SetAutoDonate(newLeaderLive, null, 0, bSend: false);   // clear the new leader's own target, silent
            int money = oldLeaderLive.Wallet.Money;
            SetAutoDonate(oldLeaderLive, initiator, money, bSend: true);  // gift + re-point the old leader
            // The old leader's client still shows his old balance (the gift rides
            // the AUTODONATE amount, which the client applies to the recipient,
            // not to the payer) — native compensates with a MONEY_CHANGE(-money)
            // sent to HIM alone (fsmission.cpp:6131-6136).
            if (ConnOf(oldLeaderShip!) is { } oldConn)
                SendPeer(oldConn, FedPacket.Pack(new[] { new MoneyChange { DMoney = -money, SidTo = oldLeaderShip!.Id, SidFrom = Na }.Encode() }));
        }

        side.LeaderShipId = initiator.Id;
        BroadcastSetTeamLeader(mission, sideId, initiator.Id);
    }

// ════════════════════ P197 — per-player money & auto-donate ════════════════════

    /// <summary>Resolve a <see cref="Ship"/> to its live peer record (the
    /// reverse of <c>_peerShips</c>' forward lookup), or <c>null</c> when the
    /// ship isn't a live player ship (an NPC or disconnected peer — the same
    /// <c>CFSShip::IsPlayer()</c> guard native applies before treating a
    /// donation target as a player).</summary>
    private LiveShip? LiveOf(Ship ship)
    {
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, ship)) return kv.Value;
        return null;
    }

/// <summary>Resolve a <see cref="Ship"/> to the <see cref="ConnectionId"/>
    /// of the peer piloting it, or <c>null</c> when not a live player ship.</summary>
    private ConnectionId? ConnOf(Ship ship)
    {
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, ship)) return kv.Key;
        return null;
    }

/// <summary>The <see cref="Side.LeaderShipId"/> owner as a live
    /// <see cref="Ship"/> on <paramref name="mission"/>/<paramref name="sideId"/>,
    /// or <c>null</c> when the side is leaderless or its leader isn't live.</summary>
    private Ship? LeaderShip(Mission mission, short sideId)
    {
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null || side.LeaderShipId is not { } lid) return null;
        foreach (var l in _peerShips.Values)
            if (ReferenceEquals(l.Ship.Mission, mission) && l.Ship.SideId == sideId && l.Ship.Id == lid)
                return l.Ship;
        return null;
    }

/// <summary>Port of <c>PickNewLeader</c> (<c>FedSrv.CPP:13981-14024</c>): among
    /// the side's ships, count who each one's donation points at, ignoring any
    /// donation that points at the current <paramref name="ignoreLeader"/>; return
    /// the ship with the most such votes, but only if that count strictly exceeds
    /// <paramref name="maxVotes"/> (the leader's own current vote count). Keeps the
    /// first ship to reach each new maximum, matching native's <c>&gt;</c> comparison
    /// (ties resolve to whichever is encountered first).</summary>
    private Ship? PickNewLeader(IEnumerable<Ship> sideShips, Ship ignoreLeader, int maxVotes)
    {
        Ship? pshipMax = null;
        var votes = new Dictionary<Ship, int>();
        foreach (var s in sideShips)
        {
            var t = LiveOf(s)?.Wallet.AutoDonateTarget;
            if (t is null) continue;
            if (ReferenceEquals(t, ignoreLeader)) continue;   // skip donations pointing at the current leader
            votes.TryGetValue(t, out var c);
            c++;
            votes[t] = c;
            if (c > maxVotes) { maxVotes = c; pshipMax = t; }
        }
        return pshipMax;
    }

/// <summary>Resign passing — the stated P187 substitution for native
    /// ResignBallot::OnPassed → DeactivateSide (fsmission.cpp:6170-6180), which
    /// is OUT OF SCOPE (this port has no side deactivation). Instead every
    /// remaining player on the resigning side is withdrawn from the mission,
    /// reusing the P183 BroadcastQuitMission broadcast and the empty-side
    /// leader/owner release: each leaver's ship is removed from its sector (it
    /// stops flying), its side cleared, QUIT_MISSION broadcast whole-mission,
    /// and the peer dropped from the roster; the side's leadership and — if it
    /// owned the mission — the mission-owner role are released exactly as an
    /// emptying side does. This is a deliberate, honest approximation of "this
    /// side is out of the fight". The QUIT_MISSION reason is reused as
    /// <see cref="QuitSideReason.LinkDead"/> because native has no vote-based
    /// resign signal at all (it deactivates, emitting nothing).</summary>
    private void ApplyResign(Mission mission, PendingBallot ballot)
    {
        if (mission.Stage != MissionStage.Started) return;
        short sideId = ballot.VotingSideId;
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null) return;

        // Snapshot the leavers first (removing them below mutates _peerShips).
        var leavers = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var conn in leavers)
        {
            if (!_peerShips.TryGetValue(conn, out var live)) continue;
            if (live.Ship.Sector is { } sec) sec.Remove(live.Ship);
            live.Ship.Sector = null;
            live.Ship.SideId = null;
            BroadcastQuitMission(mission, live.Ship.Id, QuitSideReason.LinkDead);
            _peerShips.Remove(conn);
            _peerNames.Remove(conn);
            _playerReady.Remove(conn);
        }

        // The side is now empty of players: clear its leader; if it owned the
        // mission, migrate the owner role to another occupied side's leader (or
        // null if none) — the same resolution HandleLeaderSuccession reaches for
        // an emptying side.
        side.LeaderShipId = null;
        if (mission.MissionOwnerSideId != sideId) return;
        mission.MissionOwnerSideId = null;
        foreach (var other in mission.Sides)
        {
            if (other.Id == sideId) continue;
            if (other.LeaderShipId is not { } otherLeaderId) continue;
            bool occupied = _peerShips.Any(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == other.Id);
            if (!occupied) continue;
            mission.MissionOwnerSideId = other.Id;
            BroadcastSetMissionOwner(mission, other.Id, otherLeaderId);
            return;
        }
    }

/// <summary>An OfferDraw passing announces the offer and immediately creates
    /// the AcceptDraw ballot scoped to the OTHER side(s) (native OfferDrawBallot::
    /// OnPassed, fsmission.cpp:6182-6188).</summary>
    private void ApplyOfferDraw(Mission mission, PendingBallot ballot)
    {
        if (mission.Stage != MissionStage.Started) return;
        short offeringSide = ballot.VotingSideId;
        SendChatAnnouncement(mission, ballot, "A draw is being offered to the other teams.");
        CreateAcceptDrawBallot(mission, offeringSide);
    }

/// <summary>An AcceptDraw passing ends the game as a draw — native
    /// AcceptDrawBallot::OnPassed → GameOver(NULL, "The game was declared a
    /// draw") (fsmission.cpp:6190-6194). Reuses P178's real draw outcome: the
    /// same Stage→Over flip + whole-mission GAME_OVER(62) broadcast with a NA
    /// winner that CheckForVictoryByInactiveSides builds for its zero-sides case
    /// (Server.cs:1273-1310), not a second game-over path.</summary>
    private void ApplyAcceptDraw(Mission mission, PendingBallot ballot) => DeclareDraw(mission);

/// <summary>Flip a mission to Over as a DRAW and broadcast GAME_OVER(62) to
    /// the whole mission — the reused P178 path (GameOver.ForMission(mission,
    /// NA, reason), the same outcome CheckForVictoryByInactiveSides produces
    /// when no side remains).</summary>
    private void DeclareDraw(Mission mission)
    {
        if (mission.Stage == MissionStage.Over) return;   // idempotent
        // P439 — one shared game-over path: route through DeclareGameOver so
        // the game-over → fresh-mission rotation lives in a single place. Keep
        // the draw-specific log line (the "draw via AcceptDraw ballot" text) and
        // the exact reason string; DeclareGameOver does the Stage→Over flip,
        // the GAME_OVER(62)/GAME_OVER_PLAYERS(63) broadcasts and the P439
        // rotation to a freshly-assembled NotStarted mission.
        Console.WriteLine($"[gameover] mission #{_missions.IndexOf(mission)} -> Stage Over (draw via AcceptDraw ballot)");
        DeclareGameOver(mission, GameOver.NA, "The game was declared a draw");
    }

/// <summary>Announce a ballot's outcome — the port of native Ballot::OnPassed
    /// / OnFailed (fsmission.cpp:6045-6055): "&lt;proposal&gt; has passed /
    /// been defeated. &lt;tally&gt;", sent to the ballot's audience.</summary>
    private void AnnounceBallotResult(Mission mission, PendingBallot ballot, bool passed)
    {
        string verdict = passed ? "has passed." : "has been defeated.";
        SendChatAnnouncement(mission, ballot, $"{ballot.Proposal} {verdict}  {TallyString(ballot)}");
    }

/// <summary>Send a chat announcement to a ballot's audience — CHAT_TEAM for
    /// a side-scoped ballot (native m_chattarget = CHAT_TEAM, fsmission.cpp:
    /// 5905) and CHAT_EVERYONE for the mission-wide AcceptDraw.</summary>
    private void SendChatAnnouncement(Mission mission, PendingBallot ballot, string text)
    {
        var msg = new ChatMessage
        {
            Message = text,
            ChatTarget = (int)(ballot.IsMissionWide ? ChatTarget.Everyone : ChatTarget.Team),
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        if (ballot.IsMissionWide)
        {
            int n = SendToMissionPeers(mission, packet);
            Console.WriteLine($"[ballot] #{ballot.Id} announcement whole-mission -> {n} peer(s)");
        }
        else
        {
            int n = 0;
            foreach (var kv in _peerShips)
            {
                if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
                if (kv.Value.Ship.SideId != ballot.VotingSideId) continue;
                SendPeer(kv.Key, packet);
                n++;
            }
            Console.WriteLine($"[ballot] #{ballot.Id} announcement side {ballot.VotingSideId} -> {n} peer(s)");
        }
    }

/// <summary>The port of native Ballot::GetTallyString (fsmission.cpp:
    /// 5970-6007): a per-side summary of in-favor/opposed/abstained counts.
    /// Compact form "(sideName: N for, M against, K abstained)".</summary>
    private string TallyString(PendingBallot ballot)
    {
        var parts = new List<string>();
        for (int s = 0; s < PendingBallot.SideSlots; s++)
        {
            if (ballot.Abstaining[s] + ballot.InFavor[s] + ballot.Opposed[s] == 0) continue;
            var bits = new List<string>();
            if (ballot.InFavor[s] > 0) bits.Add($"{ballot.InFavor[s]} for");
            if (ballot.Opposed[s] > 0) bits.Add($"{ballot.Opposed[s]} against");
            if (ballot.Abstaining[s] > 0) bits.Add($"{ballot.Abstaining[s]} abstained");
            parts.Add($"({string.Join(", ", bits)})");
        }
        return string.Join(", ", parts);
    }

/// <summary>P184 — broadcast <c>POSITIONREQ(40)</c> announcing a pending
    /// side-join request (that needs leader approval), to the whole mission
    /// (native <c>RequestPosition</c>'s broadcast, fsmission.cpp:4765-4769).</summary>
    private void BroadcastPositionReq(Mission mission, short shipId, short sideId)
    {
        var packet = FedPacket.Pack(new[] { new PositionReqBroadcast { ShipId = shipId, SideId = sideId }.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[posreq] ship {shipId} -> side {sideId}: POSITIONREQ(40) whole-mission to {n} peer(s)");
    }

/// <summary>P184 — broadcast <c>DELPOSITIONREQ(42)</c> announcing that a
    /// pending side-join request is gone (rejected/canceled/dropped), to the
    /// whole mission. The port of native <c>NotifyPlayerBoot</c>'s whole-mission
    /// <c>DELPOSITIONREQ(DPR_Rejected)</c> (fsmission.cpp:4881-4891), generalised
    /// to carry whatever reason the caller needs.</summary>
    private void BroadcastDelPositionReq(Mission mission, short shipId, short sideId, DelPositionReqReason reason)
    {
        var packet = FedPacket.Pack(new[] { new DelPositionReq { ShipId = shipId, SideId = sideId, Reason = reason }.Encode() });
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[delreq] ship {shipId} side {sideId} reason {reason}: DELPOSITIONREQ(42) whole-mission to {n} peer(s)");
    }

/// <summary>P184 — the <see cref="ConnectionId"/> of the connected peer whose
    /// ship is <paramref name="shipId"/> <em>on</em> <paramref name="mission"/>,
    /// or <c>null</c> if no such connected ship exists. Used to resolve a
    /// ship-id from a <c>POSITIONACK</c>/<c>DELPOSITIONREQ</c> to the pending-
    /// request key (ship ids are per-mission, so the mission filter is the
    /// multi-mission scoping guard).</summary>
    private ConnectionId? FindConnectionOfShip(Mission mission, short shipId)
    {
        foreach (var kv in _peerShips)
            if (kv.Value.Ship.Id == shipId && ReferenceEquals(kv.Value.Ship.Mission, mission))
                return kv.Key;
        return null;
    }

/// <summary>P184 — remove the pending join request (if any) that
    /// <paramref name="requesterConn"/>'s ship has on <paramref name="sideId"/>.
    /// Returns whether one was present and removed.</summary>
    private bool RemovePendingJoinRequest(ConnectionId requesterConn, short sideId)
    {
        if (_pendingJoinRequests.TryGetValue(requesterConn, out var pend) && pend.SideId == sideId)
        {
            _pendingJoinRequests.Remove(requesterConn);
            return true;
        }
        return false;
    }

/// <summary>P186 — is <paramref name="ship"/> the current <b>mission owner</b>
    /// of <paramref name="mission"/>? The port of native <c>CFSMission::GetOwner()</c>
    /// (the mission-owner side's leader; <c>fedmsgs</c>'s LOCK_LOBBY/LOCK_SIDES gate,
    /// <c>FedSrv.CPP:6940,6955</c>). True iff the ship is on the owning side
    /// (<c>Mission.MissionOwnerSideId</c>, the P183 concept) AND is that side's
    /// current leader. A mission with no owner, or a side leader on a non-owning
    /// side, is not the owner.</summary>
    private bool IsMissionOwnerPlayer(Ship ship, Mission mission)
    {
        if (mission.MissionOwnerSideId is not { } ownerSideId) return false;
        if (ship.SideId != ownerSideId) return false;
        var ownerSide = mission.Sides.FirstOrDefault(s => s.Id == ownerSideId);
        return ownerSide is not null && ownerSide.LeaderShipId == ship.Id;
    }

/// <summary>P195 — is <paramref name="ship"/> the current <b>leader</b> of
    /// the real side it is seated on in <paramref name="mission"/>? Used to
    /// filter the rebalance pool: native's <c>RandomizeSides</c> never moves
    /// team leaders (<c>fsmission.cpp:5161-5182</c>, <c>GetLeader(sideID) !=
    /// pfsPlayer</c>). A side-less ship, or a ship seated on a side it does not
    /// lead, is not a leader.</summary>
    private static bool IsSideLeader(Mission mission, Ship ship)
    {
        if (ship.SideId is not { } sideId) return false;
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        return side is not null && side.LeaderShipId == ship.Id;
    }

/// <summary>P195 — the port of native <c>CFSMission::GetSideRankSum</c>
    /// (<c>fsmission.cpp:1353-1365</c>) restricted to a single real side: the
    /// sum of every <b>leader</b> <c>PlayerScore.Rank</c> on that side, floored
    /// at 1 per player (<c>iRankSum += (iTempRank &lt; 1) ? 1 : iTempRank</c>).
    /// Because the Stage-1 rebalance moves players one-at-a-time into a running
    /// per-side sum (not a physical lobby flush), this counts only the players
    /// already anchored to the side — the leaders, who are never moved. The
    /// just-placed pool members are folded into the running sum directly by
    /// <see cref="RebalanceSides"/>, matching native's live recomputation via
    /// <c>AddPlayerToSide</c> before the next loop iteration. This port has no
    /// ghost/spectator concept, so <c>bCountGhosts</c> is moot (sum every
    /// seated leader).</summary>
    private int GetLeaderRankSum(Mission mission, short sideId)
    {
        int sum = 0;
        foreach (var kv in _peerShips)
        {
            var ship = kv.Value.Ship;
            if (ReferenceEquals(ship.Mission, mission) && ship.SideId == sideId
                && IsSideLeader(mission, ship))
                sum += Math.Max(kv.Value.PlayerScore.Rank, (short)1);
        }
        return sum;
    }

/// <summary>P195 — the <c>RandomizeSides</c> port (<c>fsmission.cpp:5133-
    /// 5260</c>) as the Stage-1 substitute the brief specifies. Native's
    /// two-phase flush-then-reassign (move every non-leader to a neutral
    /// <c>SIDE_TEAMLOBBY</c>, then greedily re-seat them) cannot be ported
    /// because this port has no lobby side — every ship is always seated on a
    /// real side. The buildable substitute rebalances <b>directly across the
    /// existing real sides in one pass</b>: collect every non-leader seated on
    /// any real side (analogous to native's lobby population, never physically
    /// moved first), then repeatedly seat the highest-<c>Rank</c> remaining
    /// player onto whichever real side currently has the lowest rank sum
    /// (ties broken by connection/iteration order via LINQ's stable
    /// <c>OrderBy</c>). The end state — players redistributed to minimize
    /// per-side rank-sum imbalance — is equivalent to native; the mechanism —
    /// no visible "everyone stripped to a lobby" intermediate state — is not.
    /// <para><b>Step 3 faithful</b>: pending join requests (P184) for this
    /// mission are cleared before any reassignment (native
    /// <c>fsmission.cpp:5183-5196</c>), since a mass reassignment would
    /// otherwise leave stale pending entries. <b>LockSides bypass</b>: the
    /// dispatch intentionally does not refuse while sides are locked (native
    /// never consults bLockSides here). Reassignment broadcasts ride the
    /// existing per-player join/leave announcements inside
    /// <see cref="TryJoinSide"/> (QUIT_SIDE / JOIN_SIDE / SET_TEAM_LEADER /
    /// SET_MISSION_OWNER) — no new broadcast shape is invented. Simplifications
    /// (native concepts absent here, all stated in docs/RANDOMIZE_TEAMS_P195.md):
    /// no banned-side mask, no AFK/ready filter (every non-leader is
    /// rebalanced), no ghost counting, and no <c>iMaxImbalance</c> player-count
    /// override (pure rank-sum minimization).</summary>
    private void RebalanceSides(Mission mission)
    {
        // Step 3 (native fsmission.cpp:5183-5196): clear this mission's pending
        // join requests before reassigning — a mass reassignment would leave
        // stale entries otherwise.
        ClearPendingJoinRequestsForMission(mission);

        // Running per-side rank sums, seeded with the leaders' contributions
        // (leaders are never moved; their rank counts from the start — the
        // equivalent of native's sides still holding their leaders after the
        // non-leader flush). Computed fresh at the top so a leader's
        // contribution is always anchored.
        var sums = new Dictionary<short, int>();
        foreach (var side in mission.Sides)
            sums[side.Id] = GetLeaderRankSum(mission, side.Id);

        // The rebalance pool: every non-leader seated on any real side of this
        // mission, ranked highest-first (LINQ OrderBy is stable, so equal ranks
        // keep connection/iteration order — the port's "arrival order" tie-break,
        // since all PlayerScores seed at Rank 0 per P193 and only a future
        // feature ever differentiates them).
        var pool = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
            .Where(kv => kv.Value.Ship.SideId is not null)
            .Where(kv => !IsSideLeader(mission, kv.Value.Ship))
            .Select(kv => (Ship: kv.Value.Ship, Rank: kv.Value.PlayerScore.Rank))
            .OrderByDescending(x => x.Rank)
            .ToList();

        foreach (var (ship, rank) in pool)
        {
            // Whichever real side currently has the lowest rank sum gets this
            // player (every side is eligible — no banned-side mask exists).
            short bestSide = -1;
            int bestSum = int.MaxValue;
            foreach (var side in mission.Sides)
            {
                if (!sums.TryGetValue(side.Id, out var s)) s = 0;
                if (s < bestSum) { bestSum = s; bestSide = side.Id; }
            }
            if (bestSide == -1) break;   // no real side — nothing to seat onto

            // Fold the newcomer into the running sum immediately (native adds
            // via AddPlayerToSide before the next PickNewSide), then move them.
            sums[bestSide] = bestSum + Math.Max(rank, (short)1);
            TryJoinSide(ship, bestSide);   // announces QUIT_SIDE / JOIN_SIDE / leader changes
        }

        Console.WriteLine($"[randomizeteams] rebalanced {pool.Count} non-leader player(s) across {mission.Sides.Count} side(s)");
    }

/// <summary>P195 — clear every pending join request (P184) belonging to a
    /// player in <paramref name="mission"/>, scoped to that mission (pending
    /// requests are keyed by connection, so the mission filter requires mapping
    /// each key to its live ship). Part of <see cref="RebalanceSides"/> step 3.</summary>
    private void ClearPendingJoinRequestsForMission(Mission mission)
    {
        int removed = 0;
        foreach (var key in _pendingJoinRequests.Keys.ToList())
        {
            if (_peerShips.TryGetValue(key, out var live) && ReferenceEquals(live.Ship.Mission, mission))
            {
                _pendingJoinRequests.Remove(key);
                removed++;
            }
        }
        Console.WriteLine($"[randomizeteams] cleared {removed} pending join request(s)");
    }

/// <summary>P190 — a <c>CHANGE_ALLIANCE</c> side id is acceptable iff it is
    /// <c>NA</c> (-1) or a valid side id in <c>[0, sideCount)</c>, mirroring the
    /// native filter in <c>UpdateAlliances</c> (<c>fsmission.cpp:5642-5647</c>).</summary>
    private static bool IsValidAllianceId(short id, int sideCount) =>
        id == -1 || (id >= 0 && id < sideCount);

/// <summary>
    /// P190 — the port of native <c>CFSMission::UpdateAlliances</c>
    /// (<c>fsmission.cpp:5638-5811</c>, read in full this session; the merge /
    /// clear algorithm is quoted in docs/ALLIANCES_P190.md). Given a requested
    /// <c>sideID</c>/<c>sideAlly</c> pair (already range- and owner- and
    /// stage-gated by the caller), re-derives the whole mission's alliance
    /// groups from the current per-side state, applies the requested change
    /// (ally <c>sideID</c> to <c>sideAlly</c>, clear <c>sideID</c>'s alliances
    /// when <c>sideAlly == NA</c>, or — when <c>sideID == NA</c> — just
    /// re-normalize the existing state), stores the result back on each
    /// <see cref="Side.AllyGroup"/>, and <b>always</b> broadcasts
    /// <c>CHANGE_ALLIANCES(197)</c> whole-mission.
    /// <para><b>Faithfulness notes (divergences from this prompt's assumptions,
    /// verified against the live native source):</b> (1) native sends the
    /// broadcast <em>unconditionally</em> at the end of the function
    /// (<c>fsmission.cpp:5797-5807</c>) — there is <b>no</b> "only if the array
    /// changed" guard, contrary to the task brief's guess. (2) The final
    /// validation rule (<c>fsmission.cpp:5786-5792</c>) clears ALL alliances
    /// when the result is a single group covering every side with no solo team
    /// group-swapped — so "merge three sides into one group" is only a valid
    /// outcome when a fourth side remains solo. (3) "Active" sides map to
    /// <see cref="Side.Active"/> (the P202 field, native <c>rgfActive</c> /
    /// <c>GetSideActive</c>): every side starts active, and the
    /// <c>SIDE_INACTIVE(68)</c> pre-game toggle can deactivate a side — which
    /// then contributes nothing to the <c>activesides &gt;= 3</c> gate and
    /// cannot hold alliances (native <c>fsmission.cpp:5675,5691,5783</c>).
    /// </summary>
    private void UpdateAlliances(Mission mission, short sideID, short sideAlly)
    {
        var sides = mission.Sides;
        int n = sides.Count;
        const int AlliancesMax = ChangeAlliances.SideAlliesMax / 2;   // c_cAlliancesMax = 3 (igc.h:508)
        const sbyte Na = -1;

        // Convert the current alliance groups to per-side bitmasks: each side's
        // own bit plus every other side sharing its group value.
        var masks = new int[n];
        for (int i = 0; i < n; i++)
        {
            sbyte? g = sides[i].AllyGroup;
            if (g.HasValue)
            {
                masks[i] = 1 << i;
                for (int j = 0; j < n; j++)
                    if (sides[j].AllyGroup == g)
                        masks[i] |= 1 << j;
            }
            else masks[i] = 0;
        }

        if (sideID != Na)   // we got a valid parameter sideID
        {
            // native: an INACTIVE side forms/keeps no alliances
            // (GetSideActive(sideID) && sideAlly != NA, fsmission.cpp:5675).
            if (sides[sideID].Active && sideAlly != Na)
                masks[sideID] = masks[sideID] | (1 << sideAlly) | (1 << sideID);   // new allies = current + sideAlly + itself
            else
                masks[sideID] = 0;
        }

        // Propagate the alliances & count active sides.
        int activesides = 0;
        for (int i = 0; i < n; i++)
        {
            if (!sides[i].Active)             // inactive side: clear its alliances, don't count (fsmission.cpp:5691)
            {
                masks[i] = 0;
                continue;
            }
            activesides++;
            if (sideID == Na) continue;
            if (i == sideID) continue;
            if ((masks[sideID] & (1 << i)) != 0)   // sideID says it's allied with i: merge
            {
                int merged = masks[sideID] | masks[i];
                masks[sideID] = merged;
                masks[i] = merged;
            }
            else
                masks[i] = masks[i] & ~(1 << sideID);   // make sure i isn't allied with sideID
        }

        // Build the final allied groups.
        var allies = new sbyte[n];
        for (int i = 0; i < n; i++) allies[i] = Na;

        if (activesides >= 3)   // need at least 3 active teams for alliances
        {
            // Reset allies for solo teams (remove them from their own mask).
            for (int i = 0; i < n; i++)
                if (masks[i] == (1 << i)) masks[i] = 0;

            // Propagate the alliances till it's stable.
            bool stop;
            do
            {
                stop = true;
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                    {
                        int ai = masks[i], aj = masks[j];
                        if ((ai & aj) != 0)              // i & j have common allies
                        {
                            int merged = ai | aj;
                            if (ai != merged || aj != merged)
                            {
                                stop = false;
                                masks[i] = merged;
                                masks[j] = merged;
                            }
                        }
                    }
            } while (!stop);

            // Create the different allied groups.
            var groups = new int[AlliancesMax];
            int soloteams = 0, numgroups = 0;
            for (int i = 0; i < n; i++)
            {
                if (masks[i] != 0)   // this side has allies: find its group or build a new one
                {
                    bool found_group = false;
                    for (int g = 0; g < AlliancesMax; g++)
                    {
                        if (masks[i] == groups[g])
                        {
                            allies[i] = (sbyte)g;
                            found_group = true;
                        }
                    }
                    if (!found_group)
                    {
                        groups[numgroups] = masks[i];
                        allies[i] = (sbyte)numgroups;
                        numgroups++;
                    }
                }
                else
                {
                    allies[i] = Na;    // this team has no allies
                    if (sides[i].Active) soloteams++;   // solo team only if active (fsmission.cpp:5783)
                }
            }

            // Validation: no group at all, OR no solo teams and only 1 group →
            // invalid alliances → clear everything.
            if (numgroups == 0 || (soloteams == 0 && numgroups == 1))
                for (int i = 0; i < n; i++) allies[i] = Na;
        }

        // Here we have valid alliances (in allies[]) — save them and broadcast.
        for (int i = 0; i < n; i++)
            sides[i].AllyGroup = allies[i] == Na ? null : allies[i];

        var wire = new ChangeAlliances();
        for (int i = 0; i < ChangeAlliances.SideAlliesMax; i++)
            wire.Allies[i] = i < n ? allies[i] : Na;
        byte[] packet = FedPacket.Pack(new[] { wire.Encode() });
        int cnt = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[alliances] mission #{_missions.IndexOf(mission)}: CHANGE_ALLIANCES(197) whole-mission to {cnt} peer(s) -> [{string.Join(", ", allies)}]");
    }

/// <summary>P186 — when a side's auto-accept turns <b>on</b>, immediately seat
    /// every player with a pending join request targeting that side. The port of
    /// native <c>SetAutoAccept</c>'s accept-sweep (<c>fsmission.cpp:4982-5009</c>):
    /// each pending entry on the side is removed and its requester re-resolved via
    /// <c>RequestPosition</c> — which, with auto-accept now on, seats them. Here the
    /// equivalent is <see cref="TryJoinSide"/> directly (the accept logic is
    /// P184's, reused rather than duplicated). The pending entry is removed
    /// <em>regardless</em> of outcome, matching native's unconditional
    /// <c>delete plinkJR</c> / "should never add the player back to the list, since
    /// autoaccept is on". A requester whose connection left meanwhile (no live
    /// ship) is simply dropped from the map without seating.</summary>
    private void SweepAutoAcceptPending(Mission mission, short sideId)
    {
        int seated = 0, dropped = 0;
        foreach (var pend in _pendingJoinRequests.Where(kv => kv.Value.SideId == sideId).ToList())
        {
            var targetConn = pend.Key;
            _pendingJoinRequests.Remove(targetConn);   // removed regardless of outcome
            if (_peerShips.TryGetValue(targetConn, out var live) && ReferenceEquals(live.Ship.Mission, mission))
            {
                TryJoinSide(live.Ship, sideId);
                seated++;
            }
            else
            {
                dropped++;
            }
        }
        Console.WriteLine($"[autoaccept] sweep side {sideId}: seated {seated} pending requester(s), dropped {dropped} stale");
    }

/// <summary>P183 — how many connected players are on <paramref name="sideId"/>
    /// of <paramref name="mission"/>, optionally excluding <paramref name="exclude"/>
    /// (the leaver), computed on demand by filtering <c>_peerShips</c> — the same
    /// per-side live-count style P180's <see cref="RecomputeSideReady"/> uses
    /// (Server.cs:2782-2785), mission-scoped per the fixed P180/P178 bug. This
    /// port keeps no running per-side player counter (native's
    /// <c>m_misdef.rgcPlayers[sideID]</c>), deliberately — one maintenance
    /// mechanism, computed where needed.</summary>
    private int CountPlayersOnSideExcluding(Mission mission, short sideId, Ship exclude) =>
        _peerShips.Count(kv => ReferenceEquals(kv.Value.Ship.Mission, mission)
                            && kv.Value.Ship.SideId == sideId
                            && !ReferenceEquals(kv.Value.Ship, exclude));

/// <summary>
    /// P183 — the <c>RemovePlayerFromSide</c> port's leader-succession branch
    /// (native fsmission.cpp:848-1099), applied when <paramref name="ship"/>
    /// leaves a side (whether by disconnecting or by switching sides). No-op
    /// unless <paramref name="ship"/> was its side's leader. Three outcomes,
    /// matching native:
    /// <list type="number">
    /// <item><b>Teammates remain</b> — promote the deterministic "first other
    /// player still on the side" (lowest ship id; native's plain list scan,
    /// fsmission.cpp:972-990) to leader, broadcasting <c>SET_TEAM_LEADER</c>
    /// (and <c>SET_MISSION_OWNER</c> if this side is the mission owner).</item>
    /// <item><b>Side empty &amp; was the mission owner</b> — migrate the owner
    /// role to another occupied side's existing leader (broadcasting
    /// <c>SET_TEAM_LEADER</c>/<c>SET_MISSION_OWNER</c> for them), or clear it
    /// with no broadcast if no other occupied side exists.</item>
    /// <item><b>Side empty &amp; not the owner</b> — just clear the side's
    /// leader, no broadcast.</item>
    /// </list>
    /// Stated simplifications (per P183 scope, never silent): leader-succession
    /// always uses "first other player on the side", dropping native's
    /// donation-based (<c>PickNewLeader</c>) and squad-capability
    /// (<c>GetCanLeadSquad</c>) tie-breaks — this port has neither donations
    /// (P119) nor squads; the lobby/side-unlock side effect native does when the
    /// owner vanishes (fsmission.cpp:1031-1062) is a deliberate no-op
    /// (<c>LOCK_LOBBY</c>/<c>LOCK_SIDES</c> aren't ported); rank-range/squad-name
    /// resets are omitted for the same reason.
    /// </summary>
    private void HandleLeaderSuccession(Ship ship)
    {
        if (ship.Mission is not Mission mission) return;
        if (ship.SideId is not { } oldSideId) return;
        var side = mission.Sides.FirstOrDefault(s => s.Id == oldSideId);
        if (side is null) return;
        if (side.LeaderShipId != ship.Id) return;   // only the leader's exit triggers succession

        // Teammates still on the side, excluding the leaver (native decrements
        // rgcPlayers[sideidOld] before checking, fsmission.cpp:917/928). Ordered
        // by ship id so "first other player" is deterministic and testable.
        var remaining = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission)
                      && kv.Value.Ship.SideId == oldSideId
                      && !ReferenceEquals(kv.Value.Ship, ship))
            .Select(kv => kv.Value.Ship)
            .OrderBy(s => s.Id)
            .ToList();

        if (remaining.Count > 0)
        {
            var newLeader = remaining[0];
            side.LeaderShipId = newLeader.Id;
            BroadcastSetTeamLeader(mission, oldSideId, newLeader.Id);
            if (mission.MissionOwnerSideId == oldSideId)
                BroadcastSetMissionOwner(mission, oldSideId, newLeader.Id);

            // P197 — retarget donations when leadership passes (native
            // RemovePlayerFromSide's leader-change block, fsmission.cpp:
            // 1106-1115): clear the new leader's own target if it pointed at the
            // departing leader, then re-point every player who was donating to
            // the departing leader onto the new leader. (Native runs these with
            // bSend=false and one side-group flush; passing bSend:true for each
            // reproduces the identical net AUTODONATE(116) side-group broadcast.)
            RetargetDonorsOffLeaver(mission, oldSideId, ship, newLeader);
            return;
        }

        // Side is now empty: clear its leader, then migrate the owner role if needed.
        side.LeaderShipId = null;
        if (mission.MissionOwnerSideId != oldSideId)
            return;   // wasn't the mission owner — nothing more to do

        mission.MissionOwnerSideId = null;
        foreach (var other in mission.Sides)
        {
            if (other.Id == oldSideId) continue;
            if (other.LeaderShipId is not { } otherLeaderId) continue;   // an occupied side always has a leader
            bool occupied = _peerShips.Any(kv => ReferenceEquals(kv.Value.Ship.Mission, mission)
                                              && kv.Value.Ship.SideId == other.Id);
            if (!occupied) continue;
            mission.MissionOwnerSideId = other.Id;
            BroadcastSetTeamLeader(mission, other.Id, otherLeaderId);
            BroadcastSetMissionOwner(mission, other.Id, otherLeaderId);
            return;
        }
        // No other occupied side: the mission has no owner — nothing to announce.
        // (Native unlocks the lobby/sides here via LOCK_LOBBY/LOCK_SIDES;
        //  neither is ported — separate, unscoped item — so it is a deliberate no-op.)
    }

/// <summary>
    /// P183 — the <c>AddPlayerToSide</c> port (native fsmission.cpp:539-661) as a
    /// directly-callable side-switch capability: seat <paramref name="ship"/>
    /// (already on <paramref name="ship"/>.Mission) on the given target side in
    /// the same mission, maintaining team-leader and mission-owner status exactly
    /// as native does. Returns <c>false</c> (no state change) if the ship has no
    /// mission or the target side does not exist in it.
    /// <para>Sequence, mirroring native: if the ship was on a real side, quit it
    /// first (<c>RemovePlayerFromSide(QSR_SwitchingSides)</c>, fsmission.cpp:
    /// 562-563) — running leader succession and broadcasting <c>QUIT_SIDE</c> —
    /// then join the new side: determine the new leader (first player onto an
    /// empty side) and mission owner (iff no side owns the mission or the owning
    /// side has no players — always alongside a new leader,
    /// <c>assert(IMPLIES(fMissionOwner, fTeamLeader))</c>), update
    /// <see cref="Side.LeaderShipId"/> / <see cref="Mission.MissionOwnerSideId"/>,
    /// and broadcast <c>JOIN_SIDE</c> always plus <c>SET_TEAM_LEADER</c> /
    /// <c>SET_MISSION_OWNER</c> when applicable — all to the whole mission.</para>
    /// <para>Not wired to any live client message yet (P184's position-request
    /// flow will trigger it); it exists as a directly-callable server capability,
    /// matching this session's "capability first, live-input wiring later"
    /// pattern. The ship leaves the old side even if the target is unoccupied;
    /// switching to the side the ship is already on re-joins it (a harmless
    /// re-announcement, still running the new-leader check for an empty side).</para>
    /// </summary>
    internal bool TryJoinSide(Ship ship, short newSideId)
    {
        if (ship.Mission is not Mission mission) return false;
        var targetSide = mission.Sides.FirstOrDefault(s => s.Id == newSideId);
        if (targetSide is null) return false;

        // Quit the old side first (native AddPlayerToSide, fsmission.cpp:562-563).
        short? oldSideId = ship.SideId;
        if (oldSideId is { } os && os != newSideId)
        {
            HandleLeaderSuccession(ship);
            BroadcastQuitSide(mission, ship.Id, QuitSideReason.SwitchingSides);
        }

        // New-side leadership: the first player onto an empty side becomes its
        // leader (native !HasPlayers(pside, true), fsmission.cpp:601-613).
        bool newLeader = CountPlayersOnSideExcluding(mission, newSideId, ship) == 0;
        bool newMissionOwner = false;
        if (newLeader)
        {
            // Mission owner iff no side owns it, or the owning side has no players
            // (native fsmission.cpp:584-592). Only ever alongside a new leader.
            if (mission.MissionOwnerSideId is null)
            {
                newMissionOwner = true;
            }
            else
            {
                var ownerSide = mission.Sides.FirstOrDefault(s => s.Id == mission.MissionOwnerSideId.Value);
                if (ownerSide is null || CountPlayersOnSideExcluding(mission, ownerSide.Id, ship) == 0)
                    newMissionOwner = true;
            }
            targetSide.LeaderShipId = ship.Id;
            if (newMissionOwner)
                mission.MissionOwnerSideId = newSideId;
        }

        ship.SideId = newSideId;

        // P324 (M4): a peer seating on this side must get an immediate per-side
        // object sync (stations/asteroids/probes) rather than wait for the
        // every-10 cadence — the faithful "send on cluster entry" native behavior.
        MarkObjectSyncDirty(newSideId);

        BroadcastJoinSide(mission, ship.Id, newSideId);
        if (newLeader)
        {
            BroadcastSetTeamLeader(mission, newSideId, ship.Id);
            if (newMissionOwner)
                BroadcastSetMissionOwner(mission, newSideId, ship.Id);
        }

        // P197 — default auto-donate-to-leader on join (native AddPlayerToSide,
        // fsmission.cpp:720-723): only when the mission is Started/Starting, and
        // only for a non-leader (the leader itself donates to no one — a leader
        // created by this very join has its target left null). bSend=true, so the
        // side hears the relationship, exactly native's (..., 0, true).
        if (mission.Stage is MissionStage.Started or MissionStage.Starting)
        {
            var joinerLive = LiveOf(ship);
            if (joinerLive is not null)
            {
                Ship? leader = newLeader ? null : LeaderShip(mission, newSideId);
                SetAutoDonate(joinerLive, leader, 0, bSend: true);
            }
            // P197 — resync: tell the joining peer every teammate's donate
            // relationship + wallet (native SendMissionInfo's money/autodonate
            // loop, fsmission.cpp:4276-4302).
            SyncMoneyStateToJoiner(mission, newSideId, ConnOf(ship));
        }

        // P468 — a player who actually SWITCHES sides (a real prior side; the
        // port's team-lobby analogue is a null SideId, i.e. the very first seat
        // whose CREATE_BUCKETS CompleteJoinAndLaunch already sends) must learn
        // the NEW side's development-tech mask. Native AddPlayerToSide re-runs
        // SendMissionInfo for the switcher (fsmission.cpp:736), which re-sends
        // CREATE_BUCKETS + BUCKET_STATUS to its own connection
        // (fsmission.cpp:4255-4273). Without this the client's
        // DevelopmentTechsBySideId stayed keyed to the OLD side and the docked
        // shop offered everything (fail-open). Sent AFTER the JOIN_SIDE
        // broadcast above so on the same FIFO connection the client first
        // updates MySideId to the new side (P438 Apply(JoinSide)) and then
        // stores this CREATE_BUCKETS under that new side.
        if (oldSideId is { } prev && prev != newSideId && ConnOf(ship) is { } sideConn)
            SendSideTechToPeer(sideConn, targetSide);

        return true;
    }

/// <summary>
    /// Pick the peers a chat message is relayed to, per the five wired targets.
    /// <b>Everyone</b> (0) → every connected peer ship (the same loop the ship-
    /// state broadcast walks, Server.cs:305-323). <b>Team</b> (5) → only peers
    /// whose <see cref="Ship.SideId"/> equals the sender's. <b>Individual</b> /
    /// <b>Individual_NoFilter</b> (6,7) → only the peer ship whose
    /// <see cref="Ship.Id"/> equals <see cref="ChatMessage.OidRecipient"/> and,
    /// like Team, only if that ship shares the sender's side — the real
    /// <c>ForwardChatMessage</c> (<c>FedSrv.CPP:3538-3556</c>) treats both
    /// targets identically in one case block and additionally allows allied
    /// sides, but this codebase has no alliance system, so same-side-only is
    /// the correct simplification (matching Team's precedent); the
    /// "NoFilter" distinction is about bypassing mute/ignore lists, and this
    /// server has none, so the two targets collapse into one case. <b>AllSector</b>
    /// (10) → every peer whose ship is currently in the sector named by
    /// <see cref="ChatMessage.OidRecipient"/> (the client picks the sector —
    /// <c>GetCluster(pcd->oidRecipient)</c>, <c>FedSrv.CPP:3428-3441</c>),
    /// regardless of side. <b>FriendlySector</b> (11) → the same sector
    /// membership but only peers sharing the sender's side (<c>FedSrv.CPP:3392-3424</c>;
    /// allied-side allowance in the real code reduced to same-side here).
    /// <b>Group</b> / <b>GroupNoEcho</b> (12,13) → aliases for Individual, per
    /// the real server's rewrite-and-fall-through (<c>FedSrv.CPP:3531-3536</c>).
    /// Every other <see cref="ChatMessage.ChatTarget"/> value is logged plainly
    /// and yields no targets — we refuse to guess a routing behavior for the
    /// remaining modes this codebase can't faithfully express (Admin is
    /// literally <c>//NYI</c> in the shipped real server; Allies' native
    /// forwarder was disabled by native's own team after a crash; Ship's
    /// parent/child routing is a separate unresolved prerequisite), and we do
    /// not fall back to a broad broadcast (a real behavior bug disguised as
    /// leniency) nor throw (an unimplemented target must not take the server
    /// down — README's "report and ignore" rule). <b>Leaders</b> (1) and
    /// <b>Wing</b> (8) ARE wired here (P396) on the P183 team-leader storage
    /// and the P146 WingId respectively. Returns empty when nothing should
    /// receive it.
    /// </summary>
    private ConnectionId[] SelectChatTargets(ConnectionId from, IChatRoutingTarget chat, short? senderSideId)
    {
        switch (chat.ChatTarget)
        {
            case (int)ChatTarget.Everyone:      // AGCChat_Everyone = 0 (igc.h:2731)
                return _peerShips.Keys.ToArray();

            case (int)ChatTarget.Team:          // AGCChat_Team = 5 (igc.h:2733)
                return _peerShips
                    .Where(kv => kv.Value.Ship.SideId == senderSideId)
                    .Select(kv => kv.Key)
                    .ToArray();

            // CHAT_INDIVIDUAL / CHAT_INDIVIDUAL_NOFILTER (igc.h:2733):
            // same behaviour here — collapse, don't build two paths for a
            // distinction that can't exist yet (no mute/ignore lists).
            case (int)ChatTarget.Individual:
            case (int)ChatTarget.IndividualNoFilter:      // FedSrv.CPP:3538-3556
                return _peerShips
                    .Where(kv => kv.Value.Ship.Id == chat.OidRecipient)
                    .Where(kv => kv.Value.Ship.SideId == senderSideId)   // same-side-only simplification
                    .Select(kv => kv.Key)
                    .ToArray();

            // CHAT_GROUP / CHAT_GROUP_NOECHO: the real server rewrites these to
            // CHAT_INDIVIDUAL and falls through (FedSrv.CPP:3531-3536) — a
            // client-sent Group target is itself just an alias for Individual.
            // Match that exact fallthrough here.
            case (int)ChatTarget.Group:        // FedSrv.CPP:3531-3536
            case (int)ChatTarget.GroupNoEcho:
                goto case (int)ChatTarget.Individual;

            case (int)ChatTarget.AllSector:               // FedSrv.CPP:3428-3441
                return _peerShips
                    .Where(kv => kv.Value.Sector.Id == chat.OidRecipient)
                    .Select(kv => kv.Key)
                    .ToArray();

            case (int)ChatTarget.FriendlySector:          // FedSrv.CPP:3392-3424
                return _peerShips
                    .Where(kv => kv.Value.Sector.Id == chat.OidRecipient)
                    .Where(kv => kv.Value.Ship.SideId == senderSideId)
                    .Select(kv => kv.Key)
                    .ToArray();

            // CHAT_LEADERS (igc.h:2733): every side's current leader — a
            // cross-team "leaders channel", NOT just the sender's own side.
            // FedSrv.CPP:3467-3480 sweeps every side (misparms.nTeams) and
            // forwards to each leader's connection, guarded by
            // commandID == c_cidNone — trivially satisfied for a plain player
            // chat message (the non-c_cidNone form is the separate drone-order
            // chat-buoy dispatch, which never reaches this case). Reuses the
            // P183 Side.LeaderShipId storage via LeaderShip()+ConnOf() rather
            // than a fresh O(peers) scan; a side's leader is included even on
            // the sender's own side (and thus the sender himself, if he leads),
            // matching native's every-side sweep and this port's Everyone
            // self-relay.
            case (int)ChatTarget.Leaders:
            {
                if (_peerShips.TryGetValue(from, out var leadersSender))
                {
                    var mission = leadersSender.Ship.Mission;
                    if (mission is not null)
                        return mission.Sides
                            .Select(s => LeaderShip(mission, s.Id))
                            .OfType<Ship>()
                            .Select(s => ConnOf(s))
                            .Where(c => c is not null)
                            .Cast<ConnectionId>()
                            .Distinct()
                            .ToArray();
                }
                return Array.Empty<ConnectionId>();
            }

            // ForwardChatMessageToWing (FedSrv.CPP:3327-3345), the direct
            // player-chat case (FedSrv.CPP:3594): same-side ships only whose
            // WingId matches the message's target wing id (carried in
            // OidRecipient), excluding pshipSkip — NULL here, so nobody is
            // skipped. Native iterates GetSide()->GetShips() (NOT
            // sector-restricted), so beyond the same-side filter the WingId
            // match is the only condition — a same-side wingmate anywhere in
            // the mission is reached.
            case (int)ChatTarget.Wing:
                return _peerShips
                    .Where(kv => kv.Value.Ship.SideId == senderSideId)
                    .Where(kv => kv.Value.Ship.WingId == chat.OidRecipient)
                    .Select(kv => kv.Key)
                    .ToArray();

            default:
                Console.WriteLine($"[chat] target {chat.ChatTarget} not implemented in this slice — message from {from} dropped");
                return Array.Empty<ConnectionId>();
        }
    }

/// <summary>Creates and places a peer's ship, then registers the live record.
    /// The registered <see cref="LiveShip"/> carries a <b>fresh</b>
    /// <see cref="PlayerScore"/> (P193) seeded to the neutral defaults
    /// <c>Rank=0, Score=0</c> — so every (re)connect starts from zero and the
    /// score is never carried across connections. Nothing here reads any prior
    /// score, because there is no persistence layer to read it from.</summary>
    private LiveShip AssignShip(LogonRequest req, short shipId, short? requestedSideId = null)
    {
        var mission = _missions[0];
        var (ship, sector) = ShipSpawner is not null
            ? ShipSpawner(mission.Core, mission, shipId)
            : SpawnDefaultShip(mission, shipId, requestedSideId);

        // P390 — reconcile an explicitly-requested side against wherever the
        // ship was actually placed. The default path (SpawnDefaultShip) threads
        // requestedSideId into placement (P368), so this never bites there; but
        // an injected ShipSpawner (e.g. the P379 debug topology's closure) has
        // its OWN side policy — a round-robin counter — and no knowledge of the
        // connecting player's choice, so its placement can strand a side-0
        // player at side-1's home (or vice versa). The later TryJoinSide cannot
        // repair that (it only rewrites SideId/leadership/roster bookkeeping,
        // never the ship's position), so the ship would sit at an enemy station
        // where docking is refused. When the player picked a real side that
        // differs from the spawner's, relocate the ship into the requested
        // side's actual home sector. We deliberately do NOT re-derive the
        // spawner's hull mix (its per-side Scout/Fighter alternation is
        // spawner-local policy, not knowable generically here — see the P390
        // report for the judgment call); the hull the spawner chose is always a
        // valid combat hull, and the actionable defect is location + side
        // consistency, which restoring fixes.
        if (requestedSideId is { } requested && ship.SideId != requested)
            sector = RepositionShipToSideHome(ship, mission, requested);

        return new LiveShip(ship, sector, ship.Position, ship.Orientation.GetForward(), ship.Orientation.GetUp(), new PlayerScore(), new PlayerWallet(), ship.ShieldFraction, false);
    }

    /// <summary>P480 — create the fresh ship for a peer being MIGRATED into a
    /// rotated (fresh) mission after a game-over. Deliberately mirrors
    /// <see cref="AssignShip"/> but FORCES the default spawn path
    /// (<see cref="SpawnDefaultShip"/>), ignoring any injected
    /// <see cref="ShipSpawner"/>: the spawner is a join-time placement policy for
    /// NEW joiners (and, in tests/Explorer, drives test-specific placement /
    /// station stripping), whereas a migrated peer is an EXISTING player being
    /// re-seated after game-over — re-running a test spawner on its behalf would
    /// repoint harness state and can even index out of bounds. Uses the same
    /// home-sector placement + fresh <see cref="PlayerScore"/>/<see cref="PlayerWallet"/>
    /// (wallet reset to 0; P443 pays starting money at the next real start) that
    /// a first-seat ship gets.</summary>
    private LiveShip AssignMigratedShip(short shipId, short? requestedSideId = null)
    {
        var mission = _missions[0];
        var (ship, sector) = SpawnDefaultShip(mission, shipId, requestedSideId);
        return new LiveShip(ship, sector, ship.Position, ship.Orientation.GetForward(), ship.Orientation.GetUp(), new PlayerScore(), new PlayerWallet(), ship.ShieldFraction, false);
    }

    /// <summary>P390 — move a freshly-spawned <paramref name="ship"/> into the
    /// home sector of <paramref name="sideId"/> and park it near that side's own
    /// home station, patching <see cref="Ship.SideId"/> to match. Used only to
    /// reconcile an explicitly-requested side against an injected ShipSpawner
    /// that dropped the ship elsewhere; returns the sector the ship now lives
    /// in.</summary>
    private Sector RepositionShipToSideHome(Ship ship, Mission mission, short sideId)
    {
        // Unregister from the spawner's sector first, so PlaceShip does not
        // double-register the ship in two sectors' model lists (P368 pitfall:
        // Mission.PlaceShip adds without removing it from any old sector).
        ship.Sector?.Remove(ship);

        var home = ResolveSideHomeSector(mission, sideId);

        // Park near the side's home station: the same x-axis standoff the P379
        // debug spawner uses (420u), kept generic so the ship is not the
        // station's exact center but sits comfortably inside its dock radius.
        // The spawner's per-side slot grid is not replicated (it needs that
        // closure's private counter) — P390 report documents the judgment call.
        Vector3 basePos;
        var rt = home.StationRuntimes.FirstOrDefault(st => st.SideId == sideId);
        if (rt is not null)
        {
            basePos = rt.Center;
        }
        else
        {
            DataStationIGC sd = default;
            bool found = false;
            foreach (var st in home.Stations)
                if (st.SideId == sideId) { sd = st; found = true; break; }
            basePos = found
                ? new Vector3(sd.Position[0], sd.Position[1], sd.Position[2])
                : Vector3.Zero;   // no resolvable owned station — arbitrary spot, still in the side's home
        }
        ship.Position = basePos + new Vector3(420f, 0f, 0f);

        ship.SideId = sideId;
        mission.PlaceShip(ship, home);
        Console.WriteLine($"[sideselect] ship {ship.Id}: repositioned to side {sideId}'s home sector {home.Id} '{home.Name}' near station {(rt is not null ? rt.StationId : (short)-1)}");
        return home;
    }

    /// <summary>P390 — resolve the sector that is home to <paramref name="sideId"/>
    /// for the purpose of a reconciled spawn placement. When the injected
    /// ShipSpawner is the P379 debug topology, each side's home is the sector
    /// holding that side's synthetic debug garrison (the sector the topology's
    /// own closure spawns that side into), so we anchor on the garrison first.
    /// The mission-wide <see cref="HomeSectorForSide"/> is deliberately NOT used
    /// alone here: on the real <c>m2xspiderw</c> map it returns the FIRST
    /// side-owned station in mission order, which for side 1 is Organia (a real
    /// base) rather than the isolated arena's Plato — reusing it would fling a
    /// side-1 player out of the Omega/Plato/Alderan arena the topology builds
    /// (P390 report documents this finding). When no synthetic garrison exists
    /// (a custom spawner other than the debug topology, e.g. a test closure, OR a
    /// MapGen-sourced debug mission — P402: the generator placed the real home
    /// stations, so the synthetic-id preference is meaningless),
    /// fall back to <see cref="HomeSectorForSide"/>'s generic real-home answer.
    /// </summary>
    private Sector ResolveSideHomeSector(Mission mission, short sideId)
    {
        // P393 — factored through the shared FindDebugGarrisonStation so the
        // debug-garrison match rule (station's own SideId + synthetic id set) lives
        // ONCE, backing both the join path here and the death-respawn path's
        // FindFriendlyStation. The pre-P393 code scanned StationRuntimes then
        // Stations with an inline HashSet; AttachStation populates both lists with
        // the same static DataStationIGC, so locating the garrison via the shared
        // helper's static-Stations scan is equivalent and behavior-preserving.
        // P402 — a generated mission resolves via the generic path (fromMapGen=true
        // makes FindDebugGarrisonStation return null for every sector), which is
        // already correct there.
        var fromMapGen = MissionCameFromMapGen(mission);
        foreach (var sector in mission.Sectors)
            if (FindDebugGarrisonStation(sector, sideId, fromMapGen) is not null)
                return sector;
        return HomeSectorForSide(mission, sideId) ?? mission.Sectors[0];
    }

/// <summary>P281 — resolve a pending (not-yet-spawned) peer's side selection
    /// and complete its join+launch. Called from the side-select drain
    /// (<see cref="AdvanceSideSelection"/>) for a peer in
    /// <see cref="_pendingSideSelect"/> with a recorded POSITIONREQ choice (a
    /// real side = explicit human choice) or on the auto-assign timeout (NA).
    /// Seats the ship
    /// (explicit → full <see cref="TryJoinSide"/> leader/owner election, native
    /// <c>AddPlayerToSide</c>; auto → direct seat matching the old round-robin
    /// spawn path), refreshes pending peers' rosters, then sends the whole
    /// join/launch burst via <see cref="CompleteJoinAndLaunch"/>.</summary>
    private void CompleteSideSelection(ConnectionId from, short requestedSideId)
    {
        if (!_pendingSideSelect.Remove(from, out var pend))
            return;   // not pending (already seated/launched) — nothing to do
        var mission = _missions[0];

        // Validate an explicit side before touching anything; refuse with
        // DPR_SideGone to the requester only (native FedSrv.CPP:6692-6704) and
        // keep them pending so they can pick again.
        if (requestedSideId != -1 && mission.Sides.All(s => s.Id != requestedSideId))
        {
            SendPeer(from, FedPacket.Pack(new[]
            {
                new DelPositionReq { ShipId = pend.ShipId, SideId = requestedSideId, Reason = DelPositionReqReason.SideGone }.Encode(),
            }));
            ConsoleLog.Write($"[sideselect] {from} '{pend.Request.CharacterName}' -> side {requestedSideId} DENIED (DPR_SideGone) to requester only");
            _pendingSideSelect[from] = pend.WithRequestedSide(null);   // clear so it waits for a valid pick
            return;
        }

        // Create the ship. The default spawn seats it in its final side's
        // home sector (P368: the requested side is threaded in BEFORE
        // placement so a single correct PlaceShip puts the ship in that side's
        // real home sector, never sector 0). With no explicit pick
        // (requestedSideId == -1) SpawnDefaultShip falls back to its own
        // round-robin side, also before placement. An injected ShipSpawner
        // (tests/Explorer only) keeps its own policy — production runs the
        // default path. An EXPLICIT side additionally runs the full
        // TryJoinSide (leader + mission-owner election, native AddPlayerToSide);
        // that only sets SideId/leadership/roster — the ship is already in the
        // correct sector, so no relocation is needed.
        LiveShip live = AssignShip(pend.Request, pend.ShipId, requestedSideId != -1 ? requestedSideId : null);
        if (requestedSideId != -1)
        {
            TryJoinSide(live.Ship, requestedSideId);
            Console.WriteLine($"[sideselect] {from} '{pend.Request.CharacterName}' chose side {requestedSideId} (ship {pend.ShipId})");
        }
        else
        {
            Console.WriteLine($"[sideselect] {from} '{pend.Request.CharacterName}' AUTO-assigned to side {live.Ship.SideId?.ToString() ?? "(none)"} (ship {pend.ShipId})");
        }

        // Reflect the new occupancy in every still-choosing peer's roster.
        BroadcastSideRosterToPendingPeers(mission);

        // Send the full join/launch burst (registers _peerShips, then the
        // MISSIONDEF/JOINED_MISSION/EXPORT/.../SET_CLUSTER/MISSION_STAGE dance).
        CompleteJoinAndLaunch(from, pend.Request, pend.ShipId, (byte)(pend.Request.DwCookie & 0xFF), live);
    }

    /// <summary>P468 — send one peer the <c>CREATE_BUCKETS(191)</c>
    /// development-tech mask + one <c>BUCKET_STATUS(65)</c> per bucket for a
    /// side, using the EXACT construction <see cref="CompleteJoinAndLaunch"/>
    /// uses for the initial join-sync burst. Faithful port of the same block of
    /// native <c>SendMissionInfo</c> (<c>fsmission.cpp:4255-4273</c>):
    /// CREATE_BUCKETS built from <c>GetDevelopmentTechs()/GetInitialTechs()</c>,
    /// then one BUCKET_STATUS per bucket. Factored (P468) so a later SIDE
    /// SWITCH (<see cref="TryJoinSide"/>) can re-send the new side's mask to the
    /// switcher instead of leaving the client's <c>DevelopmentTechsBySideId</c>
    /// keyed to the OLD side — the root of the docked shop's fail-open offer.
    /// <para>The mask rides no <c>SideId</c> (like SET_MONEY at join): the
    /// client stores CREATE_BUCKETS under <c>MySideId</c> at receipt
    /// (ClientWorld.Apply(CreateBuckets)), so the caller MUST ensure the
    /// client's <c>MySideId</c> is already the target side — the JOIN_SIDE
    /// broadcast that precedes this send on the same FIFO connection does that
    /// (ClientWorld.Apply(JoinSide), P438).</para></summary>
    private void SendSideTechToPeer(ConnectionId to, Side side)
    {
        var techs = side.DevelopmentTechs;
        SendPeer(to, FedPacket.Pack(new[]
        {
            new CreateBuckets
            {
                TtbmDevelopments = techs.ToBytes(),
                TtbmInitial = techs.ToBytes(),
            }.Encode(),
        }));
        Console.WriteLine($"[createbuckets] peer {to} -> CREATE_BUCKETS(191) ttbmDevelopments={techs.PopCount} bits ttbmInitial={techs.PopCount} bits (side {side.Id})");
        foreach (var bkt in side.Buckets)
        {
            SendPeer(to, FedPacket.Pack(new[]
            {
                new BucketStatus
                {
                    TimeTotal = (uint)bkt.InvestedMilliseconds,
                    SideId = side.Id,
                    MoneyTotal = (int)bkt.InvestedMoney,
                    IBucket = bkt.Id,
                }.Encode(),
            }));
        }
        if (side.Buckets.Count > 0)
            Console.WriteLine($"[bucketstatus] peer {to} -> BUCKET_STATUS(65) x{side.Buckets.Count} for side {side.Id}");
    }

/// <summary>P281 — the deferred join/launch burst formerly inline in the
    /// LogonRequest handler. Registers the live ship and replays the whole
    /// join-sync: MISSIONDEF(+JOINED_MISSION) static-core load, the
    /// side-creating MISSIONDEF, own-ship EXPORT, PLAYERINFO side-assignment,
    /// SET_MONEY / CREATE_BUCKETS / BUCKET_STATUS, SET_BRIEFING_TEXT,
    /// RANK_INFO, TREASURE_SETS, CLUSTERINFO, the cluster/warp/station/asteroid
    /// EXPORTs, SET_CLUSTER (the "you are launched" flip), BUY_LOADOUT_ACK,
    /// MISSION_STAGE(Started), SHIP_STATUS, GAIN_FLAG join-sync and ENTER_GAME.
    /// Nothing here was rewritten — it is the exact pre-P281 burst, relocated so
    /// it runs once the player has chosen a side (or the timeout seated them)
    /// rather than at logon. <paramref name="cookie"/> is the logon ack cookie
    /// (<c>req.DwCookie &amp; 0xFF</c>).</summary>
    private void CompleteJoinAndLaunch(ConnectionId from, LogonRequest req, short shipId, byte cookie, LiveShip live)
    {
        _peerShips[from] = live with { LastBroadcastPos = live.Ship.Position, LastBroadcastForward = live.Ship.Orientation.GetForward(), LastBroadcastUp = live.Ship.Orientation.GetUp() };
        _peerNames[from] = req.CharacterName ?? "";   // P62: remember past logon for teammate EXPORT name
        // P480 — retain the peer's LogonRequest so a later game-over rotation
        // can re-drive this peer through the same join-burst path
        // (CompleteJoinAndLaunch) when re-seating it into a fresh mission.
        _peerRequests[from] = req;

        // P473 — the test-harness economy grant, applied ON SEAT. Only a
        // connection whose character name matches the configured prefix gets
        // it; every other connection is untouched (byte-identical to P441).
        // Set AFTER _peerNames is recorded (so the prefix check can read it)
        // and BEFORE the join-time SET_MONEY sync below, so the client learns
        // the granted wallet, not the P443 starting money. Set absolutely:
        // overriding P443's seed, so a fresh side's garrison economy can never
        // price a harness out of a hull. One line per granted connection.
        if (HarnessGrantFor(from) is { } harness)
        {
            live.Wallet.Money = harness.Money;
            Console.WriteLine($"[testharness] {from} '{req.CharacterName}' GRANTED wallet ${harness.Money} at seat (prefix '{harness.CharacterNamePrefix}' matches)");
        }

        // FM_S_MISSIONDEF(38) then FM_S_JOINED_MISSION(95) FIRST,
        // before any object-creation/launch message. Reason (traced
        // in P29): the JOINED_MISSION handler
        // (appmsg.cpp:2150-2215) is the client's static-core load
        // step — it calls ResetStaticData
        // (appmsg.cpp:2162-2163 → clintlib.cpp:4193-4224), which
        // runs FlushGameState() (destroys every ship, incl. any
        // EXPORTed one, and the current mission) then
        // CreateMission() + Initialize() (fresh no-hull dummy) and
        // LoadIGCStaticCore() — the import that hydrates the
        // mission's hull/part/station registries. So any EXPORT/
        // SET_CLUSTER/BUY_LOADOUT_ACK sent BEFORE these two would
        // be destroyed/replaced by the join; they MUST come after.
        //
        // The two fields that matter are driven by the server's own
        // config + loaded core (no hardcoded values):
        //   - misparms.szIGCStaticFile is the extensionless BASE
        //     name of the static core ("PCore001"), because the
        //     client's LoadIGCStaticCore calls UTL::getFile(name,
        //     ".igc", ...) which APPENDS ".igc" (missionigc.cpp:
        //     2410, _Utility/Utility.cpp:105-123). So the wire value
        //     must NOT include the extension or the client looks for
        //     "PCore001.igc.igc" (empirically caught by the P29
        //     probe: filename=[C:\Source\git\Artwork\PCore001.igc.igc],
        //     fopen=null → version gate bails, client disconnects).
        //     Passing the extensionless base is what makes the
        //     lstrcmp at appmsg.cpp:2162 trigger ResetStaticData at
        //     all, pointing it at the real file. server.json:3 keeps
        //     the full name "PCore001.igc" for OUR own LoadCore;
        //     only the wire value is stripped.
        //   - misparms.verIGCcore must equal the version word at
        //     offset 0 of that .igc file (= the StaticCore the
        //     server loaded), else ResetStaticData bails
        //     (clintlib.cpp:4212-4218) and the client disconnects.
        // Everything else in the large MissionParams/MISSIONDEF is
        // simulation policy this headless server doesn't implement;
        // grep shows the join flow reads only those two (plus the
        // dwCookie identity / shipID tagging).
        SendPeer(from, FedPacket.Pack(new[]
        {
            new MissionDef
            {
                SzIGCStaticFile = Path.GetFileNameWithoutExtension(_config.StaticCore),
                VerIgcCore = _missions[0].Core.Version,
                DwCookie = 0,
                ISideMissionOwner = 0,
                Stage = 0,
            }.Encode(),
            new JoinedMission
            {
                ShipId = shipId,
                DTime = 0,
                Cookie = 0,
                DwCookie = 0,
            }.Encode(),
        }));
        Console.WriteLine($"[join] peer {from} '{req.CharacterName}' -> MISSIONDEF core '{Path.GetFileNameWithoutExtension(_config.StaticCore)}' v{_missions[0].Core.Version} + JOINED_MISSION ship {shipId} (static core load + hull-table hydration)");

        // FM_S_EXPORT(26)/OT_cluster(13) cluster exports used to
        // live here (right after JOINED_MISSION). Moved to AFTER
        // PLAYERINFO/SET_CLUSTER's side-assignment dance below —
        // see the comment down there for why: BaseClient::
        // OnQuitSide (clintlib.cpp:3781-3783,
        // m_pCoreIGC->ResetMission()) unconditionally nukes every
        // registered cluster, and it fires from PLAYERINFO's own
        // AddPlayerToSide call. Exporting clusters here just meant
        // they got wiped moments later.

        // FM_S_MISSIONDEF(38) again, AFTER the join reset, BEFORE
        // the ship EXPORT. Reason (P31 empirical, probe-verified;
        // reordered for the P33 native-side fix — cluster EXPORTs
        // no longer need to precede this send: P31's original
        // "civs not resolved immediately after JOINED_MISSION"
        // finding does not reproduce empirically against the
        // current static-core load path, confirmed directly via
        // native P33dbg instrumentation across multiple live
        // trials sending MISSIONDEF(38) right after JOINED_MISSION
        // with no exports in between — UpdateSides ran clean every
        // time): JOINED_MISSION's ResetStaticData
        // (appmsg.cpp:2162-2163 → clintlib.cpp:4193-4224) DESTROYS
        // the client's m_pCoreIGC and rebuilds a FRESH mission from
        // the static core — which has NO sides (the static core is
        // civ/hull records, not side records). The client's
        // MISSIONDEF ELSE-branch (m_pMissionInfo != NULL,
        // appmsg.cpp:2419-2425) runs UpdateSides here and creates
        // the real sides AND, via CmissionIGC::AddSide
        // (missionigc.cpp:3612), the SIDE_TEAMLOBBY side.
        //
        // Moved to BEFORE the ship EXPORT (was after, through P32):
        // the ship EXPORT's ShipId is now NA (see the comment on
        // that send below), which makes the native client's
        // CshipIGC::Initialize dedup path match the client's own
        // dummy ship and run CshipIGC::ReInitialize on it
        // (shipIGC.cpp:65-163) directly — including its hull
        // branch, which unconditionally reads
        // pmission->GetSide(dataShip->sideID)->GetColor()
        // (shipIGC.cpp:124-149) with NO null check. With sides
        // sent after the ship EXPORT (the old order), that side
        // lookup returns NULL and this is a genuine, deterministic
        // null-pointer access violation — confirmed directly via
        // native P33dbg instrumentation: the ship EXPORT's
        // CreateObject call never returns (crashes inside
        // ReInitialize, silently swallowed by
        // NativeClient::OnAppMessage's blanket SEH handler), which
        // is also *why* the cluster list was observed wiped by the
        // time SET_CLUSTER ran (heap corruption from the crash).
        // Sending sides first removes the NULL entirely.
        var mdSides = _missions[0].Sides;
        SendPeer(from, FedPacket.Pack(new[]
        {
            new MissionDef
            {
                SzIGCStaticFile = Path.GetFileNameWithoutExtension(_config.StaticCore),
                VerIgcCore = _missions[0].Core.Version,
                DwCookie = 0,
                NTems = (short)mdSides.Count,
                RgCivId = mdSides.Select(s => s.Civilization.CivilizationId).ToArray(),
            }.Encode(),
        }));
        Console.WriteLine($"[sides] peer {from} '{req.CharacterName}' -> MISSIONDEF(38) nTeams {mdSides.Count} civIds [{string.Join(", ", mdSides.Select(s => s.Civilization.CivilizationId))}] (create SIDE_TEAMLOBBY + real sides via UpdateSides)");

        // FM_S_EXPORT(26) first: this is the real object-creation
        // message the C++ client's dispatcher handles
        // (appmsg.cpp:80-105 → m_pCoreIGC->CreateObject → for
        // OT_ship=0 news a CshipIGC and AddShip-s it, so
        // GetShip()/GetBaseHullType()/GetPosition() read from a
        // non-null native backing — the T14 fix). SET_CLUSTER
        // below updates that object; without this preceding
        // creation message the client's ship wrapper has a null
        // native backing and GetPosition() throws NRE. object
        // creation must precede any per-tick state update
        // referencing the id, hence the order.
        //
        // Own ship only — deliberately NOT broadcast to observers;
        // mutual multi-bot visibility is out of scope here (same
        // judgment call P19/P22 made for SingleShipUpdate
        // broadening).
        //
        // nParts=0 simplification (unchanged from P26): the
        // DataShipIGC is written with no mounted-part state and no
        // PartData[] tail. Real per-hardpoint export would need
        // Ship's mounted parts serialized in the exact PartData
        // (partID/amount/mountID, 5 bytes each) format — real but
        // separable; this task sends the object-creation header
        // only. FM_S_EXPORT replaces the inert NEW_SHIP_INSTANCE
        // (31) send from P26: that message has no case on the real
        // client's dispatch switch (appmsg.cpp) so it did nothing,
        // though its codec+tests remain as a faithful-but-inert
        // port (fmid 31 is a real protocol message; our client
        // just never dispatches it).
        if (live.Ship.SideId is { } sideId)
        {
            // ShipId = NA (-1), NOT the real assigned shipId. This is the
            // fix for the movement blocker traced directly (native-side,
            // read/log evidence, not guessed): the client's OWN placeholder
            // ship (BaseClient::CreateDummyShip, clintlib.cpp:3859-3882) is
            // created with DataShipIGC.shipID = NA, and BaseClient::m_ship
            // is never reassigned anywhere for the rest of the session
            // (only two "m_ship =" sites exist in clintlib at all: NULL-init
            // and this dummy creation). CshipIGC::Initialize
            // (shipIGC.cpp:165-180) reconciles an incoming EXPORT with an
            // EXISTING object purely by dataShip->shipID equality
            // (pMission->GetShip(dataShip->shipID)). Sending the real,
            // globally-unique shipId here (matching JoinedMission/LogonAck)
            // makes that lookup MISS the dummy and creates an ORPHANED
            // second ship object instead — hulled, but never the object
            // m_ship points to. m_ship stays permanently hull-less, so when
            // SET_CLUSTER runs, appmsg.cpp:1028
            // (pship->GetThingSite()->SetTrailColor(...)) dereferences a
            // NULL ThingSite (hull-less ships never LoadCVH,
            // modelIGC.cpp:105-119) — a real access violation, confirmed
            // directly in the P31d native debug log (pkt#7 = SET_CLUSTER,
            // immediately followed by "NativeClient::ExceptionFilter()
            // caught AV as expected"). NativeClient::OnAppMessage's blanket
            // per-message SEH handler (NativeClient.cpp:29-45) silently
            // swallows it, aborting SET_CLUSTER BEFORE
            // pship->SetCluster(pcluster) (appmsg.cpp:1087) ever runs — so
            // flyingF() (clintlib.h:1198) never becomes true and
            // BaseClient::SendUpdate (clintlib.cpp:1815-1819) never fires
            // FM_C_SHIP_UPDATE. The real, original FedSrv.CPP:907 creates
            // its own internal placeholder ship the identical way
            // (dataship.shipID = NA), corroborating NA as the intended wire
            // value for a player's own ship at creation time.
            //
            // PLAYERINFO's ShipId (PlayerInfo.cs) deliberately keeps the
            // REAL id, unchanged — its native handler (appmsg.cpp:2249)
            // hardcodes DataShipIGC.hullID = NA on every call, so if it also
            // matched the dummy by id it would immediately strip the hull
            // this EXPORT just gave it via ReInitialize's FreeThingSite()
            // path (shipIGC.cpp:135-138). Leaving it mismatched makes
            // PLAYERINFO create a separate, harmless, unused bookkeeping
            // ship instead (same as happens today), while ReInitialize
            // (shipIGC.cpp:120-125) still applies this EXPORT's own SideId
            // to m_ship directly, so side comes along for free.
            const short OwnShipExportId = -1; // NA
            var exportMsg = new ExportShip
            {
                ObjectType = ExportShip.OtShip,
                CharacterName = req.CharacterName ?? "",
                HullId = live.Ship.HullType.HullId,
                ShipId = OwnShipExportId,
                SideId = sideId,
                PilotType = ExportShip.PilotTypePlayer,
            };
            SendPeer(from, FedPacket.Pack(new[] { exportMsg.Encode() }));
            Console.WriteLine($"[create] peer {from} '{req.CharacterName}' -> EXPORT hull {exportMsg.HullId} ship NA(real={shipId}) side {sideId}");
        }
        else
        {
            // A ship with no SideId by SET_CLUSTER time is a real
            // finding (every live-spawn path seats a side; see
            // SpawnDefaultShip). Log it and skip the creation
            // message for this peer — never fabricate side 0
            // (guessing would put the bot on a fake side).
            // SET_CLUSTER still goes out as before; without EXPORT
            // the client wrapper stays null-backed, matching the
            // T14 state, but that is the honest consequence of an
            // unseated side.
            Console.WriteLine($"[create] {from} '{req.CharacterName}': ship {shipId} has NO SideId — skipping EXPORT (finding, not guessed)");
        }

        // FM_S_PLAYERINFO(10): the missing side-carrier (T17 →
        // P31). The client's SET_CLUSTER handler
        // (appmsg.cpp:1034) reads
        // GetShip()->GetSide()->GetColor(); T17 proved
        // m_ship->GetSide() was NULL because the direct-connect
        // client's ship never got a side assigned (JOINED_MISSION
        // does NOT call AddPlayerToSide). This message's handler
        // (appmsg.cpp:2217-2294) is the only dispatched path that
        // reaches AddPlayerToSide (clintlib.cpp:4101 →
        // pship->SetSide(pside) at :4121) — the call that
        // actually sets m_ship's side. Placement reasoning:
        //   • AFTER JOINED_MISSION (its ResetStaticData →
        //     FlushGameState at appmsg.cpp:2162-2163 /
        //     clintlib.cpp:4198 would destroy any ship/side the
        //     PLAYERINFO handler built).
        //   • AFTER the ship EXPORT (P33 update: sides are now
        //     sent BEFORE the ship EXPORT, see above — the ship
        //     EXPORT's ShipId is NA, matching the client's own
        //     dummy, so it's the dummy that ends up with the real
        //     hull+side via ReInitialize). PLAYERINFO's own
        //     ShipId stays the real, non-NA id on purpose, so its
        //     CreateObject(OT_ship,&ds,...) at :2264 does NOT match
        //     the dummy (the real id was never assigned to it) and
        //     takes the "create a new, separate, mostly-empty
        //     bookkeeping ship" path instead of the GetShip(shipID)
        //     else-branch (:2276) — harmless, since nothing reads
        //     that object; m_ship (the dummy) is already correctly
        //     hulled+sided by the ship EXPORT alone by this point.
        //   • BEFORE SET_CLUSTER, whose handler is what crashed
        //     for want of a Side.
        // One message with the real iSide is expected to be enough
        // (code-derived, not yet live-confirmed: AddPlayerToMission,
        // called at :2284 before AddPlayerToSide at :2288, runs
        // SetSideID(SIDE_TEAMLOBBY) at clintlib.cpp:4085, so the
        // AddPlayerToSide assert at :4107 sees SideID()==-2 and
        // should pass — no two-message dance needed). Whether that
        // assert holds on the live client is P31b's empirical job.
        SendPeer(from, FedPacket.Pack(new[]
        {
            new PlayerInfo
            {
                CharacterName = req.CharacterName ?? "",
                Money = 0,
                ISide = live.Ship.SideId ?? -1,
                ShipId = shipId,
                PilotType = PlayerInfo.PilotTypePlayer,
            }.Encode(),
        }));
        Console.WriteLine($"[playerinfo] peer {from} '{req.CharacterName}' -> PLAYERINFO(10) ship {shipId} iSide {live.Ship.SideId?.ToString() ?? "(none)"} (side assignment for the direct-connect ship)");

        // FM_S_SET_MONEY(115): tell the joining/syncing player THEIR OWN money
        // (P442; native fires this inside CFSMission::SendMissionInfo,
        // fsmission.cpp:4270-4299, part of AddPlayerToSide's "tell the joiner
        // about everyone else" sync, one SET_MONEY per teammate with that
        // teammate's per-player wallet — and, separately, the joining player's
        // OWN wallet, fsmission.cpp:4270 + clintlib/appmsg.cpp:1367-1379).
        // P197 added real per-player wallets (PlayerWallet), so the honest value
        // here is the joining player's own live.Wallet.Money, NOT the pooled
        // side treasury (smSide.Money). (The pre-P442 comment claimed "this
        // port tracks ONE pooled number per side … no per-player wallets" —
        // false since P197; the side treasury still accrues income via payday
        // and is distributed to wallets there, but the per-player join sync must
        // carry the player's real wallet, matching native.) Placed right after
        // PLAYERINFO, the point where the player is definitively seated on a side.
        if (live.Ship.SideId is { } setMoneySideId)
        {
            var smSide = _missions[0].Sides.FirstOrDefault(s => s.Id == setMoneySideId);
            if (smSide is not null)
            {
                SendPeer(from, FedPacket.Pack(new[]
                {
                    new SetMoney { Money = live.Wallet.Money, ShipId = shipId }.Encode(),
                }));
                Console.WriteLine($"[setmoney] peer {from} '{req.CharacterName}' -> SET_MONEY(115) money={live.Wallet.Money} shipID={shipId}");

                // FM_S_CREATE_BUCKETS(191) + one BUCKET_STATUS(65)
                // per bucket (P119/P468): the bucket-catalog preface of the
                // same SendMissionInfo join-sync burst that carries
                // SET_MONEY — native builds CREATE_BUCKETS at
                // fsmission.cpp:4255-4258 (from
                // GetDevelopmentTechs()/GetInitialTechs(), the latter == the
                // former here via the native UpdateInitialTechs() equivalence,
                // sideigc.h:219-222), then sends one BUCKET_STATUS per bucket
                // :4260-4273. Sent to the joining peer, exactly like SET_MONEY.
                // Factored into SendSideTechToPeer (P468) so a later side
                // switch re-sends the NEW side's mask to the switcher too.
                SendSideTechToPeer(from, smSide);

                // FM_S_SET_BRIEFING_TEXT(9): tell the joining player
                // their starting location so the client can display the
                // mission briefing (P121). Native fires this in the same
                // join-sync burst (fsmission.cpp:722-732), one message to
                // the joining player's OWN connection, branching on a
                // mission-authored custom story text (m_strStoryText →
                // fGenerateCivBriefing=false) vs. just the starting
                // cluster's name (fGenerateCivBriefing=true). This port
                // ALWAYS takes the generate-civ-briefing branch: it
                // decodes no mission-level custom story-text field
                // (confirmed — no StoryText/Story in MissionDef), so
                // there is no m_strStoryText to send verbatim (see
                // SetBriefingText doc). Text = the cluster name of the
                // joining side's first-found friendly station, reusing
                // P105's FindFriendlyStation stand-in for the native
                // nearest-station/GetBase lookup (Server.cs:4750-4759).
                // Sent to the joining peer only, exactly like SET_MONEY.
                var friendlyStation = FindFriendlyStation(live.Ship);
                if (friendlyStation is { } briefStation)
                {
                    var clusterName = _missions[0].Sectors
                        .FirstOrDefault(s => s.Id == briefStation.ClusterId)?.Name;
                    if (clusterName is not null)
                    {
                        SendPeer(from, FedPacket.Pack(new[]
                        {
                            new SetBriefingText { Text = clusterName, FGenerateCivBriefing = true }.Encode(),
                        }));
                        Console.WriteLine($"[briefing] peer {from} '{req.CharacterName}' -> SET_BRIEFING_TEXT(9) generateCivBriefing=true text='{clusterName}' (friendly station {briefStation.StationId} side {briefStation.SideId})");
                    }
                }
                else
                {
                    // No friendly station anywhere (P105 helper's
                    // documented null return, Server.cs:4704) — there is
                    // no meaningful starting location to name. Native's
                    // GetBase(pside) always resolves a mission base, so
                    // this does not arise in a real mission; here it is
                    // logged and the message skipped rather than
                    // fabricating text.
                    Console.WriteLine($"[briefing] peer {from} '{req.CharacterName}' -> no friendly station for side {setMoneySideId} — SET_BRIEFING_TEXT skipped");
                }
            }
        }

        // FM_S_RANK_INFO(13): tell the joining player the server's
        // rank-name table (P130). Native builds this right after
        // LOGONACK on the valid-connect path, from the boot-time
        // LoadRankInfo table (FedSrv.CPP:998-1001; LoadRankInfo at
        // :103-150, run unconditionally at :9347). Static boot-time
        // config, not live game state, and not side-scoped — so it
        // sits outside the side block, sent ONCE to the joining
        // peer's own connection only, exactly like TREASURE_SETS.
        // The table is the real ranks.txt payload (loaded at boot,
        // every entry civ=-1 / requiredRanking=0 — standalone).
        SendPeer(from, FedPacket.Pack(new[] { _rankTable.Encode() }));
        Console.WriteLine($"[rankinfo] peer {from} '{req.CharacterName}' -> RANK_INFO(13) {_rankTable.Ranks.Length} rank(s) (civ=-1, requiredRanking=0, standalone)");

        // FM_S_TREASURE_SETS(89): tell the joining player the
        // mission's treasure-set catalog (P123). Native defines the
        // message (Messages.h:624-628) as an FM_VAR_ITEM array of
        // TreasureSet with the chance blob to ignore; modern FedSrv
        // never emits it (legacy; Wopr/WinTrek parse it as NYI — see
        // the P123 report's spawn-trigger finding), but it is the
        // join-time catalogue sync, so this port sends the real
        // already-decoded StaticCore.TreasureSets catalog at
        // join-sync, per the message's own FM_VAR_ITEM shape with
        // the per-entry chance blob omitted. Mission-wide (not
        // side-specific), so it sits outside the side block, sent to
        // the joining peer only, exactly like SET_MONEY.
        var treasureSets = _missions[0].Core.TreasureSets;
        if (treasureSets.Count > 0)
        {
            var tsMsg = TreasureSets.FromCatalog(treasureSets.Values);
            SendPeer(from, FedPacket.Pack(new[] { tsMsg.Encode() }));
            Console.WriteLine($"[treasuresets] peer {from} '{req.CharacterName}' -> TREASURE_SETS(89) {tsMsg.Sets.Length} set(s)");
        }
        else
        {
            Console.WriteLine($"[treasuresets] peer {from} '{req.CharacterName}' -> core has no treasure sets — TREASURE_SETS(89) skipped");
        }

        // FM_S_CLUSTERINFO(200): the map-overview message the
        // reference server sends in SendMissionInfo IMMEDIATELY
        // BEFORE the cluster EXPORT loop (fsmission.cpp:4124-4166,
        // then :4170). It is what feeds the native Wopr client's
        // GameInfo.Clusters (StrategyBase.cs:172:
        // GameInfo.Clusters = message.Clusters) and therefore
        // GetUnexploredClusters() (GameInfo.cs:69-92), which is
        // currently always empty because this message was never
        // sent — so ScoutExploreMap's smart branches (nearest-
        // unexplored-warp/cluster, ScoutExploreMap.cs:577-650)
        // never engaged and every bot fell back to the random-warp
        // pick (:653-662). Placed here, right after PLAYERINFO and
        // right before the cluster EXPORT burst, reproduces the
        // reference order exactly (CLUSTERINFO before EXPORT) with
        // no impact on the P33/P35 export-ordering invariants that
        // this block documents: CLUSTERINFO is pure passive data
        // the client stores (GameInfo.Clusters), it touches neither
        // the mission side-membership path (PLAYERINFO) nor the
        // m_pCoreIGC cluster pool that SET_CLUSTER resolves — those
        // still come from the EXPORT burst below, so ORDERING of
        // THAT burst is untouched. Built from the same Sectors the
        // EXPORT burst iterates; dangling warp destinations are
        // skipped and logged, never fabricated (ResolvedWarp.cs:28-32).
        // P48: cached once (mission-wide, never changes) rather than
        // re-encoded per peer/per resend -- also reused by
        // BroadcastShipStates' periodic refresh below.
        var clusterInfo = ClusterInfo.FromSectors(_missions[0].Sectors);
        _clusterInfoBytes ??= clusterInfo.Encode();
        SendPeer(from, FedPacket.Pack(new[] { _clusterInfoBytes }));
        _lastClusterInfoTick[from] = TickIndex;
        Console.WriteLine($"[clusterinfo] peer {from} '{req.CharacterName}' -> CLUSTERINFO(200) {clusterInfo.Clusters.Count} cluster(s), {clusterInfo.Warps.Count} warp(s)");

        // FM_S_EXPORT(26)/OT_cluster(13): convey the MAP's sectors
        // to the client. Placement (P33, moved here from right
        // after JOINED_MISSION): the PLAYERINFO send just above
        // moves the local player off SIDE_TEAMLOBBY onto its real
        // side. On the native client that runs
        // BaseClient::AddPlayerToSide (clintlib.cpp:4101), which —
        // because this is the player's OWN PlayerInfo
        // (pPlayerInfo == m_pPlayerInfo) — calls
        // BaseClient::OnQuitSide() first (clintlib.cpp:4112-4115,
        // matching the "leaving SIDE_TEAMLOBBY" half of the
        // transition even on the very first join). OnQuitSide
        // unconditionally nukes every registered cluster
        // (clintlib.cpp:3781-3783: SetViewCluster(NULL);
        // m_pCoreIGC->ResetMission(), which iterates
        // GetClusters() and Terminate()s every one). Exporting
        // clusters BEFORE PLAYERINFO (the original placement)
        // meant they were all silently wiped moments later —
        // confirmed directly via native P33dbg instrumentation
        // (a live cluster-count probe read 13 right after the
        // exports, then 0 by the time SET_CLUSTER ran) — which is
        // what made m_pCoreIGC->GetCluster(sectorID)
        // (appmsg.cpp:1006) return NULL and crash the very next
        // line (pcluster->GetLastUpdate(), appmsg.cpp:1041, no
        // null check) before pship->SetCluster(pcluster) could
        // ever run — the actual, final blocker behind flyingF()
        // (clintlib.h:1198) never becoming true. Sending the
        // exports after PLAYERINFO's OnQuitSide instead means
        // there is nothing left to wipe. We export ALL sectors
        // (the reference approach, a trivial loop) rather than
        // just the peer's starting sector, so the client can
        // resolve any cluster id a later message references and
        // there is no "which one" selection logic to get wrong;
        // ~92 bytes × N sectors is negligible locally. Must land
        // BEFORE SET_CLUSTER, below.
        var clusterExports = _missions[0].Sectors
            .Select(s => ExportCluster.FromSector(s).Encode())
            .ToArray();
        SendPeer(from, FedPacket.Pack(clusterExports));
        Console.WriteLine($"[export] peer {from} '{req.CharacterName}' -> {clusterExports.Length} cluster(s): " +
                          string.Join(", ", _missions[0].Sectors.Select(s => $"{s.Id}:'{s.Name}'{(s.Cluster.HomeSector ? "*" : "")}")));

        // Same peer/session, immediately after the cluster exports,
        // per sector, in the reference server's own order
        // (fsmission.cpp:4170-4222): cluster → warps → stations.
        // This is what finally gives the native client's
        // FindTarget (src/Igc/common.cpp:470, station search at
        // :500-501, adjacent-cluster warp walk at :677-707)
        // non-empty GetWarps()/GetStations() to search, closing the
        // P34 finding that FindTarget always returned NULL because
        // the client's clusters had empty lists. Asteroids ARE
        // exported (P64) — the map is m2xspiderw.igc, 134 real
        // rocks — since the client's UndockMinersAndStartMining
        // (CommanderResearchAndExpand.cs:1563-1568) searches
        // GetAsteroids() for He3-named rocks. Treasure, mines and
        // probes are intentionally skipped (none exist in this
        // map / this codebase's scope), matching the task.
        //
        // Unconditional — no SeenBySide()/fog-of-war gate, exactly
        // as P30 reasoned for clusters (Server.cs:742-747): this
        // codebase has no scouting/visibility system, so export
        // every warp, station and asteroid of every sector to every
        // peer. Ships' resolved destination sectors come for free:
        // the warp's own ClusterId addresses its sector, and all
        // sectors are already exported above.
        foreach (var sector in _missions[0].Sectors)
        {
            foreach (var resolved in sector.Warps)
                SendPeer(from, FedPacket.Pack(new[] { ExportWarp.FromWarp(resolved).Encode() }));
            foreach (var station in sector.Stations)
                SendPeer(from, FedPacket.Pack(new[] { ExportStation.FromStation(station).Encode() }));
            foreach (var asteroid in sector.Asteroids)
                SendPeer(from, FedPacket.Pack(new[] { ExportAsteroid.FromAsteroid(asteroid).Encode() }));
        }
        Console.WriteLine($"[export] peer {from} '{req.CharacterName}' -> {_missions[0].Warps.Count} warp(s), {_missions[0].Sectors.Sum(s => s.Stations.Count)} station(s), {_missions[0].Sectors.Sum(s => s.Asteroids.Count)} asteroid(s) (OT_warp 7 / OT_station 1 / OT_asteroid 5)");

        var set = new SetCluster
        {
            SectorId = live.Sector.Id,
            Ship = ServerShipUpdate.FromModel(shipId, live.Ship, (uint)TickIndex),
            Cookie = cookie,
        };
        SendPeer(from, FedPacket.Pack(new[] { set.Encode() }));

        // FM_S_BUY_LOADOUT_ACK(5): after the client has been told
        // its object (EXPORT 26) and its launch (SET_CLUSTER 7),
        // adopt the real hull onto the client's own ship pointer.
        // The C++ client handler (appmsg.cpp:3180-3183) calls
        // m_ship->ProcessShipLoadout(...), which for a no-hull
        // dummy (m_myHullType.GetHullType()==NULL) fires the
        // "hull changed" branch (shipIGC.h:888-897) and calls
        // SetBaseHullType(GetMyMission()->GetHullType(hullID)),
        // making GetBaseHullType()/GetPosition() stop throwing NRE
        // — the transition the real server achieves when a player
        // buys a loadout (FedSrv.CPP:902-928 ProcessConnect starts
        // every fresh player on a hullID=NA dummy too; the buy is
        // the step that gives it a hull).
        //
        // nParts=0 simplification (unchanged from P26/P27): the
        // ShipLoadout carries only the 2-byte hullID, no
        // ExpandedPartData[] tail. Real per-part purchase is the
        // deferred BUY_LOADOUT/economy machinery.
        //
        // fBoughtEverything=false, fLaunch=false: we assign a hull
        // but buy no parts (honest), and SET_CLUSTER has already
        // launched the ship before this ACK, so no further launch
        // messages will follow (claiming fLaunch would be false).
        // fLaunch=false also releases any client-side lockdown via
        // EndLockDown (appmsg.cpp:3180-3181); for a direct-connect
        // bot IsLockedDown() is false anyway, making it a no-op.
        SendPeer(from, FedPacket.Pack(new[]
        {
            new BuyLoadoutAck
            {
                HullId = live.Ship.HullType.HullId,
                FBoughtEverything = false,
                FLaunch = false,
            }.Encode(),
        }));

        // FM_S_MISSION_STAGE(37): the message this whole join burst
        // was missing (P37, 2026-08-31). The native client's entire
        // per-tick simulation -- CmissionIGC::Update
        // (missionigc.cpp:3133-3134) and the per-cluster ship
        // plot/execute loop (clusterIGC.cpp:103-231, PlotShipMove/
        // ExecuteShipMove) -- is a no-op until the client's own core
        // mission stage is STAGE_STARTED/STAGE_STARTING. The client's
        // only path there outside the lobby (which a direct-connect
        // client never sees) is this message
        // (appmsg.cpp:2314-2349): case STAGE_STARTED calls
        // SetInProgress(true) + m_pCoreIGC->SetMissionStage(STAGE_STARTED),
        // unblocking both gates. This server already tracks
        // Mission.Stage = Started internally (see the mission
        // construction above) but never told the client -- confirmed
        // via a live capture showing zero fmid=37 frames and three
        // native probes (GotoPlan::Execute/ExecuteShipMove/
        // CclusterIGC::Update) that never fired even with a correct
        // ship position and a resolved goto target. Sent last, after
        // everything the simulation needs (mission, sides, ship,
        // clusters, warps, stations, launch) already exists.
        // P309 — send the mission's REAL current stage, not the hardcoded Started
        // this burst used to send. Previously every joiner was told STAGE_STARTED
        // even while the mission sat NotStarted in the pre-game hold (P180), which
        // made a joining client believe the game was already live. The native
        // client's simulation only starts ticking at STAGE_STARTING/STAGE_STARTED
        // (missionigc.cpp:3133, clusterIGC.cpp:103), so a pre-game joiner must be
        // told the real (NotStarted/Starting) stage and then re-notified when the
        // mission actually starts (CompleteStartCountdown →
        // BroadcastMissionStageToMission). MissionStage enum values
        // (NotStarted=0..Over=3) are byte-identical to the wire AGCGameStage
        // constants, so a direct int cast is the faithful mapping.
        int realStage = live.Ship.Mission is { } joinedMission
            ? (int)joinedMission.Stage
            : (int)MissionStage.NotStarted;   // no mission → pre-game by definition
        SendPeer(from, FedPacket.Pack(new[]
        {
            new MissionStageMessage { Stage = realStage }.Encode(),
        }));
        Console.WriteLine($"[stage] peer {from} '{req.CharacterName}' -> MISSION_STAGE(37) {(MissionStage)realStage}");

        // FM_S_SHIP_STATUS(90): the message ScoutExploreMap's whole
        // autonomous sweep/exploration is gated on (P39 finding —
        // <c>src/Wopr/Wopr/Strategies/ScoutExploreMap.cs:85-221</c>).
        // The client only acts on a status for a ship it has a
        // PLAYERINFO record for (appmsg.cpp:2719, FindPlayer gate),
        // which this server sends only for a peer's OWN ship — so
        // this is correctly scoped to each peer's own ship. Sent
        // after MISSION_STAGE so the client's mission is running and
        // its ship is in a sector (the handler returns early if
        // GetCluster()==null, ScoutExploreMap.cs:96-97), which this
        // is: SET_CLUSTER already launched it into live.Sector.
        //
        // Simplifications (this repo's honesty-about-simplifications
        // style): no scouting/visibility system exists, so
        // Unknown=false and Detected=true always — there is no
        // scanning state to make them meaningful yet (matching the
        // simplified server's "no detection system" stance). State
        // is c_ssFlying with StationID/ParentID NA because this
        // server has no docking — ships are born flying in a sector.
        SendPeer(from, FedPacket.Pack(new[]
        {
            MakeOwnShipStatus(shipId, live).Encode(),
        }));
        ConsoleLog.Write($"[shipstatus] peer {from} '{req.CharacterName}' -> SHIP_STATUS(90) ship {shipId} sector {live.Sector.Id}");
        _lastStatusSector[from] = live.Sector.Id;
        _lastStatusTick[from] = TickIndex;

        // FM_S_GAIN_FLAG(156) join-sync (P201): the port of native
        // fsmission.cpp:740-762 ("tell them about the players who
        // have flags"), which runs right after SendMissionInfo and
        // before the late-joiner/ENTER_GAME handling. A newly-joined
        // player is told about every currently flag-carrying ship via
        // one GAIN_FLAG per carrier — sideidFlag = the carried flag's
        // side, shipidRecipient = the carrier's ship id,
        // bIsTreasureDocked = false — sent to the joining player's
        // OWN connection only (g.fm.SendMessages(pfsPlayer->GetConnection())).
        int nFlagSync = 0;
        foreach (var fk in _peerShips)
        {
            if (!ReferenceEquals(fk.Value.Ship.Mission, live.Ship.Mission)) continue;
            if (fk.Value.Ship.Flag is not { } carrierFlag) continue;
            SendPeer(from, FedPacket.Pack(new[]
            {
                new GainFlag
                {
                    SideidFlag = carrierFlag,
                    ShipidRecipient = fk.Value.Ship.Id,
                    BIsTreasureDocked = false,
                    ParttypeidDocked = 0,
                }.Encode(),
            }));
            nFlagSync++;
        }
        if (nFlagSync > 0)
            Console.WriteLine($"[gainflag] peer {from} '{req.CharacterName}' -> GAIN_FLAG(156) join-sync x{nFlagSync} for flag-carrying ship(s)");

        // FM_S_ENTER_GAME(128): the final "you're ready" signal at the
        // real end of the join-sync burst (P141). Native fires it inside
        // the mission-join path gated on STAGE_STARTED == GetStage()
        // (fsmission.cpp:799-805), flushed to the joining player's OWN
        // connection only (g.fm.SendMessages(pfsPlayer->GetConnection(),
        // FM_GUARANTEED, FM_FLUSH), fsmission.cpp:830). Its companion
        // GetPlayerScoreObject()->Connect (fsmission.cpp:801) is N/A
        // here (no score system). P180 makes the gate meaningful: now
        // that the mission is born NotStarted (not force-started at
        // boot), this join-sync send is sent ONLY when the mission is
        // already Started (a late joiner arriving after auto-start) —
        // when still NotStarted it is suppressed here, because the
        // ready-up auto-start transition broadcasts ENTER_GAME to the
        // whole mission once the game actually starts (the same
        // whole-mission audience native's StartGame uses,
        // fsmission.cpp:2267-2269). This guard only ever suppresses
        // when the mission is NOT yet started — never when it already
        // is, so a peer joining a running mission still gets its own
        // immediate ENTER_GAME through this path.
        if (live.Ship.Mission is { Stage: MissionStage.Started })
        {
            SendPeer(from, FedPacket.Pack(new[] { new EnterGame().Encode() }));
            Console.WriteLine($"[entergame] peer {from} '{req.CharacterName}' -> ENTER_GAME(128) (join-sync complete — player ready)");
        }
        else
        {
            Console.WriteLine($"[entergame] peer {from} '{req.CharacterName}' -> ENTER_GAME(128) deferred (mission NotStarted; sent on ready-up auto-start)");
        }
    }

/// <summary>P281 — send a fresh <see cref="SideRoster"/> snapshot to a
    /// single peer (typically a pending side-select peer) so it can render the
    /// side list. Names/civ ids come from the mission's real Side data — never
    /// hardcoded.</summary>
    private void SendSideRoster(ConnectionId to)
    {
        var mission = _missions[0];
        var recs = mission.Sides
            .Select(s => new SideRoster.SideRecord(
                s.Id,
                s.Civilization.CivilizationId,
                (short)_peerShips.Count(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == s.Id),
                s.Name ?? $"side{s.Id}",
                s.ColorR, s.ColorG, s.ColorB, s.ColorA))
            .ToArray();
        SendPeer(to, FedPacket.Pack(new[] { new SideRoster { Sides = recs }.Encode() }));
        Console.WriteLine($"[sideselect] -> peer {to} SIDE_ROSTER(211): {recs.Length} side(s) [{string.Join(", ", recs.Select(r => $"{r.Name}({r.SideId})={r.PlayerCount}"))}]");
    }

/// <summary>P281 — refresh every still-pending side-select peer's roster
    /// whenever any peer's seat changes, so the on-screen counts stay current.</summary>
    private void BroadcastSideRosterToPendingPeers(Mission mission)
    {
        foreach (var key in _pendingSideSelect.Keys.ToList())
            SendSideRoster(key);
    }

/// <summary>P281 — the per-tick driver that auto-assigns a pending
    /// side-select peer that never picked within <see cref="SideSelectTimeoutSeconds"/>,
    /// so a silent/AFK client is eventually seated and launched instead of
    /// hanging at the side-select screen (and never left pending forever).
    /// Pending peers are side-less and not in _peerShips, so they never block
    /// the P180 ready-up auto-start; this is purely to not strand the silent
    /// player. 30 s is comfortably longer than the 15 s P188 briefing countdown
    /// and ample for a 2-option pick, yet short enough that an abandoned join
    /// recycles quickly.</summary>
    private void AdvanceSideSelection(double now)
    {
        if (_pendingSideSelect.Count == 0) return;
        // Seat every pending peer that is ready (has a recorded POSITIONREQ
        // choice) OR timed out, in ascending JoinSeq (logon) order. Draining in
        // logon order — not POSITIONREQ arrival order — keeps side assignment
        // deterministic (round-robin sides and injected ShipSpawners both key
        // off spawn order), exactly as when peers seated synchronously at logon
        // pre-P281; racing client receive-threads otherwise shuffled sides.
        var ready = _pendingSideSelect
            .Where(kv => kv.Value.RequestedSide is not null
                      || now - kv.Value.ConnectSimTime >= SideSelectTimeoutSeconds)
            .OrderBy(kv => kv.Value.JoinSeq)
            .Select(kv => (Key: kv.Key, Pending: kv.Value))
            .ToList();
        foreach (var (key, pend) in ready)
        {
            if (pend.RequestedSide is { } side && side != -1)
            {
                Console.WriteLine($"[sideselect] draining peer {key} '{pend.Request.CharacterName}' -> chosen side {side}");
                CompleteSideSelection(key, side);
            }
            else
            {
                Console.WriteLine(pend.RequestedSide is null
                    ? $"[sideselect] peer {key} '{pend.Request.CharacterName}' never chose a side within {SideSelectTimeoutSeconds}s — auto-assigning"
                    : $"[sideselect] draining peer {key} '{pend.Request.CharacterName}' -> auto-pick (NA)");
                CompleteSideSelection(key, -1);   // NA = auto-pick (arrival order / spawner default)
            }
        }
    }
}
