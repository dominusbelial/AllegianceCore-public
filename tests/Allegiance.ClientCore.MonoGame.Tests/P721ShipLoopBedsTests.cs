using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P721 — pins the exact sounddef.mdl waves behind the own-ship thruster/
/// interior/turn/sidethrust loop beds, resolved by hull class (fighter vs
/// capital). This is the faithful port of native TrekClient::UpdateSounds'
/// per-hull-type loop SoundIDs (trekigc.cpp:1307-1355; sounddef.mdl:1012-1024,
/// 1342-1347). Kept as a pure mapping so the test fails fast on a mis-read
/// asset table without needing an audio device. Failing-first: ShipLoopBeds
/// did not exist before P721, so this test cannot compile on the pre-change
/// tree (CS0246, the same failing-first mechanism P711/P716/P717 documented).
/// </summary>
public class P721ShipLoopBedsTests
{
    [Theory]
    [InlineData(false, "accelsmall")]  // smallShipThrustInteriorSound (sounddef.mdl:1014)
    [InlineData(true, "accelcapital")] // capitalShipThrustInteriorSound (:1021)
    public void MainThruster_BedAssetFollowsHullClass(bool capital, string wave)
        => Assert.Equal(wave, Allegiance.ClientCore.MonoGame.Audio.ShipLoopBeds.MainThruster(capital));

    [Theory]
    [InlineData(false, "sshipint")]    // smallShipInteriorSound (:1012)
    [InlineData(true, "lshipint")]     // capitalShipInteriorSound (:1019)
    public void Interior_BedAssetFollowsHullClass(bool capital, string wave)
        => Assert.Equal(wave, Allegiance.ClientCore.MonoGame.Audio.ShipLoopBeds.Interior(capital));

    [Theory]
    [InlineData(false, "adjust")]      // smallShipTurnInteriorSound (:1016)
    [InlineData(true, "accelsmall")]   // capitalShipTurnInteriorSound (:1023)
    public void Turn_BedAssetFollowsHullClass(bool capital, string wave)
        => Assert.Equal(wave, Allegiance.ClientCore.MonoGame.Audio.ShipLoopBeds.Turn(capital));

    [Theory]
    [InlineData(false, "accelsmall")]  // SidethrustInteriorSound (:1344)
    [InlineData(true, "accelcapital")] // CapSidethrustInteriorSound (:1346)
    public void SideThrust_BedAssetFollowsHullClass(bool capital, string wave)
        => Assert.Equal(wave, Allegiance.ClientCore.MonoGame.Audio.ShipLoopBeds.SideThrust(capital));

    [Theory]
    [InlineData((short)300, false, "adjust")]       // smallShipTurnInteriorSound (:1016)
    [InlineData((short)320, true, "accelsmall")]    // capitalShipTurnInteriorSound (:1023)
    [InlineData((short)340, false, "adjust")]       // smallShipTurnExteriorSound (:1017)
    [InlineData((short)360, true, "accelsmall")]    // capitalShipTurnExteriorSound (:1024)
    [InlineData((short)0, false, "adjust")]         // unknown id → class fallback
    [InlineData((short)0, true, "accelsmall")]      // unknown id → class fallback
    [InlineData((short)999, false, "adjust")]       // unknown id → class fallback
    public void TurnExterior_ResolvesHullSoundId_WithClassFallback(short soundId, bool capital, string wave)
        => Assert.Equal(wave, Allegiance.ClientCore.MonoGame.Audio.ShipLoopBeds.TurnExterior(soundId, capital));

    [Fact]
    public void ExteriorBed_IsShipout()
        => Assert.Equal("shipout", Allegiance.ClientCore.MonoGame.Audio.ShipLoopBeds.Exterior);

    [Fact]
    public void AfterburnerBed_IsIafter()
        => Assert.Equal("iafter", Allegiance.ClientCore.MonoGame.Audio.ShipLoopBeds.Afterburner);
}
