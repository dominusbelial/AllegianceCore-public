// P894 end-to-end proof: a running lobby + a running server + offscreen clients.
//
//   tools/p894-e2e-proof/bin/.../p894-e2e-proof --lobby http://127.0.0.1:5147 \
//       --server 127.0.0.1:26073 --minimum 2.0.0
//
// It uses the PRODUCTION client model and wire session (no test-only stand-ins):
//   1. list    — GET /api/servers through Allegiance.ClientCore.MonoGame.ServerListing,
//                plus /api/servers/cores and /api/servers/stats (P894 LOB-0009/0018/0029/0027);
//   2. join    — a ConnectionSession logs on with the required client version and is
//                seated (World.MyShipId assigned);
//   3. refused — a second ConnectionSession reports an OLD version and must be refused
//                with the update reason and never seated (P894 LOB-0020/LOB-0026).
using System.Net.Http.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Wire;

string lobby = Arg("--lobby") ?? "http://127.0.0.1:5147";
string serverArg = Arg("--server") ?? "127.0.0.1:26073";
string minimum = Arg("--minimum") ?? "2.0.0";
int timeoutSeconds = int.TryParse(Arg("--timeout"), out var t) ? t : 60;

string[] hostPort = serverArg.Split(':');
string host = hostPort[0];
ushort port = ushort.Parse(hostPort[1]);

using var http = new HttpClient { BaseAddress = new Uri(lobby), Timeout = TimeSpan.FromSeconds(5) };
int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"[p894-e2e] {(ok ? "PASS" : "FAIL")} {what}");
    if (!ok) failures++;
}

// ── 1. the listing, exactly as the client model reads it ────────────────────
List<ServerListing>? servers = null;
var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
while (DateTime.UtcNow < deadline)
{
    try { servers = await http.GetFromJsonAsync<List<ServerListing>>("/api/servers", ServerListing.JsonOptions); }
    catch (Exception e) { Console.WriteLine($"[p894-e2e] lobby not ready: {e.Message}"); }
    if (servers is { Count: > 0 }) break;
    await Task.Delay(1000);
}
servers ??= [];
Check(servers.Count > 0, $"the server is listed by the lobby ({servers.Count} listing(s))");
var mine = servers.FirstOrDefault(s => s.Address == host && s.Port == port);
Check(mine is not null, $"the listing names {host}:{port}");
if (mine is not null)
{
    Console.WriteLine($"[p894-e2e] row: {mine.FormatRow()}");
    Check(!string.IsNullOrWhiteSpace(mine.Location), $"location advertised: '{mine.Location}'");
    Check(mine.MaxGames > 0, $"game capacity advertised: {mine.CurrentGames}/{mine.MaxGames}, load {mine.PercentLoad}%");
    Check(mine.MinClientVersion == minimum, $"minimum client version advertised: '{mine.MinClientVersion}'");
    Check(ServerDirectorySearch.DetailsLines(mine).Any(l => l.StartsWith("LOCATION:", StringComparison.Ordinal)),
        "the client's details panel shows the location");
    // The browse-time gate: this build (0.0.0 by default, or --installed) is too old.
    string installed = Arg("--installed") ?? Allegiance.Update.UpdateState.ResolveInstalledVersion(AppContext.BaseDirectory);
    var (visible, hidden) = ServerListing.FilterForClient(servers, installed);
    Console.WriteLine($"[p894-e2e] browse gate for installed {installed}: {visible.Count} visible, {hidden} hidden");
    Check(hidden >= 1, "an outdated client hides the listing that requires a newer build");
}

var cores = await http.GetFromJsonAsync<LobbyCoreList>("/api/servers/cores", ServerListing.JsonOptions);
Check(cores is { Cores.Count: > 0 }, $"master core list relayed: {string.Join(",", cores?.Cores.Select(c => c.File) ?? [])}");
var stats = await http.GetFromJsonAsync<LobbyStatsDto>("/api/servers/stats", ServerListing.JsonOptions);
Check(stats is { Servers: >= 1 }, $"directory counters: {stats?.Servers} server(s), {stats?.Players} player(s)");

// ── 2. joining with the required version ────────────────────────────────────
static LogonRequest Request(string name, string version) => new()
{
    CharacterName = name,
    ZoneTicket = [0x01, 0x02, 0x03, 0x04],
    CDKey = "ABCDEFGHIJKL",
    MissionPassword = "pw",
    FedsrvVer = LogonRequest.MsgVer,
    Time = 894u,
    DwCookie = uint.MaxValue,
    CrcFileList = -1,
    ClientVersion = version,
    SteamAuthTicket = new byte[16],
    SteamAuthTicketLength = 0,
    SteamID = 89400000000000003ul,
    DrmHash = "p894e2e",
};

using (var good = new ConnectionSession())
{
    good.ClientVersion = minimum;
    good.Connect(port, "P894JOIN", host);
    good.SendLogonRequest(Request("P894JOIN", minimum));
    var ack = good.WaitForAck(8000);
    Check(ack is { FValidated: true }, $"logon with client {minimum} accepted -> '{ack?.CharNameOrFailureReason}'");
    if (ack is { FValidated: true })
    {
        good.SendPositionReq(0);
        var seatDeadline = DateTime.UtcNow.AddSeconds(10);
        while (good.World.MyShipId is null && DateTime.UtcNow < seatDeadline) await Task.Delay(100);
        Check(good.World.MyShipId is not null, $"joined and seated (ship id {good.World.MyShipId})");
    }
}

// ── 3. an OLD client is refused, with the updater pointer ───────────────────
string oldVersion = Arg("--old") ?? "0.0.1";
using (var old = new ConnectionSession())
{
    old.ClientVersion = oldVersion;
    old.Connect(port, "P894OLD", host);
    old.SendLogonRequest(Request("P894OLD", oldVersion));
    var ack = old.WaitForAck(8000);
    Check(ack is { FValidated: false }, $"logon with client {oldVersion} refused");
    string reason = ack?.CharNameOrFailureReason ?? "";
    Console.WriteLine($"[p894-e2e] refusal reason: {reason}");
    Check(reason.Contains(minimum, StringComparison.Ordinal), "the refusal names the required build");
    Check(reason.Contains("update", StringComparison.OrdinalIgnoreCase), "the refusal points at the updater");
    Check(reason.Contains("older", StringComparison.OrdinalIgnoreCase), "the refusal says the client is older");
    old.SendPositionReq(0);
    await Task.Delay(1500);
    Check(old.World.MyShipId is null, "the refused client was never seated");
}

Console.WriteLine(failures == 0 ? "[p894-e2e] ALL CHECKS PASSED" : $"[p894-e2e] {failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

string? Arg(string name)
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == name && i + 1 < args.Length) return args[i + 1];
        if (args[i].StartsWith(name + "=", StringComparison.Ordinal)) return args[i][(name.Length + 1)..];
    }
    return null;
}

/// <summary>P894 — the lobby's counters answer (mirror of the lobby DTO; the client
/// does not otherwise consume it, so it lives here).</summary>
internal sealed record LobbyStatsDto(int Servers, int Missions, int Players, int PercentLoad);
