using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1038 — the Jet stores block (P972 §4.3 "stores", §5.1 own-ship rows, phase 3B).
/// Pure geometry and gating rules only: the block is a compact grouping of the values
/// the shared model already resolves (<see cref="HudGauge"/>, <see cref="HudOwnStatus"/>,
/// <see cref="HudFrame.WeaponSystems"/>), so a renderer cannot invent a reading.
/// <para><b>Placement.</b> Bottom-left, one column right of the speed tape
/// (<c>96·scale</c>), with its bottom edge a fixed <see cref="BottomInset"/> px above the
/// viewport bottom. The two shared bottom-left text lines (native cargo at
/// <c>h−132</c>, flight parts at <c>h−158</c>, both unscaled) sit in that band, so the
/// block is raised clear of them; the speed tape owns <c>x≈46…76</c> at 1080p, the target
/// data block owns <c>w/2+70</c>, the radar/mini-map the bottom-right corner and the chat
/// pane the top centre, so nothing else can be under it.</para>
/// <para><b>State without a second hue.</b> A store's state is its word (<c>A</c>,
/// <c>READY</c>, <c>NOT READY</c>) and its bar, never an extra colour (P972 §4.1).</para>
/// </summary>
public static class JetStoresPanel
{
    /// <summary>Left column of the block, in standard-stroke units (× scale): clear of
    /// the speed tape's tick labels (which end at ≈76 px at 1080p).</summary>
    public const float LeftColumn = 96f;

    /// <summary>Fixed distance (px, NOT scaled) from the viewport bottom to the block's
    /// bottom edge: below the speed readout plate (centred on the boresight row) and above
    /// the two shared bottom-left text lines, which are drawn at fixed offsets (h−158 and
    /// h−132, <c>Game1.InFlightUtility.cs</c>, <c>Game1.FlightParts.cs</c>) rather than at
    /// the HUD scale.</summary>
    public const float BottomInset = 166f;

    /// <summary>Row pitch in standard-stroke units (× scale). One text line (12 px) plus
    /// a segmented bar (3 px) fits inside a row.</summary>
    public const float RowPitch = 18f;

    /// <summary>P1038b — the minimum free gap between two store rows, in standard-stroke
    /// units (× scale). The row pitch is the MEASURED line height plus this gap
    /// (<see cref="RowHeight(float, float)"/>), so two rows can never touch however tall
    /// the HUD font is at the current density (the P1038 defect: 1080p rows merged on a
    /// bright backdrop).</summary>
    public const float RowGap = 3f;

    /// <summary>P1038b — the gap between a store's value and the segmented bar drawn
    /// beside it, in standard-stroke units (× scale). The bar never runs across a glyph
    /// (P1038 defect: HULL/SHLD bars struck through their own words).</summary>
    public const float BarGap = 6f;

    /// <summary>Segmented-bar geometry (physical px per segment at scale 1). The bar is
    /// eight segments with 1 px gaps: fill is shape, not a second colour.</summary>
    public const float BarWidth = 64f;
    public const float BarHeight = 3f;
    public const int BarSegments = 8;

    /// <summary>Rows the compact (800×480) block keeps — P972 §4.5 "stores collapse to
    /// three readout rows". Annunciators ride on the last row so no parity element is
    /// silently dropped.</summary>
    public const int CompactRows = 3;

    /// <summary>Rows the full block uses: HULL, SHLD, ENG, FUEL/OXY, GUN, MSL, KB/SIG,
    /// annunciators.</summary>
    public const int FullRows = 8;

    /// <summary>The shared low-resource threshold: <see cref="HudModel"/> tints a gauge
    /// red at fraction ≤ 0.5 (<c>HudModel.cs:440</c>).</summary>
    public const float LowFraction = 0.5f;

    /// <summary>The shared critical-resource threshold: the Modern renderer's <c>!</c>
    /// badge (<c>ModernHudRenderer.cs:166</c>) — ≤ 0.15 for every store, ≤ 0.05 for
    /// energy, exactly the same numbers the shared renderers use.</summary>
    public const float CriticalFraction = 0.15f;
    public const float CriticalEnergyFraction = 0.05f;

    /// <summary>The compact layout floor, matching the P1036 tape/ladder rule.</summary>
    public const float CompactHeight = 700f;

    public static bool Compact(float viewportHeight) => viewportHeight < CompactHeight;

    public static int RowCount(bool compact) => compact ? CompactRows : FullRows;

    public static float Left(float scale) => LeftColumn * scale;

    /// <summary>The block's bottom edge (screen y, +Y down).</summary>
    public static float Bottom(float viewportHeight) => viewportHeight - BottomInset;

    /// <summary>P1038b — the pitch of one row: the MEASURED text line height plus
    /// <see cref="RowGap"/> (never below <see cref="RowPitch"/>), so consecutive rows keep a
    /// real gap at every font density instead of merging.</summary>
    public static float RowHeight(float textHeight, float scale)
        => MathF.Max(RowPitch * scale, textHeight + RowGap * scale);

    /// <summary>Top edge of row <paramref name="index"/> (0 = first row under the
    /// header). Rows run downward from the header and the block stays bottom-anchored, so
    /// a taller measured row pushes the header up rather than off the viewport.</summary>
    public static float RowTop(float viewportHeight, bool compact, float rowHeight, int index)
        => Bottom(viewportHeight) - (RowCount(compact) - index) * rowHeight;

    /// <summary>Top edge of the <c>STORES</c> header, one measured row above row 0.</summary>
    public static float HeaderTop(float viewportHeight, bool compact, float rowHeight)
        => RowTop(viewportHeight, compact, rowHeight, 0) - rowHeight;

    /// <summary>P1038b — the vertical centre-line of a segmented bar that sits beside a
    /// row's value: the row's text line centred on the bar's own height.</summary>
    public static float BarY(float rowTop, float textHeight, float height)
        => rowTop + (textHeight - height) * 0.5f;

    /// <summary>True when a gauge's fraction is at the shared critical threshold
    /// (<paramref name="kind"/> ENERGY keeps its own lower threshold).</summary>
    public static bool IsCritical(GaugeKind kind, float fraction)
        => fraction <= (kind == GaugeKind.Energy ? CriticalEnergyFraction : CriticalFraction);

    /// <summary>True when a gauge's fraction is merely low (the shared red threshold).</summary>
    public static bool IsLow(GaugeKind kind, float fraction)
        => kind != GaugeKind.Thrust && fraction <= LowFraction;

    /// <summary>The store word for a resident gauge (P972 §5.1): the native gauge label
    /// mapped to the Jet stores vocabulary. FUEL becomes the pod's OXY because
    /// <see cref="HudModel"/> already relabels the pod's fuel gauge ("OXY") while keeping
    /// the same fraction.</summary>
    public static string StoreWord(GaugeKind kind) => kind switch
    {
        GaugeKind.Hull => "HULL",
        GaugeKind.Shield => "SHLD",
        GaugeKind.Energy => "ENG",
        GaugeKind.Fuel => "FUEL",
        GaugeKind.Ammo => "GUN",
        _ => "THR",
    };

    /// <summary>The store word for a pod's fuel slot: the shared gauge label is already
    /// "OXY" for a lifepod (<see cref="HudModel"/>), so the fraction is carried as-is.</summary>
    public static string FuelWord(string gaugeLabel) => gaugeLabel == "OXY" ? "OXY" : "FUEL";

    /// <summary>Native <c>PartWrapper::IsOutOfAmmo</c> for the gun pool: the ammunition
    /// gauge at (or below) the tracker's own empty epsilon. No invented zero — an
    /// unknown fraction (no telemetry) draws no cue.</summary>
    public static bool GunEmpty(float? ammoFraction)
        => ammoFraction is { } fraction && fraction <= Flight.WeaponReloadTracker.EmptyEpsilon;

    /// <summary>The missile row's arming word: the shared <see cref="WeaponStatus"/> of
    /// the first mounted launcher (P786/P803 — the same value the weapon-status list
    /// shows), never a renderer-owned timer.</summary>
    public static string? MissileStateWord(WeaponStatus status) => status switch
    {
        WeaponStatus.Ready => "READY",
        WeaponStatus.NotReady => "NOT READY",
        WeaponStatus.Reloading => "RELOAD",
        WeaponStatus.Depleted => "DEPLETED",
        _ => null,
    };

    /// <summary>
    /// The annunciator set of the own-ship status, in the native order (P972 §5.1):
    /// EYE, CLK, AUTO, VLOCK, FLAG, ART. Each flag is the shared
    /// <see cref="HudOwnStatus"/> flag, so a skin cannot show one the model hides.
    /// </summary>
    public static Annunciators AnnunciatorsOf(in HudOwnStatus status)
    {
        Annunciators set = Annunciators.None;
        if (status.DetectionEyeVisible) set |= Annunciators.Eye;
        if (status.CloakActive) set |= Annunciators.Cloak;
        if (status.AutopilotEngaged) set |= Annunciators.Autopilot;
        if (status.VectorLockEngaged) set |= Annunciators.VectorLock;
        if (status.CarriedFlag) set |= Annunciators.Flag;
        if (status.CarriedArtifact) set |= Annunciators.Artifact;
        return set;
    }

    /// <summary>The own-ship annunciators (shape + word, P972 §4.1).</summary>
    [System.Flags]
    public enum Annunciators
    {
        None = 0,
        Eye = 1,
        Cloak = 2,
        Autopilot = 4,
        VectorLock = 8,
        Flag = 16,
        Artifact = 32,
    }

    /// <summary>True when any annunciator is lit (the row then exists at all).</summary>
    public static bool AnyAnnunciator(Annunciators set) => set != Annunciators.None;

    /// <summary>P1038b — the gap between an annunciator/caution icon and the word it
    /// introduces, in standard-stroke units (× scale).</summary>
    public const float IconGap = 4f;

    /// <summary>P1038c — the gap between an icon glyph and its word as a fraction of the
    /// word's cap height. The brief requires ≥ ¼ cap of clear space; Jet uses ¾ cap so that
    /// after the icon's and the word's 1 px keylines at least ¼ cap of untouched sky still
    /// separates the two marks at every density, and never below the P1038b
    /// <see cref="IconGap"/> floor.</summary>
    public const float IconGapFraction = 0.75f;

    public static float IconWordGap(float capHeight, float scale)
        => MathF.Max(IconGap * (scale > 0f ? scale : 1f), IconGapFraction * capHeight);

    /// <summary>P1038b — the drawn size of an icon glyph: at least the cap height of the
    /// word it introduces, so the mark is legible instead of a 2–4 px speck (the P1038
    /// defect). The word stays beside it, so state never depends on the icon or the hue.</summary>
    public static float IconSize(float capHeight) => MathF.Max(capHeight, 6f);

    /// <summary>P1038b — the capital ink height of a text line. A sprite font's measured
    /// height is its LINE SPACING (22 px at scale 1 for the 8 pt HUD font) while the
    /// capitals occupy about half of it; icon glyphs and their audit floor use this value,
    /// so a mark reads at the letters' size rather than at the whole line box.</summary>
    public const float CapInkFraction = 0.5f;

    public static float CapHeight(float measuredLineHeight) => measuredLineHeight * CapInkFraction;
}
