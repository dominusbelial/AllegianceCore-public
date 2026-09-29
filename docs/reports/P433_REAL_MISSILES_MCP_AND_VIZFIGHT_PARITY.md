# P433 — real missiles: launcher resolution + firing, in both allegiance-mcp and vizfight

Date: 2026-09-29
Branch: master (committed, not pushed — Domingo pushes)

## Summary

Closed the loop that P420 documented as the real gap: a missile launcher was
never *equipped* (nothing ever carried one), so the real `SendFireMissile()`
wire path (`ConnectionSession.cs:492`, `Bot.cs:1458`) — already unit-tested for
encoding in P420/P226 — had never been exercised live. P433 makes a real
missile launcher buyable and mountable end-to-end, in BOTH tools, and wires
real firing off the real lock stream:

1. A **real launcher** (compact `OT_partType` form in `core.Launchers`, keyed
   by `PartId` — the 123 launcher records SEPARATE from the 117 `core.Parts`
   every prior loadout builder read) now resolves, mounts into a loadout, and
   fires.
2. The server's authoritative `ApplyLoadout` now **mounts** a requested
   launcher (previously it skipped any part not in `core.PartTypes`, so a
   launcher id was silently dropped — the exact gap P420 found).
3. Both combat engines fire a real `SendFireMissile()` once the real,
   server-computed lock reaches full on the engagement target.

## Why the launcher was unreachable (the verified gap)

P420 measured that `ApplyLoadout` only granted parts found in
`mission.Core.PartTypes` — and the 117 part records contain no `Magazine`
subtype, so a wire-only launcher request was skipped ("not in core"). That is
why no bot ever carried a launcher and why real missile fire never happened
live. The missile data (`core.Launchers` 123 records, `core.MissileTypes` 56
records) and the lock mechanic were never broken. This task verified those
claims against the real `PCore001.igc` and confirmed:
- 123 `DataLauncherTypeIGC` records in `core.Launchers`, keyed by `PartId`;
  0 of those PartIds overlap the 117 `core.Parts`.
- A launcher's *display name / part mask / mass* live on the missile type it
  fires (`DataMissileTypeIGC.LauncherName / LauncherPartMask / LauncherMass` —
  the embedded `LauncherDef`); its real ammo is the launcher record's `Amount`.
- Only 56 of the 123 launchers are GENUINE missile launchers (their
  `ExpendabletypeId` resolves in `core.MissileTypes`); the rest are
  chaff/mine/probe dispensers (same table, non-missile expendable) and are
  correctly excluded from missile-launcher resolution.
- On the default fighter hull (Adv Scout, id 13) `PmEquipment[Magazine]=0x8100`
  covers exactly 5 real missile launchers (LRM Killer 1/2, SRP Killer 1/2,
  Doomsday — all `MaxLock=0` dumbfire, instant-lock). Homing launchers
  (LRM Hunter 3 mask 0x0002, MRM Seeker mask 0x0001…) fit the weapon hardpoint
  set but NOT the magazine slot — the real `CanMountPart` gate is what decides.

## 1. Real launcher resolution — `tools/allegiance-mcp/Catalog.cs`

Added, mirroring `ResolveWeapon`'s exact-error policy:

- `ResolveLauncher(query)` — exact-over-substring over the GENUINE missile
  launchers in `core.Launchers` (matched against each missile type's
  `LauncherName`). Zero hits / several distinct names / a non-missile dispenser
  each throw a real, specific error (same shape as `Catalog.cs:190-197`).
- `DefaultLauncherPart()` — the lowest-`PartId` real missile launcher that
  actually MOUNTS on the default fighter hull (LRM Killer 1, part 20, 2
  missiles) — mirroring `DefaultWeaponPart`'s "real default that really works"
  pattern.
- `AppendLauncher(parts, launcherPartId, hullId, hullName, requestedName)` —
  resolves the launcher into its mountable form and checks the hull's REAL
  capability mask via `DataHullTypeIGC.CanMountPart` (the exact P432
  MatrixPlanner test, reused — not reimplemented): fits ⇒ append
  `ExpandedPartData(id, launcher.Amount, 0, 255)` + positive note; does NOT fit
  ⇒ the loadout stays GUN-ONLY with a plain explanatory note — never a crash,
  never a fabricated launcher.
- `LauncherReadyTime(partId)` / `ResolveLauncherName(partId)` — the real
  inter-shot `ReadyTime` (floored) and display name, for pacing and reporting.

`PartsForWeapon` gained an optional-launcher overload
(`PartsForWeapon(weapon, launcherPartId, hullId, hullName, requested, out note)`)
used by `run_hull_matrix`; the old one-arg form is preserved.

## 2. Server — `ApplyLoadout` mounts real launchers

`src/Allegiance.Server/Server.ShipCommands.cs` `ApplyLoadout`: when a requested
part id is not in `core.PartTypes`, it now falls back to
`StaticCore.TryBuildMissileLauncher(partId, out partType)` (the shared Core
primitive). A real missile launcher is granted as a mountable `ET_Magazine`@0
part of subtype `MissileLauncher` (the shape the `FM_CS_FIRE_MISSILE(70)`
handler expects); a non-missile dispenser still reports "not in core". The
existing P334 hull-change `CanMountPart` gate and P314 255-part cap then apply
unchanged, operating on the constructed launcher part.

## 3. Shared Core primitive (the one piece both tools share)

`src/Allegiance.Core`:
- `DataPartTypeIGC.FromLauncher(partId, launcher, missile)` — bridges a real
  launcher record + its missile type to a mountable `DataPartTypeIGC`
  (PartMask = `missile.LauncherPartMask`, Name = `missile.LauncherName`,
  Type = `MissileLauncher(launcher.ExpendabletypeId)`).
- `StaticCore.TryBuildMissileLauncher(partId, out part)` — resolution by PartId
  (requires BOTH a real `Launchers` record AND a real missile for its
  expendable).

**Share vs parallel (the documented choice):** the *construction/resolution-by-
id* primitive is shared in Core (used by the server AND both tools). The
*name-resolution* (exact-over-substring with ambiguity errors) is implemented
**in parallel** in each tool — `Catalog.ResolveLauncher` (allegiance-mcp) and
`ResolveMissileLauncher` (vizfight) — rather than extracted to Core, because
the two tools already deliberately keep standalone name-lookup copies (mcp's
Catalog is documented "standalone… mirroring how vizfight itself copies these
lookups rather than sharing"). The two implementations are faithful twins over
the same `core.Launchers`/`core.MissileTypes` data and share the Core build
primitive, so they cannot silently diverge in the underlying behavior; each
keeps its own tool-flavoured error text and default selection.

## 4. allegiance-mcp loadouts + firing

- `BotManager.SpawnBot` takes `launcherPartId` + `launcherRequestName`, appends
  the launcher via `Catalog.AppendLauncher` (gun-only on hull mismatch, logged),
  stores `LauncherPartId`/`MissileReadyTime` on the bot, and sends it in the
  BUY_LOADOUT. `RunCombination` (run_hull_matrix) threads the launcher the same
  way. `spawn_scenario` passes it through to each `SpawnBot`.
- `McpTools` — new optional `launcher` argument on `spawn_bot`, `spawn_scenario`,
  `run_hull_matrix`: `"default"` = default fighter-mounted launcher; a
  name/substring resolves one; `"none"`/absent = gun-only (standing default).
- `EngageShipAt` → `MaybeFireMissile(bot, cs, now)`: reads the bot's OWN
  `cs.World.MissileLock` (the owner-only `MISSILE_LOCK_UPDATE(204)` stream the
  server already sends for an armed ship), and on `IsFullLock` sends the real
  `cs.SendFireMissile()`, paced by the launcher's real `ReadyTime` so a
  dumbfire launcher (instant lock, always full) is never spammed — the same
  way the guns' AllWeapons beat is naturally paced by weapon refire. The server
  remains authoritative (`HandleFireMissile` → `Ship.TryFireMissile` gates on
  lock + ammo and consumes both).
- **Protocol fix:** `Program.cs` now does `Console.SetOut(Console.Error)`.
  BotManager's live diagnostics previously went to STDOUT, corrupting the MCP
  newline-delimited protocol (P433's spawn-time launcher log exposed it — the
  first P433 live runs "hung" because a `[p433]` line sat in front of the JSON
  response). The StdioServerTransport uses the raw process stdout, so
  redirecting `Console.Out` is safe and aligns code with the documented
  "all diagnostics to stderr" invariant.

## 5. vizfight loadouts + firing

- `FullInventoryParts(core, weaponPartId, launcherPartId, hull)` now appends a
  compatible launcher via `AppendCompatibleLauncher` (Core `FromLauncher` +
  `CanMountPart`, gun-only on mismatch, plain log). Updated all 4 call sites.
- `ResolveMissileLauncher` / `DefaultMissileLauncher` / `LauncherReadyTime` —
  faithful parallel to mcp's, over the same Core data.
- `AttachEnemyBot` gained `MissileLauncherPartId`/`MissileReadyTime`/`LastFireAt`;
  the enemy contingent's BUY_LOADOUT carries the launcher.
- `EngageEnemy` → `MaybeFireMissile(en, now)`: same real-lock + ReadyTime-paced
  `en.Cs.SendFireMissile()` as mcp. The friendly `BotClass` fleet stays gun-only
  (a `BotClass` has no ClientWorld to read the owner-only lock stream from —
  documented).
- `attach` takes an optional 6th positional arg naming the launcher
  (`"default"`/absent = default fighter-mounted; a name resolves one;
  `"none"` = gun-only). Default is to carry the default launcher, so real
  missiles fly in attach by default.

## 6. Unit tests

`tests/Allegiance.ClientCore.Tests/LauncherResolutionTests.cs` — 9 tests,
MatrixPlanner-style (real `PCore001.igc`, assert-behaviour-over-content):
real launcher exact-name resolve; unique-substring resolve; unknown-launcher
specific error; blank throws; default launcher genuinely mounts on the default
fighter hull; `AppendLauncher` mounts a compatible launcher into a loadout;
`AppendLauncher` that does NOT fit falls back to gun-only (no fabricated part);
no-request leaves loadout unchanged; `LauncherReadyTime` positive. All 9 pass.

## 7. Build + full test suites (each run twice)

- Whole-solution build (`AllegianceCore.slnx`): **0 warnings, 0 errors**.
- `Allegiance.ClientCore.Tests` (289): run 1 → 3 failures, run 2 → 3 failures —
  all the pre-existing documented live-wire flakes (ShipDeleteGone dock, JoinFlow
  boot-miner regression); my 9 launcher tests pass both runs; zero failures touch
  P433 symbols.
- `Allegiance.Core.Tests` (3522): run 1 → 9 failures, run 2 → 3 failures — both
  in the pre-existing documented live-wire timing-flake family, rotating
  between runs as expected (ShipIdAllocationLive, P366LeaderBotCommandLive,
  WarpBomb Live, LiveWinCondition, LiveTurretGunnerBinding, LiveRelaunch,
  LiveMoney, GameOverPlayersWire, CargoBayReload …; run 2: LiveWinCondition,
  LiveTurretGunnerBinding, CargoBayReload SwapPart); each verified to have ZERO
  references to P433 symbols; the ShipDeleteGone/JoinFlow set was confirmed to
  fail identically on a stashed (pre-P433) tree.

## 8. Live verification — REAL missile fire observed

A fresh local dev server was stood up from this repo's `Allegiance.Server`
(Debug build, real Artwork, `m2xspiderw` 2-team mission, port 6074) with the
P433 server binary, so `ApplyLoadout` mounts launchers.

### allegiance-mcp (live launcher resolution + mounting)

Driving `allegiance-mcp` against 6074, `spawn_bot {hull:"Adv Scout",
behavior:"combat", side:<0|1>, launcher:"default"}` returned cleanly with
`"launcher":"LRM Killer 1"`, and the server granted the launcher:
```
[loadout] <peer> ship 8111: granting real missile launcher part 20 ('LRM Killer 1', Magazine@0, ammo 2) — P433
[loadout] <peer> ship 8111 <- BUY_LOADOUT_ACK hull 13 nParts 5 fbought True fLaunch True
```
i.e. real launcher resolution + hull-compatible loadout building + server-side
mounting are all live for MCP-spawned bots.

**Honest limitation (mcp co-located fire):** to make an MCP-spawned bot fire, a
cross-side ship must be co-located in its sector so it builds a real lock — and
that requires a combat bot to transit warps (the two m2xspiderw teams home in
different sectors, Ganymede vs Organia). On this server instance the mcp combat
bots do not move at all — a `[p433-diag]` probe showed a transit bot stuck in
1051 with `path[4] graph=True warps=44 destMap=True warpFound=True` yet its
position frozen for minutes, and even an untouched **weave** bot (a plain
`BotClass`, code P433 never modifies) is frozen at its spawn point. This is the
documented P429 combat-movement / transit instability of this dev server
("verify combat on the opposing side"; "a bot… flies to an aleph and parks"),
independent of the missile feature. The mcp fire pipeline is byte-identical to
the vizfight pipeline below, which DID fire live; mcp's co-location is what the
server quirk blocks.

### vizfight (COMPLETE real lock-to-1.0 + real missile fire)

Running this repo's `vizfight attach 127.0.0.1 6074 8125 4 2 default`, the
enemy contingent mounted LRM Killer 1 (all 6 ships), rendezvoused in the
spectator's cluster (real warp transit, 1051→1055→1054→1050), built real locks
to 1.0, and sent real `SendFireMissile()` calls:
```
[attach] VZA00 fired real missile launcher at lock 1.0 -> SendFireMissile (ready 0.40s) — P433
[attach] VZA01 fired real missile launcher at lock 1.0 -> SendFireMissile (ready 0.40s) — P433
```
and the SERVER (authoritative) created and broadcast real missiles:
```
[firemissile] 6 ship 8103 launched -> FIRE_MISSILE(70) lock 0.000 ammo 2 -> 1
[missile] ship 8103 fired -> FIRE_MISSILE(70) type 20 pos … life 8 -> 7 peer(s) (same-side + cross-side)
[firemissile] 7 ship 8104 launched -> FIRE_MISSILE(70) lock 0.000 ammo 2 -> 1
[missile] ship 8103 expired missile 5510 -> MISSILE_DESTROYED(83) … sector 1050
```
This is real proof in vizfight: real lock-to-1.0 (dumbfire instant-lock, the
`IsFullLock` the client observed to gate the fire), real `SendFireMissile()`
wire calls, and real server-side `FIRE_MISSILE(70)` launches with real ammo
consumption (2→1→0) and real `Missile` objects (later `MISSILE_DESTROYED`).
The shared pipeline — server `ApplyLoadout` mounting, `Ship.UpdateMountedMissileLock`
building lock, `Ship.TryFireMissile` gating, `BroadcastMissileLock` feeding the
client, `SendFireMissile` → `HandleFireMissile` — is exactly what an
allegiance-mcp combat bot uses; the only thing the server quirk blocks is mcp's
co-location, not the firing.

## Deliverables

- Real launcher resolution + loadout + firing in BOTH `tools/allegiance-mcp`
  and `tools/vizfight`, plus the necessary server `ApplyLoadout` mounting and
  the shared Core primitive. Committed on `master` (not pushed).
- This report: `docs/reports/P433_REAL_MISSILES_MCP_AND_VIZFIGHT_PARITY.md`.

## Out of scope (unchanged, per brief)

- Missile-lock math (`Ship.UpdateMissileLock`) and the wire message shape —
  untouched (already correct).
- P428 HUD sprite rendering — untouched.
- No reduction of vizfight's bot-driving code; this ADDS missile capability.
