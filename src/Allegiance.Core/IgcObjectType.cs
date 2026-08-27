namespace Allegiance.Core;

/// <summary>
/// <c>ObjectType</c> from <c>src/Igc/igc.h:114-160</c>. Underlying type is
/// <c>short</c> (<c>igc.h:115</c>), which is why the record header's type field
/// is 2 bytes.
/// </summary>
public enum IgcObjectType : short
{
    Ship = 0,
    Station = 1,
    Mine = 3,
    Probe = 4,
    Asteroid = 5,
    Warp = 7,
    Treasure = 8,
    Buoy = 9,
    Side = 12,
    Cluster = 13,
    Bucket = 14,
    ProjectileType = 22,
    MissileType = 23,
    MineType = 24,
    ProbeType = 25,
    ChaffType = 26,
    Civilization = 27,
    TreasureSet = 28,
    HullType = 29,
    PartType = 30,
    StationType = 31,
    Development = 32,
    DroneType = 33,
    Constants = 34,
}
