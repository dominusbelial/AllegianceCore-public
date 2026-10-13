using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Allegiance.Core;   // P633 — ConsoleLog (non-blocking queued console sink)

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
//     run_hull_matrix  { hulls, weapons?, sizes?, target_sector?, observe_seconds? }
//                      — run a real (hull × weapon × size) combat matrix: one
//                      real per-combination scenario with real kill/survivor/
//                      cadence stats, clearing each combination's own bots
//                      before the next.
//     run_scenario     { preset, size?, target_sector?, timeout_seconds? }
//                      — P474/P490: run one live-verification parity scenario
//                      ('bomber_run' / 'capture_attempt' / 'elimination_run') and
//                      return its structured observed-outcome report (station
//                      shield/hull over time, P451 base-under-attack alerts,
//                      capture refuse-then-capture, P455 elimination + P480
//                      rotation events), cleaning up its bots when done.
//
// Example Claude Code / IDE `.mcp.json` entry (DOCUMENTATION ONLY — wiring it
// into your own config is a local step, see the P426 report doc):
//
//   "mcpServers": {
//     "allegiance": {
//       "command": "dotnet",
//       "args": ["run", "--project", "../tools/allegiance-mcp", "--", "127.0.0.1", "6073"]
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

        // P433 — route ALL console diagnostics to STDERR so the MCP stdio
        // protocol on stdout stays clean. BotManager (and the spawn/combat
        // loops) emit live diagnostics via Console.WriteLine; a real missile-
        // launcher spawn logs during the call, and any stdout log line before
        // a JSON-RPC response corrupts the newline-delimited protocol framing.
        // The StdioServerTransport uses the RAW process stdout (not Console.Out),
        // so redirecting Console.Out to stderr is safe and aligns code with the
        // documented "all diagnostics to stderr" invariant.
        Console.SetOut(Console.Error);

        // P633 — install the single bounded, non-blocking console sink (the
        // same Allegiance.Core.ConsoleLog the server uses, P619c) so NO bot
        // diagnostic ever blocks on the MCP driver's pipe or on a slow stderr
        // consumer: every Console.WriteLine now costs an enqueue on a
        // background drain thread and is dropped (never blocked) when the
        // consumer stalls. All diagnostics still land on stderr (Passthrough =
        // the just-redirected original Error stream). This directly removes the
        // measured per-bot log-spam freeze vector (P633 §Scope 5; the last Mac
        // run's 1.2 MB / 93% [p433-diag] stderr).
        //
        // P633 — the per-bot PER-PASS diagnostic lines ([p433-diag] etc.) are
        // gated behind ConsoleLog.Verbose, which is OFF unless the operator
        // opts in with ALLEGIANCE_MCP_VERBOSE=1. Lifecycle lines (spawn / arena
        // / re-ready) are unaffected (they are plain Console.WriteLine, rare).
        ConsoleLog.Verbose = Environment.GetEnvironmentVariable("ALLEGIANCE_MCP_VERBOSE") == "1";
        ConsoleLog.Install();

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
