namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P624 — radar mode (native's TK_CycleRadar LOD cycle, radarimage.h:42-52:
/// All → Default → Target → Minimal, WinTrek.cpp:9955-9975). Native's LOD
/// switch (radarimage.cpp:700-980) mostly controls WHICH contacts get icons /
/// labels / stats and their range clipping; this port condenses it to a
/// per-contact visibility filter (the port radar draws brackets with no label/
/// stat tiers, so the meaningful distinction is which contacts appear).
///
/// Classification (intent preserved, era-complexity reduced — see the P624
/// report's "Native behaviour classified"):
///   All     — every contact (ships, stations, warps, build-spots) as today.
///   Default — every contact as today (the everyday radar; native's default).
///   Target  — only ENEMY ships + anything the pilot has SELECTED; friendly
///             ships and unselected stations/warps hidden (native's Target LOD
///             hides friendly/allied ship icons, radarimage.cpp:758-772).
///   Minimal — only enemy ships + the selected target (native's Minimal keeps
///             only the own ship, radarimage.cpp:724-736 — the port's closest
///             analogue, since the own ship has no self-bracket).
/// Pure so the cycle + filter are unit-tested without a graphics device.
/// </summary>
public enum RadarMode
{
    All = 0,
    Default = 1,
    Target = 2,
    Minimal = 3,
}

public static class RadarLod
{
    public const int Count = 4;

    /// <summary>Advance the mode (wrap All→Default→Target→Minimal→All), the
    /// native <c>(m_radarCockpit + 1) % (c_rlMax + 1)</c> step
    /// (WinTrek.cpp:9961).</summary>
    public static RadarMode Next(RadarMode mode) => (RadarMode)(((int)mode + 1) % Count);

    /// <summary>Short label, matching native's c_szRadarLODs (radarimage.cpp:2-5).</summary>
    public static string Label(RadarMode mode) => mode switch
    {
        RadarMode.All => "All",
        RadarMode.Default => "Default",
        RadarMode.Target => "Target",
        _ => "Minimal",
    };

    /// <summary>Whether a SHIP contact appears in this mode. Target/Minimal hide
    /// friendly ships, keeping enemies (native's allied-hiding Target LOD) plus
    /// anything the pilot has selected.</summary>
    public static bool ShowShip(RadarMode mode, bool isEnemy, bool isSelected) => mode switch
    {
        RadarMode.All or RadarMode.Default => true,
        _ => isEnemy || isSelected,
    };

    /// <summary>Whether a station/warp/build-spot contact appears. Target/Minimal
    /// keep only the selected target (native hides unlabelled non-friendly
    /// objects, radarimage.cpp:837-951).</summary>
    public static bool ShowTargetable(RadarMode mode, bool isSelected) => mode switch
    {
        RadarMode.All or RadarMode.Default => true,
        _ => isSelected,
    };
}
