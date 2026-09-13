using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// <c>DataStationTypeIGC</c> payload for <c>OT_stationType</c> (31). Fixed
/// 524 bytes. Source layout: <c>DataBuyableIGC</c> (<c>src/Igc/igc.h:1753</c>)
/// + <c>DataStationTypeIGC</c> (<c>igc.h:2649</c>), C++ default alignment. See
/// wiki page <c>allegiance-igc-file-format</c> §OT_stationType.
/// </summary>
/// <remarks>
/// Alignment matters twice. First, the DataBuyableIGC base ends at 362; two
/// pad bytes put <c>signature</c>@364. Second, <c>classID</c> is a single byte
/// at 456, so one pad byte follows before the even-aligned
/// <c>constructionDroneTypeID</c>@458. Everything after is even-aligned
/// (13 SoundIDs, then 13 texture bytes and 25 name bytes). All float and short
/// offsets are direct — no other narrow/wide transitions exist. Offsets were
/// verified against all 12,659 corpus records.
/// </remarks>
public readonly record struct DataStationTypeIGC(
    int Price,
    uint TimeToBuild,
    string ModelName,
    string IconName,
    string Name,
    string Description,
    byte GroupId,
    byte[] TtbmRequired,
    byte[] TtbmEffects,
    float Signature,
    float MaxArmorHitPoints,
    float MaxShieldHitPoints,
    float ArmorRegeneration,
    float ShieldRegeneration,
    float ScannerRange,
    int Income,
    float Radius,
    byte[] TtbmLocal,
    short StationTypeId,
    short SuccessorStationTypeId,
    byte DefenseTypeArmor,
    byte DefenseTypeShield,
    short SabmCapabilities,
    short AabmBuild,
    byte ClassId,
    short ConstructionDroneTypeId,
    short ConstructorNeedRockSound,
    short ConstructorUnderAttackSound,
    short ConstructorDestroyedSound,
    short CompletionSound,
    short InteriorSound,
    short ExteriorSound,
    short InteriorAlertSound,
    short UnderAttackSound,
    short CriticalSound,
    short DestroyedSound,
    short CapturedSound,
    short EnemyCapturedSound,
    short EnemyDestroyedSound,
    string TextureName,
    string BuilderName)
{
    /// <summary>Exact payload size in bytes: <c>sizeof(DataStationTypeIGC)</c>.</summary>
    public const int Size = 524;

    /// <summary><c>c_sabmUnload = 0x01</c> (<c>src/Igc/igc.h:744</c>) — the
    /// station-ability bit saying mined minerals can be offloaded here. P156's
    /// miner decision cycle (the sell leg of the autonomous NPC economy loop)
    /// searches for a friendly <c>SabmUnload</c>-capable station via the P149
    /// <c>FindNearestFriendlyStation</c> primitive, exactly the way P151 already
    /// passes <see cref="SabmLand"/>/<see cref="SabmRipcord"/>. Added the same
    /// one-line way this type already exposes
    /// <see cref="SabmRestart"/>/<see cref="SabmRipcord"/>.</summary>
    public const short SabmUnload = 0x01;

    /// <summary><c>c_sabmRestart = 0x04</c> (<c>src/Igc/igc.h:746</c>) — the
    /// station-ability bit saying a player may restart/rearm here after
    /// dying. The P110 teleport-dock validation needs it (a station the
    /// player cannot start at rejects a teleport request,
    /// <c>FedSrv.CPP:7065</c>). The full bit list lives at
    /// <c>src/Igc/igc.h:744-759</c>; only the restart bit is exposed here.</summary>
    public const short SabmRestart = 0x04;

    /// <summary><c>c_sabmRipcord = 0x08</c> (<c>src/Igc/igc.h:747</c>) — the
    /// station-ability bit saying a ripcord countdown can anchor on this
    /// station ("teleport to the station"). The P111 ripcord-anchor gate reads
    /// it, alongside the friendly-side gate.</summary>
    public const short SabmRipcord = 0x08;

    /// <summary><c>c_sabmLand = 0x20</c> (<c>src/Igc/igc.h:749</c>) — the
    /// station-ability bit saying a ship may land at this station. P151's
    /// take-cover search (the native <c>GetLocalStationToHide</c>,
    /// <c>src/Igc/shipIGC.h:2181-2212</c>) prefers a land-capable local
    /// station to hide at when an NPC is too damaged to fight on; added the
    /// same one-line way this type already exposes
    /// <see cref="SabmRestart"/>/<see cref="SabmRipcord"/>.</summary>
    public const short SabmLand = 0x20;

    /// <summary><c>c_sabmFlag = 0x200</c> (<c>src/Igc/igc.h:753</c>, the
    /// &quot;counts for victory&quot; bit) — the station-ability bit behind the
    /// native <c>missionigc.cpp:280-281</c> <c>&quot;IsVictoryObjective&quot;</c>
    /// capability. P191's conquest recompute
    /// (<see cref="Model.Mission.RecomputeConquestAndTerritory"/> — the port of
    /// <c>CFSMission::CheckForVictoryByStationKill</c>,
    /// <c>fsmission.cpp:3595-3600</c>) counts only stations whose type
    /// <see cref="HasCapability(SabmFlag)"/>. This bit was <b>not</b> previously
    /// decoded in this port (only Unload/Restart/Ripcord/Land/Pedestal were —
    /// confirmed against the live source this task); P191 adds it the same
    /// one-line way the others were exposed.</summary>
    public const short SabmFlag = 0x200;

    /// <summary><c>c_sabmPedestal = 0x400</c> (<c>src/Igc/igc.h:758</c>) — the
    /// station-ability bit saying a station is a flag pedestal and therefore
    /// <b>indestructible</b> (<c>stationIGC.cpp:127</c>: the
    /// <c>CstationIGC::ReceiveDamage</c> invulnerability gate). The P125 station
    /// combat model reads it (alongside the friendly-fire gate) via
    /// <see cref="HasCapability"/>. Added the same one-line way P114 exposed the
    /// hull <c>Habm</c> bits and this type already exposes
    /// <see cref="SabmRestart"/>/<see cref="SabmRipcord"/>.</summary>
    public const short SabmPedestal = 0x400;


    /// <summary>
    /// <c>StationTypeIGC::HasCapability(sabm)</c>
    /// (<c>src/Igc/stationtypeigc.h:168-170</c>:
    /// <c>return (m_data.sabmCapabilities &amp; sabm) != 0</c>) — a bit test
    /// on <see cref="SabmCapabilities"/>, the field this port already decodes
    /// (<c>DataStationTypeIGC.cs:104</c>). The missing predicate the P110
    /// teleport-dock gate needs.
    /// </summary>
    public bool HasCapability(short bit) => (SabmCapabilities & bit) != 0;


    /// <summary>
    /// Decode an <c>OT_stationType</c> payload.
    /// </summary>
    /// <param name="b">Exactly <see cref="Size"/> bytes of payload.</param>
    /// <exception cref="InvalidDataException"><paramref name="b"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static DataStationTypeIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size)
            throw new InvalidDataException(
                $"DataStationTypeIGC expects {Size} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));

        return new DataStationTypeIGC(
            Price: BinaryPrimitives.ReadInt32LittleEndian(b[0..]),
            TimeToBuild: BinaryPrimitives.ReadUInt32LittleEndian(b[4..]),
            ModelName: Fix(b, 8, 14),
            IconName: Fix(b, 22, 13),
            Name: Fix(b, 35, 25),
            Description: Fix(b, 60, 201),
            GroupId: b[261],
            TtbmRequired: b.Slice(262, 50).ToArray(),
            TtbmEffects: b.Slice(312, 50).ToArray(),
            Signature: BinaryPrimitives.ReadSingleLittleEndian(b[364..]),
            MaxArmorHitPoints: BinaryPrimitives.ReadSingleLittleEndian(b[368..]),
            MaxShieldHitPoints: BinaryPrimitives.ReadSingleLittleEndian(b[372..]),
            ArmorRegeneration: BinaryPrimitives.ReadSingleLittleEndian(b[376..]),
            ShieldRegeneration: BinaryPrimitives.ReadSingleLittleEndian(b[380..]),
            ScannerRange: BinaryPrimitives.ReadSingleLittleEndian(b[384..]),
            Income: BinaryPrimitives.ReadInt32LittleEndian(b[388..]),
            Radius: BinaryPrimitives.ReadSingleLittleEndian(b[392..]),
            TtbmLocal: b.Slice(396, 50).ToArray(),
            StationTypeId: BinaryPrimitives.ReadInt16LittleEndian(b[446..]),
            SuccessorStationTypeId: BinaryPrimitives.ReadInt16LittleEndian(b[448..]),
            DefenseTypeArmor: b[450],
            DefenseTypeShield: b[451],
            SabmCapabilities: BinaryPrimitives.ReadInt16LittleEndian(b[452..]),
            AabmBuild: BinaryPrimitives.ReadInt16LittleEndian(b[454..]),
            ClassId: b[456],
            ConstructionDroneTypeId: BinaryPrimitives.ReadInt16LittleEndian(b[458..]),
            ConstructorNeedRockSound: BinaryPrimitives.ReadInt16LittleEndian(b[460..]),
            ConstructorUnderAttackSound: BinaryPrimitives.ReadInt16LittleEndian(b[462..]),
            ConstructorDestroyedSound: BinaryPrimitives.ReadInt16LittleEndian(b[464..]),
            CompletionSound: BinaryPrimitives.ReadInt16LittleEndian(b[466..]),
            InteriorSound: BinaryPrimitives.ReadInt16LittleEndian(b[468..]),
            ExteriorSound: BinaryPrimitives.ReadInt16LittleEndian(b[470..]),
            InteriorAlertSound: BinaryPrimitives.ReadInt16LittleEndian(b[472..]),
            UnderAttackSound: BinaryPrimitives.ReadInt16LittleEndian(b[474..]),
            CriticalSound: BinaryPrimitives.ReadInt16LittleEndian(b[476..]),
            DestroyedSound: BinaryPrimitives.ReadInt16LittleEndian(b[478..]),
            CapturedSound: BinaryPrimitives.ReadInt16LittleEndian(b[480..]),
            EnemyCapturedSound: BinaryPrimitives.ReadInt16LittleEndian(b[482..]),
            EnemyDestroyedSound: BinaryPrimitives.ReadInt16LittleEndian(b[484..]),
            TextureName: Fix(b, 486, 13),
            BuilderName: Fix(b, 499, 25));
    }
}
