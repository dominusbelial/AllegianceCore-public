using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework.Input;
using Kind = Allegiance.ClientCore.MonoGame.Rendering.LoadoutV2Layout.Kind;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("LiveNetwork")]
public sealed class P1018LoadoutIntegrationTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(Game1 game, string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private static object? Call(Game1 game, string name, params object?[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);

    private static (Game1 Game, ShopModel Shop, LoadoutV2Model Model, LoadoutV2Input Input) Screen(ConnectionSession session, HudStyle style)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_session", session);
        Set(game, "_hudSettings", new HudSettings { Style = style });
        Set(game, "_dockedActive", true);
        Set(game, "_cameraMode", CameraMode.Cockpit);
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "Flying"));
        var visuals = new VisualOptions();
        visuals.SetToggle(VisualFlags.LoadoutV2Key, true);
        Set(game, "_visuals", visuals);
        var hull = new DataHullTypeIGC { HullId = 7, Name = "Scout", Hardpoints = [],
            PmEquipment = Enumerable.Repeat((short)1, 8).ToArray(), PreferredPartsTypes = [], TtbmRequired = [], TtbmEffects = [] };
        var shop = new ShopModel(new ShopCatalog([hull], []), 7, []);
        var model = new LoadoutV2Model(shop);
        var input = new LoadoutV2Input();
        Set(game, "_shopModel", shop);
        Set(game, "_loadoutV2Model", model);
        Set(game, "_loadoutV2InputState", input);
        Set(game, "_loadoutV2Layout", LoadoutV2Presentation.Build(model, null, null, input).Layout(1280, 720, 1, input));
        return (game, shop, model, input);
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void V2_consumes_legacy_touch_bands_and_visible_navigation_uses_existing_actions(HudStyle style)
    {
        using var session = new ConnectionSession();
        var (game, shop, model, _) = Screen(session, style);
        var own = new State.ShipEntity { ShipId = 10 };
        var layout = Get<LoadoutV2Layout>(game, "_loadoutV2Layout");
        foreach (var band in new[] { DockedTouchZones.TeleportRect(1280, 720), DockedTouchZones.InvestRect(1280, 720) })
        {
            Assert.True((bool)Call(game, "TryLoadoutV2Tap", band.X + 1, band.Y + 1, own)!);
            Assert.False(Get<bool>(game, "_investActive"));
            Assert.False(Get<bool>(game, "_teleportActive"));
        }
        var back = layout.Elements.Single(e => e.Kind == Kind.Back);
        var undock = layout.Elements.Single(e => e.Kind == Kind.Undock);
        Assert.Equal(back, DockedTouchZones.LoadoutV2HitTest(layout, back.Bounds.CenterX, back.Bounds.CenterY));
        Set(game, "_mainMenuVisible", true);
        Assert.True((bool)Call(game, "TryLoadoutV2Tap", undock.Bounds.CenterX, undock.Bounds.CenterY, own)!);
        Assert.False(Get<bool>(game, "_launchSent"));
        Set(game, "_mainMenuVisible", false);
        Assert.True((bool)Call(game, "TryLoadoutV2Tap", undock.Bounds.CenterX, undock.Bounds.CenterY, own)!);
        Assert.True(Get<bool>(game, "_launchSent"));
        Assert.Equal(7, shop.EffectiveHullId);
        Set(game, "_launchSent", false);
        Assert.True(model.OpenSlotFor(model.SlotRows[0].Slot));
        Assert.True((bool)Call(game, "TryLoadoutV2Tap", back.Bounds.CenterX, back.Bounds.CenterY, own)!);
        Assert.Null(model.OpenSlot);
        Assert.False(Get<bool>(game, "_hangarOnly"));
        Assert.True((bool)Call(game, "TryLoadoutV2Tap", back.Bounds.CenterX, back.Bounds.CenterY, own)!);
        Assert.True(Get<bool>(game, "_hangarOnly"));
        Assert.False((bool)Call(game, "TryLoadoutV2Tap", back.Bounds.CenterX, back.Bounds.CenterY, own)!);
        Assert.Null(model.OpenSlot);
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Visuals_menu_toggle_persists_and_leaving_mid_dock_drops_stale_targets(HudStyle style)
    {
        using var session = new ConnectionSession();
        var (game, _, model, input) = Screen(session, style);
        string path = Path.Combine(Path.GetTempPath(), "p1018-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Set(game, "_playerPrefsPath", path);
            var visuals = Get<VisualOptions>(game, "_visuals");
            var state = new OptionsMenuModel.OptionsState(true, true, false, false, true, .1f, Visuals: visuals);
            var menu = new OptionsMenuModel();
            menu.Row0 = menu.Rows(state).ToList().FindIndex(r => r.Command == OptionsMenuModel.OptionsCommand.OpenVisualsMenu);
            menu.Activate(state);
            int row = menu.Rows(state).ToList().FindIndex(r => r.Label.StartsWith("Loadout V2: on - "));
            Assert.True(row >= 0);
            Assert.Contains("inline equipment", menu.Rows(state)[row].Label);
            menu.Row0 = row;
            Assert.Equal(OptionsMenuModel.OptionsCommand.ToggleVisualFlag, menu.Activate(state));
            Assert.Equal(VisualFlags.LoadoutV2Key, menu.VisualKeyAt(row, state));
            Assert.True(model.OpenSlotFor(model.SlotRows[0].Slot));
            Call(game, "ToggleVisualFlag", menu.VisualKeyAt(row, state));
            Assert.False(IntroPreferences.Load(path).Visuals!.IsOn(VisualFlags.LoadoutV2Key));
            Call(game, "UpdateLoadoutV2Input", new KeyboardState(), default(Game1.PadNav), false, new State.ShipEntity());
            Assert.Null(Get<LoadoutV2Layout?>(game, "_loadoutV2Layout"));
            Assert.Null(Get<LoadoutV2Presentation?>(game, "_loadoutV2Frame"));
            Assert.Null(input.Hover);
            Assert.Null(model.OpenSlot);
            Assert.False((bool)Call(game, "LoadoutV2CloseOnEscape")!);
            Call(game, "ToggleVisualFlag", VisualFlags.LoadoutV2Key);
            Assert.True(IntroPreferences.Load(path).Visuals!.IsOn(VisualFlags.LoadoutV2Key));
            Assert.False((bool)Call(game, "LoadoutV2CloseOnEscape")!);
            Assert.False(new VisualOptions().IsOn(VisualFlags.LoadoutV2Key));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Suspending_a_picker_resets_view_state_but_preserves_held_device_edges(HudStyle style)
    {
        using var session = new ConnectionSession();
        var (_, _, model, input) = Screen(session, style);
        var layout = input.Arrange(1280, 720, 1, 1, model.SlotRows.Count, 0, -1, 0);
        input.Step(model, layout, true, new(CycleFocus: true), null);
        input.Step(model, layout, true, default, null);
        input.Step(model, layout, true, new(Activate: true), null);
        Assert.NotNull(model.OpenSlot);
        input.Suspend(model);
        Assert.Null(model.OpenSlot);
        Assert.Equal(LoadoutV2Column.Hulls, input.Column);
        input.Step(model, null, false, new(Activate: true), new(10, 10, true, 120));
        var result = input.Step(model, layout, true, new(Activate: true), new(10, 10, true, 120));
        Assert.Equal(LoadoutV2Request.None, result.Request);
        Assert.Null(model.OpenSlot);
        Assert.Null(input.Hover);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Fresh_seat_publishes_real_inventory_to_both_loadout_presentations(HudStyle style)
    {
        Require.Artwork();
        using var server = new global::Allegiance.Server.Server { SpawnInitialMinersAtBoot = false };
        server.Boot(new global::Allegiance.Server.ServerConfig
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
            SideSelectAutoSeatSeconds = 30,
            Games = [new() { Name = "P1018 first dock", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        server.Missions[0].Stage = MissionStage.Started;
        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "First dock pilot");
        session.SendLogonRequest(new LogonRequest
        {
            CharacterName = "First dock pilot", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL",
            MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer, Time = 1018,
            DwCookie = uint.MaxValue, CrcFileList = -1, SteamAuthTicket = new byte[16],
            SteamAuthTicketLength = 0, SteamID = 98900000000001018, DrmHash = "p1018",
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        State.ShipEntity? own = null;
        while (watch.Elapsed.TotalSeconds < 15)
        {
            server.TickSim();
            lock (session.Gate)
            {
                if (session.World.MyShipId is { } id && session.World.Ships.TryGetValue(id, out own)
                    && own.StationId >= 0 && own.Loadout is { Count: > 0 }) break;
            }
            Thread.Sleep(1);
        }
        lock (session.Gate)
        {
            Assert.NotNull(own);
            Assert.True(own.StationId >= 0);
            Assert.NotEmpty(own.Loadout!);
            var actual = server.Missions[0].GetShip(own.ShipId)!;
            var inventory = actual.ExportInventoryForAck();
            Assert.Equal(inventory.Where(p => p.MountId < 128), own.Loadout!);
            Assert.Equal(inventory.Where(p => p.MountId >= 128), own.Cargo!);
            Assert.Equal(actual.Weapons.Count, actual.Parts.Count(p => p.EquipmentType == EquipmentType.Weapon));
            Assert.All(actual.Parts, part => Assert.True(part.FreeSpawn));
            var catalog = ShopCatalog.TryLoad(ExternalPaths.Resolve(ExternalResourceKind.Artwork))!;
            var shop = new ShopModel(catalog, own.HullId, own.Loadout!.ToArray(), session.World.MyMoney ?? 0,
                currentHullPaid: session.World.OwnHardware.HullPaid(own.HullId),
                currentFreeParts: session.World.OwnHardware.FreeParts.ToArray());
            var model = new LoadoutV2Model(shop);
            Assert.Contains(model.SlotRows, row => row.Filled);
            Assert.Equal(own.Loadout, shop.CurrentParts);
            var frame = LoadoutV2Presentation.Build(model, null, null);
            string weaponName = catalog.Parts.First(p => p.PartId == own.Loadout![0].PartId).Name;
            Assert.Contains(frame.Slots, row => row.Detail[0].Text == weaponName);
            Assert.Equal(0, shop.CurrentShipValue);
            Assert.Equal(style, new HudSettings { Style = style }.Style);
            Assert.Equal(0, shop.Cost);
        }
    }

}
