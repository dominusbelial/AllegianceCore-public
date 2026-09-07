namespace Allegiance.Core.Wire;

/// <summary>
/// <c>PartData</c> (<c>igc.h:1674-1679</c>), the 5-byte observer-facing part
/// entry carried by <c>FM_S_EXPORT</c>(26)'s ship variant (and
/// <c>NewShipInstance</c>/<c>FM_S_ADD_PART</c>(164), out of scope here) — the
/// base struct <see cref="ExpandedPartData"/> extends with a
/// <c>fractionMounted</c> tail for the owning player's own
/// <c>BUY_LOADOUT</c>/<c>ACK</c> round-trip (see that type's own doc comment,
/// which already documents and compiler-confirms this base layout via P28's
/// Windows-VM probe — no separate probe needed here). Field order and widths,
/// read directly off <c>CshipIGC::CreateAndAddPart(const PartData* ppd)</c>
/// (<c>shipIGC.h:1485-1488</c>, <c>ppd-&gt;partID, ppd-&gt;mountID, ppd-&gt;amount</c>)
/// and <c>igc.h:1674-1679</c>:
/// <code>
///   offset 0: PartID partID   (short; ObjectID = short, igc.h:477,479)
///   offset 2: short  amount
///   offset 4: Mount  mountID  (Mount = char, igc.h:841)
///   = 5 bytes total
/// </code>
/// This is the OBSERVER tail — unlike <see cref="ExpandedPartData"/> it never
/// carries <c>fractionMounted</c> (a requester-only round-trip concern), so a
/// part exported here to another peer is definitionally already mounted.
/// </summary>
public readonly record struct PartData(short PartId, short Amount, byte MountId)
{
    /// <summary>Wire width of one entry: 5 bytes (see the type doc comment).</summary>
    public const int Size = 5;

    /// <summary>Encode this entry at the writer's current position, in declaration order.</summary>
    /// <remarks>Pass <paramref name="w"/> by <c>ref</c> so the caller's shared
    /// buffer position actually advances (same ref-struct-by-value trap
    /// <see cref="ExpandedPartData.Write"/> documents).</remarks>
    public readonly void Write(ref WireWriter w)
    {
        w.WriteI16(PartId);
        w.WriteI16(Amount);
        w.WriteByte(MountId);
    }

    /// <summary>Read one entry from the reader's current position, in declaration order.</summary>
    public static PartData Read(ref WireReader r) =>
        new(r.ReadI16(), r.ReadI16(), r.ReadByte());
}
