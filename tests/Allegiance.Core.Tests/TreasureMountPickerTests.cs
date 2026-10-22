using Allegiance.Core;
using Allegiance.TestInfra;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P758 merge — pins <see cref="TreasureMountPicker.PickMount"/> against the
/// native <c>HitTreasure(c_tcPart, …)</c> search (<c>shipIGC.h:1239-1305</c>):
/// empty real mount first, merge into a same-type launcher with room, cargo
/// fallback, then <c>c_mountNA</c>.
/// </summary>
public class TreasureMountPickerTests
{
    // Lazy, so a clean clone (no artwork corpus) reaches Require.Artwork() and
    // SKIPS instead of failing in the type initializer (CI-1).
    private static readonly Lazy<StaticCore> LazyCore = new(() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc"))));
    private static StaticCore Core => LazyCore.Value;

    private static (DataHullTypeIGC Hull, DataPartTypeIGC Part) WeaponForMountZero() =>
        Core.HullTypes.Values.Where(h => h.MaxWeapons > 0)
            .SelectMany(h => Core.PartTypes.Values
                .Where(p => p.EquipmentType == EquipmentType.Weapon && h.CanMountPart(p, 0))
                .Select(p => (h, p)))
            .First();

    private static ExpandedPartData Cargo(short partId, int slot, short amount = 1) =>
        new(partId, amount, unchecked((byte)(-slot - 1)), ExpandedPartData.FullyMounted);

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Empty_real_mount_wins()
    {
        Require.Artwork();
        var (hull, part) = WeaponForMountZero();
        Assert.Equal(0, TreasureMountPicker.PickMount(hull, Core, part, [], [], 1));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Occupied_mounts_fall_back_to_the_first_empty_cargo_slot()
    {
        Require.Artwork();
        var (hull, part) = WeaponForMountZero();
        // every hardpoint holds this weapon type (not a launcher → no merge)
        var mounted = Enumerable.Range(0, hull.MaxWeapons)
            .Select(m => new ExpandedPartData(part.PartId, 1, (byte)m, ExpandedPartData.FullyMounted)).ToList();
        var cargo = new List<ExpandedPartData> { Cargo(part.PartId, 0) };
        Assert.Equal(-2, TreasureMountPicker.PickMount(hull, Core, part, mounted, cargo, 1));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void No_mount_and_full_cargo_declines_with_MountNA()
    {
        Require.Artwork();
        var (hull, part) = WeaponForMountZero();
        var mounted = Enumerable.Range(0, hull.MaxWeapons)
            .Select(m => new ExpandedPartData(part.PartId, 1, (byte)m, ExpandedPartData.FullyMounted)).ToList();
        var cargo = Enumerable.Range(0, Ship.CargoCapacity).Select(s => Cargo(part.PartId, s)).ToList();
        Assert.Equal(TreasureMountPicker.MountNA, TreasureMountPicker.PickMount(hull, Core, part, mounted, cargo, 1));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Same_type_launcher_with_room_merges_into_the_existing_magazine()
    {
        Require.Artwork();
        // Launchers live in the core's compact launcher table; build the part
        // form the client catalogue uses (DataPartTypeIGC.FromMissileLauncher).
        var missileLaunchers = Core.Launchers.Values
            .Where(l => Core.MissileTypes.ContainsKey(l.ExpendabletypeId))
            .Select(l => DataPartTypeIGC.FromMissileLauncher(l.PartId, l, Core.MissileTypes[l.ExpendabletypeId]))
            .ToList();
        var (hull, part) = Core.HullTypes.Values
            .SelectMany(h => missileLaunchers
                .Where(p => TreasureMountPicker.IsLauncher(p) && h.CanMountPart(p, 0)
                            && Ship.ClampPartAmount(h, Core, p, short.MaxValue) >= 2)
                .Select(p => (h, p)))
            .First();
        var mounted = new List<ExpandedPartData> { new(part.PartId, 1, 0, ExpandedPartData.FullyMounted) };
        Assert.Equal(0, TreasureMountPicker.PickMount(hull, Core, part, mounted, [], 1));

        // A full magazine doesn't merge: the part goes to cargo instead.
        short max = Ship.ClampPartAmount(hull, Core, part, short.MaxValue);
        var full = new List<ExpandedPartData> { new(part.PartId, max, 0, ExpandedPartData.FullyMounted) };
        Assert.Equal(-1, TreasureMountPicker.PickMount(hull, Core, part, full, [], 1));
    }
}
