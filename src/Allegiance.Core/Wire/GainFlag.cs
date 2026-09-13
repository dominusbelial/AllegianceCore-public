namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_GAIN_FLAG</c> (id 156, <c>Messages.h:861-866</c>) — the server's
/// per-ship flag-capture notice, quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, GAIN_FLAG, 156)
///     SideID        sideidFlag;
///     ShipID        shipidRecipient;
///     bool          bIsTreasureDocked; // KGJV #118 - extended to notify docking of new tech (if true)
///     PartID        parttypeidDocked;  // KGJV #118 - which part was docked, only valid if bIsTreasureDocked is true
///   END_FEDMSG
/// </code>
/// This is the whole-mission announce of one flag-capture event: a ship picked
/// up an enemy side's flag (<c>sideidFlag</c> = the ORIGINAL side whose flag it
/// is, <c>shipidRecipient</c> = the now-carrying ship) or, with
/// <c>sideidFlag = NA</c>, a ship secured/carried a flag home by docking at its
/// own side's station (native FedSrv.CPP:13568-13572, 13150-13156).
/// <c>bIsTreasureDocked = true</c> is the separate tech-recovery variant
/// (KGJV #118) — a docking ship carrying new tech bits is announced via this
/// same wire shape; this task never sets it (see the P201 report).
/// </summary>
/// <remarks>
/// <para><b>Layout</b> — <c>SideID</c>/<c>ShipID</c>/<c>PartID</c> are all
/// <c>short</c> (<c>ObjectID</c>/<c>PartID</c> typedefs, <c>igc.h:477-480</c>)
/// and <c>bool</c> is one byte. The message sits in the default-alignment
/// region of Messages.h (the two <c>#pragma pack(push, 1)</c> blocks are at
/// <c>Messages.h:636-642</c> and <c>:917-925</c>, both far from this struct),
/// so C++ default alignment applies:
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 156
///   offset  4: short  sideidFlag       (SideID — the carried flag's ORIGINAL side, or NA on secure)
///   offset  6: short  shipidRecipient  (ShipID — the carrier)
///   offset  8: bool   bIsTreasureDocked  (1 byte)
///   offset  9: pad                       (align the trailing PartID short to offset 10)
///   offset 10: short  parttypeidDocked   (PartID — only valid if bIsTreasureDocked)
///   = 12 B fixed, no var items → cbmsg = 12
/// </code></para>
/// <para><b>Audience.</b> The pickup announce is whole-mission
/// (<c>SendMessages(GetGroupRealSides(), ...)</c>, FedSrv.CPP:13573); the
/// secure announce is likewise whole-mission (FedSrv.CPP:13157, confirmed this
/// task). <b>Wire/broadcast only</b> — the sends live in
/// <c>Allegiance.Server.Server</c>, the same split as <see cref="GameState"/>
/// (P191).</para>
/// </remarks>
public sealed class GainFlag : IFedMessage
{
    public const ushort Fmid = 156;

    /// <summary>Wire width: header(4) + sideidFlag(2) + shipidRecipient(2) +
    /// bIsTreasureDocked(1) + pad(1) + parttypeidDocked(2) = 12.</summary>
    public const int FixedSize = 12;

    /// <summary>Native <c>NA</c> sentinel (-1) — the <c>sideidFlag</c> value on
    /// the secure announce (a ship docking at its own side clears the carried
    /// flag, FedSrv.CPP:13152). Mirrors the other codecs' <c>NA = -1</c>
    /// convention (<c>PlayerInfo.cs</c>/<c>GameOver.cs</c>).</summary>
    public const short NA = -1;

    /// <summary>The side whose flag this is (the ORIGINAL side), or
    /// <see cref="NA"/> on a secure announce (Messages.h:862).</summary>
    public short SideidFlag;

    /// <summary>The ship carrying / having carried the flag (Messages.h:863).</summary>
    public short ShipidRecipient;

    /// <summary>True for the tech-recovery variant (KGJV #118); always false in
    /// this task's flag-capture sends (Messages.h:864).</summary>
    public bool BIsTreasureDocked;

    /// <summary>Valid only when <see cref="BIsTreasureDocked"/>; else 0
    /// (Messages.h:865).</summary>
    public short ParttypeidDocked;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                       // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(SideidFlag);              // offset 4
        w.WriteI16(ShipidRecipient);         // offset 6
        w.WriteByte(BIsTreasureDocked ? (byte)1 : (byte)0);   // offset 8
        w.WriteByte(0);                      // offset 9 pad (align trailing PartID short)
        w.WriteI16(ParttypeidDocked);        // offset 10
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"GAIN_FLAG internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static GainFlag Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"GAIN_FLAG needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected GAIN_FLAG fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                        // header
        var msg = new GainFlag
        {
            SideidFlag = r.ReadI16(),
            ShipidRecipient = r.ReadI16(),
            BIsTreasureDocked = r.ReadByte() != 0,
        };
        r.Advance(1);                        // pad @9
        msg.ParttypeidDocked = r.ReadI16();
        return msg;
    }
}
