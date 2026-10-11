# P601b — Complete Meshy sweep: whole-repo + whole-history inventory, remaining HEAD moves, rebuilt history-rewrite list, repo-wide guard

Date: 2026-10-11 · Branch: `master` (committed, NOT pushed) ·
Pair: Domingo decision 2026-10-10 — no Meshy-generated models/textures/thumbnails/screenshots/renders in the public repo.

P601 (`b57587d1`/`f1690cbb`) moved most Meshy output out of HEAD but only searched
`docs/reports/`. P601b finishes it: a complete inventory of every image/video/model/
texture in HEAD and all history, a full classification, moves of the remaining Meshy
output still in HEAD into the private pack, a rebuilt filter-repo-ready history list,
a dry-run verification on a mirror clone, and a repo-wide guard test.

## 1. Complete inventory (HEAD + all history)

`git log --all --name-only` enumerates **5,625 unique paths**, of which **2,369** are
media (image/video/model/texture/atlas by extension `png jpg jpeg gif webp bmp tga
mp4 webm fbx fbm obj glb gltf blend mtl`, plus binary `.fbm/Image_*` payloads).

Every one is classified in **`docs/release/media-inventory.csv`**
(path, in_head yes/no, class, reason):

| class | count | meaning |
|---|---|---|
| a | 763 | Meshy output (model/texture/thumb/render/screenshot) |
| b | 722 | native/original FreeAllegiance art or renders of it |
| c | 13 | third-party credited art (ESA/Hubble/NASA source photos + dropped Sombrero) |
| d | 871 | our UI/chart/diagram/native gameplay/effect screenshots, VFX atlases |

> Classification note: the **13** class-(c) rows are the raw P589/P592 astronomy source
> photos and the dropped Sombrero files. They are third-party credited but, per Domingo's
> instruction, are ALSO in the history-rewrite list (item 3) so the public history no
> longer carries the multi-MB source photos.

Verification was by name, report context and the P287/P337/P348–P353/P510–P520/P565/
P581* history; ambiguous game screenshots were opened and examined (P572/P575/P562/P565/
p545/p581c etc.) to check whether the client had been running `--ships meshy`. Those
shots show native ships (angular native FreeAllegiance hulls) — class b/d. The
`docs/meshy_hints/` native input renders stay (class b).

**Undecided:** none. Every image settled on a class by name/location/history context +
visual check. Per the brief, any that had stayed unresolved would have been treated as
Meshy and left out of the public set; no such case arose.

## 2. Move remaining (a) out of HEAD → pack

P601 only scanned `docs/reports/`. Two Meshy dirs sat at the `docs/` level and were
missed, plus three P581-scout prompt images:

- **`docs/P514_MESHY_OH_assets/`** (24 files, 21 images) — Omicron-Hive Meshy ship
  orientation renders/rosters (P514, Meshy regeneration of OH). Class a.
- **`docs/P337_ship_textures/`** (7 images) — the P337 "restore real Meshy textures"
  live-proof renders of Meshy-textured ships. Class a.
- **`docs/prompts/p581-dreg-scout2-detached-pieces.png`**,
  **`docs/prompts/p581c-raw-scout-detached.png`**,
  **`docs/prompts/p581c-repaired-scout-spikes.png`** — P581/P581c Meshy-scout (dreg-scout2)
  inspection screenshots showing the raw/repaired Meshy model. Class a.

All 34 files (31 media + 3 companion `.json/.md/.txt`) were copied into the private pack
at **`screenshots/<repo path>`** (e.g. `screenshots/docs/P514_MESHY_OH_assets/…`) and
committed there (**pack commit `048ce0f`**, extended by a follow-up pack commit for the
report-companion data below), then removed from HEAD (`git rm`).

Additionally, P601 had moved the P511/P512/P513/P515/P520 report **images** but left
their companion data files in the MESHY report dirs. For a complete sweep these 5 were
also moved to the pack (`screenshots/docs/reports/P511_MESHY_DREG_assets/orient_extents.json`
etc.) and removed from HEAD: `P511/P513/P515 orient_extents.json`,
`P515 roster_legend.md`, `P515 roster_variety.txt`. The remaining MESHY-named tracked
files are all report `.md` documentation (P511/P512/…/P601b `_MESHY_*.md`) — not Meshy
output, kept.

Doc links replaced with P601's note — *(screenshots of Meshy models, kept in the private
asset pack)* — in `docs/P337_SHIP_TEXTURES.md`, `docs/reports/P514_MESHY_OH.md`,
`docs/prompts/p581c-meshy-repair-spike-artifacts-prompt.md`.

**Result:** `git ls-files` now contains **0 class-(a) files** (verified programmatically
against media-inventory.csv).

## 3. Rebuild the filter-repo-ready history list

`docs/release/meshy-history-paths.txt` is rebuilt as **filter-repo-ready**: one
`literal:<path>` or `glob:<pattern>` per line, **no size column**. Sizes are in a
separate **`docs/release/meshy-history-paths-sizes.txt`** (`<bytes> <path>` + total).

It covers:
- every class-(a) path across ALL history (138 Content/Models fbxs + .fbm payloads,
  103 Content/Textures/ships, 99 assets/ships/thumbs, 401 docs/reports Meshy renders
  incl P511–P515/P520/P565/P581/P581b/P581c, 34 docs-level: P337/P514 + P581 scout prompts);
- the thumbnails;
- the undecided ones (none, but the list is structured so they'd slot in);
- the raw source photos: `glob:docs/reports/*/sources/*` + the literal
  `docs/reports/P589_assets/heic0406a_source_large.jpg`;
- the dropped Sombrero files (`sombrero-m104_*`).

780 literal paths + 1 glob. **Total removed content ≈ 499 MB** (byte sizes summed from
git blobs / pack disk; final total in `meshy-history-paths-sizes.txt`).

## 4. Dry-run verification (mirror clone)

On a throwaway **bare mirror** (`/tmp/meshy-mirror`, cloned from the working repo, never
the working repo, never pushed):

```
git filter-repo --paths-from-file docs/release/meshy-history-paths.txt --invert-paths
```

After the rewrite, re-ran the media classification over the filtered history:

- **class-(a) paths remaining: 0** (of 780 removal-set literals — all gone).
- All 5 probed source-photo / Sombrero glob targets also removed.
- Repository size **before ≈ 2.3 GB → after ≈ 503 MB** (git count-objects: in-pack
  1.90 GiB → 502 MiB; 819 → 818 commits).
- Largest remaining blobs are native-proof renders, e.g. 10.5 MB
  `P576_assets/contact_planets_full.png`, 8.1 MB `P592 sprites/milkyway_terra_sprite.png`,
  6.1 MB `P588 proof/hd_Alderan_d0.png`; 100 blobs > 1 MB remain (all native/credited).

Temp mirror deleted afterwards.

## 5. Repo-wide guard test

`tests/Allegiance.Core.Tests/MeshyGuardTests.cs` rewritten to fail if ANY tracked file
anywhere is a Meshy output, detecting by:
1. **location** — `Content/Models` (.fbx/.fbm), `Content/Textures/ships`,
   `assets/ships/thumbs`;
2. **extension** — `.fbx .fbm .obj .glb .gltf .blend .mtl` anywhere outside the
   (currently empty) native allowlist;
3. **name** — any image/video/model whose path contains `meshy`, except
   `docs/meshy_hints/` and `tools/meshy-repair/`;
4. **authoritative list** — every class-(a) path in `media-inventory.csv`.

Verified **passes** on the fixed tree (0 class-a tracked), and **fails** when a Meshy
file is added (temp `assets/ships/thumbs/test-meshy-thumb.png`, staged → guard reported
it → file removed). Cleanup verified: `git ls-files assets/ships/thumbs/` is empty.

## Build & tests

- Whole-solution build: **0 warnings, 0 errors** (checked before the suite, on the
  committed tree).
- Full suite via `scripts/run-tests.sh` — **2 consecutive runs on the final code**
  (per Domingo 2026-10-09, reduced from 3; flake ledger in `docs/TEST_FLAKES.md`).

Run 1 (on committed tree):

| assembly | result |
|---|---|
| Allegiance.Lobby.Tests | 12/12 passed |
| Allegiance.ClientCore.MonoGame.Tests | 1720 passed, 3 skipped (MeshyPack — no pack built content) |
| Allegiance.ClientCore.Tests | 593/593 passed |
| Allegiance.Core.Tests | 3908/3914 passed — **6 failed (all ledgered load-flakes)** |

Run-1 failures (all live-wire/fixed-sleep load-flakes of the documented class in
`TEST_FLAKES.md`; every one verified to pass in isolation, so none is a real bug):
`GameOverPlayersWireTests.Win_by_elimination_…` (ledger line 432, intermittent —
failed once, passed on isolated retry), `CargoBayReloadTests.Reload_NA_terminates_…`
(ledger line 360) and `…Reload_transfers_ammo_…` (rotating CargoBay member, ledger
lines 53-54 class), `SideEliminationLiveTests.Eliminating_side2_…` (ledger lines
39/301/358/395), `P591dMinefieldExportAudienceTests.Npc_layer_drone_…` and
`BotFlightTests.Abrupt_disconnect_during_broadcast_…` (live-wire timing; pass in
isolation — verified). All are fixed-sleep/live-wire receipt timing; none fail
deterministically in isolation, none touch this change (release/docs/inventory +
guard test only).

Run 2 (second consecutive run on final code):

| assembly | result |
|---|---|
| Allegiance.Lobby.Tests | 12/12 passed |
| Allegiance.ClientCore.MonoGame.Tests | 1720 passed, 3 skipped |
| Allegiance.ClientCore.Tests | 591/593 passed — **2 failed (ledgered)** |
| Allegiance.Core.Tests | 3909/3914 passed — **5 failed (all ledgered)** |

Run-2 failures: `ClientWarpApproachSlowHullLiveTests.Legacy_200_450_…` (ledger
lines 215/359) & `ThreePeerSeededSessionTests.SeedThreePeers_…` (line 38),
`GameOverPlayersWireTests.Win_by_elimination_…` (432), `LiveStationRescueTests.Lifepod_…`
(line 156), `LiveWinConditionTests.Board_refused_…` (34),
`P590PodRescueTests.Pod_pickup_order_…` (432, newly-documented at line 435),
`SideEliminationLiveTests.Eliminating_side2_…` (39). Each passes in isolation (verified
for the not-already-ledgered ones; P590PodRescue passed 2/2 isolated).

Both runs' only failures are the established fixed-sleep / live-wire-receipt
**load-flakes** documented in `TEST_FLAKES.md`; none fail in isolation, none involve the
P601b change (release/docs/inventory + guard test only, no product code). Numbers above
are exactly as measured.

## Verification summary

- `docs/release/media-inventory.csv` committed; **undecided list: empty** (in report §1).
- `git ls-files` has **no class-(a) files**; guard passes and fails-on-add (tested).
- Dry run: **0 Meshy paths in filtered history**, sizes reported (§4).
- Whole-solution build 0/0; suite 2× consecutive on final code.

Deliverable commit on `master`, not pushed; pack commit in the private pack repo.
