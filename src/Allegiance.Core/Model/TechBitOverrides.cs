namespace Allegiance.Core.Model;

/// <summary>Per-game admin masks, native CFSMission::MakeOverrideTechBits
/// (FedSrv/fsmission.cpp:2718-2733). Unmentioned bits retain generation defaults.</summary>
public sealed class TechBitOverrides
{
    public const int MaxSides = 6; // Igc/igc.h:507 c_cSidesMax
    public const int MaxBits = TechTreeBitMask.ByteCount * 8;
    private byte[][]? _altered, _newSetting;
    public bool IsAllocated => _altered is not null;

    private void MakeOverrideTechBits()
    {
        if (_altered is not null) return;
        _altered = new byte[MaxSides][];
        _newSetting = new byte[MaxSides][];
        for (int side = 0; side < MaxSides; side++)
        {
            _altered[side] = new byte[TechTreeBitMask.ByteCount];
            _newSetting[side] = new byte[TechTreeBitMask.ByteCount];
        }
    }

    public void Set(int side, int bit, bool setting)
    {
        if (side is < 0 or >= MaxSides) throw new ArgumentOutOfRangeException(nameof(side));
        if (bit is < 0 or >= MaxBits) throw new ArgumentOutOfRangeException(nameof(bit));
        MakeOverrideTechBits();
        byte mask = (byte)(0x80 >> (bit % 8)); // native zlib/mask.h:159-181
        _altered![side][bit / 8] |= mask;
        if (setting) _newSetting![side][bit / 8] |= mask;
        else _newSetting![side][bit / 8] &= (byte)~mask;
    }

    /// <summary>GenerateMission: (altered &amp; setting) | (defaults - altered),
    /// before UpdateInitialTechs/CreateBuckets (Igc/missionigc.cpp:3885-3898).</summary>
    public TechTreeBitMask Apply(int side, TechTreeBitMask defaults)
    {
        if (_altered is null || side is < 0 or >= MaxSides) return defaults;
        var bits = defaults.ToBytes();
        for (int i = 0; i < bits.Length; i++)
            bits[i] = (byte)((_altered[side][i] & _newSetting![side][i]) | (bits[i] & ~_altered[side][i]));
        return new TechTreeBitMask(bits);
    }

    public TechBitOverrides Copy()
    {
        var copy = new TechBitOverrides();
        if (_altered is null) return copy;
        copy.MakeOverrideTechBits();
        for (int side = 0; side < MaxSides; side++)
        {
            _altered[side].CopyTo(copy._altered![side], 0);
            _newSetting![side].CopyTo(copy._newSetting![side], 0);
        }
        return copy;
    }
}
