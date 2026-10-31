// P902: shipping PCM/backend proof and real Game1 Sound Options captures.
// Run from repo root. Use a scratch XDG_CONFIG_HOME so preferences stay local.
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.SinglePlayer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;

string contentPath = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
bool noDevice = args.Contains("--no-device");
if (args.Contains("--audio-only"))
{
    using var content = new ContentManager(new GameServiceContainer(), contentPath);
    var device = new AudioDevice(diagnostic: Console.WriteLine);
    using var sound = new SoundManager(content, device);
    using var music = new Music(content, device);
    sound.Load(); music.Load();
    if (noDevice)
    {
        if (sound.IsLoaded || device.Available) throw new InvalidOperationException("Device-failure path was not forced");
        for (int i = 0; i < 180; i++)
        {
            sound.Load(); music.Load(); sound.UpdateAudioFrame(1f / 60);
            sound.PlayWeaponFire(143); sound.StartWeaponBurst(0, 144); sound.StopAllWeaponBursts();
            sound.SetStationInteriorAmbient(380); sound.PlayError(); sound.StartSoundId(143);
            sound.Update(1, 100, true, true, 1f / 60); music.Update(i % 2 == 0);
        }
        Console.WriteLine("P902_NO_DEVICE: SFX, music, repeated load/frame calls completed silently");
        return;
    }
    if (!sound.IsLoaded) throw new InvalidOperationException("Audio did not load");
    // Validate every bundled weapon's authored ASR region/repeat interval.
    var cache = new RepeatFirePcmCache();
    int checkedTemplates = 0;
    foreach (var pair in NativeSoundTemplateSettings.ByName)
    {
        var settings = pair.Value;
        if (settings.Stem is not { } stem || !File.Exists(Path.Combine(contentPath, "Sounds", stem + ".xnb"))) continue;
        var data = SoundPcm.ReadContent(contentPath, "Sounds/" + stem);
        if (settings.RepeatSeconds > 0) cache.Get(data, settings.RepeatSeconds);
        else if (settings.LoopSeconds > 0)
        {
            try { AsrRegion.FromSeconds(data, settings.LoopStartSeconds, settings.LoopSeconds); }
            catch (ArgumentOutOfRangeException)
            {
                Console.WriteLine($"P902_INVALID_NATIVE_REGION: {pair.Key} wave={stem} duration={(double)data.Frames / data.SampleRate:F6}, start={settings.LoopStartSeconds}, length={settings.LoopSeconds}");
                continue;
            }
        }
        checkedTemplates++;
    }
    Console.WriteLine($"P902_PCM: validated {checkedTemplates} bundled templates");
    var pcm = SoundPcm.ReadContent(contentPath, "Sounds/pulselaser");
    var template = cache.Get(pcm, .125f);
    using var voice = new AudioVoice(template) { Volume = 0 };
    voice.Play();
    for (int i = 0; i < 30; i++) { Thread.Sleep(16); voice.Pump(); }
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 120; i++) { Thread.Sleep(16); voice.Pump(); }
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    if (allocated != 0) throw new InvalidOperationException($"ASR refill allocated {allocated} bytes");
    voice.Release();
    var watch = Stopwatch.StartNew();
    while (voice.State != SoundState.Stopped && watch.Elapsed.TotalSeconds < 3)
    { Thread.Sleep(16); voice.Pump(); }
    if (voice.State != SoundState.Stopped) throw new InvalidOperationException("ASR release never finished");
    Console.WriteLine($"P902_ASR: sustained playback, soft release, 120 refill frames allocated={allocated}");
    sound.StartWeaponBurst(0, 144); sound.StartWeaponBurst(1, 42);
    sound.SetStationInteriorAmbient(380);
    sound.StartSound(143, new(new(-200, 0, 0), new(-1, 0, 0), new SoundCone(30, 60, -18), -6));
    for (int i = 0; i < 30; i++) { Thread.Sleep(16); sound.UpdateAudioFrame(1f / 60); }
    if (sound.Last3DVoiceCount == 0) throw new InvalidOperationException("Effect source did not enter 3D mix");
    sound.StopAllWeaponBursts();
    for (int i = 0; i < 60; i++) { Thread.Sleep(16); sound.UpdateAudioFrame(1f / 60); }
    if (!sound.IsLoaded) throw new InvalidOperationException("Manager disabled itself during playback");
    Console.WriteLine("P902_MANAGER: native ASR + repeat weapons, shipint, coned effect, release completed");
    return;
}

string output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "docs/reports/P902_assets");
Directory.CreateDirectory(output);
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, contentPath);
if (args.Contains("--flight"))
{
    using var host = await SkirmishHost.StartAsync(new()
    {
        Size = SkirmishSize.Small, TechMode = "unlocked",
        Sizing = new(ParallelBuild: true, TransportMode: "pool"),
    });
    await host.SetPlayerSideAsync(0);
    using var flightGame = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = host.Port, PlayerName = "P902 silent pilot",
        WindowWidth = 1280, WindowHeight = 720,
        AutoProof = new Allegiance.ClientCore.MonoGame.LiveProof.AutoProofPlan
            { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true },
    });
    flightGame.Content.RootDirectory = contentPath;
    flightGame.Components.Add(new NoDeviceFlightProof(flightGame, output));
    flightGame.Run();
    return;
}
using var game = new Game1(new Game1.LaunchOptions { Intro = true, PlayerName = "P902 pilot", WindowWidth = 1280, WindowHeight = 720 });
typeof(Game1).GetField("_splashStartSeconds", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(game, 1e9);
game.Content.RootDirectory = contentPath;
game.Components.Add(new SoundOptionsProof(game, output, noDevice));
game.Run();

sealed class NoDeviceFlightProof(Game1 game, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double? _flyingSince;
    private int _frames;
    private bool _shot;
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("Silent flight did not launch");
        var sound = Get<SoundManager>("_sound");
        if (sound.IsLoaded) throw new InvalidOperationException("Device failure was not forced");
        var session = Get<ConnectionSession>("_session");
        bool launched;
        lock (session.Gate)
            launched = session.World.MyShipId is { } id && session.World.Ships.TryGetValue(id, out var ship)
                && ship.StationId < 0 && ship.Latest is not null
                && session.World.Mission?.Stage == Allegiance.Core.Wire.MissionStageMessage.StageStarted;
        if (!launched || Get<object>("_state").ToString() != "Flying") return;
        _flyingSince ??= _watch.Elapsed.TotalSeconds;
        _frames++;
        sound.PlayWeaponFire(143); sound.StartWeaponBurst(0, 144); sound.StopAllWeaponBursts();
        sound.PlayError(); sound.PlayVoice("vo_sal_noammo");
        if (_watch.Elapsed.TotalSeconds - _flyingSince < 10) return;
        if (!_shot)
        {
            Set("_autoshotDir", output); Set("_autoShotName", "no-device-flight-modern"); Set("_autoShotScheduled", true);
            Console.WriteLine($"P902_SILENT_FLIGHT: mission started and launched, {_frames} live flight frames, SFX calls each frame, no audio retries");
            _shot = true;
        }
        else if (_watch.Elapsed.TotalSeconds - _flyingSince > 11) game.Exit();
    }
}

sealed class SoundOptionsProof(Game1 game, string output, bool noDevice) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);
    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 60) throw new TimeoutException("P902 options proof timeout");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        if (_step == 0)
        {
            Set("_splashStartSeconds", Get<double>("_nowSeconds"));
        }
        else if (_step == 1)
        {
            if (Get<object>("_state").ToString() != "Intro") throw new InvalidOperationException("Intro not reached");
            Set("_introIndex", 4); Call("ActivateIntroMain");
            var menu = Get<OptionsMenuModel>("_optionsMenu");
            menu.Row0 = 1; menu.Activate((OptionsMenuModel.OptionsState)Call("CurrentOptionsState")!);
            menu.Row0 = 4;
            // Normalize through the same command/persistence path as Q/Enter.
            var sound = Get<SoundManager>("_sound");
            if (noDevice && sound.IsLoaded) throw new InvalidOperationException("No-device proof has audio");
            while (((OptionsMenuModel.OptionsState)Call("CurrentOptionsState")!).AudioQuality != AudioQuality.High)
                Call("CycleOptionsAudioQuality");
        }
        else if (_step is >= 2 and <= 7)
        {
            int index = _step - 2;
            var style = index < 3 ? HudStyle.Classic : HudStyle.Modern;
            Get<HudSettings>("_hudSettings").Style = style;
            Call("CycleOptionsAudioQuality");
            var state = (OptionsMenuModel.OptionsState)Call("CurrentOptionsState")!;
            if (IntroPreferences.Load().AudioQuality != state.AudioQuality)
                throw new InvalidOperationException("Quality did not persist through production command");
            string name = $"sound-{style.ToString().ToLowerInvariant()}-{state.AudioQuality.ToString().ToLowerInvariant()}";
            if (noDevice) name += "-no-device";
            Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true);
            Console.WriteLine($"P902_SCREEN: {name}, quality={state.AudioQuality}, deviceLoaded={Get<SoundManager>("_sound").IsLoaded}");
        }
        else { game.Exit(); return; }
        _step++;
        _next = _watch.Elapsed.TotalSeconds + 1;
    }
}
