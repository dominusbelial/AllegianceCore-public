using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Bounded, render-only flashes. Positions use the same camera-relative
/// coordinates as hull geometry. No simulation state or gameplay visibility changes.</summary>
public sealed class EffectLights
{
    public const int Capacity = 4;
    private static readonly ExplosionPreset[] Presets = Enumerable.Range(1, 7).Select(ExplosionPresets.For).ToArray();
    public readonly Vector4[] Positions = new Vector4[Capacity]; // xyz, range
    public readonly Vector4[] Colors = new Vector4[Capacity]; // linear RGB, unused
    private readonly float[] _scores = new float[Capacity];
    public int Count { get; private set; }
    private EffectsDetail _detail = EffectsDetail.Medium;
    public EffectsDetail Detail
    {
        get => _detail;
        set { _detail = Enum.IsDefined(value) ? value : EffectsDetail.Medium; Clear(); }
    }

    public void Clear() { Count = 0; Array.Clear(Positions); Array.Clear(Colors); Array.Clear(_scores); }

    public void Explosion(ExplosionEntity e, short sector, long now, Vector3 eye)
    {
        if (Detail == EffectsDetail.Low || e.ClusterId != sector) return;
        float age = (now - e.ReceivedLocalMs) / 1000f;
        if (age < 0 || age >= .65f || !ExplosionPresets.IsKnown(e.Type)) return;
        var preset = Presets[e.Type - 1];
        float fade = 1 - age / .65f;
        var position = new Vector3(e.Position.X + e.Velocity.X * age,
            e.Position.Y + e.Velocity.Y * age, e.Position.Z + e.Velocity.Z * age) - eye;
        Add(position, Math.Max(20, preset.RadiusExplosion * 5), new Vector3(1f, .32f, .075f) * (3 * fade * fade));
    }

    public void Impact(HullHitEntity hit, short sector, long now, Vector3 eye)
    {
        if (Detail == EffectsDetail.Low || hit.ClusterId != sector) return;
        float age = (now - hit.ReceivedLocalMs) / 1000f;
        if (age < 0 || age >= .16f) return;
        var position = new Vector3(hit.Position.X, hit.Position.Y, hit.Position.Z) - eye
            + new Vector3(hit.Normal.X, hit.Normal.Y, hit.Normal.Z) * Math.Clamp(hit.Radius * .05f, .5f, 4);
        // The warm hull flash and cool shield flash retain the two native hit cues.
        Add(position, Math.Clamp(hit.Radius * 2, 8, 160),
            (hit.AbsorbedByShield ? new Vector3(.15f, .45f, 1f) : new Vector3(1f, .55f, .2f)) * (1.5f * (1 - age / .16f)));
    }

    private void Add(Vector3 position, float range, Vector3 color)
    {
        if (!float.IsFinite(position.LengthSquared()) || !float.IsFinite(range)) return;
        float score = color.LengthSquared() * range * range / Math.Max(range * range, position.LengthSquared());
        int limit = Detail == EffectsDetail.High ? Capacity : 2;
        int index = Count;
        if (Count == limit)
        {
            index = 0;
            for (int i = 1; i < Count; i++) if (_scores[i] < _scores[index]) index = i;
            if (score <= _scores[index]) return;
        }
        else Count++;
        Positions[index] = new Vector4(position, range);
        Colors[index] = new Vector4(color, 0);
        _scores[index] = score;
    }
}
