using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Allegiance.Tools.McpSpawner;

// ─────────────────────────────────────────────────────────────────────────────
// P426 — a spawn-and-forget MCP server for driving real bots against a live
// Allegiance server.
//
//   USAGE:  dotnet run --project tools/allegiance-mcp -- <host> <port>
//           (or run the built bin/Release/net10.0/allegiance-mcp <host> <port>)
//
//   e.g.    dotnet run --project tools/allegiance-mcp -- 127.0.0.1 6073
//
// At startup it connects ONE real probe client (logon + seat, the same flow
// vizfight `attach` uses) to the already-running Allegiance.Server at
// <host>:<port>, then serves the MCP stdio protocol over stdin/stdout. All
// diagnostics go to STDERR so the stdout protocol stream stays clean.
//
//   TOOLS
//     spawn_bot        { hull, side?, parts?, behavior? } — connect a real bot,
//                      seat it, buy the loadout, launch it, return a short real
//                      confirmation; the bot then persists and runs its behavior
//                      in the background for the life of this process.
//     list_hulls       read-only hull catalog from the real PCore001.igc.
//     list_parts       read-only part catalog (id/name/category).
//     list_active_bots read-only: every spawned bot with live ship/hull/side/
//                      sector/position. This is the ONLY visibility into bots —
//                      there is intentionally NO despawn/command tool
//                      (spawn-and-forget; killing this process is how a bot
//                      goes away — a documented known limitation).
//     spawn_scenario   { size, hull?, target_sector? } — spawn a whole fight:
//                      `size` real 'combat' bots per side (2×size), each through
//                      the same spawn_bot path; returns a per-side summary.
//     get_perf_stats   { bot? } — aggregate REAL per-bot update cadence
//                      (interval + jump distance: mean / p95 / worst offender),
//                      the measured evidence for the concurrency sweet spot.
//
// Example Claude Code / IDE `.mcp.json` entry (DOCUMENTATION ONLY — wiring it
// into your own config is a local step, see the P426 report doc):
//
//   "mcpServers": {
//     "allegiance": {
//       "command": "dotnet",
//       "args": ["run", "--project", "/home/domingo/Development/AllegianceCore/tools/allegiance-mcp", "--", "127.0.0.1", "6073"]
//     }
//   }
// ─────────────────────────────────────────────────────────────────────────────

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: allegiance-mcp <host> <port>   (connect a spawn-and-forget bot MCP server to a live Allegiance.Server)");
            return 2;
        }
        string host = args[0];
        if (!ushort.TryParse(args[1], out var port))
        {
            Console.Error.WriteLine($"allegiance-mcp: invalid port '{args[1]}'");
            return 2;
        }

        // Connect the probe to the live server BEFORE serving any tool call.
        // Startup fails loudly (non-zero exit) if the server is unreachable /
        // unresponsive, instead of serving tools against nothing.
        BotManager mgr;
        try
        {
            mgr = new BotManager(host, port);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"allegiance-mcp: startup failed: {ex.Message}");
            return 1;
        }

        using (mgr)
        {
            Console.Error.WriteLine($"allegiance-mcp: connected to live server {host}:{port} (probe seated); serving stdio MCP tools...");

            var serverOptions = new McpServerOptions
            {
                ServerInfo = new() { Name = "allegiance-mcp", Version = "0.1.0" },
                Capabilities = new() { Tools = new() },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = (ctx, ct) =>
                        ValueTask.FromResult(new ListToolsResult { Tools = McpTools.Definitions().ToList() }),
                    CallToolHandler = (ctx, ct) =>
                        ValueTask.FromResult(McpTools.Handle(ctx.Params.Name, ctx.Params.Arguments, mgr)),
                },
            };

            await using var server = McpServer.Create(
                new StdioServerTransport(serverOptions, loggerFactory: null),
                serverOptions,
                loggerFactory: null,
                serviceProvider: null);

            await server.RunAsync();
        }
        return 0;
    }
}
