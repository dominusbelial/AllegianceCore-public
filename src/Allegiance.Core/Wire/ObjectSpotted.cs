namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_OBJECT_SPOTTED</c> (id 6), the server's "an ally detected an
/// important object" message (<c>src/Inc/Messages.h:76-81</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, OBJECT_SPOTTED, 6)
///     ObjectType    otObject;
///     ObjectID      oidObject;
///     ObjectType    otSpotter;
///     ObjectID      oidSpotter;
///   END_FEDMSG
/// </code>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 6
///   offset 4: short  otObject    (ObjectType, igc.h:115)
///   offset 6: short  oidObject   (ObjectID,  igc.h:477)
///   offset 8: short  otSpotter   (ObjectType, igc.h:115)
///   offset 10: short oidSpotter  (ObjectID,  igc.h:477)
///   = 12 B fixed, no var items → cbmsg = 12
/// </code>
/// Both <c>ObjectType</c> members are the <em>native</em> <c>typedef short</c>
/// (<c>igc.h:115</c>) — the 4-byte <c>[v1_enum]</c> int32 is only the EXPORT
/// envelope's <c>objecttype</c> discriminator (<see cref="ExportProbe"/> /
/// <see cref="ExportShip"/>), unrelated to this struct's members, exactly as
/// <c>DataProbeExport</c> established for its own <c>otTarget</c>
/// (<c>DataProbeExport.cs:50-54</c>). <b>Wire only.</b> The real native
/// senders emit <c>OBJECT_SPOTTED</c> while traversing a side's vision
/// system (<c>FedSrv.CPP:11015</c>, ally branch; <c>FedSrv.CPP:11058</c>,
/// spotting-side branch), each assigning <c>otObject/oidObject</c> from the
/// model and <c>otSpotter/oidSpotter</c> from the spotting ship. That trigger
/// needs a scanning/detection system this port lacks — no trigger is wired
/// here, this slice only defines the message shape.
/// </summary>
public sealed class ObjectSpotted : IFedMessage
{
    public const ushort Fmid = 6;

    /// <summary>Wire width of the whole message: header(4) + four short IDs
    /// (2 each). No alignment gaps — all members are 2-byte shorts sitting
    /// back-to-back, no member wider than 2.</summary>
    public const int FixedSize = 12;

    public short OtObject;       // ObjectType of the spotted object
    public short OidObject;      // ObjectID of the spotted object
    public short OtSpotter;      // ObjectType of the spotting ship
    public short OidSpotter;     // ObjectID of the spotting ship

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(OtObject);
        w.WriteI16(OidObject);
        w.WriteI16(OtSpotter);
        w.WriteI16(OidSpotter);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"OBJECT_SPOTTED internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ObjectSpotted Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"OBJECT_SPOTTED needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected OBJECT_SPOTTED fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new ObjectSpotted
        {
            OtObject = r.ReadI16(),
            OidObject = r.ReadI16(),
            OtSpotter = r.ReadI16(),
            OidSpotter = r.ReadI16(),
        };
    }
}
