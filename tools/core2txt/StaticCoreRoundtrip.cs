using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Allegiance.Core;

namespace Allegiance.Tools.Core2Txt;

/// <summary>
/// Orchestrates the static-core text round trip: an <see cref="IgcFile"/>
/// ↔ a text folder. Mirrors native Core2Text / Text2Core
/// (<c>FedSrv/FedSrv.CPP:10385-10480</c>): Core2Text = LoadIGCStaticCore +
/// ExportStaticIGCObjs; Text2Core = ImportStaticIGCObjs + DumpIGCStaticCore.
///
/// <para><b>Text format.</b> One tab-delimited file per native table, with the
/// native headers, comments, column order and <c>%g</c> float spelling (see the
/// codecs). The text is byte-compatible with native's: a native editor can read
/// it, and <c>core2txt</c> reads native-written text.</para>
///
/// <para><b>Byte-identical round trip.</b> Native itself cannot round-trip
/// byte-identically: <c>DumpIGCStaticCore</c> stamps a fresh <c>time(0)</c> version
/// (<c>missionigc.cpp:2358</c>), and <c>ImportStaticIGCObjs</c> allocates payloads
/// with <c>new char[]</c>, so struct padding and the bytes after a string's NUL
/// keep whatever the heap contained (the shipped cores carry <c>0xCC</c>/<c>0xCD</c>
/// there). <c>core2txt</c> preserves exactly those bytes — the bytes no text column
/// determines — in a compact <c>.coremeta</c> sidecar, together with the version
/// word and the record sequence. They are restored on import, giving
/// <c>igc → text → igc</c> byte identity for the shipped cores. A foreign text
/// folder without the sidecar imports in native's canonical order with zeros in
/// the non-text bytes (what a deterministic native build would produce) and a
/// fresh version stamp.</para>
/// </summary>
public static class StaticCoreRoundtrip
{
    public const string MetaFileName = ".coremeta";
    private const string MetaBanner = "core2txt 2";

    private static readonly object RegisterLock = new();
    private static volatile bool _registered;

    /// <summary>Register every static-core type codec (idempotent).</summary>
    public static void RegisterCodecs()
    {
        lock (RegisterLock)
        {
            if (_registered) return;
            RegisterAll();
            _registered = true;
        }
    }

    private static void RegisterAll()
    {
        TypeCodecRegistry.Register(new ConstantsCodec());
        TypeCodecRegistry.Register(new ProjectileCodec());
        TypeCodecRegistry.Register(new MissileCodec());
        TypeCodecRegistry.Register(new MineCodec());
        TypeCodecRegistry.Register(new ChaffCodec());
        TypeCodecRegistry.Register(new ProbeCodec());
        TypeCodecRegistry.Register(new PartCodec());
        TypeCodecRegistry.Register(new HullCodec());
        TypeCodecRegistry.Register(new StationTypeCodec());
        TypeCodecRegistry.Register(new DevelopmentCodec());
        TypeCodecRegistry.Register(new DroneCodec());
        TypeCodecRegistry.Register(new CivilizationCodec());
        TypeCodecRegistry.Register(new TreasureSetCodec());
    }

    // ------------------------------------------------------------------
    // Export (.igc → text folder)
    // ------------------------------------------------------------------

    /// <summary>Write the native text folder for a parsed static core.</summary>
    /// <exception cref="NotSupportedException">A record type has no codec.</exception>
    public static void Export(IgcFile file, string outDir)
    {
        RegisterCodecs();
        Directory.CreateDirectory(outDir);

        var table = new CoreTable();
        table.LoadTechBitsList(Path.Combine(outDir, "BitsOfTechs.csv"));
        table.LoadPartsBitsList(Path.Combine(outDir, "BitsOfParts.csv"));
        var catalog = CoreCatalog.Build(file);

        var byType = new Dictionary<IgcObjectType, List<IgcRecord>>();
        foreach (var r in file.Records)
        {
            if (!byType.TryGetValue(r.Type, out var list)) byType[r.Type] = list = new List<IgcRecord>();
            list.Add(r);
        }
        foreach (var type in byType.Keys)
            if (TypeCodecRegistry.Get(type) is null)
                throw new NotSupportedException($"no core2txt codec for record type {type} ({(short)type})");

        var lines = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var body = file.Body.Span;
        foreach (var type in TypeCodecRegistry.ExportOrder)
        {
            if (!byType.TryGetValue(type, out var recs)) continue;
            var codec = TypeCodecRegistry.Get(type)!;
            foreach (var r in recs)
            {
                foreach (var (fileName, line) in codec.Write(body.Slice(r.PayloadOffset, r.Size), table, catalog))
                {
                    if (!lines.TryGetValue(fileName, out var target))
                        lines[fileName] = target = new List<string>();
                    target.Add(line);
                }
            }
        }

        foreach (var spec in CoreFiles.All)
            TextCoreFiles.Write(outDir, spec, lines.TryGetValue(spec.BaseName, out var l) ? l : Array.Empty<string>());

        WriteMeta(file, outDir, table);
    }

    private static void WriteMeta(IgcFile file, string outDir, CoreTable table)
    {
        var text = TextCoreFiles.Load(outDir, CoreFiles.Names);
        var imported = ImportAll(text, table);
        var index = new Dictionary<(IgcObjectType, int), TextRecord>();
        foreach (var (type, records) in imported)
            foreach (var tr in records)
                index[(type, tr.Id)] = tr;

        var sb = new StringBuilder();
        sb.Append(MetaBanner).Append('\n');
        sb.Append("version\t").Append(file.Version?.ToString(CultureInfo.InvariantCulture) ?? "").Append('\n');

        var body = file.Body.Span;
        foreach (var r in file.Records)
        {
            var payload = body.Slice(r.PayloadOffset, r.Size);
            if (!index.TryGetValue((r.Type, TypeCodecRegistry.RecordId(r.Type, payload)), out var tr))
                throw new InvalidDataException(
                    $"core2txt: {r.Type} record {(short)r.Type} did not survive the text pass");
            sb.Append("rec\t").Append(((short)r.Type).ToString(CultureInfo.InvariantCulture))
              .Append('\t').Append(tr.Id.ToString(CultureInfo.InvariantCulture))
              .Append('\t').Append(EncodeResidue(payload, tr.Covered))
              .Append('\n');
        }
        File.WriteAllText(Path.Combine(outDir, MetaFileName), sb.ToString(), Encoding.Latin1);
    }

    /// <summary>Sparse list of the nonzero bytes the text does not determine:
    /// <c>off:HH,off:HH</c> (or <c>-</c>): native's uninitialised padding and the
    /// stale bytes after a string's NUL.</summary>
    private static string EncodeResidue(ReadOnlySpan<byte> payload, byte[] covered)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < payload.Length; i++)
        {
            if (i < covered.Length && covered[i] != 0) continue;
            if (payload[i] == 0) continue;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(':')
              .Append(payload[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    // ------------------------------------------------------------------
    // Import (text folder → body / .igc)
    // ------------------------------------------------------------------

    private sealed record MetaEntry(IgcObjectType Type, int Id, List<(int Offset, byte Value)> Residue);

    /// <summary>Run every codec over a text folder, keyed by record type.</summary>
    private static Dictionary<IgcObjectType, List<TextRecord>> ImportAll(TextCoreFiles text, CoreTable table)
    {
        RegisterCodecs();
        var result = new Dictionary<IgcObjectType, List<TextRecord>>();
        foreach (var type in TypeCodecRegistry.ExportOrder)
        {
            var codec = TypeCodecRegistry.Get(type);
            if (codec is null) continue;
            result[type] = codec.Read(text, table);
        }
        return result;
    }

    /// <summary>Import a text folder into the record body (no wrapper).</summary>
    public static byte[] ImportBody(string textDir, out int? version)
    {
        RegisterCodecs();
        var (v, entries) = ReadMeta(Path.Combine(textDir, MetaFileName));
        version = v;

        var table = new CoreTable();
        table.LoadTechBitsList(Path.Combine(textDir, "BitsOfTechs.csv"));
        table.LoadPartsBitsList(Path.Combine(textDir, "BitsOfParts.csv"));

        var text = TextCoreFiles.Load(textDir, CoreFiles.Names);
        // Native Text2Core refuses a folder without Constants.csv
        // (FedSrv/FedSrv.CPP:10451-10457); say so instead of emitting zeros.
        if (!text.Has(CoreFiles.Constants.BaseName))
            throw new InvalidDataException(
                $"{textDir}: not a core text folder (missing {CoreFiles.Constants.BaseName}.csv)");
        var imported = ImportAll(text, table);

        var body = new MemoryStream();
        var header = new byte[6];
        var consumed = new HashSet<TextRecord>(ReferenceEqualityComparer.Instance);

        void Emit(IgcObjectType type, byte[] payload)
        {
            BinaryPrimitives.WriteInt16LittleEndian(header, (short)type);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), payload.Length);
            body.Write(header);
            body.Write(payload);
        }

        if (entries.Count == 0)
        {
            foreach (var type in TypeCodecRegistry.NativeImportOrder)
                if (imported.TryGetValue(type, out var records))
                    foreach (var tr in records)
                        Emit(type, tr.Payload);
            return body.ToArray();
        }

        // With a sidecar: replay the original record sequence, matching each text
        // record by its native object id and restoring the non-text residue.
        var queues = new Dictionary<(IgcObjectType, int), Queue<TextRecord>>();
        foreach (var (type, records) in imported)
            foreach (var tr in records)
            {
                var key = (type, tr.Id);
                if (!queues.TryGetValue(key, out var q)) queues[key] = q = new Queue<TextRecord>();
                q.Enqueue(tr);
            }

        foreach (var entry in entries)
        {
            if (!queues.TryGetValue((entry.Type, entry.Id), out var q) || q.Count == 0) continue;
            var tr = q.Dequeue();
            consumed.Add(tr);
            var payload = (byte[])tr.Payload.Clone();
            foreach (var (off, value) in entry.Residue)
                if (off >= 0 && off < payload.Length) payload[off] = value;
            Emit(entry.Type, payload);
        }

        // Text rows the sidecar does not know (a user added objects) are appended
        // in native order so an edited folder still builds.
        foreach (var type in TypeCodecRegistry.NativeImportOrder)
        {
            if (!imported.TryGetValue(type, out var records)) continue;
            foreach (var tr in records)
                if (!consumed.Contains(tr))
                    Emit(type, tr.Payload);
        }

        return body.ToArray();
    }

    /// <summary>Rebuild a full <c>.igc</c> (version + size wrapper + body) from a text folder.</summary>
    public static byte[] BuildIgc(string textDir)
    {
        byte[] body = ImportBody(textDir, out int? version);
        int ver = version ?? unchecked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var ms = new MemoryStream();
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, ver);
        ms.Write(buf);
        BinaryPrimitives.WriteInt32LittleEndian(buf, body.Length);
        ms.Write(buf);
        ms.Write(body);
        return ms.ToArray();
    }

    private static (int? Version, List<MetaEntry> Entries) ReadMeta(string path)
    {
        int? version = null;
        var entries = new List<MetaEntry>();
        if (!File.Exists(path)) return (null, entries);
        foreach (string raw in File.ReadAllLines(path, Encoding.Latin1))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] parts = line.Split('\t');
            switch (parts[0])
            {
                case "core2txt":
                    break;
                case "version":
                    if (parts.Length > 1 && int.TryParse(parts[1], NumberStyles.AllowLeadingSign,
                            CultureInfo.InvariantCulture, out var v)) version = v;
                    break;
                case "rec":
                    if (parts.Length < 3) continue;
                    if (!short.TryParse(parts[1], NumberStyles.AllowLeadingSign,
                            CultureInfo.InvariantCulture, out var type)) continue;
                    if (!int.TryParse(parts[2], NumberStyles.AllowLeadingSign,
                            CultureInfo.InvariantCulture, out var id)) continue;
                    var residue = new List<(int, byte)>();
                    if (parts.Length > 3 && parts[3] != "-")
                    {
                        foreach (string item in parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            int colon = item.IndexOf(':');
                            if (colon <= 0) continue;
                            if (!int.TryParse(item[..colon], NumberStyles.None,
                                    CultureInfo.InvariantCulture, out var off)) continue;
                            if (!byte.TryParse(item[(colon + 1)..], NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture, out var value)) continue;
                            residue.Add((off, value));
                        }
                    }
                    entries.Add(new MetaEntry((IgcObjectType)type, id, residue));
                    break;
            }
        }
        return (version, entries);
    }

    /// <summary>Scratch directory for a byte-identity self-test: never in /tmp
    /// (the lane rule) — the repo's gitignored <c>.scratch/</c> when it can be
    /// found, else the tool's base directory.</summary>
    public static string ScratchRoot()
    {
        string? env = Environment.GetEnvironmentVariable("ALLEGIANCE_SCRATCH_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            Directory.CreateDirectory(env);
            return env;
        }
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))
                    || File.Exists(Path.Combine(dir.FullName, "local.env")))
                {
                    string p = Path.Combine(dir.FullName, ".scratch");
                    Directory.CreateDirectory(p);
                    return p;
                }
                dir = dir.Parent;
            }
        }
        string fallback = Path.Combine(AppContext.BaseDirectory, ".scratch");
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
