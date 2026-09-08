namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_LEAVE_SHIP</c> (id 94, <c>Messages.h:656-658</c>:
/// <c>DEFINE_FEDMSG(S, LEAVE_SHIP, 94) ShipID sidChild; END_FEDMSG</c>) — the
/// server's broadcast that a ship has detached from its parent (left a turret /
/// observer seat on another player's ship). Constructed and sent <b>inside</b>
/// the boarding <c>LeaveShip()</c> function (<c>FedSrv.CPP:2312-2316</c>) — the
/// very function the <c>sidParent == NA</c> branch of the <c>FM_C_BOARD_SHIP</c>
/// handler calls (<c>FedSrv.CPP:6398</c>) — so this is the boarding-related
/// trigger. Broadcast to the <b>whole side</b> (<c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c>,
/// <c>FedSrv.CPP:2316</c>), not to a single peer. <c>sidChild</c> names the
/// leaving (detaching) ship's id.
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 94
///   offset 4: short  sidChild  (ShipID = short)
///   = 6 B fixed, no var items → cbmsg = 6
/// </code>
public sealed class LeaveShip : IFedMessage
{
    public const ushort Fmid = 94;
    public const int FixedSize = 6;   // header + sidChild short — no padding (short is 2-aligned)

    /// <summary>The id of the ship that detached from its parent (is leaving).</summary>
    public short SidChild;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SidChild);       // sidChild (ShipID short)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"LEAVE_SHIP internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static LeaveShip Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"LEAVE_SHIP needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected LEAVE_SHIP fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);               // header
        return new LeaveShip { SidChild = r.ReadI16() };
    }
}
