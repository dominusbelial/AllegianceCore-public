using System.Globalization;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>The command an intro-screen main-menu button activation issues.
/// Faithful mapping of native's intro-screen buttons
/// (WinTrek/introscreen.cpp): SinglePlayer ← trainBigButtonPane
/// (introscreen.cpp:1221)/OnButtonTraining(:1558); Lobby ← playIntButtonPane
/// (:1213)/OnButtonInternet(:1822); FindServer ← playLanButtonPane
/// (:1212)/OnButtonLAN(:1832, opens the FindServerPopup); Options ←
/// optionsButtonPane (:1223)/OnButtonOptions(:1847); Credits ← creditsButtonPane
/// (:1225)/OnButtonCredits(:1853); Exit ← exitButtonPane (:1227)/OnButtonExit
/// (:1799). DirectConnect is a modern addition (native had no direct-IP field).</summary>
public enum IntroCommand
{
    None,
    SinglePlayer,
    Lobby,
    FindServer,
    DirectConnect,
    Options,
    Credits,
    ReplayIntro,
    Help,
    Exit,
    Benchmark,
    Career,
    HostLan,
    HostInternet,
}

/// <summary>
/// P731 — the pure, unit-testable model of the intro screen's main menu. Kept
/// free of MonoGame/GraphicsDevice so every navigation path can be tested
/// headless (the task's "screen-model tests for each path").
/// </summary>
public sealed class IntroScreenModel
{
    /// <summary>Native's main-menu button slots (see <see cref="IntroCommand"/>).
    /// P757 — Replay Intro ← introButtonPane (introscreen.cpp:1224)/
    /// OnButtonIntro(:1865, → ScreenIDSplashScreen) and Help ← helpButtonPane
    /// (:1228)/OnButtonHelp(:1806, → GetWindow()-&gt;OnHelp(true)) join the menu
    /// after Credits, matching the native button set.</summary>
    public static readonly string[] MainLabels =
    {
        "Single Player", "Play Online", "Play LAN / Find Server",
        "Direct Connect", "Options", "Credits", "Replay Intro", "Help",
        "Exit", "Showcase", "Career", "Host LAN game", "Host internet game",
    };

    /// <summary>The currently highlighted main-menu index.</summary>
    public int Index;

    public int Count => MainLabels.Length;

    public void MoveUp() => Index = (Index - 1 + MainLabels.Length) % MainLabels.Length;
    public void MoveDown() => Index = (Index + 1) % MainLabels.Length;

    // ── P946 — the intro screen's keyboard mapping (pure, no window) ────────
    //
    // Native's intro screen is a mouse-driven pane: every menu entry is a
    // ButtonPane wired to a handler (introscreen.cpp:1212-1228 for the panes,
    // :1558-1876 for the handlers) and the screen itself has no key handler of
    // its own — only the popups force character translation for their edit
    // panes (:686-694, :862-877). The port's keyboard menu is therefore a port
    // design, and it has to be self-explanatory.

    /// <summary>Which intro widget owns typed characters in the current view.
    /// <see cref="None"/> = the menu/list owns the keys.</summary>
    public enum TextField
    {
        None,
        PilotName,
        ServerPassword,
        DirectAddress,
    }

    /// <summary>One frame of pressed-key edges on the main menu.</summary>
    public readonly record struct MenuKeyStep(int SelectionDelta, bool Activate, bool ToggleFieldFocus);

    /// <summary>P946 — the main menu's key→action mapping. Up/Down ALWAYS move
    /// the selection: the pilot-name field never used the arrow keys, so
    /// taking them from the menu bought nothing and made the screen feel
    /// blocked. <paramref name="upPressed"/>/<paramref name="downPressed"/>
    /// are press EDGES (the caller latches them). Tab toggles typed-text focus
    /// only while <paramref name="tabAllowed"/> (there is no identity account:
    /// with an account the pilot label is the verified callsign and Tab does
    /// nothing). Enter keeps its meaning: activate the selected entry.</summary>
    public static MenuKeyStep MainMenuKeys(bool upPressed, bool downPressed, bool enterPressed,
        bool tabPressed, bool tabAllowed) =>
        new((downPressed ? 1 : 0) - (upPressed ? 1 : 0),
            enterPressed,
            tabPressed && tabAllowed);

    /// <summary>P946 — wrap a menu/list cursor by <paramref name="delta"/>.</summary>
    public static int MoveSelection(int index, int count, int delta)
    {
        if (count <= 0) return 0;
        return ((index + delta) % count + count) % count;
    }

    /// <summary>P946 — printable characters keep going to the pilot-name field
    /// exactly as before: only while the field owns typed text.</summary>
    public static bool TypesPilotName(char c, bool pilotFocused) =>
        pilotFocused && c is >= ' ' and not '\u007f';

    /// <summary>P946 — the main menu's one-line hint. The Tab part disappears
    /// when Tab does nothing (signed-in identity account).</summary>
    public static string MainHint(bool tabAllowed) =>
        "Type your pilot name   UP/DOWN choose   ENTER select"
        + (tabAllowed ? "   TAB switch field" : "")
        + "   ESC quit";

    /// <summary>P946 — Find Server: the password field owns typed text while it
    /// is open (Tab toggles it, introscreen.cpp's commented-out edit pane is
    /// the native ancestor; the port keeps the field). Up/Down stay on the
    /// server list either way.</summary>
    public static TextField FindServerFocus(bool passwordEditing) =>
        passwordEditing ? TextField.ServerPassword : TextField.None;

    /// <summary>P946 — Direct Connect: the password field wins when open, else
    /// the address field (which is focused on entry).</summary>
    public static TextField DirectConnectFocus(bool addressEditing, bool passwordEditing) =>
        passwordEditing ? TextField.ServerPassword
        : addressEditing ? TextField.DirectAddress
        : TextField.None;

    /// <summary>P946 — the Find Server view's N (new game) shortcut must not
    /// fire while the password field is typing: a password containing 'n' was
    /// silently opening the New Game wizard.</summary>
    public static bool FindServerNewGameKey(TextField focus) => focus != TextField.ServerPassword;

    /// <summary>P946 — Find Server's hint, naming the field that owns typing.
    /// P970 adds "(required)" when the selected game advertises a password.</summary>
    public static string FindServerHint(bool passwordFocused, bool passwordRequired = false) => passwordFocused
        ? "TYPE password" + (passwordRequired ? " (this game requires it)" : "")
          + "   TAB back to list   UP/DOWN select   ENTER join   ESC back (rescanning every 1 s)"
        : "UP/DOWN select   ENTER join   TAB password" + (passwordRequired ? " (required)" : "")
          + "   ESC back (rescanning every 1 s)";

    /// <summary>P946 — Direct Connect's hint, naming the field that owns typing.</summary>
    public static string DirectConnectHint(bool passwordFocused) => passwordFocused
        ? "TYPE password   TAB back to address   ENTER connect   ESC back"
        : "TYPE address   ENTER connect   TAB password   ESC back";

    /// <summary>Map the highlighted (or given) menu index to the command it
    /// issues — the single-player hook, the lobby, Find Server, etc.</summary>
    public static IntroCommand Activate(int index) => index switch
    {
        0 => IntroCommand.SinglePlayer,
        1 => IntroCommand.Lobby,
        2 => IntroCommand.FindServer,
        3 => IntroCommand.DirectConnect,
        4 => IntroCommand.Options,
        5 => IntroCommand.Credits,
        6 => IntroCommand.ReplayIntro,
        7 => IntroCommand.Help,
        8 => IntroCommand.Exit,
        9 => IntroCommand.Benchmark,
        10 => IntroCommand.Career,
        11 => IntroCommand.HostLan,
        12 => IntroCommand.HostInternet,
        _ => IntroCommand.None,
    };

    /// <summary>Restore a remembered endpoint without losing its port or IPv6 brackets.</summary>
    public static string DirectAddress(string host, int port)
    {
        if (port is < 1 or > ushort.MaxValue) return host;
        string address = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        return address + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Parse a direct-connect <c>host[:port]</c> string (hostname,
    /// IPv4/IPv6, or MagicDNS name). No port → <paramref name="defaultPort"/>.
    /// An IPv6 literal with brackets is handled (<c>[::1]:2326</c>); a bare
    /// <c>::1</c> is taken as a bare IPv6 (no port). Returns false on empty.</summary>
    public static bool TryParseDirect(string text, ushort defaultPort, out string host, out ushort port)
    {
        host = ""; port = defaultPort;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        // Bracketed IPv6: [addr]:port or [addr].
        if (t.StartsWith('['))
        {
            int close = t.IndexOf(']');
            if (close < 0) return false;
            host = t[1..close];
            string rest = t[(close + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':')) return false;
                if (!ushort.TryParse(rest[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port == 0)
                    return false;
            }
            return host.Length > 0;
        }
        // A bare IPv6 literal has multiple colons and no brackets — it is the
        // whole host (no port). Single-colon forms are host:port.
        int colons = 0;
        foreach (var c in t) if (c == ':') colons++;
        if (colons >= 2)
        {
            host = t;
            return host.Length > 0;
        }
        int colon = t.LastIndexOf(':');
        if (colon > 0)
        {
            string rhs = t[(colon + 1)..];
            bool portLike = rhs.Length > 0 && rhs.All(char.IsDigit);
            if (portLike && ushort.TryParse(rhs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0)
            {
                host = t[..colon];
                port = p;
                return host.Length > 0;
            }
            return false;   // "host:abc" or "host:" — malformed port
        }
        host = t;
        return host.Length > 0;
    }
}
