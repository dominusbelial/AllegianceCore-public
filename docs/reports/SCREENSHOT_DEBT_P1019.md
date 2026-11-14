# P1019 — real-window proof for P1004 and P1006

The F4 failure is a proof-script binding error. Both HUD styles now pass the
real-client intro and flight checks, including Port Shift+F4 and native plain
F4. Forty real backbuffer PNGs are retained and individually viewed. Capture
coverage is complete; visual quality has the rendering qualifications below.
The report was drafted before the gate. Base: `874fd66a`; changes remain in the
working tree, without commits or pushes.

## Findings, root causes and fixes

| Finding | Evidence / root cause | Result |
| --- | --- | --- |
| Original F4 loadout assertion fails | Reproduced in Classic flight in **12.174 s**, after the earlier command/hidden-input checks passed. Original `tools/p1006-ui-proof/Program.cs:197` pressed literal F4. Fresh profiles use Port layout (`Rendering/KeyBindingStore.cs:62`, `Rendering/InputBindings.cs:171`), where inventory is Shift+F4 (`Rendering/InputActionCatalog.cs:305`) and plain F4 selects external orbit (`:349`). `Game1.Director.cs:469-497` handles that docked camera action by returning to Cockpit **with hangar-only true**. | Proof resolves the live `ConModeInventory` binding (`Program.cs:221`), logs its chord, and adds `--native-layout` to assert/exercise plain F4 independently. No production console-mode or hidden-input change was needed. |
| Intro rejects a legitimate fresh profile | Original proof `Program.cs:128` expected an empty pilot buffer. `IntroPreferences.cs:24` defaults to `pilot`; `Game1.IntroScreen.cs:149-154` loads it. | Check absence of saved player/keymap, the actual default name and first-run suggestion. |
| Intro suggestion check observes a menu | The proof held splash Escape across its 0.7 s capture interval. The new suggestion consumed that held key (`Game1.IntroScreen.cs:560-580`), so its later intended dismissal was a root-menu Escape. | Release splash Escape once that screen consumes it (`Program.cs:112-118`); give suggestion dismissal its own press. Both checks pass. |
| Large offscreen browser images are clipped | SDL offscreen starts with an 800×480 surface on this lane; the browser requests 1280×720. Initial captures were mostly black/clipped. | Use Xvfb/X11 at 1280×720. README now gives the working command. |
| Protected browser row hides 0/200 | `Game1.Session.cs:1001-1004` ellipsizes the row; `ServerListing.cs:182` previously placed counts after the address. | Small shared formatting fix moves capacity before password/slot markers and address. Existing exact formatting expectation updated in `LobbyChatLogicTests.cs:58`. Long tails still use deliberate ellipsis. |

Port file references without a prefix in this section are under
`src/Allegiance.ClientCore.Game/`, except proof references under
`tools/p1006-ui-proof/` and the test under
`tests/Allegiance.ClientCore.MonoGame.Tests/`.

## Native references

Re-read/re-grepped in the mounted native corpus before citation:

- `WinTrek/WinTrek.cpp:10001-10021`: docked inventory enters loadout from any
  non-loadout view; loadout with covering panes closes them, otherwise returns
  to hangar. The existing port branch is `Game1.ConsoleModes.cs:69-82`, before
  `Game1.Panes.cs:213` rejects ordinary hidden-shop input. The real client
  exercises that routing successfully.
- `Igc/igc.h:293`: `TK_ConModeInventory` is command 70;
  `Artwork/defaultinputmap.mdl:38` binds it to native F4. HUD style and keyboard
  preset are independent. Port Shift+F4 is the existing P949 preset policy.
- `WinTrek/WinTrek.cpp:6706-6740,9894-9933`: command replaces the docked
  hangar/loadout screen; the captures prove invisible hangar input stays inert.
- `WinTrek/introscreen.cpp:1298-1310`: first-run training suggestion;
  `WinTrek/WinTrek.cpp:9281,9304-9320` and `engine/popup.cpp:391-411`: Escape
  popup and popup input ownership.
- `WinTrek/teamscreen.cpp:1673-1704`: briefing only during Starting, closed in
  other stages; the loopback mission is already Started when the pilot joins.
- `FedSrv/fsmission.cpp:4038-4078`: actual player counts, team-slot scan and
  capacity; `WinTrek/gamescreen.cpp:1006-1021,1299-1305`: slot status and
  selected-row join request. P1019 changes row presentation only.

Updated parity evidence: `TK_MainMenu`, `TK_ViewCommand`, `TK_ConModeInventory`
in `docs/parity/parity-matrix.csv`, and `LOB-0002` in `lobby-matrix.csv`.

## Real-window checks and media review

[P1006 check-by-check results](COMMAND_SCREEN_INPUT_P1006.md#real-window-proof-p1019)
list every assertion in Classic and Modern. Each intro run checks **7/0/0**;
each Port flight run checks **8/0/0**; each native flight run checks **9/0/0**
(Passed/Failed/Skipped). The six-run matrix gives **48/0/0**. All six scenarios passed **three isolated repetitions: 18 client processes,
144/0/0 assertions**. All three browser processes reached **42/42 fixture
states** and wrote fourteen PNGs each; only the reviewed retained set is added.

[P1004 fixture-by-fixture results](LOBBY_LISTING_SLOTS_P1004.md#real-window-proof-p1019)
cover protected/free, full, no team slots, connection refused, HTTP 503,
filter-excluded and newer-client states in both styles. All fourteen production
fixture states were reached and captured; banners distinguish transport errors,
HTTP replies and a successful filtered list.

| Retained media | Count | Dimensions | Bytes |
| --- | --- | --- | --- |
| `P1004_assets/p1004_*.png` | 14 | 1280×720 | 756,324 |
| `P1006_assets/after/*.png` | 26 | 800×480 | 2,691,158 |
| **Total new PNG media** | **40** | | **3,447,482** (3.29 MiB) |

P1006 retains four intro and eight flight states per style, plus two native-F4
loadout captures. I individually viewed **all 40 retained images**, checked PNG
chunk CRCs/decompression and reviewed their content: local fixtures, generic
pilot names, local artwork, loopback addresses, no secrets or personal identity.
No image was composed or replaced with a synthetic UI. The brief remains only
in ignored task scratch.

### Rendering qualifications retained for follow-up

The images reveal **intermittent missing glyph runs** in both styles: most
obvious in `intro-after-splash-escape-{classic,modern}.png`, and in browser
headers/footers. Some browser frames omit even parts of the slot/count text.
This is visible in a separate X11 window grab as well as the backbuffer PNG;
it is not merely the PNG viewer. Root cause is **unconfirmed**. Capping at
60 fps, single-thread llvmpipe, Mesa flush mode, softpipe, a CPU PNG encoder,
and immediate SpriteBatch submission did not reliably remove it. Encoder and
render-batching experiments were reverted; no speculative renderer change is
included. The unconditional browser text draw sites are
`Game1.Session.cs:886-892,948-951,1085-1095`.

The 800×480 P1006 frames also show a clipped hangar hover tooltip, the native
command bar reaching the lower edge, and Classic command labels overlapping
ship telemetry in space. These require a broader compact-viewport/rendering
pass, beyond the small capacity-order fix allowed here. They are reported per
the brief, not presented as clean visual qualification. The visible active
screen and production input assertions still establish the P1006 behavior.
No missing-runtime blocker remains; **capture/review coverage passes, while
visual quality remains qualified by these recorded defects**.

## Software-GL setup and elapsed time

Use a fresh `XDG_CONFIG_HOME` per process and local artwork. P1006 uses:

```sh
mkdir -p .scratch/p1019/example-profile
SDL_VIDEODRIVER=offscreen LIBGL_ALWAYS_SOFTWARE=1 ALSOFT_DRIVERS=null \
ALLEGIANCE_NOVSYNC=1 XDG_CONFIG_HOME="$PWD/.scratch/p1019/example-profile" \
dotnet tools/p1006-ui-proof/bin/Debug/net10.0/p1006-ui-proof.dll \
  .scratch/p1019/example-captures classic flight
```

Set `ALLEGIANCE_ARTWORK_PATH` from `local.env`. Change style/scene for the other
runs; add `--native-layout` to test plain F4. The browser README gives the Xvfb
command and preference/password isolation. All game traffic is loopback; its
HTTP fixtures use an in-process handler. No public host or external machine was
contacted.

Measured process wall time, including startup/content loading and PNG writing,
excluding build: Classic intro **10.902 s**, Modern intro **12.248 s**; Port
Classic/Modern flight **17.305 / 16.274 s**; native Classic/Modern flight
**16.664 / 20.143 s**. A complete fourteen-capture browser run takes **26–29 s**
(the retained run: **28.825 s**). Budget roughly **two minutes** for the six
P1006 runs plus one browser run, then allow extra time for visual review.


## Gate

The report was drafted before the gate. Counts are Passed / Failed / Skipped.

| Check | Result |
| --- | --- |
| Final solution build | **0 warnings / 0 errors**, 10.57 s |
| `tools/build-tools.sh --drop-bins` | **138/138 projects**, 0 warnings / 0 errors |
| Changed formatting fact, alone ×3 on final binaries | **1/0/0 each run** |
| Related screen/browser/row/preset selection | **103/0/0** |
| P1006 real client, six scenarios ×3 | **144/0/0** assertions across 18 processes |
| P1004 real browser, three complete runs | **42/42 states**, fourteen PNGs per run |
| One full Fast sweep | **14,255/3/7**, 14,265 cases, **704.5 s: FAIL** |
| Fast infrastructure | **9/0/0: PASS** |
| Non-task failed/slow methods, each alone ×3 | **84/6/0**, 90 cases across 33 processes |
| Glossary / parity / diff / public added-line hygiene | **PASS**, 813 glossary paths |
| Retained screenshots, individually viewed and CRC-checked | **40/40**; visual defects qualified above |

The full sweep ran once with a **7,200 s (two-hour) outer bound**, exceeding
the requested minimum 90-minute bound:

```sh
timeout --signal=TERM --kill-after=30s 7200s tools/run-tests.sh \
  --tier fast --no-build --results-dir .scratch/p1019/fast
```

All task-related UI cases passed. The three non-task failures were missile-lock
partial-tip delivery, silent-peer seating (31.5 simulation seconds against
[28,31]), and a missing probe-broadcast receipt. Eleven case executions exceeded
the calibrated **13.039 s** limit. All eleven distinct failed/slow methods were
repeated three times alone, with every theory case. Missile lock and seating
each failed **0/1/0** in all three repetitions; the probe receipt passed
**1/0/0** each time. All eight timing-only methods passed all repetitions under
the limit. Their exact case counts and per-run timings are in
[TEST_FLAKES.md](../TEST_FLAKES.md) and the
[portable isolated results](P1019_assets/reproductions.json). These repeats do
not clear the failed sweep. No range, timing limit, tier or engine behavior was
changed for these failures.

The seven full-suite skips comprise three optional UI media-pack checks, one
audio-playback check, one artwork-manifest check and two pre-existing server
callback cases. They do not replace any requested P1004/P1006 proof.

Builds used `--disable-build-servers -nodeReuse:false
-p:UseSharedCompilation=false`, the supplied local package cache and a loopback
feed serving the supplied official vulnerability database (2026-11-12 data).
NuGet audit stayed enabled. Docker is absent; no service/store/port or production
environment setting was introduced. No new low-impact test mirrors the row
reorder; the existing expectation and real client verify it.

Raw logs/TRX remain in ignored `.scratch/p1019/`.
[Portable qualification/media evidence](P1019_assets/qualification.json) includes
each retained image's dimensions, bytes, hash and review status. Xvfb and the
audit feed were stopped before the Fast sweep; all started verification processes
have ended. The brief is only in ignored scratch. No commits or pushes were made.

Native intent: the visible screen owns ordinary input, while explicit native
console shortcuts remain reachable; listings show real capacity and team
availability without confusing them with directory connectivity.

Modern approach: resolve the active keyboard binding, isolate startup key
presses and preferences, use production Game1 with local fixtures and software
GL, and retain honest screenshot evidence with rendering defects qualified.
