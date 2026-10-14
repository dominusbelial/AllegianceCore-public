using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

// ─────────────────────────────────────────────────────────────────────────────
// P474 — run_scenario: live-verify the parity signature mechanics that unit
// tests can't make you FEEL (P450 capture-needs-downed-shields, P451 base-
// under-attack alert, P455/P455b elimination) by running a real multi-bot
// choreography against a live server and returning a STRUCTURED observed-
// outcome report — the real wire events the bots saw, with timestamps, not a
// narrative.
//
// Design rule (unchanged): MCP sets INTENT; the choreography below flies the
// bots at tick rate via their own ConnectionSession peers. The model never
// steers per tick.
//
// Two presets:
//   bomber_run      — side A spawns an escorted bomber group whose bomber hull
//                     carries a real anti-base weapon resolved from the core
//                     (ScenarioPlanner), flies to side B's garrison and attacks
//                     the station; side B gets a defender contingent. Reports
//                     station shield/hull over time, the P451 alert(s) side B's
//                     bots received, whether the station died, and (when it was
//                     side B's last restart station) the P455 events.
//   capture_attempt — a side-A boarding-capable hull (c_habmBoard) tries to
//                     dock with side B's station TWICE: first at full shields
//                     (report shows the refusal), then after an escort knocks
//                     the shields below the downed-shield constant (report
//                     shows the capture + side change as the bots observed it).
//
// Every preset: takes optional size/sector args with defaults, cleans up its
// bots when done (or on the timeout), and list_active_bots reflects them while
// running (they are real ActiveBots, gated out of the generic Loop via
// ActiveBot.InScenario while the scenario's own controller drives them).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>P474 — the constant the P450 capture gate uses: the station's
/// shields must be STRICTLY below this fraction for a hostile board hull to
/// board/capture (native c_fcidDownedShield = 0.1f; the same literal
/// Sector.cs:1487 and Ship.cs:4025 carry).</summary>
internal sealed partial class BotManager
{
    /// <summary>P474 — the constant the P450 capture gate uses: the station's
    /// shields must be STRICTLY below this fraction for a hostile board hull to
    /// board/capture (native c_fcidDownedShield = 0.1f; the same literal
    /// Sector.cs:1487 and Ship.cs:4025 carry). Kept locally (Sector's copy is
    /// private) so the choreography can detect the knockdown phase without
    /// peeking at server internals.</summary>
    private const float ScenarioDownedShield = 0.1f;

    /// <summary>P496 — the distance from the station centre within which a
    /// boarder is genuinely PRESSING the dock (a real P450 dock attempt), used
    /// to gate the refusal confirmation. On the normal map the board hull
    /// bounces off the garrison at ~500-535 u (a real press); a boarder still
    /// ~790 u out (the debug map, where the Omega station's approach is
    /// obstructed) is NOT at the dock and must not record a refusal.</summary>
    private const float RefusalProximity = 600f;

    /// <summary>One timestamped observed-outcome row (wire reality, not a
    /// narrative): the scenario-relative seconds, a kind tag, and the detail
    /// payload (station fractions, chat text, side id, distance...).</summary>
    private readonly record struct ScenarioRow(double T, string Kind, object Detail);

    /// <summary>
    /// P474 — run one live-verification scenario preset and return its
    /// structured observed-outcome report (a JSON string the tool wraps). Spawns
    /// the preset's bots through the exact same <see cref="SpawnBot"/> path a
    /// caller would use manually, drives them as their own ConnectionSession
    /// peers for up to <paramref name="timeoutSeconds"/> real seconds, collects
    /// the real wire events the bots saw, then cleans up exactly the bots it
    /// spawned (each ends its own real connection). <paramref name="size"/>
    /// scales the attacking group; <paramref name="targetSector"/> pins the
    /// attack sector (default: the side-B garrison's own sector, discovered
    /// live). Any genuine preflight failure (no sides, no target station, an
    /// unresolvable plan) throws a specific error before anything touches the
    /// wire; a partial spawn rolls back.
    /// </summary>
    public string RunScenario(string preset, int size, short? targetSector, double timeoutSeconds)
    {
        if (!ServerReady)
            throw new InvalidOperationException($"run_scenario/{preset}: server probe not connected/validated — cannot run a scenario");
        if (!IsSupportedScenarioPreset(preset))
            throw new ArgumentException($"{nameof(RunScenario)}: preset must be 'bomber_run', 'capture_attempt' or 'elimination_run', got '{preset}'");
        if (size < 1) size = 1;
        if (timeoutSeconds <= 0) timeoutSeconds = 90;

        var sides = ValidSides();
        if (sides.Count < 2)
            throw new InvalidOperationException($"run_scenario/{preset}: a scenario needs two real sides; this server has: {string.Join(", ", sides)}");
        // The probe seats on one side; a bot seated on the probe's OWN side
        // stalls at an aleph instead of transiting warps (documented P429 server
        // reality), while the OPPOSING side transits reliably (vizfight only
        // transits its enemy side). The scenario's ATTACKER must warp to reach
        // the defender's garrison, so it is the side OPPOSING the probe; the
        // DEFENDER (whose station is the target, and which mostly stays home)
        // is the probe's own side — so its garrison is also probe-visible.
        short defender = ProbeSide >= 0 && sides.Contains(ProbeSide) ? ProbeSide : sides[0];
        short attacker = sides.First(s => s != defender);

        return preset switch
        {
            "bomber_run" => RunBomberRun(size, targetSector, timeoutSeconds, attacker, defender),
            "elimination_run" => RunElimination(size, targetSector, timeoutSeconds, attacker, defender),
            _ => RunCaptureAttempt(size, targetSector, timeoutSeconds, attacker, defender),
        };
    }

    // ── Shared scaffolding ────────────────────────────────────────────────

    /// <summary>Mark a just-spawned bot as scenario-owned so the generic Loop
    /// never races the scenario controller on it.</summary>
    private void OwnForScenario(string name)
    {
        lock (_gate)
            foreach (var b in _bots)
                if (b.Name == name) { b.InScenario = true; break; }
    }

    private ActiveBot ScenarioBot(string name)
    {
        lock (_gate)
            foreach (var b in _bots)
                if (b.Name == name) return b;
        throw new InvalidOperationException($"run_scenario: scenario bot '{name}' vanished");
    }

    /// <summary>Discover side B's garrison station in (or near) the target
    /// sector, from a connected side-B observer peer's OWN ClientWorld (only a
    /// same-side client sees a side's stations — the P429 visibility rule).
    /// The station sought is the one in the side-B DEFENDER's home sector: when
    /// no explicit <paramref name="targetSector"/> is given, that is the
    /// observer's own current sector (a freshly-seated defender sits in its
    /// home sector), which is where its garrison lives — never an arbitrary
    /// first side-B station in dictionary order. Returns its id, name, sector
    /// and centre position. Throws a specific error when the observer never
    /// reports a matching station.</summary>
    private (short Id, string Name, short Sector, Vector3 Center) FindTargetStation(
        ActiveBot observer, short sideB, short? targetSector)
    {
        // Implicit target = the defender's own home sector (where it sits now).
        short home = CsSector(observer.Cs!);
        short effTarget = targetSector ?? (home >= 0 ? home : (short)-1);
        var deadline = DateTime.UtcNow.AddMilliseconds(8000);
        while (DateTime.UtcNow < deadline)
        {
            var cs = observer.Cs!;
            lock (cs.Gate)
            {
                foreach (var st in cs.World.Stations.Values)
                {
                    if (st.SideId != sideB) continue;
                    if (effTarget >= 0 && st.ClusterId != effTarget) continue;
                    if (st.Position is not { Length: 3 } p) continue;
                    return (st.StationId, st.Name, st.ClusterId,
                        new Vector3(p[0], p[1], p[2]));
                }
            }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException(
            $"run_scenario: no side-{sideB} station found from observer '{observer.Name}'"
            + (effTarget >= 0 ? $" in sector {effTarget}" : "")
            + (targetSector is { } t ? $" (requested sector {t})" : " (defender home)")
            + " — the server's STATIONS_UPDATE/OT_station sync may not have delivered one");
    }

    /// <summary>Snapshot a station's REAL live state from an observer peer's own
    /// ClientWorld (StationLive = STATIONS_UPDATE(57); Stations = the static
    /// record with its SideId). present=false means the station left the world
    /// (destroyed).</summary>
    private (double Hull, double Shield, short Side, bool Present) ReadStation(ActiveBot observer, short stationId)
    {
        var cs = observer.Cs!;
        lock (cs.Gate)
        {
            cs.World.StationLive.TryGetValue(stationId, out var live);
            if (!cs.World.Stations.TryGetValue(stationId, out var st))
                return (live.HullFraction, live.ShieldFraction, (short)-1, false);
            return (live.HullFraction, live.ShieldFraction, st.SideId, true);
        }
    }

    /// <summary>The real 3-D pose of a scenario bot from ITS OWN ClientWorld
    /// (same-side self — always visible).</summary>
    private bool ScenarioPose(ActiveBot bot, out Vector3 pos)
    {
        var cs = bot.Cs!;
        lock (cs.Gate)
            if (cs.World.MyShipId is { } me && cs.World.Ships.TryGetValue(me, out var e) && e.Latest is { } lu)
            {
                pos = lu.Position;
                return true;
            }
        pos = Vector3.Zero;
        return false;
    }

    /// <summary>P497 — whether the boarder has DOCKED at the target station — a
    /// side-agnostic server-truth capture signal. After capture the station re-sides
    /// to the attacker and native continues docking, so the boarder's own
    /// <see cref="ShipEntity.StationId"/> becomes the target. It is read from the
    /// boarder's OWN world (a same-side self is always visible), which the side-B
    /// defender-observer cannot do: the instant a captured station flips to side A it
    /// becomes cross-side-INVISIBLE to the side-B observer (P429), so
    /// <c>ReadStation</c> on that observer can never witness the flip. The boarder's
    /// dock is the reliable signal.</summary>
    private static bool BoarderDockedAt(ActiveBot bot, short stationId)
    {
        var cs = bot.Cs!;
        lock (cs.Gate)
            return cs.World.Ships.TryGetValue(bot.ShipId, out var me) && me.StationId == stationId;
    }

    /// <summary>Transit (via the real warp router) or station-attack driving for
    /// one attacker. Before the bot reaches the station sector it transits real
    /// warps (the tested DriveCombat router); once in-sector it flies at the
    /// station centre holding <paramref name="stateM"/> (AllWeapons to attack,
    /// 0 to board), so a bomber group can reach a distant garrison and a board
    /// hull can reach a dock point.</summary>
    private void DriveScenarioAttacker(ActiveBot bot, short stationSector, Vector3 stationCenter, int stateM, double now)
    {
        using var frameScope = new DecisionFrameScope(bot.Cs);
        if (!IsParticipating(bot) || !RefreshLife(bot, bot.Cs!, now)) return;
        if (DecisionSnapshotEnabled && SnapshotFor(bot.Cs!).MissionGeneration != bot.Cs!.World.MissionGeneration) return;
        if (UpdatePodState(bot, bot.Cs!, now)) { DrivePodHome(bot, bot.Cs!, now); return; }
        short cur = CsSector(bot.Cs!);
        if (cur == stationSector)
        {
            bot.Arrived = true;
            // Arrival/brake straddle the station's hull: fly right at the centre
            // with weapons hot (AllWeapons). A non-board hull bounces at the
            // hull radius while its rounds keep impacting the station; a
            // shield-down board hull is excluded from the bounce and reaches
            // the dock footprint.
            CombatFlyTo(bot, stationCenter, 120f, 400f, stateM);
            return;
        }
        if (!bot.Arrived)
            DriveCombat(bot, now, null);   // transit to the garrison sector first
    }

    /// <summary>Drain NEW chat lines from an observer's real EventLog since
    /// <paramref name="cursor"/>, classifying the P451 base-under-attack alert
    /// (a team chat carrying the station type's real sound id + native text)
    /// and the P455 "<Side> is no more." elimination line. Returns the rows and
    /// the new cursor.</summary>
    private (List<ScenarioRow> Rows, long Cursor) DrainScenarioChats(
        ActiveBot observer, long cursor, string stationName, string botLabel, double t)
    {
        var rows = new List<ScenarioRow>();
        var cs = observer.Cs!;
        ChatMessage[] chats;
        long newCursor;
        lock (cs.Gate)
        {
            chats = cs.World.Events.ChatsAfter(cursor).ToArray();
            newCursor = cs.World.Events.ChatSequence;
        }
        foreach (var c in chats)
        {
            string msg = c.Message ?? "";
            bool alert = c.Cd.VoiceOver != 0
                && (msg.Contains("under attack", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("under assault", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("critically damaged", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("critical damage", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains(stationName, StringComparison.OrdinalIgnoreCase));
            bool eliminated = msg.Contains("is no more", StringComparison.OrdinalIgnoreCase);
            if (alert || eliminated)
                rows.Add(new ScenarioRow(t, alert ? "p451_base_under_attack" : "p455_eliminated_chat",
                    new { bot = botLabel, voiceOver = c.Cd.VoiceOver, chat = msg.Trim() }));
        }
        return (rows, newCursor);
    }

    /// <summary>Cleanup shared by both presets: release every scenario bot (each
    /// triggers its own real disconnect) and un-gate them (removal is enough —
    /// they're removed from _bots).</summary>
    private void EndScenario(IEnumerable<string> names)
    {
        try { ClearBots(names.ToList()); }
        catch { /* best-effort cleanup */ }
    }

    // ── bomber_run ────────────────────────────────────────────────────────

    private string RunBomberRun(int size, short? targetSector, double timeoutSeconds, short sideA, short sideB)
    {
        var plan = ScenarioPlanner.PlanBomberRun();
        var ev = new List<ScenarioRow>();
        var roles = new List<(string Name, string Role)>();
        short stationId = 0;
        string stationName = "";
        short stationSector = 0;
        Vector3 stationCenter = Vector3.Zero;
        double t0 = Now();

        try
        {
            // Side B defenders first — they are the observers that see the
            // side-B garrison (cross-side stations are invisible, P429).
            int defenders = Math.Max(2, size + 1);
            for (int i = 0; i < defenders; i++)
            {
                var rd = SpawnBot(plan.EscortHullId, plan.EscortHullName, sideB,
                    Catalog.DefaultParts(), "combat", targetSector);
                roles.Add((rd.Name, "defender"));
                OwnForScenario(rd.Name);
            }

            var observer = ScenarioBot(roles[0].Name);
            (stationId, stationName, stationSector, stationCenter) =
                FindTargetStation(observer, sideB, targetSector ?? null);
            ev.Add(new ScenarioRow(Now() - t0, "station_selected",
                new { stationId, name = stationName, sector = stationSector,
                      pos = new double[] { stationCenter.X, stationCenter.Y, stationCenter.Z },
                      defenderSide = sideB }));

            // Side A bombers (real anti-base weapon) + escorts.
            var bomberParts = Catalog.PartsForWeapon(plan.AntiBaseWeaponId, null, plan.BomberHullId, plan.BomberHullName, "", out _);
            for (int i = 0; i < size; i++)
            {
                var rb = SpawnBot(plan.BomberHullId, plan.BomberHullName, sideA, bomberParts, "combat", stationSector);
                roles.Add((rb.Name, "bomber"));
                OwnForScenario(rb.Name);
            }
            for (int i = 0; i < size; i++)
            {
                var re = SpawnBot(plan.EscortHullId, plan.EscortHullName, sideA,
                    Catalog.DefaultParts(), "combat", stationSector);
                roles.Add((re.Name, "escort"));
                OwnForScenario(re.Name);
            }

            ev.Add(new ScenarioRow(Now() - t0, "group_spawned",
                new { bombers = size, escorts = size, defenders,
                      bomberHull = plan.BomberHullName, antiBaseWeapon = plan.AntiBaseWeaponName }));

            // Let the join-burst settle so the observer streams the station.
            long chatCursor = 0;
            double lastSample = -10;
            bool stationDied = false;
            double? killedAt = null;
            double? underAttackFirst = null;
            bool attackerReachedStation = false;

            while (Now() - t0 < timeoutSeconds)
            {
                double t = Now() - t0;
                var bots = roles.Select(r => ScenarioBot(r.Name)).ToArray();

                // Drive attackers (transit if far, else hold-and-fire at the
                // station centre) and let the defenders loiter (a thin screen,
                // so the bomb run itself is observable).
                foreach (var bot in bots)
                {
                    string role = roles.First(r => r.Name == bot.Name).Role;
                    if (role == "defender")
                    {
                        LoiterCombat(bot, Now());
                        continue;
                    }
                    DriveScenarioAttacker(bot, stationSector, stationCenter,
                        ShipControlState.AllWeapons, Now());
                    if (bot.Arrived) attackerReachedStation = true;
                }

                // Sample station state ~every 1 s (STATIONS_UPDATE cadence is
                // ~2/s server-side) and drain the defender's real chat stream.
                var (hull, shield, side, present) = ReadStation(observer, stationId);
                if (t - lastSample >= 1.0)
                {
                    lastSample = t;
                    ev.Add(new ScenarioRow(t, "station_state",
                        new { shield = Round(shield), hull = Round(hull), side }));
                }
                var (rows, nc) = DrainScenarioChats(observer, chatCursor, stationName, "defender", t);
                chatCursor = nc;
                ev.AddRange(rows);
                foreach (var r in rows)
                    if (r.Kind == "p451_base_under_attack") underAttackFirst ??= t;

                if (!present && hull <= 0.001)
                {
                    stationDied = true;
                    killedAt = t;
                    ev.Add(new ScenarioRow(t, "station_destroyed",
                        new { stationId, name = stationName }));
                    // P455: if that was side B's last restart station, the side
                    // goes inactive and the game ends — capture whatever fired.
                    break;
                }

                // P455/P455b — read the defender's world for the elimination /
                // game-over side effects even if the station didn't vanish yet.
                lock (observer.Cs!.Gate)
                {
                    bool inactive = !observer.Cs.World.IsSideActive(sideB);
                    if (inactive)
                    {
                        ev.Add(new ScenarioRow(t, "p455_side_inactive", new { side = sideB }));
                        break;
                    }
                    if (observer.Cs.World.GameOverResult is { } go)
                    {
                        ev.Add(new ScenarioRow(t, "p455_game_over",
                            new { message = go.GameoverMessage, winner = go.SideWinner, na = GameOver.NA }));
                        break;
                    }
                }

                Thread.Sleep(100);
            }

            ev.Add(new ScenarioRow(Now() - t0, "scenario_end",
                new { duration = Round(Now() - t0), stationDied,
                      firstUnderAttackAlertAt = underAttackFirst, killedAt, attackerReachedStation }));

            var report = new
            {
                preset = "bomber_run",
                sideA,
                sideB,
                plan = new
                {
                    bomberHull = new { id = plan.BomberHullId, name = plan.BomberHullName },
                    antiBaseWeapon = new { id = plan.AntiBaseWeaponId, name = plan.AntiBaseWeaponName },
                    escortHull = new { id = plan.EscortHullId, name = plan.EscortHullName },
                },
                station = new { id = stationId, name = stationName, sector = stationSector },
                outcome = new
                {
                    stationDied,
                    attackerReachedStation,
                    killTime = killedAt,
                    firstUnderAttackAlertAt = underAttackFirst,
                    p451AlertCount = ev.Count(r => r.Kind == "p451_base_under_attack"),
                    p455Events = ev.Where(r => r.Kind.StartsWith("p455", StringComparison.Ordinal)).Count(),
                },
                events = ev.Select(r => new { t = r.T, kind = r.Kind, detail = r.Detail }),
                note = "events are the real wire events the side-B observer bot saw, timestamped from scenario start (s). shield/hull are StationLive fractions from STATIONS_UPDATE(57). If the station did not die in the window, no P455 elimination occurred (the station survived or is not a lone restart station).",
            };
            return JsonSerializer.Serialize(report);
        }
        finally
        {
            EndScenario(roles.Select(r => r.Name));
        }
    }

    // ── elimination_run ───────────────────────────────────────────────────

    /// <summary>P490 — the dedicated elimination preset (the planned P474b): a
    /// P455/P455b elimination choreography that drives enough escorted bombers
    /// with real anti-base weapons to DESTROY side B's garrison (its last restart
    /// station) and observes the whole win sequence — the station's death
    /// broadcast, the side-B observer's <c>IsSideActive</c> flipping false
    /// (SIDE_INACTIVE), the "&lt;side&gt; is no more." elimination chat, and the
    /// observer's <c>GameOverResult</c> with native's "won by outlasting
    /// all other sides." reason — then keeps watching for the P439/P480 rotation:
    /// the defender observer peer is migrated into a fresh <c>NotStarted</c>
    /// mission and kept seated (P480). Uses <paramref name="size"/> bombers
    /// (default ≥ 1; scale it to crack the garrison hull in the window).</summary>
    private string RunElimination(int size, short? targetSector, double timeoutSeconds, short sideA, short sideB)
    {
        var plan = ScenarioPlanner.PlanBomberRun();
        var ev = new List<ScenarioRow>();
        var roles = new List<(string Name, string Role)>();
        short stationId = 0;
        string stationName = "";
        short stationSector = 0;
        Vector3 stationCenter = Vector3.Zero;
        double t0 = Now();

        try
        {
            // Side B defenders first — the observers that see the side-B garrison.
            int defenders = Math.Max(1, size);
            for (int i = 0; i < defenders; i++)
            {
                var rd = SpawnBot(plan.EscortHullId, plan.EscortHullName, sideB,
                    Catalog.DefaultParts(), "combat", targetSector);
                roles.Add((rd.Name, "defender"));
                OwnForScenario(rd.Name);
            }
            var observer = ScenarioBot(roles[0].Name);
            (stationId, stationName, stationSector, stationCenter) =
                FindTargetStation(observer, sideB, targetSector ?? null);
            ev.Add(new ScenarioRow(Now() - t0, "station_selected",
                new { stationId, name = stationName, sector = stationSector,
                      pos = new double[] { stationCenter.X, stationCenter.Y, stationCenter.Z },
                      defenderSide = sideB }));

            // Side A bombers (real anti-base weapon) + escorts.
            var bomberParts = Catalog.PartsForWeapon(plan.AntiBaseWeaponId, null, plan.BomberHullId, plan.BomberHullName, "", out _);
            for (int i = 0; i < size; i++)
            {
                var rb = SpawnBot(plan.BomberHullId, plan.BomberHullName, sideA, bomberParts, "combat", stationSector);
                roles.Add((rb.Name, "bomber"));
                OwnForScenario(rb.Name);
            }
            for (int i = 0; i < size; i++)
            {
                var re = SpawnBot(plan.EscortHullId, plan.EscortHullName, sideA,
                    Catalog.DefaultParts(), "combat", stationSector);
                roles.Add((re.Name, "escort"));
                OwnForScenario(re.Name);
            }
            ev.Add(new ScenarioRow(Now() - t0, "group_spawned",
                new { bombers = size, escorts = size, defenders,
                      bomberHull = plan.BomberHullName, antiBaseWeapon = plan.AntiBaseWeaponName }));

            long chatCursor = 0;
            double lastSample = -10;
            bool stationDied = false;
            double? killedAt = null;
            double? underAttackFirst = null;
            bool attackerReachedStation = false;
            bool sideInactive = false;
            double? sideInactiveAt = null;
            double? gameOverAt = null;
            string? gameOverReason = null;

            while (Now() - t0 < timeoutSeconds)
            {
                double t = Now() - t0;
                var bots = roles.Select(r => ScenarioBot(r.Name)).ToArray();
                foreach (var bot in bots)
                {
                    string role = roles.First(r => r.Name == bot.Name).Role;
                    if (role == "defender") { LoiterCombat(bot, Now()); continue; }
                    DriveScenarioAttacker(bot, stationSector, stationCenter,
                        ShipControlState.AllWeapons, Now());
                    if (bot.Arrived) attackerReachedStation = true;
                }

                var (hull, shield, side, present) = ReadStation(observer, stationId);
                if (t - lastSample >= 1.0)
                {
                    lastSample = t;
                    ev.Add(new ScenarioRow(t, "station_state",
                        new { shield = Round(shield), hull = Round(hull), side }));
                }
                var (rows, nc) = DrainScenarioChats(observer, chatCursor, stationName, "defender", t);
                chatCursor = nc;
                ev.AddRange(rows);
                foreach (var r in rows)
                    if (r.Kind == "p451_base_under_attack") underAttackFirst ??= t;

                if (!stationDied && !present && hull <= 0.001)
                {
                    stationDied = true;
                    killedAt = t;
                    ev.Add(new ScenarioRow(t, "station_destroyed",
                        new { stationId, name = stationName }));
                }

                // The station death triggers DeactivateSide in the SAME server tick
                // (SIDE_INACTIVE(68) + "<side> is no more." chat + win-by-elimination
                // GAME_OVER), and the P480 rotation resets the world ~0.2 s later —
                // so read the transient terminal signals immediately and drain the
                // chat log again (the chat persists in the EventLog across rotation).
                if (stationDied)
                {
                    var (rows2, nc2) = DrainScenarioChats(observer, chatCursor, stationName, "defender", t);
                    chatCursor = nc2;
                    ev.AddRange(rows2);
                    foreach (var r in rows2)
                        if (r.Kind == "p451_base_under_attack") underAttackFirst ??= t;
                }

                bool terminal = false;
                lock (observer.Cs!.Gate)
                {
                    if (!observer.Cs.World.IsSideActive(sideB) && !sideInactive)
                    {
                        sideInactive = true;
                        sideInactiveAt = t;
                        ev.Add(new ScenarioRow(t, "p455_side_inactive", new { side = sideB }));
                        terminal = true;
                    }
                    if (observer.Cs.World.GameOverResult is { } go && gameOverAt is null)
                    {
                        gameOverAt = t;
                        gameOverReason = go.GameoverMessage;
                        ev.Add(new ScenarioRow(t, "p455_game_over",
                            new { message = go.GameoverMessage, winner = go.SideWinner, na = GameOver.NA }));
                        terminal = true;
                    }
                }
                // Bounded grace after a station death: if the ~0.2 s deactivation/
                // game-over window was missed (rotation reset the world first), fall
                // through to the rotation watch after a few seconds; the server log
                // is the authoritative backstop for the eliminated side.
                if (stationDied && !terminal && killedAt is { } ka && Now() - t0 - ka >= 8)
                    break;
                if (terminal)
                    break;

                Thread.Sleep(100);
            }

            // POST-GAME — P480 rotation: after the win, the mission rotates and the
            // connected defender observer is MIGRATED into a fresh NotStarted mission
            // and kept seated. Poll its world (bounded) for that signal: stage back
            // to NotStarted, side re-active, and the peer still holding a seat.
            double? rotatedAt = null;
            if (stationDied || sideInactive || gameOverAt is not null)
            {
                var rotDeadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < rotDeadline)
                {
                    lock (observer.Cs!.Gate)
                    {
                        int? stage = observer.Cs.World.Mission?.Stage;
                        bool hasSeat = observer.Cs.World.MyShipId is { } m && observer.Cs.World.Ships.ContainsKey(m);
                        bool sideActiveAgain = observer.Cs.World.IsSideActive(sideB);
                        if (RotationCompleted(stage, hasSeat, sideActiveAgain))
                        {
                            rotatedAt = Now() - t0;
                            ev.Add(new ScenarioRow(rotatedAt.Value, "rotation_back_to_notstarted",
                                new { side = sideB, stage, sideActive = sideActiveAgain, playerKeptSeated = hasSeat,
                                      newShipId = observer.Cs.World.MyShipId }));
                            break;
                        }
                    }
                    Thread.Sleep(200);
                }
            }

            ev.Add(new ScenarioRow(Now() - t0, "scenario_end",
                new { duration = Round(Now() - t0), stationDied,
                      firstUnderAttackAlertAt = underAttackFirst, killedAt,
                      attackerReachedStation, sideInactive, sideInactiveAt,
                      gameOverAt, gameOverReason, rotatedBackToNotStarted = rotatedAt is not null, rotatedAt }));

            var report = new
            {
                preset = "elimination_run",
                sideA,
                sideB,
                plan = new
                {
                    bomberHull = new { id = plan.BomberHullId, name = plan.BomberHullName },
                    antiBaseWeapon = new { id = plan.AntiBaseWeaponId, name = plan.AntiBaseWeaponName },
                    escortHull = new { id = plan.EscortHullId, name = plan.EscortHullName },
                },
                station = new { id = stationId, name = stationName, sector = stationSector },
                outcome = new
                {
                    stationDied, attackerReachedStation, killTime = killedAt,
                    firstUnderAttackAlertAt = underAttackFirst,
                    p451AlertCount = ev.Count(r => r.Kind == "p451_base_under_attack"),
                    sideInactive, sideInactiveAt, gameOverAt, gameOverReason,
                    rotatedBackToNotStarted = rotatedAt is not null, rotatedAt,
                    p455Events = ev.Where(r => r.Kind.StartsWith("p455", StringComparison.Ordinal)).Count(),
                },
                events = ev.Select(r => new { t = r.T, kind = r.Kind, detail = r.Detail }),
                note = "events are the real wire events the side-B observer bot saw, timestamped from scenario start (s). station_destroyed records the death of the side-B garrison; p455_side_inactive records IsSideActive(sideB) flipping false (SIDE_INACTIVE(68)); p455_eliminated_chat is the '<side> is no more.' broadcast; p455_game_over is the GAME_OVER with native's 'won by outlasting all other sides.' reason; rotation_back_to_notstarted records the P480 migration of the connected defender observer into a fresh NotStarted mission with the player kept seated.",
            };
            return JsonSerializer.Serialize(report);
        }
        finally
        {
            EndScenario(roles.Select(r => r.Name));
        }
    }

    // ── capture_attempt ───────────────────────────────────────────────────

    private string RunCaptureAttempt(int size, short? targetSector, double timeoutSeconds, short sideA, short sideB)
    {
        var plan = ScenarioPlanner.PlanCaptureAttempt();
        var start = Now();
        var ev = new List<ScenarioRow>();
        var roles = new List<(string Name, string Role)>();
        short stationId = 0;
        string stationName = "";
        short stationSector = 0;
        Vector3 stationCenter = Vector3.Zero;
        double t0 = Now();

        try
        {
            // Side B defenders (observers).
            int defenders = Math.Max(1, size);
            for (int i = 0; i < defenders; i++)
            {
                var rd = SpawnBot(plan.EscortHullId, plan.EscortHullName, sideB,
                    Catalog.DefaultParts(), "combat", targetSector);
                roles.Add((rd.Name, "defender"));
                OwnForScenario(rd.Name);
            }
            var observer = ScenarioBot(roles[0].Name);
            (stationId, stationName, stationSector, stationCenter) =
                FindTargetStation(observer, sideB, targetSector ?? null);
            ev.Add(new ScenarioRow(Now() - t0, "station_selected",
                new { stationId, name = stationName, sector = stationSector,
                      pos = new double[] { stationCenter.X, stationCenter.Y, stationCenter.Z },
                      defenderSide = sideB }));

            // Side A: the boarder (c_habmBoard) + the shield-knock escorts.
            var boarder = SpawnBot(plan.BoardingHullId, plan.BoardingHullName, sideA,
                Catalog.DefaultParts(), "combat", stationSector);
            roles.Add((boarder.Name, "boarder"));
            OwnForScenario(boarder.Name);
            // P496 — scale the attacking escort group with `size` (≥1): a single
            // Adv Scout is killed by the garrison's turrets ~5 s after it drops
            // the shields, so several escorts are needed to SUSTAIN the
            // downed-shield window until the slow board hull docks and captures.
            int escorts = Math.Max(1, size);
            var escortNames = new List<string>();
            for (int i = 0; i < escorts; i++)
            {
                var re = SpawnBot(plan.EscortHullId, plan.EscortHullName, sideA,
                    Catalog.DefaultParts(), "combat", stationSector);
                escortNames.Add(re.Name);
                roles.Add((re.Name, "escort"));
                OwnForScenario(re.Name);
            }

            ev.Add(new ScenarioRow(Now() - t0, "group_spawned",
                new { boardingHull = plan.BoardingHullName, escortHull = plan.EscortHullName, escorts, defenders }));

            long chatCursor = 0;
            double lastSample = -10;
            double shieldKnockedAt = -1;
            double capturedAt = -1;
            double? refusalConfirmedAt = null;
            double? boarderArrivedAt = null;
            bool captured = false;
            bool attackerReachedStation = false;
            short stationSideStart = sideB;

            while (Now() - t0 < timeoutSeconds)
            {
                double t = Now() - t0;
                var boardBot = ScenarioBot(boarder.Name);

                // PHASE 1+3 — the boarder drives at the station centre with NO
                // weapons (it boards, it doesn't shoot). At full shields (phase
                // 1) it is not a board candidate and bounces off the hull — the
                // refusal; after the shields drop below ScenarioDownedShield it
                // penetrates to the dock footprint and captures (phase 3).
                DriveScenarioAttacker(boardBot, stationSector, stationCenter, 0, Now());
                // PHASE 2 — the escorts close and hold AllWeapons to drain the
                // station's shields.
                //
                // P496 — the fast escorts HOLD AT HOME until the slow board hull
                // is present in the garrison sector. Measured live: an escort
                // (Adv Scout) reaches the garrison in ~14 s, drops the shields to
                // 0, is then killed by the garrison's turrets ~5 s later, and the
                // shields regenerate by ~30 s — long before the mandatory board
                // hull (Hvy Troop Transport, accel 15) arrives at ~34 s (P496 §4
                // finding). Attacking from the start means the downed-shield
                // window is gone before the boarder is at the dock, so the capture
                // never overlaps. Holding the escorts until the boarder is present
                // (then a fresh, sustained attack run — several escorts) makes the
                // shield-knock coincide with the boarder already pressing the dock:
                // the refusal at full shields confirms first, then the downed
                // shields let it capture.
                if (boardBot.Arrived)
                {
                    foreach (var en in escortNames)
                        DriveScenarioAttacker(ScenarioBot(en), stationSector, stationCenter, ShipControlState.AllWeapons, Now());
                }
                else
                {
                    foreach (var en in escortNames)
                        LoiterCombat(ScenarioBot(en), Now());
                }
                if (boardBot.Arrived) attackerReachedStation = true;
                // Defenders loiter (a thin screen; the capture is the point).
                for (int i = 1; i < roles.Count; i++)
                {
                    var d = roles[i];
                    if (d.Role == "defender") LoiterCombat(ScenarioBot(d.Name), Now());
                }

                var (hull, shield, side, present) = ReadStation(observer, stationId);
                if (t - lastSample >= 1.0)
                {
                    lastSample = t;
                    ev.Add(new ScenarioRow(t, "station_state",
                        new { shield = Round(shield), hull = Round(hull), side }));
                }

                // Phase 1 refusal: once the boarder is driving at the station
                // (has arrived in-sector) for a sustained window with the
                // shields still UP and the side unchanged, the full-shield dock
                // attempt has been rejected (P450). P496 — the refusal only
                // CONFIRMS when the boarder is actually pressing the dock
                // (within RefusalProximity of the station centre), not merely
                // in-sector: on the debug map an in-sector-but-~3000-u-away
                // boarder is NOT attempting a dock, and recording that as a
                // refusal would be a false positive. The boarder's closest
                // approach to the station centre is the real evidence.
                if (boardBot.Arrived && boarderArrivedAt is null) boarderArrivedAt = t;
                if (shield >= ScenarioDownedShield && boarderArrivedAt is { } ba
                    && t - ba >= 6 && refusalConfirmedAt is null
                    && ScenarioPose(boardBot, out var bpR) && Dist(bpR, stationCenter) < RefusalProximity)
                {
                    refusalConfirmedAt = t;
                    double dist = Dist(bpR, stationCenter);
                    ev.Add(new ScenarioRow(t, "capture_refused_full_shield",
                        new { stationSide = side, shield = Round(shield), boarderDistanceToCentre = Round(dist) }));
                }

                // Phase 2: shields knocked below the downed constant.
                if (shield < ScenarioDownedShield && shieldKnockedAt < 0)
                {
                    shieldKnockedAt = t;
                    ev.Add(new ScenarioRow(t, "shields_down",
                        new { shield = Round(shield), hull = Round(hull), downedConstant = ScenarioDownedShield }));
                }

                // Phase 3: capture — the station re-sides to the attacker. The
                // side-B defender-observer goes BLIND to the flip (the captured
                // station becomes side A, cross-side-invisible to it — P429 — yet
                // the stale record stays "present", so its `side` never changes).
                // The reliable server-truth signal is the side-A BOARDER now seeing
                // the station as its OWN (a same-side station is always visible to
                // it): ReadStation(boardBot) transitions from side -1 / absent to
                // side == sideA exactly on the capture. Also accept the boarder
                // docking (native continues docking after capture).
                var (_, _, bside, _) = ReadStation(boardBot, stationId);
                bool boarderSeesOwn = bside == sideA;
                bool boarderDocked = BoarderDockedAt(boardBot, stationId);
                if (side == sideA || boarderSeesOwn || boarderDocked)
                {
                    captured = true;
                    capturedAt = t;
                    ev.Add(new ScenarioRow(t, "capture_succeeded",
                        new { oldSide = stationSideStart, newSide = sideA, stationId, shield = Round(shield),
                              observedVia = boarderDocked ? "boarder_docked"
                                         : boarderSeesOwn ? "boarder_sees_own"
                                         : "side_flip" }));
                    break;
                }
                // Abandon only if the station vanished while the boarder is NOT at
                // the dock footprint (i.e. it was destroyed, not captured-and-hidden
                // from the observer). The guard deliberately does NOT require the
                // shields to be down — a successful capture RESTORES the station's
                // shield (post-capture heroic fudge → 0.7).
                bool pressing = ScenarioPose(boardBot, out var pPr)
                    && Dist(pPr, stationCenter) < RefusalProximity;
                if (!present && !pressing) break;

                // Drain the defender's chat (a capture may emit station-type
                // sound/text lines; keep them timestamped).
                var (rows, nc) = DrainScenarioChats(observer, chatCursor, stationName, "defender", t);
                chatCursor = nc;
                ev.AddRange(rows);

                Thread.Sleep(100);
            }

            ev.Add(new ScenarioRow(Now() - t0, "scenario_end",
                new { captured, attackerReachedStation, refusalConfirmed = refusalConfirmedAt is not null,
                      refusalAt = refusalConfirmedAt, shieldsDownAt = shieldKnockedAt, capturedAt }));

            var report = new
            {
                preset = "capture_attempt",
                sideA,
                sideB,
                plan = new
                {
                    boardingHull = new { id = plan.BoardingHullId, name = plan.BoardingHullName },
                    escortHull = new { id = plan.EscortHullId, name = plan.EscortHullName },
                },
                station = new { id = stationId, name = stationName, sector = stationSector },
                outcome = new
                {
                    refusalConfirmed = refusalConfirmedAt is not null,
                    refusalAt = refusalConfirmedAt,
                    attackerReachedStation,
                    shieldsDownAt = shieldKnockedAt,
                    captured,
                    capturedAt,
                    stationSideStart,
                    stationSideEnd = captured ? sideA : sideB,
                    downedShieldConstant = ScenarioDownedShield,
                },
                events = ev.Select(r => new { t = r.T, kind = r.Kind, detail = r.Detail }),
                note = "events are the real wire events the side-B observer bot saw, timestamped from scenario start (s). capture_refused_full_shield records the boarder closing on the station while shields are up with the side unchanged (the P450 refusal); shields_down records StationLive.ShieldFraction crossing 0.1; capture_succeeded records the STATIONS_UPDATE side flip B->A.",
            };
            return JsonSerializer.Serialize(report);
        }
        finally
        {
            EndScenario(roles.Select(r => r.Name));
        }
    }

    // ── small helpers ─────────────────────────────────────────────────────

    /// <summary>P490 — the set of run_scenario presets the harness knows how to
    /// drive. Pure so the acceptance of a preset is unit-testable without a live
    /// server (the P474 ScenarioPlanTests stance).</summary>
    internal static bool IsSupportedScenarioPreset(string preset) =>
        preset is "bomber_run" or "capture_attempt" or "elimination_run";

    /// <summary>P490 — P480 rotation detector, pure so it is unit-testable: a
    /// finished mission has rotated back to <c>NotStarted</c> AND kept its
    /// connected players seated when the observer's world reports the fresh
    /// mission stage (<paramref name="stage"/>, <see cref="MissionStageMessage"/>
    /// constants) as <c>StageNotStarted</c>, the peer still holds a seat
    /// (<paramref name="hasSeat"/>), and the side is active again in the fresh
    /// world (<paramref name="sideActive"/>).</summary>
    internal static bool RotationCompleted(int? stage, bool hasSeat, bool sideActive) =>
        stage == MissionStageMessage.StageNotStarted && hasSeat && sideActive;

    private static double Round(double v) => Math.Round(v, 3);
    private static float Dist(Vector3 a, Vector3 b) => (b - a).Length();
}
