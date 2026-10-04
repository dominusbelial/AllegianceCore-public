using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;

namespace Allegiance.Core.Content;

/// <summary>
/// P288 — maps a real hull (its native <c>ModelName</c>) + a ship's civilization
/// to one of the P287-imported Meshy ship models.
///
/// <para><b>Why civ+class, not 1:1 per HullId.</b> One native model name backs
/// many hull-stat variants (e.g. <c>fig05</c> backs "Scout"/"Heavy Scout"/
/// "Training Scout" and ~14 others in PCore001), so the faithful grouping is
/// per <em>role</em> (the <c>ModelName</c>-backed class) per civilization — not
/// per hull id. <see cref="ResolveAsset"/> therefore:
/// <list type="number">
///   <item>derives the hull's <em>role</em> from its real <c>ModelName</c>
///   (e.g. <c>fig05</c>→scout, <c>bom04</c>→bomber, <c>wc_gtint</c>→int),
///   grouping every hull-stat variant that shares the mesh;</item>
///   <item>renames the role to the civilization's class token (Bios calls the
///   fighter role "fighter", Dreghklar calls it "fig2", Ga'Taraan "fig",
///   etc. — exactly the manifest's per-civ vocabulary);</item>
///   <item>emits the P287 asset name <c>"&lt;civ&gt;-&lt;class&gt;"</c> iff that
///   (civ,class) pair actually exists in P287's MODELS_MANIFEST_P287.json
///   (so a class with no imported art resolves to null → fallback).</item>
/// </list>
/// The same generic native mesh is genuinely shared across factions (multiple
/// civs fly the same geometry, differing only in texture), so a single role
/// like <c>fig05</c>→scout legitimately backs <c>bios-scout</c>, <c>gt-scout</c>,
/// <c>rix-scout</c> … simultaneously.</para>
///
/// <para><b>The civilization key is resolved separately</b> by
/// <see cref="CivKeyForSide"/> from the client's real side roster
/// (<see cref="SideRoster"/> carries each side's real <c>CivilizationId</c>,
/// Server.cs:6214-6218), so the runtime never guesses a ship's civ — see the
/// P288 writeup.</para>
/// </summary>
public sealed class HullModelResolver
{
    /// <summary>Fallback asset used when a hull has no matching civ+class art.</summary>
    public const string FallbackAsset = "bios_scout_upscaled";

    /// <summary>P287's 3 imported missile content models.</summary>
    public static readonly string[] MissileAssets = { "mis05", "mis400", "artifact" };

    /// <summary>
    /// The 72 ship asset names from MODELS_MANIFEST_P287.json (every
    /// <c>kind=="ship"</c> entry's <c>asset</c>). Embedded so the resolver is
    /// headless-testable and never depends on Content being loaded; matches the
    /// manifest exactly (asset name == <c>"&lt;civ&gt;-&lt;class&gt;"</c>).
    /// </summary>
    public static readonly IReadOnlySet<string> ShipAssets = new HashSet<string>(
    [
        // belters
        "belters-bomber","belters-fig","belters-scout",
        // bios
        "bios-bomber","bios-carrier","bios-constructor","bios-corvette","bios-cruiser",
        "bios-destroyer","bios-devastator","bios-ejectpod","bios-fighter","bios-interceptor",
        "bios-miner","bios-stealthfighter","bios-trooptransport",
        // dreg
        "dreg-bbr2","dreg-carrier","dreg-corvette","dreg-fig2","dreg-frigate","dreg-scout2","dreg-stealthfighter","dreg-trooptransport",
        // giga
        "giga-bomber","giga-fighter","giga-interceptor","giga-scout",
        // gt
        "gt-ass","gt-bomber","gt-carrier","gt-con","gt-cruiser","gt-dev","gt-fighter","gt-hod",
        "gt-int","gt-miner","gt-mustang","gt-pod","gt-rd","gt-scout","gt-sf","gt-tt",
        // ic
        "ic-bomber","ic-carrier","ic-constructor","ic-corvette","ic-cruiser","ic-dev",
        "ic-devastator","ic-ejectpod","ic-fighter","ic-frigate","ic-int","ic-interceptor",
        "ic-miner","ic-scout","ic-stealthfighter","ic-trooptransport",
        // oh
        "oh-bomber","oh-carrier","oh-corvette","oh-cruiser","oh-devastator","oh-fighter","oh-int","oh-interceptor","oh-scout","oh-stealthfighter",
        // rix
        "rix-ass","rix-bomber","rix-carrier","rix-con","rix-cruiser","rix-fighter","rix-int",
        "rix-miner","rix-pod","rix-scout","rix-sf","rix-tt",
        // tf
        "tf-bomber","tf-carrier","tf-cruiser","tf-fig","tf-int","tf-scout","tf-stealthfighter","tf-trooptransport",
    ]);

    /// <summary>
    /// Real PCore001 <c>CivilizationId</c> → the manifest's civ key. Only the 10
    /// factions P287/planned civs have keys; Phoenix(4) was added in P510 so its
    /// hulls are no longer invisible to the resolver, but it still has no P287
    /// art yet (its phoenix-* slots come in P515) so it resolves to the fallback.
    /// Matches the 10 civilizations decoded from PCore001
    /// (Dreghklar=1, Technoflux=2, Ga'Taraan=3, Phoenix=4, Omicron=5,
    /// Gigacorp=18, Iron Coalition=25, Bios=35, Belters=101, Rixian=201).
    /// </summary>
    public static readonly IReadOnlyDictionary<short, string> CivKeyById = new Dictionary<short, string>
    {
        [1] = "dreg", [2] = "tf", [3] = "gt", [4] = "phoenix", [5] = "oh", [18] = "giga",
        [25] = "ic", [35] = "bios", [101] = "belters", [201] = "rix",
    };

    /// <summary>Map a side's real <c>CivilizationId</c> (from the side roster)
    /// to the manifest civ key, or null when that civ has no imported art.</summary>
    public static string? CivKeyByIdShort(short civId) =>
        CivKeyById.TryGetValue(civId, out var k) ? k : null;

    /// <summary>
    /// Resolve the P287 ship asset for a hull of the given civilization.
    /// Returns null when the hull's native <c>ModelName</c> has no recognised
    /// role, or that civ+class has no imported art — the caller then falls back
    /// to <see cref="FallbackAsset"/>.
    /// </summary>
    public string? ResolveAsset(string civKey, DataHullTypeIGC hull)
    {
        var role = RoleOf(hull.ModelName);
        if (role is null) return null;
        var cls = ClassToken(civKey, role);
        if (cls is null) return null;
        var asset = $"{civKey}-{cls}";
        return ShipAssets.Contains(asset) ? asset : null;
    }

    /// <summary>
    /// The civilization's class vocabulary: rename a ModelName role to the
    /// manifest's class token for that civ. Returns null when the civ does not
    /// recognise the role (→ no art → fallback).
    /// </summary>
    private static string? ClassToken(string civKey, string role)
    {
        // Per-civ role → class-token overrides (the manifest's per-faction
        // vocabulary). Defaults below cover the shared generic vocabulary.
        switch (civKey)
        {
            case "dreg":
                return role switch
                {
                    "bomber" => "bbr2", "fighter" => "fig2", "scout" => "scout2", _ => DefaultToken(role),
                };
            case "belters":
            case "tf":
                return role switch
                {
                    "fighter" => "fig", "bomber" => "bomber", "scout" => "scout",
                    "int" => "int", _ => DefaultToken(role),
                };
            case "gt":
                return role switch
                {
                    "con" => "con", "int" => "int", "tt" => "tt", "sf" => "sf",
                    "ass" => "ass", "dev" => "dev", "pod" => "pod",
                    _ => DefaultToken(role),
                };
            case "rix":
                return role switch
                {
                    "con" => "con", "int" => "int", "tt" => "tt", "sf" => "sf",
                    "ass" => "ass", "pod" => "pod",
                    _ => DefaultToken(role),
                };
            case "ic":
                return role switch
                {
                    "int" => "int", "interceptor" => "interceptor",
                    "dev" => "dev", "devastator" => "devastator",
                    _ => DefaultToken(role),
                };
            case "oh":
                // Standard interceptor (faohshint) gets its own slot; the
                // heavy interceptor (faohshhint) is "interceptor".
                return role switch
                {
                    "int" => "int",
                    _ => DefaultToken(role),
                };
            default:
                return DefaultToken(role);
        }
    }

    private static string? DefaultToken(string role) => role switch
    {
        "pod" => "ejectpod",
        "con" => "constructor",
        "int" => "interceptor",
        "tt" => "trooptransport",
        "scout" => "scout",
        "miner" => "miner",
        "fighter" => "fighter",
        "bomber" => "bomber",
        "ass" => "corvette",
        "cruiser" => "cruiser",
        "destroyer" => "destroyer",
        "devastator" => "devastator",
        "frigate" => "frigate",
        "carrier" => "carrier",
        "mustang" => "mustang",
        "hod" => "hod",
        "rd" => "rd",
        "sf" => "stealthfighter",
        "dev" => "dev",
        "interceptor" => "interceptor",
        _ => null,
    };

    /// <summary>
    /// Derive the hull's role from its native <c>ModelName</c> — the
    /// <em>ModelName-backed grouping</em> the task scopes this to. Each distinct
    /// stem (e.g. <c>fig05</c>) identifies a real mesh family; the branch maps
    /// those stems to canonical roles. Unrecognised/utility/drone model names
    /// return null → fallback. Derived from the real PCore001 hull census.
    /// </summary>
    public static string? RoleOf(string modelName)
    {
        var mn = (modelName ?? "").Trim();
        if (mn.Length == 0) return null;

        if (mn.StartsWith("wc_", StringComparison.Ordinal))
        {
            var b = mn.Substring(3);
            if (b.EndsWith("stang", StringComparison.Ordinal)) return "mustang";
            if (b == "icint") return "int";                                  // IC standard Interceptor
            if (b is "ichvy" or "gchvy" or "gchvyx") return "interceptor";
            if (b.Contains("adv") || b.Contains("fig")) return "fighter";
            if (b.Contains("con")) return "con";
            if (b.Contains("mnr")) return "miner";
            if (b.Contains("pod")) return "pod";
            if (b.Contains("pt") || b.Contains("bmb")) return "bomber";
            if (b.Contains("sct")) return "scout";
            if (b.Contains("sf")) return "sf";
            if (b.Contains("hvy") || b.Contains("int")) return "interceptor";
            if (b.Contains("car")) return "carrier";
            if (b.Contains("crs")) return "cruiser";
            return "fighter";
        }

        if (mn.StartsWith("dn_", StringComparison.Ordinal))
        {
            var b = mn.Substring(3);
            if (b.Contains("con")) return "con";
            if (b.Contains("pod")) return "pod";
            if (b.Contains("bmbs")) return "sf";
            if (b.Contains("bmb")) return "bomber";
            if (b.Contains("car")) return "carrier";
            if (b.Contains("crs")) return "cruiser";
            if (b.Contains("dst")) return "ass";
            if (b.Contains("fig")) return "fighter";
            if (b.Contains("frg")) return "frigate";
            if (b.Contains("int")) return "int";
            if (b.Contains("mnr")) return "miner";
            if (b.Contains("sct")) return "scout";
            if (b.Contains("stl")) return "sf";
            if (b.Contains("vng")) return "fighter";
            if (b.Contains("rpb")) return "rd";
            return "fighter";
        }

        if (mn.StartsWith("tf", StringComparison.Ordinal))
        {
            if (mn.Contains("con")) return "con";
            if (mn.Contains("fig")) return "fighter";
            if (mn.Contains("mnr")) return "miner";
            if (mn.Contains("pod")) return "pod";
            if (mn.Contains("sb")) return "sf";
            if (mn.Contains("transport")) return "tt";
            if (mn.Contains("flagship")) return "cruiser";
            return null;
        }

        if (mn.StartsWith("faohsh", StringComparison.Ordinal))
        {
            var b = mn.Substring(6);
            if (b.Contains("pod")) return "pod";
            if (b.Contains("con")) return "con";
            if (b.Contains("bmbs")) return "sf";
            if (b.Contains("bmb")) return "bomber";
            if (b.Contains("car")) return "carrier";
            if (b.Contains("crs")) return "cruiser";
            if (b.Contains("crv")) return "ass";
            if (b.Contains("dev")) return "devastator";
            if (b.Contains("fig")) return "fighter";
            if (b.Contains("hint")) return "interceptor";
            if (b.Contains("int")) return "int";
            if (b.Contains("mnr")) return "miner";
            if (b.Contains("sct")) return "scout";
            if (b.Contains("stl")) return "sf";
            if (b.Contains("ass")) return "ass";
            return null;
        }

        if (mn.StartsWith("dreg", StringComparison.Ordinal))
        {
            if (mn.Contains("pod")) return "pod";
            if (mn.Contains("tt")) return "tt";
            if (mn.Contains("car")) return "carrier";
            if (mn.Contains("as")) return "ass";
            if (mn.Contains("mf")) return "frigate";
            return null;
        }
        if (mn.StartsWith("drg", StringComparison.Ordinal))
        {
            if (mn.Contains("as")) return "ass";
            if (mn.Contains("mf")) return "frigate";
            return null;
        }
        if (mn == "dminr") return "miner";
        if (mn == "dcrus") return "destroyer";
        if (mn == "crus2") return "cruiser";
        if (mn == "tecdev") return "dev";

        if (mn.StartsWith("quiz", StringComparison.Ordinal))
        {
            if (mn.Contains("bbr")) return "bomber";
            if (mn.Contains("fig")) return "fighter";
            if (mn.Contains("int")) return "int";
            if (mn.Contains("scout")) return "scout";
            if (mn.Contains("sf")) return "sf";
        }
        if (mn.StartsWith("rain", StringComparison.Ordinal))
        {
            if (mn.Contains("carr")) return "carrier";
            if (mn.Contains("fig")) return "fighter";
            if (mn.Contains("int")) return "int";
            if (mn.Contains("scout")) return "scout";
            if (mn.Contains("sf")) return "sf";
        }

        if (mn.StartsWith("bom", StringComparison.Ordinal))
        {
            if (mn is "bom01b" or "bom03") return "sf";
            return "bomber";
        }
        if (mn.StartsWith("scout", StringComparison.Ordinal)) return "scout";
        if (mn.StartsWith("int", StringComparison.Ordinal)) return "int";

        if (mn.StartsWith("fig", StringComparison.Ordinal))
        {
            var digits = new string(mn.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            var n = digits.Length > 0 && int.TryParse(digits, out var d) ? d : 0;
            if (n is 5 or 13 or 15 or 21 or 31) return "scout";
            if (n is 3 or 22 or 34) return "int";
            if (n is 17 or 23 or 25 or 32) return "sf";
            if (n == 12) return "rd";
            return "fighter";
        }

        if (mn.StartsWith("cap", StringComparison.Ordinal))
        {
            var digits = new string(mn.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            var n = digits.Length > 0 && int.TryParse(digits, out var d) ? d : 0;
            if (n is 4 or 21 or 103 or 303 or 400) return "ass";
            if (n is 401 or 500 or 501 or 502 or 503 or 504) return "carrier";
            if (n is 1 or 3 or 8 or 102 or 302 or 403) return "cruiser";
            if (n is 6 or 20 or 101 or 300 or 404) return "destroyer";
            if (n is 9 or 405) return "devastator";
            if (n == 100) return "dev";
            if (n is 2 or 7 or 102 or 301 or 406) return "frigate";
            if (n == 415) return "hod";
            return "cruiser";
        }

        if (mn.StartsWith("utl", StringComparison.Ordinal))
        {
            var digits = new string(mn.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            var n = digits.Length > 0 && int.TryParse(digits, out var d) ? d : 0;
            if (n is 100 or 203 or 22 or 94) return "pod";
            if (n is 101 or 19 or 90 or 91 or 200) return "miner";
            if (n is 102 or 11 or 14 or 95 or 201) return "con";
            if (n is 103 or 202 or 27 or 400 or 92 or 93) return "tt";
            if (n == 17) return "devastator";
            if (n == 18) return "ass";
            return null;
        }

        return null;
    }

    /// <summary>
    /// Enumerate every P287 ship asset reachable from a real hull set under the
    /// real shared-model semantics (a hull of role R covers <c>(civ, class)</c>
    /// for every civ whose class-token for R is in the manifest, because the
    /// underlying native mesh is shared across those factions). This is the
    /// programmatic proof that nothing P287 imported goes unused.
    /// </summary>
    public static IReadOnlyDictionary<string, (string Civ, string Class)> ReachableAssets(
        IEnumerable<DataHullTypeIGC> hulls)
    {
        var result = new Dictionary<string, (string, string)>();
        foreach (var civ in CivKeyById.Values.Distinct())
        {
            foreach (var h in hulls)
            {
                var asset = new HullModelResolver().ResolveAsset(civ, h);
                if (asset is not null)
                    result.TryAdd(asset, (civ, ClassFor(civ, h) ?? ""));
            }
        }
        return result;
    }

    private static string? ClassFor(string civ, DataHullTypeIGC hull)
    {
        var role = RoleOf(hull.ModelName);
        return role is null ? null : ClassToken(civ, role);
    }
}
