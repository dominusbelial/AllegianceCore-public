// FONT-1c real-client proof. Drives the actual Game1 over TCP against a real
// local server (the same harness pattern as tools/hud18-proof) and captures
// production 1920x1080 classic-skin backbuffer frames for the FONT-1c fixes:
//   (a) the own-status cluster (kill bonus medal + integer, SIG below it) with
//       the reticle/THRUST gauge in frame (FONT-1c fix 1/2, row 389);
//   (b) the same cluster with the detection eye and cloak icon/strength on;
//   (c) the lives/kills badges with their text INSIDE the art (fix 3);
//   (d) the turret carrier identity line (row 448, port roll-readout clash);
//   (e) the accepted/queued order rows with their arrow/check glyphs, the vote
//       banner and the centred ripcord countdown (fixes 4/5, rows 450/451/457/462);
//   (f) the inventory with an EMPTY ammo state so the native red "A" warning is
//       exercised (row 476, fix 6);
//   (g) the command-view summary at native 1:1 (FONT-1b fix 2; this tool
//       supersedes the retired tools/font1b-proof).
//
// The world/server state is real; states the local server cannot reach in a
// 2-minute proof (a vote, a turret seat, a finite lives budget, an empty
// magazine, a cloak ramp) are injected through ClientWorld.Apply with the SAME
// wire messages the production client dispatches, so the HUD code path under
// test is the production one. The server ticker is paused while an injected
// state is on screen so its per-tick own-ship SHIP_STATUS/SHIP_UPDATE cannot
// race the injected one.
//
// Run:
//   dotnet run --project tools/font1c-proof -c Debug -- --out <dir>
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Srv = Allegiance.Server.Server;
using SimVec = Allegiance.Core.Collision.Vector3;

string output = Path.GetFullPath(Arg("--out", ".scratch/font1c-capture"));
Directory.CreateDirectory(output);
string content = Path.GetFullPath(Arg("--content", "src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content"));
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);
string settingsPath = Path.Combine(output, "hud.json");
File.WriteAllText(settingsPath,
    "{\"Style\":\"Classic\",\"Size\":1,\"ColorBlindSafe\":false,\"RoundRadar\":false,\"DamageWash\":true,\"ChatLines\":7}");
Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", settingsPath);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "FONT-1c proof", Map = "m2scatter.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], GameLengthSeconds = 1800 }]
});
var mission = server.Missions[0];
// FONT-1c fix 6 — the lives badge only appears below 10 lives and the kills
// badge only in a deathmatch (console.cpp:812-821); configure a finite mission
// so both are real MISSIONDEF/MISSIONBRIEFING values on the wire.
mission.Lives = 5;
mission.KillsEnabled = true;
mission.Stage = MissionStage.Started;
using var stop = new CancellationTokenSource();
// Pause switch for injected HUD states: the per-tick own-ship status would
// otherwise overwrite the injected turret/order/badge state between Update and
// the frame's Draw.
int[] tickPaused = [0];
var ticker = new Thread(() =>
{
    while (!stop.IsCancellationRequested)
    {
        if (Volatile.Read(ref tickPaused[0]) == 0) server.TickSim();
        Thread.Sleep(5);
    }
});
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "FONT1c pilot",
        WindowWidth = 1920, WindowHeight = 1080,
        AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "" }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new Font1cProof(game, mission, output, tickPaused));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

string Arg(string name, string fallback)
{
    for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
    return fallback;
}

sealed class Font1cProof(Game1 game, Mission mission, string output, int[] tickPaused)
    : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private Ship? _own;
    private SimVec _posePos;
    private readonly Orientation _poseOrientation = Orientation.Identity;
    private readonly float[] _zeroTurnRates = new float[3];
    private bool _poseSet;
    private short _carrierId = -1;
    private short _orderStationId = -1;
    private bool _killBonusAdded, _inventoryOpened, _commandView;
    // 0 = none; 1 = lives/kills PLAYERINFO; 2 = turret SHIP_STATUS; 3 = the
    // orders/vote/ripcord console frame; 4 = detection eye + cloak ramp;
    // 5 = empty own-ship magazine. Re-applied every frame while active,
    // because the world is also fed by the network thread.
    private int _inject;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] callArgs) =>
        (typeof(Game1).GetMethod(name, Hidden) ?? throw new MissingMethodException(name)).Invoke(game, callArgs);

    private static void Pause(int[] flag, bool paused) => Volatile.Write(ref flag[0], paused ? 1 : 0);

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine("FONT1C_SCREEN " + name);
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

    /// <summary>Hold the own ship still so the frame is stable and the sim does
    /// not fly the camera away between the injection and the capture.</summary>
    private void KeepPose()
    {
        if (!_poseSet || _own is null) return;
        var sim = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");
        if (!sim.HaveHull) return;
        sim.Seed(_posePos, default, _poseOrientation, _zeroTurnRates);
        sim.Freeze();
        _own.Position = _posePos;
        _own.Velocity = default;
        _own.SetOrientation(_poseOrientation);
    }

    private void DumpState(string tag)
    {
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            var world = session.World;
            var hullById = Get<Dictionary<short, DataHullTypeIGC>>("_hullById");
            string own = world.MyShipId is { } me && world.Ships.TryGetValue(me, out var mine)
                ? $"ship={me} hull={mine.HullId} '{(hullById.TryGetValue(mine.HullId, out var h) ? h.Name : "?")}'"
                  + $" parent={mine.ParentId} state={mine.State} sector={mine.SectorId}"
                : "no own ship";
            Console.WriteLine($"[font1c] {tag}: {own} camera={Get<object>("_cameraMode")}"
                + $" killBonus={world.KillBonusPercent:0.#}% streak={world.KillBonusStreak}"
                + $" lives={world.MissionLives} dethmatch={world.MissionBriefing?.Deathmatch}"
                + $" vote='{world.ActiveBallot?.BallotText}' ripcord={world.RipcordActive}"
                + $" accepted='{world.MyAcceptedCommand?.CommandId}' queued='{world.MyQueuedCommand?.CommandId}'"
                + (world.MyShipId is { } mid && world.Ships.TryGetValue(mid, out var meEnt)
                    ? $" ammo={meEnt.Latest?.AmmoFraction:0.###} detected={meEnt.Detected}" : ""));
        }
    }

    /// <summary>Real loadout for the inventory proof: mount one fixed weapon,
    /// one shield and one afterburner through the production server path so the
    /// client receives LOADOUT_CHANGE and the pane resolves the stat rows
    /// (invsweapon/invshield/invafterburn layouts, consoledata.cpp:690-985).</summary>
    private void MountStatParts()
    {
        if (_own is null) return;
        var all = mission.Core.PartTypes.Values;
        DataPartTypeIGC? weapon = null, shield = null, afterburner = null;
        // A damaging energy weapon (the same Pulse Laser projectile 66 the HUD-18
        // proof uses) so range/damage/burst read as positive numbers; the first
        // repair gun would print a negative damage stat.
        DataPartTypeIGC? fallbackWeapon = null;
        float bestShield = -1f, bestThrust = -1f;
        foreach (var part in all)
        {
            if (part.EquipmentType == EquipmentType.Weapon)
            {
                fallbackWeapon ??= part;
                if (part.Type is DataPartTypeIGC.Weapon { ProjectileTypeId: 66 }) weapon ??= part;
            }
            else if (part.Type is DataPartTypeIGC.Shield { RateRegen: > 0f } shieldType && shieldType.MaxStrength > bestShield)
            {
                bestShield = shieldType.MaxStrength;
                shield = part;
            }
            else if (part.Type is DataPartTypeIGC.Afterburner { FuelConsumption: > 0f } burner && burner.MaxThrust > bestThrust)
            {
                bestThrust = burner.MaxThrust;
                afterburner = part;
            }
        }
        weapon ??= fallbackWeapon;
        _own.ClearLoadout();
        if (weapon is { } w) _own.MountPart(w, 100, 0, ExpandedPartData.FullyMounted);
        if (shield is { } sh) _own.MountPart(sh, 100, 0, ExpandedPartData.FullyMounted);
        if (afterburner is { } ab) _own.MountPart(ab, 100, 0, ExpandedPartData.FullyMounted);
        Console.WriteLine($"[font1c] mounted weapon='{weapon?.Name}' shield='{shield?.Name}' afterburner='{afterburner?.Name}'");
    }

    /// <summary>Save the loaded native killcounterbmp surface so the report can
    /// show the art the black integer is drawn on.</summary>
    private void SaveStatusArt()
    {
        var hud = typeof(Game1).GetProperty("ActiveHud", Hidden)?.GetValue(game);
        var status = hud is null ? null : typeof(HudRenderer).GetField("_status", Hidden)?.GetValue(hud);
        var tex = (status as StatusTextures)?.KillCounter;
        if (tex is null) { Console.WriteLine("[font1c] killcounterbmp not loaded"); return; }
        using var fs = File.Create(Path.Combine(output, "killcounter-art.png"));
        tex.SaveAsPng(fs, tex.Width, tex.Height);
        Console.WriteLine($"[font1c] killcounterbmp {tex.Width}x{tex.Height} saved");
    }

    /// <summary>Pick a station in the own sector to name the injected orders.</summary>
    private short PickOrderStationLocked(ClientWorld world)
    {
        if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)) return -1;
        foreach (var (id, station) in world.Stations)
            if (station.ClusterId == own.SectorId) return id;
        return -1;
    }

    private void Inject(ClientWorld w)
    {
        if (_inject == 0) return;
        if (w.MyShipId is not { } me || !w.Ships.TryGetValue(me, out var mine)) return;
        short ownSide = mine.SideId;
        switch (_inject)
        {
            // Badges (fix 3): PLAYERINFO carries MissionKills/MissionDeaths
            // (appmsg.cpp:2254-2256) exactly as the server seeds them.
            case 1:
                w.Apply(new PlayerInfo { CharacterName = "FONT1c pilot", ISide = ownSide, ShipId = me,
                    Money = 0, NKills = 7, NDeaths = 2, PilotType = PlayerInfo.PilotTypePlayer });
                break;
            // Turret/carrier identity: a same-side carrier export + the native
            // SHIP_STATUS the server sends for a boarded gunner (parent id +
            // c_ssTurret, igc.h:4395; console.cpp:797-804 reads it).
            case 2:
                if (_carrierId < 0) _carrierId = mission.NextObjectId();
                w.Apply(new ExportShip { ObjectType = ExportShip.OtShip, CharacterName = "Proof Carrier",
                    HullId = mine.HullId, ShipId = _carrierId, SideId = ownSide, PilotType = ExportShip.PilotTypePlayer });
                w.Apply(new ShipStatusMessage { ShipId = me, HullId = mine.HullId, SectorId = mine.SectorId,
                    StationId = -1, ParentId = _carrierId, State = ShipStatusMessage.StateTurret, Detected = true,
                    StateTime = (uint)_watch.ElapsedMilliseconds });
                break;
            // Orders (450/451), vote (457) and ripcord (462) in one frame; every
            // write is the production message the client dispatches.
            case 3:
                if (_orderStationId < 0) _orderStationId = PickOrderStationLocked(w);
                if (_orderStationId >= 0)
                {
                    if (w.MyAcceptedCommand is null)
                        w.Apply(new OrderChange { ShipID = me, Command = OrderChange.CmdAccepted,
                            CommandId = OrderChange.CidAttack, ObjectType = IgcObjectType.Station,
                            ObjectID = _orderStationId });
                    if (w.MyQueuedCommand is null)
                        w.Apply(new QueuedCommandUpdate { CommandId = OrderChange.CidGoto,
                            TargetType = IgcObjectType.Station, TargetId = _orderStationId, SenderId = -1 });
                }
                if (w.ActiveBallot is null)
                    w.Apply(new Ballot { BallotId = 7, BallotText = "Attack the enemy garrison in Omega?",
                        OtInitiator = (short)IgcObjectType.Side, OidInitiator = ownSide, TimeExpiration = uint.MaxValue });
                if (!w.RipcordActive)
                    w.Apply(new RipcordActivate { ShipidRipcord = me, OtRipcord = (short)IgcObjectType.Station,
                        OidRipcord = _orderStationId, SidRipcord = mine.SectorId });
                break;
            // Own-status cluster: the native eye is the raw own-side Detected
            // bit (console.cpp:811, clintlib.h:438 -> dialog.mdl:2526-2534) and
            // the cloak icon/strength is GetCloaking() < 1 (consoledata.cpp:480-489,
            // 563-575). Both ride the production messages.
            case 4:
                w.Apply(new ShipStatusMessage { ShipId = me, HullId = mine.HullId, SectorId = mine.SectorId,
                    StationId = -1, ParentId = -1, State = ShipStatusMessage.StateFlying, Detected = true,
                    StateTime = (uint)_watch.ElapsedMilliseconds });
                w.Apply(new OwnShipCloakUpdate { Cloaking = 0.35f });   // 65% strength
                break;
            // Empty magazine: the native red "A" empty warning is
            // PartWrapper::IsOutOfAmmo == GetAmmo() <= 0 (consoledata.cpp:1132-1145).
            case 5:
                var latest = mine.Latest;
                w.Apply(new SingleShipUpdate
                {
                    OtTarget = (short)IgcObjectType.Ship, OidTarget = me,
                    Ship = ServerShipUpdate.FromValues(me, latest?.StateM ?? 0, latest?.Controls ?? default,
                        (uint)_watch.ElapsedMilliseconds, latest?.Position ?? default,
                        latest?.Orientation ?? Orientation.Identity, latest?.Velocity ?? default,
                        latest?.TurnRates ?? _zeroTurnRates,
                        latest?.HullFraction ?? 1f, latest?.ShieldFraction ?? 1f, 0f,
                        latest?.FuelFraction ?? 1f, latest?.EnergyFraction ?? 1f,
                        latest?.OreFraction ?? 1f, latest?.Power ?? 1f),
                });
                break;
        }
    }

    /// <summary>Return the own ship to the server's flying state after the
    /// injected turret seat (parent NA, state c_ssFlying, igc.h:4389-4395).</summary>
    private void ClearTurretSeat()
    {
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            var w = session.World;
            if (w.MyShipId is not { } me || !w.Ships.TryGetValue(me, out var mine)) return;
            w.Apply(new ShipStatusMessage { ShipId = me, HullId = mine.HullId, SectorId = mine.SectorId,
                StationId = -1, ParentId = -1, State = ShipStatusMessage.StateFlying, Detected = true,
                StateTime = (uint)_watch.ElapsedMilliseconds });
        }
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 260) throw new TimeoutException("FONT-1c proof did not finish");
        bool flying = Get<object>("_state").ToString() == "Flying" && !Get<bool>("_dockedActive");
        if (!flying) return;
        KeepPose();
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate) Inject(session.World);
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
                _own ??= OwnServerShip();
                DumpState("launch");
                _next = _watch.Elapsed.TotalSeconds + 1.2;
                return;
            case 1:
                // Native kill-bonus source: the real server-side streak counter,
                // streamed on OwnShipKillBonusUpdate(212) (console.cpp:830).
                if (!_killBonusAdded && _own is not null)
                {
                    for (int i = 0; i < 4; i++) _own.AddKillBonus();
                    _killBonusAdded = true;
                    MountStatParts();
                    Console.WriteLine($"[font1c] server kill bonus raised 4x -> {_own.KillBonusPercent:0.#}%");
                }
                _next = _watch.Elapsed.TotalSeconds + 2.0;
                return;
            case 2:
                DumpState("own-status");
                SaveStatusArt();
                Shot("font1c-ownstatus-killbonus-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                return;
            case 3:
                Pause(tickPaused, true);   // freeze the server's own-ship updates first
                _inject = 4;               // detection eye + cloak ramp
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                return;
            case 4:
                DumpState("own-status-eye-cloak");
                Shot("font1c-ownstatus-eye-cloak-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                return;
            case 5:
                _inject = 1;               // lives/kills badge PLAYERINFO
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                return;
            case 6:
                DumpState("lives-kills");
                Shot("font1c-badges-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                return;
            case 7:
                _inject = 2;               // turret seat
                _next = _watch.Elapsed.TotalSeconds + 0.6;
                return;
            case 8:
                DumpState("turret");
                Shot("font1c-448-turret-carrier-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                return;
            case 9:
                ClearTurretSeat();
                _inject = 3;               // orders + vote + ripcord
                var world = session.World;
                lock (session.Gate)
                {
                    world.PostPlainText(false, "Tip: press T to select the object in the reticle");
                    world.PostPlainText(true, "WARNING: you are taking hull damage");
                }
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                return;
            case 10:
                DumpState("orders-vote-ripcord-tips");
                Shot("font1c-450-451-457-462-tips-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                return;
            case 11:
                _inject = 5;               // empty magazine for the native red A
                if (!_inventoryOpened)
                {
                    Call("ApplyConsoleMode", "ConModeInventory", false);
                    _inventoryOpened = true;
                }
                _next = _watch.Elapsed.TotalSeconds + 1.4;   // native .5 s slide (dialog.mdl:5387)
                return;
            case 12:
                DumpState("inventory-empty-ammo");
                Shot("font1c-476-inventory-empty-ammo-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                return;
            case 13:
                // Folded in from the retired font1b-proof: the command-view ship
                // summary at native 1:1 (FONT-1b fix 2).
                _inject = 0;
                if (_inventoryOpened)
                {
                    Call("ApplyConsoleMode", "ConModeInventory", false);
                    _inventoryOpened = false;
                }
                Set("_cameraMode", CameraMode.Command);   // native ofInFlightCommand (dialog.mdl:5412)
                _commandView = true;
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                return;
            case 14:
                DumpState("command-summary");
                Shot("font1c-command-summary-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.9;
                return;
            default:
                if (_inventoryOpened) Call("ApplyConsoleMode", "ConModeInventory", false);
                if (_commandView) Set("_cameraMode", CameraMode.Cockpit);
                Pause(tickPaused, false);
                _inject = 0;
                game.Exit();
                return;
        }
    }
}
