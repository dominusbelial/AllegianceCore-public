using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Content;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P565 — the hull list the offline inspector walks. Pure enumeration over a real
/// <see cref="ShopCatalog"/> (the static core the server boots) with:
/// <list type="bullet">
///   <item><b>flyable filter</b> — native lists a hangar hull only when its signed
///   <c>GroupId</c> is non-negative (load.cpp:1603-1606, clintlib.h:1858); drone /
///   utility hulls (constructors, miners, lifepods, recon) carry <c>GroupId = -1</c>
///   (raw 0xFF, see the P470 doc) and are hidden behind a toggle;</item>
///   <item><b>civ grouping</b> — each hull is assigned the first civilization whose
///   art vocabulary recognises its role (<see cref="HullModelResolver.ResolveAsset"/>),
///   so the inspector can walk a fleet per civ;</item>
///   <item><b>fallback flag</b> — whether a hull has NO usable art (native <c>.mdl</c>
///   undecodable AND no Meshy asset) and would render the generic placeholder.</item>
/// </list>
/// Everything is a pure <see cref="InspectorHull"/> record, GPU-free and headless-testable
/// against the real PCore001 the way <see cref="HullModelResolverTests"/> does.
/// </summary>
public static class InspectorHullCatalog
{
    /// <summary>A single reviewable hull in the inspector's walk order.</summary>
    public sealed record InspectorHull(
        short HullId,
        string Name,
        string ModelName,
        string? TextureName,
        float Length,
        bool IsDrone,
        string? CivKey,
        string? MeshyAsset,
        bool NativeAvailable,
        bool FallbackUsed)
    {
        /// <summary>Short, stable label for screenshots + notes (sanitised of slashes).</summary>
        public string Slug => $"{HullId}_{Sanitise(Name)}";
        private static string Sanitise(string s)
        {
            var c = new char[s.Length];
            for (int i = 0; i < s.Length; i++) c[i] = char.IsLetterOrDigit(s[i]) ? s[i] : '_';
            return new string(c);
        }
    }

    /// <summary>The primary civ for a hull: the first civ (stable CivKeyById order) whose
    /// art vocabulary resolves a Meshy asset for the hull's role, else null.</summary>
    public static string? PrimaryCiv(HullModelResolver resolver, DataHullTypeIGC hull)
    {
        foreach (var civ in HullModelResolver.CivKeyById.Values.Distinct())
            if (resolver.ResolveAsset(civ, hull) is not null)
                return civ;
        return null;
    }

    /// <summary>True when the hull is player-flyable (non-drone): native lists a hangar
    /// hull only when its signed GroupId is non-negative (clintlib.h:1858).</summary>
    public static bool IsFlyable(DataHullTypeIGC hull) => hull.GroupId >= 0;

    /// <summary>
    /// Build the ordered walk list. <paramref name="includeDrones"/> toggles the
    /// drone/utility hulls. <paramref name="nativeAvailable"/> tests whether a hull's
    /// native <c>.mdl</c> mesh is decodable (so <see cref="InspectorHull.NativeAvailable"/>
    /// is honest); <paramref name="fallbackUsed"/> = no native AND no Meshy art. Entries
    /// are ordered by HullId (stable across runs).
    /// </summary>
    public static List<InspectorHull> Enumerate(
        IEnumerable<DataHullTypeIGC> hulls,
        HullModelResolver resolver,
        bool includeDrones,
        Func<DataHullTypeIGC, bool> nativeAvailable)
    {
        var result = new List<InspectorHull>();
        foreach (var h in hulls.Where(h => includeDrones || IsFlyable(h))
                               .OrderBy(h => h.HullId))
        {
            if (result.Any(e => e.HullId == h.HullId)) continue;   // dedupe by id
            bool native = nativeAvailable(h);
            string? meshy = PrimaryCiv(resolver, h) is { } civ ? resolver.ResolveAsset(civ, h) : null;
            string? tex = NormaliseTexture(h.TextureName, h.ModelName);
            result.Add(new InspectorHull(
                h.HullId, h.Name, h.ModelName, tex, h.Length,
                !IsFlyable(h), PrimaryCiv(resolver, h), meshy,
                native, !native && meshy is null));
        }
        return result;
    }

    /// <summary>The hulls belonging to a civ's fleet (primary-civ match), in walk order.</summary>
    public static List<InspectorHull> ForCiv(List<InspectorHull> all, string civKey)
        => all.Where(e => string.Equals(e.CivKey, civKey, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Every civ that owns at least one hull in the list, in walk order.</summary>
    public static List<string> Civs(List<InspectorHull> all)
        => all.Select(e => e.CivKey).Where(c => c is not null).Cast<string>()
              .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.Ordinal).ToList();

    /// <summary>Same null-out rule native applies to a hull TextureName override
    /// (trekigc.cpp:1837-1839): alpha-leading and differs from the model.</summary>
    private static string? NormaliseTexture(string? name, string modelName)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var t = name.Trim();
        if (t.Length == 0) return null;
        if (!char.IsLetter(t[0])) return null;
        if (string.Equals(t, modelName, StringComparison.OrdinalIgnoreCase)) return null;
        return t;
    }
}
