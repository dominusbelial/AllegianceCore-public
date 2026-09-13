namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_AUTODONATE</c> (id 112), the client's auto-donate request
/// (<c>src/Inc/Messages.h:738-741</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(C, AUTODONATE, 112)
///     ShipID      sidDonateTo;
///     Money       amount;
///   END_FEDMSG
/// </code>
/// Layout (default MSVC packing — AUTODONATE sits outside Messages.h's
/// <c>#pragma pack(1)</c> regions; <c>ShipID</c> = 16-bit <c>short</c>,
/// <c>Money</c> = <c>AGCMoney</c> = 32-bit <c>long</c> (AGCIDL.idl:246);
/// base <c>FEDMESSAGE</c> = two 16-bit words cbmsg+fmid):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 112
///   offset  4: short  sidDonateTo  (ShipID)
///   offset  6: 2 pad bytes (0)     — align Money to 4
///   offset  8: int32  amount       (Money)
///   = 12 B fixed → cbmsg = 12 = sizeof(FMD_C_AUTODONATE)
/// </code>
/// <para>The two-field single-<c>ShipID</c> layout leaves the struct at 10
/// field bytes; the compiler rounds <c>sizeof</c> up to the 4-byte alignment
/// of the <c>Money long</c> member, so <b>12</b> bytes go on the wire with two
/// zero pad bytes after <c>sidDonateTo</c> — the same trailing-alignment
/// consequence the port already documented for <see cref="SetMoney"/> (whose
/// single trailing <c>ShipID</c> also pads to 12).</para>
/// <para><b>Two-mode wire semantics</b> (P197; native dispatch at
/// <c>FedSrv.CPP:5516-5658</c>): <c>amount == -1</c> selects the
/// <b>leader-only toggle</b> mode — <c>sidDonateTo</c> names a same-side
/// subordinate whose donation the leader turns on (pointing at the leader) or
/// off; any other <c>amount</c> sets/clears the <em>sender's own</em>
/// donate target, optionally transferring an immediate one-time gift of
/// <c>amount</c> (clamped to what the sender actually has).</para>
/// </summary>
public sealed class AutoDonate : IFedMessage
{
    public const ushort Fmid = 112;

    /// <summary>Wire width of the whole message: header(4) + ShipID(2) + 2 pad
    /// bytes + Money(4) = 12 (native sizeof with default MSVC packing).</summary>
    public const int FixedSize = 12;

    /// <summary>The target ship to donate to (<c>ShipID</c> = short), or
    /// <c>NA</c> (-1) to clear the sender's own target.</summary>
    public short SidDonateTo;

    /// <summary>One-time gift amount (<c>Money</c>, native 32-bit), or <c>-1</c>
    /// to select the leader-only toggle mode.</summary>
    public int Amount;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SidDonateTo);    // offset 4
        w.WriteZeros(2);            // offset 6: pad to align Money
        w.WriteI32(Amount);         // offset 8
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"AUTODONATE(C) internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static AutoDonate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"AUTODONATE(C) needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected AUTODONATE(C) fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                               // header
        var msg = new AutoDonate { SidDonateTo = r.ReadI16() };
        r.Advance(2);                               // pad
        msg.Amount = r.ReadI32();
        return msg;
    }
}
