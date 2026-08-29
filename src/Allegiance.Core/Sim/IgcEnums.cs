namespace Allegiance.Core.Sim;

// Scalar enums translated verbatim from src/Igc/igc.h so the IIgcSite
// signatures stay faithful. Underlying widths match the C++ typedefs.
//
// Time is represented as double SECONDS (not the C++ uint32 millisecond
// accumulator from zlib/ztime.h) because the C# codebase has no existing time
// type and no global clock; the engine's tick math is float-seconds anyway.
// See README "The IIgcSite seam — decisions" for the rationale.

/// <summary><c>ChatTarget</c> — src/Igc/igc.h:2729.</summary>
public enum ChatTarget : short
{
    Everyone = 0,
    Leaders = 1,
    Admin = 2,
    Ship = 3,
    Allies = 4,
    Team = 5,
    Individual = 6,
    IndividualNoFilter = 7,
    Wing = 8,
    IndividualEcho = 9,
    AllSector = 10,
    FriendlySector = 11,
    Group = 12,
    GroupNoEcho = 13,
    NoSelection = 14,
}

/// <summary><c>BucketChange</c> — src/Igc/igc.h:4748.</summary>
public enum BucketChange : short
{
    Emptied = 0,
    Terminated = 1,
    MoneyChange = 2,
    PercentCompleteChange = 3,
}

/// <summary><c>LoadoutChange</c> — src/Igc/igc.h:4732.</summary>
public enum LoadoutChange : short
{
    Mounted = 0,
    Dismounted = 1,
    Added = 2,
    Removed = 3,
    HullChange = 4,
    QuantityChange = 5,
    AddPassenger = 6,
    RemovePassenger = 7,
    PassengerMove = 8,
    TurretChange = 9,
    Disembark = 10,
    CargoSelectionChanged = 11,
}
