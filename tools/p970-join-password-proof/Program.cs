// P970 proof — real-window captures of the join-password UX in BOTH HUD styles at
// 800x480 and 1280x720. Runs the production Game1 client (no artwork, no server:
// the listing is injected exactly as the lobby resolver would hand it over) and
// writes the real backbuffer, so every screenshot is what a player sees.
//
//   SDL_VIDEODRIVER=offscreen dotnet run --project tools/p970-join-password-proof -- \
//       --style classic|modern --width 800 --height 480 --out docs/reports/P970_assets
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Discovery;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Microsoft.Xna.Framework;

string output = Path.GetFullPath(Arg("--out") ?? "screenshots");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string link = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(link) && Directory.Exists(content)) Directory.CreateSymbolicLink(link, content);

var style = (Arg("--style") ?? "classic").Equals("modern", StringComparison.OrdinalIgnoreCase)
    ? HudStyle.Modern : HudStyle.Classic;
int width = int.Parse(Arg("--width") ?? "1280", System.Globalization.CultureInfo.InvariantCulture);
int height = int.Parse(Arg("--height") ?? "720", System.Globalization.CultureInfo.InvariantCulture);

using var game = new Game1(new Game1.LaunchOptions { Intro = true, PlayerName = "P970Pilot", WindowWidth = width, WindowHeight = height });
game.Content.RootDirectory = content;
game.Components.Add(new PasswordProof(game, style, width, height, output));
game.Run();

string? Arg(string name)
{
    string[] args = Environment.GetCommandLineArgs();
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

/// <summary>Injects the two join screens the task changes and captures each one.</summary>
sealed class PasswordProof(Game1 game, HudStyle style, int width, int height, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
    private int _step;
    private double _next;

    private T Get<T>(string field) => (T)typeof(Game1).GetField(field, Hidden)!.GetValue(game)!;
    private void Set(string field, object? value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
    private void Style(HudStyle s) => Get<HudSettings>("_hudSettings").Style = s;
    private void EnumField(string field, string value)
        => Set(field, Enum.Parse(typeof(Game1).GetField(field, Hidden)!.FieldType, value));
    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
    }

    public override void Update(GameTime time)
    {
        // Autoshot uploads a temporary texture; force the UI-only frame to rebind its
        // font after that capture instead of inheriting the GL texture binding.
        game.GraphicsDevice.Textures[0] = null;
        if (Get<bool>("_autoShotScheduled")) return;
        if (_watch.Elapsed.TotalSeconds > 60) throw new TimeoutException("P970 proof timeout");
        if (_watch.Elapsed.TotalSeconds < _next) return;

        switch (_step)
        {
            case 0:
                if (Get<object>("_state").ToString() != "Intro") return;
                typeof(Game1).GetMethod("StopIntroScan", Hidden)!.Invoke(game, null);
                Set("_lanJoinPassword", new System.Text.StringBuilder());
                SetupJoinLink();
                Style(style);
                break;
            case 1:
                Console.WriteLine($"[p970-proof] view={Get<object>("_introView")} state={Get<object>("_state")} "
                    + $"viewport={game.GraphicsDevice.Viewport.Width}x{game.GraphicsDevice.Viewport.Height} "
                    + $"backbuffer={game.GraphicsDevice.PresentationParameters.BackBufferWidth}x{game.GraphicsDevice.PresentationParameters.BackBufferHeight}");
                Shot($"p970_joinlink_{StyleTag()}"); break;
            case 2: SetupFindServer(); break;
            case 3: Shot($"p970_findserver_{StyleTag()}"); break;
            case 4: SetupJoinLink(); Set("_joinLinkConfirmation", RefusedConfirmation()); break;
            case 5: Shot($"p970_joinlink_refused_{StyleTag()}"); break;
            case 6: game.Exit(); return;
        }
        _step++;
        _next = _watch.Elapsed.TotalSeconds + 1.2;
    }

    private string StyleTag() => (style == HudStyle.Modern ? "modern" : "classic") + $"_{width}x{height}";

    private ServerListing Listing(bool passwordRequired) => new(
        "abc123", "Wing War server", "127.0.0.1", 6073, 4, 16, "Wing War", "PCore001.igc", "0.1.5",
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, PasswordRequired: passwordRequired);

    private JoinLinkConfirmation RefusedConfirmation()
    {
        var confirmation = BaseConfirmation();
        confirmation.RejectPassword(JoinPasswordPrompt.RefusalMessage());
        return confirmation;
    }

    private JoinLinkConfirmation BaseConfirmation()
    {
        Assert(JoinLink.TryParse("allegiance://join?lobby=https%3A%2F%2Flobby.example.org&id=abc123", out var parsed));
        // Same lobby as the saved one, ready state: the resolved-confirmation view.
        var confirmation = new JoinLinkConfirmation(parsed, "https://lobby.example.org", "Confirm to join this game.");
        var listing = Listing(true);
        typeof(JoinLinkConfirmation).GetProperty("Listing")!.SetValue(confirmation, listing);
        typeof(JoinLinkConfirmation).GetField("_missions", Hidden)!.SetValue(confirmation, (IReadOnlyList<ServerListing>)[listing]);
        return confirmation;
    }

    private void SetupJoinLink()
    {
        Set("_joinLinkConfirmation", BaseConfirmation());
        Set("_joinLinkResolution", Task.CompletedTask);
        Set("_joinLinkInputArmed", true);
        EnumField("_state", "Intro");
        EnumField("_introView", "JoinLink");
        Set("_lanJoinPassword", new System.Text.StringBuilder());
        Set("_editingLanPassword", false);
        Set("_joinLinkPasswordSeeded", false);
        // The production update is what opens the screen on the password field.
        typeof(Game1).GetMethod("UpdateJoinLink", Hidden)!.Invoke(game, [default(Microsoft.Xna.Framework.Input.KeyboardState)]);
        // Then the player types four characters: the count and the caret are drawn.
        foreach (char c in "wing")
            typeof(Game1).GetMethod("OnTextInput", Hidden)!.Invoke(game,
                [game, new TextInputEventArgs(c, Microsoft.Xna.Framework.Input.Keys.W)]);
    }

    private void SetupFindServer()
    {
        Set("_introFoundLock", new object());
        Set("_introFound", new List<DiscoveredServer>
        {
            // Named so the sorted list puts the guarded game first: the capture must
            // show the focused password field of a protected selection.
            new("Guarded Wing War", "127.0.0.1", 6073, DiscoverySource.Lobby, 0, "Wing War", 4, 16, "0.1.5", "",
                MissionCookie: 7, PasswordRequired: true),
            new("Zzz open skirmish", "127.0.0.2", 6074, DiscoverySource.Lan, 3, "Open map", 1, 16, "0.1.5", ""),
        });
        Set("_introFoundIndex", 0);
        Set("_lanJoinPassword", new System.Text.StringBuilder());
        EnumField("_introView", "FindServer");
        Set("_findPasswordFocusTarget", null);
        typeof(Game1).GetMethod("SyncFindServerPasswordFocus", Hidden)!.Invoke(game, null);
        foreach (char c in "wing")
            typeof(Game1).GetMethod("OnTextInput", Hidden)!.Invoke(game,
                [game, new TextInputEventArgs(c, Microsoft.Xna.Framework.Input.Keys.W)]);
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("P970 proof setup refused a value");
    }
}
