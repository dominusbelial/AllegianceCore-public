using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame.Capture;

/// <summary>
/// P615 — the STATION inspector's "mark bad" ledger, one JSON line per marked
/// station appended to <c>stations-marked.jsonl</c>. Mirror of
/// <see cref="InspectorNotes"/> for the station-mode walk, but each line records
/// the station-type specifics the station render audit needs (station type id,
/// name, model, radius, native-resolved yes/no) so the file reads straight back
/// into a fix list (Domingo marks "weird" stations himself with the inspector).
/// Pure serialisation (no MonoGame), unit-testable headless.
/// </summary>
public sealed record StationInspectorNote(
    DateTimeOffset Utc,
    string File,
    short StationTypeId,
    string StationName,
    string ModelName,
    float Radius,
    bool NativeResolved,
    double CameraYaw, double CameraPitch, double CameraDistance,
    string? Tag,
    string? Note);

/// <summary>Mutable writer for the station JSONL ledger.</summary>
public sealed class InspectorStationNotes
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public InspectorStationNotes(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    /// <summary>The default station ledger path (working-directory relative).</summary>
    public const string DefaultFileName = "stations-marked.jsonl";

    public void Append(StationInspectorNote note)
    {
        string line = JsonSerializer.Serialize(note, _json) + Environment.NewLine;
        File.AppendAllText(_path, line);
    }

    public List<StationInspectorNote> ReadAll()
    {
        var notes = new List<StationInspectorNote>();
        if (!File.Exists(_path)) return notes;
        foreach (var line in File.ReadLines(_path))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(line))
                    notes.Add(JsonSerializer.Deserialize<StationInspectorNote>(line, _json)!);
            }
            catch { /* skip torn/foreign lines */ }
        }
        return notes;
    }

    /// <summary>Fold the ledger into a fix-oriented digest grouped by tag
    /// (easy read-back). Each distinct (tag, station) quoted once with its
    /// first note + screenshot.</summary>
    public string ExportSummary()
    {
        var notes = ReadAll();
        if (notes.Count == 0) return "# stations-marked\n\n(no markings recorded)\n";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# stations-marked");
        sb.AppendLine();
        foreach (var tag in notes.Where(n => !string.IsNullOrEmpty(n.Tag))
                                  .Select(n => n.Tag!).Distinct().OrderBy(t => t, StringComparer.Ordinal))
        {
            var tagged = notes.Where(n => n.Tag == tag).ToList();
            var stations = tagged.Select(n => $"{n.StationName} (id {n.StationTypeId}, model {n.ModelName}, native={n.NativeResolved})")
                                 .Distinct().ToList();
            sb.AppendLine($"## {tag}  — {tagged.Count} mark(s), {stations.Count} station(s)");
            foreach (var s in stations)
            {
                var first = tagged.First(n => $"{n.StationName} (id {n.StationTypeId}, model {n.ModelName}, native={n.NativeResolved})" == s);
                sb.AppendLine($"  - {s}  [{first.File}]{(first.Note is null ? "" : $" — {first.Note}")}");
            }
            sb.AppendLine();
        }
        var unTagged = notes.Where(n => string.IsNullOrEmpty(n.Tag)).ToList();
        if (unTagged.Count > 0)
        {
            sb.AppendLine($"## (no tag) — {unTagged.Count} mark(s)");
            foreach (var n in unTagged)
                sb.AppendLine($"  - {n.StationName} (id {n.StationTypeId}, model {n.ModelName})  [{n.File}]{(n.Note is null ? "" : $" — {n.Note}")}");
        }
        return sb.ToString();
    }
}
