// HUD-12 real-client proof. Drives the actual Game1 over TCP against a real
// local server and captures production backbuffer frames for the rows that were
// blocked on missing client data / native art:
//   * radar native bracket art: carried flag, carried artifact, command-chat
//     subjects (command view + composing), drone ripcord, friendly ripcord
//     seconds, threat last-fired (radarimage.cpp:343-357,999-1030);
//   * native lives/kills badges (console.cpp:812-821) from MISSIONDEF iLives +
//     real KILL_SHIP counters;
//   * continuous cloak strength + the decloak-tail cloak icon
//     (consoledata.cpp:480-489,563-575) from the owner-only cloak stream;
//   * the native command-view ship summary (cortCommandModeInfo).
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

Environment.SetEnvironmentVariable("ALLEGIANCE_RADAR_PROOF", "1");
string output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? ".scratch/hud12-capture");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "HUD-12 proof", Map = "m2scatter.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], GameLengthSeconds = 1800 }]
});
var mission = server.Missions[0];
// HUD-12 — a finite mission life budget (native MissionParams::iLives) so the
// lives badge (shown only while remaining < 10) is exercised, and deathmatch
// kills (IsDeathMatchGame) so the kills badge is shown.
mission.Lives = 5;
mission.KillsEnabled = true;
mission.KillsWinGoal = 50;
mission.Stage = MissionStage.Started;
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); } });
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "HUD12 pilot",
        WindowWidth = 1920, WindowHeight = 1080,
        AutoProof = new AutoProofPlan
        {
            AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "",
        }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new Hud12Proof(game, server, mission, output));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

sealed class Hud12Proof(Game1 game, Srv server, Mission mission, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private Ship? _own;
    private readonly List<Ship> _friends = [];
    private readonly List<Ship> _enemies = [];
    private Ship? _enemyAttacker;
    private static readonly DataPartTypeIGC ProofCloak = new(
        Price: 9810, TimeToBuild: 440, ModelName: "bcl1gncr.igc", IconName: "",
        Name: "HUD-12 proof cloak", Description: "", GroupId: 0,
        TtbmRequired: [], TtbmEffects: [],
        Mass: 966f, Signature: 0.0f, PartId: 9821, SuccessorPartId: -1, PartMask: 983,
        InventoryLineMdl: "", Type: new DataPartTypeIGC.Cloak(
            EnergyConsumption: 0.005f, MaxCloaking: 0.994f, OnRate: 2.0f, OffRate: 0.5f,
            EngageSound: -1, DisengageSound: -1));

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] args)
    {
        var method = typeof(Game1).GetMethod(name, Hidden)
            ?? throw new MissingMethodException(name);
        return method.Invoke(game, args);
    }
    private object? ServerCall(string name, params object[] args)
    {
        var method = typeof(Srv).GetMethod(name, Hidden)
            ?? throw new MissingMethodException("Server." + name);
        return method.Invoke(server, args);
    }

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine("HUD12_SCREEN " + name);
    }

    private Ship? FindShip(short id) => mission.Sectors.SelectMany(s => s.Models.OfType<Ship>())
        .FirstOrDefault(s => s.Id == id);

    private Ship? OwnServerShip()
    {
        var client = Get<ConnectionSession>("_session");
        short? id;
        lock (client.Gate) id = client.World.MyShipId;
        return id is { } shipId ? FindShip(shipId) : null;
    }

    private void SpawnContacts()
    {
        _own = OwnServerShip();
        if (_own?.Sector is not { } sector || _own.SideId is not { } mySide) return;
        short enemySide = mission.Sides.FirstOrDefault(s => s.Id != mySide)?.Id ?? mySide;
        var spawnMethod = typeof(Srv).GetMethod("SpawnNpcShip", Hidden)
            ?? throw new MissingMethodException("Server.SpawnNpcShip");
        Ship spawn(Mission m, Sector s, short side, SimVec p) =>
            (Ship)spawnMethod.Invoke(server, [m, s, side, p, null])!;
        // Friendly contacts around the own ship (the command-chat subject set).
        for (int i = 0; i < 3; i++)
        {
            var p = _own.Position + new SimVec(700f + i * 350f, 120f * i, 500f - i * 250f);
            _friends.Add(spawn(mission, sector, mySide, p));
        }
        // Enemy contacts: one attacker (threat) + four kill-credit victims.
        for (int i = 0; i < 5; i++)
        {
            var p = _own.Position + new SimVec(-700f - i * 320f, 100f * i, -400f + i * 180f);
            _enemies.Add(spawn(mission, sector, enemySide, p));
        }
        _enemyAttacker = _enemies[0];
        foreach (var friend in _friends)
            Console.WriteLine($"[hud12] friend {friend.Id} hull '{friend.HullType.Name}' ripcordSpeed={friend.RipcordSpeed}");
        Console.WriteLine($"[hud12] own {_own.Id} hull '{_own.HullType.Name}' ripcordSpeed={_own.RipcordSpeed}");
        Console.WriteLine($"[hud12] spawned {_friends.Count} friends + {_enemies.Count} enemies around ship {_own.Id} side {mySide}");
    }

    private void Command(string name, params object?[] args) => ServerCall(name, args!);

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 140) throw new TimeoutException("HUD-12 proof did not finish");
        bool flying = Get<object>("_state").ToString() == "Flying" && !Get<bool>("_dockedActive");
        if (!flying || _watch.Elapsed.TotalSeconds < _next) return;
        var session = Get<ConnectionSession>("_session");
        switch (_step++)
        {
            case 0:
                SpawnContacts();
                _next = _watch.Elapsed.TotalSeconds + 2.5;   // let exports/status/telemetry settle
                return;
            case 1:
                // The briefing popup is auto-dismissed by real input; do it
                // through the production method so it never covers the capture.
                Call("DismissBriefing");
                if (_own is null || _friends.Count < 3 || _enemies.Count < 5) { _step = 0; return; }
                // Real GAIN_FLAG broadcasts: friend 0 carries a flag, friend 1 an
                // artifact (SideidFlag -2 = SIDE_TEAMLOBBY sentinel).
                Command("BroadcastGainFlag", mission, (short)0, _friends[0].Id, false, (short)0);
                Command("BroadcastGainFlag", mission, (short)-2, _friends[1].Id, false, (short)0);
                // Friend 2 is a drone ripcording: the native StateM bit.
                _friends[2].StateM |= ShipControlState.DroneRip;
                // A second friend (re-use friend 2) also has an active ripcord so
                // the seconds label has a real sector-wide RIPCORD_ACTIVATE.
                if (_own.Sector is { } sector)
                {
                    var activate = new RipcordActivate
                    {
                        ShipidRipcord = _friends[2].Id, OtRipcord = 1, OidRipcord = -1,
                        SidRipcord = sector.Id,
                    };
                    // The production sector-wide send (Server.Docking.cs:427-445);
                    // the peer dictionary gives the same audience as
                    // GetGroupSectorFlying for this one-pilot proof arena.
                    var peers = (System.Collections.IDictionary)typeof(Srv)
                        .GetField("_peerShips", Hidden)!.GetValue(server)!;
                    var sendPeer = typeof(Srv).GetMethods(Hidden).Single(m => m.Name == "SendPeer"
                        && m.GetParameters().Length == 2
                        && m.GetParameters()[1].ParameterType == typeof(byte[]));
                    foreach (var peer in peers.Keys)
                        sendPeer.Invoke(server, [peer, FedPacket.Pack([activate.Encode()])]);
                    Console.WriteLine($"[hud12] sent RIPCORD_ACTIVATE for friend {_friends[2].Id} to {peers.Count} peer(s)");
                }
                if (_friends.Count >= 3)
                {
                    var world = session.World;
                    lock (session.Gate)
                    {
                        if (world.Ships.TryGetValue(_friends[2].Id, out var fe))
                        {
                            var catalog = typeof(Game1).GetField("_shopCatalog", Hidden)!.GetValue(game)
                                as ShopCatalog;
                            var hull = catalog?.Hulls.FirstOrDefault(h => h.HullId == fe.HullId);
                            var durMi = typeof(Game1).GetMethod("RipcordDurationSecondsFor", Hidden)!;
                            float dur = (float)durMi.Invoke(game, [world, fe])!;
                            bool hasRip = world.TryGetRemoteRipcord(fe.ShipId, out long ripStart);
                            Console.WriteLine($"[hud12] client friend {fe.ShipId} hullId={fe.HullId} side={fe.SideId} catalogName='{hull?.Name}' catalogRipcordSpeed={hull?.RipcordSpeed} ga={world.GlobalAttributesFor(fe.SideId).Get(GlobalAttributeSet.RipcordTime)} duration={dur} hasRip={hasRip} start={ripStart} now={world.NowSeconds() * 1000:0}");
                            foreach (var f2 in _friends)
                                Console.WriteLine($"[hud12] remote-rip {f2.Id}: {world.TryGetRemoteRipcord(f2.Id, out long st)} start={st}");
                        }
                    }
                }
                _next = _watch.Elapsed.TotalSeconds + 1;
                return;
            case 2:
                // Command view, chat NOT composing: the flag/artifact/ripcord
                // brackets without the subject set overlapping them.
                Call("ApplyConsoleMode", "ViewCommand", true);
                Set("_cameraMode", CameraMode.Command);
                Set("_commandCentered", false);
                Set("_chatActive", false);
                _next = _watch.Elapsed.TotalSeconds + 0.7;
                return;
            case 3:
                Shot("radar-native-markers-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.4;
                return;
            case 4:
                // A composing team chat = the console's subject list
                // (GetSubjects) is non-null, exactly the native subject gate.
                Set("_chatActive", true);
                Set("_chatTarget", ChatTarget.Team);
                _next = _watch.Elapsed.TotalSeconds + 0.5;
                return;
            case 5:
                Shot("radar-subjects-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.5;
                return;
            case 6:
                if (_enemyAttacker is not null && _own is not null)
                {
                    // A real VICTIM_DAMAGED(217) attribution to our own peer: the
                    // attacker's DamageBucket becomes a threat and flashes red for
                    // 250 ms (radarimage.cpp:660-671).
                    _own.ReceiveDamage(0, 12f, mission.LastUpdate, _own.Position,
                        _own.Position, _enemyAttacker);
                    // The production unicast the collision/projectile path raises
                    // when a ship round hits the local ship (Sector.cs:2151 →
                    // Server.BroadcastVictimDamaged), so the client's damage
                    // bucket for the attacker is populated exactly as in play.
                    Command("BroadcastVictimDamaged", _enemyAttacker.Id, _own.Id, 12f);
                }
                Set("_chatActive", false);
                Set("_cameraMode", CameraMode.Cockpit);
                _next = _watch.Elapsed.TotalSeconds + 0.05;
                return;
            case 7:
            case 8:
            case 9:
            case 10:
            case 11:
            case 12:
                Shot($"radar-threat-flash-{_step - 6}-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.07;
                return;
            case 13:
                // Real credited kills: our own ship's launcher destroys four enemy
                // NPCs → the server broadcasts KILL_SHIP(105) with kill credit and
                // the client's PlayerInfo::AddKill (appmsg.cpp:2807-2810).
                if (_own is not null)
                    foreach (var victim in _enemies.Skip(1))
                        victim.ReceiveDamage(0, victim.EffectiveMaxHitPoints() * 2f,
                            mission.LastUpdate, victim.Position, victim.Position, _own);
                _next = _watch.Elapsed.TotalSeconds + 1.5;
                return;
            case 14:
                Shot("lives-kills-badges-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.7;
                return;
            case 15:
                // Cloak: mount a low-draw cloak on the real server ship and engage
                // it through the real client control path (the F-key path), so the
                // owner-only OwnShipCloakUpdate carries the sim's ramp.
                if (_own is not null)
                {
                    _own.MountPart(ProofCloak, 1, mountId: 0, fractionMounted: 255);
                    _own.Energy = _own.HullType.MaxEnergy * 100f;
                }
                // The real client latch the F/G key writes; the flight frame folds
                // it into CommandControls exactly as the pilot's key press does.
                Set("_cloakToggled", true);
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                return;
            case 16:
                lock (session.Gate)
                    Console.WriteLine($"[hud12] cloak on: server Cloaking={_own?.Cloaking} client MyCloaking={session.World.MyCloaking}");
                Shot("cloak-strength-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.4;
                return;
            case 17:
                // Decloak ramp: the state bit drops, the icon must stay lit while
                // GetCloaking() < 1 (consoledata.cpp:563-575).
                Set("_cloakToggled", false);
                _next = _watch.Elapsed.TotalSeconds + 0.05;
                return;
            case 18:
            case 19:
            case 20:
                // Decloak ramp: the bit has dropped but GetCloaking() is still < 1,
                // so the icon and strength stay lit (consoledata.cpp:563-575).
                lock (session.Gate)
                    Console.WriteLine($"[hud12] cloak tail {_step - 17}: server Cloaking={_own?.Cloaking} isCloaked={_own?.IsCloaked} client MyCloaking={session.World.MyCloaking}");
                Shot($"cloak-decloak-tail-{_step - 17}-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.07;
                return;
            case 21:
                Set("_cameraMode", CameraMode.Command);
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                return;
            case 22:
                Shot("command-ship-summary-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                return;
            default:
                game.Exit();
                return;
        }
    }
}
