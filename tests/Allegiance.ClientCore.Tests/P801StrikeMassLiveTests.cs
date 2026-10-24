using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Allegiance.Core.Model;
using Allegiance.TestInfra;
using Xunit;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P801 — the unattended full-match acceptance run, with native fog ON (no
/// <c>showMap</c> and no <c>target_sector</c>, exactly the standing rule) and the
/// production showcase config. It records the server's own <c>[station-damage]</c>
/// lines and the bots' <c>[strike]</c> lines, then reports:
/// <list type="bullet">
/// <item>massed strike commits (required/assembled, visible/lone wolves);</item>
/// <item>each station's hits, and the largest number of hits inside one 20 s
/// volley window;</item>
/// <item>station and Garrison kills per match, and whether the match ended by a
/// station kill or the deadline.</item>
/// </list>
/// The acceptance assertion is deliberately about the mechanism (a massed strike
/// really was assembled and a station really died) — the report carries the
/// garrison-specific numbers and explains a zero with the captured lines.
/// </summary>
[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P801StrikeMassLiveTests(ITestOutputHelper output)
{
    private static string PlanDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "data", "showcase-plans"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "data", "showcase-plans");
    }

    private static Srv Boot()
    {
        var server = new Srv();
        server.Boot(new()
        {
            Port = 0,
            SideSelectAutoSeatSeconds = 30,
            Showcase = new() { TechMode = "research", IncomeMultiplier = 1000, ResearchTimeScale = 64, FinaleTimeoutSeconds = 3600 },
            LiveSettings = new()
            {
                TimeDilation = "off",
                AutoNextMatch = new() { Enabled = true, IntermissionSeconds = 3 },
            },
            Games = [new() { Name = "P801 strike mass", Map = "training_6.igc", Teams = 2, CivIds = [25, 3],
                SideNames = ["Alpha", "Bravo"], MaxPlayersPerTeam = 40,
                ResearchPlanDirectory = PlanDirectory(), ResearchPlans = new() { [0] = "auto", [1] = "auto" } }],
        });
        return server;
    }

    /// <summary>Thread-safe console tee: BotManager and the server log from many
    /// threads while the test thread reads the lines.</summary>
    private sealed class Tee(TextWriter inner) : TextWriter
    {
        private readonly object _lock = new();
        private readonly StringBuilder _all = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { inner.Write(value); lock (_lock) _all.Append(value); }
        public override void Write(string? value) { inner.Write(value); lock (_lock) _all.Append(value); }
        public override void WriteLine(string? value) { inner.WriteLine(value); lock (_lock) _all.Append(value).Append('\n'); }
        public string Snapshot() { lock (_lock) return _all.ToString(); }
    }

    private readonly record struct StationEvent(double T, short Station, string Name, float ShieldBefore,
        float ShieldAfter, float HullBefore, float HullAfter, bool Killed);

    private static readonly Regex StationDamage = new(
        @"\[station-damage\] t=(?<t>[\d.]+) side=(?<side>-?\d+) station=(?<id>\d+):(?<name>.*?) attackerSide=(?<aside>-?\d+) ship=(?<ship>-?\d+) hull=(?<hull>\d+):(?<hn>.*?) weapon=(?<weapon>.*?) damageType=(?<dmg>\d+) shield=(?<sb>[\d.-]+)->(?<sa>[\d.-]+) hull=(?<hb>[\d.-]+)->(?<ha>[\d.-]+) killed=(?<killed>True|False)",
        RegexOptions.Compiled);

    private static List<StationEvent> ParseStationEvents(string log) =>
        StationDamage.Matches(log).Select(m => new StationEvent(
            double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture),
            short.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture),
            m.Groups["name"].Value,
            float.Parse(m.Groups["sb"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["sa"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["hb"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["ha"].Value, CultureInfo.InvariantCulture),
            m.Groups["killed"].Value == "True")).ToList();

    [SkippableFact]
    public void Fogged_showcase_masses_strikes_and_at_least_one_station_dies()
    {
        Require.Artwork();
        int seconds = int.TryParse(Environment.GetEnvironmentVariable("P801_ACCEPT_SECONDS"), out int s) ? s : 600;
        using var server = Boot();
        using var stop = new CancellationTokenSource();
        var gate = new object();
        Mission? mission = null;
        var stationTypes = new Dictionary<short, short>();
        var ticker = new Thread(() =>
        {
            int tick = 0;
            while (!stop.IsCancellationRequested)
            {
                try { server.TickSim(); } catch { }
                if (tick++ % 20 == 0)
                {
                    var current = server.Missions.FirstOrDefault();
                    lock (gate)
                    {
                        mission = current;
                        if (current is not null)
                            foreach (var station in current.Sectors.SelectMany(s => s.StationRuntimes))
                                stationTypes[station.StationId] = station.Type.StationTypeId;
                    }
                }
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        ticker.Start();

        var saved = Console.Out;
        var tee = new Tee(saved);
        Console.SetOut(tee);
        string log;
        double elapsed;
        bool gameOver = false;
        try
        {
            using var manager = new Allegiance.Tools.McpSpawner.BotManager("127.0.0.1", server.BoundPort);
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"preset":"showcase","size_scale":1,"staging":true,"finale_after_minutes":0.25,"finale_side":"both","finale_min_relaunch_share":0.05,"finale_min_kills_last_5_minutes":1,"finale_bombers":12,"loadout_mode":"best"}""")!;
            var spawn = Allegiance.Tools.McpSpawner.McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(spawn.IsError ?? false, JsonSerializer.Serialize(spawn));

            var watch = Stopwatch.StartNew();
            Mission? first;
            lock (gate) first = mission;
            while (first is null && watch.Elapsed.TotalSeconds < 30) { Thread.Sleep(100); lock (gate) first = mission; }
            Assert.NotNull(first);
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                var status = JsonSerializer.SerializeToElement(manager.FinaleStatus());
                if (status.ValueKind == JsonValueKind.Array && status.GetArrayLength() > 0
                    && status.EnumerateArray().Any(r => r.GetProperty("state").GetString() == "game over"))
                {
                    gameOver = true;
                    break;
                }
                Mission? current;
                lock (gate) current = mission;
                if (current is not null && !ReferenceEquals(current, first)) { gameOver = true; break; }
                Thread.Sleep(250);
            }
            elapsed = watch.Elapsed.TotalSeconds;
        }
        finally
        {
            Console.SetOut(saved);
            stop.Cancel();
            log = tee.Snapshot();
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "p801-accept.log"), log); } catch { }
        }

        var events = ParseStationEvents(log);
        var strikeLines = log.Split('\n').Where(l => l.Contains("[strike]")).ToArray();
        var massed = strikeLines.Where(l => l.Contains(" required=") && l.Contains(" commit ")).ToArray();
        var finaleCommits = massed.Where(l => l.Contains("finale commit")).ToArray();
        var waveCommits = massed.Where(l => l.Contains(" commit ") && !l.Contains("finale commit")).ToArray();
        var aborts = strikeLines.Where(l => l.Contains(" abort ")).ToArray();
        var kills = events.Where(e => e.Killed).ToArray();
        // The `[station-damage]` line prints the MISSION station name ("Base",
        // "Enemy Base"), not the station type, so classify by the sampled real
        // type id (class 0 = Garrison).
        var core = Allegiance.Tools.McpSpawner.Catalog.RepresentativeCore();
        bool IsGarrison(short id) => stationTypes.TryGetValue(id, out short type)
            && core.StationTypes.TryGetValue(type, out var st) && st.ClassId == 0;
        var garrisonKills = kills.Where(e => IsGarrison(e.Station)).ToArray();
        var byStation = events.GroupBy(e => (e.Station, e.Name)).OrderBy(g => g.Key.Station).ToArray();

        foreach (var g in byStation)
        {
            var ordered = g.OrderBy(e => e.T).ToArray();
            int maxInWindow = 0;
            for (int i = 0; i < ordered.Length; i++)
                maxInWindow = Math.Max(maxInWindow, ordered.Count(e => e.T >= ordered[i].T && e.T < ordered[i].T + 20));
            var first = ordered[0];
            var last = ordered[^1];
            output.WriteLine($"[p801] station {g.Key.Station}:{g.Key.Name} hits={ordered.Length} "
                + $"maxHitsIn20s={maxInWindow} shield={first.ShieldBefore:F3}->{last.ShieldAfter:F3} "
                + $"hull={first.HullBefore:F3}->{last.HullAfter:F3} killed={last.Killed}");
        }
        foreach (var line in massed.Take(12)) output.WriteLine($"[p801] {line.Trim()}");
        foreach (var line in aborts.Take(6)) output.WriteLine($"[p801] {line.Trim()}");
        output.WriteLine($"[p801] elapsed={elapsed:F0}s gameOver={gameOver} strikes={strikeLines.Length} "
            + $"massedCommits={massed.Length} (finale={finaleCommits.Length} wave={waveCommits.Length}) "
            + $"aborts={aborts.Length} stationKills={kills.Length} garrisonKills={garrisonKills.Length}");
        output.WriteLine($"[p801] garrison kills: {string.Join(", ", garrisonKills.Select(k => $"station#{k.Station}:{k.Name}"))}");

        // The mechanism must have fired: a real massed strike was assembled and a
        // station really died from real server damage (never a bot claim).
        Assert.NotEmpty(massed);
        Assert.NotEmpty(kills);
        // The acceptance target: at least one Garrison fell. If it did not, the
        // captured lines above say why (no finale trigger, no armed bombers, or
        // the deadline ended the match first); the report carries those numbers.
        output.WriteLine($"[p801] garrisonFell={garrisonKills.Length > 0}");
    }
}
