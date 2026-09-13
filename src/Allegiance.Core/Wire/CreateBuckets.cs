namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_CREATE_BUCKETS</c> (id 191), the server's \"here are the team's
/// development/initial tech-tree masks\" bucket-catalog preface message
/// (<c>src/Inc/Messages.h:983-986</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, CREATE_BUCKETS, 191)
///     TechTreeBitMask ttbmDevelopments;
///     TechTreeBitMask ttbmInitial;
///   END_FEDMSG
/// </code>
/// Layout: <c>TechTreeBitMask</c> = <c>TLargeBitMask&lt;c_ttbMax&gt;</c>
/// (igc.h:768) with <c>c_ttbMax = 400</c> (igc.h:578) and member
/// <c>uint8_t m_bits[((nBits - 1) / 8) + 1]</c> (zlib/mask.h:351) →
/// <c>[((400-1)/8)+1] = [50]</c> bytes — a raw 50-byte bitmap with no interior
/// padding (all uint8_t):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 191
///   offset  4: 50 B ttbmDevelopments (u8[50])
///   offset 54: 50 B ttbmInitial       (u8[50])
///   = 104 B fixed → cbmsg = 104 = sizeof(FMD_S_CREATE_BUCKETS)
/// </code>
/// </para>
/// <para><b>Native construction / port mapping</b> (P119 read directly; P189
/// wired live): the reference server builds this in
/// <c>CFSMission::SendMissionInfo</c> during join sync, filling the two
/// masks from <c>pside-&gt;GetDevelopmentTechs()</c> and
/// <c>pside-&gt;GetInitialTechs()</c> (fsmission.cpp:4255-4258), then sends
/// one <c>BUCKET_STATUS</c> per bucket (:4260-4273). From P189 on, the
/// port has live development-tech state on <see cref="Side.DevelopmentTechs"/>
/// (the tech-tree slice the <see cref="TechTreeBitMask"/> value type backs),
/// so the join sync fills both masks from it — the developments mask from
/// <c>GetDevelopmentTechs()</c> and the initial mask from the native
/// <c>UpdateInitialTechs()</c> equivalence (<c>sideigc.h:219-222</c>, initial
/// == development techs). Prior to P189 (P117 finding) the masks rode
/// all-zero; that is superseded.</para>
/// </summary>
public sealed class CreateBuckets : IFedMessage
{
    public const ushort Fmid = 191;

    /// <summary>Wire width of <c>TechTreeBitMask</c>: <c>[(c_ttbMax-1)/8]+1</c>
    /// with <c>c_ttbMax = 400</c> = 50 bytes (zlib/mask.h:351).</summary>
    public const int MaskBytes = 50;

    /// <summary>Whole-message wire width: header(4) + 50 + 50 = 104.</summary>
    public const int FixedSize = 4 + 2 * MaskBytes;

    /// <summary>The side's development tech mask (<c>u8[50]</c>). This port has
    /// no tech-tree system (P117 finding), so it is transmitted all-zero; the
    /// reference fills it from <c>GetDevelopmentTechs()</c> (fsmission.cpp:4257).</summary>
    public byte[] TtbmDevelopments { get; set; } = new byte[MaskBytes];

    /// <summary>The side's initial tech mask (<c>u8[50]</c>), likewise
    /// all-zero in this port (reference: <c>GetInitialTechs()</c>,
    /// fsmission.cpp:4258).</summary>
    public byte[] TtbmInitial { get; set; } = new byte[MaskBytes];

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        if (TtbmDevelopments.Length != MaskBytes || TtbmInitial.Length != MaskBytes)
            throw new ArgumentException($"CREATE_BUCKETS masks must each be {MaskBytes} bytes");
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteBytes(TtbmDevelopments);   // offset 4, 50 B
        w.WriteBytes(TtbmInitial);        // offset 54, 50 B
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"CREATE_BUCKETS internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static CreateBuckets Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"CREATE_BUCKETS needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected CREATE_BUCKETS fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                               // header
        var msg = new CreateBuckets
        {
            TtbmDevelopments = r.ReadBytes(MaskBytes).ToArray(),
            TtbmInitial = r.ReadBytes(MaskBytes).ToArray(),
        };
        return msg;
    }
}
