using System;
using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P257 — the current client keybinding reference table, organised into logical
/// groups for the H help overlay.
///
/// Every entry is a faithful, hand-maintained snapshot of the bindings actually
/// checked in <c>Game1.UpdateFlying</c> / <c>UpdateLobby</c> TODAY (and the pure
/// translation in <c>Flight.FlightInput.Translate</c>), with the <c>TK_</c>
/// command it corresponds to where one exists (per <c>docs/KEYBINDING_AUDIT_P220.md</c>
/// classifications, which this port's own key choices implement).
///
/// This is deliberately a HARDCODED table, not a registry that drives input:
/// the input remains dispatched imperatively in Game1 and this document lists
/// it. Consequently it WILL drift the next time a binding is added elsewhere in
/// the codebase — the very problem the (now stale) P220 audit doc ran into.
/// Keeping it accurate is a named, accepted maintenance burden of this feature,
/// not something this task attempts to solve architecturally.
///
/// Descriptions are kept deliberately concise (a reference cheat-sheet read in
/// mid-flight, laid out in narrow columns) — the full TK_-annotated table lives
/// in the P257 writeup. Naming / purity discipline: <see cref="KeyBindingTable"/>
/// carries no MonoGame types (strings only) so it is headless-unit-testable.
/// All strings are pure ASCII so the spritefonts (which bake ASCII 32..126
/// only) can render every cell without throwing.
/// </summary>
public sealed class KeyBindingTable
{
    /// <summary>One binding: the key label and what it actually does.</summary>
    public readonly record struct Entry(string Key, string Description);

    /// <summary>One headed group of bindings.</summary>
    public readonly record struct Group(string Title, IReadOnlyList<Entry> Entries)
    {
        /// <summary>
        /// Vertical footprint in px: one title row + a separator gap + one row
        /// per entry (matched by <see cref="HelpOverlayLayout"/> and the
        /// renderer).
        /// </summary>
        public float Height =>
            HelpOverlayLayout.RowHeight + HelpOverlayLayout.TitleGap
            + Entries.Count * HelpOverlayLayout.RowHeight;
    }

    /// <summary>
    /// The full current keybinding reference. Order within a group is the
    /// grouping's internal organisation (movement axes, then toggles).
    /// </summary>
    public static IReadOnlyList<Group> All { get; } = new[]
    {
        new Group("MOVEMENT",
            new[]
            {
                new Entry("W / S", "Throttle fwd / back"),
                new Entry("Q / E", "Strafe left / right"),
                new Entry("Up / Down", "Pitch up / down"),
                new Entry("Left / Right", "Yaw left / right"),
                new Entry("A / D", "Roll left / right"),
                new Entry("Shift (either)", "Afterburner boost (hold) [TK_FireBooster]"),
            }),

        new Group("CRUISE THROTTLE",
            new[]
            {
                new Entry("= / -", "Cruise throttle up / down (persistent, 20% steps) [TK_ThrottleUp/Down]"),
                new Entry("Backspace", "Throttle ZERO (stop) [TK_ThrottleZero]"),
                new Entry("Backslash (\\)", "Throttle FULL [TK_ThrottleFull]"),
                new Entry("Mouse wheel", "Cruise throttle up / down (paced)"),
            }),

        new Group("WEAPONS",
            new[]
            {
                new Entry("Space", "Fire weapons (hold) [TK_FireWeapon]"),
                new Entry("Left Ctrl", "Fire missile (hold) [TK_FireMissile]"),
                new Entry("`", "Group-fire / single toggle [TK_ToggleGroupFire]"),
                new Entry("[ / ]", "Prev / next weapon [TK_Prev/NextWeapon]"),
                new Entry("C", "Drop chaff [TK_DropChaff]"),
                new Entry("F", "Toggle cloak [TK_ToggleCloak]"),
                new Entry("L", "Ripcord to selected starmap sector / cancel [TK_Ripcord]"),
                new Entry("N", "Drop mine / probe [TK_DropMine]"),
                new Entry("K", "Suicide [TK_Suicide] - ejects from a turret when you are a gunner"),
                new Entry("1-4", "Promote turret gunner in seat N to command [TK_PromoteTurret1-4]"),
            }),

        new Group("TARGETING / AUTOPILOT",
            new[]
            {
                new Entry("T", "Cycle target (nearest first)"),
                new Entry("G", "Toggle autopilot to target"),
                new Entry("U", "Nearest enemy ship (repeat: next) [TK_TargetEnemyNearest/Prev]"),
                new Entry("Shift + U", "Nearest friendly ship (repeat: next) [TK_TargetFriendlyNearest/Prev]"),
                new Entry("Z", "Nearest enemy base (repeat: next) [TK_TargetEnemyBaseNearest/Prev]"),
                new Entry("Shift + Z", "Nearest friendly / allied base (repeat: next) [TK_TargetFriendlyBaseNearest/Prev]"),
                new Entry("Y", "Target self [TK_TargetSelf]"),
                new Entry(";", "Target nothing (clear target / autopilot) [TK_TargetNothing]"),
                new Entry("'", "Target center (nearest to reticle) [TK_TargetCenter]"),
            }),

        new Group("QUEUED ORDERS (RECEIVED COMMAND)",
            new[]
            {
                new Entry("Home", "Accept the pending order (promote queued -> accepted) [TK_AcceptCommand]"),
                new Entry("End", "Reject / decline the pending order (clear queued) [TK_RejectCommand]"),
                new Entry("Delete", "Clear whatever order is already accepted [TK_ClearCommand]"),
            }),

        new Group("COMMANDED SHIP (DRONE)",
            new[]
            {
                new Entry("PageDown", "Cycle which same-side ship/drone you are commanding (nearest first, wraps)"),
                new Entry("Insert", "Issue the order: commandee builds the targeted asteroid (Builder role required)"),
            }),

        new Group("CAMERA / VIEW",
            new[]
            {
                new Entry("V", "Cycle view: cockpit -> chase -> turret (while gunning) -> missile-cam"),
                new Entry("M", "Mouse flight toggle"),
                new Entry("I", "Invert mouse pitch (session only)"),
                new Entry("Tab", "Expand/collapse the always-on mini-map (full starmap)"),
                new Entry("B", "In-mission scoreboard (kills / deaths)"),
                new Entry("P", "Pilot info (your own live stats: kills / deaths / combat rating / rank / score)"),
                new Entry("H", "Toggle this help (you are here)"),
                new Entry("F11", "Take a screen shot [TK_ScrnShot]"),
                new Entry("Alt + F4", "Quit game"),
            }),

        new Group("MUSIC",
            new[]
            {
                new Entry("J", "Toggle background music [idmToggleMusic]"),
                new Entry("F6 / F5", "Music volume up / down [idmMusicVolumeUp / idmMusicVolumeDown]"),
            }),

        new Group("CHAT",
            new[]
            {
                new Entry("Enter", "Open chat / send line"),
                new Entry("Backspace", "Delete last char in chat"),
                new Entry("R", "Open quick-chat menu"),
                new Entry("Esc", "Cancel chat / close menu"),
            }),

        new Group("LOBBY (SERVER BROWSER)",
            new[]
            {
                new Entry("Up / Down", "Select server"),
                new Entry("Enter", "Join selected server"),
                new Entry("F", "Find / filter servers by name (type to narrow, Esc clear)"),
                new Entry("R", "Refresh server list"),
                new Entry("Esc", "Clear filter"),
            }),

        new Group("READY CHECK (PRE-FLIGHT)",
            new[]
            {
                new Entry("Y", "Toggle your ready (PLAYER_READY)"),
                new Entry("O", "Force your side ready (mission owner; FORCE_TEAM_READY)"),
            }),
    };

    /// <summary>Count of every group (for the balanced column layout).</summary>
    public static int GroupCount => All.Count;
}
