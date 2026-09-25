using System.Buffers.Binary;

namespace Allegiance.Core.Wire;

/// <summary>
/// <b>This-port-only extension (no native counterpart).</b> P374 — the per-side,
/// change-driven delivery of a sector's <see cref="ClusterWarnings"/> threat state
/// from the server to the clients of one side. Native's client ran its own IGC
/// simulation and computed each cluster's warning locally (<c>trekigc.cpp:327-383</c>,
/// <c>ClusterSiteImpl::UpdateClusterWarnings</c>) from ships/stations it knew
/// about; this port's renderer-only MonoGame client has no static core and no
/// knowledge of enemy ships in distant sectors, so the server — which already
/// tracks every ship's hull/pilot type and every station's side in
/// <c>Sector._models</c> / <c>StationRuntimes</c> — computes the per-side warning
/// and streams it on change (see <c>Server.BroadcastSectorThreats</c>).
///
/// Sent to the members of ONE side when that side's view of ONE sector changes
/// in any of the three ways native's <c>UpdateClusterWarnings</c> reacts to:
/// <list type="bullet">
/// <item><see cref="Warning"/> changed to a new level (the client stores it for
/// the starmap flashing icons / hover text, and fires the alert sound + status
/// line on the INCREASE edge — <c>cwNew &gt; cwOld &amp;&amp; cwNew &gt; NoThreat</c>);</item>
/// <item><see cref="Secured"/> — the sector just gained a friendly station with no
/// enemy station → "<c>{name} secured.</c>" + <c>sectorSecuredSound</c>;</item>
/// <item><see cref="Lost"/> — the sector just lost its last friendly station →
/// "<c>{name} lost.</c>" + <c>sectorLostSound</c>.</item>
/// </list>
///
/// FMID <b>214</b> sits above native's entire real FMID range (max 200) and above
/// this port's prior extensions (204-213), so it can never collide. Layout (our
/// own, no native DEFINE_FEDMSG to be byte-faithful to):
/// <code>
///   offset  0: ushort cbmsg
///   offset  2: ushort fmid = 214
///   offset  4: short  sectorId    (the sector whose state changed)
///   offset  6: short  warning     (ClusterWarnings level 0..15)
///   offset  8: byte   secured     (1 = "sector secured" transition this update)
///   offset  9: byte   lost        (1 = "sector lost" transition this update)
///   = 10 bytes fixed
/// </code>
/// </summary>
public sealed class ClusterWarningUpdate : IFedMessage
{
    /// <summary>This-port-only FMID, above native's real range (max 200).</summary>
    public const ushort Fmid = 214;

    /// <summary>Wire width, bytes.</summary>
    public const int Width = 10;

    private readonly byte[] _b = new byte[Width];

    private ClusterWarningUpdate() { }

    /// <summary>The sector (cluster) id whose threat state changed.</summary>
    public short SectorId => BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(4));

    /// <summary>The new current <see cref="ClusterWarnings"/> level for this
    /// sector, from this side's perspective (0 = no threat).</summary>
    public short Warning => BinaryPrimitives.ReadInt16LittleEndian(_b.AsSpan(6));

    /// <summary>True when this update IS the "sector secured" transition (native
    /// posts "<c>{sector} secured.</c>" + plays <c>sectorSecuredSound</c>).</summary>
    public bool Secured => _b[8] != 0;

    /// <summary>True when this update IS the "sector lost" transition (native
    /// posts "<c>{sector} lost.</c>" + plays <c>sectorLostSound</c>).</summary>
    public bool Lost => _b[9] != 0;

    /// <summary>Build the message. <paramref name="warning"/> is the current
    /// level; <paramref name="secured"/>/<paramref name="lost"/> flag the
    /// one-shot control transitions (at most one of them true per message, and
    /// usually both false for a mere warning-level change).</summary>
    public static ClusterWarningUpdate From(
        short sectorId, short warning, bool secured = false, bool lost = false)
    {
        var u = new ClusterWarningUpdate();
        BinaryPrimitives.WriteInt16LittleEndian(u._b.AsSpan(4), sectorId);
        BinaryPrimitives.WriteInt16LittleEndian(u._b.AsSpan(6), warning);
        u._b[8] = secured ? (byte)1 : (byte)0;
        u._b[9] = lost ? (byte)1 : (byte)0;
        return u;
    }

    /// <summary>Encode the 10 canonical bytes at the writer's current position.</summary>
    public void EncodeBody(ref WireWriter w) => w.WriteBytes(_b);

    /// <summary>Read a 10-byte body from the reader's current position.</summary>
    public static ClusterWarningUpdate DecodeBody(ref WireReader r)
    {
        if (r.HasRemaining && r.Length - r.Position < Width)
            throw new ArgumentException($"ClusterWarningUpdate needs {Width} bytes, got {r.Length - r.Position}");
        var u = new ClusterWarningUpdate();
        r.ReadBytes(Width).CopyTo(u._b);
        return u;
    }

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[Width];
        WireWriter w = new(buf);
        w.WriteU16(Width);          // cbmsg
        w.WriteU16(Fmid);
        w.WriteBytes(_b.AsSpan(4)); // body (offsets 4..10)
        return buf;
    }

    /// <summary>Decode a whole message (header + body) from raw bytes, validating
    /// the fmid and length like the other codecs.</summary>
    public static ClusterWarningUpdate Decode(ReadOnlySpan<byte> m)
    {
        if (m.Length < Width)
            throw new ArgumentException($"ClusterWarningUpdate needs {Width} bytes, got {m.Length}");
        var hdr = FedMessageHeader.Read(m);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected ClusterWarningUpdate fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != m.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {m.Length}");
        var u = new ClusterWarningUpdate();
        m.Slice(4, Width - 4).CopyTo(u._b.AsSpan(4));
        return u;
    }
}
