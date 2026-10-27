using System.Data.Common;
using System.Globalization;
using Allegiance.Core.Identity;
using Allegiance.Lobby.Identity;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Allegiance.Lobby.Squad;

/// <summary>
/// Persistent squad store and membership engine for the lobby (P820a) — the
/// port of the native Zone Club squad database driven by the club
/// <c>SQUAD_*</c> messages (<c>Inc/ClubMessages.h:129-301</c>). It persists
/// squads (name, tag, description, URL, civ, created) with native role-based
/// membership (leader / ASL / member / pending, <c>Zone/Member.h:37-43,91-101</c>),
/// join requests, and an ownership/audit log, and enforces native's permission
/// rules (<c>WinTrek/SquadsScreen.cpp:1623-1670</c>): owner-only administrative
/// actions, and freely-joinable membership.
/// </summary>
/// <remarks>
/// Shares the lobby's versioned SQLite/PostgreSQL store with
/// <see cref="IdentityStore"/> (same connection string; the squad DDL lives in
/// <see cref="IdentityStore.Migrate"/>). All reads/writes are parameterized and
/// mutating paths are transactional so a half-applied action can never corrupt
/// membership. Ownership changes (creation, transfer, leader succession) are
/// logged as native's ownership log: "name          date" pairs, newest first
/// (<c>ZoneSquad.cpp:290-332</c>); other role changes are not logged.
/// </remarks>
public sealed class SquadStore
{
    private readonly IdentitySettings _settings;
    private readonly TimeProvider _clock;
    public SquadStore(IdentitySettings settings, TimeProvider clock) { _settings = settings; _clock = clock; }

    private DbConnection Open()
    {
        DbConnection c = _settings.Store == "Postgres" ? new NpgsqlConnection(_settings.ConnectionString) : new SqliteConnection(_settings.ConnectionString);
        c.Open();
        return c;
    }
    private static DbCommand Command(DbConnection c, string sql, params object?[] values)
    {
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        for (int i = 0; i < values.Length; i++)
        {
            var p = cmd.CreateParameter(); p.ParameterName = "p" + i; p.Value = values[i] ?? DBNull.Value; cmd.Parameters.Add(p);
        }
        return cmd;
    }
    private string[]? Row(string sql, params object?[] values)
    {
        using var c = Open(); using var cmd = Command(c, sql, values); using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }
    private static string[] ReadRow(DbDataReader reader) =>
        Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!).ToArray();

    private static string SqId() => Guid.NewGuid().ToString("N");

    /// <summary>Map a stored status string to <see cref="SquadContracts.SquadRole"/>.</summary>
    private static SquadContracts.SquadRole ParseRole(string s) => s switch
    {
        "leader" => SquadContracts.SquadRole.Leader,
        "asl" => SquadContracts.SquadRole.AssistantLeader,
        "member" => SquadContracts.SquadRole.Member,
        _ => SquadContracts.SquadRole.Pending,
    };
    private static string RoleString(SquadContracts.SquadRole r) => r switch
    {
        SquadContracts.SquadRole.Leader => "leader",
        SquadContracts.SquadRole.AssistantLeader => "asl",
        SquadContracts.SquadRole.Member => "member",
        _ => "pending",
    };

    /// <summary>Whether a role counts as an active (non-pending) member —
    /// native <c>IsMemberOf</c> (<c>ZoneSquad.cpp:92-99</c>).</summary>
    private static bool IsActive(SquadContracts.SquadRole r) =>
        r is SquadContracts.SquadRole.Leader or SquadContracts.SquadRole.AssistantLeader or SquadContracts.SquadRole.Member;

    private static string Date(long now) => DateTimeOffset.FromUnixTimeSeconds(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---- Native permission rules (SquadsScreen.cpp:1623-1670) ----
    //   create:      anyone signed-in, unless mid-create           :1625
    //   join:        actor neither member nor pending              :1640-1641
    //   cancel:      actor pending                                 :1642
    //   quit:        actor a member (leader is a member too)       :1643
    //   owner admin: accept/reject(pending)/boot(member)/promote/
    //                demote/transfer/edit all require actor==owner :1648-1670
    //     accept :1653, reject/boot :1652, promote :1654, demote :1655,
    //     transfer (target a member, not self, not pending) :1657
    //     edit   :1656/:1663

    /// <summary>Create a squad with the actor as leader — native
    /// <c>OnButtonCreateSquad</c>→<c>SQUAD_CREATE</c> (<c>SquadsScreen.cpp:2017-2059</c>,
    /// <c>Inc/ClubMessages.h:181-187</c>). Returns null on success or an error
    /// message (validation failures surface as <c>SQUAD_TEXT_MESSAGE</c>, :1496-1503).</summary>
    public SquadContracts.SquadActionResult CreateSquad(string actorId, SquadContracts.CreateSquadRequest req, long now)
    {
        string name = (req.Name ?? "").Trim();
        string tag = (req.Tag ?? "").Trim();
        string desc = (req.Description ?? "").Trim();
        string url = (req.Url ?? "").Trim();
        if (name.Length == 0) return SquadContracts.SquadActionResult.Fail("You must enter the \"squad name\".");
        if (name.Length > SquadContracts.MaxName) return SquadContracts.SquadActionResult.Fail($"Squad name must be ≤{SquadContracts.MaxName} characters.");
        if (tag.Length > SquadContracts.MaxTag) return SquadContracts.SquadActionResult.Fail($"Squad tag must be ≤{SquadContracts.MaxTag} characters.");
        if (desc.Length > SquadContracts.MaxDescription) return SquadContracts.SquadActionResult.Fail("Description too long.");
        if (url.Length > SquadContracts.MaxUrl) return SquadContracts.SquadActionResult.Fail("URL too long.");
        var acct = Row("SELECT callsign FROM accounts WHERE id=@p0", actorId);
        if (acct is null) return SquadContracts.SquadActionResult.Fail("Not signed in.");

        string id = SqId();
        using var c = Open();
        using var tx = c.BeginTransaction();
        // Native squad names are unique per the club database (case-insensitive).
        using (var exists = Command(c, "SELECT 1 FROM squads WHERE UPPER(name)=UPPER(@p0)", name))
        {
            exists.Transaction = tx;
            bool taken;
            using (var dr = exists.ExecuteReader()) taken = dr.Read();
            // The reader must be closed before the rollback: PostgreSQL refuses a new command while one is open.
            if (taken) { tx.Rollback(); return SquadContracts.SquadActionResult.Fail("That squad name is already in use."); }
        }

        using (var ins = Command(c, """
            INSERT INTO squads(id,name,tag,description,url,civ,created_at,leader_account_id)
            VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)
            """, id, name, tag, desc, url, req.Civ, now, actorId))
        { ins.Transaction = tx; ins.ExecuteNonQuery(); }
        using (var mem = Command(c, "INSERT INTO squad_members(squad_id,account_id,status,last_played,joined_at) VALUES(@p0,@p1,'leader',NULL,@p2)",
            id, actorId, now))
        { mem.Transaction = tx; mem.ExecuteNonQuery(); }
        Log(c, tx, id, $"{acct[0]}          {Date(now)}");
        tx.Commit();
        return SquadContracts.SquadActionResult.Success;
    }

    /// <summary>Edit description/URL/civ — native <c>OnButtonEditSquad</c>→
    /// <c>SQUAD_EDIT</c> (owner-only, <c>SquadsScreen.cpp:2061-2080,1656</c>;
    /// name is never transmitted on edit, :2181-2193).</summary>
    public SquadContracts.SquadActionResult EditSquad(string actorId, string squadId, SquadContracts.EditSquadRequest req, long now)
    {
        string desc = (req.Description ?? "").Trim();
        string url = (req.Url ?? "").Trim();
        if (desc.Length > SquadContracts.MaxDescription) return SquadContracts.SquadActionResult.Fail("Description too long.");
        if (url.Length > SquadContracts.MaxUrl) return SquadContracts.SquadActionResult.Fail("URL too long.");
        if (!IsOwner(actorId, squadId)) return SquadContracts.SquadActionResult.Fail("Only the squad leader can edit the squad.");
        using var c = Open(); using var tx = c.BeginTransaction();
        using var cmd = Command(c, "UPDATE squads SET description=@p0,url=@p1,civ=@p2 WHERE id=@p3", desc, url, req.Civ, squadId);
        cmd.Transaction = tx; cmd.ExecuteNonQuery();
        tx.Commit();
        return SquadContracts.SquadActionResult.Success;
    }

    /// <summary>All squads sorted by a column, window centered on the basis
    /// squad — native <c>SQUAD_NEXT_PAGE_ALL</c> (<c>SquadsScreen.cpp:960-1003</c>,
    /// <c>Inc/ClubMessages.h:158-161</c>). Returns up to <paramref name="count"/>
    /// summaries each carrying its absolute ranking in the sort.</summary>
    public IReadOnlyList<SquadContracts.SquadSummary> All(SquadContracts.SquadSortColumn sort, string? basisId, int count)
    {
        var rows = new List<(string Id, string Name, string Tag, short Civ, int Wins, int Losses, int Score, int MemberCount, string Leader)>();
        using (var c = Open())
        using (var cmd = Command(c, """
            SELECT s.id,s.name,s.tag,s.civ,s.wins,s.losses,s.score,
                   (SELECT COUNT(*) FROM squad_members m WHERE m.squad_id=s.id), a.callsign
            FROM squads s JOIN accounts a ON a.id=s.leader_account_id
            """))
        using (var dr = cmd.ExecuteReader())
        {
            while (dr.Read())
            {
                var r = ReadRow(dr);
                rows.Add((r[0], r[1], r[2], short.Parse(r[3]), int.Parse(r[4]), int.Parse(r[5]), int.Parse(r[6]),
                    int.Parse(r[7]), r[8]));
            }
        }
        // Sort by column (native SquadRankingCompare, SquadsScreen.cpp:2647,2796-2802;
        // name ascending, numeric columns descending).
        Comparison<(string Id, string Name, string Tag, short Civ, int Wins, int Losses, int Score, int MemberCount, string Leader)> cmp =
            sort switch
            {
                SquadContracts.SquadSortColumn.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
                SquadContracts.SquadSortColumn.Score => (a, b) => b.Score.CompareTo(a.Score),
                SquadContracts.SquadSortColumn.Wins => (a, b) => b.Wins.CompareTo(a.Wins),
                SquadContracts.SquadSortColumn.Losses => (a, b) => b.Losses.CompareTo(a.Losses),
                _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            };
        rows.Sort(cmp);

        int basisIdx = 0;
        if (basisId is not null) basisIdx = rows.FindIndex(r => r.Id == basisId);
        if (basisIdx < 0) basisIdx = 0;
        int start = Math.Max(0, basisIdx - count / 2);
        int n = Math.Min(count, rows.Count - start);
        var outList = new List<SquadContracts.SquadSummary>(n);
        for (int i = 0; i < n; i++)
        {
            var r = rows[start + i];
            outList.Add(new SquadContracts.SquadSummary(r.Id, r.Name, r.Tag, r.Civ, r.Wins, r.Losses, r.Score,
                Ranking: start + i + 1, r.MemberCount, LeaderCallsign: r.Leader));
        }
        return outList;
    }

    /// <summary>The actor's squads — native <c>SQUAD_NEXT_PAGE_MY</c> /
    /// <c>SQUAD_DUDEX_LIST</c> (<c>SquadsScreen.cpp:2345-2368</c>,
    /// <c>Inc/ClubMessages.h:163-178</c>): every squad the player is associated
    /// with (member, ASL, leader, or pending).</summary>
    public IReadOnlyList<SquadContracts.SquadSummary> ByPlayer(string accountId)
    {
        var rows = new List<SquadContracts.SquadSummary>();
        using var c = Open();
        using var cmd = Command(c, """
            SELECT s.id,s.name,s.tag,s.civ,s.wins,s.losses,s.score,m.status,
                   (SELECT COUNT(*) FROM squad_members x WHERE x.squad_id=s.id)
            FROM squad_members m JOIN squads s ON s.id=m.squad_id
            WHERE m.account_id=@p0
            """, accountId);
        using var dr = cmd.ExecuteReader();
        var unsorted = new List<(string Id, string Name, string Tag, short Civ, int Wins, int Losses, int Score, int Count, SquadContracts.SquadRole Role)>();
        while (dr.Read())
        {
            var r = ReadRow(dr);
            unsorted.Add((r[0], r[1], r[2], short.Parse(r[3]), int.Parse(r[4]), int.Parse(r[5]), int.Parse(r[6]),
                int.Parse(r[8]), ParseRole(r[7])));
        }
        unsorted.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        int rank = 1;
        foreach (var u in unsorted)
            rows.Add(new SquadContracts.SquadSummary(u.Id, u.Name, u.Tag, u.Civ, u.Wins, u.Losses, u.Score,
                Ranking: rank++, u.Count, ViewerRole: u.Role));
        return rows;
    }

    /// <summary>Find squads by partial name — native <c>SQUAD_PAGE_FIND</c>
    /// (<c>SquadsScreen.cpp:2216-2246</c>, <c>Inc/ClubMessages.h:170-172</c>).</summary>
    public IReadOnlyList<SquadContracts.SquadSummary> Find(string partial)
    {
        partial = (partial ?? "").Trim();
        if (partial.Length == 0) return [];
        var rows = new List<SquadContracts.SquadSummary>();
        using var c = Open();
        using var cmd = Command(c, """
            SELECT s.id,s.name,s.tag,s.civ,s.wins,s.losses,s.score,
                   (SELECT COUNT(*) FROM squad_members m WHERE m.squad_id=s.id), a.callsign
            FROM squads s JOIN accounts a ON a.id=s.leader_account_id
            WHERE LOWER(s.name) LIKE LOWER(@p0)
            """, "%" + partial + "%");
        using var dr = cmd.ExecuteReader();
        var unsorted = new List<(string Id, string Name, string Tag, short Civ, int Wins, int Losses, int Score, int Count, string Leader)>();
        while (dr.Read())
        {
            var r = ReadRow(dr);
            unsorted.Add((r[0], r[1], r[2], short.Parse(r[3]), int.Parse(r[4]), int.Parse(r[5]), int.Parse(r[6]),
                int.Parse(r[7]), r[8]));
        }
        unsorted.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        int rank = 1;
        foreach (var u in unsorted)
            rows.Add(new SquadContracts.SquadSummary(u.Id, u.Name, u.Tag, u.Civ, u.Wins, u.Losses, u.Score,
                Ranking: rank++, u.Count, LeaderCallsign: u.Leader));
        return rows;
    }

    /// <summary>
    /// P855/SIM-0765 — apply one finished squad game's standings: the winner
    /// squad +1 win, each loser squad +1 loss. The port of what the native
    /// club DB's <c>SquadReportGame</c> stored procedure does with
    /// <c>CQReportSquadGame</c>'s payload (<c>squadIDWon</c> +
    /// <c>squadIDLost1..5</c>, <c>FedSrv/srvqueries.h:173-194</c>, posted by
    /// <c>RecordSquadGame</c>, <c>FedSrv/FedSrv.CPP:1198-1300</c>). The SQL
    /// shape is era plumbing; the stored facts — one win for the winning
    /// squad, one loss per losing squad, once per (server, match) — are the
    /// behaviour. Idempotent via <c>squad_game_results.match_key</c>: a
    /// replayed report is skipped, and match keys are scoped per reporting
    /// server so two servers reusing an id never merge.
    /// </summary>
    /// <returns>true if the game was applied now; false if it was already on
    /// record (duplicate).</returns>
    public bool ApplyGameResult(DbConnection c, DbTransaction tx, string matchKey,
        string? winnerSquadId, IReadOnlyList<string> loserSquadIds, long now)
    {
        using (var dup = Command(c, "SELECT 1 FROM squad_game_results WHERE match_key=@p0", matchKey))
        {
            dup.Transaction = tx;
            using var dr = dup.ExecuteReader();
            if (dr.Read()) return false;
        }
        using (var ins = Command(c,
            "INSERT INTO squad_game_results(match_key,winner_squad_id,loser_squad_ids,reported_at) VALUES(@p0,@p1,@p2,@p3)",
            matchKey, winnerSquadId, string.Join(',', loserSquadIds), now))
        { ins.Transaction = tx; ins.ExecuteNonQuery(); }
        if (winnerSquadId is not null)
        {
            using var w = Command(c, "UPDATE squads SET wins = wins + 1 WHERE id=@p0", winnerSquadId);
            w.Transaction = tx; w.ExecuteNonQuery();
        }
        foreach (var loser in loserSquadIds)
        {
            using var l = Command(c, "UPDATE squads SET losses = losses + 1 WHERE id=@p0", loser);
            l.Transaction = tx; l.ExecuteNonQuery();
        }
        return true;
    }

    /// <summary>Standalone form of <see cref="ApplyGameResult(DbConnection,DbTransaction,string,string?,System.Collections.Generic.IReadOnlyList{string},long)"/>
    /// with its own transaction.</summary>
    public bool ApplyGameResult(string matchKey, string? winnerSquadId, IReadOnlyList<string> loserSquadIds, long now)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        bool applied = ApplyGameResult(c, tx, matchKey, winnerSquadId, loserSquadIds, now);
        tx.Commit();
        return applied;
    }

    /// <summary>Full squad details + membership — native <c>SQUAD_DETAILS_REQ</c>→
    /// <c>SQUAD_DETAILS</c>/<c>SQUAD_DETAILS_PLAYER</c> (<c>SquadsScreen.cpp:1332-1463</c>,
    /// <c>Inc/ClubMessages.h:202-205,278-292</c>).</summary>
    public SquadContracts.SquadDetails? Details(string squadId)
    {
        var sq = Row("SELECT id,name,tag,description,url,civ,created_at,leader_account_id FROM squads WHERE id=@p0", squadId);
        if (sq is null) return null;
        var members = new List<SquadContracts.SquadMember>();
        using var c = Open();
        using var cmd = Command(c, """
            SELECT m.account_id,a.callsign,m.status,m.last_played,m.joined_at
            FROM squad_members m JOIN accounts a ON a.id=m.account_id
            WHERE m.squad_id=@p0 ORDER BY m.joined_at
            """, squadId);
        using var dr = cmd.ExecuteReader();
        while (dr.Read())
        {
            var r = ReadRow(dr);
            long? lp = r[3].Length == 0 ? null : long.Parse(r[3]);
            members.Add(new SquadContracts.SquadMember(r[0], r[1], ParseRole(r[2]), lp, long.Parse(r[4])));
        }
        return new SquadContracts.SquadDetails(sq[0], sq[1], sq[2], sq[3], sq[4], short.Parse(sq[5]),
            long.Parse(sq[6]), sq[7], members);
    }

    /// <summary>The ownership/audit log — native <c>SQUAD_LOG_REQ</c>→
    /// <c>SQUAD_LOG_INFO</c> (<c>SquadsScreen.cpp:1465-1481</c>,
    /// <c>Inc/ClubMessages.h:207-210,294-297</c>).</summary>
    public IReadOnlyList<SquadContracts.SquadLogEntry> Log(string squadId)
    {
        var lines = new List<SquadContracts.SquadLogEntry>();
        using var c = Open();
        using var cmd = Command(c, "SELECT seq,line FROM squad_log WHERE squad_id=@p0 ORDER BY seq DESC", squadId);
        using var dr = cmd.ExecuteReader();
        while (dr.Read())
        {
            var r = ReadRow(dr);
            lines.Add(new SquadContracts.SquadLogEntry(long.Parse(r[0]), r[1]));
        }
        return lines;
    }

    /// <summary>Request to join — native <c>SQUAD_MAKE_JOIN_REQUEST</c>
    /// (<c>SquadsScreen.cpp:1968-1979</c>); anyone who is neither member nor
    /// pending (:1640-1641).</summary>
    public SquadContracts.SquadActionResult Join(string actorId, string squadId, long now)
    {
        if (Row("SELECT 1 FROM squads WHERE id=@p0", squadId) is null) return SquadContracts.SquadActionResult.Fail("Squad not found.");
        var role = MemberRole(actorId, squadId);
        if (role is { } r && IsActive(r)) return SquadContracts.SquadActionResult.Fail("Already a member.");
        if (role == SquadContracts.SquadRole.Pending) return SquadContracts.SquadActionResult.Fail("Join request already issued.");
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var cmd = Command(c, "INSERT INTO squad_members(squad_id,account_id,status,last_played,joined_at) VALUES(@p0,@p1,'pending',NULL,@p2)",
            squadId, actorId, now))
        { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
        tx.Commit();
        return SquadContracts.SquadActionResult.Success;
    }

    /// <summary>Cancel a join request — native <c>SQUAD_CANCEL_JOIN_REQUEST</c>
    /// (<c>SquadsScreen.cpp:1981-1997</c>); only a pending actor (:1642).</summary>
    public SquadContracts.SquadActionResult CancelJoin(string actorId, string squadId, long now)
    {
        if (Row("SELECT 1 FROM squads WHERE id=@p0", squadId) is null) return SquadContracts.SquadActionResult.Fail("Squad not found.");
        var role = MemberRole(actorId, squadId);
        if (role is null || role.Value != SquadContracts.SquadRole.Pending) return SquadContracts.SquadActionResult.Fail("You have no outstanding join request.");
        return RemoveMembership(squadId, actorId, "cancelled", now);
    }

    /// <summary>Accept a join request — native <c>SQUAD_ACCEPT_JOIN_REQUEST</c>
    /// (owner-only, target pending, <c>SquadsScreen.cpp:1915-1926,1653</c>).</summary>
    public SquadContracts.SquadActionResult Accept(string actorId, string squadId, string memberId, long now)
    {
        if (!IsOwner(actorId, squadId)) return SquadContracts.SquadActionResult.Fail("Only the squad leader can accept members.");
        if (MemberRole(memberId, squadId) is not SquadContracts.SquadRole.Pending) return SquadContracts.SquadActionResult.Fail("That player has no pending request.");
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var cmd = Command(c, "UPDATE squad_members SET status='member',joined_at=@p0 WHERE squad_id=@p1 AND account_id=@p2", now, squadId, memberId))
        { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
        tx.Commit();
        return SquadContracts.SquadActionResult.Success;
    }

    /// <summary>Reject a join request (pending) or boot a member — native
    /// <c>SQUAD_REJECT_JOIN_REQUEST</c>/<c>SQUAD_BOOT_MEMBER</c>, owner-only,
    /// target member-or-pending, non-self (<c>SquadsScreen.cpp:1895-1913,1652</c>).
    /// </summary>
    public SquadContracts.SquadActionResult RejectOrBoot(string actorId, string squadId, string memberId, long now)
    {
        if (string.Equals(actorId, memberId, StringComparison.Ordinal)) return SquadContracts.SquadActionResult.Fail("Cannot remove yourself.");
        if (!IsOwner(actorId, squadId)) return SquadContracts.SquadActionResult.Fail("Only the squad leader can reject or boot members.");
        var target = MemberRole(memberId, squadId);
        if (target is not (SquadContracts.SquadRole.Pending or SquadContracts.SquadRole.Member or SquadContracts.SquadRole.AssistantLeader))
            return SquadContracts.SquadActionResult.Fail("That player is not a removable member.");
        return RemoveMembership(squadId, memberId, "removed", now);
    }

    /// <summary>Promote a member to ASL — native <c>SQUAD_PROMOTE_TO_ASL</c>
    /// (owner-only, target must be POS_MEMBER, <c>SquadsScreen.cpp:1928-1939,1654</c>).</summary>
    public SquadContracts.SquadActionResult Promote(string actorId, string squadId, string memberId, long now)
    {
        if (!IsOwner(actorId, squadId)) return SquadContracts.SquadActionResult.Fail("Only the squad leader can commission officers.");
        if (MemberRole(memberId, squadId) != SquadContracts.SquadRole.Member) return SquadContracts.SquadActionResult.Fail("Only a member can be promoted.");
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var cmd = Command(c, "UPDATE squad_members SET status='asl' WHERE squad_id=@p0 AND account_id=@p1", squadId, memberId))
        { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
        tx.Commit();
        return SquadContracts.SquadActionResult.Success;
    }

    /// <summary>Demote an ASL to member — native <c>SQUAD_DEMOTE_TO_MEMBER</c>
    /// (owner-only, target POS_ASL, <c>SquadsScreen.cpp:1941-1952,1655</c>).</summary>
    public SquadContracts.SquadActionResult Demote(string actorId, string squadId, string memberId, long now)
    {
        if (!IsOwner(actorId, squadId)) return SquadContracts.SquadActionResult.Fail("Only the squad leader can demote officers.");
        if (MemberRole(memberId, squadId) != SquadContracts.SquadRole.AssistantLeader) return SquadContracts.SquadActionResult.Fail("Only an ASL can be demoted.");
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var cmd = Command(c, "UPDATE squad_members SET status='member' WHERE squad_id=@p0 AND account_id=@p1", squadId, memberId))
        { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
        tx.Commit();
        return SquadContracts.SquadActionResult.Success;
    }

    /// <summary>Transfer ownership — native <c>SQUAD_TRANSFER_CONTROL</c>
    /// (owner-only, target a member/ASL, not self, not pending,
    /// <c>SquadsScreen.cpp:1955-1966,1657</c>). Previous leader becomes an ASL
    /// (<c>Inc/ClubMessages.h:249-253</c>).</summary>
    public SquadContracts.SquadActionResult Transfer(string actorId, string squadId, string memberId, long now)
    {
        if (!IsOwner(actorId, squadId)) return SquadContracts.SquadActionResult.Fail("Only the squad leader can transfer control.");
        if (string.Equals(actorId, memberId, StringComparison.Ordinal)) return SquadContracts.SquadActionResult.Fail("Cannot transfer control to yourself.");
        var target = MemberRole(memberId, squadId);
        if (target is null || !IsActive(target.Value)) return SquadContracts.SquadActionResult.Fail("That player is not a member.");
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var upd = Command(c, "UPDATE squad_members SET status='leader' WHERE squad_id=@p0 AND account_id=@p1", squadId, memberId))
        { upd.Transaction = tx; upd.ExecuteNonQuery(); }
        using (var old = Command(c, "UPDATE squad_members SET status='asl' WHERE squad_id=@p0 AND account_id=@p1", squadId, actorId))
        { old.Transaction = tx; old.ExecuteNonQuery(); }
        using (var lead = Command(c, "UPDATE squads SET leader_account_id=@p0 WHERE id=@p1", memberId, squadId))
        { lead.Transaction = tx; lead.ExecuteNonQuery(); }
        Log(c, tx, squadId, $"{CallsignOf(memberId)}          {Date(now)}");
        tx.Commit();
        return SquadContracts.SquadActionResult.Success;
    }

    /// <summary>Quit — native <c>SQUAD_QUIT</c>; any member may quit including
    /// the leader (<c>SquadsScreen.cpp:1999-2015,1643</c>). A quitting leader's
    /// ownership is decided server-side (the Zone club server, not in this tree):
    /// this port transfers control to the earliest ASL, else earliest member,
    /// else deletes the now-empty squad. The client reflects the result via
    /// subsequent list refresh / <c>SQUAD_DELETED</c> (:1483-1494).</summary>
    public SquadContracts.SquadActionResult Quit(string actorId, string squadId, long now)
    {
        if (Row("SELECT 1 FROM squads WHERE id=@p0", squadId) is null) return SquadContracts.SquadActionResult.Fail("Squad not found.");
        var role = MemberRole(actorId, squadId);
        if (role is null || !IsActive(role.Value)) return SquadContracts.SquadActionResult.Fail("You are not a member.");
        if (role.Value != SquadContracts.SquadRole.Leader) return RemoveMembership(squadId, actorId, "quit", now);

        // Leader quitting: find a successor (earliest ASL, else earliest member).
        string? successor = null;
        using (var c = Open())
        {
            using var asl = Command(c, "SELECT account_id FROM squad_members WHERE squad_id=@p0 AND status='asl' ORDER BY joined_at LIMIT 1", squadId);
            using (var dr = asl.ExecuteReader()) if (dr.Read()) successor = dr.IsDBNull(0) ? null : dr.GetValue(0).ToString();
            if (successor is null)
            {
                using var mem = Command(c, "SELECT account_id FROM squad_members WHERE squad_id=@p0 AND status='member' ORDER BY joined_at LIMIT 1", squadId);
                using var dr2 = mem.ExecuteReader(); if (dr2.Read()) successor = dr2.IsDBNull(0) ? null : dr2.GetValue(0).ToString();
            }
        }
        if (successor is null)
        {
            // No one left: the squad dissolves.
            using var c = Open(); using var tx = c.BeginTransaction();
            using (var del = Command(c, "DELETE FROM squads WHERE id=@p0", squadId)) { del.Transaction = tx; del.ExecuteNonQuery(); }
            tx.Commit();
            return SquadContracts.SquadActionResult.Success;
        }
        using var c2 = Open(); using var tx2 = c2.BeginTransaction();
        using (var rem = Command(c2, "DELETE FROM squad_members WHERE squad_id=@p0 AND account_id=@p1", squadId, actorId))
        { rem.Transaction = tx2; rem.ExecuteNonQuery(); }
        using (var lead = Command(c2, "UPDATE squad_members SET status='leader' WHERE squad_id=@p0 AND account_id=@p1", squadId, successor))
        { lead.Transaction = tx2; lead.ExecuteNonQuery(); }
        using (var upd = Command(c2, "UPDATE squads SET leader_account_id=@p0 WHERE id=@p1", successor, squadId))
        { upd.Transaction = tx2; upd.ExecuteNonQuery(); }
        Log(c2, tx2, squadId, $"{CallsignOf(successor)}          {Date(now)}");
        tx2.Commit();
        return SquadContracts.SquadActionResult.Success;
    }

    /// <summary>The squads a character belongs to — native
    /// <c>CHARACTER_INFO_SQUAD</c> (<c>Inc/ClubMessages.h:129-138</c>), shown on
    /// the character-info screen (<c>CharInfoScreen.cpp:618-624</c>). Lists the
    /// player's active memberships (leader/ASL/member); pending requests excluded.</summary>
    public IReadOnlyList<(string Id, string Name)> CharacterSquads(string accountId)
    {
        using var c = Open();
        using var cmd = Command(c, """
            SELECT s.id,s.name FROM squad_members m JOIN squads s ON s.id=m.squad_id
            WHERE m.account_id=@p0 AND m.status IN ('leader','asl','member')
            """, accountId);
        using var dr = cmd.ExecuteReader();
        var unsorted = new List<(string Id, string Name)>();
        while (dr.Read())
        {
            var r = ReadRow(dr);
            unsorted.Add((r[0], r[1]));
        }
        unsorted.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return unsorted;
    }

    /// <summary>The squad memberships the lobby stamps into a player's join
    /// token (P820b) so they reach the game server over the identity path —
    /// native gates a side's squad on <c>GetIsMemberOfSquad</c> /
    /// <c>GetCanLeadSquad</c> (fsship.cpp:1358-1390). Only ACTIVE memberships
    /// (leader/ASL/member); <c>CanLead</c> = leader or ASL (GetCanLeadSquad,
    /// fsship.cpp:1374-1390). Pending requests are excluded (a pending player
    /// is not a member of the squad).</summary>
    public IReadOnlyList<(string Id, string Name, bool CanLead)> SquadClaimsFor(string accountId)
    {
        var claims = new List<(string Id, string Name, bool CanLead)>();
        using var c = Open();
        using var cmd = Command(c, """
            SELECT s.id,s.name,m.status FROM squad_members m JOIN squads s ON s.id=m.squad_id
            WHERE m.account_id=@p0 AND m.status IN ('leader','asl','member')
            LIMIT @p1
            """, accountId, JoinTokens.MaxSquadsPerToken);
        using var dr = cmd.ExecuteReader();
        while (dr.Read())
        {
            var r = ReadRow(dr);
            bool canLead = r[2] == "leader" || r[2] == "asl";
            claims.Add((r[0], r[1], canLead));
        }
        return claims;
    }

    // ---- internals ----

    private SquadContracts.SquadRole? MemberRole(string accountId, string squadId)
    {
        var r = Row("SELECT status FROM squad_members WHERE squad_id=@p0 AND account_id=@p1", squadId, accountId);
        return r is null ? null : ParseRole(r[0]);
    }
    private bool IsOwner(string actorId, string squadId)
        => Row("SELECT 1 FROM squads WHERE id=@p0 AND leader_account_id=@p1", squadId, actorId) is not null;
    private string CallsignOf(string accountId)
        => Row("SELECT callsign FROM accounts WHERE id=@p0", accountId)?[0] ?? accountId;

    private SquadContracts.SquadActionResult RemoveMembership(string squadId, string memberId, string reason, long now)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var del = Command(c, "DELETE FROM squad_members WHERE squad_id=@p0 AND account_id=@p1", squadId, memberId))
        { del.Transaction = tx; del.ExecuteNonQuery(); }
        tx.Commit();
        return SquadContracts.SquadActionResult.Success;
    }

    private static void Log(DbConnection c, DbTransaction tx, string squadId, string line)
    {
        long seq = 0;
        using (var m = Command(c, "SELECT COALESCE(MAX(seq),0)+1 FROM squad_log WHERE squad_id=@p0", squadId))
        { m.Transaction = tx; using var dr = m.ExecuteReader(); if (dr.Read() && !dr.IsDBNull(0)) seq = (long)Convert.ToInt64(dr.GetValue(0), CultureInfo.InvariantCulture); }
        using var cmd = Command(c, "INSERT INTO squad_log(squad_id,seq,line) VALUES(@p0,@p1,@p2)", squadId, seq, line);
        cmd.Transaction = tx; cmd.ExecuteNonQuery();
    }
}
