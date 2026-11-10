using System.Runtime.InteropServices;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>
/// The exact <see cref="ShipStatusMessage"/> wire content (everything after the
/// 4-byte <c>cbmsg/fmid</c> header, i.e. the 16-byte <c>ShipStatus</c> payload
/// plus the message's <c>shipID</c>) as a value, so the P991 change gate can
/// compare "what this side would be told" against "what it was told last" with
/// no encoding and no allocation.
/// <para><b>Native intent.</b> Native keeps one <c>ShipStatus</c> per (ship,
/// side) — <c>CFSShip::m_rgShipStatus</c> (<c>FedSrv/fsship.cpp:237-335</c>) —
/// and compares it byte-for-byte with the copy it last sent
/// (<c>FedSrv/FedSrv.CPP:3690-3699</c>: <c>if (*pss != *pssOld)</c>, then
/// <c>pfmShipStatus-&gt;status = *pssOld = *pss</c>). This struct is the port's
/// equivalent of that struct value; the wire layout it mirrors is
/// <c>src/Allegiance.Core/Wire/ShipStatus.cs:90-111</c>.</para>
/// </summary>
internal readonly struct ShipStatusContent : IEquatable<ShipStatusContent>
{
    public readonly short HullId;
    public readonly short SectorId;
    public readonly short StationId;
    public readonly short ParentId;
    /// <summary>The wire's state byte: <c>State</c> in bits 0-5, <c>Unknown</c>
    /// in bit 6, <c>Detected</c> in bit 7 (<c>ShipStatus.cs:104</c>).</summary>
    public readonly byte Flags;
    public readonly uint StateTime;

    public ShipStatusContent(short hullId, short sectorId, short stationId, short parentId,
        byte state, bool unknown, bool detected, uint stateTime)
    {
        HullId = hullId;
        SectorId = sectorId;
        StationId = stationId;
        ParentId = parentId;
        Flags = (byte)((state & 0x3F) | (unknown ? 0x40 : 0) | (detected ? 0x80 : 0));
        StateTime = stateTime;
    }

    public byte State => (byte)(Flags & 0x3F);
    public bool Unknown => (Flags & 0x40) != 0;
    public bool Detected => (Flags & 0x80) != 0;

    public bool Equals(ShipStatusContent other) =>
        HullId == other.HullId && SectorId == other.SectorId && StationId == other.StationId
        && ParentId == other.ParentId && Flags == other.Flags && StateTime == other.StateTime;

    public override bool Equals(object? obj) => obj is ShipStatusContent other && Equals(other);

    public override int GetHashCode() =>
        (HullId * 397) ^ (SectorId << 3) ^ (StationId << 7) ^ (ParentId << 11) ^ (Flags << 17) ^ (int)StateTime;

    public static bool operator ==(ShipStatusContent a, ShipStatusContent b) => a.Equals(b);
    public static bool operator !=(ShipStatusContent a, ShipStatusContent b) => !a.Equals(b);
}

public sealed partial class Server
{
    /// <summary>
    /// P991 — <c>broadcast.shipStatusChangeGate</c>, default ON since 2026-11-10 (rig A/B
    /// b105/b106). When on, every
    /// <c>FM_S_SHIP_STATUS(90)</c> send becomes edge-triggered on the wire
    /// content: a side is told a ship's status only when that status differs
    /// from the copy last sent to that side — native's per-frame rule
    /// (<c>FedSrv/FedSrv.CPP:3665-3719</c>: every other frame, per side, per
    /// ship, <c>if (*pss != *pssOld)</c>, then send to the side group and copy
    /// into the old status). OFF keeps today's three periodic refreshes
    /// (<c>StatusRefreshTicks</c>, <c>TeammateStatusRefreshTicks</c>,
    /// <c>CrossSideStatusRefreshTicks</c>) byte-for-byte.
    /// </summary>
    private bool _broadcastShipStatusChangeGate;

    /// <summary>
    /// P991 — last status content committed per (side, subject ship): native's
    /// <c>m_rgShipStatus[sid]</c> / <c>pssOld</c> pair collapsed into one entry,
    /// with the tick it was committed so the per-recipient first-delivery repair
    /// can tell "this peer has the current copy" from "this peer is behind".
    /// One entry per (side, ship) — not per (recipient, subject) — because the
    /// port's own-side and cross-side statuses are per SUBJECT, exactly like
    /// native's per-side copies (the same bytes go to every peer of the side).
    /// Sim-thread only, like every other broadcast latch. Empty and untouched
    /// while the flag is off.
    /// </summary>
    private readonly Dictionary<(short SideId, short ShipId), (ShipStatusContent Content, long SentTick)> _lastStatusSentBySide = new();

    /// <summary>
    /// P991 — the subject ships whose own-side status content CHANGED this tick,
    /// filled once per tick by <see cref="SweepShipDetection"/> (the serial sweep
    /// that already recomputes every own-side ship's Detected bit) and read by
    /// both the serial and the parallel ship-state builders. Keeping the
    /// per-SUBJECT decision in the serial sweep is what makes the parallel build
    /// byte-identical to serial with the gate on: no worker writes gate state,
    /// it only reads this set. Cleared in place, never reallocated.
    /// </summary>
    private readonly HashSet<short> _ownStatusChanged = new();

    /// <summary>P991 — resolve <c>broadcast.shipStatusChangeGate</c> from config +
    /// <c>ALLEGIANCE_BROADCAST_SHIP_STATUS_CHANGE_GATE</c> (env wins, strict
    /// grammar, same shape as the other <c>broadcast.*</c> switches) and print
    /// the active value so a rig A/B is always labelled. Default ON since
    /// 2026-11-10 (rig A/B b105/b106); OFF keeps the periodic refreshes and the
    /// pre-P991 wire byte-for-byte.</summary>
    private void ResolveShipStatusChangeGate()
    {
        _broadcastShipStatusChangeGate = ResolveShipStatusChangeGateFlag(
            _config.Broadcast?.ShipStatusChangeGate ?? true,
            Environment.GetEnvironmentVariable("ALLEGIANCE_BROADCAST_SHIP_STATUS_CHANGE_GATE"));
        Console.WriteLine(_broadcastShipStatusChangeGate
            ? "[boot] broadcast.shipStatusChangeGate = true — SHIP_STATUS(90) is sent only when the side's copy changes (native FedSrv.CPP:3665-3719); the three periodic refreshes are off; a joining/reconnecting peer gets a full status dump"
            : "[boot] broadcast.shipStatusChangeGate = false — periodic SHIP_STATUS(90) refreshes (own/teammate/cross-side) unchanged (pre-P991 wire)");
    }

    internal static bool ResolveShipStatusChangeGateFlag(bool configured, string? environment)
        => environment?.Trim().ToLowerInvariant() switch
        {
            null or "" => configured,
            "1" or "true" => true,
            "0" or "false" => false,
            _ => throw new InvalidDataException("ALLEGIANCE_BROADCAST_SHIP_STATUS_CHANGE_GATE must be 0/1 or false/true"),
        };

    // ── content builders: ONE source of truth for the packed status ---------

    /// <summary>P991 — the own-side (same-side) copy of a ship's status, exactly
    /// the content <see cref="MakeOwnShipStatus"/> and the pooled
    /// <c>PackStatus</c> write: hull/sector/station/parent, the docked/observer/
    /// turret state, <c>Unknown=false</c> (this port never hides a ship from its
    /// own side) and the real own-side <c>Detected</c> bit
    /// (<c>FedSrv/fsship.cpp:318/321-334</c>).</summary>
    private static ShipStatusContent OwnStatusContent(Ship ship, short sectorId, bool detected)
    {
        if (ship.ParentShip is { } parent)
            return new ShipStatusContent(
                hullId: -1,                                                       // NA — riding another hull (FedSrv.CPP:2454)
                sectorId: sectorId,
                stationId: ship.StationId ?? (short)-1,
                parentId: parent.Id,
                state: ship.TurretId is null ? ShipStatusMessage.StateObserver : ShipStatusMessage.StateTurret,
                unknown: false, detected: detected, stateTime: 0);
        bool docked = ship.StationId is not null;
        return new ShipStatusContent(
            hullId: ship.HullType.HullId,
            sectorId: sectorId,
            stationId: docked ? ship.StationId!.Value : (short)-1,
            parentId: -1,
            state: docked ? ShipStatusMessage.StateDocked : ShipStatusMessage.StateFlying,
            unknown: false, detected: detected, stateTime: 0);
    }

    /// <summary>P991 — a same-side NPC's own-side copy (the P366 path), built from
    /// the <see cref="Ship"/> alone like <see cref="MakeNpcShipStatus"/>; boarded
    /// state is not modelled for NPCs.</summary>
    private static ShipStatusContent NpcStatusContent(Ship ship, bool detected)
    {
        bool docked = ship.StationId is not null;
        return new ShipStatusContent(
            hullId: ship.HullType.HullId,
            sectorId: ship.Sector?.Id ?? 0,
            stationId: docked ? ship.StationId!.Value : (short)-1,
            parentId: -1,
            state: docked ? ShipStatusMessage.StateDocked : ShipStatusMessage.StateFlying,
            unknown: false, detected: detected, stateTime: 0);
    }

    /// <summary>P991 — the seeing side's cross-side copy (native
    /// <c>ShipStatusSpotted</c>/<c>ShipStatusHidden</c>,
    /// <c>FedSrv/fsship.cpp:237-291</c>): last-known sector, <c>Detected=false</c>
    /// (the seeing side's copy is never marked detected) and the hidden
    /// transition as <c>Unknown=true</c>.</summary>
    private static ShipStatusContent CrossSideStatusContent(Ship ship, short sectorId, bool spotted)
    {
        bool docked = ship.StationId is not null;
        return new ShipStatusContent(
            hullId: ship.HullType.HullId,
            sectorId: sectorId,
            stationId: docked ? ship.StationId!.Value : (short)-1,
            parentId: -1,
            state: docked ? ShipStatusMessage.StateDocked : ShipStatusMessage.StateFlying,
            unknown: !spotted, detected: false, stateTime: 0);
    }

    /// <summary>P991 — build a <see cref="ShipStatusMessage"/> from a content
    /// value, so the encoders and the change gate can never drift apart.</summary>
    private static ShipStatusMessage StatusMessage(short shipId, in ShipStatusContent content) => new()
    {
        ShipId = shipId,
        HullId = content.HullId,
        SectorId = content.SectorId,
        StationId = content.StationId,
        ParentId = content.ParentId,
        State = content.State,
        Unknown = content.Unknown,
        Detected = content.Detected,
        StateTime = content.StateTime,
    };

    // ── the per-tick subject decision (serial, in SweepShipDetection) --------

    /// <summary>P991 — record one own-side ship's status content for this tick and
    /// mark the subject when it differs from the copy this side was last told
    /// (native's <c>if (*pss != *pssOld)</c>). Called once per own-side ship per
    /// tick from <see cref="SweepShipDetection"/> — never per (recipient, subject)
    /// pair, so the comparison is O(ships) per tick and allocates nothing.</summary>
    private void TrackOwnStatusContent(Ship ship, short sectorId, bool detected)
    {
        var content = OwnStatusContent(ship, sectorId, detected);
        var key = (ship.SideId ?? (short)-1, ship.Id);
        if (_lastStatusSentBySide.TryGetValue(key, out var last) && last.Content.Equals(content)) return;
        _lastStatusSentBySide[key] = (content, TickIndex);
        _ownStatusChanged.Add(ship.Id);
    }

    /// <summary>P991 — the same one-per-tick decision for a same-side NPC.</summary>
    private void TrackNpcStatusContent(Ship ship, bool detected)
    {
        var content = NpcStatusContent(ship, detected);
        var key = (ship.SideId ?? (short)-1, ship.Id);
        if (_lastStatusSentBySide.TryGetValue(key, out var last) && last.Content.Equals(content)) return;
        _lastStatusSentBySide[key] = (content, TickIndex);
        _ownStatusChanged.Add(ship.Id);
    }

    /// <summary>P991 — drop every side's copy of one subject ship from the gate.
    /// Runs when the ship's SIDE changes (a side switch makes every side's copy —
    /// its old own-side copy and the seeing sides' cross-side copies — a
    /// different thing, so the new copies must be re-sent), when its id is about
    /// to be reused by a fresh ship (<see cref="AnnounceFreshShip"/>) and when it
    /// leaves the world. O(gate size) = O(ships × sides); never on the per-tick
    /// path.</summary>
    private void DropShipStatusContent(short shipId)
    {
        if (_lastStatusSentBySide.Count == 0) return;
        foreach (var key in _lastStatusSentBySide.Keys.Where(k => k.ShipId == shipId).ToList())
            _lastStatusSentBySide.Remove(key);
    }

    /// <summary>P991 — the same drop for a whole set of subjects (one world's
    /// ships at a rotation, from <see cref="RetirePerWorldShipState"/>); keeps
    /// every other mission's copies. O(gate size).</summary>
    private void DropRetiredStatusContent(HashSet<short> keep)
    {
        if (_lastStatusSentBySide.Count == 0) return;
        foreach (var key in _lastStatusSentBySide.Keys.Where(k => !keep.Contains(k.ShipId)).ToList())
            _lastStatusSentBySide.Remove(key);
    }

    /// <summary>P991 — the change gated cross-side status sweep for one
    /// (ship, seeing side) pair. Replaces the flag-off edge + phased per-recipient
    /// catch-up with native's per-side copy comparison (<c>FedSrv.CPP:3690-3699</c>:
    /// send to the side when the side's copy changed, then update the copy):
    /// <list type="bullet">
    /// <item>a visibility EDGE (spotted/hidden) always goes out — the content's
    /// <c>Unknown</c> bit flips, so the comparison would fire anyway, and this
    /// keeps the verbose diagnostic identical to the flag-off path;</item>
    /// <item>a still-visible ship whose copy changed (sector, hull, dock state)
    /// goes to the WHOLE side at once — native sends to
    /// <c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c> (<c>FedSrv.CPP:3719</c>);</item>
    /// <item>a peer whose own latch is older than the committed copy (late joiner,
    /// reconnect, side switch) gets one — bounded by the same phase rotation
    /// (<see cref="CrossSideSweepCache.Recipients"/>) the flag-off catch-up uses,
    /// so a 400-peer sweep never scans every pair every tick;</item>
    /// <item>a HIDDEN, unchanged ship sends nothing: never re-tell a side where a
    /// ship it cannot see happens to be (the flag-off path is silent there too).</item>
    /// </list>
    /// </summary>
    private void StatusGateCrossSide(CrossSideSweepCache sweep, Ship ship, short sideId,
        List<ConnectionId> conns, short shipSector, bool nowSeen, bool wasSeen, uint scope)
    {
        var key = (sideId, ship.Id);
        if (nowSeen != wasSeen)
        {
            _crossSideShipStatusSeen[key] = nowSeen;
            var edge = CrossSideStatusContent(ship, shipSector, nowSeen);
            _lastStatusSentBySide[key] = (edge, TickIndex);
            foreach (var conn in conns)
            {
                SendPeer(conn, PackCrossSideStatus(ship, shipSector, nowSeen));
                _crossSideShipStatusTick[(conn, ship.Id)] = TickIndex;
            }
            if (ConsoleLog.Verbose) ConsoleLog.Write($"[{(nowSeen ? "xspot" : "xhidd")}] side {sideId} {(nowSeen ? "spots" : "loses")} enemy ship {ship.Id} sector {shipSector}");
            return;
        }
        if (!nowSeen) return;   // hidden and unchanged: the side already has the hidden copy

        var content = CrossSideStatusContent(ship, shipSector, spotted: true);
        long committed;
        if (!_lastStatusSentBySide.TryGetValue(key, out var sent) || !sent.Content.Equals(content))
        {
            _lastStatusSentBySide[key] = (content, TickIndex);
            committed = TickIndex;
            foreach (var conn in conns)
            {
                SendPeer(conn, PackCrossSideStatus(ship, shipSector, spotted: true));
                _crossSideShipStatusTick[(conn, ship.Id)] = TickIndex;
            }
            if (ConsoleLog.Verbose) ConsoleLog.Write($"[xcopy] side {sideId} ship {ship.Id} copy changed -> sector {shipSector} ({conns.Count} peer(s))");
            return;
        }
        committed = sent.SentTick;

        // The side's copy is unchanged; only peers that never received THIS copy
        // need one (a late joiner, a cleared latch). The phase rotation bounds the
        // scan exactly like the flag-off catch-up.
        var recipients = TestLegacyClashProjection ? conns
            : sweep.Recipients(ship, sideId, ship.Id, TickIndex, scope: scope);
        foreach (var conn in recipients)
        {
            long last = _crossSideShipStatusTick.TryGetValue((conn, ship.Id), out var l) ? l : -1;
            if (last >= committed) continue;
            SendPeer(conn, PackCrossSideStatus(ship, shipSector, spotted: true));
            _crossSideShipStatusTick[(conn, ship.Id)] = TickIndex;
            if (ConsoleLog.Verbose) ConsoleLog.Write($"[xcatchup] side {sideId} ship {ship.Id} -> peer {conn} (behind the side's copy)");
        }
    }

    /// <summary>P991 — a ship's SIDE changed: every status copy about it is a
    /// different thing now (its old side's own-side copy is gone; the new side
    /// must be told its own-side copy; a side that used to see it as an enemy
    /// holds an enemy copy), and — when the ship belongs to a seated peer — the
    /// switching peer's whole picture is from the wrong side. Drop the subject's
    /// per-side copies (the serial detection sweep re-commits them this tick, so
    /// the ordinary edge path re-sends) and the switching recipient's per-pair
    /// latches (so the first-send path re-delivers the new side's picture).
    /// Native has no such edge because a native side switch reuses the same
    /// per-side <c>ShipStatus</c> structs and the side's roster/status copy is
    /// rebuilt by its own events (<c>FedSrv/fsmission.cpp:562-563,601-613</c>);
    /// the port's incremental per-pair streams need the explicit invalidation.
    /// O(gate + the switching peer's pair latches), i.e. a rare side switch, never
    /// the per-tick path. No-op while the gate is off.</summary>
    private void OnShipSideChanged(Ship ship)
    {
        if (!_broadcastShipStatusChangeGate) return;
        short shipId = ship.Id;
        DropShipStatusContent(shipId);

        ConnectionId? switcher = null;
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, ship)) { switcher = kv.Key; break; }

        foreach (var key in _lastTeammateStatusTick.Keys
                     .Where(k => k.Item2 == shipId || (switcher is { } c && k.Item1 == c)).ToList())
            _lastTeammateStatusTick.Remove(key);
        foreach (var key in _lastSameSideNpcStatusTick.Keys
                     .Where(k => k.ShipId == shipId || (switcher is { } c && k.Recipient == c)).ToList())
            _lastSameSideNpcStatusTick.Remove(key);
        // The cross-side latch doubles as the repair scheduler: bump the sweep
        // removal version like every other removal of it so the audience/phase
        // cache is rebuilt on the next sweep.
        if (_crossSideShipStatusTick.Keys.Any(k => k.Item2 == shipId || (switcher is { } c2 && k.Item1 == c2)))
            _statusSweepRemovalVersion++;
        foreach (var key in _crossSideShipStatusTick.Keys
                     .Where(k => k.Item2 == shipId || (switcher is { } c && k.Item1 == c)).ToList())
            _crossSideShipStatusTick.Remove(key);
    }

    /// <summary>P991 — the gate's live entry counts, for the memory-bound tests
    /// (one entry per (side, subject) the server has told, plus this tick's
    /// changed-subject set). Inert in production.</summary>
    internal (int BySide, int ChangedThisTick) StatusGateCounts()
        => (_lastStatusSentBySide.Count, _ownStatusChanged.Count);

    /// <summary>
    /// P991 — the native join-time full status dump, sent only with
    /// <c>broadcast.shipStatusChangeGate</c> on. Native sends every ship's status
    /// to a joining player in <c>CFSMission::SendMissionInfo</c>
    /// (<c>FedSrv/fsmission.cpp:4303-4323</c>: "send the data on all known ships",
    /// one <c>SHIP_STATUS</c> per ship of <c>m_pMission-&gt;GetShips()</c> whose side
    /// id is &gt;= 0, carrying THE JOINING SIDE'S copy — "We know something about
    /// the ship, even if it is obsolete info"). That dump is what makes the
    /// per-side change gate safe: the incremental per-frame send (native
    /// <c>FedSrv.CPP:3665-3719</c>) only fires on a change, so the joiner's whole
    /// picture has to arrive once, at join.
    /// <para><b>Port shape.</b> The joiner's own ship's status is already sent by
    /// the join burst (<c>CompleteJoinAndLaunch</c>), so it is skipped here — and
    /// deliberately NOT recorded in the gate, so the first broadcast tick after
    /// the join sends it once more. That single 24-byte frame is the port's repair
    /// for a consumer that subscribes to its transport after the burst (the
    /// native managed strategies attach post-assignment, <c>StrategyBase.cs:90</c>,
    /// the reason <see cref="StatusRefreshTicks"/> existed): the incremental path
    /// then has an already-delivered, current copy to compare against.</para>
    /// <para><b>Which ships.</b> Every ship of the mission on the joiner's own side
    /// (the own-side copy) and every ship another side owns that the joiner's side
    /// currently sees (<c>SeenBySide</c>, the cross-side spotted copy). An unseen
    /// enemy is SKIPPED: native's copy for a side that has never seen a ship holds
    /// only defaults (<c>ShipStatusStart</c> writes the ship's own side's copy,
    /// <c>FedSrv/fsship.cpp:437-447</c>), so there is nothing to tell and sending
    /// this port's last-known copy would leak hull/sector the side never had.
    /// </para>
    /// <para><b>State.</b> Every shipped entry is recorded as delivered to THIS
    /// peer only — the per-(recipient, subject) latch of whichever path owns that
    /// ship (teammate / same-side NPC / cross-side) — so nothing is re-sent to it
    /// until the content really changes. The side's committed copy
    /// (<c>_lastStatusSentBySide</c>) is never written here: it is what the whole
    /// side was last sent, and only the per-tick sweep maintains it.
    /// O(ships) once per join, never on the per-tick path.</para>
    /// </summary>
    private void SendStatusDumpToJoiner(ConnectionId to, Mission mission, short shipId, LiveShip live)
    {
        if (!_broadcastShipStatusChangeGate) return;
        if (mission.Stage == MissionStage.Over) return;
        short? joinSide = live.Ship.SideId is { } js && js >= 0 ? js : null;

        // The joining peer's LiveShip lookup for the own-side ships: a peer ship's
        // status carries the sector of its LiveShip record (the same value the
        // per-tick builder uses); an NPC carries its own sector.
        Dictionary<short, LiveShip>? peerLives = null;
        foreach (var other in _peerShips.Values)
        {
            if (!ReferenceEquals(other.Ship.Mission, mission)) continue;
            (peerLives ??= new Dictionary<short, LiveShip>(_peerShips.Count))[other.Ship.Id] = other;
        }

        var messages = new List<byte[]>(32);
        int nOwnSide = 0, nSeen = 0;
        foreach (var ship in mission.GetShips())
        {
            if (ship.Id == shipId) continue;                 // own status already sent by the join burst
            if (ship.SideId is not { } shipSide || shipSide < 0) continue;   // native: GetSide()->GetObjectID() >= 0
            ShipStatusContent content;
            if (joinSide is { } side && shipSide == side)
            {
                LiveShip? subject = null;
                if (peerLives is not null) peerLives.TryGetValue(ship.Id, out subject);
                content = subject is not null
                    ? OwnStatusContent(ship, subject.Sector.Id, ShipDetection.IsDetectedByEnemy(ship))
                    : NpcStatusContent(ship, ShipDetection.IsDetectedByEnemy(ship));
                // Record the delivery on the path that owns this ship, so the
                // incremental builder does not repeat it until the content changes.
                if (subject is not null) _lastTeammateStatusTick[(to, ship.Id)] = TickIndex;
                else _lastSameSideNpcStatusTick[(to, ship.Id)] = TickIndex;
                nOwnSide++;
            }
            else if (joinSide is { } seeing && ship.Sector is { } sector && ship.SeenBySide(seeing))
            {
                content = CrossSideStatusContent(ship, sector.Id, spotted: true);
                _crossSideShipStatusTick[(to, ship.Id)] = TickIndex;
                nSeen++;
            }
            else
            {
                continue;                                    // not known to the joiner's side — nothing to say
            }
            // P991 review — the dump is per JOINER: it records only the joiner's
            // per-pair latch above and never touches the side's committed copy.
            // Writing `(shipSide, id)` here stored a seen enemy's CROSS-side copy
            // under the enemy's OWN side key (Detected=false over the own copy's
            // true), so the next sweep re-sent that enemy's status to its whole
            // side on every join; and re-stamping any committed copy's SentTick
            // makes every other peer of the side look "behind" to the cross-side
            // repair. The committed copy is what the side was last sent, which
            // the per-tick sweep alone maintains (pinned by
            // Flag_on_join_dump_reaches_only_the_joiner_and_leaves_every_side_copy_alone).
            messages.Add(StatusMessage(ship.Id, content).Encode());
        }
        for (int i = 0; i < messages.Count; i += FedPacket.MaxMessagesPerPacket)
        {
            int n = Math.Min(FedPacket.MaxMessagesPerPacket, messages.Count - i);
            SendPeer(to, FedPacket.Pack(CollectionsMarshal.AsSpan(messages).Slice(i, n)));
        }
        if (messages.Count > 0)
            Console.WriteLine($"[shipstatus] peer {to} -> join status dump {messages.Count} ship(s) (own side {nOwnSide}, seen cross-side {nSeen}) — native SendMissionInfo fsmission.cpp:4303-4323");
    }
}
