using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P639 — pure (MonoGame-free) layout of the LEADERBOARD screen, the upgraded
/// in-mission scoreboard. Turns the derived <c>ScoreRosterEntry</c> list from
/// <c>ClientWorld.ScoreRoster</c> (P774: built from PLAYERINFO + KILL_SHIP) plus
/// the <c>RANK_INFO(13)</c> rank-name table
/// (<c>ClientWorld.RankTable</c>) into aligned, pure-ASCII table cells that the
/// <see cref="ScoreboardOverlayRenderer"/> draws.
///
/// <para><b>Native fidelity.</b> The native career ladder
/// (<c>WinTrek/leaderboardscreen.cpp</c>) lists a player's
/// <c>colPosition, colPlayerName, colRank, colOverallRating, colCombatRating,
/// colGamesPlayed, colTimePlayed, colCommanderWins, colWins, colLosses,
/// colKills, colDeaths, colBaseKills, colBaseCaptures</c>
/// (leaderboardscreen.cpp:54-67). Those career columns come from the Zone Club
/// stats database (<c>REQ_LEADERBOARD</c> → club meta-server) that this port
/// does not have (no persistence / no Zone Club backend — see P129/P275). So
/// the port's leaderboard shows the <b>live session</b> half of that grid —
/// position, name, rank, overall rating (= score), combat rating, kills,
/// deaths, base kills, base captures — sourced entirely from real wire data,
/// and marks the career half (games/time played, wins/losses, commander wins)
/// as an <i>era/infra constraint</i> (classified in the report).</para>
///
/// <para><b>Rank.</b> Each row's rank name comes from the server's rank table
/// via <see cref="RankNameLookup"/> (a faithful port of
/// <c>BaseClient::LookupRankName</c>, clintlib.cpp:2670-2710), turning the raw
/// <c>ScoreRosterEntry.Rank</c> id into the displayed rank ("Novice 3",
/// "Veteran 2", …).</para>
///
/// <para><b>Ordering.</b> A leaderboard reads best grouped by team with the
/// best overall rating on top: rows sort by <c>SideId</c> ascending, then
/// <c>Score</c> descending (native's default lbSortScore basis — overall
/// rating), ties broken by name. <c>Position</c> is the player's rank within
/// their own side by that same ordering.</para>
///
/// <para><b>ASCII safety.</b> The HUD spritefonts bake ASCII 32..126 only
/// (P214/p217 lesson), so every cell this produces must be pure printable
/// ASCII — the side is rendered as the numeric id, and any non-ASCII name char
/// is scrubbed.</para>
/// </summary>
public static class ScoreboardLayout
{
    /// <summary>Vertical gap between the header and the first row, in px.</summary>
    public const float HeaderGap = 10f;

    /// <summary>Row height for one roster line, in px.</summary>
    public const float RowHeight = 20f;

    /// <summary>Horizontal padding between the panel edge and the first column, in px.</summary>
    public const float Padding = 20f;

    /// <summary>Extra horizontal gap inserted between adjacent columns, in px.</summary>
    public const float ColumnGap = 24f;

    /// <summary>One leaderboard row, ready to draw: the in-side position, the
    /// player name, the rendered side label, the rank name and the live score
    /// columns (overall rating, combat rating, kills, deaths, base kills, base
    /// captures).</summary>
    public readonly record struct Row(
        string Position, string Name, string SideLabel, string RankName,
        string Score, string CombatRating, string Kills, string Deaths,
        string BaseKills, string BaseCaptures)
    {
        public const string HeaderPosition = "#";
        public const string HeaderName = "NAME";
        public const string HeaderSide = "SIDE";
        public const string HeaderRank = "RANK";
        public const string HeaderScore = "SCORE";
        public const string HeaderCombat = "COMBAT";
        public const string HeaderKills = "K";
        public const string HeaderDeaths = "D";
        public const string HeaderBaseKills = "BK";
        public const string HeaderBaseCaptures = "BC";

        public const int ColumnCount = 10;
    }

    /// <summary>
    /// Build the ordered, ASCII-safe rows from a wire roster snapshot + the
    /// server's rank-name table. Inputs are left unsorted-trusted; this applies
    /// the leaderboard ordering above (side asc, score desc, name asc) and
    /// numbers each player's position within their own side. When
    /// <paramref name="isSideActive"/> is supplied, a row whose side is inactive
    /// (an eliminated side, P455b) is marked "<c>(DESTROYED)</c>" on its side
    /// label — exactly native's team list (teamscreen.cpp:318).
    /// </summary>
    public static IReadOnlyList<Row> Build(
        IReadOnlyList<ScoreRosterEntry> entries,
        IReadOnlyList<RankInfoEntry> rankTable,
        Func<short, bool>? isSideActive = null)
    {
        // Group by side; number positions within each side (best score first).
        var grouped = entries
            .GroupBy(e => e.SideId)
            .OrderBy(g => g.Key);
        var rows = new List<Row>();
        foreach (var g in grouped)
        {
            int position = 0;
            foreach (var e in g.OrderByDescending(e => e.Score).ThenBy(e => e.CharacterName, StringComparer.Ordinal))
            {
                position++;
                rows.Add(new Row(
                    Position: position.ToString(),
                    Name: AsciiSafe(e.CharacterName),
                    SideLabel: isSideActive is not null && !isSideActive(e.SideId)
                        ? $"S{e.SideId}(DESTROYED)"
                        : $"S{e.SideId}",
                    RankName: AsciiSafe(RankNameLookup.Lookup(rankTable, e.Rank)),
                    Score: e.Score.ToString("F0"),
                    CombatRating: e.CombatRating.ToString("F1"),
                    Kills: e.Kills.ToString(),
                    Deaths: e.Deaths.ToString(),
                    BaseKills: e.BaseKills.ToString(),
                    BaseCaptures: e.BaseCaptures.ToString()));
            }
        }
        return rows;
    }

    /// <summary>The ten column widths (in px at font scale 1) for the given rows:
    /// position, name, side, rank, score, combat, kills, deaths, baseKills,
    /// baseCaptures — each at least as wide as its header label.</summary>
    public static (float Position, float Name, float Side, float Rank, float Score,
        float Combat, float Kills, float Deaths, float BaseKills, float BaseCaptures)
        ColumnWidths(IReadOnlyList<Row> rows, Func<string, float> measure)
    {
        float position = measure(Row.HeaderPosition);
        float name = measure(Row.HeaderName);
        float side = measure(Row.HeaderSide);
        float rank = measure(Row.HeaderRank);
        float score = measure(Row.HeaderScore);
        float combat = measure(Row.HeaderCombat);
        float kills = measure(Row.HeaderKills);
        float deaths = measure(Row.HeaderDeaths);
        float bk = measure(Row.HeaderBaseKills);
        float bc = measure(Row.HeaderBaseCaptures);
        foreach (var r in rows)
        {
            position = MathF.Max(position, measure(r.Position));
            name = MathF.Max(name, measure(r.Name));
            side = MathF.Max(side, measure(r.SideLabel));
            rank = MathF.Max(rank, measure(r.RankName));
            score = MathF.Max(score, measure(r.Score));
            combat = MathF.Max(combat, measure(r.CombatRating));
            kills = MathF.Max(kills, measure(r.Kills));
            deaths = MathF.Max(deaths, measure(r.Deaths));
            bk = MathF.Max(bk, measure(r.BaseKills));
            bc = MathF.Max(bc, measure(r.BaseCaptures));
        }
        return (position, name, side, rank, score, combat, kills, deaths, bk, bc);
    }

    /// <summary>Strip any character outside printable ASCII 32..126 (the
    /// spritefont-baked range), replacing it with a space so the renderer never
    /// throws on a hostile/mangled name (P214/p217 lesson).</summary>
    public static string AsciiSafe(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        int n = 0;
        foreach (char c in s)
        {
            if (c >= 32 && c <= 126) buf[n++] = c;
            else buf[n++] = ' ';
        }
        return new string(buf[..n]);
    }
}
