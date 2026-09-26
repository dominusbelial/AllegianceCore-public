# P350 — Audit and fix cross-civ hull mismatches in the P287 asset roster

## Context

`AllegianceCore` is a C# port of native FreeAllegiance
(`~/Development/Allegiance`, read-only reference, never modify it).

Live investigation today (2026-09-22), triggered by comparing this
port's rendered ships against the FreeAllegiance community wiki
(freeallegiance.org/FAW) and the real native `.mdl` corpus
(`~/Development/Artwork/*.mdl`), found a real, systematic bug: **this
port's per-civ ship-model mapping (`RosterPlanner`/`HullModelResolver`)
is picking the wrong, cross-civ hull for at least several Bios combat
classes**, because hull data carries no `CivilizationId` field at all
(confirmed by reading `DataHullTypeIGC`, `src/Allegiance.Core/Data/DataHullTypeIGC.cs`),
so the witness-hull search
(`RosterPlanner.Plan`, `src/Allegiance.ClientCore.MonoGame/Rendering/RosterPlanner.cs:78-122`)
just picks the **first** hull in iteration order that resolves to a
given `(civ, role)` asset name — with no actual signal that the chosen
hull is really that civ's own art.

## What was found, with hard evidence

**The native hull corpus has real, per-civ-dedicated hull ID blocks**,
identifiable by a consistent `ModelName` prefix family unique to each
civ (confirmed by directly reading `PCore001.igc` hull-by-hull, cross-
checked against real `.mdl`/texture files on disk and actual rendered
output via the `mdl_parse.py`/`decode_texture.py`/`textured_render.py`
pipeline, `~/Development/allegiance-tools/reference/`):

- **Bios (civId 35)**: hull IDs 13-25 (minus 23, which is a different
  civ's stealth bomber sitting in the same numeric range — the blocks
  are NOT always perfectly contiguous, don't assume they are), all
  `quiz*`-prefixed models (`quizscoutv2`, `quizfigv2`, `quizsfv2`,
  `quizintv2`, `quizbbrv2`) — confirmed visually: all render with the
  same distinctive green-scale/orange-highlight livery, a real,
  consistent house style, verified by rendering `quizscoutv2` (hull 13,
  already this port's own default-spawn hull, confirmed correct
  separately),`quizfigv2` (15/16), and `quizbbrv2` (22/24/25) through
  the full pipeline and visually inspecting all three.
- **Another civ (not yet identified — could be `tf`/Technoflux or a
  different faction, needs confirming)**: hulls ~49-65ish, `rain*`-prefixed
  (`rainscoutv2`, `rainfigv2`, `rainsfv2`, `rainintv2`, `raincarr` —
  note this civ's block includes a carrier, which Bios's `quiz*` block
  does not).
- **GT/Ga'Taraan (civId 3)**: hulls ~97-123ish, `wc_gt*`-prefixed
  (`wc_gtcon`, `wc_gtmnr`, `wc_gtpod`, `wc_gtsct`, `wc_gtfig`,
  `wc_gtadv`, `wc_gtpt`, `wc_gtstang`).
- **The actual bug, concrete instance**: this port's roster currently
  maps `bios-fighter`→`wc_gcfigx` (hull 258) and
  `bios-interceptor`→`wc_gchvyx` (hull 251) — note the prefix
  `wc_gc`, suspiciously close to GT's confirmed `wc_gt` prefix but with
  `c` instead of `t`. Strong working hypothesis (**confirm, don't
  assume**): `wc_gc` = GigaCorp (civId 18, "gc" for "GigaCorp"), meaning
  Bios's fighter and interceptor slots are currently rendering
  **GigaCorp's** real ships, not Bios's. Similarly `bios-stealthfighter`
  →`fig17` and `bios-bomber`→`bom05b` are generic/shared hulls used by
  many OTHER civs' same-role slots too (confirmed: `bom05b` is one of
  at least 9 near-identical "Gunship/Hvy Bomber/Bomber" model families
  — `bom04`, `bom07b`, `bom20`, `bom30`, `dn_faphshbmb`, `faohshbmb`,
  `wc_icbmb`, `quizbbrv2` all share the exact same 3-name pattern, one
  real hull family per civ — `bom05b` is very unlikely to be Bios's own).

## Scope

1. **Systematically map every one of the 9 playable factions**
   (`RosterPlanner.FactionOrder`: bios=35, gt=3, ic=25, rix=201,
   belters=101, dreg=1, giga=18, oh=5, tf=2) to its own real, dedicated
   `ModelName` prefix family and/or hull-ID block, using the same
   methodology demonstrated above: scan `PCore001.igc` hull-by-hull
   (grouped/sorted by `HullId`), look for a hull-ID cluster where
   multiple classes (scout/fighter/stealthfighter/interceptor/bomber/
   carrier/etc.) share a consistent, civ-unique `ModelName` prefix, and
   **confirm with a real rendered image** (via the existing
   `mdl_parse.py`→`decode_texture.py`→`textured_render.py` pipeline,
   `~/Development/allegiance-tools/reference/`) that hulls sharing a
   candidate prefix actually share a consistent visual house style —
   don't conclude from prefix pattern-matching alone, verify visually
   for at least the classes you intend to change.
2. **Not every civ will have a full quiz-style block for every role** —
   Bios's own block only covered 5 of its 13 P287 roster classes
   (scout/fighter/stealthfighter/interceptor/bomber); the other 8
   (carrier/constructor/corvette/cruiser/destroyer/devastator/ejectpod/
   miner/trooptransport) had no matching dedicated hull in that range.
   This may be genuinely by design (capital/utility ships shared across
   factions in the original game) — don't force a fake civ-specific
   pick where none exists; only fix classes where a real, confirmed
   civ-dedicated hull is found.
3. **Fix the witness-hull selection** (`RosterPlanner.Plan`,
   `RosterPlanner.cs:99-104`, and/or `HullModelResolver` if the same
   "first match wins" logic exists there too — check both) to prefer a
   hull matching the target civ's own confirmed prefix/block when one
   exists for that role, falling back to the current "first hull found"
   behavior only when no civ-specific candidate exists. A per-civ
   prefix table (derived from your own audit in step 1, not guessed) is
   the natural shape — keep it as data, not scattered conditionals.
4. **Re-verify the full 72-asset roster after the fix**: confirm the
   previously-wrong Bios classes now resolve to their real `quiz*`
   hulls, confirm classes that were already correct (or had no
   civ-specific alternative) are unchanged, and spot-check at least 2-3
   other factions' fixed classes the same way (real render, visual
   check — not just "the code compiles").

## Explicitly out of scope this task

- **The 8 Bios classes with no confirmed civ-specific hull** (carrier,
  constructor, corvette, cruiser, destroyer, devastator, ejectpod,
  miner, trooptransport) — leave their current mapping alone unless
  your own audit finds real, confirmed evidence otherwise. Don't guess.
- **Re-running the Meshy regeneration** for any hull whose witness
  changes — that's separate, deliberate future work (P348's own
  catalogue exists for exactly that, whenever it's actually run); this
  task only fixes which native `.mdl`/P287 asset gets used as the
  reference and in-game model, not regenerating anything.
- **The FreeAllegiance community wiki (freeallegiance.org/FAW)** as a
  primary source — it was the original tip-off but turned out to be an
  unreliable cross-check this session (its "Bios-bomber.png" didn't
  match either of the two real native Bios-bomber-adjacent candidates
  found). Treat the real `.mdl` corpus + actual rendered output as the
  only ground truth; the FAW wiki may reflect a different game
  build/mod, don't chase matching it.
- Do not touch native reference source (`~/Development/Allegiance`) —
  read-only.

## Verification — real proof, not just build/test

- **Live proof, required**: for every class you actually change, render
  both the OLD (currently-mapped) and NEW (corrected) hull through the
  real `.mdl` pipeline (or the in-repo `roster-capture turnaround`
  tool, P348, whichever is more direct) and visually confirm the new
  one is a plausible fit for that civ's confirmed house style, and/or
  run a real `vizfight roster` session and confirm the in-game model
  actually changed to the new hull for that civ+class.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected (failing test names vary run to run — that's the known
  signature). Check specifically whether any EXISTING test hardcodes
  an old (now-wrong) hull expectation for a class you changed — update
  those tests' expectations to the corrected hull, with a comment
  citing this task's finding, rather than leaving them silently wrong
  or deleting them.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes). Do
   not create a side branch.
2. A short final summary: the confirmed per-civ prefix/block table for
   all 9 factions (or as many as you could confirm — say plainly which
   ones you couldn't pin down and why), exactly which classes changed
   for which civs, the live-proof performed, build/test status.
