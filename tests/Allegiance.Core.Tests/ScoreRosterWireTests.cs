using System.Buffers.Binary;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P265 — <c>ScoreRosterUpdate</c>(209), the periodic owner-only in-mission
/// scoreboard snapshot the server broadcasts so the renderer-only MonoGame
/// client can draw a live roster (name / side / kills / deaths from P193
/// PlayerScore). Pure wire-codec coverage lives here (round-trip + layout
/// pins); the live end-to-end proof (a real combat kill reflecting in the
/// periodic broadcast) is in <see cref="ScoreRosterLiveTests"/>.
/// </summary>
public sealed class ScoreRosterWireTests
{
    /// <summary>⭐ The codec round-trips its whole payload: a per-player list
    /// of <c>{characterName, sideId, kills, deaths}</c> is unchanged through
    /// <see cref="ScoreRosterUpdate.Encode"/>/<see cref="ScoreRosterUpdate.Decode"/>.</summary>
    [Fact]
    public void ScoreRosterUpdate_round_trips_payload()
    {
        var entries = new[]
        {
            new ScoreRosterEntry("AlphaAce", 0, 12, 3),
            new ScoreRosterEntry("BravoBomber", 1, 0, 7),
            new ScoreRosterEntry("long-name-0123456789abcdef", 1, 5, 1),  // 26 chars > 24 → truncated to 24 + NUL
        };
        var d = ScoreRosterUpdate.Decode(new ScoreRosterUpdate { Entries = entries }.Encode());

        Assert.Equal(ScoreRosterUpdate.Fmid, d.GetFmid());
        Assert.Equal(3, d.Entries.Length);
        Assert.Equal("AlphaAce", d.Entries[0].CharacterName);
        Assert.Equal((short)0, d.Entries[0].SideId);
        Assert.Equal((short)12, d.Entries[0].Kills);
        Assert.Equal((short)3, d.Entries[0].Deaths);
        Assert.Equal("BravoBomber", d.Entries[1].CharacterName);
        Assert.Equal((short)1, d.Entries[1].SideId);
        Assert.Equal((short)0, d.Entries[1].Kills);
        Assert.Equal((short)7, d.Entries[1].Deaths);
        Assert.Equal("long-name-0123456789abcd", d.Entries[2].CharacterName);  // 24 chars, NUL-terminated
        Assert.Equal((short)5, d.Entries[2].Kills);
        Assert.Equal((short)1, d.Entries[2].Deaths);
    }

    /// <summary>Pins the resolved record stride (32 bytes) and the fixed message
    /// region (8 bytes) with discrete offsets, so a layout regression names the
    /// exact drifted field. Record layout: char[25] @0, pad @25, short sideId
    /// @26, short kills @28, short deaths @30 → 32.</summary>
    [Fact]
    public void ScoreRosterUpdate_layout_pins_stride_and_offsets()
    {
        Assert.Equal(32, ScoreRosterEntry.Size);
        Assert.Equal(8, ScoreRosterUpdate.FixedSize);

        var msg = new ScoreRosterUpdate
        {
            Entries = [new ScoreRosterEntry("A", 905, 7811, 7833)],
        };
        byte[] b = msg.Encode();
        Assert.Equal(8 + 32, b.Length);

        Assert.Equal(b.Length, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(0, 2)));  // cbmsg
        Assert.Equal(209, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2, 2)));       // fmid
        Assert.Equal(8, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4, 2)));         // ib (blob at 8)
        Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6, 2)));        // cb = 1 × 32

        Assert.Equal((byte)'A', b[8]);        // characterName[0]
        Assert.Equal(0, b[9]);                // name NUL
        Assert.Equal(0, b[33]);               // pad @25 (record-relative) aligning sideId short to even
        Assert.Equal(905, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(8 + 26, 2)));   // sideId @26
        Assert.Equal(7811, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(8 + 28, 2)));  // kills @28
        Assert.Equal(7833, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(8 + 30, 2)));  // deaths @30
    }

    /// <summary>An empty roster encodes to a fixed 8-byte message (header + an
    /// absent var-item) and decodes back to an empty list — the pre-join
    /// baseline the client renders as an empty panel.</summary>
    [Fact]
    public void ScoreRosterUpdate_empty_list_encodes_fixed_header()
    {
        var d = ScoreRosterUpdate.Decode(new ScoreRosterUpdate { Entries = [] }.Encode());
        Assert.Empty(d.Entries);
    }
}
