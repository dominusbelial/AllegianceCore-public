namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_PAYDAY</c> (id 114), the server's per-side "you got paid" message
/// (<c>Messages.h:747</c>):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 114
///   offset  4: Money  dMoney
///   = 8 B fixed, no var items → cbmsg = 8
/// </code>
/// Emitted once per side per payday tick by the server's
/// <c>DoPayday(IsideIGC*)</c> (<c>fsmission.cpp:2294-2326</c>): <c>delta =
/// m_rgMoney[sideID] / playerCount</c>, broadcast only to that side's own
/// connected peers (the send is scoped to the side's peer group, so the
/// message carries no sideID/shipID — the payload is a single field).
/// <c>Money</c> is <c>AGCMoney</c> = <c>typedef long</c>
/// (<c>AGCIDL.idl:246</c>), which is a <b>32-bit</b> integer on the native
/// Windows toolchain — hence <see cref="DMoney"/> is an <c>int</c> (4 bytes)
/// on this wire, not a C# <c>long</c>. The send is duplicated through the
/// site's <c>PaydayEvent</c> (<c>igc.h:4864</c>), but the reference FedSrv
/// only exercises that seam for the mining/helium-offload path
/// (<c>shipIGC.cpp:1985</c>, <c>FedSrv.CPP:13647</c>); this codec and its
/// P51 wiring are the cadence path, faithful to the message the real WOPR
/// bot's <c>MessageReceiver_FMD_S_PAYDAY</c>
/// (<c>CommanderResearchAndExpand.cs:697</c>) awaits.
/// </summary>
public sealed class Payday : IFedMessage
{
    public const ushort Fmid = 114;
    public const int FixedSize = 8;

    /// <summary>The per-player payout, <c>Money delta</c> (native 32-bit).</summary>
    public int DMoney;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI32(DMoney);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"PAYDAY internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static Payday Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"PAYDAY needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected PAYDAY fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                               // header
        return new Payday { DMoney = r.ReadI32() };
    }
}
