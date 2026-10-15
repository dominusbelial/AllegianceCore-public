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
        "MOVEMENT", "CRUISE THROTTLE", "WEAPONS", "INVENTORY / CARGO",
        "TARGETING / AUTOPILOT",
        "QUEUED ORDERS (RECEIVED COMMAND)", "COMMANDED SHIP (DRONE)",
        "VOTE / COMMAND", "CAMERA / VIEW", "MUSIC", "CHAT", "LOBBY (SERVER BROWSER)",
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
        new("ThrustForward", "MOVEMENT", "Throttle forward (held thrust) [TK_ThrustForward]", K(Keys.W), K(Keys.Tab)),
        new("ThrustBackward", "MOVEMENT", "Throttle backward (held thrust) [TK_ThrustBackward]", K(Keys.S), K(Keys.S)),
        new("ThrustLeft", "MOVEMENT", "Strafe left [TK_ThrustLeft]", K(Keys.Q), K(Keys.A)),
        new("ThrustRight", "MOVEMENT", "Strafe right [TK_ThrustRight]", K(Keys.E), K(Keys.D)),
        // P624 — vertical strafe (TK_ThrustUp/Down, igc.h:46/47). Native's
        // defaultinputmap.mdl binds these to W/X — but W is the port's
        // deliberate thrust-forward key (P625 catalog) and X is LeaderCommand,
        // so the vertical pair rides two free keys (see the P624 report's
        // "Native behaviour classified" table: movement is a classified
        // deliberate divergence). ThrustDown keeps the native X ergonomics by
        // relocation (LeaderCommand -> Ctrl+X); ThrustUp takes the freed Insert.
        // The sim already models UpButton/DownButton (ShipKinematics.cs:157-162),
        // the exact lateral-thrust model, so these just OR the same movement bits.
        new("ThrustUp", "MOVEMENT", "Vertical strafe up (held thrust) [TK_ThrustUp]", K(Keys.Insert), K(Keys.W)),
        new("ThrustDown", "MOVEMENT", "Vertical strafe down (held thrust) [TK_ThrustDown]", K(Keys.X), K(Keys.X)),
        // P624 — vector lock (TK_LockVector, igc.h:57; native toggles
        // coastButtonIGC, WinTrek.cpp:9458-9464). L is native's own default
        // (defaultinputmap.mdl line 39) so Ripcord moves to ITS native default
        // Ctrl+R (line 68). Latched toggle like cloak.
        new("LockVector", "MOVEMENT", "Vector lock (hold heading+speed) [TK_LockVector]", K(Keys.L), K(Keys.L)),
        new("PitchUp", "MOVEMENT", "Pitch up [TK_PitchUp]", K(Keys.Up)),
        new("PitchDown", "MOVEMENT", "Pitch down [TK_PitchDown]", K(Keys.Down)),
        new("YawLeft", "MOVEMENT", "Yaw left [TK_YawLeft]", K(Keys.Left)),
        new("YawRight", "MOVEMENT", "Yaw right [TK_YawRight]", K(Keys.Right)),
        new("RollLeft", "MOVEMENT", "Roll left [TK_RollLeft]", K(Keys.A), K(Keys.Q)),
        new("RollRight", "MOVEMENT", "Roll right [TK_RollRight]", K(Keys.D), K(Keys.E)),
        new("FireBooster", "MOVEMENT", "Afterburner boost (hold) [TK_FireBooster]", K(Keys.LeftShift), K(Keys.Space)),

        // -- CRUISE THROTTLE (native bindings already, defaultinputmap.mdl:25-32) --
        new("ThrottleUp", "CRUISE THROTTLE", "Cruise throttle up (persistent, 20% steps) [TK_ThrottleUp]", K(Keys.OemPlus), K(Keys.OemPlus)),
        new("ThrottleDown", "CRUISE THROTTLE", "Cruise throttle down (persistent, 20% steps) [TK_ThrottleDown]", K(Keys.OemMinus), K(Keys.OemMinus)),
        new("ThrottleZero", "CRUISE THROTTLE", "Throttle ZERO (stop) [TK_ThrottleZero]", K(Keys.Back), K(Keys.Back)),
        // P624 — ThrottleFull folds BOTH MonoGame "\\" keys in as native
        // alternates: OemBackslash (the catalogue default, VK 0xDC =
        // defaultinputmap.mdl line 43) plus OemPipe (the US-layout main
        // backslash — the same physical key by another scancode), so "full"
        // is reachable on any layout via the action map's alternate mechanism
        // (InputBindings.cs) instead of the old hard-coded OemPipe OR.
        new("ThrottleFull", "CRUISE THROTTLE", "Throttle FULL (either \\ key) [TK_ThrottleFull]", K(Keys.OemBackslash), K(Keys.OemBackslash), new[] { new KeyBinding(Keys.OemPipe, InputModifiers.None) }),
        // P624 — match speed (TK_MatchSpeed, igc.h:104; native while-held
        // GetThrottle, WinTrek.cpp:10966-10974 + :122-134). Unbound in native's
        // defaultinputmap.mdl; rides the free '/' key (OemQuestion). Sets the
        // P445 persistent throttle to match the current target's speed.
        new("MatchSpeed", "CRUISE THROTTLE", "Match speed with target (hold) [TK_MatchSpeed]", K(Keys.OemQuestion), null),
        new("MouseWheel", "CRUISE THROTTLE", "Mouse wheel cruise throttle (native wheel map)", new KeyBinding(Keys.None, InputModifiers.None)),

        // -- WEAPONS (native defaults where the port binds a key; FireWeapon is
        //    native mouse-left, so the port's Space is its own binding) --
        new("FireWeapon", "WEAPONS", "Fire weapons (hold) [TK_FireWeapon]", K(Keys.Space), null),
        new("FireMissile", "WEAPONS", "Fire missile (hold) [TK_FireMissile]", K(Keys.LeftControl), null),
        new("ToggleGroupFire", "WEAPONS", "Group-fire / single toggle [TK_ToggleGroupFire]", K(Keys.OemTilde), null),
        new("PrevWeapon", "WEAPONS", "Previous weapon [TK_PrevWeapon]", K(Keys.OemOpenBrackets), null),
        new("NextWeapon", "WEAPONS", "Next weapon [TK_NextWeapon]", K(Keys.OemCloseBrackets), null),
        // P641: native defaults defaultinputmap.mdl:76-79 (TK 145..148).
        new("ToggleWeapon1", "WEAPONS", "Select weapon 1 / group [TK_ToggleWeapon1]", K(Keys.D1), K(Keys.D1)),
        new("ToggleWeapon2", "WEAPONS", "Select weapon 2 / group [TK_ToggleWeapon2]", K(Keys.D2), K(Keys.D2)),
        new("ToggleWeapon3", "WEAPONS", "Select weapon 3 / group [TK_ToggleWeapon3]", K(Keys.D3), K(Keys.D3)),
        new("ToggleWeapon4", "WEAPONS", "Select weapon 4 / group [TK_ToggleWeapon4]", K(Keys.D4), K(Keys.D4)),
        // TK_ToggleBooster is declared (igc.h:287), unhandled/unbound in native.
        new("ToggleBooster", "WEAPONS", "Toggle booster [TK_ToggleBooster]", S(Keys.B), null),
        new("DropChaff", "WEAPONS", "Drop chaff [TK_DropChaff]", K(Keys.C), K(Keys.Z)),
        new("ToggleCloak", "WEAPONS", "Toggle cloak [TK_ToggleCloak]", K(Keys.F), K(Keys.G)),
        new("Ripcord", "WEAPONS", "Ripcord to selected starmap sector / cancel [TK_Ripcord]", C(Keys.R), C(Keys.R)),
        new("DropMine", "WEAPONS", "Drop mine / probe [TK_DropMine]", K(Keys.N), K(Keys.C)),
        new("Suicide", "WEAPONS", "Suicide [TK_Suicide] - ejects from a turret as gunner", A(Keys.K), new KeyBinding(Keys.D, InputModifiers.Shift | InputModifiers.Control)),
        new("PromoteTurret1", "WEAPONS", "Promote turret gunner seat 1 to command [TK_PromoteTurret1]", C(Keys.D1), null),
        new("PromoteTurret2", "WEAPONS", "Promote turret gunner seat 2 to command [TK_PromoteTurret2]", C(Keys.D2), null),
        new("PromoteTurret3", "WEAPONS", "Promote turret gunner seat 3 to command [TK_PromoteTurret3]", C(Keys.D3), null),
        new("PromoteTurret4", "WEAPONS", "Promote turret gunner seat 4 to command [TK_PromoteTurret4]", C(Keys.D4), null),

        // -- INVENTORY / CARGO (P626) — in-flight cargo selection + drop + reload.
        // Native handlers: TK_NextCargo cycles the selected cargo item
        // (WinTrek.cpp:9640-9648, NextCargoPart trekigc.cpp:2620-2642), TK_DropCargo
        // drops the selected cargo part into space as a treasure (WinTrek.cpp:
        // 9698-9730, DropPart), TK_Reload refills pools/launchers from cargo
        // (WinTrek.cpp:9674-9695, BaseClient::Reload clintlib.cpp:3425+).
        // Native defaultinputmap.mdl keys: NextCargo="," (VK 0xBC), DropCargo=
        // Ctrl+"," (VK 0xBC, ctrl), Reload=R (VK 0x52). R is already the port's
        // QuickChat key (P217, entrenched), so manual Reload rides Shift+R — the
        // auto-reload-on-empty (P361/P192) already covers the empty pool, so the
        // manual key is a convenience (classified divergence, see P626 report).
        // "," doubles as the port's starmap sector-nav key but only while the
        // starmap is expanded (P272) — a different context, so native wins in
        // flight. NextPart/PrevPart (igc.h:232-233) declare "select previous/next
        // part from inventory" but native binds them to nothing and has no case
        // handler in WinTrek.cpp (only NextCargo is wired).
        // P641 gives them inventory selection across mounted equipment and cargo,
        // with port defaults period / Shift+period; their native defaults remain unbound.
        // Generic inventory TKs have no native handlers/defaults (igc.h:234-237).
        // Port bindings operate on the selected inventory entry using SWAP_PART.
        new("MountPart", "INVENTORY / CARGO", "Mount selected cargo into empty equipment [TK_MountPart]", S(Keys.M), null),
        new("UnmountPart", "INVENTORY / CARGO", "Unmount selected equipment into cargo [TK_UnmountPart]", C(Keys.U), null),
        new("ReplacePart", "INVENTORY / CARGO", "Swap selected cargo with compatible equipment [TK_ReplacePart]", S(Keys.U), null),
        new("DropPart", "INVENTORY / CARGO", "Drop selected inventory part [TK_DropPart]", C(Keys.Delete), null),
        new("NextCargo", "INVENTORY / CARGO", "Rotate the selected cargo item [TK_NextCargo]", K(Keys.OemComma), K(Keys.OemComma)),
        new("DropCargo", "INVENTORY / CARGO", "Drop the selected cargo item into space [TK_DropCargo]", C(Keys.OemComma), C(Keys.OemComma)),
        new("Reload", "INVENTORY / CARGO", "Reload weapons/afterburner/magazines from cargo [TK_Reload]", S(Keys.R), K(Keys.R)),
        new("PrevPart", "INVENTORY / CARGO", "Select previous part from inventory [TK_PrevPart]", S(Keys.OemPeriod), null),
        new("NextPart", "INVENTORY / CARGO", "Select next part from inventory [TK_NextPart]", K(Keys.OemPeriod), null),

        // -- TARGETING / AUTOPILOT --
        new("Target", "TARGETING / AUTOPILOT", "Cycle target (nearest first) [TK_Target]", K(Keys.T), null),
        new("ToggleAutopilot", "TARGETING / AUTOPILOT", "Toggle autopilot to target [TK_ToggleAutoPilot]", K(Keys.G), K(Keys.P)),
        new("TargetEnemyNearest", "TARGETING / AUTOPILOT", "Nearest enemy utility ship [TK_TargetEnemyNearest]", K(Keys.U), S(Keys.F)),
        new("TargetFriendlyNearest", "TARGETING / AUTOPILOT", "Nearest friendly ship [TK_TargetFriendlyNearest]", S(Keys.U), S(Keys.T)),
        new("TargetEnemyBaseNearest", "TARGETING / AUTOPILOT", "Nearest enemy base, ANY sector [TK_TargetEnemyBaseNearest]", K(Keys.Z), S(Keys.U)),
        new("TargetFriendlyBaseNearest", "TARGETING / AUTOPILOT", "Nearest friendly base, ANY sector [TK_TargetFriendlyBaseNearest]", S(Keys.Z), S(Keys.B)),
        new("TargetNothing", "TARGETING / AUTOPILOT", "Target nothing (clear target / autopilot) [TK_TargetNothing]", K(Keys.OemSemicolon), null),
        new("TargetCenter", "TARGETING / AUTOPILOT", "Target center (nearest to reticle) [TK_TargetCenter]", K(Keys.OemQuotes), null),
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
        new("ExecuteCommand", "QUEUED ORDERS (RECEIVED COMMAND)", "Accept order and engage autopilot [TK_ExecuteCommand]", U(), null),
        new("TargetCommand", "TARGETING / AUTOPILOT", "Select accepted command target [TK_TargetCommand]", U(), null),
        new("AcceptCommand", "QUEUED ORDERS (RECEIVED COMMAND)", "Accept the pending order [TK_AcceptCommand]", K(Keys.Home), K(Keys.OemOpenBrackets)),
        new("RejectCommand", "QUEUED ORDERS (RECEIVED COMMAND)", "Reject / decline the pending order [TK_RejectCommand]", K(Keys.End), K(Keys.OemCloseBrackets)),
        new("ClearCommand", "QUEUED ORDERS (RECEIVED COMMAND)", "Clear whatever order is already accepted [TK_ClearCommand]", K(Keys.Delete), null),

        // -- COMMANDED SHIP (DRONE) --
        // P626 — CycleCommandee moved OFF PageDown (its P425 binding) because
        // native TK_ChatPageDown owns PageDown (defaultinputmap.mdl); it now
        // rides Shift+PageDown (same PageDown family, discoverable).
        new("CycleCommandee", "COMMANDED SHIP (DRONE)", "Cycle which same-side ship/drone you command", S(Keys.PageDown), null),
        // P624 — IssueBuild / LeaderCommand keys were Insert / X; X is native
        // TK_ThrustDown (defaultinputmap.mdl line 31) and Insert frees the
        // number row, so both commander-drone bindings move to free chords.
        new("IssueBuild", "COMMANDED SHIP (DRONE)", "Issue order: commandee builds targeted asteroid", A(Keys.I), null),
        new("LeaderCommand", "COMMANDED SHIP (DRONE)", "Leader-only: command the selected same-side bot to dock / launch", C(Keys.X), null),

        // -- VOTE / COMMAND (P626) — TK_VoteYes/VoteNo on the active ballot.
        // Native: trekClient.Vote(bAgree) (WinTrek.cpp:10377-10387); the port
        // sends a real FM_C_VOTE(19) naming the active ballot's id (P187 server
        // tally). Native defaultinputmap.mdl: VoteYes=Y (VK 0x59), VoteNo=N
        // (VK 0x4E). Y is only the ready-check toggle (a different, pre-flight
        // screen), so VoteYes=Y is contextual and native wins. N is the port's
        // DropMine key (P224) — and native's own mine key is C (VK 0x43) while
        // the port also moved chaff onto C; native's chaff key Z is taken by the
        // P415 enemy-base targeting, so full native re-alignment is impossible.
        // VoteNo therefore rides Ctrl+N (N-family, discoverable) — a classified
        // divergence (see P626 report).
        new("VoteYes", "VOTE / COMMAND", "Vote YES on the active ballot [TK_VoteYes]", K(Keys.Y), K(Keys.Y)),
        new("VoteNo", "VOTE / COMMAND", "Vote NO on the active ballot [TK_VoteNo]", C(Keys.N), K(Keys.N)),

        // P642: native unbound commands remain rebindable; port chords avoid existing actions.
        new("TargetNearest", "TARGETING / AUTOPILOT", "Target nearest target [TK_TargetNearest]", U(), null),
        new("TargetPrev", "TARGETING / AUTOPILOT", "Target previous target [TK_TargetPrev]", U(), null),
        new("TargetEnemy", "TARGETING / AUTOPILOT", "Target next enemy utility ship [TK_TargetEnemy]", U(), K(Keys.F)),
        new("TargetEnemyPrev", "TARGETING / AUTOPILOT", "Target previous enemy utility ship [TK_TargetEnemyPrev]", C(Keys.F), C(Keys.F)),
        new("TargetFriendly", "TARGETING / AUTOPILOT", "Target next friendly ship [TK_TargetFriendly]", U(), K(Keys.T)),
        new("TargetFriendlyPrev", "TARGETING / AUTOPILOT", "Target previous friendly ship [TK_TargetFriendlyPrev]", U(), C(Keys.T)),
        new("TargetEnemyBase", "TARGETING / AUTOPILOT", "Target next enemy base [TK_TargetEnemyBase]", U(), K(Keys.U)),
        new("TargetEnemyBasePrev", "TARGETING / AUTOPILOT", "Target previous enemy base [TK_TargetEnemyBasePrev]", U(), C(Keys.U)),
        new("TargetFriendlyBase", "TARGETING / AUTOPILOT", "Target next friendly base [TK_TargetFriendlyBase]", U(), K(Keys.B)),
        new("TargetFriendlyBasePrev", "TARGETING / AUTOPILOT", "Target previous friendly base [TK_TargetFriendlyBasePrev]", C(Keys.B), C(Keys.B)),
        new("TargetWarp", "TARGETING / AUTOPILOT", "Target next aleph [TK_TargetWarp]", A(Keys.W), null),
        new("TargetWarpNearest", "TARGETING / AUTOPILOT", "Target nearest aleph [TK_TargetWarpNearest]", U(), null),
        new("TargetWarpPrev", "TARGETING / AUTOPILOT", "Target previous aleph [TK_TargetWarpPrev]", U(), null),
        new("TargetFlag", "TARGETING / AUTOPILOT", "Target next enemy flag [TK_TargetFlag]", A(Keys.G), null),
        new("TargetFlagNearest", "TARGETING / AUTOPILOT", "Target nearest enemy flag [TK_TargetFlagNearest]", U(), null),
        new("TargetFlagPrev", "TARGETING / AUTOPILOT", "Target previous enemy flag [TK_TargetFlagPrev]", U(), null),
        new("TargetArtifact", "TARGETING / AUTOPILOT", "Target next artifact [TK_TargetArtifact]", A(Keys.A), null),
        new("TargetArtifactNearest", "TARGETING / AUTOPILOT", "Target nearest artifact [TK_TargetArtifactNearest]", U(), null),
        new("TargetArtifactPrev", "TARGETING / AUTOPILOT", "Target previous artifact [TK_TargetArtifactPrev]", U(), null),
        new("TargetAlliedBase", "TARGETING / AUTOPILOT", "Target next allied base [TK_TargetAlliedBase]", A(Keys.Z), null),
        new("TargetAlliedBaseNearest", "TARGETING / AUTOPILOT", "Target nearest allied base [TK_TargetAlliedBaseNearest]", U(), null),
        new("TargetAlliedBasePrev", "TARGETING / AUTOPILOT", "Target previous allied base [TK_TargetAlliedBasePrev]", U(), null),
        new("TargetSelf", "TARGETING / AUTOPILOT", "Select your own ship [TK_TargetSelf]", U(), null),
        new("ConModeCombat", "CAMERA / VIEW", "Return to combat cockpit [TK_ConModeCombat]", A(Keys.F2), K(Keys.F2)),
        new("ConModeGameState", "CAMERA / VIEW", "Game state / mission scores [TK_ConModeGameState]", A(Keys.F7), K(Keys.OemQuestion)),
        new("ConModeNav", "CAMERA / VIEW", "Sector navigation pane [TK_ConModeNav]", S(Keys.F7), K(Keys.F7)),
        new("ConModeInventory", "CAMERA / VIEW", "Inventory / docked loadout [TK_ConModeInventory]", S(Keys.F4), K(Keys.F4)),
        new("ConModeMiniTeam", "CAMERA / VIEW", "Mini team roster [TK_ConModeMiniTeam]", A(Keys.F6), S(Keys.F6)),
        new("ViewExternal", "CAMERA / VIEW", "External camera / cockpit [TK_ViewExternal]", A(Keys.V), null),
        new("ToggleGrid", "CAMERA / VIEW", "Show or hide command grid [TK_ToggleGrid]", C(Keys.OemPeriod), C(Keys.OemPeriod)),
        new("OccupyNextTurret", "CAMERA / VIEW", "Move to next turret on your carrier [TK_OccupyNextTurret]", A(Keys.OemComma), null),
        new("MainMenu", "CAMERA / VIEW", "Open main menu [TK_MainMenu]", K(Keys.Escape), K(Keys.Escape)),

        // -- CAMERA / VIEW (native wins the F-row) --
        new("CycleView", "CAMERA / VIEW", "Cycle view: cockpit -> chase -> turret -> missile-cam", K(Keys.V), K(Keys.V)),
        // P624 — look-around cameras (TK_ViewRear & friends; native sets an
        // absolute head yaw, WinTrek.cpp:9733-9791, only in cockpit/external-
        // chase). Native leaves them UNBOUND in defaultinputmap.mdl (keyboard
        // look reached them via the joystick-hat patch, WinTrek.cpp:8445-8462),
        // P641 reserves native D1-D4 for weapons and moves these four looks to
        // Alt+D1-D4. The remaining D6-D8 looks retain their P624 bindings.
        // All are HELD — release snaps the head back to forward.
        new("ViewFrontLeft", "CAMERA / VIEW", "Look front-left (hold) [TK_ViewFrontLeft]", A(Keys.D1), null),
        new("ViewFrontRight", "CAMERA / VIEW", "Look front-right (hold) [TK_ViewFrontRight]", A(Keys.D2), null),
        new("ViewLeft", "CAMERA / VIEW", "Look left (hold) [TK_ViewLeft]", A(Keys.D3), null),
        new("ViewRight", "CAMERA / VIEW", "Look right (hold) [TK_ViewRight]", A(Keys.D4), null),
        new("ViewRearLeft", "CAMERA / VIEW", "Look rear-left (hold) [TK_ViewRearLeft]", K(Keys.D6), null),
        new("ViewRear", "CAMERA / VIEW", "Look rear (hold) [TK_ViewRear]", K(Keys.D7), null),
        new("ViewRearRight", "CAMERA / VIEW", "Look rear-right (hold) [TK_ViewRearRight]", K(Keys.D8), null),
        // P624 — radar mode cycle (TK_CycleRadar, igc.h:205; native cycles
        // All→Default→Target→Minimal, WinTrek.cpp:9955-9975). Native binds the
        // period key (defaultinputmap.mdl line 111) — honored here.
        new("CycleRadar", "CAMERA / VIEW", "Cycle radar modes (All/Default/Target/Minimal) [TK_CycleRadar]", K(Keys.OemPeriod), K(Keys.OemPeriod)),
        // These TKs have no keyboard defaults in Artwork/defaultinputmap.mdl.
        new("ViewMissile", "CAMERA / VIEW", "Toggle missile view [TK_ViewMissile]", U(), null),
        new("ViewChase", "CAMERA / VIEW", "Toggle chase view [TK_ViewChase]", U(), null),
        // Native's turret branch is disabled (WinTrek.cpp:9856-9864).
        // Port extension: expose the existing carrier gunner camera, unbound.
        new("ViewTurret", "CAMERA / VIEW", "Toggle gunner carrier view [TK_ViewTurret]", U(), null),
        new("ViewExternalOrbit", "CAMERA / VIEW", "External orbit camera [TK_ViewExternalOrbit]", K(Keys.F4), null),
        new("ViewFlyby", "CAMERA / VIEW", "External fly-by camera [TK_ViewFlyby]", K(Keys.F5), null),
        new("ViewTarget", "CAMERA / VIEW", "External target camera [TK_ViewTarget]", K(Keys.F7), null),
        new("ViewBase", "CAMERA / VIEW", "External base camera [TK_ViewBase]", K(Keys.F10), null),
        new("ViewExternalStation", "CAMERA / VIEW", "External station camera [TK_ViewExternalStation]", K(Keys.F11), null),
        new("FreeCam", "CAMERA / VIEW", "Free-flight spectator camera (detached) [P612]", K(Keys.F8), null),
        new("OrbitCam", "CAMERA / VIEW", "Orbit spectator camera (locks a ship/station) [P612]", K(Keys.F9), null),
        new("Director", "CAMERA / VIEW", "Director b-roll camera (opt-in, hides HUD) [P580]", K(Keys.F2), null),
        new("FollowCam", "CAMERA / VIEW", "Spectator-follow camera [P580]", C(Keys.F3), null),
        // TK_ViewCommand(38) unbound; TK_ConModeCommand(68) F3 is its native alias
        // (WinTrek.cpp:9894-9933; defaultinputmap.mdl:36,64).
        new("ViewCommand", "CAMERA / VIEW", "Tactical 3D command view [TK_ViewCommand / TK_ConModeCommand]", K(Keys.F3), K(Keys.F3)),
        new("ToggleCommand", "CAMERA / VIEW", "Command view: three-quarter / top down [TK_ToggleCommand]", S(Keys.F6), S(Keys.F3)),
        new("TrackCommandView", "CAMERA / VIEW", "Command view: track selected target [TK_TrackCommandView]", C(Keys.F7), null),
        new("DroneFollow", "COMMANDED SHIP (DRONE)", "Follow commanded drone / wingman (external)", C(Keys.F8), null),
        new("DroneCockpit", "COMMANDED SHIP (DRONE)", "View through commanded drone / wingman", C(Keys.F9), null),
        // Native sectormap.cpp:1491: Ctrl + right mouse, no TK identity.
        new("CommandSelectionAdd", "CAMERA / VIEW", "Hold for left-click add/remove command recipient (native mouse selection)", K(Keys.LeftShift), null),
        new("SectorHighlightModifier", "CAMERA / VIEW", "Hold for right-click sector mark (leader toggles, teammate clears locally)", K(Keys.LeftControl), null),
        new("ZoomOut", "CAMERA / VIEW", "Camera zoom out [TK_ZoomOut]", K(Keys.Subtract), K(Keys.Subtract)),
        new("ZoomIn", "CAMERA / VIEW", "Camera zoom in [TK_ZoomIn]", K(Keys.Add), K(Keys.Add)),
        new("ToggleHud", "CAMERA / VIEW", "Toggle HUD (hide/show overlays for clean frames) [P612]", C(Keys.F1), null),
        new("ToggleMouse", "CAMERA / VIEW", "Mouse flight toggle [TK_ToggleMouse]", K(Keys.M), K(Keys.V)),
        new("InvertMouse", "CAMERA / VIEW", "Invert mouse pitch (session only)", C(Keys.M), null),
        new("ToggleStarmap", "CAMERA / VIEW", "Expand/collapse the always-on mini-map (full starmap)", K(Keys.Tab), null),
        new("SelectSectorPrev", "CAMERA / VIEW", "Starmap: previous sector (with map expanded)", K(Keys.OemComma), null),
        // P624 — starmap NEXT moved off '.' (OemPeriod) because native binds
        // that key to TK_CycleRadar (defaultinputmap.mdl line 111, (190,0,205));
        // it now lives on the free D9 so the ',' / D9 pair cycles the map.
        new("SelectSectorNext", "CAMERA / VIEW", "Starmap: next sector (with map expanded)", K(Keys.D9), null),
        // P506 — mouse-flight response-curve settings (native menu items,
        // WinTrek.cpp:4534 / AdjustMouseSens :5512; this keyboard port has no
        // menu, so the digit row D5-D8 carries them). P624 — the plain digit
        // row D1-D8 becomes the look-camera ring, so these debug settings move
        // to their Ctrl-chords (still rebindable via F6).
        new("MouseDeadzone", "CAMERA / VIEW", "Mouse flight: cycle deadzone preset", C(Keys.D5), null),
        new("MouseResponseCurve", "CAMERA / VIEW", "Mouse flight: toggle linear / quadratic response", C(Keys.D6), null),
        new("MouseSensitivityDown", "CAMERA / VIEW", "Mouse flight: sensitivity down", C(Keys.D7), null),
        new("MouseSensitivityUp", "CAMERA / VIEW", "Mouse flight: sensitivity up", C(Keys.D8), null),
        new("Scoreboard", "CAMERA / VIEW", "In-mission scoreboard (kills / deaths)", K(Keys.B), null),
        new("PilotInfo", "CAMERA / VIEW", "Pilot info (your own live stats)", K(Keys.P), null),
        // P639 — TeamPane: the full-roster team screen (every side's pilots,
        // rank, ship status, kills/deaths). Native TK identity is TK_ConModeTeam
        // (igc.h:295); its native default is F6 (defaultinputmap.mdl keycode
        // 117), which this port's P625 Controls/rebind screen already occupies,
        // so the port defaults it to the free Ctrl+T chord (rebindable via F6).
        // Deviation from native's default is documented in P639 report.
        new("TeamPane", "CAMERA / VIEW", "Expanded team roster [TK_ConModeTeam]", C(Keys.T), K(Keys.F6)),
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
        new("QuickChat", "CHAT", "Open quick-chat menu [TK_QuickChatMenu]", K(Keys.R), K(Keys.OemTilde)),
        new("CancelChat", "CHAT", "Cancel chat / close quick-chat", K(Keys.Escape), K(Keys.Escape)),
        // P626 — chat buffer scroll. TK_NextCommMsg/PrevCommMsg step the chat
        // scroll focus one line (igc.h:227-228: "scroll focus to next/previous
        // chat message"); TK_ChatPageUp/Down page the chat pane by a screenful
        // (igc.h:327-329; native binds PageUp/PageDown, VK 0x21/0x22, in
        // defaultinputmap.mdl). Native leaves the per-message step UNBOUND, so
        // the port rides the free Ctrl+PageUp/PageDown chords (PageUp/PageDown
        // themselves are the native page keys). "Up/Prev" = toward older,
        // "Down/Next" = toward newer, matching a bottom-anchored log.
        new("PrevCommMsg", "CHAT", "Previous chat message (scroll older) [TK_PrevCommMsg]", C(Keys.PageUp), null),
        new("NextCommMsg", "CHAT", "Next chat message (scroll newer) [TK_NextCommMsg]", C(Keys.PageDown), null),
        new("ChatPageUp", "CHAT", "Chat pane: page up (older) [TK_ChatPageUp]", K(Keys.PageUp), K(Keys.PageUp)),
        new("ChatPageDown", "CHAT", "Chat pane: page down (newer) [TK_ChatPageDown]", K(Keys.PageDown), K(Keys.PageDown)),

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
