using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame.Capture;

/// <summary>
/// P565 — the inspector's "mark bad" ledger: one JSON line per marked hull appended to
/// <c>inspector-notes.jsonl</c> (the capture counterpart to <see cref="ScreenShot"/>). Pure
/// serialisation (no MonoGame) so it is unit-testable headless.
///
/// <para>Each line captures enough for a follow-up to turn into a fix list: the hull
/// (id + name + civ), the camera pose at mark time, the active look/VFX state, the
/// screenshot file, and a short note / tag (holes / transparent / flat / scale /
/// texture / vfx-overlap). <see cref="Append"/> appends exactly one valid-JSON line;
/// <see cref="ExportSummary"/> folds the ledger into a de-duplicated fix-oriented digest
/// (grouped by tag) that an agent can consume directly.</para>
/// </summary>
public sealed record InspectorNote(
    DateTimeOffset Utc,
    string File,
    short HullId,
    string HullName,
    string? CivKey,
    string CameraMode,
    double CameraYaw, double CameraPitch, double CameraDistance,
    double CameraTx, double CameraTy, double CameraTz,
    IReadOnlyList<string> Toggles,
    IReadOnlyList<string> ActiveVfx,
    string? Tag,
    string? Note);

/// <summary>Mutable writer for the JSONL ledger.</summary>
public sealed class InspectorNotes
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public InspectorNotes(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>The default ledger path (working-directory relative).</summary>
    public const string DefaultFileName = "inspector-notes.jsonl";

    /// <summary>Append one note as a JSON line (one atomic write; the serializer emits a
    /// single self-contained line per note, so a torn/partial write can only ever lose the
    /// tail of a line, which <see cref="ReadAll"/> tolerates).</summary>
    public void Append(InspectorNote note)
    {
        string line = JsonSerializer.Serialize(note, _json) + Environment.NewLine;
        File.AppendAllText(_path, line);
    }

    /// <summary>Read every recorded note in order.</summary>
    public List<InspectorNote> ReadAll()
    {
        var notes = new List<InspectorNote>();
        if (!File.Exists(_path)) return notes;
        foreach (var line in File.ReadLines(_path))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(line))
                    notes.Add(JsonSerializer.Deserialize<InspectorNote>(line, _json)!);
            }
            catch { /* skip torn/foreign lines */ }
        }
        return notes;
    }

    /// <summary>
    /// Fold the ledger into a fix-list digest: unique hulls grouped by tag, each with
    /// the count, and a line per distinct (tag, hull) pair quoting the first note.
    /// Grouped by tag so a human/agent sees "these hulls are TRANSPARENT" at a glance.
    /// </summary>
    public string ExportSummary()
    {
        var notes = ReadAll();
        if (notes.Count == 0) return "# inspector-notes\n\n(no markings recorded)\n";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# inspector-notes");
        sb.AppendLine();
        foreach (var tag in notes.Where(n => !string.IsNullOrEmpty(n.Tag))
                                  .Select(n => n.Tag!).Distinct().OrderBy(t => t, StringComparer.Ordinal))
        {
            var tagged = notes.Where(n => n.Tag == tag).ToList();
            var hulls = tagged.Select(n => $"{n.HullName} (id {n.HullId}{(n.CivKey is null ? "" : ", " + n.CivKey)})")
                              .Distinct().ToList();
            sb.AppendLine($"## {tag}  — {tagged.Count} mark(s), {hulls.Count} hull(s)");
            foreach (var h in hulls)
            {
                var first = tagged.First(n => $"{n.HullName} (id {n.HullId}{(n.CivKey is null ? "" : ", " + n.CivKey)})" == h);
                sb.AppendLine($"  - {h}  [{first.File}]{(first.Note is null ? "" : $" — {first.Note}")}");
            }
            sb.AppendLine();
        }
        var unTagged = notes.Where(n => string.IsNullOrEmpty(n.Tag)).ToList();
        if (unTagged.Count > 0)
        {
            sb.AppendLine($"## (no tag) — {unTagged.Count} mark(s)");
            foreach (var n in unTagged)
                sb.AppendLine($"  - {n.HullName} (id {n.HullId})  [{n.File}]{(n.Note is null ? "" : $" — {n.Note}")}");
        }
        return sb.ToString();
    }
}
