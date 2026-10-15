using System.Collections.Generic;
using Allegiance.Core;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Native station LightsGeo decals, using the same flip, scale and
/// orientation as the mesh (thinggeo.cpp:1482-1510). Serialized SS groups include
/// the bay approach rows; no separate procedural runway effect is needed.</summary>
public static class NativeStationLights
{
    /// <summary>Native lights require a projected station radius above 32 px
    /// (thinggeo.cpp:1618-1630). GetImageRadius measures a horizontal eye-space
    /// offset (context.cpp:1858-1871), so depth, not radial distance, determines
    /// the projected radius even for a station near the edge of the screen.</summary>
    public static bool IsVisible(Vector3 eyeRelative, float radius, Matrix view,
        Matrix projection, float viewportWidth)
    {
        var eye = Vector3.Transform(eyeRelative, view);
        if (eye.Z >= 0f || radius <= 0f || viewportWidth <= 0f) return false;
        return radius * projection.M11 * (viewportWidth * 0.5f) / -eye.Z > 32f;
    }

    public static void Append(IReadOnlyList<MdlLight> lights, Matrix world, float scale,
        float time, List<NavStrobeView> output)
    {
        var local = NativeStationResolver.NoseFlip * Matrix.CreateScale(scale) * world;
        foreach (var light in lights)
        {
            // Native does not wrap negative time-phase forward into the first
            // period (efapp.cpp:180-194); a future phase has not flashed yet.
            if (time < light.Phase) continue;
            var timing = new NavStrobeTiming(light.Period, light.Phase,
                light.RampUp, light.Hold, light.RampDown);
            float pulse = NavStrobeMath.Pulse(time, timing);
            if (pulse <= 0f) continue;
            var position = Vector3.Transform(new Vector3(light.Position[0],
                light.Position[1], light.Position[2]), local);
            // DrawDecal's scale is the quad half extent, transformed by the
            // model scale (context.cpp:209-225, 2312-2317), not station radius.
            output.Add(new NavStrobeView(new(position.X, position.Y, position.Z),
                0.5f * scale * pulse,
                new Vector3(light.Color[0], light.Color[1], light.Color[2]), light.Color[3]));
        }
    }
}
