namespace Allegiance.Core.Wire;

/// <summary>
/// <c>PassengerData</c> (<c>src/Inc/Messages.h:30-34</c>), the per-boarded-child
/// entry carried by <c>FM_S_LOADOUT_CHANGE(96)</c>'s <c>rgPassengers</c> blob.
/// Native declaration:
/// <code>
///   struct PassengerData
///   {
///       ShipID  shipID;
///       Mount   turretID;
///   };
/// </code>
/// with <c>ShipID = ObjectID = short</c> (<c>src/Igc/igc.h:477,480</c>) and
/// <c>Mount = char</c> (<c>igc.h:841</c>). Unlike <see cref="ExpandedPartData"/>
/// (which lives inside a <c>#pragma pack(push,1)</c> block at
/// <c>igc.h:1673-1691</c> and is therefore 6 bytes), <c>PassengerData</c> is
/// declared <b>outside</b> every pack region — <c>Messages.h:33</c> sits well
/// clear of the only two pack blocks in that file (<c>Messages.h:636-642</c>,
/// <c>917-925</c>), and the four in <c>igc.h</c> (<c>1673-1691</c>,
/// <c>2120-2486</c>) are likewise balanced and none spans this record — so the
/// default MSVC struct alignment applies:
/// <code>
///   offset 0: ShipID shipID    (short, align 2)
///   offset 2: Mount  turretID  (char,  align 1)
///   offset 3: 1 pad byte       (struct align 2 → sizeof rounds 3 up to 4)
///   = 4 bytes per entry
/// </code>
/// The blob the native <c>CFSShip::QueueLoadoutChange</c> builds is
/// <c>pslist-&gt;n() * sizeof(PassengerData)</c> (<c>src/FedSrv/fsship.cpp:715</c>),
/// so each passenger occupies 4 wire bytes (the trailing pad byte is undefined
/// garbage in native; this codec writes 0 for determinism and skips it on read).
/// </summary>
public readonly record struct PassengerData(short ShipId, byte TurretId)
{
    /// <summary>Wire width of one entry: native <c>sizeof(PassengerData) = 4</c>
    /// under default MSVC alignment (Messages.h:33 outside all pack regions).</summary>
    public const int Size = 4;

    /// <summary>Encode this entry at the writer's current position, in declaration
    /// order (ShipID, then the Mount char, then one deterministic pad byte to fill
    /// out the 4-byte native width). Advances the caller's <paramref name="w"/>
    /// by <see cref="Size"/> (pass by <c>ref</c> — the ref-struct-by-value trap the
    /// other sub-codecs document).</summary>
    public readonly void Write(ref WireWriter w)
    {
        w.WriteI16(ShipId);    // offset 0
        w.WriteByte(TurretId); // offset 2
        w.WriteByte(0);        // offset 3: native's undefined pad byte, written 0 for determinism
    }

    /// <summary>Read one entry, advancing the caller's reader by the full
    /// <see cref="Size"/> (4). The trailing pad byte is consumed so successive
    /// entries stay aligned, then discarded.</summary>
    public static PassengerData Read(ref WireReader r)
    {
        short shipId = r.ReadI16();   // offset 0
        byte turret = r.ReadByte();   // offset 2
        r.Advance(1);                 // offset 3: skip the pad byte (keeps the 4-byte stride)
        return new PassengerData(shipId, turret);
    }
}
