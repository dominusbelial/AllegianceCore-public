using System.Reflection;

namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>The installed, validated skin catalog. Built-in skins are loaded at
/// construction; a missing or invalid built-in Modern skin is a startup error.
/// Optional user definitions are added later through <see cref="TryAdd"/>.</summary>
public sealed class HudSkinCatalog
{
    private readonly Dictionary<string, HudSkinDefinition> _byId = new();
    // P1041 — palette variants (jet.amber) share their skin's id and live here,
    // keyed "id/variant", so they never appear in Installed or the F-key cycle.
    private readonly Dictionary<string, HudSkinDefinition> _variants = new(StringComparer.Ordinal);
    private bool _unknownDiagnostic;

    public HudSkinCatalog() => LoadBuiltIns();

    public IReadOnlyCollection<HudSkinDefinition> Installed => _byId.Values;

    public HudSkinDefinition? TryGet(string id) =>
        _byId.TryGetValue(HudSkinIds.Normalize(id), out var def) ? def : null;

    /// <summary>P1041 — the palette variant of <paramref name="id"/> named
    /// <paramref name="variant"/>, or null when the build ships none. A variant is
    /// not an installed skin: it is only reachable through the skin it colours.</summary>
    public HudSkinDefinition? TryGetVariant(string id, string variant) =>
        _variants.TryGetValue(VariantKey(HudSkinIds.Normalize(id), HudSkinIds.Normalize(variant)), out var def) ? def : null;

    private static string VariantKey(string id, string variant) => id + "/" + variant;

    /// <summary>Returns the installed skin for <paramref name="id"/>, or the
    /// built-in Modern fallback with a one-time diagnostic for unknown ids.</summary>
    public HudSkinDefinition Resolve(string? id)
    {
        var normalized = HudSkinIds.Normalize(id);
        if (_byId.TryGetValue(normalized, out var def)) return def;

        if (!_unknownDiagnostic)
        {
            Console.WriteLine($"[hud.skin] Unknown skin id '{id}'; falling back to '{HudSkinIds.Modern}'.");
            _unknownDiagnostic = true;
        }
        return _byId[HudSkinIds.Modern];
    }

    /// <summary>Attempts to add/overwrite a definition. Returns false and keeps
    /// the prior skin if validation fails.</summary>
    public bool TryAdd(string json)
    {
        try
        {
            var def = HudSkinLoader.Load(json);
            if (def.Variant is not null) _variants[VariantKey(def.Id, def.Variant)] = def;
            else _byId[def.Id] = def;
            return true;
        }
        catch (HudSkinLoadException ex)
        {
            Console.WriteLine($"[hud.skin] Rejected skin: {ex.Message}");
            return false;
        }
    }

    private void LoadBuiltIns()
    {
        var assembly = typeof(HudSkinCatalog).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".skin.json", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var def = HudSkinLoader.Load(stream);
            // P1041 — jet.amber is a palette VARIANT of jet, not a skin of its own.
            if (def.Variant is not null) { _variants[VariantKey(def.Id, def.Variant)] = def; continue; }
            _byId[def.Id] = def;
        }

        if (!_byId.ContainsKey(HudSkinIds.Modern))
            throw new InvalidOperationException("Built-in Modern skin is missing; cannot start with a blank HUD.");
    }
}
