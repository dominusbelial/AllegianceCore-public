// Local proof only. Real lobby application, enrollment, identity and authoritative game.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
using Allegiance.Lobby.BrowserPreview;
using Allegiance.Lobby.Identity;
using Allegiance.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;

var options = BattleOptions.Parse(args);
string art = Path.GetFullPath(options.Artwork), assets = Path.GetFullPath(options.Assets), scratch = Path.GetFullPath(options.Scratch);
Directory.CreateDirectory(scratch);
CombatProof.ObserveLog(scratch);
string brokerDirectory = Path.Combine(scratch, "identity-broker");
Directory.CreateDirectory(brokerDirectory);
File.Delete(Path.Combine(scratch, "battle-ready"));
await using var battleBots = new BattleBots(scratch);
long finishAt = options.Seconds == 0 ? long.MaxValue : Environment.TickCount64 + options.Seconds * 1000L;
bool stopping = false;
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping = true; };
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal => { signal.Cancel = true; stopping = true; });
using var freePort = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
freePort.Start(); int tcpPort = ((System.Net.IPEndPoint)freePort.LocalEndpoint).Port; freePort.Stop();
File.Delete(Path.Combine(scratch, "stop"));
// One proof origin, exactly the production shape: HTTPS on loopback terminated by a
// local proxy, lobby and game listener behind it on their own loopback ports.
const string Origin = "https://localhost:8443";
const string ClientVersion = "2.0.0";
const string GamePassword = "p1061-proof-password";
var (certPath, keyPath) = WriteCertificate(scratch);
File.WriteAllText(Path.Combine(scratch, "game-password"), GamePassword);
File.WriteAllText(Path.Combine(scratch, "client-version"), ClientVersion);
// Keep test private key material in a Linux anonymous memory file. The actual
// production signer imports it through its ordinary path-based constructor.
using var signingMaterial = MemorySigningMaterial.Create();
using var lobby = new ProofLobby(scratch, signingMaterial.Path, assets, art);
lobby.UseKestrel(8049);
using var http = lobby.CreateClient(); // starts the actual app with unchanged admission/CSRF/limits
var lobbyHttp = lobby.CreateClient();
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
    ArtPath = art, StaticCore = "PCore001.igc", Port = (ushort)tcpPort, BindAddress = "127.0.0.1", UpdatesPerSecond = 20,
    // P1061 production admission: one trusted TLS proxy hop, exact public Origin,
    // no plaintext loopback exception, enforced minimum client version.
    MinimumClientVersion = ClientVersion,
    Discovery = new() { Enabled = false },
    WebSocket = new() { Enabled = true, Port = 0, BindAddress = "127.0.0.1", AllowedOrigins = [Origin],
        KnownProxies = ["127.0.0.1"], AllowLoopbackDevelopment = false, PublicUrl = "wss://localhost:8443/game" },
    LiveSettings = options.Bots == 0 || options.MapGen is not null ? new() : new()
    {
        DebugMode = true,
        TestHarness = new() { Enabled = true, CharacterNamePrefix = "mcp-", Money = 500000, IgnoreTechGate = true },
        TestUnlock = new() { Enabled = true, AllTech = true, Money = 500000 },
        AdminCheats = new() { Enabled = true, AllowNames = ["mcp-*"] },
    },
    Reconnect = new() { Enabled = true, GraceSeconds = 10 },
    Identity = new() { Issuer = identitySettings.PublicUrl, Audience = approved.ServerId, PublicKeyFile = identitySettings.PublicKeyFile, AllowAnonymous = options.Guests },
    Lobby = new() { Url = "http://127.0.0.1:8049", PublicAddress = "127.0.0.1", DisplayName = "P1061 loopback", EnrollmentFile = enrollment },
    // P1073 — an optional generated-map + showcase-research match (the live
    // P1071 preset shape). Default stays the static XXV_simple proof.
    Showcase = new() { TechMode = options.MapGen is null ? "unlocked" : "research" },
    Games = [new() { Name = options.MapGen is null ? "P1061 public mission" : "P1073 generated " + options.MapGen,
        Map = options.MapGen is null ? "XXV_simple.igc" : "", MapGen = options.MapGen is null ? null : new() { Type = options.MapGen, Seed = options.MapSeed },
        Teams = 2, CivIds = options.CivIds ?? [1, 3], CivSeed = 1051, SideNames = ["Alpha", "Bravo"],
        Password = GamePassword, AutoStart = true, AllowEmptyTeams = true, StartCountdownSeconds = 0 }],
});
using var proxy = Process.Start(new ProcessStartInfo("node") { UseShellExecute = false,
    ArgumentList = { "tools/p1051-loopback-proxy.cjs", certPath, keyPath, "8049", server.WebSocketPort.ToString(), scratch } })!;
using var desktop = new ConnectionSession();
using var keeper = new ConnectionSession();
var desktopAccount = identities.CreateAccount("DesktopPilot");
var keeperAccount = identities.CreateAccount("KeeperPilot");
var signing = lobby.Services.GetRequiredService<IdentitySigningKey>();
var botAccounts = new Dictionary<string, IdentityAccount>(StringComparer.Ordinal);
void AnswerBotIdentityRequests()
{
    foreach (string request in Directory.EnumerateFiles(brokerDirectory, "*.request"))
    {
        using var document = JsonDocument.Parse(File.ReadAllText(request));
        string name = document.RootElement.GetProperty("name").GetString()!;
        if (!name.StartsWith("mcp-", StringComparison.Ordinal) && !name.StartsWith("research-cmd", StringComparison.Ordinal)) throw new InvalidDataException("Only proof bot names may use this broker.");
        if (!botAccounts.TryGetValue(name, out var botAccount))
        {
            string callsign = name.Length <= 24 ? name : name[..15] + "-" + Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)))[..8];
            botAccounts[name] = botAccount = identities.CreateAccount(callsign);
        }
        string token = signing.Issue(botAccount, approved.ServerId, DateTimeOffset.UtcNow);
        string reply = Path.ChangeExtension(request, ".reply");
        File.WriteAllText(reply + ".tmp", JsonSerializer.Serialize(new { Callsign = botAccount.Callsign, Token = token }));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(reply + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(reply + ".tmp", reply, true); File.Delete(request);
    }
}
var core = StaticCore.Load(IgcReader.Parse(File.ReadAllBytes(Path.Combine(art, "PCore001.igc"))));
desktop.World.MissionCoreResolver = _ => core;
keeper.World.MissionCoreResolver = _ => core;

// Desktop joins the exact lobby listing (address, port, advertised route), with
// the same build identity and the game password the browser types.
async Task<JsonElement> WaitListingAsync()
{
    var until = Environment.TickCount64 + 30000;
    while (Environment.TickCount64 < until)
    {
        using var document = JsonDocument.Parse(await lobbyHttp.GetStringAsync("/api/servers"));
        foreach (var entry in document.RootElement.EnumerateArray())
            if (entry.TryGetProperty("identityServerId", out var id) && id.GetString() == approved.ServerId)
                return entry.Clone();
        await Task.Delay(100);
    }
    throw new InvalidOperationException("The server did not register with the lobby.");
}
var listing = await WaitListingAsync();
var listingInfo = new { id = listing.GetProperty("id").GetString(), address = listing.GetProperty("address").GetString(),
    port = listing.GetProperty("port").GetInt32(), webSocketUrl = listing.TryGetProperty("webSocketUrl", out var webRoute) ? webRoute.GetString() : null,
    passwordRequired = listing.TryGetProperty("passwordRequired", out var lockFlag) && lockFlag.GetBoolean(),
    minClientVersion = listing.TryGetProperty("minClientVersion", out var minimum) ? minimum.GetString() : null,
    acceptsGuests = listing.TryGetProperty("acceptsGuests", out var guestFlag) && guestFlag.GetBoolean(),
    verified = listing.GetProperty("verified").GetBoolean() };
// Fail-closed version gate, proved on the real listener with the production
// signer. The attempt starts before the loop; the server's own tick has to drain
// the LOGONREQ, so the loop ticks while this task waits for its refusal.
string? oldVersionRefusal = null;
var stale = new ConnectionSession();
var staleJoin = stale.JoinAsync((ushort)listingInfo.port, desktopAccount.Callsign,
    new LogonRequest { CharacterName = desktopAccount.Callsign, FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1,
        DwCookie = uint.MaxValue, Time = 1, ClientVersion = "0.0.0", MissionPassword = GamePassword,
        ZoneTicket = System.Text.Encoding.UTF8.GetBytes(signing.Issue(desktopAccount, approved.ServerId, DateTimeOffset.UtcNow)) },
    host: listingInfo.address!);
var navigationWarps = server.Missions[0].Sectors.SelectMany(s => s.Warps).Select(w => w.Warp).ToDictionary(w => w.WarpId);
var chart = navigationWarps.Values.Select(w => new { w.WarpId, w.ClusterId, w.Name, w.Position,
    destinationSectorId = navigationWarps[w.DestinationId].ClusterId }).ToArray();
Task? desktopJoin = null;
Task? keeperJoin = null;
string battleSector = options.MapGen is null ? "Ixion"
    : server.Missions[0].Sectors.First(s => s.Stations.Count == 0).Name;
if (options.Bots > 0)
{
    var mission = server.Missions[0];
    short home = mission.Sectors.First(s => s.Stations.Any(st => st.SideId == 0)).Id;
    short target = mission.Sectors.Single(c => c.Name == battleSector).Id;
    if (!navigationWarps.Values.Any(w => w.ClusterId == home && navigationWarps[w.DestinationId].ClusterId == target))
        throw new InvalidDataException("Battle rendezvous must be one aleph from home.");
    battleBots.Start(options.Bots, tcpPort, ClientVersion, GamePassword, brokerDirectory, battleSector);
}
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
    while (!stopping && Environment.TickCount64 < finishAt && !File.Exists(Path.Combine(scratch, "stop")))
    {
        server.TickSim();
        LateStationProof.Poll(server, scratch);
        CombatProof.Poll(server, scratch);
        if (options.Bots > 0) AnswerBotIdentityRequests();
        if (battleBots.Error is { } botError) throw new IOException("Battle startup failed: " + botError);
        if (staleJoin.IsCompleted && oldVersionRefusal is null)
            oldVersionRefusal = staleJoin.IsFaulted
                ? staleJoin.Exception!.GetBaseException().Message
                : "ADMITTED (the version gate did not refuse 0.0.0)";
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
            desktop.ClientVersion = ClientVersion;
            desktopJoin = desktop.JoinAsync((ushort)listingInfo.port, desktopAccount.Callsign,
                new LogonRequest { CharacterName = desktopAccount.Callsign, FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1,
                    DwCookie = uint.MaxValue, Time = 1, ClientVersion = ClientVersion, MissionPassword = GamePassword,
                    ZoneTicket = System.Text.Encoding.UTF8.GetBytes(signing.Issue(desktopAccount, approved.ServerId, DateTimeOffset.UtcNow)) },
                host: listingInfo.address!);
        }
        if (desktopJoin?.IsCompletedSuccessfully == true && !desktopSeated)
        { desktop.SendPositionReq(options.Bots > 0 ? (short)0 : (short)1); desktopSeated = true; }
        if (File.Exists(Path.Combine(scratch, "keeper-join")) && keeperJoin is null)
        {
            keeper.ClientVersion = ClientVersion;
            keeperJoin = keeper.JoinAsync(checked((ushort)listingInfo.port), keeperAccount.Callsign,
                new LogonRequest { CharacterName = keeperAccount.Callsign, FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1,
                    DwCookie = uint.MaxValue, Time = 1, ClientVersion = ClientVersion, MissionPassword = GamePassword,
                    ZoneTicket = System.Text.Encoding.UTF8.GetBytes(signing.Issue(keeperAccount, approved.ServerId, DateTimeOffset.UtcNow)) },
                host: listingInfo.address!);
        }
        if (keeperJoin?.IsCompletedSuccessfully == true && !keeperSeated) { keeper.SendPositionReq(0); keeperSeated = true; }
        lock (desktop.Gate)
        {
            if (desktop.World.MySideId == (options.Bots > 0 ? 0 : 1) && desktop.World.MyShipId is { } desktopId
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
                    var goalSector = desktop.World.Clusters.Values.FirstOrDefault(c => c.Name == (options.Bots > 0 ? battleSector : "Ceto")).ClusterId;
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
        object desktopState;
        lock (desktop.Gate) desktopState = new { joined = desktopJoin?.IsCompletedSuccessfully == true, error = desktopJoin?.Exception?.GetBaseException().Message,
            desktop.World.MyShipId, desktop.World.MySideId, desktopLaunched, desktopNavigation, knownClusters = desktop.World.Clusters.Count, knownWarps = desktop.World.Warps.Count, stage = desktop.World.Mission?.Stage, desktopShotLaunchers = desktopShotLaunchers.ToArray(),
            ships = desktop.World.Ships.Values.Select(s => new { s.ShipId, s.CharacterName, s.SectorId, s.StationId, s.HullId, s.PoseSectorId, visible = s.CanDrawInSector(desktop.World.MyShipId is { } ownShip && desktop.World.Ships.TryGetValue(ownShip, out var viewer) ? viewer.SectorId : (short)-1) }).ToArray(), desktopShotsReceived };
        var state = new { civIds = options.CivIds ?? [1, 3], battle = new { size = options.Bots, targetSector = battleSector, ready = battleBots.Ready, error = battleBots.Error, driverPid = battleBots.DriverPid, identityRequired = !options.Guests, accounts = botAccounts.Count }, sampledAt = Environment.TickCount64, poseReceipts, chart, listing = listingInfo, oldVersionRefusal, keeper = new { keeper.World.MyShipId, keeper.World.MySideId }, desktop = desktopState,
            controlHistory, projectiles = server.Missions.SelectMany(m => m.Sectors).Sum(s => s.Models.Count(m => m is Projectile)), players = server.LivePlayerNames, ships = server.Missions.SelectMany(m => m.GetShips()).Select(s => new
            { s.Id, s.Name, s.SideId, s.StationId, hullId = s.HullType.HullId, sectorId = s.Sector?.Id, s.Ammo, weaponCount = s.Weapons.Count, parts = s.Parts.Select(p => new { p.PartId, p.MountId, p.Amount, p.FractionMounted }).ToArray(), yaw = s.Controls.Yaw, pitch = s.Controls.Pitch, roll = s.Controls.Roll, throttle = s.Controls.Throttle, s.StateM,
                forward = new[] { s.Orientation.GetForward().X, s.Orientation.GetForward().Y, s.Orientation.GetForward().Z },
                up = new[] { s.Orientation.GetUp().X, s.Orientation.GetUp().Y, s.Orientation.GetUp().Z },
                speed = s.Velocity.Length(), targetId = s.CommandTarget?.Id,
                position = s.Position.ToString() }).ToArray() };
        // No credentials in proof evidence.
        string temp = Path.Combine(scratch, "state.tmp");
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state)); File.Move(temp, Path.Combine(scratch, "state.json"), true);
        await Task.Delay(50);
    }
}
finally { await battleBots.DisposeAsync(); stale.Dispose(); if (!proxy.HasExited) { proxy.Kill(); await proxy.WaitForExitAsync(); } }

// Proof-only TLS material for the loopback proxy. The certificate is discarded with
// the scratch directory; Chromium ignores this specific self-signed origin.
static (string Cert, string Key) WriteCertificate(string directory)
{
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var names = new SubjectAlternativeNameBuilder();
    names.AddDnsName("localhost"); names.AddIpAddress(System.Net.IPAddress.Loopback);
    request.CertificateExtensions.Add(names.Build());
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], false));
    using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    string certPath = Path.Combine(directory, "tls-cert.pem"), keyPath = Path.Combine(directory, "tls-key.pem");
    File.WriteAllText(certPath, certificate.ExportCertificatePem());
    File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
    return (certPath, keyPath);
}

sealed class ProofLobby(string scratch, string signingPath, string assets, string artwork) : WebApplicationFactory<ServerRegistry>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(Path.GetFullPath("src/Allegiance.Lobby"));
        // The production topology: one trusted proxy hop on loopback. The lobby's
        // ForwardedHeaders options are built from configuration at startup.
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Lobby:KnownProxies:0"] = "127.0.0.1" }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IdentitySettings>();
            services.AddSingleton(new IdentitySettings
            {
                PublicUrl = "https://localhost:8443",
                ConnectionString = "Data Source=" + Path.Combine(scratch, "accounts.db"),
                SigningKeyFile = signingPath,
                PublicKeyFile = Path.Combine(scratch, "public.pem"),
            });
            // The published generation is served by the lobby itself at /play, the
            // production prefix, with the public origin in the CSP.
            services.RemoveAll<BrowserPreviewSettings>();
            services.AddSingleton(new BrowserPreviewSettings
            {
                Enabled = true, Prefix = "/play", BundlePath = assets, ArtworkPath = artwork,
                PublicOrigin = "https://localhost:8443", WebSocketPath = "/game",
            });
        });
    }
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
