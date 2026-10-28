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
    // HUD-21b — the third target kind with a native ThingGeo preview target:
    // an asteroid (used only by real-window proof shots of the target pane).
    Asteroid,
    // P805b — the other two ThingGeo preview kinds (row 409): a probe (its real
    // mesh, probetypeigc.cpp:28-33) and a treasure (the buyable's model or the
    // flag's biosflag decal, treasureIGC.cpp:118-171). Selecting either is enough
    // for the pane: the preview gate needs only same-cluster, not proximity.
    Probe,
    Treasure,
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

    /// <summary>HUD-21b — seconds after launch to wait before emitting the
    /// <c>--auto-target</c>/<c>--auto-target-class</c> selection
    /// (<c>--auto-target-after</c>). 0 = immediately (the P572 behaviour). Needed
    /// when the thing to frame is staged AFTER the client is already flying
    /// (e.g. MCP bots spawned into the pilot's sector while the client runs).</summary>
    public double TargetDelaySeconds;

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

    /// <summary>P803 — delay in seconds (from seating) before the
    /// <c>--auto-launch</c> hull selection fires (<c>--auto-launch-after N</c>).
    /// 0 = the pre-P803 behaviour (launch as soon as docked). Exists so a
    /// second auto client can board the pilot BEFORE the carrier launches
    /// (boarding needs both ships docked).</summary>
    public double AutoLaunchAfterSeconds;

    /// <summary>P803 — board the named pilot as a turret gunner once seated and
    /// the mission started (<c>--auto-board "name"</c>): the production
    /// <see cref="ConnectionSession.SendBoardShip"/> path, retried until the
    /// world shows the parent link (boarding needs both ships docked, so the
    /// named pilot must NOT pass --auto-launch before this lands). Null/empty
    /// = never board. Real-window proof harness for the P803 gunner HUD.</summary>
    public string AutoBoard = "";

    /// <summary>P803 — the side leader turns native auto-accept on once seated
    /// (<c>--auto-accept</c>, FM_CS_AUTO_ACCEPT) so a second auto client joining the
    /// side mid-mission is seated without a manual accept.</summary>
    public bool AutoAccept;

    /// <summary>P803 — a boarded gunner holds its turret trigger once airborne
    /// (<c>--auto-gunner-fire</c>): proves turret fire end to end in a real window.</summary>
    public bool AutoGunnerFire;

    /// <summary>P803 — initial turret yaw in degrees for a boarded gunner (<c>--auto-turret-yaw DEG</c>).</summary>
    public float AutoTurretYawDegrees;

    /// <summary>P803 — launch as soon as this many boarded children ride this ship
    /// (<c>--auto-launch-when-boarded N</c>); <see cref="AutoLaunchAfterSeconds"/> is
    /// then only the upper bound. 0 = off.</summary>
    public int AutoLaunchWhenBoarded;

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

    /// <summary>P786 — pin the radar LOD for a proof frame
    /// (<c>--auto-radar all|default|target|minimal</c>). The FLIGHTKEYS proof
    /// cycles modes only right after launch, before a --auto-goto flight
    /// arrives, so a targeted fill-bar frame needs a pinned mode.</summary>
    public string AutoRadarMode = "";

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

    /// <summary>P805b — one timed scripted target selection inside a proof run
    /// (<c>--auto-target-seq</c>): the kind to select and the seconds after
    /// launch at which to select it. Selects WITHOUT engaging the autopilot, so
    /// a pane-preview proof can walk aleph → probe → treasure → enemy without
    /// flying out of the sector (an autopilot toward an aleph transits it).</summary>
    public readonly record struct TimedTarget(AutoTargetKind Kind, double AtSeconds);

    /// <summary>P805b — the scripted target-selection sequence
    /// (<c>--auto-target-seq "aleph@4,probe@14,..."</c>). Each entry fires once
    /// at its after-launch time; empty = the single <see cref="Target"/> path.</summary>
    public TimedTarget[] TargetSequence = [];

    /// <summary>P805b — hide the chat history for the proof frame
    /// (<c>--auto-hide-chat-history</c>): the session-only native
    /// <c>ShowChatHistoryHUD</c> toggle (WinTrek.cpp:2897,5074-5078), so scripted
    /// proof chatter (e.g. the <c>relocate</c> replies) does not sit over the top
    /// of the capture. The composer still draws.</summary>
    public bool AutoHideChatHistory;

    /// <summary>P805b — restrict a <c>treasure</c> target selection to one treasure
    /// CODE (<c>--auto-treasure part|cash|powerup|development|flag</c>).
    /// The mission's treasure set is random per spawn, so a proof that needs the
    /// PART-treasure art (the buyable's own model, treasureIGC.cpp:118-171) asks
    /// for it explicitly; null = the nearest treasure of any kind.</summary>
    public Allegiance.Core.TreasureCode? AutoTreasureCode;

    /// <summary>P805b — mount a PROBE dispenser in the docked loadout before
    /// launching (<c>--auto-carry-probe</c>): navigates the real ShopModel to the
    /// hull's Dispenser socket and fills it with the cheapest probe launcher the
    /// station offers (native BuyPartMenu rule, load.cpp:1680-1751 — equipment
    /// type + CanMount). Needed because the debug arena spawns a hull whose
    /// native preferred loadout carries a CHAFF dispenser, so the real N-key
    /// "drop mine or probe" would fire chaff (or nothing) instead of a probe.</summary>
    public bool AutoCarryProbe;

    /// <summary>P805b — seconds after launch to fire the mounted DISPENSER
    /// (<c>--auto-drop-probe-at</c>): the real N-key "drop mine or probe"
    /// (TK_DropMine, trekinput.cpp:1424 → FIRE_EXPENDABLE with
    /// EquipmentType.Dispenser). A probe-dispenser hull (e.g. the Scout's
    /// preferred "EWS Probe 1") deploys a real probe, which the target sequence
    /// can then select. Null = never.</summary>
    public double? AutoDropProbeAt;

    /// <summary>P805b — seconds after launch to summon an enemy into view
    /// (<c>--auto-summon-enemy-at</c>): relocates an MCP-harness enemy ship to a
    /// point ahead of the player via the port's privileged <c>relocate</c> chat
    /// cheat (Server.AdminCheats.cs, a PORT EXTENSION — native has no relocate
    /// token). It names every known enemy; the server refuses ships this pilot
    /// neither owns nor launched, so exactly the harness bot moves. Needs
    /// <c>liveSettings.adminCheats</c> with this pilot's name allow-listed.
    /// Null = never.</summary>
    public double? AutoSummonEnemyAt;

    /// <summary>P805b — how far ahead of the own ship to place the summoned
    /// enemy (<c>--auto-summon-enemy-dist</c>, default 500 m): close enough to
    /// read at 1080p, far enough not to collide.</summary>
    public float AutoSummonEnemyDistance = 500f;

    /// <summary>P862 — sector (name or id) every known harness bot on the pilot's
    /// own side is relocated into, <c>--auto-park-bots &lt;sector&gt;</c>: the port's
    /// privileged <c>relocate</c> chat cheat (Server.AdminCheats.cs, a PORT
    /// EXTENSION) parks them where a warp is about to be measured. Native
    /// discovery is SIDE-wide, so friendly ships in a sector are what make the
    /// server export that sector's rocks/stations to this pilot BEFORE he gets
    /// there — the live 200v200 situation (hundreds of friendly bots already
    /// fighting in Alderan) that a scripted single-pilot run otherwise lacks.
    /// Empty = never.</summary>
    public string AutoParkBotsSector = "";

    /// <summary>P862 — seconds after launch to relocate the harness bots
    /// (<c>--auto-park-bots-at</c>, default 6).</summary>
    public double AutoParkBotsAt = 6;

    /// <summary>P805b — seconds after launch to stop the ship
    /// (<c>--auto-stop-after</c>): the Backspace TK_ThrottleZero preset (-1 =
    /// kill throttle, WinTrek.cpp:10939-10960), so a long scripted sequence
    /// holds station instead of drifting into an aleph or a rock.</summary>
    public double? StopAfter;

    /// <summary>A dock-hull "kind" token accepted by <c>--auto-target</c>.</summary>
    private static readonly Dictionary<string, AutoTargetKind> TargetKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enemy"] = AutoTargetKind.Enemy,
        ["friendly"] = AutoTargetKind.Friendly,
        ["station"] = AutoTargetKind.Station,
        ["aleph"] = AutoTargetKind.Aleph,
        ["asteroid"] = AutoTargetKind.Asteroid,
        ["probe"] = AutoTargetKind.Probe,
        ["treasure"] = AutoTargetKind.Treasure,
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
            else if (a == "--auto-launch-after")
            {
                if (i + 1 < args.Count && double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var la))
                    plan.AutoLaunchAfterSeconds = la;
                else
                    Console.Error.WriteLine("[monogame] --auto-launch-after: expected seconds");
            }
            else if (a.StartsWith("--auto-launch-after=", StringComparison.Ordinal))
                _ = double.TryParse(a.Substring("--auto-launch-after=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out plan.AutoLaunchAfterSeconds);
            else if (a == "--auto-launch-when-boarded")
            {
                if (i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nb))
                { plan.AutoLaunchWhenBoarded = nb; i++; }
            }
            else if (a == "--auto-turret-yaw")
            {
                if (i + 1 < args.Count && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ty))
                { plan.AutoTurretYawDegrees = ty; i++; }
            }
            else if (a == "--auto-gunner-fire")
                plan.AutoGunnerFire = true;
            else if (a == "--auto-accept")
                plan.AutoAccept = true;
            else if (a == "--auto-board")
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                    plan.AutoBoard = args[++i];
                else
                    Console.Error.WriteLine("[monogame] --auto-board: expected a player name");
            }
            else if (a.StartsWith("--auto-board=", StringComparison.Ordinal))
                plan.AutoBoard = a.Substring("--auto-board=".Length);
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
            else if (a == "--auto-target-after")
            {
                if (i + 1 < args.Count && TryParseSeconds(args[i + 1], out var d3)) { plan.TargetDelaySeconds = d3; i++; }
            }
            else if (a.StartsWith("--auto-target-after=", StringComparison.Ordinal))
            {
                if (TryParseSeconds(a.Substring("--auto-target-after=".Length), out var d4)) plan.TargetDelaySeconds = d4;
            }
            else if (a == "--auto-target-seq")
            {
                if (i + 1 < args.Count) plan.TargetSequence = ParseTargetSequence(args[++i]);
            }
            else if (a.StartsWith("--auto-target-seq=", StringComparison.Ordinal))
            {
                plan.TargetSequence = ParseTargetSequence(a.Substring("--auto-target-seq=".Length));
            }
            else if (a == "--auto-hide-chat-history" || a == "--auto-hide-chat-history=true")
                plan.AutoHideChatHistory = true;
            else if (a == "--auto-hide-chat-history=false")
                plan.AutoHideChatHistory = false;
            else if (a == "--auto-treasure")
            {
                if (i + 1 < args.Count) plan.AutoTreasureCode = ParseTreasureCode(args[++i]);
            }
            else if (a.StartsWith("--auto-treasure=", StringComparison.Ordinal))
            {
                plan.AutoTreasureCode = ParseTreasureCode(a.Substring("--auto-treasure=".Length));
            }
            else if (a == "--auto-carry-probe" || a == "--auto-carry-probe=true")
                plan.AutoCarryProbe = true;
            else if (a == "--auto-carry-probe=false")
                plan.AutoCarryProbe = false;
            else if (a == "--auto-drop-probe-at")
            {
                if (i + 1 < args.Count && TryParseSeconds(args[++i], out var dp)) plan.AutoDropProbeAt = dp;
            }
            else if (a.StartsWith("--auto-drop-probe-at=", StringComparison.Ordinal))
            {
                if (TryParseSeconds(a.Substring("--auto-drop-probe-at=".Length), out var dp2)) plan.AutoDropProbeAt = dp2;
            }
            else if (a == "--auto-park-bots")
            {
                if (i + 1 < args.Count) plan.AutoParkBotsSector = args[++i];
            }
            else if (a.StartsWith("--auto-park-bots=", StringComparison.Ordinal))
            {
                plan.AutoParkBotsSector = a.Substring("--auto-park-bots=".Length);
            }
            else if (a == "--auto-park-bots-at")
            {
                if (i + 1 < args.Count && TryParseSeconds(args[++i], out var pb)) plan.AutoParkBotsAt = pb;
            }
            else if (a.StartsWith("--auto-park-bots-at=", StringComparison.Ordinal))
            {
                if (TryParseSeconds(a.Substring("--auto-park-bots-at=".Length), out var pb2)) plan.AutoParkBotsAt = pb2;
            }
            else if (a == "--auto-summon-enemy-at")
            {
                if (i + 1 < args.Count && TryParseSeconds(args[++i], out var se)) plan.AutoSummonEnemyAt = se;
            }
            else if (a.StartsWith("--auto-summon-enemy-at=", StringComparison.Ordinal))
            {
                if (TryParseSeconds(a.Substring("--auto-summon-enemy-at=".Length), out var se2)) plan.AutoSummonEnemyAt = se2;
            }
            else if (a == "--auto-summon-enemy-dist")
            {
                if (i + 1 < args.Count
                    && float.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var sd)
                    && float.IsFinite(sd) && sd > 0f)
                    plan.AutoSummonEnemyDistance = sd;
            }
            else if (a.StartsWith("--auto-summon-enemy-dist=", StringComparison.Ordinal))
            {
                if (float.TryParse(a.Substring("--auto-summon-enemy-dist=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var sd2)
                    && float.IsFinite(sd2) && sd2 > 0f)
                    plan.AutoSummonEnemyDistance = sd2;
            }
            else if (a == "--auto-stop-after")
            {
                if (i + 1 < args.Count && TryParseSeconds(args[++i], out var st)) plan.StopAfter = st;
            }
            else if (a.StartsWith("--auto-stop-after=", StringComparison.Ordinal))
            {
                if (TryParseSeconds(a.Substring("--auto-stop-after=".Length), out var st2)) plan.StopAfter = st2;
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
            else if (a == "--auto-radar" && i + 1 < args.Count)
            {
                plan.AutoRadarMode = args[++i];
            }
            else if (a.StartsWith("--auto-radar=", StringComparison.Ordinal))
            {
                plan.AutoRadarMode = a.Substring("--auto-radar=".Length);
            }
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

    /// <summary>P805b — parse <c>--auto-target-seq "kind@seconds,..."</c> into a
    /// time-ordered sequence of scripted target selections. Entries are sorted by
    /// time; a malformed entry (unknown kind, missing/invalid time) is skipped
    /// with a stderr note so a typo cannot silently re-time the run. Pure.</summary>
    public static TimedTarget[] ParseTargetSequence(string s)
    {
        var list = new List<TimedTarget>();
        foreach (var raw in s.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string entry = raw.Trim();
            int at = entry.IndexOf('@');
            if (at <= 0 || at == entry.Length - 1)
            {
                Console.Error.WriteLine($"[monogame] --auto-target-seq: expected kind@seconds, got '{entry}' - skipped");
                continue;
            }
            string kindTok = entry[..at].Trim();
            if (!TargetKinds.TryGetValue(kindTok, out var kind) || kind == AutoTargetKind.None)
            {
                Console.Error.WriteLine($"[monogame] --auto-target-seq: unknown kind '{kindTok}' - skipped");
                continue;
            }
            if (!TryParseSeconds(entry[(at + 1)..].Trim(), out var at2))
            {
                Console.Error.WriteLine($"[monogame] --auto-target-seq: bad time in '{entry}' - skipped");
                continue;
            }
            list.Add(new TimedTarget(kind, at2));
        }
        list.Sort((x, y) => x.AtSeconds.CompareTo(y.AtSeconds));
        return list.ToArray();
    }

    /// <summary>P805b — parse an <c>--auto-treasure</c> code token. Null (ignored)
    /// on an unknown token so a typo cannot silently switch the target kind.</summary>
    private static Allegiance.Core.TreasureCode? ParseTreasureCode(string s)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "part": return Allegiance.Core.TreasureCode.Part;
            case "cash": return Allegiance.Core.TreasureCode.Cash;
            case "powerup": return Allegiance.Core.TreasureCode.Powerup;
            case "development" or "dev": return Allegiance.Core.TreasureCode.Development;
            case "flag": return Allegiance.Core.TreasureCode.Flag;
            default:
                Console.Error.WriteLine($"[monogame] --auto-treasure: unknown code '{s}' (part|cash|powerup|development|flag) - ignoring");
                return null;
        }
    }

    private static AutoTargetKind ParseTarget(string s)
    {
        if (TargetKinds.TryGetValue(s.Trim(), out var kind))
            return kind;
        Console.Error.WriteLine($"[monogame] --auto-target: unknown kind '{s}' (enemy|friendly|station|aleph|asteroid|probe|treasure|none) - ignoring");
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
        AutoRadarMode.Length > 0 ||
        AutoDockAfter.HasValue || DockSweepCount > 0 || ExitAfter.HasValue ||
        AutoSuicideAt.Length > 0 ||
        TargetSequence.Length > 0 || AutoDropProbeAt.HasValue || AutoSummonEnemyAt.HasValue
            || AutoParkBotsSector.Length > 0
            || StopAfter.HasValue || AutoCarryProbe || AutoTreasureCode.HasValue ||
        AutoHideChatHistory ||
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
