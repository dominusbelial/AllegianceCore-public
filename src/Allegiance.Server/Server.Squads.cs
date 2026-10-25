using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>
/// P820b/P820c — squads-in-games: the side's squad, derived from its leader's
/// preferred squad and relayed to the client via native <c>SET_TEAM_INFO(15)</c>,
/// plus the native rules that hang off it — leader succession preferring a
/// squad-capable teammate (<c>GetCanLeadSquad</c>, fsship.cpp:1374-1390) and
/// <c>SetSideSquad</c>'s boot-non-members (fsmission.cpp:1612-1641). Squads
/// reach the game server through the lobby identity path: the lobby stamps the
/// player's active squad memberships into the signed join token
/// (<see cref="JoinIdentity.Squads"/>), the server stores that
/// <see cref="JoinIdentity"/> per peer (<see cref="Server.Identity"/>), and
/// the helpers in <c>Server.Identity.cs</c> read them.
///
/// <para><b>P820c gate — <see cref="Mission.SquadGame"/>.</b> Native sets a
/// side's squad ONLY when the mission is a squad game
/// (<c>AddPlayerToSide</c>, fsmission.cpp:661-690: <c>if (bSquadGame)</c> →
/// <see cref="SetSideSquad(sideid, GetPreferredSquadToLead())"/>; else
/// <c>SetSideName</c> reset), clears every side's squad when bSquadGame turns
/// off (fsmission.cpp:1889-1893), and a client's SET_TEAM_INFO is honoured only
/// in a squad game (FedSrv.CPP:7451-7463). Non-squad games have <b>no</b> side
/// squad and <b>no</b> squad boots — the pre-P820b behaviour, restored here by
/// gating every squad set + boot on <c>mission.SquadGame</c>.</para>
///
/// <para><b>P820c — call sites mirror native.</b> The squad is established only
/// when a TEAM LEADER JOINS an empty side (the port's <c>TryJoinSideCore</c>
/// new-leader block, mirroring native <c>AddPlayerToSide</c> fsmission.cpp:
/// 661-690), cleared to NA only when a side empties before the start
/// (fsmission.cpp:1008-1009), and <b>kept</b> on succession (native picks a
/// successor who can lead the existing squad, fsmission.cpp:963-990, and never
/// re-derives it). P820b's re-derive-on-every-leadership-change hooks
/// (<c>BroadcastSetTeamLeader</c> and the rotation reseat) are removed — a
/// mid-game succession can no longer change the squad or boot players during a
/// started match.</para>
///
/// <para><b>P820c — real quit-side boots.</b> Native <c>SetSideSquad</c> boots
/// each non-member via <c>RemovePlayerFromSide(pfsPlayer, QSR_SquadChange)</c>
/// (fsmission.cpp:1623-1641). The port routes the boot through its real
/// remove-from-side path — <see cref="Server.MoveToTeamLobby"/> (leader
/// succession + SIDE_TEAMLOBBY park + whole-mission <c>QUIT_SIDE(126)</c>), so
/// every client sees the departure exactly as native's client does
/// (clintlib/appmsg.cpp:2993-3000, which clears the roster row and posts the
/// "booted because of a squad change" line). P820b's silent
/// <c>Ship.SideId = -2</c> write is gone.</para>
///
/// <para><b>Scope boundary (P820b).</b> Only a VERIFIED peer's squads matter.
/// Bots and anonymous players have no squads and no behaviour change — a side's
/// squad is derived solely from a verified leader's preferred squad, and
/// boot-non-members only ever detaches a verified peer who demonstrably is not
/// on the side's squad.</para>
/// </summary>
public sealed partial class Server
{
    /// <summary>Native <c>SIDE_TEAMLOBBY</c> (<c>igc.h:514</c>; also
    /// <c>Side.ArtifactSentinelFlag</c>).</summary>
    private const short SideTeamLobby = -2;

    /// <summary>Native's "no squad" sentinel (<c>NA</c>, igc.h:471; a
    /// <c>SquadID</c> of <c>NA</c> means the side has no squad).</summary>
    private const int SquadNa = -1;

    /// <summary>
    /// P820c — establish a side's squad from a newly-joined team leader, the
    /// port of native <c>AddPlayerToSide</c>'s team-leader squad rule
    /// (fsmission.cpp:661-690): in a <b>squad game</b> the side's squad is its
    /// leader's preferred squad (<c>GetPreferredSquadToLead</c>, fsship.cpp:
    /// 1397+); in a <b>non-squad game</b> the side has no squad (the
    /// <c>else</c> <c>SetSideName</c> reset). Called from
    /// <c>TryJoinSideCore</c> when the joining player becomes the side's first
    /// leader (the port's <c>AddPlayerToSide</c>). Succession does <b>not</b>
    /// call this — native keeps the squad there.
    /// </summary>
    private void EstablishSideSquadFromLeader(Mission mission, Side side)
    {
        if (!mission.SquadGame)
        {
            // Non-squad game: no side squad, no boots (native else-branch
            // SetSideName reset, fsmission.cpp:688-689).
            ApplySideSquad(mission, side, null, null);
            return;
        }
        (string? Id, string? Name)? preferred = null;
        if (side.LeaderShipId is { } leaderShipId)
        {
            foreach (var kv in _peerShips)
            {
                if (kv.Value.Ship.Id == leaderShipId && ReferenceEquals(kv.Value.Ship.Mission, mission))
                {
                    preferred = PeerPreferredSquad(kv.Key);
                    break;
                }
            }
        }
        ApplySideSquad(mission, side, preferred?.Id, preferred?.Name);
    }

    /// <summary>
    /// P820b/P820c — native <c>CFSMission::SetSideSquad</c>
    /// (fsmission.cpp:1612-1641): set the side's squad and, when it changes,
    /// boot (move to the team lobby) any seated VERIFIED player who is not a
    /// member of that squad — routed through the real remove-from-side path
    /// (<see cref="MoveToTeamLobby"/>, <c>RemovePlayerFromSide(..., QSR_
    /// SquadChange)</c>), then relay the new squad whole-mission via native
    /// <c>SET_TEAM_INFO(15)</c>. Guarded: only in a <b>squad game</b> are players
    /// ever booted (P820c gate); only VERIFIED peers are detached (bots/anonymous
    /// are untouched — P820b scope); and only when the side actually has a
    /// squad. A <c>null</c> squad clears the side's squad with no boots.
    /// </summary>
    private void ApplySideSquad(Mission mission, Side side, string? squadId, string? squadName)
    {
        bool changed = !string.Equals(side.SquadId, squadId, StringComparison.Ordinal);
        side.SquadId = squadId;
        side.SquadName = squadName;
        if (!changed) return;
        if (mission.SquadGame && squadId is not null)
        {
            // fsmission.cpp:1623-1641 — boot seated players not on the squad.
            // Only verified peers with a non-empty squad list can be judged
            // non-members; anonymous/bots keep their seat (no squad ⇒ no
            // behaviour change). Snapshot first: the boot mutates state.
            foreach (var kv in _peerShips.ToList())
            {
                if (!ReferenceEquals(kv.Value.Ship.Mission, mission)) continue;
                if (kv.Value.Ship.SideId != side.Id) continue;
                var squads = PeerSquads(kv.Key);
                if (squads is null || squads.Count == 0) continue;              // anonymous/bot — leave seated
                if (squads.Any(s => string.Equals(s.Id, squadId, StringComparison.Ordinal))) continue;  // member
                MoveToTeamLobby(mission, kv.Value.Ship, QuitSideReason.SquadChange);  // not a member — real quit-side
            }
        }
        RelaySideSquad(mission, side);
    }

    /// <summary>P820c — native <c>SetSideSquad(sid, NA)</c> (fsmission.cpp:
    /// 1612, with squadID == NA → no boots, then SetSideName): clear a side's
    /// squad, broadcasting the resulting <c>SET_TEAM_INFO</c>. Used when a side
    /// empties before the start (fsmission.cpp:1008-1009) and to force-clear a
    /// non-squad side. No boots (native skips them for NA).</summary>
    private void ClearSideSquad(Mission mission, Side side) =>
        ApplySideSquad(mission, side, null, null);

    /// <summary>P820b — native leader-succession preference (fsmission.cpp:963-990):
    /// among the remaining players (after the donation-winner step, which the
    /// caller handles), PREFER one who can lead the side's current squad
    /// (<c>GetCanLeadSquad</c>), else fall back to <paramref name="fallback"/>
    /// (the deterministic first other player). Returns the preferred candidate,
    /// or <paramref name="fallback"/> when none can lead. The squad is <b>kept</b>
    /// across succession, exactly as native does.</summary>
    private Ship PreferSquadCapableLeader(IEnumerable<Ship> remaining, Side side, Ship fallback)
    {
        if (side.SquadId is null) return fallback;
        foreach (var s in remaining)
        {
            foreach (var kv in _peerShips)
            {
                if (!ReferenceEquals(kv.Value.Ship, s)) continue;
                if (PeerCanLeadSquad(kv.Key, side.SquadId)) return s;
            }
        }
        return fallback;
    }

    /// <summary>
    /// P820c — relay a side's squad to the whole mission on native
    /// <c>SET_TEAM_INFO(15)</c> (native <c>SetSideName</c> → <c>SET_TEAM_INFO</c>,
    /// fsmission.cpp:1580-1590). Wire layout is native's exact field shape
    /// (int squadID, short sideID, char[25] SideName; see <see cref="SetTeamInfo"/>);
    /// the <c>squadID</c> is an opaque stable int derived from the port's string
    /// squad guid, and — following native's non-lock squad-game rule
    /// (<c>SetSideSquad</c> → <c>SetSideName(sid, szSquadName)</c>,
    /// fsmission.cpp:1666-1667) — <c>SideName</c> carries the squad name when the
    /// side has one, else the real side name. Null <paramref name="to"/> =
    /// whole-mission fan-out.
    /// </summary>
    private void RelaySideSquad(Mission mission, Side side, ConnectionId? to = null)
    {
        var msg = new SetTeamInfo
        {
            SquadId = SquadWireId(side.SquadId),
            SideId = side.Id,
            SideName = side.SquadId is not null ? (side.SquadName ?? "") : (side.Name ?? ""),
        };
        var packet = FedPacket.Pack(new[] { msg.Encode() });
        if (to is { } single)
        {
            SendPeer(single, packet);
            Console.WriteLine($"[teaminfo] -> peer {single} SET_TEAM_INFO(15): side {side.Id} squad={(side.SquadId is null ? "NA" : $"'{side.SquadName}'")}");
            return;
        }
        int n = SendToMissionPeers(mission, packet);
        Console.WriteLine($"[teaminfo] side {side.Id} squad={(side.SquadId is null ? "NA" : $"'{side.SquadName}'")}: SET_TEAM_INFO(15) whole-mission to {n} peer(s)");
    }

    /// <summary>P820c — map the port's string squad guid to the opaque int the
    /// native <c>SquadID</c> wire field carries. NA(-1) when the side has no
    /// squad. A stable FNV-1a hash: deterministic across server instances, and
    /// the client only ever tests it against NA (it pairs it with the squad name
    /// carried in SET_TEAM_INFO's SideName field), so the exact value is opaque.</summary>
    private static int SquadWireId(string? squadId)
    {
        if (squadId is null) return SquadNa;
        unchecked
        {
            int h = unchecked((int)2166136261u);   // FNV-1a 32-bit offset basis (as a two's-complement int)
            foreach (char c in squadId) { h ^= c; h *= 16777619; }
            return h;
        }
    }
}
