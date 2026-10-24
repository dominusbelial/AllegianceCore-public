using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Srv = Allegiance.Server.Server;

// P802 — REAL-WINDOW proof of the team lobby's civilization picker: the real
// windowed Game1 DesktopGL client over loopback against the real
// Allegiance.Server, seated as side 0's team leader but NOT ready (the mission
// stays STAGE_NOTSTARTED). The proof drives the real UpdateReadyCheck handler
// with synthetic KeyboardStates (the P752/P761 proof idiom), takes real
// backbuffer PNGs with SaveAsPng, and switches the client's HUD skin through
// ALLEGIANCE_HUD_SETTINGS so both Classic and Modern are captured.
//
//   dotnet run --project tools/p802-civ-proof -- <outDir> <classic|modern>
string output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/reports/P802_assets");
string skin = args.Length > 1 ? args[1].ToLowerInvariant() : "modern";
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
if (!Directory.Exists(content)) content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

// Force the requested HUD skin through the production preference loader
// (HudSettings.Load honours ALLEGIANCE_HUD_SETTINGS before player.json).
string hudPath = Path.Combine(output, $".hud-{skin}.json");
File.WriteAllText(hudPath, JsonSerializer.Serialize(new { style = skin == "classic" ? 0 : 1, size = 1.0 }));
Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", hudPath);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new global::Allegiance.Server.ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new global::Allegiance.Server.GameConfig
    {
        Name = "P802 civ picker", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
    }],
});
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); } });
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P802 civ pilot",
        WindowWidth = 1280, WindowHeight = 720,
        // Seat on side 0 and HOLD: AutoReady stays false, so the ready-check
        // screen (with the civ picker) stays up for the whole run.
        AutoProof = new AutoProofPlan { AutoSide = 0 },
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new P802CivProof(game, output, skin));
    game.Run();
}
finally
{
    stop.Cancel(); ticker.Join();
    try { File.Delete(hudPath); } catch { /* best effort */ }
}
Console.WriteLine("P802_CIV_PROOF done");

internal static class Refl
{
    public const BindingFlags H = BindingFlags.Instance | BindingFlags.NonPublic;
    public static T Get<T>(object o, string name) => (T)typeof(Game1).GetField(name, H)!.GetValue(o)!;
    public static void Set(object o, string name, object? v) => typeof(Game1).GetField(name, H)!.SetValue(o, v);
    public static object? Call(object o, string name, params object?[] args) =>
        typeof(Game1).GetMethod(name, H | BindingFlags.Static)!.Invoke(o, args);
}

/// <summary>Drives the real ready-check handler: open the picker, shoot; pick a
/// different civilization, confirm, wait for the server's roster echo, shoot.</summary>
internal sealed class P802CivProof(Game1 game, string output, string skin) : GameComponent(game)
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private bool _finishAfterFlush;
    private short _before;

    private void Shot(string name)
    {
        Refl.Set(game, "_autoshotDir", output);
        Refl.Set(game, "_autoShotName", name);
        Refl.Set(game, "_autoShotScheduled", true);
        Console.WriteLine("P802_SCREEN " + name);
    }

    private void Press(params Keys[] keys)
    {
        Refl.Call(game, "UpdateReadyCheck", new KeyboardState(keys));
        Refl.Call(game, "UpdateReadyCheck", default(KeyboardState));   // release the edge
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("P802 civ proof did not finish");
        if (_finishAfterFlush)
        {
            if (!(bool)typeof(Game1).GetField("_autoShotScheduled", Refl.H)!.GetValue(game)!)
                game.Exit();
            return;
        }
        string state = Refl.Get<object>(game, "_state").ToString() ?? "?";
        if (state != "ReadyCheck" || _watch.Elapsed.TotalSeconds < _next) return;
        var session = Refl.Get<ConnectionSession>(game, "_session");
        var world = session.World;

        switch (_step++)
        {
            case 0:
            {
                // Wait until seated as side 0's leader with a loaded core + civ.
                lock (session.Gate)
                    if (!world.IsTeamLeader || world.Mission is not { Stage: (int)MissionStage.NotStarted }
                        || world.MissionCore is null || global::Allegiance.ClientCore.State.TeamCivPicker.CurrentCivId(world) is null)
                    { _step--; return; }
                lock (session.Gate) _before = global::Allegiance.ClientCore.State.TeamCivPicker.CurrentCivId(world)!.Value;
                // C opens the picker through the real input action.
                Press(Keys.C);
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            }
            case 1:
                Shot($"civ-picker-{skin}-720p");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 2:
            {
                // Walk the cursor to a DIFFERENT civilization than the current
                // one (that is the whole point of the picker), then confirm with
                // Enter: UpdateReadyCheck sends the real CHANGE_TEAM_CIV(51).
                var options = global::Allegiance.ClientCore.State.TeamCivPicker.Options(world.MissionCore);
                int target = Enumerable.Range(0, options.Count)
                    .First(i => options[i].CivId != _before && !options[i].IsRandom);
                int moves = (target - global::Allegiance.ClientCore.MonoGame.Rendering.TeamCivPickerLayout.InitialIndex(world) + options.Count) % options.Count;
                for (int i = 0; i < moves; i++) Press(Keys.Down);
                Press(Keys.Enter);
                Console.WriteLine($"[p802] CHANGE_TEAM_CIV side {world.MySideId} civ {_before} -> {options[target].CivId}");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            }
            case 3:
            {
                // The server relay must have landed on this peer's roster.
                short? now;
                lock (session.Gate) now = global::Allegiance.ClientCore.State.TeamCivPicker.CurrentCivId(world);
                if (now is null || now == _before) { _step--; return; }   // wait for the echo
                _next = _watch.Elapsed.TotalSeconds + 0.5;
                break;
            }
            case 4:
                Shot($"civ-changed-{skin}-720p");
                _finishAfterFlush = true;
                break;
            default:
                _step--;
                return;
        }
    }
}
