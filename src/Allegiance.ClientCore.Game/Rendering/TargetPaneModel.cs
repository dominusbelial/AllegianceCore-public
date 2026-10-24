using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-3 — pure resolution of the native bottom target pane. Values follow
/// <c>WinTrek/consoledata.cpp:40-199,202-277</c> (name/type/side/range/speed/
/// hull/shield), <c>WinTrek/WinTrek.cpp:8784-8818</c> (side colour and the
/// neutral grey), and <c>WinTrek/indicator.cpp:405</c> (the lead predictor's
/// in-range flag). Read under <c>ConnectionSession.Gate</c>; no MonoGame or
/// graphics types appear here, mirroring <see cref="SelectionPaneModel"/>.
/// </summary>
public static class TargetPaneModel
{
    // WinTrek/WinTrek.cpp:90 — the brightness multiplier native applies to a
    // sided targetHudColor.
    public const float HudBright = 0.85f;
    // WinTrek/WinTrek.cpp:8784-8818 — SetTargetGeo forces this alpha on both
    // the neutral grey and the sided colour.
    public const float TargetHudAlpha = 0.5f;
    /// <summary>Native's no-side colour: grey (0.5,0.5,0.5) at alpha 0.5
    /// (WinTrek/WinTrek.cpp:8785-8786), not white.</summary>
    public static readonly HudColor Neutral = new(0.5f, 0.5f, 0.5f, TargetHudAlpha);

    public static HudTargetPane Build(
        ClientWorld world,
        IgcObjectType kind,
        short id,
        string? fallbackName = null,
        IReadOnlyDictionary<short, DataHullTypeIGC>? hullTypes = null,
        IReadOnlyDictionary<short, DataStationTypeIGC>? stationTypes = null,
        // P786B — the probe-type catalogue, for the SeenBySide leg's friendly
        // probe scanners (probeigc.h:163-193). Null disables that scanner kind.
        IReadOnlyDictionary<short, DataProbeTypeIGC>? probeTypes = null,
        // HUD-18 — the client-side in-range flag from TargetWeaponRange
        // (indicator.cpp:405). Game1.ComputeTargetPane supplies the local solve;
        // the server lead message is NOT used here any more.
        bool inRange = false,
        // HUD-18 — the own |velocity| the HUD frame presents (HudFrame.Speed),
        // used only by the TTI readout. Null derives it from the own pose.
        float? ownSpeed = null,
        // P786 — the miner shipload capacity (FloatConstants[c_fcidCapacityHe3]
        // × GA[c_gaMiningCapacity], radarimage.cpp:576-577) the Target ore fill
        // divides by (native fill units oreSeen/(2·capacity)). Null leaves the
        // ore readout absent.
        float? minerCapacityHe3 = null)
    {
        string? name = Safe(fallbackName);
        string type = "unknown";
        string? sideName = null;
        HudColor sideColor = Neutral;
        float? hull = null, shield = null, ore = null, speed = null;
        float killBonus = 0f;
        bool isShip = false;

        // Native GetRange (consoledata.cpp:182-199) needs the target in the
        // player's cluster; GetPercentHitPoints (202-212) and station shields
        // (272-276) reuse that same test, plus the docked-station exception.
        bool sameCluster = SameCluster(world, kind, id, out bool dockedStation);
        float? distance = null;
        if (sameCluster
            && world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own)
            && own.Latest is { } ownUp
            && TryTargetPose(world, kind, id, out var targetPos, out _))
            distance = (targetPos - ownUp.Position).Length();

        switch (kind)
        {
            case IgcObjectType.Ship:
                isShip = true;
                if (world.Ships.TryGetValue(id, out var ship))
                {
                    // HUD-20 — native TargetExperience = 100·(GetExperienceMultiplier()−1)
                    // of a ship target (consoledata.cpp:831-843); the ship counter
                    // is seeded from PLAYERINFO's fExperience (appmsg.cpp:2280) and
                    // kept current from KILL_SHIP (:2769,2807-2811).
                    killBonus = TargetExperience(ship.Experience);
                    name = Safe(string.IsNullOrEmpty(ship.CharacterName) ? $"ship {id}" : ship.CharacterName);
                    // P786 — the type description IS the last-seen hull type:
                    // ShipEntity.HullId is the SHIP_STATUS(90) HullID (native
                    // PlayerInfo::LastSeenShipType = m_shipStatus.GetHullID(),
                    // clintlib.h:432) refreshed by EXPORT identities, so an
                    // unseen target resolves its last-seen hull name here
                    // (consoledata.cpp:44-53) and a never-identified one stays
                    // "unknown" — the HullId lookup below falls through.
                    if (hullTypes is { } hulls && hulls.TryGetValue(ship.HullId, out var hullType))
                        type = Safe(hullType.Name) ?? "unknown";
                    // P373 — only a real EXPORT identity has a trustworthy side.
                    if (ship.HasRealIdentity && ship.SideId is { } sideId)
                    {
                        sideName = SideName(world, sideId);
                        sideColor = SideColor(world, sideId);
                    }
                    // P786 — the per-target scanner gate. Native gates the
                    // hull/shield/speed readouts on trekClient.GetShip()->CanSee
                    // (consoledata.cpp:202-212,252-270,136-150), which this
                    // client now evaluates for real (TargetVisibility.CanSeeShip,
                    // shipIGC.h:380-389) instead of approximating with the
                    // cluster/visibility test. Without the hull catalogue the
                    // scanner math cannot resolve, so the pane keeps its
                    // conservative pre-P786 stand-in.
                    bool canSee = CanSeeTargetShip(world, ship, hullTypes, probeTypes, stationTypes)
                                  ?? (!ship.IsNotVisible && !ship.Unknown);
                    if (!ship.IsNotVisible && ship.Latest is { } up)
                    {
                        // Hull is cluster-gated (consoledata.cpp:202-212); ship
                        // shield and speed are only CanSee-gated natively
                        // (consoledata.cpp:252-270,136-150).
                        hull = sameCluster && canSee ? up.HullFraction : 0f;
                        shield = canSee ? up.ShieldFraction : 0f;
                        speed = canSee ? up.Velocity.Length() : -1f;
                    }
                    else
                    {
                        hull = 0f; shield = 0f; speed = -1f;
                    }
                }
                else
                {
                    hull = 0f; shield = 0f; speed = -1f;
                }
                break;

            case IgcObjectType.Station:
                if (world.Stations.TryGetValue(id, out var st))
                {
                    name = Safe(st.Name);
                    if (stationTypes is { } sts && sts.TryGetValue(st.StationTypeId, out var stt))
                        type = Safe(stt.Name) ?? "unknown";
                    if (st.SideId is { } stSide)
                    {
                        sideName = SideName(world, stSide);
                        sideColor = SideColor(world, stSide);
                    }
                    bool live = world.StationLive.TryGetValue(id, out var state);
                    bool gated = live && (sameCluster || dockedStation);
                    hull = gated ? state.HullFraction : 0f;
                    shield = gated ? state.ShieldFraction : 0f;
                }
                else
                {
                    hull = 0f; shield = 0f;
                }
                speed = 0f;   // static
                break;

            case IgcObjectType.Asteroid:
                if (world.Asteroids.TryGetValue(id, out var ast))
                {
                    name = Safe(ast.Name);
                    type = Safe(NativeAsteroidTypes.GetTypeDefaults(
                        NativeAsteroidTypes.ResolveTypeIndex(ast.AabmCapabilities)).Name) ?? "asteroid";
                    if (world.AsteroidLive.TryGetValue(id, out var live))
                    {
                        hull = sameCluster ? live.Fraction : 0f;   // consoledata.cpp:202-212
                        // P786 — Target ore in the native fill units: the
                        // side-known ore (the P797 wire only ever delivers ore
                        // the side has seen) over 2 × miner shipload capacity
                        // (radarimage.cpp:1051-1069) — the same units the radar
                        // asteroid fill uses, not the raw remaining fraction.
                        ore = minerCapacityHe3 is { } capacity
                            ? HudWireValues.OreSeenFill(live.OreFraction, ast.OreMax, capacity)
                            : null;
                    }
                    else
                    {
                        hull = 0f; ore = null;
                    }
                }
                else
                {
                    type = "asteroid";
                    hull = 0f;
                }
                speed = 0f;   // static
                break;

            case IgcObjectType.Warp:
                if (world.Warps.TryGetValue(id, out var wp))
                    name = Safe(wp.Name);
                type = "aleph";
                speed = 0f;   // static
                break;

            default:
                speed = 0f;
                break;
        }

        // HUD-18 — LOCATION: fallback. Native's TargetRangeString switches to
        // "LOCATION:" + GetSectorName(Target) exactly when GetRange returned -1
        // (dialog.mdl:4053-4060), so the sector name is only resolved then.
        string? sectorName = distance is null ? ResolveSectorName(world, kind, id) : null;

        // HUD-18 — GHLargeTargetTTI (dialog.mdl:1280-1355). Native hides the
        // whole group while docked: SwitchImage(GetPercentHitPoints(MyStation),
        // emptyImage, [(0, ...)]) at :1327-1330. A valid MyStation is the
        // player's docked station, the port's ShipEntity.StationId >= 0.
        string? timeToIntercept = null;
        if (world.MyShipId is { } ownId && world.Ships.TryGetValue(ownId, out var ownEntity)
            && ownEntity.StationId < 0)
        {
            // Native feeds GetRange straight in, including the -1 no-range
            // sentinel, and GetSpeed(Me)/GetSpeed(Target) as scalar lengths
            // (consoledata.cpp:136-199). The pane Speed already carries the
            // native 0/-1 sentinels for static/unseen targets.
            timeToIntercept = TargetTti.Text(
                distance ?? -1f,
                ownSpeed ?? ownEntity.Latest?.Velocity.Length() ?? 0f,
                speed ?? 0f);
        }

        return new HudTargetPane(
            Present: true,
            Name: name,
            TypeDescription: type,
            SideName: sideName,
            SideColor: sideColor,
            HullFraction: hull,
            ShieldFraction: shield,
            Distance: distance,
            Speed: speed,
            OreFraction: ore,
            KillBonusPercent: isShip ? killBonus : null,   // native switch hides non-ships (dialog.mdl:4208-4214)
            // HUD-18 — the client-side in-range flag (indicator.cpp:405,407),
            // supplied by Game1.ComputeTargetPane; never the lead message.
            InRange: inRange,
            IsShip: isShip,
            SectorName: sectorName,
            TimeToIntercept: timeToIntercept);
    }

    /// <summary>
    /// HUD-18 — <c>ModelData::GetSectorName</c> (<c>WinTrek/consoledata.cpp:70-87</c>):
    /// the target's cluster name when it can be seen, else a ship's last-known
    /// status sector, else "unknown". The port's <c>CanSee</c> stand-in is the
    /// same-cluster/visibility test <see cref="SameCluster"/>; a static model in
    /// another cluster is resolved from the cluster it was exported in
    /// (<c>DataStationIGC.ClusterId</c>/<c>DataAsteroidIGC.ClusterId</c>/
    /// <c>DataWarpIGC.ClusterId</c>) — native's <c>CanSee</c> is true for a
    /// friendly/known station, and this keeps the sector name available where
    /// native's null-cluster branch would only say "unknown".
    /// </summary>
    private static string? ResolveSectorName(ClientWorld world, IgcObjectType kind, short id)
    {
        short? clusterId = kind switch
        {
            // A ship's cluster is only trustworthy once a SHIP_STATUS or
            // SET_CLUSTER established a real sector (StatusSectorKnown): a new
            // ShipEntity defaults SectorId to 0, which may be a real cluster id.
            IgcObjectType.Ship => world.Ships.TryGetValue(id, out var ship)
                && ship.StatusSectorKnown && ship.SectorId >= 0 ? ship.SectorId : null,
            IgcObjectType.Station => world.Stations.TryGetValue(id, out var st) ? st.ClusterId : null,
            IgcObjectType.Asteroid => world.Asteroids.TryGetValue(id, out var ast) ? ast.ClusterId : null,
            IgcObjectType.Warp => world.Warps.TryGetValue(id, out var warp) ? warp.ClusterId : null,
            _ => null,
        };
        if (clusterId is { } cid
            && world.Clusters.TryGetValue(cid, out var cluster)
            && Safe(cluster.Name) is { } clusterName)
            return clusterName;
        return "unknown";
    }

    /// Pooled per-frame occluder list for the scanner gate (the frame path is
    /// single-threaded under the session gate; cleared per call, never read
    /// re-entrantly).
    private static readonly List<VisibilityMath.SightBody> OccluderScratch = new();

    /// Pooled per-frame friendly-scanner list for the SeenBySide leg
    /// (trekigc.cpp:2098-2113). <see cref="Build"/> runs once per frame with a
    /// single ship target, so the gather happens once per frame/cluster — not
    /// per pane row — reusing this list, same conventions as OccluderScratch.
    private static readonly List<TargetVisibility.SideScanner> ScannerScratch = new();

    /// <summary>
    /// P786 — the client-side <c>trekClient.GetShip()-&gt;CanSee(target)</c>
    /// the target pane's hull/shield/speed readouts are gated on natively
    /// (<c>consoledata.cpp:202-212,252-270,136-150</c>), evaluated for real by
    /// <see cref="TargetVisibility.CanSeeShip"/> (shipIGC.h:380-389) instead
    /// of the cluster/visibility approximation the pane used before. The
    /// occluders are the target's sector's asteroids, the only occluders
    /// native's <c>LineOfSightExist</c> considers (<c>common.cpp:2349</c>).
    /// Radii use the port's established ship-sphere convention (0.5 × hull
    /// length; <c>VisibilityMath</c> doc). Returns NULL when the hull
    /// catalogue is missing/unresolvable — the caller then keeps its
    /// conservative pre-P786 stand-in instead of guessing.
    /// </summary>
    private static bool? CanSeeTargetShip(
        ClientWorld world, ShipEntity target, IReadOnlyDictionary<short, DataHullTypeIGC>? hullTypes,
        IReadOnlyDictionary<short, DataProbeTypeIGC>? probeTypes,
        IReadOnlyDictionary<short, DataStationTypeIGC>? stationTypes)
    {
        // shipIGC.h:383 — the same side is always seen (no hull data needed).
        if (world.MySideId is { } mySide && target.SideId == mySide) return true;
        if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)
            || own.Latest is null) return false;
        if (hullTypes is null
            || !hullTypes.TryGetValue(own.HullId, out var ownHull)
            || !hullTypes.TryGetValue(target.HullId, out var targetHull)) return null;
        // shipIGC.h:355-357 — the margin divides by the TARGET side's
        // c_gaSignature global attribute.
        float targetSideGa = target.SideId is { } ts and >= 0
            ? world.GlobalAttributesFor(ts).Get(Allegiance.Core.Model.GlobalAttributeSet.Signature)
            : 0f;
        short sector = target.SectorId;
        OccluderScratch.Clear();
        foreach (var ast in world.Asteroids.Values)
        {
            if (ast.ClusterId != sector) continue;
            OccluderScratch.Add(new VisibilityMath.SightBody(
                new Vector3(ast.Position[0], ast.Position[1], ast.Position[2]), ast.Radius));
        }
        // P786B — the SeenBySide leg (trekigc.cpp:2057-2120): any friendly
        // ship/probe/station of the client's side in the TARGET's cluster with
        // the model in its own scanner range marks the side as seeing it.
        TargetVisibility.FriendlyScanners(
            world, world.MySideId ?? -1, sector, hullTypes, probeTypes, stationTypes, ScannerScratch);
        return TargetVisibility.CanSeeShip(
            own, target, ownHull, 0.5f * ownHull.Length,
            targetHull, 0.5f * targetHull.Length,
            world.MySideId ?? -1, targetSideGa, OccluderScratch, ScannerScratch);
    }

    /// <summary>
    /// HUD-18 — the port's same-cluster/visible test, native
    /// <c>targetModel-&gt;GetCluster() == pcluster &amp;&amp; CanSee(targetModel)</c>
    /// (<c>WinTrek/indicator.cpp:188-192</c>, <c>consoledata.cpp:182-199</c>).
    /// A ship must be in the own sector and not hidden/dead; a static model is
    /// in range of the sector test alone. The per-target scanner-range part of
    /// native <c>CanSee</c> (<c>Igc/shipIGC.h:380-389</c>) is not modelled by the
    /// port and is approximated by this cluster/visibility test, the same
    /// stand-in the pane's hull/shield/range rules already use.
    /// </summary>
    public static bool SameCluster(ClientWorld world, IgcObjectType kind, short id) =>
        SameCluster(world, kind, id, out _);

    private static bool SameCluster(ClientWorld world, IgcObjectType kind, short id, out bool dockedStation)
    {
        dockedStation = false;
        if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)) return false;
        dockedStation = kind == IgcObjectType.Station && own.StationId >= 0 && own.StationId == id;
        if (own.SectorId < 0) return false;
        short ownSector = own.SectorId;
        return kind switch
        {
            IgcObjectType.Ship => world.Ships.TryGetValue(id, out var rShip)
                && !rShip.IsNotVisible && rShip.SectorId == ownSector,
            IgcObjectType.Station => world.Stations.TryGetValue(id, out var rSt)
                && rSt.ClusterId == ownSector,
            IgcObjectType.Asteroid => world.Asteroids.TryGetValue(id, out var rAst)
                && rAst.ClusterId == ownSector,
            IgcObjectType.Warp => world.Warps.TryGetValue(id, out var rWarp)
                && rWarp.ClusterId == ownSector,
            _ => false,
        };
    }

    /// <summary>HUD-20 — native <c>ModelData::TargetExperience</c>
    /// (<c>WinTrek/consoledata.cpp:831-843</c>):
    /// <c>100·(GetExperienceMultiplier()−1)</c> = <c>50·e/(e+4)</c>
    /// (<c>Igc/shipIGC.h:1812-1818</c>), where <c>e</c> is the ship's experience
    /// (<c>shipIGC.h:2610</c>). Only an <c>OT_ship</c> has one; the native switch
    /// hides the readout for every other object type (dialog.mdl:4208-4214).
    /// P773 — routes through the shared <see cref="Allegiance.Core.Sim.Ship.KillBonusPercentFor"/>
    /// formula (one helper for the own and target readouts).</summary>
    public static float TargetExperience(float experience) =>
        Allegiance.Core.Sim.Ship.KillBonusPercentFor(experience);

    /// <summary>Printable-ASCII sanitisation (spritefont range 32..126), but
    /// null/empty stays null so renderers keep the native "unknown" fallback.</summary>
    private static string? Safe(string? s) =>
        string.IsNullOrEmpty(s) ? null : ScoreboardLayout.AsciiSafe(s);

    private static string? SideName(ClientWorld world, short sideId)
    {
        var roster = world.SideRoster;
        if (roster is null) return null;
        foreach (var s in roster.Sides)
            if (s.SideId == sideId) return Safe(s.Name);
        return null;
    }

    private static HudColor SideColor(ClientWorld world, short sideId)
    {
        if (world.TryGetSideColor(sideId, out var r, out var g, out var b))
            return new HudColor(r * HudBright, g * HudBright, b * HudBright, TargetHudAlpha);
        return Neutral;
    }

    /// <summary>HUD-18 — the target's live position and velocity for the local
    /// in-range solve: a ship's last <c>ServerShipUpdate</c> pose/velocity, the
    /// exported static position with zero velocity for station/asteroid/warp.</summary>
    public static bool TryTargetPose(ClientWorld world, IgcObjectType kind, short id,
        out SimVec position, out SimVec velocity)
    {
        velocity = default;
        switch (kind)
        {
            case IgcObjectType.Ship:
                if (world.Ships.TryGetValue(id, out var ship) && ship.Latest is { } up)
                { position = up.Position; velocity = up.Velocity; return true; }
                break;
            case IgcObjectType.Station:
                if (world.Stations.TryGetValue(id, out var st))
                { position = new(st.Position[0], st.Position[1], st.Position[2]); return true; }
                break;
            case IgcObjectType.Asteroid:
                if (world.Asteroids.TryGetValue(id, out var ast))
                { position = new(ast.Position[0], ast.Position[1], ast.Position[2]); return true; }
                break;
            case IgcObjectType.Warp:
                if (world.Warps.TryGetValue(id, out var wp))
                { position = new(wp.Position[0], wp.Position[1], wp.Position[2]); return true; }
                break;
        }
        position = default;
        return false;
    }
}
