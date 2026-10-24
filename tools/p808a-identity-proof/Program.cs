using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

// Real Game1 window and production intro renderer. The component supplies one F2
// key state on the game thread; browser approval remains a real HTTP/WebAuthn flow.
// Usage: <content-root> <lobby-origin> <pending|signed-in|directory> <output.png>
if (args.Length != 4) throw new ArgumentException("content-root lobby-origin pending|signed-in|directory output.png");
using var game = new Game1(new Game1.LaunchOptions
{
    Intro = args[2] != "directory", LobbyUrl = args[1], PlayerName = "AnonymousPilot", WindowWidth = 1280, WindowHeight = 720, EnhancedVfx = false,
});
game.Content.RootDirectory = Path.GetFullPath(args[0]);
var proof = new IdentityProof(game, args[2], args[3]); game.Components.Add(proof);
game.Run();
return proof.Captured ? 0 : 1;

sealed class IdentityProof(Game1 game, string mode, string output) : GameComponent(game)
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private bool _queued;
    private int _introFrames, _directoryFrames;
    public bool Captured { get; private set; }
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    private T Get<T>(string field) => (T)typeof(Game1).GetField(field, Hidden)!.GetValue(game)!;
    private void Set(string field, object value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
    public override void Update(GameTime time)
    {
        if (_clock.Elapsed.TotalSeconds > 120) { Console.Error.WriteLine("Identity proof timed out"); game.Exit(); return; }
        if (_queued)
        {
            if (!Get<bool>("_autoShotScheduled")) { Captured = true; game.Exit(); }
            return;
        }
        if (mode == "directory")
        {
            if (Get<object>("_state").ToString() == "Lobby" && Get<List<ServerListing>>("_servers").Count > 0 && ++_directoryFrames >= 8) Queue();
            return;
        }
        if (Get<object>("_state").ToString() != "Intro") return;
        if (++_introFrames == 8)
        {
            typeof(Game1).GetMethod("UpdateIdentity", Hidden)!.Invoke(game, [new KeyboardState(Keys.F2)]);
            Console.WriteLine("[proof] F2 sign-in requested in actual Game1 intro");
        }
        var screen = Get<IdentityScreenModel>("_identityScreen");
        if (_introFrames >= 8 && (mode == "pending" ? screen.Device is not null : screen.Account is not null))
        {
            Queue();
        }
    }
    private void Queue()
    {
        Set("_autoshotDir", Path.GetDirectoryName(Path.GetFullPath(output))!);
        Set("_autoShotName", Path.GetFileNameWithoutExtension(output));
        Set("_autoShotScheduled", true); _queued = true;
        Console.WriteLine($"[proof] real-window capture queued: {mode}");
    }
}
