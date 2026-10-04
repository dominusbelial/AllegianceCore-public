using System.Numerics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P541 — pure, headless-testable mouse hit-testing for the starmap: map a
/// screen-space point to the cluster under the cursor, using the SAME layout
/// maths the renderer uses (<see cref="StarmapLayout.Fit"/> — the port of
/// native's <c>GetClusterPoint</c>, sectormap.cpp:1310-1316) and native's own
/// click radius (<c>nMinDistance = 15</c>, sectormap.cpp:1324-1343). The
/// nearest cluster within the radius wins; a point outside every cluster's
/// radius maps to none (native's <c>GetClusterAtPoint</c> returns NULL there).
/// Pure <c>System.Numerics</c>, no MonoGame types on its surface, so it is
/// unit-testable without a graphics device (the established pure-logic
/// discipline of this client, e.g. P249's <see cref="StarmapLayout"/>).
/// </summary>
public static class StarmapHitTest
{
    /// <summary>
    /// Native's "close enough to click" radius in viewport px — the initial
    /// <c>nMinDistance = 15</c> in <c>GetClusterAtPoint</c>
    /// (sectormap.cpp:1324). A click within 15 px of a cluster's dot selects it;
    /// further away, or outside every dot, selects nothing.
    /// </summary>
    public const float ClickRadius = 15f;

    /// <summary>
    /// Map a screen point (viewport px, top-left origin — what a mouse/tap
    /// reports) to the cluster under it, or <c>-1</c> when the point is not
    /// within <see cref="ClickRadius"/> of any laid-out cluster's screen point.
    /// Nearest cluster within the radius wins (ties broken by layout insertion
    /// order — native's <c>&lt;</c> comparison keeps the first-found, and the
    /// distance is strictly-less so equal-distance ties keep the earlier site).
    /// The layout's cluster centres ARE the same screen points the renderer
    /// draws (both come from <see cref="StarmapLayout.Fit"/>), so what the pilot
    /// sees and what a click selects always agree.
    /// </summary>
    public static short HitTest(StarmapLayout layout, Vector2 point)
    {
        short best = -1;
        float bestDistSq = ClickRadius * ClickRadius;
        foreach (short id in layout.Ids)
        {
            if (!layout.TryGetPoint(id, out var p))
                continue;
            float dx = p.X - point.X;
            float dy = p.Y - point.Y;
            float dSq = dx * dx + dy * dy;
            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                best = id;
            }
        }
        return best;
    }
}
