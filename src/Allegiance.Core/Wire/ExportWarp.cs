namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_EXPORT</c> (id 26) for <c>OT_warp</c> (7) — the warp half of the
/// generic object-export message, mirroring <see cref="ExportCluster"/>
/// (OT_cluster) and <see cref="ExportShip"/> (OT_ship). This is what fills a
/// native client's <c>GetWarps()</c> lists (populated by
/// <c>CclusterIGC::Initialize</c> aggregating exports whose
/// <c>DataWarpIGC.clusterID</c> equals the cluster's id), so that the client's
/// warp walk in <c>FindTarget</c> (<c>src/Igc/common.cpp:677-707</c>,
/// adjacent-cluster search) and station search (<c>common.cpp:500-501</c>)
/// have something to search instead of returning NULL — the root cause behind
/// the P34 NULL <c>FindTarget</c>. The reference server exports every warp of
/// every cluster to a joining client (<c>FedSrv/fsmission.cpp:4176-4183</c>).
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 26
///   offset 4: VarItem (ib, cb) for exportData
///   offset 8: int32   objecttype   (4-byte; [v1_enum] AGCObjectType) = OT_warp (7)
///   offset 12: exportData blob = DataWarpIGC (108 B fixed)
///   = 120 B message, no further blobs.
/// </code>
/// <c>objecttype</c> is the same <c>[v1_enum]</c> <c>AGCObjectType</c> 4-byte
/// int32 as the other EXPORT codecs; <c>OT_warp</c> = <c>AGC_Warp</c> = 7
/// (<c>src/Igc/igc.h:114-160</c>). The blob is the 108-byte
/// <see cref="DataWarpIGC"/> (§OT_warp, <c>DataWarpIGC.cs</c>). Constructed
/// from a <see cref="Model.Sector"/>'s <see cref="Model.ResolvedWarp"/>
/// (<c>ExportWarp.FromWarp</c>, taking the raw <see cref="Model.ResolvedWarp.Warp"/>
/// — <c>Destination</c> is a resolution <i>result</i>, not wire data); the
/// record's <see cref="DataWarpIGC.Encode"/> does the fixed-payload
/// serialization. See <c>P35_EXPORT_WARP_STATION.md</c> in the repo root.
/// </summary>
public sealed class ExportWarp : IFedMessage
{
    public const ushort Fmid = 26;

    /// <summary><c>OT_warp</c> = <c>AGC_Warp</c> = 7 — the only object type this codec emits.</summary>
    public const int OtWarp = 7;

    /// <summary>Wire width of the fixed <see cref="DataWarpIGC"/> payload.</summary>
    public const int DataWarpSize = DataWarpIGC.Size;   // 108

    /// <summary>Wire width of the whole message: header(4) + var item(4) + objecttype(4) + DataWarpIGC(108).</summary>
    public const int FixedSize = 12 + DataWarpSize;     // 120

    public int ObjectType = OtWarp;          // int32 (not short) — [v1_enum] AGCObjectType
    public DataWarpIGC Warp;                 // the warp's OT_warp record

    public ushort GetFmid() => Fmid;

    /// <summary>Convenience factory from a live <see cref="Model.Sector"/>'s resolved warp.</summary>
    public static ExportWarp FromWarp(Model.ResolvedWarp rw) => new() { Warp = rw.Warp };

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotIb = w.Reserve(2);            // var-item ib
        int slotCb = w.Reserve(2);            // var-item cb

        w.WriteI32(ObjectType);               // objecttype — int32 at offset 8, 4-aligned, no pad

        // exportData blob (offset 12) = DataWarpIGC (108 B fixed).
        int blobStart = w.Position;
        w.WriteBytes(Warp.Encode());
        int blobEnd = w.Position;
        if (blobEnd - blobStart != DataWarpSize)
            throw new InvalidOperationException($"EXPORT DataWarpIGC layout drift: {blobEnd - blobStart} != {DataWarpSize}");

        w.WriteU16At(slotIb, (ushort)blobStart);
        w.WriteU16At(slotCb, (ushort)(blobEnd - blobStart));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"EXPORT warp layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ExportWarp Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"EXPORT warp needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected EXPORT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        int objectType = BitConverter.ToInt32(buf.Slice(8));   // int32, offset 8
        if (objectType != OtWarp)
            throw new ArgumentException($"objecttype={objectType} != OT_warp {OtWarp} (this codec only emits warps)");
        if (vi.Cb != DataWarpSize)
            throw new ArgumentException($"exportData cb={vi.Cb} != DataWarpIGC {DataWarpSize}");

        var warp = DataWarpIGC.Decode(buf.Slice(vi.Ib, vi.Cb));

        return new ExportWarp { ObjectType = objectType, Warp = warp };
    }
}
