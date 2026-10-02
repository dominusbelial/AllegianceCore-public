using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P495 — the pure decision of whether a rock is a build spot for the
/// currently-hovered station-type investment, i.e. native
/// <c>HangarWindow::SetInvestAsteroid</c> (WinTrek/loadout.cpp:1396-1409):
/// when a station-type bucket is selected/hovered, the asteroids whose
/// capability mask includes the station type's <c>AabmBuild</c> are the
/// build spots to highlight. Kept OUT of the render loop so the flagging is
/// unit-testable without a graphics device (the same pure-logic-layer
/// discipline as <see cref="RadarTargetSelection"/>). The render layer
/// resolves the selected station-type bucket to its
/// <see cref="DataStationTypeIGC.AabmBuild"/> and asks this per asteroid.
/// </summary>
public static class RadarInvestAsteroidSelection
{
    /// <summary>Whether an asteroid with capability mask
    /// <paramref name="asteroidCapabilities"/> is a build spot for the hovered
    /// station type whose build mask is <paramref name="activeAabmBuild"/>.
    /// A null active mask (no station-type bucket hovered) flags nothing.</summary>
    public static bool IsBuildSpot(short? activeAabmBuild, short asteroidCapabilities)
        => activeAabmBuild is { } a && (asteroidCapabilities & a) != 0;
}
