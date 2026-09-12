namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_FORCE_TEAM_READY</c> (id 47, <c>Messages.h:392-397</c>) — the side's
/// team leader forces their whole side ready (or un-forces it). Client-sendable
/// only by the target side's leader; the server forwards it whole-mission and
/// applies the persistent per-side force-ready flag, which bypasses the normal
/// per-player ready check. Native shape:
/// <code>
///   DEFINE_FEDMSG(CS, FORCE_TEAM_READY, 47) // force team to be ready
///   // Sent only by team leaders. Forwarded to everyone
///   // if fForceReady==true, causes TEAM_READY to also be sent
///     SideID    iSide;
///     bool      fForceReady;
///   END_FEDMSG
/// </code>
/// A <c>SideID</c> short plus a <c>bool</c> — the same
/// <c>{SideID, bool}</c> natural-alignment layout as the sibling
/// <see cref="TeamReady"/> codec:
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 47
///   offset 4: short  iSide        (SideID)
///   offset 6: bool   fForceReady  (1 byte)
///   offset 7: 1 pad byte          (struct aligned to 2)
///   = 8 B fixed
/// </code>
/// </summary>
public sealed class ForceTeamReady : IFedMessage
{
    public const ushort Fmid = 47;
    public const int FixedSize = 8;   // header + iSide@4 + bool@6 + 1 pad@7

    /// <summary>The side being forced ready (<c>SideID</c> = short).</summary>
    public short SideId;

    /// <summary>Whether to force the side ready (<c>true</c>) or restore the
    /// normal per-player check (<c>false</c>).</summary>
    public bool FForceReady;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SideId);                  // offset 4
        w.WriteByte((byte)(FForceReady ? 1 : 0)); // offset 6
        w.WriteByte(0);                      // offset 7: tail alignment padding
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"FORCE_TEAM_READY internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ForceTeamReady Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"FORCE_TEAM_READY needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected FORCE_TEAM_READY fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short sideId = r.ReadI16();      // offset 4
        bool fForceReady = r.ReadByte() != 0; // offset 6
        r.Advance(1);                    // offset 7: pad
        return new ForceTeamReady { SideId = sideId, FForceReady = fForceReady };
    }
}
