using Allegiance.Core;

namespace Allegiance.Tools.Core2Txt;

/// <summary>
/// <c>OT_stationType</c> codec (524-byte <c>DataStationTypeIGC</c>,
/// <c>igc.h:2649-2689</c>). Write: <c>Obj2Txt</c> <c>OT_stationType</c>
/// (<c>missionigc.cpp:4306-4432</c>), which also appends the
/// <c>StationsSFX.csv</c> row. Read: <c>ImportStaticIGCObjs</c> station block
/// (<c>missionigc.cpp:2060-2205</c>).
/// </summary>
public sealed class StationTypeCodec : ITypeCodec
{
    public IgcObjectType Type => IgcObjectType.StationType;

    public IReadOnlyList<TypeFileSpec> Files { get; } = new[]
    {
        CoreFiles.TypesOfStations, CoreFiles.StationsSFX,
    };

    private const int Price = 0, TimeToBuild = 4, ModelName = 8, IconName = 22,
        Name = 35, Description = 60, GroupId = 261, TtbmRequired = 262,
        TtbmEffects = 312;

    public IReadOnlyList<(string File, string Line)> Write(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog)
    {
        short id = Bin.I16(payload, 446);
        var f = new List<string>
        {
            CoreTable.FormatInt(id),
            CoreTable.FormatFloat(Bin.F32(payload, 364)),               // signature
            CoreTable.FormatFloat(Bin.F32(payload, 368)),               // maxArmorHitPoints
            CoreTable.FormatFloat(Bin.F32(payload, 372)),               // maxShieldHitPoints
            CoreTable.FormatFloat(Bin.F32(payload, 376)),               // armorRegeneration
            CoreTable.FormatFloat(Bin.F32(payload, 380)),               // shieldRegeneration
            CoreTable.FormatFloat(Bin.F32(payload, 384)),               // scannerRange
            CoreTable.FormatInt(Bin.I32(payload, 388)),                 // income
            CoreTable.FormatFloat(Bin.F32(payload, 392)),               // radius
            CoreTable.FormatInt(Bin.I16(payload, 448)),                 // successorStationTypeID
            CodecUtil.DefenseId(payload[450], asDefense: true),         // defenseTypeArmor
            CodecUtil.DefenseId(payload[451], asDefense: true),         // defenseTypeShield
            CoreTable.AbilitiesListToStrings(Bin.I16(payload, 452), CoreTable.ATStation),
            CoreTable.AbilitiesListToStrings(Bin.I16(payload, 454), CoreTable.ATAsteroid),
            CoreTable.FormatInt(payload[456]),                          // classID
            CoreTable.FormatInt(Bin.I16(payload, 458)),                 // constructionDroneTypeID
            Bin.Str(payload, 486, 13),                                  // textureName
            Bin.Str(payload, 499, 25),                                  // builderName
            CoreTable.FormatInt(Bin.I32(payload, Price)),
            CoreTable.FormatInt(unchecked((int)Bin.U32(payload, TimeToBuild))),
            Bin.Str(payload, ModelName, 14),
            Bin.Str(payload, IconName, 13),
            Bin.Str(payload, Name, 25),
            Bin.Str(payload, Description, 201),
            CoreTable.FormatInt(unchecked((sbyte)payload[GroupId])),
            table.BitsToTechsList(MaskBits(payload, TtbmRequired)),
            table.BitsToTechsList(MaskBits(payload, TtbmEffects)),
            table.BitsToTechsList(MaskBits(payload, 396)),              // ttbmLocal
        };
        var sfx = new List<string>
        {
            CoreTable.FormatInt(id),
            CoreTable.FormatInt(Bin.I16(payload, 460)), // constructorNeedRockSound
            CoreTable.FormatInt(Bin.I16(payload, 462)), // constructorUnderAttackSound
            CoreTable.FormatInt(Bin.I16(payload, 464)), // constructorDestroyedSound
            CoreTable.FormatInt(Bin.I16(payload, 466)), // completionSound
            CoreTable.FormatInt(Bin.I16(payload, 468)), // interiorSound
            CoreTable.FormatInt(Bin.I16(payload, 470)), // exteriorSound
            CoreTable.FormatInt(Bin.I16(payload, 472)), // interiorAlertSound
            CoreTable.FormatInt(Bin.I16(payload, 474)), // underAttackSound
            CoreTable.FormatInt(Bin.I16(payload, 476)), // criticalSound
            CoreTable.FormatInt(Bin.I16(payload, 478)), // destroyedSound
            CoreTable.FormatInt(Bin.I16(payload, 480)), // capturedSound
            CoreTable.FormatInt(Bin.I16(payload, 482)), // enemyCapturedSound
            CoreTable.FormatInt(Bin.I16(payload, 484)), // enemyDestroyedSound
        };
        return new[]
        {
            (CoreFiles.TypesOfStations.BaseName, string.Join('\t', f)),
            (CoreFiles.StationsSFX.BaseName, string.Join('\t', sfx)),
        };
    }

    private static bool[] MaskBits(ReadOnlySpan<byte> payload, int off)
    {
        var bits = new bool[CoreTable.CTtbMax];
        CoreTable.FromBytes(payload.Slice(off, 50), bits);
        return bits;
    }

    public List<TextRecord> Read(TextCoreFiles text, CoreTable table)
    {
        var sfxRows = text.Rows(CoreFiles.StationsSFX.BaseName);
        var result = new List<TextRecord>();
        foreach (var cells in text.Rows(CoreFiles.TypesOfStations.BaseName))
        {
            var row = new TextRow(cells);
            short id = unchecked((short)row.I(0));
            var w = new PayloadBuilder(DataStationTypeIGC.Size) { Id = id };
            w.I16(446, id);

            foreach (var sfxCells in sfxRows)
            {
                var s = new TextRow(sfxCells);
                if (s.I(0) != id) continue;
                w.I16(460, unchecked((short)s.I(1)));
                w.I16(462, unchecked((short)s.I(2)));
                w.I16(464, unchecked((short)s.I(3)));
                w.I16(466, unchecked((short)s.I(4)));
                w.I16(468, unchecked((short)s.I(5)));
                w.I16(470, unchecked((short)s.I(6)));
                w.I16(472, unchecked((short)s.I(7)));
                w.I16(474, unchecked((short)s.I(8)));
                w.I16(476, unchecked((short)s.I(9)));
                w.I16(478, unchecked((short)s.I(10)));
                w.I16(480, unchecked((short)s.I(11)));
                w.I16(482, unchecked((short)s.I(12)));
                w.I16(484, unchecked((short)s.I(13)));
            }

            w.F32(364, row.F(1));    // signature
            w.F32(368, row.F(2));    // maxArmorHitPoints
            w.F32(372, row.F(3));    // maxShieldHitPoints
            w.F32(376, row.F(4));    // armorRegeneration
            w.F32(380, row.F(5));    // shieldRegeneration
            w.F32(384, row.F(6));    // scannerRange
            w.I32(388, row.I(7));    // income
            w.F32(392, row.F(8));    // radius
            w.I16(448, unchecked((short)row.I(9)));  // successorStationTypeID
            w.U8(450, (byte)row.TokenInt(10));       // defenseTypeArmor
            w.U8(451, (byte)row.TokenInt(11));       // defenseTypeShield
            w.I16(452, CoreTable.AbilitiesListToMask(row.S(12), CoreTable.ATStation));
            w.I16(454, CoreTable.AbilitiesListToMask(row.S(13), CoreTable.ATAsteroid));
            w.U8(456, (byte)row.I(14));              // classID
            w.I16(458, unchecked((short)row.I(15))); // constructionDroneTypeID
            w.Str(486, 13, row.S(16));               // textureName
            w.Str(499, 25, row.S(17));               // builderName
            w.I32(Price, row.I(18));
            w.U32(TimeToBuild, unchecked((uint)row.I(19)));
            w.Str(ModelName, 14, row.S(20));
            w.Str(IconName, 13, row.S(21));
            w.Str(Name, 25, row.S(22));
            w.Str(Description, 201, row.S(23));
            w.U8(GroupId, (byte)row.I(24));
            CodecUtil.WriteMask(w, TtbmRequired, CodecUtil.ParseTechList(table, row.S(25)));
            CodecUtil.WriteMask(w, TtbmEffects, CodecUtil.ParseTechList(table, row.S(26)));
            CodecUtil.WriteMask(w, 396, CodecUtil.ParseTechList(table, row.S(27))); // ttbmLocal
            result.Add(w.Build());
        }
        return result;
    }
}
