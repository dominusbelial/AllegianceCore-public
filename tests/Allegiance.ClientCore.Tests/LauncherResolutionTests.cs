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
        Assert.Contains("no launcher matches", ex.Message, StringComparison.OrdinalIgnoreCase);
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

    // ── P435 — the three new expendable kinds (mine / chaff / probe) ────────

    /// <summary>Return the first real launcher whose built part satisfies
    /// <paramref name="isKind"/> (e.g. <c>t =&gt; t is DataPartTypeIGC.MineLauncher</c>),
    /// scanned over the real <c>core.Launchers</c> via the P435 generalized
    /// <see cref="StaticCore.TryBuildExpendableLauncher"/> bridge.</summary>
    private static (short PartId, string Name, short Amount) FirstDispenser(StaticCore core, Func<DataPartTypeIGC.Subtype, bool> isKind)
    {
        foreach (var (partId, l) in core.Launchers.OrderBy(kv => kv.Key))
            if (core.TryBuildExpendableLauncher(partId, out var part) && isKind(part.Type))
                return (partId, part.Name, l.Amount);
        throw new InvalidOperationException("no matching dispenser in the representative core");
    }

    /// <summary>Find a hull that MOUNTS <paramref name="part"/> (when one exists)
    /// and a hull that does NOT — real (fitting, non-fitting) hulls so the
    /// hull-compatibility gate has something real to exercise. Either may be null:
    /// some dispensers fit EVERY hull (then <c>NoFit</c> is null), and — a real
    /// data/approximation fact — chaff dispensers (part mask 0x0001/0x0002) fit NO
    /// hull under the port's <c>PmEquipment[ChaffLauncher]</c> gate (then
    /// <c>Fits</c> is null; chaff still mounts via the same-hull free-mount path,
    /// covered by the live test).</summary>
    private static (DataHullTypeIGC? Fits, DataHullTypeIGC? NoFit) FitPair(DataPartTypeIGC part)
    {
        var core = RealCore();
        DataHullTypeIGC? yes = null;
        foreach (var h in core.HullTypes.Values)
        {
            if (h.CanMountPart(in part)) { yes = h; break; }
        }
        DataHullTypeIGC? no = null;
        foreach (var h in core.HullTypes.Values)
            if (h.HullId != yes?.HullId && !h.CanMountPart(in part)) { no = h; break; }
        return (yes, no);
    }

    [Fact]
    public void ResolvesRealMineDispenserByExactName()
    {
        var core = RealCore();
        var (partId, name, _) = FirstDispenser(core, t => t is DataPartTypeIGC.MineLauncher);
        var l = Catalog.ResolveLauncher(name);
        Assert.Equal(Catalog.LauncherKind.Mine, l.Kind);
        Assert.Equal(partId, l.Id);
        Assert.True(core.Launchers.ContainsKey(l.Id), "resolved id must be a real launcher");
        Assert.True(core.MineTypes.ContainsKey(core.Launchers[l.Id].ExpendabletypeId), "must be a real mine dispenser");
    }

    [Fact]
    public void ResolvesRealChaffDispenserByExactName()
    {
        var core = RealCore();
        var (partId, name, _) = FirstDispenser(core, t => t is DataPartTypeIGC.ChaffLauncher);
        var l = Catalog.ResolveLauncher(name);
        Assert.Equal(Catalog.LauncherKind.Chaff, l.Kind);
        Assert.Equal(partId, l.Id);
        Assert.True(core.ChaffTypes.ContainsKey(core.Launchers[l.Id].ExpendabletypeId), "must be a real chaff dispenser");
    }

    [Fact]
    public void ResolvesRealProbeDispenserByExactName()
    {
        var core = RealCore();
        var (partId, name, _) = FirstDispenser(core, t => t is DataPartTypeIGC.ProbeLauncher);
        var l = Catalog.ResolveLauncher(name);
        Assert.Equal(Catalog.LauncherKind.Probe, l.Kind);
        Assert.Equal(partId, l.Id);
        Assert.True(core.ProbeTypes.ContainsKey(core.Launchers[l.Id].ExpendabletypeId), "must be a real probe dispenser");
    }

    /// <summary>⭐ P435 — resolution + hull-compatibility-gated mounting for a
    /// dispenser kind in isolation: resolving its real name yields a real partId
    /// with the right kind; <see cref="Catalog.AppendLauncher"/> MOUNTS it into a
    /// loadout when the hull fits (real ammo), and falls back to GUN-ONLY with a
    /// plain note when the hull does not (never a fabricated dispenser).</summary>
    [Theory]
    [InlineData(0)]   // mine
    [InlineData(1)]   // chaff
    [InlineData(2)]   // probe
    public void DispenserResolutionAndHullGatedMounting(int kindIdx)
    {
        var core = RealCore();
        Func<DataPartTypeIGC.Subtype, bool> isKind = kindIdx switch
        {
            0 => t => t is DataPartTypeIGC.MineLauncher,
            1 => t => t is DataPartTypeIGC.ChaffLauncher,
            _ => t => t is DataPartTypeIGC.ProbeLauncher,
        };
        var (partId, name, amount) = FirstDispenser(core, isKind);
        var l = Catalog.ResolveLauncher(name);
        Assert.Equal(partId, l.Id);
        Assert.True(core.TryBuildExpendableLauncher(partId, out var part), "the resolved id must build into a mountable dispenser part");

        var (fitHull, noFitHull) = FitPair(part);

        // Fits (when the port's CanMountPart gate admits one) -> mounted with real ammo.
        if (fitHull is { } fh)
        {
            var fitting = new List<ExpandedPartData> { new(1234, 1, 0, 255) };
            var fitNote = Catalog.AppendLauncher(fitting, l.Id, fh.HullId, fh.Name, name);
            Assert.NotNull(fitNote);
            Assert.Contains("mounted real", fitNote!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(l.Name, fitNote!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, fitting.Count);
            Assert.Equal(l.Id, fitting[1].PartId);
            Assert.Equal(l.Amount, fitting[1].Amount);
        }
        else
        {
            // With P555's native-alignment fix, chaff now mounts on most
            // socketed hulls, so a fit hull is normally found (FitPair above) and
            // this fallback is rare. When no hull fits, the honest outcome is a
            // gun-only loadout note, never a fabricated dispenser.
            var someHull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var nonFitting = new List<ExpandedPartData> { new(1234, 1, 0, 255) };
            var noFitNote = Catalog.AppendLauncher(nonFitting, l.Id, someHull.HullId, someHull.Name, name);
            Assert.NotNull(noFitNote);
            Assert.Contains("gun-only", noFitNote!, StringComparison.OrdinalIgnoreCase);
            Assert.Single(nonFitting);
        }

        // Does not fit (when such a hull exists) -> gun-only, no fabricated part.
        if (noFitHull is { } nh && fitHull is not null)
        {
            var nonFitting = new List<ExpandedPartData> { new(1234, 1, 0, 255) };
            var noFitNote = Catalog.AppendLauncher(nonFitting, l.Id, nh.HullId, nh.Name, name);
            Assert.NotNull(noFitNote);
            Assert.Contains("gun-only", noFitNote!, StringComparison.OrdinalIgnoreCase);
            Assert.Single(nonFitting);
        }
    }
}
