namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_SIDE_INACTIVE</c> (id 68, <c>Messages.h:532-536</c>, quoted in
/// full) — the pre-game roster-shrinking toggle. The <b>mission owner</b>
/// marks an *empty* side inactive/active before the mission starts, to play a
/// smaller-than-map-default team count. Native shape:
/// <code>
///   DEFINE_FEDMSG(CS, SIDE_INACTIVE, 68) // KGJV #62 both way now
///     SideID    sideID;
///     bool      bActive;    // KGJV #62 - extended for empty teams feature
///     bool      bChangeAET; // KGJV #62 - extended for empty teams feature
///     bool      bAET;       // KGJV #62 - extended for empty teams feature
///   END_FEDMSG
/// </code>
/// This message sits OUTSIDE the two <c>#pragma pack(push, 1)</c> regions in
/// <c>Messages.h</c> (those are at lines 636-642 and 917-925), so it takes
/// the default MSVC alignment of its members — the same natural-alignment
/// family as the sibling <see cref="LockSides"/>/<see cref="LockLobby"/>
/// codecs (P186). <c>SideID</c> is a typedef of <c>ObjectID</c>, a
/// <c>short</c> (<c>igc.h:491</c>):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 68
///   offset 4: short  sideID     (sideID)
///   offset 6: bool   bActive    (1 byte)
///   offset 7: bool   bChangeAET (1 byte)
///   offset 8: bool   bAET       (1 byte)
///   offset 9: 1 pad byte        (struct aligned to 2)
///   = 10 B fixed
/// </code>
/// Semantics (P202): the <c>CS</c> client request carries only
/// <c>{sideID, bActive}</c> — <c>bChangeAET</c>/<c>bAET</c> are set by the
/// <b>server's</b> reply (<c>FedSrv.CPP:7387-7414</c>), ignored on the way in.
/// If the named side currently has no seated players the server sets its
/// active flag to the requested value; otherwise it <b>silently reverts</b>
/// <c>bActive</c> to the side's real current value. It then recomputes
/// whether the mission-wide <c>AllowEmptyTeams</c> (AET) parameter must flip
/// (AET on iff at least one side is inactive), fills <c>bChangeAET</c>/
/// <c>bAET</c> when it did, and forwards the (possibly-corrected) message
/// whole-mission.
/// </summary>
public sealed class SideInactive : IFedMessage
{
    public const ushort Fmid = 68;
    public const int FixedSize = 10;   // header + sideID@4 + 3 bools@6..8 + 1 pad@9

    /// <summary>The side being toggled (<c>sideID</c>).</summary>
    public short SideId;

    /// <summary>Whether the side is to be active (<c>true</c>) or inactive
    /// (<c>false</c>) — <c>bActive</c>. On a reverted request this is
    /// corrected to the side's real current value by the server.</summary>
    public bool BActive;

    /// <summary>Whether the server flipped the mission-wide AllowEmptyTeams
    /// parameter (<c>bChangeAET</c>) — meaningful only in the server's
    /// reply; ignored on the client's request.</summary>
    public bool BChangeAet;

    /// <summary>The resulting mission-wide AllowEmptyTeams value
    /// (<c>bAET</c>) — meaningful only in the server's reply.</summary>
    public bool BAet;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SideId);                          // offset 4: sideID
        w.WriteByte((byte)(BActive ? 1 : 0));        // offset 6: bActive
        w.WriteByte((byte)(BChangeAet ? 1 : 0));     // offset 7: bChangeAET
        w.WriteByte((byte)(BAet ? 1 : 0));           // offset 8: bAET
        w.WriteByte(0);                              // offset 9: tail alignment padding
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SIDE_INACTIVE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static SideInactive Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SIDE_INACTIVE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SIDE_INACTIVE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                       // header
        short sideId = r.ReadI16();         // offset 4
        bool bActive = r.ReadByte() != 0;   // offset 6
        bool bChangeAet = r.ReadByte() != 0; // offset 7
        bool bAet = r.ReadByte() != 0;      // offset 8
        r.Advance(1);                       // offset 9: pad
        return new SideInactive { SideId = sideId, BActive = bActive, BChangeAet = bChangeAet, BAet = bAet };
    }
}
