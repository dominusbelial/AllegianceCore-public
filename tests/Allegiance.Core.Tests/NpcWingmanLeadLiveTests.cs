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
/// P169 — Wingman combat AI, Stage 3: aim leading (<c>solveForLead</c>).
/// Native solves where to point so a shot intercepts a MOVING target
/// (<c>src/Igc/common.cpp:121-149</c>) and the Wingman fires when it faces that
/// LEAD point (<c>shipIGC.cpp:2463-2476</c>). This port wires that same lead
/// solve into <see cref="Ship.UpdateWingmanCombat"/>'s fire-facing gate: when a
/// weapon is mounted the Wingman must face the lead direction (not the target's
/// raw current position) within the existing firing tolerance. Pursuit steering
/// is untouched — only where the fire CHECK aims is decoupled (the honest,
/// stated simplification vs. native turning the whole ship to the lead vector).
///
/// <list type="bullet">
/// <item><b>Discriminator (loopback-free, deterministic)</b> — directly invokes
/// the port's private <see cref="Ship.SolveForLead"/> (via reflection, the same
/// pattern <c>NpcCrossClusterLiveTests</c> already uses for private members) on
/// a geometrically-constructed laterally-moving target and asserts the solved
/// aim direction (a) swings toward the target's motion and (b) ACTUALLY
/// intercepts the target's future position sphere. This fails if lead-solving
/// were removed or incorrect, cleanly and without any sim variance.</item>
/// <item><b>Live loopback gate discriminator</b> — a real Wingman NPC in a
/// booted server engages an enemy that is driven laterally (kinematically, so
/// its motion is exactly prescribed) with a slow-projectile weapon whose lead
/// angle exceeds the 0.90 facing tolerance. The gate must REFUSE to fire while
/// the Wingman is raw-aligned but not lead-aligned — a snapshot that only the
/// lead-aware gate can produce, and that fails if the gate reverted to raw
/// facing. Also asserts the gate never fires off-lead.</item>
/// </list>
/// Harness mirrors P162's <c>NpcWingmanAttackLiveTests</c>.
/// </summary>
[Collection("LiveNetwork")]
public sealed class NpcWingmanLeadLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // Margins around the production tolerance (WingmanFireFacingCosine = 0.90,
    // Ship.cs): a snapshot counts as "clearly aligned" / "clearly misaligned"
    // only when it is unambiguous, so a borderline 0.90±ε float can't flake the
    // discrimination. Because the production fire gate fires when facing >= 0.90,
    // "raw-aligned" (>= Aligned > 0.90) would have fired under a raw gate, and
    // "lead-misaligned" (<= Misaligned < 0.90) cannot be firing under the lead
    // gate.
    private const float Aligned = 0.908f;
    private const float Misaligned = 0.872f;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P169 Wingman Lead Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 556u, DwCookie = 0x556u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 55300000000000005ul, DrmHash = "p169wingman",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static (Srv Server, Mission Mission, Sector Sector, short NpcSide, short PeerSide, short WingmanDroneId)
        Boot()
    {
        var server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        short npcSide = 0, peerSide = 0, wingDroneId = 0;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            var core = m.Core;
            peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;
            wingDroneId = core.DroneTypes.First(kv => kv.Value.PilotType == Ship.PilotTypeWingman).Key;
            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, new Vector3(985f, 630f, 618f), Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.Boot(Config());
        // P178: keep the bot SEATED (no `using` → not disposed at Boot return)
        // so the mission stays Started — the server's win-by-elimination now
        // (correctly) ends a mission when its last player leaves. The server
        // closes this socket at teardown.
        var bot = new FltBot();
        bot.Connect(server.BoundPort, "p169_wingman");
        bot.SendLogonRequest(Request("p169_wingman"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);

        Assert.NotNull(mission); Assert.NotNull(sector);
        Assert.True(wingDroneId != 0, "core must expose a Wingman pilot-type drone");
        return (server, mission!, sector!, npcSide, peerSide, wingDroneId);
    }

    /// <summary>First fightable (dispersion-0, power&gt;0) weapon — used for the
    /// deterministic geometry discriminator where any sane projectile works.</summary>
    private static (DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC proj) FirstFightable(StaticCore core)
    {
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.EnergyPerShot))
            {
                if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    return (hull, p, pt);
            }
        }
        throw new InvalidOperationException("no fightable hull+weapon in the representative core");
    }

    /// <summary>A mountable weapon whose projectile is the SLOWEST in the core —
    /// maximizes the lead angle (atan(lateralSpeed / projectileSpeed)) for a
    /// given lateral target speed, so the lead-ahead direction visibly exceeds
    /// the 0.90 facing tolerance and the gate genuinely discriminates leading
    /// from raw facing.</summary>
    private static (DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC proj) SlowestProjectileWeapon(StaticCore core)
    {
        DataPartTypeIGC.Weapon? best = null;
        DataProjectileTypeIGC bp = default;
        float bs = float.MaxValue;
        foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                     .Select(p => (DataPartTypeIGC.Weapon)p.Type))
        {
            if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Speed > 0f && pt.Speed < bs)
            {
                bs = pt.Speed; best = p; bp = pt;
            }
        }
        if (best is null)
            throw new InvalidOperationException("no weapon with a projectile in the representative core");
        var hull = core.HullTypes.Values
            .Where(h => h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed && h.MaxEnergy >= best!.EnergyPerShot)
            .OrderBy(h => h.HullId).First();
        return (hull, best, bp);
    }

    /// <summary>Reflection access to the port's private instance
    /// <c>Ship.SolveForLead</c> (same pattern as the private-member reflection in
    /// <c>NpcCrossClusterLiveTests</c>). Returns the intercept time and fills
    /// <c>dir</c> with the world-space unit lead direction.</summary>
    private static float SolveLead(Ship shooter, Ship target, Weapon weapon, float skill, out Vector3 dir)
    {
        var mi = typeof(Ship).GetMethod("SolveForLead", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Ship.SolveForLead not found");
        var args = new object[] { target, weapon, skill, null! };
        float t = (float)(mi.Invoke(shooter, args) ?? 0f);
        dir = (Vector3)args[3]!;
        return t;
    }

    private static float Facing(Ship s, Vector3 worldDir)
    {
        float len = worldDir.Length();
        return len > 1e-6f ? -s.Orientation.TimesInverse(worldDir / len).Z : 1f;
    }

    /// <summary>⭐ Deterministic discriminator: <c>SolveForLead</c> on a
    /// geometrically-constructed laterally-moving target must produce a lead
    /// direction that (a) swings toward the target's motion and (b) actually
    /// intercepts the target's future position sphere — the round fired along
    /// <c>dir</c> lands on the target's radius at the solved time. Cleanly fails
    /// if lead-solving is removed or wrong; no sim variance.</summary>
    [Fact]
    public void SolveForLead_intercepts_a_laterally_moving_target()
    {
        var core = Core();
        var (hull, weaponType, proj) = FirstFightable(core);

        // Target at the origin drifting +Y at 150 u/s; shooter 500 units out on
        // the −X axis, stationary. Raw line-of-sight is exactly +X; a real lead
        // must swing toward +Y so the shot meets the target as it crosses.
        var shooter = new Ship((short)1, hull, new Vector3(-500f, 0f, 0f), Vector3.Zero, Orientation.Identity);
        var target = new Ship((short)2, hull, Vector3.Zero, new Vector3(0f, 150f, 0f), Orientation.Identity);
        var weapon = new Weapon(shooter, weaponType, proj, 0);

        float t = SolveLead(shooter, target, weapon, skill: 1.0f, out var dir);

        var raw = target.Position - shooter.Position; raw /= raw.Length();   // +X
        float leadY = dir.Y;
        float dot = dir.X * raw.X + dir.Y * raw.Y + dir.Z * raw.Z;
        Console.WriteLine($"P169-GEO: t={t:F3}, projSpeed={proj.Speed:F1}, projAbs={proj.AbsoluteF}, dir=({dir.X:F3},{dir.Y:F3},{dir.Z:F3}), dot(raw)={dot:F3}");

        Assert.True(leadY > 0.06f,
            $"the lead direction must swing toward the target's lateral motion: dir.Y={leadY:F3}");
        Assert.True(dot < 0.996f,
            $"the lead direction must differ measurably from the raw line-of-sight: dot={dot:F3}");

        // True intercept: the round's position at the solved time must sit on the
        // target's radius sphere centred on the target's FUTURE position.
        Vector3 pf = shooter.Position + dir * (proj.Speed * t);
        Vector3 tf = target.Position + target.Velocity * t;
        float radius = 0.5f * hull.Length;
        float gap = (pf - tf).Length();
        Console.WriteLine($"P169-INTERCEPT: |pf - tf|={gap:F3}, targetRadius={radius:F3}");
        Assert.True(MathF.Abs(gap - radius) < 0.601f,
            $"the solved aim must intercept the target's future sphere: |pf-tf|={gap:F3} should ≈ radius {radius:F3}");
    }

    /// <summary>⭐ Live loopback gate discriminator: a real Wingman NPC with a
    /// slow-projectile weapon engages an enemy driven LATERALLY (kinematically,
    /// so its motion is exactly prescribed). Because the projectile is slow, the
    /// lead-ahead angle exceeds the 0.90 firing tolerance, so while the Wingman
    /// tracks the target's raw position it is NOT facing the lead point — and the
    /// new gate must therefore HOLD FIRE there, even though a raw-facing gate
    /// would have fired. Proves the fire decision is gated on the lead direction,
    /// not the raw position; fails if lead-solving were removed.</summary>
    [Fact]
    public void Wingman_fire_gate_leads_a_laterally_moving_enemy()
    {
        var (srv, mission, sector, npcSide, peerSide, wingDroneId) = Boot();
        using (srv)
        {
            var core = mission.Core;
            var enemyHull = core.HullTypes.Values.OrderByDescending(h => h.Length).First();
            Vector3 ep = new(600f, 585f, 545f);
            var enemy = new Ship((short)5001, enemyHull, ep, Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            mission.PlaceShip(enemy, sector);

            var (fhull, weapon, proj) = SlowestProjectileWeapon(core);
            float fireRange = proj.Speed * proj.Lifespan;
            Console.WriteLine(
                $"P169-WEAPON: projSpeed={proj.Speed:F1}, projAbs={proj.AbsoluteF}, fireRange={fireRange:F1}, " +
                $"nominalLeadAngle≈{MathF.Atan2(0.868f * proj.Speed, proj.Speed) * 180f / MathF.PI:F0}°");

            // Start the Wingman already well inside fire range so no long chase is
            // needed — it only has to track the moving target, not catch it.
            Vector3 wp = ep - new Vector3(Math.Min(fireRange * 0.4f, 650f), 0f, 0f);
            var wingman = srv.SpawnNpcShip(mission, sector, npcSide, wp, wingDroneId);
            Assert.Equal(Ship.PilotTypeWingman, wingman.PilotType);
            wingman.ChangeHull(fhull);
            wingman.Energy = fhull.MaxEnergy * 100f;
            wingman.Ammo = 10000;
            wingman.MountWeapon(weapon);

            // Acquire the enemy (ours is nearer than the parked peer at
            // (985,630,618), so the nearest-enemy search must choose it).
            Pump(srv, () => wingman.AttackTarget is not null, 800);
            Assert.Same(enemy, wingman.AttackTarget);

            // Drive the enemy laterally in a bounded vertical lane (bounce at
            // ±1600) so it stays near the sector centre and in range the whole
            // time, with a lateral speed that forces a lead well above tolerance.
            float latSpeed = 0.868f * proj.Speed;
            Vector3 lat = new(0f, latSpeed, 0f);
            const double dt = 0.020;
            double elapsed = 0;

            int snapshots = 0, rawAlignedNotFiring = 0, firedOffLead = 0, fired = 0;
            int ticks = 0; const int MaxTicks = 7200;
            while (ticks < MaxTicks && rawAlignedNotFiring == 0 && firedOffLead == 0)
            {
                elapsed += dt;
                Vector3 pos = ep + lat * (float)elapsed;
                if (pos.Y > ep.Y + 1600f) { lat = new Vector3(0f, -latSpeed, 0f); }
                else if (pos.Y < ep.Y - 1600f) { lat = new Vector3(0f, latSpeed, 0f); }
                enemy.Position = pos;
                enemy.Velocity = lat;

                srv.TickSim();
                ticks++; Thread.Sleep(1);

                if (!ReferenceEquals(wingman.AttackTarget, enemy)) break;
                if (wingman.Sector != sector || enemy.Sector != sector) break;
                float dist = (wingman.Position - enemy.Position).Length();
                if (dist > fireRange) continue;
                snapshots++;

                bool firing = (wingman.StateM & ShipControlState.AllWeapons) != 0;
                float facingRaw = Facing(wingman, enemy.Position - wingman.Position);
                SolveLead(wingman, enemy, wingman.Weapons[0], skill: 1.0f, out var leadDir);
                float facingLead = leadDir.LengthSquared() > 1e-6f
                    ? Facing(wingman, leadDir)
                    : -1f;                                 // no-solution → treat as maximally misaligned

                if (firing) fired++;
                // Gate invariant: never fire while clearly NOT facing the lead point.
                if (firing && facingLead <= Misaligned) firedOffLead++;
                // The discriminator: in range, clearly facing the RAW position but
                // clearly NOT the lead point → the lead-aware gate MUST be holding
                // fire (a raw gate would have fired here).
                if (!firing && facingRaw >= Aligned && facingLead <= Misaligned)
                    rawAlignedNotFiring++;
            }

            Console.WriteLine(
                $"P169-SNAP: ticks={ticks}, snapshots={snapshots}, fired={fired}, firedOffLead={firedOffLead}, rawAlignedButNotFiring={rawAlignedNotFiring}");

            Assert.True(rawAlignedNotFiring > 0,
                "the fire gate must refuse to fire while the Wingman is raw-aligned but not lead-aligned — " +
                "the discriminant that fails if lead-solving were removed (a raw gate would have fired there)");
            Assert.True(firedOffLead == 0,
                $"the fire gate must never fire while clearly not facing the lead direction: firedOffLead={firedOffLead}");
            Assert.Same(enemy, wingman.AttackTarget);
        }
    }
}
