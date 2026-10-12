namespace Allegiance.Core.Wire;

/// <summary>
/// P612 — a PORT-EXTENSION server→client message telling the client the server's
/// current sim time scale (<c>sim.timeScale</c>, 0.1–1.0). The server runs the
/// world in slow motion by advancing its sim clock by <c>period × scale</c>, so
/// the client's REMOTE-SHIP dead-reckoning/interpolation must extrapolate with
/// sim time (wall elapsed × scale), not wall time — otherwise ships would
/// overshoot ahead of their authoritative (slowed) positions and rubber-band.
/// <para>
/// Layout (uniform-packed, no host alignment padding — the same convention as
/// every codec here, e.g. <see cref="ViewClusterReply"/>):
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 991
///   offset 4: uint   scaleX1000   (the scale × 1000 as a uint, 1000 = 1.0)
///   = 8 B fixed → cbmsg = 8
/// </code>
/// Transmitted as an integer (scale × 1000) to keep the wire lossless and simple;
/// the client recovers the float as scaleX1000/1000f. Sent to every connected
/// peer at boot (alongside the mission join burst) and re-sent whenever an admin
/// changes the scale, so late/joiner clients always converge on the current value.
/// </summary>
public sealed class TimescaleUpdate : IFedMessage
{
    public const ushort Fmid = 991;
    public const int FixedSize = 8;

    /// <summary>The server's sim time scale as a float (1.0 = normal speed).</summary>
    public float Scale = 1f;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteU32((uint)Math.Round(Scale * 1000f));
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"TIMESCALE_UPDATE internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static TimescaleUpdate Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"TIMESCALE_UPDATE needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected TIMESCALE_UPDATE fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                               // header
        return new TimescaleUpdate { Scale = r.ReadU32() / 1000f };
    }
}
