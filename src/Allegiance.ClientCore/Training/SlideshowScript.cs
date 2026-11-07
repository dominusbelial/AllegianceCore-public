using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// A 2-D image expression from a training slideshow MDL (<c>use "SlideshowData"</c>
/// files such as <c>tm_1_introduction.mdl</c>). Only the operator subset the
/// slideshows actually use is modelled — <c>ImportImage</c>, <c>GroupImage</c>,
/// <c>TranslateImage</c>, <c>StringImage</c> and <c>Point</c> (P943 inventory).
/// Native implements these in <c>engine/image.cpp</c>; the evaluator is
/// <see cref="SlideshowLayout"/>.
/// </summary>
public abstract record SlideshowImage
{
    /// <summary>An expression that resolved to nothing (a dangling identifier).</summary>
    public sealed record None : SlideshowImage
    {
        public static readonly None Instance = new();
        private None() { }
    }

    /// <summary>A full-screen/overlay bitmap. Native <c>ImportImage(</c>stem<c>,</c>
    /// colorKey<c>)</c> (<c>engine/modeler.cpp:691-706</c>); <c>bColorKey</c> goes to
    /// <c>LoadImage</c>'s <c>SetColorKeyHint</c> (<c>engine/modeler.cpp:2463-2492</c>).</summary>
    public sealed record Import(string Stem, bool ColorKey) : SlideshowImage;

    /// <summary>A list of images. Native <c>GroupImage([…])</c> paints its list in
    /// reverse — the FIRST child is drawn last, on top
    /// (<c>engine/modeler.cpp:1544-1562</c> + <c>engine/image.cpp:918-921</c>,
    /// <c>:954-976</c>).</summary>
    public sealed record Group(IReadOnlyList<SlideshowImage> Items) : SlideshowImage;

    /// <summary>Native <c>TranslateImage(image, Point(x, y))</c> — the child's
    /// origin moves to the point (<c>engine/image.cpp:828-845</c>).</summary>
    public sealed record Translate(SlideshowImage Child, int X, int Y) : SlideshowImage;

    /// <summary>Native <c>StringImage(JustifyLeft, width, largeFont, white, text)</c>:
    /// the text block hangs BELOW its point (bounds <c>Rect(0, -ysize, width, 0)</c>,
    /// <c>engine/image.cpp:337-355</c>) and wraps at <paramref name="Width"/>
    /// (<c>engine/image.cpp:357-400</c>).</summary>
    public sealed record Text(string Value, int Width, string Font, string Color, string Justify) : SlideshowImage;

    /// <summary>The first <c>StringImage</c> text inside the expression (in paint
    /// order), or null. Text slides carry their narration in the art itself.</summary>
    public static string? FirstText(SlideshowImage? image) => image switch
    {
        Text t => t.Value,
        Group g => g.Items.Select(i => FirstText(i)).FirstOrDefault(s => s is not null),
        Translate tr => FirstText(tr.Child),
        _ => null,
    };
}

/// <summary>
/// P943 — a parsed training slideshow MDL: the native <c>slides</c> list of
/// (image, SoundID) pairs (WinTrek/Slideshow.cpp:92-105) plus the narration text
/// the artwork carries in the comments above each slide. Native loads the list
/// with the modeler's MDL interpreter (<c>Slideshow::Slideshow</c>,
/// <c>WinTrek/Slideshow.cpp:33-62</c>); this is a small, tolerant reader for the
/// subset those files use, so the port can draw the real slide artwork instead of
/// a textual stand-in.
/// </summary>
public sealed class SlideshowScript
{
    /// <summary>One native slide: the image expression, the sound clip stem and the
    /// narration comment block (empty when the file has none).</summary>
    public sealed record Slide(SlideshowImage Image, string SoundStem, string Narration);

    public string Namespace { get; }
    public IReadOnlyList<Slide> Slides { get; }

    /// <summary>Every bitmap stem the slide expressions import, in paint order.</summary>
    public IReadOnlyList<string> ImportStems { get; }

    private SlideshowScript(string ns, IReadOnlyList<Slide> slides, IReadOnlyList<string> imports)
    {
        Namespace = ns;
        Slides = slides;
        ImportStems = imports;
    }

    /// <summary>
    /// Parse a slideshow MDL's text. Returns null when the file declares no
    /// <c>slides</c> list at all (native would cast null and crash — the port must
    /// degrade to the caption table instead). Malformed expressions inside an
    /// otherwise valid file degrade to <see cref="SlideshowImage.None"/>, never throw.
    /// </summary>
    public static SlideshowScript? TryParse(string mdlText, string ns)
    {
        if (string.IsNullOrWhiteSpace(mdlText)) return null;
        var narrations = CollectNarrations(mdlText);
        var parser = new Parser(mdlText);
        var slidesValue = parser.Parse(out var error);
        if (slidesValue is not ListValue list || error is not null) return null;

        var pairs = new List<(SlideshowImage Image, string SoundStem)>();
        foreach (var item in list.Items)
        {
            if (item is not PairValue pair) continue;
            var image = parser.ResolveImage(pair.First);
            string stem = pair.Second switch
            {
                SymbolValue s => SoundStemOf(s.Name),
                StringValue str => str.Value,
                _ => "",
            };
            pairs.Add((image, stem));
        }
        if (pairs.Count == 0) return null;

        var slides = new List<Slide>(pairs.Count);
        for (int i = 0; i < pairs.Count; i++)
            slides.Add(new Slide(pairs[i].Image, pairs[i].SoundStem, NarrationFor(narrations, i, pairs[i].SoundStem, pairs.Count)));

        var imports = new List<string>();
        foreach (var s in slides)
            foreach (var stem in ImportsOf(s.Image))
                if (!imports.Contains(stem)) imports.Add(stem);
        return new SlideshowScript(ns, slides, imports);
    }

    /// <summary>Native sound identifiers are <c>&lt;clip&gt;SoundId</c> symbols
    /// (<c>use "sounddef";</c>); the port plays the installed clip by stem.</summary>
    private static string SoundStemOf(string symbol) =>
        symbol.EndsWith("SoundId", StringComparison.Ordinal)
            ? symbol[..^"SoundId".Length]
            : symbol;

    private static IEnumerable<string> ImportsOf(SlideshowImage image) => image switch
    {
        SlideshowImage.Import i => new[] { i.Stem },
        SlideshowImage.Group g => g.Items.SelectMany(ImportsOf),
        SlideshowImage.Translate t => ImportsOf(t.Child),
        _ => Array.Empty<string>(),
    };

    // ── narration comment blocks ────────────────────────────────────────────
    // The artwork carries each slide's narration as a `//` block whose first line
    // is the slide label (`// tm_slide_1_01`, `// tm_2_01`, `// tm_4_slide_01`),
    // followed by the wrapped sentence(s) — e.g. tm_1_introduction.mdl:58-60.

    private static List<(string Label, string Text)> CollectNarrations(string text)
    {
        var blocks = new List<(string Label, string Text)>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            if (!IsComment(lines[i]))
            {
                i++;
                continue;
            }
            var block = new List<string>();
            while (i < lines.Length && IsComment(lines[i]))
            {
                block.Add(lines[i].TrimStart()[2..].Trim());
                i++;
            }
            if (block.Count == 0 || !SlideLabel.IsMatch(block[0])) continue;
            var sb = new StringBuilder();
            foreach (var line in block.Skip(1))
            {
                if (line.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(line);
            }
            blocks.Add((block[0], Sanitize(sb.ToString())));
        }
        return blocks;
    }

    private static bool IsComment(string line) =>
        line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    private static readonly Regex SlideLabel =
        new(@"^tm_[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>Assign the narration block to a slide: by position when the file's
    /// blocks and slides line up (all corpus slideshows), else by matching the digit
    /// signature of the label and the sound stem (<c>tm_slide_4_01</c> ≡
    /// <c>tm_4_slide_01</c>, <c>tm_2_01</c> ≡ <c>tm_slide_2_01</c>).</summary>
    private static string NarrationFor(List<(string Label, string Text)> narrations, int index,
        string stem, int count)
    {
        if (narrations.Count == count) return narrations[index].Text;
        string key = DigitKey(stem);
        for (int i = 0; i < narrations.Count; i++)
            if (DigitKey(narrations[i].Label) == key) return narrations[i].Text;
        return "";
    }

    private static string DigitKey(string s)
    {
        var digits = Regex.Matches(s, @"\d+");
        return digits.Count == 0 ? "" : string.Join(".", digits.Select(m => m.Value));
    }

    /// <summary>The artwork's text carries Windows-1252 apostrophes that a UTF-8 read
    /// turns into replacement characters; the HUD font covers ASCII only, so every
    /// non-ASCII character becomes '?' (the same convention TrainingSlideData uses).
    /// Repairing the corpus text is out of scope.</summary>
    internal static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c == '\n' || c is >= ' ' and <= '~' ? c : '?');
        return sb.ToString();
    }

    // ── values ──────────────────────────────────────────────────────────────

    private abstract record MdlValue;
    private sealed record NumberValue(int Number) : MdlValue;
    private sealed record StringValue(string Value) : MdlValue;
    private sealed record SymbolValue(string Name) : MdlValue;
    private sealed record PointValue(int X, int Y) : MdlValue;
    private sealed record BooleanValue(bool Value) : MdlValue;
    private sealed record ListValue(IReadOnlyList<MdlValue> Items) : MdlValue;
    private sealed record PairValue(MdlValue First, MdlValue Second) : MdlValue;
    private sealed record ImageValue(SlideshowImage Image) : MdlValue;

    /// <summary>Recursive-descent reader for the statements the slideshow files use:
    /// <c>use "ns";</c>, <c>name = expr;</c> (scalars, points, lists, images) and the
    /// trailing <c>slides = [ (image, SoundId), … ];</c>.</summary>
    private sealed class Parser
    {
        private readonly List<(TokenKind Kind, string Text)> _tokens = new();
        private int _pos;
        private readonly Dictionary<string, MdlValue> _symbols = new(StringComparer.Ordinal);

        public Parser(string text)
        {
            Tokenize(text);
            ParseStatements();
        }

        /// <summary>The parsed <c>slides</c> list, or null when the file has none.</summary>
        public MdlValue? Parse(out string? error)
        {
            error = null;
            if (!_symbols.TryGetValue("slides", out var slides))
            {
                error = "no slides list";
                return null;
            }
            return slides;
        }

        /// <summary>Resolve an image expression, following identifier references and
        /// treating anything unrecognised as <see cref="SlideshowImage.None"/>.</summary>
        public SlideshowImage ResolveImage(MdlValue value) => ResolveValue(value) switch
        {
            ImageValue image => image.Image,
            _ => SlideshowImage.None.Instance,
        };

        private MdlValue ResolveValue(MdlValue value, int depth = 0) =>
            value is SymbolValue symbol && depth < 32 && _symbols.TryGetValue(symbol.Name, out var target)
                ? ResolveValue(target, depth + 1)
                : value;

        private void ParseStatements()
        {
            while (_pos < _tokens.Count)
            {
                if (PeekText() == "use")
                {
                    SkipStatement();
                    continue;
                }
                if (PeekKind() != TokenKind.Identifier || PeekText(1) != "=")
                {
                    _pos++;
                    continue;
                }
                string name = Advance().Text;
                _pos++;   // '='
                var value = ParseExpression();
                if (PeekText() == ";") _pos++;
                _symbols[name] = value;
            }
        }

        private void SkipStatement()
        {
            while (_pos < _tokens.Count && Advance().Text != ";") { }
        }

        private MdlValue ParseExpression()
        {
            if (_pos >= _tokens.Count) return new NumberValue(0);
            var token = Advance();
            switch (token.Kind)
            {
                case TokenKind.String:
                    return new StringValue(Sanitize(Unescape(token.Text)));
                case TokenKind.Number:
                    return new NumberValue(int.Parse(token.Text, CultureInfo.InvariantCulture));
                case TokenKind.Punctuation when token.Text == "-":
                    return ParseExpression() is NumberValue n ? new NumberValue(-n.Number) : new NumberValue(0);
                case TokenKind.Punctuation when token.Text == "(":
                {
                    var first = ParseExpression();
                    if (PeekText() == ",") _pos++;
                    var second = ParseExpression();
                    if (PeekText() == ")") _pos++;
                    return new PairValue(first, second);
                }
                case TokenKind.Punctuation when token.Text == "[":
                {
                    var items = new List<MdlValue>();
                    while (_pos < _tokens.Count && PeekText() != "]")
                    {
                        items.Add(ParseExpression());
                        if (PeekText() == ",") _pos++;
                    }
                    if (PeekText() == "]") _pos++;
                    return new ListValue(items);
                }
                case TokenKind.Identifier:
                    return ParseIdentifier(token.Text);
                default:
                    return new NumberValue(0);
            }
        }

        private MdlValue ParseIdentifier(string name)
        {
            if (PeekText() != "(")
            {
                return name switch
                {
                    "true" => new BooleanValue(true),
                    "false" => new BooleanValue(false),
                    _ => new SymbolValue(name),
                };
            }
            _pos++;   // '('
            var args = new List<MdlValue>();
            while (_pos < _tokens.Count && PeekText() != ")")
            {
                args.Add(ParseExpression());
                if (PeekText() == ",") _pos++;
            }
            if (PeekText() == ")") _pos++;
            return Apply(name, args);
        }

        private MdlValue Apply(string name, List<MdlValue> args)
        {
            // Follow identifier references first (Point constants such as
            // tm_1_introduction.mdl's slide_text_position, image aliases such as
            // hangar_background, and text constants such as slide_text_1).
            args = args.Select(ResolveValue).ToList();
            switch (name.ToLowerInvariant())
            {
                case "importimage":
                {
                    string stem = args.Count > 0 && args[0] is StringValue s ? s.Value : "";
                    bool colorKey = args.Count > 1 && args[1] is BooleanValue b && b.Value;
                    return new ImageValue(stem.Length == 0
                        ? SlideshowImage.None.Instance
                        : new SlideshowImage.Import(stem, colorKey));
                }
                case "groupimage":
                {
                    var items = args.Count > 0 && args[0] is ListValue list
                        ? list.Items.Select(ResolveImage).ToArray()
                        : Array.Empty<SlideshowImage>();
                    return new ImageValue(new SlideshowImage.Group(items));
                }
                case "translateimage":
                {
                    if (args.Count < 2 || args[1] is not PointValue p) return new ImageValue(SlideshowImage.None.Instance);
                    return new ImageValue(new SlideshowImage.Translate(ResolveImage(args[0]), p.X, p.Y));
                }
                case "stringimage":
                {
                    if (args.Count < 5) return new ImageValue(SlideshowImage.None.Instance);
                    string justify = args[0] is SymbolValue j ? j.Name : "JustifyLeft";
                    int width = args[1] is NumberValue w ? w.Number : 0;
                    string font = args[2] is SymbolValue f ? f.Name : "";
                    string color = args[3] is SymbolValue c ? c.Name : "white";
                    string text = args[4] switch
                    {
                        StringValue sv => sv.Value,
                        SymbolValue sym when _symbols.TryGetValue(sym.Name, out var v) && v is StringValue sv2 => sv2.Value,
                        _ => "",
                    };
                    return new ImageValue(new SlideshowImage.Text(text, width, font, color, justify));
                }
                case "point":
                {
                    int x = args.Count > 0 && args[0] is NumberValue nx ? nx.Number : 0;
                    int y = args.Count > 1 && args[1] is NumberValue ny ? ny.Number : 0;
                    return new PointValue(x, y);
                }
                default:
                    return new ImageValue(SlideshowImage.None.Instance);
            }
        }

        private void Tokenize(string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                    i = Math.Min(text.Length, i + 2);
                    continue;
                }
                if (c == '"')
                {
                    var sb = new StringBuilder();
                    i++;
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length)
                        {
                            sb.Append('\\').Append(text[i + 1]);
                            i += 2;
                            continue;
                        }
                        sb.Append(text[i]);
                        i++;
                    }
                    i++;   // closing quote
                    _tokens.Add((TokenKind.String, sb.ToString()));
                    continue;
                }
                if (char.IsDigit(c))
                {
                    int start = i;
                    while (i < text.Length && char.IsDigit(text[i])) i++;
                    _tokens.Add((TokenKind.Number, text[start..i]));
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    _tokens.Add((TokenKind.Identifier, text[start..i]));
                    continue;
                }
                if (c is '=' or '(' or ')' or '[' or ']' or ',' or ';' or '-' or '+')
                {
                    _tokens.Add((TokenKind.Punctuation, c.ToString()));
                    i++;
                    continue;
                }
                i++;   // stray byte
            }
        }

        /// <summary>MDL string escapes: <c>\\n</c>, <c>\\"</c> and <c>\\\\</c>.</summary>
        private static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char n = s[++i];
                sb.Append(n switch { 'n' => '\n', '"' => '"', '\\' => '\\', _ => n });
            }
            return sb.ToString();
        }

        private TokenKind PeekKind(int ahead = 0) =>
            _pos + ahead < _tokens.Count ? _tokens[_pos + ahead].Kind : TokenKind.End;

        private string PeekText(int ahead = 0) =>
            _pos + ahead < _tokens.Count ? _tokens[_pos + ahead].Text : "";

        private (TokenKind Kind, string Text) Advance()
        {
            var t = _tokens[_pos];
            _pos++;
            return t;
        }
    }

    private enum TokenKind { Identifier, Number, String, Punctuation, End }
}
