namespace Allegiance.ClientCore.MonoGame.Tests.Skins;

/// <summary>P1049b: these gates repeat a fixed model and viewport. All cache keys
/// repeat, speed-label slots restart within each draw, and animation time comes
/// from the model. The steady-state frame therefore has period one, not the
/// capacity of any dictionary. See CI_FAST_TIMING_P1049.md, P1049b cycle audit.
/// Keep a conservative 32 warm-up / 128 measured repetitions of that one-frame
/// period; the sensitivity probes also test period MeasuredFrames - 1.</summary>
internal static class JetAllocationWindow
{
    internal const int LargestCycle = 1;
    internal const int WarmupFrames = 32;
    internal const int MeasuredFrames = 128;
}
