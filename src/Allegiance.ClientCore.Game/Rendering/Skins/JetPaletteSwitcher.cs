using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>
/// P1041 — one preloaded Jet palette: its variant definition (the palette block)
/// and the Jet renderer built from it. Both variants share the skin/renderer id
/// (<see cref="HudSkinIds.Jet"/>); only the colours differ (P972 §4.1, §9 3E).
/// </summary>
public sealed record ResolvedJetPalette(HudSkinDefinition Definition, IHudSkinRenderer Renderer)
{
    /// <summary>The canonical variant id (<see cref="JetPaletteIds"/>).</summary>
    public string Id => JetPaletteIds.Normalize(Definition.Variant);
}

/// <summary>
/// P1041 — the Jet palette variant registry (P972 §2.4, §4.1). Owns the two
/// preloaded Jet renderers (green + amber) and publishes the active one at the
/// update/draw boundary exactly like <see cref="HudSkinSwitcher"/> publishes a
/// skin: a request is queued, <see cref="ApplyPending"/> swaps one reference, and
/// the frame never sees a half-switched palette. Both were built at
/// <c>LoadContent</c>, so the swap loads, parses and allocates nothing.
/// <para>The variant is NOT the skin id: <see cref="HudSkinSwitcher.Cycle"/> never
/// touches this class, so the F-key cycles skins without cycling palettes. The
/// persisted value lives in <see cref="HudSettings.JetPalette"/>; unknown values
/// resolve to <see cref="JetPaletteIds.Green"/>.</para>
/// </summary>
public sealed class JetPaletteSwitcher : IDisposable
{
    private readonly HudSettings _settings;
    private readonly ResolvedJetPalette[] _palettes;
    private ResolvedJetPalette? _pending;
    private bool _disposed;

    public JetPaletteSwitcher(HudSettings settings, ResolvedJetPalette green, ResolvedJetPalette amber)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.Equals(green.Id, JetPaletteIds.Green, StringComparison.Ordinal)
            || !string.Equals(amber.Id, JetPaletteIds.Amber, StringComparison.Ordinal))
            throw new ArgumentException("The two Jet palettes must be the green and amber variants.");
        if (green.Renderer.Id != HudSkinIds.Jet || amber.Renderer.Id != HudSkinIds.Jet)
            throw new ArgumentException("Both Jet palette renderers must report the Jet skin id.");
        if (settings.RequestJetPaletteSelection is not null)
            throw new ArgumentException("HUD settings already have a palette owner.", nameof(settings));
        _settings = settings;
        _palettes = [green, amber];
        Active = _palettes[JetPaletteIds.Normalize(settings.JetPalette) == JetPaletteIds.Amber ? 1 : 0];
        settings.RequestJetPaletteSelection = RequestFromSettings;
    }

    /// <summary>The published variant; a single reference read per frame.</summary>
    public ResolvedJetPalette Active { get; private set; }

    /// <summary>Both variants in selector order (green, amber).</summary>
    public IReadOnlyList<ResolvedJetPalette> Palettes => _palettes;

    /// <summary>Queue a variant change; applied at the next <see cref="ApplyPending"/>.
    /// Unknown ids are refused and leave the active/pending choice intact.</summary>
    public bool Request(string? id)
    {
        if (!JetPaletteIds.IsKnown(id)) return false;
        _pending = _palettes[string.Equals(id, JetPaletteIds.Amber, StringComparison.Ordinal) ? 1 : 0];
        return true;
    }

    private void RequestFromSettings(string id) => Request(id);

    /// <summary>Publish a queued variant (one reference swap) and persist it. Returns
    /// true only when the active palette really changed.</summary>
    public bool ApplyPending()
    {
        var next = _pending;
        _pending = null;
        if (next is null || ReferenceEquals(next, Active)) return false;
        Active = next;
        _settings.ActivateJetPalette(next.Id);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.RequestJetPaletteSelection = null;
        foreach (var palette in _palettes) palette.Renderer.Dispose();
    }
}

/// <summary>
/// P1041 — the stable <see cref="IHudSkinRenderer"/> the skin switcher installs in
/// its Jet slot. The skin registry sees one Jet skin whose identity never changes
/// while the palette swaps behind it, so a palette switch cannot disturb the
/// skin's persistence, cycle position or the frame's active-skin reference.
/// </summary>
public sealed class JetPaletteRenderer(JetPaletteSwitcher palettes) : IHudSkinRenderer
{
    private readonly JetPaletteSwitcher _palettes = palettes;

    public string Id => HudSkinIds.Jet;
    public IReadOnlySet<string>? Coverage => _palettes.Active.Renderer.Coverage;

    /// <summary>The published variant (the toast/options row read this).</summary>
    public string Palette => _palettes.Active.Id;

    public void Draw(HudModel model, in HudDrawFrame frame) => _palettes.Active.Renderer.Draw(model, frame);
    public void DrawCommand(HudModel model) => _palettes.Active.Renderer.DrawCommand(model);
    public void DrawConsole(HudModel model) => _palettes.Active.Renderer.DrawConsole(model);
    public void SetRadarIcons(RadarIconTextures? icons) => _palettes.Active.Renderer.SetRadarIcons(icons);
    public Rectangle DrawOverlayText(string text, Vector2 position, Color color)
        => _palettes.Active.Renderer.DrawOverlayText(text, position, color);
    public void Dispose() => _palettes.Dispose();
}

/// <summary>P1041 — the switch toast text (P972 §2.5): <c>SKIN: Jet / amber</c>
/// for a Jet variant, <c>SKIN: Jet / green</c> for the green one, and the plain
/// skin name for skins that have no palette variants.</summary>
public static class HudSkinNotice
{
    public static string Format(string skinName, string? palette)
        => palette is null ? "SKIN: " + skinName : "SKIN: " + skinName + " / " + palette;
}

/// <summary>P1041 — the Options screen's variant row (P972 §2.5: "when Jet is
/// active a variant row appears; no permanent extra blank row is imposed on
/// Classic/Modern"). Pure so the rule is testable without a graphics device.</summary>
public static class JetPaletteRow
{
    /// <summary>The row exists only while Jet is the active skin AND the
    /// <c>hud.jetSkin</c> gate is on. With the gate off the selector is HIDDEN (not
    /// a disabled row) and the stored value is kept untouched.</summary>
    public static bool Visible(string? activeSkinId, bool jetEnabled)
        => jetEnabled && string.Equals(activeSkinId, HudSkinIds.Jet, StringComparison.Ordinal);

    public static string Label(string? palette) => "Jet palette: " + JetPaletteIds.Normalize(palette);
}
