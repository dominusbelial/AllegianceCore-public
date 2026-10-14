using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Allegiance.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Allegiance.Tools.McpSpawner;

internal sealed record DriverOptions(string Host, ushort Port, int Workers, WorkerPartition? Worker, int? Parent)
{
    internal static DriverOptions Parse(string[] args)
    {
        if (args.Length < 2 || !ushort.TryParse(args[1], out var port))
            throw new ArgumentException("usage: allegiance-mcp <host> <port> [--workers N] (default 1)");
        int workers = 1;
        WorkerPartition? worker = null;
        int? parent = null;
        bool explicitWorkers = false;
        for (int i = 2; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"missing value for {args[i]}");
            switch (args[i])
            {
                case "--workers":
                    if (explicitWorkers || !int.TryParse(args[i + 1], out workers) || workers < 1) throw new ArgumentException("--workers requires a positive integer");
                    explicitWorkers = true;
                    break;
                case "--worker":
                    var split = args[i + 1].Split('/');
                    if (worker is not null || split.Length != 2 || !int.TryParse(split[0], out int index) || !int.TryParse(split[1], out int count)
                        || count < 2 || index < 1 || index > count) throw new ArgumentException("--worker requires i/N (1 <= i <= N, N > 1)");
                    worker = new(index - 1, count);
                    break;
                case "--parent":
                    if (parent is not null || !int.TryParse(args[i + 1], out int pid) || pid < 1) throw new ArgumentException("--parent requires a pid");
                    parent = pid;
                    break;
                default: throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }
        if (worker is not null && explicitWorkers || parent is not null && worker is null) throw new ArgumentException("--worker/--parent are internal child options; cannot combine with --workers");
        return new(args[0], port, workers, worker, parent);
    }
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        DriverOptions options;
        try { options = DriverOptions.Parse(args); }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        // MCP and private worker RPC use raw stdout. All bot diagnostics use stderr.
        Console.SetOut(Console.Error);
        ConsoleLog.Verbose = Environment.GetEnvironmentVariable("ALLEGIANCE_MCP_VERBOSE") == "1";
        ConsoleLog.Install();
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        using var term = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Cancel(); });
        using var interrupt = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; stop.Cancel(); });
        try
        {
            if (options.Worker is { } partition)
            {
                Console.Error.WriteLine($"allegiance-mcp worker {partition.Index + 1}/{partition.Count}: gc = {(GCSettings.IsServerGC ? "server" : "workstation")}; pid = {Environment.ProcessId}");
                if (options.Parent is { } parent)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var owner = Process.GetProcessById(parent);
                            await owner.WaitForExitAsync(stop.Token);
                        }
                        catch (ArgumentException) { } // parent already exited
                        catch (OperationCanceledException) { return; }
                        Process.GetCurrentProcess().Kill();
                    });
                using var manager = new BotManager(options.Host, options.Port, partition);
                await ProcessWorker.ServeAsync(manager, stop.Token);
            }
            else if (options.Workers > 1)
            {
                using var supervisor = await WorkerSupervisor.StartAsync(options.Host, options.Port, options.Workers, stop.Token);
                EventHandler exit = (_, _) => supervisor.Dispose();
                AppDomain.CurrentDomain.ProcessExit += exit;
                try
                {
                    Console.Error.WriteLine($"allegiance-mcp supervisor: pid = {Environment.ProcessId}; workers = {options.Workers}; server = {options.Host}:{options.Port}");
                    await ServeMcpAsync(supervisor.HandleAsync, stop.Token, supervisor: true);
                }
                finally { AppDomain.CurrentDomain.ProcessExit -= exit; }
            }
            else
            {
                // Default path retains the original single BotManager and probe.
                using var manager = new BotManager(options.Host, options.Port);
                Console.Error.WriteLine($"allegiance-mcp: gc = {(GCSettings.IsServerGC ? "server" : "workstation")}/{GCSettings.LatencyMode} (server GC recommended for large bot fleets)");
                Console.Error.WriteLine($"allegiance-mcp: connected to live server {options.Host}:{options.Port} (probe seated); serving stdio MCP tools...");
                await ServeMcpAsync((name, arguments, _) => Task.FromResult(McpTools.Handle(name, arguments, manager)), stop.Token);
            }
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex) { Console.Error.WriteLine($"allegiance-mcp: startup/transport failed: {ex.Message}"); return 1; }
    }

    private static async Task ServeMcpAsync(Func<string, IDictionary<string, System.Text.Json.JsonElement>?, CancellationToken, Task<CallToolResult>> handle, CancellationToken ct, bool supervisor = false)
    {
        var options = new McpServerOptions
        {
            ServerInfo = new() { Name = "allegiance-mcp", Version = "0.1.0" },
            Capabilities = new() { Tools = new() },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = McpTools.Definitions(supervisor).ToList() }),
                CallToolHandler = async (ctx, token) => await handle(ctx.Params.Name, ctx.Params.Arguments, token),
            },
        };
        await using var server = McpServer.Create(new StdioServerTransport(options, loggerFactory: null), options, loggerFactory: null, serviceProvider: null);
        await server.RunAsync(ct);
    }
}
