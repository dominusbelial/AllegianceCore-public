"""Build an instrumented copy of the base or candidate host and sample live matches on Linux."""
import argparse
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("source", type=Path, help="root of the matching source checkout")
parser.add_argument("label", choices=("base", "change"))
parser.add_argument("--prepare", action="store_true", help="only build the measurement binaries")
parser.add_argument("--size", choices=("Medium", "Large"), default="Medium")
parser.add_argument("--run", type=int, default=1)
args = parser.parse_args()
root = args.source.resolve()
tool = Path(__file__).resolve().parent
out = tool.parents[1] / ".scratch" / "p1099-cost" / args.label
host = out / "host"
driver = root / "tools/allegiance-mcp/bin/Debug/net10.0/allegiance-mcp"


def replace_once(source, old, new):
    if source.count(old) != 1:
        raise ValueError(f"unexpected observation source at {old!r}")
    return source.replace(old, new)


def prepare():
    source = root / "src/Allegiance.SinglePlayer"
    shutil.copytree(source, host, dirs_exist_ok=True, ignore=shutil.ignore_patterns("obj", "bin"))
    project = host / "Allegiance.SinglePlayer.csproj"
    xml = ET.parse(project)
    for ref in xml.iter("ProjectReference"):
        ref.set("Include", str((source / ref.get("Include")).resolve()))
    xml.write(project, encoding="unicode")
    observation = host / "SkirmishObservation.cs"
    s = replace_once(observation.read_text(), "    // In-process only:",
                     "    public double WaitMs, HoldMs;\n    // In-process only:")
    start = "long before = System.Diagnostics.Stopwatch.GetTimestamp();"
    enter = "long entered = System.Diagnostics.Stopwatch.GetTimestamp();"
    finish = ("HoldMs = System.Diagnostics.Stopwatch.GetElapsedTime(entered).TotalMilliseconds;\n"
              "WaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(before, entered).TotalMilliseconds;")
    if args.label == "base":
        s = replace_once(s, "lock (_simGate)\n        {", start + "\n        lock (_simGate)\n        {\n" + enter)
        s = replace_once(s, "return new(mission.ResearchEpoch", "var result = new SkirmishObservation(mission.ResearchEpoch")
        s = replace_once(s, "(_observerKills) };", "(_observerKills) };\n" + finish + "\nreturn result;")
    else:
        capture = "_observer.Capture(_server.Missions[0], sectorId);"
        s = replace_once(s, "lock (_simGate) " + capture,
                         start + "\nlock (_simGate) {\n" + enter + "\n" + capture + "\n" + finish + "\n}")
    observation.write_text(s)
    bench = out / "benchmark"
    bench.mkdir(exist_ok=True)
    shutil.copyfile(tool / "Program.cs", bench / "Program.cs")
    constants = "BASELINE" if args.label == "base" else ""
    (bench / "cost.csproj").write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><DefineConstants>{constants}</DefineConstants></PropertyGroup>
  <ItemGroup><ProjectReference Include="../host/Allegiance.SinglePlayer.csproj"/></ItemGroup>
</Project>
""")
    subprocess.run(["dotnet", "build", root / "tools/allegiance-mcp", "-m:4"], check=True)
    subprocess.run(["dotnet", "build", bench / "cost.csproj", "-m:4"], check=True)


if args.prepare:
    prepare()
else:
    log = out / f"{args.size}-{args.run}.log"
    with log.open("w") as stream:
        subprocess.run(["dotnet", out / "benchmark/bin/Debug/net10.0/cost.dll", args.size, driver],
                       cwd=root, stdout=stream, stderr=subprocess.STDOUT, check=True)
    print(log)
