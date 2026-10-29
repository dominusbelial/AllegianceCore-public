using Allegiance.Core;

namespace Allegiance.Tools.Core2Txt;

/// <summary>Shared helpers for the per-type codecs: native mask/token plumbing,
/// color/float token lists.</summary>
internal static class CodecUtil
{
    public const int TechMaskBytes = 50;
    public const int TechMaskBits = 400;

    /// <summary>Native <c>BitsToTechsList</c> over the 50 raw mask bytes at
    /// <paramref name="off"/> (<c>missionigc.cpp:377</c>).</summary>
    public static string TechList(CoreTable table, ReadOnlySpan<byte> payload, int off)
    {
        var bits = new bool[TechMaskBits];
        CoreTable.FromBytes(payload.Slice(off, TechMaskBytes), bits);
        return table.BitsToTechsList(bits);
    }

    /// <summary>Native <c>TechsListToBits</c> (clears, <c>"0"</c>, digit-leading
    /// hex, else names).</summary>
    public static bool[] ParseTechList(CoreTable table, string cell)
    {
        var bits = new bool[TechMaskBits];
        table.TechsListToBits(cell, bits);
        return bits;
    }

    /// <summary>Write the 50 mask bytes (MSB-first per byte) and mark them
    /// text-covered, matching native's <c>FromString</c> (which writes the whole
    /// mask, zeroing any bytes past the parsed prefix).</summary>
    public static void WriteMask(PayloadBuilder w, int off, bool[] bits)
    {
        for (int i = 0; i < TechMaskBytes; i++)
        {
            int v = 0;
            for (int k = 0; k < 8; k++)
            {
                int bit = i * 8 + k;
                if (bit < bits.Length && bits[bit]) v |= 0x80 >> k;
            }
            w.U8(off + i, (byte)v);
        }
    }

    /// <summary>Native <c>ZString(color.r) + " " + ...</c> is written as floats, so
    /// read them back as floats (native's own reader uses <c>atoi</c> and loses
    /// <c>0.5</c>; that is a reader defect, not the writer's intent).</summary>
    public static void WriteColor(PayloadBuilder w, int off, TextRow row, int col)
    {
        int k = 0;
        foreach (string t in CoreTable.Tokens(row.S(col)))
        {
            if (k >= 4) break;
            w.F32(off + 4 * k, CoreTable.ParseFloat(t));
            k++;
        }
    }

    public static string ColorString(ReadOnlySpan<byte> payload, int off)
        => CoreTable.FormatFloat(Bin.F32(payload, off)) + " "
         + CoreTable.FormatFloat(Bin.F32(payload, off + 4)) + " "
         + CoreTable.FormatFloat(Bin.F32(payload, off + 8)) + " "
         + CoreTable.FormatFloat(Bin.F32(payload, off + 12));

    /// <summary>Native reads a fixed-length float list with <c>GetToken()</c>;
    /// missing tokens leave the destination untouched.</summary>
    public static void WriteFloatList(PayloadBuilder w, int off, TextRow row, int col, int count)
    {
        int k = 0;
        foreach (string t in CoreTable.Tokens(row.S(col)))
        {
            if (k >= count) break;
            w.F32(off + 4 * k, CoreTable.ParseFloat(t));
            k++;
        }
    }

    /// <summary>Native <c>atoi(GetToken())</c> lists (preferred part types).</summary>
    public static void WriteShortList(PayloadBuilder w, int off, TextRow row, int col, int count)
    {
        int k = 0;
        foreach (string t in CoreTable.Tokens(row.S(col)))
        {
            if (k >= count) break;
            w.I16(off + 2 * k, unchecked((short)CoreTable.ParseInt(t)));
            k++;
        }
    }

    public static string FloatListString(ReadOnlySpan<byte> payload, int off, int count)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(CoreTable.FormatFloat(Bin.F32(payload, off + 4 * i)));
        }
        return sb.ToString();
    }

    public static string ShortListString(ReadOnlySpan<byte> payload, int off, int count)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(CoreTable.FormatInt(Bin.I16(payload, off + 2 * i)));
        }
        return sb.ToString();
    }

    /// <summary>Defense/damage columns are written as <c>"id (comment)"</c>; the
    /// reader takes the first token (<c>GetToken().GetInteger()</c>).</summary>
    public static string DefenseId(int id, bool asDefense)
        => CoreTable.FormatInt(id) + " (" + (asDefense
            ? CoreTable.ConstantsDamageComment(id, -1)
            : CoreTable.ConstantsDamageComment(-1, id)) + ")";

    /// <summary>LauncherDef <c>partMask</c> import uses a single-token
    /// <c>PartMaskFromToken</c>, not <c>PartsListToMask</c>
    /// (<c>missionigc.cpp:830,923,1013,1154</c>).</summary>
    public static short PartMaskSingle(CoreTable table, string cell, int et)
        => table.PartMaskFromToken(cell, et);
}
