namespace Allegiance.Core.Model;

/// <summary>Treasure subset of native MissionParams (Igc/igc.h:1160-1161,1180-1183).
/// Defaults: igc.h:1287-1290,1321-1322. Rates are treasures per second per sector.</summary>
public sealed class TreasureParameters
{
    public float PlayerSectorTreasureRate { get; init; } = 0.2f / 60f;
    public float NeutralSectorTreasureRate { get; init; } = 0.5f / 60f;
    public short PlayerRegenerateSet { get; init; } = 1;
    public short NeutralRegenerateSet { get; init; } = 1;
    public short PlayerStartSet { get; init; } = 1;
    public short NeutralStartSet { get; init; } = 1;
    public bool AllowDevelopments { get; init; } = true;

    public void Validate()
    {
        // MissionParams::Validate (igc.h:1384-1390).
        if (!float.IsFinite(PlayerSectorTreasureRate) || PlayerSectorTreasureRate < 0 || PlayerSectorTreasureRate > 1f / 60f
            || !float.IsFinite(NeutralSectorTreasureRate) || NeutralSectorTreasureRate < 0 || NeutralSectorTreasureRate > 1f / 60f)
            throw new InvalidDataException("Treasure rates must be finite and between 0 and 1/60 per second.");
    }
}
