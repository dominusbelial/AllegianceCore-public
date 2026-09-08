using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>
/// A single resolved explosion/blast — the port of the native
/// <c>ExplosionData</c> (<c>clusterIGC.h:340-367</c>,
/// <c>src/Igc/clusterIGC.h</c>) together with the field assignment
/// <c>CclusterIGC::CreateExplosion</c> performs there. It is the payload the
/// explosion-resolution pass
/// (<see cref="Sector.ResolveExplosion"/>) applies to every target in blast
/// range: <see cref="Amount"/> of <see cref="DamageType"/> damage at the blast
/// centre <see cref="Position"/>, decaying by the squared-distance falloff
/// <c>f = 1 − d/radius; amount *= f·f</c> (<c>clusterIGC.cpp:422-429</c>).
/// </summary>
/// <remarks>
/// <para><b>Dropped <c>explosionType</c> deliberately.</b> The native field is a
/// <em>visual-effect</em> id passed straight to the render client
/// (<c>m_pClusterSite->AddExplosion(e.position, e.radius,
/// e.explosionType)</c>, <c>clusterIGC.cpp:407</c>) — there is no rendering /
/// visual system in this headless port, so the id has no analogue here and is
/// omitted. Stated plainly, per this repo's honesty convention. The remaining
/// fields map one-to-one onto the native assignment
/// (<c>clusterIGC.h:354-360</c>): <c>damageType → DamageType</c>, <c>amount →
/// Amount</c>, <c>radius → Radius</c>, <c>time → Time</c>, <c>position →
/// Position</c>, <c>launcher → Launcher</c>.</para>
/// <para><b>Pure in-memory simulation state, never serialized.</b> Unlike
/// <c>DataShipIGC</c>/<c>DataProjectileIGC</c> the native <c>ExplosionData</c>
/// is a transient stack/resident object (a member of <c>CclusterIGC</c>'s
/// <c>m_explosions</c> ring, <c>clusterIGC.cpp:406</c>), never written to the
/// wire — so this port's representation is just a plain C# type with no
/// byte-layout concern, no packing/alignment constraint, and no codec.</para>
/// <para><b>Immediate resolution.</b> The native queues an explosion and
/// resolves it <em>next tick</em> with a position projection
/// <c>dt = (e.time − m_lastUpdate) − m_tOffset</c> (<c>clusterIGC.cpp:409,
/// 419</c>). This port resolves <b>immediately/synchronously</b> at the moment
/// the explosion is created, against each target's <em>current</em> position —
/// an honest simplification of the same category as P95's "no swept
/// dwell-time math" (see <see cref="Sector.ResolveExplosion"/>).</para>
/// </remarks>
public readonly record struct Explosion(
    byte DamageType,
    float Amount,
    float Radius,
    double Time,
    Vector3 Position,
    Model? Launcher);
