using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Tools.McpSpawner;
using Srv = Allegiance.Server.Server;

// P959 local finale-strike measurement. Real in-process server + real MCP bots on
// the rig's map with native fog, ordinary purchases, research showcase finale.
// Same harness shape as tools/p896-strike-proof (P896), extended to 2- and 3-side
// matches and to per-COMMIT telemetry: for every finale commit it reports the
// wing it asked for, what actually released, what died before release, the real
// station damage inside the wave window and the planner's own predicted kill.
//
// Usage: dotnet run --project tools/p959-strike-proof -- --output <dir>
//        [--teams 2|3] [--count 15] [--label L] [--seconds 600]
//        [--game-seconds 2400] [--finale-seconds 3600] [--finale-minutes 1]
//        [--early-minutes 0] [--bombers 12] [--survival-margin -1]
//        [--prearm-minutes 2] [--loadout varied] [--map m2xspiderw.igc]
var directory = Path.GetFullPath(Option("--output", ".scratch/p959/proof"));
Directory.CreateDirectory(directory);
int teams = int.Parse(Option("--teams", "2"), CultureInfo.InvariantCulture);
int wallLimit = int.Parse(Option("--seconds", "600"), CultureInfo.InvariantCulture);
int gameSeconds = int.Parse(Option("--game-seconds", "2400"), CultureInfo.InvariantCulture);
int finaleSeconds = int.Parse(Option("--finale-seconds", "3600"), CultureInfo.InvariantCulture);
double finaleMinutes = double.Parse(Option("--finale-minutes", "1"), CultureInfo.InvariantCulture);
double earlyMinutes = double.Parse(Option("--early-minutes", "0"), CultureInfo.InvariantCulture);
int bombers = int.Parse(Option("--bombers", "12"), CultureInfo.InvariantCulture);
double survivalMargin = double.Parse(Option("--survival-margin", "-1"), CultureInfo.InvariantCulture);
double prearmMinutes = double.Parse(Option("--prearm-minutes", "2"), CultureInfo.InvariantCulture);
string loadout = Option("--loadout", "varied");
string map = Option("--map", "m2xspiderw.igc");
string mapGen = Option("--mapgen", teams == 3 ? "DiamondRing" : "");
int mapSeed = int.Parse(Option("--map-seed", "959"), CultureInfo.InvariantCulture);
int count = int.Parse(Option("--count", "15"), CultureInfo.InvariantCulture);
int researchScale = int.Parse(Option("--research-scale", "64"), CultureInfo.InvariantCulture);
string label = Option("--label", $"t{teams}-m{marginTag()}");
string marginTag() => survivalMargin < 0 ? "default" : survivalMargin.ToString(CultureInfo.InvariantCulture);
string[] civs = teams == 3 ? ["18", "4", "5"] : ["25", "3"];

var progress = Console.Out;
var tee = new Tee(Path.Combine(directory, label + ".log"));
using var server = new Srv();
var game = new Allegiance.Server.GameConfig
{
    Name = "P959 strike", Map = map, Teams = teams,
    CivIds = civs.Select(int.Parse).ToArray(),
    SideNames = teams == 3 ? ["Alpha", "Bravo", "Charlie"] : ["Alpha", "Bravo"],
    GameLengthSeconds = gameSeconds, MaxTeamImbalance = 32767, MaxPlayersPerTeam = 40,
    ResearchPlanDirectory = Path.GetFullPath("data/showcase-plans"),
    ResearchPlans = Enumerable.Range(0, teams).ToDictionary(s => (short)s, _ => "auto"),
    MapGen = mapGen.Length > 0 ? new Allegiance.Server.MapGenConfig { Type = mapGen, Seed = mapSeed } : null,
};
server.Boot(new()
{
    Port = 0, SideSelectAutoSeatSeconds = 30, ConsoleVerbose = true,
    Showcase = new() { TechMode = "research", IncomeMultiplier = 5000, ResearchTimeScale = researchScale,
        FinaleTimeoutSeconds = finaleSeconds },
    LiveSettings = new() { TimeDilation = "off", AutoNextMatch = new() { Enabled = false } },
    Games = [game],
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
    var scenario = new Dictionary<string, JsonElement>
    {
        ["preset"] = JsonSerializer.SerializeToElement("showcase"),
        ["staging"] = JsonSerializer.SerializeToElement(true),
        ["finale_side"] = JsonSerializer.SerializeToElement(teams == 3 ? "all" : "both"),
        ["finale_min_relaunch_share"] = JsonSerializer.SerializeToElement(0),
        ["finale_min_kills_last_5_minutes"] = JsonSerializer.SerializeToElement(1),
        ["finale_bombers"] = JsonSerializer.SerializeToElement(bombers),
        ["finale_after_minutes"] = JsonSerializer.SerializeToElement(finaleMinutes),
        ["loadout_mode"] = JsonSerializer.SerializeToElement(loadout),
    };
    if (teams == 3)
        scenario["sides"] = JsonSerializer.SerializeToElement(Enumerable.Range(0, teams)
            .Select(_ => new { count }).ToArray());
    else
    {
        scenario["count_a"] = JsonSerializer.SerializeToElement(count);
        scenario["count_b"] = JsonSerializer.SerializeToElement(count);
    }
    if (survivalMargin >= 0) scenario["finale_survival_margin"] = JsonSerializer.SerializeToElement(survivalMargin);
    if (earlyMinutes > 0) scenario["finale_early_pressure_minutes"] = JsonSerializer.SerializeToElement(earlyMinutes);
    if (prearmMinutes >= 0) scenario["finale_prearm_minutes"] = JsonSerializer.SerializeToElement(prearmMinutes);
    var spawn = McpTools.Handle("spawn_scenario", scenario, manager);
    if (spawn.IsError == true) throw new InvalidOperationException(JsonSerializer.Serialize(spawn));
    var mission = server.Missions[0];
    // Garrison ids at BOOT: a killed station leaves StationRuntimes, so the live
    // list at summary time would miss exactly the kills we want to classify.
    var garrisonIds = mission.Sectors.SelectMany(x => x.StationRuntimes)
        .Where(st => st.Type.ClassId == 0).Select(st => st.StationId).ToArray();
    // --reveal-stations: a MEASUREMENT FIXTURE only (the same seeding P733's live
    // finale test and the P896 harness use) so the finale arms deterministically and
    // the strike mechanism can be compared across arms. Native fog stays on for the
    // bots' worlds; this only seeds which sides may discover which stations.
    if (args.Contains("--reveal-stations"))
        foreach (var st in mission.Sectors.SelectMany(x => x.StationRuntimes))
            foreach (var sideId in mission.Sides.Select(x => x.Id))
                st.SideVisibility.SetSideVisibility(sideId, true);
    File.WriteAllText(Path.Combine(directory, label + "-geometry.json"), JsonSerializer.Serialize(new
    {
        Map = map,
        Sectors = mission.Sectors.Select(s => new { s.Id, s.Name,
            Stations = s.StationRuntimes.Select(st => new { st.StationId, st.SideId, Type = st.Type.StationTypeId,
                Class = st.Type.ClassId, Armor = st.Type.MaxArmorHitPoints, Shield = st.Type.MaxShieldHitPoints,
                ShieldRegen = st.Type.ShieldRegeneration }).ToArray() }).ToArray(),
    }, new JsonSerializerOptions { WriteIndented = true }));
    double next = 0;
    while (watch.Elapsed.TotalSeconds < wallLimit && mission.Stage != MissionStage.Over)
    {
        if (tickFailure is not null) throw new InvalidOperationException("Simulation failed", tickFailure);
        if (watch.Elapsed.TotalSeconds >= next)
        {
            progress.WriteLine($"{label}: wall={watch.Elapsed.TotalSeconds:F0}s sim={server.SimNow:F0}s "
                + string.Join(" ", mission.Sides.Where(s => s.Active).Select(s => $"side{s.Id}={s.Kills}/{s.Deaths}"))
                + $" finale={JsonSerializer.Serialize(manager.FinaleStatus()).Length}B");
            next += 60;
        }
        Thread.Sleep(250);
    }
    Thread.Sleep(1500);   // let the bots receive GAME_OVER
    var summary = Summarize(tee.Snapshot(), mission, manager.FinaleStatus(), label, teams,
        survivalMargin, earlyMinutes, server.SimNow, garrisonIds);
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

static object Summarize(string log, Mission mission, object[] finale, string label, int teams,
    double margin, double earlyMinutes, double simSeconds, IReadOnlyCollection<short> bootGarrisons)
{
    var stationDamage = new Regex(
        @"\[station-damage\] t=(?<t>[\d.]+) side=(?<side>-?\d+) station=(?<id>\d+):(?<name>.*?) attackerSide=(?<aside>-?\d+) ship=(?<ship>-?\d+) hull=(?<hull>\d+):(?<hn>.*?) weapon=(?<weapon>.*?) damageType=(?<dmg>\d+) shield=(?<sb>[\d.-]+)->(?<sa>[\d.-]+) hull=(?<hb>[\d.-]+)->(?<ha>[\d.-]+) killed=(?<killed>True|False)",
        RegexOptions.Compiled);
    var lines = log.Split('\n');
    // Line index per station-damage event so wave windows (which are delimited by
    // log order, since commit lines carry no clock) can be sliced exactly.
    var damageByLine = new SortedDictionary<int, Hit>();
    for (int i = 0; i < lines.Length; i++)
    {
        var m = stationDamage.Match(lines[i]);
        if (!m.Success) continue;
        damageByLine[i] = new Hit(
            double.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture),
            short.Parse(m.Groups["side"].Value, CultureInfo.InvariantCulture),
            short.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture),
            m.Groups["name"].Value.Trim(),
            short.Parse(m.Groups["aside"].Value, CultureInfo.InvariantCulture),
            short.Parse(m.Groups["ship"].Value, CultureInfo.InvariantCulture),
            m.Groups["weapon"].Value.Trim(),
            float.Parse(m.Groups["sb"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["sa"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["hb"].Value, CultureInfo.InvariantCulture),
            float.Parse(m.Groups["ha"].Value, CultureInfo.InvariantCulture),
            m.Groups["killed"].Value == "True");
    }
    var hits = damageByLine.Values.ToArray();
    var commits = new List<(int Line, string Text)>();
    for (int i = 0; i < lines.Length; i++)
        if (lines[i].Contains("[strike]") && lines[i].Contains(" finale commit ")) commits.Add((i, lines[i]));
    var releases = new List<(int Line, short Side, string Role, short Ship)>();
    var deaths = new List<(int Line, short Ship)>();
    var waveEnds = new List<(int Line, string Text)>();
    var refits = new List<(int Line, string Text)>();
    var triggerLine = -1;
    for (int i = 0; i < lines.Length; i++)
    {
        var l = lines[i];
        if (l.Contains("[strike]") && l.Contains(" finale release "))
            releases.Add((i, Side(l), Field(l, "role"), (short)Int(l, "ship")));
        if (l.Contains("[p539d] DEATH")) deaths.Add((i, (short)Int(l, "ship")));
        if (l.Contains("[strike]") && l.Contains(" finale wave end ")) waveEnds.Add((i, l));
        if (l.Contains("[finale-duty]") && l.Contains("return to dock for legal strike")) refits.Add((i, l));
        if (triggerLine < 0 && l.Contains("[finale] trigger")) triggerLine = i;
    }
    var waves = new List<object>();
    for (int w = 0; w < commits.Count; w++)
    {
        var (line, text) = commits[w];
        int end = w + 1 < commits.Count ? commits[w + 1].Line
            : waveEnds.Count > 0 ? waveEnds.Select(e => e.Line).Where(x => x > line).DefaultIfEmpty(lines.Length).Min()
            : lines.Length;
        short side = Side(text);
        var targetMatch = Regex.Match(text, @"target=station#(\d+)");
        short station = targetMatch.Success ? short.Parse(targetMatch.Groups[1].Value, CultureInfo.InvariantCulture) : (short)-1;
        var members = releases.Where(r => r.Line > line && r.Line < end && r.Side == side).ToArray();
        var waveHits = damageByLine.Where(kv => kv.Key > line && kv.Key < end && kv.Value.StationId == station
            && kv.Value.AttackerSide == side).Select(kv => kv.Value).ToArray();
        var waveEnd = waveEnds.FirstOrDefault(e => e.Line > line && e.Line < end).Text;
        int killedBefore = members.Count(m => deaths.Any(d => d.Line < m.Line && d.Line > line && d.Ship == m.Ship));
        int killedAfter = members.Count(m => deaths.Any(d => d.Line > m.Line && d.Line < end && d.Ship == m.Ship));
        waves.Add(new
        {
            Commit = text.Trim(),
            Side = side, Target = station,
            Required = Int(text, "required"), Assembled = Int(text, "assembled"),
            Visible = Int(text, "visible"), LoneWolves = Int(text, "loneWolves"),
            StationHullAtCommit = Field(text, "stationHull"), StationShieldAtCommit = Field(text, "stationShield"),
            Wait = Field(text, "wait"), Reason = Field(text, "reason"),
            Released = members.Length, ReleasedShips = members.Select(m => m.Ship).ToArray(),
            KilledBeforeRelease = killedBefore, KilledAfterRelease = killedAfter,
            AliveAtRelease = members.Length - killedBefore,
            HitsLanded = waveHits.Length,
            ShieldFirst = waveHits.Length > 0 ? waveHits[0].ShieldBefore : (float?)null,
            ShieldLast = waveHits.Length > 0 ? waveHits[^1].ShieldAfter : (float?)null,
            HullFirst = waveHits.Length > 0 ? waveHits[0].HullBefore : (float?)null,
            HullLast = waveHits.Length > 0 ? waveHits[^1].HullAfter : (float?)null,
            KilledStation = waveHits.Any(h => h.Killed),
            Weapons = waveHits.Select(h => h.Weapon.Split(' ')[0]).Distinct().ToArray(),
            WaveEnd = string.IsNullOrEmpty(waveEnd) ? null : waveEnd.Trim(),
        });
    }
    var garrisonIds = bootGarrisons.Concat(mission.Sectors.SelectMany(s => s.StationRuntimes)
        .Where(st => st.Type.ClassId == 0).Select(st => st.StationId)).ToHashSet();
    var garrisonKills = hits.Where(h => h.Killed && garrisonIds.Contains(h.StationId)).ToArray();
    var finaleEnd = lines.Where(l => l.Contains("[strike]") && l.Contains("finale end")
        || l.Contains("[finale] deadline winner")).Select(l => l.Trim()).ToArray();
    var byStation = hits.GroupBy(h => (h.StationId, h.Name)).OrderBy(g => g.Key.StationId).Select(g => new
    {
        g.Key.StationId, g.Key.Name, Garrison = garrisonIds.Contains(g.Key.StationId),
        AttackerSide = g.First().AttackerSide,
        Hits = g.Count(), MaxHitsIn20s = MaxWindow(g.Select(h => h.T)),
        ShieldFirst = g.First().ShieldBefore, ShieldLast = g.Last().ShieldAfter,
        HullFirst = g.First().HullBefore, HullLast = g.Last().HullAfter,
        Killed = g.Any(h => h.Killed),
        FirstAt = Math.Round(g.Min(h => h.T), 1),
        Weapons = g.Select(h => h.Weapon.Split(' ')[0]).Distinct().ToArray(),
    }).ToArray();
    var finaleStart = triggerLine >= 0 ? lines[triggerLine] : "";
    var refitInWindow = triggerLine < 0 ? 0 : refits.Count(r => r.Line > triggerLine);
    var earlyWaves = lines.Where(l => l.Contains("[strike]") && l.Contains("early pressure")).Select(l => l.Trim()).ToArray();
    return new
    {
        Label = label,
        Teams = teams,
        Margin = margin,
        EarlyMinutes = earlyMinutes,
        Stage = mission.Stage.ToString(),
        SimSeconds = Math.Round(simSeconds, 1),
        Trigger = finaleStart.Trim(),
        Civs = mission.Sides.Select(s => new { s.Id, Civ = s.Civilization.CivilizationId, s.Kills, s.Deaths }).ToArray(),
        Waves = waves,
        WaveCount = waves.Count,
        RefitTripsTotal = refits.Count,
        RefitTripsInFinaleWindow = refitInWindow,
        EarlyPressureLines = earlyWaves,
        Garrisons = mission.Sectors.SelectMany(s => s.StationRuntimes).Where(st => st.Type.ClassId == 0)
            .Select(st => new { st.StationId, st.SideId, Hits = hits.Count(h => h.StationId == st.StationId) }).ToArray(),
        Stations = byStation,
        GarrisonKills = garrisonKills.Select(k => new { k.StationId, k.Name, AttackerSide = k.AttackerSide, At = Math.Round(k.T, 1) }).ToArray(),
        GarrisonKillCount = garrisonKills.Select(k => k.StationId).Distinct().Count(),
        TimeToFirstGarrisonKill = garrisonKills.Length > 0 ? Math.Round(garrisonKills.Min(k => k.T), 1) : (double?)null,
        FinaleEnd = finaleEnd,
        GameOver = lines.Where(l => l.Contains("[gameover]") && l.Contains("Stage Over")).Select(l => l.Trim()).ToArray(),
        FinaleState = finale.Select(f => JsonSerializer.SerializeToElement(f).GetProperty("state").GetString()).ToArray(),
    };
}

// The ordered hit list index for a log position scan (used only to build the
// line→hit map; kept separate so the Regex.Match sequence stays in order).
static double? MaxWindow(IEnumerable<double> times)
{
    var ordered = times.OrderBy(t => t).ToArray();
    int best = 0;
    for (int i = 0; i < ordered.Length; i++)
        best = Math.Max(best, ordered.Count(t => t >= ordered[i] && t < ordered[i] + 20));
    return ordered.Length == 0 ? null : best;
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

internal sealed record Hit(double T, short StationSide, short StationId, string Name, short AttackerSide,
    short Ship, string Weapon, float ShieldBefore, float ShieldAfter, float HullBefore, float HullAfter, bool Killed);

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
