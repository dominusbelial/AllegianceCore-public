namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P500 — a pure integer-free rectangle used for the connect screen's touch
/// hit-testing and button/row layout. Kept in the shared library and free of
/// MonoGame dependencies so the geometry is unit-testable without a graphics
/// device (the Draw path and the touch path both derive from these same
/// rects, so what you tap is what you draw).
/// </summary>
public readonly record struct FRect(float X, float Y, float W, float H)
{
    public bool Contains(float px, float py)
        => px >= X && py >= Y && px <= X + W && py <= Y + H;

    public float CenterX => X + W / 2f;
    public float CenterY => Y + H / 2f;
}

/// <summary>
/// P500 — the connect-screen layout: row rectangles for the server list and a
/// bottom action-button row (CONNECT / ADD / EDIT / DEL / NAME / LOBBY). Pure
/// geometry — the same constants feed both the SpriteBatch Draw path and the
/// TouchPanel hit-test, so a tap lands exactly where the button is drawn.
/// </summary>
public static class ConnectScreenLayout
{
    public const float RowH = 30f;
    public const float ListTop = 150f;
    public const float RowX = 50f;
    public const float RowWidthFactor = 0.7f;

    public enum Button { Connect, Add, Edit, Delete, Name, Lobby }

    /// <summary>
    /// P500 — bottom safe-area inset. Empirically measured on the Android head:
    /// content drawn below ~94% of the viewport height is clipped off the
    /// visible screen (the bottom ~6% — the system gesture/nav inset MonoGame
    /// Android does not present into). The connect-screen buttons and footer
    /// anchor above this inset so they are actually visible and tappable.
    /// </summary>
    public static float SafeBottom(float viewportHeight)
        => Math.Max(64f, viewportHeight * 0.084f);

    public static readonly (Button Kind, string Label)[] ButtonDefs =
    {
        (Button.Connect, "CONNECT"),
        (Button.Add, "ADD"),
        (Button.Edit, "EDIT"),
        (Button.Delete, "DEL"),
        (Button.Name, "NAME"),
        (Button.Lobby, "LOBBY"),
    };

    public static FRect RowRect(int visibleIndex, float viewportWidth)
        => new(RowX, ListTop + visibleIndex * RowH, viewportWidth * RowWidthFactor, RowH);

    /// <summary>
    /// Compute the bottom action-button row, centred horizontally, each button
    /// sized from its label length (a font-independent approximation so the
    /// geometry is stable and testable). Anchored above the bottom safe-area
    /// inset (see <see cref="SafeBottom"/>) so the row is visible/tappable on
    /// Android, where the bottom ~6% of the viewport is clipped.
    /// </summary>
    /// <summary>
    /// P759 — the abort destination for a failed join, the pure half of native
    /// <c>OnLogonGameServerFailed(false, reason)</c> (<c>introscreen.cpp:1667-1699</c>):
    /// back to the intro find-server flow, back to the lobby browser, or exit
    /// (the unchanged direct-connect path).
    /// </summary>
    public enum ConnectAbortDestination { Intro, Lobby, Exit }

    /// <summary>Resolve the abort destination from the two origin flags
    /// (<c>_returnToIntroOnFailure</c> wins, then <c>_returnToLobbyOnFailure</c>).</summary>
    public static ConnectAbortDestination AbortDestination(bool returnToIntro, bool returnToLobby)
        => returnToIntro ? ConnectAbortDestination.Intro
            : returnToLobby ? ConnectAbortDestination.Lobby
            : ConnectAbortDestination.Exit;

    public static IReadOnlyList<(Button Kind, FRect Rect)> ComputeButtons(float viewportWidth, float viewportHeight)
    {
        const float gap = 10f;
        const float btnH = 40f;
        const float minW = 74f;
        float y = viewportHeight - SafeBottom(viewportHeight) - btnH;

        var widths = new float[ButtonDefs.Length];
        float total = 0f;
        for (int i = 0; i < ButtonDefs.Length; i++)
        {
            // ~13px/char bitmap-font estimate + padding; floor at minW.
            widths[i] = Math.Max(minW, ButtonDefs[i].Label.Length * 13f + 22f);
            total += widths[i];
        }
        total += gap * (ButtonDefs.Length - 1);
        float x = (viewportWidth - total) / 2f;

        var result = new List<(Button, FRect)>(ButtonDefs.Length);
        for (int i = 0; i < ButtonDefs.Length; i++)
        {
            result.Add((ButtonDefs[i].Kind, new FRect(x, y, widths[i], btnH)));
            x += widths[i] + gap;
        }
        return result;
    }
}
