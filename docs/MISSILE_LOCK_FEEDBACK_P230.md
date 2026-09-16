# P230 — Missile-lock feedback: tone, HUD text, target-box indicator

Date: 2026-09-16. Stage P230 of the staged AllegianceCore port, landed on `master`
directly (not pushed — Domingo pushes).

This stage wires the three real lock-feedback channels into the MonoGame client:
the continuous lock-acquired tone, the partial/full/lost console tips, and the
target-box overlay that changes appearance across the three lock states. The lock
**value** itself is P226's `Ship.MissileLock` (server-computed per tick); P226
explicitly deferred all HUD feedback as a documented follow-up — this is that
follow-up.

---

## 1. The architectural question: how does the client see the lock? (A vs B)

The client is a **pure renderer of server-authoritative state**. P226 builds
`Ship.MissileLock` on the **server** each tick (`UpdateMountedMissileLock` →
`UpdateMissileLock`, `src/Allegiance.Core/Sim/Ship.cs:2478,2405`), and the MonoGame
client has **no local IGC simulation** — so it has no way to know the lock today
(no `MissileLock` field on any wire codec, confirmed by grep).

Two options were weighed.

### Option B (compute it client-side) — REJECTED, genuinely infeasible

Native's client ran its own local magazine simulation, which is *why* native's
`WinTrekClient::HasMissileLock()` (`trekigc.cpp:876-883`) can just read
`magazine->GetLock()` with no wire message. Faithfully mirroring that in this port
would mean porting `Ship.UpdateMissileLock`'s exact formula
(`Ship.cs:2405-2461`) to run client-side, which needs the missile type's static
data — `Lifespan/InitialSpeed/Acceleration/MaxLock/LockTime/LockAngle` — and the
*target's* hull `Ecm` (`magazineigc.cpp:165-167`).

**Verified: the client has none of that.** `MissionInfo` (`Allegiance.ClientCore/State/MissionInfo.cs`)
carries only the static-core *filename* and *version*; the ClientCore library
references only `Allegiance.Core`'s **wire codecs** (its csproj has a single
ProjectReference to `../Allegiance.Core/Allegiance.Core.csproj`); a repo-wide grep
for `StaticCore`/`MissionMap`/core-loading in the client projects returns nothing.
The client sees only decoded wire records — it never holds a `DataMissileTypeIGC`
instance. Option B therefore cannot work and is rejected on that ground, not on
preference.

### Option A (expose over the wire) — CHOSEN, as a new owner-only message

The brief framed (A) primarily as widening the native-faithful
`ServerShipUpdate` (the 47-byte port of native's real `ServerSingleShipUpdate`,
`igc.h:2444`, embedded in both SET_CLUSTER and SINGLE_SHIP_UPDATE). Widening that
struct would silently diverge a byte-for-byte port of a *real native struct*, so I
did **not** touch it.

Instead the lock rides a **brand-new, clearly-labelled this-port-only extension
message**, `MISSILE_LOCK_UPDATE(204)`:

- **Additive**: FMID 204 sits above native's entire real FMID range (max 200,
  `src/Inc/Messages.h`), so it can never collide with a native message this port
  might later port faithfully. Nothing else's byte layout changes.
- **Owner-only**: the server sends it **only to the owning player** — each peer
  receives its own ship's lock — following the existing per-owner convention where
  a peer's own-ship `SINGLE_SHIP_UPDATE` already goes only to that peer
  (`BroadcastShipStates`, `Server.cs`). So the per-player fractions in that struct
  are *already* owner-scoped; this message keeps that privacy rather than leaking
  the player's lock state to observers.
- **Self-contained**: it carries everything the feedback needs and the client
  lacks — the lock fraction (div-240 byte), the target's ship id **and** world
  position (so the client can draw the box even though it never renders enemy
  ships — they arrive only via `HEAVY_SHIPS_UPDATE`, which `ClientWorld` does not
  consume), and the missile type's display name (for the "acquired" tip; the
  client has no static core to look the name up from).

Layout (`src/Allegiance.Core/Wire/MissileLockUpdate.cs`, 35 bytes): `shipID`,
`targetShipID` (NA=-1 when no target), `lockPercent` (div-240), reserved, a 12-byte
`Vector3` target position, and a 13-byte ASCII missile name — documented as a port
extension, not disguised as native-faithful.

The server sends it every tick from `BroadcastMissileLock()` (`Server.cs`, hooked
into the Step-3 broadcast pass beside `BroadcastShipsUpdate`), gated to ships that
actually carry a mounted missile launcher with ammo — the same predicate P226 uses,
so armed ships (the interesting case) stream and unarmed ships produce no traffic.

---

## 2. What was (and wasn't) found for the native target-box visual

The brief asked for a fresh source check before assuming the in-3D-view bracket is
unfindable. Findings:

- **Radar blip bracket, lock-aware — FOUND** (`WinTrek/radarimage.cpp:325-353`):
  the radar expands a bracket around the target's blip as the lock builds —
  `DrawExpandedBlip(pcontext, radiusBracket, m_psurfaceLocked, …)` at full lock,
  `m_psurfacePartialLock` at partial, and `radiusBracket + 40.0f * (1.0f - lock)`
  scaling for the partial case. `AWF_FLIGHT_TARGETED_ICON = "targetedbmp"`
  (`artwork.h:70`) feeds the radar's targeted blip. This is the only lock-aware
  "square around the target" in the checkout, and it lives in the **radar**.
- **3D-view indicators — FOUND but not a lock box** (`WinTrek/indicator.cpp`):
  `Render` (`:762`) draws lead/turn/direction indicators in the 3D view
  (`RenderLeadIndicator` `:179`, `RenderTurnIndicator(pcontext, pmodelTarget, Color)`
  `:411`, with yellow/green/red accepted/current target tints `:787-791`). These are
  turn/lead arrows, not an axis-aligned lock box.

The specific "animated squares around the locked ship" Domingo remembers from
playing natively is **not in this checkout** — as the brief suspected, it lives in
a missing UI-script/surface asset (the same "not in this checkout" category as the
default keybinding table, per the P220 audit). Rather than block on the exact
native reference, the visual is built from the **confirmed behavior** the brief
blessed: partial vs full look different, and it animates. Concretely, the port's
box is tied to the *server's* lock state delivered over the new wire message:

- **No lock** (target present, lock 0): dim steel-grey, static.
- **Partial**: amber, gently pulsing scale.
- **Full**: hot red, stronger pulse + a slow acquisition *spin* (the port's reading
  of "animated").

It also honours the radar's partial/full distinction principle (a lock-quality
gradient), just expressed in the 3D-view box rather than the radar.

---

## 3. Implementation summary

- **Wire**: `MissileLockUpdate(204)` codec + `MissileLockUpdateWireTests` (6).
- **Server**: `BroadcastMissileLock()` — per-tick owner-only send; skips unarmed
  ships.
- **ClientCore (domain)**: `ClientWorld.MissileLock` snapshot
  (`MissileLockState`), applied from `MISSILE_LOCK_UPDATE(204)` (new
  `ConnectionSession` receive case); the pure edge-triggered tip rules live in
  `MissileLockFeedback` (port of `console.cpp:929-946`) and fire the three tips
  into the existing chat log as bare system lines (`SidSender = NA`, so the renderer
  shows no player prefix). `MissileLockFeedbackTests` (12).
- **MonoGame audio**: `SoundManager` gains a looping `Sounds/missilelock`
  instance (`missilelock.ogg` — native `missileToneSound = ImportWave("missilelock")`,
  `sounddef.mdl:365`, sound id `missileTone`, `sounds.h:135`; the file was copied
  from Artwork into client Content + registered in `Content.mgcb`), played via
  `EnsurePlaying` only while the client world reports **full** lock and `Stop`ped
  otherwise — native's exact `PlaySoundIf(…, bIsPlayersShip && HasMissileLock())`
  gate (`trekigc.cpp:1288-1290`, `HasMissileLock() == (GetLock() == 1.0f)`,
  `trekigc.cpp:876-883`). Deliberately distinct from the incoming-missile alarm
  (`missilelockonme`, out of scope).
- **MonoGame visual**: `LockBracketStyle` (pure per-state tint/pulse/spin) +
  `LockBracketView` (the per-frame descriptor), drawn by `HudRenderer.DrawLockBracket`
  — four outward-facing corner L-arms around the projected centre, rotated thin
  rects reusing the existing 1x1 white pixel (no new asset). `Game1.ComputeLockBracket`
  projects the wire target position through the active camera (same camera-relative
  projection as the P212 target-pick) and passes the resulting descriptor to
  `DrawHud`. `LockBracketStyleTests` (4).

Text reuse: the tips ride P214's chat-log (`ClientWorld.Events.Chats` →
`ChatRenderer`), the established text mechanism — no separate tip-text widget exists
to reuse.

---

## 4. Live proof

A throwaway off-repo harness (real in-process server + real `ConnectionSession`
against `PCore001.igc`, armed with a real lock-requiring missile launcher + an enemy
command target) captured the three states over the real wire and rendered the real
renderers to offscreen `RenderTarget2D`s (the P207/P218/P228 capture technique).

Real server-side wire log (owner-only `MISSILE_LOCK_UPDATE(204)`):

```
[missilelock] peer 1 ship 8096 target 8603 lock 0.899 "mis06" -> MISSILE_LOCK_UPDATE(204)
[missilelock] peer 1 ship 8096 target 8603 lock 0.949 "mis06" -> MISSILE_LOCK_UPDATE(204)
[missilelock] peer 1 ship 8096 target 8603 lock 0.999 "mis06" -> MISSILE_LOCK_UPDATE(204)
[missilelock] peer 1 ship 8096 target 8603 lock 0.000 "mis06" -> MISSILE_LOCK_UPDATE(204)   ← target dragged out of range
```

Real lock values observed in the client world (partial → full → lost):

```
[P230] wire: partial lock=0.050  target=8603
[P230] wire: full    lock=1.000  target=8603  name='mis06'
[P230] wire: lost    lock=0.000  target=8603
[P230] tips at full:  [Partial missile lock - center the target on screen to improve the lock. |
                       Missile lock acquired - press Left Ctrl to fire a mis06 missile at the target.]
[P230] tips at lost:  [Partial missile lock ... | Missile lock acquired ... | Missile lock lost.]
```

Screenshots (offscreen `RenderTarget2D`, vision-checked; `.verify_p230/`):
- `lock_none.png` — four grey corner L-brackets forming an axis-aligned square (no-lock).
- `lock_partial.png` — the same four corners in **amber**, axis-aligned.
- `lock_full.png` — **hot red** corners, rotated off-axis (the acquisition spin).
- `lock_tips.png` — the chat-log panel showing all three tip lines (width-clipped at
  the right edge by `ChatRenderer`'s standard long-line truncation).

Sound probe against the real `SoundManager` + real audio device:

```
[P230] SOUND: at FULL lock  tone.State=Playing (expect Playing)
[P230] SOUND: lock released tone.State=Stopped  (expect Stopped)
```

All three requirements confirmed: the three text messages appear at the right
transitions (partial → acquired → lost, exactly once each, no per-frame spam), the
tone starts at full lock and stops on release, and the target box changes appearance
across the three states.

---

## 5. Build & test status

- Whole-solution build: **0 warnings, 0 errors** (including the new
  `missilelock.ogg` content-pipeline entry).
- `Allegiance.Core.Tests`: **3050 total** (+6 `MissileLockUpdateWireTests`);
  full-suite twice, both runs 3049 passed / 1 failed — the sole failure both times
  is the pre-existing, documented `LiveTurretGunnerBindingTests` machine-state flake
  (unrelated to this stage).
- `Allegiance.ClientCore.Tests`: **68 total, 0 failed** (+12 `MissileLockFeedbackTests`,
  +1 `MissileLockLiveTests` live round-trip).
- `Allegiance.ClientCore.MonoGame.Tests`: **159 total, 0 failed** (+4
  `LockBracketStyleTests`).

Must-fail checks: the transition classifiers red when the edge rule is removed
(covered by the `MissileLockFeedback` unit tests), and the live round-trip proves the
full server→client wire path (`MISSILE_LOCK_UPDATE(204)` → `ClientWorld.MissileLock` →
tips) against a real transport — a passing codec unit test alone is not the proof.

---

## Scope boundary

- The incoming-missile **warning** tone (`missilelockonme` / `missileLockSound`,
  sounddef.mdl:364) is a separate mechanic — not implemented (tracked in the
  client backlog).
- Missile-type selection UI — out of scope, as in every prior stage.
- Native reference source (`~/Development/Allegiance`) untouched (read-only).
- P226's fire/lock logic untouched except read-as-reference; the only wire addition
  is the clearly-scoped, this-port-only `MISSILE_LOCK_UPDATE(204)` owner-only message.
