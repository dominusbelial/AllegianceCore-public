#!/usr/bin/env python3
"""Reproduce P699b seeds with real artwork, normal assertions and at most two test processes.

Build Allegiance.Core.Tests first and export local.env before invoking this tool.
Raw TRX/logs stay in the chosen scratch directory; summary.csv/json are compact evidence.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import csv
import json
import os
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
METHODS = (
    "Hundred_pods_per_side_return_from_Alderan_with_relaunch_traffic",
    "Hundred_pods_per_side_return_with_native_rescue_capable_launch_traffic",
)


def run(args, case, seed, trace=""):
    name = f"case{case}-seed{seed}" + ("-trace" if trace else "")
    env = os.environ.copy()
    env["P958_SEED"] = str(seed)
    env["P958_TRACE"] = trace
    env["P958_LEGACY_STEERING"] = "1" if args.legacy_steering else "0"
    command = [args.dotnet, "vstest", str(args.assembly),
               "/TestCaseFilter:FullyQualifiedName=Allegiance.Core.Tests.P699bPodLoadTests." + METHODS[case],
               "/Logger:trx;LogFileName=" + name + ".trx", "/ResultsDirectory:" + str(args.output)]
    load = os.getloadavg()[0] if hasattr(os, "getloadavg") else None
    with (args.output / (name + ".log")).open("w") as log:
        completed = subprocess.run(command, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, check=False)
    result = ET.parse(args.output / (name + ".trx")).find(".//t:UnitTestResult", NS)
    if result is None:
        raise RuntimeError(f"No test result for {name}; inspect its log")
    node = result.find("t:Output/t:StdOut", NS)
    lines = (node.text or "").splitlines() if node is not None else []
    pods = [json.loads(line[9:]) for line in lines if line.startswith("P958_POD ")]
    summary = next((line for line in lines if line.startswith("baseline=")), "")
    metrics = dict(re.findall(r"(\w+)=([^ ]+)", summary))
    row = dict(case=case, seed=seed, testOutcome=result.get("outcome"), exitCode=completed.returncode,
               duration=result.get("duration"), load=load, **metrics,
               zeroExpiryOutcome="Passed" if metrics.get("oxygen") == "0" else "Failed",
               expired=[pod["Ship"] for pod in pods if pod["Outcome"] == "oxygen"],
               homeReentries=sum(any(a["Sector"] == pod["HomeSector"] and b["Sector"] != pod["HomeSector"]
                                     for a, b in zip(pod["Route"], pod["Route"][1:])) for pod in pods),
               radiusExpiries=sum(pod["Outcome"] == "oxygen" and pod["RadiusAt"] is not None for pod in pods),
               bayArrivals=next((line[10:] for line in lines if line.startswith("P958_BAYS ")), ""))
    print(json.dumps(row), flush=True)
    return row


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assembly", type=Path,
                        default=ROOT / "tests/Allegiance.Core.Tests/bin/Debug/net10.0/Allegiance.Core.Tests.dll")
    parser.add_argument("--dotnet", default=str(Path.home() / ".dotnet/dotnet"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--seeds", default="0:30", help="Python-style start:stop (exclusive), or comma-separated seeds")
    parser.add_argument("--case", choices=["both", "regular", "rescue"], default="both")
    parser.add_argument("--parallel", type=int, choices=[1, 2], default=2)
    parser.add_argument("--legacy-steering", action="store_true", help="Replay the original P699b controller for diagnosis")
    parser.add_argument("--trace-failures", type=int, default=0, help="Rerun this many failing cases with collision/pose traces")
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.assembly = args.assembly.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    seeds = list(range(*map(int, args.seeds.split(":")))) if ":" in args.seeds else list(map(int, args.seeds.split(",")))
    cases = [0, 1] if args.case == "both" else [0 if args.case == "regular" else 1]
    jobs = [(case, seed) for seed in seeds for case in cases]
    rows = []
    # Each future owns a separate test process. No synthetic load is generated.
    with ThreadPoolExecutor(max_workers=args.parallel) as pool:
        for row in pool.map(lambda job: run(args, *job), jobs):
            rows.append(row)
            (args.output / "summary.json").write_text(json.dumps(rows, indent=2) + "\n")
    with (args.output / "summary.csv").open("w", newline="") as target:
        writer = csv.DictWriter(target, fieldnames=list(rows[0]), lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)
    failures = [row for row in rows if row["expired"]][:args.trace_failures]
    for row in failures:
        run(args, row["case"], row["seed"], ",".join(map(str, row["expired"])))
    if any(row["testOutcome"] == "NotExecuted" or not row.get("total") for row in rows):
        raise SystemExit("Incomplete evidence: artwork was skipped or telemetry is missing")


if __name__ == "__main__":
    main()
