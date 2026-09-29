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
            parts = Catalog.DefaultParts();

        int? side = args.TryGetValue("side", out var sJe) ? TryInt(sJe) : null;

        var result = mgr.SpawnBot(hull.Id, hull.Name, side is { } sv ? (short)sv : null, parts, behavior, targetSector);
        return Ok(JsonSerializer.Serialize(new
        {
            ok = true,
            name = result.Name,
            shipId = result.ShipId,
            hull = result.Hull,
            side = result.Side,
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

        // Need two real sides to stage a fight.
        var sides = mgr.ValidSides();
        if (sides.Count < 2)
            return Err($"spawn_scenario: staging a fight needs at least two sides; this server has: {string.Join(", ", sides)}");
        short sideA = sides[0], sideB = sides[1];

        int spawnedA = 0, spawnedB = 0;
        var failures = new List<object>();
        for (int i = 0; i < size; i++)
        {
            try { mgr.SpawnBot(hull.Id, hull.Name, sideA, Catalog.DefaultParts(), "combat", targetSector); spawnedA++; }
            catch (Exception ex) { failures.Add(new { index = i, side = sideA, error = ex.Message }); }
            try { mgr.SpawnBot(hull.Id, hull.Name, sideB, Catalog.DefaultParts(), "combat", targetSector); spawnedB++; }
            catch (Exception ex) { failures.Add(new { index = i, side = sideB, error = ex.Message }); }
        }

        var summary = new
        {
            size,
            hull = hull.Name,
            targetSector,
            sideA = new { side = sideA, spawned = spawnedA },
            sideB = new { side = sideB, spawned = spawnedB },
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
        return Ok(JsonSerializer.Serialize(new
        {
            botCount = ps.BotCount,
            interval = Stat(ps.Interval),
            jump = Stat(ps.Jump),
            units = "interval = seconds between successive real position updates; jump = meters moved per update; each mean/p95/worst over the last 200 samples per contributing bot.",
        }));
    }

    private static object Stat(MetricStat s) => new
    {
        mean = s.Mean,
        p95 = s.P95,
        worstBot = s.WorstBot,
        worstValue = s.WorstValue,
    };

    // ── Helpers ────────────────────────────────────────────────────────────

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
