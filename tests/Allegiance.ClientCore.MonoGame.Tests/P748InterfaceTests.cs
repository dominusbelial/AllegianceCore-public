using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("LiveNetwork")]
public sealed class P748InterfaceTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Set(Game1 game, string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private static T Get<T>(Game1 game, string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private static object? Call(Game1 game, string name, params object[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);

    private static Game1 Headless(ConnectionSession session)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1)); GC.SuppressFinalize(game);
        Set(game, "_session", session);
        Set(game, "_hangarLaunch", new HangarLaunchSequence());
        Set(game, "_hangarEdges", new ActionPressEdges());
        Set(game, "_consolePressEdges", new ActionPressEdges());
        var state = typeof(Game1).GetField("_state", Hidden)!;
        state.SetValue(game, Enum.Parse(state.FieldType, "Flying"));
        Set(game, "_dockedActive", true);
        return game;
    }

    private static void OwnShip(ClientWorld world, short parent = -1)
    {
        world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0, Vector3.Zero,
            Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        world.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "P748 pilot" });
        world.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        world.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = 42, ParentId = parent,
            State = ShipStatusMessage.StateDocked });
        world.Apply(new MissionDef { Stage = (int)MissionStage.Started });
        world.Apply(new EnterGame());   // P757: BOARD_NACK only counts in game (native appmsg.cpp:1900)
        world.Apply(new SetMoney { ShipId = 10, Money = 10000 });
        world.Apply(new ExportStation { Station = new DataStationIGC([0, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 0],
            7, 0, 42, 1, 255, 255, "station in remote sector") });
    }

    [Theory]
    [InlineData(1280, 720)] [InlineData(1920, 1080)] [InlineData(2560, 1440)]
    public void Every_hangar_button_has_distinct_hit_geometry_and_native_hover_leave_values(int width, int height)
    {
        var actions = HangarControls.Actions(true).ToArray();
        Assert.Equal(10, actions.Length);   // P759 added Buy + Default
        foreach (var action in actions)
        {
            var rect = HangarControls.Bounds(width, height, action);
            Assert.Equal(action, HangarControls.Hit(width, height, rect.Center.X, rect.Center.Y, true));
            Assert.NotEmpty(HangarControls.Feedback(action));
            Assert.DoesNotContain(actions.Where(a => a != action), a => rect.Intersects(HangarControls.Bounds(width, height, a)));
        }
        Assert.Equal(HangarAction.None, HangarControls.Hit(width, height, 0, 0, true));
        Assert.Equal("", HangarControls.Feedback(HangarAction.None));
        var invest = HangarControls.Bounds(width, height, HangarAction.Invest);
        Assert.Equal(HangarAction.None, HangarControls.Hit(width, height, invest.Center.X, invest.Center.Y, false));
        Assert.DoesNotContain(HangarAction.Invest, HangarControls.Actions(false));
    }

    [Fact]
    public void Hangar_dispatcher_opens_team_help_and_loadout_banishes_other_panes_and_respects_development_gate()
    {
        using var session = new ConnectionSession(); OwnShip(session.World);
        var game = Headless(session);
        Set(game, "_investActive", true); Set(game, "_teleportActive", true);
        Call(game, "ActivateHangar", HangarAction.Team);
        Assert.True(Get<bool>(game, "_teamPaneVisible"));
        Assert.False(Get<bool>(game, "_investActive")); Assert.False(Get<bool>(game, "_teleportActive"));
        Call(game, "ActivateHangar", HangarAction.Help);
        Assert.True(Get<bool>(game, "_helpVisible"));
        Call(game, "ActivateHangar", HangarAction.Loadout);
        Assert.False(Get<bool>(game, "_helpVisible")); Assert.False(Get<bool>(game, "_hangarOnly"));
        Assert.False(Get<bool>(game, "_teamPaneVisible"));
        session.World.Apply(new ResearchBriefing { Settings = new ResearchSettings { AllowDevelopments = false } });
        Call(game, "ActivateHangar", HangarAction.Invest); Assert.False(Get<bool>(game, "_investActive"));
        session.World.Apply(new ResearchBriefing());
        Call(game, "ActivateHangar", HangarAction.Invest); Assert.True(Get<bool>(game, "_investActive"));
        Call(game, "ActivateHangar", HangarAction.Invest); Assert.False(Get<bool>(game, "_investActive"));
        session.World.Apply(new ShipStatusMessage { ShipId = 10, StationId = -1, ParentId = -1, SectorId = 1 });
        Call(game, "ActivateHangar", HangarAction.Exit); Assert.False(Get<bool>(game, "_hangarQuitConfirm"));
    }

    [Fact]
    public void Quit_popup_owns_escape_and_enter_edges_and_does_not_confirm_a_key_held_before_opening()
    {
        using var session = new ConnectionSession(); OwnShip(session.World); var game = Headless(session);
        Assert.False((bool)Call(game, "HandleHangarQuitInput", new KeyboardState(Keys.Enter), false)!);
        Call(game, "ActivateHangar", HangarAction.Exit);
        Assert.True((bool)Call(game, "HandleHangarQuitInput", new KeyboardState(Keys.Enter), false)!);
        Assert.True(Get<bool>(game, "_hangarQuitConfirm"));
        Call(game, "HandleHangarQuitInput", new KeyboardState(Keys.Escape), false);
        Assert.False(Get<bool>(game, "_hangarQuitConfirm"));
        Assert.Equal("Flying", Get<object>(game, "_state").ToString());
        Call(game, "HandleHangarQuitInput", new KeyboardState(), false);
        Call(game, "ActivateHangar", HangarAction.Exit);
        Call(game, "HandleHangarQuitInput", new KeyboardState(Keys.Enter), true);
        Call(game, "HandleHangarQuitInput", new KeyboardState(Keys.Enter), false);
        Assert.True(Get<bool>(game, "_hangarQuitConfirm"));
        Call(game, "HandleHangarQuitInput", new KeyboardState(), false);
        Call(game, "HandleHangarQuitInput", new KeyboardState(Keys.Enter), false);
        Assert.False(Get<bool>(game, "_hangarQuitConfirm"));
    }

    private sealed class Site : ITransportSite
    {
        public readonly TaskCompletionSource<ConnectionId> Joined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<byte[]> Received = new();
        public void OnPeerJoined(ConnectionId id, string name) => Joined.TrySetResult(id);
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId id, ReadOnlySpan<byte> bytes)
        { foreach (var message in FedPacket.Unpack(bytes)) Received.Enqueue(message); }
    }

    private static async Task Until(Func<bool> done)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!done()) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task Command_uses_docked_station_sector_and_quit_requires_confirmation_and_authoritative_departure()
    {
        var site = new Site(); var host = new TcpTransport(site); using var session = new ConnectionSession();
        host.Host(0); session.Connect(host.BoundPort, "P748 controls");
        await site.Joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
        OwnShip(session.World); var game = Headless(session);
        Call(game, "ActivateHangar", HangarAction.Command);
        await Until(() => site.Received.Any(b => FedMessageHeader.Read(b).Fmid == ViewClusterRequest.Fmid));
        var view = ViewClusterRequest.Decode(site.Received.Single(b => FedMessageHeader.Read(b).Fmid == ViewClusterRequest.Fmid));
        Assert.Equal(7, view.ClusterId);
        Assert.Equal(CameraMode.Command, Get<CameraMode>(game, "_cameraMode"));
        Call(game, "ActivateHangar", HangarAction.Exit);
        Assert.True(Get<bool>(game, "_hangarQuitConfirm"));
        Call(game, "ConfirmHangarQuit", false);
        Assert.DoesNotContain(site.Received, b => FedMessageHeader.Read(b).Fmid == QuitMission.Fmid);
        Call(game, "ActivateHangar", HangarAction.Exit);
        Call(game, "ConfirmHangarQuit", true);
        await Until(() => site.Received.Any(b => FedMessageHeader.Read(b).Fmid == QuitMission.Fmid));
        var quit = QuitMission.Decode(site.Received.Single(b => FedMessageHeader.Read(b).Fmid == QuitMission.Fmid));
        Assert.Equal(10, quit.ShipId); Assert.Equal(QuitSideReason.Quit, quit.Reason);
        Assert.Equal("Flying", Get<object>(game, "_state").ToString());
        Assert.False(Get<bool>(game, "_hangarQuitConfirm"));
        }
        finally { host.Shutdown(); }
    }

    [Fact]
    public async Task Boarded_launch_sends_only_leave_until_ack_then_one_buy_with_pending_loadout_and_cancels_on_undock()
    {
        var site = new Site(); var host = new TcpTransport(site); using var session = new ConnectionSession();
        host.Host(0); session.Connect(host.BoundPort, "P748 boarded");
        await site.Joined.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
        OwnShip(session.World, parent: 11); var game = Headless(session);
        var parts = new[] { new ExpandedPartData(3, 1, 0, 255) };
        Set(game, "_shopModel", new ShopModel(ShopCatalog.Empty, 501, parts, 10000));
        Call(game, "ActivateHangar", HangarAction.Launch);
        Call(game, "ActivateHangar", HangarAction.Launch);
        await Until(() => site.Received.Any(b => FedMessageHeader.Read(b).Fmid == BoardShip.Fmid));
        Assert.Single(site.Received, b => FedMessageHeader.Read(b).Fmid == BoardShip.Fmid);
        Assert.Equal(-1, BoardShip.Decode(site.Received.Single(b => FedMessageHeader.Read(b).Fmid == BoardShip.Fmid)).SidParent);
        Assert.DoesNotContain(site.Received, b => FedMessageHeader.Read(b).Fmid == BuyLoadoutRequest.Fmid);
        var sequence = Get<HangarLaunchSequence>(game, "_hangarLaunch");
        Assert.False(sequence.Acknowledged(11, true));
        session.World.Apply(new LeaveShip { SidChild = 10 });
        Call(game, "ContinueHangarLaunch", session.World.Ships[10]);
        Assert.False(sequence.AwaitingDisembark);
        Call(game, "ActivateHangar", HangarAction.Launch);
        await Until(() => site.Received.Any(b => FedMessageHeader.Read(b).Fmid == BuyLoadoutRequest.Fmid));
        var buy = BuyLoadoutRequest.Decode(site.Received.Single(b => FedMessageHeader.Read(b).Fmid == BuyLoadoutRequest.Fmid));
        Assert.True(buy.FLaunch); Assert.Equal(501, buy.HullId); Assert.Equal(parts, buy.Parts);
        Assert.True(Get<bool>(game, "_launchSent")); Assert.Equal(1, Get<float>(game, "_throttle"));
        sequence.Begin(11); Assert.False(sequence.Acknowledged(-1, false)); Assert.False(sequence.AwaitingDisembark);
        sequence.Begin(11); sequence.Cancel(); Assert.False(sequence.Acknowledged(-1, true));
        Set(game, "_launchSent", false);
        session.World.Apply(new ShipStatusMessage { ShipId = 10, SectorId = 1, StationId = 42, ParentId = 11 });
        Call(game, "ActivateHangar", HangarAction.Launch);
        Assert.True(sequence.AwaitingDisembark);
        session.World.Apply(new BoardNack { SidRequestedParent = -1 });
        Call(game, "ContinueHangarLaunch", session.World.Ships[10]);
        Assert.False(sequence.AwaitingDisembark);
        Assert.False(Get<bool>(game, "_launchSent"));
        Assert.Single(site.Received, b => FedMessageHeader.Read(b).Fmid == BuyLoadoutRequest.Fmid);
        }
        finally { host.Shutdown(); }
    }

    private static InvestmentBucket Dev(short id, int group = 0) => new(id, BucketItemType.BitDevelopment,
        $"development {id}", 100, 10, -1, -1, id, -1, "A real description with cost and build time.", group);

    [Fact]
    public void Investment_tabs_preserve_filters_reset_selection_wrap_skip_disabled_groups_and_show_selected_details()
    {
        var a = Dev(1); var b = Dev(2); var c = Dev(3, 3);
        var model = new InvestmentPaneModel([a, b, c], null, true, true, 100, selectedGroup: 0, groups: [0, 1, 3]);
        model.MoveCursor(1); Assert.Equal(b, model.Current);
        Assert.False(model.GroupEnabled(1));
        model.CycleGroup(1); Assert.Equal(c, Assert.Single(model.Items)); Assert.Equal(0, model.Cursor);
        var empty = new InvestmentPaneModel([], null, true, true, 100, selectedGroup: 0);
        Assert.Null(empty.Current); Assert.Empty(InvestmentLayout.Detail(empty));
        var detail = InvestmentLayout.Detail(model);
        Assert.Contains(detail, r => r.Text == c.Name); Assert.Contains(detail, r => r.Text == "Cost: $100   Time: 10 Sec.");
        Assert.Contains(detail, r => r.Text.Contains("A real description"));
        Assert.Contains(InvestmentLayout.Build(model), r => r.Text.Contains("[TACTICAL]"));
        model.ToggleShowAll(); model.ToggleHideCompleted(); model.CycleGroup(1);
        Assert.Equal(0, model.SelectedGroup); Assert.True(model.ShowAll); Assert.True(model.HideCompleted);
        model.CycleGroup(-1); Assert.Equal(3, model.SelectedGroup);
        model.SelectGroup(2); Assert.Equal(3, model.SelectedGroup);
        model.SelectGroup(1); Assert.Equal(3, model.SelectedGroup);
    }

    [Fact]
    public void Investment_tab_input_obeys_rebinding_and_suppresses_held_keys_across_chat_and_closed_panes()
    {
        var input = new InvestmentTabsInput(); var map = new ActionMap(InputActionCatalog.Definitions);
        map.SetBinding("InvestNextTab", new(Keys.F18, InputModifiers.Control), force: true);
        var down = new KeyboardState(Keys.F18, Keys.LeftControl);
        Assert.Equal(0, input.Step(down, map, false)); Assert.Equal(0, input.Step(down, map, true));
        Assert.Equal(0, input.Step(default, map, true)); Assert.Equal(1, input.Step(down, map, true));
        Assert.Equal(0, input.Step(down, map, true)); input.Step(default, map, true);
        Assert.Equal(-1, input.Step(default, map, true, previousShoulder: true));
        Assert.Equal(0, input.Step(default, map, true, previousShoulder: true));
        input.Step(default, map, false, nextShoulder: true);
        Assert.Equal(0, input.Step(default, map, true, nextShoulder: true));
    }

    [Fact]
    public void Continued_buyability_is_independent_of_completion_while_hide_completed_and_predecessors_still_filter()
    {
        var a = Dev(1); var b = Dev(2);
        var funding = new Dictionary<short, ClientWorld.BucketFunding> { [1] = new(100, 10000), [2] = new(100, 10000) };
        var model = new InvestmentPaneModel([a, b], funding, true, true, 100);
        Assert.False(model.HideCompleted);
        Assert.True(model.CanBuy(a)); Assert.Contains(a, model.Items); Assert.False(model.CanInvest(a));
        Assert.True(model.GroupEnabled(0));
        model.ToggleHideCompleted(); Assert.Empty(model.Items); Assert.Equal(0, model.Cursor);
        Assert.True(model.GroupEnabled(0)); // Native tab enabling ignores Hide Completed.
        var locked = new InvestmentPaneModel([a, b], null, true, true, 100, canBuy: x => x.Id == 1,
            predecessors: new Dictionary<short, short> { [2] = 1 });
        Assert.Equal(a, Assert.Single(locked.Items)); locked.ToggleShowAll();
        Assert.Contains(b, locked.Items); Assert.False(locked.CanInvest(b));
        locked.MoveCursor(1); locked.ToggleShowAll(); Assert.Equal(0, locked.Cursor); Assert.Equal(a, locked.Current);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Production_catalog_retains_native_groups_and_predecessor_completion_unlocks_the_next_queue_row()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.Artwork), "PCore001.igc")));
        // PCore001 has no matching attribute-level predecessor pair. Add two
        // controlled native-format developments to exercise the production path.
        var gas = Enumerable.Repeat(1f, 25).ToArray(); gas[0] = 1.1f;
        var first = new DataDevelopmentIGC(100, 10, "", "", "P748 speed 1", "first level", 0,
            new byte[50], new byte[50], gas, 30000, 3);
        var second = first with { DevelopmentId = 30001, Price = 200, Name = "P748 speed 2" };
        var developments = (IDictionary<short, DataDevelopmentIGC>)core.Developments;
        developments.Add(first.DevelopmentId, first); developments.Add(second.DevelopmentId, second);
        using var session = new ConnectionSession(); OwnShip(session.World); var game = Headless(session);
        Set(game, "_shopCatalog", ShopCatalog.FromCore(core)!);
        var all = Enumerable.Repeat((byte)255, 50).ToArray();
        // Empty initial mask synthesizes the entire real catalog; live research
        // unlocks purchases separately through the authoritative side-tech message.
        session.World.Apply(new CreateBuckets());
        session.World.Apply(new SideTechChange { SideId = 0, TtbmDevelopments = all });
        var defs = (IReadOnlyList<InvestmentBucket>)Call(game, "BuildInvestDefs")!;
        Assert.True(defs.Count > 100);
        foreach (var b in defs)
            Assert.Equal(b.ItemType switch
            {
                BucketItemType.BitDevelopment => core.Developments[b.DevelopmentId].GroupId,
                BucketItemType.BitDroneType => core.DroneTypes[b.DroneTypeId].GroupId,
                _ => core.StationTypes[b.StationTypeId].GroupId
            }, b.GroupId);
        var predecessors = InvestmentTechGates.Predecessors(core.Developments, defs);
        var pair = predecessors.First(p => defs.Any(b => b.Id == p.Key && b.GroupId is >= 0 and <= 5));
        var current = defs.Single(b => b.Id == pair.Key); var prior = defs.Single(b => b.Id == pair.Value);
        Set(game, "_investDefs", defs); Set(game, "_investGroup", current.GroupId);
        InvestmentPaneModel Snapshot() => (InvestmentPaneModel)Call(game, "BuildInvestmentModel")!;
        Assert.DoesNotContain(current, Snapshot().Items);
        session.World.Apply(new BucketStatus { SideId = 0, IBucket = prior.Id, MoneyTotal = prior.Price,
            TimeTotal = prior.TimeToBuildSec * 1000 });
        var unlocked = Snapshot();
        Assert.True(unlocked.CanBuy(current)); Assert.Contains(current, unlocked.Items);
        Assert.All(unlocked.Items, b => Assert.Equal(current.GroupId, b.GroupId));
        session.World.Apply(new SideTechChange { SideId = 0, TtbmDevelopments = new byte[50] });
        var unavailable = defs.First(b => !Snapshot().CanBuy(b));
        Set(game, "_investGroup", unavailable.GroupId);
        var locked = Snapshot();
        Assert.DoesNotContain(unavailable, locked.Items);
        locked.ToggleShowAll(); Assert.Contains(unavailable, locked.Items); Assert.False(locked.CanInvest(unavailable));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Production_investment_snapshot_uses_wire_builder_identity_before_arrival_and_excludes_enemy_dead_and_other_types()
    {
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.Artwork), "PCore001.igc")));
        using var session = new ConnectionSession(); OwnShip(session.World); var game = Headless(session);
        Set(game, "_shopCatalog", ShopCatalog.FromCore(core)!);
        var types = core.StationTypes.Values.Where(t => t.AabmBuild != 0 && !core.StationTypes.ContainsKey(t.SuccessorStationTypeId)).Take(2).ToArray(); Assert.Equal(2, types.Length);
        var defs = types.Select((t, i) => new InvestmentBucket((short)(20 + i), BucketItemType.BitStationType,
            t.Name, 100, 1, -1, -1, -1, t.StationTypeId)).ToArray();
        Set(game, "_investDefs", defs); Set(game, "_investShowAll", true);
        session.World.Apply(new CreateBuckets { TtbmDevelopments = Enumerable.Repeat((byte)255, 50).ToArray() });
        session.World.Apply(new ExportStation { Station = new DataStationIGC([0, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 0],
            7, 0, 42, core.StationTypes.Keys.First(), 255, 255, "own station") });
        session.World.Apply(new ExportAsteroid { Asteroid = new DataAsteroidIGC(1, [0, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0],
            1, 1, (short)(types[0].AabmBuild | types[1].AabmBuild), 30, 100, 100, "", "", "", 1, "build rock", 255) });
        InvestmentPaneModel Snapshot() => (InvestmentPaneModel)Call(game, "BuildInvestmentModel")!;
        var m = Snapshot();
        Assert.True(m.CanInvest(defs[0])); Assert.True(m.CanInvest(defs[1]));
        session.World.Apply(new ResearchProduction { ShipId = 101, SideId = 0, StationTypeId = types[0].StationTypeId });
        m = Snapshot(); Assert.False(m.CanInvest(defs[0])); Assert.True(m.CanInvest(defs[1]));
        session.World.Apply(new ResearchProduction { ShipId = 102, SideId = 1, StationTypeId = types[1].StationTypeId });
        Assert.True(Snapshot().CanInvest(defs[1]));
        session.World.Apply(new ShipDelete { ShipId = 101 });
        Assert.True(Snapshot().CanInvest(defs[0]));
        session.World.Apply(new ExportAsteroid { Asteroid = new DataAsteroidIGC(1, [0, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0],
            1, 1, 0, 30, 100, 100, "", "", "", 1, "plain rock", 255) });
        Assert.False(Snapshot().CanInvest(defs[0]));
        Assert.False(Snapshot().CanInvest(defs[0] with { StationTypeId = -123 }));
    }
}
