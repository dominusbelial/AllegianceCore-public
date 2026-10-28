using System.Diagnostics;
using System.Runtime.InteropServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
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

        // P548 — native's stage gate (fsmission.cpp:1147,
        // `(STAGE_NOTSTARTED != m_misdef.stage)`): the win-by-inactive-sides
        // check only ever runs AFTER a side was deactivated, and a side is only
        // deactivated on a peer departure when the mission has STARTED. A drop
        // in a NotStarted lobby must therefore never end the mission — the old
        // code ran this mission-wide tally on EVERY disconnect, so a lobby drop
        // that left <= 1 side populated declared game-over ("Stage Over" on
        // every peer drop). Defense-in-depth: ReleasePeer already gates on
        // stage != NotStarted before calling this.
        if (mission.Stage == MissionStage.NotStarted)
            return;

        // P844/SIM-0533 — native's AllowEmptyTeams gate (fsmission.cpp:3808-3811,
        // KGJV #62): don't end the game during STAGE_STARTING when empty teams
        // are allowed — sides are expected to sit empty before the start, so a
        // tally that would crown the only populated side must not fire.
        if (mission.Stage == MissionStage.Starting && mission.AllowEmptyTeams)
            return;

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
        {
            // P844/SIM-0533 — native's allies override (fsmission.cpp:3820-3856,
            // Imago ALLY 7/17/09): the tally found several sides with players,
            // but when every ACTIVE side is one alliance group the game is over
            // and the alliance wins — the pre-fix code just returned and the
            // allied sides played on forever.
            var allyWinner = AllAlliedWinner(mission);
            if (allyWinner is null)
                return;                              // ≥2 un-allied sides alive — game continues
            DeclareGameOver(mission, allyWinner.Id, AlliedWinnerReason(mission, allyWinner));
            return;
        }

        // Exactly one side with players → it wins; zero → draw.
        short winnerId = GameOver.NA;
        string reason = "All sides were destroyed";
        if (playerSideIds.Count == 1)
        {
            winnerId = playerSideIds.First();
            var winnerSide = mission.Sides.FirstOrDefault(s => s.Id == winnerId);
            // Native names every side of the winner's alliance in the reason
            // whenever the winner is allied (fsmission.cpp:5396-5412), even when
            // the override did not fire — P852's AlliedWinnerReason.
            reason = winnerSide is not null
                ? AlliedWinnerReason(mission, winnerSide)
                : $"side{winnerId} won by outlasting all other sides.";
        }

        // Decide the outcome first so the Stage==Over gates are live for any
        // subsequent gameplay message before we broadcast the end.
        DeclareGameOver(mission, winnerId, reason);
    }

    /// <summary>P844/SIM-0533 — native's allies override for the
    /// win-by-inactive-sides family (<c>fsmission.cpp:3820-3856</c>, the tail of
    /// <c>CheckForVictoryByInactiveSides</c>; Imago ALLY 7/17/09): when every
    /// <b>active</b> side of the mission sits in ONE alliance group, the
    /// alliance wins the game even though the one-side-with-players tally
    /// found several survivors. Native's double loop keeps <c>bAllAllies</c>
    /// unless some ordered pair of distinct active sides fails
    /// <c>IsideIGC::AlliedSides</c> (<c>Igc/igc.h:4297-4305</c>: same side, or
    /// equal non-NA alliance ids — the port's <see cref="Side.AllyGroup"/>);
    /// the winner is a member of that group (native picks the last side its
    /// walk visited — the reason lists every group member, so the pick is
    /// immaterial). Returns null when the active sides are not one alliance
    /// (or there are none). Internal for the P844 certification tests.</summary>
    internal static Side? AllAlliedWinner(Mission mission)
    {
        foreach (var side in mission.Sides)
            if (side.Active && AllActiveSidesAllied(mission, side))
                return side;
        return null;
    }

    /// <summary>P844/SIM-0533 — the override's pairwise test: true when
    /// <paramref name="winner"/> carries an alliance group
    /// (<c>GetAllies() != NA</c>) and every <b>active</b> side shares it
    /// (fsmission.cpp:3824-3841 active-side pairs). Internal for the P844
    /// certification tests.</summary>
    internal static bool AllActiveSidesAllied(Mission mission, Side winner)
    {
        if (winner.AllyGroup is not { } group) return false;   // :5397 gate
        foreach (var side in mission.Sides)
            if (side.Active && side.AllyGroup != group)        // :3824-3841 active-side pairs
                return false;
        return true;
    }

    /// <summary>
    /// P548 — deactivate a side that a peer departure just emptied, the port of
    /// native <c>CFSMission::DeactivateSide</c> (<c>FedSrv/fsmission.cpp:5318-5427</c>)
    /// as invoked from the peer-departure site (<c>RemovePlayerFromSide</c>,
    /// <c>fsmission.cpp:1147-1149</c>: <c>fDeactivate =
    /// (STAGE_NOTSTARTED != m_misdef.stage) &amp;&amp; !HasPlayers(psideOld, false)</c>).
    /// Marks the side inactive, broadcasts <c>SIDE_INACTIVE(68)</c> +
    /// "<c>&lt;name&gt; is no more.</c>" (<c>:5371-5377</c>), then runs the
    /// win-by-elimination check (<see cref="CheckForVictoryByInactiveSides"/>,
    /// <c>:5378-5419</c>).
    /// <para>Unlike the station-elimination <see cref="DeactivateSide(Mission, Side)"/>
    /// (P455), this peer-departure variant is NOT the same function — it keeps
    /// P388's debug-mode exemption (a client dropping out of the unattended
    /// 0-player test arena is a non-event: no deactivate, no end) and it does
    /// not run <see cref="CheckForVictoryByInactiveSidesAfterDeactivation"/>,
    /// which ends even a debug mission. The emptied side has no players, so the
    /// side-only game-over branch (native <c>:5420-5426</c>) finds nobody to
    /// notify; native's elimination of the side's leftover drones
    /// (<c>:5331-5368</c>) is P455b scope and not repeated here. Reaching here
    /// already implies stage != NotStarted and an emptied side (the ReleasePeer
    /// gate), so those are not re-checked below except the idempotent Over.</summary>
    private void DeactivateSideForPeerDeparture(Mission mission, Side? side)
    {
        if (side is null) return;                      // defensive: side vanished
        if (mission.Stage == MissionStage.Over) return; // idempotent: already decided
        if (_config.LiveSettings?.DebugMode == true)
        {
            // P388 — the empty debug arena is the designed steady state; a
            // client leaving must not deactivate anything nor end the mission.
            Console.WriteLine("[debugmode] skipping peer-departure side deactivation (0 players is expected in the unattended test arena)");
            return;
        }

        side.Active = false;                           // fsmission.cpp:5325
        side.Ready = false;                            // P852: rgfReady[sideid] = false, fsmission.cpp:5326-5327

        // SIDE_INACTIVE(68) bActive=false, bChangeAET=false, bAET=false
        // (fsmission.cpp:5371-5376).
        var si = new SideInactive { SideId = side.Id, BActive = false, BChangeAet = false, BAet = false };
        int nSi = SendToMissionPeers(mission, FedPacket.Pack(new[] { si.Encode() }));
        Console.WriteLine($"[sideinactive] side {side.Id} deactivated (last player departed) => SIDE_INACTIVE(68) bActive=false -> {nSi} peer(s)");

        // "<Side name> is no more." (fsmission.cpp:5377).
        var chat = new ChatMessage { Message = $"{side.Name} is no more.", ChatTarget = (int)ChatTarget.Everyone };
        int nChat = SendToMissionPeers(mission, FedPacket.Pack(new[] { chat.Encode() }));
        Console.WriteLine($"[sideinactive] side {side.Id} departure elimination chat \"{side.Name} is no more.\" -> {nChat} peer(s)");

        // Win-by-inactive-sides check (fsmission.cpp:5378-5419).
        CheckForVictoryByInactiveSides(mission);
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
        side.Ready = false;                           // P852: rgfReady[sideid] = false, fsmission.cpp:5326-5327

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

        // P844/SIM-0533 — native's AllowEmptyTeams gate (fsmission.cpp:3808-3811):
        // the same CheckForVictoryByInactiveSides body serves the deactivation
        // call site, so a Starting mission with empty teams allowed never ends here.
        if (mission.Stage == MissionStage.Starting && mission.AllowEmptyTeams)
            return;

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
        {
            // P844/SIM-0533 — native's allies override (fsmission.cpp:3820-3856):
            // several sides still alive, but if every ACTIVE side is one
            // alliance group the game is over and the alliance wins.
            var allyWinner = AllAlliedWinner(mission);
            if (allyWinner is null)
                return;                               // ≥2 un-allied sides alive — game continues
            DeclareGameOver(mission, allyWinner.Id, AlliedWinnerReason(mission, allyWinner));
            return;
        }

        short winnerId = GameOver.NA;
        string reason = "All sides were destroyed";
        if (alive.Count == 1)
        {
            winnerId = alive[0].Id;
            // Native names every side of the winner's alliance in the reason
            // whenever the winner is allied (fsmission.cpp:5396-5412) — P852.
            reason = AlliedWinnerReason(mission, alive[0]);
        }
        DeclareGameOver(mission, winnerId, reason);
    }

    /// <summary>P852 — native's winner reason for the DeactivateSide victory
    /// check (fsmission.cpp:5396-5414): a solo surviving side names only
    /// itself ("A won by outlasting all other sides.", :5413-5414); a side
    /// with allies (GetAllies() != NA) names every OTHER side sharing its
    /// alliance group — AlliedSides is equality on the alliance id
    /// (igc.h:4296-4303), the port's Side.AllyGroup — producing
    /// "A & B won by outlasting all other sides." Native's loop walks ALL the
    /// mission's sides with no active filter (:5401-5410), so an eliminated
    /// but still-present ally is named too.</summary>
    private static string AlliedWinnerReason(Mission mission, Side winner)
    {
        string winnerName = winner.Name ?? $"side{winner.Id}";
        var allies = winner.AllyGroup is { } winGroup
            ? mission.Sides.Where(s => !ReferenceEquals(s, winner) && s.AllyGroup == winGroup)
                .Select(s => s.Name ?? $"side{s.Id}").ToList()
            : [];
        return $"{winnerName}{string.Join("", allies.Select(n => " & " + n))} won by outlasting all other sides.";
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
        if (mission.Stage == MissionStage.Over) return;
        mission.Stage = MissionStage.Over;

        // P765 — per-part timing of the game-over tick. Gated behind the same
        // verbose/profiler switch as the other diagnostic lines so the disabled
        // path stays a plain call (Stopwatch allocation avoided entirely). The
        // one [gameover-timing] line per game over isolates WHERE the ~700 ms
        // stall lands so the P765 fix can target the dominant part and the
        // report can quote before/after numbers.
        var sw = (ConsoleLog.Verbose || TickProfiler.Instance is not null) ? Stopwatch.StartNew() : null;
        double pause0 = 0;
        if (sw is not null)
        {
            _declareAlloc0 = GC.GetTotalAllocatedBytes();
            _declareGc0 = GC.CollectionCount(0);
            _declareGc1 = GC.CollectionCount(1);
            pause0 = GC.GetTotalPauseDuration().TotalMilliseconds;
        }
        if (sw is not null) SwMark(sw, "declare-start", mission, winnerId, 0);

        mission.Skills?.Write(Console.Out, final: true);
        if (sw is not null) SwMark(sw, "skills-report", mission, winnerId);

        // Native finalizes scores, queues GAME_OVER + GAME_OVER_PLAYERS and
        // flushes the mission group before resetting any player/world state
        // (FedSrv/fsmission.cpp:3225-3263,3427-3454). Freeze one result frame
        // and one audience before migration can replace their mission/ships.
        FinalizeMissionScores(mission, winnerId);
        if (sw is not null) SwMark(sw, "finalize-scores", mission, winnerId);

        // P808b — report verified players' final results to the lobby (career
        // stats + leaderboard). Fire-and-forget; never blocks the sim.
        ReportMissionResultsToLobby(mission, winnerId);

        byte[] packet = EncodeGameOverResult(mission, winnerId, reason);

        // Whole-mission audience, scoped to THIS mission: every peer whose ship
        // belongs to it (the mission-scoped sibling of the whole-mission idiom
        // BroadcastAsteroidDestroyed/BroadcastStationDestroyed use, Server.cs).
        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission))
            .Select(kv => kv.Key)
            .ToArray();
        if (sw is not null) SwMark(sw, "encode+audience", mission, winnerId);

        Console.WriteLine($"[gameover] mission #{_missions.IndexOf(mission)} -> Stage Over, " +
                          $"iSideWinner={(winnerId == GameOver.NA ? "NA(draw)" : winnerId.ToString())}, " +
                          $"{targets.Length} peer(s), result packet {packet.Length} bytes; {MatchupDetails(mission)}");
        foreach (var t in targets)
            SendPeer(t, packet);
        if (sw is not null) SwMark(sw, "send-gameover", mission, winnerId);

        // P439 — rotate the finished mission to a freshly-assembled NotStarted
        // one so the server is joinable again (the only way it ever leaves Over
        // today was a full server recreation). Native does this in two steps —
        // ResetMission (missionigc.cpp:3804-3828) then a regenerate at the next
        // StartCountdown (missionigc.cpp:3830+) — but the port generates at
        // assembly, so the rotation collapses the pair into one rebuild. Runs
        // on the shared path so every end state (draw, win-by-elimination,
        // conquest/prosperity/artifact) rotates identically.
        RotateMissionAfterGameOver(mission);

        // P810 — the quiet point: every GAME_OVER packet is on the wire and the
        // finished mission has been rotated. If self-update is opted in, check
        // the release feed and stage a newer build for the next restart (never
        // mid-match — this is the only call site).
        ScheduleServerUpdateCheckAtGameOver();

        if (sw is not null)
        {
            LastGameOverDeclareMs = sw.Elapsed.TotalMilliseconds;
            LastGameOverDeclareGcPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds - pause0;
            Console.WriteLine(
                $"[gameover-timing] mission #{_missions.IndexOf(mission)} winner={(winnerId == GameOver.NA ? "NA" : winnerId.ToString())} " +
                $"peers={targets.Length} elapsedMs={sw.Elapsed.TotalMilliseconds:0.0}");
        }
    }

    /// <summary>P765 — emit the cumulative elapsed ms up to one named game-over
    /// sub-step, so the report can attribute the stall to its dominant part.</summary>
    // P807 — allocation/GC baseline for the game-over tick, captured once in
    // DeclareGameOver so every SwMark reports the tick's cumulative allocation
    // and collection counts (the stall was partly a GC pause, not CPU work).
    private long _declareAlloc0;
    private int _declareGc0, _declareGc1;

    private void SwMark(Stopwatch sw, string part, Mission mission, short winnerId, int peers = -1)
    {
        if (sw is null) return;
        Console.WriteLine(
            $"[gameover-timing] mission #{_missions.IndexOf(mission)} part={part} " +
            $"cumulativeMs={sw.Elapsed.TotalMilliseconds:0.0}" +
            (peers >= 0 ? $" peers={peers}" : "") +
            $" allocMB={(GC.GetTotalAllocatedBytes() - _declareAlloc0) / (1024.0 * 1024.0):0.0} " +
            $"gc={{gen0={GC.CollectionCount(0) - _declareGc0},gen1={GC.CollectionCount(1) - _declareGc1}}}");
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
    /// <para>SimPass iterates a snapshot, so replacement during an Update cannot
    /// invalidate its enumerator. Mission indexes remain stable.</para>
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

        // P754 — notify the site that the mission is terminating, while the
        // mission object and its side/cluster state are still intact
        // (src/Igc/missionigc.cpp:3082).
        mission.Site.TerminateMissionEvent(mission);

        // Broadcast MISSION_STAGE(37) Over to the OLD mission's peers, so
        // already-connected clients get the stage native would give them
        // (SetStage always broadcasts MISSION_STAGE, fsmission.cpp:1750-1768).
        // Done before the swap so _missions.IndexOf(mission) in the broadcast's
        // log is still the real index.
        BroadcastMissionStageToMission(mission, MissionStage.Over);

        // Clear the old mission's per-mission state so nothing keyed by the
        // discarded Mission leaks into (or keeps alive) the old world: the
        // start-countdown deadline, pending ballots and the MapGen tag.
        // (P774 — the score-roster throttle state is gone with fmid 209/219.)
        _startCountdownDeadline.Remove(mission);
        _autoRestartDeadline.Remove(mission);
        _restartWarnings.Remove(mission);
        _ballots.Remove(mission);
        _mapGenMissions.Remove(mission);
        _matchups.Remove(mission);
        _missionConfigs.Remove(mission);   // P802 — the rebuilt mission re-registers its config
        // P841 — the old-player score cache dies with its mission (native
        // ~CFSMission discards m_oldPlayers; a rotated mission's joiners start
        // fresh).
        foreach (var k in _oldPlayerScores.Keys.Where(k => ReferenceEquals(k.Mission, mission)).ToList())
            _oldPlayerScores.Remove(k);
        foreach (var k in _oldPlayerSideIds.Keys.Where(k => ReferenceEquals(k.Mission, mission)).ToList())
            _oldPlayerSideIds.Remove(k);
        // HUD-17: NPC detection entries are never pruned per ship (ids only grow);
        // drop them all with the old world. A missing entry reads as false.
        _shipDetected.Clear();
        // P813c — a parked (reconnect-grace) seat belongs to THIS world; a game
        // over during the gap ends the grace (a peer that reconnects afterwards
        // gets a normal fresh logon into the rotated mission).
        DropParkedShipsForMission(mission);
        RetireBroadcastProjectionCaches();
        // Clients discard the old warning state on Over. An unchanged station
        // layout must be announced again in the new world (trekigc.cpp:327-383
        // computes warnings from each fresh cluster's assets).
        foreach (var side in mission.Sides) _lastSectorAssetMaskBySide.Remove(side.Id);
        if (idx == 0) _clusterInfoBytes = null; // MapGen can rebuild a different graph.

        // Rebuild the world from the SAME config that made the original (native
        // regenerates on the next start; this port assembles now). _config.Games
        // and _missions are index-aligned (one mission per game).
        var swR = (ConsoleLog.Verbose || TickProfiler.Instance is not null) ? Stopwatch.StartNew() : null;
        var fresh = AssembleGameMission(_config.Games[idx], _core, _landingBays, _weaponMuzzles, _stationLandingBays, _collisionHulls);
        _missions[idx] = fresh;
        if (swR is not null)
            Console.WriteLine(FormattableString.Invariant($"[gameover-timing] part=rotate-assemble cumulativeMs={swR.Elapsed.TotalMilliseconds:0.0}"));

        Console.WriteLine($"[gameover] P439: mission #{idx} finished -> rebuilt fresh STAGE_NOTSTARTED " +
                          $"({fresh.Sectors.Count} sectors, {fresh.Sides.Count} sides, {fresh.Warps.Count} warps, " +
                          $"{fresh.Sectors.Sum(s => s.Stations.Count)} stations, " +
                          $"{fresh.Sectors.Sum(s => s.Asteroids.Count)} asteroids); new joiners land here NotStarted");

        // Arm BEFORE migrating: preserved ready flags and debug auto-start must
        // not bypass the intermission. Native ProcessGameOver announces timeStart
        // while NotStarted (fsmission.cpp:3539-3546). Default policy is P660.
        var next = _config.LiveSettings?.AutoNextMatch ?? new AutoNextMatchConfig();
        var gc = _config.Games[idx];
        if (next.Enabled || gc.AutoRestart)
            _autoRestartDeadline[fresh] = _simNow + (next.Enabled
                ? next.IntermissionSeconds : gc.RestartCountdownSeconds ?? 60.0);
        SpawnInitialMiners(fresh);
        if (_config.LiveSettings?.DebugMode == true)
            TryAutoStart(fresh);

        // P480 — migrate the old mission's still-connected peers into the fresh
        // mission, exactly as native Vacate()+Reset(true) leaves them: still
        // connected, seated on the same side id, with a fresh docked/lobby ship,
        // ready cleared and wallet reset, and each re-sent the join-burst a fresh
        // joiner of a NotStarted mission receives (so their client re-learns the
        // new world's clusters/stations/exports and the NotStarted stage).
        var swMigrate = swR is not null ? Stopwatch.StartNew() : null;
        MigrateConnectedPeersIntoRotatedMission(mission, fresh);
        if (swMigrate is not null)
        {
            LastRotateMigrateSetupMs = swMigrate.Elapsed.TotalMilliseconds;
            Console.WriteLine(FormattableString.Invariant(
                $"[gameover-timing] part=rotate-migrate-setup setupMs={swMigrate.Elapsed.TotalMilliseconds:0.0} cumulativeMs={swR!.Elapsed.TotalMilliseconds:0.0}"));
        }
        // P509 re-announce (ApplyTestUnlock) and the P660 intermission countdown
        // broadcast now run in FinishPendingRotation, once the (P765-batched)
        // fresh-joiner join bursts have all drained — they must not fire before
        // every migrated peer is re-synced.
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
    /// <item><b>Ready preserved and wallet reset</b> — individual ready flags
    /// survive (fsmission.cpp:3468-3481); the fresh <see cref="PlayerWallet"/> starts at 0 (native
    /// resets <c>rgfReady</c>/<c>rgfMoney</c> at fsmission.cpp:3493-3521;
    /// P443 then seeds + pays starting money at the next real start).</item>
    /// <item><b>Leadership / ownership re-established</b> — re-seating re-runs
    /// the side-leader + mission-owner election (native AddPlayerToSide), so the
    /// same leader stays leader and the same owner side stays owner.</item>
    /// <item><b>Old-mission per-peer latches cleared</b> — see
    /// <see cref="RunMigrationLatchSweep"/>, so the new world's identity messages
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
    // P765 — in-flight game-over migrations, drained a bounded batch per tick
    // (one at a time, FIFO) so no single tick carries the whole superlinear
    // migration cost. Empty in the normal case; a queue only if two missions
    // end within a few ticks of each other. Drains via <see cref="DrainPendingRotation"/>.
    private readonly List<PendingRotationTail> _pendingRotations = new();

    // P807 — last measured handoff numbers, for the perf gate test (and the
    // report). Updated only while the verbose/profiler timing is enabled, so
    // the disabled path is untouched.
    internal double LastGameOverDeclareMs { get; private set; }
    internal double LastGameOverDeclareGcPauseMs { get; private set; }
    internal double LastRotateMigrateSetupMs { get; private set; }
    internal double MaxGameOverMigrationPhaseMs { get; private set; }
    internal int GameOverMigrationTicks { get; private set; }

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

        // P765/P807 — the whole migration is a resumable state machine drained a
        // bounded batch per tick (DrainPendingRotation / the gameOverMigration
        // step). P765 bounded the NUMBER of peers touched per tick; P807 removed
        // the per-peer O(total latch entries) scans (RunMigrationLatchSweep does
        // one bulk pass per collection) and the mission-wide PLAYERINFO /
        // JOIN_SIDE fan-out per migrant (each peer's own phase-3 join burst
        // carries every ship's PLAYERINFO and the final lobby state). Native
        // never does any of this synchronously at game-over: Vacate()+Reset(true)
        // keeps every player seated in place and normal broadcasts pick them up
        // on the next tick (fsmission.cpp:2604-2622, 3804-3828). Phase ORDER is
        // preserved (latch sweep -> all prepared -> all reseated -> all bursts ->
        // ready recompute) because leadership / CountPlayersOnSide / the P598
        // auto-start all depend on it.
        // A migration no larger than one batch completes in THIS tick, exactly as
        // before P807: several live behaviours (P660's preserved ready flags
        // auto-starting the next mission, P682's viewer-cache retirement) read
        // the post-rotation state synchronously after the game-over call. Only a
        // migration that needs more than one batch is deferred to later ticks.
        bool completesSynchronously = _pendingRotations.Count == 0
            && migrants.Length <= GameOverMigrationBatchPerTick;
        MaxGameOverMigrationPhaseMs = 0;
        GameOverMigrationTicks = 0;
        _pendingRotations.Add(new PendingRotationTail
        {
            Fresh = fresh,
            Index = _missions.IndexOf(fresh),
            Migrants = migrants,
            FreshSideIds = fresh.Sides.Select(s => s.Id).ToHashSet(),
            // P807 — the ONE bulk-clear key sets (see RunMigrationLatchSweep).
            MigrantConns = migrants.Select(m => m.Conn).ToHashSet(),
            OldShipIds = migrants.Select(m => m.Live.Ship.Id).ToHashSet(),
            OldLeaderConns = oldMission.Sides
                .Where(s => s.LeaderShipId is { } lid && lid >= 0)
                .Select(s => (Side: s.Id, Leader: _peerShips.Where(kv => kv.Value.Ship.Id == s.LeaderShipId)
                                    .Select(kv => kv.Key).FirstOrDefault()))
                .Where(t => t.Leader != default)
                .ToDictionary(t => t.Side, t => t.Leader),
            OldOwnerSide = oldMission.MissionOwnerSideId,
        });
        if (completesSynchronously)
        {
            // Drain every phase/sweep step now (the bounded loop is the same
            // resumable state machine, just stepped to completion).
            for (int guard = 0; guard < 1024 && _pendingRotations.Count > 0; guard++)
                DrainPendingRotation();
            return;
        }
        // P807 — a large migration's first batch is deliberately NOT drained
        // here. The game-over tick already carries the synchronous swap +
        // GAME_OVER fan-out; the first latch-sweep/prep slice belongs to the
        // following tick's gameOverMigration step (P765), so the handoff tick
        // itself stays bounded. Native's reset is synchronous but does not
        // re-send a join burst at all (fsmission.cpp:2604-2622,
        // missionigc.cpp:3804-3828).
    }

    /// <summary>P765 — a single in-flight game-over migration: the migrated peers
    /// of a just-rotated mission progressing through the four migration phases, a
    /// bounded batch per tick, so no one tick carries the whole (superlinear)
    /// migration cost. Phase ORDER is fixed (see <see cref="DrainPendingRotation"/>);
    /// only the per-tick batch size is bounded.</summary>
    private sealed class PendingRotationTail
    {
        public required Mission Fresh;
        public required int Index;
        public required (ConnectionId Conn, LiveShip Live)[] Migrants;
        public required HashSet<short> FreshSideIds;
        public required Dictionary<short, ConnectionId> OldLeaderConns;
        public required short? OldOwnerSide;
        // Phase 1 output (built incrementally across prep batches).
        public List<(ConnectionId Conn, LiveShip NewLive, short NewShipId, short SideId, LogonRequest Req, bool IsOldLeader)> Prepared = new();
        public int PrepCursor;
        // Phase 2 reseat order (computed once every peer is prepared).
        public List<(ConnectionId Conn, LiveShip NewLive, short NewShipId, short SideId, LogonRequest Req, bool IsOldLeader)>? Ordered;
        public int ReseatCursor;
        // Phase 3 burst cursor over Prepared.
        public int BurstCursor;
        // P807 — the whole migration's recipient / old-subject sets, used to
        // clear the per-(recipient, ship) identity latches in ONE pass per
        // collection instead of one full scan PER MIGRANT (the prep phase was
        // O(migrants x total latch entries): at 394 peers with a full match's
        // discovery latches that was ~3-10 ms per peer).
        public HashSet<ConnectionId>? MigrantConns;
        public HashSet<short>? OldShipIds;
        public int LatchSweepStep;
        public bool LatchesCleared;
    }

    /// <summary>P765 — how many migrated peers a single tick may carry through a
    /// migration phase (prep / reseat / join burst). Chosen so the per-tick slice
    /// plus the tick's normal work stays far under the ~50 ms budget at the sizes
    /// tested. Bounded by COUNT, not wall time, so the budget is deterministic
    /// and machine-independent.</summary>
    private const int GameOverMigrationBatchPerTick = 8;

    /// <summary>
    /// P765 — run the deferred game-over migration(s): advance the first pending
    /// rotation through one batch of its current phase. Prep a batch of peers,
    /// then reseat a batch, then send a batch of fresh-joiner join bursts; when
    /// every phase is drained, run the trailing steps (P598 ready recompute,
    /// ApplyTestUnlock, the intermission countdown) and pop the queue. Called
    /// every tick from the SimPass pipeline so the work is spread across ticks
    /// rather than held in the game-over tick.
    /// </summary>
    private void DrainPendingRotation()
    {
        var tail = _pendingRotations.Count == 0 ? null : _pendingRotations[0];
        if (tail is null) return;

        // P807 — per-tick attribution of where the handoff cost lands. Gated
        // exactly like the P765 [gameover-timing] lines, so the disabled path
        // is a couple of null checks. One line per phase batch, plus the
        // latch-sweep steps, so a live [slowtick] can be matched to the phase.
        var sw = (ConsoleLog.Verbose || TickProfiler.Instance is not null) ? Stopwatch.StartNew() : null;
        long alloc0 = sw is not null ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long packets0 = sw is not null ? Interlocked.Read(ref _egressPacketsOffered) : 0;
        var phaseSw = sw is not null ? new Stopwatch() : null;
        void Mark(string phase, int batch, int done, int total, string extra = "")
        {
            MaxGameOverMigrationPhaseMs = Math.Max(MaxGameOverMigrationPhaseMs, phaseSw!.Elapsed.TotalMilliseconds);
            GameOverMigrationTicks++;
            Console.WriteLine(FormattableString.Invariant(
                $"[gameover-timing] part=migrate phase={phase} batch={batch} done={done}/{total} phaseMs={phaseSw!.Elapsed.TotalMilliseconds:0.###} cumulativeMs={sw!.Elapsed.TotalMilliseconds:0.0} allocBytes={GC.GetAllocatedBytesForCurrentThread() - alloc0} packets={Interlocked.Read(ref _egressPacketsOffered) - packets0}{extra}"));
        }

        // P807 — clear every latch keyed to a migrant (recipient) or a retired
        // migrant ship (subject) BEFORE the first prep batch. One collection per
        // tick bounds each tick's sweep; the old per-peer sweep re-scanned every
        // collection for every migrant, which is what made rotate-migrate
        // superlinear in peers even after P765 bounded the batch size.
        if (!tail.LatchesCleared)
        {
            if (!RunMigrationLatchSweep(tail, sw)) return;   // one collection per tick
            tail.LatchesCleared = true;
        }

        // Phase 1 — mint/register a fresh ship for a batch of migrants this tick.
        if (tail.PrepCursor < tail.Migrants.Length)
        {
            phaseSw?.Restart();
            int slice = Math.Min(GameOverMigrationBatchPerTick, tail.Migrants.Length - tail.PrepCursor);
            for (int i = 0; i < slice; i++)
            {
                var (conn, live) = tail.Migrants[tail.PrepCursor++];
                // P765 — prep can now run a tick after migration setup, so a peer
                // may have disconnected; skip a closed connection rather than
                // registering a ghost ship for it (it will not be burst or reseated).
                if (_host.Transport?.IsPeerClosed(conn) == true)
                {
                    Console.WriteLine($"[reseat] peer {conn} disconnected before migration prep — skipped");
                    continue;
                }
                short oldSide = live.Ship.SideId ?? -1;
                // P660 policy: spectators/unseated peers also enter the next match.
                // Preserve existing seats; auto-complete side selection for the rest.
                if (!tail.FreshSideIds.Contains(oldSide) && (_config.LiveSettings?.AutoNextMatch?.Enabled ?? true))
                    oldSide = tail.Fresh.Sides.OrderBy(side => tail.Prepared.Count(p => p.SideId == side.Id)
                        + tail.Migrants.Count(p => p.Live.Ship.SideId == side.Id)).ThenBy(side => side.Id).First().Id;
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
                var newLive = AssignMigratedShip(tail.Fresh, newShipId, oldSide != -1 ? oldSide : null);
                // Placement needs the original side to select its hangar, but these
                // prepared peers have not joined yet. Otherwise CountPlayersOnSide
                // sees every migrant in phase 2 and elects no leader at all.
                // Native AddPlayerToSide elects the first joined player
                // (fsmission.cpp:601-624); the ordering below preserves the old leader.
                newLive.Ship.SideId = null;

                // Wallet reset (native fsmission.cpp:3493-3521: a fresh PlayerWallet
                // already starts at 0). Ready is PRESERVED, not cleared — native
                // ProcessGameOver resets only the SIDE aggregate readiness
                // (m_misdef.rgfReady[sideID] = false, fsmission.cpp:3501); the
                // individual player ready flags ("not away-from-keyboard") survive
                // unchanged (the "set everyone unready" block at fsmission.cpp:3468-
                // 3481 is commented out by Imago precisely so a player's ready/AFK
                // state is not reset at game-over). So a migrated peer that was
                // ready before the game-over comes back ready in the fresh mission —
                // the native-correct re-ready state (P598) — and the side aggregates
                // are re-derived from those preserved flags by the Phase 4 recompute.
                // P807 — the O(1) per-recipient / per-ship latches stay here,
                // in the bounded prep batch; only the collections whose
                // removal requires a full scan moved to RunMigrationLatchSweep.
                RetireViewerUpdateCadence(conn);
                _nextTeammateRefreshTick.Remove(conn);
                _lastStatusSector.Remove(conn);
                _lastStatusTick.Remove(conn);
                _lastClusterInfoTick.Remove(conn);
                _teammateLoadoutCache.Remove(live.Ship.Id);
                _shipDetected.Remove(live.Ship.Id);
                foreach (var pk in _pendingJoinRequests
                             .Where(k => k.Value.ShipId == live.Ship.Id).Select(k => k.Key).ToList())
                    _pendingJoinRequests.Remove(pk);

                // Register the fresh ship NOW so later phases' broadcasts reach this
                // peer (its old mission no longer matches any whole-mission sweep).
                _peerShips[conn] = newLive;

                bool isOldLeader = tail.OldLeaderConns.TryGetValue(oldSide, out var lc) && lc == conn;
                tail.Prepared.Add((conn, newLive, newShipId, oldSide, req, isOldLeader));
                Console.WriteLine($"[reseat] peer {conn} -> fresh mission: new ship {newShipId} (was {live.Ship.Id}) targeting side {oldSide}{(isOldLeader ? " (was leader)" : "")}");
            }
            if (sw is not null) Mark("prep", slice, tail.PrepCursor, tail.Migrants.Length);
            if (tail.PrepCursor < tail.Migrants.Length) return;   // more to prep — come back next tick
        }

        // Phase 2 — re-seat a batch onto its ORIGINAL side, preserving the old
        // leader (first joiner of a side becomes its leader) and the old mission
        // owner (owner-side peers re-seat first, so the owner side reclaims
        // ownership). Peers of a side missing from the fresh mission are left
        // unseated.
        tail.Ordered ??= tail.Prepared
            .Where(p => tail.FreshSideIds.Contains(p.SideId))
            .OrderBy(p => p.SideId == tail.OldOwnerSide ? 0 : 1)
            .ThenByDescending(p => p.IsOldLeader)
            .ThenBy(p => p.SideId)
            .ToList();
        if (tail.ReseatCursor < tail.Ordered.Count)
        {
            phaseSw?.Restart();
            double announceMs = 0, joinMs = 0;
            int slice = Math.Min(GameOverMigrationBatchPerTick, tail.Ordered.Count - tail.ReseatCursor);
            for (int i = 0; i < slice; i++)
            {
                var p = tail.Ordered[tail.ReseatCursor++];
                // P775 — native moment (1) for the fresh mission: announce the
                // migrant to the whole fresh mission BEFORE its reseat (the
                // ship is registered in _peerShips and its name resolves), with
                // iSide = SIDE_TEAMLOBBY so no client announces yet — the
                // JOIN_SIDE that TryJoinSide broadcasts is the one announcement
                // (fsmission.cpp:408-411 then :621-624).
                long t0 = phaseSw is not null ? Stopwatch.GetTimestamp() : 0;
                // P807 — keep the P775 stale-subject safety (a freshly allocated
                // id could reuse a retired one) but do NOT fan a PLAYERINFO out
                // to every peer here: each peer's own phase-3 burst below sends
                // one PLAYERINFO per ship to that recipient. Mission-wide
                // announce moved out of the rotation path (native's reset sends
                // no such re-announcement).
                _playerInfoSent.RemoveWhere(e => e.ShipId == p.NewShipId);
                if (phaseSw is not null) announceMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                t0 = phaseSw is not null ? Stopwatch.GetTimestamp() : 0;
                if (TryJoinSideCore(p.NewLive.Ship, p.SideId, announce: false))
                    Console.WriteLine($"[reseat] peer {p.Conn} -> fresh mission side {p.SideId} (ship {p.NewShipId})");
                else
                    Console.WriteLine($"[reseat] peer {p.Conn} -> could NOT re-seat on fresh side {p.SideId}");
                if (phaseSw is not null) joinMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            if (sw is not null) Mark("reseat", slice, tail.ReseatCursor, tail.Ordered.Count,
                FormattableString.Invariant($" announceMs={announceMs:0.##} joinMs={joinMs:0.##} playerInfo={_playerInfoSent.Count} peers={_peerShips.Count}"));
            if (tail.ReseatCursor < tail.Ordered.Count) return;   // more to reseat — next tick
        }

        // Phase 3 — re-send a batch of migrated peers the join-burst a fresh
        // joiner of a NotStarted mission receives (MISSION_STAGE NotStarted, the
        // new world's clusters/stations/EXPORTs, its side/leader/ready info,
        // launch). Reuses the exact existing join-burst path; nothing parallel is
        // hand-built.
        if (tail.BurstCursor < tail.Prepared.Count)
        {
            phaseSw?.Restart();
            int slice = Math.Min(GameOverMigrationBatchPerTick, tail.Prepared.Count - tail.BurstCursor);
            for (int i = 0; i < slice; i++)
            {
                var p = tail.Prepared[tail.BurstCursor++];
                // P765 — the burst now runs on a later tick than the prep, so a
                // peer may have disconnected in between (ReleasePeer already
                // removed its ship). Skip a closed connection instead of letting
                // CompleteJoinAndLaunch re-register a ghost ship for a dead conn.
                if (_host.Transport?.IsPeerClosed(p.Conn) == true)
                {
                    Console.WriteLine($"[reseat] peer {p.Conn} disconnected before join burst — skipped (ship {p.NewShipId})");
                    continue;
                }
                CompleteJoinAndLaunch(p.Conn, p.Req, p.NewShipId, (byte)(p.Req.DwCookie & 0xFF), p.NewLive, announceToMission: false);
                Console.WriteLine($"[reseat] peer {p.Conn} -> re-sent fresh-joiner join burst (ship {p.NewShipId}, stage {tail.Fresh.Stage})");
            }
            if (sw is not null) Mark("burst", slice, tail.BurstCursor, tail.Prepared.Count);
            if (tail.BurstCursor < tail.Prepared.Count) return;   // more bursts — next tick
        }

        FinishPendingRotation(tail);
    }

    /// <summary>P765 — finish a fully-drained rotation tail: P598 side-ready
    /// recompute, the P509 test-unlock re-announce, and the P660 intermission
    /// countdown broadcast (all previously ran inline right after the synchronous
    /// migration; they must run only after every migrated peer's fresh-joiner
    /// burst is on the wire).</summary>
    private void FinishPendingRotation(PendingRotationTail tail)
    {
        // Phase 4 — P598: re-derive each occupied side's aggregate readiness
        // from the migrated peers' PRESERVED ready flags (native's
        // CheckForSideAllReady re-run, fsmission.cpp:5498-5529 — the equivalent
        // of native re-reading rgfReady[] after the side aggregate was reset at
        // ProcessGameOver, fsmission.cpp:3501). A side whose every connected
        // peer was still ready before the game-over comes back aggregate-ready,
        // and RecomputeSideReady's flip fires TryAutoStart, so the rotated
        // mission auto-starts without anyone having to re-click ready — the
        // P598 bug (a rebuilt NotStarted mission that never auto-started until a
        // server restart). Runs AFTER the bursts so every migrated peer's fresh-
        // joiner join burst (MISSION_STAGE NotStarted etc.) is already on the
        // wire before the start countdown's STAGE_STARTING / SET_START_TIME.
        var recomputed = new HashSet<short>();
        foreach (var p in tail.Prepared)
        {
            if (p.NewLive.Ship.SideId is not { } rsid) continue;   // unseated peer
            if (!recomputed.Add(rsid)) continue;                    // once per side
            RecomputeSideReady(p.NewLive.Ship);
        }
        // P509 — re-announce the test-unlock mask to the now-migrated peers of
        // the fresh mission (formerly inline in RotateMissionAfterGameOver).
        ApplyTestUnlock(tail.Fresh);
        if (_autoRestartDeadline.TryGetValue(tail.Fresh, out var deadline))
        {
            BroadcastCountdownTime(tail.Fresh, deadline);
            Console.WriteLine($"[intermission] mission #{tail.Index} next match in {deadline - _simNow:0.###} sim seconds; connected players carried forward");
        }
        _pendingRotations.RemoveAt(0);
    }


    /// <summary>
    /// P807 — clear every per-(recipient, ship) identity latch keyed to a
    /// migrated peer, exactly as the former per-peer clear did, but in ONE pass
    /// per collection for the whole migration instead of
    /// one full scan per migrant. The per-peer form was O(migrants × total
    /// latch entries), which is why a 394-peer handoff still cost ~3-10 ms of
    /// prep PER PEER at the rig's full-match latch volume (the P765 batching
    /// bounded the batch, not the per-peer scan). One collection is swept per
    /// call (one tick), so each tick's slice is bounded by the largest single
    /// collection rather than by the product; a large migration still spends
    /// only ~a dozen extra ticks in its intermission (native has no such sweep:
    /// it never re-sends the join burst and its client keeps the same mission
    /// objects — fsmission.cpp:2604-2622, missionigc.cpp:3804-3828).
    /// <para>Semantics are identical to the per-peer method: free every entry
    /// whose recipient is a migrant, or whose subject ship id is one of the
    /// migrants' retired ships. The recipient dimension covers the object-id
    /// subjects (stations/mines/discovery), which are not ship-linked. Returns
    /// false while more sweeping is needed, true when fully drained.</para>
    /// </summary>
    private bool RunMigrationLatchSweep(PendingRotationTail tail, Stopwatch? sw)
    {
        var conns = tail.MigrantConns!;
        var oldShips = tail.OldShipIds!;
        bool IsRecipient(ConnectionId c) => conns.Contains(c);
        bool IsOldSubject(short id) => oldShips.Contains(id);
        bool ByRecipientOrShip(ConnectionId c, short id) => IsRecipient(c) || IsOldSubject(id);

        var step = tail.LatchSweepStep++;
        var stepSw = sw is not null ? Stopwatch.StartNew() : null;
        switch (step)
        {
            case 0: _teammateExportSent.RemoveWhere(e => ByRecipientOrShip(e.Recipient, e.ShipId)); break;
            case 1: _stationIdentitySent.RemoveWhere(e => IsRecipient(e.Recipient)); break;
            case 2: _playerInfoSent.RemoveWhere(e => ByRecipientOrShip(e.Recipient, e.ShipId)); break;
            case 3: _crossSideExportSent.RemoveWhere(e => ByRecipientOrShip(e.Recipient, e.ShipId)); break;
            case 4: _crossSideMineExportSent.RemoveWhere(e => IsRecipient(e.Recipient)); break;
            case 5: _crossSideDiscoverySent.RemoveWhere(e => IsRecipient(e.Recipient)); break;
            case 6: _buildingEffectExportSent.RemoveWhere(e => IsRecipient(e.Recipient)); break;
            case 7: _sameSideNpcExportSent.RemoveWhere(e => ByRecipientOrShip(e.Recipient, e.ShipId)); break;
            case 8: RemoveKeys(_lastSameSideNpcStatusTick, k => ByRecipientOrShip(k.Recipient, k.ShipId)); break;
            case 9: RemoveKeys(_teammateLoadoutVersionSent, k => ByRecipientOrShip(k.Recipient, k.ShipId)); break;
            case 10: RemoveKeys(_lastTeammateStatusTick, k => ByRecipientOrShip(k.Item1, k.Item2)); break;
            case 11: RemoveKeys(_crossSideShipStatusTick, k => ByRecipientOrShip(k.Item1, k.Item2)); break;
            case 12: RemoveKeys(_crossSideShipStatusSeen, k => IsOldSubject(k.ShipId)); break;
            default:
                // Every scanned collection is now clean. The remaining
                // recipient/ship-keyed entries are O(1) removals and run in the
                // bounded prep batch (see DrainPendingRotation), so this step
                // only marks the sweep complete.
                if (sw is not null)
                {
                    MaxGameOverMigrationPhaseMs = Math.Max(MaxGameOverMigrationPhaseMs, stepSw!.Elapsed.TotalMilliseconds);
                    GameOverMigrationTicks++;
                    Console.WriteLine(FormattableString.Invariant(
                        $"[gameover-timing] part=migrate phase=latch-sweep step={step} doneMs={stepSw.Elapsed.TotalMilliseconds:0.###} peers={conns.Count}"));
                }
                return true;
        }
        if (sw is not null)
        {
            MaxGameOverMigrationPhaseMs = Math.Max(MaxGameOverMigrationPhaseMs, stepSw!.Elapsed.TotalMilliseconds);
            GameOverMigrationTicks++;
            Console.WriteLine(FormattableString.Invariant(
                $"[gameover-timing] part=migrate phase=latch-sweep step={step} doneMs={stepSw.Elapsed.TotalMilliseconds:0.###} peers={conns.Count}"));
        }
        return false;

        static void RemoveKeys<TKey, TValue>(Dictionary<TKey, TValue> dict, Func<TKey, bool> match)
            where TKey : notnull
        {
            List<TKey>? doomed = null;
            foreach (var key in dict.Keys)
                if (match(key)) (doomed ??= new List<TKey>()).Add(key);
            if (doomed is null) return;
            foreach (var key in doomed) dict.Remove(key);
        }
    }

    /// <summary>
    /// P583 — the port of <c>ProcessGameOver</c>'s per-player scoring
    /// (<c>fsmission.cpp:3178-3263</c>): compute each side's rank sum, derive
    /// each player's rank ratio, sync the ship's repair credit into the score
    /// object, set <c>TimePlayed</c> (this port: elapsed since the peer seated
    /// — the collapsed <c>Connect</c>/<c>Disconnect</c> accounting,
    /// <c>igc.h:5602-5613</c>), set the win flag for the winning side, and run
    /// the full <see cref="PlayerScore.CalculateScore"/> with the mission
    /// core's real float constants. Called from <see cref="DeclareGameOver"/>
    /// before the game-over roster is built so the end screen shows real
    /// scores.
    /// </summary>
    private void FinalizeMissionScores(Mission mission, short winnerId)
    {
        var fc = mission.Core.Constants.FloatConstants;
        if (fc is null || fc.Length <= PlayerScore.CombatRatingDivideIndex)
        {
            Console.WriteLine("[scoring] mission core has no usable FloatConstants — skipping score finalize");
            return;
        }

        // Real sides currently seated on this mission, ascending (the port's
        // analogue of native's side iteration, fsmission.cpp:3182-3188).
        var presentSides = mission.Sides
            .Where(s => s.Id >= 0 && _peerShips.Any(kv =>
                ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == s.Id))
            .Select(s => s.Id)
            .OrderBy(x => x)
            .ToArray();

        short RankSum(short sideId)
        {
            int sum = 0;
            foreach (var kv in _peerShips)
            {
                if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
                if (kv.Value.Ship.SideId != sideId) continue;
                int r = kv.Value.PlayerScore.Rank;
                sum += r < 1 ? 1 : r;          // GetSideRankSum, fsmission.cpp:1365
            }
            return (short)Math.Max(sum, 1);
        }

        // Rank ratio per side: ratio = opposing side's rank sum / own side's
        // rank sum, only when exactly two sides are present (the >2 case keeps
        // the neutral 1.0, exactly as native's `if (sideCount == 2)` guard,
        // fsmission.cpp:3189).
        var ratioFor = new Dictionary<short, float>();
        if (presentSides.Length == 2)
        {
            short s0 = presentSides[0], s1 = presentSides[1];
            float r0 = RankSum(s0), r1 = RankSum(s1);
            ratioFor[s0] = r1 / r0;            // ratio0 = rank[1]/rank[0], fsmission.cpp:3196
            ratioFor[s1] = r0 / r1;            // ratio1 = rank[0]/rank[1], fsmission.cpp:3197
        }

        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            if (kv.Value.Ship.SideId is not { } sid) continue;
            var score = kv.Value.PlayerScore;
            score.SetRepair(kv.Value.Ship.Repair);                                   // FedSrv.CPP:3704-3709
            score.SetTimePlayed((float)Math.Max(0.0, _simNow - kv.Value.ConnectSimTime));
            score.SetRankRatio(ratioFor.TryGetValue(sid, out var rat) ? rat : 1.0f);
            score.SetWin(winnerId != GameOver.NA && sid == winnerId);
            score.CalculateScore(fc);
            Console.WriteLine($"[scoring] ship {kv.Value.Ship.Id} (side {sid}) final score {score.Score:R} " +
                              $"kills={score.Kills} deaths={score.Deaths} rescues={score.Rescues} ejections={score.Ejections} " +
                              $"baseKills={score.BaseKills:R} baseCaptures={score.BaseCaptures:R} flags={score.Flags} artifacts={score.Artifacts} " +
                              $"win={score.Win} ratio={score.RankRatio:R} played={score.TimePlayed:F0}s");
        }
    }

    /// <summary>Per-server monotonic counter for the lobby match id, so a
    /// finished match has a unique, stable id even as missions rotate (P808b).</summary>
    private int _careerReportSeq;

    /// <summary>
    /// P808b — gather each verified player's final per-civ result on
    /// <paramref name="mission"/> and fire-and-forget it to the lobby. The port of
    /// native's <c>SetCharStats</c> persist upload (<c>FedSrv/FedSrv.CPP:1045-1140</c>):
    /// only players who logged in with a verified lobby account are reported (the
    /// anti-abuse rule that results come only from verified identities), and only
    /// when this server is itself enrolled (verified) — anonymous/standalone
    /// servers have no lobby record to credit. Called from the single shared
    /// <see cref="DeclareGameOver"/> path right after
    /// <see cref="FinalizeMissionScores"/> so the results reflect the final scores
    /// before ships migrate to the rotated mission. Never blocks the sim.
    /// <para>P855/SIM-0765 — the report also carries the finished squad game's
    /// standings facts (native <c>RecordSquadGame</c>, gated at
    /// fsmission.cpp:3421-3427, body FedSrv.CPP:1198-1300) when the mission
    /// qualifies — a squad game, scores counting, a decisive winner, two teams,
    /// sides 0 and 1 in different squads.</para>
    /// </summary>
    private void ReportMissionResultsToLobby(Mission mission, short winnerId)
    {
        if (_heartbeat is null) return;
        var entries = new List<LobbyMatchResultEntry>();
        // P855 — the match id is minted for the whole report, not per player:
        // native posts the squad-game query independent of the per-player
        // SetCharStats output, so a squad game with no verified players must
        // still reach the lobby.
        string matchId = $"m{_careerReportSeq++:D6}-{(long)_simNow}";
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            if (kv.Value.Ship.SideId is not { } sid) continue;
            string? accountId = AccountId(kv.Key);
            if (accountId is null) continue;          // verified accounts only (anti-abuse)
            // P841 / SIM-0764 — native SetCharStats returns before posting the
            // query when the player's time played is zero (FedSrv.CPP:1051-1054,
            // under !ALLSRV_STANDALONE): a peer who never really played leaves
            // no stat row. The port mirrors that gate on its collapsed
            // Connect/Disconnect accounting (the same TimePlayed the score
            // finalize sets).
            if (kv.Value.PlayerScore.TimePlayed <= 0f) continue;
            short civ = 0;
            var side = mission.Sides.FirstOrDefault(s => s.Id == sid);
            if (side?.Civilization is { } civObj) civ = (short)civObj.CivilizationId;
            var score = kv.Value.PlayerScore;
            entries.Add(new LobbyMatchResultEntry(
                AccountId: accountId, Civ: civ, Score: score.Score, CombatRating: score.CombatRating,
                Kills: score.Kills, Deaths: score.Deaths,
                BaseKills: score.BaseKills, BaseCaptures: score.BaseCaptures,
                Minutes: (int)(score.TimePlayed / 60.0),
                Win: score.Win, Loss: winnerId != GameOver.NA && sid != winnerId,
                CommanderWin: false, CommanderLoss: false, ScoresCount: true,
                // P844/SIM-0526 — the remaining native CQPlayerResults columns
                // (fsmission.cpp:2969-2986): ejections, rescues, flags,
                // artifacts, techs recovered, alephs/asteroids spotted and the
                // exact time played (native nTimePlayed).
                Ejections: score.Ejections, Rescues: score.Rescues,
                Flags: score.Flags, Artifacts: score.Artifacts,
                TechsRecovered: score.TechsRecovered,
                WarpsSpotted: score.WarpsSpotted, AsteroidsSpotted: score.AsteroidsSpotted,
                TimePlayedSeconds: score.TimePlayed));
        }

        // P844/SIM-0524 — the game-level row (native CQGameResults,
        // fsmission.cpp:2768-2790): game name, winner, duration, and the goal
        // switches + values. Duration anchors on the started transition
        // (native nDuration = GetGameDuration() = g.timeNow - timeStart,
        // fsmission.cpp:3175); a mission that never started reports 0.
        int gameIdx = _missions.IndexOf(mission);
        var winnerSide = mission.Sides.FirstOrDefault(s => s.Id == winnerId);
        float duration = mission.CountdownStart > 0
            ? (float)Math.Max(0.0, mission.LastUpdate - mission.CountdownStart)
            : 0f;
        var game = new LobbyGameResult(
            GameName: gameIdx >= 0 ? _config.Games[gameIdx].Name : null,
            WinnerSideId: winnerId,
            WinnerName: winnerSide?.Name,
            DurationSeconds: duration,
            GoalConquest: mission.ConquestEnabled,
            GoalCountdown: mission.Goals.IsCountdownGame,
            GoalTeamKills: mission.KillsEnabled,
            GoalProsperity: mission.ProsperityEnabled,
            GoalArtifacts: mission.ArtifactEnabled,
            GoalFlags: mission.FlagsEnabled,
            GoalConquestPercentage: mission.ConquestWinThreshold,
            GoalCountdownLength: mission.Goals.GameLengthSeconds,
            GoalTeamKillsCount: mission.KillsWinGoal,
            GoalProsperityMoney: mission.Goals.TeamMoney,
            GoalArtifactsCount: mission.ArtifactWinGoal,
            GoalFlagsCount: mission.FlagsWinGoal);

        // P844/SIM-0525 — the per-side rows (native RecordTeamResults, walked
        // for every side of the mission, fsmission.cpp:2804-2821).
        var teams = mission.Sides.Select(s => new LobbyTeamResult(
            TeamNumber: s.Id,
            Name: s.Name ?? $"side{s.Id}",
            Faction: s.Civilization.Name,
            Kills: s.Kills, BaseKills: s.BaseKills, BaseCaptures: s.BaseCaptures,
            Deaths: s.Deaths, Ejections: s.Ejections,
            Flags: s.Flags, Artifacts: s.Artifacts,
            ConquestPercent: s.ConquestPercent, TerritoryCount: s.TerritoryCount)).ToList();

        // P855/SIM-0765 — the native squad-game gate, verbatim
        // (fsmission.cpp:3421-3427): a squad game whose scores count, with a
        // winner (no draws), exactly two teams, and side 0's squad different
        // from side 1's. (bScoresCount has no port equivalent — every port
        // mission counts scores, LobbyGameParams.FromMission.) RecordSquadGame
        // itself (FedSrv.CPP:1198-1300) then walks the sides collecting the
        // losing squads — every side with a squad that is not the winner's,
        // in side order (the port keeps the walk general; native unrolls it
        // into squadIDLost1..5 and asserts at most 5). No stage, minimum-player
        // or minimum-time condition exists in native.
        LobbySquadGameResult? squadGame = null;
        if (mission.SquadGame && winnerId != GameOver.NA && mission.Sides.Count == 2
            && !string.Equals(mission.Sides[0].SquadId, mission.Sides[1].SquadId, StringComparison.Ordinal))
        {
            string? winnerSquad = mission.Sides.FirstOrDefault(s => s.Id == winnerId)?.SquadId;
            var losers = mission.Sides
                .Where(s => s.SquadId is not null && !string.Equals(s.SquadId, winnerSquad, StringComparison.Ordinal))
                .Select(s => s.SquadId!)
                .ToList();
            squadGame = new LobbySquadGameResult(winnerSquad, losers);
        }

        if (entries.Count == 0 && squadGame is null) return;
        Console.WriteLine($"[lobby] career report: {entries.Count} verified player(s), match {matchId}"
                          + (squadGame is null ? "" : $", squad game: winner {(squadGame.WinnerSquadId ?? "NA")} vs {squadGame.LoserSquadIds.Count} loser squad(s)"));
        _ = _heartbeat.ReportResultsAsync(new LobbyMatchReport(matchId, entries, SquadGame: squadGame, Game: game, Teams: teams));
    }

    /// <summary>Build the per-player endgame roster for a mission (the port's
    /// stand-in for native's per-player <c>GAME_OVER_PLAYERS</c> list inside
    /// <c>QueueGameoverMessage</c>, fsmission.cpp:3094-3141): one
    /// <see cref="GameOverPlayerEntry"/> per currently-connected peer on a real
    /// side of the mission, in <c>_peerShips</c> order, then every departed
    /// player's cached score appended after (P844/SIM-0527 — native walks
    /// <c>m_oldPlayers</c> second, fsmission.cpp:3123-3135, with the
    /// <c>GetTimePlayed() != 0.0f</c> and non-team-lobby gates). Shared by the
    /// full whole-mission game-over (<see cref="EncodeGameOverResult"/>) and the
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
            // P844/SIM-0527 — native's roster gate (fsmission.cpp:3118):
            // <c>ppso->GetTimePlayed() > 0.0f</c> — a player who never flew is
            // not on the endgame roster. The port's collapsed Connect/
            // Disconnect accounting sets TimePlayed at score-finalize time, so
            // the side-only path (which runs before any finalize) falls back to
            // the seat-time delta — the same quantity the finalize writes.
            float played = live.PlayerScore.TimePlayed;
            if (played <= 0f)
                played = (float)Math.Max(0.0, _simNow - live.ConnectSimTime);
            if (played <= 0f)
                continue;
            string name = _peerNames.TryGetValue(kv.Key, out var n) ? n : $"Ship {live.Ship.Id}";
            entries.Add(new GameOverPlayerEntry(
                CharacterName: name,
                SideId: sid,
                Rank: live.PlayerScore.Rank,
                Score: live.PlayerScore.Score,
                Kills: (short)Math.Clamp(live.PlayerScore.Kills, short.MinValue, short.MaxValue),
                Deaths: (short)Math.Clamp(live.PlayerScore.Deaths, short.MinValue, short.MaxValue),
                Ejections: (short)Math.Clamp(live.PlayerScore.Ejections, short.MinValue, short.MaxValue),
                Rescues: (short)Math.Clamp(live.PlayerScore.Rescues, short.MinValue, short.MaxValue),
                BaseKills: (short)Math.Clamp((int)live.PlayerScore.BaseKills, short.MinValue, short.MaxValue),
                BaseCaptures: (short)Math.Clamp((int)live.PlayerScore.BaseCaptures, short.MinValue, short.MaxValue),
                Artifacts: (short)Math.Clamp(live.PlayerScore.Artifacts, short.MinValue, short.MaxValue),
                Flags: (short)Math.Clamp(live.PlayerScore.Flags, short.MinValue, short.MaxValue),
                Assists: (short)Math.Clamp(live.PlayerScore.Assists, short.MinValue, short.MaxValue),
                TimePlayed: played));
        }

        // P844/SIM-0527 — departed players: native appends every
        // <c>OldPlayerInfo</c> whose time played is non-zero and whose side is
        // not the team lobby (fsmission.cpp:3123-3135), so a player who left
        // mid-game still appears on the endgame screen with their final stats.
        // The cache entries die with their mission (the P439 rotation clears
        // them, matching native ~CFSMission discarding m_oldPlayers).
        foreach (var kvp in _oldPlayerScores)
        {
            if (!ReferenceEquals(kvp.Key.Mission, mission)) continue;
            if (kvp.Value.TimePlayed == 0f) continue;  // native opi.pso.GetTimePlayed() != 0.0f gate
            if (!_oldPlayerSideIds.TryGetValue(kvp.Key, out var oldSid) || oldSid < 0)
                continue;                              // team-lobby analogue
            var s = kvp.Value;
            entries.Add(new GameOverPlayerEntry(
                CharacterName: kvp.Key.Name,
                SideId: oldSid,
                Rank: s.Rank,
                Score: s.Score,
                Kills: (short)Math.Clamp(s.Kills, short.MinValue, short.MaxValue),
                Deaths: (short)Math.Clamp(s.Deaths, short.MinValue, short.MaxValue),
                Ejections: (short)Math.Clamp(s.Ejections, short.MinValue, short.MaxValue),
                Rescues: (short)Math.Clamp(s.Rescues, short.MinValue, short.MaxValue),
                BaseKills: (short)Math.Clamp((int)s.BaseKills, short.MinValue, short.MaxValue),
                BaseCaptures: (short)Math.Clamp((int)s.BaseCaptures, short.MinValue, short.MaxValue),
                Artifacts: (short)Math.Clamp(s.Artifacts, short.MinValue, short.MaxValue),
                Flags: (short)Math.Clamp(s.Flags, short.MinValue, short.MaxValue),
                Assists: (short)Math.Clamp(s.Assists, short.MinValue, short.MaxValue),
                TimePlayed: s.TimePlayed));
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
        // Native side-only results use the same queue + guaranteed flush,
        // addressed only to the eliminated side (fsmission.cpp:5420-5426).
        byte[] packet = EncodeGameOverResult(mission, winnerId, reason);

        Console.WriteLine($"[sidegameover] mission #{_missions.IndexOf(mission)} -> side-only GAME_OVER(62) \"{reason}\" + " +
                          $"GAME_OVER_PLAYERS(63) to {targets.Count} peer(s) on the eliminated side (mission NOT ended, no rotation)");
        foreach (var t in targets)
            SendPeer(t, packet);
    }

    /// <summary>Queue the mission result and full player roster into one
    /// guaranteed frame, as native QueueGameoverMessage + FM_FLUSH does
    /// (FedSrv/fsmission.cpp:3094-3141,3427-3429). Both full-mission and
    /// side-only results use this builder; the caller owns the audience.
    /// The roster carries the native time-played gate and the departed-player
    /// tail (P844/SIM-0527). Native's 50-player message chunks are
    /// preserved within the frame so large rosters use the same wire shape.
    /// </summary>
    private byte[] EncodeGameOverResult(Mission mission, short winnerId, string reason)
    {
        var messages = new List<byte[]> { GameOver.ForMission(mission, winnerId, reason).Encode() };
        var entries = BuildGameOverPlayerEntries(mission);
        // Keep the existing empty-roster notification for connected spectators.
        if (entries.Length == 0)
            messages.Add(new GameOverPlayers { Entries = [] }.Encode());
        // Native queues chunks from the end of the roster (fsmission.cpp:3131-3141).
        for (int remaining = entries.Length; remaining > 0;)
        {
            int count = Math.Min(remaining, 50);
            remaining -= count;
            messages.Add(new GameOverPlayers { Entries = entries[remaining..(remaining + count)] }.Encode());
        }
        Console.WriteLine($"[gameover-players] mission #{_missions.IndexOf(mission)} -> {entries.Length} player(s) queued with GAME_OVER");
        return FedPacket.Pack(messages);
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
    /// <item><b>Gate: child ship or admin.</b> Native permits a boarded child
    /// or CanCheat player (FedSrv.CPP:6338); the port's configured admin
    /// predicate is shared with spectator/cheat access.</item>
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
        // Native FedSrv.CPP:6338: only a child ship or a CanCheat player.
        if (ship.ParentShip is null && !IsSpectatorAdmin(from)) return;
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
        var settings = GameSettingsSafe(mission);
        if (peersOnSide.Length < Math.Max(1, settings.MinPlayersPerTeam))
        {
            // Floor: fewer connected players than native's nMinPlayersPerTeam
            // → forced not-ready (fsmission.cpp:5501-5503). P852 — the port
            // previously hardcoded a 1-player floor; GameConfig now carries
            // native's parameter (default 1, igc.h:1333).
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
        if (mission.Stage != MissionStage.NotStarted || _autoRestartDeadline.ContainsKey(mission))
            return;   // intermission holds even when everybody is ready
        var settings = GameSettings(mission);
        // P762 / SIM-0142 — native bAutoStart gate (FedSrv.CPP:8223): the
        // ready-up auto-start only exists when the mission parameter enables it.
        if (!settings.AutoStart) return;
        // P180 — the port's readiness gate (every occupied side aggregate-ready)
        // is a DELIBERATE port policy, the port's stand-in for the external
        // admin/Zone start trigger (P180 report); it is NOT part of native
        // FAllReady. Native's "every side ready" loop there is unreachable dead
        // code after the earlier `return true` (fsmission.cpp:4432-4444), so
        // FAllReady is the balance check only (fsmission.cpp:4375-4430). Keep
        // this separate so the port's ready-up behaviour is preserved without
        // misrepresenting native.
        if (!IsAllOccupiedSidesReady(mission))
            return;

        // Native's FAllReady balance policy: min/max players, imbalance, rank
        // spread and the bAllowEmptyTeams inactive-empty skip
        // (fsmission.cpp:4375-4430). Arena-style missions (bAllowEmptyTeams)
        // keep the port's established occupied-side rule as a fallback: native's
        // arena AutoRestart path bypasses FAllReady entirely (FedSrv.CPP:8185),
        // and an empty arena has no players to satisfy the native minimum-player
        // clause. This does NOT put the ready loop back into FAllReady.
        if (!FAllReady(mission) && !settings.AllowEmptyTeams)
            return;

        // P188 — route the ready-up auto-start through the real countdown
        // stage instead of jumping straight to Started. The ENTER_GAME
        // broadcast now happens once the countdown elapses
        // (AdvanceStartCountdowns → CompleteStartCountdown →
        // BroadcastEnterGameToMission), not here; StartCountdown emits the
        // SET_START_TIME countdown announcement and sets Stage = Starting.
        // P762 — native StartCountdown(fStartCountdown), FedSrv.CPP:8225.
        Console.WriteLine("[ready] FAllReady — mission auto-start begins countdown");
        StartCountdown(mission, (float)(settings.StartCountdownSeconds ?? BriefingCountdownSeconds));
    }

    /// <summary>
    /// P762b / SIM-0142 — the native <c>CFSMission::FAllReady</c> policy
    /// (<c>FedSrv/fsmission.cpp:4375-4432</c>), which is the <b>balance check
    /// only</b>:
    /// <list type="number">
    /// <item>Scan every side, skipping inactive empty teams when
    /// <see cref="GameConfig.AllowEmptyTeams"/> is set (fsmission.cpp:4394-4410).</item>
    /// <item>Reject when the smallest team has fewer than
    /// <see cref="GameConfig.MinimumPlayersPerTeam"/>, the largest exceeds
    /// <see cref="GameConfig.MaxPlayersPerTeam"/>, the smallest plus
    /// <see cref="GameConfig.MaxTeamImbalance"/> is less than the largest, or —
    /// in rank-balance mode (<c>iMaxImbalance == 0x7ffe</c>) — the per-side rank
    /// spread exceeds <see cref="GetRankThreshold"/> (fsmission.cpp:4423-4430).</item>
    /// <item>Otherwise return true.</item>
    /// </list>
    /// Native's following "every side ready" loop (<c>fsmission.cpp:4435-4444</c>)
    /// is <b>dead code</b>: the balance-check block's own <c>return true</c>
    /// (<c>fsmission.cpp:4432</c>) sits before it, so it can never execute.
    /// P762 had mistakenly made that loop part of FAllReady; P762b removes it so
    /// this method matches native exactly. The port's ready-up gate lives in
    /// <see cref="TryAutoStart"/> as the separate, documented P180 policy.
    /// The native team population is the side's ship list; every port ship is
    /// either a peer (a real player) or an NPC, and only peers count toward the
    /// native player filters, so the counts and rank sums walk
    /// <c>_peerShips</c>. Rank sums floor each rank at one
    /// (<c>fsmission.cpp:1365</c>) and <see cref="GetRankThreshold"/> follows
    /// the native integer formula (<c>fsmission.cpp:1380-1408</c>).
    /// </summary>
    private bool FAllReady(Mission mission)
    {
        var settings = GameSettings(mission);
        int minPlayers = settings.MaxPlayersPerTeam;   // native's "big enough" seed
        int maxPlayers = 0;
        int minTeamRank = 1_000_000;
        int maxTeamRank = 1;
        foreach (var side in mission.Sides)
        {
            int n = 0;
            int rankSum = 0;
            foreach (var kv in _peerShips)
            {
                if (!ReferenceEquals(kv.Value.Ship.Mission, mission) || kv.Value.Ship.SideId != side.Id) continue;
                n++;
                rankSum += Math.Max(1, (int)kv.Value.PlayerScore.Rank);
            }
            // KGJV #62 — skip inactive & empty teams when allowed.
            if (settings.AllowEmptyTeams && n == 0 && !side.Active) continue;
            if (n < minPlayers) minPlayers = n;
            if (n > maxPlayers) maxPlayers = n;
            if (rankSum < minTeamRank) minTeamRank = rankSum;
            if (rankSum > maxTeamRank) maxTeamRank = rankSum;
        }

        int threshold = GetRankThreshold(mission);
        if (minPlayers < settings.MinimumPlayersPerTeam
            || maxPlayers > settings.MaxPlayersPerTeam
            || minPlayers + settings.MaxTeamImbalance < maxPlayers
            || (settings.MaxTeamImbalance == 0x7ffe && maxTeamRank - minTeamRank > threshold))
            return false;

        // Native returns true here, BEFORE the "every side ready" loop — which
        // is therefore unreachable dead code (fsmission.cpp:4432-4444). Do not
        // restore that loop here; the port's readiness gate is P180's separate
        // TryAutoStart policy.
        return true;
    }

    /// <summary>
    /// P762b / SIM-0142 — native <c>CFSMission::GetRankThreshold</c>
    /// (<c>FedSrv/fsmission.cpp:1380-1408</c>):
    /// <c>threshold = highestRank + averageRank² / (highestRank + averageRank)</c>
    /// (all integer arithmetic). Native divides by <c>m_pMission-&gt;GetShips()</c>
    /// — the mission's <b>whole ship list</b>, which includes docked ships
    /// (docking removes a ship from its cluster list but not from the mission
    /// list, <c>fsmission.cpp:1405</c>). The port keeps that shape: only
    /// player ranks are summed (native loops the list but adds only
    /// <c>IsPlayer()</c> ranks), while the divisor counts every ship of the
    /// mission — peers and NPCs, flying and docked. A docked port ship remains
    /// in its sector's model list (it is only excluded from
    /// <see cref="Sector.GetShipUpdateCandidates"/>), so sector models are the
    /// mission list here; peer ships are unioned in by identity as a defensive
    /// completeness check. Never use the flying-ships-only accessor here.
    /// </summary>
    private int GetRankThreshold(Mission mission)
    {
        int highest = 1;
        int totalRank = 0;
        var missionShips = new HashSet<Ship>();
        foreach (var sector in mission.Sectors)
            foreach (var model in sector.Models)
                if (model is Ship ship) missionShips.Add(ship);
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship.Mission, mission)) missionShips.Add(kv.Value.Ship);
        int shipCount = missionShips.Count;
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            int rank = kv.Value.PlayerScore.Rank;
            if (rank > highest) highest = rank;
            totalRank += rank;
        }
        int average = shipCount > 0 ? totalRank / shipCount : 0;
        return highest + (average * average) / (highest + average);
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
        if (mission.Stage != MissionStage.NotStarted || _autoRestartDeadline.ContainsKey(mission))
            return;   // intermission is authoritative; native allows re-starting from STAGE_STARTING too, but
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

        int n = SendToMissionPeers(mission, CountdownPacket(target));
        Console.WriteLine($"[start] mission #{_missions.IndexOf(mission)} countdown begun ({countdownSeconds}s) -> STAGE_STARTING, SET_START_TIME(14) whole-mission to {n} peer(s)");
    }

    // SET_START_TIME is native (fsmission.cpp:1931-1938, 3539-3546).
    // P660 adaptation: include a PING server-clock sample so a client can use
    // the absolute deadline even with a different epoch or time dilation.
    private byte[] CountdownPacket(double target) => FedPacket.Pack(new[]
    {
        new Ping { TimeServer = (uint)(_simNow * 1000.0) }.Encode(),
        new SetStartTime { TimeStart = (uint)(target * 1000.0) }.Encode(),
    });

    private void BroadcastCountdownTime(Mission mission, double target) =>
        SendToMissionPeers(mission, CountdownPacket(target));

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
            if (now <= target) continue; // FedSrv.CPP:8238, strict expiry
            if (!GameSettings(m).AllowEmptyTeams && !HasMissionPlayers(m)) continue;
            CompleteStartCountdown(m, now);
        }
    }

    /// <summary>
    /// P598 — the per-tick driver for native's bAutoRestart timer: any
    /// NotStarted mission armed by <see cref="RotateMissionAfterGameOver"/>
    /// (GameConfig.AutoRestart) whose restart deadline has passed enters the
    /// normal start countdown (StartCountdown), so a game-over rotation
    /// auto-restarts without a ready-up — native's unattended restart
    /// (fsmission.cpp:3539-3546 sets timeStart, and the per-tick loop starts
    /// the game when it elapses). Guards on NotStarted so a mission that
    /// already left (via the ready-up path) is a no-op; a not-armed mission is
    /// skipped outright. Runs on the sim thread right after the start-
    /// countdown advance in <see cref="SimPass"/>.
    /// </summary>
    private void AdvanceAutoRestarts(double now)
    {
        foreach (var m in _missions)
        {
            if (m.Stage != MissionStage.NotStarted) { _autoRestartDeadline.Remove(m); _restartWarnings.Remove(m); continue; }
            if (!_autoRestartDeadline.TryGetValue(m, out var deadline)) continue;
            var settings = GameSettings(m);
            bool arenaNextMatch = (_config.LiveSettings?.AutoNextMatch ?? new AutoNextMatchConfig()).Enabled;
            if (!arenaNextMatch)
            {
                double remaining = deadline - now;
                if (remaining > BriefingCountdownSeconds)
                {
                    // P762 / SIM-0142 — native's warning test is FAllReady
                    // (FedSrv.CPP:8212-8220), not just the occupancy floor.
                    if (!settings.AllowEmptyTeams && remaining <= 2 * BriefingCountdownSeconds &&
                        _restartWarnings.Add(m) && !FAllReady(m))
                        SendPeriodicNotice(m, "Warning: Game will be canceled if all teams are not ready in 30 seconds.");
                    continue;
                }
                if (!settings.AllowEmptyTeams)
                    foreach (var side in m.Sides) side.Ready = true; // native force-ready
                if (!settings.AllowEmptyTeams && !FAllReady(m))
                {
                    if (remaining <= 0)
                    {
                        m.Stage = MissionStage.Over;
                        _autoRestartDeadline.Remove(m);
                        _restartWarnings.Remove(m);
                        BroadcastMissionStageToMission(m, MissionStage.Over);
                        SendPeriodicNotice(m, "Game canceled due to lack of players");
                    }
                    continue;
                }
            }
            else if (now < deadline) continue;
            _restartWarnings.Remove(m);
            foreach (var peer in _peerShips.Where(kv => ReferenceEquals(kv.Value.Ship.Mission, m)))
                _playerReady[peer.Key] = true;
            foreach (var peer in _peerShips.Values.Where(v => ReferenceEquals(v.Ship.Mission, m)).ToArray())
                RecomputeSideReady(peer.Ship);
            _autoRestartDeadline.Remove(m);
            StartCountdown(m, arenaNextMatch ? BriefingCountdownSeconds : (float)Math.Max(0, deadline - now));
        }
    }

private void CompleteStartCountdown(Mission mission, double now)
    {
        mission.Stage = MissionStage.Started;
        mission.CountdownStart = _startCountdownDeadline[mission];
        _startCountdownDeadline.Remove(mission);

        // P797 — seed every side's starting visibility the moment the mission
        // actually starts: native runs the bShowMap reveal here
        // (missionigc.cpp:4056-4071) and the first Cluster::Update pass (every
        // model's UpdateSeenBySide, clusterIGC.cpp:347-357) is what makes each
        // side's own stations/ships — and everything within their scanner range
        // — known at start. Doing it on this transition means the very first
        // join export and the first ASTEROIDS/STATIONS update carry real per-side
        // answers instead of an all-false slate.
        mission.SeedStartingVisibility();

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

/// <summary>P820c — move a peer from its side to the team lobby while staying in
    /// the mission: the real remove-from-side path a squad boot uses (native
    /// <c>RemovePlayerFromSide</c>, fsmission.cpp:848-1137 — the same quit-half
    /// <c>TryJoinSideCore</c> runs before a side switch). Runs leader succession
    /// if the leaver is the side's leader (a no-op for a plain member), parks
    /// the ship on <c>SIDE_TEAMLOBBY</c> (-2, igc.h:514) and broadcasts
    /// <c>QUIT_SIDE(reason)</c> whole-mission so every client updates its roster
    /// exactly as native's client does (clintlib/appmsg.cpp:2993-3000). P820b's
    /// silent <c>Ship.SideId = -2</c> write (no broadcast, no bookkeeping) is
    /// replaced by this path.</summary>
    private void MoveToTeamLobby(Mission mission, Ship ship, QuitSideReason reason)
    {
        if (ship.SideId is not { } oldSideId) return;
        HandleLeaderSuccession(ship);
        // P841 — native RemovePlayerFromSide zeroes the leaver's money into
        // the old side's treasury while the game runs (fsmission.cpp:1042-1063;
        // GiveSideMoney fsmission.cpp:1777). The ship-value leg is not applied
        // here: this port's side-quit keeps the ship alive (native resets the
        // player and re-seats them, so the hull value is lost to the player),
        // so crediting its value too would mint credits.
        // P847 — order fixed to native's: the payday AMOUNT is captured and the
        // wallet zeroed before the move (fsmission.cpp:1066-1068), but
        // GiveSideMoney runs AFTER `pfsPlayer->SetSide(..., SIDE_TEAMLOBBY)`
        // (fsmission.cpp:1141-1145) — so DoPayday's player count no longer
        // includes the leaver. Paying before the move split the money one extra
        // way and the leaver's share vanished with the wallet reset.
        LiveShip? live = LiveOf(ship);
        long payday = 0;
        if (live is not null)
        {
            payday = mission.Stage is MissionStage.Started or MissionStage.Starting
                ? Math.Max(0, live.Wallet.Money) : 0;
            live.Wallet.Money = 0;   // pfsPlayer->SetMoney(0), fsmission.cpp:1068
        }
        ship.SideId = SideTeamLobby;
        BroadcastQuitSide(mission, ship.Id, reason);
        MarkObjectSyncDirty(oldSideId);
        if (payday > 0 && mission.Sides.FirstOrDefault(s => s.Id == oldSideId) is { } side)
        {
            // GiveSideMoney(psideOld, payday) = accrue + IMMEDIATE DoPayday
            // (fsmission.cpp:1145 → 1777-1787). P847: the treasury credit
            // alone left the leaver's money waiting for the next natural
            // payday instead of paying the remaining side-mates right away.
            GiveSideMoney(side, payday);
        }
    }

// ═══════════════════ P850 — inbound QUIT_SIDE / SET_TEAM_LEADER ═══════════════════

    /// <summary>P850 — inbound <c>FM_CS_QUIT_SIDE(126)</c>, the client half of
    /// the quit/boot branch. Port of <c>FedSrv.CPP:6823-6906</c> (re-verified
    /// this session); the closest sibling is P762's
    /// <see cref="HandleQuitMission"/> but the leaver is moved to the team
    /// lobby — <em>staying in the mission</em> — via <see cref="MoveToTeamLobby"/>
    /// (the port of native <c>RemovePlayerFromSide</c>'s quit-side effect),
    /// never removed from the mission.
    /// <para><b>Moderators.</b> Native's Steam moderator/developer group branch
    /// (<c>:6848-6856</c>) removes any player with any reason + message. The
    /// port's equivalent identity is <see cref="ServerConfig.ModeratorAccounts"/>
    /// (the same bearer-secret <see cref="IsModerator"/> model P762 uses).</para>
    /// <para><b>Ordinary players.</b> Only quitting themselves or, as the side
    /// leader, booting a same-side teammate is accepted (<c>:6862-6864</c>).
    /// The reason must match exactly (<c>TrapHackBoot</c>, <c>:6870</c>):
    /// <see cref="QuitSideReason.Quit"/> for self,
    /// <see cref="QuitSideReason.LeaderBooted"/> for a boot. A boot is refused
    /// while <see cref="GameConfig.LockTeamSettings"/> is set (<c>:6889</c>).
    /// Native's locked-sides <c>DELPOSITIONREQ(DPR_SidesLocked)</c> block
    /// (<c>:6875-6885</c>) is unreachable in this port: it requires the sender's
    /// own side to be <c>SIDE_TEAMLOBBY</c>, which an ordinary player already
    /// parked there hits earlier at the <c>sideID &lt; 0</c> gate (<c>:6836</c>)
    /// — that degenerate re-send is handled gracefully by refusing at the same
    /// gate. Native's banned-side-mask kick-out-of-mission leg
    /// (<c>:6893-6900</c>) is N/A: this port has no banned-side-mask concept
    /// (stated in the P195 rebalance writeup) and is deliberately omitted.</summary>
    private void HandleQuitSide(ConnectionId from, Mission mission, Ship senderShip, QuitSide request)
    {
        // Native :6833-6837 — resolve the sender's own side. A NON-moderator
        // with no real side (sideID < 0; includes a player already parked on
        // SIDE_TEAMLOBBY/-2) is refused outright.
        bool isModerator = IsModerator(from);
        short sideId = senderShip.SideId ?? Na;   // Na = -1, the no-side sentinel
        if (sideId < 0 && !isModerator) return;

        // Native :6841-6846 — the target ship; not found (already gone) -> nothing.
        Ship? foundTarget = null;
        foreach (var kv in _peerShips)
        {
            var cand = kv.Value?.Ship;
            if (cand is null) continue;
            if (cand.Id == request.ShipId && ReferenceEquals(cand.Mission, mission))
            { foundTarget = cand; break; }
        }
        if (foundTarget is null) return;
        Ship targetShip = foundTarget;

        // Native :6848-6856 — moderator/administrator branch: remove the target
        // (any reason/message), no self/leader gate.
        if (isModerator && LiveOf(targetShip) is not null)   // pfssAck->IsPlayer()
        {
            MoveToTeamLobby(mission, targetShip, request.Reason);
            return;
        }

        // Native :6862-6864 — the player must be the team leader or booting
        // themselves (the client might mistakenly think it is the leader).
        bool self = ReferenceEquals(senderShip, targetShip);
        bool senderIsSideLeader = sideId >= 0
            && mission.Sides.FirstOrDefault(s => s.Id == sideId)?.LeaderShipId == senderShip.Id;
        bool targetOnSenderSide = sideId >= 0 && targetShip.SideId == sideId;
        if (!self && !(senderIsSideLeader && targetOnSenderSide)) return;

        // Native :6867-6870 — a boot targets someone else; the reason must be
        // exactly QSR_LeaderBooted (boot) or QSR_Quit (self) (TrapHackBoot).
        bool bIsBoot = senderShip.Id != request.ShipId;
        if (request.Reason != (bIsBoot ? QuitSideReason.LeaderBooted : QuitSideReason.Quit))
        {
            ReportTrapHack(TrapHackKind.Boot, from, senderShip.Id, "pfmQuitSide->reason == (bIsBoot ? QSR_LeaderBooted : QSR_Quit)", "FedSrv.CPP:6870");
            return;
        }

        // Native :6886-6901 — move to the team lobby unless a locked team's
        // settings forbid a boot (:6889: !bIsBoot || !bLockTeamSettings).
        if (!bIsBoot || !GameSettings(mission).LockTeamSettings)
        {
            MoveToTeamLobby(mission, targetShip, request.Reason);
            Console.WriteLine($"[quitside] ship {targetShip.Id} left side -> team lobby (reason {request.Reason}, boot={bIsBoot}, initiated by ship {senderShip.Id})");
        }
        else
        {
            Console.WriteLine($"[quitside] leader boot of ship {targetShip.Id} refused — team settings locked");
        }
    }

    /// <summary>P850 — inbound <c>FM_CS_SET_TEAM_LEADER(121)</c>, the client
    /// half of the leader-transfer branch. Port of <c>FedSrv.CPP:7624-7687</c>
    /// minus native's bot-auth sub-branches: the <c>isBot</c> escape hatches
    /// (bots may take command regardless of leadership, <c>:7639,7641</c>) and
    /// the "steal all the other players' money" donation grab (<c>:7654-7683</c>)
    /// are OUT OF SCOPE — this port has no bot-authentication concept
    /// (<c>szBotAuthenticationGuid</c>). An ordinary side leader transfers
    /// leadership to a same-side teammate through <see cref="SetLeader"/>
    /// (native <c>CFSMission::SetLeader</c>: command wing, SET_WINGID,
    /// SET_TEAM_LEADER).</summary>
    private void HandleSetTeamLeader(ConnectionId from, Mission mission, Ship senderShip, SetTeamLeader request)
    {
        // Native :7635-7637 — the sender's side must be a real side.
        if (senderShip.SideId is not { } sideId) return;
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null) return;

        // Native :7639 — the sender must be the current team leader (the isBot
        // escape hatch is N/A in this port).
        if (side.LeaderShipId != senderShip.Id) return;

        // Native :7641 — the named side must be the sender's own side.
        if (request.SideId != sideId) return;

        // Native :7643-7645 — the target must be a player on the sender's side.
        Ship? foundTarget = null;
        foreach (var kv in _peerShips)
        {
            var cand = kv.Value?.Ship;
            if (cand is null) continue;
            if (cand.Id == request.ShipId && ReferenceEquals(cand.Mission, mission))
            { foundTarget = cand; break; }
        }
        if (foundTarget is null) return;
        if (foundTarget.SideId != sideId) return;

        // Native :7651-7652 — CFSMission::SetLeader: command wing 0 + side
        // SET_WINGID, then the whole-mission SET_TEAM_LEADER (fsmission.cpp:1510-1553).
        SetLeader(mission, side, foundTarget);
        Console.WriteLine($"[setleader] ship {senderShip.Id} transferred leadership of side {sideId} to ship {foundTarget.Id} — SET_TEAM_LEADER(121) whole-mission");
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
            // native: pside->GetActiveF() (fsmission.cpp:5977) — a DEACTIVATED
            // side (e.g. resigned mid-game, its players dead but still connected)
            // never gets voters on a mission-wide ballot.
            if (mission.Sides.FirstOrDefault(s => s.Id == sid) is not { Active: true }) continue;
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
    /// MutinyBallot::OnPassed (fsmission.cpp:6098-6143): the P197
    /// donation/wallet transfer (fsmission.cpp:6121-6136), then the full
    /// <see cref="SetLeader"/> primitive (wing 0 + side-group SET_WINGID +
    /// SET_TEAM_LEADER, fsmission.cpp:1510-1553, called at :6139).</summary>
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

        SetLeader(mission, side, initiator);
    }

/// <summary>The leadership-change primitive — the port of native
/// <c>CFSMission::SetLeader</c> (fsmission.cpp:1510-1553), used by every native
/// caller that needs the FULL leadership change (mutiny
/// <c>MutinyBallot::OnPassed</c>, fsmission.cpp:6139; squad re-fit
/// <c>MaintainSquadLeadership</c>, fsmission.cpp:1717):
/// <list type="number">
/// <item>Put the new leader on the COMMAND WING (0) and broadcast
/// <c>SET_WINGID(106) {wingID=0, shipID, bCommanded=true}</c> to the side's
/// group (fsmission.cpp:1533-1541; the old-leader wing block above it is
/// commented out upstream — Imago #91 — so it is a no-op and is not ported).</item>
/// <item>Change the leader (<c>SetLeaderID</c>, fsmission.cpp:1545) and
/// broadcast <c>SET_TEAM_LEADER(121)</c> to the whole mission
/// (fsmission.cpp:1547-1553).</item>
/// </list>
/// Native's <c>SetLeaderID</c> additionally stops/starts the deposed/new
/// leader's command-time score bookkeeping (<c>PlayerScoreObject::
/// StopCommand/StartCommand</c>, fsmission.cpp:1470/1480); this port's
/// <see cref="PlayerScore"/> tracks no command-time counter, so that stat
/// branch has no port counterpart (documented gap on SIM-0504).</summary>
    private void SetLeader(Mission mission, Side side, Ship newLeader)
    {
        short sideId = side.Id;

        // Command wing 0 + side-group SET_WINGID (fsmission.cpp:1533-1541;
        // the side-group analogue HandleSetWingId broadcasts, Server.
        // ShipCommands.cs — _peerShips filtered to this side, not the sector).
        newLeader.WingId = 0;
        byte[] wingPacket = FedPacket.Pack(new[]
        {
            new SetWingId { ShipID = newLeader.Id, WingId = 0, BCommanded = true }.Encode(),
        });
        int nWing = 0;
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            if (kv.Value.Ship.SideId != sideId) continue;
            SendPeer(kv.Key, wingPacket);
            nWing++;
        }
        Console.WriteLine($"[lead] side {sideId} new leader ship {newLeader.Id}: SET_WINGID(106) wing 0 (commanded) to {nWing} side peer(s)");

        // SetLeaderID + SET_TEAM_LEADER whole-mission (fsmission.cpp:1545-1553).
        side.LeaderShipId = newLeader.Id;
        BroadcastSetTeamLeader(mission, sideId, newLeader.Id);
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

/// <summary>Resign passing — the port of native ResignBallot::OnPassed
    /// (fsmission.cpp:6154-6163): announce (already done by
    /// <see cref="ResolveBallots"/>), then — when the mission has STARTED —
    /// deactivate the resigning side through the shared P455 port of native
    /// <c>CFSMission::DeactivateSide</c> (fsmission.cpp:5318-5427): the side
    /// goes inactive, every ship on it is stripped and killed (players lose the
    /// ability to respawn), SIDE_INACTIVE(68) + "&lt;name&gt; is no more." go
    /// out whole-mission, and the win-by-elimination check runs (a lone
    /// surviving side wins; none → "All sides were destroyed"). This replaces
    /// P187's old "withdraw every player" substitution, which is now redundant:
    /// P455 built the faithful DeactivateSide. Native's guard is
    /// <c>sideID &gt;= 0 &amp;&amp; STAGE_STARTED == GetStage()</c>
    /// (fsmission.cpp:6158).</summary>
    private void ApplyResign(Mission mission, PendingBallot ballot)
    {
        if (mission.Stage != MissionStage.Started) return;   // native STAGE_STARTED gate, fsmission.cpp:6158
        short sideId = ballot.VotingSideId;
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null) return;
        DeactivateSide(mission, side);
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
    /// / OnFailed (fsmission.cpp:6037-6052): "&lt;proposal&gt; has passed /
    /// been defeated.  &lt;tally&gt;", sent to the ballot's audience.</summary>
    private void AnnounceBallotResult(Mission mission, PendingBallot ballot, bool passed)
    {
        string verdict = passed ? "has passed." : "has been defeated.";
        SendChatAnnouncement(mission, ballot, $"{ballot.Proposal} {verdict}  {TallyString(mission, ballot)}");
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
            // Native SendChat(NULL, chattarget, m_groupID, NA, ...) sets
            // cd.oidRecipient = m_groupID (fsmission.cpp:6041/6050/6059 via
            // FedSrvSiteBase::SendChat, FedSrv.CPP) — the voting side for a
            // side-scoped ballot, NA for the mission-wide AcceptDraw.
            OidRecipient = ballot.IsMissionWide ? Na : ballot.VotingSideId,
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
    /// 5999-6034), transcribed exactly: ONE opening "(" before the first side
    /// with any votes, sides separated by ", ", ONE closing ")" at the end;
    /// within a side the segments " N for" / " M against" / " K abstained" are
    /// appended space-separated (no comma). The side NAME + ":" prefix is
    /// included only when the ballot's chat target is not CHAT_TEAM — i.e. for
    /// the mission-wide AcceptDraw (native m_chattarget, fsmission.cpp:5905 vs
    /// 5960); side-scoped ballots name no side.</summary>
    private string TallyString(Mission mission, PendingBallot ballot)
    {
        var sb = new System.Text.StringBuilder();
        bool first = true;
        for (int s = 0; s < PendingBallot.SideSlots; s++)
        {
            if (ballot.Abstaining[s] + ballot.InFavor[s] + ballot.Opposed[s] == 0) continue;
            sb.Append(first ? "(" : ", ");
            first = false;
            if (ballot.IsMissionWide)   // native: m_chattarget != CHAT_TEAM
                sb.Append(mission.Sides.FirstOrDefault(x => x.Id == s)?.Name ?? $"side{s}").Append(':');
            if (ballot.InFavor[s] > 0) sb.Append(' ').Append(ballot.InFavor[s]).Append(" for");
            if (ballot.Opposed[s] > 0) sb.Append(' ').Append(ballot.Opposed[s]).Append(" against");
            if (ballot.Abstaining[s] > 0) sb.Append(' ').Append(ballot.Abstaining[s]).Append(" abstained");
        }
        if (!first) sb.Append(')');
        return sb.ToString();
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
        // P852 — native RandomizeSides turns auto-accept OFF on every side that
        // has it on before moving anyone (KGJV change, "changed to turn on auto
        // accept off", fsmission.cpp:5142-5160), broadcasting AUTO_ACCEPT(52)
        // per toggled side. After a rebalance, joining a led side needs the
        // leader's approval again — exactly native.
        foreach (var side in mission.Sides)
        {
            if (!side.AutoAccept) continue;
            side.AutoAccept = false;
            var aa = new AutoAccept { SideId = side.Id, FAutoAccept = false };
            int n = SendToMissionPeers(mission, FedPacket.Pack(new[] { aa.Encode() }));
            Console.WriteLine($"[randomizeteams] side {side.Id}: AUTO_ACCEPT(52) fAutoAccept=false whole-mission -> {n} peer(s) (fsmission.cpp:5142-5160)");
        }

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
                if (CheckPositionRequest(mission, sideId, live.Ship) is { } refusal)
                    RefuseSideSelection(targetConn, live.Ship.Id, sideId, refusal);
                else TryJoinSide(live.Ship, sideId);
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
    /// P758 — native leader succession on a mid-game leader exit prefers the
    /// player with the most teammates donating to them (<c>PickNewLeader</c>,
    /// fsmission.cpp:955-961, FedSrv.CPP:13981-14024); when no donation points
    /// at a remaining player, native falls back to a squad-capable player, then
    /// to the first other player (fsmission.cpp:963-986). Squads
    /// (<c>GetCanLeadSquad</c>) are not ported, so the fallback is the first
    /// other player by ship id — deterministic and testable. The lobby/side-
    /// unlock side effect native does when the owner vanishes
    /// (fsmission.cpp:1031-1062) is still a deliberate no-op
    /// (<c>LOCK_LOBBY</c>/<c>LOCK_SIDES</c> aren't ported); rank-range/squad-
    /// name resets are omitted for the same reason.
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
            Ship? newLeader = null;
            if (mission.Stage == MissionStage.Started)
            {
                // fsmission.cpp:955-961: a leader leaving a STARTED mission is
                // replaced by whoever has the most teammates donating to them.
                var donationWinner = PickNewLeader(remaining, ship, 0);
                if (donationWinner is not null && remaining.Any(s => ReferenceEquals(s, donationWinner)))
                    newLeader = donationWinner;
            }
            // fsmission.cpp:963-986: otherwise (not started, or no donation
            // winner) prefer a remaining player who can lead the side's current
            // squad (GetCanLeadSquad, fsship.cpp:1374-1390), then the first
            // remaining player. P820b adds the squad-capable preference.
            newLeader ??= PreferSquadCapableLeader(remaining, side, remaining[0]);
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
            // SIM-0510 — native RemovePlayerFromSide's tail (fsmission.cpp:
            // 1154-1155): when the leader quit a NOT-STARTED mission whose side
            // still has a squad, re-fit the squad and (if needed) the leader.
            if (mission.Stage == MissionStage.NotStarted && side.SquadId is not null)
                MaintainSquadLeadership(mission, side, ship);
            return;
        }

        // Side is now empty: clear its leader, then migrate the owner role if needed.
        side.LeaderShipId = null;
        // P820c — a side that empties before the start also clears its squad
        // (native fsmission.cpp:1008-1009: "set the squad to NA if the game
        // hasn't started"); once started the squad is kept for scoring, so no
        // clear here mid-game. The squad is established afresh when a new
        // leader joins the empty side (EstablishSideSquadFromLeader).
        if (mission.Stage == MissionStage.NotStarted)
            ClearSideSquad(mission, side);

        // P852 — native RemovePlayerFromSide's last-player rules for the side
        // the leader just left (fsmission.cpp:930-952, 1155-1158) run at the
        // END of the removal, AFTER the succession and owner migration
        // (SetAutoAccept(psideOld, true) is the function's tail): before the
        // game starts the side turns auto-accept ON — AUTO_ACCEPT(52)
        // broadcast (:934-941) and the sweep accepting every pending
        // requester — while once it is running the pending requests are
        // "implicitly rejected" (:945-951 RejectSideJoinRequests → boot each
        // requester). Sitting here (after owner migration, on both exit
        // paths) lets a sweep-seated requester be elected owner exactly like
        // native's AddPlayerToSide does.
        bool wasOwner = mission.MissionOwnerSideId == oldSideId;
        if (wasOwner)
        {
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
                break;
            }
            // No other occupied side: the mission has no owner — nothing to announce.
            // (Native unlocks the lobby/sides here via LOCK_LOBBY/LOCK_SIDES;
            //  neither is ported — separate, unscoped item — so it is a deliberate no-op.)
        }

        if (mission.Stage == MissionStage.NotStarted)
        {
            side.AutoAccept = true;
            var aa = new AutoAccept { SideId = oldSideId, FAutoAccept = true };
            int nAa = SendToMissionPeers(mission, FedPacket.Pack(new[] { aa.Encode() }));
            Console.WriteLine($"[autoaccept] side {oldSideId} emptied pre-game => AUTO_ACCEPT(52) fAutoAccept=true whole-mission -> {nAa} peer(s) (fsmission.cpp:934-941,1158)");
            SweepAutoAcceptPending(mission, oldSideId);
        }
        else
        {
            int rejected = 0;
            foreach (var pend in _pendingJoinRequests.Where(kv => kv.Value.SideId == oldSideId).ToList())
            {
                _pendingJoinRequests.Remove(pend.Key);
                BroadcastDelPositionReq(mission, pend.Value.ShipId, pend.Value.SideId, DelPositionReqReason.Rejected);
                rejected++;
            }
            if (rejected > 0)
                Console.WriteLine($"[posreq] side {oldSideId} emptied mid-game => {rejected} pending join request(s) implicitly rejected (fsmission.cpp:945-951)");
        }
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
    internal bool TryJoinSide(Ship ship, short newSideId) => TryJoinSideCore(ship, newSideId, announce: true);

    /// <summary>P807 — the rotation's silent re-seat. Same AddPlayerToSide state
    /// machine as <see cref="TryJoinSide"/>, but no mission-wide JOIN_SIDE /
    /// SET_TEAM_LEADER / SET_MISSION_OWNER fan-out: the peer's own phase-3 join
    /// burst carries that lobby state, and native re-announces nothing at a
    /// reset (fsmission.cpp:2604-2622, 3804-3828).</summary>
    private bool TryJoinSideCore(Ship ship, short newSideId, bool announce)
    {
        if (ship.Mission is not Mission mission) return false;
        var targetSide = mission.Sides.FirstOrDefault(s => s.Id == newSideId);
        if (targetSide is null) return false;

        // Quit the old side first (native AddPlayerToSide, fsmission.cpp:562-563).
        // P852 — a ship parked on SIDE_TEAMLOBBY (a pended side-switch, P852's
        // RequestPosition park) is NOT on a side: native skips
        // RemovePlayerFromSide when GetSide() == SIDE_TEAMLOBBY
        // (fsmission.cpp:4748-4750), so no second QUIT_SIDE goes out on the
        // eventual accept — the lobby park already announced the departure.
        short? oldSideId = ship.SideId;
        short? prevRealSide = oldSideId is { } osv && osv != newSideId ? osv : null;
        if (oldSideId is { } os && os != newSideId)
        {
            HandleLeaderSuccession(ship);
            if (announce && os != SideTeamLobby)
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
            // P820b/P820c — the side's squad is established when a TEAM LEADER
            // joins an empty side (native AddPlayerToSide, fsmission.cpp:661-690),
            // gated on mission.SquadGame (a non-squad game has no side squad).
            // This also covers the announce:false rotation reseat, where the
            // fresh mission re-derives each side's squad from its reseated
            // leader (native re-creates the mission and leaders re-join).
            EstablishSideSquadFromLeader(mission, targetSide);
            if (newMissionOwner)
                mission.MissionOwnerSideId = newSideId;
        }

        ship.SideId = newSideId;

        // P324 (M4): a peer seating on this side must get an immediate per-side
        // object sync (stations/asteroids/probes) rather than wait for the
        // every-10 cadence — the faithful "send on cluster entry" native behavior.
        MarkObjectSyncDirty(newSideId);

        // P807 — a rotation re-seat passes announce:false: every peer is being
        // re-synced by its own join burst in the same migration, which carries
        // the final lobby state (SET_TEAM_LEADER / SET_MISSION_OWNER in
        // CompleteJoinAndLaunch) and every ship's PLAYERINFO. Native does not
        // re-announce the players it keeps seated across a reset
        // (fsmission.cpp:2604-2622, 3804-3828), and the mission-wide fan-out
        // per migrant is what made the handoff O(peers²) sends.
        if (announce)
        {
            BroadcastJoinSide(mission, ship.Id, newSideId);
            if (newLeader)
            {
                BroadcastSetTeamLeader(mission, newSideId, ship.Id);
                if (newMissionOwner)
                    BroadcastSetMissionOwner(mission, newSideId, ship.Id);
            }
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

        // P852 — native RemovePlayerFromSide's readiness tail for the side the
        // switcher LEFT: `else CheckForSideAllReady(psideOld)`
        // (fsmission.cpp:1146-1152 — the deactivation branch is P548/P455's
        // peer-departure scope; a side SWITCH only re-checks readiness). The
        // port previously left the old side's aggregate Ready stale when a
        // member (possibly its only ready one) switched away.
        if (prevRealSide is { } oldReal && oldReal != SideTeamLobby)
        {
            var oldSide = mission.Sides.FirstOrDefault(s => s.Id == oldReal);
            if (oldSide is { Ready: true })
            {
                var remaining = _peerShips.Values
                    .FirstOrDefault(l => ReferenceEquals(l.Ship.Mission, mission) && l.Ship.SideId == oldReal);
                if (remaining is not null)
                    RecomputeSideReady(remaining.Ship);
                else
                {
                    // Emptied by the switch: fewer than nMinPlayersPerTeam → not ready.
                    oldSide.Ready = false;
                    BroadcastTeamReady(mission, oldReal, false);
                }
            }
        }

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

        // P653 H-03: free-spawn provenance — server-granted spawn hardware has no
        // purchase cost to refund, for BOTH the default and injected spawn paths.
        ship.MarkSpawnLoadoutFree();

        // P841 / SIM-0521 — an old player rejoining this mission restores their
        // cached score (native AddPlayerToMission's GetOldPlayerLink branch,
        // fsmission.cpp:378-401: kills/deaths/ejections/score carry over), and
        // the cache entry is consumed (native deletes the OldPlayerLink in
        // AddPlayerToSide, fsmission.cpp:572-577).
        var playerScore = TryTakeOldPlayerScore(mission, req.CharacterName) ?? new PlayerScore();

        // Native fresh seats enter the hangar (fsmission.cpp:791-796).
        // Include built-in debug arenas used by MCP BUY_LOADOUT(fLaunch).
        // An explicitly replaced test hook still owns its placement/state.
        if (ShipSpawner is null || ReferenceEquals(ShipSpawner, _hangarShipSpawner))
            DockInitialLoadout(ship, sector);
        return new LiveShip(ship, sector, ship.Position, ship.Orientation.GetForward(), ship.Orientation.GetUp(), playerScore, new PlayerWallet(), ship.ShieldFraction, false, ConnectSimTime: _simNow);
    }

    /// <summary>P841 — consume the cached old-player score for
    /// <paramref name="characterName"/> on <paramref name="mission"/>, or
    /// null when the name has no departed-seat history on this mission (the
    /// port of native <c>CFSMission::GetOldPlayerLink</c>,
    /// fsmission.cpp:2608-2602/378, case-insensitive like native's
    /// <c>_stricmp</c> comparison, fsmission.h:390-398). A hit means the caller
    /// re-seats the returning player with their previous score object.
    /// P847: the cache's key comparer is case-insensitive on the name, so this is
    /// a plain dictionary take — the same single entry per name
    /// <c>SaveAsOldPlayer</c> maintains by updating what
    /// <c>GetOldPlayerLink</c> finds (fsmission.cpp:2624-2631).</summary>
    private PlayerScore? TryTakeOldPlayerScore(Mission mission, string? characterName)
    {
        if (string.IsNullOrEmpty(characterName)) return null;
        if (!_oldPlayerScores.Remove((mission, characterName), out var score)) return null;
        _oldPlayerSideIds.Remove((mission, characterName));   // P844 — the side id dies with the entry
        return score;
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
    private LiveShip AssignMigratedShip(Mission mission, short shipId, short? requestedSideId = null)
    {
        var (ship, sector) = SpawnDefaultShip(mission, shipId, requestedSideId);
        DockInitialLoadout(ship, sector);
        return new LiveShip(ship, sector, ship.Position, ship.Orientation.GetForward(), ship.Orientation.GetUp(), new PlayerScore(), new PlayerWallet(), ship.ShieldFraction, false, ConnectSimTime: _simNow);
    }

    /// <summary>P390 — move a freshly-spawned <paramref name="ship"/> into the
    /// home sector of <paramref name="sideId"/> and park it near that side's own
    /// home station, patching <see cref="Ship.SideId"/> to match. Used only to
    /// reconcile an explicitly-requested side against an injected ShipSpawner
    /// that dropped the ship elsewhere; returns the sector the ship now lives
    /// in.</summary>
    /// <summary>P841 — the runtime stations of <paramref name="sector"/> that
    /// <paramref name="sideId"/> owns, in mission order (the list
    /// <see cref="RepositionShipToSideHome"/> and the GetBase port scan
    /// filter).</summary>
    internal static IEnumerable<Station> StationRuntimesForSide(Sector sector, short sideId) =>
        sector.StationRuntimes.Where(st => st.SideId == sideId);

    internal Sector RepositionShipToSideHome(Ship ship, Mission mission, short sideId)
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
        // P841 / SIM-0502 — the station picked is native GetBase(pside)'s
        // answer (fsmission.cpp:1433-1447): the side's first station with the
        // c_sabmRestart capability (the restart/home base), not merely the
        // first side-owned station; fall back to the first owned station when
        // the side owns no restart-capable one (GetBase returns NULL there,
        // and the port still needs a spawn anchor).
        Vector3 basePos;
        var rt = StationRuntimesForSide(home, sideId).FirstOrDefault(st => st.Type.HasCapability(DataStationTypeIGC.SabmRestart))
                 ?? StationRuntimesForSide(home, sideId).FirstOrDefault();
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

    /// <summary>P852 — the auto-pick, the port of native PickNewSide
    /// (fsmission.cpp:4454-4524) with bAllowTeamLobby effectively false (this
    /// port has no team lobby to return) and bannedSideMask always 0 (the port
    /// keeps no banned-side state — stated n/a). Per side, in mission order:
    /// the side is a candidate only when CheckPositionRequest == NA
    /// (fsmission.cpp:4481; inactive sides are refused there, covering
    /// native's rgfActive guard at :4475). Among candidates remember the
    /// lowest rank sum (default choice, :4494) and the fewest players
    /// (:4501). The imbalance override (:4506-4516) replaces the rank choice
    /// with the fewest-players choice when seating the newcomer would exceed
    /// fewestPlayers + iMaxImbalance; native's duplicated
    /// <c>nImbalance != 0x7fff</c> test (:4509) is a native bug kept as one
    /// check. No candidate → returns -1 with <paramref name="refusal"/> set
    /// (native returns SIDE_TEAMLOBBY; this port has no lobby to return to).
    /// The rank sum floors every player's rank at one, native GetSideRankSum
    /// (fsmission.cpp:1352-1369).</summary>
    private short AutoPickSide(Mission mission, ConnectionId from, string? name, out DelPositionReqReason refusal)
    {
        int fewestPlayers = int.MaxValue;
        long lowestRankSum = long.MaxValue;
        int chosenSidePlayers = 0;
        Side? fewestSide = null, chosenSide = null;
        DelPositionReqReason? firstRefusal = null;
        foreach (var s in mission.Sides)
        {
            if (CheckInitialPositionRequest(from, name, mission, s.Id) is { } r)
            {
                firstRefusal ??= r != DelPositionReqReason.TeamFull ? r : null;
                continue;
            }
            int players = _peerShips.Values.Count(l => ReferenceEquals(l.Ship.Mission, mission) && l.Ship.SideId == s.Id);
            long rankSum = _peerShips.Values
                .Where(l => ReferenceEquals(l.Ship.Mission, mission) && l.Ship.SideId == s.Id)
                .Sum(l => (long)Math.Max(1, (int)l.PlayerScore.Rank));
            if (rankSum < lowestRankSum)
            {
                lowestRankSum = rankSum;
                chosenSide = s;
                chosenSidePlayers = players;
            }
            if (players < fewestPlayers)
            {
                fewestPlayers = players;
                fewestSide = s;
            }
        }
        var settings = GameSettingsSafe(mission);
        if (chosenSide is not null && settings.MaxTeamImbalance != 0x7fff
            && chosenSidePlayers + 1 > fewestPlayers + settings.MaxTeamImbalance)
            chosenSide = fewestSide;   // override rank choice with imbalance choice (fsmission.cpp:4513-4515)
        if (chosenSide is not null)
        {
            refusal = DelPositionReqReason.TeamFull;
            return chosenSide.Id;
        }
        refusal = firstRefusal ?? DelPositionReqReason.TeamFull;
        return -1;
    }

/// <summary>P281 — resolve a pending (not-yet-spawned) peer's side selection
    /// and complete its join+launch. Called from the side-select drain
    /// (<see cref="AdvanceSideSelection"/>) for a peer in
    /// <see cref="_pendingSideSelect"/> with a recorded POSITIONREQ choice (a
    /// real side = explicit human choice), or, when the opt-in P763 auto-seat is
    /// enabled, on its deadline (NA).
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

        // P854 — native MISSION-JOIN gate (FedSrv.CPP:581-594): in a private game a
        // player who is not invited to ANY side is refused at mission entry — before
        // side selection — with DELPOSITIONREQ(DPR_PrivateGame) (native sets
        // iSide = NA). Mirrored here with the mission-level IsInvited ("any side",
        // fsmission.cpp:1182-1192). The peer stays pending (native keeps the
        // connection in the lobby) and every subsequent attempt is refused the same
        // way. A player invited to at least one side passes and reaches the per-side
        // gate below, which still bars them from sides that didn't invite them
        // (fsmission.cpp:4605).
        if (mission.RequiresInvitation && !mission.IsInvited(pend.Request.CharacterName ?? "", PlayerInvitationSquads(from)))
        {
            RefuseSideSelection(from, pend.ShipId, -1 /* native iSide = NA */, DelPositionReqReason.PrivateGame, pend.Request.CharacterName);
            _pendingSideSelect[from] = new PendingSideSelect(pend.ShipId, pend.Request, SimNow, pend.JoinSeq);
            return;
        }

        bool autoSelection = requestedSideId == -1;

        // Validate both explicit and auto-assigned initial picks, before spawning.
        // Choose the least populated eligible side for auto-pick; no full-side
        // fallback can bypass the cap when the timeout seats a silent peer.
        DelPositionReqReason autoRefusal = DelPositionReqReason.TeamFull;
        if (requestedSideId == -1)
        {
            requestedSideId = AutoPickSide(mission, from, pend.Request.CharacterName, out autoRefusal);
        }
        var selectionRefusal = requestedSideId == -1 ? autoRefusal : CheckInitialPositionRequest(from, pend.Request.CharacterName, mission, requestedSideId);
        if (selectionRefusal is { } denied)
        {
            RefuseSideSelection(from, pend.ShipId, requestedSideId, denied, pend.Request.CharacterName);
            _pendingSideSelect[from] = new PendingSideSelect(pend.ShipId, pend.Request, SimNow, pend.JoinSeq);
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
        LiveShip live = AssignShip(pend.Request, pend.ShipId,
            autoSelection && ShipSpawner is not null ? null : requestedSideId);
        // P775 — native moment (1): announce the joiner to the whole mission
        // BEFORE it is seated (AddPlayerToMission puts the player on
        // SIDE_TEAMLOBBY then SendPlayerInfo's it to the mission,
        // fsmission.cpp:408-411), so every peer holds the joiner's PlayerInfo
        // before the JOIN_SIDE that follows (appmsg.cpp:2912-2914 needs it).
        // iSide = SIDE_TEAMLOBBY (-2): the client announces nothing yet
        // (appmsg.cpp:2286-2290 skips the lobby side) — the JOIN_SIDE is the
        // one announcement. A side SWITCH must never re-announce: this call
        // site only seats a NEW ship, and the latch makes the later catch-all
        // (CompleteJoinAndLaunch) a no-op for a seated joiner. It is a NEW
        // ship id, so its stale latch entries are dropped first
        // (AnnounceFreshShip).
        AnnounceFreshShip(live.Ship, -2 /* SIDE_TEAMLOBBY */, pend.Request.CharacterName);
        if (!autoSelection)
        {
            TryJoinSide(live.Ship, requestedSideId);
            Console.WriteLine($"[sideselect] {from} '{pend.Request.CharacterName}' chose side {requestedSideId} (ship {pend.ShipId})");
        }
        else
        {
            // P548 — route the AUTO-assign through the SAME join path as native.
            // Native always funnels every placement (auto-balanced or explicit)
            // through AddPlayerToSide (RequestPosition → AddPlayerToSide,
            // fsmission.cpp:4755-4761), which elects the first player onto an
            // empty side as team leader (fsmission.cpp:601-613) and the mission
            // owner when no side owns it (:584-592). The old code set SideId
            // directly in SpawnDefaultShip (Server.cs:753) and skipped
            // TryJoinSide, so an auto-assigned first player was never elected
            // team leader — only an explicitly-picking player (the branch
            // above) became leader.
            if (live.Ship.SideId is { } autoSide)
            {
                TryJoinSide(live.Ship, autoSide);
                Console.WriteLine($"[sideselect] {from} '{pend.Request.CharacterName}' AUTO-assigned to side {autoSide} (ship {pend.ShipId}); leader election via TryJoinSide");
            }
            else
            {
                Console.WriteLine($"[sideselect] {from} '{pend.Request.CharacterName}' AUTO-assigned to side (none) (ship {pend.ShipId})");
            }
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
        // P656c — native sends CURRENT developments and INITIAL bucket techs
        // separately (fsmission.cpp:4255-4258; appmsg.cpp:2385-2387).
        // P560 sent base techs in both fields to preserve bucket ids, hiding
        // testUnlock/research from late joiners' shop. Keep that stable bucket
        // seed (including native path permissions) in TtbmInitial; buyability
        // must use the actual development mask.
        var initial = side.InitialTechs;
        SendPeer(to, FedPacket.Pack(new[]
        {
            new CreateBuckets
            {
                TtbmDevelopments = side.DevelopmentTechs.ToBytes(),
                TtbmInitial = initial.ToBytes(),
            }.Encode(),
        }));
        Console.WriteLine($"[createbuckets] peer {to} -> CREATE_BUCKETS(191) ttbmDevelopments={side.DevelopmentTechs.PopCount} bits ttbmInitial={initial.PopCount} bits (side {side.Id})");
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
        // P703 read-only extension: clients execute the server's actual selected plan.
        // P802 — the payload/broadcast pair is shared with the lobby civ change
        // (Server.TeamCiv.cs SendResearchBriefing), so a re-selected plan reaches
        // the same client executor through the same message.
        if (_missions.FirstOrDefault(m => m.Sides.Contains(side)) is { } researchMission)
            SendResearchBriefing(to, researchMission, side);
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
    private void CompleteJoinAndLaunch(ConnectionId from, LogonRequest req, short shipId, byte cookie, LiveShip live, bool announceToMission = true, bool resumed = false)
    {
        // Native CFSMission::SendMissionInfo is scoped to the joining player's
        // mission (FedSrv/fsmission.cpp:4108-4341), including goals/briefing.
        var goalMission = live.Ship.Mission ?? _missions[0];
        // P813c — a RESUMED seat (reconnect grace) keeps its ship and its
        // wallet; native re-grants nothing to a player who never left the
        // mission. The per-seat economy grants below are therefore skipped.
        _peerShips[from] = live with { LastBroadcastPos = live.Ship.Position, LastBroadcastForward = live.Ship.Orientation.GetForward(), LastBroadcastUp = live.Ship.Orientation.GetUp(), ResumeToken = req.DwCookie };
        _peerNames[from] = req.CharacterName ?? "";   // P62: remember past logon for teammate EXPORT name
        // P775 — the catch-all of native moment (1): a joiner that is never
        // seated (SideId still null at launch) is announced here with its own
        // (non-)side, exactly native's AddPlayerToMission announce for a
        // player in the team lobby (fsmission.cpp:408-411; the catch-all's
        // iSide mirrors G's `SideId ?? -2`). For a seated joiner this sends
        // NOTHING — every (peer, joiner) pair was latched by the side-select
        // announce before TryJoinSide, and this plain form never clears
        // latches (only AnnounceFreshShip does, at the genuinely-new-ship
        // sites). Also covers rotation phase-3 bursts (the phase-2 announce
        // already latched every pair).
        // P807 — a rotation burst passes announceToMission:false. Every peer is
        // being re-synced in this same migration and the joinInfos below carry
        // one PLAYERINFO for EVERY ship to THIS recipient, so the mission-wide
        // re-announce per migrant would be a second O(peers²) fan-out of the
        // same data (and native re-announces nothing at a reset).
        if (announceToMission)
            AnnouncePlayerToMission(live.Ship, live.Ship.SideId ?? -2, req.CharacterName);
        // P612 — a newly seated peer must learn the current sim time scale so its
        // remote-ship dead-reckoning extrapolates in scaled sim time (no rubber-
        // banding under a slow-motion server).
        SendTimescaleTo(from);
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
            if (!resumed)
            {
                live.Wallet.Money = harness.Money;
                Console.WriteLine($"[testharness] {from} '{req.CharacterName}' GRANTED wallet ${harness.Money} at seat (prefix '{harness.CharacterNamePrefix}' matches)");
            }
            else
            {
                Console.WriteLine($"[testharness] {from} '{req.CharacterName}' resumed seat — wallet grant skipped (native re-grants nothing on a resume)");
            }
        }

        // P509 — the test-unlock wallet grant, applied ON SEAT like the harness
        // grant above. When liveSettings.testUnlock.enabled, EVERY seated
        // player's wallet is set (absolutely, refreshing the P443 starting
        // money) so purchases are affordable; purchases still pay the real
        // price through the real debit path. One line per granted connection.
        if (_config.LiveSettings?.TestUnlock is { Enabled: true } tu)
        {
            if (!resumed)
            {
                live.Wallet.Money = tu.Money;
                Console.WriteLine($"[testunlock] {from} '{req.CharacterName}' wallet SET to ${tu.Money} at seat (testUnlock enabled)");
            }
            else
            {
                Console.WriteLine($"[testunlock] {from} '{req.CharacterName}' resumed seat — wallet SET skipped (keeps the live wallet)");
            }
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
                ISideMissionOwner = -1,
                RgShipIdLeaders = [-1, -1, -1, -1, -1, -1],
                Stage = 0,
                // HUD-10 — native MissionParams::dtGameLength / IsCountdownGame
                // (igc.h:1526-1564). The field already exists on the wire; the
                // server now fills it from the configured game length so the
                // client's game-state clock can show countdown remaining.
                Goals = goalMission.Goals with { },
                // P856 — the two native create-game options ride the same
                // MissionParams bitfield block native's client reads
                // (bAllowAlliedViz bit 6, bAllowFriendlyFire bit 28;
                // igc.h:1127,1149), so a joining client sees the setting.
                AllowFriendlyFire = goalMission.AllowFriendlyFire,
                AllowAlliedViz = goalMission.AllowAlliedViz,
                // HUD-12 — MissionParams::iLives (igc.h:1185), sent so the lives
                // badge can compute iLives − MissionDeaths (console.cpp:812-817).
                ILives = _missions[0].Lives,
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
                ISideMissionOwner = -1,
                RgShipIdLeaders = [-1, -1, -1, -1, -1, -1],
                Goals = goalMission.Goals with { },
                // P856 — the two native create-game options ride the same
                // MissionParams bitfield block native's client reads
                // (bAllowAlliedViz bit 6, bAllowFriendlyFire bit 28;
                // igc.h:1127,1149), so a joining client sees the setting.
                AllowFriendlyFire = goalMission.AllowFriendlyFire,
                AllowAlliedViz = goalMission.AllowAlliedViz,
                // HUD-12 — MissionParams::iLives (igc.h:1185), sent so the lives
                // badge can compute iLives − MissionDeaths (console.cpp:812-817).
                ILives = _missions[0].Lives,
            }.Encode(),
        }));
        Console.WriteLine($"[sides] peer {from} '{req.CharacterName}' -> MISSIONDEF(38) nTeams {mdSides.Count} civIds [{string.Join(", ", mdSides.Select(s => s.Civilization.CivilizationId))}] (create SIDE_TEAMLOBBY + real sides via UpdateSides)");
        // P642: native seeds groups through MISSIONDEF.rgfAllies
        // (appmsg.cpp:2424; fsmission.cpp:5288-5291). The port uses its
        // existing CHANGE_ALLIANCES codec for the same initial state.
        SendPeer(from, FedPacket.Pack([CreateAllianceSnapshot(mdSides).Encode()]));


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
        //
        // P775 — G (the mission→joiner PLAYERINFOs, one per ship already in
        // the mission) rides the SAME packet and goes FIRST. Native sends them
        // in SendLobbyMissionInfo (fsmission.cpp:406, the loop at :3958-3968)
        // while the joiner's own side is still unset, BEFORE the joiner's own
        // PLAYERINFO (:408-411); so a native joiner announces none of the
        // humans already present (clintlib.cpp:4142-4160 needs the client's
        // own GetSide()/SideID()). Sending them after this own-side carrier
        // would make every existing human an "X has joined" announcement.
        var joinInfos = new List<byte[]>();
        if (live.Ship.Mission is { } infoMission)
        {
            // Native's ship loop is over pMission->GetShips() — EVERY ship,
            // drones included, in every sector, with no visibility test
            // (fsmission.cpp:3958-3968). Each (joiner, ship) pair goes through
            // the one-shot _playerInfoSent latch like every other native
            // moment, so a ship the joiner already learned is never sent twice.
            foreach (var (peer, player) in _peerShips)
                if (player.Ship.Mission == infoMission && player.Ship.Id != shipId)
                    AppendPlayerInfoForRecipient(from, player.Ship, player.Ship.SideId ?? -2,
                        _peerNames.TryGetValue(peer, out var name) ? name : "", joinInfos);
            // Every NPC of the mission, every sector (the IsShipIdLive
            // enumeration, Server.Docking.cs) — GetShips() has no human filter.
            foreach (var sector in infoMission.Sectors)
                foreach (var model in sector.Models)
                    if (model is Ship { IsNpc: true } npc)
                        AppendPlayerInfoForRecipient(from, npc, npc.SideId ?? -2, null, joinInfos);
        }
        // HUD-20 — the shared native-shape builder fills fExperience,
        // pilotType and dtidDrone too (FedSrv.CPP:2084,2088-2091).
        joinInfos.Add(BuildPlayerInfo(live.Ship, live.Ship.SideId ?? -1, req.CharacterName ?? "",
            live.PlayerScore.Kills, live.PlayerScore.Deaths,
            live.PlayerScore.Ejections, live.PlayerScore.Rank).Encode());
        SendPeer(from, FedPacket.Pack(joinInfos));
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
                string briefingCluster = "";
                if (friendlyStation is { } briefStation)
                {
                    var clusterName = goalMission.Sectors
                        .FirstOrDefault(s => s.Id == briefStation.ClusterId)?.Name;
                    if (clusterName is not null)
                    {
                        briefingCluster = clusterName;
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

                // P759 — send the client-side civ-briefing inputs (native
                // GetCivMissionBriefing reads these live: teamscreen.cpp:732-793).
                // Sent to the joining peer only, right after SET_BRIEFING_TEXT.
                SendMissionBriefingInfo(from, goalMission,
                    goalMission.Sides.FirstOrDefault(s => s.Id == setMoneySideId) ?? smSide, briefingCluster);
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
        // (fsmission.cpp:4170-4250): cluster → alephs (+ aleph bombs) →
        // asteroids (+ building effects) → stations → treasures → mines →
        // probes. This is what gives the native client's FindTarget
        // (src/Igc/common.cpp:470, station search at :500-501, adjacent-cluster
        // warp walk at :677-707) non-empty GetWarps()/GetStations() to search,
        // closing the P34 finding that FindTarget always returned NULL because
        // the client's clusters had empty lists.
        //
        // P797 — PER SIDE, exactly native: every entry in that list is exported
        // only when the joining peer's side has SEEN it.
        //   • alephs        fsmission.cpp:4175-4186  if (pwarplink->data()->SeenBySide(pside))
        //   • asteroids     fsmission.cpp:4201-4213  if (pmdllink->data()->SeenBySide(pside))
        //                   (+ GetBuildingEffect() → ExportObj(pbe, OT_buildingEffect))
        //   • stations      fsmission.cpp:4215-4222
        //   • treasures     fsmission.cpp:4224-4231
        //   • mines         fsmission.cpp:4233-4240
        //   • probes        fsmission.cpp:4242-4249
        // Clusters above stay whole-mission (fsmission.cpp:4170), and
        // CLUSTERINFO above gives the full sector graph without positions
        // (fsmission.cpp:4122-4166), which is exactly what native does — the map
        // preview is public knowledge, the objects in it are not.
        //
        // Where the seen-ness comes from: the same latched Per-side flags the
        // live P132 sweep maintains (Sector.ResolveCrossSideVisibility), seeded
        // first by Mission.SeedStartingVisibility so a peer joining before the
        // first live sector tick still gets its own stations plus everything
        // their scanners cover (native's own-side rule + first
        // UpdateSeenBySide pass, FedSrv.CPP:10907-10912, clusterIGC.cpp:347-357).
        // Ships' resolved destination sectors come for free: the warp's own
        // ClusterId addresses its sector, and all sectors are already exported
        // above. Each object also latches _crossSideDiscoverySent so the
        // mid-game discovery sweep (SweepCrossSideDiscoveryExports) never
        // re-sends what this burst already delivered.
        var joinMission = _missions[0];
        int sentWarps = 0, sentStations = 0, sentAsteroids = 0, sentEffects = 0, sentProbes = 0;
        if (live.Ship.SideId is { } joinSide)
        {
            joinMission.SeedStartingVisibility();
            foreach (var sector in joinMission.Sectors)
            {
                foreach (var warp in sector.Warps)
                {
                    if (!warp.SeenBySide(joinSide)) continue;
                    SendPeer(from, FedPacket.Pack(new[] { ExportWarp.FromWarp(warp).Encode() }));
                    _crossSideDiscoverySent.Add((from, (byte)IgcObjectType.Warp, warp.Warp.WarpId));
                    sentWarps++;
                }
                foreach (var asteroid in sector.Mineable)
                {
                    if (!asteroid.SeenBySide(joinSide)) continue;
                    SendPeer(from, FedPacket.Pack(new[] { ExportAsteroid.FromAsteroid(asteroid.Data).Encode() }));
                    _crossSideDiscoverySent.Add((from, (byte)IgcObjectType.Asteroid, asteroid.Data.AsteroidId));
                    sentAsteroids++;
                    // Native exports the rock's building effect in the same breath
                    // (fsmission.cpp:4209-4211) — the client draws the construction
                    // only from that export.
                    if (asteroid.BuildingEffect is { } effect)
                    {
                        SendPeer(from, FedPacket.Pack(new[] { BuildingEffectExport(effect).Encode() }));
                        _buildingEffectExportSent.Add((from, asteroid.Data.AsteroidId));
                        sentEffects++;
                    }
                }
                foreach (var station in sector.StationRuntimes)
                {
                    if (!station.SeenBySide(joinSide)) continue;
                    SendPeer(from, FedPacket.Pack(new[] { ExportStation.FromStation(station.Data).Encode() }));
                    _stationIdentitySent.Add((from, joinMission, station.StationId, station.Data.StationTypeId));
                    sentStations++;
                }
                foreach (var model in sector.Models)
                {
                    if (model is Probe probe)
                    {
                        if (!probe.SeenBySide(joinSide)) continue;
                        SendPeer(from, ProbeDiscoveryPacket(probe));
                        _crossSideDiscoverySent.Add((from, (byte)IgcObjectType.Probe, probe.Id));
                        sentProbes++;
                    }
                    else if (model is Mine mine)
                    {
                        if (mine.SideId is not { } mineSide || !mine.SeenBySide(joinSide)) continue;
                        short launcherId = (mine.Launcher as Ship)?.Id ?? -1;
                        var mineExport = ExportMine.FromMine(mine, (uint)TickIndex, launcherId, mineSide);
                        SendPeer(from, FedPacket.Pack(new[] { mineExport.Encode() }));
                        _crossSideMineExportSent.Add((from, mine.Id));
                    }
                }
            }
        }
        Console.WriteLine($"[export] peer {from} '{req.CharacterName}' side {live.Ship.SideId?.ToString() ?? "(none)"} -> " +
                          $"{sentWarps}/{joinMission.Warps.Count} warp(s), {sentStations}/{joinMission.Sectors.Sum(s => s.Stations.Count)} station(s), " +
                          $"{sentAsteroids}/{joinMission.Sectors.Sum(s => s.Asteroids.Count)} asteroid(s), {sentEffects} building effect(s), " +
                          $"{sentProbes} probe(s) (OT_warp 7 / OT_station 1 / OT_asteroid 5; seen/total); " +
                          "treasures follow in the post-SET_CLUSTER snapshot + the discovery sweep");

        var set = new SetCluster
        {
            SectorId = live.Sector.Id,
            Ship = ServerShipUpdate.FromModel(shipId, live.Ship, (uint)TickIndex),
            Cookie = NextCookie(EnsurePeerUpdate(from)),   // P507 — native's NewCookie(): the client echoes this in SHIP_UPDATE (fsship.cpp:767)
        };
        SendPeer(from, FedPacket.Pack(new[] { set.Encode() }));
        SendTreasureSnapshot(from, live.Sector);

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
        SendInitialDockState(from, live.Ship);

        // P713: the first player is elected before _peerShips registration,
        // so its own election broadcasts cannot reach it. Late joiners also
        // missed all earlier ready/leadership changes. Native sends the current
        // mission definition and player roster (fsmission.cpp:3943-3974), whose
        // rgShipIDLeaders/rgfReady/rgfForceReady arrays carry this state. Use the
        // port's existing state messages for the equivalent per-join snapshot.
        if (live.Ship.Mission is { } lobbyMission)
        {
            var lobbyState = new List<byte[]>();
            // Native SendLobbyMissionInfo sends every player's PLAYERINFO
            // before role-change handlers can dereference them (appmsg.cpp:
            // 2934-2944,2976-2986). P775 — those PLAYERINFOs (every ship of
            // the mission, fsmission.cpp:3958-3968) now go out ahead of the
            // joiner's own PLAYERINFO above, in the same packet, so this
            // snapshot carries only the role/ready state.
            foreach (var side in lobbyMission.Sides)
            {
                if (side.LeaderShipId is { } leader)
                    lobbyState.Add(new SetTeamLeader { SideId = side.Id, ShipId = leader }.Encode());
                // Fresh-session defaults are unready/unforced/active; only
                // non-default state needs an additional snapshot message.
                if (side.Ready)
                    lobbyState.Add(new TeamReady { SideId = side.Id, FReady = true }.Encode());
                if (side.ForceReady)
                    lobbyState.Add(new ForceTeamReady { SideId = side.Id, FForceReady = true }.Encode());
                if (!side.Active)
                    lobbyState.Add(new SideInactive { SideId = side.Id, BActive = false }.Encode());
                if (side.Id == lobbyMission.MissionOwnerSideId && side.LeaderShipId is { } owner)
                    lobbyState.Add(new SetMissionOwner { SideId = side.Id, ShipId = owner }.Encode());
            }
            foreach (var (peer, teammate) in _peerShips)
                if (teammate.Ship.Mission == lobbyMission && teammate.Ship.SideId == live.Ship.SideId)
                {
                    // fsmission.cpp:697-709: a joiner learns teammates' existing
                    // wings. Default command-wing zero is already implicit.
                    if (teammate.Ship.Id != shipId && teammate.Ship.WingId != 0)
                        lobbyState.Add(new SetWingId { ShipID = teammate.Ship.Id,
                            WingId = teammate.Ship.WingId, BCommanded = true }.Encode());
                    if (_playerReady.TryGetValue(peer, out var ready) && ready)
                        lobbyState.Add(new PlayerReady { ShipId = teammate.Ship.Id, FReady = true }.Encode());
                }
            if (lobbyState.Count > 0) SendPeer(from, FedPacket.Pack(lobbyState));
        }

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
        // Late joiners receive the remaining intermission/briefing deadline too.
        if (live.Ship.Mission is { } timerMission &&
            (_autoRestartDeadline.TryGetValue(timerMission, out var timerTarget)
             || _startCountdownDeadline.TryGetValue(timerMission, out timerTarget)))
            SendPeer(from, CountdownPacket(timerTarget));

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
        // HUD-17: the status carries the real own-side bits now —
        // Unknown=false (this port never hides a ship from its own side) and
        // Detected = ShipDetection.IsDetectedByEnemy(ship), i.e. true only
        // while a non-own, non-allied side has the ship in its scanner net
        // (FedSrv/fsship.cpp:237-335). A fresh join before any enemy sees the
        // ship therefore sends Detected=false; the shipDetection sweep re-sends
        // the status on the change the moment an enemy spots it. State is
        // c_ssFlying with StationID/ParentID NA for a launched ship
        // (docked spawns carry their station).
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

        // Native SendMissionInfo ends with all victory rankings, including
        // before the first capture/kill (FedSrv/fsmission.cpp:4324-4339).
        SendPeer(from, FedPacket.Pack(new[] { GameState.ForMission(goalMission).Encode() }));

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
        // P820c — seed the joining peer with each side's CURRENT squad via native
        // SET_TEAM_INFO(15) (the squad relay native sends on every squad/name
        // change, fsmission.cpp:1580-1590), so a late-arriving peer's team pane
        // shows squads established before it connected. SIDE_ROSTER stays the
        // master side-name source (P820c revert). Squad games only: a non-squad
        // game never sends SET_TEAM_INFO (fsmission.cpp:661-690), so its join
        // traffic stays exactly the pre-P820b sequence.
        if (mission.SquadGame)
            foreach (var s in mission.Sides)
                RelaySideSquad(mission, s, to);
    }

/// <summary>P281 — refresh every still-pending side-select peer's roster
    /// whenever any peer's seat changes, so the on-screen counts stay current.</summary>
    private void BroadcastSideRosterToPendingPeers(Mission mission)
    {
        foreach (var key in _pendingSideSelect.Keys.ToList())
            SendSideRoster(key);
    }

    /// <summary>
    /// P759 — send one <see cref="MissionBriefingInfo"/> to a joining peer: the
    /// side/civ names, the briefing text and the authoritative win-condition
    /// flags native's client-side <c>GetCivMissionBriefing</c> reads from
    /// <c>MissionParams</c>/side/civ (<c>teamscreen.cpp:732-793</c>). NoWin is
    /// native's <c>IsNoWinningConditionsGame()</c> — none of the port's win
    /// flags set.
    /// </summary>
    private void SendMissionBriefingInfo(ConnectionId to, Mission mission, Side side, string briefingText)
    {
        bool noWin = mission.Goals.IsNoWinningConditionsGame;
        var info = new MissionBriefingInfo
        {
            SideName = side.Name ?? $"side{side.Id}",
            CivName = side.Civilization.Name,
            // Native's GetCivString prefixes each key with the civ's icon name
            // (teamscreen.cpp:636-639); the client reads the strings themselves
            // from missionbrief.mdl in the artwork corpus.
            CivIconName = side.Civilization.IconName,
            // Native %s is GetBriefingText() = the SET_BRIEFING_TEXT text.
            BriefingText = briefingText,
            CivList = mission.Sides.Select(s => s.Civilization.Name).ToList(),
            NoWin = noWin,
            Conquest = mission.ConquestEnabled,
            ConquestPercent = mission.Goals.ConquestPercentage,
            Artifacts = mission.ArtifactEnabled,
            ArtifactGoal = mission.ArtifactWinGoal,
            Flags = mission.FlagsEnabled,
            FlagsGoal = mission.FlagsWinGoal,
            Deathmatch = mission.KillsEnabled,
            KillLimit = mission.KillsWinGoal,
            // Native IsCountdownGame() ⇔ dtGameLength != 0 (igc.h:1526,1553-1556);
            // the client divides by 60 for the minutes shown (teamscreen.cpp:773-777).
            CountdownSeconds = mission.CountdownDuration ?? 0,
            Prosperity = mission.ProsperityEnabled,
            // Native WinTrek/teamscreen.cpp:790-793; same price as the bucket.
            ProsperityMoney = (int)(mission.Goals.TeamMoney * mission.Core.Constants.FloatConstants[12]),
        };
        SendPeer(to, FedPacket.Pack(new[] { info.Encode() }));
        Console.WriteLine($"[briefing] peer {to} -> MISSION_BRIEFING_INFO(996) side='{info.SideName}' civ='{info.CivName}' noWin={info.NoWin}");
    }

/// <summary>P281/P763 — the per-tick driver that seats pending side-select
    /// peers. Native has no timer: the team screen sends <c>C POSITIONREQ</c>
    /// (<c>WinTrek/teamscreen.cpp:2964-2981</c>) and the server seats only that
    /// recorded request (<c>FedSrv.CPP:6658-6691</c> →
    /// <c>fsmission.cpp:4729-4774</c>). The pre-P763 port auto-assigned a silent
    /// peer after 30 s; that is now the opt-in
    /// <see cref="ServerConfig.SideSelectAutoSeatSeconds"/> adaptation
    /// (<c>null</c>/&lt;=0 = off, the default). With it off, a peer that never
    /// chose stays pending — exactly like native's unanswered request — and
    /// still never blocks the P180 ready-up auto-start because pending peers are
    /// side-less and absent from _peerShips. When the option is set, a silent/AFK
    /// client is eventually seated and launched instead of hanging at the
    /// side-select screen. 30 s (the pre-P763 value) is comfortably longer than
    /// the 15 s P188 briefing countdown and ample for a 2-option pick, yet short
    /// enough that an abandoned join recycles quickly.</summary>
    private void AdvanceSideSelection(double now)
    {
        if (_pendingSideSelect.Count == 0) return;
        // P763 — a finite positive value opts into the timed auto-seat; null,
        // zero or negative keeps native's wait-for-POSITIONREQ behaviour.
        double autoSeatSeconds = _config.SideSelectAutoSeatSeconds ?? 0;
        bool autoSeat = autoSeatSeconds > 0 && double.IsFinite(autoSeatSeconds);
        // Seat every pending peer that is ready (has a recorded POSITIONREQ
        // choice) OR timed out, in ascending JoinSeq (logon) order. Draining in
        // logon order — not POSITIONREQ arrival order — keeps side assignment
        // deterministic (round-robin sides and injected ShipSpawners both key
        // off spawn order), exactly as when peers seated synchronously at logon
        // pre-P281; racing client receive-threads otherwise shuffled sides.
        var ready = _pendingSideSelect
            .Where(kv => kv.Value.RequestedSide is not null
                      || (autoSeat && now - kv.Value.ConnectSimTime >= autoSeatSeconds))
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
                    ? $"[sideselect] peer {key} '{pend.Request.CharacterName}' never chose a side within {autoSeatSeconds}s — auto-assigning"
                    : $"[sideselect] draining peer {key} '{pend.Request.CharacterName}' -> auto-pick (NA)");
                CompleteSideSelection(key, -1);   // NA = auto-pick (arrival order / spawner default)
            }
        }
    }
    // Initial alliance state for joins; native MISSIONDEF.rgfAllies, appmsg.cpp:2424.
    internal static ChangeAlliances CreateAllianceSnapshot(IReadOnlyList<Side> sides)
    {
        var message = new ChangeAlliances { Allies = Enumerable.Repeat((sbyte)-1, ChangeAlliances.SideAlliesMax).ToArray() };
        foreach (var side in sides)
            if (side.Id >= 0 && side.Id < message.Allies.Length) message.Allies[side.Id] = side.AllyGroup ?? -1;
        return message;
    }

}
