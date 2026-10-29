using Allegiance.Core;

namespace Allegiance.Tools.Core2Txt;

/// <summary>
/// <c>Constants</c> record codec. Payload is <c>float floatConstants[40]</c> then
/// <c>float damageConstants[20][20]</c> (<c>src/Igc/missionigc.h:21</c>,
/// <c>igc.h:565-568</c>). <c>Constants.csv</c> carries one row per value: 40
/// global rows then 400 damage rows (<c>ExportStaticIGCObjs</c>,
/// <c>missionigc.cpp:5398-5420</c>), read back by the native two-section rule
/// (<c>ImportStaticIGCObjs</c>, <c>missionigc.cpp:607-663</c>).
/// </summary>
public sealed class ConstantsCodec : ITypeCodec
{
    public IgcObjectType Type => IgcObjectType.Constants;

    public IReadOnlyList<TypeFileSpec> Files { get; } = new[] { CoreFiles.Constants };

    public IReadOnlyList<(string File, string Line)> Write(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog)
    {
        var lines = new List<(string, string)>();
        int off = 0;
        var floats = new float[CoreTable.CFcidMax];
        for (int i = 0; i < CoreTable.CFcidMax; i++, off += 4)
            floats[i] = Bin.F32(payload, off);

        for (int i = 0; i < CoreTable.CFcidMax; i++)
            lines.Add((CoreFiles.Constants.BaseName,
                CoreTable.FormatInt(i) + "\t" + CoreTable.FormatFloat(floats[i]) + "\t" +
                CoreTable.ConstantsGlobalComment(i)));

        for (int i = 0; i < CoreTable.CDmgidMax; i++)
            for (int j = 0; j < CoreTable.CDefidMax; j++, off += 4)
                lines.Add((CoreFiles.Constants.BaseName,
                    CoreTable.FormatInt(i) + "\t" + CoreTable.FormatFloat(Bin.F32(payload, off)) + "\t" +
                    CoreTable.FormatInt(j) + ": " + CoreTable.ConstantsDamageComment(i, j)));
        return lines;
    }

    public List<TextRecord> Read(TextCoreFiles text, CoreTable table)
    {
        var w = new PayloadBuilder(DataConstantsIGC.Size) { Id = 0 };
        var rows = text.Rows(CoreFiles.Constants.BaseName);
        int iLine = 0, cnt = 0;
        foreach (var cells in rows)
        {
            var row = new TextRow(cells);
            if (iLine < CoreTable.CFcidMax)
            {
                int id = row.I(0);
                if (row.Count >= 2 && id >= 0 && id < CoreTable.CFcidMax)
                    w.F32(id * 4, row.F(1));
            }
            else
            {
                int id = row.I(0);
                if (row.Count >= 2 && id >= 0 && id < CoreTable.CDmgidMax)
                    w.F32(DataConstantsIGC.DamageOffset + ((id * CoreTable.CDefidMax) + cnt) * 4, row.F(1));
                cnt++;
                if (cnt == CoreTable.CDefidMax) cnt = 0;
            }
            iLine++;
        }
        return new List<TextRecord> { w.Build() };
    }
}
