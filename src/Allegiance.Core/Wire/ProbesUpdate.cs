using System.Buffers.Binary;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

/// <summary>
/// One <c>ProbeState</c> wire record inside the <see cref="ProbesUpdate"/>
/// (92) message's <c>FM_VAR_ITEM(rgProbeStates)</c> array. The native struct is
/// defined at <c>Messages.h:635-642</c>:
/// <code>
///   //Force the probe update structure to be packed to 3 bytes
///   #pragma pack(push, 1)
///   struct ProbeState {
///     ProbeID        probeID;
///     BytePercentage bpFraction;
///   };
///   #pragma pack(pop)
/// </code>
/// <para><b>Packing / stride (confirmed, the key layout finding).</b> The
/// struct sits inside an explicit <c>#pragma pack(push, 1)</c>
/// (<c>Messages.h:635</c>) whose comment literally says "Force the probe update
/// structure to be packed to 3 bytes", so every member is 1-byte aligned and
/// the total is the raw field-sum with no tail rounding — <b>3 bytes</b>.
/// A <c>ProbeID</c> is a <c>short</c> (like every other id this arc puts on the
/// wire) and <c>BytePercentage</c> a <c>byte</c>; under default MSVC alignment
/// the short would align the total up to 4, and the pack(1) is what collapses
/// it to 3. The same non-default-packing discipline P130/P133/P134 documented
/// for this whole <c>STATIONS_UPDATE</c>/<c>ASTEROIDS_UPDATE</c> family — and
/// the simplest element of the three (probes carry no orientation/ore/shield
/// complexity, only an id and a health fraction).</para>
/// <code>
///   offset 0: short probeID
///   offset 2: byte  bpFraction
///   = 3 bytes, no alignment gaps (pack(1))
/// </code>
/// <para><b>Field semantics (confirmed against the real native assignment in
/// the message's own construction, fsship.cpp:876-877).</b>
/// <c>pps-&gt;bpFraction = pprobe-&gt;GetFraction()</c>, and
/// <c>CprobeIGC::GetFraction()</c> returns <c>m_fraction</c>
/// (<c>probeigc.h:148-151</c>) — the probe's <b>health</b> pool, the same
/// div-240 health fraction this arc transmits for a body everywhere else.
/// Exactly like P134's surprising asteroid finding, this is <b>health, not
/// ore</b>: a probe has no ore concept at all, and <c>m_fraction</c> is written
/// by <c>CprobeIGC::ReceiveDamage</c> (<c>probeigc.h:92-119</c>,
/// <c>m_fraction -= amount / maxHP</c>, clamped to 0..1) — the direct-hit
/// destructibility pool P100 made a real, written field here
/// (<see cref="Probe.Fraction"/>). Port analogue: <see cref="Probe.Fraction"/>,
/// consumed by <see cref="Probe.ReceiveDamage"/>.</para>
/// </summary>
public readonly record struct ProbesUpdateEntry(short ProbeId, byte BpFraction)
{
    /// <summary>Wire width of one record = native <c>sizeof(ProbeState)</c>
    /// under the explicit pack(1) = 3.</summary>
    public const int Size = 3;

    public void Write(ref WireWriter w)
    {
        w.WriteI16(ProbeId);      // offset 0
        w.WriteByte(BpFraction);  // offset 2
    }

    public static ProbesUpdateEntry Read(ReadOnlySpan<byte> b) => new(
        ProbeId: BinaryPrimitives.ReadInt16LittleEndian(b),
        BpFraction: b[2]);
}

/// <summary>
/// <c>FM_S_PROBES_UPDATE</c> (id 92), the server's periodic per-side probe
/// visibility/health sync (<c>Messages.h:644-646</c>), quoted in full:
/// <code>
///   DEFINE_FEDMSG(S, PROBES_UPDATE, 92)
///     FM_VAR_ITEM(rgProbeStates);
///   END_FEDMSG
/// </code>
/// The sole member is the <c>rgProbeStates</c> var item — an array of
/// <see cref="ProbesUpdateEntry"/> (<c>ProbeState</c>). As with
/// <see cref="StationsUpdate"/> (57) and <see cref="AsteroidsUpdate"/> (179)
/// there is <b>no</b> explicit count field: the count is implied by the blob's
/// byte length over the 3-byte element stride (<c>cb / sizeof(ProbeState)</c>).
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 92
///   offset 4: ushort ib (blob offset, from byte 0)
///   offset 6: ushort cb (blob byte length = n × 3)
///   offset 8: blob — n × ProbesUpdateEntry(3)
///   = 8 + blob bytes total
/// </code>
/// <para><b>Wire semantics</b> (P135, ported from the native per-cluster-per-
/// side batch in <c>fsship.cpp:851-881</c>, the direct sibling of the
/// <c>STATIONS_UPDATE</c> batch at <c>fsship.cpp:817-849</c> and the
/// <c>ASTEROIDS_UPDATE</c> batch at <c>fsship.cpp:883-915</c>): this is a
/// <b>periodic</b> per-side broadcast, <b>not</b> a one-time join-sync. The
/// reference collects, for each side and each cluster, every probe that side
/// currently sees (<c>pprobe-&gt;SeenBySide(pside)</c>) and batches them into
/// one <c>PROBES_UPDATE</c>, gated on <c>if (nProbes != 0)</c> (an empty update
/// is not sent), addressed only to <c>pside</c>'s own connection group — a
/// side gets only the probes it can currently see, not the whole mission.
/// The <see cref="Allegiance.Server.Server.BroadcastProbesUpdate"/> performs
/// that per-side batching each tick; this codec defines the message shape plus
/// the one float→wire conversion baked into <see cref="FromProbes"/>.</para>
/// <para><b>Visibility source.</b> Probes ARE visibility targets natively
/// (<c>c_mtSeenBySide</c>, probeigc.cpp:115) — and, unlike asteroids, a probe
/// <b>has a <c>SideId</c></b> and is a live <see cref="Model"/>, so P132's
/// per-tick sweep already scores it correctly with <b>no special extension</b>:
/// the probe case in <c>Sector.ResolveCrossSideVisibility</c> adds it as both a
/// target and a scanner for its own side (see <see cref="Probe.SeenBySide"/>),
/// so a probe's own side sees it unconditionally (own-side rule) and a foreign
/// side sees it only via that side's scanner in range with line of sight —
/// exactly the native model. This is the finding that distinguishes probes from
/// asteroids, which needed P134's explicit sweep extension because they carry
/// no <c>SideId</c> and live outside the model set.</para>
/// </summary>
public sealed class ProbesUpdate : IFedMessage
{
    public const ushort Fmid = 92;

    /// <summary>Wire width of the fixed region before the blob:
    /// header(4) + one ib/cb var pair(4) = 8. No fixed count field — the count
    /// is implicit in the blob length over <see cref="ProbesUpdateEntry.Size"/>.</summary>
    public const int FixedSize = FedMessageHeader.HeaderSize + VarItem.ItemSize;

    /// <summary>The per-side probe list, in the sender's collection order.</summary>
    public ProbesUpdateEntry[] Entries = [];

    public ushort GetFmid() => Fmid;

    /// <summary>Build the message from the live probes a side currently sees —
    /// bakes the div-240 <see cref="CompactQuant.Percent"/> conversion in so the
    /// caller never touches the wire convention directly (mirrors
    /// <see cref="StationsUpdate.FromStations"/> and
    /// <see cref="AsteroidsUpdate.FromAsteroids"/>): <c>bpFraction</c> = the
    /// probe's health fraction <see cref="Probe.Fraction"/> (the P100-written
    /// direct-hit pool, the same semantics native's
    /// <c>pprobe-&gt;GetFraction()</c> transmits), clamped to 0..1 defensively
    /// before the byte cast.</summary>
    public static ProbesUpdate FromProbes(IEnumerable<Probe> probes)
    {
        var entries = probes
            .Select(p => new ProbesUpdateEntry(
                p.Id,
                CompactQuant.Percent(Math.Clamp(p.Fraction, 0f, 1f))))
            .ToArray();
        return new ProbesUpdate { Entries = entries };
    }

    public byte[] Encode()
    {
        int blobLen = Entries.Length * ProbesUpdateEntry.Size;
        var buf = new byte[FixedSize + blobLen];
        WireWriter w = new(buf);

        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        int slotIb = w.Reserve(2);  // ib @4
        int slotCb = w.Reserve(2);  // cb @6
        if (w.Position != FixedSize)
            throw new InvalidOperationException($"PROBES_UPDATE internal layout drift: {w.Position} != {FixedSize}");

        // blob at the fixed-region end (ib = offset from byte 0)
        int ib = w.Position;
        foreach (var e in Entries) e.Write(ref w);
        w.WriteU16At(slotIb, (ushort)ib);
        w.WriteU16At(slotCb, (ushort)(w.Position - ib));
        w.WriteU16At(0, (ushort)w.Position);   // cbmsg
        return buf;
    }

    public static ProbesUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"PROBES_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected PROBES_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        var it = VarItem.Read(buf.Slice(4));   // the single ib/cb pair
        var entries = new List<ProbesUpdateEntry>();
        if (it.IsPresent)
        {
            if (it.Ib < 0 || it.Ib + it.Cb > buf.Length)
                throw new ArgumentException($"PROBES_UPDATE blob [{it.Ib},{it.Ib + it.Cb}) out of range for length {buf.Length}");
            if (it.Cb % ProbesUpdateEntry.Size != 0)
                throw new ArgumentException($"PROBES_UPDATE blob length {it.Cb} is not a multiple of {ProbesUpdateEntry.Size}");
            var blob = buf.Slice(it.Ib, it.Cb);
            for (int i = 0; i < it.Cb / ProbesUpdateEntry.Size; i++)
                entries.Add(ProbesUpdateEntry.Read(blob.Slice(i * ProbesUpdateEntry.Size, ProbesUpdateEntry.Size)));
        }
        return new ProbesUpdate { Entries = entries.ToArray() };
    }
}
