using Allegiance.Core;

using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Tools.Core2Txt;

/// <summary>Little-endian field accessors over a raw static-core record payload.</summary>
public static class Bin
{
    public static short I16(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadInt16LittleEndian(b[off..]);
    public static ushort U16(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt16LittleEndian(b[off..]);
    public static int I32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadInt32LittleEndian(b[off..]);
    public static uint U32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadUInt32LittleEndian(b[off..]);
    public static float F32(ReadOnlySpan<byte> b, int off) => BinaryPrimitives.ReadSingleLittleEndian(b[off..]);

    /// <summary>NUL-terminated fixed-width char array, read like native
    /// <c>ZString(const char*)</c> (bytes after the first NUL are ignored).</summary>
    public static string Str(ReadOnlySpan<byte> b, int off, int width)
    {
        int end = off;
        int limit = Math.Min(off + width, b.Length);
        while (end < limit && b[end] != 0) end++;
        return Encoding.Latin1.GetString(b[off..end]);
    }
}

/// <summary>
/// Builds a payload from text the way native <c>ImportStaticIGCObjs</c> does and
/// records which bytes came from the text. Bytes that no text column writes keep
/// their value (zero for a foreign text folder; the original byte from
/// <c>.coremeta</c> for a round trip), which is exactly the set of bytes native's
/// own Text2Core leaves uninitialised or stale.
/// </summary>
public sealed class PayloadBuilder
{
    public byte[] Bytes { get; }
    public byte[] Covered { get; }
    public int Id { get; set; }

    public PayloadBuilder(int size)
    {
        Bytes = new byte[size];
        Covered = new byte[size];
    }

    public PayloadBuilder(byte[] bytes)
    {
        Bytes = bytes;
        Covered = new byte[bytes.Length];
    }

    private void Mark(int off, int len)
    {
        for (int i = off; i < off + len && i < Covered.Length; i++) Covered[i] = 1;
    }

    public void U8(int off, byte v) { Bytes[off] = v; Mark(off, 1); }
    public void Bool(int off, bool v) => U8(off, (byte)(v ? 1 : 0));
    public void I16(int off, short v) { BinaryPrimitives.WriteInt16LittleEndian(Bytes.AsSpan(off), v); Mark(off, 2); }
    public void U16(int off, ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(off), v); Mark(off, 2); }
    public void I32(int off, int v) { BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(off), v); Mark(off, 4); }
    public void U32(int off, uint v) { BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(off), v); Mark(off, 4); }
    public void F32(int off, float v) { BinaryPrimitives.WriteSingleLittleEndian(Bytes.AsSpan(off), v); Mark(off, 4); }

    /// <summary>Native <c>Strcpy(char[N], s)</c>: copy at most N-1 chars and
    /// NUL-terminate. Only the written chars + NUL are text-covered; the tail of
    /// the fixed buffer is not (native leaves whatever was there).</summary>
    public void Str(int off, int width, string s)
    {
        var dst = Bytes.AsSpan(off, width);
        int n = Math.Min(s.Length, width - 1);
        Encoding.Latin1.GetBytes(s.AsSpan(0, n), dst);
        if (s.Length < width)
        {
            dst[n] = 0;
            Mark(off, n + 1);
        }
        else
        {
            // The text cell is at least as long as the field: native's Strcpy
            // truncates to width-1 and NUL-terminates, but the byte in the last
            // position is then not determined by the text (the shipped cores
            // carry a 25th name byte there). Zero it for a foreign folder and
            // leave it to the .coremeta residue for a round trip.
            dst[width - 1] = 0;
            Mark(off, width - 1);
        }
    }

    public TextRecord Build() => new(Id, Bytes, Covered);
}

/// <summary>One reconstructed record: its native object id, payload bytes, and
/// the per-byte "taken from the text" mask.</summary>
public sealed record TextRecord(int Id, byte[] Payload, byte[] Covered);

/// <summary>One tab-delimited text file of a core folder, header removed.</summary>
public sealed class TextFile
{
    public TextFile(string name, IReadOnlyList<string[]> rows)
    {
        Name = name;
        Rows = rows;
    }

    public string Name { get; }
    public IReadOnlyList<string[]> Rows { get; }
}

/// <summary>A parsed core text folder: every known CSV keyed by base name.</summary>
public sealed class TextCoreFiles
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private readonly Dictionary<string, TextFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public static TextCoreFiles Load(string dir, IEnumerable<string> names)
    {
        var t = new TextCoreFiles();
        foreach (string name in names)
        {
            string path = Path.Combine(dir, name + ".csv");
            if (!File.Exists(path)) continue;
            var rows = new List<string[]>();
            var raw = CoreTable.ReadCsv(path);
            for (int i = 1; i < raw.Count; i++) // native skips the header row
            {
                var r = raw[i].ToArray();
                if (r.Length > 0) r[^1] = r[^1].TrimEnd('\r'); // CRLF terminator
                rows.Add(r);
            }
            t._files[name] = new TextFile(name, rows);
        }
        return t;
    }

    public IReadOnlyList<string[]> Rows(string name)
        => _files.TryGetValue(name, out var f) ? f.Rows : Array.Empty<string[]>();

    public bool Has(string name) => _files.ContainsKey(name);

    /// <summary>Write a file in native form: header, then each row, CRLF, Latin-1.</summary>
    public static void Write(string dir, TypeFileSpec spec, IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        sb.Append(spec.Header).Append("\r\n");
        foreach (string line in lines) sb.Append(line).Append("\r\n");
        File.WriteAllText(Path.Combine(dir, spec.BaseName + ".csv"), sb.ToString(), Latin1);
    }
}

/// <summary>Column access for one text row, with native <c>atoi</c>/<c>atof</c>
/// semantics and an empty string for missing columns.</summary>
public readonly struct TextRow
{
    private readonly string[] _cells;

    public TextRow(string[] cells) => _cells = cells;

    public int Count => _cells.Length;

    public string S(int i) => i < _cells.Length ? _cells[i] : "";

    /// <summary><c>atoi(col)</c>.</summary>
    public int I(int i) => CoreTable.ParseInt(S(i));

    /// <summary><c>atof(col)</c>.</summary>
    public float F(int i) => CoreTable.ParseFloat(S(i));

    /// <summary><c>atoi</c> of the first token, native <c>GetToken().GetInteger()</c>.</summary>
    public int TokenInt(int i) => CoreTable.ParseTokenInt(S(i));

    /// <summary>Native <c>ZString(bool)</c> output read back (<c>true</c>/<c>false</c>).</summary>
    public bool B(int i) => CoreTable.ParseBool(S(i));

    /// <summary><c>GetToken()</c> of a whole column (defense ids are printed as
    /// <c>"5 (Heavy Ship)"</c>).</summary>
    public string Token(int i)
    {
        foreach (string t in CoreTable.Tokens(S(i))) return t;
        return "";
    }

    /// <summary>Comma/space-separated floats: native reads a fixed count with
    /// <c>GetToken()</c> and leaves the rest untouched when tokens run out.</summary>
    public void ReadFloats(int i, Span<float> dst)
    {
        int k = 0;
        foreach (string t in CoreTable.Tokens(S(i)))
        {
            if (k >= dst.Length) break;
            dst[k++] = CoreTable.ParseFloat(t);
        }
    }

    /// <summary>Native writes <c>ZString(color.r) + " " + ...</c> (floats), then
    /// reads four <c>GetToken().GetInteger()</c> values. Reading the writer's
    /// spelling as a float is the lossless choice (e.g. <c>0.5</c>).</summary>
    public void ReadColor(int i, Span<float> dst) => ReadFloats(i, dst);
}

/// <summary>
/// Whole-core reference data the text writers need in addition to the record
/// itself: the native "COPY OF NAMES" debugging column of
/// <c>TypesOfProjectiles.csv</c>, and the launcher definitions that the launcher
/// part rows and the expendable launcher columns repeat.
/// </summary>
public sealed class CoreCatalog
{
    /// <summary>(projectileTypeId, name) of every probe, in record order.</summary>
    public List<(short ProjectileTypeId, string Name)> Probes { get; } = new();
    /// <summary>(projectileTypeId, name) of every weapon part, in record order.</summary>
    public List<(short ProjectileTypeId, string Name)> Weapons { get; } = new();
    /// <summary>Expendable id → its embedded <c>LauncherDef</c> buyable fields.</summary>
    public Dictionary<short, LauncherDefView> LauncherDefs { get; } = new();

    public readonly record struct LauncherDefView(
        int Price, uint TimeToBuild, string ModelName, string IconName, string Name,
        string Description, byte GroupId, bool[] TtbmRequired, bool[] TtbmEffects);

    public static CoreCatalog Build(IgcFile file)
    {
        var c = new CoreCatalog();
        var body = file.Body.Span;
        foreach (var r in file.Records)
        {
            var p = body.Slice(r.PayloadOffset, r.Size);
            switch (r.Type)
            {
                case IgcObjectType.ProbeType:
                    c.Probes.Add((Bin.I16(p, 482), Bin.Str(p, 99, 25)));
                    c.LauncherDefs[Bin.I16(p, 446)] = ReadLauncherDef(p);
                    break;
                case IgcObjectType.MissileType:
                case IgcObjectType.MineType:
                case IgcObjectType.ChaffType:
                    c.LauncherDefs[Bin.I16(p, 446)] = ReadLauncherDef(p);
                    break;
                case IgcObjectType.PartType:
                    if (r.Size == 24) break; // launcher: linked via its expendable
                    int et = Bin.I16(p, 376);
                    if (et == CoreTable.ETWeapon)
                        c.Weapons.Add((Bin.I16(p, 414), Bin.Str(p, 35, 25)));
                    break;
            }
        }
        return c;
    }

    private static LauncherDefView ReadLauncherDef(ReadOnlySpan<byte> p)
    {
        var req = new bool[CoreTable.CTtbMax];
        var eff = new bool[CoreTable.CTtbMax];
        CoreTable.FromBytes(p.Slice(326, 50), req);
        CoreTable.FromBytes(p.Slice(376, 50), eff);
        return new LauncherDefView(
            Bin.I32(p, 64), Bin.U32(p, 68), Bin.Str(p, 72, 14), Bin.Str(p, 86, 13),
            Bin.Str(p, 99, 25), Bin.Str(p, 124, 201), p[325], req, eff);
    }

    /// <summary>Native mask bytes → the <c>%02X</c> spelling <c>FromString</c> expects.</summary>
    public static string HexOf(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>The debugging name list native appends to each projectile row:
    /// every matching probe name, else the first matching weapon part name, else
    /// <c>Unused</c> (<c>missionigc.cpp:4459-4485</c>).</summary>
    public string ProjectileNames(short projectileTypeId)
    {
        var sb = new StringBuilder();
        foreach (var (id, name) in Probes)
            if (id == projectileTypeId) sb.Append(name).Append(' ');
        if (sb.Length > 0) return sb.ToString();
        foreach (var (id, name) in Weapons)
            if (id == projectileTypeId) return name + " ";
        return "Unused";
    }
}
