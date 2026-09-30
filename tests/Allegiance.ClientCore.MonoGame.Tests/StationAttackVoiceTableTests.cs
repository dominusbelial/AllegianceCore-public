using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P451 — the client id→asset-stem table
/// (<see cref="SoundManager.StationAttackVoices"/> /
/// <see cref="SoundManager.TryGetStationAttackStem"/>) is proven against the REAL
/// PCore001 station types: every <c>UnderAttackSound</c> and <c>CriticalSound</c>
/// id any real station type decodes is covered by the table, the family→clip
/// mapping is semantically correct (garrison→vo_attack_garrison etc.), every
/// critical id maps to <c>vo_critical_station</c>, and every stem the table
/// references is a bundled asset in <c>Content/Sounds</c>. Pure — no graphics
/// device needed (the lookup and table are static).
/// </summary>
public class StationAttackVoiceTableTests
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

    /// <summary>⭐ Every real PCore001 station type's UnderAttackSound and
    /// CriticalSound ids resolve through the table, and every critical id maps to
    /// vo_critical_station. This is the coverage contract: the server's defending-
    /// team alert sends exactly these ids (FedSrv.CPP:12202-12210), so an uncovered
    /// id means the client would stay silent for that base.</summary>
    [Fact]
    public void Every_real_station_sound_id_resolves_and_criticals_map_to_vo_critical_station()
    {
        var core = Core();
        Assert.NotEmpty(core.StationTypes);

        foreach (var st in core.StationTypes.Values)
        {
            bool uaOk = SoundManager.TryGetStationAttackStem(st.UnderAttackSound, out var uaStem);
            bool crOk = SoundManager.TryGetStationAttackStem(st.CriticalSound, out var crStem);
            Assert.True(uaOk, $"station type {st.StationTypeId} UnderAttackSound {st.UnderAttackSound} is not in the P451 table");
            Assert.True(crOk, $"station type {st.StationTypeId} CriticalSound {st.CriticalSound} is not in the P451 table");
            Assert.False(string.IsNullOrEmpty(uaStem));
            Assert.False(string.IsNullOrEmpty(crStem));
            Assert.Equal("vo_critical_station", crStem);
        }
    }

    /// <summary>⭐ The id→family clip mapping is semantically correct: the garrison
    /// id is vo_attack_garrison, outpost → vo_attack_outpost, shipyard →
    /// vo_attack_shipyard, teleport → vo_attack_teleport, refinery → vo_attack_refinery,
    /// expansion → vo_attack_expansion, supremecy → vo_attack_supremecy, tactical →
    /// vo_attack_tactical, palisade → vo_attack_palisade, research → vo_attack_research.
    /// (Matches sounddef.mdl's <family>AttackedSound ImportWave stems.)</summary>
    [Theory]
    [InlineData(820, "vo_attack_garrison")]
    [InlineData(821, "vo_attack_outpost")]
    [InlineData(822, "vo_attack_shipyard")]
    [InlineData(823, "vo_attack_teleport")]
    [InlineData(824, "vo_attack_refinery")]
    [InlineData(825, "vo_attack_expansion")]
    [InlineData(826, "vo_attack_supremecy")]
    [InlineData(827, "vo_attack_tactical")]
    [InlineData(828, "vo_attack_mine")]
    [InlineData(829, "vo_attack_palisade")]
    [InlineData(830, "vo_attack_research")]
    [InlineData(831, "dn_vo_attack_transeiver")]
    public void Under_attack_ids_map_to_their_family_clip(short id, string expectedStem)
    {
        Assert.True(SoundManager.TryGetStationAttackStem(id, out var stem));
        Assert.Equal(expectedStem, stem);
    }

    /// <summary>⭐ Every stem the table references is a bundled .ogg in
    /// Content/Sounds — nothing the client would try to play is missing from the
    /// content pipeline (proving the P451 bundling step).</summary>
    [Fact]
    public void Every_table_stem_is_a_bundled_asset()
    {
        string soundsDir = RepoContentSoundsDir();
        foreach (var stem in SoundManager.StationAttackVoices.Values.Distinct())
            Assert.True(File.Exists(Path.Combine(soundsDir, stem + ".ogg")),
                $"P451 voice asset {stem}.ogg is not bundled in Content/Sounds");
    }
}
