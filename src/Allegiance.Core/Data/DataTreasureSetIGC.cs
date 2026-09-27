using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>PowerupCode</c> from <c>src/Igc/igc.h:2094-2098</c> — the bit flags a
/// <c>c_tcPowerup</c> treasure's <c>treasureID</c> bitmap carries; a ship that
/// picks one up applies every set flag (the <c>c_tcPowerup</c> case of
/// <c>CshipIGC::HitTreasure</c>, <c>shipIGC.h:1326-1346</c>).</summary>
[Flags]
public enum PowerupCode : int
{
    Hull = 0x01,     // c_pcHull — SetFraction(1.0f)
    Shield = 0x02,   // c_pcShield — shield SetFraction(1.0f)
    Energy = 0x04,   // c_pcEnergy — SetEnergy(GetMaxEnergy())
    Fuel = 0x08,     // c_pcFuel — SetFuel(FLT_MAX) (clamped to max)
    Ammo = 0x10,     // c_pcAmmo — SetAmmo(0x7fff) (clamped to max)
}

/// <summary>
/// <c>TreasureCode</c> from <c>src/Igc/igc.h:2086-2091</c> — the tag byte in a
/// treasure entry that selects how <c>treasureID</c> is interpreted.</summary>
public enum TreasureCode : byte
{
    Part = 1,        // c_tcPart — treasureID is a PartID
    Powerup = 2,     // c_tcPowerup — treasureID is a PowerupCode bitmap
    Development = 3, // c_tcDevelopment — treasureID is a DevelopmentID (absent from corpus)
    Cash = 4,        // c_tcCash — treasureID is the dollar amount
    Flag = 5,        // c_tcFlag (absent from corpus)
}

/// <summary>
/// <c>DataTreasureSetIGC</c> payload for <c>OT_treasureSet</c> (28). Variable
/// length: a head (carrying a count the writer contradicts) followed by
/// <c>nTreasureData</c> four-byte <see cref="TreasureData"/> entries.
/// Source: <c>src/Igc/igc.h:2100-2118</c>, C++ default alignment. See wiki page
/// <c>allegiance-igc-file-format</c> §OT_treasureSet (⭐ corrections section —
/// the recommended reader is reproduced below).
/// </summary>
/// <remarks>
/// <para><b>Writer generations.</b> The wiki splits the 99 corpus records into
/// two generations by provenance. The reader here deliberately does NOT branch
/// on generation: for all 99 records it derives the entry count from the record
/// size and ignores the header count, which is the wiki's recommended approach.</para>
/// <para><b>Layout (corpus-measured):</b> <c>name</c>[25]@0, <c>treasureSetID</c>
/// (short)@25, a two-byte <c>count</c> field @27 that the reader does not trust,
/// <c>bZoneOnly</c> (byte)@29, two pad bytes to reach the 32-byte head, then
/// <c>nTreasureData = (size − 32) / 4</c> four-byte entries at offset 32.</para>
/// <para><b>Entry:</b> <c>treasureID</c> (ushort) + <c>treasureCode</c> (byte) +
/// <c>chance</c> (byte) — 4 bytes. This is an <i>older</i> two-byte-id form of
/// <c>TreasureData</c>: today's <c>igc.h:2100</c> declares <c>ObjectID
/// treasureID</c> (4 bytes) + code + chance = 6→8 bytes. Validation: every
/// <c>Part</c>-code <c>treasureID</c> resolves to a real part in the same core
/// (1693/1693), every <c>Powerup</c> code is a valid PowerupCode bitmap
/// (96/96), and <c>Cash</c> <c>treasureID</c>s are dollar amounts
/// (500…10000, 96/96).</para>
/// <para><b>Header count caveat (deliberately unexplained).</b> The two-byte
/// count at offset 27 does <b>not</b> literally equal the entry count. Across
/// all 99 records it reliably reads <c>256 × entries</c> — including the
/// "generation A" files the wiki marks struct-accurate, not only the
/// generation-B "garbage" the wiki calls out. Because the reader must not
/// depend on a header field the writer contradicts, this observation is
/// recorded here and left unsolved (an honest workaround beats a confident
/// wrong theory).</para>
/// </remarks>
public readonly record struct DataTreasureSetIGC(
    string Name,
    short TreasureSetId,
    bool BZoneOnly,
    DataTreasureSetIGC.TreasureData[] Entries)
{
    /// <summary>Head size in bytes: the fixed part before the entries.</summary>
    public const int HeadSize = 32;
    /// <summary>Each entry occupies 4 bytes on disk (treasureID ushort + code + chance).</summary>
    public const int EntryStride = 4;

    /// <summary>A single <c>TreasureData</c> entry, four bytes on disk.</summary>
    public readonly record struct TreasureData(
        ushort TreasureId,
        TreasureCode Code,
        byte Chance);

    /// <summary>
    /// Decode an <c>OT_treasureSet</c> payload.
    /// </summary>
    /// <param name="b">Payload bytes; entry count is derived from
    /// <c>(length − <see cref="HeadSize"/>) / <see cref="EntryStride"/></c>.</param>
    /// <exception cref="InvalidDataException">Payload too small or not of the
    /// derived-entry form.</exception>
    public static DataTreasureSetIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < HeadSize)
            throw new InvalidDataException(
                $"DataTreasureSetIGC needs at least {HeadSize} bytes, got {b.Length}");
        var n = (b.Length - HeadSize) / EntryStride;
        if (n < 0 || n * EntryStride + HeadSize != b.Length)
            throw new InvalidDataException(
                $"DataTreasureSetIGC: {b.Length} bytes is not HeadSize + 4·n");

        var entries = new TreasureData[n];
        for (var i = 0; i < n; i++)
        {
            var e = b.Slice(HeadSize + EntryStride * i, EntryStride);
            entries[i] = new TreasureData(
                TreasureId: BinaryPrimitives.ReadUInt16LittleEndian(e[0..]),
                Code: (TreasureCode)e[2],
                Chance: e[3]);
        }

        return new DataTreasureSetIGC(
            Name: DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(b[..25])),
            TreasureSetId: BinaryPrimitives.ReadInt16LittleEndian(b[25..]),
            BZoneOnly: b[29] != 0,
            Entries: entries);
    }
}
