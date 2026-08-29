using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using SimModel = Allegiance.Core.Sim.Model;
using Xunit.Abstractions;

namespace Allegiance.Core.Tests;

/// <summary>
/// Verification of the slice that connects <see cref="Ship"/> into
/// <see cref="Sector"/>/<see cref="Mission"/> so a mission tick physically
/// moves ships. The reference is <c>CclusterIGC::Update</c>'
/// models loop — <c>l-&gt;data()-&gt;Update(now)</c>
/// (<c>src/Igc/clusterIGC.cpp:241-253</c>) — ported exactly; the station/ship/
/// collision blocks are out of scope. The three proofs this slice must meet are
/// inlined below: the tick changes no physics, an empty mission ticks
/// harmlessly, and sector isolation holds.
/// </summary>
public class ShipInSectorTests
{
    private readonly ITestOutputHelper _output;

    public ShipInSectorTests(ITestOutputHelper output) => _output = output;

    private static readonly Lazy<DataHullTypeIGC> RepresentativeHull = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            var core = StaticCore.Load(file);
            var ordered = core.HullTypes.Values.OrderBy(h => h.HullId).ToList();
            if (ordered.Count == 0) continue;
            return ordered[0];
        }
        throw new InvalidOperationException("no static core with a hull found in corpus");
    });

    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    private static IReadOnlyList<SideSpec> SideSpecs()
    {
        var core = RepresentativeCore.Value;
        var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(1).ToList();
        return [new SideSpec(0, "Team 1", civs[0].CivilizationId)];
    }

    /// <summary>Assemble a started mission bound to a null site.</summary>
    private static Mission NewStartedMission()
    {
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, SideSpecs(), new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    private static Ship NewShip() => new(
        shipId: 9001,
        hull: RepresentativeHull.Value,
        position: new Vector3(1.5f, -2.25f, 3.75f),
        velocity: Vector3.Zero,
        orientation: Orientation.Identity);

    private static void ApplyControls(Ship s)
    {
        s.Controls = new ControlData(Yaw: 0.8123f, Pitch: -0.5544f, Roll: 0.999f, Throttle: 0.872f);
        s.StateM = ShipControlState.ForwardButton | ShipControlState.RightButton;
    }

    /// <summary>Reference driver for the mission's chunk split, so the test can
    /// prove the Sector/Model wrapper adds no physics of its own.</summary>
    private static void SimulateLikeTheTick(Ship s, double deltaT)
    {
        int n = 1 + (int)(deltaT / Mission.TimeIncrement);
        double prev = 0;
        for (int i = 1; i <= n; i++)
        {
            double now = deltaT * ((double)i / n);
            s.ExecuteMove(prev, now);
            s.Position += s.Velocity * (float)(now - prev);
            prev = now;
        }
    }

    /// <summary>Like <see cref="SimulateLikeTheTick"/> but the timeline starts at
    /// <paramref name="start"/> instead of 0 — the reference for a ship seeded
    /// to sim-time <paramref name="start"/> (the ship-clock fix).</summary>
    private static void SimulateSeeded(Ship s, double start, double deltaT)
    {
        int n = 1 + (int)(deltaT / Mission.TimeIncrement);
        double prev = start;
        for (int i = 1; i <= n; i++)
        {
            double now = start + deltaT * ((double)i / n);
            s.ExecuteMove(prev, now);
            s.Position += s.Velocity * (float)(now - prev);
            prev = now;
        }
    }

    // ── the ship-clock fix (BOT.md latent bug) ─────────────────────────────

    /// <summary>
    /// A ship placed into a sector long after boot (mission already at sim-time
    /// 120 s) must not take an oversized first <c>dt</c> equal to the mission
    /// age — the <c>Model._lastUpdate == 0</c> bug BOT.md records. <c>Mission.PlaceShip</c>
    /// now seeds the clock to the placement sim-time, so with non-neutral
    /// controls the ship accelerates for just the elapsed chunk and travels the
    /// same small distance an identically-seeded reference does — it does not
    /// teleport across the sector. Bit-identical to the independent seeded
    /// reference (same physics, same cadence).
    /// </summary>
    [Fact]
    public void Ship_placed_late_does_not_jump()
    {
        const double bootAge = 120.0;          // mission already 120 s in
        const double deltaT = 0.625;           // 1 + (int)(0.625/0.25) = 3 chunks
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];

        mission.Update(bootAge);               // advance the mission to 120 s
        var ship = NewShip();                  // placed now, clock must seed to 120
        ApplyControls(ship);                   // non-neutral controls (the masking case)
        mission.PlaceShip(ship, sector);
        mission.Update(bootAge + deltaT);      // one tick spanning 3 chunks

        // The independent reference: a ship seeded to 120 with the same controls,
        // rolled forward over the identical chunked cadence by the test itself.
        var reference = NewShip();
        ApplyControls(reference);
        reference.InitClock(bootAge);
        SimulateSeeded(reference, bootAge, deltaT);

        Assert.Equal(reference.Velocity, ship.Velocity);
        Assert.Equal(reference.Position, ship.Position);
        Assert.Equal(reference.Orientation.GetForward(), ship.Orientation.GetForward());

        // Crucially, the ship has NOT teleported: displacement over ~0.625 s of
        // thrust is metres, not hundreds of kilometres from a 120 s step.
        var displaced = (ship.Position - new Vector3(1.5f, -2.25f, 3.75f)).Length();
        Assert.True(displaced < 100f, $"a late-placed ship must not jump; displaced {displaced} m");
    }

    // ── the tick does not change the physics ──────────────────────────────

    /// <summary>
    /// A ship driven through a real <see cref="Mission.Update"/> ends with
    /// velocity, orientation and position bit-identical to the reference that
    /// replays the exact same chunked <see cref="ExecuteMove"/> + position
    /// integration. The Mission/Sector/Model wrapper therefore introduces no
    /// force, no extra physics and no double-counting — it only decides the
    /// cadence. Multi-chunk case (deltaT = 0.908 → n = 4).
    /// </summary>
    [Fact]
    public void Mission_tick_does_not_change_the_physics_multi_chunk()
    {
        const double deltaT = 0.908;                 // 1 + (int)(0.908/0.25) = 1+3 = 4 chunks
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewShip();
        ApplyControls(ship);
        mission.PlaceShip(ship, sector);

        mission.Update(deltaT);

        var reference = NewShip();
        ApplyControls(reference);
        SimulateLikeTheTick(reference, deltaT);

        Assert.Equal(reference.Velocity, ship.Velocity);
        Assert.Equal(reference.Orientation.GetForward(), ship.Orientation.GetForward());
        Assert.Equal(reference.Orientation.GetUp(), ship.Orientation.GetUp());
        Assert.Equal(reference.Orientation.GetRight(), ship.Orientation.GetRight());
        Assert.Equal(reference.Position, ship.Position);
    }

    /// <summary>Single-chunk case: with deltaT &lt; 0.25 (n == 1) the tick is a
    /// lone ExecuteMove over [0, deltaT] + one integration — identical to a
    /// direct call.</summary>
    [Fact]
    public void Single_chunk_tick_matches_a_lone_ExecuteMove_plus_integration()
    {
        const double deltaT = 0.093;
        var mission = NewStartedMission();
        var ship = NewShip();
        ApplyControls(ship);
        mission.PlaceShip(ship, mission.Sectors[0]);
        mission.Update(deltaT);

        var direct = NewShip();
        ApplyControls(direct);
        direct.ExecuteMove(0, deltaT);
        direct.Position += direct.Velocity * (float)deltaT;

        Assert.Equal(direct.Velocity, ship.Velocity);
        Assert.Equal(direct.Position, ship.Position);
        Assert.Equal(direct.Orientation.GetForward(), ship.Orientation.GetForward());
    }

    // ── an empty mission ticks harmlessly ─────────────────────────────────

    [Fact]
    public void Empty_mission_tick_touches_nothing()
    {
        var mission = NewStartedMission();
        Assert.All(mission.Sectors, s => Assert.Empty(s.Models));   // no models anywhere
        var before = mission.Sectors.Select(s => s.UpdateCount).ToArray();

        mission.Update(0.856);                      // 4 chunks, several sectors, no models anywhere

        for (int i = 0; i < mission.Sectors.Count; i++)
        {
            Assert.Equal(before[i] + 4, mission.Sectors[i].UpdateCount); // still ticks the sector
            Assert.Empty(mission.Sectors[i].Models);
            Assert.Equal(0.856, mission.Sectors[i].LastUpdate);
        }
        Assert.Equal(0.856, mission.LastUpdate);
    }

    /// <summary>A sector with a model removed also ticks harmlessly.</summary>
    [Fact]
    public void Removed_model_is_not_ticked()
    {
        var mission = NewStartedMission();
        var ship = NewShip();
        ApplyControls(ship);
        var sector = mission.Sectors[0];
        mission.PlaceShip(ship, sector);
        Assert.True(sector.Remove(ship), "ship must be removable");

        mission.Update(0.503);
        Assert.Equal(Vector3.Zero, ship.Velocity);
        Assert.Equal(new Vector3(1.5f, -2.25f, 3.75f), ship.Position);   // never moved
        Assert.Empty(sector.Models);
    }

    // ── sector isolation ──────────────────────────────────────────────────

    /// <summary>A ship in sector A is untouched when sector B ticks on its own.</summary>
    [Fact]
    public void Ship_in_A_is_untouched_when_B_ticks()
    {
        var mission = NewStartedMission();
        Assert.True(mission.Sectors.Count >= 2, "test map must have ≥ 2 sectors");
        var a = mission.Sectors[0];
        var b = mission.Sectors[1];

        var shipA = NewShip();
        ApplyControls(shipA);
        mission.PlaceShip(shipA, a);

        var pos0 = shipA.Position;
        var vel0 = shipA.Velocity;
        var fwd0 = shipA.Orientation.GetForward();

        b.Update(0.912);                             // only sector B ticks

        Assert.Equal(pos0, shipA.Position);          // A's ship unmoved
        Assert.Equal(vel0, shipA.Velocity);
        Assert.Equal(fwd0, shipA.Orientation.GetForward());
        Assert.Equal(0L, a.UpdateCount);             // A itself was never ticked either
        Assert.Equal(1L, b.UpdateCount);
    }

    // ── the chunked cadence reaches models ────────────────────────────────

    /// <summary>With a deltaT spanning several increments, a model's Update is
    /// called once per chunk (n times), not once — reproduced through the real
    /// mission path, counted by a model subclass.</summary>
    [Fact]
    public void Model_sees_n_updates_not_one()
    {
        const double deltaT = 1.077;                 // 1 + (int)(1.077/0.25) = 1+4 = 5 chunks
        const int expectedN = 5;
        var mission = NewStartedMission();
        var counter = new CountingModel(id: 911, position: Vector3.Zero);
        mission.Sectors[0].Add(counter);

        mission.Update(deltaT);

        Assert.Equal(expectedN, counter.UpdateCalls);
        Assert.Equal(0.0, counter.LastSeenFirstUpdate - (deltaT / expectedN), 9);  // first chunk time
    }

    /// <summary>A ship that travels is the strongest witness that n updates
    /// happened: with forward+right thrust, splitting into n sub-steps yields a
    /// measurably different velocity than one big step (the cadence, not the
    /// wrapper).</summary>
    [Fact]
    public void Ship_reflects_the_full_chunked_cadence()
    {
        const double deltaT = 0.903;                 // n = 1 + (int)(0.903/0.25) = 4 chunks
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];

        var ship = NewShip();
        ApplyControls(ship);
        mission.PlaceShip(ship, sector);
        mission.Update(deltaT);

        Assert.Equal(4L, sector.UpdateCount);        // the sector ticked 4 times
        Assert.True(ship.Velocity.LengthSquared() > 0f, "ship must have accelerated");
        Assert.True(ship.Position != new Vector3(1.5f, -2.25f, 3.75f), "ship must have moved");

        // Position travelled ≈ ∫v dt; forward dominated ⇒ displacement along -z.
        var displaced = ship.Position - new Vector3(1.5f, -2.25f, 3.75f);
        Assert.True(displaced.Z < 0f, $"forward thrust must displace along -z; got Z={displaced.Z}");
    }

    /// <summary>A model's forward-clock guard: a second call at an unchanged or
    /// earlier time does not double-run it.</summary>
    [Fact]
    public void Model_backwards_or_repeated_time_is_a_noop()
    {
        var mission = NewStartedMission();
        var counter = new CountingModel(912, Vector3.Zero);
        mission.Sectors[0].Add(counter);

        mission.Update(0.504);                        // 3 chunks → 3 calls
        Assert.Equal(3, counter.UpdateCalls);

        counter.Update(0.502);                        // backwards: no-op
        Assert.Equal(3, counter.UpdateCalls);
        counter.Update(0.504);                        // repeated: no-op
        Assert.Equal(3, counter.UpdateCalls);
        counter.Update(0.810);                        // forward: runs once
        Assert.Equal(4, counter.UpdateCalls);
    }

    // ── carry-forward: the chunk-divergence measurement through the tick ──

    /// <summary>
    /// Carry-forward of the flight model's divergence measurement
    /// (<c>ShipFlightModelTests.Measure_single_vs_split_step_divergence</c>)
    /// now through the real mission tick: a ship stepped by a mission over a
    /// fixed interval vs the same interval as one big <c>ExecuteMove</c> +
    /// integration. Legitimately divergent (per-sub-step re-evaluation of turn
    /// rates, torque and throttle) — this is a <b>measurement</b>, printed and
    /// reported, not an assertion of equality.
    /// </summary>
    [Fact]
    public void Measure_mission_tick_vs_one_big_step_divergence()
    {
        const double total = 0.905216;               // non-power-of-two, 4 chunks (0.9052/0.25 = 3.62 → n=4)

        // One big step: a lone ExecuteMove over [0, total], plus the position step.
        var single = NewShip();
        ApplyControls(single);
        single.ExecuteMove(0, total);
        single.Position += single.Velocity * (float)total;

        // The same interval driven through the mission's chunked cadence.
        var mission = NewStartedMission();
        var ship = NewShip();
        ApplyControls(ship);
        mission.PlaceShip(ship, mission.Sectors[0]);
        mission.Update(total);

        float dv = (single.Velocity - ship.Velocity).Length();
        float dp = (single.Position - ship.Position).Length();
        float df = (single.Orientation.GetForward() - ship.Orientation.GetForward()).Length();
        _output.WriteLine(
            $"CHUNKDIV deltaV={dv:R} deltaPos={dp:R} deltaForward={df:R} (total={total} s, 4 chunks) hull.Speed={RepresentativeHull.Value.Speed}");

        Assert.True(float.IsFinite(dv) && float.IsFinite(dp) && float.IsFinite(df));
        Assert.True(dv > 0f, "the split cadence is expected to diverge measurably from one big step");
    }

    /// <summary>A test-only model that counts how often its <c>OnUpdate</c> runs
    /// (an observer for the chunk-cadence test, not part of the shipped API).</summary>
    private sealed class CountingModel : SimModel
    {
        public CountingModel(short id, Vector3 position) : base(id, position) { }

        public int UpdateCalls { get; private set; }
        public double LastSeenFirstUpdate { get; private set; }

        protected override void OnUpdate(double now, double dt)
        {
            if (UpdateCalls == 0) LastSeenFirstUpdate = now;
            UpdateCalls++;
        }
    }
}
