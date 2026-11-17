using Allegiance.ClientCore.MonoGame.Rendering.Screens;

namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>
/// P1042 — the Classic/Modern screen-chrome adapter. A pure adapter over the drawing
/// that existed before <see cref="IScreenSkinRenderer"/>: it owns no geometry, no
/// colours and no verb logic; it forwards the already-resolved frame to
/// <see cref="IntroScreenPainter"/>, <see cref="HangarScreenRenderer"/>,
/// <see cref="DockedScreenRenderer"/> and <see cref="LoadoutV2Renderer"/> exactly as
/// <c>Game1</c> called them, so Classic and Modern screens are byte-identical.
///
/// <para>The renderers are borrowed (Game1 owns the artwork/session lifetime) except the
/// intro painter, which is this adapter's own and allocates nothing per frame.</para>
/// </summary>
public sealed class ClassicScreenSkinRenderer : IScreenSkinRenderer
{
    private readonly string _id;
    private readonly IntroScreenPainter _intro;
    private readonly HangarScreenRenderer? _hangar;
    private readonly DockedScreenRenderer? _docked;
    private readonly LoadoutV2Renderer? _loadoutV2;

    public ClassicScreenSkinRenderer(string id, IntroScreenPainter intro,
        HangarScreenRenderer? hangar, DockedScreenRenderer? docked, LoadoutV2Renderer? loadoutV2)
    {
        if (id != HudSkinIds.Classic && id != HudSkinIds.Modern)
            throw new ArgumentException("The Classic adapter must be the classic or modern skin.", nameof(id));
        _id = id;
        _intro = intro ?? throw new ArgumentNullException(nameof(intro));
        _hangar = hangar;
        _docked = docked;
        _loadoutV2 = loadoutV2;
    }

    public string Id => _id;

    public bool HangarArtAvailable => _hangar?.CanDrawNative == true;

    public void DrawIntro(in IntroMainFrame frame) => _intro.Draw(frame);

    public void DrawHangar(in HangarScreenFrame frame)
    {
        if (_hangar is null) return;
        _hangar.Draw(frame.Viewport, frame.Hover, frame.Launch, frame.BoardState, frame.Developments);
    }

    public void DrawLoadout(in LoadoutScreenFrame frame)
    {
        if (frame.V2Layout is { } layout && frame.V2Frame is { } presentation)
        {
            _loadoutV2?.Draw(presentation, layout, frame.Style, frame.Hover, frame.Marker);
            return;
        }
        _docked?.Draw(frame.Viewport, frame.Rows, frame.DetailRows, frame.Money,
            frame.BackdropAlpha, frame.FooterHeight, frame.SplitDetailPanels,
            frame.CreditBox, frame.NativeMoneyPanel);
    }

    /// <summary>Borrowed renderers: the game owns their lifetime, exactly as before.</summary>
    public void Dispose() { }
}
