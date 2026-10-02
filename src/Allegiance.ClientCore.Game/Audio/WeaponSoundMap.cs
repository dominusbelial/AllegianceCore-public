using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P465 — the client's per-weapon sound map: resolves a weapon's
/// <c>SingleShotSound</c> / <c>BurstSound</c> id (the <c>SoundID</c> shorts each
/// weapon part decodes in <c>DataPartTypeIGC.Weapon</c>,
/// <c>src/Igc/igc.h:1841-1843</c>) to the actual wave to play.
///
/// <para><b>Why the ids resolve to waves this way.</b> A weapon part carries a
/// numeric <c>singleShotSound</c>/<c>burstSound</c>; native plays them via
/// <c>trekClient.StartSound(GetSingleShotSound(), …)</c> / the per-mount burst
/// loop (<c>WinTrek/trekigc.cpp:1005-1012</c>). Those numbers are ids into the
/// game's compiled soundbase. This checkout has no compiled soundbase (the
/// <c>…SoundId</c> constants of <c>sounddef.mdl</c>'s <c>soundList</c> are
/// build-time generated), so the id→wave table is <b>derived here</b> from the
/// real <c>Artwork/sounddef.mdl</c>: its <c>soundList</c> block maps an explicit
/// numeric id to a sound-template name —
/// <c>(531, dn_ptvulcan1ShotSound), (551, dn_ptlascan1ShotSound),
/// (628, faohhammer1ShotSound)</c> — and each template's definition references
/// its <c>ImportWave("…")</c> clip
/// (<c>dn_ptvulcan1ShotSound = GainSound(…, ImportWave("dn_ptvulcan1shoot"))</c>,
/// sounddef.mdl:130-163). Concatenating those two mappings yields id → wave.
/// (The brief's "map by model-name prefix" was <b>not</b> usable: PCore001
/// weapon parts carry generic shared models like <c>wep08</c>, not faction
/// names, so the part's model name carries no sound identity — the <c>soundList</c>
/// id resolution is the authoritative route.)</para>
///
/// <para><b>Coverage (real PCore001).</b> Of the 52 distinct <c>singleShotSound</c>
/// ids and 41 distinct <c>burstSound</c> ids used by the 82 real weapon parts,
/// 51 / 39 resolve to a real wave. The single unresolved single-shot id is 0 —
/// the non-firing Rocket Targeting aid (<c>singleShot=0</c>), which never fires
/// a shot, so its cue falls back. <see cref="FallbackStem"/> (<c>pulselaser</c>)
/// covers every unresolved id; only that one asset needs no per-weapon wave.
/// Every resolved wave is a bundled <c>Content/Sounds/*.ogg</c> (verified by
/// <c>WeaponSoundMapTests</c>).</para>
/// </summary>
public static class WeaponSoundMap
{
    /// <summary>The wave every unresolved weapon falls back to (the pre-P465
    /// single cue the port used for all weapons). Its asset is already loaded as
    /// the existing <c>_fireSound</c>.</summary>
    public const string FallbackStem = "pulselaser";

    /// <summary>Single-shot sound id → wave stem, derived from sounddef.mdl's
    /// <c>soundList</c> (id → template) + each template's <c>ImportWave</c>.</summary>
    public static readonly IReadOnlyDictionary<short, string> SingleShotStemBySoundId = new Dictionary<short, string>
    {
        [1] = "plasmarifle1",
        [2] = "machinegun1",
        [3] = "repairweapon",
        [4] = "plasmarifle2",
        [5] = "cannon2",
        [6] = "turretgun2",
        [7] = "minigun2",
        [8] = "turretgun1",
        [9] = "turretcannon1",
        [10] = "turretcannon3",
        [11] = "plasmarifle2",
        [12] = "plasmarifle3",
        [13] = "sniperlaser1",
        [14] = "sniperlaser2",
        [15] = "machinegun3",
        [16] = "machinegun2",
        [17] = "cannon1",
        [19] = "minigun1",
        [20] = "minigun3",
        [21] = "turretgun2",
        [22] = "turretgun3",
        [23] = "turretcannon2",
        [24] = "plasmarifle1",
        [25] = "plasmarifle3",
        [26] = "repairweapon",
        [27] = "turretcannon2",
        [29] = "plasmagat2",
        [30] = "plasmamini1",
        [31] = "plasmagat1",
        [32] = "plasmaac1",
        [34] = "ef42",
        [35] = "hvycannon",
        [36] = "basecannon",
        [37] = "sbcannon",
        [38] = "plasmamini2",
        [39] = "plasmamini3",
        [123] = "plasmagat3",
        [125] = "plasmaac2",
        [126] = "plasmaac3",
        [141] = "gauss",
        [143] = "pulselaser",
        [145] = "striker",
        [531] = "dn_ptvulcan1shoot",
        [534] = "dn_ptvulcan2shoot",
        [551] = "dn_ptlascan1shoot",
        [554] = "dn_ptlascan2shoot",
        [628] = "faohhammer1",
        [630] = "faohhammer2",
        [632] = "faohhammer3",
        [662] = "faohomega1",
        [664] = "faohomega2",
    };

    /// <summary>Burst sound id → wave stem (native's per-mount sustained-fire
    /// loop, trekigc.cpp:999-1004). Most burst waves reuse the single-shot clip
    /// (native wraps the same <c>ImportWave</c> in an <c>ASRSound</c> attack/
    /// release envelope); they are resolved separately because the ids live in a
    /// different numeric range.</summary>
    public static readonly IReadOnlyDictionary<short, string> BurstStemBySoundId = new Dictionary<short, string>
    {
        [41] = "plasmarifle1",
        [42] = "machinegun1",
        [44] = "plasmarifle2",
        [46] = "turretgun2",
        [47] = "minigun2",
        [48] = "turretgun1",
        [51] = "plasmarifle2",
        [52] = "plasmarifle3",
        [53] = "sniperlaser1",
        [54] = "sniperlaser2",
        [55] = "machinegun3",
        [56] = "machinegun2",
        [59] = "minigun1",
        [60] = "minigun3",
        [61] = "turretgun2",
        [62] = "turretgun3",
        [64] = "plasmarifle1",
        [65] = "plasmarifle3",
        [67] = "turretcannon2",
        [70] = "plasmamini1",
        [71] = "plasmagat1",
        [72] = "plasmaac1",
        [73] = "plasmamini2",
        [74] = "plasmamini3",
        [75] = "plasmagat2",
        [76] = "plasmagat3",
        [77] = "plasmaac2",
        [78] = "plasmaac3",
        [144] = "pulselaser",
        [146] = "striker",
        [532] = "dn_ptvulcan1shoot",
        [535] = "dn_ptvulcan2shoot",
        [552] = "dn_ptlascan1shoot",
        [555] = "dn_ptlascan2shoot",
        [629] = "faohhammer1",
        [631] = "faohhammer2",
        [633] = "faohhammer3",
        [663] = "faohomega1",
        [665] = "faohomega2",
    };

    /// <summary>Try to resolve a weapon's single-shot sound id to its wave stem.
    /// Returns false for an id not in the table (including the reserved 0 of the
    /// non-firing targeting aid); the caller falls back to <see cref="FallbackStem"/>.</summary>
    public static bool TryResolveSingleShot(short soundId, out string stem)
    {
        if (SingleShotStemBySoundId.TryGetValue(soundId, out var s))
        {
            stem = s!;
            return true;
        }
        stem = "";
        return false;
    }

    /// <summary>Like <see cref="TryResolveSingleShot"/> for the burst loop id.</summary>
    public static bool TryResolveBurst(short soundId, out string stem)
    {
        if (BurstStemBySoundId.TryGetValue(soundId, out var s))
        {
            stem = s!;
            return true;
        }
        stem = "";
        return false;
    }

    /// <summary>Resolve a single-shot id, falling back to <see cref="FallbackStem"/>
    /// when the id is unlisted — the guarantee every caller can rely on.</summary>
    public static string SingleShotStem(short soundId)
        => TryResolveSingleShot(soundId, out var stem) ? stem : FallbackStem;

    /// <summary>Resolve a burst id, falling back to <see cref="FallbackStem"/>.</summary>
    public static string BurstStem(short soundId)
        => TryResolveBurst(soundId, out var stem) ? stem : FallbackStem;
}
