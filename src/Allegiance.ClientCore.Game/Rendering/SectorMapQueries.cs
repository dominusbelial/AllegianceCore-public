using System;
using System.Collections.Generic;
using System.Linq;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P460 — pure, headless-testable sector-map queries for the starmap overlay:
/// who owns each sector (native's <c>GetClusterOwners</c>,
/// <c>WinTrek/sectormap.cpp:11-50</c>) and how many of the player's own probes
/// sit in the selected sector (native's own-probe count,
/// <c>sectormap.cpp:328-345</c>). Deliberately PURE like <see cref="StarmapLayout"/>
/// (P249) / <see cref="SectorInfoPaneLayout"/> (P272) — plain data in, plain
/// answers out, no MonoGame types on the surface — so both are unit-testable
/// without a graphics device. The MonoGame <see cref="StarmapRenderer"/> and
/// <see cref="SectorInfoPaneRenderer"/> feed real client-world data through
/// these to paint the rings and the probe readout.
/// </summary>
public static class SectorMapQueries
{
    /// <summary><c>c_eabmRescue = c_sabmRescue = 0x2000</c> (src/Igc/igc.h:781) —
    /// the probe-expendable ability threshold native's own-probe count uses
    /// (a probe counts if its type has NO abilities or is at least a rescue
    /// probe; capacity-bearing deployables like mines/dispensers do not).</summary>
    public const short EabmRescue = 0x2000;

    /// <summary>The outcome of <see cref="GetClusterOwners"/>: the owning side
    /// and (when a runner-up exists) the secondary owner. <see cref="Secondary"/>
    /// is null when no side qualifies as a runner-up (e.g. a single-owner sector
    /// or a tie resolved in the lower side id's favour).</summary>
    public readonly record struct ClusterOwners(short Owner, short? Secondary);

    /// <summary>
    /// Port of native <c>GetClusterOwners</c> (sectormap.cpp:11-50). Counts the
    /// stations of each side in a sector, ignoring stations whose type has the
    /// <c>c_sabmPedestal</c> capability (the unwinnable starbase pedestals),
    /// then selects the side with the most stations as owner and the runner-up
    /// as secondary owner. Faithful to native's tie-breaking: the selection runs
    /// in ascending side-id order with STRICTLY-GREATER comparisons
    /// (sectormap.cpp:33,43), so of two tied sides the LOWER side id wins the
    /// owner slot and the higher becomes secondary. Returns null when the sector
    /// has no (non-pedestal) stations — neither native's <c>NA</c>/<c>NA</c> nor
    /// a ring is drawn for it.
    /// </summary>
    /// <param name="stations">The sector's stations as (side id, station type id)
    /// pairs; cluster membership is implied by the caller's grouping.</param>
    /// <param name="isPedestalType">Given a station type id, whether that type has
    /// the pedestal capability (the caller resolves it via
    /// <see cref="DataStationTypeIGC.HasCapability"/> and
    /// <see cref="DataStationTypeIGC.SabmPedestal"/>, keeping this function type-agnostic
    /// and pure).</param>
    public static ClusterOwners? GetClusterOwners(
        IEnumerable<(short SideId, short StationTypeId)> stations,
        Func<short, bool> isPedestalType)
    {
        var counts = new Dictionary<short, int>();
        foreach (var (side, typeId) in stations)
        {
            if (isPedestalType(typeId))
                continue;                 // native: `!HasCapability(c_sabmPedestal)`
            counts.TryGetValue(side, out int c);
            counts[side] = c + 1;
        }

        if (counts.Count == 0)
            return null;                  // no (countable) stations → no owner

        // Native's two-pass selection (sectormap.cpp:25-48), run over the sides
        // in ascending id order with strict `>` so lower side ids win ties.
        short owner = -1, secondary = -1;
        int ownerCount = 0, secondaryCount = 0;
        foreach (var side in counts.Keys.OrderBy(s => s))
        {
            int n = counts[side];
            if (n > ownerCount)
            {
                secondary = owner; secondaryCount = ownerCount;
                owner = side; ownerCount = n;
            }
            else if (n > secondaryCount)
            {
                secondary = side; secondaryCount = n;
            }
        }

        return new ClusterOwners(owner, secondary < 0 ? null : secondary);
    }

    /// <summary>
    /// Port of native's own-probe count (sectormap.cpp:328-345): how many of the
    /// player's own probes sit in a given sector, counting only probes whose type
    /// has NO capabilities (<c>== 0</c>) or is at least a rescue probe
    /// (<c>&gt;= c_eabmRescue</c>) — native's Imago-filter, which excludes
    /// mine/dispenser-style deployables that would otherwise read as recon
    /// probes. The caller supplies each probe's real <c>EabmCapabilities</c>
    /// (resolved from the probe type table by the probe's type id).
    /// </summary>
    /// <param name="probes">The world's probes as (cluster id, side id, eabm
    /// capabilities) tuples; the caller usually passes only the selected sector's
    /// probes.</param>
    public static int CountOwnProbes(
        IEnumerable<(short ClusterId, short SideId, short EabmCapabilities)> probes,
        short clusterId, short ownSideId)
    {
        int n = 0;
        foreach (var (cl, side, cap) in probes)
        {
            if (cl == clusterId && side == ownSideId && (cap == 0 || cap >= EabmRescue))
                n++;
        }
        return n;
    }

    /// <summary>
    /// P493 — the sectors in which the client knows an ENEMY ship to be right
    /// now, from the SHIP_STATUS(90) entries its side has received (the
    /// cross-side spotted/hidden stream of <c>SweepCrossSideShipStatuses</c>).
    /// This is the "eyes for the team" datum the brief asks the sector map to
    /// render: probes/scouts tell a side where enemies were last seen, and in
    /// which sector.
    /// <para><b>Which entities count.</b> A ship is a known enemy position when
    /// (a) it has a real identity from EXPORT (<see cref="ShipEntity.HasRealIdentity"/>
    /// — a not-yet-identified ship's default SideId 0 must never be
    /// misclassified), (b) its side differs from the viewer's
    /// (<see cref="ClientWorld.MySideId"/>), (c) its last-known <c>SectorId</c>
    /// is valid (nonzero — the C# default that means "no SHIP_STATUS received
    /// yet"; the real sector ids in every shipped map are ≫ 0), and (d) it is
    /// NOT <see cref="ShipEntity.Unknown"/> — a spotted status clears unknown,
    /// a hidden status sets it, so dropping unknown ships is exactly the
    /// spotted→hidden lifecycle: the marker appears on the false→true spot and
    /// disappears on the true→false hide. A <c>SHIP_STATUS</c> is deliberately
    /// the ONLY source of these last-known sectors (not the 176 heavy telemetry,
    /// which is the separate in-view live pose stream the radar already covers).
    /// </summary>
    /// <param name="ships">The client's ship entities as (SectorId, SideId,
    /// HasRealIdentity, Unknown) tuples — the caller flattens under the gate
    /// lock.</param>
    /// <returns>Sector id → how many known enemy ships have that as their
    /// last-known sector (one marker per sector, sized/counted by this value).</returns>
    public static IReadOnlyDictionary<short, int> KnownEnemySectors(
        IEnumerable<(short SectorId, short SideId, bool HasRealIdentity, bool Unknown)> ships,
        short? mySideId)
    {
        var result = new Dictionary<short, int>();
        foreach (var (sector, side, hasReal, unknown) in ships)
        {
            if (mySideId is not { } ms) continue;          // no own side yet → can't classify enemy
            if (!hasReal) continue;                        // default SideId 0 → not identifiable (a)
            if (side == ms) continue;                      // not an enemy (b)
            if (unknown) continue;                         // hidden → no known position (d)
            if (sector == 0) continue;                     // no real status yet (c)
            result.TryGetValue(sector, out int n);
            result[sector] = n + 1;
        }
        return result;
    }
}
