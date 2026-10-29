using System.Text.RegularExpressions;
using Allegiance.Core.Audio;
using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P732VoiceCatalogTests
{
    private static string Content()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        return Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
    }

    [Fact]
    public void Every_available_voice_is_manifested_bundled_and_registered_and_missing_waves_are_explicit()
    {
        var content = Content();
        var manifest = File.ReadAllLines(Path.Combine(content, "voice-over-manifest.txt")).ToHashSet();
        var missing = File.ReadAllLines(Path.Combine(content, "voice-over-missing.txt")).ToHashSet();
        var mgcb = File.ReadAllText(Path.Combine(content, "Content.mgcb"));
        Assert.NotEmpty(manifest);
        foreach (var cue in NativeVoiceCatalog.Definitions.Values)
        {
            string path = $"Sounds/{cue.Stem}.ogg";
            Assert.Equal(cue.Available, manifest.Contains(path));
            if (cue.Available)
            {
                Assert.True(File.Exists(Path.Combine(content, path)), path);
                Assert.Contains("/build:" + path + "\n", mgcb);
            }
            else Assert.Contains(cue.Stem, missing);
        }
        Assert.Equal(new[] { "vo_ma_noimeanit", "vo_ma_woopie" }, missing.Order().ToArray());
    }

    [SkippableFact]
    public void Generated_sound_ids_and_templates_match_native_enum_and_verbatim_mdl()
    {
        Require.NativeSource();
        var native = ExternalPaths.Resolve(ExternalResourceKind.NativeSource);
        // P871b — strip C/C++ comments exactly like the native preprocessor BEFORE
        // matching: sounds.h contains a dead `/* NYI no turrets ... */` block
        // (Igc/sounds.h:443-459) whose 13 DEFSOUND lines must not shift the ids
        // of everything after it (the same bug the generator had). Block comments
        // first, then line comments.
        string sounds = Regex.Replace(
            File.ReadAllText(Path.Combine(native, "src/Igc/sounds.h")), @"/\*.*?\*/", "",
            RegexOptions.Singleline);
        sounds = Regex.Replace(sounds, @"//[^\n]*", "");
        var names = Regex.Matches(sounds, @"(?m)^DEF(?:EMPTY)?SOUND\((\w+)\)")
            .Select(m => m.Groups[1].Value + "Sound").ToArray();
        Assert.Equal(693, names.Length);   // `gcc -E` of sounds.h: 693 DEFSOUND names, ids 1001..1693
        var mdl = Regex.Replace(File.ReadAllText(Path.Combine(Content(), "sounddef.mdl")), @"//[^\n]*", "");
        foreach (var pair in NativeVoiceCatalog.Sounds.Where(p => p.Key > 1000))
        {
            var binding = Regex.Match(mdl, @"\(\s*" + names[pair.Key - 1001] + @"Id\s*,\s*(\w+)\s*\)");
            Assert.True(binding.Success, $"Unbound native ID {pair.Key}");
            Assert.Equal(binding.Groups[1].Value, pair.Value.Name);
        }
        Assert.Equal("vo_player_affirmative", NativeVoiceCatalog.Sounds[(short)(1001 + Array.IndexOf(names, "voAffirmativeSound"))].Stem);
        Assert.Equal("vo_sal_watchfire", NativeVoiceCatalog.Sounds[(short)(1001 + Array.IndexOf(names, "droneWatchFireSound"))].Stem);
        foreach (var cue in NativeVoiceCatalog.Definitions.Values)
        {
            var body = Regex.Match(mdl, @"\b" + Regex.Escape(cue.Name) + @"\s*=\s*([^;]+);").Groups[1].Value;
            Assert.Contains($"ImportWave(\"{cue.Stem}\")", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Native_gain_and_separate_sal_and_player_channels_are_preserved()
    {
        var player = NativeVoiceCatalog.Sounds[1131];
        var sal = NativeVoiceCatalog.Sounds[1324];
        Assert.Equal("MutexVO", player.Mutex);
        Assert.Equal("MutexSal", sal.Mutex);
        Assert.Equal(10, player.Timeout);
        Assert.Equal(10, player.Priority);
        Assert.Equal(-13, player.GainDb);
        Assert.Equal(-20, sal.GainDb); // VoiceOverGain plus native -7 dB.
        Assert.Equal(-8, NativeVoiceCatalog.Sounds[1035].GainDb); // SFXGain, WinTrek.cpp:2926-2927
        Assert.Equal(-8, NativeVoiceCatalog.Sounds[1023].GainDb);
        Assert.InRange(player.Gain, .2238f, .2240f);
        Assert.Equal(.1, NativeVoiceCatalog.Definitions["countdown10Sound"].Timeout);
        var ripcord = NativeVoiceCatalog.Definitions["ripcord10Sound"];
        Assert.Equal(.3, ripcord.Timeout);
        Assert.Equal(10, ripcord.Priority);
        Assert.Equal(-20, ripcord.GainDb);
    }

    private static NativeVoiceCue Cue(string name, string mutex = "MutexVO", double priority = 0, double timeout = 10) =>
        new(name, name, mutex, timeout, priority, true, -13, true);

    [Fact]
    public void Queue_serializes_each_channel_and_allows_independent_channels()
    {
        var queue = new SerializedVoiceQueue();
        Assert.Equal("first", queue.Enqueue(Cue("first"), 0)!.Name);
        Assert.Null(queue.Enqueue(Cue("second"), 1));
        Assert.Equal("sal", queue.Enqueue(Cue("sal", "MutexSal"), 1)!.Name);
        Assert.Equal("second", queue.Complete("MutexVO", 2)!.Name);
        Assert.Null(queue.Complete("MutexVO", 3));
    }

    [Fact]
    public void Queue_chooses_native_urgency_then_expires_stale_lines()
    {
        var queue = new SerializedVoiceQueue();
        queue.Enqueue(Cue("playing"), 0);
        queue.Enqueue(Cue("old"), 0);
        queue.Enqueue(Cue("important", priority: 20), 2);
        queue.Enqueue(Cue("stale", timeout: 1), 2);
        Assert.Equal("important", queue.Complete("MutexVO", 4)!.Name);
        Assert.Equal("old", queue.Complete("MutexVO", 5)!.Name);
        Assert.Null(queue.Complete("MutexVO", 6));
    }

    [Fact]
    public void Queue_suppresses_active_duplicates_and_refreshes_pending_timeout_without_changing_urgency()
    {
        var queue = new SerializedVoiceQueue();
        queue.Enqueue(Cue("playing"), 0);
        Assert.Null(queue.Enqueue(Cue("playing"), 1));
        queue.Enqueue(Cue("old"), 1);
        queue.Enqueue(Cue("new"), 2);
        queue.Enqueue(Cue("old"), 9);
        Assert.Equal("old", queue.Complete("MutexVO", 12)!.Name); // expires at 19; urgency stays 11
        Assert.Equal("new", queue.Complete("MutexVO", 12)!.Name); // exact expiration is still legal
        Assert.Null(queue.Complete("MutexVO", 12));
    }

    [Fact]
    public void Queue_reset_clears_pending_and_active_templates()
    {
        var queue = new SerializedVoiceQueue();
        queue.Enqueue(Cue("playing"), 0);
        queue.Enqueue(Cue("pending"), 1);
        queue.Clear();
        Assert.Equal("playing", queue.Enqueue(Cue("playing"), 2)!.Name);
        Assert.Null(queue.Complete("MutexVO", 2));
    }
}
