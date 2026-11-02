using System.Buffers.Binary;
using System.Text;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// Reads the offline GLB authoring format plus optional collision/frame/light
/// sidecar. Geometry follows node hierarchy, including authored LOD parents.
/// Accepts editor-emitted accessor offsets/strides, triangle primitives and
/// node transforms; native output remains a binary MDL, not a runtime GLB.
/// </summary>
public static class GlbReader
{
    public static GlbExportModel Read(Stream glb, Stream? sidecar, Action<string>? warning = null)
    {
        var (json, bin) = ParseChunks(glb);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var (meshes, levels) = ReadGeometry(root, bin);
        var (mounts, lights) = ReadNodes(root, bin);

        // P824 — base-colour images embedded by the exporter (one PNG per
        // texture stem, top-left/GPU order). A mesh's stem comes from its
        // material name (the exporter names the material after the texture
        // stem), falling back to the writer's "<stem>_<texstem>_<i>" mesh-name
        // convention; a mesh whose material has no image keeps the parsed name
        // stem but no image is attached (import then fails loudly unless
        // --no-texture).
        var images = ReadImages(root, bin);

        // The sidecar is a full SidecarDocument; the reader takes the
        // embedded "collision" record, (P823) the raw mdl FrameData and the
        // full light records (GLB light nodes carry position only — the RGBA
        // colour and blink timing ride the sidecar, as in P812).
        CollisionSidecar? collision = null;
        List<MdlFrameRecord>? mdlFrames = null;
        List<GlbLight>? sidecarLights = null;
        int oldLevelCount = 0;
        if (sidecar is not null)
        {
            using var sdoc = JsonDocument.Parse(sidecar);
            if (sdoc.RootElement.TryGetProperty("lodPixelRadii", out var oldRadii))
                oldLevelCount = oldRadii.GetArrayLength();
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

        if (levels.Count == 0 && oldLevelCount > 0)
            (warning ?? Console.Error.WriteLine)($"warning: model '{RootName(root)}' lost {oldLevelCount} LOD levels: sidecar lists radii but GLB has no level geometry (older export or deleted levels)");

        return new GlbExportModel
        {
            Name = RootName(root),
            Meshes = meshes,
            Mounts = mounts,
            Lights = sidecarLights ?? lights,
            LodLevels = levels,
            LodPixelRadii = levels.Select(l => l.PixelRadius).ToArray(),
            TextureStems = meshes.Concat(levels.SelectMany(l => l.Meshes)).Select(m => m.TextureStem).OfType<string>().Distinct().ToArray(),
            Collision = collision,
            SourceMdlFrames = mdlFrames,
            Images = images.Count > 0 ? images : null,
        };
    }

    /// <summary>P824 — parse images/samplers/textures/materials and return one
    /// <see cref="GlbImage"/> per referenced base-colour image, plus fix each
    /// mesh's texture stem from its material when possible.</summary>
    private static List<GlbImage> ReadImages(JsonElement root, byte[] bin)
    {
        var result = new List<GlbImage>();
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("textures", out var textures) || textures.ValueKind != JsonValueKind.Array)
            return result;   // untextured GLB (the writer omits empty image/texture lists)
        var bufferViews = root.TryGetProperty("bufferViews", out var bv) ? bv : default;

        // image index → (stem, png bytes)
        var imageByIndex = new Dictionary<int, (string stem, byte[] png)>();
        int i = 0;
        foreach (var img in images.EnumerateArray())
        {
            string stem = img.TryGetProperty("name", out var n) ? n.GetString() ?? $"image{i}" : $"image{i}";
            byte[] png = [];
            if (img.TryGetProperty("bufferView", out var bvi) && bufferViews.ValueKind == JsonValueKind.Array)
            {
                var view = bufferViews[bvi.GetInt32()];
                int off = view.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0;
                int len = view.GetProperty("byteLength").GetInt32();
                png = bin.AsSpan(off, len).ToArray();
            }
            imageByIndex[i++] = (stem, png);
        }

        // The exporter writes one image per texture stem and names the image
        // (and its material) after the stem, so the stem → image mapping needs
        // no material/texture indirection here; ReadGeometry resolves each mesh's
        // stem from its material name (or the mesh-name convention) and the
        // import step looks the image up by stem.
        foreach (var (_, (stem, png)) in imageByIndex)
        {
            if (png.Length == 0) continue;
            var decoded = PngCodec.Decode(png);      // validates + carries the dimensions
            result.Add(new GlbImage(stem, decoded.Width, decoded.Height, png));
        }
        return result;
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
        // Blender may reorder scenes and nodes. Prefer the common parent of
        // geometry, then any scene root (including editors with no default scene).
        var nodes = root.GetProperty("nodes");
        foreach (var node in nodes.EnumerateArray())
            if (node.TryGetProperty("children", out _) && !TryLod(node, out _, out _))
                return node.TryGetProperty("name", out var n) ? n.GetString() ?? "model" : "model";
        return "model";
    }

    private static bool TryLod(JsonElement node, out int index, out float radius)
    {
        index = -1; radius = 0;
        if (node.TryGetProperty("extras", out var e)
            && e.TryGetProperty("allegianceLodIndex", out var i)
            && e.TryGetProperty("allegiancePixelRadius", out var r))
        {
            index = i.GetInt32(); radius = r.GetSingle();
            return true;
        }
        if (!node.TryGetProperty("name", out var n)) return false;
        var name = n.GetString() ?? "";
        if (!name.StartsWith("LOD_", StringComparison.Ordinal)) return false;
        var parts = name.Split('_');
        if (parts.Length != 4 || parts[2] != "px"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out index)
            || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out radius))
            throw new InvalidDataException($"malformed LOD parent '{name}'; expected LOD_<index>_px_<radius>");
        return true;
    }

    private static (List<GlbMesh> meshes, List<GlbLodLevel> levels) ReadGeometry(JsonElement root, byte[] bin)
    {
        var accessors = root.GetProperty("accessors");
        var views = root.GetProperty("bufferViews");
        var nodes = root.GetProperty("nodes");
        var meshDefs = root.GetProperty("meshes");
        var meshes = new List<GlbMesh>();
        var levels = new SortedDictionary<int, (float radius, List<GlbMesh> meshes)>();
        var children = nodes.EnumerateArray().Where(n => n.TryGetProperty("children", out _))
            .SelectMany(n => n.GetProperty("children").EnumerateArray().Select(c => c.GetInt32())).ToHashSet();
        var visited = new HashSet<int>();
        // Walk roots from all scenes, including the former non-default LOD scene.
        for (int i = 0; i < nodes.GetArrayLength(); i++)
            if (!children.Contains(i)) Walk(i, meshes, Matrix4x4.Identity);
        if (visited.Count != nodes.GetArrayLength())
            throw new InvalidDataException("GLB nodes contain a cycle or shared hierarchy");
        if (levels.Keys.Where((k, i) => k != i).Any())
            throw new InvalidDataException("LOD indices must be consecutive starting at zero");
        return (meshes, levels.Values.Select(l => new GlbLodLevel(l.radius, l.meshes)).ToList());

        void Walk(int ni, List<GlbMesh> sink, Matrix4x4 parent)
        {
            if (!visited.Add(ni)) throw new InvalidDataException("GLB nodes contain a cycle or shared hierarchy");
            var node = nodes[ni];
            var transform = NodeTransform(node) * parent;
            if (TryLod(node, out int li, out float radius))
            {
                if (li < 0 || !levels.TryAdd(li, (radius, new List<GlbMesh>())))
                    throw new InvalidDataException($"duplicate or negative LOD index {li}");
                sink = levels[li].meshes;
            }
            if (node.TryGetProperty("mesh", out var m))
            {
                var mesh = meshDefs[m.GetInt32()];
                string name = node.TryGetProperty("name", out var nn) ? nn.GetString() ?? "mesh" : "mesh";
                foreach (var prim in mesh.GetProperty("primitives").EnumerateArray())
                {
                    if (prim.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4)
                        throw new InvalidDataException($"mesh '{name}' must use triangle primitives");
                    var attrs = prim.GetProperty("attributes");
                    var pos = ReadFloats(bin, accessors[attrs.GetProperty("POSITION").GetInt32()], views, 3);
                    var uv = ReadFloats(bin, accessors[attrs.GetProperty("TEXCOORD_0").GetInt32()], views, 2);
                    var nrm = ReadFloats(bin, accessors[attrs.GetProperty("NORMAL").GetInt32()], views, 3);
                    var idx = ReadIndices(bin, accessors[prim.GetProperty("indices").GetInt32()], views);
                    string? tex = null;
                    if (prim.TryGetProperty("material", out var mat))
                        tex = root.GetProperty("materials")[mat.GetInt32()].GetProperty("name").GetString();
                    if (tex is null)
                    {
                        var parts = name.Split('_');
                        if (parts.Length >= 3 && parts[^2].EndsWith("bmp", StringComparison.Ordinal)) tex = parts[^2];
                    }
                    if (transform != Matrix4x4.Identity)
                    {
                        if (!Matrix4x4.Invert(transform, out var inverse))
                            throw new InvalidDataException($"mesh '{name}' has a singular transform");
                        var normalMatrix = Matrix4x4.Transpose(inverse);
                        for (int v = 0; v < pos.Length; v += 3)
                        {
                            var p = Vector3.Transform(new Vector3(pos[v], pos[v+1], pos[v+2]), transform);
                            var n = Vector3.Normalize(Vector3.TransformNormal(new Vector3(nrm[v], nrm[v+1], nrm[v+2]), normalMatrix));
                            pos[v] = p.X; pos[v+1] = p.Y; pos[v+2] = p.Z;
                            nrm[v] = n.X; nrm[v+1] = n.Y; nrm[v+2] = n.Z;
                        }
                        if (transform.GetDeterminant() < 0)
                            for (int v = 0; v < idx.Length; v += 3) (idx[v+1], idx[v+2]) = (idx[v+2], idx[v+1]);
                    }
                    sink.Add(new GlbMesh(name, tex, pos, uv, nrm, idx));
                }
            }
            if (node.TryGetProperty("children", out var cs))
                foreach (var child in cs.EnumerateArray()) Walk(child.GetInt32(), sink, transform);
        }
    }

    private static Matrix4x4 NodeTransform(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var mat))
        {
            var f = mat.EnumerateArray().Select(x => x.GetSingle()).ToArray();
            return new Matrix4x4(f[0],f[1],f[2],f[3],f[4],f[5],f[6],f[7],f[8],f[9],f[10],f[11],f[12],f[13],f[14],f[15]);
        }
        var scale = node.TryGetProperty("scale", out _) ? ReadFloat3(node, "scale") : [1f,1f,1f];
        var q = node.TryGetProperty("rotation", out var r) ? ReadFloat4(r) : [0f,0f,0f,1f];
        var t = node.TryGetProperty("translation", out _) ? ReadFloat3(node, "translation") : [0f,0f,0f];
        return Matrix4x4.CreateScale(scale[0],scale[1],scale[2]) * Matrix4x4.CreateFromQuaternion(new Quaternion(q[0],q[1],q[2],q[3]))
            * Matrix4x4.CreateTranslation(t[0],t[1],t[2]);
    }

    private static (List<MountPoint> mounts, List<GlbLight> lights) ReadNodes(JsonElement root, byte[] bin)
    {
        var nodes = root.GetProperty("nodes");
        var mounts = new List<MountPoint>();
        var lights = new List<GlbLight>();

        foreach (var node in nodes.EnumerateArray())
        {
            string name = node.TryGetProperty("name", out var nn) ? nn.GetString() ?? "" : "";
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

    private static (int offset, int stride) AccessorLayout(JsonElement accessor, JsonElement views, int elementSize)
    {
        if (accessor.TryGetProperty("sparse", out _)) throw new InvalidDataException("sparse GLB accessors are unsupported; export without sparse accessors");
        var view = views[accessor.GetProperty("bufferView").GetInt32()];
        return ((view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
            + (accessor.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0),
            view.TryGetProperty("byteStride", out var stride) ? stride.GetInt32() : elementSize);
    }

    private static float[] ReadFloats(byte[] bin, JsonElement accessor, JsonElement views, int components)
    {
        if (accessor.GetProperty("componentType").GetInt32() != 5126
            || accessor.GetProperty("type").GetString() != $"VEC{components}")
            throw new InvalidDataException("geometry attributes must be float32 vectors");
        var (offset, stride) = AccessorLayout(accessor, views, components * 4);
        int count = accessor.GetProperty("count").GetInt32();
        var result = new float[count * components];
        for (int i = 0; i < count; i++)
            for (int c = 0; c < components; c++)
                result[i * components + c] = BinaryPrimitives.ReadSingleLittleEndian(bin.AsSpan(offset + i * stride + c * 4));
        return result;
    }

    private static ushort[] ReadIndices(byte[] bin, JsonElement accessor, JsonElement views)
    {
        int type = accessor.GetProperty("componentType").GetInt32();
        int size = type switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => throw new InvalidDataException("unsupported index component type") };
        var (offset, stride) = AccessorLayout(accessor, views, size);
        var result = new ushort[accessor.GetProperty("count").GetInt32()];
        for (int i = 0; i < result.Length; i++)
        {
            var bytes = bin.AsSpan(offset + i * stride);
            uint index = size == 1 ? bytes[0] : size == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (index > ushort.MaxValue) throw new InvalidDataException("mesh index exceeds native 16-bit limit; split the mesh before import");
            result[i] = (ushort)index;
        }
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
