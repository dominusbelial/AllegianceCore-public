using Allegiance.Core.Sim;
using Allegiance.Core.Model;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    // FedSrv/FedSrv.CPP:489-515: NA means first; unknown and Over refuse on the wire.
    private Mission? ResolveJoinMission(uint cookie) => cookie == uint.MaxValue
        ? _missions.FirstOrDefault() : _missions.FirstOrDefault(m => m.Cookie == cookie);

    /// <summary>
    /// P970 — the mission-join gate. Native's own order is mission lookup → mission
    /// over → <b>password</b> → (later, in AddPlayerToSide) rank/lives/bans
    /// (<c>FedSrv/FedSrv.CPP:489-515</c>, <c>:648-661</c>), so a wrong password is
    /// refused at the join itself — before the player ever picks a side — with
    /// <c>DELPOSITIONREQ.reason = DPR_BadPassword</c> and <c>iSide = NA</c>.
    /// <para>The compare stays case-sensitive, exactly like native's
    /// <c>Strcmp</c> (<c>FedSrv/FedSrv.CPP:650</c> → <c>_Utility/Utility.h:1389-1392</c>
    /// <c>lstrcmpA</c>) — this port's <see cref="StringComparison.Ordinal"/>.</para>
    /// <para>P970 also logs the refusal: one line per refusal with the peer id and
    /// callsign, <b>never the attempted or the real password</b>, rate-limited per
    /// CALLSIGN (a retrying player reconnects, so a per-connection gate would not
    /// stop the flood) — see <see cref="ReportPasswordRefusal"/>.</para>
    /// </summary>
    /// <param name="callsign">The name to log; the caller may already hold it (the
    /// side-select path removes the pending entry before this check).</param>
    private bool JoinMission(ConnectionId peer, uint cookie, short shipId, out Mission? mission, string? password = null,
        string? callsign = null)
    {
        mission = ResolveJoinMission(cookie);
        var reason = mission is null ? DelPositionReqReason.NoMission
            : mission.Stage == MissionStage.Over ? DelPositionReqReason.ServerPaused : (DelPositionReqReason?)null;
        // Native password gate follows mission lookup (FedSrv.CPP:648-661;
        // _Utility/Utility.h:1389-1392 uses case-sensitive lstrcmpA).
        if (reason is null && !string.IsNullOrEmpty(ParamsOf(mission!).StrGamePassword)
            && !string.Equals(ParamsOf(mission!).StrGamePassword, password ?? "", StringComparison.Ordinal))
        {
            reason = DelPositionReqReason.BadPassword;
            ReportPasswordRefusal(peer, callsign);
        }
        if (reason is null) return true;
        SendPeer(peer, FedPacket.Pack([new DelPositionReq
        { ShipId = shipId, SideId = -1, Reason = reason.Value }.Encode()]));
        return false;
    }

    /// <summary>
    /// P970 — one rate-limited log line per bad-password refusal, with the peer id
    /// and callsign. The password (attempted or configured) is deliberately NOT
    /// printed: the operator needs to know a player was refused, not what they
    /// typed. Repeats are counted and summarized instead of flooding.
    /// </summary>
    private void ReportPasswordRefusal(ConnectionId peer, string? callsign)
    {
        string name = callsign ?? PeerCallsign(peer) ?? "";
        // Keyed on the CALLSIGN, not the connection id: a player retrying a bad
        // password reconnects each time, so a per-connection gate would emit one
        // line per attempt and flood exactly the case this rule exists for.
        var ticket = _joinRefusalLog.Ticket(RefusalLogKey(peer, name));
        if (ticket.Emit)
            Console.WriteLine($"[join] {peer} '{ConsoleText(name)}' refused: game password incorrect "
                + "(DPR_BadPassword; native FedSrv.CPP:648-661)");
        else if (ticket.Summarize)
            Console.WriteLine($"[join] {peer}: {ticket.Suppressed} more bad-password refusals suppressed (rate-limited)");
    }

    /// <summary>Stable (process-lifetime) log-gate key for a refusal: the callsign's
    /// FNV-1a hash, or the peer id when the name is unknown.</summary>
    private static long RefusalLogKey(ConnectionId peer, string name)
    {
        if (string.IsNullOrEmpty(name)) return peer.Value;
        unchecked
        {
            long hash = 1469598103934665603L;   // FNV-1a 64 offset basis
            foreach (char c in name) { hash ^= c; hash *= 1099511628211L; }
            // Keep it out of the ConnectionId range so the two key species cannot
            // collide in the table.
            return hash | long.MinValue;
        }
    }

    /// <summary>The name a peer is known by before it is seated (pending side-select
    /// request) or after (<see cref="_peerNames"/>). Empty when unknown.</summary>
    private string? PeerCallsign(ConnectionId peer)
        => _pendingSideSelect.TryGetValue(peer, out var pending) ? pending.Request.CharacterName
        : _peerNames.TryGetValue(peer, out var name) ? name : null;

    private Mission? ConnectionMission(ConnectionId peer) =>
        _peerShips.TryGetValue(peer, out var live) ? live.Ship.Mission
        : _pendingSideSelect.TryGetValue(peer, out var pending) ? pending.Mission : null;
}
