using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
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
    public static IReadOnlyList<Tool> Definitions(bool supervisor = false)
    {
        Tool[] definitions =
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
                        parts = new { type = "array", items = new { type = "string" }, description = "Optional part names/substrings for the loadout (see list_parts). Omit for the hull's real native preferred loadout at `tech_level`." },
                        tech_level = new { type = "integer", @default = 0, minimum = 0, description = "Optional tech level for a combat bot's omitted-parts loadout (P539b/P556): 0 = the scenario hull's native preferred loadout; N advances the scenario hull floor(N/2) steps along its SuccessorHullID chain (tech 2+ flies the successor hull) and advances each of the resulting hull's native preferred parts N steps along its SuccessorPartID chain (keeping only parts that CanMountPart, including the shield chain), stopping at chain ends. Higher tech flies a tougher hull with a stronger gun and shield — for deterministic showcase fights." },
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
                Description = "Read-only: every bot this MCP server process has spawned and is still connected to, with its real live ship id, hull, side, sector, position, (for combat bots) current target ship id, and — when a bot is under a real accepted teammate order — the specific `orderedTarget` ship id it is obeying (distinct from its spawn-time behavior and its current live target). Includes removal history. Supervisor mode also offers stop_bots, clear_bots and despawn_bot.",
                InputSchema = Schema(new { type = "object" }),
            },
            new Tool { Name = "stop_bots", Description = "Disconnect all spawned bots; keep the MCP server and probes running.", InputSchema = Schema(new { type = "object" }) },
            new Tool { Name = "clear_bots", Description = "Disconnect all spawned bots and clear their removal history.", InputSchema = Schema(new { type = "object" }) },
            new Tool { Name = "despawn_bot", Description = "Disconnect the named bot (exact name).", InputSchema = Schema(new { type = "object", properties = new { name = new { type = "string" } }, required = new[] { "name" } }) },
            new Tool
            {
                Name = "spawn_scenario",
                Description = "Spawn a whole fight in one call. Three compositions: (1) homogeneous — `size` real 'combat' bots PER SIDE of one `hull` (2×size total, the P430 path, optional per-side `tech_a`/`tech_b`); (2) MIXED-FLEET (P557) — `fleet_a`/`fleet_b` (or one `fleet` for both sides) as arrays of {hull, count, tech?}, each entry spawning `count` bots of that hull with its native preferred loadout advanced by `tech` (P555/P556), staging/arena working over the whole mixed fleet; (3) SHOWCASE PRESET (P557) — `preset:\"showcase\"` composes a readable per-side mix (fighters main line + interceptors + scouts + bombers/heavies where available, ~15 per side × `size_scale`) from the real core's player-buyable hulls, gives mine dispensers to a slice of the fleet and enables the native wingman UseMines/UseMissiles/Strafe behaviour. Optional `minefields:{side,count,at}` invests the side's native LAYER-drone bucket and orders the drone to build a minefield (default: in front of the enemy's arrival aleph in the rendezvous sector). Optional `wingman:{strafe,use_missiles,use_mines}` sets the scenario default behaviour mask. Returns a per-side summary with the resolved per-hull loadouts, who carries mines/missiles, and minefield placements; any real per-bot failure is named individually. Showcase defaults to a mature-battle bomber finale after at least 30 minutes; finale_after_minutes:0 disables it. Read the live finale section in get_perf_stats.",
                InputSchema = Schema(new
                {
                    type = "object",
                    properties = new
                    {
                        refill = new { type = "boolean", @default = true, description = "Retry each failed or disconnected slot once at the end of spawning. Summary counts only bots still connected at completion; failures lists every failed attempt." },
                        size = new { type = "integer", description = "Homogeneous mode: bots PER SIDE to spawn (total = 2×size). Used when no `fleet`/`fleet_a`/`fleet_b`/`preset` is given. 5 / 10 / 15 are the intended presets." },
                        hull = new { type = "string", description = "Homogeneous mode: hull name/substring used for every bot. Defaults to the same real fighter spawn_bot / vizfight use." },
                        fleet = new { type = "array", items = new { type = "object" }, description = "P557 mixed fleet used for BOTH sides: array of { hull (name/substring), count (int ≥1), tech (optional int ≥0) }. Each entry spawns `count` bots of that hull with its native preferred loadout advanced by `tech`." },
                        fleet_a = new { type = "array", items = new { type = "object" }, description = "P557 mixed fleet for SIDE A specifically: array of { hull, count, tech? }. Overrides `fleet` for side A." },
                        fleet_b = new { type = "array", items = new { type = "object" }, description = "P557 mixed fleet for SIDE B specifically: array of { hull, count, tech? }. Overrides `fleet` for side B." },
                        preset = new { type = "string", @enum = new[] { "showcase" }, description = "P557: 'showcase' composes a readable per-side mixed fleet from the real core's player-buyable hulls (~15 per side × size_scale), gives mine dispensers to a slice of the fleet and enables native wingman UseMines/UseMissiles/Strafe." },
                        size_scale = new { type = "integer", @default = 1, minimum = 1, description = "P557: scales the showcase fleet size (15 × size_scale ships per side)." },
                        target_sector = new { type = "string", description = "Optional real sector NAME or numeric id to fly real warps to before engaging — passed identically to every bot on both sides, so they rendezvous and fight in one place." },
                        tech_a = new { type = "integer", @default = 0, minimum = 0, description = "Homogeneous mode: per-side tech level for side A (P539b/P556). Ignored when a mixed fleet is given (each entry carries its own `tech`)." },
                        tech_b = new { type = "integer", @default = 0, minimum = 0, description = "Homogeneous mode: per-side tech level for side B. Ignored when a mixed fleet is given." },
                        mode = new { type = "string", @enum = new[] { "march", "arena" }, @default = "march", description = "Battle start mode (Domingo, 2026-10-13). `march` (DEFAULT, the 200v200 baseline): each side launches at home, flies its wings through the alephs (P635 routes), stages, and converges on `target_sector` — wings advance independently. `arena`: both fleets spawn directly in `target_sector` facing each other for an instant clash (server stress test). The legacy `arena: true` flag is an alias for `mode: \"arena\"`." },
                        arena = new { type = "boolean", @default = false, description = "P539d — arena mode: launch both sides DIRECTLY into `target_sector` at mirrored spots so travel can't decide the outcome. Requires `target_sector`." },
                        staging = new { type = "boolean", @default = true, description = "P539d — travel-with-staging mode (DEFAULT when `target_sector` is set): each side gathers until its whole living complement is assembled before advancing." },
                        finale_after_minutes = new { type = "number", minimum = 0, description = "Finale minimum battle age in minutes (showcase default 30; other scenarios default 0; 0 disables). Also requires both sides' pod relaunch share and sustained kills." },
                        finale_side = new { description = "leader (default, ahead on kills; ties side 0), integer 0 or 1, or both", anyOf = new object[] { new { type = "string", @enum = new[] { "leader", "both" } }, new { type = "integer", minimum = 0, maximum = 1 } } },
                        finale_min_relaunch_share = new { type = "number", @default = .5, minimum = 0, maximum = 1 },
                        finale_min_kills_last_5_minutes = new { type = "integer", @default = 5, minimum = 1 },
                        finale_bombers = new { type = "integer", @default = 12, minimum = 1, maximum = 200 },
                        finale_escort_share = new { type = "number", @default = .1, minimum = 0, maximum = 1 },
                        finale_escort_cap = new { type = "integer", @default = 12, minimum = 0, maximum = 200 },
                        finale_escort_radius = new { type = "number", @default = 1500, exclusiveMinimum = 0 },
                        finale_escort_leash = new { type = "number", @default = 2500, exclusiveMinimum = 0 },
                        launcher = new { type = "string", description = "Optional real launcher/dispenser for the loadout (P433/P435): a name/substring resolves a real one of ANY expendable kind. Homogeneous-mode convenience; mixed-fleet/showcase equip their own mines/missiles natively." },
                        minefields = new { type = "object", description = "P557 — lay minefields via native LAYER drones before the fleets meet: { side: 'a'|'b'|'both', count: int (fields per side, default 1), at: 'rendezvous' (default; in front of the enemy's arrival aleph) | 'aleph:<sector>' | { sector, x, y, z } }. Invests the side's layer-drone bucket (the 221 testUnlock money covers it), waits for the drone, and orders it to build at the location." },
                        wingman = new { type = "object", description = "P557 — scenario-default native wingman behaviour mask: { strafe?: bool, use_missiles?: bool, use_mines?: bool }. Default = UseMissiles|UseMines (native WingmanBehaviourBitMask, igc.h:785-794)." },
                    },
                    required = Array.Empty<string>(),
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
        return supervisor ? definitions : definitions.Where(t => t.Name is not ("stop_bots" or "clear_bots" or "despawn_bot")).ToArray();
    }

    private static JsonElement Schema(object schema) =>
        JsonSerializer.SerializeToElement(schema);   // default naming: emit exact JSON-Schema keys (enum/default)

    // ── Dispatch ───────────────────────────────────────────────────────────

    public static CallToolResult Handle(string name, IDictionary<string, JsonElement>? args, BotManager mgr)
    {
        args ??= new Dictionary<string, JsonElement>();
        try
        {
            var result = name switch
            {
                "stop_bots" => Ok(JsonSerializer.Serialize(new { stopped = mgr.StopBots(null, false) })),
                "clear_bots" => Ok(JsonSerializer.Serialize(new { cleared = mgr.StopBots(null, true) })),
                "despawn_bot" => OptStr(args, "name") is { Length: > 0 } botName
                    ? Ok(JsonSerializer.Serialize(new { stopped = mgr.StopBots(botName, false) })) : Err("despawn_bot: required name is missing"),
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
                    .Select(b => new {
                        name = b.Name, shipId = b.ShipId, side = b.Side, hull = b.Hull,
                        role = b.Role, sector = b.Sector, behavior = b.Behavior,
                        state = b.State, defenseSector = b.DefenseSector,
                        target = b.Target, orderedTarget = b.OrderedTarget, engagers = b.Engagers,
                        pos = b.Position,
                        // P637 — role → target → route → staging + live motion.
                        targetSector = b.TargetSector, routeIndex = b.RouteIndex, route = b.RouteName,
                        stagingSector = b.StagingSector, stagePhase = b.StagePhase,
                        velocity = b.Velocity, speed = b.Speed, idle = b.Idle,
                        status = "active",
                    }).Cast<object>().Concat(mgr.RemovedBots().Select(b => (object)new {
                        name = b.Name, shipId = b.ShipId, status = "removed", reason = b.Reason,
                    })))),
                _ => Err($"unknown tool '{name}'"),
            };
            return name == "spawn_bot" && result.IsError == true ? SpawnBotError(args, mgr, result) : result;
        }
        catch (ArgumentException ex)
        {
            return name == "spawn_bot" ? SpawnBotError(args, mgr, Err(ex.Message)) : Err(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return name == "spawn_bot" ? SpawnBotError(args, mgr, Err(ex.Message)) : Err(ex.Message);
        }
        catch (Exception ex)
        {
            var error = Err($"spawn failed unexpectedly: {ex.Message}");
            return name == "spawn_bot" ? SpawnBotError(args, mgr, error) : error;
        }
    }

    internal static CallToolResult SpawnBotError(IDictionary<string, JsonElement> args, BotManager mgr, CallToolResult error)
    {
        int? side = OptInt(args, "side") ?? (mgr.ProbeSide >= 0 ? mgr.ProbeSide : null);
        string reason = string.Join(" ", error.Content.OfType<TextContentBlock>().Select(c => c.Text));
        SpawnLaunch.LogFailure(side, OptStr(args, "hull") ?? "(unspecified)", reason);
        return Err(JsonSerializer.Serialize(new
        {
            ok = false, requested = 1, spawned = 0, failed = 1,
            sides = new[] { new { side, requested = 1, spawned = 0, failed = 1 } },
            failures = new[] { new { side, error = reason } },
        }));
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
        // P539b — optional per-bot tech LEVEL (0 = the hull's native preferred
        // loadout; N = each preferred part advanced N steps along its native
        // successor chain, stopping at the last successor that still mounts).
        int techLevel = 0;
        if (args.TryGetValue("tech_level", out var tlJe) && TryInt(tlJe) is { } tl && tl >= 0)
            techLevel = tl;
        // P556 — a combat bot with no explicit `parts` flies the RESOLVED tech
        // loadout: the effective hull (the scenario hull advanced floor(N/2)
        // steps along SuccessorHullId) with its native preferred parts advanced
        // N steps. The effective hull is what the spawn must REQUEST, so the
        // server mounts the successor hull, not the base.
        Catalog.TechLoadout? techLoadout = null;
        IReadOnlyList<ExpandedPartData> parts;
        if (args.TryGetValue("parts", out var partsJe) && AsStrings(partsJe) is { Count: > 0 } names)
            parts = Catalog.ResolveParts(names);
        else
            // P539b/P556 — a COMBAT bot's omitted-parts default is the hull's
            // NATIVE PREFERRED loadout at the requested tech level (level 0 ==
            // the scenario hull's own PreferredPartsTypes — now including its
            // real `Sm Shield 1` after P555 — gated by CanMount exactly like
            // native's UpgradeShip), NOT P539's "highest-power/lowest-PartId"
            // picks — the native preferred shield is what a real player can
            // kill. A caller who wants P539's dogfight gun names it explicitly
            // via `parts`. idle/weave keeps the standing parity loadout.
            parts = behavior == "combat"
                ? (techLoadout = Catalog.NativePreferredLoadout(hull.Id, hull.Name, techLevel)).Value.Parts
                : Catalog.DefaultParts();
        var flyHullId = techLoadout?.HullId ?? hull.Id;
        var flyHullName = techLoadout?.HullName ?? hull.Name;

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

        var result = mgr.SpawnBot(flyHullId, flyHullName, side is { } sv ? (short)sv : null, parts, behavior, targetSector, launcherId, launcherRequest, reportFailure: false);
        if (!mgr.IsSpawnActive(result))
            throw new InvalidOperationException($"spawn_bot: bot '{result.Name}' disconnected or was disposed before reporting");
        return Ok(JsonSerializer.Serialize(new
        {
            ok = true,
            requested = 1, spawned = 1, failed = 0,
            sides = new[] { new { side = result.Side, requested = 1, spawned = 1, failed = 0 } },
            failures = Array.Empty<object>(),
            name = result.Name,
            shipId = result.ShipId,
            hull = result.Hull,
            side = result.Side,
            // P539b — the RESOLVED tech-level loadout by PART NAME (what the
            // operator actually asked this side to fly) and the level requested.
            techLevel,
            // P556 — the effective hull (successor hull the bot actually flies)
            // plus its HP, mounted shield (name + strength/regen) and gun.
            techProfile = techLoadout is { } tlx ? new
            {
                level = techLevel,
                hull = new { id = tlx.HullId, name = tlx.HullName, hitPoints = tlx.HitPoints },
                shield = Catalog.ShieldStats(tlx.Parts) is { } sh ? new { partId = sh.PartId, name = sh.Name, strength = sh.Strength, regen = sh.Regen } : null,
                gun = Catalog.FirstWeaponPartId(tlx.Parts) is { } gw ? Catalog.PartName(gw) : null,
            } : null,
            loadout = parts.Select(p => Catalog.PartName(p.PartId) ?? p.PartId.ToString()).ToArray(),
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
    /// <summary>P557 — dispatch <c>spawn_scenario</c>: the homogeneous P430
    /// path (size+hull) when no fleet/showcase/minefield args are present,
    /// otherwise the P557 mixed-fleet/showcase/minefield path.</summary>
    private static CallToolResult SpawnScenario(IDictionary<string, JsonElement> args, BotManager mgr)
    {
        bool mixed = args.ContainsKey("fleet") || args.ContainsKey("fleet_a") || args.ContainsKey("fleet_b")
                     || (OptStr(args, "preset") is { Length: > 0 })
                     || args.ContainsKey("minefields") || args.ContainsKey("wingman");
        return mixed
            ? SpawnScenarioModern(args, mgr)
            : SpawnScenarioHomogeneous(args, mgr);
    }

    private static CallToolResult SpawnScenarioHomogeneous(IDictionary<string, JsonElement> args, BotManager mgr)
    {
        var finaleSettings = ReadFinaleSettings(args, false);
        var finaleNames = new List<string>();
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

        // P539b — per-side tech LEVEL: 0 = the hull's native preferred loadout;
        // N = each preferred part advanced N steps along its native successor
        // chain (stopping at the last successor that still mounts). Higher tech
        // flies stronger parts, so a showcase operator can make one side
        // predictably dominate.
        int techA = OptInt(args, "tech_a") ?? 0;
        int techB = OptInt(args, "tech_b") ?? 0;

        // Need two real sides to stage a fight.
        var sides = mgr.ValidSides();
        if (sides.Count < 2)
            return Err($"spawn_scenario: staging a fight needs at least two sides; this server has: {string.Join(", ", sides)}");
        short sideA = sides[0], sideB = sides[1];

        var spawnSlots = new List<SpawnBatch.Slot>();
        bool refill = OptBool(args, "refill", def: true);
        // P556 — each side's resolved tech loadout (effective hull + parts). The
        // effective hull id is what each side's spawn REQUESTS so the server
        // mounts the successor hull, not the base.
        var tlA = Catalog.NativePreferredLoadout(hull.Id, hull.Name, techA);
        var tlB = Catalog.NativePreferredLoadout(hull.Id, hull.Name, techB);
        var partsA = tlA.Parts;
        var partsB = tlB.Parts;
        var flyHullIdA = tlA.HullId;   var flyHullNameA = tlA.HullName;
        var flyHullIdB = tlB.HullId;   var flyHullNameB = tlB.HullName;

        // P556 — the per-side report fragment: hull id/name/HP, mounted shield
        // (name + strength/regen), lead gun, and the full resolved loadout.
        object SideReport(Catalog.TechLoadout tl, int level) => new
        {
            level,
            hull = new { id = tl.HullId, name = tl.HullName, hitPoints = tl.HitPoints },
            shield = Catalog.ShieldStats(tl.Parts) is { } sh ? new { partId = sh.PartId, name = sh.Name, strength = sh.Strength, regen = sh.Regen } : null,
            gun = Catalog.FirstWeaponPartId(tl.Parts) is { } gw ? Catalog.PartName(gw) : null,
            loadout = tl.Parts.Select(p => Catalog.PartName(p.PartId) ?? p.PartId.ToString()).ToArray(),
        };

        // P539d — fair contact: `arena` launches both sides DIRECTLY into the
        // target sector at mirrored spots (equal distance from the centre, facing
        // each other) so travel can't decide the outcome. `staging` (default for a
        // travel scenario with a target sector) makes each side gather at a staging
        // point until its whole complement is assembled before advancing together.
        bool arena = IsArenaMode(args);   // `mode: "arena"|"march"` (2026-10-13); legacy `arena: true` still honoured
        bool staging = arena ? false : OptBool(args, "staging", def: targetSector is not null);
        var arenaSpots = new List<(Vector3 Pos, Vector3 Fwd)>();
        string arenaNote = "";
        if (arena)
        {
            if (targetSector is not { } tsA) return Err("spawn_scenario: `arena` requires `target_sector` (the rendezvous to place both sides in)");
            if (mgr.ProbeSectorCenter(tsA) is not { } cen)
                return Err($"spawn_scenario: cannot resolve the centre of sector {tsA} for arena mirroring (need warp/station anchors)");
            // Mirror axis: the line through the two sides' homes is not needed for a
            // fair arena — any axis works since positions are mirrored about the centre.
            var (pa, pb, fa, fb) = BotManager.ComputeArenaPlacements(size, cen, new Vector3(0f, 0f, 1f), BotManager.ArenaOffset, 220f);
            arenaSpots = pa.Concat(pb).Select((p, i) => (p, i < size ? fa : fb)).ToList();
            arenaNote = $"arena: {size} bots/side mirrored about sector {tsA} centre (offset {BotManager.ArenaOffset}u) — P554 relocation via the 'relocate' admin cheat on the bot's own connection (needs liveSettings.adminCheats enabled + the bot's name allow-listed); a refused relocation is a hard per-bot error, never a silent staging fallback.";
        }

        // P635 — multi-route wings (same rule as the mixed-fleet path): split each
        // side across all its distinct entry alephs into the rendezvous. `routes`
        // = "auto" (default) | "single"; `maxPerRoute` caps bots per route with the
        // excess to the least-loaded route. Only for a travelling, staging scenario.
        string routesArg = (OptStr(args, "routes") ?? "auto").ToLowerInvariant();
        int maxPerRoute = OptInt(args, "maxPerRoute") ?? 100;
        bool wantMultiRoute = routesArg == "auto" && staging && !arena;
        bool multiRoute = wantMultiRoute && targetSector is not null;
        short rz2 = wantMultiRoute && targetSector is { } rzv ? rzv : (short)0;
        var routePlans = new Dictionary<short, List<List<short>>>();
        var routeCounts = new Dictionary<short, int[]>();
        string routesNote = "single (routes not split)";
        if (multiRoute)
        {
            foreach (var side in new[] { sideA, sideB })
            {
                var routes = mgr.FindRoutes(rz2, mgr.FindSideHomeSector(side), 3);
                if (routes.Count >= 2)
                {
                    routePlans[side] = routes;
                    routeCounts[side] = new int[routes.Count];
                }
            }
            if (routePlans.Count > 0)
                routesNote = string.Join("; ", routePlans.Select(kv =>
                    $"{kv.Key}: {string.Join(" | ", kv.Value.Select(r => string.Join("→", r)))}"));
        }

        for (int i = 0; i < size; i++)
        {
            var spotA = arena ? arenaSpots[i] : default;
            var spotB = arena ? arenaSpots[size + i] : default;
            // P635 — least-loaded wing per side.
            IReadOnlyList<short>? raPath = null; int raIdx = -1;
            IReadOnlyList<short>? rbPath = null; int rbIdx = -1;
            if (routePlans.TryGetValue(sideA, out var rsA))
            { raIdx = BotManager.PickLeastLoadedRoute(routeCounts[sideA]); routeCounts[sideA][raIdx]++; raPath = rsA[raIdx]; }
            if (routePlans.TryGetValue(sideB, out var rsB))
            { rbIdx = BotManager.PickLeastLoadedRoute(routeCounts[sideB]); routeCounts[sideB][rbIdx]++; rbPath = rsB[rbIdx]; }
            spawnSlots.Add(new(i, sideA, flyHullNameA, () => mgr.SpawnBot(
                flyHullIdA, flyHullNameA, sideA, partsA, "combat", targetSector, launcherId, launcherRequest,
                useStaging: staging, arena: arena, arenaPosition: arena ? spotA.Pos : null, arenaFacing: arena ? spotA.Fwd : null,
                routePath: raPath, routeIndex: raIdx, reportFailure: false)));
            spawnSlots.Add(new(i, sideB, flyHullNameB, () => mgr.SpawnBot(
                flyHullIdB, flyHullNameB, sideB, partsB, "combat", targetSector, launcherId, launcherRequest,
                useStaging: staging, arena: arena, arenaPosition: arena ? spotB.Pos : null, arenaFacing: arena ? spotB.Fwd : null,
                routePath: rbPath, routeIndex: rbIdx, reportFailure: false)));
        }
        var batch = new SpawnBatch(mgr.Partition?.Select(spawnSlots) ?? spawnSlots);
        if (mgr.PlanOnly) return Ok(JsonSerializer.Serialize(new { sideA = batch.Counts(sideA), sideB = batch.Counts(sideB), totals = new[] { new { side = sideA, requested = size }, new { side = sideB, requested = size } } }));
        batch.Run(refill, mgr.IsSpawnActive);
        var grants = GrantReports(batch, mgr.Partition is not null);
        int warnings = batch.Results.Count(r => r.Warning is not null);

        finaleNames.AddRange(batch.Results.Select(r => r.Name));   // P656 finale roster from the P657 spawn batch
        var finale = mgr.ConfigureFinale(finaleSettings, finaleNames);
        var summary = new
        {
            finale,
            size,
            hull = hull.Name,
            targetSector,
            launcher = launcherId is null ? null : Catalog.ResolveLauncherName(launcherId.Value),
            // P539d — the fair-contact mode this scenario ran in.
            mode = arena ? "arena" : staging ? "march" : "travel",   // "march" = launch home → wings through the alephs → stage → converge
            staging,
            arena,
            arenaNote,
            routes = routesNote,
            maxPerRoute,
            // P539b/P556 — the resolved per-side tech loadout by PART NAME plus
            // the effective hull/HP/shield/gun each side actually flies, so a
            // showcase operator knows exactly who should win before the fight.
            tech = new
            {
                sideA = SideReport(tlA, techA),
                sideB = SideReport(tlB, techB),
            },
            refill,
            sideA = batch.Counts(sideA),
            sideB = batch.Counts(sideB),
            // P473 — the REAL granted loadout per bot and the partial-grant
            // warning count; a refusal surfaces as a per-bot failure above.
            grants,
            warnings,
            failures = batch.Failures,
            note = "Scenario bots run in the background for the life of this process; get_perf_stats reports their live update cadence.",
        };
        if (batch.Results.Count == 0 && (batch.Counts(sideA).requested + batch.Counts(sideB).requested > 0 || mgr.Partition is null))
            return Err(JsonSerializer.Serialize(summary));   // complete failure: an error result
        return Ok(JsonSerializer.Serialize(summary));
    }

    /// <summary>P557 — the mixed-fleet / showcase / minefield spawn path. See
    /// the tool description; the whole fair-contact (staging/arena) and
    /// wingman/mine/missile machinery operates over the resolved mixed fleet.</summary>
    private static CallToolResult SpawnScenarioModern(IDictionary<string, JsonElement> args, BotManager mgr)
    {
        var core = Catalog.RepresentativeCore();
        string? preset = OptStr(args, "preset");
        var finaleSettings = ReadFinaleSettings(args, preset == "showcase");
        var finaleNames = new List<string>();

        // Scenario-default native wingman behaviour mask (igc.h:785-794).
        short wingman = Showcase.DefaultScenarioWingman;
        if (args.TryGetValue("wingman", out var wj) && wj.ValueKind == JsonValueKind.Object)
        {
            bool any = false;
            short m = 0;
            if (BoolProp(wj, "strafe") is { } sf) { any = true; if (sf) m |= Showcase.WbbmStrafe; }
            if (BoolProp(wj, "use_missiles") is { } um) { any = true; if (um) m |= Showcase.WbbmUseMissiles; }
            if (BoolProp(wj, "use_mines") is { } umi) { any = true; if (umi) m |= Showcase.WbbmUseMines; }
            if (any) wingman = m;
        }

        // Fleet composition per side.
        bool showcase = preset == "showcase";
        List<Showcase.FleetEntry> fleetA, fleetB;
        if (showcase)
        {
            int sizeScale = OptInt(args, "size_scale") ?? 1;
            fleetA = Showcase.ComposeShowcaseFleet(core, sizeScale).ToList();
            fleetB = Showcase.ComposeShowcaseFleet(core, sizeScale).ToList();
        }
        else
        {
            var fa = ResolveFleetArg(args, "fleet_a", "a", core);
            var fb = ResolveFleetArg(args, "fleet_b", "b", core);
            var both = ResolveFleetArg(args, "fleet", "shared", core);
            if (fa is null && fb is null && both is null)
            {
                fleetA = HomogeneousFleet(args, core, "a");
                fleetB = HomogeneousFleet(args, core, "b");
            }
            else
            {
                fleetA = fa ?? both ?? HomogeneousFleet(args, core, "a");
                fleetB = fb ?? both ?? fa ?? HomogeneousFleet(args, core, "b");
            }
        }

        short? targetSector = null;
        if (OptStr(args, "target_sector") is { } ts && ts.Length > 0)
            targetSector = Catalog.ResolveSector(ts, mgr.ProbeClusters()).Id;
        else if (args.ContainsKey("target_sector"))
            return Err("spawn_scenario: `target_sector` must be a non-empty sector id or name");

        var sides = mgr.ValidSides();
        if (sides.Count < 2)
            return Err($"spawn_scenario: staging a fight needs at least two sides; this server has: {string.Join(", ", sides)}");
        short sideA = sides[0], sideB = sides[1];

        var (launcherId, launcherRequest) = ResolveLauncherArg(args);

        bool arena = IsArenaMode(args);   // `mode: "arena"|"march"` (2026-10-13); legacy `arena: true` still honoured
        bool staging = arena ? false : OptBool(args, "staging", def: targetSector is not null);
        int totalA = fleetA.Sum(e => e.Count);
        int totalB = fleetB.Sum(e => e.Count);
        var arenaSpots = new List<(Vector3 Pos, Vector3 Fwd)>();
        string arenaNote = "";
        if (arena)
        {
            if (targetSector is not { } tsA) return Err("spawn_scenario: `arena` requires `target_sector` (the rendezvous to place both sides in)");
            if (mgr.ProbeSectorCenter(tsA) is not { } cen)
                return Err($"spawn_scenario: cannot resolve the centre of sector {tsA} for arena mirroring (need warp/station anchors)");
            int n = Math.Max(totalA, totalB);
            var (pa, pb, fa2, fb2) = BotManager.ComputeArenaPlacements(n, cen, new Vector3(0f, 0f, 1f), BotManager.ArenaOffset, 220f);
            for (int i = 0; i < totalA; i++) arenaSpots.Add((pa[i], fa2));
            for (int i = 0; i < totalB; i++) arenaSpots.Add((pb[i], fb2));
            arenaNote = $"arena: {totalA}/{totalB} bots mirrored about sector {tsA} centre (offset {BotManager.ArenaOffset}u) — P554 relocation via the 'relocate' admin cheat on the bot's own connection (needs liveSettings.adminCheats enabled + the bot's name allow-listed)";
        }

        // Per-bot spawn slots (mixed-fleet loadout resolution + mine/missile slicing).
        var slots = new List<ShowcaseSpawnSlot>();
        AddSideFleet(slots, fleetA, sideA, showcase, wingman, core, launcherId);
        AddSideFleet(slots, fleetB, sideB, showcase, wingman, core, launcherId);

        // Lay minefields (native layer drones) BEFORE the fleets meet.
        var minefieldReports = new List<object>();
        if (!mgr.PlanOnly && (mgr.Partition is null || mgr.Partition.Index == 0) && args.TryGetValue("minefields", out var mfj) && mfj.ValueKind == JsonValueKind.Object)
            minefieldReports = LayMinefields(mfj, mgr, targetSector, sideA, sideB, core);

        var spawnSlots = new List<SpawnBatch.Slot>();
        bool refill = OptBool(args, "refill", def: true);

        // P635 — multi-route wings: split each side across ALL its distinct
        // entry alephs into the rendezvous (instead of funnelling the whole fleet
        // through one shortest-path mouth), giving each route its own staging
        // sector, staging/entry/exit instance and release gate. `routes` =
        // "auto" (default; split when ≥2 distinct routes exist) | "single"
        // (today's behaviour). `maxPerRoute` caps bots per route; the excess goes
        // to the least-loaded route (PickLeastLoadedRoute). Only meaningful for a
        // travelling, staging showcase to a rendezvous (never arena, which parks
        // both sides in the target sector).
        string routesArg = (OptStr(args, "routes") ?? "auto").ToLowerInvariant();
        int maxPerRoute = OptInt(args, "maxPerRoute") ?? 100;
        bool wantMultiRoute = routesArg == "auto" && staging && !arena;
        bool multiRoute = wantMultiRoute && targetSector is not null;
        short rz2 = wantMultiRoute && targetSector is { } rzv ? rzv : (short)0;
        var routePlans = new Dictionary<short, List<List<short>>>();
        var routeCounts = new Dictionary<short, int[]>();
        string routesNote = "single (routes not split)";
        if (multiRoute)
        {
            foreach (var side in new[] { sideA, sideB })
            {
                var routes = mgr.FindRoutes(rz2, mgr.FindSideHomeSector(side), 3);
                if (routes.Count >= 2)
                {
                    routePlans[side] = routes;
                    routeCounts[side] = new int[routes.Count];
                }
            }
            if (routePlans.Count > 0)
                routesNote = string.Join("; ", routePlans.Select(kv =>
                    $"{kv.Key}: {string.Join(" | ", kv.Value.Select(r => string.Join("→", r)))}"));
        }

        // P637 — non-rendezvous roles (bombers) target the ENEMY HOME sector
        // (their own objective — the enemy's garrison), not the fighter
        // rendezvous, and get their OWN route/staging toward that target so
        // they never wait on a fighter wing's release. Resolved once per side
        // at spawn (same discovery as the fighter wings). Empty when the
        // enemy home can't be resolved (the bot then just goes to the
        // rendezvous like everyone else).
        var bomberPlans = new Dictionary<short, List<List<short>>>();
        var bomberCounts = new Dictionary<short, int[]>();
        foreach (var side in arena ? Array.Empty<short>() : new[] { sideA, sideB })
        {
            short enemyHome = mgr.FindSideHomeSector(side == sideA ? sideB : sideA) ?? -1;
            if (enemyHome < 0) continue;
            var bRoutes = mgr.FindRoutes(enemyHome, mgr.FindSideHomeSector(side), 3);
            if (bRoutes.Count > 0) { bomberPlans[side] = bRoutes; bomberCounts[side] = new int[bRoutes.Count]; }
        }

        for (int si = 0; si < slots.Count; si++)
        {
            var slot = slots[si];
            var spot = arena && arenaSpots.Count > si ? arenaSpots[si] : default;
            // P637 — per-role target: a BOMBER's own objective is the ENEMY
            // HOME sector (its garrison), not the fighter rendezvous; every
            // other role converges on the rendezvous.
            short? botTarget = BotManager.ResolveScenarioTarget(arena, slot.Role, targetSector,
                mgr.FindSideHomeSector(slot.Side == sideA ? sideB : sideA) ?? -1);

            // P635/P637 — assign this bot to the least-loaded wing of its
            // side. A bomber goes on its OWN (home → enemy-home) route wing,
            // never a fighter wing (whose rendezvous staging it would wait on).
            IReadOnlyList<short>? routePath = null;
            int routeIndex = -1;
            if (!arena && slot.Role == Showcase.HullRole.Bomber && botTarget >= 0
                && bomberPlans.TryGetValue(slot.Side, out var bs))
            {
                var bc = bomberCounts[slot.Side];
                routeIndex = BotManager.PickLeastLoadedRoute(bc);
                bc[routeIndex]++;
                routePath = bs[routeIndex];
            }
            else if (routePlans.TryGetValue(slot.Side, out var rs))
            {
                var counts = routeCounts[slot.Side];
                routeIndex = BotManager.PickLeastLoadedRoute(counts);
                counts[routeIndex]++;
                routePath = rs[routeIndex];
            }
            spawnSlots.Add(new(si, slot.Side, slot.SourceHull, () => mgr.SpawnBot(slot.FlyHullId, slot.FlyHullName, slot.Side, slot.Parts, "combat", botTarget,
                slot.LauncherId, slot.LauncherName, useStaging: staging, arena: arena,
                arenaPosition: arena ? spot.Pos : null, arenaFacing: arena ? spot.Fwd : null,
                wingmanBehaviour: slot.Wingman, routePath: routePath, routeIndex: routeIndex,
                role: slot.Role.ToString(), reportFailure: false)));
        }
        var batch = new SpawnBatch(mgr.Partition?.Select(spawnSlots) ?? spawnSlots);
        if (mgr.PlanOnly) return Ok(JsonSerializer.Serialize(new { sideA = batch.Counts(sideA), sideB = batch.Counts(sideB), totals = new[] { new { side = sideA, requested = totalA }, new { side = sideB, requested = totalB } } }));
        batch.Run(refill, mgr.IsSpawnActive);
        var grants = GrantReports(batch, mgr.Partition is not null);
        int warnings = batch.Results.Count(r => r.Warning is not null);

        finaleNames.AddRange(batch.Results.Select(r => r.Name));   // P656 finale roster from the P657 spawn batch
        var finale = mgr.ConfigureFinale(finaleSettings, finaleNames);
        var (faRep, fbRep) = BuildFleetReport(core, slots);
        var summary = new
        {
            finale,
            battleMode = arena ? "arena" : staging ? "march" : "travel",
            mode = showcase ? "showcase" : (fleetA.Count > 1 || fleetB.Count > 1 ? "mixed-fleet" : "mixed-fleet"),
            preset,
            targetSector,
            arena,
            staging,
            arenaNote,
            wingman,
            routes = routesNote,
            maxPerRoute,
            bombersTarget = "enemy home sector (P637 — non-rendezvous roles get their own route/staging toward their own objective, never wait on a fighter wing's release)",
            refill,
            sideA = new { side = sideA, requested = batch.Counts(sideA).requested, spawned = batch.Counts(sideA).spawned, failed = batch.Counts(sideA).failed, refilled = batch.Counts(sideA).refilled, fleet = faRep },
            sideB = new { side = sideB, requested = batch.Counts(sideB).requested, spawned = batch.Counts(sideB).spawned, failed = batch.Counts(sideB).failed, refilled = batch.Counts(sideB).refilled, fleet = fbRep },
            minefields = minefieldReports,
            grants,
            warnings,
            failures = batch.Failures,
            note = "Scenario bots run in the background for the life of this process; get_perf_stats reports their live update cadence. fleet lists the resolved per-hull loadout and whether the slice carries mines (mounted mine dispenser) / missiles; each bot's wingman flags are in `wingman`.",
        };
        if (batch.Results.Count == 0 && (batch.Counts(sideA).requested + batch.Counts(sideB).requested > 0 || mgr.Partition is null))
            return Err(JsonSerializer.Serialize(summary));
        return Ok(JsonSerializer.Serialize(summary));
    }

    private static object[] GrantReports(SpawnBatch batch, bool worker) => batch.Results.Select(r => worker ? (object)new
    {
        side = r.Side, name = r.Name, grantedHull = r.Granted.HullId,
        grantedParts = r.Granted.Parts.Select(p => p.PartId).ToArray(), warning = r.Warning, refilled = batch.WasRefilled(r.Name),
    } : new
    {
        side = r.Side, name = r.Name, grantedHull = r.Granted.HullId,
        grantedParts = r.Granted.Parts.Select(p => p.PartId).ToArray(), warning = r.Warning,
    }).ToArray();

    /// <summary>P557 — one spawn_scenario combat bot slot (fully resolved).</summary>
    private sealed record ShowcaseSpawnSlot(
        short FlyHullId, string FlyHullName, short Side, IReadOnlyList<ExpandedPartData> Parts,
        short? LauncherId, string? LauncherName, short Wingman,
        string SourceHull, int Tech, bool CarriesMines, Showcase.HullRole Role);

    /// <summary>Expand one side's fleet into per-bot spawn slots: per entry,
    /// resolve the native-preferred loadout at its `tech` (P555/P556); when
    /// <paramref name="giveMines"/> (showcase), give a real mountable mine
    /// dispenser to ~⅓ of each entry (botIndex % 3 == 0) and enable UseMines
    /// (already in the default mask).</summary>
    private static void AddSideFleet(List<ShowcaseSpawnSlot> slots, IReadOnlyList<Showcase.FleetEntry> fleet,
        short side, bool giveMines, short wingman, StaticCore core, short? homoL)
    {
        foreach (var e in fleet)
        {
            var tl = Catalog.NativePreferredLoadout(e.HullId, e.HullName, e.Tech);
            var eff = core.HullTypes[tl.HullId];
            for (int b = 0; b < e.Count; b++)
            {
                var parts = tl.Parts.ToList();
                short? launcherId = homoL;
                string? launcherName = launcherId is { } hl ? Catalog.ResolveLauncherName(hl) : null;
                bool carriesMines = false;
                if (giveMines && b % 3 == 0)
                {
                    var (pid, note) = Showcase.PickMineDispenser(core, eff);
                    if (pid is { } pp)
                    {
                        string? mn = Catalog.AppendLauncher(parts, pp, tl.HullId, tl.HullName, note);
                        if (mn is not null && mn.StartsWith("mounted real mine", StringComparison.Ordinal))
                        {
                            carriesMines = true;
                            launcherId = pp;
                            launcherName = Catalog.ResolveLauncherName(pp);
                        }
                    }
                }
                slots.Add(new ShowcaseSpawnSlot(tl.HullId, tl.HullName, side, parts, launcherId, launcherName,
                    wingman, e.HullName, e.Tech, carriesMines, Showcase.ClassifyHull(eff)));
            }
        }
    }

    /// <summary>P557 — the per-side fleet report fragment: per distinct source
    /// hull, its count, resolved loadout by name, and whether the slice carries
    /// mines / missiles. Returns (sideA, sideB).</summary>
    private static (List<object> A, List<object> B) BuildFleetReport(StaticCore core, List<ShowcaseSpawnSlot> slots)
    {
        if (slots.Count == 0) return (new List<object>(), new List<object>());
        short first = slots[0].Side;
        var aList = slots.Where(s => s.Side == first).ToList();
        var bList = slots.Where(s => s.Side != first).ToList();
        List<object> ReportFor(List<ShowcaseSpawnSlot> list) =>
            list.GroupBy(s => s.SourceHull).Select(g =>
            {
                var first0 = g.First();
                bool anyMissiles = g.Any(s => s.Parts.Any(p =>
                    core.Launchers.ContainsKey(p.PartId) && core.MissileTypes.ContainsKey(core.Launchers[p.PartId].ExpendabletypeId)));
                return new
                {
                    hull = first0.SourceHull,
                    count = g.Count(),
                    tech = first0.Tech,
                    loadout = first0.Parts.Select(p => Catalog.PartName(p.PartId) ?? p.PartId.ToString()).ToArray(),
                    carriesMines = g.Any(s => s.CarriesMines),
                    carriesMissiles = anyMissiles,
                };
            }).Cast<object>().ToList();
        return (ReportFor(aList), ReportFor(bList));
    }

    /// <summary>P557 — lay minefields with native LAYER drones. For each side,
    /// `count` times: invest the side's layer-drone bucket to completion, wait
    /// for the drone, resolve the placement (default: in front of the enemy's
    /// arrival aleph in the rendezvous sector), and order the drone to BUILD
    /// there. Returns a report row per field.</summary>
    private static List<object> LayMinefields(JsonElement mfj, BotManager mgr, short? rendezvous,
        short sideA, short sideB, StaticCore core)
    {
        var rows = new List<object>();
        string sideToken = Str(GetProp(mfj, "side"));
        if (sideToken.Length == 0) sideToken = "both";
        int count = OptIntProp(mfj, "count") ?? 1;
        if (count < 1) count = 1;
        string? atRaw = Str(GetProp(mfj, "at"));
        var sides = new List<short>();
        if (sideToken == "a") sides.Add(sideA);
        else if (sideToken == "b") sides.Add(sideB);
        else { sides.Add(sideA); sides.Add(sideB); }

        foreach (var side in sides)
        {
            var layerBucket = mgr.FindLayerBucket(side);
            if (layerBucket is null)
            {
                rows.Add(new { side, bucket = "(none)", completed = false, error = "no native layer-drone bucket on this side (PilotType Layer not available on this civ/map) — no minefield laid" });
                continue;
            }
            // P560 — the layer drone must be fully BUILT before it can be
            // ordered to lay, so the invest observe window must span the
            // bucket's REAL build time (+ margin) — the old fixed 40 s was far
            // shorter than the 221 map's 90 s layer-bucket build, so the drone
            // never appeared and every field was reported "did not complete".
            string layerId = layerBucket.Value.Id;
            double observe = Math.Max(40.0, layerBucket.Value.TimeToBuildSec + 15.0);
            for (int i = 0; i < count; i++)
            {
                object? field = null;
                string? err = null;
                try
                {
                    short enemySide = (side == sideA) ? sideB : sideA;
                    var placement = ResolveMinefieldPlacement(atRaw, mgr, rendezvous, side, enemySide, core, out var note);
                    var invest = mgr.Invest(side, layerId, null, observe);
                    if (invest.Completed && invest.Drone is { } drone && placement is { } pl)
                    {
                        var order = mgr.OrderDrone(drone.ShipId, "build", pl.TargetDesc);
                        field = new
                        {
                            side,
                            bucket = invest.BucketName,
                            droneShip = drone.ShipId,
                            droneType = drone.Type,
                            placementNote = note,
                            placementTarget = pl.TargetDesc,
                            orderSent = order.Sent,
                            orderAffirmative = order.AffirmativeObserved,
                            orderState = order.StateAfter,
                        };
                    }
                    else
                        err = invest.Completed
                            ? "layer-drone invest completed but no drone/placement resolved — no minefield laid"
                            : "layer-drone invest did not complete in window — no minefield laid";
                }
                catch (Exception ex)
                {
                    // P560 — a minefield failure (ambiguous/unknown bucket, wallet
                    // short, invest error, order refused) must NEVER abort the
                    // fleet spawn: report the error row and continue so the
                    // showcase still stages and launches both sides.
                    err = $"minefield error: {ex.Message}";
                }
                rows.Add(field ?? new { side, bucket = layerBucket, completed = false, error = err });
            }
        }
        return rows;
    }

    /// <summary>P557/P591d — resolve a minefield placement to an order_drone
    /// `build` target (native layer drones take a build order naming a
    /// location/a warp). Default: in front of the ENEMY's arrival aleph in the
    /// rendezvous sector — the aleph the enemy fleet transits to ENTER the
    /// rendezvous — so the arriving enemy flies into the field. P591d fixes the
    /// pre-P591d default (a single arbitrary outbound aleph, same for both
    /// sides, which landed on the wrong aleph) to be per-side: the enemy's home
    /// sector is resolved and its arrival aleph computed from the real map
    /// graph.</summary>
    private static (string TargetDesc, string Note)? ResolveMinefieldPlacement(string atRaw, BotManager mgr,
        short? rendezvous, short layingSide, short enemySide, StaticCore core, out string note)
    {
        note = "";
        if (atRaw is { Length: > 0 } && atRaw.StartsWith("aleph:", StringComparison.OrdinalIgnoreCase))
        {
            var from = Catalog.ResolveSector(atRaw["aleph:".Length..].Trim(), mgr.ProbeClusters());
            var warp = mgr.FindWarpToSector(rendezvous ?? from.Id, from.Id);
            if (warp is not { } w)
            {
                note = $"could not resolve an aleph in {rendezvous} connecting to {from.Name}";
                return null;
            }
            note = $"in front of aleph to {from.Name} ({w.WarpId})";
            return ($"warp:{w.WarpId}", note);
        }
        if (rendezvous is { } rz)
        {
            // P591d — the enemy's actual arrival aleph: find the enemy side's
            // home sector, then the warp the enemy fleet transits through to
            // enter the rendezvous. Each side lays across ITS enemy's arrival
            // aleph (so the two fields sit at the two contested alephs, not
            // both on one arbitrary aleph — the pre-P591d bug).
            short? enemyHome = mgr.FindSideHomeSector(enemySide);
            if (enemyHome is { } eh && mgr.FindEnemyArrivalWarp(rz, enemySide, eh) is { } ew)
            {
                note = $"in front of enemy ({enemySide}) arrival aleph {ew.WarpId} (from {eh}) in sector {rz}";
                return ($"warp:{ew.WarpId}", note);
            }
            // Fallback: any outbound aleph in the rendezvous (pre-P591d
            // behaviour) — only when the enemy's route/home cannot be resolved.
            var warp = mgr.FindArrivalWarp(rz);
            if (warp is { } wa)
            {
                note = $"in front of an arrival aleph {wa.WarpId} in sector {rz} (enemy home unresolved — fallback)";
                return ($"warp:{wa.WarpId}", note);
            }
            note = $"could not resolve an arrival aleph in sector {rz}";
            return null;
        }
        note = "no target_sector and no explicit 'at' — minefield placement unresolved";
        return null;
    }

    private static JsonElement? GetProp(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (obj.TryGetProperty(key, out var v)) return v;
        return null;
    }
    private static int? OptIntProp(JsonElement? obj, string key)
    {
        if (obj is not { ValueKind: JsonValueKind.Object } o) return null;
        if (o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        return null;
    }
    private static string Str(JsonElement? je) => je is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : "";


    /// <summary>Parse a fleet JSON array of {hull,count,tech} into validated
    /// real <see cref="Showcase.FleetEntry"/>s; null when the key is absent.</summary>
    private static List<Showcase.FleetEntry>? ResolveFleetArg(IDictionary<string, JsonElement> args, string key, string label, StaticCore core)
    {
        if (!args.TryGetValue(key, out var je) || je.ValueKind != JsonValueKind.Array) return null;
        var specs = new List<(string Hull, int Count, int Tech)>();
        foreach (var el in je.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) throw new ArgumentException($"spawn_scenario: {key} entry must be an object {{ hull, count, tech? }}");
            if (!el.TryGetProperty("hull", out var hJ) || hJ.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"spawn_scenario: {key} entry is missing a string `hull`");
            int cnt;
            if (!el.TryGetProperty("count", out var cJ) || cJ.ValueKind != JsonValueKind.Number || !cJ.TryGetInt32(out cnt))
                throw new ArgumentException($"spawn_scenario: {key} entry '{hJ.GetString()}' needs an integer `count`");
            int tech = el.TryGetProperty("tech", out var tJ) && tJ.ValueKind == JsonValueKind.Number && tJ.TryGetInt32(out var ti) ? ti : 0;
            specs.Add((hJ.GetString()!, cnt, tech));
        }
        return Showcase.ResolveFleet(core, specs, label).ToList();
    }

    /// <summary>The homogeneous single-slot fleet: `size` bots of the resolved
    /// `hull` at the side's per-side tech level.</summary>
    private static List<Showcase.FleetEntry> HomogeneousFleet(IDictionary<string, JsonElement> args, StaticCore core, string sideLabel)
    {
        if (!args.TryGetValue("size", out var sizeJe) || TryInt(sizeJe) is not { } size || size < 1)
            throw new ArgumentException("spawn_scenario: required integer argument `size` (bots per side) must be ≥ 1 (or give `fleet`/`fleet_a`/`fleet_b`/`preset`)");
        string? hullArg = OptStr(args, "hull");
        var hull = string.IsNullOrWhiteSpace(hullArg) ? Catalog.DefaultFighterHull() : Catalog.ResolveHull(hullArg!);
        int tech = sideLabel == "a" ? OptInt(args, "tech_a") ?? 0 : OptInt(args, "tech_b") ?? 0;
        Showcase.ResolveFleet(core, new[] { (hull.Name, size, tech) }, sideLabel);
        return new List<Showcase.FleetEntry> { new(hull.Id, hull.Name, size, tech) };
    }

    private static bool? BoolProp(JsonElement obj, string key)
    {
        if (obj.TryGetProperty(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
            return v.GetBoolean();
        return null;
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
            botCount = ps.Cadence.BotCount,
            interval = Stat(ps.Cadence.Interval),
            jump = Stat(ps.Cadence.Jump),
            units = "interval = seconds between successive real position updates; jump = meters moved per update; each mean/p95/worst over the last 200 samples per contributing bot.",
            // P633 — the bot decision loop's own health (item 1): decision-pass
            // time distribution, the effective per-bot decision rate and the
            // fleet rate, plus sampled process CPU/RSS/GC and decode throughput.
            decisionPassMs = new
            {
                mean = ps.DecisionPass.MeanMs,
                p50 = ps.DecisionPass.P50Ms,
                p95 = ps.DecisionPass.P95Ms,
                max = ps.DecisionPass.MaxMs,
            },
            decisionRateHz = ps.DecisionRateHz,
            finale = mgr.FinaleStatus(),
            decisionOverruns = mgr.DecisionOverruns,
            stall = StallWatchdog.JsonSnapshot(ps.Stall),
            removedBots = mgr.RemovedBots(),
            fleetDecisionRateHz = ps.FleetDecisionRateHz,
            resources = new
            {
                cpuFraction = ps.Resources.CpuFraction,
                rssMb = ps.Resources.RssMb,
                gcGen0 = ps.Resources.Gen0,
                gcGen1 = ps.Resources.Gen1,
                gcGen2 = ps.Resources.Gen2,
            },
            maxGateHoldMs = ps.MaxGateHoldMs,
            decodeMbPerSec = ps.DecodeMbPerSec,
            // P636d — the snapshot-PUBLISH cost on the receive threads (publishes/s
            // and receive-side rebuild ms/s aggregated across every world) and the
            // decode lag (age of the newest applied update vs wall, mean/p95/max).
            // When ALLEGIANCE_BOT_DECISION_SNAPSHOT=off both publish rates are 0 —
            // the fix this task makes to the P636c switch (which only stopped the
            // read, not the write).
            snapshotPublishPerSec = ps.SnapshotStats.PublishPerSec,
            snapshotPublishMsPerSec = ps.SnapshotStats.PublishMsPerSec,
            decodeLagMs = new
            {
                mean = ps.SnapshotStats.DecodeLagMean,
                p95 = ps.SnapshotStats.DecodeLagP95,
                max = ps.SnapshotStats.DecodeLagMax,
            },
            // P539b — per-bot combat stats (real wire/world events, all
            // attributed to the shooter: shots = real fired rounds, hits = the
            // ones THIS bot caused, split by target side + shield/hull).
            combat = new
            {
                shotsFired = cs.ShotsFired,
                shipHits = cs.ShipHits,          // damaging hits on ENEMY ships
                kills = cs.Kills,
                hitRate = cs.OverallHitRate,
                friendlyHits = cs.FriendlyHits,          // friendly fire by our bots
                enemyShieldHits = cs.EnemyShieldHits,    // enemy hits absorbed by shield
                enemyHullHits = cs.EnemyHullHits,        // enemy hull-or-worse hits
                podsCreated = cs.PodsCreated,
                podsDocked = cs.PodsDocked,
                podsLingering = cs.PodsLingering,
                // P556 — mean time-to-kill per side (first DAMAGING enemy hit on a
                // victim → its death, from the MCP's real wire events), the
                // survivability metric that shows the tech effect even when kills
                // are few. null = no kill with a recorded first hit this run.
                timeToKill = new
                {
                    sideA = TtkReport(0),
                    sideB = TtkReport(1),
                },
                perBot = cs.Bots.Select(b => new
                {
                    name = b.Name, side = b.Side,
                    shotsFired = b.ShotsFired, shipHits = b.ShipHits, kills = b.Kills,
                    hitRate = b.HitRate,
                    enemyShieldHits = b.EnemyShieldHits, enemyHullHits = b.EnemyHullHits,
                    friendlyHits = b.FriendlyHits, noneHits = b.NoneHits,
                    isPod = b.IsPod, podDocked = b.PodDocked,
                }),
            },
            // P635 — per-route (wing) arrival counts, so the multi-route split is
            // verifiable per entry aleph (each wing's assigned vs arrived).
            routes = mgr.RouteArrivals()
                .Select(r => new { side = r.Side, routeIndex = r.RouteIndex, route = r.RouteName, assigned = r.Assigned, arrived = r.Arrived })
                .ToList(),
        }));
    }

    private static object Stat(MetricStat s) => new
    {
        mean = s.Mean,
        p95 = s.P95,
        worstBot = s.WorstBot,
        worstValue = s.WorstValue,
    };

    /// <summary>P556 — the per-side time-to-kill report fragment (mean seconds
    /// + sample count), or null when no kill had a recorded first hit.</summary>
    private static object? TtkReport(short side)
    {
        if (BotManager.MeanTimeToKill(side) is not { } mean) return null;
        return new { meanSeconds = mean, samples = BotManager.TimeToKillCount(side) };
    }

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

    internal static ShowcaseFinale.Settings ReadFinaleSettings(IDictionary<string, JsonElement> args, bool showcase)
    {
        double Number(string key, double fallback)
        {
            if (!args.TryGetValue(key, out var value)) return fallback;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n)) return n;
            throw new ArgumentException($"spawn_scenario: {key} must be a finite number");
        }
        int Integer(string key, int fallback)
        {
            double value = Number(key, fallback);
            if (value != Math.Truncate(value) || value < int.MinValue || value > int.MaxValue)
                throw new ArgumentException($"spawn_scenario: {key} must be an integer");
            return (int)value;
        }
        string side = "leader";
        if (args.TryGetValue("finale_side", out var selection))
            side = selection.ValueKind == JsonValueKind.String ? selection.GetString()!
                : selection.ValueKind == JsonValueKind.Number && selection.TryGetInt32(out int id) ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "invalid";
        var result = new ShowcaseFinale.Settings(Number("finale_after_minutes", showcase ? 30 : 0), side,
            Number("finale_min_relaunch_share", .5), Integer("finale_min_kills_last_5_minutes", 5),
            Integer("finale_bombers", 12), Number("finale_escort_share", .1), Integer("finale_escort_cap", 12),
            (float)Number("finale_escort_radius", 1500), (float)Number("finale_escort_leash", 2500));
        result.Validate();
        return result;
    }

    private static int? OptInt(IDictionary<string, JsonElement> a, string key) =>
        a.TryGetValue(key, out var je) ? TryInt(je) : null;

    /// <summary>2026-10-13 — battle start mode: `mode: "arena"` (or the legacy
    /// `arena: true`) spawns both fleets in the target sector; anything else is
    /// `march` (launch home → wings through the alephs → stage → converge).</summary>
    private static bool IsArenaMode(IDictionary<string, JsonElement> args)
        => string.Equals(OptStr(args, "mode"), "arena", StringComparison.OrdinalIgnoreCase) || OptBool(args, "arena");

    private static bool OptBool(IDictionary<string, JsonElement> a, string key, bool def = false) =>
        a.TryGetValue(key, out var je)
            ? (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False ? je.GetBoolean() : def)
            : def;

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
