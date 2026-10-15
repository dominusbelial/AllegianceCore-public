# P394 — the loadout shop never pre-selects your currently-equipped non-weapon parts, silently dropping them on relaunch

## Why this task exists

Live-tested this session, right after confirming shield mounting/
absorption/regen all work correctly (P87/P389). Domingo bought and
equipped a shield, undocked, flew fine — then docked again, and without
changing anything, pressed L to relaunch. The shield was gone. Confirmed
live: the shield was already missing from the shop's part list on that
second dock, *before* relaunching — it wasn't that L failed to send it,
it's that the shop never knew the ship still had one.

## Confirmed root cause

`ShopModel.cs:39-58`:
```csharp
private readonly List<ExpandedPartData> _chosenParts = new();

/// <summary>The ship's currently-mounted parts (the BUY_LOADOUT_ACK tail),
/// used only to display "current ship"; not modified by the shop.</summary>
public IReadOnlyList<ExpandedPartData> CurrentParts { get; }
```
`_chosenParts` — the list actually sent on `L` (`SendBuyLoadout(...,
parts: shop.ChosenParts)`, `Game1.Panes.cs:162,183`) — starts **empty**
on every single new `ShopModel` (built fresh on every dock,
`Game1.Panes.cs:56-88`, "Enter the hangar once per dock: build a fresh
shop model from the current ship + catalogue"). `CurrentParts` genuinely
carries the ship's real current loadout (from `mySe.Loadout`, itself fed
by the real `BUY_LOADOUT_ACK` wire tail) — but its own doc comment admits
it's display-only and never used to seed `_chosenParts`. So relaunching
without manually re-toggling every part you already have silently drops
all of them. Weapons are unaffected only because `MountCombatWeapon`
(`Server.ShipCommands.cs`) auto-arms every fixed hardpoint unconditionally
on spawn, a completely separate mechanism from the shop's chosen-parts
list — shields, cloaks, afterburners, and cargo packs have no equivalent
safety net and are the ones actually affected.

## Scope

1. Seed `_chosenParts` from `currentParts` in the `ShopModel` constructor
   (`ShopModel.cs:124-131`) — the shop should open with your currently-
   equipped non-weapon parts already toggled in, matching what a player
   would expect from a loadout screen ("here's what you have; change what
   you want"), not an empty slate every time.
2. Confirm this doesn't fight the existing toggle logic — a part that's
   already in `_chosenParts` from seeding should toggle OFF on the first
   press (existing remove-if-present behavior, `ShopModel.cs:225-228`),
   not double-add or get stuck.
3. Confirm the hull-swap interaction (P335, `EffectiveHullId`/
   `ChosenPartsThatDontFitEffectiveHull`) still behaves correctly when
   parts are pre-seeded rather than freshly chosen — a pre-seeded part
   that doesn't fit a *different* chosen hull should still be flagged/
   dropped exactly as P335 already handles for a manually-chosen
   incompatible part, not treated specially just because it was seeded.
4. Double check whether weapons should ALSO be included in the seeded set
   (for consistency/future-proofing) even though `MountCombatWeapon`
   currently covers them unconditionally — investigate and decide; don't
   silently assume either way.

## Explicitly out of scope

- `MountCombatWeapon`'s own auto-arm mechanism — unrelated, don't touch.
- Any server-side change — this is confirmed to be entirely a client-side
  (`ShopModel`) gap; the server already correctly reports the real current
  loadout via `BUY_LOADOUT_ACK`/`Loadout`, it's just never consumed to seed
  the editable chosen-parts buffer.
- P368-P393 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: buy and equip a shield, undock, confirm it
  works, dock again, and — without touching anything — press L. Confirm
  the shield is STILL equipped after relaunch (fraction resumes wherever
  it was, or re-arms to 1.0 per the existing mount behavior — confirm
  which, and that it's the correct one per `MountPart`'s existing
  contract). Repeat toggling a part OFF on a dock where it was pre-seeded
  in, confirming it correctly drops on relaunch when explicitly deselected.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The fix, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P394_SHOP_PRESEED_LOADOUT.md` in the repo root: the fix,
   the weapon-seeding judgment call from point 4, and the live
   verification performed.
