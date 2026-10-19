namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// The six core flight gauges this port's HUD shows today. Ordered semantically
/// (not by layout); each maps to a real original-game <c>*Gaugebmp.mdl</c>
/// texture (see <see cref="GaugeTextureSource"/>).
/// </summary>
public enum GaugeKind
{
    Hull,
    Shield,
    Energy,
    Fuel,
    Ammo,
    Thrust,
}

