using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-20 — the remaining HUD art gaps that were readable but not drawn:
/// the docked "Currently viewing:" sector block
/// (<c>dialog.mdl:1429-1471,5421-5422</c>), the target pane's native kill-bonus
/// art/number (<c>dialog.mdl:4192-4214,4232</c>, <c>consoledata.cpp:831-843</c>),
/// plus the per-frame allocation budgets of those paths.
/// </summary>
public class Hud20ArtGapsTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public Hud20ArtGapsTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    // ── item 2: viewing sector rule + the LookingAt block ────────────────────

    [Fact]
    public void Viewed_sector_uses_the_view_cluster_then_the_station_cluster()
    {
        // Native GetCluster() = view cluster when set, else the ship's
        // (clintlib.h:996); docked that is ViewedClusterId ?? station cluster.
        Assert.Equal((short)7, HudConsoleFormat.ViewedSectorId(7, 3));
        Assert.Equal((short)3, HudConsoleFormat.ViewedSectorId(null, 3));
    }

    [Fact]
    public void Looking_at_is_the_yellow_label_and_the_red_viewed_sector_name()
    {
        // Native CurrentViewSectorNameImage is medVerdana Color(1,0,0)
        // (dialog.mdl:1429-1444) under the yellow promptFont label (:1446-1468).
        var block = HudConsoleFormat.LookingAt("Rix");
        Assert.NotNull(block);
        Assert.Equal("Currently viewing:", block!.Value.Label);
        Assert.Equal("Rix", block.Value.Name);
        Assert.Equal(1f, block.Value.Color.R);
        Assert.Equal(0f, block.Value.Color.G);
        Assert.Equal(0f, block.Value.Color.B);
        Assert.Null(HudConsoleFormat.LookingAt(null));
    }

    [Fact]
    public void Sector_location_element_is_visible_in_flight_and_in_docked_command_view()
    {
        HudFrame? frame = null;
        var flight = HudModel.Create(frame, new HudContext
        {
            Console = new HudConsoleText { SectorName = "Helios" },
        });
        var inFlight = flight.Elements.Single(e => e.Id == "Sector location");
        Assert.True(inFlight.Visible);
        Assert.Equal("Helios", inFlight.Text);

        // Docked command view: the VIEWED sector name, at the same anchor.
        var docked = HudModel.Create(frame, new HudContext
        {
            Docked = true,
            CommandView = true,
            Console = new HudConsoleText { SectorName = "Station Sector", ViewedSectorName = "Rix" },
        });
        var viewing = docked.Elements.Single(e => e.Id == "Sector location");
        Assert.True(viewing.Visible);
        Assert.Equal("Rix", viewing.Text);
        Assert.Equal(new HudPlacement(HudAnchor.TopRight, -180f, -37f, true), viewing.Placement);

        // Docked NON-command view: native hangs the hangar variant on
        // ofInHangar | ofInLoadout (dialog.mdl:5420), covered by the hangar UI.
        var hangar = HudModel.Create(frame, new HudContext
        {
            Docked = true,
            Console = new HudConsoleText { SectorName = "Station Sector", ViewedSectorName = "Rix" },
        });
        Assert.False(hangar.Elements.Single(e => e.Id == "Sector location").Visible);
    }

    /// <summary>HUD-20 review — behaviour of the one gate both skins and the
    /// HUD-11 anchor reservation call (<see cref="HudConsoleFormat.DockedLookingAt"/>):
    /// native <c>LookingAtImage</c> is hung on <c>ofInStationCommand</c> only
    /// (<c>dialog.mdl:5421-5422</c>, <c>WinTrek.cpp:6831-6840</c>). The font
    /// choice (FONT-1 promptFont/medVerdana in Classic) needs a GraphicsDevice
    /// and is not covered here.</summary>
    [Fact]
    public void Docked_looking_at_is_drawn_only_when_visible_docked_and_in_command_view()
    {
        var shown = HudConsoleFormat.DockedLookingAt(visible: true, flightVisible: false,
            docked: true, commandView: true, "Rix");
        Assert.NotNull(shown);
        Assert.Equal("Rix", shown!.Value.Name);
        Assert.Equal(HudConsoleFormat.ViewedSectorColor, shown.Value.Color);   // native Color(1,0,0)

        // HUD hidden (the classic model.Visible check, now shared with Modern).
        Assert.Null(HudConsoleFormat.DockedLookingAt(false, false, true, true, "Rix"));
        // In flight: the ofInFlight "Current location:" block owns the anchor.
        Assert.Null(HudConsoleFormat.DockedLookingAt(true, true, false, true, "Rix"));
        // Docked hangar / loadout (not command view): the hangar variant, not this.
        Assert.Null(HudConsoleFormat.DockedLookingAt(true, false, true, false, "Rix"));
        // No viewed sector resolved yet.
        Assert.Null(HudConsoleFormat.DockedLookingAt(true, false, true, true, null));
    }

    // ── item 4(f): the target kill bonus ──────────────────────────────────

    [Fact]
    public void Target_experience_is_50e_over_e_plus_4()
    {
        // consoledata.cpp:831-843 -> 100*(GetExperienceMultiplier()-1);
        // shipIGC.h:1812-1818 -> 1 + 0.5*e/(e+4), i.e. 50*e/(e+4).
        Assert.Equal(50f * 3f / 7f, TargetPaneModel.TargetExperience(3f), 5);
        Assert.Equal(0f, TargetPaneModel.TargetExperience(0f));
        Assert.Equal(10f, TargetPaneModel.TargetExperience(1f), 5);
    }

    private static ClientWorld WorldWithShip(short id, float experience)
    {
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = id, HullId = 15, SideId = 2, CharacterName = "bandit" });
        world.Apply(new PlayerInfo
        {
            ShipId = id, ISide = 2, CharacterName = "bandit",
            FExperience = experience, DtidDrone = PlayerInfo.NA,
        });
        return world;
    }

    [Fact]
    public void Target_pane_reports_the_kill_bonus_for_a_ship_and_null_for_a_station()
    {
        var world = WorldWithShip(501, 3f);
        var ship = TargetPaneModel.Build(world, IgcObjectType.Ship, 501);
        Assert.True(ship.IsShip);
        Assert.Equal(50f * 3f / 7f, ship.KillBonusPercent!.Value, 5);

        // A ship whose experience is still 0 reads 0 (what native shows for a
        // fresh port ship — the port seeds the counter at 0, see Ship.cs P278).
        var fresh = WorldWithShip(502, 0f);
        Assert.Equal(0f, TargetPaneModel.Build(fresh, IgcObjectType.Ship, 502).KillBonusPercent);

        // The native switch hides the readout for every non-ship target.
        var station = TargetPaneModel.Build(world, IgcObjectType.Station, 900);
        Assert.False(station.IsShip);
        Assert.Null(station.KillBonusPercent);
    }

    /// <summary>HUD-20 review — the kill-bonus placement both skins draw with
    /// (<see cref="TargetPaneLayout.ExperienceArtBottomLeft"/>,
    /// <see cref="TargetPaneLayout.ExperienceNumberTopLeft"/>). Native
    /// <c>TargetExperienceOnImage</c> sits at pane-local Point(280,110)
    /// (<c>dialog.mdl:4232</c>); its art is translated Point(25,-21) (:4201-4205)
    /// and the JustifyCenter 80-wide StringImage hangs below its origin
    /// (<c>engine/image.cpp:336-388</c>), so 110 is the number's TOP. Pane rule:
    /// screen = (x0 + x·s, y0 − y·s).</summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void Kill_bonus_number_sits_on_the_medal_at_the_native_anchor(float s)
    {
        const float x0 = 100f, y0 = 900f;
        var (artLeft, artBottom) = TargetPaneLayout.ExperienceArtBottomLeft(x0, y0, s);
        Assert.Equal(x0 + 305f * s, artLeft, 3);
        Assert.Equal(y0 - 89f * s, artBottom, 3);

        float numberWidth = 14f * s;
        var (left, top) = TargetPaneLayout.ExperienceNumberTopLeft(x0, y0, s, numberWidth);
        Assert.Equal(y0 - 110f * s, top, 3);                          // StringImage origin = text top
        Assert.Equal(x0 + 320f * s, left + numberWidth / 2f, 3);      // centred in the 280..360 box

        // With the native medBoldVerdana cell (13 px, NativeFontCatalog) the
        // number lies wholly inside the 32x32 medal (local 89..121), as native.
        float cell = NativeFontCatalog.NativeCellHeight[NativeFont.MedBoldVerdana] * s;
        float medalTop = artBottom - 32f * s;
        Assert.InRange(top, medalTop, artBottom);
        Assert.InRange(top + cell, medalTop, artBottom);
    }

    /// <summary>The art is the native capital-T <c>TargetExperiencebmp.mdl</c>
    /// (Linux lookups are case sensitive) and decodes to the 32x32 medal.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Target_experience_art_resolves_with_the_native_file_name()
    {
        Require.Artwork();
        var assets = new DirectoryAssetSource(ExternalPaths.Resolve(ExternalResourceKind.Artwork));
        string rel = TargetPaneTextures.TargetExperienceMdl + ".mdl";
        Assert.True(assets.Exists(rel), rel);
        var decoded = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(assets, rel), flipVertical: false);
        Assert.Equal((32, 32), (decoded.Width, decoded.Height));
    }

    // ── per-frame allocation budgets ─────────────────────────────────────

    [Fact]
    public void Target_kill_bonus_resolution_and_string_lookup_allocate_nothing()
    {
        static void Frame()
        {
            float percent = TargetPaneModel.TargetExperience(3f);
            _ = HudStringCache.KillBonusNumber(percent);
        }
        for (int warm = 0; warm < 50; warm++) Frame();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 1000; frame++) Frame();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine($"HUD-20 kill bonus allocation over 1000 frames: {allocated} B");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Docked_currently_viewing_label_path_allocates_nothing()
    {
        static void Frame()
        {
            if (HudConsoleFormat.LookingAt("Rix") is { } block)
            {
                _ = block.Label;
                _ = block.Name;
                _ = block.Color;
            }
            _ = HudConsoleFormat.NameBelowLabel(HudConsoleFormat.SectorLabelTop, 23f);
        }
        for (int warm = 0; warm < 50; warm++) Frame();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < 1000; frame++) Frame();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine($"HUD-20 currently-viewing allocation over 1000 frames: {allocated} B");
        Assert.Equal(0, allocated);
    }
}
