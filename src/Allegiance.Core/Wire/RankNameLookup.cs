using System;

namespace Allegiance.Core.Wire;

/// <summary>
/// P639 — client-side rank-name resolution from the <c>RANK_INFO(13)</c>
/// table, a faithful port of native <c>BaseClient::LookupRankName</c>
/// (<c>src/clintlib/clintlib.cpp:2670-2710</c>).
///
/// <para>Native derives a rank display name from a <c>RankID</c> plus the
/// per-civ rank-name table <c>m_vRankInfo</c> captured from
/// <c>FM_S_RANK_INFO</c> (<c>appmsg.cpp:3372-3382</c>). The relevant rules
/// (all citable):</para>
/// <list type="bullet">
/// <item><b>Empty table</b> (<c>m_cRankInfo &lt;= 0</c> and not Zone Club):
/// native asserts and returns an empty string (<c>clintlib.cpp:2678-2682</c>).</item>
/// <item><b>rank &gt;= 0 and rank &lt; count</b>: the name is
/// <c>m_vRankInfo[rank].RankName</c> (<c>clintlib.cpp:2685-2688</c>).</item>
/// <item><b>rank &gt;= count</b> (a rank above the table's ceiling): clamp to the
/// highest entry, <c>m_vRankInfo[count-1].RankName</c>
/// (<c>clintlib.cpp:2689-2693</c>).</item>
/// <item><b>rank &lt; 0</b>: the "Unknown (%d)" template is wsprintf'd with
/// <c>rank - nClosestRank + 1</c>, nClosestRank = -1, i.e. <c>rank + 2</c>
/// (<c>clintlib.cpp:2672,2694-2698</c>). Unreachable for a real player
/// (their rank is 0..40), kept faithful for completeness.</item>
/// </list>
/// The trailing spaces native strips with <c>RemoveTrailingSpaces</c>
/// (<c>clintlib.cpp:2699</c>) are removed with a TrimEnd here.
/// </summary>
public static class RankNameLookup
{
    /// <summary>
    /// Resolve a rank id to its display name using the server-provided rank
    /// table (the <see cref="RankInfo.Ranks"/> of the last received
    /// <c>RANK_INFO(13)</c>). Returns "" when the table is empty, mirroring
    /// native's early-out; callers that want a fallback may treat "" as
    /// "no rank data received yet".
    /// </summary>
    public static string Lookup(IReadOnlyList<RankInfoEntry> table, short rank)
    {
        if (table.Count <= 0) return "";
        string name;
        if (rank >= 0)
        {
            name = rank < table.Count
                ? table[rank].RankName
                : table[table.Count - 1].RankName;
        }
        else
        {
            name = $"Unknown ({rank + 2})";
        }
        return name.TrimEnd();
    }

    /// <summary>Convenience overload over a raw array (the stored table shape).</summary>
    public static string Lookup(RankInfoEntry[] table, short rank) =>
        Lookup((IReadOnlyList<RankInfoEntry>)table, rank);
}
