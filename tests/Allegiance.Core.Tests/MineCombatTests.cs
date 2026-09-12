using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P95 — live (real in-process <see cref="Srv"/> over loopback, the same
/// 3-bot / 2-side construction as <see cref="MissileCombatTests"/>) and domain
/// proofs that <see cref="Sector.ResolveMineDetonations"/> triggers a real,
/// armed, hostile-side mine on the first qualifying ship within its blast
/// radius — applying a single computed <see cref="DataMineTypeIGC.Power"/> hit
/// via the reused <see cref="Ship.ReceiveDamage"/> (shields included),
/// consuming the mine, and raising <see cref="IIgcSite.MineDestroyedEvent"/> so
/// the same-side peer who saw it deploy also receives a real
/// <c>FM_S_MINE_DESTROYED(84)</c> — the same consistency fix P91 proved for
/// missiles, now on a detonation (not just expiry). The arm delay and the
/// friendly-fire gate are proven with real, asserted behaviour.
/// </summary>
/// <remarks>
/// Everything is data-derived from the real <c>PCore001.igc</c> static core —
/// nothing hardcoded. A mine combo (mine + hull [+ shield]) is selected from
/// the core and every expected damage / shield number is computed from the
/// decoded <c>DamageConstants</c> matrix and the type records, the same receipt
/// <see cref="CombatShieldTests"/> uses. The integration shape under test is a
/// deliberate <b>point-in-sphere</b> proximity pass (not the swept
/// <see cref="CollisionDetector"/>): a ship sitting still next to a mine still
/// sets it off, and only the first qualifying ship in a tick is hit — no
/// multi-ship AoE. Live assertions read <c>Ship.Fraction</c> /
/// <c>Ship.ShieldFraction</c> directly off the server-side <see cref="Ship"/>
/// (ground truth), the same ground-truth the missile tests use.
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

    // ── combat combo selection (data-derived, mirroring MissileCombatTests) ──

    /// <summary>A (mine, hull) pair where one cross-side detonation lands a
    /// clean, survivable hull delta (<c>Power·dtmArmor/MaxHP</c>), with a
    /// <c>Lifespan</c> comfortably past the 1 s arm delay (so it triggers well
    /// before expiring on its own). Ordered by ascending Power then ascending
    /// hull HP so the least destructive hit is chosen (most stable to measure).</summary>
    private static readonly Lazy<(DataMineTypeIGC Mine, DataHullTypeIGC Hull, float ExpectedDelta)> CrossHit = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var m in core.MineTypes.Values
            .Where(mm => mm.MineRadius > 0f && mm.Power > 0f && mm.Lifespan >= 2.5f)
            .OrderBy(mm => mm.Power))
        {
            foreach (var hull in core.HullTypes.Values.OrderBy(h => h.HitPoints))
            {
                float dtm = Dtm(core, m.DamageType, hull.DefenseType);
                if (dtm <= 0f) continue;
                float delta = m.Power * dtm / hull.HitPoints;
                if (delta >= 0.035f && delta <= 0.730f)
                    return (m, hull, delta);
            }
        }
        throw new InvalidOperationException("no cross-side (mine, hull) combo in representative core");
    });

    /// <summary>A (mine, shield, hull) trio where a fresh full shield absorbs a
    /// real, computable fraction (<c>Power·dtmShield/MaxStrength</c> ∈ [0.2,
    /// 0.45]) — the CombatShieldTests "SmallHit" recipe applied to a mine
    /// warhead — AND the same raw hit is survivable on an unshielded hull (so
    /// the down-shield full-leak half is provable too).</summary>
    private static readonly Lazy<(DataMineTypeIGC Mine, DataPartTypeIGC ShieldPart, DataPartTypeIGC.Shield ShieldType, float ExpectedAbsorbed, DataHullTypeIGC Hull, float DtmHull)> ShieldCombo = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var m in core.MineTypes.Values
            .Where(mm => mm.Power > 0f && mm.Lifespan >= 2.5f)
            .OrderBy(mm => mm.Power))
        {
            foreach (var sp in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Shield))
            {
                var st = (DataPartTypeIGC.Shield)sp.Type;
                if (st.MaxStrength <= 0f) continue;
                float dtmShield = Dtm(core, m.DamageType, st.DefenseType);
                if (dtmShield <= 0f) continue;
                float absorbed = m.Power * dtmShield / st.MaxStrength;
                if (absorbed < 0.2f || absorbed > 0.45f) continue;
                foreach (var hull in core.HullTypes.Values.OrderBy(h => h.HitPoints))
                {
                    float dtmHull = Dtm(core, m.DamageType, hull.DefenseType);
                    if (dtmHull > 0f && m.Power * dtmHull < hull.HitPoints)
                        return (m, sp, st, absorbed, hull, dtmHull);
                }
            }
        }
        throw new InvalidOperationException("no mine-shield-hull combo in representative core");
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
        FedsrvVer = LogonRequest.MsgVer, Time = 942u, DwCookie = 0x942u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 88000000000000000ul, DrmHash = "minecombat",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>
    /// A booted 3-bot / 2-side live server plus the resolved roles — the exact
    /// <see cref="MissileCombatTests"/> harness. The <see cref="Srv.ShipSpawner"/>
    /// seam places slot 0 the <b>shooter</b> at the origin (identity nose → −z),
    /// slot 1 the <b>cross-side enemy</b> at <c>(0,0,−gap)</c>, slot 2 the
    /// <b>same-side observer</b> far off. Sides round-robin A,B,A so the enemy
    /// is cross-side to both peers. The mine tests reposition the enemy /
    /// observer near the deployed mine at will — the mine never moves, so only
    /// relative distance to its blast radius matters.
    /// </summary>
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
        foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable mission; bypass ready-up
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p95_shooter", "p95_enemy", "p95_observer" };
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

    /// <summary>The distance from the mine's blast centre a triggering ship is
    /// parked at for these tests. Small enough to be comfortably inside
    /// <c>MineRadius + 0.5·shipLength</c> for any selected combo, but non-zero
    /// (avoids the exact-overlap degenerate; the point-in-sphere test passes on
    /// strict inequality of distance vs threshold, so either way it triggers).</summary>
    private const float ParkDistance = 5.0f;

    // ── live: cross-side detonation (computed damage + arm-delay + destroy) ──

    /// <summary>⭐ A real cross-side ship parked inside an armed mine's blast
    /// radius takes the exact computed hull damage (<c>Power·dtmArmor/MaxHP</c>),
    /// the mine disappears from the sector, and the same-side peer who saw it
    /// deploy receives a real <c>FM_S_MINE_DESTROYED(84)</c>. Before the 1 s arm
    /// delay elapses, the ship sitting in the radius triggers <b>nothing</b>
    /// (arm-delay proof). <see cref="Mine.SideId"/> is carried on the sim object
    /// (scope 1) — asserted equal to the deploying ship's side.</summary>
    [Fact]
    public void Cross_side_mine_detonates_on_the_first_ship_in_radius_with_computed_damage_after_the_arm_delay()
    {
        var (mineType, hull, expectedDelta) = CrossHit.Value;
        float blastOffset = MathF.Min(ParkDistance, 0.999f * (mineType.MineRadius + 0.5f * hull.Length));
        using var seat = SeatThree(100f, hull);
        var shooter = seat.Shooter;
        var enemy = seat.Enemy;

        // The observer must already hold the shooter's EXPORT, else the server
        // withholds the same-side destroy (P94's _teammateExportSent gate).
        PumpUntil(seat.Server, () => seat.ObserverBot.Exports.Any(e => e.ShipId == shooter.Id), 8000);
        Assert.Contains(seat.ObserverBot.Exports, e => e.ShipId == shooter.Id);

        var mission = shooter.Mission!;
        double now0 = mission.LastUpdate;
        var mine = shooter.DeployMine(mineType, now0);
        short mId = mine.Id;

        // (scope 1) the sim mine carries the deploying ship's side directly.
        Assert.Equal(shooter.SideId, mine.SideId);
        Assert.Contains(mine, mine.OwnerSector.Models);

        // Park the cross-side enemy INSIDE the blast radius immediately — while
        // the mine is still unarmed. A real approach the reader can follow: the
        // mine sits exactly at the shooter's position (no offset), so putting
        // the enemy a few units ahead keeps it well within MineRadius + shipLen.
        enemy.Position = mine.Position + new Vector3(0f, 0f, -blastOffset);
        float f0 = enemy.Fraction;
        Assert.Equal(1f, f0, 3);

        // ── ARM-DELAY PROOF (req 4): pump through the unarmed window and assert
        // nothing fires — then confirm it does fire once armed. ──
        // Stop comfortably before DeployTime + ArmTime(1.0s): ~0.6s. If the
        // mine were not gated, it would detonate on the very first tick.
        PumpUntil(seat.Server, () => mission.LastUpdate >= now0 + 0.6, 400);
        Assert.True(mission.LastUpdate < now0 + Mine.ArmTimeSeconds,
            $"arm window must still be open before arming (now={mission.LastUpdate:F3})");
        Assert.Equal(f0, enemy.Fraction, 3);                               // NO damage while unarmed
        Assert.Contains(mine, mine.OwnerSector.Models);                     // mine NOT consumed while unarmed
        Assert.Empty(seat.ObserverBot.MineDestroyeds);                      // no destroy broadcast yet either

        // Now pump past the 1 s arm delay; the armed mine must detonate on the
        // cross-side ship.
        PumpUntil(seat.Server, () => enemy.Fraction < f0 - 0.002f, 800);
        Assert.True(enemy.Fraction < f0, "armed mine must detonate on the cross-side ship");

        // (1) exact computed hull damage: fraction = 1 − expectedDelta.
        Assert.Equal(1f - expectedDelta, enemy.Fraction, 3);

        // (3a) the mine disappeared from the sector on detonation.
        Assert.DoesNotContain(mine, mine.OwnerSector.Models);

        // (3b) the same-side peer who saw it deploy receives a real
        // MINE_DESTROYED(84) for it — the P91-consistency-fix-on-a-hit, applied
        // to a mine detonation.
        PumpUntil(seat.Server, () => seat.ObserverBot.MineDestroyeds.Any(d => d.MineId == mId), 4000);
        var d = seat.ObserverBot.MineDestroyeds.First(x => x.MineId == mId);
        Assert.Equal(shooter.Sector?.Id, d.ClusterId);
        Console.WriteLine($"P95-CROSS: enemy hull {f0:F2}→{enemy.Fraction:F3} (expect {1f - expectedDelta:F3}); " +
                          $"mine {mId} consumed on detonation, destroy broadcast to same-side peer at {d.ClusterId}");
    }

    // ── live: cross-side mine into a mounted shield ───────────────────────────

    /// <summary>⭐ A real cross-side mine into a target with a real mounted
    /// shield absorbs / leaks through with the <b>identical</b> math as a weapon
    /// or missile hit (the reused <see cref="Ship.ReceiveDamage"/> shield path):
    /// a fresh full shield absorbs the computed <c>Power·dtmShield/MaxStrength</c>
    /// and the hull stays untouched. Both numbers asserted from decoded data —
    /// the reuse is real, proven with a computed shield-fraction delta.</summary>
    [Fact]
    public void Cross_side_mine_against_a_mounted_shield_absorbs_like_a_weapon_hit()
    {
        var (mineType, shieldPart, _, expectedAbsorbed, hull, _) = ShieldCombo.Value;
        using var seat = SeatThree(100f, hull);
        var shooter = seat.Shooter;
        var enemy = seat.Enemy;

        enemy.MountPart(shieldPart, amount: 1, mountId: 0, fractionMounted: 255);   // CombatShieldTests' shield recipe, armed full
        Assert.Equal(1.0f, enemy.ShieldFraction, 3);
        float hullStart = enemy.Fraction;

        var mission = shooter.Mission!;
        double now0 = mission.LastUpdate;
        var mine = shooter.DeployMine(mineType, now0);
        enemy.Position = mine.Position + new Vector3(0f, 0f, -ParkDistance);

        // (2) reach the armed window, detonate, shield absorbs the exact
        // computed fraction; hull untouched; mine consumed.
        PumpUntil(seat.Server, () => enemy.ShieldFraction < 0.997f, 2000);
        Assert.Equal(1.0f - expectedAbsorbed, enemy.ShieldFraction, 3);
        Assert.Equal(hullStart, enemy.Fraction, 3);
        Assert.DoesNotContain(mine, mine.OwnerSector.Models);
        Console.WriteLine($"P95-SHIELD: mine absorbed {expectedAbsorbed:F3} (shield 1.0→{enemy.ShieldFraction:F3}, hull unchanged)");
    }

    // ── live: same-side friendly approach never triggers ────────────────────

    /// <summary>⭐ A same-side ship parked inside an armed mine's blast radius
    /// triggers <b>nothing</b> — asserted with a real same-side approach (the
    /// friendly-fire gate, <c>mineigc.h:77</c>, mirroring
    /// <see cref="ResolveCombatCollisions"/>'s own gate). The same-side observer
    /// drives up beside the armed mine, hull stays full, the mine stays in the
    /// sector; only once a <em>cross-side</em> ship arrives does it detonate
    /// (and then the observer still receives the destroy, being same-side).</summary>
    [Fact]
    public void Same_side_ship_inside_the_blast_radius_never_triggers_a_mine_but_a_cross_side_ship_does()
    {
        var (mineType, hull, expectedDelta) = CrossHit.Value;
        using var seat = SeatThree(100f, hull);
        var shooter = seat.Shooter;
        var enemy = seat.Enemy;
        var observer = seat.Observer;      // same side as the shooter — the friendly intruder

        // The observer must hold the shooter's EXPORT so the final destroy
        // (after the cross-side trigger) is delivered to it.
        PumpUntil(seat.Server, () => seat.ObserverBot.Exports.Any(e => e.ShipId == shooter.Id), 8000);
        Assert.Contains(seat.ObserverBot.Exports, e => e.ShipId == shooter.Id);

        var mission = shooter.Mission!;
        double now0 = mission.LastUpdate;
        var mine = shooter.DeployMine(mineType, now0);
        short mId = mine.Id;

        // Park the SAME-SIDE observer inside the blast radius and let the mine
        // arm fully. The cross-side enemy must be moved well clear of the blast
        // radius first (its seat is otherwise close enough that a large
        // MineRadius would trigger the mine as soon as it arms).
        enemy.Position = new Vector3(0f, 5000f, 5000f);
        observer.Position = mine.Position + new Vector3(0f, 0f, -ParkDistance);
        float obsStart = observer.Fraction;
        Assert.Equal(1f, obsStart, 3);
        PumpUntil(seat.Server, () => mission.LastUpdate >= now0 + Mine.ArmTimeSeconds + 0.3, 600);

        // (5) friendly approach: NO trigger, NO damage, mine still present.
        Assert.Equal(obsStart, observer.Fraction, 3);
        Assert.Contains(mine, mine.OwnerSector.Models);
        Assert.Empty(seat.ObserverBot.MineDestroyeds);

        // Now the cross-side enemy arrives inside the radius — it, and only it,
        // triggers the detonation. The mine is consumed and its destroy is
        // broadcast to the same-side observer (who saw it deploy).
        enemy.Position = mine.Position + new Vector3(0f, 0f, ParkDistance);
        float enemyStart = enemy.Fraction;
        PumpUntil(seat.Server, () => enemy.Fraction < enemyStart - 0.002f, 800);
        Assert.True(enemy.Fraction < enemyStart, "cross-side ship must trigger the armed mine");
        Assert.Equal(1f - expectedDelta, enemy.Fraction, 3);
        Assert.Equal(obsStart, observer.Fraction, 3);                       // friendly unharmed
        Assert.DoesNotContain(mine, mine.OwnerSector.Models);               // consumed
        PumpUntil(seat.Server, () => seat.ObserverBot.MineDestroyeds.Any(d => d.MineId == mId), 4000);
        Assert.Contains(seat.ObserverBot.MineDestroyeds, d => d.MineId == mId);
        Console.WriteLine($"P95-FRIENDLY: same-side hull stayed {obsStart:F2} inside the radius; cross-side enemy took " +
                          $"{1f - expectedDelta:F3}; mine consumed, destroy to same-side observer");
    }

    // ── domain: expiry-with-no-trigger path unchanged ───────────────────────

    /// <summary>A mine with nothing entering its radius still expires normally
    /// via <see cref="Mine.TimeExpire"/> and raises
    /// <see cref="IIgcSite.MineDestroyedEvent"/> exactly as P94 already proved —
    /// the unmodified expiry path, confirmed not to have been disturbed by the
    /// new detonation pass (a mine that never sees a qualifying ship falls
    /// through to <see cref="Mine.OnUpdate"/>'s own expiry check).</summary>
    [Fact]
    public void Mine_with_no_intruder_expires_via_lifespan_and_broadcasts_destroy_unmodified()
    {
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

        // No one approaches: tick well past Lifespan (from DeployTime) and the
        // mine must remove itself + raise MineDestroyedEvent exactly once.
        double t = mission.LastUpdate;
        for (int i = 0; i < 4000 && sector.Models.Contains(mine); i++)
        {
            t += 0.025;
            mission.Update(t);
        }
        Assert.DoesNotContain(mine, sector.Models);                        // consumed by expiry, not detonation
        Assert.Equal(1, site.MineDestroys);                                // exactly once (mineigc path unchanged)
        Console.WriteLine($"P95-EXPIRY: mine {mine.Id} expired at {mine.TimeExpire:F2} (life {mType.Lifespan}), " +
                          $"one MineDestroyedEvent, no detonation");
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

    /// <summary>A site that counts mine-destroy broadcasts so the domain expiry
    /// test asserts <see cref="IIgcSite.MineDestroyedEvent"/> is raised exactly
    /// once. Everything else falls through to the interface default (no-op).</summary>
    private sealed class CountingSite : IIgcSite
    {
        public int MineDestroys { get; private set; }
        public void MineDestroyedEvent(Mine m) => MineDestroys++;
    }
}
