using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataHullTypeIGC</c> payload for <c>OT_hullType</c> (29). <b>Variable
/// length:</b> a 540-byte base followed by <c>MaxWeapons</c> hardpoint records
/// at stride <b>30</b> (corpus-measured: <c>540 + 30 * maxWeapons</c>, sizes
/// 540/570/600/…/780). Source: <c>src/Igc/igc.h:1767</c>, C++ default
/// alignment. See wiki page <c>allegiance-igc-file-format</c> §OT_hullType.
/// </summary>
public readonly record struct DataHullTypeIGC(
    int Price,
    uint TimeToBuild,
    string ModelName,
    string IconName,
    string Name,
    string Description,
    byte GroupId,
    byte[] TtbmRequired,
    byte[] TtbmEffects,
    float Mass,
    float Signature,
    float Speed,
    float[] MaxTurnRates,       // yaw, pitch, roll
    float[] TurnTorques,        // yaw, pitch, roll
    float Thrust,
    float SideMultiplier,
    float BackMultiplier,
    float ScannerRange,
    float MaxFuel,
    float Ecm,
    float Length,
    float MaxEnergy,
    float RechargeRate,
    float RipcordSpeed,
    float RipcordCost,
    short MaxAmmo,
    short HullId,
    short SuccessorHullId,      // NA = -1
    byte MaxWeapons,
    byte MaxFixedWeapons,
    float HitPoints,
    short HardpointOffset,      // always 540 in shipped files
    byte DefenseType,
    short CapacityMagazine,
    short CapacityDispenser,
    short CapacityChaffLauncher,
    short[] PreferredPartsTypes,   // c_cMaxPreferredPartTypes = 14; NA = -1 terminates
    short HabmCapabilities,
    string TextureName,
    short[] PmEquipment,        // PartMask[ET_MAX = 8]
    short InteriorSound,
    short ExteriorSound,
    short MainThrusterInteriorSound,
    short MainThrusterExteriorSound,
    short ManuveringThrusterInteriorSound,
    short ManuveringThrusterExteriorSound,
    DataHullTypeIGC.Hardpoint[] Hardpoints)
{
    /// <summary>Fixed base payload size in bytes, before the hardpoint array.</summary>
    public const int Size = 540;
    /// <summary>Stride between consecutive hardpoints, corpus-measured (not 29).</summary>
    public const int HardpointStride = 30;

    /// <summary><c>c_habmLifepod = 0x04</c> (<c>src/Igc/igc.h:728</c>) — the hull
    /// ability bit marking an eject-pod hull, read by
    /// <see cref="HasCapability"/> to decide whether a dying ship is already a
    /// lifepod (P107's second kill is final). The full ability-bit list lives at
    /// <c>src/Igc/igc.h:726-741</c>; only the lifepod bit is needed here.</summary>
    public const short HabmLifepod = 0x04;

    /// <summary><c>c_habmNoRipcord = 0x20</c> (<c>src/Igc/igc.h:731</c>) — the
    /// hull ability bit marking a hull that cannot ripcord at all (the P111
    /// gate, mirroring P107's read of <c>c_habmLifepod</c>).</summary>
    public const short HabmNoRipcord = 0x20;

    /// <summary><c>c_habmIsRipcordTarget = 0x40</c> (<c>src/Igc/igc.h:732</c>) —
    /// a hull that can be ripped TO (a valid ripcord anchor if it is a ship).</summary>
    public const short HabmIsRipcordTarget = 0x40;

    /// <summary><c>c_habmIsLtRipcordTarget = 0x1000</c> (<c>src/Igc/igc.h:738</c>)
    /// — the light-ripcord twin of <c>c_habmIsRipcordTarget</c>, required
    /// (together with it) when the ripper's own hull can light-ripcord.</summary>
    public const short HabmIsLtRipcordTarget = 0x1000;

    /// <summary><c>c_habmCanLtRipcord = 0x2000</c> (<c>src/Igc/igc.h:739</c>) —
    /// a hull whose pilot can use light-ripcord to a lighter ship target.</summary>
    public const short HabmCanLtRipcord = 0x2000;


    /// <summary>
    /// <c>IhullTypeIGC::HasCapability(habm)</c>
    /// (<c>src/Igc/hullTypeIGC.h:243-246</c>:
    /// <c>return (m_data-&gt;habmCapabilities &amp; habm) != 0</c>) — a bit test
    /// on <see cref="HabmCapabilities"/>. The port already decodes that field
    /// (<c>DataHullTypeIGC.cs:51,169</c>); this is the missing predicate the
    /// death/eject gate needs.
    /// </summary>
    public bool HasCapability(short bit) => (HabmCapabilities & bit) != 0;

    /// <summary>
    /// One <c>HardpointData</c> (<c>src/Igc/igc.h:1696</c>), 30 bytes unpacked.
    /// </summary>
    public readonly record struct Hardpoint(
        short InteriorSound,
        short TurnSound,
        string FrameName,
        string LocationAbreviation,
        short PartMask,
        bool Fixed);

    /// <summary>
    /// Decode an <c>OT_hullType</c> payload.
    /// </summary>
    /// <remarks>
    /// Corpus-measured: <c>record.Size == 540 + 30 * maxWeapons</c> (the stride
    /// is 30, not the 29 the struct's commented-out <c>minDot</c> field suggests).
    /// </remarks>
    /// <param name="b">Exactly <c>540 + 30 * maxWeapons</c> bytes of payload.</param>
    /// <exception cref="InvalidDataException">Wrong total size for the declared <c>MaxWeapons</c>.</exception>
    public static DataHullTypeIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < Size)
            throw new InvalidDataException(
                $"DataHullTypeIGC needs at least {Size} bytes, got {b.Length}");

        var maxWeapons = b[450];
        var expected = Size + HardpointStride * maxWeapons;
        if (b.Length != expected)
            throw new InvalidDataException(
                $"DataHullTypeIGC: maxWeapons={maxWeapons} implies {expected} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));

        var turnRates = new float[3];
        var turnTorques = new float[3];
        for (var i = 0; i < 3; i++)
        {
            turnRates[i] = BinaryPrimitives.ReadSingleLittleEndian(b.Slice(376 + 4 * i));
            turnTorques[i] = BinaryPrimitives.ReadSingleLittleEndian(b.Slice(388 + 4 * i));
        }

        var preferred = new short[14];
        for (var i = 0; i < 14; i++)
            preferred[i] = BinaryPrimitives.ReadInt16LittleEndian(b.Slice(466 + 2 * i));

        var pmEquipment = new short[8];
        for (var i = 0; i < 8; i++)
            pmEquipment[i] = BinaryPrimitives.ReadInt16LittleEndian(b.Slice(509 + 2 * i));

        var hardpoints = new Hardpoint[maxWeapons];
        for (var i = 0; i < maxWeapons; i++)
        {
            var h = b.Slice(Size + HardpointStride * i, HardpointStride);
            hardpoints[i] = new Hardpoint(
                InteriorSound: BinaryPrimitives.ReadInt16LittleEndian(h[0..]),
                TurnSound: BinaryPrimitives.ReadInt16LittleEndian(h[2..]),
                FrameName: Fix(h, 4, 13),
                LocationAbreviation: Fix(h, 17, 9),
                PartMask: BinaryPrimitives.ReadInt16LittleEndian(h[26..]),
                Fixed: h[28] != 0);
        }

        return new DataHullTypeIGC(
            Price: BinaryPrimitives.ReadInt32LittleEndian(b[0..]),
            TimeToBuild: BinaryPrimitives.ReadUInt32LittleEndian(b[4..]),
            ModelName: Fix(b, 8, 14),
            IconName: Fix(b, 22, 13),
            Name: Fix(b, 35, 25),
            Description: Fix(b, 60, 201),
            GroupId: b[261],
            TtbmRequired: b.Slice(262, 50).ToArray(),
            TtbmEffects: b.Slice(312, 50).ToArray(),
            Mass: BinaryPrimitives.ReadSingleLittleEndian(b[364..]),
            Signature: BinaryPrimitives.ReadSingleLittleEndian(b[368..]),
            Speed: BinaryPrimitives.ReadSingleLittleEndian(b[372..]),
            MaxTurnRates: turnRates,
            TurnTorques: turnTorques,
            Thrust: BinaryPrimitives.ReadSingleLittleEndian(b[400..]),
            SideMultiplier: BinaryPrimitives.ReadSingleLittleEndian(b[404..]),
            BackMultiplier: BinaryPrimitives.ReadSingleLittleEndian(b[408..]),
            ScannerRange: BinaryPrimitives.ReadSingleLittleEndian(b[412..]),
            MaxFuel: BinaryPrimitives.ReadSingleLittleEndian(b[416..]),
            Ecm: BinaryPrimitives.ReadSingleLittleEndian(b[420..]),
            Length: BinaryPrimitives.ReadSingleLittleEndian(b[424..]),
            MaxEnergy: BinaryPrimitives.ReadSingleLittleEndian(b[428..]),
            RechargeRate: BinaryPrimitives.ReadSingleLittleEndian(b[432..]),
            RipcordSpeed: BinaryPrimitives.ReadSingleLittleEndian(b[436..]),
            RipcordCost: BinaryPrimitives.ReadSingleLittleEndian(b[440..]),
            MaxAmmo: BinaryPrimitives.ReadInt16LittleEndian(b[444..]),
            HullId: BinaryPrimitives.ReadInt16LittleEndian(b[446..]),
            SuccessorHullId: BinaryPrimitives.ReadInt16LittleEndian(b[448..]),
            MaxWeapons: maxWeapons,
            MaxFixedWeapons: b[451],
            HitPoints: BinaryPrimitives.ReadSingleLittleEndian(b[452..]),
            HardpointOffset: BinaryPrimitives.ReadInt16LittleEndian(b[456..]),
            DefenseType: b[458],
            CapacityMagazine: BinaryPrimitives.ReadInt16LittleEndian(b[460..]),
            CapacityDispenser: BinaryPrimitives.ReadInt16LittleEndian(b[462..]),
            CapacityChaffLauncher: BinaryPrimitives.ReadInt16LittleEndian(b[464..]),
            PreferredPartsTypes: preferred,
            HabmCapabilities: BinaryPrimitives.ReadInt16LittleEndian(b[494..]),
            TextureName: Fix(b, 496, 13),
            PmEquipment: pmEquipment,
            InteriorSound: BinaryPrimitives.ReadInt16LittleEndian(b[526..]),
            ExteriorSound: BinaryPrimitives.ReadInt16LittleEndian(b[528..]),
            MainThrusterInteriorSound: BinaryPrimitives.ReadInt16LittleEndian(b[530..]),
            MainThrusterExteriorSound: BinaryPrimitives.ReadInt16LittleEndian(b[532..]),
            ManuveringThrusterInteriorSound: BinaryPrimitives.ReadInt16LittleEndian(b[534..]),
            ManuveringThrusterExteriorSound: BinaryPrimitives.ReadInt16LittleEndian(b[536..]),
            Hardpoints: hardpoints);
    }
}
