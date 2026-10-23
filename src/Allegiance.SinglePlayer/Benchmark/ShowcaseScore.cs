namespace Allegiance.SinglePlayer.Benchmark;

/// <summary>
/// P789 — the single Showcase score: one integer that says how big a battle a
/// machine carried, and how well it carried it. Pure measured inputs only —
/// no hardware names, no resolution discount:
///
/// <c>score = round(ships_in_view_mean × smooth × realtime × 10)</c>, with
/// <c>smooth = clamp(fps_1pct_low / 60, 0, 1)</c> and
/// <c>realtime = clamp(tidi_time_weighted_mean / 100, 0, 1)</c>.
///
/// Worked anchors (the P789 brief): a 200v200 battle (≈400 ships in view)
/// held at 60 fps 1 % low and full real time scores ≈ 4000; a 60v60
/// (≈120 ships) at the same quality ≈ 1200. A match the server had to slow
/// to 50 % real time caps at half credit regardless of fps, and 0 fps or an
/// empty sector scores 0.
///
/// Kept exactly as proposed in the brief: every term is already measured by
/// the client (ships in the viewed sector, fps 1 % low, HUD time dilation),
/// the clamps make the score monotone and bounded per ship, and the ×10
/// ships multiplier puts the anchors on round numbers.
/// </summary>
public static class ShowcaseScore
{
    /// <summary>The fps 1 % low that earns full smoothness credit.</summary>
    public const double FpsReference = 60;
    /// <summary>The time-dilation percentage that earns full real-time credit (100 = full speed).</summary>
    public const double TidiReferencePercent = 100;
    /// <summary>Points per in-view ship at full quality.</summary>
    public const double ShipsMultiplier = 10;

    public static int Compute(double shipsInViewMean, double fpsOnePercentLow, double tidiTimeWeightedMeanPercent)
    {
        double smooth = Math.Clamp(fpsOnePercentLow / FpsReference, 0, 1);
        double realtime = Math.Clamp(tidiTimeWeightedMeanPercent / TidiReferencePercent, 0, 1);
        double ships = double.IsFinite(shipsInViewMean) ? Math.Max(0, shipsInViewMean) : 0;
        return (int)Math.Round(ships * smooth * realtime * ShipsMultiplier);
    }
}
