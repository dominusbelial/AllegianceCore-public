using System.Buffers.Binary;
using System.Diagnostics;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Xunit.Abstractions;

namespace Allegiance.Core.Tests.Transport;

public sealed class P661EgressAccountingTests(ITestOutputHelper output)
{
    internal static byte[] Frame(params (ushort Fmid, int Size)[] mix)
    {
        var messages = mix.Select(m =>
        {
            var bytes = new byte[m.Size];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)m.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), m.Fmid);
            return bytes;
        }).ToArray();
        var packet = FedPacket.Pack(messages.AsSpan());
        var frame = new byte[5 + packet.Length];
        frame[0] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), (uint)packet.Length);
        packet.CopyTo(frame, 5);
        return frame;
    }

    [Fact]
    public void Known_mix_splits_types_lanes_peers_and_counts_discarded_frames_once_per_type()
    {
        var accounting = new EgressAccounting();
        var ordered = Frame((2, 16), (26, 100));
        var stream = Frame((175, 12), (176, 36), (175, 8));
        accounting.Record(ordered, false, false, EgressEvent.Offered);
        accounting.Record(ordered, false, false, EgressEvent.Sent);
        accounting.Record(stream, true, true, EgressEvent.Offered);
        accounting.Record(stream, true, true, EgressEvent.Coalesced);
        accounting.Record(stream, true, true, EgressEvent.Dropped);
        accounting.Record(stream, false, true, EgressEvent.Sent); // human stream
        accounting.Record(ordered, true, false, EgressEvent.Sent); // harness ordered

        var window = accounting.DrainWindow();
        Assert.Equal(ordered.Length + stream.Length, window.Rows.Sum(r => r.OfferedBytes));
        Assert.Equal(2 * ordered.Length + stream.Length, window.Rows.Sum(r => r.SentBytes));
        Assert.Equal(stream.Length, window.Rows.Sum(r => r.CoalescedBytes));
        Assert.Equal(stream.Length, window.Rows.Sum(r => r.DroppedBytes));
        var light = Assert.Single(window.Rows, r => r.Fmid == 175 && r.PeerKind == "harness");
        Assert.Equal("stream", light.Lane);
        Assert.Equal(20, light.OfferedBytes);
        Assert.Equal(2, light.OfferedMessages);
        Assert.Equal(2, light.CoalescedMessages);
        Assert.Equal(1, light.CoalescedFrames);
        Assert.Equal(2, light.DroppedMessages);
        Assert.Equal(1, light.DroppedFrames);
        Assert.Equal(0, light.SentBytes);
        Assert.Equal(100, Assert.Single(window.Rows, r => r.Fmid == 26 && r.PeerKind == "human").SentBytes);
        Assert.Equal(4, window.Rows.Count(r => r.Fmid == EgressAccounting.OverheadFmid));
        Assert.Empty(accounting.DrainWindow().Rows);
    }

    [Fact]
    public void Partial_writes_count_only_accepted_bytes_and_count_messages_at_completion()
    {
        var accounting = new EgressAccounting();
        var frame = Frame((78, 16), (176, 36));
        accounting.Record(frame, true, true, EgressEvent.Sent, 0, 10); // header + 5 message bytes
        var first = accounting.DrainWindow();
        Assert.Equal(10, first.Rows.Sum(r => r.SentBytes));
        Assert.Equal(5, Assert.Single(first.Rows, r => r.Fmid == 78).SentBytes);
        Assert.Equal(0, first.Rows.Sum(r => r.SentMessages));
        accounting.Record(frame, true, true, EgressEvent.Sent, 10, 12); // complete 78, start 176
        var second = accounting.DrainWindow();
        Assert.Equal(12, second.Rows.Sum(r => r.SentBytes));
        Assert.Equal(1, Assert.Single(second.Rows, r => r.Fmid == 78).SentMessages);
        Assert.Equal(0, Assert.Single(second.Rows, r => r.Fmid == 176).SentMessages);
        accounting.Record(frame, true, true, EgressEvent.Sent, 22, frame.Length - 22);
        var last = accounting.DrainWindow();
        Assert.Equal(frame.Length - 22, last.Rows.Sum(r => r.SentBytes));
        Assert.Equal(1, last.Rows.Sum(r => r.SentMessages));
    }

    [Fact]
    public void Full_ushort_range_empty_and_malformed_payloads_remain_diagnostic_only()
    {
        var accounting = new EgressAccounting();
        var largest = Frame((65535, 4));
        accounting.Record(largest, false, false, EgressEvent.Sent);
        var malformed = Frame((2, 10));
        malformed[5] = 0; malformed[6] = 0;
        accounting.Record(malformed, false, false, EgressEvent.Sent);
        var empty = Frame();
        accounting.Record(empty, false, false, EgressEvent.Sent);
        accounting.Record(new byte[] { 1, 0, 0, 0, 0 }, false, false, EgressEvent.Sent); // greeting excluded
        var window = accounting.DrainWindow();
        Assert.Equal(4, Assert.Single(window.Rows, r => r.Fmid == 65535).SentBytes);
        Assert.Equal(1, window.Rows.Sum(r => r.SentMessages));
        Assert.Equal(largest.Length + malformed.Length + empty.Length, window.Rows.Sum(r => r.SentBytes));
    }

    [Fact]
    public async Task Concurrent_writers_and_window_drains_conserve_all_counters()
    {
        var accounting = new EgressAccounting();
        var frame = Frame((176, 36));
        const int perWriter = 10_000;
        long bytes = 0, messages = 0;
        var writers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < perWriter; i++) accounting.Record(frame, true, true, EgressEvent.Sent);
        })).ToArray();
        do
        {
            var window = accounting.DrainWindow();
            bytes += window.Rows.Sum(r => r.SentBytes);
            messages += window.Rows.Sum(r => r.SentMessages);
            Thread.Yield();
        } while (writers.Any(w => !w.IsCompleted));
        await Task.WhenAll(writers);
        var final = accounting.DrainWindow();
        bytes += final.Rows.Sum(r => r.SentBytes);
        messages += final.Rows.Sum(r => r.SentMessages);
        Assert.Equal(4L * perWriter * frame.Length, bytes);
        Assert.Equal(4L * perWriter, messages);
    }

    [Fact]
    public void Hot_path_allocates_zero_bytes_for_all_events()
    {
        var accounting = new EgressAccounting();
        var frame = Frame((175, 12), (176, 36), (175, 8));
        for (int i = 0; i < 20_000; i++) RecordAll(accounting, frame);
        // Best of three windows: a one-off runtime allocation (tiered JIT, seen on the CI runner
        // and in TEST_FLAKES.md) must not fail the test; a real per-event allocation shows in every window.
        const int iterations = 100_000;
        long allocated = long.MaxValue, elapsed = 0;
        for (int window = 0; window < 3 && allocated != 0; window++)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            long ticks = Stopwatch.GetTimestamp();
            for (int i = 0; i < iterations; i++) RecordAll(accounting, frame);
            elapsed = Stopwatch.GetTimestamp() - ticks;
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - start);
        }
        Assert.Equal(0, allocated);
        double ns = elapsed * (1e9 / Stopwatch.Frequency) / (4 * iterations);
        output.WriteLine($"P661 accounting: allocatedBytes={allocated}, mixed3MessageEventNs={ns:0.0}, events={4 * iterations}");
    }

    private static void RecordAll(EgressAccounting accounting, byte[] frame)
    {
        accounting.Record(frame, true, true, EgressEvent.Offered);
        accounting.Record(frame, true, true, EgressEvent.Sent);
        accounting.Record(frame, true, true, EgressEvent.Coalesced);
        accounting.Record(frame, true, true, EgressEvent.Dropped);
    }

    [Fact]
    public void Summary_ranks_top_ten_and_uses_measured_wall_seconds()
    {
        var rows = Enumerable.Range(1, 12).Select(id => new EgressRow(id, "harness", "stream",
            id * 200, id * 2, id * 100, id, 0, 0, 0, 0, 0, 0)).ToArray();
        var window = new EgressWindow(DateTimeOffset.UtcNow, 2, rows);
        string line = Server.Server.FormatEgressSummary(window);
        Assert.StartsWith("[egress-by-type] windowSecs=2.000 offeredBytes=15600 sentBytes=7800", line);
        Assert.Contains("topSent=[12:fmid bytes=1200 pct=15.4 msgs/s=6.0 avg=100.0", line);
        Assert.DoesNotContain(";2:", line);
        Assert.DoesNotContain(";1:", line);
        Assert.Contains("harness/stream=7800", line);
    }
}
