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
/// <see cref="ReceivedLocalMs"/> and is then pruned client-side.
/// </summary>
/// <remarks>
/// <para><b>Why local receipt time, not the wire <c>Time0</c>.</b> The codec's
/// <c>Time0</c> is the server tick index (a uint in the server's epoch) that
/// shares no clock with the client — the same situation as the projectile's
/// <c>TimeCreate</c> (see <see cref="ProjectileEntity"/>). The port therefore
/// anchors both motion extrapolation and expiry to the local monotonic instant
/// the message was received (<see cref="Environment.TickCount64"/>), captured
/// in <see cref="ClientWorld.Apply(Wire.CreateChaff)"/>. The server broadcasts
/// CREATE_CHAFF at deploy, so for a same-host client receipt ≈ creation.</para>
/// <para><b>What rides along.</b> The wire carries the chaff's spawn position
/// (<see cref="P0"/>), its constant coasting velocity (<see cref="V0"/>), and
/// the chaff <em>type</em> id (<see cref="Etid"/>). Lifespan / radius-ceiling /
/// texture are <em>not</em> on the wire — the renderer resolves them from the
/// static core's <c>DataChaffTypeIGC</c> via <c>ChaffModelSource</c> (the
/// client otherwise has no static core; see <c>Rendering.ShopCatalog</c>).</para>
/// </remarks>
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

    /// <summary>The chaff's extrapolated world position at the given local
    /// monotonic instant — its spawn position advanced along its
    /// <see cref="V0"/> by the elapsed seconds (clamped so it never travels
    /// before it was created). Faithful to native's constant-velocity coast
    /// (<c>chaffigc.cpp:52,84</c>); the same simple linear extrapolation the
    /// projectile entity uses.</summary>
    public Vector3 PositionAt(long nowLocalMs)
    {
        float elapsed = Math.Max(0f, (nowLocalMs - ReceivedLocalMs) / 1000.0f);
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
