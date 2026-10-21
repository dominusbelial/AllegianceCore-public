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
    internal enum TargetKind { Ship, Station, Asteroid, Warp, Probe, Cluster, Treasure }

    /// <summary>A selected autopilot target: enough to re-resolve its live
    /// position each frame from <see cref="ClientWorld"/> (ships may move),
    /// plus a display name for the console / window-title readout.
    /// <para><b>HUD-12b — <see cref="UseDefault"/>.</b> Native only resolves a
    /// <c>c_cidDefault</c> command through <c>GetDefaultOrder</c>; every other id
    /// (including <c>c_cidNone</c>) is written unchanged
    /// (<c>WinTrek/WinTrek.cpp:8827-8833</c>). A plain T / click / cycle pick
    /// constructs a target with the default id and therefore resolves; the
    /// explicit <c>c_cidNone</c> callers — <c>TK_TargetSelf</c>
    /// (<c>WinTrek.cpp:10442</c>) and <c>TK_TargetCommand</c>
    /// (<c>WinTrek.cpp:10410</c>) — set <see cref="UseDefault"/> false so the
    /// native no-command payload survives.</para></summary>
    internal readonly record struct AutopilotTarget(
        TargetKind Kind, short Id, string Name, short CommandId = OrderChange.CidNone,
        bool UseDefault = true);

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
        else if (_directorActive)
            // Director b-roll: targeting returns to the normal chase framing
            // (the director is cinematic only and cancels on any flight input).
            (view, projection) = _chaseCamera.Matrices(xnaPos, rot, aspect, 0f, out eye, _myRenderRadius);
        else
            // P580 — every other mode (incl. the native external modes) projects
            // candidates through the SAME camera the Draw pass is using this
            // frame, so targeting is accurate in the current view.
            (view, projection, eye) = ComputeFlightCamera(xnaPos, rot, simOrient, aspect, 0f);
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
        bool MajorBaseOnly = false,
        bool IncludeProbes = false);

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
        in TargetFilterSpec spec,
        Func<short, short, bool>? allied = null)
    {
        // Kind gate — relation filters pick either ships or stations, never both
        // (native picks a single model LIST by type mask, common.cpp:498-506).
        // P587 — a station-targeting filter may additionally include RESCUE
        // PROBES when the local hull is a lifepod (native's
        // <c>c_ttProbe | c_sabmRescueAny</c> addition, WinTrek.cpp:10219-10220).
        bool isStationLike = o.Kind == TargetKind.Station ||
                             (spec.IncludeProbes && o.Kind == TargetKind.Probe);
        if (spec.StationOnly ? !isStationLike : o.Kind != TargetKind.Ship)
            return false;

        switch (spec.Relation)
        {
            case TargetRelation.Enemy:
                // A sided candidate whose side differs from ours; never self.
                if ((o.Kind == TargetKind.Ship && o.Id == selfId) || o.SideId is not { } es || es == mySideId
                    || (mySideId is { } mine && (allied?.Invoke(mine, es) ?? false))) return false;
                break;
            case TargetRelation.Friendly:
            case TargetRelation.AlliedBase:
                // Probe special-case (lifepod rescue, native FindableModel
                // common.cpp:279-285): a RESCUE-capable probe matches regardless
                // of its side (rescue probes help any allied pod). Stations must
                // still be same-side.
                if (o.Kind == TargetKind.Probe &&
                    (o.RoleBits & DataStationTypeIGC.SabmRescueAny) != 0)
                    break;
                if ((o.Kind == TargetKind.Ship && o.Id == selfId) || o.SideId is not { } friend
                    || (friend != mySideId && !(mySideId is { } own && (allied?.Invoke(own, friend) ?? false)))) return false;
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

    /// <summary>P623 — the step DIRECTION of a filtered-target command: native
    /// ships THREE forms of every Targeting family command — a plain "next"
    /// (<c>TK_Target{Friendly|Enemy}Bomber</c> &amp;co, cycles forward through the
    /// matching list), a Nearest form (<c>…Nearest</c>, jumps to the closest
    /// match), and a Previous form (<c>…Prev</c>, steps backward). TrekFindTarget
    /// encodes the distinction by OR-ing <c>c_ttPrevious</c> vs <c>c_ttNearest</c>
    /// into the mask (WinTrek.cpp:8963-8969); FindTarget then walks the list
    /// forward (<c>next()</c>) or backward (<c>txen()</c>) from the current target,
    /// or forces nearest when there is no current target (common.cpp:505-510,
    /// 611-614). This port mirrors that as an explicit direction on the step.</summary>
    internal enum StepDirection
    {
        /// <summary>Jump to the single closest matching candidate (native
        /// <c>c_ttNearest</c> — picks the globally nearest, ignores current).</summary>
        Nearest,
        /// <summary>Advance one matching candidate past the current (native plain
        /// "next", <c>mLink->next()</c>; wraps; nearest when no valid current).</summary>
        Next,
        /// <summary>Retreat one matching candidate before the current (native
        /// <c>c_ttPrevious</c>, <c>mLink->txen()</c>; wraps; nearest when none).</summary>
        Previous,
    }

    /// <summary>
    /// P415/P623 — the pure wrap-around cycle step over a nearest-first ordered
    /// list of matching candidates. <see cref="StepDirection.Next"/> returns the
    /// candidate AFTER <paramref name="current"/> (by kind+id) when it is present,
    /// <see cref="StepDirection.Previous"/> the one BEFORE, <see cref="StepDirection.Nearest"/>
    /// always the head (the closest); any direction falls back to the nearest
    /// (<c>ordered[0]</c>) when the current selection is absent — native's
    /// <c>FindTarget</c> forces <c>c_ttNearest</c> in exactly that case
    /// (common.cpp:505-510). Never steps past the list end (wraps). Restricted to
    /// whatever list the caller passes — for the filtered family that is the
    /// matching-only list, so a step can never select a non-matching candidate.
    /// Headless + static for unit testing.
    /// </summary>
    internal static AutopilotTarget StepCycle(IReadOnlyList<AutopilotTarget> ordered, AutopilotTarget? current,
        StepDirection dir = StepDirection.Next)
    {
        if (current is { } c && dir != StepDirection.Nearest)
        {
            for (int i = 0; i < ordered.Count; i++)
                if (ordered[i].Kind == c.Kind && ordered[i].Id == c.Id)
                    return dir == StepDirection.Previous
                        ? ordered[(i - 1 + ordered.Count) % ordered.Count]
                        : ordered[(i + 1) % ordered.Count];
        }
        return ordered[0];
    }

    /// <summary>P623 — the hull/station CLASS a Targeting-family command filters
    /// to. Native resolves each class to an ability-bitmask argument handed to
    /// <c>TrekFindTarget</c> (WinTrek.cpp:10162-10331) and tested against the
    /// candidate's <c>HabmCapabilities</c>/<c>SabmCapabilities</c> by
    /// <see cref="FindableModel"/> (common.cpp:291-321) via
    /// <c>pht->HasCapability(abm)</c> — "shares at least one ability bit".</summary>
    internal enum TargetClass
    {
        /// <summary>Bomber — native <c>c_habmThreatToStation</c> (WinTrek.cpp:10166,10301).</summary>
        Bomber,
        /// <summary>Fighter — native <c>c_habmFighter</c> (WinTrek.cpp:10174,10307).</summary>
        Fighter,
        /// <summary>Miner — native <c>c_habmMiner</c> (WinTrek.cpp:10182,10315).</summary>
        Miner,
        /// <summary>Constructor — native <c>c_habmBuilder</c> (WinTrek.cpp:10190,10323).</summary>
        Constructor,
        /// <summary>Lifepod — native <c>c_habmLifepod</c> (WinTrek.cpp:10331, friendly only).</summary>
        Lifepod,
    }

    /// <summary>P623 — the <see cref="HabmCapabilities"/> ability bit a
    /// <see cref="TargetClass"/> filter tests against (native's abm argument,
    /// WinTrek.cpp class cases). Pure + static.</summary>
    internal static short ClassRoleBits(TargetClass cls) => cls switch
    {
        TargetClass.Bomber => DataHullTypeIGC.HabmThreatToStation,
        TargetClass.Fighter => DataHullTypeIGC.HabmFighter,
        TargetClass.Miner => DataHullTypeIGC.HabmMiner,
        TargetClass.Constructor => DataHullTypeIGC.HabmBuilder,
        TargetClass.Lifepod => DataHullTypeIGC.HabmLifepod,
        _ => 0,
    };

    /// <summary>P623 — build the <see cref="TargetFilterSpec"/> for a
    /// ship-CLASS command: <see cref="TargetRelation"/> (enemy/friendly) + the
    /// class's <see cref="HabmCapabilities"/> role bit, ships only (native
    /// <c>c_ttShip</c>), mirroring the native abm case per class
    /// (WinTrek.cpp:10162-10331). Pure + static.</summary>
    internal static TargetFilterSpec ClassFilterSpec(TargetRelation relation, TargetClass cls)
        => new(relation, ClassRoleBits(cls));

    /// <summary>P623 — build the <see cref="TargetFilterSpec"/> for a MAJOR-BASE
    /// command: enemy/friendly stations ANY cluster flagged as a victory
    /// objective (native <c>c_ttStation | c_ttAnyCluster | c_sabmFlag</c>,
    /// WinTrek.cpp:10202-10206 / 10266-10270). Pure + static.</summary>
    internal static TargetFilterSpec MajorBaseSpec(TargetRelation relation)
        => new(relation, DataStationTypeIGC.SabmFlag, StationOnly: true);

    // ═══════════════════════════════════════════════════════════════════
    // P587 — native BASE targeting (TK_TargetFriendlyBase/EnemyBase and the
    // Nearest/Prev forms), the port of the ttMask/abm logic in
    // WinTrek.cpp:10194-10273 → FindTarget (common.cpp:470) with the
    // hull-dockability ability filter.
    //
    // Native resolves the ability bitmask (abm) from the LOCAL ship's own
    // base-hull capabilities (WinTrek.cpp:10211-10224):
    //   • lifepod   → c_sabmRescue | c_sabmRescueAny | c_sabmLand AND the
    //                 search mask adds c_ttProbe (rescue probes);
    //   • fighter   → c_sabmLand;
    //   • anything else (capital) → c_sabmCapLand.
    // Crucially the search is c_ttAnyCluster — bases (and rescue probes) are
    // hunted ACROSS ALL SECTORS, not just the player's current one (the port's
    // pre-P587 sector-only shift+Z). Ability semantics = HasCapability =
    // "shares at least one bit" (hullTypeIGC.h:243-245), which the port's role
    // filter already implements. Enemy bases use c_sabmPedestal exclusion.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// P587 — resolve the friendly-base <see cref="TargetFilterSpec"/> for the
    /// local ship's own hull ability bits (HabmCapabilities), mirroring native's
    /// abm selection (WinTrek.cpp:10211-10224). Pure + static for unit testing.
    /// </summary>
    internal static TargetFilterSpec FriendlyBaseSpec(short hullHabm)
    {
        if ((hullHabm & DataHullTypeIGC.HabmLifepod) != 0)
            return new TargetFilterSpec(
                TargetRelation.Friendly,
                unchecked((short)(DataStationTypeIGC.SabmRescue
                                  | DataStationTypeIGC.SabmRescueAny
                                  | DataStationTypeIGC.SabmLand)),
                StationOnly: true, IncludeProbes: true);
        if ((hullHabm & DataHullTypeIGC.HabmFighter) != 0)
            return new TargetFilterSpec(TargetRelation.Friendly, DataStationTypeIGC.SabmLand, StationOnly: true);
        return new TargetFilterSpec(TargetRelation.Friendly, DataStationTypeIGC.SabmCapLand, StationOnly: true);
    }

    /// <summary>
    /// P587 — resolve the enemy-base <see cref="TargetFilterSpec"/>: any enemy
    /// station anywhere, excluding pure-pedestal (indestructible) stations —
    /// native <c>~c_sabmPedestal</c> (WinTrek.cpp:10199). Pure + static.
    /// </summary>
    internal static TargetFilterSpec EnemyBaseSpec()
        => new(TargetRelation.Enemy,
               unchecked((short)~(int)DataStationTypeIGC.SabmPedestal),
               StationOnly: true);

    /// <summary>
    /// P587 — gather EVERY station in the mission (any sector, faithful to
    /// native's c_ttAnyCluster) and, when <paramref name="probes"/> is supplied,
    /// every probe, classifying each with its SideId and ability RoleBits (a
    /// station's SabmCapabilities, a probe's EabmCapabilities — the same wire +
    /// catalogue data the T-key family reads, no new source). This is the
    /// <see cref="GatherSectorCandidates"/> counterpart for the any-cluster base
    /// search: unlike that method it does NOT narrow to the player's current
    /// sector (native hunts friendly/enemy bases map-wide). Pure + static.
    /// </summary>
    internal static List<CandidateObject> GatherBaseCandidates(
        IReadOnlyDictionary<short, DataStationIGC> stations,
        IReadOnlyDictionary<short, ProbeEntity>? probes,
        Func<short, short>? stationRoleBits = null,
        Func<short, short>? probeRoleBits = null)
    {
        var objs = new List<CandidateObject>();
        foreach (var (id, st) in stations)
        {
            short stRole = stationRoleBits?.Invoke(st.StationTypeId) ?? 0;
            objs.Add(new CandidateObject(Vec(st.Position), TargetKind.Station, id, st.Name, st.SideId, stRole));
        }
        if (probes is not null)
            foreach (var (id, pb) in probes)
            {
                short eabm = probeRoleBits?.Invoke(pb.ProbetypeId) ?? 0;
                objs.Add(new CandidateObject(pb.Position, TargetKind.Probe, id, $"probe {id}", pb.SideId, eabm));
            }
        return objs;
    }

    /// <summary>
    /// P505 — whether an autopilot target is usable for engagement: the player's
    /// OWN ship is never usable (native excludes self from target candidates —
    /// flying the autopilot at your own hull is a no-op selection, seen live as
    /// <c>autopilot ON → domingo1 (Ship#8107)</c>). The selection can legitimately
    /// hold self via the Y-key TK_TargetSelf (<see cref="SelectSelfTarget"/>), so the
    /// G-key engage path must check this gate rather than blindly engaging. Headless
    /// + static for unit testing.
    /// </summary>
    internal static bool IsUsableAutopilotTarget(AutopilotTarget target, short myShipId)
        => !(target.Kind == TargetKind.Ship && target.Id == myShipId);

    /// <summary>
    /// P523 — resolve the RENDER-pose ANCHOR for a ship-id HUD element: the pose
    /// the renderer actually drew THIS frame, falling back to the raw last
    /// <see cref="ServerShipUpdate"/> pose only when the ship was not rendered
    /// this frame. <paramref name="renderPoses"/> is the per-frame render pose
    /// table (<c>Game1._poses</c>): the own-ship local-simulation pose for self
    /// (P508) and the P448b dead-reckoned pose for remotes. Target brackets,
    /// labels, the bearing arrow and the missile-lock bracket must anchor to this
    /// SAME pose or they jitter around the smoothly-drawn hull (the raw 20 Hz
    /// <see cref="ServerShipUpdate"/> pose steps only every ~50 ms, while the render
    /// pose advances every frame — the reported \"shaky\" bracket/label). This is
    /// the one shared \"render pose\" accessor the HUD ship anchors all use; no
    /// third path. Callers: the target bracket/label (FlightInput), the
    /// missile-lock bracket (Camera), and the radar contact bracket + in-world
    /// side tag (P527, <see cref=\"ResolveRadarContactAnchor\"/>). Headless +
    /// static for unit testing (via InternalsVisibleTo).
    /// </summary>
    internal static bool ResolveShipAnchor(
        IReadOnlyDictionary<short, Flight.PredictedPose> renderPoses,
        IReadOnlyDictionary<short, ShipEntity> ships,
        short id, out SimVec pos)
    {
        if (renderPoses is not null && renderPoses.TryGetValue(id, out var rp))
        {
            pos = rp.Position;
            return true;
        }
        if (ships is not null && ships.TryGetValue(id, out var s)
            && s.Latest is { } up && !s.IsNotVisible)
        {
            pos = up.Position;
            return true;
        }
        pos = default;
        return false;
    }

    /// <summary>
    /// P527 — resolve the position a RADAR CONTACT (another ship) and its
    /// in-world side tag anchor to in the radar draw loop, and the loop's OWN
    /// ship eye: the pose the renderer drew THIS frame (P448b dead-reckoned for
    /// remotes, P508 local-sim for self) via <see cref="ResolveShipAnchor"/>,
    /// falling back to the raw last <see cref="ServerShipUpdate"/> pose only
    /// when the ship was not rendered this frame. Fixes the live playtest
    /// (2026-10-04) where a hull and its HUD icon read DIFFERENT positions — the
    /// icon trailed a steady moving miner by roughly velocity × time-since-last-
    /// update because it anchored to the raw 20 Hz pose while the hull used the
    /// per-frame render pose. Same shared render-pose accessor as the P523
    /// anchors; the radar loop previously WAS the third path P523's comment
    /// said didn't exist. Headless + static for unit testing.
    /// </summary>
    internal static bool ResolveRadarContactAnchor(
        IReadOnlyDictionary<short, Flight.PredictedPose> renderPoses,
        IReadOnlyDictionary<short, ShipEntity> ships,
        short id, out SimVec pos)
        => ResolveShipAnchor(renderPoses, ships, id, out pos);

    /// <summary>
    /// P540b — decide the client autopilot's current steering GOAL, routing
    /// cross-sector targets through warps first. This is the single decision
    /// point that fixes the P540 eject bug (\"pod autopilot took me to the middle
    /// of nowhere\": the rescue station was two warps away, but
    /// <see cref=\"ResolveTargetPosition\"/> returned its foreign-sector
    /// coordinates and the P524 law steered at empty space inside the current
    /// sector). Pure + headless so it is unit-testable without a live world.
    ///
    /// Given the own ship's sector and the autopilot target's sector + real local
    /// position, returns:
    /// <list type=\"bullet\">
    /// <item><b>same sector</b> (or unknown target sector) → the target's own
    /// local position (<c>routed=false</c>) — the pre-P540b law unchanged;</item>
    /// <item><b>cross sector</b> → the next warp mouth toward the target's
    /// sector, routed over the REAL warp table via
    /// <see cref=\"Flight.StarmapGotoRouter.TryNextWaypointFromWarps\"/>
    /// (<c>routed=true</c>) — a local coordinate we can actually fly at, not the
    /// target's coordinates in its own sector.</item>
    /// </list>
    /// Returns <c>false</c> when there is no reachable goal (no local position
    /// <i>and</i> no route). The router is only consulted when cross-sector, so
    /// <paramref name=\"allWarps\"/> may be empty for same-sector calls.
    /// </summary>
    internal static bool TryResolveAutopilotGoal(
        short ownSector,
        short? targetSector,
        SimVec? targetLocalPos,
        IReadOnlyList<Flight.StarmapGotoRouter.AllWarp> allWarps,
        out SimVec goal,
        out bool routed)
    {
        goal = default;
        routed = false;
        if (targetSector is null || targetSector.Value == ownSector)
        {
            if (targetLocalPos is { } lp) { goal = lp; return true; }
            return false;
        }
        if (Flight.StarmapGotoRouter.TryNextWaypointFromWarps(
                ownSector, targetSector.Value, allWarps, out var wp))
        {
            goal = new SimVec(wp.X, wp.Y, wp.Z);
            routed = true;
            return true;
        }
        return false;
    }

    /// <summary>
    /// P415/P623 — the live wrapper: gather the local player's same-sector candidates
    /// (with P415 side/role classification), run <see cref="FilterCandidates"/>
    /// for the given spec, and return the matching candidate per
    /// <paramref name="dir"/> — <see cref="StepDirection.Nearest"/> = the NEAREST
    /// match, otherwise step from the current <c>_autoTarget</c> through the
    /// matching candidates (forward/backward, wrap) via <see cref="StepCycle"/> —
    /// mirroring <see cref="CycleTarget"/>'s cycle shape but restricted to the
    /// filtered (matching-only) list, faithful to native's nearest/next/previous
    /// triad (WinTrek.cpp:8963-8969 → common.cpp:505-510). Reuses
    /// <see cref="CollectTargetCandidates"/>'s existing gather (the P371
    /// same-sector narrowing); unlike the generic T-key it is frustum-independent
    /// and 3D-nearest, faithful to native.
    /// </summary>
    private AutopilotTarget? SelectFilteredTarget(SimVec simPos, Orientation simOrient, in TargetFilterSpec spec, StepDirection dir)
    {
        var world = _session.World;
        if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)) return null;
        lock (_session.Gate)
        {
            var objs = spec.StationOnly
                ? GatherBaseCandidates(world.Stations, spec.IncludeProbes ? world.Probes : null, StationRoleBits, ProbeRoleBits)
                : GatherSectorCandidates(world.Ships, world.Warps, world.Stations, world.Asteroids,
                    me, RenderSector(own.SectorId), ShipRoleBits, StationRoleBits);
            AnchorCommandCandidates(objs);
            var filter = spec;
            var matching = objs.Where(x => MatchesFilter(x, world.MySideId, me, filter, world.AreAllied)).ToList();
            return spec.StationOnly
                ? ChooseAcrossClusters(matching, CandidateSector, world.Warps, RenderSector(own.SectorId), simPos, _autoTarget, dir)
                : ChooseTarget(matching, simPos, _autoTarget, dir);
        }
    }

    /// <summary>P415/P623 — convenience used by the filtered-target keys (friend/foe
    /// U/Z, and the P623 class keys): read the local pose, run
    /// <see cref="SelectFilteredTarget"/> for <paramref name="spec"/> in the given
    /// <paramref name="dir"/> and surface the result as the selected target with a
    /// console line. On no match stages the on-screen "no &lt;noun&gt; in sector" line
    /// (the HUD class-filter feedback, drawn by DrawTargetNoMatchFeedback).</summary>
    private void SelectFilteredTargetForKey(in TargetFilterSpec spec, string noun, StepDirection dir = StepDirection.Next)
    {
        if (!TryOwnPose(out var pos, out var orient, out _)) return;
        var picked = SelectFilteredTarget(pos, orient, spec, dir);
        if (picked is { } p)
        {
            CommitTargetForPlayer(p);
            // P436 — a candidate WAS found: clear any lingering no-match line.
            _targetNoMatchNoun = null;
            Console.WriteLine($"[monogame] target {noun}: {p.Name} (#{p.Id})");
        }
        else
        {
            // P436 — stage the on-screen "no <noun> in sector" line (drawn by
            // DrawTargetNoMatchFeedback in the rendering pass; P415 had this as
            // a console line only, which is invisible while playing).
            _targetNoMatchNoun = noun;
            _targetNoMatchUntil = _nowSeconds + TargetNoMatchDisplaySeconds;
            Console.WriteLine($"[monogame] no {noun} in sector");
        }
    }

    /// <summary>P723 — SIM-0279: commit a PLAYER-selected target and play the
    /// native <c>newTargetSound</c> when the selection actually changed (native
    /// SetTarget, WinTrek.cpp:8827 — <c>GetCommandTarget(c_cmdCurrent) != pmodel</c>;
    /// wave <c>newtargetneutral</c>, sounddef.mdl:311). Programmatic autopilot /
    /// dock commits that don't originate from a player targeting key do NOT
    /// route here, matching native's SetTarget-only chime.</summary>
    private void CommitTargetForPlayer(AutopilotTarget picked)
    {
        bool changed = !(_autoTarget is { } cur && cur.Kind == picked.Kind && cur.Id == picked.Id);
        _autoTarget = picked;
        // HUD-8b — native SetTarget also writes the c_cmdCurrent slot
        // (WinTrek.cpp:8823-8852): send the real ORDER_CHANGE so the server's
        // Ship.CommandTarget drives the lead predictor, and install the client
        // slot the red order arrow reads.
        SendCurrentCommandToServer(picked);
        if (changed) _sound?.PlayNewTarget();
    }

    /// <summary>P760b/SIM-0197 — drain the world's killed-model queue once per
    /// frame (on the game thread, from the UpdateFeedback call site) and run
    /// the native TargetKilled body for each. The receive thread only enqueues
    /// (Apply(KillShip) → RecordTargetKilled): the retarget reads the own pose
    /// and mutates Game1's target/autopilot state, which is game-thread state.</summary>
    private void UpdateTargetKilled()
    {
        List<ClientWorld.TargetKilledModel> kills;
        lock (_session.Gate) kills = _session.World.TakeTargetKilledModels();
        foreach (var killed in kills) OnTargetKilled(killed);
    }

    /// <summary>P760b/SIM-0197 — the port of native
    /// <c>WinTrekClient::TargetKilled</c> (<c>trekigc.cpp:2934-2974</c>):
    /// <list type="number">
    /// <item>Compare the victim with the ship's <b>c_cmdCurrent</b> target
    /// (<c>:2936</c>) — the HUD-8 slot (<see cref="ClientWorld.MyCurrentCommand"/>),
    /// NOT the client-local autopilot selection — for ANY model type.</item>
    /// <item>Gate on the SOURCE ship (own ship, or the carrier when boarded as
    /// a gunner, <c>GetSourceShip()</c>) being in a cluster (<c>:2940-2941</c>).</item>
    /// <item>Build the victim's similar-target mask (<c>GetSimilarTargetMask</c>,
    /// <c>WinTrek.cpp:267-284</c>: type bits + friendly/enemy by side/alliance);
    /// when it is the enemy-ship mask, try rescue-capable first, then
    /// miner/builder, then any (<c>:2946-2960</c>).</item>
    /// <item>SetTarget the pick (<c>:2966</c>), never the dead model itself
    /// (<c>:2962-2963</c>).</item>
    /// <item>Re-engage the autopilot when the PLAN target was the killed model
    /// (<c>:2968-2969</c>) — the port's plan proxy is the autopilot target.</item>
    /// </list></summary>
    private void OnTargetKilled(in ClientWorld.TargetKilledModel killed)
    {
        var world = _session.World;

        // 1. c_cmdCurrent comparison (trekigc.cpp:2936).
        QueuedCommandState? current;
        lock (_session.Gate) current = world.MyCurrentCommand;
        if (current is not { } cmd || cmd.TargetType != killed.ObjectType || cmd.TargetId != killed.ModelId)
            return;

        // 2. Source ship in a cluster (trekigc.cpp:2940-2941).
        ShipEntity? source;
        lock (_session.Gate)
        {
            if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)) return;
            source = own.ParentId >= 0 && world.Ships.TryGetValue(own.ParentId, out var parent) ? parent : own;
        }
        if (source is null || source.SectorId < 0) return;

        // 3. Similar-target mask (WinTrek.cpp:267-284). The only producer is a
        // ship kill, so the mask is ship + friendly-or-enemy; the relation is
        // computed from the victim's side (alliances included, as native does).
        // An unknown victim side defaults to the enemy mask (native's
        // GetSide() != my side leg).
        bool enemy = true;
        lock (_session.Gate)
        {
            if (world.Ships.TryGetValue(killed.ModelId, out var victim)
                && victim.SideId is { } vs && world.MySideId is { } mySide
                && (vs == mySide || world.AreAllied(mySide, vs)))
                enemy = false;
        }

        if (!TryOwnPose(out var pos, out var orient, out _)) return;

        // The plan-target capture must precede the commit: CommitTargetForPlayer
        // overwrites the autopilot target.
        bool planWasKilled = _autoPilotEngaged
            && _autoTarget is { } plan && plan.Kind == TargetKind.Ship && plan.Id == killed.ModelId;

        AutopilotTarget? picked = null;
        if (enemy)
        {
            // mask == (c_ttShip | c_ttEnemy | c_ttNearest) → abm = c_habmRescue,
            // with the miner/builder and plain retries (trekigc.cpp:2944-2960).
            picked = SelectFilteredTarget(pos, orient,
                new TargetFilterSpec(TargetRelation.Enemy, DataHullTypeIGC.HabmRescue), StepDirection.Nearest);
            picked ??= SelectFilteredTarget(pos, orient,
                new TargetFilterSpec(TargetRelation.Enemy, unchecked((short)(DataHullTypeIGC.HabmMiner | DataHullTypeIGC.HabmBuilder))),
                StepDirection.Nearest);
            picked ??= SelectFilteredTarget(pos, orient,
                new TargetFilterSpec(TargetRelation.Enemy), StepDirection.Nearest);
        }
        else
        {
            // Friendly mask (abm = 0): nearest similar friendly model.
            picked = SelectFilteredTarget(pos, orient,
                new TargetFilterSpec(TargetRelation.Friendly), StepDirection.Nearest);
        }

        // Native never re-picks the dead model (trekigc.cpp:2962-2963).
        if (picked is { } p && p.Kind == TargetKind.Ship && p.Id == killed.ModelId) picked = null;

        // 4. SetTarget(pmodelTarget, c_cidDefault) (trekigc.cpp:2966); a null
        // pick clears the slot (native SetTarget(NULL)).
        if (picked is { } sel)
        {
            CommitTargetForPlayer(sel);
        }
        else
        {
            _autoTarget = null;
            SendCurrentCommandToServer(null);
        }

        // 5. Re-engage the autopilot when the plan target was the killed model
        // (trekigc.cpp:2968-2969). With no new target the flight layer's own
        // "target lost" disengage handles the fallback.
        if (planWasKilled && picked is { } re)
            EngageAutopilotToward(re, "target killed:");
    }

    /// <summary>P623 — the live CLASS-FILTER target key (TK_Target{Friendly|Enemy}
    /// {Bomber|Fighter|Miner|Constructor|Lifepod}[Nearest|Prev],
    /// WinTrek.cpp:10162-10331). Builds the per-class spec from
    /// <see cref="ClassFilterSpec"/> and runs <see cref="SelectFilteredTargetForKey"/>
    /// in the command's step direction. <paramref name="noun"/> is the HUD no-match
    /// label ("enemy bomber", "friendly miner", …).</summary>
    private void SelectClassForKey(TargetRelation relation, TargetClass cls, string noun, StepDirection dir = StepDirection.Next)
        => SelectFilteredTargetForKey(ClassFilterSpec(relation, cls), noun, dir);

    /// <summary>P623 — the live MAJOR-BASE target key (TK_Target{Friendly|Enemy}
    /// MajorBase[Nearest|Prev], WinTrek.cpp:10202-10206 / 10266-10270): stations
    /// ANY cluster flagged c_sabmFlag, step-directed like the class keys.</summary>
    private void SelectMajorBaseForKey(TargetRelation relation, string noun, StepDirection dir = StepDirection.Next)
        => SelectFilteredTargetForKey(MajorBaseSpec(relation), noun, dir);

    // ═══════════════════════════════════════════════════════════════════
    // P623 — the flight-key dispatch for the class-filtered targeting family.
    //
    // Each native command is a triad (next / nearest / prev) sharing one key
    // family: plain = next (c_ttPrevious unset, Walk forward), Shift = nearest
    // (c_ttNearest), Ctrl = previous (c_ttPrevious) — TrekFindTarget
    // WinTrek.cpp:8963-8969, FindTarget common.cpp:505-510. The Shift/Ctrl
    // chord is read FIRST so a Shift+O press selects nearest (not also next —
    // the plain chord tolerates the extra Shift modifier, so both would match).
    // One shared edge latch per action prevents re-firing while held.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Per-action press edge latches for the P623 targeting keys (a
    /// single was-down bit per command so a held key never re-fires).</summary>
    private readonly ActionPressEdges _targetPressEdges = new();

    /// <summary>Edge-press predicate for one P623 targeting action: true only on
    /// the rising edge of the chord being down.</summary>
    private bool TargetEdgeDown(KeyboardState ks, string action)
    {
        return _targetPressEdges.Observe(action, ActionDown(ks, action));
    }

    /// <summary>Dispatch one class-filtered command TRIAD (next/nearest/prev
    /// actions) on its press edge, resolving the actual direction from which
    /// chord was held (Ctrl = prev > Shift = nearest > plain = next).</summary>
    private void HandleClassTriad(KeyboardState ks, TargetRelation rel, TargetClass cls, string noun,
        string actNext, string actNearest, string actPrev)
    {
        bool next = TargetEdgeDown(ks, actNext);
        bool nearest = TargetEdgeDown(ks, actNearest);
        bool previous = TargetEdgeDown(ks, actPrev);
        if (_targetKeysSuppressed || !(next || nearest || previous)) return;
        var dir = previous ? StepDirection.Previous : nearest ? StepDirection.Nearest : StepDirection.Next;
        SelectClassForKey(rel, cls, noun, dir);
    }

    /// <summary>Dispatch one major-base command TRIAD (native leaves these
    /// unbound by default; wired so a user bind works).</summary>
    private void HandleMajorBaseTriad(KeyboardState ks, TargetRelation rel, string noun,
        string actNext, string actNearest, string actPrev)
    {
        bool next = TargetEdgeDown(ks, actNext);
        bool nearest = TargetEdgeDown(ks, actNearest);
        bool previous = TargetEdgeDown(ks, actPrev);
        if (_targetKeysSuppressed || !(next || nearest || previous)) return;
        var dir = previous ? StepDirection.Previous : nearest ? StepDirection.Nearest : StepDirection.Next;
        SelectMajorBaseForKey(rel, noun, dir);
    }

    /// <summary>P623 — the full class-filtered targeting key block, called each
    /// flight frame beside the U/Z/T/G handlers. Every triad is edge-dispatched;
    /// unbound commands (fighter/constructor/majorbase, hostile) simply never
    /// match until the user binds them via F6.</summary>
    private bool _targetKeysSuppressed;
    private void HandleP623TargetingKeys(KeyboardState ks, bool composing)
    {
        _targetKeysSuppressed = composing;
        // Enemy ship classes (native defaults: O bomber, I miner).
        HandleClassTriad(ks, TargetRelation.Enemy, TargetClass.Bomber, "enemy bomber",
            "TargetEnemyBomber", "TargetEnemyBomberNearest", "TargetEnemyBomberPrev");
        HandleClassTriad(ks, TargetRelation.Enemy, TargetClass.Miner, "enemy miner",
            "TargetEnemyMiner", "TargetEnemyMinerNearest", "TargetEnemyMinerPrev");
        HandleClassTriad(ks, TargetRelation.Enemy, TargetClass.Fighter, "enemy fighter",
            "TargetEnemyFighter", "TargetEnemyFighterNearest", "TargetEnemyFighterPrev");
        HandleClassTriad(ks, TargetRelation.Enemy, TargetClass.Constructor, "enemy constructor",
            "TargetEnemyConstructor", "TargetEnemyConstructorNearest", "TargetEnemyConstructorPrev");

        // Friendly ship classes (native defaults: K bomber, H miner, J lifepod).
        HandleClassTriad(ks, TargetRelation.Friendly, TargetClass.Bomber, "friendly bomber",
            "TargetFriendlyBomber", "TargetFriendlyBomberNearest", "TargetFriendlyBomberPrev");
        HandleClassTriad(ks, TargetRelation.Friendly, TargetClass.Miner, "friendly miner",
            "TargetFriendlyMiner", "TargetFriendlyMinerNearest", "TargetFriendlyMinerPrev");
        HandleClassTriad(ks, TargetRelation.Friendly, TargetClass.Lifepod, "friendly lifepod",
            "TargetFriendlyLifepod", "TargetFriendlyLifepodNearest", "TargetFriendlyLifepodPrev");
        HandleClassTriad(ks, TargetRelation.Friendly, TargetClass.Fighter, "friendly fighter",
            "TargetFriendlyFighter", "TargetFriendlyFighterNearest", "TargetFriendlyFighterPrev");
        HandleClassTriad(ks, TargetRelation.Friendly, TargetClass.Constructor, "friendly constructor",
            "TargetFriendlyConstructor", "TargetFriendlyConstructorNearest", "TargetFriendlyConstructorPrev");

        // Major bases (any sector, c_sabmFlag) — native leaves unbound by default.
        HandleMajorBaseTriad(ks, TargetRelation.Enemy, "enemy major base",
            "TargetEnemyMajorBase", "TargetEnemyMajorBaseNearest", "TargetEnemyMajorBasePrev");
        HandleMajorBaseTriad(ks, TargetRelation.Friendly, "friendly major base",
            "TargetFriendlyMajorBase", "TargetFriendlyMajorBaseNearest", "TargetFriendlyMajorBasePrev");

        // TK_TargetHostile — native has NO default key; wired so a user bind works.
        if (TargetEdgeDown(ks, "TargetHostile") && !composing)
            SelectHostileTarget();
    }

    // ═══════════════════════════════════════════════════════════════════
    // P587 — the LIVE friendly/enemy base targeting keys, ported from native
    // TK_TargetFriendlyBase*/TK_TargetEnemyBase* (WinTrek.cpp:10194-10273).
    // Unlike the P415 sector-only base keys these hunt ANY sector (native
    // c_ttAnyCluster) and filter by the LOCAL hull's dockability (FriendlyBaseSpec
    // / EnemyBaseSpec). The "no base" line is shown only when no base exists in
    // ANY sector (native's single on-screen "no friendly base" message).
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// P587 — friendly/allied base nearest-then-cycle across all sectors, with
    /// the local hull's dockability filter (fighter → Land, capital → CapLand,
    /// lifepod → Rescue|RescueAny|Land + rescue probes). First press = nearest
    /// matching base anywhere; repeats cycle through matching bases and wrap.
    /// Sets the autopilot target so G (or right-click) can dock via P575.
    /// </summary>
    private void SelectFriendlyBaseForKey()
    {
        var world = _session.World;
        if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)) return;
        if (!TryOwnPose(out var pos, out var orient, out _)) return;
        short hullHabm;
        lock (_session.Gate)
            hullHabm = ShipRoleBits(own.HullId);
        var spec = FriendlyBaseSpec(hullHabm);
        List<CandidateObject> objs;
        lock (_session.Gate)
            objs = GatherBaseCandidates(world.Stations, world.Probes, StationRoleBits, ProbeRoleBits);
        var filtered = FilterCandidates(objs, world.MySideId, me, pos, spec);
        if (filtered.Count == 0)
        {
            _targetNoMatchNoun = "friendly base";
            _targetNoMatchUntil = _nowSeconds + TargetNoMatchDisplaySeconds;
            Console.WriteLine("[monogame] no friendly base anywhere");
            return;
        }
        var picked = StepCycle(filtered, _autoTarget);
        CommitTargetForPlayer(picked);
        _targetNoMatchNoun = null;
        Console.WriteLine($"[monogame] target friendly base: {picked.Name} (#{picked.Id})");
        RefreshWindowTitle();
    }

    /// <summary>
    /// P587 — enemy base nearest-then-cycle across all sectors (native
    /// c_ttAnyCluster, ~c_sabmPedestal). First press = nearest enemy base
    /// anywhere; repeats cycle through enemy bases and wrap.
    /// </summary>
    private void SelectEnemyBaseForKey()
    {
        var world = _session.World;
        if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)) return;
        if (!TryOwnPose(out var pos, out var orient, out _)) return;
        List<CandidateObject> objs;
        lock (_session.Gate)
            objs = GatherBaseCandidates(world.Stations, null, StationRoleBits, null);
        var filtered = FilterCandidates(objs, world.MySideId, me, pos, EnemyBaseSpec());
        if (filtered.Count == 0)
        {
            _targetNoMatchNoun = "enemy base";
            _targetNoMatchUntil = _nowSeconds + TargetNoMatchDisplaySeconds;
            Console.WriteLine("[monogame] no enemy base anywhere");
            return;
        }
        var picked = StepCycle(filtered, _autoTarget);
        CommitTargetForPlayer(picked);
        _targetNoMatchNoun = null;
        Console.WriteLine($"[monogame] target enemy base: {picked.Name} (#{picked.Id})");
        RefreshWindowTitle();
    }

    /// <summary>
    /// P425 — select the COMMANDEE: the same-side ship/drone the local player is
    /// commanding, stored in the separate <see cref="_commandee"/> slot (DISTINCT
    /// from the target reticle <see cref="_autoTarget"/> — a drone order needs
    /// two selections at once: who to command AND what to order them at). Rather
    /// than build a new candidate-gathering system, it reuses the exact same-side
    /// machinery the P415 friendly-ship keys use (<see cref="GatherSectorCandidates"/>
    /// + <see cref="FilterCandidates"/> + <see cref="StepCycle"/> with
    /// <see cref="TargetRelation.Friendly"/>, ships only), which already gathers
    /// and side-classifies every same-side ship. First press = nearest friendly
    /// ship; repeats cycle through same-side ships and wrap — the P415 shape.
    /// A friendly ship here may be an NPC drone (the interesting case) or another
    /// same-side peer; the specific ORDER issued later is gated on the commandee's
    /// role + the target's kind by <see cref="CommandOrderResolver"/>.
    /// </summary>
    private void SelectCommandee(bool cycle)
    {
        var world = _session.World;
        if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)) return;
        if (!TryOwnPose(out var pos, out var orient, out _)) return;
        List<CandidateObject> objs;
        lock (_session.Gate)
            objs = GatherSectorCandidates(world.Ships, world.Warps, world.Stations, world.Asteroids,
                                          me, own.SectorId, ShipRoleBits, StationRoleBits);
        var filtered = FilterCandidates(objs, world.MySideId, me, pos,
                                        new TargetFilterSpec(TargetRelation.Friendly));
        if (filtered.Count == 0)
        {
            _commandGroup.Clear();
            _commandee = null;
            Console.WriteLine("[monogame] commandee: no friendly ship to command in sector");
            return;
        }
        var picked = cycle ? StepCycle(filtered, _commandee) : filtered[0];
        _commandGroup.Clear();
        _commandee = picked;
        Console.WriteLine($"[monogame] commandee: {picked.Name} (ship #{picked.Id})");
    }

    /// <summary>Issue the role/target's default command through native command
    /// chat (WinTrek/console.cpp:1450-1504). The recipient acknowledges its own
    /// queued slot; the server validates drone capabilities and target legality.</summary>
    private void TryIssueOrderToSingleCommandee()
    {
        var world = _session.World;
        if (_commandee is not { } cmd)
        {
            Console.WriteLine("[monogame] issue order: no commandee selected (cycle a friendly ship with the commandee key)");
            return;
        }
        if (_autoTarget is not { } tgt)
        {
            Console.WriteLine("[monogame] issue order: no target selected (point the reticle / T-cycle an asteroid)");
            return;
        }

        // The commandee's hull role bits come from the same ShopCatalog the P415
        // keys read (HabmCapabilities) — the client-side mirror of the server's
        // c_habmBuilder gate, so an obviously-illegal order is refused HERE first.
        short roleBits;
        bool commandeeOnParent;
        bool commandeeIsLayer;
        lock (_session.Gate)
        {
            if (!world.Ships.TryGetValue(cmd.Id, out var ent))
            {
                Console.WriteLine($"[monogame] issue order: commandee {cmd.Name} (#{cmd.Id}) no longer known — reselect");
                return;
            }
            roleBits = ShipRoleBits(ent.HullId);
            commandeeOnParent = ent.ParentId != -1;   // native m_pshipParent != NULL
            commandeeIsLayer = ShipIsLayer(ent.HullId);
        }

        // P757 — the target profile for the full native GetDefaultOrder table
        // (Igc/shipIGC.h:2071-2161): side relationship + the target's
        // hull/station/probe capabilities, resolved from the same client-side
        // catalogues the rest of the commander UI reads.
        CommandTargetProfile profile;
        lock (_session.Gate) profile = BuildTargetProfile(world, tgt);
        var order = CommandOrderResolver.ResolveDefaultOrder(roleBits, commandeeIsLayer, commandeeOnParent, profile);
        if (order is not { } o)
        {
            Console.WriteLine($"[monogame] issue order: no applicable order for commandee {cmd.Name} (#{cmd.Id}) + target {tgt.Name} ({tgt.Kind}#{tgt.Id})");
            return;
        }

        // HUD-10 — a verb chosen on the native command bar overrides the
        // role/target default for this order (console.cpp:1276 SetVerb).
        short command = _commandVerb != 0 ? _commandVerb : o.CommandId;
        lock (_session.Gate)
        {
            _commanderBuildOrders.Cancel(cmd.Id);
            if (command == Allegiance.Core.Wire.OrderChange.CidBuild)
                command = _commanderBuildOrders.Begin(world, cmd.Id, o.ObjectId);
        }
        _session.SendChat("", Allegiance.Core.Sim.ChatTarget.Individual, oidRecipient: cmd.Id,
            commandId: command, otTarget: (short)o.ObjectType, oidTarget: o.ObjectId);
        Console.WriteLine($"[monogame] order: {OrderLabel(command)} at {tgt.Name} (#{tgt.Id}) issued to {cmd.Name} (#{cmd.Id}) (CHAT recipient={cmd.Id} commandID={command} {o.ObjectType}/{o.ObjectId})");
        _sound?.PlayAutopilotEngage();
    }

    /// <summary>P425 — a short human label for a <see cref="OrderChange"/> command
    /// id, for the issue-order console line.</summary>
    private static string OrderLabel(short commandId) => commandId switch
    {
        Allegiance.Core.Wire.OrderChange.CidBuild => "BUILD",
        Allegiance.Core.Wire.OrderChange.CidMine => "MINE",
        Allegiance.Core.Wire.OrderChange.CidAttack => "ATTACK",
        Allegiance.Core.Wire.OrderChange.CidDefend => "DEFEND",
        Allegiance.Core.Wire.OrderChange.CidGoto => "GOTO",
        Allegiance.Core.Wire.OrderChange.CidPickup => "PICKUP",
        _ => $"commandID={commandId}",
    };

    /// <summary>HUD-11 — the target half of the native default-order table
    /// (<see cref="CommandOrderResolver.ResolveDefaultOrder"/>): side relationship
    /// plus the target hull/station/probe capabilities. Call under the session
    /// gate; used by both the issue-order key (commandee) and the plain target
    /// pick's native <c>SetTarget(pmodel, c_cidDefault)</c> send (WinTrek.cpp:8830-8833).</summary>
    private CommandTargetProfile BuildTargetProfile(ClientWorld world, AutopilotTarget tgt)
    {
        short? side = tgt.Kind == TargetKind.Ship && world.Ships.TryGetValue(tgt.Id, out var shipTarget) ? shipTarget.SideId
            : tgt.Kind == TargetKind.Station && world.Stations.TryGetValue(tgt.Id, out var stationTarget) ? stationTarget.SideId
            : tgt.Kind == TargetKind.Probe && world.Probes.TryGetValue(tgt.Id, out var probeTarget) ? probeTarget.SideId
            : null;
        bool? friendly = side is { } other && world.MySideId is { } ownSide
            ? other == ownSide || world.AreAllied(ownSide, other)
            : null;
        short? targetHullAbm = null, stationSabm = null, probeEabm = null;
        if (tgt.Kind == TargetKind.Ship && world.Ships.TryGetValue(tgt.Id, out var shipT))
            targetHullAbm = ShipRoleBits(shipT.HullId);
        else if (tgt.Kind == TargetKind.Station && world.Stations.TryGetValue(tgt.Id, out var stT)
                 && _shopCatalog is { StationTypes: { } stypes } && stypes.TryGetValue(stT.StationTypeId, out var stype))
            stationSabm = stype.SabmCapabilities;
        else if (tgt.Kind == TargetKind.Probe && world.Probes.TryGetValue(tgt.Id, out var prT)
                 && _shopCatalog is { ProbeTypes: { } ptypes } && ptypes.TryGetValue(prT.ProbetypeId, out var ptype))
            probeEabm = ptype.EabmCapabilities;
        return new CommandTargetProfile(tgt.Kind, tgt.Id, friendly, targetHullAbm, stationSabm, probeEabm);
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

    /// <summary>P757 — is this hull a mine-LAYER drone (native pilot type
    /// <c>c_ptLayer</c>, Ship.PilotTypeLayer)? The client's EXPORT_SHIP only
    /// carries player ships, so the drone's pilot type is resolved through the
    /// ShopCatalog's DroneTypes table (hull → DataDroneTypeIGC.PilotType) —
    /// the same catalogue the role bits come from.</summary>
    private bool ShipIsLayer(short hullId)
    {
        if (_shopCatalog is not { DroneTypes: { } drones }) return false;
        foreach (var d in drones.Values)
            if (d.HullTypeId == hullId && d.PilotType == Allegiance.Core.Sim.Ship.PilotTypeLayer)
                return true;
        return false;
    }

    /// <summary>P415 — resolve a station TYPE id to its SabmCapabilities from
    /// the client's <see cref="ShopCatalog"/> (for the base / major-base
    /// filters; 0 = unknown).</summary>
    private short StationRoleBits(short stationTypeId)
    {
        if (_shopCatalog?.StationTypes is not { } sts) return 0;
        return sts.TryGetValue(stationTypeId, out var st) ? st.SabmCapabilities : (short)0;
    }

    /// <summary>P587 — resolve a probe TYPE id to its EabmCapabilities from the
    /// client's <see cref="ShopCatalog"/> (for the lifepod rescue-probe search;
    /// 0 = unknown). Note c_eabmRescue/c_eabmRescueAny share the SabmRescue/
    /// SabmRescueAny bit values (igc.h:781-782), so a probe's eabm bits slot
    /// directly into the same role-bit test as a station's sabm bits.</summary>
    private short ProbeRoleBits(short probeTypeId)
    {
        if (_shopCatalog?.ProbeTypes is not { } pts) return 0;
        return pts.TryGetValue(probeTypeId, out var p) ? p.EabmCapabilities : (short)0;
    }

    // ═══════════════════════════════════════════════════════════════════
    // P417 — the small leftover targeting keys: TargetSelf, TargetNothing,
    // TargetCenter (the standalone QoL screenshot TK_ScrnShot lives in
    // Capture/ScreenShot.cs + the Draw-end hook in Game1.Rendering.cs).
    //
    // Native semantics (WinTrek.cpp):
    //   TK_TargetSelf   (10440) SetTarget(GetShip(), c_cidNone) — select your
    //                             own ship, no command.
    //   TK_TargetNothing(10446) SetTarget(NULL, c_cidNone)     — clear the
    //                             target (and implicitly the autopilot, since
    //                             this port's DisengageAutopilot clears both).
    //   TK_TargetCenter (10127) FindTarget(c_ttFront|AllTypes|AllSides,
    //                             orientation) → SetTarget — the candidate
    //                             nearest the reticle/fwd vector. This port's
    //                             PickNearestTarget already ranks candidates
    //                             nearest-to-crosshair (screen-space DistSq),
    //                             so it is the direct reuse (no new ranking).
    //   TK_TargetCommand (10408) selects the independent accepted slot;
    //   production key handling is in Game1.Orders.cs (P675).
    //
    // P523 — NATIVE DEFAULT KEYS: the decoded Artwork/defaultinputmap.mdl
    // (triples (VK, modifier, TK id), P445's method) binds TK_TargetCenter (90)
    // to mouse button 1 and binds NEITHER TK_TargetSelf (100) NOR
    // TK_TargetNothing (103) to any default key. So native has NO default key
    // for TargetSelf — the port's earlier Y binding (P417) conflicted with the
    // P282 ready key (Y), letting the ready press leak into flight and select
    // your own ship. The port now leaves TargetSelf unbound (matching native);
    // the command is retained below for completeness. ; = TargetNothing and
    // ' = TargetCenter are this port's own free-key choices.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>P417 — TK_TargetSelf: select the local player's own ship as the
    /// T/G target (no command — native SetTarget(GetShip(), c_cidNone),
    /// WinTrek.cpp:10440-10443). Selecting self is a harmless no-op for the
    /// autopilot's steering (ResolveTargetPosition of your own live position
    /// keeps it in place), which matches native's intent of "bring the selection
    /// back to me".</summary>
    private void SelectSelfTarget()
    {
        var world = _session.World;
        short? me;
        lock (_session.Gate)
            me = world.MyShipId;
        if (me is not { } myId) return;
        string name = "you";
        lock (_session.Gate)
        {
            if (world.Ships.TryGetValue(myId, out var ent) &&
                !string.IsNullOrEmpty(ent.CharacterName))
                name = ent.CharacterName;
        }
        // HUD-12b — native TK_TargetSelf is SetTarget(GetShip(), c_cidNone)
        // (WinTrek.cpp:10440-10443): c_cidNone is NOT c_cidDefault, so native
        // never resolves a default order here. Keep the no-command payload.
        CommitTargetForPlayer(new AutopilotTarget(TargetKind.Ship, myId, name, UseDefault: false));
        Console.WriteLine($"[monogame] target: self ({name} #{myId})");
        RefreshWindowTitle();
    }

    /// <summary>P417 — TK_TargetNothing: clear the current target and disengage
    /// the autopilot (native SetTarget(NULL, c_cidNone), WinTrek.cpp:10446-10449).
    /// Mirrors however target-clear already works in this port — DisengageAutopilot
    /// is exactly the "clear target + engaged flag" path the G-key toggle-off and
    /// manual-input disengage already use.</summary>
    private void ClearTarget()
    {
        DisengageAutopilot("target nothing");
        // HUD-8b — native TK_TargetNothing is SetTarget(NULL, c_cidNone)
        // (WinTrek.cpp:10446-10449), which clears the c_cmdCurrent slot even though
        // autopilot disengage alone would not.
        SendCurrentCommandToServer(null);
    }

    /// <summary>P417 — TK_TargetCenter: select the candidate nearest the
    /// reticle/crosshair. Reuses the existing nearest-to-crosshair ranking
    /// (PickNearestTarget — the same screen-space DistSq ordering CollectTargetCandidates
    /// already applies), which is this port's analogue of native's
    /// c_ttFront nearest-target (WinTrek.cpp:10127-10151).</summary>
    private void SelectCentermostTarget()
    {
        if (!TryOwnPose(out var pos, out var orient, out _)) return;
        var picked = PickNearestTarget(pos, orient);
        if (picked is { } p)
        {
            CommitTargetForPlayer(p);
            Console.WriteLine($"[monogame] target center: {p.Name} ({p.Kind}#{p.Id})");
            RefreshWindowTitle();
        }
        else
            Console.WriteLine("[monogame] target center: nothing in view");
    }

    // ═══════════════════════════════════════════════════════════════════
    // P623 — TK_TargetHostile: target the thing that has done the most
    // damage to you recently.
    //
    // Native reads the ship's DamageTrack (WinTrek.cpp:10101-10124): a list of
    // DamageBuckets, one per damaging model, each holding the damage THAT model
    // inflicted, summed into rotating time slots so it decays over a rolling
    // window. The window is c_iDamageSlotCount(8) × c_dwDamageSlotInterval(5s)
    // = 40 s (igc.h:4926-4928); a bucket's total only reflects damage from the
    // last ~40 s, fading as its oldest slot is dropped (DamageTrack::SwitchSlots,
    // common.cpp:2572-2589; DamageBucket::ApplyDamage, igc.h:5024-5044).
    // TK_TargetHostile targets the bucket with the HIGHEST total; if that model
    // is already the current target and another bucket exists, it steps to the
    // second-highest; with no buckets at all it falls back to the nearest enemy
    // ship (WinTrek.cpp:10108-10123).
    //
    // This port's sim is server-authoritative, so the client does not simulate
    // its own ship's damage track. The server therefore attributes every hit on
    // a ship from a ship launcher to that victim's own peer via a targeted
    // VICTIM_DAMAGED wire message (attacker id + damage amount); the client
    // aggregates into the same 40 s decaying window below. That attribution is
    // the port's analogue of the native in-process DamageBucket population.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>P623 — the client's per-attacker rolling damage window, the port's
    /// analogue of native's DamageTrack (igc.h:4943-4970 / common.cpp:2556-2614).
    /// Pure + headless so it is unit-testable. Damage is bucketed into
    /// <see cref="SlotCount"/> rotating 5 s slots; totals reflect only the last
    /// ~40 s (native c_iDamageSlotCount×c_dwDamageSlotInterval, igc.h:4926-4928),
    /// and a bucket fades as its oldest slot drops out — mirroring
    /// DamageTrack::SwitchSlots dropping the oldest slot's contribution each
    /// slot rotation.</summary>
    internal sealed class HostileDamageTracker
    {
        /// <summary>Native <c>c_dwDamageSlotInterval = 5000</c> ms (igc.h:4927).</summary>
        public const long SlotMillis = 5000;
        /// <summary>Native <c>c_iDamageSlotCount = 8</c> (igc.h:4928) → a 40 s window.</summary>
        public const int SlotCount = 8;

        /// <summary>per-attacker: epoch → accumulated damage (only epochs within
        /// the last SlotCount-1 rotations are retained).</summary>
        private readonly Dictionary<short, Dictionary<long, float>> _byAttacker = new();
        // HUD-12 — per-attacker DamageBucket flash anchor (native
        // m_timeLastDamage, igc.h:5050-5057,5100). The native flash is a
        // 250 ms bright / 250 ms dim cycle while damage keeps arriving.
        private readonly Dictionary<short, long> _flashAnchor = new();
        // HUD-12b — reused scratch for Prune: Threat() runs per radar contact per
        // frame, and the old LINQ ToList() allocated a list (plus its iterator)
        // every call. The stale-epoch list is grown once and cleared in place.
        private readonly List<long> _staleEpochs = new();

        /// <summary>HUD-12b test/diagnostic view: how many attackers still own a
        /// flash anchor. Prune must drop the anchor with the empty bucket, or a
        /// HUD-12 leak kept one entry per attacker forever.</summary>
        internal int FlashAnchorCount => _flashAnchor.Count;

        /// <summary>Record <paramref name="damage"/> dealt to the local ship by
        /// <paramref name="attackerId"/> at server time <paramref name="nowMs"/>.
        /// Buckets into the current 5 s epoch and drops entries older than the
        /// 40 s window (native slot rotation).</summary>
        public void Record(short attackerId, float damage, long nowMs)
        {
            if (damage <= 0f) return;
            long epoch = nowMs / SlotMillis;
            if (!_byAttacker.TryGetValue(attackerId, out var slots))
            {
                slots = new Dictionary<long, float>();
                _byAttacker[attackerId] = slots;
            }
            slots[epoch] = slots.GetValueOrDefault(epoch) + damage;
            Prune(attackerId, epoch);
            // DamageBucket::ApplyDamage (igc.h:5024-5044): if we are not already
            // in the bright phase, push the anchor to now (first hit in a while)
            // or to the next full cycle (damage during the dim phase).
            long anchor = _flashAnchor.GetValueOrDefault(attackerId);
            if (nowMs >= anchor + FlashIntervalMs)
            {
                long timeReset = anchor + 2 * FlashIntervalMs;
                _flashAnchor[attackerId] = nowMs > timeReset ? nowMs : timeReset;
            }
        }

        /// <summary>Drop slots older than the rolling window for one attacker
        /// (native SwitchSlots dropping the oldest slot as the track advances).
        /// A slot at epoch e is retained while <c>e ≥ epoch−7</c> (8 slots span
        /// [epoch−7, epoch]); older than that it has left the 40 s window.</summary>
        private void Prune(short attackerId, long curEpoch)
        {
            if (!_byAttacker.TryGetValue(attackerId, out var slots)) return;
            long cutoff = curEpoch - (SlotCount - 1);
            _staleEpochs.Clear();
            foreach (var epoch in slots.Keys)
                if (epoch < cutoff) _staleEpochs.Add(epoch);
            foreach (var epoch in _staleEpochs) slots.Remove(epoch);
            if (slots.Count == 0)
            {
                _byAttacker.Remove(attackerId);
                // HUD-12b — the bucket is gone, so its flash anchor must go too
                // (native deletes the DamageBucket and with it m_timeLastDamage,
                // common.cpp:2572-2589 / igc.h:5050-5057).
                _flashAnchor.Remove(attackerId);
            }
        }

        /// <summary>Native <c>c_dwFlashInterval = 250</c> ms (igc.h:4926): the
        /// bright half of a DamageBucket's flash cycle.</summary>
        public const long FlashIntervalMs = 250;

        /// <summary>HUD-12 — native <c>pmodel-&gt;GetDamageBuckets()</c> presence
        /// (<c>radarimage.cpp:660-670</c>): a bucket exists while the model's
        /// in-window damage total is at least 0.5 (native deletes the bucket when
        /// <c>totalDamage() &lt; 0.5</c> after a slot rotation,
        /// <c>common.cpp:2572-2589</c>).</summary>
        public bool Threat(short attackerId, long nowMs)
        {
            Prune(attackerId, nowMs / SlotMillis);
            return Total(attackerId) >= 0.5f;
        }

        /// <summary>HUD-12 — native <c>DamageBucket::flash(now)</c>
        /// (<c>igc.h:5010-5014</c>): the bright 250 ms half of the bucket's
        /// cycle, driven by <see cref="_flashAnchor"/> exactly like
        /// <c>ApplyDamage</c>'s <c>m_timeLastDamage</c> update.</summary>
        public bool Flash(short attackerId, long nowMs)
        {
            long anchor = _flashAnchor.GetValueOrDefault(attackerId);
            return nowMs >= anchor && nowMs < anchor + FlashIntervalMs;
        }

        /// <summary>Total damage in the current window from <paramref name="attackerId"/>
        /// (the bucket's <c>totalDamage()</c>, igc.h:5004-5007).</summary>
        public float Total(short attackerId)
        {
            if (!_byAttacker.TryGetValue(attackerId, out var slots)) return 0f;
            float sum = 0f;
            foreach (var d in slots.Values) sum += d;
            return sum;
        }

        /// <summary>All damagers with positive in-window damage, as
        /// (attackerId, total). Empty when nobody is tracked.</summary>
        public IReadOnlyList<(short AttackerId, float Total)> Damagers(long nowMs)
        {
            // Prune every tracked attacker to its current window before reading.
            long epoch = nowMs / SlotMillis;
            foreach (var id in _byAttacker.Keys.ToList()) Prune(id, epoch);
            var list = _byAttacker
                .Where(kv => kv.Value.Values.Sum() > 0f)
                .Select(kv => (kv.Key, kv.Value.Values.Sum()))
                .OrderByDescending(x => x.Item2)
                .ToList();
            return list;
        }
    }

    /// <summary>P623 — the client's per-attacker damage window (populated from the
    /// server's VICTIM_DAMAGED attribution; see the region comment).</summary>
    private readonly HostileDamageTracker _hostileTracker = new();

    /// <summary>P623 — record that the local ship took <paramref name="damage"/> from
    /// ship <paramref name="attackerId"/> at client time <paramref name="nowSeconds"/>
    /// (fed from the VICTIM_DAMAGED wire message dispatch; the server stamps the
    /// authoritative tick, converted to ms here).</summary>
    internal void RecordHostileDamage(short attackerId, float damage, double nowSeconds)
        => _hostileTracker.Record(attackerId, damage, (long)(nowSeconds * 1000.0));

    /// <summary>P623 — drain this frame's VICTIM_DAMAGED(217) attributions from the
    /// world into the local ship's hostile damage window (called once per Update
    /// from the flight loop, beside the other wire drains).</summary>
    private void UpdateHostileTracking()
    {
        var world = _session.World;
        List<(short AttackerId, float Damage, long Ms)> items;
        lock (_session.Gate)
            items = world.TakeNewVictimDamages();
        foreach (var (attackerId, damage, _) in items)
            _hostileTracker.Record(attackerId, damage, (long)(_nowSeconds * 1000.0));
    }

    /// <summary>P623 — TK_TargetHostile: target the ship that has done the most
    /// damage to the local ship within the last ~40 s, stepping to the
    /// second-worst when the worst is already the current target, falling back to
    /// the nearest enemy ship when nothing has hurt us lately — native
    /// WinTrek.cpp:10101-10124 (bucket list sorted by <c>totalDamage</c>,
    /// common.cpp:2624-2660; fallback abm c_habmRescue|Miner|Builder,
    /// WinTrek.cpp:10122).</summary>
    private void SelectHostileTarget()
    {
        var world = _session.World;
        short me;
        lock (_session.Gate)
        {
            if (world.MyShipId is not { } m) return;
            me = m;
        }

        var damagers = _hostileTracker.Damagers((long)(_nowSeconds * 1000.0));
        if (damagers.Count > 0)
        {
            short top = damagers[0].AttackerId;
            // Step to the second-highest when the top is already the current
            // target and another bucket exists (native: n>1 && GetCommandTarget
            // == pmodel → pmodel = next bucket, WinTrek.cpp:10111-10115).
            if (damagers.Count > 1 && _autoTarget is { Kind: TargetKind.Ship } cur && cur.Id == top)
                top = damagers[1].AttackerId;

            lock (_session.Gate)
            {
                if (world.Ships.TryGetValue(top, out var ent) && !ent.IsNotVisible)
                {
                    string name = string.IsNullOrEmpty(ent.CharacterName) ? $"ship {top}" : ent.CharacterName;
                    CommitTargetForPlayer(new AutopilotTarget(TargetKind.Ship, top, name));
                    _targetNoMatchNoun = null;
                    Console.WriteLine($"[monogame] target hostile: {name} (#{top}) (in-window damage {damagers[0].Total:0.##})");
                    RefreshWindowTitle();
                    return;
                }
            }
            // The top damager is no longer a known/visible ship — fall through to
            // the native nearest-enemy fallback.
        }

        // Native fallback: no damage buckets → nearest enemy ship (WinTrek.cpp:10117-10123,
        // abm c_habmRescue|c_habmMiner|c_habmBuilder). Mirror the port's plain-enemy
        // U-key convention for the fallback target shape, then the "no hostile" line.
        if (!TryOwnPose(out var pos, out var orient, out _)) return;
        var filtered = SelectFilteredTarget(pos, orient,
            new TargetFilterSpec(TargetRelation.Enemy), StepDirection.Nearest);
        if (filtered is { } f)
        {
            CommitTargetForPlayer(f);
            _targetNoMatchNoun = null;
            Console.WriteLine($"[monogame] target hostile: no recent damager — nearest enemy {f.Name} (#{f.Id})");
            RefreshWindowTitle();
        }
        else
        {
            _targetNoMatchNoun = "enemy ship";
            _targetNoMatchUntil = _nowSeconds + TargetNoMatchDisplaySeconds;
            Console.WriteLine("[monogame] target hostile: no recent damager and no enemy ship in sector");
        }
    }
}
