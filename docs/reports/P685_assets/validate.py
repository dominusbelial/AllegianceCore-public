#!/usr/bin/env python3
"""P685 local verification. Build first, then invoke with focus, suites, or all."""
import gzip
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent
os.chdir(ROOT)
ENV = os.environ.copy()
config = ROOT / "local.env"
if config.exists():
    for line in config.read_text().splitlines():
        if line.strip() and not line.lstrip().startswith("#") and "=" in line:
            key, value = line.split("=", 1)
            ENV[key.strip()] = value.strip().strip("\"'")
ENV.update(NUGET_HTTP_CACHE_PATH="/tmp/p685/nuget-cache", ALLEGIANCE_LIVE_P660_SHOWCASE="1")
for key in ("ALLEGIANCE_BROADCAST_SNAPSHOT", "ALLEGIANCE_BROADCAST_ENCODE_ONCE", "ALLEGIANCE_BROADCAST_TIERS"):
    ENV.pop(key, None)
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
RESULTS = OUT / "results.json"
records = json.loads(RESULTS.read_text()) if RESULTS.exists() else []

def run(label, project, filt=None, overrides=None):
    cmd = ["dotnet", "test", f"tests/Allegiance.{project}.Tests", "--no-build", "--nologo", "-v:m",
           "--logger", f"trx;LogFileName={label}.trx", "--results-directory", str(OUT)]
    if filt:
        cmd += ["--filter", filt]
    start = time.monotonic()
    with (OUT / f"{label}.log").open("w") as log:
        process = subprocess.run(cmd, env=ENV | (overrides or {}), stdout=log, stderr=subprocess.STDOUT)
    record = dict(label=label, project=project, filter=filt, overrides=overrides or {},
                  exit=process.returncode, seconds=round(time.monotonic() - start, 2), failures=[])
    trx = OUT / f"{label}.trx"
    if trx.exists():
        xml = ET.parse(trx)
        counters = xml.find(".//t:Counters", NS)
        record["counts"] = dict(counters.attrib) if counters is not None else {}
        methods = {t.attrib["id"]: t.find("t:TestMethod", NS).attrib for t in xml.findall(".//t:UnitTest", NS)}
        for test in xml.findall(".//t:UnitTestResult", NS):
            if test.attrib.get("outcome") == "Failed":
                method = methods[test.attrib["testId"]]
                record["failures"].append(method["className"].split(",")[0] + "." + method["name"])
        with gzip.open(str(trx) + ".gz", "wb") as destination:
            destination.write(trx.read_bytes())
        trx.unlink()
    records.append(record)
    RESULTS.write_text(json.dumps(records, indent=2) + "\n")
    print(json.dumps(record), flush=True)
    return record

def cohort(*names):
    return "|".join("FullyQualifiedName~" + name for name in names)

if __name__ == "__main__":
    phase = sys.argv[1] if len(sys.argv) > 1 else "all"
    if phase in ("focus", "all"):
        run("core-merge", "Core", cohort("P663", "P674", "P676", "P681", "PaydayTests", "StartingMoneyStationIncome"))
        run("client-merge", "ClientCore", cohort("FedMsgDispatchCoverage", "P663", "P675", "P676", "P678", "P680"))
        run("audio-merge", "ClientCore.MonoGame", cohort("P664", "P675", "P676", "P678", "P680"))
        run("pipeline", "Core", cohort("BroadcastSnapshotTests", "BroadcastEncodeOnceTests", "BroadcastSnapshotLoopbackTests",
            "ParallelBroadcastByteIdentityTests", "InterestTierTests", "InterestTierCombatTests", "PipelineCostTests", "P684MergeTests"))
        for rep in range(1, 4):
            run(f"rotation-{rep}", "ClientCore", cohort("P682"))
        run("showcase", "ClientCore", cohort("P660ShowcaseLiveTests"),
            {key: "1" for key in ("ALLEGIANCE_BROADCAST_SNAPSHOT", "ALLEGIANCE_BROADCAST_ENCODE_ONCE", "ALLEGIANCE_BROADCAST_TIERS")})
        run("battle", "ClientCore", "FullyQualifiedName~P632PortModeZeroKillsLiveTests.Assert_port_bots_kill_in_window",
            {"ALLEGIANCE_LIVE_P632_ASSERT": "1", "ALLEGIANCE_P632_SIZE": "15", "ALLEGIANCE_P632_SECONDS": "90",
             "ALLEGIANCE_P632_DEBUG": "1", "ALLEGIANCE_P632_TAG": "p685"})
        run("relaunch", "ClientCore", "FullyQualifiedName~BotAcceptsAndEngagesOrderLiveTests.Mcp_debug_bot_buys_relaunches_and_fights")
        run("mining", "Core", cohort("MinerDecisionCycleLiveTests", "MiningSellTests"))
    if phase in ("suites", "all"):
        suites = [run("suite-" + project.lower(), project)
                  for project in ("Lobby", "Core", "ClientCore", "ClientCore.MonoGame")]
        for suite in suites:
            for index, method in enumerate(sorted(set(suite["failures"])), 1):
                for rep in range(1, 4):
                    run(f"isolation-{suite['project'].lower()}-{index}-{rep}", suite["project"], "FullyQualifiedName=" + method)
    uncleared = []
    for record in records:
        if record["exit"] == 0:
            continue
        cleared = record["label"].startswith("suite-") and record["failures"] and all(
            sum(r["exit"] == 0 and r["project"] == record["project"] and r["filter"] == "FullyQualifiedName=" + method
                and r["label"].startswith("isolation-") for r in records) >= 3 for method in record["failures"])
        if not cleared:
            uncleared.append(record["label"])
    print(json.dumps({"uncleared": uncleared}), flush=True)
    sys.exit(bool(uncleared))
