// Real production Game1 window and transport. The loopback endpoint never
// accepts/answers the logon. Capture the pending join, then Escape and return.
// SDL_VIDEODRIVER=offscreen dotnet run --project tools/p974-session-proof -- \
//   --style classic|modern --out docs/reports/P974_assets
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

string? Arg(string key)
{
    int i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
string output = Path.GetFullPath(Arg("--out") ?? "screenshots");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
var style = Arg("--style") == "modern" ? HudStyle.Modern : HudStyle.Classic;
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
using var game = new Game1(new Game1.LaunchOptions
{
    Intro = true, PlayerName = "P974Pilot", WindowWidth = 800, WindowHeight = 480,
    LobbyUrl = "http://127.0.0.1:1",
});
game.Content.RootDirectory = content;
game.Components.Add(new SessionProof(game, style, (ushort)((IPEndPoint)listener.LocalEndpoint).Port, output));
game.Run();

sealed class SessionProof(Game1 game, HudStyle style, ushort port, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _step, _updates;
    private double _next;
    private object? Get(string field) => typeof(Game1).GetField(field, Hidden)!.GetValue(game);
    private void Set(string field, object value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
    private void EnumField(string field, string value)
        => Set(field, Enum.Parse(typeof(Game1).GetField(field, Hidden)!.FieldType, value));
    private void Call(string method, params object?[] values) => typeof(Game1).GetMethod(method, Hidden)!.Invoke(game, values);
    private void Shot(string label)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", $"p974_{label}_{style.ToString().ToLowerInvariant()}");
        Set("_autoShotScheduled", true);
    }
    public override void Update(GameTime time)
    {
        _updates++;
        game.GraphicsDevice.Textures[0] = null;
        if (_watch.Elapsed.TotalSeconds > 30) throw new TimeoutException("P974 proof timed out");
        if ((bool)Get("_autoShotScheduled")! || _watch.Elapsed.TotalSeconds < _next) return;
        switch (_step)
        {
            case 0:
                if (Get("_state")!.ToString() != "Intro") return;
                Call("StopIntroScan");
                ((HudSettings)Get("_hudSettings")!).Style = style;
                EnumField("_introView", "DirectConnect");
                Set("_returnToIntroOnFailure", true);
                Set("_playerName", "P974Pilot");
                Call("BeginConnect", "127.0.0.1", port, uint.MaxValue, null, false, null, null,
                    Enum.Parse(typeof(Game1).GetField("_joinOrigin", Hidden)!.FieldType, "Intro"));
                break;
            case 1:
                if (Get("_state")!.ToString() != "Connecting") throw new InvalidOperationException("join did not stay pending");
                Console.WriteLine($"[p974-proof] {style}: pending join, updates={_updates}, taskCompleted={((Task)Get("_connectOperation")!).IsCompleted}");
                Console.WriteLine($"[p974-proof] viewport={game.GraphicsDevice.Viewport} backbuffer={game.GraphicsDevice.PresentationParameters.BackBufferWidth}x{game.GraphicsDevice.PresentationParameters.BackBufferHeight}");
                Shot("connecting");
                break;
            case 2:
                Call("UpdateConnecting", new KeyboardState(Keys.Escape));
                break;
            case 3:
                if (Get("_state")!.ToString() == "Connecting") return;
                if (Get("_state")!.ToString() != "Intro") throw new InvalidOperationException("Escape did not return to the origin");
                Console.WriteLine($"[p974-proof] {style}: cancelled to Intro, updates={_updates}");
                Shot("cancelled");
                break;
            case 4:
                game.Exit();
                return;
        }
        _step++;
        _next = _watch.Elapsed.TotalSeconds + 0.7;
    }
}
