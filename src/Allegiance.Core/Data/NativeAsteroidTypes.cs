namespace Allegiance.Core;

using Allegiance.Core.MapGen;

/// <summary>
/// The native fixed asteroid-type table and its selection rules — one shared
/// home for every asteroid the port generates (the procedural map generator,
/// the research-showcase rocks, and any future creation site), so model /
/// icon / hit-points / radius / name-prefix all come from the same
/// authoritative table instead of invented names like <c>"rock_mine"</c> /
/// <c>"rock_build"</c>.
/// </summary>
/// <remarks>
/// <para><b>Source of truth.</b> <c>asteroidTypes[]</c>,
/// <c>~/Development/Allegiance/src/Igc/asteroidigc.cpp:126-137</c>, each row an
/// <c>AsteroidTypeRow { name, prefix, AsteroidDef }</c> where <c>AsteroidDef</c>
/// is <c>{ ore, oreMax, aabmCapabilities, asteroidID, hitpoints, radius,
/// modelName, textureName, iconName }</c> (<c>igc.h:1709-1720</c>). The nine
/// rows below are transcribed verbatim; the index ranges
/// (<c>nFirstHugeType .. numAsteroidTypes</c>) mirror asteroidigc.cpp:140-148.</para>
///
/// <para><b>Selection.</b> <see cref="GetRandomType"/> is the faithful port of
/// <c>IasteroidIGC::GetRandomType</c> (asteroidigc.cpp:231-255): it picks the
/// huge pair for <c>aabm == 0</c>, the generic/buildable pair for
/// <c>c_aabmBuildable</c>, and the minable/helium pair for <c>c_aabmMineHe3</c>
/// (native's <c>randomInt(0,1)</c> inclusive → the port's
/// <c>Random.Shared.Next(0,2)</c>). The special subtypes
/// (<c>c_aabmSpecial &lt;&lt; 0/1/2</c> = Uranium/Silicon/Carbon) are placed by
/// <c>IasteroidIGC::GetSpecialAsterioid</c> (asteroidigc.cpp:190-229) in native;
/// <see cref="ResolveTypeIndex"/> folds that in by exact-matching a special
/// capability mask to its table row. Native <c>CreateAsteroid</c>
/// (<c>~/Development/Allegiance/src/Igc/common.cpp:2844-2893</c>) then pulls
/// every field from <c>IasteroidIGC::GetTypeDefaults(type)</c>
/// (asteroidigc.cpp:174-177) — exactly what <see cref="GetTypeDefaults"/>
/// exposes here.</para>
/// </remarks>
public static class NativeAsteroidTypes
{
    /// <summary>One transcribed <c>AsteroidDef</c> row (igc.h:1709-1720) +
    /// its display name and name prefix. Field order matches the C++ struct.</summary>
    public readonly record struct Row(
        string Name,          // asteroidTypes[i].name ("asteroid"/"Helium 3"/"Uranium"/...)
        string Prefix,        // asteroidTypes[i].prefix ("\0a"/"He"/"U"/"Si"/"C")
        float Ore,            // AsteroidDef.ore
        float OreMax,         // AsteroidDef.oreMax
        short Capabilities,   // AsteroidDef.aabmCapabilities
        short AsteroidId,     // AsteroidDef.asteroidID
        float HitPoints,      // AsteroidDef.hitpoints
        short Radius,         // AsteroidDef.radius
        string ModelName,     // AsteroidDef.modelName
        string TextureName,   // AsteroidDef.textureName
        string IconName);     // AsteroidDef.iconName

    // Transcribed from asteroidTypes[] (asteroidigc.cpp:126-137). Values are
    // verbatim: hp/radius are the native per-type values, the ore column is
    // non-zero only for the helium rows (1.0/1.0), and every row carries the
    // real native model + radar-icon names (bgrndNN / *rock icon), NOT invented
    // placeholders. The "\0a" huge/buildable prefix renders as an empty display
    // name natively (strlen stops at the leading NUL); see AsteroidName.
    private static readonly Row[] Table =
    [
        new("asteroid",     "\0a", 0.0f, 0.0f, 0,                         0, 25000, 400, "bgrnd03", "", "meteoricon"), // asteroidigc.cpp:128
        new("asteroid",     "\0a", 0.0f, 0.0f, 0,                         0, 25000, 400, "bgrnd05", "", "meteoricon"), // asteroidigc.cpp:129
        new("asteroid",     "\0a", 0.0f, 0.0f, DataAsteroidIGC.AabmBuildable, 0, 10000, 200, "bgrnd03", "", "meteoricon"), // asteroidigc.cpp:130 (buildable)
        new("asteroid",     "\0a", 0.0f, 0.0f, DataAsteroidIGC.AabmBuildable, 0, 10000, 200, "bgrnd05", "", "meteoricon"), // asteroidigc.cpp:131 (buildable)
        new("Helium 3",     "He", 1.0f, 1.0f, DataAsteroidIGC.AabmMineHe3, 0, 25000, 100, "bgrnd55", "", "heliumrock"), // asteroidigc.cpp:132
        new("Helium 3",     "He", 1.0f, 1.0f, DataAsteroidIGC.AabmMineHe3, 0, 25000, 100, "bgrnd56", "", "heliumrock"), // asteroidigc.cpp:133
        new("Uranium",      "U",  0.0f, 0.0f, (short)(DataAsteroidIGC.AabmSpecial << 0), 0, 25000, 200, "bgrnd51", "", "hotrock"),    // asteroidigc.cpp:134
        new("Silicon",      "Si", 0.0f, 0.0f, (short)(DataAsteroidIGC.AabmSpecial << 1), 0, 25000, 200, "bgrnd52", "", "copperrock"), // asteroidigc.cpp:135
        new("Carbonaceous", "C",  0.0f, 0.0f, (short)(DataAsteroidIGC.AabmSpecial << 2), 0, 25000, 200, "bgrnd53", "", "carbonrock"), // asteroidigc.cpp:136
    ];

    // Index ranges (asteroidigc.cpp:140-148).
    private const int FirstHugeType = 0;
    private const int NumHugeTypes = 2;
    private const int FirstGenericType = FirstHugeType + NumHugeTypes;
    private const int NumGenericTypes = 2;
    private const int FirstMinableType = FirstGenericType + NumGenericTypes;
    private const int NumMinableTypes = 2;
    private const int FirstSpecialType = FirstMinableType + NumMinableTypes;
    private const int NumSpecialTypes = 3;

    /// <summary>Total row count: <c>numAsteroidTypes = sizeof(asteroidTypes) /
    /// sizeof(AsteroidTypeRow)</c> (asteroidigc.cpp:148).</summary>
    public const int Count = 9;

    /// <summary>
    /// <c>IasteroidIGC::GetTypeDefaults(index)</c> (asteroidigc.cpp:174-177) —
    /// the row's <c>AsteroidDef</c>. Bounds-checked (throws out of range rather
    /// than reading garbage, matching native's assert-plus-return-first-row
    /// hardening with a loud failure).
    /// </summary>
    public static Row GetTypeDefaults(int index) =>
        index >= 0 && index < Count ? Table[index]
        : throw new ArgumentOutOfRangeException(nameof(index), index, $"asteroid type index {index} out of range [0,{Count})");

    /// <summary><c>IasteroidIGC::GetTypePrefix(index)</c> (asteroidigc.cpp:169-172).</summary>
    public static string GetTypePrefix(int index) => GetTypeDefaults(index).Prefix;

    /// <summary>
    /// <c>IasteroidIGC::GetRandomType(aabm)</c> (asteroidigc.cpp:231-255): a
    /// random type index for the three callable capability masks — <c>0</c>
    /// (huge pair), <c>c_aabmBuildable</c> (generic pair), <c>c_aabmMineHe3</c>
    /// (minable pair). Native <c>randomInt(0,1)</c> is inclusive of both bounds
    /// (zmath.h:125-127), so the port draws <c>Next(0,2)</c>. A capability mask
    /// outside those three (native's <c>default: assert(false)</c>) is not a
    /// GetRandomType case; use <see cref="ResolveTypeIndex"/> for those.
    /// </summary>
    public static int GetRandomType(short aabm, IMapRandom? rng = null)
    {
        // P811 — an injected rng (the map generator's seeded stream) makes this
        // coin flip reproducible with the rest of the map; null keeps the
        // ambient shared RNG for the other callers (native draws randomInt(0,1)
        // from the run's RNG). SharedMapRandom delegates to Random.Shared, so
        // the null/default path is bit-for-bit the historic behaviour.
        var r = rng ?? SharedMapRandom.Instance;
        return aabm switch
        {
            0 => FirstHugeType + r.Next(0, 2),
            DataAsteroidIGC.AabmBuildable => FirstGenericType + r.Next(0, 2),
            DataAsteroidIGC.AabmMineHe3 => FirstMinableType + r.Next(0, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(aabm), aabm,
                "GetRandomType only handles aabm 0 / c_aabmBuildable / c_aabmMineHe3 (asteroidigc.cpp:235-252)"),
        };
    }

    /// <summary>
    /// The caller-facing selector the map generator and showcase use: the type
    /// index for an arbitrary asteroid capability mask. Routes the three
    /// GetRandomType kinds (0 / buildable / mineHe3) through
    /// <see cref="GetRandomType"/>, and resolves the special subtype masks
    /// (<c>c_aabmSpecial &lt;&lt; 0/1/2</c>) to their exact table row — the port
    /// of how native's <c>PopulateCluster</c> mixes <c>GetRandomType</c>
    /// (common.cpp:3036,3075) with <c>GetSpecialAsterioid</c> (common.cpp:3057,
    /// which yields <c>nFirstSpecialType + 0/1/2</c>, asteroidigc.cpp:228). An
    /// unrecognised mask degrades to the first (huge) row, mirroring native
    /// <c>FindAsteroidRow</c>'s assert-then-return-<c>asteroidTypes[0]</c>
    /// fallback (asteroidigc.cpp:160-161) without crashing the map.
    /// </summary>
    public static int ResolveTypeIndex(short aabm, IMapRandom? rng = null)
    {
        if (aabm is 0 or DataAsteroidIGC.AabmBuildable or DataAsteroidIGC.AabmMineHe3)
            return GetRandomType(aabm, rng);
        for (int i = FirstSpecialType; i < FirstSpecialType + NumSpecialTypes; i++)
            if (Table[i].Capabilities == aabm) return i;
        return FirstHugeType;   // native FindAsteroidRow fallback (asteroidigc.cpp:161)
    }

    /// <summary>
    /// The native asteroid display name: <c>GetAsteroidName</c>
    /// (<c>common.cpp:2786-2801</c>) = <c>prefix + (1024 + ((id+1093)·5237) %
    /// 8713)</c>, where <c>id</c> is the asteroid's <c>AsteroidID</c>. The
    /// huge/buildable rows carry the <c>"\0a"</c> prefix whose leading NUL
    /// truncates the C string to empty natively; the port treats a leading-NUL
    /// prefix as empty (the native effective name) and renders the bare number,
    /// so a generated rock never has a blank name, while
    /// Helium/Uranium/Silicon/Carbon carry their real native prefixes
    /// ("He…"/"U…"/"Si…"/"C…").
    /// </summary>
    public static string AsteroidName(int typeIndex, short asteroidId)
    {
        string rawPrefix = GetTypePrefix(typeIndex);
        string prefix = rawPrefix.Length > 0 && rawPrefix[0] == '\0' ? "" : rawPrefix;
        int number = 1024 + ((asteroidId + 1093) * 5237) % 8713;
        return prefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
