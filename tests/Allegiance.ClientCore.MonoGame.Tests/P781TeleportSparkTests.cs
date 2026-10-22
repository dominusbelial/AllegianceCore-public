using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Collision;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P781 — the lead-up teleport-spark rules: the pure ramp helpers
/// (<see cref="Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount"/>,
/// <c>SelectTeleportTime</c>, <c>AlephEta</c>) and the full
/// <see cref="WarpTransitParticles.EmitTeleport"/> emitter (native
/// <c>AddTeleportParticles</c>, effect/thinggeo.cpp:619-696) — the ramp, the
/// 1.5·R forward ring, the planar pull, the full 3-D blast at transit, sizing,
/// duration, and the zero-allocation steady state of the emit/age/gather path.
/// Deterministic <see cref="ITrailRandom"/> throughout.
/// </summary>
public class P781TeleportSparkTests
{
    private sealed class FixedRandom : ITrailRandom
    {
        public float T = 0.5f;
        public SimVec Dir = new(1f, 0f, 0f);
        public float Range(float min, float max) => min + T * (max - min);
        public SimVec Direction() => Dir;
    }

    private const float R = 10f;
    private static readonly SimVec Origin = new(1000f, 2000f, 3000f);
    private static readonly SimVec FrameVel = new(100f, 0f, 0f);

    // ── the pure ramp (native thinggeo.cpp:650-659, WinTrek.cpp:3293) ─────

    [Fact]
    public void SparkCount_follows_the_native_table()
    {
        // t = 5, 3, 1.5, 0 with the SHIPPED default smoke quality 3.
        Assert.Equal(0, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(5f, 3f));
        Assert.Equal(0, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(3f, 3f));
        Assert.Equal(4, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(1.5f, 3f));
        Assert.Equal(9, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(0f, 3f));
        // With smoke quality 1 (the static initialiser): 0/0/1/3.
        Assert.Equal(0, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(5f, 1f));
        Assert.Equal(0, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(3f, 1f));
        Assert.Equal(1, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(1.5f, 1f));
        Assert.Equal(3, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(0f, 1f));
        // Negative timers emit nothing.
        Assert.Equal(0, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(-1f, 3f));
    }

    [Fact]
    public void SparkCount_at_the_capture_times_in_float_arithmetic()
    {
        // The brief's hand table (t = 2.5/1.5/1/0.2/0 → 1/4/6/8/9) checked in the
        // same float arithmetic native uses (thinggeo.cpp:651-659): at exactly
        // t = 1.0, 1 − 1/3f = 0.66666663f and 9 × that = 5.9999995f, so floorf
        // gives 5, not 6 (6 from t ≈ 0.99 down). Every other point matches.
        Assert.Equal(1, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(2.5f, 3f));
        Assert.Equal(4, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(1.5f, 3f));
        Assert.Equal(5, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(1.0f, 3f));
        Assert.Equal(6, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(0.99f, 3f));
        Assert.Equal(8, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(0.2f, 3f));
        Assert.Equal(9, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(0f, 3f));
        // The pool uses the shipped default (WinTrek.cpp:3293), not the static 1.
        Assert.Equal(3f, WarpTransitParticles.ShowSmoke);
    }

    [Fact]
    public void Ripcord_spark_timer_runs_past_zero_so_the_spray_stops_while_the_state_lives_on()
    {
        // Native: SetTimeUntilRipcord(GetRipcordTimeLeft()) on ACTIVATE, then
        // m_fTimeUntilRipcord -= dt every frame with no clamp (thinggeo.cpp:694),
        // so it is negative right after zero and the spray stops (thinggeo.cpp:625)
        // even though the frozen model is still set until the server's move.
        long start = 50_000;
        const float dur = 3f;
        Assert.Equal(2f, Allegiance.ClientCore.Flight.RipcordLeadup.TimeUntilTeleport(start, start + 1000, dur, 1.0), 4);
        float past = Allegiance.ClientCore.Flight.RipcordLeadup.TimeUntilTeleport(start, start + 3500, dur, 1.0);
        Assert.True(past < 0f);
        Assert.Equal(0, Allegiance.ClientCore.Flight.RipcordLeadup.SparkCount(past, 3f));
        Assert.True(Allegiance.ClientCore.Flight.RipcordLeadup.IsRipcording(start, start + 3500, dur, 1.0));
        // No start / unknown duration: no sparks rather than a wrong ramp.
        Assert.Equal(-1f, Allegiance.ClientCore.Flight.RipcordLeadup.TimeUntilTeleport(null, start, dur, 1.0));
        Assert.Equal(-1f, Allegiance.ClientCore.Flight.RipcordLeadup.TimeUntilTeleport(start, start, 0f, 1.0));
        // TiDi: half-speed sim time.
        Assert.Equal(2.5f, Allegiance.ClientCore.Flight.RipcordLeadup.TimeUntilTeleport(start, start + 1000, dur, 0.5), 4);
    }

    [Fact]
    public void SelectTeleportTime_prefers_the_sooner_or_only_active_timer()
    {
        // thinggeo.cpp:620-623: start from the ripcord timer; use the aleph
        // timer when the ripcord one is idle (< 0) or the aleph comes first.
        Assert.Equal(-1f, Allegiance.ClientCore.Flight.RipcordLeadup.SelectTeleportTime(-1f, -1f));
        Assert.Equal(2f, Allegiance.ClientCore.Flight.RipcordLeadup.SelectTeleportTime(2f, -1f));
        Assert.Equal(1f, Allegiance.ClientCore.Flight.RipcordLeadup.SelectTeleportTime(-1f, 1f));
        Assert.Equal(1f, Allegiance.ClientCore.Flight.RipcordLeadup.SelectTeleportTime(3f, 1f));
        Assert.Equal(0.5f, Allegiance.ClientCore.Flight.RipcordLeadup.SelectTeleportTime(0.5f, 1f));
    }

    [Fact]
    public void AlephEta_is_range_over_closing()
    {
        var ship = new SimVec(0f, 0f, 0f);
        var warp = new SimVec(600f, 0f, 0f);
        Assert.Equal(6f, Allegiance.ClientCore.Flight.RipcordLeadup.AlephEta(ship, new SimVec(100f, 0f, 0f), warp), 4);
        Assert.Equal(-1f, Allegiance.ClientCore.Flight.RipcordLeadup.AlephEta(ship, new SimVec(-100f, 0f, 0f), warp));   // receding
        Assert.Equal(-1f, Allegiance.ClientCore.Flight.RipcordLeadup.AlephEta(ship, SimVec.Zero, warp));                 // stationary
    }

    // ── the emitter (thinggeo.cpp:619-696) ────────────────────────────────

    [Fact]
    public void EmitTeleport_negative_timer_emits_nothing()
    {
        var pool = new WarpTransitParticles();
        pool.EmitTeleport(Origin, FrameVel, SimVec.Zero, SimVec.Zero, R, -1f, 1f / 60f, new FixedRandom());
        Assert.Equal(0, pool.ActiveCount);
    }

    [Fact]
    public void EmitTeleport_count_follows_the_ramp()
    {
        var rng = new FixedRandom();
        var pool = new WarpTransitParticles();
        pool.EmitTeleport(Origin, FrameVel, SimVec.Zero, SimVec.Zero, R, 3f, 1f / 60f, rng);
        Assert.Equal(0, pool.ActiveCount);      // t = 3 → ramp 1 → 0 sparks

        pool.EmitTeleport(Origin, FrameVel, SimVec.Zero, SimVec.Zero, R, 1.5f, 1f / 60f, rng);
        Assert.Equal(4, pool.ActiveCount);

        pool.Reset();
        pool.EmitTeleport(Origin, FrameVel, SimVec.Zero, SimVec.Zero, R, 0f, 1f / 60f, rng);
        Assert.Equal(9, pool.ActiveCount);      // full transit burst
    }

    [Fact]
    public void Early_sparks_form_a_flat_ring_1_5R_in_front()
    {
        // t = 2 → ramp 2/3. With the fixed rng the blast lies exactly along the
        // velocity, so the planar pull is fully visible: every start sits near
        // the plane spanned by the spray X/Y axes, offset ≈ 1.5·R·ramp in front.
        var pool = new WarpTransitParticles();
        pool.EmitTeleport(Origin, FrameVel, SimVec.Zero, SimVec.Zero, R, 2f, 1f / 60f, new FixedRandom());
        Assert.Equal(2, pool.ActiveCount);   // floor(9·(1−2/3)) = 2 in float arithmetic

        foreach (var v in pool.Enumerate())
        {
            var d = v.Position - Origin;
            float inFront = d.X;                        // along Z = normalize(frameVelocity)
            Assert.True(inFront > 1.5f * R * (2f / 3f) - 0.5f,
                $"spark must be pushed in front along Z (got {inFront})");
            Assert.True(inFront < 1.5f * R * (2f / 3f) + R,
                $"spark must not be flung far past the ring (got {inFront})");
            float outOfPlane = MathF.Sqrt(d.Y * d.Y + d.Z * d.Z);
            Assert.True(outOfPlane < 0.5f,
                $"early sparks must lie near the X/Y plane (got {outOfPlane})");
        }
    }

    [Fact]
    public void At_transit_the_blast_is_fully_three_dimensional()
    {
        // An off-axis blast direction: at t = 0 the interpolation leaves the
        // full 3-D blast; at t = 2 the planar pull has crushed most of it.
        var rng = new FixedRandom { Dir = new SimVec(1f, 1f, 1f).Normalize() };
        var pool = new WarpTransitParticles();
        pool.EmitTeleport(Origin, FrameVel, SimVec.Zero, SimVec.Zero, R, 0f, 1f / 60f, rng);
        float transitOutOfPlane = MaxOutOfPlane(pool);

        pool.Reset();
        pool.EmitTeleport(Origin, FrameVel, SimVec.Zero, SimVec.Zero, R, 2f, 1f / 60f, rng);
        float earlyOutOfPlane = MaxOutOfPlane(pool);

        Assert.True(transitOutOfPlane > earlyOutOfPlane * 2f,
            $"transit blast must be far less planar than the early ring ({transitOutOfPlane} vs {earlyOutOfPlane})");
    }

    [Fact]
    public void Spark_size_scales_with_the_ramp_and_radius_and_lives_half_a_second()
    {
        var pool = new WarpTransitParticles();
        pool.EmitTeleport(Origin, FrameVel, SimVec.Zero, SimVec.Zero, R, 0f, 1f / 60f, new FixedRandom());
        // t = 0: scaled = (2·1+1)·R = 3R; size = random(0.45R, 0.9R) → 0.675R fixed.
        foreach (var v in pool.Enumerate())
        {
            Assert.InRange(v.Size, R * 0.449f, R * 0.901f);
            Assert.Equal(1f, v.RemainingFrac, 4);       // fresh
        }

        for (int i = 0; i < 20 && pool.ActiveCount > 0; i++)
            pool.Update(0.03f);
        Assert.Equal(0, pool.ActiveCount);              // 0.5 s lifetime
    }

    [Fact]
    public void EmitBurst_equals_EmitTeleport_at_the_transit_instant()
    {
        var a = new WarpTransitParticles();
        var b = new WarpTransitParticles();
        a.EmitBurst(Origin, FrameVel, SimVec.Zero, SimVec.Zero, R, 1f / 60f, new FixedRandom());
        b.EmitTeleport(Origin, FrameVel, SimVec.Zero, SimVec.Zero, R, 0f, 1f / 60f, new FixedRandom());
        Assert.Equal(b.ActiveCount, a.ActiveCount);
        using var va = a.Enumerate().GetEnumerator();
        using var vb = b.Enumerate().GetEnumerator();
        while (va.MoveNext() & vb.MoveNext())
        {
            Assert.Equal(vb.Current.Position, va.Current.Position);
            Assert.Equal(vb.Current.Velocity, va.Current.Velocity);
        }
    }

    [Fact]
    public void CopyTo_matches_Enumerate()
    {
        var pool = new WarpTransitParticles();
        pool.EmitTeleport(Origin, FrameVel, SimVec.Zero, SimVec.Zero, R, 1.5f, 1f / 60f, new FixedRandom());
        var list = new List<WarpSparkView>();
        pool.CopyTo(list);
        var enumerated = pool.Enumerate().ToList();
        Assert.Equal(enumerated.Count, list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            Assert.Equal(enumerated[i].Position, list[i].Position);
            Assert.Equal(enumerated[i].Size, list[i].Size);
            Assert.Equal(enumerated[i].RemainingFrac, list[i].RemainingFrac, 5);
        }
    }

    /// <summary>⭐ P781 zero-allocation gate: after warm-up, 600 emitter-frames
    /// for 50 ships (emit + age + CopyTo) allocate 0 bytes on the calling
    /// thread — the native-style per-frame spray must never touch the GC.</summary>
    [Fact]
    public void Emit_update_copyto_steady_state_allocates_zero()
    {
        var pool = new WarpTransitParticles();
        var rng = new FixedRandom();
        var views = new List<WarpSparkView>();
        var rngs = new FixedRandom[50];
        for (int s = 0; s < 50; s++) rngs[s] = new FixedRandom();

        void Frame(int frame)
        {
            for (int s = 0; s < 50; s++)
            {
                // Staggered activations: the ramp phases overlap across ships.
                float t = (frame % 180) / 60f - s * 0.01f;
                pool.EmitTeleport(Origin, FrameVel, SimVec.Zero, SimVec.Zero, R, t,
                    WarpTransitParticles.EmissionClockSeconds, rngs[s]);
            }
            pool.Update(WarpTransitParticles.EmissionClockSeconds);
            pool.CopyTo(views);
        }

        for (int i = 0; i < 60; i++) Frame(i);          // warm-up (capacities settle)
        Thread.Sleep(300);                              // let tiered JIT promote the hot path first
        for (int i = 0; i < 60; i++) Frame(i);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 60; i < 660; i++) Frame(i);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
        Assert.True(pool.ActiveCount > 0);              // the scene actually sprays
        Assert.True(pool.ActiveCount <= pool.Capacity);
    }

    /// <summary>P781 — the shared billboard batcher now fills reused buffers
    /// (it allocated two arrays per draw call, ~110 B per live particle per
    /// frame — the dominant GC cost of a 2 700-spark lead-up scene): the
    /// non-allocating fill writes exactly the geometry the old builder did, in
    /// any chunk, and allocates nothing.</summary>
    [Fact]
    public void Billboard_fill_matches_the_builder_and_allocates_nothing()
    {
        var items = new List<ParticleBillboard>();
        for (int i = 0; i < 300; i++)
            items.Add(new ParticleBillboard(new Microsoft.Xna.Framework.Vector3(i, -i, 2 * i), 1f + i % 7,
                new Microsoft.Xna.Framework.Color(i % 255, 40, 200, 255), i * 0.1f));
        var view = Microsoft.Xna.Framework.Matrix.CreateLookAt(
            new Microsoft.Xna.Framework.Vector3(5, 6, 7), Microsoft.Xna.Framework.Vector3.Zero, Microsoft.Xna.Framework.Vector3.Up);
        ParticleBillboardBatcher.BuildBillboardArrays(items, view, out var refVerts, out var refIdx);

        var verts = new Microsoft.Xna.Framework.Graphics.VertexPositionColorTexture[items.Count * 4];
        var idx = new short[items.Count * 6];
        // Warm-up past tiered-JIT promotion (call counting + the ~100 ms tier-up
        // delay), so a background re-JIT installing mid-window cannot show up.
        for (int k = 0; k < 200; k++)
            ParticleBillboardBatcher.FillBillboardArrays(items, 0, items.Count, view, verts, idx);
        Thread.Sleep(300);
        for (int k = 0; k < 200; k++)
            ParticleBillboardBatcher.FillBillboardArrays(items, 0, items.Count, view, verts, idx);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 0; k < 100; k++)
            ParticleBillboardBatcher.FillBillboardArrays(items, 0, items.Count, view, verts, idx);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(refVerts, verts);
        Assert.Equal(refIdx, idx);

        // A later chunk starts its indices at 0 (one draw call per chunk).
        ParticleBillboardBatcher.FillBillboardArrays(items, 100, 50, view, verts, idx);
        Assert.Equal(refVerts[400], verts[0]);
        Assert.Equal((short)0, idx[0]);
        Assert.Equal(8191, ParticleBillboardBatcher.MaxQuadsPerDraw);   // 16-bit indices: 8191 × 4 ≤ 32 767
    }

    private static float MaxOutOfPlane(WarpTransitParticles pool)
    {
        float max = 0f;
        foreach (var v in pool.Enumerate())
        {
            var d = v.Position - Origin;
            float outOfPlane = MathF.Sqrt(d.Y * d.Y + d.Z * d.Z);
            if (outOfPlane > max) max = outOfPlane;
        }
        return max;
    }
}
