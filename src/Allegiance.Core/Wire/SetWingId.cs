namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_CS_SET_WINGID</c> (id 106, <c>Inc/Messages.h:705-708</c>, quoted in
/// full): a player tagging a ship with a wing-group membership id, relayed
/// side-wide (P146):
/// <code>
///   DEFINE_FEDMSG(CS, SET_WINGID, 106)
///     ShipID              shipID;
///     WingID              wingID;
///     bool                bCommanded;
///   END_FEDMSG
/// </code>
/// The native handler (<c>FedSrv.CPP:5312-5344</c>, <c>case FM_CS_SET_WINGID</c>)
/// decodes <c>shipID</c>/<c>wingID</c>, bounds-checks <c>wingID &lt; c_widMax</c>,
/// server-corrects <c>bCommanded</c> (<c>false</c> for a self-assignment,
/// <c>true</c> when a leader retags another side-member), forwards the whole
/// (corrected) message <b>side-wide</b> via
/// <c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c> (<c>FedSrv.CPP:5337</c>), and
/// mutates the target ship's <c>WingID</c> server-side. Like <c>SWAP_PART</c>/
/// <c>DROP_PART</c>, the CS variant is used in both directions — the client
/// sends it and the server forwards the corrected bytes to the same side.
/// </summary>
/// <remarks>
/// <para><b><c>WingID</c> is a <c>short</c>, not a byte.</b> It is a typedef
/// alias of <c>ObjectID</c> (<c>Igc/igc.h:504</c> <c>typedef ObjectID WingID;</c>),
/// and <c>ObjectID</c> itself is <c>typedef short</c> (<c>igc.h:477</c>). The
/// bound is <c>c_widMax = 10</c> (<c>igc.h:505</c>), matching the ten named
/// wings in <c>c_pszWingName[c_widMax]</c> (<c>Igc/common.cpp:23-35</c>:
/// <c>"command","attack","defend","escort","search","alpha","bravo",
/// "charlie","delta","echo"</c>) — the native client also validates
/// <c>0 ≤ Val ≤ 9</c> before sending (<c>PigSrv/PigShip.cpp:234-236</c>).</para>
/// <para><b><c>ShipID</c></b> is a <c>short</c> (ObjectID/ShipID,
/// <c>igc.h:477,480</c>), same as every ship-id field in this port.
/// <b><c>bCommanded</c></b> is a one-byte C++ <c>bool</c>, the same convention
/// this port uses e.g. <see cref="HighlightCluster"/>.</para>
/// </remarks>
/// <code>
///   offset 0: ushort cbmsg
///   offset 2: ushort fmid = 106
///   offset 4: short  shipID       (ShipID)
///   offset 6: short  wingID       (WingID = ObjectID = short)
///   offset 8: bool   bCommanded   (1 byte, C++ bool)
///   offset 9: 1 byte alignment pad (C++ struct align 2 → sizeof 10)
///   = 10 B fixed, no var items → cbmsg = 10
/// </code>
/// Identical size/shape to the sibling <c>DROP_PART(33)</c>
/// (<see cref="DropPart"/>) — header + two shorts @4/@6 + a trailing 1-byte
/// bool @8 + 1 align pad @9, 10 bytes total.
/// </summary>
public sealed class SetWingId : IFedMessage
{
    public const ushort Fmid = 106;
    public const int FixedSize = 10;   // header + shipID short @4 + wingID short @6 + bCommanded bool @8 + 1 pad @9

    /// <summary>Native's wing-group bound: <c>c_widMax = 10</c> (<c>igc.h:505</c>).
    /// Valid wing ids are <c>[0, WidMax)</c>.</summary>
    public const short WidMax = 10;

    /// <summary>The id of the tagged ship. Per this port's self-targeting-only
    /// simplification (no team-leader system), the server requires this to be
    /// the <em>sending</em> player's own ship id (<c>FedSrv.CPP:5323</c>) — the
    /// client sends its own id (<c>PigSrv/PigShip.cpp:240</c>).</summary>
    public short ShipID;

    /// <summary>The wing-group id being assigned (a <c>short</c>, <c>[0, WidMax)</c>
    /// — <c>WingID = ObjectID = short</c>, <c>igc.h:477,504</c>).</summary>
    public short WingId;

    /// <summary>Whether the assignment is leader-issued (<c>true</c>) or a
    /// self-assignment (<c>false</c>) — a UI-display distinction on the client,
    /// not simulation-relevant. The server always corrects it; with the
    /// self-targeting-only simplification it is always <c>false</c>
    /// (<c>FedSrv.CPP:5334-5335</c>).</summary>
    public bool BCommanded;

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);                       // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipID);                  // offset 4: shipID (ShipID short)
        w.WriteI16(WingId);                  // offset 6: wingID (WingID short)
        w.WriteByte(BCommanded ? (byte)1 : (byte)0);   // offset 8: bCommanded (C++ bool)
        while (w.Position < FixedSize) w.WriteByte(0);   // align-2 padding (offset 9)
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SET_WINGID internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static SetWingId Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SET_WINGID needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SET_WINGID fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                // header
        short shipID = r.ReadI16();
        short wingID = r.ReadI16();
        bool bCommanded = r.ReadByte() != 0;
        return new SetWingId { ShipID = shipID, WingId = wingID, BCommanded = bCommanded };
    }
}
