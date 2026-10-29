using Allegiance.Core;

namespace Allegiance.Tools.Core2Txt;

/// <summary>
/// <c>OT_hullType</c> codec. A record is the 540-byte
/// <c>DataHullTypeIGC</c> (<c>igc.h:1767-1820</c>) followed by
/// <c>maxWeapons</c> 30-byte <c>HardpointData</c> records (<c>igc.h:1696-1707</c>),
/// the count and stride the corpus reader measures
/// (<see cref="DataHullTypeIGC.Size"/>/<see cref="DataHullTypeIGC.HardpointStride"/>).
/// Write: <c>Obj2Txt</c> <c>OT_hullType</c> (<c>missionigc.cpp:5056-5225</c>).
/// Read: <c>ImportStaticIGCObjs</c> hull block (<c>missionigc.cpp:1851-2000</c>).
///
/// <para>Two native writer/reader asymmetries are resolved toward the native
/// header/reader, which is also what makes the round trip lossless:</para>
/// <list type="bullet">
/// <item>the writer emits the dispenser mask before the magazine mask while the
/// header and the reader use ALLOWED MISSILES (magazine) then ALLOWED
/// DISPENSERS; both sides here follow the header;</item>
/// <item><c>pmEquipment[ET_Weapon]</c> / <c>[ET_Pack]</c> are not text columns and
/// are not text-covered (native's reader zeroes them; a round trip keeps the
/// original bytes, exactly like struct padding).</item>
/// </list>
/// </summary>
public sealed class HullCodec : ITypeCodec
{
    public IgcObjectType Type => IgcObjectType.HullType;

    public IReadOnlyList<TypeFileSpec> Files { get; } = new[]
    {
        CoreFiles.TypesOfShips, CoreFiles.Hardpoints, CoreFiles.ShipsSFX,
    };

    private const int Price = 0, TimeToBuild = 4, ModelName = 8, IconName = 22,
        Name = 35, Description = 60, GroupId = 261, TtbmRequired = 262,
        TtbmEffects = 312;
    private const int Base = 540, HardpointStride = 30;

    public IReadOnlyList<(string File, string Line)> Write(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog)
    {
        int maxWeapons = payload[450];
        string name = Bin.Str(payload, Name, 25);
        string model = Bin.Str(payload, ModelName, 14);
        short hullId = Bin.I16(payload, 446);

        var hps = new List<(string, string)>();
        for (int i = 0; i < maxWeapons && Base + (i + 1) * HardpointStride <= payload.Length; i++)
        {
            var hp = payload.Slice(Base + i * HardpointStride, HardpointStride);
            var hf = new List<string>
            {
                CoreTable.FormatInt(hullId),
                CoreTable.FormatInt(Bin.I16(hp, 0)),        // interiorSound
                CoreTable.FormatInt(Bin.I16(hp, 2)),        // turnSound
                Bin.Str(hp, 4, 13),                          // frameName
                Bin.Str(hp, 17, 9),                          // locationAbreviation
                table.BitsToPartsList(Bin.I16(hp, 26), CoreTable.ETWeapon),
                CoreTable.FormatBool(hp[28] != 0),          // bFixed
                name + " (" + model + ")",
            };
            hps.Add((CoreFiles.Hardpoints.BaseName, string.Join('\t', hf)));
        }

        var f = new List<string>
        {
            CoreTable.FormatInt(hullId),
            CoreTable.FormatFloat(Bin.F32(payload, 364)),                // mass
            CoreTable.FormatFloat(Bin.F32(payload, 368)),                // signature
            CoreTable.FormatFloat(Bin.F32(payload, 372)),                // speed
            CodecUtil.FloatListString(payload, 376, 3),                  // maxTurnRates
            CodecUtil.FloatListString(payload, 388, 3),                  // turnTorques
            CoreTable.FormatFloat(Bin.F32(payload, 400)),                // thrust
            CoreTable.FormatFloat(Bin.F32(payload, 404)),                // sideMultiplier
            CoreTable.FormatFloat(Bin.F32(payload, 408)),                // backMultiplier
            CoreTable.FormatFloat(Bin.F32(payload, 412)),                // scannerRange
            CoreTable.FormatFloat(Bin.F32(payload, 416)),                // maxFuel
            CoreTable.FormatFloat(Bin.F32(payload, 420)),                // ecm
            CoreTable.FormatFloat(Bin.F32(payload, 424)),                // length
            CoreTable.FormatFloat(Bin.F32(payload, 428)),                // maxEnergy
            CoreTable.FormatFloat(Bin.F32(payload, 432)),                // rechargeRate
            CoreTable.FormatFloat(Bin.F32(payload, 436)),                // ripcordSpeed
            CoreTable.FormatFloat(Bin.F32(payload, 440)),                // ripcordCost
            CoreTable.FormatInt(Bin.I16(payload, 444)),                  // maxAmmo
            CoreTable.FormatInt(Bin.I16(payload, 448)),                  // successorHullID
            CoreTable.FormatInt(maxWeapons),
            CoreTable.FormatInt(payload[451]),                           // maxFixedWeapons
            CoreTable.FormatFloat(Bin.F32(payload, 452)),                // hitPoints
            CodecUtil.DefenseId(payload[458], asDefense: true),          // defenseType
            CoreTable.FormatInt(Bin.I16(payload, 460)),                  // capacityMagazine
            CoreTable.FormatInt(Bin.I16(payload, 462)),                  // capacityDispenser
            CoreTable.FormatInt(Bin.I16(payload, 464)),                  // capacityChaffLauncher
            CodecUtil.ShortListString(payload, 466, 14),                 // preferredPartsTypes
            CoreTable.AbilitiesListToStrings(Bin.I16(payload, 494), CoreTable.ATHull),
            Bin.Str(payload, 496, 13),                                   // textureName
            table.BitsToPartsList(Bin.I16(payload, 510), CoreTable.ETChaffLauncher),
            table.BitsToPartsList(Bin.I16(payload, 514), CoreTable.ETMagazine),
            table.BitsToPartsList(Bin.I16(payload, 516), CoreTable.ETDispenser),
            table.BitsToPartsList(Bin.I16(payload, 518), CoreTable.ETShield),
            table.BitsToPartsList(Bin.I16(payload, 520), CoreTable.ETCloak),
            table.BitsToPartsList(Bin.I16(payload, 524), CoreTable.ETAfterburner),
            CoreTable.FormatInt(Bin.I32(payload, Price)),
            CoreTable.FormatInt(unchecked((int)Bin.U32(payload, TimeToBuild))),
            model,
            Bin.Str(payload, IconName, 13),
            name,
            Bin.Str(payload, Description, 201),
            CoreTable.FormatInt(unchecked((sbyte)payload[GroupId])),
            table.BitsToTechsList(MaskBits(payload, TtbmRequired)),
            table.BitsToTechsList(MaskBits(payload, TtbmEffects)),
        };
        hps.Add((CoreFiles.TypesOfShips.BaseName, string.Join('\t', f)));

        var sfx = new List<string>
        {
            CoreTable.FormatInt(hullId),
            name,
            CoreTable.FormatInt(Bin.I16(payload, 526)), // interiorSound
            CoreTable.FormatInt(Bin.I16(payload, 528)), // exteriorSound
            CoreTable.FormatInt(Bin.I16(payload, 530)), // mainThrusterInteriorSound
            CoreTable.FormatInt(Bin.I16(payload, 532)), // mainThrusterExteriorSound
            CoreTable.FormatInt(Bin.I16(payload, 534)), // manuveringThrusterInteriorSound
            CoreTable.FormatInt(Bin.I16(payload, 536)), // manuveringThrusterExteriorSound
        };
        hps.Add((CoreFiles.ShipsSFX.BaseName, string.Join('\t', sfx)));
        return hps;
    }

    private static bool[] MaskBits(ReadOnlySpan<byte> payload, int off)
    {
        var bits = new bool[CoreTable.CTtbMax];
        CoreTable.FromBytes(payload.Slice(off, 50), bits);
        return bits;
    }

    public List<TextRecord> Read(TextCoreFiles text, CoreTable table)
    {
        var hpRows = text.Rows(CoreFiles.Hardpoints.BaseName);
        var sfxRows = text.Rows(CoreFiles.ShipsSFX.BaseName);
        var result = new List<TextRecord>();

        foreach (var cells in text.Rows(CoreFiles.TypesOfShips.BaseName))
        {
            var row = new TextRow(cells);
            int hullId = row.I(0);
            int maxWeapons = row.I(19);
            var w = new PayloadBuilder(Base + HardpointStride * Math.Max(0, maxWeapons)) { Id = hullId };

            // ShipsSFX: native scans every row (no early break).
            foreach (var sfxCells in sfxRows)
            {
                var s = new TextRow(sfxCells);
                if (s.I(0) != hullId) continue;
                w.I16(526, unchecked((short)s.I(2)));
                w.I16(528, unchecked((short)s.I(3)));
                w.I16(530, unchecked((short)s.I(4)));
                w.I16(532, unchecked((short)s.I(5)));
                w.I16(534, unchecked((short)s.I(6)));
                w.I16(536, unchecked((short)s.I(7)));
            }

            w.I16(446, unchecked((short)hullId));
            w.F32(364, row.F(1));    // mass
            w.F32(368, row.F(2));    // signature
            w.F32(372, row.F(3));    // speed
            CodecUtil.WriteFloatList(w, 376, row, 4, 3);  // maxTurnRates
            CodecUtil.WriteFloatList(w, 388, row, 5, 3);  // turnTorques
            w.F32(400, row.F(6));    // thrust
            w.F32(404, row.F(7));    // sideMultiplier
            w.F32(408, row.F(8));    // backMultiplier
            w.F32(412, row.F(9));    // scannerRange
            w.F32(416, row.F(10));   // maxFuel
            w.F32(420, row.F(11));   // ecm
            w.F32(424, row.F(12));   // length
            w.F32(428, row.F(13));   // maxEnergy
            w.F32(432, row.F(14));   // rechargeRate
            w.F32(436, row.F(15));   // ripcordSpeed
            w.F32(440, row.F(16));   // ripcordCost
            w.I16(444, unchecked((short)row.I(17))); // maxAmmo
            w.I16(448, unchecked((short)row.I(18))); // successorHullID
            w.U8(450, (byte)maxWeapons);
            w.U8(451, (byte)row.I(20));              // maxFixedWeapons
            w.F32(452, row.F(21));                   // hitPoints
            w.U8(458, (byte)row.TokenInt(22));       // defenseType
            w.I16(460, unchecked((short)row.I(23))); // capacityMagazine
            w.I16(462, unchecked((short)row.I(24))); // capacityDispenser
            w.I16(464, unchecked((short)row.I(25))); // capacityChaffLauncher
            CodecUtil.WriteShortList(w, 466, row, 26, 14); // preferredPartsTypes
            w.I16(494, CoreTable.AbilitiesListToMask(row.S(27), CoreTable.ATHull));
            w.Str(496, 13, row.S(28));               // textureName

            // Header/reader order: chaff, magazine, dispenser, shield, cloak, booster.
            w.I16(510, CodecUtil.PartMaskSingle(table, row.S(29), CoreTable.ETChaffLauncher));
            w.I16(514, CodecUtil.PartMaskSingle(table, row.S(30), CoreTable.ETMagazine));
            w.I16(516, CodecUtil.PartMaskSingle(table, row.S(31), CoreTable.ETDispenser));
            w.I16(518, CodecUtil.PartMaskSingle(table, row.S(32), CoreTable.ETShield));
            w.I16(520, CodecUtil.PartMaskSingle(table, row.S(33), CoreTable.ETCloak));
            w.I16(524, CodecUtil.PartMaskSingle(table, row.S(34), CoreTable.ETAfterburner));

            w.I32(Price, row.I(35));
            w.U32(TimeToBuild, unchecked((uint)row.I(36)));
            w.Str(ModelName, 14, row.S(37));
            w.Str(IconName, 13, row.S(38));
            w.Str(Name, 25, row.S(39));
            w.Str(Description, 201, row.S(40));
            w.U8(GroupId, (byte)row.I(41));
            CodecUtil.WriteMask(w, TtbmRequired, CodecUtil.ParseTechList(table, row.S(42)));
            CodecUtil.WriteMask(w, TtbmEffects, CodecUtil.ParseTechList(table, row.S(43)));

            // Hardpoints in file order; native's counter increments per matching
            // row and its record size is base + 30 * iHardpoints.
            int hpIndex = 0;
            foreach (var hpCells in hpRows)
            {
                var h = new TextRow(hpCells);
                if (h.I(0) != hullId) continue;
                if (hpIndex >= maxWeapons || Base + (hpIndex + 1) * HardpointStride > w.Bytes.Length) break;
                int off = Base + hpIndex * HardpointStride;
                w.I16(off, unchecked((short)h.I(1)));        // interiorSound
                w.I16(off + 2, unchecked((short)h.I(2)));    // turnSound
                w.Str(off + 4, 13, h.S(3));                  // frameName
                w.Str(off + 17, 9, h.S(4));                  // locationAbreviation
                w.I16(off + 26, table.PartsListToMask(h.S(5), CoreTable.ETWeapon));
                w.Bool(off + 28, h.B(6));                    // bFixed
                hpIndex++;
            }

            w.I16(456, Base); // hardpointOffset: native Import writes sizeof(DataHullTypeIGC)
            result.Add(w.Build());
        }
        return result;
    }
}
