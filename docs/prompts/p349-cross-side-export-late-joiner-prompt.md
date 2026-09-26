# P349 — Cross-side ship EXPORT never catches up a peer who joins after the visibility transition

## Context

`AllegianceCore` is a C# port of native FreeAllegiance
(`~/Development/Allegiance`, read-only reference, never modify it).

Direct follow-up to P346 (`1c1fb7a`, cross-side ship identity EXPORT)
and P347 (`d7abf80`, MonoGame client decode of the cross-side
visibility sync). Live-tested today (2026-09-22) against a full 72-bot
`vizfight roster` session: Domingo's own words, "not working i saw
many of the same ship, like 65 of my same ship" (repeated on a second,
independent retest: "same deal") — connecting a real MonoGame client
as the Bios-seated viewer, the vast majority of the 71 non-Bios ships
render as the same fallback placeholder model instead of their real,
distinct per-civ hulls.

**Root cause, confirmed by direct comparison of the two export
mechanisms — this is real, not a guess:**

- **Server-side identity delivery IS mostly working**: the live
  session's own server log shows `[xexport]` firing 555 times total,
  including **58 distinct ships exported specifically to side 0**
  (the human viewer's own side) — confirmed via
  `grep "\[xexport\] side 0 <-"`. So most cross-side identity data
  genuinely was sent by the server, contradicting a naive "P346 never
  fires" theory.
- **`HullModelResolver.ResolveAsset` is also confirmed correct**: a
  direct offline test resolving several of the real live (civ, hullId)
  pairs actually exported this session (`rix`+381→`rix-int`,
  `gt`+258→`gt-fighter`, `belters`+290→`belters-bomber`, etc.) all
  resolved to the correct, distinct per-civ asset — no nulls, no
  fallback. The resolver itself is not the bug.
- **The real, structural gap**: compare the two export mechanisms
  directly.
  - Same-side `[teamexport]` (`Server.Broadcast.cs:440-480`, the
    original, working design) checks a per-`(peer, ship)` "already
    sent" set (`_teammateExportSent`) **every single tick** for every
    connected peer × every teammate ship — so a peer that joins the
    mission at ANY time still gets caught up on its very next tick,
    because the loop re-evaluates every peer against every ship it
    hasn't yet been told about, unconditionally.
  - P346's cross-side export (`Server.Broadcast.cs:183-188`,
    `Step("crossSideExports", ...) => sector.DrainCrossSideExports()`)
    is fundamentally different: it is driven **purely by a one-shot
    queue**, populated only at the exact moment
    `Sector.ResolveCrossSideVisibility` detects a genuine
    false→true transition for a given `(ship, side)` pair. Once
    drained and sent to whichever peers happen to be connected to
    that side **at that exact tick**, the event is gone forever — no
    re-check, no latch, no "peers I haven't told yet" concept at all.
  - **This exactly matches `vizfight roster`'s own real boot order**:
    all 72 bots connect and become mutually visible to each other's
    sides well before a human viewer typically connects last (per the
    tool's own console flow: "full roster parked; waiting for a
    spectator..."). By the time the human's own side-0 presence is
    seated, most cross-side visibility transitions to side 0 already
    fired — to nobody useful (or to bios bots that connected earlier)
    — and will never fire again for the newly-joined human peer.
    (The 58 exports that DID reach side 0 in the live session are
    plausibly the minority of transitions that happened to fire at or
    after the human's own connection — timing-dependent, not
    reliable.)

## Scope

1. **Redesign P346's cross-side export delivery to match the
   `[teamexport]` per-`(peer, ship)` "catch every peer up" pattern**,
   not the one-shot transition-queue pattern. Concretely: instead of
   (or in addition to) draining `Sector.DrainCrossSideExports()`'s
   transition events, add a per-tick sweep (mirroring `[teamexport]`'s
   own loop shape exactly, `Server.Broadcast.cs:440-480`) that, for
   every connected peer and every ship currently `SeenBySide`-true for
   that peer's side (P132's live sweep result — already correct and
   tested, don't touch it), sends the cross-side EXPORT if-and-only-if
   a matching `_crossSideExportSent`-style set (new, parallel to
   `_teammateExportSent`) doesn't already contain
   `(peerId, shipId)`. This guarantees a peer who joins after
   visibility was already established still gets caught up on its
   next tick, exactly like teammates already are.
2. **Decide whether the original transition-queue mechanism
   (`DrainCrossSideExports`) stays, is removed, or is subsumed** by
   the new per-tick sweep — state your reasoning plainly. (The
   per-tick "haven't-told-you-yet" sweep alone is likely sufficient
   and strictly more correct — it naturally covers both the "newly
   visible" and "newly joined" cases with one mechanism, the same way
   `[teamexport]`'s own single loop already does for the same-side
   case. If you keep both, explain why a dual mechanism is actually
   needed rather than redundant.)
3. **Real per-recipient bookkeeping**: a set keyed on
   `(peerId, shipId)`, not just `(shipId, sideId)` — multiple peers can
   share a side (as this exact roster scenario has, side 0 = Bios =
   the human + several Bios bots), and each individual peer connection
   needs its own "have I told THIS peer" latch, exactly mirroring
   `_teammateExportSent`'s own key shape.
4. **Confirm cleanup on disconnect** — same-side `_teammateExportSent`
   presumably has (or needs) disconnect cleanup so the set doesn't
   grow unbounded across a long server lifetime; give the new
   cross-side set the same treatment, whatever that turns out to be.

## Explicitly out of scope this task

- **`Sector.ResolveCrossSideVisibility`/P131's `InScannerRange`/
  `LineOfSightExist`** — the actual visibility DETERMINATION is
  correct and already tested; this task only fixes WHO gets told about
  an already-correctly-computed visible ship, and WHEN.
- **The client-side decode (P347)** — confirmed correct in this
  session's own investigation (`Apply(HeavyShipsUpdate)`/
  `Apply(ExportShip)` merge into the same entity correctly via
  `Upsert`); nothing here needs a client-side change.
- **`HullModelResolver`/`ResolveAsset`/model-texture resolution** —
  confirmed correct by direct testing this session; don't touch.
- **The same-side `[teamexport]` path itself** — reference/copy its
  pattern, don't modify it.
- Do not touch native reference source (`~/Development/Allegiance`) —
  read-only.

## Verification — real proof, not just build/test

- **Live proof, required**: reproduce the exact reported scenario —
  `vizfight roster <port>` (full 72-hull, no civ filter), let it run
  for a bit so bots' mutual cross-side visibility transitions have
  already settled, THEN connect a real MonoGame client as the late-
  joining human viewer (this ordering is the whole point — don't
  connect the viewer first, that would accidentally not exercise the
  bug). Confirm the client now renders genuinely distinct per-civ
  hulls for ships across multiple factions, not a sea of the same
  fallback model. A description or capture showing several visibly
  distinct hull shapes/textures simultaneously is the real deliverable
  Domingo is after ("render all hulls for a nice picture").
- Also confirm the ALREADY-WORKING case regresses cleanly: a peer
  connected from mission start (like P346/P347's own original live
  proofs) still gets its cross-side exports correctly and promptly,
  not delayed or duplicated.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected (failing test names vary run to run — that's the known
  signature).

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes). Do
   not create a side branch.
2. A short final summary: the final mechanism shape (per-tick sweep
   vs. transition-queue vs. both, and why), the late-joiner live proof
   performed (describe what was actually seen), regression confirmation
   for the already-working early-joiner case, build/test status.
