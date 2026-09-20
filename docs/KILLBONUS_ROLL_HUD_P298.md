# Kill Bonus + Roll HUD gauges — P298

Two new cockpit HUD readouts in the MonoGame client, surfacing state that
native FreeAllegiance only hints at via training overlays. This stage gives the
pilot real, live-value instruments for both.

## Native citation (authoritative, verified in the reference tree)

Native does NOT have persistent live gauges for either — both exist only as
`TrainingOverlay` callouts in `src/WinTrek/indicator.cpp`, the same file P241
cited for the real cockpit gauge layout:

- **KillBonus** — `KillBonusTrainingOverlay` (indicator.cpp:625-631): a red
  training arrow from `centre + (−15,−160.5)` pointing up-left, labeled
  "KILL BONUS" — i.e. native marks the KB element as worth a HUD callout but
  does not continuously render its number. The NUMBER native does show lives on
  the console command `showexperience` (WinTrek/console.cpp:826), which prints
  `100·(GetExperienceMultiplier()−1)` — exactly the port's P278
  `KillBonusPercent` (`50·c/(c+4)`, cap 50). Native replicates the streak to
  the client as `fExperience` inside `FM_S_PLAYERINFO(10)`
  (appmsg.cpp:2280 `pship->SetExperience(...)`), and the client's local ship
  replica reads it back via `GetExperienceMultiplier()`
  (shipIGC.h:1812-1817,2610).
- **Roll** — `RollTrainingOverlay` (indicator.cpp:634-641): purely tutorial text
  — "PUSH Q AND E TO ROLL LEFT AND RIGHT" / "PUSH V TO TOGGLE MOUSE CONTROL" at
  `centre + (−110,−170)`. Native has no persistent roll instrument; its joystick
  indicator (indicator.cpp:154-177) is pitch/yaw only. The port's Roll gauge is
  therefore a genuinely new live readout of an attitude axis native never
  instruments persistently.

## Real data source for each

- **KillBonus** — the REAL server-side P278 value, streamed to the renderer-only
  client via a new owner-only per-tick message `OwnShipKillBonusUpdate(212)`
  (FMID 212, above native's max and the port's 204-211 extensions). The server
  sends each seated peer its own ship's `Ship.KillBonusPercent` + streak every
  tick (`Server.BroadcastOwnShipKillBonus`, alongside the P255 mining stream).
  This mirrors native's `PLAYERINFO.fExperience` replication but for a client
  with no static ship replica to read it from (the established P230/P255/P266
  owner-only-extension precedent). The client stores it in
  `ClientWorld.KillBonusPercent`/`KillBonusStreak`; `Game1` lifts it into
  `HudFrame`; `HudRenderer.DrawKillBonus` renders it. No new server mechanic —
  P278 is untouched; this only adds the readout.
- **Roll** — the ship's REAL current roll angle about its forward axis, derived
  client-side from the orientation this client already tracks for the
  chase/cockpit cameras (`ServerShipUpdate.Orientation`, the compact-quaternion
  attitude on the per-tick update). Extracted by the new
  `Orientation.GetRollDegrees()` (Core): the level reference is world-up (+Y)
  projected into the plane ⊥ forward, and the roll is the signed angle between
  it and the actual up, measured about forward via the right vector
  (`atan2(right·levelUp, up·levelUp)`). Verified to recover exactly a
  `Roll(+θ)` applied to a level ship; degenerate (vertical climb) → 0°.
  `Game1` lifts it into `HudFrame.RollDegrees`; `HudRenderer.DrawRoll` renders
  it. Positive = the bank direction native's `Orientation.Roll(+θ)` produces.

## Presentation (this port's, matching the HUD's existing widget language)

Follows the file's established `DrawX` convention (a private method called from
the main `Draw` pass, gated on whether a frame exists). Both draw whenever a
ship frame exists — 0% KB / level 0° are honest readouts, not clutter:

- **KillBonus** (top-left, warm gold = reward): the live `%` numeral, a
  proportional 0..50% bar (fills toward 100% as the bonus nears the +50% cap),
  and the raw streak tally ("N KILLS" / "1 KILL"). Distinct from every other
  widget colour (teal accent / red warning / amber MINING / violet CLOAKED).
- **Roll** (top-right, teal): the signed degrees numeral (`+038deg`; ASCII —
  the HUD font bakes only 32..126, no degree glyph) plus a horizontal
  deflection dial whose marker swings left/right with the bank, centre tick at
  0°.

## Files changed

- `src/Allegiance.Core/Wire/OwnShipKillBonusUpdate.cs` (new) — the 212 codec.
- `src/Allegiance.Server/Server.cs` — per-tick `BroadcastOwnShipKillBonus()`.
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — store + apply 212.
- `src/Allegiance.Bot/Bot.cs` — collect 212 (participating peers receive it).
- `src/Allegiance.Core/Sim/Orientation.cs` — `GetRollDegrees()`.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — feed KB + roll into `HudFrame`.
- `src/Allegiance.ClientCore.MonoGame/Rendering/HudFrame.cs` — +3 fields.
- `src/Allegiance.ClientCore.MonoGame/Rendering/HudRenderer.cs` — `DrawKillBonus`
  + `DrawRoll`, called from the main draw pass.

## Verification

- **Whole-solution build** — 0 warnings, 0 errors (repo enforces warnings-as-errors).
- **New tests** (all passing): `OwnShipKillBonusUpdateTests` (wire round-trip +
  FromShip), `OrientationRollTests` (roll recovery / level / pitched / degenerate),
  `ClientWorldKillBonusTests` (Apply), `KillBonusWireLiveTests` (**live**: a REAL
  kill streak — real `BroadcastKillShip` credit — climbs the value the connected
  client receives on 212: 0% baseline → 10 → 16.67 → 25, per `50·c/(c+4)`).
- **Offscreen HUD captures** — `.verify_p298/` (untracked): the real production
  `Draw` path rendered to offscreen `RenderTarget2D`s. KB sweep
  (`kb_00_none` 0% / `kb_10_onekill` 10% 1 KILL / `kb_17_twokills` 17% /
  `kb_25_fourkills` 25%) shows the gold readout+bar tracking the real streak
  values; roll sweep (`roll_left45` −045 marker left / `roll_level` +000 centre /
  `roll_right38` +038 marker right) shows the dial tracking the real bank; a
  combined frame (`combined_streak4_banked`: 4-kill 25% + 22° bank) shows both
  together. The harness (/tmp/p298hud) is disposable and removed before commit.
- **Full test suite** — run at least twice; only the pre-existing
  `LiveTurretGunnerBindingTests` timing flake is expected.
