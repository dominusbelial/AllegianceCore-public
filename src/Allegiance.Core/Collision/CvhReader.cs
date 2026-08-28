namespace Allegiance.Core.Collision;

/// <summary>
/// Reads a <c>.cvh</c> collision-hull file. Text format, mirroring the C++
/// loader <c>HitTest::Load</c> (<c>src/_Utility/HitTest.cpp:676-807</c>): the
/// first physical line is the header (5 floats), the second is the hull count.
/// Physical line breaks are significant only where noted; within a logical
/// record the C++ reads whitespace-delimited tokens, and adjacency indices in
/// particular may span lines freely.
/// </summary>
public static class CvhReader
{
    /// <summary>
    /// Parse a whole <c>.cvh</c> file. Throws <see cref="InvalidDataException"/>
    /// on any grammar violation; the file's own declared counts are the oracle
    /// and are always enforced, so a well-formed file leaves no line unconsumed.
    /// </summary>
    public static CvhFile Parse(string text)
    {
        var lines = TrimTail(text.Split('\n'));
        if (lines.Length == 0)
            throw new InvalidDataException(".cvh file is empty.");

        var cur = new Cursor(lines);

        // Line 1: radius ee.x ee.y ee.z erm  (5 floats)
        var head = Tokenize(cur.Next());
        if (head.Length != 5)
            throw HeaderError(cur.PrevLine, "expected 5 floats (radius ee.x ee.y ee.z erm)");

        // Line 2: nHulls (single int)
        var nHullsTok = Tokenize(cur.Next());
        if (nHullsTok.Length != 1)
            throw HeaderError(cur.PrevLine, $"expected exactly 1 token (nHulls), got {nHullsTok.Length}");
        var nHulls = ParseUInt(nHullsTok[0]);

        var (radius, ee, erm) = (float.Parse(head[0]), ParseVec(head[1], head[2], head[3]), float.Parse(head[4]));

        return new CvhFile
        {
            Radius = radius,
            EllipseEquation = ee,
            EllipseRadiusMultiplier = erm,
            Hulls = [],
            Frames = [],
        };
    }

    private static string[] TrimTail(string[] lines)
    {
        var n = lines.Length;
        while (n > 0 && string.IsNullOrWhiteSpace(lines[n - 1])) n--;
        return n == 0 ? [] : lines[..n];
    }

    private static InvalidDataException HeaderError(int atLine0Based, string msg) =>
        new($"line {atLine0Based + 1}: {msg}.");

    private static string[] Tokenize(string line) => line.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries);

    private static Vector3 ParseVec(string x, string y, string z) => new(
        float.Parse(x), float.Parse(y), float.Parse(z));

    private static int ParseUInt(string s) => int.TryParse(s, out var v) && v >= 0
        ? v : throw new InvalidDataException($"expected a non-negative integer, got '{s}'.");

    /// <summary>Physical-line cursor; <see cref="Index"/> is the next line to consume.</summary>
    private struct Cursor
    {
        public string[] Lines;
        public int Index;

        public Cursor(string[] lines) { Lines = lines; Index = 0; }

        /// <summary>Line index of the most recently consumed line.</summary>
        public int PrevLine => Index - 1;

        public string Next() =>
            Index < Lines.Length ? Lines[Index++]
            : throw new InvalidDataException($"line {Index + 1}: unexpected end of file.");
    }
}
