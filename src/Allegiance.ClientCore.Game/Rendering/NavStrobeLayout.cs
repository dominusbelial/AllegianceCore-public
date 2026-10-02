using System.Collections.Generic;
using SimVec = Allegiance.Core.Collision.Vector3;
using Orientation = Allegiance.Core.Sim.Orientation;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// One strobe light's world-space anchor for a frame: its position, its real
/// timing (<see cref="NavStrobeTiming"/>) and its colour (linear RGB). Built per
/// hull/structure instance by <see cref="NavStrobeLayout"/>; the pulse envelope
/// is applied at draw time by <see cref="NavStrobeMath.Pulse"/>.
/// </summary>
public readonly record struct NavStrobeAnchor(
    SimVec Position, NavStrobeTiming Timing, float R, float G, float B);

/// <summary>
/// Chooses where navigation strobe lights sit on a ship/structure, following the
/// exact "faithful geometric approximation" precedent this port's P358 / P365
/// landings established for effect hardpoints (see P365_ENGINE_NOZZLE_GLOW.md /
/// P358_SHIP_ENGINE_TRAIL.md): this port renders Meshy-regenerated content hulls,
/// not the native <c>.mdl</c> geometry, so the native <c>SS</c>-named hardpoint
/// groups (compiled by <c>LightsGeo</c>'s GroupGeoCallback, efapp.cpp:531-561)
/// do not survive into the regenerated meshes. As with P358/P365's aft-centre
/// glow/nozzle, the strobe anchors are derived from the real rendered hull's
/// bounding geometry — the wingtips and stern — so they sit on the actually-drawn
/// hull on every hull class.
/// </summary>
/// <remarks>
/// <para><b>Anchor layout (standard aviation nav-light convention).</b> Port
/// (left) wingtip = red, starboard (right) wingtip = green, stern = white — the
/// universal red-left/green-right/white-tail scheme, standing in for native's
/// per-group material-derived colour (native reads the <c>SS</c> group's child
/// material diffuse, efapp.cpp:556, or white efapp.cpp:558; that material colour
/// isn't recoverable from regenerated meshes, so the standard scheme is chosen
/// and documented as the port's substitution).</para>
/// <para><b>Alternating blink.</b> Native's per-light timing means port and
/// starboard can blink out of phase (that is how a hull gets its alternating
/// red/green nav-light look — efapp.cpp:474-529). The port reproduces the same
/// mechanism: all lights share native's default period/ramp but carry distinct
/// <see cref="NavStrobeTiming.Phase"/>s — port at phase 0, starboard at half a
/// period, stern at a quarter — so they flash alternately rather than in unison.
/// The <c>period</c>/<c>rampUp</c>/<c>hold</c>/<c>rampDown</c> are native's exact
/// defaults (1.25 / 0.1 / 0 / 0.05 s).</para>
/// </remarks>
public static class NavStrobeLayout
{
    /// <summary>Wingtip anchors sit at this fraction of the hull radius out along
    /// the left/right axis — the ship's widest real extent.</summary>
    public const float WingSpanFraction = 0.95f;

    /// <summary>Stern anchor sits at this fraction of the hull radius behind the
    /// centre — the same aft point family P358/P365 use for the exhaust/nozzle.</summary>
    public const float AftFraction = 0.92f;

    /// <summary>Station beacon anchors sit at this fraction of the structure radius.</summary>
    public const float StationBeaconFraction = 1.00f;

    /// <summary>
    /// The port's normalization of native's decal scale to the real-hull world
    /// units: a strobe's quad half-extent is <c>pulse·radius·SizeFactor</c>, so at
    /// full pulse (value 1) a nav light spans ≈0.19× the hull radius across — a
    /// small, clearly-a-beacon dot on every hull class (native draws at
    /// <c>0.5·value</c> in native mesh units; the same documented normalization
    /// P358/P365 apply to particle/glow sizes).
    /// </summary>
    public const float SizeFactor = 0.095f;

    /// <summary>Three strobes on a ship: red port wingtip, green starboard
    /// wingtip, white stern — each with native's default timing but a distinct
    /// phase so the two wingtips alternate.</summary>
    public static IReadOnlyList<NavStrobeAnchor> ShipAnchors(SimVec position, Orientation orientation, float radius)
    {
        var right = orientation.GetRight();
        var left = -right;
        var backward = orientation.GetBackward();

        float period = NavStrobeTiming.Native.Period;
        float rampUp = NavStrobeTiming.Native.RampUp;
        float hold = NavStrobeTiming.Native.Hold;
        float rampDown = NavStrobeTiming.Native.RampDown;

        var anchors = new List<NavStrobeAnchor>(3)
        {
            // Port (left) wingtip — red, phase 0.
            new(
                position + (left * (radius * WingSpanFraction)),
                new NavStrobeTiming(period, 0f, rampUp, hold, rampDown),
                0.95f, 0.10f, 0.050f),
            // Starboard (right) wingtip — green, phase = half a period (alternates with port).
            new(
                position + (right * (radius * WingSpanFraction)),
                new NavStrobeTiming(period, period * 0.5f, rampUp, hold, rampDown),
                0.035f, 0.92f, 0.080f),
            // Stern — white, phase = quarter period.
            new(
                position + (backward * (radius * AftFraction)),
                new NavStrobeTiming(period, period * 0.25f, rampUp, hold, rampDown),
                1f, 1f, 1f),
        };
        return anchors;
    }

    /// <summary>Three beacons on a station: red and green at the ring's opposite
    /// ends (alternating) plus a white top beacon — the same nav-colour scheme and
    /// alternating phase mechanism as the ship strobes, anchored at the structure's
    /// real radius so they sit on the rendered station mesh.</summary>
    public static IReadOnlyList<NavStrobeAnchor> StationAnchors(SimVec position, Orientation orientation, float radius)
    {
        var right = orientation.GetRight();
        var left = -right;
        var up = orientation.GetUp();

        float period = NavStrobeTiming.Native.Period;
        float rampUp = NavStrobeTiming.Native.RampUp;
        float hold = NavStrobeTiming.Native.Hold;
        float rampDown = NavStrobeTiming.Native.RampDown;

        var anchors = new List<NavStrobeAnchor>(3)
        {
            new(
                position + (left * (radius * StationBeaconFraction)),
                new NavStrobeTiming(period, 0f, rampUp, hold, rampDown),
                0.95f, 0.10f, 0.050f),
            new(
                position + (right * (radius * StationBeaconFraction)),
                new NavStrobeTiming(period, period * 0.5f, rampUp, hold, rampDown),
                0.035f, 0.92f, 0.080f),
            new(
                position + (up * (radius * StationBeaconFraction)),
                new NavStrobeTiming(period, period * 0.6667f, rampUp, hold, rampDown),
                1f, 1f, 1f),
        };
        return anchors;
    }
}
