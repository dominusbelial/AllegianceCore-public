using System.Buffers.Binary;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

public sealed class ScoreRosterDeltaTests
{
    private static ScoreRosterEntry Row(string name, short kills = 0) => new(name, 1, kills, 0, 3, 9f, 5f);

    [Fact]
    public void Sparse_roundtrip_preserves_all_columns_and_order_even_with_duplicate_names()
    {
        ScoreRosterEntry[] before = [Row("same"), Row("same"), Row("last")];
        ScoreRosterEntry[] after = [before[0], new("same", 2, 4, 5, 6, 7.25f, 8.5f, 9, 10, 11, 12, 13, 14), before[2]];
        var delta = ScoreRosterDelta.Between(before, after);
        Assert.Single(delta.Changes);
        Assert.Equal((ushort)1, delta.Changes[0].Index);
        var bytes = delta.Encode();
        Assert.Equal(70, bytes.Length);
        Assert.Equal(after, ScoreRosterDelta.Decode(bytes).ApplyTo(before));
    }

    [Fact]
    public void Joins_interior_leaves_truncation_and_empty_roster_roundtrip()
    {
        ScoreRosterEntry[] rows = [Row("A"), Row("B"), Row("C")];
        foreach (ScoreRosterEntry[] next in new ScoreRosterEntry[][]
            { [rows[0], rows[2]], [rows[0], Row("D"), Row("E")], [Row("F")], [], [Row("G")] })
        {
            rows = ScoreRosterDelta.Decode(ScoreRosterDelta.Between(rows, next).Encode()).ApplyTo(rows);
            Assert.Equal(next, rows);
        }
    }

    [Theory]
    [InlineData(0, 0)] // cbmsg
    [InlineData(2, 209)] // wrong fmid
    [InlineData(4, 65535)] // excessive base count
    [InlineData(6, 65535)] // excessive result count
    [InlineData(8, 2)] // change count vs length
    [InlineData(10, 1)] // reserved bits
    [InlineData(12, 1)] // index outside result
    public void Decoder_rejects_invalid_headers_counts_and_indices(int offset, ushort value)
    {
        var bytes = ScoreRosterDelta.Between([Row("A")], [Row("A", 1)]).Encode();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
        Assert.Throws<ArgumentException>(() => ScoreRosterDelta.Decode(bytes));
    }

    [Fact]
    public void Decoder_rejects_every_truncation_and_trailing_data()
    {
        var bytes = ScoreRosterDelta.Between([], [Row("A"), Row("B")]).Encode();
        for (int i = 0; i < bytes.Length; i++)
            Assert.Throws<ArgumentException>(() => ScoreRosterDelta.Decode(bytes.AsSpan(0, i)));
        Assert.Throws<ArgumentException>(() => ScoreRosterDelta.Decode([.. bytes, 0]));
    }

    [Fact]
    public void Codec_rejects_duplicate_indices_missing_appends_and_message_overflow()
    {
        var bytes = ScoreRosterDelta.Between([], [Row("A"), Row("B")]).Encode();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12 + ScoreRosterDelta.ChangeSize), 0);
        Assert.Throws<ArgumentException>(() => ScoreRosterDelta.Decode(bytes));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12 + ScoreRosterDelta.ChangeSize), 0);
        Assert.Throws<ArgumentException>(() => ScoreRosterDelta.Decode(bytes));
        var missing = ScoreRosterDelta.Between([], [Row("A"), Row("B")]);
        missing.Changes = [missing.Changes[0]];
        Assert.Throws<ArgumentException>(() => missing.Encode());
        var many = Enumerable.Repeat(Row("A"), ScoreRosterDelta.MaxEntries).ToArray();
        Assert.Throws<ArgumentException>(() => ScoreRosterDelta.Between([], many).Encode());
        Assert.Throws<ArgumentException>(() => new ScoreRosterUpdate { Entries = [.. many, Row("B")] }.Encode());
        Assert.Equal(many, ScoreRosterUpdate.Decode(new ScoreRosterUpdate { Entries = many }.Encode()).Entries);
    }

    [Fact]
    public void Full_decoder_rejects_header_overlap_stride_out_of_range_and_unclaimed_bytes()
    {
        var original = new ScoreRosterUpdate { Entries = [Row("A")] }.Encode();
        foreach (var (offset, value) in new (int, ushort)[] { (4, 0), (4, 7), (4, 9), (6, 0), (6, 55), (6, 65535) })
        {
            var bytes = original.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
            Assert.Throws<ArgumentException>(() => ScoreRosterUpdate.Decode(bytes));
        }
    }
}
