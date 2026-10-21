// HUD-20 real-client proof. Drives the actual Game1 over TCP against a real
// local server and captures production 1920x1080 backbuffer frames for the
// remaining HUD art gaps:
//   (a) the F4 inventory cloak row with native "N% / T sec"
//       (invcloak.mdl:10-23, consoledata.cpp:480-490);
//   (b) docked command view "Currently viewing:" + the red viewed-sector name
//       (dialog.mdl:1429-1471,5421-5422, WinTrek.cpp:6831-6840);
//   (d) the target pane's native kill-bonus art/number on an enemy ship, and
//       the same pane on a station with no readout
//       (dialog.mdl:4192-4214,4232, consoledata.cpp:831-843).
// The enemy's experience is the live PLAYERINFO seed: P775 sends PLAYERINFO
// only at the native moments — the proof's dummy contacts are announced to
// the whole mission at creation via Server.AnnounceNewShip (native moment (3),
// FedSrv.CPP:2269; the join-burst loop fsmission.cpp:3958-3968), never on
// first sighting.
// Run once per HUD skin:
//   dotnet run --project tools/hud20-proof -c Release -- --style Classic --out <dir>
//   dotnet run --project tools/hud20-proof -c Release -- --style Modern  --out <dir>
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Srv = Allegiance.Server.Server;
using SimVec = Allegiance.Core.Collision.Vector3;

string output = Path.GetFullPath(Arg("--out", ".scratch/hud20-capture"));
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
    Games = [new GameConfig { Name = "HUD-20 proof", Map = "m2xspiderw.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], GameLengthSeconds = 1800 }]
});
var mission = server.Missions[0];
mission.Stage = MissionStage.Started;
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); } });
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "HUD20 pilot",
        WindowWidth = 1920, WindowHeight = 1080,
        AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true },   // stay docked first
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new Hud20Proof(game, server, mission, output));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

string Arg(string name, string fallback)
{
    for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
    return fallback;
}

sealed class Hud20Proof(Game1 game, Srv server, Mission mission, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private Ship? _own;
    private Ship? _enemy;
    private Ship? _friendly;
    private short _observerId = -1;
    private readonly float[] _zeroTurnRates = new float[3];

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object?[] callArgs) =>
        (typeof(Game1).GetMethod(name, Hidden) ?? throw new MissingMethodException(name)).Invoke(game, callArgs);
    private object? ServerCall(string name, params object?[] callArgs) =>
        (typeof(Srv).GetMethod(name, Hidden) ?? throw new MissingMethodException("Server." + name)).Invoke(server, callArgs);

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine("HUD20_SCREEN " + name);
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

    private SimVec Ahead(float distance)
    {
        var forward = Norm(_own?.Orientation.GetForward() ?? new SimVec(0, 0, -1));
        var position = _own?.Position ?? default;
        return new SimVec(position.X + forward.X * distance, position.Y + forward.Y * distance, position.Z + forward.Z * distance);
    }

    private void SpawnContacts()
    {
        _own = OwnServerShip();
        if (_own?.Sector is not { } sector || _own.SideId is not { } mySide) return;
        short enemySide = mission.Sides.FirstOrDefault(s => s.Id != mySide)?.Id ?? mySide;
        // Inert dummy ships: PilotTypePlayer with no peer connection, so the
        // server's AI loop (which only drives pilotType < c_ptPlayer) leaves
        // them stationary; an aggressive miner NPC shot the own ship down in
        // the first run and invalidated the target-pane shot.
        var hull = mission.Core.HullTypes.Values.First(h => h.MaxFixedWeapons > 0);
        Ship Spawn(short side, SimVec p)
        {
            var ship = new Ship(mission.NextObjectId(), hull, p, SimVec.Zero, Orientation.Identity)
            {
                SideId = side,
                PilotType = Allegiance.Core.Sim.Ship.PilotTypePlayer,
            };
            mission.PlaceShip(ship, sector);
            return ship;
        }
        _enemy = Spawn(enemySide, Ahead(260f));
        _friendly = Spawn(mySide, Ahead(320f));
        // A real kill streak: 3 cross-side kills = experience 3, the value the
        // PLAYERINFO announce carries on the client (FedSrv.CPP:2084). P775 —
        // the dummies bypass join and SpawnNpcShip, so announce them explicitly
        // (the native drone-creation moment, FedSrv.CPP:2269) AFTER the kill
        // bonuses, so the seed carries the real experience. Without this the
        // client would never learn them: the per-sighting PLAYERINFO seed is
        // gone with P775.
        _enemy.AddKillBonus();
        _enemy.AddKillBonus();
        _enemy.AddKillBonus();
        // Reflection like the rest of this tool (Server internals are not
        // InternalsVisibleTo'd to the proof exes).
        ServerCall("AnnounceNewShip", _enemy);
        ServerCall("AnnounceNewShip", _friendly);
        Console.WriteLine($"[hud20] enemy {_enemy.Id} side {enemySide} hull '{hull.Name}' experience {_enemy.KillBonus}; friendly {_friendly.Id}");
    }

    private void SelectTarget(Ship target)
    {
        var kindType = typeof(Game1).GetNestedType("TargetKind", BindingFlags.NonPublic)
            ?? throw new MissingMemberException("Game1.TargetKind");
        var targetType = typeof(Game1).GetNestedType("AutopilotTarget", BindingFlags.NonPublic)
            ?? throw new MissingMemberException("Game1.AutopilotTarget");
        object kind = Enum.Parse(kindType, "Ship");
        object value = Activator.CreateInstance(targetType,
            kind, target.Id, $"Ship {target.Id}", (short)-1, true)!;
        Set("_autoTarget", value);
        Console.WriteLine($"[hud20] selected enemy ship {target.Id}");
    }

    private void SelectStation(short stationId, string name)
    {
        var kindType = typeof(Game1).GetNestedType("TargetKind", BindingFlags.NonPublic)!;
        var targetType = typeof(Game1).GetNestedType("AutopilotTarget", BindingFlags.NonPublic)!;
        object kind = Enum.Parse(kindType, "Station");
        object value = Activator.CreateInstance(targetType,
            kind, stationId, name, (short)-1, true)!;
        Set("_autoTarget", value);
        Console.WriteLine($"[hud20] selected station {stationId} '{name}'");
    }

    private void PinPose()
    {
        if (_own is null) return;
        var sim = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");
        if (sim.HaveHull) sim.Seed(_own.Position, default, _own.Orientation, _zeroTurnRates);
        // The dummies never move; do NOT teleport them (the first run put an
        // enemy on top of the own ship).
        if (_enemy is not null) _enemy.Velocity = SimVec.Zero;
        if (_friendly is not null) _friendly.Velocity = SimVec.Zero;
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 180) throw new TimeoutException("HUD-20 proof did not finish");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        var session = Get<ConnectionSession>("_session");
        bool docked = Get<bool>("_dockedActive");
        bool flying = Get<object>("_state").ToString() == "Flying" && !docked;
        switch (_step)
        {
            case 0:
                // Docked hangar (no launch): does the console layer draw here?
                if (!docked) { _next = _watch.Elapsed.TotalSeconds + 0.5; return; }
                Call("DismissBriefing");
                _own = OwnServerShip();
                // Board one same-side NPC as an OBSERVER while both ships are
                // docked, through the real PerformBoard path (FedSrv.CPP:2340-
                // 2446). The Scout carrier has no turret mounts, so ChooseTurretSlot
                // returns null -> TurretId null -> PassengerData turretID NA/0xFF
                // (HUD-20's server fix). The NPC is exported to us by the
                // same-side NPC path, so the client's passenger loop can store it.
                if (_own is { SideId: { } boardSide, Sector: { } boardSector, StationId: { } boardStation })
                {
                    var boardHull = mission.Core.HullTypes.Values.First(h => h.MaxWeapons <= h.MaxFixedWeapons);
                    var observer = new Ship(mission.NextObjectId(), boardHull, _own.Position,
                        SimVec.Zero, Orientation.Identity)
                    {
                        SideId = boardSide, IsNpc = true, DroneTypeId = -1,
                        // PilotTypePlayer: the AI loop only drives pilotType <
                        // c_ptPlayer, so the observer stays put instead of
                        // warping away and unboarding (the first run).
                        PilotType = Allegiance.Core.Sim.Ship.PilotTypePlayer,
                    };
                    mission.PlaceShip(observer, boardSector);
                    observer.StationId = boardStation;
                    // P775 — the observer dummy bypasses join; announce it like
                    // any newly created player (FedSrv.CPP:2269) so the client
                    // keeps a PlayerInfo for it.
                    ServerCall("AnnounceNewShip", observer);
                    bool boarded = (bool)ServerCall("PerformBoard", observer, _own)!;
                    _observerId = observer.Id;
                    Console.WriteLine($"[hud20] observer {observer.Id} board -> {boarded} (turret {(observer.TurretId?.ToString() ?? "NA")}, station {boardStation})");
                }
                Shot("a-docked-hangar");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                _step++;
                return;
            case 1:
                // Real hangar Command verb: command view + a VIEW_CLUSTER request.
                var own = OwnServerShip();
                if (own?.StationId is not { } stationId) { _step = 0; return; }
                Call("ActivateHangar", HangarAction.Command);
                // View a different sector than the station's, through the real
                // client request path (Server.ShipCommands.HandleViewCluster).
                short stationSector = own.Sector?.Id ?? -1;
                short other = session.World.Clusters.Keys.Where(c => c != stationSector).OrderBy(c => c).FirstOrDefault();
                if (other != 0 && other != stationSector)
                {
                    session.SendViewClusterRequest(other);
                    Console.WriteLine($"[hud20] station sector {stationSector}, viewing sector {other}");
                }
                _next = _watch.Elapsed.TotalSeconds + 2.0;
                _step++;
                return;
            case 2:
                lock (session.Gate)
                    Console.WriteLine($"[hud20] commanded viewedCluster={session.World.ViewedClusterId}, camera={Get<CameraMode>("_cameraMode")}");
                Shot("b-currently-viewing");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                _step++;
                return;
            case 3:
                // Launch from the hangar through the real verb.
                if (docked)
                {
                    var me = OwnServerShip();
                    if (me is null) { _next = _watch.Elapsed.TotalSeconds + 0.5; return; }
                    Call("LaunchFromHangar", session.World.Ships[me.Id]);
                }
                _next = _watch.Elapsed.TotalSeconds + 2.5;
                _step++;
                return;
            case 4:
                if (!flying) { _next = _watch.Elapsed.TotalSeconds + 0.5; return; }
                Set("_cameraMode", CameraMode.Cockpit);
                SpawnContacts();
                // Mount a low-draw cloak early so its owner-only
                // OwnShipCloakUpdate ramp has settled by the inventory shot,
                // and engage it through the real client latch.
                if (_own is not null)
                {
                    // A REAL core cloak part: the client's inventory resolves the
                    // row's name/stats from its shop catalogue, so a synthetic
                    // part id draws no row at all. The launch hull (a Scout) has
                    // no cloak slot in its PmEquipment mask, and the inventory
                    // honours that mask (SlotMask, inventoryimage.cpp:106-114), so
                    // change to a hull that really can cloak first.
                    var cloak = mission.Core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Cloak);
                    var cloakHull = mission.Core.HullTypes.Values.First(h =>
                        h.MaxFixedWeapons > 0 && h.PmEquipment.Length > (int)EquipmentType.Cloak
                        && h.PmEquipment[(int)EquipmentType.Cloak] != 0);
                    _own.ChangeHull(cloakHull);
                    Console.WriteLine($"[hud20] hull -> '{cloakHull.Name}' (cloak mask {cloakHull.PmEquipment[(int)EquipmentType.Cloak]})");
                    _own.MountPart(cloak, 1, mountId: 0, fractionMounted: 255);
                    _own.Energy = _own.HullType.MaxEnergy * 100f;
                    _own.StateM |= ShipControlState.CloakActive;
                    Console.WriteLine($"[hud20] mounted core cloak part {cloak.PartId} '{cloak.Name}'");
                }
                Set("_cloakToggled", true);
                _next = _watch.Elapsed.TotalSeconds + 3.0;   // exports + PLAYERINFO seed + telemetry
                _step++;
                return;
            case 5:
                PinPose();
                if (_enemy is null) { _step = 4; return; }
                SelectTarget(_enemy);
                lock (session.Gate)
                    if (session.World.Ships.TryGetValue(_enemy.Id, out var ee))
                        Console.WriteLine($"[hud20] client enemy {ee.ShipId} player={ee.Player is not null} experience={ee.Experience}");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                _step++;
                return;
            case 6:
                Shot("d-target-killbonus");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                _step++;
                return;
            case 7:
                var st = _own?.Sector?.StationRuntimes.FirstOrDefault();
                if (st is not null) SelectStation(st.StationId, st.Data.Name);
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                _step++;
                return;
            case 8:
                Shot("d-target-station");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                _step++;
                return;
            case 9:
                // Cloak engaged through the real client latch; mount a low-draw
                // cloak so the owner-only OwnShipCloakUpdate carries the ramp.
                if (_own is not null)
                {
                    var cloak = mission.Core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Cloak);
                    var cloakHull = mission.Core.HullTypes.Values.First(h =>
                        h.MaxFixedWeapons > 0 && h.PmEquipment.Length > (int)EquipmentType.Cloak
                        && h.PmEquipment[(int)EquipmentType.Cloak] != 0);
                    _own.ChangeHull(cloakHull);
                    _own.MountPart(cloak, 1, mountId: 0, fractionMounted: 255);
                    _own.Energy = _own.HullType.MaxEnergy * 100f;
                    _own.StateM |= ShipControlState.CloakActive;
                }
                Set("_cloakToggled", true);
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                _step++;
                return;
            case 10:
                Call("ApplyConsoleMode", "ConModeInventory", false);
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                _step++;
                return;
            case 11:
                lock (session.Gate)
                {
                    Console.WriteLine($"[hud20] cloak: client MyCloaking={session.World.MyCloaking}");
                    short source = session.World.MyShipId is { } myId
                        && session.World.Ships[myId].ParentId >= 0 ? session.World.Ships[myId].ParentId
                        : session.World.MyShipId ?? -1;
                    int observers = session.World.Ships.Values.Count(s =>
                        s.ParentId == source && s.BoardTurretId == Allegiance.ClientCore.State.ShipEntity.BoardTurretObserver);
                    Console.WriteLine($"[hud20] client observer children of {source}: {observers}");
                    if (session.World.Ships.TryGetValue(_observerId, out var obsEnt))
                        Console.WriteLine($"[hud20] client observer {_observerId}: parent={obsEnt.ParentId} boardTurret={obsEnt.BoardTurretId} player={obsEnt.Player is not null} name='{obsEnt.CharacterName}'");
                    else
                        Console.WriteLine($"[hud20] client has no entity for observer {_observerId}");
                }
                Shot("a-inventory-cloak");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                _step++;
                return;
            default:
                game.Exit();
                return;
        }
    }
}
