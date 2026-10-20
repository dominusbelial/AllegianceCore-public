using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P752 — pure, headless-testable port of the starmap's per-sector threat
/// presentation, so every native branch is pinned by a unit test instead of
/// living only inside the renderer's draw code.
///
/// Native <c>SectorMapPane::Render</c> (<c>WinTrek/sectormap.cpp:1063-1082</c>)
/// collapses each sector's <c>ClusterWarning</c> into exactly one flashing icon
/// through a threshold cascade — the FIRST matching branch wins:
/// <code>
///   warn >= c_cwStationThreatened (13)  -> sectorwarning icon
///   warn >= c_cwCapitalInCluster (10)   -> sectorcapital icon
///   warn >= c_cwBomberInCluster   ( 8)  -> sectorbomber icon
///   warn >= c_cwMinerThreatened    ( 5)  -> sectorminer icon
///   warn >= c_cwCombatInCluster    ( 4)  -> sectorcombat icon
///   (anything lower — 1..3 — draws NO icon)
/// </code>
/// The same text (<c>GetClusterWarningText</c>, common.cpp) is drawn by the
/// info pane while the pointer is over the sector (sectormap.cpp:517-534) and
/// posted as the one-shot alert line on a warning increase (trekigc.cpp:349).
/// The port's alert feed and per-sector warning state are P374's
/// <see cref="ClusterWarnings"/>; this class owns only the presentation tier.
/// </summary>
public static class StarmapThreatPresentation
{
    /// <summary>The distinct icon tiers native's cascade can produce, ordered
    /// exactly by the native threshold checks.</summary>
    public enum Tier { None, Combat, Miner, Bomber, Capital, Station }

    /// <summary>
    /// The icon tier a sector with warning level <paramref name="warning"/>
    /// draws — native's threshold cascade (sectormap.cpp:1066-1082). Levels
    /// 1..3 (enemy fighter/miner/builder merely present) draw no icon at all.
    /// </summary>
    public static Tier IconTier(short warning)
    {
        if (warning >= ClusterWarnings.StationThreatened) return Tier.Station;
        if (warning >= ClusterWarnings.CapitalInCluster) return Tier.Capital;
        if (warning >= ClusterWarnings.BomberInCluster) return Tier.Bomber;
        if (warning >= ClusterWarnings.MinerThreatened) return Tier.Miner;
        if (warning >= ClusterWarnings.CombatInCluster) return Tier.Combat;
        return Tier.None;
    }

    /// <summary>
    /// The alert/warning text for a sector (native <c>GetClusterWarningText</c>,
    /// drawn by the info pane on hover, sectormap.cpp:523-534). Empty for
    /// <see cref="ClusterWarnings.NoThreat"/> and for any unrecognized level —
    /// never a crash on a weird wire value.
    /// </summary>
    public static string WarningText(short warning)
        => warning > ClusterWarnings.NoThreat ? ClusterWarnings.GetWarningText(warning) : "";
}
