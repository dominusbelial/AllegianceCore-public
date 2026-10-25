using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// P812 — the reverse loader STUB: reads a tool-exported .glb (plus the
/// collision sidecar when present) back into <see cref="GlbExportModel"/>
/// shaped data — the shape a future runtime loader would fold into the port's
/// <see cref="Allegiance.Core.Data"/> / <see cref="Allegiance.Core.Collision"/>
/// model structures. Deliberately minimal: it understands exactly what
/// <see cref="GlbWriter"/> emits (one root node, mesh children, HP_* empty
/// nodes with translation+rotation, Light_* nodes).
/// </summary>
public static class GlbReader
{
    public static GlbExportModel Read(Stream glb, Stream? sidecar)
    {
        var (json, bin) = ParseChunks(glb);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var meshes = ReadMeshes(root, bin);
        var (mounts, lights) = ReadNodes(root, bin);

        // The sidecar is a full SidecarDocument; the reader stub takes the
        // embedded "collision" record, (P823) the raw mdl FrameData and the
        // full light records (GLB light nodes carry position only — the RGBA
        // colour and blink timing ride the sidecar, as in P812).
        CollisionSidecar? collision = null;
        List<MdlFrameRecord>? mdlFrames = null;
        List<GlbLight>? sidecarLights = null;
        if (sidecar is not null)
        {
            using var sdoc = JsonDocument.Parse(sidecar);
            if (sdoc.RootElement.TryGetProperty("collision", out var c)
                && c.ValueKind == JsonValueKind.Object)
                collision = c.Deserialize<CollisionSidecar>(SidecarJson.Options);
            if (sdoc.RootElement.TryGetProperty("mdlFrames", out var mf)
                && mf.ValueKind == JsonValueKind.Array)
                mdlFrames = mf.Deserialize<List<MdlFrameRecord>>(SidecarJson.Options);
            if (sdoc.RootElement.TryGetProperty("lights", out var li)
                && li.ValueKind == JsonValueKind.Array)
                sidecarLights = li.Deserialize<List<GlbLight>>(SidecarJson.Options);
        }

        return new GlbExportModel
        {
            Name = RootName(root),
            Meshes = meshes,
            Mounts = mounts,
            Lights = sidecarLights ?? lights,
            LodPixelRadii = [],                     // LOD thresholds live only in the sidecar/mdl
            TextureStems = meshes.Select(m => m.TextureStem).OfType<string>().Distinct().ToArray(),
            Collision = collision,
            SourceMdlFrames = mdlFrames,
        };
    }

    private static (byte[] json, byte[] bin) ParseChunks(Stream glb)
    {
        using var ms = new MemoryStream();
        glb.CopyTo(ms);
        var data = ms.ToArray();
        if (data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x46546C67)
            throw new InvalidDataException("not a GLB (bad magic)");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)) != 2)
            throw new InvalidDataException("unsupported glTF version (only 2.0)");
        int pos = 12;
        byte[]? json = null, bin = null;
        while (pos + 8 <= data.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            if (len < 0 || pos + 8 + len > data.Length)
                throw new InvalidDataException(
                    $"truncated GLB: chunk at 0x{pos:X} claims {len} bytes past the end of the file");
            var chunk = data[(pos + 8)..(pos + 8 + len)];
            if (type == 0x4E4F534A) json = chunk;
            else if (type == 0x004E4942) bin = chunk;
            pos += 8 + len;
        }
        if (json is null)
            throw new InvalidDataException("GLB has no JSON chunk");
        return (json, bin ?? []);
    }

    private static string RootName(JsonElement root)
    {
        var scene = root.GetProperty("scenes")[root.GetProperty("scene").GetInt32()];
        int rootNode = scene.GetProperty("nodes")[0].GetInt32();
        return root.GetProperty("nodes")[rootNode].GetProperty("name").GetString() ?? "model";
    }

    private static List<GlbMesh> ReadMeshes(JsonElement root, byte[] bin)
    {
        var accessors = root.GetProperty("accessors");
        var bufferViews = root.GetProperty("bufferViews");

        var meshes = new List<GlbMesh>();
        foreach (var mesh in root.GetProperty("meshes").EnumerateArray())
        {
            var prim = mesh.GetProperty("primitives")[0];
            var pos = ReadAccessorVec3(bin, accessors[prim.GetProperty("attributes").GetProperty("POSITION").GetInt32()], bufferViews);
            var uv = ReadAccessorVec2(bin, accessors[prim.GetProperty("attributes").GetProperty("TEXCOORD_0").GetInt32()], bufferViews);
            var nrm = ReadAccessorVec3(bin, accessors[prim.GetProperty("attributes").GetProperty("NORMAL").GetInt32()], bufferViews);
            var idx = ReadAccessorU16(bin, accessors[prim.GetProperty("indices").GetInt32()], bufferViews);
            string name = mesh.GetProperty("name").GetString() ?? "mesh";
            // The writer names textured meshes "<stem>_<texstem>_<i>"; keep the
            // texture stem out of the mesh name when parsing back.
            string? tex = null;
            var parts = name.Split('_');
            if (parts.Length >= 3 && parts[^2].EndsWith("bmp", StringComparison.Ordinal))
                tex = parts[^2];
            meshes.Add(new GlbMesh(name, tex, pos, uv, nrm, idx));
        }
        return meshes;
    }

    private static (List<MountPoint> mounts, List<GlbLight> lights) ReadNodes(JsonElement root, byte[] bin)
    {
        var nodes = root.GetProperty("nodes");
        var mounts = new List<MountPoint>();
        var lights = new List<GlbLight>();

        foreach (var node in nodes.EnumerateArray())
        {
            string name = node.GetProperty("name").GetString() ?? "";
            if (name.StartsWith("HP_", StringComparison.Ordinal))
            {
                var translation = ReadFloat3(node, "translation");
                var rotation = node.TryGetProperty("rotation", out var r)
                    ? ReadFloat4(r) : [0f, 0f, 0f, 1f];
                var forward = RotateZ(rotation);
                var (kind, index) = SplitNodeName(name);
                mounts.Add(new MountPoint
                {
                    NodeName = name,
                    Kind = kind,
                    Index = index,
                    NativeName = name,                  // stub: native name is not re-derivable from the GLB alone
                    Sources = ["glb"],
                    Position = translation,
                    Forward = forward,
                });
            }
            else if (name.StartsWith("Light_", StringComparison.Ordinal))
            {
                // Light nodes carry position only; blink params live in the sidecar.
                lights.Add(new GlbLight(name, [1f, 1f, 1f, 1f], ReadFloat3(node, "translation"), 0, 0, 0, 0, 0));
            }
        }
        return (mounts, lights);
    }

    internal static (string kind, int index) SplitNodeName(string nodeName)
    {
        // HP_<Kind>_<Index>
        var body = nodeName["HP_".Length..];
        int cut = body.LastIndexOf('_');
        if (cut <= 0 || !int.TryParse(body[(cut + 1)..], out int index) || index < 0)
            throw new InvalidDataException(
                $"malformed hardpoint node name '{nodeName}' (expected HP_<Kind>_<Index> with a non-negative integer index)");
        return (body[..cut], index);
    }

    private static float[] RotateZ(float[] q)
    {
        // rotate (0,0,1) by quaternion q=(x,y,z,w): v' = v + 2w(u×v) + 2u×(u×v)
        float x = q[0], y = q[1], w = q[3];
        return [2 * w * y, -2 * w * x, 1 - 2 * (x * x + y * y)];
    }

    private static float[] ReadFloat3(JsonElement node, string prop)
    {
        var a = node.GetProperty(prop);
        return [a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle()];
    }

    private static float[] ReadFloat4(JsonElement a) =>
        [a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle(), a[3].GetSingle()];

    private static (int view, int offset, int stride) ViewOf(JsonElement accessor, JsonElement bufferViews)
    {
        var v = bufferViews[accessor.GetProperty("bufferView").GetInt32()];
        return (v.GetProperty("byteOffset").GetInt32(), 0, v.GetProperty("byteLength").GetInt32());
    }

    private static float[] ReadAccessorVec3(byte[] bin, JsonElement accessor, JsonElement bufferViews)
    {
        var (off, _, _) = ViewOf(accessor, bufferViews);
        int count = accessor.GetProperty("count").GetInt32();
        var result = new float[count * 3];
        for (int i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(off + i * 4));
        return result;
    }

    private static float[] ReadAccessorVec2(byte[] bin, JsonElement accessor, JsonElement bufferViews)
    {
        var (off, _, _) = ViewOf(accessor, bufferViews);
        int count = accessor.GetProperty("count").GetInt32();
        var result = new float[count * 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(off + i * 4));
        return result;
    }

    private static ushort[] ReadAccessorU16(byte[] bin, JsonElement accessor, JsonElement bufferViews)
    {
        var (off, _, _) = ViewOf(accessor, bufferViews);
        int count = accessor.GetProperty("count").GetInt32();
        var result = new ushort[count];
        for (int i = 0; i < count; i++)
            result[i] = BinaryPrimitives.ReadUInt16LittleEndian(bin.AsSpan(off + i * 2));
        return result;
    }
}

internal static class SidecarJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
