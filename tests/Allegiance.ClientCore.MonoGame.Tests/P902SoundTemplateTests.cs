using System.Buffers.Binary;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P902SoundTemplateTests
{
    // Native ds3dvirtualbuffer.cpp:780-797: angles are used directly, not halved.
    [Theory]
    [InlineData(0, 0)] [InlineData(30, 0)] [InlineData(60, 0)]
    [InlineData(75, -4.5f)] [InlineData(90, -9)]
    [InlineData(120, -18)] [InlineData(180, -18)]
    public void Cone_enters_the_production_mix(float angle, float expectedDb)
    {
        var cone = new SoundCone(60, 120, -18);
        var facing = new Vector3(MathF.Cos(MathHelper.ToRadians(angle)), MathF.Sin(MathHelper.ToRadians(angle)), 0);
        var voice = new Voice3DState(1, 0, new(new Vector3(-200, 0, 0), Vector3.Zero),
            Native3DRole.ProjectileAmbient, cone, facing, -6);
        var listener = new Listener3D(Vector3.Zero, Vector3.Zero, Vector3.UnitX, true);
        var mixed = Sound3DMix.Solve(voice, listener, true);
        Assert.Equal(Sound3DModel.DbToLinear(expectedDb - 6), mixed.Loudness, 5);
        Assert.True(mixed.Audible);
        Assert.Equal(1, Sound3DMix.Solve(voice, listener, false).Loudness);
    }

    [Fact]
    public void Cone_can_make_a_voice_inaudible_but_is_skipped_at_listener()
    {
        var voice = new Voice3DState(1, 0, new(new Vector3(-200, 0, 0), Vector3.Zero),
            Native3DRole.ProjectileAmbient, new SoundCone(0, 0, -100), -Vector3.UnitX, -6);
        var listener = new Listener3D(Vector3.Zero, Vector3.Zero, Vector3.UnitX, true);
        Assert.False(Sound3DMix.Solve(voice, listener, true).Audible);
        var headRelative = Sound3DMix.Solve(voice with { Emitter = new(Vector3.Zero, Vector3.Zero) }, listener, true);
        Assert.True(headRelative.Audible);
        Assert.Equal(Sound3DModel.DbToLinear(-6), headRelative.Loudness);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SoundCone(90, 30, -6));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SoundCone(0, 360, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SoundCone(float.NaN, 360, 0));
    }

    // SoundPitchTemplate::SetPitch multiplies template/runtime factors (:304).
    [Theory]
    [InlineData(.25f, 1, -2)] [InlineData(.5f, 1, -1)]
    [InlineData(1, 1, 0)] [InlineData(2, 1, 1)] [InlineData(4, 1, 2)]
    [InlineData(.5f, 2, 0)] [InlineData(.35f, 1, -1.514573f)]
    public void Pitch_wrapper_scales_frequency(float template, float runtime, float octaves) =>
        Assert.Equal(octaves, new PitchSoundTemplate(template).Octaves(runtime), 5);

    [Fact]
    public void Pitch_is_applied_only_to_authored_templates()
    {
        Assert.Equal(.35f, NativeSoundTemplateSettings.ByName["asteroidSound"].PitchFactor);
        Assert.Equal(.5f, NativeSoundTemplateSettings.ByName["BOStarbaseInternalSound"].PitchFactor);
        Assert.Equal(.5f, NativeSoundTemplateSettings.ByName["capitalShipExteriorSound"].PitchFactor);
        Assert.Equal(1, NativeSoundTemplateSettings.Resolve(143).PitchFactor); // pulse laser is not randomized
        Assert.Throws<ArgumentOutOfRangeException>(() => new PitchSoundTemplate(.24f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PitchSoundTemplate(4.01f));
    }

    private static SoundPcm Pcm(params short[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return new(bytes, 8000, 1);
    }
    private static short[] Read(AsrCursor cursor, int frames)
    {
        byte[] buffer = new byte[frames * 2];
        int bytes = cursor.Read(buffer);
        return Enumerable.Range(0, bytes / 2).Select(i => BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i * 2))).ToArray();
    }

    [Fact]
    public void Asr_plays_attack_then_repeats_sustain_then_tail()
    {
        var cursor = new AsrCursor(Pcm(10, 20, 30, 40, 50, 60), new(2, 2));
        Assert.Equal(new short[] { 10, 20, 30, 40, 30, 40, 30 }, Read(cursor, 7));
        cursor.Stop();
        Assert.Equal(new short[] { 40, 50, 60 }, Read(cursor, 12));
        Assert.True(cursor.Finished);
        Assert.Empty(Read(cursor, 2));
    }

    [Fact]
    public void Asr_stop_during_attack_skips_sustain_and_force_stop_is_immediate()
    {
        var cursor = new AsrCursor(Pcm(10, 20, 30, 40, 50, 60), new(2, 2));
        Assert.Equal(new short[] { 10 }, Read(cursor, 1));
        cursor.Stop();
        Assert.Equal(new short[] { 20, 50, 60 }, Read(cursor, 10));
        var forced = new AsrCursor(Pcm(1, 2, 3), new(1, 1));
        forced.Stop(true);
        Assert.True(forced.Finished);
        Assert.Empty(Read(forced, 10));
    }

    [Fact]
    public void Whole_wave_adapters_keep_shipint_and_projectile_sample_order()
    {
        var pcm = Pcm(10, 20, 30);
        var cursor = new AsrCursor(pcm, AsrRegion.Whole(pcm));
        Assert.Equal(new short[] { 10, 20, 30, 10, 20, 30, 10 }, Read(cursor, 7));
        cursor.Stop();
        Assert.Equal(new short[] { 20, 30 }, Read(cursor, 8));
        Assert.Equal(new AsrRegion(1, 2), AsrRegion.FromSeconds(pcm, 1f / 8000, 2f / 8000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AsrCursor(pcm, new(2, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AsrRegion.FromSeconds(pcm, 0, 1));
    }

    [Fact]
    public void Repeating_fire_overlaps_saturates_aligns_and_caches()
    {
        var pcm = Pcm(20000, 20000, 20000, -20000, -20000, -20000);
        var cache = new RepeatFirePcmCache();
        var template = cache.Get(pcm, 2f / 8000);
        Assert.Same(template, cache.Get(pcm, 2.1f / 8000)); // same frame-aligned delay
        Assert.Equal(new AsrRegion(4, 2), template.Region);
        short[] data = Enumerable.Range(0, template.Pcm.Frames)
            .Select(i => BinaryPrimitives.ReadInt16LittleEndian(template.Pcm.Data.AsSpan(i * 2))).ToArray();
        Assert.Equal(new short[] { 20000, 20000, short.MaxValue, 0, 20000, -20000, 0, short.MinValue, -20000, -20000 }, data);
        Assert.NotSame(template, cache.Get(pcm, 1f / 8000));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.Get(pcm, 0));
    }

    [Theory]
    [InlineData(144, .125f)] [InlineData(146, .25f)]
    [InlineData(64, .125f)] [InlineData(53, .125f)] [InlineData(67, .067f)]
    public void Native_rapid_weapons_use_repeat_pcm(short id, float seconds) =>
        Assert.Equal(seconds, NativeSoundTemplateSettings.Resolve(id).RepeatSeconds);

    [Theory]
    [InlineData(42, .100658f)] [InlineData(59, .079864f)] [InlineData(48, .082404f)]
    public void Native_asr_weapons_keep_authored_regions(short id, float length)
    {
        var template = NativeSoundTemplateSettings.Resolve(id);
        Assert.Equal(0, template.RepeatSeconds);
        Assert.Equal(.000023f, template.LoopStartSeconds);
        Assert.Equal(length, template.LoopSeconds);
    }

    [Fact]
    public void Asr_and_cache_hit_mix_math_allocate_nothing_after_warmup()
    {
        var pcm = Pcm(10, 20, 30, 40, 50, 60);
        var cache = new RepeatFirePcmCache();
        var template = cache.Get(pcm, 2f / 8000);
        var cursor = new AsrCursor(template.Pcm, template.Region);
        byte[] buffer = new byte[1024];
        for (int i = 0; i < 100; i++) { cursor.Read(buffer); cache.Get(pcm, 2f / 8000); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { cursor.Read(buffer); cache.Get(pcm, 2f / 8000); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // Native software-mixer budgets, ds3dengine.cpp:507-545; default :333.
    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Quality_menu_persists_all_three_real_budgets(HudStyle style)
    {
        var hud = new HudSettings { Style = style };
        Assert.Equal(style, hud.Style);
        string directory = Path.Combine(FindRoot(), ".scratch", "p902", "prefs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "player.json");
        try
        {
            Assert.Equal(AudioQuality.Medium, new IntroPreferences().AudioQuality);
            var menu = new OptionsMenuModel { Row0 = 1 };
            var state = new OptionsMenuModel.OptionsState(true, true, false, false, false, 0);
            menu.Activate(state);
            foreach (var (quality, desired, max) in new[] { (AudioQuality.Low, 8, 8), (AudioQuality.Medium, 16, 24), (AudioQuality.High, 24, 32) })
            {
                new IntroPreferences { AudioQuality = quality }.Save(path);
                var loaded = IntroPreferences.Load(path);
                Assert.Equal(quality, loaded.AudioQuality);
                using var content = new ContentManager(new GameServiceContainer());
                using var sound = new SoundManager(content) { Quality = loaded.AudioQuality };
                Assert.Equal(new VoiceBudgetConfig(desired, max), sound.QualityBudget);
                var rows = menu.Rows(state with { AudioQuality = quality });
                Assert.Contains(rows, r => r.Label == $"Sound Quality: {quality} ({desired} voices)");
                Assert.Equal(OptionsMenuModel.OptionsCommand.CycleAudioQuality, menu.CommandForHotkey('Q', state));
                // An unlimited category isolates the actual global voice cap.
                var candidates = Enumerable.Range(0, 40).Select(i => new BudgetSound(i, SoundCategory.Interface, 1, true, 0, false)).ToArray();
                Assert.Equal(desired, VoiceBudget.Decide(candidates, sound.QualityBudget).AdmittedIds.Count);
            }
            Assert.Equal(AudioQuality.Low, AudioQualityPresets.Next(AudioQuality.High));
            Assert.Equal(AudioQuality.Medium, AudioQualityPresets.Normalize((AudioQuality)900));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Device_failure_keeps_sfx_music_and_updates_silent_without_retries()
    {
        int attempts = 0, messages = 0;
        var device = new AudioDevice(() => { attempts++; throw new NoAudioHardwareException("forced device failure"); }, _ => messages++);
        using var content = new ContentManager(new GameServiceContainer(), "missing-audio-content");
        using var sound = new SoundManager(content, device);
        using var music = new Music(content, device);
        for (int i = 0; i < 100; i++)
        {
            sound.Load(); music.Load();
            sound.UpdateAudioFrame(.016f);
            // P1003 — the afterburner bed now takes the native loop state
            // (mounted part + its own power ramp), never the button bit.
            sound.Update(1, 100, true, new AfterburnerLoop(true, 1f, 680, 700), .016f);
            sound.PlayWeaponFire(); sound.PlayWeaponFire(143);
            sound.StartWeaponBurst(0, 144); sound.StopAllWeaponBursts();
            sound.PlayError(); sound.PlayVoice("vo_sal_noammo"); sound.StartSoundId(143);
            sound.SetStationInteriorAmbient(380); sound.SetGunnerInteriorLoop(180, true);
            sound.StartAsteroidAmbient(1, 1); sound.StopAsteroidAmbient(1);
            sound.UpdateRipcordLoop(true); sound.UpdateMiningLoop(true);
            Assert.Null(sound.PlaySlideSound("tm_slide_01"));
            music.Update(i % 2 == 0);
        }
        Assert.False(sound.IsLoaded);
        Assert.False(device.Available);
        Assert.Equal(1, attempts); Assert.Equal(1, messages);
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
