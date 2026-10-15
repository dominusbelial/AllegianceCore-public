namespace Allegiance.Core.Wire;

/// <summary>Per-server diagnostic counters. Full record quantization is counted
/// separately from the unavoidable per-viewer relative-position encoding.</summary>
public sealed class ShipUpdateEncodingCounters
{
    public long HeavyRecords, SingleRecords, LightRecords, RelativePositions;
}
