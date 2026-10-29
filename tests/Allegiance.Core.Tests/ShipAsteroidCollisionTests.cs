using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P254 — real ship-vs-asteroid elastic collision. A ship that overlaps an
/// asteroid in <see cref="Sector.ResolveShipAsteroidCollisions"/> (a dedicated
/// per-tick pass, deliberately independent of the ordnance-gated combat pass) is
/// bounced: pushed out to just outside the rock's surface and given the native
/// reflected velocity scaled by <c>cElasticity = 0.75</c>, plus real impact
/// damage (the native <c>|Δv|² · (1/128)</c> metric, doubled for the ship and
/// split against both hit-point pools — <c>shipIGC.cpp:906-960</c>). Two live
/// proofs, both driven through real <see cref="Sector.Update"/> ticks on a real
/// mission (the same server-side sim the mining/combat suites use):
/// <list type="number">
/// <item>A non-mining ship rammed into a rock measures a real before/after
/// position + velocity change (sign flip, ~0.75 elasticity) and real hull
/// damage (fraction decreases by the computed amount) — in an otherwise-empty
/// sector, proving no ordnance is required for a bounce.</item>
/// <item>A ship ACTIVELY MINING its target rock does NOT bounce: ore accrues
/// normally across many ticks and the ship's position/velocity stay undisturbed,
/// even when given a small inward (sub-mining-threshold) velocity in coast mode —
/// the mandatory regression guard proving the
/// <see cref="Ship.MiningAsteroid"/> exclusion, not merely the velocity debounce,
/// protects the already-working mining feature.</item>
/// </list>
/// </summary>
public sealed class ShipAsteroidCollisionTests
{
    // ── identical harness to MiningTests (real static core + real map) ────
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

    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, float hitPoints, short radius, short sectorId)
    {
        return new DataAsteroidIGC(
            Signature: 1.0f,
            Position: [pos.X, pos.Y, pos.Z],
            Up: [0f, 1f, 0f],
            Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f],
            Ore: oreMax,
            OreMax: oreMax,
            AabmCapabilities: Asteroid.CapMineHe3,
            AsteroidId: id,
            HitPoints: hitPoints,
            Radius: radius,
            ModelName: "roid.mdl",
            TextureName: "",
            IconName: "",
            ClusterId: sectorId,
            Name: $"ROID_{id}",
            Fraction: 0);
    }

    /// <summary>A sector + spot far (&gt; <paramref name="margin"/>) from every
    /// existing asteroid (ANY capability — the collision pass is not He3-gated),
    /// every warp and the sector origin, so a synthetic rock + ship placed there
    /// provably interact only with each other and never transits a warp.</summary>
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
            occupied.Add(Vector3.Zero);   // stay clear of the spawn origin too
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
        throw new InvalidOperationException("corpus map too crowded to place a clean synthetic asteroid");
    }

    private static void Pump(Mission m, int n, double step = 0.25)
    {
        for (int i = 0; i < n; i++) m.Update(m.LastUpdate + step);
    }

    /// <summary>A He3 rock far from everything else, with a healthy non-zero
    /// hit-point pool so the impact-damage path is exercised (the MiningTests
    /// MkAsteroid uses HitPoints: 0, which would null the ship-damage gate).</summary>
    private static (Vector3 Center, Sector Sector, Asteroid Rock) PlantRock(Mission m, short id)
    {
        var (spot, sector) = FarSpot(m);
        var rock = new Asteroid(MkAsteroid(id, spot, oreMax: 99999f, hitPoints: 4000f, radius: 150, sectorId: sector.Id));
        m.PlaceAsteroid(rock, sector);
        return (spot, sector, rock);
    }

    private static DataHullTypeIGC FirstHull(Mission m)
        => m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();

    /// <summary>A Shield part type (for the P392 shielded-vs-unshielded ram).</summary>
    private static readonly Lazy<DataPartTypeIGC> ShieldPart = new(() =>
        RepresentativeCore.Value.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Shield));

    private const float ApproachSpeed = 200f;   // the ram test's inbound speed; Δv = approach − (−0.75·approach) = 350

    /// <summary>A hull whose single 200 m/s asteroid ram lands a clear, central,
    /// SURVIVABLE hull-fraction drop (≈0.035–0.6) in the representative core —
    /// scanned from decoded data rather than guessed, so the damage proof is real
    /// and measurable without accidentally killing the ship (the weakest hull
    /// dies outright, which is also real native behaviour but not the proof we
    /// want to show). The expected drop is recomputed in the test body from the
    /// live core/hull, so this is self-consistent regardless of which core.</summary>
    private static readonly Lazy<DataHullTypeIGC> RamHull = new(() =>
    {
        var core = RepresentativeCore.Value;
        float damage1 = 2f * (ApproachSpeed * 1.75f) * (ApproachSpeed * 1.75f) / 128f;   // 2·|Δv|²/128, Δv = approach − (−0.75·approach)
        float hp2 = 4000f;                                                               // the test rock's HP
        float shipAmount = hp2 * damage1 / (hp2 + damage1);
        DataHullTypeIGC? best = null;
        float bestScore = float.NegativeInfinity;
        foreach (var h in core.HullTypes.Values)
        {
            float dtm = core.Constants.DamageConstants[0][h.DefenseType];
            if (dtm <= 0f) continue;
            float drop = shipAmount * dtm / h.HitPoints;
            if (drop >= 0.045f && drop <= 0.55f)
            {
                float score = -MathF.Abs(drop - 0.3f);   // prefer the clearest central drop
                if (score > bestScore) { bestScore = score; best = h; }
            }
        }
        if (best is null) throw new InvalidOperationException("no hull gives a survivable central collision-damage drop in the representative core");
        return best.Value;   // .Value (not !): DataHullTypeIGC is a value-type record
    });

    // ══ SCENARIO 1 — non-mining ship bounced with real position/velocity + damage ══
    /// <summary>⭐ A non-mining ship rammed into a rock in an otherwise-empty
    /// sector (no ordnance anywhere) is REALLY bounced: measured before/after,
    /// its velocity flips sign and drops to ~0.75× the approach speed (native
    /// elasticity), its position is pushed out past the rock's surface on the
    /// incoming side, and its hull fraction decreases by the exact computed
    /// impact damage (native <c>|Δv|² · (1/128)</c>, doubled and split against
    /// the rock's hit-point pool). The rock also takes the native share of
    /// damage.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_mining_ship_rams_asteroid_bounces_and_takes_real_damage()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var (center, sector, rock) = PlantRock(mission, 8100);
        var hull = RamHull.Value;

        float shipRadius = 0.5f * hull.Length;
        float contact = shipRadius + rock.Radius;

        var ship = new Ship(9100, hull, center + new Vector3(contact - 5f, 0f, 0f), new Vector3(-ApproachSpeed, 0f, 0f), Orientation.Identity);
        ship.StateM = ShipControlState.CoastButton;   // coast: keep velocity (no auto-brake) so it flies ballistically into the rock
        mission.PlaceShip(ship, sector);

        // Measured before.
        float preVelX = ship.Velocity.X;
        float preDist = (ship.Position - center).Length();
        float preFrac = ship.Fraction;

        // The rock's approach velocity and the outward normal are anti-parallel →
        // this is a genuine inbound collision (dot(v, n) < 0). One tick: the
        // swept detector sees the already-overlapping pair and the pass bounces it.
        Pump(mission, 1);

        // Measured after.
        float postVelX = ship.Velocity.X;
        float postDist = (ship.Position - center).Length();
        float postFrac = ship.Fraction;

        // (a) velocity bounced: sign flipped (was flying in −x, now flying out +x),
        //     and at ~0.75 the approach speed (native cElasticity).
        Assert.True(preVelX < 0f, $"pre-bounce velocity must be inbound, got {preVelX}");
        Assert.True(postVelX > 0f, $"velocity must flip to outbound after bounce, got {postVelX}");
        float approachSpeed = ApproachSpeed;
        float expectedOut = 0.75f * approachSpeed;
        Assert.True(MathF.Abs(postVelX - expectedOut) < approachSpeed * 0.25f,
            $"outbound speed {postVelX} should be ~0.75× approach ({expectedOut:0.#})");

        // (b) position bounced OUT: farther from the rock's centre than at contact,
        //     and pushed out on the +x (incoming) side.
        Assert.True(postDist > contact + 0.3f, $"post-bounce must clear the surface (dist {postDist:0.##} > contact {contact:0.##})");
        Assert.True(ship.Position.X > center.X, $"post-bounce must sit on the +x (incoming) side, got {ship.Position.X - center.X:0.##}");
        Assert.True(postDist < preDist || postVelX > 0f, "sanity: it left the incoming flight path via the bounce");

        // (c) real damage: hull fraction decreased by the exact computed amount.
        //     damage2 = |Δv|² · (1/128), damage1 = 2·damage2,
        //     ship share = rockHP · damage1 / (rockHP + damage1), then × damage constant / maxHP.
        var Δv = new Vector3(-approachSpeed, 0f, 0f) - new Vector3(expectedOut, 0f, 0f);
        float damage2 = Δv.LengthSquared() * (1.0f / 128.0f);
        float damage1 = 2.0f * damage2;
        float hp2 = rock.Data.HitPoints;                                      // rock current HP at impact (fraction still ~1: 4000)
        float hp1 = hull.HitPoints;                                           // ship current HP at impact (full before the hit)
        float shipAmount = hp2 * damage1 / (hp2 + damage1);
        float dtm = mission.Core.Constants.DamageConstants[0][hull.DefenseType];
        float expectedDrop = shipAmount * dtm / hull.HitPoints;
        float actualDrop = preFrac - postFrac;

        // The rock's native share: hp1·damage2/(hp1+damage2), over the rock's HP.
        float rockAmount = hp1 * damage2 / (hp1 + damage2);
        // P649: NULL-launcher damage uses the rock's owning core too
        // (asteroidigc.h:135), including a zero collision-to-asteroid cell.
        float rockDtm = mission.Core.Constants.DamageConstants[0][Asteroid.DefenseType];
        float expectedRockFrac = MathF.Max(0f, 1f - rockAmount * rockDtm / rock.Data.HitPoints);

        Assert.True(dtm > 0f, $"representative core must apply non-zero collision damage (row 0) to hull def {hull.DefenseType}");
        Assert.True(actualDrop > 0.025f, $"real damage must land (fraction {preFrac:0.####} → {postFrac:0.####}, drop {actualDrop:0.####})");
        Assert.True(MathF.Abs(actualDrop - expectedDrop) < 0.065f,
            $"damage drop {actualDrop:0.####} should match the computed native amount {expectedDrop:0.####}");
        Assert.True(postFrac > 0.3f, "ship must survive a single asteroid ram");

        // (d) the rock took the native share too (real, measurable, survivable) —
        //     a heavy ram transfers a noticeable chunk to the rock exactly as the
        //     native hp·d/(hp+d) share dictates.
        Assert.True(MathF.Abs(rock.Fraction - expectedRockFrac) < 0.07f && rock.Fraction > 0.5f,
            $"rock should take the native share (expected ~{expectedRockFrac:0.####}, got {rock.Fraction:0.####}) and survive");

        Console.WriteLine($"{nameof(Non_mining_ship_rams_asteroid_bounces_and_takes_real_damage)}: ship {preVelX:+#0.##;-0.##}→{postVelX:+#0.##;-0.##} m/s (elasticity {postVelX / approachSpeed:0.###}, ~0.75), " +
                          $"dist {preDist:0.#}→{postDist:0.#} (contact {contact:0.#}), hull {preFrac:0.####}→{postFrac:0.####} " +
                          $"(drop {actualDrop:0.####}, expected {expectedDrop:0.####}); rock frac {rock.Fraction:0.####} (expected {expectedRockFrac:0.####}). Empty sector — no ordnance involved.");
    }

    // ══ P463c — the collision SIGNAL (one site event with the native jiggle) ══
    /// <summary>⭐ P463c — a ship-vs-asteroid collision raises exactly ONE
    /// <see cref="IIgcSite.CollisionEvent"/> (the seam the wire
    /// <c>COLLISION_EVENT(216)</c> broadcast rides on), carrying the native
    /// jiggle intensity for the ship — <c>2·damage1·c_impactJiggleCoefficient</c>
    /// (shipIGC.cpp:836,1006, coeff=1/20) — and 0 for the rock (native never
    /// shakes a rock). Fails before P463c (no <c>CollisionEvent</c> existed), so
    /// it is the "fail first" server proof the task calls for.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Asteroid_collision_emits_exactly_one_collision_event_with_native_ship_jiggle()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var (center, sector, rock) = PlantRock(mission, 8240);
        var hull = RamHull.Value;

        float shipRadius = 0.5f * hull.Length;
        float contact = shipRadius + rock.Radius;

        var ship = new Ship(9240, hull, center + new Vector3(contact - 5f, 0f, 0f),
            new Vector3(-ApproachSpeed, 0f, 0f), Orientation.Identity);
        ship.StateM = ShipControlState.CoastButton;
        mission.PlaceShip(ship, sector);

        recording.Reset();
        Pump(mission, 1);   // the single tick that resolves the bounce

        var evs = recording.Calls.Where(c => c.StartsWith("CollisionEvent")).ToList();
        Assert.Single(evs);   // exactly one site event per collision
        Assert.True(ship.Velocity.X > 0f, $"the collision must actually resolve a bounce (vel.X {ship.Velocity.X:0.#})");

        // Native ship jiggle = 2·damage1·coeff, where damage1 = 2·damage2 and
        // damage2 = |Δv|²/128 (coeff = c_impactJiggleCoefficient = 1/20). Δv is
        // the measured inbound→outbound velocity change.
        var Δv = ship.Velocity - new Vector3(-ApproachSpeed, 0f, 0f);
        float damage2 = Δv.LengthSquared() * (1.0f / 128.0f);
        float damage1 = 2.0f * damage2;
        float expectedJ1 = 2.0f * damage1 * (1.0f / 20.0f);

        var (j1, j2) = ParseCollisionJiggle(evs[0]);
        Assert.True(MathF.Abs(j1 - expectedJ1) <= MathF.Max(1e-3f, expectedJ1 * 0.005f),
            $"ship jiggle {j1} must equal native 2·damage1·coeff = {expectedJ1}");
        Assert.Equal(0f, j2);   // the rock is never shaken

        Console.WriteLine($"P463c-ROCK: one CollisionEvent, ship jiggle {j1:0.###} (native {expectedJ1:0.###}), rock jiggle {j2}; sector {sector.Id}.");
    }

    /// <summary>Parse the two trailing jiggle floats from a recorded
    /// <c>CollisionEvent(...)</c> call string
    /// (<c>CollisionEvent(Ship, null, &lt;cluster&gt;, Vector3, j1, j2)</c>).</summary>
    private static (float J1, float J2) ParseCollisionJiggle(string call)
    {
        int i = call.IndexOf('(');
        var args = call.Substring(i + 1, call.Length - i - 2).Split(", ");
        float j1 = float.Parse(args[^2], System.Globalization.CultureInfo.InvariantCulture);
        float j2 = float.Parse(args[^1], System.Globalization.CultureInfo.InvariantCulture);
        return (j1, j2);
    }

    // ══ SCENARIO 3 (P392) — a mounted shield absorbs asteroid-collision damage ══
    /// <summary>⭐ An identical ship-vs-asteroid ram: an UNSHIELDED ship takes
    /// real hull damage (fraction drops measurably); a SHIELDED ship (a real
    /// mounted <c>Shield</c> part at full charge) loses measurably LESS hull
    /// from the SAME impact. This is the shield survivability the native
    /// mounted-shield absorption (<c>CshieldIGC::ApplyDamage</c>,
    /// <c>shieldIGC.cpp:140-195</c>, routed by <c>Ship.ReceiveDamage</c>) delivers
    /// for the ship-vs-asteroid collision — the "shield should do his thing
    /// too" fix (P392).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Shielded_ship_takes_measurably_less_hull_damage_from_asteroid_ram()
    {
        Require.Artwork();
        (float ShipDrop, float RockFraction, float RockDtm) Run(bool shielded)
        {
            var recording = new RecordingIgcSite();
            var mission = NewStartedMission(recording);
            var (center, sector, rock) = PlantRock(mission, 8150);
            var hull = RamHull.Value;

            float shipRadius = 0.5f * hull.Length;
            float contact = shipRadius + rock.Radius;

            var ship = new Ship(9150, hull, center + new Vector3(contact - 5f, 0f, 0f),
                new Vector3(-ApproachSpeed, 0f, 0f), Orientation.Identity);
            if (shielded)
                ship.MountPart(ShieldPart.Value, amount: 1, mountId: 0, fractionMounted: 255);
            ship.StateM = ShipControlState.CoastButton;   // ballistic inbound (keep velocity)
            mission.PlaceShip(ship, sector);

            float pre = ship.Fraction;
            Pump(mission, 1);
            float drop = pre - ship.Fraction;
            Assert.True(!ship.Dead, "ship must survive the single ram to observe hull loss");
            return (drop, rock.Fraction, mission.Core.Constants.DamageConstants[0][Asteroid.DefenseType]);
        }

        var (unshielded, rockAfterUnshielded, rockDtm) = Run(false);
        var (shielded, rockAfterShielded, _) = Run(true);

        // The unshielded ship takes real collision damage to its hull.
        Assert.True(unshielded > 0.012f,
            $"unshielded ship must take real hull damage from the ram, got drop {unshielded:0.####}");
        // The shielded ship loses measurably LESS hull from the SAME collision —
        // the ship's own protection comes from the mounted shield absorbing part
        // of the impact inside ReceiveDamage (P87; the pool term hp1 only feeds
        // the asteroid's damage share, not the ship's).
        Assert.True(shielded < unshielded - 0.003f,
            $"shielded ship must lose measurably less hull ({shielded:0.####} vs unshielded {unshielded:0.####})");

        // The P392 change itself: hp1 now includes the shield term, so a shielded
        // ship's effective collision pool feeds the rock's damage split exactly
        // as the ship-ship pass (CollisionHpPool). Observable signature — the
        // shielded ram deals MORE damage to the rock (lower post-fraction) than
        // the identical unshielded ram. The runs are deterministic and differ
        // ONLY in the shield mount, so any rock-fraction gap is caused by the
        // fix (pre-P392 hp1 was hull-only for both, giving equal rock damage).
        if (rockDtm > 0)
            Assert.True(rockAfterShielded < rockAfterUnshielded,
                $"shielded ram must damage the rock more (frac {rockAfterShielded:0.####} vs {rockAfterUnshielded:0.####})");
        else
        {
            // Native asteroidigc.h:135: a zero matrix cell nullifies both hits.
            Assert.Equal(1f, rockAfterUnshielded);
            Assert.Equal(1f, rockAfterShielded);
        }

        Console.WriteLine($"P392-SHIELD: identical asteroid ram — unshielded hull drop {unshielded:0.####}, "
                          + $"shielded drop {shielded:0.####} (shield absorbs real asteroid-collision damage); "
                          + $"rock frac {rockAfterUnshielded:0.####}(unshielded) → {rockAfterShielded:0.####}(shielded) — "
                          + "shielded ship's collision pool (incl. shield) scales the rock's damage like the ship-ship pass.");
    }

    // ══ SCENARIO 2 — actively-mining ship does NOT bounce (mandatory regression) ══
    /// <summary>⭐ A ship ACTIVELY MINING its target rock is NOT bounced off it:
    /// with the ship parked overlapping the rock surface (well inside the mining
    /// envelope), ore accrues normally across many ticks and its position &
    /// velocity stay completely undisturbed. Then, in coast mode with a small
    /// sub-mining-threshold velocity aimed INTO the rock (so the velocity debounce
    /// does not protect it — only the <see cref="Ship.MiningAsteroid"/> exclusion
    /// can), it still does not bounce: ore keeps accruing and the ship keeps
    /// drifting gently inward instead of being thrown out with a reflected
    /// velocity. Without the exclusion this step would yank the ship to the
    /// bounce point with an outward velocity and fail.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Actively_mining_ship_does_not_bounce_and_ore_accrues_uninterrupted()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var (center, sector, rock) = PlantRock(mission, 8110);
        var hull = FirstHull(mission);

        float shipRadius = 0.5f * hull.Length;
        float contact = shipRadius + rock.Radius;
        Vector3 surfacePos = center + new Vector3(contact - 5f, 0f, 0f);   // just inside the surface → overlapping, yet within the mining envelope (radius + half-length + 30)

        var ship = new Ship(9110, hull, surfacePos, Vector3.Zero, Orientation.Identity);
        ship.MiningRateOverridePerSecond = 100f;    // P528 seam: keep the collision-test ore magnitudes (50, then 100)
        ship.MineCapacityOverrideUnits = 10000f;    // P528 seam: never clamp mid-test (would clear MiningAsteroid)
        mission.PlaceShip(ship, sector);

        // ── Phase A: park the miner at its rock, let mining establish. ──
        Pump(mission, 2);                       // 2 × 25 = 50 ore; MiningAsteroid latched, ore accruing
        Assert.True(ship.IsMining, "ship must be flagged mining");
        Assert.True(ReferenceEquals(ship.MiningAsteroid, rock), "the ship's current mining asteroid must be OUR rock");
        Assert.True(MathF.Abs(ship.Ore - 50f) < 0.5f, $"ore should be ~50 after 2 ticks, got {ship.Ore:0.##}");
        float fracA = ship.Fraction;
        Vector3 posA = ship.Position;
        Vector3 velA = ship.Velocity;
        Assert.Equal(1f, fracA, 3);
        Assert.True((posA - surfacePos).Length() < 0.01f, "mining must not disturb the parked position");
        Assert.True(velA.Length() < 0.01f, "mining must not impart velocity");

        // ── Phase B: a real inward drift at sub-mining-threshold speed in coast
        //    mode. Mining stays active (|v|=0.5 < 1), but WITHOUT the exclusion the
        //    collision pass would see an inbound overlap and bounce the ship out. ──
        ship.StateM = ShipControlState.CoastButton;   // preserve the inward velocity (no auto-brake)
        ship.Velocity = new Vector3(-0.5f, 0f, 0f);   // magnitude 0.5 < mining's <1.0 stop gate
        float oreBeforeB = ship.Ore;
        Pump(mission, 4);                             // 4 more ticks of mining + the inward drift

        float oreGained = ship.Ore - oreBeforeB;
        Assert.True(ship.IsMining, "ship must still be mining after the inward drift");
        Assert.True(ReferenceEquals(ship.MiningAsteroid, rock), "still mining the SAME rock (the one it drills)");
        // Ore kept accruing at the full rate (4 × 25 = 100) — mining is completely
        // uninterrupted by the collision code. THIS is the primary regression proof.
        Assert.True(oreGained > 80f, $"ore must keep accruing (~100 over 4 ticks), got {oreGained:0.##}");

        // Velocity still points INWARD (a bounce would reflect it outward positive).
        Assert.True(ship.Velocity.X < 0f, $"mining ship must keep drifting inward, not be bounced out (velocity.X {ship.Velocity.X:0.####})");
        // Position not thrown to the bounce point: it has drifted gently inward
        // (surfacePos.X minus a few tenths), nowhere near the outward bounce point
        // (center.X + contact + 0.5) which a bounced ship would be yanked to.
        Assert.True(ship.Position.X < center.X + contact - 2f,
            $"mining ship must stay inside the rock surface (X {ship.Position.X - center.X:0.##} < {contact - 2:0.##}), not be ejected to the bounce point");
        // No damage whatsoever — a bounce would have reduced the hull fraction.
        Assert.Equal(1f, ship.Fraction, 3);

        Console.WriteLine($"P254-MINE: {oreGained:0.##} ore gained over 4 inward-drift ticks (rate intact), " +
                          $"velocity.X {ship.Velocity.X:0.####} (still inward — NOT bounced), pos {ship.Position.X - center.X:0.##} (inside surface {contact:0.##}), " +
                          $"fraction {ship.Fraction:0.####} (no damage), still mining rock {rock.Data.AsteroidId}. Exclusion held.");
    }

    // ══ WIRE PROOF — the bounced state reaches a real client peer automatically ══
    // (no new client rendering: the peer just decodes the server's real ship
    // position/velocity updates, which now carry the bounce).
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P254 Bounce Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 4422u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 44600000000000000ul, DrmHash = "p254bounce",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        Thread.Sleep(60);
        return n;
    }

    /// <summary>⭐ Over real loopback TCP on the zero-asteroid demo map (so the only
    /// rock in the universe is the injected one), the bot's own ship is rammed into
    /// the rock server-side; the server bounces it (velocity flips outward) and —
    /// with NO new client rendering — the peer decodes that bounced position/velocity
    /// from ordinary server ship updates. This is the client-visibility proof: the
    /// collision is a pure server-side sim fix and the client shows it automatically.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Loopback_bounced_ship_velocity_and_position_reach_the_wire()
    {
        Require.Artwork();
        using var server = new Srv();
        Asteroid? rock = null;
        Vector3 apos = new(0f, 0f, -900f);   // far down the −z beam (the position AsteroidCombatLiveTests proved clear of warps/stations)
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var sector = mission.Sectors[0];
            rock = new Asteroid(MkAsteroid(7150, apos, oreMax: 99999f, hitPoints: 4000f, radius: 150, sectorId: sector.Id));
            mission.PlaceAsteroid(rock, sector);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p254_bounce");
        bot.SendLogonRequest(Request("p254_bounce"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        var live = () => server.Ships.First(s => s.Ship.Id == shipId).Ship;
        Assert.NotNull(rock);

        // Reposition the bot's ship overlapping the rock, coasting straight in.
        var hull = live().HullType;
        float contact = 0.5f * hull.Length + rock!.Radius;
        live().Position = apos + new Vector3(contact - 5f, 0f, 0f);
        live().Velocity = new Vector3(-150f, 0f, 0f);
        live().StateM = ShipControlState.CoastButton;   // keep velocity (no auto-brake)

        // Pump until the SERVER has bounced the ship (velocity flipped outward).
        PumpUntil(server, () => live().Velocity.X > 0f, 2000);
        Assert.True(live().Velocity.X > 0f, $"server-side ship must bounce off the rock, got velocity.X {live().Velocity.X:0.#}");

        // The WIRE must then carry the bounced state: the peer's own-ship updates
        // show the outward velocity and a position outside the rock's surface.
        bot.ResetObservations();
        PumpUntil(server, () => bot.Observations.Count >= 4, 1500);
        FltBot.ShipObservation? obs = null;
        foreach (var o in bot.Observations) if (o.ShipId == shipId) obs = o;
        Assert.True(obs is not null, "peer must observe its own ship after the bounce");
        Assert.True(obs!.Value.Velocity.X > 0f,
            $"wire must carry the bounced OUTWARD velocity, got {obs.Value.Velocity.X:+#0.#;-0.#}");
        Assert.True((obs.Value.Position - apos).Length() > contact - 2f,
            $"wire position must show the ship pushed out of the rock (dist {(obs.Value.Position - apos).Length():0.#} > contact {contact:0.#})");

        Console.WriteLine($"P254-WIRE: peer decoded ship vel {obs.Value.Velocity.X:+#0.#;-0.#} m/s (bounced outward), " +
                          $"pos-dist-to-rock {(obs.Value.Position - apos).Length():0.#} (> surface {contact:0.#}) — server-side bounce is client-visible with NO new rendering.");
    }

    /// <summary>⭐ P463c — LIVE wire proof that a resolved collision reaches a real
    /// peer as the port-own <c>COLLISION_EVENT(216)</c> message: over real loopback
    /// TCP on the zero-asteroid demo map, the bot's ship is rammed into the injected
    /// rock server-side and the peer decodes exactly the expected collision event —
    /// party1 = the bot's own ship id, party2 = NA (the rock), a positive native
    /// jiggle1, and jiggle2 = 0. This is the live half of the task's "ram an
    /// asteroid" evidence (the audio/jiggle playback itself is a client-side concern
    /// covered by the pure tests; the wire that drives it is proven here).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Loopback_collision_event_reaches_the_wire_as_collision_event_216()
    {
        Require.Artwork();
        using var server = new Srv();
        Asteroid? rock = null;
        Sector? sec0 = null;
        Vector3 apos = new(0f, 0f, -986f);   // clear of warps/stations on the −z beam
        server.ShipSpawner = (core, mission, shipId) =>
        {
            sec0 = mission.Sectors[0];
            rock = new Asteroid(MkAsteroid(7350, apos, oreMax: 99999f, hitPoints: 6500f, radius: 150, sectorId: sec0.Id));
            mission.PlaceAsteroid(rock, sec0);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, sec0);
            return (ship, sec0);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p463c_collision");
        bot.SendLogonRequest(Request("p463c_collision"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        var live = () => server.Ships.First(s => s.Ship.Id == shipId).Ship;
        Assert.NotNull(rock);
        var hull = live().HullType;
        float contact = 0.5f * hull.Length + rock!.Radius;
        live().Position = apos + new Vector3(contact - 5f, 0f, 0f);
        live().Velocity = new Vector3(-150f, 0f, 0f);
        live().StateM = ShipControlState.CoastButton;   // ballistic inbound

        bot.ResetCollisionEvents();
        PumpUntil(server, () => bot.CollisionEvents.Count >= 1, 2000);
        Assert.True(bot.CollisionEvents.Count >= 1,
            "the peer must receive COLLISION_EVENT(216) when its ship rams a rock");

        var ev = bot.CollisionEvents[0];
        Assert.Equal(shipId, ev.Party1Id);        // the ship party carries the local ship id
        Assert.Equal((short)-1, ev.Party2Id);     // the rock party is NA (no portable id)
        Assert.Equal(sec0!.Id, ev.ClusterId);     // the sector where it happened
        Assert.True(ev.Jiggle1 > 0f, $"the ship party must carry a positive native jiggle, got {ev.Jiggle1}");
        Assert.Equal(0f, ev.Jiggle2);             // the rock is never shaken

        Console.WriteLine($"P463c-LIVE: peer decoded COLLISION_EVENT(216) party1={ev.Party1Id} (ship {shipId}) " +
                          $"party2={ev.Party2Id} (rock) cluster={ev.ClusterId} jiggle1={ev.Jiggle1:0.###} jiggle2={ev.Jiggle2}.");
    }
}
