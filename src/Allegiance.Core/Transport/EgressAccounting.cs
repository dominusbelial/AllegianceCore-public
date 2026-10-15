using System.Buffers.Binary;
using System.Diagnostics;

namespace Allegiance.Core.Transport;

public enum EgressEvent { Offered, Sent, Coalesced, Dropped }

/// <summary>P661: host-only fixed storage, indexed by (peer kind, lane, fmid).
/// No decoding, CRC recomputation, allocation, or new locks on the send path.
/// Bytes include FEDMESSAGE headers; framing/CRC/unparsed bytes have a separate row.</summary>
public sealed class EgressAccounting
{
    public const int OverheadFmid = 65536;
    private const int Types = OverheadFmid + 1;
    private readonly Counters[] _counters = new Counters[4 * Types];
    private int _maxFmid;
    private long _windowStart = Stopwatch.GetTimestamp();

    private struct Counters
    {
        public long OfferedBytes, OfferedMessages, SentBytes, SentMessages;
        public long CoalescedBytes, CoalescedMessages, CoalescedFrames;
        public long DroppedBytes, DroppedMessages, DroppedFrames;
    }

    /// <summary>Record an outer Data frame. For Sent, offset/count describe only
    /// bytes accepted by the socket this time, including partial writes. A message
    /// is counted once when its last byte is accepted. Other events cover a whole
    /// frame. The caller already owns the frame; this method retains nothing.</summary>
    internal void Record(ReadOnlySpan<byte> frame, bool harness, bool stream,
        EgressEvent ev, int offset = 0, int count = -1)
    {
        if (frame.Length < 5 || frame[0] != 2) return; // greetings aren't egress
        if (count < 0) count = frame.Length;
        int end = offset + count;
        int group = (harness ? 2 : 0) + (stream ? 1 : 0);
        int messageEnd = Math.Max(5, frame.Length - 4);
        int pos = 5;
        int attributed = 0;
        while (messageEnd - pos >= 4)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(frame[pos..]);
            if (size < 4 || size > messageEnd - pos) break;
            int fmid = BinaryPrimitives.ReadUInt16LittleEndian(frame[(pos + 2)..]);
            int high = Volatile.Read(ref _maxFmid);
            while (fmid > high)
            {
                int previous = Interlocked.CompareExchange(ref _maxFmid, fmid, high);
                if (previous == high) break;
                high = previous;
            }
            int bytes = Math.Max(0, Math.Min(end, pos + size) - Math.Max(offset, pos));
            if (bytes > 0)
            {
                bool complete = end >= pos + size;
                bool firstOfType = true;
                if (ev is EgressEvent.Coalesced or EgressEvent.Dropped)
                {
                    // One discarded FRAME per distinct fmid, even if a batch
                    // contains several messages of that type. Discards are rare;
                    // this walk avoids a per-peer scratch array or HashSet.
                    for (int prior = 5; prior < pos;)
                    {
                        if (BinaryPrimitives.ReadUInt16LittleEndian(frame[(prior + 2)..]) == fmid)
                        { firstOfType = false; break; }
                        prior += BinaryPrimitives.ReadUInt16LittleEndian(frame[prior..]);
                    }
                }
                Add(ref _counters[group * Types + fmid], ev, bytes,
                    complete ? 1 : 0, firstOfType ? 1 : 0);
                attributed += bytes;
            }
            pos += size;
            if (pos >= end) break;
        }
        Add(ref _counters[group * Types + OverheadFmid], ev, count - attributed, 0, 0);
    }

    private static void Add(ref Counters c, EgressEvent ev, long bytes, long messages, long frames)
    {
        switch (ev)
        {
            case EgressEvent.Offered:
                Interlocked.Add(ref c.OfferedBytes, bytes);
                Interlocked.Add(ref c.OfferedMessages, messages);
                break;
            case EgressEvent.Sent:
                Interlocked.Add(ref c.SentBytes, bytes);
                Interlocked.Add(ref c.SentMessages, messages);
                break;
            case EgressEvent.Coalesced:
                Interlocked.Add(ref c.CoalescedBytes, bytes);
                Interlocked.Add(ref c.CoalescedMessages, messages);
                Interlocked.Add(ref c.CoalescedFrames, frames);
                break;
            case EgressEvent.Dropped:
                Interlocked.Add(ref c.DroppedBytes, bytes);
                Interlocked.Add(ref c.DroppedMessages, messages);
                Interlocked.Add(ref c.DroppedFrames, frames);
                break;
        }
    }

    /// <summary>Single report consumer only. Atomic exchanges conserve every
    /// counter across windows; a concurrent update may straddle a window boundary
    /// between its byte and message counters. Allocations happen only here.</summary>
    public EgressWindow DrainWindow()
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _windowStart) / (double)Stopwatch.Frequency;
        _windowStart = now;
        var rows = new List<EgressRow>();
        int max = Volatile.Read(ref _maxFmid);
        for (int group = 0; group < 4; group++)
        {
            for (int fmid = 0; fmid <= max; fmid++) DrainRow(group, fmid, rows);
            DrainRow(group, OverheadFmid, rows);
        }
        return new EgressWindow(DateTimeOffset.UtcNow, seconds, rows.ToArray());
    }

    private void DrainRow(int group, int fmid, List<EgressRow> rows)
    {
        ref var c = ref _counters[group * Types + fmid];
        var row = new EgressRow(fmid, group >= 2 ? "harness" : "human",
            (group & 1) != 0 ? "stream" : "ordered",
            Interlocked.Exchange(ref c.OfferedBytes, 0), Interlocked.Exchange(ref c.OfferedMessages, 0),
            Interlocked.Exchange(ref c.SentBytes, 0), Interlocked.Exchange(ref c.SentMessages, 0),
            Interlocked.Exchange(ref c.CoalescedBytes, 0), Interlocked.Exchange(ref c.CoalescedMessages, 0),
            Interlocked.Exchange(ref c.CoalescedFrames, 0), Interlocked.Exchange(ref c.DroppedBytes, 0),
            Interlocked.Exchange(ref c.DroppedMessages, 0), Interlocked.Exchange(ref c.DroppedFrames, 0));
        if (row.OfferedBytes != 0 || row.OfferedMessages != 0 || row.SentBytes != 0 || row.SentMessages != 0
            || row.CoalescedBytes != 0 || row.CoalescedMessages != 0 || row.CoalescedFrames != 0
            || row.DroppedBytes != 0 || row.DroppedMessages != 0 || row.DroppedFrames != 0) rows.Add(row);
    }
}

public sealed record EgressWindow(DateTimeOffset WindowEndUtc, double WindowSeconds, EgressRow[] Rows);

public readonly record struct EgressRow(int Fmid, string PeerKind, string Lane,
    long OfferedBytes, long OfferedMessages, long SentBytes, long SentMessages,
    long CoalescedBytes, long CoalescedMessages, long CoalescedFrames,
    long DroppedBytes, long DroppedMessages, long DroppedFrames);
