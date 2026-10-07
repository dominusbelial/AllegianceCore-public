namespace Allegiance.Core.Wire;

/// <summary>
/// P574 — a port-internal wire notice (custom FM id 853; no native
/// <c>DEFINE_FEDMSG</c> counterpart) carrying a mine's <b>current strength
/// fraction</b> after it was worn by a ship hit. Native communicates this
/// client-side through <c>GetThingSite()->SetMineStrength(m_fraction)</c> on
/// every hit (<c>mineigc.h:142,180</c>), which thins the client's
/// <c>MineFieldGeo</c> decal cloud to <c>1 + int(fraction·radius²/100)</c>
/// (<c>efgeo.cpp:21-24,43-51</c>) — the visible \"the field wears down\" half
/// of a minefield popping. This port's mine wire had no strength byte (see
/// <c>Mine.ReduceStrength</c>'s remarks), so the server emits this message
/// alongside the per-hit <c>EXPLOSION_EVENT(207)</c> pop, and the client
/// renderer thins the decals to match.
/// </summary>
/// <remarks>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 853
///   offset 4: short  clusterID   (SectorID, igc.h:487)
///   offset 6: short  mineID      (MineID,   igc.h:495)
///   offset 8: float  strength    (0..1 fraction)
///   = 12 B fixed, no var items → cbmsg = 12
/// </code>
/// Emitted by <c>Server.BroadcastMineHit</c> to the same same-sector audience
/// that receives the pop. Unknown to the native protocol (a pure port
/// extension), so it is filtered to this project's own client and server only.
/// </remarks>
public sealed class MineStrength : IFedMessage
{
    public const ushort Fmid = 853;

    public const int FixedSize = 12;

    public short ClusterId;    // the sector the mine is in
    public short MineId;       // the worn mine
    public float Strength;     // m_fraction after the hit, in [0,1]

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ClusterId);
        w.WriteI16(MineId);
        w.WriteU32(BitConverter.SingleToUInt32Bits(Strength));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"MINE_STRENGTH internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static MineStrength Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"MINE_STRENGTH needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected MINE_STRENGTH fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new MineStrength
        {
            ClusterId = r.ReadI16(),
            MineId = r.ReadI16(),
            Strength = BitConverter.UInt32BitsToSingle(r.ReadU32()),
        };
    }
}
