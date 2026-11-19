namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>Regression: a held trigger's burst loop played forever after docking or
/// being podded, because only EmitOwnShots (flight frames with a weapon-fitted own
/// ship) ever stopped it. The per-frame stale check stops it once that path stops running.</summary>
public sealed class OwnWeaponLoopStaleTests
{
    [Theory]
    [InlineData(1_000, 1_000, false)]   // emitted this frame
    [InlineData(1_250, 1_000, false)]   // within the grace (a skipped frame or two)
    [InlineData(1_251, 1_000, true)]    // stopped running: docked / podded / left flight
    [InlineData(60_000, 0, true)]
    public void Loops_stop_once_own_shot_emission_stops(long now, long last, bool stale)
        => Assert.Equal(stale, Game1.OwnWeaponLoopsStale(now, last));
}
