# P1076 — Home station artwork before launch

Implementation and all requested browser proofs are complete. The final full Fast
sweep remains red on the existing failure/timing families qualified below.
Started 2026-11-23 17:20 UTC; completed 2026-11-23 18:21 UTC,
within the three-hour cap. No commits, pushes, installs, deployment or public-host runs.

## Work per item

1. Implemented own-side current-sector priority zero, preserving closest-first
   order, enemy/neutral offscreen grace and rock priorities.
2. Implemented actual-dock station and sector sky prefetch at priority zero,
   throttled to 250 ms, without starting the flight scene's 30-second clock.
   Optional warp prefetch is omitted to keep the byte change narrow.
3. Encoded pack bytes were measured separately in hangar and the first 30 seconds
   after launch, with tested garrison closures and untouched-base comparisons
   recorded below. All non-station increases are identified by original asset.
4. Cold Omicron and Belters graphics proof passed in Classic and Modern: all
   four garrisons were texture-resident before launch, real keyboard turns
   produced further station draw frames, and 5,984 sampled flight frames had
   **zero discarded frames**. Captures are within five seconds of the launch
   click (including the first-frame GPU uploads):

   | Garrison | Classic | Modern |
   |---|---|---|
   | Omicron | [4.299 s](P1076_assets/home-civ5-classic.jpg) | [4.194 s](P1076_assets/home-civ5-modern.jpg) |
   | Belters | [4.659 s](P1076_assets/home-civ101-classic.jpg) | [4.725 s](P1076_assets/home-civ101-modern.jpg) |

   Screenshots were opened and reviewed: original textured station geometry is
   visible at the left of each view. The pilot rotates the ship using real
   ArrowLeft input; no proof camera or injected pose is used. The resident control cases also passed: Dreghklar captures at
   [3.239 s Classic](P1076_assets/home-civ1-classic.jpg) /
   [4.245 s Modern](P1076_assets/home-civ1-modern.jpg), and Ga'Taraan at
   [3.531 s Classic](P1076_assets/home-civ3-classic.jpg) /
   [3.480 s Modern](P1076_assets/home-civ3-modern.jpg). Across all eight flights:
   **11,631 sampled frames, zero discards**, with garrison textures resident
   before launch. P1075 passed in both styles; P1073 generated-map passed with six captures.
   P1074 also passed in Classic and Modern. The baseline byte comparison is
   complete; the final Fast sweep is red on known unrelated failure/timing families
   with isolated qualification complete.

Tested garrison closures (encoded gzip body bytes; incremental means beyond the
unchanged resident join):

| Civilization | Pack / model | Full closure | Incremental |
|---|---|---:|---:|
| Dreghklar (1) | `station-26` / `dgarr` | 67,149 | 0 |
| Ga'Taraan (3) | `station-44` / `ss401` | 245,622 | 0 |
| Omicron (5) | `station-715` / `faohbsgar` | 2,943,171 | 2,943,171 |
| Belters (101) | `station-315` / `ss300` | 6,135,886 | 6,084,633 |

The final and untouched-base generations have byte-identical starter, join,
sector and battle manifests, including all 601 resident join files. No original
artwork, pack closure, texture size, verification, canonical-name rule or CSP
changed. Byte-phase measurements and the untouched-source comparison follow below.

The previous sector request throttle/grace compared frame delta seconds rather
than accumulated time. It now uses `_nowSeconds`; GPU-free tests cover both the
250 ms boundary and the launch-relative 30-second prefetch boundary. Docked
metrics resolve the actual station sector even when the ship sector is unset.
The local proof host accepts optional civilization pairs so cold Omicron and
Belters garrisons supplement the resident Dreghklar/Ga'Taraan control pair.

The focused graphics proof holds flight-only responses until 5.5 seconds,
then delays later pack responses by 150 ms. This tests that the dock prefetch
supports the first look-back independently of flight-only downloads. The final proof holds the real turn key while leaving the bay and records
the authoritative exit orientation separately from GPU upload stalls. P1075
regression retains its original 150 ms response delay; its byte ledger now also
records the hangar phase.

## Encoded byte measurements

All totals count completed HTTP pack response bodies, using the encoded response
content length, in fresh browser contexts. Hangar begins at side selection;
flight begins at the launch click. Join/framework, headers and WebSocket traffic
are excluded, as in P1075. The unchanged hangar animation is 298,646 bytes.
Families assign shared hashes once; the Belters station's 1,426,908 shared texture
bytes appear under “hulls” in the raw family ledger but are part of its audited
6,084,633-byte incremental station closure. Full requests and phase/family sums
are in [evidence.json](P1076_assets/evidence.json).

The original P1075 procedure retains its 150 ms post-launch response delay and
stationary portal camera. Its new phase split is:

| Phase / family | Classic | Modern |
|---|---:|---:|
| Hangar: sky | 2,986,397 | 2,986,397 |
| Hangar: animation | 298,646 | 298,646 |
| Flight first 30 s: rocks | 4,787,650 | 4,787,650 |
| Flight first 30 s: scene / funnels / effects | 947,445 | 947,445 |
| Flight first 30 s: friendly stations | 0 | 74,174 |
| **Hangar total** | **3,285,043** | **3,285,043** |
| **Flight first 30 s total** | **5,735,095** | **5,809,269** |

Relative to P1075's 6,153,987-byte controlled flight, the sky shifts into hangar
and one additional original surface, `bgrnd51bmp.mdl` (2,567,505 encoded bytes),
is requested at approximately 2.2 seconds. The accumulated-time correction makes
the existing camera priority re-evaluate every 250 ms. It is necessary for a
station captured by the player's side to become immediate without a roster-size
change; the GPU-free test covers that case. Rock priority values and closest-first
ordering are unchanged. Modern's 74,174 bytes are the now-friendly station created
by Classic's construction fixture. These explain the increased combined total;
there is no new speculative painting library, duplicate closure or texture-size
change. The untouched-source comparison confirms the view-dependent range below.

An untouched `37b04423` source archive, built locally with the same manifests,
fixture and procedure, measured **298,646 hangar bytes in each style**, then
**6,153,987 Classic / 12,210,722 Modern** flight bytes. This reproduces both the
published P1075 controlled result and its documented larger Modern repeat.

After fixing a proof-only pending-route shutdown race, the changed build was
qualified again: **5,735,095 Classic / 9,298,499 Modern** flight bytes, with the
same **3,285,043** hangar bytes each. Modern additionally viewed the original
`bgrnd53bmp.mdl` surface (3,489,230 bytes). Adding the moved sky and excluding the
unchanged hangar animation gives **12,284,896** bytes: exactly the baseline's
larger Modern result plus the **74,174-byte own-side station**. Both changed
qualifications and the base comparison are preserved, rather than selecting only
the smallest window. The non-station increase in the primary Classic pair is
explicitly **2,567,505 bytes** of visible original rock art; retaining the correct
periodic refresh also prevents newly friendly stations from remaining deferred.
No rock priority was retuned. The cleanup repeat passed seven renderer cases per
style and another **4,991 frames with zero discards**, then both contexts and
hosts exited cleanly.

The focused real-turn proof has a different view/network profile (5.5-second
flight-only hold, then 150 ms per response), so its wider camera interest is
reported separately:

| Civilization / style | Hangar | First 30 s flight | Home station bytes in hangar / flight |
|---|---:|---:|---:|
| 1 / Classic | 3,285,043 | 6,656,820 | 0 / 0 |
| 1 / Modern | 3,285,043 | 9,224,325 | 0 / 0 |
| 3 / Classic | 2,652,911 | 9,224,325 | 0 / 0 |
| 3 / Modern | 2,652,911 | 9,500,804 | 0 / 0 |
| 5 / Classic | 6,228,214 | 9,904,808 | 2,943,171 / 0 |
| 5 / Modern | 6,228,214 | 9,224,325 | 2,943,171 / 0 |
| 101 / Classic | 8,737,544 | 9,425,633 | 6,084,633 / 0 |
| 101 / Modern | 8,737,544 | 9,511,118 | 6,084,633 / 0 |

The rotating view requests additional existing rock surfaces; its rocks total
ranges from 5,709,375 to 8,276,880 bytes. Actual arriving battle contacts account
for up to 680,483 hull bytes. Cold-garrison station traffic is entirely in hangar
and exactly matches the audited incremental closures above.

## Validation

| Check | Result |
|---|---|
| Final solution build, disabled build servers | **0 warnings / 0 errors** |
| Final `tools/build-tools.sh` | **153/153 projects built** |
| New GPU-free / fixture cases | **16 ×3 alone = 48 passed** |
| Browser head suite | **52/52 passed** |
| Browser game suite | **277/277 passed** again in the final Fast sweep |
| P1076 cold and resident garrison flights | **8/8 passed**, eight reviewed captures, 11,631 frames / zero discards |
| P1075 original regression | **Classic + Modern passed**, seven renderer cases each, 5,003 frames / zero discards |
| P1073 generated-map regression | **Passed**, six captures |
| P1074 keyboard regression | **Classic + Modern passed** |
| Final full Fast sweep | **16,036 passed / 1 failed / 7 skipped**, 610.0 s; 9 infrastructure checks passed |
| Final Fast isolated qualifications | **81/81 passed**, six methods ×3 processes |
| Frame-probe Node tests | **3/3 passed** |
| Glossary paths / parity CSV / whitespace | Passed |

The scheduling test also qualifies an ownership change at unchanged station
count; that final assertion passed in three fresh isolated processes. Earlier
unchanged scheduling/allocation flakes each passed three isolated reruns and are
recorded in `docs/TEST_FLAKES.md`. The proof tool now removes pending network
routes before successful context shutdown; the fixed and baseline pair both completed cleanly. No test assertion,
timeout, tier, calibration threshold or quarantine was relaxed.

The final Fast sweep's assertion failure is the already-ledgered
`P467E2eGunDamageDevLiveTests.Completing_dev_143_raises_gun_damage_in_combat`
(single-round damage observation). Five passing executions exceeded the actual
**11.540-second** calibrated limit: P814 gunner selection, SC2 Nanite selection,
P796 varied loadout, P1000 civ profiles and P999 strategy profiles. All six implicated
methods then passed **three fresh isolated processes each**, including every
theory row: **81/81 cases passed**, maximum isolated
case **5.567 s**, below the 11.540-second limit. Exact cases and
durations are in [the flake ledger](../TEST_FLAKES.md) and the evidence. The full
sweep remains red; no server, gun, bot policy or test threshold changed.

All .NET commands used `$HOME/.dotnet/dotnet`, a task-specific
`NUGET_HTTP_CACHE_PATH` and disabled build servers. Existing Chromium, native
libraries/fonts and artwork were reused. All owned proof hosts, browsers and MCP
drivers exited cleanly. The final Fast runner and isolated qualification processes have also finished.

## Reproduction / remaining work

Use fresh generation, state and evidence directories and the existing artwork,
Playwright and Chromium installation. No package installation is needed:

```sh
BROWSER_CLIENT_VERSION=2.0.0 scripts/publish-browser-generation.sh "$GENERATION"
python3 tools/p1075-proof-pack.py --generation "$GENERATION" --artwork "$ALLEGIANCE_ARTWORK_PATH"
python3 tools/p1061-browser-provenance.py "$GENERATION"
"$HOME/.dotnet/dotnet" tools/p1051-browser-host/bin/Debug/net10.0/p1051-browser-host.dll \
  "$ALLEGIANCE_ARTWORK_PATH" "$GENERATION" "$STATE" \
  --guests 1 --bots 5 --seconds 1800 --map-gen SingleRing --map-seed 1073 --civ-ids 5,101
# In another terminal, with the installed Chromium library/font environment:
node tools/p1076-home-station.cjs "$NPM_DIRECTORY" "$STATE" "$EVIDENCE" "$GENERATION"
```

Stop that owned host by creating `$STATE/stop`. Repeat serially with a fresh
state directory and `--civ-ids 1,3` for the resident control pair. The P1075
regression uses `tools/p1075-browser-followups.cjs` with the same four arguments;
P1073 and P1074 use their existing generated/static zero-bot host recipes.

No implementation, requested screenshot or browser proof remains unfinished.
The full Fast gate remains red on the ledgered broad-sweep failure/timing families
above, with all isolated qualifications passing. Public-host measurement and
deployment are outside this lane. Changes remain uncommitted in the working tree.

## Native intent

Native station types name their local artwork (`Igc/igc.h:2649`), and native sky
setup resolves local artwork (`WinTrek/WinTrek.cpp:3622-3640`). This task changes
browser delivery order only; it introduces no native simulation rule.

## Modern approach

The existing verified pack streamer and shared Classic/Modern browser scene
prefetch the dock's station and sky during hangar time, then prioritize own-side
stations even behind the launch camera. Verification, canonical asset names and
CSP stay intact. Code, evidence and this report remain in the working tree.
