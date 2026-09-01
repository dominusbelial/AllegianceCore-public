namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_EXPORT</c> (id 26) for <c>OT_station</c> (1) — the station half of
/// the generic object-export message, mirroring <see cref="ExportCluster"/>
/// (OT_cluster) and <see cref="ExportShip"/> (OT_ship). This is what fills a
/// native client's <c>GetStations()</c> lists (populated by
/// <c>CclusterIGC::Initialize</c> aggregating exports whose
/// <c>DataStationIGC.clusterID</c> equals the cluster's id), so that the
/// client's station search in <c>FindTarget</c> (<c>src/Igc/common.cpp:500-501</c>)
/// finds a real station instead of returning NULL — part of the root-cause fix
/// behind the P34 NULL <c>FindTarget</c>. The reference server exports every
/// station of every cluster to a joining client
/// (<c>FedSrv/fsmission.cpp:4215-4222</c>).
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 26
///   offset 4: VarItem (ib, cb) for exportData
///   offset 8: int32   objecttype   (4-byte; [v1_enum] AGCObjectType) = OT_station (1)
///   offset 12: exportData blob = DataStationIGC (88 B fixed)
///   = 100 B message, no further blobs.
/// </code>
/// <c>objecttype</c> is the same <c>[v1_enum]</c> <c>AGCObjectType</c> 4-byte
/// int32 as the other EXPORT codecs; <c>OT_station</c> = <c>AGC_Station</c> =
/// 1 (<c>src/Igc/igc.h:114-160</c>). The blob is the 88-byte
/// <see cref="DataStationIGC"/> (§OT_station, <c>DataStationIGC.cs</c>).
/// Constructed directly from a <see cref="Model.Sector"/>'s
/// <see cref="Model.Sector.Stations"/> entries (each an already-decoded
/// <c>DataStationIGC</c>); the record's <see cref="DataStationIGC.Encode"/>
/// does the fixed-payload serialization. See <c>P35_EXPORT_WARP_STATION.md</c>
/// in the repo root.
/// </summary>
public sealed class ExportStation : IFedMessage
{
    public const ushort Fmid = 26;

    /// <summary><c>OT_station</c> = <c>AGC_Station</c> = 1 — the only object type this codec emits.</summary>
    public const int OtStation = 1;

    /// <summary>Wire width of the fixed <see cref="DataStationIGC"/> payload.</summary>
    public const int DataStationSize = DataStationIGC.Size;   // 88

    /// <summary>Wire width of the whole message: header(4) + var item(4) + objecttype(4) + DataStationIGC(88).</summary>
    public const int FixedSize = 12 + DataStationSize;        // 100

    public int ObjectType = OtStation;          // int32 (not short) — [v1_enum] AGCObjectType
    public DataStationIGC Station;              // the station's OT_station record

    public ushort GetFmid() => Fmid;

    /// <summary>Convenience factory from a live <see cref="Model.Sector"/>'s station record.</summary>
    public static ExportStation FromStation(DataStationIGC s) => new() { Station = s };

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotIb = w.Reserve(2);            // var-item ib
        int slotCb = w.Reserve(2);            // var-item cb

        w.WriteI32(ObjectType);               // objecttype — int32 at offset 8, 4-aligned, no pad

        // exportData blob (offset 12) = DataStationIGC (88 B fixed).
        int blobStart = w.Position;
        w.WriteBytes(Station.Encode());
        int blobEnd = w.Position;
        if (blobEnd - blobStart != DataStationSize)
            throw new InvalidOperationException($"EXPORT DataStationIGC layout drift: {blobEnd - blobStart} != {DataStationSize}");

        w.WriteU16At(slotIb, (ushort)blobStart);
        w.WriteU16At(slotCb, (ushort)(blobEnd - blobStart));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"EXPORT station layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ExportStation Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"EXPORT station needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected EXPORT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        int objectType = BitConverter.ToInt32(buf.Slice(8));   // int32, offset 8
        if (objectType != OtStation)
            throw new ArgumentException($"objecttype={objectType} != OT_station {OtStation} (this codec only emits stations)");
        if (vi.Cb != DataStationSize)
            throw new ArgumentException($"exportData cb={vi.Cb} != DataStationIGC {DataStationSize}");

        var station = DataStationIGC.Decode(buf.Slice(vi.Ib, vi.Cb));

        return new ExportStation { ObjectType = objectType, Station = station };
    }
}
