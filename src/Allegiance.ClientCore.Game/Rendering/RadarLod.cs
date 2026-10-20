namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P624 / HUD-4 — radar mode (native's TK_CycleRadar LOD cycle,
/// radarimage.h:42-52: All → Default → Target → Minimal, WinTrek.cpp:9955-9975).
/// The icon-presence half of native's LOD switch (radarimage.cpp:700-980) is
/// ported per contact kind; the label/stat tiers that native layers on top are
/// separate parity rows and remain partial. Minimal keeps only the own ship and
/// important-mask contacts (the port tracks the selected target as its only
/// important mask), matching radarimage.cpp:724-736.
///
/// Native's fourth on-screen label is "None" although the enum is c_rlMinimal
/// (c_szRadarLODs, radarimage.cpp:2-5).
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

    /// <summary>rangeClipLabels, radarimage.cpp:15 — the 1000 m LOD clip native
    /// applies to pedestal stations, enemy probes/mines in Target, treasure in
    /// Target, asteroids, and off-screen same-side ships' range.</summary>
    public const float RangeClip = 1000f;

    /// <summary>Advance the mode (wrap All→Default→Target→Minimal→All), the
    /// native <c>(m_radarCockpit + 1) % (c_rlMax + 1)</c> step
    /// (WinTrek.cpp:9961).</summary>
    public static RadarMode Next(RadarMode mode) => (RadarMode)(((int)mode + 1) % Count);

    /// <summary>Short label, matching native's c_szRadarLODs (radarimage.cpp:2-5);
    /// the fourth mode is labelled "None" by native.</summary>
    public static string Label(RadarMode mode) => mode switch
    {
        RadarMode.All => "All",
        RadarMode.Default => "Default",
        RadarMode.Target => "Target",
        _ => "None",
    };

    /// <summary>Whether an ordinary (non-important) contact keeps its icon in a
    /// sparse mode. Native Minimal suppresses every non-self, non-important icon
    /// (radarimage.cpp:724-736); Target/Default/All apply per-kind rules below.</summary>
    private static bool MinimalVisible(bool isSelf, bool isSelected) => isSelf || isSelected;

    /// <summary>Ship icon presence. Target hides friendly/allied ships; Minimal
    /// keeps only self and the selected target (radarimage.cpp:749-772).</summary>
    public static bool ShowShip(RadarMode mode, bool ownOrAllied, bool isSelf, bool isSelected)
    {
        if (mode == RadarMode.Minimal) return MinimalVisible(isSelf, isSelected);
        if (isSelected || isSelf) return true;
        return mode switch
        {
            RadarMode.All or RadarMode.Default => true,
            RadarMode.Target => !ownOrAllied,
            _ => false,
        };
    }

    /// <summary>Station icon presence. Pedestal stations clip at 1000 m in every
    /// mode; Target hides an own/allied off-screen station (radarimage.cpp:782-810).</summary>
    public static bool ShowStation(RadarMode mode, bool pedestal, bool ownOrAllied, bool onScreen, float separation, bool isSelected)
    {
        if (mode == RadarMode.Minimal) return isSelected;
        if (isSelected) return true;
        if (pedestal) return separation < RangeClip;
        return mode switch
        {
            RadarMode.All or RadarMode.Default => true,
            RadarMode.Target => !ownOrAllied || onScreen,
            _ => false,
        };
    }

    /// <summary>Warp icon presence. Warps stay visible in All/Default/Target
    /// (only their range clips in Target), and follow the Minimal/important gate
    /// otherwise (radarimage.cpp:952-963).</summary>
    public static bool ShowWarp(RadarMode mode, bool isSelected)
    {
        if (mode == RadarMode.Minimal) return isSelected;
        return true;
    }

    /// <summary>Missile icon presence. Outside All only missiles aimed at the
    /// player's own side are shown (radarimage.cpp:819-832).</summary>
    public static bool ShowMissile(RadarMode mode, bool aimsAtOwnSide, bool isSelected)
    {
        if (mode == RadarMode.Minimal) return isSelected;
        if (isSelected) return true;
        return mode switch
        {
            RadarMode.All => true,
            _ => aimsAtOwnSide,
        };
    }

    /// <summary>Probe/mine icon presence. Target keeps only enemy probes/mines
    /// within the 1000 m clip (radarimage.cpp:834-873).</summary>
    public static bool ShowProbeOrMine(RadarMode mode, bool ownOrAllied, float separation, bool isSelected)
    {
        if (mode == RadarMode.Minimal) return isSelected;
        if (isSelected) return true;
        return mode switch
        {
            RadarMode.All or RadarMode.Default => true,
            RadarMode.Target => !ownOrAllied && separation < RangeClip,
            _ => false,
        };
    }

    /// <summary>Treasure icon presence. Not-useful treasure is hidden outside
    /// All; useful treasure clips at 1000 m in Target (radarimage.cpp:877-900).
    /// <paramref name="useful"/> is true for both useful parts and tech.</summary>
    public static bool ShowTreasure(RadarMode mode, bool useful, float separation, bool isSelected)
    {
        if (mode == RadarMode.Minimal) return isSelected;
        if (isSelected) return true;
        if (!useful) return mode == RadarMode.All;
        return mode switch
        {
            RadarMode.All or RadarMode.Default => true,
            RadarMode.Target => separation < RangeClip,
            _ => false,
        };
    }

    /// <summary>Asteroid icon presence. Generic rocks show only within the 1000 m
    /// clip (and not in command view); named special rocks show in All/Default
    /// regardless, and everything clips in Target (radarimage.cpp:903-930).</summary>
    public static bool ShowAsteroid(RadarMode mode, bool namedSpecial, bool rangeZero, float separation, bool isSelected)
    {
        if (mode == RadarMode.Minimal) return isSelected;
        if (isSelected) return true;
        bool withinClip = separation < RangeClip && !rangeZero;
        return mode switch
        {
            RadarMode.All or RadarMode.Default => namedSpecial || withinClip,
            RadarMode.Target => withinClip,
            _ => false,
        };
    }
}
