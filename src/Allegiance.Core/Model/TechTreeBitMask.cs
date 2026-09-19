namespace Allegiance.Core.Model;

/// <summary>
/// A 400-bit technology-tree bitmask — the port of the native
/// <c>TechTreeBitMask</c> type (<c>src/Igc/igc.h:768</c>,
/// <c>typedef TLargeBitMask&lt;c_ttbMax&gt;</c>, with <c>c_ttbMax = 400</c>,
/// <c>igc.h:578</c>) backed by the raw 50-byte array this port already decodes
/// everywhere (<see cref="DataDevelopmentIGC.TtbmRequired"/> /
/// <see cref="DataDevelopmentIGC.TtbmEffects"/> /
/// <see cref="DataCivilizationIGC.TtbmBaseTechs"/> /
/// <see cref="DataCivilizationIGC.TtbmNoDevTechs"/>). The native storage is
/// <c>uint8_t m_bits[((nBits - 1) / 8) + 1]</c> (<c>src/zlib/mask.h:351</c>) →
/// <c>[((400-1)/8)+1] = [50]</c> raw bytes, MSB-first per byte exactly as
/// <c>SetBit</c>/<c>GetBit</c> (<c>mask.h:159-181</c>) lay them out.
/// </summary>
/// <remarks>
/// Only the operators this task's tech-tree slice actually exercises are
/// ported, each a faithful byte-for-byte transcription of the native member:
/// <list type="bullet">
/// <item><see cref="Or"/> — byte-wise <c>|=</c> (<c>mask.h:235-241</c>), the
/// port of <c>ApplyDevelopmentTechs</c>'s <c>m_data.ttbmDevelopmentTechs |= ttbm</c>
/// (<c>sideigc.h:201</c>).</item>
/// <item><see cref="IsSubsetOf"/> — the <c>&lt;=</c> subset test
/// (<c>mask.h:300-313</c>: <c>(m_bits[i] &amp; tbm.m_bits[i]) != m_bits[i]</c>),
/// the exact comparison <c>CanBuy</c> and <c>ApplyDevelopmentTechs</c> use
/// (<c>sideigc.h:197,241</c>) and <c>IsObsolete</c> uses
/// (<c>developmentigc.h:112</c>).</item>
/// <item>equality — the <c>==</c> byte comparison (<c>mask.h:269</c>), used by
/// <see cref="Side"/>/native's <c>SetDevelopmentTechs</c> change gate.</item>
/// </list>
/// Immutable: every producing member returns a fresh mask backed by a fresh
/// 50-byte array, so no caller can mutate another's bits through aliasing.
/// </remarks>
public readonly struct TechTreeBitMask : IEquatable<TechTreeBitMask>
{
    /// <summary>Wire width: <c>[(c_ttbMax - 1) / 8] + 1</c> with
    /// <c>c_ttbMax = 400</c> = 50 bytes (<c>zlib/mask.h:351</c>).</summary>
    public const int ByteCount = 50;

    private readonly byte[] _bits;

    /// <summary>Wrap an existing 50-byte bitmap (cloned so the caller's array
    /// stays private). Throws if <paramref name="bits"/> is not exactly
    /// <see cref="ByteCount"/> bytes.</summary>
    public TechTreeBitMask(byte[] bits)
    {
        if (bits is null || bits.Length != ByteCount)
            throw new ArgumentException($"TechTreeBitMask needs exactly {ByteCount} bytes, got {bits?.Length ?? -1}");
        _bits = (byte[])bits.Clone();
    }

    /// <summary>The all-clear mask (<c>ClearAll</c>, <c>mask.h:149-152</c>).</summary>
    public static TechTreeBitMask Empty { get; } = new TechTreeBitMask(new byte[ByteCount]);

    /// <summary>Wrap a <see cref="ReadOnlySpan{T}"/> of exactly
    /// <see cref="ByteCount"/> bytes.</summary>
    public static TechTreeBitMask FromBytes(ReadOnlySpan<byte> bits)
    {
        if (bits.Length != ByteCount)
            throw new ArgumentException($"TechTreeBitMask needs exactly {ByteCount} bytes, got {bits.Length}");
        var arr = new byte[ByteCount];
        bits.CopyTo(arr);
        return new TechTreeBitMask(arr);
    }

    /// <summary>The raw 50 bytes as a read-only span (owned by this mask).</summary>
    public ReadOnlySpan<byte> AsSpan() => _bits;

    /// <summary>A defensive 50-byte copy of the bitmap.</summary>
    public byte[] ToBytes() => (byte[])_bits.Clone();

    /// <summary>True when every bit is clear (all 50 bytes zero).</summary>
    public bool IsEmpty
    {
        get
        {
            foreach (var b in _bits)
                if (b != 0) return false;
            return true;
        }
    }

    /// <summary>Count of set bits (popcount over the 50 bytes).</summary>
    public int PopCount
    {
        get
        {
            int n = 0;
            foreach (var b in _bits) n += System.Numerics.BitOperations.PopCount(b);
            return n;
        }
    }

    /// <summary>The byte-wise OR — the port of the native <c>operator |=</c>
    /// (<c>mask.h:235-241</c>), i.e. the <c>|</c> used by
    /// <see cref="Side"/>.ApplyDevelopmentTechs.</summary>
    public TechTreeBitMask Or(TechTreeBitMask other)
    {
        var r = new byte[ByteCount];
        for (int i = 0; i < ByteCount; i++)
            r[i] = (byte)(_bits[i] | other._bits[i]);
        return new TechTreeBitMask(r);
    }

    /// <summary>
    /// True when every bit set in this mask is also set in
    /// <paramref name="other"/> — the port of the native <c>&lt;=</c>
    /// operator (<c>mask.h:300-313</c>: <c>(m_bits[i] &amp; tbm.m_bits[i]) ==
    /// m_bits[i]</c> for every byte). This is the exact subset test the native
    /// tech tree uses for both buyability (<c>ttbmRequired &lt;= ttbmSide</c>,
    /// <c>sideigc.h:241</c>) and new-tech detection
    /// (<c>!(ttbm &lt;= ttbmDevelopmentTechs)</c>, <c>sideigc.h:197</c>), and
    /// for <c>IsObsolete</c> (<c>developmentigc.h:112</c>).
    /// </summary>
    public bool IsSubsetOf(TechTreeBitMask other)
    {
        for (int i = 0; i < ByteCount; i++)
            if ((_bits[i] & other._bits[i]) != _bits[i])
                return false;
        return true;
    }

    public bool Equals(TechTreeBitMask other) =>
        _bits.AsSpan().SequenceEqual(other._bits);

    public override bool Equals(object? obj) =>
        obj is TechTreeBitMask m && Equals(m);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var b in _bits) h.Add(b);
        return h.ToHashCode();
    }

    public static bool operator ==(TechTreeBitMask a, TechTreeBitMask b) => a.Equals(b);
    public static bool operator !=(TechTreeBitMask a, TechTreeBitMask b) => !a.Equals(b);

    public override string ToString() =>
        string.Join("", _bits.Select(b => b.ToString("X2")));

    /// <summary>
    /// The port's single CanBuy containment — true when every tech bit set in
    /// the buyable's required mask <paramref name="need"/> is also set in the
    /// union of a station's local tech mask <paramref name="local"/> and the
    /// side's development techs <paramref name="sideDev"/>:
    /// <c>need ⊆ (local | sideDev)</c>. This is the exact comparison native
    /// <c>CanBuy</c> performs (<c>stationIGC.h:383-391</c>:
    /// <c>b-&gt;GetRequiredTechs() &lt;= (m_myStationType.GetLocalTechs() | side.GetTechs())</c>)
    /// and the one P283 wires into <c>Server.StationSellsHull</c> — shared here
    /// in <c>Allegiance.Core</c> so both the server's buy-gate and the
    /// MonoGame client's docked-shop filter consume one implementation rather
    /// than two (P284 reuses P283's exact bitmask math instead of re-deriving
    /// it). <paramref name="need"/> is the 50-byte <c>TtbmRequired</c> the port
    /// decodes on <see cref="DataHullTypeIGC"/>/<see cref="DataPartTypeIGC"/>,
    /// <paramref name="local"/> the 50-byte <c>TtbmLocal</c> on
    /// <see cref="DataStationTypeIGC"/>, and <paramref name="sideDev"/> the
    /// side's runtime <c>DevelopmentTechs</c>. Both byte masks may be shorter
    /// than 50 (defensive) and are treated as all-clear beyond their length,
    /// so an absent/unresolvable station or side never blanks a buyable.
    /// </summary>
    public static bool RequiredTechsBuyable(byte[]? need, byte[]? local, TechTreeBitMask sideDev)
    {
        var dev = sideDev.AsSpan();
        int n = Math.Max(Math.Max(need?.Length ?? 0, local?.Length ?? 0), dev.Length);
        for (int i = 0; i < n; i++)
        {
            byte rb = i < (need?.Length ?? 0) ? need![i] : (byte)0;
            byte lb = i < (local?.Length ?? 0) ? local![i] : (byte)0;
            byte db = i < dev.Length ? dev[i] : (byte)0;
            if ((rb & ~(lb | db)) != 0) return false;
        }
        return true;
    }
}
