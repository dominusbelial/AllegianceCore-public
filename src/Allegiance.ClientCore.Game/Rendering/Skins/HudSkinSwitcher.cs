namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>A fully loaded skin. Immutable references are published together at
/// the update/draw boundary; frame selection is a single reference read.</summary>
public sealed record ResolvedHudSkin(HudSkinDefinition Definition, IHudSkinRenderer Renderer);

/// <summary>Owns the two preloaded adapters. Lookup happens only on a request;
/// ApplyPending and Active perform no lookup, IO, parsing or allocation.
/// Called on the game thread, outside all SpriteBatch passes.</summary>
public sealed class HudSkinSwitcher : IDisposable
{
    private readonly HudSettings _settings;
    private readonly ResolvedHudSkin[] _installed;
    private ResolvedHudSkin? _pending;
    private bool _cycleWasDown;
    private bool _disposed;

    public HudSkinSwitcher(HudSettings settings, IHudSkinRenderer classic,
        IHudSkinRenderer modern, HudSkinCatalog? catalog = null)
    {
        catalog ??= HudSkinResolver.Catalog;
        if (classic.Id != HudSkinIds.Classic || modern.Id != HudSkinIds.Modern)
            throw new ArgumentException("Classic and Modern adapters must match their skin ids.");
        if (settings.RequestSkinSelection is not null)
            throw new ArgumentException("HUD settings already have a renderer owner.", nameof(settings));
        _settings = settings;
        _installed = [new(catalog.Resolve(HudSkinIds.Classic), classic), new(catalog.Resolve(HudSkinIds.Modern), modern)];
        Active = settings.Skin == HudSkinIds.Classic ? _installed[0] : _installed[1];
        settings.ActivateSkin(Active.Definition.Id);
        settings.RequestSkinSelection = RequestFromSettings;
    }

    public ResolvedHudSkin Active { get; private set; }

    public bool Request(string id)
    {
        var normalized = HudSkinIds.Normalize(id);
        foreach (var skin in _installed)
            if (skin.Definition.Id == normalized) { _pending = skin; return true; }
        return false; // An unavailable skin leaves the active/pending skin intact.
    }

    private void RequestFromSettings(string id) => Request(id);

    public void Cycle(int direction = 1)
    {
        var current = _pending ?? Active;
        int index = ReferenceEquals(current, _installed[0]) ? 0 : 1;
        _pending = _installed[(index + (direction < 0 ? -1 : 1) + _installed.Length) % _installed.Length];
    }

    /// <summary>Observe even while blocked: holding a key through compose/modal
    /// dismissal or focus regain must never manufacture another press edge.</summary>
    public void ObserveCycle(bool down, bool blocked)
    {
        bool pressed = down && !_cycleWasDown;
        _cycleWasDown = down;
        if (pressed && !blocked) Cycle();
    }

    public bool ApplyPending()
    {
        var next = _pending;
        _pending = null;
        if (next is null || ReferenceEquals(next, Active)) return false;
        Active = next;
        _settings.ActivateSkin(next.Definition.Id);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.RequestSkinSelection = null;
        foreach (var skin in _installed) skin.Renderer.Dispose();
    }
}
