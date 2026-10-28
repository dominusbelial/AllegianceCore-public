// P853b — REAL-WINDOW proof of the client's two host-editing affordances in the
// pre-game team lobby:
//
//   * the side leader's TEAM NAME edit  (native TeamSettingsDialogPopup,
//     WinTrek/teamscreen.cpp:856-1104 — teamNameEditPane, OnButtonOk → FM_CS_SET_TEAM_INFO)
//   * the mission owner's GAME SETTINGS form (native NewGameScreen,
//     WinTrek/newgamescreen.cpp:1034-1130 — Invalid(true) then FM_CS_MISSIONPARAMS)
//
// The proof runs the real windowed MonoGame client against a real
// Allegiance.Server over loopback, seated as side 0's leader AND mission owner,
// NOT ready (the mission stays STAGE_NOTSTARTED). It drives the production
// UpdateReadyCheck handler with synthetic KeyboardStates and the production
// Window.TextInput handler with real TextInputEventArgs (the P752/P802/P761 proof
// idiom), takes real backbuffer PNGs via SaveAsPng, and reads back the SERVER's
// echo — the renamed side and the new lives/teams — so the screenshots show the
// result, not just the click.
//
//   dotnet run --project tools/p853b-hostedit-proof -- <outDir> <classic|modern>
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Srv = Allegiance.Server.Server;

string output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/reports/P853B_assets");
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
        // Four assembled sides so the owner's TEAMS control can really shrink
        // (P853's documented grow clamp caps it at the assembled count).
        Name = "P853b host edit", Map = "XXV_simple.igc", Teams = 4, CivIds = [1, 3, 2, 4],
        SideNames = ["Alpha", "Bravo", "Charlie", "Delta"],
        Lives = 3,
        // Native MissionParams::Reset starts a game on conquest 100 (igc.h:1314);
        // the port's GameConfig defaults to NO winning condition, which
        // MissionParams::Invalid(true) refuses (igc.h:1349-1352) — a fresh
        // owner form would have to pick one first. Start the proof from native's
        // default so the first send is valid, like a native-created game.
        Goals = new Allegiance.Core.Model.MissionGoals { ConquestPercentage = 100 },
    }],
});
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); } });
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P853b host",
        WindowWidth = 1280, WindowHeight = 720,
        // Seat on side 0 and HOLD: AutoReady stays false, so the ready-check
        // (team lobby) screen stays up for the whole run.
        AutoProof = new AutoProofPlan { AutoSide = 0 },
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new P853bHostEditProof(game, output, skin, server));
    game.Run();
}
finally
{
    stop.Cancel(); ticker.Join();
    try { File.Delete(hudPath); } catch { /* best effort */ }
}
Console.WriteLine("P853B_HOSTEDIT_PROOF done");

internal static class Refl
{
    public const BindingFlags H = BindingFlags.Instance | BindingFlags.NonPublic;
    public static T Get<T>(object o, string name) => (T)typeof(Game1).GetField(name, H)!.GetValue(o)!;
    public static void Set(object o, string name, object? v) => typeof(Game1).GetField(name, H)!.SetValue(o, v);
    public static object? Call(object o, string name, params object?[] args) =>
        typeof(Game1).GetMethod(name, H | BindingFlags.Static)!.Invoke(o, args);
}

/// <summary>Drives the production handlers: open the team-name edit, type a name,
/// confirm, read the server's rename echo; then open the game-settings form, change
/// LIVES and TEAMS, send, read the server's re-advertisement; then force an invalid
/// form so the error state is on a real frame too.</summary>
internal sealed class P853bHostEditProof(Game1 game, string output, string skin, Srv server) : GameComponent(game)
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private string _shotName = "";
    private bool _shotPending;
    private short _livesBefore;
    private short _teamsBefore;
    private string _nameBefore = "";

    private void Shot(string name)
    {
        Refl.Set(game, "_autoshotDir", output);
        Refl.Set(game, "_autoShotName", $"p853b-{name}-{skin}");
        Refl.Set(game, "_autoShotScheduled", true);
        _shotName = name;
        _shotPending = true;
        Console.WriteLine($"P853B_SCREEN p853b-{name}-{skin}");
    }

    private void Press(params Keys[] keys)
    {
        Refl.Call(game, "UpdateReadyCheck", new KeyboardState(keys));
        Refl.Call(game, "UpdateReadyCheck", default(KeyboardState));   // release the edge
    }

    /// <summary>Feed one typed character through the production
    /// Window.TextInput handler (OnTextInput), exactly what MonoGame pumps.</summary>
    private void Type(string text)
    {
        var method = typeof(Game1).GetMethod("OnTextInput", Refl.H)!;
        foreach (char c in text)
            method.Invoke(game, [null, new TextInputEventArgs(c)]);
    }

    private ConnectionSession Session => Refl.Get<ConnectionSession>(game, "_session");

    private bool WaitFor(Func<bool> condition, double seconds)
    {
        // The world is mutated by the network thread; keep the poll cheap and let
        // the game keep rendering (the screenshots need real presented frames).
        return condition();
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 180) throw new TimeoutException("P853b host-edit proof did not finish");
        if (_shotPending)
        {
            // Wait until the Draw-end hook has flushed the frame before acting again.
            if (!Refl.Get<bool>(game, "_autoShotScheduled")) _shotPending = false;
            return;
        }
        string state = Refl.Get<object>(game, "_state").ToString() ?? "?";
        if (state != "ReadyCheck" || _watch.Elapsed.TotalSeconds < _next) return;
        var session = Session;
        var world = session.World;

        switch (_step++)
        {
            case 0:
            {
                // Seated as side 0's leader AND the mission owner, mission NotStarted.
                lock (session.Gate)
                    if (!world.IsTeamLeader || !world.AmMissionOwner
                        || world.Mission is not { Stage: (int)MissionStage.NotStarted })
                    { _step--; return; }
                lock (session.Gate)
                {
                    _nameBefore = TeamNameEdit.CurrentName(world);
                    _livesBefore = world.Mission!.Params?.ILives ?? 0;
                    _teamsBefore = world.Mission.Params?.NTems ?? 0;
                }
                Console.WriteLine($"[p853b] start: side name '{_nameBefore}', lives {_livesBefore}, teams {_teamsBefore}");
                _next = _watch.Elapsed.TotalSeconds + 0.5;
                break;
            }
            case 1:
                Shot("teams-closed");            // the lobby pane: TEAM NAME row + T hint
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 2:
                Press(Keys.T);                   // open the inline edit
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 3:
                Shot("rename-open");
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 4:
                // Empty the box (select all is not modelled): BACKSPACE the
                // pre-filled name, then confirm → the refusal is on screen.
                for (int i = 0; i < 32; i++) Press(Keys.Back);
                Press(Keys.Enter);
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 5:
                Shot("rename-error");
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 6:
                Type("Blue Squad");
                Press(Keys.Enter);               // → FM_CS_SET_TEAM_INFO(15) with squadID NA
                _next = _watch.Elapsed.TotalSeconds + 1.5;
                break;
            case 7:
            {
                // The server's SET_TEAM_INFO relay must have renamed the side on
                // THIS peer (P853 ClientWorld.SideNameOverride).
                string now;
                lock (session.Gate) now = TeamNameEdit.CurrentName(world);
                if (now == _nameBefore) { _step--; return; }   // wait for the echo
                Console.WriteLine($"[p853b] rename echo: side 0 '{_nameBefore}' -> '{now}'");
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            }
            case 8:
                Shot("rename-applied");
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 9:
                Press(Keys.G);                   // open the owner's settings form
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 10:
                Shot("settings-open");
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 11:
            {
                // LIVES: one native step (LivesValues), TEAMS: shrink by one, both
                // through the real UP/DOWN + LEFT/RIGHT key path.
                var form = Refl.Get<object?>(game, "_settingsForm");
                if (form is null) { _step--; return; }
                int livesRow = Array.IndexOf(MissionSettingsForm.Fields, MissionSettingsForm.Field.Lives);
                int teamsRow = Array.IndexOf(MissionSettingsForm.Fields, MissionSettingsForm.Field.Teams);
                int cursor = (int)form.GetType().GetProperty("Cursor")!.GetValue(form)!;
                for (int i = 0; i < Math.Abs(livesRow - cursor); i++) Press(Keys.Down);
                Press(Keys.Right);                            // next LivesValues entry
                for (int i = 0; i < Math.Abs(livesRow - teamsRow); i++) Press(Keys.Up);
                Press(Keys.Left);                             // one team fewer
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            }
            case 12:
                // The edited form, before the send: TEAMS one lower, LIVES one
                // native step up. (Shot first: the frame is flushed at the next
                // Draw, so the send must wait a frame or the screenshot would
                // already show the closed pane.)
                Shot("settings-edited");
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 13:
                Press(Keys.Enter);               // → FM_CS_MISSIONPARAMS(113)
                _next = _watch.Elapsed.TotalSeconds + 1.5;
                break;
            case 14:
            {
                // The server re-advertises the whole MISSIONDEF; the client's
                // MissionInfo must now carry the new lives + team count, and the
                // AUTHORITATIVE mission (the second vantage point) must have
                // adopted them (FedSrv.CPP:7577, fsmission.cpp:1796-1895).
                short lives, teams;
                lock (session.Gate)
                {
                    lives = world.Mission?.ILives ?? -1;
                    teams = world.Mission?.Params?.NTems ?? -1;
                }
                if (lives == _livesBefore && teams == _teamsBefore) { _step--; return; }  // wait for the echo
                Console.WriteLine($"[p853b] params echo: lives {_livesBefore} -> {lives}, teams {_teamsBefore} -> {teams}");
                // The AUTHORITATIVE mission — the second vantage point: the
                // server adopted the owner's edit (fsmission.cpp:1843-1859) and
                // the rename (fsmission.cpp:1564-1600).
                if (server.TestMission is { } mission)
                    Console.WriteLine($"[p853b] SERVER state: lives={mission.Lives} params.nTeams={server.TestParamsOf(mission).NTems} " +
                        $"sides=[{string.Join(", ", mission.Sides.Select(sd => $"{sd.Id}:{sd.Name}{(sd.Active ? "" : "(inactive)")}"))}]");
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            }
            case 15:
                Press(Keys.G);                   // reopen: shows the server's NEW values
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 16:
            {
                // Park the cursor on LIVES so the applied frame is visually
                // distinct from the pre-send draft AND points at the value the
                // server just echoed back (3 -> 4).
                var form = Refl.Get<object?>(game, "_settingsForm");
                if (form is null) { _step--; return; }
                int livesRow = Array.IndexOf(MissionSettingsForm.Fields, MissionSettingsForm.Field.Lives);
                int cursor = (int)form.GetType().GetProperty("Cursor")!.GetValue(form)!;
                for (int i = 0; i < Math.Abs(livesRow - cursor); i++)
                    Press(livesRow > cursor ? Keys.Down : Keys.Up);
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            }
            case 17:
                Shot("settings-applied");
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 18:
            {
                var form = Refl.Get<object?>(game, "_settingsForm");
                if (form is null) { _step--; return; }
                int conquestRow = Array.IndexOf(MissionSettingsForm.Fields, MissionSettingsForm.Field.Conquest);
                int cursor = (int)form.GetType().GetProperty("Cursor")!.GetValue(form)!;
                for (int i = 0; i < Math.Abs(conquestRow - cursor); i++)
                    Press(conquestRow > cursor ? Keys.Down : Keys.Up);
                // ConquestPercentValues = [0,80,90,100]: three LEFT steps turn the
                // only winning condition off → Invalid(true) refuses the send.
                Press(Keys.Left); Press(Keys.Left); Press(Keys.Left);
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            }
            case 19:
                Press(Keys.Enter);               // send attempt → error row, nothing sent
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            case 20:
                Shot("settings-error");
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                break;
            default:
                if (!_shotPending) game.Exit();
                return;
        }
    }
}
