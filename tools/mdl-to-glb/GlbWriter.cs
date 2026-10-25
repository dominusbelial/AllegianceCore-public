using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// Minimal glTF 2.0 GLB writer (no external dependencies). Geometry is written
/// <b>raw</b> — native winding (DirectX clockwise-front) and native coordinates
/// — so the export is a lossless container; any glTF consumer that wants the
/// usual CCW-front y-up convention applies the documented flip itself
/// (P812 report §coordinate conventions).
/// </summary>
public static class GlbWriter
{
    private const uint Magic = 0x46546C67;   // 'glTF'
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;

    public static void Write(GlbExportModel model, Stream output)
    {
        using var ms = new MemoryStream();
        var bin = WriteBin(model, out var views, out var accessors, out var meshesJson, out var minMax,
            out var imagesJson, out var texturesJson, out var materialsJson);
        var json = BuildJson(model, meshesJson, views, accessors, minMax, bin.Length, imagesJson, texturesJson, materialsJson);

        // GLB header + chunks, both 4-byte aligned.
        int jsonPad = (4 - json.Length % 4) % 4;
        int binPad = (4 - (int)bin.Length % 4) % 4;
        uint total = (uint)(12 + 8 + json.Length + jsonPad + 8 + bin.Length + binPad);

        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], total);
        output.Write(header);

        WriteChunk(output, JsonChunk, json.Length + jsonPad);
        output.Write(json);
        for (int i = 0; i < jsonPad; i++) output.WriteByte(0x20);

        WriteChunk(output, BinChunk, (int)bin.Length + binPad);
        output.Write(bin);
        for (int i = 0; i < binPad; i++) output.WriteByte(0);
    }

    private static void WriteChunk(Stream s, uint type, int length)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(b[4..], type);
        s.Write(b);
    }

    // -- binary chunk -------------------------------------------------------

    private static byte[] WriteBin(GlbExportModel model, out List<object> views, out List<object> accessors,
        out List<object> meshesJson, out List<(float[] min, float[] max)> minMax,
        out List<object> imagesJson, out List<object> texturesJson, out List<object> materialsJson)
    {
        var viewsLocal = new List<object>();
        var accessorsLocal = new List<object>();
        var meshesLocal = new List<object>();
        var minMaxLocal = new List<(float[] min, float[] max)>();
        // P824 — one image/texture/material per texture stem; textured mesh
        // primitives reference their stem's material (baseColourTexture).
        var imagesLocal = new List<object>();
        var texturesLocal = new List<object>();
        var materialsLocal = new List<object>();
        var materialIndexByStem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        views = viewsLocal;
        accessors = accessorsLocal;
        meshesJson = meshesLocal;
        minMax = minMaxLocal;
        imagesJson = imagesLocal;
        texturesJson = texturesLocal;
        materialsJson = materialsLocal;
        using var ms = new MemoryStream();

        int AddView(int length)
        {
            var offset = (int)ms.Length;
            if (offset % 4 != 0)
            {
                ms.Write(new byte[4 - offset % 4]);
                offset = (int)ms.Length;
            }
            viewsLocal.Add(new { buffer = 0, byteOffset = offset, byteLength = length });
            return viewsLocal.Count - 1;
        }

        // -- images/textures/materials (P824): one per embedded texture stem --
        foreach (var image in model.Images ?? [])
        {
            int imgView = AddView(image.Png.Length);
            WriteBytes(ms, image.Png);
            imagesLocal.Add(new { bufferView = imgView, mimeType = "image/png", name = image.Stem });
            texturesLocal.Add(new { sampler = 0, source = imagesLocal.Count - 1 });
            materialsLocal.Add(new
            {
                name = image.Stem,
                pbrMetallicRoughness = new
                {
                    baseColorTexture = new { index = texturesLocal.Count - 1 },
                    metallicFactor = 0.0,
                    roughnessFactor = 1.0,
                },
                doubleSided = true,
            });
            materialIndexByStem[image.Stem] = materialsLocal.Count - 1;
        }

        foreach (var mesh in model.Meshes)
        {
            // positions
            int posView = AddView(mesh.Positions.Length * sizeof(float));
            WriteFloats(ms, mesh.Positions);
            var (min, max) = MinMax(mesh.Positions, 3);
            minMax.Add((min, max));
            int posAcc = accessorsLocal.Count;
            accessorsLocal.Add(new { bufferView = posView, componentType = 5126, count = mesh.Positions.Length / 3, type = "VEC3", min, max });

            // texcoords
            int uvView = AddView(mesh.TexCoords.Length * sizeof(float));
            WriteFloats(ms, mesh.TexCoords);
            int uvAcc = accessorsLocal.Count;
            accessorsLocal.Add(new { bufferView = uvView, componentType = 5126, count = mesh.TexCoords.Length / 2, type = "VEC2" });

            // normals
            int nrmView = AddView(mesh.Normals.Length * sizeof(float));
            WriteFloats(ms, mesh.Normals);
            int nrmAcc = accessorsLocal.Count;
            accessorsLocal.Add(new { bufferView = nrmView, componentType = 5126, count = mesh.Normals.Length / 3, type = "VEC3" });

            // indices (raw ushort, native order — never rewound here)
            int idxView = AddView(mesh.Indices.Length * sizeof(ushort));
            WriteUshorts(ms, mesh.Indices);
            int idxAcc = accessorsLocal.Count;
            accessorsLocal.Add(new { bufferView = idxView, componentType = 5123, count = mesh.Indices.Length, type = "SCALAR" });

            object primitive = mesh.TextureStem is { } texStem && materialIndexByStem.TryGetValue(texStem, out var mi)
                ? new
                {
                    attributes = new { POSITION = posAcc, TEXCOORD_0 = uvAcc, NORMAL = nrmAcc },
                    indices = idxAcc,
                    material = mi,      // P824 — the stem's baseColour material
                    mode = 4,
                }
                : new
                {
                    attributes = new { POSITION = posAcc, TEXCOORD_0 = uvAcc, NORMAL = nrmAcc },
                    indices = idxAcc,
                    mode = 4,
                };
            meshesLocal.Add(new
            {
                name = mesh.Name,
                primitives = new[] { primitive },
            });
        }

        return ms.ToArray();
    }

    private static void WriteFloats(MemoryStream ms, float[] data)
    {
        var bytes = new byte[data.Length * sizeof(float)];
        for (int i = 0; i < data.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), data[i]);
        ms.Write(bytes);
    }

    private static void WriteBytes(MemoryStream ms, byte[] data) => ms.Write(data);

    private static void WriteUshorts(MemoryStream ms, ushort[] data)
    {
        var bytes = new byte[data.Length * sizeof(ushort)];
        for (int i = 0; i < data.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), data[i]);
        ms.Write(bytes);
    }

    private static (float[] min, float[] max) MinMax(float[] pos, int stride)
    {
        var min = new float[stride];
        var max = new float[stride];
        for (int c = 0; c < stride; c++)
        {
            min[c] = float.PositiveInfinity;
            max[c] = float.NegativeInfinity;
        }
        for (int i = 0; i + stride <= pos.Length; i += stride)
            for (int c = 0; c < stride; c++)
            {
                min[c] = MathF.Min(min[c], pos[i + c]);
                max[c] = MathF.Max(max[c], pos[i + c]);
            }
        return (min, max);
    }

    // -- JSON chunk ----------------------------------------------------------

    private static byte[] BuildJson(GlbExportModel model, List<object> meshesJson, List<object> views,
        List<object> accessors, List<(float[] min, float[] max)> minMax, int binLength,
        List<object> imagesJson, List<object> texturesJson, List<object> materialsJson)
    {
        // Root first, so every child index is stable (children start at 1).
        var nodes = new List<object>();
        var children = new List<int>();
        nodes.Add(new { name = model.Name, children });

        // mesh nodes
        for (int i = 0; i < model.Meshes.Count; i++)
        {
            children.Add(nodes.Count);
            nodes.Add(new { name = model.Meshes[i].Name, mesh = i });
        }

        // mount empty nodes: HP_<Kind>_<Index>, local +Z aimed along forward
        foreach (var mount in model.Mounts)
        {
            children.Add(nodes.Count);
            nodes.Add(new
            {
                name = mount.NodeName,
                translation = mount.Position,
                rotation = QuaternionFromZ(mount.Forward),
            });
        }

        // light placeholder nodes (sidecar carries the blink timing)
        foreach (var light in model.Lights)
        {
            children.Add(nodes.Count);
            nodes.Add(new { name = light.Name, translation = light.Position });
        }

        var doc = new
        {
            asset = new
            {
                version = "2.0",
                generator = $"AllegianceCore mdl-to-glb (P812); source {model.Name}.mdl + {model.Name}.cvh",
            },
            scene = 0,
            scenes = new object[] { new { nodes = new[] { 0 } } },
            nodes,
            meshes = meshesJson,
            accessors,
            bufferViews = views,
            buffers = new object[] { new { byteLength = binLength } },
            // P824 — base-colour images + samplers/textures/materials when
            // textures were embedded (empty lists omitted from the JSON).
            images = imagesJson.Count > 0 ? imagesJson : null,
            samplers = imagesJson.Count > 0
                ? new object[] { new { magFilter = 9729, minFilter = 9729, wrapS = 10497, wrapT = 10497 } }
                : null,
            textures = texturesJson.Count > 0 ? texturesJson : null,
            materials = materialsJson.Count > 0 ? materialsJson : null,
        };

        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var json = JsonSerializer.Serialize(doc, options);
        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>Unit quaternion carrying local +Z onto <paramref name="forward"/>
    /// (the wivuu convention: a hardpoint's local +Z is its muzzle/nozzle axis).</summary>
    public static float[] QuaternionFromZ(float[] forward)
    {
        float fx = forward[0], fy = forward[1], fz = forward[2];
        float len = MathF.Sqrt(fx * fx + fy * fy + fz * fz);
        if (len < 1e-8f)
            return [0f, 0f, 0f, 1f];                       // degenerate: keep identity
        fx /= len; fy /= len; fz /= len;

        // q = (cross(z, f), 1 + dot(z, f)) normalized; identity when parallel.
        float dot = fz;
        if (dot > 0.999999f)
            return [0f, 0f, 0f, 1f];
        if (dot < -0.999999f)
            return [1f, 0f, 0f, 0f];                       // 180° about X
        float cx = -fy, cy = fx, cz = 0f;                  // cross((0,0,1), f)
        float w = 1f + dot;
        float n = MathF.Sqrt(cx * cx + cy * cy + cz * cz + w * w);
        return [cx / n, cy / n, cz / n, w / n];
    }
}
