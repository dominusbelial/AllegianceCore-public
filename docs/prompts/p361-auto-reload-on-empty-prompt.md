# P361 — Auto-trigger reload when ammo/fuel hits zero (sim mechanic)

## Why this task exists

Domingo remembered a native HUD showing weapon systems' reload status
(scoped separately as P362, queued behind this one). Researching that
led to a real, confirmed, and more precise finding than first assumed:
native does NOT have a standalone "ammo slowly regenerates over time"
system. Instead, **hitting zero ammo/fuel automatically triggers the
SAME reload mechanism the player's manual reload key uses**, rate-
limited by a real debounce timer so it doesn't refire every tick.

**Exact native mechanism** (`~/Development/Allegiance/src/Igc/shipIGC.h`):

- `SetAmmo(short newVal)` (lines 597-621): when ammo transitions from
  >0 to exactly 0 AND the previous reload debounce has elapsed
  (`GetMyLastUpdate() >= m_timeReloadAmmo`), it:
  1. Sets a new debounce deadline:
     `m_timeReloadAmmo = GetMyLastUpdate() + 0.5f /
     GetMyMission()->GetFloatConstant(c_fcidMountRate)` (line 608) —
     this is a rate-limit on how often the auto-reload ATTEMPT can
     fire, not the reload duration itself.
  2. Calls `pigc->Reload(this, NULL, ET_Weapon)` (line 610) — **the
     exact same `IIgcSite::Reload` callback the manual reload key
     (`TK_Reload`, `WinTrek.cpp:9687`, also passing `NULL` for the
     weapon-ammo case) already uses.** This attempts to pull
     replacement ammo from the ship's own cargo hold (spare parts) —
     the same mechanic this port's P192 (`Ship.cs`,
     `TryApplyReloadEntry` and the surrounding cargo-hold code,
     `RELOAD(129)` wire message) already implements for the MANUAL
     case.
  3. If the reload attempt succeeds (a spare was found and used):
     plays a "reloading" notification. If it fails (nothing in cargo
     to reload from): posts "Ammo depleted." instead — a real, correct
     failure case, not a silent no-op.
- `SetFuel(float newVal)` (lines 627-648+, read the rest yourself)
  mirrors this exactly for fuel/afterburner (`ET_Afterburner`
  instead of `ET_Weapon`).

**This port already has the underlying P192 reload logic** (manual
reload via a `RELOAD(129)` wire round-trip) — **what's missing is the
automatic trigger**: nothing in this port currently calls that same
reload logic automatically when a ship's ammo or fuel hits zero.
Confirm this directly (search `Ship.cs` for wherever ammo/fuel get
mutated by weapon fire/afterburner use) before assuming the exact
shape of the fix.

## Scope

1. **Read the rest of native's `SetFuel`** (`shipIGC.h:627` onward —
   this prompt's excerpt is incomplete) to confirm it's a faithful
   mirror of `SetAmmo`'s logic before porting it.
2. **Find where this port currently mutates a ship's ammo/fuel**
   (weapon-fire deduction, afterburner fuel consumption — search
   `Ship.cs` for wherever `Ammo`/`Fuel` fields change) and add the
   same zero-transition + debounce-gated auto-reload-attempt logic,
   reusing the port's EXISTING P192 reload/cargo-hold mechanism rather
   than building a new one. If the existing P192 code is structured
   as a request/response wire round-trip (client asks, server
   decides+replies) rather than a synchronous server-side function
   call, adapt this task's trigger to fit that shape faithfully rather
   than forcing a synchronous call that doesn't match this port's
   existing architecture — investigate first, don't assume.
3. **Real debounce timing**: port the same `0.5 / c_fcidMountRate`
   rate-limit (check `DataConstantsIGC`-style access to this float
   constant in this port, following the pattern already established
   for other native float constants elsewhere in this codebase).
4. **Real success/failure distinction**: when the auto-reload attempt
   succeeds vs. fails (nothing left in cargo), the client should be
   able to tell the difference (matching native's real "Reloading
   ammo..." vs. "Ammo depleted." notification text) — at minimum,
   make sure whatever state P362 (the HUD task) will need to display
   this distinction is actually available; you don't need to build the
   HUD display itself, that's P362's job.

## Explicitly out of scope

- The HUD display of reload/weapon status — that's P362, queued
  behind this task. This task is sim-only.
- Any change to the MANUAL reload path (`TK_Reload`'s equivalent,
  P192's existing wire-triggered reload) — this task only adds the
  automatic zero-triggered case, the manual path must keep working
  unchanged.
- P354-P360 — unrelated, already landed or in flight, don't touch.
- The "replenish when docked" mechanic Domingo asked about separately
  — investigated this session and concluded that's very likely just
  the existing `BUY_LOADOUT` re-purchase flow (already fully
  supported), not a distinct mechanic. Don't build anything new for
  docked replenishment in this task.

## Verification — real proof, not just build/test

- **Live proof, required**: real client/bot fires a weapon down to
  zero ammo (or drains fuel via afterburner to zero), confirm an
  automatic reload attempt actually fires (real cargo-hold spare
  consumed, ammo/fuel actually returns to a usable state) without any
  manual reload input, respecting the real debounce timing (don't
  spam-attempt every tick). Also confirm the FAILURE case: a ship with
  empty cargo (no spares) at zero ammo does NOT crash and correctly
  stays at zero (matching native's "Ammo depleted" case).
- Confirm the existing MANUAL reload path (P192) still works
  unchanged — this task adds a new trigger, it must not break the old
  one.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The auto-reload-on-empty mechanic, committed on `master` (not
   pushed — Domingo pushes). Do not create a side branch.
2. Report doc `P361_AUTO_RELOAD_ON_EMPTY.md` in the repo root: the
   real native mechanism confirmed (cite exact lines), how it was
   wired into this port's existing P192 reload architecture, and the
   live success/failure verification performed.
