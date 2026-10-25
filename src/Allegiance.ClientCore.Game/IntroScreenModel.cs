using System.Globalization;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>The command an intro-screen main-menu button activation issues.
/// Faithful mapping of native's intro-screen buttons
/// (WinTrek/introscreen.cpp): SinglePlayer ← trainBigButtonPane
/// (introscreen.cpp:1221)/OnButtonTraining(:1558); Lobby ← playIntButtonPane
/// (:1213)/OnButtonInternet(:1822); FindServer ← playLanButtonPane
/// (:1212)/OnButtonLAN(:1832, opens the FindServerPopup); Options ←
/// optionsButtonPane (:1223)/OnButtonOptions(:1847); Credits ← creditsButtonPane
/// (:1225)/OnButtonCredits(:1853); Exit ← exitButtonPane (:1238)/OnButtonExit
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
    /// (:1226)/OnButtonHelp(:1806, → GetWindow()-&gt;OnHelp(true)) join the menu
    /// after Credits, matching the native button set.</summary>
    public static readonly string[] MainLabels =
    {
        "Single Player", "Play Online", "Play LAN / Find Server",
        "Direct Connect", "Options", "Credits", "Replay Intro", "Help",
        "Exit", "Showcase", "Career",
    };

    /// <summary>The currently highlighted main-menu index.</summary>
    public int Index;

    public int Count => MainLabels.Length;

    public void MoveUp() => Index = (Index - 1 + MainLabels.Length) % MainLabels.Length;
    public void MoveDown() => Index = (Index + 1) % MainLabels.Length;

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
