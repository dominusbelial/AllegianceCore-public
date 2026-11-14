using System;
using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>How one stat changed between two <see cref="ShipStatSheet"/>s.</summary>
public enum StatOutcome
{
    /// <summary>Identical value (or identical class text) on both sheets.</summary>
    Same,
    /// <summary>A real change in the pilot's favour (per <see cref="StatDirection"/>).</summary>
    Better,
    /// <summary>A real change against the pilot.</summary>
    Worse,
    /// <summary>Changed, but the stat has no defensible ordering (a class text).</summary>
    Changed,
    /// <summary>Only the new sheet has this stat (e.g. the new hull has an extra gun).</summary>
    Added,
    /// <summary>Only the old sheet had this stat.</summary>
    Removed,
}

/// <summary>
/// P1011 — one stat's delta between two sheets: both absolute values, the signed
/// difference, the stat's "better/worse" direction and the resolved outcome.
/// Native has no delta display; this is the Modern presentation layer's data
/// (the epic's "inline part pickers with deltas"), so the direction rules are
/// the only new judgement and they are stated once, per field, in
/// <see cref="ShipStatSheet"/>.
/// </summary>
public readonly record struct StatDelta(
    StatKey Key,
    string Label,
    StatDirection Direction,
    double? OldValue,
    double? NewValue,
    string? OldText,
    string? NewText,
    string Unit,
    double Difference,
    int Sign,
    StatOutcome Outcome)
{
    /// <summary>True when this row reads as an improvement for the pilot.</summary>
    public bool IsBetter => Outcome == StatOutcome.Better;

    /// <summary>True when this row reads as a regression for the pilot.</summary>
    public bool IsWorse => Outcome == StatOutcome.Worse;

    /// <summary>
    /// Pair two sheets by <see cref="StatKey"/>. The result is ordered like the
    /// NEW sheet (so a renderer can print the stats bar top to bottom), with any
    /// stats the new sheet no longer has appended at the end as
    /// <see cref="StatOutcome.Removed"/>.
    /// </summary>
    public static IReadOnlyList<StatDelta> Between(ShipStatSheet oldSheet, ShipStatSheet newSheet)
    {
        ArgumentNullException.ThrowIfNull(oldSheet);
        ArgumentNullException.ThrowIfNull(newSheet);

        var oldByKey = new Dictionary<StatKey, ShipStat>();
        foreach (var s in oldSheet.Stats)
            oldByKey[s.Key] = s;

        var result = new List<StatDelta>(Math.Max(oldSheet.Stats.Count, newSheet.Stats.Count));
        var seen = new HashSet<StatKey>();
        foreach (var now in newSheet.Stats)
        {
            seen.Add(now.Key);
            if (oldByKey.TryGetValue(now.Key, out var before))
                result.Add(Compare(before, now));
            else
                result.Add(Added(now));
        }
        foreach (var before in oldSheet.Stats)
            if (!seen.Contains(before.Key))
                result.Add(Removed(before));

        return result;
    }

    /// <summary>Compare one stat against the same stat on the previous sheet.</summary>
    public static StatDelta Compare(in ShipStat before, in ShipStat now)
    {
        // Categorical rows (native's DAMAGE CLASS / DEFENSE TYPE / DEFENSE CLASS):
        // compare the text; there is no numeric difference to report.
        if (before.Text is not null || now.Text is not null)
        {
            bool same = string.Equals(before.Text, now.Text, StringComparison.Ordinal);
            return new StatDelta(now.Key, now.Label, now.Direction, null, null,
                before.Text, now.Text, now.Unit, 0, 0,
                same ? StatOutcome.Same : StatOutcome.Changed);
        }

        if (before.Value is not { } oldValue || now.Value is not { } newValue)
        {
            // One side could not resolve its ordnance type (no core lookup).
            return new StatDelta(now.Key, now.Label, now.Direction, before.Value, now.Value,
                null, null, now.Unit, 0, 0,
                before.Value == now.Value ? StatOutcome.Same : StatOutcome.Changed);
        }

        double difference = newValue - oldValue;
        int sign = difference > 1e-6 ? 1 : difference < -1e-6 ? -1 : 0;
        StatOutcome outcome = sign == 0
            ? StatOutcome.Same
            : now.Direction switch
            {
                StatDirection.HigherIsBetter => sign > 0 ? StatOutcome.Better : StatOutcome.Worse,
                StatDirection.LowerIsBetter => sign < 0 ? StatOutcome.Better : StatOutcome.Worse,
                _ => StatOutcome.Changed,
            };
        // A neutral-direction numeric row (none today) still reports the sign.
        return new StatDelta(now.Key, now.Label, now.Direction, oldValue, newValue,
            null, null, now.Unit, difference, sign, outcome);
    }

    private static StatDelta Added(in ShipStat now) => new(
        now.Key, now.Label, now.Direction, null, now.Value, null, now.Text, now.Unit, 0, 0,
        StatOutcome.Added);

    private static StatDelta Removed(in ShipStat before) => new(
        before.Key, before.Label, before.Direction, before.Value, null, before.Text, null,
        before.Unit, 0, 0, StatOutcome.Removed);

    /// <summary>Count the rows that read better / worse (the stats-bar summary).</summary>
    public static (int Better, int Worse) Count(IReadOnlyList<StatDelta> deltas)
    {
        int better = 0, worse = 0;
        foreach (var d in deltas)
        {
            if (d.Outcome == StatOutcome.Better) better++;
            else if (d.Outcome == StatOutcome.Worse) worse++;
        }
        return (better, worse);
    }
}
