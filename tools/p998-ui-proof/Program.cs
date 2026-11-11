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
using CollisionVector3 = Allegiance.Core.Collision.Vector3;

// P998 real-client UI proof: the docked LOADOUT screen AFTER A POD DEATH, in
// Classic and Modern. Drives the real Game1 + a real loopback server over TCP; no
// replacement renderer, no seeded rows, no screenshot composition.
//
//   p998-ui-proof <output-directory> <classic|modern>
//
// It launches a real hull, gets the player podded and the pod killed (the rig's
// timeline), then captures the loadout screen a player reaches from the hangar.
// The run FAILS (exit 1) unless BOTH halves hold:
//   CURRENT SHIP  = the bare pod the server docked (pod hull, ZERO parts) — the
//                   P998 fix (native KillShipEvent terminates every part,
//                   FedSrv.CPP:11686-11696);
//   PENDING       = the restored last real ship with its parts — the P996
//                   restore (native RestoreLoadout, clintlib.h:1876-1886).
// So the capture cannot paper over either a kept loadout or a broken restore.
string output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/reports/P998_assets");
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
    // Test-only server config (same fixture the P699/P996 pod proofs use): all tech
    // + a real wallet so the hangar purchase goes through the real debit path.
    LiveSettings = new() { TestUnlock = new() { Enabled = true, Money = 1_000_000 } },
    Games = [new() { Name = "P998 UI proof", Map = "XXV_simple.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], MaxTeamImbalance = 0x7fff }],
});
server.Missions[0].Stage = MissionStage.Started;
using var game = new Game1(new Game1.LaunchOptions
{
    DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P998 proof pilot",
    // 1920x1080: the docked loadout screen draws its part-info panel to the
    // RIGHT of the row list, sized to the highlighted hull's (long) native
    // description — at 1280x720 that panel is wider than the screen and covers
    // the CURRENT SHIP rows this proof is about.
    WindowWidth = 1920, WindowHeight = 1080,
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
    Console.WriteLine($"P998_UI_{(ok ? "PASS" : "FAIL")} {style}: {JsonSerializer.Serialize(proof.Result, ProofJsonContext.Default.ProofResult)}");
}
return ok ? 0 : 1;

sealed class Proof(Game1 game, Srv server, object simGate, string output, string style, Action pass) : Microsoft.Xna.Framework.GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _step;
    private double _next;
    private ProofResult _result = new(style, null, null, null, null, 0, 0, null);

    public ProofResult Result => _result;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine($"P998_SCREEN {name}");
    }

    public override void Update(Microsoft.Xna.Framework.GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 240) throw new TimeoutException("P998 real-client proof timed out");
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
                // pod the ship, let the pod drift (the flying-pod HUD capture), then
                // kill the pod -> the server respawns the player DOCKED as a Lifepod
                // (FedSrv.CPP:12030-12065).
                if (!Get<bool>("_launchSent")) { _next = 0; return; }
                if (Get<bool>("_dockedActive")) { _next = 0; return; }   // still docked: launch not done
                if (session.World.MyShipId is not { } myId) return;
                var ship = server.Missions.Select(m => m.GetShip(myId)).FirstOrDefault(x => x is not null);
                if (ship is null) { _next = 0; return; }
                if (ship.StationId is not null) { _next = 0; return; }
                _result = _result with { LaunchedHull = ship.HullType.HullId, LaunchedParts = ship.Parts.Count };
                Console.WriteLine($"[p998-proof] launched hull {ship.HullType.HullId} '{ship.HullType.Name}' "
                    + $"with {ship.Parts.Count} part(s) — podding it now");
                ship.ReceiveDamage(0, 1_000_000, ship.Mission!.LastUpdate, ship.Position, CollisionVector3.Zero, null);
                Console.WriteLine($"[p998-proof] ejected -> pod hull {ship.HullType.HullId}, "
                    + $"{ship.Parts.Count} part(s) on the server, station={(ship.StationId?.ToString() ?? "none (drifting)")}");
                _step = 1;
                _next = _watch.Elapsed.TotalSeconds + 1.5;
                return;
            }
            case 1:
            {
                // The FLYING pod HUD: the pod's inventory pane must be empty.
                if (Get<bool>("_dockedActive")) { _next = 0; return; }
                if (session.World.MyShipId is { } id && session.World.Ships.TryGetValue(id, out var me))
                {
                    _result = _result with
                    {
                        ClientPodHull = me.HullId,
                        ClientPodParts = me.Loadout?.Count ?? 0,
                        ClientPodCargo = me.Cargo?.Count ?? 0,
                    };
                    Console.WriteLine($"[p998-proof] flying pod: client hull {me.HullId}, "
                        + $"{me.Loadout?.Count ?? 0} mounted part(s), {me.Cargo?.Count ?? 0} cargo entry/entries");
                }
                // Dismiss the join-time mission briefing so the flight HUD is not
                // covered by a modal panel (native TK_Enter/ESC dismiss, P271).
                Set("_briefingDismissed", true);
                Shot($"pod-hud-{style}");
                _step = 2;
                _next = _watch.Elapsed.TotalSeconds + 1.5;
                return;
            }
            case 2:
            {
                // Kill the pod -> docked Lifepod (native FedSrv.CPP:12030-12065).
                if (session.World.MyShipId is not { } myId) return;
                var ship = server.Missions.Select(m => m.GetShip(myId)).FirstOrDefault(x => x is not null);
                if (ship is null) { _next = 0; return; }
                ship.ReceiveDamage(0, 1_000_000, ship.Mission!.LastUpdate, ship.Position, CollisionVector3.Zero, null);
                Console.WriteLine($"[p998-proof] pod killed -> docked pod hull {ship.HullType.HullId}, "
                    + $"{ship.Parts.Count} part(s), station={ship.StationId}, deadline={ship.LifepodTimeToDie?.ToString() ?? "none"}");
                _result = _result with
                {
                    PodHull = ship.HullType.HullId,
                    ServerPodParts = ship.Parts.Count,
                    PodDeadline = ship.LifepodTimeToDie,
                };
                _step = 3;
                _next = _watch.Elapsed.TotalSeconds + 1.5;
                return;
            }
            case 3:
            {
                // The docked HANGAR (native vmHangar, P945): wait for the shop model
                // the dock transition builds, then click through to the LOADOUT pane.
                if (!Get<bool>("_dockedActive") || Get<ShopModel?>("_shopModel") is not { } shop) { _next = 0; return; }
                bool podDocked;
                lock (session.Gate) podDocked = session.World.OwnShipDockedInLifepod;
                if (!podDocked) { _next = 0; return; }
                // Park the pointer above the list so no row tooltip covers the
                // CURRENT SHIP / PENDING panes in the capture (the docked screen is
                // a free-cursor screen and its hover band maps the real pointer).
                Microsoft.Xna.Framework.Input.Mouse.SetPosition(64, 6);
                Set("_proofMouse", new Microsoft.Xna.Framework.Input.MouseState(
                    game.GraphicsDevice.Viewport.Width / 2, 4, 0,
                    Microsoft.Xna.Framework.Input.ButtonState.Released,
                    Microsoft.Xna.Framework.Input.ButtonState.Released,
                    Microsoft.Xna.Framework.Input.ButtonState.Released,
                    Microsoft.Xna.Framework.Input.ButtonState.Released,
                    Microsoft.Xna.Framework.Input.ButtonState.Released));
                var rows = ShopLayout.Build(shop).Select(r => r.Text).ToArray();
                _result = _result with
                {
                    CurrentHull = shop.CurrentHullId,
                    CurrentParts = shop.CurrentParts.Count,
                    HangarSelection = shop.EffectiveHullId,
                    HangarParts = shop.ChosenParts.Count,
                    LoadoutRows = rows,
                };
                Console.WriteLine($"[p998-proof] docked in pod hull: CURRENT SHIP = {shop.CurrentHullId} with "
                    + $"{shop.CurrentParts.Count} part(s); PENDING = {shop.EffectiveHullId} + {shop.ChosenParts.Count} part(s)");
                typeof(Game1).GetMethod("ActivateHangar", Hidden)!.Invoke(game, [HangarAction.Loadout]);
                _step = 4;
                _next = _watch.Elapsed.TotalSeconds + 1.5;
                return;
            }
            case 4:
                Shot($"loadout-after-pod-death-{style}");
                _step = 5;
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                return;
            default:
            {
                // The proof only counts if the screens show BOTH native outcomes.
                bool podBare = _result.CurrentHull is { } cur && _result.PodHull is { } pod && cur == pod
                    && _result.CurrentParts == 0;
                bool restored = _result.HangarSelection is { } sel && _result.PodHull is { } podHull && sel != podHull
                    && _result.HangarParts > 0;
                bool flyingBare = _result.ClientPodHull is { } flying && _result.PodHull is { } pod2 && flying == pod2
                    && _result.ClientPodParts == 0 && _result.ClientPodCargo == 0;
                if (!podBare)
                    Console.WriteLine($"[p998-proof] FAIL: CURRENT SHIP {_result.CurrentHull} has {_result.CurrentParts} part(s), "
                        + $"pod hull is {_result.PodHull} (must be the bare pod)");
                if (!restored)
                    Console.WriteLine($"[p998-proof] FAIL: PENDING {_result.HangarSelection} + {_result.HangarParts} part(s) (must be the restored real ship)");
                if (!flyingBare)
                    Console.WriteLine($"[p998-proof] FAIL: flying pod {_result.ClientPodHull} had {_result.ClientPodParts} part(s) / "
                        + $"{_result.ClientPodCargo} cargo (must be bare)");
                if (podBare && restored && flyingBare) pass();
                game.Exit();
                return;
            }
        }
    }
}

internal sealed record ProofResult(string Style, short? LaunchedHull, int? LaunchedParts, short? PodHull,
    int? ServerPodParts, int CurrentParts, int HangarParts, double? PodDeadline)
{
    public short? CurrentHull { get; init; }
    public short? HangarSelection { get; init; }
    public short? ClientPodHull { get; init; }
    public int ClientPodParts { get; init; }
    public int ClientPodCargo { get; init; }
    public string[] LoadoutRows { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ProofResult))]
internal sealed partial class ProofJsonContext : JsonSerializerContext;
