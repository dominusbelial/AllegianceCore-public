using System.Diagnostics;
using System.Globalization;
using System.Runtime;

namespace Allegiance.ClientCore.MonoGame.Diagnostics;

public sealed class FlightGcLatency : IDisposable
{
    private readonly bool _enabled;
    private readonly GCLatencyMode _original;
    private bool _active;
    public FlightGcLatency(bool enabled) { _enabled = enabled; _original = GCSettings.LatencyMode; }
    public void SetFlying(bool flying)
    {
        if (!_enabled || flying == _active) return;
        GCSettings.LatencyMode = flying ? GCLatencyMode.SustainedLowLatency : _original;
        _active = flying;
    }
    public void Dispose() { if (_active) GCSettings.LatencyMode = _original; _active = false; }
}

/// <summary>Opt-in fixed storage, frame-ID GPU correlation and delayed CSV output.
/// Allocated bytes include all managed threads; timing includes present separately.</summary>
public sealed class ClientFrameTelemetry : IDisposable
{
    public event Action<long, double, double, double?>? SampleCompleted;
    private struct Sample
    {
        public long Id;
        public double Frame, Update, Draw, Post, Present, Pause, SceneGpu, PostGpu;
        public long Allocated;
        public long Draws;
        public int W, H, Rw, Rh;
    }
    private readonly Sample[] _samples = new Sample[4096];
    private readonly double[] _sort = new double[4096];
    private readonly StreamWriter? _csv;
    private long _frameStart = Stopwatch.GetTimestamp(), _lastAlloc = GC.GetTotalAllocatedBytes(false);
    private TimeSpan _lastPause = GC.GetTotalPauseDuration();
    private long _reportStart = Stopwatch.GetTimestamp(), _reportedId;
    private int _g0 = GC.CollectionCount(0), _g1 = GC.CollectionCount(1), _g2 = GC.CollectionCount(2);
    private double _update, _draw, _post, _present;
    private long _drawStart, _phaseStart;
    public long FrameId { get; private set; }
    public string Overlay { get; private set; } = "P643 collecting...";
    public double LatestGpuMs { get; private set; } = double.NaN;
    public long GpuGeneration { get; private set; }
    public bool GpuAvailable { get; set; }
    public ClientFrameTelemetry(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _csv = new(path) { AutoFlush = true };
            _csv.WriteLine("frame,frame_ms,update_ms,draw_submit_ms,post_submit_ms,present_wait_ms,scene_gpu_ms,post_gpu_ms,gc_pause_ms,allocated_bytes,draw_calls,width,height,render_width,render_height");
        }
        Console.WriteLine($"[p643] telemetry enabled; serverGC={GCSettings.IsServerGC}; latency={GCSettings.LatencyMode}");
    }
    public readonly struct UpdateScope : IDisposable
    {
        private readonly ClientFrameTelemetry? _owner;
        private readonly long _start;
        public UpdateScope(ClientFrameTelemetry? owner) { _owner = owner; _start = owner is null ? 0 : Stopwatch.GetTimestamp(); }
        public void Dispose() { if (_owner is not null) _owner._update += Stopwatch.GetElapsedTime(_start).TotalMilliseconds; }
    }
    public void BeginDraw()
    {
        if (FrameId == 0)
        {
            // Loading has a different allocation/timing budget from rendered frames.
            _frameStart = _reportStart = Stopwatch.GetTimestamp();
            _lastAlloc = GC.GetTotalAllocatedBytes(false); _lastPause = GC.GetTotalPauseDuration();
            _g0 = GC.CollectionCount(0); _g1 = GC.CollectionCount(1); _g2 = GC.CollectionCount(2);
        }
        FrameId++; _drawStart = Stopwatch.GetTimestamp(); _post = 0;
    }
    public void BeginPost() { _phaseStart = Stopwatch.GetTimestamp(); }
    public void EndPost() { _post += Stopwatch.GetElapsedTime(_phaseStart).TotalMilliseconds; }
    public void Submitted() { _draw = Stopwatch.GetElapsedTime(_drawStart).TotalMilliseconds - _post; }
    public void EndDraw(double presentMs, long draws, int w, int h, int rw, int rh)
    {
        _present = presentMs;
        long now = Stopwatch.GetTimestamp(), allocated = GC.GetTotalAllocatedBytes(false);
        var pause = GC.GetTotalPauseDuration();
        ref var s = ref _samples[FrameId % _samples.Length];
        s = new() { Id = FrameId, Frame = Stopwatch.GetElapsedTime(_frameStart, now).TotalMilliseconds,
            Update = _update, Draw = _draw, Post = _post, Present = _present,
            Pause = (pause - _lastPause).TotalMilliseconds, Allocated = allocated - _lastAlloc,
            SceneGpu = double.NaN, PostGpu = double.NaN, Draws = draws, W = w, H = h, Rw = rw, Rh = rh };
        _frameStart = now; _lastAlloc = allocated; _lastPause = pause; _update = 0;
        // Allow 16 frames for asynchronous query completion before CSV serialization.
        if (FrameId > 16) WriteCsv(_samples[(FrameId - 16) % _samples.Length]);
        if (Stopwatch.GetElapsedTime(_reportStart, now).TotalSeconds >= 1) Report(now);
    }
    public void ReceiveGpu(long id, bool post, double ms)
    {
        ref var s = ref _samples[id % _samples.Length];
        if (s.Id != id) return;
        if (post) s.PostGpu = ms; else s.SceneGpu = ms;
        if (double.IsFinite(s.SceneGpu) && double.IsFinite(s.PostGpu))
        { LatestGpuMs = s.SceneGpu + s.PostGpu; GpuGeneration++; }
    }
    private void WriteCsv(Sample s)
    {
        if (s.Id == 0) return;
        SampleCompleted?.Invoke(s.Id, s.Frame, s.Update + s.Draw + s.Post,
            double.IsFinite(s.SceneGpu + s.PostGpu) ? s.SceneGpu + s.PostGpu : null);
        if (_csv is null) return;
        _csv.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{s.Id},{s.Frame:F4},{s.Update:F4},{s.Draw:F4},{s.Post:F4},{s.Present:F4},{s.SceneGpu:F4},{s.PostGpu:F4},{s.Pause:F4},{s.Allocated},{s.Draws},{s.W},{s.H},{s.Rw},{s.Rh}"));
    }
    private void Report(long now)
    {
        long last = FrameId - 16;
        long first = Math.Max(_reportedId + 1, last - _samples.Length + 1);
        int count = (int)(last - first + 1);
        if (count <= 0) return;
        double pause = 0, alloc = 0; Sample worst = default;
        for (int i = 0; i < count; i++)
        {
            var s = _samples[(first + i) % _samples.Length]; _sort[i] = s.Frame;
            pause += s.Pause; alloc += s.Allocated;
            if (s.Frame >= worst.Frame) worst = s;
        }
        Array.Sort(_sort, 0, count);
        double p99 = _sort[Math.Min(count - 1, (int)(count * .99))];
        Sample tail = default;
        double p99Distance = double.MaxValue;
        for (long id = first; id <= last; id++)
        {
            var candidate = _samples[id % _samples.Length];
            double distance = Math.Abs(candidate.Frame - p99);
            if (distance < p99Distance) { tail = candidate; p99Distance = distance; }
        }
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        Overlay = $"p50 {_sort[count / 2]:F2} p99 {_sort[Math.Min(count - 1, (int)(count * .99))]:F2} max {worst.Frame:F2} ms | CPU u/d/p {worst.Update:F2}/{worst.Draw:F2}/{worst.Post:F2}\n"
            + $"GPU {(GpuAvailable ? $"{LatestGpuMs:F2} ms (async scene+post)" : "unavailable; present wait is NOT GPU time")} | present {worst.Present:F2} ms | draws {worst.Draws} {worst.Rw}x{worst.Rh}->{worst.W}x{worst.H}\n"
            + $"GC gen0/1/2 {g0-_g0}/{g1-_g1}/{g2-_g2} pause {pause:F2} ms | {alloc / count:F0} B/frame | heap {GC.GetTotalMemory(false)/1048576.0:F1} MiB\n"
            + $"p99 #{tail.Id}: GC {tail.Pause:F2} GPU {tail.SceneGpu+tail.PostGpu:F2} | max #{worst.Id}: GC {worst.Pause:F2} GPU {worst.SceneGpu+worst.PostGpu:F2} ms";
        Console.WriteLine("[p643] " + Overlay.Replace('\n', ' '));
        _g0=g0; _g1=g1; _g2=g2; _reportedId=last; _reportStart=now;
    }
    public void Dispose()
    {
        for (long id = Math.Max(1, FrameId - 15); id <= FrameId; id++) WriteCsv(_samples[id % _samples.Length]);
        _csv?.Dispose();
    }
}
