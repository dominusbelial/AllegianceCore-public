# P322 — Server cleanup batch: connection-cache leak, SWAP_PART validation, thread pruning

Three individually-small, mechanical stability findings from the `zai`/glm-5.3
full-codebase server review
(`~/Documents/Domingo-Llm-Wiki/pages/allegiance-server-review-glm53-full-codebase.md`)
landed as one batch on `master` (commit on master only; **not pushed** — Domingo
pushes). Separate concerns, separate edits, one clean-tree commit set.

All three are in the **port** (`AllegianceCore`); native source at
`~/Development/Allegiance` was **not** modified (read-only reference).

---

## 1. M1 — connection-scoped caches never freed on disconnect

**Finding:** `Server.ReleasePeer()` freed `_pendingSideSelect`, `_peerShips`,
`_peerNames`, `_playerReady` (and matching `_pendingJoinRequests`), but never
touched the per-connection status/cluster/ping trackers or the composite-keyed
teammate caches. On a long-uptime server with connection churn those
collections grew monotonically (and interacted with H4's ship-id wraparound).

**Fix** (`src/Allegiance.Server/Server.cs`):
- `ReleasePeer` now removes the connection-keyed entries **before** the
  idempotent early-return (so a second release is still a no-op):
  `_lastStatusSector`, `_lastStatusTick`, `_lastClusterInfoTick`,
  `_lastPingQuery` (Server.cs:1461-1470).
- After the ship is retrieved, every composite `(recipient, ship)` entry where
  the departing peer is the **recipient** OR the departing ship is the
  **subject** is freed, plus the per-subject-ship snapshot cache
  (Server.cs:1481-1497):
  `_teammateExportSent`, `_teammatePlayerInfoSent`, `_teammateLoadoutVersionSent`,
  `_teammateLoadoutCache` (keyed by subject ship id), `_lastTeammateStatusTick`.

> **Naming note:** the review brief called the P62 teammate cache
> `_teammateLoadoutSent`; in the live tree P317 renamed the two loadout-tracker
> fields to `_teammateLoadoutCache` (one snapshot per subject ship) and
> `_teammateLoadoutVersionSent` (per (recipient, subject) dirty-compare). The
> fix cleans both actual fields.

**Live proof:** `ConnectionCacheCleanupTests` boots a teams:1 server, connects
two same-side bots (which populate the teammate caches via the per-tick
broadcast passes), disconnects them, and asserts every one of the twelve caches
drains to zero — repeated over two cycles so any leak compounds and is
unmistakable. With the fix removed the test fails with the caches stuck at
`(…, 2, 2, 2, 0, 2, 2, 2, 2, 2)` (verified red, then restored).

## 2. M5 — SWAP_PART negative mount range unvalidated

**Finding:** `Ship.ResolvePart()` does `mount < 0 ? _cargo[CargoSlotForMount(mount)]
: …` with `CargoSlotForMount(mount) => -mount - 1`. The wire `Mount` is a signed
char (`Wire/SwapPart.cs`, `MountOld`/`MountNew` as `sbyte`), so a client can send
any value in [−128, 127], but only [−1, −5] (`-c_maxCargo`) addresses a real cargo
slot. A mount below −5 (e.g. −6..−128) indexed `_cargo[-mount-1]` past the array →
`IndexOutOfRangeException` — exception-driven control flow on attacker input,
caught only by the dispatch's generic catch and mislogged as a "bad message"
(the review's L6 mislogging, **not** fixed here, just no longer made worse).

**Fix** (`src/Allegiance.Core/Sim/Ship.cs:2095-2111`): `TrySwapPartWithCargo` now
rejects `mountOld < -CargoCapacity || mountNew < -CargoCapacity` with `false`
*before* `ResolvePart` runs — the identical range guard `TryApplyReloadEntry`
(RELOAD) already applies (`mount < -c_maxCargo`, `FedSrv.CPP:6531`). Non-negative
mounts address the equipped list (a `FirstOrDefault` scan, never an index) so
they are inherently safe and untouched. Clean rejection falls through to
`HandleSwapPart`'s existing silent-no-op log (Server.cs:7034), a genuine fix to
the control-flow smell, not a band-aid.

**Live proof (2 tests):**
- Unit-level MUST-FAIL `SwapPart_out_of_range_negative_mount_is_rejected_cleanly_not_thrown`
  — calls `Ship.TrySwapPartWithCargo` with −6/−7/−128 (source) and −6
  (destination) and asserts it returns `false` without throwing; also asserts a
  valid in-range −1 still swaps (guard doesn't over-reject). Removing the guard
  makes it throw → red (verified, restored).
- Wire-level `SwapPart_wire_out_of_range_negative_mount_is_rejected_no_broadcast_no_change`
  — a real bot sends `SWAP_PART` with `mountOld = -6`; asserts no sector
  broadcast and the server loadout is untouched.

## 3. M9 — `TcpTransport._threads` grows forever

**Finding:** `TcpTransport.Spawn()` did `lock (_gate) _threads.Add(t)` and never
removed entries, so every connection's read-loop thread stayed in `_threads`
after the connection closed; long-uptime servers with churn accumulated an
unbounded list of dead `Thread` objects (and a pointless `Join(500)` in
`Shutdown`).

**Fix** (`src/Allegiance.Core/Transport/TcpTransport.cs:465-491`): each Spawn'd
thread now prunes itself on termination — `lock (_gate) _threads.Remove(
Thread.CurrentThread)` in a `finally` wrapping the body. `_threads.Add(t)` in the
outer frame strictly precedes `t.Start()`, so the add always happens-before the
body's remove (no ordering race). `_threads` stays bounded by the *live* thread
set (just the host's accept loop). Safe with `Shutdown`: joining a finished
thread is a no-op; pruning only drops already-terminated references.

**Live proof:** `TcpTransportThreadLeakTests` observes the real server transport
via `server.Host.Transport`, records the baseline (1 = accept loop), then
connects/disconnects a bot three times, asserting the tracked count returns to
baseline each time. Without the self-prune the count sticks at baseline+1 →
red (verified, restored).

---

## Scope boundary / exclusions

- CRITICAL/HIGH findings (C1/C2/H1-H6, now P311-P318) — separate tasks.
- Other MEDIUM findings (cross-side visibility throttle, `GetShipUpdateCandidates`
  caching, per-side batch cadence, oxygen-death ghost ship, stale `MiningAsteroid`,
  NPC BFS caching) — P323/P324, separate tasks.
- Review L6 (the dispatch's generic "bad message" mislogging) — explicitly
  **not** fixed here; M5 simply stops contributing a throw to it.
- Native `~/Development/Allegiance` — untouched.

## Test seams added

- `Server.ConnectionScopedCacheCounts()` (internal, Server.cs:341-353) — M1 live
  observability (InternalsVisibleTo to Core.Tests).
- `TcpTransport.TrackedThreadCount()` (internal, TcpTransport.cs:493) — M9 live
  observability.

## Build / test status

- Whole-solution build (Server + Core + Bot + Lobby + tests): **0 warnings, 0 errors**.
- New tests pass in isolation: `ConnectionCacheCleanupTests`,
  `TcpTransportThreadLeakTests`, and the two M5 additions to `CargoBayReloadTests`.
- Every fix proven **must-fail-if-removed** (each reverted → its test red, then
  restored green).
- Full suite run(s): see commit message / final handoff for the run log; the only
  expected interruption is the pre-existing `LiveTurretGunnerBindingTests`
  timing flake (documented machine-state flake).
