namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P721 — the own-ship thruster/interior/turn/sidethrust loop BEDS, resolved by
/// hull class (fighter vs capital). This is the faithful port of native's
/// per-hull-type loop SoundIDs in <c>TrekClient::UpdateSounds</c>
/// (trekigc.cpp:1307-1355), each wave resolved from sounddef.mdl:
/// <list type="bullet">
/// <item>smallShipInteriorSound = sshipint (:1012), capitalShipInteriorSound = lshipint (:1019)</item>
/// <item>smallShipThrustInteriorSound = accelsmall (:1014), capitalShipThrustInteriorSound = accelcapital (:1021)</item>
/// <item>smallShipTurnInteriorSound = adjust (:1016), capitalShipTurnInteriorSound = accelsmall (:1023)</item>
/// <item>SidethrustInteriorSound = accelsmall (:1344), CapSidethrustInteriorSound = accelcapital (:1346)</item>
/// <item>smallShipExteriorSound = shipout (:1013); afterburner interior/exterior = iafter</item>
/// </list>
/// Native distinguishes the INTERIOR vs EXTERIOR rows with two separate loop
/// instances and mutes the wrong volume bus by camera view; the underlying
/// assets are the same pair per bed, so the port keeps one instance per bed and
/// lets <see cref="SoundManager"/> pick it by hull class and camera mode.
/// Keeping this mapping as pure data lets the unit tests pin the exact
/// sounddef.mdl waves (fail-fast against a mis-read asset table) without an
/// audio device.
/// </summary>
public static class ShipLoopBeds
{
    // Interior hum bed.
    public const string FighterInterior = "sshipint";      // smallShipInteriorSound (:1012)
    public const string CapitalInterior = "lshipint";      // capitalShipInteriorSound (:1019)

    // Main-thruster bed.
    public const string FighterMainThruster = "accelsmall";  // smallShipThrustInteriorSound (:1014)
    public const string CapitalMainThruster = "accelcapital";// capitalShipThrustInteriorSound (:1021)

    // Maneuvering (turn) bed.
    public const string FighterTurn = "adjust";             // smallShipTurnInteriorSound (:1016)
    public const string CapitalTurn = "accelsmall";         // capitalShipTurnInteriorSound (:1023)

    // Sidethrust bed (native reuses ONE loop instance for the interior and
    // exterior rows; only the hull class changes the asset).
    public const string FighterSideThrust = "accelsmall";   // SidethrustInteriorSound (:1344)
    public const string CapitalSideThrust = "accelcapital"; // CapSidethrustInteriorSound (:1346)

    // Hull exterior bed (same asset, capital pitches it 0.5; see :1020).
    public const string Exterior = "shipout";               // smallShipExteriorSound (:1013)
    // Afterburner bed (interior and exterior; per-mounted-part sound, iafter default).
    public const string Afterburner = "iafter";

    public static string MainThruster(bool capital) => capital ? CapitalMainThruster : FighterMainThruster;
    public static string Interior(bool capital) => capital ? CapitalInterior : FighterInterior;
    public static string Turn(bool capital) => capital ? CapitalTurn : FighterTurn;
    public static string SideThrust(bool capital) => capital ? CapitalSideThrust : FighterSideThrust;
}
