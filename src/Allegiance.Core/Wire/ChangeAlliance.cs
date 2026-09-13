namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_CHANGE_ALLIANCE</c> (id 196, <c>Messages.h:1017-1020</c>, quoted in
/// full):
/// <code>
///   DEFINE_FEDMSG(C, CHANGE_ALLIANCE, 196) // sent by game owner when changing alliances
///     SideID    sideID;
///     SideID    sideAlly; // side to ally to or NA to clear all alliances
///   END_FEDMSG
/// </code>
/// Client-sendable: the mission owner asks the server to change the
/// <c>sideID</c> side's alliance group to include <c>sideAlly</c>, or (when
/// <c>sideAlly == NA</c>) to clear <c>sideID</c>'s alliances entirely
/// (<c>FedSrv.CPP:7469-7481 FM_C_CHANGE_ALLIANCE</c>). Both <c>SideID</c>s are
/// <c>short</c>s (igc.h typedefs). Natural alignment, no padding:
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 196
///   offset 4: short  sideID   (SideID)
///   offset 6: short  sideAlly (SideID)
///   = 8 B fixed
/// </code>
/// The server does not reply with this message; it reacts by broadcasting the
/// <c>S</c>-directional <see cref="ChangeAlliances"/>(197) with the resulting
/// alliance array (<c>fsmission.cpp:5797-5807</c>).
/// </summary>
public sealed class ChangeAlliance : IFedMessage
{
    public const ushort Fmid = 196;
    public const int FixedSize = 8;  // header + sideID@4 + sideAlly@6

    /// <summary>The side whose alliance is being changed (<c>SideID</c> =
    /// short). May be <c>NA</c> (-1), in which case the server only re-derives
    /// and re-broadcasts the existing normalized alliance state.</summary>
    public short SideId;

    /// <summary>The side to ally <see cref="SideId"/> to, or <c>NA</c> (-1) to
    /// clear <see cref="SideId"/>'s alliances (<c>SideID</c> = short).</summary>
    public short SideAlly;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SideId);         // offset 4
        w.WriteI16(SideAlly);       // offset 6
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"CHANGE_ALLIANCE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ChangeAlliance Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"CHANGE_ALLIANCE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected CHANGE_ALLIANCE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short sideId = r.ReadI16();      // offset 4
        short sideAlly = r.ReadI16();    // offset 6
        return new ChangeAlliance { SideId = sideId, SideAlly = sideAlly };
    }
}
