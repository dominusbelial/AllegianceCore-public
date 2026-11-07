using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.Core;
using Allegiance.TestInfra;
using Allegiance.ClientCore.Training;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("Training")]
public sealed class P940BasicFlightInputTests
{
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Native_training_ogg_decodes_to_non_silent_pcm_with_the_original_duration()
    {
        Require.Artwork();
        string clip = Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.Artwork), "tm_2_01.ogg");
        var pcm = TrainingVoicePcm.Read(clip);
        using var source = new NVorbis.VorbisReader(clip);
        Assert.Equal(source.Channels, pcm.Channels);
        Assert.Equal(source.SampleRate, pcm.SampleRate);
        Assert.Equal(source.TotalSamples, pcm.Frames);
        Assert.Contains(pcm.Data, value => value != 0);
        // Regression oracle: the previous runtime fallback used a WAVE-only API.
        Assert.Throws<ArgumentException>(() => Microsoft.Xna.Framework.Audio.SoundEffect.FromFile(clip));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Installed_training_voice_plays_through_the_production_sound_loader()
    {
        Require.Artwork();
        var device = new AudioDevice();
        Skip.IfNot(device.TryInitialize(), "No audio device; real-window tests supply the null audio backend.");
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AllegianceCore.slnx")))
            root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("solution root");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string contentPath = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "bin", configuration, "net10.0", "Content");
        using var content = new Microsoft.Xna.Framework.Content.ContentManager(new Microsoft.Xna.Framework.GameServiceContainer(), contentPath);
        using var sound = new SoundManager(content, device);
        sound.Load();
        Assert.True(sound.IsLoaded);
        using var voice = sound.PlaySlideSound("tm_2_01");
        Assert.NotNull(voice);
        Assert.Equal(Microsoft.Xna.Framework.Audio.SoundState.Playing, voice.State);
        voice.Stop();
        // A repeated reminder reuses the decoded asset but owns a fresh instance.
        using var reminder = sound.PlaySlideSound("tm_2_01");
        Assert.NotNull(reminder);
        Assert.NotSame(voice, reminder);
        Assert.Equal(Microsoft.Xna.Framework.Audio.SoundState.Playing, reminder.State);
        Assert.Null(sound.PlaySlideSound("p940-missing-voice"));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Connected_basic_flight_has_one_physics_owner_and_never_nudges_at_zero_elapsed_time()
    {
        Require.Artwork();
        using var host = Allegiance.SinglePlayer.TrainingHost.Start(Allegiance.SinglePlayer.TrainingHost.BasicFlight);
        // A connected-host fixture, without a wall-clock background ticker.
        typeof(Allegiance.SinglePlayer.TrainingHost).GetField("_clientBound", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(host, true);
        Assert.True(host.Mission.NativeGotoPlanEnabled);
        for (int i = 0; i < 1200; i++) host.StepLight(.05f);
        Assert.Same(host.CommanderShip, host.Mission.GetShip(host.CommanderShip.Id));
        Assert.False(host.CommanderShip.Dead);
        host.CreateWaypoint(700, new(-200, -200, 400), 1010, 0, true);
        host.IssueCommand(host.CommanderShip.Id, TrainingMissionIds.CommandCurrent, null, 700, null, TrainingMissionIds.CmdGoto);
        host.IssueCommand(host.CommanderShip.Id, TrainingMissionIds.CommandAccepted, null, 700, null, TrainingMissionIds.CmdGoto);
        var initial = host.CommanderShip.Position;
        for (int i = 0; i < 10; i++) host.StepLight(0);
        Assert.Equal(initial, host.CommanderShip.Position);
        for (int i = 0; i < 40; i++) host.StepLight(.05f);
        Assert.NotEqual(initial, host.CommanderShip.Position);
        Assert.True(host.CommanderShip.Velocity.LengthSquared() > 0);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Simulation_cannot_observe_a_partly_installed_training_action_list()
    {
        Require.Artwork();
        using var host = Allegiance.SinglePlayer.TrainingHost.Start(Allegiance.SinglePlayer.TrainingHost.BasicFlight);
        using var connection = new Allegiance.ClientCore.Session.ConnectionSession();
        var site = new ClientTrainingSite(new ClientTrainingFacilities { World = () => connection.World, Session = connection, Host = host });
        using var between = new ManualResetEventSlim();
        using var attempting = new ManualResetEventSlim();
        using var observed = new ManualResetEventSlim();
        bool partial = false;
        var simulation = new Thread(() =>
        {
            between.Wait(); attempting.Set();
            host.RunTrainingFrame(() =>
            {
                partial = host.CommanderShip.CommandTarget?.Id == 700
                    && host.CommanderShip.AcceptedShipTarget is null;
                observed.Set();
                return false;
            });
        });
        simulation.Start();
        try
        {
            var mission = new OrderInstallationMission(site, () =>
            {
                between.Set(); Assert.True(attempting.Wait(TimeSpan.FromSeconds(5)));
                // Without frame ownership the simulated tick observes the
                // selected buoy before its accepted plan has been installed.
                observed.Wait(TimeSpan.FromSeconds(1));
            });
            var session = new TrainingSession(site, mission);
            session.Start();
            Assert.True(observed.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(partial, "simulation observed current=700 while accepted was still empty");
            Assert.Equal((short?)700, host.CommanderShip.AcceptedShipTarget?.Id);
            session.DetachClientHooks();
        }
        finally { between.Set(); Assert.True(simulation.Join(TimeSpan.FromSeconds(5))); }
    }

    private sealed class OrderInstallationMission(ITrainingSite site, System.Action between) : TrainingMission(site)
    {
        public override int GetMissionID() => 2;
        public override int GetStartSectorId() => 1010;
        protected override void CreateUniverse() { }
        protected override Condition CreateMission()
        {
            var goal = new Goal(new FalseCondition());
            var instructor = new TargetRef(ObjectType.Ship, 990);
            var buoy = new TargetRef(ObjectType.Buoy, 700);
            goal.AddStartAction(new CreateWaypointAction(Site, 700, new(-200, -200, 400), 1010));
            goal.AddStartAction(new SetCommandAction(Site, instructor, TrainingMissionIds.CommandCurrent, buoy, TrainingMissionIds.CmdGoto));
            goal.AddStartAction(new CallbackAction(between));
            goal.AddStartAction(new SetCommandAction(Site, instructor, TrainingMissionIds.CommandAccepted, buoy, TrainingMissionIds.CmdGoto));
            return goal;
        }
    }
    private sealed class CallbackAction(System.Action action) : Allegiance.ClientCore.Training.Action
    {
        public override void Execute() => action();
    }

    [Theory]
    [InlineData(Keys.Space)] [InlineData(Keys.LeftShift)]
    public void Prompted_booster_key_reaches_the_flight_action_and_training_filter(Keys key)
    {
        var map = new ActionMap(InputActionCatalog.Definitions, InputLayout.Port);
        var delivered = new List<int>();
        var gate = new TrainingInputGate(k => { delivered.Add(k); return true; });
        map.ActionFilter = gate.Approves;
        var keys = new KeyboardState(key);
        gate.Update(keys, map);
        Assert.Contains(TrainingMissionIds.KeyFireBooster, delivered);
        Assert.True(Game1.BasicFlightBoosterDown(keys, map, basicFlight: true));
        // On the Port preset SPACE retains its weapon meaning outside Basic Flight.
        Assert.Equal(key == Keys.LeftShift, Game1.BasicFlightBoosterDown(keys, map, basicFlight: false));
    }

    [Fact]
    public void Native_layout_boosts_on_space_everywhere_and_not_on_shift()
    {
        // P941 native defaults: SPACE is TK_FireBooster, the left mouse fires.
        var map = new ActionMap(InputActionCatalog.Definitions, InputLayout.Native);
        var space = new KeyboardState(Keys.Space);
        Assert.True(Game1.BasicFlightBoosterDown(space, map, basicFlight: true));
        Assert.True(Game1.BasicFlightBoosterDown(space, map, basicFlight: false));
        Assert.False(map.IsDownMostSpecific(space, "FireWeapon"));
        var shift = new KeyboardState(Keys.LeftShift);
        Assert.False(Game1.BasicFlightBoosterDown(shift, map, basicFlight: false));
    }

    [Fact]
    public void Rejected_native_booster_key_stays_rejected_until_release()
    {
        var map = new ActionMap(InputActionCatalog.Definitions, InputLayout.Port);
        bool allowed = false;
        var gate = new TrainingInputGate(_ => allowed);
        map.ActionFilter = gate.Approves;
        var space = new KeyboardState(Keys.Space);
        gate.Update(space, map);
        Assert.False(Game1.BasicFlightBoosterDown(space, map, basicFlight: true));
        allowed = true; gate.Update(space, map);
        Assert.False(Game1.BasicFlightBoosterDown(space, map, basicFlight: true));
        gate.Update(default, map); gate.Update(space, map);
        Assert.True(Game1.BasicFlightBoosterDown(space, map, basicFlight: true));
    }

    [Theory]
    [InlineData(Keys.OemBackslash)] [InlineData(Keys.OemPipe)]
    public void Both_physical_backslash_variants_reach_full_throttle_on_default_bindings(Keys key)
    {
        var map = new ActionMap(InputActionCatalog.Definitions, InputLayout.Port);
        var delivered = new List<int>();
        var gate = new TrainingInputGate(k => { delivered.Add(k); return true; });
        map.ActionFilter = gate.Approves;
        var keys = new KeyboardState(key);
        gate.Update(keys, map);
        Assert.True(map.IsDownMostSpecific(keys, "ThrottleFull"));
        Assert.Contains(DefaultInputMap.TkNumber("TK_ThrottleFull")!.Value, delivered);
    }
}
