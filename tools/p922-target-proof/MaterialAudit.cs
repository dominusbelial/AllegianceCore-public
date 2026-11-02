using System.Buffers.Binary;
using System.Text.Json;
using Allegiance.Core;

// Distinguishes imported image rows from a mesh's inherited file-level binding;
// those are separate properties, not interchangeable meanings of "file row".
static class MaterialAudit
{
    public static void Run(string output)
    {
        string artwork = ExternalPaths.Require(ExternalResourceKind.Artwork);
        string pack = ExternalPaths.Require(ExternalResourceKind.HdPack);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(pack, "manifest.json")));
        var imported = new HashSet<string>();
        foreach (var entry in manifest.RootElement.GetProperty("textures").EnumerateObject())
        {
            var data = File.ReadAllBytes(Path.Combine(artwork, entry.Name + ".mdl"));
            if (BinaryPrimitives.ReadUInt32LittleEndian(data) != MdlTextureDecoder.MdlMagic) imported.Add(entry.Name);
        }
        var rows = new List<object>();
        foreach (var model in manifest.RootElement.GetProperty("coverage").GetProperty("models").EnumerateArray())
        {
            string name = model.GetProperty("model").GetString()!;
            string kind = model.GetProperty("kind").GetString()!;
            var stems = model.GetProperty("stems").EnumerateArray().Select(s => s.GetString()!).Where(imported.Contains).ToArray();
            if (stems.Length == 0) continue;
            var mdl = MdlFile.Parse(File.ReadAllBytes(Path.Combine(artwork, name + ".mdl")));
            rows.Add(new { kind, model = name, importedAtlases = stems, parts = mdl.Meshes.Count,
                inheritedParts = mdl.Meshes.Count(m => m.TextureStem is null),
                inheritedTriangles = mdl.Meshes.Where(m => m.TextureStem is null).Sum(m => m.TriangleCount),
                totalTriangles = mdl.Meshes.Sum(m => m.TriangleCount),
                ownStems = mdl.Meshes.Where(m => m.TextureStem is not null).Select(m => m.TextureStem).Distinct().ToArray() });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { importedAtlasCount = imported.Count, rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"P922_AUDIT importedAtlases={imported.Count} modelRows={rows.Count}");
    }
}
