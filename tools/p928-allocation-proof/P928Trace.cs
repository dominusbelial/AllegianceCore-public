using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.P928;

// This file is also compiled into the replay test. No production instrumentation
// or flags: the public site callbacks observe dispatch in its actual order.
//
// P951 — machine-independent golden format (v2). The v1 format hashed raw float
// bits of authoritative state, site-callback payloads and wire payloads. Those
// floats are machine-sensitive: libm transcendentals (the native goto planner's
// steering angles) differ by ULPs between environments, and a crowded sector
// amplifies that into visibly different trajectories for the planner ships
// (measured up to ~5 m over the 500 ticks between a workstation and the runner
// image; see docs/reports/P928_GOLDEN_PORTABLE_P951.md). v2 therefore hashes
// three independent streams:
//   state-discrete  — ids, types, membership, order, dead/StateM/visibility/
//                     dock/parent bits and weapon mount/fire state (measured
//                     bit-identical for every model, every tick);
//   events-discrete — collision/kill/shot callback identity (kind + involved
//                     ids) in actual dispatch order; damage and projectile-hit
//                     callbacks are counted in their own columns instead of
//                     hashed, because a grazing hit/miss flip is physically
//                     unavoidable across machines (measured ±1 event on 20 of
//                     500 ticks);
//   continuous      — the float state of every model EXCEPT the twelve
//                     goto-planner ships (whose trajectories are chaotic across
//                     machines) and except ordnance/mine health (hit/miss
//                     knife-edge), quantized to tolerances far above the
//                     measured noise: positions/velocities 0.01, orientation
//                     components 0.001, fractions 0.001, energy/ammo/fuel/ore
//                     0.01.
// The scenario, its seed and its RNG draw order are unchanged from v1.
public static class P928Trace
{
    public static string[] Run(bool detailed = false)
    {
        var root = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var coreBytes = File.ReadAllBytes(Path.Combine(root, "PCore001.igc"));
        var core = StaticCore.Load(IgcReader.Parse(coreBytes));
        var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).ToArray();
        var site = new Site(detailed);
        var map = MissionMap.FromLists([DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with
            { ClusterId = 0, Name = "P928 crowded" }], [], [], []);
        using var mission = Mission.Assemble(core, map,
            [new(0, "Alpha", civs[0].CivilizationId), new(1, "Bravo", civs[1].CivilizationId)], site);
        mission.Stage = MissionStage.Started;
        mission.CrowdingPenaltyEnabled = false;
        mission.NativeGotoPlanEnabled = true;
        var sector = mission.Sectors[0];
        sector.CollisionHulls = new(name =>
        {
            var p = Path.Combine(root, name + ".cvh");
            return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
        });
        var rng = new DrawRandom(928);
        var fighter = core.HullTypes.Values.Where(h => h.MaxFixedWeapons > 0 && h.HitPoints > 0 &&
            File.Exists(Path.Combine(root, h.ModelName + ".cvh"))).OrderBy(h => h.HullId).First();
        var turret = core.HullTypes.Values.Where(h => h.MaxWeapons > h.MaxFixedWeapons && h.HitPoints > 0 &&
            File.Exists(Path.Combine(root, h.ModelName + ".cvh"))).OrderBy(h => h.HullId).First();
        var gun = core.PartTypes.Values.Select(p => p.Type).OfType<DataPartTypeIGC.Weapon>()
            .Where(w => core.ProjectileTypes[w.ProjectileTypeId].Power > 0 && w.DtimeBurst > 0)
            .OrderBy(w => w.ProjectileTypeId).First() with { Dispersion = .005f, EnergyPerShot = .1f, CAmmoPerShot = 0, DtimeBurst = .4f };
        var ships = new List<Ship>();
        var plannerShips = new HashSet<short>();
        for (short i = 0; i < 240; i++)
        {
            var hull = (i % 12 == 0 ? turret : fighter) with { HitPoints = 20000, MaxEnergy = 1000 };
            // Paired clouds deliberately overlap at the start: both actual
            // physical collision callbacks and projectile damage must be covered.
            var position = new Vector3((i / 2 % 10) * 80 + (i % 2) * 3 - 400,
                (i / 20 % 6) * 90 - 270, (i / 120) * 450 - 225);
            var ship = new Ship((short)(1000 + i), hull, position,
                new Vector3(i % 2 == 0 ? 12 : -12, 0, 0), Orientation.Identity)
                { SideId = (short)(i % 2), IsNpc = i % 20 == 5, Controls = new(0, 0, 0, -1),
                  StateM = ShipControlState.CoastButton | ShipControlState.AllWeapons };
            mission.PlaceShip(ship, sector);
            ships.Add(ship);
            if (ship.IsNpc) ship.GotoWaypoint = new(GotoKind.Waypoint, sector.Id, new(700, 600, -500));
            // Register turrets out of mount order, including duplicate mount IDs.
            // OrderBy's stable ties and fixed FirstOrDefault are both exercised.
            short[] mounts = i % 12 == 0
                ? [(short)(hull.MaxWeapons - 1), 0, (short)hull.MaxFixedWeapons, (short)hull.MaxFixedWeapons, 0]
                : [0, 0];
            foreach (short mount in mounts)
            {
                var w = ship.MountWeapon(gun, mount);
                typeof(Weapon).GetField("_rng", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(w, rng);
            }
        }
        for (short i = 0; i < 20; i++)
        {
            var parent = ships[i * 12];
            var gunner = new Ship((short)(2000 + i), fighter, parent.Position, Vector3.Zero, Orientation.Identity)
                { SideId = parent.SideId, Controls = new(.2f, -.1f, .15f, -1), StateM = ShipControlState.AllWeapons };
            mission.PlaceShip(gunner, sector);
            gunner.ParentShip = parent;
            foreach (var w in parent.Weapons.Where(w => w.MountId >= parent.HullType.MaxFixedWeapons)) w.SetGunner(gunner);
            ships.Add(gunner);
        }
        foreach (var ship in ships) if (ship.IsNpc) plannerShips.Add(ship.Id);
        var rockType = DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]);
        for (short i = 0; i < 12; i++)
            mission.PlaceAsteroid(new(rockType with { AsteroidId = (short)(3000 + i), ClusterId = 0,
                Position = [-400 + i * 80, 0, 0], Radius = 15, HitPoints = 100000 }), sector);
        var stationType = core.StationTypes.Values.Where(t => t.MaxArmorHitPoints > 0 && t.MaxShieldHitPoints > 0 &&
            !t.HasCapability(DataStationTypeIGC.SabmPedestal)).OrderBy(t => t.StationTypeId).First();
        for (short i = 0; i < 2; i++)
            mission.PlaceStation(new(new DataStationIGC([i == 0 ? -950 : 950, 0, 0], [0, 1, 0], [1, 0, 0],
                [0, 0, 0, 1], 0, i, (short)(4000 + i), stationType.StationTypeId, 1, 1, "P928 base"), stationType), sector);
        var probeType = core.ProbeTypes.Values.Where(p => p.ProjectileTypeId < 0 && p.Lifespan > 0)
            .OrderBy(p => p.ExpendableTypeId).First() with { Lifespan = 1000 };
        for (short i = 0; i < 8; i++)
        {
            var probe = new Probe((short)(5000 + i), sector, probeType, new(i * 70 - 280, 0, -170), 0, (short)(i % 2));
            // Constructor attitude is random in production. Pin the initial
            // state before the run; this sensor has no stochastic firing path.
            probe.Orientation.Reset();
            sector.Add(probe);
        }
        var mineType = core.MineTypes.Values.OrderBy(m => m.ExpendableTypeId).First() with { Power = .01f, Lifespan = 1000 };
        for (short i = 0; i < 12; i++)
            sector.Add(new Mine((short)(6000 + i), sector, mineType, new(i * 70 - 400, 0, -225), 0));
        var pt = core.ProjectileTypes[gun.ProjectileTypeId] with { Power = 1000000, BlastPower = 0, Lifespan = 2, Radius = 1 };
        short next = 10000;
        var lines = new List<string> { (detailed ? "# P928 supplemental v2" : "# P928 v2") + " seed=928 ticks=500 ships=260; core-sha256=" + Convert.ToHexString(SHA256.HashData(coreBytes)),
            "tick,state-discrete-sha256,events-discrete-sha256,continuous-sha256,rng-draws,collisions,damage,kills,shots" };
        for (int tick = 1; tick <= 500; tick++)
        {
            double now = tick * .053;
            site.Clear();
            // Sustained seeded salvo, with a lethal shot touching a chosen live
            // target every 25 ticks, exercises kills and changing membership.
            for (int k = 0; k < 6; k++)
            {
                var target = ships[rng.Next(240)];
                var pos = k == 0 && tick % 25 == 0 ? target.Position : new Vector3(rng.Next(-600, 600), rng.Next(-350, 350), rng.Next(-250, 250));
                sector.Add(new Projectile(next++, sector, k == 0 && tick % 25 == 0 ? pt : pt with { Power = .1f },
                    pos, new(0, 0, -100), new(0, 0, -1), mission.LastUpdate));
            }
            mission.Update(now);
            using var state = new MemoryStream();
            using var writer = new BinaryWriter(state);
            // Discrete authoritative state: membership, order and every
            // machine-stable non-float field (measured bit-identical across
            // environments for all 260 ships and all static/sensor models).
            writer.Write((int)mission.Stage);
            foreach (var model in sector.Models)
            {
                writer.Write(model.GetType().Name); writer.Write(model.Id);
                for (short side = 0; side < 2; side++) { writer.Write(model.SeenBySide(side)); writer.Write(model.GetCurrentEye(side)); }
                if (model is Ship s) WriteShipDiscrete(writer, s, detailed, plannerShips.Contains(s.Id));
            }
            // Retired/dead ships and static targets are authoritative state too.
            foreach (var s in ships) { writer.Write(s.Id); WriteShipDiscrete(writer, s, detailed, plannerShips.Contains(s.Id)); }
            foreach (var a in sector.Mineable) writer.Write(a.Data.AsteroidId);
            foreach (var s in sector.StationRuntimes) writer.Write(s.Data.StationId);
            using var continuous = new MemoryStream();
            using var cwriter = new BinaryWriter(continuous);
            // Quantized continuous state, planner ships and knife-edge health
            // excepted (see the header comment for the measured rationale).
            foreach (var model in sector.Models)
            {
                if (model is Ship s && !plannerShips.Contains(s.Id)) WriteShipContinuous(cwriter, s, detailed);
                if (model is Probe p) { Q(cwriter, p.Fraction, .001f); Q(cwriter, p.Ammo, .01f); }
            }
            foreach (var s in ships) if (!plannerShips.Contains(s.Id)) WriteShipContinuous(cwriter, s, detailed);
            foreach (var a in sector.Mineable) { Q(cwriter, a.Fraction, .001f); Q(cwriter, a.OreRemaining, .01f); }
            foreach (var s in sector.StationRuntimes) { Q(cwriter, s.ArmorFraction, .001f); Q(cwriter, s.ShieldFraction, .001f); }
            lines.Add(string.Join(',', tick.ToString(CultureInfo.InvariantCulture), Hash(state), Hash(site.Events), Hash(continuous),
                rng.Draws, site.Collisions, site.Damage, site.Kills, site.Shots));
        }

        return lines.ToArray();
    }
    private static string Hash(MemoryStream stream) => Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    private static void Vec(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    private static void Basis(BinaryWriter w, Orientation o) { Vec(w, o.GetRight()); Vec(w, o.GetUp()); Vec(w, o.GetBackward()); }
    /// <summary>Quantize to a multiple of <paramref name="step"/> (banker's
    /// rounding — an IEEE operation, identical on every machine for identical
    /// input) so ULP-scale environment noise cannot reach the hash.</summary>
    private static void Q(BinaryWriter w, float v, float step) => w.Write(MathF.Round(v / step) * step);
    private static void QVec(BinaryWriter w, Vector3 v, float step) { Q(w, v.X, step); Q(w, v.Y, step); Q(w, v.Z, step); }
    private static void QBasis(BinaryWriter w, Orientation o, float step) { QVec(w, o.GetRight(), step); QVec(w, o.GetUp(), step); QVec(w, o.GetBackward(), step); }

    private static void WriteShipDiscrete(BinaryWriter w, Ship s, bool detailed, bool planner)
    {
        // The native goto planner toggles thrust control-state bits from its
        // own machine-sensitive steering floats (measured: bit 0x40 flips on
        // 32 of 500 ticks between environments for planner ships only), so a
        // planner ship's StateM is not hashed; every other discrete field is
        // measured bit-identical across environments.
        if (!planner) w.Write((int)s.StateM);
        w.Write(s.Dead); w.Write(s.StationId ?? -1); w.Write(s.ParentShip?.Id ?? -1);
        foreach (var gun in s.Weapons)
        {
            w.Write(gun.MountId); w.Write(gun.Active); w.Write(gun.FiringShot);
            w.Write(gun.TurretRoundsFired); w.Write(gun.FiringBurst);
        }
    }

    private static void WriteShipContinuous(BinaryWriter w, Ship s, bool detailed)
    {
        Q(w, s.Fraction, .001f); Q(w, s.ShieldFraction, .001f); Q(w, s.Energy, .01f); Q(w, s.Ammo, .01f); Q(w, s.Fuel, .01f);
        if (detailed)
        {
            Q(w, s.Ore, .01f); Q(w, s.Cloaking, .001f);
            foreach (float rate in s.TurnRates) Q(w, rate, .001f);
        }
        QBasis(w, s.Orientation, .001f); QVec(w, s.Velocity, .01f); QVec(w, s.Position, .01f);
        if (detailed) foreach (var gun in s.Weapons) Q(w, gun.MountedFraction, .001f);
    }

    private sealed class DrawRandom(int seed) : Random(seed)
    {
        public long Draws;
        public override int Next(int maxValue) { Draws++; return base.Next(maxValue); }
        public override int Next(int minValue, int maxValue) { Draws++; return base.Next(minValue, maxValue); }
        public override double NextDouble() { Draws++; return base.NextDouble(); }
    }
    // P951: event callbacks are hashed by identity only — kind byte and the
    // involved ids, in actual dispatch order. Payload floats (impact points,
    // impulses, damage amounts) are environment-sensitive and no longer hashed;
    // damage/hit callbacks are additionally knife-edge (a grazing projectile
    // hits or misses with ULP-scale position noise), so damage is compared per
    // tick with a measured tolerance by the test instead of hashed here.
    private sealed class Site : IIgcSite
    {
        public readonly MemoryStream Events = new();
        private readonly BinaryWriter _writer;
        public int Collisions, Damage, Kills, Shots;
        private readonly bool _detailed;
        public Site(bool detailed) { _detailed = detailed; _writer = new(Events); }
        public void Clear() { Events.SetLength(0); Collisions = Damage = Kills = Shots = 0; }
        public void CollisionEvent(Ship a, Ship? b, short sector, Vector3 impact, float j1, float j2)
        { Collisions++; _writer.Write((byte)1); _writer.Write(a.Id); _writer.Write(b?.Id ?? -1); _writer.Write(sector); }
        public void DamageShipEvent(double now, IShipIgc ship, IModelIgc? launcher, byte type, float amount, float leakage, Vector3 p1, Vector3 p2)
        { Damage++; }
        public void KillShipEvent(double now, IShipIgc ship, IModelIgc? launcher, float amount, Vector3 p1, Vector3 p2)
        { Kills++; _writer.Write((byte)3); if (_detailed) _writer.Write((launcher as Allegiance.Core.Sim.Model)?.Id ?? -1); _writer.Write(((Ship)ship).Id); }
        public void DamageStationEvent(IStationIgc station, IModelIgc launcher, byte type, float amount)
        { if (!_detailed) return; _writer.Write((byte)6); _writer.Write(((Station)station).StationId); _writer.Write((launcher as Allegiance.Core.Sim.Model)?.Id ?? -1); _writer.Write(type); }
        public void KillStationEvent(IStationIgc station, IModelIgc launcher, float amount)
        { if (!_detailed) return; _writer.Write((byte)7); _writer.Write(((Station)station).StationId); }
        public void ProbeDestroyedEvent(Probe probe)
        { if (!_detailed) return; _writer.Write((byte)8); _writer.Write(probe.Id); }
        public void MineDestroyedEvent(Mine mine)
        { if (!_detailed) return; _writer.Write((byte)9); _writer.Write(mine.Id); }
        public void ProjectileCreatedEvent(Projectile p)
        { Shots++; _writer.Write((byte)4); _writer.Write(p.Id); }
        public void ProjectileHitEvent(Projectile p, Vector3 impact, Vector3 normal, float radius, short target, short sector, bool shield)
        { }
    }
}
