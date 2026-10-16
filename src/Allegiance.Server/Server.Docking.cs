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
/// Board/leave/dock/undock/teleport/rescue/ripcord/relaunch members of <see cref="Server"/>
/// (P330 stage-4 split from <c>Server.cs</c>). Partial class — shares the <c>Server</c>
/// type, so private field/method access across the split files is unaffected.
/// </summary>
public sealed partial class Server
{

    // Seat a new player in the friendly hangar before their first purchase,
    // matching native SetStation on side entry (FedSrv/fsmission.cpp:791-796).
    // MCP initial buys and pod relaunches then use the same docked fLaunch path.
    private void DockInitialLoadout(Ship ship, Sector sector)
    {
        ship.MarkSpawnLoadoutFree();
        if (ship.StationId is not null || ship.Mission is not { } mission) return;
        var station = sector.Stations.FirstOrDefault(st => st.SideId == ship.SideId
            && mission.Core.StationTypes.TryGetValue(st.StationTypeId, out var type)
            && type.HasCapability(DataStationTypeIGC.SabmRestart));
        if (station == default) return;
        ship.StationId = station.StationId;
        ship.LastDockTime = _simNow;
        ship.Velocity = Vector3.Zero;
        ship.StateM = 0;
    }

    private void SendInitialDockState(ConnectionId from, Ship ship)
    {
        if (ship.StationId is { } station)
            SendPeer(from, FedPacket.Pack([new DockedNotify { StationId = station }.Encode()]));
    }

    /// <summary>
    /// P108+P109 — the server-side board action, the port of
    /// <c>FedSrv.CPP:2322-2475</c> <c>BoardShip()</c>. Establishes turret/
    /// observer <b>occupancy</b> (P108) <em>and</em>, since P109, the real
    /// weapon-<b>gunner binding</b> (native <c>SetTurretID</c>-to-<c>SetGunner</c>,
    /// <c>shipIGC.h:1405-1437</c>), so a boarded gunner actually crews the
    /// carrier's weapon at its slot.
    /// Returns true on a successful board; false lets the caller send
    /// <c>BOARD_NACK</c>. Steps mirrored from the native (each cited):
    /// <list type="bullet">
    /// <item><b>Self-exclusion</b> — <c>pship != pshipBoard</c>
    /// (<c>FedSrv.CPP:2332</c>).</item>
    /// <item><b>Carrier is a player ship</b> — <c>pfsShipBoard-&gt;IsPlayer()</c>
    /// (<c>FedSrv.CPP:2340</c>). In this port that means the carrier has an
    /// owning peer in <c>_peerShips</c> (the player/bot distinction). The
    /// dispatcher already enforced same-side via <see cref=\"FindSameSideShip\"/>,
    /// mirroring native <c>GetShip()</c> on the boarder's side.</item>
    /// <item><b>Both docked</b> — the native precondition
    /// (<c>FedSrv.CPP:2342-2345</c>) requires the boarder and carrier <c>not in
    /// a cluster</c> and <c>at a station</c> (boarding a docked ship from a
    /// docked ship). In this port a docked ship is <c>StationId != null</c>
    /// (docked ships retain their <see cref=\"Ship.Sector\"/>), so the gate
    /// translates to both <c>StationId</c>s being set. Existing children may
    /// switch turrets on their own carrier in flight (FedSrv.CPP:2341).</item>
    /// <item><b>Turret-slot search</b> — <see cref=\"ChooseTurretSlot\"/>,
    /// faithful to the native walk (<c>FedSrv.CPP:2347-2402</c>): no turret
    /// slots ⇒ observer; else pick the first unoccupied, equipped
    /// <c>[MaxFixedWeapons, MaxWeapons)</c> slot, falling back to observer.</item>
    /// <item><b>Observer gate collapsed</b> — native
    /// <c>(turret != NA) || CanCheat()</c> (<c>FedSrv.CPP:2406</c>); this port
    /// has no cheat/admin concept, so per the P100/P103 precedent the gate
    /// collapses to always-true — observer boarding is allowed unconditionally.</item>
    /// <item><b>Defensive detach of the boarder's own children</b> — the native
    /// <c>while (psl = pchildren-&gt;first()) LeaveShip(...)</c>
    /// (<c>FedSrv.CPP:2415-2420</c>). No nested boarding exists in this port, so
    /// the list is always empty; kept defensively per the task.</item>
    /// <item><b>Money refund</b> — <c>refund = pship-&gt;GetValue()</c>
    /// (<c>FedSrv.CPP:2428</c>, native GetValue, shipIGC.h:396-406).
    /// Credits the player wallet and sends a side-wide MONEY_CHANGE
    /// (FedSrv.CPP:2433-2442). Free spawn hardware has zero refund credit.</item>
    /// <item><b>State transition</b> — <c>SetParentShip / SetTurretID</c>
    /// (<c>FedSrv.CPP:2445-2446</c>); the boarder's parts are cleared (the
    /// native <c>SetParentShip</c> with no old parent terminates them,
    /// <c>shipIGC.cpp:3619-3633</c> — \"no parts allowed on an observer or
    /// turret\"); if the carrier is docked the boarder inherits its dock state
    /// (<c>FedSrv.CPP:2456-2457</c>). The wire <c>SHIP_STATUS</c> (hull NA,
    /// parent id, turret/observer state) is derived in
    /// <see cref=\"MakeOwnShipStatus\"/>, so the existing per-tick refresh is
    /// what reflects the change — no new message.</item>
    /// </list>
    /// </summary>
    private bool PerformBoard(Ship boarder, Ship carrier)
    {
        if (ReferenceEquals(boarder, carrier))
        {
            ConsoleLog.Write($"[boardship] ship {boarder.Id} tried to board itself — refused (pship != pshipBoard, FedSrv.CPP:2332)");
            return false;
        }

        // Native SetParentShip clears the hull on the first board
        // (Igc/shipIGC.cpp:3619-3633). A boarded ship has no hardware to sell.
        if (boarder.Dead || carrier.Dead
            || carrier.ParentShip is not null || boarder.Mission != carrier.Mission
            || boarder.SideId != carrier.SideId) return false;

        // pfsShipBoard->IsPlayer() (FedSrv.CPP:2340) — the carrier must be a
        // peer-owned (player) ship in this port.
        if (!_peerShips.Any(kv => ReferenceEquals(kv.Value.Ship, carrier)))
        {
            ConsoleLog.Write($"[boardship] ship {boarder.Id} -> {carrier.Id}: carrier is not a player ship (FedSrv.CPP:2340) — refused");
            return false;
        }

        // Both docked: the native precondition (FedSrv.CPP:2342-2345).
        if (!ReferenceEquals(boarder.ParentShip, carrier)
            && (boarder.StationId is null || carrier.StationId is null))
        {
            ConsoleLog.Write($"[boardship] ship {boarder.Id} -> {carrier.Id}: boarder and carrier must both be docked (FedSrv.CPP:2342-2345) — refused");
            return false;
        }

        // Turret-slot search (FedSrv.CPP:2347-2402); null = observer.
        short? turret = ChooseTurretSlot(boarder, carrier);
        // Observer gate collapsed (no cheat/admin concept) — proceed regardless.

        // Defensive detach of the boarder's own existing children
        // (FedSrv.CPP:2415-2420): anyone riding on the boarder must fend for
        // themselves before the boarder climbs aboard the carrier. Always empty
        // today (no nested boarding), kept per the task's defensive guard.
        var kids = boarder.ChildShips.ToArray();
        foreach (var kid in kids)
            PerformLeave(kid, boarder);

        // FedSrv.CPP:2426-2443: refund once to the player's wallet, side-wide notice.
        if (boarder.ParentShip is null) RefundHardware(boarder);

        if (boarder.ParentShip is { } previous && boarder.TurretId is { } previousSlot)
            previous.Weapons.FirstOrDefault(w => w.MountId == previousSlot)?.SetGunner(null);

        // SetParentShip + SetTurretID (FedSrv.CPP:2445-2446). Reparent cleanly
        // if the boarder was somehow already on another carrier, then register
        // as a child of the carrier.
        if (boarder.ParentShip is { } oldParent && !ReferenceEquals(oldParent, carrier))
            oldParent.RemoveChildShip(boarder);
        boarder.ParentShip = carrier;
        carrier.AddChildShip(boarder);
        boarder.TurretId = turret;

        // P109 — the turret weapon-gunner binding (native SetTurretID →
        // SetGunner, shipIGC.h:1405-1437): if the boarder occupies a real
        // turret slot (not an observer), bind it as the gunner of the
        // carrier's mounted weapon at that mount. The mount lookup reuses the
        // exact weapon→MountId mapping the P108 turret-slot walk already used
        // (MountedPart.MountId / Ship.Weapons[i].MountId), so no new
        // bookkeeping. An observer (turret null) binds nothing — it has no gun
        // to crew. Leave clears the binding (PerformLeave).
        if (turret is { } slot)
        {
            var turretWeapon = carrier.Weapons.FirstOrDefault(w => w.MountId == slot);
            if (turretWeapon is not null)
            {
                turretWeapon.SetGunner(boarder);
                Console.WriteLine($"[boardship] bound gunner ship {boarder.Id} to carrier {carrier.Id} turret weapon mount {slot}");
            }
            else
            {
                // A boarded turret slot with no realized weapon on it (the slot
                // check in ChooseTurretSlot looks at Parts, which may hold an
                // inert/other part). Bind nothing; occupancy stands.
                Console.WriteLine($"[boardship] turret slot {slot} on {carrier.Id} has no realized weapon — gunner binding skipped");
            }
        }

        // "No parts allowed on an observer or turret" (native SetParentShip with
        // no old parent terminates them, shipIGC.cpp:3619-3633).
        // RefundHardware consumed an unboarded ship's loadout; an existing
        // child already has no parts, as native SetParentShip requires.

        // If the carrier is docked, the boarder inherits its dock state
        // (FedSrv.CPP:2456-2457 SetStation(pstationBoard)).
        if (carrier.StationId is { } cs)
        {
            boarder.StationId = cs;
            boarder.LastDockTime = _simNow;   // P381: stamp the dock clock on the inheriting boarder too
        }

        Console.WriteLine($"[boardship] ship {boarder.Id} boarded {carrier.Id} as " +
            $"{(turret is null ? "observer (no turret slot)" : $"turret gunner slot {turret}")} — no ack, rides SHIP_STATUS refresh");
        return true;
    }

    /// <summary>
    /// P108 — the server-side leave action, the port of the internal C++
    /// <c>LeaveShip(CFSShip*, IshipIGC*)</c> (<c>FedSrv.CPP:2277-2320</c>) — the
    /// <em>function</em>, not the wire message (which it builds and sends at
    /// <c>FedSrv.CPP:2312-2316</c>). Called for the <c>sidParent == NA</c> leave
    /// and for the boarder's own children during <see cref=\"BoardShip\"/>.
    /// Returns true on success; false lets the caller send <c>BOARD_NACK</c>.
    /// <list type="bullet">
    /// <item><b>Gate: the carrier is not in a cluster</b> — native
    /// <c>pshipParent-&gt;GetCluster() == NULL</c> (<c>FedSrv.CPP:2285</c>);
    /// in this port a docked carrier is <c>StationId != null</c>.</item>
    /// <item><b>Clears parent/turret</b> and removes this ship from the
    /// carrier's child list (native <c>SetParentShip(NULL)</c>,
    /// <c>shipIGC.cpp:3677-3689</c> DeleteChildShip).</item>
    /// <item><b>Restores a hull</b> — the side's civilization's Lifepod hull
    /// (native <c>SetBaseHullType(lifepod)</c>, <c>FedSrv.CPP:2301-2302</c>),
    /// the same resolution the death-respawn slice uses.</item>
    /// <item><b>Broadcasts <c>LEAVE_SHIP(94)</c> to the whole side</b>
    /// (<c>FedSrv.CPP:2312-2316</c>, <c>sidChild</c> = the detaching ship's id).</item>
    /// </list>
    /// The <c>SHIP_STATUS</c> consequences (state back to docked, parent NA,
    /// hull = the lifepod) are derived in <see cref=\"MakeOwnShipStatus\"/> and
    /// reflected by the existing per-tick refresh.
    /// </summary>
    private bool PerformLeave(Ship boarder, Ship carrier)
    {
        if (carrier.StationId is null)
        {
            ConsoleLog.Write($"[leaveship] ship {boarder.Id} from {carrier.Id}: carrier not docked — leave refused (FedSrv.CPP:2285)");
            return false;
        }
        if (boarder.ParentShip is not { } parent || !ReferenceEquals(parent, carrier))
        {
            ConsoleLog.Write($"[leaveship] ship {boarder.Id} from {carrier.Id}: not actually boarded on it — leave refused");
            return false;
        }

        // Clear the parent/turret relationship (native SetParentShip(NULL)):
        // remove from the carrier's child list, then null our back-pointer.
        carrier.RemoveChildShip(boarder);
        boarder.ParentShip = null;

        // P109 — release the turret weapon-gunner binding (native
        // SetParentShip(NULL) → SetGunner(NULL) on the occupied mount,
        // shipIGC.cpp:3652,3681). Capture the slot before clearing it and
        // unbind the carrier's weapon there so the freed turret stops firing
        // for the departing gunner.
        if (boarder.TurretId is { } slot)
        {
            var turretWeapon = carrier.Weapons.FirstOrDefault(w => w.MountId == slot);
            turretWeapon?.SetGunner(null);
        }
        boarder.TurretId = null;

        // Restore the side's Lifepod hull (FedSrv.CPP:2301-2302).
        if (boarder.SideId is { } sideId && boarder.Mission is { } leaveMission)
        {
            var side = leaveMission.Sides.FirstOrDefault(s => s.Id == sideId);
            if (side is not null && leaveMission.Core.HullTypes.TryGetValue(side.Civilization.Lifepod, out var lp))
            {
                boarder.ChangeHull(lp);
            }
            else
            {
                Console.WriteLine($"[leaveship] ship {boarder.Id} side {sideId}: lifepod hull unresolvable — leaving as current hull");
            }
        }

        // LEAVE_SHIP(94) to the WHOLE side, naming the detaching ship
        // (FedSrv.CPP:2312-2316) — not to a single peer.
        if (boarder.SideId is { } sideId2)
        {
            var leave = new LeaveShip { SidChild = boarder.Id };
            byte[] packet = FedPacket.Pack(new[] { leave.Encode() });
            int recipients = 0;
            foreach (var kv in _peerShips)
                if (kv.Value.Ship.SideId == sideId2) { SendPeer(kv.Key, packet); recipients++; }
            Console.WriteLine($"[leaveship] ship {boarder.Id} left {carrier.Id} — LEAVE_SHIP(94) sidChild={boarder.Id} -> side {sideId2} ({recipients} peer(s))");
        }
        else
        {
            Console.WriteLine($"[leaveship] ship {boarder.Id} left {carrier.Id} — no side, LEAVE_SHIP(94) suppressed");
        }
        return true;
    }

    /// <summary>
    /// P111 — the <c>FM_C_RIPCORD_REQUEST(184)</c> handler, the port of
    /// <c>FedSrv.CPP:7715-7728</c> <c>case FM_C_RIPCORD_REQUEST</c>.
    /// Mirrors the native gate exactly (each sub-condition confirmed in
    /// <c>FedSrv.CPP:7717-7721</c>): only a mission-started, real-side,
    /// un-boarded, flying ship whose hull can ripcord may proceed. A request
    /// that fails any gate is <b>silently ignored</b> — native does <c>break</c>;
    /// there is no denial message for a gate violation (only a request that
    /// names a cluster with no valid anchor yields <c>RIPCORD_DENIED</c>, which
    /// is decided later, inside <c>RequestRipcord</c>).
    /// <list type="bullet">
    /// <item><b>Mission started.</b> <c>GetStage() != STAGE_STARTED</c> →
    /// ignored (<c>FedSrv.CPP:7717</c>).</item>
    /// <item><b>Real side.</b> <c>GetSide()-&gt;GetObjectID() &lt; 0</c> → ignored
    /// (<c>FedSrv.CPP:7717</c>). Mirrored as <c>SideId &lt; 0</c>.</item>
    /// <item><b>Not boarded.</b> <c>GetParentShip()</c> → ignored
    /// (<c>FedSrv.CPP:7718</c>). A boarded ship is not its own actor. (Native
    /// always has this gate live; there is no landing-dependency caveat — it
    /// is independent of cluster E.)</item>
    /// <item><b>Flying.</b> <c>GetCluster() == NULL</c> → ignored
    /// (<c>FedSrv.CPP:7719</c>). Native clears the cluster on dock
    /// (<c>shipIGC.h:457,459</c>), so a docked ship has no cluster. In this port
    /// a docked ship retains its <c>Sector</c> but has <c>StationId != null</c>
    /// (the P103/P106 convention), so "flying" = <c>Sector != null &amp;&amp;
    /// StationId == null</c>.</item>
    /// <item><b>Not a flag-carrier.</b> <c>GetFlag() != NA</c> → ignored
    /// (<c>FedSrv.CPP:7720</c>). This port tracks no flags (the same
    /// always-passing gate-collapse precedent P100/P107 used); phase 4 /
    /// <c>GAIN_FLAG</c> territory.</item>
    /// <item><b>Hull can ripcord.</b> <c>GetBaseHullType()-&gt;HasCapability(
    /// c_habmNoRipcord)</c> → ignored (<c>FedSrv.CPP:7721</c>). <c>c_habmNoRipcord
    /// = 0x20</c> (<c>igc.h:731</c>), read like P107 read <c>c_habmLifepod</c>.</item>
    /// </list>
    /// Runs on the sim thread (the dispatch drains inside <see cref="Tick"/>),
    /// so it may mutate <c>_peerShips</c>/ships and send without locking.
    /// </summary>
    private void HandleRipcordRequest(ConnectionId from, LiveShip live, RipcordRequest req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;

        if (mission is null || mission.Stage != MissionStage.Started)
        {
            ConsoleLog.Write($"[ripcord] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — request ignored (gate FedSrv.CPP:7717)");
            return;
        }
        if (ship.SideId is not { } side || side < 0)
        {
            ConsoleLog.Write($"[ripcord] {from} ship {ship.Id}: side {ship.SideId?.ToString() ?? "(none)"} < 0 — request ignored (gate FedSrv.CPP:7717)");
            return;
        }
        if (ship.ParentShip is not null)
        {
            ConsoleLog.Write($"[ripcord] {from} ship {ship.Id}: boarded (parent {ship.ParentShip.Id}) — request ignored (gate FedSrv.CPP:7718)");
            return;
        }
        if (ship.Sector is null || ship.StationId is not null)
        {
            ConsoleLog.Write($"[ripcord] {from} ship {ship.Id}: {(ship.Sector is null ? "no cluster" : "docked")} — request ignored (gate GetCluster(), FedSrv.CPP:7719)");
            return;
        }
        if (ship.HullType.HasCapability(DataHullTypeIGC.HabmNoRipcord))
        {
            ConsoleLog.Write($"[ripcord] {from} ship {ship.Id}: hull {ship.HullType.HullId} has c_habmNoRipcord — request ignored (gate FedSrv.CPP:7721)");
            return;
        }
        // Flag-carrier gate (FedSrv.CPP:7720) collapses to always-passing: this
        // port tracks no flags. Documented, not silently dropped.

        // Native resolves the requested sector via
        // GetIGCMission()->GetCluster(sidRipcord), which returns NULL for an
        // unknown id — and RequestRipcord treats pcluster==NULL exactly like
        // sidRipcord==NA (a cancel). Mirror that so a bogus sector id behaves
        // as a cancel, never as a wildcard.
        var target = mission.Sectors.FirstOrDefault(s => s.Id == req.SidRipcord);
        RequestRipcord(from, ship, target);
    }

    /// <summary>
    /// P111 — the port of <c>FedSrvSiteBase::RequestRipcord</c>
    /// (<c>FedSrv.CPP:13319-13378</c>), the real countdown-start state machine
    /// a client <c>RIPCORD_REQUEST</c> reaches after the handler gate.
    /// <paramref name="target"/> is the requested target sector (null when the
    /// request named <c>NA</c> or an unknown sector id). Three outcomes:
    /// <list type="number">
    /// <item><b>Cancel</b> — <c>target == null</c> with an active ripcord: clear
    /// the model and broadcast <c>RIPCORD_ABORTED(187)</c> sector-wide to
    /// everyone flying in the requester's current cluster (<c>FedSrv.CPP:13327-13339</c>).</item>
    /// <item><b>(Re)activate</b> — a valid anchor is found in the requested
    /// sector via <see cref="FindRipcordModel"/>: set the model, re-seed the
    /// countdown (<c>ResetRipcordTimeLeft</c>), and send
    /// <c>RIPCORD_ACTIVATE(185)</c> — <em>sector-wide</em> on a first activation
    /// (<c>pmodelOld == NULL</c>) or to the <em>requester alone</em> on a
    /// re-target (<c>FedSrv.CPP:13366-13368</c>). The <c>sidRipcord</c> field is
    /// the <b>requested</b> cluster id (<c>pcluster-&gt;GetObjectID()</c>,
    /// <c>FedSrv.CPP:13364</c>), not the anchor's own sector.</item>
    /// <item><b>Deny</b> — no valid anchor: <c>RIPCORD_DENIED(186)</c> to the
    /// requester alone (<c>FedSrv.CPP:13371-13376</c>).</item>
    /// </list>
    /// If the ship is already anchored in the requested sector the request is
    /// a no-op (the outer <c>else if</c> of <c>FedSrv.CPP:13341-13342</c>).
    /// </summary>
    private void RequestRipcord(ConnectionId from, Ship ship, Sector? target)
        => RequestRipcordCore(ship, target, from);

    /// <summary>
    /// P172 — the connection-agnostic core of <see cref="RequestRipcord"/> (the
    /// P111 countdown-start state machine, port of
    /// <c>FedSrvSiteBase::RequestRipcord</c>, <c>FedSrv.CPP:13319-13378</c>).
    /// <paramref name="from"/> is the initiating peer's connection for a real-
    /// player request, or <c>null</c> for an NPC. <b>NPC-connection resolution</b>
    /// (the task's open question): an ownerless NPC has no <c>_peerShips</c> entry
    /// (P147 — <c>SpawnNpcShip</c> creates no peer; a stale <c>SendPeer</c> would
    /// throw "not connected" in the transport), so the requester-only
    /// <c>SendPeer</c> sends below are skipped when <paramref name="from"/> is
    /// <c>null</c>, while the sector-wide first-activation broadcast
    /// (<see cref="SendRipcordSectorWide"/>) still reaches any real peers flying
    /// in the ripper's sector. Same state machine, unchanged, for both paths;
    /// every downstream step (countdown, revalidation, teleport) is
    /// connection-agnostic, so the NPC ripcord completes exactly like a player's.
    /// </summary>
    private void RequestRipcordCore(Ship ship, Sector? target, ConnectionId? from)
    {
        var current = ship.RipcordModel;
        if (target is null)
        {
            // sidRipcord == NA (or unknown): cancel an active ripcord.
            if (current is not null)
            {
                ship.SetRipcordModel(null);
                SendRipcordSectorWide(ship.Sector!,
                    new RipcordAborted { ShipidRipcord = ship.Id }.Encode());
                Console.WriteLine($"[ripcord] ship {ship.Id} cancels -> RIPCORD_ABORTED(187) sector {ship.Sector!.Id} wide");
            }
            return;
        }

        if (current is { } cur && cur.ClusterId == target.Id)
            return;   // already anchored in this sector — nothing to do (FedSrv.CPP:13341-13342)

        var found = FindRipcordModel(ship, target);
        if (found is { } anchor)
        {
            bool firstActivation = current is null;
            ship.SetRipcordModel(anchor);
            ship.ResetRipcordTimeLeft();

            var msg = new RipcordActivate
            {
                ShipidRipcord = ship.Id,
                OtRipcord = anchor.Ot,
                OidRipcord = anchor.Oid,
                SidRipcord = target.Id,   // the REQUESTED cluster, FedSrv.CPP:13364
            };
            if (firstActivation)
            {
                SendRipcordSectorWide(ship.Sector!, msg.Encode());
                Console.WriteLine($"[ripcord] ship {ship.Id} FIRST activation anchor (ot={anchor.Ot} oid={anchor.Oid}) -> sector {target.Id} RIPCORD_ACTIVATE(185) sector {ship.Sector!.Id} wide");
            }
            else
            {
                if (from is { } f) SendPeer(f, FedPacket.Pack(new[] { msg.Encode() }));
                Console.WriteLine($"[ripcord] ship {ship.Id} RE-TARGET anchor (ot={anchor.Ot} oid={anchor.Oid}) -> sector {target.Id} RIPCORD_ACTIVATE(185) requester-only");
            }
        }
        else
        {
            if (from is { } f) SendPeer(f, FedPacket.Pack(new[] { new RipcordDenied().Encode() }));
            ConsoleLog.Write($"[ripcord] ship {ship.Id}: no valid anchor in sector {target.Id} -> RIPCORD_DENIED(186) requester-only");
        }
    }

    /// <summary>
    /// P172 — the <see cref="ServerHost.RequestRipcordReceived"/> handler: an
    /// <b>NPC</b>-initiated ripcord request routed up through the IIgcSite seam
    /// (<c>Ship.TryRipcordOrDefer</c> → <c>Mission.Site.RequestRipcord</c> →
    /// this handler). Enters the exact same <see cref="RequestRipcordCore"/>
    /// state machine with a <c>null</c> connection — the NPC-connection
    /// resolution (no <c>_peerShips</c> entry, so the requester-only sends are
    /// skipped; sector-wide broadcasts and the whole countdown/teleport chain are
    /// connection-agnostic). Runs on the sim thread (called from a ship's
    /// <c>OnUpdate</c> inside <c>TickSim</c>), same threading as every inbound
    /// handler. Runs on the sim thread, like the direct real-player path.
    /// </summary>
    private void OnNpcRequestRipcord(Ship ship, Sector? target)
        => RequestRipcordCore(ship, target, null);

    /// <summary>Send a pre-encoded server message to every peer currently
    /// flying in <paramref name="sector"/> — the port's
    /// <c>GetGroupSectorFlying(pclusterShip)</c> (<c>FedSrv.CPP:13338</c>), the
    /// sector-wide broadcast both the ripcord slices and the P117 relaunch
    /// (<c>FedSrv.CPP:13019</c>) use.</summary>
    private void SendSectorWide(Sector sector, byte[] encoded)
    {
        var packet = FedPacket.Pack(new[] { encoded });
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship.Sector, sector))
                SendPeer(kv.Key, packet);
    }

    /// <summary>
    /// P113 — the port of <c>FedSrvSiteBase::UseRipcord</c>
    /// (<c>FedSrv.CPP:13440-13500</c>), the actual relocation that runs when a
    /// ripcord countdown reaches zero with a still-legal anchor. Drained from
    /// <c>Sector.ResolveRipcordCountdowns</c> (via <c>DrainRipcordTeleports</c>)
    /// in the same <see cref="SimPass"/> slot the P112 abort broadcast uses —
    /// the sector pass detects the zero-cross and hands the ripper off here,
    /// exactly the sector-detects / host-acts split that governs the abort path.
    /// The anchor was just revalidated legal in the same tick, so resolving it
    /// here is a straight object lookup (no re-validation).
    /// <para>Three outcomes, mirroring the native function which always returns
    /// <c>true</c> (so the ripper's ripcord state is cleared — the attempt is
    /// <b>consumed, never retried on a later tick</b> even when it aborts):</para>
    /// <list type="number">
    /// <item><b>Station anchor</b> (<c>FedSrv.CPP:13441-13443</c>): native
    /// delegates to the station's own <c>Launch</c>, the exact mechanic the
    /// port already uses for undocking — so this calls the shared
    /// <see cref="RepositionForLaunch"/> (P69's launch core) and moves the
    /// ripper to the anchor station's cluster.</item>
    /// <item><b>Ship anchor with enough energy</b> (<c>FedSrv.CPP:13446-13474</c>):
    /// pay <c>RipcordCost</c> (the ripper's hull cost) from the target's energy,
    /// then the genuinely new ship-relative repositioning: <c>r = target.Radius +
    /// ripper.Radius + 25.0f</c> (the native ship-anchor margin),
    /// a <b>random</b> direction (<c>Vector::RandomDirection()</c>), the same
    /// <c>c_fcidExitStationSpeed</c> (slot 4, stationIGC.cpp:307) velocity (<c>c_fcidExitStationSpeed</c>),
    /// zeroed turn rates, and relocation to the target ship's cluster.</item>
    /// <item><b>Ship anchor without enough energy</b> (<c>FedSrv.CPP:13451-13461</c>):
    /// broadcast <c>RIPCORD_ABORTED(187)</c> to everyone flying in the ripper's
    /// cluster, do <b>not</b> move, and consume the attempt (state cleared) —
    /// native returns <c>true</c> either way, so it is not retried when the
    /// target later regains energy.</item>
    /// <item><b>Probe anchor</b> (<c>FedSrv.CPP:13461-13476</c>, P454): the
    /// same non-station placement as the ship anchor
    /// (<see cref="PlaceRipcordNearTarget"/> — probe.Radius + ripper.Radius +
    /// 25, random direction, <c>ExitStationSpeed</c>, zeroed turn rates, move
    /// to the probe's cluster) but with <b>no energy cost</b>: the RipcordCost
    /// gate lives inside the OT_ship branch
    /// (<c>FedSrv.CPP:13452-13460</c>), so a probe anchor pays nothing.</item>
    /// </list>
    /// <see cref="ShipStatusSpotted"/> (the native's trailing status refresh,
    /// <c>FedSrv.CPP:13499</c>) has no real equivalent in this port — P104 left
    /// <c>OBJECT_SPOTTED</c>(6) wire-only/untriggered — so it is intentionally
    /// skipped (finding stated in the P113 report), exactly as the task allows.
    /// Runs on the sim thread (the drain sits in <see cref="SimPass"/>).
    /// </summary>
    private void PerformRipcordTeleport(Ship rip)
    {
        var mission = rip.Mission;
        var anchor = rip.RipcordModel;
        if (mission is null || anchor is null)
        {
            rip.SetRipcordModel(null);
            return;
        }

        var a = anchor.Value;
        var targetSector = mission.Sectors.FirstOrDefault(s => s.Id == a.ClusterId);

        if (a.Ot == (short)IgcObjectType.Station)
        {
            // Station anchor: UseRipcord delegates to the station's Launch
            // (FedSrv.CPP:13441-13443) — reuse the undock-launch core, then move
            // to the station's cluster (SetCluster(pcluster), FedSrv.CPP:13474-13476).
            DataStationIGC? station = null;
            if (targetSector is not null)
                foreach (var st in targetSector.Stations)
                    if (st.StationId == a.Oid) { station = st; break; }

            if (station is null || targetSector is null)
            {
                // Anchor vanished between revalidation and drain (a capture/
                // destruction race). Native's UseRipcord returns true regardless,
                // so consume the attempt without relocating.
                rip.SetRipcordModel(null);
                return;
            }

            RepositionForLaunch(rip, station.Value);
            MoveShipToSector(rip, targetSector);
            // P75-style re-dock debounce: the ripper arrives just outside the
            // anchor station's DockRadius; the launch-cooldown latch stops an
            // accidental re-dock on the very next tick, mirroring PerformUndock.
            rip.LastLaunchTime = _simNow;
        }
        else if (a.Ot == (short)IgcObjectType.Ship)
        {
            // Ship anchor: resolve the target ship in its cluster.
            Ship? target = null;
            if (targetSector is not null)
                foreach (var m in targetSector.Models)
                    if (m is Ship s && s.Id == a.Oid) { target = s; break; }

            if (target is null || targetSector is null)
            {
                rip.SetRipcordModel(null);
                return;
            }

            // Energy gate (FedSrv.CPP:13452-13460): the TARGET ship must cover
            // the RIPPER's RipcordCost (pship->GetBaseHullType()->GetRipcordCost()).
            float ripcordCost = rip.HullType.RipcordCost;
            float newEnergy = target.Energy - ripcordCost;
            if (newEnergy < 0.0f)
            {
                // Insufficient energy: broadcast RIPCORD_ABORTED(187) to everyone
                // flying in the RIPPER's cluster (native "broadcast ... to everyone
                // flying in pship's cluster", FedSrv.CPP:13458-13460), do NOT move,
                // and consume the attempt (return true -> state cleared). Never
                // retried when the target later regains energy.
                var ripSec = rip.Sector;
                if (ripSec is not null)
                    SendRipcordSectorWide(ripSec,
                        new RipcordAborted { ShipidRipcord = rip.Id }.Encode());
                rip.SetRipcordModel(null);
                Console.WriteLine($"[ripcord] ship {rip.Id} ripcord to ship-anchor {target.Id} ABORTED — target energy {target.Energy} < RipcordCost {ripcordCost}; attempt consumed (RIPCORD_ABORTED(187) -> ripper sector {ripSec?.Id.ToString() ?? "?"} wide)");
                return;
            }
            target.Energy = newEnergy;

            // Ship-relative reposition (FedSrv.CPP:13461-13474): NOT station-
            // relative — r = target.Radius + ripper.Radius + 25.0f (literal 25,
            // distinct from the station launch-slot offsets), a random direction,
            // ExitStationSpeed velocity, zeroed turn rates, and move to the
            // target's cluster. Shared with the probe branch via
            // <see cref="PlaceRipcordNearTarget"/>.
            PlaceRipcordNearTarget(rip, target.Position, ShipRadius(target), targetSector);
            Console.WriteLine($"[ripcord] ship {rip.Id} ripcord -> ship-anchor {target.Id} at r={ShipRadius(target) + ShipRadius(rip) + 25.0f:F1} (cost {ripcordCost} energy paid from target, now {target.Energy}) in sector {targetSector.Id}");
        }
        else if (a.Ot == (short)IgcObjectType.Probe)
        {
            // P454 — probe anchor: native treats a probe exactly like the
            // ship-anchor "non-station" case (UseRipcord's else, FedSrv.CPP:
            // 13462-13476 — there is no probe-specific branch; a probe is just
            // not a station), with ONE difference: the energy gate is inside
            // the OT_ship branch (FedSrv.CPP:13452-13460), so a PROBE anchor
            // pays no RipcordCost — the cost applies to ship anchors only.
            Probe? target = null;
            if (targetSector is not null)
                foreach (var m in targetSector.Models)
                    if (m is Probe pr && pr.Id == a.Oid) { target = pr; break; }

            if (target is null || targetSector is null)
            {
                rip.SetRipcordModel(null);
                return;
            }

            // Non-station placement shared with the ship branch: r =
            // probe.Radius + ripper.Radius + 25, random direction,
            // ExitStationSpeed velocity, zeroed turn rates, move to the
            // probe's cluster. No energy cost (see above).
            PlaceRipcordNearTarget(rip, target.Position, target.ProbeType.Radius, targetSector);
            Console.WriteLine($"[ripcord] ship {rip.Id} ripcord -> probe-anchor {target.Id} at r={target.ProbeType.Radius + ShipRadius(rip) + 25.0f:F1} (no energy cost) in sector {targetSector.Id}");
        }
        else
        {
            // Unknown anchor type — impossible per FindRipcordModel's tiers,
            // but consume the attempt defensively rather than leave state active.
            rip.SetRipcordModel(null);
            return;
        }

        // Successful teleport: clear the ripcord state (the native's
        // SetRipcordModel(NULL) once UseRipcord returns true, shipIGC.cpp:293).
        rip.SetRipcordModel(null);
        UpdatePeerSectorSnapshot(rip, targetSector);
    }

    /// <summary>
    /// P115 — the port of <c>FedSrvSiteBase::RescueShipEvent</c>
    /// (<c>FedSrv.CPP:13033-13074</c>): a <c>c_habmLifepod</c> ship that drifted
    /// within proximity of a friendly <c>c_habmRescue</c>-capable ship is
    /// rescued — <c>PLAYER_RESCUED</c>(16) to both ships' connections, then the
    /// rescued ship is placed at the nearest friendly station. Queued by
    /// <c>Sector.ResolveRescueEvents</c> and drained here on the sim thread
    /// right after <c>Mission.Update</c> (the same accumulate/drain split as the
    /// ripcord teleport, P113). The pair (rescuee, rescuer) is already validated
    /// by the pass — same-side, exactly-one-is-the-lifepod, the other is
    /// rescue-capable, within proximity — so this method only performs the
    /// wire + placement halves of the native event.
    /// </summary>
    /// <remarks>
    /// <para><b>Score / Steam — N/A, skipped.</b> Native awards rescue
    /// achievements before the wire (<c>AddRescue</c>/<c>AwardPodPickup</c>/
    /// <c>AwardGetRescued</c>, <c>FedSrv.CPP:13039-13051</c>). This port has
    /// <b>no score system</b> (<c>PlayerInfo.cs:207</c> throws on non-empty
    /// <c>rgPersistPlayerScores</c> — "score history not supported") and no
    /// Steam integration anywhere, so both are intentionally skipped, the same
    /// precedent every score/Steam-adjacent gap this arc has already hit
    /// (P104's trigger deferral named the same absence).</para>
    /// <para><b>ResetWarpState() — no real equivalent needed here.</b> The
    /// native re-arms the rescued ship's <c>m_warpState = warpReady</c>
    /// (<c>fsship.h:199-201</c>) before docking it. This port's warp-cooldown
    /// stand-in (<c>Ship.LastWarpTime</c> / <c>Sector.WarpCooldownSeconds</c>,
    /// the collapsed <c>warpNoUpdate→warpWaiting→warpReady</c> machine,
    /// <c>Ship.cs:123-135</c>) is consulted only by the two proximity passes —
    /// <c>ResolveWarpCollisions</c> (<c>Sector.cs:1017</c>) and
    /// <c>ResolveDockCollisions</c> (<c>Sector.cs:1255</c>) — as their re-warp /
    /// re-dock debounce. This rescue places the rescuee by <b>direct</b> dock
    /// (below), which never enters those passes, so no warp-readiness gate
    /// stands between the rescue and its dock. Clearing <c>LastWarpTime</c>
    /// would only prematurely drop a legitimate 2 s re-warp debounce with no
    /// benefit to the direct placement. Conclusion: the existing cooldown
    /// already serves the native <c>warpState</c>'s debounce purpose, and no
    /// equivalent call is required here.</para>
    /// <para><b>Station search</b> — reuses P105's <see cref="FindFriendlyStation"/>
    /// (<c>Server.cs:4177</c>), the port's established stand-in for the native
    /// <c>FindTarget(..., c_ttStation | c_ttFriendly | c_ttAnyCluster |
    /// c_ttNearest, ..., c_sabmRestart)</c> nearest-friendly-station search
    /// (<c>FedSrv.CPP:13065-13066</c>). It already returns a same-side station
    /// (any friendly match, first-found — the port's documented simplification
    /// of "nearest"), so the native's same-side fallback
    /// (<c>pstation->GetSide() != pshipRescued's side → GetBase()</c>,
    /// <c>FedSrv.CPP:13067-13071</c>) is inherently satisfied — a same-side
    /// station is never discarded. When no friendly station exists anywhere,
    /// the native returns <c>false</c> with no dock (<c>FedSrv.CPP:13075</c>);
    /// mirrored (no placement, wire already sent).</para>
    /// <para><b>Placement</b> — reuses <c>PerformTeleport</c>'s relocation core
    /// (<c>Server.cs:3284-3299</c>, P110): move the rescuee to the station's
    /// cluster, latch <c>Ship.StationId</c> (and any boarded children), and
    /// point the owning peer's <c>LiveShip.Sector</c> snapshot at the station's
    /// cluster so the next SHIP_STATUS/SINGLE_SHIP_UPDATE read the right
    /// sector. Like the P110 teleport path, no <c>DockWithStationEvent</c>
    /// seam / <c>FM_S_DOCKED</c>(61) is raised here (the direct-dock latch is
    /// enough; the docked state is conveyed by the periodic SHIP_STATUS).</para>
    /// </remarks>
    private void PerformRescue(Ship rescuee, Ship rescuer)
    {
        var mission = rescuee.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[rescue] ship {rescuee.Id} (rescued by {rescuer.Id}) has no mission — rescue dropped");
            return;
        }

        // P583 — award the RESCUER a rescue (+<c>c_fcidPointsRescues</c>, 5 on
        // PCore15). Native calls <c>AddRescue()</c> on the rescuer's score
        // object before the wire (<c>FedSrv.CPP:13039</c>), for player and bot
        // rescuers alike. Here the rescuer's score is stored only when it is a
        // live peer (a connected MCP/bench bot included); an ownerless NPC
        // rescuer (SpawnNpcShip) has no score object to credit — listed as a
        // gap in docs/reports/P583_NATIVE_SCORING.md.
        if (LiveOf(rescuer)?.PlayerScore is { } rescuerScore)
        {
            rescuerScore.AddRescue();
            Console.WriteLine($"[scoring] rescuer ship {rescuer.Id} rescue +1 (player peer)");
        }

        // 1) Wire: PLAYER_RESCUED(16) to BOTH the rescuer's and the rescuee's
        // connections (native pfmPlayerRescued sent to both, FedSrv.CPP:13056-13065).
        // Peer-resolution doubles as the native's "if IsPlayer" gate: only a
        // peer-owned ship has a connection to send to; a non-peer ship's send is
        // skipped (it may still be placed below).
        var rescueMsg = new PlayerRescued { ShipIdRescuer = rescuer.Id, ShipIdRescuee = rescuee.Id };
        byte[] packet = FedPacket.Pack(new[] { rescueMsg.Encode() });
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, rescuee) || ReferenceEquals(kv.Value.Ship, rescuer))
                SendPeer(kv.Key, packet);

        // 2) Nearest friendly station, else no placement (FedSrv.CPP:13065-13075).
        var station = FindFriendlyStation(rescuee);
        if (station is null)
        {
            Console.WriteLine($"[rescue] ship {rescuee.Id} rescued by {rescuer.Id}: no friendly station anywhere — PLAYER_RESCUED(16) sent to {packet.Length}-byte packet but no placement (native returns false, FedSrv.CPP:13075)");
            return;
        }

        _podTrips.End(rescuee, _simNow, "rescued");

        // 3) Place the rescuee at the station — PerformTeleport's relocation
        // core (Server.cs:3284-3299): move to the station's cluster and latch
        // docked. FindFriendlyStation already guarantees same-side, so the
        // native's same-side fallback is satisfied.
        var destSector = mission.Sectors.First(s => s.Stations.Contains(station.Value));
        MoveShipToSector(rescuee, destSector);   // P307: real list move (old.Remove → dest.Add → Sector), not just the back-ref
        foreach (var kid in rescuee.ChildShips)   // empty for a solo lifepod; mirrored for parity
            kid.StationId = station.Value.StationId;
        rescuee.StationId = station.Value.StationId;
        foreach (var kid in rescuee.ChildShips) kid.LastDockTime = _simNow;   // P381: dock clock
        rescuee.LastDockTime = _simNow;                                       // P381: dock clock

        // Keep the owning peer's broadcast bookkeeping coherent — SHIP_STATUS
        // frames derive Sector from the LiveShip snapshot, so point it at the
        // station's cluster like PerformTeleport (Server.cs:3295) and the P105
        // respawn (Server.cs:4152) do.
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, rescuee))
                _peerShips[kv.Key] = kv.Value with { Sector = destSector };

        var note = $"[rescue] ship {rescuee.Id} (lifepod) rescued by ship {rescuer.Id} -> PLAYER_RESCUED(16) rescuer={rescuer.Id}/rescuee={rescuee.Id}; placed docked at station {station.Value.StationId} '{station.Value.Name}' in sector '{destSector.Name}'";
        Console.WriteLine(note);
        Notify(note);
    }

    /// <summary>
    /// P301 — the port of <c>FedSrvSiteBase::RescueShipEvent</c> for the
    /// station-rescuer case (<c>FedSrv.CPP:13033-13074</c> with
    /// <c>pshipRescuer == NULL</c>), driven by
    /// <c>Sector.ResolveStationRescueEvents</c> (via <c>DrainStationRescues</c>).
    /// A drifting <c>c_habmLifepod</c> that reached a same-side station whose
    /// type carries <c>c_sabmRescue</c> is docked at that station — this is the
    /// real native mechanic behind native's OT_station collision branch
    /// (<c>shipIGC.cpp:862-867</c>) and the counterpart to the P115 ship-ship
    /// rescue (<see cref="PerformRescue"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>No wire.</b> Native's NULL-rescuer branch performs the placement
    /// with <b>no</b> <c>PLAYER_RESCUED</c> message and no score/Steam award —
    /// the whole wire block (including the rescuer connection send) sits inside
    /// <c>if (pshipRescuer)</c> (<c>FedSrv.CPP:13037-13057</c>). A station is
    /// not a ship with a connection or a score, so none of that applies. This
    /// method therefore only does the placement half: dock the pod at the
    /// station.</para>
    /// <para><b>Placement / rescue-point locality.</b> Native then finds the
    /// nearest friendly <c>c_sabmRestart</c> station and docks there
    /// (<c>FedSrv.CPP:13060-13076</c>). The pod is, by construction of the
    /// detection pass, physically AT this same-side <c>c_sabmRescue</c> station
    /// — a friendly, reachable base — so docking it at that very station is the
    /// local, faithful equivalent (the native search would resolve to a nearby
    /// friendly restart base in practice). Docking latches <c>Ship.StationId</c>,
    /// which is exactly what cancels the P279 oxygen timer
    /// (<c>Ship.OnUpdate</c>'s <c>StationId is null</c> conjunct,
    /// <c>Ship.cs:5538</c>), so a rescued pod stops burning oxygen.</para>
    /// <para><b>Ship-station idempotence.</b> The dock pass latches
    /// <c>StationId</c>, so a pod re-detected on a later tick is re-docked to
    /// the same station harmlessly (same as PerformRescue's direct dock).</para>
    /// </remarks>
    private void PerformStationRescue(Ship rescuee, Station station)
    {
        var mission = rescuee.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[rescue] lifepod ship {rescuee.Id} reached station {station.StationId} but has no mission — station rescue dropped");
            return;
        }

        _podTrips.End(rescuee, _simNow, "station-rescued");

        // Dock the pod at the rescue station — the same direct-dock relocation
        // core PerformRescue/PerformTeleport use (move to the station's sector,
        // latch StationId for the pod and any boarded children, and point the
        // owning peer's LiveShip.Sector snapshot at the station's cluster so the
        // next SHIP_STATUS/SINGLE_SHIP_UPDATE read the right sector).
        var destSector = mission.Sectors.First(s => s.Stations.Contains(station.Data));
        MoveShipToSector(rescuee, destSector);   // P307: real list move (old.Remove → dest.Add → Sector), not just the back-ref
        foreach (var kid in rescuee.ChildShips)
            kid.StationId = station.StationId;
        rescuee.StationId = station.StationId;
        foreach (var kid in rescuee.ChildShips) kid.LastDockTime = _simNow;   // P381: dock clock
        rescuee.LastDockTime = _simNow;                                       // P381: dock clock
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, rescuee))
                _peerShips[kv.Key] = kv.Value with { Sector = destSector };

        var note = $"[rescue] lifepod ship {rescuee.Id} rescued by station {station.StationId} '{station.Data.Name}' (c_sabmRescue) -> docked; no PLAYER_RESCUED (native NULL-rescuer, FedSrv.CPP:13037); oxygen timer cancelled by dock";
        Console.WriteLine(note);
        Notify(note);
    }

    /// <summary>
    /// P123 — the cash-treasure pickup, drained from
    /// <c>Sector.ResolveTreasurePickups</c> (via <c>DrainTreasurePickups</c>).
    /// The port of the <c>c_tcCash</c> branch of <c>HitTreasureEvent</c>
    /// followed by <c>KillTreasureEvent</c> (<c>FedSrv.CPP:13589-13616</c>,
    /// <c>12493-12509</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Pooled-money simplification — confirmed and applied.</b> The
    /// native splits the cash credit by whether the picking ship is a player:
    /// a player credits its autodonate target's per-player wallet and
    /// broadcasts <c>MONEY_CHANGE</c> to the whole side's peer group
    /// (<c>FedSrv.CPP:13591-13611</c>, <c>pfsDonate-&gt;SetMoney(...)</c> with
    /// <c>sidTo = pshipDonate</c>, <c>sidFrom = NA</c>); a non-player calls
    /// <c>m_pfsMission-&gt;GiveSideMoney(ship-&gt;GetSide(), Money(amount))</c>
    /// (<c>FedSrv.CPP:13612-13615</c>). This port has no per-player wallets and
    /// no autodonate mechanism — it tracks ONE pooled number per side
    /// (<see cref="Side.Money"/>, Model/Side.cs:52, P118) — so the two native
    /// branches <b>collapse to the same side-pooled credit</b>: credit the
    /// picking ship's side by <c>amount</c>, then broadcast
    /// <c>MONEY_CHANGE</c> side-wide via P118's
    /// <see cref="BroadcastMoneyChangeSideWide"/> with <c>sidTo = the picking
    /// ship</c> (the autodonate target degenerates to the ship itself since
    /// there is no autodonate), <c>sidFrom = NA</c> (server-initiated pooled
    /// credit). Both the \"player\" and \"non-player\" native branches thus do
    /// the same thing here.</para>
    /// <para><b>Destroy half.</b> Native then (because <c>HitTreasureEvent</c>
    /// returns true) runs <c>KillTreasureEvent</c>
    /// (<c>FedSrv.CPP:12493-12509</c>): broadcast <c>DESTROY_TREASURE</c>
    /// (<c>treasureID</c> = the treasure's object id, <c>sectorID</c> = its
    /// cluster id) <b>whole-mission</b> to <c>GetGroupRealSides()</c> — every
    /// peer on every side, <em>not</em> just the picking side — and terminate
    /// the treasure (removed from its sector). The flag respawn leg is
    /// explicitly out of scope (Phase 6 capture-the-flag).</para>
    /// <para><b>Not wired this slice:</b> the per-player <c>ACQUIRE_TREASURE</c>
    /// notice (<c>FedSrv.CPP:13633-13641</c>, to the picking player's own
    /// connection) — its codec is built (<see cref="Allegiance.Core.Wire.AcquireTreasure"/>)
    /// but the broadcast set is kept to the task's enumerated
    /// <c>MONEY_CHANGE</c> + <c>DESTROY_TREASURE</c>; wiring it is a one-line
    /// follow-up (see the P123 report).</para>
    /// </remarks>
    private void PerformTreasurePickup(Ship ship, Treasure treasure)
    {
        var mission = ship.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[treasure] ship {ship.Id} picked treasure {treasure.Id} but has no mission — pickup dropped");
            return;
        }

        // P201 — flag-treasure pickup, the port of the c_tcFlag case at the top
        // of HitTreasureEvent (FedSrv.CPP:13562-13575). The treasure's
        // TreasureId IS the ORIGINAL side whose flag this is (native
        // `oid = treasure->GetTreasureID()` then `m_sidFlag = oid`,
        // shipIGC.h:1349). Eligibility (not already carrying, not own-side) is
        // enforced by the sector pickup pass (Sector.ResolveTreasurePickups)
        // — a refused pickup is never queued, so it never reaches here (native
        // returns false → no destroy). Set the carrier's Flag, announce
        // whole-mission (native SendMessages(GetGroupRealSides()),
        // FedSrv.CPP:13573), then destroy the treasure (native returns true →
        // KillTreasureEvent → DESTROY_TREASURE, the same destroy cash uses
        // below). `treasure->SetTreasureID(NA)` (FedSrv.CPP:13576, to stop the
        // respawn) collapses to removing the treasure, which
        // BroadcastTreasureDestroyed does.
        if (treasure.Code == TreasureCode.Flag)
        {
            short oid = treasure.Data.TreasureId;
            ship.Flag = oid;
            BroadcastGainFlag(mission, oid, ship.Id, bIsTreasureDocked: false, parttypeidDocked: 0);
            BroadcastTreasureDestroyed(treasure);
            Console.WriteLine($"[flag] ship {ship.Id} picked up side {oid}'s flag -> GAIN_FLAG(156) whole-mission + DESTROY_TREASURE");
            return;
        }

        // P145 — part-treasure pickup (the FM_C_TREASURE_ACK offer leg). Port of
        // the c_tcPart branch of HitTreasureEvent (FedSrv.CPP:13604-13641):
        //   • Buffer the offer on the ship ONLY if none is already pending
        //     ("As long as either it isn't a part or there is space in the
        //     treasure buffer", FedSrv.CPP:13604-13607); a second touch while one
        //     is pending is silently NOT offered.
        //   • When offered, send ACQUIRE_TREASURE to the picking player's OWN
        //     connection only (SendMessages(pfsPlayer->GetConnection(), ...),
        //     FedSrv.CPP:13639-13641) — the first real send of the P123 codec.
        //   • REGARDLESS of the buffer state, destroy the treasure (native
        //     KillTreasureEvent always fires on any successful HitTreasureEvent
        //     return — Step 1's critical finding; the touched treasure is gone
        //     before the player ever chooses a mount, part or cash alike).
        // The part-type id buffered is Treasure.Data.TreasureId (for a part
        // treasure that is the part's PartId, CreateDropTreasure FedSrv.CPP:4369).
        if (treasure.Code == TreasureCode.Part)
        {
            if (ship.PendingTreasureOffer is null)
            {
                ship.PendingTreasureOffer = ((short)treasure.Data.TreasureId, (short)treasure.Amount);
                var acquire = new AcquireTreasure
                {
                    TreasureCode = (byte)TreasureCode.Part,
                    TreasureId = (short)treasure.Data.TreasureId,
                    Amount = (short)treasure.Amount,
                };
                var owner = _peerShips.FirstOrDefault(kv => ReferenceEquals(kv.Value.Ship, ship));
                if (owner.Key != default)
                {
                    SendPeer(owner.Key, FedPacket.Pack(new[] { acquire.Encode() }));
                    Console.WriteLine($"[treasure] ship {ship.Id} offered part {treasure.Data.TreasureId} amount={treasure.Amount} -> ACQUIRE_TREASURE(162) to its own connection");
                }
                else
                {
                    Console.WriteLine($"[treasure] ship {ship.Id} picked part treasure {treasure.Id} but has no connection — offer buffered, ACQUIRE_TREASURE not sent");
                }
            }
            else
            {
                Console.WriteLine($"[treasure] ship {ship.Id} already has a pending offer ({ship.PendingTreasureOffer.Value.PartTypeId}x{ship.PendingTreasureOffer.Value.Amount}) — second part-touch {treasure.Id} NOT offered (native gate, FedSrv.CPP:13604) but still destroyed");
            }
            BroadcastTreasureDestroyed(treasure);   // KillTreasureEvent always fires (FedSrv.CPP:13627-13628 → 12493-12509)
            return;
        }

        // P412 — powerup-treasure pickup (c_tcPowerup). The treasure's
        // TreasureId is a PowerupCode bitmap; apply every set flag
        // (shipIGC.h:1326-1346: `if (objectID & c_pcFuel) SetFuel(FLT_MAX)` …),
        // then destroy (native HitTreasureEvent returns true →
        // KillTreasureEvent → DESTROY_TREASURE, same as cash/part/flag).
        if (treasure.Code == TreasureCode.Powerup)
        {
            int bits = treasure.Data.TreasureId;
            ship.ApplyPowerup(bits);
            Console.WriteLine($"[treasure] ship {ship.Id} picked powerup treasure {treasure.Id} bits=0x{bits:X2} -> fuel {ship.Fuel:F1}/{ship.HullType.MaxFuel:F1}");
            BroadcastTreasureDestroyed(treasure);
            return;
        }

        if (ship.SideId is not { } sideId)
        {
            Console.WriteLine($"[treasure] ship {ship.Id} picked cash treasure {treasure.Id} but has no side — pickup dropped");
            return;
        }
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null)
        {
            Console.WriteLine($"[treasure] side {sideId} not found in mission — pickup dropped");
            return;
        }

        int amount = treasure.Amount;

        // P551 — the port of the c_tcCash branch of HitTreasureEvent
        // (FedSrv.CPP:13589-13616). Native splits by player vs NPC:
        //   • PLAYER picker (FedSrv.CPP:13592-13611): credit the wallet of the
        //     picker's AUTODONATE target, or the picker's OWN wallet when there
        //     is no autodonate target (`pshipDonate = ship->GetAutoDonate();
        //     if (pshipDonate == NULL) pshipDonate = ship;`), with the
        //     MONEY_CHANGE sidTo = that target's ship (FedSrv.CPP:13605-13606)
        //     — NOT the side pot. This fixes the old port's client/server
        //     mismatch (side.Money += amount while broadcasting sidTo = picker).
        //   • NPC picker (FedSrv.CPP:13612-13615): pooled credit to the side
        //     (GiveSideMoney) with no per-player MONEY_CHANGE.
        var pickerLive = _peerShips.Values.FirstOrDefault(l => ReferenceEquals(l.Ship, ship));
        if (pickerLive is not null)
        {
            Ship target = pickerLive.Wallet.AutoDonateTarget ?? ship;   // FedSrv.CPP:13594-13596
            var targetLive = LiveOf(target) ?? pickerLive;
            targetLive.Wallet.Money = (int)Math.Clamp((long)targetLive.Wallet.Money + amount, 0, int.MaxValue);                          // pfsDonate->SetMoney(+amount), FedSrv.CPP:13609
            Console.WriteLine($"[treasure] ship {ship.Id} (player) picked cash treasure {treasure.Id} amount={amount} -> wallet of ship {target.Id} now {targetLive.Wallet.Money}");
            BroadcastMoneyChangeSideWide(ship.Mission!, sideId, amount, target.Id);    // sidTo = target, sidFrom = NA (FedSrv.CPP:13605-13606, side group)
        }
        else
        {
            side.Money += amount;                                       // GiveSideMoney (FedSrv.CPP:13614)
            Console.WriteLine($"[treasure] ship {ship.Id} (npc) picked cash treasure {treasure.Id} amount={amount} -> side {sideId} money {side.Money}");
        }

        BroadcastTreasureDestroyed(treasure);                       // DESTROY_TREASURE whole-mission + remove from sector
    }

    /// <summary>P123 — remove a treasure from its sector and broadcast
    /// <c>DESTROY_TREASURE(30)</c> whole-mission (every peer, every side) — the
    /// port of <c>KillTreasureEvent</c>'s wire half (<c>FedSrv.CPP:12498-12503</c>,
    /// <c>GetGroupRealSides()</c>). The treasure's <c>objectID</c> and its
    /// sector id go on the wire. Expiry's site handler separately respawns flags;
    /// pickup consumes the payload without respawning (FedSrv.CPP:13576).</summary>
    private void BroadcastTreasureDestroyed(Treasure treasure)
    {
        var sector = treasure.OwnerSector;
        sector.Remove(treasure);
        var msg = new DestroyTreasure { TreasureId = treasure.Id, SectorId = sector.Id };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = 0;
        foreach (var kv in _peerShips)   // whole-mission: every peer, regardless of side
        {
            if (treasure.OwnerMission is { } owner && !ReferenceEquals(kv.Value.Ship.Mission, owner)) continue;
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[treasure] destroyed {treasure.Id} in sector {sector.Id} -> DESTROY_TREASURE(30) whole-mission to {n} peer(s)");
    }

    /// <summary>P201 — broadcast <c>GAIN_FLAG(156)</c> <b>whole-mission</b> (the
    /// native <c>SendMessages(GetGroupRealSides(), ...)</c> audience,
    /// FedSrv.CPP:13573 for the pickup and :13157 for the secure — both
    /// confirmed whole-mission, not side-wide) for one flag-capture event:
    /// <c>sideidFlag</c> = the carried flag's ORIGINAL side on a pickup, or
    /// <see cref="GainFlag.NA"/> on a secure; <c>shipidRecipient</c> = the
    /// carrier's ship id. Scoped to the mission's peers (matching native
    /// GetGroupRealSides), so a world with multiple missions announces only to
    /// the peers on the acting mission. <c>bIsTreasureDocked</c> is always
    /// <c>false</c> in this task (the tech-recovery variant is out of scope).</summary>
    private void BroadcastGainFlag(Mission mission, short sideidFlag, short shipidRecipient, bool bIsTreasureDocked, short parttypeidDocked)
    {
        var msg = new GainFlag
        {
            SideidFlag = sideidFlag,
            ShipidRecipient = shipidRecipient,
            BIsTreasureDocked = bIsTreasureDocked,
            ParttypeidDocked = parttypeidDocked,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = 0;
        foreach (var kv in _peerShips)   // whole-mission (GetGroupRealSides): every peer on this mission, regardless of side
        {
            if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[gainflag] sideidFlag={sideidFlag} shipidRecipient={shipidRecipient} treasureDocked={bIsTreasureDocked} parttypeidDocked={parttypeidDocked} -> GAIN_FLAG(156) whole-mission to {n} peer(s)");
    }

    /// <summary>P164 — broadcast <c>BUILDINGEFFECT_DESTROYED(181)</c>
    /// whole-mission (every peer, every side) for an in-progress building
    /// effect whose Builder died mid-build — the port of
    /// <c>KillBuildingEffectEvent</c>'s wire half (<c>FedSrv.CPP:12484-12492</c>,
    /// <c>GetGroupRealSides()</c>). The interrupted build's target asteroid id
    /// goes on the wire; the message is structurally identical to
    /// <see cref="BroadcastTreasureDestroyed"/> (single destroyed-object id,
    /// whole-mission <c>_peerShips</c> sweep). Called from the
    /// <see cref="DrainBuildingEffectInterruptions"/> drain in the same tick
    /// slot as the treasure drain. The native sends this only on the
    /// <b>interruption</b> path — a build reaching its completion deadline
    /// transforms in place (P163 <c>CompleteBuild</c>) and sends nothing.</summary>
    private void BroadcastBuildingEffectDestroyed(short asteroidId)
    {
        var msg = new BuildingEffectDestroyed { AsteroidId = asteroidId };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = 0;
        foreach (var kv in _peerShips)   // whole-mission: every peer, regardless of side
        {
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[build] interrupted building effect on asteroid {asteroidId} -> BUILDINGEFFECT_DESTROYED(181) whole-mission to {n} peer(s)");
    }

    /// <summary>P294 — broadcast <c>EXPORT(26)/OT_buildingEffect</c> when a new
    /// in-progress building effect starts (a Builder committed to constructing
    /// a station at an asteroid) — the port of
    /// <c>FedSrvSiteBase::CreateBuildingEffect</c>'s broadcast half
    /// (<c>FedSrv.CPP:13733-13767</c>), which exports the fresh effect to every
    /// side that can see the build. This port has no per-side SeenBySide
    /// tracking for building effects (the DESTROYED sibling is whole-mission
    /// too, <see cref="BroadcastBuildingEffectDestroyed"/>), so the creation is
    /// broadcast to every peer regardless of side — a strict superset of
    /// native's audience, harmless to the client (its draw loop filters to the
    /// local player's own sector by <c>ClusterId</c>). The message carries the
    /// full <see cref="DataBuildingEffectExport"/> so the client can render the
    /// in-progress visual and reconstruct native's 4-phase animation from
    /// <c>timeStart</c>. The visual colour is native's hardcoded building-effect
    /// tint <c>Color(50/255, 28/255, 146/255)</c> (<c>buildingeffectigc.h:93</c>,
    /// which ignores the side colour via the commented-out <c>//*pcolor</c>).
    /// Called from the <see cref="DrainBuildingEffectCreations"/> drain in the
    /// same tick slot as the interruption drain. Native sends <b>no</b>
    /// periodic progress and nothing on completion (the client self-expires at
    /// <see cref="BuildingEffect.BuildDuration"/>); this port mirrors that.</summary>
    private void BroadcastBuildingEffectCreated(BuildingEffect effect)
    {
        uint timeStart = (uint)TickIndex;
        var msg = ExportBuildingEffect.FromBuildingEffect(
            effect, timeStart, effect.Builder.Position,
            50f / 255f, 28f / 255f, 146f / 255f, 1f);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        int n = 0;
        foreach (var kv in _peerShips)   // whole-mission: every peer, regardless of side
        {
            SendPeer(kv.Key, packet);
            n++;
        }
        Console.WriteLine($"[build] building effect started on asteroid {effect.Target.Data.AsteroidId} (station {effect.StationType.Name}) -> EXPORT(26)/OT_buildingEffect({ExportBuildingEffect.OtBuildingEffect}) whole-mission to {n} peer(s)");
    }

    /// <summary>
    /// P117 — the port of <c>FedSrvSiteBase::LandOnCarrierEvent</c>
    /// (<c>FedSrv.CPP:12808-13031</c>), the real effect of a ship landing on a
    /// carrier: restore the lander's ship-level consumable pools, reposition it
    /// at a real launch slot (or the no-slots fallback), and broadcast
    /// <c>RELAUNCH_SHIP(192)</c> sector-wide. Drained from
    /// <c>Sector.ResolveCarrierLandings</c> (via <c>DrainCarrierLandings</c>) in
    /// the same <see cref="SimPass"/> slot the P115 rescue drain uses — the
    /// sector pass detects the geometry, the host performs the effect.
    /// </summary>
    /// <remarks>
    /// <para><b>Resupply, at this port's own granularity.</b> The native resets
    /// per-part amounts and walks the tech-tree successor of each part
    /// (<c>RefillPart</c>, <c>FedSrv.CPP:12589-12618</c>) and then also resets
    /// the ship-level scalars (<c>FedSrv.CPP:12855-12861</c>:
    /// <c>SetAmmo(SHRT_MAX); SetFuel(FLT_MAX);
    /// SetEnergy(GetHullType()-&gt;GetMaxEnergy()); SetFraction(1.0f);</c> plus a
    /// mounted shield's fraction). This port tracks ammo/energy as ship-level
    /// scalar pools seeded from <c>HullType.MaxEnergy</c>/<c>MaxAmmo</c>
    /// (<c>Sim/Ship.cs:62-63</c>) — there is no per-part inventory — so the
    /// honest port of the whole resupply is
    /// to restore the two scalar pools to their hull maxima. That is exactly
    /// the native's ship-level reset above, at this port's granularity: <c>Ammo
    /// = SHRT_MAX</c> collapses to the finite hull <c>MaxAmmo</c> and
    /// <c>Energy = MaxEnergy</c> is verbatim. The native's <c>SetFraction(1.0f)</c>
    /// and shield-fraction reset are <b>not</b> performed here — the P117 scope
    /// is the consumable resupply (energy+ammo), and hull/shield repair is a
    /// separate, documented, not-yet-portable effect (see the P117 report).</para>
    /// <para><b>Fuel is a confirmed, separate, pre-existing gap.</b> Native
    /// <c>SetFuel(FLT_MAX)</c> (FedSrv.CPP:12856) has no target here: <c>grep
    /// "Fuel"</c> finds zero consumers in <c>Sim/Ship.cs</c>, <c>Model/Sector.cs</c>
    /// and <c>Server.cs</c> (only <c>DataHullTypeIGC.MaxFuel</c> is decoded,
    /// <c>Data/DataHullTypeIGC.cs:32</c>). Fuel tracking is deliberately not
    /// invented in this task.</para>
    /// <para><b>Dock auto-upgrade (restock) is P488b.</b> Native's carrier-land
    /// restock walks <c>GetSuccessorPartType()</c> to upgrade each desired part
    /// to the side's best available version (FedSrv.CPP:12831-12854), gated on
    /// the successor's required techs being a subset of the side's
    /// (development | building) techs. P488 brought the successor
    /// <em>resolution</em> into the port (<see cref="Allegiance.Core.Model.SuccessorUpgrade"/>
    /// — used at purchase in <c>ApplyLoadout</c> and at station build), but this
    /// <b>desired-loadout restock</b> still needs the per-part desired-loadout /
    /// inventory infrastructure this port doesn't have (documented P117) and is
    /// the named follow-up <b>P488b</b>; the two scalar-pool reset above remains
    /// the resupply here.</para>
    /// <para><b>RecalculateCollisions</b> (<c>FedSrv.CPP:13006</c>) is N/A: this
    /// port has no rigid-body collision-resolution system (established P115/P116),
    /// so there is no post-move collision pass to call.</para>
    /// <para><b>Autopilot command re-tasking</b> (<c>FedSrv.CPP:13024-13030</c>,
    /// the <c>SetCommand</c>/<c>c_cidDefend</c>/<c>c_cidGoto</c> re-assign when
    /// the lander was GOTO-bound for the carrier) is skipped: this port models no
    /// AI/pilot-type/autopilot command concept — the only command-ish state is a
    /// single weapon-aim <c>Ship.CommandTarget</c> with no <c>c_cid*</c> IDs
    /// (confirmed, <c>Sim/Ship.cs:444-456</c>). Same precedent as the
    /// ShipStatusSpotted (P113) and other confirmed-absent native concepts.</para>
    /// <para><b>Round-robin launch slots are ported, not elided.</b> The native
    /// picks <c>slot = pshipCarrier-&gt;GetLaunchSlot()</c> — a per-carrier modulo
    /// counter (<c>shipIGC.h:2168-2172</c>) — so consecutive landings spread
    /// across the bay's <c>launchN</c> frames. <see cref="Ship.GetLaunchSlot"/>
    /// ports that counter faithfully (<see cref="Ship.NextLaunchSlot"/>), driven
    /// off the carrier's real reconstructed bay. The two native branches
    /// (<c>FedSrv.CPP:12970-13000</c>) are reproduced: with launch slots,
    /// position/velocity/orientation derive from the slot's local frame
    /// transformed by the carrier's orientation; with zero slots, the ship is
    /// launched straight back along the carrier's backward axis.</para>
    /// <para><b>Cookie.</b> Native sets <c>cookie = NewCookie()</c> for a player
    /// (<c>FedSrv.CPP:13018</c>); this port's <c>Cookie</c> is a single byte sent
    /// as <c>0</c> (no per-player cookie machinery), the same established
    /// simplification <c>Eject.ForSurvivor</c> and <c>Server.cs:3289,4616</c>
    /// apply — so <c>RelaunchShip.Cookie = 0</c>.</para>
    /// </remarks>
    private void PerformRelaunch(Ship lander, Ship carrier)
    {
        var mission = lander.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[relaunch] ship {lander.Id} landed on carrier {carrier.Id} has no mission — relaunch dropped");
            return;
        }

        // 1) Resupply — restore the lander's ship-level consumable pools to
        //    their hull maxima (see remarks: the port's granularity-equivalent
        //    of the native's RefillPart loop + ship-level scalar reset).
        lander.Energy = lander.EffectiveMaxEnergy();
        lander.Ammo = lander.HullType.MaxAmmo;

        // 2) The carrier's real launch slots, resolved through the sector's
        //    landing-bay cache (the same production cache ResolveCarrierLandings
        //    used to detect the landing), falling back to a direct .cvh
        //    reconstruction when no cache is attached. A detected landing
        //    guarantees a resolvable bay with land slots; its launch-slot count
        //    is what selects the native's branch.
        var bay = lander.Sector?.LandingBays?.Bay(carrier.HullType)
                  ?? (carrier.Hull is not null
                        ? HullLandingBay.Reconstruct(carrier.Hull, carrier.HullType.Length)
                        : null);
        int launchSlots = bay?.LaunchSlots.Count ?? 0;

        var orientation = carrier.Orientation;                  // FedSrv.CPP:12963
        float vLaunch = lander.Mission!.Core.Constants.FloatConstants[4];                       // c_fcidExitStationSpeed = 50, FedSrv.CPP:12959, missionigc.cpp:2689,2744

        var position = carrier.Position + Jitter(0.5f);         // FedSrv.CPP:12964-12968 (small ±0.5 jitter)
        var velocity = carrier.Velocity;                        // FedSrv.CPP:12969

        Orientation newOrientation;
        int selectedSlot = -1;                                  // launch-slot index used (fallback branch = -1)
        if (launchSlots == 0)
        {
            // Fallback: launch straight back along the carrier's backward axis
            // (FedSrv.CPP:12971-12976). Keep the carrier's own orientation.
            var backward = orientation.GetBackward();
            position -= backward * (ShipRadius(carrier) + ShipRadius(lander) + vLaunch * 0.5f);
            velocity -= backward * vLaunch;
            newOrientation = orientation;
        }
        else
        {
            // Real launch slot: transform the slot's local frame by the
            // carrier's orientation (FedSrv.CPP:12978-12999), round-robin across
            // slots, and turn the lander to face the launch direction.
            selectedSlot = carrier.GetLaunchSlot(launchSlots);
            var slotPos = bay!.LaunchSlots[selectedSlot];
            position += slotPos.Position * orientation;               // GetLaunchPosition(slot) * orientation
            var forward = slotPos.Direction * orientation;            // GetLaunchDirection(slot) * orientation
            position += forward * (ShipRadius(lander) + vLaunch * 0.5f);
            velocity += forward * vLaunch;
            newOrientation = new Orientation(orientation);            // orientationBfr = orientation;
            newOrientation.Set(forward);                              // orientationBfr.TurnTo(forward) — align the nose to the launch direction
        }

        lander.Position = position;
        lander.Velocity = velocity;
        lander.SetOrientation(newOrientation);

        // 3) Broadcast RELAUNCH_SHIP(192) to everyone flying in the lander's
        //    cluster (GetGroupSectorFlying, FedSrv.CPP:13019). The loadout blob
        //    is the lander's own ShipLoadout (hullID + mounted parts), reusing
        //    BuyLoadoutRequest's blob shape.
        var relaunch = new RelaunchShip
        {
            ShipId = lander.Id,
            CarrierId = carrier.Id,
            Position = position,
            Velocity = velocity,
            Orientation = newOrientation,
            Cookie = 0,
            HullId = lander.HullType.HullId,
            Parts = lander.Parts.Select(p => p.ToExpandedPartData()).ToList(),
        };
        byte[] encoded = relaunch.Encode();
        SendSectorWide(lander.Sector!, encoded);

        var note = $"[relaunch] ship {lander.Id} relaunched off carrier {carrier.Id} (bay '{carrier.HullType.ModelName}', {launchSlots} launch slots{(selectedSlot >= 0 ? $", slot {selectedSlot}" : "")}) -> RELAUNCH_SHIP(192) sector-wide; energy={lander.Energy:F0}/{lander.HullType.MaxEnergy:F0} ammo={lander.Ammo}/{lander.HullType.MaxAmmo}; pos=({position.X:F1},{position.Y:F1},{position.Z:F1}) vel=({velocity.X:F1},{velocity.Y:F1},{velocity.Z:F1})";
        Console.WriteLine(note);
        Notify(note);
    }

    /// <summary>P113 — the ship's bounding radius, <c>GetRadius()</c>. The port
    /// has no per-model radius accessor (test ships carry no collision hull), so
    /// the established <c>0.5 × HullType.Length</c> sphere approximation is used,
    /// the same convention ServerHost uses for its spawn offsets (ServerHost.cs:566).
    /// Feeds the ship-anchor ripcord margin <c>r = target.Radius + ripper.Radius +
    /// 25.0f</c> (FedSrv.CPP:13462).</summary>
    private static float ShipRadius(Ship ship) => 0.5f * ship.HullType.Length;

    /// <summary>P113/P454 — the ship/probe-anchor ripcord relocation, shared by
    /// the OT_ship and OT_probe branches of <see cref="PerformRipcordTeleport"/>
    /// (the native's non-station <c>UseRipcord</c> else-branch,
    /// <c>FedSrv.CPP:13461-13476</c>): place <paramref name="rip"/> at
    /// <c>targetPos + dir · (targetRadius + ripper.Radius + 25)</c> in a
    /// <b>random</b> unit direction, kick it outward at
    /// <c>c_fcidExitStationSpeed</c> (slot 4, stationIGC.cpp:307), zero its turn rates, orient it along
    /// the direction, and move it into <paramref name="targetSector"/>'s
    /// cluster. The energy cost is <b>not</b> applied here — it is the
    /// ship-branch's responsibility (<c>FedSrv.CPP:13452-13460</c>), so a probe
    /// anchor (which pays nothing) shares this same placement verbatim.</summary>
    private static void PlaceRipcordNearTarget(Ship rip, Vector3 targetPos, float targetRadius, Sector targetSector)
    {
        float r = targetRadius + ShipRadius(rip) + 25.0f;
        Vector3 v = RandomDirection();
        rip.Position = targetPos + v * r;
        rip.Velocity = v * rip.Mission!.Core.Constants.FloatConstants[4];
        var o = new Orientation();
        o.Set(v);                       // Orientation o(v), FedSrv.CPP:13465
        rip.SetOrientation(o);
        rip.SetTurnRate(ControlData.AxisYaw, 0.0f);
        rip.SetTurnRate(ControlData.AxisPitch, 0.0f);
        rip.SetTurnRate(ControlData.AxisRoll, 0.0f);
        MoveShipToSector(rip, targetSector);
    }

    /// <summary>P113 — move a ripper into the anchor's sector
    /// (<c>SetCluster(pcluster)</c>, FedSrv.CPP:13474-13476): out of its old
    /// sector's model list, into the target's, and update its <see cref="Ship.Sector"/>
    /// back-ref so it ticks with the new sector next pass.</summary>
    private static void MoveShipToSector(Ship ship, Sector target)
    {
        // P307 hardening (ghost-ship sector-membership desync): make the
        // relocation exact-once-safe so every call site converges on the same
        // invariant — the ship ends up in exactly the target sector's _models
        // list (not its old one's, and not none) with the Sector back-ref in
        // sync. This is a strict superset of the original logic and is
        // behavior-identical for the ripcord callers: it additionally
        // tolerates a ship that a death/relocation pass has ALREADY removed
        // from its old sector's list (the remove is then a harmless no-op)
        // and a same-sector move where the ship may already be present in the
        // target (the Contains guard prevents a double-add).
        var old = ship.Sector;
        if (old is not null && !ReferenceEquals(old, target))
            old.Remove(ship);
        if (!target.Models.Contains(ship))
            target.Add(ship);
        ship.Sector = target;
    }

    /// <summary>P113 — keep the owning peer's broadcast bookkeeping coherent
    /// after a ripcord teleport changes the ripper's cluster: SHIP_STATUS /
    /// SINGLE_SHIP_UPDATE frames derive Sector from the <c>_peerShips</c>
    /// LiveShip snapshot, so point it at the anchor's cluster exactly like
    /// PerformTeleport / the lifepod respawn do (Server.cs:3072, 3929).</summary>
    private void UpdatePeerSectorSnapshot(Ship rip, Sector target)
    {
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, rip))
            {
                _peerShips[kv.Key] = kv.Value with { Sector = target };
                break;
            }
    }

    /// <summary>Pick a turret slot for <paramref name="boarder"/> on
    /// <paramref name="carrier"/>, faithfully porting the native walk
    /// (<c>FedSrv.CPP:2347-2402</c>). Returns the chosen slot index, or
    /// <c>null</c> (= NA) when the ship boards as an <b>observer</b> — either
    /// because the carrier has no turret slots at all
    /// (<c>minMount == maxMount</c>), or because no unoccupied, equipped slot
    /// exists after a full loop. Occupancy is read from the carrier's existing
    /// <see cref="Ship.ChildShips"/> turret ids; equipment from the carrier's
    /// mounted <see cref="MountedPart.MountId"/>s (the port already tracks
    /// both, so this is a pure reuse, no new fields).</summary>
    internal static short? ChooseTurretSlot(Ship boarder, Ship carrier)
    {
        int minMount = carrier.HullType.MaxFixedWeapons;
        int maxMount = carrier.HullType.MaxWeapons;
        // Equal -> no turret slots at all (observer). The `>=` (rather than `==`)
        // also swallows an INVERTED range (MaxFixedWeapons > MaxWeapons, possible
        // on a corrupt/hand-edited core — review H1's data-dependent trigger)
        // into the observer case instead of handing an empty range to the walk.
        if (minMount >= maxMount)
            return null;                                  // no turret slots at all → observer

        // P313 / review H1: the search-start heuristic reuses the boarder's
        // PREVIOUS slot so re-boarding the SAME carrier keeps the same turret,
        // but a stale TurretId inherited from a previous, LARGER carrier can lie
        // outside THIS carrier's [minMount, maxMount) range. Native trusts the
        // assert `GetParentShip() != NULL` (FedSrv.CPP:2359) to rule that out;
        // the port (like native, FedSrv.CPP:2341-2345) lets a docked boarder
        // re-board a DIFFERENT carrier without leaving first, so that id can go
        // out of range and the loop's wrap `turret >= maxMount -> minMount` never
        // revisits the start -> infinite spin (server-wide freeze, unrecoverable).
        // Clamping the start INTO the real range revalidates the stale id against
        // the new carrier's slots (the root cause) and keeps the full-loop
        // termination (turret cycling back to startMount) reachable.
        int startMount = Math.Clamp(boarder.TurretId ?? (maxMount - 1), minMount, maxMount - 1);
        int turret = startMount;
        while (true)
        {
            turret++;
            if (turret >= maxMount)
                turret = minMount;

            // Occupied? any existing child already rides this slot.
            bool occupied = false;
            foreach (var child in carrier.ChildShips)
                if (child.TurretId == (short)turret) { occupied = true; break; }

            // Equipped? some mounted part sits in this slot (Turkey 07/10 #145).
            bool equipped = false;
            foreach (var part in carrier.Parts)
                if (part.MountId == turret) { equipped = true; break; }

            if (!occupied && equipped)
                return (short)turret;                     // free and equipped → board here
            if (turret == startMount)
                return null;                              // full loop, no slot → observer
        }
    }

    /// <summary>The boarder's surrendered hardware value = hull price + Σ
    /// mounted part prices (<c>CshipIGC::GetValue</c>, <c>shipIGC.h:396-406</c>).</summary>
    // Native GetPrice: launcher quantity * unit price; packs are free
    // (Igc/magazineigc.h:85-88, dispenserigc.h:86-89, packigc.h:80-83).
    private static long PartValue(DataPartTypeIGC type, int amount) =>
        type.Type is DataPartTypeIGC.Pack ? 0
        : IsExpendableLauncher(type.EquipmentType) ? (long)type.Price * amount : type.Price;

    private static long RefundablePartValue(MountedPart part) =>
        part.FreeSpawn ? 0 : PartValue(part.Type, part.Amount);

    private static long RefundableShipValue(Ship ship) =>
        (long)ship.RefundableHullValue + ship.Parts.Sum(RefundablePartValue)
        + ship.CargoHold.Sum(p => p is null ? 0L : RefundablePartValue(p));

    private void RefundHardware(Ship ship)
    {
        if (LiveOf(ship) is not { } live) return;
        long value = RefundableShipValue(ship);
        // The wire wallet is int32: discard only the unrepresentable excess.
        int refund = (int)Math.Min(Math.Max(0, value), Math.Min(int.MaxValue, Math.Max(0L, (long)int.MaxValue - live.Wallet.Money)));
        ship.RefundableHullValue = 0;
        ship.ClearLoadout(); // consume all hardware before any outward send can throw
        if (refund == 0) return;
        live.Wallet.Money = checked(live.Wallet.Money + refund);
        if (ship.SideId is { } side) BroadcastMoneyChangeSideWide(ship.Mission!, side, refund, ship.Id);
    }

    /// <summary>P494 — an expendable launcher part (native <c>ET_Magazine /
    /// ET_Dispenser / ET_ChaffLauncher</c>, the only equipment kinds
    /// <c>CFSPlayer::SaveDesiredLoadout</c> records, fsship.h:475-498).</summary>
    private static bool IsExpendableLauncher(EquipmentType et) =>
        et is EquipmentType.Magazine or EquipmentType.Dispenser or EquipmentType.ChaffLauncher;

    /// <summary>Native destination slot for a desired launcher re-buy.</summary>
    private static byte FirstFreeMountSlot(Ship ship, DataPartTypeIGC part)
    {
        int count = part.EquipmentType == EquipmentType.Weapon ? Math.Min(8, ship.HullType.Hardpoints.Length) : 1;
        for (byte mount = 0; mount < count; mount++)
            if (ship.HullType.CanMountPart(part, mount)
                && !ship.Parts.Any(p => p.EquipmentType == part.EquipmentType && p.MountId == mount)) return mount;
        return byte.MaxValue;
    }

    /// <summary>P494 — write the player's DESIRED loadout after a successful
    /// BUY_LOADOUT: the free expendable-launcher part types currently mounted
    /// (native <c>CFSPlayer::SaveDesiredLoadout</c>, fsship.h:475-498 — slots
    /// 0-2 are the three launchers, the cargo entries are launchers too). The
    /// dock auto-restock (<see cref="RestockOnDock"/>) re-buys these, upgraded
    /// to their successors.</summary>
    private void SaveDesiredLoadout(LiveShip live)
    {
        var desired = live.Ship.Parts
            .Where(p => p.Type.Price == 0 && IsExpendableLauncher(p.EquipmentType))
            .Select(p => p.Type)
            .ToList();
        live.Wallet.DesiredLoadout = desired;
        Console.WriteLine($"[loadout] ship {live.Ship.Id}: saved desired loadout = [{string.Join(", ", desired.Select(d => $"{d.PartId} '{d.Name}'"))}]");
    }

    /// <summary>
    /// P494 (P488b) — the dock auto-restock: when a player-piloted ship docks,
    /// refill the ship's expendable pools and re-buy the desired loadout,
    /// upgrading each mounted part to its successor as the side out-techs it —
    /// the port of the native dock/carrier restock (<c>RefillPart</c>
    /// FedSrv.CPP:12589-12621, the desired-loadout walk FedSrv.CPP:12836-12945 /
    /// <c>AlliedRearm</c> 12655-12812), run at the port's dock transition
    /// (<see cref="OnShipDocked"/>).
    /// <para><b>Successor upgrade gate</b> is native <c>RefillPart</c>'s: the
    /// BASE part must be buyable under the side's techs, and it is replaced by
    /// its furthest buyable successor (FedSrv.CPP:12592-12620). The mask is the
    /// side's <c>development | building</c> techs (FedSrv.CPP:12819) — no
    /// station-local gate, matching native.</para>
    /// <para><b>Charging</b> composes P441's pricing: the player is debited the
    /// P441 ship-value differential (FedSrv.CPP:6184-6193) when the restock
    /// upgrades a mounted part to a more valuable successor, via the same
    /// side-wide <c>MONEY_CHANGE(60)</c> (<see cref="BroadcastMoneyChangeSideWide"/>).
    /// Affordable-gated (soft-fail): if the wallet can't cover the projected
    /// cost, the paid successor upgrades are skipped (the ship keeps its base
    /// parts and the free refill still happens) — the port's log-and-continue
    /// convention where native's BUY_LOADOUT would TrapHackBoot.</para>
    /// </summary>
    private void RestockOnDock(LiveShip live)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || ship.SideId is not { } sd) return;
        var side = mission.Sides.FirstOrDefault(s => s.Id == sd);
        if (side is null || ship.StationId is null) return;

        var ttbm = side.Techs;                 // dev | building (native RefillPart ttbm, FedSrv.CPP:12819)
        long oldValue = RefundableShipValue(ship);

        // ---- projection: the target part for each mounted part + the desired-
        // loadout re-buys + the total cost (P441 ship-value differential).
        var targets = new DataPartTypeIGC[ship.Parts.Count];
        for (int i = 0; i < ship.Parts.Count; i++)
        {
            var cur = ship.Parts[i].Type;
            targets[i] = TechTreeBitMask.RequiredTechsBuyable(cur.TtbmRequired, null, ttbm)
                ? SuccessorUpgrade.PartSuccessor(mission.Core, cur, null, ttbm)
                : cur;                                  // base not buyable → untouched (RefillPart:12592)
        }
        // The desired-loadout re-buy: parts saved at buy time that the ship no
        // longer carries are re-mounted (upgraded to their successors).
        var reBuys = new List<DataPartTypeIGC>();
        if (live.Wallet.DesiredLoadout is { Count: > 0 })
        {
            foreach (var dpt in live.Wallet.DesiredLoadout)
            {
                bool present = ship.Parts.Any(q => q.Type.PartId == dpt.PartId)
                            || ship.CargoHold.Any(c => c is { } cc && cc.Type.PartId == dpt.PartId);
                if (present) continue;
                reBuys.Add(SuccessorUpgrade.PartSuccessor(mission.Core, dpt, null, ttbm));
            }
        }
        long projectedValue = ship.RefundableHullValue
            + targets.Select((t, i) => ship.Parts[i].FreeSpawn && t.PartId == ship.Parts[i].PartId ? 0L
                : PartValue(t, Ship.ClampPartAmount(ship.HullType, mission.Core, t,
                    ship.Parts[i].Type.Price == 0 ? short.MaxValue : ship.Parts[i].Amount))).Sum()
            + reBuys.Sum(r => PartValue(r, Ship.ClampPartAmount(ship.HullType, mission.Core, r, short.MaxValue)))
            + ship.CargoHold.Sum(p => p is null ? 0L : RefundablePartValue(p));
        long projectedCost = projectedValue - oldValue;
        bool afford = projectedCost <= live.Wallet.Money && live.Wallet.Money >= 0;   // P441-style funds gate (soft-fail)

        // ---- apply the successor upgrades + free-amount resets (backwards so
        // newly-replaced parts are not revisited, RefillPart FedSrv.CPP:12665).
        int upgraded = 0;
        for (int i = ship.Parts.Count - 1; i >= 0; i--)
        {
            var p = ship.Parts[i];
            var target = targets[i];
            short newAmount = p.Type.Price == 0 ? short.MaxValue : p.Amount;   // RefillPart:12605-12607
            if (target.PartId == p.Type.PartId)
            {
                ship.SetPartAmount(p, newAmount);
                continue;
            }
            if (!ship.HullType.CanMountPart(target, p.MountId)
                || !afford && PartValue(target, Ship.ClampPartAmount(ship.HullType, mission.Core, target, newAmount)) > RefundablePartValue(p))
            {
                // Paid upgrade unaffordable → keep the base part, still reset the amount.
                ship.SetPartAmount(p, newAmount);
                Console.WriteLine($"[dock-restock] ship {ship.Id}: successor upgrade of {p.Type.PartId} ('{p.Type.Name}') -> {target.PartId} ('{target.Name}') unaffordable — kept base");
                continue;
            }
            ship.ReplacePartType(p, target, newAmount);
            upgraded++;
        }

        // ---- desired-loadout re-buy: mount each missing desired part (upgraded
        // to its successor) into a free mount slot, so the player gets the
        // loadout they bought back at dock. A paid re-buy is gated on the wallet
        // covering the projected cost (P441 soft-fail); free ones always mount.
        foreach (var succ in reBuys)
        {
            if (succ.Price > 0 && !afford)
            {
                Console.WriteLine($"[dock-restock] ship {ship.Id}: desired re-buy {succ.PartId} ('{succ.Name}', {succ.Price}) unaffordable — skipped");
                continue;
            }
            byte slot = FirstFreeMountSlot(ship, succ);
            if (slot == byte.MaxValue) continue;   // no free mount slot
            ship.MountPart(succ, short.MaxValue, slot, byte.MaxValue);
        }

        // ---- pool refill (native SetAmmo(SHRT_MAX)/SetFuel(FLT_MAX)/
        // SetEnergy(MaxEnergy)/SetFraction(1.0), FedSrv.CPP:12947-12955).
        ship.Ammo = ship.HullType.MaxAmmo;
        ship.SetFuel((short)ship.HullType.MaxFuel);
        ship.Energy = ship.EffectiveMaxEnergy();
        ship.SetFraction(1.0f);
        ship.SetShieldFraction(1.0f);

        // ---- charge P441-style: the ship-value differential, side-wide
        // MONEY_CHANGE to the owner's side (FedSrv.CPP:6195-6209).
        long cost = RefundableShipValue(ship) - oldValue;
        if (cost > 0)
        {
            live.Wallet.Money = checked((int)(live.Wallet.Money - cost));
            if (sd is { } bs) BroadcastMoneyChangeSideWide(ship.Mission!, bs, checked((int)-cost), ship.Id);
            Console.WriteLine($"[dock-restock] ship {ship.Id}: dock restock charged {cost} (wallet now {live.Wallet.Money}) + MONEY_CHANGE(60) dMoney={-cost} side-wide");
        }
        Console.WriteLine($"[dock-restock] ship {ship.Id}: dock restock done — {upgraded} successor upgrade(s), pools refilled, net cost {cost}");
    }

    /// <summary>
    /// P110 + P283 — the port's decision for <c>station.CanBuy(baseHullType)</c>,
    /// i.e. whether a station can sell the player's current hull
    /// (<c>FedSrv.CPP:7076</c>). Native: <c>CanBuy(b) = b->GetRequiredTechs()
    /// &lt;= (m_myStationType.GetLocalTechs() | side.GetTechs())</c>
    /// <c>stationigc.h:383-391</c>, bitwise containment of the buyable's
    /// required-tech mask in the union of the station's local techs and the
    /// side's techs), where the station's local techs are its station type's
    /// <c>ttbmLocal</c> (<c>stationtypeigc.h:159-161</c>) and the side's techs
    /// are the runtime-accreted mask <c>side.GetTechs() = ttbmBuildingTechs |
    /// ttbmDevelopmentTechs</c> (<c>sideigc.h:133-136</c>). <b>P283 wiring +
    /// P444 completion.</b> P283 wired this to <see cref="Side.DevelopmentTechs"/>
    /// (P189 — the live dev mask, seeded from the civ's base techs at side
    /// construction and grown by development completion, which subsumes the
    /// P110-era static <c>civ.TtbmBaseTechs</c> approximation). P444 completes
    /// the native <c>GetTechs()</c> term: <c>side.Techs = BuildingTechs |
    /// DevelopmentTechs</c> (<see cref="Side.Techs"/>), where
    /// <see cref="Side.BuildingTechs"/> is the recompute-on-access OR of every
    /// owned station type's <c>TtbmEffects</c> (native's
    /// <c>ttbmBuildingTechs</c>, sideigc.h:172-185) — so a side that builds a
    /// Supremacy/Shipyard/Tactical/Expansion base gains that tier's effect
    /// techs side-wide and the garrison starts selling that tier's hulls.
    /// <c>bNewHull</c> therefore reduces to
    /// <c>hull.TtbmRequired ⊆ (stationType.TtbmLocal | side.Techs)</c>
    /// (all 50-byte / 400-bit tech masks, <c>DataHullTypeIGC.cs:21</c>,
    /// <c>DataStationTypeIGC.cs:29</c>, <c>TechTreeBitMask.cs</c>).
    /// Measured on PCore001.igc: a faction's garrison sells its starter hulls
    /// (<c>bNewHull=false</c>), rejects advanced hulls until their governing
    /// development is researched (<c>bNewHull=true</c> before,
    /// <c>bNewHull=false</c> after <c>CompleteDevelopment</c> grows
    /// <c>Side.DevelopmentTechs</c>) — exactly the pair the task's
    /// verification scenarios exercise.
    /// </summary>
    private static bool StationSellsHull(DataStationTypeIGC st, Side side, DataHullTypeIGC hull)
    {
        var need = hull.TtbmRequired;
        var local = st.TtbmLocal;
        var sideTechs = side.Techs.AsSpan();
        for (int i = 0; i < need.Length && i < local.Length; i++)
            if ((need[i] & ~(local[i] | (i < sideTechs.Length ? sideTechs[i] : (byte)0))) != 0) return false;
        for (int i = local.Length; i < need.Length; i++)
            if (need[i] != 0 && (i >= sideTechs.Length || (need[i] & ~sideTechs[i]) != 0)) return false;
        return true;
    }

    /// <summary>Resolve a same-side ship by id — the port of native
    /// <c>GetSide()->GetShip(id)</c> (<c>FedSrv.CPP:6404</c>, and the
    /// <c>ORDER_CHANGE</c> target resolution <c>pside-&gt;GetShip(pfmOC-&gt;shipID)</c>
    /// at <c>FedSrv.CPP:7275-7278</c>), which looks the target up on the
    /// commanding/boarding ship's <em>own side</em> (hence the same-side
    /// requirement). Returns <c>null</c> for an unknown id or a mismatched
    /// side.
    /// <para><b>P148 extension.</b> Native <c>GetSide()->GetShip()</c> is
    /// <b>side-scoped, not sector-scoped</b>, and searches every ship on the
    /// side — including NPC/drones. This port's original lookup only searched
    /// <see cref="_peerShips"/> (the peer-connection registry), so it could
    /// <b>not</b> find an NPC ship (which, by P147's design, has no peer
    /// entry). It is therefore extended to also enumerate the mission's full
    /// ship set — every sector's model list, filtered to
    /// <see cref="Ship"/> instances — with the same id + side match. A placed
    /// NPC lives in a sector's model list (<c>Mission.PlaceShip</c> →
    /// <c>sector.Add</c>, <c>Mission.cs:347-358</c>), so this finds it while
    /// remaining side-scoped and id-keyed, faithful to native. The
    /// <c>_peerShips</c> scan is retained (player ships are always there and
    /// it is cheap); the mission scan is the net-new coverage.</para>
    /// </summary>
    private Ship? FindSameSideShip(short id, short? sideId, Mission? mission)
    {
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.Id == id && kv.Value.Ship.SideId == sideId)
                return kv.Value.Ship;

        // P148: also search the mission's full ship set (catches NPC/drones
        // placed via Mission.PlaceShip, which have no _peerShips entry).
        if (mission is not null)
            foreach (var sec in mission.Sectors)
                foreach (var mdl in sec.Models)
                    if (mdl is Ship s && s.Id == id && s.SideId == sideId)
                        return s;

        return null;
    }

    /// <summary>
    /// P153 — cross-side ship-by-id lookup for the generic <c>ORDER_CHANGE</c>
    /// fallback (<c>FedSrv.CPP:7335-7339</c>): the <b>targeted</b> object
    /// (<c>pmodel</c>, decoded from <c>pfmOC-&gt;objectType</c>/<c>objectID</c>)
    /// that the commanding player wishes to lock — an enemy or ally ship, NPC
    /// or real peer. Unlike <see cref="FindSameSideShip"/> this imposes <b>no
    /// side filter</b>: target-locking an <em>enemy</em> ship is the whole
    /// point of the <c>c_cmdCurrent</c> SetTarget action
    /// (<c>WinTrek.cpp:8820-8852</c>), and native resolves it generically via
    /// <c>pMission-&gt;GetModel(objectType, objectID)</c>
    /// (<c>FedSrv.CPP:7336</c>), which spans the whole mission with no side
    /// restriction. The two-source scan mirrors
    /// <see cref="FindSameSideShip"/>'s own P148 extension (peer ships first,
    /// then every sector's model list for NPCs placed via
    /// <c>Mission.PlaceShip</c>, which have no <c>_peerShips</c> entry) —
    /// minus the side equality. Returns <c>null</c> for an unknown id, which
    /// the caller treats as native <c>GetModel</c> returning <c>NULL</c> (a
    /// clear-target / no-op, <c>FedSrv.CPP:7336-7338</c>).
    /// </summary>
    private Ship? FindShipAnySide(short id, Mission? mission)
    {
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.Id == id)
                return kv.Value.Ship;

        // NPC/drone ships placed via Mission.PlaceShip live in a sector's
        // model list with no _peerShips entry (P147) — enumerate them the same
        // way FindSameSideShip does, without the side filter.
        if (mission is not null)
            foreach (var sec in mission.Sectors)
                foreach (var mdl in sec.Models)
                    if (mdl is Ship s && s.Id == id)
                        return s;

        return null;
    }

    /// <summary>P422 — resolve a runtime <see cref="Asteroid"/> by its
    /// <c>ObjectID</c> across every sector of the mission (asteroids live in a
    /// sector's <c>_mineable</c> set, not its model list — cf. Asteroid's doc
    /// comment). Used by the player Build command to resolve the
    /// <c>OT_asteroid</c> wire target to the real rock. Same breadth as the
    /// established <c>pMission-&gt;GetModel(otTarget, oidTarget)</c> resolution
    /// (<c>FedSrv.CPP:6428</c>) applied to asteroids.</summary>
    private Asteroid? FindAsteroidById(short id, Mission? mission)
    {
        if (mission is null) return null;
        foreach (var sec in mission.Sectors)
            foreach (var rock in sec.Mineable)
                if (rock.Data.AsteroidId == id)
                    return rock;
        return null;
    }

    /// <summary>Allocate the next ship id, skipping any id currently held by a
    /// live ship (P316, option (b)). The ShipID wire field is a 16-bit
    /// <c>short</c> (native <c>typedef ObjectID ShipID</c>, <c>Igc/igc.h:480</c>;
    /// <c>ObjectID</c> at <c>igc.h:477</c>), so the space cannot be widened
    /// without breaking wire compatibility. Wrapping blindly would let a newly
    /// validated logon receive an id still held by a live ship, mis-directing
    /// <c>POSITIONACK</c>/<c>ORDER_CHANGE</c>/boarding lookups
    /// (<see cref="FindConnectionOfShip"/>/<see cref="FindSameSideShip"/>) and
    /// stale-hitting the never-pruned teammate-export cache (review findings H4
    /// and M1). Rather than wrap, walk forward past any still-live id at
    /// allocation time. Only called for validated logons (and NPC spawns), so
    /// an unauthenticated LOGONREQ flood burns no ids.</summary>
    private short AllocateShipId()
    {
        for (int attempt = 0; attempt < 65536; attempt++)
        {
            short candidate = unchecked((short)_nextShipId++);
            if (!IsShipIdLive(candidate))
                return candidate;
        }
        // Pathological: the entire 16-bit space is occupied. Hand out the last
        // visited value rather than spinning forever.
        return unchecked((short)(_nextShipId - 1));
    }

    /// <summary>Whether <paramref name="id"/> is currently held by any live
    /// ship: a seated peer (<see cref="_peerShips"/>), a peer still awaiting
    /// side selection (its <see cref="PendingSideSelect.ShipId"/> is reserved
    /// even though no <c>Ship</c> object exists yet), or any NPC ship placed in
    /// a mission sector (P147, no <c>_peerShips</c> entry).</summary>
    private bool IsShipIdLive(short id)
    {
        foreach (var kv in _peerShips)
            if (kv.Value.Ship.Id == id)
                return true;
        foreach (var kv in _pendingSideSelect)
            if (kv.Value.ShipId == id)
                return true;
        foreach (var mission in _missions)
            foreach (var sector in mission.Sectors)
                foreach (var model in sector.Models)
                    if (model is Ship s && s.Id == id)
                        return true;
        return false;
    }

    /// <summary>Send <c>FM_S_BOARD_NACK(178)</c> to one peer (the requester)
    /// naming the <paramref name="requestedParent"/> id — <c>SendMessages(
    /// pfsPlayer-&gt;GetConnection(), ...)</c>, <c>FedSrv.CPP:6412-6416</c>.</summary>
    private void SendBoardNack(ConnectionId from, short requestedParent)
    {
        var nack = new BoardNack { SidRequestedParent = requestedParent };
        SendPeer(from, FedPacket.Pack(new[] { nack.Encode() }));
    }

    /// <summary>
    /// This port's <c>GetModel(ot, oid)</c> — resolve an object by
    /// <c>(ObjectType, ObjectID)</c> across the objects the port can name: the
    /// peer ships (the only first-class models, all present in <see cref=\"_peerShips\"/>)
    /// and sector-attached stations. On success outputs the object's real
    /// position, the sector that owns it, and its side (nullable for a neutral
    /// object). Returns false for an unnamed type or an unknown id — the caller
    /// then emits a bare-cluster reply. This matches the practical reality of a
    /// spectator target (another player's ship, or a station).
    /// </summary>
    private bool TryResolveViewTarget(Mission mission, IgcObjectType ot, short oid, out Vector3 position, out Sector? sector, out short? side)
    {
        switch (ot)
        {
            case IgcObjectType.Ship:
                foreach (var kv in _peerShips)
                {
                    if (ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.Id == oid)
                    {
                        position = kv.Value.Ship.Position;
                        sector = kv.Value.Ship.Sector;
                        side = kv.Value.Ship.SideId;
                        return true;
                    }
                }
                break;

            case IgcObjectType.Station:
                foreach (var s in mission.Sectors)
                {
                    foreach (var st in s.Stations)
                    {
                        if (st.StationId == oid)
                        {
                            position = new Vector3(st.Position[0], st.Position[1], st.Position[2]);
                            sector = s;
                            side = st.SideId;
                            return true;
                        }
                    }
                }
                break;
        }

        position = Vector3.Zero;
        sector = null;
        side = null;
        return false;
    }

    /// <summary>The port's stand-in for native <c>ImodelIGC::SeenBySide(side)</c> —
    /// the same-side predicate the P58 teammate broadcast already uses
    /// (<c>Server.cs:521</c>). An object with no side (a neutral world object)
    /// is always visible; a side-carrying object is visible only to its own
    /// side. The port has no fog-of-war/scanner, so this is the faithful
    /// existing gate, not an invented one.</summary>
    private static bool SeenByRequesterSide(short? objectSide, short? requesterSide) =>
        objectSide is null || objectSide == requesterSide;

    /// <summary>
    /// Apply a received <see cref="BuyLoadoutRequest"/> (FM_C_BUY_LOADOUT, 4)
    /// to the peer's ship and reply with the real FM_S_BUY_LOADOUT_ACK (5),
    /// tail carrying the ship's actual mounted-part count. Runs on the sim
    /// thread (ProcessDatagram). This is the P61 generalization of the P26/P27
    /// hull-only adopt-a-hull step: resolve the requested hull against the
    /// core, replace the ship's loadout with the requested parts, and echo the
    /// mounted parts back byte-for-byte.
    /// </summary>
    /// <summary>Shared undock/ripcord station launch, native stationIGC.cpp:299-382.</summary>
    private void RepositionForLaunch(Ship ship, DataStationIGC station)
    {
        var sector = ship.Mission!.Sectors.FirstOrDefault(s => s.Id == station.ClusterId);
        var runtime = sector?.StationRuntimes.FirstOrDefault(s => s.StationId == station.StationId);
        if (runtime is null) return;
        runtime.Launch(ship, sector!.StationLandingBays?.LaunchGeometry(runtime.Type), _simNow);
    }

    /// <summary>
    /// P152 — the connection-agnostic core of an undock, extracted from
    /// <see cref="PerformUndock"/> so the existing real-peer undock path and the
    /// new docked-NPC "launch" arm of <see cref="HandleOrderChange"/>
    /// (ORDER_CHANGE Stage 6) share one state transition with no duplication:
    /// resolve the ship's docked station in <paramref name="sector"/>, clear
    /// <see cref="Ship.StationId"/>, apply the station's launch geometry via
    /// <see cref="RepositionForLaunch"/>, and
    /// start the undock debounce (<see cref="Ship.LastLaunchTime"/>, P75).
    /// <para><b>Why this is safe to share with an NPC.</b> The only part of the
    /// real-peer <see cref="PerformUndock"/> that needs a <see cref="ConnectionId"/>
    /// is the trailing <c>FM_S_SET_CLUSTER(7)</c> re-send — which exists solely to
    /// tell the <em>undocking peer's own client-side simulation</em> that
    /// <c>GetStation() == NULL</c> (<c>appmsg.cpp:1044-1049</c>). An NPC has no
    /// owning peer's client to inform (P147: <c>SpawnNpcShip</c> creates no
    /// <c>_peerShips</c> entry), and other real peers observing the NPC learn its
    /// new flying state for free via the existing
    /// <c>HeavyShipsUpdate</c>/<c>BroadcastShipsUpdate</c> machinery (the same
    /// reasoning P147 established) — so the connection-specific message is
    /// genuinely inapplicable to the NPC path, not merely inconvenient. This
    /// helper is therefore the pure state-mutation core, sending no wire
    /// messages; the caller decides whether any peer-specific send follows.</para>
    /// Caller must have already confirmed <c>ship.StationId != null</c>.
    /// </summary>
    private void UndockShipState(Ship ship, Sector sector)
    {
        var station = ship.StationId!.Value;
        ship.StationId = null;
        // Native ChangeStation propagates station state to every child
        // (FedSrv/FedSrv.CPP:13165-13175). Otherwise a gunner remains marked
        // docked forever after its parent launches, blocking valid turret input.
        foreach (var child in ship.ChildShips) child.StationId = null;

        // Use the station's native slot rotation, offsets, jitter, and velocity.
        DataStationIGC? stationModel = null;
        foreach (var st in sector.Stations)
            if (st.StationId == station) { stationModel = st; break; }
        if (stationModel is { } stFound)
        {
            // P261: the launch repair/reload — native fires
            // CstationIGC::RepairAndRefuel in ChangeStation's undock branch
            // (FedSrv.CPP:13189-13191), i.e. at LAUNCH, gated on the DOCKED
            // station's type capabilities. Both undock arms (real-peer
            // PerformUndock and the docked-NPC launch arm of HandleOrderChange)
            // converge here, so this single call restores every launching ship.
            // The station's StationTypeId is resolved to its DataStationTypeIGC
            // from the mission core exactly as FindNearestFriendlyStation does
            // (Server's existing core-resolution idiom); an unresolvable type
            // can't gate capabilities, so the restore is skipped (defensive —
            // every real map station has a resolvable type).
            if (ship.Mission?.Core.StationTypes.TryGetValue(stFound.StationTypeId, out var dtype) == true)
                ship.RepairAndRefuel(dtype);

            RepositionForLaunch(ship, stFound);
        }

        // P75: start the undock debounce — Sector.ResolveDockCollisions won't
        // consider this ship for re-docking again until Sector.LaunchCooldownSeconds
        // elapses, regardless of how close it drifts back to the station.
        ship.LastLaunchTime = _simNow;
    }

    /// <summary>
    /// Undock a docked ship: clear its station, apply its native launch pose
    /// and outward velocity, start the re-dock debounce, and re-send
    /// FM_S_SET_CLUSTER(7) — the only message that clears the native client's
    /// own GetStation()/GetCluster() model state (appmsg.cpp:1044-1049), which
    /// BaseClient::flyingF() (clintlib.h:1198) and the Hangar screen's launch
    /// lockdown (appmsg.cpp:991-996) both gate on. Extracted so both real
    /// undock triggers this server recognizes can share it: an explicit
    /// FM_C_DOCKED(48) request (WOPR bots, P69/P71), and BUY_LOADOUT's own
    /// FLaunch=true (the real WinTrek client's Hangar "Launch" button, which
    /// never sends a separate undock message — see ApplyLoadout below).
    /// Caller must have already confirmed <c>live.Ship.StationId != null</c>.
    /// </summary>
    private void PerformUndock(ConnectionId from, LiveShip live)
    {
        // Capture the docked station id before the undock core clears it — the
        // diagnostic below reports which station the ship left.
        var undockedFrom = live.Ship.StationId!.Value;

        // P152 extraction: the connection-independent state mutation (clear
        // StationId, reposition clear of DockRadius, start the debounce) now
        // lives in UndockShipState, shared with the docked-NPC launch arm of
        // HandleOrderChange. Behavior for this real-peer path is unchanged —
        // this is a pure extraction; only the peer-specific SET_CLUSTER send
        // remains here.
        UndockShipState(live.Ship, live.Sector);

        // Re-send FM_S_SET_CLUSTER(7) to the undocking peer — the only place
        // that calls m_ship->SetStation(NULL) client-side (appmsg.cpp:1044-1049).
        // Without this the client still believes GetStation()!=NULL and
        // GetCluster()==NULL forever, so flyingF() never becomes true and (for
        // the real client) the Hangar launch lockdown never clears either.
        var set = new SetCluster
        {
            SectorId = live.Sector.Id,
            Ship = ServerShipUpdate.FromModel(live.Ship.Id, live.Ship, (uint)TickIndex),
            // P518 — this SET_CLUSTER must rotate the per-player cookie with
            // NextCookie (native CFSPlayer::SetCluster ALWAYS stamps
            // pfmSetCluster->cookie = NewCookie(), fsship.cpp:767), exactly like
            // the join burst (CompleteJoinAndLaunch) and the warp path
            // (OnShipWarped). The old `Cookie = 0` told the client its cookie was
            // 0 while the server's PeerShipUpdate.Cookie stayed at the join value
            // (e.g. 1): every native SHIP_UPDATE the client echoed after undock
            // then hit the dispatch cookie gate and was silently dropped — the
            // server's ship froze parked, so it could never dock again ("flies
            // through stations"). This was the P518 root cause.
            Cookie = NextCookie(EnsurePeerUpdate(from)),
        };
        SendPeer(from, FedPacket.Pack(new[] { set.Encode() }));
        SendTreasureSnapshot(from, live.Sector);

        var undockNote = $"[undock] ship {live.Ship.Id} undocked from station {undockedFrom} — next SHIP_STATUS(90) carries StationID=NA + State=c_ssFlying";
        Console.WriteLine(undockNote);
        Notify(undockNote);      // spectator event feed (same text)
    }

    /// <summary>
    /// P110 — the teleport/relocation dock request, the <b>non-</b><c>NA</c>
    /// half of <c>FM_C_DOCKED(48)</c>. A displaced ship asks to land at a
    /// specific <paramref name="targetStationId"/>; the server validates it
    /// and answers with <c>FM_S_TELEPORT_ACK(102)</c>. Port of
    /// <c>FedSrv.CPP:7054-7131</c>. This is <b>not</b> the proximity-triggered
    /// dock this port already handles via <see cref="Sector.ResolveDockCollisions"/>
    /// (untouched) — it is the explicit-<c>stationID</c> request direction that
    /// a relocated ship (a future rescue/warp-arrival trigger) uses to land
    /// somewhere, exactly the native responder half. The caller has already
    /// gated: a peer-owned, currently-docked ship
    /// (<c>GetStation()!=NULL</c>, <c>FedSrv.CPP:7040-7041</c>). Runs on the
    /// sim thread (called from the <c>FM_C_DOCKED</c> wire handler).
    /// <para><b>Validation</b> (<c>FedSrv.CPP:7063-7066</c>): the station must
    /// exist, be on the player's side or an ally's, and be
    /// <c>c_sabmRestart</c>-capable. If any fails, native sends the ACK with
    /// <c>stationID = NA</c> (<c>FedSrv.CPP:7067-7073</c>) and <c>break</c>s —
    /// the ship is <b>left where it is</b>, not moved. This port's ally check
    /// collapses to same-side (no alliance concept, stated plainly, matching
    /// how <see cref="FindFriendlyStation"/> defines "friendly").</para>
    /// <para><b>Acknowledge</b>: valid → echo the real <paramref
    /// name="targetStationId"/> and compute <c>bNewHull =
    /// !station.CanBuy(ship's base hull)</c> (<c>FedSrv.CPP:7076</c>). When
    /// <c>bNewHull</c>, mirror <c>FedSrv.CPP:7079-7120</c>: boot boarded
    /// children, refund the ship's hardware value, strip its parts, swap to
    /// the side's Lifepod hull — reusing this port's existing helpers
    /// (<see cref="RefundableShipValue"/>, <see cref="Ship.ClearLoadout"/>,
    /// <see cref="Ship.ChangeHull"/>), the same three operations P105/P108
    /// built, <b>not</b> reimplemented. Finally move any boarded children to
    /// the station and the ship itself (<c>FedSrv.CPP:7124-7131</c>).</para>
    /// </summary>
    private void PerformTeleport(ConnectionId from, LiveShip live, short targetStationId)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[teleport] {from} ship {ship.Id}: no mission — teleport request dropped");
            return;
        }

        if (ship.StationId is null || ship.ParentShip is not null || ship.Dead || ship.SideId is not >= 0) return;

        // Resolve the named station across every sector (native
        // GetStation(id) spans the mission, FedSrv.CPP:7056).
        DataStationIGC? target = null;
        foreach (var sector in mission.Sectors)
        {
            foreach (var st in sector.Stations)
                if (st.StationId == targetStationId) { target = st; break; }
            if (target is not null) break;
        }

        // Validation triple (FedSrv.CPP:7063-7066): exists, friendly/allied,
        // c_sabmRestart-capable. Side can't be null here — the caller's docked
        // gate implies a placed ship with a side — but guard anyway.
        bool exists = target is not null;
        bool sameSide = target is { } t0 && t0.SideId == ship.SideId;
        bool restartOk = target is { } t1
            && mission.Core.StationTypes.TryGetValue(t1.StationTypeId, out var stype)
            && stype.HasCapability(DataStationTypeIGC.SabmRestart);

        if (!exists || !sameSide || !restartOk)
        {
            // Reject (FedSrv.CPP:7067-7073): ack stationID = NA, ship stays put.
            var reject = new TeleportAck { StationId = -1, BNewHull = false };
            SendPeer(from, FedPacket.Pack(new[] { reject.Encode() }));
            var why = string.Join("; ",
                (exists ? null : "station not found"),
                (sameSide ? null : "not on this side or an ally's"),
                (restartOk ? null : "not c_sabmRestart-capable"))
                .Replace("  ", " ");
            ConsoleLog.Write($"[teleport] {from} ship {ship.Id}: request for station {targetStationId} rejected — {why} -> TELEPORT_ACK stationID=NA (FedSrv.CPP:7067-7073)");
            return;
        }

        // Valid — acknowledge with the real station id (FedSrv.CPP:7075-7077),
        // and the port's bNewHull = !CanBuy(current hull). stype is guaranteed
        // present past the restartOk guard (which itself requires the lookup
        // to succeed), so the re-lookup here is provably safe. The ship's side
        // provides the runtime side-tech term (native side.GetTechs() = the
        // port's Side.DevelopmentTechs, P283) StationSellsHull consults.
        mission.Core.StationTypes.TryGetValue(target!.Value.StationTypeId, out var validType);
        var side = mission.Sides.FirstOrDefault(s => s.Id == ship.SideId!.Value);
        if (side is null)
        {
            ConsoleLog.Write($"[teleport] {from} ship {ship.Id}: side {ship.SideId} not in mission — cannot compute bNewHull; teleport rejected");
            var reject = new TeleportAck { StationId = -1, BNewHull = false };
            SendPeer(from, FedPacket.Pack(new[] { reject.Encode() }));
            return;
        }
        bool bNewHull = !StationSellsHull(validType, side, ship.HullType);
        var ack = new TeleportAck { StationId = targetStationId, BNewHull = bNewHull };
        SendPeer(from, FedPacket.Pack(new[] { ack.Encode() }));
        Console.WriteLine($"[teleport] {from} ship {ship.Id} -> station {targetStationId} '{target!.Value.Name}' bNewHull={bNewHull} — TELEPORT_ACK(102)");

        if (bNewHull)
        {
            // Boot any boarded children (LeaveShip each) — FedSrv.CPP:7083-7087.
            foreach (var kid in ship.ChildShips.ToArray())
                PerformLeave(kid, ship);

            // Native FedSrv.CPP:7090-7105, same wallet/refund rule as boarding.
            RefundHardware(ship);

            // Sell off / strip all mounted parts (FedSrv.CPP:7107-7113) →
            // ClearLoadout, the P108-ready helper.
            // RefundHardware already stripped the hardware.

            // Swap to the side's Lifepod hull (FedSrv.CPP:7116-7117) — the
            // same resolution P105/P107/P108 already use.
            var lifepodId = mission.Sides.First(s => s.Id == ship.SideId!.Value).Civilization.Lifepod;
            if (mission.Core.HullTypes.TryGetValue(lifepodId, out var lp))
            {
                ship.ChangeHull(lp);
                Console.WriteLine($"[teleport] ship {ship.Id} hull -> Lifepod {lp.HullId} (old hull not sellable at station {targetStationId})");
            }
            else
            {
                Console.WriteLine($"[teleport] ship {ship.Id} side lifepod hull {lifepodId} not in core — hull swap skipped (still stripped + refunded)");
            }
        }

        // Relocate to the target station's cluster and dock there — move any
        // boarded children first, then the ship itself (FedSrv.CPP:7124-7131).
        var destSector = mission.Sectors.First(s => s.Stations.Contains(target!.Value));
        MoveShipToSector(ship, destSector);      // P307: real list move (old.Remove → dest.Add → Sector), not just the back-ref
        foreach (var kid in ship.ChildShips)          // empty in the bNewHull case (booted above)
            kid.StationId = targetStationId;
        ship.StationId = targetStationId;
        foreach (var kid in ship.ChildShips) kid.LastDockTime = _simNow;   // P381: dock clock
        ship.LastDockTime = _simNow;                                       // P381: dock clock

        // Keep the owning peer's broadcast bookkeeping coherent — SHIP_STATUS
        // frames derive Sector from the snapshot, so point it at the station's
        // cluster like P105 does (Server.cs:3469-3470).
        _peerShips[from] = _peerShips[from] with { Sector = destSector };

        var note = $"[teleport] ship {ship.Id} docked at station {targetStationId} '{target.Value.Name}' in sector '{destSector.Name}'";
        Console.WriteLine(note);
        Notify(note);
    }

    /// <summary>
    /// P381 — how long a docked NPC drone must sit docked before it undocks on
    /// its own, in sim-seconds. The port of native's ~10&nbsp;s docked minimum in
    /// <c>IshipIGC::OkToLaunch</c> (<c>shipIGC.h:1914-1915</c>: <c>now &gt;
    /// GetMyLastUpdate() + 10.0f</c>). Matches the native <c>10.0f</c> exactly.
    /// </summary>
    private const double NpcAutoLaunchCooldownSeconds = 10.0;

    /// <summary>
    /// P381 — the per-tick pass that undocks docked NPC drones on their own:
    /// the port of the <c>CclusterIGC::Update</c> auto-launch loop
    /// (<c>clusterIGC.cpp:106-140</c>) that launches every docked autopilot
    /// drone each cluster tick. Drives the behavior from real native play: drone
    /// ships that start (or end up) docked undock and go back to work with zero
    /// player/bot involvement — the missing step that made previously-idle miners
    /// "just sit there".
    /// <para><b>Predicate.</b> For every sector of every mission, a ship is
    /// auto-launched when it is an NPC (<see cref="Ship.IsNpc"/>), docked
    /// (<see cref="Ship.StationId"/> non-null), not a boarded passenger
    /// (<see cref="Ship.ParentShip"/> null — native iterates
    /// <c>pstation-&gt;GetShips()</c>, the station-docked set, and asserts docked
    /// drones are never observers/parents; a boarder inherits the carrier's
    /// StationId but must never be auto-undocked), has not been commanded to stay
    /// docked (<see cref="Ship.StayDocked"/> false), and has sat docked at least
    /// <see cref="NpcAutoLaunchCooldownSeconds"/>. It is launched through the same
    /// shared <see cref="UndockShipState"/> core the ORDER_CHANGE LAUNCH handler
    /// uses (<c>Server.ShipCommands.cs:1100-1122</c>), with no new wire message:
    /// observing peers learn the NPC's new flying state for free via the existing
    /// <c>HeavyShipsUpdate</c>/<c>BroadcastShipsUpdate</c> machinery (the P147/P152
    /// reasoning — <c>UndockShipState</c> is pure state mutation).</para>
    /// <para><b>Deliberately not ported.</b> The <c>OkToLaunch</c> feasibility
    /// pre-check (<c>LegalCommand</c>/<c>PickDefaultOrder(…, bDocked=true)</c>,
    /// shipIGC.h:1919-1920) is dead code for this port's scope — established at
    /// <c>Server.ShipCommands.cs:1089-1100</c>: the only ported PickDefaultOrder
    /// sub-block (the P151 take-cover check, shipIGC.h:2230) is gated on
    /// <c>!bDocked</c>, which <c>true</c> makes false — so it is not ported either;
    /// the instant this pass clears <see cref="Ship.StationId"/>, the ship's
    /// existing per-tick decision loop (<c>Ship.cs</c> ~6000-6110, already running
    /// every tick regardless of dock state) picks it up on its own the very next
    /// tick. The friend/enemy local-safety check (shipIGC.h:1925-1969) is also not
    /// ported: it requires per-ship SeenBySide tracking, pilot-type/ally/distance
    /// classification, and a three-branch squared-distance threshold lattice this
    /// port does not track — a real behavior but secondary to "miners undock at
    /// all" (see P381_NPC_AUTOLAUNCH.md). Runs on the sim thread in
    /// <see cref="SimPass"/> alongside the mission tick, so no locks are needed.
    /// </summary>
    private void RunNpcAutoLaunch(double now)
    {
        foreach (var m in _missions)
        {
            foreach (var sector in m.Sectors)
            {
                foreach (var model in sector.Models.ToArray())   // snapshot: UndockShipState mutates the ship (not the list), but never iterate a live list in place
                {
                    if (model is not Ship ship) continue;
                    if (!ship.IsNpc) continue;                              // the IsNpc gate is non-negotiable — real player ships are never auto-undocked
                    if (ship.StationId is null) continue;                   // only docked drones
                    if (ship.ParentShip is not null) continue;              // boarded passengers ride the carrier, not a station dock
                    if (ship.StayDocked) continue;                          // commanded to stay (ORDER_CHANGE c_cidDoNothing)
                    if (now - ship.LastDockTime < NpcAutoLaunchCooldownSeconds) continue;   // native ~10s minimum

                    double elapsed = now - ship.LastDockTime;
                    short stationId = ship.StationId!.Value;
                    ship.StayDocked = false;   // already false here; mirrors the ORDER_CHANGE LAUNCH arm's shape (FedSrv.CPP:7298)
                    UndockShipState(ship, sector);
                    Console.WriteLine($"[npc-autolaunch] ship {ship.Id} side {ship.SideId} auto-undocked from station {stationId} after {elapsed:F1}s docked");
                }
            }
        }
    }

}
