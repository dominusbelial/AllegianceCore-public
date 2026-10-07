using System;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Capture;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>P565 — pure tests for the inspector's "mark bad" JSONL ledger + summary export.</summary>
public class InspectorNotesTests
{
    private static InspectorNote Note(string? tag, string? note = null, string hull = "Adv Fighter", short id = 17)
        => new(DateTimeOffset.UtcNow, "screenshots/inspect_17_x.png", id, hull, "ic",
            "orbit", 0.5, 0.2, 600, 0, 0, 0,
            new[] { "art=native", "cloak=off" }, new[] { "smoke:50%" }, tag, note);

    [Fact]
    public void Append_then_read_round_trips_fields()
    {
        string path = Path.Combine(Path.GetTempPath(), "inspector-notes-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var w = new InspectorNotes(path);
            w.Append(Note("transparent"));
            var notes = w.ReadAll();
            Assert.Single(notes);
            Assert.Equal("transparent", notes[0].Tag);
            Assert.Equal("Adv Fighter", notes[0].HullName);
            Assert.Equal((short)17, notes[0].HullId);
            Assert.Equal("ic", notes[0].CivKey);
            Assert.Equal("orbit", notes[0].CameraMode);
            Assert.Contains("smoke:50%", notes[0].ActiveVfx);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Multiple_appends_accumulate_in_order()
    {
        string path = Path.Combine(Path.GetTempPath(), "inspector-notes-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var w = new InspectorNotes(path);
            w.Append(Note("holes", id: 1));
            w.Append(Note("flat", id: 2));
            w.Append(Note(null, note: "odd seams", id: 3));
            var notes = w.ReadAll();
            Assert.Equal(3, notes.Count);
            Assert.Equal(new short[] { 1, 2, 3 }, notes.Select(n => n.HullId).ToArray());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Export_summary_groups_by_tag_and_lists_hulls()
    {
        string path = Path.Combine(Path.GetTempPath(), "inspector-notes-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var w = new InspectorNotes(path);
            w.Append(Note("transparent", hull: "Scout", id: 5));
            w.Append(Note("transparent", hull: "Bomber", id: 6));
            w.Append(Note("scale", note: "too big", hull: "Carrier", id: 8));
            string summary = w.ExportSummary();
            Assert.Contains("## transparent", summary);
            Assert.Contains("## scale", summary);
            Assert.Contains("Scout (id 5, ic)", summary);
            Assert.Contains("Carrier (id 8, ic)", summary);
            Assert.Contains("too big", summary);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Empty_ledger_exports_a_note_free_summary()
    {
        string path = Path.Combine(Path.GetTempPath(), "inspector-notes-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var w = new InspectorNotes(path);
            Assert.Contains("no markings", w.ExportSummary());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
