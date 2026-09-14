# P205 — vizfight `showcase`: a scripted trailer scenario

## Summary

`tools/vizfight` gains a third named mode, `showcase`, that choreographs a
multi-phase trailer sequence through the **real** in-process server and **real**
`Allegiance.Bot` peers: diplomacy (a real alliance), ready-up + launch countdown,
formation flight, a skirmish with a **real kill**, a real flag capture
(pickup + secure-by-docking), a real economy transaction (funded by the side's
payday, quoted with the real `MONEY_CHANGE`), a real leadership (mutiny) vote,
and a dock/resupply beat. It is a new entry point (`showcase`) dispatched the
same way `explore` already is; `Main()` and `ExploreMain()` are untouched and
still run clean (verified below).

Every beat uses an existing, already-shipped Bot/Server API. **No server-side
gameplay was added or changed.** The only non-`Main`/`ExploreMain` addition is
the showcase entry point plus its helpers in `Program.cs`, and one new
three-team `ShowcaseConfig()`.

## How it's staged

- Map: **`m2xspiderw.igc`** (the same map `ExploreMain` boots; the static
  geometry P204 already renders). The whole arc plays out in side 0's home
  sector **Ganymede (1050)**, which hosts side 0's Garrison station (4328) at
  `(1120, 1560, 0)` — discovered at runtime off the real mission, not hardcoded.
- Sides: a three-team config **Alpha / Bravo / Gamma**. Two teams can never form
  a real alliance — the server's `UpdateAlliances` only builds alliance groups
  when **≥ 3 active sides** exist (Server.cs:5453) — so a three-team config is
  required for the diplomacy beat to actually succeed. Alpha and Bravo ally;
  the skirmish is Alpha **vs Gamma** so the friend/foe gate still allows a real
  kill.
- Six real Bot peers: A1, A2, CV (Alpha/side 0 — A1 is the leader+owner), B1
  (Bravo/side 1, allied wingman), G1, G2 (Gamma/side 2, the picket). Side
  membership + leadership/ownership are granted through the **real** join flow
  (`server.TryJoinSide`), not a spawner shortcut.
- Combat/flying is kept **> 200 u from the station** (ships auto-dock within
  `Sector.DockRadius`); only the carrier's final secure leg enters the radius.

## The scripted arc (real timings from a live run; 20 Hz sim)

| # | Beat | Mechanism (Bot API) | Observed sim time |
|---|------|---------------------|-------------------|
| 0 | Boot + seat 6 pilots on 3 sides; A1 becomes owner | `server.TryJoinSide` (P183) | — |
| 1 | Diplomatic alliance (Alpha ↔ Bravo) | `SendChangeAlliance` (P190) | t16.3 |
| 2 | Ready-up + launch countdown | `SendPlayerReady` ×6 → P180 auto-start countdown → `Stage=Started` | t16.4 → t31.4 |
| 3 | Launch with real loadouts | `SendBuyLoadout(hullId, fLaunch:true, parts)` | t31.4 |
| 4 | Formation flight | movement `CommandControls` (SteerToward law) | t33.4 |
| 5 | Skirmish — real kill | AllWeapons fire at a Gamma NPC drone | t37.6 (`type:"kill"`) |
| 6 | Flag capture (pickup + secure-by-dock) | physical Treasure pickup (P201) + dock-secure (P201 `OnShipDocked`) | t40.7 / t50.8 |
| 7 | Dock / resupply | carrier docked + payday wait | t50.9 |
| 8 | Economy — real donation | `SendPlayerDonate` (P203) after payday funds the wallet | t76.6 |
| 9 | Leadership vote | `#mutiny` chat → ballot (P187) → `SendVote` | t76.9 |
| 10 | Finale | fleet holds watch | t77.0 |

## Flag-treasure seeding (real values used)

Placed in sector **1050 'Ganymede'** (`layout.Arena`, side 0's home) at
**`FlagPos = station + (−720, 0, −300) = (400, 1560, −300)`**, seeded exactly per
the established P201 `PlaceFlagTreasure` shape (adapted to the showcase's real
discovered sector/side ids):

- `ObjectId = 8420`, `ClusterId = 1050`, `TreasureId = 2` (**Gamma's** flag —
  a side-0 carrier may pick up an enemy side's flag), `Amount = 0`,
  `TreasureCode = Flag`, `Lifespan = 9999`, `CreateNow = true`, `V0 = [0,0,0]`.

The side-0 carrier (CV, rescue-capable Scout hull) flies to the flag, picks it
up by **physical proximity** (the flag appears in the `models` array as
`kind:"treasure"`, then disappears on pickup), then returns to within the
station's `DockRadius`, auto-docks, and the real `OnShipDocked` secure path
fires — `GAIN_FLAG(NA)`, `side0.Flags = 1`.

## Sample real `event` text per narrated beat (from the live capture)

- Alliance (real `CHANGE_ALLIANCES` group): `Diplomacy: the commander allies
  Bravo to Alpha — real CHANGE_ALLIANCES group {0,0,-1} (a pact holds; Gamma
  stands apart).`
- Kill (real `KILL_SHIP`): `Combat: A2 downs #8102 — real KILL_SHIP observed.`
- Flag pickup (real `GAIN_FLAG`): `Flag seized in flight — ship 8098 now
  carries side 2's colours (real GAIN_FLAG pickup).`
- Secure (real `GAIN_FLAG NA` + counter): `Carrier docks at the garrison and
  secures the flag — Alpha now holds 1 flag(s) (real secure-on-dock,
  GAIN_FLAG NA).`
- Payday (real treasury delta): `Galactic payday — Alpha treasury climbs to
  675 credits (real income disbursed to pilot wallets).`
- Economy (real `MONEY_CHANGE`): `Finance: pilot donates 205 credits to ship
  8096 — real MONEY_CHANGE dMoney=205 (sidTo=8096).`
- Vote (real leadership transfer): `Leadership vote passes — command of side 0
  transfers to ship 8097 (real mutiny ballot, SET_TEAM_LEADER).`

## Findings baked into the design (real behaviour, not guesses)

1. **Alliances need ≥ 3 active teams** (`UpdateAlliances`, Server.cs:5453) —
   hence the three-team config; two sides can never ally.
2. **Player-ship deaths eject, not KILL_SHIP.** Destroyed player ships emit
   `EJECT(77)` (no `KILL_SHIP`), so a real `type:"kill"` frame needs an NPC
   victim. The showcase spawns one ownerless **Gamma miner drone** via the
   existing `Server.SpawnNpcShip` for Alpha to shoot down — that produces the
   real `KILL_SHIP(105)`.
3. **Wallets start at 0**; income arrives via the automatic payday (~every 60 s,
   `IncomePerPayday × civ income` → `side.Money`, then split to wallets). The
   economy beat therefore waits for the payday to fund the donor's wallet, then
   donates an affordable slice — quoting the real `MONEY_CHANGE`.
4. **Ships auto-dock within `DockRadius` (200)** — all combat/formation staging
   is > 200 u from the station; only the carrier's secure leg enters the radius.

## Verification

1. **`dotnet build`** — whole solution: **0 warnings, 0 errors**.
2. **Live end-to-end** (`dotnet run --project tools/vizfight -- showcase 7791`)
   independently verified over a raw WebSocket (no library — `wscap.py`, the
   stdlib RFC6455 client in this directory):
   - **Real kill**: a `{"type":"kill","victim":8102,"killer":8097,...}` frame
     from a real `KILL_SHIP` (NPC drone 8102 destroyed by A2).
   - **Ships move and fight**: 7 live ship ids streamed per tick; 97 `models`
     frames of `kind:"projectile"` (real weapon fire); ship 8096 translated
     hundreds of units over the run.
   - **Treasure appears then disappears**: a `models` entry with
     `kind:"treasure", id:8420` appears at t37.75 and is gone by t40.65 — the
     real pickup consumed it.
   - **Every non-visual beat narrated with real data**: alliance group, KILL_SHIP
     kill, GAIN_FLAG pickup/secure, payday delta, real `MONEY_CHANGE dMoney=205`,
     and the mutiny leadership transfer (all above).
   - Full arc completed on every run; final run's capture: 13 sector msgs,
     2432 tick frames, 29 events, 1 kill.
3. **Regression** — `Main()` (default, port 7801) boots 8 real bots across 4
   lanes and waits for a spectator with no exceptions; `ExploreMain()`
   (explore, port 7802) boots clean and waits for outside peers with no
   exceptions. Both older scenarios are untouched.
