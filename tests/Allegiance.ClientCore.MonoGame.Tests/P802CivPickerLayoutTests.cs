using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P802 — the team lobby's civilization picker UI (rows + state), driven through
/// the same <see cref="ClientWorld"/> state the overlay reads.
/// Native rules pinned here: the current civ is always shown (teamscreen.cpp:586-591,
/// 2479-2492); only the side's <b>team leader</b> before <c>STAGE_NOTSTARTED</c>
/// may open the selector (FedSrv.CPP:7344-7366); the selector lists the core's
/// civilizations below the native "prevent special civs" cut of 300 plus
/// <c>RANDOM</c> (teamscreen.cpp:899-916, <c>igc.h:501</c>); everyone else sees
/// the civ read-only and live.
/// </summary>
public sealed class P802CivPickerLayoutTests
{
    private static StaticCore LoadCore() => StaticCore.Load(
        IgcReader.ParseFile(Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.Artwork), "PCore001.igc")));

    /// <summary>Seated leader of side 0, mission NotStarted, civ 1.</summary>
    private static ClientWorld LeaderWorld(StaticCore core, short myShip = 10, short civ = 1)
    {
        var world = new ClientWorld { MissionCoreResolver = _ => core };
        world.Apply(new MissionDef { SzIGCStaticFile = "pcore001" });
        world.Apply(new JoinedMission { ShipId = myShip });
        world.Apply(new SideRoster { Sides = [new(0, civ, 1, "Alpha"), new(1, 3, 1, "Bravo")] });
        foreach (short id in new short[] { 10, 11 })
            world.Apply(new PlayerInfo { ShipId = id, ISide = 0, CharacterName = "Pilot " + id });
        world.Apply(new SetTeamLeader { SideId = 0, ShipId = 10 });
        var payload = new byte[ServerShipUpdate.Width];
        payload[0] = (byte)myShip;
        var reader = new WireReader(payload);
        world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.DecodeBody(ref reader) });
        return world;
    }

    private static string[] Texts(IReadOnlyList<ReadyCheckLayout.Row> rows) => rows.Select(r => r.Text).ToArray();

    /// <summary>⭐ Closed picker: the current civ is on screen and only the
    /// leader is told the change key exists — everyone else gets the read-only
    /// note (no control at all).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Closed_picker_shows_current_civ_and_only_leaders_get_the_hint()
    {
        Require.Artwork();
        var core = LoadCore();
        var world = LeaderWorld(core);
        var leader = TeamCivPickerLayout.Build(world, open: false, index: 0);
        Assert.Contains($"TEAM CIV: {core.Civilizations[1].Name}", Texts(leader));
        Assert.Contains("C  CHANGE TEAM CIVILIZATION", Texts(leader));
        Assert.DoesNotContain("SELECT CIVILIZATION", Texts(leader));

        // Non-leader: same civ line, no control, an explicit read-only note.
        world.Apply(new SetTeamLeader { SideId = 0, ShipId = 11 });
        var follower = TeamCivPickerLayout.Build(world, open: true, index: 0);
        Assert.Contains($"TEAM CIV: {core.Civilizations[1].Name}", Texts(follower));
        Assert.DoesNotContain("C  CHANGE TEAM CIVILIZATION", Texts(follower));
        Assert.DoesNotContain("SELECT CIVILIZATION", Texts(follower));
        Assert.Contains("Only the team leader can change it, before the game starts.", Texts(follower));
    }

    /// <summary>⭐ Open picker: one row per legal core civ plus RANDOM, cursor
    /// highlighted, and the confirm hint. After the mission starts the same
    /// leader loses the selector (read-only, native STAGE_NOTSTARTED gate).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Open_picker_lists_every_legal_civ_plus_random_and_closes_at_start()
    {
        Require.Artwork();
        var core = LoadCore();
        var world = LeaderWorld(core);
        var options = TeamCivPicker.Options(core);
        int index = TeamCivPicker.IndexOf(options, 1);
        Assert.True(index >= 0);

        var rows = TeamCivPickerLayout.Build(world, open: true, index: index);
        Assert.Contains("SELECT CIVILIZATION", Texts(rows));
        Assert.Contains("UP/DOWN SELECT   ENTER CONFIRM   C CLOSE", Texts(rows));
        foreach (var option in options)
            Assert.Contains(rows, r => r.Kind == ReadyCheckLayout.Kind.Item && r.Text.TrimStart('>', ' ').StartsWith(option.Name, StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Kind == ReadyCheckLayout.Kind.Item && r.Highlight && r.Text.Contains(core.Civilizations[1].Name));
        Assert.Equal(index, TeamCivPickerLayout.InitialIndex(world));

        world.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarted });
        var afterStart = TeamCivPickerLayout.Build(world, open: true, index: index);
        Assert.DoesNotContain("SELECT CIVILIZATION", Texts(afterStart));
        Assert.Contains("Only the team leader can change it, before the game starts.", Texts(afterStart));
    }

    /// <summary>The cursor starts on the side's current civ, or on RANDOM when
    /// the side already runs a random faction (native's "Random" selection,
    /// teamscreen.cpp:1007-1009).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Initial_cursor_tracks_the_current_civ_and_the_random_flag()
    {
        Require.Artwork();
        var core = LoadCore();
        var world = LeaderWorld(core);
        var options = TeamCivPicker.Options(core);
        Assert.Equal(TeamCivPicker.IndexOf(options, 1), TeamCivPickerLayout.InitialIndex(world));

        world.Apply(new ChangeTeamCiv { SideId = 0, CivId = options[0].CivId, Random = true });
        Assert.Equal(options.Count - 1, TeamCivPickerLayout.InitialIndex(world));
    }

    /// <summary>The side-select roster carries each side's civ read-only
    /// (<c>SideSelectLayout</c>'s optional resolver), so the side screen shows
    /// the civilizations before the player is seated.</summary>
    [Fact]
    public void Side_select_roster_annotates_each_side_with_its_civ()
    {
        var roster = new SideRoster { Sides = [new(0, 1, 2, "Alpha"), new(1, 3, 1, "Bravo")] };
        var plain = SideSelectLayout.Build(roster, 0, amMissionOwner: false);
        Assert.DoesNotContain(plain, r => r.Text.Contains('['));
        var annotated = SideSelectLayout.Build(roster, 0, amMissionOwner: false, civNameFor: side => side == 1 ? "Iron Coalition" : null);
        Assert.Contains(annotated, r => r.Text.Contains("Alpha") && !r.Text.Contains("Iron Coalition"));
        Assert.Contains(annotated, r => r.Text.Contains("Bravo") && r.Text.Contains("[Iron Coalition]"));
    }
}
