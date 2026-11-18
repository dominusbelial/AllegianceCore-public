namespace Allegiance.ClientCore.MonoGame.Rendering.Skins;

/// <summary>
/// P1042c — the shared content renderers the screen adapters borrow for the in-game families
/// whose composition a single renderer owns (the leaderboard, the starmap, the sector info
/// pane, the game-over tables, and the Classic panel painter). Borrowed, not owned: the game
/// owns every lifetime exactly as it did before the boundary. A null member simply means the
/// adapter was built without that family installed (the proof tool and unit tests).
/// </summary>
public sealed class ScreenRenderers
{
    /// <summary>The Classic/Modern panel painter (side select, ready check, briefing, team
    /// pane, quick chat, key bindings, connection lost).</summary>
    public Screens.PanelScreenPainter? Panel { get; init; }

    /// <summary>The P639 in-mission leaderboard renderer.</summary>
    public ScoreboardOverlayRenderer? Scoreboard { get; init; }

    /// <summary>The P249 whole-mission starmap renderer (native map content).</summary>
    public StarmapRenderer? Starmap { get; init; }

    /// <summary>The P272 sector info pane renderer.</summary>
    public SectorInfoPaneRenderer? SectorInfo { get; init; }

#if !BROWSER
    /// <summary>The P262/P752 game-over result renderer (its two tables are content).</summary>
    public GameOverScreenRenderer? GameOver { get; init; }
#endif
}
