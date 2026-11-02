using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Srv = Allegiance.Server.Server;
using SimVec = Allegiance.Core.Collision.Vector3;

// Real Game1 flight window, with a deterministic client telemetry fixture after
// loopback logon. Usage: output [--no-pack], or --audit output.json

if (args[0] == "--audit") { MaterialAudit.Run(args[1]); return; }
string output = Path.GetFullPath(args[0]);
bool noPack = args.Contains("--no-pack");
if (noPack) Environment.SetEnvironmentVariable(ExternalPaths.HdPackEnvVar, Path.Combine(output, "absent-pack"));
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);
using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Require(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    Games = [new() { Name = "P922 rendering proof", Map = "XXV_simple.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }]
});
server.Missions[0].Stage = MissionStage.Started;
using var game = new Game1(new Game1.LaunchOptions
{
    DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P922 proof",
    WindowWidth = 1280, WindowHeight = 720, OwnShipPrediction = false,
    EffectsRequested = EffectsQuality.Enhanced, EffectsDetail = EffectsDetail.Medium,
    AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true }
});
game.Content.RootDirectory = content;
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(50); } });
ticker.Start();
game.Components.Add(new Proof(game, server.Missions[0].Core, output, noPack, () => { stop.Cancel(); ticker.Join(); }));
try { game.Run(); } finally { stop.Cancel(); ticker.Join(); }

