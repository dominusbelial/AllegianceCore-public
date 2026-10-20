namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P758 — the visual state of a bombed aleph, the render half of native
/// <c>IwarpIGC::AddBomb</c> (<c>clintlib/appmsg.cpp:858-866</c>): from the
/// <c>WARP_BOMB(188)</c> receipt until the detonation deadline the aleph's
/// funnel destabilizes — native swaps the warp's geometry to its bomb art
/// (<c>WarpGeo::AddBomb</c>); the port pulses the funnel red-hot with a
/// rising urgency as the deadline approaches. Pure math (headless-testable);
/// the draw call consumes <see cref="Pulse"/>.
/// </summary>
public static class WarpBombVisual
{
    /// <summary>The destabilization strength in [0,1] for a bombed aleph.
    /// <paramref name="remainingSeconds"/> ≤ 0 (deadline passed / clock
    /// unresolved) is 0 — after the blast the warp is gone, so the idle look is
    /// correct. Otherwise the pulse rises linearly through the final 10 seconds
    /// and beats at ~1.2 Hz, mirroring native's animated bomb art pacing.</summary>
    public static float Pulse(double remainingSeconds, double elapsedSeconds)
    {
        if (remainingSeconds <= 0) return 0f;
        float ramp = remainingSeconds >= 10f ? 0.35f : 0.35f + 0.65f * (float)((10.0 - remainingSeconds) / 10.0);
        float beat = 0.5f + 0.5f * (float)Math.Sin(elapsedSeconds * 2.0 * Math.PI * 1.2);
        return Math.Clamp(ramp * (0.55f + 0.45f * beat), 0f, 1f);
    }
}
