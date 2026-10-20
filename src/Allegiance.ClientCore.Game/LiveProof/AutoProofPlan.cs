using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Allegiance.ClientCore.MonoGame.LiveProof;

/// <summary>What <c>--auto-target</c> should frame once airborne (P572). Nearest
/// of the given kind in the pilot's sector; <see cref="None"/> leaves the
/// client's normal default target behaviour untouched.</summary>
public enum AutoTargetKind
{
    None = 0,
    Enemy,
    Friendly,
    Station,
    Aleph,
}

/// <summary>P639 — which screen overlay a real-window autoshot should have open
/// (forced by <c>--auto-screen</c>). Mirrors the leaderboard (B), character
/// sheet (P) and team pane (Ctrl+T) toggles.</summary>
public enum AutoScreenKind
{
    None = 0,
    Leaderboard,
    CharSheet,
    TeamPane,
}

/// <summary>
/// P572 — the parsed live-proof automation plan for a NORMAL WINDOWED client.
/// Every flag defaults off; the client does nothing a player wouldn't. Purely
/// client-side — nothing here is sent on the wire beyond what a player sends.
/// This class is deliberately free of MonoGame / world dependencies so the flag
/// grammar and the schedule in <see cref="AutoProofMachine"/> are unit-testable
/// in isolation.
/// </summary>
public sealed class AutoProofPlan
{
    /// <summary>Side id to auto-select on the side-select screen (<c>--auto-side</c>).
    /// Null = the human picks; the server no longer auto-seats by default (P763),
    /// so pass this for unattended proofs.</summary>
    public int? AutoSide;

    /// <summary>Send PLAYER_READY(true) automatically once seated (<c>--auto-ready</c>).</summary>
    public bool AutoReady;

    /// <summary>When docked, launch with this hull (<c>--auto-launch [hull]</c>).
    /// A value selects that hull (by numeric id or case-insensitive name) and
    /// rebases the loadout; the empty string selects the CURRENT hull with its
    /// current parts. Null = never auto-launch (stay docked like a player).</summary>
    public string LaunchHull = "";

    /// <summary>Whether <c>--auto-launch</c> was given at all (distinguishes an
    /// explicit "keep the current hull" from "don't touch the hangar").</summary>
    public bool AutoLaunchRequested;

    /// <summary>P577b — open the in-game Credits/Licences screen (F12 overlay)
    /// so a real-window autoshot captures the updated credit text
    /// (<c>--auto-credits</c>). Off by default.</summary>
    public bool AutoCredits;

    /// <summary>P639 — force one of the three screen overlays open for a
    /// real-window autoshot (<c>--auto-screen leaderboard|charsheet|teampane|none</c>):
    /// the leaderboard (B), the character sheet (P) or the team pane (Ctrl+T).
    /// Client-only automation, off by default (None) — mirrors the P572
    /// <c>--hide-hud</c>/P577b <c>--auto-credits</c> philosophy of scripting the
    /// client to produce honest real-backbuffer proof frames.</summary>
    public AutoScreenKind AutoScreen = AutoScreenKind.None;

    /// <summary>HUD-4b — keep the graphics/HUD settings panel open for the whole
    /// run (<c>--auto-graphics-settings</c>), so the scheduled real-backbuffer
    /// autoshot captures it (the panel draws in EndDraw, after the in-frame
    /// overlay toggles that <c>--auto-screen</c> scripts).</summary>
    public bool AutoGraphicsSettings;

    /// <summary>Sector (numeric id or case-insensitive name) to engage the P541
    /// starmap goto toward after launch (<c>--auto-goto</c>). Null = don't.</summary>
    public string GotoSector = "";

    /// <summary>Framing target after launch (<c>--auto-target</c>).</summary>
    public AutoTargetKind Target = AutoTargetKind.None;

    /// <summary>P623 — CLASS-filtered target to frame once airborne
    /// (<c>--auto-target-class rel:cls[:dir]</c>), e.g. <c>enemy:bomber</c>,
    /// <c>enemy:miner</c>, <c>friendly:miner:nearest</c>. Empty = off. Drives the
    /// real class-filter handler (<see cref="SelectClassForKey"/>/MajorBase) so a
    /// real-window autoshot can prove the class bracket. Pure client-side.</summary>
    public string AutoTargetClass = "";

    /// <summary>Initial camera mode (<c>--camera mode</c>): cockpit | chase |
    /// flyby | flybytarget | orbit | target | base | station | override.</summary>
    public string Camera = "cockpit";
    /// <summary>P640: real commander highlight relay, then expanded starmap.</summary>
    public string AutoHighlightSector = "";
    public bool AutoFlightParts;
    public bool AutoParityCommands;
    /// <summary>HUD-7 — open the in-flight equipment/inventory pane (F4,
    /// ConModeInventory) once after launch so a real-window autoshot captures
    /// the native inventory pane (<c>--auto-inventory</c>).</summary>
    public bool AutoInventory;

    /// <summary>P580 — enable the director b-roll camera at launch
    /// (<c>--director</c>). Mutually the same thing the F2 key toggles; the flag
    /// just arms it from startup so a recording can begin immediately.</summary>
    public bool Director;

    /// <summary>P580 — spectator-follow a specific ship id at launch
    /// (<c>--follow id</c>): the director films that ship (battles without
    /// flying). The same thing the F3 key toggles. -1 = none.</summary>
    public short FollowShip = -1;

    /// <summary>P580 — deterministic director seed (<c>--director-seed N</c>),
    /// so a recording can be repeated shot-for-shot. Null = wall-clock seed.</summary>
    public int? DirectorSeed;

    /// <summary>Suppress the HUD overlay (<c>--hide-hud</c>) so the shot is clean
    /// geometry (P572 minimal toggle; see <see cref="AutoProofMachine"/>).</summary>
    public bool HideHud;

    /// <summary>Real-backbuffer screenshot seconds-after-launch schedule
    /// (<c>--autoshots t1,t2,...</c>). Empty = no timed shots.</summary>
    public double[] AutoshotSeconds = Array.Empty<double>();

    /// <summary>Output directory for the autoshots (<c>--autoshot-dir</c>).</summary>
    public string AutoshotDir = "";

    /// <summary>P584 — seconds after launch at which to send a suicide (the
    /// <c>K</c> key, TK_Suicide) so the death/eject HUD feedback (death banner,
    /// eject-pod panel) can be photographed in the real window. Multiple times
    /// let a run force an eject into the pod and then destroy the pod (second
    /// suicide → ghost → death banner). After the LAST scheduled suicide a
    /// real-backbuffer autoshot is taken a couple of seconds later so the
    /// ghost/pod state has been processed and drawn. Empty = never self-kill.</summary>
    public double[] AutoSuicideAt = Array.Empty<double>();

    /// <summary>Seconds after launch to head to the home station and dock
    /// (<c>--auto-dock-after</c>). Null = never auto-dock.</summary>
    public double? AutoDockAfter;

    /// <summary>How many hull previews to step through (and screenshot) in the
    /// docked hangar sweep (<c>--dock-sweep N</c>). 0 = no sweep.</summary>
    public int DockSweepCount;

    /// <summary>Seconds between each dock-sweep hull step (fixes the preview
    /// render + screenshot before advancing). Default 1.5 s.</summary>
    public double SweepIntervalSeconds = 1.5;

    /// <summary>Seconds after launch to exit the client (<c>--exit-after</c>).
    /// Null = run until the user closes it.</summary>
    public double? ExitAfter;

    /// <summary>A dock-hull "kind" token accepted by <c>--auto-target</c>.</summary>
    private static readonly Dictionary<string, AutoTargetKind> TargetKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enemy"] = AutoTargetKind.Enemy,
        ["friendly"] = AutoTargetKind.Friendly,
        ["station"] = AutoTargetKind.Station,
        ["aleph"] = AutoTargetKind.Aleph,
        ["none"] = AutoTargetKind.None,
        ["off"] = AutoTargetKind.None,
    };

    /// <summary>Parse the automation flags from an argv stream, consuming only
    /// the <c>--auto-*</c> / <c>--camera</c> / <c>--hide-hud</c> /
    /// <c>--autoshot*</c> / <c>--dock-sweep</c> flags. Everything else is
    /// returned untouched in <paramref name="rest"/> for the caller's own
    /// parsing (host/port/name/window/…). Pure and side-effect free.</summary>
    public static AutoProofPlan Parse(IReadOnlyList<string> args, out List<string> rest)
    {
        var plan = new AutoProofPlan();
        rest = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--auto-side")
            {
                if (i + 1 < args.Count && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var side))
                    plan.AutoSide = side;
                else
                    Console.Error.WriteLine("[monogame] --auto-side: expected an integer side id");
            }
            else if (a.StartsWith("--auto-side=", StringComparison.Ordinal))
            {
                if (int.TryParse(a.Substring("--auto-side=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s2))
                    plan.AutoSide = s2;
            }
            else if (a == "--auto-ready" || a == "--auto-ready=true")
                plan.AutoReady = true;
            else if (a == "--auto-ready=false")
                plan.AutoReady = false;
            else if (a == "--auto-launch")
            {
                plan.AutoLaunchRequested = true;
                // Optional hull token: consume the next arg only if it is a
                // quoted/named hull and not another flag.
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                    plan.LaunchHull = args[++i];
            }
            else if (a.StartsWith("--auto-launch=", StringComparison.Ordinal))
            {
                plan.AutoLaunchRequested = true;
                plan.LaunchHull = a.Substring("--auto-launch=".Length);
            }
            else if (a == "--auto-credits" || a == "--auto-credits=true")
                plan.AutoCredits = true;
            else if (a == "--auto-credits=false")
                plan.AutoCredits = false;
            else if (a == "--auto-screen" && i + 1 < args.Count)
                plan.AutoScreen = ParseScreen(args[++i]);
            else if (a.StartsWith("--auto-screen="))
                plan.AutoScreen = ParseScreen(a.Substring("--auto-screen=".Length));
            else if (a == "--auto-screen")
                plan.AutoScreen = AutoScreenKind.None;
            else if (a == "--auto-graphics-settings" || a == "--auto-graphics-settings=true")
                plan.AutoGraphicsSettings = true;
            else if (a == "--auto-graphics-settings=false")
                plan.AutoGraphicsSettings = false;
            else if (a == "--auto-goto")
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                    plan.GotoSector = args[++i];
            }
            else if (a.StartsWith("--auto-goto=", StringComparison.Ordinal))
            {
                plan.GotoSector = a.Substring("--auto-goto=".Length);
            }
            else if (a == "--auto-target")
            {
                if (i + 1 < args.Count)
                    plan.Target = ParseTarget(args[++i]);
            }
            else if (a.StartsWith("--auto-target=", StringComparison.Ordinal))
            {
                plan.Target = ParseTarget(a.Substring("--auto-target=".Length));
            }
            else if (a == "--auto-target-class")
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                    plan.AutoTargetClass = args[++i];
            }
            else if (a.StartsWith("--auto-target-class=", StringComparison.Ordinal))
            {
                plan.AutoTargetClass = a.Substring("--auto-target-class=".Length);
            }
            else if (a == "--auto-parity-commands") plan.AutoParityCommands = true;
            else if (a == "--auto-flight-parts") plan.AutoFlightParts = true;
            else if (a == "--auto-inventory" || a == "--auto-inventory=true") plan.AutoInventory = true;
            else if (a == "--auto-inventory=false") plan.AutoInventory = false;
            else if (a == "--auto-highlight")
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-')) plan.AutoHighlightSector = args[++i];
            }
            else if (a.StartsWith("--auto-highlight=", StringComparison.Ordinal))
                plan.AutoHighlightSector = a.Substring("--auto-highlight=".Length);
            else if (a == "--camera")
            {
                if (i + 1 < args.Count)
                {
                    string cam = args[++i];
                    if (IsCameraToken(cam))
                        plan.Camera = cam.ToLowerInvariant();
                    else
                        Console.Error.WriteLine($"[monogame] --camera: unknown mode '{cam}' (cockpit|chase|flyby|flybytarget|orbit|target|base|station|override)");
                }
            }
            else if (a.StartsWith("--camera=", StringComparison.Ordinal))
            {
                string cam = a.Substring("--camera=".Length);
                if (IsCameraToken(cam))
                    plan.Camera = cam.ToLowerInvariant();
            }
            else if (a == "--director" || a == "--director=true")
                plan.Director = true;
            else if (a == "--director=false")
                plan.Director = false;
            else if (a == "--director-seed")
            {
                if (i + 1 < args.Count && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sd))
                    plan.DirectorSeed = sd;
            }
            else if (a.StartsWith("--director-seed=", StringComparison.Ordinal))
            {
                if (int.TryParse(a.Substring("--director-seed=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sd2))
                    plan.DirectorSeed = sd2;
            }
            else if (a == "--follow")
            {
                if (i + 1 < args.Count && short.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fid))
                    plan.FollowShip = fid;
            }
            else if (a.StartsWith("--follow=", StringComparison.Ordinal))
            {
                if (short.TryParse(a.Substring("--follow=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fid2))
                    plan.FollowShip = fid2;
            }
            else if (a == "--hide-hud" || a == "--hide-hud=true")
                plan.HideHud = true;
            else if (a == "--hide-hud=false")
                plan.HideHud = false;
            else if (a == "--autoshots")
            {
                if (i + 1 < args.Count)
                    plan.AutoshotSeconds = ParseSecondsList(args[++i]);
            }
            else if (a.StartsWith("--autoshots=", StringComparison.Ordinal))
            {
                plan.AutoshotSeconds = ParseSecondsList(a.Substring("--autoshots=".Length));
            }
            else if (a == "--autoshot-dir")
            {
                if (i + 1 < args.Count)
                    plan.AutoshotDir = args[++i];
            }
            else if (a.StartsWith("--autoshot-dir=", StringComparison.Ordinal))
            {
                plan.AutoshotDir = a.Substring("--autoshot-dir=".Length);
            }
            else if (a == "--auto-suicide-at")
            {
                if (i + 1 < args.Count)
                    plan.AutoSuicideAt = ParseSecondsList(args[++i]);
            }
            else if (a.StartsWith("--auto-suicide-at=", StringComparison.Ordinal))
            {
                plan.AutoSuicideAt = ParseSecondsList(a.Substring("--auto-suicide-at=".Length));
            }
            else if (a == "--auto-dock-after")
            {
                if (i + 1 < args.Count && TryParseSeconds(args[++i], out var d))
                    plan.AutoDockAfter = d;
            }
            else if (a.StartsWith("--auto-dock-after=", StringComparison.Ordinal))
            {
                if (TryParseSeconds(a.Substring("--auto-dock-after=".Length), out var d2))
                    plan.AutoDockAfter = d2;
            }
            else if (a == "--dock-sweep")
            {
                if (i + 1 < args.Count && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    plan.DockSweepCount = Math.Max(0, n);
            }
            else if (a.StartsWith("--dock-sweep=", StringComparison.Ordinal))
            {
                if (int.TryParse(a.Substring("--dock-sweep=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n2))
                    plan.DockSweepCount = Math.Max(0, n2);
            }
            else if (a == "--exit-after")
            {
                if (i + 1 < args.Count && TryParseSeconds(args[++i], out var e))
                    plan.ExitAfter = e;
            }
            else if (a.StartsWith("--exit-after=", StringComparison.Ordinal))
            {
                if (TryParseSeconds(a.Substring("--exit-after=".Length), out var e2))
                    plan.ExitAfter = e2;
            }
            else
            {
                rest.Add(a);
            }
        }
        return plan;
    }

    private static AutoTargetKind ParseTarget(string s)
    {
        if (TargetKinds.TryGetValue(s.Trim(), out var kind))
            return kind;
        Console.Error.WriteLine($"[monogame] --auto-target: unknown kind '{s}' (enemy|friendly|station|aleph|none) - ignoring");
        return AutoTargetKind.None;
    }

    /// <summary>P639 — parse <c>--auto-screen</c> into the overlay to force open
    /// for a real-window autoshot (leaderboard|charsheet|teampane|none).</summary>
    public static AutoScreenKind ParseScreen(string s) => s.Trim().ToLowerInvariant() switch
    {
        "leaderboard" or "lb" or "b" => AutoScreenKind.Leaderboard,
        "charsheet" or "character" or "pilot" or "p" => AutoScreenKind.CharSheet,
        "teampane" or "team" or "t" => AutoScreenKind.TeamPane,
        "none" or "" => AutoScreenKind.None,
        _ => AutoScreenKind.None,
    };

    /// <summary>Parse a fractional-seconds value (comma-less, culture-invariant).</summary>
    public static bool TryParseSeconds(string s, out double v)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0;

    /// <summary>Parse a comma-separated seconds list ("5,10.5,20") into an
    /// ascending sorted array of non-negative doubles. Malformed entries are
    /// skipped. Pure + testable.</summary>
    public static double[] ParseSecondsList(string s)
    {
        var outList = new List<double>();
        foreach (var tok in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (TryParseSeconds(tok.Trim(), out var v))
                outList.Add(v);
        outList.Sort();
        return outList.Distinct().ToArray();
    }

    /// <summary>Whether ANY automation is requested (so a bare/default plan is
    /// inert and the client behaves exactly as a normal session). Pure.</summary>
    public bool AnyActive =>
        AutoSide.HasValue || AutoReady || AutoLaunchRequested ||
        AutoCredits || AutoScreen != AutoScreenKind.None || AutoGraphicsSettings ||
        GotoSector.Length > 0 || Target != AutoTargetKind.None || AutoTargetClass.Length > 0 ||
        HideHud || AutoParityCommands || AutoFlightParts || AutoInventory || AutoHighlightSector.Length > 0 || AutoshotSeconds.Length > 0 ||
        AutoDockAfter.HasValue || DockSweepCount > 0 || ExitAfter.HasValue ||
        AutoSuicideAt.Length > 0 ||
        Director || FollowShip != -1 || DirectorSeed.HasValue ||
        !string.Equals(Camera, "cockpit", StringComparison.OrdinalIgnoreCase);

    /// <summary>Accepted <c>--camera</c> mode tokens (P580 adds the native
    /// external modes; the director is armed separately via <c>--director</c>).</summary>
    public static bool IsCameraToken(string s) => s.ToLowerInvariant() switch
    {
        "cockpit" or "chase" or "flyby" or "flybytarget" or "orbit"
            or "target" or "base" or "station" or "override"
            or "freecam" or "orbitcam" or "command" or "commandtd" or "dronefollow" or "dronecockpit" => true,   // P612 — the spectator cameras
        _ => false,
    };

    /// <summary>Whether the given hull token is a numeric id.</summary>
    public static bool IsNumericToken(string token)
        => int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
}
