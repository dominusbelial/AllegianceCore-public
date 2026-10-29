# Score a completed rig batch

Keep the server log pieces in chronological order. Retain UTC timestamps on
future captures so the 10-second peers samples can be matched to busy windows.
Use the same rig card, scenario, calibration and Battle reference for an A/B.

From the repository root, after the batch finishes:

```bash
python3 scripts/allegiance-index.py \
  --server "$EVIDENCE/server/b55-prof-2026-10-29.log" \
  --samples "$EVIDENCE/samples/2026-10-29.csv" \
  --bots "$EVIDENCE/bots/b55/err.worker-1.log" "$EVIDENCE/bots/b55/err.worker-2.log" \
  --rig docs/benchmarks/rig-221.json --label b55 \
  --json "$OUTPUT/aix-b55.json"
```

`EVIDENCE` is the local evidence directory; `OUTPUT` must already exist. Add
another `--server FILE` for a midnight split. Without timestamps the script
uses final `[gameover-timing] ... winner=... peers=N` summaries; if those are
also absent, explicitly pass `--players N`. Do not average an entire day's
samples to infer a batch. Omitting bot logs still uses finale/scoring evidence
if supplied; a pure profiler capture is labelled `server-only`.

The command only reads evidence and writes the specified JSON. Deployment-chain
integration is left to the operator. See [the definition](../docs/PERFORMANCE.md#allegiance-index)
and [calibration and missing-evidence notes](../docs/reports/AIX1_REPORT.md).
