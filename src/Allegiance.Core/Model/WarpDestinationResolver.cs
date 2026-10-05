using System.Collections.Generic;

namespace Allegiance.Core.Model;

/// <summary>
/// P540c — resolve a warp's DESTINATION SECTOR from its PARTNER warp, exactly
/// as native does. A warp's <c>DestinationId</c> is the id of its PARTNER
/// warp (the other aleph of the pair — native <c>warpIGC.h:129-141</c>,
/// <c>GetWarp(m_warpDef.destinationID)</c>), NOT the destination sector; the
/// destination sector is the partner warp's own <c>ClusterId</c> (<c>DataWarpIGC</c>,
/// <c>igc.h:2506-2514</c>). That is the resolution the server's transit performs
/// (<c>Mission.Assemble</c> lines 518-542; <c>ServerHost.HitWarpEvent</c> via
/// <c>ResolvedWarp.Destination</c>/<c>Partner()</c>) and the native client's warp
/// walk expects.
///
/// <para>Shared by the CLIENT consumers (<c>Game1.AllWarps</c>,
/// <c>SpectatorFrame.BuildSectorFrame</c>) that must never mistake a warp id for
/// a sector id. The two ids live in different spaces — a warp whose
/// <c>DestinationId</c> is fed straight in as a destination SECTOR aims the
/// router at a phantom sector the server never transits to. Returns
/// <c>null</c> when the partner warp is not yet known — callers SKIP that edge
/// rather than guess (a warp id is never a sector id).</para>
/// </summary>
public static class WarpDestinationResolver
{
    /// <summary>
    /// The destination sector of the warp named by <paramref name="warpId"/>:
    /// its partner warp's <c>ClusterId</c>. Returns <c>null</c> when
    /// <paramref name="warpId"/> is unknown, or when the warp's partner
    /// (<c>DestinationId</c>) is not in <paramref name="warps"/> — a dangling /
    /// not-yet-synced edge the caller should skip, never guess.
    /// </summary>
    public static short? DestinationSector(IReadOnlyDictionary<short, DataWarpIGC> warps, short warpId)
    {
        if (warps is null) return null;
        if (!warps.TryGetValue(warpId, out var warp)) return null;
        if (!warps.TryGetValue(warp.DestinationId, out var partner)) return null;
        return partner.ClusterId;
    }
}
