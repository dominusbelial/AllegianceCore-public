using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_PROJECTILE_INSTANCE</c> (id 12), the real game's wire message for a
/// newly-created projectile (<c>Messages.h:131-135</c>):
/// <code>
///   DEFINE_FEDMSG(CS, PROJECTILE_INSTANCE, 12)
///     Time              timeCreate;
///     DataProjectileIGC data;
///     Vector            position;
///   END_FEDMSG
/// </code>
/// <c>Time</c> → <c>uint</c> (this project's established convention for every
/// codec's time slot, cf. <c>ServerShipUpdate.Time</c> / <c>ShipUpdate</c>),
/// carrying the server tick index at creation. <c>Vector</c> → the 12-byte
/// three-float LE encode/decode shared throughout (<c>WriteVec</c>/<c>ReadVec</c>).
/// <c>DataProjectileIGC</c> inlines as <c>short projectileTypeID</c> +
/// <c>Vector forward</c> + <c>Vector velocity</c> + <c>float lifespan</c>.
/// <para/>
/// This is a "CS" (shared id, both directions in the real protocol) message,
/// but <b>this task implements the server→client broadcast direction only</b>:
/// weapon fire resolves entirely server-side from <c>ControlData</c>/<c>StateM</c>
/// (<c>Sim/Weapon.cs</c> <c>FireWeapon</c>, the same component that builds this
/// message), so there is no client→server direction to implement here.
/// </summary>
/// <remarks>
/// <b>Alignment assumption (stated plainly).</b> The C++ struct
/// <c>DataProjectileIGC</c> is not declared anywhere in this checkout
/// (verified: <c>grep -rn "DataProjectileIGC" src/</c> finds only usage sites
/// in <c>src/Igc/weaponIGC.cpp</c> and <c>src/Igc/projectileIGC.cpp</c>), and
/// unlike every prior ported struct (<c>ShipLoadout</c>, <c>ExpandedPartData</c>,
/// <c>PartData</c>) there is no header, no <c>#pragma pack(1)</c> confirmation,
/// and no corpus (.igc files never contain a live projectile — this is an
/// ephemeral runtime message). Whether the leading <c>short projectileTypeID</c>
/// is followed by compiler-inserted alignment padding before the first
/// <c>Vector</c> is <b>unprovable with this project's resources</b>, so this
/// codec picks the natural-MSVC-alignment default (padded: 2 bytes inserted at
/// offset 10 so <c>forward</c> lands 4-byte-aligned at offset 12), matching how
/// MSVC lays out a struct of <c>short</c> then 4-byte-aligned members when no
/// packing pragma is in effect. Choice and rationale are recorded in
/// PROJECTILE_BROADCAST_P88.md; the actual correctness bar this task enforces
/// is the server-encodes / client-decodes round-trip and the real observed
/// broadcast behaviour, both tested live.
/// </remarks>
public sealed class ProjectileInstance : IFedMessage
{
    public const ushort Fmid = 12;

    /// <summary>Wire width of the inlined <c>DataProjectileIGC</c> (32 B: 2 + 2 pad + 12 + 12 + 4) under the natural-MSVC-alignment assumption.</summary>
    public const int DataProjectileSize = 32;

    /// <summary>Whole-message width: header(4) + timeCreate(4) + DataProjectileIGC(32) + position(12) + launcherId(2).</summary>
    public const int FixedSize = 4 + 4 + DataProjectileSize + 12 + 2;

    public uint TimeCreate;
    public short ProjectileTypeId;
    public Vector3 Forward;
    public Vector3 Velocity;
    public float Lifespan;
    public Vector3 Position;

    /// <summary>P542 — the SHIP id that launched the round (the shooter's
    /// <c>Ship.Id</c>, stamped from <c>Projectile.Launcher</c> server-side). Added
    /// so the client can de-duplicate its own-shot echo per-shooter: an incoming
    /// <c>PROJECTILE_INSTANCE</c> whose <see cref="LauncherId"/> is NOT our own
    /// ship is a foreign (teammate/enemy) round and is never dropped as our echo.
    /// Native achieves this by de-duplicating only the projectile the CLIENT'S OWN
    /// IGC sim created at the trigger (matched by creation signature); this port
    /// resolves fire server-side and simulates no client projectile, so the
    /// launcher id is carried on the wire as the faithful "per own shot" key.
    /// <c>-1</c> (never sent for ship-launched rounds — see
    /// <see cref="FromProjectile"/>) denotes an unknown/absent launcher.</summary>
    public short LauncherId;

    /// <summary>Build the message from a live <see cref="Projectile"/> (the
    /// model <c>Weapon.FireWeapon</c> just created) at the given uint creation
    /// time (the server tick index, this project's uint-Time convention). The
    /// decoded projectile type's <c>ProjectileTypeId</c> is what the native
    /// <c>Export</c>/construction sites read/write for the id (projectileIGC.cpp:152);
    /// <c>position</c> is the muzzle spawn point; <c>velocity</c> is the
    /// inherited launch velocity; <c>forward</c> is the normalised launch
    /// heading; <c>lifespan</c> is the decoded type lifespan (creation-time-only,
    /// never re-exported — projectileIGC.cpp:156 does not write it back);
    /// <c>launcherId</c> is the launching <see cref="Ship"/>'s id (<c>-1</c> when
    /// the launcher is not a ship).</summary>
    public static ProjectileInstance FromProjectile(Projectile p, uint timeCreate) => new()
    {
        TimeCreate = timeCreate,
        ProjectileTypeId = p.ProjectileType.ProjectileTypeId,
        Forward = p.Forward,
        Velocity = p.Velocity,
        Lifespan = p.ProjectileType.Lifespan,
        Position = p.Position,
        LauncherId = p.Launcher is Ship sh ? sh.Id : (short)-1,
    };

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU32(TimeCreate);
        w.WriteI16(ProjectileTypeId);
        w.WriteZeros(2);            // 2 B alignment padding (assumption, see remarks)
        WriteVec(ref w, Forward);
        WriteVec(ref w, Velocity);
        w.WriteU32(BitConverter.SingleToUInt32Bits(Lifespan));   // raw 4-byte float (wire byte-for-byte)
        WriteVec(ref w, Position);
        w.WriteI16(LauncherId);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"PROJECTILE_INSTANCE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ProjectileInstance Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"PROJECTILE_INSTANCE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected PROJECTILE_INSTANCE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                                   // header
        var m = new ProjectileInstance
        {
            TimeCreate = r.ReadU32(),
            ProjectileTypeId = r.ReadI16(),
        };
        r.Advance(2);                                   // alignment padding (assumption)
        m.Forward = ReadVec(ref r);
        m.Velocity = ReadVec(ref r);
        m.Lifespan = BitConverter.UInt32BitsToSingle(r.ReadU32());
        m.Position = ReadVec(ref r);
        m.LauncherId = r.ReadI16();
        return m;
    }

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
