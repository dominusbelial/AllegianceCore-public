// P864d — REAL-WINDOW proof of the TEAM LOBBY on the client, both HUD styles:
//
//   * the team screen lists the players who are in the team lobby ("Not on a
//     team") BY NAME, with native's per-player status column
//     (WinTrek/teamscreen.cpp:378-386 team count, :470-510 player rows,
//     clintlib/clintlib.h:571 "Not on a team"),
//   * a player flushed to the lobby requests a side from that screen and lands
//     there (teamscreen.cpp:2962-2982 → FM_C_POSITIONREQ, fsmission.cpp:4729-4774),
//   * a team leader sees the same lobby pool on the ready-check (the port's team
//     lobby) and accepts a waiting player from it
//     (teamscreen.cpp:2998-3013 → FM_C_POSITIONACK accepted).
//
// Topology: a real Allegiance.Server + TWO real wire clients (Allegiance.Bot) +
// the real windowed MonoGame client over loopback. The first bot is seated first,
// so it is side 0's leader AND the mission owner; it sends the real
// FM_C_RANDOMIZE_TEAMS (FedSrv.CPP:6964-6997 → FlushSides, fsmission.cpp:
// 5069-5119) while the mission is STAGE_NOTSTARTED, which strips every
// non-leader (the second bot and the windowed client) into SIDE_TEAMLOBBY. The
// proof then drives the PRODUCTION UpdateSideSelect / UpdateReadyCheck handlers
// with synthetic KeyboardStates (the P752/P802/P853b proof idiom) and reads the
// result back from the SERVER, so every screenshot shows the real state.
//
//   dotnet run --project tools/p864d-teamlobby-proof -- <outDir> <classic|modern>
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using FltBot = Allegiance.Bot.Bot;
using Srv = Allegiance.Server.Server;

string output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/reports/P864d_assets");
string skin = args.Length > 1 ? args[1].ToLowerInvariant() : "modern";
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
if (!Directory.Exists(content)) content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

// Force the requested HUD skin through the production preference loader
// (HudSettings.Load honours ALLEGIANCE_HUD_SETTINGS before player.json).
string hudPath = Path.Combine(output, $".hud-{skin}.json");
// HudSettings' source-generated JSON uses the STRING enum converter and
// PascalCase member names (Rendering/HudSettings.cs HudSettingsJson), so the
// file must say {"Style":"Classic"} — {"style":0} fails to bind, the loader
// swallows the JsonException and falls back to the fresh-profile default
// (Modern), which would silently run both "styles" as Modern.
File.WriteAllText(hudPath, JsonSerializer.Serialize(
    new { Style = skin == "classic" ? "Classic" : "Modern", Size = 1.0 }));
Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", hudPath);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new global::Allegiance.Server.ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    // Native behaviour: a peer that never picks a side stays pending.
    SideSelectAutoSeatSeconds = null,
    Games = [new global::Allegiance.Server.GameConfig
    {
        Name = "P864d team lobby", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
        SideNames = ["Alpha", "Bravo"],
        // FM_C_RANDOMIZE_TEAMS is STAGE_NOTSTARTED-only (FedSrv.CPP:6971-6973),
        // so the mission is held in the lobby for the whole run.
        AutoStart = false,
    }],
});
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); } });
ticker.Start();

// Two REAL wire clients, seated BEFORE the windowed client so the first one is
// side 0's leader and the mission owner.
FltBot? owner = null, lobbyPlayer = null;
try
{
    owner = new FltBot();
    lobbyPlayer = new FltBot();
    short ownerShip = Seat(server, owner, "P864D-Owner", side: 0, acceptAutoSeat: true);
    short lobbyShip = Seat(server, lobbyPlayer, "P864D-Lobby", side: 0, acceptAutoSeat: true);
    Console.WriteLine($"[p864d] wire clients seated: owner ship {ownerShip} (leader/owner), lobby ship {lobbyShip}");

    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P864D-Pilot",
        WindowWidth = 1280, WindowHeight = 720,
        // Seat on side 0 and hold: AutoReady stays false and the mission is
        // AutoStart=false, so the team lobby (ready-check) stays up.
        AutoProof = new AutoProofPlan { AutoSide = 0 },
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new P864dTeamLobbyProof(game, output, skin, server, owner, lobbyPlayer, ownerShip, lobbyShip));
    game.Run();
}
finally
{
    owner?.Dispose();
    lobbyPlayer?.Dispose();
    stop.Cancel(); ticker.Join();
    try { File.Delete(hudPath); } catch { /* best effort */ }
}
Console.WriteLine("P864D_TEAMLOBBY_PROOF done");
return;

// ── helpers ──────────────────────────────────────────────────────────────────

static LogonRequest Logon(string name) => new()
{
    CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
    CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
    FedsrvVer = LogonRequest.MsgVer, Time = 864u, DwCookie = uint.MaxValue,
    CrcFileList = -1, SteamAuthTicket = new byte[16],
    SteamAuthTicketLength = 0, SteamID = 8640000000000000000ul, DrmHash = "p864d",
};

static void PumpUntil(Srv server, Func<bool> done, int max)
{
    int n = 0;
    while (!done() && n < max) { Thread.Sleep(5); n++; }
    if (n >= max) throw new TimeoutException("P864d proof: server state never arrived");
}

static short Seat(Srv server, FltBot bot, string name, short side, bool acceptAutoSeat)
{
    bot.AutoSelectSideAfterLogon = false;
    bot.Connect(server.BoundPort, name);
    bot.SendLogonRequest(Logon(name));
    PumpUntil(server, () => bot.SideRosters.Count > 0, 4000);
    // The side already has a leader (the owner), so a request would PEND; the
    // leader's auto-accept (native AUTO_ACCEPT(52), teamscreen.cpp:2795-2815)
    // seats the joiner immediately, which is what an operator sees.
    if (acceptAutoSeat) bot.SendAutoAccept(side, true);
    Thread.Sleep(250);
    bot.SendPositionReq(side);
    PumpUntil(server, () => bot.ShipAssignment is not null, 8000);
    return bot.ShipAssignment!.Ship.ShipId;
}

internal static class Refl
{
    public const BindingFlags H = BindingFlags.Instance | BindingFlags.NonPublic;
    public static T Get<T>(object o, string name) => (T)typeof(Game1).GetField(name, H)!.GetValue(o)!;
    public static void Set(object o, string name, object? v) => typeof(Game1).GetField(name, H)!.SetValue(o, v);
    public static object? Call(object o, string name, params object?[] args) =>
        typeof(Game1).GetMethod(name, H | BindingFlags.Static)!.Invoke(o, args);
}

/// <summary>Drives the production handlers step by step:
/// flush → the client's team screen lists the lobby by name → request a side →
/// land → leader sees the pool again and accepts the waiting player.</summary>
internal sealed class P864dTeamLobbyProof(
    Game1 game, string output, string skin, Srv server,
    FltBot owner, FltBot lobbyPlayer, short ownerShip, short lobbyShip) : GameComponent(game)
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private bool _shotPending;
    private short _myShip = -1;
    private short _requestedSide = -1;

    private void Shot(string name)
    {
        Refl.Set(game, "_autoshotDir", output);
        Refl.Set(game, "_autoShotName", $"p864d-{name}-{skin}");
        Refl.Set(game, "_autoShotScheduled", true);
        _shotPending = true;
        Console.WriteLine($"P864D_SCREEN p864d-{name}-{skin}");
    }

    /// <summary>Press through the production handler with a real key edge.</summary>
    private void PressSideSelect(params Keys[] keys)
    {
        Refl.Call(game, "UpdateSideSelect", new KeyboardState(keys));
        Refl.Call(game, "UpdateSideSelect", default(KeyboardState));   // release the edge
    }

    private void PressReadyCheck(params Keys[] keys)
    {
        Refl.Call(game, "UpdateReadyCheck", new KeyboardState(keys));
        Refl.Call(game, "UpdateReadyCheck", default(KeyboardState));
    }

    private double _lastDiag = double.NegativeInfinity;
    private void Diag(string message)
    {
        if (_watch.Elapsed.TotalSeconds - _lastDiag < 1.0) return;
        _lastDiag = _watch.Elapsed.TotalSeconds;
        Console.WriteLine("[p864d] " + message);
    }

    private ConnectionSession Session => Refl.Get<ConnectionSession>(game, "_session");
    private string State => Refl.Get<object>(game, "_state").ToString() ?? "?";

    /// <summary>The AUTHORITATIVE side from the server's mission model (the second
    /// vantage point next to the client's own world).</summary>
    private short ServerSideOf(short shipId)
    {
        foreach (var sector in server.Missions[0].Sectors)
            foreach (var model in sector.Models)
                if (model is Ship ship && ship.Id == shipId) return ship.SideId ?? -99;
        return -99;
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 240) throw new TimeoutException("P864d proof did not finish");
        if (_shotPending)
        {
            if (!Refl.Get<bool>(game, "_autoShotScheduled")) _shotPending = false;
            return;
        }
        if (_watch.Elapsed.TotalSeconds < _next) return;

        var session = Session;
        var world = session.World;

        switch (_step++)
        {
            case 0:
            {
                // The windowed client must be seated on side 0 as a plain member
                // (the bot owns side 0 and the mission), mission NotStarted.
                lock (session.Gate)
                {
                    if (State != "ReadyCheck" || world.MyShipId is not { } me || world.MySideId != 0
                        || world.Mission is not { Stage: (int)MissionStage.NotStarted })
                    { _step--; return; }
                    _myShip = me;
                }
                var hud = Refl.Get<Allegiance.ClientCore.MonoGame.Rendering.HudSettings>(game, "_hudSettings");
                Console.WriteLine($"[p864d] windowed client seated: ship {_myShip} side 0, state {State}, HUD style {hud.Style} (requested '{skin}')");
                if (!skin.Equals(hud.Style.ToString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"P864d proof: requested HUD style '{skin}' but the client loaded '{hud.Style}'");
                Console.WriteLine($"[p864d] server: owner ship {ownerShip} side {ServerSideOf(ownerShip)}, lobby ship {lobbyShip} side {ServerSideOf(lobbyShip)}");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            }
            case 1:
            {
                // The owner flushes the team (real FM_C_RANDOMIZE_TEAMS on the
                // wire): every non-leader lands on SIDE_TEAMLOBBY.
                Console.WriteLine("[p864d] owner sends RANDOMIZE_TEAMS (FlushSides: non-leaders -> team lobby)");
                owner.SendRandomizeTeams();
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            }
            case 2:
            {
                // Both the windowed client and the second bot are in the lobby,
                // and the client is on the TEAM SCREEN (side-select overlay).
                if (ServerSideOf(_myShip) != -2) { _step--; return; }
                if (WorldSide(world) != -2 || State != "SideSelect") { _step--; return; }
                if (LobbyNames(world).Count < 2) { _step--; return; }
                Console.WriteLine($"[p864d] team lobby now holds: {string.Join(", ", LobbyNames(world))} (server ships {_myShip}->{ServerSideOf(_myShip)}, {lobbyShip}->{ServerSideOf(lobbyShip)})");
                Shot("lobby-list");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                break;
            }
            case 3:
                // Pick the other side (Down moves the picker off Alpha) and
                // confirm: the production POSITIONREQ path.
                // Request the OTHER side (Alpha is side 0 and still held by the
                // owner/leader, so a request there would pend); Bravo is empty
                // and leaderless, so native auto-accepts it
                // (fsmission.cpp:4757).
                _requestedSide = 1;
                PressSideSelect(Keys.Down);
                _next = _watch.Elapsed.TotalSeconds + 0.4;
                break;
            case 4:
                PressSideSelect(Keys.Enter);
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                break;
            case 5:
            {
                // It landed there: the server has it on the requested side and the
                // client is back on the team lobby (ready-check) for that side.
                short serverSide = ServerSideOf(_myShip);
                Diag($"landing: server side {serverSide}, client state {State}, MySideId {world.MySideId?.ToString() ?? "null"}, MyShipId {world.MyShipId?.ToString() ?? "null"}");
                if (serverSide != _requestedSide) { _step--; return; }
                short worldSide = WorldSide(world);
                string sideName = SideName(world, worldSide);
                if (worldSide != _requestedSide || State != "ReadyCheck") { _step--; return; }
                Console.WriteLine($"[p864d] side request landed: ship {_myShip} on side {serverSide} ('{sideName}') — state {State}, leader={IsLeader(world)}");
                Shot("after-request");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                break;
            }
            case 6:
            {
                // The remaining lobby player asks to join OUR side: it pends on the
                // leader's answer (this client is that side's leader now).
                Console.WriteLine($"[p864d] lobby player {lobbyShip} requests side {_requestedSide}");
                lobbyPlayer.SendPositionReq(_requestedSide);
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            }
            case 7:
            {
                if (!HasPendingRequest(world, _requestedSide)) { _step--; return; }
                Console.WriteLine($"[p864d] leader sees {LobbyNames(world).Count} waiting player(s): {string.Join(", ", LobbyNames(world))}");
                Shot("leader-lobby");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                break;
            }
            case 8:
                // The leader's Accept button (native OnButtonAccept → POSITIONACK).
                PressReadyCheck(Keys.A);
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 9:
            {
                short serverSide = ServerSideOf(lobbyShip);
                if (serverSide != _requestedSide) { _step--; return; }
                Console.WriteLine($"[p864d] accepted: lobby player {lobbyShip} seated on side {serverSide}; team lobby now {LobbyNames(world).Count} waiting");
                Shot("leader-accepted");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                break;
            }
            default:
                if (!_shotPending) game.Exit();
                return;
        }
    }

    private static short WorldSide(ClientWorld world) => world.MySideId ?? -99;

    private static List<string> LobbyNames(ClientWorld world) =>
        world.TeamLobbyMembers().Select(m => m.Name + (m.IsSelf ? "(you)" : "")).ToList();

    private static bool HasPendingRequest(ClientWorld world, short side) =>
        world.PendingPositionRequests.Any(r => r.SideId == side);

    private bool IsLeader(ClientWorld world) => world.IsTeamLeader;

    private static string SideName(ClientWorld world, short side) =>
        world.SideRoster?.Sides.FirstOrDefault(s => s.SideId == side).Name ?? $"side {side}";
}
