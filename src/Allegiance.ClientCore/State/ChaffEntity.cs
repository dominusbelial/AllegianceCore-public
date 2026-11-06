using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// A live deployed chaff decoy in the client's world model — one per real
/// <c>FM_S_CREATE_CHAFF(109)</c> broadcast (<c>Wire/CreateChaff.cs</c>). Chaff
/// mirrors the <b>transient</b> projectile shape (not the id-keyed
/// probe/mine tables): the CREATE_CHAFF codec carries <b>no object id</b>
/// (native chaff has no FM_S_CHAFF_DESTROYED either — <c>Chaff</c> expiry simply
/// removes the model from its cluster, <c>chaffigc.h:68-73</c>), so a chaff is
/// modelled as an entry in a short-lived list that expires
/// <see cref="ClientWorld.PruneExpiredChaff"/> seconds after
/// its converted creation instant and is then pruned client-side.
/// </summary>
/// <remarks>The wire Time0 is server simulation milliseconds. The world converts
/// it through its PING-fed session site (Igc/chaffigc.cpp:48); coasting and
/// expiry therefore include time already elapsed before receipt. Local-only
/// fixtures without a server sample retain a receipt anchor until one arrives.</remarks>
public sealed class ChaffEntity
{
    /// <summary>The decoded chaff type id (<c>CreateChaff.Etid</c> = the
    /// <c>DataChaffTypeIGC.ExpendableTypeId</c>). The renderer resolves the
    /// type's real lifespan, radius-ceiling and texture from it via the static
    /// core (see <c>ChaffModelSource</c>); <c>ClientWorld.PruneExpiredChaff</c>
    /// resolves the lifespan for removal.</summary>
    public short Etid;

    /// <summary>The deploy (spawn) position carried by the broadcast
    /// (<c>CreateChaff.P0</c>).</summary>
    public Vector3 P0;

    /// <summary>The constant coasting velocity, units/second
    /// (<c>CreateChaff.V0</c>) — inherited from the deploying ship, never
    /// altered (native chaff has no acceleration, <c>chaffigc.cpp:52,59</c>).</summary>
    public Vector3 V0;

    /// <summary>Local monotonic receipt instant
    /// (<see cref="Environment.TickCount64"/>), set by
    /// <see cref="ClientWorld.Apply(Wire.CreateChaff)"/>.</summary>
    public long ReceivedLocalMs;

    /// <summary>Native converted creation instant in the local simulation epoch.</summary>
    public double StartClientSeconds;
    internal double StartServerSeconds;
    internal Func<long, double>? ClientTimeAt;
    public double ElapsedSeconds(long nowLocalMs) => ClientTimeAt is null
        ? (nowLocalMs - ReceivedLocalMs) / 1000.0
        : ClientTimeAt(nowLocalMs) - StartClientSeconds;


    /// <summary>The chaff's extrapolated world position at the given local
    /// monotonic instant — its spawn position advanced along its
    /// <see cref="V0"/> by the elapsed seconds (clamped so it never travels
    /// before it was created). Faithful to native's constant-velocity coast
    /// (<c>chaffigc.cpp:52,84</c>); the same simple linear extrapolation the
    /// projectile entity uses.</summary>
    public Vector3 PositionAt(long nowLocalMs)
    {
        float elapsed = Math.Max(0f, (float)ElapsedSeconds(nowLocalMs));
        return P0 + V0 * elapsed;
    }

    /// <summary>Fallback chaff lifespan (seconds) used when a chaff's type
    /// cannot be resolved from the static core (unknown <see cref="Etid"/> or no
    /// core). Matches native's <c>c_fLifespan = 5.0f</c> (<c>chaffigc.cpp:21</c>),
    /// the port's single source for the unresolvable-type degrade-not-crash
    /// default (also read by <c>ChaffModelSource.ResolveLifespan</c>).</summary>
    public const float DefaultLifespan = 5f;

    /// <summary>Fallback chaff radius ceiling (units) when a chaff's type cannot
    /// be resolved from the static core. A mid representative of the corpus
    /// <c>DataChaffTypeIGC.Radius</c> values (5–15; the demo PCore001 uses 5),
    /// used so an unresolvable type still renders a sane-sized decoy rather than
    /// vanishing (read by <c>ChaffModelSource.ResolveRadiusCeiling</c>).</summary>
    public const float DefaultRadiusCeiling = 5f;
}
