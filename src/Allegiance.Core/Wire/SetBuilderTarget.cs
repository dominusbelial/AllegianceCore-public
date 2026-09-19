namespace Allegiance.Core.Wire;

/// <summary>
/// P285 — a <b>NEW port-defined</b> client-to-server (<c>C</c>) message
/// (fmid <b>201</b>, beyond native's 195-max allocation, in this port's own
/// custom-extension range beside <c>ChangeAlliance</c>(196)/<c>AsteroidMined</c>
/// (198)/<c>ClusterInfo</c>(200)): the client tells the server which station
/// type the Builder hull the player is purchasing/launching should construct.
/// The server sets the peer ship's <see cref="Sim.Ship.BuilderStationType"/>
/// — this port's per-ship-instance <c>m_pbaseData</c> equivalent — closing the
/// gap the P268/P284 hangar shop had no player-facing way to set. Kept as a
/// small dedicated fmid (rather than extending the native-shaped
/// <c>BUY_LOADOUT</c>(4) codec) so the existing wire shape stays
/// byte-compatible with native.
/// Layout (natural alignment, like the sibling <see cref="JoinSide"/> codec):
/// header(4) + <c>hullId</c>(short, offset 4) + <c>stationTypeId</c>(short,
/// offset 6) = <b>8 bytes fixed</b>.
/// </summary>
public sealed class SetBuilderTarget : IFedMessage
{
    public const ushort Fmid = 201;
    public const int FixedSize = 8;

    /// <summary>The Builder hull the player is buying (<c>hullId</c>). The
    /// server honours the target only when this hull resolves in the core AND
    /// carries the <c>c_habmBuilder</c> (0x8000) capability — a player may only
    /// assign a build target when purchasing a Builder-class hull.</summary>
    public short HullId;

    /// <summary>The station type this Builder should construct
    /// (<c>stationTypeId</c>), set on <see cref="Sim.Ship.BuilderStationType"/>.</summary>
    public short StationTypeId;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(HullId);                  // offset 4
        w.WriteI16(StationTypeId);           // offset 6
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SET_BUILDER_TARGET internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static SetBuilderTarget Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SET_BUILDER_TARGET needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SET_BUILDER_TARGET fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        short hullId = r.ReadI16();      // offset 4
        short stationTypeId = r.ReadI16(); // offset 6
        return new SetBuilderTarget { HullId = hullId, StationTypeId = stationTypeId };
    }
}
