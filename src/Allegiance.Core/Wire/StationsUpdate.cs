using System.Buffers.Binary;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>
/// One <c>StationState</c> wire record inside the <see cref="StationsUpdate"/>
/// (57) message's <c>FM_VAR_ITEM(rgStationStates)</c> array. The native struct
/// is defined at <c>Messages.h:418-424</c> (quoted in the P133 task):
/// <code>
///   struct StationState {
///     StationID      stationID;
///     BytePercentage bpHullFraction;
///     BytePercentage bpShieldFraction;
///   };
/// </code>
/// <para><b>Element stride.</b> The array blob is memcpy'd verbatim by the
/// client (<c>appmsg.cpp</c> reads <c>FM_VAR_REF(pfm, rgStationStates)</c> as
/// <c>n * sizeof(StationState)</c>), and the server emits it as
/// <c>FM_VAR_PARM(NULL, nStations * sizeof(StationState))</c> (fsship.cpp), so
/// the per-element size is the native compile-time <c>sizeof(StationState)</c>.
/// StationState (<c>Messages.h:418-422</c>) sits in the same pre-pack block the
/// <c>DEFINE_FEDMSG</c> macro and this port's ship-update payloads occupy —
/// the natural packed layout is short(2) + byte(1) + byte(1) = <b>4</b>, the
/// same id+byte+byte triangle as <c>ASTEROID_MINED</c>'s fixed head
/// (short/short/byte) minus one id. A <c>StationID</c> is a <c>short</c>
/// (<c>igc.h</c>, identical to the asteroid/sector/ship ids this port already
/// puts on the wire):</para>
/// <code>
///   offset 0: short stationID
///   offset 2: byte  bpHullFraction    (div-240 BytePercentage, Utility.h:1307-1312)
///   offset 3: byte  bpShieldFraction  (div-240 BytePercentage)
///   = 4 bytes, no alignment gaps
/// </code>
/// </summary>
public readonly record struct StationsUpdateEntry(short StationId, byte BpHullFraction, byte BpShieldFraction)
{
    /// <summary>Wire width of one record = native <c>sizeof(StationState)</c> = 4.</summary>
    public const int Size = 4;

    public void Write(ref WireWriter w)
    {
        w.WriteI16(StationId);          // offset 0
        w.WriteByte(BpHullFraction);    // offset 2
        w.WriteByte(BpShieldFraction);  // offset 3
    }

    public static StationsUpdateEntry Read(ReadOnlySpan<byte> b)
        => new(
            StationId: BinaryPrimitives.ReadInt16LittleEndian(b),
            BpHullFraction: b[2],
            BpShieldFraction: b[3]);
}

/// <summary>
/// <c>FM_S_STATIONS_UPDATE</c> (id 57), the server's periodic per-side station
/// visibility/health sync (<c>Messages.h:418-426</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, STATIONS_UPDATE, 57)
///     FM_VAR_ITEM(rgStationStates);
///   END_FEDMSG
/// </code>
/// The sole member is the <c>rgStationStates</c> var item — an array of
/// <see cref="StationsUpdateEntry"/> (<c>StationState</c>). Unlike
/// <see cref="RankInfo"/> (13) there is <b>no</b> explicit count field: the
/// count is implied by the blob's byte length over the element stride
/// (<c>cb / sizeof(StationState)</c>).
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 57
///   offset 4: ushort ib (blob offset, from byte 0)
///   offset 6: ushort cb (blob byte length = n × 4)
///   offset 8: blob — n × StationsUpdateEntry(4)
///   = 8 + blob bytes total
/// </code>
/// <para><b>Wire semantics</b> (P133, ported from the native per-side batch in
/// <c>fsship.cpp:820-845</c>): this is a <b>periodic</b> per-side broadcast,
/// <b>not</b> a one-time join-sync. The reference collects, for each side and
/// each cluster, every station that side currently sees
/// (<c>pstation-&gt;SeenBySide(pside)</c> — now a real, live answer thanks to
/// the P132 visibility sweep) and batches them into one <c>STATIONS_UPDATE</c>,
/// gated on <c>if (nStations != 0)</c> (an empty update is not sent), addressed
/// only to <c>pside</c>'s own connection group — a side gets only the stations
/// it can currently see, not the whole mission (contrast the whole-mission
/// <c>ASTEROID_MINED</c>(198)/<c>STATION_DESTROYED</c>(59) audiences). The
/// <see cref="Server"/> performs that per-side batching each tick in
/// <see cref="Allegiance.Server.Server.BroadcastStationsUpdate"/>; this codec
/// defines the message shape plus the one float→wire conversion
/// (<see cref="FromStations"/>).</para>
/// <para><b>Fraction encoding.</b> <c>bpHullFraction</c> /
/// <c>bpShieldFraction</c> are div-240 <c>BytePercentage</c>s produced by
/// <see cref="CompactQuant.Percent"/>, the exact convention this port already
/// uses for a body's fraction on the wire (cf. <c>ASTEROID_MINED</c>'s
/// <c>bpOreFraction</c>, <c>AsteroidMined.cs:59</c>, and the ship-update
/// <c>HullFraction</c>/<c>ShieldFraction</c> slots,
/// <c>ServerShipUpdate.cs:122-123</c>) — fed from the P125 live station
/// fractions <see cref="Station.ArmorFraction"/> / <see cref="Station.ShieldFraction"/>.</para>
/// </summary>
public sealed class StationsUpdate : IFedMessage
{
    public const ushort Fmid = 57;

    /// <summary>Wire width of the fixed region before the blob:
    /// header(4) + one ib/cb var pair(4) = 8. No fixed count field — the count
    /// is implicit in the blob length over <see cref="StationsUpdateEntry.Size"/>.</summary>
    public const int FixedSize = FedMessageHeader.HeaderSize + VarItem.ItemSize;

    /// <summary>The per-side station list, in the sender's collection order.</summary>
    public StationsUpdateEntry[] Entries = [];

    public ushort GetFmid() => Fmid;

    /// <summary>Build the message from the live stations a side currently sees —
    /// bakes the div-240 <see cref="CompactQuant.Percent"/> conversion in so the
    /// caller never touches the wire convention directly (mirrors
    /// <see cref="AsteroidMined.ForAsteroid"/>). Fractions are clamped to 0..1
    /// defensively before the byte cast; the same conversion
    /// <c>ServerShipUpdate</c> applies to a ship's hull/shield slots.</summary>
    public static StationsUpdate FromStations(IEnumerable<Station> stations)
    {
        var entries = stations
            .Select(st => new StationsUpdateEntry(
                st.StationId,
                CompactQuant.Percent(Math.Clamp(st.ArmorFraction, 0f, 1f)),
                CompactQuant.Percent(Math.Clamp(st.ShieldFraction, 0f, 1f))))
            .ToArray();
        return new StationsUpdate { Entries = entries };
    }

    public byte[] Encode()
    {
        int blobLen = Entries.Length * StationsUpdateEntry.Size;
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int slotIb = w.Reserve(2);  // ib @4
        int slotCb = w.Reserve(2);  // cb @6
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"STATIONS_UPDATE internal layout drift: {w.Position} != {FixedSize}");

        // blob at the fixed-region end (ib = offset from byte 0)
        int ib = w.Position;
        foreach (var e in Entries) e.Write(ref w);
        w.WriteU16At(slotIb, (ushort)ib);
        w.WriteU16At(slotCb, (ushort)(w.Position - ib));
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg
        return buf;
    }

    public static StationsUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"STATIONS_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected STATIONS_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var it = VarItem.Read(buf.Slice(4));   // the single ib/cb pair
        var entries = new List<StationsUpdateEntry>();
        if (it.IsPresent)
        {
            if (it.Ib < 0 || it.Ib + it.Cb > buf.Length)
                throw new ArgumentException($"STATIONS_UPDATE blob [{it.Ib},{it.Ib + it.Cb}) out of range for length {buf.Length}");
            if (it.Cb % StationsUpdateEntry.Size != 0)
                throw new ArgumentException($"STATIONS_UPDATE blob length {it.Cb} is not a multiple of {StationsUpdateEntry.Size}");
            var blob = buf.Slice(it.Ib, it.Cb);
            for (int i = 0; i < it.Cb / StationsUpdateEntry.Size; i++)
                entries.Add(StationsUpdateEntry.Read(blob.Slice(i * StationsUpdateEntry.Size, StationsUpdateEntry.Size)));
        }
        return new StationsUpdate { Entries = entries.ToArray() };
    }
}
