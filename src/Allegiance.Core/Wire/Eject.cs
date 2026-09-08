using Allegiance.Core.Collision;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_EJECT</c> (id 77, <c>Messages.h:572-578</c>) — the server's "a
/// player just ejected into a drifting Lifepod hull here" message, broadcast
/// to <b>everyone flying in the same sector</b>
/// (<c>GetGroupSectorFlying(pcluster)</c>, <c>FedSrv.CPP:12011-12021</c>),
/// i.e. a sector-scoped, cross-side broadcast — not same-side-gated. This is
/// the wire counterpart of the in-space eject (the <c>!bKilled</c> branch of
/// <c>KillShipEvent</c>), where the dying player's ship survives, weakened,
/// as a drifting Lifepod hull still in space.
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 77
///   offset  4: short  shipID    (ShipID = ObjectID = short, igc.h:477,480)
///   offset  6: 2 pad bytes      (align the first Vector's floats to 4)
///   offset  8: Vector position  (12 bytes, three LE floats)
///   offset 20: Vector velocity  (12 bytes)
///   offset 32: Vector forward   (12 bytes)
///   offset 44: byte   cookie    (Cookie = BYTE, igc.h:516)
///   offset 45: 3 pad bytes      (round the struct to the largest-member
///                                alignment 4 → sizeof = 48, the MSVC model
///                                already documented in JoinedMission.cs:29-44)
///   = 48 bytes total
/// </code>
/// </summary>
public sealed class Eject : IFedMessage
{
    public const ushort Fmid = 77;
    public const int FixedSize = 48;   // 44 B of fields + 3 trailing pad (4-aligned Vector)

    // Named absolute wire offsets (message-relative).
    private const int ShipIdOffset = 4;
    private const int PositionOffset = 8;
    private const int VelocityOffset = 20;
    private const int ForwardOffset = 32;
    private const int CookieOffset = 44;

    public short ShipId;      // the ejecting (surviving) player's ship (Messages.h:573)
    public Vector3 Position;  // the lifepod's resting spot, outside the danger (Messages.h:574)
    public Vector3 Velocity;  // the lifepod's outward velocity (Messages.h:575)
    public Vector3 Forward;   // the lifepod's facing (Messages.h:576)
    public byte Cookie;       // used only by the receiving client (Messages.h:577) — NewCookie()

    /// <summary>Build the EJECT broadcast for a survivor's lifepod, with its
    /// <c>cookie</c> left zero (a direct-connect bot has no pending cookie the
    /// server must validate; the native calls <c>NewCookie()</c> here,
    /// FedSrv.CPP:12019, whose only consumer is the receiving client).</summary>
    public static Eject ForSurvivor(short shipId, Vector3 position, Vector3 velocity, Vector3 forward) =>
        new() { ShipId = shipId, Position = position, Velocity = velocity, Forward = forward, Cookie = 0 };

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipId);
        w.Advance(PositionOffset - w.Position);  // 2 pad bytes to the float-aligned Vector
        WriteVec(ref w, Position);
        WriteVec(ref w, Velocity);
        WriteVec(ref w, Forward);
        w.WriteByte(Cookie);
        w.WriteZeros(FixedSize - w.Position);   // 3 trailing pad bytes to the 4-aligned 48
        if (w.Position != FixedSize) throw new InvalidOperationException($"EJECT internal layout drift: {w.Position} != {FixedSize}");
        w.WriteU16At(0, (ushort)FixedSize);
        return buf;
    }

    public static Eject Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length != FixedSize)
            throw new ArgumentException($"EJECT needs exactly {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected EJECT fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != FixedSize) throw new ArgumentException($"cbmsg={hdr.CbMsg} != fixed {FixedSize}");

        WireReader r = new(buf);
        r.Advance(4);                       // header
        short shipId = r.ReadI16();
        r.Advance(2);                       // pad before the Vector
        var pos = ReadVec(ref r);
        var vel = ReadVec(ref r);
        var fwd = ReadVec(ref r);
        byte cookie = r.ReadByte();
        return new Eject
        {
            ShipId = shipId,
            Position = pos,
            Velocity = vel,
            Forward = fwd,
            Cookie = cookie,
        };
    }

    // The established 12-byte three-float LE encode/decode shared throughout
    // this project's wire codecs (same technique MissileLaunchData.cs:80-90 /
    // ProjectileInstance.cs:127-137 use) — reused, not reinvented.
    private static void WriteVec(ref WireWriter w, Vector3 v)
    {
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.X));
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.Y));
        w.WriteU32(BitConverter.SingleToUInt32Bits(v.Z));
    }

    private static Vector3 ReadVec(ref WireReader r) =>
        new(BitConverter.UInt32BitsToSingle(r.ReadU32()),
            BitConverter.UInt32BitsToSingle(r.ReadU32()),
            BitConverter.UInt32BitsToSingle(r.ReadU32()));
}
