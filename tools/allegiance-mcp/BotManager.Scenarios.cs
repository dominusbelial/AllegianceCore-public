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
        if (preset is not ("bomber_run" or "capture_attempt"))
            throw new ArgumentException($"run_scenario: preset must be 'bomber_run' or 'capture_attempt', got '{preset}'");
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

    /// <summary>Transit (via the real warp router) or station-attack driving for
    /// one attacker. Before the bot reaches the station sector it transits real
    /// warps (the tested DriveCombat router); once in-sector it flies at the
    /// station centre holding <paramref name="stateM"/> (AllWeapons to attack,
    /// 0 to board), so a bomber group can reach a distant garrison and a board
    /// hull can reach a dock point.</summary>
    private void DriveScenarioAttacker(ActiveBot bot, short stationSector, Vector3 stationCenter, int stateM, double now)
    {
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
            DriveCombat(bot, now);   // transit to the garrison sector first
    }

    /// <summary>Drain NEW chat lines from an observer's real EventLog since
    /// <paramref name="cursor"/>, classifying the P451 base-under-attack alert
    /// (a team chat carrying the station type's real sound id + native text)
    /// and the P455 "<Side> is no more." elimination line. Returns the rows and
    /// the new cursor.</summary>
    private (List<ScenarioRow> Rows, int Cursor) DrainScenarioChats(
        ActiveBot observer, int cursor, string stationName, string botLabel, double t)
    {
        var rows = new List<ScenarioRow>();
        var cs = observer.Cs!;
        ChatMessage[] chats;
        lock (cs.Gate) chats = cs.World.Events.Chats.Skip(cursor).ToArray();
        int newCursor = cursor + chats.Length;
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
            int chatCursor = 0;
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

            // Side A: the boarder (c_habmBoard) + the shield-knock escort.
            var boarder = SpawnBot(plan.BoardingHullId, plan.BoardingHullName, sideA,
                Catalog.DefaultParts(), "combat", stationSector);
            roles.Add((boarder.Name, "boarder"));
            OwnForScenario(boarder.Name);
            var escort = SpawnBot(plan.EscortHullId, plan.EscortHullName, sideA,
                Catalog.DefaultParts(), "combat", stationSector);
            roles.Add((escort.Name, "escort"));
            OwnForScenario(escort.Name);

            ev.Add(new ScenarioRow(Now() - t0, "group_spawned",
                new { boardingHull = plan.BoardingHullName, escortHull = plan.EscortHullName, defenders }));

            int chatCursor = 0;
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
                var escBot = ScenarioBot(escort.Name);

                // PHASE 1+3 — the boarder drives at the station centre with NO
                // weapons (it boards, it doesn't shoot). At full shields (phase
                // 1) it is not a board candidate and bounces off the hull — the
                // refusal; after the shields drop below ScenarioDownedShield it
                // penetrates to the dock footprint and captures (phase 3).
                DriveScenarioAttacker(boardBot, stationSector, stationCenter, 0, Now());
                // PHASE 2 — the escort closes and holds AllWeapons to drain the
                // station's shields.
                DriveScenarioAttacker(escBot, stationSector, stationCenter,
                    ShipControlState.AllWeapons, Now());
                if (boardBot.Arrived || escBot.Arrived) attackerReachedStation = true;
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
                // attempt has been rejected (P450). The boarder's closest
                // approach to the station centre is the real evidence.
                if (boardBot.Arrived && boarderArrivedAt is null) boarderArrivedAt = t;
                if (shield >= ScenarioDownedShield && boarderArrivedAt is { } ba
                    && t - ba >= 6 && refusalConfirmedAt is null)
                {
                    refusalConfirmedAt = t;
                    double dist = ScenarioPose(boardBot, out var bp) ? Dist(bp, stationCenter) : -1;
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

                // Phase 3: capture — the station re-sides to the attacker.
                if (side == sideA)
                {
                    captured = true;
                    capturedAt = t;
                    ev.Add(new ScenarioRow(t, "capture_succeeded",
                        new { oldSide = stationSideStart, newSide = sideA, stationId, shield = Round(shield) }));
                    break;
                }
                if (!present) break;

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

    private static double Round(double v) => Math.Round(v, 3);
    private static float Dist(Vector3 a, Vector3 b) => (b - a).Length();
}
