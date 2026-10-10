namespace Allegiance.Core.Rendering;

using System.Collections.Generic;
using System.Text.Json;
using Allegiance.Core.Sim;

/// <summary>
/// P592 — configuration for the <b>giant sky objects</b>, loaded from
/// <c>giant-objects.json</c> (a small file at the artwork root: cluster id →
/// image, angle, direction, brightness) so a given sector can own a giant
/// (e.g. Terra's own face-on Milky Way, Alderan's Andromeda) and another
/// sector can gain one later without code. Parsed framework-neutrally (JSON,
/// no XNA) so the file can be validated headlessly.
///
/// Toggle: <c>--giant-objects off</c> (ON by default in the black sky).
/// </summary>
public sealed class GiantObjectConfig
{
    /// <summary>Default angular span (degrees across) of a giant patch when the
    /// object entry omits <c>angle_deg</c> — roughly a quarter of the visible
    /// dome, "a large object that spans across half the sky" (Domingo). The
    /// value is a named constant so it is easy to tune in the real window.</summary>
    public const float DefaultSpanDeg = 90f;

    /// <summary>One configured giant object.</summary>
    public sealed class GiantSpec
    {
        public int ClusterId { get; init; }
        public string Sector { get; init; } = "";
        public string Image { get; init; } = "";
        public float AngleDeg { get; init; } = DefaultSpanDeg;
        public NativeSkyMath.Vector3Unit Direction { get; init; }
        public float Brightness { get; init; } = 0.85f;
        public float RotationDeg { get; init; } = 0f;
        public string Credit { get; init; } = "";
    }

    /// <summary>The parsed specs, indexed by cluster id.</summary>
    public IReadOnlyDictionary<int, GiantSpec> ByCluster { get; }

    /// <summary>A config with no objects (used before the file loads).</summary>
    public static GiantObjectConfig Empty() => new(new Dictionary<int, GiantSpec>());

    private GiantObjectConfig(IReadOnlyDictionary<int, GiantSpec> byCluster)
        => ByCluster = byCluster;

    /// <summary>Parse a <c>giant-objects.json</c> document (UTF-8 text). On any
    /// structural error throws <see cref="JsonException"/> so a malformed file
    /// fails loudly rather than silently losing a sector's giant.</summary>
    public static GiantObjectConfig Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var map = new Dictionary<int, GiantSpec>();

        // Optional global default angle, overridable per object.
        float globalAngle = DefaultSpanDeg;
        if (root.TryGetProperty("span_deg", out var sa) && sa.ValueKind == JsonValueKind.Number)
            globalAngle = sa.GetSingle();

        if (root.TryGetProperty("objects", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in arr.EnumerateArray())
            {
                int id = o.GetProperty("cluster_id").GetInt32();
                var spec = new GiantSpec
                {
                    ClusterId = id,
                    Sector = Str(o, "sector"),
                    Image = Str(o, "image"),
                    AngleDeg = Num(o, "angle_deg", globalAngle),
                    Brightness = Math.Clamp(Num(o, "brightness", 0.85f), 0f, 1f),
                    RotationDeg = Num(o, "rot_deg", 0f),
                    Direction = Vec(o.GetProperty("direction")),
                    Credit = Str(o, "credit"),
                };
                if (spec.Image.Length == 0)
                    throw new JsonException($"giant-objects.json: cluster {id} has no 'image'");
                map[id] = spec;
            }
        }
        return new GiantObjectConfig(map);
    }

    /// <summary>Resolve the FINAL placement direction for a cluster, enforcing
    /// the two physical placement rules from the task: keep the giant clear of
    /// the sector's sun (so the lens flare doesn't wash it out) and clear of the
    /// planet. The json direction is the nominal FIXED/seeded direction; this
    /// nudges it only if it happens to collide with the live sector data
    /// (belt-and-suspenders for whatever the real map supplies). Pure and
    /// deterministic for a given (nominal, light, planet).</summary>
    /// <param name="spec">The configured spec (its <see cref="GiantSpec.Direction"/>
    /// is the nominal direction).</param>
    /// <param name="towardLight">Normalised direction toward the sector's sun, or
    /// null when the sector has no light.</param>
    /// <param name="planetDir">Unit direction of the sector's planet, or null.</param>
    /// <param name="minClearDeg">Minimum angular separation required from the sun
    /// and the planet.</param>
    public static NativeSkyMath.Vector3Unit ResolveDirection(
        GiantSpec spec, NativeSkyMath.Vector3Unit? towardLight,
        NativeSkyMath.Vector3Unit? planetDir, float minClearDeg = 22f)
    {
        var dir = Normalize(spec.Direction);
        float minRad = MinClearRad(minClearDeg);
        if (towardLight is { } light)
            dir = ClearOf(dir, Normalize(light), minRad);
        if (planetDir is { } planet)
            dir = ClearOf(dir, Normalize(planet), minRad);
        return dir;
    }

    private static NativeSkyMath.Vector3Unit ClearOf(NativeSkyMath.Vector3Unit dir,
        NativeSkyMath.Vector3Unit obstacle, float minRad)
    {
        float dot = Math.Clamp(dir.X * obstacle.X + dir.Y * obstacle.Y + dir.Z * obstacle.Z, -1f, 1f);
        float ang = MathF.Acos(dot);
        if (ang >= minRad) return dir;
        // Too close: rotate dir away from the obstacle around an axis
        // perpendicular to both, by enough to reach minClear (+a small margin).
        var axis = CrossProduct(dir, obstacle);
        float len = MathF.Sqrt(axis.X * axis.X + axis.Y * axis.Y + axis.Z * axis.Z);
        if (len < 1e-6f)
        {
            // dir and obstacle are parallel: pick an arbitrary perpendicular axis.
            axis = MathF.Abs(dir.Y) > 0.9f
                ? new NativeSkyMath.Vector3Unit(1f, 0f, 0f)
                : new NativeSkyMath.Vector3Unit(0f, 1f, 0f);
        }
        else
        {
            axis = new NativeSkyMath.Vector3Unit(axis.X / len, axis.Y / len, axis.Z / len);
        }
        // Rotate by (minRad + margin − ang); cross(dir,obstacle) sign gives the
        // direction that moves AWAY from the obstacle.
        float rot = minRad - ang + 0.04f;
        return Rotate(dir, axis, rot);
    }

    private static NativeSkyMath.Vector3Unit Rotate(NativeSkyMath.Vector3Unit v,
        NativeSkyMath.Vector3Unit axis, float angleRad)
    {
        float c = MathF.Cos(angleRad), s = MathF.Sin(angleRad);
        float cx = axis.X, cy = axis.Y, cz = axis.Z;
        float crossX = cy * v.Z - cz * v.Y;
        float crossY = cz * v.X - cx * v.Z;
        float crossZ = cx * v.Y - cy * v.X;
        float dot = cx * v.X + cy * v.Y + cz * v.Z;
        return new NativeSkyMath.Vector3Unit(
            v.X * c + crossX * s + cx * dot * (1 - c),
            v.Y * c + crossY * s + cy * dot * (1 - c),
            v.Z * c + crossZ * s + cz * dot * (1 - c));
    }

    private static NativeSkyMath.Vector3Unit CrossProduct(NativeSkyMath.Vector3Unit a, NativeSkyMath.Vector3Unit b)
        => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static float MinClearRad(float deg) => deg * MathF.PI / 180f;

    private static string Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";

    private static float Num(JsonElement o, string name, float dflt)
        => o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetSingle() : dflt;

    private static NativeSkyMath.Vector3Unit Vec(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 3)
            return Normalize(new NativeSkyMath.Vector3Unit(
                e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle()));
        return new NativeSkyMath.Vector3Unit(0f, 0f, 1f);
    }

    private static NativeSkyMath.Vector3Unit Normalize(NativeSkyMath.Vector3Unit v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (len < 1e-6f || float.IsNaN(len)) return new NativeSkyMath.Vector3Unit(0f, 0f, 1f);
        return new NativeSkyMath.Vector3Unit(v.X / len, v.Y / len, v.Z / len);
    }
}
