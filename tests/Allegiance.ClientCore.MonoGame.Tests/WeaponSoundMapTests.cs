using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P465 — the client's per-weapon sound map
/// (<see cref="WeaponSoundMap.SingleShotStemBySoundId"/> /
/// <see cref="WeaponSoundMap.BurstStemBySoundId"/>, derived from
/// <c>Artwork/sounddef.mdl</c>'s <c>soundList</c> id→template→<c>ImportWave</c>
/// resolution) is proven against the REAL PCore001 weapon parts: every weapon's
/// <c>SingleShotSound</c>/<c>BurstSound</c> id a real part carries resolves to
/// the correct wave (or, for the documented non-firing targeting aid, falls back
/// to <c>pulselaser</c>), three real weapons resolve to distinct correct waves,
/// and an unknown id falls back. Pure — no graphics device needed.
/// </summary>
public class WeaponSoundMapTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        return StaticCore.Load(file);
    }

    private static string RepoContentSoundsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "Allegiance.ClientCore.MonoGame")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new InvalidOperationException("cannot locate AllegianceCore repo root");
        return Path.Combine(dir, "src", "Allegiance.ClientCore.MonoGame", "Content", "Sounds");
    }

    private static System.Collections.Generic.IEnumerable<DataPartTypeIGC> Weapons()
        => Core().PartTypes.Values.Where(p => p.Type is DataPartTypeIGC.Weapon);

    /// <summary>⭐ Every real PCore001 weapon part's SingleShotSound id resolves
    /// to a wave — the coverage contract. The only tolerated miss is the
    /// non-firing Rocket Targeting weapon whose singleShotSound is the reserved
    /// 0 (it never fires a shot); that one falls back to pulselaser.</summary>
    [Fact]
    public void Every_real_weapon_singleShot_id_resolves_or_is_the_documented_noise_floor()
    {
        var weapons = Weapons().ToList();
        Assert.NotEmpty(weapons);

        foreach (var p in weapons)
        {
            var w = (DataPartTypeIGC.Weapon)p.Type;
            if (w.SingleShotSound == 0)      // the non-firing targeting aid (no shot cue)
            {
                Assert.Equal(WeaponSoundMap.FallbackStem, WeaponSoundMap.SingleShotStem(w.SingleShotSound));
                continue;
            }
            Assert.True(WeaponSoundMap.TryResolveSingleShot(w.SingleShotSound, out var stem),
                $"weapon {p.Name} (part {p.PartId}) singleShot {w.SingleShotSound} is not in the P465 single-shot table");
            Assert.False(string.IsNullOrEmpty(stem));
        }
    }

    /// <summary>⭐ Three real PCore001 weapons resolve to three DISTINCT correct
    /// waves: PE Vul Can 1 (id 531 → dn_ptvulcan1shoot), EW Lsr Can 1
    /// (id 551 → dn_ptlascan1shoot), PW A-B Hammer 1 (id 628 → faohhammer1).
    /// This is the brief's "at least three real weapons → distinct correct waves".</summary>
    [Fact]
    public void Three_real_weapons_resolve_to_distinct_correct_waves()
    {
        var core = Core();
        var weapons = core.PartTypes.Values
            .Where(p => p.Type is DataPartTypeIGC.Weapon w && w.SingleShotSound != 0)
            .ToList();

        // Locate the three real weapon parts by their decoded single-shot ids.
        DataPartTypeIGC? VulCan = null, LsrCan = null, Hammer = null;
        foreach (var p in weapons)
        {
            var id = ((DataPartTypeIGC.Weapon)p.Type).SingleShotSound;
            if (id == 531) VulCan = p;
            else if (id == 551) LsrCan = p;
            else if (id == 628) Hammer = p;
        }
        Assert.NotNull(VulCan);
        Assert.NotNull(LsrCan);
        Assert.NotNull(Hammer);

        string vulcan = WeaponSoundMap.SingleShotStem(((DataPartTypeIGC.Weapon)VulCan!.Value.Type).SingleShotSound);
        string lascan = WeaponSoundMap.SingleShotStem(((DataPartTypeIGC.Weapon)LsrCan!.Value.Type).SingleShotSound);
        string hammer = WeaponSoundMap.SingleShotStem(((DataPartTypeIGC.Weapon)Hammer!.Value.Type).SingleShotSound);

        Assert.Equal("dn_ptvulcan1shoot", vulcan);
        Assert.Equal("dn_ptlascan1shoot", lascan);
        Assert.Equal("faohhammer1", hammer);

        // Distinct — the whole point of the per-weapon map.
        Assert.Equal(3, new[] { vulcan, lascan, hammer }.Distinct().Count());
    }

    /// <summary>⭐ An unknown / unlisted weapon id falls back to pulselaser and
    /// does NOT resolve (TryResolve false).</summary>
    [Fact]
    public void Unknown_weapon_id_falls_back_to_pulselaser()
    {
        Assert.False(WeaponSoundMap.TryResolveSingleShot(short.MaxValue, out _));
        Assert.Equal(WeaponSoundMap.FallbackStem, WeaponSoundMap.SingleShotStem(short.MaxValue));
        Assert.Equal(WeaponSoundMap.FallbackStem, WeaponSoundMap.BurstStem(short.MaxValue));
    }

    /// <summary>⭐ Every stem the single-shot table references is a bundled .ogg in
    /// Content/Sounds — nothing the client would try to play is missing from the
    /// content pipeline (proving the P465 bundling step).</summary>
    [Fact]
    public void Every_singleShot_table_stem_is_a_bundled_asset()
    {
        string soundsDir = RepoContentSoundsDir();
        foreach (var stem in WeaponSoundMap.SingleShotStemBySoundId.Values.Distinct())
            Assert.True(File.Exists(Path.Combine(soundsDir, stem + ".ogg")),
                $"P465 weapon wave {stem}.ogg is not bundled in Content/Sounds");
    }
}
