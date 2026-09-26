# P373 — no audio cue when a new ship (enemy or friendly) first becomes visible

## Why this task exists

Domingo asked live: "also enemy ships on the same sector should be
visible and it had a sound cue too" — while investigating P372 (radar/
HUD has no friend/enemy color). Investigated the sound-cue half
separately and confirmed a real, distinct native feature this port is
missing entirely.

**Confirmed real native mechanic** (`WinTrek/trekigc.cpp:2076-2093`):

```cpp
if (!m_sideVisibility.fVisible())
{
    if (m_bIsShip)
    {
        IsideIGC* pside = pmodel->GetSide();
        if ((pside != trekClient.GetSide()) && (!pside->AlliedSides(pside,trekClient.GetSide())))
        {
            trekClient.PlaySoundEffect(newShipSound, pmodel);
        }
        else
        {
            trekClient.PlaySoundEffect(newEnemySound, pmodel);
        }
    }
}
```

This fires exactly once per ship, on the real edge transition from
NOT-visible to visible (`m_sideVisibility.fVisible()` false → about to
become true) — not a repeating alarm, not polled every frame. Two
distinct sound effects are used depending on whether the newly-visible
ship is hostile (different, non-allied side) or same-side/allied —
confirm the exact variable-to-condition mapping yourself when you
implement this (the two variable names `newShipSound`/`newEnemySound`
read as swapped relative to the condition at first glance — read
enough surrounding context, including wherever these two sound handles
are originally loaded/named in the resource data, to get the real
enemy/friendly mapping right rather than guessing from the misleading
variable names alone).

**Port status, confirmed**: grepped this port's audio code
(`Allegiance.ClientCore.MonoGame/Audio/`) and `Game1.cs` for any
first-sighting/new-contact sound concept — nothing exists. Ships
becoming visible currently makes no sound at all.

## Scope

1. **Read the rest of `m_sideVisibility`'s visibility-tracking logic**
   in `trekigc.cpp` around this call site (the surrounding
   `if`/`else if` chain, lines ~2050-2110 and nearby) to understand
   exactly what "becomes visible" means in native terms — likely tied
   to the same real side/ally/scanner-range visibility logic already
   referenced in this port's own `SeenBySide`/cross-side EXPORT
   machinery (P369 just fixed real bugs in exactly this area — read
   its report `P369_SHIP_SIDEID_NEVER_SET.md` for the current, correct
   state of cross-side identity/visibility in this port before
   building on it).
2. **Determine the real client-side trigger condition for THIS port**:
   the moment a ship enters `world.Ships` with a real identity (a
   fresh `ExportShip`/teammate export) for the first time, OR the
   moment a previously-`IsGone` (P370) ship becomes visible again —
   both count as "first sighting" from the local player's perspective.
   Fire once per such transition, not on every subsequent position
   update for an already-known ship.
3. **Two real distinct sound effects**: friendly/same-side newly-
   visible vs enemy (different, non-allied side) newly-visible. Check
   this port's existing sound asset pipeline
   (`Allegiance.ClientCore.MonoGame/Audio/SoundManager.cs`, and
   whatever native sound resource names correspond to
   `newShipSound`/`newEnemySound` — check native's sound definition
   data, e.g. `sounddef`-style resources, for the real asset names) —
   reuse or add the two real distinct cues faithfully rather than
   using one generic "ping" for both cases.
4. Since this port has no alliance concept (an already-established,
   accepted simplification elsewhere in the project), "allied side"
   collapses to "own side only" — same convention used in P372, don't
   build a new alliance concept here either.

## Explicitly out of scope

- P372's visual radar/HUD side-coloring — separate, complementary (one
  is audio, one is visual); don't merge them into one task, but do
  make sure both share the same real "which side is this ship on"
  data source rather than each re-deriving it independently.
- Any repeating/ongoing "enemy nearby" alarm — native's mechanic is a
  one-shot first-sighting cue, not a continuous warning tone. Don't
  build something native doesn't have.
- P368-P371 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: real multi-client session, 2+ real sides.
  Confirm a real, audible, DISTINCT sound plays exactly once at the
  moment an enemy ship first becomes visible to you, and a different
  distinct sound plays exactly once when a same-side/friendly ship
  first becomes visible — and confirm it does NOT replay on every
  subsequent frame/update while that ship remains visible (only on the
  genuine first-sighting edge). Also test the P370 "ship returns to
  view" case (undocks / warps back in) — confirm this correctly counts
  as a fresh sighting too if that matches native's real behavior for a
  ship re-entering after being marked gone (verify against native
  rather than assuming either way).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The new-contact sound cue, committed on `master` (not pushed —
   Domingo pushes). Do not create a side branch.
2. Report doc `P373_NEW_CONTACT_SOUND_CUE.md` in the repo root: the
   real enemy-vs-friendly sound mapping confirmed (correcting the
   misleading native variable names if that's what you find), the
   exact client-side trigger condition chosen, and the live
   verification performed.
