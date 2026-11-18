// Record actual desktop join/launch/60-second first-sector flight opens on loopback.
using System.Reflection;
using Allegiance.Server;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.Replay;
using Allegiance.Core.Wire;
using Allegiance.Core;
using System.Security.Cryptography;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Input;

// Recreate raw desktop reference bytes without opening a window or server.
if (args is ["--decode-briefings", var fixtureDirectory])
{
    foreach (string file in Directory.GetFiles(fixtureDirectory, "briefing-*.bin"))
    {
        byte[] message = File.ReadAllBytes(file);
        if (FedMessageHeader.Read(message).Fmid != MissionBriefingInfo.Fmid) throw new InvalidDataException(file);
        using var input = new MemoryStream(message, 4, message.Length - 4);
        using var brotli = new System.IO.Compression.BrotliStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        brotli.CopyTo(decoded);
        File.WriteAllBytes(Path.ChangeExtension(file, ".desktop.json"), decoded.ToArray());
        Console.WriteLine($"DESKTOP_BROTLI_PASS {Path.GetFileName(file)} bytes={decoded.Length}");
    }
    return;
}

if (args.Length is < 2 or > 4 || !Enum.TryParse<HudStyle>(args[0], out var style))
    throw new ArgumentException("p1051-flight-opens Classic|Modern OUTPUT.json [SIDE] [MINERS]");
short selectedSide = args.Length >= 3 ? short.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : (short)0;
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
using var server = new Allegiance.Server.Server { SpawnInitialMinersAtBoot = args.Length == 4 && bool.Parse(args[3]), SimulationRandom = new Random(1051) };
server.Boot(new ServerConfig
{
    ArtPath = artwork, StaticCore = "PCore001.igc", Port = 0, BindAddress = "127.0.0.1", UpdatesPerSecond = 20,
    Discovery = new() { Enabled = false },
    Games = [new() { Name = "P1051 recorded flight", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], CivSeed = 1051, SideNames = ["Alpha", "Bravo"],
        AutoStart = true, AllowEmptyTeams = true, StartCountdownSeconds = 0 }],
});
using var cancellation = new CancellationTokenSource();
var ticker = Task.Run(async () => { while (!cancellation.IsCancellationRequested) { server.TickSim(); await Task.Delay(50); } });
using var recording = new WireRecording(Path.Combine(scratch, style + ".wire.gz"));
using var game = new Game1(new Game1.LaunchOptions
{
    DirectHost = "127.0.0.1", DirectPort = checked((ushort)server.BoundPort),
    AssetSource = new RecordingSource(new DirectoryAssetSource(artwork), recorder),
    LobbyUrl = "", LobbyUrlExplicit = true, PlayerName = "DesktopRecorder", WindowWidth = 800, WindowHeight = 480,
    EffectsRequested = Allegiance.ClientCore.State.EffectsQuality.Native, SkyTier = "native", AlephGlimpse = false,
    AutoProof = new Allegiance.ClientCore.MonoGame.LiveProof.AutoProofPlan
        { AutoSide = selectedSide, AutoReady = true, AutoLaunchRequested = true, Camera = "chase", ExitAfter = 100 },
});
var session = (ConnectionSession)typeof(Game1).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
session.Recording = recording;
game.Content = new RecordingContent(game.Services, content, recorder);
game.Components.Add(new Scenes(game, recorder));
try { game.Run(); } finally { cancellation.Cancel(); ticker.GetAwaiter().GetResult(); }
recording.Dispose();
int briefing = 0;
foreach (var packet in new BattleReplay(Path.Combine(scratch, style + ".wire.gz")).Packets)
    foreach (var message in packet.Payload.Length == 0 ? [] : FedPacket.Unpack(packet.Payload))
        if (FedMessageHeader.Read(message).Fmid == MissionBriefingInfo.Fmid)
        {
            File.WriteAllBytes(Path.Combine(scratch, $"briefing-{style}-{briefing++}.bin"), message);
            var decoded = MissionBriefingInfo.Decode(message);
            File.WriteAllBytes(Path.Combine(scratch, $"briefing-{style}-{briefing - 1}.json"), JsonSerializer.SerializeToUtf8Bytes(decoded, MissionBriefingInfoJsonContext.Default.MissionBriefingInfo));
        }
File.WriteAllText(output, JsonSerializer.Serialize(new { version = 1, style = style.ToString(), recorder.Scenes, recorder.Opens, recorder.Required, recorder.RockSurfaces, recorder.FirstSector, flightSeconds = 60 }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"P1051_OPENS_PASS style={style} scenes={recorder.Scenes.Count} opens={recorder.Opens.Count}");

sealed record Open(string Source, string Origin, string Purpose, string Scene, long ByteSize, string Sha256);
sealed class OpenRecorder(string style)
{
    internal readonly List<string> Scenes = new();
    public HashSet<string> Required { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RockSurfaces { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DataClusterIGC? FirstSector { get; set; }
    internal void Require(string? stem) { if (!string.IsNullOrEmpty(stem)) Required.Add(stem + ".mdl"); }
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
    private readonly System.Diagnostics.Stopwatch _wall = System.Diagnostics.Stopwatch.StartNew();
    private double _flight;
    private System.Diagnostics.Stopwatch? _flightWall;
    private bool _briefingOpened;
    private string _previous = "";
    public override void Update(GameTime gameTime)
    {
        if (_wall.Elapsed.TotalSeconds > 100) throw new TimeoutException("Desktop flight recording timed out.");
        var session = (ConnectionSession)typeof(Game1).GetField("_session", Hidden)!.GetValue(game)!;
        string state = typeof(Game1).GetField("_state", Hidden)!.GetValue(game)!.ToString()!;
        if (session.World.MyShipId is { } id && session.World.Ships.TryGetValue(id, out var own))
        {
            state = own.StationId >= 0 ? "Docked" : "Flight";
            if (state == "Flight")
            {
                _flightWall ??= System.Diagnostics.Stopwatch.StartNew();
                _flight = _flightWall.Elapsed.TotalSeconds;
                var catalog = (ShopCatalog)typeof(Game1).GetField("_shopCatalog", Hidden)!.GetValue(game)!;
                foreach (var ship in session.World.Ships.Values.Where(s => s.SectorId == own.SectorId))
                {
                    var hull = catalog.Hulls.First(h => h.HullId == ship.HullId);
                    recorder.Require(hull.ModelName); recorder.Require(hull.TextureName);
                }
                foreach (var station in session.World.Stations.Values.Where(s => s.ClusterId == own.SectorId))
                { var type = catalog.StationTypes![station.StationTypeId]; recorder.Require(type.ModelName); recorder.Require(type.TextureName);
                    _ = typeof(Game1).GetMethod("StationDockGeometry", Hidden)!.Invoke(game, [type]); }
                foreach (var rock in session.World.Asteroids.Values.Where(a => a.ClusterId == own.SectorId))
                { recorder.Require(rock.ModelName); recorder.Require(rock.TextureName); if (!string.IsNullOrEmpty(rock.TextureName)) recorder.RockSurfaces.Add(rock.TextureName + ".mdl"); }
                if (session.World.Clusters.TryGetValue(own.SectorId, out var cluster))
                {
                    recorder.FirstSector ??= cluster;
                    recorder.Require(cluster.PlanetName);
                    recorder.Require(cluster.PosterName);
                    var digits = new string(cluster.PosterName.Where(char.IsDigit).ToArray());
                    if (digits.Length > 0) { recorder.Require("globe" + digits); recorder.Require("environment" + digits + "bmp"); }
                }
            }
        }
        if (state != _previous) { recorder.Enter(state); _previous = state; }
        if (!_briefingOpened && session.World.MissionBriefing is { } briefing)
        {
            var assets = (IAssetSource)typeof(Game1).GetField("_assets", Hidden)!.GetValue(game)!;
            using var source = assets.OpenRead("missionbrief.mdl");
            using var text = new StreamReader(source, System.Text.Encoding.Latin1);
            var strings = MissionBriefingStrings.Parse(text.ReadToEnd());
            _ = MissionBriefingGenerator.Build(briefing, strings: strings);
            recorder.Require("missionbrief"); _briefingOpened = true;
        }
        if (_flight >= 60) game.Exit();
        base.Update(gameTime);
    }
}
