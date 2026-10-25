# P814 — Bot turret gunners (bombers/gunships fly with defending gunners)

Branch `pi/p814` (worktree `allegiance-hermes-p814`). Base at dispatch: **`c190523f`**
(P811) — the brief cited 2026-10-24 master `6ef6d9bf`; the dispatcher created the
worktree from the later tip, so **every line cite below was re-grepped against this
tree** (2026-10-24). No Core/Server rule change was needed — the native boarding +
turret-gunner machinery (P108/P109/P464, merged under **P803**) was already in
place and correct; P814 supplies the missing **bot consumption** (loadout arming,
boarding, gunner AI) in `tools/allegiance-mcp/**` and the skirmish toggle.

Depends on **P803** (turret gunner client/HUD): reused as-is —
`ShipEntity.BoardTurretId` (State/ShipEntity.cs:275), the server relay of
`ACTIVE_TURRET_UPDATE(99)`/`INACTIVE_TURRET_UPDATE(100)`
(Server.ShipCommands.cs:228-272), and the human gunner cadence
(ClientCore.Game/Game1.TurretGunner.cs) as the model for the bot cadence.

---

## Why (restated)

Domingo (2026-10-24): *"bombers with defense will give the showcase a different
tone"*. Previously bot bombers/gunships flew with empty, silent turrets:
- the loadout selector only filled FIXED hardpoints, so turret mounts were never
  armed (a gunner can't even sit in an unequipped slot — native `BoardShip`);
- no bot ever boarded, and native turret weapons fire only with a gunner holding
  fire (`Igc/weaponIGC.h:236-254`; there is **no native auto-turret**).

---

## Scope items, with native rule, port before/after, data path, tests

### 1. Loadouts — turret mounts armed
- **Native rule:** a turret seat is only boardable when its slot is **equipped**
  with a mounted part (`FedSrv.CPP:2377-2392`, the `ppl != NULL` check inside
  `BoardShip`); the free-seat arithmetic counts turret mounts
  `MaxWeapons − MaxFixedWeapons` (the client's own count,
  `WinTrek/teleportpane.cpp:520-531`).
- **Before:** `TechLoadoutSelector.cs` iterated only `Hardpoints[m].Fixed`
  (SelectVaried:104, Select:258, SelectBomber:468). Turret hulls bought by bots
  had no weapons on `[MaxFixedWeapons, MaxWeapons)`.
- **After:** new `TechLoadoutSelector.ArmTurretMounts` (TechLoadoutSelector.cs:381)
  arms every turret mount with a legal weapon (`CanMountPart` per mount, tech/price
  legality unchanged — same `legal` pool and shared energy/ammo/budget as the fixed
  loop, ranked by anti-fighter `WeaponScore` since a turret's job is defending the
  carrier). Wired into **Select** (:274), **SelectVaried** (:138) and
  **SelectBomber** (:570, drawn from leftover energy/ammo/budget so the station
  volley is untouched). P796 variety/coverage intact — turret guns rotate the same
  least-used legality gates.
- **Data path to the client:** the armed turret parts ride the carrier's
  `BUY_LOADOUT_ACK(5)` (own world) and the same-side `LOADOUT_CHANGE(96)` broadcast
  → `ShipEntity.Loadout` (State/ClientWorld.cs:3988) — the gunner reads its crewed
  weapon's ballistics from there (Ballistics resolver in
  `BotManager.TurretGunner.cs`).
- **Tests (fail on master):** `P814GunnerTests.…` all **new** — on master they
  cannot even compile (`ArmTurretMounts` does not exist). Direct: every turret
  mount gets a fitting weapon, pool respected, fixed mounts untouched, fixed-only
  hull unchanged. Integration: `SelectVaried` across civs/docks arms turrets on
  every turret-capable Heavy chosen and stays `Legal`.

### 2. Boarding — a gunner boards a teammate's turret hull
- **Native rule:** `BoardShip` (`FedSrv.CPP:2322-2475`) — boarder and carrier must
  be docked (`pship->GetCluster()==NULL` + at a station, :2342-2345), carrier is a
  player ship (:2340), the slot search finds an unoccupied **equipped** turret
  (:2347-2402), boarding clears the boarder's parts and seats them
  (`SetParentShip`/`SetTurretID` :2445-2446); refusal sends `BOARD_NACK(178)` to
  the requester alone (:6412-6416); a gunner whose carrier dies ejects with it
  (native eject unboards, `appmsg.cpp:183-187`).
- **Before:** server boarding (P108/P109) worked for humans; bots never boarded.
- **After:** `BotManager.TurretGunner.cs TrySeekBoard` — a docked, eligible
  (fighter/interceptor, side above `MinFreePilots`) bot finds a same-side docked
  turret carrier at its station with a free equipped seat (`FreeSeats = capacity −
  TURRET-state children`, the same definition as ClientWorld.Apply(BoardNack),
  State/ClientWorld.cs:4514-4526) and calls `cs.SendBoardShip(carrier)` (the real
  `FM_C_BOARD_SHIP(103)`). On `BOARD_NACK` (`World.BoardFailureRevision` bump,
  :4474) or a bounded timeout it falls back to launching its own ship (item 2 /
  4a: "the gunner falls back to flying").
- **Data path to the client (existing native field, no new message):** board
  success reflects through the per-tick `SHIP_STATUS(90)` refresh → the gunner's
  own `ShipEntity.ParentId` + `BoardTurretId` (State/ShipEntity.cs:207,275). The
  bot detects boarding from its own `ParentId >= 0 && BoardTurretId != 0xFF`.

### 3. Gunner AI — aim + fire through the native gunner path
- **Native rule:** turret weapons fire only with a gunner present **and**
  `weaponsMaskIGC` set (`Igc/weaponIGC.h:236-254`); the carrier's turret weapon is
  selected per-gunner and the server keeps firing while ACTIVE holds. The client
  sends `ACTIVE_TURRET_UPDATE(99)` (aiming/firing) / `INACTIVE_TURRET_UPDATE(100)`
  (clears) at `c_dsecUpdateClient` = 0.15 s (`clintlib.cpp:1870-1905`); the server
  catch-up-integrates the gunner's facing via `ExecuteTurretMove`
  (`shipIGC.cpp:3349-3392`; slew rates `c_minRate`/`c_maxRate` :1640-1641).
- **Before:** bots had no way to drive a turret (no sender past the test harness).
- **After:** `BotManager.TurretGunner.cs DriveTurretGunner` — while boarded, the
  bot (a) picks the nearest enemy threatening the carrier, **fighters first, then
  an inbound hostile missile** (native projectiles collide with and destroy
  missiles, so this is legitimate — `Sim/Missile.cs` collision); (b) leads with the
  shared Core `ShipDodgeLaw.SolveForImpact` (the same solver native's wingman
  `solveForLead` uses, `shipIGC.cpp:2463-2468` / `Ship.SolveForLead`); (c) slews
  the locally-tracked turret orientation at the native slew rates via
  `Orientation.TurnTo(dir, MaxSlewRate·dt)`; (d) emits
  `cs.SendActiveTurret(serverMs, ClientActiveTurretUpdate.From(...))` when the
  fire gate (angle ≤ FireAngleRad **and** in weapon range) holds, else
  `cs.SendInactiveTurret()`. `serverMs` comes from a per-bot
  `OwnShipServerClock` fed from `ConnectionSession.TakePingEchoes()` so ACTIVE
  stays inside the server's ±1.5 s replay window (:3353-3354). The bot is a
  passenger while boarded — it sends no flight `SHIP_UPDATE` (its `ParentId` gate
  owns the tick).
- **Classification:** native behaviour; port of the client gunner loop to a bot
  driver. Modern approach: a shared "gunner controller" both the MonoGame human
  and the MCP bot call (currently duplicated skew logic — noted, not blocking).
- **Data path (existing native message):** `FM_C_ACTIVE_TURRET_UPDATE(99)` — the
  exact message human gunners use; no server rule change.
- **Tests:** `P814GunnerTests.TryLead_returns_intercept…`,
  `SlewToward_converges_…`, `FireGate_…` — pure math, new on master.

### 4. Who gunners (policy) + 4a standing rules
- **Native rule:** crewing is seat-based (any player may occupy an empty equipped
  slot); there is no native "who should be a gunner" — that is bot policy.
- **After:** `GunnerPolicy.cs` — data-driven, race-agnostic tunings (no civ/hull/
  station names): `CanHost` (Bomber/Heavy), `CanServeAsGunner`
  (Fighter/Interceptor — never miners/constructors/economy, a strike's own bomber,
  a scout on recon), `ShouldSeekBoard` gated on the side keeping `MinFreePilots`
  free ("never starve the economy roles"), `RecruitLimit` bounded by free seats and
  `MaxGunnersPerCarrier`. Finale massing (P801) counts a manned bomber as one
  bomber — gunners are passengers, not extra bombers.
- **4a standing rules (Domingo):**
  - *Every turreted ship waits before launching:* `ManageCarrierGather` — a carrier
    buys its turret hull with `fLaunch:false` (seated docked), holds the launch for
    the bounded gather window (`GatherSeconds`, default 18 s), cut short when all
    seats fill or the pilot is needed urgently (base defence / finale go time),
    then undocks via `SendDocked()` (`FM_C_DOCKED(48)`, Messages.h:402-404 /
    Server.Docking.cs:2127 — the real undock announce).
  - *Docked bots prefer to board:* `TrySeekBoard` (above). Boarding is preferred,
    not forced — NACK/timeout falls back to launching normally.
  - *A human pilot is never held:* 4b below; bots board him while docked, he
    launches when he wants (nothing in the bot path touches a human's ship).

### 4b — skirmish + the human player
- Same MCP bots drive the skirmish (`src/Allegiance.SinglePlayer`,
  memory `project_bot_offense_key_for_single_player`), so gunners work there
  identically. Teammate bots board the HUMAN player's turret hull while the player
  is docked (`TrySeekBoard` treats the human carrier as any same-side docked
  carrier — native `pfsShipBoard->IsPlayer()`, FedSrv.CPP:2340, holds for a
  player-owned ship), fire as the player flies, and never block the player's launch
  (the gather-wait only applies to **bot** carriers; a human-owned carrier is never
  held). The player can refuse/eject gunners through the native controls — the
  native leave path `BOARD_SHIP(sidParent=NA)` (`FedSrv.CPP:6393-6401`) and the
  turret HUD (P803) are unchanged. The native PIG chat lines
  ("I'll be on your turret…" / "I need turret gunners NOW!", Pigs/Piglets/include/
  common.js:271-272) are **not yet ported** as spoken bot lines (see Unfinished).

### 4c — on by default + one off-switch
- `GunnerPolicy.Enable` defaults **true** (native behaviour, not a flagged
  experiment); `ALLEGIANCE_GUNNERS=off|0` turns the whole feature off (also
  `ALLEGIANCE_GUNNER_GATHER_SECONDS`, `…_MAX_PER_CARRIER`, `…_MIN_FREE_PILOTS`).
- Skirmish: `SkirmishOptions.BotGunners` (default true), a setup-screen row
  "Bot gunners: On/Off" (`SkirmishScreenModel`), persisted in the `player.json`
  skirmish block via `SkirmishPrefs`, and propagated to the bot driver as
  `ALLEGIANCE_GUNNERS` (`SkirmishHost.StartAsync`). The showcase/MCP consumes the
  same env toggle — one switch, default ON.

### 5. Telemetry (bounded `[gunner]` lines)
- `[gunner] board ok/nack`, `[gunner] carrier … holding launch …`,
  `[gunner] carrier … launching with N gunner(s)`, and fire (rate-limited every 15
  shots). Counters on the bot: `GunnerBoardings/Nacks/Shots/Hits/Kills`. Hits/kills
  are best-effort (the credited shooter on the wire is the carrier, so exact
  per-gunner attribution lives in the carrier's own `SampleCombatStats`; the gunner
  counts target hull-fraction drops / deaths it causes).

### 6. Coverage map
- Added `W13 | combat | Turret gunners (bots)…` to
  `~/allegiance-scratch/coverage/MECHANISM_COVERAGE.md` (also noted in this report).
  The old `M10` row's "bot turret-aim controller" roadmap item is now covered.

---

## Parity rows (per native behaviour touched)

| # | Native rule (cite) | Port status | Before → After | Data path | Test |
|---|---|---|---|---|---|
| P1 | `BoardShip` slot-equipped gate (`FedSrv.CPP:2377-2392`) | done | turret mounts unarmed → armed (`ArmTurretMounts`) | carrier `LOADOUT_CHANGE(96)` → `ShipEntity.Loadout` | `P814GunnerTests.…` |
| P2 | Free-turret-seat count = `MaxWeapons−MaxFixedWeapons` (teleportpane.cpp:520-531) | done | bots never counted → `GunnerPolicy.TurretCapacity/FreeSeats` | client world children `State == c_ssTurret` | `Turret_capacity_and_free_seats` |
| P3 | `BoardShip` docked-precondition + seat search (`FedSrv.CPP:2342-2402`) | done (P108/P109) | bots never board → `TrySeekBoard` sends real `FM_C_BOARD_SHIP(103)` | `SHIP_STATUS(90)` → `ShipEntity.ParentId`/`BoardTurretId` | loopback (see Unfinished) |
| P4 | `BOARD_NACK(178)` (`FedSrv.CPP:6412-6416`) | done (P638) | fallback now wired → NACK/timeout → launch own ship | `World.BoardFailureRevision` (ClientWorld.cs:4474) | loopback |
| P5 | Turret weapon selection = gunner + `weaponsMaskIGC` (`weaponIGC.h:236-254`) | done (P109) | no bot fire → `DriveTurretGunner` sends `ACTIVE/INACTIVE_TURRET_UPDATE` | `FM_C_ACTIVE_TURRET_UPDATE(99)` | loopback (fail-on-master live test, below) |
| P6 | `ExecuteTurretMove` slew (`shipIGC.cpp:1618-1702`) + ±1.5 s window (:3353-3354) | done (P464) | no bot timestamps → per-bot `OwnShipServerClock` from PING echoes | `FM_C_ACTIVE_TURRET_UPDATE.time` | `SlewToward_converges_…` |
| P7 | `solveForLead` intercept (`shipIGC.cpp:2463-2468`) | done (Core `SolveForImpact`) | bots lead with shared Core solver | in-MCP `GunnerPolicy.TryLead` | `TryLead_returns_intercept_…` |
| P8 | Undock announce `FM_C_DOCKED(48)` (Messages.h:402-404) | done (P69/P71) | carrier never held → `ManageCarrierGather` + `SendDocked()` | `SET_CLUSTER(7)` on launch | loopback |

Message-wiring parity rows (`docs/parity/fedmsg-wiring.csv`: ACTIVE_TURRET_UPDATE 99,
INACTIVE_TURRET_UPDATE 100, BOARD_SHIP 103, BOARD_NACK 178) were already `done`
under P464/P638/P108/P109 — P814 adds no wire message, so no CSV row changes.

---

## Gate / verification

Ran in this worktree:
- **Whole-solution build 0/0** (post-change `dotnet build AllegianceCore.slnx
  -c Debug`: Build succeeded, 0 warnings, 0 errors).
- **`tools/build-tools.sh`**: "all 73 tool projects built" (0/0). (Proof tools are
  outside the solution, as ci.yml.)
- **New tests 3× alone**: `dotnet test … --filter P814GunnerTests` → **11/11 pass,
  3/3 runs** (pass, pass, pass). They are NOT Tier-Live (all < 10 s) — none tagged
  Live.
- **Pre-existing row-index tests adjusted**: adding the P814 "Bot gunners" setup
  row (index 9) shifted the skirmish screen's Start/Back rows down one, so the
  two existing tests that assert `StartRow`/`BackRow` (SP6GameSpeedTests
  `Game_speed_row_…`, P799LivesLimitTests `Skirmish_lives_row_…`) were updated
  9/10 → 10/11. Both still pass (13/13 with P814).
- **One Fast-tier suite**: `tools/run-tests.sh --tier fast` → **PASS — 10194
  tests, 290 s, 0 failures** (Lobby 24, MonoGame 3303, ClientCore.Tests 1759 —
  incl. the 11 P814 tests + the 2 adjusted row-index tests, Core.Tests 5093;
  quarantine 12). The first run of the day hit the two row-index failures above
  (fixed), the re-run is fully green.
- **AOT publish**: NOT applicable — `src/Allegiance.Server` / `src/Allegiance.Core`
  unchanged (only `tools/allegiance-mcp`, `src/Allegiance.SinglePlayer`,
  `tests/…` touched).

Deliberately **not run here** (pi-lane rule: live-on-rig / 200v200 / skirmish-screenshot
verification is Claude's mini runner):
- **Fail-on-master loopback test** (the scoped live proof): two bots dock, one
  boards the other's bomber, the carrier launches, the gunner fires at an enemy
  fighter and hits. **It fails on master** because master has no bot boarding /
  no bot turret fire sender. Verify on a loopback once per the recipe.
- **Skirmish-sized human-in-a-bomber run** (auto-proof client, Classic + Modern
  real-window screenshots), **whole-match unattended with fog** (economy →
  research → strike/finale → game over → next match).
- **Showcase never-parks 5×** (bots/flight/sim affected) — compare against the
  base rate for load flakes.
- **Rig recipe for Claude**: the 200v200 march on 221, two full matches —
  `[gunner]` lines, bomber survival to target, finale bombers assembled, garrison
  kills, kills/min + tick health vs the previous batch.

---

## Files changed (working tree, NOT committed)

- `tools/allegiance-mcp/TechLoadoutSelector.cs` — `ArmTurretMounts` + wiring into
  Select / SelectVaried / SelectBomber.
- `tools/allegiance-mcp/GunnerPolicy.cs` — NEW pure policy + aim/lead/slew/fire math
  + tunings + `ALLEGIANCE_GUNNERS` toggle.
- `tools/allegiance-mcp/BotManager.TurretGunner.cs` — NEW integration (board,
  gather, drive, telemetry).
- `tools/allegiance-mcp/BotManager.cs` — `TryGunnerFlow` hook in `DriveCombat`;
  ActiveBot gunner state fields.
- `tools/allegiance-mcp/BotManager.Lifecycle.cs` — gunner state reset on new life.
- `src/Allegiance.SinglePlayer/SkirmishOptions.cs`, `SkirmishScreenModel.cs`,
  `SkirmishHost.cs` — `BotGunners` toggle (default ON) + setup-screen row + env.
- `tests/Allegiance.ClientCore.Tests/P814GunnerTests.cs` — NEW 11 tests.
- Coverage: `~/allegiance-scratch/coverage/MECHANISM_COVERAGE.md` — W13 row.

## Unfinished / follow-ups
- **Human-player skirmish proof** (4b) and **loopback boarding→fire proof** — need
  Claude's live runner (recipe above).
- The native **PIG spoken lines** ("I'll be on your turret…"/"I need turret gunners
  NOW!", common.js:271-272) are not emitted by the bots yet — the port has no
  carrier-needs-gunners chat today; suggested as a small follow-up (native chat
  lines already reach the client via CHATMESSAGE).
- Gunner **hit/kill telemetry is best-effort** (carrier is the wire shooter);
  exact per-gunner attribution could ride the existing hit stream if wanted later.
- Consolidate the duplicate turret-slew math (human `TurretLook.Integrate` vs the
  bot's `GunnerPolicy` wrapper around `Orientation.TurnTo`) into one shared
  controller — flagged as a modern better approach, not blocking.

---

# P814b — bot gunners actually happen (turret bombers, a gunship slot, live proof)

Same worktree/branch (`hermes/p814`, rebased on P803 = public master `27d95413`;
HEAD has Claude's review commit `9bcdf4dc`). This section is P814b.

## Why (Claude's live test, 2026-10-24)
`P814GunnerLiveTests.Showcase_bots_board_turret_hulls_and_the_server_fires_their_turrets`
ran a real local showcase (size_scale 1) and counted **server** turret rounds
(`Weapon.TurretRoundsFired`). After 15 minutes: **0 boardings, 0 turret rounds** —
the P814 boarding/gunner machinery existed but no bot ever flew a turret hull:
- `TechLoadoutSelector.SelectBomber` ranked the cloak-priority Stealth Bomber (0 turret
  seats) as the best strike hull, so showcase and finale bombers both flew Stealth Bombers;
- the showcase role mix had no Heavy/gunship slot (`Showcase.cs:138-160`).

## Scope items addressed

### 1. Visible-element bombers fly turret bombers (data-driven, race-agnostic)
- **Native basis:** a gunner can only sit an EQUIPPED turret seat (`FedSrv.CPP:2377-2392`);
  seats = `MaxWeapons - MaxFixedWeapons` (teleportpane.cpp:520-531). A turret-capable
  strike hull (Bomber / Hvy Bomber ...) is a carrier a gunner can board.
- **After:** `TechLoadoutSelector` gained `StrikeHullPref` (`Best` | `TurretCarrier`)
  and a tunable `TurretStrikeMargin` (default **0.25** = 25 %, env
  `ALLEGIANCE_TURRET_STRIKE_MARGIN`). With `TurretCarrier`, `SelectBomber` picks the
  best TURRET-capable strike hull whose `Volley.Score(target)` is within the margin of
  the best strike hull; otherwise it falls back to the best hull (the fallback when no
  turret bomber exists / qualifies). **Lone wolves (P801) keep the cloak hull**: the
  finale split (`PrepareFinaleRelaunch`) selects `Best` for the first `MaxLoneWolves`
  bombers (they get the cloak Stealth Bomber -> `AssignFinaleLoneWolf` marks them wolves)
  and `TurretCarrier` for every visible bomber.
- **Wired:** initial showcase/fleet bomber spawns (`SeatCombatClient` ->
  `PickTechLoadout(preferTurretBomber: role==Bomber)`) and non-finale relaunches
  (`RepickRelaunchLoadout`) request `TurretCarrier`; `SelectFinaleLoadout` threads the
  pref. Race-agnostic, data-driven over the real core (no civ/hull names).
- **Measured (new unit test, real PCore001, civs 25/3/35 x every dock x every
  class-0 garrison):** `TurretCarrier` turned a fixed-only Stealth Bomber into a turret
  carrier in **714/714** combinations; it never regressed a Best hull that was already a
  turret carrier and never picked a fixed-only hull when Best had a turret carrier.

### 2. A gunship slot in the showcase role mix
- `ComposeShowcaseFleetCount` now includes a **Heavy** slot (weight 0.05) alongside
  Fighter .46 / Interceptor .18 / Scout .18 / Bomber .13 (sum 1.00). The Heavy
  representative (Gunship / Assault Ship - turret-capable) is spawned when the core can
  buy one, giving the showcase a turret carrier independent of the bombers. Covered by
  `P814b_showcase_mix_adds_a_gunship_slot_when_a_heavy_exists`.

### 3. Dock gather + fighter boarding actually overlap (scope 3)
- The first live run showed carriers holding their launch but **0/2 gunners boarding**
  - the 18 s gather window rarely overlapped a docked fighter's instant relaunch.
- **After:** `GunnerPolicy` gather window raised to **28 s** (env
  `ALLEGIANCE_GUNNER_GATHER_SECONDS`), and a docked gunner-eligible fighter that sees a
  same-side docked turret carrier at its station **parks** (defers its own launch) up to
  `BoardParkSeconds` (**12 s**, env `ALLEGIANCE_GUNNER_BOARD_PARK_SECONDS`) instead of
  insta-launching past the carrier's gather, so the gather and the board reliably
  overlap. Bounded; falls back to launching normally.

### 4. Gunners fire and hit (live proof)
See the live test numbers in the verification section below: boardings > 0 and server
`TurretRoundsFired` >= 5 (breaks early when met; cap 900 s).

### 5. P801 interplay
- The volley requirement still counts each bomber's REAL loadout: `StrikeVolley.FromParts`
  admits only mounts that damage BOTH the station's shield and armor
  (`StrikeVolley.Compute`'s `Damages` gate, StrikeVolley.cs:150-154) - a turret's
  anti-fighter gun counts only if it genuinely damages the station ("unless they do").
- Finale massing unchanged: `P801StrikeMassTests` green (pure StrikeVolley/ResearchTactics),
  lone wolves keep their own lane and cloak, and the visible element assembles on the
  shared go time.

## Files changed (P814b, working tree)
- `tools/allegiance-mcp/TechLoadoutSelector.cs` - `StrikeHullPref`, `TurretStrikeMargin`,
  turret-carrier ranking in `SelectBomber`, threaded through `Select`/`SelectAtLocal`.
- `tools/allegiance-mcp/BotManager.TechLoadouts.cs` - `PickTechLoadout(preferTurretBomber)`.
- `tools/allegiance-mcp/BotManager.cs` - `SeatCombatClient(preferTurretBomber)` (initial
  bomber spawn), `ActiveBot.GunnerParkUntil`.
- `tools/allegiance-mcp/BotManager.Finale.cs` - `SelectFinaleLoadout(preferTurretCarrier)`,
  finale wolf/visible loadout split in `PrepareFinaleRelaunch`.
- `tools/allegiance-mcp/GunnerPolicy.cs` - gather 28 s, new `BoardParkSeconds` (12 s).
- `tools/allegiance-mcp/BotManager.TurretGunner.cs` - gunner **park** so the gather/board
  overlap; station ids in telemetry.
- `tools/allegiance-mcp/BotManager.Lifecycle.cs` - reset `GunnerParkUntil` on a new life.
- `tools/allegiance-mcp/Showcase.cs` - Heavy/gunship slot in the role mix.
- `tests/Allegiance.ClientCore.Tests/P814GunnerTests.cs` - 2 new P814b tests (hull
  preference over the real corpus; gunship slot in the mix).
- `tests/Allegiance.ClientCore.Tests/P656cFinaleLiveTests.cs` - updated the reflection
  `SelectFinaleLoadout` call to the new 4-arg signature (intent unchanged).

## Verification (P814b)
- **Live loopback test PASSES** (this worktree): `P814GunnerLiveTests.Showcase_bots_board_turret_hulls_and_the_server_fires_their_turrets`
  → **passed in ~80 s** (breaks early when boardings > 0 AND server `TurretRoundsFired` >= 5):
  - boarded gunners (peak) = **3**; server turret rounds = **5**; docked peak = 5, distinct docked ships = 31.
  - roles: Fighter 12 / Interceptor 6 / Scout 2 / Bomber 4 / **Heavy 2** / support 4.
  - ships flying turret hulls: 5 — **Hvy Bomber ×3** (the visible bombers), **Cruiser + Attack Carrier** (the gunship/Heavy slot).
  - gunners boarded into real turret mounts (mount 2/3/1) and fired on-target (remaining 0.0 deg).
- **P814 unit + P801**: 25/25 (P814GunnerTests 13 + P801StrikeMassTests 12). P801 finale massing unchanged.
- Whole-solution build 0/0; `tools/build-tools.sh` all 73 tool projects 0/0.

### Root cause found + fixed (P814b scope 3, live-only)
A docked ship is flagged `IsNotVisible` (its `IsGone` bit is set when it departs the
sector to dock — native caches departed ships in the null cluster,
`clintlib/appmsg.cpp:475-488`). `TrySeekBoard`'s candidate search excluded any
`IsNotVisible` ship, so a held, docked turret carrier was never a valid board target —
the gather ran, but no fighter could ever board (0 boardings in the first live runs).
Fixed in `BotManager.TurretGunner.cs`: a SAME-STATION docked carrier qualifies when it is
not `IsDead` (the `IsGone`/docked state is a live host, not a corpse). Only a genuinely
dead ship is excluded.

### Consolidated gate numbers (P814b, all run in this worktree)
- **Live loopback** `P814GunnerLiveTests.Showcase_bots_board_turret_hulls_and_the_server_fires_their_turrets`:
  **PASS in ~80-85 s** — boarded gunners (peak) 3-4, server `TurretRoundsFired` = 5 (≥ 5 threshold),
  distinct docked ships 31, turret hulls flying (Hvy Bomber ×3 + Cruiser + Attack Carrier).
  Evidence: `~/allegiance-evidence/p814b/P814B_live_pass.log`.
- **P814 unit** (incl. 2 new P814b tests) + **P801** massing: 25/25.
- **P745 / P798**: 24/24.
- **P656cFinaleLiveTests**: 4/4 (updated its reflection `SelectFinaleLoadout` call to the new
  4-arg signature — the test's intent, Best → Stealth Bomber hull 23, is unchanged).
- Whole-solution build **0/0**; `tools/build-tools.sh` all **73** tool projects **0/0**.
- **One Fast-tier suite** (`tools/run-tests.sh --tier fast`): **PASS — 10210 tests, 294 s**.
  (The FIRST fast run failed 4× P656c — my added `preferTurretCarrier` param broke its reflection
  call; fixed, all green on the re-run. It also flaked 1× `P784ComposeTests.Fanout` (order-fanout
  timing under 5-agent contention); that test passes 1/1 alone — a load flake, not a change of mine.)
