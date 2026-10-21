// HUD-18 real-client proof. Drives the actual Game1 over TCP against a real
// local server and captures production 1920x1080 backbuffer frames for the
// three target-pane items this task implements:
//   (a) an enemy ship in weapon range while the own hull LACKS c_habmLeadIndicator
//       (indicator.cpp:186-228 before the :286-320 capability gate);
//   (b) a station and an asteroid in weapon range (non-ship targets, which the
//       server's old lead-message flag never covered);
//   (c) a target in another sector -> "LOCATION:<sector>" (dialog.mdl:4053-4060);
//   (d) the Modern pane's TTI line on a closing target (dialog.mdl:1280-1355).
// Run once per HUD skin:
//   dotnet run --project tools/hud18-proof -c Release -- --style Classic --out <dir>
//   dotnet run --project tools/hud18-proof -c Release -- --style Modern  --out <dir>
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
using Srv = Allegiance.Server.Server;
using SimVec = Allegiance.Core.Collision.Vector3;

string output = Path.GetFullPath(Arg("--out", ".scratch/hud18-capture"));
Directory.CreateDirectory(output);
string style = Arg("--style", "Classic");
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);
string settingsPath = Path.Combine(output, "hud.json");
File.WriteAllText(settingsPath, $"{{\"Style\":\"{style}\",\"Size\":1,\"ColorBlindSafe\":false,\"RoundRadar\":false,\"DamageWash\":true,\"ChatLines\":7}}");
Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", settingsPath);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "HUD-18 proof", Map = "m2scatter.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], GameLengthSeconds = 1800 }]
});
var mission = server.Missions[0];
mission.Stage = MissionStage.Started;
using var stop = new CancellationTokenSource();
var ticker = new Thread(() =>
{
    while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); }
});
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "HUD18 pilot",
        WindowWidth = 1920, WindowHeight = 1080,
        AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "" }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new Hud18Proof(game, server, mission, output, style));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

string Arg(string name, string fallback)
{
    for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
    return fallback;
}

sealed class Hud18Proof(Game1 game, Srv server, Mission mission, string output, string style)
    : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private Ship? _own, _enemy;
    private readonly float[] _zeroTurnRates = new float[3];
    private SimVec _posePos, _simVelocity;
    private readonly Orientation _poseOrientation = Orientation.Identity;
    private bool _poseSet, _weaponForced;
    private short _enemyId = -1, _nearStationId = -1, _asteroidId = -1, _farStationId = -1;
    private string _farSectorName = "?";
    private float _enemyDistance = 550f;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] callArgs) =>
        (typeof(Game1).GetMethod(name, Hidden) ?? throw new MissingMethodException(name)).Invoke(game, callArgs);
    private void ServerCall(string name, params object?[] callArgs) =>
        (typeof(Srv).GetMethod(name, Hidden) ?? throw new MissingMethodException("Server." + name)).Invoke(server, callArgs);

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine("HUD18_SCREEN " + name);
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

    private void SelectTarget(string kind, short id, string name)
    {
        var kindType = typeof(Game1).GetNestedType("TargetKind", BindingFlags.NonPublic)!;
        var targetType = typeof(Game1).GetNestedType("AutopilotTarget", BindingFlags.NonPublic)!;
        object k = Enum.Parse(kindType, kind);
        object value = Activator.CreateInstance(targetType, k, id, name, (short)-1, true)!;
        Set("_autoTarget", value);
        Console.WriteLine($"[hud18] selected {kind} {id} '{name}'");
    }

    private void KeepPose()
    {
        if (!_poseSet || _own is null) return;
        var sim = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");
        if (!sim.HaveHull) return;
        // Re-seed every frame with the chosen velocity: the pose stays put while
        // the HUD's GetSpeed(Me) (HudFrame.Speed = |sim velocity|) is non-zero.
        sim.Seed(_posePos, _simVelocity, _poseOrientation, _zeroTurnRates);
        // Freeze the local sim so Game1's own flight pass cannot integrate it
        // forward (and then send that drifted pose to the server) between our
        // seeds: the HUD's GetSpeed(Me) still reads Seed's velocity.
        sim.Freeze();
        _own.Position = _posePos;
        _own.Velocity = default;          // server truth: no drift, no SHIP_RESET
        _own.SetOrientation(_poseOrientation);
        if (_enemy is not null)
        {
            _enemy.Position = Relative(_enemyDistance, 0f, 0f);
            _enemy.Velocity = default;
        }
    }

    /// <summary>Force a known absolute-energy weapon (Pulse Laser: 1250 m/s,
    /// 0.5 s lifespan, power +5) on the own hull's selected mount 0, through the
    /// real ClearLoadout/MountPart path so the client receives a LOADOUT_CHANGE.</summary>
    private void ForceWeapon()
    {
        if (_weaponForced || _own is null) return;
        var part = mission.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Weapon w && w.ProjectileTypeId == 66);
        _own.ClearLoadout();
        _own.MountPart(part, 100, 0, ExpandedPartData.FullyMounted);
        _weaponForced = true;
        Console.WriteLine($"[hud18] forced weapon part {part.PartId} '{part.Name}' on mount 0");
    }

    private void DumpState(string tag)
    {
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            var world = session.World;
            if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var mine))
            { Console.WriteLine($"[hud18] {tag}: no own ship"); return; }
            var hullById = Get<Dictionary<short, DataHullTypeIGC>>("_hullById");
            hullById.TryGetValue(mine.HullId, out var hull);
            bool lead = hull.HullId != 0 && hull.HasCapability(DataHullTypeIGC.HabmLeadIndicator);
            string loadout = mine.Loadout is { } lo
                ? string.Join(",", lo.Select(p => $"part{p.PartId}@m{p.MountId}"))
                : "-";
            var leadState = world.LeadIndicator;
            Console.WriteLine($"[hud18] diag pose=({_posePos.X:0},{_posePos.Y:0},{_posePos.Z:0})"
                + $" fwd=({_poseOrientation.GetForward().X:0.00},{_poseOrientation.GetForward().Y:0.00},{_poseOrientation.GetForward().Z:0.00})"
                + $" serverOwn=({_own?.Position.X:0},{_own?.Position.Y:0},{_own?.Position.Z:0})"
                + $" serverEnemy=({_enemy?.Position.X:0},{_enemy?.Position.Y:0},{_enemy?.Position.Z:0})"
                + $" clientOwn={(mine.Latest is { } mu ? $"({mu.Position.X:0},{mu.Position.Y:0},{mu.Position.Z:0})" : "-")}"
                + $" clientEnemy={(world.Ships.TryGetValue(_enemyId, out var ce) && ce.Latest is { } eu ? $"({eu.Position.X:0},{eu.Position.Y:0},{eu.Position.Z:0}) sector={ce.SectorId}" : "-")}"
                + $" serverOwnVel=({_own?.Velocity.X:0.0},{_own?.Velocity.Y:0.0},{_own?.Velocity.Z:0.0})"
                + $" clientOwnVel={(mine.Latest is { } muv ? $"({muv.Velocity.X:0.0},{muv.Velocity.Y:0.0},{muv.Velocity.Z:0.0})" : "-")}"
                + $" serverEnemyVel=({_enemy?.Velocity.X:0.0},{_enemy?.Velocity.Y:0.0},{_enemy?.Velocity.Z:0.0})");
            Console.WriteLine($"[hud18] {tag}: own ship={me} hull={mine.HullId} '{hull.Name}' leadCapability={lead}"
                + $" loadout=[{loadout}] statusSector={mine.SectorId} leadMsg(HasLead={leadState.HasLead},InRange={leadState.InRange})"
                + $" camera={Get<object>("_cameraMode")} selectedMount={Get<int>("_selectedMount")}");
        }
    }

    /// <summary>Spawn a real, engine-ticked enemy ship that has NO NPC pilot, so
    /// it stays exactly where KeepPose puts it. SpawnDefaultShip is the
    /// player-seat spawn (PilotTypePlayer, IsNpc=false); it initially lands in
    /// the enemy's home sector, so move it into the own sector through the
    /// mission's own PlaceShip (Remove first, then PlaceShip — the P368
    /// double-registration pitfall).</summary>
    private void SpawnEnemy()
    {
        _own ??= OwnServerShip();
        if (_own?.Sector is not { } sector || _own.SideId is not { } mySide) return;
        short enemySide = mission.Sides.FirstOrDefault(s => s.Id != mySide)?.Id ?? mySide;
        short id = (short)typeof(Srv).GetMethod("AllocateShipId", Hidden)!.Invoke(server, null)!;
        object tuple = typeof(Srv).GetMethod("SpawnDefaultShip", Hidden)!
            .Invoke(server, [mission, id, (short?)enemySide])!;
        _enemy = (Ship)tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
        var home = (Sector)tuple.GetType().GetField("Item2")!.GetValue(tuple)!;
        home.Remove(_enemy);
        mission.PlaceShip(_enemy, sector);
        _enemy.Position = Relative(_enemyDistance, 0f, 0f);
        _enemyId = _enemy.Id;
        Console.WriteLine($"[hud18] enemy spawn pose=({_posePos.X:0},{_posePos.Y:0},{_posePos.Z:0}) want=({_enemy.Position.X:0},{_enemy.Position.Y:0},{_enemy.Position.Z:0})");
        Console.WriteLine($"[hud18] spawned enemy {_enemy.Id} hull '{_enemy.HullType.Name}' side={_enemy.SideId}"
            + $" IsNpc={_enemy.IsNpc} pilot={_enemy.PilotType} at {_enemyDistance} m ahead of the frozen pose");
    }

    /// <summary>Re-export an ENEMY-owned station 350 m ahead (its static record
    /// only; the sector's real station stays put). A damaging weapon is only in
    /// range on a non-friend, so the own-side Garrison would (correctly) fail
    /// the indicator.cpp:217 gate; the enemy station is the non-ship target the
    /// old ship-only lead flag never covered.</summary>
    private void SpawnStationNear()
    {
        if (_own?.Sector is not { } sector || _own.SideId is not { } mySide) return;
        Station? station = null;
        foreach (var s in mission.Sectors)
        {
            if (s.Id == sector.Id) continue;
            station = s.StationRuntimes.FirstOrDefault(st => st.SideId != mySide);
            if (station is not null) break;
        }
        if (station is not { } enemyStation) { Console.WriteLine("[hud18] no enemy station to re-export near"); return; }
        var p = Relative(350f, 0f, 0f);
        // A NEW client-visible station record in OUR sector: same type/name/side
        // as the enemy Garrison, a fresh id, ClusterId = the own sector (so
        // GetRange resolves a distance) and a position 350 m ahead.
        var data = enemyStation.Data with
        {
            StationId = mission.NextObjectId(),
            ClusterId = sector.Id,
            Position = [p.X, p.Y, p.Z], Up = [0f, 1f, 0f], Forward = [0f, 0f, -1f],
        };
        _nearStationId = data.StationId;
        ServerCall("SendSectorWide", sector, ExportStation.FromStation(data).Encode());
        Console.WriteLine($"[hud18] in-sector enemy station {data.StationId} '{data.Name}' side={data.SideId}"
            + $" cluster={data.ClusterId} re-exported 350 m ahead (wire-only, real station untouched)");
    }

    private void SpawnAsteroid()
    {
        if (_own?.Sector is not { } sector) return;
        var row = NativeAsteroidTypes.GetTypeDefaults(0);
        var p = Relative(300f, 0f, 0f);
        short id = mission.NextObjectId();
        var data = new DataAsteroidIGC(
            Signature: 10f, Position: [p.X, p.Y, p.Z], Up: [0f, 1f, 0f], Forward: [0f, 0f, -1f],
            Rotation: [0f, 0f, 0f], Ore: 0f, OreMax: 0f, AabmCapabilities: 0,
            AsteroidId: id, HitPoints: row.HitPoints, Radius: row.Radius,
            ModelName: row.ModelName, TextureName: row.TextureName, IconName: row.IconName,
            ClusterId: sector.Id, Name: "\0a5000", Fraction: 255);
        _asteroidId = id;
        ServerCall("SendSectorWide", sector, new ExportAsteroid { Asteroid = data }.Encode());
        Console.WriteLine($"[hud18] asteroid {id} radius={row.Radius} 300 m ahead");
    }

    private void FindFarStation()
    {
        if (_own?.Sector is not { } ownSector) return;
        foreach (var sector in mission.Sectors)
        {
            if (sector.Id == ownSector.Id) continue;
            var station = sector.StationRuntimes.FirstOrDefault();
            if (station is null) continue;
            _farStationId = station.StationId;
            _farSectorName = sector.Name;
            Console.WriteLine($"[hud18] far station {station.StationId} '{station.Data.Name}' in sector {sector.Id} '{sector.Name}'");
            var session = Get<ConnectionSession>("_session");
            lock (session.Gate)
                Console.WriteLine($"[hud18] client knows far station: {session.World.Stations.ContainsKey(_farStationId)}"
                    + $" cluster={session.World.Clusters.GetValueOrDefault(sector.Id).Name}");
            return;
        }
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 200) throw new TimeoutException("HUD-18 proof did not finish");
        bool flying = Get<object>("_state").ToString() == "Flying" && !Get<bool>("_dockedActive");
        if (!flying) return;
        KeepPose();
        if (_watch.Elapsed.TotalSeconds < _next) return;
        string suffix = style.ToLowerInvariant();
        switch (_step++)
        {
            case 0:
                Call("DismissBriefing");
                var sim = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");
                if (!sim.HaveHull) { _step = 0; return; }
                _posePos = sim.Position;
                _poseOrientation.Set(sim.Orientation.GetForward(), sim.Orientation.GetUp());
                _poseSet = true;
                _simVelocity = default;
                _own ??= OwnServerShip();
                ForceWeapon();
                DumpState("launch");
                _next = _watch.Elapsed.TotalSeconds + 1.2;
                return;
            case 1:
                SpawnEnemy();
                FindFarStation();
                _next = _watch.Elapsed.TotalSeconds + 1.6;
                return;
            case 2:
                // (a) enemy ship in weapon range on a hull WITHOUT the lead capability.
                SelectTarget("Ship", _enemyId, $"Ship {_enemyId}");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                return;
            case 3:
                DumpState("enemy-in-range");
                Shot($"hud18-a-enemy-in-range-{suffix}-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                return;
            case 4:
                SpawnStationNear();
                SpawnAsteroid();
                _next = _watch.Elapsed.TotalSeconds + 1.4;
                return;
            case 5:
                SelectTarget("Station", _nearStationId, $"Station {_nearStationId}");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                return;
            case 6:
                DumpState("station-in-range");
                Shot($"hud18-b-station-in-range-{suffix}-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                return;
            case 7:
                SelectTarget("Asteroid", _asteroidId, $"Asteroid {_asteroidId}");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                return;
            case 8:
                DumpState("asteroid-in-range");
                Shot($"hud18-b-asteroid-in-range-{suffix}-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                return;
            case 9:
                SelectTarget("Station", _farStationId, $"Station {_farStationId}");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                return;
            case 10:
                DumpState("other-sector");
                Shot($"hud18-c-location-{suffix}-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                return;
            case 11:
                // (d) TTI: own sim speed 100 m/s, static enemy 500 m ahead.
                _enemyDistance = 500f;
                _simVelocity = new SimVec(0f, 0f, -100f);
                SelectTarget("Ship", _enemyId, $"Ship {_enemyId}");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                return;
            case 12:
                DumpState("tti");
                Shot($"hud18-d-tti-{suffix}-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                return;
            default:
                game.Exit();
                return;
        }
    }
}
