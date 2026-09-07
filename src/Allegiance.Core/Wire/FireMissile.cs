namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_FIRE_MISSILE</c> (id 70, <c>Messages.h:547-557</c>) — the
/// client→server launch request, rebroadcast by the server to the firing
/// ship's sector group (<c>FedSrv.CPP:5408-5507</c>), carrying a
/// variable-length <c>MissileLaunchData[]</c> array (one entry per missile in
/// a salvo, see <see cref="MissileLaunchData"/>) plus the fixed lock/time/
/// target metadata. Native shape:
/// <code>
///   DEFINE_FEDMSG(CS, FIRE_MISSILE, 70)
///     FM_VAR_ITEM(missileLaunchData);
///     float               lock;
///     Time                timeFired;
///     ExpendableTypeID    missiletypeID;
///     ShipID              launcherID;
///     SectorID            clusterID;
///     ObjectType          targetType;
///     ObjectID            targetID;
///     bool                bDud;
///   END_FEDMSG
/// </code>
/// Macros: var items MUST precede fixed fields (messagecore.h:779-781), so the
/// <c>ib/cb</c> descriptor occupies offsets 4-6; blobs are appended at
/// <c>pbFM + sizeof(FMD_CS_FIRE_MISSILE)</c> (Messages.cpp:404), i.e. the
/// struct's padded size. <c>Time</c> → <c>uint</c>, this project's established
/// wire convention for a time slot (cf. ProjectileInstance.cs:17 /
/// <c>ShipUpdate.cs:31</c>). All the ID fields — <c>ExpendableTypeID</c>(
/// <c>igc.h:497</c>), <c>ShipID</c>(<c>igc.h:480</c>), <c>SectorID</c>(
/// <c>igc.h:487</c>), <c>ObjectID</c>/<c>MissileID</c>(<c>igc.h:477,493</c>) —
/// are <c>short</c>. <c>ObjectType</c> is the <c>[v1_enum]</c> 4-byte
/// <c>AGCObjectType</c> int32 (AGCIDL.idl:303), the same width
/// <c>ExportShip.cs:115</c> uses — the struct's 4-byte alignment forces 2 pad
/// bytes after <c>clusterID</c> so <c>targetType</c> lands on a 4-boundary,
/// matching the natural-MSVC-alignment default every ported codec here uses.
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 70
///   offset  4: IB ib (var descriptor)         = 32 (start of the array blob)
///   offset  6: CB cb (var descriptor)         = 40 · n
///   offset  8: float  lock
///   offset 12: uint   timeFired               (Time)
///   offset 16: short  missiletypeID          (ExpendableTypeID)
///   offset 18: short  launcherID            (ShipID)
///   offset 20: short  clusterID             (SectorID)
///   offset 22: pad 2                         (align targetType to 4)
///   offset 24: int32  targetType             ([v1_enum] AGCObjectType)
///   offset 28: short  targetID              (ObjectID)
///   offset 30: byte   bDud
///   offset 31: pad 1                         (round fixed region to struct align 4)
///   = 32 B fixed region
///   offset 32: MissileLaunchData[n]          each 40 B (see MissileLaunchData.Size)
///   = 32 + 40·n B message total
/// </code>
/// This is a wire-only codec: encode/decode/round-trip, nothing that runs in
/// a real tick. Sim integration (a minimal non-homing <c>Missile</c> object, a
/// <c>Ship.FireMissile</c>-style trigger, same-side broadcast) is a separate
/// follow-up, deliberately not reached for here.
/// </summary>
public sealed class FireMissile : IFedMessage
{
    public const ushort Fmid = 70;

    /// <summary>
    /// Wire width of the fixed region: header(4) + var descriptor(4) + lock(4)
    /// + timeFired(4) + three shorts(6) + 2 pad + targetType(4) + targetID(2)
    /// + bDud(1) + 1 trailing pad = 32. Same <c>sizeof(FMD_CS_FIRE_MISSILE)</c>
    /// the client passes as <c>cbfm</c> (messagecore.h:724), which equals the
    /// var-blob offset (Messages.cpp:404).
    /// </summary>
    public const int FixedSize = 32;

    public float Lock;
    public uint TimeFired;
    public short MissileTypeId;      // ExpendableTypeID
    public short LauncherId;         // ShipID
    public short ClusterId;          // SectorID
    public int TargetType;           // [v1_enum] AGCObjectType — 4-byte int32 (not short)
    public short TargetId;           // ObjectID; NA(-1) when there is no target
    public bool BDud;

    /// <summary>
    /// The <c>MissileLaunchData[]</c> tail (<c>Count = Entries.Count</c>), one
    /// entry per missile in the salvo, in launch order. May be empty — a zero
    /// entry is legal (cb = 0, no blob) though a real salvo always has ≥ 1.
    /// </summary>
    public IReadOnlyList<MissileLaunchData> Entries { get; init; } = [];

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        int n = Entries.Count;
        int total = FixedSize + n * MissileLaunchData.Size;
        var buf = new byte[total];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);

        int slotIb = w.Position; w.Advance(2);   // var-item ib (reserve)
        int slotCb = w.Position; w.Advance(2);   // var-item cb (reserve)

        w.WriteU32(BitConverter.SingleToUInt32Bits(Lock));   // lock
        w.WriteU32(TimeFired);                               // timeFired (Time → uint)
        w.WriteI16(MissileTypeId);
        w.WriteI16(LauncherId);
        w.WriteI16(ClusterId);
        w.WriteZeros(2);                                    // pad: align targetType to 4
        w.WriteI32(TargetType);
        w.WriteI16(TargetId);
        w.WriteByte(BDud ? (byte)1 : (byte)0);
        w.WriteByte(0);                                     // trailing pad: round fixed region to 32
        if (w.Position != FixedSize) throw new InvalidOperationException($"FIRE_MISSILE fixed-region drift: {w.Position} != {FixedSize}");

        int blobStart = w.Position;
        foreach (var e in Entries) e.Write(ref w);
        int blobEnd = w.Position;
        if (blobEnd - blobStart != n * MissileLaunchData.Size)
            throw new InvalidOperationException($"FIRE_MISSILE array drift: {blobEnd - blobStart} != {n} × {MissileLaunchData.Size}");

        w.WriteU16At(slotIb, (ushort)blobStart);
        w.WriteU16At(slotCb, (ushort)(blobEnd - blobStart));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != total) throw new InvalidOperationException($"FIRE_MISSILE layout drift: {w.Position} != {total}");
        return buf;
    }

    public static FireMissile Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"FIRE_MISSILE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected FIRE_MISSILE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        VarItem vi = VarItem.Read(buf.Slice(4));
        if (vi.Cb % MissileLaunchData.Size != 0)
            throw new ArgumentException($"missileLaunchData cb={vi.Cb} is not a whole multiple of MissileLaunchData({MissileLaunchData.Size})");
        int n = vi.Cb / MissileLaunchData.Size;

        WireReader r = new(buf);
        r.Advance(8);                               // past header + var descriptor
        float lockVal = BitConverter.UInt32BitsToSingle(r.ReadU32());
        uint timeFired = r.ReadU32();
        short missileTypeId = r.ReadI16();
        short launcherId = r.ReadI16();
        short clusterId = r.ReadI16();
        r.Advance(2);                               // pad
        int targetType = r.ReadI32();
        short targetId = r.ReadI16();
        bool bDud = r.ReadByte() != 0;
        r.Advance(1);                               // trailing pad
        if (r.Position != FixedSize) throw new ArgumentException($"FIRE_MISSILE fixed-region underread: {r.Position} != {FixedSize}");

        var entries = new List<MissileLaunchData>(n);
        for (int i = 0; i < n; i++) entries.Add(MissileLaunchData.Read(ref r));

        return new FireMissile
        {
            Lock = lockVal,
            TimeFired = timeFired,
            MissileTypeId = missileTypeId,
            LauncherId = launcherId,
            ClusterId = clusterId,
            TargetType = targetType,
            TargetId = targetId,
            BDud = bDud,
            Entries = entries,
        };
    }
}
