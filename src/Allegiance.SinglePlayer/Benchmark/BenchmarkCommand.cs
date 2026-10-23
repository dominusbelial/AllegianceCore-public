using Allegiance.ClientCore.Replay;
using Allegiance.ClientCore.Session;

namespace Allegiance.SinglePlayer.Benchmark;

public sealed record BenchmarkCommand(string Mode, SkirmishSize Size, string? ReplayFile, double Seconds, string Output, bool Headless, string? RecordFile, bool ExitWhenComplete = false)
{
    public const string PilotName = "p729-benchmark";
    /// <summary>P789 — the fixed Showcase is the one RANKED run: seed 729, the
    /// fixed Small size and the fixed 150 s duration, identical everywhere.
    /// The first-launch offer and the intro's tune entry both start exactly
    /// this; any other size/duration scores unranked.</summary>
    public bool IsFixedShowcase => Mode == "skirmish" && Size == SkirmishSize.Small && Seconds == 150;
    public static BenchmarkCommand? Parse(string[] args)
    {
        int index = Array.IndexOf(args, "--benchmark");
        if (index < 0) return null;
        if (index + 1 >= args.Length) throw new ArgumentException("--benchmark skirmish:small|medium|large|epic or replay[:file]");
        string value = args[index + 1];
        string Get(string flag, string fallback) { int i = Array.IndexOf(args, flag); return i < 0 ? fallback : i + 1 < args.Length ? args[i + 1] : throw new ArgumentException("Missing value for " + flag); }
        double seconds = double.Parse(Get("--benchmark-seconds", "150"), System.Globalization.CultureInfo.InvariantCulture);
        if (!double.IsFinite(seconds) || seconds < 1 || seconds > 180) throw new ArgumentException("Benchmark duration must be 1..180 seconds");
        bool replay = value == "replay" || value.StartsWith("replay:", StringComparison.Ordinal);
        if (!replay && !value.StartsWith("skirmish:", StringComparison.Ordinal)) throw new ArgumentException("Unknown benchmark mode");
        var size = replay ? SkirmishSize.Small : Enum.Parse<SkirmishSize>(value[9..], true);
        if (!Enum.IsDefined(size)) throw new ArgumentException("Unknown benchmark preset");
        return new(replay ? "replay" : "skirmish", size, replay ? (value.Length > 7 ? value[7..] : BattleReplay.DefaultPath) : null,
            seconds, Get("--benchmark-output", "benchmark.json"), args.Contains("--benchmark-headless"), Get("--benchmark-record", "") is { Length: > 0 } file ? file : null, args.Contains("--benchmark-exit"));
    }
    /// <summary>P789 — the skirmish benchmark is now the fixed Showcase: same
    /// seed (729), Showcase pacing (×5000 income / ×8 research). Parse is
    /// unchanged; only the workload's speed follows the Showcase rename.</summary>
    public SkirmishOptions Options => new() { Size = Size, Seed = 729, Speed = Mode == "replay" ? SkirmishSpeed.Normal : SkirmishSpeed.Showcase, ResearchSpeed = 8, StartAsSpectator = false };
    public static async Task<int> RunHeadlessAsync(BenchmarkCommand command)
    {
        try
        {
            var runs = new List<BenchmarkRun>();
            for (int i = 1; i <= 3; i++)
            {
                if (command.Mode == "replay")
                {
                    var replay = new BattleReplay(command.ReplayFile!);
                    using var session = new ConnectionSession();
                    var counters = new BenchmarkCounters();
                    var hashes = new List<string>();
                    while (!replay.Finished) { replay.Advance(session); hashes.Add(replay.FrameStateHash(session)); }
                    runs.Add(counters.Finish(i) with { FrameStateHashes = hashes.ToArray() });
                }
                else
                {
                    using var host = await SkirmishHost.StartAsync(command.Options);
                    BenchmarkCounters? counters = null;
                    using var recording = i == 1 && command.RecordFile is not null ? new WireRecording(command.RecordFile) : null;
                    var play = await SkirmishSmoke.PlayAsync(host, TimeSpan.FromSeconds(command.Seconds), recording: recording, benchmarkCamera: true, onBattleStarted: () =>
                    { host.BeginMeasurement(); counters = new BenchmarkCounters(host.BotProcessIds, host.Stats); });
                    if (!play.BotsAlive || !play.Running || play.Bots != host.Options.TeamSize * 2 - 1) throw new IOException("Benchmark lost its running battle");
                    runs.Add(counters!.Finish(i, host) with { Shots = play.Shots, Hits = play.Hits });
                }
            }
            if (command.Mode == "replay" && runs.Skip(1).Any(r => !r.FrameStateHashes.SequenceEqual(runs[0].FrameStateHashes)))
                throw new InvalidDataException("Replay frame-state hashes diverged");
            var median = BenchmarkResult.MedianOf(runs);
            var result = new BenchmarkResult { Mode = command.Mode, Preset = command.Size.ToString(), DurationSeconds = command.Mode == "replay" ? new BattleReplay(command.ReplayFile!).DurationMicroseconds / 1_000_000d : command.Seconds,
                ReplaySha256 = command.Mode == "replay" ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(command.ReplayFile!))) : null, Runs = runs.ToArray(), Median = median,
                Bottleneck = BenchmarkPolicy.Bottleneck(median), Recommendation = BenchmarkPolicy.Recommend(median, command.Size),
                TargetStatus = "Headless: graphics target unverified; run rendered benchmark for auto-tune",
                // P789 — the score needs MEASURED client fps; headless runs do not
                // render, so no score is fabricated. The rendered fixed Showcase
                // (intro tune entry / first-launch offer) produces the score.
                MatchKind = command.IsFixedShowcase ? "showcase-fixed" : "unranked",
                ScoreNote = "headless: not rendered; the score only uses measured client fps and time dilation" };
            result.Save(command.Output);
            Console.WriteLine($"[benchmark] {command.Mode}: 3 runs; {result.Bottleneck}; {Path.GetFullPath(command.Output)}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("[benchmark] " + ex); return 1; }
    }
}
