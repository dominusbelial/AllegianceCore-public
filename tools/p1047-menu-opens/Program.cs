// Record actual desktop Game1 opens, including ContentManager streams. No server is started or joined.
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Input;

if (args.Length != 2 || !Enum.TryParse<HudStyle>(args[0], out var style))
    throw new ArgumentException("p1047-menu-opens Classic|Modern OUTPUT.json");
string output = Path.GetFullPath(args[1]);
string scratch = Path.GetDirectoryName(output)!;
Directory.CreateDirectory(scratch);
Environment.SetEnvironmentVariable("ALLEGIANCE_PLAYER_SETTINGS", Path.Combine(scratch, style + "-player.json"));
Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", Path.Combine(scratch, style + "-hud.json"));
Environment.SetEnvironmentVariable("ALLEGIANCE_KEYMAP", Path.Combine(scratch, style + "-keymap.json"));
new IntroPreferences { ShowStartupTrainingSuggestion = false, Lobby = "" }.Save();
new HudSettings { Style = style }.Save(HudSettings.DefaultPath);
var recorder = new OpenRecorder(style.ToString());
string artwork = AssetSources.ResolveRoot();
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
using var game = new Game1(new Game1.LaunchOptions
{
    Intro = true, AssetSource = new RecordingSource(new DirectoryAssetSource(artwork), recorder),
    LobbyUrl = "", LobbyUrlExplicit = true, PlayerName = "pilot", WindowWidth = 800, WindowHeight = 480,
    EffectsRequested = Allegiance.ClientCore.State.EffectsQuality.Native,
});
game.Content = new RecordingContent(game.Services, content, recorder);
game.Components.Add(new Scenes(game, recorder));
game.Run();
File.WriteAllText(output, JsonSerializer.Serialize(new { version = 1, style = style.ToString(), recorder.Scenes, recorder.Opens }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"P1047_OPENS_PASS style={style} scenes={recorder.Scenes.Count} opens={recorder.Opens.Count}");

sealed record Open(string Source, string Origin, string Purpose, string Scene, long ByteSize, string Sha256);
sealed class OpenRecorder(string style)
{
    internal readonly List<string> Scenes = new();
    internal readonly List<Open> Opens = new();
    internal string Scene = "Boot";
    internal void Enter(string scene) { Scene = scene; Scenes.Add(style + "/" + scene); }
    internal Stream Record(string source, string origin, string purpose, Stream stream)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        stream.Position = 0;
        Opens.Add(new(source.Replace('\\', '/'), origin, purpose, Scene, stream.Length, hash));
        return stream;
    }
}
sealed class RecordingSource(IAssetSource source, OpenRecorder recorder) : IAssetSource
{
    public bool Exists(string path) => source.Exists(path);
    public Stream OpenRead(string path) => recorder.Record(path, "artwork", "Artwork", source.OpenRead(path));
    public IEnumerable<string> Enumerate(string path) => source.Enumerate(path);
}
sealed class RecordingContent(IServiceProvider services, string root, OpenRecorder recorder) : ContentManager(services)
{
    private string _purpose = "Content";
    public override T Load<T>(string assetName)
    {
        string before = _purpose; _purpose = typeof(T).Name;
        try { return base.Load<T>(assetName); }
        finally { _purpose = before; }
    }
    protected override Stream OpenStream(string assetName) =>
        recorder.Record("Content/" + assetName + ".xnb", "content", _purpose, File.OpenRead(Path.Combine(root, assetName + ".xnb")));
}
sealed class Scenes(Game1 game, OpenRecorder recorder) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private int _step, _frames;
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    public override void Update(GameTime gameTime)
    {
        if (++_frames > 600) throw new TimeoutException("Desktop menu trace did not reach the required scenes.");
        if (Get<object>("_state").ToString() != "Intro") return;
        switch (_step++)
        {
            case 0:
                recorder.Enter("Intro");
                if (Get<object>("_introView").ToString() != "Main") throw new InvalidOperationException("Expected actual intro menu.");
                break;
            case 4: Set("_introIndex", 4); Set("_introProofKeys", new[] { Keys.Enter }); break;
            case 5: Set("_introProofKeys", Array.Empty<Keys>()); break;
            case 8:
                if (Get<object>("_introView").ToString() != "Settings") throw new InvalidOperationException("Expected actual Options menu.");
                recorder.Enter("Options"); break;
            case 10: Set("_introProofKeys", new[] { Keys.Down }); break;
            case 11: Set("_introProofKeys", Array.Empty<Keys>()); break;
            case 12: Set("_introProofKeys", new[] { Keys.Down }); break;
            case 13: Set("_introProofKeys", Array.Empty<Keys>()); break;
            case 14: Set("_introProofKeys", new[] { Keys.Enter }); break;
            case 15: Set("_introProofKeys", Array.Empty<Keys>()); break;
            case 18:
                if (Get<OptionsMenuModel>("_optionsMenu").Current != OptionsMenuModel.Page.Game) throw new InvalidOperationException("Expected Game Options.");
                recorder.Enter("GameOptions"); break;
            case 20: Set("_introProofKeys", new[] { Keys.Y }); break;
            case 21: Set("_introProofKeys", Array.Empty<Keys>()); break;
            case 24:
                if (!IntroPreferences.Load().InvertMouseY) throw new InvalidOperationException("Options toggle did not save.");
                game.Exit(); break;
        }
        base.Update(gameTime);
    }
}
