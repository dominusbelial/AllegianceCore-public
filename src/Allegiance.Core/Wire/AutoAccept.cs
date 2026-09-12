namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_AUTO_ACCEPT</c> (id 52, <c>Messages.h:413-416</c>) — the side's
/// team leader turns auto-accept on or off for their side. Client-sendable
/// only by the target side's leader; the server forwards it whole-mission, sets
/// <see cref="Side.AutoAccept"/>, and — when turning <b>on</b> — immediately
/// seats every player with a pending join request on that side
/// (<c>FedSrv.CPP:6908-6936</c>, <c>fsmission.cpp:4974-5011 SetAutoAccept</c>).
/// Native shape:
/// <code>
///   DEFINE_FEDMSG(CS, AUTO_ACCEPT, 52) // sent by team leader; forwarded to all
///     SideID  iSide;    // can be NA (mission-owner-level auto-accept-of-leaders)
///     bool    fAutoAccept;
///   END_FEDMSG
/// </code>
/// A <c>SideID</c> short plus a <c>bool</c> — the same
/// <c>{SideID, bool}</c> natural-alignment layout as the sibling
/// <see cref="TeamReady"/> codec:
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 52
///   offset 4: short  iSide        (SideID)
///   offset 6: bool   fAutoAccept  (1 byte)
///   offset 7: 1 pad byte          (struct aligned to 2)
///   = 8 B fixed
/// </code>
/// The <c>iSide == NA</c> mission-owner-level variant is out of scope in this
/// port (no mission-wide auto-accept-of-leaders concept) and is refused by the
/// server; the codec still models the <c>NA</c> side id as <c>-1</c> so the
/// value round-trips.
/// </summary>
public sealed class AutoAccept : IFedMessage
{
    public const ushort Fmid = 52;
    public const int FixedSize = 8;   // header + iSide@4 + bool@6 + 1 pad@7

    /// <summary>The side whose auto-accept is being toggled (<c>SideID</c> = short).
    /// A value of <c>-1</c> (<c>NA</c>) is the mission-owner-level variant,
    /// refused by this port's server.</summary>
    public short SideId;

    /// <summary>Whether to turn auto-accept on (<c>true</c>) or off (<c>false</c>).</summary>
    public bool FAutoAccept;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SideId);                  // offset 4
        w.WriteByte((byte)(FAutoAccept ? 1 : 0)); // offset 6
        w.WriteByte(0);                      // offset 7: tail alignment padding
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"AUTO_ACCEPT internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static AutoAccept Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"AUTO_ACCEPT needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected AUTO_ACCEPT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short sideId = r.ReadI16();      // offset 4
        bool fAutoAccept = r.ReadByte() != 0; // offset 6
        r.Advance(1);                    // offset 7: pad
        return new AutoAccept { SideId = sideId, FAutoAccept = fAutoAccept };
    }
}
