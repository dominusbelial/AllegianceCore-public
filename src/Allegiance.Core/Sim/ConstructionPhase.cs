using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>Native building-effect phase sample (Igc/buildingeffectigc.h:160-225).</summary>
public readonly record struct ConstructionPhase(Vector3 Position, float Radius, float InnerAlpha,
    float InnerIntensity, float OuterIntensity, bool Expired)
{
    public const double Duration = 40;

    public static ConstructionPhase At(double elapsed, Vector3 start, Vector3 stop, float rockRadius, float stationRadius)
    {
        float maximum = 1.1f * Math.Max(rockRadius, stationRadius);
        if (elapsed >= Duration) return new(stop, stationRadius, 0, 0, 0, true);
        if (elapsed >= 30)
        {
            float f = (float)((elapsed - 30) / 10);
            return new(stop, stationRadius * f + maximum * (1 - f), 1 - f, 1 - f, 1 - f, false);
        }
        if (elapsed >= 20) return new(stop, maximum, .5f + .5f * (float)((elapsed - 20) / 10), 1, 1, false);
        if (elapsed >= 10)
        {
            float f = (float)((elapsed - 10) / 10);
            return new(stop, maximum * f + rockRadius * (1 - f), .5f, 1, 1, false);
        }
        float envelope = Math.Max(0, (float)(elapsed / 10));
        return new(stop * envelope + start * (1 - envelope), rockRadius * envelope + 1 - envelope, .5f, 1, 1, false);
    }
}
