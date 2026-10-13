using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P625 - the catalogue of every rebindable command the client implements.
///
/// <see cref="Default"/> is the binding the port actually ships today (kept -
/// the port's WASD/pitch-yaw-roll flight scheme is a deliberate design, not a
/// native omission; see the P625 report "Native behaviour classified" table).
/// <see cref="NativeDefault"/> is what <c>defaultinputmap.mdl</c> assigns to
/// the same TK command (decoded via <see cref="DefaultInputMap"/>) and is shown
/// on the rebind popup as a suggested native binding. Categories mirror native's
/// <c>g_pCommandInfo</c> groupings (trekinput.cpp:1441-1621) translated to the
/// port's established help headings.
///
/// NOTE the two exceptions the P625 brief mandates (Domingo 2026-10-12):
///   - cameras keep the native F-row (F4 orbit, F5 flyby, F7 target,
///     F10 base, F11 station) - "native wins the F-keys";
///   - screenshot moved from the old port F11 to PrintScreen (native's own
///     TK_ScrnShot default, defaultinputmap.mdl line 108);
///   - music volume moved OFF the F-row to a free Ctrl chord (Ctrl + = / -).
/// </summary>
public static class InputActionCatalog
{
    public static readonly string[] CategoryOrder =
    {
        "MOVEMENT", "CRUISE THROTTLE", "WEAPONS", "TARGETING / AUTOPILOT",
        "QUEUED ORDERS (RECEIVED COMMAND)", "COMMANDED SHIP (DRONE)",
        "CAMERA / VIEW", "MUSIC", "CHAT", "LOBBY (SERVER BROWSER)",
        "READY CHECK (PRE-FLIGHT)",
        "PANES / MENUS", "FREE-CAM / SPECTATOR", "SYSTEM",
    };

    // ---- convenient chord builders -----------------------------------------
    private static KeyBinding K(Keys k) => new(k, InputModifiers.None);
    private static KeyBinding S(Keys k) => new(k, InputModifiers.Shift);
    private static KeyBinding C(Keys k) => new(k, InputModifiers.Control);
    private static KeyBinding A(Keys k) => new(k, InputModifiers.Alt);
    /// <summary>Unbound — a rebindable command with no default key (native's
    /// defaultinputmap.mdl gives no key to TK_TargetHostile, TK_Target{Enemy|Friendly}
    /// {Fighter|Constructor|MajorBase}*, &amp;c.; they exist as commands, listed in
    /// help, and the user may bind them via F6).</summary>
    private static KeyBinding U() => new(Keys.None, InputModifiers.None);

    private static readonly InputActionDef[]
        D = new InputActionDef[]
    {
        // -- MOVEMENT (port scheme; native uses thrust axes + mouse - see report) --
        new("ThrustForward", "MOVEMENT", "Throttle forward (held thrust) [TK_ThrustForward]", K(Keys.W)),
        new("ThrustBackward", "MOVEMENT", "Throttle backward (held thrust) [TK_ThrustBackward]", K(Keys.S)),
        new("ThrustLeft", "MOVEMENT", "Strafe left [TK_ThrustLeft]", K(Keys.Q)),
        new("ThrustRight", "MOVEMENT", "Strafe right [TK_ThrustRight]", K(Keys.E)),
        new("PitchUp", "MOVEMENT", "Pitch up [TK_PitchUp]", K(Keys.Up)),
        new("PitchDown", "MOVEMENT", "Pitch down [TK_PitchDown]", K(Keys.Down)),
        new("YawLeft", "MOVEMENT", "Yaw left [TK_YawLeft]", K(Keys.Left)),
        new("YawRight", "MOVEMENT", "Yaw right [TK_YawRight]", K(Keys.Right)),
        new("RollLeft", "MOVEMENT", "Roll left [TK_RollLeft]", K(Keys.A)),
        new("RollRight", "MOVEMENT", "Roll right [TK_RollRight]", K(Keys.D)),
        new("FireBooster", "MOVEMENT", "Afterburner boost (hold) [TK_FireBooster]", K(Keys.LeftShift)),

        // -- CRUISE THROTTLE (native bindings already, defaultinputmap.mdl:25-32) --
        new("ThrottleUp", "CRUISE THROTTLE", "Cruise throttle up (persistent, 20% steps) [TK_ThrottleUp]", K(Keys.OemPlus), K(Keys.OemPlus)),
        new("ThrottleDown", "CRUISE THROTTLE", "Cruise throttle down (persistent, 20% steps) [TK_ThrottleDown]", K(Keys.OemMinus), K(Keys.OemMinus)),
        new("ThrottleZero", "CRUISE THROTTLE", "Throttle ZERO (stop) [TK_ThrottleZero]", K(Keys.Back), K(Keys.Back)),
        new("ThrottleFull", "CRUISE THROTTLE", "Throttle FULL (either \\ key) [TK_ThrottleFull]", K(Keys.OemBackslash), K(Keys.OemBackslash)),
        new("MouseWheel", "CRUISE THROTTLE", "Mouse wheel cruise throttle (native wheel map)", new KeyBinding(Keys.None, InputModifiers.None)),

        // -- WEAPONS (native defaults where the port binds a key; FireWeapon is
        //    native mouse-left, so the port's Space is its own binding) --
        new("FireWeapon", "WEAPONS", "Fire weapons (hold) [TK_FireWeapon]", K(Keys.Space), null),
        new("FireMissile", "WEAPONS", "Fire missile (hold) [TK_FireMissile]", K(Keys.LeftControl), null),
        new("ToggleGroupFire", "WEAPONS", "Group-fire / single toggle [TK_ToggleGroupFire]", K(Keys.OemTilde), null),
        new("PrevWeapon", "WEAPONS", "Previous weapon [TK_PrevWeapon]", K(Keys.OemOpenBrackets), K(Keys.OemOpenBrackets)),
        new("NextWeapon", "WEAPONS", "Next weapon [TK_NextWeapon]", K(Keys.OemCloseBrackets), K(Keys.OemCloseBrackets)),
        new("DropChaff", "WEAPONS", "Drop chaff [TK_DropChaff]", K(Keys.C), K(Keys.Z)),
        new("ToggleCloak", "WEAPONS", "Toggle cloak [TK_ToggleCloak]", K(Keys.F), K(Keys.G)),
        new("Ripcord", "WEAPONS", "Ripcord to selected starmap sector / cancel [TK_Ripcord]", K(Keys.L), C(Keys.R)),
        new("DropMine", "WEAPONS", "Drop mine / probe [TK_DropMine]", K(Keys.N), K(Keys.C)),
        new("Suicide", "WEAPONS", "Suicide [TK_Suicide] - ejects from a turret as gunner", A(Keys.K), new KeyBinding(Keys.D, InputModifiers.Shift | InputModifiers.Control)),
        new("PromoteTurret1", "WEAPONS", "Promote turret gunner seat 1 to command [TK_PromoteTurret1]", K(Keys.D1), K(Keys.D1)),
        new("PromoteTurret2", "WEAPONS", "Promote turret gunner seat 2 to command [TK_PromoteTurret2]", K(Keys.D2), K(Keys.D2)),
        new("PromoteTurret3", "WEAPONS", "Promote turret gunner seat 3 to command [TK_PromoteTurret3]", K(Keys.D3), K(Keys.D3)),
        new("PromoteTurret4", "WEAPONS", "Promote turret gunner seat 4 to command [TK_PromoteTurret4]", K(Keys.D4), K(Keys.D4)),

        // -- TARGETING / AUTOPILOT --
        new("Target", "TARGETING / AUTOPILOT", "Cycle target (nearest first) [TK_Target]", K(Keys.T), K(Keys.T)),
        new("ToggleAutopilot", "TARGETING / AUTOPILOT", "Toggle autopilot to target [TK_ToggleAutoPilot]", K(Keys.G), K(Keys.P)),
        new("TargetEnemyNearest", "TARGETING / AUTOPILOT", "Nearest enemy ship (repeat: next) [TK_TargetEnemyNearest]", K(Keys.U), K(Keys.F)),
        new("TargetFriendlyNearest", "TARGETING / AUTOPILOT", "Nearest friendly ship (repeat: next) [TK_TargetFriendlyNearest]", S(Keys.U), S(Keys.F)),
        new("TargetEnemyBaseNearest", "TARGETING / AUTOPILOT", "Nearest enemy base, ANY sector (repeat: next) [TK_TargetEnemyBaseNearest]", K(Keys.Z), K(Keys.U)),
        new("TargetFriendlyBaseNearest", "TARGETING / AUTOPILOT", "Nearest friendly base, ANY sector (repeat: next) [TK_TargetFriendlyBaseNearest]", S(Keys.Z), S(Keys.U)),
        new("TargetNothing", "TARGETING / AUTOPILOT", "Target nothing (clear target / autopilot) [TK_TargetNothing]", K(Keys.OemSemicolon), K(Keys.OemSemicolon)),
        new("TargetCenter", "TARGETING / AUTOPILOT", "Target center (nearest to reticle) [TK_TargetCenter]", K(Keys.OemQuotes), K(Keys.OemQuotes)),
        // P623 — CLASS-FILTERED targeting: the 33 TK_Target{Enemy|Friendly}
        // {Bomber|Fighter|Miner|Constructor|Lifepod|MajorBase}[Nearest|Prev]
        // commands (igc.h:377-415). Each class resolves to a Habm/Sabm ability-bit
        // filter (WinTrek.cpp:10162-10331 → FindableModel common.cpp:291-321), and
        // each command steps the selection per native's nearest/next/previous triad
        // (TrekFindTarget WinTrek.cpp:8963-8969). Default keys follow native's
        // defaultinputmap.mdl where it assigns one (O/I/K/H/J × plain/Shift/Ctrl =
        // next/nearest/prev); the classes native leaves unbound (fighter,
        // constructor, major base) are registered here as rebindable commands.
        // Native wins the occupied keys (H/I/J/K) — see the moved bindings in the
        // P623 report (Help→F1, InvertMouse→Ctrl+M, ToggleMusic→Alt+J, Suicide→Alt+K).
        new("TargetEnemyBomber", "TARGETING / AUTOPILOT", "Target next enemy bomber [TK_TargetEnemyBomber]", K(Keys.O), K(Keys.O)),
        new("TargetEnemyBomberNearest", "TARGETING / AUTOPILOT", "Target nearest enemy bomber [TK_TargetEnemyBomberNearest]", S(Keys.O), S(Keys.O)),
        new("TargetEnemyBomberPrev", "TARGETING / AUTOPILOT", "Target previous enemy bomber [TK_TargetEnemyBomberPrev]", C(Keys.O), C(Keys.O)),
        new("TargetEnemyFighter", "TARGETING / AUTOPILOT", "Target next enemy fighter [TK_TargetEnemyFighter]", U(), null),
        new("TargetEnemyFighterNearest", "TARGETING / AUTOPILOT", "Target nearest enemy fighter [TK_TargetEnemyFighterNearest]", U(), null),
        new("TargetEnemyFighterPrev", "TARGETING / AUTOPILOT", "Target previous enemy fighter [TK_TargetEnemyFighterPrev]", U(), null),
        new("TargetEnemyConstructor", "TARGETING / AUTOPILOT", "Target next enemy constructor [TK_TargetEnemyConstructor]", U(), null),
        new("TargetEnemyConstructorNearest", "TARGETING / AUTOPILOT", "Target nearest enemy constructor [TK_TargetEnemyConstructorNearest]", U(), null),
        new("TargetEnemyConstructorPrev", "TARGETING / AUTOPILOT", "Target previous enemy constructor [TK_TargetEnemyConstructorPrev]", U(), null),
        new("TargetEnemyMiner", "TARGETING / AUTOPILOT", "Target next enemy miner [TK_TargetEnemyMiner]", K(Keys.I), K(Keys.I)),
        new("TargetEnemyMinerNearest", "TARGETING / AUTOPILOT", "Target nearest enemy miner [TK_TargetEnemyMinerNearest]", S(Keys.I), S(Keys.I)),
        new("TargetEnemyMinerPrev", "TARGETING / AUTOPILOT", "Target previous enemy miner [TK_TargetEnemyMinerPrev]", C(Keys.I), C(Keys.I)),
        new("TargetEnemyMajorBase", "TARGETING / AUTOPILOT", "Target next enemy major base, ANY sector [TK_TargetEnemyMajorBase]", U(), null),
        new("TargetEnemyMajorBaseNearest", "TARGETING / AUTOPILOT", "Target nearest enemy major base [TK_TargetEnemyMajorBaseNearest]", U(), null),
        new("TargetEnemyMajorBasePrev", "TARGETING / AUTOPILOT", "Target previous enemy major base [TK_TargetEnemyMajorBasePrev]", U(), null),
        new("TargetFriendlyBomber", "TARGETING / AUTOPILOT", "Target next friendly bomber [TK_TargetFriendlyBomber]", K(Keys.K), K(Keys.K)),
        new("TargetFriendlyBomberNearest", "TARGETING / AUTOPILOT", "Target nearest friendly bomber [TK_TargetFriendlyBomberNearest]", S(Keys.K), S(Keys.K)),
        new("TargetFriendlyBomberPrev", "TARGETING / AUTOPILOT", "Target previous friendly bomber [TK_TargetFriendlyBomberPrev]", C(Keys.K), C(Keys.K)),
        new("TargetFriendlyFighter", "TARGETING / AUTOPILOT", "Target next friendly fighter [TK_TargetFriendlyFighter]", U(), null),
        new("TargetFriendlyFighterNearest", "TARGETING / AUTOPILOT", "Target nearest friendly fighter [TK_TargetFriendlyFighterNearest]", U(), null),
        new("TargetFriendlyFighterPrev", "TARGETING / AUTOPILOT", "Target previous friendly fighter [TK_TargetFriendlyFighterPrev]", U(), null),
        new("TargetFriendlyConstructor", "TARGETING / AUTOPILOT", "Target next friendly constructor [TK_TargetFriendlyConstructor]", U(), null),
        new("TargetFriendlyConstructorNearest", "TARGETING / AUTOPILOT", "Target nearest friendly constructor [TK_TargetFriendlyConstructorNearest]", U(), null),
        new("TargetFriendlyConstructorPrev", "TARGETING / AUTOPILOT", "Target previous friendly constructor [TK_TargetFriendlyConstructorPrev]", U(), null),
        new("TargetFriendlyMiner", "TARGETING / AUTOPILOT", "Target next friendly miner [TK_TargetFriendlyMiner]", K(Keys.H), K(Keys.H)),
        new("TargetFriendlyMinerNearest", "TARGETING / AUTOPILOT", "Target nearest friendly miner [TK_TargetFriendlyMinerNearest]", S(Keys.H), S(Keys.H)),
        new("TargetFriendlyMinerPrev", "TARGETING / AUTOPILOT", "Target previous friendly miner [TK_TargetFriendlyMinerPrev]", C(Keys.H), C(Keys.H)),
        new("TargetFriendlyLifepod", "TARGETING / AUTOPILOT", "Target next friendly lifepod [TK_TargetFriendlyLifepod]", K(Keys.J), K(Keys.J)),
        new("TargetFriendlyLifepodNearest", "TARGETING / AUTOPILOT", "Target nearest friendly lifepod [TK_TargetFriendlyLifepodNearest]", S(Keys.J), S(Keys.J)),
        new("TargetFriendlyLifepodPrev", "TARGETING / AUTOPILOT", "Target previous friendly lifepod [TK_TargetFriendlyLifepodPrev]", C(Keys.J), C(Keys.J)),
        new("TargetFriendlyMajorBase", "TARGETING / AUTOPILOT", "Target next friendly major base, ANY sector [TK_TargetFriendlyMajorBase]", U(), null),
        new("TargetFriendlyMajorBaseNearest", "TARGETING / AUTOPILOT", "Target nearest friendly major base [TK_TargetFriendlyMajorBaseNearest]", U(), null),
        new("TargetFriendlyMajorBasePrev", "TARGETING / AUTOPILOT", "Target previous friendly major base [TK_TargetFriendlyMajorBasePrev]", U(), null),
        new("TargetHostile", "TARGETING / AUTOPILOT", "Target hostile (who last damaged you) [TK_TargetHostile]", U(), null),

        // -- QUEUED ORDERS (RECEIVED COMMAND) --
        new("AcceptCommand", "QUEUED ORDERS (RECEIVED COMMAND)", "Accept the pending order [TK_AcceptCommand]", K(Keys.Home), K(Keys.OemOpenBrackets)),
        new("RejectCommand", "QUEUED ORDERS (RECEIVED COMMAND)", "Reject / decline the pending order [TK_RejectCommand]", K(Keys.End), K(Keys.OemCloseBrackets)),
        new("ClearCommand", "QUEUED ORDERS (RECEIVED COMMAND)", "Clear whatever order is already accepted [TK_ClearCommand]", K(Keys.Delete), K(Keys.Delete)),

        // -- COMMANDED SHIP (DRONE) --
        new("CycleCommandee", "COMMANDED SHIP (DRONE)", "Cycle which same-side ship/drone you command", K(Keys.PageDown), null),
        new("IssueBuild", "COMMANDED SHIP (DRONE)", "Issue order: commandee builds targeted asteroid", K(Keys.Insert), null),
        new("LeaderCommand", "COMMANDED SHIP (DRONE)", "Leader-only: command the selected same-side bot to dock / launch", K(Keys.X), null),

        // -- CAMERA / VIEW (native wins the F-row) --
        new("CycleView", "CAMERA / VIEW", "Cycle view: cockpit -> chase -> turret -> missile-cam", K(Keys.V), K(Keys.V)),
        new("ViewExternalOrbit", "CAMERA / VIEW", "External orbit camera [TK_ViewExternalOrbit]", K(Keys.F4), null),
        new("ViewFlyby", "CAMERA / VIEW", "External fly-by camera [TK_ViewFlyby]", K(Keys.F5), null),
        new("ViewTarget", "CAMERA / VIEW", "External target camera [TK_ViewTarget]", K(Keys.F7), null),
        new("ViewBase", "CAMERA / VIEW", "External base camera [TK_ViewBase]", K(Keys.F10), null),
        new("ViewExternalStation", "CAMERA / VIEW", "External station camera [TK_ViewExternalStation]", K(Keys.F11), null),
        new("FreeCam", "CAMERA / VIEW", "Free-flight spectator camera (detached) [P612]", K(Keys.F8), null),
        new("OrbitCam", "CAMERA / VIEW", "Orbit spectator camera (locks a ship/station) [P612]", K(Keys.F9), null),
        new("Director", "CAMERA / VIEW", "Director b-roll camera (opt-in, hides HUD) [P580]", K(Keys.F2), null),
        new("FollowCam", "CAMERA / VIEW", "Spectator-follow camera [P580]", K(Keys.F3), null),
        new("ToggleHud", "CAMERA / VIEW", "Toggle HUD (hide/show overlays for clean frames) [P612]", C(Keys.F1), null),
        new("ToggleMouse", "CAMERA / VIEW", "Mouse flight toggle [TK_ToggleMouse]", K(Keys.M), K(Keys.V)),
        new("InvertMouse", "CAMERA / VIEW", "Invert mouse pitch (session only)", C(Keys.M), null),
        new("ToggleStarmap", "CAMERA / VIEW", "Expand/collapse the always-on mini-map (full starmap)", K(Keys.Tab), null),
        new("SelectSectorPrev", "CAMERA / VIEW", "Starmap: previous sector (with map expanded)", K(Keys.OemComma), null),
        new("SelectSectorNext", "CAMERA / VIEW", "Starmap: next sector (with map expanded)", K(Keys.OemPeriod), null),
        // P506 — mouse-flight response-curve settings (native menu items,
        // WinTrek.cpp:4534 / AdjustMouseSens :5512; this keyboard port has no
        // menu, so the free digit row D5-D8 carries them).
        new("MouseDeadzone", "CAMERA / VIEW", "Mouse flight: cycle deadzone preset", K(Keys.D5), null),
        new("MouseResponseCurve", "CAMERA / VIEW", "Mouse flight: toggle linear / quadratic response", K(Keys.D6), null),
        new("MouseSensitivityDown", "CAMERA / VIEW", "Mouse flight: sensitivity down", K(Keys.D7), null),
        new("MouseSensitivityUp", "CAMERA / VIEW", "Mouse flight: sensitivity up", K(Keys.D8), null),
        new("Scoreboard", "CAMERA / VIEW", "In-mission scoreboard (kills / deaths)", K(Keys.B), null),
        new("PilotInfo", "CAMERA / VIEW", "Pilot info (your own live stats)", K(Keys.P), null),
        new("Help", "CAMERA / VIEW", "Toggle this help (you are here)", K(Keys.F1), K(Keys.F1)),
        new("OpenKeyBindings", "CAMERA / VIEW", "Open Controls (rebind keys) [P625]", K(Keys.F6), null),
        new("ScreenShot", "CAMERA / VIEW", "Take a screen shot [TK_ScrnShot]", K(Keys.PrintScreen), K(Keys.PrintScreen)),
        new("Quit", "CAMERA / VIEW", "Quit game", new KeyBinding(Keys.F4, InputModifiers.Alt), null),

        // -- MUSIC (moved off the F-row per Domingo; Ctrl + = / - is free) --
        new("ToggleMusic", "MUSIC", "Toggle background music [idmToggleMusic]", A(Keys.J), null),
        new("MusicVolumeUp", "MUSIC", "Music volume up [idmMusicVolumeUp]", C(Keys.OemPlus), null),
        new("MusicVolumeDown", "MUSIC", "Music volume down [idmMusicVolumeDown]", C(Keys.OemMinus), null),

        // -- CHAT --
        new("StartChat", "CHAT", "Open chat / send line [TK_StartChat]", K(Keys.Enter), K(Keys.Enter)),
        new("ChatDelete", "CHAT", "Delete last char in chat", K(Keys.Back), null),
        new("CycleChatTarget", "CHAT", "Cycle chat recipient (while composing)", K(Keys.Tab), null),
        new("QuickChat", "CHAT", "Open quick-chat menu [TK_QuickChatMenu]", K(Keys.R), null),
        new("CancelChat", "CHAT", "Cancel chat / close menu [TK_MainMenu]", K(Keys.Escape), K(Keys.Escape)),

        // -- LOBBY (SERVER BROWSER) --
        new("LobbyUp", "LOBBY (SERVER BROWSER)", "Select server up", K(Keys.Up), null),
        new("LobbyDown", "LOBBY (SERVER BROWSER)", "Select server down", K(Keys.Down), null),
        new("LobbyJoin", "LOBBY (SERVER BROWSER)", "Join selected server", K(Keys.Enter), null),
        new("LobbyFilter", "LOBBY (SERVER BROWSER)", "Find / filter servers by name", K(Keys.F), null),
        new("LobbyRefresh", "LOBBY (SERVER BROWSER)", "Refresh server list", K(Keys.R), null),
        new("LobbyClearFilter", "LOBBY (SERVER BROWSER)", "Clear filter", K(Keys.Escape), null),

        // -- READY CHECK (PRE-FLIGHT) --
        new("ReadyToggle", "READY CHECK (PRE-FLIGHT)", "Toggle your ready (PLAYER_READY)", K(Keys.Y), null),
        new("ForceReady", "READY CHECK (PRE-FLIGHT)", "Force your side ready (mission owner)", K(Keys.O), null),

        // -- PANES / MENUS (docked shop / teleport / invest, side-select) --
        // Generic navigation verbs shared across the docked sub-panes and the
        // side-select / ready-check pickers (the same physical Up/Down/Space/
        // Enter/B row in each context, as P625 noted). Pane-specific verbs get
        // their own actions. Native has no single TK for these convenience
        // bindings (they come from each pane's own key handling).
        new("MenuUp", "PANES / MENUS", "Menu / pane: cursor up", K(Keys.Up), null),
        new("MenuDown", "PANES / MENUS", "Menu / pane: cursor down", K(Keys.Down), null),
        new("MenuSelect", "PANES / MENUS", "Menu / pane: select / activate", K(Keys.Space), null),
        new("MenuConfirm", "PANES / MENUS", "Menu / pane: confirm (Enter)", K(Keys.Enter), null),
        new("MenuBack", "PANES / MENUS", "Menu / pane: back to hangar / previous", K(Keys.B), null),
        new("ShopTabSwitch", "PANES / MENUS", "Docked shop: switch HULLS / PARTS tab", K(Keys.Tab), null),
        new("ShopCycleHullPrev", "PANES / MENUS", "Docked shop: previous hull", K(Keys.Q), null),
        new("ShopCycleHullNext", "PANES / MENUS", "Docked shop: next hull", K(Keys.E), null),
        new("ShopLaunch", "PANES / MENUS", "Docked shop: launch (BUY_LOADOUT)", K(Keys.L), null),
        new("ShopUndock", "PANES / MENUS", "Docked shop: undock without buying", K(Keys.U), null),
        new("ToggleTeleportPane", "PANES / MENUS", "Docked: toggle the teleport pane", K(Keys.T), null),
        new("ToggleInvestmentPane", "PANES / MENUS", "Docked: toggle the investment pane", K(Keys.I), null),
        new("InvestPartial", "PANES / MENUS", "Investment: partial invest (10%)", K(Keys.R), null),
        new("InvestShowAll", "PANES / MENUS", "Investment: show all buckets", K(Keys.S), null),
        new("InvestHideCompleted", "PANES / MENUS", "Investment: hide completed buckets", K(Keys.H), null),
        new("RandomizeTeams", "READY CHECK (PRE-FLIGHT)", "Mission owner: randomize teams (side-select)", K(Keys.R), null),

        // -- FREE-CAM / SPECTATOR (P612 spectator cameras, native has no TK) --
        // Dedicated actions so the spectator free-cam's own WASD/Q-E movement
        // and Shift/Ctrl speed are rebindable independently of ship flight.
        new("FreeCamForward", "FREE-CAM / SPECTATOR", "Free-cam: move forward", K(Keys.W), null),
        new("FreeCamBack", "FREE-CAM / SPECTATOR", "Free-cam: move back", K(Keys.S), null),
        new("FreeCamLeft", "FREE-CAM / SPECTATOR", "Free-cam: move left", K(Keys.A), null),
        new("FreeCamRight", "FREE-CAM / SPECTATOR", "Free-cam: move right", K(Keys.D), null),
        new("FreeCamUp", "FREE-CAM / SPECTATOR", "Free-cam: move up", K(Keys.Q), null),
        new("FreeCamDown", "FREE-CAM / SPECTATOR", "Free-cam: move down", K(Keys.E), null),
        new("FreeCamSpeedUp", "FREE-CAM / SPECTATOR", "Free-cam: speed up (fast)", K(Keys.LeftShift), null),
        new("FreeCamSpeedSlow", "FREE-CAM / SPECTATOR", "Free-cam: speed down (slow)", K(Keys.LeftControl), null),
        new("OrbitSubjectNext", "FREE-CAM / SPECTATOR", "Orbit camera: cycle locked subject", K(Keys.PageUp), null),

        // -- SYSTEM (game-flow / overlay keys that are not flight commands) --
        new("GameOverBack", "SYSTEM", "Game-over: back / leave (Log Off)", K(Keys.Back), null),
        new("Credits", "SYSTEM", "Toggle the Credits / Licences overlay", K(Keys.F12), null),
        new("CreditsScrollUp", "SYSTEM", "Credits overlay: scroll up", K(Keys.PageUp), null),
        new("CreditsScrollDown", "SYSTEM", "Credits overlay: scroll down", K(Keys.PageDown), null),
    };

    /// <summary>The full rebindable action set.</summary>
    public static IReadOnlyList<InputActionDef> Definitions => D;
}
