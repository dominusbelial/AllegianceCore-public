using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P759b — the strings of one <c>missionbrief.mdl</c> file (artwork corpus), the
/// data native's briefing popups read through the modeler namespace
/// (<c>WinTrek/teamscreen.cpp:788-826</c> resolves the <c>missionbrief</c>
/// namespace and pulls <c>Title</c>/<c>String</c>/civ briefing keys/<c>TipN</c>
/// from it). The file is Latin-1 text of <c>name = "…";</c> assignments; the
/// loader below reproduces the mdl string-reader behaviour the brief pins:
/// <c>numTips = "40"</c> and <c>Tip1..Tip40</c> with commented <c>//Tip</c> lines
/// skipped (the mdl loader never sees comments, teamscreen.cpp:820-826
/// <c>GetMissionTip</c> indexes <c>rand() % numTips + 1</c>).
/// </summary>
public sealed class MissionBriefingStrings
{
    private const int NumTipsKey = 40;   // mdl convention pinned by the file itself

    private readonly Dictionary<string, string> _strings;

    public int NumTips { get; }

    private MissionBriefingStrings(Dictionary<string, string> strings, int numTips)
    {
        _strings = strings;
        NumTips = numTips;
    }

    /// <summary>Native <c>GetString</c>/<c>GetCivString</c>
    /// (<c>teamscreen.cpp:621-639</c>): look the key up; <see cref="GetCivString"/>
    /// concatenates the civilization's icon name before the key.</summary>
    public string GetString(string name) =>
        _strings.TryGetValue(name, out var v) ? v : "";

    public string GetCivString(string civPrefix, string name) =>
        GetString(civPrefix + name);

    /// <summary>The parsed, comment-free tip pool: <c>Tip1..TipNumTips</c> in
    /// file order. Native does not expose this, but <c>GetMissionTip</c>'s
    /// <c>rand() % numTips + 1</c> pick draws exactly from these keys.</summary>
    public IReadOnlyList<string> Tips =>
        Enumerable.Range(1, NumTips).Select(i => GetString($"Tip{i}")).ToArray();

    /// <summary>Parse one <c>missionbrief.mdl</c> (already Latin-1 decoded to
    /// text). Skips blank lines, <c>//</c> comment lines (including the
    /// commented <c>//Tip11</c> duplicate the file carries) and non-string
    /// assignments (pane/image expressions), and unescapes <c>\\n</c>,
    /// <c>\\"</c>, <c>\\t</c> and <c>\\\\</c> inside the quoted values.</summary>
    public static MissionBriefingStrings Parse(string mdlText)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        int numTips = 0;
        foreach (var rawLine in mdlText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').TrimStart();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string name = line[..eq].Trim();
            string rest = line[(eq + 1)..].TrimStart();
            if (rest.Length == 0 || rest[0] != '"') continue;   // pane/image expressions

            // Scan the closing quote honouring backslash escapes, then require
            // the terminating `;` (trailing // comments may follow it).
            var sb = new StringBuilder(rest.Length);
            int i = 1;
            bool closed = false;
            while (i < rest.Length)
            {
                char c = rest[i];
                if (c == '\\' && i + 1 < rest.Length)
                {
                    char e = rest[++i];
                    sb.Append(e switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        _ => e,
                    });
                }
                else if (c == '"')
                {
                    closed = true;
                    break;
                }
                else sb.Append(c);
                i++;
            }
            if (!closed) continue;

            strings[name] = sb.ToString();
            if (name == "numTips" &&
                int.TryParse(sb.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                numTips = n;
        }
        if (numTips <= 0) numTips = NumTipsKey;
        return new MissionBriefingStrings(strings, numTips);
    }
}

/// <summary>
/// P759b — the pure port of native <c>GetCivMissionBriefing</c>,
/// <c>GetMissionBriefingTitle</c>, <c>GetMissionBriefingString</c> and
/// <c>GetMissionTip</c> (<c>WinTrek/teamscreen.cpp:718-826</c>). The briefing is
/// built entirely from the civ-prefixed strings of the artwork's
/// <c>missionbrief.mdl</c>: the <c>NoWin</c> vs <c>Default</c> main text
/// (<c>_snprintf</c>'d with the censored side name, the briefing text and the
/// civ list, teamscreen.cpp:742-750), then — when any win condition applies —
/// the <c>SecondaryObjectives</c> header and one formatted line per enabled
/// condition (Conquest %, Artifacts count, Flags count, Countdown minutes,
/// Deathmatch kill limit, Prosperity money × <c>c_fcidWinTheGameMoney</c>,
/// teamscreen.cpp:752-793), plus the random <c>Tip</c> of
/// <c>GetMissionTip</c> (teamscreen.cpp:820-826). No prose is invented here:
/// every displayed word comes from the mdl file (or the wire inputs it formats).
/// </summary>
public static class MissionBriefingGenerator
{
    private static MissionBriefingStrings? _cached;

    /// <summary>Load the artwork corpus's <c>missionbrief.mdl</c> (Latin-1),
    /// parsing it once per process. Throws
    /// <see cref="ExternalResourceUnavailableException"/> when the artwork is
    /// not configured — callers fall back to the pre-P759 briefing rendering.</summary>
    public static MissionBriefingStrings LoadStrings()
    {
        if (_cached is { } cached) return cached;
        string artwork = ExternalPaths.Require(ExternalResourceKind.Artwork);
        string path = Path.Combine(artwork, "missionbrief.mdl");
        string text = File.ReadAllText(path, Encoding.Latin1);
        var parsed = MissionBriefingStrings.Parse(text);
        _cached = parsed;
        return parsed;
    }

    /// <summary>Build the civ briefing from the wire inputs and the mdl strings.
    /// <paramref name="tipNumber"/> overrides the random tip with a 1-based
    /// <c>TipN</c> key (tests), matching native's <c>rand() % numTips + 1</c>
    /// domain; null picks a random tip exactly as native does.</summary>
    public static (string Title, string Objective, string Body) Build(
        MissionBriefingInfo info, int? tipNumber = null, MissionBriefingStrings? strings = null)
    {
        strings ??= LoadStrings();
        string prefix = info.CivIconName;

        // GetCivListString (teamscreen.cpp:641-669): the OTHER sides' civ names,
        // deduplicated, joined "A, B and C". The wire carries every side's civ
        // name, so the client filters its own civ out (native filters its own
        // side out) and dedups by name (native dedups by civ object id).
        var others = info.CivList
            .Where(c => !string.Equals(c, info.CivName, StringComparison.Ordinal))
            .Distinct()
            .ToList();
        // Native joins "A, B and C" (" and " before the last name).
        string civList = others.Count <= 1 ? string.Join("", others)
            : string.Join(", ", others.Take(others.Count - 1)) + " and " + others[^1];
        if (civList.Length == 0) civList = info.CivName;

        // Main text: NoWin vs Default, _snprintf'd with the side name (native
        // runs it through CensorBadWords; the port has no profanity filter and
        // uses the server-validated name verbatim), the briefing text and the
        // civ list (teamscreen.cpp:742-750).
        string main = FormatPrintf(
            strings.GetCivString(prefix, info.NoWin ? "NoWin" : "Default"),
            info.SideName, info.BriefingText, civList);

        // Secondary objectives (teamscreen.cpp:752-793): the header only when
        // any condition applies, then one formatted line per condition.
        string body = main;
        bool conquest = info.Conquest && info.ConquestPercent != 100;
        bool countdown = info.CountdownSeconds != 0;
        if (info.Artifacts || info.Flags || countdown || conquest
            || info.Deathmatch || info.Prosperity)
        {
            body += strings.GetCivString(prefix, "SecondaryObjectives");

            if (conquest)
                body += FormatPrintf(strings.GetCivString(prefix, "Conquest"), info.ConquestPercent);
            if (info.Artifacts)
                body += FormatPrintf(strings.GetCivString(prefix, "Artifacts"), info.ArtifactGoal);
            if (info.Flags)
                body += FormatPrintf(strings.GetCivString(prefix, "Flags"), info.FlagsGoal);
            if (countdown)
                body += FormatPrintf(strings.GetCivString(prefix, "Countdown"),
                    (int)(info.CountdownSeconds / 60));
            if (info.Deathmatch)
                body += FormatPrintf(strings.GetCivString(prefix, "Deathmatch"), info.KillLimit);
            if (info.Prosperity)
                body += FormatPrintf(strings.GetCivString(prefix, "Prosperity"), info.ProsperityMoney);
        }

        // GetMissionTip (teamscreen.cpp:820-826): rand() % numTips + 1, then the
        // TipN string. The renderer labels the line (native's dialog draws the
        // tip in its own pane under a baked-in "Tip of the Game" caption).
        int tip = (tipNumber is { } t ? ((t - 1) % strings.NumTips + strings.NumTips) % strings.NumTips : Random.Shared.Next(strings.NumTips)) + 1;
        body += $"\n\nTip: {strings.GetString($"Tip{tip}")}";

        return (strings.GetCivString(prefix, "Title"),
                strings.GetCivString(prefix, "String"),
                body);
    }

    /// <summary>The C <c>_snprintf</c> subset the mdl briefing strings use:
    /// <c>%s</c> and <c>%d</c> consume the arguments in order, <c>%%</c> is a
    /// literal percent (e.g. <c>"Control %d%% of the bases."</c>).</summary>
    public static string FormatPrintf(string format, params object[] args)
    {
        var sb = new StringBuilder(format.Length + 16);
        int arg = 0;
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c == '%' && i + 1 < format.Length)
            {
                char spec = format[++i];
                if (spec == '%') sb.Append('%');
                else if (spec == 's' || spec == 'd')
                    sb.Append(arg < args.Length ? args[arg++]?.ToString() ?? "" : "");
                else sb.Append(c).Append(spec);
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
