namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_C_SUICIDE</c> (id 80, <c>Messages.h:590-591</c>) — the client's
/// "I want to die" request. Deliberately empty: the struct is header-only
/// (<c>FMD_C_SUICIDE</c> = <c>{ ushort cbmsg; ushort fmid; }</c>), so there are
/// no fields to encode or decode and <c>cbmsg = sizeof(FMD_C_SUICIDE) = 4</c>.
/// The server's handling is <c>FedSrv.CPP:6335-6350</c>: a suicide re-triggers
/// the exact same kill path a combat death uses (a direct
/// <c>GetSite()-&gt;KillShipEvent(...)</c>, launcher NULL), gated on the mission
/// being <c>STAGE_STARTED</c> and the ship having a cluster — see the server's
/// <c>HandleSuicide</c> for the relaxed-cheat-gate note.
/// </summary>
public sealed class Suicide : IFedMessage
{
    public const ushort Fmid = 80;
    public const int FixedSize = 4;   // header only — no fields (Messages.h:590-591)

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SUICIDE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static Suicide Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SUICIDE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SUICIDE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");
        return new Suicide();
    }
}
