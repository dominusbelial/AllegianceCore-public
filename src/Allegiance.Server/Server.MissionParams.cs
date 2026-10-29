using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>
/// P853 — live host editing: the server half of
/// <c>FM_CS_MISSIONPARAMS(113)</c>, <c>FM_CS_SET_TEAM_INFO(15)</c> and the
/// shared <c>SetSideName</c> primitive.
///
/// <para><b>Native contracts (FedSrv/FedSrv.CPP, FedSrv/fsmission.cpp).</b>
/// <list type="bullet">
/// <item><c>FM_CS_MISSIONPARAMS</c> (<c>FedSrv.CPP:7518-7597</c>): owner-only
/// (<c>GetOwner() != pfsPlayer → break</c>), validated with
/// <c>TrapHackBoot(missionparams.Invalid(true) == NULL)</c> (<c>igc.h:1339</c>,
/// ported on <see cref="MissionParamsMsg.Invalid"/>), the server overrides
/// <c>bObjectModelCreated</c> from the mission's own def (:7536-7537), the
/// standalone club check (<c>TrapHackBoot(bClubGame == false)</c>, :7541),
/// the two player-immutable equality traps (bLockGameOpen :7545-7546,
/// nTotalMaxPlayersPerGame :7547-7548), the lockGameOpen team-size rule
/// (:7550-7557), the <c>STAGE_NOTSTARTED</c> gate (:7560 — a started mission
/// silently ignores the change), then <c>SetMissionParams</c>, a whole-mission
/// <c>MISSIONDEF(38)</c> forward (:7577) and the lobby advertisement refresh
/// (<c>SetLobbyIsDirty</c>, :7579).</item>
/// <item><c>CFSMission::SetMissionParams</c> (<c>fsmission.cpp:1796-1895</c>):
/// IGC-static fields preserved (:1803-1806), club flag forced (:1809-1817),
/// team-count shrink boots each destroyed side's players with
/// <c>QSR_SideDestroyed</c> (:1821-1835), a squad-game flip maintains or clears
/// every side's squad (:1885-1893), the IGC mission is re-told the params
/// (:1842), grow creates the new sides (<c>InitSide</c>, :1846-1852),
/// alliances revalidated (<c>UpdateAlliances(NA, NA)</c>, :1861), every side's
/// ready state re-checked (:1864-1869) and rank-window violations are removed
/// from the mission (<c>QSR_RankLimits</c>, :1871-1890).</item>
/// <item><c>FM_CS_SET_TEAM_INFO</c> (<c>FedSrv.CPP:7430-7467</c>): only while
/// <c>STAGE_NOTSTARTED &amp;&amp; !bLockTeamSettings</c> (:7433-7436, otherwise
/// silently ignored), the four TrapHackBoots (sideID ≥ 0 :7439, sideID &lt;
/// c_cSidesMax :7440, SideName NUL-terminated inside c_cbName :7441, sender is
/// a member of the named squad :7442 — <c>GetIsMemberOfSquad(NA)</c> is
/// <b>true</b>, <c>fsship.cpp:1359-1363</c>), the team-leadership race check
/// (:7445-7448: side exists, sender is its leader and is seated on it), then
/// the squad-game / non-squad split (:7451-7463): a squad game re-points the
/// side's squad (leader-or-current-squad trap, :7455), a non-squad game with
/// <c>squadID == NA</c> renames the side via <c>SetSideName</c> (:7461).</item>
/// <item><c>CFSMission::SetSideName</c> (<c>fsmission.cpp:1564-1600</c>): ONE
/// method used by every caller (config side names :689, squad names :1667,
/// invitation squads <c>fsside.cpp:78</c>, this handler :7461, admin
/// <c>AdminGame.cpp:303</c>): truncate to c_cbName-1 = 24 chars (:1569-1572),
/// rename the IGC side, broadcast <c>SET_TEAM_INFO(15)</c> with the side's
/// current squad id and the new name to the whole mission (:1575-1590).</item>
/// </list></para>
/// </summary>
public sealed partial class Server
{
    /// <summary>Native <c>c_cSidesMax = 6</c> (<c>Igc/igc.h:507</c>).</summary>
    private const int CSidesMax = 6;

    /// <summary>Native <c>c_cbName = 25</c> (<c>Utility.h:60,64</c>, the
    /// SET_TEAM_INFO SideName capacity); the rename truncation keeps 24.</summary>
    private const int CbName = SetTeamInfo.SideNameCapacity;

    /// <summary>P853 — the current <c>m_misdef.misparms</c> stand-in per live
    /// mission: the seed the mission was assembled from, mutated only by an
    /// accepted FM_CS_MISSIONPARAMS. The FM_CS_MISSIONPARAMS equality guards
    /// compare the client's echo against this (native compares against
    /// <c>GetMissionDef()-&gt;misparms</c>, FedSrv.CPP:7545-7548). Cleared
    /// implicitly with the mission (rotation re-assembles; the entry is
    /// dropped when a mission object is released by the periodic sweep —
    /// see <see cref="PruneMissionParams"/>).</summary>
    private readonly Dictionary<Mission, MissionParamsMsg> _missionParams = new();

    /// <summary>P853 — the mission's current params, seeded lazily from the
    /// game config + assembled mission exactly as native seeds
    /// <c>m_misdef.misparms</c> at construction (fsmission.cpp:217).</summary>
    private MissionParamsMsg ParamsOf(Mission mission)
    {
        if (_missionParams.TryGetValue(mission, out var seeded)) return seeded;
        var g = GameSettings(mission);
        seeded = new MissionParamsMsg
        {
            StrGameName = g.Name ?? "game",
            // P853b — the misdef stand-in carries the same IGC-static identity
            // native's m_misdef.misparms does (fsmission.cpp:1803-1806 copies it
            // from there). The advertisement now stamps this whole block into
            // MISSIONDEF, so an empty name here would blank the client's
            // static-core reload key (P29).
            SzIgcStaticFile = Path.GetFileNameWithoutExtension(_config.StaticCore),
            VerIgcCore = mission.Core.Version,
            Bits =
            {
                // The port's missions are created by the server app (native
                // bObjectModelCreated; LobbyGameParams.FromMission advertises
                // the same, LobbyHeartbeat.cs:548).
                [MissionParamsMsg.BitObjectModelCreated] = true,
                [MissionParamsMsg.BitSquadGame] = mission.SquadGame,
                [MissionParamsMsg.BitAllowEmptyTeams] = g.AllowEmptyTeams,
                [MissionParamsMsg.BitAllowFriendlyFire] = mission.AllowFriendlyFire,
                [MissionParamsMsg.BitAllowAlliedViz] = mission.AllowAlliedViz,
                [MissionParamsMsg.BitAllowAlliedRip] = mission.AllowAlliedRip,
                [MissionParamsMsg.BitAllowDevelopments] = mission.AllowDevelopments,
                [MissionParamsMsg.BitScoresCount] = true,   // LobbyHeartbeat.cs:551 — native Reset default
            },
            NTems = (short)mission.Sides.Count,
            IMinRank = -1,                      // igc.h:1301 Reset default — the port has no rank window
            IMaxRank = 1000,                    // igc.h:1302
            NMinPlayersPerTeam = (byte)Math.Clamp(g.MinPlayersPerTeam, 0, 255),
            NMaxPlayersPerTeam = (byte)Math.Clamp(g.MaxPlayersPerTeam, 0, 255),
            IMaxImbalance = (short)g.MaxTeamImbalance,
            ILives = mission.Lives,
            FGoalTeamMoney = mission.Goals.TeamMoney,
            DtGameLength = mission.Goals.GameLengthSeconds,
            IGoalConquestPercentage = mission.Goals.ConquestPercentage,
            IGoalTerritoryPercentage = mission.Goals.TerritoryPercentage,
            // Native Reset default (igc.h:1316) and the value MISSIONDEF has
            // always advertised at params +184; the port derives no behaviour
            // from it, so keep it truthful instead of letting the overlay zero it.
            IGoalArtifactsPercentage = 100,
            NGoalFlagsCount = mission.Goals.Flags,
            NGoalArtifactsCount = mission.Goals.Artifacts,
            NGoalTeamKills = mission.Goals.TeamKills,
        };
        for (int i = 0; i < mission.Sides.Count && i < MissionParamsMsg.CSidesMax; i++)
            seeded.RgCivId[i] = mission.Sides[i].Civilization.CivilizationId;
        _missionParams[mission] = seeded;
        return seeded;
    }

    /// <summary>P853 — drop the params snapshot of a mission that no longer
    /// exists (rotation). Cheap on the sim thread: the dictionary holds one
    /// entry per live mission.</summary>
    private void PruneMissionParams()
    {
        if (_missionParams.Count == 0) return;
        foreach (var key in _missionParams.Keys.Where(m => !_missions.Contains(m)).ToList())
            _missionParams.Remove(key);
    }

    /// <summary>P853 — inbound <c>FM_CS_MISSIONPARAMS(113)</c>, the port of
    /// <c>FedSrv.CPP:7518-7597</c>. Every guard runs in native order; each
    /// TrapHackBoot uses the port's <see cref="ReportTrapHack"/> path.</summary>
    private void HandleMissionParams(ConnectionId from, MissionParamsMsg msg)
    {
        if (!_peerShips.TryGetValue(from, out var live))
        {
            Console.WriteLine($"[missionparams] {from}: MISSIONPARAMS from a peer with no ship — ignored");
            return;
        }
        if (live.Ship.Mission is not Mission mission)
        {
            Console.WriteLine($"[missionparams] {from} ship {live.Ship.Id}: no mission — MISSIONPARAMS ignored");
            return;
        }

        // :7529-7531 — the player better be the mission owner.
        if (!IsMissionOwnerPlayer(live.Ship, mission))
        {
            Console.WriteLine($"[missionparams] ship {live.Ship.Id}: not the mission owner — FM_CS_MISSIONPARAMS refused");
            return;
        }

        // :7534 — TrapHackBoot(missionparams.Invalid(true) == NULL).
        if (msg.Invalid(bFromClient: true) is { } why)
        {
            Console.WriteLine($"[missionparams] ship {live.Ship.Id}: invalid params '{why}'");
            ReportTrapHack(TrapHackKind.Boot, from, live.Ship.Id,
                "missionparams.Invalid(true) == NULL", "FedSrv.CPP:7534");
            return;
        }

        // :7536-7537 — the server overrides bObjectModelCreated from the
        // mission's own def; every port mission is server-created (true).
        msg[MissionParamsMsg.BitObjectModelCreated] = true;

        // :7540-7541 — standalone server: never a club game.
        if (msg[MissionParamsMsg.BitClubGame])
        {
            ReportTrapHack(TrapHackKind.Boot, from, live.Ship.Id,
                "missionparams.bClubGame == false", "FedSrv.CPP:7541");
            return;
        }

        var cur = ParamsOf(mission);

        // :7543-7546 — players can't change the game-open lock.
        if (msg[MissionParamsMsg.BitLockGameOpen] != cur[MissionParamsMsg.BitLockGameOpen])
        {
            ReportTrapHack(TrapHackKind.Boot, from, live.Ship.Id,
                "bLockGameOpen == GetMissionDef()->misparms.bLockGameOpen", "FedSrv.CPP:7545");
            return;
        }

        // :7547-7548 — nor the game-wide player cap.
        if (msg.NTotalMaxPlayersPerGame != cur.NTotalMaxPlayersPerGame)
        {
            ReportTrapHack(TrapHackKind.Boot, from, live.Ship.Id,
                "nTotalMaxPlayersPerGame == GetMissionDef()->misparms.nTotalMaxPlayersPerGame", "FedSrv.CPP:7547");
            return;
        }

        // :7550-7557 — a locked-open game can only shrink teams to the
        // per-team cap the total allows (Imago 7/10, default games only).
        if (cur[MissionParamsMsg.BitLockGameOpen] && !msg[MissionParamsMsg.BitObjectModelCreated]
            && msg.NMaxPlayersPerTeam != cur.NMaxPlayersPerTeam
            && msg.NMaxPlayersPerTeam < msg.NTotalMaxPlayersPerGame / Math.Max((short)1, msg.NTems))
        {
            ReportTrapHack(TrapHackKind.Boot, from, live.Ship.Id,
                "nMaxPlayersPerTeam >= nTotalMaxPlayersPerGame / nTeams (locked game)", "FedSrv.CPP:7551-7556");
            return;
        }

        // :7560 — only a not-started mission changes; otherwise silently
        // ignore the change (native's REVIEW note).
        if (mission.Stage != MissionStage.NotStarted)
        {
            Console.WriteLine($"[missionparams] ship {live.Ship.Id}: mission stage {mission.Stage} != STAGE_NOTSTARTED — change silently ignored (FedSrv.CPP:7560,7593-7595)");
            return;
        }

        SetMissionParams(mission, msg, cur);

        // :7577 — tell the people in the game about the new settings (the
        // whole MissionDef, exactly what native forwards).
        SendMissionDef(mission);
        // :7579 — SetLobbyIsDirty: the next throttled heartbeat republishes the
        // lobby advertisement (P866 — a real dirty mark now, not just the
        // per-tick snapshot refresh).
        MarkLobbyDirty("missionparams");
        Console.WriteLine($"[missionparams] ship {live.Ship.Id}: mission params updated (nTeams {cur.NTems}, maxPerTeam {cur.NMaxPlayersPerTeam}, rank {cur.IMinRank}..{cur.IMaxRank}) — MISSIONDEF(38) whole-mission + lobby refresh");
    }

    /// <summary>The native side-name array (<c>FedSrv/fsmission.cpp:23-32</c>).</summary>
    private static readonly string[] NativeSideNames =
        ["Team 1", "Team 2", "Team 3", "Team 4", "Team 5", "Team 6"];

    /// <summary>P871 / MISSIONPARAMS — native <c>CFSMission::InitSide</c>
    /// (<c>FedSrv/fsmission.cpp:33-62</c>) for a LIVE growth: the slot's
    /// <c>rgCivID</c> (defaulting to an unused civilization when the params carry
    /// NA), the native side name and the slot palette. Returns null when the core
    /// has no civilization to back the side.</summary>
    private static Side? InitSide(Mission mission, short sideId, MissionParamsMsg msg)
    {
        short civId = sideId < msg.RgCivId.Length ? msg.RgCivId[sideId] : (short)-1;
        if (civId < 0 || !mission.Core.Civilizations.ContainsKey(civId))
        {
            civId = mission.Core.Civilizations.Keys
                .FirstOrDefault(id => mission.Sides.All(s => s.Civilization.CivilizationId != id), (short)-1);
            if (civId < 0) civId = mission.Core.Civilizations.Keys.FirstOrDefault();
        }
        if (!mission.Core.Civilizations.TryGetValue(civId, out var civ)) return null;
        if (sideId < msg.RgCivId.Length) msg.RgCivId[sideId] = civId;
        string name = sideId >= 0 && sideId < NativeSideNames.Length ? NativeSideNames[sideId] : $"Team {sideId + 1}";
        return mission.AddSide(sideId, name, civ);
    }

    /// <summary>P853 — the port of <c>CFSMission::SetMissionParams</c>
    /// (<c>fsmission.cpp:1796-1895</c>). <paramref name="cur"/> is the
    /// mission's previous params (read-only here); <paramref name="msg"/> is
    /// mutated into the new <c>m_misdef.misparms</c>.</summary>
    private void SetMissionParams(Mission mission, MissionParamsMsg msg, MissionParamsMsg cur)
    {
        // :1803-1806 — don't even think about changing the IGC static stuff.
        msg.VerIgcCore = cur.VerIgcCore;
        msg.SzIgcStaticFile = cur.SzIgcStaticFile;
        // :1809-1817 — the standalone server forces the club flag off.
        msg[MissionParamsMsg.BitClubGame] = false;
        // FedSrv.CPP:7562-7572 (KGJV #114) — every port mission is a
        // bObjectModelCreated game, so the client may not change the game
        // name or password.
        msg.StrGameName = cur.StrGameName;
        msg.StrGamePassword = cur.StrGamePassword;

        // :1821-1835 — team-count shrink: kick everyone off each extra team
        // (RemovePlayerFromSide QSR_SideDestroyed) and deactivate the side.
        // Native's UpdateSides destroys the IGC side; this port keeps the
        // Side object but marks it inactive (the P455 elimination state) and
        // broadcasts SIDE_INACTIVE(68) so every client drops the row.
        short oldTeams = cur.NTems;
        short newTeams = msg.NTems;
        if (newTeams > mission.Sides.Count)
        {
            // :1846-1852 — grow the mission with InitSide, then
            // CmissionIGC::UpdateSides (missionigc.cpp:3749-3779) creates the IGC
            // side from the params' rgCivID[slot], the native side-name array and
            // the per-slot palette. P871 closes the former clamp: the port now
            // appends the side to the live mission (Mission.AddSide) and
            // re-publishes the roster.
            for (short sideId = (short)mission.Sides.Count; sideId < newTeams; sideId++)
            {
                if (InitSide(mission, sideId, msg) is not { } created)
                {
                    Console.WriteLine($"[missionparams] side {sideId} could not be created (no civilization) — nTeams clamped to {sideId}");
                    msg.NTems = newTeams = sideId;
                    break;
                }
                var active = new SideInactive { SideId = sideId, BActive = true, BChangeAet = false, BAet = false };
                SendToMissionPeers(mission, FedPacket.Pack([active.Encode()]));
                Console.WriteLine($"[missionparams] side {sideId} '{created.Name}' created live (InitSide, civ {created.Civilization.CivilizationId}) -> SIDE_INACTIVE(68) bActive");
            }
            if (newTeams > oldTeams) BroadcastSideRoster(mission);
        }
        for (short sideId = (short)(mission.Sides.Count - 1); sideId >= newTeams; sideId--)
        {
            var destroyed = mission.Sides.FirstOrDefault(s => s.Id == sideId);
            if (destroyed is null) continue;
            foreach (var kv in _peerShips.Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission)
                                                      && kv.Value.Ship.SideId == sideId).ToList())
                MoveToTeamLobby(mission, kv.Value.Ship, QuitSideReason.SideDestroyed);
            destroyed.Active = false;    // fsmission.cpp:5325 (the elimination state)
            destroyed.Ready = false;
            var si = new SideInactive { SideId = sideId, BActive = false, BChangeAet = false, BAet = false };
            SendToMissionPeers(mission, FedPacket.Pack([si.Encode()]));
            Console.WriteLine($"[missionparams] side {sideId} destroyed by nTeams shrink -> SIDE_INACTIVE(68)");
        }

        // :1837 — detect the squad-game flip for the tail maintenance.
        bool squadChange = cur[MissionParamsMsg.BitSquadGame] != msg[MissionParamsMsg.BitSquadGame];

        // :1840-1842 — adopt the new params as the mission's misdefs...
        _missionParams[mission] = msg;
        // ...and tell the IGC mission (the port's Mission carries the same
        // settings the lobby advertisement and briefing paths read).
        mission.Goals = new MissionGoals
        {
            ConquestPercentage = msg.IGoalConquestPercentage,
            TerritoryPercentage = msg.IGoalTerritoryPercentage,
            Flags = msg.NGoalFlagsCount,
            Artifacts = msg.NGoalArtifactsCount,
            TeamKills = msg.NGoalTeamKills,
            TeamMoney = msg.FGoalTeamMoney,
            GameLengthSeconds = msg.DtGameLength,
        };
        GameSettings(mission).MaxTeamImbalance = msg.IMaxImbalance;
        mission.Lives = msg.ILives;
        mission.SquadGame = msg[MissionParamsMsg.BitSquadGame];
        mission.AllowEmptyTeams = msg[MissionParamsMsg.BitAllowEmptyTeams];
        mission.AllowFriendlyFire = msg[MissionParamsMsg.BitAllowFriendlyFire];
        mission.AllowAlliedViz = msg[MissionParamsMsg.BitAllowAlliedViz];
        mission.AllowAlliedRip = msg[MissionParamsMsg.BitAllowAlliedRip];
        mission.AllowDevelopments = msg[MissionParamsMsg.BitAllowDevelopments];

        // :1846-1852 — grow InitSide: beyond the assembled count it is
        // unsupported (clamped above); within it, a side an earlier shrink
        // deactivated comes back (the port's stand-in for InitSide).
        for (short sideId = oldTeams; sideId < newTeams; sideId++)
        {
            var revived = mission.Sides.FirstOrDefault(s => s.Id == sideId);
            if (revived is null || revived.Active) continue;
            revived.Active = true;
            var si = new SideInactive { SideId = sideId, BActive = true, BChangeAet = false, BAet = false };
            SendToMissionPeers(mission, FedPacket.Pack([si.Encode()]));
            Console.WriteLine($"[missionparams] side {sideId} restored by nTeams growth -> SIDE_INACTIVE(68) bActive");
        }

        // :1861 — #ALLY: some teams may have been removed, revalidate.
        UpdateAlliances(mission, (short)-1, (short)-1);

        // :1864-1869 — see if that changed the ready state of anyone.
        foreach (var side in mission.Sides)
        {
            var seater = _peerShips.Values.FirstOrDefault(v => ReferenceEquals(v.Ship.Mission, mission) && v.Ship.SideId == side.Id);
            if (seater is not null) RecomputeSideReady(seater.Ship);
        }

        // :1871-1890 — boot any player who is no longer inside the rank
        // window (RemovePlayerFromMission QSR_RankLimits). DIVERGENCE: native
        // spares cheaters/privileged users (CanCheat / UTL::PrivilegedUser,
        // :1885-1886); the port has no per-peer cheat flag, so nobody is
        // exempt. The default window (-1..1000) boots nobody.
        if (msg.IMinRank > -1 || msg.IMaxRank < 1000)
        {
            foreach (var kv in _peerShips.Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission)).ToList())
            {
                int rank = kv.Value.PlayerScore.Rank;
                if (msg.IMinRank > rank || msg.IMaxRank < rank)
                {
                    Console.WriteLine($"[missionparams] ship {kv.Value.Ship.Id} rank {rank} outside [{msg.IMinRank},{msg.IMaxRank}] — removed from mission (QSR_RankLimits)");
                    ReleasePeerWithReason(kv.Key, QuitSideReason.RankLimits);
                }
            }
        }

        // :1885-1893 — the squad-game flip maintains (or clears) every side.
        if (squadChange)
        {
            for (short sideId = 0; sideId < newTeams; sideId++)
            {
                var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
                if (side is null) continue;
                if (msg[MissionParamsMsg.BitSquadGame]) MaintainSquadLeadership(mission, side);
                else ClearSideSquad(mission, side);
            }
        }
    }

    /// <summary>P853 — the <c>FM_S_MISSIONDEF(38)</c> re-advertisement native
    /// sends after an accepted params change (FedSrv.CPP:7577 forwards
    /// <c>GetMissionDef()</c>). Built from the mission + its current params
    /// snapshot, mirroring the join-burst MISSIONDEF (Server.Lobby.cs:4399)
    /// plus the P853 params-tail fields.</summary>
    private void SendMissionDef(Mission mission)
    {
        var cur = ParamsOf(mission);
        // Native setters update the cached def; these state fields live on
        // Mission here (FedSrv.CPP:5202/5219 forward the current whole def).
        cur[MissionParamsMsg.BitLockLobby] = mission.LockLobby;
        cur[MissionParamsMsg.BitLockSides] = mission.LockSides;
        cur[MissionParamsMsg.BitAllowEmptyTeams] = mission.AllowEmptyTeams;
        var md = new MissionDef
        {
            SzIGCStaticFile = Path.GetFileNameWithoutExtension(_config.StaticCore),
            VerIgcCore = mission.Core.Version,
            DwCookie = 0,
            NTems = cur.NTems,
            RgCivId = mission.Sides.Select(s => s.Civilization.CivilizationId).ToArray(),
            ISideMissionOwner = mission.MissionOwnerSideId ?? (short)-1,
            RgShipIdLeaders = mission.Sides.Select(s => s.LeaderShipId ?? (short)-1).ToArray(),
            RgcPlayers = mission.Sides.Select(s => (byte)Math.Min(255, _peerShips.Values.Count(p =>
                ReferenceEquals(p.Ship.Mission, mission) && p.Ship.SideId == s.Id))).ToArray(),
            RgfAutoAccept = mission.Sides.Select(s => s.AutoAccept).ToArray(),
            RgfReady = mission.Sides.Select(s => s.Ready).ToArray(),
            RgfForceReady = mission.Sides.Select(s => s.ForceReady).ToArray(),
            RgfActive = mission.Sides.Select(s => s.Active).ToArray(),
            RgfAllies = mission.Sides.Select(s => s.AllyGroup ?? (sbyte)-1).ToArray(),
            Stage = (int)mission.Stage,
            Goals = mission.Goals with { },
            AllowFriendlyFire = mission.AllowFriendlyFire,
            AllowAlliedViz = mission.AllowAlliedViz,
            ILives = mission.Lives,
            IMinRank = cur.IMinRank,
            IMaxRank = cur.IMaxRank,
            NMinPlayersPerTeam = cur.NMinPlayersPerTeam,
            NMaxPlayersPerTeam = cur.NMaxPlayersPerTeam,
            NTotalMaxPlayersPerGame = cur.NTotalMaxPlayersPerGame,
            // P853b — native forwards its whole m_misdef, misparms included
            // (FedSrv.CPP:7577), which is how the owner's client learns every
            // field of the live settings before it edits them again.
            Params = cur,
        };
        int n = SendToMissionPeers(mission, FedPacket.Pack([md.Encode()]));
        Console.WriteLine($"[missionparams] -> MISSIONDEF(38) nTeams {md.NTems} stage {md.Stage} whole-mission to {n} peer(s)");
    }

    /// <summary>P853 — inbound <c>FM_CS_SET_TEAM_INFO(15)</c> (the C→S half;
    /// the S→C relay was P820c), the port of <c>FedSrv.CPP:7430-7467</c>.</summary>
    private void HandleSetTeamInfo(ConnectionId from, SetTeamInfo msg)
    {
        if (!_peerShips.TryGetValue(from, out var live))
        {
            Console.WriteLine($"[teaminfo] {from}: SET_TEAM_INFO from a peer with no ship — ignored");
            return;
        }
        if (live.Ship.Mission is not Mission mission)
        {
            Console.WriteLine($"[teaminfo] {from} ship {live.Ship.Id}: no mission — SET_TEAM_INFO ignored");
            return;
        }

        // :7433-7436 — only a not-started mission with unlocked team settings
        // accepts the change; otherwise silently ignore it.
        if (mission.Stage != MissionStage.NotStarted || GameSettings(mission).LockTeamSettings)
        {
            Console.WriteLine($"[teaminfo] ship {live.Ship.Id}: stage {mission.Stage} lockTeamSettings={GameSettings(mission).LockTeamSettings} — SET_TEAM_INFO silently ignored (FedSrv.CPP:7433-7436)");
            return;
        }

        // :7439-7442 — the four TrapHackBoots.
        if (msg.SideId < 0)
        {
            ReportTrapHack(TrapHackKind.Boot, from, live.Ship.Id, "pfmSetTeamInfo->sideID >= 0", "FedSrv.CPP:7439");
            return;
        }
        if (msg.SideId >= CSidesMax)
        {
            ReportTrapHack(TrapHackKind.Boot, from, live.Ship.Id, "pfmSetTeamInfo->sideID < c_cSidesMax", "FedSrv.CPP:7440");
            return;
        }
        if (msg.SideName.Length >= CbName)
        {
            // The codec strips the terminator, so a decoded 25-char name means
            // native's memchr(SideName, 0, c_cbName) found no NUL.
            ReportTrapHack(TrapHackKind.Boot, from, live.Ship.Id, "memchr(pfmSetTeamInfo->SideName, 0, c_cbName)", "FedSrv.CPP:7441");
            return;
        }
        if (!PeerIsMemberOfSquad(from, msg.SquadId))
        {
            ReportTrapHack(TrapHackKind.Boot, from, live.Ship.Id, "pfsPlayer->GetIsMemberOfSquad(squadID)", "FedSrv.CPP:7442");
            return;
        }

        // :7445-7448 — the team-leadership race check: the named side exists,
        // the sender is its current leader and is seated on it.
        var side = mission.Sides.FirstOrDefault(s => s.Id == msg.SideId);
        if (side is null || side.LeaderShipId != live.Ship.Id || live.Ship.SideId != msg.SideId)
        {
            Console.WriteLine($"[teaminfo] ship {live.Ship.Id}: side {msg.SideId} race check failed (exists={side is not null}) — SET_TEAM_INFO ignored (FedSrv.CPP:7445-7448)");
            return;
        }

        if (mission.SquadGame)
        {
            // :7451-7459 — squad game: only a squad re-point; NA is a no-op.
            if (msg.SquadId == SquadNa) return;
            var squad = PeerSquads(from)?.FirstOrDefault(s => SquadWireId(s.Id) == msg.SquadId);
            if (squad is null
                || !(PeerCanLeadSquad(from, squad.Id)
                     || string.Equals(side.SquadId, squad.Id, StringComparison.Ordinal)))
            {
                ReportTrapHack(TrapHackKind.Boot, from, live.Ship.Id,
                    "GetCanLeadSquad(squadID) || GetSide()->GetSquadID() == squadID", "FedSrv.CPP:7455");
                return;
            }
            ApplySideSquad(mission, side, squad.Id, squad.Name);
        }
        else
        {
            // :7461-7462 — non-squad game: a NA squad id renames the side.
            if (msg.SquadId == SquadNa)
                SetSideName(mission, side, msg.SideName);
        }
    }

    /// <summary>P853 — the port of <c>CFSSide::GetIsMemberOfSquad</c>'s server
    /// question (<c>fsship.cpp:1359-1371</c>): NA is always a member; otherwise
    /// the sender's verified squad memberships must contain the wire id.</summary>
    private bool PeerIsMemberOfSquad(ConnectionId from, int squadId)
    {
        if (squadId == SquadNa) return true;   // fsship.cpp:1361-1363
        return PeerSquads(from)?.Any(s => SquadWireId(s.Id) == squadId) ?? false;
    }

    /// <summary>P853 / SIM-0508 — THE one side-rename primitive, the port of
    /// <c>CFSMission::SetSideName</c> (<c>fsmission.cpp:1564-1600</c>), used by
    /// the SET_TEAM_INFO handler, the invitation-squad load (SIM-0491) and any
    /// future admin path. Truncates to c_cbName-1 = 24 chars (:1569-1572),
    /// renames the side (<c>IsideIGC::SetName</c>) and broadcasts
    /// <c>SET_TEAM_INFO(15)</c> carrying the side's current squad id and the
    /// new name to the whole mission (:1575-1590).</summary>
    private void SetSideName(Mission mission, Side side, string name)
    {
        if (side.Id < 0 || side.Id >= CSidesMax) return;   // :1566 assert
        string trimmed = name.Length <= CbName - 1 ? name : name[..(CbName - 1)];
        side.SetName(trimmed);                             // :1568-1572 (m_misdef.rgszName + SetName)
        var relay = new SetTeamInfo
        {
            SideId = side.Id,
            SquadId = SquadWireId(side.SquadId),           // :1586 pside->GetSquadID()
            SideName = trimmed,                            // :1587 the new real name
        };
        int n = SendToMissionPeers(mission, FedPacket.Pack([relay.Encode()]));
        Console.WriteLine($"[teaminfo] side {side.Id} renamed '{trimmed}' (squad {side.SquadId ?? "NA"}): SET_TEAM_INFO(15) whole-mission to {n} peer(s) (fsmission.cpp:1575-1590)");
    }

    // ═══════════════════ P866 — lobby publication + the dirty flag ═══════════════════

    /// <summary>P866 / SIM-0572 — the port of native <c>CFSMission::m_fLobbyDirty</c>
    /// (<c>FedSrv/fsmission.cpp:90</c>, set by <c>SetLobbyIsDirty</c>,
    /// <c>fsmission.cpp:5624-5628</c>). Native's flag is a bool on the sim thread;
    /// this port's heartbeat runs off-thread, so the flag is a monotonic VERSION:
    /// every native <c>SetLobbyIsDirty()</c> call site bumps it, and the heartbeat
    /// publishes when the version it last published differs from the current one
    /// (a read/clear race can then never lose an update — the counter only grows).
    /// Initial value 1: the mission was constructed dirty
    /// (<c>fsmission.cpp:90</c> <c>m_fLobbyDirty(true)</c>, plus the
    /// <c>SetLobbyIsDirty()</c> at the end of construction, <c>:448</c>).</summary>
    private long _lobbyDirtyVersion = 1;

    /// <summary>P866 — the mission the last published advertisement described.
    /// A rotation/new mission identity bumps the dirty version, the port form of
    /// native creating a fresh <c>CFSMission</c> (dirty at construction).</summary>
    private Mission? _lobbySnapshotMission;

    /// <summary>P866 — the dirty flag the lobby heartbeat reads (never clears):
    /// monotonic counter, readable off the sim thread.</summary>
    public long LobbyDirtyVersion { get { lock (_statsLock) return _lobbyDirtyVersion; } }

    /// <summary>P866 — the port of <c>CFSMission::SetLobbyIsDirty</c>
    /// (<c>fsmission.cpp:5624-5628</c>): mark the lobby advertisement stale so the
    /// next throttled heartbeat republishes it (<c>UpdateLobby</c>,
    /// <c>fsmission.cpp:5606-5621</c>: at most once per 10 s,
    /// <c>c_flUpdateTimeInterval</c>). Called at every native call site this port
    /// has: host mission-param edit (<c>FedSrv.CPP:7579</c>), stage change
    /// (<c>fsmission.cpp:1766</c>), squad change (<c>:1669</c>), lobby/side lock
    /// (<c>:5028</c>), a player entering/leaving the team lobby
    /// (<c>fsship.cpp:1134</c>) and a player leaving the mission
    /// (<c>fsmission.cpp:523</c>).</summary>
    private void MarkLobbyDirty(string why)
    {
        lock (_statsLock) _lobbyDirtyVersion++;
        ConsoleLog.Write($"[lobby-dirty] mark ({why}) → version {LobbyDirtyVersion}");
    }

    /// <summary>
    /// P866 / SIM-0537 — the live half of native <c>CFSMission::QueueLobbyMissionInfo</c>
    /// (<c>FedSrv/fsmission.cpp:3983-4096</c>): the fields that need the server's
    /// player bookkeeping. Runs on the sim thread inside the per-tick stats
    /// snapshot (<c>Server.Broadcast.cs</c>) and freezes the result into
    /// <see cref="LobbyGameParams"/> for the off-thread heartbeat.
    /// <list type="bullet">
    /// <item><b>Player counts</b> (<c>:4041-4043</c>) — non-NPC players of this
    /// mission, and how many are still on <c>SIDE_TEAMLOBBY</c> (the port's
    /// unseated <c>null</c> / <c>SideTeamLobby</c> ships).</item>
    /// <item><b>Slot availability</b> (<c>:4046-4073</c>) — native's scan of the
    /// mission's sides: a joiner policy/stage/lobby-lock pre-gate, then per side
    /// <c>nMaxPlayersPerTeam - nNumPlayersOnSide</c>, plus the stage/base/active
    /// gate; the guaranteed bit when the side auto-accepts or is leaderless
    /// (<c>fAutoAcceptLeaders</c>, always on in this port — the same fold
    /// <c>RequestPosition</c>'s leaderless fast path makes).</item>
    /// <item><b>The countdown</b> (<c>:4036-4037,4084-4086</c>) — the armed
    /// deadline as an offset from now, and whether the countdown has started.</item>
    /// <item><b>Team/side counts, caps, eject pods, invulnerable stations,
    /// squad game, squad ids</b> (<c>:3995-4013,4076-4095</c>) — read from the
    /// mission's own <c>misparms</c> stand-in (<see cref="ParamsOf"/>) so a host
    /// edit is advertised, exactly like native's <c>m_misdef.misparms</c>.</item>
    /// </list>
    /// </summary>
    private LobbyGameParams SnapshotLobbyGameParams(Mission mission)
    {
        var parms = ParamsOf(mission);
        int players = 0, noat = 0;
        var perSide = new Dictionary<short, int>();
        foreach (var kv in _peerShips)
        {
            var ship = kv.Value.Ship;
            if (!ReferenceEquals(ship.Mission, mission) || ship.IsNpc) continue;
            players++;
            if (ship.SideId is { } sideId && sideId >= 0)
                perSide[sideId] = perSide.GetValueOrDefault(sideId) + 1;
            else
                noat++;   // SIDE_TEAMLOBBY (never seated / parked, -2)
        }

        short teams = parms.NTems > 0 ? parms.NTems : (short)mission.Sides.Count;
        short maxPerTeam = parms.NMaxPlayersPerTeam;
        int maxPerGame = parms.NTotalMaxPlayersPerGame > 0
            ? Math.Min(parms.NTotalMaxPlayersPerGame, teams * maxPerTeam)   // :4079-4081
            : teams * maxPerTeam;

        // :4047-4048 — pre-gate: joiners allowed (or the game has not started),
        // the lobby is not locked, and the game is not over.
        bool anySlots = false, guaranteedSlots = false;
        if ((parms[MissionParamsMsg.BitAllowJoiners] || mission.Stage == MissionStage.NotStarted)
            && !mission.LockLobby && mission.Stage != MissionStage.Over)
        {
            foreach (var side in mission.Sides)
            {
                if (side.Id < 0 || side.Id >= teams) continue;   // native's live side list only has nTeams sides
                int available = maxPerTeam - perSide.GetValueOrDefault(side.Id);
                // :4055-4057 — a not-started game always qualifies; once running a
                // side needs its restart base (GetBase) and to be active.
                bool canTake = mission.Stage == MissionStage.NotStarted
                    || (side.Active && mission.Sectors.Any(sec => sec.StationRuntimes.Any(st =>
                        !st.Dead && st.SideId == side.Id && st.Type.HasCapability(DataStationTypeIGC.SabmRestart))));
                if (!canTake || available == 0) continue;
                anySlots = true;
                // :4064-4072 — guaranteed when the side auto-accepts, or is
                // leaderless and the game auto-accepts leaders. The port folds
                // fAutoAcceptLeaders to "a leaderless side accepts" (the same rule
                // RequestPosition's fast path uses, Server.Dispatch.cs).
                if (side.AutoAccept || side.LeaderShipId is null) guaranteedSlots = true;
            }
        }

        // :3995-4013 — the squads of sides that have ships (and are active once
        // the game started). A squad id only exists on a squad game.
        List<string>? squadIds = null;
        if (mission.SquadGame)
            foreach (var side in mission.Sides)
            {
                if (side.SquadId is not { } squad) continue;
                bool hasShips = mission.Sectors.Any(sec => sec.Models.OfType<Ship>().Any(sh => sh.SideId == side.Id));
                if (!hasShips) continue;
                if (mission.Stage == MissionStage.Started && !side.Active) continue;
                (squadIds ??= []).Add(squad);
            }

        // :4036-4037 — misparms.timeStart - now, milliseconds; :4084-4086 the
        // countdown-started bit (STARTING, or NOTSTARTED with bAutoRestart).
        uint startOffsetMs = 0;
        if (_startCountdownDeadline.TryGetValue(mission, out var deadline) && deadline > _simNow)
            startOffsetMs = (uint)((deadline - _simNow) * 1000.0);
        bool countdownStarted = mission.Stage == MissionStage.Starting
            || (mission.Stage == MissionStage.NotStarted && parms[MissionParamsMsg.BitAutoRestart]);

        var live = new LobbyMissionLive(
            NumPlayers: players,
            NumNoatPlayers: noat,
            Teams: teams,
            MaxPlayersPerGame: maxPerGame,
            MinPlayersPerTeam: parms.NMinPlayersPerTeam,
            MaxPlayersPerTeam: maxPerTeam,
            StartTimeOffsetMs: startOffsetMs,
            CountdownStarted: countdownStarted,
            SquadGame: mission.SquadGame,
            // Native reads misparms.bEjectPods (fsmission.cpp:4095, Reset default
            // ON, igc.h:1246). The port's death path always ejects pods
            // (Server.Combat.cs:1715-1717 deliberately folds bEjectPods on), so
            // the advertisement publishes that effective value rather than the
            // unmodelled bit — the same honesty the client's GameParams uses for
            // its other Reset defaults.
            EjectPods: true,
            InvulnerableStations: parms[MissionParamsMsg.BitInvulnerableStations],
            AnySlotsAvailable: anySlots,
            GuaranteedSlotsAvailable: guaranteedSlots,
            SquadIds: squadIds);
        return LobbyGameParams.FromMission(mission, live);
    }

    /// <summary>P866 test seam — the advertisement snapshot the heartbeat would
    /// publish right now (same call the per-tick stats snapshot makes).</summary>
    internal LobbyGameParams TestLobbyParams() =>
        SnapshotLobbyGameParams(_missions.Count > 0 ? _missions[0] : throw new InvalidOperationException("no mission"));
}
