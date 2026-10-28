#!/usr/bin/env python3
"""P862 — summarize a sector-change run from the client perf CSV + log.

Reads the [sector-entry] lines the client logs on an own-ship sector change
(`Game1.SectorLoad.cs`) and reports the frame-time profile around each change
from `ALLEGIANCE_CLIENT_PERF_CSV` (P643 per-frame telemetry): worst frame, p99,
the 5 s before/after windows, the allocation burst and the GC work, plus what the
entry frames loaded.

Usage:
  scripts/p862-warp-summary.py <client.out> <perf.csv> [--label NAME] [--json OUT]

Exit code 0 always (a summary, not a gate).
"""
import argparse
import csv
import json
import re
import sys

SECTOR_RE = re.compile(
    r"\[sector-entry\] (?P<from>.+?) \((?P<from_id>-?\d+)\) -> (?P<to>.+?) \((?P<to_id>-?\d+)\) "
    r"frame (?P<frame>\d+): entry frame (?P<ms>[0-9.]+) ms; warming (?P<warming>[^;]+)")
WARM_RE = re.compile(r"\[sector-warm\] (?P<body>.+)")
ENTRY30_RE = re.compile(r"\[sector-entry\] (?P<name>.+?) \((?P<id>-?\d+)\) first 30 frames: (?P<body>.+)")


def percentile(values, q):
    if not values:
        return float("nan")
    ordered = sorted(values)
    k = min(len(ordered) - 1, max(0, int(round((len(ordered) - 1) * q))))
    return ordered[k]


def num(row, key):
    v = row.get(key, "")
    try:
        f = float(v)
    except (TypeError, ValueError):
        return float("nan")
    return f if f == f else float("nan")


def window(rows, start, length):
    """Frames by index into the CSV (the CSV is in frame order)."""
    out = []
    for r in rows:
        try:
            f = int(r["frame"])
        except (TypeError, ValueError):
            continue
        if start <= f < start + length:
            out.append(r)
    return out


def profile(rows):
    frames = [num(r, "frame_ms") for r in rows]
    gpu = [num(r, "scene_gpu_ms") for r in rows]
    alloc = [int(r["allocated_bytes"]) for r in rows if r.get("allocated_bytes", "").isdigit()]
    gc_pause = [num(r, "gc_pause_ms") for r in rows]
    finite_gpu = [g for g in gpu if g == g]
    return {
        "frames": len(rows),
        "worst_ms": max(frames) if frames else float("nan"),
        "p99_ms": percentile(frames, 0.99),
        "p50_ms": percentile(frames, 0.50),
        "worst_gpu_ms": max(finite_gpu) if finite_gpu else float("nan"),
        "alloc_mb": (max(alloc) / 1048576.0) if alloc else 0.0,
        "alloc_total_mb": (sum(alloc) / 1048576.0) if alloc else 0.0,
        "gc_pause_ms": max(gc_pause) if gc_pause else 0.0,
        "gc_pause_total_ms": sum(p for p in gc_pause if p == p),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("client_log")
    ap.add_argument("perf_csv")
    ap.add_argument("--label", default="")
    ap.add_argument("--json", default="")
    ap.add_argument("--window-frames", type=int, default=300,
                    help="frames in the before/after window (300 ≈ 5 s at 60 fps)")
    args = ap.parse_args()

    log = open(args.client_log, errors="replace").read().splitlines()
    rows = list(csv.DictReader(open(args.perf_csv)))
    if not rows:
        print("no frames in", args.perf_csv)
        return 0

    entries = []
    for line in log:
        m = SECTOR_RE.search(line)
        if m:
            entries.append({**m.groupdict(), "frame": int(m.group("frame"))})

    print(f"=== P862 sector-change summary {args.label} ===")
    print(f"frames: {len(rows)}  ({profile(rows)['p50_ms']:.1f} ms p50 overall)")
    for m in WARM_RE.finditer("\n".join(log)):
        print("  warm:", m.group("body"))
    for m in ENTRY30_RE.finditer("\n".join(log)):
        print("  entry:", m.group("name"), f"({m.group('id')})", m.group("body"))

    if not entries:
        print("!! no [sector-entry] line in the log — did the ship change sector?")
        return 0

    report = []
    for e in entries:
        n = e["frame"]
        before = window(rows, max(1, n - args.window_frames), args.window_frames)
        after = window(rows, n, args.window_frames)
        pb, pa = profile(before), profile(after)
        print()
        print(f"-- {e['from']} ({e['from_id']}) -> {e['to']} ({e['to_id']}) at frame {n} "
              f"(warming {e['warming'].strip()})")
        print(f"   entry frame (logged by the client): {float(e['ms']):.1f} ms")
        print(f"   5 s before: worst {pb['worst_ms']:.1f} ms  p99 {pb['p99_ms']:.1f}  "
              f"p50 {pb['p50_ms']:.1f}  worst GPU {pb['worst_gpu_ms']:.2f}  "
              f"max alloc {pb['alloc_mb']:.1f} MB  GC pause {pb['gc_pause_ms']:.1f} ms")
        print(f"   5 s after : worst {pa['worst_ms']:.1f} ms  p99 {pa['p99_ms']:.1f}  "
              f"p50 {pa['p50_ms']:.1f}  worst GPU {pa['worst_gpu_ms']:.2f}  "
              f"max alloc {pa['alloc_mb']:.1f} MB  GC pause {pa['gc_pause_ms']:.1f} ms")
        # The frames immediately around the change (the hitch window).
        near = window(rows, n - 2, 8)
        if near:
            print("   frames -2..+5: " + "  ".join(
                f"{r['frame']}:{num(r, 'frame_ms'):.1f}ms/{num(r, 'scene_gpu_ms'):.1f}gpu/"
                f"{int(r['allocated_bytes']) / 1048576.0:.0f}MB" for r in near))
        report.append({"from": e["from"], "from_id": int(e["from_id"]),
                       "to": e["to"], "to_id": int(e["to_id"]), "frame": n,
                       "entry_ms": float(e["ms"]),
                       "before": pb, "after": pa})

    if args.json:
        json.dump(report, open(args.json, "w"), indent=1)
        print("\nwrote", args.json)
    return 0


if __name__ == "__main__":
    sys.exit(main())
