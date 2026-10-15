using System.Buffers.Binary;
using System.Text;

namespace Allegiance.Core;

/// <summary>
/// A single decoded mesh from a <c>.mdl</c> model file: per-vertex position /
/// texcoord / normal arrays plus a triangle-list index array.
/// </summary>
/// <remarks>
/// Vertices are split from the file's packed
/// <c>x,y,z,u,v,nx,ny,nz</c> 32-byte records into flat arrays so a downstream
/// MonoGame renderer can build its buffers without re-reading the file.
/// <see cref="Indices"/> preserves the <b>raw file order</b> exactly — no
/// triangle-winding correction is applied here (whether the DirectX-style
/// winding needs a flip for MonoGame is an empirical question for the renderer
/// task that consumes this output).
/// </remarks>
public sealed class MdlMesh
{
    /// <summary>Per-vertex position, 3 floats per vertex: <c>x,y,z</c>.</summary>
    public required float[] Positions { get; init; }

    /// <summary>Per-vertex texcoord, 2 floats per vertex: <c>u,v</c>.</summary>
    public required float[] TexCoords { get; init; }

    /// <summary>Per-vertex normal, 3 floats per vertex: <c>nx,ny,nz</c>.</summary>
    public required float[] Normals { get; init; }

    /// <summary>Triangle-list index array, raw order as read from the file.</summary>
    public required ushort[] Indices { get; init; }

    /// <summary>The enclosing TextureGeo image namespace; null means inherit
    /// ThingGeo's initial texture (geometry.cpp:1079-1082, 1439-1473).</summary>
    public string? TextureStem { get; init; }

    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>
/// P628 — one native LODGeo distance-detail level: its baked pixel-radius
/// threshold (<c>LODGeoImpl::m_radii</c>, geometry.cpp:1634) and the resolved
/// lower-detail mesh list for that level. See <see cref="MdlFile.LodLevels"/>.
/// </summary>
public sealed class MdlLodLevel
{
    /// <summary>The baked pixel-radius threshold (screen pixels) at/above which
    /// this coarser level kicks in — the artist-authored <c>(pixels, geo)</c>
    /// value (modeler.cpp:1676-1683).</summary>
    public required float PixelRadius { get; init; }

    /// <summary>This level's meshes (a reduced-detail variant of the base).</summary>
    public required IReadOnlyList<MdlMesh> Meshes { get; init; }
}

/// <summary>
/// A named hardpoint / attach-point record (from <c>FrameData</c>) — weapon
/// mounts, engine nozzles, etc. Carried as named position/orientation metadata;
/// not mesh geometry. Useful later for engine-nozzle-positioned smoke-trail
/// emission points.
/// </summary>
public sealed class MdlFrame
{
    public required string Name { get; init; }

    /// <summary>3 floats: <c>x,y,z</c>.</summary>
    public required float[] Position { get; init; }

    /// <summary>3 floats: <c>x,y,z</c>.</summary>
    public required float[] Forward { get; init; }

    /// <summary>3 floats: <c>x,y,z</c>.</summary>
    public required float[] Up { get; init; }
}

/// <summary>Serialized native LightData (effect/efapp.cpp:99-112, 330-340).
/// Positions are model-local, in the same space as mesh vertices.</summary>
public sealed record MdlLight(float[] Color, float[] Position, float Period,
    float Phase, float RampUp, float Hold, float RampDown);

/// <summary>
/// The result of parsing an Allegiance <c>.mdl</c> binary model file: the file
/// header namespaces/imports/exports plus the resolved geometry (walked only
/// from the <c>"object"</c> export) and the named <c>FrameData</c> records.
/// </summary>
public sealed class MdlFile
{
    internal MdlFile() { }
    /// <summary>Only lights reachable from the native "lights" export.</summary>
    public IReadOnlyList<MdlLight> Lights { get; init; } = [];
    /// <summary>Unsupported nodes reached while collecting drawable geometry.
    /// Exposed so asset audits cannot silently accept dropped subgroups.</summary>
    public IReadOnlyList<string> UnsupportedGeometryNodes { get; init; } = [];
    /// <summary>The namespace name table (file header).</summary>
    public required IReadOnlyList<string> Namespaces { get; init; }

    /// <summary>The import member-name table (file header).</summary>
    public required IReadOnlyList<string> Imports { get; init; }

    /// <summary>The export names (file header).</summary>
    public required IReadOnlyList<string> Exports { get; init; }

    /// <summary>
    /// The final resolved mesh list, collected by walking only the
    /// <c>"object"</c> export (per <c>mdlc.cpp</c>'s own
    /// <c>FindMemberLocal("object")</c> convention) — not every
    /// <c>MeshGeo</c> seen during parsing, which would double-count
    /// <c>LODGeo</c>'s discarded low-detail variants. Multiple meshes per file
    /// are real and expected (turret / antenna / substructure chunks).
    /// </summary>
    public required IReadOnlyList<MdlMesh> Meshes { get; init; }

    /// <summary>
    /// Named hardpoint/attach-point records, accumulated from every
    /// <c>FrameData</c> read while parsing (matched to the Python reference's
    /// flat accumulation across the whole file).
    /// </summary>
    public required IReadOnlyList<MdlFrame> Frames { get; init; }

    /// <summary>
    /// P628 — the LODGeo's distance-detail levels retained for native LOD
    /// (<c>engine/geometry.cpp</c> <c>LODGeoImpl</c>; modelled in
    /// <c>engine/modeler.cpp:1666-1686</c> <c>LODGeoFactory::Apply</c> as
    /// <c>LODGeo(baseGeo, [(pixels, geo), ...])</c>). Each entry carries the
    /// baked <b>pixel radius</b> threshold (the <c>m_radii</c> member,
    /// geometry.cpp:1634, used in <c>LODGeoImpl::Render</c> geometry.cpp:1760-1773)
    /// and that level's resolved meshes (a lower-triangle-count detail variant).
    /// Ordered descending by pixel radius (finest→coarsest), matching the
    /// artist-authored list order. Empty when the model has no LODGeo.
    /// </summary>
    public required IReadOnlyList<MdlLodLevel> LodLevels { get; init; }

    /// <summary>Parse a <c>.mdl</c> model file from disk.</summary>
    public static MdlFile Parse(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>Parse a <c>.mdl</c> model file from its raw bytes.</summary>
    public static MdlFile Parse(byte[] data) => new MdlParser(data).Run();
}

/// <summary>
/// Faithful C# port of <c>~/Development/allegiance-tools/reference/mdl_parse.py</c>
/// (the proven, tested Python reference). Cracked format source:
/// <c>engine/mdlbin.cpp</c>, <c>engine/value.h</c>, <c>engine/modeler.cpp</c>,
/// <c>effect/efapp.cpp</c>; see the wiki page
/// <c>allegiance-mdl-model-format.md</c>.
/// </summary>
internal static class MdlFileParserSupport
{
    internal const uint MdlMagic = 0x0DEBADF00u;
    internal const uint MdlVersion = 0x00010000u;
}

/// <summary>Private implementation of the <c>.mdl</c> object-graph reader.</summary>
internal sealed class MdlParser
{
    // -- token stream -----------------------------------------------------
    private const uint ObjectEnd = 0;
    private const uint ObjectFloat = 1;
    private const uint ObjectString = 2;
    private const uint ObjectTrue = 3;
    private const uint ObjectFalse = 4;
    private const uint ObjectList = 5;
    private const uint ObjectApply = 6;
    private const uint ObjectBinary = 7;
    private const uint ObjectReference = 8;
    private const uint ObjectImport = 9;
    private const uint ObjectPair = 10;

    private readonly byte[] _data;
    private int _pos;
    private readonly List<string> _importNames = [];
    private readonly List<string> _importNamespaces = [];
    private readonly List<string> _unsupportedGeometryNodes = [];
    private readonly List<MdlFrame> _frames = [];
    private readonly Dictionary<uint, MdlNode> _objects = [];
    private readonly List<MdlMesh> _meshes = [];
    private readonly List<MdlLodLevel> _lodLevels = [];

    internal MdlParser(byte[] data) => _data = data;

    public MdlFile Run()
    {
        uint magic = ReadUInt32();
        uint version = ReadUInt32();
        if (magic != MdlFileParserSupport.MdlMagic)
            throw new InvalidDataException($"bad magic 0x{magic:x8}");
        if (version != MdlFileParserSupport.MdlVersion)
            throw new InvalidDataException($"bad version 0x{version:x8}");

        uint countNamespaces = ReadUInt32();
        uint countImports = ReadUInt32();
        uint countExports = ReadUInt32();
        uint countDefinitions = ReadUInt32();

        var namespaces = new string[countNamespaces];
        for (int i = 0; i < namespaces.Length; i++)
            namespaces[i] = ReadCString();

        for (int i = 0; i < countImports; i++)
        {
            _importNamespaces.Add(namespaces[ReadUInt32()]);
            _importNames.Add(ReadCString());
        }

        var exports = new string[countExports];
        for (int i = 0; i < exports.Length; i++)
            exports[i] = ReadCString();

        for (int i = 0; i < countExports + countDefinitions; i++)
        {
            uint idx = ReadUInt32();
            _objects[idx] = ReadObject();
        }

        // Collect meshes by walking only the resolved "object" export (the real
        // root geometry) -- never every MeshGeo seen during parsing, which
        // would double-count LODGeo's discarded low-detail variants.
        for (int i = 0; i < exports.Length; i++)
        {
            if (exports[i] == "object")
            {
                _objects.TryGetValue((uint)i, out var root);
                CollectMeshes(root);
            }
        }

        return new MdlFile
        {
            Namespaces = namespaces,
            Imports = _importNames,
            Exports = exports,
            Meshes = _meshes,
            Frames = _frames,
            LodLevels = _lodLevels,
            Lights = Array.IndexOf(exports, "lights") is int li && li >= 0
                && _objects.TryGetValue((uint)li, out var lights) && lights is MdlLightsGeo lg
                    ? lg.Lights : [],
            UnsupportedGeometryNodes = _unsupportedGeometryNodes,
        };
    }

    // -- generic object-graph reader --------------------------------------

    private MdlNode ReadObject()
    {
        var stack = new Stack<MdlNode>();
        while (true)
        {
            uint token = ReadUInt32();
            switch (token)
            {
                case ObjectEnd:
                    if (stack.Count != 1)
                        throw new InvalidDataException(
                            $"ObjectEnd with stack count {stack.Count}");
                    return stack.Pop();
                case ObjectFloat:
                    stack.Push(new MdlFloat(ReadSingle()));
                    break;
                case ObjectString:
                    stack.Push(new MdlString(ReadCString()));
                    break;
                case ObjectTrue:
                    stack.Push(MdlBool.True);
                    break;
                case ObjectFalse:
                    stack.Push(MdlBool.False);
                    break;
                case ObjectList:
                {
                    uint count = ReadUInt32();
                    var list = new List<MdlNode>((int)count);
                    for (int i = 0; i < (int)count; i++)
                        list.Add(ReadObject());
                    stack.Push(new MdlList(list));
                    break;
                }
                case ObjectApply:
                {
                    var fn = stack.Pop();
                    stack.Push(Apply(fn, stack));
                    break;
                }
                case ObjectBinary:
                {
                    var fn = stack.Pop();
                    stack.Push(ReadBinary(fn));
                    break;
                }
                case ObjectReference:
                {
                    uint idx = ReadUInt32();
                    stack.Push(_objects[idx]);
                    break;
                }
                case ObjectImport:
                {
                    uint idx = ReadUInt32();
                    stack.Push(new MdlImport(_importNames[(int)idx], _importNamespaces[(int)idx]));
                    break;
                }
                case ObjectPair:
                {
                    var b = stack.Pop();
                    var a = stack.Pop();
                    stack.Push(new MdlPair(a, b));
                    break;
                }
                default:
                    throw new InvalidDataException(
                        $"bad token {token} at 0x{_pos - 4:x}");
            }
        }
    }

    private static string? FnName(MdlNode fn) =>
        fn is MdlImport imp ? imp.Name : null;

    private MdlNode Apply(MdlNode fn, Stack<MdlNode> stack)
    {
        string? name = FnName(fn);
        switch (name)
        {
            case "GroupGeo":
                return new MdlGroupGeo(stack.Pop());
            case "TextureGeo":
            {
                var geo = stack.Pop();
                var image = stack.Pop();
                return new MdlTextureGeo(image, geo);
            }
            case "ModifiableNumber":
                return new MdlModifiableNumber(stack.Pop());
            case "LightsGeo":
                stack.Pop();
                return new MdlGeneric("LightsGeo");
            case "LODGeo":
                // engine/modeler.cpp LODGeoFactory::Apply pops (geo, lodList) in
                // push/pop order -- geo is the base/default (highest-detail)
                // geometry; lodList holds the lower-detail variants for
                // distance-based swapping. Keep the base mesh AND retain the
                // levels so the renderer can select detail by screen size
                // (P628 native LOD; geometry.cpp LODGeoImpl::Render).
            {
                var geo = stack.Pop();
                var lodList = stack.Pop();
                CaptureLodLevels(lodList);
                return geo;
            }
            default:
                // Unknown Apply target (texture import object itself, or
                // something not yet identified): pass through minimally.
                if (stack.Count > 0) stack.Pop();
                return new MdlGeneric($"Apply<{name ?? "?"}>");
        }
    }

    private MdlNode ReadBinary(MdlNode fn)
    {
        string? name = FnName(fn);
        switch (name)
        {
            case "MeshGeo":
            {
                uint countVertices = ReadUInt32();
                uint countIndices = ReadUInt32();
                // 8 floats per vertex: x,y,z,u,v,nx,ny,nz (position -> texcoord
                // -> normal -- NOT position -> normal -> texcoord).
                var verts = new float[8 * countVertices];
                for (int i = 0; i < verts.Length; i++)
                    verts[i] = ReadSingle();
                var indices = new ushort[countIndices];
                for (int i = 0; i < indices.Length; i++)
                    indices[i] = ReadUInt16();
                return new MdlMeshGeo(verts, indices);
            }
            case "LightsGeo":
            {
                // Color RGBA + position XYZ + five timing floats, 48 bytes.
                uint count = ReadUInt32();
                var lights = new List<MdlLight>((int)count);
                for (int i = 0; i < count; i++)
                    lights.Add(new MdlLight(
                        [ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle()],
                        ReadVec3(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle()));
                return new MdlLightsGeo(lights);
            }
            case "FrameData":
            {
                // effect/efapp.cpp FrameDataFactory::Read: DWORD count, then per
                // entry a padded C-string name + 3 raw Vectors (position,
                // forward, up; 12 bytes each, no padding between).
                uint count = ReadUInt32();
                var frames = new List<MdlFrame>((int)count);
                for (int i = 0; i < (int)count; i++)
                {
                    string fname = ReadCString();
                    var pos = ReadVec3();
                    var fwd = ReadVec3();
                    var up = ReadVec3();
                    frames.Add(new MdlFrame
                    {
                        Name = fname,
                        Position = pos,
                        Forward = fwd,
                        Up = up,
                    });
                }
                _frames.AddRange(frames);
                return new MdlFrameData(frames);
            }
            default:
                throw new InvalidDataException(
                    $"no Read() handler implemented for binary function {name ?? "?"}");
        }
    }

    // -- mesh collection (walk the resolved "object" export) --------------

    private void CollectMeshes(MdlNode? node) => CollectMeshes(node, _meshes);

    private void CollectMeshes(MdlNode? node, List<MdlMesh> sink, string? texture = null)
    {
        switch (node)
        {
            case null:
                return;
            case MdlMeshGeo m:
                sink.Add(BuildMesh(m, texture));
                break;
            case MdlGroupGeo g:
                CollectMeshes(g.Children, sink, texture);
                break;
            case MdlTextureGeo t:
                CollectMeshes(t.Geo, sink, t.Image is MdlImport image ? image.Namespace : texture);
                break;
            case MdlList l:
                foreach (var item in l.Items) CollectMeshes(item, sink, texture);
                break;
            case MdlGeneric unknown:
                _unsupportedGeometryNodes.Add(unknown.Type);
                break;
        }
    }

    /// <summary>P628 — capture the LODGeo lod-list into <see cref="_lodLevels"/>.
    /// The list is <c>[(pixels, geo), (pixels, geo), ...]</c> (modeler.cpp:1676-1683);
    /// in the binary each pair is encoded <b>(geo, pixels)</b> — observed on
    /// fig05/utl18a/bgrnd05 — so <c>pair.A</c> is the reduced-detail geo node
    /// and <c>pair.B</c> the baked pixel-radius <c>MdlFloat</c> threshold. The
    /// geo is resolved to meshes here so the renderer can build a GPU level per
    /// LOD. Malformed entries are skipped defensively.</summary>
    private void CaptureLodLevels(MdlNode? lodList)
    {
        if (lodList is not MdlList list) return;
        foreach (var item in list.Items)
        {
            if (item is not MdlPair pair
                || pair.B is not MdlFloat px
                || pair.A is not { } geoNode)
                continue;
            var levelMeshes = new List<MdlMesh>();
            CollectMeshes(geoNode, levelMeshes);
            if (levelMeshes.Count == 0) continue;
            _lodLevels.Add(new MdlLodLevel { PixelRadius = px.Value, Meshes = levelMeshes });
        }
    }

    private static MdlMesh BuildMesh(MdlMeshGeo m, string? texture)
    {
        int n = m.Verts.Length / 8;
        var pos = new float[3 * n];
        var tc = new float[2 * n];
        var nr = new float[3 * n];
        for (int i = 0; i < n; i++)
        {
            int b = 8 * i;
            pos[3 * i] = m.Verts[b];
            pos[3 * i + 1] = m.Verts[b + 1];
            pos[3 * i + 2] = m.Verts[b + 2];
            tc[2 * i] = m.Verts[b + 3];
            tc[2 * i + 1] = m.Verts[b + 4];
            nr[3 * i] = m.Verts[b + 5];
            nr[3 * i + 1] = m.Verts[b + 6];
            nr[3 * i + 2] = m.Verts[b + 7];
        }
        return new MdlMesh
        {
            Positions = pos,
            TexCoords = tc,
            Normals = nr,
            Indices = m.Indices,
            TextureStem = texture,
        };
    }

    // -- low-level reader --------------------------------------------------

    private uint ReadUInt32()
    {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos));
        _pos += 4;
        return v;
    }

    private ushort ReadUInt16()
    {
        var v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_pos));
        _pos += 2;
        return v;
    }

    private float ReadSingle()
    {
        var v = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(_pos));
        _pos += 4;
        return v;
    }

    private float[] ReadVec3()
    {
        float[] v = [ReadSingle(), ReadSingle(), ReadSingle()];
        return v;
    }

    private string ReadCString()
    {
        int start = _pos;
        int end = Array.IndexOf(_data, (byte)0, start);
        if (end < 0)
            throw new InvalidDataException($"unterminated cstring at 0x{start:x}");
        int length = end - start + 1;          // strlen + NUL
        int padded = (length + 3) & ~3;        // (strlen+1+3) & ~3
        string s = Encoding.ASCII.GetString(_data, start, end - start);
        _pos = start + padded;
        return s;
    }
}

/// <summary>
/// Discriminated node types produced by the <c>.mdl</c> token-stack machine.
/// Kept internal — the public surface is <see cref="MdlFile"/> /
/// <see cref="MdlMesh"/> / <see cref="MdlFrame"/>.
/// </summary>
internal abstract class MdlNode;

internal sealed class MdlFloat(float value) : MdlNode
{
    public float Value { get; } = value;
}

internal sealed class MdlString(string value) : MdlNode
{
    public string Value { get; } = value;
}

internal sealed class MdlBool(bool value) : MdlNode
{
    public static readonly MdlBool True = new(true);
    public static readonly MdlBool False = new(false);
    public bool Value { get; } = value;
}

internal sealed class MdlList(List<MdlNode> items) : MdlNode
{
    public List<MdlNode> Items { get; } = items;
}

internal sealed class MdlPair(MdlNode a, MdlNode b) : MdlNode
{
    public MdlNode A { get; } = a;
    public MdlNode B { get; } = b;
}

internal sealed class MdlImport(string name, string ns) : MdlNode
{
    public string Name { get; } = name;
    public string Namespace { get; } = ns;
}

internal sealed class MdlGroupGeo(MdlNode children) : MdlNode
{
    public MdlNode Children { get; } = children;
}

internal sealed class MdlTextureGeo(MdlNode image, MdlNode geo) : MdlNode
{
    public MdlNode Image { get; } = image;
    public MdlNode Geo { get; } = geo;
}

internal sealed class MdlModifiableNumber(MdlNode value) : MdlNode
{
    public MdlNode Value { get; } = value;
}

internal sealed class MdlMeshGeo(float[] verts, ushort[] indices) : MdlNode
{
    public float[] Verts { get; } = verts;
    public ushort[] Indices { get; } = indices;
}

internal sealed class MdlFrameData(IReadOnlyList<MdlFrame> frames) : MdlNode
{
    public IReadOnlyList<MdlFrame> Frames { get; } = frames;
}

internal sealed class MdlGeneric(string type) : MdlNode
{
    public string Type { get; } = type;
}

internal sealed class MdlLightsGeo(IReadOnlyList<MdlLight> lights) : MdlNode
{
    public IReadOnlyList<MdlLight> Lights { get; } = lights;
}
