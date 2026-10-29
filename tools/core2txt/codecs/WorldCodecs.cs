using Allegiance.Core;

namespace Allegiance.Tools.Core2Txt;

/// <summary>
/// <c>OT_development</c> codec (468-byte <c>DataDevelopmentIGC</c>,
/// <c>igc.h:2691-2696</c>). Write: <c>Obj2Txt</c> <c>OT_development</c>
/// (<c>missionigc.cpp:5229-5285</c>); its non-default
/// <c>GlobalAttributeSet</c> goes to <c>GlobalAttributes.csv</c>. Read:
/// <c>ImportStaticIGCObjs</c> development block (<c>missionigc.cpp:1903-1975</c>).
/// </summary>
public sealed class DevelopmentCodec : ITypeCodec
{
    public IgcObjectType Type => IgcObjectType.Development;

    public IReadOnlyList<TypeFileSpec> Files { get; } = new[]
    {
        CoreFiles.Developments, CoreFiles.GlobalAttributes,
    };

    private const int Price = 0, TimeToBuild = 4, ModelName = 8, IconName = 22,
        Name = 35, Description = 60, GroupId = 261, TtbmRequired = 262,
        TtbmEffects = 312, Gas = 364;

    public IReadOnlyList<(string File, string Line)> Write(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog)
    {
        short id = Bin.I16(payload, 464);
        bool defaultGas = true;
        for (int i = 0; i < DataDevelopmentIGC.GaCount; i++)
            if (BitConverter.SingleToInt32Bits(Bin.F32(payload, Gas + 4 * i))
                != BitConverter.SingleToInt32Bits(1.0f)) { defaultGas = false; break; }

        var lines = new List<(string, string)>();
        if (!defaultGas)
        {
            var g = new List<string> { CoreTable.FormatInt(id), Bin.Str(payload, Name, 25) };
            for (int i = 0; i < DataDevelopmentIGC.GaCount; i++)
                g.Add(CoreTable.FormatFloat(Bin.F32(payload, Gas + 4 * i)));
            lines.Add((CoreFiles.GlobalAttributes.BaseName, string.Join('\t', g)));
        }

        var f = new List<string>
        {
            CoreTable.FormatInt(id),
            CoreTable.FormatInt(Bin.I16(payload, 466)),                 // completionSound
            CoreTable.FormatInt(Bin.I32(payload, Price)),
            CoreTable.FormatInt(unchecked((int)Bin.U32(payload, TimeToBuild))),
            Bin.Str(payload, ModelName, 14),
            Bin.Str(payload, IconName, 13),
            Bin.Str(payload, Name, 25),
            Bin.Str(payload, Description, 201),
            CoreTable.FormatInt(unchecked((sbyte)payload[GroupId])),
            table.BitsToTechsList(MaskBits(payload, TtbmRequired)),
            table.BitsToTechsList(MaskBits(payload, TtbmEffects)),
        };
        lines.Add((CoreFiles.Developments.BaseName, string.Join('\t', f)));
        return lines;
    }

    private static bool[] MaskBits(ReadOnlySpan<byte> payload, int off)
    {
        var bits = new bool[CoreTable.CTtbMax];
        CoreTable.FromBytes(payload.Slice(off, 50), bits);
        return bits;
    }

    public List<TextRecord> Read(TextCoreFiles text, CoreTable table)
    {
        var gasRows = text.Rows(CoreFiles.GlobalAttributes.BaseName);
        var result = new List<TextRecord>();
        foreach (var cells in text.Rows(CoreFiles.Developments.BaseName))
        {
            var row = new TextRow(cells);
            short id = unchecked((short)row.I(0));
            var w = new PayloadBuilder(DataDevelopmentIGC.Size) { Id = id };
            w.I16(464, id);
            w.I16(466, unchecked((short)row.I(1))); // completionSound
            w.I32(Price, row.I(2));
            w.U32(TimeToBuild, unchecked((uint)row.I(3)));
            w.Str(ModelName, 14, row.S(4));
            w.Str(IconName, 13, row.S(5));
            w.Str(Name, 25, row.S(6));
            w.Str(Description, 201, row.S(7));
            w.U8(GroupId, (byte)row.I(8));
            CodecUtil.WriteMask(w, TtbmRequired, CodecUtil.ParseTechList(table, row.S(9)));
            CodecUtil.WriteMask(w, TtbmEffects, CodecUtil.ParseTechList(table, row.S(10)));

            // GlobalAttributeSet: last matching row wins; a matching row with no
            // value columns leaves native's fgas[0] at -1.0f and initializes.
            // GlobalAttributes.csv has no owning-type column, and development and
            // civilization ids overlap, so native's dev reader also picks up a
            // CIVILIZATION row with the same id (its "COPY OF NAME" column is
            // ignored). Matching the row's name is the writer's intent and the
            // only way the round trip can be lossless.
            string devName = row.S(6);
            bool found = false;
            var gas = new float[DataDevelopmentIGC.GaCount];
            int written = 0;
            foreach (var gasCells in gasRows)
            {
                var g = new TextRow(gasCells);
                if (g.I(0) != id) continue;
                if (g.S(1) != devName) continue;
                found = true;
                written = 0;
                for (int i = 0; i < DataDevelopmentIGC.GaCount && i + 2 < g.Count; i++)
                {
                    gas[i] = g.F(i + 2);
                    written++;
                }
            }
            if (!found || written == 0)
            {
                for (int i = 0; i < DataDevelopmentIGC.GaCount; i++) w.F32(Gas + 4 * i, 1.0f);
            }
            else
            {
                for (int i = 0; i < written; i++) w.F32(Gas + 4 * i, gas[i]);
            }
            result.Add(w.Build());
        }
        return result;
    }
}

/// <summary>
/// <c>OT_droneType</c> codec (384-byte <c>DataDroneTypeIGC</c>,
/// <c>igc.h:2698-2707</c>). Write: <c>Obj2Txt</c> <c>OT_droneType</c>
/// (<c>missionigc.cpp:5289-5312</c>). Read: <c>missionigc.cpp:1977-2028</c>.
/// </summary>
public sealed class DroneCodec : ITypeCodec
{
    public IgcObjectType Type => IgcObjectType.DroneType;

    public IReadOnlyList<TypeFileSpec> Files { get; } = new[] { CoreFiles.TypesOfDrones };

    private const int Price = 0, TimeToBuild = 4, ModelName = 8, IconName = 22,
        Name = 35, Description = 60, GroupId = 261, TtbmRequired = 262,
        TtbmEffects = 312;

    public IReadOnlyList<(string File, string Line)> Write(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog)
    {
        var f = new List<string>
        {
            CoreTable.FormatInt(Bin.I16(payload, 380)),                 // droneTypeID
            CoreTable.FormatFloat(Bin.F32(payload, 364)),               // shootSkill
            CoreTable.FormatFloat(Bin.F32(payload, 368)),               // moveSkill
            CoreTable.FormatFloat(Bin.F32(payload, 372)),               // bravery
            CoreTable.FormatInt(payload[376]),                          // pilotType
            CoreTable.FormatInt(Bin.I16(payload, 378)),                 // hullTypeID
            CoreTable.FormatInt(Bin.I16(payload, 382)),                 // etidLaid
            CoreTable.FormatInt(Bin.I32(payload, Price)),
            CoreTable.FormatInt(unchecked((int)Bin.U32(payload, TimeToBuild))),
            Bin.Str(payload, ModelName, 14),
            Bin.Str(payload, IconName, 13),
            Bin.Str(payload, Name, 25),
            Bin.Str(payload, Description, 201),
            CoreTable.FormatInt(unchecked((sbyte)payload[GroupId])),
            table.BitsToTechsList(MaskBits(payload, TtbmRequired)),
            table.BitsToTechsList(MaskBits(payload, TtbmEffects)),
        };
        return new[] { (CoreFiles.TypesOfDrones.BaseName, string.Join('\t', f)) };
    }

    private static bool[] MaskBits(ReadOnlySpan<byte> payload, int off)
    {
        var bits = new bool[CoreTable.CTtbMax];
        CoreTable.FromBytes(payload.Slice(off, 50), bits);
        return bits;
    }

    public List<TextRecord> Read(TextCoreFiles text, CoreTable table)
    {
        var result = new List<TextRecord>();
        foreach (var cells in text.Rows(CoreFiles.TypesOfDrones.BaseName))
        {
            var row = new TextRow(cells);
            short id = unchecked((short)row.I(0));
            var w = new PayloadBuilder(DataDroneTypeIGC.Size) { Id = id };
            w.I16(380, id);
            w.F32(364, row.F(1));   // shootSkill
            w.F32(368, row.F(2));   // moveSkill
            w.F32(372, row.F(3));   // bravery
            w.U8(376, (byte)row.I(4));               // pilotType
            w.I16(378, unchecked((short)row.I(5)));  // hullTypeID
            w.I16(382, unchecked((short)row.I(6)));  // etidLaid
            w.I32(Price, row.I(7));
            w.U32(TimeToBuild, unchecked((uint)row.I(8)));
            w.Str(ModelName, 14, row.S(9));
            w.Str(IconName, 13, row.S(10));
            w.Str(Name, 25, row.S(11));
            w.Str(Description, 201, row.S(12));
            w.U8(GroupId, (byte)row.I(13));
            CodecUtil.WriteMask(w, TtbmRequired, CodecUtil.ParseTechList(table, row.S(14)));
            CodecUtil.WriteMask(w, TtbmEffects, CodecUtil.ParseTechList(table, row.S(15)));
            result.Add(w.Build());
        }
        return result;
    }
}

/// <summary>
/// <c>OT_civilization</c> codec (268-byte <c>DataCivilizationIGC</c>,
/// <c>igc.h:2071-2085</c>). Write: <c>Obj2Txt</c> <c>OT_civilization</c>
/// (<c>missionigc.cpp:5318-5360</c>), always appending its
/// <c>GlobalAttributeSet</c> row. Read: <c>missionigc.cpp:2212-2280</c>.
/// </summary>
public sealed class CivilizationCodec : ITypeCodec
{
    public IgcObjectType Type => IgcObjectType.Civilization;

    public IReadOnlyList<TypeFileSpec> Files { get; } = new[]
    {
        CoreFiles.Civilizations, CoreFiles.GlobalAttributes,
    };

    private const int GasBase = 160;

    public IReadOnlyList<(string File, string Line)> Write(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog)
    {
        short id = Bin.I16(payload, 262);
        string name = Bin.Str(payload, 8, 25);

        var g = new List<string> { CoreTable.FormatInt(id), name };
        for (int i = 0; i < DataCivilizationIGC.GaCount; i++)
            g.Add(CoreTable.FormatFloat(Bin.F32(payload, GasBase + 4 * i)));

        var f = new List<string>
        {
            CoreTable.FormatInt(id),
            CoreTable.FormatFloat(Bin.F32(payload, 0)),                 // incomeMoney
            CoreTable.FormatFloat(Bin.F32(payload, 4)),                 // bonusMoney
            name,
            Bin.Str(payload, 33, 13),                                   // iconName
            Bin.Str(payload, 46, 13),                                   // hudName
            CoreTable.FormatInt(Bin.I16(payload, 260)),                 // lifepod
            CoreTable.FormatInt(Bin.I16(payload, 264)),                 // initialStationTypeID
            table.BitsToTechsList(MaskBits(payload, 59)),               // ttbmBaseTechs
            table.BitsToTechsList(MaskBits(payload, 109)),              // ttbmNoDevTechs
        };
        return new[]
        {
            (CoreFiles.GlobalAttributes.BaseName, string.Join('\t', g)),
            (CoreFiles.Civilizations.BaseName, string.Join('\t', f)),
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
        var gasRows = text.Rows(CoreFiles.GlobalAttributes.BaseName);
        var result = new List<TextRecord>();
        foreach (var cells in text.Rows(CoreFiles.Civilizations.BaseName))
        {
            var row = new TextRow(cells);
            short id = unchecked((short)row.I(0));
            var w = new PayloadBuilder(DataCivilizationIGC.Size) { Id = id };
            w.I16(262, id);
            w.F32(0, row.F(1));                       // incomeMoney
            w.F32(4, row.F(2));                       // bonusMoney
            w.Str(8, 25, row.S(3));                   // name
            w.Str(33, 13, row.S(4));                  // iconName
            w.Str(46, 13, row.S(5));                  // hudName
            w.I16(260, unchecked((short)row.I(6)));   // lifepod
            w.I16(264, unchecked((short)row.I(7)));   // initialStationTypeID
            CodecUtil.WriteMask(w, 59, CodecUtil.ParseTechList(table, row.S(8)));
            CodecUtil.WriteMask(w, 109, CodecUtil.ParseTechList(table, row.S(9)));

            // Native sets the whole set from the (last) matching GlobalAttributes
            // row; no row means the destination was never initialised (native
            // copies uninitialised floats), so a foreign folder gets zeros. The
            // row's name disambiguates it from a development row with the same id.
            string civName = row.S(3);
            foreach (var gasCells in gasRows)
            {
                var g = new TextRow(gasCells);
                if (g.I(0) != id) continue;
                if (g.S(1) != civName) continue;
                for (int i = 0; i < DataCivilizationIGC.GaCount && i + 2 < g.Count; i++)
                    w.F32(GasBase + 4 * i, g.F(i + 2));
            }
            result.Add(w.Build());
        }
        return result;
    }
}

/// <summary>
/// <c>OT_treasureSet</c> codec. Payload is the 32-byte
/// <c>DataTreasureSetIGC</c> head plus 4-byte <c>TreasureData</c> entries
/// (<c>igc.h:2107-2121</c>). Write: <c>Obj2Txt</c> <c>OT_treasureSet</c>
/// (<c>missionigc.cpp:5364-5410</c>). Read: <c>missionigc.cpp:2280-2325</c>.
/// Native accumulates its entry counter across sets (a bug); the port sizes each
/// record from its own rows.
/// </summary>
public sealed class TreasureSetCodec : ITypeCodec
{
    public IgcObjectType Type => IgcObjectType.TreasureSet;

    public IReadOnlyList<TypeFileSpec> Files { get; } = new[] { CoreFiles.TypesOfTreasures };

    private const int HeadSize = DataTreasureSetIGC.HeadSize;

    private static string CodeName(byte code) => code switch
    {
        1 => "Part",
        2 => "Powerup",
        3 => "Development",
        4 => "Cash",
        5 => "Flag",
        _ => "",
    };

    public IReadOnlyList<(string File, string Line)> Write(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog)
    {
        int count = Math.Max(0, (payload.Length - HeadSize) / 4);
        var treasures = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
        {
            int off = HeadSize + 4 * i;
            if (i > 0) treasures.Append(' ');
            treasures.Append(CoreTable.FormatInt(Bin.U16(payload, off))).Append(' ');
            string code = CodeName(payload[off + 2]);
            if (code.Length > 0) treasures.Append(code).Append(' ');
            treasures.Append(CoreTable.FormatInt(payload[off + 3]));
        }

        var f = new List<string>
        {
            CoreTable.FormatInt(Bin.I16(payload, 26)),          // treasureSetID
            Bin.Str(payload, 0, 25),                             // name
            CoreTable.FormatInt(Bin.I16(payload, 28)),          // nTreasureData
            CoreTable.FormatBool(payload[30] != 0),             // bZoneOnly
            treasures.ToString(),
        };
        return new[] { (CoreFiles.TypesOfTreasures.BaseName, string.Join('\t', f)) };
    }

    public List<TextRecord> Read(TextCoreFiles text, CoreTable table)
    {
        var result = new List<TextRecord>();
        foreach (var cells in text.Rows(CoreFiles.TypesOfTreasures.BaseName))
        {
            var row = new TextRow(cells);
            short id = unchecked((short)row.I(0));
            int n = row.I(2);
            var entries = new List<(int Id, byte Code, int Chance)>();
            var tokens = CoreTable.Tokens(row.S(4)).GetEnumerator();
            for (int i = 0; i <= n; i++)
            {
                if (!tokens.MoveNext()) break;
                int tid = CoreTable.ParseInt(tokens.Current);
                if (!tokens.MoveNext()) break; // native breaks once nothing remains
                string codeText = tokens.Current;
                if (!tokens.MoveNext()) break;
                int chance = CoreTable.ParseInt(tokens.Current);
                byte code = codeText.StartsWith("Part", StringComparison.Ordinal) ? (byte)1
                    : codeText.StartsWith("Powerup", StringComparison.Ordinal) ? (byte)2
                    : codeText.StartsWith("Development", StringComparison.Ordinal) ? (byte)3
                    : codeText.StartsWith("Cash", StringComparison.Ordinal) ? (byte)4
                    : codeText.StartsWith("Flag", StringComparison.Ordinal) ? (byte)5
                    : (byte)0;
                entries.Add((tid, code, chance));
            }

            var w = new PayloadBuilder(HeadSize + 4 * entries.Count) { Id = id };
            w.Str(0, 25, row.S(1));                    // name
            w.I16(26, id);                             // treasureSetID
            w.I16(28, unchecked((short)n));            // nTreasureData
            w.Bool(30, row.B(3));                      // bZoneOnly
            for (int i = 0; i < entries.Count; i++)
            {
                int off = HeadSize + 4 * i;
                w.U16(off, unchecked((ushort)entries[i].Id));
                if (entries[i].Code != 0) w.U8(off + 2, entries[i].Code);
                w.U8(off + 3, (byte)entries[i].Chance);
            }
            result.Add(w.Build());
        }
        return result;
    }
}
