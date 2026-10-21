using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// HUD-17 — the ship's own-side <c>ShipStatus::GetDetected()</c> bit, computed
/// per the native rule instead of the port's old unconditional <c>true</c>.
/// <para>
/// Native keeps one <c>ShipStatus</c> per (ship, side) in
/// <c>CFSShip::m_rgShipStatus[]</c> (<c>FedSrv/fsship.cpp:237-335</c>). The bit
/// in the ship's <b>own side's</b> copy is set while at least one side that is
/// neither the ship's own side nor an ally has a known (not unknown) status for
/// the ship: <c>ShipStatusSpotted</c> sets it on a non-own, non-allied spot
/// (<c>fsship.cpp:263-265</c>), and <c>ShipStatusHidden</c> recomputes it from
/// every remaining side's <c>!GetUnknown()</c> status (<c>fsship.cpp:306-318</c>),
/// copying the result to every boarded child's own-side copy
/// (<c>fsship.cpp:268-284,321-334</c>).
/// </para>
/// <para>
/// <b>Port mapping.</b> The port's per-side "status known" flag is the P132
/// cross-side visibility sweep's latched <see cref="Model.SeenBySide"/> result
/// (the same mapping P493's <c>SweepCrossSideShipStatuses</c> already uses for
/// the enemy-side spotted/hidden half). A side that can currently see the ship
/// is exactly a side whose status for it is not unknown.
/// </para>
/// <para>
/// <b>Docking.</b> Native <c>CshipIGC::SetStation</c> clears the ship's cluster
/// (<c>Igc/shipIGC.h:421-457</c>, "This also sets the ship's cluster to NULL"),
/// so a docked ship is outside every side's scanner net and ends up not
/// detected. The port keeps a docked ship in its sector's model list, so the
/// explicit <see cref="Ship.StationId"/> gate stands in for that cluster
/// removal.
/// </para>
/// <para>
/// <b>Boarded children.</b> A boarded child's own-side Detected bit follows its
/// carrier's (<c>fsship.cpp:268-284,321-334</c>), hence the recursive parent
/// read.
/// </para>
/// <para>
/// <b>Cost.</b> One index loop over the mission's sides with one dictionary
/// lookup each and no LINQ/lambdas/enumerator boxing, so it is safe on the
/// per-tick server path. The <c>Side</c> object is resolved by the same indexed
/// identity lookup <see cref="Ship.SideGlobalAttributes"/> uses (side ids are
/// only unique inside a mission).
/// </para>
/// </summary>
public static class ShipDetection
{
    /// <summary>Whether at least one non-own, non-allied side currently has a
    /// known status for <paramref name="ship"/> — the value native stores in the
    /// ship's own-side <c>ShipStatus::m_detected</c> bit.</summary>
    public static bool IsDetectedByEnemy(Ship ship)
    {
        // Boarded children follow their carrier's own-side copy
        // (fsship.cpp:268-284,321-334). Walk the parent chain iteratively with a
        // small depth cap rather than recursing: native carrier nesting is
        // shallow, and a malformed/self-referential ParentShip (which the tests'
        // hand-built fixtures can create) must not blow the stack.
        for (int depth = 0; depth < MaxParentDepth; depth++)
        {
            if (ship.SideId is not { } ownSide) return false;   // sideless: no own-side copy to mark
            if (ship.Mission is not { } mission) return false;  // not placed: no side universe
            if (ship.StationId is not null) return false;       // docked == left the cluster (shipIGC.h:421-457)

            if (ship.ParentShip is { } parent)
            {
                if (ReferenceEquals(parent, ship)) return false; // malformed self-parent
                ship = parent;
                continue;
            }

            Sides(mission, ownSide, out var ownGroup, out var sides);

            for (int i = 0; i < sides.Count; i++)
            {
                var side = sides[i];
                if (side.Id == ownSide) continue;                       // own side never counts (fsship.cpp:306)
                if (ownGroup is { } group && side.AllyGroup == group)    // allied ⇔ same non-null group
                    continue;                                            // fsship.cpp:263,306
                if (ship.SeenBySide(side.Id)) return true;               // status not unknown (fsship.cpp:308-313)
            }
            return false;
        }
        return false;   // cyclic/deeper-than-native parent chain: treat as undetected
    }

    /// <summary>Native carrier nesting is a shallow tree (a gunner boards one
    /// carrier, which cannot itself be boarded); this cap only guards malformed
    /// hand-built fixtures against an infinite/cyclic parent chain.</summary>
    private const int MaxParentDepth = 16;

    /// <summary>Resolve the ship's own alliance group and expose the mission's
    /// side list without allocating. Split out so the hot loop above stays a
    /// single indexed pass.</summary>
    private static void Sides(Mission mission, short ownSide, out sbyte? ownGroup,
        out IReadOnlyList<Side> sides)
    {
        sides = mission.Sides;
        ownGroup = null;
        for (int i = 0; i < sides.Count; i++)
            if (sides[i].Id == ownSide)
            {
                ownGroup = sides[i].AllyGroup;
                return;
            }
    }
}
