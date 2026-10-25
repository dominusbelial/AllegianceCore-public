using System.Globalization;

namespace Allegiance.ClientCore.MonoGame.Career;

/// <summary>
/// Pure, unit-testable model of the character-info screen — the port of native
/// <c>WinTrek/CharInfoScreen.cpp</c>: per-civilization career stats with the
/// civilization selector (<c>:717-762</c>) and the read-only ⇄ edit profile
/// description toggle (<c>:837-877</c>). Kept free of MonoGame so every path is
/// testable headless (the task's "client screen model tests").
/// </summary>
public sealed class CharacterInfoModel
{
    /// <summary>Per-civ career lines from <c>CareerApiClient.GetCareerAsync</c>.</summary>
    public IReadOnlyList<CareerDto.CareerCivStats> Civs { get; private set; } = [];

    /// <summary>Currently selected civilization index (native m_pcomboCiv selection).</summary>
    public int SelectedCiv { get; private set; }

    /// <summary>The player's callsign (native CHAR_INFO_GENERAL name).</summary>
    public string Callsign { get; private set; } = "";

    /// <summary>The profile description (native m_pstringCharDescription / edit pane).</summary>
    public string Description { get; private set; } = "";

    /// <summary>Whether the description is in edit mode (native OnButtonEdit/:863-877).</summary>
    public bool Editing { get; private set; }

    /// <summary>The civ name shown for the "all civs / overall" row.</summary>
    public const string AllCivsLabel = "All";

    /// <summary>The civ combo entries: an "All civs" row followed by each played civ.</summary>
    public IReadOnlyList<string> CivNames { get; private set; } = [];

    public void Load(CareerDto.CareerInfo info)
    {
        Callsign = info.Callsign;
        Description = info.Description;
        Civs = info.Civs;
        var names = new List<string> { AllCivsLabel };
        names.AddRange(info.Civs.Select(c => "Civ " + c.Civ.ToString(CultureInfo.InvariantCulture)));
        CivNames = names;
        if (SelectedCiv >= CivNames.Count) SelectedCiv = 0;
    }

    /// <summary>Select a civilization — native <c>OnCivChange</c>
    /// (<c>CharInfoScreen.cpp:717-762</c>).</summary>
    public CareerDto.CareerCivStats? SelectCiv(int index)
    {
        SelectedCiv = Math.Clamp(index, 0, Math.Max(0, CivNames.Count - 1));
        return SelectedStats;
    }

    /// <summary>The stats to display for the current selection. The "All civs"
    /// row is the sum of all civilizations (native <c>civid=-1 ⇒ sum of all civs</c>,
    /// <c>Inc/ClubMessages.h:79</c>).</summary>
    public CareerDto.CareerCivStats? SelectedStats
    {
        get
        {
            if (Civs.Count == 0) return null;
            if (SelectedCiv == 0) return Sum(Civs);
            int i = SelectedCiv - 1;
            return i < Civs.Count ? Civs[i] : null;
        }
    }

    /// <summary>Native <c>m_pstringRank</c>: the rank name for the selected civ
    /// (<c>CharInfoScreen.cpp:739</c>, <c>LookupRankName</c> clintlib.cpp:2670-2710).</summary>
    public string RankName => SelectedStats is { } s ? CareerRanks.Name(s.Rank) : "";

    /// <summary>Enter edit mode — native <c>OnButtonEdit</c>
    /// (<c>CharInfoScreen.cpp:863-877</c>).</summary>
    public void BeginEdit() => Editing = true;

    /// <summary>Leave edit mode and lock the description — native
    /// <c>OnButtonApply</c> (<c>CharInfoScreen.cpp:837-859</c>).</summary>
    public void ApplyEdit()
    {
        Editing = false;
    }

    private static CareerDto.CareerCivStats Sum(IReadOnlyList<CareerDto.CareerCivStats> civs) => new(
        Civ: CareerDto.AllCivs,
        Rank: (short)(civs.Count == 0 ? 0 : 0),
        Score: civs.Sum(c => c.Score),
        BaseCaptures: civs.Sum(c => c.BaseCaptures),
        BaseKills: civs.Sum(c => c.BaseKills),
        Rating: civs.Count == 0 ? 0 : civs.Max(c => c.Rating),
        MinutesPlayed: civs.Sum(c => c.MinutesPlayed),
        Kills: civs.Sum(c => c.Kills),
        Deaths: civs.Sum(c => c.Deaths),
        Wins: civs.Sum(c => c.Wins),
        Losses: civs.Sum(c => c.Losses),
        PromotionAt: null);
}
