# P62 — Broadcast a teammate's ship EXPORT, not just its status

Date: 2026-09-03
Branch: `master`
Scope: `AllegianceCore`-only (no Windows VM, no native `Allegiance` repo involvement).
Base: `9e28473` (P61: real per-part loadout).
Baseline suite before change: 2280 passed / 0 failed (`/home/domingo/.dotnet/dotnet test`).

## Why this exists

Live debugging (real WOPR bots, real server, temporary diagnostics — since
reverted) established the real chain by which a commander can never dispatch a
miner, correcting an earlier wrong diagnosis (that the bot never sent
`FM_CS_PING` — it does, confirmed live):

1. `MessageReceiver_FMD_CS_PING` fires correctly and
   `UndockMinersAndStartMining` genuinely runs (both confirmed live).
2. Its filter is `GetSide().GetShips().Where(p => p.GetName().StartsWith(".Miner"))` —
   but `GetSide().GetShips()` was observed live to stay stuck at 2 (never
   growing to include the miner), so the filter always sees zero candidates.
3. `GetShip(shipID)` (the lookup `MessageReceiver_FMD_S_SHIP_STATUS` uses to
   apply a `SHIP_STATUS` update) is a **lookup, not a create** — it can only
   update a ship object the client already knows exists.
4. `AllegianceCore` sends `FM_S_EXPORT(26)` (the message that actually
   **creates** the client-side ship object: `appmsg.cpp:80-105`,
   `CreateObject(OT_ship, ...)` → `news` a `CshipIGC`) **only to the joining
   peer, for that peer's own ship** (`Server.cs:~767-823` here; the
   `OwnShipExportId = -1` / NA trick documented in the same block). No
   already-connected peer is ever told a *different* ship exists. P58's own
   same-side `SHIP_STATUS` broadcast (`Server.cs:440-469` pre-change) was
   therefore correctly wired and correctly sent, but structurally unable to
   help until the receiving ship object exists at all.

P62 closes that gap: broadcast a real, non-NA `ExportShip` for each same-side
teammate's ship to every already-connected peer, once.

## The second requirement (character-name memory), done first

`UndockMinersAndStartMining`'s filter is a **name** check
(`GetName().StartsWith(".Miner")`), and `ExportShip.CharacterName` is what
becomes that name client-side. `Server.cs` did not remember any peer's own
character name past the logon handshake — `req.CharacterName` (`LogonRequest`)
was a local used inline at the logon handler and then discarded; no
`_peerNames`-style dictionary existed and neither `LiveShip` nor `Ship`
carries a name field. Without fixing this first, the new broadcast would
compile, send, and even make `GetSide().GetShips()` grow — but every
broadcast ship would show up with an empty/wrong name and the `.Miner` filter
would still match nothing.

So, added first and independently verifiable: a
`Dictionary<ConnectionId, string> _peerNames` (Server.cs:62), populated at the
exact point `req.CharacterName` first becomes available for a peer that will
own a ship — `Server.cs:684`, the same `if (validated)` block that seats
`_peerShips[from]` — and released on disconnect at `Server.cs:619`. It follows
this file's existing dictionary-per-concern style (`_lastStatusSector` /
`_lastTeammateStatusTick` etc.) and is sim-thread-only like them, so no lock.

## What changed

**`src/Allegiance.Server/Server.cs`** (the only production file for the
feature):
- `_peerNames` — Server.cs:62 (above).
- `_teammateExportSent` — `HashSet<(ConnectionId Recipient, short ShipId)>`
  at Server.cs:70. Deliberately a `HashSet`, distinct in shape from the
  time-windowed dictionaries (`_lastTeammateStatusTick` etc.): there is no
  cadence dimension. A repeat send would be wrong here, not merely wasteful —
  a second `CreateObject` for an id the client already owns could misbehave
  client-side. One entry is added per (recipient, subject) pair, once.
- `BroadcastShipStates` P62 block (Server.cs:481-519), at the front of the
  existing P58 same-side teammate loop (Server.cs:474-527):
  - For each same-side subject ship (skip own; skip cross-side — the P58
    gates preserved), if the (recipient, subject) pair has not yet had its
    export sent **and** the subject peer's name resolves, send a real
    `ExportShip` with `ShipId = teammateId` (**real id, NOT -1/NA**),
    `CharacterName` from `_peerNames`, `HullId`/`SideId`/`PilotType` sourced
    exactly as the own-ship export does (Server.cs:512-518; the log line
    `[teamexport] peer <recipient> <- ship <id> '<name>' hull <hullId>` at
    Server.cs:513). Then mark the pair sent.
  - Ordering: the status send that follows (Server.cs:520-528) is gated on
    `_teammateExportSent.Contains(...)` so a `SHIP_STATUS` for a brand-new
    pair never races ahead of its own export within the same broadcast pass.
    The export is emitted first in the same loop iteration (or, equivalently,
    already emitted on an earlier tick), satisfying requirement #4. On the
    very first tick for a pair, both go out in-order over the same socket; on
    every later tick only the status refreshes on its existing 100-tick
    cadence. If a subject peer's name never resolves (defensive), neither the
    export nor the status is sent for that pair, keeping them in step.
  - Field choices match the own-ship EXPORT exactly except `ShipId` and
    `CharacterName`: `nParts = 0` and `baseObjectID = -1` are unchanged (a
    teammate's real P61 loadout and docked-carriage — already carried by
    `SHIP_STATUS`/`FM_S_DOCKED` — are both explicitly out of scope). ObjectType
    = OT_ship(0), PilotType = c_ptPlayer(10), identical to the own-ship send.
- Method doc comment updated to mention the one-shot EXPORT (Server.cs:385-391).

**`src/Allegiance.Bot/Bot.cs`** (test support only — needed so the loopback
`Bot` can observe the new message): an `Exports` list/property
(Bot.cs:129/132) and a decoder case (Bot.cs:290-298). The case peeks the
4-byte `objecttype` at message offset 8 and only decodes `OT_ship(0)` — the
server uses the same fmid 26 for clusters(13)/warps(7)/stations(1), and
decoding those as ships throws. (This was the subtle bug that made every
loopback multi-bot test fail at first: an unhandled decode exception killed
the bot's transport thread before its SET_CLUSTER arrived.)

**`tests/.../ShipStatusServerTests.cs`** — `Teammate_ship_is_exported_once_with_real_id_and_name_but_enemy_never`
(ShipStatusServerTests.cs:270). Three bots / two teams (round-robin default
spawn ⇒ a same-side pair and a cross-side singleton), asserted on the wire:

## Proven at the wire level (loopback, no VM)

1. **Real id + name**: the observer receives an `ExportShip` with `ShipId`
   equal to the subject's actual assigned ship id (asserted `> 0`, i.e. not
   -1/NA) and `CharacterName` equal to the subject's real logon name
   (ShipStatusServerTests.cs:316-321), `SideId` == the subject ship's side,
   `PilotType` == c_ptPlayer, hull present.
2. **Ordering**: polled in small pump steps so the moment the observer's
   first `SHIP_STATUS` for the subject ship arrives, the subject `EXPORT` is
   already present (ShipStatusServerTests.cs:307-314). No first status is
   ever observed without its export already delivered.
3. **Exactly once**: after pumping ~350 more sim ticks (several 100-tick
   teammate-refresh intervals), the observer still has exactly one export for
   that subject — the one-shot set held (ShipStatusServerTests.cs:326-328).
4. **Same-side only**: the cross-side ship's id never appears in the
   observer's export feed (ShipStatusServerTests.cs:332-333).

Confirmation cadence: full suite (2281 tests, incl. the new one) ran clean 11
times consecutively after one initial warm-up flake in an unrelated,
pre-existing timing-sensitive network test (not in the touched classes — the
ShipStatusServerTests class passed 10/10 and the new test 12/12 individually,
all green). `dotnet build` 0 warnings.

## What Claude Code's own live-VM pass must confirm next

This fix proves the **wire-level half**: the managed server now exports a real
teammate ship object to every already-connected peer once, before any status.
What it cannot prove without a live VM is the **client-object-model payoff**,
the thing this whole roadmap phase exists for:

- Does a real commander client's `GetSide().GetShips()` actually **grow to
  include** a real `.Miner`-named teammate ship on its side (i.e. does the
  imported `CshipIGC` appear in the side's ship list)?
- Does `UndockMinersAndStartMining`
  (`CommanderResearchAndExpand.cs:1555-1622`) then finally **find** that
  miner by the `.Miner` prefix and dispatch a real order (undock → mine →
  sell)?

Nothing after the client-side `CreateObject` is exercised by the loopback
proofs; the docs above cite the native-side handlers (`appmsg.cpp:80-105`,
`shipIGC.cpp:165-180`) as the *expected* behavior, which the VM pass must
confirm end-to-end.

## Out of scope (untouched)

- A teammate's real mounted-part loadout (P61) in the broadcast EXPORT —
  `nParts=0` stays, matching every other EXPORT site; a real `PartData[]`
  view of another ship is separate future work.
- Cross-side visibility — same-side only, matching P58.
- `NewShipInstance.cs` (fmid 31) — confirmed unused/no dispatch case; not
  wired up.
- `FM_CS_PING` / `UndockMinersAndStartMining`'s own logic / WOPR-native side —
  all confirmed working.

## Files touched

- `src/Allegiance.Server/Server.cs` (production: the only feature file)
- `src/Allegiance.Bot/Bot.cs` (test support: ExportShip capture on the loopback bot)
- `tests/Allegiance.Core.Tests/ShipStatusServerTests.cs` (tests)
- `MULTISHIP_EXPORT_P62.md` (this doc)
