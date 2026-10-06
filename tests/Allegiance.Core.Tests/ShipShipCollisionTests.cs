using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P354 — real ship-vs-ship elastic collision physics (elastic bounce + push +
/// collision damage). This is the general two-finite-mass generalisation of the
/// P254 ship-vs-asteroid pass: <see cref="Sector.ResolveShipShipCollisions"/>
/// ports the native <c>OT_ship</c> branch of <c>CshipIGC::HandleCollision</c>
/// (<c>shipIGC.cpp:1023-1202</c>). The suite proves, through real
/// <see cref="Sector.Update"/> ticks on a real mission (the same live-wire
/// convention the <c>CombatDamageTests</c>/<c>CombatShieldTests</c>/
/// <c>ShipAsteroidCollisionTests</c> families use):
/// <list type="number">
/// <item>A heavy-vs-light head-on: BOTH ships' velocities change (unlike the
/// asteroid case where the rock never moves), total linear momentum is
/// conserved, and the heavier ship deflects the lighter one more.</item>
/// <item>The tactic Domingo remembered: a faster/heavier ship bumping a slower
/// (stationary) one measurably accelerates the slow ship forward — a real
/// projected-speed increase matching the mass-weighted formula, not just "no
/// exception thrown".</item>
/// <item>An unshielded ship takes real collision damage; a shielded ship under
/// the SAME collision loses less hull (the mounted shield absorbs in
/// <c>ReceiveDamage</c>, and the HP-pool boost reshapes the exchange).</item>
/// <item>A same-side lifepod/rescue-capable pair in proximity is NOT bumped by
/// physics — mutual exclusivity with the rescue pass holds.</item>
/// <item>An ENEMY (different-side) lifepod still takes real collision damage —
/// the friendly-only exemption does not apply cross-side (the detail that
/// differs from P254's unconditional asteroid lifepod gate).</item>
/// </list>
/// </summary>
public sealed class ShipShipCollisionTests
{
    // ── identical harness to ShipAsteroidCollisionTests (real static core) ────
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

    private static Mission NewStartedMission(IIgcSite site)
    {
        var civs = RepresentativeCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId).Take(2).ToList();
        IReadOnlyList<SideSpec> specs = [];
        for (int i = 0; i < civs.Count; i++)
            specs = specs.Append(new SideSpec((short)i, $"Team {i + 1}", civs[i].CivilizationId)).ToList();
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, specs, site);
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>A sector + spot far (> <paramref name="margin"/>) from every
    /// asteroid (ANY capability), every warp and the sector origin, so two
    /// synthetic ships placed there provably interact only with each other and
    /// never transit a warp or scrape a rock/station.</summary>
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

    private static void Pump(Mission m, int n, double step = 0.25)
    {
        for (int i = 0; i < n; i++) m.Update(m.LastUpdate + step);
    }

    // ── hull selection from the live core ─────────────────────────────────────
    private static IEnumerable<DataHullTypeIGC> AllHulls()
        => RepresentativeCore.Value.HullTypes.Values;

    /// <summary>A moderate mass-ratio (non-lifepod) hull pair — light vs heavy with
    /// ratio bounded in ~[1.6, 3.2], both with non-trivial hit-points. Picked by
    /// SEARCHING the core (not by percentile): the mass distribution is heavily
    /// skewed (scouts ~40 t vs bombers ~2500 t), so percentile selection produced
    /// an extreme 60:1 ratio that made the heavy body effectively stationary AND
    /// killed the light target outright — hiding the two-body behaviour this suite
    /// must prove. A bounded ~2:1 pair lets BOTH bodies move measurably and keeps
    /// both survivable at moderate speeds. Value-type record → <c>.Value</c> to
    /// unwrap the nullable.</summary>
    private static readonly Lazy<(DataHullTypeIGC Light, DataHullTypeIGC Heavy)> HullPair = new(() =>
    {
        var hulls = AllHulls().Where(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod) && h.HitPoints >= 150f)
                              .OrderBy(h => h.Mass).ToList();
        (DataHullTypeIGC Light, DataHullTypeIGC Heavy) best = default;
        float bestScore = float.MaxValue;
        for (int i = 0; i < hulls.Count; i++)
            for (int j = i + 1; j < hulls.Count; j++)
            {
                float r = hulls[j].Mass / hulls[i].Mass;
                if (r < 1.6f || r > 3.2f) continue;
                float score = MathF.Abs(r - 2.2f);
                if (score < bestScore) { bestScore = score; best = (hulls[i], hulls[j]); }
            }
        if (best.Light == default) throw new InvalidOperationException("no moderate mass-ratio hull pair in the core");
        return best;
    });

    private static readonly Lazy<DataHullTypeIGC> LightHull = new(() => HullPair.Value.Light);
    private static readonly Lazy<DataHullTypeIGC> HeavyHull = new(() => HullPair.Value.Heavy);

    /// <summary>A real lifepod hull (eject-pod).</summary>
    private static readonly Lazy<DataHullTypeIGC> LifepodHull = new(() =>
        AllHulls().First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod)));

    /// <summary>A rescue-capable hull that is not itself a lifepod (a fighter).</summary>
    private static readonly Lazy<DataHullTypeIGC> RescueHull = new(() =>
        AllHulls().First(h => h.HasCapability(DataHullTypeIGC.HabmRescue)
                              && !h.HasCapability(DataHullTypeIGC.HabmLifepod)));

    /// <summary>A Shield part type for the shield test.</summary>
    private static readonly Lazy<DataPartTypeIGC> ShieldPart = new(() =>
        RepresentativeCore.Value.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Shield));

    private static Ship Place(Mission m, Sector sector, short id, DataHullTypeIGC hull,
        Vector3 pos, Vector3 vel, short? side)
    {
        var ship = new Ship(id, hull, pos, vel, Orientation.Identity);
        ship.SideId = side;
        ship.StateM = ShipControlState.CoastButton;   // preserve velocity (no auto-brake)
        m.PlaceShip(ship, sector);
        return ship;
    }

    // ══ SCENARIO 1 — both velocities change + momentum conserved + heavier deflects more ══
    /// <summary>⭐ A heavy ship and a light ship on a head-on collision course in
    /// an otherwise-empty sector: BOTH velocities change post-collision (the
    /// two-body case — unlike the asteroid pass where only the ship moved), the
    /// total linear momentum <c>m1·v1 + m2·v2</c> is conserved, and the heavier
    /// body experiences the smaller velocity change (the lighter one is deflected
    /// more), matching mass-weighted momentum transfer.</summary>
    [Fact]
    public void Head_on_collision_changes_both_velocities_conserves_momentum_and_deflects_lighter_more()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var (spot, sector) = FarSpot(mission);

        var heavy = HeavyHull.Value;
        var light = LightHull.Value;
        float contact = 0.5f * heavy.Length + 0.5f * light.Length;
        const float V = 60f;                       // equal approach speeds

        // Overlapping head-on: heavy approaches from +x (moving −x), light from
        // −x (moving +x). They sit overlapping by 6 so the swept detector sees the
        // pair this tick.
        var ship1 = Place(mission, sector, 9901, heavy, spot + new Vector3(contact / 2 - 3f, 0f, 0f), new Vector3(-V, 0f, 0f), 1);
        var ship2 = Place(mission, sector, 9902, light, spot + new Vector3(-(contact / 2 - 3f), 0f, 0f), new Vector3(V, 0f, 0f), 2);

        float m1 = ship1.Mass, m2 = ship2.Mass;
        float pxPre = m1 * ship1.Velocity.X + m2 * ship2.Velocity.X;
        float v1xPre = ship1.Velocity.X, v2xPre = ship2.Velocity.X;
        Assert.True(m1 > m2, $"setup needs a heavier body (m1 {m1:0.##} vs m2 {m2:0.##})");

        Pump(mission, 1);

        float v1xPost = ship1.Velocity.X, v2xPost = ship2.Velocity.X;

        // (a) BOTH bodies changed — the defining difference from the asteroid pass.
        Assert.True(MathF.Abs(v1xPost - v1xPre) > 1f, $"heavy velocity must change: {v1xPre:0.##}→{v1xPost:0.##}");
        Assert.True(MathF.Abs(v2xPost - v2xPre) > 1f, $"light velocity must change: {v2xPre:0.##}→{v2xPost:0.##}");

        // (b) total linear momentum conserved by the mass-weighted blend.
        float pxPost = m1 * v1xPost + m2 * v2xPost;
        Assert.True(MathF.Abs(pxPost - pxPre) < MathF.Max(1f, MathF.Abs(pxPre) * 0.06f),
            $"momentum must be conserved: {pxPre:0.##} → {pxPost:0.##}");

        // (c) the heavier body is deflected less (the lighter one changes more).
        float dv1 = MathF.Abs(v1xPost - v1xPre);
        float dv2 = MathF.Abs(v2xPost - v2xPre);
        Assert.True(dv2 > dv1, $"lighter ship must be deflected more (Δv2 {dv2:0.##} vs Δv1 {dv1:0.##})");
        // Both survive a single moderate-speed collision.
        Assert.True(!ship1.Dead && !ship2.Dead, "both ships must survive the head-on");

        Console.WriteLine($"P354-HEADON: heavy {v1xPre:0.##}→{v1xPost:0.##}, light {v2xPre:0.##}→{v2xPost:0.##} "
                          + $"m/s; momentum {pxPre:0.##}→{pxPost:0.##}; Δv2({dv2:0.##}) > Δv1({dv1:0.##}).");
    }

    // ══ P463c — the collision SIGNAL (one site event with the native per-party jiggles) ══
    /// <summary>⭐ P463c — a ship-ship collision raises exactly ONE
    /// <see cref="IIgcSite.CollisionEvent"/> carrying the native per-party
    /// jiggle intensities: party1 <c>(2·damage1+damage2)·coeff</c>, party2
    /// <c>(damage1+2·damage2)·coeff</c> (shipIGC.cpp:1191,1195, coeff=1/20),
    /// where damage1 = 2·base1+base2 and damage2 = base1+2·base2 with
    /// base_i = |v_i − v_i'|²/128. Fails before P463c (no <c>CollisionEvent</c>
    /// existed), the "fail first" ship-ship server proof.</summary>
    [Fact]
    public void Ship_ship_collision_emits_exactly_one_collision_event_with_native_per_party_jiggles()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var (spot, sector) = FarSpot(mission);

        var heavy = HeavyHull.Value;
        var light = LightHull.Value;
        float contact = 0.5f * heavy.Length + 0.5f * light.Length;
        const float V = 60f;

        var ship1 = Place(mission, sector, 9931, heavy, spot + new Vector3(contact / 2 - 3f, 0f, 0f), new Vector3(-V, 0f, 0f), 1);
        var ship2 = Place(mission, sector, 9932, light, spot + new Vector3(-(contact / 2 - 3f), 0f, 0f), new Vector3(V, 0f, 0f), 2);

        Vector3 v1Pre = ship1.Velocity, v2Pre = ship2.Velocity;

        recording.Reset();
        Pump(mission, 1);

        var evs = recording.Calls.Where(c => c.StartsWith("CollisionEvent")).ToList();
        Assert.Single(evs);   // exactly one site event per collision
        Assert.True(!ship1.Dead && !ship2.Dead, "both ships must survive the head-on to observe the jiggle signal");

        // Recompute the native per-party jiggles from the measured velocity change.
        Vector3 v1Post = ship1.Velocity, v2Post = ship2.Velocity;
        float base1 = (v1Pre - v1Post).LengthSquared() / 128f;
        float base2 = (v2Pre - v2Post).LengthSquared() / 128f;
        float damage1 = 2f * base1 + base2;
        float damage2 = base1 + 2f * base2;
        const float coeff = 1f / 20f;
        float expJ1 = (2f * damage1 + damage2) * coeff;
        float expJ2 = (damage1 + 2f * damage2) * coeff;

        var (j1, j2) = ParseJiggle(evs[0]);
        Assert.True(MathF.Abs(j1 - expJ1) <= MathF.Max(1e-3f, expJ1 * 0.006f),
            $"party1 jiggle {j1} must equal native (2·damage1+damage2)·coeff = {expJ1}");
        Assert.True(MathF.Abs(j2 - expJ2) <= MathF.Max(1e-3f, expJ2 * 0.006f),
            $"party2 jiggle {j2} must equal native (damage1+2·damage2)·coeff = {expJ2}");

        Console.WriteLine($"P463c-SHIP: one CollisionEvent, party jiggles ({j1:0.###},{j2:0.###}) " +
                          $"(native ({expJ1:0.###},{expJ2:0.###})), sector {sector.Id}.");
    }

    /// <summary>Parse the two trailing jiggle floats from a recorded
    /// <c>CollisionEvent(Ship, Ship, &lt;cluster&gt;, Vector3, j1, j2)</c>.</summary>
    private static (float J1, float J2) ParseJiggle(string call)
    {
        int i = call.IndexOf('(');
        var args = call.Substring(i + 1, call.Length - i - 2).Split(", ");
        float j1 = float.Parse(args[^2], System.Globalization.CultureInfo.InvariantCulture);
        float j2 = float.Parse(args[^1], System.Globalization.CultureInfo.InvariantCulture);
        return (j1, j2);
    }

    // ══ SCENARIO 2 — the remembered tactic: bump a slower ship to speed it up ══
    /// <summary>⭐ THE tactic Domingo remembered: a faster/heavier ship (escort)
    /// rear-ending a slower/stationary one (bomber) measurably ACCELERATES the
    /// slow ship forward — its forward speed in the direction of the bump
    /// increases to the mass-weighted elastic-launch value predicted by the
    /// native formula (<c>shipIGC.cpp:1093-1124</c>), not merely "no exception".
    /// The bumper's momentum is conserved into the exchange.</summary>
    [Fact]
    public void Faster_heavier_ship_bumping_a_slower_one_accelerates_it_forward()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var (spot, sector) = FarSpot(mission);

        var heavy = HeavyHull.Value;
        var light = LightHull.Value;
        float contact = 0.5f * heavy.Length + 0.5f * light.Length;
        const float VBump = 80f;                   // the escort's approach speed

        // Rear-end: target at rest ahead; bumper behind, overlapping by 5, coasting +x.
        var target = Place(mission, sector, 9911, light, spot, Vector3.Zero, 2);
        var bumper = Place(mission, sector, 9912, heavy, spot + new Vector3(-(contact - 5f), 0f, 0f), new Vector3(VBump, 0f, 0f), 1);

        float mT = target.Mass, mB = bumper.Mass;
        Assert.True(mB > mT, $"setup needs a heavier bumper (mB {mB:0.##} vs mT {mT:0.##})");
        float pxPre = mB * bumper.Velocity.X + mT * target.Velocity.X;

        Pump(mission, 1);

        float vtPost = target.Velocity.X;
        float vbPost = bumper.Velocity.X;

        // The target is accelerated FORWARD (+x, the direction of the bump): a
        // real projected-speed increase from rest.
        Assert.True(vtPost > 0f, $"target must be pushed forward (+x), got {vtPost:0.##}");
        // Concrete speed assertion: the mass-weighted elastic launch value
        // 1.75·VBump·mB/(mB+mT) (derived from shipIGC.cpp:1093-1124 for a
        // stationary target) — with a tolerance for the 25% inelastic blend and
        // the blended normal direction.
        float expected = 1.75f * VBump * mB / (mB + mT);
        Assert.True(vtPost > expected * 0.75f,
            $"target forward speed {vtPost:0.##} should approach the mass-weighted launch {expected:0.##}");
        Assert.True(vtPost > VBump * 0.5f,
            $"target should be kicked to a substantial fraction of the bumper speed, got {vtPost:0.##}");

        // Momentum conserved by the exchange.
        float pxPost = mB * vbPost + mT * vtPost;
        Assert.True(MathF.Abs(pxPost - pxPre) < MathF.Max(1f, MathF.Abs(pxPre) * 0.06f),
            $"momentum must be conserved: {pxPre:0.##} → {pxPost:0.##}");

        Assert.True(!bumper.Dead && !target.Dead, "both ships must survive the bump");

        Console.WriteLine($"P354-TACTIC: target {target.Velocity.X:0.##} m/s forward from rest after the bump "
                          + $"(expected ~{expected:0.##}), bumper {bumper.Velocity.X:0.##}; momentum {pxPre:0.##}→{pxPost:0.##}. The remembered tactic is REAL.");
    }

    // ══ SCENARIO 3 — unshielded takes real damage; shielded takes proportionally less ══
    /// <summary>⭐ Under an identical rear-end collision, an UNSHIELDED target
    /// takes real hull damage (fraction drops); a SHIELDED target (a real mounted
    /// <c>Shield</c> part) loses measurably LESS hull from the same hit. This is
    /// the shield survivability the native collision HP-pool boost
    /// (<c>shipIGC.cpp:1147-1158</c>) plus <c>ReceiveDamage</c>'s mounted-shield
    /// absorption (<c>Ship.cs:3180-3183</c>) deliver together.</summary>
    [Fact]
    public void Shielded_target_takes_proportionally_less_collision_damage_than_unshielded()
    {
        float Drop(bool shielded)
        {
            var recording = new RecordingIgcSite();
            var mission = NewStartedMission(recording);
            var (spot, sector) = FarSpot(mission);

            var heavy = HeavyHull.Value;
            var light = LightHull.Value;
            float contact = 0.5f * heavy.Length + 0.5f * light.Length;
            const float VBump = 70f;

            var target = Place(mission, sector, 9921, light, spot, Vector3.Zero, 2);
            if (shielded)
                target.MountPart(ShieldPart.Value, amount: 1, mountId: 0, fractionMounted: 255);
            var bumper = Place(mission, sector, 9922, heavy, spot + new Vector3(-(contact - 5f), 0f, 0f), new Vector3(VBump, 0f, 0f), 1);

            float pre = target.Fraction;
            Pump(mission, 1);
            float drop = pre - target.Fraction;
            Assert.True(!target.Dead, "target must survive the single bump to observe hull loss");
            return drop;
        }

        float unshielded = Drop(false);
        float shielded = Drop(true);

        // The unshielded ship takes real collision damage.
        Assert.True(unshielded > 0.008f, $"unshielded target must take real hull damage, got drop {unshielded:0.####}");
        // The shielded ship loses measurably less hull from the SAME collision.
        Assert.True(shielded < unshielded - 0.004f,
            $"shielded target must lose less hull ({shielded:0.####} vs unshielded {unshielded:0.####})");

        Console.WriteLine($"P354-SHIELD: same rear-end — unshielded hull drop {unshielded:0.####}, "
                          + $"shielded drop {shielded:0.####}. Shield absorbs real collision damage.");
    }

    // ══ SCENARIO 4 — mutual exclusivity with the rescue pass ══
    /// <summary>⭐ A SAME-SIDE lifepod/rescue-capable pair in proximity is NOT
    /// given a physics bump or collision damage — the rescue-eligible pair is
    /// skipped by the collision pass (the re-checked native rescue predicate,
    /// <c>shipIGC.cpp:1027-1042</c>) and handed to the rescue pass instead:
    /// velocities/positions are undisturbed AND the pair is drained as a rescue.</summary>
    [Fact]
    public void Same_side_rescue_pair_triggers_rescue_not_a_physics_bump()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var (spot, sector) = FarSpot(mission);

        var rescueHull = RescueHull.Value;
        var podHull = LifepodHull.Value;
        float contact = 0.5f * rescueHull.Length + 0.5f * podHull.Length;
        const float V = 20f;   // slow approach + partial overlap so the swept
                               // collision is detected while the pair is still
                               // approaching (not post-crossing: the tiny pod must
                               // not be fully engulfed by the larger rescuer,
                               // which would make the relative-approach debounce
                               // legitimately decline to bounce)

        // Same side; a moving rescue-capable ship bumps a stationary lifepod.
        // Partial overlap (distance between centres > |rResc − rPod|, < rResc + rPod).
        var pod = Place(mission, sector, 9931, podHull, spot, Vector3.Zero, 1);
        var rescuer = Place(mission, sector, 9932, rescueHull, spot + new Vector3(-(contact - 1.0f), 0f, 0f), new Vector3(V, 0f, 0f), 1);

        float preRescuerVx = rescuer.Velocity.X;
        float prePodFrac = pod.Fraction;
        Pump(mission, 1);

        // NO physics bump: the bumper keeps its velocity (would have changed/bounced
        // if the collision pass had applied), the pod stays at rest, no hull damage.
        Assert.Equal(preRescuerVx, rescuer.Velocity.X, 2);
        Assert.True(MathF.Abs(pod.Velocity.X) < 0.5f, "pod must not be launched by a physics bump");
        Assert.Equal(prePodFrac, pod.Fraction, 6);

        // BUT the pair WAS detected and handed to the rescue pass (mutual
        // exclusivity held — rescue happened instead of a bump). The mission
        // chunks each tick into sub-updates (Mission.Update, n = 1 + deltaT/Δt),
        // so the rescue pass may legitimately queue the pair once per chunk; what
        // matters is that EVERY queued entry is exactly this pair and never a
        // physics interaction.
        var rescues = sector.DrainRescues();
        Assert.NotEmpty(rescues);
        Assert.All(rescues, r => Assert.True(
            ReferenceEquals(r.Rescuee, pod) && ReferenceEquals(r.Rescuer, rescuer),
            $"rescue must be pod<->rescuer, got {r.Rescuee.HullType.Name}<->{r.Rescuer.HullType.Name}"));
        Console.WriteLine($"P354-RESCUE: {rescues.Count} chunk-queued same-side rescue(s) of pod by rescuer; "
                          + "physics skipped (velocities intact, no damage) — exclusivity holds.");
    }

    // ══ SCENARIO 5 — an ENEMY lifepod still takes real collision damage ══
    /// <summary>⭐ The detail that distinguishes this pass from P254's asteroid
    /// lifepod gate: the friendly-only damage exemption
    /// (<c>shipIGC.cpp:1166-1168</c>) does NOT protect an ENEMY (different-side)
    /// lifepod. A different-side pod colliding with another ship takes real hull
    /// damage, and it is not rescued (cross-side can never rescue).</summary>
    [Fact]
    public void Enemy_lifepod_takes_real_collision_damage()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var (spot, sector) = FarSpot(mission);

        var heavyEnemy = HeavyHull.Value;       // an ordinary heavy combat ship (not a pod)
        var podHull = LifepodHull.Value;
        float contact = 0.5f * heavyEnemy.Length + 0.5f * podHull.Length;
        const float V = 50f;

        // DIFFERENT sides: a moving enemy ship bumps a stationary enemy lifepod.
        var pod = Place(mission, sector, 9941, podHull, spot, Vector3.Zero, 1);
        Place(mission, sector, 9942, heavyEnemy, spot + new Vector3(-(contact - 5f), 0f, 0f), new Vector3(V, 0f, 0f), 2);

        float prePodFrac = pod.Fraction;
        Pump(mission, 1);

        // The enemy lifepod took REAL collision damage — here so much that it is
        // genuinely destroyed by the impact. This is the maximal proof that the
        // friendly-only damage exemption (shipIGC.cpp:1166-1168) does NOT apply
        // cross-side: the SAME geometry in the SAME-side rescue test (above)
        // leaves its pod completely unharmed (physics is skipped there), whereas
        // this different-side pod is destroyed. Contrast that makes the
        // cross-side non-exemption unmistakable.
        Assert.True(pod.Dead,
            $"enemy lifepod must take real collision damage — it is destroyed here (frac {prePodFrac:0.####} → {pod.Fraction:0.####}); "
            + "the friendly-only exemption correctly did NOT protect it cross-side");

        // And it is NOT a rescue pair (different sides) — nothing drained.
        Assert.Empty(sector.DrainRescues());

        Console.WriteLine($"P354-ENEMYPOD: enemy lifepod was DESTROYED by the collision (pre-frac {prePodFrac:0.####}); "
                          + "friendly-only exemption correctly did NOT apply cross-side. Not rescued (different sides).");
    }

    // ══ P549 — friendly RAMMING does no damage (native ReceiveDamage FF gate) ══
    /// <summary>⭐ P549 — the friendly-fire gate in <c>ReceiveDamage</c>
    /// (native <c>shipIGC.cpp:1222-1228</c>) drops ALL positive damage from a
    /// same-side launcher when friendly fire is off (always here), so a teammate
    /// collision deals ZERO hull damage to either ship — the collision still
    /// BOUNCES (velocities change), but neither hull drops. Under the identical
    /// geometry with DIFFERENT sides the collision deals real damage. Before
    /// P549 the same-side pair took real ramming damage (only a lifepod was
    /// exempt), so this test's friendly-leg fails on the pre-fix code.</summary>
    [Fact]
    public void Friendly_ramming_deals_zero_damage_but_enemy_ramming_damages_under_same_geometry()
    {
        float Drop(bool friendly)
        {
            var recording = new RecordingIgcSite();
            var mission = NewStartedMission(recording);
            var (spot, sector) = FarSpot(mission);
            var heavy = HeavyHull.Value;
            var light = LightHull.Value;
            float contact = 0.5f * heavy.Length + 0.5f * light.Length;
            const float VBump = 70f;
            short bumperSide = 1;
            short targetSide = friendly ? (short)1 : (short)2;
            var target = Place(mission, sector, 9961, light, spot, Vector3.Zero, targetSide);
            Place(mission, sector, 9962, heavy, spot + new Vector3(-(contact - 5f), 0f, 0f), new Vector3(VBump, 0f, 0f), bumperSide);
            float pre = target.Fraction;
            Pump(mission, 1);
            return pre - target.Fraction;
        }

        float friendly = Drop(true);
        float enemy = Drop(false);

        // Teammate collision: ZERO hull damage to the (previously) damaged target.
        Assert.True(friendly < 1e-6f, $"friendly ramming must deal 0 damage, got drop {friendly:0.####}");
        // Enemy collision under the same geometry still damages.
        Assert.True(enemy > 0.005f, $"enemy ramming must deal real damage under the same geometry, got {enemy:0.####}");

        Console.WriteLine($"P549-RAM: same-side collision drop {friendly:0.####} (must be 0), "
                          + $"cross-side collision drop {enemy:0.####} (must be > 0). Friendly FF-off ramming is harmless; enemy ramming still hurts.");
    }

    /// <summary>⭐ P549 — the same-side collision still physically BOUNCES even
    /// though it deals no damage: the elastic-collision velocity exchange is
    /// independent of the ReceiveDamage friendly-fire gate, so both ships' speeds
    /// change as if the collision were cross-side. Guards against a regression
    /// that naively skipped the whole collision response for friendly pairs.</summary>
    [Fact]
    public void Friendly_ramming_still_bounces_both_ships()
    {
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var (spot, sector) = FarSpot(mission);
        var heavy = HeavyHull.Value;
        var light = LightHull.Value;
        float contact = 0.5f * heavy.Length + 0.5f * light.Length;
        const float V = 60f;

        var ship1 = Place(mission, sector, 9981, heavy, spot + new Vector3(contact / 2 - 3f, 0f, 0f), new Vector3(-V, 0f, 0f), 1);
        var ship2 = Place(mission, sector, 9982, light, spot + new Vector3(-(contact / 2 - 3f), 0f, 0f), new Vector3(V, 0f, 0f), 1);
        float v1xPre = ship1.Velocity.X, v2xPre = ship2.Velocity.X;

        Pump(mission, 1);

        // Both bodies changed velocity (the physical bounce)…
        Assert.True(MathF.Abs(ship1.Velocity.X - v1xPre) > 1f, "heavy must still bounce (velocity change)");
        Assert.True(MathF.Abs(ship2.Velocity.X - v2xPre) > 1f, "light must still bounce (velocity change)");
        // …yet NEITHER took hull damage.
        Assert.True(ship1.Fraction > 0.999f && ship2.Fraction > 0.999f, "friendly bounce must deal no hull damage");
        Console.WriteLine($"P549-RAMBOUNCE: same-side pair bounced ({v1xPre:0.#}->{ship1.Velocity.X:0.#}, {v2xPre:0.#}->{ship2.Velocity.X:0.#}) with zero hull loss.");
    }
}
