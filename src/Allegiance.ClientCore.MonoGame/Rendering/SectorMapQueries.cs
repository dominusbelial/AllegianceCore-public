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
}
