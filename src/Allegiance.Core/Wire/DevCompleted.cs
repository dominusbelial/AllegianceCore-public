namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_DEV_COMPLETED</c> (id 8, <c>src/Inc/Messages.h:89-91</c>, quoted in
/// full):
/// <code>
///   DEFINE_FEDMSG(S, DEV_COMPLETED, 8)
///     DevelopmentID     devId;
///   END_FEDMSG
/// </code>
/// Sent to the side whose development finished (<c>FedSrv.CPP:11262-11266</c>,
/// <c>DevelopmentCompleted</c>) after the effect techs were applied and the
/// <c>SIDE_TECH_CHANGE</c> broadcast went out; the recipient client plays the
/// completion sound / UI cue for the <c>devId</c>. <c>DevelopmentID</c> is a
/// <c>short</c> (<c>typedef ObjectID DevelopmentID</c>, <c>src/Igc/igc.h:486,477</c>).
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 8
///   offset 4: short  devId       (DevelopmentID = ObjectID = short)
///   = 6 B fixed, no var items → cbmsg = 6
/// </code>
public sealed class DevCompleted : IFedMessage
{
    public const ushort Fmid = 8;
    public const int FixedSize = 6;   // header + devId short @4

    /// <summary>The completed development's id (<c>DevelopmentID</c>).</summary>
    public short DevId;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);          // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(DevId);      // offset 4: devId (DevelopmentID short)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"DEV_COMPLETED internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static DevCompleted Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"DEV_COMPLETED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected DEV_COMPLETED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);           // header
        return new DevCompleted { DevId = r.ReadI16() };
    }
}
