using System.Reflection;
using Allegiance.Core.Model;

namespace Allegiance.Core.Tests;

/// <summary>Give this test mission a private core and constant arrays before
/// editing them. Never mutate a cache entry another server can be reading.</summary>
internal sealed class CoreConstantOverride : IDisposable
{
    private static readonly FieldInfo CoreField = typeof(Mission).GetField("<Core>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private readonly Mission _mission;
    private readonly StaticCore _previous;
    internal CoreConstantOverride(Mission mission, int slot, float value)
    {
        _mission = mission;
        _previous = mission.Core;
        var clone = (StaticCore)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_previous, null)!;
        var constants = _previous.Constants;
        typeof(StaticCore).GetField("<Constants>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(clone,
            constants with { FloatConstants = (float[])constants.FloatConstants.Clone(),
                DamageConstants = constants.DamageConstants.Select(row => (float[])row.Clone()).ToArray() });
        clone.Constants.FloatConstants[slot] = value;
        CoreField.SetValue(mission, clone);
    }
    public void Dispose() => CoreField.SetValue(_mission, _previous);
}
