using System.Buffers.Binary;

namespace Allegiance.Core.Wire;

/// <summary>
/// P672, port-only fmid 219. Ordered, reliable patch of the last roster (209 or
/// 219). Indices preserve the full snapshot's order and allow duplicate names.
/// Header: cbmsg/fmid, ushort baseCount, ushort resultCount, ushort changeCount,
/// ushort reserved(0), then changeCount records of ushort index + 56-byte row.
/// Truncation removes trailing rows; shifted rows represent interior departures.
/// Every appended index must be present. Full 209 snapshots heal missed patches.
/// </summary>
public sealed class ScoreRosterDelta : IFedMessage
{
    public const ushort Fmid = 219;
    public const int FixedSize = 12;
    public const int ChangeSize = 2 + ScoreRosterEntry.Size;
    public const int MaxEntries = (ushort.MaxValue - ScoreRosterUpdate.FixedSize) / ScoreRosterEntry.Size;

    public ushort BaseCount;
    public ushort ResultCount;
    public (ushort Index, ScoreRosterEntry Entry)[] Changes = [];
    public ushort GetFmid() => Fmid;

    public byte[] Encode()
    {
        Validate();
        int size = FixedSize + Changes.Length * ChangeSize;
        if (size > ushort.MaxValue) throw new ArgumentException("Score roster delta exceeds cbmsg");
        var bytes = new byte[size];
        var w = new WireWriter(bytes);
        w.WriteU16((ushort)size); w.WriteU16(Fmid);
        w.WriteU16(BaseCount); w.WriteU16(ResultCount);
        w.WriteU16((ushort)Changes.Length); w.WriteU16(0);
        foreach (var (index, entry) in Changes) { w.WriteU16(index); entry.Write(ref w); }
        return bytes;
    }

    private void Validate()
    {
        if (BaseCount > MaxEntries || ResultCount > MaxEntries || Changes.Length > ResultCount)
            throw new ArgumentException("Score roster delta count out of bounds");
        int previous = -1, appended = 0;
        foreach (var (index, _) in Changes)
        {
            if (index <= previous || index >= ResultCount)
                throw new ArgumentException("Score roster delta indices must be increasing and in bounds");
            if (index >= BaseCount) appended++;
            previous = index;
        }
        if (appended != Math.Max(0, ResultCount - BaseCount))
            throw new ArgumentException("Score roster delta omits appended rows");
    }

    public static ScoreRosterDelta Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < FixedSize) throw new ArgumentException("Score roster delta header truncated");
        var h = FedMessageHeader.Read(bytes);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        if (h.Fmid != Fmid || h.CbMsg != bytes.Length || bytes.Length != FixedSize + count * ChangeSize
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]) != 0)
            throw new ArgumentException("Invalid score roster delta header/count/length");
        var delta = new ScoreRosterDelta
        {
            BaseCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]),
            ResultCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]),
            Changes = new (ushort, ScoreRosterEntry)[count],
        };
        for (int i = 0, offset = FixedSize; i < count; i++, offset += ChangeSize)
            delta.Changes[i] = (BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]),
                ScoreRosterEntry.Read(bytes.Slice(offset + 2, ScoreRosterEntry.Size)));
        delta.Validate();
        return delta;
    }

    /// <summary>Ignore a patch with a missing baseline; the next full refresh heals it.</summary>
    public ScoreRosterEntry[] ApplyTo(ScoreRosterEntry[] baseline)
    {
        Validate();
        if (baseline.Length != BaseCount) return baseline;
        var result = new ScoreRosterEntry[ResultCount];
        Array.Copy(baseline, result, Math.Min(baseline.Length, result.Length));
        foreach (var (index, entry) in Changes) result[index] = entry;
        return result;
    }

    public static ScoreRosterDelta Between(ScoreRosterEntry[] baseline, ScoreRosterEntry[] current)
    {
        if (baseline.Length > MaxEntries || current.Length > MaxEntries)
            throw new ArgumentException("Score roster exceeds wire bounds");
        return new ScoreRosterDelta
        {
            BaseCount = (ushort)baseline.Length, ResultCount = (ushort)current.Length,
            Changes = current.Select((entry, index) => (Index: (ushort)index, Entry: entry))
                .Where(c => c.Index >= baseline.Length || baseline[c.Index] != c.Entry).ToArray(),
        };
    }
}
