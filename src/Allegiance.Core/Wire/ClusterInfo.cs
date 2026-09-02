namespace Allegiance.Core.Wire;

using System.Buffers.Binary;

/// <summary>
/// <c>FM_S_CLUSTERINFO</c> (id 200) — the wire message the reference server
/// sends to every joining bot at game start so the bot can tell when it has
/// explored enough (<c>src/Inc/Messages.h:1039</c>: <i>"BT - WOPR - Give the
/// bots extra data at game start about the map so that they can determine when
/// they have discovered enough stuff."</i>). A compact cluster-connectivity
/// graph — cluster IDs and warp endpoints only, no positions.
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 200
///   offset 4: SectorID[32]  clusterIDs         — every cluster id, -1-terminated
///   offset 68: bool[32]     homeSectors        — flags parallel to clusterIDs by index
///   offset 100: SectorID[1026] warpFromClusterIDs — source cluster id per edge
///   offset 2152: SectorID[1026] warpToClusterIDs   — destination cluster id per edge
///   = 4204 B message, fixed.
/// </code>
/// <c>SectorID</c> is a <c>short</c> (2 B, LE), native <c>bool</c> is 1 B — the
/// whole struct is naturally aligned (every short array starts at an even
/// offset, the single bool array at a 1-boundary), so no padding is inserted
/// and the wire is exactly the sequential byte copy of the C++ struct. This is
/// what the managed client side already decodes byte-for-byte
/// (<c>src/Wopr/AllegianceInterop/AllMessages.h:10165-10207</c>,
/// <c>FMD_S_CLUSTERINFO</c>), and that code is <b>unchanged</b> — this codec
/// exists solely to reproduce, server-side, the byte stream that client parses.
/// </summary>
/// <remarks>
/// <para><b>Sentinel convention.</b> As in native
/// (<c>FedSrv/fsmission.cpp:4127-4163</c>), a <c>-1</c> value serves <i>both</i>
/// as the array terminator and (redundantly, in native's per-item writes) as
/// the empty-slot filler. The two arrays that store <c>-1</c> are
/// <c>clusterIDs</c> and the two warp arrays; <c>homeSectors</c> is a pure flag
/// array parallel to <c>clusterIDs</c> by index, <i>not</i> terminated — the
/// managed client's parsing loop (<c>AllMessages.h:10176</c>) walks
/// <c>clusterIDs</c> until <c>-1</c> and reads <c>homeSectors[i]</c> at the
/// same index, so the home flag list must be in exactly the same order as the
/// id list. This codec reproduces native's wire exactly: a single trailing
/// <c>-1</c> after the last id and after the last warp edge, zero-filled
/// remainder — observably identical to native's per-item <c>-1</c> writes
/// (that list ends the same way). Built from the same cluster/warp data
/// <see cref="ExportCluster"/>'s send site iterates
/// (<c>Server.cs:808-812</c>). Dangling <see cref="Model.ResolvedWarp.Destination"/>
/// (null) edges are <b>skipped, not guessed</b> — the ship-against-map
/// honesty about unresolved references (<c>Model/ResolvedWarp.cs:28-32</c>).
/// Counts verified against the map dump: 13 clusters, 38 warps, 0 dangling —
/// comfortably inside the <c>31</c>-usable-cluster / <c>1025</c>-warp ceilings
/// implied by the 32/1026 array widths plus their <c>-1</c> terminator slots.
/// See <c>CLUSTERINFO_P47.md</c> in the repo root.
/// </remarks>
public sealed class ClusterInfo : IFedMessage
{
    public const ushort Fmid = 200;

    /// <summary>Width of the <c>clusterIDs</c> array (slot 0 is reserved for the leading <c>-1</c>).</summary>
    public const int MaxClusters = 32;
    /// <summary>Width of the <c>warpFrom/warpTo</c> arrays (slot 0 reserved for the leading <c>-1</c>).</summary>
    public const int MaxWarps = 1026;

    public const int MaxClustersBytes = MaxClusters * sizeof(short);   // 64
    public const int MaxHomeBytes = MaxClusters;                       // 32
    public const int MaxWarpsBytes = MaxWarps * sizeof(short);         // 2052

    /// <summary>Wire width of the whole message: header(4) + clusterIDs(64) + homeSectors(32) + two warp arrays(2×2052).</summary>
    public const int FixedSize = 4 + MaxClustersBytes + MaxHomeBytes + 2 * MaxWarpsBytes;   // 4204

    /// <summary>One decoded cluster entry: its id and whether it is a home sector.</summary>
    public readonly record struct ClusterEntry(short Id, bool HomeSector);

    /// <summary>One decoded warp edge: source cluster id → destination cluster id.</summary>
    public readonly record struct WarpEdge(short SourceClusterId, short DestClusterId);

    /// <summary>The clusters (<c>clusterIDs</c>/<c>homeSectors</c>), in wire order.</summary>
    public IReadOnlyList<ClusterEntry> Clusters { get; init; } = [];

    /// <summary>The warp edges (<c>warpFromClusterIDs</c>/<c>warpToClusterIDs</c>), in wire order.</summary>
    public IReadOnlyList<WarpEdge> Warps { get; init; } = [];

    public ushort GetFmid() => Fmid;

    /// <summary>Convenience factory from the assembled mission sectors — the exact data the
    /// cluster <see cref="ExportCluster"/> burst iterates (<c>Server.cs:808-812</c>). One
    /// cluster entry per sector (<c>Sector.Id</c>, <c>Sector.Cluster.HomeSector</c>); one warp
    /// edge per warp, source = the owning sector's id, destination =
    /// <c>ResolvedWarp.Destination.Id</c>. A warp whose destination is <c>null</c> (a dangling
    /// edge) is skipped, not fabricated.</summary>
    public static ClusterInfo FromSectors(IReadOnlyList<Model.Sector> sectors)
    {
        var clusters = new List<ClusterEntry>(sectors.Count);
        var warps = new List<WarpEdge>();
        foreach (var sector in sectors)
        {
            clusters.Add(new ClusterEntry(sector.Id, sector.Cluster.HomeSector));
            foreach (var resolved in sector.Warps)
            {
                if (resolved.Destination is null)
                {
                    Console.WriteLine($"[clusterinfo] skipping dangling warp {resolved.Warp.WarpId} (destination unresolved)");
                    continue;
                }
                warps.Add(new WarpEdge(sector.Id, resolved.Destination.Id));
            }
        }
        return new ClusterInfo { Clusters = clusters, Warps = warps };
    }

    public byte[] Encode()
    {
        if (Clusters.Count > MaxClusters - 1)
            throw new InvalidOperationException($"too many clusters for CLUSTERINFO: {Clusters.Count} > {MaxClusters - 1}");
        if (Warps.Count > MaxWarps - 1)
            throw new InvalidOperationException($"too many warps for CLUSTERINFO: {Warps.Count} > {MaxWarps - 1}");

        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);        // cbmsg placeholder
        w.WriteU16(Fmid);     // offset 4

        // clusterIDs[32] (offset 4, 64 B) — the list is a FIXED region; write
        // each id into its fixed slot, then a trailing -1 in the next slot. The
        // remaining slots are the zeroed remainder (matching native's
        // ZeroMemory then indexed writes, fsmission.cpp:4127-4134). We must
        // reach absolute offset 68 for homeSectors regardless of how few ids
        // we wrote.
        for (int i = 0; i < Clusters.Count; i++) w.WriteI16(Clusters[i].Id);
        w.WriteI16(-1);
        w.WriteZeros(MaxClustersBytes - (Clusters.Count + 1) * sizeof(short));   // pad to the end of clusterIDs[32]

        // homeSectors[32] (offset 68) — flags parallel to clusterIDs by INDEX,
        // no terminator. Write each flag into its fixed slot, then zero-pad.
        foreach (var c in Clusters) w.WriteByte((byte)(c.HomeSector ? 1 : 0));
        w.WriteZeros(MaxHomeBytes - Clusters.Count);

        // warpFromClusterIDs[1026] (offset 100, 2052 B) — source id per edge,
        // then a -1 terminator in the next slot, zero-padded to the region end.
        foreach (var e in Warps) w.WriteI16(e.SourceClusterId);
        w.WriteI16(-1);
        w.WriteZeros(MaxWarpsBytes - (Warps.Count + 1) * sizeof(short));

        // warpToClusterIDs[1026] (offset 2152, 2052 B) — destination id per edge,
        // then a -1 terminator in the next slot, zero-padded to the region end.
        foreach (var e in Warps) w.WriteI16(e.DestClusterId);
        w.WriteI16(-1);
        w.WriteZeros(MaxWarpsBytes - (Warps.Count + 1) * sizeof(short));

        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"CLUSTERINFO layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ClusterInfo Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"CLUSTERINFO needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected CLUSTERINFO fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        // clusterIDs / homeSectors — walk clusterIDs until -1 (or array end).
        var clusters = new List<ClusterEntry>();
        for (int i = 0; i < MaxClusters; i++)
        {
            short id = BinaryPrimitives.ReadInt16LittleEndian(buf.Slice(4 + i * 2));
            if (id == -1) break;
            bool home = buf[68 + i] != 0;
            clusters.Add(new ClusterEntry(id, home));
        }

        // warpFrom/warpTo — walk warpFrom until -1 (or array end), pairing by index.
        var warps = new List<WarpEdge>();
        for (int i = 0; i < MaxWarps; i++)
        {
            short from = BinaryPrimitives.ReadInt16LittleEndian(buf.Slice(100 + i * 2));
            if (from == -1) break;
            short to = BinaryPrimitives.ReadInt16LittleEndian(buf.Slice(2152 + i * 2));
            warps.Add(new WarpEdge(from, to));
        }

        return new ClusterInfo { Clusters = clusters, Warps = warps };
    }
}
