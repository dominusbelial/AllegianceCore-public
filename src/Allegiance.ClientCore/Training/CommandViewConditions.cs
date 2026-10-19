using System.Numerics;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// Command-view / escalation conditions — the C# ports of the native training conditions Mission 5
/// (Command View) and Mission 6 (Practice Arena) need: <c>GetPickedCondition</c>,
/// <c>GetViewSectorCondition</c>, <c>GetShipIsDamagedCondition</c>, <c>KillsReachedCondition</c> and
/// <c>CommandAcknowledgedCondition</c>.
/// </summary>

/// <summary>
/// TRUE when the given model is the one the player currently has picked (selected) in the command
/// view — port of <c>GetPickedCondition</c> (<c>getpickedcondition.cpp:35-38</c>): compares the
/// console's picked object to the target.
/// </summary>
public sealed class GetPickedCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _target;

    public GetPickedCondition(ITrainingSite site, ObjectType targetType, int targetId)
    {
        _site = site;
        _target = new TargetRef(targetType, targetId);
    }

    public override bool Evaluate() => _site.GetPicked(_target);
}

/// <summary>
/// TRUE when the player is currently viewing the given sector — port of <c>GetViewSectorCondition</c>
/// (<c>getviewsectorcondition.cpp:32-34</c>): the view cluster's object id equals the expected sector.
/// </summary>
public sealed class GetViewSectorCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly int _sector;

    public GetViewSectorCondition(ITrainingSite site, int sector)
    {
        _site = site;
        _sector = sector;
    }

    public override bool Evaluate() => _site.GetViewSector() == _sector;
}

/// <summary>
/// TRUE when a ship's armour fraction differs from an expected value — port of
/// <c>GetShipIsDamagedCondition</c> (<c>getshipisdamagedcondition.cpp:49-59</c>). Useful values of the
/// fraction are <c>0.0f</c> (tell if the ship is dead: true while the fraction is non-zero, i.e. the
/// ship exists with armour) and <c>1.0f</c> (tell if the ship is damaged: true while it has lost hull).
/// When the target is absent the result mirrors native's "assume fraction 0" rule
/// (<c>getshipisdamagedcondition.cpp:58-59</c>).
/// </summary>
public sealed class GetShipIsDamagedCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _target;
    private readonly float _fraction;

    public GetShipIsDamagedCondition(ITrainingSite site, ObjectType objectType, int objectId, float fraction)
    {
        _site = site;
        _target = new TargetRef(objectType, objectId);
        _fraction = fraction;
    }

    public override bool Evaluate()
    {
        var resolved = _site.Resolve(_target);
        if (resolved is null)
            return _fraction != 0.0f;                       // no ship -> assume fraction 0
        return _site.GetFraction(_target) != _fraction;     // getshipisdamagedcondition.cpp:56
    }
}

/// <summary>
/// TRUE once the training mission's real kill count reaches a threshold — port of
/// <c>KillsReachedCondition</c> (<c>killsreachedcondition.cpp:22-26</c>): <c>Training::GetKillCount()
/// >= kills</c>. The count is the <em>real kill credit</em> the mission accumulates (see
/// <see cref="TrainingMission.ShipKilled"/>), driven by the server's kill-credit path — never a
/// separate ad-hoc counter.
/// </summary>
public sealed class KillsReachedCondition : Condition
{
    private readonly TrainingMission _mission;
    private readonly int _kills;

    public KillsReachedCondition(TrainingMission mission, int kills)
    {
        _mission = mission;
        _kills = kills;
    }

    public override bool Evaluate() => _mission.GetKillCount() >= _kills;
}

/// <summary>
/// TRUE when the last command acknowledged by the player matches the expected command — port of
/// <c>CommandAcknowledgedCondition</c> (<c>commandacknowledgedcondition.cpp:21-46</c>): a static
/// "last acknowledged command" that the queued-order accept path sets via
/// <see cref="SetCommandAcknowledged"/>, evaluated here against the target command.
/// </summary>
public sealed class CommandAcknowledgedCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly int _targetCommand;

    public CommandAcknowledgedCondition(ITrainingSite site, int cTargetCommand)
    {
        _site = site;
        _targetCommand = cTargetCommand;
    }

    public override bool Evaluate() => _site.GetCommandAcknowledged() == _targetCommand;
}
