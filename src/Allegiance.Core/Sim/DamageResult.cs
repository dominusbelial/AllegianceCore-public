namespace Allegiance.Core.Sim;

/// <summary>
/// <c>DamageResult</c> — the return of <c>ImodelIGC::ReceiveDamage</c>,
/// <c>src/Igc/igc.h:47-51</c>:
/// <code>
///   typedef char DamageResult;
///   const DamageResult c_drKilled       = 1;
///   const DamageResult c_drHullDamage   = 2;
///   const DamageResult c_drShieldDamage = 3;
///   const DamageResult c_drNoDamage     = 4;
/// </code>
/// The projectile uses <c>dr &lt;= c_drHullDamage</c> to decide whether its
/// impact dealt visible hull damage (<c>projectileIGC.cpp:193</c>).
/// </summary>
public enum DamageResult : byte
{
    Killed = 1,
    HullDamage = 2,
    ShieldDamage = 3,
    NoDamage = 4,
}
