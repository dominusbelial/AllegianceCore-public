using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;
using XnaVec = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P579 — real-window proof facility for the adopted Volumetric tier. ENV-gated
/// hooks, all inert by default (no env var → zero overhead):
///
///   <list type="bullet">
///   <item><b>ALLEGIANCE_VOL_PROOF</b> = <c>TRIGGER_SEC:TYPE@DIST[:COUNT][;…]</c>
///   deterministically injects synthetic explosion <see cref="ExplosionEntity"/>s
///   into the real draw list at camera-relative positions (straight ahead at
///   DIST units, TYPE the native category). This renders a controlled blast in
///   the REAL backbuffer at a known distance — the same entity type/distance the
///   game's own fighter deaths produce — so brightness and frame-time can be
///   measured exactly. TRIGGER_SEC is WALL seconds after the render loop starts,
///   so it aligns with the live-proof wall-clock autoshots. Example:
///   <c>6:3@150;3@600;3@1500</c> = at 6 s, one fighter (type 3) death at 150 u,
///   one at 600 u, one at 1500 u. Entities re-inject every frame with
///   ReceivedLocalMs pinned to the trigger so the blast ages normally.</item>
///   <item><b>ALLEGIANCE_VOL_FRAMESTRIP</b> = <c>0.1,0.3,0.6,1.0,2.0</c> — at each
///   of those explosion AGES, queue a real-backbuffer autoshot, giving the
///   deterministic frame-strip (0.1…2.0 s) the report needs.</item>
///   <item><b>ALLEGIANCE_VOL_PERF</b> = <c>windowMs</c> logs per-frame ms average
///   + 1%-low (p1) over the trailing window, periodically, from the real render
///   loop (the frame-time the task's perf gate cares about). Pair with
///   ALLEGIANCE_NOVSYNC=1 to see true GPU time rather than the 60 Hz lock.</item>
///   </list>
/// </summary>
public sealed partial class Game1
{
    // ── deterministic real-window injection ─────────────────────────────────
    private bool _volProofParsed;
    private double _volProofTriggerWallSec;
    private long _volProofAnchorWallMs = -1;
    private double _volProofStartSec = -1;
    private float[] _volProofStrip = Array.Empty<float>();
    private int _volProofStripConsumed;
    private (int Type, float Dist, int Count)[] _volProofSpec = Array.Empty<(int, float, int)>();

    private void ParseVolumetricProof()
    {
        var raw = System.Environment.GetEnvironmentVariable("ALLEGIANCE_VOL_PROOF");
        if (string.IsNullOrWhiteSpace(raw)) return;
        var parts = raw.Split(':', 2);
        if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _volProofTriggerWallSec))
        {
            Console.WriteLine($"[volproof] bad ALLEGIANCE_VOL_PROOF '{raw}' — expected TRIGGER_SEC:TYPE@DIST[:COUNT][;…]");
            return;
        }
        var specs = new List<(int, float, int)>();
        foreach (var tok in parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = tok.Split('@');
            if (seg.Length != 2 || !int.TryParse(seg[0], out int type) || !float.TryParse(seg[1].Split(':')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float dist))
            {
                Console.WriteLine($"[volproof] bad spec '{tok}'"); continue;
            }
            int count = 1;
            if (seg[1].Contains(':') && !int.TryParse(seg[1].Split(':')[1], out count)) count = 1;
            specs.Add((type, dist, Math.Max(1, count)));
        }
        _volProofSpec = specs.ToArray();
        var stripRaw = System.Environment.GetEnvironmentVariable("ALLEGIANCE_VOL_FRAMESTRIP");
        if (!string.IsNullOrWhiteSpace(stripRaw))
            _volProofStrip = stripRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : -1f)
                .Where(f => f >= 0f).ToArray();
        _volProofParsed = true;
        Console.WriteLine($"[volproof] armed: trigger={_volProofTriggerWallSec}s wall specs={_volProofSpec.Length} framestrip=[{string.Join(",", _volProofStrip)}]");
    }

    /// <summary>Called every frame from the Draw explosion block: append the
    /// synthetic proof entities (once the wall-clock trigger has passed) at
    /// camera-relative positions ALONG THE VIEW FORWARD (so the blast is dead
    /// ahead of the camera), pinning Receipt to the trigger so the blast ages
    /// like a real death; optionally queue frame-strip autoshots.</summary>
    private void InjectVolumetricProof(List<ExplosionEntity> draw, short ownSectorId, XnaVec cameraEye, Matrix view, long nowLocalMs)
    {
        if (!_volProofParsed) ParseVolumetricProof();
        if (!_volProofParsed || _volProofSpec.Length == 0) return;

        if (_volProofAnchorWallMs < 0) _volProofAnchorWallMs = Environment.TickCount64;
        double wallElapsed = (Environment.TickCount64 - _volProofAnchorWallMs) / 1000.0;
        if (wallElapsed < _volProofTriggerWallSec) return;

        if (_volProofStartSec < 0)
        {
            _volProofStartSec = _nowSeconds;
            Console.WriteLine($"[volproof] trigger: injecting {_volProofSpec.Sum(s => s.Count)} volume(s) (wall {wallElapsed:0.0}s)");
        }
        double age = _nowSeconds - _volProofStartSec;
        if (age > 8.0) return;                       // let the blast finish, then stop injecting
        long anchor = nowLocalMs - (long)(age * 1000.0);   // age = now − anchor

        // Camera forward = NEGATED column 2 of the view matrix: MonoGame's
        // CreateLookAt stores the backward vector zaxis=(position−target) as
        // (M13,M23,M33), so world forward = −(M13,M23,M33).
        var forward = XnaVec.Normalize(new XnaVec(-view.M13, -view.M23, -view.M33));

        foreach (var (type, dist, count) in _volProofSpec)
        {
            for (int k = 0; k < count; k++)
            {
                // Dead ahead at DIST units; jitter count > 1 so overlapping
                // blasts are distinguishable.
                float jx = count > 1 ? ((k % 3) - 1) * 6f : 0f;
                float jy = count > 1 ? (((k / 3) % 3) - 1) * 6f : 0f;
                var p = cameraEye + forward * dist + new XnaVec(jx, jy, 0f);
                draw.Add(new ExplosionEntity
                {
                    Type = type,
                    Position = new Allegiance.Core.Collision.Vector3(p.X, p.Y, p.Z),
                    ClusterId = ownSectorId,
                    Velocity = Allegiance.Core.Collision.Vector3.Zero,
                    ReceivedLocalMs = anchor,
                });
            }
        }

        // Queue a real-backbuffer autoshot at each frame-strip age (consumed once).
        if (_volProofStrip.Length > 0)
        {
            while (_volProofStripConsumed < _volProofStrip.Length && age + 0.033 >= _volProofStrip[_volProofStripConsumed])
            {
                ScheduleAutoshot(shotIndex: -1);
                _volProofStripConsumed++;
            }
        }
    }

    // ── frame-time stats (avg ms + 1%-low) ──────────────────────────────────
    private bool _volPerfParsed;
    private readonly double[] _volPerfRing = new double[1024];
    private int _volPerfIdx;
    private double _volPerfNextReport;
    private System.IO.StreamWriter? _volPerfLog;

    private void MaybeLogFrameStats(double frameMs)
    {
        if (!_volPerfParsed)
        {
            var raw = System.Environment.GetEnvironmentVariable("ALLEGIANCE_VOL_PERF");
            if (string.IsNullOrWhiteSpace(raw)) return;
            _volPerfParsed = true;
            _volPerfNextReport = _nowSeconds + 2.0;
            // ALLEGIANCE_VOL_PERFLOG=<path> → append per-frame CSV (now,frameMs) so
            // the explosion window's cost can be isolated frame-accurately.
            var logPath = System.Environment.GetEnvironmentVariable("ALLEGIANCE_VOL_PERFLOG");
            if (!string.IsNullOrWhiteSpace(logPath))
            {
                try { _volPerfLog = new System.IO.StreamWriter(logPath, append: true) { AutoFlush = true }; }
                catch { _volPerfLog = null; }
            }
            Console.WriteLine("[volperf] frame-time stats armed (report every 2 s)");
        }
        if (_volPerfLog is { } log)
            log.WriteLine($"{_nowSeconds:0.000},{frameMs:0.000}");
        _volPerfRing[_volPerfIdx++ % _volPerfRing.Length] = frameMs;
        if (_nowSeconds < _volPerfNextReport) return;
        _volPerfNextReport = _nowSeconds + 2.0;

        int n = Math.Min(_volPerfIdx, _volPerfRing.Length);
        if (n < 10) return;
        var sorted = _volPerfRing.Take(n).OrderBy(v => v).ToArray();
        double avg = sorted.Average();
        double p1 = sorted[(int)(n * 0.01)];
        Console.WriteLine($"[volperf] {n} frames  avg {avg:0.00} ms ({1000.0 / Math.Max(avg, 1e-4):0.0} fps)  p1(1% low) {p1:0.00} ms ({1000.0 / Math.Max(p1, 1e-4):0.0} fps)  shipsDrawCalls={_lodDrawCalls} lodFull={_lodFullCount} lodFar={_lodFarCount}");
    }
}
