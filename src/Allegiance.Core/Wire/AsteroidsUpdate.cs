using System.Buffers.Binary;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>
/// The 8-byte <c>CompactOrientation</c> carried inside each
/// <see cref="AsteroidStateEntry"/>. Raw quantized-quaternion components, laid
/// out exactly as native <c>CompactOrientation</c> (<c>igc.h:2137-2176</c>):
/// <c>unsigned short m_qa</c> + three <c>short</c>s
/// (<c>m_qx,m_qy,m_qz</c>). See the enclosing entry's doc for the
/// identity-orientation finding this task reached.
/// </summary>
public readonly record struct CompactOrientation(
    ushort Qa, short Qx, short Qy, short Qz)
{
    /// <summary>Wire width of one <c>CompactOrientation</c> = native
    /// <c>sizeof</c> under the enclosing struct's <c>pack(1)</c> = 8.</summary>
    public const int Size = 8;

    /// <summary>The identity quaternion (<c>a=1</c>, vector 0) encodes as
    /// qa=65535,qx=qy=qz=0, but native deliberately treats the <b>all-zero</b>
    /// form as "reset to identity" (<c>Export</c>'s all-zero branch,
    /// <c>igc.h:2155,2164-2165</c>), and the port's decoder honours that
    /// (<c>CompactOrientationCodec.Decode</c>, Quant.cs:142-143). We send the
    /// zero form.</summary>
    public static CompactOrientation Identity => new(0, 0, 0, 0);

    public void Write(ref WireWriter w)
    {
        w.WriteU16(Qa);   // offset 2 within the entry
        w.WriteI16(Qx);   // offset 4
        w.WriteI16(Qy);   // offset 6
        w.WriteI16(Qz);   // offset 8
    }

    public static CompactOrientation Read(ReadOnlySpan<byte> b) => new(
        Qa: BinaryPrimitives.ReadUInt16LittleEndian(b),
        Qx: BinaryPrimitives.ReadInt16LittleEndian(b[2..]),
        Qy: BinaryPrimitives.ReadInt16LittleEndian(b[4..]),
        Qz: BinaryPrimitives.ReadInt16LittleEndian(b[6..]));
}

/// <summary>
/// One <c>AsteroidState</c> wire record inside the <see cref="AsteroidsUpdate"/>
/// (179) message's <c>FM_VAR_ITEM(rgAsteroidStates)</c> array. The native
/// struct is defined at <c>Messages.h:917-925</c>:
/// <code>
///   #pragma pack(push, 1)
///   struct AsteroidState {
///     AsteroidID         asteroidID;    // short
///     CompactOrientation co;            // 8 bytes (igc.h:2137-2176)
///     short              ore;
///     BytePercentage     bpFraction;    // byte
///   };
///   #pragma pack(pop)
/// </code>
/// <para><b>Packing / stride (confirmed, the key layout finding).</b> The
/// struct sits inside <c>#pragma pack(push, 1)</c> (<c>Messages.h:917</c>), so
/// every member is 1-byte aligned and the total is the raw field-sum with no
/// tail rounding — <b>13 bytes</b>. Under default MSVC alignment the max
/// member alignment would be 2 (the shorts), which would round the total up to
/// 14; the pack(1) is what collapses it to 13, and the client
/// (<c>appmsg.cpp:607-612</c>) reads <c>n * sizeof(AsteroidState)</c> = 13 per
/// element. This is the same non-default-packing discipline P130/P133 noted for
/// this message family.</para>
/// <code>
///   offset 0:  short asteroidID
///   offset 2:  ushort co.qa
///   offset 4:  short  co.qx
///   offset 6:  short  co.qy
///   offset 8:  short  co.qz
///   offset 10: short  ore
///   offset 12: byte   bpFraction
///   = 13 bytes, no alignment gaps (pack(1))
/// </code>
/// <para><b>Field semantics (both confirmed against real native assignments in
/// the message's own construction).</b></para>
/// <list type="bullet">
/// <item><b><c>ore</c> — raw ore amount.</b> <c>pas-&gt;ore =
/// short(pasteroid-&gt;GetOre())</c> (<c>fsship.cpp:909</c>), where
/// <c>GetOre()</c> returns the rock's <em>current ore pool</em>
/// <c>m_asteroidDef.ore</c> (<c>asteroidigc.h:187-190</c>) — the same pool
/// mining drains (<c>MineOre</c>, asteroidigc.h:221-233). Port analogue:
/// <see cref="Asteroid.OreRemaining"/>.</item>
/// <item><b><c>bpFraction</c> — the HEALTH fraction, NOT the ore fraction.</b>
/// <c>pas-&gt;bpFraction = pasteroid-&gt;GetFraction()</c> (<c>fsship.cpp:911</c>),
/// and <c>CasteroidIGC::GetFraction()</c> returns <c>m_fraction</c> — the
/// rock's direct-hit <em>damage/health</em> pool
/// (<c>asteroidigc.h:172-174</c>, written by ReceiveDamage at :143/:152),
/// independent of the ore pool. The ore fraction is a <b>separate</b> native
/// accessor, <c>GetOreFraction()</c> (<c>igc.h:4162</c>), transmitted only by
/// <c>MineAsteroidEvent</c> / <c>ASTEROID_MINED</c>(198) — never by this
/// message. So the two fields are genuinely not redundant: <c>ore</c> is the
/// raw remaining ore, <c>bpFraction</c> is the rock's current damage fraction.
/// Port analogue: <see cref="Asteroid.Fraction"/>.</item>
/// <item><b><c>co</c> — identity (confirmed simplification).</b> Native sends
/// <c>pas-&gt;co.Set(pasteroid-&gt;GetOrientation())</c> (<c>fsship.cpp:910</c>).
/// This port's runtime <see cref="Asteroid"/> deliberately carries <b>no</b>
/// orientation/transform (P102 kept it minimal — it is not a <c>Model</c>, has
/// no live transform), and the record's <c>Data.Rotation</c> is a native
/// axis-angle <c>Rotation</c> (Utility.h:515, axis+angle) whose reconstruction
/// into the runtime quaternion would require re-building the model's full
/// transform — "inventing a rotation system for one field," exactly what this
/// arc declines to do. We therefore send the identity CompactOrientation
/// (all-zero; decodes to identity, igc.h:2155), the same identity-<c>co</c>
/// convention the ship-update payload already ships
/// (<c>ClientShipUpdate.cs:87</c>).</item>
/// </list>
/// <para><b>Fraction encoding.</b> <c>bpFraction</c> is a div-240
/// <c>BytePercentage</c> via <see cref="CompactQuant.Percent"/>, the exact
/// convention every other fraction slot in this arc uses (<c>StationsUpdate</c>,
/// <c>AsteroidMined</c>).</para>
/// </summary>
public readonly record struct AsteroidStateEntry(
    short AsteroidId, CompactOrientation Co, short Ore, byte BpFraction)
{
    /// <summary>Wire width of one record = native <c>sizeof(AsteroidState)</c>
    /// under pack(1) = 13.</summary>
    public const int Size = 13;

    public void Write(ref WireWriter w)
    {
        w.WriteI16(AsteroidId);      // offset 0
        Co.Write(ref w);             // offsets 2..9
        w.WriteI16(Ore);             // offset 10
        w.WriteByte(BpFraction);     // offset 12
    }

    public static AsteroidStateEntry Read(ReadOnlySpan<byte> b) => new(
        AsteroidId: BinaryPrimitives.ReadInt16LittleEndian(b),
        Co: CompactOrientation.Read(b.Slice(2)),
        Ore: BinaryPrimitives.ReadInt16LittleEndian(b.Slice(10)),
        BpFraction: b[12]);
}

/// <summary>
/// <c>FM_S_ASTEROIDS_UPDATE</c> (id 179), the server's periodic per-side
/// asteroid visibility/ore/health sync (<c>Messages.h:917-928</c>), quoted in
/// full:
/// <code>
///   DEFINE_FEDMSG(S, ASTEROIDS_UPDATE, 179)
///     FM_VAR_ITEM(rgAsteroidStates);
///   END_FEDMSG
/// </code>
/// The sole member is the <c>rgAsteroidStates</c> var item — an array of
/// <see cref="AsteroidStateEntry"/> (<c>AsteroidState</c>). Like
/// <see cref="StationsUpdate"/> (57) and <see cref="RankInfo"/> (13) there is
/// <b>no</b> explicit count field: the count is implied by the blob's byte
/// length over the 13-byte element stride (<c>cb / sizeof(AsteroidState)</c>).
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 179
///   offset 4: ushort ib (blob offset, from byte 0)
///   offset 6: ushort cb (blob byte length = n × 13)
///   offset 8: blob — n × AsteroidStateEntry(13)
///   = 8 + blob bytes total
/// </code>
/// <para><b>Wire semantics</b> (P134, ported from the native per-cluster-per-
/// side batch in <c>fsship.cpp:883-915</c>, the direct sibling of the
/// <c>STATIONS_UPDATE</c> batch at <c>fsship.cpp:817-849</c>): this is a
/// <b>periodic</b> per-side broadcast, <b>not</b> a one-time join-sync. The
/// reference collects, for each side and each cluster, every asteroid that side
/// currently sees (<c>pasteroid-&gt;SeenBySide(pside)</c>) and batches them into
/// one <c>ASTEROIDS_UPDATE</c>, gated on <c>if (nAsteroids != 0)</c> (an empty
/// update is not sent), addressed only to <c>pside</c>'s own connection group —
/// a side gets only the asteroids it can currently see, not the whole mission
/// (contrast the whole-mission audiences of <c>ASTEROID_MINED</c>(198) /
/// <c>ASTEROID_DESTROYED</c>(86) / <c>STATION_DESTROYED</c>(59)). The
/// <see cref="Allegiance.Server.Server.BroadcastAsteroidsUpdate"/> performs that
/// per-side batching each tick; this codec defines the message shape plus the
/// one conversion baked into <see cref="FromAsteroids"/>.</para>
/// <para><b>Visibility source.</b> Asteroids are genuine visibility targets —
/// native gives them <c>c_mtSeenBySide</c> (<c>asteroidigc.cpp:36</c>) and the
/// cross-side sweep scores them like any other target (own-side rule does not
/// apply — an asteroid has no <c>SideId</c>, so it is seen only via a scanner
/// of the requesting side in range with line of sight). This port's P132 sweep
/// previously scored only ships/probes/stations; P134 extends it so
/// <c>_mineable</c> asteroids join as targets (see <c>Sector.ResolveCrossSideVisibility</c>),
/// giving <see cref="Asteroid.SeenBySide"/> a real per-side answer.</para>
/// </summary>
public sealed class AsteroidsUpdate : IFedMessage
{
    public const ushort Fmid = 179;

    /// <summary>Wire width of the fixed region before the blob:
    /// header(4) + one ib/cb var pair(4) = 8. No fixed count field — the count
    /// is implicit in the blob length over <see cref="AsteroidStateEntry.Size"/>.</summary>
    public const int FixedSize = FedMessageHeader.HeaderSize + VarItem.ItemSize;

    /// <summary>The per-side asteroid list, in the sender's collection order.</summary>
    public AsteroidStateEntry[] Entries = [];

    public ushort GetFmid() => Fmid;

    /// <summary>Build the message from the live asteroids a side currently
    /// sees — bakes the div-240 <see cref="CompactQuant.Percent"/> conversion
    /// and the two field-semantics resolutions in so the caller never touches
    /// the wire convention directly (mirrors <see cref="StationsUpdate.FromStations"/>
    /// and <see cref="AsteroidMined.ForAsteroid"/>): <c>ore</c> = the raw
    /// current ore pool <see cref="Asteroid.OreRemaining"/> (clamped to short
    /// range, the native <c>short(...)</c> truncation), <c>bpFraction</c> = the
    /// rock's health fraction <see cref="Asteroid.Fraction"/>, and <c>co</c> =
    /// identity (see the entry doc for the cited simplification).</summary>
    public static AsteroidsUpdate FromAsteroids(IEnumerable<Asteroid> asteroids)
    {
        var entries = asteroids
            .Select(a => new AsteroidStateEntry(
                a.Data.AsteroidId,
                CompactOrientation.Identity,
                Ore: (short)Math.Clamp(a.OreRemaining, short.MinValue, short.MaxValue),
                BpFraction: CompactQuant.Percent(Math.Clamp(a.Fraction, 0f, 1f))))
            .ToArray();
        return new AsteroidsUpdate { Entries = entries };
    }

    public byte[] Encode()
    {
        int blobLen = Entries.Length * AsteroidStateEntry.Size;
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int slotIb = w.Reserve(2);  // ib @4
        int slotCb = w.Reserve(2);  // cb @6
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"ASTEROIDS_UPDATE internal layout drift: {w.Position} != {FixedSize}");

        // blob at the fixed-region end (ib = offset from byte 0)
        int ib = w.Position;
        foreach (var e in Entries) e.Write(ref w);
        w.WriteU16At(slotIb, (ushort)ib);
        w.WriteU16At(slotCb, (ushort)(w.Position - ib));
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg
        return buf;
    }

    public static AsteroidsUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"ASTEROIDS_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected ASTEROIDS_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var it = VarItem.Read(buf.Slice(4));   // the single ib/cb pair
        var entries = new List<AsteroidStateEntry>();
        if (it.IsPresent)
        {
            if (it.Ib < 0 || it.Ib + it.Cb > buf.Length)
                throw new ArgumentException($"ASTEROIDS_UPDATE blob [{it.Ib},{it.Ib + it.Cb}) out of range for length {buf.Length}");
            if (it.Cb % AsteroidStateEntry.Size != 0)
                throw new ArgumentException($"ASTEROIDS_UPDATE blob length {it.Cb} is not a multiple of {AsteroidStateEntry.Size}");
            var blob = buf.Slice(it.Ib, it.Cb);
            for (int i = 0; i < it.Cb / AsteroidStateEntry.Size; i++)
                entries.Add(AsteroidStateEntry.Read(blob.Slice(i * AsteroidStateEntry.Size, AsteroidStateEntry.Size)));
        }
        return new AsteroidsUpdate { Entries = entries.ToArray() };
    }
}
