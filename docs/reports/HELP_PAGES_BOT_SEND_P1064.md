# P1064 — Controls help pages and disconnect-safe bot sends

Started **2026-11-20 17:13 local (UTC−05:00)**. Codex lane; no commits or pushes.
Completed **2026-11-20 18:02 local (UTC−05:00)**. Both requested fixes and their
qualification are complete. The final Fast gate remains red on unrelated
assertions/timings, retained below with three isolated passes per affected method.
Evidence is under `.scratch/p1064/` unless linked here.

## B — deterministic bot receive/disconnect race

The failing ordering is:

1. `TcpTransport.ReadDataLoop` has delivered a complete frame to `Bot.OnReceive`
   (`TcpTransport.cs:1475`, baseline `:1455`). The callback may still be decoding
   sector assignments or preparing a scripted-pilot pose/PING reply.
2. Another thread calls `Shutdown`, which clears `_clientSender` under `_gate`
   (`TcpTransport.cs:710`) **before** its bounded receive-thread join. Alternatively,
   the sender worker's failure callback calls `DropClient`, which clears the same
   field (`:1387-1392`) while the receive callback is active. `IsConnected` can still
   be true in the latter window.
3. `Bot.Send` (`Bot.cs:1894-1895`, called from `OnReceive:2249/2340/2341`) reaches
   the old client-side `Send` null-sender throw. `ReadDataLoop` does not catch
   `InvalidOperationException`, so it escapes the background thread and aborts
   the process. A caller's connected check cannot atomically protect the sender.

`tests/Allegiance.Core.Tests/Transport/P1064DisconnectedSendTests.cs` reproduces
both orderings over real loopback TCP. Its existing Bot inbound-delay hook holds
an inbound PING before dispatch; two real sector assignments arm the scripted
warp handshake. The main test removes the sender through either `Shutdown` or
the exact `DropClient` worker-failure path, verifies that the sender is null, then
releases the frame. The resumed callback really records the PING and attempts
its scripted-pose reply. A **test-only** receive wrapper captures any escaped
exception as an assertion instead of aborting the runner. No product exception
handler was added. The worker-failure row forces teardown ordering directly; it
does not claim to synthesize a real kernel write error.

On unchanged product code: **2 failed / 1 passed**, both race rows reporting
`InvalidOperationException: not connected` through `Bot.Send → TcpTransport.Send`.
Raw evidence: `before.log`, `before/before.trx`. The third case protects the
never-connected programming-error contract; its final version also checks
Shutdown-before-Join and a failed Join.

### Fix and caller audit

`TcpTransport.Send:606-622` now checks successful client-connection history while
holding the same `_gate` that protects sender publication/removal. A null sender
following a successful Join drops the late packet and logs the first such send
once per connection. A client that has never joined successfully still throws,
including after a failed connection attempt. Sends that already captured a
sender retain its existing closed-queue no-op. All ordinary/coalescing send
entry points converge here. No protocol, queues, packet bytes, default transport,
or server-side departed-peer policy changes.

The transport is the right shared layer: a Bot-only shutting-down flag would
miss sender-worker failures and leave the same check/send race in other clients.
No additional Bot dispatch guard is needed, and no broad exception is swallowed.

| Caller checked | Finding / action |
|---|---|
| Product Bot (`src/Allegiance.Bot/Bot.cs:1894-1895`, receive replies `:2249/2340/2341`, keep-alive `:1792-1794`) | Proven receive/teardown race; fixed centrally. The keep-alive connected check also benefits from the same atomic sender policy. |
| MCP driver (`tools/allegiance-mcp/ObservedSession.cs:35-69`, `StallWatchdog.cs:64-86`, BotManager call sites) | Injects/joins the same product Bot or ConnectionSession transport. Its observation wrapper rethrows receive exceptions; it does not own a separate wire-send implementation. Central fix covers the proven race; driver unchanged. |
| Test `FltBot` callers, including `PlayerMoneyAutoDonateTests.cs:8` | `FltBot` is an alias of the product `Allegiance.Bot.Bot`, not a different implementation. Same fix; fixture assertions/teardown unchanged. |
| Game session (`src/Allegiance.ClientCore/Session/ConnectionSession.cs:1379-1389`, `ConnectionSession.Ownership.cs:137-147/238-260`) | Session gate/owner checks suppress explicit retirement sends, but sender-worker failure can happen outside that gate between connected check and transport Send. Central policy closes that window. No independent client patch. |

### B qualification

Each suite ran **alone in 20 fresh processes**, serially, with artwork:

| Suite | Processes | Cases | Aborts / failures |
|---|---:|---:|---:|
| `P1064DisconnectedSendTests` | 20/20 passed | 60 passed | 0 / 0 |
| `PlayerMoneyAutoDonateTests` | 20/20 passed | 220 passed | 0 / 0 |

Evidence: `repeat-core/` TRX files and `repeat-core-<suite>-<N>.log`.
P1060's isolated baseline was **11 passed / 11 passed / abort after 7 passes**
(one abort in three class runs). P1064's final Fast sweep completes Core without
an abort: **6,793 result records**, **6,787 passed / 3 unrelated failed / 3 not
executed**. Thus **6,790 Core cases actually execute**, and every requested Core
case has a result: **0 missing / 0 extra**. Both the 3-case regression and the
11-case donation class pass inside that full sweep as well.

| Coverage measure | P1060 | P1064 |
|---|---:|---:|
| All Fast result records | 14,075 | **15,660** |
| Core result records | 5,219 (derived) | **6,793** |
| Missing Core cases after abort | 1,571 | **0** |
| Expected Fast cases | 15,646 (14,075 + 1,571) | **15,660** |

The 1,585 additional Fast results are **1,571 recovered + 14 new tests** (3 Core,
11 MonoGame). P1060 does not state a Core subtotal: its **5,219** above is derived
from this checkout's 6,793 Core inventory minus the 3 new regression cases minus
its recorded 1,571 missing Core cases. This distinguishes an inferred subtotal
from P1060's directly reported overall count. P1064 separately reconciles every
project's discovery names against its main + quarantine TRX results.
[Portable Fast summary](HELP_PAGES_BOT_SEND_P1064_assets/fast-summary.json).

## A — two Controls pages

`hlpControls` remains the stable **CONTROLS: FLIGHT** entry point. Adjacent new
`hlpControls2` is **CONTROLS: OTHER**. No topic ID was renamed; no new persistence
setting was introduced. KeyBind's Controls help and InFlightProof retain their
existing `GoTo("hlpControls")` calls. The topic bar displays both pages; existing
Up/Down next/previous selection and Backspace history work across them. The
intro's Controls topics now use the same actual reference renderer as flight,
rather than displaying only the topic's introductory prose. Other topics' body
content is unchanged, and their navigation bar fits the longer topic labels.

| Page | Complete groups | Live Port entries | Native preset entries |
|---|---|---:|---:|
| Flight | Movement; cruise throttle; weapons; targeting/autopilot; camera/view | 127 | 134 |
| Other | Inventory/cargo; commanded ship; queued orders; panes/menus; chat; lobby; ready check; team funding; vote; music; local skirmish; free-cam/spectator; training; system | 107 | 118 |
| Total | Every live catalogue group exactly once | **234** | **252** |

The split is by whole group, including both new Home/End local-skirmish bindings
on Other. Tall groups may continue in another column **on the same page**. The
shared model assigns new groups to Other by default; coverage and headroom tests
make growth visible. Existing unbound actions retain the catalogue's established
omission from `ToHelpGroups`; nothing is removed from the action map.

The shared measured layout packs wide sections together, measures the aligned
key/description maxima separately, scales row pitch together with text, and
reserves two spare rows below the tallest column. It chooses the largest fitting
hundredth-scale candidate and caches that result until bindings/page/viewport
change. Text uses the same point sampling as the other bitmap UI surfaces.
Trailing source-only `[TK_...]` annotations remain in the catalogue but are omitted
from rendered descriptions; action descriptions, key labels and defaults remain.
The default Port pages reach **0.51 / 0.53** at 720p (the old table's floor was
0.5); the native preset needs **0.48 / 0.50** for its additional bound controls.

### Production-font capacity

Measurements use Game1's real HUD SpriteFont (22px line spacing, 11px monospace
advance), the production 98.2% × 92% panel, topic bar and footer reservations.
Both HUD styles share these measurements. All pages use three columns. Row
figures include section titles and group spacing; the tallest-column figure is
the limiting vertical footprint, and capacity is per column. Full measurements:
[capacity.json](HELP_PAGES_BOT_SEND_P1064_assets/capacity.json).

| Preset | Height | Page | Scale | Total rows used / capacity | Tallest column / capacity | Spare rows below tallest |
|---|---:|---|---:|---:|---:|---:|
| Port | 720 | Flight | 0.51 | 137.36 / 148.77 | 47.45 / 49.59 | 2.14 |
| Port | 720 | Other | 0.53 | 133.36 / 143.16 | 45.45 / 47.72 | 2.26 |
| Native | 720 | Flight | 0.48 | 146.36 / 158.07 | 50.45 / 52.69 | 2.23 |
| Native | 720 | Other | 0.50 | 144.36 / 151.75 | 48.45 / 50.58 | 2.13 |
| Port | 1080 | Flight | 0.79 | 141.36 / 153.21 | 48.45 / 51.07 | 2.62 |
| Port | 1080 | Other | 0.85 | 133.36 / 142.40 | 45.45 / 47.47 | 2.01 |
| Native | 1080 | Flight | 0.75 | 146.36 / 161.38 | 51.45 / 53.79 | 2.34 |
| Native | 1080 | Other | 0.79 | 144.36 / 153.21 | 48.45 / 51.07 | 2.62 |
| Port | 1440 | Flight | 1.00 | 137.36 / 166.20 | 52.45 / 55.40 | 2.95 |
| Port | 1440 | Other | 1.00 | 133.36 / 166.20 | 52.45 / 55.40 | 2.95 |
| Native | 1440 | Flight | 1.00 | 144.36 / 166.20 | 52.45 / 55.40 | 2.95 |
| Native | 1440 | Other | 1.00 | 144.36 / 166.20 | 51.45 / 55.40 | 3.95 |

`HelpOverlayTests` now checks each page at 720p/1080p/1440p, both presets, exact
once-only entry coverage through the actual measured sections, non-overlap,
width bounds, stable navigation and two remaining rows. Its output reports
scale/columns/rows used/capacity/spare rows; the guard asks for group rebalancing
when new bindings exhaust that room. All **83 HelpOverlay / P757 navigation /
P941 input-layout cases passed three isolated runs** after the final renderer
change (`help-point-final/`); the 39-case HelpOverlayTests class also passed
three fresh processes alone (`help-alone/`).

### Inspected captures

All eight final images were opened and visually inspected individually. Each
shows three text columns inside the panel, the selected topic, the Back hint and
footer. No clipped text or overlapping rows/columns were observed. The 720p
text is small; the 1080p reference is visibly larger. Classic and Modern images
are pixel-identical at each size/page because this overlay is shared; both
runtime style selections were exercised.

| Capture | What it shows |
|---|---|
| [Classic 720 Flight](HELP_PAGES_BOT_SEND_P1064_assets/help-classic-1280x720-page1.png) | Camera/view and its continuation, weapons/cruise, targeting/movement; Flight selected; bottom gap visible. |
| [Classic 720 Other](HELP_PAGES_BOT_SEND_P1064_assets/help-classic-1280x720-page2.png) | Cargo/chat/ready, panes/lobby/training, and skirmish/funding/queued/free-cam groups; Other selected. |
| [Classic 1080 Flight](HELP_PAGES_BOT_SEND_P1064_assets/help-classic-1920x1080-page1.png) | Larger text, continued camera/targeting sections within this page; clear column gutters and footer gap. |
| [Classic 1080 Other](HELP_PAGES_BOT_SEND_P1064_assets/help-classic-1920x1080-page2.png) | Larger Other table, visible Home/End spectator-sector rows; all three columns end above Back/footer. |
| [Modern 720 Flight](HELP_PAGES_BOT_SEND_P1064_assets/help-modern-1280x720-page1.png) | Same shared Flight reference and reserved bottom space with Modern selected at runtime. |
| [Modern 720 Other](HELP_PAGES_BOT_SEND_P1064_assets/help-modern-1280x720-page2.png) | Same shared Other reference, including both new local-skirmish bindings. |
| [Modern 1080 Flight](HELP_PAGES_BOT_SEND_P1064_assets/help-modern-1920x1080-page1.png) | Larger shared Flight table with distinct headings and column gutters. |
| [Modern 1080 Other](HELP_PAGES_BOT_SEND_P1064_assets/help-modern-1920x1080-page2.png) | Larger shared Other table, its topic selection and clear footer. |

PNGs total **684,828 bytes**; current whole asset directory **734,178 bytes**, below
1.5 MB. `tools/p1064-help-images.py` losslessly packs the eight full backbuffers
and verifies decoded RGB equality/opaque alpha; no crop, resize or quantization.
[captures.json](HELP_PAGES_BOT_SEND_P1064_assets/captures.json) records dimensions,
pixel hashes and byte counts.

Reproduce with `tools/p979-visual-proof` `ui --help-pages --style classic|modern
--width 1280 --height 720 --out <dir>` and 1920×1080, under software GL/Xvfb, then
`python3 tools/p1064-help-images.py <dir> docs/reports/HELP_PAGES_BOT_SEND_P1064_assets`.
Fresh proof profiles and an empty lobby URL keep the captures offline. Exploratory
SDL-offscreen shots were rejected: that driver kept an 800×480 drawable despite
the requested larger backbuffer. Final images use correctly sized X11 windows.

## Gates and Fast coverage

- Final solution build: **0 warnings / 0 errors** (`gate-build.log`, 8.32 s).
- Tools build: **all 148 projects passed, zero warnings/errors** in the clean
  serial gate (`tools-build-clean.log`). An earlier pass had one MSB3026 copy-retry
  warning from an overlapping proof build; the clean rerun resolves that gate.
  Offline package feed and disabled build servers apply throughout.
- New/changed tests: HelpOverlayTests **39 ×3 alone passed**, broader
  help/navigation **83 ×3 passed**; regression **3 ×20 passed**;
  donation class **11 ×20 passed**. No unrelated timing/assertion limits changed.
- Existing TCP send/drop/greeting safety: **15 passed** (`send-safety/`).
- Full HUD zero-delta: **1,848 combinations passed**, **0 unmasked pixel
  differences / 0 semantic differences / 0 corrupt goldens**. The 84 animation-only
  differences stay inside the fixture's pre-existing masks; no mask changed.
  Original tracked source was built separately from `git archive HEAD`.
  Evidence: `hud-baseline/manifest.json`, `hud-compare.log`, and the portable
  [comparison summary](HELP_PAGES_BOT_SEND_P1064_assets/hud-compare.json).
- Glossary links: **1,035 paths pass**. Parity CSV: **2 rows validated**.
- Final hygiene: `git diff --check` passes; no personal home paths in changed
  text. Eight inspected PNGs and all report assets fit the 1.5 MB budget. The
  supplied brief is preserved in ignored `.scratch/p1064/` and absent from
  `git status`. All started build/proof/test processes have exited. No commits
  or pushes; changes remain in the working tree.
- Final Fast sweep, run last: **FAIL**, exit **1**, **17:44:23–17:55:13 local**,
  **645.4 s** tier duration. **15,650 passed / 4 failed / 6 not executed**, **15,660
  records**, zero missing/extra across all seven projects, no host abort.
  Infrastructure: **9/9 pass**; calibrated limit **11.542 s**; **6 timing offenders**;
  no quarantine flake. All **55 HelpOverlay-named cases** pass, including the
  39-case class; both B target classes pass. Four unrelated assertion failures
  and all timing methods each pass three fresh processes alone. No gate waived.


### Non-task qualification after the sweep

All **8 affected methods ×3 fresh processes**, serially with artwork: **24
processes / 60 cases passed**, zero failed/skipped, slowest **5.459 s**, below the
original **11.542 s** sweep limit. Every theory row ran each time. The six timing
offender executions cover five methods; DroneSpawnLoadout is also an assertion
failure, giving eight distinct methods overall.

| Unrelated method | Fast observation | Cases passed per isolated run | Slowest case in runs 1 / 2 / 3 (s) |
|---|---|---:|---:|
| `P796VariedLoadoutTests.Varied_equips_every_legal_hull_and_part_of_the_role_over_many_purchases` | civ 35 passed in 14.899 s (timing) | 3 ×3 | 5.445 / 5.456 / 5.459 |
| `P814GunnerTests.P814b_TurretCarrier_pref_keeps_or_beats_the_Best_hulls_turret_seats` | passed in 16.510 s (timing) | 1 ×3 | 3.217 / 3.211 / 3.236 |
| `P974SessionOwnershipTests.Overall_deadline_includes_the_join_answer_and_never_accepts_silence` | ACK null after the 200 ms join deadline (assertion) | 1 ×3 | 0.215 / 0.213 / 0.214 |
| `SC2NaniteSupportTests.Support_selection_obeys_research_dock_budget_and_mounts` | civs 25/3 passed in 16.678/11.729 s (timing) | 2 ×3 | 4.042 / 4.115 / 4.138 |
| `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity` | unexpected cross-side ASTEROID_MINED (assertion) | 1 ×3 | 1.800 / 1.797 / 1.800 |
| `DroneSpawnLoadoutTests.Spawned_wingman_defends_a_charge_and_damages_a_threat_without_manual_arming` | enemy fraction 1 after 20,000 ticks; 30.780 s (assertion + timing) | 1 ×3 | 0.303 / 0.312 / 0.313 |
| `P817NativeMinerDecisionCycleLiveTests.Idle_npc_autonomously_mines_full_bag_and_sells_at_friendly_station` | NPC did not dock/sell within tick budget (assertion) | 1 ×3 | 1.085 / 1.042 / 1.047 |
| `P999CivStrategyTests.Every_profile_is_reachable_in_catalog_and_actual_side_buy_gates` | civ 5 passed in 24.238 s (timing) | 10 ×3 | 4.449 / 4.502 / 4.464 |

Evidence: `final-isolation/` TRX/logs and the portable
[isolation summary](HELP_PAGES_BOT_SEND_P1064_assets/isolation-summary.json).
P796/P814/SC2/P999 timing and the miner/asteroid families have existing ledger
history. P974 uses its own `DelayedTransport`, not TcpTransport; the Wingman
failure is a pure-server scenario with no client. Their assertions and production
paths are unchanged. The isolated passes qualify these observations; they do not
establish a root cause or turn the original sweep green. All four assertions and
six timing offenders are recorded in `docs/TEST_FLAKES.md`.

Both requested fixes, all prescribed repeats, the eight inspected captures and
all gate runs are complete. The unrelated Fast failures remain unfixed and the
red overall gate is preserved. No test tier, quarantine, timing limit, binding,
protocol or game rule was changed to obtain these results.

The checkout had cached packages but no project assets files; restores used an
empty local feed with the preinstalled package cache. No packages were added or
downloaded. No service, port, environment configuration or wire contract changed.

## Native intent / Modern approach

Native help is a multi-topic manual: `effect/help.cpp:210-218` selects main and
secondary topics and resets their scroll positions; `:226-240` pops/pushes Back
history. The port retains that model and stable topic ID while presenting the
large live Controls reference on two measured pages in both HUD styles.

The supplied native reference's TCP adapter returns `E_FAIL` when disconnected
or its socket is invalid (`_Utility/TcpTransport.cpp:282-287`). The native
messaging wrapper logs a failed send result in debug builds and returns it
(`_Utility/Messages.cpp:590-605`), rather than throwing through receive dispatch.
The managed void-returning transport expresses that closed-connection outcome
as a dropped late packet, with one diagnostic, while preserving an exception for
never-connected misuse. This is a lifecycle safety change, not a new game rule.
Tracked in `docs/parity/p1064-help-bot-send.csv`.
