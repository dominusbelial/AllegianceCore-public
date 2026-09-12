namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_LOCK_SIDES</c> (id 44, <c>Messages.h:376-378</c>) — lock or unlock
/// team changes (side switching). Client-sendable only by the <b>mission
/// owner</b>; the server forwards it whole-mission, sets
/// <see cref="Mission.LockSides"/>, and refuses subsequent side-switch
/// <c>POSITIONREQ</c>s with <see cref="DelPositionReqReason.SidesLocked"/>.
/// Native shape:
/// <code>
///   DEFINE_FEDMSG(CS, LOCK_SIDES, 44) // allow/disallow team changes
///     bool  fLock;
///   END_FEDMSG
/// </code>
/// A single <c>bool</c> — identical layout to the sibling
/// <see cref="LockLobby"/> codec:
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 44
///   offset 4: bool   fLock   (1 byte)
///   offset 5: 1 pad byte     (struct aligned to 2)
///   = 6 B fixed
/// </code>
/// </summary>
public sealed class LockSides : IFedMessage
{
    public const ushort Fmid = 44;
    public const int FixedSize = 6;   // header + fLock bool@4 + 1 pad@5

    /// <summary>Whether to lock (<c>true</c>) or unlock (<c>false</c>) side changes.</summary>
    public bool FLock;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteByte((byte)(FLock ? 1 : 0));  // offset 4: fLock
        w.WriteByte(0);                      // offset 5: tail alignment padding
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"LOCK_SIDES internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static LockSides Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"LOCK_SIDES needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected LOCK_SIDES fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        bool fLock = r.ReadByte() != 0;  // offset 4
        r.Advance(1);                    // offset 5: pad
        return new LockSides { FLock = fLock };
    }
}
