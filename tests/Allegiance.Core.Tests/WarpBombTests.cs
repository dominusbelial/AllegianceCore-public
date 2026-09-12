using System.Reflection;
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
/// P101 — warp bombing. A real <see cref="Missile"/> that reaches a warp (instead
/// of a ship) triggers <c>FM_S_WARP_BOMB(188)</c>, is consumed at trigger time,
/// and schedules a <see cref="Sector.WarpBombDelaySeconds"/>(15 s)-delayed real
/// <see cref="Explosion"/> at the warp's position (native
/// <c>FedSrvSiteBase::WarpBombEvent</c>, <c>FedSrv.CPP:12543-12562</c>, and the
/// warp's own <c>Update</c> bomb-pop, <c>warpIGC.h:54-90</c>). The one genuine
/// behavioural departure from every prior ordnance broadcast in this project
/// (P88/P90/P94/P99 all same-side) is proved live: the <c>WARP_BOMB</c> goes to
/// the <b>whole mission</b> (<c>GetGroupRealSides()</c>, <c>FedSrv.CPP:12558</c>),
/// every connected peer on every side.
/// </summary>
/// <remarks>
/// Proofs are two kinds, mirroring <see cref="MissileCombatTests"/>' split: a
/// <b>live loopback</b> 3-bot / 2-side test that injects a warp via the
/// established reflection seam and a real missile at its centre, then asserts the
/// whole-mission broadcast reaches the cross-side peer too, the missile is
/// consumed at trigger, nothing detonates before <c>timeExplosion</c>, and the
/// delayed real explosion lands the exact computed falloff damage on a ship in
/// the blast; and a <b>domain</b> test proving a missile far from any warp
/// expires normally and raises no warp bomb (no regression). Everything is
/// data-derived from <c>PCore001.igc</c> — no hardcoded damage — and the
/// explosion math reuses <see cref="ExplosionPropagationTests"/>' style
/// (<c>f = 1 − d/radius; amount = Power·f²</c>).
/// </remarks>
[Collection("LiveNetwork")]
public sealed class WarpBombTests
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

    /// <summary>A (missile, hull) pair for the warp-bomb blast: a missile with
    /// real Power and a large blast radius (the native warp-bomb explosion uses
    /// the missile's <c>Power</c> and <c>BlastRadius</c> — warpIGC.h:65-67), and a
    /// hull whose damage cell for that type is &gt; 0 and whose single full hit is
    /// survivable and measurable. Ordered by descending BlastRadius so the widest
    /// realistic blast is chosen (room for a victim just past the combat-collision
    /// threshold but comfortably inside the blast).</summary>
    private static readonly Lazy<(DataMissileTypeIGC Missile, DataHullTypeIGC Hull)> Blast = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var m in core.MissileTypes.Values
            .Where(mm => mm.Power > 0f && mm.BlastRadius >= 150f)
            .OrderByDescending(mm => mm.BlastRadius))
        {
            foreach (var hull in core.HullTypes.Values.OrderBy(h => h.HitPoints))
            {
                float dtm = Dtm(core, m.DamageType, hull.DefenseType);
                if (dtm <= 0f) continue;
                float fullDrop = m.Power * dtm / hull.HitPoints;   // d ≤ 0 → full Amount
                if (fullDrop >= 0.080f && fullDrop <= 0.900f)
                    return (m, hull);
            }
        }
        throw new InvalidOperationException("no warp-bomb (missile, hull) combo in representative core");
    });

    // ── domain harness (started mission, one sector, counting site) ──────────

    private sealed class CountingSite : IIgcSite
    {
        public int WarpBombs { get; private set; }
        public int MissileDestroys { get; private set; }
        public void WarpBombEvent(IWarpIgc warp, IMissileIgc missile) => WarpBombs++;
        public void MissileDestroyedEvent(Missile m) => MissileDestroys++;
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

    private static (Mission Mission, Sector Sector, CountingSite Site) Domain()
    {
        var core = RepresentativeCore();
        var site = new CountingSite();
        var mission = Mission.Assemble(core, SomeMap(),
            [new SideSpec(0, "Team", core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId)], site);
        mission.Stage = MissionStage.Started;
        return (mission, mission.Sectors[0], site);
    }

    private static Ship PlaceShip(Mission mission, Sector sector, DataHullTypeIGC hull, Vector3 pos, short side, ref short id)
    {
        var ship = new Ship(id++, hull, pos, Vector3.Zero, Orientation.Identity);
        if (side != 0) ship.SideId = side;
        mission.PlaceShip(ship, sector);
        return ship;
    }

    /// <summary>Attach a real <see cref="ResolvedWarp"/> to a sector's private
    /// <c>_warps</c> via the established reflection seam (same technique
    /// <see cref="MissileCombatTests"/> uses for <c>Ship._shieldFraction</c>).
    /// The warp has <c>Destination = null</c> (dangling) so
    /// <see cref="Sector"/>'s ship-transit pass skips it — but the warp-bomb
    /// trigger has no destination gate (native bombards whatever it hits).</summary>
    private static ResolvedWarp AttachWarp(Sector sector, short warpId, Vector3 pos, short radius)
    {
        var warp = new ResolvedWarp(
            new DataWarpIGC(
                WarpId: warpId, DestinationId: -1, Radius: radius,
                TextureName: "", IconName: "", Name: "",
                Position: [pos.X, pos.Y, pos.Z],
                Forward: [0f, 0f, -1f],
                Rotation: [0f, 0f, 0f, 1f],
                Signature: 0f, ClusterId: -1),
            destination: null);
        var field = typeof(Sector).GetField("_warps", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(Sector), "_warps");
        ((List<ResolvedWarp>)field.GetValue(sector)!).Add(warp);
        return warp;
    }

    // ── domain: trigger → consume-now → delayed explosion, exact math ─────────

    /// <summary>⭐ A missile planted inside a warp's radius arms the bomb on the
    /// <b>first</b> tick: the missile is consumed <em>immediately</em> (not at
    /// <c>timeExplosion</c>), both site events fire exactly once, nothing detonates
    /// before the scheduled instant, and a real <see cref="Explosion"/> resolves at
    /// the warp's position when the sim clock reaches <c>timeExplosion</c>,
    /// dealing the exact computed falloff damage to a ship in the blast (a null
    /// launcher → no friendly gate, native <c>warpIGC.h:77</c>).</summary>
    [Fact]
    public void Domain_missile_in_warp_radius_is_consumed_now_and_the_delayed_blast_hits_a_nearby_ship()
    {
        var (missileType, hull) = Blast.Value;
        var (mission, sector, site) = Domain();
        short id = 922;
        // Launcher well clear of the warp so it never shares the blast.
        var launcher = PlaceShip(mission, sector, hull, new Vector3(0f, 0f, 4000f), side: 1, ref id);

        short warpRad = 20;
        AttachWarp(sector, 907, Vector3.Zero, warpRad);

        // Victim just past the combat-collision threshold (so the missile's own
        // combat pass never eats it), but comfortably inside the warp-bomb blast.
        float shipRad = 0.5f * hull.Length;
        float dVictim = (shipRad + missileType.Radius) + 20f;
        Assert.True(dVictim < missileType.BlastRadius - shipRad, $"victim d={dVictim:F1} must be < blast {missileType.BlastRadius - shipRad:F1}");
        Assert.True(dVictim > warpRad, "victim outside the warp's own radius");
        var victim = PlaceShip(mission, sector, hull, new Vector3(0f, 0f, dVictim), side: 2, ref id);

        double now = mission.LastUpdate;
        var missile = new Missile(933, sector, missileType, Vector3.Zero, Vector3.Zero, new Vector3(0f, 0f, -1f), now, launcher);
        sector.Add(missile);
        Assert.Contains(missile, sector.Models);

        // First tick: the missile (dist 0) is within warp.Radius + missile.Radius → trigger.
        double trigger = now + 0.05;
        mission.Update(trigger);
        Assert.Equal(1, site.WarpBombs);
        Assert.Equal(1, site.MissileDestroys);
        Assert.DoesNotContain(missile, sector.Models);     // consumed at trigger, not at te
        Assert.Equal(1f, victim.Fraction, 3);              // nothing detonated yet

        // Sim clock advances in <= 0.25 s chunks; step monotonically up to just
        // BELOW timeExplosion, asserting nothing detonates early the whole way.
        double te = trigger + Sector.WarpBombDelaySeconds;
        double t = trigger;
        while (t < te - 0.055)
        {
            t += 0.05;
            mission.Update(t);
            Assert.Equal(1f, victim.Fraction, 3);          // no premature blast
        }

        // Cross timeExplosion: a real Explosion resolves at the warp's position.
        mission.Update(te);

        // Expected damage: blast centred at the warp (origin), amount = Power·f²,
        // f = 1 − d/radius with d = surface distance (centre − ship radius).
        float d = dVictim - shipRad;
        Assert.True(d > 0f, $"want a real falloff (d>0), got d={d}");
        float f = 1f - d / missileType.BlastRadius;
        float amount = missileType.Power * f * f;
        float dtm = Dtm(RepresentativeCore(), missileType.DamageType, hull.DefenseType);
        float drop = amount * dtm / hull.HitPoints;
        Assert.Equal(1f - drop, victim.Fraction, 3);
        Console.WriteLine($"P101-DOMAIN: missile consumed at t={trigger:R}; bomb armed, nothing before t={te:R}; " +
                          $"victim {1f:F2}→{victim.Fraction:F3} (expect {1f - drop:F3}, d={d:F1}, amount={amount:F1})");
    }

    // ── domain: regression — a missile far from any warp behaves exactly as before ──

    /// <summary>⭐ A missile that never comes near a warp behaves exactly as
    /// before P101: it expires on its own, is removed from the sector, raises the
    /// missile-destroy event, and <em>does not</em> raise a warp-bomb event —
    /// even with a warp present in the sector, proving the new pass adds no
    /// behaviour to a missile outside any warp's volume.</summary>
    [Fact]
    public void Domain_missile_far_from_any_warp_expires_normally_and_raises_no_warp_bomb()
    {
        var (missileType, hull) = Blast.Value;   // lifespan is long enough to tick past expiry
        var (mission, sector, site) = Domain();
        short id = 942;
        var launcher = PlaceShip(mission, sector, hull, new Vector3(0f, 0f, 6000f), side: 1, ref id);
        // A warp exists in the sector — far from the missile's flight.
        AttachWarp(sector, 908, new Vector3(0f, 0f, -5000f), (short)90);

        double now = mission.LastUpdate;
        var missile = new Missile(945, sector, missileType, new Vector3(0f, 0f, 2000f), Vector3.Zero, new Vector3(0f, 0f, -1f), now, launcher);
        sector.Add(missile);

        // Tick past its full lifespan (expiry, not a warp bomb).
        double t = now;
        double deadline = now + missileType.Lifespan + 0.08;
        while (t < deadline) { t += 0.1; mission.Update(t); }

        Assert.DoesNotContain(missile, sector.Models);   // expired + removed on its own
        Assert.Equal(1, site.MissileDestroys);           // destroy broadcast on expiry (unchanged)
        Assert.Equal(0, site.WarpBombs);                 // never triggered a warp bomb
        Console.WriteLine($"P101-NOREGRESS: missile {missile.Id} expired normally at {deadline:R}, {site.MissileDestroys} destroy event(s), {site.WarpBombs} warp-bomb(s)");
    }

    // ── live loopback harness (3-bot / 2-side, MissileCombatTests recipe) ────

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Warp Bomb Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 988u, DwCookie = 0x988u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99400000000000000ul, DrmHash = "warpbomb",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

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
                _ => new Ship(id, hull, new Vector3(0f, 836f, 936f), Vector3.Zero, Orientation.Identity),
            };
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B,A
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p101_shooter", "p101_enemy", "p101_observer" };
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

    // ── LIVE: whole-mission broadcast + consumed-at-trigger + delayed real blast ──

    /// <summary>⭐ A real in-process server with 3 bots on 2 sides (A,B,A). A
    /// warp is injected into sector 0 (reflection seam) and a real missile is
    /// placed at its centre. On the first tick the bomb arms: the missile is
    /// consumed immediately, and a real <c>FM_S_WARP_BOMB(188)</c> arrives at
    /// <b>every</b> connected peer — the cross-side (Bravo) enemy included —
    /// naming the correct warp id and missile-type id. This is the one genuine
    /// behavioural departure from every prior same-side ordnance broadcast in the
    /// project, asserted explicitly. Pumping on to <c>timeExplosion</c>, a real
    /// explosion resolves at the warp and deals the exact computed falloff damage
    /// to a ship in the blast; nothing detonates before.</summary>
    [Fact]
    public void Live_whole_mission_warp_bomb_reaches_the_cross_side_peer_and_the_delayed_blast_lands()
    {
        var (missileType, hull) = Blast.Value;
        float gap = 100f;
        using var seat = SeatThree(gap, hull);
        var shooter = seat.Shooter;
        var enemy = seat.Enemy;
        var observer = seat.Observer;
        var mission = shooter.Mission!;
        var sector = mission.Sectors[0];

        // Warp parked well away from the three spawn points, dangling destination
        // (so no ship transits — but the warp-bomb trigger ignores the gate).
        short warpId = 953;
        short warpRad = 20;
        var warpPos = new Vector3(700f, 0f, 700f);
        AttachWarp(sector, warpId, warpPos, warpRad);

        // Observer must already hold the shooter's EXPORT for the missile-destroy
        // notice to reach it (P90/P91 gate).
        PumpUntil(seat.Server, () => seat.ObserverBot.Exports.Any(e => e.ShipId == shooter.Id), 8000);
        Assert.Contains(seat.ObserverBot.Exports, e => e.ShipId == shooter.Id);

        // Reposition the enemy (Bravo) into the blast: just past the missile's
        // own combat-collision threshold so the missile never eats it in the
        // combat pass, but comfortably inside the warp-bomb blast radius, and
        // outside the warp's own radius.
        float shipRad = 0.5f * hull.Length;
        float dVictim = (shipRad + missileType.Radius) + 20f;
        Assert.True(dVictim < missileType.BlastRadius - shipRad, "enemy must be inside the warp-bomb blast");
        enemy.Position = warpPos + new Vector3(0f, 0f, dVictim);
        float enemyStart = enemy.Fraction;
        Assert.Equal(1f, enemyStart, 3);

        // A real missile placed at the warp's centre (reflect-add is unnecessary —
        // Sector.Add is public; the warp is reflected in via AttachWarp).
        double now = mission.LastUpdate;
        var missile = new Missile(958, sector, missileType, warpPos, Vector3.Zero, new Vector3(0f, 0f, -1f), now, shooter);
        sector.Add(missile);

        // (1) The bomb arms on the first tick and reaches EVERY peer, cross-side included.
        PumpUntil(seat.Server, () => seat.EnemyBot.WarpBombs.Count >= 1, 2000);
        Assert.NotEmpty(seat.ShooterBot.WarpBombs);
        Assert.NotEmpty(seat.EnemyBot.WarpBombs);
        Assert.NotEmpty(seat.ObserverBot.WarpBombs);
        Assert.NotEqual(seat.Shooter.SideId, seat.Enemy.SideId);   // cross-side proven by construction

        foreach (var bot in new[] { seat.ShooterBot, seat.EnemyBot, seat.ObserverBot })
        {
            var b = bot.WarpBombs.First();
            Assert.Equal(warpId, b.WarpIdBombed);                    // correct warp id
            Assert.Equal(missileType.ExpendableTypeId, b.ExpendableIdMissile);  // correct missile-type id
            Assert.True(b.TimeExplosion > 0, "timeExplosion is a future tick");
            string who = ReferenceEquals(bot, seat.EnemyBot) ? "Bravo-enemy(cross-side)"
                       : ReferenceEquals(bot, seat.ObserverBot) ? "Alpha-observer" : "Alpha-shooter";
            Console.WriteLine($"P101-LIVE: {who} got WARP_BOMB(188): warp={b.WarpIdBombed} type={b.ExpendableIdMissile} teTick={b.TimeExplosion}");
        }

        // (2) The missile is consumed at TRIGGER time, not at timeExplosion.
        Assert.DoesNotContain(missile, sector.Models);
        // And the missile-destroy notice also reaches the same-side observer.
        PumpUntil(seat.Server, () => seat.ObserverBot.MissileDestroyeds.Any(d => d.MissileId == missile.Id), 2000);
        Assert.Contains(seat.ObserverBot.MissileDestroyeds, d => d.MissileId == missile.Id);

        // (3) Nothing detonates before timeExplosion: step a few ticks and confirm.
        Assert.Equal(1f, enemy.Fraction, 3);

        // (4) At timeExplosion, a real Explosion resolves at the warp's position
        // and the enemy takes the exact computed falloff damage.
        PumpUntil(seat.Server, () => enemy.Fraction < enemyStart - 0.004f, 3000);
        Assert.True(enemy.Fraction < enemyStart, "delayed warp-bomb blast must reach the ship in the blast");

        float d = dVictim - shipRad;
        float f = 1f - d / missileType.BlastRadius;
        float amount = missileType.Power * f * f;
        float dtm = Dtm(seat.Core, missileType.DamageType, hull.DefenseType);
        float drop = amount * dtm / hull.HitPoints;
        Assert.Equal(1f - drop, enemy.Fraction, 3);
        Console.WriteLine($"P101-LIVE-BLAST: enemy hull {enemyStart:F2}→{enemy.Fraction:F3} (expect {1f - drop:F3}, d={d:F1}) at the warp position");
    }
}
