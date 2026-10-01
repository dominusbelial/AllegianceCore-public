using System.IO;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P465c — weapon-muzzle (hardpoint) offsets. Verifies that
/// <see cref="HullWeaponPositions.Compute"/> reproduces native's per-mount
/// weapon positions (<c>ChullTypeIGC::Initialize</c>,
/// <c>src/Igc/hullTypeIGC.cpp:89-136</c>: frame position × 0.5·length/radius,
/// zero when the frame is missing, with the HitTest::Load sign flip) from a
/// REAL PCore001 hull and its REAL <c>.cvh</c>, and that
/// <see cref="Weapon.FireWeapon"/> spawns each round at
/// <c>ship position + offset × shipOrientation</c> (<c>src/Igc/weaponIGC.cpp:203</c>)
/// instead of the ship centre when a <see cref="WeaponMuzzleCache"/> is wired.
/// </summary>
public class HullWeaponPositionsTests
{
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

    private static readonly Lazy<Dictionary<string, string>> CvhByName = new(() =>
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in CvhCorpus.Files)
            d.TryAdd(Path.GetFileNameWithoutExtension(p), p);
        return d;
    });

    /// <summary>Locate a hull's real <c>.cvh</c> by <c>ModelName</c> (flat file
    /// naming, the server's <c>&lt;artPath&gt;/&lt;modelName&gt;.cvh</c> convention),
    /// or null when the corpus lacks it.</summary>
    private static CvhFile? LoadCvh(string modelName)
        => CvhByName.Value.TryGetValue(modelName, out var p)
            ? CvhReader.Parse(File.ReadAllText(p)) : null;

    private static IReadOnlyList<SideSpec> SideSpecs()
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        return [new SideSpec(0, "Team 1", civ.CivilizationId)];
    }

    private static Mission NewStartedMission()
    {
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, SideSpecs(), new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>A real hull whose hardpoint 0 is a fixed mount with a NONZERO
    /// <c>.cvh</c> frame offset, paired with a dispersion-0 weapon that fires a
    /// real projectile and costs no more energy than the hull carries — so a
    /// fired round demonstrably leaves the gun, not the centre. Data-driven:
    /// nothing is hardcoded.</summary>
    private static readonly Lazy<(DataHullTypeIGC Hull, DataPartTypeIGC.Weapon Weapon, DataProjectileTypeIGC Projectile, Vector3 Offset)> Muzzle =
        new(() =>
        {
            var core = RepresentativeCore.Value;
            foreach (var hull in core.HullTypes.Values
                         .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                         .OrderBy(h => h.HullId))
            {
                var model = LoadCvh(hull.ModelName);
                if (model is null) continue;
                var offsets = HullWeaponPositions.Compute(hull, model);
                if (offsets.Length == 0 || offsets[0] == Vector3.Zero) continue;
                foreach (var w in core.PartTypes.Values
                             .Where(p => p.EquipmentType == EquipmentType.Weapon)
                             .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                             .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                             .OrderBy(w => w.EnergyPerShot))
                {
                    if (core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var pt))
                        return (hull, w, pt, offsets[0]);
                }
            }
            throw new InvalidOperationException("no muzzle-fightable hull+weapon in the representative core");
        });

    private static Ship NewShip() => new(
        shipId: 9350,
        hull: Muzzle.Value.Hull,
        position: new Vector3(40f, -25f, 60f),
        velocity: Vector3.Zero,
        orientation: Orientation.Identity);

    private static Weapon EquipSingleShot(Ship ship)
    {
        var (_, weaponType, _, _) = Muzzle.Value;
        ship.StateM |= ShipControlState.AllWeapons;
        if (weaponType.CAmmoPerShot > 0)
            ship.Ammo = 1;                            // one round, then the burst stops
        else
            ship.Energy = weaponType.EnergyPerShot;   // exactly one shot drains below threshold
        return ship.MountWeapon(weaponType, mountId: 0);
    }

    /// <summary>The native expected value for mount 0, derived INDEPENDENTLY of
    /// the production helper: the <c>.cvh</c> frame position after the
    /// HitTest::Load sign flip (<c>src/_Utility/HitTest.cpp:796-801</c>:
    /// position.x = −x), times m_scale = 0.5·length/radius
    /// (<c>hullTypeIGC.cpp:62</c>).</summary>
    private static Vector3 NativeMount0Expected(CvhFile model, DataHullTypeIGC hull)
    {
        var frame = model.Frames.First(f => string.Equals(f.Name, hull.Hardpoints[0].FrameName, StringComparison.Ordinal));
        var flipped = new Vector3(-frame.Position.X, frame.Position.Y, frame.Position.Z);
        return flipped * (0.5f * hull.Length / model.Radius);
    }

    // ── computed position vs native formula (real hull + real .cvh) ────────

    /// <summary>⭐ A real PCore001 hull with its real <c>.cvh</c>: the computed
    /// mount-0 weapon position equals <c>frame.position · 0.5·length/radius</c>
    /// (with the HitTest::Load sign flip), exactly native.</summary>
    [Fact]
    public void Real_hull_mount0_position_matches_frame_times_scale()
    {
        var (hull, _, _, offset) = Muzzle.Value;
        var model = LoadCvh(hull.ModelName);
        Assert.NotNull(model);

        var expected = NativeMount0Expected(model!, hull);
        Assert.NotEqual(Vector3.Zero, expected);                 // the test premise: a real, non-degenerate muzzle
        Assert.Equal(expected.X, offset.X, 4);
        Assert.Equal(expected.Y, offset.Y, 4);
        Assert.Equal(expected.Z, offset.Z, 4);
    }

    // ── missing-frame fallback to zero ────────────────────────────────────

    /// <summary>A model that parses but has <b>no frames</b>: every mount must
    /// fall back to zero (native <c>pfd ? position·scale : Vector::GetZero()</c>,
    /// hullTypeIGC.cpp:102,132).</summary>
    [Fact]
    public void Missing_frame_falls_back_to_zero_even_with_a_model()
    {
        var hull = RepresentativeCore.Value.HullTypes.Values.First(h => h.Hardpoints.Length > 0);
        // Minimal valid .cvh: header + 1 hull (1 vertex at origin, 1 adjacency), NO frames.
        var model = CvhReader.Parse("100 1 1 1 1\n1\n1 1 0 0 0\n0 0 0 1\n0\n");
        var computed = HullWeaponPositions.Compute(hull, model);
        Assert.Equal(hull.Hardpoints.Length, computed.Length);
        Assert.All(computed, v => Assert.Equal(Vector3.Zero, v));
    }

    /// <summary>No <c>.cvh</c> at all (the historic "no hull mesh" case): every
    /// mount is zero, so rounds keep spawning at the ship centre.</summary>
    [Fact]
    public void Null_model_yields_all_zero_offsets()
    {
        var hull = RepresentativeCore.Value.HullTypes.Values.First(h => h.Hardpoints.Length > 0);
        var computed = HullWeaponPositions.Compute(hull, null);
        Assert.Equal(hull.Hardpoints.Length, computed.Length);
        Assert.All(computed, v => Assert.Equal(Vector3.Zero, v));
    }

    // ── fired round spawn position ────────────────────────────────────────

    /// <summary>⭐ Wiring a real <see cref="WeaponMuzzleCache"/> into the
    /// mission, a fired round's spawn equals <c>ship position + offset ·
    /// orientation</c> — it leaves the gun, not the centre.</summary>
    [Fact]
    public void Fired_round_spawns_at_ship_plus_mount_offset_rotated_by_orientation()
    {
        var mission = NewStartedMission();
        mission.WeaponMuzzles = new WeaponMuzzleCache(LoadCvh);
        var sector = mission.Sectors[0];
        var ship = NewShip();
        mission.PlaceShip(ship, sector);
        var weapon = EquipSingleShot(ship);

        weapon.Update(0.0);                                 // select → activate, cooldown armed
        weapon.Update(weapon.GetDtBurst() + 0.005);          // first chunk past cooldown → one shot
        var proj = Assert.Single(sector.Models.OfType<Projectile>());

        var (_, _, _, offset) = Muzzle.Value;
        var expected = ship.Position + offset * ship.Orientation;   // identity → ship.Position + offset
        Assert.Equal(expected.X, proj.Position.X, 4);
        Assert.Equal(expected.Y, proj.Position.Y, 4);
        Assert.Equal(expected.Z, proj.Position.Z, 4);
        Assert.NotEqual(ship.Position, proj.Position);       // rounds no longer leave the centre
    }

    /// <summary>Characterises the PRE-fix behaviour (spawn = centre): a mission
    /// with no <see cref="WeaponMuzzleCache"/> wired keeps the historic
    /// ship-centre spawn (muzzle offset zero). This is what the code under test
    /// returned before P465c wired a cache, and what a bare test mission still
    /// returns.</summary>
    [Fact]
    public void Without_a_wired_cache_round_spawns_at_ship_centre()
    {
        var mission = NewStartedMission();                   // no WeaponMuzzles wired
        var sector = mission.Sectors[0];
        var ship = NewShip();
        mission.PlaceShip(ship, sector);
        var weapon = EquipSingleShot(ship);

        weapon.Update(0.0);
        weapon.Update(weapon.GetDtBurst() + 0.005);
        var proj = Assert.Single(sector.Models.OfType<Projectile>());
        Assert.Equal(ship.Position, proj.Position);
    }

    /// <summary>An out-of-range mount never throws and yields zero (the native
    /// guard is implied — <c>m_positionWeapons</c> is indexed by a valid mount;
    /// a defensive zero is the port's contract for an impossible mount).</summary>
    [Fact]
    public void Out_of_range_mount_yields_zero()
    {
        var cache = new WeaponMuzzleCache(_ => null);
        var hull = RepresentativeCore.Value.HullTypes.Values.First(h => h.Hardpoints.Length > 0);
        Assert.Equal(Vector3.Zero, cache.Position(hull, hull.Hardpoints.Length + 5));
        Assert.Equal(Vector3.Zero, cache.Position(hull, -1));
    }

    /// <summary>The cache resolves each distinct hull model ONCE and returns a
    /// stable array (the native once-per-hull-type computation is preserved, not
    /// reparsed per shot).</summary>
    [Fact]
    public void Cache_is_keyed_by_model_name()
    {
        var (hull, _, _, offset) = Muzzle.Value;
        var cache = new WeaponMuzzleCache(LoadCvh);
        Assert.Equal(offset.X, cache.Position(hull, 0).X, 4);   // first call resolves + caches
        Assert.Equal(offset.Y, cache.Position(hull, 0).Y, 4);
        Assert.Equal(offset.Z, cache.Position(hull, 0).Z, 4);
    }
}
