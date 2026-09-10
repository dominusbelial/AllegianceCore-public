namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_ACQUIRE_TREASURE</c> (id 162), the server's per-player \"you got
/// this treasure\" notice (<c>src/Inc/Messages.h:871-875</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, ACQUIRE_TREASURE, 162)
///     TreasureCode      treasureCode;
///     ObjectID          treasureID;
///     short             amount;
///   END_FEDMSG
/// </code>
/// Emitted by <c>FedSrvSiteBase::HitTreasureEvent</c> to the picking player's
/// <em>own connection only</em> (<c>FedSrv.CPP:13633-13641</c>,
/// <c>SendMessages(pfsPlayer-&gt;GetConnection(), ...)</c>) for every treasure
/// code (cash, part, powerup, …) after the underlying grant. It tells the
/// client what it acquired; the client may then acknowledge part-treasures
/// with <see cref="TreasureAck"/>.
/// </summary>
/// <remarks>
/// <para><b>Layout</b> (default MSVC packing — :871 sits between the two
/// <c>#pragma pack(1)</c> regions; member widths: <c>TreasureCode = char</c>
/// (igc.h:2086), <c>ObjectID = short</c> (igc.h:477), <c>amount</c> short):</para>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 162
///   offset 4: char   treasureCode   (1 byte)
///   offset 5: 1 pad byte            (align the following short to 2)
///   offset 6: short  treasureID     (ObjectID)
///   offset 8: short  amount
///   = 10 B occupied, rounds to multiple of 2 → cbmsg = 10
/// </code>
/// <para>The struct's largest member is the 2-aligned FEDMESSAGE base (the
/// char and shorts are ≤2), so it rounds to an even 10 — the interior pad at
/// offset 5 is real but there is no trailing pad. Built (codec complete) in
/// P123 but not yet emitted: the native sends it inside <c>HitTreasureEvent</c>
/// right before returning true, so wiring it is a one-line follow-up when the
/// pickup broadcast set is extended (see the P123 report).</para>
/// </remarks>
public sealed class AcquireTreasure : IFedMessage
{
    public const ushort Fmid = 162;

    /// <summary>Wire width: header(4) + code(1) + pad(1) + id(2) + amount(2) = 10.</summary>
    public const int FixedSize = 10;

    public byte TreasureCode;   // TreasureCode (char)
    public short TreasureId;    // ObjectID
    public short Amount;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteByte(TreasureCode);  // offset 4
        w.WriteByte(0);             // pad @5
        w.WriteI16(TreasureId);     // offset 6
        w.WriteI16(Amount);         // offset 8
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"ACQUIRE_TREASURE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static AcquireTreasure Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"ACQUIRE_TREASURE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected ACQUIRE_TREASURE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        var msg = new AcquireTreasure { TreasureCode = r.ReadByte() };
        r.Advance(1);                                   // pad @5
        msg.TreasureId = r.ReadI16();
        msg.Amount = r.ReadI16();
        return msg;
    }
}
