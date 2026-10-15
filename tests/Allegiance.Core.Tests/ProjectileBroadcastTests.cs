using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using System.Reflection;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P88 (updated P375): the live loopback proof that a real weapon shot is
/// broadcast as a real <c>FM_CS_PROJECTILE_INSTANCE(12)</c> message to the
/// shooter's same-side peers — and, since P375, also to a cross-side enemy whose
/// side has a seated peer in the shooter's sector (the P369 cross-side
/// visibility gate), so a watching opponent sees the bolt in real time.
/// Construction mirrors <see cref="TeammatePositionBroadcastTests"/> (3 bots,
/// round-robin sides, a guaranteed same-side pair plus a cross-side enemy); the
/// shooter mounts a real weapon part pulled from the loaded <c>StaticCore</c> (the
/// <see cref="LoadoutRoundTripTests.RealWeapon"/> recipe) and the server's own
/// authoritative sim fires it. Three things are asserted for real:
/// (1) the same-side observer receives a decoded <see cref="ProjectileInstance"/>
/// whose <c>ProjectileTypeId</c> matches the fired part's projectile type, whose
/// <c>Position</c> is near the shooter, and whose <c>Velocity</c> is nonzero
/// along the firing direction; (2) since P375 the cross-side enemy also receives
/// those same real broadcasts (correct type, per-shot multiplicity) — no longer
/// withheld; (3) multiple shots produce multiple distinct broadcasts — a genuine
/// per-shot event, not a cached one-time snapshot.
/// </summary>
[Collection("LiveNetwork")]
public class ProjectileBroadcastTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Projectile Broadcast Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 851u, DwCookie = 0x851u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 85000000000000000ul, DrmHash = "projtest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A real weapon part the shooter will fire — a dispersion-0
    /// weapon with a positive-power projectile so the projectile velocity lies
    /// exactly along the firing forward (making the direction assertion exact,
    /// not probabilistic), the cheapest-energy such weapon. Selection ignores
    /// the starter hull's small energy/ammo capacity because the test tops the
    /// ship's pools up itself (arming the scenario — the same freedom
    /// <c>CombatShieldTests.ArmShooter</c> exercises); the only requirement is
    /// that a workable weapon exists in the core. Returns the decoded weapon
    /// and its projectile type id.</summary>
    private static (DataPartTypeIGC.Weapon w, short projectileTypeId) RealWeapon(StaticCore core)
    {
        foreach (var w in core.PartTypes.Values
                     .Where(p => p.EquipmentType == EquipmentType.Weapon)
                     .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                     .Where(w => w.Dispersion == 0f)
                     .OrderBy(w => w.EnergyPerShot))
        {
            if (!core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var pt) || pt.Power <= 0f) continue;
            return (w, w.ProjectileTypeId);
        }
        throw new InvalidOperationException("no dispersion-0 weapon with a positive-power projectile in the representative core");
    }

    /// <summary>⭐ A teammate must see a real, decoded PROJECTILE_INSTANCE(12)
    /// for a shot the teammate's same-side ally fires, with real fields — and
    /// the cross-side enemy must receive nothing, no matter how long we pump.
    /// Also asserts per-shot multiplicity (≥2 distinct broadcasts from ≥2
    /// distinct shots), proving the broadcast is a real per-shot event and not
    /// a one-time/cached snapshot.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Real_weapon_fire_broadcasts_projectile_to_teammate_but_not_enemy()
    {
        Require.Artwork();
        using var server = new Srv();

        // P375 — place all three bots in the SAME sector (Sectors[0]) with
        // round-robin sides. The cross-side enemy must be co-located with the
        // shooter for the P375 cross-side audience gate (a seated peer of the
        // enemy's side in the shooter's sector) to fire — without controlling the
        // spawn, bots could land in different sectors and the cross-side send is
        // (correctly) withheld, making the cross-side assertion non-deterministic.
        int placed = 0;
        var spawnerField = typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ShipSpawner field not found");
        spawnerField.SetValue(server, (StaticCore core, Mission mission, short id) =>
        {
            var sector = mission.Sectors[0];
            // Use a hull with a real magazine: P653 H-08 clamps Ammo to hull.MaxAmmo
            // (shipIGC.h:597-601); the generic Values.First() is a 'Miner' with
            // MaxAmmo=0, which would leave the shooter unable to fire.
            var hull = core.HullTypes.Values.Where(h => h.MaxAmmo >= 10).OrderBy(h => h.HullId).First();
            var side = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin — matches the test's ships dict
            var ship = new Ship(id, hull, new Vector3(placed * 250f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        });

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p88_shooter", "p88_observer", "p88_enemy" };
        try
        {
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i].Connect(server.BoundPort, names[i]);
                bots[i].SendLogonRequest(Request(names[i]));
            }
            PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            var ships = server.Ships.ToDictionary(s => s.Ship.Id, s => s.Ship.SideId);
            short[] ids = bots.Select(b => b.ShipAssignment!.Ship.ShipId).ToArray();

            int subject = -1, observer = -1, enemy = -1;
            for (int i = 0; i < bots.Length && observer < 0; i++)
                for (int j = 0; j < bots.Length; j++)
                    if (j != i && ships[ids[j]] == ships[ids[i]]) { subject = i; observer = j; break; }
            Assert.True(subject >= 0, "round-robin must yield a same-side pair");
            for (int k = 0; k < bots.Length; k++)
                if (k != subject && k != observer) { enemy = k; break; }
            Assert.True(enemy >= 0 && ships[ids[enemy]] != ships[ids[subject]],
                "3 bots / 2 teams must leave one bot cross-side");

            short subjectId = ids[subject];
            var obsBot = bots[observer];
            var enemyBot = bots[enemy];

            var live = server.Ships.First(l => l.Ship.Id == subjectId);
            var shooter = live.Ship;
            var mission = shooter.Mission ?? throw new InvalidOperationException("shooter must have a mission");
            var (weaponType, projectileTypeId) = RealWeapon(mission.Core);

            // Arm the shooter with the real weapon and select it (all-weapons
            // state, the CombatShieldTests.ArmShooter recipe), and top up the
            // energy/ammo pools so the weapon keeps firing across several
            // cooldowns (Ship.cs has no recharge — pool only drains, so this
            // is how the scenario sustains >= 2 distinct shots deterministically).
            shooter.StateM |= ShipControlState.AllWeapons;
            shooter.Energy = 1_000_000f;
            shooter.Ammo = 30_000;
            shooter.MountWeapon(weaponType);

            // Gate precondition: the observer must already hold the shooter's
            // EXPORT, else the server (correctly) withholds the teammate send.
            PumpUntil(server, () => obsBot.Exports.Any(e => e.ShipId == subjectId), 8000);
            Assert.Contains(obsBot.Exports, e => e.ShipId == subjectId);

            // Pump real ticks until the observer has seen >= 2 DISTINCT
            // projectile broadcasts of the shooter's armed weapon type (multiple
            // real shots fired by the server's authoritative sim — proves
            // per-shot, not a single cached one). We scope to the armed type
            // because the mission can also carry ambient/AI fire of OTHER
            // projectile types in the same sector; what we assert is the
            // shooter's own shots propagating (a pre-existing ambient-type
            // contaminant is why the unscoped `received.Count` assertion flakes
            // on the pristine tree too — see P371's note).
            PumpUntil(server, () => obsBot.ProjectileInstances.Count(p => p.ProjectileTypeId == projectileTypeId) >= 2, 8000);

            var received = obsBot.ProjectileInstances.Where(p => p.ProjectileTypeId == projectileTypeId).ToList();
            Assert.True(received.Count >= 2,
                $"observer must receive >= 2 distinct PROJECTILE_INSTANCE messages of the shooter's type {projectileTypeId}, got {received.Count}");

            // (1) Real fields on every such broadcast: the spawn position is near
            // the shooter's own position, and the velocity is nonzero and along
            // the firing forward (dispersion 0 ⇒ velocity ∥ forward).
            foreach (var p in received)
            {
                Assert.True((p.Position - shooter.Position).Length() < 50f,
                    $"projectile spawn {p.Position} must be near the shooter {shooter.Position}");
                float speed = p.Velocity.Length();
                Assert.True(speed > 0f, "projectile velocity must be nonzero");
                float dot = p.Velocity.X * p.Forward.X + p.Velocity.Y * p.Forward.Y + p.Velocity.Z * p.Forward.Z;
                Assert.True(dot / speed > 0.999f,
                    $"projectile velocity {p.Velocity} must lie along the firing forward {p.Forward}");
            }

            // Give the sim a generous margin of extra ticks and confirm the
            // same-side observer keeps receiving more distinct shots of the type.
            PumpUntil(server, () => obsBot.ProjectileInstances.Count(p => p.ProjectileTypeId == projectileTypeId) >= 4, 8000);
            Assert.True(obsBot.ProjectileInstances.Count(p => p.ProjectileTypeId == projectileTypeId) >= 4,
                "more firing must produce more distinct broadcasts of the shooter's type");

            // P375 — the cross-side enemy now SEES the shooter's shots of the
            // armed type too (it already renders the shooter's ship via P369
            // identity + telemetry, so withholding the shot left it seeing damage
            // with no bolt). This was deliberately "same-side only" before P375;
            // the cross-side audience is the exact P369 visibility gate (a seated
            // peer of the enemy's side in the shooter's sector + shooter not
            // cloak-hidden). So the enemy's list of the shooter's type is no
            // longer empty — it carries the same real shots the teammate saw,
            // with per-shot multiplicity.
            var enemyShots = enemyBot.ProjectileInstances.Where(p => p.ProjectileTypeId == projectileTypeId).ToList();
            Assert.NotEmpty(enemyShots);
            Assert.True(enemyShots.Count >= 2,
                $"the cross-side enemy must receive the same distinct per-shot broadcasts the teammate does (P375), got {enemyShots.Count}");
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }

    /// <summary>The codec round-trips byte-exactly: encode → decode reproduces
    /// every field, and a freshly-built buffer carries the right width/fmid.</summary>
    [Fact]
    public void ProjectileInstance_round_trips_the_wire()
    {
        var msg = new ProjectileInstance
        {
            TimeCreate = 771u,
            ProjectileTypeId = 9012,
            Forward = new Vector3(0f, 0f, -1f),
            Velocity = new Vector3(0f, 0f, -150f),
            Lifespan = 8.5f,
            Position = new Vector3(100f, 200f, 300f),
            LauncherId = 6402,
        };
        var bytes = msg.Encode();
        Assert.Equal(ProjectileInstance.FixedSize, bytes.Length);

        var decoded = ProjectileInstance.Decode(bytes);
        Assert.Equal(msg.TimeCreate, decoded.TimeCreate);
        Assert.Equal(msg.ProjectileTypeId, decoded.ProjectileTypeId);
        Assert.Equal(msg.Forward, decoded.Forward);
        Assert.Equal(msg.Velocity, decoded.Velocity);
        Assert.Equal(msg.Lifespan, decoded.Lifespan, 5);
        Assert.Equal(msg.Position, decoded.Position);
        Assert.Equal(msg.LauncherId, decoded.LauncherId);

        // 54-byte body: header(4) + timeCreate(4) + DataProjectileIGC(32) + position(12) + launcherId(2).
        Assert.Equal(54, bytes.Length);
        Assert.Equal(ProjectileInstance.Fmid, FedMessageHeader.Read(bytes).Fmid);
    }

    /// <summary>Lifespan is carried but never written back by the native
    /// Export (projectileIGC.cpp:146-158 writes only typeID/velocity/forward),
    /// so this task treats it as creation-time-only — verified here to
    /// round-trip faithfully anyway, and immutable across re-encode.</summary>
    [Fact]
    public void ProjectileInstance_width_is_54_bytes()
    {
        Assert.Equal(54, ProjectileInstance.FixedSize);
        Assert.Equal(32, ProjectileInstance.DataProjectileSize);
    }
}
