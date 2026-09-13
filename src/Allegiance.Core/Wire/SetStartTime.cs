namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_SET_START_TIME</c> (id 14, <c>Messages.h:153-155</c>) — the server
/// announces the mission's (re)start time to the whole mission, the visible
/// countdown between "everyone ready" and the mission actually starting.
/// Native shape:
/// <code>
///   DEFINE_FEDMSG(S, SET_START_TIME, 14)
///     Time  timeStart;
///   END_FEDMSG
/// </code>
/// Layout (natural alignment — no pragma pack encloses this message in
/// <c>Messages.h</c>):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 14
///   offset 4: uint32 timeStart   (Time = ms accumulator, ztime.h:20-21)
///   = 8 bytes fixed
/// </code>
/// <c>timeStart</c> is the raw native millisecond <c>Time</c> value on the
/// wire (kept as <see cref="uint"/> here for fidelity); the server converts
/// its double-seconds sim clock to milliseconds when broadcasting and stores
/// the same double target for its own countdown-elapse decision.
/// </summary>
public sealed class SetStartTime : IFedMessage
{
    public const ushort Fmid = 14;
    public const int FixedSize = 8;   // header(4) + uint32 timeStart@4

    /// <summary>The mission's target start time as a raw millisecond
    /// <c>Time</c> value (uint32 on the wire, <c>Time = ms</c>).</summary>
    public uint TimeStart;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);               // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU32(TimeStart);       // @4
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SET_START_TIME internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static SetStartTime Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SET_START_TIME needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SET_START_TIME fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                // header
        uint ts = r.ReadU32();       // @4
        return new SetStartTime { TimeStart = ts };
    }
}
