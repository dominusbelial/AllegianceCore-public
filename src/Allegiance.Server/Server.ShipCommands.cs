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
/// Inbound ship-command handlers of <see cref="Server"/>
/// (P330 stage-4 split from <c>Server.cs</c>). Partial class — shares the <c>Server</c>
/// type, so private field/method access across the split files is unaffected.
/// </summary>
public sealed partial class Server
{

    /// <summary>
    /// Handle a client <c>FM_C_VIEW_CLUSTER(104)</c> for a <b>docked</b> peer —
    /// the P106 port of the native <c>FedSrv.CPP:6420-6454</c>
    /// <c>case FM_C_VIEW_CLUSTER</c>:
    /// <list type="bullet">
    /// <item><b>Gate on docked.</b> The dispatch branch already enforced
    /// <see cref="Ship.StationId"/> != null — the port's direct equivalent of the
    /// native <c>GetStation() != NULL</c> (<c>FedSrv.CPP:6421</c>). A flying ship
    /// never reaches this method (its request is a no-op, no reply).</item>
    /// <item><b>NA cluster = silent.</b> <c>clusterID != NA</c> gates the whole
    /// reply in native (<c>FedSrv.CPP:6423</c>); a request naming NA(-1) cluster
    /// gets no reply at all (native still calls <c>SetCluster(NULL)</c> after,
    /// but that persistence is deliberately not ported — see finding below).</item>
    /// <item><b>Target resolution.</b> When <c>otTarget != NA</c> the native
    /// resolves <c>pMission-&gt;GetModel(otTarget, oidTarget)</c>
    /// (<c>FedSrv.CPP:6428</c>) — this port's model list is the peer ships
    /// (plus sector-attached stations), resolved in
    /// <see cref="TryResolveViewTarget"/>.</item>
    /// <item><b>Reply shape.</b> Echo <c>clusterID</c> and, only when the target
    /// resolved to a real object in that cluster (<c>ptarget-&gt;GetCluster() ==
    /// pcluster</c>, <c>FedSrv.CPP:6430</c>) <b>and</b> visible to the requester's
    /// side (<c>ptarget-&gt;SeenBySide(pfsPlayer-&gt;GetSide())</c>,
    /// <c>FedSrv.CPP:6431</c>), the object's real position with
    /// <c>bUsePosition=true</c>; else bare cluster with <c>bUsePosition=false</c>.
    /// The visibility predicate reused here is this port's existing same-side
    /// gate (the P58 teammate broadcast's <c>tv.Value.Ship.SideId != recipientSide
    /// → skip</c>, <c>Server.cs:521</c>), applied to the target: an object with no
    /// side (a neutral world object) is visible; a side-carrying object is
    /// visible only to the same side — this port has no fog-of-war/scanner, so
    /// same-side is the faithful stand-in for native <c>SeenBySide</c>.</item>
    /// <item><b>Reply to requester only.</b> <c>SendPeer(from, …)</c> — the
    /// direct equivalent of <c>g.fm.SendMessages(pfsPlayer-&gt;GetConnection(), …)</c>
    /// (<c>FedSrv.CPP:6433-6434</c>); never a sector/mission broadcast.</item>
    /// </list>
    /// <para><b>Finding on the per-player "currently viewed cluster" state:</b>
    /// the native's trailing <c>pfsPlayer-&gt;SetCluster(pcluster, true)</c>
    /// (<c>FedSrv.CPP:6453</c>) persists a per-player viewed-cluster. Searching
    /// <c>FedSrv.CPP</c>'s native call sites, <b>no other handler reads that
    /// state back</b> — it is written only here and consumed nowhere else in
    /// server code (its consumer would be the client, if anything). So this
    /// port implements <c>VIEW_CLUSTER</c> as a pure request/reply with no new
    /// server-side per-player state, and says so; nothing in the wire protocol
    /// depends on the server remembering what the client is currently viewing.
    /// Confirmed for this task (details and rationale in VIEW_CLUSTER_P106.md).</para>
    /// Runs on the sim thread (the dispatch is drained inside <see cref="Tick"/>,
    /// like every other inbound handler), so it may read <c>_peerShips</c> and
    /// touch <c>SendPeer</c> without extra locking.
    /// </summary>
    private void HandleViewCluster(ConnectionId from, LiveShip live, ViewClusterRequest req)
    {
        var mission = live.Ship.Mission;
        if (mission is null) return;

        // clusterID == NA → native resolves pcluster = NULL and sends NO reply
        // (the whole reply block sits inside the `!= NA` guard). Mirror exactly.
        if (req.ClusterId == -1)
        {
            Console.WriteLine($"[viewcluster] {from} ship {live.Ship.Id}: clusterID=NA — no reply (native FedSrv.CPP:6423)");
            return;
        }

        var requesterSide = live.Ship.SideId;
        var pcluster = mission.Sectors.FirstOrDefault(s => s.Id == req.ClusterId);

        // Target resolution + the native's own two-gate check:
        //   ptarget && ptarget->GetCluster() == pcluster && ptarget->SeenBySide(requesterSide)
        //   (FedSrv.CPP:6429-6432). Resolution is the port's GetModel;
        //   GetCluster()==pcluster maps to "the object's owning sector";
        //   SeenBySide maps to the same-side predicate (finding above).
        bool bUsePosition = false;
        Vector3 targetPos = Vector3.Zero;
        if (req.OtTarget != -1 && pcluster is not null
            && TryResolveViewTarget(mission, (IgcObjectType)req.OtTarget, req.OidTarget, out targetPos, out var objectSector, out var objectSide)
            && objectSector == pcluster
            && SeenByRequesterSide(objectSide, requesterSide))
        {
            bUsePosition = true;
        }

        var reply = new ViewClusterReply
        {
            Position = bUsePosition ? targetPos : Vector3.Zero,
            ClusterId = req.ClusterId,
            BUsePosition = bUsePosition,
        };
        SendPeer(from, FedPacket.Pack(new[] { reply.Encode() }));
        Console.WriteLine($"[viewcluster] {from} ship {live.Ship.Id} docked -> cluster {req.ClusterId}" +
            $"{(req.OtTarget != -1 ? $" otarget={(IgcObjectType)req.OtTarget}:{req.OidTarget}" : "")}" +
            $" bUsePosition={bUsePosition} (target ok={bUsePosition})");
    }

    /// <summary>
    /// P108 — the <c>FM_C_BOARD_SHIP(103)</c> handler, the port of
    /// <c>FedSrv.CPP:6385-6418</c> <c>case FM_C_BOARD_SHIP</c>. Dual-purpose
    /// like the native: <paramref name="req"/><c>.SidParent == NA</c> leaves
    /// the player's current parent ship (via <see cref="PerformLeave"/>), any
    /// other value boards that ship (via <see cref="PerformBoard"/>).
    /// <list type="bullet">
    /// <item><b>Gate: mission started.</b> The native refuses unless
    /// <c>pfsMission-&gt;GetStage() == STAGE_STARTED</c>
    /// (<c>FedSrv.CPP:6388</c>). We keep it via <see cref="Ship.Mission"/> /
    /// <see cref="MissionStage.Started"/>. The native's companion
    /// <c>IsGhost()</c> gate is skipped — this port has no ghost concept.</item>
    /// <item><b>Success ⇒ no dedicated ack.</b> Confirmed by reading the whole
    /// native handler: on success neither <c>BoardShip()</c> nor the internal
    /// <c>LeaveShip()</c> sends a dedicated acknowledgement — they only flush
    /// the <c>SHIP_STATUS</c> updates queued by their <c>ShipStatus-&gt;SetState</c>
    /// calls (<c>FedSrv.CPP:2450-2457</c>). This port's equivalent mechanism is
    /// an immediate same-side status refresh using <see cref="MakeOwnShipStatus"/>,
    /// so all clients know the parent before a subsequent PROMOTE arrives.
    /// <b>No new success message is introduced</b>.</item>
    /// <item><b>Failure ⇒ <c>BOARD_NACK(178)</c> to the requester alone.</b>
    /// <c>g.fm.SendMessages(pfsPlayer-&gt;GetConnection(), ...)</c>
    /// (<c>FedSrv.CPP:6412-6416</c>). Covers: not a started mission; the leave
    /// case with no current parent; <c>LeaveShip</c> refusing (carrier not
    /// docked / not actually boarded); the board case with a missing or
    /// invalid target.</item>
    /// </list>
    /// Runs on the sim thread (the dispatch drains inside
    /// <see cref="Tick"/>), so it may mutate <see cref="_peerShips"/>/sides
    /// without extra locking, like every other inbound handler.
    /// </summary>
    private void HandleBoardShip(ConnectionId from, LiveShip live, BoardShip req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[boardship] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — BOARD_NACK (native gate, FedSrv.CPP:6388)");
            SendBoardNack(from, req.SidParent);
            return;
        }

        bool success;
        if (req.SidParent == Na)
        {
            // Leave whatever this ship is currently boarded on (FedSrv.CPP:6393-6401).
            var carrier = ship.ParentShip;
            success = carrier is not null && PerformLeave(ship, carrier);
        }
        else
        {
            // Board the named ship (FedSrv.CPP:6404-6409): resolved on the
            // boarder's OWN side (native GetShip on GetSide()).
            var target = FindSameSideShip(req.SidParent, ship.SideId, mission);
            success = target is not null && PerformBoard(ship, target);
        }

        if (!success)
        {
            SendBoardNack(from, req.SidParent);
            ConsoleLog.Write($"[boardship] {from} ship {ship.Id} -> sidParent {req.SidParent} refused — BOARD_NACK(178) to requester only");
        }
        else
        {
            // Native BoardShip flushes the queued SHIP_STATUS changes
            // (FedSrv.CPP:2450-2457). A periodic teammate refresh can arrive
            // after PROMOTE, whose wire payload names only the child.
            var packet = FedPacket.Pack([MakeOwnShipStatus(ship.Id, live).Encode()]);
            foreach (var peer in _peerShips)
                if (peer.Value.Ship.Mission == mission && peer.Value.Ship.SideId == ship.SideId)
                    SendPeer(peer.Key, packet);
        }
    }

    /// <summary>
    /// P109 — the real <c>CshipIGC::ProcessShipUpdate(const ClientActiveTurretUpdate&amp;)</c>
    /// port (<c>shipIGC.cpp:3349-3392</c>), applied to the <b>gunner's</b> own
    /// ship. Called from the <c>FM_C_ACTIVE_TURRET_UPDATE(99)</c> dispatch,
    /// gated here on the sender being a genuine boarded gunner (the same
    /// cluster/parent/turret gate native <c>FedSrv.CPP:4873-4875</c> applies
    /// before routing to <c>SetActiveTurretUpdate</c>). Sequence:
    /// <list type="bullet">
    /// <item><b>Staleness bound</b> — <c>deltaT = GetMyLastUpdate() − atu.time</c>;
    /// outside <c>±1.5 s</c> the whole message is dropped (<c>rc = c_susOutOfDate</c>,
    /// <c>shipIGC.cpp:3353-3354</c>).</item>
    /// <item><b>\"We're shooting\"</b> — <c>SetStateM(weaponsMaskIGC)</c>
    /// (<c>shipIGC.cpp:3359</c>); this makes the turret weapon's
    /// <c>IsWeaponSelected</c> true (gunner present + <c>weaponsMaskIGC</c>).</item>
    /// <item><b>Controls</b> — <c>shipupdate.controls.Export(&amp;m_controls)</c>
    /// (<c>shipIGC.cpp:3361</c>).</item>
    /// <item><b>Snapshot catch-up</b> — decode the reported orientation, then
    /// integrate it forward to the present in ≤ <c>c_fTimeIncrement</c> (0.25 s)
    /// steps via <see cref="Ship.ExecuteTurretMove"/> (the first of native's two
    /// real call sites, <c>shipIGC.cpp:3371-3384</c>), then
    /// <c>SetOrientation(orientation)</c>.</item>
    /// </list>
    /// Uses native's per-player monotonic gate (FedSrv/fsship.cpp:1208-1218)
    /// with wire milliseconds converted to simulation seconds.
    /// </summary>
    private bool ShouldLogTurret(ConnectionId from)
    {
        if (!ConsoleLog.Verbose) return false;
        var ticket = _turretLog.Ticket(from.Value);
        if (ticket.Summarize)
            ConsoleLog.Write($"[turret] {from}: {ticket.Suppressed} turret diagnostics suppressed");
        return ticket.Emit;
    }

    private void HandleActiveTurretUpdate(ConnectionId from, Ship gunner, ClientActiveTurretUpdate atu)
    {
        // FedSrv.CPP:4873-4875 — the gate that precedes SetActiveTurretUpdate.
        if (gunner.Mission?.Stage != MissionStage.Started || gunner.StationId is not null || gunner.Sector is null || gunner.ParentShip is not { StationId: null } || gunner.TurretId is null)
        {
            if (ShouldLogTurret(from)) ConsoleLog.Write($"[turret] ship {gunner.Id}: ACTIVE_TURRET_UPDATE from a non-gunner — ignored (FedSrv.CPP:4873-4875)");
            return;
        }

        // ProcessShipUpdate(const ClientActiveTurretUpdate&), shipIGC.cpp:3349-3392.
        // Native monotonic gate: FedSrv/fsship.cpp:1208-1218. Shared with
        // pilot updates so boarding/promoting cannot replay old input.
        var pu = EnsurePeerUpdate(from);
        if (pu.HasClientTime && atu.Time <= pu.LastClientTime) return;
        double updateTime = atu.Time / 1000.0;
        double deltaT = gunner.LastUpdate - updateTime; // Igc/shipIGC.cpp:3353
        if ((deltaT < 1.5) && (deltaT > -1.5))                 // shipIGC.cpp:3354
        {
            // "If we got this ... we're shooting." (shipIGC.cpp:3358-3359)
            pu.HasClientTime = true;
            pu.LastClientTime = atu.Time;
            gunner.StateM = ShipControlState.WeaponsMask;      // SetStateM(weaponsMaskIGC)
            gunner.Controls = atu.Controls;                    // Export(&m_controls), shipIGC.cpp:3361
            Orientation orientation = atu.Orientation;         // Export(&orientation), shipIGC.cpp:3363-3364

            // Snapshot catch-up, never advancing more than c_fTimeIncrement
            // (0.25 s) per step (shipIGC.cpp:3366-3384).
            int n = 1 + (int)(MathF.Abs((float)deltaT) / 0.25f);
            double thisTime = updateTime;
            for (int i = 1; i <= n; i++)
            {
                double nextTime = updateTime + (deltaT * ((float)i / (float)n));
                gunner.ExecuteTurretMove(thisTime, nextTime, orientation);
                thisTime = nextTime;
            }
            gunner.SetOrientation(orientation);                // SetOrientation(orientation), shipIGC.cpp:3386

            if (ShouldLogTurret(from)) ConsoleLog.Write($"[turret] ship {gunner.Id} ACTIVE_TURRET_UPDATE time={atu.Time} deltaT={deltaT:F3} -> " +
                              $"StateM=weaponsMaskIGC controls={atu.Controls} orientForward={orientation.GetForward()}");
        }
        else
        {
            if (ShouldLogTurret(from)) ConsoleLog.Write($"[turret] ship {gunner.Id} ACTIVE_TURRET_UPDATE stale (deltaT={deltaT:F3} outside ±1.5) — dropped (shipIGC.cpp:3354)");
        }
    }

    /// <summary>
    /// P140 — the <c>FM_C_PROMOTE(190)</c> handler, the port of
    /// <c>FedSrv.CPP:6353-6382</c> <c>case FM_C_PROMOTE</c>. A commanding ship
    /// promotes one of its own boarded turret gunners into the command seat —
    /// the real <see cref="Ship.Promote"/> role-swap (hull, loadout,
    /// ammo/energy, position/orientation, hull/shield fractions, and every
    /// sibling gunner transfer to the promoted ship; the old parent is demoted
    /// into the vacated turret mount — nothing is destroyed).
    /// <list type="bullet">
    /// <item><b>Gate: mission started.</b> The native refuses unless
    /// <c>pfsMission-&gt;GetStage() == STAGE_STARTED</c>
    /// (<c>FedSrv.CPP:6357</c>). Kept via <see cref="Ship.Mission"/> /
    /// <see cref="MissionStage.Started"/>. The native's companion
    /// <c>IsGhost()</c> gate is skipped — this port has no ghost concept (the
    /// same omission P108 documents for <c>BOARD_SHIP</c>).</item>
    /// <item><b>Eligibility: the requesting ship's OWN boarded children only.</b>
    /// The native walks <c>GetIGCShip()-&gt;GetChildShips()</c>
    /// (<c>FedSrv.CPP:6359</c>) and matches <c>pship-&gt;GetTurretID() ==
    /// mountidPromoted</c> (<c>FedSrv.CPP:6362</c>). A mount matching no real
    /// boarded child falls through the loop and is a <b>silent no-op</b> — no
    /// crash, no ack, no broadcast.</item>
    /// <item><b>No <c>ShipStatusRecalculate</c> call needed.</b> The native calls
    /// <c>((CFSShip*)pship-&gt;GetPrivateData())-&gt;ShipStatusRecalculate()</c>
    /// (<c>FedSrv.CPP:6364</c>) after the hull-changing promote. This port's
    /// status is not cached — <see cref="MakeOwnShipStatus"/> derives it fresh
    /// from <c>HullType.HullId</c>/<c>ParentShip</c>/<c>StationId</c> every
    /// per-tick refresh (<see cref="BroadcastShipStates"/>), the same mechanism
    /// P108/P110 established for post-hull-change status, so the promoted
    /// state is reflected automatically with no explicit recalc.</item>
    /// <item><b>Send target: sector-wide.</b> The native routes
    /// <c>GetGroupSectorFlying(pcluster)</c> (whole sector) when the requesting
    /// ship is in a cluster, else the requesting side's group
    /// (<c>FedSrv.CPP:6374-6377</c>). During active gameplay this port's ships
    /// are always placed in a <see cref="Ship.Sector"/> (there is no reachable
    /// in-game no-cluster state), so the side-group fallback is unreachable —
    /// routed through <see cref="SendSectorWide"/> (this port's
    /// <c>GetGroupSectorFlying</c> analogue).</item>
    /// </list>
    /// Runs on the sim thread (the dispatch drains inside <see cref="Tick"/>),
    /// so it may mutate <see cref="_peerShips"/> ship graph state without extra
    /// locking, like every other inbound handler.
    /// </summary>
    private void HandlePromote(ConnectionId from, LiveShip live, PromoteRequest req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            ConsoleLog.Write($"[promote] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6357)");
            return;
        }

        // Walk the requesting ship's OWN boarded children for the one at the
        // requested turret mount (FedSrv.CPP:6359-6370).
        Ship? promoted = null;
        foreach (var child in ship.ChildShips)
        {
            if (child.TurretId == req.MountidPromoted)
            {
                _loadoutCommandDepth++;
                try { child.Promote(); }
                finally { _loadoutCommandDepth--; }
                promoted = child;
                break;
            }
        }

        if (promoted is null)
        {
            Console.WriteLine($"[promote] {from} ship {ship.Id}: no boarded child at mount {req.MountidPromoted} — silent no-op (FedSrv.CPP:6362)");
            return;
        }

        // Broadcast PROMOTE(189) to the sector-wide audience
        // (GetGroupSectorFlying(pcluster), FedSrv.CPP:6374-6376). The no-cluster
        // side-group fallback (FedSrv.CPP:6377) is unreachable here — an in-game
        // ship always has a sector — so SendSectorWide is the faithful route.
        var msg = new Promote { ShipidPromoted = promoted.Id };
        SendSectorWide(ship.Sector!, msg.Encode());
        Console.WriteLine($"[promote] {from} ship {ship.Id}: promoted gunner {promoted.Id} at mount {req.MountidPromoted} -> PROMOTE(189) shipidPromoted={promoted.Id} sector {ship.Sector!.Id} wide; old parent {ship.Id} demoted to its own seat");
    }

    /// <summary>
    /// P143 — the <c>FM_CS_SWAP_PART(34)</c> handler, the port of
    /// <c>FedSrv.CPP:6459-6501</c> <c>case FM_CS_SWAP_PART</c>. A flying,
    /// unboarded ship asks to move the mounted part at <c>(etOld, mountOld)</c>
    /// to <c>mountNew</c>, swapping it with a <b>different</b> part of the
    /// <b>same</b> <c>etOld</c> type already occupying <c>mountNew</c> if one is
    /// present (otherwise a plain move). The whole original message is then
    /// re-broadcast <b>sector-wide</b> with <c>shipID</c> filled in — everyone
    /// flying in the same cluster (<c>GetGroupSectorFlying(pcluster)</c>,
    /// <c>FedSrv.CPP:6498</c>), a broadcast-to-everyone-nearby message like
    /// PROMOTE, <b>not</b> a same-side-only relay like HIGHLIGHT_CLUSTER.
    /// No <c>LOADOUT_CHANGE</c> is queued from this handler — the broadcast
    /// <b>is</b> the SWAP_PART message itself (confirmed by reading the handler
    /// in full: the only forward is <c>pfmSwapPart</c>, <c>FedSrv.CPP:6497-6498</c>).
    /// <list type="bullet">
    /// <item><b>Gate: mission started + unboarded.</b> The native refuses unless
    /// <c>pfsMission-&gt;GetStage() == STAGE_STARTED</c> and
    /// <c>GetParentShip() == NULL</c> (<c>FedSrv.CPP:6461,6464</c>). The stage
    /// gate is kept verbatim like <see cref="HandlePromote"/>; the unboarded
    /// gate is <see cref="Ship.ParentShip"/> null — a boarded gunner/observer
    /// cannot swap its own parts, and unlike PROMOTE (a gunner can't promote
    /// itself) SWAP_PART needs the explicit check the native has.</item>
    /// <item><b>No-match is a silent no-op.</b> If no part is at
    /// <c>(etOld, mountOld)</c>, native does nothing — no error, no broadcast,
    /// no ack (<c>FedSrv.CPP:6485-6486</c>). Log-and-return, matching
    /// <see cref="HandlePromote"/>'s own <c>promoted is null</c> silent branch.</item>
    /// <item><b>Send target: sector-wide.</b> Native routes
    /// <c>GetGroupSectorFlying(pcluster)</c> (whole sector, all sides), via
    /// <see cref="SendSectorWide"/> (this port's analogue) — the same route
    /// PROMOTE uses; only reached when the requesting ship has a sector.</item>
    /// </list>
    /// Runs on the sim thread (dispatch drains inside <see cref="Tick"/>), so
    /// it may mutate the requesting ship's <c>_parts</c> through
    /// <see cref="Ship.TrySwapPart"/> without extra locking, like every other
    /// inbound handler.
    /// </summary>
    private void HandleSwapPart(ConnectionId from, LiveShip live, SwapPart req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            ConsoleLog.Write($"[swappart] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6461)");
            return;
        }

        // Native GetCluster gate: FedSrv.CPP:6302,6466,6515-6516.
        if (ship.StationId is not null || ship.Sector is null || ship.Dead || ship.SideId is not >= 0) return;

        // Native TrapHackBoot(pfsPlayer->GetIGCShip()->GetParentShip() == NULL)
        // (FedSrv.CPP:6464): a boarded gunner/observer cannot swap parts.
        if (ship.ParentShip is not null)
        {
            ConsoleLog.Write($"[swappart] {from} ship {ship.Id}: boarded gunner (parent {ship.ParentShip.Id}) — SWAP_PART ignored (FedSrv.CPP:6464)");
            return;
        }

        // The swap-or-move; false means the source mount held no part, which
        // native treats as a silent no-op (FedSrv.CPP:6485-6486). The dispatch
        // is cargo-aware (P192): a negative mountOld/mountNew addresses the
        // ship's cargo hold rather than the equipped-mount list
        // (Ship.TrySwapPartWithCargo), native's mechanism for moving a part
        // into/out of cargo.
        bool swapped;
        _loadoutCommandDepth++;
        try { swapped = ship.TrySwapPartWithCargo(req.EtOld, req.MountOld, req.MountNew); }
        finally { _loadoutCommandDepth--; }
        if (!swapped)
        {
            Console.WriteLine($"[swappart] {from} ship {ship.Id}: no mounted {req.EtOld} part at mount {req.MountOld} — silent no-op (native, FedSrv.CPP:6485)");
            return;
        }

        // Fill in shipID and broadcast SWAP_PART(34) sector-wide
        // (pfmSwapPart->shipID = pfsPlayer->GetShipID(); then
        // ForwardMessage(GetGroupSectorFlying(pcluster), pfmSwapPart),
        // FedSrv.CPP:6497-6498). An in-game ship always has a sector, so
        // SendSectorWide is the faithful route (cf. HandlePromote's reasoning).
        var msg = new SwapPart
        {
            ShipID = ship.Id,
            EtOld = req.EtOld,
            MountOld = req.MountOld,
            MountNew = req.MountNew,
        };
        SendSectorWide(ship.Sector!, msg.Encode());
        Console.WriteLine($"[swappart] {from} ship {ship.Id}: {req.EtOld} mount {req.MountOld} -> {req.MountNew} -> SWAP_PART(34) shipID={ship.Id} sector {ship.Sector!.Id} wide");
    }

    /// <summary>
    /// P192 — the <c>FM_CS_RELOAD(129)</c> handler, the port of
    /// <c>FedSrv.CPP:6504-6653</c> <c>case FM_CS_RELOAD</c>. A flying, unboarded
    /// ship reloads its cargo hold into its live pools / equipped launchers,
    /// up to 5 <c>{amountTransfered, mount}</c> entries (fuel/ammo/missile/
    /// chaff/dispenser, <c>FedSrv.CPP:6523</c>). Each entry is applied in wire
    /// order by <see cref="Ship.TryApplyReloadEntry"/> (ammo-pack → live
    /// <c>Ship.Ammo</c> + ammo-weapon mounted-fraction reset; fuel-pack → stated
    /// no-op + afterburner fraction reset; launcher → swap <c>Amount</c> into
    /// the equipped same-type launcher). On full success the whole message is
    /// re-broadcast <b>sector-wide</b> with <c>shipID</c> filled in
    /// (<c>FedSrv.CPP:6651-6652</c>).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Gates: mission started + unboarded + real sector + non-empty
    /// loadout.</b> Native requires <c>Stage == STAGE_STARTED</c> and
    /// <c>GetSide()-&gt;GetObjectID() &gt;= 0</c> (<c>FedSrv.CPP:6506</c>),
    /// <c>GetParentShip() == NULL</c> (<c>FedSrv.CPP:6509</c>), and — the NYI
    /// lifepod hack — <c>GetParts()-&gt;n() != 0</c> with a real cluster
    /// (<c>FedSrv.CPP:6516</c>). The stage + unboarded gates mirror
    /// <see cref="HandleSwapPart"/>'s.</item>
    /// <item><b>Malformed-entry response: softened to log-and-stop, no
    /// disconnect.</b> Native <c>bBoot = true; break;</c> then hard-disconnects
    /// the connection via <c>TrapHackBoot(!bBoot)</c> (<c>FedSrv.CPP:6531-6648</c>).
    /// This port's established TrapHackBoot convention (P182/P184/P186, cf.
    /// HandleTreasureAck/FireExpendable) is a graceful log-and-refuse — so on a
    /// malformed entry the remaining entries in the batch are refused and no
    /// broadcast is sent, but the connection stays up. The <b>no-rollback</b> of
    /// earlier-in-batch transfers is replicated faithfully: entries already
    /// applied before the malformed one keep their effect (a real native quirk,
    /// <c>FedSrv.CPP:6542/6563/6628</c>).</item>
    /// <item><b>Send target: sector-wide</b> — <see cref="SendSectorWide"/>,
    /// the same <see cref="HandleSwapPart"/> relay route (native
    /// <c>GetGroupSectorFlying(pcluster)</c>, <c>FedSrv.CPP:6652</c>).</item>
    /// </list>
    /// Runs on the sim thread (dispatch drains inside <see cref="Tick"/>), so it
    /// may mutate the ship's cargo hold / ammo / mounted-fractions without extra
    /// locking, like every other inbound handler.
    /// </remarks>
    private void HandleReload(ConnectionId from, LiveShip live, Reload req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            ConsoleLog.Write($"[reload] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6506)");
            return;
        }

        // Native GetCluster gate: FedSrv.CPP:6302,6466,6515-6516.
        if (ship.StationId is not null || ship.Sector is null || ship.Dead || ship.SideId is not >= 0) return;

        // Native TrapHackBoot(pfsPlayer->GetIGCShip()->GetParentShip() == NULL)
        // (FedSrv.CPP:6509): a boarded gunner/observer cannot reload its own hold.
        if (ship.ParentShip is not null)
        {
            ConsoleLog.Write($"[reload] {from} ship {ship.Id}: boarded gunner (parent {ship.ParentShip.Id}) — RELOAD ignored (FedSrv.CPP:6509)");
            return;
        }

        // Native skips the whole body unless there's a real cluster AND a
        // non-empty part list (the NYI lifepod hack, FedSrv.CPP:6516).
        if (ship.Sector is null || ship.Parts.Count == 0)
        {
            Console.WriteLine($"[reload] {from} ship {ship.Id}: {(ship.Sector is null ? "no sector" : "empty loadout")} — skipped (native NYI gate, FedSrv.CPP:6516)");
            return;
        }

        // Native TrapHackBoot((nReloads > 0) && (nReloads <= 5))
        // (FedSrv.CPP:6523). Softened: an out-of-range batch is refused outright.
        int nReloads = req.Entries.Count;
        if (nReloads <= 0 || nReloads > 5)
        {
            ConsoleLog.Write($"[reload] {from} ship {ship.Id}: {nReloads} entries outside [1,5] — refused (native TrapHackBoot, FedSrv.CPP:6523)");
            return;
        }

        // The per-entry transfer loop (FedSrv.CPP:6529-6643). bBoot short-circuits
        // the batch WITHOUT rolling back earlier entries; the broadcast only
        // happens when every entry was well-formed (the pinned P192 quirk:
        // a malformed entry refuses the rest and suppresses the broadcast —
        // CargoBayReloadTests.Reload_malformed_entry_refuses_rest_without_rollback_or_broadcast).
        // P626 — the client's reload key therefore sends an entry ONLY for cargo
        // slots it expects to reload cleanly (NA on ammo/fuel packs and orphan
        // launchers all succeed); a spare weapon parked in cargo is not addressed
        // by the client and is the caller's responsibility, exactly as native.
        bool bBoot = false;
        foreach (var entry in req.Entries)
        {
            if (!ship.TryApplyReloadEntry(entry.Mount, entry.AmountTransfered))
            {
                bBoot = true;
                break;   // faithful: earlier-in-batch transfers stay applied
            }
        }

        if (bBoot)
        {
            // Native: TrapHackBoot(!bBoot) — a hard disconnect. This port's
            // established convention softens it to log-and-refuse-the-rest,
            // no broadcast, no disconnect (see remarks).
            ConsoleLog.Write($"[reload] {from} ship {ship.Id}: malformed entry, refusing remaining reloads (softened TrapHackBoot, FedSrv.CPP:6645-6648)");
            return;
        }

        // Fill in shipID and broadcast RELOAD(129) sector-wide
        // (pfmReload->shipID = pfsPlayer->GetShipID(); then
        // ForwardMessage(GetGroupSectorFlying(pcluster), pfmReload),
        // FedSrv.CPP:6651-6652).
        var msg = new Reload { Entries = req.Entries, ShipID = ship.Id };
        SendSectorWide(ship.Sector, msg.Encode());
        Console.WriteLine($"[reload] {from} ship {ship.Id}: {nReloads} cargo entry/entries applied -> RELOAD(129) shipID={ship.Id} sector {ship.Sector!.Id} wide");
    }

    /// <summary>
    /// P144 — the <c>FM_CS_DROP_PART(33)</c> handler, the port of
    /// <c>FedSrv.CPP:6296-6330</c> <c>case FM_CS_DROP_PART</c>. A flying,
    /// unboarded ship asks to drop the mounted part at <c>(et, mount)</c> into
    /// space. The part is removed from the ship's loadout (a removal, unlike
    /// SWAP_PART's reposition), the whole original message is re-broadcast
    /// <b>sector-wide</b> with <c>shipID</c> filled in — everyone flying in the
    /// same cluster (<c>GetGroupSectorFlying(pcluster)</c>,
    /// <c>FedSrv.CPP:6312</c>), the exact same audience SWAP_PART uses — and,
    /// unless the dropped part is an empty (<c>Amount == 0</c>) Pack, a real
    /// part-type treasure is created in the sector and announced via a
    /// sector-wide <c>FM_S_TREASURE(28)</c> send.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Gate: mission started + unboarded.</b> Identical to
    /// <see cref="HandleSwapPart"/>'s — native requires
    /// <c>pfsMission-&gt;GetStage() == STAGE_STARTED</c> and
    /// <c>GetParentShip() == NULL</c> (<c>FedSrv.CPP:6297,6300</c>).</item>
    /// <item><b>No-match is a silent no-op.</b> If no part is at
    /// <c>(et, mount)</c>, native does nothing — no error, no broadcast, no
    /// treasure (<c>FedSrv.CPP:6322</c>). Log-and-return.</item>
    /// <item><b>Relay then conditional treasure.</b> Native forwards the
    /// original <c>DROP_PART</c> <em>regardless</em> of whether a treasure is
    /// created (<c>FedSrv.CPP:6312</c>), and only then creates the treasure
    /// guarded by <c>((type != OT_pack) &amp;&amp; !IlauncherIGC::IsLauncher(type))
    /// || (ppart-&gt;GetAmount() != 0)</c> (<c>FedSrv.CPP:6323</c>) and
    /// terminates the part (<c>FedSrv.CPP:6327</c>). This port collapses the
    /// launcher branch (no magazine/dispenser/chaff-launcher subtype exists in
    /// <see cref="DataPartTypeIGC"/>'s set), so the effective skip is: dropped
    /// part is <c>Pack</c> <em>and</em> its <c>Amount == 0</c>.</item>
    /// <item><b>Send target: sector-wide</b> — <see cref="SendSectorWide"/>,
    /// the same <see cref="HandleSwapPart"/> relay route (native
    /// <c>GetGroupSectorFlying(pcluster)</c>, <c>FedSrv.CPP:6312</c>), for both
    /// the DROP_PART echo and the TREASURE(28) notice.</item>
    /// <item><b>Treasure math.</b> Delegated to
    /// <see cref="Ship.CreateDropTreasure"/> (the port of the native
    /// <c>CreateTreasure</c> chain, <c>FedSrv.CPP:4324-4396</c>): <c>P0</c> =
    /// ship position + <c>(0.5·hull.Length + 10)·</c>random-unit-direction;
    /// <c>V0</c> = that direction × 25 + ship velocity; <c>code = Part</c>;
    /// <c>treasureID = part.PartId</c>; <c>amount = part.Amount</c>;
    /// <c>lifespan = 120.0f</c>; <c>createNow = false</c>;
    /// <c>objectID = mission.NextObjectId()</c>; <c>clusterID = ship sector</c>;
    /// <c>time0 = (uint)TickIndex</c> (this port's uint-Time convention).
    /// <c>TreasureSpawn</c>(28) is this port's first real send of the
    /// previously-built-but-never-sent codec (P123 built it for round-trip
    /// completeness only).</item>
    /// <item><b>Known accepted gaps (explicit, not silent).</b> The created
    /// treasure is <em>not</em> pickup-able (that is <c>ADD_PART</c>(164), out
    /// of scope), its <c>V0</c> is stored on the wire for a real client but not
    /// integrated into <see cref="Treasure.OnUpdate"/> (it rests static, like
    /// cash treasures), and <c>lifespan</c>/<c>time0</c> expiry is not enforced
    /// — all per <see cref="Treasure"/>'s own remarks and this task's scope.</item>
    /// </list>
    /// </remarks>
    private void HandleDropPart(ConnectionId from, LiveShip live, DropPart req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            ConsoleLog.Write($"[droppart] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6297)");
            return;
        }

        // Native GetCluster gate: FedSrv.CPP:6302,6466,6515-6516.
        if (ship.StationId is not null || ship.Sector is null || ship.Dead || ship.SideId is not >= 0) return;

        // Native TrapHackBoot(pfsPlayer->GetIGCShip()->GetParentShip() == NULL)
        // (FedSrv.CPP:6300): a boarded gunner/observer cannot drop its own parts.
        if (ship.ParentShip is not null)
        {
            ConsoleLog.Write($"[droppart] {from} ship {ship.Id}: boarded gunner (parent {ship.ParentShip.Id}) — DROP_PART ignored (FedSrv.CPP:6300)");
            return;
        }

        // P626 — a negative (cargo-bay) mount drops a CARGO part (native's
        // TK_DropCargo, WinTrek.cpp:9698-9730: it drops the selected cargo part
        // into space); a non-negative mount drops an equipped part. The wire
        // Mount is a signed char, so a high-bit byte is a negative mount.
        sbyte signedMount = unchecked((sbyte)req.Mount);
        if ((short)req.Et < -1 || (short)req.Et > 7
            || signedMount < -Ship.CargoCapacity || signedMount >= 8
            || signedMount > 0 && req.Et != EquipmentType.Weapon
            || signedMount >= 0 && (short)req.Et < 0) return;
        if (signedMount < 0 && ship.CargoHold[-signedMount - 1] is { } cargo
            && (short)req.Et >= 0 && cargo.EquipmentType != req.Et) return;
        bool droppedOk;
        MountedPart dropped;
        if (signedMount < 0) droppedOk = ship.TryRemoveCargoPart(signedMount, out dropped);
        else droppedOk = ship.TryRemovePart(req.Et, req.Mount, out dropped);
        if (!droppedOk)
        {
            Console.WriteLine(signedMount < 0
                ? $"[droppart] {from} ship {ship.Id}: no cargo part at mount {signedMount} — silent no-op (native, FedSrv.CPP:6322)"
                : $"[droppart] {from} ship {ship.Id}: no mounted {req.Et} part at mount {req.Mount} — silent no-op (native, FedSrv.CPP:6322)");
            return;
        }

        // Fill in shipID and broadcast DROP_PART(33) sector-wide
        // (pfmDropPart->shipID = pfsPlayer->GetShipID(); then
        // ForwardMessage(GetGroupSectorFlying(pcluster), pfmDropPart),
        // FedSrv.CPP:6311-6312). An in-game ship always has a sector, so
        // SendSectorWide is the faithful route (cf. HandleSwapPart's reasoning).
        var msg = new DropPart
        {
            ShipID = ship.Id,
            Et = req.Et,
            Mount = req.Mount,
        };
        SendSectorWide(ship.Sector!, msg.Encode());
        Console.WriteLine($"[droppart] {from} ship {ship.Id}: dropped {req.Et}@{req.Mount} -> DROP_PART(33) shipID={ship.Id} sector {ship.Sector!.Id} wide");

        // Conditional treasure: native's guard is
        // ((type != OT_pack) && !IsLauncher(type)) || (amount != 0)
        // (FedSrv.CPP:6323); the launcher branch does not exist in this port, so
        // it collapses to: skip iff the dropped part is a Pack with Amount == 0
        // (third CreateTreasure overload also returns NULL in that case,
        // FedSrv.CPP:4385-4387).
        if (dropped.Type.EquipmentType == EquipmentType.Pack && dropped.Amount == 0)
        {
            Console.WriteLine($"[droppart] {from} ship {ship.Id}: dropped empty Pack (amount 0) — no treasure (native skip, FedSrv.CPP:6323)");
            return;
        }

        var treasure = ship.CreateDropTreasure(dropped, _simNow, (uint)TickIndex);
        SendSectorWide(ship.Sector!, new TreasureSpawn { Data = treasure.Data }.Encode());
        Console.WriteLine($"[droppart] {from} ship {ship.Id}: created part treasure obj={treasure.Data.ObjectId} part={treasure.Data.TreasureId} amount={treasure.Data.Amount} -> TREASURE(28) sector {ship.Sector!.Id} wide");
    }

    /// <summary>
    /// P146 — the <c>FM_CS_SET_WINGID(106)</c> handler, the port of
    /// <c>FedSrv.CPP:5312-5344</c> <c>case FM_CS_SET_WINGID</c>. A player tags a
    /// ship (normally their own) with a wing-group id — a small bounded group
    /// number <c>[0, c_widMax)</c> — the (server-corrected) message is relayed
    /// <b>side-wide</b>, and the target ship's <c>WingID</c> state is mutated
    /// server-side.
    /// <list type="bullet">
    /// <item><b>Gate: mission present only.</b> Native's sole gate is
    /// <c>if (!pfsMission) break</c> (<c>FedSrv.CPP:5313</c>) — there is
    /// genuinely <b>no</b> <c>STAGE_STARTED</c> check here, unlike most other
    /// in-mission handlers (no <c>GetStage() == STAGE_STARTED</c> line in the
    /// whole case). Re-read of the quoted handler confirms it, so this port adds
    /// none.</item>
    /// <item><b>Self-targeting-only (deliberate simplification).</b> Native lets
    /// a side's <em>leader</em> retag another ship on its own side
    /// (<c>FedSrv.CPP:5323-5330</c>: <c>pfsPlayer == pfsMission-&gt;GetLeader(...)</c>
    /// — a team-leader concept absent everywhere in this port). With no leader
    /// system, that branch is unreachable, so <c>shipID</c> must resolve to the
    /// <em>sending</em> player's own ship; any other target is a silent no-op
    /// (no broadcast, no state change). <c>bCommanded</c> is therefore always
    /// server-corrected to <c>false</c> (the self-assignment value,
    /// <c>FedSrv.CPP:5334-5335</c>).</item>
    /// <item><b>Wing-id range check.</b> Native <c>TrapHackBoot(pfmSW-&gt;wingID
    /// &lt; c_widMax)</c> (<c>FedSrv.CPP:5319</c>); <c>c_widMax = 10</c>
    /// (<c>igc.h:505</c>). The port rejects out-of-range (incl. negative = <c>NA</c>)
    /// ids as a silent no-op, matching the treat-a-bad-request-as-no-op pattern
    /// used elsewhere (cf. <see cref=\"HandleSwapPart\"/>).</item>
    /// <item><b>Audience: side-wide, not sector-wide.</b> Native forwards to
    /// <c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c> (<c>FedSrv.CPP:5337</c>) —
    /// the sending side's peer group, <em>not</em>
    /// <c>GetGroupSectorFlying(pcluster)</c> (the sector audience SWAP_PART/
    /// DROP_PART/PROMOTE use). The port's side-group analogue is the
    /// <c>BroadcastMoneyChangeSideWide</c> shape (<see cref=\"Server\"/>
    /// <c>Server.cs:3626-3637</c>): filter <c>_peerShips</c> by matching
    /// <c>Ship.SideId</c>, pack once, send to each target. A different-side peer
    /// flying in the same sector must <b>not</b> receive it — a different
    /// audience shape than SWAP_PART/DROP_PART/PROMOTE, confirmed by the live
    /// test.</item>
    /// <item><b>Server-side state.</b> <c>pfsShip-&gt;GetIGCShip()-&gt;
    /// SetWingID(pfmSW-&gt;wingID)</c> (<c>FedSrv.CPP:5338</c>) — this port mutates
    /// <see cref="Ship.WingId"/>.</item>
    /// </list>
    /// </summary>
    private void HandleFireExpendable(ConnectionId from, LiveShip live, FireExpendableRequest req)
    {
        var ship = live.Ship;
        // Native requires GetCluster() (FedSrv/FedSrv.CPP:5428,5634).
        if (ship.StationId is not null || ship.Sector is null) return;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            ConsoleLog.Write($"[fireexpendable] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:5611)");
            return;
        }

        // Native TrapHackBoot(pShip->GetParentShip() == NULL) (FedSrv.CPP:5644):
        // a boarded gunner/observer cannot fire its own dispenser. Ported as the
        // graceful log-and-return refusal every other TrapHackBoot-guarded C→S
        // handler here uses (cf. HandleSwapPart, FedSrv.CPP:6464).
        if (ship.ParentShip is not null)
        {
            ConsoleLog.Write($"[fireexpendable] {from} ship {ship.Id}: boarded gunner (parent {ship.ParentShip.Id}) — FIRE_EXPENDABLE ignored (FedSrv.CPP:5644)");
            return;
        }

        // Resolve the mounted dispenser at mount 0 of the requested equipment
        // type (GetMountedPart(et, 0), FedSrv.CPP:5652). No such part is native's
        // silent `if (pdispenser)` guard — a silent no-op.
        var part = ship.Parts.FirstOrDefault(p => p.EquipmentType == req.Et && p.MountId == 0);
        if (part is null)
        {
            Console.WriteLine($"[fireexpendable] {from} ship {ship.Id}: no mounted {req.Et} part at mount 0 — silent no-op (native, FedSrv.CPP:5652)");
            return;
        }

        // Native TrapHackBoot(pdispenser->GetAmount() > 0) (FedSrv.CPP:5656).
        // Ported as a graceful refusal: no chaff, no broadcast, no decrement — the
        // must-fail-if-removed guard. (This port does not throw on a hack-flagged
        // request; it reports and ignores, matching the established idiom.)
        if (part.Amount <= 0)
        {
            ConsoleLog.Write($"[fireexpendable] {from} ship {ship.Id}: dispenser {req.Et}@0 has Amount {part.Amount} — refused, no fire (native TrapHackBoot, FedSrv.CPP:5656)");
            return;
        }

        // P224 — dispatch the mounted launcher by its construction-only subtype:
        // chaff / mine / probe. Native resolves the expendable from the
        // dispenser's expenditureTypeID and branches on its object type
        // (OT_chaffType / OT_mineType / OT_probeType, FedSrv.CPP:5696); the port's
        // mounted-part subtype (DataPartTypeIGC.ChaffLauncher / MineLauncher /
        // ProbeLauncher, DataPartTypeIGC.cs) is that resolution's stand-in.
        bool sectorWide;          // chaff: whole cluster; mine/probe: same-side only
        List<byte[]> enc;
        string deployed;          // human-readable description for the log line
        switch (part.Type.Type)
        {
            case DataPartTypeIGC.ChaffLauncher cl:
            {
                if (!mission.Core.ChaffTypes.TryGetValue(cl.ExpendableTypeId, out var chaffType))
                {
                    ConsoleLog.Write($"[fireexpendable] {from} ship {ship.Id}: chaff launcher names unknown chaff type {cl.ExpendableTypeId} — ignored");
                    return;
                }

                // Deploy the chaff at the ship's position/velocity and run the native
                // missile-retarget loop; the result names the new Chaff and the spoofed
                // missiles (fed by the server's shared _rand, like native's global random).
                var result = ship.DeployChaff(chaffType, mission.LastUpdate, _rand);
                sectorWide = true;

                // Build all four broadcasts in one guaranteed batch (native creates them
                // in this same handler and flushes once: SendMessages(GetGroupSectorFlying
                // (pcluster), FM_GUARANTEED, FM_FLUSH), FedSrv.CPP:5749). Audience: the
                // whole cluster, all sides — GetGroupSectorFlying(pcluster), the exact
                // SendSectorWide audience.
                enc = new List<byte[]>
                {
                    new FireExpendable { LauncherId = ship.Id, EquipmentType = req.Et }.Encode(),
                    new CreateChaff
                    {
                        P0 = result.Chaff.Position,
                        V0 = result.Chaff.Velocity,
                        Time0 = (uint)TickIndex,   // this project's uint-Time convention (cf. BroadcastFireMissile)
                        Etid = chaffType.ExpendableTypeId,
                    }.Encode(),
                };
                foreach (var sp in result.Spoofed)
                    enc.Add(new MissileSpoofed { MissileId = sp.Id }.Encode());
                enc.Add(new EndSpoofing().Encode());   // exactly once, even when nothing was spoofed
                deployed = $"chaff {chaffType.ExpendableTypeId} (pos {result.Chaff.Position} vel {result.Chaff.Velocity}) " +
                           $"-> FE(87)+CREATE_CHAFF(109)+[{result.Spoofed.Count}×MISSILE_SPOOFED]+END_SPOOFING(111)";
                break;
            }
            case DataPartTypeIGC.MineLauncher ml:
            {
                if (!mission.Core.MineTypes.TryGetValue(ml.ExpendableTypeId, out var mineType))
                {
                    ConsoleLog.Write($"[fireexpendable] {from} ship {ship.Id}: mine launcher names unknown mine type {ml.ExpendableTypeId} — ignored");
                    return;
                }
                var mine = ship.DeployMine(mineType, mission.LastUpdate);
                sectorWide = false;
                // Mine/probe carry no dedicated instance message: native's single
                // S FIRE_EXPENDABLE (pfmSFE) plus the mine object exported through the
                // generic CreateObject/OT_mine path. The object EXPORT is triggered here
                // automatically by DeployMine → MineCreatedEvent → BroadcastExportMine.
                enc = new List<byte[]> { new FireExpendable { LauncherId = ship.Id, EquipmentType = req.Et }.Encode() };
                deployed = $"mine {mineType.ExpendableTypeId} (id {mine.Id}) -> FE(87)+EXPORT(26)/OT_mine";
                break;
            }
            case DataPartTypeIGC.ProbeLauncher pl:
            {
                if (!mission.Core.ProbeTypes.TryGetValue(pl.ExpendableTypeId, out var probeType))
                {
                    ConsoleLog.Write($"[fireexpendable] {from} ship {ship.Id}: probe launcher names unknown probe type {pl.ExpendableTypeId} — ignored");
                    return;
                }
                var probe = ship.DeployProbe(probeType, mission.LastUpdate);
                sectorWide = false;
                enc = new List<byte[]> { new FireExpendable { LauncherId = ship.Id, EquipmentType = req.Et }.Encode() };
                deployed = $"probe {probeType.ExpendableTypeId} (id {probe.Id}) -> FE(87)+EXPORT(26)/OT_probe";
                break;
            }
            default:
            {
                ConsoleLog.Write($"[fireexpendable] {from} ship {ship.Id}: mounted {req.Et}@0 is {part.Type.Type.GetType().Name}, not a chaff/mine/probe launcher — ignored");
                return;
            }
        }

        // Decrement the dispenser's ammo (FedSrv.CPP:5744-5745).
        short newAmount = (short)(part.Amount - 1);
        ship.SetPartAmount(part, newAmount);

        byte[] packet = FedPacket.Pack(enc.ToArray());
        int n = 0;
        if (sectorWide)
        {
            // Chaff: whole cluster, all sides — GetGroupSectorFlying(pcluster), the
            // exact SendSectorWide audience. Native's single flush (FedSrv.CPP:5783)
            // technically also covers mine/probe, but this port broadcasts mine/probe
            // OBJECT creation same-side only (no cross-side EXPORT exists), so a
            // cross-side FE(87) without the object would be incoherent.
            foreach (var kv in _peerShips)
                if (ReferenceEquals(kv.Value.Ship.Sector, ship.Sector)) { SendPeer(kv.Key, packet); n++; }
        }
        else
        {
            // Mine/probe: SAME-SIDE only, matching this port's established mine/probe
            // broadcast audience — BroadcastExportMine / BroadcastExportProbe
            // (Server.cs:9739-9743 / 9793-9797: the layer's own peer always, plus
            // same-side teammates who hold its ExportShip via the _teammateExportSent
            // gate) and BroadcastFireMissile (Server.cs:9538-9546). Every FE(87)
            // recipient therefore also receives the mine/probe EXPORT.
            if (ship.SideId is { } sideId)
            {
                foreach (var kv in _peerShips)
                {
                    bool isLayer = ReferenceEquals(kv.Value.Ship, ship);
                    bool isVisibleTeammate = !isLayer
                                             && kv.Value.Ship.SideId == sideId
                                             && _teammateExportSent.Contains((kv.Key, ship.Id));
                    if (isLayer || isVisibleTeammate) { SendPeer(kv.Key, packet); n++; }
                }
            }
        }

        Console.WriteLine($"[fireexpendable] {from} ship {ship.Id} fired {deployed} to {n} {(sectorWide ? "cluster" : "same-side")} peer(s); " +
                          $"dispenser {newAmount + 1} -> {newAmount}");
    }

    /// <summary>
    /// P226 — the inbound <c>FM_CS_FIRE_MISSILE(70)</c> player-launch request,
    /// the port of native <c>FedSrv.CPP:5413-5507</c> <c>case FM_CS_FIRE_MISSILE</c>.
    /// Gates (mirroring native): the mission must be <c>STAGE_STARTED</c>
    /// (<c>FedSrv.CPP:5416</c>), the launcher unboarded (<c>TrapHackBoot(parent
    /// == NULL)</c>, <c>FedSrv.CPP:5423</c>), and a missile launcher mounted at
    /// <c>ET_Magazine</c>@0 (<c>GetMountedPart(ET_Magazine, 0)</c>,
    /// <c>FedSrv.CPP:5425</c>) with <c>Amount &gt; 0</c> (<c>FedSrv.CPP:5426</c>).
    /// The server is authoritative and ignores the client-supplied ids/missile-type/
    /// target ("Ignore the IDs provided by the client and generate one ourselves",
    /// <c>FedSrv.CPP:5444</c>): it resolves the launcher's own missile type and
    /// fires against the ship's server-side <see cref="Ship.CommandTarget"/> — the
    /// lock target <c>Ship.UpdateMissileLock</c> builds against each tick — via the
    /// shared <see cref="Ship.TryFireMissile"/> gate, which requires the lock to be
    /// sufficient (a <c>MaxLock == 0</c> type needs none) <em>and</em> ammo
    /// remaining before a <see cref="Missile"/> is created. Creation raises
    /// <c>MissileCreatedEvent</c> → <see cref="BroadcastFireMissile"/>, the outbound
    /// FIRE_MISSILE(70) to the firing ship's sector, and decrements ammo. No
    /// inbound <c>Server.cs</c> FIRE_MISSILE case existed before P226 — the brief
    /// called that absence correct — but native's real player path is exactly this
    /// inbound message, so P226 adds it (documented in <c>docs/MISSILE_FIRE_P226.md</c>).
    /// </summary>
    private void HandleFireMissile(ConnectionId from, LiveShip live, FireMissile req)
    {
        var ship = live.Ship;
        // Native requires GetCluster() (FedSrv/FedSrv.CPP:5428,5634).
        if (ship.StationId is not null || ship.Sector is null) return;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            ConsoleLog.Write($"[firemissile] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:5416)");
            return;
        }
        if (ship.ParentShip is not null)
        {
            ConsoleLog.Write($"[firemissile] {from} ship {ship.Id}: boarded gunner (parent {ship.ParentShip.Id}) — FIRE_MISSILE ignored (FedSrv.CPP:5423)");
            return;
        }
        var part = ship.Parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0);
        if (part is not { Type.Type: DataPartTypeIGC.MissileLauncher })
        {
            ConsoleLog.Write($"[firemissile] {from} ship {ship.Id}: no mounted missile launcher (ET_Magazine@0) — ignored (native GetMountedPart(ET_Magazine,0), FedSrv.CPP:5425)");
            return;
        }
        if (part.Amount <= 0)
        {
            ConsoleLog.Write($"[firemissile] {from} ship {ship.Id}: launcher has Amount {part.Amount} — refused, no fire (native TrapHackBoot amount>0, FedSrv.CPP:5426)");
            return;
        }

        short amountBefore = part.Amount;
        bool fired = ship.TryFireMissile(mission.LastUpdate);
        short amountAfter = fired ? ship.Parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0)?.Amount ?? amountBefore : amountBefore;
        Console.WriteLine(fired
            ? $"[firemissile] {from} ship {ship.Id} launched -> FIRE_MISSILE(70) lock {ship.MissileLock:F3} ammo {amountBefore} -> {amountAfter}"
            : $"[firemissile] {from} ship {ship.Id}: FIRE_MISSILE requested but lock gate not met (MissileLock {ship.MissileLock:F3}, MaxLock need none iff 0) — no missile");
    }

    private void HandleSetWingId(ConnectionId from, LiveShip live, SetWingId req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;

        // Native's ONLY gate: if (!pfsMission) break (FedSrv.CPP:5313). Deliberately
        // no STAGE_STARTED check — the native SET_WINGID handler has none.
        if (mission is null)
        {
            ConsoleLog.Write($"[setwingid] {from} ship {ship.Id}: no mission — ignored (native gate, FedSrv.CPP:5313)");
            return;
        }

        // Self-targeting-only simplification: without a team-leader system the
        // native leader-retags-another-ship branch (FedSrv.CPP:5323-5330) is
        // unreachable, so shipID must be the sender's own ship; otherwise silent.
        if (req.ShipID != ship.Id)
        {
            ConsoleLog.Write($"[setwingid] {from} ship {ship.Id}: SET_WINGID for ship {req.ShipID} != own ship — ignored (self-targeting-only, FedSrv.CPP:5323)");
            return;
        }

        // Native TrapHackBoot(pfmSW->wingID < c_widMax) (FedSrv.CPP:5319);
        // c_widMax = 10 (igc.h:505). Negative ids (= NA) also rejected.
        if (req.WingId < 0 || req.WingId >= SetWingId.WidMax)
        {
            ConsoleLog.Write($"[setwingid] {from} ship {ship.Id}: wingID {req.WingId} out of range [0,{SetWingId.WidMax}) — ignored (native bound, FedSrv.CPP:5319)");
            return;
        }

        // Server-corrected message: bCommanded always false for a self-assignment
        // (FedSrv.CPP:5334-5335); the branch that would set true (leader retag)
        // is removed with the leader system.
        var corrected = new SetWingId
        {
            ShipID = ship.Id,
            WingId = req.WingId,
            BCommanded = false,
        };
        byte[] packet = FedPacket.Pack(new[] { corrected.Encode() });

        // Side-wide broadcast — port of CFSSide::FromIGC(pside)->GetGroup()
        // (FedSrv.CPP:5337). Filter _peerShips by Ship.SideId, the same
        // side-group analogue BroadcastMoneyChangeSideWide uses
        // (Server.cs:3626-3637). NOT the sector group.
        var sideId = ship.SideId;
        var targets = _peerShips
            .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();
        foreach (var t in targets) SendPeer(t, packet);

        // Mutate the target ship's wing server-side
        // (pfsShip->GetIGCShip()->SetWingID(pfmSW->wingID), FedSrv.CPP:5338).
        ship.WingId = req.WingId;

        Console.WriteLine($"[setwingid] {from} ship {ship.Id}: wingID -> {req.WingId} (bCommanded=false) side {sideId} to {targets.Length} peer(s)");
    }

    /// <summary>
    /// P200 — answer a client's <c>FM_C_REQPINGDATA(195)</c> (the w0dk4
    /// player-pings feature) with that client's server-wide latency roster.
    /// Port of <c>FedSrv.CPP:7743-7808</c> <c>case FM_C_REQPINGDATA</c>:
    /// <list type="bullet">
    /// <item><b>5-second debounce</b> (native
    /// <c>CFMConnection::PingQuery</c>, <c>messagecore.h:217-223</c>): a
    /// request less than 5s after this connection's last successful full send
    /// gets only the unchanged-window marker, never the per-player entries.</item>
    /// <item><b>Whole-server roster, requester-only.</b> On a full send, one
    /// <see cref="PingData"/> is created for EVERY connection on the whole
    /// server with a live player (this port's <c>_peerShips</c> = native's
    /// "all connections with a live CFSPlayer"), deliberately NOT scoped to a
    /// mission or side — native iterates <c>g.fm.GetConnections()</c> outright.
    /// Every entry and the terminator are addressed ONLY to the requester
    /// (native: every <c>SendMessages(pfsPlayer-&gt;GetConnection(), ...)</c>).</item>
    /// <item><b>Terminator marker.</b> The final <c>{shipID = -1, ping = 1}</c>
    /// PINGDATA reuses the exact wire shape with a sentinel <c>shipID</c> as the
    /// "batch just sent / nothing changed" window close — not a player's data.</item>
    /// <item><b>Value sourcing.</b> Native derives <c>ping</c> from DirectPlay's
    /// <c>dwRoundTripLatencyMS</c> and <c>loss</c> from its retry/drop counters;
    /// this port's plain TCP transport exposes no managed access to either, so
    /// entries carry <c>ping = 0, loss = 0</c> (the task's stated, pre-approved
    /// fallback — wire-complete but not a measured value; see
    /// <c>docs/PINGDATA_REQPINGDATA_P200.md</c>).</item>
    /// </list>
    /// Runs on the sim thread (dispatch drains inside <see cref="Tick"/>).
    /// </summary>
    private void HandleReqPingData(ConnectionId from)
    {
        // The debounce gate (native PingQuery: elapsed > 5s ⇒ full roster).
        bool full = !_lastPingQuery.TryGetValue(from, out var last) || (_simNow - last) > 5.0;
        if (full)
            _lastPingQuery[from] = _simNow;   // stamp on the successful full send only

        // The terminator / unchanged-window marker (sentinel shipID = -1,
        // ping = 1; loss left 0 as BEGIN_PFM_CREATE zero-initialises it).
        var marker = new PingData { ShipId = -1, Ping = 1, Loss = 0 };

        if (!full)
        {
            // Debounced: send only the unchanged-window marker to the requester.
            SendPeer(from, FedPacket.Pack(new[] { marker.Encode() }));
            Console.WriteLine($"[pingdata] {from}: REQPINGDATA debounced (<5s) — unchanged-window marker only");
            return;
        }

        // Whole-server roster: one PINGDATA per live player, requester-only.
        var entries = new List<PingData>();
        foreach (var kv in _peerShips)
        {
            entries.Add(new PingData
            {
                ShipId = kv.Value.Ship.Id,
                Ping = 0,   // no managed transport RTT — see class/doc notes
                Loss = 0,   // no managed transport loss counter
            });
        }
        entries.Add(marker);

        byte[] packet = FedPacket.Pack(entries.Select(e => e.Encode()).ToArray());
        SendPeer(from, packet);   // ONLY the requester sees any of this
        Console.WriteLine($"[pingdata] {from}: full roster -> {_peerShips.Count} player(s) + terminator, requester-only");
    }

    /// <summary>
    /// P148 — the <c>FM_CS_ORDER_CHANGE(93)</c> handler, Stage 2 of the
    /// ORDER_CHANGE foundation arc. Ports <b>only the "stay docked" slice</b> of
    /// native <c>FedSrv.CPP:7264-7299</c> <c>case FM_CS_ORDER_CHANGE</c>:
    /// <list type="bullet">
    /// <item><b>Gate: <c>STAGE_STARTED</c> only.</b>
    /// <c>if (!pfsMission || pfsMission-&gt;GetStage() != STAGE_STARTED) break</c>
    /// (<c>FedSrv.CPP:7266</c>) — no other checks precede the decode.</item>
    /// <item><b>Target resolution.</b> <c>pship = pfsPlayer-&gt;GetIGCShip()</c>;
    /// <c>if (pside-&gt;GetShip(pfmOC-&gt;shipID)) pship = pside-&gt;GetShip(...)</c>
    /// (<c>FedSrv.CPP:7271-7278</c>) — the target is looked up on the commanding
    /// player's <em>own side</em>, and an unresolvable <c>shipID</c> leaves
    /// <c>pship</c> as the sender's own ship (not an error). Mirrored via the
    /// P148-extended <see cref="FindSameSideShip"/>, falling back to the sender.</item>
    /// <item><b>Stay-docked mutation.</b> When the target is an NPC
    /// (<c>GetPilotType() &lt; c_ptPlayer</c>, <c>FedSrv.CPP:7285</c>), docked
    /// (<c>GetStation() != NULL</c>, <c>FedSrv.CPP:7288</c>), and commanded
    /// <c>commandID == c_cidDoNothing</c> (<c>FedSrv.CPP:7290</c>), native sets
    /// <c>SetStayDocked(true)</c> (<c>FedSrv.CPP:7292</c>) — this port sets
    /// <see cref="Ship.StayDocked"/>.</item>
    /// </list>
    /// <para><b>Explicit exclusions (documented in ORDER_CHANGE_P148.md /
    /// ORDER_CHANGE_P150.md / ORDER_CHANGE_P152.md):</b> the carrier-specific
    /// shipyard-then-repair fallback (<c>FedSrv.CPP:7312-7320</c>,
    /// unreachable here — this port has no carrier pilot-type distinction);
    /// <c>c_cmdPlan</c> / the generic <c>SetCommand</c> fallback for non-NPC
    /// targets (<c>FedSrv.CPP:7335-7339</c>, which needs the full native 4-slot
    /// command array this port does not have). The docked "launch" sub-branch
    /// (formerly deferred — <c>FedSrv.CPP:7296-7302</c>) IS now handled (P152):
    /// it needs no <c>PickDefaultOrder</c>-equivalent call in this port's scope
    /// (see the launch arm below). Every combination outside the docked /
    /// return-to-base gates is <b>logged and no-oped</b>, not guessed at.</para>
    /// <para><b>Chat ack (stretch goal, not implemented).</b> Native also sends
    /// a <c>SendChat(... CHAT_INDIVIDUAL ...)</c> "Affirmative." ack from the
    /// commanded ship to the commander (<c>FedSrv.CPP:7293-7294</c>). This port's
    /// chat relay is client-initiated (<c>Server.cs:2528+</c>), and no reusable
    /// "server constructs a chat as if from ship X" helper exists; building one
    /// is out of the deliberate-narrow scope. The state mutation is the proof
    /// point and stands alone — the ack is documented as deferred, not dropped
    /// silently.</para>
    /// Runs on the sim thread (the dispatch drains inside <see cref="Tick"/>),
    /// so it may mutate ship state without extra locking.
    /// </summary>
    private void HandleOrderChange(ConnectionId from, LiveShip live, OrderChange req)
    {
        var ship = live.Ship;                        // the commanding player's ship
        var mission = ship.Mission;

        // Native gate: if (!pfsMission || pfsMission->GetStage() != STAGE_STARTED) break (FedSrv.CPP:7266).
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            ConsoleLog.Write($"[orderchange] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:7266)");
            return;
        }

        // Target resolution: pside->GetShip(shipID) on the commander's own side,
        // falling back to the sender's own ship when shipID doesn't resolve
        // (FedSrv.CPP:7271-7278).
        var target = FindSameSideShip(req.ShipID, ship.SideId, mission) ?? ship;

        // P590 — the c_cidPickup order: a lifepod commands a same-side
        // rescue-capable friendly to pick it up (the port's client path for
        // native's individual-chat c_cidPickup, WinTrek.cpp:10666-10680 /
        // trekigc.cpp:4893-4897). Validated against native's own combos
        // (shipIGC.h:1661-1678, via LegalCommand): sender must be a lifepod,
        // target same-side and rescue-capable, both flying (not docked).
        if (req.CommandId == OrderChange.CidPickup)
        {
            if (ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod)
                && target.HullType.HasCapability(DataHullTypeIGC.HabmRescue)
                && target.SideId == ship.SideId
                && target.StationId is null && !target.Dead)
            {
                // The rescued pod is the SENDER itself (the subject of the pickup).
                target.PickupTarget = ship;
                Console.WriteLine($"[pickup] {from} ship {ship.Id} (lifepod) ordered pickup by {target.Id}: target.PickupTarget={ship.Id} — {(target.IsNpc ? "NPC diverter armed" : "real-player rescuer queued")}");
                if (!target.IsNpc && ConnOf(target) is { } pc)
                {
                    // Real-player rescuer: stream the pending order to its client
                    // so it can show "prepare for recovery. Press [insert] to
                    // accept." (native trekigc.cpp:4896), and it can accept/act.
                    var cupd = new QueuedCommandUpdate
                    {
                        CommandId = OrderChange.CidPickup,
                        TargetType = IgcObjectType.Ship,
                        TargetId = ship.Id,
                    };
                    SendPeer(pc, FedPacket.Pack(new[] { cupd.Encode() }));
                }
                return;
            }
            ConsoleLog.Write($"[orderchange] {from} ship {ship.Id} c_cidPickup: rejected (sender{(ship.HullType.HasCapability(DataHullTypeIGC.HabmLifepod) ? "" : " NOT")} a pod, target{(target.HullType.HasCapability(DataHullTypeIGC.HabmRescue) ? "" : " NOT")} rescue-capable, same-side={target.SideId == ship.SideId}, target docked/dead={target.StationId is not null || target.Dead})");
            return;
        }

        // P425 — DIRECT command of a same-side NPC/drone to BUILD. A drone is
        // never player-piloted (native spawns builders as command-driven drones,
        // FedSrv.CPP:2220,11351); commanding one means naming it as ShipID and
        // sending the desired command, and the drone obeys immediately — no
        // accept/reject (distinct from P414/P424's queued order flow, which is
        // for commanding other REAL players who get free will). Native gives a
        // drone order straight onto the drone's accepted slot
        // (OfflineCommandToDrone sets c_cmdAccepted, gated on LegalCommand,
        // clintlib.cpp; the Builder's accepted build is promoted to its plan on
        // the next update, shipIGC.cpp:1939). This port expresses "legal build"
        // exactly as P422's player path does (Builder-role hull + OT_asteroid +
        // capability match + configured station type) via the SHARED legality
        // body, but WITHOUT the self-command-only gate — commanding the drone is
        // the entire point here. Placed BEFORE the two NPC arms below (docked →
        // stay/launch, flying → return-to-base) which would otherwise swallow
        // every commandID for an NPC; a real Build order must reach construction
        // instead. Consistent with P422/P165's "no travel-before-build gate":
        // a legal order starts the timed construction immediately.
        if (target.IsNpc && req.CommandId == OrderChange.CidBuild)
        {
            HandleDirectNpcBuildOrder(from, live, target, req);
            return;
        }

        // P148/P150/P152 docked NPC handling: target IsNpc && docked. Two arms —
        // commandID==c_cidDoNothing → stay docked (P148); anything else →
        // launch (P152). Both are halves of the native docked branch
        // (FedSrv.CPP:7288-7302).
        if (target.IsNpc && target.StationId is not null)
        {
            if (req.CommandId == OrderChange.CidDoNothing)
            {
                target.StayDocked = true;   // pship->SetStayDocked(true), FedSrv.CPP:7292
                if (ConsoleLog.Verbose) ConsoleLog.Write($"[orderchange] {from} ship {ship.Id}: commanded NPC {target.Id} (side {target.SideId}, docked@{target.StationId}) to stay docked (commandID=c_cidDoNothing) -> StayDocked=true (FedSrv.CPP:7290-7294)");
            }
            else
            {
                // P152 — the "launch" arm (native FedSrv.CPP:7296-7302):
                // SetStayDocked(false) and actually leave the station.
                //
                // No PickDefaultOrder-equivalent call is made — stated plainly,
                // not omitted silently. Native calls
                // PickDefaultOrder(..., bDocked=true) here (FedSrv.CPP:7301),
                // but that call is a deliberate no-op for this port's scope.
                // Confirmed by reading the only PickDefaultOrder sub-block this
                // port has ported — the P151 take-cover check at
                // shipIGC.h:2230 gates on `!bDocked`, which `true` makes false —
                // and every other native sub-block in that switch (mining/ore,
                // unload, "arriving after player command", cross-cluster
                // FindTarget) is either irrelevant without ore/mining
                // infrastructure or itself gated on things this port doesn't
                // build (c_cidJoin, m_fOre, c_ttAnyCluster). So the state
                // transition alone (StayDocked=false + the shared undock core)
                // IS the port; a PickDefaultOrder call would be dead code.
                target.StayDocked = false;   // pship->SetStayDocked(false), FedSrv.CPP:7298
                if (target.Sector is { } undockSector)
                {
                    // Share the real-peer undock core: clear StationId, push
                    // clear of DockRadius, start the debounce. Connection-free —
                    // an NPC has no owning peer's client to receive the
                    // SET_CLUSTER that PerformUndock additionally sends (that
                    // message only informs the undocking peer's own client-side
                    // sim, appmsg.cpp:1044-1049; observing peers get the NPC's
                    // new flying state via the existing
                    // HeavyShipsUpdate/BroadcastShipsUpdate machinery, the same
                    // reasoning P147 established).
                    UndockShipState(target, undockSector);
                }
                else
                {
                    // Defensive: a docked NPC is always a placed ship
                    // (Mission.PlaceShip sets Sector; the dock seam only sets
                    // StationId afterward), so unreachable in practice — but
                    // still clear the dock rather than leave a contradictory
                    // StayDocked=false + StationId!=null state.
                    target.StationId = null;
                    if (ConsoleLog.Verbose) ConsoleLog.Write($"[orderchange] docked NPC {target.Id} had no Sector — StationId cleared without reposition (defensive)");
                }
                if (ConsoleLog.Verbose) ConsoleLog.Write($"[orderchange] {from} ship {ship.Id}: commanded docked NPC {target.Id} (side {target.SideId}) to LAUNCH (commandID={req.CommandId} != c_cidDoNothing) -> StayDocked=false, StationId=null, repositioned clear of DockRadius (FedSrv.CPP:7296-7302; no PickDefaultOrder call — bDocked=true skips the port's only ported sub-block, shipIGC.h:2230)");
            }
            return;
        }

        // P150 — the "return to base" branch: target IsNpc && NOT docked
        // (flying). Native's `else` arm, FedSrv.CPP:7304-7332. No commandID gate
        // here (unlike the docked branch) — confirmed by reading the native:
        // the else branch never reads pfmOC->commandID.
        if (target.IsNpc && target.StationId is null)
        {
            target.StayDocked = true;   // pship->SetStayDocked(true), FedSrv.CPP:7321

            // pship->SetCommand(c_cmdQueued, NULL, c_cidNone): forget any
            // remembered sector (FedSrv.CPP:7322) — unconditional in this branch.
            target.QueuedTarget = null;
            target.QueuedCommandId = OrderChange.CidNone;

            // Find the base server-side because the ship's exact location is
            // only available here when it is in a different sector (comment at
            // FedSrv.CPP:7306). This port has NO carrier pilot-type distinction
            // (Ship.IsNpc is a single bool, P148 — not a PilotType enum), so the
            // native `GetPilotType() != c_ptCarrier` gate (FedSrv.CPP:7307) is
            // UNREACHABLE here: always take the non-carrier path, the nearest
            // friendly repair-capable station in the ship's own sector (P149;
            // c_sabmRepair = 0x40). The carrier shipyard-first search
            // (FedSrv.CPP:7312-7320) is its own future slice.
            var station = target.FindNearestFriendlyStation(0x40);

            // The station-store is gated by `if (pmodel)` (FedSrv.CPP:7323-7331);
            // StayDocked + Queued-clear above happened unconditionally.
            if (station is { } st)
            {
                ApplyCommandSlot(target, req.Command, st, req.CommandId);   // pship->SetCommand(pfmOC->command, pmodel, pfmOC->commandID), FedSrv.CPP:7330
            }

            if (ConsoleLog.Verbose) ConsoleLog.Write($"[orderchange] {from} ship {ship.Id}: commanded flying NPC {target.Id} (side {target.SideId}, sector {target.Sector?.Id}) to return to base -> StayDocked=true, Queued cleared, " +
                $"{(station is null ? "no friendly repair station in sector -> command slot unset (native if(pmodel) gate, FedSrv.CPP:7323)" : $"station {station.Value.StationId} found -> slot-write dispatched for command={req.Command} cmdID={req.CommandId} (FedSrv.CPP:7330)")}");
            return;
        }

        // P153/P154 — the generic non-NPC fallback (native FedSrv.CPP:7335-7339).
        // This branch is reached exactly when the commanded ship `target` is a
        // REAL player ship (IsNpc=false): the two NPC branches above returned
        // for docked/flown NPCs, so everything left here is the player-owned
        // path. Native runs the generic SetCommand for it (DoNotExecute stays
        // false for a player, FedSrv.CPP:7284-7285; pside->GetObjectID()>=0
        // holds for any seated side):
        //
        //   if (pside->GetObjectID() >= 0 && !DoNotExecute) {
        //       pmodel = pMission->GetModel(pfmOC->objectType, pfmOC->objectID);
        //       pship->SetCommand(pfmOC->command, pmodel, pfmOC->commandID);
        //   }
        //
        // Two real client actions exercise this against a real player's own
        // ship, both sending objectType/objectID naming a target model (or
        // OT_invalid + NA to CLEAR):
        //  - P153 — the SetTarget UI action (WinTrek.cpp:8820-8852) sends
        //    command=c_cmdCurrent; the port's c_cmdCurrent slot is
        //    Ship.CommandTarget (Model?, P139).
        //  - P154 — the SetAccepted menu-order action (WinTrek.cpp:10681-10682,
        //    8907) sends command=c_cmdAccepted with the SAME pmodel as
        //    SetTarget; the port's real-player c_cmdAccepted slot is
        //    Ship.AcceptedShipTarget/AcceptedShipCommandId (Model? + short,
        //    mirroring CommandTarget's shape and distinct from the NPC-specific
        //    DataStationIGC?-typed AcceptedTarget of P150).
        // Both are wired identically: a SHIP target resolves cross-side via
        // FindShipAnySide; objectID=NA clears; any non-ship objectType is a
        // logged no-op (this port's Station is not a Model, so neither slot can
        // hold one — documented scope boundary, not a bug).
        bool accepted = req.Command == OrderChange.CmdAccepted;
        bool current = req.Command == OrderChange.CmdCurrent;

        // P424 — the real player's own pending queued order (c_cmdQueued slot).
        // Native's accept/reject of a received order are CLIENT-LOCAL
        // (AcceptQueuedCommand / RejectQueuedCommand, WinTrek.cpp:10583-10712)
        // because native stores the queued order on the client's own ship copy.
        // This port stores the real-player queued slot SERVER-side
        // (Ship.QueuedCommandId), so the client signals its intent over the
        // same real ORDER_CHANGE wire and the server performs the transition
        // authoritatively. The player responds to THEIR OWN pending order, so
        // this branch requires the commanded target to be the sender's own ship
        // (ShipID=self resolves to the sender via FindSameSideShip above). The
        // client encodes accept vs reject on the queued slot by commandID:
        //   - commandID != c_cidNone  → ACCEPT: promote queued → accepted
        //     (native AcceptQueuedCommand; the client echoes its known queued
        //     command id — the same action the server holds, so the server
        //     validates it against its own QueuedCommandId).
        //   - commandID == c_cidNone  → REJECT: clear the queued command
        //     (native RejectQueuedCommand(true, true), WinTrek.cpp:10583-10623).
        bool queued = req.Command == OrderChange.CmdQueued;
        if (queued && !target.IsNpc && ReferenceEquals(target, ship))
        {
            if (req.CommandId == OrderChange.CidNone)
            {
                // REJECT — clear the pending queued order.
                if (target.QueuedCommandId != OrderChange.CidNone)
                {
                    ClearQueuedCommand(target);
                    if (ConsoleLog.Verbose) ConsoleLog.Write($"[orderchange] {from} ship {ship.Id}: REJECTED own queued order (commandID=c_cidNone, c_cmdQueued cleared) (native RejectQueuedCommand, WinTrek.cpp:10583-10623)");
                }
                else
                {
                    if (ConsoleLog.Verbose) ConsoleLog.Write($"[orderchange] {from} ship {ship.Id}: REJECT with no pending queued order — no-op (safe)");
                }
            }
            else
            {
                // ACCEPT — promote the pending queued order to the accepted slot.
                // Native only honours a legal queued command that exists
                // (cid != c_cidNone; LegalCommand gate, WinTrek.cpp:10626-10647).
                if (target.QueuedCommandId == OrderChange.CidNone)
                {
                    if (ConsoleLog.Verbose) ConsoleLog.Write($"[orderchange] {from} ship {ship.Id}: ACCEPT with no pending queued order — no-op (native cid==c_cidNone, WinTrek.cpp:10628)");
                }
                else if (req.CommandId != target.QueuedCommandId)
                {
                    // Client echoed a different action than the one pending —
                    // not an accept of THIS order. Log and no-op (do not guess).
                    if (ConsoleLog.Verbose) ConsoleLog.Write($"[orderchange] {from} ship {ship.Id}: ACCEPT echoed commandID={req.CommandId} but pending queued order is {target.QueuedCommandId} — mismatch, no-op");
                }
                else
                {
                    target.AcceptedShipCommandId = target.QueuedCommandId;
                    target.AcceptedShipTarget = target.QueuedShipTarget;   // SetTarget+SetAccepted move the target too (WinTrek.cpp:10668-10681)
                    ClearQueuedCommand(target);
                    if (ConsoleLog.Verbose) ConsoleLog.Write($"[orderchange] {from} ship {ship.Id}: ACCEPTED own queued order {target.AcceptedShipCommandId} -> accepted slot (accepted target {(target.AcceptedShipTarget is null ? "null" : $"#{target.AcceptedShipTarget.Id}")}), queued cleared (native AcceptQueuedCommand, WinTrek.cpp:10626-10712)");
                }
            }
            return;
        }

        // H-25: another player owns their current/accepted slots. Native's
        // trusted-client lookup (FedSrv/FedSrv.CPP:7271-7278,7335-7339) needs
        // this server authority check; same-side NPC orders above stay native.
        if (!ReferenceEquals(target, ship)) return;

        if (!target.IsNpc && (accepted || current))
        {
            // P422 — the real player-issuable Build command (native
            // c_cidBuild, igc.h:910). The client's "target asteroid + Build"
            // (SetAccepted) sends command=c_cmdAccepted, commandID=c_cidBuild,
            // objectType=OT_asteroid, objectID=<rock id>. Native validates it
            // via LegalCommand's c_cidBuild case (Builder-role pilot + buildable
            // asteroid capability, shipIGC.h:1695-1707). Legal command → start
            // the timed construction; any failed gate is refused exactly as
            // native refuses it (no construction registers, no effect).
            if (req.CommandId == OrderChange.CidBuild)
            {
                HandlePlayerBuildOrder(from, live, target, req);
                return;
            }

            string slot = accepted ? "AcceptedShipTarget" : "CommandTarget";
            if (req.ObjectID == Na)
            {
                // Clear-target: SetTarget/SetAccepted's pmodel==NULL case sends
                // objectType=OT_invalid + objectID=NA (WinTrek.cpp:8834-8836);
                // native SetCommand(command, NULL, cid) clears the slot.
                if (accepted)
                {
                    target.AcceptedShipTarget = null;
                    target.AcceptedShipCommandId = OrderChange.CidNone;
                }
                else
                {
                    target.CommandTarget = null;
                }
                if (ConsoleLog.Verbose) ConsoleLog.Write($"[orderchange] {from} ship {ship.Id}: {target.Id} cleared {(accepted ? "accepted ship target" : "command target")} ({(accepted ? "c_cmdAccepted" : "c_cmdCurrent")}, objectID=NA sentinel, WinTrek.cpp:8834-8836)");
            }
            else if (req.ObjectType == IgcObjectType.Ship)
            {
                // Resolve the command target across sides within this mission.
                // found null (unknown id) =
                // native GetModel NULL -> SetCommand(command, NULL, cid) clears.
                var found = FindShipAnySide(req.ObjectID, mission);
                // Command selection/acceptance does not require current detection:
                // native resolves the mission model and calls SetCommand directly
                // (FedSrv/FedSrv.CPP:7335-7339; Igc/shipIGC.h:725-790).
                // AcceptQueuedCommand likewise permits a known target before it
                // is visible (WinTrek/WinTrek.cpp:10650-10696). CanSee belongs to
                // missile lock/firing (Igc/magazineigc.cpp:127-132), which recheck
                // visibility independently. Keep the player-slot ownership gate above.
                if (accepted)
                {
                    target.AcceptedShipTarget = found;
                    target.AcceptedShipCommandId = req.CommandId;
                }
                else
                {
                    target.CommandTarget = found;
                }
                string foundDesc = found is null
                    ? $"ship {req.ObjectID} not found — {(accepted ? "accepted ship target" : "command target")} cleared (FedSrv.CPP:7336-7338)"
                    : $"target ship {found.Id} (side {found.SideId}, {(found.IsNpc ? "NPC" : "peer")}{(accepted ? $") cmdID={req.CommandId}" : ")")} — {slot} set (FedSrv.CPP:7335-7339)";
                if (ConsoleLog.Verbose) ConsoleLog.Write($"[orderchange] {from} ship {ship.Id}: {target.Id} {(accepted ? "c_cmdAccepted" : "c_cmdCurrent")} -> {foundDesc}");
            }
            else
            {
                // Non-ship objectType (station/etc.): both slots are Model?-
                // typed and cannot hold it — logged, slot unchanged (documented
                // scope boundary).
                if (ConsoleLog.Verbose) ConsoleLog.Write($"[orderchange] {from} ship {ship.Id}: {target.Id} {(accepted ? "c_cmdAccepted" : "c_cmdCurrent")} objectType {req.ObjectType} is not a ship — non-ship target out of scope for {slot} (logged, no-op)");
            }
            return;
        }

        // Every other combination is EXPLICITLY OUT OF SCOPE (P148/P150/P153/P154):
        // a non-NPC target with a command other than c_cmdCurrent/c_cmdAccepted,
        // or any objectType/slot pairing not covered above. Log and no-op; don't
        // guess at behavior for the deferred branches.
        if (ConsoleLog.Verbose) ConsoleLog.Write($"[orderchange] {from} ship {ship.Id}: ORDER_CHANGE target {target.Id} " +
            $"IsNpc={target.IsNpc} StationId={target.StationId} commandID={req.CommandId} command={req.Command} — out-of-scope branch, no-op (P148/P150/P153/P154)");
    }

    /// <summary>
    /// P424 — clear a real player's pending queued order (the <c>c_cmdQueued</c>
    /// slot) and notify that player's own client. Shared by REJECT, ACCEPT
    /// (post-promotion), and the 15&nbsp;s auto-reject pass — every path that
    /// ends a pending queued order funnels through here so the owner's client
    /// always learns the slot is empty (a <c>QUEUED_COMMAND_UPDATE(210)</c>
    /// carrying <see cref="OrderChange.CidNone"/>). The DataStationIGC-typed
    /// <see cref="Ship.QueuedTarget"/> is <b>not</b> cleared here — it belongs to
    /// the NPC return-to-base flow (P150) and is managed separately there; only
    /// the real-player <see cref="Ship.QueuedCommandId"/> /
    /// <see cref="Ship.QueuedShipTarget"/> / <see cref="Ship.QueuedAt"/> are
    /// this feature's state.</summary>
    private void ClearQueuedCommand(Ship target)
    {
        target.QueuedCommandId = OrderChange.CidNone;
        target.QueuedShipTarget = null;
        target.QueuedAt = 0;

        if (ConnOf(target) is { } conn)
        {
            var upd = new QueuedCommandUpdate
            {
                CommandId = OrderChange.CidNone,
                TargetType = (IgcObjectType)(-1),   // OT_invalid
                TargetId = -1,                       // NA
            };
            SendPeer(conn, FedPacket.Pack(new[] { upd.Encode() }));
        }
    }

    /// <summary>
    /// P424 → <b>promoted (P457)</b> — queue a real command on a real player's
    /// ship (the <c>c_cmdQueued</c> slot), arming the 15&nbsp;s auto-reject timer
    /// and notifying that player's own client via <c>QUEUED_COMMAND_UPDATE(210)</c>.
    /// <para>P424 originally shipped this as the <b>test/demo-only</b> writer
    /// (see P424_ACCEPT_REJECT_QUEUED_ORDERS.md §3). P457 makes it the <b>real</b>
    /// writer: the quick-chat order-issuing flow (native's "give an order to
    /// another player") now calls it from the server chat relay
    /// (<see cref="QueueOrdersFromRelayedChat"/>), so the two decades-old
    /// behaviours meet — a commander quick-chat finally orders a teammate.</para>
    /// <para><b>Guards retained.</b> NPC ships and non-live-peer ships are
    /// refused (logged, not queued) — real-player only; a <c>commandId</c> of
    /// <see cref="OrderChange.CidNone"/> is refused (not a real order).</para>
    /// <para><b>Target.</b> <paramref name="targetModel"/> is the real in-mission
    /// model the order is aimed at (promotion moves it into the accepted slot),
    /// or <c>null</c> for a targetless order. A non-Model target (a station /
    /// asteroid / warp, which the port cannot store as
    /// <see cref="Ship.QueuedShipTarget"/>) is carried by the explicit
    /// <paramref name="wireOtTarget"/>/<paramref name="wireOidTarget"/> so the
    /// client's HUD still names the right object even though the accepted slot
    /// has no model for it.</param>
    /// <param name="target">The real player ship receiving the order (must be a
    /// live peer, else the queue is refused with a log — no NPC queued orders
    /// ride this writer).</param>
    /// <param name="commandId">The queued order's action (<c>CommandID</c>), e.g.
    /// <see cref="OrderChange.CidGoto"/>. Must not be <see cref="OrderChange.CidNone"/>.</param>
    /// <param name="targetModel">An optional real in-mission model the order is
    /// aimed at (the target that promotion moves into the accepted slot);
    /// <c>null</c> for a targetless order.</param>
    /// <param name="wireOtTarget">The target's wire object type, used when
    /// <paramref name="targetModel"/> is null (a non-Model target).</param>
    /// <param name="wireOidTarget">The target's wire object id, used when
    /// <paramref name="targetModel"/> is null (a non-Model target).</param>
    public void QueueCommandForPlayer(Ship target, short commandId, Model? targetModel = null,
        IgcObjectType wireOtTarget = (IgcObjectType)(-1), short wireOidTarget = -1)
    {
        if (target.IsNpc || ConnOf(target) is not { } conn)
        {
            Console.WriteLine($"[queuedcmd] queue refused: ship {target.Id} is {(target.IsNpc ? "an NPC" : "not a live peer")} — only real-player queued orders are supported (P424/P457)");
            return;
        }
        if (commandId == OrderChange.CidNone)
        {
            Console.WriteLine($"[queuedcmd] queue refused: commandID=c_cidNone is not a real order (P424/P457)");
            return;
        }

        target.QueuedCommandId = commandId;
        target.QueuedShipTarget = targetModel;
        target.QueuedAt = target.Mission?.LastUpdate ?? 0;

        // Derive the target's wire object type/id. A real Model target (a Ship)
        // resolves from the model; a non-Model target (station/asteroid/warp)
        // rides the explicit wire target the real issuing flow supplied.
        IgcObjectType targetType;
        short targetId;
        if (targetModel is not null)
        {
            targetType = targetModel is Ship ? IgcObjectType.Ship : (IgcObjectType)(-1);
            targetId = targetModel.Id;
        }
        else
        {
            targetType = wireOtTarget;
            targetId = wireOidTarget;
        }
        var upd = new QueuedCommandUpdate
        {
            CommandId = commandId,
            TargetType = targetType,
            TargetId = targetId,
        };
        SendPeer(conn, FedPacket.Pack(new[] { upd.Encode() }));
        Console.WriteLine($"[queuedcmd] queued order commandID={commandId} on ship {target.Id} (target {(targetModel is null ? (targetId < 0 ? "none" : $"wire #{targetId} {targetType}") : $"#{targetModel.Id} {targetType}")}), 15s auto-reject armed @{target.QueuedAt:0.###}");
    }

    /// <summary>
    /// P457 — the real issuing flow, server side: when a relayed command chat
    /// (a legal <c>CommandID != c_cidNone</c>) with a non-Everyone target is
    /// delivered, queue a real queued order on each <b>same-side real-player</b>
    /// recipient the relay actually delivers to, excluding the sender, via the
    /// P424 path (<see cref="QueueCommandForPlayer"/>). NPC recipients are
    /// untouched here (P469). This is the server-authoritative counterpart to
    /// native's client-side <c>SetQueuedCommand</c> (<c>WinTrek.cpp:4860-4890</c>):
    /// this port's queued-order slot lives server-side (P424), so the server
    /// queues on relay instead of each receiving client queueing locally — keep
    /// that divergence and see the P457 report.
    /// </summary>
    private void QueueOrdersFromRelayedChat(ConnectionId from, Ship sender, ChatMessage chat, ConnectionId[] targets)
    {
        short cid = chat.CommandID;
        // Native's validity range (FedSrv.CPP:3364-3368): legal iff
        // c_cidNone <= commandID < c_cidMax; an order chat is a legal id != c_cidNone.
        bool isOrder = cid >= OrderChange.CidNone && cid < OrderChange.CidMax && cid != OrderChange.CidNone;
        if (!isOrder) return;                                       // plain / voice-only chat
        if (chat.ChatTarget == (int)ChatTarget.Everyone) return;    // command chat on Everyone is not forwarded (FedSrv.CPP:3385-3391)

        var (found, model, ot, oid) = ResolveChatOrderTarget(sender.Mission, (IgcObjectType)chat.OtTarget, chat.OidTarget);
        if (!found)
        {
            Console.WriteLine($"[queuedcmd] order chat from ship {sender.Id} cid={cid}: target (ot={chat.OtTarget}, oid={chat.OidTarget}) not resolvable server-side — order dropped (native pmodel==NULL -> non-command)");
            return;
        }

        foreach (var t in targets)
        {
            if (t == from) continue;                                // never queue on the sender
            if (!_peerShips.TryGetValue(t, out var recipLive)) continue;
            var rs = recipLive.Ship;
            if (rs.IsNpc) continue;                                 // NPC recipients -> P469
            if (rs.SideId != sender.SideId) continue;               // same-side only
            QueueCommandForPlayer(rs, cid, model, ot, oid);
        }
    }

    /// <summary>P457 — resolve a command chat's wire target
    /// (<c>otTarget</c>/<c>oidTarget</c>, chosen by the sender's client) to the
    /// server-side model for the queued order. <c>found=false</c> when the target
    /// cannot be resolved (the order is dropped, matching native's
    /// <c>pmodel == NULL</c> → non-command rule). A Model target (a Ship) returns
    /// the live model; a non-Model target (station / asteroid / warp, which the
    /// port cannot store as <see cref="Ship.QueuedShipTarget"/>) returns the wire
    /// target so the client's HUD still names it.</summary>
    private (bool Found, Model? Model, IgcObjectType Ot, short Oid) ResolveChatOrderTarget(Mission? mission, IgcObjectType ot, short oid)
    {
        switch (ot)
        {
            case IgcObjectType.Ship:
                var s = FindShipAnySide(oid, mission);
                return s is not null ? (true, s, IgcObjectType.Ship, oid) : (false, null, (IgcObjectType)(-1), -1);
            case IgcObjectType.Station:
                if (mission is not null)
                    foreach (var sec in mission.Sectors)
                        foreach (var st in sec.Stations)
                            if (st.StationId == oid)
                                return (true, null, IgcObjectType.Station, oid);
                return (false, null, (IgcObjectType)(-1), -1);
            case IgcObjectType.Asteroid:
                var a = FindAsteroidById(oid, mission);
                return a is not null ? (true, null, IgcObjectType.Asteroid, oid) : (false, null, (IgcObjectType)(-1), -1);
            case IgcObjectType.Warp:
                if (mission is not null)
                    foreach (var sec in mission.Sectors)
                        foreach (var w in sec.Warps)
                            if (w.Warp.WarpId == oid)
                                return (true, null, IgcObjectType.Warp, oid);
                return (false, null, (IgcObjectType)(-1), -1);
            default:
                return (false, null, (IgcObjectType)(-1), -1);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // P469 — quick-chat orders to drones (native CommandToDrone)
    // ═══════════════════════════════════════════════════════════════════════
    // Native: a player selects a drone and sends a quick-chat command as an
    // Individual chat; ForwardChatMessage's CHAT_INDIVIDUAL branch detects a
    // non-player recipient and hands it to CommandToDrone (FedSrv.CPP:3557-3573
    // → 3175-3325). This port mirrors that: an Individual-family chat naming a
    // same-side NPC with a legal CommandID makes the drone obey immediately
    // (the anti-thesis of the P424/P457 accept-or-reject queued flow, which is
    // for real players who get free will).

    /// <summary>
    /// P469 — entry from the chat relay (Server.Dispatch.cs): if an
    /// Individual-family chat (CHAT_INDIVIDUAL / NOFILTER / Group→Individual,
    /// FedSrv.CPP:3531-3556) carries a legal <c>CommandID</c> and its named
    /// recipient is a <b>same-side NPC</b>, route it to
    /// <see cref="CommandToDrone"/>. A real-player recipient is untouched — the
    /// P457 <see cref="QueueOrdersFromRelayedChat"/> path owns those. Runs on
    /// the sim thread (inside the relay handler), so mutating NPC state is safe.
    /// </summary>
    private void TryCommandToDrone(ConnectionId from, Ship sender, ChatMessage chat)
    {
        // Individual-family only. IndividualEcho is server-generated (skip it);
        // Everyone/Team/Leaders/Wing carry no per-drone recipient to obey.
        int ct = chat.ChatTarget;
        bool individual = ct == (int)ChatTarget.Individual
                       || ct == (int)ChatTarget.IndividualNoFilter
                       || ct == (int)ChatTarget.Group
                       || ct == (int)ChatTarget.GroupNoEcho;
        if (!individual) return;

        // Legal CommandID range (FedSrv.CPP:3364-3368): c_cidNone <= cid < c_cidMax.
        short cid = chat.CommandID;
        if (cid < OrderChange.CidNone || cid >= OrderChange.CidMax)
        {
            ConsoleLog.Write($"[drone] ship {sender.Id}: chat commandID={cid} outside legal range [{OrderChange.CidNone},{OrderChange.CidMax}) — ignored (native range gate, FedSrv.CPP:3364-3368)");
            return;
        }

        // Resolve the named recipient (CFSShip::GetShipFromID, FedSrv.CPP:3541);
        // a ship that went away is ignored exactly as native does (:3542).
        var recipient = FindShipAnySide(chat.OidRecipient, sender.Mission);
        if (recipient is null)
        {
            ConsoleLog.Write($"[drone] ship {sender.Id}: Individual chat to ship {chat.OidRecipient} not found — ignored (may have gone away, FedSrv.CPP:3541-3542)");
            return;
        }
        if (!recipient.IsNpc)
            return;   // real-player recipient → the P457 queued-order relay owns it

        // Same-side + non-none gate (FedSrv.CPP:3190-3191).
        if (cid == OrderChange.CidNone || recipient.SideId != sender.SideId)
        {
            ConsoleLog.Write($"[drone] ship {sender.Id}: {(cid == OrderChange.CidNone ? "voice-only (c_cidNone)" : $"order cid={cid} to enemy-side NPC {recipient.Id}")} — ignored (native same-side + non-none gate, FedSrv.CPP:3190-3191)");
            return;
        }

        CommandToDrone(from, sender, recipient, chat);
    }

    /// <summary>
    /// P469 — the port of native <c>CommandToDrone</c> (FedSrv.CPP:3175-3325):
    /// resolve the order's target, run the native <c>LegalCommand</c> gate, map
    /// the command onto an existing port sim arm, and on success reply
    /// <c>"Affirmative."</c> to the commanding player. An illegal command is a
    /// logged no-op (native sets no chat at all — the "Negative." is commented
    /// out at FedSrv.CPP:3286-3292).
    /// </summary>
    private void CommandToDrone(ConnectionId from, Ship sender, Ship drone, ChatMessage chat)
    {
        short cid = chat.CommandID;

        // FedSrv.CPP:3193: player orders defer runaway checks for 120 seconds.
        drone.SetRunawayCheckCooldown(120f);

        // Resolve the order's target model (FedSrv.CPP:3197-3260). Buoy-from-pdb
        // construction is out of scope: the P403 ChatBuoy path already owns
        // buoy orders, so the drone-chat buoy branch is not reproduced here.
        var (found, model, ot, oid) = ResolveChatOrderTarget(sender.Mission, (IgcObjectType)chat.OtTarget, chat.OidTarget);

        // c_cidDefault → a concrete command from the target (FedSrv.CPP:3247-3250,
        // IshipIGC::GetDefaultOrder, shipIGC.h:2071-2160).
        if (cid == OrderChange.CidDefault && model is not null)
            cid = GetDefaultOrder(drone, model, ot, oid);

        // LegalCommand gate (FedSrv.CPP:3252; shipIGC.h:1604-1721). `found` — the
        // port's stand-in for native's non-NULL `pmodel`: a resolved ship/station/
        // asteroid/warp id is a VALID target even though this port has no Model
        // object for the non-Ship kinds (native keeps an ImodelIGC for every kind,
        // so its own `pmodel == NULL` gate is never tripped by a real target).
        if (!LegalCommand(drone, cid, found, model, ot, oid))
        {
            ConsoleLog.Write($"[drone] ship {sender.Id}: order cid={cid} to NPC {drone.Id} (pilotType {drone.PilotType}) is ILLEGAL — refused, no effect (native LegalCommand, shipIGC.h:1604-1721)");
            return;
        }

        // SetStayDocked(false) on any accepted command (FedSrv.CPP:3254) — a
        // commanded drone is released from a previous stay-docked latch.
        drone.StayDocked = false;

        // Map onto an existing sim arm (Goto/Attack/Defend/Mine/Build/DoNothing).
        if (!ApplyDroneCommand(from, sender, drone, cid, model, ot, oid))
        {
            ConsoleLog.Write($"[drone] ship {sender.Id}: order cid={cid} to NPC {drone.Id} legal but no matching port sim arm — not executed (unsupported; see P469 report)");
            return;
        }

        drone.ResetRunawayForNewOrder(); // SetCommand resets even an identical order (shipIGC.h:725-764)

        // Reply "Affirmative." to the commander (FedSrv.CPP:3256-3257).
        SendDroneAffirmative(from, sender, drone);

        // Native echoes the accepted command to the command wing and Charlie/
        // Delta/Echo wings (FedSrv.CPP:3563-3571). Named gap: this port has no
        // command-wing routing worth replicating here (see the P469 report).
    }

    /// <summary>
    /// P469 — the <c>IshipIGC::LegalCommand(cid, pmodel)</c> gate
    /// (shipIGC.h:1604-1721) — the per-pilot-type legality the native drone
    /// command actually enforces. Subsumes the one-arg per-pilot-type command
    /// table (shipIGC.h:1559-1602): each case below restricts by pilot type
    /// exactly as that table does (Attack/Wingman-only, Mine/Miner-only,
    /// Build/Layer-or-Builder-only, DoNothing always-legal), so a carrier told
    /// to attack and a wingman told to mine both come back false, matching the
    /// table. The capabilities this port approximates natively are the asteroid
    /// mining ability (<see cref="Asteroid.CanMineHe3"/>) and the configured
    /// station type's build AABM (<see cref="Ship.BuilderStationType"/>).
    /// </summary>
    private bool LegalCommand(Ship drone, short cid, bool targetValid, Model? model, IgcObjectType ot, short oid)
    {
        // Target-invalid → only DoNothing is legal (shipIGC.h:1608-1617).
        // `targetValid` is `found` from ResolveChatOrderTarget: a resolved
        // non-Ship target (warp/asteroid/station/buoy) is a VALID target even
        // though this port has no Model object for it (native keeps an
        // ImodelIGC for every kind, so its pmodel==NULL gate never fires on a
        // real warp/asteroid/station — P591 fixes the port to mirror that).
        bool targetInvalid =
            !targetValid ||
            ReferenceEquals(model, drone) ||
            (model is Ship ms && ms.Mission != drone.Mission);
        if (targetInvalid)
            return cid == OrderChange.CidDoNothing;

        byte pt = drone.PilotType;
        bool friendly = IsFriendlyTarget(drone, model, ot, oid);   // #ALLY shipIGC.h:1621

        switch (cid)
        {
            case OrderChange.CidNone:
                return false;                                    // shipIGC.h:1625-1629
            case OrderChange.CidDefault:
            case OrderChange.CidGoto:
                // (pilotType >= Player) || type != station || friendly (1631-1636)
                if (pt >= Ship.PilotTypePlayer) return true;
                return ot != IgcObjectType.Station || friendly;
            case OrderChange.CidAttack:
                // (Wingman||Player) && !warp && !treasure && !friendly (1638-1647)
                if (!(pt == Ship.PilotTypeWingman || pt >= Ship.PilotTypePlayer)) return false;
                if (ot == IgcObjectType.Warp || ot == IgcObjectType.Treasure) return false;
                if (friendly) return false;
                if (ot == IgcObjectType.Ship && model is Ship es)
                {
                    if (es.PilotType == Ship.PilotTypeMiner) return true;
                    if (es.Sector is not null) return true;      // GetCluster() != NULL → fightable
                    return false;
                }
                return true;
            case OrderChange.CidDefend:
                // (Wingman||Player) (1655-1659)
                return pt == Ship.PilotTypeWingman || pt >= Ship.PilotTypePlayer;
            case OrderChange.CidPickup:
                // (Wingman||Player) && !parented && lifepod/rescue combo (1661-1678)
                if (!(pt == Ship.PilotTypeWingman || pt >= Ship.PilotTypePlayer)) return false;
                if (drone.ParentShip is not null) return false;
                if (ot == IgcObjectType.Ship && model is Ship ps)
                {
                    bool targetPod = ps.HullType.HasCapability(DataHullTypeIGC.HabmLifepod);
                    bool myRescue = drone.HullType.HasCapability(DataHullTypeIGC.HabmRescue);
                    bool targetRescue = ps.HullType.HasCapability(DataHullTypeIGC.HabmRescue);
                    bool myPod = drone.HullType.HasCapability(DataHullTypeIGC.HabmLifepod);
                    return (targetPod && myRescue) || (targetRescue && myPod);
                }
                return ot != IgcObjectType.Treasure;
            case OrderChange.CidRepair:
                // (Wingman||Player) && (ship||station) && friendly (1680-1686)
                if (!(pt == Ship.PilotTypeWingman || pt >= Ship.PilotTypePlayer)) return false;
                if (ot != IgcObjectType.Ship && ot != IgcObjectType.Station) return false;
                return friendly;
            case OrderChange.CidJoin:
                return ot == IgcObjectType.Ship;                 // shipIGC.h:1688-1693
            case OrderChange.CidBuild:
                if (pt == Ship.PilotTypeLayer)
                    return ot == IgcObjectType.Buoy || ot == IgcObjectType.Warp;   // 1697-1698
                if (pt == Ship.PilotTypeBuilder)
                {
                    if (ot != IgcObjectType.Asteroid) return false;                 // 1701
                    var b = ResolveAsteroidForDrone(drone.Mission, ot, oid, model);
                    return b is not null && drone.BuilderStationType is { } bst
                        && b.Data.HasCapability(bst.AabmBuild);                      // 1702
                }
                return false;
            case OrderChange.CidMine:
                if (pt != Ship.PilotTypeMiner) return false;      // 1711
                if (ot != IgcObjectType.Asteroid) return false;   // 1712
                var m = ResolveAsteroidForDrone(drone.Mission, ot, oid, model);
                return m is not null && m.CanMineHe3;             // 1713 HasCapability(m_abmOrders)
            default:
                return false;
        }
    }

    /// <summary>
    /// P469 — native's bFriendly test (<c>pmodel-&gt;GetSide() == GetSide()
    /// || AlliedSides(...)</c>, shipIGC.h:1621). Same-side (or allied) → true;
    /// an asteroid/warp/buoy carries no side → false (not "friendly").
    /// </summary>
    private bool IsFriendlyTarget(Ship drone, Model? model, IgcObjectType ot, short oid)
    {
        short? targetSide = model is Ship s ? s.SideId : null;
        if (targetSide is null && ot == IgcObjectType.Station)
        {
            if (drone.Mission is not null)
                foreach (var sec in drone.Mission.Sectors)
                    foreach (var st in sec.StationRuntimes)
                        if (st.StationId == oid) { targetSide = st.SideId; break; }
        }
        if (targetSide is null) return false;
        if (drone.Mission is not { } mission) return drone.SideId == targetSide;   // no mission → same-side only
        return AlliedSides(mission, drone.SideId, targetSide);
    }

    /// <summary>
    /// P469 — <c>IshipIGC::GetDefaultOrder(pmodel)</c> (shipIGC.h:2071-2160):
    /// turn a <c>c_cidDefault</c> into a concrete command from the target. Base
    /// case = <see cref="OrderChange.CidGoto"/>; a Miner/Builder on a
    /// capability-matching asteroid resolves Mine/Build; a Layer on a buoy/warp
    /// resolves Build; a Wingman on a friendly target resolves Defend (the
    /// lifepod-pickup / carrier-goto refinements of :2106-2140 are subsumed —
    /// see the P469 report).
    /// </summary>
    private short GetDefaultOrder(Ship drone, Model? model, IgcObjectType ot, short oid)
    {
        short cid = OrderChange.CidGoto;
        switch (drone.PilotType)
        {
            case Ship.PilotTypeMiner:
            case Ship.PilotTypeBuilder:
                var rock = ResolveAsteroidForDrone(drone.Mission, ot, oid, model);
                if (ot == IgcObjectType.Asteroid && rock is not null)
                {
                    if (drone.PilotType == Ship.PilotTypeMiner && rock.CanMineHe3)
                        cid = OrderChange.CidMine;
                    else if (drone.PilotType == Ship.PilotTypeBuilder
                             && drone.BuilderStationType is { } bst
                             && rock.Data.HasCapability(bst.AabmBuild))
                        cid = OrderChange.CidBuild;
                }
                break;
            case Ship.PilotTypeLayer:
                if (ot == IgcObjectType.Buoy || ot == IgcObjectType.Warp)
                    cid = OrderChange.CidBuild;
                break;
            case Ship.PilotTypeWingman:
                if (IsFriendlyTarget(drone, model, ot, oid))
                    cid = OrderChange.CidDefend;
                break;
        }
        return cid;
    }

    /// <summary>
    /// P469 — map a legal command onto an existing port sim arm. Returns true
    /// only when the order was actually executed (an arm was set). The
    /// <see cref="OrderChange.CidJoin"/>/Pickup/Repair/Capture commands have no
    /// matching arm and fall through to false — logged as unsupported (the P469
    /// report lists them).
    /// </summary>
    private bool ApplyDroneCommand(ConnectionId from, Ship sender, Ship drone, short cid, Model? model, IgcObjectType ot, short oid)
    {
        switch (cid)
        {
            case OrderChange.CidGoto:
            {
                if (ResolveDroneGoto(drone.Mission, model, ot, oid) is not { } g) return false;
                drone.SetGoto(g);                                   // P403 goto
                ConsoleLog.Write($"[drone] ship {sender.Id}: ordered NPC {drone.Id} (pilotType {drone.PilotType}) to GOTO ({g.Kind} cluster {g.ClusterId}) — P403 goto issued");
                return true;
            }
            case OrderChange.CidAttack:
                if (model is Ship enemy)
                {
                    drone.AttackTarget = enemy;                     // P162 wingman attack
                    ConsoleLog.Write($"[drone] ship {sender.Id}: ordered NPC {drone.Id} (wingman) to ATTACK ship {enemy.Id} (side {enemy.SideId})");
                    return true;
                }
                return false;
            case OrderChange.CidDefend:
                if (model is Ship charge)
                {
                    drone.DefendTarget = charge;                    // P166 wingman defend
                    ConsoleLog.Write($"[drone] ship {sender.Id}: ordered NPC {drone.Id} (wingman) to DEFEND ship {charge.Id} (side {charge.SideId})");
                    return true;
                }
                return false;
            case OrderChange.CidMine:
            {
                var rock = ResolveAsteroidForDrone(drone.Mission, ot, oid, model);
                if (rock is null) return false;
                drone.MiningAsteroidTarget = rock;                  // P156 miner target
                ConsoleLog.Write($"[drone] ship {sender.Id}: ordered NPC {drone.Id} (miner) to MINE asteroid {rock.Data.AsteroidId}");
                return true;
            }
            case OrderChange.CidBuild:
                // P591 — a LAYER drone's c_cidBuild is a LAY order (native
                // c_ptLayer c_cidBuild, shipIGC.cpp:2810-2828 / 4050-4110): fly
                // to the target (a warp/buoy) and LayExpendable. LegalCommand
                // already restricted the Layer's Build to Buoy/Warp targets
                // (shipIGC.h:1697-1698). Until P591 this fell through the
                // Builder-only asteroid arm below and was logged as "no matching
                // port sim arm" — the P161 deployment gap.
                if (drone.PilotType == Ship.PilotTypeLayer)
                    return ArmLayerBuild(drone, ot, oid);
                // The P425 direct-NPC-build path: shared legality + construction.
                if (ot != IgcObjectType.Asteroid) return false;
                return StartBuildIfLegal(from, sender.Id, drone, new OrderChange
                {
                    ShipID = drone.Id,
                    ObjectID = oid,
                    ObjectType = IgcObjectType.Asteroid,
                    CommandId = OrderChange.CidBuild,
                    Command = OrderChange.CmdAccepted,
                });
            case OrderChange.CidDoNothing:
                drone.StayDocked = true;                            // P148 stay/hold
                ConsoleLog.Write($"[drone] ship {sender.Id}: ordered NPC {drone.Id} to DO NOTHING (stay/hold — StayDocked=true, P148)");
                return true;
            case OrderChange.CidPickup:
                // P590 — a pod orders a rescue-capable NPC to pick it up. LegalCommand
                // already validated the (targetPod && myRescue) combos; here we arm the
                // NPC's pickup-diversion goal. The diverter steers to contact and the
                // existing ResolveRescueEvents pass performs the actual rescue.
                if (model is not Ship podShip) return false;
                drone.PickupTarget = podShip;
                ConsoleLog.Write($"[pickup] ship {sender.Id}: ordered NPC {drone.Id} (rescue-capable) to PICK UP pod {podShip.Id} (side {podShip.SideId}) — diversion armed");
                return true;
            default:
                return false;   // Join/Pickup/Repair/Capture — no matching arm
        }
    }

    /// <summary>
    /// P591 — arm a LAYER drone's build order (native <c>c_ptLayer c_cidBuild</c>,
    /// <c>shipIGC.cpp:4050-4110</c> / <c>2810-2828</c>): resolve the ordered
    /// target to a lay point and set the drone's
    /// <see cref="Ship.LayerBuildOrder"/>, which the drone's autopilot then
    /// flies to and lays at on arrival. Returns <c>true</c> only when armed
    /// (the caller then replies "Affirmative.").
    /// <list type="bullet">
    /// <item><b>Warp target</b> (the MCP's <c>warp:&lt;id&gt;</c>): compute the
    /// native OT_warp waypoint-buoy position — <c>db.position = p −
    /// orientation.GetBackward() · pdmt->radius</c> (<c>shipIGC.cpp:4061-4080</c>),
    /// which with <c>GetBackward() = −GetForward()</c> equals <c>p +
    /// forward·radius</c>: the lay point sits one radius in front of the aleph,
    /// into the rendezvous sector, so the arriving enemy fleet flies into the
    /// field. The port uses the laid mine's <c>MineRadius</c> as the standoff
    /// (the base model <c>radius</c> field native reads decodes as a corrupt
    /// value for the real mine type — see the P591 report), else <c>0</c>.</item>
    /// <item><b>Buoy target</b>: <b>unsupported</b> — this port has no sim buoy
    /// object (buoys are wire-only, the P403 ChatBuoy path; the drone's internal
    /// waypoint buoy is created from the warp target above). A direct buoy order
    /// returns <c>false</c> and is logged as unexecuted, matching the existing
    /// CommandToDrone buoy limitation (<c>Server.ShipCommands.cs:1648</c>).</item>
    /// </list>
    /// </summary>
    private bool ArmLayerBuild(Ship drone, IgcObjectType ot, short oid)
    {
        var mission = drone.Mission;
        if (mission is null || drone.Sector is null) return false;

        if (ot != IgcObjectType.Warp)
            return false;   // buoy/probe-position targets: no sim object to resolve (see above)

        ResolvedWarp? w = null;
        foreach (var sec in mission.Sectors)
            foreach (var wr in sec.Warps)
                if (wr.Warp.WarpId == oid) { w = wr; break; }
        if (w is null)
        {
            ConsoleLog.Write($"[layer] ship {drone.Id}: build order to warp {oid} not found — not armed");
            return false;
        }
        var p = w.Warp.Position;
        if (p is not { Length: 3 })
        {
            ConsoleLog.Write($"[layer] ship {drone.Id}: warp {oid} has no position — not armed");
            return false;
        }
        var pos = new Vector3(p[0], p[1], p[2]);
        var f = w.Warp.Forward;
        Vector3 forward = f is { Length: 3 } ? new Vector3(f[0], f[1], f[2]) : new Vector3(0f, 0f, 1f);
        if (forward.LengthSquared() < 1e-6f) forward = new Vector3(0f, 0f, 1f);
        // P591 — the lay point must CLEAR the warp's transit volume, or the drone
        // approaching its lay point flies into the aleph and warps through it
        // forever (measured live: the layer ping-ponged Omega(1053)<->Alderan(1052)
        // via the very warp it was ordered to lay at, never reaching the field).
        // The transit fires at warp.Radius + shipRadius (Sector.cs:3249-3251,
        // shipIGC.cpp:629-636), so the field is placed a full mine-radius BEYOND
        // that — the drone then lays before it can enter the transit volume (its
        // 2×AutopilotArrivalRadius reach keeps it well clear), and the field still
        // sits "at the aleph" inside the mine's blast radius of the transit path.
        float shipRadius = 0.5f * drone.HullType.Length;
        float transitRadius = w.Warp.Radius + shipRadius;
        float offset = 0f;
        if (drone.LayerLaidExpendableTypeId >= 0
            && mission.Core.MineTypes.TryGetValue(drone.LayerLaidExpendableTypeId, out var mt))
            offset = mt.MineRadius;   // the native OT_warp standoff (shipIGC.cpp:4061-4080)
        float standoff = transitRadius + offset;
        // Place on the warp's BACKWARD side — into the sector the warp's own
        // cluster opens into (away from the warp's forward/destination) — so a
        // drone arriving from the sector interior reaches the lay point without
        // crossing the warp's transit volume (the measured ping-pong fix).
        pos -= forward * standoff;

        // Reach = standoff + margin: a drone that emerges AT this aleph (its
        // transit entry point, i.e. distance `standoff` from the lay point) is
        // already within reach and lays immediately instead of warping through
        // the very aleph it is laying at.
        drone.SetLayerBuild(new Ship.LayerBuildGoal(w.Warp.ClusterId, pos, standoff + 80f));
        ConsoleLog.Write($"[layer] ship {drone.Id} (pilotType {drone.PilotType}) ordered BUILD at warp {oid} → lay point sector {w.Warp.ClusterId} ({pos.X:0},{pos.Y:0},{pos.Z:0}), lays expendable {drone.LayerLaidExpendableTypeId} (native OT_warp buoy, shipIGC.cpp:4050-4080)");
        return true;
    }

    /// <summary>P469 — resolve the asteroid named by a Mine/Build order's wire
    /// target (a <c>Model</c> target is never an Asteroid — non-Ship targets
    /// resolve to <c>null</c> model, so the wire <c>ot</c>/<c>oid</c> name it).</summary>
    private Asteroid? ResolveAsteroidForDrone(Mission? mission, IgcObjectType ot, short oid, Model? model)
    {
        if (ot != IgcObjectType.Asteroid) return null;
        return FindAsteroidById(oid, mission);
    }

    /// <summary>P469 — build a <see cref="GotoOrder"/> for the order's target so
    /// the NPC flies to it via the P403 navigation resolver. Ship targets use
    /// their live sector+position; station/asteroid/warp targets resolve their
    /// containing sector + a representative position. <c>null</c> when the
    /// target cannot be located (the order is not executed).</summary>
    private GotoOrder? ResolveDroneGoto(Mission? mission, Model? model, IgcObjectType ot, short oid)
    {
        if (model is Ship s)
            return s.Sector is { } ss ? new GotoOrder(GotoKind.Waypoint, ss.Id, s.Position) : null;
        if (mission is null) return null;
        foreach (var sec in mission.Sectors)
        {
            switch (ot)
            {
                case IgcObjectType.Station:
                    foreach (var st in sec.StationRuntimes)
                        if (st.StationId == oid)
                            return new GotoOrder(GotoKind.Waypoint, sec.Id, st.Center);
                    break;
                case IgcObjectType.Asteroid:
                    foreach (var rock in sec.Mineable)
                        if (rock.Data.AsteroidId == oid && rock.Data.Position is { Length: 3 } rp)
                            return new GotoOrder(GotoKind.Waypoint, sec.Id, new Vector3(rp[0], rp[1], rp[2]));
                    break;
                case IgcObjectType.Warp:
                    foreach (var w in sec.Warps)
                        if (w.Warp.WarpId == oid && w.Warp.Position is { Length: 3 } p)
                            return new GotoOrder(GotoKind.Waypoint, sec.Id, new Vector3(p[0], p[1], p[2]));
                    break;
            }
        }
        return null;
    }

    /// <summary>P469 — reply "Affirmative." to the commanding player as an
    /// Individual chat FROM the drone (native SendChat(pshipTo, CHAT_INDIVIDUAL,
    /// ...), FedSrv.CPP:3256-3257). The same private-chat construction
    /// <see cref="RefuseBuild"/> uses, with the drone as speaker.</summary>
    private void SendDroneAffirmative(ConnectionId from, Ship sender, Ship drone)
    {
        var msg = new ChatMessage
        {
            Message = "Affirmative.",
            SidSender = drone.Id,
            ChatTarget = (int)ChatTarget.Individual,
            OidRecipient = sender.Id,
        };
        SendPeer(from, FedPacket.Pack(new[] { msg.Encode() }));
        ConsoleLog.Write($"[drone] ship {sender.Id}: NPC {drone.Id} replied \"Affirmative.\"");
    }

    /// <summary>
    /// P424 — the per-tick auto-reject pass for real players' pending queued
    /// orders: a queued order that has sat neither accepted nor rejected for
    /// <see cref="Ship.QueuedCommandAutoRejectSeconds"/> (15&nbsp;s) of sim time
    /// is cleared — the port of native's client update-loop check
    /// <c>if (m_timeRejectQueuedCommand &lt; time) RejectQueuedCommand(false)</c>
    /// (<c>WinTrek.cpp:7686-7687</c>; the constant at :84). Native's genuine
    /// "ignore it" outcome. Runs server-side because this port's real-player
    /// queued slot lives server-side (the P424 decision). Only real-player ships
    /// are considered (NPC QueuedCommandId is the separate P150 return-to-base
    /// flow and never arms a timer).</summary>
    private void AutoRejectExpiredQueuedCommands(double now)
    {
        foreach (var kv in _peerShips)
        {
            var live = kv.Value;
            var ship = live.Ship;
            if (ship.QueuedCommandId == OrderChange.CidNone)
                continue;
            if (now - ship.QueuedAt >= Ship.QueuedCommandAutoRejectSeconds)
            {
                short expiredId = ship.QueuedCommandId;
                ClearQueuedCommand(ship);
                Console.WriteLine($"[queuedcmd] ship {ship.Id}: queued order commandID={expiredId} AUTO-REJECTED after {Ship.QueuedCommandAutoRejectSeconds}s inaction (native auto-reject, WinTrek.cpp:7686-7687)");
            }
        }
    }

    /// <summary>
    /// P422 — apply a real player's legal Build command (<c>c_cidBuild</c> =
    /// <see cref="OrderChange.CidBuild"/>) against an <c>OT_asteroid</c> target:
    /// the port of native <c>LegalCommand</c>'s c_cidBuild case
    /// (<c>shipIGC.h:1695-1707</c>). Every gate below mirrors a native refusal
    /// exactly — a request that fails any condition is <b>refused</b>
    /// (log-and-return, no construction registers):
    /// <list type="bullet">
    /// <item><b>Self-command only.</b> A player issues Build on their own ship
    /// (this port's P146 self-targeting-only simplification; no team-leader
    /// system to command another ship's construction).</item>
    /// <item><b>Builder-role pilot.</b> Native LegalCommand requires
    /// <c>m_pilotType == c_ptBuilder</c> (<c>shipIGC.h:1699-1702</c>). This
    /// port's player ships carry <c>PilotType=Player</c> (P285), so the
    /// faithful stand-in for "Builder-role" is the Constructor hull capability
    /// the port already uses to identify a Builder — <c>c_habmBuilder</c>
    /// (<c>DataHullTypeIGC.HabmBuilder</c>, igc.h:741). A non-Builder hull is
    /// refused.</item>
    /// <item><b>OT_asteroid target only.</b> Native's c_cidBuild case is legal
    /// only against an <c>OT_asteroid</c> (<c>shipIGC.h:1699-1701</c>); any
    /// other object type is refused.</item>
    /// <item><b>Capability match.</b> Native requires
    /// <c>((IasteroidIGC*)pmodel)-&gt;HasCapability(m_abmOrders)</c>
    /// (<c>shipIGC.h:1702</c>) where <c>m_abmOrders</c> is the configured
    /// station type's build AABM (<c>GetBuildAABM()</c>,
    /// <c>shipIGC.h:2053-2057</c>) — this port's
    /// <see cref="Ship.BuilderStationType"/>. An asteroid lacking the required
    /// capability bit is refused (not every rock is buildable, per the 0x08
    /// <c>c_aabmBuildable</c> bit this port already decodes).</item>
    /// </list>
    /// A legal NPC command retains the build target until the constructor
    /// arrives and stops; then it registers the existing timed construction
    /// effect (native build order arrival, shipIGC.cpp:2810-2827). The ordered
    /// rock is exempt from approach/drilling impact damage, alongside the
    /// mining target exemption. Player Builder hulls keep the established
    /// immediate <see cref="Model.Sector.StartBuildingEffect"/> path.
    /// </summary>
    private void HandlePlayerBuildOrder(ConnectionId from, LiveShip live, Ship target, OrderChange req)
    {
        // Self-command only (native resolves the commander's own ship via
        // pside->GetShip, FedSrv.CPP:7271-7278; this port's P146 precedent).
        if (!ReferenceEquals(target, live.Ship))
        {
            ConsoleLog.Write($"[build] {from}: BUILD for ship {target.Id} != own ship {live.Ship.Id} — refused (self-command only, native pside->GetShip resolves the commander's own ship, FedSrv.CPP:7271-7278)");
            return;
        }
        StartBuildIfLegal(from, live.Ship.Id, target, req);
    }

    /// <summary>
    /// P425 — the direct command of a same-side NPC/drone to BUILD (see the
    /// branch in <see cref="HandleOrderChange"/>). Commanding a drone is
    /// commanding someone OTHER than the sender — native gives drone orders
    /// straight to the drone's accepted slot (OfflineCommandToDrone sets
    /// c_cmdAccepted, clintlib.cpp) — so this deliberately has NO self-command
    /// gate. Everything else is the SAME legality body as the player path
    /// (<see cref="StartBuildIfLegal"/>): a non-Builder drone, a non-asteroid
    /// target, an unbuildable rock, or a drone with no configured station type
    /// is refused exactly as native refuses it, and the refusal is surfaced to
    /// the commanding player's own client.
    /// </summary>
    private void HandleDirectNpcBuildOrder(ConnectionId from, LiveShip live, Ship target, OrderChange req)
    {
        // This direct NPC build path is port-added (native FedSrv.CPP:7281-7322).
        if (live.Ship.Mission is not { } mission || !IsSideLeader(mission, live.Ship)) return;
        StartBuildIfLegal(from, live.Ship.Id, target, req);
    }

    /// <summary>
    /// P422/P425 — the SHARED legality body for a real Build command
    /// (<c>c_cidBuild</c> = <see cref="OrderChange.CidBuild"/>) against an
    /// <c>OT_asteroid</c> target, reached from BOTH the player self-command path
    /// (<see cref="HandlePlayerBuildOrder"/>) and the direct NPC/drone command
    /// path (<see cref="HandleDirectNpcBuildOrder"/>): the port of native
    /// <c>LegalCommand</c>'s c_cidBuild case (<c>shipIGC.h:1695-1707</c>).
    /// Every gate below mirrors a native refusal exactly — a request that fails
    /// any condition is <b>refused</b> (logged AND surfaced to the commanding
    /// player's own client as a private "Negative." chat, matching native's
    /// <c>SendChat(pshipTo, CHAT_INDIVIDUAL, ..., "Negative.")</c> refusal —
    /// clintlib.cpp:2261-2268; no construction registers):
    /// <list type="bullet">
    /// <item><b>Builder-role pilot.</b> Native LegalCommand requires
    /// <c>m_pilotType == c_ptBuilder</c> (<c>shipIGC.h:1699-1702</c>). This
    /// port's ships carry <c>PilotType=Player</c> or a drone pilot type
    /// (P285), so the faithful stand-in for "Builder-role" is the Constructor
    /// hull capability the port already uses to identify a Builder —
    /// <c>c_habmBuilder</c> (<c>DataHullTypeIGC.HabmBuilder</c>, igc.h:741).
    /// A non-Builder hull is refused.</item>
    /// <item><b>OT_asteroid target only.</b> Native's c_cidBuild case is legal
    /// only against an <c>OT_asteroid</c> (<c>shipIGC.h:1699-1701</c>); any
    /// other object type is refused.</item>
    /// <item><b>Capability match.</b> Native requires
    /// <c>((IasteroidIGC*)pmodel)-&gt;HasCapability(m_abmOrders)</c>
    /// (<c>shipIGC.h:1702</c>) where <c>m_abmOrders</c> is the configured
    /// station type's build AABM (<c>GetBuildAABM()</c>,
    /// <c>shipIGC.h:2053-2057</c>) — this port's
    /// <see cref="Ship.BuilderStationType"/>. An asteroid lacking the required
    /// capability bit is refused (not every rock is buildable, per the 0x08
    /// <c>c_aabmBuildable</c> bit this port already decodes).</item>
    /// <item><b>Same-sector rock.</b> <see cref="Model.Sector.StartBuildingEffect"/>
    /// requires the rock to be in the builder's own sector (its preconditions),
    /// the port's stand-in for native "the builder reaches the asteroid's
    /// cluster"; a rock elsewhere is refused rather than crashing the tick.</item>
    /// </list>
    /// NPC constructors retain an accepted target while traveling and register
    /// construction only on arrival (native shipIGC.cpp:2810-2827). Player
    /// Builder hulls use the existing immediate effect path. A registered
    /// effect commits its constructor and asteroid until completion or abort.
    /// </summary>
    private bool StartBuildIfLegal(ConnectionId from, short commanderShipId, Ship target, OrderChange req)
    {
        if (target.Dead || target.StationId is not null
            || target.Sector is null || !target.Sector.Models.Contains(target)) return false;

        // Gate 1 — Builder-role pilot (native LegalCommand requires
        // m_pilotType == c_ptBuilder, shipIGC.h:1699-1702). The port's stand-in
        // for "Builder-role" is twofold, both faithful to native's PILOT-TYPE
        // gate: a ship whose real PilotType IS Builder (an NPC Builder drone —
        // Ship.PilotTypeBuilder, the faithful c_ptBuilder) OR a ship whose hull
        // carries c_habmBuilder (DataHullTypeIGC.HabmBuilder, igc.h:741 — the
        // P285/P422 stand-in for a player's Builder-class hull, since player
        // ships carry PilotType=Player). A non-Builder is refused.
        bool isBuilderPilot = target.PilotType == Ship.PilotTypeBuilder;
        if (!isBuilderPilot && !target.HullType.HasCapability(DataHullTypeIGC.HabmBuilder))
        {
            RefuseBuild(from, commanderShipId, target, $"BUILD from a non-Builder ship ('{target.HullType.Name}', pilotType {target.PilotType}, no c_habmBuilder) — refused (native LegalCommand Builder-only gate, shipIGC.h:1699-1702)");
            return false;
        }
        // H-18: only an actual construction effect commits a constructor.
        // IsBuilding also marks an idle reservation (P652c). Repeating the
        // accepted order is harmless; changing its rock cannot start a second build.
        if (target.IsBuilding && !target.IsNpc) return false;
        var commitment = target.Sector.BuildingEffects.FirstOrDefault(e => ReferenceEquals(e.Builder, target));
        if (commitment is not null)
            return req.ObjectType == IgcObjectType.Asteroid && req.ObjectID == commitment.Target.Data.AsteroidId;

        // Gate 2 — OT_asteroid target only (shipIGC.h:1699-1701).
        if (req.ObjectType != IgcObjectType.Asteroid)
        {
            RefuseBuild(from, commanderShipId, target, $"BUILD target objectType {req.ObjectType} is not an asteroid — refused (native LegalCommand OT_asteroid-only, shipIGC.h:1699-1701)");
            return false;
        }

        var rock = FindAsteroidById(req.ObjectID, target.Mission);
        if (rock is null)
        {
            RefuseBuild(from, commanderShipId, target, $"BUILD target asteroid {req.ObjectID} not found in any sector — refused");
            return false;
        }

        // Gate 3 — capability match (native HasCapability(m_abmOrders),
        // shipIGC.h:1702; m_abmOrders = configured station type's build AABM,
        // shipIGC.h:2053-2057).
        if (target.BuilderStationType is not { } st)
        {
            RefuseBuild(from, commanderShipId, target, $"BUILD with no configured BuilderStationType (P285 SET_BUILDER_TARGET) — nothing to build, refused");
            return false;
        }
        if (!rock.Data.HasCapability(st.AabmBuild))
        {
            RefuseBuild(from, commanderShipId, target, $"BUILD of '{st.Name}' (AabmBuild 0x{st.AabmBuild:x}) refused at asteroid {rock.Data.AsteroidId} (cap 0x{rock.Data.AabmCapabilities:x}) — capability mismatch (native HasCapability(m_abmOrders), shipIGC.h:1702)");
            return false;
        }

        // The builder's sector is required (StartBuildingEffect preconditions);
        // a placed ship always has one, but be defensive like the caller's gates.
        if (target.Sector is not { } sector)
        {
            RefuseBuild(from, commanderShipId, target, $"BUILD legal but ship has no sector — refused (StartBuildingEffect precondition, Sector.cs:363)");
            return false;
        }

        // Same-sector rock guard: StartBuildingEffect requires the target rock
        // in the builder's own sector (its preconditions) and would throw
        // otherwise — the port's stand-in for native "the builder is in the
        // asteroid's cluster". Refuse cleanly instead of crashing the tick.
        if (!sector.Mineable.Contains(rock))
        {
            RefuseBuild(from, commanderShipId, target, $"BUILD target asteroid {rock.Data.AsteroidId} is not in builder ship {target.Id}'s sector '{sector.Name}' — refused (StartBuildingEffect same-sector precondition)");
            return false;
        }

        // H-18: single-commitment — never register two constructors on the same
        // asteroid. Refuse if another builder already has a pending effect here.
        if (sector.BuildingEffects.Any(effect => ReferenceEquals(effect.Target, rock)))
        {
            RefuseBuild(from, commanderShipId, target, $"BUILD target asteroid {rock.Data.AsteroidId} is already under construction — refused (H-18 single-commitment)");
            return false;
        }

        // Native constructors travel to the accepted rock before drilling
        // (shipIGC.cpp:2810-2827). SetBuilderBuild releases idle reservations;
        // the arrival path and Sector enforce H-18's single commitment.
        if (target.IsNpc)
            target.SetBuilderBuild(rock);
        else
            sector.StartBuildingEffect(target, rock, st, sector.LastUpdate);
        Console.WriteLine($"[build] {from} ship {target.Id} issued BUILD of '{st.Name}' at asteroid {rock.Data.AsteroidId} '{rock.Data.Name}' — {(target.IsNpc ? "traveling before drilling" : "construction started")}");
        return true;
    }

    /// <summary>
    /// P422/P425 — surface a refused Build command to the commanding player's
    /// own client as a private "Negative." chat (native's command-refusal ack is
    /// exactly <c>SendChat(pshipTo, CHAT_INDIVIDUAL, ..., "Negative.")</c>,
    /// clintlib.cpp:2261-2268). Logged here too, so the server console carries
    /// the full reason even if the commanding player is no longer connected.
    /// </summary>
    private void RefuseBuild(ConnectionId from, short commanderShipId, Ship target, string reason)
    {
        ConsoleLog.Write($"[build] {from} ship {target.Id}: {reason}");
        var msg = new ChatMessage
        {
            Message = $"Negative. {reason}",
            SidSender = commanderShipId,
            ChatTarget = (int)ChatTarget.IndividualNoFilter,
            OidRecipient = commanderShipId,
        };
        SendPeer(from, FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// P150 — the port of <c>pship-&gt;SetCommand(pfmOC-&gt;command, pmodel,
    /// pfmOC-&gt;commandID)</c> (<c>FedSrv.CPP:7330</c>): write the found station
    /// into whichever of the port's command slots the decoded <c>command</c>
    /// field names. The port does not build the full native 4-slot array — the
    /// real client's three ORDER_CHANGE send sites only ever use
    /// <c>c_cmdAccepted</c> (<c>WinTrek.cpp:3778</c>, <c>8907</c>) or
    /// <c>c_cmdCurrent</c> (<c>WinTrek.cpp:8842</c>), never <c>c_cmdQueued</c>
    /// or <c>c_cmdPlan</c> — so this switch covers the two new named slots
    /// (<see cref="Ship.AcceptedTarget"/>, <see cref="Ship.QueuedTarget"/>) and
    /// the existing <c>c_cmdCurrent</c> slot (<see cref="Ship.CommandTarget"/>),
    /// logging-and-not-storing anything else (<c>c_cmdPlan</c>, or a degenerate
    /// value) rather than guessing.
    /// </summary>
    private static void ApplyCommandSlot(Ship target, short commandSlot, DataStationIGC station, short commandId)
    {
        switch (commandSlot)
        {
            case OrderChange.CmdAccepted:
                target.AcceptedTarget = station;
                target.AcceptedCommandId = commandId;
                break;
            case OrderChange.CmdQueued:
                target.QueuedTarget = station;
                target.QueuedCommandId = commandId;
                break;
            case OrderChange.CmdCurrent:
                // The port's c_cmdCurrent slot is Ship.CommandTarget — a `Model?`
                // for weapon-aim (P139); stations are DataStationIGC record structs,
                // not Models, so a station cannot be stored there. This is SAFE for
                // every reachable case: the only client send site that emits
                // c_cmdCurrent (WinTrek.cpp:8842) is the client-local autopilot
                // setter that also sets its own local target, and the native drone
                // path is DoNotExecute-guarded (FedSrv.CPP:7287) so no drone command
                // round-trips into the generic fallback either. Logged, not stored,
                // and not silently ignored.
                if (ConsoleLog.Verbose) ConsoleLog.Write("[orderchange] c_cmdCurrent write into the return-to-base branch is not representable in this port " +
                                  "(Ship.CommandTarget is Model?, stations are DataStationIGC) — logged, no store");
                break;
            default:
                // c_cmdPlan (3) is explicitly out of scope (P150); any degenerate
                // value likewise. Log and no-op — never guess at the slot's meaning.
                if (ConsoleLog.Verbose) ConsoleLog.Write($"[orderchange] command slot {commandSlot} has no representable home in this port — logged, no store (c_cmdPlan/unknown out of scope)");
                break;
        }
    }

    /// <summary>
    /// P145 — the <c>FM_C_TREASURE_ACK(163)</c> handler, the port of
    /// <c>FedSrv.CPP:6094-6152</c> <c>case FM_C_TREASURE_ACK</c> — the second leg
    /// of the part-treasure pickup round-trip (the offer was buffered + announced
    /// by <see cref="PerformTreasurePickup"/>). The client's decision on the
    /// buffered <see cref="Ship.PendingTreasureOffer"/>:
    /// <list type="bullet">
    /// <item><b>Accept into a real mount</b> (<c>pfmTA-&gt;mountID != c_mountNA</c>,
    /// <c>FedSrv.CPP:6099-6119</c>): merge into an existing same-type part at that
    /// slot (<c>ppart-&gt;SetAmount(ppart-&gt;GetAmount() + amount)</c>,
    /// <c>FedSrv.CPP:6101-6103</c>) or mount fresh
    /// (<c>CreateAndAddPart(ppt, mountID, amount)</c>, <c>FedSrv.CPP:6105-6106</c>),
    /// then broadcast <c>ADD_PART(164)</c> sector-wide via
    /// <c>GetGroupSectorFlying(pcluster)</c> (<c>FedSrv.CPP:6108-6119</c>) with the
    /// <em>granted</em> (buffered) amount, not a merged total.</item>
    /// <item><b>Decline</b> (<c>pfmTA-&gt;mountID == c_mountNA</c>,
    /// <c>FedSrv.CPP:6125-6129</c>): re-create the part as a fresh treasure at the
    /// ship's <em>current</em> position (native <c>CreateTreasure(g.timeNow,
    /// pfsPlayer-&gt;GetIGCShip(), amount, ppt, GetPosition(), 25.0f, 120.0f)</c>)
    /// — a new object, since the original was destroyed on touch.</item>
    /// </list>
    /// Either way the pending offer is cleared (<c>FedSrv.CPP:6148</c>).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Gates.</b> Stage started (<c>FedSrv.CPP:6096</c>) and a non-null
    /// <see cref="Ship.PendingTreasureOffer"/> (native
    /// <c>GetTreasureObjectID() != NA</c>, <c>FedSrv.CPP:6097</c>). The task's
    /// native unboarded gate is enforced before accepting or declining the offer
    /// (FedSrv.CPP:6099). The pending offer is consumed before any mutation or send.</item>
    /// <item><b>Bad mount = native TrapHackBoot abort.</b> A mount out of the
    /// hull's hardpoint range, or one whose <c>PartMask</c> cannot host the part
    /// type (native <c>TrapHackBoot(!CanMount(ppt, pfmTA-&gt;mountID))</c>,
    /// <c>FedSrv.CPP:6100</c>), is a hack: this port's TrapHackBoot analogue is
    /// log-and-return; the offer has already been consumed so it cannot replay.</item>
    /// <item><b>Fresh-mount fraction.</b> <see cref="Ship.MountPart"/> is given
    /// <see cref="ExpandedPartData.FullyMounted"/> — an accepted part is mounted
    /// up, the same observer-facing stance the port's other mount paths use.</item>
    /// <item><b>Send target: sector-wide</b> — <see cref="SendSectorWide"/> for
    /// both the <c>ADD_PART</c> echo and the decline's <c>TreasureSpawn</c>,
    /// matching native <c>GetGroupSectorFlying(pcluster)</c>.</item>
    /// </list>
    /// Runs on the sim thread (dispatch drains inside <see cref="Tick"/>), so it
    /// may mutate the ship's <c>_parts</c> / offer state without extra locking,
    /// like every other inbound handler.
    /// </remarks>
    private void HandleTreasureAck(ConnectionId from, LiveShip live, TreasureAck ack)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            ConsoleLog.Write($"[treasureack] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — ignored (native gate, FedSrv.CPP:6096)");
            return;
        }
        if (ship.PendingTreasureOffer is not { } offer)
        {
            ConsoleLog.Write($"[treasureack] {from} ship {ship.Id}: no pending part-treasure offer — ignored (native gate GetTreasureObjectID()!=NA, FedSrv.CPP:6097)");
            return;
        }

        ship.PendingTreasureOffer = null; // Native clears its buffer (FedSrv.CPP:6148).
        if (ship.Sector is null || ship.StationId is not null || ship.Dead
            || ship.ParentShip is not null || offer.Amount < 0) return;

        // Resolve the buffered part type from the static core (native
        // pMission->GetPartType(oid), FedSrv.CPP:6104) — the port's part-type
        // lookup is Mission.Core.PartTypes (StaticCore).
        if (!mission.Core.PartTypes.TryGetValue(offer.PartTypeId, out var partType))
        {
            Console.WriteLine($"[treasureack] {from} ship {ship.Id}: buffered part-type {offer.PartTypeId} absent from core — offer cleared");
            return;
        }

        offer.Amount = Ship.ClampPartAmount(ship.HullType, mission.Core, partType, offer.Amount);
        if (ack.MountId != TreasureAck.MountNA)
        {
            // Accept into a real mount.
            byte mount = ack.MountId;
            if (!ship.HullType.CanMountPart(partType, unchecked((sbyte)mount)))
            {
                Console.WriteLine($"[treasureack] {from} ship {ship.Id}: mount {mount} cannot mount part {offer.PartTypeId} — dropped (native TrapHackBoot !CanMount, FedSrv.CPP:6100)");
                return;
            }

            // Merge into an existing same-type part at this mount, else mount
            // fresh (the exact _parts lookup TrySwapPart/TryRemovePart reuse,
            // then native FedSrv.CPP:6101-6106).
            var existing = ship.Parts.FirstOrDefault(p => p.EquipmentType == partType.EquipmentType && p.MountId == mount);
            if (existing is not null)
            {
                if (existing.PartId != offer.PartTypeId) return;
                int total = Ship.ClampPartAmount(ship.HullType, mission.Core, partType, (int)existing.Amount + offer.Amount);
                offer.Amount = (short)Math.Max(0, total - existing.Amount);
                ship.SetPartAmount(existing, total);
                Console.WriteLine($"[treasureack] {from} ship {ship.Id}: merged part {offer.PartTypeId} amount {offer.Amount} into existing mount {mount} (now {existing.Amount + offer.Amount})");
            }
            else
            {
                // P314 — the MountPart cap (Ship.cs) refuses a fresh mount once
                // a ship is at the wire's 255-part ceiling; log and drop the
                // buffered offer instead of adding a 256th part that would
                // crash ExportShip.Encode (the teammate-export path).
                if (ship.MountPart(partType, offer.Amount, mount, ExpandedPartData.FullyMounted) is null)
                {
                    Console.WriteLine($"[treasureack] {from} ship {ship.Id}: REFUSED fresh mount of part {offer.PartTypeId} at mount {mount} — ship already at the 255-part wire ceiling; offer dropped");
                            return;
                }
                Console.WriteLine($"[treasureack] {from} ship {ship.Id}: mounted fresh part {offer.PartTypeId} amount {offer.Amount} at mount {mount}");
            }

            // Broadcast ADD_PART(164) sector-wide with the granted (buffered)
            // amount — native pfmAddPart->newPartData.amount = amount, the
            // granted quantity, not a merged total (FedSrv.CPP:6110-6116).
            var add = new AddPart
            {
                ShipID = ship.Id,
                NewPartData = new PartData(offer.PartTypeId, offer.Amount, mount),
            };
            SendSectorWide(ship.Sector!, add.Encode());
            Console.WriteLine($"[treasureack] {from} ship {ship.Id}: -> ADD_PART(164) shipID={ship.Id} part={offer.PartTypeId} amount={offer.Amount} mount={mount} sector {ship.Sector!.Id} wide");
        }
        else
        {
            // Declined: drop it back into space at the ship's CURRENT position
            // (native CreateTreasure(g.timeNow, pfsPlayer->GetIGCShip(), amount,
            // ppt, GetPosition(), 25.0f, 120.0f), FedSrv.CPP:6127-6129) — a NEW
            // treasure (not the original, which was destroyed on touch).
            var treasure = ship.CreateDropTreasure(partType, offer.Amount, _simNow, (uint)TickIndex);
            SendSectorWide(ship.Sector!, new TreasureSpawn { Data = treasure.Data }.Encode());
            Console.WriteLine($"[treasureack] {from} ship {ship.Id}: declined part {offer.PartTypeId} amount {offer.Amount} -> re-created treasure obj={treasure.Data.ObjectId} -> TREASURE(28) sector {ship.Sector!.Id} wide");
        }

    }

    private void ApplyLoadout(ConnectionId from, LiveShip live, BuyLoadoutRequest buy)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[loadout] {from} ship {ship.Id}: no mission (ship not placed) — cannot apply loadout");
            return;
        }

        // P473 — the test-harness tech-gate bypass, resolved once up front. When
        // an eligible harness connection has ignoreTechGate set, ONLY the tech
        // (station CanBuy) check is skipped — funds, pricing, the drone-hull
        // (GroupId<0) gate and the hardpoint-compatibility gate all still run,
        // so the harness pays the real price through the real debit path. A
        // non-prefixed (human) connection always resolves to null here.
        var harness = HarnessGrantFor(from);
        bool harnessIgnoreTech = harness is { IgnoreTechGate: true };

        // Resolve the requested hull against the static core (the established
        // lookup pattern: StaticCore.HullTypes is keyed by HullId,
        // StaticCore.cs:148). Unknown hull ⇒ nothing to grant ⇒ no ACK.
        if (!mission.Core.HullTypes.TryGetValue(buy.HullId, out var hull))
        {
            Console.WriteLine($"[loadout] {from} ship {ship.Id}: BUY_LOADOUT names unknown hull {buy.HullId} — no ACK (report, not fabricated)");
            return;
        }
        bool hullChanged = hull.HullId != ship.HullType.HullId;

        // A real docked station is required even when the harness bypasses tech.
        // Native PurchaseShipLoadout consults that station (shipIGC.h:992-1000).
        DataStationTypeIGC? stationType = null;
        Side? side = null;
        if (ship.StationId is { } dockId && ship.SideId is { } sd)
        {
            side = mission.Sides.FirstOrDefault(s => s.Id == sd);
            foreach (var sec in mission.Sectors)
                foreach (var st in sec.Stations)
                    if (st.StationId == dockId && mission.Core.StationTypes.TryGetValue(st.StationTypeId, out var dt))
                    {
                        stationType = dt;
                        break;
                    }
        }

        // P441 — a refused purchase's ack reflects the ship's REAL, unchanged
        // loadout with FBoughtEverything=false and FLaunch=false (native soft-fails
        // the port's log-and-refuse way instead of TrapHackBoot's disconnect).
        void Refuse(string why)
        {
            var refused = new BuyLoadoutAck
            {
                HullId = ship.HullType.HullId,
                FBoughtEverything = false,
                FLaunch = false,
                Parts = ship.ExportInventoryForAck().ToArray(),
            };
            SendPeer(from, FedPacket.Pack(new[] { refused.Encode() }));
            Console.WriteLine($"[loadout] {from} ship {ship.Id}: {why} — REFUSED (ship unchanged; ack fbought=false fLaunch=false)");
        }

        // Native FedSrv.CPP:6153-6172: docked, alive/non-ghost, side, unboarded.
        if (ship.StationId is null || ship.Dead || ship.ParentShip is not null
            || ship.SideId is not >= 0 || stationType is null || side is null
            || buy.Parts.Count > BuyLoadoutRequest.MaxParts || buy.Parts.Any(p => p.Amount < 0))
        {
            Refuse("purchase requires a live unboarded ship docked at a valid station and a bounded nonnegative loadout");
            return;
        }

        long oldShipValue = RefundableShipValue(ship);

        // P441+P470 — hull acceptance gate. Native's hangar never offers a
        // player a drone-only hull (GroupID < 0 — load.cpp:1603-1606,
        // clintlib.h:1858), so a player cannot legitimately request one; the
        // port must refuse it the same way it refuses an unsellable hull.
        // P441 also refuses a DIFFERENT hull the docked station can't sell
        // (native CanBuy abort, shipIGC.h:999-1000). Both conditions run under
        // the SAME gate and the SAME `hullChanged` guard native uses (the
        // `hullID != pht->GetObjectID()` check, shipIGC.h:996): a same-hull buy
        // re-mounts parts / relaunches with no hull purchase and no hull gate.
        // The GroupId rule is dock-independent (a drone hull is unwinnable
        // whether or not the buyer is docked); the sellable rule needs a
        // station, so it still only runs when docked. A refused purchase leaves
        // the ship exactly as it was.
        if (hullChanged && (hull.GroupId < 0 ||
            (!harnessIgnoreTech && stationType is { } dockSt && side is { } dockSide && !StationSellsHull(dockSt, dockSide, hull))))
        {
            Refuse(hull.GroupId < 0
                ? $"hull {hull.HullId} ('{hull.Name}') is a drone-only hull (group {hull.GroupId}) — not player-buyable"
                : $"hull {hull.HullId} ('{hull.Name}') not sellable at docked station (tech)");
            return;
        }

        // P488 — successor upgrade (native m_station->GetSuccessor(pht),
        // shipIGC.h:1004). Once the requested hull passes the gate, a CHANGING
        // hull that the docked station considers superseded is actually bought
        // as the side's best available (furthest buyable) successor — exactly
        // native PurchaseShipLoadout, which only runs the successor block when
        // the requested hull differs from the current one (shipIGC.h:995-1004;
        // a same-hull buy re-mounts parts with no upgrade). Non-docked keeps the
        // requested hull (no station to consult — the same scope as the tech
        // gate above). The successor is tech-gated through the SAME union as
        // CanBuy, so a fresh side with unmet successor tech buys the requested
        // hull unchanged.
        DataHullTypeIGC grantedHull = hull;
        if (hullChanged && stationType is { } succSt && side is { } succSide)
            grantedHull = SuccessorUpgrade.HullSuccessor(mission.Core, hull, succSt.TtbmLocal, succSide.Techs);
        bool grantedHullChanged = grantedHull.HullId != ship.HullType.HullId;

        // P441 — PROJECTION pass. Resolve every requested part and apply the
        // part tech gate + hardpoint gate BEFORE mutating the ship, so the cost
        // is known and the funds gate can run against an untouched ship (a
        // refused purchase must leave the ship exactly as it was).
        var toMount = new List<(DataPartTypeIGC Type, short Amount, byte MountId, byte Fraction)>();
        int projectedSkipped = 0;
        foreach (var p in buy.Parts)
        {
            if (!mission.Core.PartTypes.TryGetValue(p.PartId, out var partType))
            {
                // P433/P435 — a real launcher/dispenser: the compact launcher form
                // of OT_partType lives in core.Launchers (NOT PartTypes — the 117
                // part records have no Magazine/Dispenser/ChaffLauncher subtype),
                // so resolve the PartId into its mountable form of whichever real
                // expendable kind it fires: a missile launcher (ET_Magazine,
                // subtype MissileLauncher — the shape the FM_CS_FIRE_MISSILE(70)
                // handler expects), a mine or probe dispenser (ET_Dispenser,
                // subtype MineLauncher/ProbeLauncher), or a chaff dispenser
                // (ET_ChaffLauncher, subtype ChaffLauncher) — all shapes the
                // FIRE_EXPENDABLE(82) handler resolves. A PartId that is neither a
                // core.Part nor a real launcher is reported as not-in-core.
                if (!mission.Core.TryBuildExpendableLauncher(p.PartId, out partType))
                {
                    projectedSkipped++;
                    continue;
                }
                if (ConsoleLog.Verbose) ConsoleLog.Write($"[loadout] {from} ship {ship.Id}: granting real launcher part {p.PartId} ('{partType.Name}', {partType.EquipmentType}@{p.MountId} subtype {partType.Type.GetType().Name}, ammo {p.Amount}) — P435");   // P619c: per-part spawn grant, gated (join-burst volume)
            }
            // P441 — part tech gate (native m_station->CanBuy(part),
            // shipIGC.h:1034): a DOCKED ship whose station can't sell the part
            // skips it. Native then tries to "reuse a part the ship already
            // owned" (shipIGC.h:1040+); the port deliberately does NOT mirror
            // that salvage branch (see P441_SERVER_BUY_PRICING.md) — we simply
            // skip the part, which also makes FBoughtEverything false.
            // P473 — a harness connection with ignoreTechGate set skips ONLY
            // this check; it still pays the part's real price if mounted.
            if (!harnessIgnoreTech && stationType is { } pSt && side is { } pSide &&
                !TechTreeBitMask.RequiredTechsBuyable(partType.TtbmRequired, pSt.TtbmLocal, pSide.Techs))
            {
                projectedSkipped++;
                continue;
            }
            // P488 — part successor upgrade (native m_station->GetSuccessor(ppt),
            // shipIGC.h:1033): a requested part the docked station considers
            // superseded is bought as its furthest buyable successor, then the
            // hardpoint gate below re-checks the (possibly upgraded) part against
            // the new hull. Non-docked keeps the requested part (no station to
            // consult, same scope as the tech gate).
            if (stationType is { } pSuccSt && side is { } pSuccSide)
                partType = SuccessorUpgrade.PartSuccessor(mission.Core, partType, pSuccSt.TtbmLocal, pSuccSide.Techs);
            int mount = unchecked((sbyte)p.MountId);
            bool occupied = toMount.Any(x => x.MountId == p.MountId
                && (mount < 0 || x.Type.EquipmentType == partType.EquipmentType));
            if (!grantedHull.CanMountPart(partType, mount) || occupied)
            {
                projectedSkipped++;
                continue;
            }
            toMount.Add((partType, Ship.ClampPartAmount(grantedHull, mission.Core, partType, p.Amount), p.MountId, p.FractionMounted));
        }

        // P441 — PRICING, the way native does: cost = ShipValue(after) -
        // ShipValue(before) (FedSrv.CPP:6182-6207; GetValue = hull price + Σ part
        // prices, shipIGC.h:396-406). Computed from the projected (actually-
        // mounted) parts so a skipped part is never charged. Launchers /
        // dispensers carry their REAL price (DataPartTypeIGC.FromLauncherDef now
        // takes the expendable's launcherDef.price — DataMissileTypeIGC.Price &
        // friends), not 0.
        long newHullValue = grantedHullChanged ? Math.Max(0, grantedHull.Price) : ship.RefundableHullValue;
        long cost = newHullValue + toMount.Sum(x => PartValue(x.Type, x.Amount)) - oldShipValue;

        // P441 — FUNDS gate. Native TrapHackBoot disconnects here; the port's
        // soft-fail convention is log-and-refuse: ship unchanged, ack shows the
        // real unchanged loadout with FBoughtEverything=false / FLaunch=false,
        // no MONEY_CHANGE. A negative cost (downgrade) refunds, as in native.
        if (cost > live.Wallet.Money || live.Wallet.Money < 0 || (long)live.Wallet.Money - cost > int.MaxValue)
        {
            Refuse($"cost {cost} > wallet {live.Wallet.Money}");
            return;
        }

        // ------------------------------------------------------------------
        // The ship mutates here. All refusal gates already returned with the
        // ship untouched.
        // ------------------------------------------------------------------
        using var loadoutUpdate = ship.BeginLoadoutUpdate();
        if (grantedHullChanged)
        {
            // P334 — real server-side hull swap, mirroring native's actual
            // behavior. BUY_LOADOUT is dispatched to CfsShip::PurchaseShipLoadout
            // (FedSrv.CPP:6189), which mutates the EXISTING ship object IN PLACE
            // via SetBaseHullType(pht) (shipIGC.h:1019-1020) — it does NOT
            // retire-and-recreate the ship. The same Ship keeps its id, side,
            // position/sector, score and wallet; only its base hull type is
            // swapped. The port's Ship.ChangeHull is that same in-place
            // primitive (port of CshipIGC::SetBaseHullType, Ship.cs:189-234,
            // shipIGC.cpp:1352), already used by the death-respawn path. Every
            // hull-derived physical value (Mass, MaxTurnRates/TurnTorques,
            // Thrust, Speed, Signature, HitPoints = fraction·HullType.HitPoints,
            // the hardpoint layout, MaxEnergy/MaxFuel) is a live getter over
            // _hull (Ship.cs:157,314,3407-3478, etc.), so swapping _hull in
            // place makes them all reflect the new hull automatically — no
            // per-field recomputation is needed.
            Console.WriteLine($"[loadout] {from} ship {ship.Id}: swapping base hull {ship.HullType.HullId} ('{ship.HullType.Name}') -> {grantedHull.HullId} ('{grantedHull.Name}') — in-place SetBaseHullType (native PurchaseShipLoadout, shipIGC.h:1019-1020; P488 successor {(grantedHull.HullId != hull.HullId ? $"of requested {hull.HullId}" : "")})");
            ship.ChangeHull(grantedHull);
            ship.RefundableHullValue = Math.Max(0, grantedHull.Price);

            // Native SetBaseHullType resets the construction-seeded runtime
            // pools to empty (m_energy = 0.0f, m_ammo = 0; full repair
            // m_fraction = 1.0f; empty fuel/ore too, shipIGC.cpp:1428-1437).
            // The port has NO passive energy regeneration (Energy only refills
            // at dock repair, Ship.RepairAndRefuel:3298, or the constructor's
            // full seed:152), so literal zeroing would strand a launched
            // hull-swapped ship unable to fire or boost — a broken state the
            // task forbids ("rather than silently corrupting state"). Instead
            // this RE-SEEDS the two pools to the NEW hull's full values exactly
            // as the constructor does for a fresh ship (Ship.cs:152-153), the
            // port-functional equivalent of native's eventual full recharge:
            // Energy = hull.MaxEnergy, Ammo = hull.MaxAmmo, and the full-repair
            // fraction (1.0f) is kept as native's m_fraction = 1.0f. Deliberately
            // kept OUT of ChangeHull itself so that shared primitive retains its
            // pinned "swap-only-the-hull-record" contract
            // (CloakVisibilityGateLiveTests tops energy back up after it).
            ship.SetFraction(1.0f);
            ship.Energy = ship.EffectiveMaxEnergy();
            ship.Ammo = grantedHull.MaxAmmo;
        }

        // Replace the ship's loadout with the requested parts (only the ones
        // that passed the projection). Mirror the native ProcessShipLoadout
        // rebuild (shipIGC.h:901-921).
        ship.ClearLoadout();
        int granted = 0, skipped = projectedSkipped;
        foreach (var (partType, amount, mountId, fraction) in toMount)
        {
            // P314 — the MountPart cap (Ship.cs) refuses a fresh mount once a
            // ship is at the wire's 255-part ceiling; log and count it skipped
            // instead of letting a 256th part crash ExportShip.Encode later.
            int mount = unchecked((sbyte)mountId);
            bool mounted = mount < 0
                ? ship.TryMountCargoPart(partType, amount, mount, fraction)
                : ship.MountPart(partType, amount, mountId, fraction) is not null;
            if (!mounted)
            {
                ConsoleLog.Write($"[loadout] {from} ship {ship.Id}: refused mounting part {partType.PartId} at mount {mountId} — ship already at the 255-part wire ceiling; counted as skipped");
                skipped++;
                continue;
            }
            granted++;
        }

        // P522 — native's BUY_LOADOUT restock tops the mounted shield to full
        // AFTER the loadout is rebuilt (FedSrv.CPP:12756-12764, the block after
        // the part processing: pship->SetFraction(1.0f) then
        // pshield->SetFraction(1.0f) — native tops the shield on EVERY
        // BUY_LOADOUT, hull change or not). Placed here, after ClearLoadout +
        // the mount loop, so a fresh loadout's shield ends full regardless of
        // what the rebuild did to it; without it a newly-bought (or hull-
        // swapped) ship can launch with a shield below 1.0 — less than native's
        // starting shield (the other half of the P522 undock false-red-flash
        // fix; the client resets its flash baseline across undock regardless).
        ship.SetFraction(1.0f);
        ship.SetShieldFraction(1.0f);

        // P441 — DEBIT / REFUND + side-wide MONEY_CHANGE (native
        // FedSrv.CPP:6195-6207). The actual cost is recomputed from the really-
        // mounted parts (== projected cost unless a 255-ceiling skip occurred);
        // only when cost != 0 does money move. Negative cost (downgrade) refunds:
        // dMoney = -cost > 0, sidTo = buyer ship, sidFrom = NA, sent to the
        // buyer's whole side group (BroadcastMoneyChangeSideWide).
        long actualCost = RefundableShipValue(ship) - oldShipValue;
        if (actualCost != 0)
        {
            live.Wallet.Money = checked((int)(live.Wallet.Money - actualCost));
            if (ship.SideId is { } buyerSide)
                BroadcastMoneyChangeSideWide(ship.Mission!, buyerSide, checked((int)-actualCost), ship.Id);
            Console.WriteLine($"[loadout] {from} ship {ship.Id}: {(actualCost < 0 ? "refunded" : "debited")} {(-actualCost < 0 ? -actualCost : actualCost)} (wallet now {live.Wallet.Money}) + MONEY_CHANGE(60) dMoney={-actualCost} side-wide");
        }
        else
        {
            Console.WriteLine($"[loadout] {from} ship {ship.Id}: cost 0 — no money movement");
        }

        // The ACK's tail reflects the ship's REAL inventory after the apply:
        // equipped parts then the cargo hold (native's ProcessShipLoadout mixes
        // both into one tail, P626). The byte-for-byte round-trip guarantee
        // applies to the entire requested inventory, including native negative
        // cargo mounts, so "everything granted" counts equipped and cargo parts.
        var ackParts = ship.ExportInventoryForAck().ToArray();
        int mountedAcked = ackParts.Length;
        bool allGranted = mountedAcked == buy.Parts.Count && granted == buy.Parts.Count && skipped == 0;
        // fBoughtEverything = every requested part was actually mounted. Kept
        // false for the zero-part hull-only case so those bytes stay identical
        // to P26/P27/P28 (preserving the tested adopt-a-hull path exactly).
        bool fboughtEverything = buy.Parts.Count > 0 && allGranted;
        var ack = new BuyLoadoutAck
        {
            HullId = grantedHull.HullId,
            FBoughtEverything = fboughtEverything,
            FLaunch = buy.FLaunch,
            Parts = ackParts,
        };
        SendPeer(from, FedPacket.Pack(new[] { ack.Encode() }));
        var note = $"[loadout] ship {ship.Id} adopted loadout hull {ack.HullId} + {ack.Parts.Count} part(s)";
        Console.WriteLine($"[loadout] {from} ship {ship.Id} <- BUY_LOADOUT_ACK hull {ack.HullId} nParts {ack.Parts.Count} fbought {fboughtEverything} fLaunch {ack.FLaunch}{(skipped > 0 ? $" ({skipped} skipped)" : "")}");
        Notify(note);

        // P494 — the just-bought loadout becomes the player's DESIRED loadout
        // for the dock auto-restock (native pfsPlayer->SaveDesiredLoadout(),
        // FedSrv.CPP:6256: save the free expendable launchers so a later dock
        // re-buys them, upgraded to their successors).
        SaveDesiredLoadout(live);

        // The real WinTrek client's Hangar screen "Launch" button sends
        // exactly this message with FLaunch=true and nothing else
        // (HangarScreen::OnButtonLaunch -> BuyLoadout(ship, true),
        // hangarscreen.cpp:145-160) -- it never sends a separate undock
        // request the way WOPR bots do (FM_C_DOCKED, see PerformUndock's own
        // doc comment). The client's BUY_LOADOUT_ACK handler deliberately
        // does NOT clear the launch lockdown when fLaunch is true
        // (appmsg.cpp:3180: "if (!pfmBuyLoadoutAck->fLaunch && IsLockedDown())
        // EndLockDown(...)") -- it is waiting for FM_S_SET_CLUSTER instead
        // (appmsg.cpp:991-996). Without this, a real player could never
        // launch at all: every click of the Hangar "Launch" button re-hits
        // HangarScreen::OnButtonLaunch's own "if (!IsLockedDown()) ... else
        // assert(false)" (hangarscreen.cpp:147-157) once the first click
        // leaves the lockdown permanently set, since nothing server-side ever
        // released it.
        if (buy.FLaunch && live.Ship.StationId != null)
        {
            PerformUndock(from, live);
        }
    }


    /// <summary>The real engine's <c>CanMount</c> subset test
    /// (<c>src/Igc/hullTypeIGC.h:199-213</c>, <c>partType.PartMask &amp;
    /// ~hardpoint.PartMask == 0</c>): a part type fits a hardpoint iff its
    /// <c>PartMask</c> bits are a subset of the hardpoint's. Factored out of
    /// <see cref="MountCombatWeapon"/>'s inline test (P145) so the same bit
    /// test is shared, not duplicated, by the TREASURE_ACK mount choice
    /// (native <c>CanMount(ppt, pfmTA-&gt;mountID)</c>, <c>FedSrv.CPP:6100</c>).</summary>
    private static bool CanMount(DataPartTypeIGC part, short hardpointMask) =>
        (part.PartMask & ~hardpointMask) == 0;

    /// <summary>
    /// Arm the freshly-placed ship so a live bot can actually fire — the
    /// missing live half of the (unit-tested) combat slice. Requires the ship
    /// to be placed (<see cref="Ship.MountWeapon"/> resolves the weapon's
    /// projectile against the mission core). Picks, <b>per fixed hardpoint</b>,
    /// the cheapest energy-per-shot <c>Weapon</c> part type (dispersion-free,
    /// so a straight shot lands) whose projectile type exists in the core
    /// <em>and</em> whose <see cref="DataPartTypeIGC.PartMask"/> is a subset of
    /// that specific hardpoint's <see cref="DataHullTypeIGC.Hardpoint.PartMask"/>
    /// — the real engine's <c>CanMount</c> subset test (<c>src/Igc/hullTypeIGC.h:199-213</c>,
    /// <c>partType.PartMask &amp; ~hardpoint.PartMask == 0</c>). Different fixed
    /// hardpoints on the same hull may therefore end up with different weapon
    /// types — that is correct, faithful per-hardpoint behaviour, not a bug to
    /// unify. Non-fixed (turret/gunner) hardpoints are skipped:
    /// <see cref="Weapon.IsWeaponSelected"/> never selects a turret gun here
    /// (Weapon.cs:274-275), so mounting one would be a dead-end — worse than
    /// not mounting it. If the core offers no usable weapon at all, a fixed
    /// hardpoint has no PartMask-compatible weapon anywhere in the core, or the
    /// hull has no fixed hardpoints, the relevant slot is left unmounted and
    /// the omission is logged (not fabricated, never thrown).
    ///
    /// Weapon-compatibility scope, stated plainly (this slice's style — no
    /// shields, no economy, no lobby): the real engine consults only
    /// <c>Hardpoint.PartMask</c> for weapons; the hull-level
    /// <see cref="DataHullTypeIGC.PmEquipment"/> gate applies solely to
    /// non-weapon equipment (<c>hullTypeIGC.h:163-176</c>, the <c>mountID==0</c>
    /// <c>else</c> branch) and non-weapon equipment is already out of scope
    /// (no shields repo-wide). So this check is exactly hardpoint vs
    /// part-type <c>PartMask</c> for weapons — <c>PmEquipment</c> and
    /// <c>Ship</c>'s non-weapon equipment handling are deliberately untouched.
    /// </summary>
    private void MountCombatWeapon(Ship ship)
    {
        var mission = ship.Mission;
        if (mission is null)
        {
            Console.WriteLine("[combat] cannot mount a weapon: ship not placed (no mission)");
            return;
        }
        // The usable-weapon universe is exactly what it was before this task:
        // dispersion-free weapons whose projectile exists and has Power > 0,
        // ordered cheapest energy-per-shot first. This task adds the per-hardpoint
        // PartMask subset dimension; it does not change what makes a weapon usable.
        // Materialised once so each hardpoint can scan the same ordered list cheaply.
        var candidates = mission.Core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon)
            .Select(p => (part: p, w: (DataPartTypeIGC.Weapon)p.Type))
            .Where(t => t.w.Dispersion == 0f
                        && mission.Core.ProjectileTypes.TryGetValue(t.w.ProjectileTypeId, out var ptyp) && ptyp.Power > 0f)
            .OrderBy(t => t.w.EnergyPerShot)
            .ToList();
        if (candidates.Count == 0)
        {
            Console.WriteLine("[combat] no usable weapon in core — ship left unarmed (a finding, not fabricated)");
            return;
        }

        var hardpoints = ship.HullType.Hardpoints;
        int fixedCount = 0, armed = 0;
        for (short mount = 0; mount < hardpoints.Length; mount++)
        {
            if (!hardpoints[mount].Fixed)
                continue;              // turret/gunner slot: never selects (Weapon.cs:274-275), so skipping is correct
            fixedCount++;
            short hpMask = hardpoints[mount].PartMask;
            var weaponRec = candidates.FirstOrDefault(t => CanMount(t.part, hpMask));
            if (weaponRec.part == default)
            {
                Console.WriteLine($"[combat] ship {ship.Id}: fixed mount {mount} on hull '{ship.HullType.Name}' "
                    + $"(PartMask {hpMask}) has no PartMask-compatible weapon in core — left unmounted (a finding, not fabricated)");
                continue;
            }
            ship.MountWeapon(weaponRec.w, mount);   // the cheapest compatible weapon for THIS hardpoint
            Console.WriteLine($"[combat]   mount {mount}: '{weaponRec.part.Name}' (PartMask {weaponRec.part.PartMask}, {weaponRec.w.EnergyPerShot}/shot)");
            armed++;
        }
        if (armed == 0)
        {
            Console.WriteLine($"[combat] ship {ship.Id}: hull '{ship.HullType.Name}' has no fixed hardpoints (or none arming) — left unarmed (a finding, not fabricated)");
            return;
        }
        int nonFixed = hardpoints.Length - fixedCount;
        int unmounted = fixedCount - armed;
        Console.WriteLine($"[combat] ship {ship.Id} armed {armed} of {fixedCount} fixed hardpoints "
            + $"(per-hardpoint PartMask-compatible cheapest; {unmounted} fixed slot(s) with no compatible weapon left unmounted; "
            + $"hull {fixedCount} fixed/{hardpoints.Length} total, {nonFixed} non-fixed skipped)");
    }

}
