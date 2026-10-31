#!/usr/bin/env python3
"""Run the existing local showcase proof with initial miners and count real ore sales.

Uses isolated test/tool builds; --reference HEAD selects the unchanged bot sources.
Raw outputs stay in .scratch/p905. Uses P700's fixture wallet seeds; no remote
machines or sim changes. Mining and ore sales use the unchanged engine.
"""
import argparse
import os
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--reference", help="Git revision supplying the MCP bot sources")
parser.add_argument("--label", required=True)
parser.add_argument("--sim-seconds", type=int, default=600)
args = parser.parse_args()
if not re.fullmatch(r"[A-Za-z0-9_-]+", args.label):
    parser.error("label must contain only letters, digits, underscores or hyphens")
if args.sim_seconds <= 0:
    parser.error("sim-seconds must be positive")
work = ROOT / ".scratch/p905" / args.label
work.mkdir(parents=True, exist_ok=True)
test_dir = work / "tests"
test_dir.mkdir(exist_ok=True)


def source(name):
    return (ROOT / name).read_text()


def resolve_includes(data, folder, mcp):
    def resolve(match):
        path = match[1].replace("\\", "/")
        if "tools/allegiance-mcp/allegiance-mcp.csproj" in path:
            target = mcp
        elif path.startswith("../"):
            target = (ROOT / folder / path).resolve()
        else:
            return match[0]
        return f'Include="{target}"'
    return re.sub(r'Include="([^"]+)"', resolve, data)


mcp = ROOT / "tools/allegiance-mcp/allegiance-mcp.csproj"
if args.reference:
    tool_dir = work / "mcp"
    tool_dir.mkdir(exist_ok=True)
    names = subprocess.check_output(["git", "ls-tree", "-r", "--name-only", args.reference,
                                     "tools/allegiance-mcp"], cwd=ROOT, text=True).splitlines()
    for name in names:
        if Path(name).suffix not in (".cs", ".csproj"):
            continue
        data = subprocess.check_output(["git", "show", f"{args.reference}:{name}"], cwd=ROOT, text=True)
        if name.endswith(".csproj"):
            data = resolve_includes(data, "tools/allegiance-mcp", mcp)
        target = tool_dir / Path(name).relative_to("tools/allegiance-mcp")
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(data)
    mcp = tool_dir / "allegiance-mcp.csproj"

project = "tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj"
(test_dir / Path(project).name).write_text(resolve_includes(source(project), str(Path(project).parent), mcp))
(test_dir / "Directory.Build.targets").write_text(source("tests/Directory.Build.targets").replace(
    "$(MSBuildThisFileDirectory)_shared/TierTestFramework.cs", str(ROOT / "tests/_shared/TierTestFramework.cs")))
(test_dir / "P660NextMatchLiveTests.cs").write_text(source("tests/Allegiance.ClientCore.Tests/P660NextMatchLiveTests.cs"))
proof = source("tests/Allegiance.ClientCore.Tests/P700ShowcaseLiveTests.cs")
proof = proof.replace("server.SpawnInitialMinersAtBoot = false", "server.SpawnInitialMinersAtBoot = true")
proof = proof.replace("        Require.Artwork();", "        Require.Artwork();\n        Assert.True(System.Runtime.GCSettings.IsServerGC);")
proof = proof.replace("        using var stop =", """        var miners = server.Missions.SelectMany(m => m.Sectors).SelectMany(s => s.Models.OfType<Ship>())
            .Where(s => s.IsNpc && s.PilotType == Ship.PilotTypeMiner).ToArray();
        var previousOre = miners.ToDictionary(s => s.Id, s => s.Ore);
        var incomeGate = new object();
        double[] units = [0, 0], credits = [0, 0];
        var actorIds = new HashSet<short>();
        var dockedSince = new Dictionary<short, long>();
        var dockedIdlers = new HashSet<short>();
        int dockedAtEnd = 0;
        using var stop =""")
# With miners present, a defence incident can protect a miner in a sector
# without a station. P700's station-only fixture assertion is inapplicable.
start = proof.index('                    if (bot.Sector == bot.TargetSector && bot.Role != "Bomber" && bot.State == "defending")')
brace = proof.index("{", start)
level = 1
end = brace + 1
while level:
    level += (proof[end] == "{") - (proof[end] == "}")
    end += 1
proof = proof[:start] + proof[end:]
proof = re.sub(r'if \(!double.IsNaN\(firstCombat\).*?break;',
               f'if (server.SimNow >= {args.sim_seconds}) break;', proof, count=1, flags=re.S)
proof = proof.replace("server.TickSim(); Thread.Sleep(20);", """server.TickSim();
                lock (incomeGate)
                {
                    foreach (var miner in miners)
                    {
                        float before = previousOre[miner.Id];
                        if (!miner.Dead && before > miner.Ore + .1f)
                        {
                            double sold = before - miner.Ore;
                            units[miner.SideId!.Value] += sold;
                            credits[miner.SideId.Value] += sold * miner.EffectiveOreCreditPerUnit();
                        }
                        previousOre[miner.Id] = miner.Ore;
                    }
                    dockedAtEnd = 0;
                    if (server.Missions[0].Stage == MissionStage.Started)
                        foreach (var entry in server.Ships)
                        {
                            var ship = entry.Ship;
                            if (!actorIds.Contains(ship.Id)) continue;
                            if (ship.StationId is not null && !ship.Dead && !Catalog.IsLifepodHull(ship.HullType.HullId))
                            {
                                dockedAtEnd++;
                                if (!dockedSince.TryGetValue(ship.Id, out long since)) dockedSince[ship.Id] = since = Environment.TickCount64;
                                if (Environment.TickCount64 - since >= 5000) dockedIdlers.Add(ship.Id);
                            }
                            else dockedSince.Remove(ship.Id);
                        }
                }
                Thread.Sleep(20);""")
proof = proof.replace("                var bots = manager.ActiveBots();", "                var bots = manager.ActiveBots();\n                lock (incomeGate) actorIds = bots.Select(b => b.ShipId).ToHashSet();")
proof = proof.replace("            var combat = manager.GetCombatSummary();", """            lock (incomeGate)
                output.WriteLine("P905_HE3 " + JsonSerializer.Serialize(new { units, credits, initialMiners = miners.Length }));
            var combat = manager.GetCombatSummary();""")
proof = proof.replace("            var combat = manager.GetCombatSummary();", "            var combat = manager.GetCombatSummary();\n"
    + "            lock (incomeGate) System.IO.File.WriteAllText("
    + 'Path.Combine(AppContext.BaseDirectory, "p905-summary.json"), '
    + "JsonSerializer.Serialize(new { units, credits, initialMiners = miners.Length, wallSeconds = watch.Elapsed.TotalSeconds, simulatedSeconds = server.SimNow, combat, active = manager.ActiveBots().Count, serverGc = System.Runtime.GCSettings.IsServerGC, dockedAtEnd, dockedIdlers, perf = manager.GetPerfStats(null) }));")
# Friendly miner support can yield legitimate short target holds. Keep the
# >5s parking assertion, and record the general idle telemetry without using
# the no-miners fixture's zero-hold assumption as an economy gate.
proof = proof.replace('            Assert.Equal(0, idleMetric.GetProperty("currentCount").GetInt32());', '')
proof = proof.replace('            Assert.Equal(0, idleMetric.GetProperty("botsLastMinute").GetInt32());', '')
proof = proof.replace('            Assert.Equal(0, server.SimPassStageFailures);', '            Assert.Equal(0, server.SimPassStageFailures);\n            lock (incomeGate) Assert.Empty(dockedIdlers);')
(test_dir / "P700ShowcaseLiveTests.cs").write_text(proof)
env = dict(os.environ, DOTNET_gcServer="1", NUGET_HTTP_CACHE_PATH=str(ROOT / ".scratch/p905/nuget-http"),
           ALLEGIANCE_BROADCAST_PARALLEL_BUILD="1", ALLEGIANCE_TRANSPORT_MODE="pool",
           ALLEGIANCE_SHIP_UPDATE_MODEL="native", ALLEGIANCE_SIM_NATIVE_GOTOPLAN="1")
dotnet = os.environ.get("DOTNET", str(Path.home() / ".dotnet/dotnet"))
with (work / "run.log").open("w") as log:
    result = subprocess.run([dotnet, "test", str(test_dir / Path(project).name), "--filter",
                             "FullyQualifiedName~Local_showcase_never_parks_with_target_for_five_seconds",
                             "--logger", "console;verbosity=detailed", "--logger", "trx;LogFileName=proof.trx",
                             "--results-directory", str(work / "results")], cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT)
summary = test_dir / "bin/Debug/net10.0/p905-summary.json"
if summary.exists():
    (work / "summary.json").write_bytes(summary.read_bytes())
print(f"{args.label}: exit={result.returncode}, log={work.relative_to(ROOT)}/run.log")
raise SystemExit(result.returncode)
