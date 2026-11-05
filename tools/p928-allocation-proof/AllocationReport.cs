using System.Globalization;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Allegiance.Tools.P928;

public static class AllocationReport
{
    public static void Write(string path)
    {
        var etlx = TraceLog.CreateFromEventPipeDataFile(path);
        using var log = new TraceLog(etlx);
        var totals = new Dictionary<(string Type, string Site), (long Bytes, int Samples)>();
        long sampled = 0;
        int events = 0;
        foreach (var ev in log.Events)
        {
            if (ev is not GCAllocationTickTraceData allocation) continue;
            long bytes = allocation.AllocationAmount64;
            sampled += bytes;
            events++;
            string site = "[unresolved]";
            for (var frame = ev.CallStack(); frame is not null; frame = frame.Caller)
            {
                var method = frame.CodeAddress.FullMethodName;
                if (method.StartsWith("Allegiance.", StringComparison.Ordinal) ||
                    method.Contains("ClashBench", StringComparison.Ordinal))
                { site = method; break; }
            }
            var key = (allocation.TypeName, site);
            var old = totals.GetValueOrDefault(key);
            totals[key] = (old.Bytes + bytes, old.Samples + 1);
        }
        Console.WriteLine($"# GCAllocationTick samples={events} sampled-bytes={sampled}; includes startup and warmup");
        Console.WriteLine("sampled-bytes\tsamples\ttype\tfirst-application-frame");
        foreach (var row in totals.OrderByDescending(p => p.Value.Bytes))
            Console.WriteLine(string.Join('\t', row.Value.Bytes.ToString(CultureInfo.InvariantCulture),
                row.Value.Samples, row.Key.Type, row.Key.Site));
    }
}
