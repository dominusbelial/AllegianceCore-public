using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Allegiance.Core;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Allegiance.Tools.McpSpawner;

internal sealed record DriverOptions(string Host, ushort Port, int Workers, WorkerPartition? Worker, int? Parent, string? Skirmish = null, double RecoveryDeadlineSeconds = 900, string? LogonFile = null, int? BackfillSeats = null, string[]? BotNames = null, string? ClientVersion = null, BotTransportOptions? Transport = null, bool SustainableRaid = false)
{
    internal static DriverOptions Parse(string[] args)
    {
        if (args.Length < 2 || !ushort.TryParse(args[1], out var port))
            throw new ArgumentException("usage: allegiance-mcp <host> <port> [--workers N] (default 1)");
        int workers = 1;
        WorkerPartition? worker = null;
        int? parent = null;
        string? skirmish = null;
        double recoveryDeadline = 900;
        string? logonFile = null;
        bool sustainableRaid = false;
        bool explicitWorkers = false;
        int? backfillSeats = null;
        string[]? botNames = null;
        string? clientVersion = null;
        var transport = BotTransportOptions.Read(Environment.GetEnvironmentVariable);
        for (int i = 2; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"missing value for {args[i]}");
            switch (args[i])
            {
                case "--recovery-deadline-seconds":
                    if (!double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out recoveryDeadline)
                        || !double.IsFinite(recoveryDeadline) || recoveryDeadline <= 0 || recoveryDeadline > 86400)
                        throw new ArgumentException("--recovery-deadline-seconds requires a number greater than 0 and at most 86400");
                    break;
                case "--sustainable-raid":
                    if (!bool.TryParse(args[i + 1], out sustainableRaid))
                        throw new ArgumentException("--sustainable-raid requires true or false");
                    break;
                case "--backfill-seats":
                    if (backfillSeats is not null || !int.TryParse(args[i + 1], out int seats) || seats is < 1 or > 200)
                        throw new ArgumentException("--backfill-seats requires 1..200");
                    backfillSeats = seats;
                    break;
                case "--client-version":
                    clientVersion = args[i + 1];
                    if (clientVersion.Length > 64 || !Version.TryParse(clientVersion.TrimStart('v', 'V'), out _))
                        throw new ArgumentException("--client-version requires the installed release version");
                    break;
                case "--bot-names":
                    botNames = args[i + 1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (botNames.Length == 0 || botNames.Any(p => p == "*" || p[..^1].Contains('*')))
                        throw new ArgumentException("--bot-names requires comma-separated exact names or trailing-* prefixes");
                    break;
                case "--transport": transport = transport with { Protocol = args[i + 1] }; break;
                case "--ws-url": transport = transport with { Url = args[i + 1] }; break;
                case "--ws-origin": transport = transport with { Origin = args[i + 1] }; break;
                case "--ws-fraction": transport = transport with { Fraction = BotTransportOptions.ParseFraction(args[i + 1]) }; break;
                case "--ws-ca-file": transport = transport with { CaFile = Path.GetFullPath(args[i + 1]) }; break;
                case "--logon-file": logonFile = Path.GetFullPath(args[i + 1]); break;
                case "--skirmish": skirmish = args[i + 1]; break;
                case "--workers":
                    if (explicitWorkers || !int.TryParse(args[i + 1], out workers) || workers < 1) throw new ArgumentException("--workers requires a positive integer");
                    explicitWorkers = true;
                    break;
                case "--worker":
                    var split = args[i + 1].Split('/');
                    if (worker is not null || split.Length != 2 || !int.TryParse(split[0], out int index) || !int.TryParse(split[1], out int count)
                        || count < 1 || index < 1 || index > count) throw new ArgumentException("--worker requires i/N (1 <= i <= N, N >= 1)");
                    worker = new(index - 1, count);
                    break;
                case "--parent":
                    if (parent is not null || !int.TryParse(args[i + 1], out int pid) || pid < 1) throw new ArgumentException("--parent requires a pid");
                    parent = pid;
                    break;
                default: throw new ArgumentException($"unknown option '{args[i]}'");
            }
        }
        if (worker is not null && explicitWorkers || skirmish is not null && worker is not null) throw new ArgumentException("--worker/--parent are internal child options; cannot combine with --workers");
        if (logonFile is not null && !args[0].Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
            !(System.Net.IPAddress.TryParse(args[0], out var address) && System.Net.IPAddress.IsLoopback(address)))
            throw new ArgumentException("--logon-file is a loopback identity fixture only.");
        if (backfillSeats is not null && (worker is not null || skirmish is not null || workers != 1))
            throw new ArgumentException("--backfill-seats owns one driver; cannot combine with worker/skirmish modes");
        if (sustainableRaid && backfillSeats is null) throw new ArgumentException("--sustainable-raid requires --backfill-seats");
        if (clientVersion is not null && backfillSeats is null) throw new ArgumentException("--client-version requires --backfill-seats");
        if (botNames is not null && backfillSeats is null) throw new ArgumentException("--bot-names requires --backfill-seats");
        transport.Validate();
        if (logonFile is not null && (transport.Protocol == "ws" || transport.Fraction is > 0)
            && !Allegiance.Core.Transport.WebSocketEndpoint.FromConfiguration(transport.Url!).Uri.IsLoopback)
            throw new ArgumentException("--logon-file requires a loopback WebSocket endpoint too");
        return new(args[0], port, workers, worker, parent, skirmish, recoveryDeadline, logonFile, backfillSeats, botNames, clientVersion, transport, sustainableRaid);
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
        BotLogonIdentity.FilePath = options.LogonFile;
        ConsoleLog.Verbose = Environment.GetEnvironmentVariable("ALLEGIANCE_MCP_VERBOSE") == "1";
        ConsoleLog.Install();
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        using var term = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stop.Cancel(); });
        using var interrupt = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; stop.Cancel(); });
        try
        {
            if (options.Parent is { } ownerPid && options.Worker is null)
                ProcessLifetime.WatchOwner(ownerPid, stop.Token);
            if (options.BackfillSeats is { } seats)
            {
                using var manager = new BotManager(options.Host, options.Port, backfill: true, clientVersion: options.ClientVersion, transport: options.Transport);
                manager.SustainableRaidEnabled = options.SustainableRaid;
                var driver = new BackfillDriver(manager, seats, options.BotNames);
                await driver.RunAsync(stop.Token);
            }
            else if (options.Skirmish is not null)
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle;
                await SkirmishDriver.RunAsync(options, stop.Token);
            }
            else if (options.Worker is { } partition)
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
                using var manager = new BotManager(options.Host, options.Port, partition, transport: options.Transport);
                await ProcessWorker.ServeAsync(manager, stop.Token);
            }
            else
            {
                using var supervisor = await WorkerSupervisor.StartAsync(options.Host, options.Port, options.Workers, stop.Token, recoveryDeadlineSeconds: options.RecoveryDeadlineSeconds, transport: options.Transport);
                EventHandler exit = (_, _) => supervisor.Dispose();
                AppDomain.CurrentDomain.ProcessExit += exit;
                try
                {
                    Console.Error.WriteLine($"allegiance-mcp supervisor: pid = {Environment.ProcessId}; workers = {options.Workers}; server = {options.Host}:{options.Port}");
                    await ServeMcpAsync(supervisor.HandleAsync, stop.Token, supervisor: true);
                }
                finally { AppDomain.CurrentDomain.ProcessExit -= exit; }
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
