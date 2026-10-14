# P483 — explain and fix the `partialGrants` in `run_hull_matrix` (requested parts not mounted even with the harness grant)

## Why this task exists

P473 (`7134562a`) made bots report what the server actually sold them. Its
live MCP run (`docs/reports/P473_BOT_HARNESS_ECONOMY_GRANT.md`,
"Live MCP run — grant ON vs OFF") shows that, **with the harness grant ON**,
most `run_hull_matrix` rows still report `partialGrants=2`:

```
OK hull=Adv Fighter grantedHulls=[15, 15] partialGrants=2
OK hull=Fighter     grantedHulls=[55, 55] partialGrants=2
OK hull=Gunship     grantedHulls=[22, 22] partialGrants=2
OK hull=Scout       grantedHulls=[14, 14] partialGrants=2
(3 planner "weapon does not mount" errors — not grant refusals)
```

What a partial grant means is exact.
`BotManager.EvaluateGrant` (`tools/allegiance-mcp/BotManager.cs:407-421`)
compares the **requested part ids** against the parts in the server's
`BuyLoadoutAck` and warns when any are missing. **It is not the tech
check.** The harness `ignoreTechGate` skips the part tech check as well
(`src/Allegiance.Server/Server.ShipCommands.cs:2463-2470`), and money is
$1,000,000. So two requested parts per bot are being dropped for some
other reason.

Exactly one of these causes is responsible, possibly more than one:

1. **The planner asks for parts that can't mount** on that hull. Native
   only mounts a part when `m_myHullType.CanMount(ppt, mountID)`
   (`shipIGC.h:1030`); the port does the same at the P334 hardpoint policy,
   `Server.ShipCommands.cs:~2471+`. That's a **tool bug** in
   `MatrixPlanner.cs` / `Catalog.cs`: the matrix would be testing loadouts
   that don't exist.
2. **The server drops a part native would mount.** This is a **real bug**,
   for example in mount numbering after P437's per-type rule, `Launchers`
   vs `PartTypes` resolution (P433), ammo/cargo items, or any other
   `[loadout] … skipped` branch in `ApplyLoadout`.
3. **The part is mounted, but the ack doesn't list it.** That's a
   wire/report bug: `BuyLoadoutAck.Parts` doesn't carry certain part kinds,
   such as launchers, cargo or ammo packs. Then `EvaluateGrant` would be
   raising a false alarm.

## Scope

1. **Diagnose first, with evidence.** Run `run_hull_matrix` live, as P473
   did: the FIFO stdio pattern, a standalone server with `testHarness` on,
   and the same hull set. For **every** missing part id on every row, record
   the part's name and equipment type, the requested mount, and the
   server's `[loadout]` log line explaining it (every skip branch in
   `ApplyLoadout` logs a reason), and assign it one of the classes above.
   Put the table in the report.
2. **Fix whichever classes are real, at their source:**
   - **Class 1:** the planner only requests parts that native `CanMount`
     on that hull's real hardpoints, using the same `CanMountPart`
     containment the server uses. Don't re-derive it. This also covers the
     3 "weapon does not mount" planner errors: those combos should be
     planned out with a reason, not attempted.
   - **Class 2:** fix the server path, with a native citation. Add a
     regression test that fails before the fix and buys that exact
     hull/part through `BUY_LOADOUT`.
   - **Class 3:** fix the ack contents to match native's
     `BUY_LOADOUT` acknowledgment shape, or, if native genuinely omits that
     kind, fix `EvaluateGrant` to compare only what native acks. Cite
     either way.
3. Re-run the live matrix. Grant-ON rows should show `partialGrants=0`,
   except for documented, legitimate cases.

## Explicitly out of scope

- The harness grant's design (P473), and real-player purchase rules (P441).
- Adding new behaviours or presets to allegiance-mcp (P474).
- Shop UI changes, unless class 2 shows the same server bug affects real
  players. In that case fix the shared server path, and note the
  player-visible impact in the report.

## Verification

- The diagnosis table, with a server log line for every missing part.
- Tests for each fixed class, failing first: a planner unit test for
  class 1, a `BUY_LOADOUT` wire test for class 2, and a codec or ack test
  for class 3.
- The live before and after `run_hull_matrix` output in the report.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P483_HULL_MATRIX_PARTIAL_GRANTS.md`. Cover the
   diagnosis table, which classes were real, the fixes, and the
   before/after matrix.
