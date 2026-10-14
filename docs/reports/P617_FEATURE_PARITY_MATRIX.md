# P617 — Feature-parity matrix: native client/server vs the port (audit)

Date: 2026-10-12 · Branch: `master` (not pushed) · Type: **audit only, no feature implementation**

## Why

Domingo found the old Allegiance community still active on Discord (game nights +
dev/bug-report/ui/textures/ice/core channels) and plans to approach them after more art
passes and a real push for feature parity. Veterans will judge the port on *"does it do
everything mine does"*, so this task measures parity systematically from native checklists
instead of discovering gaps one at a time.

Deliverables: `docs/parity/parity-matrix.csv` (436 rows) and this scorecard report.

---

## 1. Method

For every native checklist item (player commands, network messages, screens, parts/hull/
equip ability types, commander features, training/lobby flow) the port status was
classified as **done / partial / missing / na** with a port `file:line` cite (or a reason),
and weighted 1–5 by what a veteran pilot notices first. Native source
(`~/Development/Allegiance/src`) is the primary reference, read-only; line cites are against
that tree. Port code is `~/Development/AllegianceCore` (repo-root-relative paths).

Ground-truth native counts were extracted **mechanically** (regex over `Messages.h`,
`Igc/igc.h`, `WinTrek/*`), not recalled:
- **202** distinct `TK_*` player commands referenced in `trekinput.cpp` / `WinTrek.cpp` /
  `console.cpp` (enum in `Igc/igc.h:226–440`; the 13 non-commands — `TK_QuickChat0-9`,
  `TK_FOVIn/Out`, `TK_Obsolete1` — excluded).
- **136** `DEFINE_FEDMSG` wire messages (`Inc/Messages.h`, ids 1–200).
- **8** native part types (`ET_ChaffLauncher…ET_Afterburner`, `Igc/igc.h:~965`) + **24**
  ability bits (**15** `c_habm*` hull, **9** `c_eabm*` equip) + 7 cross-cutting subsystems.
- Screens/panes enumerated from `WinTrek/*screen*.cpp`, `*pane*.cpp` + feature-level screens.

---

## 2. Scorecard (unweighted and veteran-weighted "done %")

"done %" is computed over **non-na** rows only (na = not applicable to this port's
architecture). Weighted % weights each row by its 1–5 veteran-visibility weight.

| Area | rows | done | partial | missing | na | done% (unwtd) | done% (weighted) |
|---|---|---|---|---|---|---|---|
| **commands** (TK_ player commands) | 203 | 123 | 21 | 46 | 13 | 64.7% | 71.9% |
| **fedmsg** (network messages) | 136 | 91 | 34 | 0 | 11 | 72.8% | 73.6% |
| **parts** (types + ability bits) | 40 | 31 | 9 | 0 | 0 | 77.5% | 82.4% |
| **screens** (client screens/panes) | 27 | 16 | 7 | 1 | 3 | 66.7% | 73.6% |
| **commander** (team features) | 20 | 18 | 1 | 0 | 1 | 94.7% | 96.3% |
| **lobby** (training + lobby flow) | 12 | 9 | 3 | 0 | 0 | 75.0% | 76.5% |
| **TOTAL** | 438 | 288 | 75 | 47 | 28 | 70.2% | 75.2% |

> **P627 scorecard refresh.** Rows above are re-computed from the authoritative
> `docs/parity/parity-matrix.csv` on P627 (same formula as documented above:
> `done %` over non-na rows, weighted by each row's 1–5 weight). The **fedmsg**
> row is the audited correction from `docs/reports/P627_FEDMSG_WIRING_AUDIT.md`:
> BALLOT(17) and SHIP_STATUS(90) were false-positive "partials" (already consumed
> by the real client under their own class), PLAYER_RESCUED(16) was a genuine
> trivial gap now wired, and PROMOTE(189) was an over-claimed "done" (the server
> broadcast is not consumed by the real client). The other areas' numbers drift
> from the original P617 snapshot purely because parity-matrix.csv gained rows in
> the intervening P-tasks (e.g. the P625b action-map row).

Headlines
- **Commander/team features are essentially complete** (95–96%) — the tactical layer vets
  care about (wing orders, queued commands, investments, build/research, drones, ballots,
  alliances, chat/quick-chat, stats) is all present.
- **Commands are the biggest gap** (32% / 43%). The port implements a focused, hard-coded
  core binding set; the long tail of native niche bindings (per-class target stepping, swap-
  part/cargo keys, console modes, view cameras, training-mission clock) is absent.
- **Parts/ability types** are strongly implemented (77% / 82%); every ability bit is at
  least **decoded** from the core (`DataHullTypeIGC.cs`, `DataMissileTypeIGC.cs`).
- **fedmsg** 66% / 67% is a **conservative lower bound** (see §8 caveat): many
  server→client messages the classifier could not confirm a client dispatch-case for are
  in fact handled under renamed classes (e.g. `SHIP_STATUS`(90) is consumed by the client
  as `case ShipStatusMessage.Fmid`). A wiring verification pass is ranked below.

Checklist completeness (native total vs classified):
| area | native total | rows classified | covered |
|---|---|---|---|
| commands | 202 | 202 | 202/202 |
| fedmsg | 136 | 136 | 136/136 |
| part types | 8 | 8 | 8/8 |
| hull ability bits | 15 | 15 | 15/15 |
| equip ability bits | 9 | 9 | 9/9 |
| screens | 26 | 26 | 26/26 |
| commander | 20 | 20 | 20/20 |
| lobby | 12 | 12 | 12/12 |

---

## 3. Wiki-tracked fedmsg roadmap — refreshed facts

The `allegiance-fedmsg-roadmap` wiki page tracks coverage; the facts below are refreshed
against today's code (Claude syncs the wiki). Method: every `DEFINE_FEDMSG` in
`Inc/Messages.h` matched by fmid to the port's `src/Allegiance.Core/Wire/*.cs` codec
registry and to the server (`Server.Dispatch.cs`/`Server.cs`) and client
(`ConnectionSession.cs` `case X.Fmid`) dispatch sites.

- **Codec coverage: 125/136** ported (has a real wire codec class). The wiki's last
  documented tally was **123/136 (as of P200, 2026-09-13)** → **+2** since (PINGDATA/REQPINGDATA
  and any subsequently-landed codecs). Verified by direct extraction, reproducible.
- The **11 with no codec** are exactly the deliberate-not-built registry in
  `src/Allegiance.Core/Wire/NotSupportedFedMsgs.cs` (11/11 match): `STATIC_MAP_INFO`(11),
  `SET_TEAM_INFO`(15), `POSTER`(27), `URLROOT`(36), `ICQ_CHAT_ACK`(107), `MISSIONPARAMS`(113),
  `MUTE`(117), `SET_TEAM_INVESTOR`(122), `THREAT`(131), `ASTEROID_DRAINED`(180),
  `BANDWIDTH`(193). Each carries a documented `DeadInNative` / `NotApplicableToThisPort` /
  `MissingStruct` reason.
- **End-to-end dispatch handling (new dimension the wiki did not track):** by the fmid-set
  classifier, **83 done / 42 partial / 11 na**. The 42 "partial" are overwhelmingly
  server→client messages the server emits but where no client `case X.Fmid` was found —
  a **conservative count**: renamed client handlers (e.g. `ShipStatus`↔`ShipStatusMessage`)
  defeat name-based matching, so some of these are already handled. Treat the fedmsg "done"
  % as a **lower bound** until the §7 ranked verification pass confirms each of the 42.

---

## 4. Top 25 ranked missing/partial items (visibility × effort)

Each is a candidate delegated task with its native citation. Effort: S = small, M = medium,
L = large. Ranked by veteran-visibility × estimated effort.

**Core flight & combat feel**
1. **Class-filtered target cycling (enemy/friendly bomber · fighter · miner · constructor ·
   lifepod · major-base, + nearest/prev steps)** — the 33 `TK_Target*{Class}[Nearest|Prev]`
   commands (`Igc/igc.h:158–199`). Vets constantly hunt miners/bombers. Today the port has a
   single generic cycle (`T`) + `U`/`Z`; no class filter. Effort M.
2. **Target-most-recent-damager** (`TK_TargetHostile`, `Igc/igc.h:314`) — press to snap to the
   ship hitting you. Effort S–M.
3. **Explicit nearest/prev target steps** (`TK_TargetNearest`/`Prev`/`Friendly…` —
   `Igc/igc.h:298–304`) — generic cycle wraps but there is no "previous target". Effort S.
4. **Match-speed** (`TK_MatchSpeed`, `Igc/igc.h:313`) — match velocity of the target for
   formation/docking. Effort S.
5. **Heading/speed lock (vector lock)** (`TK_LockVector`, `Igc/igc.h:280`) — lock current
   heading+speed. Effort S.
6. **Vertical strafe** (`TK_ThrustUp/Down`, `Igc/igc.h:269–270`) — port has Q/E lateral
   strafe only. Effort S.
7. **Rear / side look cameras** (`TK_ViewRear`, `TK_ViewFrontLeft/Right`, `TK_ViewLeft/Right`,
   `TK_ViewRearLeft/Right` — `Igc/igc.h:249–253,296,320`). Effort S–M.
8. **Tactical command-view camera** (`TK_ViewCommand`, `Igc/igc.h:261`) — a free/strategic
   overhead camera; the starmap exists but no true 3-D command view. Effort M.
9. **In-flight part/cargo cycling + drop cargo** (`TK_NextPart/PrevPart`, `TK_NextCargo`,
   `TK_DropCargo`, `TK_Reload` — `Igc/igc.h:232–237,344,354,355`). Mounting exists only on the
   docked loadout screen; no mid-flight selection keys. Effort M.

**Commanding**
10. **Commander drone-cam / follow-subordinate** (follow your commanded drone's view). Effort M.
11. **In-flight console/team mini-pane** (`TK_ConModeMiniTeam`, `TK_ConModeTeam` —
    `Igc/igc.h:291,322`) — full team console absent mid-flight. Effort M.

**Lobby / launch flow**
12. **GUI create-game wizard** (native `WinTrek/newgamescreen.cpp`) — missions today are
    created from server config; no in-client wizard. Effort M.
13. **Kick / ban / mute moderation UI** (native moderation flow) — currently server
    admin-cheats only, no lobby UI. Effort M.
14. **Full training-mission tutorial UI** (native `WinTrek/trainingscreen.cpp` + TM clock
    keys `Igc/igc.h:313,340–343`) — `docs/NATIVE_TRAINING_SCRIPT.md` covers scripted server
    scenarios; the client tutorial UI and `TK_IncreaseTMClockSpeed` etc. are absent. Effort L.
15. **Career leaderboard screen** (native `WinTrek/leaderboardscreen.cpp`) — in-mission
    scoreboard exists (P265); career ladder is absent. Effort S.

**Comms**
16. **Chat-buffer scroll** (`TK_NextCommMsg`/`PrevCommMsg`, `TK_ChatPageUp/Down` —
    `Igc/igc.h:227,328–329`). Effort S.
17. **Dedicated vote keys** (`TK_VoteYes/No`, `Igc/igc.h:339–340`) — ballots work (P187) via
    menu/chat; no keybind. Effort S.

**HUD / F-keys**
18. **F5 / F11 binding conflict** — P580 wired native F-key external cameras to `F4/F5/F7/
    F10/F11` (`Game1.FlightInput.cs:813–817`) which **collide with `F5` (music volume down,
    :583) and `F11` (screenshot, :528/:1380)**; `F4` overlaps `Alt+F4` quit. Needs a binding
    decision (camera vs music/screenshot). Effort S (fix) + design decision.
19. **Complete F1–F12 contextual overlays** — the port has `H` help + credits; native's
    full F-key overlay set is not replicated 1:1. Effort M.
20. **Radar mode cycle** (`TK_CycleRadar`, `Igc/igc.h:325`). Effort S.

**Wire-message client wiring**
21. **Client-side S-message wiring verification pass** — confirm/disambiguate the ~42
    "partial" server→client messages (are they handled under renamed classes or truly
    unconsumed?). Produces the authoritative fedmsg tally and fixes false
    positives like `SHIP_STATUS`. Effort M (audit) — prerequisite to trusting the fedmsg %.

**Screens**
22. **Full character-info sheet** (native `WinTrek/CharInfoScreen.cpp`) — port has the
    pilot-info overlay (P275); the complete career char sheet is absent. Effort S–M.
23. **Full team pane** (native `WinTrek/teampane.cpp`, `teamscreen.cpp`) — side-select +
    partial team display only. Effort M.

**Parts / abilities depth**
24. **Light-ripcord ability full behaviour** (`c_habmIsLtRipcordTarget`/`c_habmCanLtRipcord`,
    `Igc/igc.h:738–739`) — decoded but only partial runtime behaviour. Effort S.
25. **Fuel/ammo pack economy + quick-ready** (`ET_Pack`; `c_eabmQuickReady`) — cargo-bay
    reload exists (P192) but no fuel-consumption economy / quick-ready timing. Effort M.

Also flagged (not a numbered task but high impact): **keyboard rebinding is not exposed** —
the port uses a hard-coded, non-configurable key map (`KeyBindingTable.cs`, documented as
intentionally drifting), whereas native ships a rebindable `defaultinputmap.mdl`. Vets with
custom binds will notice this immediately; a rebinding UI is a natural future task.

---

## 5. What veterans will notice in the first 5 minutes

Strong (already there)
- **Flight feel** — native-faithful kinematics, own-ship prediction (P447/448/502/505),
  native own-ship model (P507/508), collision & damage, energy/cloak/boost. Core combat is
  close.
- **Firing & weapons** — group/single fire, prev/next weapon, missile, chaff, mines, warp
  bombs, target brackets & lead indicator.
- **Comms** — Enter chat (team/all), `R` quick-chat with orders, chat log, ChatBuoy orders.
- **Commanding** — Home/End/Delete queued-order accept/reject/clear, order drones, autopilot
  to target.
- **Hangar / loadout / shop** and the docked screen; investment & teleport panes; starmap.

Gaps a veteran will hit immediately
- Trying to **target enemy miners/bombers** with class filters — no such keys.
- **Hitting a key for match-speed / vector-lock / drop-cargo / chat-scroll / part cycling** —
  nothing happens (fixed keymap, no rebinding UI).
- **F5/F11** do something unexpected (camera toggle instead of / in addition to music-down /
  screenshot).
- **No career leaderboard / full character sheet** from the menus.
- **Cannot rebind keys** to their muscle-memory layout.

---

## 6. Key-binding label audit (production changes)

Constraint: *no production code changes except trivially-wrong key-binding labels found on
the way, listed in the report.*

I compared every label in `Rendering/KeyBindingTable.cs` against the live key handlers
(`Game1.FlightInput.cs`, `Game1.Spectator.cs`, `Game1.Panes.cs`) key-by-key (F1/F8/F9/H/M/I/
Tab/B/P/V/K/C/F/L/N/T/G/U/Z/;/' /=/−/Backspace/Backslash/Space/LCtrl/Home/End/Delete/Enter/R/
Y/O/1-4/PageDown/Insert). **Result: no trivially-wrong labels found** — every documented
label matches the actual binding (e.g. `F1` really does toggle HUD via
`Game1.Spectator.cs:263`; `H` really is the help overlay per `Game1.FlightInput.cs:371`).

One non-label finding: the **F5/F11/F4 dual-binding conflict** (§4 item 18). It is a binding
**conflict**, not a wrong *label*, so it is deliberately **not** changed here (fixing it is a
design decision); it is flagged and ranked.

**Therefore: zero production code changes in this task.** Docs-only.

---

## 7. Native behaviour classified

| Native behaviour | Native cite | Class | What the port does |
|---|---|---|---|
| Modem-era ship-update budget (~1.6 kB/s) | P604 repro | era constraint | Sized for today (P604b: density-aware budget, no fixed modem cap) |
| Fixed modem band `PM_PING`-style latency counters | native DirectPlay RTT | era constraint | `0,0` fallback; RTT derived from PING(35) traffic instead (wiki roadmap) |
| `TK_ToggleBooster` toggle | igc.h:283 | design | Hold-Shift afterburner; toggle deliberately unwired (`FlightInput.cs:120`) |
| `TK_Throttle33/66` presets | igc.h:284–285 | design | Presets implemented; unbound by default (matches native default map) |
| 202 TK_ keymap | igc.h:226–440 | design | Focused hard-coded subset; long tail unported (this audit) |
| Hard-coded rebindable input map | `defaultinputmap.mdl` | design | Not exposed (no rebind UI) — flagged |
| Per-class target commands | igc.h:158–199 | design | Generic cycle only — ranked gap |
| `TK_CycleChatMsgs` | igc.h:262 | known dead code | Marked na (native does nothing) |
| `TK_DebugTest1-9` / console modes | igc.h:342+ | debug/obsolete | Marked na |
| `SET_TEAM_INFO`, `MUTE`, `BANDWIDTH` etc. (11 msgs) | `NotSupportedFedMsgs.cs` | dead-in-native / N-A / missing-struct | Deliberately not built, documented per message |

## 8. Modern alternative considered

- **Networking**: native uses fixed modem-era per-message budgets and DirectPlay guarantees.
  The port keeps gameplay-intent (ordered, guaranteed game-state) but on a plain TCP
  transport with coalescing and density-aware broadcast budgeting — a modern replacement of
  the fixed ~1.6 kB/s modem cap. Chosen: current approach; a snapshot-interpolation /
  relevancy upgrade remains a proposal for the transport backlog.
- **Input**: native exposes a rebindable command map; the port uses a hard-coded imperative
  keymap. Modern option: a data-driven rebindable command binding layer (idiom used by every
  modern game). Proposal (not implemented — audit only): introduce a binding registry so
  rebinding and the help overlay stop drifting (addresses the documented drift liability in
  `KeyBindingTable.cs`).

---

## 9. Verification

- **Whole-solution build**: `dotnet build AllegianceCore.slnx -c Release` →
  **Build succeeded, 0 warnings, 0 errors**. Docs-only change ⇒ no suite run required
  (per the shared rule); the 2× `scripts/run-tests.sh` suite was not executed because no
  production code changed.
- **Spot-check of 10 random "done" rows** (read the cited port code):
  1. `PING`(35) → `Wire/Ping.cs` `Fmid=35` ✓
  2. `TK_TargetEnemyBase` → `Game1.Targeting.cs` exists ✓
  3. `SIDE_INACTIVE`(68) → `Wire/SideInactive.cs` `Fmid=68` ✓
  4. `c_habmBoard` → `DataHullTypeIGC.cs` `HabmBoard` present ✓
  5. `TK_ViewSector` → `Game1.StarmapClick.cs` exists ✓
  6. `TK_ThrustRight` → `FlightInput.cs:115` (strafe-right) ✓
  7. `RIPCORD_DENIED`(186) → `Wire/RipcordDenied.cs` `Fmid=186` ✓
  8. `TK_FireWeapon` → `FlightInput.cs:135` (fire contribution) ✓
  9. `STATION_DESTROYED`(59) → `Wire/StationDestroyed.cs` `Fmid=59` ✓
  10. `Teleport pane` → `Rendering/TeleportPaneModel.cs` exists ✓
  All 10 verified (4 wire fmids cross-checked numerically).
- **Commit**: single commit on `master`, not pushed; includes `docs/parity/parity-matrix.csv`
  and this report (all new files, `git status --untracked-files=all`).

---

## 10. Caveats / honesty notes

- The **fedmsg "done%" is a conservative lower bound**: renamed client handlers defeat
  name-based dispatch matching. The ranked verification pass (§4 item 21) is the prerequisite
  to a fully trusted fedmsg number.
- Per-class target, swap-part, console-mode and view commands are grouped and conservatively
  classified; individual "missing" rows were confirmed absent from the client by grep
  (`TargetHostile`, `MatchSpeed`, `LockVector`, `OccupyTurret`, `DropCargo`, `NextCargo`,
  `TargetEnemyBomber`, `ChatPageUp`, `ThrustUp` all return zero client references).
- No production code was changed; no test suite runs were performed (no code changed).
