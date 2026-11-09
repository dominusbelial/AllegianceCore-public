using System.Reflection;

namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>The installed, validated skin catalog. Built-in skins are loaded at
/// construction; a missing or invalid built-in Modern skin is a startup error.
/// Optional user definitions are added later through <see cref="TryAdd"/>.</summary>
public sealed class HudSkinCatalog
{
    private readonly Dictionary<string, HudSkinDefinition> _byId = new();
    private bool _unknownDiagnostic;

    public HudSkinCatalog() => LoadBuiltIns();

    public IReadOnlyCollection<HudSkinDefinition> Installed => _byId.Values;

    public HudSkinDefinition? TryGet(string id) =>
        _byId.TryGetValue(HudSkinIds.Normalize(id), out var def) ? def : null;

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
            _byId[def.Id] = def;
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
            _byId[def.Id] = def;
        }

        if (!_byId.ContainsKey(HudSkinIds.Modern))
            throw new InvalidOperationException("Built-in Modern skin is missing; cannot start with a blank HUD.");
    }
}
