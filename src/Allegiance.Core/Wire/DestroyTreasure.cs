namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_DESTROY_TREASURE</c> (id 30), the server's \"this treasure was
/// destroyed / picked up\" message (<c>src/Inc/Messages.h:257-261</c>), quoted
/// in full:
/// <code>
///   DEFINE_FEDMSG(S, DESTROY_TREASURE, 30)
///     TreasureID        treasureID;
///     SectorID          sectorID;
///   END_FEDMSG
/// </code>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 30
///   offset 4: short  treasureID   (TreasureID = ObjectID, igc.h:490,477)
///   offset 6: short  sectorID     (SectorID = ObjectID, igc.h:487)
///   = 8 B fixed, no var items → cbmsg = 8
/// </code>
/// Simple fixed-size message, no var item — the same shape as
/// <see cref="MineDestroyed"/>. Emitted by the native
/// <c>FedSrvSiteBase::KillTreasureEvent</c> (<c>FedSrv.CPP:12493-12509</c>)
/// whenever a treasure is terminated — on pickup (<c>HitTreasureEvent</c>
/// returning true) or on expiry — broadcast <b>whole-mission</b> to
/// <c>GetGroupRealSides()</c> (all real sides, not just the acting side). This
/// port sends it on cash-pickup (P123) to every peer regardless of side, and
/// would equally use it for expiry when the spawn/expiry lifecycle is built.
/// </summary>
public sealed class DestroyTreasure : IFedMessage
{
    public const ushort Fmid = 30;

    /// <summary>Wire width of the whole message: header(4) + treasureID(2) + sectorID(2). No
    /// alignment gaps — both 2-byte IDs sit back-to-back, no member wider than 2.</summary>
    public const int FixedSize = 8;

    public short TreasureId;   // the destroyed/picked-up treasure
    public short SectorId;     // the sector it was in

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(TreasureId);
        w.WriteI16(SectorId);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"DESTROY_TREASURE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static DestroyTreasure Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"DESTROY_TREASURE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected DESTROY_TREASURE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        return new DestroyTreasure
        {
            TreasureId = r.ReadI16(),
            SectorId = r.ReadI16(),
        };
    }
}
