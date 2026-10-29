using Allegiance.Core;

namespace Allegiance.Tools.Core2Txt;

/// <summary>
/// <c>OT_projectileType</c> codec (88-byte payload, <c>igc.h:1924</c>).
/// Write side: <c>Obj2Txt</c> <c>OT_projectileType</c>
/// (<c>missionigc.cpp:4456-4505</c>). Read side:
/// <c>ImportStaticIGCObjs</c> projectile block (<c>missionigc.cpp:672-745</c>).
/// </summary>
public sealed class ProjectileCodec : ITypeCodec
{
    public IgcObjectType Type => IgcObjectType.ProjectileType;

    public IReadOnlyList<TypeFileSpec> Files { get; } = new[] { CoreFiles.TypesOfProjectiles };

    public IReadOnlyList<(string File, string Line)> Write(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog)
    {
        var f = new List<string>
        {
            CoreTable.FormatInt(Bin.I16(payload, 72)),                  // projectileTypeID
            CoreTable.FormatFloat(Bin.F32(payload, 52)),                // power
            CoreTable.FormatFloat(Bin.F32(payload, 56)),                // blastPower
            CoreTable.FormatFloat(Bin.F32(payload, 60)),                // blastRadius
            CoreTable.FormatFloat(Bin.F32(payload, 64)),                // speed
            CoreTable.FormatFloat(Bin.F32(payload, 68)),                // lifespan
            CodecUtil.DefenseId(payload[74], asDefense: false),         // damageType (comment)
            CoreTable.FormatBool(payload[75] != 0),                     // absoluteF
            CoreTable.FormatBool(payload[76] != 0),                     // bDirectional
            CoreTable.FormatFloat(Bin.F32(payload, 80)),                // width
            CoreTable.FormatInt(Bin.I16(payload, 84)),                  // ambientSound
            CodecUtil.ColorString(payload, 0),                          // color
            CoreTable.FormatFloat(Bin.F32(payload, 16)),                // DataObjectIGC radius
            CoreTable.FormatFloat(Bin.F32(payload, 20)),                // rotation
            Bin.Str(payload, 24, 13),                                   // modelName
            Bin.Str(payload, 37, 13),                                   // textureName
            catalog.ProjectileNames(Bin.I16(payload, 72)),              // COPY OF NAMES
        };
        return new[] { (CoreFiles.TypesOfProjectiles.BaseName, string.Join('\t', f)) };
    }

    public List<TextRecord> Read(TextCoreFiles text, CoreTable table)
    {
        var result = new List<TextRecord>();
        foreach (var cells in text.Rows(CoreFiles.TypesOfProjectiles.BaseName))
        {
            var row = new TextRow(cells);
            var w = new PayloadBuilder(DataProjectileTypeIGC.Size) { Id = row.I(0) };
            w.I16(72, unchecked((short)row.I(0)));          // projectileTypeID
            w.F32(52, row.F(1));                            // power
            w.F32(56, row.F(2));                            // blastPower
            w.F32(60, row.F(3));                            // blastRadius
            w.F32(64, row.F(4));                            // speed
            w.F32(68, row.F(5));                            // lifespan
            w.U8(74, (byte)row.TokenInt(6));                // damageType
            w.Bool(75, row.B(7));                           // absoluteF
            w.Bool(76, row.B(8));                           // bDirectional
            w.F32(80, row.F(9));                            // width
            w.I16(84, unchecked((short)row.I(10)));         // ambientSound
            CodecUtil.WriteColor(w, 0, row, 11);            // color
            w.F32(16, row.F(12));                           // radius
            w.F32(20, row.F(13));                           // rotation
            w.Str(24, 13, row.S(14));                       // modelName
            w.Str(37, 13, row.S(15));                       // textureName
            result.Add(w.Build());
        }
        return result;
    }
}
