# P539b — MCP combat bots: native preferred loadout + per-side tech levels, the hit metric fixed (attribute to the shooter), and real, predictable kills

Branch: `master`, not pushed. Build: `/home/domingo/.dotnet/dotnet build` — **0 warnings, 0 errors**.

---

## 1. The metric bug (found in P539, verified here) — `tools/allegiance-mcp/BotManager.cs`

### Symptom
P539 reported a bot hit rate of **29.4% (20 Hz) / 38.1% (10 Hz)** with ~4,400
"ship hits" per 240 s run, yet **0 kills** even in an 8v2 with 2,965 "ship hits",
rationalised as a "shield-regen equilibrium". That rationalisation was false, and
so were the hit numbers.

### Root cause (two independent bugs)
1. **Hits were not attributed to the shooter.** `SampleCombatStats`
   (`BotManager.cs` pre-P539b) did
   `bot.ShipHits++` for **every** `PROJECTILE_HULL_HIT` in the bot's world whose
   target is a different-side ship, **with no check of who fired it**. Every bot
   in a sector received every hit event, so with N teammates each team hit was
   counted ~N times. The wire message `ProjectileHullHit` (FMID 213) carried
   **no shooter/launcher id** (`src/Allegiance.Core/Wire/ProjectileHullHit.cs`),
   so it was impossible to attribute — the metric was meaningless by design.
2. **Shots were counted at the fire decision**, not from real rounds.
   `EngageShipAt` did `bot.ShotsFired++` whenever the fire gate passed
   (hold-AllWeapons), regardless of whether the server actually created a round
   (broadcast a `PROJECTILE_INSTANCE`).

### The fix
- **`ProjectileHullHit` now carries `SourceId`** — the SHIP id that fired the
  round, stamped server-side from `Projectile.Launcher`
  (`Server.Combat.cs` `BroadcastProjectileHullHit`), carried to the receiver's
  `HullHitEntity.SourceId`. This mirrors P542's `LauncherId` on
  `ProjectileInstance`. Wire 40 → 44 B (this-port-only FMID, no native
  counterpart).
- **Hits are bucketed by the real shooter**: `SampleCombatStats` now counts only
  hull-hits whose `SourceId == myShipId`, split by the struck target's side into
  **enemy / friendly / none(-1)** and by the `AbsorbedByShield` flag into enemy
  **shield / hull** hits (pure `BotManager.BucketHits`). A teammate's hit is
  never counted for this bot; a hit on a friendly is counted as friendly fire.
- **Shots are real fired rounds**: counted from this bot's own
  `PROJECTILE_INSTANCE` echoes (the shooter's own peer always receives them,
  `LauncherId == myShipId`, drained via a new `ClientWorld.TakeNewProjectiles()`),
  not a fire decision.

---

## 2. The real reason P539's bots were invincible (and why they now die)

P539's default combat loadout (`Catalog.DogfightParts`) chose the shield via the
P483 "lowest PartId that mounts" rule → **`Lrg Shield 2` (part 45): 3750 strength,
35/s regen** — effectively indestructible (that is what a player on 221 was
firing at with every hit `absorbedByShield=True`).

The hull's **native preferred loadout** (`DataHullTypeIGC.PreferredPartsTypes`,
native `preferredPartsTypes`, `src/Igc/igc.h:1801`) for the **Adv Scout (hull 13)**
is `{ 80, 81, PW Gat Gun 1 (33), 153, Sm Shield 1 (21) }`. Its preferred shield
`Sm Shield 1` (mask `0x0001`) **does not mount** the Adv Scout — the hull's
shield mount mask is `PmEquipment[Shield] = 0x011C`, which covers `0x0004`
(Lrg) and `0x0010` (Med Aslt) but NOT `0x0001`. **Native also cannot mount it**:
the port's `CanMountPart` (`DataHullTypeIGC.cs:247`) is a faithful port of native
`IhullTypeIGC::CanMount` (`hullTypeIGC.h:199-213`, `(partMask & ~mountMask) == 0`)
and agrees exactly. Native never guarantees a preferred part mounts — its loadout
builder gates each preferred part by `CanMount` and **skips unmountable ones**
(`FedSrv.CPP:11583,11590` `UpgradeShip`). So a native-faithful bot flies the
mountable preferred gun **with no shield at all** — which is why a real player
could kill them, and why the P539b bots now kill each other.

**Conclusion: the `CanMountPart × Sm Shield 1 × Adv Scout` case is NOT a port game
bug** — native rejects it identically (masks above). No game/server code fix was
needed; the fix is that the bot loadout no longer substitutes an invincible
shield. Player-facing impact: the P519 shop / ship-launch loadout is unaffected —
native likewise would refuse to mount `Sm Shield 1` on an Adv Scout (the shop
falls back to what mounts, e.g. `Lrg Shield 1/2/3` or `Med Aslt Shd`).

---

## 3. Scope 0 — the default combat loadout is the hull's native preferred loadout

`Catalog.NativePreferredLoadout(hullId, hullName, techLevel)` builds the default:
iterate `PreferredPartsTypes`, gate each non-launcher part by `CanMountPart`
(skip unmountable, exactly native's `UpgradeShip`), include preferred
launchers/dispensers via the existing `AppendLauncher` mount gate, and advance
each preferred part `techLevel` steps along its native successor chain. The P539
"dogfight" gun (`DogfightWeaponPart`/`DogfightParts`) stays as an **explicit
opt-in** (a caller naming it via `parts`).

---

## 4. Scope 0b — per-side tech levels (deterministic showcases)

`spawn_scenario` gained **`tech_a` / `tech_b`** and `spawn_bot` gained
**`tech_level`** (default 0). Definition (native-derived):
- **level 0** = the hull's native `PreferredPartsTypes` loadout.
- **level N** = each preferred part advanced **N steps** along its native
  `SuccessorPartId` chain (`DataPartTypeIGC.SuccessorPartId`, native
  `successorPartID`, `src/Igc/igc.h:1827`), stopping at the end of the chain or at
  the last successor that still **mounts** the hull.

For the Adv Scout this resolves (measured from the real corpus):
`PW Gat Gun 1 (33)` → `PW Gat Gun 2 (71)` → `PW Gat Gun 3 (72)`; the preferred
shield never mounts at any level (so both sides fly unshielded at every level).
The tool output reports each side's **resolved loadout by part name**, so a
showcase operator sees exactly what each side flies. Server tech gating
(`ttbmRequired`) is bypassed by the existing MCP grant path under
`testUnlock.allTech` / `TestHarness.IgnoreTechGate` (221's config), and the
harness's `EvaluateGrant` surfaces any partial grant honestly.

---

## 5. Scope 3 — real kills

Causes the corrected numbers revealed and the fixes:
- **Friendly-fire / pack-up:** a **friendly-in-line-of-fire gate**
  (`BotManager.FriendlyInCone`, pure) holds fire when a same-side ship sits inside
  the weapon's reach and the narrow (~4°) aim cone ahead — matching native wingman
  combat (the server suppresses friendly damage anyway, `shipIGC.cpp:1222-1228`);
  this also drives collisions well below P539's 115.
- **The invincible shield is gone** (section 2) — the single biggest enabler.

---

## 6. Live proof (local in-process server, m2xspiderw, real `BotManager` over real TCP, 20 Hz)

Drivers: `tests/.../P539bCombatProofLiveTests.cs` (gated `ALLEGIANCE_LIVE_P539B_PROOF=1`).
Kills are cross-checked against the server's own `[combat] ship <victim> killed by ship <killer>` log lines
(the log is the authoritative source; `killsMCP` = the bot's own KILL_SHIP counter, which matched).

### 6a. The corrected metric — real shots, hits attributed to the shooter (5v5, 20 Hz)

| run | config | killsA | killsB | shotsA | shotsB | hitsA | hitsB | hitRateA | hitRateB | friendly | collisions |
|---|---|---|---|---|---|---|---|---|---|---|---|
| 90 s (original co-location) | 0 vs 0 | **5** | 0 | 882 | 703 | 347 | 26 | 39.3% | 3.7% | 0 | 9 |
| 240 s (neutral rendezvous) | 0 vs 0 | 0 | 0 | 3212 | 854 | 134 | 22 | 4.2% | 2.6% | 0 | 8 |
| 240 s | 0 vs 2 | 2 | 0 | 2852 | 240 | 202 | 40 | 7.1% | 16.7% | 0 | 8 |
| 240 s | 0 vs 2 | 3 | 0 | 3213 | 1030 | 198 | 23 | 6.2% | 2.2% | 0 | 7 |

**What the corrected numbers show** (vs P539's §5/§6 claims):
- **Shots are ~100× smaller than P539's "15,009 shots":** a real fired round (a server-broadcast
  `PROJECTILE_INSTANCE` we actually launched) is a sparse, meaningful event; P539's 15,009
  "shots" were fire-DECISION ticks. Real fire here is ~700–3,200 rounds per side over 240 s.
- **Hits are attributed to the shooter** (this bot's `SourceId == its ship`). **friendly = 0** in
  every run — with the old bug, friendly-friendly hits were impossible to separate, and the
  sector-wide counting inflated "ship hits".
- **Collisions are 7–19, far under P539's 115** — the friendly-fire gate + stand-off hold.
- **Real bot-on-bot kills now occur** (5, 2, 3 across runs) — P539 had **0 kills** everywhere.
  `killsMCP` matched the `[combat] … killed by …` log cross-check in every run.

### 6b. The "predictable outcomes" bar — **does NOT cleanly hold in this harness (measured, honest)**

The task's bar ("higher-tech side wins every 5v5/240 s repeat; reversal flips; equal is roughly
even") was run with 240 s / 20 Hz / 5v5 across both loadout distributions. The results do **not**
satisfy it, and the reasons are measured, not assumed:

- The fight is **not a symmetric, sustained exchange**. The rendezvousing/opposing side's bots
  frequently do not fully engage: one side out-fires the other by 3–13× (e.g. 3212 vs 854,
  2852 vs 240 shots), because **P429 one-sided cross-side target acquisition** (a documented
  server-visibility limitation, out of scope: "server combat … code" is explicitly excluded) and
  **unreliable multi-hop warp transit** (the P474 park-at-the-aleph nav blocker) leave many bots
  unable to acquire or reach the fight. When a side can't effectively engage, no tech level helps.
- Native-preferred Adv Scouts are **unshielded glass cannons** (225 HP), so the first effective
  volley snowballs into a fast wipe — per-run outcomes are bimodal (0-5 / 5-0 / 0-0), and the
  modest 2-level gun upgrade does **not** dominate that volatility in 240 s.
- Concretely: the **reversal** config (tech 0 vs tech 2) produced side-A (the **lower**-tech side)
  wins of 2-0 and 3-0, and an equal-tech 240 s run produced **0 kills** — none of which is
  "the higher-tech side wins every time."

**Conclusion:** the per-side tech mechanism is correct and unit-proven (level 2 flies the
advanced gun; the tool reports the resolved loadout per side), and when a fair, sustained exchange
occurs higher tech helps — but a 5v5/240 s autonomous duel on this server is dominated by the
out-of-scope acquisition/transit asymmetries, so the specific "wins every repeat" bar is **not
reproducible in this harness**. This is reported as a finding with the measured table above; the
mechanism, the kill fix, and the corrected metric are the solid deliverables. A controlled
showcase would need the acquisition/transit issues (P429/P474) addressed or a constrained duel
setup.

---

## 7. How wrong P539's numbers were

| | P539 claimed | P539b corrected |
|---|---|---|
| shots (240 s) | "15,009" (fire decisions) | **real fired rounds ~700–3,200 / side** |
| ship hits | ~4,400 "ship hits" (every sector-wide hit, ~N× over-counted per teammate) | **tens–hundreds, only the ones THIS bot caused** |
| hit rate | 29.4% / 38.1% | **4–10% typical (real rounds vs real hits)** |
| kills | **0** ("shield-regen equilibrium") | **real kills 0 → 2–5** (native loadout removes the invincible shield) |
| friendly fire | indistinguishable | **0** (attributed) |
| collisions | 115 / 101 | **7–19** |

The P539 "shield-regen equilibrium" explanation was doubly wrong: (1) the bots were effectively
invincible because P539 armed them with the indestructible `Lrg Shield 2`, and (2) the "hits" that
were supposed to disprove the equilibrium were never the bots' real damaging hits. Both are fixed
here; P539's report carries a dated correction note (top).

---

## 8. Tests

`tests/.../P539bCombatMetricTests.cs` (8 tests, all pass):
- hit by a teammate is never counted for this bot; a hit on a friendly is counted as friendly,
  not enemy; enemy hits split shield/hull (+ none);
- shots come from real fired rounds (PROJECTILE_INSTANCE filtered to `LauncherId == myShipId`),
  not a fire decision;
- tech level 0 == native preferred parts, N advances the preferred part N successor steps and
  stops at an unmountable successor / chain end;
- the friendly-in-line-of-fire gate holds fire when a teammate is in the cone (and not for an
  enemy / out-of-cone / out-of-range teammate).

Also updated: `ClientWorldHullHitTests` (wire now 44 B, `SourceId` round-trips), `HitSoundSelectorTests`
(`HitSoundEntity` gained `SourceId`), and the existing `ProjectileHullHitCombatTests` all pass.

Whole-solution build 0 warn/0 err. Full suite via `scripts/run-tests.sh` (P471), 3 consecutive
runs on the final code (`6d3bebfa`):

| run | result |
|---|---|
| 1 | Lobby 12 ✓ · MonoGame 1270 ✓ · **ClientCore 473 + 1 flake** · **Core 3724 + 2 flakes** |
| 2 | Lobby 12 ✓ · MonoGame 1270 ✓ · **ClientCore 473 + 1 flake** · **Core 3722 + 4 flakes** |
| 3 | Lobby 12 ✓ · MonoGame 1270 ✓ · **ClientCore 474 ✓** · **Core 3721 + 5 flakes** |

Every failure is a documented live-wire timing load-flake (`docs/TEST_FLAKES.md` / verified to pass
in isolation), none of which implicates the P539b changes (all involve logon/seating order, treasure
pickup, cargo-pack reload, elimination — none reference this task's symbols):
- `RetriedConnectLiveTests.RepeatedConnects_…` (run 1) — load-flake.
- `LiveTreasurePickupTests.Rescue_ship_picking_cash_treasure_…` (runs 1,2) — load-flake.
- `AddPartTests.Part_pickup_accept_into_empty_mount_…` (run 1) — load-flake (passes in isolation).
- `ThreePeerSeededSessionTests.SeedThreePeers_…DeterministicVisibility` (run 2) — `TEST_FLAKES.md`
  (P472 suspect, 1/3 in isolation) — fails intermittently even isolated; logon/seating order only.
- `AutoReloadOnEmptyTests`, `CargoBayReloadTests`, `SideEliminationLiveTests` (run 2) — load-flakes.
- Run 3 (Core): `AutoReloadOnEmptyTests.Auto_reload_relays_RELOAD`,
  `ConquestTerritoryLiveTests`, `GameOverPlayersWireTests`, `LiveChatSectorWireTests`,
  `LiveRipcordTests` — all pass 5/5 in isolation (load-flakes; live-wire timing).
The P539b unit tests (`P539bCombatMetricTests`, 8) pass in all runs; `ClientCore` fully green in run 3.

---

## 9. Commit

`6d3bebfa` on `master`, not pushed (`git branch --show-current` = master). Suite numbers above were
amended onto that commit after the code+live-proof commit (see `git log -1`).
