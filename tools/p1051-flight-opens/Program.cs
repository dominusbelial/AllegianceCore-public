// Record actual desktop join/launch/60-second first-sector flight opens on loopback.
// --sectors additionally records 30-second visits throughout the native map.
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

bool allSectors = args.Contains("--sectors");
args = args.Where(a => a != "--sectors").ToArray();
if (args.Length is < 2 or > 4 || !Enum.TryParse<HudStyle>(args[0], out var style))
    throw new ArgumentException("p1051-flight-opens Classic|Modern OUTPUT.json [SIDE] [MINERS] [--sectors]");
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
recorder.AllSectors = allSectors;
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
        { AutoSide = selectedSide, AutoReady = true, AutoLaunchRequested = true, Camera = "chase", ExitAfter = allSectors ? 2400 : 100 },
});
var session = (ConnectionSession)typeof(Game1).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
session.Recording = recording;
recorder.ResolveSector = () => session.World.MyShipId is { } id && session.World.Ships.TryGetValue(id, out var own)
    && own.StationId < 0 ? own.SectorId : null;
game.Content = new RecordingContent(game.Services, content, recorder);
game.Components.Add(new Scenes(game, recorder));
try { game.Run(); } finally { cancellation.Cancel(); ticker.GetAwaiter().GetResult(); }
recording.Dispose();
// The battle replay's deliberate ten-minute limit is unchanged. Sector walks
// are longer and need no raw briefing fixture: their evidence is the full wire
// recording plus the per-sector opens below. The original short mode still
// exports its decoder fixtures through the existing bounded replay reader.
IEnumerable<RecordedPacket> briefingPackets = allSectors ? []
    : new BattleReplay(Path.Combine(scratch, style + ".wire.gz")).Packets;
int briefing = 0;
foreach (var packet in briefingPackets)
    foreach (var message in packet.Payload.Length == 0 ? [] : FedPacket.Unpack(packet.Payload))
        if (FedMessageHeader.Read(message).Fmid == MissionBriefingInfo.Fmid)
        {
            File.WriteAllBytes(Path.Combine(scratch, $"briefing-{style}-{briefing++}.bin"), message);
            var decoded = MissionBriefingInfo.Decode(message);
            File.WriteAllBytes(Path.Combine(scratch, $"briefing-{style}-{briefing - 1}.json"), JsonSerializer.SerializeToUtf8Bytes(decoded, MissionBriefingInfoJsonContext.Default.MissionBriefingInfo));
        }
File.WriteAllText(output, JsonSerializer.Serialize(new { version = 1, style = style.ToString(), recorder.Scenes, recorder.Opens, recorder.Required, recorder.RockSurfaces, recorder.FirstSector, recorder.Sectors, recorder.Visits, recorder.SectorRequired, map = "XXV_simple.igc", seed = 1051, flightSeconds = allSectors ? recorder.Visits.Sum(v => v.Seconds) : 60 }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"P1051_OPENS_PASS style={style} scenes={recorder.Scenes.Count} opens={recorder.Opens.Count}");

sealed record Open(string Source, string Origin, string Purpose, string Scene, long ByteSize, string Sha256, short? SectorId);
sealed record SectorVisit(short SectorId, string Name, double Seconds);
sealed class OpenRecorder(string style)
{
    internal readonly List<string> Scenes = new();
    public bool AllSectors { get; set; }
    public short? SectorId { get; set; }
    public Func<short?>? ResolveSector { get; set; }
    public List<DataClusterIGC> Sectors { get; } = [];
    public List<SectorVisit> Visits { get; } = [];
    public HashSet<string> Required { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RockSurfaces { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DataClusterIGC? FirstSector { get; set; }
    public Dictionary<short, HashSet<string>> SectorRequired { get; } = [];
    internal void Require(string? stem)
    {
        if (string.IsNullOrEmpty(stem)) return;
        Required.Add(stem + ".mdl");
        if (SectorId is { } id)
        {
            if (!SectorRequired.TryGetValue(id, out var names)) SectorRequired[id] = names = new(StringComparer.OrdinalIgnoreCase);
            names.Add(stem + ".mdl");
        }
    }
    internal readonly List<Open> Opens = new();
    internal string Scene = "Boot";
    internal void Enter(string scene) { Scene = scene; Scenes.Add(style + "/" + scene); }
    internal Stream Record(string source, string origin, string purpose, Stream stream)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        stream.Position = 0;
        Opens.Add(new(source.Replace('\\', '/'), origin, purpose, Scene, stream.Length, hash, ResolveSector?.Invoke()));
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
    private short _sector = -1;
    private readonly System.Diagnostics.Stopwatch _dwell = new();
    private readonly HashSet<short> _visited = [];
    private short _targetWarp = -1;
    private bool _starmapOpened;
    private bool _chartWaypoint;
    private bool _leavingMouth;
    private Dictionary<short, DataWarpIGC>? _mapWarps;
    public override void Update(GameTime gameTime)
    {
        if (_wall.Elapsed.TotalSeconds > (recorder.AllSectors ? 2400 : 100)) throw new TimeoutException("Desktop flight recording timed out.");
        var session = (ConnectionSession)typeof(Game1).GetField("_session", Hidden)!.GetValue(game)!;
        string state = typeof(Game1).GetField("_state", Hidden)!.GetValue(game)!.ToString()!;
        if (session.World.MyShipId is { } id && session.World.Ships.TryGetValue(id, out var own))
        {
            state = own.StationId >= 0 ? "Docked" : "Flight";
            if (state == "Flight")
            {
                recorder.SectorId = own.SectorId;
                if (recorder.AllSectors) VisitSectors(session, own.SectorId);
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
        if (!recorder.AllSectors && _flight >= 60) game.Exit();
        base.Update(gameTime);
    }
    private void VisitSectors(ConnectionSession session, short sectorId)
    {
        // Chart navigation selects real local alephs; the desktop's production
        // autopilot emits the same controls/pose stream as an ordinary player.
        // No ship position, sector, speed or server state is written by this tool.
        _mapWarps ??= Allegiance.Core.Model.MissionMap.Load(IgcReader.ParseFile(
            Path.Combine(AssetSources.ResolveRoot(), "XXV_simple.igc"))).Warps.ToDictionary(w => w.WarpId);
        if (_sector != sectorId)
        {
            _sector = sectorId; _targetWarp = -1; _chartWaypoint = _leavingMouth = false; _dwell.Restart();
            typeof(Game1).GetField("_autoPilotEngaged", Hidden)!.SetValue(game, false);
            typeof(Game1).GetField("_throttle", Hidden)!.SetValue(game, -1f);
            var cluster = session.World.Clusters[sectorId];
            if (recorder.Sectors.All(c => c.ClusterId != sectorId)) recorder.Sectors.Add(cluster);
            recorder.Enter("Sector/" + sectorId);
            Console.WriteLine($"P1054_ENTER sector={sectorId} name={cluster.Name}");
        }
        if (!_starmapOpened && _dwell.Elapsed.TotalSeconds >= 3)
        {
            typeof(Game1).GetField("_minimapExpanded", Hidden)!.SetValue(game, true);
            _starmapOpened = true;
        }
        if (_starmapOpened && _dwell.Elapsed.TotalSeconds >= 6)
            typeof(Game1).GetField("_minimapExpanded", Hidden)!.SetValue(game, false);
        if (_dwell.Elapsed.TotalSeconds < 30 && !_visited.Contains(sectorId)) return;
        if (_visited.Add(sectorId))
        {
            recorder.Visits.Add(new(sectorId, session.World.Clusters[sectorId].Name, _dwell.Elapsed.TotalSeconds));
            Console.WriteLine($"P1054_DWELL sector={sectorId} seconds={_dwell.Elapsed.TotalSeconds:F2}");
        }
        if (_targetWarp >= 0)
        {
            if (_chartWaypoint && session.World.Warps.ContainsKey(_targetWarp)
                && (!_leavingMouth || DistanceFromMouth(session) > _mapWarps[_targetWarp].Radius * 3))
            {
                Engage("Warp", _targetWarp); _chartWaypoint = false;
            }
            return;
        }
        var queue = new Queue<(short Sector, short First)>();
        var seen = new HashSet<short> { sectorId };
        queue.Enqueue((sectorId, -1));
        while (_targetWarp < 0 && queue.TryDequeue(out var step))
            foreach (var warp in _mapWarps.Values.Where(w => w.ClusterId == step.Sector))
            {
                if (!_mapWarps.TryGetValue(warp.DestinationId, out var destination) || !seen.Add(destination.ClusterId)) continue;
                short first = step.First < 0 ? warp.WarpId : step.First;
                if (!_visited.Contains(destination.ClusterId)) { _targetWarp = first; break; }
                queue.Enqueue((destination.ClusterId, first));
            }
        if (_targetWarp < 0) { game.Exit(); return; }
        // Give a return through the incoming mouth a normal approach: the
        // proof pilot flies clear before turning back instead of asking the
        // desktop's arrival/braking law to start inside its arrival radius.
        _leavingMouth = DistanceFromMouth(session) < _mapWarps[_targetWarp].Radius * 3;
        if (!session.World.Warps.ContainsKey(_targetWarp) || _leavingMouth)
        {
            var warp = _mapWarps[_targetWarp];
            // A real local waypoint order at the chart's coordinates, exactly
            // the native command-view position order (WinTrek/console.cpp:1940-1972;
            // clintlib/clintlib.cpp:2460-2469). Discovery remains intact.
            float[] point = warp.Position;
            if (_leavingMouth)
            {
                var own = session.World.Ships[session.World.MyShipId!.Value].Latest!.Position;
                var offset = new System.Numerics.Vector3(own.X - point[0], own.Y - point[1], own.Z - point[2]);
                if (offset.LengthSquared() < 1) throw new InvalidDataException("No clearance direction at aleph centre.");
                offset = System.Numerics.Vector3.Normalize(offset) * 1000;
                point = [point[0] + offset.X, point[1] + offset.Y, point[2] + offset.Z];
            }
            var buoy = new DataBuoyIGC(point, sectorId, DataBuoyIGC.CBuoyWaypoint, 30000, true);
            lock (session.Gate) session.World.RegisterCommandBuoy(buoy);
            session.SendChatBuoy("", Allegiance.Core.Sim.ChatTarget.Individual, session.World.MyShipId!.Value, buoy, commandId: OrderChange.CidGoto);
            Engage("Buoy", buoy.BuoyId); _chartWaypoint = true;
        }
        else Engage("Warp", _targetWarp);
    }
    private float DistanceFromMouth(ConnectionSession session)
    {
        var point = _mapWarps![_targetWarp].Position;
        var own = session.World.Ships[session.World.MyShipId!.Value].Latest!.Position;
        return new System.Numerics.Vector3(own.X - point[0], own.Y - point[1], own.Z - point[2]).Length();
    }
    private void Engage(string kind, short id)
    {
        var targetType = typeof(Game1).GetNestedType("AutopilotTarget", BindingFlags.NonPublic | BindingFlags.Public)!;
        var kindType = typeof(Game1).GetNestedType("TargetKind", BindingFlags.NonPublic | BindingFlags.Public)!;
        var target = Activator.CreateInstance(targetType, Enum.Parse(kindType, kind), id, "P1054 chart aleph", OrderChange.CidNone, true);
        typeof(Game1).GetMethod("EngageAutopilotToward", Hidden)!.Invoke(game, [target, "P1054 recorder"]);
    }

}
