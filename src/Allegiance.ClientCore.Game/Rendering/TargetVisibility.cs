using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P786 (PAR-5) — the client-side per-target <c>CanSee</c> scanner gate the
/// target pane and order-turn arrows were missing. Native
/// <c>IshipIGC::CanSee(pModel)</c> (<c>Igc/shipIGC.h:380-389</c>) is
/// <c>same side || SeenBySide(side) || InScannerRange(pModel)</c>; this port
/// evaluates the same three legs on the client from wire state:
/// <list type="number">
/// <item><b>Same side</b> — always seen (<c>shipIGC.h:383</c>).</item>
/// <item><b>SeenBySide</b> — P786B: the client's ThingSite side visibility
/// (<c>WinTrek/trekigc.cpp:2057-2120</c>, <c>UpdateSideVisibility</c>) =
/// same/allied side, else the last spotter still has it in scanner range,
/// else <b>any scanner of the client's side in the target's cluster</b>
/// (<c>pcluster-&gt;GetClusterSite()-&gt;GetScanners(0)</c>, each
/// <c>s-&gt;InScannerRange(pmodel)</c>). The client's scanner list holds
/// exactly the friendly ships, probes and stations of the client's side —
/// the client site only registers scanners of its own (or allied, allied-viz)
/// side (<c>WinTrek/trekigc.cpp:283-293</c>); the port registers the own side
/// only (allied-viz is not modelled). Each scanner type keeps its native
/// range/LOS rule:
/// <list type="bullet">
/// <item><b>ship</b> — <c>shipIGC.h:341-376</c>: hull scannerRange ·
/// signature / side-GA, the own LOCAL frame with the backward z-dilation
/// (<c>z &lt; 0 → z *= 4</c>, <c>:358-360</c>), and
/// <c>LineOfSightExist</c>; a parented ship (turret,
/// <c>m_pshipParent != NULL</c>, <c>:346</c>) is never a scanner, which the
/// port's friendly-ship gather enforces via
/// <c>ShipEntity.ParentId &lt; 0</c> (<c>CanDrawInSector</c>).</item>
/// <item><b>probe</b> — <c>probeigc.h:163-193</c>: probeType scannerRange ·
/// signature · own-side GA[c_gaScanRange] / target-side GA[c_gaSignature]
/// (the port keeps both global attributes at 1.0 — the P117/P119/P121/P124
/// convention, see <see cref="VisibilityMath.DetectionRadius"/>), a PLAIN
/// radial test (no local-frame dilation), and <c>LineOfSightExist</c>.</item>
/// <item><b>station</b> — <c>stationIGC.h:308-333</c>: stationType
/// scannerRange · signature / side-GA, a plain radial test, and
/// <c>LineOfSightExist</c>.</item>
/// </list></item>
/// <item><b>InScannerRange</b> (<c>shipIGC.h:341-376</c>) — the own ship's
/// own scanner leg, same rule as the ship scanner above (kept separate as
/// native keeps it as the third <c>CanSee</c> leg).</item>
/// </list>
/// The target signature is the target HULL's base signature: the native
/// client replicates enemy ships without parts, so their signature never gets
/// the cloak/afterburner adjustments — the base hull value is exactly what
/// the native <c>InScannerRange</c> reads for them. The scanner orientation
/// is reconstructed from the streamed nose direction (roll does not affect
/// the radial test; the basis is completed deterministically).
/// </summary>
public static class TargetVisibility
{
    /// <summary>The local-frame backward dilation native applies
    /// (<c>shipIGC.h:358-360</c>).</summary>
    public const float BackwardZDilation = 4f;

    /// <summary>
    /// P786B — one friendly scanner of the client's side in a target's
    /// cluster, with the inputs its native <c>InScannerRange</c> reads: the
    /// centre + radius, the type's scanner range (hull for a ship —
    /// <c>shipIGC.h:350</c>, probe type — <c>probeigc.h:165</c>, station
    /// type — <c>stationIGC.h:315</c>), and for a ship scanner the streamed
    /// nose direction the local-frame backward dilation needs
    /// (<c>shipIGC.h:357-360</c>; probe/station scanners run a plain radial
    /// test — <c>probeigc.h:176</c>, <c>stationIGC.h:321</c>).
    /// </summary>
    public readonly record struct SideScanner(
        Vector3 Position,
        float Radius,
        float ScannerRange,
        bool IsShip,
        Vector3 ForwardNose);

    /// <summary>
    /// The full client-side <c>CanSee</c> for a SHIP target, leg for leg as
    /// native evaluates it on the client (see the type doc). <paramref
    /// name="occluders"/> are the target cluster's asteroid occluders
    /// (positions + radii), the only occluders native's
    /// <c>LineOfSightExist</c> considers (<c>common.cpp:2349</c>).
    /// <paramref name="sideScanners"/> are the friendly scanners the client
    /// knows in the target's cluster (the SeenBySide leg, trekigc.cpp:2098-2113);
    /// null/empty leaves only the own-ship scanner leg — the pre-P786B shape.
    /// </summary>
    public static bool CanSeeShip(
        bool sameSide,
        bool targetUnknown, bool targetNotVisible, bool targetHasRealIdentity,
        short ownSector, short targetSector,
        Vector3 ownPosition, Vector3 ownForwardNose, float ownRadius, float ownScannerRange,
        Vector3 targetPosition, float targetRadius, float targetHullSignature,
        float targetSideSignatureAttribute,
        IReadOnlyList<VisibilityMath.SightBody> occluders,
        IReadOnlyList<SideScanner>? sideScanners = null)
    {
        if (sameSide) return true;                                   // shipIGC.h:383
        if (targetUnknown || targetNotVisible || !targetHasRealIdentity) return false;
        if (targetSector < 0) return false;

        // shipIGC.h:388 — the own ship's scanner leg (the third CanSee leg); it
        // needs the own ship in the target's cluster. The SeenBySide leg below
        // does not (Claude review: a target in another sector picked from the
        // command view is seen whenever a friendly scanner THERE sees it).
        if (ownSector >= 0 && targetSector == ownSector && ShipInScannerRange(
                ownPosition, ownRadius, ownScannerRange, ownForwardNose,
                targetPosition, targetRadius, targetHullSignature,
                targetSideSignatureAttribute, occluders))
            return true;

        // trekigc.cpp:2098-2113 — the "do it the hard way" branch of the
        // client's side visibility: any scanner of the client's side in the
        // target's cluster that has the model in ITS scanner range marks the
        // side as seeing it (SeenBySide, modelIGC.h:99-110). Each scanner
        // type applies its own native range/LOS rule (see the type doc).
        if (sideScanners is not null)
        {
            for (int i = 0; i < sideScanners.Count; i++)
            {
                var s = sideScanners[i];
                bool sees = s.IsShip
                    ? ShipInScannerRange(
                        s.Position, s.Radius, s.ScannerRange, s.ForwardNose,
                        targetPosition, targetRadius, targetHullSignature,
                        targetSideSignatureAttribute, occluders)
                    : PlainScannerInRange(
                        s.Position, s.Radius, s.ScannerRange,
                        targetPosition, targetRadius, targetHullSignature,
                        targetSideSignatureAttribute, occluders);
                if (sees) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// P804 — one scanner's native <c>InScannerRange(pModel)</c> test for an
    /// arbitrary target (used by the lead-reticle relay gate,
    /// <c>indicator.cpp:280-315</c>: <c>ps-&gt;InScannerRange(targetModel)</c>).
    /// A ship scanner applies <c>shipIGC.h:341-376</c> (local frame + backward
    /// dilation); a station (or a probe) applies the plain radial
    /// <c>stationIGC.h:308-333</c> / <c>probeigc.h:163-193</c> rule. Both include
    /// native's <c>LineOfSightExist</c> over the cluster's asteroids.
    /// </summary>
    public static bool InScannerRange(
        in SideScanner scanner,
        Vector3 targetPosition, float targetRadius, float targetSignature,
        float targetSideSignatureAttribute,
        IReadOnlyList<VisibilityMath.SightBody> occluders)
        => scanner.IsShip
            ? ShipInScannerRange(
                scanner.Position, scanner.Radius, scanner.ScannerRange, scanner.ForwardNose,
                targetPosition, targetRadius, targetSignature,
                targetSideSignatureAttribute, occluders)
            : PlainScannerInRange(
                scanner.Position, scanner.Radius, scanner.ScannerRange,
                targetPosition, targetRadius, targetSignature,
                targetSideSignatureAttribute, occluders);

    /// <summary>The native SHIP scanner rule (<c>shipIGC.h:341-376</c>):
    /// <c>r = scannerRadius + targetRadius + scannerRange · signature /
    /// targetSideGA</c>, the delta tested in the scanner's LOCAL frame with
    /// the backward z-dilation (<c>deltaP.z &lt; 0 → z *= 4</c>,
    /// <c>:358-360</c>), and <c>LineOfSightExist</c> over the cluster's
    /// asteroids (<c>:365</c>).</summary>
    private static bool ShipInScannerRange(
        Vector3 scannerPosition, float scannerRadius, float scannerRange, Vector3 scannerNose,
        Vector3 targetPosition, float targetRadius, float targetSignature,
        float targetSideSignatureAttribute,
        IReadOnlyList<VisibilityMath.SightBody> occluders)
    {
        // shipIGC.h:355-357 — margin m = scannerRange · signature / sideGA.
        float m = scannerRange * targetSignature;
        if (targetSideSignatureAttribute > 0f) m /= targetSideSignatureAttribute;
        float r = scannerRadius + targetRadius + m;

        // shipIGC.h:358-361 — the delta in the scanner's local frame, with the
        // backward half-space dilated 4×. Native's local frame is right/up/
        // BACKWARD (forward = −Z, orientation.h:83), so the local z of
        // (scanner − target) is −(delta · nose): a target ahead reads positive
        // and a target behind reads negative and gets dilated. The streamed
        // nose direction defines the axis; right/up are completed
        // deterministically (roll is irrelevant to a radial test — the
        // components square).
        Vector3 f = scannerNose.LengthSquared() > 1e-6f
            ? scannerNose.Normalize() : new Vector3(0f, 0f, -1f);
        Vector3 seed = MathF.Abs(f.Z) < 0.9f ? new Vector3(0f, 0f, -1f) : new Vector3(1f, 0f, 0f);
        Vector3 x = Vector3.CrossProduct(seed, f).Normalize();
        Vector3 y = Vector3.CrossProduct(f, x);
        Vector3 delta = scannerPosition - targetPosition;            // shipIGC.h:358
        float lx = delta * x, ly = delta * y, lz = -(delta * f);
        if (lz < 0f) lz *= BackwardZDilation;
        if (lx * lx + ly * ly + lz * lz > r * r) return false;       // shipIGC.h:361

        // shipIGC.h:362 — native line of sight over the cluster's asteroids.
        return VisibilityMath.LineOfSightExist(
            scannerPosition, new VisibilityMath.SightBody(targetPosition, targetRadius), occluders);
    }

    /// <summary>The native PROBE/STATION scanner rule (<c>probeigc.h:163-193</c>,
    /// <c>stationIGC.h:308-333</c>): the same margin
    /// <c>scannerRange · signature / targetSideGA</c> (the probe additionally
    /// multiplies by its own side's GA[c_gaScanRange], <c>probeigc.h:165</c> —
    /// a global attribute the port keeps at 1.0, the P117/P119/P121/P124
    /// convention), but a PLAIN radial test — no local frame, no backward
    /// dilation (<c>probeigc.h:176</c>, <c>stationIGC.h:321</c>) — then
    /// <c>LineOfSightExist</c> (<c>probeigc.h:177</c>, <c>stationIGC.h:322</c>).</summary>
    private static bool PlainScannerInRange(
        Vector3 scannerPosition, float scannerRadius, float scannerRange,
        Vector3 targetPosition, float targetRadius, float targetSignature,
        float targetSideSignatureAttribute,
        IReadOnlyList<VisibilityMath.SightBody> occluders)
    {
        float m = scannerRange * targetSignature;
        if (targetSideSignatureAttribute > 0f) m /= targetSideSignatureAttribute;
        float r = scannerRadius + targetRadius + m;
        if ((scannerPosition - targetPosition).LengthSquared() > r * r) return false;
        return VisibilityMath.LineOfSightExist(
            scannerPosition, new VisibilityMath.SightBody(targetPosition, targetRadius), occluders);
    }

    /// <summary>
    /// P786B — the friendly scanners of <paramref name="mySide"/> in <paramref
    /// name="cluster"/> the client knows, the port's
    /// <c>pcluster-&gt;GetClusterSite()-&gt;GetScanners(0)</c>
    /// (<c>trekigc.cpp:2099</c>; the client site only ever holds own-side — or
    /// allied-viz — scanners, <c>trekigc.cpp:283-293</c>): friendly ships with
    /// a fresh pose (<see cref="ShipEntity.CanDrawInSector"/>, which also
    /// enforces the native turret exclusion <c>m_pshipParent == NULL</c>,
    /// <c>shipIGC.h:346</c>), friendly probes (<c>clusterIGC.h:219</c>) and
    /// friendly stations (<c>clusterIGC.h:126</c>). Each contributes its own
    /// type's scanner range and radius (hull length/2 for ships — the port's
    /// ship-sphere convention; <c>DataProbeTypeIGC.Radius</c>/
    /// <c>DataStationTypeIGC.Radius</c> for the statics); a type whose catalogue
    /// entry is missing cannot contribute (its range is unknowable). Appends
    /// into <paramref name="into"/> (cleared first) so the per-frame path stays
    /// allocation-free.
    /// </summary>
    public static List<SideScanner> FriendlyScanners(
        ClientWorld world, short mySide, short cluster,
        IReadOnlyDictionary<short, DataHullTypeIGC>? hullTypes,
        IReadOnlyDictionary<short, DataProbeTypeIGC>? probeTypes,
        IReadOnlyDictionary<short, DataStationTypeIGC>? stationTypes,
        List<SideScanner> into)
    {
        into.Clear();
        // Friendly ships — native registers every ship as a scanner for its
        // side on entering a cluster (clusterIGC.h:148, shipIGC.cpp:3700) and
        // only unparented ships evaluate the range test (shipIGC.h:346).
        foreach (var ship in world.Ships.Values)
        {
            if (ship.SideId != mySide || !ship.CanDrawInSector(cluster)) continue;
            if (hullTypes is null || !hullTypes.TryGetValue(ship.HullId, out var hull)) continue;
            if (ship.Latest is not { } up) continue;
            into.Add(new SideScanner(
                up.Position, 0.5f * hull.Length, hull.ScannerRange,
                IsShip: true, up.Orientation.GetForward()));
        }
        // Friendly probes (clusterIGC.h:219).
        foreach (var probe in world.Probes.Values)
        {
            if (probe.SideId != mySide || probe.ClusterId != cluster) continue;
            if (probeTypes is null || !probeTypes.TryGetValue(probe.ProbetypeId, out var pt)) continue;
            into.Add(new SideScanner(probe.Position, pt.Radius, pt.ScannerRange, IsShip: false, default));
        }
        // Friendly stations (clusterIGC.h:126, stationIGC.h:275).
        foreach (var station in world.Stations.Values)
        {
            if (station.SideId != mySide || station.ClusterId != cluster) continue;
            if (stationTypes is null || !stationTypes.TryGetValue(station.StationTypeId, out var stt)) continue;
            into.Add(new SideScanner(
                new Vector3(station.Position[0], station.Position[1], station.Position[2]),
                stt.Radius, stt.ScannerRange, IsShip: false, default));
        }
        return into;
    }

    /// <summary>Convenience overload taking the two entities plus the resolved
    /// hull data (own scanner hull, target hull) and the target side's
    /// <c>c_gaSignature</c> global attribute. Returns false when the own ship
    /// or either hull is unknown (the caller then falls back to the pane's
    /// existing conservative behaviour).</summary>
    public static bool CanSeeShip(
        ShipEntity own, ShipEntity target,
        DataHullTypeIGC ownHull, float ownRadius,
        DataHullTypeIGC targetHull, float targetRadius,
        short mySide, float targetSideSignatureAttribute,
        IReadOnlyList<VisibilityMath.SightBody> occluders,
        IReadOnlyList<SideScanner>? sideScanners = null)
    {
        if (own.Latest is not { } ownUp) return false;
        if (target.Latest is null) return false;
        return CanSeeShip(
            sameSide: target.SideId == mySide,
            targetUnknown: target.Unknown,
            targetNotVisible: target.IsNotVisible,
            targetHasRealIdentity: target.HasRealIdentity,
            ownSector: own.SectorId,
            targetSector: target.SectorId,
            ownPosition: ownUp.Position,
            ownForwardNose: ownUp.Orientation.GetForward(),
            ownRadius: ownRadius,
            ownScannerRange: ownHull.ScannerRange,
            targetPosition: target.Latest?.Position ?? default,
            targetRadius: targetRadius,
            targetHullSignature: targetHull.Signature,
            targetSideSignatureAttribute: targetSideSignatureAttribute,
            occluders: occluders,
            sideScanners: sideScanners);
    }
}
