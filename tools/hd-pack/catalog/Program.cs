using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;

// JSON is written explicitly so this tool also works with reflection disabled.
if (args.Length != 3) throw new ArgumentException("catalog <core.igc> <artwork> <output.json>");
var core = StaticCore.Load(IgcReader.ParseFile(args[0]));
var disk = Directory.EnumerateFiles(args[1], "*", SearchOption.TopDirectoryOnly)
    .Where(p => Path.GetExtension(p).Equals(".mdl", StringComparison.OrdinalIgnoreCase))
    .ToDictionary(p => Path.GetFileNameWithoutExtension(p)!, p => p, StringComparer.OrdinalIgnoreCase);
var rows = core.HullTypes.Values.Select(v => (kind: "ship", model: v.ModelName, texture: v.TextureName))
    .Concat(core.StationTypes.Values.Select(v => ("station", v.ModelName, v.TextureName)))
    .Concat(core.MissileTypes.Values.Select(v => ("missile", v.ModelName, v.TextureName)))
    .Concat(core.ProbeTypes.Values.Select(v => ("probe", v.ModelName, v.TextureName)))
    .Concat(core.ProjectileTypes.Values.Select(v => ("projectile", v.ModelName, v.TextureName)))
    .Distinct().OrderBy(v => v.Item1).ThenBy(v => v.Item2).ToArray();
using var stream = File.Create(args[2]);
using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
json.WriteStartObject(); json.WriteString("coreSha256", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(args[0]))));
json.WriteStartArray("models");
foreach (var row in rows)
{
    json.WriteStartObject(); json.WriteString("kind", row.Item1); json.WriteString("model", row.Item2);
    // Authoring policy: hull length >= 75 or native capital threat/carrier.
    // Native fields/rules: src/Igc/igc.h:1780,5383-5394,736.
    json.WriteBoolean("capital", row.Item1 == "ship" && core.HullTypes.Values.Any(h =>
        h.ModelName == row.Item2 && (h.Length >= 75 ||
            (h.HabmCapabilities & 0x80) == 0 && (h.HabmCapabilities & 0x609) != 0)));
    json.WriteStartArray("stems");
    var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (!string.IsNullOrWhiteSpace(row.Item3)) stems.Add(row.Item3);
    if (disk.TryGetValue(row.Item2, out var path))
    {
        var model = MdlFile.Parse(File.ReadAllBytes(path));
        foreach (var mesh in model.Meshes.Concat(model.LodLevels.SelectMany(l => l.Meshes)))
            if (mesh.TextureStem is { } stem) stems.Add(stem);
    }
    foreach (string stem in stems.Order(StringComparer.OrdinalIgnoreCase)) json.WriteStringValue(stem.ToLowerInvariant());
    json.WriteEndArray(); json.WriteEndObject();
}
json.WriteEndArray(); json.WriteEndObject();
