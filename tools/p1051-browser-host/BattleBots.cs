using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>Owns the existing MCP driver. Every child is stopped via its exact
/// tracked PID (the driver's finally and MCP's worker ownership do the rest).</summary>
internal sealed class BattleBots : IAsyncDisposable
{
    private readonly string _commands, _scratch;
    private Process? _driver;
    private Task? _startup;
    private readonly CancellationTokenSource _stop = new();
    internal bool Ready { get; private set; }
    internal string? Error => _startup?.Exception?.GetBaseException().Message;
    internal int? DriverPid => _driver?.Id;
    internal BattleBots(string scratch) { _scratch = scratch; _commands = Path.Combine(scratch, "mcp_cmds.jsonl"); }
    internal void Start(int count, int port, string version, string password, string broker, string target)
    {
        string credentials = Path.Combine(_scratch, "bot-logon.json");
        File.WriteAllText(credentials, JsonSerializer.Serialize(new { ClientVersion = version, MissionPassword = password, BrokerDirectory = broker }));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(credentials, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(_commands, ""); File.Delete(_commands + ".ready");
        var start = new ProcessStartInfo("python3") { UseShellExecute = false };
        foreach (string arg in new[] { "scripts/mcp_driver.py", Path.GetFullPath("tools/allegiance-mcp/bin/Debug/net10.0/allegiance-mcp"), "127.0.0.1", port.ToString(), _commands,
            Path.Combine(_scratch, "mcp_out.log"), Path.Combine(_scratch, "mcp_err.log"), "--logon-file", credentials }) start.ArgumentList.Add(arg);
        _driver = Process.Start(start) ?? throw new IOException("Cannot start bot driver.");
        _startup = SpawnAsync(count, target);
    }
    private void Command(string tool, object args, string result) => File.AppendAllText(_commands, JsonSerializer.Serialize(new { tool, args, result_file = Path.Combine(_scratch, result) }) + "\n");
    private async Task WaitFileAsync(string file)
    {
        long deadline = Environment.TickCount64 + 180000;
        while (!File.Exists(file))
        {
            _stop.Token.ThrowIfCancellationRequested();
            if (_driver!.HasExited || Environment.TickCount64 >= deadline) throw new IOException("Bot driver did not finish: " + Path.GetFileName(file));
            await Task.Delay(100, _stop.Token);
        }
    }
    private async Task<JsonDocument> ResultAsync(string name)
    {
        string file = Path.Combine(_scratch, name);
        await WaitFileAsync(file);
        using var result = JsonDocument.Parse(File.ReadAllText(file));
        if (result.RootElement.TryGetProperty("isError", out var error) && error.GetBoolean()) throw new IOException("MCP battle command refused: " + name);
        string body = string.Join("\n", result.RootElement.GetProperty("content").EnumerateArray().Where(c => c.GetProperty("type").GetString() == "text").Select(c => c.GetProperty("text").GetString()));
        return JsonDocument.Parse(body);
    }
    private async Task SpawnAsync(int count, string target)
    {
        await WaitFileAsync(_commands + ".ready");
        foreach (string name in new[] { "scenario.json", "inventory.json", "reserve.json" }) File.Delete(Path.Combine(_scratch, name));
        Command("spawn_scenario", new { sides = new[] { new { count }, new { count } }, target_sector = target, staging = false, loadout_mode = "varied", loadout_seed = 1065 }, "scenario.json");
        using var scenario = await ResultAsync("scenario.json");
        Command("list_active_bots", new { }, "inventory.json");
        using var inventory = await ResultAsync("inventory.json");
        // As in SkirmishDriver.Exchange, reserve one friendly bot slot for the
        // human pilot. This also gives an actual 1 v 1 when count=1.
        var friendly = inventory.RootElement.EnumerateArray().First(b => b.GetProperty("side").GetInt16() == 0 && b.GetProperty("status").GetString() == "active");
        Command("despawn_bot", new { name = friendly.GetProperty("name").GetString() }, "reserve.json");
        using var reserve = await ResultAsync("reserve.json");
        if (reserve.RootElement.GetProperty("stopped").GetInt32() != 1) throw new IOException("Pilot slot was not released.");
        Ready = true;
        File.WriteAllText(Path.Combine(_scratch, "battle-ready"), target);
    }
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
    private bool _disposed;
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (_startup is { } startup) { try { await startup; } catch (Exception) { /* exposed through Error and host state */ } }
        if (_driver is { } driver)
        {
            if (!driver.HasExited)
            {
                // SIGTERM runs the driver's finally; its MCP child disposes the
                // supervisor's exact owned worker PIDs. No process-name matching.
                if (OperatingSystem.IsWindows()) driver.Kill(entireProcessTree: true);
                else kill(driver.Id, 15);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                try { await driver.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { driver.Kill(entireProcessTree: true); await driver.WaitForExitAsync(); }
            }
            driver.Dispose(); _driver = null;
        }
        _stop.Dispose();
    }
}
