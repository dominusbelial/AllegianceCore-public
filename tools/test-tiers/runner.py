#!/usr/bin/env python3
"""Build once, audit discovered tiers, run projects, check TRX durations/coverage."""
import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import fcntl
import json
import os
import re
from pathlib import Path
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
PROJECTS = ["Allegiance.Lobby.Tests", "Allegiance.ClientCore.MonoGame.Tests",
            "Allegiance.ClientCore.Tests", "Allegiance.Core.Tests"]
TIERS = ("fast", "live", "perf")
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def duration(value):
    hours, minutes, seconds = value.split(":")
    return float(hours) * 3600 + float(minutes) * 60 + float(seconds)


def read_trx(path):
    tree = ET.parse(path)
    methods = {}
    for definition in tree.findall(".//t:UnitTest", NS):
        method = definition.find("t:TestMethod", NS)
        methods[definition.get("id")] = method.get("className") + "." + method.get("name")
    results = tree.findall(".//t:UnitTestResult", NS)
    ids = [row.get("executionId") for row in results]
    if len(ids) != len(set(ids)):
        raise ValueError(f"Duplicate execution IDs in {path}")
    return [dict(name=row.get("testName"), method=methods[row.get("testId")],
                 outcome=row.get("outcome"), seconds=duration(row.get("duration", "0:0:0")))
            for row in results]


def timing_offenders(rows, inventory, limit):
    """Fail closed: TRX results must resolve to a single known tier."""
    by_name = {}
    for case in inventory:
        if case["name"] in by_name and by_name[case["name"]] != case["tier"]:
            raise ValueError("Ambiguous tier for displayed test name: " + case["name"])
        by_name[case["name"]] = case["tier"]
    offenders = []
    for row in rows:
        if row["name"] not in by_name:
            raise ValueError("TRX test absent from discovery: " + row["name"])
        if by_name[row["name"]] == "Fast" and row["seconds"] > limit:
            offenders.append(row)
    return offenders


def check_coverage(expected, actual):
    missing = Counter(expected) - Counter(actual)
    extra = Counter(actual) - Counter(expected)
    if missing or extra:
        def sample(counts):
            return f"{sum(counts.values())} cases, sample={dict(list(counts.items())[:10])}"
        raise ValueError(f"Test coverage differs: missing {sample(missing)}; extra {sample(extra)}")


def idle_check(directory):
    # Two short samples, normalized for CPU count; never silently run a busy perf gate.
    threshold = float(os.environ.get("TT1_MAX_LOAD_PER_CPU", "0.8"))
    cpus = len(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else (os.cpu_count() or 1)
    samples = []
    deadline = time.monotonic() + 60
    quiet = 0
    while True:
        load = os.getloadavg()[0] / cpus if hasattr(os, "getloadavg") else 0
        samples.append(dict(loadPerCpu=load, cpus=cpus))
        quiet = quiet + 1 if load <= threshold else 0
        if quiet >= 2:
            break
        if time.monotonic() >= deadline:
            (directory / "idle.json").write_text(json.dumps(samples, indent=2))
            raise RuntimeError(f"Perf requires idle machine: load/CPU={load:.2f} > {threshold:.2f}")
        time.sleep(3)
    (directory / "idle.json").write_text(json.dumps(samples, indent=2))


class Runner:
    def __init__(self, args):
        self.args = args
        self.dotnet = os.environ.get("DOTNET", "dotnet")
        self.output = Path(args.results_dir).resolve()
        self.output.mkdir(parents=True, exist_ok=False)
        self.extra_filter = os.environ.get("TEST_FILTER", "")
        self.inventory = {}
        self.expected = {}
        self.summaries = {}
        self.calibration = {}

    def command(self, args, logfile, env=None):
        with logfile.open("w") as log:
            return subprocess.run([self.dotnet, *args], cwd=ROOT, env=env,
                                  stdout=log, stderr=subprocess.STDOUT).returncode

    def project(self, name):
        return str(ROOT / "tests" / name / (name + ".csproj"))

    def filter(self, *filters):
        return "&".join(f"({f})" for f in (*filters, self.extra_filter) if f)

    def discover(self, name):
        manifest = self.output / (name + ".jsonl")
        env = dict(os.environ, TT1_DISCOVERY_MANIFEST=str(manifest))
        log = self.output / (name + "-discovery.log")
        cmd = ["test", self.project(name), "--no-build", "--nologo", "--list-tests"]
        if self.extra_filter:
            cmd += ["--filter", self.extra_filter]
        if self.command(cmd, log, env) or not manifest.exists():
            raise RuntimeError(f"Discovery failed: {log}")
        records = list(map(json.loads, manifest.read_text().splitlines()))
        discovery_errors = [r["error"] for r in records if "error" in r]
        if discovery_errors:
            raise RuntimeError("Invalid tier assignment: " + "; ".join(discovery_errors))
        rows = list({row["id"]: row for row in records}.values())
        if not rows:
            raise RuntimeError(f"Empty discovery: {name}")
        self.inventory[name] = rows
        listed = [line.strip() for line in log.read_text().splitlines() if line.startswith("    ")]
        selected = [row for row in rows if row.get("discoveryName", row["name"]) in listed]
        containers = {row.get("discoveryId", row["id"]): row.get("discoveryName", row["name"]) for row in selected}
        if Counter(listed) != Counter(containers.values()):
            raise RuntimeError(f"Discovery diagnostics or missing/extra cases in {log}")
        self.expected[name] = selected
        if any(row["tier"] not in ("Fast", "Live", "Perf") or row["traits"]["Tier"] != [row["tier"]] for row in rows):
            raise RuntimeError(f"Non-exclusive tier assignment: {name}")
        return {tier: sum(row["tier"].lower() == tier for row in selected) for tier in TIERS}

    def calibrate(self, directory):
        log = directory / "calibration.json"
        if self.command(["run", "--project", "tools/test-tiers", "--no-build"], log):
            raise RuntimeError(f"P744 calibration failed: {log}")
        self.calibration = json.loads(log.read_text())
        print(f"P744 factor={self.calibration['factor']:.3f}; Fast limit={self.calibration['fastLimitSeconds']:.3f}s", flush=True)

    def run_project(self, name, tier, lane, directory, filter_override=None):
        wanted = [r for r in self.expected[name] if r["tier"].lower() == tier
                  and (("LiveTiming" in r["traits"].get("Lane", [])) == (lane == "quarantine"))]
        if not wanted and filter_override is None:
            return [], 0
        stem = name + "-" + lane
        trx = directory / (stem + ".trx")
        log = directory / (stem + ".log")
        selection = filter_override or self.filter("Tier=" + tier.capitalize(),
                             "Lane=LiveTiming" if lane == "quarantine" else "Lane!=LiveTiming")
        start = time.monotonic()
        command = ["test", self.project(name), "--no-build", "--nologo", "--filter", selection,
                   "--logger", "trx;LogFileName=" + trx.name, "--logger", "console;verbosity=normal",
                   "--results-directory", str(directory)]
        if tier != "fast":
            command += ["--settings", str(ROOT / "tools/test-tiers/sequential.runsettings")]
        code = self.command(command, log)
        if not trx.exists():
            raise RuntimeError(f"No TRX generated: {log}")
        rows = read_trx(trx)
        if any(r["outcome"] not in ("Passed", "NotExecuted") for r in rows):
            code = 1
        print(f"{tier}/{lane} {name}: {dict(Counter(r['outcome'] for r in rows))} ({time.monotonic()-start:.1f}s); {log}", flush=True)
        return rows, code

    def run_tier(self, tier):
        directory = self.output / tier
        directory.mkdir()
        start = time.monotonic()
        if tier == "perf":
            idle_check(directory)
        self.calibrate(directory)
        rows_by_project = {name: [] for name in PROJECTS}
        errors, flakes, offenders = [], [], []
        # The existing LiveNetwork collection still controls intra-project scheduling.
        workers = self.args.parallel if tier == "fast" else 1
        with ThreadPoolExecutor(workers) as pool:
            jobs = {name: pool.submit(self.run_project, name, tier, "main", directory) for name in PROJECTS}
            for name, job in jobs.items():
                try:
                    rows, code = job.result()
                    rows_by_project[name].extend(rows)
                    if code:
                        errors.append(f"{name}: main failed")
                except Exception as error:
                    errors.append(str(error))
        # P647 quarantine always runs, serially, with its existing isolated retry rule.
        for name in PROJECTS:
            try:
                rows, code = self.run_project(name, tier, "quarantine", directory)
                rows_by_project[name].extend(rows)
                failed_methods = sorted({r["method"] for r in rows if r["outcome"] == "Failed"})
                if code and not failed_methods:
                    errors.append(f"{name}: quarantine process failed")
                for index, method in enumerate(failed_methods):
                    retry_dir = directory / f"retry-{name}-{index}"
                    retry_dir.mkdir()
                    retry_rows, retry_code = self.run_project(name, tier, "quarantine", retry_dir,
                        self.filter("FullyQualifiedName=" + method))
                    offenders.extend(timing_offenders(retry_rows, self.inventory[name],
                                                       self.calibration["fastLimitSeconds"]))
                    if retry_code or not retry_rows:
                        errors.append(f"{method}: quarantine failed in isolation")
                    else:
                        flakes.append(method)
            except Exception as error:
                errors.append(str(error))
        for name, rows in rows_by_project.items():
            try:
                check_coverage([r["name"] for r in self.expected[name] if r["tier"].lower() == tier],
                               [r["name"] for r in rows])
                offenders.extend(timing_offenders(rows, self.inventory[name], self.calibration["fastLimitSeconds"]))
            except Exception as error:
                errors.append(str(error))
        if offenders:
            errors.append(f"{len(offenders)} Fast test executions exceeded the calibrated 10 second limit")
        summary = dict(tier=tier, seconds=time.monotonic()-start, projects={
            name: dict(count=len(rows), outcomes=dict(Counter(r["outcome"] for r in rows)))
            for name, rows in rows_by_project.items()}, calibration=self.calibration,
            timingOffenders=offenders, errors=errors, quarantineFlakes=flakes)
        self.summaries[tier] = summary
        (directory / "summary.json").write_text(json.dumps(summary, indent=2))
        text = f"{tier}: {sum(len(r) for r in rows_by_project.values())} tests, {summary['seconds']:.1f}s, {'FAIL' if errors else 'PASS'}\n"
        text += "".join(f"  ERROR: {e}\n" for e in errors)
        text += "".join(f"  SLOW FAST: {r['seconds']:.3f}s {r['name']}\n" for r in offenders)
        text += "".join(f"  QUARANTINE FLAKE: {f}\n" for f in flakes)
        (directory / "summary.txt").write_text(text)
        print(text, flush=True)
        return not errors

    def run(self):
        if not self.args.no_build:
            print("Building solution", flush=True)
            if self.command(["build", "AllegianceCore.slnx", "-m:8", "-v:minimal"], self.output / "build.log"):
                raise RuntimeError(f"Build failed: {self.output / 'build.log'}")
        with ThreadPoolExecutor(4) as pool:
            counts = dict(zip(PROJECTS, pool.map(self.discover, PROJECTS)))
        (self.output / "inventory.json").write_text(json.dumps(counts, indent=2))
        print("Discovery counts: " + json.dumps(counts), flush=True)
        ok = True
        infrastructure = None
        if self.args.tier in ("fast", "all"):
            start = time.monotonic()
            with (self.output / "infrastructure.log").open("w") as log:
                code = subprocess.run([sys.executable, "-m", "unittest", "discover", "-s",
                    "tools/test-tiers", "-p", "test_runner.py"], cwd=ROOT,
                    stdout=log, stderr=subprocess.STDOUT).returncode
            match = re.search(r"Ran (\d+) tests?", (self.output / "infrastructure.log").read_text())
            count = int(match.group(1)) if match else 0
            infrastructure = dict(tier="Fast", count=count, seconds=time.monotonic()-start, exitCode=code)
            ok = code == 0 and count > 0
            print(f"Fast infrastructure checks: {'PASS' if ok else 'FAIL'}", flush=True)
        for tier in TIERS if self.args.tier == "all" else [self.args.tier]:
            try:
                ok = self.run_tier(tier) and ok
            except Exception as error:
                self.summaries[tier] = dict(errors=[str(error)])
                directory = self.output / tier
                directory.mkdir(exist_ok=True)
                (directory / "summary.json").write_text(json.dumps(self.summaries[tier], indent=2))
                (directory / "summary.txt").write_text(f"{tier}: FAIL: {error}\n")
                print(f"{tier}: FAIL: {error}", flush=True)
                ok = False
        if infrastructure is not None:
            limit = self.summaries.get("fast", {}).get("calibration", {}).get("fastLimitSeconds", 10)
            infrastructure["timingGuardPassed"] = infrastructure["seconds"] <= limit
            infrastructure_ok = infrastructure["exitCode"] == 0 and infrastructure["count"] > 0 and infrastructure["timingGuardPassed"]
            ok = ok and infrastructure_ok
            fast = self.summaries["fast"]
            fast["infrastructure"] = infrastructure
            if not infrastructure_ok:
                fast.setdefault("errors", []).append("Fast infrastructure checks failed or exceeded the calibrated limit")
            (self.output / "fast/summary.json").write_text(json.dumps(fast, indent=2))
            with (self.output / "fast/summary.txt").open("a") as summary:
                summary.write(f"Fast infrastructure: {'PASS' if infrastructure_ok else 'FAIL'} ({infrastructure['count']} checks, {infrastructure['seconds']:.3f}s)\n")
            (self.output / "fast/infrastructure.json").write_text(json.dumps(infrastructure, indent=2))
        (self.output / "summary.json").write_text(json.dumps(dict(discovery=counts, tiers=self.summaries,
            infrastructure=infrastructure, passed=ok), indent=2))
        lines = [(self.output / tier / "summary.txt").read_text() for tier in self.summaries]
        (self.output / "summary.txt").write_text("".join(lines) + f"Overall: {'PASS' if ok else 'FAIL'}\n")
        print(f"{'PASS' if ok else 'FAIL'}: {self.output / 'summary.json'}", flush=True)
        return 0 if ok else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tier", choices=(*TIERS, "all"), default="all")
    parser.add_argument("--parallel", type=int, default=4, help="concurrent Fast projects (default: 4)")
    parser.add_argument("--no-build", action="store_true", help="use a previously built solution")
    parser.add_argument("--results-dir", default=str(ROOT / "TestResults" / ("tiers-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ"))))
    args = parser.parse_args()
    if args.parallel < 1:
        parser.error("--parallel must be positive")
    # Prevent overlapping invocations from contaminating Perf; shared across worktrees.
    lock_path = Path(os.environ.get("TT1_LOCK_PATH", "/tmp/allegiance-test-tiers.lock"))
    try:
        with lock_path.open("a") as lock:
            print(f"Waiting for runner lock: {lock_path}", flush=True)
            fcntl.flock(lock, fcntl.LOCK_EX)
            return Runner(args).run()
    except (OSError, ValueError, RuntimeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
