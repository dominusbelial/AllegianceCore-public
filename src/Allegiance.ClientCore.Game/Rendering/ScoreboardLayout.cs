using System;
using System.Collections.Generic;
using System.Linq;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P265 — pure (MonoGame-free) layout of the in-mission scoreboard overlay.
/// Turns the wire <c>ScoreRosterEntry</c> list from <c>ClientWorld.ScoreRoster</c>
/// into aligned, pure-ASCII table cells (name / side / kills / deaths) that the
/// <see cref="ScoreboardOverlayRenderer"/> draws.
///
/// <para>Kept MonoGame-free (mirroring <see cref="KeyBindingTable"/> /
/// <see cref="HelpOverlayLayout"/>) so the ordering, ASCII-safety and column
/// alignment are headless-unit-testable without a graphics device.</para>
///
/// <para><b>Ordering.</b> A scoreboard reads best grouped by team with the best
/// scorer on top: rows sort by <c>SideId</c> ascending, then <c>Kills</c>
/// descending (ties broken by name). This is a presentation choice, not a wire
/// change — the client is free to order the same snapshot however it likes.</para>
///
/// <para><b>ASCII safety.</b> The HUD spritefonts bake ASCII 32..126 only
/// (P214/p217 lesson), so every cell this produces must be pure printable
/// ASCII — the side is rendered as the numeric id, and any non-ASCII name char
/// is scrubbed (there are no legal characters in a normal name, but a hostile
/// or mangled name must not crash the renderer).</para>
/// </summary>
public static class ScoreboardLayout
{
    /// <summary>Vertical gap between the header and the first row, in px.</summary>
    public const float HeaderGap = 10f;

    /// <summary>Row height for one roster line, in px.</summary>
    public const float RowHeight = 20f;

    /// <summary>Horizontal padding between the panel edge and the first column, in px.</summary>
    public const float Padding = 20f;

    /// <summary>Extra horizontal gap inserted between adjacent columns, in px.</summary>
    public const float ColumnGap = 24f;

    /// <summary>One scored row, ready to draw: the player name and the rendered
    /// side label (numeric id), plus every live counter column (kills, deaths,
    /// ejections, rescues, base kills, base captures, artifacts, flags).</summary>
    public readonly record struct Row(
        string Name, string SideLabel, string Kills, string Deaths,
        string Ejections, string Rescues, string BaseKills, string BaseCaptures,
        string Artifacts, string Flags)
    {
        public const string HeaderName = "NAME";
        public const string HeaderSide = "SIDE";
        public const string HeaderKills = "K";
        public const string HeaderDeaths = "D";
        public const string HeaderEjections = "EJ";
        public const string HeaderRescues = "RS";
        public const string HeaderBaseKills = "BK";
        public const string HeaderBaseCaptures = "BC";
        public const string HeaderArtifacts = "AR";
        public const string HeaderFlags = "FL";
    }

    /// <summary>
    /// Build the ordered, ASCII-safe rows from a wire roster snapshot. Inputs
    /// are left unsorted-trusted; this applies the scoreboard ordering above.
    /// Every returned string is pure ASCII 32..126 (scrubbing otherwise).
    /// When <paramref name="isSideActive"/> is supplied, a row whose side is
    /// inactive (an eliminated side, P455b) is marked "<c>(DESTROYED)</c>" on
    /// its side label — exactly native's team list, which spells out
    /// "destroyed" for an empty/inactive team (teamscreen.cpp:318).
    /// </summary>
    public static IReadOnlyList<Row> Build(
        IReadOnlyList<Allegiance.Core.Wire.ScoreRosterEntry> entries,
        Func<short, bool>? isSideActive = null)
    {
        var rows = entries
            .OrderBy(e => e.SideId)
            .ThenByDescending(e => e.Kills)
            .ThenBy(e => e.CharacterName, StringComparer.Ordinal)
            .Select(e => new Row(
                Name: AsciiSafe(e.CharacterName),
                SideLabel: isSideActive is not null && !isSideActive(e.SideId)
                    ? $"S{e.SideId}(DESTROYED)"
                    : $"S{e.SideId}",
                Kills: e.Kills.ToString(),
                Deaths: e.Deaths.ToString(),
                Ejections: e.Ejections.ToString(),
                Rescues: e.Rescues.ToString(),
                BaseKills: e.BaseKills.ToString(),
                BaseCaptures: e.BaseCaptures.ToString(),
                Artifacts: e.Artifacts.ToString(),
                Flags: e.Flags.ToString()))
            .ToList();
        return rows;
    }

    /// <summary>The ten column widths (in px at font scale 1) for the given rows:
    /// name, side, kills, deaths, ejections, rescues, baseKills, baseCaptures,
    /// artifacts, flags — each at least as wide as its header label.</summary>
    public static (float Name, float Side, float Kills, float Deaths,
        float Ejections, float Rescues, float BaseKills, float BaseCaptures,
        float Artifacts, float Flags) ColumnWidths(
        IReadOnlyList<Row> rows, Func<string, float> measure)
    {
        float name = measure(Row.HeaderName);
        float side = measure(Row.HeaderSide);
        float kills = measure(Row.HeaderKills);
        float deaths = measure(Row.HeaderDeaths);
        float ej = measure(Row.HeaderEjections);
        float rs = measure(Row.HeaderRescues);
        float bk = measure(Row.HeaderBaseKills);
        float bc = measure(Row.HeaderBaseCaptures);
        float ar = measure(Row.HeaderArtifacts);
        float fl = measure(Row.HeaderFlags);
        foreach (var r in rows)
        {
            name = MathF.Max(name, measure(r.Name));
            side = MathF.Max(side, measure(r.SideLabel));
            kills = MathF.Max(kills, measure(r.Kills));
            deaths = MathF.Max(deaths, measure(r.Deaths));
            ej = MathF.Max(ej, measure(r.Ejections));
            rs = MathF.Max(rs, measure(r.Rescues));
            bk = MathF.Max(bk, measure(r.BaseKills));
            bc = MathF.Max(bc, measure(r.BaseCaptures));
            ar = MathF.Max(ar, measure(r.Artifacts));
            fl = MathF.Max(fl, measure(r.Flags));
        }
        return (name, side, kills, deaths, ej, rs, bk, bc, ar, fl);
    }

    /// <summary>Strip any character outside printable ASCII 32..126 (the
    /// spritefont-baked range), replacing it with a space so the renderer never
    /// throws on a hostile/mangled name (P214/p217 lesson).</summary>
    public static string AsciiSafe(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        int n = 0;
        foreach (char c in s)
        {
            if (c >= 32 && c <= 126) buf[n++] = c;
            else buf[n++] = ' ';
        }
        return new string(buf[..n]);
    }
}
