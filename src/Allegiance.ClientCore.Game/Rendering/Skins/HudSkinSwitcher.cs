namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>A fully loaded skin. Immutable references are published together at
/// the update/draw boundary; frame selection is a single reference read.</summary>
public sealed record ResolvedHudSkin(HudSkinDefinition Definition, IHudSkinRenderer Renderer);

/// <summary>Owns the preloaded skin adapters in install order (Classic, Modern,
/// then Jet when the game supplies it). Lookup happens only on a request;
/// ApplyPending and Active perform no lookup, IO, parsing or allocation.
/// Called on the game thread, outside all SpriteBatch passes.
/// <para>P1035 — an ordered N-skin registry. Jet is <em>installed</em> but gated:
/// while <c>hud.jetSkin</c> is off it is skipped by <see cref="Cycle"/> and
/// refused by <see cref="Request"/>; turning the gate off at runtime while Jet is
/// active falls back to Modern at the next boundary
/// (<see cref="ObserveJetAvailability"/>). The gate is a delegate so selection
/// stays allocation-free and the switcher never reads game options itself.</para></summary>
public sealed class HudSkinSwitcher : IDisposable
{
    private readonly HudSettings _settings;
    private readonly ResolvedHudSkin[] _installed;
    private readonly Func<bool>? _jetEnabled;
    private readonly int _jetIndex;
    private ResolvedHudSkin? _pending;
    private bool _jetWasEnabled;
    private bool _cycleWasDown;
    private bool _disposed;

    /// <summary>The two-skin form used before Jet existed: Classic then Modern,
    /// with no gated entry.</summary>
    public HudSkinSwitcher(HudSettings settings, IHudSkinRenderer classic,
        IHudSkinRenderer modern, HudSkinCatalog? catalog = null)
        : this(settings, classic, modern, null, catalog, null) { }

    /// <param name="jet">The preloaded Jet renderer, or null when the build has
    /// none (Jet then is not installed at all: never requested, never cycled).</param>
    /// <param name="catalog">Definitions; the shared catalog by default.</param>
    /// <param name="jetEnabled">The <c>hud.jetSkin</c> gate, evaluated on Cycle
    /// and Request only. Null means "installed and always enabled" (tests that pin
    /// the registry itself).</param>
    public HudSkinSwitcher(HudSettings settings, IHudSkinRenderer classic,
        IHudSkinRenderer modern, IHudSkinRenderer? jet, HudSkinCatalog? catalog = null,
        Func<bool>? jetEnabled = null)
    {
        catalog ??= HudSkinResolver.Catalog;
        if (classic.Id != HudSkinIds.Classic || modern.Id != HudSkinIds.Modern)
            throw new ArgumentException("Classic and Modern adapters must match their skin ids.");
        if (jet is not null && jet.Id != HudSkinIds.Jet)
            throw new ArgumentException("The Jet adapter must match its skin id.");
        if (settings.RequestSkinSelection is not null)
            throw new ArgumentException("HUD settings already have a renderer owner.", nameof(settings));
        _settings = settings;
        _jetEnabled = jet is null ? null : jetEnabled;
        _installed = jet is null
            ? [new(catalog.Resolve(HudSkinIds.Classic), classic), new(catalog.Resolve(HudSkinIds.Modern), modern)]
            : [new(catalog.Resolve(HudSkinIds.Classic), classic), new(catalog.Resolve(HudSkinIds.Modern), modern),
               new(catalog.Resolve(HudSkinIds.Jet), jet)];
        _jetIndex = jet is null ? -1 : _installed.Length - 1;
        _jetWasEnabled = _jetEnabled?.Invoke() ?? true;
        // A persisted Jet id while the gate is off displays Modern WITHOUT
        // rewriting the saved value (P1035): the settings keep 'jet', the active
        // reference is Modern, and Style/level defaults stay Modern's.
        int saved = Find(HudSkinIds.Normalize(settings.Skin));
        int activeIndex = saved >= 0 && Enabled(saved) ? saved : 1;
        Active = _installed[activeIndex];
        if (saved < 0 || activeIndex == saved) settings.ActivateSkin(Active.Definition.Id);
        settings.RequestSkinSelection = RequestFromSettings;
    }

    public ResolvedHudSkin Active { get; private set; }

    /// <summary>Every installed skin in cycle order, including a gated-off Jet.</summary>
    public IReadOnlyList<ResolvedHudSkin> Installed => _installed;

    public bool Request(string id)
    {
        int index = Find(HudSkinIds.Normalize(id));
        if (index < 0 || !Enabled(index)) return false; // unavailable: active/pending intact
        _pending = _installed[index];
        return true;
    }

    private void RequestFromSettings(string id) => Request(id);

    /// <summary>Advance to the next <em>enabled</em> skin in install order, so a
    /// gated-off Jet is skipped and Classic/Modern wrap exactly as before.</summary>
    public void Cycle(int direction = 1)
    {
        var current = _pending ?? Active;
        int index = IndexOf(current);
        int step = direction < 0 ? -1 : 1;
        for (int offset = 1; offset <= _installed.Length; offset++)
        {
            int next = ((index + step * offset) % _installed.Length + _installed.Length) % _installed.Length;
            if (!Enabled(next)) continue;
            _pending = _installed[next];
            return;
        }
    }

    /// <summary>P1035 — observe the live <c>hud.jetSkin</c> gate at the update
    /// boundary. Turning it off while Jet is active (or queued) queues Modern in
    /// its place, so the switch happens atomically at the next
    /// <see cref="ApplyPending"/>. Allocation-free and idempotent.</summary>
    public void ObserveJetAvailability(bool enabled)
    {
        if (_jetIndex < 0 || _jetEnabled is null) return;
        if (enabled == _jetWasEnabled) return;
        _jetWasEnabled = enabled;
        if (enabled) return;
        if (_pending is not null && ReferenceEquals(_pending, _installed[_jetIndex]))
            _pending = _installed[1];
        if (ReferenceEquals(Active, _installed[_jetIndex]))
            _pending = _installed[1];
    }

    /// <summary>True when the skin at <paramref name="index"/> may be selected now.</summary>
    private bool Enabled(int index) => index != _jetIndex || _jetEnabled is null || _jetEnabled();

    private int Find(string id)
    {
        for (int i = 0; i < _installed.Length; i++)
            if (string.Equals(_installed[i].Definition.Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }

    private int IndexOf(ResolvedHudSkin skin)
    {
        for (int i = 0; i < _installed.Length; i++)
            if (ReferenceEquals(_installed[i], skin)) return i;
        return 1;   // Modern is the safe fallback index for an unknown reference.
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
