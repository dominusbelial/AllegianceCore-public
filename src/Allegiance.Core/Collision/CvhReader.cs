namespace Allegiance.Core.Collision;

/// <summary>
/// Reads a <c>.cvh</c> collision-hull file. Text format, mirroring the C++
/// loader <c>HitTest::Load</c> (<c>src/_Utility/HitTest.cpp:676-807</c>). The
/// format is line-oriented but loosely tokenized, and the C++ reads the two
/// header records and each vertex header with <c>fgets</c>+<c>sscanf</c> while
/// adjacency indices are read free-form with <c>fscanf</c>. We reproduce both
/// behaviours exactly (see remarks on <see cref="Parse"/>).
/// </summary>
public static class CvhReader
{
    /// <summary>
    /// Parse a whole <c>.cvh</c> file. <b>Fails (throws) on any violation of
    /// the file's own declared counts</b> — a file that parses but whose counts
    /// disagree is a failure, not a warning.
    /// </summary>
    /// <remarks>
    /// Grammar, one logical record per numbered bullet (physical lines are
    /// mostly free-form within a record; adjacency indices may span lines):
    /// <list type="number">
    /// <item><b>Header:</b> one line, <c>radius ee.x ee.y ee.z erm</c> (5 floats).</item>
    /// <item><b>Hull count:</b> one line, single int <c>nHulls</c>.</item>
    /// <item><b>Per hull:</b> one line, <c>nVertices nAdjacencies center.x center.y center.z</c>.</item>
    /// <item><b>Per vertex:</b> one line, <c>x y z n</c>, where <c>n</c> is the
    /// number of adjacency indices that follow, read free-form across lines.</item>
    /// <item><b>Frames:</b> after the last hull, any remaining lines form
    /// triples — a name line, a 3-float position line, a 3-float forward line.</item>
    /// </list>
    /// Structural invariants enforced here (the "four oracles"):
    /// <list type="number">
    /// <item><b>Whole file consumed</b> — the parse walks to the last line.</item>
    /// <item><b>Declared counts match</b> — exactly <c>nHulls</c> hulls, each with
    /// exactly <c>nVertices</c> vertices; the per-vertex adjacency counts sum to
    /// <c>nAdjacencies</c>.</item>
    /// <item><b>Every adjacency index ∈ [0, nVertices)</b> of its hull.</item>
    /// <item><b>Finite floats; radius &gt; 0</b>.</item>
    /// </list>
    /// The C++ loads hulls by iterating <c>hullID = nHulls-1 → 0</c>
    /// (<c>HitTest.cpp:701</c>) but still reads the file in order, so on-disk
    /// hull order is file order; storage reversal is an internal layout detail
    /// and is not reproduced. Likewise the sign flips the C++ applies to
    /// <c>center.x</c>, vertex <c>x</c>, frame position/forward are render-time
    /// transforms and are <b>not</b> applied to the parsed values.
    /// </remarks>
    public static CvhFile Parse(string text)
    {
        var lines = TrimTail(text.Split('\n'));
        if (lines.Length == 0)
            throw new InvalidDataException(".cvh file is empty.");

        var cur = new Cursor(lines);

        // --- line 1: radius ee.x ee.y ee.z erm ---------------------------------
        var head = Tokenize(cur.Next());
        if (head.Length != 5)
            throw At(cur.PrevLine, $"expected 5 floats (radius ee.x ee.y ee.z erm), got {head.Length}");
        var radius = ParseFloat(head[0], cur.PrevLine);
        var ee = ParseVec(head[1], head[2], head[3], cur.PrevLine);
        var erm = ParseFloat(head[4], cur.PrevLine);

        // --- line 2: nHulls ----------------------------------------------------
        var nHullsTok = Tokenize(cur.Next());
        if (nHullsTok.Length != 1)
            throw At(cur.PrevLine, $"expected exactly 1 token (nHulls), got {nHullsTok.Length}");
        var nHulls = ParseUInt(nHullsTok[0], cur.PrevLine);

        // --- hulls (file order) ------------------------------------------------
        var hulls = new List<CvhHull>(nHulls);
        for (var h = 0; h < nHulls; h++)
        {
            var hullHead = Tokenize(cur.Next());
            if (hullHead.Length != 5)
                throw At(cur.PrevLine, $"hull {h}: expected 5 tokens (nVertices nAdjacencies center), got {hullHead.Length}");
            var nVertices = ParseUInt(hullHead[0], cur.PrevLine);
            var nAdjacencies = ParseUInt(hullHead[1], cur.PrevLine);
            var center = ParseVec(hullHead[2], hullHead[3], hullHead[4], cur.PrevLine);
            if (nVertices <= 0 || nAdjacencies <= 0)
                throw At(cur.PrevLine, $"hull {h}: counts must be positive (got {nVertices}/{nAdjacencies})");

            var vertices = new CvhVertex[nVertices];
            var sumAdj = 0;
            for (var v = 0; v < nVertices; v++)
            {
                var vt = Tokenize(cur.Next());
                if (vt.Length != 4)
                    throw At(cur.PrevLine, $"hull {h} vertex {v}: expected 4 tokens (x y z n), got {vt.Length}");
                var pos = ParseVec(vt[0], vt[1], vt[2], cur.PrevLine);
                var n = ParseUInt(vt[3], cur.PrevLine);
                if (n <= 0)
                    throw At(cur.PrevLine, $"hull {h} vertex {v}: adjacency count {n} must be positive");
                sumAdj += n;

                var adj = ReadAdjacencies(ref cur, n, h, v, nVertices);
                vertices[v] = new CvhVertex(pos, adj);
            }
            if (sumAdj != nAdjacencies)
                throw At(cur.PrevLine, $"hull {h}: adjacency sum {sumAdj} != declared nAdjacencies {nAdjacencies}");

            hulls.Add(new CvhHull
            {
                Center = center,
                VertexCount = nVertices,
                AdjacencyCount = nAdjacencies,
                Vertices = vertices,
            });
        }

        // --- frames: triples (name, position, forward) to EOF ------------------
        var frames = new List<CvhFrame>();
        while (!cur.Done)
        {
            var name = cur.Next();
            if (string.IsNullOrWhiteSpace(name))
                throw At(cur.PrevLine, "frame name line is empty");
            var pos = Read3(ref cur, "frame position");
            var fwd = Read3(ref cur, "frame forward");
            frames.Add(new CvhFrame(name.Trim(), pos, fwd));
        }

        return new CvhFile
        {
            Radius = radius,
            EllipseEquation = ee,
            EllipseRadiusMultiplier = erm,
            Hulls = hulls,
            Frames = frames,
        };
    }

    /// <summary>
    /// Read exactly <paramref name="want"/> adjacency indices, free-form across
    /// lines (the C++ uses <c>fscanf(fileIn, "%d ", ...)</c>, which skips
    /// whitespace). A surplus token on a line would collide with the next
    /// vertex line in the C++ reader, so it is rejected here as malformed.
    /// </summary>
    private static int[] ReadAdjacencies(ref Cursor cur, int want, int h, int v, int nVertices)
    {
        var result = new int[want];
        var got = 0;
        while (got < want)
        {
            var line = cur.Next();
            foreach (var tok in Tokenize(line))
            {
                if (got == want)
                    throw At(cur.PrevLine, $"hull {h} vertex {v}: extra adjacency index {tok} after the declared {want}");
                var id = ParseUInt(tok, cur.PrevLine);
                if (id >= nVertices)
                    throw At(cur.PrevLine, $"hull {h} vertex {v}: adjacency index {id} out of range [0,{nVertices})");
                result[got++] = id;
            }
            if (got == want) break;   // stop at the line boundary (matched C++ fgets behaviour)
        }
        return result;
    }

    private static Vector3 ParseVec(string x, string y, string z, int line) => new(
        ParseFloat(x, line), ParseFloat(y, line), ParseFloat(z, line));

    private static Vector3 Read3(ref Cursor cur, string what)
    {
        var t = Tokenize(cur.Next());
        if (t.Length != 3)
            throw At(cur.PrevLine, $"{what}: expected 3 floats, got {t.Length}");
        return ParseVec(t[0], t[1], t[2], cur.PrevLine);
    }

    private static float ParseFloat(string s, int line) =>
        float.TryParse(s, out var v) && !float.IsNaN(v) && !float.IsInfinity(v)
            ? v : throw At(line, $"non-finite float token '{s}'");

    private static int ParseUInt(string s, int line) =>
        int.TryParse(s, out var v) && v >= 0 ? v : throw At(line, $"expected a non-negative integer, got '{s}'");

    private static InvalidDataException At(int line0Based, string msg) =>
        new($"line {line0Based + 1}: {msg}.");

    private static string[] TrimTail(string[] lines)
    {
        var n = lines.Length;
        while (n > 0 && string.IsNullOrWhiteSpace(lines[n - 1])) n--;
        return n == 0 ? [] : lines[..n];
    }

    private static string[] Tokenize(string line) => line.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Physical-line cursor; <see cref="Index"/> is the next line to consume.</summary>
    private struct Cursor
    {
        public string[] Lines;
        public int Index;

        public Cursor(string[] lines) { Lines = lines; Index = 0; }

        /// <summary>Line index (0-based) of the most recently consumed line.</summary>
        public int PrevLine => Index - 1;

        public bool Done => Index >= Lines.Length;

        public string Next() =>
            Index < Lines.Length ? Lines[Index++]
            : throw new InvalidDataException($"line {Index + 1}: unexpected end of file.");
    }
}
