# Score a completed rig batch

Keep the server log pieces in chronological order. Retain UTC timestamps on
future captures so the 10-second peers samples can be matched to busy windows.
Use the same rig card, scenario, calibration and Battle reference for an A/B.
The rig card is schema 2: a list of machines, each with roles (`server`,
`bots`, `client`), CPU, performance cores, efficiency cores, threads and RAM.
Schema-1 cards (server object + botHosts) convert on load with zero
efficiency cores. Weighted cores = P + 0.5·E (`EFFICIENCY_WEIGHT`); threads
never count. The summary prints AIX, AIX/server-core (the server machine's
weighted cores) and AIX/total-core (all machines in the run); **machines is a
label, never a divisor**.

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

**Shared-machine runs (AIX-S).** For a skirmish where one machine is both
server and client (card: one machine with `roles: ["server", "client"]`, or
`"shared": true`), also pass `--frames FILE` with the client's `[frameprof]`
log pieces:

```bash
python3 scripts/allegiance-index.py --server "$EVIDENCE/server.log" \
  --frames "$EVIDENCE/frames.log" --rig "$EVIDENCE/rig-skirmish.json" \
  --label p889 --json "$OUTPUT/p889.json"
```

The tool then prints **AIX-S** in addition to AIX: a busy window is good only
if the server window is good and the client frame p95 in that aligned window
is ≤ 33.3 ms (`GOOD_CLIENT_P95_MS`). Missing client evidence fails closed.
AIX-S is a different measurement on different hardware; **never compare it
directly with dedicated AIX**.

The command only reads evidence and writes the specified JSON. Deployment-chain
integration is left to the operator. See [the definition](../docs/PERFORMANCE.md#allegiance-index)
and the [AIX reports](../docs/reports/AIX1_REPORT.md).
