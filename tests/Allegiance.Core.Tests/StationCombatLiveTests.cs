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
/// P125 — live loopback proof that a real station is a valid direct-hit target of
/// real ordnance in the existing swept collision pass
/// (<see cref="Sector.ResolveCombatCollisions"/>, the station added as a fourth
/// static target type alongside the asteroid), with the real wire/undock outcome:
/// <list type="number">
/// <item>a <b>cross-side</b> missile hit on a shield-up station reduces
/// <see cref="Station.ShieldFraction"/> by the exact decoded
/// <c>Power·dtmShield/maxShield</c> amount and leaves the armor untouched;</item>
/// <item>a <b>same-side (friendly)</b> missile hit deals no damage — the
/// simplified friendly-fire gate (no alliance concept; same-side-only);</item>
/// <item>enough damage to cross <see cref="Station.ArmorFraction"/> ≤ 0 really
/// destroys the station: it is removed from <see cref="Sector.Stations"/> AND
/// <see cref="Sector.StationRuntimes"/>, a real <c>FM_S_STATION_DESTROYED(59)</c>
/// naming that station id and the credited launcher ship id is broadcast to the
/// WHOLE MISSION (<c>GetGroupRealSides()</c>, <c>FedSrv.CPP:12279</c> — the
/// cross-side enemy and the same-side teammate both receive it), and any ship
/// docked at the dead base is ejected through the real undock path
/// (<c>Ship.StationId</c> cleared by <c>Server.PerformUndock</c>, the native
/// <c>VacateStation</c> port).</item>
/// <item>shield regeneration over real ticks — a damaged shield recharges toward
/// max at the documented rate (armor full, so only the shield regrows, the native
/// <c>stationIGC.h:195-208</c> sequencing).</item>
/// </list>
/// Construction mirrors <see cref="AsteroidCombatLiveTests"/>' 3-bot / 2-side
/// harness but the target is a <b>synthetic</b> station placed in sector 0 via
/// <c>Mission.PlaceStation</c> in the ShipSpawner (a real station type from the
/// core <c>with</c>-modified to chosen capacities/defense indices so the damage
/// math is exactly controllable, like the asteroid tests synthesize their rock).
/// Ground truth is read off the live server-side station (<see cref="Station.ShieldFraction"/>,
/// <see cref="Station.ArmorFraction"/>, sector membership) and the wire
/// <see cref="Bot.StationDestroyeds"/> collection on BOTH sides.
/// </summary>
[Collection("LiveNetwork")]
public sealed class StationCombatLiveTests
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

    /// <summary>A (missile, defense-index pair) where the missile's damage type has
    /// STRICTLY POSITIVE matrix cells for BOTH the armor and shield defense indices
    /// (so a real, non-degenerate shield-absorb and overflow both resolve), and the
    /// missile has real reach (InitialSpeed·Lifespan) to aim a live impact.</summary>
    private static readonly Lazy<(DataMissileTypeIGC Missile, byte DefArmor, byte DefShield, float Ds, float Da)> Combo = new(() =>
    {
        var core = Core();
        // Prefer the HIGHEST-power missile among those with strictly positive cells
        // for both defense indices and real reach — higher power gives comfortable
        // margins for the shield/overflow/kill tests (which size capacities to it).
        DataMissileTypeIGC? best = null;
        (byte defA, byte defS) = (0, 0);
        (float ds, float da) = (0f, 0f);
        foreach (var m in core.MissileTypes.Values
            .Where(mm => mm.InitialSpeed > 0f && mm.Power > 0f && mm.Lifespan >= 4f && mm.Lifespan <= 8f))
        {
            for (byte s = 0; s < DataConstantsIGC.DefidMax; s++)
                for (byte a = 0; a < DataConstantsIGC.DefidMax; a++)
                {
                    float sCell = Dtm(core, m.DamageType, s);
                    float aCell = Dtm(core, m.DamageType, a);
                    if (sCell > 0f && aCell > 0f && m.InitialSpeed * m.Lifespan >= 90f)
                    {
                        if (best is null || m.Power > best.Value.Power)
                        {
                            best = m; defA = a; defS = s; ds = sCell; da = aCell;
                        }
                        break;   // first valid (s,a) per missile is enough to qualify it
                    }
                }
        }
        if (best is null)
            throw new InvalidOperationException("no (missile, defense-pair) combo with positive cells + reach in representative core");
        return (best!.Value, defA, defS, ds, da);
    });

    /// <summary>The distance the firing ship parks from the station centre (ahead,
    /// along −z) for a real missile impact, sized to a comfortable fraction of the
    /// missile's reach and outside the station's collision radius.</summary>
    private static float FireGap(DataMissileTypeIGC m)
        => MathF.Min(0.55f * m.InitialSpeed * m.Lifespan, 170f);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Station Combat Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 778u, DwCookie = 0x778u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 77200000000000000ul, DrmHash = "stationcombat",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A booted 3-bot / 2-side live server with a single synthetic station
    /// (owned by side A) placed in sector 0 by the ShipSpawner. Slot 0 = <b>side A</b>
    /// at the origin (nose → −z); slot 1 = <b>side B</b> cross-side enemy; slot 2 =
    /// <b>side A</b> same-side observer. Tests reposition a shooter ahead of the
    /// station and fire real missiles at it.</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship A = null!, B = null!, Teammate = null!;
        public FltBot ABot = null!, BBot = null!, TeammateBot = null!;
        public Station Base = null!;
        public Sector Sector = null!;
        public StaticCore Core = null!;

        public void Dispose()
        {
            foreach (var b in new[] { ABot, BBot, TeammateBot }) b?.Dispose();
            Server?.Dispose();
        }
    }

    /// <summary>Seat 3 bots and place a synthetic station with the given armor /
    /// shield capacities and sabm bits. The station type is a real core type
    /// <c>with</c>-modified to the chosen defense indices (from <see cref="Combo"/>),
    /// capacities, regen rate and sabm, with a collision radius of 60 so a real
    /// missile intersects it.</summary>
    private static Seated SeatBase(float maxArmor, float maxShield, float shieldRegen, short sabm, float gap)
    {
        var (missile, defArmor, defShield, _, _) = Combo.Value;
        var seat = new Seated();
        var core = Core();
        seat.Core = core;
        seat.Server = new Srv();

        var stationSpot = new Vector3(0f, 0f, -1000f);   // far from the origin spawn, down the −z beam
        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            if (seat.Base is null)
            {
                var baseType = core.StationTypes.Values.First();
                var type = baseType with
                {
                    MaxArmorHitPoints = maxArmor,
                    MaxShieldHitPoints = maxShield,
                    DefenseTypeArmor = defArmor,
                    DefenseTypeShield = defShield,
                    ShieldRegeneration = shieldRegen,
                    ArmorRegeneration = 0f,
                    SabmCapabilities = sabm,
                    Radius = 40,   // ≤ the smallest fireGap (0.55·reach ≥ 49.5) so the shooter is OUTSIDE the base — the co-location guard in the combat pass never skips a real attack
                };
                var data = new DataStationIGC(
                    Position: [stationSpot.X, stationSpot.Y, stationSpot.Z],
                    Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
                    ClusterId: sector.Id, SideId: 0, StationId: 7801,
                    StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: "SYN_BASE");
                seat.Base = new Station(data, type);
                mission.PlaceStation(seat.Base, sector);
            }
            var ship = placed switch
            {
                0 => new Ship(id, core.HullTypes.Values.OrderBy(h => h.HitPoints).First(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
                1 => new Ship(id, core.HullTypes.Values.OrderBy(h => h.HitPoints).First(), new Vector3(0f, 0f, -gap), Vector3.Zero, Orientation.Identity),
                _ => new Ship(id, core.HullTypes.Values.OrderBy(h => h.HitPoints).First(), new Vector3(0f, 620f, 720f), Vector3.Zero, Orientation.Identity),
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
        string[] names = { "p125_a", "p125_b", "p125_team" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            foreach (var bot in bots)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                if (ship.Position.Length() < 1f) { seat.A = ship; seat.ABot = bot; }
                else if (MathF.Abs(ship.Position.Z + gap) < 2f) { seat.B = ship; seat.BBot = bot; }
                else { seat.Teammate = ship; seat.TeammateBot = bot; }
            }
            Assert.NotNull(seat.A); Assert.NotNull(seat.B); Assert.NotNull(seat.Teammate);
            Assert.True(seat.A.SideId != seat.B.SideId, "enemy must be cross-side to the A ship");
            Assert.True(seat.A.SideId == seat.Teammate.SideId, "teammate must be same-side to the A ship");
            seat.A.Velocity = Vector3.Zero;
            seat.B.Velocity = Vector3.Zero;
            seat.Teammate.Velocity = Vector3.Zero;
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    /// <summary>Park the shooter ahead of the station (identity nose → −z) and fire
    /// a real missile at it.</summary>
    private static void AimAndFire(Seated seat, Ship shooter, DataMissileTypeIGC missile, float fireGap)
    {
        shooter.Position = seat.Base.Center + new Vector3(0f, 0f, fireGap);
        shooter.FireMissile(missile, shooter.Mission!.LastUpdate);
    }

    /// <summary>⭐ A real cross-side missile hit on a shield-up station reduces
    /// <see cref="Station.ShieldFraction"/> by the exact decoded
    /// <c>Power·dtmShield/maxShield</c> (≤ shield capacity, so the armor is
    /// untouched) — a real projectile/missile hit on a real station, asserted
    /// numerically. The station's MaxShield is chosen so the missile's fixed power
    /// absorbs cleanly inside the shield.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cross_side_missile_hits_station_shield_exact_fraction_armor_untouched()
    {
        Require.Artwork();
        var (missile, _, defShield, ds, _) = Combo.Value;
        // MaxShield chosen so absorbed = Power·ds/MaxShield ∈ (0.15, 0.6): survivable, exact.
        float maxShield = MathF.Max(300f, MathF.Round(missile.Power * ds / 0.4f));
        float absorbed = missile.Power * ds / maxShield;
        Assert.True(absorbed > 0.1f && absorbed < 0.8f, $"shield absorb must be survivable; got {absorbed:F3}");

        float fireGap = FireGap(missile);
        using var seat = SeatBase(maxArmor: 5000f, maxShield, shieldRegen: 0f, sabm: 0, gap: 100f);
        var enemy = seat.B;
        var bas = seat.Base;
        var sector = seat.Sector;

        Assert.Equal(1f, bas.ShieldFraction, 3);
        Assert.Equal(1f, bas.ArmorFraction, 3);
        Assert.Contains(bas, sector.StationRuntimes);

        AimAndFire(seat, enemy, missile, fireGap);
        PumpUntil(seat.Server, () => bas.ShieldFraction < 1f - 0.020f, 9000);
        Assert.True(bas.ShieldFraction < 1f, "cross-side missile must actually hit the station");
        Assert.Equal(1f - absorbed, bas.ShieldFraction, 3);       // exact
        Assert.Equal(1f, bas.ArmorFraction, 3);                   // hull untouched
        Assert.False(bas.Dead);
        Assert.Contains(bas, sector.StationRuntimes);
        Console.WriteLine($"P125-LIVE-SHIELD: cross-side missile {missile.Power:F0} @ dtmShield {ds:F3} / maxShield {maxShield:F0} " +
                          $"=> ShieldFraction 1.0→{1f - absorbed:F3}, armor still 1.0");
    }

    /// <summary>⭐ A SAME-SIDE (friendly) missile hit on a station deals NO damage —
    /// the friendly-fire gate simplified to same-side-blocked (the teammate shares
    /// the station's side A). Armor and shield both stay at 1.0 even for a round
    /// powerful enough to destroy the base from the cross-side. Verification point 5.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Same_side_friendly_missile_deals_no_damage()
    {
        Require.Artwork();
        var (missile, _, _, ds, _) = Combo.Value;
        float maxShield = MathF.Max(200f, missile.Power * ds / 1.5f);   // one hit would overflow (kill) if it landed
        float fireGap = FireGap(missile);
        using var seat = SeatBase(maxArmor: 1000f, maxShield, shieldRegen: 0f, sabm: 0, gap: 100f);
        var teammate = seat.Teammate;    // same side A as the station
        var bas = seat.Base;

        Assert.Equal(1f, bas.ShieldFraction, 3);
        AimAndFire(seat, teammate, missile, fireGap);
        // Pump a generous fixed number of ticks (≈45 sim s) so the round has ample
        // time to arrive; a friendly hit must leave the base untouched throughout.
        for (int i = 0; i < 900; i++) { seat.Server.TickSim(); Thread.Sleep(1); }
        Assert.Equal(1f, bas.ShieldFraction, 3);
        Assert.Equal(1f, bas.ArmorFraction, 3);
        Assert.False(bas.Dead);
        Console.WriteLine("P125-LIVE-FF: same-side missile → NoDamage (armor+shield untouched) despite a would-be lethal round");
    }

    /// <summary>⭐ A lethal cross-side hit really destroys the station: it is removed
    /// from <see cref="Sector.Stations"/> and <see cref="Sector.StationRuntimes"/>,
    /// a real <c>STATION_DESTROYED(59)</c> naming that station id + the credited
    /// launcher ship id reaches the cross-side enemy AND the same-side teammate
    /// (the WHOLE-mission audience, <c>GetGroupRealSides()</c>), and a ship that was
    /// docked at the base is ejected through the real undock path
    /// (<see cref="Ship.StationId"/> cleared). Verification point 3.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Lethal_hit_destroys_station_ejects_docked_ship_and_broadcasts_destroy_to_whole_mission()
    {
        Require.Artwork();
        var (missile, _, defShield, ds, da) = Combo.Value;
        // Size capacities from the actual missile power so a SINGLE hit both
        // overflows the shield (absorbed = Power·ds/maxShield = 2) and drives the
        // armor to zero (overflow dmg ≥ 1 by construction: maxArmor ≤ maxShield·da/ds/2).
        float maxShield = MathF.Max(50f, missile.Power * ds / 2f);
        float absorbed = missile.Power * ds / maxShield;             // ≈ 2
        float overflow = absorbed - 1f;                              // surplus past the (full) shield
        float maxArmor = MathF.Max(1f, MathF.Floor(maxShield * da / ds / 2f));
        float killDmg = (overflow * maxShield * da) / (maxArmor * ds);   // ≥ 1 → lethal
        Assert.True(absorbed > 1f, $"must overflow the shield to kill; absorbed={absorbed:F3}");
        Assert.True(killDmg >= 1f, $"one hit must be lethal; killDmg={killDmg:F3}");
        float fireGap = FireGap(missile);
        using var seat = SeatBase(maxArmor, maxShield, shieldRegen: 0f, sabm: 0, gap: 100f);
        var enemy = seat.B;
        var bas = seat.Base;
        var sector = seat.Sector;

        seat.BBot.ResetStationDestroyeds();
        seat.TeammateBot.ResetStationDestroyeds();

        // Dock the SAME-SIDE teammate at the base (set its StationId to the base id —
        // the real dock state the eject path reads). It stays in _peerShips, so the
        // whole-mission vacate will find and eject it.
        seat.Teammate.StationId = bas.StationId;
        Assert.Equal(bas.StationId, seat.Teammate.StationId);

        Assert.Equal(1f, bas.ArmorFraction, 3);
        Assert.Contains(bas, sector.StationRuntimes);

        AimAndFire(seat, enemy, missile, fireGap);
        PumpUntil(seat.Server, () => bas.Dead, 12000);
        Assert.True(bas.Dead, "lethal cross-side hit must destroy the station");

        // Removed from BOTH the static and runtime lists (the death contract).
        Assert.DoesNotContain(bas, sector.StationRuntimes);
        Assert.DoesNotContain(bas.Data, sector.Stations);

        // Docked ship ejected through the real undock path (StationId cleared).
        Assert.Null(seat.Teammate.StationId);

        // STATION_DESTROYED(59) reaches BOTH peers (whole mission): correct station id
        // and the credited launcher = the enemy ship's id.
        PumpUntil(seat.Server, () => seat.BBot.StationDestroyeds.Count > 0 && seat.TeammateBot.StationDestroyeds.Count > 0, 6000);
        Assert.True(seat.BBot.StationDestroyeds.Count > 0, "cross-side enemy must receive STATION_DESTROYED (whole mission)");
        Assert.True(seat.TeammateBot.StationDestroyeds.Count > 0, "same-side teammate must receive STATION_DESTROYED (whole mission)");
        foreach (var d in seat.BBot.StationDestroyeds.Concat(seat.TeammateBot.StationDestroyeds))
        {
            Assert.Equal(bas.StationId, d.StationId);
            Assert.Equal(enemy.Id, d.Launcher);
        }
        Console.WriteLine($"P125-LIVE-KILL: base {bas.StationId} destroyed (launcher ship {enemy.Id}), removed from sector, " +
                          $"docked teammate ejected (StationId→null), STATION_DESTROYED(59) -> both same-side AND cross-side peer [WHOLE MISSION]");
    }

    /// <summary>⭐ Shield regeneration over REAL ticks: after a cross-side hit knocks
    /// the shield down (armor stays full), pumping server ticks recharges
    /// <see cref="Station.ShieldFraction"/> toward max at the documented rate
    /// (<c>dt·ShieldRegeneration/maxShield</c>, native <c>stationIGC.h:201-208</c>).
    /// Verification point 4.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Shield_regenerates_over_real_ticks()
    {
        Require.Artwork();
        var (missile, _, defShield, ds, _) = Combo.Value;
        // MaxShield chosen so the single hit chips the shield to ~0.5 (armor untouched).
        float maxShield = MathF.Max(300f, MathF.Round(missile.Power * ds / 0.5f));
        float absorbed = missile.Power * ds / maxShield;
        float shieldRegen = 40f;
        float fireGap = FireGap(missile);
        using var seat = SeatBase(maxArmor: 5000f, maxShield, shieldRegen, sabm: 0, gap: 100f);
        var enemy = seat.B;
        var bas = seat.Base;

        AimAndFire(seat, enemy, missile, fireGap);
        PumpUntil(seat.Server, () => bas.ShieldFraction < 1f - 0.050f, 9000);
        float knocked = bas.ShieldFraction;
        Assert.True(knocked < 1f, "shield must be knocked down first");
        Assert.Equal(1f, bas.ArmorFraction, 3);   // armor full → only the shield regens

        // dt per tick = 1/UpdatesPerSecond = 0.05 s. Gain per tick = 0.05·shieldRegen/maxShield.
        float perTick = 0.05f * shieldRegen / maxShield;
        // Loop real ticks until the shield has measurably regrown (well above the knock).
        int ticks = 0;
        while (bas.ShieldFraction < knocked + 0.060f && ticks < 15000)
        {
            seat.Server.TickSim();
            ticks++;
            Thread.Sleep(1);
        }
        Assert.True(ticks > 0, "some ticks must have elapsed");
        float expected = Math.Min(1f, knocked + ticks * perTick);
        Assert.True(bas.ShieldFraction > knocked, $"shield must regenerate over real ticks: {knocked:F3} → {bas.ShieldFraction:F3}");
        // The measured value is consistent with the documented rate (± one tick's drift).
        Assert.True(MathF.Abs(bas.ShieldFraction - expected) <= 3f * perTick + 0.005f,
            $"shield regen rate mismatch: measured {bas.ShieldFraction:F3} vs expected ≈{expected:F3}");
        Assert.Equal(1f, bas.ArmorFraction, 3);   // armor still full (only shield regened)
        Console.WriteLine($"P125-LIVE-REGEN: shield {knocked:F3} → {bas.ShieldFraction:F3} over {ticks} ticks " +
                          $"(rate {perTick:F4}/tick), armor held at 1.0");
    }
}
