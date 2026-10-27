using System.Data.Common;
using Allegiance.Lobby.Identity;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Allegiance.Lobby.Career;

/// <summary>
/// Persistent, per-account, per-civilization career statistics and leaderboard
/// backing for the lobby (P808b). This is the port of the native club database's
/// per-character stats: game servers report each verified player's final
/// <see cref="PlayerScore"/>-style result (native <c>SetCharStats</c>,
/// <c>FedSrv/FedSrv.CPP:1045-1140</c>), and the lobby accumulates it into a
/// per-civ career line and serves character info + the leaderboard to the
/// client screens.
/// </summary>
/// <remarks>
/// Shares the lobby's versioned SQLite/PostgreSQL store with
/// <see cref="IdentityStore"/> (same connection string; the career DDL lives in
/// <see cref="IdentityStore.Migrate"/>). Reuses the source-generated request
/// shaping already used by the rest of the lobby. All reads/writes are
/// parameterized; the write path is transactional so a half-applied report can
/// never corrupt a career line.
/// </remarks>
public sealed class CareerStore
{
    private readonly IdentitySettings _settings;
    private readonly TimeProvider _clock;
    public CareerStore(IdentitySettings settings, TimeProvider clock) { _settings = settings; _clock = clock; }

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
    private int Execute(string sql, params object?[] values)
    {
        using var c = Open(); using var cmd = Command(c, sql, values); return cmd.ExecuteNonQuery();
    }
    private string[]? Row(string sql, params object?[] values)
    {
        using var c = Open(); using var cmd = Command(c, sql, values); using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }
    private static string[] ReadRow(DbDataReader reader) =>
        Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!).ToArray();

    /// <summary>
    /// Ingest one server-reported match result (the port of native
    /// <c>SetCharStats</c>' persist update, <c>FedSrv.CPP:1051-1105</c>).
    /// Anti-abuse is enforced at the endpoint (verified server only) and here
    /// (one result per player per match via the composite key, so a replayed or
    /// duplicated report is idempotently skipped). Only results whose match
    /// scores counted are applied (native's <c>if (GetScoresCount())</c> gate,
    /// <c>FedSrv.CPP:1065</c>) and only players with time played
    /// (<c>0.0f == GetTimePlayed() ⇒ return</c>, <c>FedSrv.CPP:1052-1055</c>).
    /// <para>P855/SIM-0765 — when the report carries a squad-game section
    /// (native <c>RecordSquadGame</c>, FedSrv.CPP:1198-1300), the squad
    /// standings ride the SAME transaction through <see cref="Squad.SquadStore.ApplyGameResult"/>:
    /// winner +1 win, each loser +1 loss, once per (server, match).</para>
    /// </summary>
    public CareerContracts.MatchReportOutcome ApplyMatchReport(CareerContracts.MatchReport report, string serverId, long now, Squad.SquadStore? squads = null)
    {
        int applied = 0, duplicates = 0, skipped = 0;
        if (report.Players.Count == 0 && report.SquadGame is null) return new(applied, duplicates, skipped);
        // A server's match ids are its own: scope them per server, so two servers
        // reusing an id never block (or merge with) each other's results.
        string matchKey = $"{serverId}:{report.MatchId}";

        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var p in report.Players)
        {
            if (!p.ScoresCount || p.Minutes <= 0) { skipped++; continue; }
            // Anti-abuse: one result per player per match (composite PK).
            using (var dup = Command(c, "SELECT 1 FROM match_results WHERE match_id=@p0 AND account_id=@p1", matchKey, p.AccountId))
            { dup.Transaction = tx; using var dr = dup.ExecuteReader(); if (dr.Read()) { duplicates++; continue; } }

            using (var ins = Command(c, """
                INSERT INTO match_results(match_id, account_id, civ, score, combat_rating, kills, deaths, base_kills, base_captures,
                                          minutes, win, loss, commander_win, commander_loss, reported_at, server_id)
                VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15)
                """, matchKey, p.AccountId, p.Civ, p.Score, p.CombatRating, p.Kills, p.Deaths, p.BaseKills, p.BaseCaptures,
                p.Minutes, p.Win ? 1 : 0, p.Loss ? 1 : 0, p.CommanderWin ? 1 : 0, p.CommanderLoss ? 1 : 0, now, serverId))
            { ins.Transaction = tx; ins.ExecuteNonQuery(); }

            // Read-modify-write the per-civ career line (native accumulates the
            // persist score and recomputes rank, FedSrv.CPP:1067-1088).
            int prevScore = 0, prevKills = 0, prevDeaths = 0, prevBaseKills = 0, prevBaseCaptures = 0,
                prevMinutes = 0, prevWins = 0, prevLosses = 0, prevGames = 0, prevCw = 0, prevCl = 0;
            using (var sel = Command(c, "SELECT score,kills,deaths,base_kills,base_captures,minutes,wins,losses,games,commander_wins,commander_losses FROM career_by_civ WHERE account_id=@p0 AND civ=@p1", p.AccountId, p.Civ))
            {
                sel.Transaction = tx;
                using var dr = sel.ExecuteReader();
                if (dr.Read())
                {
                    var r = ReadRow(dr);
                    prevScore = (int)float.Parse(r[0], System.Globalization.CultureInfo.InvariantCulture);
                    prevKills = int.Parse(r[1]); prevDeaths = int.Parse(r[2]);
                    prevBaseKills = (int)float.Parse(r[3], System.Globalization.CultureInfo.InvariantCulture);
                    prevBaseCaptures = (int)float.Parse(r[4], System.Globalization.CultureInfo.InvariantCulture);
                    prevMinutes = int.Parse(r[5]); prevWins = int.Parse(r[6]); prevLosses = int.Parse(r[7]);
                    prevGames = int.Parse(r[8]); prevCw = int.Parse(r[9]); prevCl = int.Parse(r[10]);
                }
            }

            // Cumulative score; rank = highest rung below it (FedSrv.CPP:1067-1078).
            float newScore = prevScore + p.Score;
            short newRank = CareerRankTable.RankFor((int)newScore);

            using (var upsert = Command(c, """
                INSERT INTO career_by_civ(account_id,civ,score,rank,combat_rating,kills,deaths,base_kills,base_captures,
                                          minutes,wins,losses,games,commander_wins,commander_losses)
                VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14)
                ON CONFLICT(account_id,civ) DO UPDATE SET
                  score=@p2, rank=@p3, combat_rating=@p4, kills=@p5, deaths=@p6, base_kills=@p7, base_captures=@p8,
                  minutes=@p9, wins=@p10, losses=@p11, games=@p12, commander_wins=@p13, commander_losses=@p14
                """, p.AccountId, p.Civ, newScore, newRank, p.CombatRating,
                prevKills + p.Kills, prevDeaths + p.Deaths, prevBaseKills + (int)p.BaseKills, prevBaseCaptures + (int)p.BaseCaptures,
                prevMinutes + p.Minutes, prevWins + (p.Win ? 1 : 0), prevLosses + (p.Loss ? 1 : 0), prevGames + 1,
                prevCw + (p.CommanderWin ? 1 : 0), prevCl + (p.CommanderLoss ? 1 : 0)))
            { upsert.Transaction = tx; upsert.ExecuteNonQuery(); }

            // Overall (all-civ) combat rating and last-played timestamp ride the
            // career meta line (native: "Rating is always overall", FedSrv.CPP:1114).
            using (var meta = Command(c, """
                INSERT INTO career_meta(account_id,description,combat_rating,updated_at)
                VALUES(@p0,'',@p1,@p2)
                ON CONFLICT(account_id) DO UPDATE SET combat_rating=@p1, updated_at=@p2
                """, p.AccountId, p.CombatRating, now))
            { meta.Transaction = tx; meta.ExecuteNonQuery(); }

            applied++;
        }

        // P855/SIM-0765 — the squad-game section rides the same transaction and
        // the same per-(server,match) idempotence discipline as the player rows
        // (matchKey is already server-scoped). A replayed report is skipped.
        if (report.SquadGame is { } sg)
        {
            if (squads is null)
                throw new InvalidOperationException("a squad-game section requires the lobby's SquadStore");
            if (!squads.ApplyGameResult(c, tx, matchKey, sg.WinnerSquadId, sg.LoserSquadIds, now)) duplicates++;
        }

        tx.Commit();
        return new(applied, duplicates, skipped);
    }

    /// <summary>Fetch a player's full character info (native
    /// <c>CHARACTER_INFO_GENERAL</c> + one <c>CHARACTER_INFO_BY_CIV</c> per
    /// civilization with career data, <c>Inc/ClubMessages.h:99-102,77-91</c>).</summary>
    public CareerContracts.CareerInfo? GetCareer(string accountId)
    {
        var acct = Row("SELECT id,callsign FROM accounts WHERE id=@p0", accountId);
        if (acct is null) return null;
        var meta = Row("SELECT description,combat_rating FROM career_meta WHERE account_id=@p0", accountId);
        string description = meta?[0] ?? "";
        int rating = meta is { Length: > 1 } && float.TryParse(meta[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var cr) ? (int)cr : 0;

        var civs = new List<CareerContracts.CareerCivStats>();
        using var c = Open();
        using var cmd = Command(c, """
            SELECT civ,rank,score,base_captures,base_kills,combat_rating,minutes,kills,deaths,wins,losses
            FROM career_by_civ WHERE account_id=@p0 ORDER BY civ
            """, accountId);
        using var dr = cmd.ExecuteReader();
        while (dr.Read())
        {
            var r = ReadRow(dr);
            short civ = short.Parse(r[0]);
            short rank = short.Parse(r[1]);
            civs.Add(new CareerContracts.CareerCivStats(
                Civ: civ,
                Rank: rank,
                Score: float.Parse(r[2], System.Globalization.CultureInfo.InvariantCulture),
                BaseCaptures: (int)float.Parse(r[3], System.Globalization.CultureInfo.InvariantCulture),
                BaseKills: (int)float.Parse(r[4], System.Globalization.CultureInfo.InvariantCulture),
                Rating: (int)float.Parse(r[5], System.Globalization.CultureInfo.InvariantCulture),
                MinutesPlayed: int.Parse(r[6]),
                Kills: int.Parse(r[7]),
                Deaths: int.Parse(r[8]),
                Wins: int.Parse(r[9]),
                Losses: int.Parse(r[10]),
                PromotionAt: CareerRankTable.PromotionAt(rank)));
        }
        short allRank = CareerRankTable.RankFor((int)civs.Sum(c => c.Score));
        return new CareerContracts.CareerInfo(accountId, acct[1], description, rating, civs, allRank);
    }

    /// <summary>Set a player's public profile description (native
    /// <c>CHARACTER_INFO_EDIT_DESCRIPTION</c>, <c>Inc/ClubMessages.h:124-126</c>).</summary>
    public void SetDescription(string accountId, string description, long now)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using var cmd = Command(c, """
            INSERT INTO career_meta(account_id,description,combat_rating,updated_at) VALUES(@p0,@p1,0,@p2)
            ON CONFLICT(account_id) DO UPDATE SET description=@p1, updated_at=@p2
            """, accountId, description, now);
        cmd.Transaction = tx; cmd.ExecuteNonQuery(); tx.Commit();
    }

    /// <summary>
    /// Serve the leaderboard (native <c>LEADER_BOARD_QUERY</c> →
    /// <c>LEADER_BOARD_LIST</c>/<c>LEADER_BOARD_QUERY_FAIL</c>,
    /// <c>Inc/ClubMessages.h:320-360</c>). Sorts every career line by the
    /// requested column, computes absolute positions, then returns a window of
    /// up to <c>cLinesPerScreen·3</c> (84) entries centered on the basis
    /// character (native <c>leaderboardscreen.cpp</c> pages by re-querying with
    /// a new basis, :512-519,:554-630).
    /// </summary>
    public CareerContracts.LeaderboardResponse Leaderboard(CareerContracts.LeaderboardQuery q)
    {
        var rows = new List<LeaderboardRow>();
        if (q.Civ == CareerContracts.AllCivs)
        {
            using var c = Open();
            using var cmd = Command(c, """
                SELECT b.account_id, a.callsign, SUM(b.score), SUM(b.minutes), SUM(b.base_kills), SUM(b.base_captures),
                       SUM(b.kills), SUM(b.deaths), MAX(b.combat_rating), SUM(b.wins), SUM(b.losses), SUM(b.games),
                       SUM(b.commander_wins), SUM(b.commander_losses)
                FROM career_by_civ b JOIN accounts a ON a.id=b.account_id
                GROUP BY b.account_id, a.callsign
                """);
            using var dr = cmd.ExecuteReader();
            while (dr.Read())
            {
                var r = ReadRow(dr);
                float score = float.Parse(r[2], System.Globalization.CultureInfo.InvariantCulture);
                rows.Add(new LeaderboardRow
                {
                    AccountId = r[0], Callsign = r[1],
                    Score = score,
                    Minutes = int.Parse(r[3]),
                    BaseKills = (int)float.Parse(r[4], System.Globalization.CultureInfo.InvariantCulture),
                    BaseCaptures = (int)float.Parse(r[5], System.Globalization.CultureInfo.InvariantCulture),
                    Kills = int.Parse(r[6]), Deaths = int.Parse(r[7]),
                    Rating = (short)(int)float.Parse(r[8], System.Globalization.CultureInfo.InvariantCulture),
                    Wins = int.Parse(r[9]), Losses = int.Parse(r[10]), Games = int.Parse(r[11]),
                    CommanderWins = int.Parse(r[12]), CommanderLosses = int.Parse(r[13]),
                    Rank = CareerRankTable.RankFor((int)score),
                });
            }
        }
        else
        {
            using var c = Open();
            using var cmd = Command(c, """
                SELECT b.account_id, a.callsign, b.score, b.minutes, b.base_kills, b.base_captures,
                       b.kills, b.deaths, b.combat_rating, b.wins, b.losses, b.games,
                       b.commander_wins, b.commander_losses, b.rank
                FROM career_by_civ b JOIN accounts a ON a.id=b.account_id WHERE b.civ=@p0
                """, q.Civ);
            using var dr = cmd.ExecuteReader();
            while (dr.Read())
            {
                var r = ReadRow(dr);
                rows.Add(new LeaderboardRow
                {
                    AccountId = r[0], Callsign = r[1],
                    Score = float.Parse(r[2], System.Globalization.CultureInfo.InvariantCulture),
                    Minutes = int.Parse(r[3]),
                    BaseKills = (int)float.Parse(r[4], System.Globalization.CultureInfo.InvariantCulture),
                    BaseCaptures = (int)float.Parse(r[5], System.Globalization.CultureInfo.InvariantCulture),
                    Kills = int.Parse(r[6]), Deaths = int.Parse(r[7]),
                    Rating = (short)(int)float.Parse(r[8], System.Globalization.CultureInfo.InvariantCulture),
                    Wins = int.Parse(r[9]), Losses = int.Parse(r[10]), Games = int.Parse(r[11]),
                    CommanderWins = int.Parse(r[12]), CommanderLosses = int.Parse(r[13]),
                    Rank = short.Parse(r[14]),
                });
            }
        }

        if (rows.Count == 0)
            return new([], null, null, "no players");

        // Sort (native ColumnToSort, leaderboardscreen.cpp:660-708; descending
        // except by name ascending). Positions are assigned in sorted order.
        Comparison<LeaderboardRow> cmp = q.Sort switch
        {
            CareerContracts.LeaderBoardSort.Name => (a, b) => string.Compare(a.Callsign, b.Callsign, StringComparison.OrdinalIgnoreCase),
            CareerContracts.LeaderBoardSort.Rank => (a, b) => b.Rank.CompareTo(a.Rank),
            CareerContracts.LeaderBoardSort.Rating => (a, b) => b.Rating.CompareTo(a.Rating),
            CareerContracts.LeaderBoardSort.TimePlayed => (a, b) => b.Minutes.CompareTo(a.Minutes),
            CareerContracts.LeaderBoardSort.BaseKills => (a, b) => b.BaseKills.CompareTo(a.BaseKills),
            CareerContracts.LeaderBoardSort.BaseCaptures => (a, b) => b.BaseCaptures.CompareTo(a.BaseCaptures),
            CareerContracts.LeaderBoardSort.Kills => (a, b) => b.Kills.CompareTo(a.Kills),
            CareerContracts.LeaderBoardSort.Deaths => (a, b) => b.Deaths.CompareTo(a.Deaths),
            CareerContracts.LeaderBoardSort.Wins => (a, b) => b.Wins.CompareTo(a.Wins),
            CareerContracts.LeaderBoardSort.Losses => (a, b) => b.Losses.CompareTo(a.Losses),
            CareerContracts.LeaderBoardSort.GamesPlayed => (a, b) => b.Games.CompareTo(a.Games),
            CareerContracts.LeaderBoardSort.CommanderWins => (a, b) => b.CommanderWins.CompareTo(a.CommanderWins),
            _ => (a, b) => b.Score.CompareTo(a.Score),   // Score is the default column
        };
        rows.Sort(cmp);

        // Locate the basis (native m_idBasis/m_strBasis, leaderboardscreen.cpp:716-722).
        int basisIdx;
        if (q.BasisAccountId is not null)
            basisIdx = rows.FindIndex(r => string.Equals(r.AccountId, q.BasisAccountId, StringComparison.Ordinal));
        else if (q.Basis is { Length: > 0 })
        {
            basisIdx = rows.FindIndex(r => string.Equals(r.Callsign, q.Basis, StringComparison.OrdinalIgnoreCase));
            if (basisIdx < 0)
                basisIdx = rows.FindIndex(r => r.Callsign.StartsWith(q.Basis!, StringComparison.OrdinalIgnoreCase)); // prefix search
        }
        else
            basisIdx = 0;   // top of the list

        if (basisIdx < 0)
            return new([], EntryNumber1: null, BasisPosition: null, FailReason: q.Basis);

        // Window: up to 84 (cLinesPerScreen·3) entries centered on the basis.
        const int window = 84;
        int start = Math.Max(0, basisIdx - (window / 2));
        int count = Math.Min(window, rows.Count - start);
        var entries = new List<CareerContracts.LeaderboardEntry>(count);
        for (int i = 0; i < count; i++)
        {
            var r = rows[start + i];
            entries.Add(new CareerContracts.LeaderboardEntry(
                AccountId: r.AccountId, Callsign: r.Callsign,
                Position: start + i + 1,
                TotalScore: r.Score, MinutesPlayed: r.Minutes,
                BaseKills: r.BaseKills, BaseCaptures: r.BaseCaptures,
                Kills: r.Kills, Deaths: r.Deaths, Rank: r.Rank, Rating: r.Rating,
                Wins: r.Wins, Losses: r.Losses, GamesPlayed: r.Games,
                CommanderWins: r.CommanderWins, CommanderLosses: r.CommanderLosses));
        }
        var top = new CareerContracts.LeaderboardEntry(
            AccountId: rows[0].AccountId, Callsign: rows[0].Callsign, Position: 1,
            TotalScore: rows[0].Score, MinutesPlayed: rows[0].Minutes,
            BaseKills: rows[0].BaseKills, BaseCaptures: rows[0].BaseCaptures,
            Kills: rows[0].Kills, Deaths: rows[0].Deaths, Rank: rows[0].Rank, Rating: rows[0].Rating,
            Wins: rows[0].Wins, Losses: rows[0].Losses, GamesPlayed: rows[0].Games,
            CommanderWins: rows[0].CommanderWins, CommanderLosses: rows[0].CommanderLosses);
        return new(entries, top, basisIdx + 1, null);
    }

    /// <summary>Internal mutable aggregation row for leaderboard sorting.</summary>
    private sealed class LeaderboardRow
    {
        public string AccountId = ""; public string Callsign = "";
        public float Score; public int Minutes; public int BaseKills; public int BaseCaptures;
        public int Kills; public int Deaths; public short Rank; public short Rating;
        public int Wins; public int Losses; public int Games; public int CommanderWins; public int CommanderLosses;
    }
}
