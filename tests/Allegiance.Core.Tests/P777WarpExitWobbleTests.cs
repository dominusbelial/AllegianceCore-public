using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Ls = global::Allegiance.Server.LiveSettingsConfig;
using ServerHost = global::Allegiance.Server.ServerHost;

namespace Allegiance.Core.Tests;

/// <summary>
/// P777 — native's aleph <b>exit wobble</b> (<c>CFSShip::HitWarp</c>,
/// <c>FedSrv/fsship.cpp:90-132</c>): the exit frame is the destination warp's
/// <c>Orientation(forward)</c> pitched and yawed by <c>random(−error, error)</c>
/// each, the roll turn-rate is bumped by <c>random(π·0.5·error, π·1.5·error)</c>,
/// and <c>error = d/(R·R) + 0.125</c> measured against the <b>source</b> warp,
/// scaled by <c>300/mass</c> above 300 kg. The formula lives in
/// <see cref="AlephExit"/>; <c>ServerHost.HitWarpEvent</c> orchestrates it.
/// </summary>
/// <remarks>
/// <para>The alephs of <c>m2xspiderw.igc</c> all have <c>R = 100</c>; warp 2157
/// (Tellus → Omega, cluster 1055 → 1053) is the batch-16 rig aleph the P776
/// tests use, and its partner 2156 carries a tilted forward
/// <c>(1, −0.811, 0.088)</c> whose <c>Orientation.Set(forward)</c> up differs
/// from the old port-only <c>Set(forward, worldUp)</c> — so test 2 really
/// discriminates the two frames.</para>
/// <para>Entry geometry is quoted as <c>d/R</c>, the off-axis distance of the
/// ship's line of flight from the source aleph centre over the radius; the
/// native <c>d</c> is that distance <b>squared</b>, so
/// <c>error = (d/R)² + 0.125</c>.</para>
/// </remarks>
[Collection("LiveNetwork")]
public sealed class P777WarpExitWobbleTests(ITestOutputHelper output)
{
    private const short TellusWarpId = 2157;   // Tellus (1055) -> Omega (1053); partner 2156
    private const short FighterHullId = 121;   // PCore001: "Fighter", mass 30 kg, length 14 (radius 7)
    private const float Radius = 100f;         // every m2xspiderw aleph

    private static readonly Lazy<(StaticCore Core, MissionMap Map)> Assets = new(() =>
    {
        var coreFile = IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc"));
        if (coreFile.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        var mapFile = IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "m2xspiderw.igc"));
        if (mapFile.Wrapper != IgcWrapper.DataSize)
            throw new InvalidDataException("m2xspiderw.igc is not a mission map");
        return (StaticCore.Load(coreFile), MissionMap.Load(mapFile));
    });

    private static Vector3 Vec(float[]? v, Vector3 fallback) =>
        v is { Length: 3 } ? new Vector3(v[0], v[1], v[2]) : fallback;

    private static Mission NewStartedMission(IIgcSite? site = null)
    {
        var (core, map) = Assets.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var mission = Mission.Assemble(core, map, [new SideSpec(0, "Team 1", civ)], site ?? new NullIgcSite());
        mission.Stage = MissionStage.Started;
        return mission;
    }

    private static (Mission Mission, Sector Sector, ResolvedWarp Warp) EntryScenario()
    {
        var mission = NewStartedMission();
        var warp = mission.Warps.First(w => w.Warp.WarpId == TellusWarpId);
        var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
        return (mission, sector, warp);
    }

    /// <summary>The destination warp's base frame — native
    /// <c>pwarpDest-&gt;GetOrientation()</c>, i.e.
    /// <c>Orientation(dataWarp-&gt;forward)</c> (<c>warpIGC.cpp:44-46</c>).</summary>
    private static Orientation BaseFrame(ResolvedWarp warp)
    {
        var o = new Orientation();
        o.Set(Vec(warp.Warp.Forward, new Vector3(0f, 0f, -1f)));
        return o;
    }

    /// <summary>Recover the applied pitch/yaw from the exit frame:
    /// <c>θ = asin(up·b₀)</c>, <c>φ = asin(backward·r₀)</c> with
    /// <c>b₀, r₀</c> the unperturbed backward/right of
    /// <c>Orientation.Set(destForward)</c> (Pitch then Yaw are pre-multiplied
    /// about those own axes, <c>orientation.cpp:514-560</c>).</summary>
    private static (float Theta, float Phi) RecoverAngles(Orientation exit, Orientation baseFrame)
    {
        var b0 = baseFrame.GetBackward();
        var r0 = baseFrame.GetRight();
        float theta = MathF.Asin(Math.Clamp(exit.GetUp() * b0, -1f, 1f));
        float phi = MathF.Asin(Math.Clamp(exit.GetBackward() * r0, -1f, 1f));
        return (theta, phi);
    }

    private static float TorqueMultiplier(Vector3 velocity, float maxSpeed)
    {
        float fraction = maxSpeed > 0f ? velocity.Length() / maxSpeed : 0f;   // shipIGC.h:542-549
        return 0.5f + 0.5f * 2.0f * fraction / (fraction + 1.0f);
    }

    /// <summary>Put a ship at <c>d/R = frac</c> off the source aleph centre,
    /// flying at <paramref name="speed"/> u/s parallel to
    /// <paramref name="flight"/> — the line of flight stays that far off the
    /// centre, so native's <c>d</c> is <c>(frac·R)²</c>.</summary>
    private static Vector3 OffAxisPosition(Vector3 warpPos, Vector3 flight, Vector3 side, float frac) =>
        warpPos + side * (frac * Radius);

    // ── 1. the exit through the real HitWarpEvent ────────────────────────────

    /// <summary>
    /// ⭐ Test 1 — <c>ServerHost.HitWarpEvent</c> exits with native's wobble:
    /// bounds, roll bump, velocity along the <b>perturbed</b> backward, and a
    /// seed-reproducible pose. Fighter hull (mass 30 ≤ 300, no mass scaling),
    /// entry at 50 u/s with <c>d/R = 0.5</c>, so <c>error = 0.25 + 0.125 =
    /// 0.375</c>.
    /// **Fails on the unfixed base: no pitch/yaw, no roll bump.**
    /// </summary>
    [SkippableFact]
    public void Exit_orientation_is_wobbled_within_native_bounds_and_reproducible()
    {
        Require.Artwork();
        const float entrySpeed = 50f;
        const float frac = 0.5f;
        const float expectedError = frac * frac + 0.125f;   // 0.375

        var mission = NewStartedMission();
        var warp = mission.Warps.First(w => w.Warp.WarpId == TellusWarpId);
        var partner = warp.Partner()!;
        var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
        var warpPos = Vec(warp.Warp.Position, Vector3.Zero);
        var baseFrame = BaseFrame(partner);
        var flight = baseFrame.GetBackward();        // fly along the destination's backward
        var side = baseFrame.GetRight();             // 0.5R off the source aleph's centre line
        float bonus = mission.Core.Constants.FloatConstants[3];

        (Orientation Orientation, Vector3 Position, Vector3 Velocity, float Roll) Run(int seed)
        {
            var ship = new Ship(9001, Assets.Value.Core.HullTypes[FighterHullId],
                OffAxisPosition(warpPos, flight, side, frac), flight * entrySpeed, Orientation.Identity)
            { SideId = 0 };
            mission.PlaceShip(ship, sector);
            var host = new ServerHost { WarpExitRandom = new Random(seed) };
            float rollBefore = ship.TurnRates[ControlData.AxisRoll];
            host.HitWarpEvent(ship, warp);
            Assert.Equal(partner.Warp.ClusterId, ship.Sector?.Id);
            return (ship.Orientation.Copy(), ship.Position, ship.Velocity,
                    ship.TurnRates[ControlData.AxisRoll] - rollBefore);
        }

        var first = Run(1234);
        var (theta, phi) = RecoverAngles(first.Orientation, baseFrame);

        float e = expectedError;
        Assert.InRange(MathF.Max(MathF.Abs(theta), MathF.Abs(phi)), 1e-6f, e + 1e-4f);
        output.WriteLine($"P777-T1: error={e:F4} theta={theta:F4} phi={phi:F4} rollDelta={first.Roll:F4} " +
                         $"rollRange=[{MathF.PI * 0.5f * e:F4},{MathF.PI * 1.5f * e:F4}]");

        Assert.InRange(first.Roll, MathF.PI * 0.5f * e - 1e-4f, MathF.PI * 1.5f * e + 1e-4f);

        // Velocity: (entry + c_fcidExitWarpSpeed) along the PERTURBED backward.
        Assert.Equal(entrySpeed + bonus, first.Velocity.Length(), 3);
        var alongPerturbed = first.Orientation.GetBackward() * -(entrySpeed + bonus);
        Assert.True((first.Velocity - alongPerturbed).Length() < 1e-3f,
            $"velocity {first.Velocity} must lie along the perturbed backward {alongPerturbed}");

        // Same seed, same exit — bit-identical pose and roll bump.
        var second = Run(1234);
        Assert.Equal(first.Position, second.Position);
        Assert.Equal(first.Velocity, second.Velocity);
        Assert.Equal(first.Roll, second.Roll);
        Assert.Equal(first.Orientation.GetRight(), second.Orientation.GetRight());
        Assert.Equal(first.Orientation.GetUp(), second.Orientation.GetUp());
        Assert.Equal(first.Orientation.GetBackward(), second.Orientation.GetBackward());
    }

    // ── 2. a stationary ship keeps the exact aleph frame ─────────────────────

    /// <summary>
    /// ⭐ Test 2 — native only wobbles when <c>speed2 &gt; 0</c>
    /// (<c>fsship.cpp:94</c>): a parked ship exits in the exact
    /// <c>Orientation.Set(destForward)</c> frame with no roll bump, and the
    /// position is the native <c>:129-132</c> formula on the unperturbed frame.
    /// The destination warp is <b>2156</b>, whose forward <c>(1, −0.811, 0.088)</c>
    /// makes <c>Set(forward)</c> differ from the old port-only
    /// <c>Set(forward, worldUp)</c> — asserted here so the test cannot pass on
    /// the world-up frame.
    /// **Fails on the unfixed base (different up/right).**
    /// </summary>
    [SkippableFact]
    public void Stationary_ship_exits_with_the_exact_aleph_frame()
    {
        Require.Artwork();
        const int seed = 4242;

        var mission = NewStartedMission();
        var warp = mission.Warps.First(w => w.Warp.WarpId == TellusWarpId);
        var partner = warp.Partner()!;
        var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
        var destPos = Vec(partner.Warp.Position, Vector3.Zero);
        var expectedFrame = BaseFrame(partner);

        // The test only discriminates if the two frames differ: Set(forward)
        // vs the old port-only Set(forward, worldUp).
        var oldPortFrame = new Orientation();
        oldPortFrame.Set(Vec(partner.Warp.Forward, new Vector3(0f, 0f, -1f)), new Vector3(0f, 1f, 0f));
        Assert.True((oldPortFrame.GetUp() - expectedFrame.GetUp()).Length() > 1e-3f,
            "warp 2156's Set(forward) up must differ from Set(forward, worldUp)");
        output.WriteLine($"P777-T2: dest forward={Vec(partner.Warp.Forward, Vector3.Zero)} " +
                         $"nativeUp={expectedFrame.GetUp()} oldWorldUpFrameUp={oldPortFrame.GetUp()}");

        var ship = new Ship(9002, Assets.Value.Core.HullTypes[FighterHullId],
            Vec(warp.Warp.Position, Vector3.Zero), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, sector);
        float rollBefore = ship.TurnRates[ControlData.AxisRoll];
        var host = new ServerHost { WarpExitRandom = new Random(seed) };
        host.HitWarpEvent(ship, warp);

        Assert.Equal(partner.Warp.ClusterId, ship.Sector?.Id);
        Assert.Equal(expectedFrame.GetRight(), ship.Orientation.GetRight());
        Assert.Equal(expectedFrame.GetUp(), ship.Orientation.GetUp());
        Assert.Equal(expectedFrame.GetBackward(), ship.Orientation.GetBackward());
        Assert.Equal(rollBefore, ship.TurnRates[ControlData.AxisRoll]);

        // Native position: no pitch/yaw/roll draws at speed 0, so the first two
        // random draws are the up then right offsets (fsship.cpp:129-131).
        var expectedRng = new Random(seed);
        float upOff = AlephExit.NativeRandom(expectedRng, 2f, 5f);
        float rightOff = AlephExit.NativeRandom(expectedRng, 2f, 5f);
        float shipRadius = 0.5f * Assets.Value.Core.HullTypes[FighterHullId].Length;
        var expectedPos = destPos
            + expectedFrame.GetUp() * upOff
            + expectedFrame.GetRight() * rightOff
            - expectedFrame.GetBackward() * (shipRadius + 5f);
        Assert.Equal(expectedPos, ship.Position);
        Assert.InRange(upOff, 2f, 5f);
        Assert.InRange(rightOff, 2f, 5f);

        // Speed 0 still gets the exit bonus: -(0 + c_fcidExitWarpSpeed) along backward.
        float bonus = mission.Core.Constants.FloatConstants[3];
        Assert.Equal(bonus, ship.Velocity.Length(), 3);
        Assert.True((ship.Velocity - expectedFrame.GetBackward() * -bonus).Length() < 1e-3f);
    }

    // ── 3. the wobble distribution ───────────────────────────────────────────

    /// <summary>
    /// ⭐ Test 3 — the distribution of the pure helper. 20,000 seeded draws per
    /// <c>d/R</c> × mass cell: every angle inside ±e, extremes past 0.95e on
    /// both sides, means at zero, roll delta inside [π/2·e, 3π/2·e] with mean
    /// ≈ π·e, mass 600 halving e, and the up/right offsets continuous in
    /// [2, 5) (the base's <c>_rand.Next(20,51)/10</c> had only 31 values).
    /// **The offset-continuity assertion fails on the unfixed base (there is no
    /// helper there at all); the angle/roll assertions have no base equivalent.**
    /// </summary>
    [SkippableTheory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1.0f)]
    public void Wobble_distribution_matches_native_bounds(float frac)
    {
        const int draws = 20_000;
        const float shipRadius = 10f;
        const float bonus = 15f;

        var destPos = Vector3.Zero;
        var destForward = new Vector3(0f, 0f, -1f);
        var frame0 = new Orientation();
        frame0.Set(destForward);
        var b0 = frame0.GetBackward();
        var r0 = frame0.GetRight();
        var side = new Vector3(1f, 0f, 0f);
        var sourcePos = Vector3.Zero;
        var shipPos = OffAxisPosition(sourcePos, b0, side, frac);
        var shipVel = b0 * 50f;

        var measured = new Dictionary<float, (float E, float MinT, float MaxT, float MeanT, float MinP, float MaxP,
                                              float MeanP, float MinRoll, float MaxRoll, float MeanRoll,
                                              int DistinctUp, int DistinctRight)>();
        foreach (float mass in new[] { 100f, 600f })
        {
            float e = frac * frac + 0.125f;
            if (mass > 300f) e *= 300f / mass;

            var rng = new Random(7000 + (int)(frac * 10) * 31 + (int)mass);
            var orientation = new Orientation();
            float minT = float.MaxValue, maxT = float.MinValue, sumT = 0f;
            float minP = float.MaxValue, maxP = float.MinValue, sumP = 0f;
            float minRoll = float.MaxValue, maxRoll = float.MinValue, sumRoll = 0f;
            var upOffsets = new HashSet<float>();
            var rightOffsets = new HashSet<float>();

            for (int i = 0; i < draws; i++)
            {
                var result = AlephExit.Apply(orientation,
                    sourcePos, Radius,
                    destPos, destForward,
                    shipPos, shipVel, mass, shipRadius, bonus, rng);

                Assert.Equal(e, result.Error, 6);
                float theta = MathF.Asin(Math.Clamp(orientation.GetUp() * b0, -1f, 1f));
                float phi = MathF.Asin(Math.Clamp(orientation.GetBackward() * r0, -1f, 1f));
                Assert.True(MathF.Abs(theta) <= e, $"|theta| {MathF.Abs(theta)} > e {e}");
                Assert.True(MathF.Abs(phi) <= e, $"|phi| {MathF.Abs(phi)} > e {e}");
                minT = MathF.Min(minT, theta); maxT = MathF.Max(maxT, theta); sumT += theta;
                minP = MathF.Min(minP, phi); maxP = MathF.Max(maxP, phi); sumP += phi;

                Assert.InRange(result.RollRateDelta, MathF.PI * 0.5f * e, MathF.PI * 1.5f * e);
                minRoll = MathF.Min(minRoll, result.RollRateDelta); maxRoll = MathF.Max(maxRoll, result.RollRateDelta);
                sumRoll += result.RollRateDelta;

                var backward = orientation.GetBackward();
                var delta = result.Position - destPos + backward * (shipRadius + 5f);
                upOffsets.Add(delta * orientation.GetUp());
                rightOffsets.Add(delta * orientation.GetRight());
            }

            Assert.All(upOffsets, u => Assert.InRange(u, 2f, 5f));
            Assert.All(rightOffsets, w => Assert.InRange(w, 2f, 5f));
            Assert.True(upOffsets.Count > 1000, $"up offsets are quantised: {upOffsets.Count} distinct in {draws}");
            Assert.True(rightOffsets.Count > 1000, $"right offsets are quantised: {rightOffsets.Count} distinct in {draws}");

            float meanT = sumT / draws, meanP = sumP / draws, meanRoll = sumRoll / draws;
            Assert.True(maxT > 0.95f * e && minT < -0.95f * e, $"theta extremes [{minT}, {maxT}] vs e {e}");
            Assert.True(maxP > 0.95f * e && minP < -0.95f * e, $"phi extremes [{minP}, {maxP}] vs e {e}");
            Assert.True(MathF.Abs(meanT) < 0.03f * e, $"mean theta {meanT} vs e {e}");
            Assert.True(MathF.Abs(meanP) < 0.03f * e, $"mean phi {meanP} vs e {e}");
            Assert.True(MathF.Abs(meanRoll - MathF.PI * e) < 0.03f * MathF.PI * e,
                $"mean roll {meanRoll} vs pi*e {MathF.PI * e}");

            measured[mass] = (e, minT, maxT, meanT, minP, maxP, meanP, minRoll, maxRoll, meanRoll,
                              upOffsets.Count, rightOffsets.Count);
        }

        // Mass > 300 halves e at 600 kg (fsship.cpp:105-108).
        Assert.Equal(measured[100f].E * 0.5f, measured[600f].E, 6);
        Assert.InRange(measured[600f].MaxT, 0.49f * measured[100f].MaxT, 0.51f * measured[100f].MaxT);

        foreach (var (mass, m) in measured.OrderBy(k => k.Key))
        {
            output.WriteLine($"P777-T3 d/R={frac:F1} mass={mass:F0}: e={m.E:F6} " +
                $"theta[{m.MinT:F5},{m.MaxT:F5}] mean={m.MeanT:F6}; " +
                $"phi[{m.MinP:F5},{m.MaxP:F5}] mean={m.MeanP:F6}; " +
                $"roll[{m.MinRoll:F5},{m.MaxRoll:F5}] mean={m.MeanRoll:F5} (pi*e={MathF.PI * m.E:F5}); " +
                $"distinct up/right={m.DistinctUp}/{m.DistinctRight}");
        }
    }

    // ── 4. what native guarantees about re-entry, and what it does not ───────

    /// <summary>
    /// ⭐ Test 4 — exit clearance against P776's cone
    /// (<see cref="AlephConeHitTest.Touches"/>, <c>modelIGC.cpp:127-130</c>,
    /// <c>HitTest.cpp:254-283</c>), 20,000 seeded draws:
    /// <list type="bullet">
    /// <item><b>centre hit</b> (<c>d = 0</c>, <c>error = 0.125</c>): no exit
    /// touches for r = 5/10/25/50, minimum clearance ≈ 3.3-3.5 u (versus 5 u
    /// without wobble). Passes on the unfixed base too.</item>
    /// <item><b>rim hit</b> (<c>d = R</c>, <c>error = 1.125</c>): a measurable
    /// fraction of exits touches. **Fails on the unfixed base (0 %).**</item>
    /// </list>
    /// This pins what native guarantees and what it does not; touching exits are
    /// native behaviour and must not be "fixed".
    /// </summary>
    [SkippableFact]
    public void Exit_clearance_matches_the_native_analysis()
    {
        const int draws = 20_000;
        const float bonus = 15f;
        var destPos = Vector3.Zero;
        var destForward = new Vector3(0f, 0f, -1f);
        var sourcePos = Vector3.Zero;
        var side = new Vector3(1f, 0f, 0f);

        // ---- centre hit: never touches, min clearance 3.3-3.5 u ----
        foreach (float shipRadius in new[] { 5f, 10f, 25f, 50f })
        {
            var rng = new Random(9100 + (int)shipRadius);
            var orientation = new Orientation();
            int touching = 0;
            float minClearance = float.MaxValue;
            var shipPos = sourcePos;                      // on the axis: d = 0
            var shipVel = new Vector3(0f, 0f, -1f) * 50f;
            for (int i = 0; i < draws; i++)
            {
                var result = AlephExit.Apply(orientation, sourcePos, Radius, destPos, destForward,
                    shipPos, shipVel, 100f, shipRadius, bonus, rng);
                Assert.Equal(0.125f, result.Error, 6);
                bool touches = AlephConeHitTest.Touches(destPos, destForward, Radius,
                                                        result.Position, shipRadius);
                if (touches) touching++;
                var b = AlephConeHitTest.Backward(destForward);
                var d = result.Position - destPos;
                float z = d * b;
                float rho = (d - b * z).Length();
                minClearance = MathF.Min(minClearance,
                    AlephConeHitTest.DistanceToConeSection(rho, z, Radius) - shipRadius);
            }
            output.WriteLine($"P777-T4 centre r={shipRadius}: touching={touching}/{draws} " +
                             $"minClearance={minClearance:F4} (no wobble = 5.0)");
            Assert.Equal(0, touching);
            Assert.True(minClearance > 3.0f,
                $"centre-hit clearance {minClearance} should stay above ~3.3 u (analytic sphere model)");
        }

        // ---- rim hit: a real fraction touches (native has these) ----
        foreach (float shipRadius in new[] { 5f, 10f, 25f, 50f })
        {
            float frac = 1.0f;                            // d/R = 1.0 -> error = 1.125
            var rng = new Random(9300 + (int)shipRadius);
            var orientation = new Orientation();
            var shipPos = sourcePos + side * (frac * Radius);
            var shipVel = new Vector3(0f, 0f, -1f) * 50f;
            int touching = 0;
            for (int i = 0; i < draws; i++)
            {
                var result = AlephExit.Apply(orientation, sourcePos, Radius, destPos, destForward,
                    shipPos, shipVel, 100f, shipRadius, bonus, rng);
                Assert.Equal(1.125f, result.Error, 6);
                if (AlephConeHitTest.Touches(destPos, destForward, Radius, result.Position, shipRadius))
                    touching++;
            }
            float fraction = touching / (float)draws;
            output.WriteLine($"P777-T4 rim d/R={frac:F1} r={shipRadius}: touching={touching}/{draws} = {fraction:P2}");
            Assert.True(fraction > 0f, $"rim hit at r={shipRadius} must leave some exits touching the cone");
        }
    }

    // ── 5. the roll spin decays under neutral controls ──────────────────────

    /// <summary>
    /// ⭐ Test 5 — the native roll bump is a turn <b>rate</b>
    /// (<c>fsship.cpp:116-118</c>), so the flight integrator ramps it back
    /// toward the neutral desired rate by at most
    /// <c>torqueMultiplier·(dt/mass)·turnTorque</c> per step
    /// (<c>shipIGC.cpp:2898-2910</c>) — it is never snapped to zero.
    /// **Fails on the unfixed base (first post-exit roll rate is 0, no spin).**
    /// </summary>
    [SkippableFact]
    public void Exit_roll_spin_decays_under_neutral_controls()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var warp = mission.Warps.First(w => w.Warp.WarpId == TellusWarpId);
        var partner = warp.Partner()!;
        var sector = mission.Sectors.First(s => s.Warps.Contains(warp));
        var warpPos = Vec(warp.Warp.Position, Vector3.Zero);
        var baseFrame = BaseFrame(partner);
        var ship = new Ship(9003, Assets.Value.Core.HullTypes[FighterHullId],
            OffAxisPosition(warpPos, baseFrame.GetBackward(), baseFrame.GetRight(), 0.5f),
            baseFrame.GetBackward() * 50f, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, sector);
        var host = new ServerHost { WarpExitRandom = new Random(555) };
        host.HitWarpEvent(ship, warp);

        float first = ship.TurnRates[ControlData.AxisRoll];
        Assert.True(first > 0f, "the exit must bump the roll turn-rate (fsship.cpp:116-118)");
        output.WriteLine($"P777-T5: post-exit roll rate={first:F4} rad/s");

        ship.Controls = ControlData.Neutral;
        ship.StateM = 0;
        double time = mission.LastUpdate;
        const float dt = 0.05f;
        float previous = first;
        for (int step = 0; step < 3; step++)
        {
            float allowed = TorqueMultiplier(ship.Velocity, ship.EffectiveMaxSpeed())
                            * (dt / ship.Mass) * ship.EffectiveTurnTorque(ControlData.AxisRoll);
            ship.ExecuteMove(time, time + dt);
            time += dt;
            float now = ship.TurnRates[ControlData.AxisRoll];
            float delta = previous - now;
            output.WriteLine($"P777-T5 step {step}: roll {previous:F4} -> {now:F4} (delta {delta:F6}, limit {allowed:F6})");
            Assert.True(delta >= -1e-5f, "neutral controls must not increase the roll rate");
            Assert.True(delta <= allowed * 1.01f + 1e-6f,
                $"roll rate fell {delta} in one step, more than the torque limit {allowed}");
            if (step == 0) Assert.True(now > 0f, "the roll spin must not be snapped to zero on the next tick");
            previous = now;
        }
    }

    // ── 6. NPC exits: measured, report-only ─────────────────────────────────

    private static Cfg Config() => new()
    {
        ArtPath = TestArtRoot.Value,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        LiveSettings = new Ls(),
        Games =
        [
            new Gm { Name = "P777", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static Vector3 ClearPoint(Sector sector, Vector3 anchor, float distance)
    {
        var warpPositions = sector.Warps
            .Where(w => w.Warp.Position is { Length: 3 })
            .Select(w => new Vector3(w.Warp.Position[0], w.Warp.Position[1], w.Warp.Position[2]))
            .ToList();
        Vector3 best = anchor; float bestMin = -1f;
        for (int xi = -1; xi <= 1; xi++) for (int yi = -1; yi <= 1; yi++) for (int zi = -1; zi <= 1; zi++)
        {
            if (xi == 0 && yi == 0 && zi == 0) continue;
            var candidate = anchor + new Vector3(xi, yi, zi) * distance;
            float min = float.MaxValue;
            foreach (var p in warpPositions) min = MathF.Min(min, (candidate - p).Length());
            if (min > bestMin) { bestMin = min; best = candidate; }
        }
        return best;
    }

    /// <summary>
    /// ⭐ Test 6 (report-only) — how often real NPC drone exits touch the
    /// destination cone under native wobble, and whether any re-warps inside
    /// 2.2 s. Six layer drones are routed to Alderan through real alephs (the
    /// P591c harness) on a seeded exit RNG, for 60 s of sim (the brief's window);
    /// the run then continues to 600 s to widen the sample, and both windows are
    /// reported. The counts feed Domingo's decision on the port-only NPC cooldown
    /// (<c>Sector.WarpCooldownSeconds</c>); nothing here asserts behaviour.
    /// </summary>
    [SkippableFact]
    public void Npc_exit_touch_rate_is_measured()
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;
        server.Boot(Config());
        server.Host.WarpExitRandom = new Random(0x7777);
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        var mission = server.Missions[0];

        var alderan = mission.Sectors.First(s => s.Id == 1052);
        var mine = mission.Core.MineTypes.Values
            .Where(mm => mm.MineRadius > 0f && mm.Power > 0f && mm.Lifespan >= 60f)
            .OrderBy(mm => mm.MineRadius).First();
        var layerType = mission.Core.DroneTypes.Values.First(d => d.PilotType == Ship.PilotTypeLayer);
        var side = mission.Sides[1].Id;
        var layWarp = alderan.Warps.First(w => w.Warp.WarpId == 2152);

        var drones = new List<Ship>();
        foreach (var startSectorId in new short[] { 1051, 1051, 1059, 1059, 1055, 1053 })
        {
            var start = mission.Sectors.First(s => s.Id == startSectorId);
            var home = start.Warps.First(w => w.Destination is not null);
            var spawn = ClearPoint(start, Vec(home.Warp.Position, Vector3.Zero),
                                   Sector.WarpVisualScale * home.Warp.Radius + 405f);
            var drone = server.SpawnNpcShip(mission, start, side, spawn, layerType.DroneTypeId);
            drone.SideId = side;
            drone.LayerLaidExpendableTypeId = mine.ExpendableTypeId;
            var layPos = Vec(layWarp.Warp.Position, Vector3.Zero)
                         - Vec(layWarp.Warp.Forward, new Vector3(0f, 0f, 1f)).Normalize()
                           * (layWarp.Warp.Radius + 0.5f * drone.HullType.Length + mine.MineRadius);
            float standoff = layWarp.Warp.Radius + 0.5f * drone.HullType.Length + mine.MineRadius;
            drone.SetLayerBuild(new Ship.LayerBuildGoal(layWarp.Warp.ClusterId, layPos, standoff + 80f));
            drones.Add(drone);
        }

        var lastSector = drones.ToDictionary(d => d.Id, d => d.Sector);
        var lastTransitTime = drones.ToDictionary(d => d.Id, _ => double.NegativeInfinity);
        int transits = 0, touching = 0, rewarpWithin22 = 0;
        int transits60 = 0, touching60 = 0, rewarp60 = 0;
        var perTransit = new List<string>();

        var prePos = drones.ToDictionary(d => d.Id, d => d.Position);
        var preVel = drones.ToDictionary(d => d.Id, d => d.Velocity);

        const int ticks60 = 20 * 60;
        const int ticks = 20 * 600;
        for (int t = 0; t < ticks; t++)
        {
            foreach (var d in drones) { prePos[d.Id] = d.Position; preVel[d.Id] = d.Velocity; }
            server.TickSim();
            foreach (var drone in drones)
            {
                if (drone.Sector is not { } now || ReferenceEquals(now, lastSector[drone.Id])) continue;
                bool inFirstMinute = t < ticks60;
                transits++;
                if (inFirstMinute) transits60++;
                double when = mission.LastUpdate;
                bool rewarp = when - lastTransitTime[drone.Id] < 2.2;
                if (rewarp) { rewarpWithin22++; if (inFirstMinute) rewarp60++; }
                lastTransitTime[drone.Id] = when;

                // The arrival aleph: in the new sector, the warp whose destination
                // is the sector the ship just left.
                var arrival = now.Warps.FirstOrDefault(w => w.Destination?.Id == lastSector[drone.Id]!.Id);
                bool touch = false;
                if (arrival is not null)
                {
                    touch = AlephConeHitTest.Touches(
                        Vec(arrival.Warp.Position, Vector3.Zero),
                        Vec(arrival.Warp.Forward, new Vector3(0f, 0f, -1f)),
                        arrival.Warp.Radius, drone.Position, 0.5f * drone.HullType.Length);
                }
                if (touch) { touching++; if (inFirstMinute) touching60++; }

                // The error the exit was drawn with: measured from the SOURCE
                // warp (the arrival warp's partner) against the pre-tick pose
                // (fsship.cpp:96-111).
                float error = float.NaN;
                if (arrival?.Partner() is { } source)
                {
                    var dp = Vec(source.Warp.Position, Vector3.Zero) - prePos[drone.Id];
                    var v = preVel[drone.Id];
                    float speed2 = v.LengthSquared();
                    if (speed2 > 0f)
                    {
                        float tt = (dp * v) / speed2;
                        float dd = (dp - v * tt).LengthSquared();
                        error = dd / (source.Warp.Radius * source.Warp.Radius) + 0.125f;
                    }
                }
                if (perTransit.Count < 200)
                    perTransit.Add($"ship {drone.Id} t={when:F1}s {lastSector[drone.Id]!.Id}->{now.Id}" +
                                   (arrival is null ? " (arrival warp not found)" : $"/{arrival.Warp.WarpId}") +
                                   $" error={error:F4} (d/R={(error - 0.125f < 0f ? 0f : MathF.Sqrt(error - 0.125f)):F3})" +
                                   (touch ? " TOUCH" : " clear") + (rewarp ? " RE-WARP<2.2s" : ""));
                lastSector[drone.Id] = now;
            }
        }

        foreach (var line in perTransit) output.WriteLine("P777-T6 " + line);
        string summary = $"P777-T6: 6 drones; first 60 s: transits={transits60} exitsTouchingCone={touching60} " +
                         $"reWarpsWithin2.2s={rewarp60}; full 600 s: transits={transits} " +
                         $"exitsTouchingCone={touching} reWarpsWithin2.2s={rewarpWithin22}; " +
                         $"laid={drones.Count(d => d.Dead)}/{drones.Count}";
        output.WriteLine(summary);
        Console.WriteLine(summary);
        Assert.Equal(ticks, ticks);   // report-only: nothing beyond "runs"
    }
}
