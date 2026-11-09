using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P970 — the shared "this game needs a password" model every join screen uses
/// (join link, Find Server, Direct Connect, the server browser). One model, both
/// HUD styles: <see cref="HudStyle"/> only changes fonts/colours, never this text
/// or these focus rules, so Classic and Modern always agree.
/// <para><b>Native intent.</b> Native had no "password required" flag on the
/// lobby or game list (<c>lobby/messagesall.h:25-63</c> carries no password), so
/// its join always tried the empty password and only after
/// <c>DPR_BadPassword</c> popped a password dialog with the field focused and the
/// last attempt pre-typed (<c>WinTrek/gamescreen.cpp:1498-1505</c>,
/// <c>WinTrek/passworddialog.cpp:57-68</c>). This port keeps that refusal path
/// (the authoritative check stays on the server,
/// <c>FedSrv/FedSrv.CPP:648-661</c>) and adds the advertised flag so the field is
/// focused and explained BEFORE the wasted round trip.</para>
/// <para>Everything here is pure string/focus logic so it is unit-testable
/// without a graphics device and identical in both HUD styles. All text is
/// ASCII: the bundled HUD fonts bake ASCII 32..126 only.</para>
/// </summary>
public static class JoinPasswordPrompt
{
    /// <summary>The port's edit-box cap (<c>OnTextInput</c> refuses the 16th
    /// character). Native's own pane allows <c>c_cbGamePassword - 1</c> = 16
    /// (<c>WinTrek/passworddialog.cpp:41</c>); 15 is what this client has always
    /// accepted, so the counter reports that number rather than promising one
    /// more character than the field takes.</summary>
    public const int MaxLength = 15;

    /// <summary>The "remember for this server" key. A FUNCTION key on purpose: the
    /// password field owns typed text while it is focused, so a printable key (M)
    /// would be typed into the password instead of ticking the box. F8 is free on
    /// every join screen (the intro and browser read Up/Down/Enter/TAB/Esc/N/F/L/P/I
    /// raw or through the action map; F8 is only bound in flight).</summary>
    public const string RememberKey = "F8";

    /// <summary>Does the field own typing the moment this join screen opens? A
    /// password-protected game with nothing typed yet opens with the password
    /// field focused (native focused it on refusal, passworddialog.cpp:61-65).</summary>
    public static bool FocusOnOpen(bool passwordRequired, int typedLength)
        => passwordRequired && typedLength == 0;

    /// <summary>The plain statement of the requirement (one line, ASCII). Empty
    /// when the game advertises no password, so an open game's screen is
    /// byte-identical to the pre-P970 one.</summary>
    public static string RequirementLine(bool passwordRequired)
        => passwordRequired
            ? "This game requires a password (case-sensitive, max "
              + MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + " chars)."
            : "";

    /// <summary>The edit line with the character count, e.g.
    /// <c>Password: ** (2/15)_</c>. <paramref name="focused"/> adds the caret;
    /// <paramref name="label"/> is the screen's own prefix ("Password", "TAB
    /// password") so each screen keeps its idiom.</summary>
    public static string EditLine(bool passwordRequired, string label, int typedLength, bool focused)
    {
        var masked = new string('*', Math.Clamp(typedLength, 0, MaxLength));
        var count = " (" + Math.Clamp(typedLength, 0, MaxLength).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "/" + MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
        return label + ": " + masked + count + (focused ? "_" : "");
    }

    /// <summary>
    /// What ENTER means on a join screen whose password box has focus. A REQUIRED
    /// password submits on ENTER — native's password dialog treats Enter as its OK
    /// button (<c>WinTrek/passworddialog.cpp:91-95</c>). An OPTIONAL password keeps
    /// the port's long-standing two-step edit (ENTER finishes typing, a second
    /// ENTER joins), so a stray key never joins with half a password typed.
    /// </summary>
    public static bool EnterSubmits(bool editing, bool passwordRequired) => !editing || passwordRequired;

    /// <summary>The footer hint: names TAB/ENTER and, when the game is protected,
    /// says the password is needed (native's dialog told the player nothing but
    /// the field label; this says it plainly).</summary>
    public static string Hint(bool passwordRequired, bool focused)
    {
        if (passwordRequired)
            return focused
                ? "TYPE the password   BACKSPACE delete   ENTER join   ESC cancel   F8 remember for this server"
                : "TAB password (required by this game)   ENTER join   ESC cancel   F8 remember for this server";
        return focused
            ? "TYPE the password   BACKSPACE delete   ENTER join   ESC cancel"
            : "UP/DOWN select   ENTER join   TAB password   ESC cancel";
    }

    /// <summary>The opt-in checkbox line (default OFF; "saved" means a stored value
    /// exists for this server right now). ASCII only.</summary>
    public static string RememberLine(bool remember, bool saved)
        => RememberKey + " remember password for this server: " + (remember ? "ON" : "OFF")
           + (saved ? "  (saved)" : "");

    /// <summary>The opt-in line's help: how to forget. Turning the box OFF deletes
    /// the stored value, which is the join screen's "forget saved password".</summary>
    public static string RememberHint(bool remember, bool saved)
        => saved && !remember
            ? RememberKey + " will forget the saved password for this server."
            : remember
                ? "The password is kept in a user-only file next to player.json."
                : "The password is not saved for this server.";

    /// <summary>The message a refused join shows on the join screen. Reuses the
    /// native refusal text (<c>DPR_BadPassword</c>,
    /// <see cref="LobbyStatusLayout.RequestFailureText"/>) plus the retry
    /// instruction, because this port (like native's game list,
    /// gamescreen.cpp:1498-1505 <c>bDisconnect = false</c>) lets the player retype
    /// in place.</summary>
    public static string RefusalMessage(string? detail = null)
        => "The game password is incorrect." + (string.IsNullOrWhiteSpace(detail) ? "" : " " + detail)
           + " Type it again and press ENTER.";

    /// <summary>The refusal text for a wire reason: only <c>DPR_BadPassword</c> is a
    /// password problem; the caller decides whether to bounce to the join screen.</summary>
    public static bool IsPasswordRefusal(DelPositionReqReason reason) => reason == DelPositionReqReason.BadPassword;
}
