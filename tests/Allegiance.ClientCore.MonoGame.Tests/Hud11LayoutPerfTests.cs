using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-11 — the layout/behaviour rules from the HUD-3…HUD-10 review: per-frame
/// snapshot buffers, the one-pass contact count with the HUD-5b label cache,
/// the console-block line gaps/reservation, the configured chat-pane height,
/// the native inventory stats slot, the command-bar wing caption and the native
/// c_cidDefault target-order send. All rules are pure and headless.
/// </summary>
public class Hud11LayoutPerfTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public Hud11LayoutPerfTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;
    // ── HUD-11 item 1: snapshot buffers (no ToArray per frame) ────────────

    private static HudContact Contact(int id, bool accepted = false, bool name = true)
        => new(HudContactKind.Ship, new HudPoint(id, id), true, 1f, null, false, false,
            Name: name ? $"ship-{id}" : null, Range: 1200 + id,
            IsSameSide: !accepted, AcceptedOrder: accepted);

    [Fact]
    public void Contact_snapshot_reuses_one_buffer_across_frames()
    {
        var buffers = new HudSnapshotBuffers();
        var contacts = new[] { Contact(1), Contact(2, accepted: true), Contact(3) };
        var context = new HudContext { Contacts = contacts };

        var first = HudModel.Create(null, context, buffers);
        var second = HudModel.Create(null, context, buffers);

        Assert.Same(first.Context.Contacts, second.Context.Contacts);
        Assert.Same(buffers.Contacts, second.Context.Contacts);
        Assert.Equal(3, second.Context.Contacts.Count);
        Assert.Equal(contacts[1], second.Context.Contacts[1]);
    }

    [Fact]
    public void Inventory_row_snapshot_reuses_one_buffer_across_frames()
    {
        var buffers = new HudSnapshotBuffers();
        var pane = new HudInventoryPane("Adv Fighter", new[]
        {
            new HudInventoryRow(HudInventoryRowKind.Weapon, "1.", "PW Gat Gun 3", false, false,
                false, "660m / 5 X 2", 3, 1f, null, null),
            new HudInventoryRow(HudInventoryRowKind.EmptyCargo, "S.", "<cargo mount>", false,
                false, false, null, 0, 0f, null, null),
        });
        var context = new HudContext { InventoryVisible = true, InventoryPane = pane };

        var first = HudModel.Create(null, context, buffers);
        var second = HudModel.Create(null, context, buffers);

        Assert.Same(first.Context.InventoryPane!.Rows, second.Context.InventoryPane!.Rows);
        Assert.Same(buffers.InventoryRows, second.Context.InventoryPane!.Rows);
        Assert.Equal(2, second.Context.InventoryPane!.Rows.Count);
        Assert.Equal("660m / 5 X 2", second.Context.InventoryPane!.Rows[0].StatsText);
    }

    [Fact]
    public void Per_frame_snapshot_allocation_does_not_scale_with_contact_count()
    {
        static long BytesPerFrame(int count, HudSnapshotBuffers buffers)
        {
            var contacts = new HudContact[count];
            for (int i = 0; i < count; i++) contacts[i] = Contact(i);
            var context = new HudContext { Contacts = contacts };
            for (int warm = 0; warm < 20; warm++) HudModel.Create(null, context, buffers);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < 200; frame++) HudModel.Create(null, context, buffers);
            return (GC.GetAllocatedBytesForCurrentThread() - before) / 200;
        }

        long small = BytesPerFrame(4, new HudSnapshotBuffers());
        long large = BytesPerFrame(4000, new HudSnapshotBuffers());

        // Before HUD-11 each Create copied context.Contacts.ToArray(), so the
        // 4000-contact frame would allocate hundreds of KB more than the
        // 4-contact one. With the reused buffer the per-frame cost is flat.
        _output.WriteLine($"HUD11 allocation/frame: 4 contacts {small} B, 4000 contacts {large} B");
        Assert.True(large < small + 4096,
            $"per-frame allocation scaled with contacts: 4 contacts {small} B, 4000 contacts {large} B");
    }

    // ── HUD-11 item 2: one contact pass + the shared label cache ──────────

    [Fact]
    public void HudElements_counts_contacts_in_one_pass_through_the_shared_label_cache()
    {
        var contacts = new HudContact[60];
        for (int i = 0; i < contacts.Length; i++)
            contacts[i] = Contact(i, accepted: i % 10 == 0, name: i % 3 != 0);
        var buffers = new HudSnapshotBuffers();
        var model = HudModel.Create(null, new HudContext { Contacts = contacts }, buffers);

        buffers.RadarLabels.ResetResolveCalls();
        var elements = model.Elements;
        // One Resolve per contact (HUD-10 ran nine Count(...) walks plus an
        // uncached Resolve for every contact in a separate pass).
        Assert.Equal(contacts.Length, buffers.RadarLabels.ResolveCalls);

        buffers.RadarLabels.ResetResolveCalls();
        _ = model.Elements;
        Assert.Equal(contacts.Length, buffers.RadarLabels.ResolveCalls);

        Assert.Equal(6, elements.Single(e => e.Id == "Radar accepted order brackets").Value);
        Assert.Equal(60, elements.Single(e => e.Id == "Radar ship contacts").Value);
    }

    // ── HUD-11 items 3/4/5: console blocks keep native order and reserve ──

    [Fact]
    public void Console_location_and_order_lines_use_a_full_font_line_gap()
    {
        const float lineHeight = 23f;
        float sectorLabel = HudConsoleFormat.SectorLabelTop;
        float sectorName = HudConsoleFormat.NameBelowLabel(sectorLabel, lineHeight);
        // Native translates by 14 px for its 14 px promptFont (dialog.mdl:1473-1485);
        // the port substitute font is taller, so the gap is the line height.
        Assert.True(sectorName - sectorLabel > HudConsoleFormat.SectorNameBelowLabel);

        // FONT-1c fix 4 — the order lines are native Point(0,-20)/(0,-40)
        // (dialog.mdl:1647-1648), so their text tops are +20/+40 native px from
        // the label origin, scaled with the pane. The glyph rows use the same
        // values (OrderIconTopLeft), so arrow/check and text share a row.
        float orderLabel = HudConsoleFormat.OrderLabelTop * 2f;
        float queued = HudConsoleFormat.OrderQueuedTop(orderLabel, 2f);
        float accepted = HudConsoleFormat.OrderAcceptedTop(orderLabel, 2f);
        Assert.True(orderLabel < queued && queued < accepted, "current-order block must stay in native label/queued/accepted order");
        Assert.Equal(20f * 2f, queued - orderLabel);
        Assert.Equal(40f * 2f, accepted - orderLabel);
    }

    [Fact]
    public void Edge_clamped_labels_yield_to_the_reserved_console_block()
    {
        var layout = new HudTextLayout();
        layout.Reset(1920, 1080);
        // The native top-left LastOrderImage block (dialog.mdl:5451/1605-1648):
        // "Current orders:" plus the queued/accepted lines.
        var orderBlock = new Rectangle(37, 37, 240, 3 * 23);
        layout.Reserve(orderBlock);

        var placed = layout.PlaceBlock("radar:enemy", new Rectangle(40, 40, 120, 40));

        Assert.False(placed.Intersects(orderBlock));
        Assert.True(placed.Bottom <= 1080 && placed.Right <= 1920);
    }

    [Fact]
    public void Flight_hud_draws_no_money_or_kill_block_at_the_top_left()
    {
        var frame = new HudFrame(
            HullFraction: 1f, ShieldFraction: 1f, AmmoFraction: 1f, FuelFraction: 1f,
            EnergyFraction: 1f, Power: 1f, Speed: 100f, Throttle: 1f, AfterburnerActive: false,
            Firing: false, GroupFire: true, SelectedMount: 0, Flashing: false, WarpFlash: 0f,
            Mining: false, Cloaked: false, KillBonusPercent: 50f,
            RollDegrees: 0f, WeaponSystems: Array.Empty<WeaponStatusRow>(), IsAdriftLifepod: false,
            OxygenSecondsRemaining: 0, Money: 500, MouseStick: null, TimeDilationPercent: 100f,
            WarningMask: 0, ControlState: 0, SignaturePercent: 50f, Detected: true,
            AutopilotEngaged: false, VectorLockEngaged: false, CarriedFlag: false,
            CarriedArtifact: false, TurnYaw: 0f, TurnPitch: 0f);
        var console = new HudConsoleText
        {
            SectorName = "Fejfar",
            AcceptedOrder = "defend Garrison",
            QueuedOrder = "attack Enemy fighter",
        };
        var model = HudModel.Create(frame, new HudContext { Console = console });
        var elements = model.Elements;

        // HUD-10 removed the flight credits readout (moneyPane is investment-pane
        // only, dialog.mdl:5199-5203,5368); the old port "N KILLS / KILL BONUS /
        // MONEY" block is gone, so the native top-left order block has no
        // competing row to shuffle. Kills/lives were never on the wire.
        Assert.True(elements.Single(e => e.Id == "Money credits").Available);
        Assert.False(elements.Single(e => e.Id == "Money credits").Visible);
        Assert.False(elements.Single(e => e.Id == "Lives remaining").Available);
        Assert.False(elements.Single(e => e.Id == "Mission kills").Available);
        Assert.Equal(37f, HudConsoleFormat.OrderLabelTop);
        Assert.Equal(37f, HudConsoleFormat.OrderLabelX);
    }

    // ── HUD-11 item 6: configured chat-pane height for banner offsets ─────

    [Fact]
    public void Banner_offsets_use_the_configured_chat_pane_height_not_the_shown_line_count()
    {
        var chat = new HudChat(new[] { "one", "two" }, 0, "", false, 0, "", PaneLines: 9);

        Assert.Equal(9, HudConsoleFormat.BannerChatLines(chat));
        Assert.Equal(HudConsoleFormat.VoteTop(9), HudConsoleFormat.VoteTop(HudConsoleFormat.BannerChatLines(chat)));
        Assert.NotEqual(HudConsoleFormat.VoteTop(2), HudConsoleFormat.VoteTop(HudConsoleFormat.BannerChatLines(chat)));
        // Native default ChatLines 7 when no snapshot/chat pane is supplied.
        Assert.Equal(ChatPaneLayout.DefaultLines, HudConsoleFormat.BannerChatLines(null));
        Assert.Equal(7, HudConsoleFormat.BannerChatLines(new HudChat(Array.Empty<string>(), 0, "", false, 0, "")));
    }

    [Fact]
    public void Configured_chat_lines_are_clamped_and_capped_at_six_in_the_loadout_view()
    {
        Assert.Equal(7, ChatPaneLayout.ConfiguredLines(7));
        Assert.Equal(1, ChatPaneLayout.ConfiguredLines(0));
        Assert.Equal(10, ChatPaneLayout.ConfiguredLines(99));
        Assert.Equal(6, ChatPaneLayout.ConfiguredLines(9, loadoutView: true));
        Assert.Equal(4, ChatPaneLayout.ConfiguredLines(4, loadoutView: true));
    }

    // ── HUD-11 item 7: native inventory stats slot fits at 1080p ──────────

    [Fact]
    public void Native_inventory_stats_string_fits_its_stats_slot_at_1080p()
    {
        string stats = "1250m / 20 X 5";   // invsweapon.mdl StatsBar shape
        float slot = (InventoryPaneLayout.LineWidth - InventoryPaneLayout.StatsX - 3f)
            * InventoryPaneLayout.Scale(1080);
        // Deliberately wide substitute glyphs (native medVerdana is narrower).
        static float Measure(string text) => text.Length * 13f;

        float scale = InventoryPaneLayout.FitFontScale(stats, slot, Measure);
        string fitted = HudFormat.FitToWidth(stats, t => Measure(t) * scale, slot);

        Assert.True(scale < 1f, "the wide substitute font should need scaling at 1080p");
        Assert.Equal(stats, fitted);   // no ellipsis: the native reading survives
        Assert.True(Measure(stats) * scale <= slot);
    }

    // ── HUD-11 item 8: chat rows and the command-bar wing caption ─────────

    [Fact]
    public void Chat_rows_are_scaled_into_the_native_line_slot()
    {
        Assert.Equal(1f, ChatPaneLayout.TextScale(12f));
        float scale = ChatPaneLayout.TextScale(23f);
        Assert.Equal(ChatPaneLayout.NativeFontHeight / 23f, scale, 3);
        Assert.True(ChatPaneLayout.NativeFontHeight * scale <= ChatPaneLayout.NativeFontHeight);
        // A 7-row pane still spans exactly the native 8 + 14*7 height.
        Assert.Equal(106, ChatPaneLayout.Height(7));
        Assert.Equal(20, ChatPaneLayout.Top(7));
    }

    [Fact]
    public void Wing_caption_is_below_the_native_combo_art()
    {
        float top = 900f;
        float caption = CommandBarLayout.WingCaptionTop(top);
        Assert.True(caption >= top + CommandBarLayout.PanelHeight);
        // The baked WING art occupies the combo rectangle (216,8)-(270,50) in
        // the panel; the caption must be outside it.
        Assert.True(caption > top + CommandBarLayout.WingTop + CommandBarLayout.WingHeight);
    }

    // ── HUD-11 item 9: plain target picks send c_cidDefault's order ───────

    [Fact]
    public void Plain_enemy_target_pick_sends_the_native_default_attack_order()
    {
        var target = new Game1.AutopilotTarget(Game1.TargetKind.Ship, 42, "enemy");
        var profile = new Game1.CommandTargetProfile(Game1.TargetKind.Ship, 42, Friendly: false,
            TargetHullAbm: null, StationSabm: null, ProbeEabm: null);

        var set = Game1.CurrentCommandSetFor(target, DataHullTypeIGC.HabmFighter,
            ownIsLayer: false, ownHasParent: false, profile);

        Assert.NotNull(set);
        Assert.Equal(OrderChange.CidAttack, set!.Value.CommandId);
        Assert.Equal(IgcObjectType.Ship, set.Value.ObjectType);
        Assert.Equal(42, set.Value.ObjectId);
    }

    [Fact]
    public void Plain_friendly_station_pick_uses_the_own_hulls_default_land_order()
    {
        var target = new Game1.AutopilotTarget(Game1.TargetKind.Station, 7, "Garrison");
        var profile = new Game1.CommandTargetProfile(Game1.TargetKind.Station, 7, Friendly: true,
            TargetHullAbm: null, StationSabm: DataStationTypeIGC.SabmLand, ProbeEabm: null);

        var set = Game1.CurrentCommandSetFor(target, DataHullTypeIGC.HabmFighter,
            ownIsLayer: false, ownHasParent: false, profile);

        Assert.Equal(OrderChange.CidGoto, set!.Value.CommandId);
        Assert.Equal(IgcObjectType.Station, set.Value.ObjectType);
    }

    [Fact]
    public void Human_on_a_miner_capable_hull_picks_an_asteroid_with_goto_not_mine()
    {
        // Merge fixup: native GetDefaultOrder switches on m_pilotType; a human is
        // c_ptPlayer, so hull Miner/Builder bits never turn a pick into Mine/Build.
        var rock = new Game1.AutopilotTarget(Game1.TargetKind.Asteroid, 5, "rock");
        var profile = new Game1.CommandTargetProfile(Game1.TargetKind.Asteroid, 5, Friendly: null,
            TargetHullAbm: null, StationSabm: null, ProbeEabm: null);
        short hullRoles = (short)(DataHullTypeIGC.HabmMiner | DataHullTypeIGC.HabmBuilder);

        Assert.Equal(OrderChange.CidMine, Game1.CurrentCommandSetFor(rock, hullRoles,
            ownIsLayer: false, ownHasParent: false, profile)!.Value.CommandId);   // raw hull bits: drone rule
        Assert.Equal(OrderChange.CidGoto, Game1.CurrentCommandSetFor(rock, Game1.PlayerPilotRoles(hullRoles),
            ownIsLayer: false, ownHasParent: false, profile)!.Value.CommandId);
    }

    [Fact]
    public void Explicit_command_selection_is_preserved_and_cluster_targets_send_nothing()
    {
        var pickup = new Game1.AutopilotTarget(Game1.TargetKind.Ship, 9, "pod", OrderChange.CidPickup);
        var profile = new Game1.CommandTargetProfile(Game1.TargetKind.Ship, 9, Friendly: true,
            TargetHullAbm: DataHullTypeIGC.HabmLifepod, StationSabm: null, ProbeEabm: null);
        Assert.Equal(OrderChange.CidPickup, Game1.CurrentCommandSetFor(pickup, DataHullTypeIGC.HabmRescue,
            false, false, profile)!.Value.CommandId);

        var cluster = new Game1.AutopilotTarget(Game1.TargetKind.Cluster, 1, "sector");
        var clusterProfile = new Game1.CommandTargetProfile(Game1.TargetKind.Cluster, 1, null, null, null, null);
        Assert.Null(Game1.CurrentCommandSetFor(cluster, DataHullTypeIGC.HabmFighter, false, false, clusterProfile));
        Assert.Null(Game1.CurrentCommandSetFor(null, DataHullTypeIGC.HabmFighter, false, false, profile));
    }
}
