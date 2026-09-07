namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_EXPORT</c> (id 26) for <c>OT_mine</c> (3) — the mine half of the
/// generic object-export message, mirroring <see cref="ExportStation"/>
/// (OT_station), <see cref="ExportShip"/> (OT_ship), <see cref="ExportCluster"/>
/// (OT_cluster) and <see cref="ExportWarp"/> (OT_warp). Unlike missiles, mines
/// need no dedicated "instance" message (<c>PROJECTILE_INSTANCE</c> 12 /
/// <c>FIRE_MISSILE</c> 70): the reference server creates a mine through the
/// <b>same generic</b> <c>CreateObject(..., OT_mine, &amp;obj, sizeof(obj))</c>
/// path every other exported object uses (<c>FedSrv.CPP:10072</c>), and the
/// native client exports one via <c>CmineIGC::Export</c>
/// (<c>src/Igc/mineigc.cpp:158-181</c>) writing <c>sizeof(DataMineExport)</c>.
/// So this task is a new <c>ExportMine</c> codec sharing fmid 26 — not a new
/// fmid and not a var-item-array message like <c>FireMissile</c>'s.
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 26
///   offset 4: VarItem (ib, cb) for exportData
///   offset 8: int32   objecttype   (4-byte; [v1_enum] AGCObjectType) = OT_mine (3)
///   offset 12: exportData blob = DataMineExport (32 B fixed)
///   = 44 B message, no further blobs.
/// </code>
/// <c>objecttype</c> is the same <c>[v1_enum]</c> <c>AGCObjectType</c> 4-byte
/// int32 as the other EXPORT codecs; <c>OT_mine</c> = <c>AGC_Mine</c> = 3
/// (<c>src/Igc/igc.h:122</c>). The blob is the fixed 32-byte
/// <see cref="DataMineExport"/> (§OT_mine, <c>DataMineExport.cs</c>). The
/// enclosing var-item is the EXPORT envelope's own mechanism, not evidence the
/// blob varies in length — <c>DataMineExport</c> is fixed at 32 bytes and
/// <c>Decode</c> requires exactly that.
/// </summary>
public sealed class ExportMine : IFedMessage
{
    public const ushort Fmid = 26;

    /// <summary><c>OT_mine</c> = <c>AGC_Mine</c> = 3 (<c>src/Igc/igc.h:122</c>) — the only object type this codec emits.</summary>
    public const int OtMine = 3;

    /// <summary>Wire width of the fixed <see cref="DataMineExport"/> payload.</summary>
    public const int DataMineSize = DataMineExport.Size;   // 32

    /// <summary>Wire width of the whole message: header(4) + var item(4) + objecttype(4) + DataMineExport(32).</summary>
    public const int FixedSize = 12 + DataMineSize;        // 44

    public int ObjectType = OtMine;         // int32 (not short) — [v1_enum] AGCObjectType
    public DataMineExport Mine;             // the OT_mine record

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotIb = w.Reserve(2);            // var-item ib
        int slotCb = w.Reserve(2);            // var-item cb

        w.WriteI32(ObjectType);               // objecttype — int32 at offset 8, 4-aligned, no pad

        // exportData blob (offset 12) = DataMineExport (32 B fixed).
        int blobStart = w.Position;
        w.WriteBytes(Mine.Encode());
        int blobEnd = w.Position;
        if (blobEnd - blobStart != DataMineSize)
            throw new InvalidOperationException($"EXPORT DataMineExport layout drift: {blobEnd - blobStart} != {DataMineSize}");

        w.WriteU16At(slotIb, (ushort)blobStart);
        w.WriteU16At(slotCb, (ushort)(blobEnd - blobStart));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"EXPORT mine layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ExportMine Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"EXPORT mine needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected EXPORT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        int objectType = BitConverter.ToInt32(buf.Slice(8));   // int32, offset 8
        if (objectType != OtMine)
            throw new ArgumentException($"objecttype={objectType} != OT_mine {OtMine} (this codec only emits mines)");
        if (vi.Cb != DataMineSize)
            throw new ArgumentException($"exportData cb={vi.Cb} != DataMineExport {DataMineSize}");

        var mine = DataMineExport.Decode(buf.Slice(vi.Ib, vi.Cb));

        return new ExportMine { ObjectType = objectType, Mine = mine };
    }
}
