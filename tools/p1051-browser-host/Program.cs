// Local proof only. Real lobby application, enrollment, identity and authoritative game.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using System.Text.Json;
using Allegiance.Core.Identity;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.Flight;
using SimVec = Allegiance.Core.Collision.Vector3;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Lobby;
using Allegiance.Lobby.Identity;
using Allegiance.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;

if (args.Length != 3) throw new ArgumentException("p1051-browser-host ARTWORK PUBLISHED_WWWROOT SCRATCH");
string art = Path.GetFullPath(args[0]), assets = Path.GetFullPath(args[1]), scratch = Path.GetFullPath(args[2]);
Directory.CreateDirectory(scratch);
using var freePort = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
freePort.Start(); int tcpPort = ((System.Net.IPEndPoint)freePort.LocalEndpoint).Port; freePort.Stop();
File.Delete(Path.Combine(scratch, "stop"));
// Keep test private key material in a Linux anonymous memory file. The actual
// production signer imports it through its ordinary path-based constructor.
using var signingMaterial = MemorySigningMaterial.Create();
using var lobby = new ProofLobby(scratch, signingMaterial.Path);
lobby.UseKestrel(8049);
using var http = lobby.CreateClient(); // starts the actual app with unchanged admission/CSRF/limits
var identities = lobby.Services.GetRequiredService<IdentityStore>();
var identitySettings = lobby.Services.GetRequiredService<IdentitySettings>();
var account = identities.CreateAccount("ProofOperator");
identitySettings.OperatorAccountIds = [account.Id];
var device = identities.StartDevice(new DeviceStartRequest("server", "P1051 loopback server"));
if (!identities.Approve(device.UserCode, account.Id)) throw new InvalidOperationException("fixture operator approval failed");
var approved = identities.Poll(device.DeviceCode);
if (approved.ServerId is null || approved.ServerSecret is null) throw new InvalidOperationException("fixture enrollment failed");
string enrollment = Path.Combine(scratch, "enrollment.json");
await File.WriteAllTextAsync(enrollment, JsonSerializer.Serialize(new ServerEnrollment(approved.ServerId, approved.ServerSecret), IdentityJsonContext.Default.ServerEnrollment));
using var server = new Allegiance.Server.Server { SpawnInitialMinersAtBoot = false, SimulationRandom = new Random(1051) };
server.Boot(new ServerConfig
{
    ArtPath = art, StaticCore = "PCore001.igc", Port = tcpPort, BindAddress = "127.0.0.1", UpdatesPerSecond = 20,
    Discovery = new() { Enabled = false },
    WebSocket = new() { Enabled = true, Port = 0, BindAddress = "127.0.0.1", AllowedOrigins = ["http://localhost:8048"],
        AllowLoopbackDevelopment = true, PublicUrl = "ws://localhost:8048/game" },
    Reconnect = new() { Enabled = true, GraceSeconds = 10 },
    Identity = new() { Issuer = identitySettings.PublicUrl, Audience = approved.ServerId, PublicKeyFile = identitySettings.PublicKeyFile, AllowAnonymous = false },
    Lobby = new() { Url = "http://127.0.0.1:8049", PublicAddress = "127.0.0.1", DisplayName = "P1051 loopback", EnrollmentFile = enrollment },
    Games = [new() { Name = "P1051 loopback mission", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], CivSeed = 1051, SideNames = ["Alpha", "Bravo"],
        AutoStart = true, AllowEmptyTeams = true, StartCountdownSeconds = 0 }],
});
using var proxy = Process.Start(new ProcessStartInfo("node") { UseShellExecute = false,
    ArgumentList = { "tools/p1051-loopback-proxy.cjs", assets, server.WebSocketPort.ToString(), scratch } })!;
using var desktop = new ConnectionSession();
using var keeper = new ConnectionSession();
var desktopAccount = identities.CreateAccount("DesktopPilot");
var keeperAccount = identities.CreateAccount("KeeperPilot");
var signing = lobby.Services.GetRequiredService<IdentitySigningKey>();
var core = StaticCore.Load(IgcReader.Parse(File.ReadAllBytes(Path.Combine(art, "PCore001.igc"))));
desktop.World.MissionCoreResolver = _ => core;
keeper.World.MissionCoreResolver = _ => core;
// A headless pilot can follow the shipped map as a human with a map chart can.
// Only ordinary wire controls enter the server; scanner/visibility still govern
// both clients' live entity observations. This is not server state injection.
var navigationWarps = MissionMap.Load(IgcReader.ParseFile(Path.Combine(art, "XXV_simple.igc"))).Warps.ToDictionary(w => w.WarpId);
var chart = navigationWarps.Values.Select(w => new { w.WarpId, w.ClusterId, w.Name, w.Position,
    destinationSectorId = navigationWarps[w.DestinationId].ClusterId }).ToArray();
Task? desktopJoin = null;
Task? keeperJoin = null;
bool keeperSeated = false;
bool desktopSeated = false, desktopLaunched = false;
int desktopApproach = 0;
long desktopReadyGeneration = -1, desktopNextLaunch = 0;
string desktopNavigation = "waiting";
long desktopShotsReceived = 0;
var desktopShotLaunchers = new HashSet<short>();
var controlHistory = new List<object>();
var lastControls = new Dictionary<short, (ControlData, int)>();
try
{
    Console.WriteLine("P1051_HOST_READY");
    while (!File.Exists(Path.Combine(scratch, "stop")))
    {
        server.TickSim();
        // Malformed admission fixtures use the production signer and validator.
        // Requests contain no secrets; bearer output stays in ignored scratch.
        string ticketRequestPath = Path.Combine(scratch, "ticket-request.json");
        if (File.Exists(ticketRequestPath))
        {
            using var request = JsonDocument.Parse(await File.ReadAllTextAsync(ticketRequestPath));
            var fixtureAccount = identities.Account(request.RootElement.GetProperty("accountId").GetString()!)
                ?? throw new InvalidDataException("Unknown fixture account");
            string kind = request.RootElement.GetProperty("kind").GetString()!;
            if (kind is not ("expired" or "wrong-audience")) throw new InvalidDataException("Invalid ticket fixture");
            string ticket = signing.Issue(fixtureAccount, kind == "wrong-audience" ? "p1051-other-server" : approved.ServerId,
                kind == "expired" ? DateTimeOffset.UtcNow.AddMinutes(-5) : DateTimeOffset.UtcNow);
            string responsePath = Path.Combine(scratch, "ticket-response.json");
            await File.WriteAllTextAsync(responsePath + ".tmp", JsonSerializer.Serialize(new { token = ticket }));
            File.Move(responsePath + ".tmp", responsePath, true); File.Delete(ticketRequestPath);
        }
        if (File.Exists(Path.Combine(scratch, "desktop-join")) && desktopJoin is null)
        {
            desktopJoin = desktop.JoinAsync(checked((ushort)tcpPort), desktopAccount.Callsign,
                new LogonRequest { CharacterName = desktopAccount.Callsign, FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1,
                    DwCookie = uint.MaxValue, Time = 1, ZoneTicket = System.Text.Encoding.UTF8.GetBytes(signing.Issue(desktopAccount, approved.ServerId, DateTimeOffset.UtcNow)) });
        }
        if (desktopJoin?.IsCompletedSuccessfully == true && !desktopSeated)
        { desktop.SendPositionReq(1); desktopSeated = true; }
        if (File.Exists(Path.Combine(scratch, "keeper-join")) && keeperJoin is null)
            keeperJoin = keeper.JoinAsync(checked((ushort)tcpPort), keeperAccount.Callsign,
                new LogonRequest { CharacterName = keeperAccount.Callsign, FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1,
                    DwCookie = uint.MaxValue, Time = 1, ZoneTicket = System.Text.Encoding.UTF8.GetBytes(signing.Issue(keeperAccount, approved.ServerId, DateTimeOffset.UtcNow)) });
        if (keeperJoin?.IsCompletedSuccessfully == true && !keeperSeated) { keeper.SendPositionReq(0); keeperSeated = true; }
        if (desktop.World.MySideId == 1 && desktop.World.MyShipId is { } desktopId
            && desktop.World.Ships.TryGetValue(desktopId, out var desktopShip))
        {
            if (desktop.World.MissionGeneration != desktopReadyGeneration)
            { desktop.SendPlayerReady(true, desktopId); desktopReadyGeneration = desktop.World.MissionGeneration; }
            if (desktopShip.StationId >= 0 && desktop.World.Mission?.Stage == MissionStageMessage.StageStarted
                && Environment.TickCount64 >= desktopNextLaunch)
            { desktop.SendDocked(); desktopNextLaunch = Environment.TickCount64 + 1000; }
            desktopLaunched = desktopShip.StationId < 0;
            if (desktopLaunched && desktopShip.Latest is { } pose)
            {
                // An opposing player crosses the real aleph, respecting team
                // balance and scanner visibility. No server-side repositioning.
                var goalSector = desktop.World.Clusters.Values.FirstOrDefault(c => c.Name == "Ceto").ClusterId;
                // Follow only the browser pose actually visible in this client's world.
                // Rotated matches may launch from the opposite station bay.
                var observedPilot = desktop.World.Ships.Values.FirstOrDefault(s => s.ShipId != desktopId
                    && s.SectorId == desktopShip.SectorId && s.StationId < 0 && s.Latest is not null
                    && s.CharacterName?.StartsWith("Browser1051", StringComparison.Ordinal) == true);
                float baySide = observedPilot?.Latest?.Position.Y < 0 ? -1 : 1;
                SimVec goal = observedPilot?.Latest is { } observedPose
                    ? observedPose.Position + new SimVec(-75, baySide * 130, -10) : new(-75, 550, 100);
                if (desktopShip.SectorId != goalSector) desktopApproach = 0;
                else if (desktopApproach < 2)
                {
                    SimVec clearance = desktopApproach == 0 ? new(-1800, -1000, 1500) : new(-700, baySide * 1000, 300);
                    if ((clearance - pose.Position).Length() < 150) desktopApproach++;
                    else goal = clearance;
                }
                bool routed = false;
                if (desktopShip.SectorId != goalSector)
                {
                    var queue = new Queue<(short Sector, short First)>();
                    var visited = new HashSet<short> { desktopShip.SectorId };
                    queue.Enqueue((desktopShip.SectorId, -1));
                    short route = -1;
                    while (queue.Count > 0 && route < 0)
                    {
                        var current = queue.Dequeue();
                        foreach (var warp in navigationWarps.Values.Where(w => w.ClusterId == current.Sector))
                        {
                            if (!navigationWarps.TryGetValue(warp.DestinationId, out var destination) || !visited.Add(destination.ClusterId)) continue;
                            short first = current.First < 0 ? warp.WarpId : current.First;
                            if (destination.ClusterId == goalSector) { route = first; break; }
                            queue.Enqueue((destination.ClusterId, first));
                        }
                    }
                    if (route >= 0)
                    {
                        var warp = navigationWarps[route];
                        goal = new(warp.Position[0], warp.Position[1], warp.Position[2]); routed = true;
                    }
                    desktopNavigation = routed ? "warp approach" : "waiting for known route";
                }
                else desktopNavigation = "rendezvous";
                // The proof pilot navigates real hazards too. Reuse the existing
                // client Dodge feed (common.cpp:1692-1790,2000-2018), with only
                // client-visible bodies and real static-core hull/radius data.
                // This changes ordinary pilot input, never a world position.
                SimVec? avoidanceGoal = null;
                lock (desktop.Gate)
                    if (core.HullTypes.TryGetValue(desktopShip.HullId, out var moverHull)
                        && ClientAutopilotDodge.TryComputeDodgeGoal(desktop.World, desktopId, desktopShip.SideId,
                            pose.Position, pose.Velocity, new(moverHull.Length, moverHull.Thrust, moverHull.Mass), true,
                            id => core.HullTypes.TryGetValue(id, out var h) ? h.Length : null,
                            id => core.StationTypes.TryGetValue(id, out var st) ? st.Radius : null,
                            null, out var dodgeGoal)) avoidanceGoal = dodgeGoal;
                var controls = avoidanceGoal is { } escape ? AutopilotSteering.Pursue(pose.Position, pose.Orientation, escape)
                    : routed ? AutopilotSteering.Pursue(pose.Position, pose.Orientation, goal)
                    : AutopilotSteering.SteerToward(pose.Position, pose.Orientation, goal, pose.Velocity);
                bool reached = !routed && desktopApproach == 2 && desktopShip.SectorId == goalSector && (goal - pose.Position).Length() < 65;
                bool firing = File.Exists(Path.Combine(scratch, "desktop-fire"));
                if (reached)
                {
                    // Aim into empty space for the receipt, rather than at the
                    // other pilot. This is still ordinary turn/fire input.
                    var away = pose.Position + new SimVec(0, 0, 1000);
                    controls = AutopilotSteering.SteerToward(pose.Position, pose.Orientation, away, pose.Velocity) with { Throttle = -1 };
                    desktopNavigation = "on station";
                }
                desktop.CommandControls(controls, firing ? ShipControlState.AllWeapons : 0);
                desktopShotsReceived = Math.Max(desktopShotsReceived, desktop.World.Diag.TotalReceived);
                foreach (var shot in desktop.World.Projectiles) desktopShotLaunchers.Add(shot.LauncherId);
            }
        }
        foreach (var ship in server.Missions.SelectMany(m => m.GetShips()))
        {
            var key = (ship.Controls, ship.StateM);
            if (controlHistory.Count < 2048 && lastControls.GetValueOrDefault(ship.Id) != key)
            { controlHistory.Add(new { ship.Id, ship.Name, yaw = ship.Controls.Yaw, pitch = ship.Controls.Pitch, throttle = ship.Controls.Throttle, ship.StateM, position = ship.Position.ToString() }); lastControls[ship.Id] = key; }
        }
        var posePeers = (System.Collections.IDictionary)typeof(Allegiance.Server.Server).GetField("_peerShipUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
        var livePeers = (System.Collections.IDictionary)typeof(Allegiance.Server.Server).GetField("_peerShips", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
        var poseReceipts = new List<object>();
        foreach (System.Collections.DictionaryEntry peer in posePeers)
        {
            if (livePeers[peer.Key] is not { } live) continue;
            var ship = (Ship)live.GetType().GetProperty("Ship")!.GetValue(live)!;
            var receipt = peer.Value!;
            object Read(string name) => receipt.GetType().GetField(name)!.GetValue(receipt)!;
            poseReceipts.Add(new { ship.Id, accepted = Read("Accepted"), rejected = Read("Rejected"),
                lastClientTime = Read("LastClientTime"), lastPoseAt = Read("LastPoseAt") is double poseAt && double.IsFinite(poseAt) ? (double?)poseAt : null });
        }
        var state = new { sampledAt = Environment.TickCount64, poseReceipts, chart, keeper = new { keeper.World.MyShipId, keeper.World.MySideId }, desktop = new { joined = desktopJoin?.IsCompletedSuccessfully == true, error = desktopJoin?.Exception?.GetBaseException().Message,
            desktop.World.MyShipId, desktop.World.MySideId, desktopLaunched, desktopNavigation, knownClusters = desktop.World.Clusters.Count, knownWarps = desktop.World.Warps.Count, stage = desktop.World.Mission?.Stage, desktopShotLaunchers = desktopShotLaunchers.ToArray(),
            ships = desktop.World.Ships.Values.Select(s => new { s.ShipId, s.CharacterName, s.SectorId, s.StationId }).ToArray(), desktopShotsReceived },
            controlHistory, projectiles = server.Missions.SelectMany(m => m.Sectors).Sum(s => s.Models.Count(m => m is Projectile)), players = server.LivePlayerNames, ships = server.Missions.SelectMany(m => m.GetShips()).Select(s => new
            { s.Id, s.Name, s.SideId, s.StationId, sectorId = s.Sector?.Id, s.Ammo, weaponCount = s.Weapons.Count, parts = s.Parts.Select(p => new { p.PartId, p.MountId, p.Amount, p.FractionMounted }).ToArray(), yaw = s.Controls.Yaw, pitch = s.Controls.Pitch, throttle = s.Controls.Throttle, s.StateM, position = s.Position.ToString() }).ToArray() };
        // No credentials in proof evidence.
        string temp = Path.Combine(scratch, "state.tmp");
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state)); File.Move(temp, Path.Combine(scratch, "state.json"), true);
        await Task.Delay(50);
    }
}
finally { if (!proxy.HasExited) { proxy.Kill(); await proxy.WaitForExitAsync(); } }

sealed class ProofLobby(string scratch, string signingPath) : WebApplicationFactory<ServerRegistry>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
        => builder.UseContentRoot(Path.GetFullPath("src/Allegiance.Lobby")).ConfigureServices(services =>
        {
            services.RemoveAll<IdentitySettings>();
            services.AddSingleton(new IdentitySettings
            {
                PublicUrl = "http://localhost:8048",
                ConnectionString = "Data Source=" + Path.Combine(scratch, "accounts.db"),
                SigningKeyFile = signingPath,
                PublicKeyFile = Path.Combine(scratch, "public.pem"),
            });
        });
}

// This local Linux proof uses the production signer without persisting a private
// test key. Only the public verification key is written to the ignored fixture.
sealed class MemorySigningMaterial(FileStream stream, string path) : IDisposable
{
    public string Path { get; } = path;
    [DllImport("libc", SetLastError = true)]
    private static extern int memfd_create(string name, uint flags);
    public static MemorySigningMaterial Create()
    {
        int descriptor = memfd_create("p1051-signing", 1); // MFD_CLOEXEC
        if (descriptor < 0) throw new IOException("Unable to create in-memory signing fixture", Marshal.GetLastPInvokeError());
        var file = new FileStream(new SafeFileHandle((nint)descriptor, ownsHandle: true), FileAccess.ReadWrite);
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] pem = System.Text.Encoding.ASCII.GetBytes(key.ExportPkcs8PrivateKeyPem());
            try { file.Write(pem); file.Flush(); file.Position = 0; }
            finally { CryptographicOperations.ZeroMemory(pem); }
            return new(file, "/proc/self/fd/" + descriptor);
        }
        catch { file.Dispose(); throw; }
    }
    public void Dispose() => stream.Dispose();
}
