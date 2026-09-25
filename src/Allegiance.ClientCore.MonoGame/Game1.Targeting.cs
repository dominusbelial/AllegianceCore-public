using System.Net.Http;
using System.Net.Http.Json;
using System.Linq;
using System.Text;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.QuickChat;
using Allegiance.ClientCore.MonoGame.Capture;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using NumericsVec2 = System.Numerics.Vector2;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// The targeting slice of <see cref="Game1"/> (P384 stage-3 split from <c>Game1.cs</c>).
/// Partial class — shares the <c>Game1</c> type, so private field/method access
/// across the split files is unaffected.
/// </summary>
public sealed partial class Game1
{


    /// <summary>
    /// P255 — the own ship's mining-facing render override, delegated to the
    /// pure shared <see cref="MiningOrientation.Facing"/> (aim at the rock +
    /// gentle roll for effect; see its remarks for the native shipIGC.cpp:2251
    /// citation and why this is the render-side mirror of the mining autopilot).
    /// </summary>
    private Orientation MiningFacing(Orientation original, SimVec shipPos, float[] rockPos, float dtSeconds)
        => MiningOrientation.Facing(original, shipPos, rockPos, dtSeconds);

    /// <summary>The kind of object an autopilot target can be.</summary>
    internal enum TargetKind { Ship, Station, Asteroid, Warp }

    /// <summary>A selected autopilot target: enough to re-resolve its live
    /// position each frame from <see cref="ClientWorld"/> (ships may move),
    /// plus a display name for the console / window-title readout.</summary>
    internal readonly record struct AutopilotTarget(TargetKind Kind, short Id, string Name);

    /// <summary>
    /// Soft-lock target candidates (P212, cycling added live 2026-09-14; P256
    /// priority tiers added live 2026-09-18): every known non-self, non-dead ship,
    /// station, asteroid and warp whose projected screen position lands inside the
    /// view frustum, sorted nearest-to-crosshair first within a strict priority
    /// tier. Candidates are projected through the same view/projection matrices
    /// <see cref="Draw"/> builds for the active camera (the chase camera is
    /// sampled with a zero delta so its turn-smoothing state is not disturbed by
    /// the extra call). No true 3D ray-pick — this cone/nearest-to-crosshair
    /// approximation is the intended POC scope.
    ///
    /// Priority tiers, highest first — a tier is offered ONLY when no higher tier
    /// has anything in view, and when it is offered it is the only tier in the pool
    /// (everything below it is suppressed outright, not merely deprioritized):
    ///   1. Ships — with ~134 asteroids per map, one rock is almost always more
    ///      centered on the crosshair than a moving ship, so a plain
    ///      nearest-to-crosshair contest across everything made ship-targeting
    ///      nearly unreachable whenever any asteroid shared the view (confirmed
    ///      live: a real ship was in-frustum and an asteroid still won).
    ///   2. Warps — the alephs between sectors are sparse (~2-4 per sector) yet
    ///      are the whole reason to hit T/G, so they must not be buried behind the
    ///      dense asteroid field. P256's direct fix for "I can't target alephs":
    ///      previously warps were pooled with stations and all ~134 asteroids and
    ///      sorted purely by crosshair distance, so asteroids dominated the order
    ///      and T stepped past many rocks before ever reaching a warp.
    ///   3. Stations — also sparse (~2 per map) and a primary navigation
    ///      destination (dock / refuel / buy), so they earn their own tier for the
    ///      exact same crowding reason as warps.
    ///   4. Asteroids — the most numerous static scenery; offered only when no
    ///      ship, warp or station is in view.
    /// </summary>
    private List<AutopilotTarget> CollectTargetCandidates(SimVec simPos, Orientation simOrient)
    {
        var world = _session.World;
        var xnaPos = PoseConverter.ToXna(simPos);
        var rot = PoseConverter.ToXnaRotation(simOrient);
        float aspect = GraphicsDevice.Viewport.AspectRatio;

        Matrix view, projection;
        Vector3 eye;
        if (_cameraMode == CameraMode.Cockpit)
            (view, projection) = _cockpitCamera.Matrices(xnaPos, rot, aspect, out eye);
        else
            (view, projection) = _chaseCamera.Matrices(xnaPos, rot, aspect, 0f, out eye, _myRenderRadius);
        var viewProj = view * projection;

        var objs = new List<CandidateObject>();
        lock (_session.Gate)
        {
            // P371: every candidate source is narrowed to the player's OWN current
            // sector BEFORE frustum selection (see GatherSectorCandidates). If the
            // own ship is somehow absent we cannot determine "our sector", so we
            // gather nothing rather than dump the map-wide pool.
            if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var ownShip))
                objs = GatherSectorCandidates(world.Ships, world.Warps, world.Stations,
                                              world.Asteroids, me, ownShip.SectorId);
        }

        var ordered = SelectCandidates(objs, p =>
        {
            var rel = PoseConverter.ToXna(p) - eye;
            var clip = Vector4.Transform(new Vector4(rel.X, rel.Y, rel.Z, 1f), viewProj);
            if (clip.W <= 1e-6f) return null;                         // behind the camera
            float sx = clip.X / clip.W;
            float sy = clip.Y / clip.W;
            if (MathF.Abs(sx) > 1f || MathF.Abs(sy) > 1f) return null;// outside the FOV frustum
            return (sx, sy);
        }, out int shipsInFrustum, out int warpsInFrustum, out int stationsInFrustum,
           out int asteroidsInFrustum, out string winningTier);

        int shipsSeen = objs.Count(o => o.Kind == TargetKind.Ship);
        int warpsSeen = objs.Count(o => o.Kind == TargetKind.Warp);
        int stationsSeen = objs.Count(o => o.Kind == TargetKind.Station);
        int asteroidsSeen = objs.Count(o => o.Kind == TargetKind.Asteroid);

        var preview = string.Join(", ", ordered.Take(6).Select(t => t.Name));
        Console.WriteLine($"[monogame] target-pick: ships {shipsInFrustum}/{shipsSeen} in view, " +
                          $"{stationsSeen} station(s), {asteroidsSeen} asteroid(s), {warpsSeen} warp(s) scanned, " +
                          $"tier={winningTier}, candidates={ordered.Count} [{preview}{(ordered.Count > 6 ? ", …" : "")}], mode={_cameraMode}");

        return ordered;
    }

    /// <summary>
    /// P371 — narrow every target-pick candidate source to the local player's OWN
    /// current sector, before any frustum/FOV selection. Mirrors the warp-draw
    /// loop's proven pattern (`if (wp.ClusterId != myWarpSector) continue;`, see
    /// <see cref="Draw"/> warp-draw): the client's <c>Ships/Warps/Stations/Asteroids</c>
    /// are the full, map-wide, unconditionally-exported sets — every warp/station/
    /// asteroid of every sector and every known ship are exported to every peer at
    /// join (a deliberate simplification from an earlier task, not something to
    /// change here). Because sectors do NOT share a bounded/related coordinate
    /// space, an object from a totally different sector can coincidentally project
    /// inside the camera frustum and be offered as a real target, producing the
    /// "large list of unnamed targets with T" (the ~134 map-wide asteroids and
    /// every known ship, including out-of-sector and ghost ones, all pooled).
    ///
    /// Filtering is per object type: ships by <see cref="ShipEntity.SectorId"/>
    /// (the same source the warp-draw loop reads as <c>ownW.SectorId</c>),
    /// warps/stations/asteroids by their <c>ClusterId</c>
    /// (<see cref="DataWarpIGC.ClusterId"/>, <see cref="DataStationIGC.ClusterId"/>,
    /// <see cref="DataAsteroidIGC.ClusterId"/>). This only shrinks the candidate
    /// POOL to what is really in the player's current sector; how a candidate is
    /// CHOSEN from within that pool (<see cref="SelectCandidates"/>) is untouched.
    /// The self / not-visible / no-update ship filters are preserved exactly.
    /// </summary>
    internal static List<CandidateObject> GatherSectorCandidates(
        IReadOnlyDictionary<short, ShipEntity> ships,
        IReadOnlyDictionary<short, DataWarpIGC> warps,
        IReadOnlyDictionary<short, DataStationIGC> stations,
        IReadOnlyDictionary<short, DataAsteroidIGC> asteroids,
        short me, short mySector)
    {
        var objs = new List<CandidateObject>();

        // Tier 1 source — ships (always gathered; only offered when one is in
        // view). The self / dead / no-update filters match the original method.
        foreach (var (id, ship) in ships)
        {
            if (id == me || ship.IsNotVisible || ship.Latest is not { } up) continue;
            if (ship.SectorId != mySector) continue;   // P371: same-sector only
            string nm = string.IsNullOrEmpty(ship.CharacterName) ? $"ship {id}" : ship.CharacterName;
            objs.Add(new CandidateObject(up.Position, TargetKind.Ship, id, nm));
        }

        // Tier 2/3/4 sources — warps, stations, asteroids (all gathered; the tier
        // ordering decides which is actually offered). P371: narrow each to the
        // player's current sector via its ClusterId.
        foreach (var (id, wp) in warps)
        {
            if (wp.ClusterId != mySector) continue;
            objs.Add(new CandidateObject(Vec(wp.Position), TargetKind.Warp, id, wp.Name));
        }
        foreach (var (id, st) in stations)
        {
            if (st.ClusterId != mySector) continue;
            objs.Add(new CandidateObject(Vec(st.Position), TargetKind.Station, id, st.Name));
        }
        foreach (var (id, ast) in asteroids)
        {
            if (ast.ClusterId != mySector) continue;
            objs.Add(new CandidateObject(Vec(ast.Position), TargetKind.Asteroid, id, ast.Name));
        }

        return objs;
    }

    /// <summary>A raw target candidate to feed <see cref="SelectCandidates"/>: a
    /// world position (<see cref="SimVec"/>), its <see cref="TargetKind"/>, and its
    /// display name/id. The world-object filters (self / dead / no-update ships)
    /// are the caller's job; the tier ordering is pure here.</summary>
    internal readonly record struct CandidateObject(SimVec Position, TargetKind Kind, short Id, string Name);

    /// <summary>P256 — pure candidate collection + priority-tier ordering. Given
    /// every object to consider and a projector that maps a world position to a
    /// normalized screen-space offset (sx, sy) — null when the point falls outside
    /// the view frustum — returns the in-view candidates ordered nearest-to-
    /// crosshair within the HIGHEST present tier, offering ONLY that tier
    /// (everything below it is suppressed outright, not merely deprioritized). Tier
    /// order: <b>Ships → Warps → Stations → Asteroids</b>.
    ///
    /// Extracted from <see cref="CollectTargetCandidates"/> so the priority logic is
    /// a single, headless, testable source of truth (reached from the live-verify
    /// harness via InternalsVisibleTo). See <see cref="CollectTargetCandidates"/>
    /// for the full rationale of each tier.</summary>
    internal static List<AutopilotTarget> SelectCandidates(
        IReadOnlyList<CandidateObject> objects,
        Func<SimVec, (float Sx, float Sy)?> projectToScreen,
        out int shipsInFrustum, out int warpsInFrustum, out int stationsInFrustum, out int asteroidsInFrustum,
        out string winningTier)
    {
        // Fill a per-kind bucket with only the in-view members. The enum's integer
        // order (Ship, Station, Asteroid, Warp) is NOT the priority order, so rank
        // each kind explicitly; then offer the single lowest-rank non-empty bucket.
        var buckets = new List<(AutopilotTarget Target, float DistSq)>[4];
        for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<(AutopilotTarget Target, float DistSq)>();
        foreach (var o in objects)
        {
            if (projectToScreen(o.Position) is not { } scr) continue;
            buckets[(int)o.Kind].Add((new AutopilotTarget(o.Kind, o.Id, o.Name), scr.Sx * scr.Sx + scr.Sy * scr.Sy));
        }

        shipsInFrustum = buckets[(int)TargetKind.Ship].Count;
        warpsInFrustum = buckets[(int)TargetKind.Warp].Count;
        stationsInFrustum = buckets[(int)TargetKind.Station].Count;
        asteroidsInFrustum = buckets[(int)TargetKind.Asteroid].Count;

        static int Rank(TargetKind k) => k switch
        {
            TargetKind.Ship => 0,
            TargetKind.Warp => 1,
            TargetKind.Station => 2,
            TargetKind.Asteroid => 3,
            _ => 4,
        };

        int best = int.MaxValue;
        for (int t = 0; t < buckets.Length; t++)
            if (buckets[t].Count > 0) best = Math.Min(best, Rank((TargetKind)t));

        if (best == int.MaxValue)
        {
            winningTier = "none";
            return new List<AutopilotTarget>();
        }

        for (int t = 0; t < buckets.Length; t++)
        {
            if (buckets[t].Count == 0 || Rank((TargetKind)t) != best) continue;
            winningTier = ((TargetKind)t).ToString().ToLowerInvariant();
            buckets[t].Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
            return buckets[t].ConvertAll(c => c.Target);
        }
        winningTier = "none";
        return new List<AutopilotTarget>();
    }

    /// <summary>Nearest in-view candidate (see <see cref="CollectTargetCandidates"/>),
    /// or null if nothing qualifies — used for the G-key implicit-pick fallback,
    /// which always wants the closest thing, not a cycle step.</summary>
    private AutopilotTarget? PickNearestTarget(SimVec simPos, Orientation simOrient)
    {
        var candidates = CollectTargetCandidates(simPos, simOrient);
        return candidates.Count > 0 ? candidates[0] : null;
    }

    /// <summary>T-key target selection: steps to the NEXT in-view candidate after
    /// whichever is currently selected (nearest-to-crosshair first, wrapping
    /// around), rather than always re-picking the single nearest one. Without
    /// this, repeated T presses looked "stuck" on the same ship whenever the
    /// player wasn't actively moving the crosshair between presses — confirmed
    /// live. Falls back to the nearest candidate when nothing is currently
    /// selected, or the current selection no longer appears in view.</summary>
    private AutopilotTarget? CycleTarget(SimVec simPos, Orientation simOrient)
    {
        var candidates = CollectTargetCandidates(simPos, simOrient);
        if (candidates.Count == 0) return null;
        if (_autoTarget is { } current)
        {
            int idx = candidates.FindIndex(c => c.Kind == current.Kind && c.Id == current.Id);
            if (idx >= 0)
                return candidates[(idx + 1) % candidates.Count];
        }
        return candidates[0];
    }
}
