namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_INACTIVE_TURRET_UPDATE</c> (id 100) — the counterpart signal to
/// <see cref="ActiveTurretUpdate"/>: a turret gunner announces it has stopped
/// actively controlling/firing the turret (<c>Messages.h:678-679</c>, the
/// empty body). The client picks this message over <c>ACTIVE_TURRET_UPDATE</c>
/// when its ship's <c>StateM</c> no longer has <c>weaponsMaskIGC</c>
/// (<c>clintlib.cpp:1894-1899</c>); the server handler (FedSrv.CPP:4882-4895)
/// clears the "shooting" state bit on the gunner.
///
/// The struct has <b>no fields at all</b> — the message is exactly the 4-byte
/// header (<c>cbmsg</c>=4, <c>fmid</c>=100). This is why the codec carries no
/// payload.
/// </summary>
public sealed class InactiveTurretUpdate : IFedMessage
{
    public const ushort Fmid = 100;
    public const int FixedSize = 4;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                 // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU16At(0, (ushort)FixedSize);
        return buf;
    }

    public static InactiveTurretUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"INACTIVE_TURRET_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected INACTIVE_TURRET_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");
        return new InactiveTurretUpdate();
    }
}
