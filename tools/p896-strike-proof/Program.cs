using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Tools.McpSpawner;
using Srv = Allegiance.Server.Server;

// P896 local strike measurement. Real in-process server + real MCP bots on the
// rig's map (m2xspiderw.igc) with native fog, ordinary purchases, both finales.
// Bounded: --seconds caps wall time, the match's own GameLengthSeconds caps sim
// time. --swap exchanges the two sides' civilizations so "position" and
// "matchup" can be told apart (P826/P819 method).
//
// Usage: dotnet run --project tools/p896-strike-proof -- --output <dir> [--margin 0]
//        [--swap] [--label m0] [--seconds 600] [--game-seconds 1800]
//        [--finale-minutes 0.5] [--finale-seconds 300] [--size-scale 1]
//        [--count 12] [--loadout varied]
var directory = Path.GetFullPath(Option("--output", ".scratch/p896/proof"));
Directory.CreateDirectory(directory);
bool swap = args.Contains("--swap");
int wallLimit = int.Parse(Option("--seconds", "600"), CultureInfo.InvariantCulture);
int gameSeconds = int.Parse(Option("--game-seconds", "1800"), CultureInfo.InvariantCulture);
int finaleSeconds = int.Parse(Option("--finale-seconds", "300"), CultureInfo.InvariantCulture);
double finaleMinutes = double.Parse(Option("--finale-minutes", "0.5"), CultureInfo.InvariantCulture);
double margin = double.Parse(Option("--margin", "0"), CultureInfo.InvariantCulture);
int scale = int.Parse(Option("--size-scale", "1"), CultureInfo.InvariantCulture);
int? count = args.Contains("--count") ? int.Parse(Option("--count", "12"), CultureInfo.InvariantCulture) : null;
string loadout = Option("--loadout", "varied");
string map = Option("--map", "m2xspiderw.igc");
string label = Option("--label", $"{(swap ? "swapped" : "normal")}-m{margin.ToString(CultureInfo.InvariantCulture)}");
var civs = swap ? new[] { 3, 25 } : new[] { 25, 3 };

var progress = Console.Out;
var tee = new Tee(Path.Combine(directory, label + ".log"));
using var server = new Srv();
server.Boot(new()
{
    Port = 0, SideSelectAutoSeatSeconds = 30, ConsoleVerbose = true,
    Showcase = new() { TechMode = "research", IncomeMultiplier = 5000, ResearchTimeScale = 8,
        FinaleTimeoutSeconds = finaleSeconds },
    LiveSettings = new() { TimeDilation = "off", AutoNextMatch = new() { Enabled = false } },
    Games = [new() { Name = "P896 strike", Map = map, Teams = 2, CivIds = civs,
        SideNames = ["Alpha", "Bravo"], GameLengthSeconds = gameSeconds,
        MaxTeamImbalance = 32767, MaxPlayersPerTeam = 40,
        ResearchPlanDirectory = Path.GetFullPath("data/showcase-plans"),
        ResearchPlans = new() { [0] = "plan_a", [1] = "plan_a" } }],
});
using var stop = new CancellationTokenSource();
Exception? tickFailure = null;
var ticker = new Thread(() =>
{
    try { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(20); } }
    catch (Exception ex) { tickFailure = ex; }
}) { IsBackground = true };
ticker.Start();
var watch = Stopwatch.StartNew();
try
{
    using var manager = new BotManager("127.0.0.1", server.BoundPort);
    Console.SetOut(tee);
    var scenario = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
        """{"preset":"showcase","staging":true,"finale_side":"both","finale_min_relaunch_share":0,"finale_min_kills_last_5_minutes":1,"finale_bombers":8}""")!;
    scenario["size_scale"] = JsonSerializer.SerializeToElement(scale);
    if (count is { } n) { scenario["count_a"] = JsonSerializer.SerializeToElement(n); scenario["count_b"] = JsonSerializer.SerializeToElement(n); }
    scenario["finale_after_minutes"] = JsonSerializer.SerializeToElement(finaleMinutes);
    scenario["finale_survival_margin"] = JsonSerializer.SerializeToElement(margin);
    scenario["loadout_mode"] = JsonSerializer.SerializeToElement(loadout);
    var spawn = McpTools.Handle("spawn_scenario", scenario, manager);
    if (spawn.IsError == true) throw new InvalidOperationException(JsonSerializer.Serialize(spawn));
    var mission = server.Missions[0];
    // --reveal-stations: a MEASUREMENT FIXTURE only (the same seeding P733's live
    // finale test uses) so the finale arms deterministically and the strike
    // mechanism can be compared across arms. Native fog stays on for the bots'
    // worlds; this only seeds which sides may discover which stations.
    if (args.Contains("--reveal-stations"))
        foreach (var st in mission.Sectors.SelectMany(x => x.StationRuntimes))
            foreach (var sideId in mission.Sides.Select(x => x.Id))
                st.SideVisibility.SetSideVisibility(sideId, true);
    // Real map geometry (station ids, sides, warps) so route/hop symmetry between
    // the two homes can be measured from the map itself, not from a run's luck.
    File.WriteAllText(Path.Combine(directory, label + "-geometry.json"), JsonSerializer.Serialize(new
    {
        Map = map,
        Sectors = mission.Sectors.Select(s => new { s.Id, s.Name,
            Stations = s.StationRuntimes.Select(st => new { st.StationId, st.SideId, Type = st.Type.StationTypeId,
                Class = st.Type.ClassId, Position = new[] { st.Center.X, st.Center.Y, st.Center.Z } }).ToArray(),
            Warps = s.Warps.Select(w => new { w.Warp.WarpId, To = w.Destination?.Id, w.Warp.Position }).ToArray() }).ToArray(),
    }, new JsonSerializerOptions { WriteIndented = true }));
    if (args.Contains("--geometry-only"))
    {
        progress.WriteLine("geometry written: " + Path.Combine(directory, label + "-geometry.json"));
        return;
    }
    double next = 0;
    while (watch.Elapsed.TotalSeconds < wallLimit && mission.Stage != MissionStage.Over)
    {
        if (tickFailure is not null) throw new InvalidOperationException("Simulation failed", tickFailure);
        if (watch.Elapsed.TotalSeconds >= next)
        {
            progress.WriteLine($"{label}: wall={watch.Elapsed.TotalSeconds:F0}s sim={server.SimNow:F0}s "
                + string.Join(" ", mission.Sides.Select(s => $"side{s.Id}={s.Kills}/{s.Deaths}"))
                + $" finale={JsonSerializer.Serialize(manager.FinaleStatus()).Length}B");
            next += 60;
        }
        Thread.Sleep(250);
    }
    Thread.Sleep(1500);   // let the bots receive GAME_OVER
    var summary = Summarize(tee.Snapshot(), mission, manager.FinaleStatus(), label, margin, swap, server.SimNow);
    File.WriteAllText(Path.Combine(directory, label + "-summary.json"), JsonSerializer.Serialize(summary,
        new JsonSerializerOptions { WriteIndented = true }));
    File.WriteAllText(Path.Combine(directory, label + "-finale.json"), JsonSerializer.Serialize(manager.FinaleStatus(),
        new JsonSerializerOptions { WriteIndented = true }));
    progress.WriteLine(JsonSerializer.Serialize(summary));
}
finally
{
    Console.SetOut(progress);
    stop.Cancel();
    if (!ticker.Join(10_000)) throw new TimeoutException("ticker shutdown");
    tee.Dispose();
}

static object Summarize(string log, Mission mission, object[] finale, string label, double margin,
    bool swap, double simSeconds)
{
    var stationDamage = Regex.Matches(log,
        @"\[station-damage\] t=(?<t>[\d.]+) side=(?<side>-?\d+) station=(?<id>\d+):(?<name>.*?) attackerSide=(?<aside>-?\d+) ship=(?<ship>-?\d+) hull=(?<hull>\d+):(?<hn>.*?) weapon=(?<weapon>.*?) damageType=(?<dmg>\d+) shield=(?<sb>[\d.-]+)->(?<sa>[\d.-]+) hull=(?<hb>[\d.-]+)->(?<ha>[\d.-]+) killed=(?<killed>True|False)");
    var lines = log.Split('\n');
    var hits = stationDamage.Select(m => new
    {
        T = double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture),
        StationSide = short.Parse(m.Groups["side"].Value, CultureInfo.InvariantCulture),
        StationId = short.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture),
        Name = m.Groups["name"].Value.Trim(),
        AttackerSide = short.Parse(m.Groups["aside"].Value, CultureInfo.InvariantCulture),
        Ship = short.Parse(m.Groups["ship"].Value, CultureInfo.InvariantCulture),
        Weapon = m.Groups["weapon"].Value.Trim(),
        ShieldBefore = float.Parse(m.Groups["sb"].Value, CultureInfo.InvariantCulture),
        ShieldAfter = float.Parse(m.Groups["sa"].Value, CultureInfo.InvariantCulture),
        HullBefore = float.Parse(m.Groups["hb"].Value, CultureInfo.InvariantCulture),
        HullAfter = float.Parse(m.Groups["ha"].Value, CultureInfo.InvariantCulture),
        Killed = m.Groups["killed"].Value == "True",
    }).ToArray();
    var commits = lines.Where(l => l.Contains("[strike]") && l.Contains(" finale commit ")).Select(l => ParseWave(l))
        .Where(w => w is not null).Select(w => w!).ToArray();
    var releases = lines.Select((l, i) => (Line: l, Index: i))
        .Where(e => e.Line.Contains("[strike]") && e.Line.Contains(" finale release "))
        .Select(e => (Side: Side(e.Line), Role: Field(e.Line, "role"), Ship: Field(e.Line, "ship"), Index: e.Index))
        .ToArray();
    // What became of each released wave member: a real credited death after the
    // release, or still alive / censored at the end of the run.
    var releaseOutcomes = releases.Select(r => new
    {
        r.Side, r.Role, r.Ship,
        Outcome = lines.Skip(r.Index).Any(l => l.Contains("[p539d] DEATH") && l.Contains($"ship {r.Ship} ")) ? "killed" : "censored",
    }).ToArray();
    var staging = lines.Where(l => l.Contains("[strike]") && l.Contains(" finale staging ")).Select(l => new
    {
        Side = Side(l), Assembled = Int(l, "assembled"), Required = Int(l, "required"), InSector = Int(l, "inSector"),
        Docked = Int(l, "docked"), Pods = Int(l, "pods"), Unarmed = Int(l, "unarmed"), Elsewhere = Field(l, "elsewhere"),
    }).ToArray();
    var stationKills = lines.Where(l => l.Contains("[station] station ") && l.Contains(" destroyed ")).Select(l => l.Trim()).ToArray();
    var deaths = lines.Where(l => l.Contains("[p539d] DEATH")).Select(l => Side(l)).GroupBy(s => s)
        .ToDictionary(g => g.Key, g => g.Count());
    var finaleArmed = lines.Where(l => l.Contains("[finale] armed") || l.Contains("[finale] registered")).Select(l => l.Trim()).ToArray();
    var finaleTrigger = lines.Where(l => l.Contains("[finale] trigger")).Select(l => l.Trim()).ToArray();
    var finaleEnd = lines.Where(l => l.Contains("finale end") || l.Contains("[finale] deadline winner")
        || l.Contains("[finale] re-arm") || l.Contains("[finale] side=") || l.Contains("waiting for an observed enemy station"))
        .Select(l => l.Trim()).ToArray();
    var gameOver = lines.Where(l => l.Contains("[gameover]") && l.Contains("Stage Over")).Select(l => l.Trim()).ToArray();
    var routes = lines.Where(l => l.Contains("[strike-route]")).Select(l => l.Trim()).ToArray();
    var push = new
    {
        Seek = lines.Count(l => l.Contains("[push] seek")),
        Contact = lines.Count(l => l.Contains("[push] contact")),
        Hold = lines.Where(l => l.Contains("[push] hold")).Select(l => Field(l, "reason")).GroupBy(r => r)
            .ToDictionary(g => g.Key, g => g.Count()),
    };
    return new
    {
        Label = label,
        Civs = mission.Sides.Select(s => new { s.Id, Civ = s.Civilization.CivilizationId, s.Kills, s.Deaths }).ToArray(),
        Margin = margin,
        Swapped = swap,
        Stage = mission.Stage.ToString(),
        SimSeconds = Math.Round(simSeconds, 1),
        Garrisons = mission.Sectors.SelectMany(s => s.StationRuntimes)
            .Where(st => st.Type.ClassId == 0)
            .Select(st => new { st.StationId, st.SideId, Hits = hits.Count(h => h.StationId == st.StationId) }).ToArray(),
        HitsByStation = hits.GroupBy(h => new { h.StationId, h.StationSide, h.AttackerSide }).Select(g => new
        {
            g.Key.StationId, StationSide = g.Key.StationSide, AttackerSide = g.Key.AttackerSide, Hits = g.Count(),
            MaxHitsIn20s = MaxWindow(g.Select(h => h.T)), Killed = g.Any(h => h.Killed),
            ShieldFirst = g.First().ShieldBefore, ShieldLast = g.Last().ShieldAfter,
            HullFirst = g.First().HullBefore, HullLast = g.Last().HullAfter,
            Weapons = g.Select(h => h.Weapon.Split(' ')[0]).Distinct().ToArray(),
        }).ToArray(),
        Waves = commits, ReleasesBySide = releases.GroupBy(r => r.Side)
            .ToDictionary(g => g.Key, g => new { Total = g.Count(), LoneWolf = g.Count(r => r.Role == "lone-wolf") }),
        ReleaseOutcomes = releaseOutcomes,
        ReleasesKilledBySide = releaseOutcomes.GroupBy(r => r.Side)
            .ToDictionary(g => g.Key, g => new { Released = g.Count(), Killed = g.Count(r => r.Outcome == "killed") }),
        Staging = staging, StationKills = stationKills, BomberDeathsBySide = deaths,
        FinaleArmed = finaleArmed, FinaleTrigger = finaleTrigger, FinaleEnd = finaleEnd, Routes = routes,
        Push = push, GameOver = gameOver,
        FinaleState = finale.Select(f => JsonSerializer.SerializeToElement(f).GetProperty("state").GetString()).ToArray(),
    };

}

static double? MaxWindow(IEnumerable<double> times)
{
    var ordered = times.OrderBy(t => t).ToArray();
    int best = 0;
    for (int i = 0; i < ordered.Length; i++)
        best = Math.Max(best, ordered.Count(t => t >= ordered[i] && t < ordered[i] + 20));
    return ordered.Length == 0 ? null : best;
}

static object? ParseWave(string line)
{
    if (!line.Contains("finale commit")) return null;
    return new
    {
        Side = Side(line), Required = Int(line, "required"), Assembled = Int(line, "assembled"),
        Visible = Int(line, "visible"), LoneWolves = Int(line, "loneWolves"), Sector = Int(line, "sector"),
        StationHull = Field(line, "stationHull"), StationShield = Field(line, "stationShield"),
        Reason = Field(line, "reason"), Survival = line.Contains(" survival=") ? Field(line, "survival") : null,
        Wait = Field(line, "wait"), Target = Field(line, "target"),
    };
}

static short Side(string line)
{
    var m = Regex.Match(line, @"\bside[= ](-?\d+)");
    return m.Success ? short.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : (short)-1;
}

static string Field(string line, string key)
{
    var m = Regex.Match(line, Regex.Escape(key) + @"=(\S+)");
    return m.Success ? m.Groups[1].Value.TrimEnd(',') : "";
}

static int Int(string line, string key) => int.TryParse(Field(line, key), out int v) ? v : -1;

string Option(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

/// <summary>Console tee: every bot/server line goes to stdout AND to the run log,
/// so the in-memory summary is parsed from exactly what was logged.</summary>
internal sealed class Tee : TextWriter
{
    private readonly TextWriter _inner;
    private readonly StreamWriter _file;
    private readonly StringBuilder _all = new();
    internal Tee(string path)
    {
        _inner = Console.Out;
        _file = new StreamWriter(path) { AutoFlush = true };
    }
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) { _inner.Write(value); _file.Write(value); lock (_all) _all.Append(value); }
    public override void Write(string? value) { _inner.Write(value); _file.Write(value); lock (_all) _all.Append(value); }
    public override void WriteLine(string? value) { _inner.WriteLine(value); _file.WriteLine(value); lock (_all) _all.Append(value).Append('\n'); }
    internal string Snapshot() { lock (_all) return _all.ToString(); }
    protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
}
