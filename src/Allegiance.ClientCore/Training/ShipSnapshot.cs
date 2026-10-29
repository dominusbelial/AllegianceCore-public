using System.Numerics;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// The physical/equipment state <c>ResetShipAction</c> snapshots on its first <c>Execute</c> and
/// restores on later <c>Execute</c>s — the C# port of native <c>ResetShipAction::State</c>
/// (<c>resetshipaction.h:44-53</c>), reduced to the states this sim actually models (position,
/// velocity, orientation, fuel, shield fraction, hull fraction, ammo, energy). Native's
/// <c>rotation</c> (angular velocity) has no modelled C# counterpart and is intentionally absent.
/// The value is an opaque blob exchanged between <see cref="ITrainingSite.CaptureShip"/> and
/// <see cref="ITrainingSite.RestoreShip"/> (each site implements both consistently), so its vector
/// representation is <see cref="Vector3"/> and the host converts to/from its sim vectors. Immutable
/// so the action can re-apply the same snapshot across Executes.
/// </summary>
public sealed class ShipSnapshot
{
    public Vector3 Position { get; init; }
    public Vector3 Velocity { get; init; }
    public Orientation Orientation { get; init; } = new();
    public float Fuel { get; init; }
    public float Shield { get; init; }
    public float Hull { get; init; }
    public short Ammo { get; init; }
    public float Energy { get; init; }

    /// <summary>An empty snapshot for an absent ship (nothing to capture/restore).</summary>
    public static ShipSnapshot Empty { get; } = new();
}
