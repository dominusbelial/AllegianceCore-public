using System.Text;
using System.Text.RegularExpressions;

namespace Allegiance.ClientCore.MonoGame.QuickChat;

/// <summary>
/// P217 — a small, pragmatic parser for the quick-chat subset of the original's
/// <c>quickchat.mdl</c> + <c>sounddef.mdl</c> scene-script files. It reads ONLY
/// the handful of constructs this one feature uses — <c>SonicChat</c> /
/// <c>SonicChatVoice</c> declarations, the <c>sonicChats</c> index list,
/// <c>QuickChatCommand</c> declarations, and the nested <c>QuickChatMenu</c> /
/// <c>QuickChatMenuItem</c> tree — plus the <c>SerializedSound(...ImportWave(...))</c>
/// name-to-file mapping from <c>sounddef.mdl</c>. It is deliberately NOT a
/// general <c>.mdl</c> interpreter (the wiki's scope decision confirms .mdl is a
/// whole scripted scene-description language; only the constructs this file
/// actually uses are needed). Same line-oriented/regex technique the port used
/// for P213's engine-sound discovery from <c>sounddef.mdl</c>.
/// </summary>
/// <remarks>
/// <para><b>Parse-at-runtime choice.</b> The parser runs once at client startup
/// over verbatim copies of the two real <c>.mdl</c> files shipped as Content
/// (next to the compiled assets). This keeps the original files the single
/// source of truth (auditable, matches the "copy the real source into Content/"
/// instruction) and avoids a fragile build-time codegen step that would embed a
/// generated table hard to audit against source. The parse is trivially fast
/// (~few thousand lines once) and the resulting in-memory
/// <see cref="QuickChatDatabase"/> serves both the send menu and the receive
/// voice-over resolution.</para>
/// <para><b>Index scheme.</b> The wire <c>voiceOver</c> value is the <b>explicit</b>
/// index from <c>quickchat.mdl</c>'s <c>sonicChats = [ (N, name), … ]</c> list —
/// the same value native's <c>GetSonicChat()-&gt;GetIndex()</c> sends
/// (WinTrek.cpp:991) and the same key its <c>m_vsonicChats[id]</c> is indexed by
/// (WinTrek.cpp:1097-1099, built by <c>DoSetIndex</c> at :1083). The indices are
/// NOT contiguous (holes, e.g. 238 and 242-299 unused, 235 reused) so the parser
/// keys by the explicit number, not by list position. The receiving side
/// resolves a received <c>voiceOver</c> through the same index → entry map.</para>
/// <para><b>Comment handling.</b> <c>//</c> line and trailing comments are
/// stripped (outside string literals); commented-out menu items / list entries
/// are excluded exactly as the original ignores them.</para>
/// </remarks>
public static class QuickChatMdlParser
{
    private static readonly Regex SonicChatDecl =
        new(@"\s*([A-Za-z0-9_]+)\s*=\s*SonicChat\(\s*\[", RegexOptions.Compiled);

    private static readonly Regex SonicChatVoice =
        new(@"SonicChatVoice\(\s*([A-Za-z0-9_]+)\s*,\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    private static readonly Regex SonicChatListPair =
        new(@"\(\s*(\d+)\s*,\s*([A-Za-z0-9_]+)\s*\)", RegexOptions.Compiled);

    private static readonly Regex CommandDecl =
        new(@"\s*([A-Za-z0-9_]+)\s*=\s*QuickChatCommand\(", RegexOptions.Compiled);

    /// <summary>A plain scalar constant declaration (<c>ttFriendly = 0x0001;</c>,
    /// <c>cidAttack = 1;</c>, <c>cidNone = -1;</c>, <c>habmLifepod = 0x04;</c>) —
    /// the <c>cid*</c>/<c>tt*</c>/<c>habm*</c> symbol table the quick-chat
    /// command block references. Matches ONLY single-value scalar assignments,
    /// so it never collides with a <c>QuickChatCommand(...)</c> or a composite
    /// alias like <c>ttShipTypes = ttShip | ttStation;</c> (the latter are not
    /// referenced by any shipped command's mask, so skipping them is safe).</summary>
    private static readonly Regex ConstDecl =
        new(@"^\s*([A-Za-z0-9_]+)\s*=\s*(-?(?:0[xX][0-9A-Fa-f]+|\d+))\s*;", RegexOptions.Compiled);

    private static readonly Regex MenuItemDecl =
        new(@"QuickChatMenuItem\(\s*'(.)'\s*,\s*""((?:[^""\\]|\\.)*)""\s*,\s*([A-Za-z0-9_]+)\s*\)", RegexOptions.Compiled);

    private static readonly Regex SerializedSoundDecl =
        new(@"([A-Za-z0-9_]+)\s*=\s*SerializedSound\(([^;]*?)\)\s*;", RegexOptions.Compiled);

    private static readonly Regex ImportWave =
        new(@"ImportWave\(\s*""([^""]+)""", RegexOptions.Compiled);

    /// <summary>Parse the two real data files into a usable quick-chat database.</summary>
    /// <param name="quickchatMdl">Full text of <c>quickchat.mdl</c>.</param>
    /// <param name="sounddefMdl">Full text of <c>sounddef.mdl</c>.</param>
    public static QuickChatDatabase Parse(string quickchatMdl, string sounddefMdl)
    {
        var qcLines = LineSplit(quickchatMdl);
        var soundDefs = ParseSoundStems(sounddefMdl);

        // name -> (soundIdent, caption)
        var sonicChats = ParseSonicChats(qcLines);
        // index -> name (the wire voiceOver value -> chat)
        var indexList = ParseSonicChatIndexList(qcLines);
        // the mdl's own cid*/tt*/habm* scalar constants (resolved, not copied)
        var constants = ParseConstants(qcLines);
        // command name -> (sonicChatName, cid, ttMask, abilityMask) — symbols
        var commands = ParseCommands(qcLines);
        // menu name -> [(key, caption, target-ref)]
        var menus = ParseMenus(qcLines);

        var entriesByIndex = new Dictionary<int, QuickChatEntry>();
        var entriesByName = new Dictionary<string, QuickChatEntry>();
        foreach (var (index, name) in indexList)
        {
            if (!sonicChats.TryGetValue(name, out var sc))
                continue;   // a dangling list ref — ignore, no entry
            string stem = sc.SoundIdent.Length > 0
                && soundDefs.TryGetValue(sc.SoundIdent, out var s) ? s : "";
            var entry = new QuickChatEntry
            {
                Index = index,
                Caption = sc.Caption,
                SoundStem = stem,
            };
            entriesByIndex[index] = entry;
            entriesByName[name] = entry;
        }

        var menuNodes = new Dictionary<string, QuickChatMenu>();
        foreach (var name in menus.Keys)
            menuNodes[name] = new QuickChatMenu { Name = name };

        foreach (var (menuName, items) in menus)
        {
            var node = menuNodes[menuName];
            foreach (var (key, caption, targetRef) in items)
            {
                var choice = new QuickChatMenuChoice { Key = key, Label = caption };
                if (menuNodes.TryGetValue(targetRef, out var submenu))
                {
                    choice.Submenu = submenu;
                }
                else if (commands.TryGetValue(targetRef, out var cmd)
                         && entriesByName.TryGetValue(cmd.SonicChatName, out var entry))
                {
                    choice.Send = new QuickChatSendAction
                    {
                        Entry = entry,
                        Target = QuickChatTarget.Resolve(cmd.TargetMaskExpr),
                        CommandId = (short)ResolveSymbol(cmd.CidExpr, constants),
                        TargetTypeMask = EvaluateExpression(cmd.TargetMaskExpr, constants),
                        AbilityMask = EvaluateExpression(cmd.AbilityExpr, constants),
                    };
                }
                // An unmatched reference (neither a known menu nor a known,
                // resolvable command) is dropped — never a crash.
                node.Choices.Add(choice);
            }
        }

        return new QuickChatDatabase
        {
            EntriesByIndex = entriesByIndex,
            MenusByName = menuNodes,
            MainMenu = menuNodes.TryGetValue("mainMenu", out var mm) ? mm : null,
        };
    }

    private static string[] LineSplit(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>Strip <c>//</c> line comments while leaving string literals intact.</summary>
    private static string StripComments(string line)
    {
        var sb = new StringBuilder(line.Length);
        bool inStr = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                inStr = !inStr;
                sb.Append(c);
                continue;
            }
            if (!inStr && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Split a <c>fun(arg1, arg2, …)</c> arg list on top-level commas
    /// (paren-depth 0), trimming each part.</summary>
    private static string[] SplitTopLevel(string s)
    {
        var parts = new List<string>();
        int depth = 0;
        var cur = new StringBuilder();
        foreach (var c in s)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            if (c == ',' && depth == 0)
            {
                parts.Add(cur.ToString().Trim());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }
        if (cur.Length > 0) parts.Add(cur.ToString().Trim());
        return parts.ToArray();
    }

    /// <summary>Parse <c>name = SonicChat([ SonicChatVoice(soundIdent, "cap") ]);</c>
    /// declarations (first voice variant; all real entries have exactly one).</summary>
    private static Dictionary<string, (string SoundIdent, string Caption)> ParseSonicChats(string[] lines)
    {
        var result = new Dictionary<string, (string, string)>();
        foreach (var raw in lines)
        {
            var line = StripComments(raw);
            var m = SonicChatDecl.Match(line);
            if (!m.Success) continue;
            string name = m.Groups[1].Value;
            int openBracket = line.IndexOf('[');
            int closeBracket = line.LastIndexOf(']');
            if (openBracket < 0 || closeBracket <= openBracket) continue;
            var body = line.Substring(openBracket + 1, closeBracket - openBracket - 1);
            var v = SonicChatVoice.Match(body);
            if (!v.Success) continue;
            result[name] = (v.Groups[1].Value, UnescapeCaption(v.Groups[2].Value));
        }
        return result;
    }

    /// <summary>Parse the <c>sonicChats = [ (0, name), (1, name), … ];</c> index
    /// list (the wire voiceOver number → sonic chat symbol), excluding commented
    /// entries.</summary>
    private static List<(int Index, string Name)> ParseSonicChatIndexList(string[] lines)
    {
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (StripComments(lines[i]).TrimStart().StartsWith("sonicChats", StringComparison.Ordinal))
            {
                start = i;
                break;
            }
        }
        var result = new List<(int, string)>();
        if (start < 0) return result;
        var sb = new StringBuilder();
        for (int i = start; i < lines.Length; i++)
        {
            sb.Append(' ').Append(StripComments(lines[i]));
            if (sb.ToString().Contains("];", StringComparison.Ordinal))
                break;
        }
        foreach (Match m in SonicChatListPair.Matches(sb.ToString()))
            result.Add((int.Parse(m.Groups[1].Value), m.Groups[2].Value));
        return result;
    }

    /// <summary>Parse <c>name = QuickChatCommand(sonicChat, cid, ttMask, abilityMask);</c>
    /// — keeps the sonic-chat symbol plus the (still-symbolic) cid, target-mask
    /// and ability-mask expressions. The symbols are resolved against the
    /// <c>cid*</c>/<c>tt*</c>/<c>habm*</c> constants by
    /// <see cref="ResolveSymbol"/>/<see cref="EvaluateExpression"/> in
    /// <see cref="Parse"/>, not hand-copied numbers.</summary>
    private static Dictionary<string, (string SonicChatName, string CidExpr, string TargetMaskExpr, string AbilityExpr)> ParseCommands(string[] lines)
    {
        var result = new Dictionary<string, (string, string, string, string)>();
        foreach (var raw in lines)
        {
            var line = StripComments(raw);
            var m = CommandDecl.Match(line);
            if (!m.Success) continue;
            string name = m.Groups[1].Value;
            int open = line.IndexOf('(');
            int close = line.LastIndexOf(')');
            if (open < 0 || close <= open) continue;
            var args = SplitTopLevel(line.Substring(open + 1, close - open - 1));
            if (args.Length < 4) continue;
            result[name] = (args[0], args[1], args[2], args[3]);
        }
        return result;
    }

    /// <summary>Parse the mdl's scalar constant table (<c>ttFriendly = 0x0001;</c>,
    /// <c>cidAttack = 1;</c>, <c>cidNone = -1;</c>, …) into name → value. These are
    /// the single source of truth for the quick-chat command block's cid/tt/habm
    /// symbols — resolving from the file's own declarations, never hand-copied.</summary>
    private static Dictionary<string, long> ParseConstants(string[] lines)
    {
        var result = new Dictionary<string, long>();
        foreach (var raw in lines)
        {
            var line = StripComments(raw);
            var m = ConstDecl.Match(line);
            if (!m.Success) continue;
            string num = m.Groups[2].Value;
            bool neg = num.StartsWith('-');
            string mag = neg ? num.Substring(1) : num;
            long val = mag.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt64(mag.Substring(2), 16)
                : long.Parse(mag);
            result[m.Groups[1].Value] = neg ? -val : val;
        }
        return result;
    }

    /// <summary>Resolve a single symbol (or bare literal) to a value from the
    /// constant table. Unknown symbols resolve to <c>0</c> — the parser is
    /// deliberately lenient so an undeclared symbol degrades to \"no flag\"
    /// rather than throwing on a real-data oddity.</summary>
    private static long ResolveSymbol(string tok, IReadOnlyDictionary<string, long> constants)
    {
        tok = tok.Trim();
        if (tok.Length == 0) return 0;
        if (long.TryParse(tok, out var n)) return n;
        return constants.TryGetValue(tok, out var v) ? v : 0;
    }

    /// <summary>Evaluate a bitwise-OR expression of symbols/literals (e.g.
    /// <c>ttFriendly | ttStation | ttNearest</c>) against the constant table.</summary>
    private static long EvaluateExpression(string expr, IReadOnlyDictionary<string, long> constants)
    {
        long acc = 0;
        foreach (var part in expr.Split('|'))
        {
            var tok = part.Trim();
            if (tok.Length == 0) continue;
            acc |= ResolveSymbol(tok, constants);
        }
        return acc;
    }

    /// <summary>Parse the nested <c>name =\n QuickChatMenu([ QuickChatMenuItem('k', "cap", target), … ]);</c>
    /// tree, linking each item's <c>node</c> reference (a menu name or a command name).</summary>
    private static Dictionary<string, List<(char Key, string Caption, string TargetRef)>> ParseMenus(string[] lines)
    {
        var result = new Dictionary<string, List<(char, string, string)>>();
        for (int li = 0; li < lines.Length; li++)
        {
            var line = StripComments(lines[li]);
            if (!line.Contains("QuickChatMenu([", StringComparison.Ordinal))
                continue;

            // Body: everything after '[' on the opening line through the ']);' close.
            var body = new StringBuilder();
            int idx = li;
            int openBracket = line.IndexOf('[');
            if (openBracket >= 0)
                body.Append(line.Substring(openBracket + 1));
            while (body.ToString().Contains("]);", StringComparison.Ordinal) == false && idx + 1 < lines.Length)
            {
                idx++;
                body.Append(' ').Append(StripComments(lines[idx]));
            }
            var full = body.ToString();
            int closeMarker = full.IndexOf("]);", StringComparison.Ordinal);
            if (closeMarker < 0) continue;
            full = full.Substring(0, closeMarker);

            // The menu symbol is assigned on the line(s) above the QuickChatMenu open.
            string name = ResolveSymbolBefore(lines, li);
            if (name.Length == 0) continue;

            var items = new List<(char, string, string)>();
            foreach (Match im in MenuItemDecl.Matches(full))
                items.Add((im.Groups[1].Value[0], UnescapeCaption(im.Groups[2].Value), im.Groups[3].Value));
            result[name] = items;
        }
        return result;
    }

    /// <summary>Walk upward from <paramref name="openLineIdx"/> to find the
    /// nearest preceding <c>symbol =</c> assignment (the menu's name), ignoring
    /// blank/comment lines and stopping at any other non-blank line.</summary>
    private static string ResolveSymbolBefore(string[] lines, int openLineIdx)
    {
        for (int j = openLineIdx - 1; j >= 0; j--)
        {
            var prev = StripComments(lines[j]).Trim();
            if (prev.Length == 0) continue;
            var m = Regex.Match(prev, @"^([A-Za-z0-9_]+)\s*=");
            if (m.Success) return m.Groups[1].Value;
            break;   // some other statement — not the assignment
        }
        return "";
    }

    /// <summary>Parse <c>ident = SerializedSound(…ImportWave("stem")…);</c> from
    /// <c>sounddef.mdl</c> → sound identifier to the real .ogg asset stem.</summary>
    private static Dictionary<string, string> ParseSoundStems(string sounddefMdl)
    {
        var result = new Dictionary<string, string>();
        foreach (Match m in SerializedSoundDecl.Matches(sounddefMdl))
        {
            var iw = ImportWave.Match(m.Groups[2].Value);
            if (iw.Success)
                result[m.Groups[1].Value] = iw.Groups[1].Value;
        }
        return result;
    }

    /// <summary>Turn an mdl string-literal escape back into the literal char
    /// (the parser only ever sees \' / \\ ; real captions rarely use more).</summary>
    private static string UnescapeCaption(string s) =>
        s.Replace("\\'", "'").Replace("\\\\", "\\");
}
