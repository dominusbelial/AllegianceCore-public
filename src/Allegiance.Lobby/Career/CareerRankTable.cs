namespace Allegiance.Lobby.Career;

/// <summary>
/// The lobby's per-civilization rank ladder — the port of native's club
/// <c>Ranks</c> table that drives <c>SetCharStats</c>'s rank assignment
/// (<c>FedSrv/FedSrv.CPP:1062-1103</c>). Native stores one <c>RankInfo</c>
/// entry per (civ, rank) with a cumulative <c>requiredRanking</c> score
/// threshold (<c>Inc/Messages.h:139-145</c>, <c>Inc/ClubMessages.h:364-370</c>),
/// and a player's rank for a civilization is the highest rank whose
/// <c>requiredRanking &lt; int(score)</c> (the ascending-threshold scan in
/// <c>SetCharStats</c>).
/// </summary>
/// <remarks>
/// The exact <c>requiredRanking</c> seed values come from the native club
/// server's SQL <c>Ranks</c> table (the operator-owned club database, not part
/// of the MSFA game-server source this repo ports). The rank <em>names</em>
/// follow the native <c>ranks.txt</c> convention the game server already loads
/// (40 levels, <c>Server.Lifecycle.cs:765-771</c>, <c>RankInfo.cs:125-153</c>).
/// The thresholds below are a geometric escalation over the same score scale
/// native's <c>CalculateScore</c> produces, configurable by an operator who has
/// the true club seed. The <em>mechanism</em> — cumulative persist score, then
/// highest-threshold-below-score rank, per civ and overall — is native's
/// exactly (<c>FedSrv.CPP:1067-1103</c>).
/// </remarks>
public static class CareerRankTable
{
    /// <summary>A single rung of the ladder: achieving <see cref="RequiredRanking"/>
    /// (strictly less than) confers <see cref="Rank"/> with <see cref="Name"/>.</summary>
    public readonly record struct Rung(int Rank, int RequiredRanking, string Name);

    /// <summary>The civ-agnostic overall (<c>NA</c>) ladder shares the same
    /// thresholds as each civ in this port (native loads one <c>Ranks</c> set
    /// and keys entries by civ; the port treats all civs identically).</summary>
    public static readonly Rung[] Ladder =
    {
        new(0, 0, "Recruit"),
        new(1, 100, "Cadet"),
        new(2, 400, "Ensign"),
        new(3, 1_200, "Lieutenant Junior Grade"),
        new(4, 2_800, "Lieutenant"),
        new(5, 5_500, "Lieutenant Commander"),
        new(6, 9_500, "Commander"),
        new(7, 15_000, "Captain"),
        new(8, 22_000, "Commodore"),
        new(9, 32_000, "Rear Admiral"),
        new(10, 45_000, "Vice Admiral"),
        new(11, 62_000, "Admiral"),
        new(12, 85_000, "Fleet Admiral"),
        new(13, 115_000, "Grand Admiral"),
        new(14, 155_000, "Master 1"),
        new(15, 205_000, "Master 2"),
        new(16, 270_000, "Master 3"),
        new(17, 350_000, "Master 4"),
        new(18, 450_000, "Master 5"),
        new(19, 575_000, "Master 6"),
        new(20, 730_000, "Master 7"),
        new(21, 920_000, "Master 8"),
        new(22, 1_150_000, "Master 9"),
    };

    /// <summary>
    /// Highest rank whose <c>requiredRanking &lt; score</c> — native's
    /// ascending-threshold scan collapsed to a binary lookup. Native iterates
    /// <c>g.vRankInfo</c> from the top and takes the first
    /// <c>ri.requiredRanking &lt; iScore</c> (<c>FedSrv.CPP:1070-1078</c>,
    /// <c>:1095-1103</c>), which is the highest rung below the score.
    /// </summary>
    public static short RankFor(int score)
    {
        short rank = 0;
        foreach (var rung in Ladder)
            if (rung.RequiredRanking < score)
                rank = (short)rung.Rank;
        return rank;
    }

    /// <summary>The <c>requiredRanking</c> of the next rung above
    /// <paramref name="rank"/> — native's <c>nPromotionAt</c> for the
    /// character-info screen (<c>WinTrek/CharInfoScreen.cpp:563</c>,
    /// <c>Inc/ClubMessages.h:90</c>). Returns <c>null</c> at the top rung.</summary>
    public static int? PromotionAt(short rank)
    {
        foreach (var rung in Ladder)
            if (rung.Rank == rank + 1)
                return rung.RequiredRanking;
        return null;
    }

    /// <summary>Rank name for <paramref name="rank"/> (native
    /// <c>BaseClient::LookupRankName</c>, <c>clintlib.cpp:2670-2710</c>:
    /// clamp to the top rung).</summary>
    public static string Name(short rank)
    {
        if (rank >= Ladder.Length) rank = (short)(Ladder.Length - 1);
        return rank < 0 ? "" : Ladder[rank].Name;
    }
}
