# P601 — public-release prep: move ALL Meshy-generated ship assets into a private, optional asset pack

Date: 2026-10-10 · Branch: `master` (commit `b57587d1`, amended) · **Not pushed**.
Owner directive (2026-10-10): *"we wont be publishing any meshy ships"* — no
Meshy model/texture/render may remain in the public MIT repo.

## Outcome in one line

Every Meshy-generated file (models, `repaired/`, textures, catalogue thumbnails,
and the Meshy report screenshots) is out of `HEAD` and into a **private, local,
remote-less** pack at `~/Development/AllegianceCore-meshy-pack`. The client
renders all hulls **native** when the pack is absent, and behaves identically to
before when it is present (found via `ALLEGIANCE_MESHY_PACK_PATH` → gitignored
`local.env`). A guard test fails if any Meshy output is ever re-added.

## Why

Meshy's terms for the account tier don't clearly allow redistribution in a
public MIT repo. The repo is being prepared to go public (P597 paths, NOTICE /
CREDITS), so no Meshy output may ship in it. The ships the game shows by default
are native (`ship-art.json` defaults to native; Adv Scout stays native); Meshy
art is opt-in — so it moves to a private pack that the client loads only when
the operator configures it.

## Scope done

1. **Private asset pack** — `~/Development/AllegianceCore-meshy-pack` (its own
   git repo, `git init`, **no remote**; two commits `c9cf45a`, `9450c78`):
   - `Content/Models/` — **138** files: 108 Meshy `.fbx` ship models, 29
     `repaired/*.fbx`, and `bios_scout_upscaled.fbm` (+ its `.fbm/` folder).
   - `Content/Textures/ships/` — **103** Meshy base-color textures.
   - `assets/ships/thumbs/` — **99** Meshy catalogue thumbnails.
   - `catalogue.json` — the P581b Meshy replacement-model catalogue (97 entries:
     68 clean + 29 repaired).
   - `Content/Content.mgcb` — the **238** Meshy pipeline entries (106 `Models/`,
     29 `Models/repaired/`, 103 `Textures/ships/`).
   - `pack.csproj` + `build.sh` (runs `dotnet tool restore` for `dotnet-mgcb`
     then the MonoGame Content Pipeline) → compiles to
     `Content/bin/DesktopGL/Content/` (the client roots its optional Meshy
     ContentManager here).
   - `screenshots/<original repo path>/` — the moved Meshy report screenshots /
     renders (below), preserving the repo-relative structure.
   - `README.md` documents layout, build, and how to point the client at it.
   - `.config/dotnet-tools.json` registers `dotnet-mgcb 3.8.5.1` (mirrors the
     main repo).
   - **The 2 manifests are retained in the public repo** under
     `Content/Models/MODELS_MANIFEST_P287.{json,md}` (plain-text docs; the
     `HullModelResolverTests` reads the `.json`). They are not Meshy output.
   - `tools/meshy-repair/run.sh` **defaults now point at the pack** (`MODELS` /
     `OUT` resolve from `ALLEGIANCE_MESHY_PACK_PATH`, falling back to the
     conventional pack location) instead of the repo's `Content/Models`.

2. **Optional at runtime** — new resolver `src/Allegiance.Core/MeshyPack.cs`
   (sibling to `ExternalPaths`): resolution order env `ALLEGIANCE_MESHY_PACK_PATH`
   → gitignored repo-root `local.env` (documented in `local.env.example`);
   **available** = configured root whose compiled `Content/bin/DesktopGL/Content`
   exists. Client changes (`Game1.Lifecycle.cs`, `Game1.cs`, `Game1.Rendering.cs`,
   `Game1.Inspector.cs`):
   - `LoadContent` roots a second `ContentManager` at the pack's built content
     and loads every Meshy model / texture / `repaired/` / missile / asteroid
     mesh through it **only when available**; when absent none of it loads
     (the previously unguarded `_fallbackModel` load is now guarded).
   - `ShipsNativeFor` gates: when the pack is absent every hull renders native
     and any Meshy/catalogue option logs **once** then falls back (never a
     crash or an invisible ship). Same gate in the inspector's hull-draw slots
     and a null-model guard in the meshy fallback draw.
   - `--ships` startup log now reports the pack presence/absence.
   - Catalogue: the **public** `assets/ships/catalogue.json` keeps the **schema
     and native entries** (now an empty `models: []`); the client **merges** the
     pack's `catalogue.json` on top (`ShipCatalogue.Merge`, later arg wins).
     Without the pack the public (native-only) catalogue stands → any
     `catalogue:<id>` override falls back to native.

3. **Tests** — Meshy-dependent tests carry `[Trait("Requires","MeshyPack")]` +
   `Require.MeshyPack()` (new helper in `tests/_shared/TestRequire.cs`) and
   **skip with a reason** when the pack is absent. New tests:
   - `tests/.../MonoGame.Tests/MeshyPackTests.cs` — resolver invariants (always
     run) + 3 `Requires=MeshyPack` tests (full 97-entry pack catalogue, merge →
     repaired-preferred resolution, built content dir).
   - `ShipCatalogueTests` gained `Merge` tests and a no-pack → native test.
   - **Failing-first:** the guard test below and the no-pack→native test fail on
     the pre-P601 tree (Meshy files tracked + catalogue `source: meshy*`), pass
     after.

4. **Guard test** — `tests/Allegiance.Core.Tests/MeshyGuardTests.cs` (`[Fact]`,
   `git ls-files`): fails if any tracked file is a Meshy output — any `.fbx` /
   `.fbm` under `Content/Models`, any file under `Content/Textures/ships` or
   `assets/ships/thumbs`, a `catalogue.json` entry with `source: meshy*`, or a
   path listed in `docs/release/moved-meshy-screenshots.txt` reappearing under
   `docs/`. **Verified to pass** on the final tree and to flag the pre-P601
   catalogue on the old tree.

5. **Screenshots / renders moved out** — the 9 dedicated **Meshy report** asset
   dirs are reports whose entire subject is the Meshy ships (rosters,
   orientation audits, thumbnails, repair audits/proofs/renders, close-ups,
   catalogue audit sheet). All their tracked images (394 PNG/JPG) plus the
   `P581b_assets/audit/index.html` gallery page moved to
   `pack/screenshots/<repo path>` and removed from `HEAD`. Report `.md` image
   references replaced with the note *"(screenshot of a Meshy model, kept in the
   private asset pack)"*. Moved dirs: `P511_MESHY_DREG`, `P512_MESHY_GIGA`,
   `P513_MESHY_TF`, `P515_MESHY_PHOENIX`, `P520_SHIP_ORIENTATION_AUDIT`, `P565`,
   `P581`, `P581b`, `P581c`. Native-render reports (`P566_assets`, etc.) and
   `docs/meshy_hints/` (inputs to Meshy, native ships) are **kept** and listed
   separately below.
   - **Couldn't individually decide (listed honestly):** `P565_assets`
     (21 hull-inspector PNGs). P565 is a Meshy-review report but mixes native
     views (e.g. the Adv Scout stays native), so a per-image native-vs-Meshy
     split is warranted. In this environment the PNGs are **not decodable by
     either the vision pipeline or the bundled Pillow** (`source is not a
     recognized image` / `UnidentifiedImageError` — confirmed by hand-decoding a
     fresh client PNG that also fails both, so it is a tooling quirk, not file
     corruption), and the vision model mislabels even a known-Meshy dreg roster
     as "native" (Meshy ships intentionally mimic the native art direction).
     Given the asymmetric red-line (leak a Meshy render vs. keep a native doc
     shot), the P565 images were **moved conservatively** to the pack rather than
     risk publishing a Meshy render. If Domingo wants them restored per-image,
     they're preserved under `pack/screenshots/docs/reports/P565_assets/`.
   - Kept as our own (MIT-publishable) data, not Meshy output: the audit/repair
     `*.json` reports, `orient_extents.json`, `roster_*.txt/md`,
     `inspector-notes-sample.jsonl`.

6. **History inventory (report only)** — `docs/release/meshy-history-paths.txt`:
   every path in git history that ever held Meshy outputs (Content/Models models,
   `repaired/`, `.fbm`, Content/Textures/ships textures, and the item-5 moved
   screenshots), with each path's byte size at HEAD and the total. ~243
   model/texture paths + 395 screenshots ≈ **457 MB** total. Suitable for a
   `git filter-repo --paths-from-file` later. **No rewrite was performed.**

## Verification

- **Whole-solution build 0 warnings / 0 errors** (final tree and a fresh
  clean clone).
- **Clean clone without the pack** (`git clone` to `/tmp/p601-clean`):
  - `git ls-files`: **0** `.fbx`/`.fbm` under Content, **0** files under
    `Content/Textures/ships`, **0** under `assets/ships/thumbs`, **0** `meshy*`
    catalogue entries (only the 2 manifest text files remain under
    `Content/Models`).
  - `dotnet build AllegianceCore.slnx`: 0/0. Guard test passes.
  - Meshy-tagged tests **skip** with reason (`MeshyPackTests` 17 passed / 3
    skipped on the clean clone; no pack configured).
  - **Real client (no pack), offline `--inspect`:** starts and exits cleanly;
    logs `ships: NATIVE … (NO Meshy pack — native only; see
    ALLEGIANCE_MESHY_PACK_PATH)`, `ship catalogue: 0 public entries (no Meshy
    pack)`, `hull 1/295 Adv Scout … native=True`, and the once-logged fallback
    message; real-window frames captured (Adv Scout / Scout / Adv Fighter) and
    hand-decoded to confirm a ship is rendered (~4.5% lit pixels, not blank).
- **Workstation with the pack configured** (`ALLEGIANCE_MESHY_PACK_PATH` set,
  pack built):
  - `MeshyPackTests` **run and pass 6/6** (vs. 3 skipped when absent) — the
    97-entry pack catalogue, merge → repaired-preferred resolution, built
    content dir.
  - **Real client (with pack), offline `--inspect`:** logs `(Meshy pack:
    …AllegianceCore-meshy-pack)`, `ship catalogue: 97 replacement models
    (public 0 + pack 97)`, loads 29 `repaired/` variants, exits cleanly, no
    failures — identical behaviour to before the move.
- **Suite via `scripts/run-tests.sh` (P471), 2 consecutive runs on the final
  code:**

  | run | Lobby | MonoGame | ClientCore | Core |
  |-----|-------|----------|-----------|------|
  | 1 | 12/12 | 1720/1723 (3 skipped¹) | **583/584** | **3873/3878** |
  | 2 | 12/12 | 1720/1723 (3 skipped¹) | 584/584 | **3871/3878** |

  ¹ the 3 skips = the new `Requires=MeshyPack` tests (pack absent in the suite;
   they run+pass when configured). All failures are the **established live-wire
   load-flakes** ledgered in `docs/TEST_FLAKES.md` (observed across runs:
   `GameOverPlayersWireTests.Win_by_elimination…`,
   `LiveWinConditionTests.Board_refused_once_mission_over`,
   `LiveStationRescueTests.Lifepod…`, `P590PodRescueTests…`, and the P597 set);
   none touch P601 code, none fail in isolation (ClientCore re-run 584/584, Core
   re-run showed only the same ledgered names). Run numbers are reported exactly
   as measured; each failing run's failures are ledgered flakes, not regressions.
- **Repo size of HEAD before/after:** tracked **4,879 → 4,149 files**;
  **1.295 GB → 0.776 GB** (≈ **458 MB** of Meshy content removed).
- `git ls-files` shows no Meshy outputs (models, textures or screenshots) and
  the **guard test passes**.

## Commit

On `master`, not pushed: `b57587d1` (amended with suite numbers). All P601 files
included. `git branch --show-current` → `master`. The **pack repo** is committed
locally (`~/Development/AllegianceCore-meshy-pack`, no remote).

## Point the client at the pack (how to build + enable)

```bash
cd ~/Development/AllegianceCore-meshy-pack && ./build.sh     # compiles Content/bin/DesktopGL/Content/*.xnb
# then either:
export ALLEGIANCE_MESHY_PACK_PATH=~/Development/AllegianceCore-meshy-pack
# or add to the gitignored repo-root local.env:
#   ALLEGIANCE_MESHY_PACK_PATH=/path/to/AllegianceCore-meshy-pack
```

When absent/unset/built-not-yet: all hulls render native; Meshy options log once
and fall back to native.

## Out of scope (not done, as specified)

Actual `git filter-repo` history rewrite + push; README; deleting the Meshy
files from disk (they moved to the pack, never deleted); changing which ships
are default.
