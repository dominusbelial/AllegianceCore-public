using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// P823 — writes a binary <c>.mdl</c> in native's token-stream format
/// (<c>engine/mdlbin.cpp</c> <c>MDLBinaryNameSpace::ReadFile</c>: magic,
/// version, namespace/import/export counts, then the object table; objects are
/// a postfix token stream — float/string/list/apply/binary/reference/import/
/// pair, each object terminated by <c>ObjectEnd</c> with exactly one stack
/// entry) that the port's own <see cref="Allegiance.Core.MdlFile"/> parser
/// (<c>src/Allegiance.Core/Data/MdlFile.cs</c>) reads and the client renderer
/// draws.
///
/// <para><b>Emitted shape</b> (canonical; mirrors the walk of the native files'
/// "object" export):</para>
/// <list type="bullet">
/// <item>namespaces <c>model</c>, <c>effect</c>, then one namespace per texture
/// stem (each stem imports itself, <c>&lt;stem&gt;</c>/<c>&lt;stem&gt;</c> — the
/// convention every native hull file follows, e.g. <c>fig30bmp</c>).</item>
/// <item>imports in the fixed native order <c>ModifiableNumber, LightsGeo, time,
/// FrameData, LODGeo, MeshGeo, TextureGeo, &lt;stems…&gt;, GroupGeo</c>
/// (fig30/bom04 use exactly this order; ss27 differs — see the report).</item>
/// <item>exports <c>frame, lights, frames, object</c>: <c>frame</c> =
/// <c>Apply(ModifiableNumber, 1.0)</c>, <c>lights</c> = LightsGeo binary,
/// <c>frames</c> = FrameData binary, <c>object</c> =
/// <c>GroupGeo([mesh…])</c> with textured meshes wrapped
/// <c>TextureGeo(&lt;stem&gt;, MeshGeo)</c>.</item>
/// </list>
///
/// <para><b>LOD:</b> the GLB does not carry per-level geometry (the P812 export
/// ships the base meshes + the sidecar's baked pixel radii only), so the writer
/// emits the single detail level — the brief's "else one level" fallback. The
/// report records this as the known, explained delta of the native round trip.</para>
/// </summary>
public static class MdlWriter
{
    internal const uint MdlMagic = 0x0DEBADF00u;   // MdlFileParserSupport.MdlMagic
    internal const uint MdlVersion = 0x00010000u;

    // Object tokens (engine/mdlbin.cpp Object* enum / MdlParser).
    private const uint ObjectEnd = 0;
    private const uint ObjectFloat = 1;
    private const uint ObjectList = 5;
    private const uint ObjectApply = 6;
    private const uint ObjectBinary = 7;
    private const uint ObjectImport = 9;

    /// <summary>The .mdl's "frame" export value. Native files carry a
    /// per-model number here (fig30/ss27: 1.0, bom04: 60.0); the port's parser
    /// does not retain it and no port consumer reads it, so the writer emits
    /// 1.0 (the delta is byte-explained in the P823 report).</summary>
    internal const float FrameExportValue = 1.0f;

    public static byte[] Write(GlbExportModel model)
    {
        if (model.Meshes.Count == 0)
            throw new InvalidDataException("cannot write a .mdl with no meshes (a model file must carry geometry)");

        var w = new TokenWriter();

        // -- import table (order fixed; indices referenced by the token stream) --
        // 0 ModifiableNumber, 1 LightsGeo, 2 time, 3 FrameData, 4 LODGeo,
        // 5 MeshGeo, 6 TextureGeo, 7.. <stems>, 7+n GroupGeo.
        var stems = DistinctStems(model);
        int impModifiable = 0, impLights = 1, impTime = 2, impFrameData = 3,
            impLod = 4, impMesh = 5, impTexture = 6;
        var impStem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < stems.Length; i++)
            impStem[stems[i]] = 7 + i;
        int impGroup = 7 + stems.Length;

        // -- namespaces -----------------------------------------------------
        w.UInt(MdlMagic);
        w.UInt(MdlVersion);
        w.UInt(2 + (uint)stems.Length);                  // countNameSpaces
        w.UInt((uint)(7 + stems.Length + 1));            // countImports
        w.UInt(4);                                       // countExports
        w.UInt(0);                                       // countDefinitions
        w.CString("model");
        w.CString("effect");
        foreach (var s in stems)
            w.CString(s);
        w.Import(0, impModifiable, "ModifiableNumber");
        w.Import(1, impLights, "LightsGeo");
        w.Import(0, impTime, "time");
        w.Import(1, impFrameData, "FrameData");
        w.Import(0, impLod, "LODGeo");
        w.Import(0, impMesh, "MeshGeo");
        w.Import(0, impTexture, "TextureGeo");
        for (int i = 0; i < stems.Length; i++)
            w.Import(2 + i, impStem[stems[i]], stems[i]);
        w.Import(0, impGroup, "GroupGeo");
        foreach (var e in new[] { "frame", "lights", "frames", "object" })
            w.CString(e);

        // -- object table: export i ↔ object index i -------------------------
        // Native's token order is ARGS FIRST, then the function import, then
        // Apply (verified on fig30/bom04/ss27: the 'frame' export is
        // [Float][Import][Apply]; the 'object' export is [arg…][Import][Apply]).
        // idx 0: frame = Apply(ModifiableNumber, 1.0)
        w.UInt(0);
        w.UInt(ObjectFloat); w.Float(FrameExportValue);
        w.UInt(ObjectImport); w.UInt((uint)impModifiable);
        w.UInt(ObjectApply);
        w.UInt(ObjectEnd);

        // idx 1: lights = LightsGeo binary (count + 48 bytes per light:
        // RGBA colour, XYZ position, period, phase, rampUp, hold, rampDown —
        // effect/efapp.cpp:101-112 LightData).
        w.UInt(1);
        w.UInt(ObjectImport); w.UInt((uint)impLights);
        w.UInt(ObjectBinary);
        w.UInt((uint)model.Lights.Count);
        foreach (var l in model.Lights)
        {
            w.Float(l.Color[0]); w.Float(l.Color[1]); w.Float(l.Color[2]); w.Float(l.Color[3]);
            w.Float(l.Position[0]); w.Float(l.Position[1]); w.Float(l.Position[2]);
            w.Float(l.Period); w.Float(l.Phase); w.Float(l.RampUp); w.Float(l.Hold); w.Float(l.RampDown);
        }
        w.UInt(ObjectEnd);

        // idx 2: frames = FrameData binary (count, then per entry a padded
        // C-string name + position/forward/up as 9 raw floats —
        // effect/efapp.cpp FrameDataFactory::Read).
        var frames = ResolveFrames(model);
        w.UInt(2);
        w.UInt(ObjectImport); w.UInt((uint)impFrameData);
        w.UInt(ObjectBinary);
        w.UInt((uint)frames.Count);
        foreach (var f in frames)
        {
            w.CString(f.Name);
            w.Float(f.Position[0]); w.Float(f.Position[1]); w.Float(f.Position[2]);
            w.Float(f.Forward[0]); w.Float(f.Forward[1]); w.Float(f.Forward[2]);
            w.Float(f.Up[0]); w.Float(f.Up[1]); w.Float(f.Up[2]);
        }
        w.UInt(ObjectEnd);

        // idx 3: object = GroupGeo([mesh…]); textured meshes wrapped
        // TextureGeo(<stem import>, MeshGeo). Args-first token order: the mesh
        // list precedes the GroupGeo import (native convention, see above).
        w.UInt(3);
        w.UInt(ObjectList); w.UInt((uint)model.Meshes.Count);
        foreach (var mesh in model.Meshes)
            WriteMesh(w, mesh, impMesh, impTexture, impStem);
        w.UInt(ObjectImport); w.UInt((uint)impGroup);
        w.UInt(ObjectApply);
        w.UInt(ObjectEnd);

        return w.ToArray();
    }

    /// <summary>Texture stems in first-appearance order across the model's
    /// meshes (this is also the namespace order, matching the native files'
    /// convention of one namespace per texture unit).</summary>
    internal static string[] DistinctStems(GlbExportModel model) =>
        model.Meshes.Select(m => m.TextureStem).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void WriteMesh(TokenWriter w, GlbMesh mesh, int impMesh, int impTexture,
        Dictionary<string, int> impStem)
    {
        // The MeshGeo tokens; when nested inside TextureGeo there is NO End —
        // the geo is an argument on the same stack machine (native encodes
        // TextureGeo as [image][geo][fn][Apply], one object).
        void MeshGeoTokens(bool end)
        {
            w.UInt(ObjectImport); w.UInt((uint)impMesh);
            w.UInt(ObjectBinary);
            int nv = mesh.Positions.Length / 3;
            int ni = mesh.Indices.Length;
            w.UInt((uint)nv);
            w.UInt((uint)ni);
            // Packed native vertex record: x,y,z,u,v,nx,ny,nz (8 floats).
            for (int i = 0; i < nv; i++)
            {
                w.Float(mesh.Positions[3 * i]); w.Float(mesh.Positions[3 * i + 1]); w.Float(mesh.Positions[3 * i + 2]);
                w.Float(mesh.TexCoords[2 * i]); w.Float(mesh.TexCoords[2 * i + 1]);
                w.Float(mesh.Normals[3 * i]); w.Float(mesh.Normals[3 * i + 1]); w.Float(mesh.Normals[3 * i + 2]);
            }
            foreach (var idx in mesh.Indices)
                w.UShort(idx);
            if (end)
                w.UInt(ObjectEnd);
        }

        if (mesh.TextureStem is { } stem)
        {
            if (!impStem.TryGetValue(stem, out var stemImport))
                throw new InvalidDataException(
                    $"mesh '{mesh.Name}' references texture stem '{stem}' that is not in the model's texture-stem list");
            // TextureGeo args-first: image import, then the geo tokens, then
            // the TextureGeo import, then Apply (the parser pops geo first,
            // then the image — so the image is pushed first).
            w.UInt(ObjectImport); w.UInt((uint)stemImport);
            MeshGeoTokens(end: false);
            w.UInt(ObjectImport); w.UInt((uint)impTexture);
            w.UInt(ObjectApply);
            w.UInt(ObjectEnd);
        }
        else
        {
            MeshGeoTokens(end: true);
        }
    }

    /// <summary>The FrameData chunk to write. When the sidecar carried the raw
    /// records (<see cref="GlbExportModel.SourceMdlFrames"/>) they are written
    /// verbatim — that keeps the frames export byte-identical to the native
    /// file. Otherwise (new art) frames are synthesized from the mount nodes:
    /// name = <see cref="NativeNameMapper.NativeNameFor"/>, position/forward
    /// from the GLB node, up = (0,1,0) unless the forward is within 0.9 of it —
    /// the same fallback rule native's hardpoint-orientation code uses
    /// (<c>Igc/hullTypeIGC.cpp:117-124</c>).</summary>
    internal static List<MdlFrameRecord> ResolveFrames(GlbExportModel model)
    {
        if (model.SourceMdlFrames is { Count: > 0 })
            return [.. model.SourceMdlFrames];

        var frames = new List<MdlFrameRecord>(model.Mounts.Count);
        foreach (var m in model.Mounts)
        {
            var name = NativeNameMapper.IsNativeName(m.NativeName, m.NodeName)
                ? m.NativeName
                : NativeNameMapper.NativeNameFor(m.Kind, m.Index);
            var fwd = Normalize(m.Forward);
            float[] up = MathF.Abs(Dot(fwd, [0f, 1f, 0f])) < 0.9f ? [0f, 1f, 0f] : [0f, 0f, 1f];
            frames.Add(new MdlFrameRecord(name, m.Position, fwd, up));
        }
        return frames;
    }

    internal static float[] Normalize(float[] v)
    {
        float len = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        return len < 1e-8f ? [0f, 0f, 0f] : [v[0] / len, v[1] / len, v[2] / len];
    }

    private static float Dot(float[] a, float[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    /// <summary>Little-endian binary emitter for the token stream.</summary>
    private sealed class TokenWriter
    {
        private readonly MemoryStream _ms = new();
        private readonly byte[] _buf = new byte[8];

        public void UInt(uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buf, v);
            _ms.Write(_buf.AsSpan(0, 4));
        }

        public void UShort(ushort v)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_buf, v);
            _ms.Write(_buf.AsSpan(0, 2));
        }

        public void Float(float v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(_buf, v);
            _ms.Write(_buf.AsSpan(0, 4));
        }

        /// <summary>ASCII string, NUL-terminated, padded to a 4-byte boundary
        /// (the native <c>ReadString</c> skips <c>(len+1+3) &amp; ~3</c> bytes,
        /// mdlbin.cpp:67-73).</summary>
        public void CString(string s)
        {
            var bytes = new byte[(s.Length + 1 + 3) & ~3];
            Encoding.ASCII.GetBytes(s, bytes);
            _ms.Write(bytes);
        }

        public void Import(int nsIndex, int importIndex, string name)
        {
            UInt((uint)nsIndex);        // namespace-table index (mdlbin.cpp:117-127)
            CString(name);              // member name; the import's own index is its position in the list
        }

        public byte[] ToArray() => _ms.ToArray();
    }
}
