using System.Net;
using System.Net.Sockets;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.SinglePlayer;

internal static class LocalHostProof
{
    internal static async Task Run(string output, bool lan)
    {
        CrashLog.DataDirectoryOverride = Path.GetFullPath(output);
        CrashLog.Install(); // Same tee installed by the game client's Program.Main.
        Environment.SetEnvironmentVariable("ALLEGIANCE_TICK_PROFILER", "1");
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        using (var host = await SkirmishHost.StartAsync(new()
        {
            HostLan = lan, HostPort = port, GameName = "P907 local host proof",
            Size = SkirmishSize.Small, Commander = SkirmishCommander.You,
        }))
        {
            await Task.Delay(6500);
            if (host.Failure is { } failure) throw new IOException(failure);
            Console.WriteLine($"[local-host-proof] mode={(lan ? "lan" : "skirmish")} botsAlive={host.BotsAlive}");
        }
        string text = File.ReadAllText(CrashLog.EnsureRolling());
        if (!text.Contains("[thread-pool] scope=process") || text.Contains("[udp-receive]") != lan)
            throw new IOException("embedded server metrics missing from real client.log tee");
        File.WriteAllLines(Path.Combine(output, "metrics.log"), text.Split('\n').Where(line =>
            line.Contains("[thread-pool]") || line.Contains("[udp-receive]") || line.Contains("[local-host-proof]")));
    }
}
