namespace Allegiance.Core.Sim;

/// <summary>Shared mission damage-slot phase (Igc/igc.h:5070-5095).
/// Exactly one five-second boundary is retired per mission Update.</summary>
internal sealed class DamageTrackClock
{
    private double _next = 5;
    internal long Generation { get; private set; }
    internal void Initialize(double now) { _next = now + 5; Generation = 0; }
    internal void Update(double now)
    {
        if (now >= _next) { _next += 5; Generation++; }
    }
}
