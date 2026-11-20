using Allegiance.Core;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Local-skirmish spectator travel between sectors (port addition: native's
/// spectator moves by starmap pick only). Pure and device-free so the rules are
/// unit-testable:
/// <list type="bullet">
/// <item><see cref="TryCrossAleph"/> — the free camera flown into an aleph comes
/// out of its destination aleph in the linked sector, like a ship.</item>
/// <item><see cref="CycleSector"/> — the next/previous sector by id.</item>
/// <item><see cref="ArrivalView"/> — where the camera appears when a sector is
/// picked directly: looking at the sector's first station, else its first
/// aleph, else the sector origin.</item>
/// </list>
/// Sim and XNA world coordinates are identical (PoseConverter.ToXna).
/// </summary>
public static class SpectatorSectorTravel
{
    /// <summary>The camera enters an aleph within its data radius, never less
    /// than this (a radius-0 aleph would otherwise be untouchable).</summary>
    public const float MinTriggerRadius = 60f;

    /// <summary>Exit distance beyond the destination aleph's radius, so the
    /// camera is clear of it and cannot bounce straight back.</summary>
    public const float ExitClearance = 300f;

    /// <summary>Arrival viewpoint distance from the framed object.</summary>
    public const float ArrivalDistance = 2500f;

    public readonly record struct Arrival(short SectorId, Vector3 Eye, Vector3 Forward);

    public static Vector3 Vec(float[] a) => new(a[0], a[1], a[2]);

    /// <summary>If <paramref name="eye"/> is inside an aleph of
    /// <paramref name="sector"/>, the destination sector and the exit pose just
    /// outside the destination aleph, facing out along its forward.</summary>
    public static bool TryCrossAleph(Vector3 eye, short sector, IReadOnlyDictionary<short, DataWarpIGC> warps, out Arrival arrival)
    {
        arrival = default;
        foreach (var warp in warps.Values)
        {
            if (warp.ClusterId != sector) continue;
            float trigger = MathF.Max(warp.Radius, MinTriggerRadius);
            if (Vector3.DistanceSquared(eye, Vec(warp.Position)) > trigger * trigger) continue;
            if (!warps.TryGetValue(warp.DestinationId, out var exit) || exit.ClusterId == sector) continue;
            var forward = Vec(exit.Forward);
            forward = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : Vector3.UnitZ;
            float clear = MathF.Max(exit.Radius, MinTriggerRadius) + ExitClearance;
            arrival = new Arrival(exit.ClusterId, Vec(exit.Position) + forward * clear, forward);
            return true;
        }
        return false;
    }

    /// <summary>The sector after (<paramref name="direction"/> &gt; 0) or before
    /// <paramref name="current"/> in id order, wrapping; the first/last when
    /// <paramref name="current"/> is not in the list; -1 when there are none.</summary>
    public static short CycleSector(IEnumerable<short> sectors, short current, int direction)
    {
        var ids = sectors.Distinct().Order().ToArray();
        if (ids.Length == 0) return -1;
        int index = Array.IndexOf(ids, current);
        if (index < 0) return direction < 0 ? ids[^1] : ids[0];
        return ids[((index + Math.Sign(direction)) % ids.Length + ids.Length) % ids.Length];
    }

    /// <summary>Where the camera appears when <paramref name="sector"/> is picked:
    /// above and behind the first station (by id), else the first aleph, else
    /// the sector origin, looking at it.</summary>
    public static Arrival ArrivalView(short sector, IReadOnlyDictionary<short, DataStationIGC> stations, IReadOnlyDictionary<short, DataWarpIGC> warps)
    {
        Vector3 target = Vector3.Zero;
        var station = stations.Values.Where(s => s.ClusterId == sector).OrderBy(s => s.StationId).Select(s => (DataStationIGC?)s).FirstOrDefault();
        if (station is { } st) target = Vec(st.Position);
        else if (warps.Where(w => w.Value.ClusterId == sector).OrderBy(w => w.Key).Select(w => (DataWarpIGC?)w.Value).FirstOrDefault() is { } wp)
            target = Vec(wp.Position);
        var offset = Vector3.Normalize(new Vector3(0f, 0.35f, -1f)) * ArrivalDistance;
        var eye = target + offset;
        return new Arrival(sector, eye, Vector3.Normalize(target - eye));
    }

    /// <summary>Free-cam yaw/pitch for a forward vector (inverse of
    /// <see cref="FreeCam.Basis"/>).</summary>
    public static (float Yaw, float Pitch) YawPitch(Vector3 forward)
    {
        forward = Vector3.Normalize(forward);
        float pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));
        float limit = MathF.PI / 2f - 0.015f;
        return (MathF.Atan2(forward.X, forward.Z), Math.Clamp(pitch, -limit, limit));
    }
}
