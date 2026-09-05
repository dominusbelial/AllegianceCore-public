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

    /// <summary>
    /// Convenience factory from a live <see cref="Model.Sector"/>'s station record.
    /// Guards a degenerate orientation (<c>Forward</c>/<c>Up</c> whose native
    /// cross product is the zero vector) by substituting the native client's own
    /// identity fallback before constructing the message. Root-caused in P79: both
    /// Garrison stations in <c>m2xspiderw.igc</c> carry <c>up=fwd=(0,0,0)</c>, which
    /// makes the reference client's <c>Orientation::SetUp</c>
    /// (<c>src/zlib/orientation.h:115-122</c>) fire its
    /// <c>ZAssert(vec.LengthSquared() != 0)</c> on <b>every</b> connection,
    /// flushing a `.dmp` per assert. See <c>ECONDEMO_CONCURRENT_CONNECT_CRASH_P79.md</c>.
    /// </summary>
    public static ExportStation FromStation(DataStationIGC s)
    {
        return new() { Station = DegenerateCrossProduct(s.Forward, s.Up)
            ? SafeFallback(s) : s };
    }

    /// <summary>
    /// True when the native cross product <c>CrossProduct(Forward, Up)</c> is the
    /// zero vector (all three components exactly <c>0f</c> — the native assert's own
    /// test is exact <c>LengthSquared() != 0</c>, so no epsilon). This is the exact
    /// precondition that makes <c>Orientation::Set</c>'s
    /// <c>SetUp(CrossProduct(rightAxis, forwardAxis))</c> feed a zero vector into
    /// <c>SetUp</c>'s <c>ZAssert</c>. A parallel pair like <c>Forward=(1,0,0),
    /// Up=(2,0,0)</c> (cross product genuinely zero, neither input zero) is caught
    /// too, not just the literal <c>(0,0,0),(0,0,0)</c> the P79 map happened to carry.
    /// Formula ported verbatim from <c>src/zlib/vector.cpp:62-71</c>
    /// (<c>rightAxis = (a.y*b.z - b.y*a.z, a.z*b.x - b.z*a.x, a.x*b.y - b.x*a.y)</c>),
    /// same float arithmetic and operand order.
    /// </summary>
    private static bool DegenerateCrossProduct(float[]? forward, float[]? up)
    {
        // Non-length-3 is garbage we cannot meaningfully forward — treat as degenerate
        // rather than let a malformed record trigger the client assert.
        if (forward is not { Length: 3 } f || up is not { Length: 3 } u) return true;

        float cx = f[1] * u[2] - u[1] * f[2];
        float cy = f[2] * u[0] - u[2] * f[0];
        float cz = f[0] * u[1] - u[0] * f[1];
        return cx == 0f && cy == 0f && cz == 0f;
    }

    /// <summary>
    /// Rebuild the station with only <c>Forward</c>/<c>Up</c> swapped to the native
    /// client's own identity fallback — the value <c>Orientation::Reset()</c>
    /// (<c>src/zlib/orientation.cpp:307-311</c>) resolves to when read through
    /// <c>GetForward()</c>/<c>GetUp()</c> (<c>src/zlib/orientation.h:83-98</c>):
    /// <c>Forward=(0,0,-1)</c>, <c>Up=(0,1,0)</c>. <c>Position</c>, <c>Rotation</c> and
    /// every other field pass through unchanged.
    /// </summary>
    private static DataStationIGC SafeFallback(DataStationIGC s) => s with
    {
        Forward = [0f, 0f, -1f],
        Up = [0f, 1f, 0f],
    };

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
