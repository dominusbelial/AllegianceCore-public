namespace Allegiance.ClientCore.Training;

/// <summary>
/// Mission 8 (Nanite) condition/action seams — the C# ports of <c>GetShipHasMountedCondition</c>
/// (<c>getshiphasmountedcondition.cpp:22-46</c>), <c>SetDroneNanAction</c>
/// (<c>setdronenanaction.cpp:33-52</c>) and the station-damage start action Mission 8 goal 1 uses
/// (<c>mission8.cpp:203</c>, <c>IstationIGC::ReceiveDamage</c>, <c>stationIGC.cpp:147-156</c>).
/// </summary>

/// <summary>
/// TRUE when a ship has a part of the given part type mounted — port of
/// <c>GetShipHasMountedCondition</c> (<c>getshiphasmountedcondition.cpp:36-45</c>): true when any
/// part in the ship's part list has that part-type object id and a mount id &gt;= 0 (a mounted,
/// not cargo, slot). Mission 8 goal 1 gates on the player mounting the Nanite Repair System
/// (part 190).
/// </summary>
public sealed class GetShipHasMountedCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;
    private readonly int _partId;

    public GetShipHasMountedCondition(ITrainingSite site, TargetRef ship, int partId)
    {
        _site = site;
        _ship = ship;
        _partId = partId;
    }

    public override bool Evaluate() => _site.HasPartMounted(_ship, _partId);
}

/// <summary>
/// Mounts the Nanite Repair System (part 190) on a drone — port of <c>SetDroneNanAction</c>
/// (<c>setdronenanaction.cpp:36-52</c>): the native terminates the drone's existing front weapon
/// and sticks Nanite 2 (part 190) on the front mount. Mission 8 goal 3 equips the nan wingman
/// (<c>mission8.cpp:465</c>).
/// </summary>
public sealed class SetDroneNanAction : Action
{
    /// <summary>The Nanite Repair System part id (native <c>mission8.cpp:133</c> mounts part 190).</summary>
    public const int NanitePartId = 190;

    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;

    public SetDroneNanAction(ITrainingSite site, TargetRef ship)
    {
        _site = site;
        _ship = ship;
    }

    public override void Execute() => _site.MountPart(_ship, NanitePartId);
}

/// <summary>
/// Damages a station's armour — the write behind Mission 8 goal 1's start action
/// (<c>mission8.cpp:203</c>: <c>pStation-&gt;ReceiveDamage(c_dmgidNoWarn, 12500.0f, ...)</c>), which knocks
/// the home station down so the player can practise repairing it with the nanite gun. The repair
/// side is a negative amount (<c>stationIGC.cpp:147-156</c>); the player's nanite gun fires are the
/// real-client realisation, while the headless proof scripts the repaired outcome via the host.
/// </summary>
public sealed class DamageStationAction : Action
{
    private readonly ITrainingSite _site;
    private readonly int _stationId;
    private readonly float _amount;

    public DamageStationAction(ITrainingSite site, int stationId, float amount)
    {
        _site = site;
        _stationId = stationId;
        _amount = amount;
    }

    public override void Execute() => _site.DamageStation(_stationId, _amount);
}
