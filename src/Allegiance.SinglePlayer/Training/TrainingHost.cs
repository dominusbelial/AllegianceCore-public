using System.Numerics;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.Training;
using Coll = Allegiance.Core.Collision;
using Srv = Allegiance.Server.Server;
using Cfg = Allegiance.Server.ServerConfig;
using Gm = Allegiance.Server.GameConfig;

namespace Allegiance.SinglePlayer;

/// <summary>
/// The in-process training-host API the client's <see cref="ClientCore.Training.ITrainingSite"/>
/// creation/command actions call on the embedded server (TR-2b Scope 2). Training is
/// single-player and the embedded host runs in the same process as the client, so these are
/// plain in-process calls — native sends no dedicated training network messages
/// (<c>src/training/setcommandaction.cpp:42</c>, <c>createwaypointaction.cpp:39</c> all act
/// directly on the local <c>ImissionIGC</c>). Each maps 1:1 to the native action:
/// <list type="bullet">
///   <item><c>CreateWaypoint</c> ← <c>CreateWaypointAction::Execute</c> (createwaypointaction.cpp:34-46);</item>
///   <item><c>IssueCommand</c> ← <c>SetCommandAction::Execute</c> (setcommandaction.cpp:69-84);</item>
///   <item><c>SetAutopilot</c> ← <c>SetAutopilotAction::Execute</c> (setautopilotaction.cpp:41-43);</item>
///   <item><c>CreateDrone</c>/<c>PlaceDrone</c> ← <c>TrainingMission::CreateDrone</c> (trainingmission.cpp:589-650)
///        and <c>CreateDroneAction::Execute</c>.</item>
/// </list>
/// </summary>
public interface ITrainingHost
{
    void CreateWaypoint(int buoyId, Vector3 position, int sectorId, int buoyType, bool visible);
    void CreateProbe(int probeId, int typeId, Vector3 position, int sideId, int sectorId);
    int CreateDrone(string name, int shipId, int hullId, int sideId, int pilotType,
        int behaviour = 0, int stationType = -1, int techLevel = 1);
    void PlaceDrone(int shipId, int sectorId, Vector3 position, Vector3 forward, Vector3 up,
        int? commandTargetShipId, int commandId);
    void IssueCommand(int shipId, int command, int? targetShipId, int? targetBuoyId, int? targetStationId, int commandId);
    void SetAutopilot(int shipId, bool on);
}

/// <summary>
/// TR-2b — the embedded-server <b>training host</b>: boots the real <see cref="Srv"/> with the
/// native training universe (PCore014 static core + <c>training_1</c> map, one Iron-League side),
/// seats the player's interceptor (hull 412) and the commander wingman (hull 412) in start sector
/// 1010 as native does (<c>LoadUniverse</c> trainingmission.cpp:651-782, <c>Mission2::CreateUniverse</c>
/// mission2.cpp:75-94, <c>SetupShipAndCamera</c> trainingmission.cpp:128-148), and exposes the
/// in-process creation/command/autopilot handlers that the mission script drives.
/// </summary>
/// <remarks>
/// <b>Port boundaries (honest).</b> The universe object graph (sides, stations, sectors, hull) is
/// the real <see cref="Mission"/> assembled from the real core+map; the player and commander are
/// real <see cref="Ship"/> models placed in the real sector 1010. A training waypoint is a
/// lightweight position record in the host that <see cref="TrainingHostSite"/> resolves for the
/// geometric conditions — the sim has no <c>OT_buoy</c> model, so full client replication of a buoy
/// object is deferred (see the report). Command targets (Goto), drone placement and the autopilot
/// flag operate on the real <see cref="Ship"/> objects; the host-side autopilot pass flies any ship
/// under a Goto order so the mission's scripted objectives resolve against the real sim.
/// </remarks>
public sealed class TrainingHost : ITrainingHost, IDisposable
{
    /// <summary>A training waypoint (native <c>OT_buoy</c>, createwaypointaction.cpp:34-46).</summary>
    public readonly record struct Waypoint(Vector3 Position, int SectorId, int BuoyType, bool Visible);

    private readonly Srv _server;
    private readonly object _simGate = new();
    private readonly CancellationTokenSource _stop = new();
    private Thread? _ticker;
    private int _disposed;
    private readonly Dictionary<int, Waypoint> _waypoints = new();
    private readonly Dictionary<short, bool> _autopilot = new();
    private readonly Dictionary<int, Ship> _drones = new();
    private readonly HashSet<int> _destroyed = new();
    private readonly Dictionary<int, TargetRef?> _commandTargets = new();
    private readonly Dictionary<int, int> _commands = new();
    private readonly Dictionary<int, int> _droneBehaviours = new();    // c_wbbm* mask
    private readonly Dictionary<int, int> _droneStationTypes = new();  // constructor station type
    private readonly Dictionary<int, int> _droneTechLevels = new();    // negative tech-level loadout

    public Mission Mission { get; }
    public Ship PlayerShip { get; }
    public Ship CommanderShip { get; }
    public int PlayerShipId { get; }
    public int CommanderShipId { get; }
    /// <summary>The player's start sector (native <c>Mission2::GetStartSectorID()</c> = 1010).</summary>
    public short StartSectorId { get; }

    public IReadOnlyDictionary<int, Waypoint> Waypoints => _waypoints;
    public IReadOnlyDictionary<int, Ship> Drones => _drones;

    public ushort Port => _server.BoundPort;

    private TrainingHost(Srv server, Mission mission, Ship player, Ship commander, short startSector)
    {
        _server = server;
        Mission = mission;
        PlayerShip = player;
        CommanderShip = commander;
        PlayerShipId = player.Id;
        CommanderShipId = commander.Id;
        StartSectorId = startSector;
    }

    /// <summary>The universe each native training mission boots: map, player hull, start sector and
    /// the on-screen game name. Mission 2/3 share the <c>training_1</c> map (sectors around 1010),
    /// Mission 4 uses <c>training_3</c> (sector 1030).</summary>
    public readonly record struct TrainingVariant(string Map, short PlayerHull, short StartSector, string GameName);

    public static readonly TrainingVariant BasicFlight = new("training_1.igc", 412, 1010, "Basic Flight");
    public static readonly TrainingVariant BasicWeaponry = new("training_1.igc", 414, 1010, "Basic Weaponry");
    public static readonly TrainingVariant EnemyEngagement = new("training_3.igc", 486, 1030, "Enemy Engagement");

    public static TrainingHost Start(string? artwork = null) => Start(BasicFlight, artwork);

    public static TrainingHost Start(TrainingVariant variant, string? artwork = null)
    {
        var server = new Srv();
        var artPath = artwork ?? Allegiance.Core.ExternalPaths.Resolve(Allegiance.Core.ExternalResourceKind.Artwork);
        server.Boot(TrainingConfig(variant, artPath));
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;   // bypass the ready-up lobby (established P180 pattern)

        var core = mission.Core;
        var sector = mission.Sectors.First(s => s.Id == variant.StartSector);
        var hull = core.HullTypes.TryGetValue(variant.PlayerHull, out var ph) ? ph
            : core.HullTypes.Values.OrderBy(x => x.HullId).First(); // mission2.cpp:74 etc.

        // Player interceptor: at the origin facing +X, up +Z — native SetupShipAndCamera
        // (trainingmission.cpp:128-134: SetOrientation(fwd(1,0,0), up(0,0,1)), pos(0,0,0)).
        var playerOri = Orientation.Identity;
        playerOri.Set(new Coll.Vector3(1f, 0f, 0f), new Coll.Vector3(0f, 0f, 1f));
        var player = new Ship((short)0, hull, Coll.Vector3.Zero, Coll.Vector3.Zero, playerOri) { SideId = 0 };
        mission.PlaceShip(player, sector);

        // Commander wingman (hull 412, side 0, native LoadUniverse CreateDrone("Commander",…412,0,c_ptWingman),
        // trainingmission.cpp:836-841). Id 990 matches the ported Mission2's stable commander id
        // (Mission2.cs:24). Placed at (500,0,0) facing back toward the player (Mission2::CreateUniverse,
        // mission2.cpp:82-89). IsNpc=false so the server's NPC default-order AI never wrestles control
        // away from the host's autopilot pass — the host flies it via AdvanceAutopilot when Goto'd.
        var cmdOri = Orientation.Identity;
        cmdOri.Set(new Coll.Vector3(-1f, 0f, 0f), new Coll.Vector3(0f, 0f, 1f));
        var commander = new Ship((short)990, hull, new Coll.Vector3(500f, 0f, 0f), Coll.Vector3.Zero, cmdOri)
        {
            SideId = 0, PilotType = Ship.PilotTypeWingman,   // c_ptWingman
        };
        mission.PlaceShip(commander, sector);

        var host = new TrainingHost(server, mission, player, commander, variant.StartSector);
        return host;
    }

    /// <summary>
    /// Start the background sim ticker. The headless proof drives the sim deterministically via
    /// <see cref="Step"/> instead, so this is opt-in (the real-client wiring would call it).
    /// </summary>
    public void StartBackgroundTicker()
    {
        if (_ticker is not null) return;
        _ticker = new Thread(TickLoop) { IsBackground = true, Name = "training-server" };
        _ticker.Start();
    }

    private static Cfg TrainingConfig(TrainingVariant variant, string artPath) => new()
    {
        ArtPath = artPath,
        StaticCore = "PCore014.igc",
        Port = 0,
        BindAddress = "127.0.0.1",
        Discovery = new() { Enabled = false },
        Transport = new() { MaxConnections = 32, MaxConnectionsPerIp = 32 },
        Games =
        [
            new Gm
            {
                Name = variant.GameName, Map = variant.Map, Teams = 1,
                CivIds = [25], SideNames = ["Iron League"],
                MaxPlayersPerTeam = 8, MinimumPlayersPerTeam = 0,
            },
        ],
    };

    private void TickLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
                lock (_simGate) _server.Tick();
        }
        catch (Exception)
        {
            // Background sim tick is best-effort; a failure surfaces through Mission state.
        }
    }

    /// <summary>Manually advance the embedded sim once (deterministic headless driving).</summary>
    public void Tick()
    {
        lock (_simGate) _server.Tick();
    }

    /// <summary>
    /// Advance the sim one tick AND run the host-side autopilot pass: any ship under a
    /// <see cref="GotoOrder"/> (the native <c>c_oGoto</c> waypoint objective, shipIGC.cpp:4053,4160)
    /// flies toward its target at cruise speed and clears on arrival — the host realisation of the
    /// commander/autopilot flight native drives (trainingmission.cpp:646
    /// <c>pShip-&gt;SetAutopilot(true)</c>), so goal 2 (commander reaches the waypoint) and goal 8
    /// (player autopilots within 500 m of the station) resolve against the real sim.
    /// </summary>
    public void Step()
    {
        lock (_simGate) _server.Tick();
        AdvanceAutopilot();
    }

    /// <summary>
    /// Lightweight deterministic driver step for the long-narrative missions (M3/M4): advance the real
    /// mission clock by <paramref name="dt"/> via <see cref="Mission.Update"/> (measured ~0.14 ms) and
    /// run the host autopilot pass — <em>without</em> the full server tick, whose networking/broadcast
    /// overhead costs ~50 ms even headless. Sufficient for the headless mission logic: positions are
    /// static, the player's turns are scripted by the driver, and the host autopilot pass flies any
    /// Goto'd ship, so the elapsed-time / geometric / command gates all resolve against the real host.
    /// </summary>
    public void StepLight(float dt = 0.05f)
    {
        lock (_simGate)
        {
            double now = Mission.LastUpdate + dt;
            if (now > Mission.LastUpdate) Mission.Update(now);
            AdvanceAutopilot();
        }
    }

    private const float CruiseSpeed = 100f;     // interceptor cruise, m/s (era-design nominal)
    private const float ArrivalRadius = 50f;

    private void AdvanceAutopilot()
    {
        // Zero the sim ships' own throttle so the server does not integrate their Controls field
        // into unintended motion; movement is driven exclusively by the host autopilot pass below
        // (and, for the player, by the mission's scripted goals, which read the decoupled applied
        // controls via TrainingHostSite.GetShipControls rather than the sim field).
        PlayerShip.Controls = ZeroThrottle;
        CommanderShip.Controls = ZeroThrottle;
        foreach (var d in _drones.Values) d.Controls = ZeroThrottle;

        foreach (var ship in Ships())
        {
            if (ship.GotoWaypoint is not { } g) continue;
            var dir = g.Position - ship.Position;
            float dist = dir.Length();
            if (dist <= ArrivalRadius)
            {
                ship.SetGoto(null);
                continue;
            }
            float step = Math.Min(dist, CruiseSpeed * 0.05f);
            ship.Position += dir / dist * step;
        }
    }

    private static readonly ControlData ZeroThrottle = new(0f, 0f, 0f, 0f);

    private IEnumerable<Ship> Ships()
    {
        foreach (var d in _drones.Values) yield return d;
        yield return PlayerShip;
        yield return CommanderShip;
    }

    /// <summary>The home station's position (map station 1010, native <c>OT_station 1010</c>).</summary>
    public Vector3 StationPosition
        => Mission.Map.Stations.First(s => s.StationId == StartSectorId).Position is { Length: 3 } p
            ? new Vector3(p[0], p[1], p[2]) : Vector3.Zero;

    // ── ITrainingHost handlers ──────────────────────────────────────────────────
    public void CreateWaypoint(int buoyId, Vector3 position, int sectorId, int buoyType, bool visible)
        => _waypoints[buoyId] = new Waypoint(position, sectorId, buoyType, visible);

    public void CreateProbe(int probeId, int typeId, Vector3 position, int sideId, int sectorId)
    {
        // No OT_probe runtime model in the training arc; a probe is inert to Mission 2's script
        // (only CreateWaypoint is used). Recorded for completeness, unused.
        _waypoints[probeId] = new Waypoint(position, sectorId, typeId, true);
    }

    public int CreateDrone(string name, int shipId, int hullId, int sideId, int pilotType)
        => CreateDrone(name, shipId, hullId, sideId, pilotType, 0, -1, 1);

    public int CreateDrone(string name, int shipId, int hullId, int sideId, int pilotType,
        int behaviour, int stationType, int techLevel)
    {
        int id = shipId == -1 ? NextId() : shipId;
        var hull = Mission.Core.HullTypes.TryGetValue((short)hullId, out var h) ? h
            : Mission.Core.HullTypes.Values.OrderBy(x => x.HullId).First();
        var ship = new Ship((short)id, hull, Coll.Vector3.Zero, Coll.Vector3.Zero, Orientation.Identity)
        {
            SideId = (short)sideId, IsNpc = true, PilotType = (byte)pilotType,
        };
        Mission.PlaceShip(ship, Mission.Sectors[0]);
        _drones[id] = ship;
        _droneBehaviours[id] = behaviour;      // c_wbbm* mask (createdroneaction.cpp:91-198 host intake)
        _droneStationTypes[id] = stationType;  // constructor station type (NA=-1)
        _droneTechLevels[id] = techLevel;      // negative tech-level loadout
        return id;
    }

    public void PlaceDrone(int shipId, int sectorId, Vector3 position, Vector3 forward, Vector3 up,
        int? commandTargetShipId, int commandId)
    {
        if (!_drones.TryGetValue(shipId, out var ship))
        {
            if (shipId == CommanderShipId) ship = CommanderShip;
            else return;
        }
        var ori = Orientation.Identity;
        ori.Set(ToCore(forward), ToCore(up));
        ship.Position = ToCore(position);
        ship.SetOrientation(ori);
        var sector = Mission.Sectors.FirstOrDefault(s => s.Id == sectorId);
        if (sector is not null && ship.Sector != sector)
            Mission.PlaceShip(ship, sector);
        if (commandTargetShipId is { } && commandId == TrainingMissionIds.CmdGoto)
            ship.SetGoto(new GotoOrder(GotoKind.Waypoint, (short)sectorId, ToCore(position)));
    }

    public void IssueCommand(int shipId, int command, int? targetShipId, int? targetBuoyId, int? targetStationId, int commandId)
    {
        Ship? ship = ResolveShip(shipId);
        if (ship is null) return;
        // Only an accepted or current order sets the command id a GetCommandCondition sees; a queued
        // order (c_cmdQueued) merely presents a prompt until the pilot accepts it (getcommandcondition.cpp:44-49).
        if (command != TrainingMissionIds.CommandQueued)
            _commands[shipId] = commandId;
        if (commandId == TrainingMissionIds.CmdGoto)
        {
            if (targetBuoyId is { } bid && _waypoints.TryGetValue(bid, out var wp))
            {
                _commandTargets[shipId] = new TargetRef(ObjectType.Buoy, bid);
                ship.SetGoto(new GotoOrder(GotoKind.Waypoint, (short)wp.SectorId, ToCore(wp.Position)));
            }
            else if (targetShipId is { } tsid && ResolveShip(tsid) is { } target)
            {
                _commandTargets[shipId] = new TargetRef(ObjectType.Ship, tsid);
                ship.SetGoto(new GotoOrder(GotoKind.Waypoint, target.Sector!.Id, target.Position));
            }
            else if (targetStationId is { } stid && stid == StartSectorId)
            {
                _commandTargets[shipId] = new TargetRef(ObjectType.Station, stid);
                ship.SetGoto(new GotoOrder(GotoKind.Waypoint, (short)stid, ToCore(StationPosition)));
            }
        }
        else
        {
            _commandTargets[shipId] = null;
            ship.SetGoto(null);   // c_cidDoNothing / None clear the current target (setcommandaction.cpp:77-84)
        }
    }

    public void SetAutopilot(int shipId, bool on)
    {
        Ship? ship = ResolveShip(shipId);
        if (ship is null) return;
        _autopilot[ship.Id] = on;
        if (!on) ship.SetGoto(null);
    }

    public bool IsAutopilot(int shipId) => _autopilot.TryGetValue((short)shipId, out var b) && b;

    // ── M3/M4 combat/sensor state (native IdamageIGC / IshipIGC seams) ────────────
    /// <summary>The ship is "destroyed" when it is absent or marked destroyed (the headless driver
    /// calls <see cref="DestroyDrone"/> to script the outcome the training script gates on).</summary>
    public bool IsDestroyed(int shipId)
    {
        if (_destroyed.Contains(shipId)) return true;
        if (shipId == PlayerShipId) return false;
        return ResolveShip(shipId) is null;
    }

    /// <summary>Destroy a drone — remove it from the mission so <see cref="ResolveShip"/> and the
    /// geometry reads treat it as gone (the "armour fraction reaches 0" outcome the
    /// <c>GetShipIsDestroyedCondition</c> gating detects).</summary>
    public void DestroyDrone(int shipId)
    {
        if (_drones.TryGetValue(shipId, out var ship))
        {
            _drones.Remove(shipId);
            ship.Sector?.Remove(ship);
            ship.Sector = null;
        }
        _destroyed.Add(shipId);
    }

    /// <summary>Record/replace a ship's current command-target and accepted/current command id (the
    /// radar-target the driver picks / the order it accepts; read by the <c>CurrentTarget</c>
    /// indirection and <c>GetCommandCondition</c>).</summary>
    public void SetCommandTarget(int shipId, TargetRef? target, int commandId = TrainingMissionIds.CmdGoto)
    {
        _commandTargets[shipId] = target;
        _commands[shipId] = commandId;
    }

    public TargetRef? GetCommandTarget(int shipId) => _commandTargets.TryGetValue(shipId, out var t) ? t : null;

    public int GetCommandId(int shipId) => _commands.TryGetValue(shipId, out var c) ? c : TrainingMissionIds.CmdNone;

    public int GetSectorId(int shipId) => ResolveShip(shipId) is { } s && s.Sector is { } sec ? sec.Id : -1;

    /// <summary>Heuristic for <c>IshipIGC::InScannerRange</c> (<c>shipseesobjectcondition.cpp:47-50</c>):
    /// same sector and within 1500 m. Drives the cosmetic enemy-detection eyeball; not a full
    /// signature-vs-range calc (a TR-4 sim concern).</summary>
    public bool InScannerRange(int shipId, TargetRef target)
    {
        if (ResolveShip(shipId) is not { } s || s.Sector is not { } sec) return false;
        var t = new TrainingHostSite(this).Resolve(target);
        if (t is null || t.SectorId != sec.Id) return false;
        return (t.Position - ToNum(s.Position)).LengthSquared() <= 1500f * 1500f;
    }

    private static System.Numerics.Vector3 ToNum(Allegiance.Core.Collision.Vector3 v) => new(v.X, v.Y, v.Z);

    internal Ship? ResolveShip(int id)
    {
        if (id == PlayerShipId) return PlayerShip;
        if (id == CommanderShipId) return CommanderShip;
        return _drones.TryGetValue(id, out var d) ? d : null;
    }

    private int _next = 4000;
    private int NextId() => _next++;

    private static Coll.Vector3 ToCore(Vector3 v) => new(v.X, v.Y, v.Z);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _ticker?.Join(2000);
        _server.Dispose();
        _stop.Dispose();
    }
}
