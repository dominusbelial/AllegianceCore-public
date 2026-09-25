using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// Pure tests for the P374 <see cref="ClusterWarnings"/> classifier — the port
/// of native's asset-mask / cluster-warning machinery (igc.h:4350-4387,
/// common.cpp:2490-2551) that drives the sector-threat sounds + starmap icons.
/// </summary>
public class ClusterWarningsTests
{
    private static DataHullTypeIGC Hull(short habm) =>
        default(DataHullTypeIGC) with { HabmCapabilities = habm };

    private static Ship ShipOf(DataHullTypeIGC hull, byte pilotType = Ship.PilotTypePlayer) =>
        new((short)1, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { PilotType = pilotType };

    // ── GetAssetMask ───────────────────────────────────────────────────────

    [Fact]
    public void AssetMask_FriendlyBuilder_PilotType() =>
        Assert.Equal(ClusterWarnings.AmBuilder, ClusterWarnings.GetAssetMask(ShipOf(Hull(0), Ship.PilotTypeBuilder), friendly: true));

    [Fact]
    public void AssetMask_EnemyMiner_PilotType() =>
        Assert.Equal(ClusterWarnings.AmEnemyMiner, ClusterWarnings.GetAssetMask(ShipOf(Hull(0), Ship.PilotTypeMiner), friendly: false));

    [Fact]
    public void AssetMask_EnemyCarrier_PilotType() =>
        Assert.Equal(ClusterWarnings.AmEnemyCarrier, ClusterWarnings.GetAssetMask(ShipOf(Hull(0), Ship.PilotTypeCarrier), friendly: false));

    [Fact]
    public void AssetMask_FriendlyFighter_HullFighterBit() =>
        Assert.Equal(ClusterWarnings.AmFighter, ClusterWarnings.GetAssetMask(ShipOf(Hull(DataHullTypeIGC.HabmFighter)), friendly: true));

    [Fact]
    public void AssetMask_EnemyFighter_HullFighterBit() =>
        Assert.Equal(ClusterWarnings.AmEnemyFighter, ClusterWarnings.GetAssetMask(ShipOf(Hull(DataHullTypeIGC.HabmFighter)), friendly: false));

    [Fact]
    public void AssetMask_EnemyAPCFighter() =>
        Assert.Equal((short)(ClusterWarnings.AmEnemyFighter | ClusterWarnings.AmEnemyAPC),
            ClusterWarnings.GetAssetMask(ShipOf(Hull((short)(DataHullTypeIGC.HabmBoard | DataHullTypeIGC.HabmCaptureThreat | DataHullTypeIGC.HabmFighter))), friendly: false));

    [Fact]
    public void AssetMask_EnemyAPCNonFighter() =>
        Assert.Equal((short)(ClusterWarnings.AmEnemyCapital | ClusterWarnings.AmEnemyAPC),
            ClusterWarnings.GetAssetMask(ShipOf(Hull((short)(DataHullTypeIGC.HabmBoard | DataHullTypeIGC.HabmCaptureThreat))), friendly: false));

    [Fact]
    public void AssetMask_EnemyBomberFighter() =>
        Assert.Equal((short)(ClusterWarnings.AmEnemyFighter | ClusterWarnings.AmEnemyBomber),
            ClusterWarnings.GetAssetMask(ShipOf(Hull((short)(DataHullTypeIGC.HabmThreatToStation | DataHullTypeIGC.HabmFighter))), friendly: false));

    [Fact]
    public void AssetMask_EnemyBomberNonFighter() =>
        Assert.Equal((short)(ClusterWarnings.AmEnemyCapital | ClusterWarnings.AmEnemyBomber),
            ClusterWarnings.GetAssetMask(ShipOf(Hull(DataHullTypeIGC.HabmThreatToStation)), friendly: false));

    [Fact]
    public void AssetMask_EnemyFighterRipcordTarget_AddsTeleportShip() =>
        Assert.Equal((short)(ClusterWarnings.AmEnemyFighter | ClusterWarnings.AmEnemyTeleportShip),
            ClusterWarnings.GetAssetMask(ShipOf(Hull((short)(DataHullTypeIGC.HabmFighter | DataHullTypeIGC.HabmIsRipcordTarget))), friendly: false));

    [Fact]
    public void AssetMask_FriendlyLifepod_IsZero() =>
        Assert.Equal((short)0, ClusterWarnings.GetAssetMask(ShipOf(Hull(DataHullTypeIGC.HabmLifepod)), friendly: true));

    // ── GetWarning priority ladder ─────────────────────────────────────────

    [Theory]
    [InlineData((short)(ClusterWarnings.AmStation | ClusterWarnings.AmEnemyAPC), ClusterWarnings.StationCaptureThreat)]
    [InlineData((short)(ClusterWarnings.AmStation | ClusterWarnings.AmEnemyTeleport), ClusterWarnings.StationTeleportThreat)]
    [InlineData((short)(ClusterWarnings.AmStation | ClusterWarnings.AmEnemyBomber), ClusterWarnings.StationThreatened)]
    [InlineData(ClusterWarnings.AmEnemyAPC, ClusterWarnings.TransportInCluster)]
    [InlineData(ClusterWarnings.AmEnemyTeleportShip, ClusterWarnings.TeleportInCluster)]
    [InlineData(ClusterWarnings.AmEnemyCapital, ClusterWarnings.CapitalInCluster)]
    [InlineData(ClusterWarnings.AmEnemyCarrier, ClusterWarnings.EnemyCarrierInCluster)]
    [InlineData(ClusterWarnings.AmEnemyBomber, ClusterWarnings.BomberInCluster)]
    [InlineData((short)(ClusterWarnings.AmEnemyFighter | ClusterWarnings.AmCarrier), ClusterWarnings.CarrierThreatened)]
    [InlineData((short)(ClusterWarnings.AmEnemyFighter | ClusterWarnings.AmBuilder), ClusterWarnings.BuilderThreatened)]
    [InlineData((short)(ClusterWarnings.AmEnemyFighter | ClusterWarnings.AmMiner), ClusterWarnings.MinerThreatened)]
    [InlineData((short)(ClusterWarnings.AmEnemyFighter | ClusterWarnings.AmFighter), ClusterWarnings.CombatInCluster)]
    [InlineData(ClusterWarnings.AmEnemyBuilder, ClusterWarnings.EnemyBuilderInCluster)]
    [InlineData(ClusterWarnings.AmEnemyMiner, ClusterWarnings.EnemyMinerInCluster)]
    [InlineData(ClusterWarnings.AmEnemyFighter, ClusterWarnings.EnemyFighterInCluster)]
    [InlineData((short)0, ClusterWarnings.NoThreat)]
    public void Warning_Ladder(short am, short expected) =>
        Assert.Equal(expected, ClusterWarnings.GetWarning(am, bInvulnerableStations: false));

    [Fact]
    public void Warning_InvulnerableStations_CollapsesStationThreats()
    {
        // With bInvulnerableStations=true the station-capture/teleport/bomber
        // branches are gated off; a lone APC in a sector with a station degrades
        // to NoThreat (no APC+bomber branch reaches a level).
        Assert.Equal(ClusterWarnings.NoThreat,
            ClusterWarnings.GetWarning((short)(ClusterWarnings.AmStation | ClusterWarnings.AmEnemyAPC), bInvulnerableStations: true));
    }

    // ── GetWarningText ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(ClusterWarnings.NoThreat, "No threat")]
    [InlineData(ClusterWarnings.MinerThreatened, "Miner at risk")]
    [InlineData(ClusterWarnings.BomberInCluster, "Enemy bomber spotted")]
    [InlineData(ClusterWarnings.CapitalInCluster, "Enemy capital ship spotted")]
    [InlineData(ClusterWarnings.StationThreatened, "Station at risk")]
    [InlineData(ClusterWarnings.StationCaptureThreat, "Station at risk of capture")]
    public void WarningText_MatchesNative(short cw, string expected) =>
        Assert.Equal(expected, ClusterWarnings.GetWarningText(cw));

    [Fact]
    public void WarningText_OutOfRange_IsEmpty() =>
        Assert.Equal("", ClusterWarnings.GetWarningText(99));

    // ── Alert sound stems (native c_vAlertSounds) ──────────────────────────

    [Theory]
    [InlineData(ClusterWarnings.NoThreat, "")]
    [InlineData(ClusterWarnings.MinerThreatened, "vo_detect_miner")]
    [InlineData(ClusterWarnings.BuilderThreatened, "vo_detect_builder")]
    [InlineData(ClusterWarnings.CarrierThreatened, "vo_sal_carrieratrisk")]
    [InlineData(ClusterWarnings.BomberInCluster, "vo_sal_bombersighted")]
    [InlineData(ClusterWarnings.EnemyCarrierInCluster, "vo_sal_carrierdetected")]
    [InlineData(ClusterWarnings.CapitalInCluster, "vo_sal_capitalsighted")]
    [InlineData(ClusterWarnings.TeleportInCluster, "vo_sal_teleportsighted")]
    [InlineData(ClusterWarnings.TransportInCluster, "vo_sal_transportsighted")]
    [InlineData(ClusterWarnings.StationThreatened, "vo_sal_stationrisk")]
    [InlineData(ClusterWarnings.StationTeleportThreat, "vo_sal_stationrisk")]
    [InlineData(ClusterWarnings.StationCaptureThreat, "vo_sal_stationrisk")]
    public void AlertSoundStem(short cw, string expected) =>
        Assert.Equal(expected, ClusterWarnings.AlertSoundStem(cw));

    [Fact]
    public void ControlSoundStems() =>
        Assert.Equal(new[] { "vo_sal_sectorsecured", "vo_sal_sectorlost" },
            new[] { ClusterWarnings.SecuredSoundStem, ClusterWarnings.LostSoundStem });
}
