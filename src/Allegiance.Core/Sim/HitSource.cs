namespace Allegiance.Core.Sim;

/// <summary>Telemetry only: what dealt a ship's killing blow. Native never records
/// it; FM_S_KILL_SHIP carries just the credited model (Inc/Messages.h:696-703),
/// and the credit is the top damage dealer, not the final hit (shipIGC.cpp:1319-1331).</summary>
public enum HitKind : byte { None, Gun, Missile, Mine, Blast, Collision, OutOfBounds, Oxygen }

/// <summary>The source of one hit: kind, the weapon's launcher name when the core
/// has one (missiles and mines), the core damage type and the sim time.</summary>
public readonly record struct HitSource(HitKind Kind, string? Name, byte DamageType, double Time)
{
    public string Describe() => Name is { Length: > 0 }
        ? $"{Kind.ToString().ToLowerInvariant()}:{Name}"
        : Kind.ToString().ToLowerInvariant();
}
