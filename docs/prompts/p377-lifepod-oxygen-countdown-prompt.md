# P377 — display the lifepod's real oxygen countdown to the player

## Why this task exists

Domingo asked directly: "can we display the count down inside the
pod?" — after confirming this port's real, already-ported (P279)
5-minute lifepod oxygen mechanic. Investigated and confirmed native
has a real, self-only UI data source for exactly this.

**Confirmed real native mechanic**:
- `Igc/shipIGC.h:2472-2489`, `IshipIGC::GetEndurance()`: for a lifepod
  hull, returns `(m_timeToDie - GetLastUpdate()) /
  GetFloatConstant(c_fcidLifepodEndurance)`, clamped to `[0, 1]` — a
  real remaining-fraction value. Returns `1.0f` for any non-lifepod
  hull (i.e. "full"/not applicable).
- `WinTrek/consoledata.cpp:533-546`, `ModelData::GetEndurance()`: the
  client-side accessor, with the real dev comment *"Andon: Returns the
  time left in a lifepod. Modified to only work with self."* — gated
  to `pmodel == trekClient.GetShip()` (only ever shown for the local
  player's own ship, never a remote one).
- Not found: a direct hardcoded call site wiring this into a specific
  named HUD gauge widget in the `WinTrek/*.cpp` files searched — likely
  bound via an older reflection/property-name UI layout mechanism not
  grep-able the same way. **Read more of `consoledata.cpp`/
  `consoledata.h` yourself** (search for how other `ModelData::GetXxx`
  accessors — e.g. fuel, energy — are referenced/bound to on-screen
  gauges) to identify the real gauge slot this value was shown in
  before assuming a placement.

**Port status, confirmed**: this port's real oxygen mechanic (P279,
`Ship.cs:205-227`, `LifepodTimeToDie`) already computes everything
needed server-side, but nothing currently sends the remaining-fraction
value to the client, and nothing displays it.

## Scope

1. **Finish tracing which real HUD gauge slot native showed this in**
   (scope item above) before deciding where to put it in this port —
   check whether it's plausible native repurposed an existing gauge
   (e.g. the FUEL ring, since a coasting lifepod has no real fuel
   concept) rather than a dedicated new widget. Match native's real
   placement if you can determine it with reasonable confidence;
   otherwise choose a sensible, clearly-labeled placement and say so
   explicitly in the report rather than presenting a guess as
   confirmed.
2. **Server**: compute the same real 0..1 endurance fraction
   server-side (mirroring `GetEndurance()`'s exact formula, reusing
   `LifepodTimeToDie`/the mission's real `LifepodEndurance` constant —
   don't hardcode 300s, read the real constant the same way P279
   already does) and send it to the OWNING peer only (matching
   native's self-only scope) via whichever existing per-tick own-ship
   update channel makes sense (check whether an existing fraction
   field — e.g. `FuelFraction` on the ship's own status — can
   faithfully carry this while in a lifepod, since a lifepod has no
   real fuel to report anyway, before adding new wire protocol).
3. **Client**: render the real remaining time/fraction on the HUD only
   while the local player's own ship is currently a lifepod hull
   (`HullType.HasCapability(HabmLifepod)`-equivalent client-side
   check) — a real countdown (e.g. "4:32" style, or a depleting bar,
   matching whatever gauge slot scope item 1 lands on) that reflects
   the true server-computed deadline, not a client-side guess.
4. Never show this for a remote/other player's lifepod (matches
   native's confirmed self-only gate).

## Explicitly out of scope

- Redesigning the oxygen mechanic itself (P279's timer/deadline logic
  is already correct and untouched) — this task only surfaces the
  existing real value to the UI.
- P376 (target bracket color) — separate, don't touch.
- P368-P375 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required**: real client, eject into a lifepod, confirm
  a real, correctly-counting-down oxygen indicator appears on YOUR OWN
  HUD, ticking down from the real ~5-minute deadline, and disappears
  (or reads full/inactive) once rescued/docked (matching P279's
  existing "docking cancels the timer" behavior — confirm the displayed
  value reflects that correctly too, not just the countdown itself).
  Confirm a SECOND player watching that lifepod does NOT see this
  countdown on their own HUD for someone else's pod.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The lifepod oxygen countdown display, committed on `master` (not
   pushed — Domingo pushes). Do not create a side branch.
2. Report doc `P377_LIFEPOD_OXYGEN_COUNTDOWN.md` in the repo root: what
   real native gauge placement (if determined) or chosen placement was
   used and why, the wire mechanism chosen, and the live verification
   performed (including the docking-cancels-timer and self-only
   checks).
