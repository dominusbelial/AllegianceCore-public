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
/// P100 — live loopback proof that a real probe is a valid direct-hit target of
/// real ordnance in the existing swept collision pass
/// (<see cref="Sector.ResolveCombatCollisions"/>), with the two deliberate
/// behaviors the native formula dictates:
/// <list type="number">
/// <item>a <b>cross-side</b> missile hit reduces <see cref="Probe.Fraction"/>
/// by the exact decoded <c>Power·DamageConstant/maxHP</c> amount, and the probe
/// survives (stays in the sector, not destroyed);</item>
/// <item>a <b>same-side (friendly)</b> missile hit damages the probe just the
/// same — a real, deliberately-asserted difference from every ship/mine combat
/// test in this project, proving the "no friendly-fire gate for probes"
/// finding (<c>probeigc.h:74-89</c>) is real and not just asserted in a
/// comment;</item>
/// <item>enough hits to cross <c>Fraction ≤ 0</c> really Killed the probe: it is
/// removed from the sector and the same-side peer who saw it deploy receives a
/// real <c>FM_S_PROBE_DESTROYED(85)</c> naming that same probe id — confirming
/// the "no <c>Server.cs</c> changes needed" claim (the wire layer was already
/// wired by P99's <c>ProbeDestroyedEvent</c> bridge).</item>
/// </list>
/// Construction mirrors <see cref="MineCombatTests"/>/<see cref="MissileCombatTests"/>'
/// 3-bot / 2-side harness (a guaranteed same-side pair plus a cross-side enemy);
/// the probe is deployed by calling <c>Ship.DeployProbe</c> on the server-side
/// <see cref="Ship"/> (the public seam), and ordnance is real missiles fired via
/// <c>Ship.FireMissile</c> at the stationary probe. Ground truth is read off the
/// live server-side probe (probe <c>Fraction</c>, sector membership) and the
/// wire <see cref="Bot.ProbeDestroyeds"/> collection (P99).
/// </summary>
[Collection("LiveNetwork")]
public sealed class ProbeCombatLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static float Dtm(StaticCore core, byte dmg, byte def) => core.Constants.DamageConstants[dmg][def];

    /// <summary>A (missile, probe type) pair where one cross-side hit lands a
    /// clean, survivable fraction on the probe (<c>Power·dtm/maxHP ∈ [0.065,
    /// 0.085]</c> — a small, precise, non-lethal delta we can assert exactly)
    /// with straight-line reach ≥ 90 m (so we can aim a real impact). Chosen
    /// gentle so several tests can measure a single surviving hit.</summary>
    private static readonly Lazy<(DataMissileTypeIGC Missile, DataProbeTypeIGC Probe, float Delta)> Nonlethal = new(() =>
    {
        var core = Core();
        foreach (var m in core.MissileTypes.Values
            .Where(mm => mm.InitialSpeed > 0f && mm.Power > 0f && mm.Lifespan >= 4f && mm.Lifespan <= 8f))
        {
            if (m.InitialSpeed * m.Lifespan < 90f) continue;
            foreach (var pt in core.ProbeTypes.Values
                .Where(p => p.HitPoints > 0f && p.Lifespan >= 8f)
                .OrderBy(p => p.HitPoints))
            {
                float dtm = Dtm(core, m.DamageType, pt.DefenseType);
                if (dtm <= 0f) continue;
                float delta = m.Power * dtm / pt.HitPoints;
                if (delta >= 0.063f && delta <= 0.087f)
                    return (m, pt, delta);
            }
        }
        throw new InvalidOperationException("no non-lethal (missile, probe) combo in representative core");
    });

    /// <summary>A (missile, probe type) pair where a SINGLE hit is lethal
    /// (<c>Power·dtm ≥ maxHP</c>) and the missile has straight-line reach ≥
    /// 90 m — so one cross-side shot crosses <c>Fraction ≤ 0</c> and must kill
    /// the probe outright.</summary>
    private static readonly Lazy<(DataMissileTypeIGC Missile, DataProbeTypeIGC Probe)> Lethal = new(() =>
    {
        var core = Core();
        foreach (var m in core.MissileTypes.Values
            .Where(mm => mm.InitialSpeed > 0f && mm.Power > 0f && mm.Lifespan >= 4f && mm.Lifespan <= 8f))
        {
            if (m.InitialSpeed * m.Lifespan < 90f) continue;
            foreach (var pt in core.ProbeTypes.Values
                .Where(p => p.HitPoints > 0f && p.Lifespan >= 8f)
                .OrderBy(p => p.HitPoints))
            {
                float dtm = Dtm(core, m.DamageType, pt.DefenseType);
                if (dtm <= 0f) continue;
                if (m.Power * dtm >= pt.HitPoints)
                    return (m, pt);
            }
        }
        throw new InvalidOperationException("no lethal (missile, probe) combo in representative core");
    });

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Probe Combat Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 921u, DwCookie = 0x921u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 87000000000000000ul, DrmHash = "probecombat",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A booted 3-bot / 2-side live server plus the resolved roles —
    /// the identical harness <see cref="MineCombatTests"/> uses. Slot 0 the
    /// <b>deployer</b> (side A) at the origin identity (nose → −z), slot 1 the
    /// <b>cross-side enemy</b> (side B), slot 2 the <b>same-side observer</b>
    /// (side A). The deployer lays the probe at its own position (the origin);
    /// the tests move the enemy/observer into the line of fire ahead of the
    /// probe and fire real missiles at it.</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship Deployer = null!, Enemy = null!, Observer = null!;
        public FltBot DeployerBot = null!, EnemyBot = null!, ObserverBot = null!;
        public StaticCore Core = null!;

        public void Dispose()
        {
            foreach (var b in new[] { DeployerBot, EnemyBot, ObserverBot }) b?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatThree(float gap, DataHullTypeIGC hull)
    {
        var seat = new Seated();
        var core = Core();
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
                _ => new Ship(id, hull, new Vector3(0f, 860f, 960f), Vector3.Zero, Orientation.Identity),
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
        string[] names = { "p100_deployer", "p100_enemy", "p100_observer" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            foreach (var bot in bots)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                if (ship.Position.Length() < 1f) { seat.Deployer = ship; seat.DeployerBot = bot; }
                else if (MathF.Abs(ship.Position.Z + gap) < 2f) { seat.Enemy = ship; seat.EnemyBot = bot; }
                else { seat.Observer = ship; seat.ObserverBot = bot; }
            }
            Assert.NotNull(seat.Deployer); Assert.NotNull(seat.Enemy); Assert.NotNull(seat.Observer);
            Assert.True(seat.Deployer.SideId != seat.Enemy.SideId, "enemy must be cross-side to the deployer");
            Assert.True(seat.Deployer.SideId == seat.Observer.SideId, "observer must be same-side to the deployer");
            seat.Deployer.Velocity = Vector3.Zero;
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

    /// <summary>The distance the firing ship parks behind the probe (ahead, in
    /// −z) for a real missile impact, sized to a comfortable fraction of the
    /// missile's reach.</summary>
    private static float FireGap(DataMissileTypeIGC m)
        => MathF.Min(0.3f * m.InitialSpeed * m.Lifespan, 150f);

    /// <summary>⭐ A real cross-side missile hit on a real probe reduces the
    /// probe's <see cref="Probe.Fraction"/> by the exact decoded
    /// <c>Power·DamageConstant/maxHP</c> amount — asserted numerically, not just
    /// "some damage" — and the probe survives (still in its sector, no destroy
    /// broadcast). Deployed by the origin ship, shot by the cross-side enemy.</summary>
    [Fact]
    public void Cross_side_missile_hits_probe_with_exact_computed_damage_and_it_survives()
    {
        var (missile, probeType, delta) = Nonlethal.Value;
        float fireGap = FireGap(missile);
        Assert.True(fireGap < missile.InitialSpeed * missile.Lifespan, "firing gap must be inside missile reach");
        using var seat = SeatThree(100f, Core().HullTypes.Values.OrderBy(h => h.HitPoints).First());
        var deployer = seat.Deployer;
        var enemy = seat.Enemy;
        var observer = seat.Observer;

        // Observer must hold the deployer's ExportShip (same-side peer) so the
        // eventual destroy (if any) would be delivered — and it sees the probe deploy.
        PumpUntil(seat.Server, () => seat.ObserverBot.Exports.Any(e => e.ShipId == deployer.Id), 9000);
        Assert.Contains(seat.ObserverBot.Exports, e => e.ShipId == deployer.Id);

        var mission = deployer.Mission!;
        var probe = deployer.DeployProbe(probeType, mission.LastUpdate);
        short pId = probe.Id;
        Assert.Contains(probe, probe.OwnerSector.Models);
        Assert.Equal(1f, probe.Fraction, 3);

        // The cross-side enemy repositions ahead of the probe (identity nose → −z)
        // and fires a real missile straight at it.
        enemy.Position = probe.Position + new Vector3(0f, 0f, fireGap);
        float f0 = probe.Fraction;
        enemy.FireMissile(missile, mission.LastUpdate);

        PumpUntil(seat.Server, () => probe.Fraction < f0 - 0.008f, 6000);
        Assert.True(probe.Fraction < f0, "cross-side missile must actually hit the probe");

        // (1) exact computed fraction: 1 − delta (decoded data).
        Assert.Equal(1f - delta, probe.Fraction, 3);
        // (4) survived: still in its sector, not destroyed.
        Assert.Contains(probe, probe.OwnerSector.Models);
        Assert.Empty(seat.ObserverBot.ProbeDestroyeds);
        Console.WriteLine($"P100-CROSS: probe {f0:F2}→{probe.Fraction:F3} (expect 1 − {delta:F3}); survived, still in sector");
    }

    /// <summary>⭐ A SAME-SIDE (friendly) missile hit damages the probe exactly
    /// the same as a cross-side one — a real, deliberately-asserted difference
    /// from every ship/mine combat test in this project, proving the "no
    /// friendly-fire gate for probes" finding (<c>probeigc.h:74-89</c>) is real,
    /// not just asserted in a comment. The observer (same side as the deploying
    /// ship, hence same side as the probe it sits beside) fires its own missile
    /// at the probe and the identical damage lands.</summary>
    [Fact]
    public void Same_side_friendly_missile_damages_the_probe_no_friendly_fire_gate()
    {
        var (missile, probeType, delta) = Nonlethal.Value;
        float fireGap = FireGap(missile);
        using var seat = SeatThree(100f, Core().HullTypes.Values.OrderBy(h => h.HitPoints).First());
        var deployer = seat.Deployer;
        var observer = seat.Observer;   // same side A as the deployer ⇒ same side as the probe
        // P467: the observer's side civ base c_gaDamageMissiles scales the hit
        // (native missileigc.cpp:474) — fold it into the expected damage.
        float ga = observer.GlobalAttributeFactor(GlobalAttributeSet.DamageMissiles);

        PumpUntil(seat.Server, () => seat.ObserverBot.Exports.Any(e => e.ShipId == deployer.Id), 9000);

        var mission = deployer.Mission!;
        var probe = deployer.DeployProbe(probeType, mission.LastUpdate);
        Assert.Equal(deployer.SideId, probe.SideId);   // probe inherits side A
        Assert.Contains(probe, probe.OwnerSector.Models);
        Assert.Equal(1f, probe.Fraction, 3);

        // The SAME-SIDE observer fires its own missile at the probe (side A ⇒
        // side A). For a ship this would be gated away and pass through; for a
        // probe there is NO gate — the damage must land identically.
        observer.Position = probe.Position + new Vector3(0f, 0f, fireGap);
        float f0 = probe.Fraction;
        observer.FireMissile(missile, mission.LastUpdate);

        PumpUntil(seat.Server, () => probe.Fraction < f0 - 0.010f, 6000);
        Assert.True(probe.Fraction < f0, "friendly fire must still damage the probe");
        Assert.Equal(1f - ga * delta, probe.Fraction, 3);   // identical computed damage
        Assert.Contains(probe, probe.OwnerSector.Models);
        Console.WriteLine($"P100-FRIENDLY: same-side missile still dealt {delta:F3} (probe {f0:F2}→{probe.Fraction:F3}); " +
                          $"NO friendly-fire gate for a probe (ship path would pass through)");
    }

    /// <summary>⭐ Enough damage to cross <c>Fraction ≤ 0</c> really Killed the
    /// probe: a single lethal cross-side missile removes it from the sector, and
    /// the same-side peer who saw it deploy receives a real
    /// <see cref="ProbeDestroyed"/>(85) naming that same probe id — confirming
    /// the "no <c>Server.cs</c> changes needed" claim (the wire layer was already
    /// bridged by P99's <c>ProbeDestroyedEvent</c>, so a probe killed by any
    /// cause reaches the broadcast unmodified).</summary>
    [Fact]
    public void Lethal_hit_kills_probe_removes_from_sector_and_broadcasts_destroy_to_same_side_peer()
    {
        var (missile, probeType) = Lethal.Value;
        float fireGap = FireGap(missile);
        using var seat = SeatThree(100f, Core().HullTypes.Values.OrderBy(h => h.HitPoints).First());
        var deployer = seat.Deployer;
        var enemy = seat.Enemy;
        var observer = seat.Observer;

        // The observer must see BOTH the deployer and the probe deploy, else the
        // same-side PROBE_DESTROYED is withheld (P99's _teammateExportSent gate).
        PumpUntil(seat.Server, () => seat.ObserverBot.Exports.Any(e => e.ShipId == deployer.Id), 9000);
        Assert.Contains(seat.ObserverBot.Exports, e => e.ShipId == deployer.Id);

        var mission = deployer.Mission!;
        var probe = deployer.DeployProbe(probeType, mission.LastUpdate);
        short pId = probe.Id;
        // Latch that the observer actually received the probe's EXPORT (saw it deploy).
        PumpUntil(seat.Server, () => seat.ObserverBot.ProbeExports.Any(e => e.Probe.ProbeId == pId), 9000);
        Assert.Contains(seat.ObserverBot.ProbeExports, e => e.Probe.ProbeId == pId);

        // One lethal cross-side missile kills it outright.
        enemy.Position = probe.Position + new Vector3(0f, 0f, fireGap);
        enemy.FireMissile(missile, mission.LastUpdate);
        PumpUntil(seat.Server, () => probe.Dead, 9000);
        Assert.True(probe.Dead, "lethal cross-side hit must kill the probe");

        // Removed from the sector (death contract), fraction clamped to 0.
        Assert.DoesNotContain(probe, probe.OwnerSector.Models);
        Assert.Equal(0f, probe.Fraction, 3);

        // The same-side peer who saw it deploy receives a real PROBE_DESTROYED(85)
        // naming that same probe id — the P99 wire bridge, reached unmodified.
        PumpUntil(seat.Server, () => seat.ObserverBot.ProbeDestroyeds.Any(d => d.ProbeId == pId), 6000);
        var d = seat.ObserverBot.ProbeDestroyeds.First(x => x.ProbeId == pId);
        Assert.Equal(deployer.Sector?.Id, d.ClusterId);
        Console.WriteLine($"P100-KILL: probe {pId} killed (fraction 1.0→0.0), removed from sector, " +
                          $"PROBE_DESTROYED(85) to same-side peer at {d.ClusterId}");
    }
}
