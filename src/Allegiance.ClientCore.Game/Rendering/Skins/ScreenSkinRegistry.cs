namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>
/// P1042 (P972 §2.5, §6.1) — the screen-chrome registry that publishes WITH the HUD
/// skin and the palette. Every adapter is preloaded at <c>LoadContent</c>; a switch only
/// swaps one reference, and it is published by <c>Game1.CommitHudSkinChange</c> in the
/// same update-boundary call as <see cref="HudSkinSwitcher.ApplyPending"/> and
/// <see cref="JetPaletteSwitcher.ApplyPending"/>, so a frame can never see new HUD
/// symbology over old screen chrome (no half-switched frame).
///
/// <para>Jet is gated exactly like the HUD skin: while <c>hud.jetSkin</c> is off it is
/// never published (unknown/gated ids fall back to Modern, matching
/// <see cref="HudSkinSwitcher"/>), so with the flag off nothing in this story is visible.
/// Lookup happens only on publish — never during Draw.</para>
/// </summary>
public sealed class ScreenSkinRegistry : IDisposable
{
    private readonly IScreenSkinRenderer _classic;
    private readonly IScreenSkinRenderer _modern;
    private readonly IScreenSkinRenderer? _jet;
    private bool _disposed;

    public ScreenSkinRegistry(IScreenSkinRenderer classic, IScreenSkinRenderer modern,
        IScreenSkinRenderer? jet = null)
    {
        if (classic.Id != HudSkinIds.Classic || modern.Id != HudSkinIds.Modern)
            throw new ArgumentException("Screen adapters must match their skin ids.");
        if (jet is not null && jet.Id != HudSkinIds.Jet)
            throw new ArgumentException("The Jet screen adapter must match its skin id.");
        _classic = classic;
        _modern = modern;
        _jet = jet;
        Active = modern;
    }

    /// <summary>The published chrome; a single reference read per frame.</summary>
    public IScreenSkinRenderer Active { get; private set; }

    /// <summary>The installed adapters in cycle order (Jet present only when built).</summary>
    public IReadOnlyList<IScreenSkinRenderer> Installed => _jet is null
        ? [_classic, _modern]
        : [_classic, _modern, _jet];

    /// <summary>Publish the chrome for a skin id. Returns true only when the active
    /// chrome really changed; no allocation, IO or lookup map.</summary>
    public bool Publish(string? skinId, bool jetEnabled)
    {
        var next = Resolve(skinId, jetEnabled);
        if (ReferenceEquals(next, Active)) return false;
        Active = next;
        return true;
    }

    /// <summary>The chrome <paramref name="skinId"/> selects right now. Unknown ids and a
    /// gated-off Jet resolve to Modern (P972 §2.4's unknown-skin rule), never to null.</summary>
    public IScreenSkinRenderer Resolve(string? skinId, bool jetEnabled)
    {
        string id = HudSkinIds.Normalize(skinId);
        if (id == HudSkinIds.Classic) return _classic;
        if (id == HudSkinIds.Jet) return jetEnabled && _jet is not null ? _jet : _modern;
        return _modern;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _classic.Dispose();
        _modern.Dispose();
        _jet?.Dispose();
    }
}
