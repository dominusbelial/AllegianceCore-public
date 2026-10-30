using System.Numerics;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// A lightweight handle to a world object, mirroring the native training library's
/// <c>AbstractTarget</c>/<c>TypeIDTarget</c> pair (<c>training/TypeIDTarget.cpp</c>).
/// Native conditions and actions never hold a live object pointer that can go stale;
/// they hold a <em>(type, id)</em> pair and resolve it to a live <see cref="ITrainingObject"/>
/// through <see cref="ITrainingSite"/> at each <c>Evaluate</c>/<c>Execute</c>. This port keeps
/// that same lazy-resolution semantic so a <c>TargetRef</c> remains valid across deaths,
/// cluster hops and respawns.
/// </summary>
/// <param name="Type">Object type (<c>OT_*</c>, <c>igc.h:116-129</c>).</param>
/// <param name="Id">Object id, or <see cref="Na"/> when unbound.</param>
public readonly record struct TargetRef(ObjectType Type, int Id)
{
    /// <summary>Sentinel "<c>not available</c>" id — the native <c>NA</c> macro.</summary>
    public const int Na = -1;

    /// <summary>A target with no object bound (<c>NA</c> type/id), as native passes for
    /// "<c>no target</c>" (e.g. <c>c_cidDoNothing</c> clears a command target).</summary>
    public static TargetRef None => new(ObjectType.Invalid, Na);

    public bool IsBound => Id != Na;
}

/// <summary>
/// The native <c>ObjectType</c> values actually referenced by the training library
/// (<c>src/Igc/igc.h:116-129</c>). Kept as a signed enum because <c>OT_invalid = -1</c>.
/// </summary>
public enum ObjectType
{
    Invalid = -1,
    Ship = 0,
    Station = 1,
    Asteroid = 5,
    Probe = 4,
    Buoy = 9,
    Warp = 7,
}

/// <summary>
/// The live object a <see cref="TargetRef"/> resolves to — the geometry surface of the
/// native <c>ImodelIGC</c> interface (<c>src/Igc/modelIGC.h</c>) that the geometric
/// conditions (radius/pointing-at/moving-towards) read. Ship-only state (autopilot,
/// state mask, applied controls) is exposed through <see cref="ITrainingSite"/> instead,
/// so the world object stays a plain geometry carrier.
/// </summary>
public interface ITrainingObject
{
    TargetRef Ref { get; }
    float Radius { get; }
    Vector3 Position { get; }
    Vector3 Velocity { get; }
    Vector3 Forward { get; }
    int SectorId { get; }
}
