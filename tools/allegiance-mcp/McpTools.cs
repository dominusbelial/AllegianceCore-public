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
                        behavior = new { type = "string", @enum = new[] { "idle", "weave" }, @default = "idle", description = "Behavior preset: 'idle' seats but sends no movement; 'weave' runs the real loiter/weave flight loop (moving ship)." },
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
                Description = "Read-only: every bot this MCP server process has spawned and is still connected to, with its real live ship id, hull, side, sector and position (from its own server-reported world). This is the only visibility into spawned bots; there is intentionally no despawn/command tool.",
                InputSchema = Schema(new { type = "object" }),
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
                "list_hulls" => Ok(JsonSerializer.Serialize(Catalog.RepresentativeCore().HullTypes.Values
                    .OrderBy(h => h.HullId).Select(h => new { id = h.HullId, name = h.Name }))),
                "list_parts" => Ok(JsonSerializer.Serialize(Catalog.AllParts()
                    .Select(p => new { id = p.Id, name = p.Name, category = p.Category }))),
                "list_active_bots" => Ok(JsonSerializer.Serialize(mgr.ActiveBots()
                    .Select(b => new { name = b.Name, shipId = b.ShipId, side = b.Side, hull = b.Hull, sector = b.Sector, behavior = b.Behavior, pos = b.Position }))),
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
        if (behavior is not ("idle" or "weave"))
            return Err($"spawn_bot: behavior must be 'idle' or 'weave', got '{behavior}'");

        // All name/side validation happens BEFORE anything touches the wire.
        var hull = Catalog.ResolveHull(hullArg!);
        IReadOnlyList<ExpandedPartData> parts;
        if (args.TryGetValue("parts", out var partsJe) && AsStrings(partsJe) is { Count: > 0 } names)
            parts = Catalog.ResolveParts(names);
        else
            parts = Catalog.DefaultParts();

        int? side = args.TryGetValue("side", out var sJe) ? TryInt(sJe) : null;

        var result = mgr.SpawnBot(hull.Id, hull.Name, side is { } sv ? (short)sv : null, parts, behavior);
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
