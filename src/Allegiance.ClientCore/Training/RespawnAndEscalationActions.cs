using System.Numerics;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// Mission 6 (Practice Arena) escalation/respawn actions — the C# ports of <c>AddRespawnAction</c>
/// (<c>addrespawnaction.cpp:17-34</c>) and <c>ModifyCreateDroneActionAction</c>
/// (<c>modifycreatedroneactionaction.cpp:10-74</c>), both of which operate on a
/// <see cref="CreateDroneAction"/> held by the mission.
/// </summary>

/// <summary>
/// Registers a <see cref="CreateDroneAction"/> on the training mission's respawn list — port of
/// <c>AddRespawnAction</c> (<c>addrespawnaction.cpp:31-34</c>): <c>g_pMission-&gt;AddRespawn(cda,
/// downtime, spawnGroup, spawnOnAdd)</c>. The mission's <see cref="TrainingMission.Frame"/> respawn
/// pass (<c>trainingmission.cpp:176-221</c>) re-runs the action after the drone dies and its downtime
/// elapses.
/// </summary>
public sealed class AddRespawnAction : Action
{
    private readonly TrainingMission _mission;
    private readonly CreateDroneAction _cda;
    private readonly float _downtime;
    private readonly int _spawnGroup;
    private readonly bool _spawnOnAdd;

    public AddRespawnAction(TrainingMission mission, CreateDroneAction cda, float downtime,
        int spawnGroup = -1, bool spawnOnAdd = false)
    {
        _mission = mission;
        _cda = cda;
        _downtime = downtime;
        _spawnGroup = spawnGroup;   // NA = -1
        _spawnOnAdd = spawnOnAdd;
    }

    public override void Execute() => _mission.AddRespawn(_cda, _downtime, _spawnGroup, _spawnOnAdd);
}

/// <summary>
/// Mutates an existing <see cref="CreateDroneAction"/>'s spawn parameters — port of
/// <c>ModifyCreateDroneActionAction</c> (<c>modifycreatedroneactionaction.cpp:10-74</c>). Mission 6
/// uses these to escalate enemy waves (behaviour, tech level, hull, name, location, spawn command).
/// The native applies each modification at <see cref="Execute"/> only if it differs from the NA /
/// -100 sentinel (<c>modifycreatedroneactionaction.cpp:57-74</c>).
/// </summary>
public sealed class ModifyCreateDroneActionAction : Action
{
    private readonly CreateDroneAction _cda;

    private int _locationSector = -1;       // NA
    private Vector3 _locationPosition;
    private int _behaviour = -1;            // NA
    private int _techLevel = -100;
    private int _hullId = -1;               // NA
    private string? _name;
    private bool _changeName;
    private int _commandTargetId = -100;
    private ObjectType _commandTargetType;
    private int _commandId;

    public ModifyCreateDroneActionAction(CreateDroneAction cda) => _cda = cda;

    public void ModifyCreatedBehaviour(int behaviour) => _behaviour = behaviour;
    public void ModifyCreatedTechLevel(int techLevel) => _techLevel = techLevel;
    public void ModifyCreatedHullType(int hullId) => _hullId = hullId;
    public void ModifyCreatedName(string name)
    {
        _name = name;
        _changeName = true;
    }
    public void ModifyCreatedLocation(int sectorId, Vector3 position)
    {
        _locationSector = sectorId;
        _locationPosition = position;
    }
    public void ModifyCreatedCommand(int targetId, ObjectType targetType, int commandId)
    {
        _commandTargetId = targetId;
        _commandTargetType = targetType;
        _commandId = commandId;
    }

    public override void Execute()
    {
        // modifycreatedroneactionaction.cpp:57-74 — apply only non-sentinel modifications.
        if (_behaviour > -1) _cda.SetCreatedBehaviour(_behaviour);
        if (_techLevel > -100) _cda.SetCreatedTechLevel(_techLevel);
        if (_hullId > -1) _cda.SetCreatedHullType(_hullId);
        if (_changeName) _cda.SetCreatedName(_name!);
        if (_locationSector > -1) _cda.SetCreatedLocation(_locationSector, _locationPosition);
        if (_commandTargetId > -100) _cda.SetCreatedCommand(_commandTargetId, _commandTargetType, _commandId);
    }
}
