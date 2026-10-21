// HUD-16 real-client proof. Drives the actual Game1 over TCP against a real
// local server and captures production 1920x1080 backbuffer frames that show
// the native per-contact radar icons (radarimage.cpp:591-593,417-420):
//   * an enemy ship and a friendly ship (hull-type icons, hullTypeIGC.cpp:51);
//   * a probe (probeigc.cpp:114) and a missile (missileigc.cpp:50,60);
//   * a cash treasure (treasureIGC.cpp:118-155, icon "cashicon");
//   * the sector's station (stationIGC.cpp:240-244), aleph (warpIGC.cpp:34)
//     and asteroid (asteroidigc.cpp:35), framed by moving the own ship's frozen
//     render pose in front of each.
// The contacts are real wire objects (EXPORT/TREASURE/… broadcasts) drawn by
// the production radar gather + renderer, not a synthetic contact list.
// Run once per HUD skin:
//   dotnet run --project tools/hud16-proof -- --style Classic --out <dir>
//   dotnet run --project tools/hud16-proof -- --style Modern  --out <dir>
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Srv = Allegiance.Server.Server;
using SimVec = Allegiance.Core.Collision.Vector3;

string output = Path.GetFullPath(Arg("--out", ".scratch/hud16-capture"));
Directory.CreateDirectory(output);
string style = Arg("--style", "Classic");
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);
// HUD settings are read by a field initializer when Game1 is constructed, so the
// environment must be set before `new Game1`.
string settingsPath = Path.Combine(output, "hud.json");
File.WriteAllText(settingsPath, $"{{\"Style\":\"{style}\",\"Size\":1,\"ColorBlindSafe\":false,\"RoundRadar\":false,\"DamageWash\":true,\"ChatLines\":7}}");
Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", settingsPath);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "HUD-16 proof", Map = "m2scatter.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], GameLengthSeconds = 1800 }]
});
var mission = server.Missions[0];
mission.Stage = MissionStage.Started;
using var stop = new CancellationTokenSource();
var gate = new SimGate();
var ticker = new Thread(() =>
{
    while (!stop.IsCancellationRequested)
    {
        if (!gate.Paused) server.TickSim();
        Thread.Sleep(5);
    }
});
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "HUD16 pilot",
        WindowWidth = 1920, WindowHeight = 1080,
        AutoProof = new AutoProofPlan
        {
            AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "",
        }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new Hud16Proof(game, server, mission, output, style));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

string Arg(string name, string fallback)
{
    for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
    return fallback;
}

sealed class SimGate { public volatile bool Paused = false; }

sealed class Hud16Proof(Game1 game, Srv server, Mission mission, string output, string style)
    : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private Ship? _own;
    private Ship? _enemy, _friendly;
    private Probe? _probe;
    private Missile? _missile;
    private Treasure? _treasure;
    private readonly float[] _zeroTurnRates = new float[3];
    // The frozen own-ship pose the cockpit camera and radar project from.
    private SimVec _posePos;
    private readonly Orientation _poseOrientation = Orientation.Identity;
    private bool _poseSet;
    // Per-shot framing targets found in the live sector.
    private SimVec _stationTarget, _asteroidTarget, _warpTarget;
    private bool _haveStation, _haveAsteroid, _haveWarp;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] callArgs)
    {
        var method = typeof(Game1).GetMethod(name, Hidden)
            ?? throw new MissingMethodException(name);
        return method.Invoke(game, callArgs);
    }
    private void ServerCall(string name, params object?[] callArgs)
    {
        var method = typeof(Srv).GetMethod(name, Hidden)
            ?? throw new MissingMethodException("Server." + name);
        method.Invoke(server, callArgs);
    }

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine("HUD16_SCREEN " + name);
    }

    private Ship? OwnServerShip()
    {
        var client = Get<ConnectionSession>("_session");
        short? id;
        lock (client.Gate) id = client.World.MyShipId;
        return id is { } shipId
            ? mission.Sectors.SelectMany(s => s.Models.OfType<Ship>()).FirstOrDefault(s => s.Id == shipId)
            : null;
    }

    private static SimVec Norm(SimVec v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len <= 1e-6f ? new SimVec(0, 0, -1) : new SimVec(v.X / len, v.Y / len, v.Z / len);
    }

    private static SimVec Cross(SimVec a, SimVec b)
        => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>Build a world position from the frozen pose: forward/lateral/up metres.</summary>
    private SimVec Relative(float forward, float lateral, float up)
    {
        SimVec f = Norm(_poseOrientation.GetForward());
        SimVec r = Norm(_poseOrientation.GetRight());
        SimVec u = Norm(_poseOrientation.GetUp());
        return new SimVec(
            _posePos.X + f.X * forward + r.X * lateral + u.X * up,
            _posePos.Y + f.Y * forward + r.Y * lateral + u.Y * up,
            _posePos.Z + f.Z * forward + r.Z * lateral + u.Z * up);
    }

    /// <summary>Point the own-ship render pose at a world position from a given distance.</summary>
    private void FaceTarget(SimVec target, float distance)
    {
        SimVec forward = Norm(new SimVec(target.X - _posePos.X, target.Y - _posePos.Y, target.Z - _posePos.Z));
        if (float.IsNaN(forward.X)) forward = new SimVec(0, 0, -1);
        _posePos = new SimVec(target.X - forward.X * distance, target.Y - forward.Y * distance, target.Z - forward.Z * distance);
        SimVec up = MathF.Abs(forward.Y) > 0.95f ? new SimVec(0, 0, -1) : new SimVec(0, 1, 0);
        SimVec right = Norm(Cross(forward, up));
        _poseOrientation.Set(forward, Norm(Cross(right, forward)));
        _poseSet = true;
        var got = _poseOrientation.GetForward();
        Console.WriteLine($"[hud16] facing target {target.X:0},{target.Y:0},{target.Z:0} from {_posePos.X:0},{_posePos.Y:0},{_posePos.Z:0}"
            + $" wanted fwd {forward.X:0.000},{forward.Y:0.000},{forward.Z:0.000}"
            + $" pose fwd {got.X:0.000},{got.Y:0.000},{got.Z:0.000}");
    }

    private void SpawnShips()
    {
        _own = OwnServerShip();
        if (_own?.Sector is not { } sector || _own.SideId is not { } mySide) return;
        short enemySide = mission.Sides.FirstOrDefault(s => s.Id != mySide)?.Id ?? mySide;
        var spawnMethod = typeof(Srv).GetMethod("SpawnNpcShip", Hidden)
            ?? throw new MissingMethodException("Server.SpawnNpcShip");
        Ship spawn(short side, SimVec p) => (Ship)spawnMethod.Invoke(server, [mission, sector, side, p, null])!;
        _enemy = spawn(enemySide, Relative(170f, -90f, 30f));
        _friendly = spawn(mySide, Relative(170f, 90f, 30f));
        foreach (var sh in new[] { _enemy, _friendly })
            Console.WriteLine($"[hud16] spawned ship {sh.Id} hull '{sh.HullType.Name}' icon='{sh.HullType.IconName}' side={sh.SideId}");
    }

    /// <summary>Create a real probe model in the own ship's sector and send the
    /// native EXPORT(26)/OT_probe (BroadcastExportProbe), with the own ship as
    /// the deployer so the client resolves the side.</summary>
    private void SpawnProbe()
    {
        if (_own?.Sector is not { } sector || _own.SideId is not { } mySide) return;
        var probeType = mission.Core.ProbeTypes.Values.First(p => p.IconName == "probe");
        short id = mission.NextObjectId();
        _probe = new Probe(id, sector, probeType, Relative(230f, 0f, -20f), server.SimNow, mySide, launcher: _own);
        sector.Add(_probe);
        ServerCall("BroadcastExportProbe", _probe);
        Console.WriteLine($"[hud16] probe {id} type {probeType.ExpendableTypeId} icon='{probeType.IconName}'");
    }

    /// <summary>Create a real missile aimed at the own ship and send the native
    /// FIRE_MISSILE, so the client's radar keeps it in Default mode (native only
    /// shows non-All missiles aimed at the player's side, radarimage.cpp:819-832).</summary>
    private void SpawnMissile()
    {
        if (_own?.Sector is not { } sector) return;
        var missileType = mission.Core.MissileTypes.Values.First(m => m.IconName == "missle");
        short id = mission.NextObjectId();
        _missile = new Missile(id, sector, missileType, Relative(210f, 120f, 45f),
            new SimVec(0, 0, 0), Norm(new SimVec(_posePos.X - Relative(210f, 120f, 45f).X,
                _posePos.Y - Relative(210f, 120f, 45f).Y, _posePos.Z - Relative(210f, 170f, 45f).Z)),
            server.SimNow, launcher: _own, target: _own);
        sector.Add(_missile);
        ServerCall("BroadcastFireMissile", _missile);
        Console.WriteLine($"[hud16] missile {id} type {missileType.ExpendableTypeId} icon='{missileType.IconName}'");
    }

    /// <summary>Create a real cash treasure (native icon "cashicon") at a fixed
    /// point and send the native TREASURE(28) notice.</summary>
    private void SpawnTreasure()
    {
        if (_own?.Sector is not { } sector) return;
        var p = Relative(260f, -120f, 60f);
        var data = new DataTreasureIGC(
            P0: [p.X, p.Y, p.Z], V0: [0f, 0f, 0f], Lifespan: 600f,
            Time0: (uint)(server.SimNow * 1000), ObjectId: mission.NextObjectId(),
            TreasureId: 250, ClusterId: sector.Id, Amount: 250,
            TreasureCode: TreasureCode.Cash, CreateNow: false);
        _treasure = new Treasure(data.ObjectId, sector, data, server.SimNow, mission);
        sector.Add(_treasure);
        ServerCall("SendSectorWide", sector, new TreasureSpawn { Data = data }.Encode());
        Console.WriteLine($"[hud16] cash treasure {data.ObjectId} at {p.X:0},{p.Y:0},{p.Z:0}");
    }

    /// <summary>Pin the own ship to the frozen render pose and every spawned
    /// contact to its chosen offset, so nothing drifts between spawn and shot.</summary>
    private void KeepPose()
    {
        if (!_poseSet || _own is null) return;
        var sim = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");
        if (!sim.HaveHull) return;
        sim.Seed(_posePos, default, _poseOrientation, _zeroTurnRates);
        _own.Position = _posePos;
        _own.Velocity = default;
        _own.SetOrientation(_poseOrientation);
        if (_enemy is not null) { _enemy.Position = Relative(170f, -90f, 30f); _enemy.Velocity = default; }
        if (_friendly is not null) { _friendly.Position = Relative(170f, 90f, 30f); _friendly.Velocity = default; }
        if (_probe is not null) _probe.Position = Relative(230f, 0f, -20f);
        if (_missile is not null) _missile.Position = Relative(210f, 120f, 45f);
    }

    /// <summary>The mission map ships no asteroids in this sector, so export a
    /// real OT_asteroid record (the native asteroid table's own name/model/icon)
    /// through the same EXPORT(26) path the server uses, at a fixed point ahead.</summary>
    private void SpawnAsteroid()
    {
        if (_own?.Sector is not { } sector) return;
        var row = NativeAsteroidTypes.GetTypeDefaults(0);   // huge 'meteoricon' rock
        var p = Relative(240f, -40f, -10f);
        short id = mission.NextObjectId();
        var data = new DataAsteroidIGC(
            Signature: 10f,
            Position: [p.X, p.Y, p.Z],
            Up: [0f, 1f, 0f],
            Forward: [0f, 0f, -1f],
            Rotation: [0f, 0f, 0f],
            Ore: 0f, OreMax: 0f, AabmCapabilities: 0,
            AsteroidId: id, HitPoints: row.HitPoints, Radius: row.Radius,
            ModelName: row.ModelName, TextureName: row.TextureName, IconName: row.IconName,
            ClusterId: sector.Id, Name: "\0a5000", Fraction: 255);
        _asteroidTarget = p;
        _haveAsteroid = true;
        ServerCall("SendSectorWide", sector,
            new ExportAsteroid { Asteroid = data }.Encode());
        Console.WriteLine($"[hud16] asteroid {id} '{row.Name}' icon='{row.IconName}' cached={row.ModelName} at {p.X:0},{p.Y:0},{p.Z:0}");
    }

    /// <summary>Place a real station contact in front of the frozen camera pose
    /// by re-exporting the sector station's own record at a new position (the
    /// client's predicted own pose rejects teleports, so the contacts move, not
    /// the camera — the same technique tools/hud13-proof uses for ships).</summary>
    private void SpawnStationNear()
    {
        if (_own?.Sector is not { } sector) return;
        var station = sector.StationRuntimes.FirstOrDefault();
        if (station is null) return;
        var p = Relative(320f, -150f, 20f);
        var data = station.Data with
        {
            Position = [p.X, p.Y, p.Z],
            Up = [0f, 1f, 0f],
            Forward = [0f, 0f, -1f],
        };
        _stationTarget = p;
        _haveStation = true;
        ServerCall("SendSectorWide", sector, ExportStation.FromStation(data).Encode());
        Console.WriteLine($"[hud16] station {data.StationId} '{data.Name}' icon='{station.Type.IconName}' re-exported at {p.X:0},{p.Y:0},{p.Z:0}");
    }

    /// <summary>Place a real aleph contact in front of the frozen pose by
    /// re-exporting the sector warp's record at a new position
    /// (warpDef.iconName is what the radar uses, warpIGC.cpp:34).</summary>
    private void SpawnWarpNear()
    {
        if (_own?.Sector is not { } sector) return;
        var warp = sector.Warps.FirstOrDefault(w => w.Warp.IconName.Length > 0);
        if (warp is null) return;
        var p = Relative(300f, 140f, 40f);
        var data = warp.Warp with { Position = [p.X, p.Y, p.Z] };
        _warpTarget = p;
        _haveWarp = true;
        ServerCall("SendSectorWide", sector, ExportWarp.FromWarp(new ResolvedWarp(data, warp.Destination)).Encode());
        Console.WriteLine($"[hud16] warp {data.WarpId} icon='{data.IconName}' re-exported at {p.X:0},{p.Y:0},{p.Z:0}");
    }

    /// <summary>Find the live sector's station / asteroid / aleph to frame.</summary>
    private void FindTargets()
    {
        _own ??= OwnServerShip();
        if (_own?.Sector is not { } sector) return;
        if (sector.StationRuntimes.FirstOrDefault() is { } station)
        {
            _stationTarget = new SimVec(station.Data.Position[0], station.Data.Position[1], station.Data.Position[2]);
            _haveStation = true;
            Console.WriteLine($"[hud16] station {station.StationId} '{station.Data.Name}' type={station.Data.StationTypeId} icon='{station.Type.IconName}' at {_stationTarget.X:0},{_stationTarget.Y:0},{_stationTarget.Z:0}");
        }
        if (sector.Mineable.FirstOrDefault(a => a.Radius > 100) is { } asteroid)
        {
            _asteroidTarget = new SimVec(asteroid.Data.Position[0], asteroid.Data.Position[1], asteroid.Data.Position[2]);
            _haveAsteroid = true;
            Console.WriteLine($"[hud16] asteroid {asteroid.Data.AsteroidId} '{asteroid.Data.Name}' radius={asteroid.Radius} icon='{asteroid.Data.IconName}' at {_asteroidTarget.X:0},{_asteroidTarget.Y:0},{_asteroidTarget.Z:0}");
        }
        if (sector.Warps.FirstOrDefault(w => w.Warp.IconName.Length > 0) is { } warp)
        {
            _warpTarget = new SimVec(warp.Warp.Position[0], warp.Warp.Position[1], warp.Warp.Position[2]);
            _haveWarp = true;
            Console.WriteLine($"[hud16] warp {warp.Warp.WarpId} icon='{warp.Warp.IconName}' at {_warpTarget.X:0},{_warpTarget.Y:0},{_warpTarget.Z:0}");
        }
    }

    private readonly Dictionary<string, (int X, int Y)> _markers = new();

    /// <summary>Project the live contacts exactly the way the radar gather does
    /// (the cockpit camera's matrices + RadarProjection) and remember the pixel
    /// of each named contact, so the report's zoom crops land on the icons.</summary>
    private void MarkContacts(string shot)
    {
        var session = Get<ConnectionSession>("_session");
        var sim = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");
        if (!sim.HaveHull) return;
        var simForward = sim.Orientation.GetForward();
        Console.WriteLine($"[hud16] sim {sim.Position.X:0},{sim.Position.Y:0},{sim.Position.Z:0}"
            + $" fwd {simForward.X:0.000},{simForward.Y:0.000},{simForward.Z:0.000}");
        var cam = Get<CockpitCamera>("_cockpitCamera");
        var (view, projection) = cam.Matrices(
            PoseConverter.ToXna(sim.Position), PoseConverter.ToXnaRotation(sim.Orientation),
            1920f / 1080f, out var eye);
        void Mark(string key, SimVec world)
        {
            var site = RadarProjection.Project(PoseConverter.ToXna(world), eye, view, projection, 1920f, 1080f, 14f);
            _markers[shot + "/" + key] = ((int)site.Point.X, (int)site.Point.Y);
            var rel = PoseConverter.ToXna(world) - eye;
            var cam = Vector3.Transform(rel, view);
            var clip = Vector4.Transform(new Vector4(rel, 1f), view * projection);
            Console.WriteLine($"[hud16] projected {key} -> {site.Placement} {site.Point.X:0},{site.Point.Y:0}"
                + $" rel {rel.X:0},{rel.Y:0},{rel.Z:0} cam {cam.X:0},{cam.Y:0},{cam.Z:0}"
                + $" clip {clip.X:0},{clip.Y:0},{clip.Z:0},{clip.W:0} eye {eye.X:0},{eye.Y:0},{eye.Z:0}");
        }
        if (_enemy is not null) Mark("enemy", _enemy.Position);
        if (_friendly is not null) Mark("friendly", _friendly.Position);
        lock (session.Gate)
        {
            var w = session.World;
            if (_probe is not null && w.Probes.TryGetValue(_probe.Id, out var probe))
                Mark("probe", probe.Position);
            if (_missile is not null && w.Missiles.TryGetValue(_missile.Id, out var missile))
                Mark("missile", missile.Position);
            if (_treasure is not null && w.Treasures.TryGetValue(_treasure.Id, out var treasure))
                Mark("treasure", treasure.Position);
        }
        if (_haveStation) Mark("station", _stationTarget);
        if (_haveAsteroid) Mark("asteroid", _asteroidTarget);
        if (_haveWarp) Mark("aleph", _warpTarget);
    }

    private void DumpRadarProof(string shot)
    {
        MarkContacts(shot);
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            var w = session.World;
            Console.WriteLine($"[hud16] client contacts: {string.Join(", ", w.Probes.Count + w.Missiles.Count + w.Treasures.Count)}"
                + $" probes={w.Probes.Count} missiles={w.Missiles.Count} treasures={w.Treasures.Count}"
                + $" asteroids={w.Asteroids.Count} warps={w.Warps.Count} stations={w.Stations.Count} ships={w.Ships.Count}");
        }
        string? line = Get<string?>("_radarProofLine");
        if (!string.IsNullOrEmpty(line)) Console.WriteLine("[hud16] radar proof line: " + line);
    }

    /// <summary>Save a nearest-neighbour zoom of one region of a captured
    /// frame, so the 16x16 icon art is legible in the report evidence.</summary>
    private void Zoom(string shot, int x, int y, int w, int h, int scale, string name)
    {
        string source = Path.Combine(output, shot + ".png");
        if (!File.Exists(source)) return;
        using var stream = File.OpenRead(source);
        using var texture = Texture2D.FromStream(game.GraphicsDevice, stream);
        var pixels = new Color[texture.Width * texture.Height];
        texture.GetData(pixels);
        var zoomed = new Color[w * scale * h * scale];
        for (int row = 0; row < h * scale; row++)
        {
            int sy = y + row / scale;
            if (sy < 0 || sy >= texture.Height) continue;
            for (int col = 0; col < w * scale; col++)
            {
                int sx = x + col / scale;
                if (sx < 0 || sx >= texture.Width) continue;
                zoomed[row * w * scale + col] = pixels[sy * texture.Width + sx];
            }
        }
        using var outTexture = new Texture2D(game.GraphicsDevice, w * scale, h * scale);
        outTexture.SetData(zoomed);
        using var outStream = File.Create(Path.Combine(output, name + ".png"));
        outTexture.SaveAsPng(outStream, outTexture.Width, outTexture.Height);
        Console.WriteLine("[hud16] zoom saved: " + name);
    }

    /// <summary>Save the decoded native icon art for the contact kinds this
    /// run shows, so the report can compare the screenshots against the real
    /// surfaces (8x nearest-neighbour zoom).</summary>
    private void DumpIconArt()
    {
        var textures = Get<RadarIconTextures?>("_radarIconTextures");
        if (textures is null) return;
        var byStem = (Dictionary<string, Texture2D?>)typeof(RadarIconTextures)
            .GetField("_byStem", Hidden)!.GetValue(textures)!;
        foreach (var stem in new[] { "dn_himnradv", "dn_himnrbmp", "probe", "missle", "cashicon",
                     "icontech", "dn_hsgar", "aleph", "meteoricon", "dn_hifig", "iconpubmp", "minefield" })
        {
            if (!byStem.TryGetValue(stem + "bmp", out var texture) || texture is null) continue;
            var pixels = new Color[texture.Width * texture.Height];
            texture.GetData(pixels);
            const int scale = 8;
            var big = new Color[pixels.Length * scale * scale];
            for (int row = 0; row < texture.Height * scale; row++)
                for (int col = 0; col < texture.Width * scale; col++)
                    big[row * texture.Width * scale + col] = pixels[(row / scale) * texture.Width + col / scale];
            using var art = new Texture2D(game.GraphicsDevice, texture.Width * scale, texture.Height * scale);
            art.SetData(big);
            using var stream = File.Create(Path.Combine(output, "art-" + stem + ".png"));
            art.SaveAsPng(stream, art.Width, art.Height);
            Console.WriteLine("[hud16] art saved: " + stem);
        }
    }

    /// <summary>Zoom a 260x190 box centred on a projected contact pixel.</summary>
    private void ZoomMarker(string shot, string key, string name)
    {
        if (!_markers.TryGetValue(shot + "/" + key, out var at)) return;
        Zoom(shot, at.X - 130, at.Y - 150, 260, 190, 5, name);
    }

    /// <summary>Zoom a wide box (large contacts have a big native icon offset:
    /// min(distanceToEdge, radiusBracket) can be 100+ px).</summary>
    private void ZoomWide(string shot, string key, string name)
    {
        if (!_markers.TryGetValue(shot + "/" + key, out var at)) return;
        Zoom(shot, at.X - 240, at.Y - 240, 480, 360, 3, name);
    }

    /// <summary>Re-encode every capture as a small JPEG so the report evidence
    /// stays inside the repository's per-task size budget (the PNG originals go
    /// to ~/allegiance-evidence/HUD16/).</summary>
    private void SaveJpegs()
    {
        string suffix = style.ToLowerInvariant();
        foreach (var stem in new[] { "ships", "probe", "treasure", "missile", "station", "aleph" })
        {
            string source = Path.Combine(output, $"radar-icons-{stem}-{suffix}-1920x1080.png");
            if (!File.Exists(source)) continue;
            using var stream = File.OpenRead(source);
            using var texture = Texture2D.FromStream(game.GraphicsDevice, stream);
            using var outStream = File.Create(Path.Combine(output, $"radar-icons-{stem}-{suffix}-1920x1080.jpg"));
            texture.SaveAsJpeg(outStream, texture.Width, texture.Height);
        }
    }

    private void SaveZooms()
    {
        string suffix = style.ToLowerInvariant();
        string ships = $"radar-icons-ships-{suffix}-1920x1080";
        ZoomMarker(ships, "enemy", $"zoom-enemy-ship-{suffix}");
        ZoomMarker(ships, "friendly", $"zoom-friendly-ship-{suffix}");
        ZoomMarker($"radar-icons-probe-{suffix}-1920x1080", "probe", $"zoom-probe-{suffix}");
        ZoomMarker($"radar-icons-treasure-{suffix}-1920x1080", "treasure", $"zoom-treasure-{suffix}");
        ZoomMarker($"radar-icons-missile-{suffix}-1920x1080", "missile", $"zoom-missile-{suffix}");
        ZoomWide($"radar-icons-station-{suffix}-1920x1080", "station", $"zoom-station-{suffix}");
        ZoomMarker($"radar-icons-ships-{suffix}-1920x1080", "asteroid", $"zoom-asteroid-{suffix}");
        ZoomWide($"radar-icons-aleph-{suffix}-1920x1080", "aleph", $"zoom-aleph-{suffix}");
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 180) throw new TimeoutException("HUD-16 proof did not finish");
        bool flying = Get<object>("_state").ToString() == "Flying" && !Get<bool>("_dockedActive");
        if (!flying) return;
        KeepPose();
        if (_watch.Elapsed.TotalSeconds < _next) return;
        switch (_step++)
        {
            case 0:
                Call("DismissBriefing");
                var sim = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");
                if (!sim.HaveHull) { _step = 0; return; }
                _posePos = sim.Position;
                _poseOrientation.Set(sim.Orientation.GetForward(), sim.Orientation.GetUp());
                _poseSet = true;
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                return;
            case 1:
                SpawnShips();
                SpawnAsteroid();
                FindTargets();
                _next = _watch.Elapsed.TotalSeconds + 1.4;
                return;
            case 2:
            {
                string shot = $"radar-icons-ships-{style.ToLowerInvariant()}-1920x1080";
                DumpRadarProof(shot);
                Shot(shot);
                _next = _watch.Elapsed.TotalSeconds + 0.8;
            }
                return;
            case 3:
                SpawnProbe();
                _next = _watch.Elapsed.TotalSeconds + 1.2;
                return;
            case 4:
            {
                string shot = $"radar-icons-probe-{style.ToLowerInvariant()}-1920x1080";
                DumpRadarProof(shot);
                Shot(shot);
                _next = _watch.Elapsed.TotalSeconds + 0.8;
            }
                return;
            case 5:
                SpawnTreasure();
                _next = _watch.Elapsed.TotalSeconds + 1.2;
                return;
            case 6:
            {
                string shot = $"radar-icons-treasure-{style.ToLowerInvariant()}-1920x1080";
                DumpRadarProof(shot);
                Shot(shot);
                _next = _watch.Elapsed.TotalSeconds + 0.8;
            }
                return;
            case 7:
                SpawnMissile();
                _next = _watch.Elapsed.TotalSeconds + 0.2;
                return;
            case 8:
            {
                string shot = $"radar-icons-missile-{style.ToLowerInvariant()}-1920x1080";
                DumpRadarProof(shot);
                Shot(shot);
                _next = _watch.Elapsed.TotalSeconds + 0.8;
            }
                return;
            case 9:
                SpawnStationNear();
                _next = _watch.Elapsed.TotalSeconds + 1.2;
                return;
            case 10:
            {
                string shot = $"radar-icons-station-{style.ToLowerInvariant()}-1920x1080";
                DumpRadarProof(shot);
                Shot(shot);
                _next = _watch.Elapsed.TotalSeconds + 0.8;
            }
                return;
            case 11:
                SpawnWarpNear();
                _next = _watch.Elapsed.TotalSeconds + 1.2;
                return;
            case 12:
            {
                string shot = $"radar-icons-aleph-{style.ToLowerInvariant()}-1920x1080";
                DumpRadarProof(shot);
                Shot(shot);
                _next = _watch.Elapsed.TotalSeconds + 0.8;
            }
                return;
            default:
                DumpIconArt();
                SaveZooms();
                SaveJpegs();
                game.Exit();
                return;
        }
    }
}
