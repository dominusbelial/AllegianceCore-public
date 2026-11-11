using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Srv = Allegiance.Server.Server;
using XnaVector3 = Microsoft.Xna.Framework.Vector3;
using CollisionVector3 = Allegiance.Core.Collision.Vector3;

// P996 real-client UI proof: the docked hangar AFTER A POD DEATH, in Classic and
// Modern. Drives the real Game1 + a real loopback server over TCP; no replacement
// renderer, no seeded rows, no screenshot composition.
//
//   p996-ui-proof <output-directory> <classic|modern>
//
// It launches a real hull, gets the player podded and the pod killed (the rig's
// timeline), and then captures the docked screens. The run FAILS (exit 1) unless
// the hangar's shop model really restored the last real ship — so the screenshots
// cannot paper over a broken restore.
string output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/reports/P996_assets");
string style = args.Length > 1 ? args[1] : "modern";
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent) && Directory.Exists(content))
    Directory.CreateSymbolicLink(localContent, content);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    SideSelectAutoSeatSeconds = 30,
    // Test-only server config (same fixture the P699 pod proofs use): all tech +
    // a real wallet so the hangar purchase goes through the real debit path.
    LiveSettings = new() { TestUnlock = new() { Enabled = true, Money = 1_000_000 } },
    Games = [new() { Name = "P996 UI proof", Map = "XXV_simple.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], MaxTeamImbalance = 0x7fff }],
});
server.Missions[0].Stage = MissionStage.Started;
using var game = new Game1(new Game1.LaunchOptions
{
    DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P996 proof pilot",
    WindowWidth = 1280, WindowHeight = 720,
    AutoProof = new AutoProofPlan
    {
        AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "Adv Fighter",
    },
});
game.Content.RootDirectory = content;
object simGate = new();
using var stop = new CancellationTokenSource();
var ticker = new Thread(() =>
{
    while (!stop.IsCancellationRequested)
    {
        lock (simGate) server.TickSim();
        Thread.Sleep(20);
    }
}) { IsBackground = true };
ticker.Start();
bool ok = false;
var proof = new Proof(game, server, simGate, output, style, () => ok = true);
game.Components.Add(proof);
try { game.Run(); }
finally
{
    stop.Cancel();
    ticker.Join();
    Directory.CreateDirectory(output);
    File.WriteAllText(Path.Combine(output, $"proof-{style}.json"),
        JsonSerializer.Serialize(proof.Result, ProofJsonContext.Default.ProofResult));
    Console.WriteLine($"P996_UI_{(ok ? "PASS" : "FAIL")} {style}: {JsonSerializer.Serialize(proof.Result, ProofJsonContext.Default.ProofResult)}");
}
return ok ? 0 : 1;

sealed class Proof(Game1 game, Srv server, object simGate, string output, string style, Action pass) : Microsoft.Xna.Framework.GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _step;
    private double _next;
    private ProofResult _result = new(style, null, null, null, 0, null);

    public ProofResult Result => _result;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine($"P996_SCREEN {name}");
    }

    public override void Update(Microsoft.Xna.Framework.GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 240) throw new TimeoutException("P996 real-client proof timed out");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        Get<HudSettings>("_hudSettings").Style = style == "classic" ? HudStyle.Classic : HudStyle.Modern;
        lock (simGate) Advance();
    }

    private void Advance()
    {
        var session = Get<ConnectionSession?>("_session");
        if (session is null) return;
        switch (_step)
        {
            case 0:
            {
                // Fly the real hull the auto-proof bought, then reproduce the rig:
                // pod the ship, then kill the pod → the server respawns the player
                // DOCKED as a Lifepod (FedSrv.CPP:12030-12065).
                if (!Get<bool>("_launchSent")) { _next = 0; return; }
                if (Get<bool>("_dockedActive")) { _next = 0; return; }   // still docked: launch not done
                if (session.World.MyShipId is not { } myId) return;
                var ship = server.Missions.Select(m => m.GetShip(myId)).FirstOrDefault(x => x is not null);
                if (ship is null) { _next = 0; return; }
                if (ship.StationId is not null) { _next = 0; return; }
                _result = _result with { LaunchedHull = ship.HullType.HullId };
                Console.WriteLine($"[p996-proof] launched hull {ship.HullType.HullId} '{ship.HullType.Name}' — podding it now");
                ship.ReceiveDamage(0, 1_000_000, ship.Mission!.LastUpdate, ship.Position, CollisionVector3.Zero, null);
                Console.WriteLine($"[p996-proof] ejected -> pod hull {ship.HullType.HullId}, station={ship.StationId}");
                ship.ReceiveDamage(0, 1_000_000, ship.Mission!.LastUpdate, ship.Position, CollisionVector3.Zero, null);
                Console.WriteLine($"[p996-proof] pod killed -> docked pod hull {ship.HullType.HullId}, station={ship.StationId}, deadline={ship.LifepodTimeToDie?.ToString() ?? "none"}");
                _result = _result with { PodHull = ship.HullType.HullId, PodDeadline = ship.LifepodTimeToDie };
                _step = 1;
                _next = _watch.Elapsed.TotalSeconds + 1.5;
                return;
            }
            case 1:
            {
                // The docked HANGAR (native vmHangar, P945) with the restored ship.
                if (!Get<bool>("_dockedActive") || Get<ShopModel?>("_shopModel") is not { } shop) { _next = 0; return; }
                bool podDocked;
                lock (session.Gate) podDocked = session.World.OwnShipDockedInLifepod;
                if (!podDocked) { _next = 0; return; }
                _result = _result with
                {
                    HangarSelection = shop.EffectiveHullId,
                    HangarParts = shop.ChosenParts.Count,
                };
                Console.WriteLine($"[p996-proof] docked in pod hull: hangar selection = {shop.EffectiveHullId} "
                    + $"({shop.EffectiveHull?.Name ?? "?"}) + {shop.ChosenParts.Count} part(s)");
                Shot($"hangar-after-pod-death-{style}");
                _step = 2;
                _next = _watch.Elapsed.TotalSeconds + 1.5;
                return;
            }
            case 2:
            {
                // The LOADOUT screen a player clicks through to (native
                // vmLoadout): the same restored hull + parts, per HUD style.
                typeof(Game1).GetMethod("ActivateHangar", Hidden)!.Invoke(game, [HangarAction.Loadout]);
                _step = 3;
                _next = _watch.Elapsed.TotalSeconds + 1.5;
                return;
            }
            case 3:
                Shot($"loadout-after-pod-death-{style}");
                _step = 4;
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                return;
            default:
            {
                // The proof is only a proof if the hangar really restored a real ship.
                bool restored = _result.HangarSelection is { } sel && _result.PodHull is { } pod && sel != pod
                    && _result.HangarParts > 0;
                if (!restored)
                    Console.WriteLine($"[p996-proof] FAIL: hangar selection {_result.HangarSelection} vs pod {_result.PodHull} ({_result.HangarParts} part(s))");
                else
                    pass();
                game.Exit();
                return;
            }
        }
    }
}

internal sealed record ProofResult(string Style, short? LaunchedHull, short? PodHull, short? HangarSelection,
    int HangarParts, double? PodDeadline);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ProofResult))]
internal sealed partial class ProofJsonContext : JsonSerializerContext;
