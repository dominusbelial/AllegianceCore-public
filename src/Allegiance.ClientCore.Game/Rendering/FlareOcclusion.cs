using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>How the transit-flare occlusion test gets its scene depth.</summary>
public enum FlareOcclusionMode
{
    /// <summary>Do not test occlusion at all — the flare always draws (measurement floor).</summary>
    Off,

    /// <summary>P585 behaviour: read THIS frame's linear-depth target inside the
    /// frame, i.e. a synchronous GPU→CPU readback that waits for the pipeline.</summary>
    Blocking,

    /// <summary>P862b (default): answer from the depth target as it stood at the
    /// START of the frame — the PREVIOUS frame's depth — so the read is one frame
    /// behind instead of stalling the frame that is being drawn.</summary>
    Deferred,
}

/// <summary>One queued occlusion sample: the depth-target pixel and the aleph's
/// own normalized view depth, both from the frame that asked.</summary>
public readonly record struct FlareSample(short WarpId, int X, int Y, float AlephNorm);

/// <summary>
/// P862b — <b>the transit-flare occlusion test, without the pipeline stall.</b>
///
/// <para><b>The problem.</b> P585 hides the lens-flare burst when the aleph's
/// funnel centre is behind opaque geometry, by sampling the P547 linear-depth
/// target at that pixel (<c>DepthRT.GetData(...)</c>). That call is a synchronous
/// GPU→CPU readback: it has to wait until every command already submitted — this
/// frame's depth pass, the whole opaque scene — has finished, and it does so for
/// every flaring aleph, every frame of a transit pulse, i.e. exactly inside the
/// arrival window P862b is trying to clear.</para>
///
/// <para><b>The fix.</b> The depth target is only re-rendered at the start of a
/// frame, so at that moment it still holds the PREVIOUS frame's depth. Read it
/// there (one frame late), resolve every sample the last frame asked for in one
/// batched readback, and let the flare consult the stored verdict. The test is
/// still exact — the pixel and the depth it is compared against both come from
/// the same frame — the flare is simply decided one frame later, which is
/// invisible at 60 fps (a pulse lasts ~0.9 s) and nothing else depends on it.
/// The verdict defaults to "visible" when there is no trustworthy depth (soft
/// particles off, first frame, or the target is not readable), exactly as P585
/// treated a failed readback.</para>
///
/// <para><b>Measurement.</b> Every mode counts its samples and time, so the
/// report can quote what the blocking readback cost versus this one
/// (<c>ALLEGIANCE_FLARE_OCCLUSION=blocking|off|deferred</c> selects the arm with
/// the same binary).</para>
/// </summary>
public sealed class FlareOcclusionProbe
{
    /// <summary>Depth margin (normalized units) by which the scene must be in
    /// front of the aleph to count as occluding — the P585 inline constant.</summary>
    public const float OcclusionMargin = 0.005f;

    /// <summary>Largest batched readback (pixels). Up to
    /// <see cref="AlephGlimpseRenderer.MaxGlimpses"/> flares per frame are
    /// usually within a few dozen pixels of each other; when they are not, the
    /// samples are read one at a time instead of dragging back a whole screen.</summary>
    public const int MaxBatchPixels = 64 * 64;

    /// <summary>Hard cap on depth readbacks in ONE frame. Samples that share a small
    /// rectangle are covered by a single batched read; when they are spread across
    /// the screen only the first is read this frame (the others keep their previous
    /// verdict and re-queue at their next refresh). A pulse can show up to
    /// <see cref="AlephGlimpseRenderer.MaxGlimpses"/> flares at once and letting all
    /// of them read back in the same frame is what would turn a ~3 ms driver cost
    /// into an 18 ms frame.</summary>
    public const int MaxReadbacksPerFrame = 1;

    /// <summary>Cap on remembered per-warp verdicts (a map has far fewer alephs).</summary>
    public const int MaxVerdicts = 128;

    /// <summary>How often (in rendered frames) a flaring aleph's sample is
    /// REFRESHED. One depth readback costs ~3 ms on this workstation's GL driver
    /// (a per-call driver cost, not a decode), so reading once per flare per frame
    /// during a pulse would eat a sixth of the frame for one funnel; at 6 frames
    /// (~10 Hz at 60 fps) a 0.9 s pulse is re-tested ~9 times, which is more than
    /// enough for a flare that fades in and out over a second. The first sample of
    /// a pulse is taken immediately.</summary>
    public const int RefreshFrames = 6;

    /// <summary>After this many frames without a refresh the remembered verdict is
    /// discarded and the flare is treated as visible again (so a stale "occluded"
    /// can never hide a flare indefinitely).</summary>
    public const int MaxVerdictAgeFrames = 30;

    private readonly struct Verdict
    {
        public readonly bool Occluded;
        public readonly long Frame;
        public Verdict(bool occluded, long frame) { Occluded = occluded; Frame = frame; }
    }

    private readonly Func<Rectangle, Color[]?>? _readDepth;
    private readonly Dictionary<short, Verdict> _verdicts = new();
    private readonly List<FlareSample> _pending = new();
    private long _frame;
    private long _pendingFrame;

    private long _reportSamples;
    private double _reportMs;
    private double _reportWorstMs;
    private long _reportBatches;
    private double _nextReport;

    /// <param name="mode">Which arm to run (see <see cref="ParseMode"/>).</param>
    /// <param name="readDepth">Reads a rectangle of the linear-depth target back
    /// to the CPU. <c>null</c> (or a <c>null</c> result) means "no depth
    /// available": every flare is then treated as visible.</param>
    public FlareOcclusionProbe(FlareOcclusionMode mode, Func<Rectangle, Color[]?>? readDepth)
    {
        Mode = mode;
        _readDepth = readDepth;
    }

    /// <summary>The active arm.</summary>
    public FlareOcclusionMode Mode { get; }

    /// <summary>Read <c>ALLEGIANCE_FLARE_OCCLUSION</c>: <c>deferred</c> (default),
    /// <c>blocking</c> (the pre-P862b in-frame readback) or <c>off</c> (no test).
    /// Anything else keeps the default.</summary>
    public static FlareOcclusionMode ParseMode(string? raw)
    {
        if (string.Equals(raw, "blocking", StringComparison.OrdinalIgnoreCase)) return FlareOcclusionMode.Blocking;
        if (string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "0", StringComparison.Ordinal)
            || string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return FlareOcclusionMode.Off;
        return FlareOcclusionMode.Deferred;
    }

    /// <summary>Samples resolved (deferred) or read inline (blocking) so far.</summary>
    public long Samples { get; private set; }

    /// <summary>Readbacks issued so far (one per batch, or one per sample).</summary>
    public long Batches { get; private set; }

    /// <summary>Total milliseconds spent inside depth readbacks.</summary>
    public double TotalMs { get; private set; }

    /// <summary>Worst single readback, milliseconds.</summary>
    public double WorstMs { get; private set; }

    /// <summary>Samples asked for so far this frame.</summary>
    public int LastFrameSamples { get; private set; }

    /// <summary>Samples still queued for the start of the next frame.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Verdicts currently remembered (diagnostics).</summary>
    public int VerdictCount => _verdicts.Count;

    /// <summary>Start a rendered frame: bump the frame clock and reset the
    /// per-frame sample counter.</summary>
    public void BeginFrame()
    {
        _frame++;
        LastFrameSamples = 0;
    }

    /// <summary>
    /// Answer whether the flare at <paramref name="warpId"/> is hidden behind
    /// opaque geometry, and queue this frame's sample for the next readback.
    /// <paramref name="alephNorm"/> is the aleph's own view depth normalized the
    /// same way the depth pass normalizes (see <see cref="LinearDepth"/>).
    /// </summary>
    public bool Query(short warpId, int screenX, int screenY, float alephNorm,
        int depthWidth, int depthHeight, int viewWidth, int viewHeight)
    {
        if (Mode == FlareOcclusionMode.Off) return false;
        if (!TryPixel(screenX, screenY, viewWidth, viewHeight, depthWidth, depthHeight, out int px, out int py))
            return false;

        if (Mode == FlareOcclusionMode.Blocking)
            return ReadOne(px, py, alephNorm);

        // Deferred: answer with what a previous frame's depth said about this
        // warp (one frame of latency), and queue a fresh sample for the read that
        // happens at the start of the next frame — immediately for a warp that has
        // no verdict yet, then every RefreshFrames (staggered by warp id so a wave
        // of simultaneous pulses does not pile its readbacks into one frame).
        bool have = _verdicts.TryGetValue(warpId, out var v)
                    && _frame - v.Frame <= MaxVerdictAgeFrames;
        bool occluded = have && v.Occluded;
        bool due = !have
                   || (_frame + (warpId & 0x7FFF) % RefreshFrames) % RefreshFrames == 0;
        if (due)
        {
            _pending.Add(new FlareSample(warpId, px, py, alephNorm));
            LastFrameSamples++;
            if (_pendingFrame == 0) _pendingFrame = _frame;
        }
        return occluded;
    }

    /// <summary>
    /// Resolve the samples the previous frame queued, from the depth target as it
    /// still stands at the start of this frame. Call this right BEFORE this
    /// frame's depth pass re-renders the target. A no-op for the other modes and
    /// when nothing was queued.
    /// </summary>
    public void SampleDeferred()
    {
        if (Mode != FlareOcclusionMode.Deferred) return;
        if (_pending.Count == 0 || _readDepth is null)
        {
            _pending.Clear();
            return;
        }

        long recorded = _pendingFrame;
        long started = Stopwatch.GetTimestamp();
        int resolved = 0;
        if (TryBatch(_pending, out var rect))
        {
            var pixels = Read(rect);
            if (pixels is not null)
            {
                for (int i = 0; i < _pending.Count; i++)
                {
                    var s = _pending[i];
                    int idx = (s.Y - rect.Y) * rect.Width + (s.X - rect.X);
                    if (idx < 0 || idx >= pixels.Length) continue;
                    Store(s.WarpId, LinearDepth.Decode(pixels[idx]), s.AlephNorm, recorded);
                    resolved++;
                }
            }
            Batches++;
            _reportBatches++;
        }
        else
        {
            // Flares spread across the screen: one small read this frame (the rest
            // of the due samples keep their previous verdict and re-queue at their
            // next refresh) rather than dragging the whole screen back.
            for (int i = 0; i < MaxReadbacksPerFrame && i < _pending.Count; i++)
            {
                var s = _pending[i];
                var pixel = Read(new Rectangle(s.X, s.Y, 1, 1));
                Batches++;
                _reportBatches++;
                if (pixel is null || pixel.Length == 0) continue;
                Store(s.WarpId, LinearDepth.Decode(pixel[0]), s.AlephNorm, recorded);
                resolved++;
            }
        }
        Charge(Stopwatch.GetElapsedTime(started).TotalMilliseconds, resolved);
        _pending.Clear();
        _pendingFrame = 0;      // the next queued sample stamps its own frame

    }

    /// <summary>
    /// Emit at most one diagnostic line per second while flares are being tested:
    /// the sample/batch counts and the readback time, which is the evidence the
    /// arrival-frame report quotes.
    /// </summary>
    public void Report(double nowSeconds, int depthWidth = 0, int depthHeight = 0)
    {
        if (_reportSamples == 0 || nowSeconds < _nextReport) return;
        _nextReport = nowSeconds + 1.0;
        string mode = Mode switch
        {
            FlareOcclusionMode.Deferred => "deferred 1 frame",
            FlareOcclusionMode.Blocking => "blocking (in-frame readback)",
            _ => "off",
        };
        string depthText = depthWidth > 0 ? $", depth {depthWidth}x{depthHeight}" : "";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[flare-occlusion] {_reportSamples} flare sample(s) in the last second via {_reportBatches} readback(s), {_reportMs:F2} ms total, worst {_reportWorstMs:F2} ms ({mode}{depthText}, {_verdicts.Count} verdict(s) cached)"));
        _reportSamples = 0;
        _reportMs = 0;
        _reportWorstMs = 0;
        _reportBatches = 0;
    }

    /// <summary>Map a top-left-origin screen pixel onto the linear-depth target
    /// (OpenGL render targets are bottom-left origin, hence the Y flip).</summary>
    public static bool TryPixel(int screenX, int screenY, int viewWidth, int viewHeight,
        int depthWidth, int depthHeight, out int x, out int y)
    {
        x = y = -1;
        if (viewWidth <= 0 || viewHeight <= 0 || depthWidth <= 0 || depthHeight <= 0) return false;
        x = (int)(screenX * (double)depthWidth / viewWidth);
        y = depthHeight - 1 - (int)(screenY * (double)depthHeight / viewHeight);
        if (x < 0 || y < 0 || x >= depthWidth || y >= depthHeight) return false;
        return true;
    }

    /// <summary>The P585 occlusion rule: the scene is occluding when it is nearer
    /// than the aleph by more than <see cref="OcclusionMargin"/>.</summary>
    public static bool IsSceneNearer(float sceneNorm, float alephNorm)
        => sceneNorm < alephNorm - OcclusionMargin;

    /// <summary>The bounding rectangle of a set of samples, when it is small
    /// enough to be worth one batched readback (false otherwise).</summary>
    public static bool TryBatch(IReadOnlyList<FlareSample> samples, out Rectangle rect)
    {
        rect = default;
        if (samples.Count == 0) return false;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s.X < minX) minX = s.X;
            if (s.Y < minY) minY = s.Y;
            if (s.X > maxX) maxX = s.X;
            if (s.Y > maxY) maxY = s.Y;
        }
        rect = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        if ((long)rect.Width * rect.Height > MaxBatchPixels)
        {
            rect = default;
            return false;
        }
        return true;
    }

    private bool ReadOne(int px, int py, float alephNorm)
    {
        long started = Stopwatch.GetTimestamp();
        var pixel = Read(new Rectangle(px, py, 1, 1));
        Batches++;
        _reportBatches++;
        LastFrameSamples++;
        Charge(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 1);
        if (pixel is null || pixel.Length == 0) return false;   // no depth → visible
        Samples++;
        return IsSceneNearer(LinearDepth.Decode(pixel[0]), alephNorm);
    }

    private Color[]? Read(Rectangle rect)
    {
        try { return _readDepth?.Invoke(rect); }
        catch { return null; }                                  // readback hiccup → no verdict
    }

    private void Store(short warpId, float sceneNorm, float alephNorm, long frame)
    {
        if (_verdicts.Count > MaxVerdicts) _verdicts.Clear();
        _verdicts[warpId] = new Verdict(IsSceneNearer(sceneNorm, alephNorm), frame);
        Samples++;
    }

    private void Charge(double ms, int samples)
    {
        TotalMs += ms;
        if (ms > WorstMs) WorstMs = ms;
        _reportMs += ms;
        if (ms > _reportWorstMs) _reportWorstMs = ms;
        _reportSamples += samples;
    }
}
