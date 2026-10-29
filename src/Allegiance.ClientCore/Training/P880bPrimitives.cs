using Allegiance.Core;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// P880b framework primitives — the C# ports of <c>GetShipHasCargoCondition</c>,
/// <c>ReleaseConsumerAction</c>, <c>ResetShipAction</c>, <c>SetRadarLODAction</c> and
/// <c>SetCargoNanAction</c>. Each is a thin wrapper over a <see cref="ITrainingSite"/> seam so the
/// networked lesson and the embedded host share one real-model binding.
/// </summary>

/// <summary>
/// TRUE when a mounted part of the ship has the given equipment type — port of
/// <c>GetShipHasCargoCondition</c> (<c>getshiphascargocondition.cpp:29-36</c>). Native walks the
/// ship's mounted part list (<c>GetParts()</c>, which does <em>not</em> include the cargo hold)
/// comparing each part's <c>GetEquipmentType()</c> to the requested type. Despite the name the check
/// is over mounted parts and matches by <em>equipment type</em>, not a specific part id (that is
/// <c>GetShipHasMountedCondition</c>'s job). <paramref name="part"/> is an <see cref="EquipmentType"/>
/// (native <c>ET_*</c>), e.g. <c>ET_Shield = 4</c>.
/// </summary>
public sealed class GetShipHasCargoCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;
    private readonly EquipmentType _part;

    public GetShipHasCargoCondition(ITrainingSite site, TargetRef ship, EquipmentType part)
    {
        _site = site;
        _ship = ship;
        _part = part;
    }

    public override bool Evaluate() => _site.HasPartOfEquipmentType(_ship, _part);
}

/// <summary>
/// Releases the consumer attached to a waypoint buoy — port of <c>ReleaseConsumerAction</c>
/// (<c>releaseconsumeraction.cpp:25-28</c>: <c>m_buoy-&gt;ReleaseConsumer()</c>). Native uses this to
/// tear down the waypoint's consumer (the object that reacts to a ship reaching the waypoint). The
/// host realises it as marking the buoy's consumer released so a released buoy no longer resolves.
/// </summary>
public sealed class ReleaseConsumerAction : Action
{
    private readonly ITrainingSite _site;
    private readonly int _buoyId;

    public ReleaseConsumerAction(ITrainingSite site, int buoyId)
    {
        _site = site;
        _buoyId = buoyId;
    }

    public override void Execute() => _site.ReleaseConsumer(_buoyId);
}

/// <summary>
/// Snapshots a ship's physical/equipment state on its first <c>Execute</c> and restores it on every
/// later <c>Execute</c> — port of <c>ResetShipAction</c> (<c>resetshipaction.cpp:22-36</c>). Native
/// lazily allocates its <c>State</c> on the first Execute (when the ship has a mission) and re-applies
/// position/velocity/orientation/rotation/fuel/shield/hull/ammo/energy thereafter. This port keeps the
/// same latch semantics: the first Execute captures, subsequent Executes restore. Native's rotation
/// (angular velocity) has no modelled C# counterpart and is not captured.
/// </summary>
public sealed class ResetShipAction : Action
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;
    private ShipSnapshot? _snapshot;

    public ResetShipAction(ITrainingSite site, TargetRef ship)
    {
        _site = site;
        _ship = ship;
    }

    public override void Execute()
    {
        if (_snapshot is { } snap)
            _site.RestoreShip(_ship, snap);
        else
            _snapshot = _site.CaptureShip(_ship);
    }
}

/// <summary>
/// Sets the window's radar LOD mode — port of <c>SetRadarLODAction</c> (<c>setradarlodaction.cpp:31-36</c>:
/// <c>SetRadarMode(static_cast&lt;int&gt;(m_radarLOD))</c> when the current mode differs). <paramref name="lod"/>
/// is the integer <see cref="Rendering.RadarMode"/> (All/Default/Target/Minimal). HUD-visible, so the
/// client realises it through the shared radar tracker used by both HUD styles.
/// </summary>
public sealed class SetRadarLODAction : Action
{
    private readonly ITrainingSite _site;
    private readonly int _lod;

    public SetRadarLODAction(ITrainingSite site, int lod)
    {
        _site = site;
        _lod = lod;
    }

    public override void Execute() => _site.SetRadarLod(_lod);
}

/// <summary>
/// The native <c>SetCargoNanAction</c> primitive (setcargonanaction.cpp:22-52): ensure the ship
/// carries a nanite weapon and fill its cargo hold with gatling + proximity mines. Native ships no
/// mission ever instantiates it (Mission 8 uses <c>SetDroneNanAction</c> instead), so it is retained
/// as a modding primitive; the host realises the cargo recipe against the real <see cref="Ship"/>.
/// </summary>
public sealed class SetCargoNanAction : Action
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _ship;

    public SetCargoNanAction(ITrainingSite site, TargetRef ship)
    {
        _site = site;
        _ship = ship;
    }

    public override void Execute() => _site.SetCargoNan(_ship);
}
