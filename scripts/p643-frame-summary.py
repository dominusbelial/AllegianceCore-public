#!/usr/bin/env python3
"""Summarize P643 per-frame CSV; keep frame IDs for GC/GPU correlation."""
import argparse
import csv
import math
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("csv", nargs="+", type=Path)
parser.add_argument("--warmup-seconds", type=float, default=2)
parser.add_argument("--drop-last", type=int, default=1, help="exclude final screenshot/exit frame")
args = parser.parse_args()
print("| scene | frames | render -> output | p50 / p99 / max ms | CPU update / draw / post median ms | GPU scene / post median ms | present median ms | GC pause ms | bytes/frame | p99 / max GC; GPU ms |")
print("|---|---:|---|---|---|---|---:|---:|---:|---|")

def median(values):
    values = sorted(v for v in values if math.isfinite(v))
    return values[len(values) // 2] if values else float("nan")

for path in args.csv:
    rows = list(csv.DictReader(path.open()))
    if args.drop_last:
        rows = rows[:-args.drop_last]
    elapsed = 0
    keep = []
    for row in rows:
        row = {key: float(value) for key, value in row.items()}
        elapsed += row["frame_ms"]
        if elapsed >= args.warmup_seconds * 1000:
            keep.append(row)
    if not keep:
        raise SystemExit(f"No post-warmup frames: {path}")
    ordered = sorted(keep, key=lambda r: r["frame_ms"])
    p50 = ordered[len(ordered) // 2]
    p99 = ordered[min(len(ordered) - 1, int(len(ordered) * .99))]
    worst = ordered[-1]
    r = keep[-1]
    dims = f'{r["render_width"]:.0f}x{r["render_height"]:.0f} -> {r["width"]:.0f}x{r["height"]:.0f}'
    cpu = " / ".join(f"{median(r[k] for r in keep):.2f}" for k in ("update_ms", "draw_submit_ms", "post_submit_ms"))
    gpu = " / ".join(f"{median(r[k] for r in keep):.2f}" for k in ("scene_gpu_ms", "post_gpu_ms"))
    tail = " / ".join(f'#{r["frame"]:.0f} {r["gc_pause_ms"]:.2f}; {r["scene_gpu_ms"] + r["post_gpu_ms"]:.2f}' for r in (p99, worst))
    print(f'| {path.stem} | {len(keep)} | {dims} | {p50["frame_ms"]:.2f} / {p99["frame_ms"]:.2f} / {worst["frame_ms"]:.2f} | {cpu} | {gpu} | {median(r["present_wait_ms"] for r in keep):.2f} | {sum(r["gc_pause_ms"] for r in keep):.2f} | {sum(r["allocated_bytes"] for r in keep)/len(keep):.0f} | {tail} |')
