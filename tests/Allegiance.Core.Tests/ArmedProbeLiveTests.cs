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
/// P491 — the live wire proof that a real armed probe actually fires: a probe
/// with a real <c>ProjectileTypeId</c>, deployed from a seated ship through the
/// P435 <c>Ship.DeployProbe</c> dispenser seam, autonomously acquires a hostile
/// ship that flies into range, and:
/// <list type="number">
/// <item>spawns a real <c>Projectile</c> that the deployer's own peer receives
/// as a real <c>FM_CS_PROJECTILE_INSTANCE(12)</c> <see cref="ProjectileInstance"/>
/// (the same broadcast path ships use — launcher = the deployer ship, so the
/// same-side and P375 cross-side legs come free);</item>
/// <item>hits and damages the enemy — the enemy ship's
/// <see cref="Ship.Fraction"/> drops below 1;</item>
/// <item>decrements the probe's <see cref="Probe.Ammo"/> from its decoded pool
/// (5 for 'Plas Gen 1').</item>
/// </list>
/// The armed probe type is the real PCore001 'Plas Gen 1' (id 30):
/// <c>c_eabmQuickReady</c> (fires 5 s after deploy, not 30), <c>c_eabmShootShips</c>,
/// ammo 5, dispersion 0, accuracy 1 — deterministic. The probe is deployed at the
/// deployer's origin and the enemy parks ~150 m ahead, inside the round's ~208 m
/// reach; the probe leads and fires straight at it. Mirror of the P100
/// <c>ProbeCombatLiveTests</c> harness (in-process server + seated bots).
/// </summary>
[Collection("LiveNetwork")]
public sealed class ArmedProbeLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(System.IO.Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    /// <summary>The real armed probe type: 'Plas Gen 1' (id 30) — quick-ready,
    /// ShootShips, ammo 5, dispersion 0, accuracy 1.</summary>
    private static DataProbeTypeIGC PlasGen() => Core().ProbeTypes[30];

    private static DataHullTypeIGC FighterHull()
        => Core().HullTypes.Values
            .Where(h => h.HitPoints > 0f && !h.HasCapability(DataHullTypeIGC.HabmLifepod))
            .OrderBy(h => h.HitPoints).First();

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Armed Probe Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 921u, DwCookie = 0x921u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 87654321098765432ul, DrmHash = "armedprobe",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A booted 2-bot / 2-side live server plus the resolved roles —
    /// slot 0 the <b>deployer</b> (side A) at the origin, slot 1 the
    /// <b>cross-side enemy</b> (side B).</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship Deployer = null!, Enemy = null!;
        public FltBot DeployerBot = null!, EnemyBot = null!;
        public StaticCore Core = null!;

        public void Dispose()
        {
            foreach (var b in new[] { DeployerBot, EnemyBot }) b?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatTwo()
    {
        var seat = new Seated();
        var core = Core();
        seat.Core = core;
        seat.Server = new Srv();
        var hull = FighterHull();

        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            var ship = placed switch
            {
                0 => new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity),
                _ => new Ship(id, hull, new Vector3(0f, 0f, -200f), Vector3.Zero, Orientation.Identity),
            };
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;  // bypass ready-up (P180)
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot() };
        string[] names = { "p491_deployer", "p491_enemy" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "both bots must be assigned a ship");

            foreach (var bot in bots)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                if (ship.Position.Length() < 1f) { seat.Deployer = ship; seat.DeployerBot = bot; }
                else { seat.Enemy = ship; seat.EnemyBot = bot; }
            }
            Assert.NotNull(seat.Deployer); Assert.NotNull(seat.Enemy);
            Assert.True(seat.Deployer.SideId != seat.Enemy.SideId, "enemy must be cross-side to the deployer");
            seat.Deployer.Velocity = Vector3.Zero;
            seat.Enemy.Velocity = Vector3.Zero;
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    /// <summary>⭐ A real armed probe deployed from a seated ship (the P435
    /// <c>DeployProbe</c> seam) autonomously fires at an enemy flown into range:
    /// the deployer's peer receives a real <c>PROJECTILE_INSTANCE(12)</c> for the
    /// probe's round, the enemy ship takes real damage, and the probe's ammo
    /// decrements from its decoded pool of 5.</summary>
    [Fact]
    public void Armed_probe_spawns_hits_and_damages_enemy_with_ammo_decrement_live()
    {
        using var seat = SeatTwo();
        var deployer = seat.Deployer;
        var enemy = seat.Enemy;
        var mission = deployer.Mission!;
        var probeType = PlasGen();
        short pTypeId = probeType.ProjectileTypeId;

        // Deploy the real armed probe through the P435 dispenser seam (the same
        // terminal call HandleFireExpendable makes).
        var probe = deployer.DeployProbe(probeType, mission.LastUpdate);
        Assert.Equal((short)5, probe.Ammo);
        Assert.Contains(probe, probe.OwnerSector.Models);

        // Fly the enemy into range: ~150 m ahead of the probe, inside the round's
        // ~208 m reach (speed 1000 × life 0.218 / 1.05).
        enemy.Position = probe.Position + new Vector3(0f, 0f, 150f);
        enemy.Velocity = Vector3.Zero;

        // Clear the deployer's projectile accumulator so we only count the
        // probe's round on the wire.
        seat.DeployerBot.ResetProjectileInstances();

        // Pump until: the probe has spent ammo (fired at least one round) AND the
        // enemy has taken damage AND the deployer's peer saw the round broadcast.
        int pumps = PumpUntil(seat.Server,
            () => probe.Ammo < 5 && enemy.Fraction < 1f && seat.DeployerBot.ProjectileInstances.Any(),
            20000);

        Assert.True(probe.Ammo < 5, $"probe ammo must decrement from 5 (left {probe.Ammo})");
        Assert.True(enemy.Fraction < 1f, $"enemy must take damage from the armed probe (fraction {enemy.Fraction:F4})");
        var round = seat.DeployerBot.ProjectileInstances.FirstOrDefault(pi => pi.ProjectileTypeId == pTypeId);
        Assert.NotNull(round);
        // The broadcast round spawns at the probe, aimed at the enemy (+z), speed
        // 1000 (Plas Gen; ammo probe → identity-GA civ).
        Assert.True(MathF.Abs(round.Velocity.Z - 1000f) < 50f,
            $"broadcast round velocity {round.Velocity} should fly at the enemy (+z ~1000)");

        Console.WriteLine($"P491-LIVE: armed probe '{probeType.LauncherName}' ammo 5→{probe.Ammo}; enemy {1f}→{enemy.Fraction:F4}; " +
                          $"PROJ_INST(12) type {pTypeId} vel {round.Velocity} on deployer peer after {pumps} pumps");
    }
}
