using System.Numerics;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// World-creation actions — the C# ports of <c>CreateWaypointAction</c>, <c>CreateProbeAction</c>,
/// <c>CreateDroneAction</c> and <c>SetCommandAction</c>.
/// </summary>

/// <summary>
/// Spawns a waypoint buoy — port of <c>CreateWaypointAction</c> (<c>CreateWaypointAction.cpp:21-48</c>).
/// </summary>
public sealed class CreateWaypointAction : Action
{
    private readonly ITrainingSite _site;
    private readonly int _buoyId;
    private readonly Vector3 _position;
    private readonly int _sectorId;
    private readonly int _type;
    private readonly bool _visible;

    public CreateWaypointAction(ITrainingSite site, int buoyId, Vector3 position, int sectorId, int type = 1, bool visible = true)
    {
        _site = site;
        _buoyId = buoyId;
        _position = position;
        _sectorId = sectorId;
        _type = type;
        _visible = visible;
    }

    public override void Execute() => _site.CreateWaypoint(_buoyId, _position, _sectorId, _type, _visible);
}

/// <summary>
/// Spawns a probe — port of <c>CreateProbeAction</c> (<c>CreateProbeAction.cpp:21-49</c>).
/// </summary>
public sealed class CreateProbeAction : Action
{
    private readonly ITrainingSite _site;
    private readonly int _probeId;
    private readonly int _typeId;
    private readonly Vector3 _position;
    private readonly int _sideId;
    private readonly int _sectorId;

    public CreateProbeAction(ITrainingSite site, int probeId, int typeId, Vector3 position, int sideId, int sectorId)
    {
        _site = site;
        _probeId = probeId;
        _typeId = typeId;
        _position = position;
        _sideId = sideId;
        _sectorId = sectorId;
    }

    public override void Execute() => _site.CreateProbe(_probeId, _typeId, _position, _sideId, _sectorId);
}

/// <summary>
/// Creates (and places) a drone ship — port of <c>CreateDroneAction</c>
/// (<c>CreateDroneAction.cpp:27-212</c>): name, ship/hull/side/pilot ids, spawn
/// sector/position/orientation, optional spawn command, wingman behaviour, station/expendable type
/// and the tech-level weapon/booster/missile upgrade block (lines 91-198). The tech-level and
/// behaviour fields are carried to the site (TR-2 host applies them when seating the drone on the
/// embedded server, which runs its own drone/wingman AI — <c>Ship.NpcUpdates.cs</c>, SIM-0111..0118).
/// The native default wingman behaviour (use missiles, run at 60% hull, line 43) is <see cref="DefaultWingmanBehaviour"/>.
/// </summary>
public sealed class CreateDroneAction : Action
{
    public const int DefaultWingmanBehaviour = 0x10 | 0x20; // c_wbbmUseMissiles | c_wbbmRunAt60Hull

    private readonly ITrainingSite _site;
    private readonly string _name;
    private readonly int _shipId;
    private int _hullId;
    private readonly int _sideId;
    private readonly int _pilotType;
    private int _techLevel = 1;                 // native ctor default (createdroneaction.cpp:33)
    private int _stationTypeId = -1;            // NA
    private int _wingmanBehaviour = DefaultWingmanBehaviour; // createdroneaction.cpp:42

    private int _sectorId = -1;              // NA
    private Vector3 _position;
    private Vector3 _forward = new(1f, 0f, 0f);
    private Vector3 _up = new(0f, 0f, 1f);
    private TargetRef? _commandTarget;
    private int _commandId;

    public CreateDroneAction(ITrainingSite site, string name, int shipId, int hullId, int sideId, int pilotType)
    {
        _site = site;
        _name = name;
        _shipId = shipId;
        _hullId = hullId;
        _sideId = sideId;
        _pilotType = pilotType;
    }

    public override void Execute()
    {
        int id = _site.CreateDrone(_name, _shipId, _hullId, _sideId, _pilotType,
            behaviour: _wingmanBehaviour, stationType: _stationTypeId, techLevel: _techLevel);
        _site.PlaceDrone(id, _sectorId, _position, _forward, _up, _commandTarget, _commandId);
    }

    public int ShipId => _shipId;

    public void SetCreatedLocation(int sectorId, Vector3 position)
    {
        _sectorId = sectorId;
        _position = position;
    }

    public void SetCreatedOrientation(Vector3 forward, Vector3 up)
    {
        _forward = forward;
        _up = up;
    }

    public void SetCreatedCommand(int targetId, ObjectType targetType, int commandId)
    {
        _commandTarget = new TargetRef(targetType, targetId);
        _commandId = commandId;
    }

    public void SetCreatedHullType(int hullId) => _hullId = hullId;

    /// <summary><c>SetCreatedBehaviour</c> (<c>createdroneaction.cpp:185-187</c>) — wingman
    /// behaviour bitmask (<c>c_wbbm*</c>, igc.h:786-791), e.g. 0x50 = InRangeAggressive|UseMissiles.</summary>
    public void SetCreatedBehaviour(int wingmanBehaviour) => _wingmanBehaviour = wingmanBehaviour;

    /// <summary><c>SetStationType</c> (<c>createdroneaction.cpp:177-179</c>) — the station the
    /// drone is a constructor for (<c>m_stationTypeID</c>).</summary>
    public void SetStationType(int stationTypeId) => _stationTypeId = stationTypeId;

    /// <summary><c>SetCreatedTechLevel</c> (<c>createdroneaction.cpp:189-191</c>) — the negative
    /// tech level driving the weapon/booster/missile upgrade block (<c>createdroneaction.cpp:91-198</c>);
    /// 1 (default) applies no upgrades.</summary>
    public void SetCreatedTechLevel(int techLevel) => _techLevel = techLevel;
}

/// <summary>
/// Sets a command on a ship (toward an optional target) — port of <c>SetCommandAction</c>
/// (<c>setcommandaction.cpp:28-96</c>). The native special-cases issuing a queued order to the player
/// through the window's queued-command UI; the C# port funnels both through
/// <see cref="ITrainingSite.IssueCommand"/>, letting TR-2 reproduce that distinction in the host.
/// Command ids: <c>c_cidDoNothing=-3, c_cidNone=-1, c_cidGoto=5</c> (<c>igc.h:898-912</c>).
/// </summary>
public sealed class SetCommandAction : Action
{
    public const int CmdDoNothing = -3;
    public const int CmdNone = -1;
    public const int CmdGoto = 5;

    public const int CommandQueued = 0;
    public const int CommandAccepted = 1;
    public const int CommandCurrent = 2;

    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;
    private readonly int _command;
    private readonly TargetRef? _target;
    private readonly int _commandId;

    public SetCommandAction(ITrainingSite site, TargetRef ship, int command, TargetRef? target, int commandId)
    {
        _site = site;
        _ship = ship;
        _command = command;
        _target = target;
        _commandId = commandId;
    }

    public override void Execute() => _site.IssueCommand(_ship, _command, _target, _commandId);
}
