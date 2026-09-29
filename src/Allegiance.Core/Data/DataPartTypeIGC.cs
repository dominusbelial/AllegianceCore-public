using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary><c>EquipmentType</c> from <c>src/Igc/igc.h:530-539</c>. Underlying
/// type is <c>short</c>; a part type record's discriminated subtype is chosen
/// by this value (<c>src/Igc/partTypeIGC.cpp:65</c> names it "PartTypeID").</summary>
public enum EquipmentType : short
{
    ChaffLauncher = 0,
    Weapon = 1,
    Magazine = 2,
    Dispenser = 3,
    Shield = 4,
    Cloak = 5,
    Pack = 6,
    Afterburner = 7,
}

/// <summary>
/// <c>DataPartTypeIGC</c> payload for <c>OT_partType</c> (30). Variable length
/// via subtype dispatch. The record is read whole by <c>CpartTypeIGC::Initialize</c>,
/// which decides <c>ClauncherTypeIGC</c> (24 B) vs <c>CpartTypeIGC</c> purely by
/// record size (<c>src/Igc/missionigc.cpp:3344-3355</c>); the part base and its
/// derived subtypes are then distinguished by <see cref="EquipmentType"/>.
/// Source: <c>src/Igc/igc.h:1822-1889</c>, C++ default alignment. See wiki page
/// <c>allegiance-igc-file-format</c> §OT_partType.
/// </summary>
/// <remarks>
/// Corpus-measured sizes (only these occur among 22,439 records), each mapped
/// to its subtype by <c>equipmentType</c>@376 and cross-checked against the
/// C++ struct arithmetic:
/// <list type="bullet">
/// <item>24 <c>DataLauncherTypeIGC</c> — handled by <see cref="DataLauncherTypeIGC"/>, not here;</item>
/// <item>400 <c>DataPackTypeIGC</c> — <c>equipmentType == Pack</c>;</item>
/// <item>412 <c>DataShieldTypeIGC</c> — <c>equipmentType == Shield</c>;</item>
/// <item>416 <c>DataCloakTypeIGC</c> <i>or</i> <c>DataAfterburnerTypeIGC</c> — shared size;</item>
/// <item>424 <c>DataWeaponTypeIGC</c> — <c>equipmentType == Weapon</c>.</item>
/// </list>
/// The spec's generic <c>DataPartTypeIGC</c> base is 396 bytes, but <b>no
/// 396-byte record exists in the corpus</b> — every observed record carries a
/// subtype tail. <c>equipmentType</c>@376 is a 2-byte <c>short</c>; <c>partMask</c>
///@378 is the next short; the 1-byte <c>groupID</c>@261 and 1-byte
/// <c>defenseType</c>@404 (uchar) each precede an intra-struct pad byte.
/// </remarks>
public readonly record struct DataPartTypeIGC(
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
    short PartId,
    short SuccessorPartId,
    short PartMask,
    string InventoryLineMdl,
    DataPartTypeIGC.Subtype Type)
{
    /// <summary>Base payload size before the subtype tail: <c>sizeof(DataPartTypeIGC)</c>.</summary>
    public const int Size = 396;

    /// <summary>Convenience: the <see cref="EquipmentType"/> carried by the subtype.</summary>
    public EquipmentType EquipmentType => Type.EquipmentType;

    /// <summary>The discriminant carrier for the part subtype tails (weapon /
    /// shield / cloak / afterburner / pack). Pattern-match on these to get at
    /// the subtype-specific fields.</summary>
    public abstract record Subtype
    {
        public abstract EquipmentType EquipmentType { get; }
    }

    /// <summary><c>DataWeaponTypeIGC</c> (<c>src/Igc/igc.h:1833</c>), 424 bytes.</summary>
    public sealed record Weapon(
        float DtimeReady,
        float DtimeBurst,
        float EnergyPerShot,
        float Dispersion,
        short CAmmoPerShot,
        short ProjectileTypeId,
        short ActivateSound,
        short SingleShotSound,
        short BurstSound) : Subtype
    {
        public const int PayloadSize = 424;
        public override EquipmentType EquipmentType => EquipmentType.Weapon;
    }

    /// <summary><c>DataShieldTypeIGC</c> (<c>src/Igc/igc.h:1846</c>), 412 bytes.</summary>
    public sealed record Shield(
        float RateRegen,
        float MaxStrength,
        byte DefenseType,
        short ActivateSound,
        short DeactivateSound) : Subtype
    {
        public const int PayloadSize = 412;
        public override EquipmentType EquipmentType => EquipmentType.Shield;
    }

    /// <summary><c>DataCloakTypeIGC</c> (<c>src/Igc/igc.h:1855</c>), 416 bytes.</summary>
    public sealed record Cloak(
        float EnergyConsumption,
        float MaxCloaking,
        float OnRate,
        float OffRate,
        short EngageSound,
        short DisengageSound) : Subtype
    {
        public const int PayloadSize = 416;
        public override EquipmentType EquipmentType => EquipmentType.Cloak;
    }

    /// <summary><c>DataAfterburnerTypeIGC</c> (<c>src/Igc/igc.h:1865</c>), 416 bytes.</summary>
    public sealed record Afterburner(
        float FuelConsumption,
        float MaxThrust,
        float OnRate,
        float OffRate,
        short InteriorSound,
        short ExteriorSound) : Subtype
    {
        public const int PayloadSize = 416;
        public override EquipmentType EquipmentType => EquipmentType.Afterburner;
    }

    /// <summary><c>DataPackTypeIGC</c> (<c>src/Igc/igc.h:1875</c>), 400 bytes.</summary>
    public sealed record Pack(
        byte PackType,
        short Amount) : Subtype
    {
        public const int PayloadSize = 400;
        public override EquipmentType EquipmentType => EquipmentType.Pack;
    }

    /// <summary>
    /// P182 — a <b>construction-only</b> <see cref="EquipmentType.ChaffLauncher"/>
    /// mount subtype. In the native engine a chaff launcher is a compact 24-byte
    /// <c>DataLauncherTypeIGC</c> record (handled by <see cref="DataLauncherTypeIGC"/>,
    /// not this struct — the corpus has no <c>OT_partType</c> <em>part</em> record that
    /// decodes as <c>ChaffLauncher</c>, so <see cref="Decode"/>'s
    /// <see cref="ExpectedSize"/> deliberately still rejects it) that names its
    /// expended chaff type via <c>expenditureTypeID</c>
    /// (<c>src/Igc/igc.h:1881</c>). This port's <c>Ship.Parts</c>/<c>MountPart</c>
    /// mounting store is keyed on <see cref="EquipmentType"/> via
    /// <see cref="DataPartTypeIGC.Subtype.EquipmentType"/>, so to let a ship mount a
    /// chaff launcher (tracking an <c>Amount</c> like any other mounted part) and to let
    /// the <c>FIRE_EXPENDABLE(82)</c> dispatch resolve <em>which</em> chaff type the
    /// launcher fires, the union needs this one case — carrying the single field the
    /// native launcher uses to name its expendable. It is deliberately <em>not</em>
    /// added to <see cref="Decode"/>'s size switch (no such part record exists to
    /// decode); callers construct it directly (as the tests do), mirroring how every
    /// other mount is constructed from a decoded part record but this one has no corpus
    /// record to come from.
    /// </summary>
    public sealed record ChaffLauncher(short ExpendableTypeId) : Subtype
    {
        public override EquipmentType EquipmentType => EquipmentType.ChaffLauncher;
    }

    /// <summary>
    /// P224 — a <b>construction-only</b> <see cref="EquipmentType.Dispenser"/>
    /// mine-launcher mount subtype, the exact <see cref="ChaffLauncher"/> recipe
    /// repeated for mines. In the native engine a mine launcher is likewise a
    /// compact 24-byte <c>DataLauncherTypeIGC</c> record (handled by
    /// <see cref="DataLauncherTypeIGC"/>, not this struct) naming its expended
    /// mine via <c>expenditureTypeID</c> (<c>src/Igc/igc.h:1881</c>). This port's
    /// <c>Ship.Parts</c>/<c>MountPart</c> store is keyed on
    /// <see cref="DataPartTypeIGC.Subtype.EquipmentType"/>, so to let a ship mount
    /// a mine launcher and to let the <c>FIRE_EXPENDABLE(82)</c> dispatch resolve
    /// <em>which</em> mine type it fires, the union needs this case — carrying the
    /// single field the native launcher uses to name its expendable. Like
    /// <see cref="ChaffLauncher"/> it is deliberately <em>not</em> added to
    /// <see cref="Decode"/>'s size switch (no such part record exists to decode);
    /// callers construct it directly (as the tests do).
    /// <para><b>EquipmentType choice.</b> The mine type itself reports
    /// <c>ET_Dispenser</c> (<c>CmineTypeIGC::GetEquipmentType</c>,
    /// <c>src/Igc/minetypeigc.h:62-64</c>), and the mounted dispenser's own
    /// <c>GetEquipmentType</c> mirrors its expendable type's
    /// (<c>dispenserigc.h:45-48</c>), so a mine launcher mounts under
    /// <c>ET_Dispenser</c> — the same value <c>IsLauncherType</c> accepts for a
    /// dispenser alongside chaff/magazine (<c>src/Igc/igc.h:3879-3885</c>) and the
    /// value native names <c>&quot;mine&quot;</c> in
    /// <c>IpartTypeIGC::GetEquipmentTypeName</c> (<c>src/Igc/partTypeIGC.cpp:74</c>).</para>
    /// </summary>
    public sealed record MineLauncher(short ExpendableTypeId) : Subtype
    {
        public override EquipmentType EquipmentType => EquipmentType.Dispenser;
    }

    /// <summary>
    /// P224 — a <b>construction-only</b> <see cref="EquipmentType.Dispenser"/>
    /// probe-launcher mount subtype, the <see cref="MineLauncher"/> recipe repeated
    /// for probes. The native probe type also reports <c>ET_Dispenser</c>
    /// (<c>CprobeTypeIGC::GetEquipmentType</c>, <c>src/Igc/probetypeigc.h:92-94</c>),
    /// so — exactly like a mine launcher — a probe launcher mounts under
    /// <c>EquipmentType.Dispenser</c> and carries only the <c>expenditureTypeID</c>
    /// that names its probe type. Construction-only for the same reason as
    /// <see cref="ChaffLauncher"/>/<see cref="MineLauncher"/>: no corpus part record
    /// exists to decode; tests and live loadouts build it directly.
    /// <para>Because mine and probe launchers share <see cref="EquipmentType.Dispenser"/>
    /// (the wire carries only the <c>EquipmentType</c>, <c>FIRE_EXPENDABLE(82)</c>),
    /// the <c>HandleFireExpendable</c> dispatch distinguishes them by this
    /// <em>subtype</em>, not by the equipment type — a ship carrying both mounted is
    /// resolved to whichever occupies mount 0 (native's own single dispenser-at-mount-0
    /// rule, <c>FedSrv.CPP:5652</c>).</para>
    /// </summary>
    public sealed record ProbeLauncher(short ExpendableTypeId) : Subtype
    {
        public override EquipmentType EquipmentType => EquipmentType.Dispenser;
    }

    /// <summary>
    /// P226 — a <b>construction-only</b> <see cref="EquipmentType.Magazine"/>
    /// missile-launcher mount subtype, the exact <see cref="ChaffLauncher"/>
    /// recipe repeated for missiles. Native's missile type reports
    /// <c>ET_Magazine</c> (<c>CmissileTypeIGC::GetEquipmentType</c>,
    /// <c>src/Igc/missiletypeigc.h:60-63</c>) — the same equipment value the
    /// <c>FM_CS_FIRE_MISSILE(70)</c> server handler resolves at mount 0 via
    /// <c>GetMountedPart(ET_Magazine, 0)</c> (<c>FedSrv.CPP:5425</c>) — so a
    /// missile launcher mounts under <c>EquipmentType.Magazine</c> and carries
    /// only the <c>expenditureTypeID</c> that names its missile type
    /// (<c>src/Igc/igc.h:1881</c>). Construction-only for the same reason as
    /// <see cref="ChaffLauncher"/>/<see cref="MineLauncher"/>/<see cref="ProbeLauncher"/>:
    /// no corpus part record exists to decode; tests and live loadouts build it
    /// directly (and it is deliberately <em>not</em> added to <see cref="Decode"/>'s
    /// size switch, matching the launcher-family convention).
    /// </summary>
    public sealed record MissileLauncher(short ExpendableTypeId) : Subtype
    {
        public override EquipmentType EquipmentType => EquipmentType.Magazine;
    }

    /// <summary>
    /// P433 — construct a mountable missile-launcher <see cref="DataPartTypeIGC"/>
    /// from a real launcher record + its missile type, the shared bridge from a
    /// buyable launcher <c>PartId</c> to the mountable part the mounting store
    /// and the server's <c>FM_CS_FIRE_MISSILE(70)</c> handler expect
    /// (<see cref="EquipmentType.Magazine"/>, subtype <see cref="MissileLauncher"/>).
    /// Aliased to <see cref="FromMissileLauncher"/> so the P433-era name keeps
    /// compiling; new callers should use the per-kind methods.
    /// </summary>
    public static DataPartTypeIGC FromLauncher(short partId, in DataLauncherTypeIGC launcher, in DataMissileTypeIGC missile)
        => FromMissileLauncher(partId, in launcher, in missile);

    /// <summary>
    /// P435 — construct a mountable missile-launcher <see cref="DataPartTypeIGC"/>
    /// from a real launcher record + its missile type (the shared bridge from a
    /// buyable launcher <c>PartId</c> to the mountable part the mounting store
    /// and the server's <c>FM_CS_FIRE_MISSILE(70)</c> handler expect). In the
    /// native engine a launcher is a compact 24-byte <see cref="DataLauncherTypeIGC"/>
    /// record (handled by that struct, not by <see cref="Decode"/> — the corpus
    /// has no <c>OT_partType</c> <em>part</em> record that decodes as a magazine),
    /// and the launcher's own display name / part mask / mass live on the
    /// EXPENDABLE type it fires (the embedded <c>LauncherDef</c>: <c>LauncherName</c>/
    /// <c>LauncherPartMask</c>/<c>LauncherMass</c>/…). Used by the server's
    /// <c>ApplyLoadout</c> to grant a requested launcher and by both tools
    /// (allegiance-mcp + vizfight) to pre-validate hull compatibility via
    /// <c>DataHullTypeIGC.CanMountPart</c>.
    /// </summary>
    public static DataPartTypeIGC FromMissileLauncher(short partId, in DataLauncherTypeIGC launcher, in DataMissileTypeIGC missile)
        => FromLauncherDef(partId, in launcher,
            name: missile.LauncherName, description: missile.LauncherDescription,
            groupId: missile.LauncherGroupId,
            ttbmRequired: missile.LauncherTtbmRequired, ttbmEffects: missile.LauncherTtbmEffects,
            signature: missile.LauncherSignature, mass: missile.LauncherMass,
            partMask: missile.LauncherPartMask,
            type: new MissileLauncher(launcher.ExpendabletypeId));

    /// <summary>
    /// P435 — construct a mountable mine-dispenser <see cref="DataPartTypeIGC"/>
    /// from a real launcher record + its mine type (shared bridge, parallel to
    /// <see cref="FromMissileLauncher"/>). Mounts under <see cref="EquipmentType.Dispenser"/>
    /// (native <c>CmineTypeIGC::GetEquipmentType</c>, minetypeigc.h:62-64) with
    /// subtype <see cref="MineLauncher"/> — the exact shape the server's
    /// <c>FIRE_EXPENDABLE(82)</c> handler resolves at <c>Dispenser</c>@0.
    /// </summary>
    public static DataPartTypeIGC FromMineLauncher(short partId, in DataLauncherTypeIGC launcher, in DataMineTypeIGC mine)
        => FromLauncherDef(partId, in launcher,
            name: mine.LauncherName, description: mine.LauncherDescription,
            groupId: mine.LauncherGroupId,
            ttbmRequired: mine.LauncherTtbmRequired, ttbmEffects: mine.LauncherTtbmEffects,
            signature: mine.LauncherSignature, mass: mine.LauncherMass,
            partMask: mine.LauncherPartMask,
            type: new MineLauncher(launcher.ExpendabletypeId));

    /// <summary>
    /// P435 — construct a mountable chaff-dispenser <see cref="DataPartTypeIGC"/>
    /// from a real launcher record + its chaff type (shared bridge, parallel to
    /// <see cref="FromMissileLauncher"/>). Mounts under <see cref="EquipmentType.ChaffLauncher"/>
    /// (native <c>CchaffTypeIGC::GetEquipmentType</c>) with subtype
    /// <see cref="ChaffLauncher"/> — the exact shape the server's
    /// <c>FIRE_EXPENDABLE(82)</c> handler resolves at <c>ChaffLauncher</c>@0.
    /// </summary>
    public static DataPartTypeIGC FromChaffLauncher(short partId, in DataLauncherTypeIGC launcher, in DataChaffTypeIGC chaff)
        => FromLauncherDef(partId, in launcher,
            name: chaff.LauncherName, description: chaff.LauncherDescription,
            groupId: chaff.LauncherGroupId,
            ttbmRequired: chaff.LauncherTtbmRequired, ttbmEffects: chaff.LauncherTtbmEffects,
            signature: chaff.LauncherSignature, mass: chaff.LauncherMass,
            partMask: chaff.LauncherPartMask,
            type: new ChaffLauncher(launcher.ExpendabletypeId));

    /// <summary>
    /// P435 — construct a mountable probe-dispenser <see cref="DataPartTypeIGC"/>
    /// from a real launcher record + its probe type (shared bridge, parallel to
    /// <see cref="FromMissileLauncher"/>). Mounts under <see cref="EquipmentType.Dispenser"/>
    /// (native <c>CprobeTypeIGC::GetEquipmentType</c>, probetypeigc.h:92-94) with
    /// subtype <see cref="ProbeLauncher"/> — the exact shape the server's
    /// <c>FIRE_EXPENDABLE(82)</c> handler resolves at <c>Dispenser</c>@0.
    /// </summary>
    public static DataPartTypeIGC FromProbeLauncher(short partId, in DataLauncherTypeIGC launcher, in DataProbeTypeIGC probe)
        => FromLauncherDef(partId, in launcher,
            name: probe.LauncherName, description: probe.LauncherDescription,
            groupId: probe.LauncherGroupId,
            ttbmRequired: probe.LauncherTtbmRequired, ttbmEffects: probe.LauncherTtbmEffects,
            signature: probe.LauncherSignature, mass: probe.LauncherMass,
            partMask: probe.LauncherPartMask,
            type: new ProbeLauncher(launcher.ExpendabletypeId));

    /// <summary>The single shared launcher-bridge body: every expendable kind's
    /// embedded <c>LauncherDef</c> spells the mountable part identically, so all
    /// four <c>From&lt;Kind&gt;Launcher</c> methods funnel here and differ only in
    /// the <c>LauncherDef</c> scalars and the mounted subtype.</summary>
    private static DataPartTypeIGC FromLauncherDef(
        short partId, in DataLauncherTypeIGC launcher,
        string name, string description, byte groupId,
        byte[] ttbmRequired, byte[] ttbmEffects,
        float signature, float mass, short partMask,
        Subtype type)
    {
        return new DataPartTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "",
            Name: name, Description: description,
            GroupId: groupId,
            TtbmRequired: ttbmRequired, TtbmEffects: ttbmEffects,
            Mass: mass, Signature: signature,
            PartId: partId, SuccessorPartId: launcher.SuccessorPartId,
            PartMask: partMask,
            InventoryLineMdl: launcher.InventoryLineMdl,
            Type: type);
    }

    /// <summary>
    /// Decode an <c>OT_partType</c> payload.
    /// </summary>
    /// <remarks>
    /// Validates the record size against the size implied by <c>equipmentType</c>
    ///@376, mirroring how the engine reconstructs the object. A 24-byte
    /// payload (a launcher, not a part) is rejected here — use
    /// <see cref="DataLauncherTypeIGC.Decode"/> for those.
    /// </remarks>
    /// <param name="b">The payload bytes (may be a part subtype of 400/412/416/424 bytes).</param>
    /// <exception cref="InvalidDataException">On an unrecognised size/subtype combination.</exception>
    public static DataPartTypeIGC Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length < Size)
            throw new InvalidDataException(
                $"DataPartTypeIGC needs at least {Size} bytes, got {b.Length}");
        if (b.Length == DataLauncherTypeIGC.Size)
            throw new InvalidDataException(
                "DataPartTypeIGC: 24-byte record is a DataLauncherTypeIGC, not a part");

        var equipmentType = (EquipmentType)BinaryPrimitives.ReadInt16LittleEndian(b[376..]);
        var expected = ExpectedSize(equipmentType);
        if (expected == 0)
            throw new InvalidDataException(
                $"DataPartTypeIGC: unsupported equipmentType {(short)equipmentType}");
        if (b.Length != expected)
            throw new InvalidDataException(
                $"DataPartTypeIGC: equipmentType {(short)equipmentType} ({equipmentType}) " +
                $"implies {expected} bytes, got {b.Length}");

        static string Fix(ReadOnlySpan<byte> s, int start, int count) =>
            DataProjectileTypeIGC.CutAtNull(Encoding.ASCII.GetString(s.Slice(start, count)));

        Subtype subtype = equipmentType switch
        {
            EquipmentType.Weapon => new Weapon(
                DtimeReady: BinaryPrimitives.ReadSingleLittleEndian(b[396..]),
                DtimeBurst: BinaryPrimitives.ReadSingleLittleEndian(b[400..]),
                EnergyPerShot: BinaryPrimitives.ReadSingleLittleEndian(b[404..]),
                Dispersion: BinaryPrimitives.ReadSingleLittleEndian(b[408..]),
                CAmmoPerShot: BinaryPrimitives.ReadInt16LittleEndian(b[412..]),
                ProjectileTypeId: BinaryPrimitives.ReadInt16LittleEndian(b[414..]),
                ActivateSound: BinaryPrimitives.ReadInt16LittleEndian(b[416..]),
                SingleShotSound: BinaryPrimitives.ReadInt16LittleEndian(b[418..]),
                BurstSound: BinaryPrimitives.ReadInt16LittleEndian(b[420..])),

            EquipmentType.Shield => new Shield(
                RateRegen: BinaryPrimitives.ReadSingleLittleEndian(b[396..]),
                MaxStrength: BinaryPrimitives.ReadSingleLittleEndian(b[400..]),
                DefenseType: b[404],
                ActivateSound: BinaryPrimitives.ReadInt16LittleEndian(b[406..]),
                DeactivateSound: BinaryPrimitives.ReadInt16LittleEndian(b[408..])),

            EquipmentType.Cloak => new Cloak(
                EnergyConsumption: BinaryPrimitives.ReadSingleLittleEndian(b[396..]),
                MaxCloaking: BinaryPrimitives.ReadSingleLittleEndian(b[400..]),
                OnRate: BinaryPrimitives.ReadSingleLittleEndian(b[404..]),
                OffRate: BinaryPrimitives.ReadSingleLittleEndian(b[408..]),
                EngageSound: BinaryPrimitives.ReadInt16LittleEndian(b[412..]),
                DisengageSound: BinaryPrimitives.ReadInt16LittleEndian(b[414..])),

            EquipmentType.Afterburner => new Afterburner(
                FuelConsumption: BinaryPrimitives.ReadSingleLittleEndian(b[396..]),
                MaxThrust: BinaryPrimitives.ReadSingleLittleEndian(b[400..]),
                OnRate: BinaryPrimitives.ReadSingleLittleEndian(b[404..]),
                OffRate: BinaryPrimitives.ReadSingleLittleEndian(b[408..]),
                InteriorSound: BinaryPrimitives.ReadInt16LittleEndian(b[412..]),
                ExteriorSound: BinaryPrimitives.ReadInt16LittleEndian(b[414..])),

            EquipmentType.Pack => new Pack(
                PackType: b[396],
                Amount: BinaryPrimitives.ReadInt16LittleEndian(b[398..])),

            _ => throw new InvalidDataException(
                $"DataPartTypeIGC: unsupported equipmentType {(short)equipmentType}"),
        };

        return new DataPartTypeIGC(
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
            PartId: BinaryPrimitives.ReadInt16LittleEndian(b[372..]),
            SuccessorPartId: BinaryPrimitives.ReadInt16LittleEndian(b[374..]),
            PartMask: BinaryPrimitives.ReadInt16LittleEndian(b[378..]),
            InventoryLineMdl: Fix(b, 380, 13),
            Type: subtype);
    }

    private static int ExpectedSize(EquipmentType et) => et switch
    {
        EquipmentType.Weapon => Weapon.PayloadSize,
        EquipmentType.Shield => Shield.PayloadSize,
        EquipmentType.Cloak => Cloak.PayloadSize,
        EquipmentType.Afterburner => Afterburner.PayloadSize,
        EquipmentType.Pack => Pack.PayloadSize,
        _ => 0,
    };
}
