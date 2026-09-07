namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_PLAYER_READY</c> (id 45, <c>Messages.h:380-384</c>), a client-to-
/// server request that the server is expected to relay back:
/// <code>
/// DEFINE_FEDMSG(CS, PLAYER_READY, 45) // client is ready or not
///   // Sent to everyone on team
///   bool fReady;
///   ShipID shipID;
/// END_FEDMSG
/// </code>
/// The native WinTrek client's Team Lobby "JOIN" control
/// (<c>teampane.cpp</c>) sends this to mark itself ready; the client-side
/// handler (<c>appmsg.cpp:2297-2312</c>) only updates
/// <see cref="Sim.Player"/>-equivalent roster state (<c>PlayerInfo::Update</c>,
/// <c>clintlib.h:333</c>: <c>m_fmPlayerInfo.fReady = pfmPlayerReady->fReady</c>)
/// and fires the team-roster-changed sink when this message is <em>received</em>
/// — the client never applies its own click locally, it waits for the relay.
/// Before this port implemented it, the server logged "unsupported fmid 45 —
/// ignored" and the client's own ready state (and everything the Team Lobby
/// screen renders from it) never updated, making the JOIN button appear
/// completely dead — confirmed live (P84) on the real client.
/// <para>Layout (compiler-verified via <c>offsetof</c>/<c>sizeof</c> against
/// the real <c>FMD_CS_PLAYER_READY</c> struct on the Windows VM, no pragma
/// pack applies to this message): header(4) + <c>bool fReady</c>(1, offset 4)
/// + 1 byte alignment padding (offset 5) + <c>ShipID shipID</c>(short, 2,
/// offset 6) = <b>8 bytes fixed</b>.</para>
/// </summary>
public sealed class PlayerReady : IFedMessage
{
    public const ushort Fmid = 45;
    public const int FixedSize = 8;

    /// <summary>Whether the player has marked itself ready.</summary>
    public bool FReady;

    /// <summary>The ready player's ship id (<c>ShipID</c> = short, igc.h).</summary>
    public short ShipId;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);              // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteByte((byte)(FReady ? 1 : 0)); // offset 4
        w.WriteByte(0);                      // offset 5: alignment padding
        w.WriteI16(ShipId);                  // offset 6
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"PLAYER_READY internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static PlayerReady Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"PLAYER_READY needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected PLAYER_READY fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                    // header
        bool fReady = r.ReadByte() != 0; // offset 4
        r.Advance(1);                    // offset 5: alignment padding
        short shipId = r.ReadI16();      // offset 6
        return new PlayerReady { FReady = fReady, ShipId = shipId };
    }
}
