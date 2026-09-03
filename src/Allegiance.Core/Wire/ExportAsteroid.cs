namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_EXPORT</c> (id 26) for <c>OT_asteroid</c> (5) — the asteroid half
/// of the generic object-export message, mirroring <see cref="ExportCluster"/>
/// (OT_cluster), <see cref="ExportShip"/> (OT_ship), <see cref="ExportWarp"/>
/// (OT_warp) and <see cref="ExportStation"/> (OT_station). This is what fills
/// a native client's <c>GetAsteroids()</c> lists (populated by
/// <c>CclusterIGC::Initialize</c> aggregating exports whose
/// <c>DataAsteroidIGC.ClusterId</c> equals the cluster's id), so that the
/// client's asteroid search in <c>CommanderResearchAndExpand
/// .UndockMinersAndStartMining</c> (<c>src/Wopr/Wopr/Strategies/
/// CommanderResearchAndExpand.cs:1563-1568</c>, a
/// <c>GetClusters().Where(p => p.GetAsteroids().Where(r =>
/// r.GetName().StartsWith("He")).Sum(x => x.GetOre()) &gt; 150)</c> filter over
/// <c>GetAsteroids()</c>) finally has real, He3-named rocks to search — the
/// P64 gap. The reference server exports every asteroid of every cluster to a
/// joining client (<c>FedSrv/fsmission.cpp:4223-4231</c>, after the stations).
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 26
///   offset 4: VarItem (ib, cb) for exportData
///   offset 8: int32   objecttype   (4-byte; [v1_enum] AGCObjectType) = OT_asteroid (5)
///   offset 12: exportData blob = DataAsteroidIGC (144 B fixed)
///   = 156 B message, no further blobs.
/// </code>
/// <c>objecttype</c> is the same <c>[v1_enum]</c> <c>AGCObjectType</c> 4-byte
/// int32 as the other EXPORT codecs; <c>OT_asteroid</c> = <c>AGC_Asteroid</c> =
/// 5 (<c>Inc/AGCIDL.idl:313</c>, cf. <c>src/Igc/igc.h:114-160</c>). The blob
/// is the 144-byte <see cref="DataAsteroidIGC"/> (§OT_asteroid,
/// <c>DataAsteroidIGC.cs</c>). Constructed from a <see cref="Model.Sector"/>'s
/// <see cref="Model.Sector.Asteroids"/> entries (each an already-decoded
/// <c>DataAsteroidIGC</c>); the record's <see cref="DataAsteroidIGC.Encode"/>
/// does the fixed-payload serialization. See
/// <c>EXPORT_ASTEROIDS_P64.md</c> in the repo root.
/// </summary>
public sealed class ExportAsteroid : IFedMessage
{
    public const ushort Fmid = 26;

    /// <summary><c>OT_asteroid</c> = <c>AGC_Asteroid</c> = 5 — the only object type this codec emits.</summary>
    public const int OtAsteroid = 5;

    /// <summary>Wire width of the fixed <see cref="DataAsteroidIGC"/> payload.</summary>
    public const int DataAsteroidSize = DataAsteroidIGC.Size;   // 144

    /// <summary>Wire width of the whole message: header(4) + var item(4) + objecttype(4) + DataAsteroidIGC(144).</summary>
    public const int FixedSize = 12 + DataAsteroidSize;        // 156

    public int ObjectType = OtAsteroid;          // int32 (not short) — [v1_enum] AGCObjectType
    public DataAsteroidIGC Asteroid;            // the asteroid's OT_asteroid record

    public ushort GetFmid() => Fmid;

    /// <summary>Convenience factory from a live <see cref="Model.Sector"/>'s asteroid record.</summary>
    public static ExportAsteroid FromAsteroid(DataAsteroidIGC a) => new() { Asteroid = a };

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotIb = w.Reserve(2);            // var-item ib
        int slotCb = w.Reserve(2);            // var-item cb

        w.WriteI32(ObjectType);               // objecttype — int32 at offset 8, 4-aligned, no pad

        // exportData blob (offset 12) = DataAsteroidIGC (144 B fixed).
        int blobStart = w.Position;
        w.WriteBytes(Asteroid.Encode());
        int blobEnd = w.Position;
        if (blobEnd - blobStart != DataAsteroidSize)
            throw new InvalidOperationException($"EXPORT DataAsteroidIGC layout drift: {blobEnd - blobStart} != {DataAsteroidSize}");

        w.WriteU16At(slotIb, (ushort)blobStart);
        w.WriteU16At(slotCb, (ushort)(blobEnd - blobStart));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"EXPORT asteroid layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ExportAsteroid Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"EXPORT asteroid needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected EXPORT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        int objectType = BitConverter.ToInt32(buf.Slice(8));   // int32, offset 8
        if (objectType != OtAsteroid)
            throw new ArgumentException($"objecttype={objectType} != OT_asteroid {OtAsteroid} (this codec only emits asteroids)");
        if (vi.Cb != DataAsteroidSize)
            throw new ArgumentException($"exportData cb={vi.Cb} != DataAsteroidIGC {DataAsteroidSize}");

        var asteroid = DataAsteroidIGC.Decode(buf.Slice(vi.Ib, vi.Cb));

        return new ExportAsteroid { ObjectType = objectType, Asteroid = asteroid };
    }
}
