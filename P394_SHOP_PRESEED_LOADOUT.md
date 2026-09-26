# P394 — the loadout shop never pre-selected your currently-equipped non-weapon parts, silently dropping them on relaunch

## Problem (confirmed live, this session)

The shop's pending-loadout list `ShopModel._chosenParts` — the exact list sent
when the player presses **L** to relaunch (`SendBuyLoadout(..., parts:
shop.ChosenParts)`, `Game1.Panes.cs:162,183`) — started **empty** on every
single new `ShopModel`. A fresh `ShopModel` is built on every dock
(`Game1.Panes.cs:56-88`, "enter the hangar once per dock"). The ship's real
current loadout (`currentParts` = the `BUY_LOADOUT_ACK` tail on
`ShipEntity.Loadout`) was held in a display-only `CurrentParts` that was never
used to seed `_chosenParts`.

Consequence: relaunching **L** without manually re-toggling every part you
already had sent an empty part list. The server's `ApplyLoadout`
(`Server.ShipCommands.cs:1436-1498`) does `ship.ClearLoadout()` then re-mounts
**only** the requested parts — so every non-weapon part (shields, cloaks,
afterburners, cargo packs) was silently dropped. Weapons were spared only
because `MountCombatWeapon` (`Server.cs:639`) auto-arms every fixed hardpoint
unconditionally at the join-time spawn — a separate mechanism with no analogue
for non-weapon parts.

Live-confirmed on the second dock (before relaunching): the bought-and-equipped
shield was already absent from the shop's part list — the shop never knew the
ship still had one.

## The fix

`ShopModel` now **seeds `_chosenParts` from `currentParts`** in the constructor
(`ShopModel.cs:158-188`). The shop opens with your currently-equipped parts
already toggled in ("here's what you have; change what you want"), and
relaunching **L** without touching anything re-sends them — so they survive.

Details / decisions:

- **Verbatim copy, not re-derivation.** Each seeded entry keeps its real mounted
  `Amount` and `FractionMounted` from the ACK tail. It does **not** re-derive
  `DefaultAmount` (20 for a weapon) or force `FullyMounted` — otherwise a
  partly-used weapon/ammo pool would be topped up to 20 on every relaunch and
  the true mount state erased.
- **Toggle still works on seeded entries.** A part already present in
  `_chosenParts` from seeding hits the existing remove-if-present branch
  (`Activate`, `ShopModel.cs:225-228`) — the first **SPACE** press removes it,
  it does not double-add or get stuck. A pre-seeded part the player explicitly
  deselects therefore drops on relaunch (proven live).
- **Mount ids renumbered.** Seeded entries are renumbered to the shop's
  sequential-list-position convention (`RenumberMounts`, the same invariant
  `Activate` enforces) regardless of the ACK tail's wire mount ids, so the
  pending loadout stays internally consistent.
- **Out of scope respected.** `MountCombatWeapon`'s auto-arm and every
  server-side path are untouched — this is entirely a client-side `ShopModel`
  gap.

## Point 3 (scope): P335 hull-swap fit interaction

`ChosenPartsThatDontFitEffectiveHull` operates uniformly over `_chosenParts`,
so a **pre-seeded** part that has no compatible hardpoint on a *different*
chosen hull is flagged/dropped exactly as P335 already handled a freshly-chosen
incompatible part (proven by unit test
`Pre_seeded_part_that_does_not_fit_chosen_hull_is_predicted_dropped`).

Seeding also surfaced a latent **P335 model/view mismatch** that is now fixed as
part of this task's interaction confirmation. The server's P334 drop policy is
**scoped to the hull-change path only** — a same-hull loadout is free-mounted
and nothing is dropped (`Server.ShipCommands.cs:1516-1527`; and the UI already
gated its "DROPPED on launch" warning on `WillSwapHull`, `ShopLayout.cs:111-123`).
But the model property `ChosenPartsThatDontFitEffectiveHull` computed
`CanMountPart` **unconditionally**, so it could flag a legitimately free-mounted
same-hull part as "will be dropped" even though the server keeps it. Now that the
shop pre-seeds the currently-mounted parts, this false warning would have
appeared on **every dock** for any part mounted via a same-hull buy. Fixed by
gating the property on `WillSwapHull` (return `[]` when no real hull swap is
pending), matching both the server's actual policy and the UI's own gating
(`ShopModel.cs:102-132`). The swapped-hull flagging behaviour is unchanged.

## Point 4 (scope): should weapons be included in the seed? — YES

Judgement call: **weapons ARE seeded**, alongside non-weapon parts.

- **Consistency.** The shop is meant to reflect "here's what you have; change
  what you want." Showing the shield/cloak/afterburner toggled in but hiding the
  guns would be inconsistent and confusing, and the pending loadout is the exact
  set **L** sends.
- **Idempotent with `MountCombatWeapon`.** Including weapons is harmless today:
  `ApplyLoadout` re-mounts exactly what's requested, and `MountCombatWeapon`'s
  auto-arm is orthogonal (it arms the hardpoints regardless). No double-mount,
  no conflict — the server's loadout is a clear+rebuild of the requested list.
- **Future-proofing.** `MountCombatWeapon` is the only thing currently keeping
  weapons armed through a relaunch, and it runs only at the join-time spawn.
  If that unconditional auto-arm is ever narrowed/changed (e.g. per-hardpoint
  limits), having the real weapons in the seed means the relaunch carries them
  through the ordinary `BUY_LOADOUT` grant path instead of silently depending on
  the spawn-side safety net.

## Live verification (real proof)

Reproduced over a **real in-process server + real `ConnectionSession` TCP
wire round-trips**, mirroring the player's manual sequence
(`P394ShopPreSeedLiveTests.cs`), using the real `PCore001.igc` catalogue:

1. **Equip a shield** — `BUY_LOADOUT(hull, fLaunch:false, parts:[real Shield
   part])`; the ACK populates `ShipEntity.Loadout` with the shield.
2. **Reopen the shop the way `Game1.Panes` does on a dock** — a fresh
   `ShopModel` built from the ship's live loadout.
3. **Press L (untouched)** — `BUY_LOADOUT(fLaunch:true, parts: shop.ChosenParts)`
   (the exact call `Game1.Panes.cs:162` makes); the server clears + re-mounts the
   loadout and undocks.

`EquippedShield_UntouchedPreseeded_SurvivesRelaunch_Live` asserts the shield is
**(a)** pre-seeded into `ChosenParts`, **(b)** still mounted in the ship's loadout
after the relaunch. **Pre-fix this test goes red** (empty `ChosenParts` →
shield dropped) — confirmed by the must-fail check (seeding disabled → both live
tests fail; restored → both pass).

`PreSeededPart_ToggledOff_DropsOnRelaunch_Live` asserts a pre-seeded shield the
player explicitly deselects (**SPACE**) drops on relaunch — proving seeding does
not pin a part on.

**Fraction confirmation (as the task requested):** a re-mounted shield does **not**
"resume wherever it was" — `MountPart` re-arms it to fully-up
(`CshieldIGC::Arm`, `shieldIGC.h:99-103`: `m_fraction = 1.0f`), which is the
existing, correct mount contract for a freshly-mounted shield. The request's
`fractionMounted` byte is preserved verbatim through the wire/ACK as bookkeeping;
the *functional* charge re-arms to full. Documented in the live test.

## Whole-solution build & full test suite

- Whole solution (`AllegianceCore.slnx`, Debug): **0 warnings, 0 errors**.
- Full suite run twice; the only failures across every run are the pre-existing
  live-wire timing/environmental flakes:
  - **MonoGame.Tests (the changed project): 770/770 green on both full runs.** ✅
  - Core.Tests / ClientCore.Tests ×2 each: only rotating live-wire flakes; the
    non-documented ones pass in isolation, and the ClientCore pair was
    confirmed failing on the **pristine tree** (`git stash`) — i.e. environmental,
    not this change (neither suite references the `ClientCore.MonoGame` assembly,
    so this change is compilation-isolated from them).
  - Lobby.Tests: 12/12 green.
- New tests: 8 `ShopModelTests` (seeding, verbatim amount/fraction, weapons
  included, sequential renumbering, toggle-off, P335 fit-with-pre-seeded ×3
  incl. the WillSwapHull gate) + 2 `P394ShopPreSeedLiveTests` live proofs.

## Out of scope (untouched)

`MountCombatWeapon`'s auto-arm; all server-side logic (`ApplyLoadout`, `MountPart`,
etc.); P368–P393.
