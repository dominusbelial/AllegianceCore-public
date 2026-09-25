namespace Allegiance.Core;

/// <summary>
/// P374 — port of native's cluster-warning classifier (<c>Igc/igc.h:4350-4387</c>,
/// <c>Igc/common.cpp:2490-2551</c>). Native derives a per-sector <c>AssetMask</c>
/// (which kinds of friendly/enemy assets are present) from a side's knowledge of
/// that sector, then collapses the mask into a single <c>ClusterWarning</c>
/// severity/type value that drives the client's threat sounds, starmap flashing
/// icons and status text. This port reproduces both steps exactly, server-side,
/// because the port's renderer-only MonoGame client has no static core (no hull
/// capability bits) and no knowledge of enemy ships in distant sectors — so the
/// trigger must be computed here and broadcast (see <c>ClusterWarningUpdate</c>
/// and <c>Server.BroadcastSectorThreats</c>).
///
/// <c>bInvulnerableStations</c> (<c>GetClusterWarning</c>): this port decodes no
/// <c>misparms.bInvulnerableStations</c>, and native's shipped default is
/// <c>false</c>, so the port passes <c>false</c> — stations are vulnerable, and
/// the station-capture/teleport/bomber threat branches are active.
/// </summary>
public static class ClusterWarnings
{
    // ── AssetMask bits (igc.h:4350-4364) ──────────────────────────────────
    public const short AmFighter = 0x01;
    public const short AmMiner = 0x02;
    public const short AmBuilder = 0x04;
    public const short AmStation = 0x08;
    public const short AmEnemyFighter = 0x10;
    public const short AmEnemyMiner = 0x20;
    public const short AmEnemyBuilder = 0x40;
    public const short AmEnemyAPC = 0x80;
    public const short AmEnemyStation = 0x100;
    public const short AmEnemyCapital = 0x200;
    public const short AmEnemyCarrier = 0x400;
    public const short AmCarrier = 0x800;
    public const short AmEnemyTeleport = 0x1000;
    public const short AmEnemyTeleportShip = 0x2000;
    public const short AmEnemyBomber = 0x4000;

    // ── ClusterWarning levels (igc.h:4367-4384) ───────────────────────────
    public const short NoThreat = 0;
    public const short EnemyFighterInCluster = 1;
    public const short EnemyMinerInCluster = 2;
    public const short EnemyBuilderInCluster = 3;
    public const short CombatInCluster = 4;
    public const short MinerThreatened = 5;
    public const short BuilderThreatened = 6;
    public const short CarrierThreatened = 7;
    public const short BomberInCluster = 8;
    public const short EnemyCarrierInCluster = 9;
    public const short CapitalInCluster = 10;
    public const short TeleportInCluster = 11;
    public const short TransportInCluster = 12;
    public const short StationThreatened = 13;
    public const short StationTeleportThreat = 14;
    public const short StationCaptureThreat = 15;
    public const short Max = 16;

    /// <summary>
    /// Port of native <c>GetAssetMask</c> (<c>igc.h:5361-5405</c>): classify one
    /// ship into its friendly/enemy asset bits. Native switches on the ship's
    /// <b>pilot type</b> (builder/layer/miner/carrier) and falls back to hull
    /// capability bits — this port's <see cref="Sim.Ship.PilotType"/> and
    /// <see cref="Data.DataHullTypeIGC.HasCapability"/> give exactly those inputs.
    /// </summary>
    public static short GetAssetMask(Sim.Ship ship, bool friendly)
    {
        var hull = ship.HullType;
        short habm = hull.HabmCapabilities;

        switch (ship.PilotType)
        {
            case Sim.Ship.PilotTypeLayer:   // NYI differentiation — treated as builder (native igc.h:5364)
            case Sim.Ship.PilotTypeBuilder:
                return friendly ? AmBuilder : AmEnemyBuilder;
            case Sim.Ship.PilotTypeMiner:
                return friendly ? AmMiner : AmEnemyMiner;
            case Sim.Ship.PilotTypeCarrier:
                return friendly ? AmCarrier : AmEnemyCarrier;
            default:
                if (friendly)
                    return hull.HasCapability(DataHullTypeIGC.HabmLifepod) ? (short)0 : AmFighter;
                short am;
                if ((habm & (DataHullTypeIGC.HabmBoard | DataHullTypeIGC.HabmCaptureThreat)) != 0)
                    am = (habm & DataHullTypeIGC.HabmFighter) != 0
                        ? (short)(AmEnemyFighter | AmEnemyAPC)
                        : (short)(AmEnemyCapital | AmEnemyAPC);
                else if ((habm & DataHullTypeIGC.HabmThreatToStation) != 0)
                    am = (habm & DataHullTypeIGC.HabmFighter) != 0
                        ? (short)(AmEnemyFighter | AmEnemyBomber)
                        : (short)(AmEnemyCapital | AmEnemyBomber);
                else
                    am = hull.HasCapability(DataHullTypeIGC.HabmLifepod) ? (short)0 : AmEnemyFighter;
                if ((habm & DataHullTypeIGC.HabmIsRipcordTarget) != 0)
                    am |= AmEnemyTeleportShip;
                return am;
        }
    }

    /// <summary>
    /// Port of native <c>GetClusterWarning</c> (<c>common.cpp:2490-2526</c>) —
    /// collapse an asset mask into the single highest-priority warning level.
    /// Priority is checked top-down exactly as native; the first matching branch
    /// wins, so a sector with a station at capture-threat reports that even when
    /// it also holds an enemy fighter.
    /// </summary>
    public static short GetWarning(short am, bool bInvulnerableStations)
    {
        if ((am & AmEnemyAPC) != 0 && (am & AmStation) != 0 && !bInvulnerableStations)
            return StationCaptureThreat;
        if ((am & (AmEnemyTeleport | AmEnemyTeleportShip)) != 0 && (am & AmStation) != 0 && !bInvulnerableStations)
            return StationTeleportThreat;
        if ((am & AmEnemyBomber) != 0 && (am & AmStation) != 0 && !bInvulnerableStations)
            return StationThreatened;
        if ((am & AmEnemyAPC) != 0 && !bInvulnerableStations)
            return TransportInCluster;
        if ((am & AmEnemyTeleportShip) != 0)
            return TeleportInCluster;
        if ((am & AmEnemyCapital) != 0)
            return CapitalInCluster;
        if ((am & AmEnemyCarrier) != 0)
            return EnemyCarrierInCluster;
        if ((am & AmEnemyBomber) != 0 && !bInvulnerableStations)
            return BomberInCluster;
        if ((am & AmEnemyFighter) != 0 && (am & AmCarrier) != 0)
            return CarrierThreatened;
        if ((am & AmEnemyFighter) != 0 && (am & AmBuilder) != 0)
            return BuilderThreatened;
        if ((am & AmEnemyFighter) != 0 && (am & AmMiner) != 0)
            return MinerThreatened;
        if ((am & AmEnemyFighter) != 0 && (am & AmFighter) != 0)
            return CombatInCluster;
        if ((am & AmEnemyBuilder) != 0)
            return EnemyBuilderInCluster;
        if ((am & AmEnemyMiner) != 0)
            return EnemyMinerInCluster;
        if ((am & AmEnemyFighter) != 0)
            return EnemyFighterInCluster;
        return NoThreat;
    }

    /// <summary>
    /// Port of native <c>GetClusterWarningText</c> (<c>common.cpp:2530-2551</c>) —
    /// the human description per warning level, used for the client's
    /// <c>"%s in %s"</c> status line and the starmap info-pane hover text.
    /// Returns an empty string for an out-of-range level (defensive; native asserts).
    /// </summary>
    public static string GetWarningText(int cw)
    {
        if (cw < 0 || cw >= Max) return "";
        return cw switch
        {
            NoThreat => "No threat",
            EnemyFighterInCluster => "Enemy fighter spotted",
            EnemyMinerInCluster => "Enemy miner spotted",
            EnemyBuilderInCluster => "Enemy builder spotted",
            CombatInCluster => "Conflict",
            MinerThreatened => "Miner at risk",
            BuilderThreatened => "Builder at risk",
            CarrierThreatened => "Carrier at risk",
            BomberInCluster => "Enemy bomber spotted",
            EnemyCarrierInCluster => "Enemy carrier spotted",
            CapitalInCluster => "Enemy capital ship spotted",
            TeleportInCluster => "Enemy assault ship spotted",
            TransportInCluster => "Enemy transport spotted",
            StationThreatened => "Station at risk",
            StationTeleportThreat => "Station at risk by teleport",
            StationCaptureThreat => "Station at risk of capture",
            _ => "",
        };
    }

    /// <summary>
    /// Port of native <c>c_vAlertSounds</c> (<c>trekigc.cpp:344-352</c>): map a
    /// warning level to its sound-definition slot name. Levels 5-12 map to eight
    /// distinct detection/threatened sounds; 13-15 all use the station-at-risk
    /// sound; 0-4 have no alert sound (<c>NA</c> → empty). The returned stems are
    /// the <c>sounddef.mdl</c> wave names the MonoGame client resolves.
    /// </summary>
    public static string AlertSoundStem(int cw) => cw switch
    {
        MinerThreatened => "vo_detect_miner",
        BuilderThreatened => "vo_detect_builder",
        CarrierThreatened => "vo_sal_carrieratrisk",
        BomberInCluster => "vo_sal_bombersighted",
        EnemyCarrierInCluster => "vo_sal_carrierdetected",
        CapitalInCluster => "vo_sal_capitalsighted",
        TeleportInCluster => "vo_sal_teleportsighted",
        TransportInCluster => "vo_sal_transportsighted",
        StationThreatened or StationTeleportThreat or StationCaptureThreat => "vo_sal_stationrisk",
        _ => "",
    };

    /// <summary>The <c>sounddef.mdl</c> stem for the sector-secured cue
    /// (<c>sectorSecuredSound</c>).</summary>
    public const string SecuredSoundStem = "vo_sal_sectorsecured";
    /// <summary>The <c>sounddef.mdl</c> stem for the sector-lost cue
    /// (<c>sectorLostSound</c>).</summary>
    public const string LostSoundStem = "vo_sal_sectorlost";
}
