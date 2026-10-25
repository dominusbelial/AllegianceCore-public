using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Srv = Allegiance.Server.Server;

// P820b — REAL-WINDOW capture of the in-game team pane with a side squad.
//
// A real production Game1 client flies over TCP against a real in-process
// Allegiance.Server (mission forced Started, like the P752/P802 proof idiom).
// The side's squad is established through the SAME internal test seams the
// P820c behavioral tests use (Server.BroadcastTestSeams.cs, InternalsVisibleTo):
// the real client's peer ConnectionId is stamped with a squad-bearing
// JoinIdentity (TestSetIdentity) and EstablishSideSquadFromLeader is invoked
// (TestEstablishSideSquad), which relays a genuine SET_TEAM_INFO(15) to the
// real client. Nothing is seeded into the renderer; the squad on the pane is
// the real ClientWorld value fed from the real wire message.
//
//   dotnet run --project tools/p820b-teampane-proof -- <outDir> <squad|nonsquad>
//
// One run covers BOTH HUD styles (Classic + Modern); the game type selects
// whether the server boots a squad game (GameConfig.SquadGame=true) or a
// plain non-squad game.
string output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/reports/P820B_assets");
string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "squad";
bool squad = mode != "nonsquad";
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
if (!Directory.Exists(content)) content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

Console.WriteLine($"[p820b-teampane] mode={(squad ? "squad" : "nonsquad")} output={output}");

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    SideSelectAutoSeatSeconds = 30,   // P763: proofs never send POSITIONREQ unprompted
    Games = [new GameConfig
    {
        Name = squad ? "P820b squad game" : "P820b plain game", Map = "XXV_simple.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
        SquadGame = squad,            // P820c gate: bSquadGame (igc.h:1142)
    }],
});
// Start the mission so the single real client flies (P752 proof idiom).
server.Missions[0].Stage = MissionStage.Started;
var mission = server.Missions[0];
var side0 = mission.Sides.First(s => s.Id == 0);
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); } });
ticker.Start();

try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P820b SquadPilot",
        WindowWidth = 1280, WindowHeight = 720,
        AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "" },
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new TeamPaneProof(game, server, mission, side0, squad, output));
    game.Run();
}
finally
{
    stop.Cancel(); ticker.Join();
}

Console.WriteLine($"P820B_TEAMPANE_PROOF done ({(squad ? "squad" : "nonsquad")})");
return;

sealed class TeamPaneProof(Game1 game, Srv server, Mission mission, Side side0, bool squad, string output) : GameComponent(game)
{
    private const BindingFlags H = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private bool _finishAfterFlush;
    private ConnectionId _clientConn;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, H)!.GetValue(game)!;
    private void Set(string name, object? value) => typeof(Game1).GetField(name, H)!.SetValue(game, value);

    private static object? CallSrv(object srv, string name, params object?[] args) =>
        srv.GetType().GetMethod(name, H)!.Invoke(srv, args);

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine($"P820B_SCREEN {name}");
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("P820b team-pane proof did not finish");
        if (_finishAfterFlush)
        {
            if (!(bool)typeof(Game1).GetField("_autoShotScheduled", H)!.GetValue(game)!)
                game.Exit();
            return;
        }
        if (Get<object>("_state").ToString() != "Flying" || _watch.Elapsed.TotalSeconds < _next) return;

        switch (_step++)
        {
            case 0:
                // Wait until seated + launched and the client's peer is registered.
                if (!DiscoverClient())
                {
                    _step--;
                    return;
                }
                // Stamp the real client's peer identity with a squad-bearing claim
                // (the P820c test seam; the lobby would mint this into a join token)
                // and establish the side squad from the (now squad-capable) leader.
                if (squad)
                {
                    CallSrv(server, "TestSetIdentity", _clientConn,
                        new JoinIdentity(Iss: "https://lobby.test", Aud: "game", Sub: "p820b-proof-pilot",
                            Callsign: "P820b SquadPilot", Iat: 1700000000, Exp: 1700086400, Jti: "p820b-jti-1",
                            new[] { new SquadClaim("aaaa", "Iron Harbor", true) }));
                    CallSrv(server, "TestEstablishSideSquad", mission, side0);
                    Console.WriteLine("[p820b-teampane] squad established + SET_TEAM_INFO relayed");
                }
                else
                {
                    // Non-squad game: even with a squad identity, the P820c gate
                    // leaves the side squad-less (no SET_TEAM_INFO, no boots).
                    CallSrv(server, "TestSetIdentity", _clientConn,
                        new JoinIdentity(Iss: "https://lobby.test", Aud: "game", Sub: "p820b-proof-pilot",
                            Callsign: "P820b SquadPilot", Iat: 1700000000, Exp: 1700086400, Jti: "p820b-jti-1",
                            new[] { new SquadClaim("aaaa", "Iron Harbor", true) }));
                    CallSrv(server, "TestEstablishSideSquad", mission, side0);
                    Console.WriteLine("[p820b-teampane] non-squad gate: no side squad");
                }
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                break;

            case 1:
                // Wait for the client's world to converge on the relayed squad
                // (squad game) or stay absent (non-squad game).
                if (!ClientConverged()) { _step--; return; }
                Set("_teamPaneVisible", true);
                Get<HudSettings>("_hudSettings").Style = HudStyle.Classic;
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;

            case 2:
                Shot(squad ? "teampane-squad-game-classic-720p" : "teampane-nonsquad-classic-720p");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;

            case 3:
                Get<HudSettings>("_hudSettings").Style = HudStyle.Modern;
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;

            case 4:
                Shot(squad ? "teampane-squad-game-modern-720p" : "teampane-nonsquad-modern-720p");
                _finishAfterFlush = true;
                break;

            default:
                game.Exit();
                return;
        }
    }

    private bool DiscoverClient()
    {
        // The single real network peer is the proof client (SpawnInitialMinersAtBoot
        // false, mission forced Started). Prefer the peer whose ship is the side's
        // leader (first seat on the empty side); else fall back to the sole peer.
        var peers = (System.Collections.IEnumerable)CallSrv(server, "TestSnapshotPeers")!;
        ConnectionId? leaderConn = null;
        int count = 0;
        foreach (var p in peers)
        {
            count++;
            var (conn, ship, _) = ((ConnectionId Conn, Ship Ship, Sector Sector))p;
            if (ship.Id == side0.LeaderShipId) leaderConn = conn;
            if (ship.Id == side0.LeaderShipId || count == 1) _clientConn = conn;
        }
        if (count == 0) return false;
        _clientConn = leaderConn ?? _clientConn;
        Console.WriteLine($"[p820b-teampane] client peer conn={_clientConn.Value} peers={count} leaderShip={side0.LeaderShipId}");
        return true;
    }

    private bool ClientConverged()
    {
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            string? sq = session.World.SideSquadName(side0.Id);
            if (squad) return sq is { Length: > 0 };   // must SHOW the squad
            return sq is null or { Length: 0 };         // must OMIT the squad
        }
    }
}
