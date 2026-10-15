using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.State;

/// <summary>Native WinTrek.cpp:8655-8671 rising-edge voices and
/// trekigc.cpp:1285-1286 shared warning loop. Consume edges once per frame.</summary>
public sealed class ShipWarningState
{
    public const string BoundsVoice = "vo_sal_boundryexceeded";
    public const string CrowdingVoice = "vo_sal_sectorcrowded";
    public const string LoopSound = "boundsalert";
    public ShipWarningMask Mask { get; private set; }
    private ShipWarningMask _pending;
    public bool LoopActive => Mask != ShipWarningMask.None;

    public void Apply(ShipWarningMask mask)
    {
        mask &= ShipWarningMask.OutOfBounds | ShipWarningMask.CrowdedSector;
        _pending |= mask & ~Mask;
        Mask = mask;
    }

    public ShipWarningMask TakeRisingEdges()
    {
        var edges = _pending;
        _pending = ShipWarningMask.None;
        return edges;
    }

    public void Reset() { Mask = ShipWarningMask.None; _pending = ShipWarningMask.None; }
}
