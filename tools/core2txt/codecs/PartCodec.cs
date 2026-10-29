using Allegiance.Core;

namespace Allegiance.Tools.Core2Txt;

/// <summary>
/// <c>OT_partType</c> codec. Native splits the part list across six files by
/// equipment type (<c>Obj2Txt</c> <c>OT_partType</c>,
/// <c>missionigc.cpp:4786-5052</c>) and reads them back in the fixed order
/// afterburners, weapons, packs, shields, cloaks, launchers
/// (<c>ImportStaticIGCObjs</c>, <c>missionigc.cpp:1291-1850</c>). A 24-byte
/// record is <c>DataLauncherTypeIGC</c>; larger records are
/// <c>DataPartTypeIGC</c> plus the equipment-specific tail
/// (<c>igc.h:1822-1889</c>).
/// </summary>
public sealed class PartCodec : ITypeCodec
{
    public IgcObjectType Type => IgcObjectType.PartType;

    public IReadOnlyList<TypeFileSpec> Files { get; } = new[]
    {
        CoreFiles.Expendables, CoreFiles.Weapons, CoreFiles.Shields,
        CoreFiles.Cloaks, CoreFiles.Afterburners, CoreFiles.Packs,
    };

    /// <summary>The order native's <c>ImportStaticIGCObjs</c> builds part records
    /// in (used for a text folder without a sidecar).</summary>
    private static readonly TypeFileSpec[] ReadOrder =
    {
        CoreFiles.Afterburners, CoreFiles.Weapons, CoreFiles.Packs,
        CoreFiles.Shields, CoreFiles.Cloaks, CoreFiles.Expendables,
    };

    // DataPartTypeIGC offsets (igc.h:1822-1831).
    private const int Price = 0, TimeToBuild = 4, ModelName = 8, IconName = 22,
        Name = 35, Description = 60, GroupId = 261, TtbmRequired = 262,
        TtbmEffects = 312, Mass = 364, Signature = 368, PartId = 372,
        SuccessorPartId = 374, EquipmentType = 376, PartMask = 378,
        InventoryLine = 380;

    public IReadOnlyList<(string File, string Line)> Write(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog)
    {
        if (payload.Length == DataLauncherTypeIGC.Size)
            return new[] { (CoreFiles.Expendables.BaseName, WriteLauncher(payload, table, catalog)) };

        int et = Bin.I16(payload, EquipmentType);
        var f = new List<string> { CoreTable.FormatInt(Bin.I16(payload, PartId)) };

        // Equipment-specific leading columns.
        switch (et)
        {
            case CoreTable.ETWeapon:
                f.Add(CoreTable.FormatFloat(Bin.F32(payload, 396))); // dtimeReady
                f.Add(CoreTable.FormatFloat(Bin.F32(payload, 400))); // dtimeBurst
                f.Add(CoreTable.FormatFloat(Bin.F32(payload, 404))); // energyPerShot
                f.Add(CoreTable.FormatFloat(Bin.F32(payload, 408))); // dispersion
                f.Add(CoreTable.FormatInt(Bin.I16(payload, 412)));   // cAmmoPerShot
                f.Add(CoreTable.FormatInt(Bin.I16(payload, 414)));   // projectileTypeID
                f.Add(CoreTable.FormatInt(Bin.I16(payload, 416)));   // activateSound
                f.Add(CoreTable.FormatInt(Bin.I16(payload, 418)));   // singleShotSound
                f.Add(CoreTable.FormatInt(Bin.I16(payload, 420)));   // burstSound
                break;
            case CoreTable.ETShield:
                f.Add(CoreTable.FormatFloat(Bin.F32(payload, 396))); // rateRegen
                f.Add(CoreTable.FormatFloat(Bin.F32(payload, 400))); // maxStrength
                f.Add(CodecUtil.DefenseId(payload[404], asDefense: true));
                f.Add(CoreTable.FormatInt(Bin.I16(payload, 406)));   // activateSound
                f.Add(CoreTable.FormatInt(Bin.I16(payload, 408)));   // deactivateSound
                break;
            case CoreTable.ETCloak:
                f.Add(CoreTable.FormatFloat(Bin.F32(payload, 396))); // energyConsumption
                f.Add(CoreTable.FormatFloat(Bin.F32(payload, 400))); // maxCloaking
                f.Add(CoreTable.FormatFloat(Bin.F32(payload, 404))); // onRate
                f.Add(CoreTable.FormatFloat(Bin.F32(payload, 408))); // offRate
                f.Add(CoreTable.FormatInt(Bin.I16(payload, 412)));   // engageSound
                f.Add(CoreTable.FormatInt(Bin.I16(payload, 414)));   // disengageSound
                break;
            case CoreTable.ETAfterburner:
                f.Add(CoreTable.FormatFloat(Bin.F32(payload, 396))); // fuelConsumption
                f.Add(CoreTable.FormatFloat(Bin.F32(payload, 400))); // maxThrust
                f.Add(CoreTable.FormatFloat(Bin.F32(payload, 404))); // onRate
                f.Add(CoreTable.FormatFloat(Bin.F32(payload, 408))); // offRate
                f.Add(CoreTable.FormatInt(Bin.I16(payload, 412)));   // interiorSound
                f.Add(CoreTable.FormatInt(Bin.I16(payload, 414)));   // exteriorSound
                break;
            case CoreTable.ETPack:
                f.Add(CoreTable.FormatInt(payload[396]));            // packType (char → int)
                f.Add(CoreTable.FormatInt(Bin.I16(payload, 398)));   // amount
                break;
            default:
                return Array.Empty<(string, string)>(); // native debugf's and writes nothing
        }

        f.Add(CoreTable.FormatFloat(Bin.F32(payload, Mass)));
        f.Add(CoreTable.FormatFloat(Bin.F32(payload, Signature)));
        f.Add(CoreTable.FormatInt(Bin.I16(payload, SuccessorPartId)));
        f.Add(table.BitsToPartsList(Bin.I16(payload, PartMask), et));
        f.Add(Bin.Str(payload, InventoryLine, 13));
        AppendBuyable(f, payload);
        f.Add(table.BitsToTechsList(MaskBits(payload, TtbmRequired)));
        f.Add(table.BitsToTechsList(MaskBits(payload, TtbmEffects)));

        return new[] { (FileFor(et).BaseName, string.Join('\t', f)) };
    }

    private static string WriteLauncher(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog)
    {
        short etid = Bin.I16(payload, 8);
        var f = new List<string>
        {
            CoreTable.FormatInt(etid),
            CoreTable.FormatInt(Bin.I16(payload, 2)),           // partID
            CoreTable.FormatInt(Bin.I16(payload, 0)),           // amount
            CoreTable.FormatInt(Bin.I16(payload, 4)),           // successorPartID
            CoreTable.FormatInt(Bin.I16(payload, 6)),           // launchCount
            Bin.Str(payload, 10, 13),                            // inventoryLineMDL
        };
        if (catalog.LauncherDefs.TryGetValue(etid, out var ld))
        {
            f.Add(CoreTable.FormatInt(ld.Price));
            f.Add(CoreTable.FormatInt(unchecked((int)ld.TimeToBuild)));
            f.Add(ld.ModelName);
            f.Add(ld.IconName);
            f.Add(ld.Name);
            f.Add(ld.Description);
            f.Add(CoreTable.FormatInt(unchecked((sbyte)ld.GroupId)));
            f.Add(table.BitsToTechsList(ld.TtbmRequired));
            f.Add(table.BitsToTechsList(ld.TtbmEffects));
        }
        else
        {
            // Native dereferences the (null) launcherDef and would crash; keep the
            // row shape with empty values instead.
            f.AddRange(new[] { "0", "0", "", "", "", "", "0", "0", "0" });
        }
        return string.Join('\t', f);
    }

    private static void AppendBuyable(List<string> f, ReadOnlySpan<byte> payload)
    {
        f.Add(CoreTable.FormatInt(Bin.I32(payload, Price)));
        f.Add(CoreTable.FormatInt(unchecked((int)Bin.U32(payload, TimeToBuild))));
        f.Add(Bin.Str(payload, ModelName, 14));
        f.Add(Bin.Str(payload, IconName, 13));
        f.Add(Bin.Str(payload, Name, 25));
        f.Add(Bin.Str(payload, Description, 201));
        f.Add(CoreTable.FormatInt(unchecked((sbyte)payload[GroupId])));
    }

    private static bool[] MaskBits(ReadOnlySpan<byte> payload, int off)
    {
        var bits = new bool[CoreTable.CTtbMax];
        CoreTable.FromBytes(payload.Slice(off, 50), bits);
        return bits;
    }

    private static TypeFileSpec FileFor(int et) => et switch
    {
        CoreTable.ETWeapon => CoreFiles.Weapons,
        CoreTable.ETShield => CoreFiles.Shields,
        CoreTable.ETCloak => CoreFiles.Cloaks,
        CoreTable.ETAfterburner => CoreFiles.Afterburners,
        CoreTable.ETPack => CoreFiles.Packs,
        _ => CoreFiles.Weapons,
    };

    public List<TextRecord> Read(TextCoreFiles text, CoreTable table)
    {
        var result = new List<TextRecord>();
        foreach (var spec in ReadOrder)
        {
            foreach (var cells in text.Rows(spec.BaseName))
            {
                var row = new TextRow(cells);
                result.Add(spec == CoreFiles.Expendables
                    ? ReadLauncher(row)
                    : ReadPart(table, row, spec));
            }
        }
        return result;
    }

    private static TextRecord ReadLauncher(TextRow row)
    {
        var w = new PayloadBuilder(DataLauncherTypeIGC.Size) { Id = row.I(1) };
        w.I16(8, unchecked((short)row.I(0)));   // expendabletypeID
        w.I16(2, unchecked((short)row.I(1)));   // partID
        w.I16(0, unchecked((short)row.I(2)));   // amount
        w.I16(4, unchecked((short)row.I(3)));   // successorPartID
        w.I16(6, unchecked((short)row.I(4)));   // launchCount
        w.Str(10, 13, row.S(5));                // inventoryLineMDL
        return w.Build();
    }

    private static TextRecord ReadPart(CoreTable table, TextRow row, TypeFileSpec spec)
    {
        short partId = unchecked((short)row.I(0));
        int et = spec == CoreFiles.Weapons ? CoreTable.ETWeapon
            : spec == CoreFiles.Shields ? CoreTable.ETShield
            : spec == CoreFiles.Cloaks ? CoreTable.ETCloak
            : spec == CoreFiles.Afterburners ? CoreTable.ETAfterburner
            : CoreTable.ETPack;
        int size = et switch
        {
            CoreTable.ETWeapon => 424,
            CoreTable.ETShield => 412,
            CoreTable.ETCloak => 416,
            CoreTable.ETAfterburner => 416,
            _ => 400,
        };
        var w = new PayloadBuilder(size) { Id = partId };
        w.I16(PartId, partId);
        w.I16(EquipmentType, (short)et);

        // Equipment-specific leading columns (native column indexes).
        switch (et)
        {
            case CoreTable.ETWeapon:
                w.F32(396, row.F(1));   // dtimeReady
                w.F32(400, row.F(2));   // dtimeBurst
                w.F32(404, row.F(3));   // energyPerShot
                w.F32(408, row.F(4));   // dispersion
                w.I16(412, unchecked((short)row.F(5))); // cAmmoPerShot (native atof)
                w.I16(414, unchecked((short)row.I(6))); // projectileTypeID
                w.I16(416, unchecked((short)row.I(7))); // activateSound
                w.I16(418, unchecked((short)row.I(8))); // singleShotSound
                w.I16(420, unchecked((short)row.I(9))); // burstSound
                break;
            case CoreTable.ETShield:
                w.F32(396, row.F(1));   // rateRegen
                w.F32(400, row.F(2));   // maxStrength
                w.U8(404, (byte)row.TokenInt(3)); // defenseType
                w.I16(406, unchecked((short)row.I(4))); // activateSound
                w.I16(408, unchecked((short)row.I(5))); // deactivateSound
                break;
            case CoreTable.ETCloak:
                w.F32(396, row.F(1));
                w.F32(400, row.F(2));
                w.F32(404, row.F(3));
                w.F32(408, row.F(4));
                w.I16(412, unchecked((short)row.I(5))); // engageSound
                w.I16(414, unchecked((short)row.I(6))); // disengageSound
                break;
            case CoreTable.ETAfterburner:
                w.F32(396, row.F(1));
                w.F32(400, row.F(2));
                w.F32(404, row.F(3));
                w.F32(408, row.F(4));
                w.I16(412, unchecked((short)row.I(5))); // interiorSound
                w.I16(414, unchecked((short)row.I(6))); // exteriorSound
                break;
            case CoreTable.ETPack:
                w.U8(396, (byte)row.I(1));              // packType
                w.I16(398, unchecked((short)row.I(2))); // amount
                break;
        }

        // Shared tail: mass, signature, successor, partMask, inventory, buyable,
        // tech masks. The native pack reader looks the mask up in the CLOAK table
        // (missionigc.cpp:1490); the writer used the PACK table, so we keep the
        // writer's table — the native reader only breaks when a name table is
        // loaded (the decimal spelling is equipment-independent).
        int col = et switch
        {
            CoreTable.ETWeapon => 10,
            CoreTable.ETShield => 6,
            CoreTable.ETCloak => 7,
            CoreTable.ETAfterburner => 7,
            _ => 3,
        };
        w.F32(Mass, row.F(col));
        w.F32(Signature, row.F(col + 1));
        w.I16(SuccessorPartId, unchecked((short)row.I(col + 2)));
        w.I16(PartMask, CodecUtil.PartMaskSingle(table, row.S(col + 3), et));
        w.Str(InventoryLine, 13, row.S(col + 4));

        w.I32(Price, row.I(col + 5));
        w.U32(TimeToBuild, unchecked((uint)row.I(col + 6)));
        w.Str(ModelName, 14, row.S(col + 7));
        w.Str(IconName, 13, row.S(col + 8));
        w.Str(Name, 25, row.S(col + 9));
        w.Str(Description, 201, row.S(col + 10));
        w.U8(GroupId, (byte)row.I(col + 11));
        CodecUtil.WriteMask(w, TtbmRequired, CodecUtil.ParseTechList(table, row.S(col + 12)));
        CodecUtil.WriteMask(w, TtbmEffects, CodecUtil.ParseTechList(table, row.S(col + 13)));
        return w.Build();
    }
}
