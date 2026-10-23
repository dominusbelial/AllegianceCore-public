using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Host = global::Allegiance.Server.ServerHost;

namespace Allegiance.Core.Tests;

/// <summary>
/// P790 — pod rescue on CONTACT. Native's friendly ship-vs-ship collision case
/// (<c>Igc/shipIGC.cpp:1023-1044</c>, inside <c>case OT_ship</c>) tests the two
/// hulls' capabilities and calls <c>igcsite-&gt;RescueShipEvent(pod, rescuer)</c>
/// <b>on the collision itself</b>: a <c>true</c> answer <c>break</c>s out of the
/// switch (no bounce, no damage, no collision effects); a <c>false</c> answer
/// falls through into the ordinary bounce, where the friendly-lifepod damage
/// exemption (<c>shipIGC.cpp:1166-1168</c>) still keeps both hulls unharmed.
/// There is <b>no centre-distance test</b> in the native rule — the hull contact
/// is the trigger.
///
/// Before P790 this port only rescued through the later tick-position proximity
/// sweep (<c>Sector.ResolveRescueEvents</c>, sphere-sum radius) and the physics
/// pass skipped every pair inside that <em>same</em> radius
/// (<c>IsRescueEligiblePair</c>, which also required the centres to be within
/// <c>0.5·LengthA + 0.5·LengthB</c> at collision time). A fast fighter's hull
/// touches the pod in the MIDDLE of the tick's swept step, so at the tick the
/// collision is detected the two centres are often still further apart than the
/// rescue radius (measured: 15-20 u apart head-on at 100 m/s while the hulls are
/// already touching) — the rescue therefore landed a tick late, or never, for a
/// passing/glancing contact. P790 moves the trigger to the contact itself.
///
/// These tests use the real <c>PCore001</c> hull 15 (Adv Fighter, the hull
/// Domingo flew, carrying <c>c_habmRescue</c>) into hull 33 (Lifepod) with the
/// real <c>.cvh</c> collision hulls attached (<see cref="CollisionHulls"/>), so
/// the detected contact is genuine convex-hull contact, not a sphere guess. The
/// production site gate (<c>ServerHost.RescueShipEvent</c> →
/// <c>FedSrv.CPP:13033-13076</c>, the "is there a friendly restart station"
/// answer) is exercised through a real <see cref="Host"/>, exactly as
/// P754CallbackTests does.
/// </summary>
public sealed class P790PodRescueOnContactTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
        => StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>XXV_simple (the LiveRescueTests topology): every side-0/1
    /// garrison sector keeps its real restart stations, so "a friendly restart
    /// station present" is a fact of the map, not a test fiction.</summary>
    private static Mission Assembly(IIgcSite site)
    {
        var core = Core();
        var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));
        var sides = new[] { new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3) };
        var m = Mission.Assemble(core, map, sides, site);
        m.Stage = MissionStage.Started;
        foreach (var sec in m.Sectors) sec.CollisionHulls = ArtHulls();
        return m;
    }

    /// <summary>The production collision-hull loader shape
    /// (<c>Server.Lifecycle.cs</c>): <c>&lt;artwork&gt;/&lt;model&gt;.cvh</c>.</summary>
    private static CollisionHullCache ArtHulls() => new(name =>
    {
        var p = Path.Combine(ArtRoot, name + ".cvh");
        return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
    });

    /// <summary>The site gate. <c>RescueDestination</c> is the host's
    /// <c>FindFriendlyStation</c> seam (P754b: <c>FedSrv.CPP:13060-13075</c>
    /// returns the native <c>pstation ? … : false</c>): <c>true</c> = a friendly
    /// restart station exists, <c>false</c> = native's refusal.</summary>
    private static Host ProductionSite(bool restartStationExists)
    {
        var host = new Host { RescueDestination = _ => restartStationExists };
        return host;
    }

    private static bool MissionHasFriendlyRestartStation(Mission m)
        => m.Sectors.SelectMany(s => s.StationRuntimes).Any(st =>
            st.SideId == 0 && st.Type.HasCapability(DataStationTypeIGC.SabmRestart));

    /// <summary>A spot in the no-station fly sector far from every rock/warp/
    /// station (the ShipShipCollisionTests placement, tightened to include
    /// stations) so the two synthetic ships interact only with each other.</summary>
    private static (Vector3 Spot, Sector Sector) FarSpot(Mission m, float margin = 4500f)
    {
        var dirs = new[]
        {
            new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 1, 0),
            new Vector3(0, -1, 0), new Vector3(0, 0, 1), new Vector3(0, 0, -1),
        };
        foreach (var sector in m.Sectors)
        {
            var occupied = new List<Vector3>();
            foreach (var a in sector.Mineable)
                if (a.Data.Position is { Length: 3 } p)
                    occupied.Add(new Vector3(p[0], p[1], p[2]));
            foreach (var w in sector.Warps)
                if (w.Warp.Position is { Length: 3 } p)
                    occupied.Add(new Vector3(p[0], p[1], p[2]));
            foreach (var st in sector.StationRuntimes)
                occupied.Add(st.Center);
            occupied.Add(Vector3.Zero);
            Vector3 anchor = occupied.Count == 0 ? Vector3.Zero
                : new Vector3(occupied.Average(p => p.X), occupied.Average(p => p.Y), occupied.Average(p => p.Z));
            foreach (var d in dirs)
                foreach (var dist in new[] { 8500f, 12000f, 16000f })
                {
                    var cand = anchor + d * dist;
                    if (occupied.All(p => (p - cand).Length() > margin))
                        return (cand, sector);
                }
        }
        throw new InvalidOperationException("corpus map too crowded to place a clean synthetic pair");
    }

    private const float Dt = 0.05f;                 // 20 updates/s, the rig rate

    private static DataHullTypeIGC AdvFighter(StaticCore core) => core.HullTypes[15];   // quizfigv2
    private static DataHullTypeIGC Lifepod(StaticCore core) => core.HullTypes[33];      // dreghpod

    private static Ship Place(Mission m, Sector sector, short id, DataHullTypeIGC hull,
        Vector3 pos, Vector3 velocity, short side)
    {
        var ship = new Ship(id, hull, pos, velocity, Orientation.Identity) { SideId = side };
        ship.StateM = ShipControlState.CoastButton;  // ballistic: keep the velocity
        m.PlaceShip(ship, sector);
        return ship;
    }

    /// <summary>The contact geometry: a stationary friendly pod at the spot and
    /// the fighter behind it on the −z axis (its hull's long axis — the real
    /// Adv Fighter model is elongated along z, <c>quizfigv2</c>) flying straight
    /// at it along +z at combat speed. <paramref name="lateral"/> offsets the
    /// approach to make it a glancing contact.</summary>
    private static (Ship Pod, Ship Fighter, Sector Sector, float Threshold) HeadOn(
        Mission m, float speed, float lateral = 0f, float startDistance = 20f, short podSide = 0)
    {
        var core = m.Core;
        var (spot, sector) = FarSpot(m);
        var podHull = Lifepod(core);
        var fighterHull = AdvFighter(core);
        var pod = Place(m, sector, 9001, podHull, spot, Vector3.Zero, podSide);
        var fighter = Place(m, sector, 9002, fighterHull,
            spot + new Vector3(lateral, 0f, -startDistance), new Vector3(0f, 0f, speed), 0);
        return (pod, fighter, sector, 0.5f * fighterHull.Length + 0.5f * podHull.Length);
    }

    /// <summary>Pump until the ship-ship physics pass reports a hull contact, so
    /// each test asserts on the exact tick the collision is detected. Returns
    /// the tick count, or -1 when no contact is ever reported.</summary>
    private static int PumpToContact(Mission m, Sector sector, int max = 40)
    {
        double now = m.LastUpdate;
        for (int i = 0; i < max; i++)
        {
            now += Dt;
            m.Update(now);
            if (sector.LastShipShipEvents > 0) return i;
        }
        return -1;
    }

    /// <summary>Report/assertion helper: the centre distance at the FIRST hull
    /// contact the physics pass resolves for this pair. It sweeps the same
    /// <see cref="CollisionDetector"/> the pass uses — whose step starts at the
    /// post-move position <c>q = p + v·dt</c> because the sector moves its
    /// models before its collision block — and evaluates the two centres at the
    /// reported contact time. That is the distance the old
    /// <c>IsRescueEligiblePair</c> centre gate was compared against.</summary>
    private static (float Distance, float T) ContactDistance(Ship a, Ship b)
    {
        var det = new CollisionDetector();
        foreach (var s in new[] { b, a })                       // pod first: the pass's _models order
            det.Add(new Collider(s, s.Position + s.Velocity * Dt, s.Velocity, s.Orientation,
                s.Sector!.CollisionHulls!.Resolve(s.HullType.ModelName), 0.5f * s.HullType.Length,
                isStatic: false, HitShape.Sphere));
        var events = det.Detect(Dt);
        var e = Assert.Single(events);
        var pa = a.Position + a.Velocity * Dt + a.Velocity * e.TCollision;
        var pb = b.Position + b.Velocity * Dt + b.Velocity * e.TCollision;
        return ((pa - pb).Length(), e.TCollision);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (1) ⭐ head-on contact → rescue THAT tick, no bounce, no damage
    // ═══════════════════════════════════════════════════════════════════════
    /// <summary>⭐ The brief's headline case: an Adv Fighter (hull 15, hull 15's
    /// real <c>quizfigv2</c> convex hull) at combat speed (100 m/s) flies head-on
    /// into a stationary friendly Lifepod (hull 33, <c>dreghpod</c>) and the pod
    /// is rescued <b>on the tick the hull contact is detected</b> — no bounce, no
    /// damage, one queued pair. On the pre-P790 code this fails: at that tick the
    /// centres are still 20 u apart (further than the 11.45 u rescue radius), so
    /// nothing is queued until a later tick's proximity sample.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Adv_fighter_head_on_contact_rescues_the_pod_that_tick_with_no_bounce_or_damage()
    {
        Require.Artwork();
        var mission = Assembly(ProductionSite(restartStationExists: true));
        Assert.True(MissionHasFriendlyRestartStation(mission),
            "setup: the map must provide side 0 a friendly restart station (FedSrv.CPP:13060-13064)");

        var (pod, fighter, sector, threshold) = HeadOn(mission, speed: 100f, startDistance: 20f);
        float preVz = fighter.Velocity.Z;
        float prePodFrac = pod.Fraction, preFighterFrac = fighter.Fraction;
        float preDistance = (fighter.Position - pod.Position).Length();

        // The hull contact happens MID-STEP: at the contact tick's positions the
        // centres are still beyond the old rescue radius (the sweep closes the
        // last ~10 u during the step), so no proximity pass could have rescued
        // them at that tick — the rescue can only come from the contact itself.
        // Measured here with the pass's own detector for the report.
        var (contactDistance, tContact) = ContactDistance(fighter, pod);
        Assert.True(contactDistance <= threshold,
            $"the hull contact must be detected within the sphere gate: contact distance {contactDistance:0.###} " +
            $"vs sphere sum {threshold:0.##}");

        int tick = PumpToContact(mission, sector);
        Assert.Equal(0, tick);               // the first Update resolves the contact; -1 would fail here too
        Assert.True(preDistance > threshold,
            $"setup: the contact tick must start with the centres ({preDistance:0.##}) beyond the old " +
            $"sphere-sum threshold ({threshold:0.##}) — otherwise the proximity path could mask the bug");

        // Rescued exactly once, this tick, as the native (pod, rescuer) pair.
        var rescues = sector.DrainRescues();
        var pair = Assert.Single(rescues);
        Assert.Same(pod, pair.Rescuee);
        Assert.Same(fighter, pair.Rescuer);

        // No bounce: the native `break` never reaches the physics block.
        Assert.True(MathF.Abs(fighter.Velocity.Z - preVz) < 0.5f,
            $"the rescued fighter must not be bounced: vz {preVz:0.##} -> {fighter.Velocity.Z:0.##}");
        Assert.True(MathF.Abs(pod.Velocity.Length()) < 0.5f, "the rescued pod must not be launched");
        Assert.Equal(0, sector.LastShipShipResolved);        // no physics resolution at all

        // No damage to either hull (the rescue pre-empts it; the lifepod
        // exemption would cover the bounce path too, shipIGC.cpp:1166-1168).
        Assert.Equal(prePodFrac, pod.Fraction, 6);
        Assert.Equal(preFighterFrac, fighter.Fraction, 6);
        Assert.False(pod.Dead || fighter.Dead);

        Console.WriteLine($"P790-CONTACT-HEADON: hull 15 at 100 m/s -> hull 33 rescued on tick {tick} " +
                          $"(centres {preDistance:0.##} u at the contact tick > old threshold {threshold:0.##} u; " +
                          $"actual hull-contact distance {contactDistance:0.###} u at t={tContact:0.####} s); " +
                          $"fighter vz {preVz:0.##} unchanged, fractions {pod.Fraction:0.####}/{fighter.Fraction:0.####}, physics resolves 0.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (2) ⭐ glancing contact → rescue THAT tick
    // ═══════════════════════════════════════════════════════════════════════
    /// <summary>⭐ The same rule at a glancing angle (6 u lateral offset): the
    /// hulls still touch, so the pod is rescued on the contact tick with no
    /// bounce and no damage. On the pre-P790 code the pair was only rescued once
    /// a tick-position sample happened to fall inside the sphere radius (in this
    /// geometry it caught up a tick later; a tick lattice can also straddle a
    /// narrow glancing window and miss it).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Adv_fighter_glancing_contact_rescues_the_pod_that_tick()
    {
        Require.Artwork();
        var mission = Assembly(ProductionSite(restartStationExists: true));
        var (pod, fighter, sector, threshold) = HeadOn(mission, speed: 100f, lateral: 6f, startDistance: 24f);
        float preVx = fighter.Velocity.X, preVz = fighter.Velocity.Z;
        float prePodFrac = pod.Fraction;

        int tick = PumpToContact(mission, sector);
        Assert.True(tick >= 0, "setup: the glancing approach must reach hull contact");

        var pair = Assert.Single(sector.DrainRescues());
        Assert.Same(pod, pair.Rescuee);
        Assert.Same(fighter, pair.Rescuer);

        Assert.True(MathF.Abs(fighter.Velocity.X - preVx) < 0.5f, "no sideways kick from the contact");
        Assert.True(MathF.Abs(fighter.Velocity.Z - preVz) < 0.5f, "no bounce along the approach");
        Assert.True(MathF.Abs(pod.Velocity.Length()) < 0.5f, "the rescued pod must not be launched");
        Assert.Equal(0, sector.LastShipShipResolved);
        Assert.Equal(prePodFrac, pod.Fraction, 6);

        Console.WriteLine($"P790-CONTACT-GLANCE: glancing hull contact (6 u offset) rescued on tick {tick}; " +
                          $"velocity ({fighter.Velocity.X:0.##},{fighter.Velocity.Z:0.##}) intact, pod fraction {pod.Fraction:0.####}, physics resolves 0.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (3) refused rescue (no friendly restart station) → native fall-through bounce
    // ═══════════════════════════════════════════════════════════════════════
    /// <summary>⭐ The refused leg: the SAME head-on geometry with the site
    /// answering native's <c>false</c> (<c>FedSrv.CPP:13075</c> — no friendly
    /// restart station). Nothing is rescued and the code falls through to the
    /// ordinary elastic bounce — while the friendly-lifepod exemption keeps BOTH
    /// hulls at full health (<c>shipIGC.cpp:1166-1168</c>). Pre-P790 the physics
    /// pass skipped the pair for being inside the sphere radius, so no bounce
    /// ever happened and the ships passed through each other.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Refused_rescue_falls_through_to_the_bounce_with_no_damage_to_either()
    {
        Require.Artwork();
        var mission = Assembly(ProductionSite(restartStationExists: false));
        var (pod, fighter, sector, _) = HeadOn(mission, speed: 100f, startDistance: 20f);
        float preVz = fighter.Velocity.Z;
        float prePodFrac = pod.Fraction, preFighterFrac = fighter.Fraction;

        int tick = PumpToContact(mission, sector);
        Assert.True(tick >= 0, "setup: the fighter must reach hull contact");

        // Refused: nothing queued (before or after the bounce).
        Assert.Empty(sector.DrainRescues());

        // Native's fall-through: real physics — the bounce changes the fighter's
        // normal velocity (it is the moving body; the pod is pushed too).
        Assert.True(sector.LastShipShipResolved >= 1,
            "a refused rescue must fall through to the ordinary collision resolution");
        Assert.True(MathF.Abs(fighter.Velocity.Z - preVz) > 1f || MathF.Abs(pod.Velocity.Z) > 1f,
            $"the refused pair must BOUNCE (fighter vz {preVz:0.##} -> {fighter.Velocity.Z:0.##}, " +
            $"pod vz {pod.Velocity.Z:0.##})");

        // ... and the friendly-lifepod exemption keeps both hulls unharmed.
        Assert.Equal(prePodFrac, pod.Fraction, 6);
        Assert.Equal(preFighterFrac, fighter.Fraction, 6);
        Assert.False(pod.Dead || fighter.Dead);

        Console.WriteLine($"P790-REFUSED: no restart station -> refused on contact, native fall-through bounced the pair " +
                          $"(fighter vz {preVz:0.##} -> {fighter.Velocity.Z:0.##}); fractions {pod.Fraction:0.####}/{fighter.Fraction:0.####} (no damage).");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (4) enemy pod → never rescued, normal collision
    // ═══════════════════════════════════════════════════════════════════════
    /// <summary>⭐ The friendly gate (<c>shipIGC.cpp:1023-1026</c>): a
    /// different-side pod is never a rescue pair no matter how hard the fighter
    /// hits it — it collides normally (physics resolves) and, unlike the
    /// friendly leg, it is not protected by the friendly-lifepod damage
    /// exemption.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Enemy_pod_is_never_rescued_and_collides_normally()
    {
        Require.Artwork();
        var mission = Assembly(ProductionSite(restartStationExists: true));
        var (pod, fighter, sector, _) = HeadOn(mission, speed: 100f, startDistance: 20f, podSide: 1);
        float preVz = fighter.Velocity.Z;
        float prePodFrac = pod.Fraction;

        int tick = PumpToContact(mission, sector);
        Assert.True(tick >= 0, "setup: the enemy pod must be hit");

        Assert.Empty(sector.DrainRescues());
        Assert.True(sector.LastShipShipResolved >= 1, "an enemy pod must collide normally (physics resolves)");
        // The enemy pod takes REAL damage (no friendly-lifepod exemption). A 25 HP
        // pod is destroyed by the 100 m/s impact, and native then skips the
        // position/velocity correction (shipIGC.cpp:963-966) — so the physics
        // evidence is the damage/death, not necessarily a velocity change.
        Assert.True(pod.Dead || pod.Fraction < prePodFrac - 1e-4f,
            $"the enemy pod must take real collision damage (fraction {prePodFrac:0.####} -> {pod.Fraction:0.####}, dead={pod.Dead})");
        Assert.True(pod.Dead || MathF.Abs(fighter.Velocity.Z - preVz) > 1f || MathF.Abs(pod.Velocity.Z) > 1f,
            "the enemy pod collision must resolve as a real collision (damage/bounce)");

        Console.WriteLine($"P790-ENEMYPOD: different-side pod never rescued (0 queued) on tick {tick}; " +
                          $"physics resolved {sector.LastShipShipResolved} collision(s), fighter vz {preVz:0.##} -> {fighter.Velocity.Z:0.##}.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // (5) determinism — serial vs parallel, several thread counts
    // ═══════════════════════════════════════════════════════════════════════
    /// <summary>⭐ P667/P790 determinism gate: the rescue set (and its order) is
    /// identical with <see cref="ParallelSectorSim"/> off and on at 1/2/4/8
    /// workers. The scene mixes both triggers — three contact groups (two
    /// head-on, one glancing), one proximity-only group (the pair is inside the
    /// sphere-sum radius but the hulls never touch) and one pod with two
    /// rescue-capable ships touching it — plus an enemy pod (head-on contact)
    /// that must never be rescued. The contact trigger runs inside the physics
    /// pass (whose detector ordering is already thread-independent) and both
    /// triggers share the per-tick one-rescue-per-ship latch, so no pod is ever
    /// booked twice.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Serial_and_parallel_produce_identical_rescue_sets_for_any_thread_count()
    {
        Require.Artwork();
        const int Ticks = 20;

        List<(short Rescuee, short Rescuer)> Run(bool parallel, int threads)
        {
            ParallelSectorSim.Enabled = parallel;
            ParallelSectorSim.ThreadCount = threads;
            try
            {
                var mission = Assembly(ProductionSite(restartStationExists: true));
                var (spot, sector) = FarSpot(mission);
                var core = mission.Core;
                var podHull = Lifepod(core);
                var fighterHull = AdvFighter(core);

                // Six groups 60 u apart along +x so groups never interact:
                //   0, 1  contact (head-on, 100 m/s)
                //   2     contact (glancing, 6 u offset)
                //   3     proximity-only: 11 u apart at rest — inside the 11.45 u
                //         sphere sum, hulls never touching (contact needs ~10.4 u)
                //   4     enemy pod: never a rescue pair
                //   5     ONE pod with TWO rescue-capable ships touching it at
                //         rest: the pod must be rescued exactly once, by the
                //         first pair in scan order, for any thread count (the
                //         old serial loop could book a pod twice here while the
                //         parallel latch booked it once — a real divergence).
                for (int k = 0; k < 6; k++)
                {
                    var c = spot + new Vector3(60f * k, 0f, 0f);
                    short podSide = k == 4 ? (short)1 : (short)0;
                    // pod first (lower id) so the (i,j) scan order is stable
                    Place(mission, sector, (short)(9100 + 2 * k), podHull, c, Vector3.Zero, podSide);
                    if (k == 2)
                        Place(mission, sector, (short)(9101 + 2 * k), fighterHull,
                            c + new Vector3(6f, 0f, -24f), new Vector3(0f, 0f, 100f), 0);
                    else if (k == 3)
                        Place(mission, sector, (short)(9101 + 2 * k), fighterHull,
                            c + new Vector3(0f, 0f, -11f), Vector3.Zero, 0);
                    else if (k == 5)
                    {
                        Place(mission, sector, (short)(9101 + 2 * k), fighterHull,
                            c + new Vector3(0f, 0f, -5f), Vector3.Zero, 0);
                        Place(mission, sector, 9112, fighterHull,
                            c + new Vector3(0f, 0f, 5f), Vector3.Zero, 0);
                    }
                    else
                        Place(mission, sector, (short)(9101 + 2 * k), fighterHull,
                            c + new Vector3(0f, 0f, -20f), new Vector3(0f, 0f, 100f), 0);
                }

                var pairs = new List<(short, short)>();
                double now = mission.LastUpdate;
                for (int t = 0; t < Ticks; t++)
                {
                    now += Dt;
                    mission.Update(now);
                    foreach (var (rescuee, rescuer) in sector.DrainRescues())
                    {
                        pairs.Add((rescuee.Id, rescuer.Id));
                        // Emulate the host's drain (Server.PerformRescue): the pod is
                        // docked at a friendly station, leaves the flying set and is
                        // no longer a rescue or collision body — exactly what makes a
                        // second rescue of the same pod impossible in production.
                        rescuee.StationId = 1;
                        sector.Remove(rescuee);
                    }
                }
                return pairs;
            }
            finally { ParallelSectorSim.Reset(); }
        }

        var serial = Run(parallel: false, threads: 1);
        // One rescue per pod, never twice; the two contact groups, the glancing
        // contact group, the proximity-only group and the two-rescuer group are
        // all rescued (five pods); the enemy pod (9108) never is.
        Assert.Equal(5, serial.Count);
        Assert.Equal(serial.Count, serial.Select(p => p.Item1).Distinct().Count());
        Assert.Contains((short)9100, serial.Select(p => p.Item1));
        Assert.Contains((short)9102, serial.Select(p => p.Item1));
        Assert.Contains((short)9104, serial.Select(p => p.Item1));
        Assert.Contains((short)9106, serial.Select(p => p.Item1));
        Assert.Contains((short)9110, serial.Select(p => p.Item1));
        Assert.DoesNotContain(serial, p => p.Item1 == 9108);

        foreach (var (parallel, threads) in new[] { (true, 1), (true, 2), (true, 4), (true, 8) })
        {
            var other = Run(parallel, threads);
            Assert.Equal(serial, other);
        }

        Console.WriteLine($"P790-DETERMINISM: rescue set {string.Join(", ", serial.Select(p => $"{p.Item1}->{p.Item2}"))} " +
                          $"identical for serial and parallel t=1/2/4/8 over {Ticks} ticks.");
    }
}
