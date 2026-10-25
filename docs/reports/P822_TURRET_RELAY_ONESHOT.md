# P822 — native one-shot active-turret relay and active/inactive selection

Dispatch base: public master `9d78ec6b` (worktree also carries `2f1278be`, P812,
which only adds `tools/mdl-to-glb`), branch `pi/p822`. Changes remain in the
working tree — nothing committed or pushed. Native references are relative to
`~/Development/Allegiance/src/`. Raw logs live outside the repo in
`~/allegiance-evidence/P822/` (they are linked inline); no report assets were
added under `docs/reports/`.

## What this closes

`docs/parity/sim-rules-matrix.csv` row **SIM-0470**
(`ship_status_delta_and_turret_updates`, weight 3) was `partial` for two native
differences plus one unsettled cadence classification:

1. **One-shot relay.** Native gathers the relay lists once per
   `SendShipUpdates` pass and then calls `pfsPlayer->ResetLastUpdate()`
   (`FedSrv/FedSrv.CPP:3796`; `m_ucLastUpdate = c_ucNone`,
   `FedSrv/fsship.h:290-293`). A stored `ClientActiveTurretUpdate` is therefore
   relayed by the **first** pass after it arrived (if
   `|atu.time − g.timeNow| ≤ 1.5 s`, `FedSrv.CPP:3783-3790`) and never again;
   the port re-sent it every tick while it stayed inside the 1.5 s window.
2. **Active vs inactive selection.** Native selects active purely by the
   last-update kind (`if (ucLastUpdate == c_ucActiveTurretUpdate)`, **no weapons
   check**, `FedSrv.CPP:3783-3789`) and inactive only in the `else` branch when
   no weapon state bit is set (`FedSrv.CPP:3791-3794`). The port required
   `weaponsMaskIGC` for the active relay and put every weapons-off gunner in the
   inactive list regardless of its last update kind, so an aiming,
   non-firing gunner's facing froze on the other clients.
3. **The P659 per-viewer 2 Hz pose floor** (`broadcast.minUpdateHz`,
   `src/Allegiance.Server/ServerConfig.cs:626-667`) was the third reason the row
   stayed partial. Settled below as an **era constraint**, the modern stand-in
   for SIM-0469's modem-era bandwidth budget.

All three are resolved; the row is now `implemented` and the `periodic` area
moves from 80.00 % to 86.67 % (see [Parity](#parity-and-doc-updates)).

## Native rules and classifications

| Native rule | Native source | Classification and result |
|---|---|---|
| Relay lists are gathered once per pass, then every player's last-update kind is cleared | `FedSrv/FedSrv.CPP:3722-3796`, `FedSrv/fsship.h:290-293` | **Design.** Implemented: `_pendingTurretRelays` marks an accepted ATU and is consumed after the pass has projected every viewer. The old repeat-every-tick relay was a **port bug**. |
| Active membership = update kind `c_ucActiveTurretUpdate` **and** `\|atu.time − now\| ≤ 1.5 s`; no weapons test | `FedSrv/FedSrv.CPP:3783-3790`, `FedSrv/fsship.h:242` | **Design.** Implemented verbatim (`CollectActiveTurretCandidates`). The extra `weaponsMaskIGC` gate was a **port bug**. |
| Inactive membership = the `else` branch, `(stateM & (oneWeaponIGC\|allWeaponsIGC)) == 0`; no age gate; the same IDs go to every cluster player **including self** | `FedSrv/FedSrv.CPP:3791-3794,4009-4020` | **Design.** Implemented as `pending == false && weapons off` (the branch is mutually exclusive with active by construction). P788c's inactive sender/consumer is unchanged. |
| Active relay skips the recipient's own gunner ship; the stored `atu` is forwarded intact (controls + orientation) | `FedSrv/FedSrv.CPP:4049-4071` (self skip `:4058`) | **Design.** Unchanged (P803); the cluster-wide gather is now shared by all viewers of the pass. |
| `FM_C_INACTIVE_TURRET_UPDATE` sets `m_ucLastUpdate = c_ucInactiveTurretUpdate` and `SetStateM(0)` | `FedSrv/fsship.cpp:1220-1223`, `FedSrv.CPP:4887-4894` | **Design.** The port's ITU handler now also cancels a still-pending active mark, so a gunner that fired and stopped inside one pass is inactive, never active. |
| `ProcessShipUpdate(const ClientActiveTurretUpdate&)` sets `weaponsMaskIGC` and integrates the reported orientation forward in ≤ 0.25 s steps | `Igc/shipIGC.cpp:3349-3392` | **Design.** Unchanged (P109/P803); it is what makes an accepted ATU both "shooting" and stored (`m_atu`). |
| Only **player** gunner ships participate; boarded (`GetParentShip() != NULL`), real turret mount (`GetTurretID() != NA`) | `FedSrv/FedSrv.CPP:3762-3792` | **Design.** P788c's exclusions kept (observers/NPC/drones/dead/docked/unboarded). |
| Odd-frame cadence plus a bandwidth-selected HEAVY/LIGHT split under a 1600 byte/s modem budget | `FedSrv/FedSrv.CPP:3664-3666,3830-3985` | **Era constraint** — already classified that way in SIM-0469. The port's density-aware HEAVY budget (`Server.Broadcast.cs:3171-3201`, P604b/P632) plus P659's per-viewer pose floor is the modern replacement; see [item 3](#item-3--the-p659-pose-floor-is-the-era-constrained-budget). |
| Server-authoritative pose: `c_ucShipUpdate` one-shot for the player's own ship | `FedSrv/FedSrv.CPP:3764-3781`, `FedSrv/fsship.cpp:1441` | **Design / port adaptation** (documented, out of scope). The port never accepts a client-computed pose; own-ship poses are exported by the server. It bears on item 3 only as the reason the port needs a *pose floor* at all. |

No native **bug** was reproduced or preserved: both remaining differences were
omissions in the port, so closing them is straight parity. Where the native
behaviour itself is era-bound (the modem byte budget and the fixed
2 heavy / 2 light / 2 active-per-interval quota that replaced it when the
bandwidth patch was disabled, `FedSrv.CPP:3838-3910`), the port keeps its
documented modern adaptation instead of the fixed caps.

## Implementation

### One-shot "last update kind"

Two native pieces were conflated in `_lastTurretUpdates`; they are now separate:

| Native state | Port state | Written by | Cleared by |
|---|---|---|---|
| `CFSPlayer::m_atu` (persistent stored update, `fsship.h:297-298`) | `Server._lastTurretUpdates` (unchanged, keyed by ship id) | `RecordAcceptedActiveTurretUpdate` | never (replaced by a fresher ATU) |
| `CFSPlayer::m_ucLastUpdate` (`fsship.h:242-243,273-275`) | `Server._pendingTurretRelays` (`HashSet<Ship>`) | `RecordAcceptedActiveTurretUpdate` (ATU accepted), removed by the ITU handler | `ConsumePendingTurretRelays()`, once per ship-update pass |

`RecordAcceptedActiveTurretUpdate` (`src/Allegiance.Server/Server.ShipCommands.cs`)
is the single seam for both writes, called by the accepted-ATU path right after
`SetOrientation` — the same place native does `m_atu = atu;
m_ucLastUpdate = c_ucActiveTurretUpdate` (`fsship.cpp:1206-1218`). The
`FM_C_INACTIVE_TURRET_UPDATE` handler (`src/Allegiance.Server/Server.Dispatch.cs`)
removes the mark when it clears `StateM`, matching
`SetInactiveTurretUpdate` (`fsship.cpp:1220-1223`).

`BroadcastShipsUpdate` (`src/Allegiance.Server/Server.Broadcast.cs`) calls
`ConsumePendingTurretRelays()` in its `finally` block, i.e. **after** every
viewer of that pass has been projected — serial or (P603) parallel, legacy
sender or interest-tier sender. That is the port's counterpart of native's
per-cluster `ResetLastUpdate`; the whole set is consumed at once because native
processes every cluster that holds a flying player, and a boarded gunner is
itself such a player, so "no viewer in my sector" still resets the kind natively
too. The only residual difference is the granularity (one pass vs one processed
cluster), which cannot be observed on the wire: it would require a gunner's
update to arrive while its own cluster is skipped, which cannot happen while the
gunner is seated there.

### Selection

`CollectActiveTurretCandidates` is native's per-cluster `rgActiveTurrets` fill
(`FedSrv.CPP:3774-3792`) and `CollectInactiveTurretRelay`
(`src/Allegiance.Server/Server.NativeBroadcastOrder.cs`) is its `else` branch:

| Gunner state (boarded, real mount, live, undocked, player) | Active relay | Inactive ID |
|---|---|---|
| pending ATU, stored update within ±1.5 s | **yes** (all other cluster players) | no |
| pending ATU, stored update outside ±1.5 s | no | no (weapons on) / yes (weapons off) |
| no pending ATU, weapons off | no | **yes** (every cluster player, self included) |
| no pending ATU, weapons on | no | no |

Because active is keyed on the *kind* and inactive on `!kind && weapons off`,
the two lists are mutually exclusive exactly as native's `if`/`else if` are.

The active gather is cached per sector for the duration of one pass
(`_activeTurretCandidates`, cleared at the start and end of `BroadcastShipsUpdate`
like the existing `_inactiveTurretRelays`), so:

* every viewer of a sector sees the identical candidate list, even when sector
  workers run in parallel — no per-viewer divergence and no second
  `sector.Models` walk;
* the per-viewer body only filters out the recipient's own ship
  (`FedSrv.CPP:4058`) and encodes the `ServerActiveTurretUpdate` records with the
  CompactTime delta from the message `timeReference` (unchanged from P803);
* the freshness gate uses the **gunner's** mission clock
  (`gunner.Mission.LastUpdate`, native `g.timeNow`) rather than the viewer's
  (identical here — same sector — but now stated correctly).

### Paths covered

The collection lives in the one place both senders already shared, so nothing
had to be duplicated:

* legacy per-peer sender (`BroadcastShipsUpdatePeerLegacy`), serial and P603
  parallel sector projection, port and P604-native ship-update models;
* the P677 interest-tier sender (`BroadcastTierPose`) with the P669 immutable
  snapshot / encode-once publisher;
* both `sim.nativeBroadcastOrder` phases (P788c): started missions project in
  the pre-`Mission.Update` phase and every other mission in the post phase, and a
  mission is never in both, so consuming at the end of each
  `BroadcastShipsUpdate` call consumes exactly once per tick per mission.

### Thread safety

`_pendingTurretRelays` is armed/cleared on the sim thread inside the datagram
handler and in the pass epilogue, and read by sector workers during projection;
both sides take the existing `_snapshotCaptureGate` leaf lock, and the atomic
`_lastTurretUpdates` write happens inside the same critical section so a worker
can never observe a mark without its stored update. `TestResetBroadcastState`
clears the mark and both per-pass gathers so recorded-tick replays stay
deterministic.

### Modern better approaches (kept native for parity)

* **One-shot latch → explicit state.** `m_ucLastUpdate` is a mutable per-player
  slot whose meaning depends on being consumed exactly once by a broadcast pass;
  now that the port has a single projection point the consumption is safe, but a
  modern design would carry an explicit generation/sequence number on the stored
  update (or make the relay event-driven) so "already relayed" is a property of
  the data rather than of a transient flag. The port keeps the latch to stay
  byte- and behaviour-identical to native.
* **Two lists → one record.** Native splits a gunner's state across
  `HEAVY.rgActiveTurretUpdates` and `LIGHT.rgInactiveTurretUpdates` because the
  two are sized by different quotas. A single per-gunner record carrying
  `(orientation, controls, firing)` would remove the mutual-exclusion coupling.
  Not adopted: no new wire fields (the task forbids them) and the real client
  consumes both shapes.
* **Audience scoping.** Native sends every cluster gunner's relay to every
  cluster player; the port's P677 interest tiers already exist, but turret relays
  stay cluster-wide because that is the native rule and gunners are cheap
  (one 16-byte `ServerActiveTurretUpdate` per active gunner per pass).

### Files

| File | Change |
|---|---|
| `src/Allegiance.Server/Server.cs` | `_pendingTurretRelays` (native `m_ucLastUpdate`), docs for the split from `_lastTurretUpdates` (`m_atu`). |
| `src/Allegiance.Server/Server.ShipCommands.cs` | `RecordAcceptedActiveTurretUpdate` seam; the ATU handler arms the one-shot mark. |
| `src/Allegiance.Server/Server.Dispatch.cs` | `INACTIVE_TURRET_UPDATE` cancels a pending active mark (`fsship.cpp:1222`). |
| `src/Allegiance.Server/Server.Broadcast.cs` | `CollectActiveTurretCandidates` (native kind + freshness, cluster-wide, cached per pass), `CollectActiveTurretRelay` rewritten to use it (no weapons gate), `ConsumePendingTurretRelays`, pass-scoped clearing. |
| `src/Allegiance.Server/Server.NativeBroadcastOrder.cs` | `_activeTurretCandidates`; `CollectInactiveTurretRelay` takes native's `else`-branch form (`!pending && weapons off`). |
| `src/Allegiance.Server/Server.BroadcastTestSeams.cs` | reset the one-shot latch and the per-pass gathers. |
| `tests/…/P822TurretRelayOneShotTests.cs` | new: 4 behaviours × 8 configurations. |
| `tests/…/P803TurretRelayBroadcastTests.cs` | store the ATU through the new seam (a bare `_lastTurretUpdates` write is native `m_atu` only, no `m_ucLastUpdate`). |

No wire format changed, no new message, no new field, no client change, no HUD
change (so no Classic/Modern pair is owed by this task).

## Item 3 — the P659 pose floor is the era-constrained budget

**Decision: era constraint, not a behaviour difference.** Evidence:

1. **Native LIGHT records carry no pose.** `ServerLightShipUpdate` is
   `shipID + stateM + controls` only (`Igc/igc.h:2423-2429`), and the native
   client consumes them by calling `pship->ProcessShipUpdate(lsu)`
   (`clintlib/appmsg.cpp:712-731`) into its **local IGC simulation**, which is
   what moves the ship between sparse updates. Native can be sparse because every
   client runs the same simulation locally; the fraction of ships promoted to
   HEAVY is a *bandwidth* decision (`FedSrv.CPP:3830-3985`: 1600 byte/s modem
   budget, fixed 2 heavy / 2 light / 2 active per interval).
2. **The port's client is a pure renderer.** It has no local IGC sim, so a LIGHT
   record cannot produce a pose at all: `LightShipUpdateEntry` is used for
   presence/state only, and remote poses are bridged by a dead-reckoner whose
   extrapolation is captured at 1.5 s (native's own
   `ProcessShipUpdate` window, `Igc/shipIGC.cpp:3113-3115`). With only native's
   fixed heavy quota, a dense sector would freeze distant ships for seconds —
   the P604b/P632 finding recorded in `Server.Broadcast.cs` (195 in-sector ships
   with a fixed 2 heavy/tick ⇒ ≈4.9 s between re-feeds ≫ 1.5 s).
3. Therefore the port must deliver *poses* where native delivered *controls*, and
   `broadcast.minUpdateHz` (2 Hz per viewer, `ServerConfig.cs:626-667`) plus the
   density-aware heavy budget is the direct modern replacement for native's
   bandwidth-selected heavy share. It is the same decision SIM-0469 already
   classified **era constraint** for the `native_modem_ship_budget` row, whose
   port answer is the density-aware heavy quota at
   `Server.Broadcast.cs:3171-3201` (P604b/P632). SIM-0469 is cited from SIM-0470's
   notes now; that row's own `port_cite_or_reason` still names the older
   `Server.Broadcast.cs:609` line, which the P604b/P632 edit has since moved —
   left as-is because it is not this task's row.

**Modern better approach (what the port does, and what could go further):** a
per-viewer, density-aware pose budget with age floors and big-change promotion
(P813a) instead of a fixed per-tick quota and a modem byte cap; the further
option, if bandwidth ever matters more than server CPU, is to restore the native
design properly by giving the client a local simulation (or interpolating
authoritative snapshots on a render clock, which P813b already does for remote
ships) so sparse LIGHT records would again suffice. Neither is a parity gap in
the relay rule itself.

The own-ship path is the related documented adaptation and is untouched: native's
`c_ucShipUpdate` one-shot (`FedSrv.CPP:3764-3781`) only ever *validates* a pose
the client computed, while this port's server owns the pose (P604/P608). Because
of that, own-ship cadence cannot be "one-shot relayed" either, and the port sends
its own pose every broadcast tick.

Nothing about SIM-0470's relay rule remains different. The row's notes therefore
carry the era-constraint classification of the cadence, not a partial-status
caveat.

## Verification

Every .NET command used `~/.dotnet/dotnet`. Builds, NuGet caches, AOT output and
logs were kept in worktree scratch (`$TMPDIR`, `.scratch/`), raw evidence in
`~/allegiance-evidence/P822/`. No `git stash`, no `pkill -f`/`pgrep -f`, no
remote host was touched.

| Check | Result |
|---|---|
| Whole solution (`dotnet build AllegianceCore.slnx`) | **0 warnings / 0 errors** (`~/allegiance-evidence/P822/build-solution.log`) |
| Every tool project (`tools/build-tools.sh`) | **84/84 built, 0 warnings / 0 errors** (`build-tools.log`) |
| Server Native AOT (`Release`, `linux-x64`, self-contained, `PublishAot=true`) | **ELF produced, no IL2026/IL3050** (`aot-publish.log`) |
| New + affected Core group ×3 alone | **58/58 ×3** — `P822TurretRelayOneShotTests`, `P803TurretRelayBroadcastTests`, `P788cNativeTickOrderTests`, `UpdateCadenceTests` (`isolated/core-group-*.log`) |
| `P803TurretRelayTests` (client) ×3 alone | **4/4 ×3** (`isolated/client-p803-*.log`) |
| Other turret classes | `LiveTurretGunnerBindingTests`, `TurretGunnerFiringTests`, `P655GameStateIntegrityTests`, `LightHeavyShipsUpdateCodecTests`, `Wire/TurretWireTests`: **53/53** |
| ONE `tools/run-tests.sh --tier fast` | **10,728 cases, 10,724 pass, 1 fail, 3 existing skips**, 303.4 s; 9/9 infrastructure checks pass. The single failure is the documented load flake below. |
| P700 showcase never-parks 5× (plus 1 confirmation) | never-park assertion not tripped in **5/5** runs (worst 2.005 s vs the 5 s bound); arrival deadline met by runs 1-4 and missed by run 5 (20/30). Confirmation run 6 **PASS** (runner-reported). Gunners are off, so no change vs base — details below. |
| `python3 scripts/parity-scorecard.py` | passes; `periodic` 80.00 % → **86.67 %**, sim-rules TOTAL 99.07 % → **99.30 %** |
| `python3 tools/check-glossary-links.py` | passes (new GLOSSARY entry path exists) |
| CSV / text line endings | `grep -c $'\r' docs/parity/sim-rules-matrix.csv` = **0**, `docs/TEST_FLAKES.md` = **0** |

### New tests — the eight configurations

`P822TurretRelayOneShotTests` runs each behaviour with
`sim.nativeBroadcastOrder` **off and on** × interest tiers **off and on** ×
parallel projection **off and on** (8 `InlineData` rows per test, 32 cases).
The flag is set through `SimConfig.NativeBroadcastOrder`, i.e. the *configured*
half of `Server.ResolveNativeBroadcastOrderFlag(configured, env)` — the
`ALLEGIANCE_SIM_NATIVE_BROADCAST_ORDER=0/1/false/true` forms (env wins, invalid
values fail) are pinned by `P788cConfigTests` in the same 58-case group, because
setting a process-wide env var inside an xUnit run would leak into the other
collections running in parallel:

| Test | Asserts |
|---|---|
| `Fresh_active_update_is_relayed_in_exactly_one_pass_to_every_other_cluster_player` | the real `FM_C_ACTIVE_TURRET_UPDATE(99)` wire path; pass *N* relays the gunner to the carrier and both other viewers, never to the gunner itself, and not as inactive; pass *N+1* relays neither — with an assertion that the stored update is **still inside the ±1.5 s window**, so the silence is the consumed kind and not the age gate. |
| `Aiming_non_firing_gunner_with_a_fresh_update_is_relayed_as_active` | same update with `StateM = 0` (aiming, not firing): pass *N* relayed as **active**, not inactive; pass *N+1* inactive (kind consumed). |
| `Weapons_off_gunner_without_a_pending_update_is_inactive` | ATU then `FM_C_INACTIVE_TURRET_UPDATE` in one pass: the inactive ID wins, no active relay (`fsship.cpp:1222` cancels the pending active mark). |
| `Stale_active_update_with_weapons_on_is_neither_active_nor_inactive` | accepted `m_atu` outside the ±1.5 s window, weapons on: no active, no inactive, and still neither on the next pass. |

### Which new tests fail on master

Measured by restoring the dispatch-base server sources in this worktree
(`git checkout HEAD -- src/Allegiance.Server`, no stash; the working copies were
kept in scratch and put back afterwards) and running the same fixtures against
them (32 cases, 16 failures):

* `Fresh_active_update_…` — **fails 8/8** configurations on master (the stored
  update is re-relayed on the second pass, so the "exactly one pass" assertions
  fail; the second pass also still carries the active record).
* `Aiming_non_firing_…` — **fails 8/8** (master requires `weaponsMaskIGC`, so the
  weapons-off gunner is missing from the active list and appears in the inactive
  list instead).
* `Weapons_off_gunner_without_a_pending_update_is_inactive` — **passes 8/8** on
  master: it pins the P788c behaviour that already matched (the pending-cancel
  assertion is satisfied on master because master has no pending state at all).
* `Stale_active_update_with_weapons_on_is_neither_active_nor_inactive` —
  **passes 8/8** on master (both of master's gates already excluded it). Run
  against master through a master-compatible variant, because the fixture's
  `RecordAcceptedActiveTurretUpdate` seam does not exist there.

Net: 16 of 32 cases are genuine master failures (the two behaviour changes), and
the suite is 32/32 on the working tree.

### ONE fast-tier run

`tools/run-tests.sh --tier fast`: **10,728 cases, 10,724 pass, 1 fail, 3 existing
optional-asset skips**, 303.4 s, infrastructure 9/9, P744 calibration factor
1.046 (limit 10.459 s), no timing offenders, no quarantine flakes.
Per project: Lobby 79/79, Update 28/28, MonoGame 3,466 pass + 3 skips, Core
5,342/5,342, ClientCore 1,809 pass / 1 fail.

The single failure was
`CrossSideCombatVisibilityLiveTests.Fire_is_not_relayed_to_peers_in_other_sectors`
(setup `Assert.NotSame`: the shooter and its intended elsewhere-teammate were
placed in the same sector because the fixture assigns sectors by asynchronous
spawn order). It passes **3/3 exact-method runs alone** and touches no path this
task changed; ledgered in `docs/TEST_FLAKES.md` (2026-10-25) with the existing
P699c/TR-2 entries. Host load average was ~14.7 from other agents' suites.

### Showcase never-parks, 5× against the base rate

Bot turret gunners are **off by default** (`GunnerPolicy.Effective` reads
`ALLEGIANCE_GUNNERS`; it was unset here), so a showcase scenario sends no
`ACTIVE_TURRET_UPDATE` at all and this diff cannot reach its ships. Numbers
(`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`):

| Run | Duration | reached/30 | longest speed<1 s with target (limit 5 s) | shots / hits / kills |
|---|---:|---:|---:|---:|
| 1 | 388.3 s | 24 | 1.904 | 5546 / 2486 / 17 |
| 2 | 361.7 s | 24 | 1.912 | 2126 / 613 / 6 |
| 3 | 389.3 s | 28 | 0.200 | 793 / 4 / 0 |
| 4 | 366.8 s | 24 | 2.005 | 2714 / 1257 / 11 |
| 5 | 480.0 s | **20** | 2.005 | 9359 / 4664 / 49 |

* **Never-parks: the bound was never tripped in 5/5 runs** — worst
  stopped-with-target interval 2.005 s against the 5 s assertion (base rate in
  the P817 record is 1.5-3.9 s), so no change vs base rate.
* **Arrival deadline: runs 1-4 met it (24/24/28/24 of 30); run 5 did not**
  (20/30 after the 480 s window, the test requires ≥ 24). That is the arrival
  miss of the class already recorded in `docs/TEST_FLAKES.md` (P817: "the initial
  arrival miss was transient under this host load"). Durations are 3-4× the
  documented ~1m50s base rate, consistent with the host load.
* Verdict capture: the five-run series used
  `--logger console;verbosity=detailed`, which prints `Test Run Successful.`
  rather than `Passed!`; every asserted metric was captured in the table above,
  and runs 1-4 stayed inside all of them. The **confirmation run 6 is
  runner-reported PASS** in 6 m 16 s
  (`~/allegiance-evidence/P822/p700-run6-pass.log`).
* Ledgered in `docs/TEST_FLAKES.md` (2026-10-25).

## What remains

* Nothing in SIM-0470's relay rule: one-shot consumption, the freshness window,
  active-by-kind, inactive-by-`else`, self-skip versus self-included IDs, and the
  existing `HEAVY`/`LIGHT` wire fields are all native-faithful.
* Retained, documented adaptations (out of scope here): the server-authoritative
  own/remote pose model with its density-aware budget and P659 pose floor
  (era constraint, SIM-0469), the fixed native per-interval quotas
  (`FedSrv.CPP:3838-3852`, an era-bound artefact of the disabled bandwidth patch),
  and the active-turret *quota* (`FedSrv.CPP:3876-3910` fixed
  `cActivePerInterval = 2`, short-list deviation sort `:3953-3969`) which the
  port does not impose — with a normal cluster gunner count every pending update
  fits one message, and the port's cluster-wide list is never truncated.
* SIM-0141 (broadcast-order flag) and SIM-0150/0208 (NPC warp cooldown) remain
  separate A/B items, untouched.

## Parity and doc updates

* `docs/parity/sim-rules-matrix.csv`: SIM-0470 `partial` → **`implemented`**;
  sites now name `RecordAcceptedActiveTurretUpdate`, `CollectActiveTurretCandidates`,
  `CollectActiveTurretRelay`, `CollectInactiveTurretRelay`,
  `ConsumePendingTurretRelays` and the ITU handler; the evidence column lists the
  four new tests; the notes classify the cadence floor as **era constraint** with
  SIM-0469, and record that both prior omissions were port bugs. `periodic`
  86.67 % (39/45), sim-rules TOTAL 99.30 % (1279/1288); LF preserved, 0 CR bytes.
* `GLOSSARY.md` §3 Server: new "Turret relay (one-shot last update kind)" entry.
* `docs/TEST_FLAKES.md`: the 2026-10-25 CrossSide fire-relay and showcase
  arrival-miss observations above.

## Report metadata

* Base: `9d78ec6b` (+ `2f1278be` P812 tooling in the worktree); branch `pi/p822`.
* Raw evidence: `~/allegiance-evidence/P822/` (`build-solution.log`,
  `build-tools.log`, `aot-publish.log`, `isolated/*.log`,
  `fast-tier-run/` with `summary.json` and per-project logs, `p700-run6-pass.log`).
* Toolchain: `~/.dotnet/dotnet` (SDK 10).
