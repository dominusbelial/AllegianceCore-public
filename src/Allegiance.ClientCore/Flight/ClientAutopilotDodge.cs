using System;
using System.Collections.Generic;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Flight;

/// <summary>
/// P524 — the shared <b>client-side feed</b> for the native dodge law
/// (<see cref="ShipDodgeLaw"/>, a byte-identical port of <c>Dodge</c>,
/// <c>src/Igc/common.cpp:1692-1790</c>). Given a player/bot mover flying toward
/// some goal inside a <see cref="ClientWorld"/>, this enumerates the
/// same-cluster asteroids, stations and known ships and — when a collision is
/// imminent — returns the dodge <em>goal</em> the mover should steer to instead.
///
/// <para><b>Why shared.</b> Native's universal navigation/autopilot steering is
/// <c>GotoPlan::SetControls</c> (<c>common.cpp:1830-2034</c>), whose very first
/// priority is <c>Dodge(...)</c> (<c>common.cpp:2000-2018</c>): an imminent
/// collision pre-empts the straight-line goto goal, and the goal itself is
/// handed to the dodge scan as <c>pmodelSkip</c> so the mover never dodges its
/// own destination. The server NPC autopilot (<see cref="Ship.TryComputeDodgeGoal"/>)
/// and the P489 client bots both already steer with this law. P524 brings the
/// SAME behaviour to the MonoGame player <c>G</c> autopilot, and in doing so
/// hoists the obstacle feed out of <c>BotManager</c> (and the P489 test's
/// private copy) into ONE canonical place — this type — so the bots and the
/// player autopilot literally share the same feed instead of maintaining three
/// near-identical implementations.</para>
///
/// <para><b>Contract.</b> The mover's own position/velocity/hull are supplied
/// by the caller (the caller has the freshest pose — e.g. the MonoGame P508
/// local-sim pose, or a bot's own <c>ClientWorld</c>), so this is a pure
/// function of the caller's snapshot plus the shared world. The world's
/// <c>Ships/Asteroids/Stations</c> collections are not internally synchronized,
/// so call this under the caller's world gate lock (the same discipline as
/// every other reader of those collections).</para>
/// </summary>
public static class ClientAutopilotDodge
{
    /// <summary>Dodge goal magnitude (how far away the escape point is placed).
    /// Matches the server's <see cref="Ship.DodgeGoalDistance"/> (520) and the
    /// MCP feed the P489 bots use (native's <c>Dodge</c> default
    /// <c>common.cpp:1759</c>). Only the direction matters; the magnitude is how
    /// far the escape point sits from the mover.</summary>
    public const float DodgeGoalDistance = 520f;

    /// <summary>Imminence horizon for a degenerate non-positive acceleration
    /// (native's acceleration-based gate would divide by zero) — the server's
    /// <see cref="Ship.DodgeFallbackTMax"/> (3).</summary>
    public const float FallbackTMax = 3f;

    /// <summary>Fallback ship hull length when a ship's hull type can't be
    /// resolved (≈ a small fighter, 30 u), giving the bot feed's 15 u default
    /// ship radius (<c>0.5·Length</c>).</summary>
    private const float UnknownHullLength = 30f;

    /// <summary>Fallback station radius when the station TYPE can't be resolved
    /// (≈ a small outpost), matching the MCP feed's 150 u fallback.</summary>
    private const float UnknownStationRadius = 150f;

    /// <summary>The mover's own resolved hull stats. The dodge mover's combined
    /// radius is <c>0.5·<see cref="Length"/> + 1</c> (native
    /// <c>GetRadius()+1</c>, <c>common.cpp:1705-1706</c>) and its acceleration
    /// is <c><see cref="Thrust"/>/<see cref="Mass"/></c> (<c>common.cpp:1707</c>).</summary>
    public readonly record struct MoverHull(float Length, float Thrust, float Mass);

    /// <summary>
    /// The navigation goal to skip — native's <c>pmodelSkip</c>
    /// (<c>common.cpp:2003</c>, set to the goto target by
    /// <c>GetGotoPosition</c>, <c>common.cpp:1291/1362/1449/1464/1501/1522/1560</c>).
    /// Normally exactly ONE of the three ids is set (the current autopilot
    /// target — a ship, a station being flown to, or an asteroid being mined);
    /// the others default to null. A <c>Warp</c> target needs no skip (warps are
    /// not dodge obstacles). Passing <c>null</c> (no goal) preserves the P489
    /// bot behaviour exactly.
    /// </summary>
    public readonly record struct SkipGoal(short? ShipId = null, short? StationId = null, short? AsteroidId = null);

    /// <summary>
    /// Compute the dodge goal for <paramref name="myShipId"/> against the
    /// obstacles in its own cluster (asteroids, stations — unless
    /// <paramref name="dodgeStations"/> is false — and known same-side ships),
    /// or return <c>false</c> when nothing is imminent — in which case the
    /// caller flies its straight-line goal unchanged (the exact
    /// <c>GotoPlan::SetControls</c> "first priority ... dodge"
    /// <c>common.cpp:2000-2018</c> contract).
    /// </summary>
    /// <param name="world">The mover's <see cref="ClientWorld"/> (call under its
    /// gate lock).</param>
    /// <param name="myShipId">The mover's own ship id (excluded from the set).</param>
    /// <param name="mySideId">The mover's side; different-side ships are marked
    /// <see cref="ShipDodgeLaw.Obstacle.Ignore"/> (native's same-side-only
    /// "trucker rules of road", <c>common.cpp:1679-1680</c>).</param>
    /// <param name="myPos">The mover's current world position (freshest pose).</param>
    /// <param name="myVel">The mover's current world velocity.</param>
    /// <param name="hull">The mover's own hull stats (<see cref="MoverHull"/>).</param>
    /// <param name="dodgeStations">False for a board-capable hull that must fly
    /// into the station it is capturing rather than evade it (P497); otherwise
    /// true so the common <c>G</c> autopilot dodges the bases it is not heading
    /// for.</param>
    /// <param name="hullLengthById">Resolves ANOTHER ship's hull id to its hull
    /// <c>Length</c> (for that ship's dodge radius), or null when unresolvable
    /// (falls back to <see cref="UnknownHullLength"/>).</param>
    /// <param name="stationRadiusByType">Resolves a station type id to its
    /// collision <c>Radius</c>, or null when unresolvable / non-positive (falls
    /// back to <see cref="UnknownStationRadius"/>).</param>
    /// <param name="skipGoal">The navigation goal to ignore
    /// (<see cref="SkipGoal"/>), or null for none.</param>
    /// <param name="goal">Receives the dodge goal on success.</param>
    /// <param name="dodgeGoalDistance">Dodge goal magnitude (default
    /// <see cref="DodgeGoalDistance"/>).</param>
    /// <param name="fallbackTMax">Degenerate-acceleration imminence horizon
    /// (default <see cref="FallbackTMax"/>).</param>
    public static bool TryComputeDodgeGoal(
        ClientWorld world,
        short myShipId, short mySideId,
        in Vector3 myPos, in Vector3 myVel,
        in MoverHull hull,
        bool dodgeStations,
        Func<short, float?> hullLengthById,
        Func<short, float?> stationRadiusByType,
        SkipGoal? skipGoal,
        out Vector3 goal,
        float dodgeGoalDistance = DodgeGoalDistance,
        float fallbackTMax = FallbackTMax)
    {
        goal = default;
        if (!world.Ships.TryGetValue(myShipId, out var me)) return false;
        short cur = me.SectorId;                       // the mover's current cluster
        if (cur < 0) return false;

        float radiusPlusOne = 0.5f * hull.Length + 1f; // GetRadius()+1 (common.cpp:1705-1706)
        float accel = hull.Mass > 0f ? hull.Thrust / hull.Mass : 0f;   // Thrust/Mass (:1707)
        var mover = new ShipDodgeLaw.Mover(myPos, myVel, radiusPlusOne, accel);
        return ShipDodgeLaw.TryComputeDodgeGoal(
            mover,
            EnumerateObstacles(world, cur, mySideId, myShipId, dodgeStations,
                hullLengthById, stationRadiusByType, skipGoal),
            dodgeGoalDistance, fallbackTMax, out goal);
    }

    /// <summary>The candidate obstacles in the mover's cluster — same-cluster
    /// asteroids, stations and known ships — skipping self, the current
    /// navigation goal (<see cref="SkipGoal"/>, native <c>pmodelSkip</c>) and
    /// different-side ships. Static rocks/stations carry zero velocity (their
    /// relative motion is just −our velocity); ships carry their live pose.
    /// Evaluated in the same order the server NPC autopilot and the P489 bots
    /// scan, so the shared law's strict <c>t &lt; tCollide</c> winner selection
    /// behaves identically across every consumer.</summary>
    private static IEnumerable<ShipDodgeLaw.Obstacle> EnumerateObstacles(
        ClientWorld world, short cur, short mySideId, short myShipId, bool dodgeStations,
        Func<short, float?> hullLengthById, Func<short, float?> stationRadiusByType,
        SkipGoal? skipGoal)
    {
        // (1) Asteroids — every rock in the cluster, all static.
        foreach (var (id, a) in world.Asteroids)
        {
            if (skipGoal is { AsteroidId: var skipA } && skipA == id) continue;   // pmodelSkip
            if (a.ClusterId != cur || a.Position is not { Length: 3 } p) continue;
            yield return new ShipDodgeLaw.Obstacle(new Vector3(p[0], p[1], p[2]), Vector3.Zero, a.Radius, false);
        }

        // (2) Stations — unless a board-capable hull must fly INTO one (P497).
        if (dodgeStations)
        {
            foreach (var (id, st) in world.Stations)
            {
                if (skipGoal is { StationId: var skipS } && skipS == id) continue;   // pmodelSkip — don't dodge the station you're flying to
                if (st.ClusterId != cur || st.Position is not { Length: 3 } p) continue;
                float r = stationRadiusByType(st.StationTypeId) ?? UnknownStationRadius;
                yield return new ShipDodgeLaw.Obstacle(new Vector3(p[0], p[1], p[2]), Vector3.Zero, r, false);
            }
        }

        // (3) Known ships — same-cluster, same-side only (native's "we're
        // heavier" rule); self and the goal are skipped.
        foreach (var (id, s) in world.Ships)
        {
            if (skipGoal is { ShipId: var skipSh } && skipSh == id) continue;   // pmodelSkip
            if (id == myShipId || s.Latest is not { } lu) continue;             // never self
            if (s.SectorId != cur) continue;
            bool enemy = s.SideId != mySideId;                                  // only dodge same-side ships
            float sr = 0.5f * (hullLengthById(s.HullId) ?? UnknownHullLength);
            yield return new ShipDodgeLaw.Obstacle(lu.Position, lu.Velocity, sr, enemy);
        }
    }
}
