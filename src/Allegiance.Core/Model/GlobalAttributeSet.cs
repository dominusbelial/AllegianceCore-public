namespace Allegiance.Core.Model;

/// <summary>
/// The side-global attribute multiplier set — the port of the native
/// <c>GlobalAttributeSet</c> (<c>src/Igc/igc.h:915-966</c>): 25 floating-point
/// multiplicative factors (<c>c_gaMax = 25</c>) that scale a team's ship
/// characteristics (scan range, shield regeneration, signature, thrust, etc.)
/// as its civilization's <c>GasBaseAttributes</c> plus every completed
/// development's <c>Gas</c> accumulate. This task <b>stores, seeds and
/// broadcasts</b> the set and implements <see cref="Apply"/>; it does <b>not</b>
/// wire every gameplay consumer of the 25 named multipliers (that is a
/// separate, wider task — see the stage report).
/// </summary>
/// <remarks>
/// <para><b><see cref="Apply"/> semantics — multiplication, not addition</b>
/// (read from the live native source, not guessed). The native <c>Apply</c>
/// (<c>igc.h:935-946</c>) multiplies each element:
/// <code>
/// void GlobalAttributeSet::Apply(const GlobalAttributeSet&amp; gas)
/// {
///     for (int i = 0; i &lt; c_gaMax; i++)
///     {
///         assert (m_attributes[i] &gt; 0.0f &amp;&amp; m_attributes[i] &lt; 100.0f);
///         assert (gas.m_attributes[i] &gt; 0.0f &amp;&amp; gas.m_attributes[i] &lt; 100.0f);
///         m_attributes[i] *= gas.m_attributes[i];
///     }
/// }
/// </code>
/// and the baseline <c>Initialize()</c> (<c>igc.h:923-927</c>) sets every
/// element to <c>1.0f</c> (the multiplicative identity). So a development whose
/// <c>Gas</c> is all 1.0 grants no attribute change — exactly the condition
/// the native uses to derive a development's <c>TechOnly</c> flag
/// (<c>developmentigc.cpp:38-46</c>).</para>
/// </remarks>
public readonly struct GlobalAttributeSet : IEquatable<GlobalAttributeSet>
{
    /// <summary>The fixed element count (<c>c_gaMax = 25</c>).</summary>
    public const int Count = 25;

    // Named element indices — the port of native `c_ga…` constants
    // (`src/Igc/igc.h:585-610`). P446 wires the four flight-physics consumers;
    // the rest are listed (not wired) in the P446 report.
    /// <summary>Max-speed multiplier (<c>c_gaMaxSpeed = 0</c>, igc.h:585).</summary>
    public const int MaxSpeed = 0;
    /// <summary>Engine-thrust multiplier (<c>c_gaThrust = 1</c>, igc.h:586).</summary>
    public const int Thrust = 1;
    /// <summary>Turn-rate multiplier (<c>c_gaTurnRate = 2</c>, igc.h:588).</summary>
    public const int TurnRate = 2;
    /// <summary>Turn-torque multiplier (<c>c_gaTurnTorque = 3</c>, igc.h:589).</summary>
    public const int TurnTorque = 3;

    // P467b adds the four ship-durability indices (igc.h:593-595,600): hull
    // max-armor, shield max-strength, shield regeneration and ship max-energy.
    // Wired in Ship.EffectiveMaxHitPoints / EffectiveMaxShieldStrength /
    // EffectiveShieldRegen / EffectiveMaxEnergy (see the P467B report).

    /// <summary>Ship hull max-armor multiplier (<c>c_gaMaxArmorShip = 8</c>, igc.h:593).</summary>
    public const int MaxArmorShip = 8;
    /// <summary>Ship shield max-strength multiplier (<c>c_gaMaxShieldShip = 9</c>, igc.h:594).</summary>
    public const int MaxShieldShip = 9;
    /// <summary>Ship shield-regeneration multiplier (<c>c_gaShieldRegenerationShip = 10</c>, igc.h:595).</summary>
    public const int ShieldRegenerationShip = 10;
    /// <summary>Ship max-energy multiplier (<c>c_gaMaxEnergy = 13</c>, igc.h:600).</summary>
    public const int MaxEnergy = 13;

    // P467 adds the five weapon-output indices (igc.h:601-613): ammo-projectile
    // speed, energy-weapon lifespan, missile turn rate, gun damage and missile
    // damage. The remaining GAs (stations 4-7, ship durability 8-10, 13,
    // visibility 11-12, mining 17-19, ripcord 20, economy 23-24) stay unwired —
    // see the P446/P467 reports for the full ledger.

    /// <summary>Ammo-weapon projectile-speed multiplier (<c>c_gaSpeedAmmo = 14</c>,
    /// igc.h:601). Applied only to a weapon whose <c>cAmmoPerShot</c> ≠ 0.</summary>
    public const int SpeedAmmo = 14;
    /// <summary>Energy-weapon projectile-lifespan multiplier (<c>c_gaLifespanEnergy = 15</c>,
    /// igc.h:602). Applied only to a weapon whose <c>energyPerShot</c> ≠ 0.</summary>
    public const int LifespanEnergy = 15;
    /// <summary>Missile turn-rate multiplier (<c>c_gaTurnRateMissile = 16</c>, igc.h:604).</summary>
    public const int TurnRateMissile = 16;
    /// <summary>Gun (projectile) damage multiplier (<c>c_gaDamageGuns = 21</c>, igc.h:612).</summary>
    public const int DamageGuns = 21;
    /// <summary>Missile damage multiplier (<c>c_gaDamageMissiles = 22</c>, igc.h:613).</summary>
    public const int DamageMissiles = 22;

    private readonly float[] _values;

    /// <summary>Wrap a 25-float array (cloned). Throws if not exactly
    /// <see cref="Count"/> elements.</summary>
    public GlobalAttributeSet(float[] values)
    {
        if (values is null || values.Length != Count)
            throw new ArgumentException($"GlobalAttributeSet needs exactly {Count} floats, got {values?.Length ?? -1}");
        _values = (float[])values.Clone();
    }

    /// <summary>The multiplicative-identity set (every factor 1.0 —
    /// <c>Initialize()</c>, <c>igc.h:923-927</c>).</summary>
    public static GlobalAttributeSet Identity { get; } = new GlobalAttributeSet(UnityArray());

    private static float[] UnityArray()
    {
        var a = new float[Count];
        Array.Fill(a, 1.0f);
        return a;
    }

    /// <summary>Wrap a span of exactly <see cref="Count"/> floats.</summary>
    public static GlobalAttributeSet FromArray(ReadOnlySpan<float> values)
    {
        if (values.Length != Count)
            throw new ArgumentException($"GlobalAttributeSet needs exactly {Count} floats, got {values.Length}");
        var a = new float[Count];
        values.CopyTo(a);
        return new GlobalAttributeSet(a);
    }

    /// <summary>The <paramref name="index"/>-th multiplier.</summary>
    public float Get(int index) => _values[index];

    /// <summary>A defensive 25-float copy.</summary>
    public float[] ToArray() => (float[])_values.Clone();

    /// <summary>
    /// Element-wise multiply this set by <paramref name="gas"/> — the exact
    /// port of the native <c>Apply</c> (<c>igc.h:935-946</c>):
    /// <c>result[i] = this[i] * gas[i]</c>. Immutable: returns a fresh set;
    /// the receiver is unchanged. This is how a completed development's
    /// <c>Gas</c> accumulates onto the side's global attribute set
    /// (<c>ApplyGlobalAttributeSet</c>, <c>sideigc.h:343-345</c>).
    /// </summary>
    public GlobalAttributeSet Apply(GlobalAttributeSet gas)
    {
        var r = new float[Count];
        for (int i = 0; i < Count; i++)
            r[i] = _values[i] * gas._values[i];
        return new GlobalAttributeSet(r);
    }

    public bool Equals(GlobalAttributeSet other)
    {
        for (int i = 0; i < Count; i++)
            if (_values[i] != other._values[i]) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is GlobalAttributeSet g && Equals(g);
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var v in _values) h.Add(v);
        return h.ToHashCode();
    }

    public static bool operator ==(GlobalAttributeSet a, GlobalAttributeSet b) => a.Equals(b);
    public static bool operator !=(GlobalAttributeSet a, GlobalAttributeSet b) => !a.Equals(b);

    public override string ToString() => string.Join(",", _values.Select(v => v.ToString("0.###")));
}
