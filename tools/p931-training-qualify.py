#!/usr/bin/env python3
"""Bounded real-input training cohorts. Source local.env before invoking.

Example: python3 tools/p931-training-qualify.py --runs 10 --jobs 2
Every case replays the native prefix in a fresh shipping Game1 window. The
existing observer checks real deaths and exact native messages; this wrapper
only launches windows and records their exit status and source fingerprints.
"""
import argparse
import concurrent.futures
import hashlib
import json
import os
import signal
import threading
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runs", type=int, default=1)
    parser.add_argument("--jobs", type=int, choices=range(1, 5), default=2)
    parser.add_argument("--minutes", type=float, default=60)
    parser.add_argument("--policy", choices=("hunt", "sacrifice"), action="append")
    parser.add_argument("--style", choices=("Classic", "Modern"), action="append")
    parser.add_argument("--output", type=Path, default=ROOT / ".scratch/p931/qualification")
    args = parser.parse_args()
    if args.runs < 1 or args.minutes <= 0:
        parser.error("runs and minutes must be positive")
    if not os.environ.get("ALLEGIANCE_ARTWORK_PATH"):
        parser.error("source local.env first; artwork is required, with no skipped windows")
    output = args.output.resolve()
    if not output.is_relative_to(ROOT):
        parser.error("output must be inside the worktree")
    if output.exists():
        parser.error("use a new output directory to preserve prior cohort evidence")
    output.mkdir(parents=True)
    deadline = time.monotonic() + args.minutes * 60
    proof = ROOT / "tools/p912-training-proof/bin/Debug/net10.0/p912-training-proof.dll"
    content = ROOT / "src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content"
    dotnet = os.environ.get("DOTNET", str(Path.home() / ".dotnet/dotnet"))
    sources = [ROOT / "tools/p912-training-proof/Program.cs",
        ROOT / "src/Allegiance.ClientCore/Training/TrainingClock.cs",
        ROOT / "src/Allegiance.ClientCore.Game/Game1.TrainingIntegration.cs",
        ROOT / "src/Allegiance.ClientCore.Game/Game1.Training.cs", *sorted(
        (ROOT / "src/Allegiance.ClientCore.Game").glob("Game1.TrainingProof*.cs")),
        ROOT / "src/Allegiance.ClientCore.Game/Training/TrainingProofPilot.cs"]
    receipt = {"sources": {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                           for p in sources},
               "artifacts": {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest()
                             for p in (proof, proof.parent / "Allegiance.ClientCore.Game.dll",
                                       proof.parent / "Allegiance.ClientCore.dll", proof.parent / "Allegiance.Core.dll")},
               "jobs": args.jobs, "cases": []}

    cancelled = threading.Event()
    for sig in (signal.SIGINT, signal.SIGTERM):
        signal.signal(sig, lambda *_: cancelled.set())

    def run(case):
        policy, style, index = case
        directory = output / f"{policy}-{style}-{index}"
        directory.mkdir()
        env = os.environ.copy()
        env.update(ALLEGIANCE_NOVSYNC="1", SDL_VIDEODRIVER="offscreen", DISPLAY="",
                   LIBGL_ALWAYS_SOFTWARE="1", ALSOFT_DRIVERS="null", DOTNET_gcServer="1",
                   ALLEGIANCE_BROADCAST_PARALLEL_BUILD="1", ALLEGIANCE_TRANSPORT_MODE="pool",
                   XDG_CONFIG_HOME=str(directory / "xdg"),
                   ALLEGIANCE_HUD_SETTINGS=str(directory / "hud.json"))
        (directory / "xdg").mkdir()
        command = [dotnet, str(proof), "window", str(content), str(directory),
                   "4" if policy == "hunt" else "8", style, policy]
        if policy == "hunt":
            command.append("7")
        started = time.monotonic()
        result = dict(policy=policy, style=style, run=index, window=str(directory.relative_to(ROOT)),
                      load_before=os.getloadavg(), started_utc=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()))
        remaining = deadline - started
        if cancelled.is_set() or (output / "cancel").exists() or remaining <= 0:
            return dict(result, outcome="NotRun", reason="cohort cancelled/deadline")
        fingerprints = receipt["sources"] | receipt["artifacts"]
        if any(hashlib.sha256((ROOT / name).read_bytes()).hexdigest() != digest
               for name, digest in fingerprints.items()):
            return dict(result, outcome="NotRun", reason="sources/artifacts changed during cohort")
        with (directory / "run.log").open("w") as log:
            process = subprocess.Popen(command, cwd=ROOT, env=env, stdout=log,
                                       stderr=subprocess.STDOUT, start_new_session=True)
            reason = None
            try:
                limit = started + min(1260, remaining)
                while process.poll() is None:
                    if cancelled.is_set() or (output / "cancel").exists():
                        reason = "cohort cancelled"
                        break
                    if time.monotonic() >= limit:
                        reason = "window/cohort deadline"
                        break
                    time.sleep(.5)
            finally:
                if process.poll() is None:
                    os.killpg(process.pid, signal.SIGTERM)
                    try:
                        process.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        os.killpg(process.pid, signal.SIGKILL)
                        process.wait()
            text = (directory / "run.log").read_text()
            marker = "P912_LESSON_PASS M4" if policy == "hunt" else "P912_WINDOW_PASS M8"
            passed = process.returncode == 0 and marker in text and (directory / "complete.txt").exists()
            result.update(outcome="Passed" if passed else "Cancelled" if reason == "cohort cancelled" else "Failed",
                          exit_code=process.returncode)
            if reason:
                result["reason"] = reason
        result.update(seconds=round(time.monotonic() - started, 3), load_after=os.getloadavg())
        return result

    cases = [(p, s, n) for n in range(1, args.runs + 1)
             for p in args.policy or ("hunt", "sacrifice")
             for s in args.style or ("Classic", "Modern")]
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs) as pool:
        for future in concurrent.futures.as_completed([pool.submit(run, case) for case in cases]):
            result = future.result()
            receipt["cases"].append(result)
            (output / "summary.json").write_text(json.dumps(receipt, indent=2) + "\n")
            print(f"{result['policy']} {result['style']} #{result['run']}: {result['outcome']}", flush=True)
    return int(any(case["outcome"] != "Passed" for case in receipt["cases"]))


if __name__ == "__main__":
    raise SystemExit(main())
