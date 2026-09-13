namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_SIDE_TECH_CHANGE</c> (id 75, <c>src/Inc/Messages.h:560-564</c>,
/// quoted in full):
/// <code>
///   DEFINE_FEDMSG(S, SIDE_TECH_CHANGE, 75)
///     SideID          sideID;
///     TechTreeBitMask ttbmDevelopments;
///   END_FEDMSG
/// </code>
/// Sent to the changing side's own peer group whenever a development adds
/// previously-unheld effect techs to the side (<c>ApplyDevelopmentTechs</c>,
/// <c>src/Igc/sideigc.h:194-210</c> → <c>SideDevelopmentTechChange</c>,
/// <c>FedSrv.CPP:11282</c>), so the side's clients update their tech tree.
/// Crucially, the broadcast is <b>gated on the mission stage</b>: native only
/// sends it while <c>GetStage() == STAGE_STARTED</c> (<c>FedSrv.CPP:11283-11288</c>)
/// — tech changes during the lobby are silent.
/// </summary>
/// <remarks>
/// <para><b>Wire layout.</b> <c>SideID</c> is a <c>short</c>
/// (<c>typedef ObjectID SideID</c>, <c>src/Igc/igc.h:491,477</c>);
/// <c>TechTreeBitMask</c> is the 50-byte <c>uint8_t[((400-1)/8)+1]</c> bitmap
/// (<c>src/zlib/mask.h:351</c>, <c>c_ttbMax = 400</c>, <c>igc.h:578</c>) with no
/// interior padding:</para>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 75
///   offset 4: short  sideID         (SideID = ObjectID = short)
///   offset 6: 50 B   ttbmDevelopments (u8[50])
///   = 56 B fixed → cbmsg = 56 = sizeof(FMD_S_SIDE_TECH_CHANGE)
/// </code>
/// </remarks>
public sealed class SideTechChange : IFedMessage
{
    public const ushort Fmid = 75;

    /// <summary>Wire width of <c>TechTreeBitMask</c>: 50 bytes
    /// (<c>zlib/mask.h:351</c>).</summary>
    public const int MaskBytes = 50;

    /// <summary>Whole-message wire width: header(4) + sideID(2) + 50 = 56.</summary>
    public const int FixedSize = 4 + 2 + MaskBytes;

    /// <summary>The side whose development techs changed (<c>SideID</c>).</summary>
    public short SideId;

    /// <summary>The side's new development-tech bitmap (<c>u8[50]</c>) — the
    /// post-OR <c>GetDevelopmentTechs()</c> value (<c>FedSrv.CPP:11286</c>).</summary>
    public byte[] TtbmDevelopments { get; set; } = new byte[MaskBytes];

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        if (TtbmDevelopments.Length != MaskBytes)
            throw new ArgumentException($"SIDE_TECH_CHANGE mask must be {MaskBytes} bytes");
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SideId);         // offset 4: sideID (SideID short)
        w.WriteBytes(TtbmDevelopments);   // offset 6, 50 B
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SIDE_TECH_CHANGE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static SideTechChange Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SIDE_TECH_CHANGE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SIDE_TECH_CHANGE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);               // header
        var msg = new SideTechChange
        {
            SideId = r.ReadI16(),
            TtbmDevelopments = r.ReadBytes(MaskBytes).ToArray(),
        };
        return msg;
    }
}
