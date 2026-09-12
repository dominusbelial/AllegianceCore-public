namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_LOCK_LOBBY</c> (id 43, <c>Messages.h:372-374</c>) — lock or unlock
/// the mission lobby. Client-sendable only by the <b>mission owner</b> (the
/// owner side's leader); the server forwards it whole-mission and sets
/// <see cref="Mission.LockLobby"/>. Native shape:
/// <code>
///   DEFINE_FEDMSG(CS, LOCK_LOBBY, 43) // lock/unlock the lobby
///     bool  fLock;
///   END_FEDMSG
/// </code>
/// A single <c>bool</c>. The same 2-aligned tail as the sibling
/// single-field <see cref="FireExpendableRequest"/> codec:
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 43
///   offset 4: bool   fLock   (1 byte)
///   offset 5: 1 pad byte     (struct aligned to 2)
///   = 6 B fixed
/// </code>
/// </summary>
public sealed class LockLobby : IFedMessage
{
    public const ushort Fmid = 43;
    public const int FixedSize = 6;   // header + fLock bool@4 + 1 pad@5

    /// <summary>Whether to lock (<c>true</c>) or unlock (<c>false</c>) the lobby.</summary>
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
        if (w.Position != FixedSize) throw new InvalidOperationException($"LOCK_LOBBY internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static LockLobby Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"LOCK_LOBBY needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected LOCK_LOBBY fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        bool fLock = r.ReadByte() != 0;  // offset 4
        r.Advance(1);                    // offset 5: pad
        return new LockLobby { FLock = fLock };
    }
}
