namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_KILL_SHIP</c> (id 105), the server's "this ship died" message
/// (<c>Messages.h:696-703</c>):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 105
///   offset  4: short  shipID        (ShipID,  igc.h:477)
///   offset  6: short  typeCredit    (ObjectType, igc.h:115)
///   offset  8: short  idCredit      (ObjectID,  igc.h:477)
///   offset 10: short  sideidKiller  (SideID,    igc.h:483)  — NA(-1) when the killer has no side
///   offset 12: bool   bKillCredit   (1 byte)
///   offset 13: bool   bDeathCredit  (1 byte)
///   = 14 B fixed, no var items → cbmsg = 14
/// </code>
/// Emitted once per death by the server's KillShipEvent handler
/// (<c>FedSrv.CPP:11793</c>). <c>bDeathCredit</c> says the victim was truly
/// killed (vs ejected); <c>bKillCredit</c> says the killer deserves the kill.
/// Both are host-decided at emit time — this class only carries them.
/// </summary>
public sealed class KillShip : IFedMessage
{
    public const ushort Fmid = 105;
    public const int FixedSize = 14;

    public short ShipId;          // the victim
    public short TypeCredit;      // ObjectType of the credited killer (0 = OT_ship)
    public short IdCredit;        // ObjectID of the credited killer (NA = -1 for none)
    public short SideIdKiller;    // the killer's side, NA(-1) when it has none
    public bool BKillCredit;
    public bool BDeathCredit;

    /// <summary>Build the message the way FedSrv does for a victim killed by a
    /// <paramref name="killer"/> with no credited side and no eject-pod nuance —
    /// the sensible defaults for a headless sim (all bDeathCredit, kill credit
    /// left to the host).</summary>
    public static KillShip FromVictim(short victimId, IgcObjectType killerType, short killerId) =>
        new()
        {
            ShipId = victimId,
            TypeCredit = (short)killerType,
            IdCredit = killerId,
            SideIdKiller = -1,      // NA: our ships have no side association
            BKillCredit = false,
            BDeathCredit = true,
        };

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipId);
        w.WriteI16(TypeCredit);
        w.WriteI16(IdCredit);
        w.WriteI16(SideIdKiller);
        w.WriteByte(BKillCredit ? (byte)1 : (byte)0);
        w.WriteByte(BDeathCredit ? (byte)1 : (byte)0);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"KILL_SHIP internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static KillShip Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"KILL_SHIP needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected KILL_SHIP fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new KillShip
        {
            ShipId = r.ReadI16(),
            TypeCredit = r.ReadI16(),
            IdCredit = r.ReadI16(),
            SideIdKiller = r.ReadI16(),
            BKillCredit = r.ReadByte() != 0,
            BDeathCredit = r.ReadByte() != 0,
        };
    }
}
