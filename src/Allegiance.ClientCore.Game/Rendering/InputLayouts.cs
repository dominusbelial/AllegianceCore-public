using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P941 — which default binding set a profile uses. Native's
/// <c>defaultinputmap.mdl</c> (<see cref="Native"/>) is the default for a fresh
/// profile; <see cref="Port"/> is the layout the port shipped before P941,
/// kept as a named, selectable preset so existing players (and the tests that
/// pin those keys) can keep it.
/// </summary>
public enum InputLayout
{
    /// <summary>Native's shipped map: the keyboard table in
    /// <c>Artwork/defaultinputmap.mdl</c> (keyCommandMap, lines 3-107) plus its
    /// mouse/wheel map (mouseButtonCommandMap, lines 109-116).</summary>
    Native = 0,

    /// <summary>The pre-P941 port layout (the <see cref="InputActionDef.Default"/>
    /// chord of every action).</summary>
    Port = 1,
}

/// <summary>One native mouse/wheel binding (mouseButtonCommandMap index,
/// trekinput.cpp:616-645: 0=left, 1=right, 2=middle, 8=wheel-down, 9=wheel-up).</summary>
public readonly record struct MouseButtonBinding(int Button)
{
    public const int Left = 0;
    public const int Right = 1;
    public const int Middle = 2;
    public const int WheelDown = 8;
    public const int WheelUp = 9;

    /// <summary>ASCII label for the F6 list / help overlay (the spritefonts bake
    /// ASCII 32..126 only).</summary>
    public string Label => Button switch
    {
        Left => "Mouse Left",
        Right => "Mouse Right",
        Middle => "Mouse Middle",
        WheelDown => "Wheel Down",
        WheelUp => "Wheel Up",
        _ => $"Mouse {Button}",
    };
}

/// <summary>
/// P941 — the two selectable default binding layouts and the native
/// mouse/wheel map.
///
/// <para><b>Native layout.</b> Every action takes its
/// <see cref="InputActionDef.NativeDefault"/> (the chord decoded from
/// <c>defaultinputmap.mdl</c> by <see cref="DefaultInputMap"/>). Native leaves a
/// large tail of commands unbound (TK_TargetHostile, TK_TargetSelf, the swap/
/// mount commands, …), so for those the port keeps its own established binding —
/// no action loses its only binding. The five commands native wires to the mouse
/// (mouseButtonCommandMap, mdl:109-116) lose their keyboard chord and ride the
/// mouse/wheel instead: left = TK_FireWeapon (55), middle = TK_FireMissile (56),
/// wheel-up = TK_ThrottleUp (58), wheel-down = TK_ThrottleDown (59), right =
/// TK_TargetCenter (90).</para>
///
/// <para><b>Relocations.</b> A handful of port-only commands (native binds no
/// key for them) kept a chord that a native default now claims. Native wins the
/// chord (the established rule throughout P623/P624/P626) and the port command
/// moves to a free chord in the same family — see <see cref="Relocations"/>.
/// The port's own suite keys (F6 Controls, the lobby/shop/invest panes and
/// free-cam movement) share chords with flight actions by context and are left
/// alone, exactly as before P941.</para>
/// </summary>
public static class InputLayouts
{
    /// <summary>The preset names shown in the F6 Controls screen.</summary>
    public const string NativeName = "Native layout";
    public const string PortName = "Port layout";

    public static string Name(InputLayout layout) => layout == InputLayout.Native ? NativeName : PortName;

    /// <summary>The F6 preset cycle order (Native -> Port -> Native).</summary>
    public static InputLayout Next(InputLayout layout) => layout == InputLayout.Native ? InputLayout.Port : InputLayout.Native;

    /// <summary>Native <c>mouseButtonCommandMap</c> (Artwork/defaultinputmap.mdl:109-116)
    /// mapped to the port's actions. Parsed/validated against
    /// <see cref="DefaultInputMap.ParseMouseMap"/> by the P941 fidelity test.</summary>
    public static readonly IReadOnlyList<(int Button, int Tk, string Action)> NativeMouseRows =
    [
        (MouseButtonBinding.Left, 55, "FireWeapon"),       // mdl:111 (0, 55) TK_FireWeapon
        (MouseButtonBinding.Middle, 56, "FireMissile"),    // mdl:112 (2, 56) TK_FireMissile
        (MouseButtonBinding.WheelUp, 58, "ThrottleUp"),    // mdl:113 (9, 58) TK_ThrottleUp
        (MouseButtonBinding.WheelDown, 59, "ThrottleDown"),// mdl:114 (8, 59) TK_ThrottleDown
        (MouseButtonBinding.Right, 90, "TargetCenter"),    // mdl:115 (1, 90) TK_TargetCenter
    ];

    /// <summary>Action -> native mouse/wheel binding (empty for every other
    /// action). The same map applies to both layouts: it is the port's existing
    /// virtual-joystick button behaviour (Game1.MousePick.cs, P587), which already
    /// reads these five commands.</summary>
    public static readonly IReadOnlyDictionary<string, MouseButtonBinding> MouseMap =
        NativeMouseRows.ToDictionary(r => r.Action, r => new MouseButtonBinding(r.Button));

    /// <summary>True when native binds this action to a mouse button/wheel — the
    /// action therefore has no keyboard chord in the native layout.</summary>
    public static bool IsMouseAction(string action) => MouseMap.ContainsKey(action);

    /// <summary>One port-only command that must move off a chord a native default
    /// now owns.</summary>
    public readonly record struct Relocation(string Action, KeyBinding Chord, string NativeRule);

    /// <summary>
    /// Port-only commands relocated by the native layout. Each entry names the
    /// native rule that claims its old chord. No entry is unbound: the command
    /// keeps a reachable binding.
    /// </summary>
    public static readonly IReadOnlyList<Relocation> Relocations =
    [
        // Tab = TK_ThrustForward (mdl:21) and Insert is freed when TK_ThrustUp
        // takes W (mdl:19). The starmap pane is a port convenience key.
        new("ToggleStarmap", new(Keys.Insert, InputModifiers.None),
            "Tab is TK_ThrustForward (defaultinputmap.mdl:21); Insert is free because native TK_ThrustUp takes W (mdl:19)."),

        // Ctrl+digit = native TK_SwapWeapon1-4 (mdl:62-65). PromoteTurret1-4 is
        // a port extension (native turret branch disabled, WinTrek.cpp:9856-9864).
        new("PromoteTurret1", new(Keys.D1, InputModifiers.Control | InputModifiers.Shift),
            "Ctrl+1..4 are native TK_SwapWeapon1-4 (defaultinputmap.mdl:62-65)."),
        new("PromoteTurret2", new(Keys.D2, InputModifiers.Control | InputModifiers.Shift),
            "Ctrl+1..4 are native TK_SwapWeapon1-4 (defaultinputmap.mdl:62-65)."),
        new("PromoteTurret3", new(Keys.D3, InputModifiers.Control | InputModifiers.Shift),
            "Ctrl+1..4 are native TK_SwapWeapon1-4 (defaultinputmap.mdl:62-65)."),
        new("PromoteTurret4", new(Keys.D4, InputModifiers.Control | InputModifiers.Shift),
            "Ctrl+1..4 are native TK_SwapWeapon1-4 (defaultinputmap.mdl:62-65)."),

        // Ctrl+5..8 are native TK_SwapMissile/Chaff/Shield/Cloak (mdl:66-69); the
        // mouse-response settings are port-only (P506).
        new("MouseDeadzone", new(Keys.D5, InputModifiers.Control | InputModifiers.Shift),
            "Ctrl+5..8 are native TK_SwapMissile/Chaff/Shield/Cloak (defaultinputmap.mdl:66-69)."),
        new("MouseResponseCurve", new(Keys.D6, InputModifiers.Control | InputModifiers.Shift),
            "Ctrl+5..8 are native TK_SwapMissile/Chaff/Shield/Cloak (defaultinputmap.mdl:66-69)."),
        new("MouseSensitivityDown", new(Keys.D7, InputModifiers.Control | InputModifiers.Shift),
            "Ctrl+5..8 are native TK_SwapMissile/Chaff/Shield/Cloak (defaultinputmap.mdl:66-69)."),
        new("MouseSensitivityUp", new(Keys.D8, InputModifiers.Control | InputModifiers.Shift),
            "Ctrl+5..8 are native TK_SwapMissile/Chaff/Shield/Cloak (defaultinputmap.mdl:66-69)."),

        // P = native TK_ToggleAutoPilot (mdl:13). PilotInfo is a port addition.
        new("PilotInfo", new(Keys.P, InputModifiers.Control | InputModifiers.Shift),
            "P is native TK_ToggleAutoPilot (defaultinputmap.mdl:13); Ctrl+P stays the profile editor."),

        // Shift+U = native TK_TargetEnemyBaseNearest (mdl:48). ReplacePart and
        // UnmountPart are port commands native leaves unbound (no
        // defaultinputmap.mdl entry); U/Shift+U/Ctrl+U are all native targeting.
        new("ReplacePart", new(Keys.U, InputModifiers.Control | InputModifiers.Shift),
            "Shift+U is native TK_TargetEnemyBaseNearest (defaultinputmap.mdl:48)."),
        new("UnmountPart", new(Keys.U, InputModifiers.Alt),
            "Ctrl+U is native TK_TargetEnemyBasePrev (defaultinputmap.mdl:49)."),

        // Shift+B = native TK_TargetFriendlyBaseNearest (mdl:49); B itself is
        // TK_TargetFriendlyBase (mdl:50). ToggleBooster is declared but unhandled
        // in native (igc.h:287, no WinTrek case), and the scoreboard is a port pane.
        new("ToggleBooster", new(Keys.B, InputModifiers.Control | InputModifiers.Shift),
            "Shift+B is native TK_TargetFriendlyBaseNearest (defaultinputmap.mdl:51)."),
        new("Scoreboard", new(Keys.B, InputModifiers.Alt),
            "B is native TK_TargetFriendlyBase (defaultinputmap.mdl:50); Ctrl+B its previous target (mdl:52)."),

        // '[' / ']' are native TK_AcceptCommand / TK_RejectCommand (mdl:12,14).
        // Native binds no key to TK_PrevWeapon/TK_NextWeapon (igc.h:228-229);
        // Home/End are freed by AcceptCommand/RejectCommand moving to [ / ].
        new("PrevWeapon", new(Keys.Home, InputModifiers.None),
            "[ and ] are native TK_AcceptCommand/TK_RejectCommand (defaultinputmap.mdl:12,14)."),
        new("NextWeapon", new(Keys.End, InputModifiers.None),
            "[ and ] are native TK_AcceptCommand/TK_RejectCommand (defaultinputmap.mdl:12,14)."),

        // Delete is native TK_RejectCommand's alternate (mdl:15). TK_ClearCommand
        // has no native default (igc.h:248), so it rides Shift+Delete.
        new("ClearCommand", new(Keys.Delete, InputModifiers.Shift),
            "Delete is native TK_RejectCommand (defaultinputmap.mdl:15)."),

        // T = native TK_TargetFriendly (mdl:41), Shift/Ctrl variants follow
        // (mdl:42-43). TK_Target itself is unbound in native (igc.h:303), so the
        // port's generic cycle-target command rides Alt+T.
        new("Target", new(Keys.T, InputModifiers.Alt),
            "T is native TK_TargetFriendly (defaultinputmap.mdl:41)."),

        // '/' is native TK_ConModeGameState (mdl:16). TK_MatchSpeed is unbound in
        // native (igc.h:330), so the port keeps it on the same key family with Alt.
        new("MatchSpeed", new(Keys.OemQuestion, InputModifiers.Alt),
            "'/' is native TK_ConModeGameState (defaultinputmap.mdl:16)."),

        // F2/F4/F5/F7 are native console modes (mdl:35,38,39,37). The port's
        // extra cameras (native binds no key for TK_ViewTarget/Flyby/Orbit,
        // igc.h:257-260) keep the F-row with Shift.
        new("Director", new(Keys.F2, InputModifiers.Control),
            "F2 is native TK_ConModeCombat (defaultinputmap.mdl:35)."),
        new("ViewExternalOrbit", new(Keys.F4, InputModifiers.Shift),
            "F4 is native TK_ConModeInventory (defaultinputmap.mdl:38)."),
        new("ViewFlyby", new(Keys.F5, InputModifiers.Shift),
            "F5 is native TK_ConModeInvest (defaultinputmap.mdl:39)."),
        new("ViewTarget", new(Keys.F7, InputModifiers.Shift),
            "F7 is native TK_ConModeNav (defaultinputmap.mdl:37)."),

        // '.' is native TK_CycleRadar (mdl:79). TK_NextPart/PrevPart are
        // declared but unbound in native (igc.h:232-233, no WinTrek handler).
        new("NextPart", new(Keys.OemPeriod, InputModifiers.Alt),
            "'.' is native TK_CycleRadar (defaultinputmap.mdl:79)."),

        // Alt+J is native TK_ToggleLODSlider (mdl:22); music has no TK.
        new("ToggleMusic", new(Keys.J, InputModifiers.Control | InputModifiers.Alt),
            "Alt+J is native TK_ToggleLODSlider (defaultinputmap.mdl:22)."),

        // V = native TK_ToggleMouse (mdl:104). Swapped with CycleView (a port-only
        // view cycle, no native TK) so both stay reachable: V toggles the mouse,
        // M cycles the cockpit/chase view.
        new("CycleView", new(Keys.M, InputModifiers.None),
            "V is native TK_ToggleMouse (defaultinputmap.mdl:104)."),

        // '`' is native TK_QuickChatMenu (mdl:18); TK_ToggleGroupFire has no
        // native default (igc.h:230).
        new("ToggleGroupFire", new(Keys.OemTilde, InputModifiers.Control),
            "'`' is native TK_QuickChatMenu (defaultinputmap.mdl:18)."),

        // Shift+F6 is native TK_ConModeMiniTeam (mdl:101); Shift+F3 is native
        // TK_ToggleCommand (mdl:63) exactly as native itself binds it.
        new("ToggleCommand", new(Keys.F3, InputModifiers.Shift),
            "Shift+F6 is native TK_ConModeMiniTeam (defaultinputmap.mdl:101); Shift+F3 is TK_ToggleCommand (mdl:63)."),
    ];

    private static readonly Dictionary<string, KeyBinding> _relocationMap =
        Relocations.ToDictionary(r => r.Action, r => r.Chord);

    /// <summary>
    /// The effective default chord of every action under
    /// <paramref name="layout"/>. The port layout is the catalogue's own
    /// <see cref="InputActionDef.Default"/>; the native layout is
    /// <see cref="InputActionDef.NativeDefault"/> where native binds a key, the
    /// port chord for the commands native leaves unbound, the mouse map for the
    /// five native mouse commands, and the relocations above.
    /// </summary>
    public static IReadOnlyDictionary<string, KeyBinding> Defaults(InputLayout layout)
    {
        var map = new Dictionary<string, KeyBinding>(StringComparer.Ordinal);
        foreach (var def in InputActionCatalog.Definitions)
        {
            if (layout == InputLayout.Port)
            {
                map[def.Name] = def.Default;
                continue;
            }

            if (IsMouseAction(def.Name))
            {
                // Native binds five commands to the mouse/wheel. FireWeapon,
                // FireMissile and TargetCenter have NO keyboard chord in
                // keyCommandMap (mdl:3-107), so they become mouse-only; the
                // throttle keeps '='/'-' (mdl:29-30) as well as the wheel.
                map[def.Name] = def.NativeDefault ?? new KeyBinding(Keys.None, InputModifiers.None);
                continue;
            }

            var chord = def.NativeDefault ?? def.Default;
            if (_relocationMap.TryGetValue(def.Name, out var moved)) chord = moved;
            map[def.Name] = chord;
        }
        return map;
    }

    /// <summary>The native layout's alternate chords: native binds both '[' and
    /// INSERT to TK_AcceptCommand and both ']' and DELETE to TK_RejectCommand
    /// (defaultinputmap.mdl:12-15), so the second chord is an alternate.</summary>
    public static IReadOnlyList<KeyBinding> Alternates(string action) => action switch
    {
        "AcceptCommand" => [new(Keys.Insert, InputModifiers.None)],
        "RejectCommand" => [new(Keys.Delete, InputModifiers.None)],
        _ => Array.Empty<KeyBinding>(),
    };
}
