using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Microsoft.Xna.Framework;
using Vector3 = Microsoft.Xna.Framework.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>The actual preview transform, in game hull units (not raw mesh units).</summary>
public readonly record struct LoadoutV2PreviewCamera(short HullId, Matrix World, Matrix View, Matrix Projection);

public sealed record LoadoutV2HardpointMarker(Vector2 Point, bool Occluded, bool IsTurret,
    IReadOnlyList<Vector2?> Arc, Vector2? AxisEnd, bool ArcLimitKnown, string Label, string? GunnerName);

/// <summary>
/// Pure, cached CVH geometry and projection. Positions use HullWeaponPositions,
/// native Igc/hullTypeIGC.cpp:89-136; directions use HitTest.cpp:797-801's frame
/// flip. No frame means no marker, including equipment/cargo sockets.
/// Native UpdateCone is dead code (WinTrek/load.cpp:1321-1356), and minDot is
/// commented out of HardpointData (Igc/igc.h:1698). With no limit, the turret
/// decoration is explicitly an aim guide, never a claimed firing restriction.
/// </summary>
public sealed class LoadoutV2Hardpoints
{
    private readonly DataHullTypeIGC _hull;
    private readonly Vector3[] _positions;
    private readonly Vector3?[] _directions;
    private readonly Plane[][] _occluders;

    public LoadoutV2Hardpoints(DataHullTypeIGC hull, CvhFile? model)
    {
        _hull = hull;
        _positions = HullWeaponPositions.Compute(hull, model).Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray();
        _directions = new Vector3?[hull.Hardpoints.Length];
        if (model is null) { _occluders = []; return; }
        for (int i = 0; i < _directions.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(hull.Hardpoints[i].FrameName)) continue;
            foreach (var frame in model.Frames)
            {
                if (frame.Name != hull.Hardpoints[i].FrameName) continue;
                var direction = new Vector3(frame.Forward.X, -frame.Forward.Y, -frame.Forward.Z);
                // A zero direction still has a real position; omit only its arc.
                _directions[i] = direction.LengthSquared() > 1e-8f ? Vector3.Normalize(direction) : Vector3.Zero;
                break;
            }
        }
        float scale = .5f * hull.Length / model.Radius;
        _occluders = model.Hulls.Select(h => Planes(h, scale)).ToArray();
    }

    /// <summary>Hover wins over keyboard focus, including non-weapon rows.
    /// An open picker retains its source slot while the offer cursor moves.</summary>
    public static int SelectedSlot(LoadoutV2Presentation frame, LoadoutV2Layout.Element? hover)
        => hover is { Kind: LoadoutV2Layout.Kind.Slot } row ? row.Index
            : frame.SlotFocus >= 0 ? frame.SlotFocus : frame.OpenSlot;

    public LoadoutV2HardpointMarker? Project(LoadoutV2SlotRow slot, LoadoutV2PreviewCamera camera,
        TouchRect preview, float? minDot = null)
    {
        int mount = slot.Mount;
        if (camera.HullId != _hull.HullId || slot.Slot.Type != EquipmentType.Weapon
            || mount >= _hull.MaxWeapons || mount >= _directions.Length || _directions[mount] is not { } direction)
            return null;
        var position = _positions[mount];
        var transform = camera.World * camera.View * camera.Projection;
        if (ScreenPoint(position, transform, preview) is not { } point) return null;
        bool turret = !_hull.Hardpoints[mount].Fixed;
        var arc = new List<Vector2?>();
        Vector2? axis = null;
        bool known = turret && minDot is { } dot && float.IsFinite(dot) && dot >= -1 && dot <= 1;
        if (turret && direction != Vector3.Zero)
        {
            var up = MathF.Abs(Vector3.Dot(direction, Vector3.Up)) < .9f ? Vector3.Up : Vector3.UnitZ;
            var right = Vector3.Normalize(Vector3.Cross(direction, up));
            up = Vector3.Cross(right, direction);
            float length = _hull.Length * .23f;
            // A real limit, when supplied, is exactly acos(minDot), as in the
            // commented native cone. PCore001 supplies none: the small guide
            // has decorative dimensions, labelled accordingly by the renderer.
            float along = known ? length * minDot!.Value : length * .8f;
            float across = known ? length * MathF.Sqrt(MathF.Max(0, 1 - minDot!.Value * minDot.Value)) : length * .35f;
            var center = position + direction * along;
            for (int i = 0; i <= 48; i++)
            {
                float angle = MathHelper.TwoPi * i / 48;
                arc.Add(ScreenPoint(center + across * (right * MathF.Cos(angle) + up * MathF.Sin(angle)), transform, preview));
            }
            axis = ScreenPoint(position + direction * length, transform, preview);
        }
        var eye = Matrix.Invert(camera.World * camera.View).Translation;
        bool occluded = _occluders.Any(planes => IntersectsBeforeMount(planes, eye, position, _hull.Length * .008f));
        return new(point, occluded, turret, arc, axis, known, slot.Label, turret ? slot.GunnerName : null);
    }

    /// <summary>Homogeneous projection; offscreen coordinates remain available
    /// for clipping the arc. Points behind the camera or outside Z clip do not.</summary>
    public static Vector2? ScreenPoint(Vector3 position, Matrix transform, TouchRect preview)
    {
        var clip = Vector4.Transform(new Vector4(position, 1), transform);
        if (!float.IsFinite(clip.W) || clip.W <= 0 || clip.Z < 0 || clip.Z > clip.W
            || preview.Width <= 0 || preview.Height <= 0) return null;
        var point = new Vector2(preview.X + (clip.X / clip.W + 1) * .5f * preview.Width,
            preview.Y + (1 - clip.Y / clip.W) * .5f * preview.Height);
        return float.IsFinite(point.X) && float.IsFinite(point.Y) ? point : null;
    }

    // Convex CVH face planes, cached once. Adjacent edge pairs define candidate
    // faces; retain only supporting planes. The ray is tested against the union
    // of sub-hulls, so a rear mount stays visible but dim through the real hull.
    private static Plane[] Planes(CvhHull hull, float scale)
    {
        var points = hull.Vertices.Select(v => new Vector3(-v.Position.X, v.Position.Y, v.Position.Z) * scale).ToArray();
        var result = new List<Plane>();
        for (int i = 0; i < points.Length; i++)
        {
            var neighbors = hull.Vertices[i].AdjacentIndices;
            for (int a = 0; a < neighbors.Length; a++)
                for (int b = a + 1; b < neighbors.Length; b++)
                {
                    var normal = Vector3.Cross(points[neighbors[a]] - points[i], points[neighbors[b]] - points[i]);
                    if (normal.LengthSquared() < 1e-8f) continue;
                    normal.Normalize();
                    var plane = new Plane(normal, -Vector3.Dot(normal, points[i]));
                    float low = points.Min(p => plane.DotCoordinate(p)), high = points.Max(p => plane.DotCoordinate(p));
                    const float tolerance = .0001f;
                    if (low < -tolerance && high > tolerance) continue;
                    if (high > tolerance) plane = new Plane(-plane.Normal, -plane.D);
                    if (!result.Any(p => Vector3.Dot(p.Normal, plane.Normal) > .9999f && MathF.Abs(p.D - plane.D) < tolerance))
                        result.Add(plane);
                }
        }
        return result.ToArray();
    }

    private static bool IntersectsBeforeMount(Plane[] planes, Vector3 eye, Vector3 mount, float tolerance)
    {
        if (planes.Length < 4) return false;
        var ray = mount - eye;
        float enter = 0, leave = 1;
        foreach (var plane in planes)
        {
            float distance = plane.DotCoordinate(eye), slope = Vector3.Dot(plane.Normal, ray);
            if (MathF.Abs(slope) < 1e-8f) { if (distance > 0) return false; continue; }
            float t = -distance / slope;
            if (slope < 0) enter = MathF.Max(enter, t); else leave = MathF.Min(leave, t);
            if (enter > leave) return false;
        }
        return (1 - enter) * ray.Length() > tolerance;
    }
}
