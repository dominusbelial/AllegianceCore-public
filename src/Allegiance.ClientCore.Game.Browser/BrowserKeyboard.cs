using System.Collections.ObjectModel;
using Microsoft.Xna.Framework.Input;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>DOM physical codes, not localized key text or XNA enum-name guesses.
/// Platform adaptation only; native command names: WinTrek/trekinput.cpp:176-186,252-264.</summary>
public static class BrowserKeyboard
{
    public static IReadOnlyDictionary<string, Keys> Codes { get; } = Build();
    public static Keys Parse(string code) => Codes.GetValueOrDefault(code, Keys.None);
    public static KeyboardState State(IEnumerable<string> codes) => new(codes.Select(Parse).Where(k => k != Keys.None).Distinct().ToArray());

    private static IReadOnlyDictionary<string, Keys> Build()
    {
        var codes = new Dictionary<string, Keys>(StringComparer.Ordinal)
        {
            ["ArrowUp"] = Keys.Up, ["ArrowDown"] = Keys.Down, ["ArrowLeft"] = Keys.Left, ["ArrowRight"] = Keys.Right,
            ["Backspace"] = Keys.Back, ["Enter"] = Keys.Enter, ["NumpadEnter"] = Keys.Enter,
            ["Tab"] = Keys.Tab, ["Space"] = Keys.Space, ["Escape"] = Keys.Escape,
            ["ShiftLeft"] = Keys.LeftShift, ["ShiftRight"] = Keys.RightShift,
            ["ControlLeft"] = Keys.LeftControl, ["ControlRight"] = Keys.RightControl,
            ["AltLeft"] = Keys.LeftAlt, ["AltRight"] = Keys.RightAlt,
            ["MetaLeft"] = Keys.LeftWindows, ["MetaRight"] = Keys.RightWindows, ["ContextMenu"] = Keys.Apps,
            ["CapsLock"] = Keys.CapsLock, ["NumLock"] = Keys.NumLock, ["ScrollLock"] = Keys.Scroll,
            ["Pause"] = Keys.Pause, ["PrintScreen"] = Keys.PrintScreen,
            ["Insert"] = Keys.Insert, ["Delete"] = Keys.Delete, ["Home"] = Keys.Home, ["End"] = Keys.End,
            ["PageUp"] = Keys.PageUp, ["PageDown"] = Keys.PageDown,
            ["Equal"] = Keys.OemPlus, ["Minus"] = Keys.OemMinus, ["Backquote"] = Keys.OemTilde,
            ["Backslash"] = Keys.OemPipe, ["IntlBackslash"] = Keys.OemBackslash,
            ["BracketLeft"] = Keys.OemOpenBrackets, ["BracketRight"] = Keys.OemCloseBrackets,
            ["Comma"] = Keys.OemComma, ["Period"] = Keys.OemPeriod, ["Slash"] = Keys.OemQuestion,
            ["Semicolon"] = Keys.OemSemicolon, ["Quote"] = Keys.OemQuotes,
            ["NumpadAdd"] = Keys.Add, ["NumpadSubtract"] = Keys.Subtract,
            ["NumpadMultiply"] = Keys.Multiply, ["NumpadDivide"] = Keys.Divide, ["NumpadDecimal"] = Keys.Decimal,
        };
        for (int i = 0; i < 26; i++) codes["Key" + (char)('A' + i)] = (Keys)((int)Keys.A + i);
        for (int i = 0; i < 10; i++)
        {
            codes["Digit" + i] = (Keys)((int)Keys.D0 + i);
            codes["Numpad" + i] = (Keys)((int)Keys.NumPad0 + i);
        }
        for (int i = 1; i <= 24; i++) codes["F" + i] = (Keys)((int)Keys.F1 + i - 1);
        return new ReadOnlyDictionary<string, Keys>(codes);
    }
}

/// <summary>Conservative browser defaults: an OS/browser-owned chord is not a
/// usable game default even on browsers which sometimes allow its cancellation.
/// Ctrl-as-fire also makes ordinary held W/T/N become close/new-tab/new-window.
/// These are browser adaptations, not changes to native or desktop defaults.</summary>
public static class BrowserKeyboardBindings
{
    public sealed record Reserved(string Action, KeyBinding Replacement, string Reason);
    public static IReadOnlyList<Reserved> ReservedDefaults { get; } = Array.AsReadOnly(new Reserved[]
    {
        new("FireMissile", new(Keys.CapsLock, 0), "Holding Ctrl with W/T/N can close the tab or open a tab/window; CapsLock is unbound beside the weapon/boost keys."),
        new("VoteNo", new(Keys.N, InputModifiers.Alt), "Ctrl+N opens a browser window; retain the N mnemonic."),
        new("TeamPane", new(Keys.T, InputModifiers.Alt), "Ctrl+T opens a browser tab; retain the T mnemonic."),
        new("TargetFriendlyPrev", new(Keys.T, InputModifiers.Alt), "Native preset Ctrl+T opens a browser tab; retain the T mnemonic."),
        new("ViewFlyby", new(Keys.D5, InputModifiers.Shift), "F5/Shift+F5 reload the page; use the free shifted view-number row."),
        new("ToggleInvestmentPane", new(Keys.D8, InputModifiers.Shift), "Native preset F5 reloads the page."),
        new("ViewExternalStation", new(Keys.D6, InputModifiers.Shift), "F11 is browser fullscreen."),
        new("Credits", new(Keys.F9, InputModifiers.Shift), "F12 opens developer tools."),
        new("CycleHudSkin", new(Keys.D0, InputModifiers.Shift), "Shift+F12 may open developer tools."),
        new("IncreaseTMClockSpeed", new(Keys.Add, InputModifiers.Shift), "Ctrl+F11 is not portable through browser/OS fullscreen handling."),
        new("ResetTMClockSpeed", new(Keys.Decimal, InputModifiers.Shift), "Ctrl+F12 may open developer tools."),
        new("ScreenShot", new(Keys.F2, InputModifiers.Shift), "PrintScreen belongs to the OS capture tool and may not reach the page."),
        new("ConModeCombat", new(Keys.F1, InputModifiers.Shift), "Alt+F2 may open the OS command launcher."),
        new("ConModeGameState", new(Keys.D9, InputModifiers.Shift), "Alt+F7 may start OS window movement."),
        new("ConModeMiniTeam", new(Keys.D7, InputModifiers.Shift), "Alt+F6 may switch OS windows."),
        new("Quit", new(Keys.F8, InputModifiers.Shift), "Alt+F4 closes the browser window."),
    });

    public static string? ReservedReason(KeyBinding chord)
    {
        if (chord.Key is Keys.F5 or Keys.F11 or Keys.F12 or Keys.PrintScreen) return "Browser/OS function key; use the browser default shown in Controls.";
        if (chord.Modifiers.HasFlag(InputModifiers.Alt) && chord.Key is >= Keys.F1 and <= Keys.F10)
            return "Alt+function key may be owned by the OS window manager.";
        if (chord.Modifiers.HasFlag(InputModifiers.Control) && chord.Key is Keys.W or Keys.T or Keys.N or Keys.Tab)
            return "Browser tab/window management cannot reliably be cancelled.";
        return null;
    }

    public static void ApplyDefaults(Dictionary<string, KeyBinding> defaults)
    {
        foreach (var rule in ReservedDefaults)
        {
            var current = defaults[rule.Action];
            if (ReservedReason(current) is not null || rule.Action == "FireMissile" && current.Key == Keys.LeftControl)
                defaults[rule.Action] = rule.Replacement;
        }
    }

    public static string Label(string action, KeyBinding binding) => binding.Label
        + (ReservedDefaults.Any(r => r.Action == action && r.Replacement == binding) ? " (browser)" : "");
}
