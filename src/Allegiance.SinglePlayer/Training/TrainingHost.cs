using System.Numerics;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Wire;
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
    bool TrainingShipExists(int shipId);
    bool TryTakeLifecycleEvent(out TrainingLifecycleEvent trainingEvent);
    void RestoreTrainee();
    void CreateWaypoint(int buoyId, Vector3 position, int sectorId, int buoyType, bool visible);
    void CreateProbe(int probeId, int typeId, Vector3 position, int sideId, int sectorId);
    int CreateDrone(string name, int shipId, int hullId, int sideId, int pilotType,
        int behaviour = 0, int stationType = -1, int techLevel = 1);
    void PlaceDrone(int shipId, int sectorId, Vector3 position, Vector3 forward, Vector3 up,
        int? commandTargetShipId, int commandId);
    void IssueCommand(int shipId, int command, int? targetShipId, int? targetBuoyId, int? targetStationId, int commandId, int? targetAsteroidId = null, int? targetWarpId = null);
    void SetAutopilot(int shipId, bool on);
    /// <summary>Whether a ship is currently on autopilot (<c>IshipIGC::GetAutopilot</c>;
    /// the host flies any Goto'd ship and flags it here).</summary>
    bool IsAutopilot(int shipId);
    /// <summary>The live ammunition count of a ship (<c>IshipIGC::GetAmmo</c>).</summary>
    int GetAmmo(int shipId);
    /// <summary>The live probe count of the host's world (<c>GetProbes()->n()</c>).</summary>
    int GetProbeCount();
    /// <summary>Damage a ship by a collision amount, then reset controls and fire all weapons
    /// (<c>DoDamageAdjustableAction</c>, dodamageadjustableaction.cpp:27-36).</summary>
    void DamageShipAdjustable(int shipId, float amount);

    // ── host real-model reads the client production site forwards ───────────
    /// <summary>The current command target of a ship (<c>IshipIGC::GetCommandTarget(c_cmdCurrent)</c>).</summary>
    TargetRef? GetCommandTarget(int shipId);
    /// <summary>The accepted/current command id of a ship (<c>GetCommandID(c_cmdAccepted)</c>).</summary>
    int GetCommandId(int shipId);
    /// <summary>Whether <paramref name="ship"/> sees <paramref name="target"/> in scanner range
    /// (<c>IshipIGC::InScannerRange</c>).</summary>
    bool InScannerRange(int shipId, TargetRef target);
    /// <summary>Resolve a training waypoint buoy to its live geometry (<c>GetModel(OT_buoy, id)</c>).</summary>
    Allegiance.ClientCore.Training.ITrainingObject? ResolveWaypoint(int buoyId);

    // ── M5/M6 command-view / kill-credit reads (authoritative host state) ─────
    bool IsPicked(TargetRef model);
    int ViewSector { get; }
    float GetFraction(TargetRef model);
    int GetCommandAcknowledged();

    // ── M8 nanite / repair / station seams (authoritative host state) ─────────
    bool HasPartMounted(int shipId, int partId);
    void MountPart(int shipId, int partId, int mount = 0);
    void DamageStation(int stationId, float amount);
    /// <summary>Knock a ship's armour down by <paramref name="amount"/> (the damaged-but-alive
    /// outcome behind <c>DoDamageAction</c> and Mission 8's bomber).</summary>
    void DamageShip(int shipId, float amount);

    // ── P880b — real-model equipment/state/radar/buoy seams ──────────────────
    /// <summary>TRUE when a mounted part of the ship has the given equipment type
    /// (<c>GetShipHasCargoCondition</c>, getshiphascargocondition.cpp:29-36).</summary>
    bool HasPartOfEquipmentType(int shipId, EquipmentType part);
    /// <summary>Snapshot a ship's real physical/equipment state (<c>ResetShipAction</c>).</summary>
    ShipSnapshot CaptureShip(int shipId);
    /// <summary>Restore a ship's real state from a snapshot (<c>ResetShipAction</c>).</summary>
    void RestoreShip(int shipId, ShipSnapshot snap);
    /// <summary>Release a waypoint buoy's consumer (<c>ReleaseConsumerAction</c>,
    /// releaseconsumeraction.cpp:25-28); a released buoy no longer resolves.</summary>
    void ReleaseConsumer(int buoyId);
    /// <summary>Set the host radar LOD mode (<c>SetRadarLODAction</c>, setradarlodaction.cpp:31-36).</summary>
    void SetRadarLod(int lod);
    /// <summary>The native <c>SetCargoNanAction</c> cargo recipe (setcargonanaction.cpp:22-52).</summary>
    void SetCargoNan(int shipId);
    /// <summary>Repair and re-arm a ship's real resources + cargo (<c>RepairRearmAction</c>,
    /// repairrearraction.cpp:37-74).</summary>
    void RepairRearm(int shipId);
    /// <summary><c>DoDamageAction</c> (dodamageaction.cpp:24-50): demonstrate shield or hull
    /// damage on the player's real ship — shield to 20% or a 7.5-point hull dent.</summary>
    void DealDamage(bool toHull);
    /// <summary><c>ToggleWeaponAction</c> (toggleweaponaction.cpp:41-47): select the weapon
    /// mount for fire (native <c>GetWindow()->ToggleWeapon</c>).</summary>
    void ToggleWeapon(int mount);
    /// <summary><c>SwapWeaponAction</c> (swapweaponaction.cpp:25-32): load a part of
    /// <paramref name="equipmentType"/> from cargo into the mount via the real
    /// <see cref="Ship.TrySwapPartWithCargo"/> path.</summary>
    void SwapWeapon(int equipmentType, int mount);

    /// <summary><c>SetSignatureAction</c> (setsignatureaction.cpp:49-53 → <c>ImodelIGC::SetSignature</c>,
    /// modelIGC.h:283-290): set a ship's signature override — Mission 4 raises the four enemy miners'
    /// signatures to 1.0e6 (mission4.cs:442) so they dominate the scan-visibility math.</summary>
    void SetSignature(int shipId, float signature);
}

/// <summary>
/// TR-2b — the embedded-server <b>training host</b>: boots the real <see cref="Srv"/> with the
/// native training universe (PCore014 static core + authored training map, six native sides),
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
public sealed partial class TrainingHost : ITrainingHost, IDisposable
{
    /// <summary>A training waypoint (native <c>OT_buoy</c>, createwaypointaction.cpp:34-46).</summary>
    public readonly record struct Waypoint(Vector3 Position, int SectorId, int BuoyType, bool Visible);

    /// <summary>A map station's position/sector/side record (native <c>IstationIGC</c>; the M8
    /// home + enemy-outpost stations are the mission's repair/destroy subjects).</summary>
    public readonly record struct HostStation(Vector3 Position, int SectorId, int SideId);

    private readonly Srv _server;
    private readonly object _simGate = new();
    private readonly CancellationTokenSource _stop = new();
    private Thread? _ticker;
    private bool _clientBound;
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

    // M8 nanite / repair / station state (native mission8.cpp / stationIGC.cpp:147-156).
    private readonly Dictionary<int, HostStation> _stations = new();   // station id -> map record
    private readonly Dictionary<int, float> _stationFractions = new(); // armour fraction (1 = full)
    private readonly Dictionary<int, float> _shipFractions = new();    // ship armour fraction (1 = full)
    private readonly HashSet<(int ShipId, int PartId)> _mountedParts = new(); // mounted part ids
    private readonly HashSet<int> _releasedConsumers = new();                 // released waypoint-buoy consumers

    // M5/M6 command-view + escalation state (native trekClient view/console + TrainingMission::ShipKilled).
    private TargetRef? _pickedRef;                                     // console's picked object
    private int _viewSector;                                           // currently-viewed cluster (GetViewCluster)
    private int _commandAcknowledged = TrainingMissionIds.CmdDoNothing; // m_cCommandAcknowledged
    private volatile TrainingMission? _activeMission;                  // receives real kill credit

    public Mission Mission { get; }
    public Ship PlayerShip { get; private set; }
    public Ship CommanderShip { get; }
    public int PlayerShipId => PlayerShip.Id;
    public int CommanderShipId { get; }
    /// <summary>The player's start sector (native <c>Mission2::GetStartSectorID()</c> = 1010).</summary>
    public short StartSectorId { get; }

    /// <summary>The station id of the player's home station — the station in the start sector owned
    /// by the player's side (native <c>GetSide()-&gt;GetStations()-&gt;first()</c>; the player is side 0).
    /// For the native training maps this is usually the start-sector station (e.g. training_6: 2080),
    /// except Mission 8's <c>training_nan</c>, where the home station is 1052 (cluster 1051, side 0)
    /// and the enemy outpost is 1051 (cluster 1052, side 1) — probed from the .igc this session.</summary>
    public short HomeStationId { get; }

    public IReadOnlyDictionary<int, Waypoint> Waypoints => _waypoints;
    public IReadOnlyDictionary<int, Ship> Drones => _drones;
    public IReadOnlyDictionary<int, HostStation> Stations => _stations;

    public ushort Port => _server.BoundPort;

    private TrainingHost(Srv server, Mission mission, Ship player, Ship commander, short startSector)
    {
        _server = server;
        Mission = mission;
        PlayerShip = player;
        CommanderShip = commander;
        CommanderShipId = commander.Id;
        StartSectorId = startSector;
        _viewSector = startSector;

        // Populate the map station records and pick the player's home station (native side's first
        // station). Station/sector ids are distinct namespaces, so search by the station's cluster
        // (sector) on the player's side, falling back to the station id equal to the start sector
        // for the older training maps.
        foreach (var st in mission.Map.Stations)
            _stations[st.StationId] = new HostStation(
                st.Position is { Length: 3 } p ? new Vector3(p[0], p[1], p[2]) : Vector3.Zero,
                st.ClusterId, st.SideId);
        // Pick the player's home station (native side's first station). Station/sector ids are
        // distinct namespaces, so search by the station's cluster (sector) on the player's side,
        // falling back to the station id equal to the start sector for the older training maps.
        short homeId = startSector;
        foreach (var st in mission.Map.Stations)
            if (st.ClusterId == startSector && st.SideId == player.SideId) { homeId = (short)st.StationId; break; }
        if (homeId == startSector)
            foreach (var st in mission.Map.Stations)
                if (st.StationId == startSector) { homeId = (short)st.StationId; break; }
        HomeStationId = homeId;
        _stationFractions[HomeStationId] = 1.0f;
    }

    /// <summary>The universe each native training mission boots: map, player hull, start sector and
    /// the on-screen game name. Mission 2/3 share the <c>training_1</c> map (sectors around 1010),
    /// Mission 4 uses <c>training_3</c> (sector 1030).</summary>
    public readonly record struct TrainingVariant(string Map, short PlayerHull, short StartSector, string GameName);

    public static readonly TrainingVariant BasicFlight = new("training_1.igc", 412, 1010, "Basic Flight");
    public static readonly TrainingVariant BasicWeaponry = new("training_1.igc", 414, 1010, "Basic Weaponry");
    public static readonly TrainingVariant EnemyEngagement = new("training_3.igc", 486, 1030, "Enemy Engagement");
    public static readonly TrainingVariant CommandView = new("training_5.igc", 412, 1050, "Command View");
    public static readonly TrainingVariant PracticeArena = new("training_6.igc", 488, 2080, "Practice Arena");
    // TR-5: Mission 8 "Nanite" (training_nan, Rix scout 685, sector 1051 — mission8.cpp:62-64,89)
    // and Mission 10 "Free Flight" (a Mission-6 clone without the intro and with fewer constraints,
    // mission10.cpp:1-7 — same training_6/488/2080 universe as Practice Arena).
    public static readonly TrainingVariant Nanite = new("training_nan.igc", 685, 1051, "Nanite");
    public static readonly TrainingVariant FreeFlight = new("training_6.igc", 488, 2080, "Free Flight");

    public static TrainingHost Start(string? artwork = null) => Start(BasicFlight, artwork);

    public static TrainingHost Start(TrainingVariant variant, string? artwork = null)
    {
        var server = new Srv { SpawnInitialMinersAtBoot = false, LocalTraining = true };
        var artPath = artwork ?? Allegiance.Core.ExternalPaths.Resolve(Allegiance.Core.ExternalResourceKind.Artwork);
        server.Boot(TrainingConfig(variant, artPath));
        var mission = server.Missions[0];
        mission.IsTraining = true;
        // training/trainingmission.cpp:670-685,718-728: training has its own palette.
        (byte R, byte G, byte B)[] colors = [(188, 160, 0), (0, 138, 217), (156, 16, 102),
            (50, 140, 20), (255, 145, 145), (50, 200, 125)];
        foreach (var side in mission.Sides)
        {
            var color = colors[side.Id];
            side.ColorR = color.R / 255f; side.ColorG = color.G / 255f; side.ColorB = color.B / 255f;
            side.ConquestPercent = 100 / 6;
        }
        mission.Sides[0].DevelopmentTechs = new TechTreeBitMask(Enumerable.Repeat((byte)255, TechTreeBitMask.ByteCount).ToArray());
        mission.Stage = MissionStage.Started;   // bypass the ready-up lobby (established P180 pattern)
        ApplyMissionSideMaxEnergy(variant, mission);   // mission3.cpp:187-190 / mission4.cpp:113-116

        var core = mission.Core;
        var sector = mission.Sectors.First(s => s.Id == variant.StartSector);
        var hull = core.HullTypes.TryGetValue(variant.PlayerHull, out var ph) ? ph
            : core.HullTypes.Values.OrderBy(x => x.HullId).First(); // mission2.cpp:74 etc.

        // Player interceptor: at the origin facing +X, up +Z — native SetupShipAndCamera
        // (trainingmission.cpp:128-134: SetOrientation(fwd(1,0,0), up(0,0,1)), pos(0,0,0)).
        var playerOri = Orientation.Identity;
        playerOri.Set(new Coll.Vector3(1f, 0f, 0f), new Coll.Vector3(0f, 0f, 1f));
        var player = new Ship((short)0, hull, Coll.Vector3.Zero, Coll.Vector3.Zero, playerOri) { SideId = 0, Name = "Cadet" };
        mission.PlaceShip(player, sector);
        EquipDefaultTrainingLoadout(player);
        ApplyMissionShipSetup(variant, player);   // mission3.cpp:168-186 / mission4.cpp:110-137
        if (variant == CommandView) player.StationId = 1050; // training/mission5.cpp:95-96

        // Commander wingman (hull 412, side 0, native LoadUniverse CreateDrone("Commander",…412,0,c_ptWingman),
        // trainingmission.cpp:796-797). Id 990 matches the ported Mission2's stable commander id
        // (Mission2.cs:24). Placed at (500,0,0) facing back toward the player (Mission2::CreateUniverse,
        // mission2.cpp:82-89). The real NPC flag also keeps the commander in late-join
        // roster/hull replication. Native training-specific AI remains separately partial.
        var cmdOri = Orientation.Identity;
        cmdOri.Set(new Coll.Vector3(-1f, 0f, 0f), new Coll.Vector3(0f, 0f, 1f));
        var commander = new Ship((short)990, core.HullTypes[412], new Coll.Vector3(500f, 0f, 0f), Coll.Vector3.Zero, cmdOri)
        {
            SideId = 0, IsNpc = true, PilotType = Ship.PilotTypeWingman, Name = "Commander",
        };
        mission.PlaceShip(commander, sector);
        EquipDefaultTrainingLoadout(commander);

        server.AnnounceNewShip(commander); // training/trainingmission.cpp:796-797: real named commander, with hull metadata
        var host = new TrainingHost(server, mission, player, commander, variant.StartSector);
        if (variant != BasicFlight)
        {
            // Keep the instructor out of the exercise: mission3.cpp:194-199,
            // mission4.cpp:121-128, mission5.cpp:138-145, mission6.cpp:382-389,
            // mission8.cpp:100-104, mission10.cpp:379-385.
            commander.StationId = host.HomeStationId;
            commander.StayDocked = true;
            commander.SetCommand(OrderChange.CmdAccepted, null, OrderChange.CidDoNothing);
            commander.SetCommand(OrderChange.CmdCurrent, null, OrderChange.CidDoNothing);
            host.IssueCommand(commander.Id, TrainingMissionIds.CommandAccepted, null, null, null, TrainingMissionIds.CmdDoNothing);
            host.SetAutopilot(commander.Id, false);
        }
        // Native's player IS the trainee (training/trainingmission.cpp:132-140).
        // The wire logon reserves its own id before side selection. Replace the headless
        // placeholder with that id at the spawn seam, so ACK, host actions and replication
        // all refer to one ship. Custom spawners bypass the ordinary initial hangar.
        server.ShipSpawner = (_, m, id) =>
        {
            if (host._clientBound)
                throw new InvalidOperationException("A training host accepts only one trainee.");
            var old = host.PlayerShip;
            old.Sector?.Remove(old);
            var trainee = new Ship(id, old.HullType, Coll.Vector3.Zero, Coll.Vector3.Zero, playerOri)
            {
                SideId = old.SideId, Name = "Cadet", StationId = old.StationId,
            };
            m.PlaceShip(trainee, sector);
            EquipDefaultTrainingLoadout(trainee);
            ApplyMissionShipSetup(variant, trainee);   // mission3.cpp:168-186 / mission4.cpp:110-137
            host.PlayerShip = trainee;
            host._clientBound = true;
            Console.WriteLine($"[training] trainee bound to logon ship {id}, sector {sector.Id}, hull {variant.PlayerHull}, station {trainee.StationId?.ToString() ?? "none"}");
            return (trainee, sector);
        };
        // Route the server's real kill-credit events into the active mission's kill counter (M6), so
        // kill goals read the real credit (ejections excluded via the !bLifepod gate in BroadcastKillShip).
        server.KillCreditGranted += (launcher, victim) =>
        {
            try { host.RecordKillCredit(launcher, victim); } catch { /* training-only observer */ }
        };
        server.TrainingShipDeath = host.HandleTraineeDeath;
        server.TrainingStationDeath = host.HandleStationDeath;
        return host;
    }

    /// <summary>
    /// Side-wide global-attribute setup the native missions apply in their <c>CreateUniverse</c>:
    /// Mission 3 cuts the side's max energy to a quarter (mission3.cpp:187-190), Mission 4 multiplies
    /// it tenfold (mission4.cpp:113-116). Applied on the server (side 0 = the player's side) before the
    /// player/trainee ships are created, so their <c>EffectiveMaxEnergy</c> reads the reduced/increased
    /// ceiling from birth — the same as native's <c>SetAttribute(c_gaMaxEnergy, …)</c> before play begins.
    /// </summary>
    private static void ApplyMissionSideMaxEnergy(TrainingVariant variant, Mission mission)
    {
        float value = variant switch
        {
            _ when variant == BasicWeaponry => 0.25f,   // mission3.cpp:187-190 (one quarter)
            _ when variant == EnemyEngagement => 10.0f, // mission4.cpp:113-116 (ten times)
            _ => 0f,
        };
        if (value == 0f) return;
        var side = mission.Sides.First(s => s.Id == 0);
        var arr = side.GlobalAttributes.ToArray();
        arr[GlobalAttributeSet.MaxEnergy] = value;   // c_gaMaxEnergy = 13 (igc.h:600)
        side.GlobalAttributes = new GlobalAttributeSet(arr);
    }

    /// <summary>
    /// Per-player loadout mutations the native missions apply in their <c>CreateUniverse</c> (mirrored on
    /// the server so client and server agree — the brief's "applied on the server"):
    /// <list type="bullet">
    /// <item>Mission 3 (mission3.cpp:168-186): remove weapon 0, add anti-shield part 92 at mount 0,
    /// remove the missile magazine, select only the starting weapon (mount 0) and disable group fire.</item>
    /// <item>Mission 4 (mission4.cpp:110-137): enable group fire (all starting weapons) and remove the
    /// missile magazine.</item>
    /// </list>
    /// </summary>
    private static void ApplyMissionShipSetup(TrainingVariant variant, Ship ship)
    {
        if (variant == BasicWeaponry)
        {
            // mission3.cpp:172-175 — pPart = GetMountedPart(ET_Weapon,0); pPart->Terminate().
            ship.TryRemovePart(EquipmentType.Weapon, 0, out _);
            // mission3.cpp:176 — AddPartToShip(92, 0, 0): mount anti-shield gun part 92 at mount 0, full.
            if (ship.Mission is { } m && m.Core.PartTypes.TryGetValue(92, out var antiShield))
                ship.MountPart(antiShield, short.MaxValue, 0, ExpandedPartData.FullyMounted);
            // mission3.cpp:177-180 — remove the missile magazine.
            ship.TryRemovePart(EquipmentType.Magazine, 0, out _);
            // mission3.cpp:181 — SetSelectedWeapon(0).
            ship.StateM = (ship.StateM & ~ShipControlState.SelectedWeaponMask) | (0 & ShipControlState.SelectedWeaponMask);
            // mission3.cpp:182 — fGroupFire = false.
            ship.StateM &= ~ShipControlState.AllWeapons;
        }
        else if (variant == EnemyEngagement)
        {
            // mission4.cpp:126 — fGroupFire = true (activate all starting weapons).
            ship.StateM |= ShipControlState.AllWeapons;
            // mission4.cpp:131-136 — remove the missile magazine.
            ship.TryRemovePart(EquipmentType.Magazine, 0, out _);
        }
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

    /// <summary>P889 — internal for the sizing test: training declares a shared
    /// machine and the few seats it actually uses.</summary>
    internal static Cfg TrainingConfig(TrainingVariant variant, string artPath) => new()
    {
        ArtPath = artPath,
        StaticCore = "PCore014.igc",
        Port = 0,
        BindAddress = "127.0.0.1",
        Discovery = new() { Enabled = false },
        // P889 — training boots the real server in the game client's process on
        // one PC: shared profile, sized for its few seats (the trainee and the
        // commander; drones are not independent connections).
        HostProfile = "shared",
        ExpectedPlayers = 2,
        Transport = new() { MaxConnections = 32, MaxConnectionsPerIp = 32 },
        Games =
        [
            new Gm
            {
                Name = variant.GameName, Map = variant.Map, Teams = 6,
                CivIds = [variant == Nanite ? 201 : 25, 35, 18, 25, 35, 18],
                SideNames = [variant == Nanite ? "Rixian Unity" : "Iron League", "Bios", "Gigacorp",
                    "Protectorate", "Colossal Mining Corp", "Midnight Runners"],
                MaxPlayersPerTeam = 8, MinimumPlayersPerTeam = 0,
            },
        ],
    };

    private void TickLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (Paused) { Thread.Sleep(10); continue; }
                lock (_simGate)
                {
                    _server.Tick();
                    AdvanceAutopilot();
                }
            }
        }
        catch (Exception)
        {
            // Background sim tick is best-effort; a failure surfaces through Mission state.
        }
    }

    /// <summary>
    /// P785 — TK_PauseTM: native's pause freezes the whole in-process clock
    /// (<c>Time::Pause</c>, ztime.cpp:15-25; trainingmission.cpp:347-358), so the
    /// training universe stops ticking while the mission waits. The port freezes the
    /// embedded host's sim the same way (the mission clock freeze is handled by
    /// <see cref="Allegiance.ClientCore.Training.TrainingClock"/>).
    /// </summary>
    public volatile bool Paused;

    /// <summary>Manually advance the embedded sim once (deterministic headless driving).</summary>
    public void Tick()
    {
        if (Paused) return;
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
        if (Paused) return;
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
        // M4's routed NPC orders run in the real simulation. A position nudge in the source
        // sector cannot implement intersector travel or retain collision response.
        if (_clientBound && StartSectorId == EnemyEngagement.StartSector) return;
        // Zero the sim ships' own throttle so the server does not integrate their Controls field
        // into unintended motion; movement is driven exclusively by the host autopilot pass below
        // (and, for the player, by the mission's scripted goals, which read the decoupled applied
        // controls via TrainingHostSite.GetShipControls rather than the sim field).
        if (!_clientBound) PlayerShip.Controls = ZeroThrottle;
        CommanderShip.Controls = ZeroThrottle;
        foreach (var d in _drones.Values) d.Controls = ZeroThrottle;

        foreach (var ship in Ships())
        {
            if (ship.GotoWaypoint is not { } g) continue;
            if (_clientBound && ReferenceEquals(ship, PlayerShip) && !IsAutopilot(ship.Id)) continue;
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

    private static readonly ControlData ZeroThrottle = ControlData.Neutral;   // Reset() throttle -1 = kill (igc.h:1666-1670)

    private IEnumerable<Ship> Ships()
    {
        foreach (var d in _drones.Values) yield return d;
        yield return PlayerShip;
        yield return CommanderShip;
    }

    /// <summary>The home station's position (native <c>OT_station 1010</c> etc.; the home station is
    /// the player's side's station in the start sector — see <see cref="HomeStationId"/>).</summary>
    public Vector3 StationPosition
        => _stations.TryGetValue(HomeStationId, out var hs) ? hs.Position : Vector3.Zero;

    /// <summary>A map station's position record, or <c>null</c> when the id is not a map station
    /// (native <c>pMission-&gt;GetModel(OT_station, id)</c>, e.g. Mission 8's enemy outpost 1051).</summary>
    public Vector3? TryGetStationPosition(int stationId)
        => _stations.TryGetValue(stationId, out var hs) ? hs.Position : (Vector3?)null;

    /// <summary>Resolve a training waypoint buoy to its live geometry surface (native
    /// <c>GetModel(OT_buoy, id)</c>, createwaypointaction.cpp:34-46); <c>null</c> when the buoy
    /// has not been created. Lets the client <c>Resolve</c> a <see cref="ObjectType.Buoy"/>
    /// through the embedded host's waypoint table.</summary>
    public Allegiance.ClientCore.Training.ITrainingObject? ResolveWaypoint(int buoyId)
        => !_releasedConsumers.Contains(buoyId) && _waypoints.TryGetValue(buoyId, out var wp)
            ? new WaypointObject(new Allegiance.ClientCore.Training.TargetRef(
                Allegiance.ClientCore.Training.ObjectType.Buoy, buoyId), wp)
            : null;

    /// <summary>Geometry surface for a host waypoint record (native <c>OT_buoy</c>).</summary>
    private sealed class WaypointObject : Allegiance.ClientCore.Training.ITrainingObject
    {
        private readonly Waypoint _wp;
        public WaypointObject(Allegiance.ClientCore.Training.TargetRef r, Waypoint wp) { Ref = r; _wp = wp; }
        public Allegiance.ClientCore.Training.TargetRef Ref { get; }
        public float Radius => 10f;
        public System.Numerics.Vector3 Position => _wp.Position;
        public System.Numerics.Vector3 Velocity => System.Numerics.Vector3.Zero;
        public System.Numerics.Vector3 Forward => System.Numerics.Vector3.UnitX;
        public int SectorId => _wp.SectorId;
    }

    // ── ITrainingHost handlers ──────────────────────────────────────────────────
    public void CreateWaypoint(int buoyId, Vector3 position, int sectorId, int buoyType, bool visible)
    {
        lock (_simGate) _waypoints[buoyId] = new Waypoint(position, sectorId, buoyType, visible);
    }

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
        lock (_simGate)
        {
            int id = shipId == -1 ? NextId() : shipId;
            var hull = Mission.Core.HullTypes.TryGetValue((short)hullId, out var h) ? h
                : Mission.Core.HullTypes.Values.OrderBy(x => x.HullId).First();
            var ship = new Ship((short)id, hull, Coll.Vector3.Zero, Coll.Vector3.Zero, Orientation.Identity)
            {
                // Native's c_ptPlayer (10) is a player-piloted ship, not an NPC: it gets no
                // server AI at all (training/mission3.cpp:222 creates the inert derelict hulls
                // this way). Treating them as NPCs let the damage-retreat / default-order AI
                // fly the target hulls out of the lesson. Wingman/other pilot types stay NPCs.
                SideId = (short)sideId, IsNpc = pilotType < Ship.PilotTypePlayer,
                PilotType = (byte)pilotType, Name = name,
            };
            Mission.PlaceShip(ship, Mission.Sectors[0]);
            // P775 — native training creates the drone's player info when it
            // creates the drone (trainingmission.cpp:620-623): announce the new
            // drone to the whole mission, exactly like CreateStockDrone
            // (FedSrv.CPP:2269).
            _server.AnnounceNewShip(ship);
            EquipDefaultTrainingLoadout(ship); // identity precedes equipment, training/trainingmission.cpp:620-629
            _drones[id] = ship;
            _droneBehaviours[id] = behaviour;      // c_wbbm* mask (createdroneaction.cpp:91-198 host intake)
            _droneStationTypes[id] = stationType;  // constructor station type (NA=-1)
            _droneTechLevels[id] = techLevel;      // negative tech-level loadout
            // Apply the wingman behaviour to the real sim ship so it actually behaves as
            // configured (SetWingmanBehaviour, createdroneaction.cpp:201-203). Full tech-level
            // loadout application remains the TRN-0010 gap; constructor base data is applied below.
            ship.WingmanBehaviour = behaviour;
            if (stationType >= 0 && Mission.Core.StationTypes.TryGetValue((short)stationType, out var baseType))
            {
                ship.BuilderStationType = baseType; // CreateDroneAction.cpp:86-87, mission4.cpp:866
                ship.WaitsForBuildOrder = true; // mission4.cpp:930-931 pauses it until the next explicit Build
            }
            return id;
        }
    }

    public void PlaceDrone(int shipId, int sectorId, Vector3 position, Vector3 forward, Vector3 up,
        int? commandTargetShipId, int commandId)
    {
        lock (_simGate)
        {
            if (!_drones.TryGetValue(shipId, out var ship))
            {
                if (shipId == CommanderShipId) ship = CommanderShip;
                else return;
            }
            var ori = Orientation.Identity;
            ori.Set(ToCore(forward), ToCore(up));
            ship.Position = ToCore(position);
            ship.Velocity = Coll.Vector3.Zero;   // placing a drone sets its state; clears any residual sim velocity
            ship.SetOrientation(ori);
            var sector = Mission.Sectors.FirstOrDefault(s => s.Id == sectorId);
            if (sector is not null && ship.Sector != sector)
            {
                // training/CreateDroneAction.cpp:75-80 calls SetCluster, which replaces
                // membership (Igc/shipIGC.h:215-227), rather than adding a second entry.
                ship.Sector?.Remove(ship);
                Mission.PlaceShip(ship, sector);
            }
            if (commandTargetShipId is { } && commandId == TrainingMissionIds.CmdGoto)
                ship.SetGoto(new GotoOrder(GotoKind.Waypoint, (short)sectorId, ToCore(position)));
        }
    }

    public void IssueCommand(int shipId, int command, int? targetShipId, int? targetBuoyId, int? targetStationId, int commandId, int? targetAsteroidId = null, int? targetWarpId = null)
    {
        lock (_simGate)
        {
            Ship? ship = ResolveShip(shipId);
            if (ship is null) return;
            // Only an accepted or current order sets the command id a GetCommandCondition sees; a queued
            // order (c_cmdQueued) merely presents a prompt until the pilot accepts it (getcommandcondition.cpp:44-49).
            // (A queued Goto still arms the autopilot pass here so a proof that accepts it can fly; the M8
            // driver pins the bomber to the player's live position, so the player's own queued "meet me"
            // flight does not break M8's within-radius gate.)
            if (command != TrainingMissionIds.CommandQueued)
                _commands[shipId] = commandId;

            // Mirror the write onto the real ship slots (native SetCommandAction calls
            // pShip->SetCommand, setcommandaction.cpp:69-84) so a later read of the ship's
            // own c_cmdCurrent/c_cmdAccepted target sees it — the same state native's
            // GetTargetCondition/GetCommandCondition read. A buoy has no runtime Model in
            // the training arc, so its target stays on the host's waypoint table.
            Model? model = null;
            if (targetShipId is { } tsid) model = ResolveShip(tsid);
            else if (targetStationId is { } stid)
                model = EquipmentTarget.Resolve(ship.Sector, IgcObjectType.Station, (short)stid);
            if (targetAsteroidId is { } aid)
                model = Mission.Sectors.Select(sec => EquipmentTarget.Resolve(sec, IgcObjectType.Asteroid, (short)aid)).FirstOrDefault(m => m is not null);
            if (targetWarpId is { } wid)
                model = Mission.Sectors.Select(sec => EquipmentTarget.Resolve(sec, IgcObjectType.Warp, (short)wid)).FirstOrDefault(m => m is not null);
            if (targetBuoyId is { } buoyId && _waypoints.TryGetValue(buoyId, out var waypoint))
                model = new Buoy(new DataBuoyIGC { BuoyId = (short)buoyId, ClusterId = (short)waypoint.SectorId,
                    Position = [waypoint.Position.X, waypoint.Position.Y, waypoint.Position.Z] });
            if (command == TrainingMissionIds.CommandQueued && _clientBound && ReferenceEquals(ship, PlayerShip))
            {
                var ot = targetBuoyId.HasValue ? IgcObjectType.Buoy : EquipmentTarget.TypeOf(model);
                _server.QueueCommandForPlayer(ship, (short)commandId, model is Buoy ? null : model, ot,
                    model?.Id ?? -1, CommanderShip.Id);
                return;
            }
            if (command is TrainingMissionIds.CommandCurrent or TrainingMissionIds.CommandAccepted)
            {
                ship.SetCommand((short)command, model, (short)commandId);
                // M4's networked host delegates navigation/combat/construction to the sim.
                // Other variants still use AdvanceAutopilot's scripted movement owner.
                if (ship.IsNpc && _clientBound && StartSectorId == EnemyEngagement.StartSector)
                {
                    ship.AttackTarget = commandId == TrainingMissionIds.CmdAttack ? model as Ship : null;
                    ship.BuilderAsteroidTarget = commandId == TrainingMissionIds.CmdBuild && targetAsteroidId is { } rock ? ResolveAsteroid(rock) : null;
                    ship.PlanTarget = model is EquipmentTarget { Body: Station st } ? st.Data : null;
                    if (commandId == TrainingMissionIds.CmdBuild && ship.BuilderAsteroidTarget is { } buildRock)
                        ship.SetBuilderBuild(buildRock);
                    ship.PlanCommandId = (short)commandId;
                }
            }

            if (commandId == TrainingMissionIds.CmdGoto)
            {
                if (targetBuoyId is { } bid && _waypoints.TryGetValue(bid, out var wp))
                {
                    _commandTargets[shipId] = new TargetRef(ObjectType.Buoy, bid);
                    ship.SetGoto(new GotoOrder(GotoKind.Waypoint, (short)wp.SectorId, ToCore(wp.Position)));
                }
                else if (targetShipId is { } tsid2 && ResolveShip(tsid2) is { } target)
                {
                    _commandTargets[shipId] = new TargetRef(ObjectType.Ship, tsid2);
                    ship.SetGoto(new GotoOrder(GotoKind.Waypoint, target.Sector!.Id, target.Position));
                }
                else if (targetStationId is { } stid && _stations.TryGetValue(stid, out var station))
                {
                    _commandTargets[shipId] = new TargetRef(ObjectType.Station, stid);
                    // Native flies a station goto through the bay approach and docks on
                    // arrival (Waypoint::c_oDock, common.cpp:1505-1545). The real ship's
                    // plan slot drives that native approach (Ship.ApplyNpcAutopilot →
                    // TryDockApproach); a plain waypoint goto would stop at the centre
                    // instead and never enter a bay. Set the real station plan and clear
                    // the waypoint goto so the plan branch owns the tick.
                    var runtime = Mission.Sectors.SelectMany(s => s.StationRuntimes)
                        .FirstOrDefault(s => s.StationId == stid);
                    if (runtime is not null)
                    {
                        ship.PlanTarget = runtime.Data;
                        ship.PlanCommandId = (short)commandId;
                        ship.SetGoto(null);
                    }
                    else
                    {
                        ship.SetGoto(new GotoOrder(GotoKind.Waypoint, (short)station.SectorId, ToCore(station.Position)));
                    }
                }
            }
            else
            {
                _commandTargets[shipId] = null;
                ship.SetGoto(null);   // c_cidDoNothing / None clear the current target (setcommandaction.cpp:77-84)
            }
        }
    }

    public void SetAutopilot(int shipId, bool on)
    {
        lock (_simGate)
        {
            Ship? ship = ResolveShip(shipId);
            if (ship is null) return;
            // Real native ship field (IshipIGC::SetAutopilot, setautopilotaction.cpp:41-43);
            // the navigation pass reads it so a player under a station plan keeps flying.
            ship.Autopilot = on;
            _autopilot[ship.Id] = on;
            if (!on) ship.SetGoto(null);
        }
    }

    public bool IsAutopilot(int shipId)
        => ResolveShip(shipId) is { } s ? s.Autopilot : _autopilot.TryGetValue((short)shipId, out var b) && b;

    /// <summary>The live ammunition count of a ship (<c>IshipIGC::GetAmmo()</c>, shipIGC.cpp:…;
    /// a ship with no mounted ammo magazine reports 0 — native <c>Ship::GetAmmo</c> sums its
    /// mounted <c>ET_Magazine</c> parts, absent here for a bare hull).</summary>
    public int GetAmmo(int shipId)
        => ResolveShip(shipId) is { } s ? s.Ammo : 0;

    /// <summary>The live probe count of the host's world (<c>GetProbes()-&gt;n()</c>, igc.cpp:…):
    /// every live (undead) <see cref="Probe"/> in every sector's model list — the same real
    /// world query the probe-visibility rule uses (<c>Mission.CanSideSeeCluster</c>).</summary>
    public int GetProbeCount()
    {
        int n = 0;
        foreach (var sector in Mission.Sectors)
            foreach (var m in sector.Models)
                if (m is Probe p && !p.Dead) n++;
        return n;
    }

    /// <summary>Resolve a real asteroid from the mission's runtime <see cref="Sector.Mineable"/>
    /// lists (native <c>pMission->GetModel(OT_asteroid, id)</c>, getsectorcondition.cpp:27-51).
    /// An asteroid is a live runtime rock with <see cref="Asteroid.Data"/> and a
    /// <see cref="Asteroid.Sector"/>.</summary>
    public Asteroid? ResolveAsteroid(int id)
    {
        foreach (var sector in Mission.Sectors)
            foreach (var a in sector.Mineable)
                if (a.Data.AsteroidId == id) return a;
        return null;
    }

    /// <summary>Resolve a real probe model from the mission's sector model lists
    /// (native <c>pMission->GetModel(OT_probe, id)</c>); <c>null</c> when absent.</summary>
    public Probe? ResolveProbe(int id)
    {
        foreach (var sector in Mission.Sectors)
            foreach (var m in sector.Models)
                if (m is Probe p && p.Id == id && !p.Dead) return p;
        return null;
    }

    /// <summary>Damage a ship by a collision amount, then reset controls and fire all weapons —
    /// <c>DoDamageAdjustableAction::Execute</c> (dodamageadjustableaction.cpp:27-36):
    /// <c>ReceiveDamage</c> then <c>SetStateBits(weaponsMaskIGC|buttonsMaskIGC, allWeaponsIGC)</c>
    /// and <c>SetControls(controls.Reset())</c>.</summary>
    public void DamageShipAdjustable(int shipId, float amount)
    {
        lock (_simGate)
        {
            Ship? ship = ResolveShip(shipId);
            if (ship is null) return;
            var v = new Coll.Vector3(-1f, 0f, 0f);
            ship.ReceiveDamage(CollisionDamageType, amount, Mission.LastUpdate, v, v, null);
            // native SetStateBits(weaponsMaskIGC|buttonsMaskIGC, allWeaponsIGC): arm "every gun
            // fires" (AllWeapons, igc.h:826) and clear the movement/button bits (igc.h:810-824).
            ship.StateM = (ship.StateM & ~(ShipControlState.WeaponsMask | ShipControlState.MoveButtonsMask))
                | ShipControlState.AllWeapons;
            ship.Controls = ControlData.Neutral;
            // Make the damage observable to GetFraction/GetShipFraction exactly like DamageShip:
            // lower the tracked armour fraction by the same per-point scaling.
            float cur = _shipFractions.TryGetValue(shipId, out var f) ? f : 1f;
            _shipFractions[shipId] = Math.Clamp(cur - amount / 20000f, 0f, 1f);
        }
    }

    /// <summary><c>c_dmgidCollision &amp; c_dmgidMask = row 0</c> (missionigc.h:155; the same
    /// byte <c>Sector</c> uses for its collision pass, Sector.cs:2851).</summary>
    private const byte CollisionDamageType = 0;

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
            ship.Sector?.Terminate(ship);          // P754b: destroyed drone = real termination
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

    public TargetRef? GetCommandTarget(int shipId)
    {
        // A scripted target (the mission's CreateUniverse/headless driver) owns the slot;
        // otherwise read the ship's LIVE c_cmdCurrent target (shipIGC.h:2624; the read behind
        // GetTargetCondition, gettargetcondition.cpp:52) — the real model the server wrote from
        // an ordinary client ORDER_CHANGE. A buoy has no runtime Model, so it only exists in
        // the scripted slot.
        if ((!_clientBound || shipId != PlayerShipId) && _commandTargets.TryGetValue(shipId, out var scripted)) return scripted;
        if (ResolveShip(shipId) is { } ship && ship.CommandTarget is { } model)
        {
            var type = EquipmentTarget.TypeOf(model);
            ObjectType? trainingType = type switch
            {
                IgcObjectType.Ship => ObjectType.Ship,
                IgcObjectType.Station => ObjectType.Station,
                IgcObjectType.Asteroid => ObjectType.Asteroid,
                IgcObjectType.Probe => ObjectType.Probe,
                IgcObjectType.Buoy => ObjectType.Buoy,
                IgcObjectType.Warp => ObjectType.Warp,
                _ => null,
            };
            if (trainingType is { } t && model.Id >= 0) return new TargetRef(t, model.Id);
        }
        return null;
    }

    public int GetCommandId(int shipId)
        => _clientBound && shipId == PlayerShipId ? (PlayerShip.AcceptedShipCommandId != TrainingMissionIds.CmdNone
            ? PlayerShip.AcceptedShipCommandId : PlayerShip.CurrentCommandId)
            : _commands.TryGetValue(shipId, out var c) ? c
            : ResolveShip(shipId) is { } ship ? ship.CurrentCommandId : TrainingMissionIds.CmdNone;

    // ── M5/M6 command-view + kill-credit state ──────────────────────────────────
    /// <summary>Set which model the player has picked/selected in the command view (native
    /// console picked object, <c>GetPickedCondition.cpp:37</c>).</summary>
    public void SetPicked(TargetRef? picked) => _pickedRef = picked;
    public bool IsPicked(TargetRef model)
        => _pickedRef is { } p && p.Type == model.Type && p.Id == model.Id;

    /// <summary>The sector the player is currently viewing (native <c>trekClient.GetViewCluster()</c>).</summary>
    public int ViewSector => _viewSector;
    public void SetViewSector(int sector) => _viewSector = sector;

    /// <summary>Set the last command the player acknowledged (queued-order accept, native
    /// <c>CommandAcknowledgedCondition::SetCommandAcknowledged</c>, commandacknowledgedcondition.cpp:43-46).</summary>
    public void AcknowledgeCommand(int commandId) => _commandAcknowledged = commandId;
    public int GetCommandAcknowledged() => _commandAcknowledged;

    /// <summary>Armour fraction (0 = destroyed/absent, 1 = pristine) behind <c>GetShipIsDamagedCondition</c>.
    /// Stations read the host's tracked armour fraction (<see cref="_stationFractions"/>), so Mission 8's
    /// damaged-then-repaired home station and destroyed enemy outpost resolve against the real host.</summary>
    public float GetFraction(TargetRef model)
    {
        if (model.Type == ObjectType.Ship)
        {
            if (IsDestroyed(model.Id)) return 0f;
            // The REAL ship armour fraction (IshipIGC::GetFraction) when the model is live;
            // the host's tracked value remains the floor for damage actions that only
            // record a fraction (Mission 8's scripted bomber dent).
            float tracked = _shipFractions.TryGetValue(model.Id, out var f) ? f : 1f;
            return ResolveShip(model.Id) is { } ship ? Math.Min(tracked, ship.Fraction) : tracked;
        }
        if (model.Type == ObjectType.Station)
            return GetStationFraction(model.Id);
        return ResolveShip(model.Id) is { } ? 1f : 0f;
    }

    /// <summary>A ship's armour fraction (1 = full, 0 = destroyed; <c>IshipIGC::GetFraction</c>). By
    /// default a live drone is 1; <see cref="DamageShip"/> lowers it so Mission 8's "bomber hull
    /// damaged" gate (<c>GetShipIsDamagedCondition(bomber, 1.0)</c>, mission8.cpp:586) can fire
    /// without destroying the bomber.</summary>
    public float GetShipFraction(int shipId)
        => IsDestroyed(shipId) ? 0f : (_shipFractions.TryGetValue(shipId, out var f) ? f : 1f);

    /// <summary>Knock a ship's armour down by <paramref name="amount"/> (the damaged-but-alive outcome
    /// the headless driver scripts for Mission 8's bomber; <c>Ship.ReceiveDamage</c>, shipIGC.cpp:1212,
    /// is the real-client realisation).</summary>
    public void DamageShip(int shipId, float amount)
    {
        float cur = IsDestroyed(shipId) ? 0f : (_shipFractions.TryGetValue(shipId, out var f) ? f : 1f);
        float nf = Math.Clamp(cur - amount / 20000f, 0f, 1f);
        _shipFractions[shipId] = nf;
        // Also write the real ship armour so GetFraction's real-model read observes the damage
        // (the damaged-but-alive outcome Mission 8's bomber gate needs).
        if (ResolveShip(shipId) is { } s) s.SetFraction(nf);
    }

    /// <summary>
    /// <c>DoDamageAction::Execute</c> (<c>dodamageaction.cpp:26-49</c>) on the player's real
    /// ship: the shield demo drops a mounted shield by 3% of its max strength until it is at
    /// 20%; the hull demo sets the shield to 20% (if above 25%) or dents the hull by 7.5
    /// points. The HUD gauges read the real shield/hull fractions, so both are observable.
    /// </summary>
    public void DealDamage(bool toHull)
    {
        lock (_simGate)
        {
            var ship = PlayerShip;
            var shield = ship.Parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Shield && p.MountId == 0);
            if (!toHull)
            {
                if (shield is not null && ship.ShieldFraction > 0.2f
                    && shield.Type.Type is DataPartTypeIGC.Shield st)
                {
                    ship.ReceiveDamage(CollisionDamageType, st.MaxStrength * 0.03f, Mission.LastUpdate,
                        new Coll.Vector3(-1f, 0f, 0f), new Coll.Vector3(1f, 0f, 0f), null);
                }
            }
            else if (shield is not null && ship.ShieldFraction > 0.25f)
            {
                ship.SetShieldFraction(0.2f);
            }
            else if (ship.Fraction > 0.5f)
            {
                ship.ReceiveDamage(CollisionDamageType, 7.5f, Mission.LastUpdate,
                    new Coll.Vector3(-1f, 0f, 0f), new Coll.Vector3(1f, 0f, 0f), null);
            }
        }
    }

    /// <summary>
    /// <c>ToggleWeaponAction::Execute</c> (<c>toggleweaponaction.cpp:41-47</c> —
    /// <c>GetWindow()->ToggleWeapon(mount)</c>): select <paramref name="mount"/> as the
    /// firing weapon, or switch to group fire when it is already the selected one. The
    /// selection lives in the real <see cref="Ship.StateM"/> selected-weapon bits.
    /// </summary>
    public void ToggleWeapon(int mount)
    {
        lock (_simGate)
        {
            var ship = PlayerShip;
            int selected = ship.StateM & ShipControlState.SelectedWeaponMask;
            if (selected == (mount & ShipControlState.SelectedWeaponMask))
                ship.StateM |= ShipControlState.AllWeapons;           // fGroupFire = true
            else
                ship.StateM = (ship.StateM & ~ShipControlState.SelectedWeaponMask)
                    | (mount & ShipControlState.SelectedWeaponMask);  // SetSelectedWeapon(mount)
        }
    }

    /// <summary>
    /// <c>SwapWeaponAction::Execute</c> (<c>swapweaponaction.cpp:25-32</c> —
    /// <c>GetWindow()->SwapPart(equipmentType, mount)</c>): move the first cargo part of the
    /// requested equipment type into <paramref name="mount"/> through the real cargo-aware
    /// swap (<see cref="Ship.TrySwapPartWithCargo"/>), which returns the part previously in
    /// the mount to that cargo slot. Nothing in cargo is native's silent no-op.
    /// </summary>
    public void SwapWeapon(int equipmentType, int mount)
    {
        lock (_simGate)
        {
            var ship = PlayerShip;
            var et = (EquipmentType)equipmentType;
            for (int i = 0; i < ship.CargoHold.Count; i++)
            {
                if (ship.CargoHold[i] is { } part && part.EquipmentType == et)
                {
                    ship.TrySwapPartWithCargo(et, -(i + 1), mount);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// <c>SetSignatureAction::Execute</c> (<c>setsignatureaction.cpp:49-53</c> →
    /// <c>ImodelIGC::SetSignature</c>, modelIGC.h:283-286): write a signature override onto a
    /// real ship — the Mission 4 cloak-signature lesson (mission4.cs:442) raises the four enemy
    /// miners' signatures to 1.0e6 so they dominate the scan-visibility math
    /// (<c>Sector.ResolveCrossSideVisibility</c>). The override lives on the real
    /// <see cref="Ship.EffectiveSignature"/> so visibility reads observe it.
    /// </summary>
    public void SetSignature(int shipId, float signature)
    {
        lock (_simGate)
        {
            if (ResolveShip(shipId) is { } s) s.SetSignature(signature);
        }
    }

    /// <summary>A station's armour fraction (1 = pristine, 0 = destroyed; <c>IstationIGC</c> reads
    /// behind <c>GetShipIsDamagedCondition(OT_station,…)</c>). Unknown station ids report 0.</summary>
    public float GetStationFraction(int stationId)
        => _stationFractions.TryGetValue(stationId, out var f) ? f
            : (_stations.ContainsKey(stationId) ? 1f : 0f);

    /// <summary>Knock a station's armour down by <paramref name="amount"/> — the write behind
    /// <c>DamageStationAction</c> (Mission 8 goal 1's start action, mission8.cpp:203). Mission 8
    /// damages 12500; scaled to a meaningful armour-fraction drop (~0.017 per 1000).</summary>
    public void DamageStation(int stationId, float amount)
    {
        float cur = _stationFractions.TryGetValue(stationId, out var f) ? f : 1f;
        _stationFractions[stationId] = Math.Clamp(cur - amount / 20000f, 0f, 1f);
    }

    /// <summary>Restore a station to full armour (the repaired outcome the nanite gun produces; the
    /// headless proof scripts it, mirroring how <see cref=\"RecordKillCredit\"/> scripts a kill).</summary>
    public void RepairStation(int stationId) => _stationFractions[stationId] = 1f;

    /// <summary>TRUE when a ship has a part of the given part type mounted — <c>GetShipHasMountedCondition</c>
    /// (getshiphasmountedcondition.cpp:36-45): a part in the ship's real <see cref="Ship.Parts"/>
    /// whose part-type id matches and whose mount id is &gt;= 0 (a mounted, not cargo, slot). The
    /// scripted <see cref="_mountedParts"/> set is kept as an OR so the headless proof can still
    /// script the mounting outcome where the sim's part model cannot represent the part.</summary>
    public bool HasPartMounted(int shipId, int partId)
        => _mountedParts.Contains((shipId, partId))
           || (ResolveShip(shipId) is { } s && s.Parts.Any(p => p.PartId == partId && p.MountId >= 0));

    /// <summary>Mount a part on a real ship — the write behind <c>MountPart</c>
    /// (setdronenanaction.cpp:36-52). The part type is resolved from the mission core
    /// (<c>mission.Core.PartTypes</c>, with expenditure-launcher construction as a fallback);
    /// the ship's existing part on the same mount/equipment-type slot is terminated first
    /// (native's terminate-then-create), then the new part is mounted as a real <see cref="Ship.Parts"/>
    /// entry. When the part type is unresolvable the scripted set is used so the headless proof
    /// still records the outcome.</summary>
    public void MountPart(int shipId, int partId, int mount = 0)
    {
        lock (_simGate)
        {
            Ship? s = ResolveShip(shipId);
            if (s is not { Mission: { } mission })
            {
                _mountedParts.Add((shipId, partId));
                return;
            }
            DataPartTypeIGC? resolved = null;
            if (mission.Core.PartTypes.TryGetValue((short)partId, out var p))
                resolved = p;
            else if (mission.Core.TryBuildExpendableLauncher((short)partId, out p))
                resolved = p;
            if (resolved is not { } part || !s.HullType.CanMountPart(part, mount))
            {
                _mountedParts.Add((shipId, partId));
                return;
            }
            // setdronenanaction.cpp:36-40 — terminate any existing part on the target slot first.
            s.TryRemovePart(part.EquipmentType, (byte)mount, out _);
            s.MountPart(part, short.MaxValue, unchecked((byte)mount), ExpandedPartData.FullyMounted);
            _mountedParts.Add((shipId, partId));
        }
    }

    // ── P880b — real-model equipment/state/radar/buoy seams ─────────────────
    /// <summary>TRUE when a mounted part of the ship has the given equipment type — the read behind
    /// <c>GetShipHasCargoCondition</c> (getshiphascargocondition.cpp:29-36), which walks the ship's
    /// whole part list — native <c>m_parts</c> holds cargo too (shipIGC.cpp:1460-1464) — comparing
    /// each part's equipment type; the port keeps cargo in <see cref="Ship.CargoHold"/>.</summary>
    public bool HasPartOfEquipmentType(int shipId, EquipmentType part)
        => ResolveShip(shipId) is { } s
           && (s.Parts.Any(p => p.EquipmentType == part) || s.CargoHold.Any(p => p?.EquipmentType == part));

    /// <summary>Snapshot a ship's real physical/equipment state (<c>ResetShipAction</c> first
    /// <c>Execute</c>, resetshipaction.cpp:22-36).</summary>
    public ShipSnapshot CaptureShip(int shipId)
    {
        if (ResolveShip(shipId) is not { } s) return ShipSnapshot.Empty;
        return new ShipSnapshot
        {
            Position = ToNum(s.Position),
            Velocity = ToNum(s.Velocity),
            Orientation = s.Orientation,
            Fuel = s.Fuel,
            Shield = s.ShieldFraction,
            Hull = s.Fraction,
            Ammo = s.Ammo,
            Energy = s.Energy,
        };
    }

    /// <summary>Restore a ship's real state from a snapshot (<c>ResetShipAction</c> later
    /// <c>Execute</c>s, resetshipaction.cpp:24-35). A zero shield fraction leaves the shield as-is
    /// (native only restores the shield when a shield is mounted).</summary>
    public void RestoreShip(int shipId, ShipSnapshot snap)
    {
        lock (_simGate)
        {
            if (ResolveShip(shipId) is not { } s) return;
            s.Position = ToCore(snap.Position);
            s.Velocity = ToCore(snap.Velocity);
            s.SetOrientation(snap.Orientation);
            s.SetFuel(snap.Fuel);
            if (snap.Shield > 0f) s.SetShieldFraction(snap.Shield);
            s.SetFraction(snap.Hull);
            s.Ammo = snap.Ammo;
            s.Energy = snap.Energy;
        }
    }

    /// <summary>Release a waypoint buoy's consumer (<c>ReleaseConsumerAction</c>,
    /// releaseconsumeraction.cpp:25-28 — <c>IbuoyIGC::ReleaseConsumer</c>); the released buoy no
    /// longer resolves through <see cref="ResolveWaypoint"/>.</summary>
    public void ReleaseConsumer(int buoyId)
    {
        lock (_simGate) _releasedConsumers.Add(buoyId);
    }

    /// <summary>The host radar LOD mode (<c>SetRadarLODAction</c>, setradarlodaction.cpp:31-36;
    /// the headless stand-in for the client window's <c>SetRadarMode</c>).</summary>
    public int RadarLod { get; set; }
    public void SetRadarLod(int lod) => RadarLod = lod;

    /// <summary>The native <c>SetCargoNanAction</c> cargo recipe (setcargonanaction.cpp:22-52):
    /// ensure a nanite weapon (part 190) is carried on the front mount and fill the cargo hold
    /// with gatling (33) + proximity mines (59). Best-effort against the mission core — parts the
    /// universe cannot represent are skipped, mirroring how <see cref="EquipDefaultTrainingLoadout"/>
    /// tolerates unresolvable part ids.</summary>
    public void SetCargoNan(int shipId)
    {
        lock (_simGate)
        {
            if (ResolveShip(shipId) is not { } s || s.Mission is not { } mission) return;
            DataPartTypeIGC? Part(short id) => mission.Core.PartTypes.TryGetValue(id, out var p)
                || mission.Core.TryBuildExpendableLauncher(id, out p) ? p : null;
            // setdronenanaction.cpp:36-40 — terminate any existing front weapon, then mount nanite 190.
            s.TryRemovePart(EquipmentType.Weapon, 0, out _);
            if (Part(190) is { } nan && s.HullType.CanMountPart(nan, 0))
                s.MountPart(nan, 0, 0, ExpandedPartData.FullyMounted);
            // Fill the cargo hold: slot -1 gatling 33, remaining slots prox 59 (0x7fff amount).
            DataPartTypeIGC? gat = Part(33);
            DataPartTypeIGC? prox = Part(59);
            for (int slot = 0; slot < Ship.CargoCapacity; slot++)
            {
                s.ClearCargoSlot(slot);
                var item = slot == 0 ? gat : prox;
                if (item is { } ip)
                    s.MountPart(ip, short.MaxValue, unchecked((byte)(-slot - 1)), ExpandedPartData.FullyMounted);
            }
        }
    }

    /// <summary>Repair and re-arm a ship — <c>RepairRearmAction::Execute</c>
    /// (repairrearmaction.cpp:37-69): the mount-0 shield and the hull to 1, energy/ammo/fuel to the
    /// hull maxima (GA-scaled energy via <see cref="Ship.EffectiveMaxEnergy"/>), then the cargo walk
    /// exactly as the native loops execute it. Every loop runs <c>i &gt; -c_maxCargo</c>, so mount
    /// -c_maxCargo is never filled; the first empty mount gets a missile launcher (150); the
    /// "second slot" loop has no <c>break</c>, so it fills <em>every</em> remaining empty mount with
    /// ammo (24) and the "subsequent slots" loop starts exhausted. Native code, not its comments.</summary>
    public void RepairRearm(int shipId)
    {
        lock (_simGate)
        {
            if (ResolveShip(shipId) is not { } s) return;
            if (s.Parts.Any(p => p.EquipmentType == EquipmentType.Shield && p.MountId == 0))
                s.SetShieldFraction(1f);
            s.SetFraction(1f);
            _shipFractions.Remove(shipId);   // the tracked dent heals with the real hull
            s.Energy = s.EffectiveMaxEnergy();
            s.Ammo = s.HullType.MaxAmmo;
            s.SetFuel(s.HullType.MaxFuel);
            if (s.Mission is not { } mission) return;
            DataPartTypeIGC? Part(short id) => mission.Core.PartTypes.TryGetValue(id, out var p)
                || mission.Core.TryBuildExpendableLauncher(id, out p) ? p : null;
            void Fill(int mount, short partId)
            {
                if (Part(partId) is { } part)
                    s.MountPart(part, short.MaxValue, unchecked((byte)mount), ExpandedPartData.FullyMounted);
            }
            bool Empty(int mount) => s.CargoHold[Ship.CargoSlotForMount(mount)] is null;
            int i;
            for (i = -1; i > -Ship.CargoCapacity; i--)
                if (Empty(i)) { Fill(i, 150); break; }
            for (; i > -Ship.CargoCapacity; i--)
                if (Empty(i)) Fill(i, 24);
            for (; i > -Ship.CargoCapacity; i--)
                if (Empty(i)) Fill(i, 150);
        }
    }

    /// <summary>Bind the mission that receives the real kill-credit count (native ShipKilled hook).</summary>
    public void SetActiveMission(TrainingMission mission) => _activeMission = mission;

    /// <summary>
    /// Apply a <em>real kill-credit</em> outcome to the active mission's kill counter — native
    /// <c>TrainingMission::ShipKilled</c> (<c>trainingmission.cpp:515-524</c>): the launcher must be on
    /// the player's side and the victim a wingman (<c>c_ptWingman</c>). The server's real kill-credit
    /// path (which already excludes ejections via the <c>!bLifepod</c> gate) routes here; the headless
    /// proof scripts a kill outcome through this same single method (mirroring how
    /// <see cref="DestroyDrone"/> scripts the destroyed outcome).
    /// </summary>
    public void RecordKillCredit(int launcherId, int victimId)
    {
        var launcher = ResolveShip(launcherId);
        var victim = ResolveShip(victimId);
        bool playerSide = launcher is not null && launcher.SideId == PlayerShip.SideId;
        bool wingman = victim is not null && victim.PilotType == Ship.PilotTypeWingman; // c_ptWingman
        _activeMission?.ShipKilled(playerSide && wingman);
    }


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
        return _drones.TryGetValue(id, out var d) && d.Sector is not null && !d.Dead ? d : null;
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
