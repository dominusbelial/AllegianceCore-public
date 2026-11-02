using System.Buffers.Binary;
using System.Text;
using Allegiance.Core;

namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// Offline binary ThingGeo optimizer. Native: effect/efapp.cpp:713-818,
/// engine/geometry.cpp:1009-1012,1037-1068,1187-1399,1789-1900.
/// Keeps the namespace graph (including every LOD), rather than going through
/// the GLB adapter, which intentionally exports only the finest geometry.
/// </summary>
public static class MdlOptimizer
{
    public sealed record Result(byte[] Bytes, int NodesBefore, int NodesAfter, int Passes);

    public static Result Optimize(byte[] input)
    {
        var doc = new Document(input);
        var root = doc.Exports.IndexOf("object");
        if (root < 0) throw new InvalidDataException("model has no object export");
        var geo = doc.Objects[root];
        int before = Count(geo), passes = 0;

        // Native's callback extracts named frm-* groups before stripping materials
        // (efapp.cpp:527-610). Binary GroupGeo stores ONLY its child list; names
        // from the .x importer are not serialized (modeler.cpp:1570-1590).
        // For compiled inputs the extraction has already happened: retain the raw
        // lights/frames exports and frame number, even if no geometry references them.
        while (true)
        {
            var old = doc.EncodeNode(geo);
            var stripped = RemoveMaterial(geo);
            if (!doc.EncodeNode(stripped).AsSpan().SequenceEqual(old))
                geo = Fold(stripped);
            geo = FoldTexture(geo);
            geo = Fold(geo);
            passes++;
            if (old.AsSpan().SequenceEqual(doc.EncodeNode(geo))) break;
        }
        doc.Objects[root] = geo;
        var bytes = doc.Write();
        // Native mdlc/mdlc.cpp:135-150: reload the binary before accepting it.
        var loaded = MdlFile.Parse(bytes);
        if (loaded.UnsupportedGeometryNodes.Count != 0)
            throw new InvalidDataException("optimized geometry is unsupported by the game loader");
        int after = Count(geo);
        if (after > before) throw new InvalidDataException("optimization increased geometry node count");
        return new Result(bytes, before, after, passes);
    }

    private sealed record Node(string Kind, byte[] Data, List<Node> Children)
    {
        public static Node Make(string kind, params Node[] children) => new(kind, [], [.. children]);
    }

    private static bool Is(Node n, string kind) => n.Kind == kind;
    private static Node List(IEnumerable<Node> nodes) => new("list", [], nodes.ToList());
    private static Node Group(IEnumerable<Node> nodes) => Node.Make("GroupGeo", List(nodes));
    private static bool Empty(Node n) => Is(n, "emptyGeo");
    private static Node Texture(Node image, Node geo) => Node.Make("TextureGeo", image, geo);
    private static int Count(Node n) => (n.Kind.EndsWith("Geo", StringComparison.Ordinal) ? 1 : 0)
        + n.Children.Sum(Count);

    private static Node RemoveMaterial(Node n)
    {
        n = n with { Children = n.Children.Select(RemoveMaterial).ToList() };
        return Is(n, "MaterialGeo") ? n.Children[1] : n;
    }

    private static bool SameImage(Node a, Node b) => a.Kind == b.Kind && a.Data.AsSpan().SequenceEqual(b.Data);

    private static Node FoldTexture(Node n)
    {
        n = n with { Children = n.Children.Select(FoldTexture).ToList() };
        if (Is(n, "TextureGeo"))
        {
            // Native outer TextureGeo wins (geometry.cpp:1051-1056).
            while (Is(n.Children[1], "TextureGeo"))
                n = Texture(n.Children[0], n.Children[1].Children[1]);
        }
        else if (Is(n, "GroupGeo"))
        {
            var children = n.Children[0].Children;
            var remaining = new List<Node>();
            var buckets = new List<(Node Image, List<Node> Geos)>();
            foreach (var child in children)
            {
                if (!Is(child, "TextureGeo")) { remaining.Add(child); continue; }
                int i = buckets.FindIndex(b => SameImage(b.Image, child.Children[0]));
                if (i < 0) { i = buckets.Count; buckets.Add((child.Children[0], [])); }
                buckets[i].Geos.Add(child.Children[1]);
            }
            // Avoid reordering a group unless a texture really folds.
            if (buckets.Any(b => b.Geos.Count > 1))
            {
                remaining.AddRange(buckets.Select(b => Texture(b.Image,
                    b.Geos.Count == 1 ? b.Geos[0] : Group(b.Geos))));
                n = Group(remaining);
            }
        }
        else if (Is(n, "LODGeo"))
        {
            var levels = n.Children[0].Children;
            var geos = new[] { n.Children[1] }.Concat(levels.Select(p => p.Children[0])).ToArray();
            if (geos.All(g => Is(g, "TextureGeo")) &&
                geos.All(g => SameImage(g.Children[0], geos[0].Children[0])))
            {
                n = Texture(geos[0].Children[0], Node.Make("LODGeo",
                    List(levels.Select(p => Node.Make("pair", p.Children[0].Children[1], p.Children[1]))),
                    geos[0].Children[1]));
            }
        }
        return n;
    }

    private static Node Fold(Node n)
    {
        n = n with { Children = n.Children.Select(Fold).ToList() };
        if (Is(n, "TextureGeo") && Empty(n.Children[1])) return n.Children[1];
        if (Is(n, "LODGeo") && n.Children[0].Children.Count == 0) return n.Children[1];
        if (!Is(n, "GroupGeo")) return n;
        var children = n.Children[0].Children.SelectMany(c =>
            Is(c, "GroupGeo") ? c.Children[0].Children : new List<Node> { c }).Where(c => !Empty(c)).ToList();
        // GroupGeo::Fold combines bare meshes only; texture buckets were formed
        // above, so different materials never accidentally share a draw call.
        var output = new List<Node>();
        int meshAt = -1;
        foreach (var child in children)
        {
            if (Is(child, "MeshGeo") && meshAt >= 0 &&
                VertexCount(output[meshAt]) + VertexCount(child) <= ushort.MaxValue)
                output[meshAt] = Combine(output[meshAt], child);
            else
            {
                if (Is(child, "MeshGeo")) meshAt = output.Count;
                output.Add(child);
            }
        }
        return output.Count switch { 0 => Node.Make("emptyGeo"), 1 => output[0], _ => Group(output) };
    }

    private static int VertexCount(Node n) => BinaryPrimitives.ReadInt32LittleEndian(n.Data);
    private static Node Combine(Node a, Node b)
    {
        // TMeshGeo::Combine starts with the argument mesh and maps the receiver
        // onto it, comparing all eight floats (geometry.cpp:327-377;
        // context.h:160-173). UV seams and split normals must survive.
        int av = VertexCount(a), bv = VertexCount(b);
        int ai = BinaryPrimitives.ReadInt32LittleEndian(a.Data.AsSpan(4));
        int bi = BinaryPrimitives.ReadInt32LittleEndian(b.Data.AsSpan(4));
        var vertices = Enumerable.Range(0, bv).Select(i => b.Data.AsSpan(8 + i * 32, 32).ToArray()).ToList();
        var map = new ushort[av];
        for (int i = 0; i < av; i++)
        {
            var vertex = a.Data.AsSpan(8 + i * 32, 32).ToArray();
            int match = vertices.FindIndex(v => Enumerable.Range(0, 8).All(j =>
                BinaryPrimitives.ReadSingleLittleEndian(v.AsSpan(j * 4)) ==
                BinaryPrimitives.ReadSingleLittleEndian(vertex.AsSpan(j * 4))));
            if (match < 0) { match = vertices.Count; vertices.Add(vertex); }
            map[i] = checked((ushort)match);
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(vertices.Count); w.Write(ai + bi);
        foreach (var vertex in vertices) w.Write(vertex);
        w.Write(b.Data.AsSpan(8 + bv * 32, bi * 2));
        for (int i = 0; i < ai; i++)
            w.Write(map[BinaryPrimitives.ReadUInt16LittleEndian(a.Data.AsSpan(8 + av * 32 + i * 2))]);
        return new Node("MeshGeo", ms.ToArray(), []);
    }

    // This small token graph deliberately rejects unknown functions rather than
    // flattening/dropping them. Binary payloads are copied verbatim, not decoded
    // and quantized. References resolve to nodes; exports remain in file order.
    private sealed class Document
    {
        private readonly byte[] _input;
        private int _pos;
        private readonly List<string> _namespaces = [];
        private readonly List<(uint Namespace, string Name)> _imports = [];
        public List<string> Exports { get; } = [];
        public Dictionary<int, Node> Objects { get; } = [];
        private readonly List<int> _order = [];

        public Document(byte[] input)
        {
            _input = input;
            if (UInt() != MdlWriter.MdlMagic || UInt() != MdlWriter.MdlVersion)
                throw new InvalidDataException("optimize requires compiled binary MDL");
            int ns = checked((int)UInt()), ni = checked((int)UInt()), ne = checked((int)UInt()), nd = checked((int)UInt());
            for (int i = 0; i < ns; i++) _namespaces.Add(String());
            for (int i = 0; i < ni; i++) _imports.Add((UInt(), String()));
            for (int i = 0; i < ne; i++) Exports.Add(String());
            for (int i = 0; i < ne + nd; i++)
            {
                int index = checked((int)UInt());
                _order.Add(index); Objects.Add(index, ReadNode());
            }
            if (_pos != input.Length) throw new InvalidDataException("unexpected trailing MDL data");
        }

        private byte[] Take(int length)
        {
            if (length < 0 || length > _input.Length - _pos) throw new InvalidDataException("truncated MDL");
            var data = _input.AsSpan(_pos, length).ToArray(); _pos += length; return data;
        }
        private uint UInt() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        private string String()
        {
            int end = Array.IndexOf(_input, (byte)0, _pos);
            if (end < 0) throw new InvalidDataException("unterminated MDL string");
            int length = end - _pos;
            return Encoding.ASCII.GetString(Take((length + 4) & ~3), 0, length);
        }
        private Node ReadNode()
        {
            var stack = new Stack<Node>();
            while (true)
            {
                switch (UInt())
                {
                    case 0:
                        if (stack.Count != 1) throw new InvalidDataException("invalid MDL stack");
                        return stack.Pop();
                    case 1: stack.Push(new Node("float", Take(4), [])); break;
                    case 2: stack.Push(new Node("string", Encoding.ASCII.GetBytes(String()), [])); break;
                    case 3: stack.Push(Node.Make("true")); break;
                    case 4: stack.Push(Node.Make("false")); break;
                    case 5:
                        int count = checked((int)UInt()); var items = new List<Node>();
                        for (int i = 0; i < count; i++) items.Add(ReadNode());
                        stack.Push(List(items)); break;
                    case 6:
                        var fn = stack.Pop(); string name = ImportName(fn);
                        int arity = name switch
                        {
                            "GroupGeo" or "ModifiableNumber" or "LightsGeo" => 1,
                            "TextureGeo" or "MaterialGeo" or "LODGeo" => 2,
                            "Material" or "Color" => 4,
                            _ => throw new InvalidDataException($"unsupported optimize function: {name}"),
                        };
                        var args = new Node[arity];
                        for (int i = arity - 1; i >= 0; i--) args[i] = stack.Pop();
                        stack.Push(new Node(name, [], [.. args])); break;
                    case 7:
                        string binary = ImportName(stack.Pop()); int start = _pos;
                        switch (binary)
                        {
                            case "MeshGeo":
                                int nv = checked((int)UInt()), ni = checked((int)UInt());
                                Take(checked(nv * 32 + ni * 2)); break;
                            case "LightsGeo": Take(checked((int)UInt() * 48)); break;
                            case "FrameData":
                                int nf = checked((int)UInt());
                                for (int i = 0; i < nf; i++) { String(); Take(36); }
                                break;
                            default: throw new InvalidDataException($"unsupported optimize binary: {binary}");
                        }
                        stack.Push(new Node(binary, _input.AsSpan(start, _pos - start).ToArray(), [])); break;
                    case 8: stack.Push(Objects[checked((int)UInt())]); break;
                    case 9:
                        var index = UInt();
                        if (index >= _imports.Count) throw new InvalidDataException("invalid MDL import");
                        var import = new Node("import", BitConverter.GetBytes(index), []);
                        stack.Push(ImportName(import) == "emptyGeo" ? Node.Make("emptyGeo") : import); break;
                    case 10:
                        var b = stack.Pop(); var a = stack.Pop(); stack.Push(Node.Make("pair", a, b)); break;
                    default: throw new InvalidDataException("unsupported MDL token");
                }
            }
        }
        private string ImportName(Node n) => n.Kind == "import"
            ? _imports[checked((int)BinaryPrimitives.ReadUInt32LittleEndian(n.Data))].Name
            : throw new InvalidDataException("MDL function must be an import");
        private int ImportIndex(string name)
        {
            int i = _imports.FindIndex(p => p.Name == name && _namespaces[(int)p.Namespace] == "model");
            if (i >= 0) return i;
            int ns = _namespaces.IndexOf("model");
            if (ns < 0) { ns = _namespaces.Count; _namespaces.Add("model"); }
            _imports.Add(((uint)ns, name)); return _imports.Count - 1;
        }
        private static void WriteString(BinaryWriter w, byte[] bytes)
        {
            w.Write(bytes); w.Write((byte)0);
            for (int i = bytes.Length + 1; i % 4 != 0; i++) w.Write((byte)0);
        }
        private void Emit(BinaryWriter w, Node n, bool end)
        {
            switch (n.Kind)
            {
                case "float": w.Write(1u); w.Write(n.Data); break;
                case "string": w.Write(2u); WriteString(w, n.Data); break;
                case "true": w.Write(3u); break;
                case "false": w.Write(4u); break;
                case "list":
                    w.Write(5u); w.Write(n.Children.Count);
                    foreach (var child in n.Children) Emit(w, child, true);
                    break;
                case "pair":
                    foreach (var child in n.Children) Emit(w, child, false);
                    w.Write(10u); break;
                case "import": w.Write(9u); w.Write(n.Data); break;
                case "emptyGeo": w.Write(9u); w.Write(ImportIndex("emptyGeo")); break;
                default:
                    foreach (var child in n.Children) Emit(w, child, false);
                    int index = _imports.FindIndex(p => p.Name == n.Kind);
                    if (index < 0) index = ImportIndex(n.Kind);
                    w.Write(9u); w.Write(index);
                    w.Write(n.Data.Length > 0 ? 7u : 6u);
                    if (n.Data.Length > 0) w.Write(n.Data);
                    break;
            }
            if (end) w.Write(0u);
        }
        public byte[] EncodeNode(Node n)
        {
            using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
            Emit(w, n, true); return ms.ToArray();
        }
        public byte[] Write()
        {
            // Emit first: folds may introduce an emptyGeo import.
            var encoded = _order.Select(i => (Index: i, Bytes: EncodeNode(Objects[i]))).ToArray();
            using var ms = new MemoryStream(); using var w = new BinaryWriter(ms);
            w.Write(MdlWriter.MdlMagic); w.Write(MdlWriter.MdlVersion);
            w.Write(_namespaces.Count); w.Write(_imports.Count); w.Write(Exports.Count); w.Write(Objects.Count - Exports.Count);
            foreach (var ns in _namespaces) WriteString(w, Encoding.ASCII.GetBytes(ns));
            foreach (var import in _imports) { w.Write(import.Namespace); WriteString(w, Encoding.ASCII.GetBytes(import.Name)); }
            foreach (var export in Exports) WriteString(w, Encoding.ASCII.GetBytes(export));
            foreach (var item in encoded) { w.Write(item.Index); w.Write(item.Bytes); }
            return ms.ToArray();
        }
    }
}
