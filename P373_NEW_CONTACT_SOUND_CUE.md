# P373 — a one-shot sound cue when a new ship (enemy or friendly) first becomes visible

**Summary.** Native Allegiance plays a one-shot sound cue the instant a ship
first becomes visible to the player — a real, distinct not-visible→visible
edge, not a repeating alarm — with two DIFFERENT cues depending on whether the
newly-visible ship is hostile or friendly (`WinTrek/trekigc.cpp:2076-2094`,
`UpdateSideVisibility`). Before this task the port had NO such concept: a ship
entering view made no sound at all. This task ports the mechanic faithfully to
the MonoGame client. Committed on `master` (not pushed — Domingo pushes).

---

## 1. The real enemy-vs-friendly sound mapping (native variable names are SWAPPED)

Native's call site (trekigc.cpp:2080-2094):

```cpp
if (!m_sideVisibility.fVisible())
{
    if (m_bIsShip)
    {
        IsideIGC* pside = pmodel->GetSide();
        if ((pside != trekClient.GetSide()) && (!pside->AlliedSides(...)))  // ENEMY
            trekClient.PlaySoundEffect(newShipSound, pmodel);               // <- newShipSound
        else                                                                // FRIENDLY/same/allied
            trekClient.PlaySoundEffect(newEnemySound, pmodel);              // <- newEnemySound
    }
}
```

The **variables are misleadingly named**. Resolving them to their real asset
definitions in the resource data (`Artwork/sounddef.mdl:247-248`):

```
newShipSound   = GainSound(SFXGain, ImportWave("newtarget"));         // sounddef.mdl:247
newEnemySound  = GainSound(SFXGain, ImportWave("newtargetenemy"));    // sounddef.mdl:248
```

So the **confirmed, code-faithful mapping** is:

| Newly-visible ship | Variable played | Real wave asset |
|---|---|---|
| **Enemy** (different, non-allied side) | `newShipSound` | `newtarget.ogg` |
| **Friendly** (same side / allied)      | `newEnemySound` | `newtargetenemy.ogg` |

I reproduce the behavior the CODE produces (enemy → `newtarget`, friendly →
`newtargetenemy`), not the swapped variable-name reading. The two assets are
genuinely distinct files (MD5 `07db61e3…` vs `fc4bbce1…`, durations 2.045 s vs
1.877 s), so the two cases are audibly different cues, matching native.

**Native also re-cues on return**: `UpdateSideVisibility` fires on the
`m_sideVisibility.fVisible()` false→true edge each time it happens, so a ship
that was hidden (docked / warped out, `SDR_DOCKED`/`SDR_LEFTSECTOR`, P370) and
then becomes visible again counts as a FRESH sighting and cues again. The port
reproduces that.

---

## 2. The exact client-side trigger condition chosen

This port's MonoGame client is a **pure renderer with no local IGC/visibility
sim** — it cannot evaluate native's `InScannerRange`/`SeenBySide` detection. Its
"is this ship visible to me" model is exactly what the server already telegraphs
into `ClientWorld`:

- a ship first enters `world.Ships` with a real identity via `EXPORT(26)`
  (a same-side `[teamexport]` or a cross-side identity export, P369), or
- a previously-`IsGone` (P370: docked / warped-out) ship becomes visible again
  — re-`EXPORT`, or 176 telemetry resuming for an already-identified ship (the
  P369 cross-side identity latch is one-shot per (peer,ship), so a returning
  cross-side ship often has NO fresh EXPORT and returns via 176 alone).

**Chosen edge**: the moment a ship transitions absent/gone → visible **and is
classified from its real `SideId`** (the SAME source P372's side-colour uses —
the task's "share one data source" requirement). Concretely:

- `ClientWorld.Apply(ExportShip)` (real-id branch) and `ClientWorld.Apply(HeavyShipsUpdate)`
  (176) call a new `AnnounceNewContactIfNew(entity)` which queues a
  `ClientNewContactCue(shipId, isEnemy)` **exactly once per visibility episode**.
- A per-entity transient latch `ShipEntity.NewContactSounded` suppresses re-firing
  while the ship stays visible (routine pose/identity refreshes never re-cue);
  it is reset to false when the ship leaves view (`Apply(ShipDelete)` /
  `Apply(KillShip)`), so a returning ship re-cues (native's edge semantics).
- Classification: `enemy = entity.SideId != ClientWorld.MySideId` (no alliance
  concept in this port — "allied" collapses to own side only, the P372
  convention). The **local player's own ship** (`IsMine`) and **dead ships**
  never announce.
- A `HasRealIdentity` guard on the 176 path means a not-yet-identified ship
  (its `SideId` still the C# default 0) is NOT cued from that default — it cues
  once the `EXPORT` identifies it (avoiding a misclassification during the brief
  pre-identity window; the server sweeps cross-side EXPORT *before* 176 in the
  same tick, so ordering is favorable).

`Game1.UpdateFlying` drains the queue each frame under the session gate lock
(mirroring the existing `TakeSoundEvents`/`TakeNewContactCues` drain-then-forget
lifecycle) into `SoundManager.PlayNewContact(isEnemy)`, which plays
`newtarget.ogg` (enemy) or `newtargetenemy.ogg` (friendly).

Why both ExportShip and 176: the "returns to view without a fresh EXPORT" case
(the one-shot P369 latch) can only be observed on the 176 edge; the "brand-new
identified ship" case originates at the EXPORT. Testing both paths and the latch
against each ordering is what makes it fire exactly once per real transition.

---

## 3. What was built

- `src/Allegiance.ClientCore/State/ClientNewContactCue.cs` (new) — the queued
  `ClientNewContactCue(shipId, isEnemy)` record.
- `src/Allegiance.ClientCore/State/ShipEntity.cs` — `NewContactSounded` (the
  one-shot per-episode latch) and `HasRealIdentity` (identity-established gate).
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — the `_newContactCues`
  queue + `NewContactCues`/`TakeNewContactCues()`; `AnnounceNewContactIfNew`;
  hooked into `Apply(ExportShip)` (real-id) and `Apply(HeavyShipsUpdate)`; latch
  reset in `Apply(ShipDelete)`/`Apply(KillShip)`; cleared in `Reset()`.
- `src/Allegiance.ClientCore.MonoGame/Audio/SoundManager.cs` — loads
  `Sounds/newtarget` + `Sounds/newtargetenemy`, new `PlayNewContact(bool isEnemy)`.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — drains the cue queue each frame
  under the gate lock into the sound manager.
- `src/Allegiance.ClientCore.MonoGame/Content/Content.mgcb` + two new tracked
  assets `Content/Sounds/newtarget.ogg`, `newtargetenemy.ogg`.
- `tests/Allegiance.ClientCore.Tests/ClientWorldNewContactCueTests.cs` (new, 10).
- `.verify_p373/harness/` — live 2-side verification harness.

---

## 4. Verification

### Deterministic tests — `ClientWorldNewContactCueTests` (10, all pass)
New teammate EXPORT → friendly cue once; new enemy EXPORT → enemy cue once;
refresh of an already-visible ship → no re-cue; own ship (dummy + real) never
cues; P370-gone ship returning via EXPORT re-cues as friendly/enemy; returning
identified ship re-cues via 176 (the no-fresh-EXPORT case); unidentified ship on
176 does not cue from default SideId; dead ship never re-cues; multiple cues
drain in order then empty; `Reset()` clears pending cues.

### Live proof — real multi-client, 2 real sides (`.verify_p373/harness`)
ONE real in-process `Allegiance.Server` (PCore001 + `XXV_simple`, teams:2,
largest-scanner hull 92 so ships are mutually visible) with THREE real
`ConnectionSession` clients over the real TCP transport, driving real
`ClientWorld`s — the exact data plane the MonoGame client uses. The viewer
drains its real `TakeNewContactCues()` each phase:

- **Viewer A (Alpha) seated alone** → drains **0 cues** (own ship never cues).
- **Friendly B (Alpha) joins** → A learns B's ship and drains **exactly ONE
  cue, `IsEnemy=false`** (friendly), no spurious cues.
- **Enemy C (Bravo) joins** → A learns C's ship and drains **exactly ONE cue,
  `IsEnemy=true`** (enemy), no spurious cues.
- **Steady state (~15 s / 300 ticks)** → drains **0 cues**: an already-visible
  friendly and enemy do **NOT** replay on subsequent position updates (the
  one-shot-per-edge requirement).
- The two cue assets are confirmed **distinct** (different MD5, 20 829 B vs
  21 484 B; durations 2.045 s vs 1.877 s) → the two cases are audibly different.

Result: **passes=17, fails=0, exit 0** (log `p373_verify.log`). Auditory
playback itself can't be heard in a headless run; the proof is that the two
real, distinct assets are wired to the correct one-shot classifications at the
correct edges, exactly as native.

### P370 "returns to view" direction
Covered by the passing unit test `Returning_identified_ship_cues_via_heavy_ships_update`
(re-cue on 176 after a `SHIP_DELETE`) composed with the already-passing P370
live suites in the repo (`ShipDeleteGoneLiveTests` prove a real dock/warp-out
emits `SHIP_DELETE` and the watcher latches `IsGone`, and undock/warp-back
resumes 176 telemetry and clears it) — the full real-transport chain for that
edge, and it matches native (native re-fires on the `fVisible()` false→true edge
for a returning ship).

### Build & test
- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**
  (Release also clean).
- Full `dotnet test` run **2×**: only the pre-existing documented live-wire
  timing flakes appear (run 1: ShipsUpdate/ProjectileBroadcast/NpcShipFoundation/
  LiveTurretGunner/InboundFlood; run 2: ProjectileBroadcast/LoadoutChange/
  LiveTurretGunner/LiveRipcord/CargoBayReload) — they vary between runs, touch
  none of the P373 symbols or changed files, and none involve the cue paths.
  `Allegiance.ClientCore.Tests` (which holds the 10 new tests) passed
  **214/214** in run 2.
- Targeted suites green: `ClientWorldNewContactCueTests` (10) + `ShipDeleteGoneTests`
  (12) + `ClientWorldSideIdTests` + `ClientWorldSoundEventTests` (20 total).

---

## 5. Files changed (committed on master)

- `src/Allegiance.ClientCore/State/ClientNewContactCue.cs` (new)
- `src/Allegiance.ClientCore/State/ShipEntity.cs`
- `src/Allegiance.ClientCore/State/ClientWorld.cs`
- `src/Allegiance.ClientCore.MonoGame/Audio/SoundManager.cs`
- `src/Allegiance.ClientCore.MonoGame/Game1.cs`
- `src/Allegiance.ClientCore.MonoGame/Content/Content.mgcb`
- `src/Allegiance.ClientCore.MonoGame/Content/Sounds/newtarget.ogg` (new)
- `src/Allegiance.ClientCore.MonoGame/Content/Sounds/newtargetenemy.ogg` (new)
- `tests/Allegiance.ClientCore.Tests/ClientWorldNewContactCueTests.cs` (new)
- `.verify_p373/harness/` (new live harness + log)

A pre-existing unstaged edit to `src/Allegiance.Bot/Bot.cs` was already in the
working tree before this task; it is unrelated and was left out of this commit.
