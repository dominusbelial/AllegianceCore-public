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
/// P452 — live (real in-process <see cref="Srv"/> over loopback) and domain
/// proofs that <see cref="Sector.ResolveMineDetonations"/> implements the
/// native mineFIELD as persistent area denial (attrition, not a one-shot
/// bomb), ported from <c>CmineIGC::HandleCollision</c>
/// (<c>src/Igc/mineigc.h:68-145</c>):
/// <list type="bullet">
/// <item>Only a ship on a <b>different</b>, non-allied side, only after the
/// 1.0 s arm delay, and only while <b>moving</b> (<c>|v|² &gt; 1</c>,
/// <c>mineigc.h:80-82</c>) is affected. A ship parked inside an armed field
/// takes <b>zero</b> damage and the field survives.</item>
/// <item>Each in-field tick, the ship's dwell time in the field is computed
/// from the native sphere-intersection quadratic (<c>mineigc.h:84-113</c>)
/// and the field suffers the native endurance wear
/// (<c>amount = dt·speed²·Power·rHim²·c_damageMultiplier/(2·rMe)</c>,
/// <c>mineigc.h:116-118</c>) via the exponential decay shared with
/// <see cref="Mine.ReduceStrength"/>; the ship takes the derived <c>damage</c>
/// (<c>mineigc.h:120-128</c>).</item>
/// <item>The worst mine hit per ship per tick is applied once through the
/// reused <see cref="Ship.ReceiveDamage"/> (shields included) — the port of
/// native <c>AddMineDamage</c>/<c>ApplyMineDamage</c>
/// (<c>shipIGC.h:1998-2035</c>) — with no area <see cref="Explosion"/>.</item>
/// <item>The field persists across hits and ships; several ships in the same
/// tick all take damage. It is destroyed (<c>MineDestroyedEvent</c>) only when
/// its endurance drops below 1 (<c>mineigc.h:139-142</c>), or by its lifespan
/// (unchanged).</item>
/// </list>
/// </summary>
/// <remarks>
/// Everything is data-derived from the real <c>PCore001.igc</c> static core —
/// nothing hardcoded. The expected damage in the moving-crossing test is
/// hand-computed in-test from the decoded <c>Power</c>/<c>Endurance</c>/radius
/// of the selected mine type and the hull's radius / armor matrix. Live
/// assertions read <c>Ship.Fraction</c> / <c>Ship.ShieldFraction</c> /
/// <c>Mine.Fraction</c> directly off the server-side objects (ground truth);
/// the domain tests control the exact tick <c>dt</c> so the native formula is
/// reproducible to within tolerance.
/// </remarks>
[Collection("LiveNetwork")]
public sealed class MineCombatTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static float Dtm(StaticCore core, byte dmg, byte def) => core.Constants.DamageConstants[dmg][def];

    // Native constants (mineigc.h:79,117).
    private const float C_DamageMultiplier = 1.0f / (5.0f * 5.0f * 100.0f);
    private const float Dt = 0.05f;          // 20 Hz tick (matches server config)
    private const float SlowSpeed = 40f;     // |v|² = 1600 ≫ 1 (gate), full-tick occupancy
    private const float FastSpeed = 80f;

    /// <summary>Optionalisation-free: the exclusive-OR for a speed/gate sanity
    /// check is spelled out in each test; no global flags.</summary>

    // ── data-derived combos (expected values hand-computed in-test) ─────────

    private sealed record NativeHit(
        DataMineTypeIGC Mine, DataHullTypeIGC Hull,
        float RMe, float RHim, float Speed, float Damage);

    /// <summary>A (mine, hull, speed) combo where a centred moving ship takes a
    /// clean, survivable single-tick native-formula hit that does <b>not</b>
    /// destroy the field. The ship's expected <c>damage</c> (the endurance
    /// consumed, <c>mineigc.h:120-128</c> with initial fraction 1) is computed
    /// here from the real decoded data and asserted against in the test.</summary>
    private static readonly Lazy<NativeHit> SingleTick = new(() =>
    {
        var core = RepresentativeCore();
        const float speed = SlowSpeed;
        foreach (var m in core.MineTypes.Values
            .Where(mm => mm.MineRadius > 0f && mm.Power > 0f && mm.Endurance > 0f && mm.Lifespan >= 2.5f)
            .OrderBy(mm => mm.Power))
        {
            foreach (var hull in core.HullTypes.Values.Where(h => h.Length > 0f).OrderBy(h => h.HitPoints))
            {
                float rMe = m.MineRadius, rHim = 0.5f * hull.Length;
                if (!TryDamage(core, m, hull, rMe, rHim, speed, out float damage)) continue;
                // Single-tick hull fraction swing (proxy; GA absorbed by the test's
                // real EffectiveMaxHitPoints): measurable and survivable.
                float dtm = Dtm(core, m.DamageType, hull.DefenseType);
                float delta = damage * dtm / hull.HitPoints;
                if (delta is >= 0.004f and <= 0.090f)
                    return new NativeHit(m, hull, rMe, rHim, speed, damage);
            }
        }
        throw new InvalidOperationException("no native single-tick mine combo in representative core");
    });

    /// <summary>Computes the native in-field <c>amount</c> and the resulting
    /// ship <c>damage</c> for one full tick, mirroring
    /// <c>mineigc.h:116-128</c>. Skips the combo when the ship would not stay
    /// inside the field for the whole tick (so the hand-compute is exact) or
    /// when the single tick would destroy the field.</summary>
    private static bool TryDamage(
        StaticCore core, DataMineTypeIGC m, DataHullTypeIGC hull,
        float rMe, float rHim, float speed, out float damage)
    {
        damage = 0f;
        float r = rMe + rHim;
        if (speed * speed <= 1f) return false;             // velocity gate (mineigc.h:80-82)
        if (r / speed <= 2f * Dt) return false;            // full-tick occupancy for the hand-compute
        float amount = Dt * (speed * speed) * m.Power * rHim * rHim * C_DamageMultiplier / (2f * rMe);
        float E = m.Endurance;
        float newEndurance = E * MathF.Exp(-amount / E);
        if (newEndurance < 1f) return false;               // field must survive one tick (mineigc.h:139-142)
        damage = E - newEndurance;                         // = oldEndurance − newEndurance (fraction 1)
        return true;
    }

    private sealed record SpeedPair(
        DataMineTypeIGC Mine, DataHullTypeIGC Hull,
        float RMe, float RHim, float SlowDamage, float FastDamage);

    /// <summary>A combo where a 2× faster crossing should take ~4× the damage
    /// (speed² scaling) with both crossings full-tick, survivable, and not
    /// field-destroying in a single tick.</summary>
    private static readonly Lazy<SpeedPair> Speeds = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var m in core.MineTypes.Values
            .Where(mm => mm.MineRadius > 0f && mm.Power > 0f && mm.Endurance > 0f && mm.Lifespan >= 2.5f)
            .OrderBy(mm => mm.Power))
        {
            foreach (var hull in core.HullTypes.Values.Where(h => h.Length > 0f).OrderBy(h => h.HitPoints))
            {
                float rMe = m.MineRadius, rHim = 0.5f * hull.Length;
                float r = rMe + rHim;
                if (r / FastSpeed <= 2f * Dt) continue;    // even the fast ship is full-tick
                float E = m.Endurance;
                float aSlow = Dt * SlowSpeed * SlowSpeed * m.Power * rHim * rHim * C_DamageMultiplier / (2f * rMe);
                float aFast = Dt * FastSpeed * FastSpeed * m.Power * rHim * rHim * C_DamageMultiplier / (2f * rMe);
                float dSlow = E * (1f - MathF.Exp(-aSlow / E));
                float dFast = E * (1f - MathF.Exp(-aFast / E));
                // Deep linear regime (aSlow ≪ E) so the ratio is ≈ (Fast/Slow)² = 4
                // despite the exp saturation; both damages must be measurable but
                // survivable, and the fast crossing must not destroy the field.
                float x = aSlow / E;
                if (x < 0.004f || x > 0.032f) continue;
                if (E - dFast < 1f) continue;
                float dtm = Dtm(core, m.DamageType, hull.DefenseType);
                if (dSlow * dtm / hull.HitPoints < 0.004f) continue;   // slow hit measurable
                if (dFast * dtm / hull.HitPoints >= 0.30f) continue;    // fast hit survivable
                return new SpeedPair(m, hull, rMe, rHim, dSlow, dFast);
            }
        }
        throw new InvalidOperationException("no speed-pair mine combo in representative core");
    });

    private sealed record WearHit(DataMineTypeIGC Mine, DataHullTypeIGC Hull, float Speed);

    /// <summary>A combo that wears out after a couple of fast crossings: each
    /// crossing consumes a healthy slice of the field's endurance (without
    /// destroying it in one), and the victim survives the ~E damage each time.
    /// The test then proves repeated crossings drive <c>newEndurance &lt; 1</c>
    /// → <c>MineDestroyedEvent</c>.</summary>
    private static readonly Lazy<WearHit> Wear = new(() =>
    {
        var core = RepresentativeCore();
        float[] speeds = { 100f, 200f, 300f, 400f };
        foreach (var m in core.MineTypes.Values
            .Where(mm => mm.MineRadius > 0f && mm.Power > 0f && mm.Endurance >= 4f && mm.Lifespan >= 2.5f)
            .OrderBy(mm => mm.Endurance))
        {
            foreach (var hull in core.HullTypes.Values.Where(h => h.Length > 0f).OrderByDescending(h => h.HitPoints))
            {
                float rMe = m.MineRadius, rHim = 0.5f * hull.Length;
                float dtm = Dtm(core, m.DamageType, hull.DefenseType);
                if (dtm <= 0f) continue;
                foreach (float speed in speeds)
                {
                    float r = rMe + rHim;
                    if (r / speed <= 2f * Dt) continue;
                    float E = m.Endurance;
                    float amount = Dt * speed * speed * m.Power * rHim * rHim * C_DamageMultiplier / (2f * rMe);
                    // One crossing takes 1.2–3 endurance-budgets: not fatal to the
                    // field, but 2–3 crossings drive it below endurance 1.
                    if (amount < 1.2f * E || amount > 3f * E) continue;
                    // Survivability of a single crossing for the victim (~E damage).
                    if (E * dtm / hull.HitPoints >= 0.5f) continue;
                    return new WearHit(m, hull, speed);
                }
            }
        }
        throw new InvalidOperationException("no wear-out mine combo in representative core");
    });

    // ── live loopback helpers (3-bot / 2-side construction, P91 recipe) ──────

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Mine Combat Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 942u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 88000000000000000ul, DrmHash = "minecombat",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A booted 3-bot / 2-side live server plus the resolved roles —
    /// the exact <see cref="MissileCombatTests"/> harness (slot 0 the
    /// shooter at the origin, slot 1 the cross-side enemy at <c>(0,0,−gap)</c>,
    /// slot 2 the same-side observer far off; sides round-robin A,B,A). The
    /// mine tests reposition / give velocity to the ships at will — the mine
    /// never moves, so only relative distance to its radius matters.</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship Shooter = null!, Enemy = null!, Observer = null!;
        public FltBot ShooterBot = null!, EnemyBot = null!, ObserverBot = null!;
        public StaticCore Core = null!;

        public void Dispose()
        {
            foreach (var b in new[] { ShooterBot, EnemyBot, ObserverBot }) b?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatThree(float gap, DataHullTypeIGC hull)
    {
        var seat = new Seated();
        var core = RepresentativeCore();
        seat.Core = core;
        seat.Server = new Srv();

        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            var ship = placed switch
            {
                0 => new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity),
                1 => new Ship(id, hull, new Vector3(0f, 0f, -gap), Vector3.Zero, Orientation.Identity),
                _ => new Ship(id, hull, new Vector3(0f, 820f, 920f), Vector3.Zero, Orientation.Identity),
            };
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B,A
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p452_shooter", "p452_enemy", "p452_observer" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            foreach (var bot in bots)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                if (ship.Position.Length() < 1f) { seat.Shooter = ship; seat.ShooterBot = bot; }
                else if (MathF.Abs(ship.Position.Z + gap) < 2f) { seat.Enemy = ship; seat.EnemyBot = bot; }
                else { seat.Observer = ship; seat.ObserverBot = bot; }
            }
            Assert.NotNull(seat.Shooter); Assert.NotNull(seat.Enemy); Assert.NotNull(seat.Observer);
            Assert.True(seat.Shooter.SideId != seat.Enemy.SideId, "enemy must be cross-side to the shooter");
            Assert.True(seat.Shooter.SideId == seat.Observer.SideId, "observer must be same-side to the shooter");
            seat.Shooter.Velocity = Vector3.Zero;
            seat.Enemy.Velocity = Vector3.Zero;
            seat.Observer.Velocity = Vector3.Zero;
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    /// <summary>The distance from the mine's centre a ship is parked at for the
    /// live tests — small enough to be comfortably inside
    /// <c>MineRadius + 0.5·shipLength</c> for any selected combo, but non-zero.</summary>
    private const float ParkDistance = 5.0f;

    // ── live: STATIONARY immunity (regression — fails on the old one-shot) ──

    /// <summary>⭐ Requirement (a). A cross-side enemy ship parked <b>stationary</b>
    /// inside an armed field takes <b>zero</b> damage over several ticks and the
    /// mine survives. This is exactly the scenario the old single-hit port broke
    /// (it detonated the mine at full <c>Power</c> and consumed it); under native
    /// semantics (<c>mineigc.h:80-82</c>) a parked ship is immune.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Stationary_enemy_ship_parked_inside_an_armed_field_takes_zero_damage_and_the_mine_survives()
    {
        Require.Artwork();
        var (mineType, hull, _) = (SingleTick.Value.Mine, SingleTick.Value.Hull, 0);
        float rMe = mineType.MineRadius;
        float blastOffset = MathF.Min(ParkDistance, 0.999f * (rMe + 0.5f * hull.Length));
        using var seat = SeatThree(100f, hull);
        var shooter = seat.Shooter;
        var enemy = seat.Enemy;

        var mission = shooter.Mission!;
        double now0 = mission.LastUpdate;
        var mine = shooter.DeployMine(mineType, now0);
        short mId = mine.Id;
        Assert.Contains(mine, mine.OwnerSector.Models);

        // Park the cross-side enemy INSIDE the armed field, velocity stays zero.
        enemy.Position = mine.Position + new Vector3(0f, 0f, -blastOffset);
        enemy.Velocity = Vector3.Zero;
        float f0 = enemy.Fraction;
        Assert.Equal(1f, f0, 3);

        // Pump well past the arm delay and through several further ticks.
        PumpUntil(seat.Server, () => mission.LastUpdate >= now0 + Mine.ArmTimeSeconds + 0.6, 1200);
        Assert.True(mission.LastUpdate >= now0 + Mine.ArmTimeSeconds, "must have armed");

        // Zero damage, mine survives, no destruction broadcast.
        Assert.Equal(f0, enemy.Fraction, 3);
        Assert.Contains(mine, mine.OwnerSector.Models);
        Assert.Equal(1f, mine.Fraction, 3);
        Assert.DoesNotContain(seat.ObserverBot.MineDestroyeds, d => d.MineId == mId);
        Console.WriteLine($"P452-STATIONARY: parked cross-side enemy stayed at {enemy.Fraction:F3}, " +
                          $"mine survived at fraction {mine.Fraction:F3}, no destroy broadcast");
    }

    // ── live: same-side immunity, then a moving cross-side attrition hit ────

    /// <summary>⭐ Requirements (f) + continuous chew. A <b>same-side</b> moving
    /// ship crossing the field takes nothing (friendly gate), the field is
    /// untouched. A <b>cross-side</b> moving ship then takes real attrition
    /// damage while the field <em>survives</em> with a reduced
    /// <see cref="Mine.Fraction"/> — the persistence half of the native
    /// minefield (the old port exploded instead).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Same_side_moving_ship_is_immune_while_a_cross_side_moving_ship_wears_the_field_without_consuming_it()
    {
        Require.Artwork();
        var (mineType, hull, _) = (SingleTick.Value.Mine, SingleTick.Value.Hull, 0);
        float rMe = mineType.MineRadius;
        float blastOffset = MathF.Min(ParkDistance, 0.999f * (rMe + 0.5f * hull.Length));
        using var seat = SeatThree(100f, hull);
        var shooter = seat.Shooter;
        var enemy = seat.Enemy;
        var observer = seat.Observer;      // same side as the shooter — the friendly mover

        var mission = shooter.Mission!;
        double now0 = mission.LastUpdate;
        var mine = shooter.DeployMine(mineType, now0);
        Assert.Contains(mine, mine.OwnerSector.Models);
        float frac0 = mine.Fraction;
        Assert.Equal(1f, frac0, 3);

        // Relocate the launcher clear of its own field (the mine stays at the
        // deploy point, but its Launcher pointer is position-independent) so the
        // transiting ships never collide with it.
        shooter.Position = new Vector3(0f, 5060f, 940f);

        // Clear the cross-side enemy out of the way; drive the same-side observer
        // through the armed field MOVING (it must still be immune: friendly gate).
        enemy.Position = new Vector3(0f, 5000f, 5090f);
        enemy.Velocity = Vector3.Zero;
        observer.Position = mine.Position + new Vector3(0f, 0f, -blastOffset);
        observer.Velocity = new Vector3(0f, 0f, 60f);   // moving, same-side
        float obsStart = observer.Fraction;
        PumpUntil(seat.Server, () => mission.LastUpdate >= now0 + Mine.ArmTimeSeconds + 0.3, 800);
        Assert.Equal(obsStart, observer.Fraction, 3);       // (f) friendly moving ship: no damage
        Assert.Equal(frac0, mine.Fraction, 3);              // field unworn by the friendly
        Assert.Contains(mine, mine.OwnerSector.Models);

        // Park the friendly observer well clear too (otherwise the transiting
        // cross-side enemy collides with it). Friendliness is already proven above.
        observer.Position = new Vector3(0f, 5150f, 955f);
        observer.Velocity = Vector3.Zero;
        enemy.Position = mine.Position + new Vector3(0f, 0f, blastOffset);
        enemy.Velocity = new Vector3(0f, 0f, -60f);
        float enemyStart = enemy.Fraction;
        PumpUntil(seat.Server, () => enemy.Fraction < enemyStart - 0.002f, 600);
        Assert.True(enemy.Fraction < enemyStart, "cross-side moving ship must take attrition damage");
        Assert.Contains(mine, mine.OwnerSector.Models);             // NOT consumed — persists
        Assert.True(mine.Fraction < frac0, "field must be worn by the cross-side crossing");
        Console.WriteLine($"P452-WEAR: friendly mover stayed {obsStart:F3}; cross-side mover took " +
                          $"{enemyStart - enemy.Fraction:F3}; field {frac0:F2}→{mine.Fraction:F3}, mine persists");
    }

    // ── domain: native-formula single-tick attrition ────────────────────────

    /// <summary>A 2-side domain mission (CountingSite) with a mine deployed at a
    /// remote, collision-free coordinate and the launcher kept at the origin —
    /// so enemies can be driven straight through the field without any
    /// ship-vs-ship / ship-vs-world interference muddying the mine maths.</summary>
    private sealed record Domain2(StaticCore Core, Mission Mission, Sector Sector, CountingSite Site, Ship Launcher, DataHullTypeIGC Hull);

    private static Domain2 AssembleTwoSide(DataHullTypeIGC hull)
    {
        var core = RepresentativeCore();
        // Remote isolates mine attrition from map geometry. Keep that fixture
        // inside its loaded universe so unrelated OOB damage cannot enter the math.
        core.Constants.FloatConstants[1] = 1_000_000f;
        var site = new CountingSite();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var mission = Mission.Assemble(core, SomeMap(),
            [new SideSpec(0, "Alpha", civ), new SideSpec(1, "Bravo", civ)], site);
        mission.Stage = MissionStage.Started;
        var sector = mission.Sectors[0];
        var launcher = new Ship(701, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        launcher.SideId = 0;
        mission.PlaceShip(launcher, sector);
        return new Domain2(core, mission, sector, site, launcher, hull);
    }

    /// <summary>Adds a cross-side (side 1) enemy at <paramref name="pos"/> with
    /// velocity <paramref name="vel"/>; returns it. Distinct ids so several can
    /// share a sector.</summary>
    private static Ship AddEnemy(Domain2 d, short id, Vector3 pos, Vector3 vel)
    {
        var e = new Ship(id, d.Hull, pos, vel, Orientation.Identity);
        e.SideId = 1;
        d.Mission.PlaceShip(e, d.Sector);
        return e;
    }

    private static Mine DeployRemote(Domain2 d, DataMineTypeIGC m, Vector3 at)
    {
        var mine = d.Launcher.DeployMine(m, d.Mission.LastUpdate);
        mine.Position = at;               // mine is static; relocate it clear of the launcher
        return mine;
    }

    private static void Step(Domain2 d) => d.Mission.Update(d.Mission.LastUpdate + Dt);

    /// <summary>Advances ~1.1 s of ticks (22 × <see cref="Dt"/>) so a just-deployed
    /// mine clears its 1.0 s arm delay (<see cref="Mine.ArmTimeSeconds"/>) before
    /// any moving victim is introduced. No victim is present during arming and the
    /// remote mine is nowhere near the launcher, so this wears nothing.</summary>
    private static void ArmPastDelay(Domain2 d)
    {
        for (int i = 0; i < 22; i++) Step(d);
    }

    /// <summary>The hull fraction a ship ends at after one fresh, full-tick native
    /// attrition hit — the independent hand-compute of the native formula
    /// (<c>mineigc.h:116-128</c>) the formula tests assert against. It uses the
    /// sim's <b>actual velocity</b> when the mine pass ran. P762b moved the mine
    /// pass after the collision passes (native clusterIGC.cpp:322-345), and this
    /// port's per-ship Move runs earlier in OnUpdate, so the pass sees the
    /// post-Move (damped) velocity: callers capture that velocity after Step.
    /// (Native's own pass sees the tick-start velocity because its Move follows
    /// the mine pass; that ordering half is a stated port deviation.) Assumes
    /// the ship stays inside
    /// the field the whole tick (enforced by the selectors' full-tick occupancy
    /// filter) and the mine was at fraction 1 when hit.</summary>
    private static float NativeOneTickFraction(Domain2 d, Mine mine, Ship ship, Vector3 passVelocity)
    {
        float vx = passVelocity.X, vy = passVelocity.Y, vz = passVelocity.Z;
        float a = vx * vx + vy * vy + vz * vz;
        float rMe = mine.MineType.MineRadius;
        float rHim = 0.5f * ship.HullType.Length;
        float amount = Dt * a * mine.MineType.Power * rHim * rHim * C_DamageMultiplier / (2f * rMe);
        float E = mine.MineType.Endurance;
        float damage = E * (1f - MathF.Exp(-amount / E));   // oldEndurance = E (fraction 1)
        float dtm = Dtm(d.Core, mine.MineType.DamageType, ship.HullType.DefenseType);
        return 1f - damage * dtm / ship.EffectiveMaxHitPoints();
    }

    // A far, world-clean spot: no station/warp/asteroid interference, no ship.
    private static readonly Vector3 Remote = new(3000f, 5400f, 7900f);

    /// <summary>⭐ Requirement (b). A moving cross-side ship crossing the field
    /// takes damage matching the native formula within tolerance: the test
    /// hand-computes <c>damage = endurance·(1 − exp(−amount/endurance))</c> from
    /// the real <c>Power</c>/<c>Endurance</c>/radius of <c>PCore001.igc</c> and
    /// the hull's radius and armor matrix, then asserts the ship's hull fraction
    /// fell by <c>damage·dtm/maxHP</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Moving_cross_side_ship_takes_exact_native_formula_attrition_damage()
    {
        Require.Artwork();
        var hit = SingleTick.Value;
        var d = AssembleTwoSide(hit.Hull);
        var mine = DeployRemote(d, hit.Mine, Remote);
        ArmPastDelay(d);
        var enemy = AddEnemy(d, 810, Remote + new Vector3(0f, 0f, -2f), new Vector3(hit.Speed, 0f, 0f));

        // One controlled tick, then hand-compute the expected hull fraction from
        // the native formula using the velocity the mine pass actually saw
        // (post-Move, because this port moves ships before the collision/mine
        // passes; see NativeOneTickFraction's note).
        Step(d);
        var passVelocity = enemy.Velocity;
        float expected = NativeOneTickFraction(d, mine, enemy, passVelocity);
        Assert.InRange(expected, 0f, 0.998f);
        Assert.Equal(expected, enemy.Fraction, 2);
        Assert.Contains(mine, d.Sector.Models);          // field survives a single gentle crossing
        Console.WriteLine($"P452-NATIVE: hull 1→{enemy.Fraction:F4} (hand-computed {expected:F4}); mine survived");
    }

    /// <summary>⭐ Requirement (c). Two cross-side ships in the same tick both
    /// take damage and the field survives with a reduced <see cref="Mine.Fraction"/>
    /// — the native multi-ship-per-tick behaviour (<c>mineigc.h:128</c>: “any
    /// number of ships can be hit by the same field in the same tick”).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Two_enemy_ships_crossing_in_the_same_tick_both_take_damage_and_the_field_survives_worn()
    {
        Require.Artwork();
        var hit = SingleTick.Value;
        var d = AssembleTwoSide(hit.Hull);
        var mine = DeployRemote(d, hit.Mine, Remote);
        ArmPastDelay(d);
        float frac0 = mine.Fraction;
        // Same velocity ⇒ no relative approach ⇒ no ship-ship bounce muddies the maths.
        var e1 = AddEnemy(d, 811, Remote + new Vector3(0f, 0f, -3f), new Vector3(hit.Speed, 0f, 0f));
        var e2 = AddEnemy(d, 812, Remote + new Vector3(0f, 0f, 3f), new Vector3(hit.Speed, 0f, 0f));
        float f1 = e1.Fraction, f2 = e2.Fraction;

        Step(d);

        Assert.True(e1.Fraction < f1, "enemy 1 must take attrition damage");
        Assert.True(e2.Fraction < f2, "enemy 2 must take attrition damage");
        Assert.Contains(mine, d.Sector.Models);              // field survives both hits
        Assert.True(mine.Fraction < frac0, "field must be worn by both ships combined");
        Assert.True(mine.Fraction > 0f, "field must still be alive after one tick");
        Console.WriteLine($"P452-TWO: hulls {f1:F3}→{e1.Fraction:F3}, {f2:F3}→{e2.Fraction:F3}; " +
                          $"field {frac0:F2}→{mine.Fraction:F3}, survives");
    }

    /// <summary>⭐ Requirement (d). A faster crossing takes more damage than a
    /// slower one over the same field geometry, scaling with speed²
    /// (<c>amount ∝ dt·speed²</c>): the 2× faster ship takes ~4× the damage.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Faster_crossing_takes_more_damage_scaling_with_speed_squared()
    {
        Require.Artwork();
        var sp = Speeds.Value;
        var dtm = Dtm(RepresentativeCore(), sp.Mine.DamageType, sp.Hull.DefenseType);

        var d1 = AssembleTwoSide(sp.Hull);
        var m1 = DeployRemote(d1, sp.Mine, Remote);
        ArmPastDelay(d1);
        var slow = AddEnemy(d1, 813, Remote + new Vector3(0f, 0f, -2f), new Vector3(SlowSpeed, 0f, 0f));
        Step(d1);
        var slowVelocity = slow.Velocity;
        float slowMaxHp = slow.EffectiveMaxHitPoints();
        float slowDamage = (1f - slow.Fraction) * slowMaxHp / dtm;
        Assert.Equal(NativeOneTickFraction(d1, m1, slow, slowVelocity), slow.Fraction, 2);

        var d2 = AssembleTwoSide(sp.Hull);
        var m2 = DeployRemote(d2, sp.Mine, Remote);
        ArmPastDelay(d2);
        var fast = AddEnemy(d2, 814, Remote + new Vector3(0f, 0f, -2f), new Vector3(FastSpeed, 0f, 0f));
        Step(d2);
        var fastVelocity = fast.Velocity;
        float fastMaxHp = fast.EffectiveMaxHitPoints();
        float fastDamage = (1f - fast.Fraction) * fastMaxHp / dtm;
        Assert.Equal(NativeOneTickFraction(d2, m2, fast, fastVelocity), fast.Fraction, 2);

        // Damage is proportional to speed² (amount ∝ dt·|v|²), so the 2× faster
        // ship takes ~4× the damage. Both ships are damped proportionally, so the
        // measured ratio still tracks (Fast/Slow)² within tolerance.
        float ratio = fastDamage / slowDamage;
        float expectedRatio = (FastSpeed * FastSpeed) / (SlowSpeed * SlowSpeed);   // = 4
        Assert.InRange(ratio, 0.9f * expectedRatio, 1.1f * expectedRatio);
        Assert.True(fastDamage > slowDamage, "faster ship must take strictly more damage");
        Console.WriteLine($"P452-SPEED: slow damage {slowDamage:F3}, fast damage {fastDamage:F3}, ratio {ratio:F2} (expect ~{expectedRatio})");
    }

    /// <summary>⭐ Requirement (e). Repeated crossings eventually wear the field
    /// below endurance 1, and it is destroyed with exactly one
    /// <see cref="IIgcSite.MineDestroyedEvent"/> through the existing path.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Repeated_crossings_wear_the_field_to_destruction_with_mine_destroyed_event()
    {
        Require.Artwork();
        var wear = Wear.Value;
        var d = AssembleTwoSide(wear.Hull);
        var mine = DeployRemote(d, wear.Mine, Remote);
        ArmPastDelay(d);
        short mId = mine.Id;
        float frac0 = mine.Fraction;

        int crossings = 0;
        for (int i = 0; i < 40 && d.Sector.Models.Contains(mine); i++)
        {
            // A fresh crossing victim each time (a single victim would die before
            // exhausting the field's endurance budget — see report).
            var victim = AddEnemy(d, (short)(900 + i), Remote + new Vector3(0f, 0f, -2f), new Vector3(wear.Speed, 0f, 0f));
            Step(d);
            crossings++;
        }

        Assert.False(d.Sector.Models.Contains(mine), "repeated crossings must eventually destroy the field");
        Assert.Equal(1, d.Site.MineDestroys);            // exactly one MineDestroyedEvent
        Console.WriteLine($"P452-WEAROUT: field {frac0:F2} destroyed after {crossings} crossings; " +
                          $"{d.Site.MineDestroys} MineDestroyedEvent (mine {mId})");
    }

    /// <summary>⭐ Requirement (f) domain half. A same-side (friendly) moving ship
    /// crossing the field takes nothing — even while moving and squarely inside
    /// the radius, the friendly gate (<c>mineigc.h:77</c>) protects it and the
    /// field is unworn.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Same_side_moving_ship_crossing_the_field_takes_nothing_and_does_not_wear_it()
    {
        Require.Artwork();
        var hit = SingleTick.Value;
        var d = AssembleTwoSide(hit.Hull);
        var mine = DeployRemote(d, hit.Mine, Remote);
        ArmPastDelay(d);
        float frac0 = mine.Fraction;
        var friendly = new Ship(815, hit.Hull, Remote + new Vector3(0f, 0f, -2f), new Vector3(hit.Speed, 0f, 0f), Orientation.Identity);
        friendly.SideId = 0;                              // same side as the launcher
        d.Mission.PlaceShip(friendly, d.Sector);
        float f0 = friendly.Fraction;

        Step(d);
        Assert.Equal(f0, friendly.Fraction, 3);           // no damage
        Assert.Equal(frac0, mine.Fraction, 3);            // field unworn
        Assert.Contains(mine, d.Sector.Models);
        Console.WriteLine($"P452-FRIENDLY: same-side mover stayed {friendly.Fraction:F3}, field {frac0:F2}→{mine.Fraction:F3}");
    }

    /// <summary>⭐ Shield path preserved under native attrition (converted from the
    /// old one-shot shield test): a cross-side moving ship with a mounted shield
    /// crossing the field takes the attrition hit through the reused
    /// <see cref="Ship.ReceiveDamage"/> shield path — the shield absorbs it and the
    /// hull is untouched, exactly as a weapon/missile hit behaves.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cross_side_moving_ship_with_a_mounted_shield_absorbs_the_attrition_hit_like_a_weapon_hit()
    {
        Require.Artwork();
        var hit = SingleTick.Value;
        var d = AssembleTwoSide(hit.Hull);
        float rMe = hit.Mine.MineRadius, rHim = 0.5f * hit.Hull.Length;

        // Seek (speed, shield) so the crossing is full-tick and the shield absorbs
        // a healthy, computable slice (0.03–0.6 of its raw strength) — i.e. the
        // old shield test's intent under the new attrition formula.
        float[] speeds = { 120f, 250f, 400f, 600f };
        float chosenSpeed = 0f;
        DataPartTypeIGC? shieldPart = null;
        DataPartTypeIGC.Shield? shieldType = null;
        foreach (float s in speeds)
        {
            if (rMe + rHim <= 2f * s * Dt) continue;   // not full-tick at this speed
            float amount = Dt * s * s * hit.Mine.Power * rHim * rHim * C_DamageMultiplier / (2f * rMe);
            float damage = hit.Mine.Endurance * (1f - MathF.Exp(-amount / hit.Mine.Endurance));
            foreach (var x in d.Core.PartTypes.Values
                .Where(p => p.EquipmentType == EquipmentType.Shield
                    && p.Type is DataPartTypeIGC.Shield sh && sh.MaxStrength > 0f)
                .Select(p => (Part: p, Shield: (DataPartTypeIGC.Shield)p.Type)))
            {
                float dtmS = Dtm(d.Core, hit.Mine.DamageType, x.Shield.DefenseType);
                if (dtmS <= 0f) continue;
                float drop = damage * dtmS / x.Shield.MaxStrength;
                if (drop is >= 0.030f and <= 0.60f)
                {
                    chosenSpeed = s; shieldPart = x.Part; shieldType = x.Shield;
                    break;
                }
            }
            if (chosenSpeed > 0f) break;
        }
        Assert.True(shieldPart is not null && shieldType is not null, "no (speed, shield) pairing exists in the core");

        var mine = DeployRemote(d, hit.Mine, Remote);
        ArmPastDelay(d);
        float frac0 = mine.Fraction;
        var enemy = AddEnemy(d, 816, Remote + new Vector3(0f, 0f, -2f), new Vector3(chosenSpeed, 0f, 0f));
        enemy.MountPart(shieldPart.Value, amount: 1, mountId: 0, fractionMounted: 255);   // CombatShieldTests' shield recipe
        Assert.Equal(1.0f, enemy.ShieldFraction, 3);
        float hullStart = enemy.Fraction;

        Step(d);

        // P467b: the mounted shield's effective max strength is its side's scaled
        // (raw × c_gaMaxShieldShip), so it absorbs drop = damage·dtmShield/(MaxStrength·ga),
        // where damage comes from the native formula at the sim's actual velocity.
        float enemyShieldGa = enemy.GlobalAttributeFactor(GlobalAttributeSet.MaxShieldShip);
        float vxs = enemy.Velocity.X, vys = enemy.Velocity.Y, vzs = enemy.Velocity.Z;
        float aS = vxs * vxs + vys * vys + vzs * vzs;
        float amountS = Dt * aS * hit.Mine.Power * rHim * rHim * C_DamageMultiplier / (2f * rMe);
        float damageS = hit.Mine.Endurance * (1f - MathF.Exp(-amountS / hit.Mine.Endurance));
        float expectedDrop = damageS * Dtm(d.Core, hit.Mine.DamageType, shieldType!.DefenseType) / (shieldType.MaxStrength * enemyShieldGa);
        Assert.Equal(1f - expectedDrop, enemy.ShieldFraction, 3);
        Assert.Equal(hullStart, enemy.Fraction, 3);       // hull untouched (shield absorbed fully)
        Assert.Contains(mine, d.Sector.Models);           // field persists
        Assert.True(mine.Fraction < frac0);               // … but is worn
        Console.WriteLine($"P452-SHIELD: shield {1f}→{enemy.ShieldFraction:F3}, hull unchanged {hullStart:F3}; " +
                          $"field {frac0:F2}→{mine.Fraction:F3}");
    }

    // ── P574: per-hit mine pops at the native P2, throttled per ship ────────

    /// <summary>⭐ Requirement (P574). A cross-side ship crossing an armed field
    /// raises exactly one <see cref="IIgcSite.MineHitEvent"/> (the port of
    /// <c>ApplyMineDamage</c>'s <c>AddExplosion(P2, 1.5, c_etMine)</c>,
    /// <c>shipIGC.h:2021-2024</c>) and its P2 is placed natively
    /// (<c>mineigc.h:129-132</c>): just ahead of the ship along its velocity at
    /// distance (10 + rHim), perturbed by at most 10 (the c_radiusPlacement
    /// random offset) — i.e. <b>not</b> the field centre. This is the
    /// failing-first test for the per-hit pop: the pre-P574 port raised no
    /// MineHitEvent at all, so it failed (and sent only the wrong field-centre
    /// blast on death).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Crossing_ship_raises_one_mine_pop_at_the_native_P2_ahead_of_the_ship()
    {
        Require.Artwork();
        var hit = SingleTick.Value;
        var d = AssembleTwoSide(hit.Hull);
        var mine = DeployRemote(d, hit.Mine, Remote);
        ArmPastDelay(d);
        var enemy = AddEnemy(d, 819, Remote + new Vector3(0f, 0f, -2f), new Vector3(hit.Speed, 0f, 0f));

        Step(d);

        var pop = Assert.Single(d.Site.MineHits);
        var vel = enemy.Velocity;
        float vlen = vel.Length();
        Assert.True(vlen > 0f);
        float rHim = 0.5f * hit.Hull.Length;
        float ahead = 10f + rHim;                      // c_radiusPlacement(10) + rHim
        var dv = pop.P2 - pop.P1;
        float along = (dv.X * vel.X + dv.Y * vel.Y + dv.Z * vel.Z) / vlen;   // distance ahead along velocity
        float lu = along / vlen;
        float nx = dv.X - vel.X * lu, ny = dv.Y - vel.Y * lu, nz = dv.Z - vel.Z * lu;
        float lateral = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        Assert.InRange(along, ahead - 10f, ahead + 10f);   // within (10+rHim)±10
        Assert.InRange(lateral, 0f, 10.001f);              // random offset bound ≤ 10
        Assert.Contains(mine, d.Sector.Models);            // field survives the pop
        Console.WriteLine($"P574-POP: pop along {along:F2} (ahead {ahead:F2}±10), lateral {lateral:F2}; mine survives");
    }

    /// <summary>⭐ Requirement (P574). The 0.5 s per-ship pop throttle
    /// (<c>shipIGC.h:2021-2023</c>): two consecutive in-field ticks (0.05 s
    /// apart) produce only the FIRST pop — the second is inside the window and
    /// suppressed. The pre-P574 port had no throttle (no per-hit pop at all), so
    /// this failed-first.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Two_consecutive_in_field_ticks_produce_only_the_first_pop()
    {
        Require.Artwork();
        var hit = SingleTick.Value;
        var d = AssembleTwoSide(hit.Hull);
        var mine = DeployRemote(d, hit.Mine, Remote);
        ArmPastDelay(d);
        var enemy = AddEnemy(d, 822, Remote + new Vector3(0f, 0f, -2f), new Vector3(hit.Speed, 0f, 0f));

        Step(d);   // tick 1 → pop
        Step(d);   // tick 2 (0.05 s later) → within the 0.5 s window → suppressed

        Assert.Single(d.Site.MineHits);
        Assert.Contains(mine, d.Sector.Models);
        Console.WriteLine($"P574-THROTTLE: two ticks -> {d.Site.MineHits.Count} pop(s)");
    }

    /// <summary>⭐ Requirement (P574). The throttle RELEASES once more than 0.5 s
    /// has elapsed since the last pop: after a pop, advancing the clock past the
    /// window (white-box: stamp the last pop 0.6 s in the past) yields a second
    /// pop. Combined with the window test above, this pins the exact native
    /// boundary <c>m_maxMineTime &gt; m_timeLastMineExplosion + 0.5f</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Throttle_releases_once_more_than_half_a_second_has_elapsed()
    {
        Require.Artwork();
        var hit = SingleTick.Value;
        var d = AssembleTwoSide(hit.Hull);
        var mine = DeployRemote(d, hit.Mine, Remote);
        ArmPastDelay(d);
        var enemy = AddEnemy(d, 823, Remote + new Vector3(0f, 0f, -2f), new Vector3(hit.Speed, 0f, 0f));

        Step(d);                                   // pop #1
        Assert.Single(d.Site.MineHits);
        enemy.TimeLastMineExplosion = d.Mission.LastUpdate - 0.6;   // last pop was 0.6 s ago
        Step(d);                                   // now > last + 0.5 → eligible again

        Assert.Equal(2, d.Site.MineHits.Count);
        Assert.Contains(mine, d.Sector.Models);
        Console.WriteLine($"P574-THROTTLE-RELEASE: after 0.6 s gap -> {d.Site.MineHits.Count} pop(s)");
    }

    /// <summary>⭐ Requirement (P574). No pop for a friendly crossing nor a
    /// parked cross-side ship: both are damage-free (<c>mineigc.h:77,80-82</c>),
    /// so no hit, so no <see cref="IIgcSite.MineHitEvent"/>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Friendly_and_stationary_crossings_raise_no_mine_pop()
    {
        Require.Artwork();
        var hit = SingleTick.Value;
        var d = AssembleTwoSide(hit.Hull);
        var mine = DeployRemote(d, hit.Mine, Remote);
        ArmPastDelay(d);

        // Friendly (same-side, moving): the friendly gate suppresses damage → no pop.
        var friendly = new Ship(824, hit.Hull, Remote + new Vector3(0f, 0f, -2f), new Vector3(hit.Speed, 0f, 0f), Orientation.Identity);
        friendly.SideId = 0;
        d.Mission.PlaceShip(friendly, d.Sector);
        Step(d);
        Assert.Empty(d.Site.MineHits);
        Assert.Contains(mine, d.Sector.Models);

        // Parked cross-side enemy: velocity gate → no damage → no pop.
        var parked = new Ship(825, hit.Hull, Remote + new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity);
        parked.SideId = 1;
        d.Mission.PlaceShip(parked, d.Sector);
        Step(d);
        Assert.Empty(d.Site.MineHits);
        Console.WriteLine("P574-NOPOP: friendly + parked crossings -> 0 pops");
    }

    // ── P574: live — the per-hit pop reaches the observer at P2, not the centre ──

    /// <summary>⭐ Requirement (P574, wire level). Across a real loopback server,
    /// a cross-side ship crossing an armed field makes the same-sector observer
    /// receive an <c>EXPLOSION_EVENT(207)</c> of type <c>c_etMine</c> placed at
    /// the native P2 — <b>ahead of the ship, not the mine centre</b>. The
    /// pre-P574 port broadcast only ONE centre blast on field DEATH (no per-hit
    /// pops), so this failed-first (no Mine explosion reached the observer while
    /// the field lived).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_crossing_broadcasts_a_mine_pop_ahead_of_the_ship_not_at_the_field_centre()
    {
        Require.Artwork();
        var (mineType, hull, _) = (SingleTick.Value.Mine, SingleTick.Value.Hull, 0);
        using var seat = SeatThree(100f, hull);
        var shooter = seat.Shooter;
        var enemy = seat.Enemy;
        var observer = seat.Observer;
        var observerBot = seat.ObserverBot;

        var mission = shooter.Mission!;
        double now0 = mission.LastUpdate;
        var mine = shooter.DeployMine(mineType, now0);
        Assert.Contains(mine, mine.OwnerSector.Models);

        // Relocate the launcher + observer clear of the field.
        shooter.Position = new Vector3(0f, 8080f, 9080f);
        shooter.Velocity = Vector3.Zero;
        observer.Position = new Vector3(0f, 8090f, 9090f);
        observer.Velocity = Vector3.Zero;

        // Park the cross-side enemy just OUTSIDE the armed radius, heading in, so
        // it is mid-transit (and thus genuinely crossing) once the 1 s arm delay
        // elapses.
        float rEntrance = mineType.MineRadius + 0.5f * hull.Length + 40f;
        enemy.Position = mine.Position + new Vector3(0f, 0f, -rEntrance);
        enemy.Velocity = new Vector3(0f, 0f, 60f);

        PumpUntil(seat.Server, () => observerBot.ExplosionEvents.Any(e => e.Type == ExplosionType.Mine), 2000);

        var pop = observerBot.ExplosionEvents.First(e => e.Type == ExplosionType.Mine);
        Assert.True(mine.OwnerSector.Models.Contains(mine), "the pop must come while the field is still alive (per-hit, not death)");
        float centreDist = (pop.Position - mine.Position).Length();
        Assert.True(centreDist > 5f, "the pop must be placed ahead of the ship, not at the field centre");
        Assert.Equal(1.5f, pop.Scale, 4);
        Console.WriteLine($"[P574-LIVE] mine pop at {pop.Position} ({centreDist:F1} from centre, radius {mineType.MineRadius}), scale {pop.Scale}");
    }

    // ── domain: expiry-with-no-intruder path unchanged ──────────────────────

    /// <summary>A mine with nothing entering its radius still expires normally
    /// via <see cref="Mine.TimeExpire"/> and raises
    /// <see cref="IIgcSite.MineDestroyedEvent"/> exactly as P94 already proved —
    /// the unmodified expiry path, confirmed not to have been disturbed by the
    /// new attrition pass (a mine that never sees a moving hostile ship falls
    /// through to <see cref="Mine.OnUpdate"/>'s own expiry check).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mine_with_no_intruder_expires_via_lifespan_and_broadcasts_destroy_unmodified()
    {
        Require.Artwork();
        var core = RepresentativeCore();
        var site = new CountingSite();
        var mission = Mission.Assemble(core, SomeMap(), [new SideSpec(0, "Team", core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId)], site);
        mission.Stage = MissionStage.Started;
        var sector = mission.Sectors[0];

        var mType = core.MineTypes.Values
            .Where(m => m.Lifespan >= 2.0f && m.Lifespan < 8.0f)
            .OrderBy(m => m.Lifespan)
            .FirstOrDefault();
        Assert.True(mType.ExpendableTypeId != 0 || core.MineTypes.Count == 0, "need a mine type with 2<=lifespan<8");

        var ship = new Ship(777, core.HullTypes.Values.First(), Vector3.Zero, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(ship, sector);
        double now0 = mission.LastUpdate;
        var mine = ship.DeployMine(mType, now0);

        double t = mission.LastUpdate;
        for (int i = 0; i < 4000 && sector.Models.Contains(mine); i++)
        {
            t += 0.025;
            mission.Update(t);
        }
        Assert.DoesNotContain(mine, sector.Models);                        // consumed by expiry, not detonation
        Assert.Equal(1, site.MineDestroys);                                // exactly once (mineigc path unchanged)
        Console.WriteLine($"P452-EXPIRY: mine {mine.Id} expired at {mine.TimeExpire:F2} (life {mType.Lifespan}), " +
                          $"one MineDestroyedEvent, no attrition");
    }

    private static MissionMap SomeMap()
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count > 0) return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    }

    /// <summary>A site that counts mine-destroy broadcasts so the domain tests
    /// assert <see cref="IIgcSite.MineDestroyedEvent"/> is raised exactly once
    /// on wear-out, and records every per-hit
    /// <see cref="IIgcSite.MineHitEvent"/> (P574, with the native P1/P2) so the
    /// pop tests can assert placement and the 0.5 s per-ship throttle. Everything
    /// else falls through to the interface default (no-op).</summary>
    private sealed class CountingSite : IIgcSite
    {
        public int MineDestroys { get; private set; }
        public List<(Mine Mine, Vector3 P1, Vector3 P2)> MineHits { get; } = new();
        public void MineDestroyedEvent(Mine m) => MineDestroys++;
        public void MineHitEvent(Mine m, Vector3 p1, Vector3 p2) => MineHits.Add((m, p1, p2));
    }
}
