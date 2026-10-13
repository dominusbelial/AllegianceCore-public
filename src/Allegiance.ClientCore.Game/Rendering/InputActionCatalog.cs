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
    };

    // ---- convenient chord builders -----------------------------------------
    private static KeyBinding K(Keys k) => new(k, InputModifiers.None);
    private static KeyBinding S(Keys k) => new(k, InputModifiers.Shift);
    private static KeyBinding C(Keys k) => new(k, InputModifiers.Control);
    private static KeyBinding A(Keys k) => new(k, InputModifiers.Alt);

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
        new("Suicide", "WEAPONS", "Suicide [TK_Suicide] - ejects from a turret as gunner", K(Keys.K), new KeyBinding(Keys.D, InputModifiers.Shift | InputModifiers.Control)),
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

        // -- QUEUED ORDERS (RECEIVED COMMAND) --
        new("AcceptCommand", "QUEUED ORDERS (RECEIVED COMMAND)", "Accept the pending order [TK_AcceptCommand]", K(Keys.Home), K(Keys.OemOpenBrackets)),
        new("RejectCommand", "QUEUED ORDERS (RECEIVED COMMAND)", "Reject / decline the pending order [TK_RejectCommand]", K(Keys.End), K(Keys.OemCloseBrackets)),
        new("ClearCommand", "QUEUED ORDERS (RECEIVED COMMAND)", "Clear whatever order is already accepted [TK_ClearCommand]", K(Keys.Delete), K(Keys.Delete)),

        // -- COMMANDED SHIP (DRONE) --
        new("CycleCommandee", "COMMANDED SHIP (DRONE)", "Cycle which same-side ship/drone you command", K(Keys.PageDown), null),
        new("IssueBuild", "COMMANDED SHIP (DRONE)", "Issue order: commandee builds targeted asteroid", K(Keys.Insert), null),

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
        new("ToggleHud", "CAMERA / VIEW", "Toggle HUD (hide/show overlays for clean frames) [P612]", K(Keys.F1), null),
        new("ToggleMouse", "CAMERA / VIEW", "Mouse flight toggle [TK_ToggleMouse]", K(Keys.M), K(Keys.V)),
        new("InvertMouse", "CAMERA / VIEW", "Invert mouse pitch (session only)", K(Keys.I), null),
        new("ToggleStarmap", "CAMERA / VIEW", "Expand/collapse the always-on mini-map (full starmap)", K(Keys.Tab), null),
        new("Scoreboard", "CAMERA / VIEW", "In-mission scoreboard (kills / deaths)", K(Keys.B), null),
        new("PilotInfo", "CAMERA / VIEW", "Pilot info (your own live stats)", K(Keys.P), null),
        new("Help", "CAMERA / VIEW", "Toggle this help (you are here)", K(Keys.H), K(Keys.F1)),
        new("OpenKeyBindings", "CAMERA / VIEW", "Open Controls (rebind keys) [P625]", K(Keys.F6), null),
        new("ScreenShot", "CAMERA / VIEW", "Take a screen shot [TK_ScrnShot]", K(Keys.PrintScreen), K(Keys.PrintScreen)),
        new("Quit", "CAMERA / VIEW", "Quit game", new KeyBinding(Keys.F4, InputModifiers.Alt), null),

        // -- MUSIC (moved off the F-row per Domingo; Ctrl + = / - is free) --
        new("ToggleMusic", "MUSIC", "Toggle background music [idmToggleMusic]", K(Keys.J), null),
        new("MusicVolumeUp", "MUSIC", "Music volume up [idmMusicVolumeUp]", C(Keys.OemPlus), null),
        new("MusicVolumeDown", "MUSIC", "Music volume down [idmMusicVolumeDown]", C(Keys.OemMinus), null),

        // -- CHAT --
        new("StartChat", "CHAT", "Open chat / send line [TK_StartChat]", K(Keys.Enter), K(Keys.Enter)),
        new("ChatDelete", "CHAT", "Delete last char in chat", K(Keys.Back), null),
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
    };

    /// <summary>The full rebindable action set.</summary>
    public static IReadOnlyList<InputActionDef> Definitions => D;
}
