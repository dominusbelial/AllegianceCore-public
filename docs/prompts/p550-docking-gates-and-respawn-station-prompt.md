# P550 — docking and respawn parity: Land/CapLand gates and the native landing check (reuse the carrier InGarage geometry), and respawn/rescue at the NEAREST station that allows restart

## Why this task exists

From P537/P531, **re-verified by Claude's spot-check (2026-10-05; wiki
`allegiance-p1-p200-parity-audit`)**:

1. **Any same-side ship docks at any station type.** Port docking is a
   point-in-sphere test with `DockRadius = 200` (`Sector.cs:1501`,
   `:~3440-3450`) and **checks neither `SabmLand` nor `SabmCapLand`**
   (`SabmCapLand` is unused outside `DataStationTypeIGC.cs:200`). Native
   gates on Land (fighters) / CapLand (capitals) (`Igc/shipIGC.cpp:725-727`),
   then uses landing-bay geometry `InGarage` (`:843,853`). **The port already
   has a real `InGarage` for carrier landing bays** (`Sector.cs:1041-1127`,
   `Collision/HullLandingBayCache.cs`). Reuse it for stations if native
   station docking uses the same garage test. Otherwise port native's
   station landing check exactly.
2. **Respawn/rescue station = the debug garrison or the first same-side
   station, not the nearest, and no `c_sabmRestart` filter.** Port
   `Server.Combat.cs:~1845-1874`. Native `FindTarget(... c_ttNearest ...)`
   with a `GetBase` fallback (`FedSrv.CPP:12034-12038`, `13066-13074`) and the
   restart-capability filter. Also check P540's client eject autopilot target
   choice agrees with the server's rescue choice.

## Scope

Fixes 1–2, cited to native. Keep the debug-mode garrison behaviour only if
native's debug mode has an equivalent; otherwise put it behind the existing
debug flag and say so.

## Explicitly out of scope

- Build timing, station starting hull/shield, building cost (separate).

## Proof

- Failing-first tests: a fighter can't dock at a station type without
  `SabmLand`; a capital needs `SabmCapLand`; docking requires the native
  landing geometry (not just a 200u sphere); respawn picks the nearest
  restart-capable station, skips one without restart, and falls back like
  native.
- **Live loopback:** a real client docks at a valid station (unchanged
  feel) and is refused at an invalid one. Eject far from base and the pod
  is rescued at the nearest valid station (reuse the P540b live proof).
- Report `docs/reports/P550_DOCKING_AND_RESPAWN.md`, plus dated notes in
  P537/P531.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
