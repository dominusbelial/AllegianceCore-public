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

    // P475 adds the four station-durability indices (igc.h:591-595): station
    // max-armor, armor regeneration, station max-shield and shield
    // regeneration. Wired in Station.EffectiveMaxArmor / EffectiveMaxShield /
    // EffectiveArmorRegen / EffectiveShieldRegen (see the P475 report).

    /// <summary>Station max-armor multiplier (<c>c_gaMaxArmorStation = 4</c>, igc.h:591).</summary>
    public const int MaxArmorStation = 4;
    /// <summary>Station armor-regeneration multiplier (<c>c_gaArmorRegenerationStation = 5</c>, igc.h:592).</summary>
    public const int ArmorRegenerationStation = 5;
    /// <summary>Station max-shield multiplier (<c>c_gaMaxShieldStation = 6</c>, igc.h:594).</summary>
    public const int MaxShieldStation = 6;
    /// <summary>Station shield-regeneration multiplier (<c>c_gaShieldRegenerationStation = 7</c>, igc.h:595).</summary>
    public const int ShieldRegenerationStation = 7;

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

    // P476 adds the two visibility indices (igc.h:601-602): scan range and
    // signature. c_gaScanRange boosts the SCANNER side's detection radius
    // (MyHullType::GetScannerRange, stationIGC.cpp:591, probeigc.h:165);
    // c_gaSignature attenuates the TARGET side's signature wherever detection
    // compares signature against scan range (the `m /= pside->…GetAttribute(
    // c_gaSignature)` divisor in shipIGC.h:357 / stationIGC.h:322 /
    // probeigc.h:177 and bSimpleEye's SigMod, clusterIGC.cpp:152). Wired in
    // Ship/Station/Probe.EffectiveScannerRange and the Sector visibility sweep
    // (see the P476 report).

    /// <summary>Scanner-range multiplier (<c>c_gaScanRange = 11</c>, igc.h:601).
    /// Boosts the owning side's ship/probe/station detection range.</summary>
    public const int ScanRange = 11;
    /// <summary>Signature-resistance multiplier (<c>c_gaSignature = 12</c>,
    /// igc.h:602). Divides a target's detection radius (higher = harder to see).</summary>
    public const int Signature = 12;

    // P467 adds the five weapon-output indices (igc.h:601-613): ammo-projectile
    // speed, energy-weapon lifespan, missile turn rate, gun damage and missile
    // damage. The remaining GAs (mining 17-19, ripcord 20, economy 23-24) stay
    // unwired — see the P446/P467/P476 reports for the full ledger. The station
    // durability indices (4-7) are wired by P475; ship durability (8-10, 13) by
    // P467b; visibility (11-12) by P476.

    /// <summary>Ammo-weapon projectile-speed multiplier (<c>c_gaSpeedAmmo = 14</c>,
    /// igc.h:601). Applied only to a weapon whose <c>cAmmoPerShot</c> ≠ 0.</summary>
    public const int SpeedAmmo = 14;
    /// <summary>Energy-weapon projectile-lifespan multiplier (<c>c_gaLifespanEnergy = 15</c>,
    /// igc.h:602). Applied only to a weapon whose <c>energyPerShot</c> ≠ 0.</summary>
    public const int LifespanEnergy = 15;
    /// <summary>Missile turn-rate multiplier (<c>c_gaTurnRateMissile = 16</c>, igc.h:604).</summary>
    public const int TurnRateMissile = 16;

    // P477 adds the three mining indices (igc.h:610-612): He3-mining rate,
    // He3 yield and He3 capacity. Wired in Ship.EffectiveMiningRate /
    // EffectiveMineCapacity / EffectiveOreCreditPerUnit (miner sim accrual,
    // the cargo-hold capacity used by the mining gate / NPC decision / wire
    // ore-fraction, and the dock-sell payout — see the P477 report).

    /// <summary>He3-mining rate multiplier (<c>c_gaMiningRate = 17</c>,
    /// igc.h:611). Scales ore mined per second at an He3 asteroid
    /// (<c>shipIGC.cpp:2212</c>: <c>dT · GA[c_gaMiningRate]</c>).</summary>
    public const int MiningRate = 17;
    /// <summary>He3-yield multiplier (<c>c_gaMiningYield = 18</c>, igc.h:612).
    /// Scales the credits a miner earns per unit of ore unloaded at a friendly
    /// dock (<c>shipIGC.cpp:1988</c> / <c>shipIGC.h:449</c>:
    /// <c>m_fOre · value · GA[c_gaMiningYield]</c>).</summary>
    public const int MiningYield = 18;
    /// <summary>He3-cargo-capacity multiplier (<c>c_gaMiningCapacity = 19</c>,
    /// igc.h:613). Scales the ore a miner's hold can carry
    /// (<c>shipIGC.cpp:2166,2213</c> / <c>shipIGC.h:331,2249</c>:
    /// <c>capacity = c_fcidCapacityHe3 · GA[c_gaMiningCapacity]</c>).</summary>
    public const int MiningCapacity = 19;
    /// <summary>Gun (projectile) damage multiplier (<c>c_gaDamageGuns = 21</c>, igc.h:612).</summary>
    public const int DamageGuns = 21;
    /// <summary>Missile damage multiplier (<c>c_gaDamageMissiles = 22</c>, igc.h:613).</summary>
    public const int DamageMissiles = 22;

    // P478 adds the two economy indices (igc.h:620-621): development cost and
    // development time. Wired in BucketCatalog.ForSide — a development bucket's
    // money cost and build time are scaled at CREATION by the owning side's GA
    // (the port of native CbucketIGC::Initialize, bucketigc.cpp:70-74), using
    // the civ-base GA the side held at join (native reads the side's GA while
    // CreateBuckets runs, before any development completes).

    /// <summary>Development-cost multiplier (<c>c_gaDevelopmentCost = 23</c>,
    /// igc.h:620). Scales a development bucket's money cost at creation.</summary>
    public const int DevelopmentCost = 23;
    /// <summary>Development-time multiplier (<c>c_gaDevelopmentTime = 24</c>,
    /// igc.h:621). Scales a development bucket's build time at creation.</summary>
    public const int DevelopmentTime = 24;

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
