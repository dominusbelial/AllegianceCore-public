using System.Text.Json;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P418 — translate a live <see cref="ClientWorld"/> snapshot into the exact
/// JSON frame shapes <c>vizfight</c>'s spectator frontend (<c>wwwroot/index.html</c>)
/// already understands, so the same unchanged web page can render a world that
/// came from a real networked <see cref="Session.ConnectionSession"/> instead of
/// an in-process <see cref="Allegiance.Server.Server"/>.
///
/// This is the bridge for the new <c>vizfight attach &lt;host&gt; &lt;port&gt;</c>
/// mode: a pure spectator peer that seats a ship on a real server and republishes
/// what it actually receives on the wire. The three frames mirror the three
/// message kinds the frontend renders:
///   <list type="bullet">
///   <item><c>tick</c> — per-tick ship + non-ship-model state (the P44 ships /
///   P204 models schema, byte-compatible with <c>vizfight.TickFrame</c>).</item>
///   <item><c>kill</c> — a real <c>KILL_SHIP(105)</c> decoded from the wire
///   (the P44 kill message: victim/killer/simTime).</item>
///   <item><c>sector</c> — one cluster's static geometry (warps/stations/
///   asteroids), the same shape <c>vizfight.SectorMessageJson</c> emits.</item>
///   </list>
///
/// The class is deliberately pure and web-free (just <c>System.Text.Json</c>,
/// which the shared framework ships) so the mapping is unit-testable from
/// <c>Allegiance.ClientCore.Tests</c> without pulling in the web tool. It does
/// no I/O, no locking and no state — the caller holds the
/// <see cref="Session.ConnectionSession.Gate"/> lock while gathering its
/// snapshot and passes the world in.
/// </summary>
public static class SpectatorFrame
{
    /// <summary>Fallback hull label when the caller's hull-id→name map has no
    /// entry for a ship (a core the map didn't cover). Keeps the frame
    /// renderable rather than crashing.</summary>
    public const string UnknownHullPrefix = "hull";

    /// <summary>
    /// Build the per-tick frame: <c>{ type="tick", simTime, ships:[...],
    /// models:[...] }</c>.
    /// </summary>
    /// <param name="world">The live world (read under the session gate).</param>
    /// <param name="hullName">Resolves a ship's HullId to its display name
    /// (e.g. from a decoded static core). Unknown ids fall back to
    /// <see cref="UnknownHullPrefix"/><c>HullId</c>.</param>
    /// <param name="simTime">A monotonic seconds value to tag the frame with
    /// (the wire has no shared epoch with the client; the caller passes its
    /// own clock, exactly as <c>vizfight</c> tags frames with <c>SimNow</c>).</param>
    /// <param name="modelCounter">A running counter so the id-less transient
    /// entities (projectiles/chaff) get stable-enough ids for the frontend's
    /// per-entry keys. Pass a persistent counter; the value is updated in place
    /// and returned.</param>
    public static string BuildTickFrame(
        ClientWorld world,
        Func<short, string> hullName,
        double simTime,
        ref long modelCounter)
    {
        var ships = new object[world.Ships.Count];
        int si = 0;
        foreach (var e in world.Ships.Values)
        {
            var latest = e.Latest;
            ships[si++] = new
            {
                id = e.ShipId,
                hull = ResolveHull(hullName, e.HullId, e.ShipId),
                side = e.SideId,
                pos = Vec(latest?.Position),
                fwd = Vec(latest is null ? null : Orientation(latest)),
                hp = (double)(latest?.HullFraction ?? 0f),
                alive = !e.IsDead,
            };
        }

        var models = new List<object>();
        foreach (var p in world.Projectiles)
            models.Add(new { id = ++modelCounter, kind = "projectile", pos = Vec(p.Position), fwd = Vec(p.Forward), side = (short)-1 });
        foreach (var m in world.Missiles.Values)
            models.Add(new { id = m.MissileId, kind = "missile", pos = Vec(m.Position), fwd = Vec(m.Forward), side = (short)-1 });
        foreach (var m in world.Mines.Values)
            models.Add(new { id = m.MineId, kind = "mine", pos = Vec(m.Position), side = m.SideId });
        foreach (var c in world.Chaff)
            models.Add(new { id = ++modelCounter, kind = "chaff", pos = Vec(c.P0) });
        foreach (var t in world.Treasures.Values)
            models.Add(new { id = t.TreasureId, kind = "treasure", pos = Vec(t.Position), code = t.Code.ToString(), side = t.PayloadId });

        return JsonSerializer.Serialize(new
        {
            type = "tick",
            simTime = Round2(simTime),
            ships,
            models,
        }, JsonOpts);
    }

    /// <summary>Build the P44 kill message from a real <c>KILL_SHIP(105)</c>
    /// decoded off the wire: <c>{ type="kill", victim, killer, simTime }</c>.
    /// Same shape as <c>vizfight</c>'s kill broadcast.</summary>
    public static string BuildKillFrame(KillShip kill, double simTime) =>
        JsonSerializer.Serialize(new
        {
            type = "kill",
            victim = kill.ShipId,
            killer = kill.IdCredit,
            simTime = Round2(simTime),
        }, JsonOpts);

    /// <summary>
    /// Build a one-time <c>sector</c> geometry message for one cluster id from
    /// the world's EXPORT tables, matching <c>vizfight.SectorMessageJson</c>
    /// (warps/stations/asteroids with position + radius). Returns null when the
    /// world has no stations/warps/asteroids for that cluster (nothing to show).
    /// </summary>
    public static string? BuildSectorFrame(ClientWorld world, short clusterId, Func<short, string> clusterName)
    {
        var warps = world.Warps.Values
            .Where(w => w.ClusterId == clusterId)
            .Select(w => new { id = w.WarpId, pos = FloatArr(w.Position), radius = (float)w.Radius, destSector = w.DestinationId })
            .ToArray();
        var stations = world.Stations.Values
            .Where(s => s.ClusterId == clusterId)
            .Select(s => new { id = s.StationId, pos = FloatArr(s.Position) })
            .ToArray();
        var asteroids = world.Asteroids.Values
            .Where(a => a.ClusterId == clusterId)
            .Select(a => new { id = a.AsteroidId, pos = FloatArr(a.Position), radius = (float)a.Radius, oreFraction = (float)0 })
            .ToArray();

        // A cluster with nothing at all shouldn't be announced; emit only when
        // there is at least one piece of geometry to render.
        if (warps.Length == 0 && stations.Length == 0 && asteroids.Length == 0)
            return null;

        return JsonSerializer.Serialize(new
        {
            type = "sector",
            id = clusterId,
            name = clusterName(clusterId),
            warps,
            stations,
            asteroids,
        }, JsonOpts);
    }

    private static string ResolveHull(Func<short, string> hullName, short hullId, short shipId)
    {
        string n = hullName(hullId);
        return string.IsNullOrEmpty(n) ? $"{UnknownHullPrefix}{hullId}" : n;
    }

    private static Vector3? Orientation(ServerShipUpdate u)
    {
        try { return u.Orientation.GetForward(); }
        catch { return null; }
    }

    private static float[] Vec(Vector3? v) => v is { } p ? [Round2(p.X), Round2(p.Y), Round2(p.Z)] : [0f, 0f, 0f];
    private static float[] FloatArr(float[]? v) => v is { Length: 3 } p ? [p[0], p[1], p[2]] : [0f, 0f, 0f];

    private static float Round2(float v) => MathF.Round(v, 2) + 0f;
    private static float Round2(double v) => (float)Math.Round(v, 2) + 0f;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
}
