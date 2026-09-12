using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_CREATE_CHAFF</c> (id 109, <c>Messages.h:731-737</c>) — the server's
/// notice that a fresh chaff decoy appeared, carrying everything the recipient
/// needs to reconstruct it (<c>position</c>, <c>velocity</c>, birth <c>time</c>,
/// and the chaff <em>type</em> id). Native shape:
/// <code>
///   DEFINE_FEDMSG(S, CREATE_CHAFF, 109)
///     Vector            p0;
///     Vector            v0;
///     Time              time0;
///     ExpendableTypeID  etid;
///   END_FEDMSG
/// </code>
/// Built by the server's <c>HandleFireExpendable</c> as the second of the four
/// messages in the chaff-deployment broadcast flow. Fields: two 12-byte
/// <see cref="Vector3"/> (4-aligned), a <c>uint</c> time slot (<c>Time</c> →
/// <c>uint</c>, this project's established wire convention), and a
/// <c>short</c> <c>ExpendableTypeID</c> (<c>igc.h:497</c>).
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 109
///   offset 4: Vector p0       (12 bytes, 4-aligned)
///   offset 16: Vector v0      (12 bytes)
///   offset 28: uint   time0   (Time)
///   offset 32: short  etid    (ExpendableTypeID)
///   offset 34: 2 pad bytes    (round 4-aligned struct to 36)
///   = 36 B fixed
/// </code>
public sealed class CreateChaff : IFedMessage
{
    public const ushort Fmid = 109;
    public const int FixedSize = 36;   // header + p0(12) + v0(12) + time0(4) + etid(2) + 2 pad

    /// <summary>The chaff decoy's spawn position (<c>Vector p0</c>).</summary>
    public Vector3 P0;

    /// <summary>The chaff decoy's constant coasting velocity (<c>Vector v0</c>).</summary>
    public Vector3 V0;

    /// <summary>The chaff's birth time (<c>Time</c> → <c>uint</c> — the port's
    /// wire-time convention, same as <c>FireMissile.TimeFired</c> /
    /// <c>ProjectileInstance</c>).</summary>
    public uint Time0;

    /// <summary>The id of the <c>DataChaffTypeIGC</c> this decoy is made of
    /// (<c>ExpendableTypeID</c>, a <c>short</c> — e.g. <c>DataChaffTypeIGC.ExpendableTypeId</c>).</summary>
    public short Etid;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        WriteVec(ref w, P0);
        WriteVec(ref w, V0);
        w.WriteU32(Time0);          // offset 28: time0 (Time → uint)
        w.WriteI16(Etid);           // offset 32: etid (ExpendableTypeID short)
        while (w.Position < FixedSize) w.WriteByte(0);   // align-4 pad (offsets 34-35)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"CREATE_CHAFF internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static CreateChaff Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"CREATE_CHAFF needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected CREATE_CHAFF fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                // header
        var p0 = ReadVec(ref r);
        var v0 = ReadVec(ref r);
        uint time0 = r.ReadU32();
        short etid = r.ReadI16();
        return new CreateChaff { P0 = p0, V0 = v0, Time0 = time0, Etid = etid };
    }

    // Same established 12-byte three-float LE helper as every vector-bearing wire codec.
    private static void WriteVec(ref WireWriter w, Vector3 v)
    {
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.X));
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.Y));
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.Z));
    }

    private static Vector3 ReadVec(ref WireReader r) =>
        new(BitConverter.UInt32BitsToSingle(r.ReadU32()),
            BitConverter.UInt32BitsToSingle(r.ReadU32()),
            BitConverter.UInt32BitsToSingle(r.ReadU32()));
}
