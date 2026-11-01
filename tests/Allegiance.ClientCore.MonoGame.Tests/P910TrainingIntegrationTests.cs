using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.Training;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework.Input;
using V = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("Training")]
public sealed class P910TrainingIntegrationTests : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    public P910TrainingIntegrationTests() { TrainingClock.ResetForTests(); TrainingController.EndMission(); }
    public void Dispose() { TrainingClock.ResetForTests(); TrainingController.EndMission(); TrainingOverlay.Current = 0; }
    internal static void Set(Game1 game, string field, object? value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
    internal static T Get<T>(Game1 game, string field) => (T)typeof(Game1).GetField(field, Hidden)!.GetValue(game)!;
    internal static object? Call(Game1 game, string method, params object?[] args) =>
        typeof(Game1).GetMethods(Hidden).Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(game, args);
    internal static Game1 Headless(ConnectionSession connection, HudStyle style)
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_session", connection);
        Set(game, "_hudSettings", new HudSettings { Style = style });
        Set(game, "_trainingSounds", new Dictionary<int, Microsoft.Xna.Framework.Audio.SoundEffectInstance>());
        Set(game, "_criticalTips", new List<(string, double)>());
        Set(game, "_trainingScreen", new TrainingScreenModel());
        Set(game, "_ownSim", new Allegiance.ClientCore.Flight.OwnShipSimulator());
        return game;
    }
    private static ClientWorld Seed(ConnectionSession c)
    {
        var w = c.World;
        w.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0,
            V.Zero, Orientation.Identity, V.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        w.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "Cadet" });
        w.Apply(new ExportShip { ShipId = 11, SideId = 0, CharacterName = "Commander" });
        return w;
    }
    internal sealed class ProbeMission(ITrainingSite site, bool commandView = false) : TrainingMission(site)
    {
        public int Landings, StationCalls;
        public TargetRef? Destroyed, Launcher;
        public bool EndOnLanding { get; set; }
        public override bool CommandViewEnabled => commandView;
        public override int GetMissionID() => 6;
        public override int GetStartSectorId() => 1;
        protected override void CreateUniverse() { }
        protected override Condition CreateMission() => new FalseCondition();
        public override bool ShipLanded() { Landings++; if (EndOnLanding) Terminate(); return !EndOnLanding; }
        public override void StationDestroyed(TargetRef station, TargetRef? launcher, bool lastOwnRestart)
        { StationCalls++; Destroyed = station; Launcher = launcher; base.StationDestroyed(station, launcher, lastOwnRestart); }
    }
    private static ClientTrainingSite Site(ConnectionSession c) => new(new ClientTrainingFacilities { World = () => c.World, Session = c });

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Real_chat_receiver_records_recipient_blocks_self_targets_and_uses_training_private_cue(HudStyle style)
    {
        using var c = new ConnectionSession(); var w = Seed(c); var site = Site(c);
        var game = Headless(c, style);
        var session = new TrainingSession(site, new ProbeMission(site));
        var chat = new ChatMessage { SidSender = 10, OidRecipient = 10, ChatTarget = (int)ChatTarget.Individual,
            CommandID = OrderChange.CidGoto, OtTarget = (short)IgcObjectType.Ship, OidTarget = 11, Message = "self" };
        w.ReceiveChat(chat);
        Assert.True(site.ConsumeChat((int)ChatTarget.Individual));
        Assert.False(site.ConsumeChat((int)ChatTarget.Individual));
        Assert.Equal(-1, chat.OidTarget); Assert.Equal(-1, chat.OtTarget);
        w.ReceiveChat(new ChatMessage { SidSender = 11, OidRecipient = 10, ChatTarget = (int)ChatTarget.Individual,
            CommandID = -1, Message = "lesson" });
        Assert.Contains(w.TakeSoundEvents(), e => e.Kind == ClientSoundEventKind.ChatCommander);
        session.DetachClientHooks();
        Assert.Null(w.TrainingChatRecipient);
        w.ReceiveChat(new ChatMessage { SidSender = 11, OidRecipient = 10, ChatTarget = (int)ChatTarget.Individual,
            CommandID = -1, Message = "multiplayer" });
        Assert.Contains(w.TakeSoundEvents(), e => e.Kind == ClientSoundEventKind.ChatPersonal);
        Assert.False(site.ConsumeChat((int)ChatTarget.Everyone));
        GC.KeepAlive(game);
    }

    [Fact]
    public void Repeated_chat_receipts_set_one_native_recipient_latch()
    {
        using var c = new ConnectionSession(); Seed(c); var site = Site(c);
        var session = new TrainingSession(site, new ProbeMission(site));
        for (int i = 0; i < 2; i++)
            c.World.ReceiveChat(new ChatMessage { SidSender = 11, OidRecipient = 10,
                ChatTarget = (int)ChatTarget.Individual, CommandID = -1, Message = "same channel" });
        Assert.True(site.ConsumeChat((int)ChatTarget.Individual));
        Assert.False(site.ConsumeChat((int)ChatTarget.Individual));
        session.DetachClientHooks();
    }

    [Fact]
    public void Command_view_enabled_and_normal_multiplayer_keep_self_command_targets()
    {
        using var c = new ConnectionSession(); var w = Seed(c); var site = Site(c);
        var session = new TrainingSession(site, new ProbeMission(site, true));
        foreach (bool training in new[] { true, false })
        {
            if (!training) session.DetachClientHooks();
            var chat = new ChatMessage { SidSender = 10, OidRecipient = 10, ChatTarget = 1,
                OtTarget = (short)IgcObjectType.Ship, OidTarget = 11 };
            w.ReceiveChat(chat); Assert.Equal(11, chat.OidTarget);
        }
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Training_critical_tip_replaces_only_slot_zero_and_retains_old_timestamps(HudStyle style)
    {
        using var c = new ConnectionSession(); var game = Headless(c, style);
        Call(game, "PushTip", "older", true); Set(game, "_nowSeconds", 1.0);
        Call(game, "PushTip", "current", true);
        TrainingController.BeginMission(); Set(game, "_nowSeconds", 2.0);
        Call(game, "PushTip", "training one", true); Set(game, "_nowSeconds", 3.0);
        Call(game, "PushTip", "training two", true);
        Assert.Equal(new[] { ("older", 0.0), ("training two", 3.0) }, Get<List<(string, double)>>(game, "_criticalTips"));
        TrainingController.EndMission(); Call(game, "PushTip", "normal", true);
        Assert.Equal(3, Get<List<(string, double)>>(game, "_criticalTips").Count);
    }

    [Theory]
    [InlineData(HudStyle.Classic, "quit")]
    [InlineData(HudStyle.Modern, "quit")]
    [InlineData(HudStyle.Classic, "complete")]
    [InlineData(HudStyle.Modern, "complete")]
    [InlineData(HudStyle.Classic, "timeout")]
    [InlineData(HudStyle.Modern, "timeout")]
    [InlineData(HudStyle.Classic, "disconnect")]
    [InlineData(HudStyle.Modern, "disconnect")]
    [InlineData(HudStyle.Classic, "abort-connect")]
    [InlineData(HudStyle.Modern, "abort-connect")]
    [InlineData(HudStyle.Classic, "window-close")]
    [InlineData(HudStyle.Modern, "window-close")]
    public void Every_exit_restores_preferences_keymap_and_overlay(HudStyle style, string route)
    {
        using var c = new ConnectionSession(); Seed(c); var game = Headless(c, style);
        var map = KeyBindingStore.Instance.Map;
        string original = map.ToJson(); var oldFilter = map.ActionFilter;
        try
        {
            map.SetBinding("FireWeapon", new KeyBinding(Keys.F9, InputModifiers.None), force: true);
            var saved = map.ToJson();
            Call(game, "SaveTrainingPreferences");
            var site = Site(c); var session = new TrainingSession(site, new ProbeMission(site));
            Set(game, "_trainingSession", session);
            Set(game, "_trainingMissionId", 6);
            TrainingController.BeginMission();
            map.ActionFilter = _ => false; map.SetBinding("FireWeapon", new KeyBinding(Keys.F8, InputModifiers.None), force: true);
            Get<HudSettings>(game, "_hudSettings").Style = style == HudStyle.Classic ? HudStyle.Modern : HudStyle.Classic;
            TrainingOverlay.Current = 42;
            Set(game, "_investActive", true); Set(game, "_teleportActive", true);
            switch (route)
            {
                case "quit":
                    Assert.True((bool)Call(game, "RequestTrainingQuit")!);
                    Call(game, "ConfirmTrainingQuit", false);
                    Assert.True(TrainingController.IsTraining); Assert.Equal(42, TrainingOverlay.Current);
                    Call(game, "RequestTrainingQuit"); Call(game, "ConfirmTrainingQuit", true); break;
                case "complete": session.Terminate(); Call(game, "UpdateTraining", 0.0); break;
                case "timeout": Call(game, "AbortTrainingStart", "join timeout"); break;
                case "disconnect": Assert.True((bool)Call(game, "ExitTrainingSession")!); break;
                case "abort-connect": Call(game, "AbortConnect"); break;
                case "window-close": Call(game, "ReleaseTraining"); break;
            }
            Assert.False(TrainingController.IsTraining); Assert.Equal(0, TrainingOverlay.Current);
            Assert.Equal(style, Get<HudSettings>(game, "_hudSettings").Style);
            Assert.Equal(saved, map.ToJson()); Assert.Same(oldFilter, map.ActionFilter);
            Assert.Null(Get<object?>(game, "_trainingSession")); Assert.Null(c.World.TrainingChatRecipient);
            Assert.False(Get<bool>(game, "_investActive")); Assert.False(Get<bool>(game, "_teleportActive"));
            Call(game, "ReleaseTraining"); // idempotent and inert for next multiplayer session
            Assert.False((bool)Call(game, "RequestTrainingQuit")!);
        }
        finally { map.ResetToDefaults(); map.ApplyJson(original); map.ActionFilter = oldFilter; }
    }

    [Fact]
    public void Timer_slide_advance_starts_next_voice_without_an_input_edge()
    {
        using var c = new ConnectionSession(); var game = Headless(c, HudStyle.Modern);
        var slide = new SlideshowModel([new("one", null, "p910-one"), new("two", null, "p910-two")], "test");
        Call(game, "BeginTrainingSlideshow", slide, true, 2);
        var previous = Console.Out; using var output = new StringWriter(); Console.SetOut(output);
        try
        {
            slide.OnSoundFinished();
            for (int i = 0; i < 62; i++)
            {
                Set(game, "_nowSeconds", i / 60.0);
                Call(game, "UpdateTrainingSlideshow", new KeyboardState());
            }
        }
        finally { Console.SetOut(previous); }
        Assert.Equal(1, slide.Index); Assert.Contains("slide voice p910-two", output.ToString());
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void Quitting_pre_or_post_slideshow_skips_start_and_postlude(bool post)
    {
        using var c = new ConnectionSession(); var game = Headless(c, HudStyle.Classic);
        Call(game, "BeginTrainingSlideshow", new SlideshowModel([new("one", null, "s")], "test"), post, 2);
        Assert.True((bool)Call(game, "RequestTrainingQuit")!); Call(game, "ConfirmTrainingQuit", true);
        Assert.Null(Get<SlideshowModel?>(game, "_trainingSlideshow"));
        Assert.Equal("Training", Get<object>(game, "_introView").ToString());
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Cancelled_slideshow_quit_consumes_held_escape_until_release(HudStyle style)
    {
        using var c = new ConnectionSession(); var game = Headless(c, style);
        var slides = new SlideshowModel([new("one", null, "s1"), new("two", null, "s2")], "test");
        Call(game, "BeginTrainingSlideshow", slides, true, 2);
        Call(game, "UpdateTrainingSlideshow", new KeyboardState());
        Call(game, "RequestTrainingQuit");
        Call(game, "UpdateTrainingSlideshow", new KeyboardState(Keys.Escape));
        Assert.False(Get<bool>(game, "_trainingQuitConfirm"));
        Call(game, "UpdateTrainingSlideshow", new KeyboardState(Keys.Escape));
        Assert.Equal(0, slides.Index);
        Call(game, "UpdateTrainingSlideshow", new KeyboardState());
        Call(game, "UpdateTrainingSlideshow", new KeyboardState(Keys.Space));
        Assert.Equal(1, slides.Index);
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Confirming_lesson_quit_does_not_start_the_chooser_entry_on_held_enter(HudStyle style)
    {
        using var c = new ConnectionSession(); Seed(c); var game = Headless(c, style);
        var site = Site(c); var session = new TrainingSession(site, new ProbeMission(site));
        Set(game, "_trainingSession", session);
        Get<TrainingScreenModel>(game, "_trainingScreen").Index = 0;
        TrainingController.BeginMission();
        Call(game, "RequestTrainingQuit");
        Call(game, "UpdateTrainingQuitInput", new KeyboardState(Keys.Enter));
        Assert.Null(Get<TrainingSession?>(game, "_trainingSession"));
        Call(game, "UpdateTrainingScreen", new KeyboardState(Keys.Enter), false, false, true, false);
        Assert.Null(Get<SlideshowModel?>(game, "_trainingSlideshow"));
        Assert.False(TrainingController.IsTraining);
    }

    [Theory]
    [InlineData(ShipControlState.CoastButton)] [InlineData(ShipControlState.CloakActive)]
    public void Toggles_approve_after_XOR_and_multiplayer_ignores_masks(int bit)
    {
        TrainingController.BeginMission(); TrainingControlState.AllowableActionMask = 0;
        Assert.False(Game1.ApprovedTrainingToggle(false, bit));
        TrainingControlState.SetInputAction = bit; Assert.True(Game1.ApprovedTrainingToggle(true, bit));
        TrainingController.EndMission(); Assert.True(Game1.ApprovedTrainingToggle(false, bit));
        Assert.False(Game1.ApprovedTrainingToggle(true, bit));
    }

    [Fact]
    public void Recommendation_advances_only_recommended_launch_skips_seven_and_caps_at_nanite()
    {
        var property = typeof(TrainingScreenModel).GetProperty(nameof(TrainingScreenModel.RecommendedMission))!;
        int saved = TrainingScreenModel.RecommendedMission;
        try
        {
            property.SetValue(null, 1); var screen = new TrainingScreenModel();
            screen.RecommendNext(4); Assert.Equal(1, TrainingScreenModel.RecommendedMission);
            foreach (int id in new[] { 1, 2, 3, 4, 5, 6, 8, 8 }) screen.RecommendNext(id);
            Assert.Equal(8, new TrainingScreenModel().Selected.MissionId);
            Assert.DoesNotContain(TrainingScreenModel.Missions, m => m.MissionId == 7);
            Assert.All(TrainingScreenModel.Missions, m => Assert.True(m.Playable));
        }
        finally { property.SetValue(null, saved); }
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Team_take_me_to_keeps_selection_but_gates_command_camera_in_training(HudStyle style)
    {
        using var c = new ConnectionSession(); Seed(c); var game = Headless(c, style);
        Set(game, "_commandCamera", new CommandCamera());
        foreach (bool enabled in new[] { false, true })
        {
            var site = Site(c); var session = new TrainingSession(site, new ProbeMission(site, enabled));
            Set(game, "_trainingSession", session); Set(game, "_cameraMode", CameraMode.Cockpit);
            Set(game, "_teamPaneVisible", true);
            Call(game, "TakeMeToSelected", (short)11, (short?)1);
            Assert.False(Get<bool>(game, "_teamPaneVisible"));
            Assert.Equal(enabled ? CameraMode.Command : CameraMode.Cockpit, Get<CameraMode>(game, "_cameraMode"));
            Assert.NotNull(c.World.MyCurrentCommand);
            session.DetachClientHooks();
        }
        Set(game, "_trainingSession", null); Set(game, "_cameraMode", CameraMode.Cockpit);
        Call(game, "TakeMeToSelected", (short)11, (short?)1);
        Assert.Equal(CameraMode.Command, Get<CameraMode>(game, "_cameraMode"));
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Training_freezes_donation_enablement_and_normal_multiplayer_refreshes_it(HudStyle style)
    {
        using var c = new ConnectionSession(); var world = Seed(c);
        world.Apply(new PlayerInfo { ShipId = 10, ISide = 0, CharacterName = "Cadet" });
        world.Apply(new PlayerInfo { ShipId = 11, ISide = 0, CharacterName = "Commander" }); world.MarkApplied();
        var controls = new Allegiance.ClientCore.MonoGame.Flight.TeamPaneControls();
        var map = new ActionMap(InputActionCatalog.Definitions);
        var next = map.BindingOf("TeamPaneNextPilot").Key;
        for (int i = 0; i < 2; i++) { controls.Step(new(next), map, world, true, false); controls.Step(default, map, world, true, false); }
        world.Apply(new SetMoney { ShipId = 10, Money = 0 }); controls.Step(default, map, world, true, false);
        var site = Site(c); var session = new TrainingSession(site, new ProbeMission(site));
        world.Apply(new SetMoney { ShipId = 10, Money = 200 });
        Assert.Null(controls.Step(new(map.BindingOf("InvestDonate").Key), map, world, true, false).Gift);
        session.DetachClientHooks(); controls.Step(default, map, world, true, false);
        Assert.NotNull(controls.Step(new(map.BindingOf("InvestDonate").Key), map, world, true, false).Gift);
        GC.KeepAlive(style);
    }

    [Fact]
    public void Local_echo_of_private_outgoing_chat_reaches_recipient_condition()
    {
        using var c = new ConnectionSession(); Seed(c); var site = Site(c);
        var session = new TrainingSession(site, new ProbeMission(site));
        c.World.PostOwnChatLine("hello", ChatTarget.Individual, 11);
        Assert.True(site.ConsumeChat((int)ChatTarget.Individual));
        session.DetachClientHooks();
        c.World.PostOwnChatLine("normal", ChatTarget.Individual, 11);
        Assert.False(site.ConsumeChat((int)ChatTarget.Individual));
    }

    [Fact]
    public void Native_training_CLI_switch_selects_free_flight_without_consuming_other_options()
    {
        string[] args = ["-training", "--width", "1024"];
        Assert.True(Game1.LaunchOptions.TakeTrainingSwitch(ref args));
        Assert.Equal(new[] { "--width", "1024" }, args);
        Assert.False(Game1.LaunchOptions.TakeTrainingSwitch(ref args));
    }

    [Fact]
    public void Paused_frames_do_not_consume_key_chat_or_wait_predicates()
    {
        using var c = new ConnectionSession(); Seed(c); var site = Site(c);
        var mission = new KeyMission(site); mission.Start(); mission.Evaluations = 0;
        var wait = new CountingWait(); mission.AddWaitCondition(wait);
        TrainingClock.Pause(); site.RecordKeyPress(42); site.RecordChat((int)ChatTarget.Everyone);
        Assert.False(mission.Frame()); Assert.Equal(0, mission.Evaluations); Assert.Equal(0, wait.Evaluations);
        TrainingClock.Continue(); Assert.False(mission.Frame()); Assert.Equal(1, wait.Evaluations);
        Assert.Equal(0, mission.Evaluations);
        wait.Ready = true; mission.Frame();
        Assert.False(mission.Frame()); Assert.True(mission.Frame()); Assert.Equal(1, mission.Evaluations);
        Assert.True(site.ConsumeChat((int)ChatTarget.Everyone));
    }
    private sealed class CountingWait : Condition
    {
        public int Evaluations;
        public bool Ready;
        public override bool Evaluate() { Evaluations++; return Ready; }
    }
    private sealed class KeyMission(ClientTrainingSite site) : TrainingMission(site)
    {
        public int Evaluations;
        public override int GetMissionID() => 6;
        public override int GetStartSectorId() => 1;
        protected override void CreateUniverse() { }
        protected override Condition CreateMission() => new CountCondition(this, site);
        private sealed class CountCondition(KeyMission mission, ClientTrainingSite site) : Condition
        {
            public override bool Evaluate() { mission.Evaluations++; return site.ConsumeKey(42); }
        }
    }
}
