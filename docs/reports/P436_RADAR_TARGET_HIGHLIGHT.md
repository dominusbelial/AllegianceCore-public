# P436 — highlight the currently-selected target on the radar

Date: 2026-09-29
Branch: master (committed, not pushed — Domingo pushes)
Prereqs: P415 (U/Z filtered-target selection) and P250/P372 (the local radar).

## Problem

Domingo, live-testing missiles: *"pressing u not doing anything if the target is
not on my view i dont get a direction or a cue to follow."* End-to-end
investigation (in the brief) confirmed **U already works** — P415's
`SelectFilteredTargetForKey` logs `[monogame] target enemy ship: <name>` on
success and `no enemy ship in sector` on failure — but that feedback is a
**console line only, invisible while playing**. The radar is real and drawing
every frame, yet **every ship contact rendered identically** — a small
side-coloured square (`RadarBracketRenderer.DrawContact`, `Ship` case) — with
**no distinct treatment for the player's own selected target** (`_autoTarget`).
Native has a dedicated target detail tier (`c_rlTarget`, one of
`radarimage.cpp`'s real detail levels), and this port's own `RadarBracketRenderer`
doc comment explicitly deferred it ("no LOD modes… those are named follow-ups").
P436 is that follow-up.

## What was built

### 1. A real, distinct radar highlight for the player's own selected target

`src/Allegiance.ClientCore.MonoGame/Rendering/RadarBracketRenderer.cs`
- `RadarContact` gains an `IsSelectedTarget = false` field (trailing optional
  positional param; existing call sites unchanged).
- `Draw` gains an optional `highlightPulse` (0..1 frame-independent phase,
  computed by the caller from wall time) so the selected-target marker can pulse
  without making the renderer time-aware.
- `DrawContact` now draws the highlight **after** the base icon so it dominates,
  **whether the contact is on-screen or edge-clamped off-screen** — the off-screen
  case is the exact reported gap.
- Visual treatment (unmistakable from every other blip):
  - a large bright-**white** ring that pulses outward (`16 + 4·pulse` px),
  - a second tighter white ring, and
  - a white diamond "lock" frame around the base icon (a rotated-square
    target-reticle shape no contact type's round/square icon mimics).
  - The highlight's alpha is **floored at 0.9**, well above the distance fade, so
    a distant or edge-clamped target stays visible when its base blip has faded.
  - White was chosen because no per-side blip colour (arbitrary rosters) nor the
    teal/gold/violet palette can be mistaken for it; combined with the pulse +
    diamond it cannot alias a station's single ring or a warp's double ring.
- `RadarBracketLayout`'s on-screen/edge-clamp placement and opacity math were
  **not touched** (the brief's explicit boundary).

The selection decision is extracted as a pure, GPU-free helper so it is
unit-testable:
`src/Allegiance.ClientCore.MonoGame/Rendering/RadarTargetSelection.cs`
- `IsSelected(contactType, contactId, selectedType, selectedId)` — a contact is
  the selected target only when **both** the type and id match; a null selection
  (or an Asteroid selection, which has no radar blip) flags nothing.

### 2. Wiring in the per-frame gather loop

`src/Allegiance.ClientCore.MonoGame/Game1.Rendering.cs` (`DrawRadar`)
- Reads the current `_autoTarget`, maps its kind to a `RadarContactType`
  (Ship/Station/Warp; Asteroid → null), and computes a wall-clock pulse.
- Each ship / station / warp contact is created with `IsSelectedTarget` set via
  `RadarTargetSelection.IsSelected` (the station/warp loops now bind their
  dictionary key as the contact id).
- The radar draw passes the pulse through; the "no contacts" early-return is
  restructured so the no-candidate feedback below still draws.

### 3. Visible "no candidate found" feedback

P415's failure case was console-only (`no {noun} in sector`). P436 adds a brief
centred HUD line (`NO <NOUN> IN SECTOR`) using the port's existing HUD-small
font and a single `SpriteBatch.DrawString` — deliberately **not** a new
toast/notification system.
- `Game1.cs`: transient state `_targetNoMatchNoun` / `_targetNoMatchUntil`
  (+ a 2.5 s display window).
- `Game1.Targeting.cs` (`SelectFilteredTargetForKey`): stages the line on "no
  candidate", clears it on success.
- `Game1.Rendering.cs` (`DrawTargetNoMatchFeedback`): draws it centred at 40%
  height, fading out over the final 0.6 s. Drawn even when there are no radar
  contacts.

## Verification — real proof

### Whole build: 0 warnings / 0 errors
Built every source project (`Core`, `Server`, `ClientCore`, `Bot`, `Lobby`,
`ClientCore.MonoGame`) with `/home/domingo/.dotnet/dotnet build` — all
`Build succeeded, 0 Warning(s), 0 Error(s)` (TreatWarningsAsErrors is on).

### Unit test (pure, no graphics device)
`tests/Allegiance.ClientCore.MonoGame.Tests/RadarTargetSelectionTests.cs` —
the brief's isolation requirement: given a contact list + a selected target id,
the right one is flagged and nothing else:
- matching kind+id selected (ship/station/warp);
- same type, different id → not selected;
- same id, different type → not selected (a station/warp with the same id number
  as the selected ship must not be flagged);
- null selection / null id / null type → flags nothing;
- across a mixed list, exactly the selected id is flagged and the others are not.

### LIVE proof (required)
`tests/Allegiance.ClientCore.MonoGame.Tests/RadarTargetHighlightLiveTests.cs` —
real in-process server (m2xspiderw, 2 sides), a **real peer client** on side 0 in
the Ganymede sector, and a **real side-1 enemy NPC** placed in that same sector
**directly behind the peer's facing** (at +Z; the peer spawns facing −Z), i.e.
genuinely off-screen — the reported problem case. Over the real wire:
- the peer receives the enemy's real identity (HasRealIdentity via the cross-side
  export);
- the enemy's real perceived position is confirmed **behind** the peer's real
  forward (off-screen ⇒ the radar draws it edge-clamped):
  `dot(forward, toEnemy) < −500` — the enemy is 1500 units behind a −Z-facing peer,
  giving comfortable margin;
- the **real U-key path** (`GatherSectorCandidates` + `FilterCandidates(Enemy)`,
  the exact code U invokes) selects **that** real enemy;
- that exact enemy contact is the flagged highlight
  (`RadarTargetSelection.IsSelected(Ship, enemyId, Ship, pickedId)`), and no other
  ship in the sector is flagged.
Ran 3× — passes each time (stable, not a flake).

### Full test suite
Run the mono-relevant suites twice; only the pre-existing documented live-wire
timing flakes appeared, and they vary run-to-run (counts differ between identical
runs), confirming they are the known flakes, unrelated to this change (this change
touches only `Allegiance.ClientCore.MonoGame`; none of the flaky tests are in that
assembly's logic):
- `Allegiance.ClientCore.MonoGame.Tests`: **864 passed, 0 failed** (both runs).
- `Allegiance.Core.Tests` / `Allegiance.ClientCore.Tests`: live-network timing
  flakes only (e.g. `AutoReloadOnEmpty`, `LiveTurretGunnerBinding`,
  `ThreePeerSeededSession`, `JoinFlowLiveTests.EntityTable_TracksARealTickingServer`,
  `CrossSideCombatVisibilityLiveTests`). Spot-checked one flake
  (`CargoBayReloadTests.SwapPart_negative_mount…`) in isolation — **passes**,
  confirming load/timing flakiness rather than a regression.

### Honest scope of the "live" claim
Following the port's established live-proof standard (P415's live test drives the
production gather/filter over a real server + real clients — no GUI pixel capture),
P436's live proof verifies the **wire + targeting + flagging layer end to end**:
the off-screen enemy the radar will edge-clamp is exactly the contact marked for
the distinct highlight. The rendering itself (`RadarBracketRenderer`'s white pulsing
ring + diamond) is drawn by the same `SpriteBatch` primitives the rest of the radar
already uses. No automated GUI pixel capture was performed in this (headless)
session; the visual is described precisely above and reproduced by the pure +
live tests.

## Out of scope (unchanged, per the brief)
- `RadarBracketLayout` placement/opacity math — untouched.
- Other radar LOD tiers (`c_rlAll`/`c_rlDefault`/`c_rlMinimal`/`c_rlNone`) — not
  implemented; this is specifically the target highlight.
- P415's target-selection logic itself (`SelectFilteredTargetForKey`) — unchanged
  apart from staging/clearing the on-screen feedback line.
- The friendly-target box colour confusion Domingo flagged tonight (a friendly
  ship's target box reading non-blue) is a separate cosmetic item in
  `TargetSelectionStyle` — **not root-caused or changed here**; the target-selection
  bracket tests (friendly-blue / enemy-red) still pass, so if the confusion is real
  it is elsewhere in that rendering path and deserves its own small task.

## Files changed
- `src/Allegiance.ClientCore.MonoGame/Rendering/RadarBracketRenderer.cs` — selected-target highlight + `RadarContact.IsSelectedTarget`.
- `src/Allegiance.ClientCore.MonoGame/Rendering/RadarTargetSelection.cs` — new pure selection helper.
- `src/Allegiance.ClientCore.MonoGame/Game1.Rendering.cs` — wire the highlight + no-match feedback draw.
- `src/Allegiance.ClientCore.MonoGame/Game1.Targeting.cs` — stage/clear the no-match feedback.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — transient no-match feedback state.
- `tests/…/RadarTargetSelectionTests.cs` — new pure unit tests.
- `tests/…/RadarTargetHighlightLiveTests.cs` — new live proof.
