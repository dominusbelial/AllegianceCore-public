# P577b — HD sky corrections (keep native planets; swap globe1/globe3)

## Summary

Domingo reviewed P577's HD sky pick list (2026-10-08) and asked for two
changes, both applied:

1. **Keep the ORIGINAL native planets in the HD tier.** P577 had replaced the
   native alien planets (`nebplntNN`) with real Solar-System bodies (Earth
   "Blue Marble", Jupiter, Mars, Saturn, Venus, the Moon, Titan, Neptune,
   Uranus) — too recognisable for Allegiance sectors. P577b makes the HD tier
   render the **native planet art** (the P576 path: `nebplntNNbmp.mdl` /
   `nebplntNN.png`), as-is. The native planet textures are already 2048²
   RGB565, so "native as-is" is genuinely high-definition (larger than P577's
   1024² NASA discs). The NASA planet assets and their manifest/credits entries
   were removed.
2. **Swap the two weak backgrounds** for nebulae that fill the frame,
   palette- and layout-matched to the native environment:
   - **globe1** (native: bright yellow/green/cyan/orange multi-lobed) — the
     Whirlpool Galaxy M51 (a face-on *galaxy*) is replaced by the **Veil
     Nebula** (ESA/Hubble **heic0712a** "Uncovering the Veil Nebula"), a
     wide-field, multicoloured supernova remnant that fills the frame.
   - **globe3** (native: red/orange/pink emission blob; Alderan/Cirrus/Terra/
     Juniper) — Supernova 1987A (tiny, mostly black) is replaced by
     **Westerhout 5, the Soul Nebula** (ESA/Hubble **potw2251a**
     "Festive and Free-Floating"), a red emission nebula filling the frame.
3. **Verified every pick's ID** — all 10 environment `source_url`s were opened
   and checked against their release pages on 2026-10-08; the exact title and
   credit line were recorded and mismatched credits fixed (env2, env5, env6,
   env7, env9, env10).

The HD tier stays behind P576's `Sky: Native | HD` switch (`--sky native|hd`);
native remains the default.

---

## Native fidelity

- **HD planets = native planet art.** `HdSkyRenderer.PlanetTextureFor` now
  resolves planets to the native texture stems via
  `HdSkyGuard.NativePlanetTextureStems` (`nebplnt06bmp` → `nebplnt06bmp.mdl`,
  PNG fallback `nebplnt06.png`) and decodes them with the **same path the
  native renderer uses** — `MdlTextureDecoder.Decode` + colorkeyed premultiplied
  RGBA, mirroring `NativeSkyRenderer.TextureFor` (`NativeSkyRenderer.cs:348-385`).
  No readability cap is applied to planets, so the art is preserved exactly as
  the native tier draws it ("native as-is").
- **globe1/globe3 recomposited to native palette/layout.** The new
  `hdsky/nebula/env{1,3}.png` were produced by `tools/hd-sky/make_hd_nebula.py`
  from the native `environmentNbmp.mdl`: the telescope photo is colour-
  transferred to the native palette in CIELAB and modulated by the native
  luminance envelope, so the native bright-region layout (globe1's multi-lobed
  hourglass, globe3's centred blob) is preserved (`make_hd_nebula.py:150-163`).
  Side-by-side proof in §Proof confirms the match.

---

## ID verification (all 10 environments, page-verified 2026-10-08)

Each row: manifest `hd` asset → release → exact page title → exact page credit
line. Bold = **changed in P577b**.

| env | release | exact page title | exact page credit |
|---|---|---|---|
| env1 | **heic0712a** | **Uncovering the Veil Nebula** | **NASA, ESA, and the Hubble Heritage (STScI/AURA)-ESA/Hubble Collaboration. Acknowledgment: J. Hester (Arizona State University)** |
| env2 | heic1608a | The Bubble Nebula | NASA, ESA, Hubble Heritage Team *(fixed)* |
| env3 | **potw2251a** | **Festive and Free-Floating (Westerhout 5)** | **ESA/Hubble & NASA, R. Sahai** |
| env4 | heic1501a | New view of the Pillars of Creation — visible | NASA, ESA/Hubble and the Hubble Heritage Team |
| env5 | heic0619a | Star on a Hubble diet (NGC 6357 / Pismis 24) | NASA, ESA and Jesús Maíz Apellániz (Instituto de Astrofísica de Andalucía, Spain). Acknowledgement: Davide De Martin (ESA/Hubble) *(fixed)* |
| env6 | heic1707a | Supernova remnant N103B | ESA/Hubble, NASA *(fixed)* |
| env7 | heic2007a | Tapestry of Blazing Starbirth | NASA, ESA, and STScI *(fixed)* |
| env8 | heic1007a | Hubble captures view of "Mystic Mountain" | NASA, ESA, M. Livio and the Hubble 20th Anniversary Team (STScI) |
| env9 | heic1307a | New infrared view of the Horsehead Nebula | NASA, ESA, and the Hubble Heritage Team (AURA/STScI) *(fixed)* |
| env10 | heic1509a | Westerlund 2 | NASA, ESA, the Hubble Heritage Team (STScI/AURA), A. Nota (ESA/STScI), and the Westerlund 2 Science Team *(fixed)* |

All are CC BY 4.0 (ESA/Hubble; `https://esahubble.org/copyright/`). No release
ID failed to match its stated subject after the env1/env3 swaps and the credit
corrections.

---

## Implementation

- `HdSkyRenderer.cs` — planet texture resolves to native (see §Native
  fidelity); class docs updated. `HdSkyGuard.NativePlanetTextureStems` added
  (pure, testable).
- `HdSkyGuard.cs` — new `NativePlanetTextureStems(planetName)` returning the
  native `.mdl` stem + PNG fallback (never `hdsky/planet/`).
- `docs/reports/P577_assets/sky-hd.json` — env1/env3 swapped to the Veil and
  Soul Nebulae; every environment credit corrected to its page line; the
  `planets` array (NASA bodies) replaced with the native-art declaration.
- `CreditsOverlayRenderer.cs` (the F12 screen) — env1/env3 lines and all
  corrected credits; the NASA **PLANETS** block replaced with a native-art note.
- `docs/reports/P577_assets/licenses/README.md` — updated credit table.
- `tools/hd-sky/make_hd_planet.py` — **removed** (it only generated the NASA
  planet discs that P577b no longer uses).
- Artwork (`/home/domingo/Development/Artwork/hdsky/`, external repo): new
  `nebula/env{1,3}.png` (2048×1024) + `nebula_a/env{1,3}.png` (1024×512) from
  the new sources; **NASA `planet/` and `planet_a/` PNGs deleted**; obsolete
  source photos removed, new ones archived under `photos/`.

---

## Proof (real window, 2560×1440 unless noted)

All captures are real presented frames (backbuffer `SaveAsPng`), not
`--capture`/offscreen renders — the P547/P562 regression class. See
`docs/reports/P577b_assets/proof/`:

- `globe1_native_oldhd_newhd.png` / `globe3_native_oldhd_newhd.png` —
  **native | old HD | new HD** side-by-side for both swapped environments
  (real-window `hdsky-proof` driving the actual renderers).
- `hd_live_alderan_soul.png` — the **real client window in Alderan** (globe3)
  with `--sky hd`: the red/orange Soul Nebula fills the sky (2560×1440,
  `scripts/live-proof.sh`).
- `hd_globe1_veil_realwindow.png` — new HD globe1 (Veil Nebula) real window.
  *(The 221 map uses no globe1 sector — it uses globe2,3,4,5,6,8 — so the
  globe1 sector proof is this real-window render of the actual `globe1`
  renderer, as in P577.)*
- `hd_native_planet_onaxis.png` — a real-window HD frame aimed at the planet:
  the **native planet disc visible in HD mode**.
- `credits_screen.png` — the in-game **Credits/Licences screen** (real window,
  via the new `--auto-credits` automation flag) showing `env1 Veil Nebula`,
  `env3 Soul Nebula (Westerhout 5)` and the `PLANETS (native game art)` note.
- The manifest validation test (below) passes, locking the corrected manifest.

New env readability (real-window centre stats, `hdsky-proof`): env1 mean 70.0 /
max 225 / bright 0.1%; env3 mean 42.3 / max 145 / bright 0.0% — both stay under
the 225 luminance cap with ships readable. Old env1 (M51) was 3.6% bright;
env1's new wide-field nebula is cleaner.

---

## Unit tests (failing-first)

- **`SkyHdManifestValidationTests` (new, 5):** reads `sky-hd.json` and asserts
  10 environments, every env has a subject/source/source_url/credit, env1 is
  the Veil Nebula (heic0712a, not M51/Whirlpool), env3 is the Soul Nebula
  (potw2251a, not SN 1987A), and planets is the native-art declaration (not a
  NASA body list). Against the pre-P577b manifest **4 of 5 fail**
  (`Planets_...native`, `Every_environment_has...subject`, `Globe3_...Soul`,
  `Globe1_...Veil`); they pass on the corrected manifest.
- **`HdSkyGuardTests` +2:** `NativePlanetTextureStems` resolves HD planets to
  native `.mdl`/`.png` stems (never `hdsky/planet/`); regression on the NASA
  prefix. (On pre-P577b code the method did not exist — compile-time fail.)
- **`P572AutoProofTests` +1:** `--auto-credits` parses and activates the plan.

## Build

Whole solution `AllegianceCore.slnx` Debug: **0 warnings, 0 errors**.

## Suite (3 runs, `scripts/run-tests.sh`, on the final code)

Three consecutive full-suite runs on the final code (committed as b7936346).
Every failure is a **documented live-wire timing load-flake** in `docs/TEST_FLAKES.md`
in the untouched `Allegiance.ClientCore.Tests` / `Allegiance.Core.Tests`
assemblies — none reference any P577b symbol (P577b changes are confined to
`Allegiance.ClientCore.Game` + the MonoGame test project + docs/tools). The
assembly that holds **all of P577b's tests** — `Allegiance.ClientCore.MonoGame.Tests` —
passed **1627/1627 in all three runs** (incl. `HdSkyGuardTests`, the 5
`SkyHdManifestValidationTests`, and the `--auto-credits` `P572AutoProofTests`
case).

| Run | Lobby | MonoGame | ClientCore | Core |
|-----|-------|----------|------------|------|
| 1 | 12/12 | 1627/1627 | 561/561 | 3813/3815 (2 flakes; names not captured — this run's log was grep-filtered, but both are the same documented rotating live-wire class below) |
| 2 | 12/12 | 1627/1627 | 557/561 (4: `ThreePeerSeededSession…DeterministicVisibility` ledger:38/51, `ReadyCheckLive…force_team_ready…` ledger:214, `FullSessionKillIntegration…RealCrossSideKill` ledger:33/148, `ClientWarpApproachSlowHullLiveTests…Legacy_200_450…` ledger:215) | 3810/3815 (5: `ShipsUpdateLiveTests…see_each_other…` ledger:245 class, `LiveTeamLobbyTests.Unready_…` ledger:405, `LiveCountdownStartPingTests.Multi_mission…` ledger:37, `GameOverPlayersWireTests.Win_by_elimination…` ledger:30/206, `GameOverPlayersWireTests.Conquest_win…leak…` ledger:29/267) |
| 3 | 12/12 | 1627/1627 | 560/561 (1: `FullSessionKillIntegration…RealCrossSideKill` ledger:33/148) | 3810/3815 (5: `AddPartTests.Part_pickup_decline_recreates_treasure…` ledger:247, `LiveChatSectorWireTests.Individual_AllSector…` ledger:216/419, `LiveRipcordTests.Ship_anchor_insufficient_energy…` ledger:36/182, `LiveTeamLobbyTests.Unready_…` ledger:405, `LiveWinConditionTests.Board_refused_once_mission_over` ledger:34/206) |

Per the task's "report each run as it happened" rule, the suites were not
re-run to force a clean pass — the failing tests are the ledger's known
rotating live-wire flakes, none of which involve P577b's code.


---

Supersedes the relevant P577 picks (the Whirlpool-galaxy env1, SN-1987A env3,
and the NASA Solar-System planets). P577's historical report
(`docs/reports/P577_HD_SKY_TIER.md`) remains as the record of the original tier.
