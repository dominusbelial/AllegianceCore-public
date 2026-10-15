namespace Allegiance.Core.Model;

/// <summary>Native MissionParams defaults (Igc/igc.h:1246-1249,1330).</summary>
public sealed class ResearchSettings
{
    public bool AllowDevelopments { get; set; } = true;
    public bool AllowShipyardPath { get; set; } = true;
    public bool AllowExpansionPath { get; set; } = true;
    public bool AllowTacticalPath { get; set; } = true;
    public bool AllowSupremacyPath { get; set; } = true;
    public int InitialMinersPerTeam { get; set; } = 1;

    public void Validate()
    {
        if (InitialMinersPerTeam is < 0 or > 127)
            throw new InvalidDataException("initialMinersPerTeam must be between 0 and 127 (native char)");
    }

    public TechTreeBitMask StartingTechs(DataCivilizationIGC civ)
    {
        // Igc/missionigc.cpp:3869-3883; constants Igc/igc.h:572-575.
        if (!AllowDevelopments) return TechTreeBitMask.FromBytes(civ.TtbmNoDevTechs);
        var bytes = (byte[])civ.TtbmBaseTechs.Clone();
        if (AllowShipyardPath) bytes[0] |= 0x40;
        if (AllowExpansionPath) bytes[0] |= 0x20;
        if (AllowTacticalPath) bytes[0] |= 0x10;
        if (AllowSupremacyPath) bytes[0] |= 0x08;
        return TechTreeBitMask.FromBytes(bytes);
    }
}
