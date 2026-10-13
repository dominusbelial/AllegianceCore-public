# P623 — Parity wave 1: TARGETING PACK (class-filtered target cycling, target-my-attacker, nearest/previous)

Date: 2026-10-13 · Branch: `master` (not pushed) · Commit: single amended commit (`git log -1`)

## Why

P617 ranks targeting as the #1 thing veterans hit in their first five minutes: "trying to
**target enemy miners/bombers** with class filters — no such keys". Before this task the port
had one generic cycle (`T`) plus `U`/`Z` (nearest enemy/friendly ship/base) — none of the
class-filtered commands, no target-the-thing-that-hit-you, and no explicit nearest/previous
steps.

This task ports native's full Targeting family:

- the **33** `TK_Target{Enemy|Friendly}{Bomber|Fighter|Miner|Constructor|Lifepod|MajorBase}[Nearest|Prev]`
  commands (`Igc/igc.h:377–415`), each resolving a class to an ability-bit filter and stepping
  the selection forward / to-nearest / backward;
- **TK_TargetHostile** (`Igc/igc.h:314`) — snap to the ship that has done you the most damage
  recently, with native's 40-second decay window;
- the **nearest / next / previous** step triad native encodes via `c_ttNearest`/`c_ttPrevious`
  (`WinTrek.cpp:8963–8969 → FindTarget`, `common.cpp:505–510, 611–614`).

## Native behaviour classified

| Native behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Class = hull ability-bit filter (`c_habmThreatToStation` bomber, `c_habmFighter`, `c_habmMiner`, `c_habmBuilder`, `c_habmLifepod`, `c_sabmFlag` major base) | `WinTrek.cpp:10162–10331` → `FindableModel` `common.cpp:291–321` | design | `ClassRoleBits`/`ClassFilterSpec`/`MajorBaseSpec` build the same abm; `MatchesFilter` tests "shares at least one ability bit" (`HasCapability`, `hullTypeIGC.h:243–245`) — the exact native test |
| Nearest = globally closest; next = advance one in list order; previous = retreat one; any with no current target = nearest | `TrekFindTarget` `WinTrek.cpp:8963–8969`; `FindTarget` `common.cpp:505–510` (force `c_ttNearest`), `:611–614` (walk + wrap) | design | `StepDirection` + `StepCycle(list, current, dir)` — Nearest=head, Next=+1 (wrap), Previous=−1 (wrap), all fall back to head when current absent. Steps only over the matching-only list, so a step never lands on a non-match |
| Side relation (enemy vs friendly) | `FindableModel` `common.cpp:269–278` | design | `TargetRelation` (P415) |
| Target the thing that did you the MOST damage recently, stepping to 2nd-worst when the worst is already selected; 40 s rolling window (8 × 5 s slots) | `WinTrek.cpp:10101–10124`; `igc.h:4926–4928`, `DamageTrack::SwitchSlots` `common.cpp:2572–2589` | design | `HostileDamageTracker`: per-attacker damage bucketed into 8 × 5 s slots, totals reflect the last ~40 s; `SelectHostileTarget` picks the top, steps to 2nd if top is already current, falls back to nearest enemy |
| In-process damage-track population (the client simulates its own ship) | `DamageTrack::ApplyDamage` `common.cpp:2592–2617` | design | Port sim is server-authoritative: a new **`VICTIM_DAMAGED(217)`** targeted unicast carries the attacker id + damage to the struck ship's own peer, feeding the client's window — the wire analogue of the native bucket |
| `defaultinputmap.mdl` default keys: `O/I/K/H/J` × plain/Shift/Ctrl = next/nearest/prev | `Artwork/defaultinputmap.mdl`; `igc.h:377–415` | design | Adopted verbatim for the five keyed classes; native-wins the occupied keys (see "Default keys" below) |

**Explicitly NOT in scope (documented, not built):** `TK_TargetFlag`/`TK_TargetArtifact`
(the treasure-targeting commands, `igc.h:388–389/402–403/416`) require treasure-entity
classification and a neutral/enemy treasure side relation that this port's client does not yet
surface; they remain `missing` in the matrix for a later parity wave. `TK_TargetCommand`
already existed (P415, command-object targeting) — no `GetCommandTarget` concept exists in this
port (documented in P417).

## What was implemented

### 1. Class-filtered targeting (the 33 commands)

- **`Game1.TargetClass`** enum (Bomber/Fighter/Miner/Constructor/Lifepod) + **`ClassRoleBits`**
  mapping each to its `HabmCapabilities` bit (native abm: `HabmThreatToStation`, `HabmFighter`,
  `HabmMiner`, `HabmBuilder`, `HabmLifepod`).
- **`ClassFilterSpec(relation, class)`** → a ships-only `TargetFilterSpec` with that role bit;
  **`MajorBaseSpec(relation)`** → stations any-cluster flagged `c_sabmFlag`.
- **`StepDirection`** (Nearest/Next/Previous) and **`StepCycle(list, current, dir)`** implement
  the native triad (wrap; nearest fallback). `SelectFilteredTarget`/`SelectFilteredTargetForKey`
  now take a direction and are reused by every class command.
- **`InputActionCatalog`**: all 33 commands registered as rebindable actions using **native
  names** from `trekinput.cpp:1459` (the command-name table) and native default keys where
  `defaultinputmap.mdl` assigns one. `Game1.HandleP623TargetingKeys` dispatches them edge-once,
  reading Ctrl=prev > Shift=nearest > plain=next (so a `Shift+O` press selects nearest, never
  also next). Appear in the help screen (`KeyBindingTable.All` ← `ActionMap.ToHelpGroups`).

### 2. TK_TargetHostile (target the thing that did you the most damage recently)

- **Server:** `Sector.ResolveCombatCollisions` raises a new `IIgcSite.VictimDamagedEvent`
  (attacker ship id, victim ship id, damage) immediately after a ship's `ReceiveDamage` returns
  real damage (covers the projectile and the missile single-target calls); `Server` unicasts a
  **`VICTIM_DAMAGED(217)`** wire message to the **struck ship's own peer only** —
  `Server.Combat.BroadcastVictimDamaged`.
- **Client:** `ConnectionSession` → `ClientWorld.Apply(VictimDamaged)` buffers the attribution;
  `Game1.UpdateHostileTracking` drains it into `Game1.HostileDamageTracker` — a per-attacker
  8×5 s slot window (native `c_iDamageSlotCount`×`c_dwDamageSlotInterval`, `igc.h:4926–4928`)
  giving a 40 s decaying window.
- **`SelectHostileTarget`** (`TK_TargetHostile`) selects the highest-total damager, steps to the
  second-highest when the top is already the current target, and falls back to the nearest enemy
  ship with no buckets — native `WinTrek.cpp:10101–10124`.
- Native leaves `TK_TargetHostile` **unbound by default** (`defaultinputmap.mdl` has no 91);
  the port mirrors that: the command is registered + F6-rebindable, not bound.

### 3. HUD class-filter feedback

Class-filter no-match already rides the existing `_targetNoMatchNoun` banner
(`DrawTargetNoMatchFeedback`, `Game1.Rendering.cs:2945`) — pressing `O` with no enemy bomber in
sector shows "no enemy bomber in sector"; a successful class select logs `target <noun>: …` and
clears the banner. That is the class-filter feedback native conveys (native shows the single
"no <x>" line; it has no special filter indicator).

### 4. Default keys (native wins)

The new commands take native `defaultinputmap.mdl` defaults. Where a native default collided with
a port-only in-flight binding, native won and the port binding was moved:

| Native target key (this task) | Displaced port binding | Moved to | Notes |
|---|---|---|---|
| `O` EnemyBomber | — (ForceReady stays on `O`, pre-flight only, no in-flight clash) | — | `O` is free in flight |
| `I` EnemyMiner | `InvertMouse` | `Ctrl+M` | related to `M` ToggleMouse |
| `K` FriendlyBomber | `Suicide` (native default is `Shift+Ctrl+D` — kept as native default) | `Alt+K` | |
| `H` FriendlyMiner | `Help` | `F1` | **F1 is native's real Help key** (`defaultinputmap.mdl` (112,8,204)=TK_Help) |
| `J` FriendlyLifepod | `ToggleMusic` | `Alt+J` | |

`ToggleHud` was on `F1` (which became Help) → moved to `Ctrl+F1`.

Commands native leaves unbound (fighter/constructor/major-base × nearest/prev, and hostile) are
registered as rebindable commands; because the help overlay's `EveryCell`/`NoDuplicateKeys`
invariants forbid empty/duplicate key cells, `ActionMap.ToHelpGroups` now omits `Keys.None`
(unbound) rows from the overlay — they remain in the catalog and are F6-bindable.

## Modern alternative considered

- **Input**: native ships a hard-coded rebindable map; the port (since P625) has a data-driven
  `ActionMap`/`InputActionCatalog` — the modern idiom. This task adds 34 commands to that catalog
  rather than cloning ~30 near-identical methods, and reuses the live help overlay so a rebind is
  reflected immediately. Chosen: the existing data-driven approach; the "collapse ~30 handlers
  into one parameterized filter" pattern is the port's established convention.
- **Damage attribution**: native propagates damage buckets in-process (same-process client sim).
  A modern thin-client/authoritative-server split sends only a small targeted attribution (one
  unicast to the victim) instead of replicating the damage simulation client-side — chosen. This
  is a single-peer send, zero-cost to every other client, and leaks nothing beyond the
  participant.

## Verification

- **Whole-solution build** (`dotnet build AllegianceCore.slnx -c Debug`): **0 warnings, 0 errors**.
- **Unit tests (new)**:
  - `P623TargetingPackTests` (12 tests): class→abm mapping; enemy-bomber/friendly-lifepod filter
    correctness; major-base spec; nearest/next/previous step semantics incl. wrap + nearest
    fallback; hostile 40 s window ranking + decay + cross-slot aggregation. **12/12 pass**.
  - `ClientWorldVictimDamagedTests` (5 tests): `VictimDamaged` wire round-trip, fixed 12-byte
    layout, wrong-FMID rejection, world apply + drain-exactly-once, non-positive-damage ignored.
    **5/5 pass**.
  - Updated `HelpOverlayTests.Table_HasTheHelpSelfEntry` (Help is now on `F1`, native's key) and
    `P625KeyMapTests` conflict-flow samples (K is now `TargetFriendlyBomber`, so the single-owner
    conflict key moved to `X`/`LeaderCommand`). All pass.
- **Full suite** (`scripts/run-tests.sh`, P471), two consecutive runs on the final code:
  - **Run 1** — 2 failures, both **ledgered load-flakes** unrelated to targeting
    (`TEST_FLAKES.md` line 216 `LiveChatSectorWireTests.Individual_AllSector…relay…`;
    line 432 `GameOverPlayersWireTests.Win_by_elimination_broadcasts…`). Everything else passed
    (Core 4007/4009).
  - **Run 2** — 1 failure, a **timing flake** in `Transport.ConsoleLogNonBlockingTests.
    Verbose_gate_silences_and_reenables…` (diagnostic-log thread flush race, unrelated to
    targeting). Otherwise all green (MonoGame 1875/1878, ClientCore 669/669, Core 4008/4009).
  - Per the shared rule I report each run as it happened and did not re-run a failing suite;
    none of the flakes touch the targeting/hostile/wire paths P623 changed.

### Live proof (real client window, 2560×1440, 15v15 showcase)

Ran `scripts/live-proof.sh` (P572) on a local server with the `spawn_scenario` showcase (15
combat bots per side, both sides), a real windowed MonoGame client on side 0, and drove the
class-filter keys through a new `--auto-target-class rel:cls` automation hook
(`Game1.AutoProof.cs`) that exercises the **real** `HandleP623TargetingKeys → SelectClassForKey →
FilterCandidates/MatchesFilter` path and takes a real backbuffer autoshot. Assets in
`docs/reports/P623_assets/`:

- `p623_bomber_filter_nomatch_hud.png` — the client pressed the **enemy-bomber** filter (O) and
  the HUD correctly shows the class-filter feedback **`NO ENEMY BOMBER IN SECTOR`** (the
  showcase faction's only bombers are **Stealth Bombers**, cloaked → correctly NOT targetable
  per the native visibility rules; the filter returns the truthful no-match).
- `p623_miner_target_label_8097.png` — the **enemy-miner** filter (I) selected the real side-1
  native **miner `Ship 8097`** (HUD target label; client log
  `[monogame] target enemy miner: Ship 8097 (#8097)`), and the autopilot engaged toward it.
- `p623_launch_omega.png` — launch frame in the shared sector.

Honest limitations on a *perfect* bracket frame: MCP wire bots refuse to pilot **miners** by
design ("not a player-buyable combat ship"), and the native initial miners are **nomadic** (they
warp between sectors continuously, so the 3-D bracket rarely coincides with a captured frame —
the selection + HUD label are caught instead). The showcase's bombers are **stealth (cloaked)**
and therefore untargetable until they decloak, so the enemy-bomber frame correctly shows the
no-match feedback rather than a bracket. The class-filter machinery itself is pinned precisely by
the 12 unit tests above; these real-window frames prove the end-to-end key → handler → HUD path
in a genuine 15v15 client.

**Mandatory live-bot check (P623 touches the wire).** Because VICTIM_DAMAGED(217) adds a
server→victim send, the mandatory rule required a live bot run showing the showcase bots leave
their home sectors, reach the target and fight — not just idle. The same 15v15 showcase runs
above satisfy it: bots converged on Alderan (the client reached Alderan mid-proof, minimap
`YOU: Alderan`), the server logged continuous combat (≈249 combat events in `server.out`), and
real kills occurred (the in-window kill feed showed `mcp-b028… was killed`, `mcp-b030…
was killed`). The added message is a single targeted unicast to the victim only; it did not
alter the existing broadcast/transport path (bots reached and fought normally).

## Parity matrix / scorecard refresh

`docs/parity/parity-matrix.csv` updated: **34 rows → `done`** (TK_TargetHostile + the 33 class
commands; Flag/Artifact remain `missing`, out of scope). Commands area refreshes to
**done 100 / partial 26 / missing 64 / na 13 = 52.6% unweighted, 62.4% veteran-weighted** (from
P617's 61 done / 32.3% / 42.8%). Total matrix: **done 257 / 438 rows, 62.7% unweighted,
69.2% weighted** (from P617's 52.9% / 61.1%).

## Files changed (P623)

New: `src/Allegiance.Core/Wire/VictimDamaged.cs`, `tests/…/P623TargetingPackTests.cs`,
`tests/…/ClientWorldVictimDamagedTests.cs`, `docs/reports/P623_TARGETING_PACK.md`,
`docs/reports/P623_assets/*.png`. Modified: `Game1.Targeting.cs`, `Game1.FlightInput.cs`,
`Rendering/InputActionCatalog.cs`, `Rendering/InputBindings.cs`, `ClientCore/Session/ConnectionSession.cs`,
`ClientCore/State/ClientWorld.cs`, `Core/Model/Sector.cs`, `Core/Sim/IIgcSite.cs`,
`Core/Sim/RecordingIgcSite.cs`, `Server/Server.Combat.cs`, `Server/Server.Lifecycle.cs`,
`Server/ServerHost.cs`, the two updated test files, `LiveProof/AutoProofPlan.cs`,
`LiveProof/AutoProofMachine.cs`, `Game1.AutoProof.cs` (the `--auto-target-class` live-proof hook),
and `docs/parity/parity-matrix.csv`.

## Honesty notes

- The generic friend/foe/base nearest-cycling keys (`U`/`Z`) keep the port's established
  collapsed nearest→cycle behaviour (P415/P587); the explicit Nearest/Next/Previous triad is
  delivered on the class family via the new keys, which is the P617 #1 ask ("target enemy
  miners/bombers with class filters").
- Flag/Artifact treasure targeting is deliberately deferred (needs treasure-entity
  classification) — flagged for a later parity wave.
- The suite's two observed failures (run 1) are ledgered load-flakes
  (`TEST_FLAKES.md` lines 216, 432), unrelated to targeting.
