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
/// P466 — real ship-vs-station elastic collision. A ship that overlaps a station
/// in <see cref="Sector.ResolveShipStationCollisions"/> (a dedicated per-tick pass,
/// modelled on the P254 asteroid pass) is bounced: the station's mass is treated as
/// infinite, so only the ship's velocity changes — reflect the normal component and
/// scale by <c>cElasticity = 0.75</c> — plus real impact damage to BOTH the ship and
/// the station via the native <c>|Δv|² · (1/128)</c> metric (the OT_station case of
/// <c>CshipIGC::HandleCollision</c>, shipIGC.cpp:719-868, whose non-docking path falls
/// through to the shared asteroid/probe bounce block shipIGC.cpp:868-960). Three live
/// proofs, all driven through real <see cref="Sector.Update"/> ticks on a real mission:
/// <list type="number">
/// <item>An ENEMY ship rammed into an enemy station bounces (velocity sign flip at
/// ~0.75 elasticity), loses real hull damage, and deals real damage to the station's
/// armor — the core "ramming bases" behaviour that did not exist before P466 (a ship
/// flew straight through an enemy base).</item>
/// <item>A lifepod bounces but neither deals nor takes damage (native
/// c_habmLifepod gate, shipIGC.cpp:803).</item>
/// <item>A SAME-SIDE ship is NOT bounced off its own station (the dock-exclusion
/// gate) — the mechanistic guarantee that a friendly ship reaching its base still
/// docks. End-to-end friendly docking is separately proven by the existing
/// <see cref="DockDockingTests"/> loopback test.</item>
/// </list>
/// </summary>
public sealed class ShipStationCollisionTests
{
    // ── identical harness to MiningTests / ShipAsteroidCollisionTests ────────
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

    /// <summary>A station type whose armor AND shield defense rows carry a non-zero
    /// collision (row 0) constant — required so a ram flows through the station's
    /// shield-overflow path and visibly damages its armor (a shield-less / no-row-0
    /// type takes no direct collision damage at all, native stationIGC.cpp:203-204).</summary>
    private static DataStationTypeIGC StationBaseType()
    {
        var core = RepresentativeCore.Value;
        foreach (var t in core.StationTypes.Values)
        {
            if (core.Constants.DamageConstants[0][t.DefenseTypeArmor] > 0f
                && core.Constants.DamageConstants[0][t.DefenseTypeShield] > 0f)
                return t;
        }
        throw new InvalidOperationException("no station type with non-zero collision damage constants in the core");
    }

    /// <summary>A sector + spot far (&gt; <paramref name="margin"/>) from every existing
    /// asteroid, warp, real station AND the sector origin, so a synthetic station + ship
    /// placed there provably interact only with each other (the ship↔station pass adds
    /// every station as a static collider, so the synthetic base must not be placed on
    /// top of a real one).</summary>
    private static (Vector3 Spot, Sector Sector) FarSpot(Mission m, float margin = 5500f)
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
            occupied.Add(Vector3.Zero);   // stay clear of the spawn origin too
            Vector3 anchor = occupied.Count == 0 ? Vector3.Zero
                : new Vector3(occupied.Average(p => p.X), occupied.Average(p => p.Y), occupied.Average(p => p.Z));
            foreach (var d in dirs)
                foreach (var dist in new[] { 10000f, 14000f, 18000f })
                {
                    var cand = anchor + d * dist;
                    if (occupied.All(p => (p - cand).Length() > margin))
                        return (cand, sector);
                }
        }
        throw new InvalidOperationException("corpus map too crowded to place a clean synthetic station");
    }

    private static void Pump(Mission m, int n, double step = 0.25)
    {
        for (int i = 0; i < n; i++) m.Update(m.LastUpdate + step);
    }

    private static DataHullTypeIGC FirstHull(Mission m)
        => m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();

    private const float ApproachSpeed = 200f;   // the ram test's inbound speed; Δv = approach − (−0.75·approach) = 350

    /// <summary>A hull whose single 200 m/s station ram lands a clear, central,
    /// SURVIVABLE hull-fraction drop (≈0.06–0.5) in the representative core — scanned
    /// from decoded data (same approach as the P254 asteroid RamHull) so the ship is
    /// demonstrably damaged but not killed by the impact.</summary>
    private static readonly Lazy<DataHullTypeIGC> RamHull = new(() =>
    {
        var core = RepresentativeCore.Value;
        float damage1 = 2f * (ApproachSpeed * 1.75f) * (ApproachSpeed * 1.75f) / 128f;   // 2·|Δv|²/128, Δv = 1.75·approach
        float hp2 = StationArmorHP;                                                      // the test station's full armor
        float shipAmount = hp2 * damage1 / (hp2 + damage1);
        DataHullTypeIGC? best = null;
        float bestScore = float.NegativeInfinity;
        foreach (var h in core.HullTypes.Values)
        {
            // A board hull would make the ram a capture candidate (the test drains
            // the station's shields to make the armor drop exact, and the port's
            // board-and-shields-down exclusion would then swallow the bounce); a
            // lifepod hits the lifepod gate (no ship damage). Exclude both so the
            // enemy-ram is unambiguously a plain combat ram.
            if (h.HasCapability(DataHullTypeIGC.HabmBoard)) continue;
            if (h.HasCapability(DataHullTypeIGC.HabmLifepod)) continue;
            float dtm = core.Constants.DamageConstants[0][h.DefenseType];
            if (dtm <= 0f) continue;
            float drop = shipAmount * dtm / h.HitPoints;
            if (drop >= 0.06f && drop <= 0.5f)
            {
                float score = -MathF.Abs(drop - 0.25f);   // prefer the clearest central drop
                if (score > bestScore) { bestScore = score; best = h; }
            }
        }
        if (best is null) throw new InvalidOperationException("no hull gives a survivable central station-collision-damage drop in the representative core");
        return best.Value;   // .Value (not !): DataHullTypeIGC is a value-type record
    });

    private const float StationArmorHP = 5000f;   // the synthetic station's MaxArmorHitPoints

    /// <summary>Build a synthetic station at <paramref name="center"/> on the given
    /// side, with a small armour pool and a small shield pool (the shield makes the
    /// damage-overflow path work; the pool is pre-drained in the damage test so the
    /// collision's armor share is exact). Radius is kept modest so the ship reaches the
    /// contact surface deterministically.</summary>
    private static (Vector3 Center, Sector Sector, Station Base) PlantStation(
        Mission m, short id, short sideId, float radius = 220f)
    {
        var (spot, sector) = FarSpot(m);
        var bt = StationBaseType();
        var type = bt with
        {
            MaxArmorHitPoints = StationArmorHP,
            MaxShieldHitPoints = 100f,
            ShieldRegeneration = 0f,
            ArmorRegeneration = 0f,
            Radius = radius,
        };
        var data = new DataStationIGC(
            Position: [spot.X, spot.Y, spot.Z],
            Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
            ClusterId: sector.Id, SideId: sideId, StationId: id,
            StationTypeId: bt.StationTypeId, BpHull: 1, BpShield: 1, Name: $"SYN_{id}");
        var station = new Station(data, type);
        m.PlaceStation(station, sector);
        return (spot, sector, station);
    }

    // ══ SCENARIO 1 — enemy ship bounced with real damage to BOTH bodies ══
    /// <summary>⭐ An ENEMY ship rammed into an enemy station is REALLY bounced:
    /// velocity flips sign and drops to ~0.75× the approach speed, the ship is pushed
    /// out past the station surface, its hull fraction decreases by the exact computed
    /// impact damage, AND the station's armor fraction decreases by the native
    /// hp·d/(hp+d) share — the "ram an enemy base, hull drops, base hull drops" behaviour.
    /// The pair is not a dock candidate (different side) and not a board candidate
    /// (shields up / not a board hull), so it bounces.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Enemy_ship_rams_enemy_station_bounces_and_both_take_real_damage()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        short sideA = mission.Sides[0].Id;
        short sideB = mission.Sides[1].Id;
        Assert.NotEqual(sideA, sideB);
        var (center, sector, station) = PlantStation(mission, 9300, sideA);
        var hull = RamHull.Value;

        // Enemy ship: side B vs the side-A station. Coasting straight in (ballistic,
        // no auto-brake) from just inside the contact surface.
        float shipRadius = 0.5f * hull.Length;
        float contact = shipRadius + station.Radius;
        // Start the ship's centre just OUTSIDE the station sphere but with its body
        // overlapping the surface — a genuine surface ram (distance R + ½·shipR lies
        // in (R, R + shipR)). Placing it deeper (contact − 5) would put a small hull's
        // centre inside the sphere, which the pass (correctly) treats as embedded.
        var ship = new Ship(9400, hull, center + new Vector3(station.Radius + shipRadius * 0.5f, 0f, 0f),
            new Vector3(-ApproachSpeed, 0f, 0f), Orientation.Identity);
        ship.StateM = ShipControlState.CoastButton;
        ship.SideId = sideB;
        mission.PlaceShip(ship, sector);

        // Pre-drain the station's small shield so the collision's armor share is exact
        // (stationIGC.cpp:174-200): with ShieldFraction 0 the overflow leaves
        // dmg = amount·dtmArmor/maxArmor exactly. Shield is 100 HP; a 100 HP drain zeros it.
        station.ReceiveDamage(0, 100f, 0.0, Vector3.Zero, Vector3.Zero, launcher: null);
        Assert.Equal(0f, station.ShieldFraction, 3);
        Assert.Equal(1f, station.ArmorFraction, 3);

        // Measured before.
        float preVelX = ship.Velocity.X;
        float preDist = (ship.Position - center).Length();
        float preFrac = ship.Fraction;
        float preArmor = station.ArmorFraction;

        Pump(mission, 1);

        // Measured after.
        float postVelX = ship.Velocity.X;
        float postDist = (ship.Position - center).Length();
        float postFrac = ship.Fraction;
        float postArmor = station.ArmorFraction;

        // (a) velocity bounced: sign flipped (was −x, now +x) at ~0.75× approach.
        Assert.True(preVelX < 0f, $"pre-bounce velocity must be inbound, got {preVelX}");
        Assert.True(postVelX > 0f, $"velocity must flip to outbound after the bounce, got {postVelX}");
        float expectedOut = 0.75f * ApproachSpeed;
        Assert.True(MathF.Abs(postVelX - expectedOut) < ApproachSpeed * 0.25f,
            $"outbound speed {postVelX:0.#} should be ~0.75× approach ({expectedOut:0.#})");

        // (b) position bounced OUT, on the +x (incoming) side, past the surface.
        Assert.True(postDist > contact + 0.3f, $"post-bounce must clear the surface (dist {postDist:0.##} > contact {contact:0.##})");
        Assert.True(ship.Position.X > center.X, $"post-bounce must sit on the +x side, got {ship.Position.X - center.X:0.##}");

        // (c) real ship damage: damage2=|Δv|²/128, damage1=2·damage2, ship share =
        //     stationHP·damage1/(stationHP+damage1), then × dtm / maxHP.
        var Δv = new Vector3(-ApproachSpeed, 0f, 0f) - new Vector3(expectedOut, 0f, 0f);
        float damage2 = Δv.LengthSquared() * (1.0f / 128.0f);
        float damage1 = 2.0f * damage2;
        float shipAmount = station.ArmorFraction * station.MaxArmor * damage1
                         / (station.ArmorFraction * station.MaxArmor + damage1);
        float dtmShip = mission.Core.Constants.DamageConstants[0][hull.DefenseType];
        float expectedShipDrop = shipAmount * dtmShip / hull.HitPoints;
        float actualShipDrop = preFrac - postFrac;
        Assert.True(dtmShip > 0f, $"representative core must apply non-zero collision damage (row 0) to hull def {hull.DefenseType}");
        Assert.True(actualShipDrop > 0.015f, $"ship must take real damage (fraction {preFrac:0.####} → {postFrac:0.####}, drop {actualShipDrop:0.####})");
        Assert.True(MathF.Abs(actualShipDrop - expectedShipDrop) < 0.075f,
            $"ship damage drop {actualShipDrop:0.####} should match the computed native amount {expectedShipDrop:0.####}");
        Assert.True(postFrac > 0.3f, "ship must survive a single station ram");

        // (d) real STATION damage: station share = shipHP·damage2/(shipHP+damage2),
        //     overflowed to armor as amount·dtmArmor/maxArmor (shield pre-drained).
        float hp1 = hull.HitPoints;                                       // no shield mounted → CollisionHpPool == hull HP
        float stationShare = hp1 * damage2 / (hp1 + damage2);
        float dtmArmor = mission.Core.Constants.DamageConstants[0][station.Type.DefenseTypeArmor];
        float expectedArmorDrop = stationShare * dtmArmor / station.MaxArmor;
        float actualArmorDrop = preArmor - postArmor;
        Assert.True(dtmArmor > 0f, "station type must have a non-zero collision armor constant");
        Assert.True(actualArmorDrop > 0.004f, $"station must take real armor damage (armor {preArmor:0.####} → {postArmor:0.####}, drop {actualArmorDrop:0.####})");
        Assert.True(MathF.Abs(actualArmorDrop - expectedArmorDrop) < 0.030f,
            $"station armor drop {actualArmorDrop:0.####} should match the computed native share {expectedArmorDrop:0.####}");
        Assert.True(postArmor > 0.5f, "station must survive a single ship ram");

        Console.WriteLine($"P466-RAM: ship {preVelX:+#0.##;-0.##}→{postVelX:+#0.##;-0.##} m/s (elasticity {postVelX / ApproachSpeed:0.###}, ~0.75), " +
                          $"hull {preFrac:0.####}→{postFrac:0.####} (drop {actualShipDrop:0.####}, expected {expectedShipDrop:0.####}); " +
                          $"station armor {preArmor:0.####}→{postArmor:0.####} (drop {actualArmorDrop:0.####}, expected {expectedArmorDrop:0.####}). Enemy ram — both bodies damaged.");
    }

    // ══ SCENARIO 2 — a lifepod bounces with no damage to either body ══
    /// <summary>⭐ A lifepod rammed into a station bounces (velocity flips at ~0.75)
    /// but neither deals nor takes any damage: the pod's hull fraction stays 1.0 and
    /// the station's armor stays 1.0 — the native c_habmLifepod gate (shipIGC.cpp:803,
    /// the same gate P254 applies to asteroids).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lifepod_bounces_off_station_with_no_damage()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        short sideA = mission.Sides[0].Id;
        short sideB = mission.Sides[1].Id;
        var (center, sector, station) = PlantStation(mission, 9310, sideA);

        // Pick a lifepod hull.
        var podHull = mission.Core.HullTypes.Values
            .First(h => h.HasCapability(DataHullTypeIGC.HabmLifepod));

        float shipRadius = 0.5f * podHull.Length;
        float contact = shipRadius + station.Radius;
        // Centre just OUTSIDE the station sphere but body-overlapping (surface ram).
        var pod = new Ship(9410, podHull, center + new Vector3(station.Radius + shipRadius * 0.5f, 0f, 0f),
            new Vector3(-ApproachSpeed, 0f, 0f), Orientation.Identity);
        pod.StateM = ShipControlState.CoastButton;
        pod.SideId = sideB;                                  // an enemy pod — bounces (not a rescue/dock candidate)
        mission.PlaceShip(pod, sector);

        float preVelX = pod.Velocity.X;
        float preFrac = pod.Fraction;
        float preArmor = station.ArmorFraction;
        float preShield = station.ShieldFraction;

        Pump(mission, 1);

        float postVelX = pod.Velocity.X;
        Assert.True(preVelX < 0f, "pre-bounce pod velocity must be inbound");
        Assert.True(postVelX > 0f, $"pod must bounce (velocity {postVelX:0.#} should be outbound)");
        Assert.True(MathF.Abs(postVelX - 0.75f * ApproachSpeed) < ApproachSpeed * 0.25f,
            $"pod outbound speed {postVelX:0.#} should be ~0.75× approach");

        // No damage to the pod…
        Assert.Equal(preFrac, pod.Fraction, 4);
        Assert.Equal(1f, pod.Fraction, 4);
        // …and no damage to the station (the pod deals no collision damage).
        Assert.Equal(preArmor, station.ArmorFraction, 4);
        Assert.Equal(1f, station.ArmorFraction, 4);
        Assert.Equal(preShield, station.ShieldFraction, 4);

        Console.WriteLine($"P466-POD: pod {preVelX:+#0.#;-0.#}→{postVelX:+#0.#;-0.#} m/s, hull fraction {pod.Fraction:0.####} (undamaged), " +
                          $"station armor {station.ArmorFraction:0.####} (undamaged) — pod bounced, no damage either way.");
    }

    // ══ SCENARIO 3 — a same-side ship is NOT bounced (the dock-exclusion gate) ══
    /// <summary>⭐ A SAME-SIDE ship coasting into its own station is NOT bounced by the
    /// collision pass: velocity stays inbound, position undisturbed, no damage — the
    /// exclusion that lets <see cref="Sector.ResolveDockCollisions"/> dock the ship
    /// instead. Without it (if the pair were treated as a ram) the pass would throw the
    /// friendly ship back out at the hull surface and break docking at every base whose
    /// collision radius exceeds <see cref="Sector.DockRadius"/>. End-to-end friendly
    /// docking is separately proven by the existing <see cref="DockDockingTests"/> loopback.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Same_side_ship_is_not_bounced_off_its_own_station()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        short sideA = mission.Sides[0].Id;
        var (center, sector, station) = PlantStation(mission, 9320, sideA);
        var hull = FirstHull(mission);

        float shipRadius = 0.5f * hull.Length;
        float contact = shipRadius + station.Radius;
        var ship = new Ship(9420, hull, center + new Vector3(station.Radius + shipRadius * 0.5f, 0f, 0f),
            new Vector3(-ApproachSpeed, 0f, 0f), Orientation.Identity);
        ship.StateM = ShipControlState.CoastButton;
        ship.SideId = sideA;                                 // SAME side as the station
        mission.PlaceShip(ship, sector);

        float preVelX = ship.Velocity.X;
        float prePosX = ship.Position.X;
        float preFrac = ship.Fraction;

        // Several ticks: a ram would have bounced on the FIRST tick (flipping velocity
        // outward and pushing the ship out); the exclusion keeps it flying inbound.
        Pump(mission, 3);

        // Velocity stays INBOUND (never flipped) — the ship was not bounced.
        Assert.True(ship.Velocity.X < 0f, $"same-side ship must NOT be bounced (velocity.X {ship.Velocity.X:0.#} should stay inbound)");
        // It kept flying forward into the approach (moved further inward than it started),
        // NOT ejected to the bounce point (center.X + contact + 0.5).
        Assert.True(ship.Position.X < center.X + contact - 1f,
            $"same-side ship must not be ejected to the bounce point (X {ship.Position.X - center.X:0.##} < contact {contact - 1:0.##})");
        // No damage whatsoever — a bounce would have cut the hull fraction.
        Assert.Equal(preFrac, ship.Fraction, 4);
        Assert.Equal(1f, ship.Fraction, 4);

        Console.WriteLine($"P466-FRIENDLY: same-side ship velocity.X {ship.Velocity.X:0.#} (still inbound — NOT bounced), " +
                          $"pos-X-relative {ship.Position.X - center.X:0.##} (inside contact {contact:0.##}), fraction {ship.Fraction:0.####} (no damage). " +
                          "Dock exclusion held — ResolveDockCollisions handles the landing.");
    }

    // ══ SCENARIO 4 — a shield-down hostile board hull FLIES IN and captures ══
    /// <summary>⭐ P497 — the capture-reachability loopback. A DIFFERENT-side
    /// <c>c_habmBoard</c> hull, with the station's shields below the downed
    /// constant, is coasted ballistically (<see cref="ShipControlState.CoastButton"/> —
    /// no autopilot, no bot law, no dodge) in OPEN FLIGHT from OUTSIDE the hull
    /// straight at the station centre. Because the dock point
    /// (<see cref="Sector.DockRadius"/> = 200) lies DEEP inside a base whose
    /// collision radius (300 here) exceeds it, the ship must physically traverse
    /// the hull to reach it. The board exclusion (a different-side shield-down
    /// board hull) must let it through; then <see cref="ResolveDockCollisions"/>
    /// captures when it crosses <see cref="Sector.DockRadius"/>. This is the
    /// open-flight variant P450's capture test lacks (that test teleports the
    /// capturer to a spot already inside the dock radius — <c>AtBase</c>, dist 0).
    /// Passing here proves the SERVER reachability path works; any live-run failure
    /// to capture therefore names the BOT (drive/dodge law), not the server.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hostile_shield_down_board_hull_flying_in_from_outside_the_hull_captures()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        short sideA = mission.Sides[0].Id;
        short sideB = mission.Sides[1].Id;
        Assert.NotEqual(sideA, sideB);

        // A station whose collision radius (300) comfortably exceeds the 200 u
        // DockRadius, so the dock point is genuinely buried inside the hull.
        var (center, sector, station) = PlantStation(mission, 9340, sideA, radius: 300f);
        station.ReceiveDamage(0, 100f, 0.0, Vector3.Zero, Vector3.Zero, launcher: null);   // drain the shield to 0 (< 0.1 downed)
        Assert.Equal(0f, station.ShieldFraction, 3);

        // A real c_habmBoard hull.
        var board = mission.Core.HullTypes.Values
            .First(h => h.HasCapability(DataHullTypeIGC.HabmBoard));
        Assert.True(board.HasCapability(DataHullTypeIGC.HabmBoard));

        float shipRadius = 0.5f * board.Length;
        float surface = station.Radius + shipRadius;                    // the hull contact radius (~315)

        // Coast ballistically from clearly OUTSIDE the hull, straight at the centre.
        var ship = new Ship(9440, board, center + new Vector3(station.Radius + 3f * shipRadius, 0f, 0f),
            new Vector3(-ApproachSpeed, 0f, 0f), Orientation.Identity);
        ship.StateM = ShipControlState.CoastButton;
        ship.SideId = sideB;
        mission.PlaceShip(ship, sector);

        float startDist = (ship.Position - center).Length();
        Assert.True(startDist > surface, $"must start outside the hull (start {startDist:0.#} > surface {surface:0.#})");

        // Enough ticks to traverse the hull and cross DockRadius (≈ (345-200)/50 ≈ 3),
        // with margin.
        Pump(mission, 12);

        float endDist = (ship.Position - center).Length();
        Console.WriteLine($"P497-LOOPBACK: board hull side {ship.SideId} coasted from {startDist:0.#} to {endDist:0.#} u " +
                          $"(dock point ≤ {Sector.DockRadius:0.#}, hull surface {surface:0.#}); station side {station.SideId}");

        // The ship physically reached the dock point (traversed the hull)…
        Assert.True(endDist <= Sector.DockRadius + 1f,
            $"shield-down board hull must reach the dock point; ended at {endDist:0.#} u (> {Sector.DockRadius} DockRadius) — server held it at the hull");
        // …and captured (the station re-sides to the capturer).
        Assert.True(station.SideId == sideB, $"shield-down board hull must capture the station; side {station.SideId}");
    }
}
