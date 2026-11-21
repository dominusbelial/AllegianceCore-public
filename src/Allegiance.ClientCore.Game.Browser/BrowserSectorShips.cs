namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    /// <summary>Wire-owned membership, read under the session gate. Visibility is
    /// still checked with CanDrawInSector; membership alone reveals nothing.</summary>
    public IReadOnlyList<ShipEntity> ShipsInSector(short sector) => _ships.InSector(sector);
}
