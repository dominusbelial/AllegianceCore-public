namespace Allegiance.Core.Sim;

/// <summary>
/// The seven native visual explosion categories, byte-for-byte the
/// <c>ExplosionType</c> constants of <c>src/Igc/igc.h:36-45</c>. Each value
/// selects a row of the native <c>WinTrekClient::AddExplosion</c> table
/// (<c>WinTrek/trekigc.cpp:95-177</c>) that fixes the sprite count, fireball
/// radius, shock-wave radius, tint colour, and sprite-film set for that kind of
/// object. The server derives the <em>category</em> of every real blast (what
/// died) and sends it to the client, which renders from the matching preset —
/// this is the faithful split of native's
/// <c>IclusterSite::AddExplosion(position, radius, explosionType)</c>
/// (<c>clusterIGC.cpp:407</c>) across this port's server-authoritative sim.
/// </summary>
public static class ExplosionType
{
    /// <summary><c>c_etMine = 1</c> — a mining charge detonation.</summary>
    public const int Mine = 1;

    /// <summary><c>c_etMissile = c_etProjectile = 2</c> — a missile / projectile
    /// impact (both natives share the id, <c>igc.h:38-39</c>).</summary>
    public const int Missile = 2;

    /// <summary><c>c_etSmallShip = c_etProbe = 3</c> — a fighter-class ship or
    /// a probe (<c>igc.h:40-41</c>).</summary>
    public const int SmallShip = 3;

    /// <summary><c>c_etBigShip = 4</c> — a non-fighter (capital-scale) ship.</summary>
    public const int BigShip = 4;

    /// <summary><c>c_etSmallStation = 5</c> — a non-victory station.</summary>
    public const int SmallStation = 5;

    /// <summary><c>c_etAsteroid = 6</c> — a destroyed asteroid.</summary>
    public const int Asteroid = 6;

    /// <summary><c>c_etLargeStation = 7</c> — a victory-condition station.</summary>
    public const int LargeStation = 7;
}
