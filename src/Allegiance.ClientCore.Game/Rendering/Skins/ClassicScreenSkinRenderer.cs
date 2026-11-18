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
    private readonly ScreenRenderers? _extra;

    public ClassicScreenSkinRenderer(string id, IntroScreenPainter intro,
        HangarScreenRenderer? hangar, DockedScreenRenderer? docked, LoadoutV2Renderer? loadoutV2,
        ScreenRenderers? extra = null)
    {
        if (id != HudSkinIds.Classic && id != HudSkinIds.Modern)
            throw new ArgumentException("The Classic adapter must be the classic or modern skin.", nameof(id));
        _id = id;
        _intro = intro ?? throw new ArgumentNullException(nameof(intro));
        _hangar = hangar;
        _docked = docked;
        _loadoutV2 = loadoutV2;
        _extra = extra;
    }

    public string Id => _id;

    public bool HangarArtAvailable => _hangar?.CanDrawNative == true;

    public void DrawIntro(in IntroMainFrame frame) => _intro.Draw(frame);

    /// <summary>P1042b — the pre-game sub-views forward to the verbatim painter.</summary>
    public void DrawIntroView(in IntroViewFrame frame) => _intro.DrawView(frame);

    /// <summary>P1042b — career/squads/events forward to the unchanged P808c renderer.</summary>
    public void DrawCareer(in CareerScreenFrame frame) => _intro.DrawCareer(frame);

    /// <summary>P1042c — the panel-style in-game overlays forward to the Classic painter,
    /// which draws exactly the fills/runs the pre-boundary renderers drew.</summary>
    public void DrawPanel(in ScreenPanelFrame frame) => _extra?.Panel?.Draw(frame);

    /// <summary>P1042c — the leaderboard forwards to the unchanged P639 renderer (null
    /// palette = its own Classic/Modern constants).</summary>
    public void DrawScoreboard(in ScoreboardFrame frame)
        => _extra?.Scoreboard?.Draw(frame.Viewport, frame.Rows, frame.SelfName, frame.Palette);

    /// <summary>P1042c — the starmap and its info pane forward to the unchanged renderers;
    /// the map content (sectors, alephs, icons, warp edges) is drawn exactly as before.</summary>
    public void DrawStarmap(in StarmapScreenFrame frame)
    {
        _extra?.Starmap?.Draw(frame.Sites, frame.Edges, frame.CurrentSector, frame.SelectedSector,
            frame.Warnings, frame.FlashOn, frame.Viewport, frame.PanelW, frame.PanelH,
            frame.Owners, frame.SideColor, frame.KnownEnemySectors, frame.HighlightedClusters,
            frame.ChatCluster);
        if (frame.InfoPane is { } info)
            _extra?.SectorInfo?.Draw(info, frame.Viewport,
                frame.Panel.X, frame.Panel.Y, frame.Panel.Width, frame.Panel.Height,
                frame.OwnSideColor, frame.Expanded);
    }

#if !BROWSER
    /// <summary>P1042c — the game-over result screen forwards to the unchanged renderer.</summary>
    public void DrawGameOver(in GameOverScreenFrame frame)
        => _extra?.GameOver?.Draw(frame.Viewport, frame.Info, frame.Tables, frame.NextMatchSeconds, frame.Perf);
#endif

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
