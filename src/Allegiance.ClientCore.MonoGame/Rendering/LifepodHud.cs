namespace Allegiance.ClientCore.MonoGame.Rendering;

using System.Linq;

/// <summary>
/// P377 — pure helpers for the lifepod oxygen countdown display. The MonoGame
/// client decides, from the loaded core + the own ship's SHIP_STATUS, whether
/// the local player's own ship is an actively-drifting lifepod whose oxygen is
/// genuinely burning, and converts the wire-carried remaining FRACTION (the
/// own-ship update's FUEL slot) into a wall-clock mm:ss countdown. Kept pure
/// (no MonoGame types) so the decision is unit-testable.
/// </summary>
public static class LifepodHud
{
    /// <summary>
    /// True while the local player's OWN ship is a Lifepod hull still adrift in
    /// space — i.e. the pod whose oxygen is burning. Mirrors the server's
    /// self-only, in-space gate (native <c>IshipIGC::GetEndurance</c>'s
    /// lifepod-hull + cluster conjuncts; <c>StationId &lt; 0</c> == NA / flying
    /// free). A docked (rescued / death-respawned) pod reads false, and so does
    /// a remote lifepod (this path is reached only for the local player's own
    /// ShipEntity in Game1.DrawHud).
    /// </summary>
    public static bool IsAdriftLifepod(ShopCatalog? catalog, short hullId, short stationId)
    {
        if (catalog is not { } sc) return false;
        var hull = sc.Hulls.FirstOrDefault(h => h.HullId == hullId);
        if (hull.HullId == 0) return false;   // no matching hull (HullId 0 = not-found / not-yet-known)
        return hull.HasCapability(Allegiance.Core.DataHullTypeIGC.HabmLifepod) && stationId < 0;
    }

    /// <summary>
    /// Whole seconds of oxygen left for the mm:ss countdown — ceil of the
    /// wire-carried remaining <paramref name="fuelFraction"/> (which is the
    /// server-computed oxygen fraction while adrift) × the real endurance
    /// constant the server arms pods with (the client's loaded core
    /// <c>FloatConstants[31]</c>, 300 s fallback).
    /// </summary>
    public static int OxygenSeconds(float fuelFraction, ShopCatalog? catalog)
        => (int)Math.Ceiling(fuelFraction * (catalog?.LifepodEnduranceSeconds ?? 300f));
}
