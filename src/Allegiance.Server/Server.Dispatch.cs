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
/// The inbound wire dispatcher of <see cref="Server"/>
/// (P328 stage-2 split from <c>Server.cs</c>). Partial class — shares the <c>Server</c>
/// type, so private field/method access across the split files is unaffected.
/// Hosts <c>ProcessDatagram</c>: unpack, validate and answer one inbound packet by
/// routing every wire message to its <c>Handle*</c> counterpart. Those handlers stay
/// in <c>Server.cs</c> and move out in later stages.
/// </summary>
public sealed partial class Server
{

    /// <summary>Synchronous entry retained for packet-level regression fixtures.
    /// Live receives use the same dispatcher with worker-prepared commands when
    /// receive.decouple is enabled.</summary>
    private void ProcessDatagram(ConnectionId from, byte[] data)
        => ProcessInboundPacket(from, InboundPacket.Prepare(data, decode: false));

    private void ProcessInboundPacket(ConnectionId from, InboundPacket inbound)
    {
        if (inbound.Error is { } error)
        {
            Console.WriteLine($"[wire] crc/ framing failure from {from}: {error.Message}");
            return;
        }

        foreach (var command in inbound.Messages)
        {
            // A packet may have been queued before LOGOFF retired the peer.
            // Native never reuses that player connection (FedSrv/FedSrv.CPP:5787-5800).
            if (_host.Transport is TcpTransport transport && transport.IsPeerClosed(from)) return;
            var m = command.Bytes;
            if (command.BadHeader)
            {
                Console.WriteLine($"[wire] bad message header from {from}: FedMessageHeader needs >= 4 bytes");
                continue;
            }
            ushort fmid = command.Fmid;
            if (fmid == ViewClusterRequest.Fmid && _config.BenchmarkSeed is not null) Console.WriteLine($"[p729-view] peer={from} admin={IsSpectatorAdmin(from)} name={_peerNames.GetValueOrDefault(from)}");
            if (SinglePlayerPaused && fmid != Ping.Fmid) continue;
            _host.InboundTrace?.Message(from, fmid, m.Length);

            if (fmid == Ping.Fmid)   // FM_CS_PING (Messages.h:280-284)
            {
                // P188 — a client keepalive/latency probe: stamp the server
                // time into the message and echo it back to the SAME connection
                // it came from (not broadcast), exactly native FM_CS_PING
                // (FedSrv.CPP:4897-4911: stamp timeServer then ForwardMessage to
                // the sender's own connection). No ship/mission/side context is
                // needed at all, so this is handled at the very top of the
                // dispatch, before any mission lookup — it works regardless of
                // game state (even with no mission assigned).
                try
                {
                    var ping = command.Read<Ping>();
                    // P645 — rate-limit client-triggered replies: the native
                    // client pings at most once per ~6 s (clintlib.cpp:2052-2085
                    // CheckServerLag, maxLagCheck=6.0f), so a peer exceeding the
                    // (much laxer) reply interval is a flood — its excess PINGs
                    // are DROPPED and counted, not answered, so a PING storm
                    // cannot amplify into an unbounded guaranteed-reply queue.
                    if (!_host.ShouldAnswerPing(from))
                        continue;
                    ping.TimeServer = (uint)(_simNow * 1000.0);
                    SendPeer(from, FedPacket.Pack(new[] { ping.Encode() }));
                    // P645 — gate the [ping] per-request line (sampled per peer)
                    // so a flood cannot force unbounded console I/O.
                    var pt = _host.PingLogTicket(from);
                    if (pt.Emit)
                        Console.WriteLine($"[ping] {from} <- echo (client {ping.TimeClient}, server {ping.TimeServer})");
                    else if (pt.Summarize)
                        Console.WriteLine($"[ping] {from}: {pt.Suppressed} more pings echoed (rate-limited log)");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad PING from {from}: {e.Message}");
                }
                continue;
            }

            if (fmid == ShowcaseFinaleRequest.Fmid)
            {
                try { ArmResearchFinale(from, command.Read<ShowcaseFinaleRequest>()); }
                catch (ArgumentException e) { Console.WriteLine($"[wire] bad SHOWCASE_FINALE from {from}: {e.Message}"); }
                continue;
            }

            if (fmid == LogonRequest.Fmid)
            {
                LogonRequest req;
                try { req = command.Read<LogonRequest>(); }
                catch (Exception e) { Console.WriteLine($"[wire] bad LOGONREQ from {from}: {e.Message}"); continue; }

                // P654 (H-28) — reject an out-of-range / control-character
                // CharacterName up front (before any allocation or seating).
                // Native: c_cbName = c_cbNameDB + 1 = 25, usable name = 24
                // (_Utility/Utility.h:60,64); boots when there is no NUL within
                // it (FedSrv.CPP:5817-5818). A 24+ name or one containing
                // control/format chars is refused (rate-limited log).
                if (req.CharacterName is { Length: > 0 } rn && HasIllegalName(rn))
                {
                    var nl = _duplicateLogonLog.Ticket(from.Value);
                    if (nl.Emit)
                        Console.WriteLine($"[logon] {from} '{ConsoleLog.Sanitize(rn)}': CharacterName rejected (length {rn.Length} > 24 or illegal chars; native c_cbName=25, H-28)");
                    else if (nl.Summarize)
                        Console.WriteLine($"[logon] {from}: {nl.Suppressed} more illegal CharacterNames refused (rate-limited)");
                    continue;
                }

                // P646 (review finding H3) — ONE LOGON PER CONNECTION. Native
                // asserts an existing player never sends LOGONREQ
                // (FedSrv.CPP:4832 `assert (pfm->fmid != FM_C_LOGONREQ);
                // shouldn't have a pfsPlayer yet if logging on`). The old code
                // had no such check, so a seated peer re-sending a version-
                // matching LOGONREQ got a fresh AllocateShipId(), a new
                // _pendingSideSelect entry, and — on seating — overwrote
                // _peerShips[from], orphaning the first ship in its sector with
                // nobody owning it (repeatable for unlimited orphan ships from
                // one connection). Two cases, neither allocates:
                if (_peerShips.ContainsKey(from))
                {
                    // Already seated: reject (logged, rate-limited), no ack, no
                    // id, no pending entry.
                    var dl = _duplicateLogonLog.Ticket(from.Value);
                    if (dl.Emit)
                        Console.WriteLine($"[logon] {from} '{req.CharacterName}' DUPLICATE LOGONREQ from a seated peer — rejected (one logon per connection, FedSrv.CPP:4832)");
                    else if (dl.Summarize)
                        Console.WriteLine($"[logon] {from}: {dl.Suppressed} more duplicate LOGONREQs from a seated peer suppressed (rate-limited)");
                    continue;
                }
                if (_pendingSideSelect.TryGetValue(from, out var pendingLogon))
                {
                    // Still awaiting side selection: the LOGONREQ is idempotent —
                    // re-send the SAME LogonAck (same reserved ship id, same
                    // validated/cookie), allocate nothing, register nothing new.
                    Console.WriteLine($"[logon] {from} '{pendingLogon.Request.CharacterName}' re-LOGONREQ while pending side-select — re-sent same LogonAck (ship {pendingLogon.ShipId}), no new id");
                    var reack = new LogonAck
                    {
                        CharNameOrFailureReason = pendingLogon.Request.VersionMatches ? $"Welcome {pendingLogon.Request.CharacterName}" : "wrong client version",
                        TimeServer = (uint)pendingLogon.Request.Time + 1,
                        ShipID = pendingLogon.Request.VersionMatches ? pendingLogon.ShipId : (short)0,
                        FValidated = pendingLogon.Request.VersionMatches,
                        FRetry = !pendingLogon.Request.VersionMatches,
                        Cookie = (byte)(pendingLogon.Request.DwCookie & 0xFF),
                    };
                    SendPeer(from, FedPacket.Pack(new[] { reack.Encode() }));
                    if (pendingLogon.Request.VersionMatches && _missions.Count > 0)
                        SendSideRoster(from);
                    continue;
                }

                // Native private standalone joins refuse duplicate names, case
                // insensitively (FedSrv/FedSrv.CPP:600-624). USEAUTH instead
                // removes the old player (:629; fsmission.cpp:1248-1276).
                // Refuse the newcomer explicitly: an untrusted name must never
                // evict an existing player. Reserve names during side selection
                // too, before allocating ids or authenticating privileges.
                var duplicateName = req.VersionMatches && _missions.Count > 0
                    && (_pendingSideSelect.Values.Any(p => string.Equals(p.Request.CharacterName ?? "", req.CharacterName ?? "", StringComparison.OrdinalIgnoreCase))
                        || _peerShips.Any(p => ReferenceEquals(p.Value.Ship.Mission, _missions[0])
                            && _peerNames.TryGetValue(p.Key, out var name)
                            && string.Equals(name, req.CharacterName ?? "", StringComparison.OrdinalIgnoreCase)));
                var validated = req.VersionMatches && !duplicateName;
                var failureReason = duplicateName ? "character name already in use in this mission" : "wrong client version";
                if (validated) AuthenticatePrivileges(from, req);
                // P619c — grant a connected HUMAN client (a character name NOT
                // under the MCP harness prefix) the longer transport liveness
                // grace, so a real player whose machine hiccups (OBS, a heavy
                // compile, 390 bots) is not mass-disconnected the way P611 cut
                // the play client on 2026-10-12. Harness bots keep the strict
                // limit. Best-effort: if the transport isn't the concrete
                // TcpTransport (e.g. a test fake), nothing happens.
                if (validated && req.CharacterName is { Length: > 0 } cn
                    && !cn.StartsWith(HarnessPrefix, StringComparison.Ordinal))
                    (_host.Transport as TcpTransport)?.SetPeerHuman(from, true);
                // P316: allocate a ship id ONLY for a validated logon with a
                // mission to join, AFTER the validation check — an
                // unauthenticated/invalid LOGONREQ flood burns no ids at all
                // (it previously ran (short)(_nextShipId++) for every request,
                // letting 65,536 raw requests exhaust the id space). The
                // allocator also skips any id still held by a live ship.
                // P281: allocate up-front so LogonAck carries a stable id, but
                // do NOT spawn the ship yet — a validated logon enters the
                // post-connect side-select hold (_pendingSideSelect) until it
                // picks a side (POSITIONREQ) or the timeout seats it.
                short shipId = 0;
                if (validated && _missions.Count > 0)
                {
                    shipId = AllocateShipId();
                    // P281 — post-connect side select: do NOT spawn the ship
                    // here. Today a joining peer gets zero say over its side
                    // (AssignShip seats it instantly via round-robin). Now
                    // register a PENDING side-select peer (no Ship, not yet in
                    // _peerShips). The ship is created — and the whole
                    // join/launch burst sent — only once the peer chooses a
                    // side (POSITIONREQ → CompleteSideSelection) or the
                    // side-select timeout seats it.
                    _pendingSideSelect[from] = new PendingSideSelect(shipId, req, _simNow, _nextJoinSeq++);
                    Console.WriteLine($"[launch] peer {from} '{req.CharacterName}' pending side-select (ship id {shipId} reserved; launch deferred to side choice)");
                }

                var ack = new LogonAck
                {
                    CharNameOrFailureReason = validated ? $"Welcome {req.CharacterName}" : failureReason,
                    TimeServer = (uint)req.Time + 1,
                    ShipID = validated ? shipId : (short)0,
                    FValidated = validated,
                    FRetry = !req.VersionMatches,
                    Cookie = (byte)(req.DwCookie & 0xFF),
                };
                SendPeer(from, FedPacket.Pack(new[] { ack.Encode() }));
                Console.WriteLine($"[logon] {from} '{req.CharacterName}' validated={validated} -> " +
                                  $"'{ack.CharNameOrFailureReason}' (server time {ack.TimeServer}, ship {ack.ShipID})");

                // Push the side roster AFTER the logon ack so a packet-order-
                // fixed harness sees LOGONACK as its first frame (the roster is
                // what the side-select screen is rendered from).
                if (validated && _missions.Count > 0)
                    SendSideRoster(from);

                // P281 — the join/launch burst is deferred to
                // CompleteJoinAndLaunch, called from CompleteSideSelection once
                // this peer picks a side (or, with the opt-in P763 auto-seat
                // enabled, when its deadline fires). Until then the peer is only
                // in _pendingSideSelect, so a silent/AFK client never blocks the
                // P180 ready-up auto-start.
            }
            else if (fmid == ShipUpdate.Fmid)
            {
                // Step 3: inbound control input. Decode and, for the peer's own
                // ship, ONLY set the ship's Controls/StateM. Nothing moves here —
                // the ship's Motion lives in Mission.Update (the authoritative
                // tick). Running on the sim thread (Drain), so setting controls
                // is race-free w.r.t. the tick loop, and the payload's
                // position/velocity/orientation echoes are ignored as authority.
                try
                {
                    var u = command.Read<ShipUpdate>();
                    if (_peerShips.TryGetValue(from, out var live))
                    {
                        // P180 — a genuine pre-game hold: while the mission is
                        // still NotStarted (awaiting ready-up), a ship must not
                        // be able to fly, so inbound control input is ignored
                        // (report and drop, matching this file's other
                        // not-Started refusals like HandleSuicide). This is a
                        // real, necessary gate — the control path previously had
                        // zero Stage check unlike the 10+ other actions — a ship
                        // must not move while the mission hasn't started.
                        if (live.Ship.Mission is not { Stage: MissionStage.Started })
                        {
                            EnsurePeerUpdate(from).DroppedPreGame++;
                            ConsoleLog.Write($"[ctrl] {from} ship {live.Ship.Id}: mission {(live.Ship.Mission is null ? "none" : $"stage {live.Ship.Mission.Stage}")} != STAGE_STARTED — SHIP_UPDATE ignored (pre-game hold)");
                        }
                        else if (live.Ship.ParentShip is not null)
                        {
                            EnsurePeerUpdate(from).DroppedBoarded++;
                            ConsoleLog.Write($"[ctrl] {from} ship {live.Ship.Id}: boarded gunner — SHIP_UPDATE ignored, use ACTIVE_TURRET_UPDATE (FedSrv.CPP:4859)");
                        }
                        else if (live.Ship.Sector is null || live.Ship.StationId is not null)
                        {
                            // P518 — native also requires the ship to be IN a
                            // cluster (FedSrv.CPP:4857 `pship->GetCluster()`);
                            // a not-yet-in-a-sector / off-the-flight-stream ship's
                            // pose is dropped (and now counted, so a silent drop
                            // is never invisible again).
                            EnsurePeerUpdate(from).DroppedNotInCluster++;
                            ConsoleLog.Write($"[ctrl] {from} ship {live.Ship.Id}: not in a cluster (Sector null) — SHIP_UPDATE ignored (FedSrv.CPP:4857 GetCluster())");
                        }
                        else
                        {
                            // P507 — native own-ship model (part 1, server):
                            // the peer's SHIP_UPDATE carries its reported pose +
                            // cookie, and the server sanity-checks it exactly as
                            // native FedSrv.CPP:4851-4864 → SetShipUpdate
                            // (fsship.cpp:1173-1200) → ProcessShipUpdate
                            // (shipIGC.cpp:3139-3210). Two audiences:
                            //
                            //  * LEGACY (pre-P508) clients send Time=0 and a zero
                            //    pose (ConnectionSession.CommandControls and the
                            //    test bots). Detect that and keep working
                            //    controls-only exactly as before P507.
                            //  * NATIVE clients (P508) send a real time + pose +
                            //    the cookie from SET_CLUSTER; those run the full
                            //    accept/reject pipeline.
                            var payload = u.Payload;
                            bool legacy = u.Time == 0 || payload.Position.LengthSquared() == 0f;
                            if (legacy)
                            {
                                live.Ship.Controls = payload.Controls;
                                live.Ship.StateM = payload.StateM;
                                var c = payload.Controls;
                                ConsoleLog.Write($"[ctrl] {from} ship {live.Ship.Id} LEGACY controls-only (time 0 / zero pose) controls=(yaw {c.Yaw:F3} pitch {c.Pitch:F3} " +
                                                  $"roll {c.Roll:F3} thr {c.Throttle:F3}) state=0x{payload.StateM:X}");
                            }
                            else if (EnsurePeerUpdate(from).Cookie != u.Cookie)
                            {
                                // Native drops the whole update on a cookie
                                // mismatch — the client hasn't applied a pending
                                // SET_CLUSTER/SHIP_RESET yet (FedSrv.CPP:4854).
                                // Controls are NOT applied either, exactly native.
                                // P518 — counted, so a cookie-divergence silent drop
                                // (the P518 root cause) is visible in the summary.
                                EnsurePeerUpdate(from).DroppedCookie++;
                                ConsoleLog.Write($"[ctrlak] {from} ship {live.Ship.Id}: cookie mismatch (expect 0x{EnsurePeerUpdate(from).Cookie:X2}, got 0x{u.Cookie:X2}) — SHIP_UPDATE dropped (FedSrv.CPP:4854)");
                            }
                            else
                            {
                                // P541 — a non-legacy (real-time + pose) update marks
                                // this peer as a native client that flies its own ship
                                // locally (sticky). The server must not autopilot such
                                // a ship (see the CHATBUOY gate). Set here, before the
                                // cookie/accept pipeline, so even a momentarily-dropped
                                // update still identifies the peer as client-flown.
                                EnsurePeerUpdate(from).NativeClient = true;
                                ProcessPeerShipUpdate(from, live.Ship, u, EnsurePeerUpdate(from));
                            }
                        }
                    }
                    else
                    {
                        // P518 — count the drop so it surfaces in the summary (a
                        // peer that once had a ship may still have P507 state).
                        if (_peerShipUpdate.TryGetValue(from, out var orphanPu)) orphanPu.DroppedNoShip++;
                        ConsoleLog.Write($"[ctrl] {from}: ship update for a peer with no ship — ignored");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad SHIP_UPDATE from {from}: {e.Message}");
                }
            }
            else if (fmid == ActiveTurretUpdate.Fmid)   // FM_C_ACTIVE_TURRET_UPDATE (Messages.h:673-676)
            {
                // P109 — a turret gunner actively firing the turret, sent
                // INSTEAD of SHIP_UPDATE(24) while it controls the turret
                // (clintlib.cpp:1884-1893). Routes to the real
                // ProcessShipUpdate(const ClientActiveTurretUpdate&) port
                // (shipIGC.cpp:3349-3392) on the gunner's own ship, gated on the
                // sender actually being a boarded gunner (FedSrv.CPP:4873-4875).
                try
                {
                    var u = command.Read<ActiveTurretUpdate>();
                    if (_peerShips.TryGetValue(from, out var live))
                        HandleActiveTurretUpdate(from, live.Ship, u.Atu);
                    else
                        ConsoleLog.Write($"[turret] {from}: no ship — ACTIVE_TURRET_UPDATE ignored");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad ACTIVE_TURRET_UPDATE from {from}: {e.Message}");
                }
            }
            else if (fmid == InactiveTurretUpdate.Fmid)  // FM_C_INACTIVE_TURRET_UPDATE (Messages.h:678-679)
            {
                // P109 — the gunner stops actively controlling/firing the turret
                // (sent when StateM drops weaponsMaskIGC, clintlib.cpp:1894-1899).
                // Clears the same "shooting" state bit ACTIVE_TURRET_UPDATE set.
                try
                {
                    _ = command.Read<InactiveTurretUpdate>();   // no fields; decode validates cbmsg=4
                    if (_peerShips.TryGetValue(from, out var live))
                    {
                        var gunner = live.Ship;
                        if (gunner.Mission?.Stage == MissionStage.Started && gunner.StationId is null && gunner.Sector is not null && gunner.ParentShip is { StationId: null } && gunner.TurretId is not null)
                        {
                            // Native server-side stop-shooting: SetInactiveTurretUpdate
                            // → GetIGCShip()->SetStateM(0) (fsship.cpp:1223) — a full
                            // assignment to 0, clearing the weaponsMaskIGC "shooting"
                            // bit (and every other state bit) exactly as native does.
                            gunner.StateM = 0;
                            if (ShouldLogTurret(from)) ConsoleLog.Write($"[turret] ship {gunner.Id} INACTIVE_TURRET_UPDATE — SetStateM(0) (stop shooting, fsship.cpp:1223)");
                        }
                        else
                        {
                            ConsoleLog.Write($"[turret] {from}: INACTIVE_TURRET_UPDATE from a non-gunner — ignored (FedSrv.CPP:4887-4889)");
                        }
                    }
                    else
                    {
                        ConsoleLog.Write($"[turret] {from}: no ship — INACTIVE_TURRET_UPDATE ignored");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad INACTIVE_TURRET_UPDATE from {from}: {e.Message}");
                }
            }
            else if (fmid == ChatMessage.Fmid)
            {
                // Step: relay an inbound FM_CS_CHATMESSAGE(23). Decode, resolve
                // the sender's ship (and thus its SideId — P21 gave ships a real
                // SideId), then forward the ORIGINAL bytes to the peers that the
                // target selects. Only two targets are wired: Everyone (every
                // connected peer, mirroring BroadcastShipStates' loop over
                // _peerShips) and Team (peers whose SideId matches the sender's,
                // reusing the side field rather than reinventing comparison).
                // Any other ChatTarget is reported and dropped — not broadcast
                // as a leniency fallback (that would be a real behavior bug) and
                // not thrown (an unimplemented target from a client must not
                // crash the server). Matches README's "report and ignore" rule.
                try
                {
                    var chat = command.Read<ChatMessage>();
                    if (!_peerShips.TryGetValue(from, out var senderLive))
                    {
                        ConsoleLog.Write($"[chat] {from}: chat from a peer with no ship — ignored");
                    }
                    else
                    {
                        // P554 — the admin-cheat hook (port of native DoCheatCode,
                        // FedSrv.CPP:5023) runs BEFORE ballot commands / relay: a
                        // line a privileged player's cheat parser consumes is never
                        // forwarded as chat (native bForward=false). A line it does
                        // NOT consume falls through to the ordinary relay below.
                        if (!TryHandleAdminCheat(from, senderLive.Ship, chat)
                            && !TryHandleBallotCommand(from, senderLive.Ship, chat.Message))
                        {
                            // P654 (H-24) — the per-peer chat budget (native's
                            // DecrementChatBudget, FedSrv.CPP:3372 / fsship.h:449-465):
                            // when a peer has exhausted its token bucket, the relay is
                            // dropped so one chatter cannot amplify into an unbounded
                            // N-fold fan-out that disconnects recipients (H-05).
                            // Group-noecho chat is the native UNPAID send
                            // (bPay = chatTarget != CHAT_GROUP_NOECHO,
                            // FedSrv.CPP:3372); every other target pays.
                            if (!TryChargeChatBudget(from, (ChatTarget)chat.ChatTarget != ChatTarget.GroupNoEcho))
                            {
                                ConsoleLog.Write($"[chat] {from}: chat budget exhausted — message dropped (native chat budget, H-24)");
                            }
                            else
                            {
                                var targets = SelectChatTargets(from, chat, senderLive.Ship.SideId);
                                Console.WriteLine($"[chat] {from} ship {senderLive.Ship.Id} side {sideStr(senderLive.Ship.SideId)} '{ConsoleLog.Sanitize(chat.Message, 128)}' " +
                                                  $"target {chat.ChatTarget} -> {targets.Length} peer(s)");
                                if (targets.Length > 0)
                                {
                                    senderLive.Ship.Mission?.Skills?.Record(senderLive.Ship.SideId,
                                        chat.VoiceOver >= 0 ? Allegiance.Core.Model.ShowcaseSkill.Voice : Allegiance.Core.Model.ShowcaseSkill.Chat);
                                    // P654 (H-05/H-10) — re-encode the relay from the
                                    // decoded fields instead of forwarding the client's
                                    // raw bytes: cap the message length (native UI ~255)
                                    // and stamp the sender's OWN ship id into sidSender
                                    // (native overwrites pfmChat->cd.sidSender =
                                    // GetObjectID(), FedSrv.CPP:5249), so an oversized
                                    // message cannot be amplified verbatim and a forged
                                    // sender cannot be impersonated.
                                    var outChat = chat;
                                    if (outChat.Message.Length > 255)
                                        outChat.Message = outChat.Message.Substring(0, 255);
                                    outChat.Cd.SidSender = senderLive.Ship.Id;
                                    byte[] packet = FedPacket.Pack(new[] { outChat.Encode() });
                                    foreach (var t in targets) SendPeer(t, packet);
                                }
                                // P457 — a relayed COMMAND chat (real CommandID, non-
                                // Everyone target) queues a queued order on each same-side
                                // real-player recipient the relay delivers to.
                                QueueOrdersFromRelayedChat(from, senderLive.Ship, chat, targets);

                                // P469 — an Individual-family chat naming a same-side NPC
                                // with a legal CommandID makes that DRONE obey the order
                                // immediately (native CommandToDrone, FedSrv.CPP:3557-3573).
                                // Real-player recipients are owned by the P457 path above.
                                TryCommandToDrone(from, senderLive.Ship, chat);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad CHATMESSAGE from {from}: {e.Message}");
                }
            }
            else if (fmid == Vote.Fmid)
            {
                // Step: P187 — record a client's vote on a pending ballot
                // (FM_C_VOTE, Messages.h:185-188), the port of native
                // CFSMission::TallyVote (fsmission.cpp:1207-1219). Decode, find
                // the named ballot in the SENDER'S OWN mission, and cast the
                // vote if the sender is an eligible voter. Any refusal (ballot
                // missing/already resolved, or the sender not eligible) is
                // logged and ignored — never fatal, never creates a ballot.
                try
                {
                    var vote = command.Read<Vote>();
                    if (!_peerShips.TryGetValue(from, out var voterLive))
                    {
                        ConsoleLog.Write($"[vote] {from}: VOTE from a peer with no ship — ignored");
                    }
                    else if (voterLive.Ship.Mission is not { } voteMission)
                    {
                        ConsoleLog.Write($"[vote] ship {voterLive.Ship.Id}: VOTE for a ship with no mission — ignored");
                    }
                    else
                    {
                        var list = BallotsOf(voteMission);
                        var ballot = list.FirstOrDefault(b => b.Id == vote.BallotId);
                        if (ballot is null)
                        {
                            ConsoleLog.Write($"[vote] ship {voterLive.Ship.Id}: VOTE for unknown/resolved ballot {vote.BallotId} — ignored");
                        }
                        else
                        {
                            short voterSide = voterLive.Ship.SideId ?? -1;
                            bool cast = ballot.TryCastVote(voterLive.Ship.Id, voterSide, vote.Agree);
                            Console.WriteLine($"[vote] ship {voterLive.Ship.Id} ballot {ballot.Id} ({ballot.Type}) " +
                                              $"{(vote.Agree ? "for" : "against")} side {voterSide} -> {(cast ? "recorded" : "REFUSED (not eligible / already voted)")}");
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad VOTE from {from}: {e.Message}");
                }
            }
            else if (fmid == ChatBuoy.Fmid)
            {
                // Step: relay an inbound FM_CS_CHATBUOY(108) — the message
                // BaseClient::SendChat actually uses for a chat targeting a buoy
                // (clintlib.cpp:2432-2444: otTarget == OT_buoy → CHATBUOY, else
                // CHATMESSAGE). The commander's real dispatch to a docked miner
                // (CommanderResearchAndExpand.cs:1610,1615-1618 passes
                // otTarget = buoy.GetObjectType(), oidTarget = buoy.GetObjectID())
                // therefore arrives HERE, not as CHATMESSAGE(23) — previously
                // logged "[wire] unsupported fmid 108 — ignored" and silently
                // dropped. Same routing as CHATMESSAGE: decode, resolve the
                // sender's ship/SideId, then call the exact same
                // SelectChatTargets method (its parameter is now the shared
                // IChatRoutingTarget, so no duplicated switch), and forward the
                // ORIGINAL bytes to whoever it selects (verbatim relay, matching
                // CS_PING's echo and ChatMessage's own relay, Server.cs:1281).
                try
                {
                    var buoy = command.Read<ChatBuoy>();
                    // Native stage gate: FedSrv/FedSrv.CPP:5297. Reject poisoned
                    // positions before routing, orders, logging or relay.
                    if (!_peerShips.TryGetValue(from, out var buoySender)
                        || buoySender.Ship.Mission?.Stage != MissionStage.Started
                        || buoy.Db.Position.Any(v => !float.IsFinite(v))) continue;
                    if (!_peerShips.TryGetValue(from, out var senderLive))
                    {
                        ConsoleLog.Write($"[chatbuoy] {from}: chat from a peer with no ship — ignored");
                    }
                    else
                    {
                        // P654 (H-24) — per-peer chat budget: when exhausted,
                        // the CHATBUOY (its relay AND its server-side goto
                        // translation) is dropped, bounding the N-fold relay
                        // amplification of H-05 (native DecrementChatBudget,
                        // FedSrv.CPP:3372).
                        if (!TryChargeChatBudget(from, (ChatTarget)buoy.ChatTarget != ChatTarget.GroupNoEcho))
                        {
                            ConsoleLog.Write($"[chatbuoy] {from}: chat budget exhausted — CHATBUOY dropped (native chat budget, H-24)");
                        }
                        else
                        {
                            var targets = SelectChatTargets(from, buoy, senderLive.Ship.SideId);
                            Console.WriteLine($"[chatbuoy] {from} ship {senderLive.Ship.Id} side {sideStr(senderLive.Ship.SideId)} " +
                                              $"'{ConsoleLog.Sanitize(buoy.Message, 128)}' target {buoy.ChatTarget} buoy {buoy.Db.BuoyId} -> {targets.Length} peer(s)");

                            // P403 — receiving-side execution. Native's c_buoyCluster buoy is
                            // a "go to this OTHER sector" order (deleted on arrival in the
                            // cluster, FedSrv.CPP:3302), and ResetWaypoint gives an OT_buoy
                            // target the plain c_oGoto objective (default case, shipIGC.cpp:
                            // 4053,4160). This port's v1 has no wingman/squadron-selection
                            // concept (documented in P403_BUOY_GOTO_COMMAND.md), so the
                            // commanded ship is the SENDER's own ship — and only when the
                            // order is addressed to it (the starmap trigger sends
                            // Individual with oidRecipient = own ship id). The server, as the
                            // ship's owner/simulation authority, translates the received
                            // CHATBUOY into a real move order the ship's update loop acts on
                            // (warp-pathing via the shared navigation resolver). A CHATBUOY
                            // addressed to anyone else (a teammate, a team, everyone) is
                            // still relayed as chat but does NOT re-route this sender's own
                            // ship — commanding a different subordinate is the documented
                            // follow-up. The relay below is unchanged — the order also still
                            // lands in the recipient peers' Events.ChatBuoys, matching the
                            // native echo.
                            bool orderTargetsSelf =
                                buoy.Cd.ChatTarget is (int)ChatTarget.Individual or (int)ChatTarget.IndividualNoFilter or (int)ChatTarget.IndividualEcho
                                && buoy.Cd.OidRecipient == senderLive.Ship.Id;
                            // P541 — re-route the sender's own ship ONLY when the server
                            // actually flies it. A real P508 native client flies its own
                            // ship locally (and steers it toward the clicked sector via its
                            // own client autopilot); the server autopiloting it too would
                            // fight the local sim (measured: a native-pose client's accepted
                            // static poses pin the server's gotoing ship at spawn). Legacy /
                            // bot senders (NativeClient false) still get the server-side
                            // GotoPlan move (the P403 behaviour).
                            bool serverFliesSender = !(_peerShipUpdate.TryGetValue(from, out var senderPu) && senderPu.NativeClient);
                            if (buoy.Db.Type == DataBuoyIGC.CBuoyCluster && orderTargetsSelf && serverFliesSender)
                            {
                                senderLive.Ship.SetGoto(new GotoOrder(
                                    GotoKind.Cluster,
                                    buoy.Db.ClusterId,
                                    Vector3.Zero));
                            }

                            if (targets.Length > 0)
                            {
                                // P654 (H-05/H-10) — re-encode the relay: cap the
                                // message and stamp the sender's OWN ship id into
                                // sidSender (native FedSrv.CPP:5301), so an oversized
                                // buoy cannot be amplified verbatim and its sender
                                // cannot be spoofed.
                                var outBuoy = buoy;
                                if (outBuoy.Message.Length > 255)
                                    outBuoy.Message = outBuoy.Message.Substring(0, 255);
                                outBuoy.Cd.SidSender = senderLive.Ship.Id;
                                byte[] packet = FedPacket.Pack(new[] { outBuoy.Encode() });
                                foreach (var t in targets) SendPeer(t, packet);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad CHATBUOY from {from}: {e.Message}");
                }
            }
            else if (fmid == HighlightCluster.Fmid)
            {
                // Step: relay an inbound FM_CS_HIGHLIGHT_CLUSTER(199) — the
                // commander marking a sector on the map for their own teammates
                // (Xynth #208). Pure relay (FedSrv.CPP:7369-7375, the whole
                // handler): decode, resolve the sender's ship (and thus its
                // SideId — the ship's real side, P21), then forward the ORIGINAL
                // bytes to every peer on that same side. This mirrors the native
                // body verbatim —
                //   g.fm.ForwardMessage(CFSSide::FromIGC(pfsPlayer->GetSide())->GetGroup(), pfmHC, FM_GUARANTEED);
                // (FedSrv.CPP:7374) — the sender's own side GROUP. No validation,
                // no cluster-existence check, no sender-side check, no
                // STAGE_STARTED gate in the native case (Messages.h:1033-1036
                // declares only clusterID + highlight), so this port adds none.
                // Reuses the exact relay shape of the CHATBUOY/CHATMESSAGE
                // branches: resolve SideId → FedPacket.Pack(new[]{ m }) verbatim
                // → SendPeer to the matching-side peers. The
                // _peerShips.Where(SideId == sender's) predicate is the SAME one
                // the Team chat target uses (SelectChatTargets, Server.cs:2370-2374),
                // so no new relay infrastructure is introduced.
                try
                {
                    var hl = command.Read<HighlightCluster>();
                    if (!_peerShips.TryGetValue(from, out var senderLive))
                    {
                        ConsoleLog.Write($"[highlight] {from}: highlight from a peer with no ship — ignored");
                    }
                    else
                    {
                        var targets = _peerShips
                            .Where(kv => kv.Value.Ship.SideId == senderLive.Ship.SideId)
                            .Select(kv => kv.Key)
                            .ToArray();
                        Console.WriteLine($"[highlight] {from} ship {senderLive.Ship.Id} side {sideStr(senderLive.Ship.SideId)} " +
                                          $"cluster {hl.ClusterId} highlight {hl.Highlight} -> {targets.Length} same-side peer(s)");
                        if (targets.Length > 0)
                        {
                            // P654 (H-05) — re-encode the fixed-size relay from the
                            // decoded fields instead of forwarding the client's raw
                            // bytes (which a fixed-size decoder accepts even 65,535
                            // bytes long) — this bounds the per-message fan-out to
                            // exactly FixedSize bytes. Budget-charged (H-24) so a
                            // flood cannot amplify N-fold.
                            if (!TryChargeChatBudget(from))
                            {
                                ConsoleLog.Write($"[highlight] {from}: budget exhausted — HIGHLIGHT_CLUSTER dropped (H-24)");
                            }
                            else
                            {
                                byte[] packet = FedPacket.Pack(new[] { hl.Encode() });
                                foreach (var t in targets) SendPeer(t, packet);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad HIGHLIGHT_CLUSTER from {from}: {e.Message}");
                }
            }
            else if (fmid == Docked.Fmid)
            {
                // Step: handle an inbound FM_C_DOCKED(48). The native handler
                // (FedSrv.CPP:7031-7135) is genuinely dual-purpose — read the
                // whole block — and this branch now mirrors both halves:
                //  • stationID == NA  → the pure-undock announce, already
                //    ported (PerformUndock) and preserved exactly as-is.
                //  • stationID != NA  → the teleport/relocation dock request
                //    (P110): a displaced ship lands at a specific friendly
                //    restart-capable station, acknowledged with
                //    FM_S_TELEPORT_ACK(102). Native gating (all in
                //    FedSrv.CPP:7040-7077): only a ship the server believes is
                //    already docked somewhere (GetStation()!=NULL, :7040) and
                //    that is not a boarded child (pshipParent==NULL, :7045)
                //    participates; a non-NA request names a station that must
                //    be real, on the player's side or an ally's, and
                //    c_sabmRestart-capable — else the ACK answers stationID=NA
                //    (rejected, :7068) and the ship stays put. The old
                //    "obsolete half dropped" handling is gone: this is exactly
                //    the legit-request direction Messages.h:402-404 calls
                //    obsolete *as a general client-dock trigger*, but the
                //    server still answers a real one (the "port the responder,
                //    defer the trigger" discipline).
                try
                {
                    var docked = command.Read<Docked>();
                    if (!_peerShips.TryGetValue(from, out var undockLive))
                    {
                        ConsoleLog.Write($"[undock] {from}: dock message from a peer with no ship — ignored");
                    }
                    else if (undockLive.Ship.StationId is null)
                    {
                        ConsoleLog.Write($"[undock] {from} ship {undockLive.Ship.Id}: not docked server-side — dock message ignored (gate: GetStation()!=NULL, FedSrv.CPP:7040)");
                    }
                    else if (docked.StationId != -1)
                    {
                        // P110 — the teleport/relocation dock request.
                        PerformTeleport(from, undockLive, docked.StationId);
                    }
                    else
                    {
                        PerformUndock(from, undockLive);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad DOCKED from {from}: {e.Message}");
                }
            }
            else if (fmid == BuyLoadoutRequest.Fmid)   // FM_C_BUY_LOADOUT (Messages.h:65-68)
            {
                // The client's requested loadout (hull + parts) — this message
                // was not received at all before P61. Decode, resolve against
                // the peer's mission core, apply to the ship's generalized
                // loadout, and answer with a real FM_S_BUY_LOADOUT_ACK carrying
                // the actual mounted-part tail (nParts = real count, not 0).
                try
                {
                    var buy = command.Read<BuyLoadoutRequest>();
                    if (!_peerShips.TryGetValue(from, out var buyLive))
                    {
                        ConsoleLog.Write($"[loadout] {from}: BUY_LOADOUT from a peer with no ship — ignored");
                    }
                    else
                    {
                        ApplyLoadout(from, buyLive, buy);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad BUY_LOADOUT from {from}: {e.Message}");
                }
            }
            else if (fmid == QuitMission.Fmid)
            {
                try { HandleQuitMission(from, command.Read<QuitMission>()); }
                catch (Exception e) { ConsoleLog.Write($"[wire] bad QUIT_MISSION from {from}: {e.Message}"); }
            }
            else if (fmid == PositionReq.Fmid)   // FM_C_POSITIONREQ (Messages.h:320-323)
            {
                // P184 — a client's request to be seated on a side. Port of
                // FedSrv.CPP:6658-6707 → RequestPosition (fsmission.cpp:4729-4774).
                // Validate the side exists (refuse with DELPOSITIONREQ(DPR_SideGone)
                // to the requester ONLY); then auto-accept when the target side has
                // no leader (a leaderless side is empty in this port — TryJoinSide
                // seats the first player as leader), otherwise broadcast
                // POSITIONREQ(40) whole-mission and record the pending request for
                // the leader to approve. P655 validates native's supported
                // CheckPositionRequest rules before either path, and rechecks
                // approvals/auto-accept after countdown or occupancy changes.
                // Initial choices are checked before spawning a ship.
                try
                {
                    var pr = command.Read<PositionReq>();
                    // P281 — the initial side-select request: a peer in
                    // _pendingSideSelect (logged on, ship not yet spawned) that
                    // sends POSITIONREQ is choosing its starting side. Record the
                    // choice on the pending record; the per-tick side-select drain
                    // (AdvanceSideSelection) seats it in LOGON order, so side
                    // assignment stays deterministic. NA(-1) here means "auto-pick"
                    // (recorded as -1 so the drain seats it immediately). A peer
                    // that sends nothing stays pending unless the opt-in
                    // ServerConfig.SideSelectAutoSeatSeconds timer is enabled
                    // (P763; off by default, matching native).
                    if (_pendingSideSelect.ContainsKey(from))
                    {
                        // A real side = the chosen side. NA(-1) = auto-pick NOW
                        // (recorded as -1 so the drain seats it immediately,
                        // rather than leaving RequestedSide null which means
                        // "no choice yet").
                        _pendingSideSelect[from] = _pendingSideSelect[from].WithRequestedSide(pr.SideId);
                        Console.WriteLine($"[posreq] {from}: POSITIONREQ side {pr.SideId} recorded for pending side-select peer");
                    }
                    else if (!_peerShips.TryGetValue(from, out var reqLive))
                    {
                        ConsoleLog.Write($"[posreq] {from}: POSITIONREQ from a peer with no ship — ignored");
                    }
                    else if (reqLive.Ship.Mission is not Mission mission)
                    {
                        ConsoleLog.Write($"[posreq] {from} ship {reqLive.Ship.Id}: no mission — POSITIONREQ ignored");
                    }
                    else
                    {
                        var targetSide = mission.Sides.FirstOrDefault(s => s.Id == pr.SideId);
                        if (targetSide is null)
                        {
                            // Side no longer exists: deny to the requester only
                            // (FedSrv.CPP:6692-6704, not whole-mission).
                            var deny = new DelPositionReq { ShipId = reqLive.Ship.Id, SideId = pr.SideId, Reason = DelPositionReqReason.SideGone };
                            SendPeer(from, FedPacket.Pack(new[] { deny.Encode() }));
                            ConsoleLog.Write($"[posreq] ship {reqLive.Ship.Id} -> side {pr.SideId} DENIED (DPR_SideGone) to requester only");
                        }
                        else if (CheckPositionRequest(mission, pr.SideId, reqLive.Ship) is { } refusal)
                        {
                            RefuseSideSelection(from, reqLive.Ship.Id, pr.SideId, refusal);
                        }
                        else if (mission.LockSides)
                        {
                            // P186 — the mission owner locked side changes:
                            // refuse any side-switch POSITIONREQ with
                            // DELPOSITIONREQ(DPR_SidesLocked) to the requester
                            // only, mirroring native CheckPositionRequest's
                            // bLockSides gate (fsmission.cpp:4590). This side has
                            // no SIDE_TEAMLOBBY (this port seats players directly
                            // via config), so every POSITIONREQ is a real side
                            // switch and is refused while LockSides is on. Placed
                            // before the auto-accept/leader branches because native
                            // runs CheckPositionRequest before RequestPosition's
                            // accept logic — the lock wins over both.
                            var deny = new DelPositionReq { ShipId = reqLive.Ship.Id, SideId = pr.SideId, Reason = DelPositionReqReason.SidesLocked };
                            SendPeer(from, FedPacket.Pack(new[] { deny.Encode() }));
                            ConsoleLog.Write($"[posreq] ship {reqLive.Ship.Id} -> side {pr.SideId} DENIED (DPR_SidesLocked) to requester only — sides locked");
                        }
                        else if (targetSide.LeaderShipId is null || targetSide.AutoAccept)
                        {
                            // No leader on the target side yet (hence empty) → auto-
                            // accept immediately (native RequestPosition's leaderless
                            // fast path, fsmission.cpp:4757; this port folds
                            // fAutoAcceptLeaders to always-on for leaderless sides —
                            // the config isn't ported). P186: an already-led side
                            // with Side.AutoAccept set also auto-accepts here
                            // (native RequestPosition's rgfAutoAccept[sideID] branch,
                            // fsmission.cpp:4759), so a request to it seats the
                            // requester instead of going pending. TryJoinSide seats
                            // and announces; the accept broadcasts cover the
                            // POSITIONREQ(40) notice.
                            Console.WriteLine($"[posreq] ship {reqLive.Ship.Id} -> side {pr.SideId}: {(targetSide.LeaderShipId is null ? "no leader" : "auto-accept")} — auto-accept via TryJoinSide");
                            TryJoinSide(reqLive.Ship, pr.SideId);
                        }
                        else
                        {
                            // Side has a leader: broadcast the pending request and
                            // record it for the leader to approve.
                            BroadcastPositionReq(mission, reqLive.Ship.Id, pr.SideId);
                            _pendingJoinRequests[from] = (reqLive.Ship.Id, pr.SideId);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad POSITIONREQ from {from}: {e.Message}");
                }
            }
            else if (fmid == PositionAck.Fmid)   // FM_C_POSITIONACK (Messages.h:331-336)
            {
                // P184 — the team leader accepts or rejects a pending side-join
                // request. Port of FedSrv.CPP:6709-6747. Gates (defensive re-checks,
                // never trusting the client's belief it's still leader): the sender
                // must be seated on the named side AND be that side's current leader;
                // the target ship must still exist in this mission (else silently
                // ignore — "player already went away"). On accept: seat the requester
                // via TryJoinSide after removing the pending request. On reject:
                // remove the pending request and broadcast DELPOSITIONREQ(DPR_Rejected)
                // whole-mission. Silent no-op if there was no matching pending request.
                try
                {
                    var pa = command.Read<PositionAck>();
                    if (!_peerShips.TryGetValue(from, out var ackLive))
                    {
                        ConsoleLog.Write($"[posack] {from}: POSITIONACK from a peer with no ship — ignored");
                    }
                    else if (ackLive.Ship.Mission is not Mission mission)
                    {
                        ConsoleLog.Write($"[posack] {from} ship {ackLive.Ship.Id}: no mission — POSITIONACK ignored");
                    }
                    else if (ackLive.Ship.SideId != pa.SideId)
                    {
                        ConsoleLog.Write($"[posack] {from} ship {ackLive.Ship.Id}: ACK side {pa.SideId} != own side {ackLive.Ship.SideId} — refused");
                    }
                    else
                    {
                        var targetSide = mission.Sides.FirstOrDefault(s => s.Id == pa.SideId);
                        if (targetSide is null || targetSide.LeaderShipId != ackLive.Ship.Id)
                        {
                            ConsoleLog.Write($"[posack] {from} ship {ackLive.Ship.Id}: not {pa.SideId}'s current leader — ACK refused");
                        }
                        else
                        {
                            var targetConn = FindConnectionOfShip(mission, pa.ShipId);
                            if (targetConn is null)
                            {
                                // Target ship already went away — silently ignore.
                                ConsoleLog.Write($"[posack] ship {pa.ShipId} not in mission — ACK ignored (player already went away)");
                            }
                            else if (pa.FAccepted)
                            {
                                if (RemovePendingJoinRequest(targetConn.Value, pa.SideId))
                                {
                                    // Still exists + request still there → seat them.
                                    var targetShip = _peerShips[targetConn.Value].Ship;
                                    if (CheckPositionRequest(mission, pa.SideId, targetShip) is { } refusal)
                                        RefuseSideSelection(targetConn.Value, targetShip.Id, pa.SideId, refusal);
                                    else TryJoinSide(targetShip, pa.SideId);
                                }
                                else
                                {
                                    Console.WriteLine($"[posack] ship {pa.ShipId}: no matching pending request — accept no-op");
                                }
                            }
                            else
                            {
                                if (RemovePendingJoinRequest(targetConn.Value, pa.SideId))
                                {
                                    BroadcastDelPositionReq(mission, pa.ShipId, pa.SideId, DelPositionReqReason.Rejected);
                                }
                                else
                                {
                                    Console.WriteLine($"[posack] ship {pa.ShipId}: no matching pending request — reject no-op");
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad POSITIONACK from {from}: {e.Message}");
                }
            }
            else if (fmid == DelPositionReq.Fmid)   // FM_CS_DELPOSITIONREQ (Messages.h:366-370)
            {
                // P184 — the second, genuinely client-sendable entry point for
                // withdrawing a pending request (not just a server broadcast).
                // Port of FedSrv.CPP:7484-7516: if shipID names the sender's own
                // ship, the reason MUST be DPR_Canceled (self-cancel); otherwise
                // the sender MUST be the named side's leader and the reason MUST
                // be DPR_Rejected (leader-initiated reject). On either valid case,
                // relay the EXACT received message whole-mission (native's plain
                // ForwardMessage) and remove the matching pending request WITHOUT an
                // additional broadcast (the relay already told everyone).
                try
                {
                    var del = command.Read<DelPositionReq>();
                    if (!_peerShips.TryGetValue(from, out var delLive))
                    {
                        ConsoleLog.Write($"[delreq] {from}: DELPOSITIONREQ from a peer with no ship — ignored");
                    }
                    else if (delLive.Ship.Mission is not Mission mission)
                    {
                        ConsoleLog.Write($"[delreq] {from} ship {delLive.Ship.Id}: no mission — DELPOSITIONREQ ignored");
                    }
                    else
                    {
                        var targetSide = mission.Sides.FirstOrDefault(s => s.Id == del.SideId);
                        if (targetSide is null)
                        {
                            ConsoleLog.Write($"[delreq] ship {del.ShipId} side {del.SideId}: side does not exist — ignored");
                        }
                        else
                        {
                            bool selfCancel = del.ShipId == delLive.Ship.Id;
                            bool valid = selfCancel
                                ? del.Reason == DelPositionReqReason.Canceled          // own ship → must cancel
                                : (del.Reason == DelPositionReqReason.Rejected          // someone else → leader reject
                                   && targetSide.LeaderShipId == delLive.Ship.Id);
                            if (!valid)
                            {
                                ConsoleLog.Write($"[delreq] ship {del.ShipId} side {del.SideId} reason {del.Reason} from {delLive.Ship.Id}: invalid combo — ignored");
                            }
                            else
                            {
                                // P654 (H-05) — re-encode the fixed-size relay from
                                // the decoded fields (bounds fan-out to exactly the
                                // fixed size), budget-charged (H-24), then drop the
                                // matching request without a second broadcast.
                                if (!TryChargeChatBudget(from))
                                {
                                    ConsoleLog.Write($"[delreq] {from}: budget exhausted — DELPOSITIONREQ dropped (H-24)");
                                }
                                else
                                {
                                    SendToMissionPeers(mission, FedPacket.Pack(new[] { del.Encode() }));
                                    var targetConn = selfCancel ? (ConnectionId?)from : FindConnectionOfShip(mission, del.ShipId);
                                    if (targetConn is not null)
                                        RemovePendingJoinRequest(targetConn.Value, del.SideId);
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad DELPOSITIONREQ from {from}: {e.Message}");
                }
            }
            else if (fmid == ForceTeamReady.Fmid)   // FM_CS_FORCE_TEAM_READY (Messages.h:392-397)
            {
                // P184 — the side's team leader forces the whole side ready (or
                // un-forces it). Port of FedSrv.CPP:6987-7015 (shared block with
                // PLAYER_READY, the fIsForce branch): refuse unless the sender is
                // the named side's current leader (and names their own side). On
                // success: relay the exact message whole-mission, set Side.ForceReady,
                // and re-run the readiness computation — which, per the amended
                // RecomputeSideReady, now reports ready unconditionally (subject to
                // the >=1 connected-player floor), broadcasting TEAM_READY and
                // triggering auto-start if the mission is now fully ready.
                try
                {
                    var ft = command.Read<ForceTeamReady>();
                    if (!_peerShips.TryGetValue(from, out var frLive))
                    {
                        ConsoleLog.Write($"[forceready] {from}: FORCE_TEAM_READY from a peer with no ship — ignored");
                    }
                    else if (frLive.Ship.Mission is not Mission mission)
                    {
                        ConsoleLog.Write($"[forceready] {from} ship {frLive.Ship.Id}: no mission — FORCE_TEAM_READY ignored");
                    }
                    else if (frLive.Ship.SideId is not { } frSideId || ft.SideId != frSideId)
                    {
                        ConsoleLog.Write($"[forceready] {from} ship {frLive.Ship.Id}: names side {ft.SideId}, own side {frLive.Ship.SideId} — ignored (must name own side)");
                    }
                    else
                    {
                        var frSide = mission.Sides.FirstOrDefault(s => s.Id == frSideId);
                        if (frSide is null || frSide.LeaderShipId != frLive.Ship.Id)
                        {
                            ConsoleLog.Write($"[forceready] {from} ship {frLive.Ship.Id}: not side {frSideId}'s current leader — FORCE_TEAM_READY refused");
                        }
                        else
                        {
                            // P654 (H-05/H-24) — re-encode the fixed-size relay and
                            // budget-charge it (native ForwardMessage then SetForceReady).
                            if (!TryChargeChatBudget(from))
                            {
                                ConsoleLog.Write($"[forceready] {from}: budget exhausted — FORCE_TEAM_READY dropped (H-24)");
                            }
                            else
                            {
                                SendToMissionPeers(mission, FedPacket.Pack(new[] { ft.Encode() }));
                                frSide.ForceReady = ft.FForceReady;
                                RecomputeSideReady(frLive.Ship);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad FORCE_TEAM_READY from {from}: {e.Message}");
                }
            }
            else if (fmid == StartGame.Fmid)   // FM_C_START_GAME (Messages.h:868-869)
            {
                // P188 — the mission owner manually triggers the start
                // countdown, a genuine alternative to the ready-up auto-start.
                // Port of FedSrv.CPP:7597-7621: refuse unless the sender is the
                // mission owner (P183's owner concept — the owning side's
                // leader, the same IsMissionOwnerPlayer gate P186's LOCK_LOBBY
                // uses), the mission is STAGE_NOTSTARTED, and every occupied
                // side is ready. Native's bot-authentication special-case (bots
                // may start regardless of ownership / with zero countdown) is
                // OUT OF SCOPE — no such concept exists in this port. Native's
                // full FAllReady (team-balance/rank logic needing
                // nMaxPlayersPerTeam/a rank system) is likewise replaced by
                // this port's own already-simplified readiness check — the
                // same IsAllOccupiedSidesReady P180's auto-start uses (a real,
                // deliberate simplification, stated in the P188 writeup). On
                // success: call StartCountdown with the real native constant
                // c_fMissionBriefingCountdown = 15s (igc.h:1005).
                try
                {
                    _ = command.Read<StartGame>();   // zero-field marker; decode validates cbmsg=4
                    if (!_peerShips.TryGetValue(from, out var sgLive))
                    {
                        ConsoleLog.Write($"[startgame] {from}: START_GAME from a peer with no ship — ignored");
                    }
                    else if (sgLive.Ship.Mission is not Mission mission)
                    {
                        ConsoleLog.Write($"[startgame] {from} ship {sgLive.Ship.Id}: no mission — START_GAME ignored");
                    }
                    else if (!IsMissionOwnerPlayer(sgLive.Ship, mission))
                    {
                        ConsoleLog.Write($"[startgame] {from} ship {sgLive.Ship.Id}: not the mission owner — START_GAME refused");
                    }
                    else if (mission.Stage != MissionStage.NotStarted)
                    {
                        ConsoleLog.Write($"[startgame] {from} ship {sgLive.Ship.Id}: mission stage {mission.Stage} != STAGE_NOTSTARTED — START_GAME refused");
                    }
                    else if (!IsAllOccupiedSidesReady(mission))
                    {
                        ConsoleLog.Write($"[startgame] {from} ship {sgLive.Ship.Id}: not all occupied sides ready — START_GAME refused");
                    }
                    else
                    {
                        StartCountdown(mission, BriefingCountdownSeconds);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad START_GAME from {from}: {e.Message}");
                }
            }
            else if (fmid == RandomizeTeams.Fmid)   // FM_C_RANDOMIZE_TEAMS (Messages.h:201)
            {
                // P195 — the mission owner's "balance the teams" trigger (not
                // actually random). Port of FedSrv.CPP:6964-6997: refuse unless
                // the sender is the mission owner (the same IsMissionOwnerPlayer
                // gate P186's LOCK_LOBBY/START_GAME use) and the mission is
                // STAGE_NOTSTARTED, then balance the sides. The real dispatch
                // branches on iMaxImbalance==0x7ffe (balance) vs flush — the
                // flush branch (dump everyone to a neutral lobby, no
                // reassignment) is OUT OF SCOPE: this port has no SIDE_TEAMLOBBY
                // and no MissionParams field, and "balance" is what the message
                // name promises, so Stage-1 always balances (a deliberate,
                // permanent simplification — see docs/RANDOMIZE_TEAMS_P195.md).
                // LockSides is deliberately BYPASSED (not a refusal): native's
                // RandomizeSides does not consult bLockSides (SetLockSides is
                // even commented out there, fsmission.cpp:5258-5259, "Randomize
                // should not touch sides!"), and this is a privileged
                // owner-only, NotStarted-only mass reassignment, not the
                // ordinary player-initiated side-switch the lock guards.
                try
                {
                    _ = command.Read<RandomizeTeams>();   // zero-field marker; decode validates cbmsg=4
                    if (!_peerShips.TryGetValue(from, out var rtLive))
                    {
                        ConsoleLog.Write($"[randomizeteams] {from}: RANDOMIZE_TEAMS from a peer with no ship — ignored");
                    }
                    else if (rtLive.Ship.Mission is not Mission mission)
                    {
                        ConsoleLog.Write($"[randomizeteams] {from} ship {rtLive.Ship.Id}: no mission — RANDOMIZE_TEAMS ignored");
                    }
                    else if (!IsMissionOwnerPlayer(rtLive.Ship, mission))
                    {
                        ConsoleLog.Write($"[randomizeteams] {from} ship {rtLive.Ship.Id}: not the mission owner — RANDOMIZE_TEAMS refused");
                    }
                    else if (mission.Stage != MissionStage.NotStarted)
                    {
                        ConsoleLog.Write($"[randomizeteams] {from} ship {rtLive.Ship.Id}: mission stage {mission.Stage} != STAGE_NOTSTARTED — RANDOMIZE_TEAMS refused");
                    }
                    else
                    {
                        RebalanceSides(mission);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad RANDOMIZE_TEAMS from {from}: {e.Message}");
                }
            }
            else if (fmid == AutoDonate.Fmid)   // FM_C_AUTODONATE (Messages.h:738-741)
            {
                // P197 — the per-player auto-donate request: a two-mode dispatch
                // (set-own-target-with-optional-gift, or the leader-only
                // amount==-1 toggle of a subordinate's donation) plus the
                // donation-confidence leadership check. Port of
                // FedSrv.CPP:5516-5658 case FM_C_AUTODONATE.
                try
                {
                    var autoDonate = command.Read<AutoDonate>();
                    if (!_peerShips.TryGetValue(from, out var adLive))
                    {
                        ConsoleLog.Write($"[autodonate] {from}: AUTODONATE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleAutoDonate(from, adLive, autoDonate);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad AUTODONATE from {from}: {e.Message}");
                }
            }
            else if (fmid == RequestMoney.Fmid)   // FM_CS_REQUEST_MONEY (Messages.h:934-938)
            {
                // P199 — a player who wants to buy something but lacks the funds
                // asks whoever actually holds the team's money to send it. Port of
                // FedSrv.CPP:5361-5406 case FM_CS_REQUEST_MONEY. This handler
                // NEVER transfers money — it only FORWARDS the request (with
                // shipidRequest filled in) to exactly one recipient: the sender's
                // auto-donate target if that player's wallet holds >= amount,
                // otherwise the richest OTHER teammate; silently dropped if no
                // recipient resolves.
                try
                {
                    var requestMoney = command.Read<RequestMoney>();
                    if (!_peerShips.TryGetValue(from, out var rmLive))
                    {
                        ConsoleLog.Write($"[requestmoney] {from}: REQUEST_MONEY from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleRequestMoney(from, rmLive, requestMoney);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad REQUEST_MONEY from {from}: {e.Message}");
                }
            }
            else if (fmid == PlayerDonate.Fmid)   // FM_C_PLAYER_DONATE (Messages.h:527-530)
            {
                // P203 — a manual, direct player-to-player gift. Port of
                // FedSrv.CPP:7213-7262 case FM_C_PLAYER_DONATE: gated on
                // STAGE_STARTED; a same-side or allied-side (P190 AllyGroup)
                // recipient with sufficient sender-wallet funds is debited +
                // credited with MONEY_CHANGE(60) to the sender's side group
                // always (and the recipient's side group when the gift crossed
                // sides); any refusal bounces back a no-op MONEY_CHANGE to the
                // sender alone with no wallet movement.
                try
                {
                    var playerDonate = command.Read<PlayerDonate>();
                    if (!_peerShips.TryGetValue(from, out var pdLive))
                    {
                        ConsoleLog.Write($"[playerdonate] {from}: PLAYER_DONATE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandlePlayerDonate(from, pdLive, playerDonate);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad PLAYER_DONATE from {from}: {e.Message}");
                }
            }
            else if (fmid == LockLobby.Fmid)   // FM_CS_LOCK_LOBBY (Messages.h:372-374)
            {
                // P186 — the mission owner locks/unlocks the lobby. Port of
                // FedSrv.CPP:6938-6951: refuse unless the sender is the mission
                // owner (the owning side's leader, per the P183 owner concept
                // `Mission.MissionOwnerSideId`). On success relay the exact
                // message whole-mission and set Mission.LockLobby. Honesty note:
                // the flag has no real gating effect in this port — native's
                // consequence (refusing brand-new lobby joiners, FedSrv.CPP:733,
                // DPR_LobbyLocked) belongs to the legacy lobby/rejoin machinery
                // (OldPlayerLink/bAllowJoiners/nTeams) this port has ruled N/A:
                // players are seated directly via config, not a "first join"
                // flow, so there is no hook point to attach the lock to. Built
                // for wire completeness and any future client UI only.
                try
                {
                    var ll = command.Read<LockLobby>();
                    if (!_peerShips.TryGetValue(from, out var llLive))
                    {
                        ConsoleLog.Write($"[locklobby] {from}: LOCK_LOBBY from a peer with no ship — ignored");
                    }
                    else if (llLive.Ship.Mission is not Mission mission)
                    {
                        ConsoleLog.Write($"[locklobby] {from} ship {llLive.Ship.Id}: no mission — LOCK_LOBBY ignored");
                    }
                    else if (!IsMissionOwnerPlayer(llLive.Ship, mission))
                    {
                        ConsoleLog.Write($"[locklobby] {from} ship {llLive.Ship.Id}: not the mission owner — LOCK_LOBBY refused");
                    }
                    else
                    {
                        // P654 (H-05/H-24) — re-encode the fixed-size relay and
                        // budget-charge it (native ForwardMessage then SetLockLobby).
                        if (!TryChargeChatBudget(from))
                        {
                            ConsoleLog.Write($"[locklobby] {from}: budget exhausted — LOCK_LOBBY dropped (H-24)");
                        }
                        else
                        {
                            SendToMissionPeers(mission, FedPacket.Pack(new[] { ll.Encode() }));
                            mission.LockLobby = ll.FLock;
                            Console.WriteLine($"[locklobby] ship {llLive.Ship.Id}: LOCK_LOBBY fLock={ll.FLock} whole-mission, Mission.LockLobby={mission.LockLobby}");
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad LOCK_LOBBY from {from}: {e.Message}");
                }
            }
            else if (fmid == LockSides.Fmid)   // FM_CS_LOCK_SIDES (Messages.h:376-378)
            {
                // P186 — the mission owner locks/unlocks team changes. Port of
                // FedSrv.CPP:6953-6962: refuse unless the sender is the mission
                // owner. On success relay the exact message whole-mission and set
                // Mission.LockSides. Unlike LOCK_LOBBY this one HAS a real,
                // reachable hook: the POSITIONREQ side-switch handler refuses a
                // request with DELPOSITIONREQ(DPR_SidesLocked) while LockSides is
                // on, mirroring native CheckPositionRequest's bLockSides gate
                // (fsmission.cpp:4590).
                try
                {
                    var ls = command.Read<LockSides>();
                    if (!_peerShips.TryGetValue(from, out var lsLive))
                    {
                        ConsoleLog.Write($"[locksides] {from}: LOCK_SIDES from a peer with no ship — ignored");
                    }
                    else if (lsLive.Ship.Mission is not Mission mission)
                    {
                        ConsoleLog.Write($"[locksides] {from} ship {lsLive.Ship.Id}: no mission — LOCK_SIDES ignored");
                    }
                    else if (!IsMissionOwnerPlayer(lsLive.Ship, mission))
                    {
                        ConsoleLog.Write($"[locksides] {from} ship {lsLive.Ship.Id}: not the mission owner — LOCK_SIDES refused");
                    }
                    else
                    {
                        // P654 (H-05/H-24) — re-encode the fixed-size relay and
                        // budget-charge it (native ForwardMessage then SetLockSides).
                        if (!TryChargeChatBudget(from))
                        {
                            ConsoleLog.Write($"[locksides] {from}: budget exhausted — LOCK_SIDES dropped (H-24)");
                        }
                        else
                        {
                            SendToMissionPeers(mission, FedPacket.Pack(new[] { ls.Encode() }));
                            mission.LockSides = ls.FLock;
                            Console.WriteLine($"[locksides] ship {lsLive.Ship.Id}: LOCK_SIDES fLock={ls.FLock} whole-mission, Mission.LockSides={mission.LockSides}");
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad LOCK_SIDES from {from}: {e.Message}");
                }
            }
            else if (fmid == SideInactive.Fmid)   // FM_CS_SIDE_INACTIVE (Messages.h:532-536)
            {
                // P202 — pre-game roster shrinking: the mission owner marks an
                // *empty* side inactive/active before the mission starts, to play a
                // smaller-than-map-default team count. Port of FedSrv.CPP:7379-7425
                // (the FM_CS_SIDE_INACTIVE dispatch), read in full this session.
                // SCOPE BOUNDARY: this is ONLY use #1 of native's SIDE_INACTIVE
                // machinery — the in-game forced-elimination flow (DeactivateSide,
                // fsmission.cpp:5340-5380) is out of scope (superseded by P187's
                // resign substitution), and P178's CheckForVictoryByInactiveSides
                // keeps detecting side-emptiness by connected-peer presence and does
                // NOT consume the new Side.Active field.
                try
                {
                    var si = command.Read<SideInactive>();
                    if (!_peerShips.TryGetValue(from, out var siLive))
                    {
                        ConsoleLog.Write($"[sideinactive] {from}: SIDE_INACTIVE from a peer with no ship — ignored");
                    }
                    else if (siLive.Ship.Mission is not Mission mission)
                    {
                        ConsoleLog.Write($"[sideinactive] {from} ship {siLive.Ship.Id}: no mission — SIDE_INACTIVE ignored");
                    }
                    else if (!IsMissionOwnerPlayer(siLive.Ship, mission))
                    {
                        ConsoleLog.Write($"[sideinactive] {from} ship {siLive.Ship.Id}: not the mission owner — SIDE_INACTIVE refused");
                    }
                    else if (mission.Stage != MissionStage.NotStarted)
                    {
                        ConsoleLog.Write($"[sideinactive] ship {siLive.Ship.Id}: mission stage {mission.Stage} != NotStarted — SIDE_INACTIVE refused");
                    }
                    else
                    {
                        var side = mission.Sides.FirstOrDefault(s => s.Id == si.SideId);
                        if (side is null)
                        {
                            ConsoleLog.Write($"[sideinactive] ship {siLive.Ship.Id}: side {si.SideId} not in this mission — SIDE_INACTIVE ignored");
                        }
                        else
                        {
                            // The native bObjectModelCreated gate (FedSrv.CPP:7381,
                            // "!pfsMission->GetMissionDef()->misparms.bObjectModelCreated")
                            // is N/A here and deliberately skipped: native builds the
                            // sim object model on a later lobby-driven "generate"
                            // trigger, so there is a live window where that flag is
                            // false; this port's Mission.Assemble builds the object
                            // model once at boot, so the check has no reachable
                            // false-case in this architecture. Stated, not fabricated
                            // (no bObjectModelCreated-equivalent flag is invented).

                            // Only change the active flag if the side is currently
                            // empty; otherwise silently revert the requested value to
                            // the side's real current state (native's defensive
                            // re-check, FedSrv.CPP:7387-7390 — not a refusal).
                            bool hasPlayers = _peerShips.Any(kv =>
                                ReferenceEquals(kv.Value.Ship.Mission, mission) &&
                                kv.Value.Ship.SideId == si.SideId);
                            if (!hasPlayers)
                                side.Active = si.BActive;
                            else
                                si.BActive = side.Active;

                            // Keep the mission-wide AllowEmptyTeams (AET) flag in
                            // sync: AET is ON iff at least one side is inactive
                            // (FedSrv.CPP:7395-7414).
                            bool aet = mission.AllowEmptyTeams;
                            bool allActive = true;
                            foreach (var s in mission.Sides)
                                if (!s.Active) allActive = false;

                            bool mustChangeAet = (allActive && aet) || (!allActive && !aet);
                            if (mustChangeAet)
                            {
                                mission.AllowEmptyTeams = !aet;
                                si.BChangeAet = true;
                                si.BAet = mission.AllowEmptyTeams;
                            }
                            else
                                si.BChangeAet = false;

                            // Forward the (possibly-corrected) message whole-mission
                            // (native ForwardMessage(pfsMission->GetGroupMission(), ...,
                            // FM_GUARANTEED), FedSrv.CPP:7416).
                            int n = SendToMissionPeers(mission, FedPacket.Pack(new[] { si.Encode() }));
                            Console.WriteLine($"[sideinactive] ship {siLive.Ship.Id}: SIDE_INACTIVE side {si.SideId} bActive={si.BActive} bChangeAET={si.BChangeAet} bAET={si.BAet} -> {n} peer(s), Mission.AllowEmptyTeams={mission.AllowEmptyTeams}");

                            // #ALLY — clear this side's alliance membership too
                            // (UpdateAlliances(sid, NA), FedSrv.CPP:7424-7425).
                            UpdateAlliances(mission, si.SideId, -1);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad SIDE_INACTIVE from {from}: {e.Message}");
                }
            }
            else if (fmid == AutoAccept.Fmid)   // FM_CS_AUTO_ACCEPT (Messages.h:413-416)
            {
                // P186 — the side's leader toggles auto-accept. Port of
                // FedSrv.CPP:6908-6936. Gate defensively: recompute the sender's
                // OWN side server-side (don't trust the message's iSide for the
                // gate), require the sender be that side's current leader, and
                // require the named iSide to BE that own side (native's
                // TrapHackBoot(iSide == sid)). The iSide == NA mission-owner-level
                // auto-accept-of-leaders variant is out of scope — log and ignore
                // (no such mission-wide concept exists in this port). On success:
                // relay whole-mission, set Side.AutoAccept, and when turning ON,
                // sweep P184's pending-request map for that side and seat each
                // requester via TryJoinSide (fsmission.cpp:4974-5011 SetAutoAccept).
                try
                {
                    var aa = command.Read<AutoAccept>();
                    if (!_peerShips.TryGetValue(from, out var aaLive))
                    {
                        ConsoleLog.Write($"[autoaccept] {from}: AUTO_ACCEPT from a peer with no ship — ignored");
                    }
                    else if (aaLive.Ship.Mission is not Mission mission)
                    {
                        ConsoleLog.Write($"[autoaccept] {from} ship {aaLive.Ship.Id}: no mission — AUTO_ACCEPT ignored");
                    }
                    else if (aaLive.Ship.SideId is not { } aaSideId)
                    {
                        ConsoleLog.Write($"[autoaccept] {from} ship {aaLive.Ship.Id}: no side (NA) — AUTO_ACCEPT ignored (no mission-owner auto-accept-of-leaders in this port)");
                    }
                    else if (aa.SideId != aaSideId)
                    {
                        ConsoleLog.Write($"[autoaccept] {from} ship {aaLive.Ship.Id}: names side {aa.SideId}, own side {aaSideId} — ignored (must name own side)");
                    }
                    else
                    {
                        var aaSide = mission.Sides.FirstOrDefault(s => s.Id == aaSideId);
                        if (aaSide is null || aaSide.LeaderShipId != aaLive.Ship.Id)
                        {
                            ConsoleLog.Write($"[autoaccept] {from} ship {aaLive.Ship.Id}: not side {aaSideId}'s current leader — AUTO_ACCEPT refused");
                        }
                        else
                        {
                            // P654 (H-05/H-24) — re-encode the fixed-size relay and
                            // budget-charge it (native ForwardMessage then SetAutoAccept).
                            if (!TryChargeChatBudget(from))
                            {
                                ConsoleLog.Write($"[autoaccept] {from}: budget exhausted — AUTO_ACCEPT dropped (H-24)");
                            }
                            else
                            {
                                SendToMissionPeers(mission, FedPacket.Pack(new[] { aa.Encode() }));
                                aaSide.AutoAccept = aa.FAutoAccept;
                                Console.WriteLine($"[autoaccept] ship {aaLive.Ship.Id}: AUTO_ACCEPT side {aaSideId} fAutoAccept={aa.FAutoAccept} whole-mission, Side.AutoAccept={aaSide.AutoAccept}");
                                if (aa.FAutoAccept)
                                {
                                    SweepAutoAcceptPending(mission, aaSideId);
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad AUTO_ACCEPT from {from}: {e.Message}");
                }
            }
            else if (fmid == ChangeTeamCiv.Fmid)   // FM_CS_CHANGE_TEAM_CIV (Messages.h:407-411)
            {
                // P186 — the side's leader changes their side's civilization.
                // Port of FedSrv.CPP:7344-7367 (native changes the leader's OWN
                // side, pside = pfsPlayer->GetSide(), so the named iSide must be
                // the sender's own side): refuse unless the sender is that side's
                // current leader AND the mission is STAGE_NOTSTARTED. On success:
                // relay whole-mission, resolve the target civilization from
                // Mission.Core (refuse — log and ignore — if civID is absent, the
                // native TrapHackBoot(pciv)), and set the side's civilization. The
                // random (random-faction) field is out of scope — ignored, always
                // a direct civ change. The per-player score-reset loop of native
                // SetSideCiv (fsmission.cpp:5274-5283) is out of scope (no
                // player-score-persistence system, the same gap that excluded
                // GAME_OVER_PLAYERS from [[allegiance-win-condition-plan]]).
                try
                {
                    var cc = command.Read<ChangeTeamCiv>();
                    if (!_peerShips.TryGetValue(from, out var ccLive))
                    {
                        ConsoleLog.Write($"[changeciv] {from}: CHANGE_TEAM_CIV from a peer with no ship — ignored");
                    }
                    else if (ccLive.Ship.Mission is not Mission mission)
                    {
                        ConsoleLog.Write($"[changeciv] {from} ship {ccLive.Ship.Id}: no mission — CHANGE_TEAM_CIV ignored");
                    }
                    else if (ccLive.Ship.SideId is not { } ccSideId || cc.SideId != ccSideId)
                    {
                        ConsoleLog.Write($"[changeciv] {from} ship {ccLive.Ship.Id}: names side {cc.SideId}, own side {ccLive.Ship.SideId} — ignored (must change own side's civ)");
                    }
                    else
                    {
                        var ccSide = mission.Sides.FirstOrDefault(s => s.Id == ccSideId);
                        if (ccSide is null || ccSide.LeaderShipId != ccLive.Ship.Id)
                        {
                            ConsoleLog.Write($"[changeciv] {from} ship {ccLive.Ship.Id}: not side {ccSideId}'s current leader — CHANGE_TEAM_CIV refused");
                        }
                        else if (mission.Stage != MissionStage.NotStarted)
                        {
                            ConsoleLog.Write($"[changeciv] {from} ship {ccLive.Ship.Id}: mission stage {mission.Stage} != STAGE_NOTSTARTED — CHANGE_TEAM_CIV refused");
                        }
                        else if (!mission.Core.Civilizations.TryGetValue(cc.CivId, out var targetCiv))
                        {
                            ConsoleLog.Write($"[changeciv] {from} ship {ccLive.Ship.Id}: civilization {cc.CivId} not in core — CHANGE_TEAM_CIV ignored");
                        }
                        else
                        {
                            // P654 (H-05/H-24) — re-encode the fixed-size relay and
                            // budget-charge it (native ForwardMessage then SetSideCiv).
                            if (!TryChargeChatBudget(from))
                            {
                                ConsoleLog.Write($"[changeciv] {from}: budget exhausted — CHANGE_TEAM_CIV dropped (H-24)");
                            }
                            else
                            {
                                SendToMissionPeers(mission, FedPacket.Pack(new[] { cc.Encode() }));
                                short oldCivId = ccSide.Civilization.CivilizationId;
                                ccSide.Civilization = targetCiv;
                                Console.WriteLine($"[changeciv] ship {ccLive.Ship.Id}: side {ccSideId} civ {oldCivId} -> {targetCiv.CivilizationId} whole-mission");
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad CHANGE_TEAM_CIV from {from}: {e.Message}");
                }
            }
            else if (fmid == ChangeAlliance.Fmid)   // FM_C_CHANGE_ALLIANCE (Messages.h:1017-1020)
            {
                // P190 — the mission owner changes an alliance while the mission
                // is still STAGE_NOTSTARTED. Port of FedSrv.CPP:7469-7481.
                // DIVERGENCE FROM NATIVE (deliberate, stated in the P190 report):
                // native performs NO owner check at all — it only gates on
                // Stage == NOTSTARTED and trusts the client UI to expose the
                // control only to the owner (the message's own comment says
                // "sent by game owner"). This port's established convention
                // (P184's DELPOSITIONREQ precedent, P186's IsMissionOwnerPlayer
                // gate) is to correct known native gaps rather than replicate
                // them, so CHANGE_ALLIANCE is additionally gated on
                // IsMissionOwnerPlayer here. Also refuse out-of-range sideID /
                // sideAlly (native silently drops them inside UpdateAlliances,
                // fsmission.cpp:5642-5647; here we log-and-ignore up front).
                try
                {
                    var ca = command.Read<ChangeAlliance>();
                    if (!_peerShips.TryGetValue(from, out var caLive))
                    {
                        ConsoleLog.Write($"[alliances] {from}: CHANGE_ALLIANCE from a peer with no ship — ignored");
                    }
                    else if (caLive.Ship.Mission is not Mission mission)
                    {
                        ConsoleLog.Write($"[alliances] {from} ship {caLive.Ship.Id}: no mission — CHANGE_ALLIANCE ignored");
                    }
                    else if (!IsMissionOwnerPlayer(caLive.Ship, mission))
                    {
                        ConsoleLog.Write($"[alliances] {from} ship {caLive.Ship.Id}: not the mission owner — CHANGE_ALLIANCE refused (divergence: native has no owner gate)");
                    }
                    else if (mission.Stage != MissionStage.NotStarted)
                    {
                        ConsoleLog.Write($"[alliances] {from} ship {caLive.Ship.Id}: mission stage {mission.Stage} != STAGE_NOTSTARTED — CHANGE_ALLIANCE refused");
                    }
                    else if (!IsValidAllianceId(ca.SideId, mission.Sides.Count) || !IsValidAllianceId(ca.SideAlly, mission.Sides.Count))
                    {
                        ConsoleLog.Write($"[alliances] {from} ship {caLive.Ship.Id}: sideID={ca.SideId} or sideAlly={ca.SideAlly} out of range (must be NA or [0,{mission.Sides.Count})) — CHANGE_ALLIANCE ignored (native fsmission.cpp:5642-5647 silently returns)");
                    }
                    else
                    {
                        UpdateAlliances(mission, ca.SideId, ca.SideAlly);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad CHANGE_ALLIANCE from {from}: {e.Message}");
                }
            }
            else if (fmid == PlayerReady.Fmid)   // FM_CS_PLAYER_READY (Messages.h:380-384)
            {
                // P84: "Sent to everyone on team" (the native message's own
                // header comment) -- the client's own handler
                // (appmsg.cpp:2297-2312) only updates PlayerInfo.fReady and
                // fires the roster-changed sink when this message is
                // RECEIVED; a click on the Team Lobby's own JOIN control
                // never applies locally, it waits for this relay. Before this,
                // the server logged "unsupported fmid 45" and the click
                // appeared completely dead, live-confirmed on the real
                // client (P84). Relay the original bytes to every peer on the
                // sender's side, including the sender itself (its own client
                // is waiting on the same relay to update its own roster
                // entry) -- same shape as ChatTarget.Team in SelectChatTargets.
                try
                {
                    var ready = command.Read<PlayerReady>();
                    if (!_peerShips.TryGetValue(from, out var readyLive))
                    {
                        ConsoleLog.Write($"[ready] {from}: PLAYER_READY from a peer with no ship — ignored");
                    }
                    else
                    {
                        // P654 (H-24) — budget-charge the relay (bounded fan-out).
                        if (!TryChargeChatBudget(from))
                        {
                            ConsoleLog.Write($"[ready] {from}: budget exhausted — PLAYER_READY dropped (H-24)");
                        }
                        else
                        {
                            var targets = _peerShips
                                .Where(kv => kv.Value.Ship.SideId == readyLive.Ship.SideId)
                                .Select(kv => kv.Key)
                                .ToArray();
                            // P654 (H-19) — re-encode the relay with the sender's
                            // OWN ship id, never the client's forged ShipId (native
                            // TrapHackBoot(shipID == GetShipID()), FedSrv.CPP:7019) —
                            // so a peer can only mark ITSELF ready, and the relay is
                            // exact-size (H-05).
                            ready.ShipId = readyLive.Ship.Id;
                            byte[] packet = FedPacket.Pack(new[] { ready.Encode() });
                            foreach (var t in targets) SendPeer(t, packet);
                            Console.WriteLine($"[ready] {from} ship {ready.ShipId} side {sideStr(readyLive.Ship.SideId)} fReady={ready.FReady} -> {targets.Length} peer(s)");

                            // P180 — the real trigger point. This port's analogue of
                            // native's `PlayerReadyChange` (fsmission.cpp:5485-5489):
                            // a player's own ready flag just changed, so remember it
                            // and recompute the side's aggregate readiness, which may
                            // fire a TEAM_READY broadcast and, in turn, the ready-up
                            // auto-start. Runs AFTER the existing relay (the real
                            // client still needs the raw relay for its own roster
                            // UI — the message is sent to everyone on the team).
                            _playerReady[from] = ready.FReady;
                            RecomputeSideReady(readyLive.Ship);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad PLAYER_READY from {from}: {e.Message}");
                }
            }
            else if (fmid == Suicide.Fmid)   // FM_C_SUICIDE (Messages.h:590-591)
            {
                // The client's suicide request (P103). Decode is trivially empty
                // (header-only message); the real work is the gated death in
                // HandleSuicide. A malformed frame is reported and dropped like
                // every other inbound handler — never crashes the server.
                try
                {
                    _ = command.Read<Suicide>();
                    if (!_peerShips.TryGetValue(from, out var live))
                    {
                        ConsoleLog.Write($"[suicide] {from}: SUICIDE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleSuicide(from, live);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad SUICIDE from {from}: {e.Message}");
                }
            }
            else if (fmid == Logoff.Fmid)   // FM_CS_LOGOFF (Messages.h)
            {
                // P127 — the client's graceful logoff request. LOGOFF is a
                // zero-field CS message: the server echoes the exact same
                // (empty) message shape back to the same connection
                // (native FedSrv.CPP:5787-5802 mirrors it with
                // ForwardMessage(..., FM_GUARANTEED)), then releases the peer
                // through the EXACT same cleanup path an abrupt TCP drop uses
                // (PeerLeftEvt → ReleasePeer) — reusing it, not building a
                // second mechanism. Native's `delete pfsPlayer` deletes the
                // player object immediately; ReleasePeer is the port's
                // immediate effect: dropping the peer from _peerShips/_peerNames
                // AND (P128) retiring its ship (SHIP_DELETE SDR_LOGGEDOFF +
                // removed from the sector), so no zombie keeps flying. If the
                // client then closes its socket the transport's OnPeerLeft
                // fires and ReleasePeer runs again — an idempotent no-op.
                // No ship gate here: unlike SUICIDE, LOGOFF acts on the
                // connection itself, not the ship, so it holds even for a peer
                // that never validated/logged on.
                try
                {
                    _ = command.Read<Logoff>();
                    SendPeer(from, FedPacket.Pack(new[] { m }));   // guaranteed echo back
                    ReleasePeer(from);
                    // P654 (H-14) — retire the transport connection now. Native
                    // LOGOFF deletes the player and the connection is NOT reused
                    // (FedSrv.CPP:5787-5802); leaving the socket open re-armed
                    // LOGONREQ, letting one connection churn unlimited ship ids
                    // (the P646 one-logon bypass). Closing the socket makes any
                    // later frame on it ineligible; the transport's OnPeerLeft
                    // fires and ReleasePeer runs again as an idempotent no-op.
                    RemoveChatBudget(from);
                    (_host.Transport as TcpTransport)?.ClosePeerAfterFlush(from);
                    Console.WriteLine($"[logoff] peer {from} logged off (echo queued, released, close after drain — H-14)");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad LOGOFF from {from}: {e.Message}");
                }
            }
            else if (fmid == ViewClusterRequest.Fmid)   // FM_C_VIEW_CLUSTER (Messages.h)
            {
                // The docked-spectator/map-view request (P106): a docked player
                // previews another cluster. Gate and reply mirror the native
                // handler (FedSrv.CPP:6420-6454) — only reply when docked
                // (GetStation()!=NULL), only to the requesting peer. The real
                // work (cluster/target resolution, visibility gate, reply) is
                // in HandleViewCluster. A malformed frame is reported and
                // dropped like every other inbound handler.
                try
                {
                    var req = command.Read<ViewClusterRequest>();
                    if (!_peerShips.TryGetValue(from, out var reqLive))
                    {
                        ConsoleLog.Write($"[viewcluster] {from}: VIEW_CLUSTER from a peer with no ship — ignored");
                    }
                    else if (reqLive.Ship.StationId is null)
                    {
                        // P612 — a FLYING peer sending VIEW_CLUSTER. Native ignores a
                        // flying ship (gate GetStation()!=NULL, FedSrv.CPP:6421), and so
                        // does this port — EXCEPT for an ADMIN spectator, whose free
                        // camera uses this message to name the sector it is now viewing,
                        // which the ship broadcast then streams (all ships, ignoring side
                        // visibility). Non-admins keep native behaviour: ignored, no leak.
                        if (IsSpectatorAdmin(from))
                        {
                            RecordSpectateSector(from, req.ClusterId);
                            ConsoleLog.Write($"[viewcluster] {from} ship {reqLive.Ship.Id} ADMIN spectator -> viewing sector {req.ClusterId}");
                        }
                        else
                        {
                            ConsoleLog.Write($"[viewcluster] {from} ship {reqLive.Ship.Id}: not docked — ignored (gate: GetStation()!=NULL, FedSrv.CPP:6421)");
                        }
                    }
                    else
                    {
                        HandleViewCluster(from, reqLive, req);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad VIEW_CLUSTER from {from}: {e.Message}");
                }
            }
            else if (fmid == BoardShip.Fmid)   // FM_C_BOARD_SHIP (Messages.h:686-688)
            {
                // The client's board/leave request (P108): dual-purpose per
                // native (FedSrv.CPP:6385-6418) — sidParent == NA leaves the
                // current parent, any other value boards that ship. Only a
                // FAILURE sends a BOARD_NACK to the requester alone; success
                // rides the existing SHIP_STATUS periodic refresh (via
                // MakeOwnShipStatus). The real work (turret-slot search,
                // observer gate, money refund, parent/child/turret state
                // transition) is in HandleBoardShip. A malformed frame is
                // reported and dropped like every other inbound handler.
                try
                {
                    var board = command.Read<BoardShip>();
                    if (!_peerShips.TryGetValue(from, out var boardLive))
                    {
                        ConsoleLog.Write($"[boardship] {from}: BOARD_SHIP from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleBoardShip(from, boardLive, board);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad BOARD_SHIP from {from}: {e.Message}");
                }
            }
            else if (fmid == PromoteRequest.Fmid)   // FM_C_PROMOTE (Messages.h:979-981)
            {
                // The commanding ship's request to promote a boarded turret
                // gunner into its own seat (P140). Port of FedSrv.CPP:6353-6382
                // case FM_C_PROMOTE: stage gate, then walk the requesting
                // ship's OWN boarded children for the one at the requested
                // turret mount, promote it, and broadcast PROMOTE(189) sector-
                // wide. A mount matching no real child is a silent no-op.
                try
                {
                    var promoteReq = command.Read<PromoteRequest>();
                    if (!_peerShips.TryGetValue(from, out var promoteLive))
                    {
                        ConsoleLog.Write($"[promote] {from}: PROMOTE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandlePromote(from, promoteLive, promoteReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad PROMOTE from {from}: {e.Message}");
                }
            }
            else if (fmid == SwapPart.Fmid)   // FM_CS_SWAP_PART (Messages.h:273-278)
            {
                // A flying, unboarded ship's request to move the mounted part
                // at (etOld, mountOld) to mountNew, swapping it with a different
                // same-type part already there if one is present (P143). Port of
                // FedSrv.CPP:6459-6501 case FM_CS_SWAP_PART: stage gate +
                // unboarded gate, then the swap-or-move (Ship.TrySwapPart),
                // then re-broadcast SWAP_PART(34) sector-wide with shipID
                // filled in. A source mount holding no part is a silent no-op.
                try
                {
                    var swapReq = command.Read<SwapPart>();
                    if (!_peerShips.TryGetValue(from, out var swapLive))
                    {
                        ConsoleLog.Write($"[swappart] {from}: SWAP_PART from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleSwapPart(from, swapLive, swapReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad SWAP_PART from {from}: {e.Message}");
                }
            }
            else if (fmid == Reload.Fmid)   // FM_CS_RELOAD (Messages.h:843-846)
            {
                // A flying, unboarded ship's request to reload its cargo hold
                // into its live pools / equipped launchers (P192). Port of
                // FedSrv.CPP:6504-6653 case FM_CS_RELOAD: stage gate + unboarded
                // gate + the per-entry pack/launcher transfer loop
                // (Ship.TryApplyReloadEntry), then re-broadcast RELOAD(129)
                // sector-wide with shipID filled in.
                try
                {
                    var reloadReq = command.Read<Reload>();
                    if (!_peerShips.TryGetValue(from, out var reloadLive))
                    {
                        ConsoleLog.Write($"[reload] {from}: RELOAD from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleReload(from, reloadLive, reloadReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad RELOAD from {from}: {e.Message}");
                }
            }
            else if (fmid == DropPart.Fmid)   // FM_CS_DROP_PART (Messages.h:266-270)
            {
                // A flying, unboarded ship's request to drop the mounted part at
                // (et, mount) into space, leaving a real part-type treasure (P144).
                // Port of FedSrv.CPP:6296-6330 case FM_CS_DROP_PART: stage gate +
                // unboarded gate, then the removal (Ship.TryRemovePart), then
                // re-broadcast DROP_PART(33) sector-wide with shipID filled in,
                // then — unless the dropped part is an empty (Amount==0) Pack —
                // create the treasure and send FM_S_TREASURE(28) sector-wide.
                // A source mount holding no part is a silent no-op.
                try
                {
                    var dropReq = command.Read<DropPart>();
                    if (!_peerShips.TryGetValue(from, out var dropLive))
                    {
                        ConsoleLog.Write($"[droppart] {from}: DROP_PART from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleDropPart(from, dropLive, dropReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad DROP_PART from {from}: {e.Message}");
                }
            }
            else if (fmid == SetWingId.Fmid)   // FM_CS_SET_WINGID (Messages.h:705-708)
            {
                // A player tagging a ship with a wing-group id, relayed side-wide
                // (P146). Port of FedSrv.CPP:5312-5344 case FM_CS_SET_WINGID:
                // mission-not-null gate ONLY (native has no STAGE_STARTED gate),
                // then self-targeting-only (shipID must be the sender's own ship
                // — the leader-retags-another-ship branch is unreachable without
                // a team-leader system), a wingID range check, bCommanded forced
                // false, and a side-wide broadcast + server-side WingId mutation.
                try
                {
                    var swReq = command.Read<SetWingId>();
                    if (!_peerShips.TryGetValue(from, out var swLive))
                    {
                        ConsoleLog.Write($"[setwingid] {from}: SET_WINGID from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleSetWingId(from, swLive, swReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad SET_WINGID from {from}: {e.Message}");
                }
            }
            else if (fmid == TreasureAck.Fmid)   // FM_C_TREASURE_ACK (Messages.h:877-879)
            {
                // The client's mount decision on an offered part-treasure pickup
                // (P145). Port of FedSrv.CPP:6094-6152 case FM_C_TREASURE_ACK:
                // stage gate + pending-offer gate, then a real mount merges the
                // part into an existing same-type part at that slot (or mounts it
                // fresh) and broadcasts ADD_PART(164) sector-wide; the c_mountNA
                // sentinel declines and re-creates the treasure at the ship's
                // current position; either way the pending offer is cleared.
                try
                {
                    var ack = command.Read<TreasureAck>();
                    if (!_peerShips.TryGetValue(from, out var ackLive))
                    {
                        ConsoleLog.Write($"[treasureack] {from}: TREASURE_ACK from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleTreasureAck(from, ackLive, ack);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad TREASURE_ACK from {from}: {e.Message}");
                }
            }
            else if (fmid == RipcordRequest.Fmid)   // FM_C_RIPCORD_REQUEST (Messages.h:951-953)
            {
                // The client's ripcord request (P111): start, re-target, or
                // cancel a ripcord countdown toward a chosen sector. Gates and
                // state machine in HandleRipcordRequest / RequestRipcord —
                // the port of FedSrv.CPP:7715-7728 and FedSrv.CPP:13319-13378.
                try
                {
                    var req = command.Read<RipcordRequest>();
                    if (!_peerShips.TryGetValue(from, out var ripLive))
                    {
                        ConsoleLog.Write($"[ripcord] {from}: RIPCORD_REQUEST from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleRipcordRequest(from, ripLive, req);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad RIPCORD_REQUEST from {from}: {e.Message}");
                }
            }
            else if (fmid == BucketDonate.Fmid)   // FM_C_BUCKET_DONATE (Messages.h:488-491)
            {
                // A client donating money into one of its side's construction
                // buckets (P119). Port of FedSrv.CPP:7137-7211
                // case FM_C_BUCKET_DONATE: gates on STAGE_STARTED, resolves the
                // side's bucket by id, validates the pooled treasury covers the
                // donation (the honest single-pool equivalent of the native's
                // per-player pfsPlayer->GetMoney() >= moneyGiven, FedSrv.CPP:7148
                // — this port has no per-player wallets, only Side.Money), caps
                // the investment at what the bucket still needs (refunding the
                // excess by simply never deducting it), then broadcasts
                // BUCKET_STATUS to the whole side.
                try
                {
                    var donate = command.Read<BucketDonate>();
                    if (!_peerShips.TryGetValue(from, out var donateLive))
                    {
                        ConsoleLog.Write($"[bucketdonate] {from}: BUCKET_DONATE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleBucketDonate(from, donateLive, donate);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad BUCKET_DONATE from {from}: {e.Message}");
                }
            }
            else if (fmid == OrderChange.Fmid)   // FM_CS_ORDER_CHANGE (Messages.h:648-654)
            {
                // P148 — Stage 2 "stay docked" slice of the ORDER_CHANGE(93)
                // drone-command handler. Port of FedSrv.CPP:7264-7299:
                // STAGE_STARTED gate, target resolved by shipID on the commander's
                // own side (falling back to the sender's own ship), and — only
                // when the target IsNpc AND docked AND commandID==c_cidDoNothing —
                // sets Ship.StayDocked = true. Every other branch (launch,
                // return-to-base, generic SetCommand fallback) is explicitly out
                // of scope this task and no-ops with a log.
                try
                {
                    var ocReq = command.Read<OrderChange>();
                    if (!_peerShips.TryGetValue(from, out var ocLive))
                    {
                        ConsoleLog.Write($"[orderchange] {from}: ORDER_CHANGE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleOrderChange(from, ocLive, ocReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad ORDER_CHANGE from {from}: {e.Message}");
                }
            }
            else if (fmid == FireExpendableRequest.Fmid)   // FM_C_FIRE_EXPENDABLE (Messages.h:592-596)
            {
                // A flying, unboarded ship firing its mounted chaff dispenser
                // (P182). Port of FedSrv.CPP:5609-5750 case FM_C_FIRE_EXPENDABLE:
                // stage gate + unboarded gate, then resolve the mounted dispenser
                // at mount 0 of the requested equipment type, refuse on Amount==0,
                // deploy chaff + run the missile-retarget loop (Ship.DeployChaff),
                // decrement the dispenser's ammo, and broadcast FE(87) +
                // CREATE_CHAFF(109) + MISSILE_SPOOFED(110)×n + END_SPOOFING(111)
                // sector-wide. The mine/probe branch of native is explicitly out
                // of scope (separate, already-solved DeployMine/DeployProbe).
                try
                {
                    var feReq = command.Read<FireExpendableRequest>();
                    if (!_peerShips.TryGetValue(from, out var feLive))
                    {
                        ConsoleLog.Write($"[fireexpendable] {from}: FIRE_EXPENDABLE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleFireExpendable(from, feLive, feReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad FIRE_EXPENDABLE from {from}: {e.Message}");
                }
            }
            else if (fmid == FireMissile.Fmid)   // FM_CS_FIRE_MISSILE (Messages.h:547-557)
            {
                // P226 — the client→server missile-launch request, port of
                // FedSrv.CPP:5413-5507 case FM_CS_FIRE_MISSILE. Although the P226
                // brief asserted "no client→server fire-missile request exists",
                // native declares FIRE_MISSILE as a bidirectional CS message and
                // FedSrv RECEIVES it here; the high-word missileFireIGC bit cannot
                // cross the 16-bit CompactState wire (igc.h:829,2457-2465), so the
                // wire request is the real player-launch path. The server is
                // authoritative (native "ignore[s] the IDs provided by the client"):
                // it resolves the mounted ET_Magazine@0 launcher + its missile
                // type, builds lock against the ship's server-side CommandTarget
                // (updated per-tick in Ship.OnUpdate), and fires through the shared
                // Ship.TryFireMissile gate (lock-sufficient + ammo), which creates
                // the real Missile and raises MissileCreatedEvent →
                // BroadcastFireMissile (the outbound FIRE_MISSILE(70) sector
                // broadcast) and decrements ammo.
                try
                {
                    var fmReq = command.Read<FireMissile>();
                    if (!_peerShips.TryGetValue(from, out var fmLive))
                    {
                        ConsoleLog.Write($"[firemissile] {from}: FIRE_MISSILE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleFireMissile(from, fmLive, fmReq);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad FIRE_MISSILE from {from}: {e.Message}");
                }
            }
            else if (fmid == ReqPingData.Fmid)   // FM_C_REQPINGDATA (Messages.h:1012-1013)
            {
                // P200 — the client's "send me every connected player's
                // latency roster" trigger (the w0dk4 player-pings feature).
                // Port of FedSrv.CPP:7743-7808 case FM_C_REQPINGDATA. Unlike
                // almost every other C message, native answers only the
                // REQUESTER (every SendMessages targets
                // pfsPlayer->GetConnection()), and it iterates EVERY
                // connection on the whole server with a live player — NOT
                // scoped to a mission or side. The 5-second debounce is
                // native CFMConnection::PingQuery (messagecore.h:217-223).
                try
                {
                    _ = command.Read<ReqPingData>();   // zero-field trigger; decode validates cbmsg=4
                    HandleReqPingData(from);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad REQPINGDATA from {from}: {e.Message}");
                }
            }
            else
            {
                // P315 (H3): rate-limit the per-message refusal log so a flood of
                // unsupported/garbage fmids cannot force unbounded synchronous
                // console I/O inside one tick. Emit a few lines per peer per
                // second, then periodic summaries of the suppressed count.
                var t = _unsupportedLog.Ticket(from.Value);
                if (t.Emit)
                    Console.WriteLine($"[wire] from {from}: unsupported fmid {fmid} — ignored at headless milestone");
                else if (t.Summarize)
                    Console.WriteLine($"[wire] from {from}: {t.Suppressed} more unsupported-fmid messages suppressed (P315 rate limit)");
            }

            static string sideStr(short? s) => s is { } v ? v.ToString() : "(none)";
        }
    }

    // ── P654 (H-24) per-peer message/chat token budget ─────────────────────
    // The port of native CFSPlayer::DecrementChatBudget (fsship.h:449-468).
    // Charges are made on the sim thread only (inside ProcessDatagram), so no
    // locking. Refilled once per eligible player tick by AdvancePeriodicRules.
    //
    // P762 / SIM-0145 — the exact charge/debt semantics:
    //   bPay=true  : send allowed iff budget >= cost; THEN budget -= cost and
    //                the result is floored at -cost. A denied paid send therefore
    //                leaves the player in debt (-cost), not at zero.
    //   bPay=false : send allowed iff budget >= 0 and the budget is NOT changed
    //                (the group-noecho chat case, FedSrv.CPP:3372).
    // A fresh player starts at the configured maximum (fsship.cpp:958).
    private bool TryChargeChatBudget(ConnectionId peer, bool pay = true)
    {
        long now = TickIndex;
        int budget = _chatBudgets.TryGetValue(peer, out var cur) ? cur.Budget : _chatBudgetMax;
        bool send;
        if (pay)
        {
            send = budget >= _chatBudgetCost;
            budget -= _chatBudgetCost;
            if (budget < -_chatBudgetCost) budget = -_chatBudgetCost;   // fsship.h:464-465
        }
        else
        {
            send = budget >= 0;                                          // fsship.h:468
        }
        _chatBudgets[peer] = (budget, now);
        return send;
    }

    private void RemoveChatBudget(ConnectionId peer) => _chatBudgets.Remove(peer);

    // ── P654 (H-28) CharacterName validation ───────────────────────────────
    // Native limits the name to c_cbName = c_cbNameDB + 1 = 25 bytes (the usable
    // name is c_cbNameDB = 24, _Utility/Utility.h:60,64), and boots when there is
    // no NUL within that (FedSrv.CPP:5817-5818). This port stores the name as a
    // managed string (no NUL), so the enforceable limit is 24 chars.
    private static bool HasIllegalName(string name)
    {
        if (name.Length > 24) return true;
        foreach (var c in name)
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                return true;
        return false;
    }

    // P507 — the port of native CFSPlayer::SetShipUpdate →
    // CshipIGC::ProcessShipUpdate (fsship.cpp:1173-1200,
    // shipIGC.cpp:3139-3210). Runs the accept/reject pipeline on the peer's own
    // (non-boarded, in-cluster) ship; always applies the client's controls +
    // stateM first (GetSC), then judges the reported pose. Only reached for a
    // non-legacy update whose cookie matched (see the ShipUpdate.Fmid branch).
    // Sim-thread only, like its caller.
    private void ProcessPeerShipUpdate(ConnectionId from, Ship ship, ShipUpdate u, PeerShipUpdate pu)
    {
        var payload = u.Payload;

        // Native monotonic timestamp gate (FedSrv/fsship.cpp:1175). Use the
        // payload time, as native does, and refuse conflicting duplicate clocks.
        if (u.Time != payload.Time || (pu.HasClientTime && payload.Time <= pu.LastClientTime))
        { pu.DroppedReplay++; return; }
        // One pose per server simulation instant; queued packets cannot chain
        // the native 20m acceptance tolerance without advancing the world.
        if (pu.LastPoseAt == SimNow) { pu.DroppedSameTick++; return; }
        // GetSC (shipIGC.cpp:3067-3071) — the commanded controls and movement-bit
        // mask are ALWAYS applied, "as today" (the task explicitly keeps
        // controls/stateM application; native only reaches this inside the
        // in-window+legal gate — a divergence the task directs us to keep for
        // control-input continuity).
        ship.Controls = payload.Controls;
        ship.StateM = payload.StateM;

        // deltaT = server "now" (native GetMyLastUpdate, shipIGC.cpp:3141) minus
        // the client's reported SERVER time (the client converted its own clock
        // via ServerTimeFromClientTime, shipIGC.h:1178). u.Time is uint
        // milliseconds; SimNow is seconds.
        float deltaT = (float)(SimNow - u.Time / 1000.0);

        if (deltaT < -1.5f || deltaT > 1.5f)
        {
            // c_susOutOfDate (shipIGC.cpp:3142,3199-3200): the ±1.5s window.
            pu.OutOfDate++;
            ConsoleLog.Write($"[ctrlak] {from} ship {ship.Id}: OUT_OF_DATE deltaT={deltaT:0.###}s (window ±1.5s) — controls applied, pose ignored");
            return;
        }

        if (!IsLegalPosition(payload.Position))
        {
            // c_susInvalid (shipIGC.cpp:3195-3197): position outside
            // 0 < |pos|² < (90000)² (LegalPosition, shipIGC.cpp:3090-3094).
            pu.Invalid++;
            ConsoleLog.Write($"[ctrlak] {from} ship {ship.Id}: INVALID position ({payload.Position.X:0.#},{payload.Position.Y:0.#},{payload.Position.Z:0.#}) — controls applied, pose ignored");
            return;
        }

        // Advance the client's reported pose to the server's "now" with the REAL
        // flight step (CalculateShip, shipIGC.cpp:3177 → 3244-3297), chunked;
        // reuse the same ShipKinematics integrator the server tick simulates
        // (P448a) — not a second integrator.
        Vector3 position = payload.Position;
        Vector3 velocity = payload.Velocity;
        var orientation = payload.Orientation;   // fresh object from decode; CalculateShipPose mutates it in place
        var turnRates = payload.TurnRates;       // fresh float[3]

        float maxSpeed = MathF.Max(ship.MovementSpeedLimit(), ship.Velocity.Length());
        // Compare at the same instant. A delayed snapshot of authorized boost
        // or launch momentum may be faster than the server's now-decayed speed.
        ship.CalculateShipPose(deltaT, ref position, ref velocity, orientation, turnRates);
        float replayedSpeed = velocity.Length();
        // Each compact direction component rounds by at most 1/480; the
        // scalar speed is stored in eighths (Wire/Compact/Quant.cs).
        float wireRounding = .125f + maxSpeed * MathF.Sqrt(3f) / 480f;
        bool legalVelocity = FiniteVector(velocity) && replayedSpeed <= maxSpeed + wireRounding;
        // Compact velocity quantisation must not ratchet trusted momentum up
        // over successive accepted packets. Allow rounding, then cap slightly
        // below the bound to avoid float normalization rounding it upward.
        if (legalVelocity && replayedSpeed > maxSpeed)
            velocity *= (maxSpeed * .999999f) / replayedSpeed;

        if (!pu.HasMovementAnchor)
        {
            pu.HasMovementAnchor = true;
            pu.LastPosePosition = ship.Position;
            pu.MovementBudgetAt = SimNow;
            pu.MovementCredit = 20f + maxSpeed * MathF.Abs(deltaT);
        }
        // Credit is spent on total travel, not on displacement relative to the
        // last client-mutated server pose. Latency/packet loss may accumulate at
        // most 1.5 seconds of hull travel plus the native 20m tolerance. Thus a
        // sequence of small legal-looking packets cannot manufacture distance.
        float elapsed = (float)Math.Max(0, SimNow - pu.MovementBudgetAt);
        // Refill the full elapsed server time after packet loss. Idle credit
        // stays bounded, but a long legitimate gap may cover its actual travel.
        float creditCap = maxSpeed * MathF.Max(1.5f, elapsed) + 20f;
        pu.MovementCredit = MathF.Min(creditCap, pu.MovementCredit + maxSpeed * elapsed);
        pu.MovementBudgetAt = SimNow;
        float travel = (position - pu.LastPosePosition).Length();
        float envelope = 20f + maxSpeed * MathF.Min(1.5f, elapsed + MathF.Abs(deltaT));

        // Orientation is ALWAYS accepted (shipIGC.cpp:3178).
        ship.SetOrientation(orientation);

        float dSq = (ship.Position - position).LengthSquared();
        if (legalVelocity && FiniteVector(position) && velocity.Length() <= maxSpeed
            && dSq < envelope * envelope && travel <= pu.MovementCredit)
        {
            // Native's 20m tolerance (shipIGC.cpp:3179-3183), extended by a
            // bounded clock/latency allowance and constrained by total travel.
            pu.Accepted++;
            pu.HasClientTime = true;
            pu.LastClientTime = payload.Time;
            pu.LastPoseAt = SimNow;
            pu.MovementCredit -= travel;
            pu.LastPosePosition = position;
            ship.Position = position;
            ship.Velocity = velocity;

            // P760 — native warp-state handshake (fsship.cpp:1185-1191).
            // Accepted update transitions NoUpdate -> Waiting (armed for 2 s)
            // and Waiting -> Ready once the armed time has passed.
            // P760b — one clock: native arms and compares against g.timeNow, the
            // mission clock (fsship.cpp:1188), the same clock LastWarpTime is
            // stamped with and Ship.OnUpdate compares against — so TimeNextWarp
            // is armed and checked in mission time here too, never the server
            // receive clock.
            if (!ship.IsNpc)
            {
                double missionNow = ship.Mission?.LastUpdate ?? SimNow;
                switch (ship.PlayerWarpState)
                {
                    case WarpState.NoUpdate:
                        ship.PlayerWarpState = WarpState.Waiting;
                        ship.TimeNextWarp = missionNow + 2.0;
                        ConsoleLog.Write($"[warp] ship {ship.Id} update accepted: NoUpdate -> Waiting, next warp at {ship.TimeNextWarp:F3}");
                        break;
                    case WarpState.Waiting when missionNow >= ship.TimeNextWarp:
                        ship.PlayerWarpState = WarpState.Ready;
                        ConsoleLog.Write($"[warp] ship {ship.Id} update accepted: Waiting -> Ready");
                        break;
                }
            }

            ConsoleLog.Write($"[ctrlak] {from} ship {ship.Id}: ACCEPTED (dist {MathF.Sqrt(dSq):0.##}m, deltaT {deltaT:0.###}s)");
        }
        else
        {
            // c_susRejected — native sends SHIP_RESET with the server's own pose
            // + a fresh NewCookie() (fsship.cpp:1192-1200); the client must apply
            // it and echo the cookie, until which its updates are dropped by the
            // dispatch cookie gate.
            pu.Rejected++;
            pu.TotalCorrections++;
            pu.LastPoseAt = SimNow;
            pu.RejectDistanceSum += MathF.Sqrt(dSq);
            pu.RejectCount++;
            if (!legalVelocity || velocity.Length() > maxSpeed) pu.RejectedSpeed++;
            if (!FiniteVector(position) || !(dSq < envelope * envelope)) pu.RejectedEnvelope++;
            if (!(travel <= pu.MovementCredit)) pu.RejectedTravel++;
            // The reset tells the client to resume from this authoritative pose
            // (FedSrv/fsship.cpp:1192-1200). Measure its next travel from there
            // too: the server keeps flying during rejected/lost updates, so the
            // last accepted client pose can become arbitrarily stale. Retain
            // the remaining credit and budget clock; cookie renewal must never
            // grant another 20m allowance for a hostile teleport chain.
            pu.LastPosePosition = ship.Position;
            byte newCookie = NextCookie(pu, reposition: false);
            var reset = new ShipReset
            {
                Ship = ServerShipUpdate.FromModel(ship.Id, ship, (uint)TickIndex),
                Cookie = newCookie,
            };
            SendPeer(from, FedPacket.Pack(new[] { reset.Encode() }));
            ConsoleLog.Write($"[ctrlak] {from} ship {ship.Id}: REJECTED dist={MathF.Sqrt(dSq):0.##}m envelope={envelope:0.##}m speed={velocity.Length():0.##}/{maxSpeed:0.##}m/s travel={travel:0.##}m credit={pu.MovementCredit:0.##}m deltaT={deltaT:0.###}s outside movement envelope / speed / travel budget — sent SHIP_RESET cookie 0x{newCookie:X2}");
        }
    }

    /// <summary>Native <c>LegalPosition</c> (shipIGC.cpp:3090-3094):
    /// <c>0 &lt; |pos|² &lt; (90000)²</c>.</summary>
    private static bool IsLegalPosition(Vector3 position)
    {
        float l = position.LengthSquared();
        return l > 0f && l < 90000f * 90000f;
    }
}
