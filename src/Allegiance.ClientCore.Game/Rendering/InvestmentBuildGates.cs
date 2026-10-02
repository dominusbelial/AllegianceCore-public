using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P495 — the pure client-side build gates behind the investment pane's
/// station-type CanInvest, a port of native
/// <c>PartListItem::BuildSpotFoundForStationType</c> and
/// <c>FindBuilderForStationType</c> (WinTrek/loadout.cpp:213-240) plus the
/// station gate they feed in <c>PurchasesPaneImpl::CanInvest</c>
/// (loadout.cpp:1124-1131). Kept MonoGame-free so the gates are unit-testable
/// without a graphics device; the host (<c>Game1.Panes.cs</c>) feeds the real
/// world data (asteroid capabilities, building effects, side ship hulls) and
/// the static core into these each frame and hands the verdict to
/// <see cref="InvestmentPaneModel"/> via its <c>stationCanBuild</c> delegate.
/// </summary>
/// <remarks>
/// <para><b>Build spot</b> (<see cref="BuildSpotFound"/>): native scans every
/// known asteroid (<c>GetCore()-&gt;GetAsteroids()</c>, loadout.cpp:213-225)
/// for one whose capability mask includes the station type's
/// <c>GetBuildAABM()</c>. The port does the same over the client's
/// <c>ClientWorld.Asteroids</c>, comparing via
/// <see cref="DataAsteroidIGC.HasCapability"/> against
/// <see cref="DataStationTypeIGC.AabmBuild"/>.</para>
///
/// <para><b>Existing builder</b> (<see cref="BuilderExists"/>): native returns
/// a side ship whose <c>GetBaseData() == pStationtype</c> (loadout.cpp:227-240)
/// — a constructor drone committed to building that exact station type. The
/// client's peer ship wire does NOT carry a ship's base data (which station it
/// is building), so the port approximates the verdict from the two observable
/// signals that together tie a constructor to a specific station type:
/// <list type="bullet">
/// <item>the side has a ship whose real hull is the station type's
/// construction drone hull — the P458 station-type→construction-drone→hull
/// mapping over the real drone hulls (P459), observed on the wire the P486
/// way (<see cref="SideHasConstructorHull"/>);</item>
/// <item>an active building effect sits on an asteroid whose capability
/// matches the station type's <c>AabmBuild</c> — the rock's capability is the
/// discriminator that tells WHICH station type is actually being built
/// (<see cref="ActiveConstructionOnCompatibleRock"/>).</item>
/// </list>
/// Requiring BOTH avoids the two failure modes of either signal alone: the hull
/// alone would falsely refuse every station type that shares the same
/// constructor hull (many PCore001 station types share "Sm/Lg Std Constructor"),
/// and the rock alone would count an enemy's construction (opposing factions
/// build different station types on different rock capabilities, so requiring
/// the own-side constructor hull excludes them). Documented approximation:
/// a constructor still en route (spawned but not yet building) has no building
/// effect yet, so <see cref="BuilderExists"/> is momentarily false — but such a
/// constructor exists only once its bucket is 100% funded, which already makes
/// <c>CanInvest</c> refuse via the percent-bought gate (loadout.cpp:1138-1139),
/// so the two coincide in every observable outcome.</para>
/// </remarks>
public static class InvestmentBuildGates
{
    /// <summary>
    /// Native <c>BuildSpotFoundForStationType</c> (loadout.cpp:213-225): any
    /// known asteroid whose capability mask includes <paramref name="aabmBuild"/>
    /// (the station type's <see cref="DataStationTypeIGC.AabmBuild"/>).
    /// </summary>
    public static bool BuildSpotFound(IEnumerable<DataAsteroidIGC> asteroids, short aabmBuild)
        => MatchingAsteroidIds(asteroids, aabmBuild).Any();

    /// <summary>
    /// The real hull a station type's construction drone flies — the P458
    /// mapping: the station type's <see cref="DataStationTypeIGC.ConstructionDroneTypeId"/>
    /// resolves (through the static core's drone-type table) to the drone
    /// type's <see cref="DataDroneTypeIGC.HullTypeId"/> (the real constructor
    /// hulls of P459, e.g. "Sm Std Constructor"). Null when the construction
    /// drone type is absent/unknown, which the caller treats as permissive.
    /// </summary>
    public static short? ConstructionHullFor(
        DataStationTypeIGC stationType, IReadOnlyDictionary<short, DataDroneTypeIGC>? droneTypes)
        => droneTypes is { } dt && dt.TryGetValue(stationType.ConstructionDroneTypeId, out var d)
            ? d.HullTypeId
            : (short?)null;

    /// <summary>The side has at least one ship whose hull equals the station
    /// type's construction-drone hull — the P486 observation of a constructor
    /// drone on the wire by its real hull.</summary>
    public static bool SideHasConstructorHull(IEnumerable<short> sideShipHullIds, short? constructorHull)
        => constructorHull is { } h && sideShipHullIds.Contains(h);

    /// <summary>An active building effect (an in-progress construction) sits on
    /// an asteroid whose capability includes <paramref name="aabmBuild"/> —
    /// i.e. a construction of the station type whose build mask that is.</summary>
    public static bool ActiveConstructionOnCompatibleRock(
        IEnumerable<DataAsteroidIGC> asteroids, IEnumerable<short> constructedAsteroidIds, short aabmBuild)
    {
        foreach (var id in constructedAsteroidIds)
            foreach (var a in asteroids)
                if (a.AsteroidId == id && a.HasCapability(aabmBuild))
                    return true;
        return false;
    }

    /// <summary>
    /// Native <c>FindBuilderForStationType</c> (loadout.cpp:227-240) as the
    /// client can observe it: true iff the side has a constructor drone of the
    /// station type's real hull AND a construction of that type is actively
    /// underway at a compatible rock (see the class doc for the approximation
    /// and why both signals are required).
    /// </summary>
    public static bool BuilderExists(
        IEnumerable<DataAsteroidIGC> asteroids, IEnumerable<short> constructedAsteroidIds,
        IEnumerable<short> sideShipHullIds, DataStationTypeIGC stationType,
        IReadOnlyDictionary<short, DataDroneTypeIGC>? droneTypes)
        => SideHasConstructorHull(sideShipHullIds, ConstructionHullFor(stationType, droneTypes))
           && ActiveConstructionOnCompatibleRock(asteroids, constructedAsteroidIds, stationType.AabmBuild);

    /// <summary>
    /// The station-type sub-gate of native <c>CanInvest</c> (loadout.cpp:1124-1131):
    /// a station-type investment is allowed iff a build spot exists for it AND no
    /// builder for it already exists.
    /// </summary>
    public static bool CanBuildStation(
        IEnumerable<DataAsteroidIGC> asteroids, IEnumerable<short> constructedAsteroidIds,
        IEnumerable<short> sideShipHullIds, DataStationTypeIGC stationType,
        IReadOnlyDictionary<short, DataDroneTypeIGC>? droneTypes)
        => BuildSpotFound(asteroids, stationType.AabmBuild)
           && !BuilderExists(asteroids, constructedAsteroidIds, sideShipHullIds, stationType, droneTypes);

    /// <summary>The build-spot set for the asteroid hint: every known asteroid
    /// whose capability includes <paramref name="aabmBuild"/> (native
    /// <c>SetInvestAsteroid</c>, loadout.cpp:1396-1409 highlights exactly these
    /// rocks). Shared by the gate's <see cref="BuildSpotFound"/> and the render
    /// highlight so both stay one source of truth.</summary>
    public static IEnumerable<short> MatchingAsteroidIds(IEnumerable<DataAsteroidIGC> asteroids, short aabmBuild)
        => asteroids.Where(a => a.HasCapability(aabmBuild)).Select(a => a.AsteroidId).Distinct();
}
