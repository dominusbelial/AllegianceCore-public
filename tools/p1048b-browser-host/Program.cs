// Local proof only. Real lobby application, enrollment, identity and authoritative game.
using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Identity;
using Allegiance.Lobby;
using Allegiance.Lobby.Identity;
using Allegiance.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;

if (args.Length != 3) throw new ArgumentException("p1048b-browser-host ARTWORK PUBLISHED_WWWROOT SCRATCH");
string art = Path.GetFullPath(args[0]), assets = Path.GetFullPath(args[1]), scratch = Path.GetFullPath(args[2]);
Directory.CreateDirectory(scratch);
using var freePort = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
freePort.Start(); int tcpPort = ((System.Net.IPEndPoint)freePort.LocalEndpoint).Port; freePort.Stop();
File.Delete(Path.Combine(scratch, "stop"));
using var lobby = new ProofLobby(scratch);
lobby.UseKestrel(8049);
using var http = lobby.CreateClient(); // starts the actual app with unchanged admission/CSRF/limits
var identities = lobby.Services.GetRequiredService<IdentityStore>();
var identitySettings = lobby.Services.GetRequiredService<IdentitySettings>();
var account = identities.CreateAccount("ProofOperator");
identitySettings.OperatorAccountIds = [account.Id];
var device = identities.StartDevice(new DeviceStartRequest("server", "P1048B loopback server"));
if (!identities.Approve(device.UserCode, account.Id)) throw new InvalidOperationException("fixture operator approval failed");
var approved = identities.Poll(device.DeviceCode);
if (approved.ServerId is null || approved.ServerSecret is null) throw new InvalidOperationException("fixture enrollment failed");
string enrollment = Path.Combine(scratch, "enrollment.json");
await File.WriteAllTextAsync(enrollment, JsonSerializer.Serialize(new ServerEnrollment(approved.ServerId, approved.ServerSecret), IdentityJsonContext.Default.ServerEnrollment));
using var server = new Allegiance.Server.Server { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = art, StaticCore = "PCore001.igc", Port = tcpPort, BindAddress = "127.0.0.1", UpdatesPerSecond = 20,
    Discovery = new() { Enabled = false },
    WebSocket = new() { Enabled = true, Port = 0, BindAddress = "127.0.0.1", AllowedOrigins = ["http://localhost:8048"],
        AllowLoopbackDevelopment = true, PublicUrl = "ws://localhost:8048/game" },
    Identity = new() { Issuer = identitySettings.PublicUrl, Audience = approved.ServerId, PublicKeyFile = identitySettings.PublicKeyFile, AllowAnonymous = false },
    Lobby = new() { Url = "http://127.0.0.1:8049", PublicAddress = "127.0.0.1", DisplayName = "P1048B loopback", EnrollmentFile = enrollment },
    Games = [new() { Name = "P1048B loopback mission", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
        AutoStart = true, AllowEmptyTeams = true, StartCountdownSeconds = 0 }],
});
using var proxy = Process.Start(new ProcessStartInfo("node") { UseShellExecute = false,
    ArgumentList = { "tools/p1048b-loopback-proxy.cjs", assets, server.WebSocketPort.ToString() } })!;
try
{
    Console.WriteLine("P1048B_HOST_READY");
    while (!File.Exists(Path.Combine(scratch, "stop")))
    {
        server.TickSim();
        var state = new { players = server.LivePlayerNames, ships = server.Missions.SelectMany(m => m.GetShips()).Select(s => new
            { s.Id, s.SideId, s.StationId, position = s.Position.ToString() }).ToArray() };
        // No credentials in proof evidence.
        string temp = Path.Combine(scratch, "state.tmp");
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state)); File.Move(temp, Path.Combine(scratch, "state.json"), true);
        await Task.Delay(10);
    }
}
finally { if (!proxy.HasExited) { proxy.Kill(); await proxy.WaitForExitAsync(); } }

sealed class ProofLobby(string scratch) : WebApplicationFactory<ServerRegistry>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseContentRoot(Path.GetFullPath("src/Allegiance.Lobby")).ConfigureServices(services =>
        {
            services.RemoveAll<IdentitySettings>();
            services.AddSingleton(new IdentitySettings
            {
                PublicUrl = "http://localhost:8048",
                ConnectionString = "Data Source=" + Path.Combine(scratch, "accounts.db"),
                SigningKeyFile = Path.Combine(scratch, "signing.pem"),
                PublicKeyFile = Path.Combine(scratch, "public.pem"),
            });
        });
}
