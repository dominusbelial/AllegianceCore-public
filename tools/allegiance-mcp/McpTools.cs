using System.Text.Json;
using Allegiance.Core.Wire;
using ModelContextProtocol.Protocol;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P426 — the MCP tool surface: exact <see cref="Tool"/> definitions (with
/// hand-written input schemas) and the dispatcher that turns a
/// <c>tools/call</c> into a real action against the <see cref="BotManager"/>.
/// Four tools: <c>spawn_bot</c> (primary), and read-only <c>list_hulls</c>,
/// <c>list_parts</c>, <c>list_active_bots</c>. Errors are real, specific
/// <c>isError</c> results — never silent no-ops.
/// </summary>
internal static class McpTools
{
    public static IReadOnlyList<Tool> Definitions()
    {
        return
        [
            new Tool
            {
                Name = "spawn_bot",
                Description = "Spawn-and-forget: connect a real bot to the live Allegiance server on the requested side, seat it, buy the loadout and launch it. Returns a short real confirmation (ship id, hull, side). The bot persists and runs its behavior in the background for the life of this MCP server process.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        hull = new { type = "string", description = "Hull name or unique substring (see list_hulls). Exact name or a unique case-insensitive substring resolves; ambiguous/none is an error." },
                        side = new { type = "integer", @enum = new[] { 0, 1 }, description = "Side id to seat on (see list_active_bots / server config). Omit to auto-pick the first available side." },
                        parts = new { type = "array", items = new { type = "string" }, description = "Optional part names/substrings for the loadout (see list_parts). Omit for the real playable default loadout (weapon/afterburner/cloak/shield)." },
                        behavior = new { type = "string", @enum = new[] { "idle", "weave", "combat" }, @default = "idle", description = "Behavior preset: 'idle' seats but sends no movement; 'weave' runs the real loiter/weave flight loop (moving ship); 'combat' engages real cross-side gunfire — without target_sector it attacks any different-side ship co-located in its own cluster, with target_sector it first navigates real warps to that sector then attacks there." },
                        target_sector = new { type = "string", description = "Optional (combat only): a real sector NAME or numeric id to fly real warps to before engaging (pass the SAME value to bots on both sides to make them converge and fight in one place). Resolved exact-name-over-substring like hulls/parts; a numeric id matches directly. Ambiguous/no-match is an error." },
                        launcher = new { type = "string", description = "Optional real launcher/dispenser for the loadout (P433/P435): a name/substring resolves a real one of ANY expendable kind — missile (e.g. LRM Killer 1), mine, chaff, or probe dispenser; default picks the default fighter-mounted missile launcher; none/absent = gun-only. Checked against the hull real capability mask; one that does not mount falls back to gun-only (reported)." },
                    },
                    required = new[] { "hull" },
                }),
            },
            new Tool
            {
                Name = "list_hulls",
                Description = "Read-only: every hull in the real static core (PCore001.igc) with its id and name, so a caller can pick a valid hull for spawn_bot instead of guessing.",
                InputSchema = Schema(new { type = "object" }),
            },
            new Tool
            {
                Name = "list_parts",
                Description = "Read-only: every part in the real static core with its id, name and equipment category, so a caller can pick valid parts for spawn_bot instead of guessing.",
                InputSchema = Schema(new { type = "object" }),
            },
            new Tool
            {
                Name = "list_active_bots",
                Description = "Read-only: every bot this MCP server process has spawned and is still connected to, with its real live ship id, hull, side, sector, position, (for combat bots) current target ship id, and — when a bot is under a real accepted teammate order — the specific `orderedTarget` ship id it is obeying (distinct from its spawn-time behavior and its current live target). This is the only visibility into spawned bots; there is intentionally no despawn/command tool.",
                InputSchema = Schema(new { type = "object" }),
            },
            new Tool
            {
                Name = "spawn_scenario",
                Description = "Spawn a whole fight in one call: `size` real 'combat' bots PER SIDE (2×size total), each spawned through the exact same path as a manual spawn_bot call, split evenly across the server's first two real sides. Optional `hull` (default = the same real fighter spawn_bot/vizfight use) and optional `target_sector` (passed identically to every bot on both sides so they converge). Returns a per-side spawn summary; any real per-bot failure is named individually, never swallowed.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        size = new { type = "integer", description = "Bots PER SIDE to spawn (total = 2×size). Any positive integer; 5 / 10 / 15 are the intended presets for measuring the concurrency sweet spot." },
                        hull = new { type = "string", description = "Optional hull name/substring used for every bot (see list_hulls). Defaults to the same real fighter spawn_bot / vizfight use." },
                        target_sector = new { type = "string", description = "Optional real sector NAME or numeric id to fly real warps to before engaging — passed identically to every bot on both sides, so they rendezvous and fight in one place." },
                        launcher = new { type = "string", description = "Optional real launcher/dispenser for the loadout (P433/P435): a name/substring resolves a real one of ANY expendable kind — missile (e.g. LRM Killer 1), mine, chaff, or probe dispenser; default picks the default fighter-mounted missile launcher; none/absent = gun-only. Checked against the hull real capability mask; one that does not mount falls back to gun-only (reported)." },
                    },
                    required = new[] { "size" },
                }),
            },
            new Tool
            {
                Name = "get_perf_stats",
                Description = "Read-only: aggregate REAL per-bot update cadence across every active bot (or restrict to one bot via the optional `bot` filter). For update INTERVAL (seconds between successive real position updates) and JUMP DISTANCE (meters moved per update) each: pooled mean, nearest-rank p95, and the single worst offender (bot name + its worst single-sample value). A growing interval / jump distance as bot count rises is the real, measured 'teleporting' symptom — not a guess. Aggregate is over the last 200 real samples per bot.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        bot = new { type = "string", description = "Optional bot-name substring to restrict the aggregate to a single bot (pinpoints a specific offender)." },
                    },
                }),
            },
            new Tool
            {
                Name = "run_hull_matrix",
                Description = "Systematically run a real combat scenario for every (hull × weapon × size) combination in the cartesian product of the requested lists, capture real per-combination balance/performance data, and return one row per combination. For each combination it spawns `size` real 'combat' bots PER SIDE with that exact hull+weapon loadout, lets them fight for a real observation window, captures real KILL_SHIP-based combat outcomes (kill count per side, time-to-first-kill, survivors per side) and the real per-bot update cadence, then clears exactly that combination's own bots (each ends its own real connection) before the next combination — so measurements never bleed across loadouts. A weapon that does not mount on a hull's real capability mask is a real, specific per-combination error row, never a silent skip or a crash of the whole matrix.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        hulls = new { type = "array", items = new { type = "string" }, description = "REQUIRED: real hull names/substrings to test (see list_hulls). Each resolves exact-over-substring like spawn_bot; a weapon that doesn't mount on a hull is a real, specific per-combination error row." },
                        weapons = new { type = "array", items = new { type = "string" }, description = "Optional real weapon part names/substrings (see list_parts). Defaults to the real default weapon spawn_bot/spawn_scenario use when omitted." },
                        sizes = new { type = "array", items = new { type = "integer" }, description = "Optional per-side bot counts. Defaults to [5, 10, 15] (the P430 presets)." },
                        target_sector = new { type = "string", description = "Optional real sector NAME or numeric id to fly real warps to before engaging — passed identically to every bot on both sides so they converge and fight in one place. Omit to fight in place (this Test Mission homes both teams in the same starting sector, so combat begins immediately)." },
                        observe_seconds = new { type = "number", description = "Optional real observation window per combination (real seconds of live combat). Default 45 — long enough for the rendezvous/engagement to produce real combat." },
                        launcher = new { type = "string", description = "Optional real launcher/dispenser for the loadout (P433/P435): a name/substring resolves a real one of ANY expendable kind — missile (e.g. LRM Killer 1), mine, chaff, or probe dispenser; default picks the default fighter-mounted missile launcher; none/absent = gun-only. Checked against the hull real capability mask; one that does not mount falls back to gun-only (reported)." },
                    },
                    required = new[] { "hulls" },
                }),
            },
            new Tool
            {
                Name = "run_scenario",
                Description = "P474/P490 — run ONE live-verification parity scenario against the live server and return its structured observed-outcome report (the real wire events the bots saw, with timestamps, not a narrative). Presets: 'bomber_run' (side A spawns an escorted bomber group whose bomber carries a real anti-base weapon resolved from the core, flies to side B's garrison and attacks the station; side B gets a defender contingent; reports station shield/hull over time, the P451 base-under-attack alert(s) side B received, whether the station died, and — when it was side B's last restart station — the P455 SIDE_INACTIVE/'is no more.'/GAME_OVER events), 'capture_attempt' (a side-A boarding-capable c_habmBoard hull tries to dock with side B's station twice — first at full shields where the report shows the P450 refusal, then after an escort knocks the shields below the downed-shield constant where it shows the capture + side change as the bots observed it), and 'elimination_run' (P490/P474b — like bomber_run but scaled to DESTROY side B's last restart station and reports the full P455/P455b win sequence — station death, SIDE_INACTIVE, '<side> is no more.', GAME_OVER 'won by outlasting all other sides.' — then the P480 rotation back to a fresh NotStarted mission with the connected defender peers kept seated). All three take optional size/sector args with defaults, clean up their bots when done (or on the timeout), and list_active_bots reflects them while running.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        preset = new { type = "string", @enum = new[] { "bomber_run", "capture_attempt", "elimination_run" }, description = "REQUIRED: which parity scenario to run live." },
                        size = new { type = "integer", description = "Optional group size scaling (default 1). bomber_run/elimination_run: bombers (and equal escorts) on side A; capture_attempt: the attacker/defender cohort size." },
                        target_sector = new { type = "string", description = "Optional real sector NAME or numeric id for the side-B garrison to attack. Default = the side-B garrison's own sector, discovered live from a side-B observer's world." },
                        timeout_seconds = new { type = "number", description = "Optional observation window in real seconds (default 90). The scenario stops early the moment it observes its terminal outcome (station destroyed / captured / side eliminated / game over); on timeout it cleans up and reports what was observed." },
                    },
                    required = new[] { "preset" },
                }),
            },
            new Tool
            {
                Name = "invest",
                Description = "P486 — fund a REAL investment on the chosen side through the harness commander's P473-granted wallet, then report the observed BUCKET_STATUS progress (or completion + the drone that appeared on the wire). `bucket` is a drone-type or station-type bucket name or id (resolved with the same ids the server bakes into its list — see list_drones context); a development bucket is refused. Donates `amount` (default: the remaining price), so completing a bucket also requires the build time (TimeToBuild seconds) to elapse, which the tool waits out up to `observe_seconds`. Works entirely as an ordinary same-side player — the native drone AI then flies the spawned drone.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        side = new { type = "integer", description = "REQUIRED: side id to invest on (the commander seats on it)." },
                        bucket = new { type = "string", description = "REQUIRED: drone-type / station-type bucket NAME or id to invest in (e.g. '.Miner', 'Wingman', or a numeric id). Resolved exact-over-unique-substring." },
                        amount = new { type = "integer", description = "Optional money to donate. Default = the remaining price (to completion). Capped at the outstanding balance and the commander wallet." },
                        observe_seconds = new { type = "number", description = "Optional observation window in real seconds (default 20). The tool waits up to this for the bucket to complete (money full AND build time elapsed) and for the spawned drone to appear on the wire; otherwise returns current progress." },
                    },
                    required = new[] { "side", "bucket" },
                }),
            },
            new Tool
            {
                Name = "list_drones",
                Description = "P486 — every drone (same-side NPC) the harness commander observes on the wire for the chosen side: ship id, drone type (from its real hull), hull id, sector, position, state and character name where observable. Deliberately flags what a player's view CANNOT see: cargo/ore and the drone's internal order are not on the peer ship wire, so they are not claimed. `type` optionally filters by drone-type name or hull id substring.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        side = new { type = "integer", description = "REQUIRED: side id to list drones for." },
                        type = new { type = "string", description = "Optional drone-type name or hull-id substring filter (e.g. 'miner', 'wingman')." },
                    },
                    required = new[] { "side" },
                }),
            },
            new Tool
            {
                Name = "order_drone",
                Description = "P486 — issue a REAL order to a drone through the P469/P457 path: an Individual quick-chat (FM_CS_CHATMESSAGE 23) from the harness commander naming the drone as recipient, with the native command id and a resolved target — exactly what the server's CommandToDrone consumes. `command` is one of: default, goto, attack, defend, mine, build, donothing, repair, pickup, join. `target` is a descriptor: 'asteroid:<id>', 'ship:<id>', 'station:<id>', 'warp:<id>', a bare ship id, or none/omitted. Returns the drone's observed reaction: the server's 'Affirmative.' reply and any sector/state change.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        droneId = new { type = "integer", description = "REQUIRED: the drone's ship id (see list_drones)." },
                        command = new { type = "string", description = "REQUIRED: native command name (default, goto, attack, defend, mine, build, donothing, repair, pickup, join)." },
                        target = new { type = "string", description = "Optional target descriptor: 'asteroid:<id>' / 'ship:<id>' / 'station:<id>' / 'warp:<id>' / a bare ship id, or none/omitted for a targetless (e.g. donothing) order." },
                    },
                    required = new[] { "droneId", "command" },
                }),
            },
        ];
    }

    private static JsonElement Schema(object schema) =>
        JsonSerializer.SerializeToElement(schema);   // default naming: emit exact JSON-Schema keys (enum/default)

    // ── Dispatch ───────────────────────────────────────────────────────────

    public static CallToolResult Handle(string name, IDictionary<string, JsonElement>? args, BotManager mgr)
    {
        args ??= new Dictionary<string, JsonElement>();
        try
        {
            return name switch
            {
                "spawn_bot" => SpawnBot(args, mgr),
                "spawn_scenario" => SpawnScenario(args, mgr),
                "get_perf_stats" => GetPerfStats(args, mgr),
                "run_hull_matrix" => RunHullMatrix(args, mgr),
                "run_scenario" => RunScenario(args, mgr),
                "invest" => Invest(args, mgr),
                "list_drones" => ListDrones(args, mgr),
                "order_drone" => OrderDrone(args, mgr),
                "list_hulls" => Ok(JsonSerializer.Serialize(Catalog.RepresentativeCore().HullTypes.Values
                    .OrderBy(h => h.HullId).Select(h => new { id = h.HullId, name = h.Name }))),
                "list_parts" => Ok(JsonSerializer.Serialize(Catalog.AllParts()
                    .Select(p => new { id = p.Id, name = p.Name, category = p.Category }))),
                "list_active_bots" => Ok(JsonSerializer.Serialize(mgr.ActiveBots()
                    .Select(b => new { name = b.Name, shipId = b.ShipId, side = b.Side, hull = b.Hull, sector = b.Sector, behavior = b.Behavior, target = b.Target, orderedTarget = b.OrderedTarget, pos = b.Position }))),
                _ => Err($"unknown tool '{name}'"),
            };
        }
        catch (ArgumentException ex)
        {
            return Err(ex.Message);                       // unresolvable hull/part/side, bad behavior
        }
        catch (InvalidOperationException ex)
        {
            return Err(ex.Message);                       // server-side refusal / not-ready
        }
        catch (Exception ex)
        {
            return Err($"spawn failed unexpectedly: {ex.Message}");
        }
    }

    private static CallToolResult SpawnBot(IDictionary<string, JsonElement> args, BotManager mgr)
    {
        string? hullArg = OptStr(args, "hull");
        if (string.IsNullOrWhiteSpace(hullArg))
            return Err("spawn_bot: required argument `hull` (name or substring) is missing");

        string behavior = OptStr(args, "behavior") is { } bb && bb.Length > 0 ? bb : "idle";
        if (behavior is not ("idle" or "weave" or "combat"))
            return Err($"spawn_bot: behavior must be 'idle', 'weave' or 'combat', got '{behavior}'");

        // target_sector is combat-only. Resolve it against the probe's live
        // sector map (same exact-over-substring rules as hulls/parts) BEFORE
        // anything touches the wire, so a bad name fails cheaply.
        short? targetSector = null;
        if (OptStr(args, "target_sector") is { } ts && ts.Length > 0)
        {
            if (behavior != "combat")
                return Err("spawn_bot: `target_sector` only applies to behavior 'combat'");
            targetSector = Catalog.ResolveSector(ts, mgr.ProbeClusters()).Id;
        }
        else if (args.ContainsKey("target_sector"))
        {
            return Err("spawn_bot: `target_sector` must be a non-empty sector id or name (and only applies to behavior 'combat')");
        }

        // All name/side validation happens BEFORE anything touches the wire.
        var hull = Catalog.ResolveHull(hullArg!);
        IReadOnlyList<ExpandedPartData> parts;
        if (args.TryGetValue("parts", out var partsJe) && AsStrings(partsJe) is { Count: > 0 } names)
            parts = Catalog.ResolveParts(names);
        else
            // P539 — a COMBAT bot's omitted-parts default is the real dogfight
            // loadout (a sensible anti-ship gun that mounts the hull, not the
            // dispersion-0 test gun); idle/weave keeps the standing parity loadout.
            parts = behavior == "combat"
                ? Catalog.DogfightParts(hull.Id, hull.Name)
                : Catalog.DefaultParts();

        int? side = args.TryGetValue("side", out var sJe) ? TryInt(sJe) : null;

        // P433/P435 — optional real launcher/dispenser: `launcher` names one
        // explicitly ("default" = the default fighter-mounted missile launcher; a
        // name/substring resolves a real one of ANY kind — missile/mine/chaff/probe;
        // "none"/absent = gun-only, the standing default). The resolved launcher is
        // appended to the loadout and gated against the hull's real capability mask
        // by the spawner (gun-only if it does not fit).
        short? launcherId = null;
        string? launcherRequest = null;
        if (OptStr(args, "launcher") is { Length: > 0 } la && !la.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            launcherRequest = la;
            launcherId = la.Equals("default", StringComparison.OrdinalIgnoreCase)
                ? Catalog.DefaultLauncherPart().Id
                : Catalog.ResolveLauncher(la).Id;
        }

        var result = mgr.SpawnBot(hull.Id, hull.Name, side is { } sv ? (short)sv : null, parts, behavior, targetSector, launcherId, launcherRequest);
        return Ok(JsonSerializer.Serialize(new
        {
            ok = true,
            name = result.Name,
            shipId = result.ShipId,
            hull = result.Hull,
            side = result.Side,
            // P473 — the REAL loadout the server granted (from the BUY_LOADOUT_ACK),
            // plus any partial-grant warning. Never a silent success.
            granted = new
            {
                hull = result.Granted.HullId,
                parts = result.Granted.Parts.Select(p => p.PartId).ToArray(),
                partCount = result.Granted.Parts.Count,
            },
            warning = result.Warning,
            launcher = launcherId is null ? null : Catalog.LauncherReadyTime(launcherId.Value) > 0 ? Catalog.ResolveLauncherName(launcherId.Value) : null,
            active = result.ActiveCount,
            note = "Bot is running in the background for the life of this MCP server process. list_active_bots shows it live.",
        }));
    }

    /// <summary>P430 — stage a whole fight in one call: <c>size</c> real combat
    /// bots per side (2×size total), split across the server's first two real
    /// sides, each spawned through the exact same <see cref="SpawnBot"/> path a
    /// caller would use manually. Returns a real per-side summary and names any
    /// genuine per-bot failure individually — a partial failure is reported, not
    /// swallowed. A completely-failed scenario is an error result; otherwise the
    /// (possibly partial) summary comes back as a normal result.</summary>
    private static CallToolResult SpawnScenario(IDictionary<string, JsonElement> args, BotManager mgr)
    {
        if (!args.TryGetValue("size", out var sizeJe) || TryInt(sizeJe) is not { } size || size < 1)
            return Err("spawn_scenario: required integer argument `size` (bots per side) must be ≥ 1");

        // Resolve the hull before anything touches the wire (default = the real
        // fighter spawn_bot / vizfight use), consistent with spawn_bot's stance.
        string? hullArg = OptStr(args, "hull");
        var hull = string.IsNullOrWhiteSpace(hullArg) ? Catalog.DefaultFighterHull() : Catalog.ResolveHull(hullArg!);

        short? targetSector = null;
        if (OptStr(args, "target_sector") is { } ts && ts.Length > 0)
            targetSector = Catalog.ResolveSector(ts, mgr.ProbeClusters()).Id;
        else if (args.ContainsKey("target_sector"))
            return Err("spawn_scenario: `target_sector` must be a non-empty sector id or name");

        // P433 - optional real missile launcher for every bot in the scenario
        // (same resolution semantics as the spawn_bot `launcher` argument).
        var (launcherId, launcherRequest) = ResolveLauncherArg(args);

        // Need two real sides to stage a fight.
        var sides = mgr.ValidSides();
        if (sides.Count < 2)
            return Err($"spawn_scenario: staging a fight needs at least two sides; this server has: {string.Join(", ", sides)}");
        short sideA = sides[0], sideB = sides[1];

        int spawnedA = 0, spawnedB = 0;
        var failures = new List<object>();
        var grants = new List<object>();   // P473 — per-bot granted loadout + warning
        int warnings = 0;
        for (int i = 0; i < size; i++)
        {
            try
            {
                var ra = mgr.SpawnBot(hull.Id, hull.Name, sideA, Catalog.DogfightParts(hull.Id, hull.Name), "combat", targetSector, launcherId, launcherRequest);
                spawnedA++;
                grants.Add(new { side = sideA, name = ra.Name, grantedHull = ra.Granted.HullId, grantedParts = ra.Granted.Parts.Select(p => p.PartId).ToArray(), warning = ra.Warning });
                if (ra.Warning is not null) warnings++;
            }
            catch (Exception ex) { failures.Add(new { index = i, side = sideA, error = ex.Message }); }
            try
            {
                var rb = mgr.SpawnBot(hull.Id, hull.Name, sideB, Catalog.DogfightParts(hull.Id, hull.Name), "combat", targetSector, launcherId, launcherRequest);
                spawnedB++;
                grants.Add(new { side = sideB, name = rb.Name, grantedHull = rb.Granted.HullId, grantedParts = rb.Granted.Parts.Select(p => p.PartId).ToArray(), warning = rb.Warning });
                if (rb.Warning is not null) warnings++;
            }
            catch (Exception ex) { failures.Add(new { index = i, side = sideB, error = ex.Message }); }
        }

        var summary = new
        {
            size,
            hull = hull.Name,
            targetSector,
            launcher = launcherId is null ? null : Catalog.ResolveLauncherName(launcherId.Value),
            sideA = new { side = sideA, spawned = spawnedA },
            sideB = new { side = sideB, spawned = spawnedB },
            // P473 — the REAL granted loadout per bot and the partial-grant
            // warning count; a refusal surfaces as a per-bot failure above.
            grants,
            warnings,
            failures,
            note = "Scenario bots run in the background for the life of this process; get_perf_stats reports their live update cadence.",
        };
        if (spawnedA == 0 && spawnedB == 0)
            return Err(JsonSerializer.Serialize(summary));   // complete failure: an error result
        return Ok(JsonSerializer.Serialize(summary));
    }

    /// <summary>P430 — aggregate the real per-bot update-cadence statistics (an
    /// optional <c>bot</c> name substring restricts the aggregate to one bot).
    /// See <see cref="BotManager.GetPerfStats"/> for what the numbers mean.</summary>
    private static CallToolResult GetPerfStats(IDictionary<string, JsonElement> args, BotManager mgr)
    {
        string? filter = OptStr(args, "bot");
        var ps = mgr.GetPerfStats(filter is { Length: > 0 } ? filter : null);
        // P539 — the real per-bot / per-side combat stats from the bots' own
        // wire/world events, so hit rate and kills are measurable from MCP.
        var cs = mgr.GetCombatSummary();
        return Ok(JsonSerializer.Serialize(new
        {
            botCount = ps.BotCount,
            interval = Stat(ps.Interval),
            jump = Stat(ps.Jump),
            units = "interval = seconds between successive real position updates; jump = meters moved per update; each mean/p95/worst over the last 200 samples per contributing bot.",
            // P539 — per-bot combat stats (real wire/world events).
            combat = new
            {
                shotsFired = cs.ShotsFired,
                shipHits = cs.ShipHits,
                kills = cs.Kills,
                hitRate = cs.OverallHitRate,
                podsCreated = cs.PodsCreated,
                podsDocked = cs.PodsDocked,
                podsLingering = cs.PodsLingering,
                perBot = cs.Bots.Select(b => new
                {
                    name = b.Name, side = b.Side,
                    shotsFired = b.ShotsFired, shipHits = b.ShipHits, kills = b.Kills,
                    hitRate = b.HitRate, isPod = b.IsPod, podDocked = b.PodDocked,
                }),
            },
        }));
    }

    private static object Stat(MetricStat s) => new
    {
        mean = s.Mean,
        p95 = s.P95,
        worstBot = s.WorstBot,
        worstValue = s.WorstValue,
    };

    /// <summary>
    /// P432 — the hull/weapon test matrix: plan the cartesian product of real
    /// (hull × weapon × size) combinations up front (pure), then execute each
    /// runnable combination against the live server for a real observation
    /// window, capturing a real per-combination row. Executes combinations
    /// sequentially and clears each one's own bots before the next, so
    /// measurements never bleed. Returns one row per combination; a real,
    /// specific per-combination compatibility error row for a weapon that does
    /// not mount on a hull (never a silent skip, never a whole-matrix crash).
    /// A matrix in which no combination ran at all is an error result; a partial
    /// matrix is a normal result with the per-row outcomes.
    /// </summary>
    private static CallToolResult RunHullMatrix(IDictionary<string, JsonElement> args, BotManager mgr)
    {
        if (!args.TryGetValue("hulls", out var hullsJe) || AsStrings(hullsJe) is not { Count: > 0 } hulls)
            return Err("run_hull_matrix: required argument `hulls` (array of real hull names/substrings) is missing or empty");

        // Weapons default to the real default weapon; sizes to the P430 presets.
        List<string>? weapons = args.TryGetValue("weapons", out var wJe) ? AsStrings(wJe) : null;
        if (weapons is { Count: 0 }) weapons = null;   // empty array == default
        List<int> sizes = args.TryGetValue("sizes", out var sJe) && AsInts(sJe) is { Count: > 0 } sArr
            ? sArr : new List<int> { 5, 10, 15 };

        short? targetSector = null;
        if (OptStr(args, "target_sector") is { } ts && ts.Length > 0)
            targetSector = Catalog.ResolveSector(ts, mgr.ProbeClusters()).Id;

        double observeSeconds = 45;
        if (args.TryGetValue("observe_seconds", out var oJe) && oJe.ValueKind == JsonValueKind.Number
            && oJe.TryGetDouble(out var od) && od > 0)
            observeSeconds = od;

        // P433 - optional real missile launcher (same resolution semantics).
        var (launcherId, launcherRequest) = ResolveLauncherArg(args);

        // Plan first (pure; any whole-matrix resolution error aborts before wire).
        List<MatrixPlanner.MatrixCombo> plan;
        try { plan = MatrixPlanner.Plan(hulls, weapons ?? [], sizes); }
        catch (ArgumentException ex) { return Err($"run_hull_matrix: {ex.Message}"); }

        var rows = new List<object>();
        int okCount = 0, errCount = 0;
        foreach (var combo in plan)
        {
            if (combo.Error is not null)
            {
                errCount++;
                rows.Add(new { hull = combo.HullName, weapon = combo.WeaponName, size = combo.Size, status = "error", error = combo.Error });
                continue;
            }
            try
            {
                var r = mgr.RunCombination(combo.HullId, combo.HullName, combo.WeaponPartId, combo.WeaponName,
                    combo.Size, targetSector, observeSeconds, launcherId, launcherRequest);
                okCount++;
                rows.Add(new
                {
                    hull = r.HullName, weapon = r.WeaponName, size = r.Size, status = "ok",
                    sideA = new { spawned = r.SpawnedA, killed = r.KilledA, survived = r.SurvivedA },
                    sideB = new { spawned = r.SpawnedB, killed = r.KilledB, survived = r.SurvivedB },
                    timeToFirstKill = r.TimeToFirstKill,
                    // P473 — the REAL hull each spawned bot was granted (all equal
                    // when the harness grant let every bot buy the combo's hull)
                    // and how many bots got a partial-grant warning.
                    grantedHulls = r.GrantedHullIds,
                    partialGrants = r.PartialGrants,
                    cadence = new
                    {
                        botCount = r.Perf.BotCount,
                        interval = Stat(r.Perf.Interval),
                        jump = Stat(r.Perf.Jump),
                    },
                });
            }
            catch (Exception ex)
            {
                errCount++;
                rows.Add(new { hull = combo.HullName, weapon = combo.WeaponName, size = combo.Size, status = "error", error = ex.Message });
            }
        }

        var summary = new
        {
            combos = rows,
            total = rows.Count,
            ok = okCount,
            errors = errCount,
            observeSeconds,
            targetSector,
            note = "One row per (hull, weapon, size) combination. kills = real KILL_SHIP events whose victim was that combination's own ship; on this server a player-owned ship EJECTS to a lifepod before a true death, so a real kill follows the pod being destroyed (the P107 mechanic). Each combination's bots were cleared (each ended its own real connection) before the next ran.",
        };
        if (okCount == 0)
            return Err(JsonSerializer.Serialize(summary));   // nothing actually ran: an error result
        return Ok(JsonSerializer.Serialize(summary));
    }

    /// <summary>
    /// P474/P490 — run ONE live-verification parity scenario (bomber_run /
    /// capture_attempt / elimination_run) against the live server and return its
    /// structured observed-outcome report as JSON. Delegates the whole choreography +
    /// observation to <see cref="BotManager.RunScenario"/>; this layer only
    /// parses the tool args and resolves the optional sector against the probe's
    /// live map (before anything touches the wire, the same cheap-fail stance as
    /// the other tools). A genuine preflight failure (bad preset, no target
    /// station, unresolvable plan) is a specific error result.
    /// </summary>
    private static CallToolResult RunScenario(IDictionary<string, JsonElement> args, BotManager mgr)
    {
        string preset = OptStr(args, "preset") is { Length: > 0 } p ? p : "";
        if (!BotManager.IsSupportedScenarioPreset(preset))
            return Err($"run_scenario: required argument `preset` must be 'bomber_run', 'capture_attempt' or 'elimination_run', got '{preset}'");

        int size = 1;
        if (args.TryGetValue("size", out var sizeJe) && TryInt(sizeJe) is { } sz && sz >= 1)
            size = sz;

        short? targetSector = null;
        if (OptStr(args, "target_sector") is { } ts && ts.Length > 0)
            targetSector = Catalog.ResolveSector(ts, mgr.ProbeClusters()).Id;

        double timeoutSeconds = 90;
        if (args.TryGetValue("timeout_seconds", out var toJe) && toJe.ValueKind == JsonValueKind.Number
            && toJe.TryGetDouble(out var tod) && tod > 0)
            timeoutSeconds = tod;

        string report = mgr.RunScenario(preset, size, targetSector, timeoutSeconds);
        return Ok(report);
    }

    /// <summary>P486 — <c>invest</c>: parse the side/bucket/amount/observe args
    /// and delegate to the commander tool in <see cref="BotManager.Invest"/>.
    /// Resolution errors (unknown/ambiguous bucket, unknown side, wallet short)
    /// throw and surface as specific error results.</summary>
    private static CallToolResult Invest(IDictionary<string, JsonElement> args, BotManager mgr)
    {
        if (!args.TryGetValue("side", out var sideJe) || TryInt(sideJe) is not { } s)
            return Err("invest: required integer argument `side` (side id to invest on) is missing");
        string bucketArg = OptStr(args, "bucket") ?? "";
        if (string.IsNullOrWhiteSpace(bucketArg))
            return Err("invest: required argument `bucket` (drone-type / station-type name or id) is missing");
        int? amount = args.TryGetValue("amount", out var amtJe) ? TryInt(amtJe) : null;
        double observe = 20;
        if (args.TryGetValue("observe_seconds", out var oJe) && oJe.ValueKind == JsonValueKind.Number
            && oJe.TryGetDouble(out var od) && od > 0)
            observe = od;
        var r = mgr.Invest((short)s, bucketArg, amount, observe);
        return Ok(JsonSerializer.Serialize(r));
    }

    /// <summary>P486 — <c>list_drones</c>: parse side (and optional type filter)
    /// and delegate to <see cref="BotManager.ListDrones"/>.</summary>
    private static CallToolResult ListDrones(IDictionary<string, JsonElement> args, BotManager mgr)
    {
        if (!args.TryGetValue("side", out var sideJe) || TryInt(sideJe) is not { } s)
            return Err("list_drones: required integer argument `side` (side id to list) is missing");
        string? type = OptStr(args, "type");
        var r = mgr.ListDrones((short)s, string.IsNullOrWhiteSpace(type) ? null : type);
        return Ok(JsonSerializer.Serialize(r));
    }

    /// <summary>P486 — <c>order_drone</c>: parse droneId/command/target and
    /// delegate to <see cref="BotManager.OrderDrone"/>. Unknown command / bad
    /// target / unobservable drone surface as specific errors.</summary>
    private static CallToolResult OrderDrone(IDictionary<string, JsonElement> args, BotManager mgr)
    {
        if (!args.TryGetValue("droneId", out var dj) || TryInt(dj) is not { } droneId)
            return Err("order_drone: required integer argument `droneId` is missing");
        string? command = OptStr(args, "command");
        if (string.IsNullOrWhiteSpace(command))
            return Err("order_drone: required argument `command` is missing");
        string? target = OptStr(args, "target");
        var r = mgr.OrderDrone((short)droneId, command, string.IsNullOrWhiteSpace(target) ? null : target);
        return Ok(JsonSerializer.Serialize(r));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static (short? Id, string? Request) ResolveLauncherArg(IDictionary<string, JsonElement> args)
    {
        if (OptStr(args, "launcher") is { Length: > 0 } la && !la.Equals("none", StringComparison.OrdinalIgnoreCase))
            return la.Equals("default", StringComparison.OrdinalIgnoreCase)
                ? (Catalog.DefaultLauncherPart().Id, la)
                : (Catalog.ResolveLauncher(la).Id, la);
        return (null, null);
    }

    private static List<int>? AsInts(JsonElement je)
    {
        if (je.ValueKind != JsonValueKind.Array) return null;
        var list = new List<int>();
        foreach (var e in je.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i)) list.Add(i);
        return list.Count > 0 ? list : null;
    }

    private static CallToolResult Ok(string text) => Text(text, isError: false);
    private static CallToolResult Err(string text) => Text(text, isError: true);
    private static CallToolResult Text(string text, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError,
    };

    private static string? OptStr(IDictionary<string, JsonElement> a, string key) =>
        a.TryGetValue(key, out var je) ? Str(je) : null;

    private static string Str(JsonElement je) =>
        je.ValueKind == JsonValueKind.String ? je.GetString() ?? "" : "";

    private static int? TryInt(JsonElement je) =>
        je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var i) ? i : null;

    private static List<string>? AsStrings(JsonElement je)
    {
        if (je.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var e in je.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString()!);
        return list;
    }
}
