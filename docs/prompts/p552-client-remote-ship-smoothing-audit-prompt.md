# P552 — READ-ONLY audit: client remote-ship smoothing and snap correction vs native (the small "teleporting" Domingo sees in 10v10/15v15)

## Why this task exists

**Domingo (live, 221, 20 Hz):** small teleporting of remote ships in big
fights. At 10 Hz it measured worse (MCP perf: remote position jump between
updates, mean 2.07 → 4.46, worst 4.65 → 11.96). **No audit covered client
prediction/correction:** P538 explicitly deferred dead-reckoning to the
2026-09-29 "report A". `RemoteShipPredictor` was **removed** earlier (raw
pose looked better then); revisit only with evidence.

## Scope (READ-ONLY: no code changes, no commits except the report)

1. **Native:** how the native client moves remote ships between updates.
   Find and cite the receipt of ship updates (`clintlib/appmsg.cpp` ship
   update handlers, `ProcessFractions` region `:~800-830`), any
   extrapolation/interpolation, correction blending or snapping (`Igc`
   `shipIGC` `SetPosition`/`SetVelocity` and dead-reckoning between updates,
   `WinTrek` per-frame update). Say exactly what native does when a new
   pose arrives (snap, blend, velocity-extrapolate) and at what rate.
2. **Port:** the MonoGame client's remote-ship path (ClientWorld ship
   update receipt, `Latest` pose, any P448/P448b dead-reckon re-seed, the
   render-time position used for remote ships). Cite file:line.
3. **Diff and measure:** list the concrete differences. Measure against a
   local server with an MCP 10v10 at 20 Hz: per remote ship, the
   frame-to-frame rendered position delta vs expected motion, and count
   visible snaps (e.g. a correction > N units in one frame). Give numbers.
4. **Recommendation:** the smallest native-faithful change that removes the
   visible snaps (with the native citation), or the evidence that native
   snaps the same way. Rank options and estimate effort.

## Deliverable

The report `docs/reports/P552_REMOTE_SHIP_SMOOTHING_AUDIT.md` (committed on
`master`, not pushed): native citations, port citations, diff table,
measurements, recommendation. No other changes. No test suite needed;
build once to make sure you didn't touch code.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands in the foreground, blocking. **Never** start them in the background and then reply "waiting for it to finish".
