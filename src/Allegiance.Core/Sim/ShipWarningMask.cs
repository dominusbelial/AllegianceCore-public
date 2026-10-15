namespace Allegiance.Core.Sim;

/// <summary>Native igc.h:635-637, reset each unparented ship update (shipIGC.cpp:273).</summary>
[Flags]
public enum ShipWarningMask : byte
{
    None = 0,
    OutOfBounds = 0x01,
    CrowdedSector = 0x02
}
