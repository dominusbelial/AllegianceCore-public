using System;
using System.IO;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Content;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Capture;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P615 — the STATION inspector walk (<c>--inspect-stations</c>): the station
/// catalogue, its mark-bad ledger format, and the mode's texture resolution.
/// Pure enumeration + serialisation (no graphics device), mirroring the hull
/// inspector's headless testability.
/// </summary>
public class InspectorStationTests
{
    private static string ArtRoot => ExternalPaths.Resolve(ExternalResourceKind.Artwork);
    private static bool FileExists(string stem) => File.Exists(Path.Combine(ArtRoot, stem + ".mdl"));

    private static StaticCore LoadCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        return StaticCore.Load(file);
    }

    /// <summary>The station walk list covers EXACTLY the core's station-type set
    /// (the static core has 143 station types) — no missing, no invented ids,
    /// ordered stably by StationTypeId. FAILING-FIRST: the pre-P615 code had no
    /// station walk at all.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Station_walk_list_equals_the_core_station_type_set()
    {
        Require.Artwork();
        var core = LoadCore();
        var walk = InspectorStationCatalog.Enumerate(core.StationTypes.Values, _ => false);

        var expectedIds = core.StationTypes.Keys.OrderBy(k => k).ToList();
        Assert.Equal(expectedIds.Count, walk.Count);
        Assert.Equal(expectedIds, walk.Select(w => w.StationTypeId).ToList());
        // Sanity on the brief's stated magnitude (143 types in this core).
        Assert.True(walk.Count >= 100, $"expected ~143 station types, got {walk.Count}");
    }

    /// <summary>Each walk entry resolves a model + native texture (utl02bmp-style)
    /// for its station, so the inspector renders native-faithfully. Uses the same
    /// NativeStationResolver rule the live renderer does.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Every_station_model_resolves_mesh_and_native_texture()
    {
        Require.Artwork();
        var core = LoadCore();
        int fallback = 0;
        foreach (var st in core.StationTypes.Values)
        {
            if (string.IsNullOrWhiteSpace(st.ModelName)) { fallback++; continue; }
            var mdl = MdlParse(st.ModelName.Trim());
            var tex = NativeStationResolver.ResolveTextureStem(mdl, st.TextureName, st.ModelName.Trim(), FileExists);
            if (tex is null) { fallback++; Console.WriteLine($"station model '{st.ModelName}' no texture"); }
        }
        Assert.Equal(0, fallback);
    }

    /// <summary>The station mark-bad ledger round-trips and its export reads back
    /// as a fix list (the "easy to read back" requirement for Domingo's own marks).
    /// FAILING-FIRST: pre-P615 there was no station ledger type.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Station_mark_bad_output_format_round_trips()
    {
        Require.Artwork();
        string tmp = Path.Combine(Path.GetTempPath(), $"p615-stations-{Guid.NewGuid():N}.jsonl");
        try
        {
            var notes = new InspectorStationNotes(tmp);
            notes.Append(new StationInspectorNote(
                DateTimeOffset.Now, "inspect_shot_1.png", 10, "Garrison", "ss24a", 302.5f,
                NativeResolved: true, 1.2, 0.3, 600.0, "texture", "door texture wrong"));

            var back = notes.ReadAll();
            var n = Assert.Single(back);
            Assert.Equal(10, n.StationTypeId);
            Assert.Equal("Garrison", n.StationName);
            Assert.Equal("ss24a", n.ModelName);
            Assert.Equal(302.5f, n.Radius);
            Assert.True(n.NativeResolved);
            Assert.Equal("texture", n.Tag);
            Assert.Equal("door texture wrong", n.Note);

            string summary = notes.ExportSummary();
            Assert.Contains("## texture", summary);
            Assert.Contains("Garrison", summary);
            Assert.Contains("ss24a", summary);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static MdlFile MdlParse(string stem)
        => MdlFile.Parse(Path.Combine(ArtRoot, stem + ".mdl"));
}
