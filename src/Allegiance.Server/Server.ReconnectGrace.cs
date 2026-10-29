using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>
/// P813c (EXPERIMENTAL) — reconnect grace. Everything here is inert unless
/// <c>reconnect.enabled</c> is set in <c>server.json</c> (or
/// <c>--reconnect</c>/<c>--reconnectGrace=N</c> on the command line), and the
/// whole feature is an <b>experiment behind a default-off flag</b>: kept only if
/// the 200v200 baseline improves (see
/// <c>docs/reports/P813C_RECONNECT_GRACE.md</c>).
///
/// <para><b>Native baseline (what a drop does today, and what the window
/// expiry still does):</b> the dropped connection's player object is destroyed
/// (<c>FedSrv/FedSrv.CPP:4549-4566</c> <c>OnDestroyConnection</c> →
/// <c>delete pfsPlayer</c>), which runs
/// <c>RemovePlayerFromMission(QSR_LinkDead)</c> (<c>FedSrv/fsship.cpp:995</c>)
/// and destroys the ship (<c>FedSrv/fsship.cpp:58-68</c>
/// <c>CFSShip::~CFSShip</c> → <c>AnnounceExit(NULL, SDR_LOGGEDOFF)</c> +
/// <c>m_pShip-&gt;Terminate()</c>). A <em>voluntary</em> quit is deleted in the
/// LOGOFF handler itself (<c>FedSrv.CPP:5801</c>, with the comment that the
/// delete is there "so we can tell when we get there whether it was a dropped
/// connection"). This class only delays the dropped-connection delete; the
/// voluntary-quit path calls <c>ReleasePeerOnRequest</c> and is immediate.</para>
///
/// <para><b>Resume token:</b> P883 carries the client-generated session nonce in
/// the optional tagged trailing LOGONREQ extension, <c>ResumeToken</c>. This is
/// explicitly port-only: native <c>dwCookie</c> selects the mission
/// (<c>Inc/Messages.h:43</c>), and <c>LOGONACK.cookie</c> is a distinct byte
/// gameplay cookie (<c>FedSrv.CPP:983</c>). A client with no token is never parked.</para>
///
/// <para><b>Controls during the gap:</b> native has no "client silent" timer —
/// a ship keeps the last <c>SetControls</c> it was given. Decision: zero the
/// controls to <see cref="ControlData.Neutral"/> (the native
/// <c>ControlData::Reset()</c>, <c>Igc/igc.h:1666-1670</c>, used by
/// <c>CshipIGC::PlotShipMove</c> for a ship with no plan target,
/// <c>Igc/shipIGC.cpp:2308-2348</c>, which also clears the movement/weapon
/// state bits). The ship therefore <b>coasts</b> on its current velocity with
/// no turning and no firing, instead of flying blind on the pilot's last
/// stick input.</para>
///
/// <para><b>Leadership:</b> a parked ship keeps its seat, so a side leader who
/// drops keeps leadership for the window; native has no equivalent (it
/// promotes at once, <c>RemovePlayerFromSide</c> → <c>pship-&gt;Promote()</c>,
/// <c>fsmission.cpp:895-913</c>). If the window expires, the native removal —
/// including that succession — runs unchanged.</para>
/// </summary>
public sealed partial class Server
{
    // P813c/P883 — parked seats are keyed by mission cookie and character name (the mission's
    // unique player identity, FedSrv.CPP:600-624) with the token as a second
    // factor, because the port extension field the token rides (LOGONREQ.ResumeToken)
    // is only 32 bits and older tooling sends a constant there.
    private readonly Dictionary<(uint Cookie, string Name), ParkedPeer> _parkedShips = new();
    private static (uint, string) ParkedKey(uint cookie, string name) => (cookie, name.ToUpperInvariant());

    private bool _reconnectGraceEnabled;
    private double _reconnectGraceSeconds;
    private long _reconnectParkedTotal, _reconnectResumedTotal, _reconnectExpiredTotal, _reconnectRefusedTotal;

    /// <summary>One held (parked) unrequested drop. <see cref="Live"/> is the
    /// player's seat — the same <c>Ship</c> object, still in its sector, simply
    /// no longer in <c>_peerShips</c> (so it stops receiving commands and keeps
    /// flying under <c>Mission.Update</c> with neutral controls).</summary>
    private sealed record ParkedPeer(
        LiveShip Live,
        string Name,
        uint Token,
        double ExpiresAt,
        ConnectionId Conn,
        QuitSideReason Reason,
        bool Ready,
        Allegiance.Core.Identity.JoinIdentity? Identity = null);

    /// <summary>P813c test seam — whether the experimental grace is armed.</summary>
    internal bool ReconnectGraceEnabled => _reconnectGraceEnabled;

    /// <summary>P813c test seam — the resolved hold window (sim seconds).</summary>
    internal double ReconnectGraceSeconds => _reconnectGraceSeconds;

    /// <summary>P813c test seam — ships currently parked (held for a resume).</summary>
    internal int ReconnectParkedShipCount => _parkedShips.Count;

    /// <summary>P813c test seam — the parked ship id for <paramref name="name"/>, or null.</summary>
    internal short? ReconnectParkedShipId(string name)
    {
        foreach (var (key, parked) in _parkedShips)
            if (string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase))
                return parked.Live.Ship.Id;
        return null;
    }

    /// <summary>P813c — cumulative counters for tests and the windowed
    /// <c>[reconnect-summary]</c> telemetry line the rig recipe reads.</summary>
    internal long ReconnectParkedTotal => Interlocked.Read(ref _reconnectParkedTotal);
    internal long ReconnectResumedTotal => Interlocked.Read(ref _reconnectResumedTotal);
    internal long ReconnectExpiredTotal => Interlocked.Read(ref _reconnectExpiredTotal);
    internal long ReconnectRefusedTotal => Interlocked.Read(ref _reconnectRefusedTotal);

    /// <summary>
    /// P813c — resolve <c>reconnect.enabled</c> / <c>reconnect.graceSeconds</c>
    /// once at boot, validate, and banner the active value so an A/B run can
    /// never be mistaken for the other arm of the experiment. Default OFF.
    /// </summary>
    private void ResolveReconnectGrace()
    {
        var rc = _config.Reconnect ?? new ReconnectConfig();
        rc.Validate();
        _reconnectGraceEnabled = rc.Enabled;
        _reconnectGraceSeconds = rc.GraceSeconds;
        Console.WriteLine(_reconnectGraceEnabled
            ? $"[boot] *** RECONNECT GRACE ACTIVE (reconnect.enabled=true, graceSeconds={_reconnectGraceSeconds:0.##}) — "
              + "EXPERIMENTAL non-native: an unrequested drop parks the ship for the window; a resume token "
              + "(LOGONREQ.ResumeToken) rejoins the same ship. Expiry runs the native removal (FedSrv.CPP:4549-4566). ***"
            : "[boot] reconnect.enabled=false — native immediate ship removal on a drop (FedSrv.CPP:4549-4566, fsship.cpp:58-68)");
    }

    /// <summary>
    /// P813c — try to hold an unrequested drop's ship for the grace window.
    /// Returns false (and changes nothing) when the feature is off, the peer
    /// never presented a resume token, or the ship is already gone — in every
    /// one of those cases the caller falls through to the native removal.
    /// </summary>
    private bool TryParkLiveShip(ConnectionId id, LiveShip live, QuitSideReason reason,
        Allegiance.Core.Identity.JoinIdentity? identity = null)
    {
        if (!_reconnectGraceEnabled) return false;
        if (live.ResumeToken == 0)
        {
            // No token: the client cannot prove it is the same session, so hold
            // nothing (this is also today's tooling, which sends a constant).
            Console.WriteLine($"[reconnect] peer {id} ship {live.Ship.Id} dropped with no resume token (LOGONREQ.ResumeToken=0) — "
                + "native immediate removal, nothing parked");
            return false;
        }
        if (live.Ship.Sector is null || live.Ship.Mission is not { } mission || !_missions.Contains(mission))
        {
            // Never seated / already finalized: there is no world state to hold.
            return false;
        }
        string name = _peerNames.TryGetValue(id, out var known) ? known : "";
        if (name.Length == 0) return false;

        // Coast: native's ControlData::Reset() (Igc/igc.h:1666-1670) — zero the
        // turn axes, throttle to −1 (no thrust input) — and clear the
        // movement/weapon state bits, exactly the no-plan branch of
        // CshipIGC::PlotShipMove (Igc/shipIGC.cpp:2308-2348). The ship keeps its
        // velocity (coast) but cannot turn or fire while nobody is flying it.
        live.Ship.Controls = ControlData.Neutral;
        live.Ship.StateM = 0;

        lock (_statsLock)
        {
            double now = _simNow;
            _parkedShips[ParkedKey(mission.Cookie, name)] = new ParkedPeer(live, name, live.ResumeToken, now + _reconnectGraceSeconds, id, reason,
                _playerReady.TryGetValue(id, out var ready) && ready, identity);
        }
        Interlocked.Increment(ref _reconnectParkedTotal);
        Console.WriteLine($"[reconnect] peer {id} '{ConsoleLog.Sanitize(name)}' ship {live.Ship.Id} dropped UNREQUESTED — "
            + $"parked {_reconnectGraceSeconds:0.##}s (token 0x{live.ResumeToken:X8}, controls neutral/coast); "
            + "native removal (FedSrv.CPP:4549-4566) deferred until the window expires");
        return true;
    }

    /// <summary>
    /// P813c — resume a parked seat for a reconnecting peer's LOGONREQ.
    /// <para>Match is <b>name + token</b>. A name match with the wrong token (a
    /// different client claiming the name, or a genuine new session that does not
    /// know the token) is refused AND the parked ship is retired immediately
    /// through the native path, so a player can never end up with two ships.
    /// </para>
    /// <para>A parked ship that died during the gap (no sector — the port's
    /// death contract, <c>FinalizeDeadShip</c>) is not resurrected: the entry is
    /// dropped and the logon continues as a normal fresh logon (a new ship), the
    /// pre-P813c outcome. Game-over during the gap is handled by
    /// <see cref="DropParkedShipsForMission"/> at rotation.</para>
    /// Returns true when the peer was resumed and LOGONACK + the join burst were
    /// sent (the caller must then skip the normal logon path).
    /// </summary>
    private bool TryResumeParkedPeer(ConnectionId from, LogonRequest req)
    {
        if (!_reconnectGraceEnabled) return false;
        string name = req.CharacterName ?? "";
        var requestedMission = ResolveJoinMission(req.DwCookie);
        if (name.Length == 0 || requestedMission is null || requestedMission.Stage == MissionStage.Over
            || !_parkedShips.TryGetValue(ParkedKey(requestedMission.Cookie, name), out var parked)) return false;

        if (!ReferenceEquals(ResolveJoinMission(req.DwCookie), parked.Live.Ship.Mission)) return false;
        if (req.ResumeToken != parked.Token)
        {
            Interlocked.Increment(ref _reconnectRefusedTotal);
            Console.WriteLine($"[reconnect] {from} '{ConsoleLog.Sanitize(name)}' claims a parked seat with token "
                + $"0x{req.ResumeToken:X8} != 0x{parked.Token:X8} — REFUSED; retiring the parked ship {parked.Live.Ship.Id} now (native path)");
            _parkedShips.Remove(ParkedKey(requestedMission.Cookie, name));
            RetireDepartedPeer(parked.Live, parked.Reason, parked.Name);
            return false;
        }

        _parkedShips.Remove(ParkedKey(requestedMission.Cookie, name));
        var live = parked.Live;
        // The name was free while the seat was parked, so a NEW player may have
        // taken it in the meantime. Resuming would then put two ships with one
        // name in the mission — refuse, retire the parked seat, and let this
        // logon take the normal (duplicate-name) path below.
        bool nameTaken = _peerNames.Any(kv => kv.Key != from
            && ReferenceEquals(ConnectionMission(kv.Key), parked.Live.Ship.Mission)
            && string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase));
        if (nameTaken)
        {
            Interlocked.Increment(ref _reconnectRefusedTotal);
            Console.WriteLine($"[reconnect] {from} '{ConsoleLog.Sanitize(name)}' resume REFUSED — the name is now used by "
                + $"another live peer; retiring the parked ship {live.Ship.Id} (native path)");
            RetireDepartedPeer(live, parked.Reason, parked.Name);
            return false;
        }
        bool resumable = live.Ship.Sector is not null
                         && live.Ship.Mission is { } mission && _missions.Contains(mission);
        if (!resumable)
        {
            // Died (or was finalized) during the gap, or the mission rotated out
            // from under it: nothing to resume — fall back to a normal logon.
            Interlocked.Increment(ref _reconnectRefusedTotal);
            Console.WriteLine($"[reconnect] {from} '{ConsoleLog.Sanitize(name)}' resume REFUSED — ship {live.Ship.Id} is no longer "
                + $"in the world (sector {(live.Ship.Sector is null ? "none" : "present")}, "
                + $"mission {(live.Ship.Mission is null ? "gone" : "outside the live set")}); "
                + "continuing as a fresh logon (the ship stays dead — no resurrection)");
            return false;
        }

        _peerNames[from] = name;
        _peerRequests[from] = req;
        // The connection-scoped bearer authentications were dropped with the old
        // connection; the same player presents the same LOGONREQ here, so re-run
        // the exact logon-time gate (native keeps the player object and its
        // privileges across a link drop it never had a window for).
        AuthenticatePrivileges(from, req);
        // Native keeps the per-player ready flag across a connection gap (it is
        // per player per mission, fsmission.cpp:3468-3481) — restore what the
        // peer had at drop time instead of resetting it.
        _playerReady[from] = parked.Ready;

        // LOGONACK first (a packet-order-fixed client expects it as the first
        // frame), same shape/values as a fresh validated logon: the SAME ship id
        // and the session cookie the native ack carries (FedSrv.CPP:983).
        var ack = new LogonAck
        {
            CharNameOrFailureReason = $"Welcome back {name}",
            TimeServer = (uint)req.Time + 1,
            ShipID = live.Ship.Id,
            FValidated = true,
            FRetry = false,
            Cookie = 0,
        };
        SendPeer(from, FedPacket.Pack(new[] { ack.Encode() }));
        Console.WriteLine($"[reconnect] {from} '{ConsoleLog.Sanitize(name)}' RESUME — ship {live.Ship.Id} seat "
            + $"side {live.Ship.SideId?.ToString() ?? "(none)"} reattached (token 0x{parked.Token:X8}); replaying the native join burst");

        // Replay THE native join sequence for the resumed peer (MISSIONDEF →
        // JOINED_MISSION → PLAYERINFOs → SET_MONEY/tech → CLUSTERINFO/exports →
        // SET_CLUSTER → MISSION_STAGE → SHIP_STATUS → ENTER_GAME), so the client's
        // world is re-synced from the server's authoritative state — the same
        // burst native's SendMissionInfo/AddPlayerToSide sends a player who joins
        // an in-progress mission (fsmission.cpp:3943-3974,4122-4250). No mission
        // announce (the player never left the mission) and no economy re-grant
        // (resumed seat, not a new one).
        // The seat's verified account (P808a) carries over: the resume token proves the session.
        if (parked.Identity is { } identity) _peerIdentities[from] = identity;
        CompleteJoinAndLaunch(from, req, live.Ship.Id, ack.Cookie, live, announceToMission: false, resumed: true);
        Interlocked.Increment(ref _reconnectResumedTotal);
        Console.WriteLine($"[reconnect] {from} '{ConsoleLog.Sanitize(name)}' resumed ship {live.Ship.Id} — "
            + $"join burst replayed ({_reconnectGraceSeconds:0.##}s window)");
        return true;
    }

    /// <summary>Sim-thread snapshot of the simulation clock (the reconnect paths
    /// run from the transport drain / sim pass, both on the sim thread).</summary>
    private double NowSeconds()
    {
        lock (_statsLock) return _simNow;
    }

    /// <summary>
    /// P813c — the grace sweep, run once per tick. Every parked ship whose window
    /// elapsed is removed from the table and put through the native removal
    /// (retire the ship + announce the departure), i.e. exactly what the drop
    /// would have done at once with the flag off. Cheap no-op when idle.
    /// </summary>
    private void AdvanceReconnectGrace()
    {
        if (_parkedShips.Count == 0) return;
        double now = NowSeconds();
        List<ParkedPeer>? expired = null;
        foreach (var parked in _parkedShips.Values)
        {
            if (parked.ExpiresAt > now) continue;
            (expired ??= new List<ParkedPeer>()).Add(parked);
        }
        if (expired is null) return;
        foreach (var parked in expired)
        {
            _parkedShips.Remove(ParkedKey(parked.Live.Ship.Mission!.Cookie, parked.Name));
            Interlocked.Increment(ref _reconnectExpiredTotal);
            Console.WriteLine($"[reconnect] '{ConsoleLog.Sanitize(parked.Name)}' did not return within "
                + $"{_reconnectGraceSeconds:0.##}s — running the native drop removal for ship {parked.Live.Ship.Id} "
                + "(SDR_LOGGEDOFF + departure, FedSrv.CPP:4549-4566)");
            RetireDepartedPeer(parked.Live, parked.Reason, parked.Name);
        }
    }

    /// <summary>
    /// P813c — a mission that just rotated (game over) cannot honour a parked
    /// seat in the world it belonged to. Drop those entries WITHOUT the departure
    /// protocol: the whole old mission is being discarded (native's
    /// <c>Vacate()</c>/<c>Reset</c>, fsmission.cpp:2604-2622), so there are no
    /// peers left in it to announce to and no side to re-evaluate. A peer that
    /// reconnects afterwards gets a normal fresh logon into the new mission.
    /// </summary>
    private void DropParkedShipsForMission(Mission mission)
    {
        if (_parkedShips.Count == 0) return;
        List<ParkedPeer>? doomed = null;
        foreach (var parked in _parkedShips.Values)
            if (ReferenceEquals(parked.Live.Ship.Mission, mission))
                (doomed ??= new List<ParkedPeer>()).Add(parked);
        if (doomed is null) return;
        foreach (var parked in doomed)
        {
            _parkedShips.Remove(ParkedKey(parked.Live.Ship.Mission!.Cookie, parked.Name));
            Interlocked.Increment(ref _reconnectExpiredTotal);
            ReleaseParkedShipScopedCaches(parked.Live.Ship.Id);
            Console.WriteLine($"[reconnect] '{ConsoleLog.Sanitize(parked.Name)}' grace ended by game over — "
                + $"mission rotated away; parked ship {parked.Live.Ship.Id} discarded with the old world");
        }
    }

    /// <summary>P813c — free the ship-id-scoped latches a parked ship leaves
    /// behind when its world is discarded (the same ship-subject dimension
    /// <see cref="ReleasePeerWithReason"/> frees for a normal departure).</summary>
    private void ReleaseParkedShipScopedCaches(short shipId)
    {
        _teammateExportSent.RemoveWhere(e => e.ShipId == shipId);
        _playerInfoSent.RemoveWhere(e => e.ShipId == shipId);
        _crossSideExportSent.RemoveWhere(e => e.ShipId == shipId);
        _sameSideNpcExportSent.RemoveWhere(e => e.ShipId == shipId);
        foreach (var k in _lastSameSideNpcStatusTick.Keys.Where(k => k.ShipId == shipId).ToList())
            _lastSameSideNpcStatusTick.Remove(k);
        foreach (var k in _teammateLoadoutVersionSent.Keys.Where(k => k.ShipId == shipId).ToList())
            _teammateLoadoutVersionSent.Remove(k);
        _teammateLoadoutCache.Remove(shipId);
        foreach (var k in _lastTeammateStatusTick.Keys.Where(k => k.Item2 == shipId).ToList())
            _lastTeammateStatusTick.Remove(k);
        _statusSweepRemovalVersion++;
        foreach (var k in _crossSideShipStatusTick.Keys.Where(k => k.Item2 == shipId).ToList())
            _crossSideShipStatusTick.Remove(k);
        foreach (var k in _crossSideShipStatusSeen.Keys.Where(k => k.ShipId == shipId).ToList())
            _crossSideShipStatusSeen.Remove(k);
        _shipDetected.Remove(shipId);
    }

    /// <summary>P813c — the windowed telemetry line the rig recipe reads
    /// (<c>[reconnect-summary]</c>, one line per <c>DisconnectSummaryTicks</c>).
    /// Bounded: one line per window regardless of how many peers reconnect.</summary>
    private void EmitReconnectSummary()
    {
        if (!_reconnectGraceEnabled) return;
        Console.WriteLine($"[reconnect-summary] window: parkedTotal={ReconnectParkedTotal} resumedTotal={ReconnectResumedTotal} "
            + $"expiredTotal={ReconnectExpiredTotal} refusedTotal={ReconnectRefusedTotal} currentlyParked={_parkedShips.Count}");
    }
}
