# P362 — Weapon/systems status HUD list (ammo, reload state, per mounted system)

## Why this task exists

Domingo remembered a real native HUD element: a small screen listing
equipped weapon systems, showing when any of them is reloading.
Queued behind **P361** (the sim mechanic — auto-reload-on-empty),
which this task depends on for the reload-in-progress state; also
depends on data this port already has.

## Scope

1. **Confirm what's actually available to display** before building
   anything: this port already decodes per-ship `Loadout`
   (`IReadOnlyList<ExpandedPartData>`, `ShipEntity.cs:77`) — each
   entry has `PartId`, `Amount` (current stock), `MountId` (which
   slot), `FractionMounted`. Combined with P361's new reload-attempt
   state (success/failure/in-progress, whatever shape that task
   produced), you should have everything needed: which weapon is
   mounted where, its current ammo count, and whether it's currently
   in a reload attempt.
2. **Build a small, persistent (or toggle-visible — your call,
   whichever matches this port's existing HUD conventions better,
   check how other compact HUD elements like the gauges handle this)
   list showing each mounted weapon/system** with at minimum: an
   identifying icon or short name, current ammo/amount, and a visible
   "reloading" indicator when applicable (matching native's real
   "Reloading ammo..."/"Ammo depleted." distinction from P361's work —
   read P361's actual report doc, `P361_AUTO_RELOAD_ON_EMPTY.md`,
   once it exists, for exactly what state it exposes).
3. Reuse existing HUD rendering conventions from `HudRenderer.cs`
   (font, layout spacing, the `DrawBar`/`DrawGauge` primitives already
   established and reused by P359) rather than inventing a new visual
   language for this element.

## Explicitly out of scope

- The sim mechanic itself — that's P361, already landed by the time
  this task starts (this task is queued behind it). Read P361's real
  landed state, don't re-derive or second-guess it.
- The target info readout / bearing vector (P359) and persistent
  mini-map (P360) — separate, unrelated HUD tasks from the same
  research arc, don't touch their code.
- Any wire-protocol changes — all data this task needs already exists
  client-side (loadout, P361's new reload state); this is a pure
  rendering task.
- P354-P358 — unrelated, already landed, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: real client with a real multi-weapon
  loadout, fire a weapon to zero ammo, confirm the HUD list correctly
  shows that system as reloading (and shows the transition back to
  available once P361's reload attempt succeeds, or a clear "depleted"
  state if it fails). Confirm multiple distinct mounted systems each
  show correctly and independently (not all systems showing the same
  state).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The weapon/systems status HUD list, committed on `master` (not
   pushed — Domingo pushes). Do not create a side branch.
2. Report doc `P362_WEAPON_STATUS_HUD.md` in the repo root: what was
   built, how it reads P361's reload state, and the live verification
   performed (describe what you actually saw for both the reloading
   and depleted cases).
