namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_EXPORT</c> (id 26) for <c>OT_cluster</c> (13) — the cluster/sector
/// half of the generic object-export message, mirroring <see cref="ExportShip"/>
/// (OT_ship, the ship half). This is the message that makes a joining client's
/// <c>flyingF()</c> (<c>clintlib.h:1198</c>:
/// <c>m_ship != NULL &amp;&amp; m_ship-&gt;GetCluster() != NULL</c>) become true:
/// the reference server exports every cluster to a joining client
/// (<c>FedSrv/fsmission.cpp:4170-4173</c>:
/// <c>ExportObj(pcluster, OT_cluster, NULL)</c>) so that when the client
/// later receives <c>FM_S_SET_CLUSTER</c>, its handler's
/// <c>m_pCoreIGC-&gt;GetCluster(sectorID)</c> (<c>clintlib/appmsg.cpp:1003</c>)
/// resolves a real <c>CclusterIGC</c> and assigns the ship to it.
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 26
///   offset 4: VarItem (ib, cb) for exportData
///   offset 8: int32   objecttype   (4-byte; [v1_enum] AGCObjectType) = OT_cluster (13)
///   offset 12: exportData blob = DataClusterIGC (92 B fixed)
///   = 104 B message, no further blobs.
/// </code>
/// <c>objecttype</c> is the same <c>[v1_enum]</c> <c>AGCObjectType</c> 4-byte
/// int32 as <see cref="ExportShip"/>; <c>OT_cluster</c> = <c>AGC_Cluster</c> =
/// 13 (<c>AGCIDL.idl</c>). The blob is the 92-byte <see cref="DataClusterIGC"/>
/// (§OT_cluster, <c>DataClusterIGC.cs</c>). Created by
/// <c>ExportCluster.FromSector</c> — which, unlike <see cref="ExportShip"/>
/// (whose hull/part data has no Data/* backing and is flattened inline), can
/// simply hold the already-decoded record and let <see cref="DataClusterIGC.Encode"/>
/// do the fixed-payload serialization. See <c>P30_EXPORT_CLUSTER.md</c> in the
/// repo root.
/// </summary>
public sealed class ExportCluster : IFedMessage
{
    public const ushort Fmid = 26;

    /// <summary><c>OT_cluster</c> = <c>AGC_Cluster</c> = 13 — the only object type this codec emits.</summary>
    public const int OtCluster = 13;

    /// <summary>Wire width of the fixed <see cref="DataClusterIGC"/> payload.</summary>
    public const int DataClusterSize = DataClusterIGC.Size;   // 92

    /// <summary>Wire width of the whole message: header(4) + var item(4) + objecttype(4) + DataClusterIGC(92).</summary>
    public const int FixedSize = 12 + DataClusterSize;        // 104

    public int ObjectType = OtCluster;          // int32 (not short) — [v1_enum] AGCObjectType
    public DataClusterIGC Cluster;              // the sector's OT_cluster record

    public ushort GetFmid() => Fmid;

    /// <summary>Convenience factory from a live <see cref="Model.Sector"/>.</summary>
    public static ExportCluster FromSector(Model.Sector sector) => new() { Cluster = sector.Cluster };

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotIb = w.Reserve(2);            // var-item ib
        int slotCb = w.Reserve(2);            // var-item cb

        w.WriteI32(ObjectType);               // objecttype — int32 at offset 8, 4-aligned, no pad

        // exportData blob (offset 12) = DataClusterIGC (92 B fixed).
        int blobStart = w.Position;
        w.WriteBytes(Cluster.Encode());
        int blobEnd = w.Position;
        if (blobEnd - blobStart != DataClusterSize)
            throw new InvalidOperationException($"EXPORT DataClusterIGC layout drift: {blobEnd - blobStart} != {DataClusterSize}");

        w.WriteU16At(slotIb, (ushort)blobStart);
        w.WriteU16At(slotCb, (ushort)(blobEnd - blobStart));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"EXPORT cluster layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ExportCluster Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"EXPORT cluster needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected EXPORT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        int objectType = BitConverter.ToInt32(buf.Slice(8));   // int32, offset 8
        if (objectType != OtCluster)
            throw new ArgumentException($"objecttype={objectType} != OT_cluster {OtCluster} (this codec only emits clusters)");
        if (vi.Cb != DataClusterSize)
            throw new ArgumentException($"exportData cb={vi.Cb} != DataClusterIGC {DataClusterSize}");

        var cluster = DataClusterIGC.Decode(buf.Slice(vi.Ib, vi.Cb));

        return new ExportCluster { ObjectType = objectType, Cluster = cluster };
    }
}
