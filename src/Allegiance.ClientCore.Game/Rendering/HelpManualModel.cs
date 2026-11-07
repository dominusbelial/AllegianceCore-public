using System.Collections.Generic;
using System.Linq;

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
///
/// <para>P941/P949 — the gameplay pages that name keys are built from the LIVE
/// <see cref="ActionMap"/> bindings (<see cref="BuildTopics"/>), so the F1
/// overview states the keys the player actually has: the fresh-profile Port
/// layout (W/S throttle, A/D roll, Space fires) and the "Classic Allegiance"
/// native preset both read correctly, and a rebind shows up the next time the
/// help opens.</para>
/// </summary>
public sealed class HelpManualModel
{
    /// <summary>One manual page: its topic id, display title and body lines.</summary>
    public readonly record struct Topic(string Id, string Title, IReadOnlyList<string> Lines);

    /// <summary>The manual's topics in nav-pane order (native helpdata), with the
    /// key-naming pages generated from the live bindings.</summary>
    public static IReadOnlyList<Topic> Topics => BuildTopics(KeyBindingStore.Instance.Map);

    /// <summary>Build every topic for <paramref name="map"/>. Kept public and pure
    /// (an explicit map, no singletons) so the P941 test can pin both layouts.
    /// Every line stays ASCII 32..126 (the baked HUD spritefonts).</summary>
    public static IReadOnlyList<Topic> BuildTopics(ActionMap map)
    {
        string Key(string action) => map.LabelOf(action) is { Length: > 0 } label ? label : "unbound";

        return
        [
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
                "The " + Key("CycleChatTarget") + " key cycles your chat recipient; "
                    + Key("QuickChat") + " opens the",
                "quick chat order menu. " + Key("Help") + " (this help) lists every control.",
                "",
                "Press " + Key("Help") + " or " + Key("CancelChat") + " to close this help.",
            ]),
            new("hlpControls", "CONTROLS",
            [
                "Every current client binding, by group. See the columns.",
                "Mouse and wheel commands show the button native binds.",
                "Backspace returns to the previous topic (native Back).",
            ]),
            new("hlpFlight", "FLIGHT",
            [
                "FLIGHT",
                "",
                Key("ThrustForward") + "/" + Key("ThrustBackward")
                    + " throttle up/down; " + Key("ThrustLeft") + "/" + Key("ThrustRight")
                    + " lateral thrust.",
                Key("ThrustUp") + "/" + Key("ThrustDown") + " vertical thrust; "
                    + Key("RollLeft") + "/" + Key("RollRight") + " roll the craft.",
                Key("FireBooster") + " fires the booster (hold); the wheel or "
                    + Key("ThrottleFull") + " sets full throttle.",
                Key("ToggleMouse") + " toggles mouse flight (virtual joystick): with it ON",
                "the mouse flies and its buttons fire (left = weapons, middle = missiles,",
                "right = target centre). With it OFF the mouse is a pointer: left selects a",
                "same-side ship, right orders the object under the cursor.",
                Key("TargetFriendly") + " targets a friendly; " + Key("TargetEnemy")
                    + " an enemy; " + Key("ToggleAutopilot") + " engages the autopilot.",
                Key("Ripcord") + " teleports you home when it is legal.",
                "Dock at a friendly station to buy parts and new hulls.",
            ]),
            new("hlpCombat", "COMBAT",
            [
                "COMBAT",
                "",
                Key("FireWeapon") + " fires the selected weapon; " + Key("FireMissile")
                    + " fires a missile.",
                Key("PrevWeapon") + "/" + Key("NextWeapon") + " cycle weapons.",
                Key("TargetEnemy") + " targets the next enemy, " + Key("TargetEnemyBase")
                    + " the next enemy base.",
                "Missiles lock while you hold the target in your front arc.",
                Key("DropChaff") + " launches chaff to break an incoming missile's lock.",
                "Shields regenerate when you stop taking hits.",
            ]),
            new("hlpCommand", "COMMAND",
            [
                "COMMANDING AND ORDERS",
                "",
                "The commander views the sector map (" + Key("ToggleStarmap")
                    + ") and picks with the mouse",
                "to order drones: mine, build, attack, defend, goto.",
                Key("CommandSelectionAdd") + "+left click adds recipients; right click issues "
                    + Key("ExecuteCommand"),
                "or the default order at that spot.",
                Key("AcceptCommand") + " accepts a received order; " + Key("RejectCommand")
                    + " declines it.",
                "Drones acknowledge through chat; refuse what they cannot do.",
                "Invest in developments from the docked hangar to unlock",
                "better hulls, weapons and stations.",
            ]),
        ];
    }

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
        var topics = Topics;
        int i = topics.ToList().FindIndex(t => t.Id == CurrentTopic);
        int n = topics.Count;
        GoTo(topics[(((i + delta) % n) + n) % n].Id);
    }

    /// <summary>Scroll the current page (native per-page scroll bars). Clamped
    /// to the page's line count.</summary>
    public void ScrollBy(int lines, int visibleLines)
    {
        int max = System.Math.Max(0, Current.Lines.Count - System.Math.Max(1, visibleLines));
        Scroll = System.Math.Clamp(Scroll + lines, 0, max);
    }
}
