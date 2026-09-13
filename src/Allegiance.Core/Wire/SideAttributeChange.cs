namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_SIDE_ATTRIBUTE_CHANGE</c> (id 76, <c>src/Inc/Messages.h:566-570</c>,
/// quoted in full):
/// <code>
///   DEFINE_FEDMSG(S, SIDE_ATTRIBUTE_CHANGE, 76)
///     SideID              sideID;
///     GlobalAttributeSet  gasAttributes;
///   END_FEDMSG
/// </code>
/// Sent to the <b>whole mission</b> (not the side's own group) whenever a
/// completed development applies an attribute delta to a side
/// (<c>ApplyGlobalAttributeSet</c>, <c>src/Igc/sideigc.h:343-345</c> →
/// <c>SideGlobalAttributeChange</c>, <c>FedSrv.CPP:11313-11318</c>); it is
/// <b>unconditional</b> (no stage gate) and broadcast to
/// <c>GetGroupMission()</c>, unlike <see cref="SideTechChange"/>'s gated,
/// side-scoped audience.
/// </summary>
/// <remarks>
/// <para><b>Wire layout.</b> <c>SideID</c> is a <c>short</c>
/// (<c>typedef ObjectID SideID</c>, <c>src/Igc/igc.h:491,477</c>).
/// <c>GlobalAttributeSet</c> is <c>float m_attributes[25]</c>
/// (<c>c_gaMax = 25</c>, <c>src/Igc/igc.h:915,948</c>) — 100 bytes, 4-byte
/// aligned. So the short sideID @4 is followed by <b>2 pad bytes</b> to reach
/// the 4-aligned float array at offset 8:</para>
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 76
///   offset  4: short  sideID         (SideID = ObjectID = short)
///   offset  6: 2 B pad (align 4 for the float array)
///   offset  8: 25×float gasAttributes (100 B, little-endian)
///   = 108 B fixed → cbmsg = 108 = sizeof(FMD_S_SIDE_ATTRIBUTE_CHANGE)
/// </code>
/// </remarks>
public sealed class SideAttributeChange : IFedMessage
{
    public const ushort Fmid = 76;

    /// <summary>The GlobalAttributeSet element count (<c>c_gaMax = 25</c>,
    /// <c>igc.h:915</c>).</summary>
    public const int GaCount = 25;

    /// <summary>Whole-message wire width: header(4) + sideID(2) + pad(2) +
    /// 25 floats(100) = 108.</summary>
    public const int FixedSize = 4 + 2 + 2 + GaCount * 4;

    /// <summary>The side whose global attribute set changed (<c>SideID</c>).</summary>
    public short SideId;

    /// <summary>The side's 25 attribute multipliers (<c>float[25]</c>) — the
    /// post-<c>Apply</c> <c>GetGlobalAttributeSet()</c> value
    /// (<c>FedSrv.CPP:11316</c>).</summary>
    public float[] GasAttributes { get; set; } = new float[GaCount];

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        if (GasAttributes.Length != GaCount)
            throw new ArgumentException($"SIDE_ATTRIBUTE_CHANGE needs exactly {GaCount} floats");
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SideId);         // offset 4: sideID (SideID short)
        w.WriteZeros(2);            // offset 6: align-4 pad
        for (int i = 0; i < GaCount; i++)
            w.WriteI32(BitConverter.SingleToInt32Bits(GasAttributes[i]));   // offset 8+, 100 B
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SIDE_ATTRIBUTE_CHANGE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static SideAttributeChange Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SIDE_ATTRIBUTE_CHANGE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SIDE_ATTRIBUTE_CHANGE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);               // header
        var msg = new SideAttributeChange { SideId = r.ReadI16() };
        r.Advance(2);               // align-4 pad
        var gas = new float[GaCount];
        for (int i = 0; i < GaCount; i++)
            gas[i] = BitConverter.Int32BitsToSingle(r.ReadI32());
        msg.GasAttributes = gas;
        return msg;
    }
}
