using System.Collections.Generic;
using Allegiance.Core;
using Allegiance.Core.Sim;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P570 — the pure, headless rules for gathering a sector's stations into the
/// per-frame colour-pass draw list. Kept apart from <see cref="Game1"/> so the
/// "each station is drawn exactly once" invariant is unit-testable (the live
/// report: with soft particles ON a second, mis-tinted copy of the home station
/// could appear). The gather filters to the local player's own sector
/// (<c>st.ClusterId == mySector</c>, the same discipline asteroids/warps use)
/// and builds the full <see cref="Orientation"/> from the station's Forward+Up.
/// </summary>
public static class StationSceneGather
{
    /// <summary>
    /// Collect the colour-pass entries for the local sector. <paramref name="stations"/>
    /// is the world's station table keyed by id; <paramref name="radiusOf"/> resolves
    /// the real <c>DataStationTypeIGC.Radius</c> for a station (the caller's
    /// catalogue lookup). Deduplicates by <see cref="DataStationIGC.StationId"/> so
    /// the same station can never be added twice in one frame (a stale/duplicate
    /// entry is what rendered the second mis-tinted station), guaranteeing each
    /// station is drawn exactly once.
    /// </summary>
    public static List<(SimVec Pos, Orientation Ori, short SideId, float Radius)> ColourPass(
        IEnumerable<KeyValuePair<short, DataStationIGC>> stations, short mySector, Func<DataStationIGC, float> radiusOf)
    {
        var seen = new HashSet<short>();
        var result = new List<(SimVec, Orientation, short, float)>();
        foreach (var kv in stations)
        {
            var st = kv.Value;
            if (st.ClusterId != mySector) continue;
            // P570 — draw each station exactly once, even if the source ever yields
            // the same station twice (the double-station failure mode).
            if (!seen.Add(st.StationId)) continue;
            var o = new Orientation();
            o.Set(Vec(st.Forward), Vec(st.Up));
            result.Add((Vec(st.Position), o, st.SideId, radiusOf(st)));
        }
        return result;
    }

    private static SimVec Vec(float[] v) => new(v[0], v[1], v[2]);
}
