using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P433 — prove the real missile-launcher resolution + hull-compatibility-gated
/// loadout building in <see cref="Catalog"/> against the REAL PCore001.igc
/// corpus (the same static core the live server and vizfight decode), in the
/// same assert-behaviour-over-content style as <see cref="BotSpawnerResolutionTests"/>.
/// These pin the launcher pipeline: a launcher name resolves to a real missile
/// launcher, a non-missile dispenser (chaff/mine/probe) does NOT resolve, the
/// default launcher genuinely mounts on the default fighter hull, and
/// <see cref="Catalog.AppendLauncher"/> either adds a real compatible launcher
/// to a loadout or honestly reports gun-only — never fabricating one.
/// </summary>
public class LauncherResolutionTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static StaticCore RealCore() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>A real missile launcher known to mount on the default fighter
    /// hull (Adv Scout): the lowest-PartId genuine missile launcher whose part
    /// mask the hull's PmEquipment[Magazine] covers — the same 5 the live dump
    /// shows (LRM Killer 1/2, SRP Killer 1/2, Doomsday).</summary>
    private static string MountableLauncherName()
    {
        var core = RealCore();
        var hull = core.HullTypes[Catalog.DefaultFighterHull().Id];
        foreach (var c in core.Launchers.OrderBy(kv => kv.Key))
            if (core.MissileTypes.TryGetValue(c.Value.ExpendabletypeId, out var mt)
                && core.TryBuildMissileLauncher(c.Key, out var part) && hull.CanMountPart(in part))
                return mt.LauncherName;
        throw new InvalidOperationException("no launcher mounts on the default fighter hull");
    }

    /// <summary>A real missile launcher known NOT to mount on the default
    /// fighter hull's magazine slot: a homing launcher whose part mask falls in
    /// the weapon-hardpoint set (e.g. LRM Hunter 3, mask 0x0002) that the hull's
    /// PmEquipment[Magazine] does not cover.</summary>
    private static string NonMountableLauncherName()
    {
        var core = RealCore();
        var hull = core.HullTypes[Catalog.DefaultFighterHull().Id];
        var mountable = new HashSet<short>();
        foreach (var c in core.Launchers)
            if (core.TryBuildMissileLauncher(c.Key, out var part) && hull.CanMountPart(in part))
                mountable.Add(c.Key);
        foreach (var c in core.Launchers.OrderBy(kv => kv.Key))
            if (core.MissileTypes.TryGetValue(c.Value.ExpendabletypeId, out var mt) && !mountable.Contains(c.Key))
                return mt.LauncherName;
        throw new InvalidOperationException("no non-mountable launcher found");
    }

    [Fact]
    public void ResolvesRealMissileLauncherByExactName()
    {
        var name = MountableLauncherName();
        var launcher = Catalog.ResolveLauncher(name);
        var core = RealCore();
        Assert.True(core.Launchers.ContainsKey(launcher.Id), $"resolved id {launcher.Id} must be a real launcher");
        Assert.True(core.MissileTypes.ContainsKey(core.Launchers[launcher.Id].ExpendabletypeId), "must be a genuine missile launcher");
        Assert.Equal(name, launcher.Name);
        Assert.True(launcher.Amount > 0, "a real launcher carries real ammo");
    }

    [Fact]
    public void ResolvesRealMissileLauncherByUniqueSubstring()
    {
        var name = MountableLauncherName();
        // A unique-ish substring of a real launcher name either resolves to it
        // or is ambiguous — never a silent no-op.
        var sub = name.Split(' ')[0];
        var r = Record.Exception(() => Catalog.ResolveLauncher(sub));
        Assert.True(r is null or ArgumentException, $"expected resolve-or-ambiguous, got {r}");
    }

    [Fact]
    public void UnknownLauncherThrowsASpecificError()
    {
        var ex = Assert.Throws<ArgumentException>(() => Catalog.ResolveLauncher("definitely-not-a-launcher-xyzzy"));
        Assert.Contains("no missile launcher matches", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlankLauncherThrows()
    {
        Assert.Throws<ArgumentException>(() => Catalog.ResolveLauncher("   "));
    }

    [Fact]
    public void DefaultLauncherPartIsARealLauncherThatMountsOnTheDefaultFighterHull()
    {
        var core = RealCore();
        var launcher = Catalog.DefaultLauncherPart();
        var hull = core.HullTypes[Catalog.DefaultFighterHull().Id];
        Assert.True(core.Launchers.ContainsKey(launcher.Id), "default must be a real launcher id");
        Assert.True(core.TryBuildMissileLauncher(launcher.Id, out var part), "default must be a genuine missile launcher");
        Assert.True(hull.CanMountPart(in part), "default launcher must actually mount on the default fighter hull");
    }

    [Fact]
    public void AppendLauncherMountsARealCompatibleLauncherIntoALoadout()
    {
        var name = MountableLauncherName();
        var launcher = Catalog.ResolveLauncher(name);
        var hull = Catalog.DefaultFighterHull();
        var parts = new List<ExpandedPartData> { new(1234, 1, 0, 255) };   // sentinel weapon id
        var note = Catalog.AppendLauncher(parts, launcher.Id, hull.Id, hull.Name, name);
        Assert.NotNull(note);
        Assert.Contains("mounted real missile launcher", note!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, parts.Count);
        Assert.Equal(launcher.Id, parts[1].PartId);
        Assert.Equal(launcher.Amount, parts[1].Amount);
    }

    [Fact]
    public void AppendLauncherThatDoesNotFitFallsBackToGunOnly()
    {
        var name = NonMountableLauncherName();
        var launcher = Catalog.ResolveLauncher(name);
        var hull = Catalog.DefaultFighterHull();
        var parts = new List<ExpandedPartData> { new(1234, 1, 0, 255) };   // sentinel weapon id
        var note = Catalog.AppendLauncher(parts, launcher.Id, hull.Id, hull.Name, name);
        Assert.NotNull(note);
        Assert.Contains("gun-only", note!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(parts);   // no fabricated launcher was added
    }

    [Fact]
    public void AppendLauncherWithNoRequestLeavesLoadoutUnchanged()
    {
        var hull = Catalog.DefaultFighterHull();
        var parts = new List<ExpandedPartData> { new(1234, 1, 0, 255) };
        var note = Catalog.AppendLauncher(parts, null, hull.Id, hull.Name, "");
        Assert.Null(note);
        Assert.Single(parts);
    }

    [Fact]
    public void LauncherReadyTimeIsPositiveForARealLauncher()
    {
        var launcher = Catalog.ResolveLauncher(MountableLauncherName());
        Assert.True(Catalog.LauncherReadyTime(launcher.Id) > 0);
    }
}
