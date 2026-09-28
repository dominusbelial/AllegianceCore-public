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
                                              world.Asteroids, me, ownShip.SectorId,
                                              ShipRoleBits, StationRoleBits);
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
        short me, short mySector,
        Func<short, short>? shipRoleBits = null,
        Func<short, short>? stationRoleBits = null)
    {
        var objs = new List<CandidateObject>();

        // Tier 1 source — ships (always gathered; only offered when one is in
        // view). The self / dead / no-update filters match the original method.
        foreach (var (id, ship) in ships)
        {
            if (id == me || ship.IsNotVisible || ship.Latest is not { } up) continue;
            if (ship.SectorId != mySector) continue;   // P371: same-sector only
            string nm = string.IsNullOrEmpty(ship.CharacterName) ? $"ship {id}" : ship.CharacterName;
            // P415 — the owning side is used only once a real EXPORT identity is
            // known (P373 gate), and the hull-role bits are resolved from the
            // ship's HullId by the caller's resolver (client ShopCatalog).
            short? side = ship.HasRealIdentity ? ship.SideId : null;
            short role = shipRoleBits?.Invoke(ship.HullId) ?? 0;
            objs.Add(new CandidateObject(up.Position, TargetKind.Ship, id, nm, side, role));
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
            // P415 — stations always carry a real SideId on the wire; role bits
            // are the station-type's SabmCapabilities (for the base / major-base
            // filters) resolved from the caller's catalogue.
            short stRole = stationRoleBits?.Invoke(st.StationTypeId) ?? 0;
            objs.Add(new CandidateObject(Vec(st.Position), TargetKind.Station, id, st.Name, st.SideId, stRole));
        }
        foreach (var (id, ast) in asteroids)
        {
            if (ast.ClusterId != mySector) continue;
            objs.Add(new CandidateObject(Vec(ast.Position), TargetKind.Asteroid, id, ast.Name));
        }

        return objs;
    }

    /// <summary>A raw target candidate to feed <see cref="SelectCandidates"/> /
    /// <see cref="FilterCandidates"/>: a world position (<see cref="SimVec"/>),
    /// its <see cref="TargetKind"/>, and its display name/id. The world-object
    /// filters (self / dead / no-update ships) are the caller's job; the tier
    /// ordering is pure here.
    ///
    /// <para>P415 — <see cref="SideId"/> and <see cref="RoleBits"/> are the
    /// extra classification data the friend/foe/hull-role targeting family
    /// needs, drawn from the SAME source data already flowing into
    /// <see cref="GatherSectorCandidates"/>'s <c>ships</c>/<c>stations</c>
    /// dictionaries (no new wire source): <see cref="SideId"/> is the owning
    /// side (a ship's <c>ShipEntity.SideId</c> — present only once the ship has
    /// a real EXPORT identity, per P373 — or a station's
    /// <c>DataStationIGC.SideId</c>), and <see cref="RoleBits"/> is the
    /// hull/station ABILITY bitmask that the role filters read (a ship's
    /// <c>HabmCapabilities</c> or a station's <c>SabmCapabilities</c>, resolved
    /// from the client's <c>ShopCatalog</c> — the same ability-bit family native's
    /// role filters read, <c>src/Igc/igc.h:726-758</c>).</summary>
    internal readonly record struct CandidateObject(
        SimVec Position, TargetKind Kind, short Id, string Name,
        short? SideId = null, short RoleBits = 0);

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

    // ═══════════════════════════════════════════════════════════════════
    // P415 — friend/foe/hull-role filtered nearest targeting (the port of
    // native's Targeting family, trekinput.cpp:1531-1600 → FindTarget,
    // src/Igc/common.cpp:470-653 with FindableModel common.cpp:254-353).
    //
    // Native implements ~30 near-identical commands (TargetEnemy/Friendly/
    // AlliedBase × Nearest/Prev × optional per-role ability bitmask) as the
    // combinatorial product of a target-TYPE mask, a SIDE relation
    // (enemy/friendly/neutral) and an optional ability-bit role filter. This
    // port mirrors that with ONE parameterized filter
    // (<see cref="TargetRelation"/> + <see cref="TargetFilterSpec"/>) instead
    // of ~30 cloned methods — the port's shared-implementation convention for
    // similar families. The nearest/prev pair collapses to a single cycling
    // selection over the filtered list (first press = nearest, repeats step
    // through the matching-only candidates and wrap).
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>The side relation of a target candidate to the local player —
    /// the axis native's <c>c_ttEnemy</c>/<c>c_ttFriendly</c> side filters
    /// encode (FindableModel, common.cpp:269-278).</summary>
    internal enum TargetRelation
    {
        /// <summary>A candidate on a DIFFERENT side than the local player
        /// (native <c>c_ttEnemy</c>).</summary>
        Enemy,
        /// <summary>A candidate on the local player's OWN side (native
        /// <c>c_ttFriendly</c> collapsed to same-side: this port's client has
        /// no alliance wire map, so allied sides are indistinguishable from
        /// strangers client-side).</summary>
        Friendly,
        /// <summary>An allied-side base. Native (the #Imago 8/1/09 addition,
        /// <c>iAllies==2</c>, common.cpp:275-278) matches bases on a side that
        /// is allied-with-but-not-equal to the player's. This port's client has
        /// no alliance-side map (alliances need ≥3 active sides, Server.cs
        /// ~:5453), so the relation is implemented as the same-side (friendly)
        /// matching and documented as only meaningfully exercisable in a 3+
        /// side game — see the P415 writeup.</summary>
        AlliedBase,
        /// <summary>The local player's own ship. Included for the native family's
        /// completeness (TK_TargetSelf); no key is bound to it in this port.</summary>
        Self,
    }

    /// <summary>
    /// The full parameterization of a filtered-nearest targeting command:
    /// a <see cref="TargetRelation"/>, an optional hull/station ability-role
    /// bitmask (<see cref="RoleBitmask"/>; 0 = any role, mirroring native's
    /// <c>abmAbilities==0</c> no-role-filter case), and the optional
    /// station-only / major-base-only narrowings. This is the port's single
    /// generalized replacement for native's ~30 TrekFindTarget call sites
    /// (WinTrek.cpp:10122-10303).
    /// </summary>
    internal readonly record struct TargetFilterSpec(
        TargetRelation Relation,
        short RoleBitmask = 0,
        bool StationOnly = false,
        bool MajorBaseOnly = false);

    /// <summary>
    /// P415 — pure filtered-nearest selection over already-gathered
    /// <see cref="CandidateObject"/>s (same-sector, P371). Returns the matching
    /// candidates sorted NEAREST-first by true 3D distance from
    /// <paramref name="from"/> — the port's analogue of native
    /// <c>FindTarget</c>'s <c>c_ttNearest</c> (<c>dp.LengthSquared()</c>,
    /// common.cpp:575-578). Non-matching candidates (wrong relation, wrong role,
    /// wrong kind) are excluded outright, so the nearest/cycle only ever steps
    /// through matching candidates. Headless + static for unit testing.
    /// </summary>
    internal static List<AutopilotTarget> FilterCandidates(
        IReadOnlyList<CandidateObject> objs,
        short? mySideId,
        short selfId,
        SimVec from,
        in TargetFilterSpec spec)
    {
        var items = new List<(AutopilotTarget Target, float DistSq)>();
        foreach (var o in objs)
        {
            if (!MatchesFilter(o, mySideId, selfId, spec)) continue;
            var d = o.Position - from;
            items.Add((new AutopilotTarget(o.Kind, o.Id, o.Name), d.LengthSquared()));
        }
        items.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
        return items.ConvertAll(i => i.Target);
    }

    /// <summary>P415 — the pure relation/role/kind gate for one candidate.
    /// Matches native <c>FindableModel</c>'s side + ability-bit logic
    /// (common.cpp:254-353) simplified to the sides/roles this port's client
    /// can actually see.</summary>
    internal static bool MatchesFilter(
        in CandidateObject o,
        short? mySideId,
        short selfId,
        in TargetFilterSpec spec)
    {
        // Kind gate — relation filters pick either ships or stations, never both
        // (native picks a single model LIST by type mask, common.cpp:498-506).
        if (spec.StationOnly ? o.Kind != TargetKind.Station : o.Kind != TargetKind.Ship)
            return false;

        switch (spec.Relation)
        {
            case TargetRelation.Enemy:
                // A sided candidate whose side differs from ours; never self.
                if (o.Id == selfId || o.SideId is not { } es || es == mySideId) return false;
                break;
            case TargetRelation.Friendly:
            case TargetRelation.AlliedBase:
                // Same-side only (AlliedBase degrades to friendly in this port —
                // see the enum remark; no alliance side map reaches the client).
                if (o.Id == selfId || o.SideId != mySideId) return false;
                break;
            case TargetRelation.Self:
                if (o.Id != selfId) return false;
                break;
        }

        // Role filter (native abmAbilities bit test, common.cpp:291-321): when a
        // role bitmask is given, the candidate's ability bits must include it.
        if (spec.RoleBitmask != 0 && (o.RoleBits & spec.RoleBitmask) == 0) return false;
        // Major-base-only narrows stations to those whose type is a victory
        // objective (native c_sabmFlag, common.cpp:10206 / igc.h:753).
        if (spec.MajorBaseOnly && (o.RoleBits & DataStationTypeIGC.SabmFlag) == 0) return false;
        return true;
    }

    /// <summary>
    /// P415 — the pure wrap-around cycle step over a nearest-first ordered list of
    /// matching candidates: returns the candidate AFTER <paramref name="current"/>
    /// (by kind+id) when it is present, else the nearest (<c>ordered[0]</c>); never
    /// steps past the list end (wraps). Mirrors <see cref="CycleTarget"/>'s shape
    /// but is restricted to whatever list the caller passes — for the filtered
    /// family that is the matching-only list, so a cycle can never select a
    /// non-matching candidate. Headless + static for unit testing.
    /// </summary>
    internal static AutopilotTarget StepCycle(IReadOnlyList<AutopilotTarget> ordered, AutopilotTarget? current)
    {
        if (current is { } c)
        {
            for (int i = 0; i < ordered.Count; i++)
                if (ordered[i].Kind == c.Kind && ordered[i].Id == c.Id)
                    return ordered[(i + 1) % ordered.Count];
        }
        return ordered[0];
    }

    /// <summary>
    /// P415 — the live wrapper: gather the local player's same-sector candidates
    /// (with P415 side/role classification), run <see cref="FilterCandidates"/>
    /// for the given spec, and either return the NEAREST matching candidate or,
    /// when <paramref name="cycle"/> and the current <c>_autoTarget</c> is among
    /// the matching set, step to the next matching candidate (wrap-around) via
    /// <see cref="StepCycle"/> — mirroring <see cref="CycleTarget"/>'s cycle shape
    /// but restricted to the filtered (matching-only) list. Reuses
    /// <see cref="CollectTargetCandidates"/>'s existing gather (the P371
    /// same-sector narrowing); unlike the generic T-key it is frustum-independent
    /// and 3D-nearest, faithful to native.
    /// </summary>
    private AutopilotTarget? SelectFilteredTarget(SimVec simPos, Orientation simOrient, in TargetFilterSpec spec, bool cycle)
    {
        var world = _session.World;
        if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)) return null;
        List<CandidateObject> objs;
        lock (_session.Gate)
            objs = GatherSectorCandidates(world.Ships, world.Warps, world.Stations, world.Asteroids,
                                          me, own.SectorId, ShipRoleBits, StationRoleBits);
        var filtered = FilterCandidates(objs, world.MySideId, me, simPos, spec);
        if (filtered.Count == 0) return null;
        return cycle ? StepCycle(filtered, _autoTarget) : filtered[0];
    }

    /// <summary>P415 — convenience used by the U/Z filtered-target keys: read the
    /// local pose, run <see cref="SelectFilteredTarget"/> for <paramref name="spec"/>
    /// in cycling mode (first press = nearest, repeats step through matches) and
    /// surface the result as the selected target with a console line.</summary>
    private void SelectFilteredTargetForKey(in TargetFilterSpec spec, string noun)
    {
        if (!TryOwnPose(out var pos, out var orient, out _)) return;
        var picked = SelectFilteredTarget(pos, orient, spec, cycle: true);
        if (picked is { } p)
        {
            _autoTarget = picked;
            Console.WriteLine($"[monogame] target {noun}: {p.Name} (#{p.Id})");
        }
        else
            Console.WriteLine($"[monogame] no {noun} in sector");
    }

    /// <summary>P415 — resolve a ship hull id to its role-relevant ABILITY bits
    /// from the client's static <see cref="ShopCatalog"/> (HabmCapabilities;
    /// 0 = unknown/unresolvable so the candidate matches only the no-role
    /// filters). This is the SAME core table the docked shop reads, not a new
    /// wire source.</summary>
    private short ShipRoleBits(short hullId)
    {
        if (_shopCatalog is not { } cat) return 0;
        foreach (var h in cat.Hulls)
            if (h.HullId == hullId) return h.HabmCapabilities;
        return 0;
    }

    /// <summary>P415 — resolve a station TYPE id to its SabmCapabilities from
    /// the client's <see cref="ShopCatalog"/> (for the base / major-base
    /// filters; 0 = unknown).</summary>
    private short StationRoleBits(short stationTypeId)
    {
        if (_shopCatalog?.StationTypes is not { } sts) return 0;
        return sts.TryGetValue(stationTypeId, out var st) ? st.SabmCapabilities : (short)0;
    }
}
