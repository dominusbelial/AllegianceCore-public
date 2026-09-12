namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_FIRE_EXPENDABLE</c> (id 87, <c>Messages.h:618-623</c>) — the server's
/// ammo-sync broadcast announcing that a ship fired one of its dispensers, the
/// server half of the <c>FIRE_EXPENDABLE</c> pair (the client request is the
/// distinct <see cref="FireExpendableRequest"/> shape at id 82). Native shape:
/// <code>
///   DEFINE_FEDMSG(S, FIRE_EXPENDABLE, 87)
///     ShipID        launcherID;
///     EquipmentType equipmentType;
///   END_FEDMSG
/// </code>
/// Two <c>short</c>s: the firing ship and which dispenser type it fired. Built
/// by the server's <c>HandleFireExpendable</c> as the first of the four messages
/// in the chaff-deployment broadcast flow (followed by <see cref="CreateChaff"/>,
/// one <see cref="MissileSpoofed"/> per spoofed missile, and one
/// <see cref="EndSpoofing"/>), all sent to the firing ship's whole cluster
/// (<c>GetGroupSectorFlying(pcluster)</c>).
/// </summary>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 87
///   offset 4: short  launcherID     (ShipID)
///   offset 6: short  equipmentType  (EquipmentType)
///   = 8 B fixed (the same two-back-to-back-shorts shape as MineDestroyed)
/// </code>
public sealed class FireExpendable : IFedMessage
{
    public const ushort Fmid = 87;
    public const int FixedSize = 8;   // header + launcherID @4 + equipmentType @6

    /// <summary>The id of the ship that fired the dispenser (<c>ShipID</c> = <c>short</c>,
    /// <c>igc.h:480</c>).</summary>
    public short LauncherId;

    /// <summary>The <see cref="Allegiance.Core.EquipmentType"/> of the dispenser fired.</summary>
    public EquipmentType EquipmentType;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(LauncherId);          // offset 4: launcherID (ShipID short)
        w.WriteI16((short)EquipmentType); // offset 6: equipmentType (EquipmentType short)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"FIRE_EXPENDABLE(87) internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static FireExpendable Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"FIRE_EXPENDABLE(87) needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected FIRE_EXPENDABLE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                // header
        short launcherId = r.ReadI16();
        var equipmentType = (EquipmentType)r.ReadI16();
        return new FireExpendable { LauncherId = launcherId, EquipmentType = equipmentType };
    }
}
