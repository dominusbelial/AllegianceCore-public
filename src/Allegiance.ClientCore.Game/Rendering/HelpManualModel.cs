namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P757 — the pure model of the in-game help <b>manual</b>: native
/// <c>HelpPaneImpl</c> (<c>effect/help.cpp</c>). Native builds the pane from
/// the <c>helppane</c> + <c>helpdata</c> namespaces with a nav pane of main
/// topics, a main page, a secondary page, per-page scroll bars, and a topic
/// <b>history</b> with a Back button (<c>m_listHistory</c>: <c>OnMainTopic</c>
/// pushes the topic on the front, <c>OnButtonBack</c> pops it,
/// <c>DoSetTopic</c> resets both scroll positions — help.cpp:225-296). The
/// port's manual keeps that navigation machinery with pages defined in code:
/// the start page (<c>hlpStart</c>), the generated key reference (P257
/// <see cref="KeyBindingTable"/>) as the Controls page, and short gameplay
/// pages that describe the port's real controls. Pure and MonoGame-free so
/// every navigation rule is headless-testable.
/// </summary>
public sealed class HelpManualModel
{
    /// <summary>One manual page: its topic id, display title and body lines.</summary>
    public readonly record struct Topic(string Id, string Title, IReadOnlyList<string> Lines);

    /// <summary>The manual's topics in nav-pane order (native helpdata).</summary>
    public static readonly Topic[] Topics =
    {
        new("hlpStart", "START",
        [
            "ALLEGIANCE - QUICK START",
            "",
            "You are a pilot in a squad of starfighters. Work with your",
            "team commander to capture sectors, mine helium, and destroy",
            "the enemy team's stations.",
            "",
            "First steps: join a side in the lobby, launch from your",
            "station's hangar, and follow your commander's orders.",
            "The TAB key cycles your chat recipient; R opens the quick",
            "chat order menu. H (this help) lists every control.",
            "",
            "Press H or ESC to close this help.",
        ]),
        new("hlpControls", "CONTROLS",
        [
            "Every current client binding, by group. See the columns.",
            "Backspace returns to the previous topic (native Back).",
        ]),
        new("hlpFlight", "FLIGHT",
        [
            "FLIGHT",
            "",
            "W/S throttle up/down; Q/E roll; A/D lateral thrust.",
            "Mouse or joystick steers (toggle with the mouse-flight key).",
            "T cycles the nearest target; Insert toggles the autopilot,",
            "which flies you to the selected target.",
            "Ripcord (Ctrl+R) teleports you home when it is legal.",
            "Dock at a friendly station to buy parts and new hulls.",
        ]),
        new("hlpCombat", "COMBAT",
        [
            "COMBAT",
            "",
            "Space fires the selected weapon; [ and ] cycle weapons.",
            "G targets the nearest enemy; Ctrl+G the nearest enemy base.",
            "Missiles lock while you hold the target in your front arc.",
            "Launch chaff (C) to break an incoming missile's lock.",
            "Shields regenerate when you stop taking hits.",
        ]),
        new("hlpCommand", "COMMAND",
        [
            "COMMANDING AND ORDERS",
            "",
            "The commander views the sector map (Ctrl+M) and clicks",
            "objects to order drones: mine, build, attack, defend, goto.",
            "Players issue orders with the commandee key + Insert.",
            "Drones acknowledge through chat; refuse what they cannot do.",
            "Invest in developments from the docked hangar to unlock",
            "better hulls, weapons and stations.",
        ]),
    };

    /// <summary>The topic history, newest first (native <c>m_listHistory</c>).</summary>
    private readonly List<string> _history = new() { "hlpStart" };

    /// <summary>Vertical scroll position of the current page (native
    /// <c>mscrollMain-&gt;SetPos(0)</c> on every topic change, help.cpp:234).</summary>
    public int Scroll { get; private set; }

    /// <summary>The current topic id (front of the history).</summary>
    public string CurrentTopic => _history[0];

    /// <summary>The current topic record.</summary>
    public Topic Current => Topics.First(t => t.Id == CurrentTopic);

    /// <summary>How many entries the history holds (1 = at the start topic; the
    /// native Back button is disabled then, help.cpp:270-276).</summary>
    public int HistoryDepth => _history.Count;

    /// <summary>Switch to a main topic (native <c>OnMainTopic</c>, help.cpp:280-288):
    /// push it on the history front unless it is already current, and reset the
    /// page scroll.</summary>
    public void GoTo(string topicId)
    {
        if (_history[0] != topicId)
        {
            _history.Insert(0, topicId);
            Scroll = 0;
        }
    }

    /// <summary>Native <c>OnButtonBack</c> (help.cpp:270-277): pop the history
    /// front and re-show the previous topic — a no-op at the start topic.</summary>
    public void Back()
    {
        if (_history.Count > 1)
        {
            _history.RemoveAt(0);
            Scroll = 0;
        }
    }

    /// <summary>Move the topic selection in the nav pane by whole topics;
    /// going to a neighbouring topic is a normal topic switch (history push).</summary>
    public void MoveTopicSelection(int delta)
    {
        int i = Array.FindIndex(Topics, t => t.Id == CurrentTopic);
        int n = Topics.Length;
        GoTo(Topics[(((i + delta) % n) + n) % n].Id);
    }

    /// <summary>Scroll the current page (native per-page scroll bars). Clamped
    /// to the page's line count.</summary>
    public void ScrollBy(int lines, int visibleLines)
    {
        int max = Math.Max(0, Current.Lines.Count - Math.Max(1, visibleLines));
        Scroll = Math.Clamp(Scroll + lines, 0, max);
    }
}
