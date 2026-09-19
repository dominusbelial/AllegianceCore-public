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

    /// <summary><c>c_habmBoard = 0x01</c> (<c>src/Igc/igc.h:726</c>) — the hull
    /// ability bit that lets a ship dock with an ENEMY station and capture it
    /// (the P126 hostile-capture gate, checked in
    /// <c>Sector.ResolveDockCollisions</c> — the deferred branch P110's own
    /// doc comment flagged at <c>FedSrv.CPP:13100-13105</c>). Read by
    /// <see cref="HasCapability"/> the same one-line way the sibling
    /// <c>Habm*</c> bits are.</summary>
    public const short HabmBoard = 0x01;

    /// <summary><c>c_habmFighter = 0x80</c> (<c>src/Igc/igc.h:733</c>) — the hull
    /// ability bit marking a fighter-class hull. P263 reads it to pick the
    /// explosion category of a dying ship: a fighter renders the small-ship blast
    /// (<c>c_etSmallShip</c>), anything else the big-ship blast
    /// (<c>c_etBigShip</c>) — exactly the native client's choice on KILL_SHIP
    /// (<c>appmsg.cpp:2853-2857</c>).</summary>
    public const short HabmFighter = 0x80;

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

    /// <summary><c>c_habmRescue = 0x02</c> (<c>src/Igc/igc.h:727</c>) — a hull
    /// able to pick up a friendly <c>c_habmLifepod</c> eject pod (the
    /// <c>PLAYER_RESCUED</c> trigger's capability gate, paired with
    /// <c>c_habmLifepod</c> on the pod). Decoded by
    /// <see cref="HasCapability"/>; read by the collision dispatch at
    /// <c>shipIGC.cpp:1031-1043</c>. No landing-bay geometry involved.</summary>
    public const short HabmRescue = 0x02;

    /// <summary><c>c_habmLandOnCarrier = 0x10</c> (<c>src/Igc/igc.h:730</c>) —
    /// a hull allowed to land in another ship's landing bay (the lander side of
    /// <c>RELAUNCH_SHIP</c>'s capability gate, paired with
    /// <c>c_habmCarrier</c> on the hosting ship). Gate checked before
    /// <c>InGarage</c> at <c>shipIGC.cpp:1038-1044</c>.</summary>
    public const short HabmLandOnCarrier = 0x10;

    /// <summary><c>c_habmCarrier = 0x400</c> (<c>src/Igc/igc.h:736</c>) — a hull
    /// that is itself a carrier: it hosts a landing bay other ships fly into
    /// (the <c>RELAUNCH_SHIP</c> hosting side, <c>shipIGC.cpp:1034-1047</c>).
    /// Carrier landing-bay geometry is <b>not</b> part of this
    /// <c>OT_hullType</c> record (see the native struct,
    /// <c>src/Igc/igc.h:1767-1820</c>); it lives in the hull's <c>.cvh</c>
    /// model frames instead — see <c>HullLandingBay</c>.</summary>
    public const short HabmCarrier = 0x400;

    /// <summary><c>c_habmLeadIndicator = 0x800</c> (<c>src/Igc/igc.h:737</c>) — the
    /// hull ability bit that grants its pilot the predictive LEAD AIM indicator
    /// (P266): the reticle, drawn at the solved lead point of a selected moving
    /// target, that shows where to fire so a shot intercepts it. This is the
    /// primary gate native's client uses before drawing the lead surface
    /// (<c>WinTrek/indicator.cpp:254-259</c>: <c>pship-&gt;GetBaseHullType()
    /// -&gt;HasCapability(c_habmLeadIndicator)</c>), with the friendly-relay
    /// scanner exception (<see cref="HabmRemoteLeadIndicator"/>) when the hull
    /// lacks it. Fighters and interceptors carry this bit; most bombers/transports
    /// do not (they get the relay-assisted version or none).</summary>
    public const short HabmLeadIndicator = 0x800;

    /// <summary><c>c_habmRemoteLeadIndicator = 0x100</c> (<c>src/Igc/igc.h:734</c>)
    /// — the hull ability bit that lets a FRIENDLY SHIP act as a lead-indicator
    /// relay for other pilots who lack <see cref="HabmLeadIndicator"/>: if such a
    /// relay ship (with no parent, i.e. not itself boarded) has the target in its
    /// scanner range, the lacking pilot still gets the lead reticle
    /// (<c>WinTrek/indicator.cpp:294-315</c>). The station twin is
    /// <c>DataStationTypeIGC.SabmRemoteLeadIndicator</c>.</summary>
    public const short HabmRemoteLeadIndicator = 0x100;


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
    /// Whether a given part can mount on <em>some</em> hardpoint of this hull
    /// — the P276 "connector/hardpoint compatibility" test, a faithful port of
    /// <c>IhullTypeIGC::CanMount(IpartTypeIGC*, Mount)</c> at
    /// <c>src/Igc/hullTypeIGC.h:199-213</c> combined with
    /// <c>GetPartMask(EquipmentType, Mount)</c> at
    /// <c>src/Igc/hullTypeIGC.h:163-176</c>. A part fits a mount when every bit
    /// of the part's own <c>partMask</c> is covered by the mount's mask
    /// (<c>(partMask &amp; ~mountMask) == 0</c>).
    ///
    /// <para><b>Weapons</b> are per-hardpoint: the hull carries up to
    /// <see cref="MaxWeapons"/> <see cref="Hardpoint"/> records, each with its
    /// own <c>partMask</c>, and a weapon fits if at least one hardpoint
    /// accepts it. <b>Every other equipment type</b> has a single mount (mount
    /// 0) whose mask is the hull's <c>pmEquipment[et]</c> table
    /// (<c>PartMask pmEquipment[ET_MAX]</c>, <c>src/Igc/igc.h:1809</c>), so a
    /// non-weapon part fits iff its <c>partMask</c> is covered by
    /// <see cref="PmEquipment"/> at the part's <see cref="EquipmentType"/>
    /// index (0..7 = <c>ET_MAX</c>, <c>src/Igc/igc.h:530-539</c>).</para>
    /// </summary>
    public bool CanMountPart(in DataPartTypeIGC part)
    {
        var et = part.EquipmentType;
        if (et == EquipmentType.Weapon)
        {
            foreach (var hp in Hardpoints)
                if ((part.PartMask & ~hp.PartMask) == 0) return true;
            return false;
        }
        int idx = (int)et;
        if ((uint)idx >= (uint)PmEquipment.Length) return false;
        return (part.PartMask & ~PmEquipment[idx]) == 0;
    }

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
