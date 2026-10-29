using Allegiance.Core;

namespace Allegiance.Tools.Core2Txt;

/// <summary>Shared read helpers for the four expendable codecs.</summary>
internal static class ExpendableRead
{
    /// <summary>
    /// Native copies the launcher definition (price/time/model/icon/name/
    /// description/group/tech masks) out of <c>Expendables.csv</c>: it scans rows
    /// from the top and breaks at the first row whose expendable id differs
    /// (<c>missionigc.cpp:768-786</c>), later matches winning.
    /// </summary>
    public static void LauncherDef(CoreTable table, IReadOnlyList<string[]> rows, int expendableId, PayloadBuilder w)
    {
        foreach (var cells in rows)
        {
            var row = new TextRow(cells);
            if (row.I(0) != expendableId) break;
            w.I32(64, row.I(6));                    // price
            w.U32(68, unchecked((uint)row.I(7)));   // timeToBuild
            w.Str(72, 14, row.S(8));                // modelName
            w.Str(86, 13, row.S(9));                // iconName
            w.Str(99, 25, row.S(10));               // name
            w.Str(124, 201, row.S(11));             // description
            w.U8(325, (byte)row.I(12));             // groupID
            CodecUtil.WriteMask(w, 326, CodecUtil.ParseTechList(table, row.S(13)));
            CodecUtil.WriteMask(w, 376, CodecUtil.ParseTechList(table, row.S(14)));
        }
    }

    /// <summary>The 16 columns the four expendable files share. Column indexes
    /// are the per-file native positions; <paramref name="radiusCol"/> is -1 for
    /// mines, whose object radius column native writes but never reads back.</summary>
    public static void Common(CoreTable table, PayloadBuilder w, TextRow row, IReadOnlyList<string[]> expendableRows,
        int loadTimeCol, int lifespanCol, int signatureCol, int hitPointsCol, int defenseCol,
        int abilitiesCol, int iconCol, int colorCol, int radiusCol, int rotationCol,
        int modelCol, int textureCol, int launcherSignatureCol, int massCol,
        int partMaskCol, int partMaskEt, int expendableSizeCol)
    {
        w.I16(446, unchecked((short)row.I(0)));      // expendabletypeID
        LauncherDef(table, expendableRows, row.I(0), w);

        w.F32(52, row.F(loadTimeCol));
        w.F32(56, row.F(lifespanCol));
        w.F32(60, row.F(signatureCol));
        w.F32(440, row.F(hitPointsCol));
        w.U8(444, (byte)row.TokenInt(defenseCol));
        w.I16(448, CoreTable.AbilitiesListToMask(row.S(abilitiesCol), CoreTable.ATExpendable));
        w.Str(450, 13, row.S(iconCol));
        CodecUtil.WriteColor(w, 0, row, colorCol);
        if (radiusCol >= 0) w.F32(16, row.F(radiusCol));
        w.F32(20, row.F(rotationCol));
        w.Str(24, 13, row.S(modelCol));
        w.Str(37, 13, row.S(textureCol));
        w.F32(428, row.F(launcherSignatureCol));
        w.F32(432, row.F(massCol));
        w.I16(436, CodecUtil.PartMaskSingle(table, row.S(partMaskCol), partMaskEt));
        w.I16(438, unchecked((short)row.I(expendableSizeCol)));
    }
}

/// <summary>
/// <c>OT_missileType</c> codec (524 bytes, <c>igc.h:1960</c>).
/// Write: <c>Obj2Txt</c> <c>OT_missileType</c> (<c>missionigc.cpp:4510-4590</c>).
/// Read: <c>ImportStaticIGCObjs</c> missile block (<c>missionigc.cpp:747-874</c>).
/// </summary>
public sealed class MissileCodec : ITypeCodec
{
    public IgcObjectType Type => IgcObjectType.MissileType;

    public IReadOnlyList<TypeFileSpec> Files { get; } = new[] { CoreFiles.ExpendableMissiles };

    public IReadOnlyList<(string File, string Line)> Write(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog)
    {
        var f = new List<string>
        {
            CoreTable.FormatInt(Bin.I16(payload, 446)),                 // expendabletypeID
            CoreTable.FormatFloat(Bin.F32(payload, 464)),               // acceleration
            CoreTable.FormatFloat(Bin.F32(payload, 468)),               // turnRate
            CoreTable.FormatFloat(Bin.F32(payload, 472)),               // initialSpeed
            CoreTable.FormatFloat(Bin.F32(payload, 476)),               // lockTime
            CoreTable.FormatFloat(Bin.F32(payload, 480)),               // readyTime
            CoreTable.FormatFloat(Bin.F32(payload, 484)),               // maxLock
            CoreTable.FormatFloat(Bin.F32(payload, 488)),               // chaffResistance
            CoreTable.FormatFloat(Bin.F32(payload, 492)),               // dispersion
            CoreTable.FormatFloat(Bin.F32(payload, 496)),               // lockAngle
            CoreTable.FormatFloat(Bin.F32(payload, 500)),               // power
            CoreTable.FormatFloat(Bin.F32(payload, 504)),               // blastPower
            CoreTable.FormatFloat(Bin.F32(payload, 508)),               // blastRadius
            CoreTable.FormatFloat(Bin.F32(payload, 512)),               // width
            CodecUtil.DefenseId(payload[516], asDefense: false),        // damageType (comment)
            CoreTable.FormatBool(payload[517] != 0),                    // bDirectional
            CoreTable.FormatInt(Bin.I16(payload, 518)),                 // launchSound
            CoreTable.FormatInt(Bin.I16(payload, 520)),                 // ambientSound
            CoreTable.FormatFloat(Bin.F32(payload, 52)),                // loadTime
            CoreTable.FormatFloat(Bin.F32(payload, 56)),                // lifespan
            CoreTable.FormatFloat(Bin.F32(payload, 60)),                // signature
            CoreTable.FormatFloat(Bin.F32(payload, 440)),               // hitPoints
            CodecUtil.DefenseId(payload[444], asDefense: true),         // defenseType (comment)
            CoreTable.AbilitiesListToStrings(Bin.I16(payload, 448), CoreTable.ATExpendable),
            Bin.Str(payload, 450, 13),                                  // iconName
            CodecUtil.ColorString(payload, 0),                          // color
            CoreTable.FormatFloat(Bin.F32(payload, 16)),                // radius
            CoreTable.FormatFloat(Bin.F32(payload, 20)),                // rotation
            Bin.Str(payload, 24, 13),                                   // modelName
            Bin.Str(payload, 37, 13),                                   // textureName
            CoreTable.FormatFloat(Bin.F32(payload, 428)),               // launcherDef.signature
            CoreTable.FormatFloat(Bin.F32(payload, 432)),               // launcherDef.mass
            table.BitsToPartsList(Bin.I16(payload, 436), CoreTable.ETMagazine),
            CoreTable.FormatInt(Bin.I16(payload, 438)),                 // expendableSize
            Bin.Str(payload, 99, 25),                                   // launcherDef.name
        };
        return new[] { (CoreFiles.ExpendableMissiles.BaseName, string.Join('\t', f)) };
    }

    public List<TextRecord> Read(TextCoreFiles text, CoreTable table)
    {
        var expendables = text.Rows(CoreFiles.Expendables.BaseName);
        var result = new List<TextRecord>();
        foreach (var cells in text.Rows(CoreFiles.ExpendableMissiles.BaseName))
        {
            var row = new TextRow(cells);
            var w = new PayloadBuilder(DataMissileTypeIGC.Size) { Id = row.I(0) };
            ExpendableRead.Common(table, w, row, expendables,
                loadTimeCol: 18, lifespanCol: 19, signatureCol: 20, hitPointsCol: 21,
                defenseCol: 22, abilitiesCol: 23, iconCol: 24, colorCol: 25, radiusCol: 26,
                rotationCol: 27, modelCol: 28, textureCol: 29, launcherSignatureCol: 30,
                massCol: 31, partMaskCol: 32, partMaskEt: CoreTable.ETMagazine, expendableSizeCol: 33);
            w.F32(464, row.F(1));   // acceleration
            w.F32(468, row.F(2));   // turnRate
            w.F32(472, row.F(3));   // initialSpeed
            w.F32(476, row.F(4));   // lockTime
            w.F32(480, row.F(5));   // readyTime
            w.F32(484, row.F(6));   // maxLock
            w.F32(488, row.F(7));   // chaffResistance
            w.F32(492, row.F(8));   // dispersion
            w.F32(496, row.F(9));   // lockAngle
            w.F32(500, row.F(10));  // power
            w.F32(504, row.F(11));  // blastPower
            w.F32(508, row.F(12));  // blastRadius
            w.F32(512, row.F(13));  // width
            w.U8(516, (byte)row.TokenInt(14)); // damageType
            w.Bool(517, row.B(15));            // bDirectional
            w.I16(518, unchecked((short)row.I(16))); // launchSound
            w.I16(520, unchecked((short)row.I(17))); // ambientSound
            result.Add(w.Build());
        }
        return result;
    }
}

/// <summary>
/// <c>OT_mineType</c> codec (480 bytes, <c>igc.h:1990</c>).
/// Write: <c>missionigc.cpp:4594-4650</c>. Read: <c>missionigc.cpp:1200-1290</c>.
/// </summary>
public sealed class MineCodec : ITypeCodec
{
    public IgcObjectType Type => IgcObjectType.MineType;

    public IReadOnlyList<TypeFileSpec> Files { get; } = new[] { CoreFiles.ExpendableMines };

    public IReadOnlyList<(string File, string Line)> Write(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog)
    {
        var f = new List<string>
        {
            CoreTable.FormatInt(Bin.I16(payload, 446)),         // expendabletypeID
            CoreTable.FormatFloat(Bin.F32(payload, 464)),       // mine radius (derived member)
            CoreTable.FormatFloat(Bin.F32(payload, 468)),       // power
            CoreTable.FormatFloat(Bin.F32(payload, 472)),       // endurance
            CodecUtil.DefenseId(payload[476], asDefense: false),
            CoreTable.FormatFloat(Bin.F32(payload, 52)),        // loadTime
            CoreTable.FormatFloat(Bin.F32(payload, 56)),        // lifespan
            CoreTable.FormatFloat(Bin.F32(payload, 60)),        // signature
            CoreTable.FormatFloat(Bin.F32(payload, 440)),       // hitPoints
            CodecUtil.DefenseId(payload[444], asDefense: true),
            CoreTable.AbilitiesListToStrings(Bin.I16(payload, 448), CoreTable.ATExpendable),
            Bin.Str(payload, 450, 13),                          // iconName
            CodecUtil.ColorString(payload, 0),                  // color
            CoreTable.FormatFloat(Bin.F32(payload, 16)),        // DataObjectIGC radius (write-only)
            CoreTable.FormatFloat(Bin.F32(payload, 20)),        // rotation
            Bin.Str(payload, 24, 13),                           // modelName
            Bin.Str(payload, 37, 13),                           // textureName
            CoreTable.FormatFloat(Bin.F32(payload, 428)),       // launcherDef.signature
            CoreTable.FormatFloat(Bin.F32(payload, 432)),       // launcherDef.mass
            table.BitsToPartsList(Bin.I16(payload, 436), CoreTable.ETDispenser),
            CoreTable.FormatInt(Bin.I16(payload, 438)),
            Bin.Str(payload, 99, 25),                           // launcherDef.name
        };
        return new[] { (CoreFiles.ExpendableMines.BaseName, string.Join('\t', f)) };
    }

    public List<TextRecord> Read(TextCoreFiles text, CoreTable table)
    {
        var expendables = text.Rows(CoreFiles.Expendables.BaseName);
        var result = new List<TextRecord>();
        foreach (var cells in text.Rows(CoreFiles.ExpendableMines.BaseName))
        {
            var row = new TextRow(cells);
            var w = new PayloadBuilder(DataMineTypeIGC.Size) { Id = row.I(0) };
            // The object-radius column (13) is written by native but never read
            // back ("there can only be one radius", missionigc.cpp:1265).
            ExpendableRead.Common(table, w, row, expendables,
                loadTimeCol: 5, lifespanCol: 6, signatureCol: 7, hitPointsCol: 8,
                defenseCol: 9, abilitiesCol: 10, iconCol: 11, colorCol: 12, radiusCol: -1,
                rotationCol: 14, modelCol: 15, textureCol: 16, launcherSignatureCol: 17,
                massCol: 18, partMaskCol: 19, partMaskEt: CoreTable.ETDispenser, expendableSizeCol: 20);
            w.F32(464, row.F(1));   // mine radius
            w.F32(468, row.F(2));   // power
            w.F32(472, row.F(3));   // endurance
            w.U8(476, (byte)row.TokenInt(4)); // damageType
            result.Add(w.Build());
        }
        return result;
    }
}

/// <summary>
/// <c>OT_chaffType</c> codec (468 bytes, <c>igc.h:1998</c>).
/// Write: <c>missionigc.cpp:4654-4705</c>. Read: <c>missionigc.cpp:1030-1125</c>.
/// </summary>
public sealed class ChaffCodec : ITypeCodec
{
    public IgcObjectType Type => IgcObjectType.ChaffType;

    public IReadOnlyList<TypeFileSpec> Files { get; } = new[] { CoreFiles.ExpendableChaff };

    public IReadOnlyList<(string File, string Line)> Write(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog)
    {
        var f = new List<string>
        {
            CoreTable.FormatInt(Bin.I16(payload, 446)),         // expendabletypeID
            CoreTable.FormatFloat(Bin.F32(payload, 464)),       // chaffStrength
            CoreTable.FormatFloat(Bin.F32(payload, 52)),        // loadTime
            CoreTable.FormatFloat(Bin.F32(payload, 56)),        // lifespan
            CoreTable.FormatFloat(Bin.F32(payload, 60)),        // signature
            CoreTable.FormatFloat(Bin.F32(payload, 440)),       // hitPoints
            CodecUtil.DefenseId(payload[444], asDefense: true),
            CoreTable.AbilitiesListToStrings(Bin.I16(payload, 448), CoreTable.ATExpendable),
            Bin.Str(payload, 450, 13),                          // iconName
            CodecUtil.ColorString(payload, 0),                  // color
            CoreTable.FormatFloat(Bin.F32(payload, 16)),        // radius
            CoreTable.FormatFloat(Bin.F32(payload, 20)),        // rotation
            Bin.Str(payload, 24, 13),                           // modelName
            Bin.Str(payload, 37, 13),                           // textureName
            CoreTable.FormatFloat(Bin.F32(payload, 428)),       // launcherDef.signature
            CoreTable.FormatFloat(Bin.F32(payload, 432)),       // launcherDef.mass
            table.BitsToPartsList(Bin.I16(payload, 436), CoreTable.ETChaffLauncher),
            CoreTable.FormatInt(Bin.I16(payload, 438)),
            Bin.Str(payload, 99, 25),                           // launcherDef.name
        };
        return new[] { (CoreFiles.ExpendableChaff.BaseName, string.Join('\t', f)) };
    }

    public List<TextRecord> Read(TextCoreFiles text, CoreTable table)
    {
        var expendables = text.Rows(CoreFiles.Expendables.BaseName);
        var result = new List<TextRecord>();
        foreach (var cells in text.Rows(CoreFiles.ExpendableChaff.BaseName))
        {
            var row = new TextRow(cells);
            var w = new PayloadBuilder(DataChaffTypeIGC.Size) { Id = row.I(0) };
            ExpendableRead.Common(table, w, row, expendables,
                loadTimeCol: 2, lifespanCol: 3, signatureCol: 4, hitPointsCol: 5,
                defenseCol: 6, abilitiesCol: 7, iconCol: 8, colorCol: 9, radiusCol: 10,
                rotationCol: 11, modelCol: 12, textureCol: 13, launcherSignatureCol: 14,
                massCol: 15, partMaskCol: 16, partMaskEt: CoreTable.ETChaffLauncher, expendableSizeCol: 17);
            w.F32(464, row.F(1));   // chaffStrength
            result.Add(w.Build());
        }
        return result;
    }
}

/// <summary>
/// <c>OT_probeType</c> codec (492 bytes, <c>igc.h:2003</c>).
/// Write: <c>missionigc.cpp:4709-4780</c>. Read: <c>missionigc.cpp:875-1025</c>.
/// </summary>
public sealed class ProbeCodec : ITypeCodec
{
    public IgcObjectType Type => IgcObjectType.ProbeType;

    public IReadOnlyList<TypeFileSpec> Files { get; } = new[] { CoreFiles.ExpendableProbes };

    public IReadOnlyList<(string File, string Line)> Write(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog)
    {
        var f = new List<string>
        {
            CoreTable.FormatInt(Bin.I16(payload, 446)),         // expendabletypeID
            CoreTable.FormatFloat(Bin.F32(payload, 464)),       // scannerRange
            CoreTable.FormatFloat(Bin.F32(payload, 468)),       // dtimeBurst
            CoreTable.FormatFloat(Bin.F32(payload, 472)),       // dispersion
            CoreTable.FormatFloat(Bin.F32(payload, 476)),       // accuracy
            CoreTable.FormatInt(Bin.I16(payload, 480)),         // ammo
            CoreTable.FormatInt(Bin.I16(payload, 482)),         // projectileTypeID
            CoreTable.FormatInt(Bin.I16(payload, 484)),         // ambientSound
            CoreTable.FormatFloat(Bin.F32(payload, 488)),       // dtRipcord
            CoreTable.FormatFloat(Bin.F32(payload, 52)),        // loadTime
            CoreTable.FormatFloat(Bin.F32(payload, 56)),        // lifespan
            CoreTable.FormatFloat(Bin.F32(payload, 60)),        // signature
            CoreTable.FormatFloat(Bin.F32(payload, 440)),       // hitPoints
            CodecUtil.DefenseId(payload[444], asDefense: true),
            CoreTable.AbilitiesListToStrings(Bin.I16(payload, 448), CoreTable.ATExpendable),
            Bin.Str(payload, 450, 13),                          // iconName
            CodecUtil.ColorString(payload, 0),                  // color
            CoreTable.FormatFloat(Bin.F32(payload, 16)),        // radius
            CoreTable.FormatFloat(Bin.F32(payload, 20)),        // rotation
            Bin.Str(payload, 24, 13),                           // modelName
            Bin.Str(payload, 37, 13),                           // textureName
            CoreTable.FormatFloat(Bin.F32(payload, 428)),       // launcherDef.signature
            CoreTable.FormatFloat(Bin.F32(payload, 432)),       // launcherDef.mass
            table.BitsToPartsList(Bin.I16(payload, 436), CoreTable.ETDispenser),
            CoreTable.FormatInt(Bin.I16(payload, 438)),
            Bin.Str(payload, 99, 25),                           // launcherDef.name
        };
        return new[] { (CoreFiles.ExpendableProbes.BaseName, string.Join('\t', f)) };
    }

    public List<TextRecord> Read(TextCoreFiles text, CoreTable table)
    {
        var expendables = text.Rows(CoreFiles.Expendables.BaseName);
        var result = new List<TextRecord>();
        foreach (var cells in text.Rows(CoreFiles.ExpendableProbes.BaseName))
        {
            var row = new TextRow(cells);
            var w = new PayloadBuilder(DataProbeTypeIGC.Size) { Id = row.I(0) };
            ExpendableRead.Common(table, w, row, expendables,
                loadTimeCol: 9, lifespanCol: 10, signatureCol: 11, hitPointsCol: 12,
                defenseCol: 13, abilitiesCol: 14, iconCol: 15, colorCol: 16, radiusCol: 17,
                rotationCol: 18, modelCol: 19, textureCol: 20, launcherSignatureCol: 21,
                massCol: 22, partMaskCol: 23, partMaskEt: CoreTable.ETDispenser, expendableSizeCol: 24);
            w.F32(464, row.F(1));   // scannerRange
            w.F32(468, row.F(2));   // dtimeBurst
            w.F32(472, row.F(3));   // dispersion
            w.F32(476, row.F(4));   // accuracy
            w.I16(480, unchecked((short)row.I(5)));  // ammo
            w.I16(482, unchecked((short)row.I(6)));  // projectileTypeID
            w.I16(484, unchecked((short)row.I(7)));  // ambientSound
            w.F32(488, row.F(8));                    // dtRipcord
            result.Add(w.Build());
        }
        return result;
    }
}
