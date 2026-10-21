namespace Allegiance.Core.Wire;

/// <summary>
/// <c>FM_S_SHIP_STATUS</c> (id 90, <c>Messages.h:630-633</c>:
/// <c>DEFINE_FEDMSG(S, SHIP_STATUS, 90) ShipID shipID; ShipStatus status; END_FEDMSG</c>)
/// — the server's per-ship status message. This is the message the WOPR
/// <c>ScoutExploreMap</c> strategy's entire autonomous sweep/exploration logic
/// is gated on receiving
/// (<c>src/Wopr/Wopr/Strategies/ScoutExploreMap.cs:41-45,85-221,357-484</c>):
/// without it the strategy only ever took the <c>Start()</c>→<c>ReturnToBase()</c>
/// fallback for a non-scout hull. Found missing in P39.
///
/// Wire layout — <b>compiler-verified against the real MSVC toolchain</b> (a
/// standalone <c>sizeof</c>/<c>offsetof</c> probe compiled with cl.exe against
/// the checked-in <c>igc.h</c> + <c>Messages.h</c> on the Windows VM,
/// 2026-09-01), because g++'s <c>-mms-bitfields</c> emulation is NOT reliable
/// for this mixed char/bool bitfield case:
/// <code>
///   offset  0: ushort cbmsg = 24
///   offset  2: ushort fmid  = 90
///   offset  4: short   shipID
///   offset  6: (2 pad bytes — ShipStatus's DWORD member makes it 4-aligned)
///   offset  8: ShipStatus status (16 bytes, MSVC default packing; it is NOT in
///              a #pragma pack(push,1) region — igc.h:4399-4509 sits outside
///              both pack regions at igc.h:1673-1691 and 2120-2486):
///      +0 short   HullID
///      +2 short   SectorID
///      +4 short   StationID
///      +6 short   ParentID  (ShipID — the carrying ship if docked/turreted)
///      +8 byte    bitfield: bits0-5 ShipState, bit6 unknown, bit7 detected
///                 (MSVC packs the three bit-fields
///                 <c>ShipState m_state:6; bool m_unknown:1; bool m_detected:1;</c>
///                 into ONE byte — probe value for state=3,unknown=1,detected=1
///                 is 0xc3; verified, not guessed)
///      +12 DWORD  StateTime (m_dTime)
///     = 16 bytes
///   = 24 bytes total; no var items → cbmsg = 24
/// </code>
/// ShipState is an ordered enum, not bit flags (<c>igc.h:4389-4397</c>):
/// <c>c_ssDead=1, c_ssDocked=2, c_ssFlying=3, c_ssObserver=4, c_ssTurret=5</c>;
/// <c>fsship.cpp:417</c>'s <c>&gt;= c_ssFlying</c> comparison relies on that
/// ordering (confirmed by reading igc.h, not guessed).
///
/// <b>Detected/Unknown semantics.</b> These are real, not constants (HUD-17).
/// <c>Unknown=false</c> for every status this server sends about a ship's own
/// side (the port never hides a ship from its own side), and
/// <c>Detected</c> is the ship's own-side copy of native
/// <c>ShipStatus::GetDetected()</c>: true while at least one side that is
/// neither the ship's own side nor allied with it has a known (spotted) status
/// for it (<c>FedSrv/fsship.cpp:237-335</c>; computed by
/// <c>Allegiance.Core.Sim.ShipDetection.IsDetectedByEnemy</c>). A docked ship
/// reads false (docking leaves the cluster, <c>Igc/shipIGC.h:421-457</c>) and a
/// boarded child copies its carrier's value (<c>fsship.cpp:268-284,321-334</c>).
/// <b>Polarity:</b> the raw bit is true when the ship is eyed; the client's
/// own-HUD eye shows on the raw true (<c>WinTrek/console.cpp:811</c> +
/// <c>Artwork/dialog.mdl:206-207,2526-2534</c>) while
/// <c>PlayerInfo::GetDetected()</c> is the negation (<c>clintlib/clintlib.h:438</c>)
/// and the radar friendly eye reads the raw bit unchanged
/// (<c>WinTrek/radarimage.cpp:1006-1019</c>).
/// This is scoped to each peer's OWN side's copies: the peer's own ship status
/// and the same-side/teammate statuses. Cross-side (enemy-copy) statuses are a
/// separate concern (P493) and keep <c>Detected=false</c>.
/// </summary>
public sealed class ShipStatusMessage : IFedMessage
{
    public const ushort Fmid = 90;
    public const int ShipStatusSize = 16;
    public const int FixedSize = 8 + ShipStatusSize;

    // ShipState ordered enum values (igc.h:4389-4397).
    public const byte StateDead = 1;
    public const byte StateDocked = 2;
    public const byte StateFlying = 3;
    public const byte StateObserver = 4;
    public const byte StateTurret = 5;

    // --- Fields -----------------------------------------------------------
    public short ShipId;          // the peer's own ship id
    public short HullId;          // status.GetHullID()
    public short SectorId;        // status.GetSectorID() — drives ScoutExploreMap's sector tracking
    public short StationId;       // status.GetStationID() (NA/-1 when flying free)
    public short ParentId;        // status.GetParentID() (NA/-1 when not docked/turreted)
    public byte State = StateFlying;   // bits0-5 — the ShipState enum
    public bool Unknown;          // bit6 — false for every own-side status this server sends
    public bool Detected;  // bit7 (default false: every own-side builder sets it) — the ship's own-side Detected bit (ShipDetection.IsDetectedByEnemy)
    public uint StateTime;        // status.GetStateTime() — the server's sim clock at the moment of the status

    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        var buf = new byte[FixedSize];
        WireWriter w = new(buf);
        w.WriteU16(0);            // cbmsg placeholder
        w.WriteU16(Fmid);
        w.WriteI16(ShipId);

        // ShipStatus begins at offset 8 (2 pad bytes align its DWORD member).
        w.WriteZeros(2);          // pad
        w.WriteI16(HullId);
        w.WriteI16(SectorId);
        w.WriteI16(StationId);
        w.WriteI16(ParentId);
        byte bf = (byte)((State & 0x3F) | (Unknown ? 0x40 : 0) | (Detected ? 0x80 : 0));
        w.WriteByte(bf);
        w.WriteZeros(3);          // pad before the DWORD StateTime
        w.WriteU32(StateTime);
        w.WriteU16At(0, (ushort)w.Position);
        if (w.Position != FixedSize) throw new InvalidOperationException($"SHIP_STATUS internal layout drift: {w.Position} != {FixedSize}");
        return buf;
    }

    public static ShipStatusMessage Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < FixedSize)
            throw new ArgumentException($"SHIP_STATUS needs >= {FixedSize} bytes, got {buf.Length}");
        var hdr = FedMessageHeader.Read(buf);
        if (hdr.Fmid != Fmid) throw new ArgumentException($"expected SHIP_STATUS fmid={Fmid}, got {hdr.Fmid}");
        if (hdr.CbMsg != buf.Length) throw new ArgumentException($"cbmsg={hdr.CbMsg} != buffer length {buf.Length}");

        WireReader r = new(buf);
        r.Advance(4);                       // header
        short shipId = r.ReadI16();
        r.Advance(2);                       // pad
        short hullId = r.ReadI16();
        short sectorId = r.ReadI16();
        short stationId = r.ReadI16();
        short parentId = r.ReadI16();
        byte bf = r.ReadByte();
        r.Advance(3);                       // pad
        uint stateTime = r.ReadU32();

        return new ShipStatusMessage
        {
            ShipId = shipId,
            HullId = hullId,
            SectorId = sectorId,
            StationId = stationId,
            ParentId = parentId,
            State = (byte)(bf & 0x3F),
            Unknown = (bf & 0x40) != 0,
            Detected = (bf & 0x80) != 0,
            StateTime = stateTime,
        };
    }
}
