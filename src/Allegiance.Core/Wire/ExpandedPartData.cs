namespace Allegiance.Core.Wire;

/// <summary>
/// <c>ExpandedPartData</c> (<c>src/Igc/igc.h:1687-1690</c>), the 6-byte
/// variable-loadout entry carried by both <c>FM_C_BUY_LOADOUT(4)</c> (the
/// client's requested loadout) and <c>FM_S_BUY_LOADOUT_ACK(5)</c> (the granted
/// one). It extends the 5-byte <c>PartData</c> base
/// (<c>igc.h:1674-1679</c>: <c>PartID partID; short amount; Mount mountID;</c>)
/// with a single <c>BytePercentage fractionMounted</c> tail member — so its
/// field list is:
/// <code>
///   offset 0: PartID      partID         (short; ObjectID = short, igc.h:477,479)
///   offset 2: short       amount
///   offset 4: Mount       mountID        (Mount = char, igc.h:841)
///   offset 5: BytePercentage fractionMounted  (1 byte, igc.h:1689;
///             BytePercentage stores a single unsigned char, _Utility/Utility.h:1307-1337)
///   = 6 bytes, matching the compiler-confirmed sizeof(ExpandedPartData)=6
/// </summary>
/// <remarks>
/// This is the wire <em>entry</em> only — the same 6-byte shape
/// <see cref="Allegiance.Core.Sim.Ship"/> persists per mounted part so the
/// server can echo a requested loadout back byte-for-byte. Distinct from the
/// smaller 5-byte <c>PartData</c> used by <c>ExportShip</c>/<c>NewShipInstance</c>
/// for the (separate, out-of-scope) observer-side EXPORT tail — this codec is
/// for the owning player's own BUY_LOADOUT/ACK round-trip only.
/// </remarks>
public readonly record struct ExpandedPartData(short PartId, short Amount, byte MountId, byte FractionMounted)
{
    /// <summary>Wire width of one entry: <c>sizeof(ExpandedPartData)=6</c> (compiler-confirmed, P28).</summary>
    public const int Size = 6;

    /// <summary>Encode this entry at the writer's current position, in declaration order.</summary>
    /// <remarks>Must advance the caller's <paramref name="w"/> position (pass by
    /// <c>ref</c>) so the enclosing message's <c>cbmsg</c>/<c>cb</c> patch offsets
    /// reflect every written part — a by-value ref struct argument would only
    /// advance its copy and leave the shared buffer's data unwritten-at-the-right
    /// offset for the length accounting.</remarks>
    public readonly void Write(ref WireWriter w)
    {
        w.WriteI16(PartId);
        w.WriteI16(Amount);
        w.WriteByte(MountId);
        w.WriteByte(FractionMounted);
    }

    /// <summary>Read one entry from the reader's current position, in declaration order.</summary>
    /// <remarks>Pass the reader by <c>ref</c> so successive entries advance the
    /// caller's position (same ref-struct-by-value trap as the writer side).</remarks>
    public static ExpandedPartData Read(ref WireReader r) =>
        new(r.ReadI16(), r.ReadI16(), r.ReadByte(), r.ReadByte());

    /// <summary>A fully-mounted part's <c>fractionMounted</c> raw byte:
    /// <c>BytePercentage(1.0f).GetChar() = 1.0f * Divisor(240) = 240</c>
    /// (<c>_Utility/Utility.h:1311-1327</c>). Used when a caller wants a part
    /// recorded as already fully mounted on its slot.</summary>
    public const byte FullyMounted = 240;
}
