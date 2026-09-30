# P451 — "base under attack" / "critically damaged" alert for the defending team

A gameplay-parity port of native `FedSrvSiteBase::DamageStationEvent`
(`~/Development/Allegiance/src/FedSrv/FedSrv.CPP:12183-12233`), which gives the
defending side a throttled team chat line plus a voice cue the moment an enemy
starts hitting a station. Landed in two commits on `master` (server, then
client); not pushed — Domingo pushes.

- Server commit `9d9d4e4d` "P451 (server): base under-attack / critically-damaged team alert…"
- Client commit `a4106a4c` "P451 (client): play the station under-attack / critical voice…"

---

## Summary of behaviour (native parity)

- **Server:** when a real damaging hit (`amount >= 1.0f`), from a real seated
  launcher, carrying no `c_dmgidNoWarn` flag, lands on a station, the server
  sends **at most one** alert per station per **10 s** to the **defending side
  only**:
  - hull fraction `< 0.30` → `"<base> in <sector> critically damaged and under
    attack by <pilot>"` with `VoiceOver = CriticalSound`;
  - otherwise → `"<base> in <sector> under attack by <pilot>"` with
    `VoiceOver = UnderAttackSound`.
  - A same-side player launcher instead gets a private `"Watch your fire"`
    (droneWatchFireSound), as in native.
- **Client:** when a chat arrives whose `VoiceOver` is one of the real PCore001
  station types' `UnderAttackSound`/`CriticalSound` ids, the matching
  under-attack / critical clip plays. The chat text already shows in the chat
  log (unchanged).

## Pre-fix failing-test evidence

The wire-level suite
`tests/Allegiance.Core.Tests/StationUnderAttackAlertLiveTests.cs` (3 tests)
**fails on the unfixed code**: temporarily removing the
`_host.StationDamaged = BroadcastStationDamaged;` wiring in
`Server.Lifecycle.cs` (so `DamageStationEvent` reaches no subscriber) made all 3
tests red (`defender never received the under-attack alert` / `first hit must
alert the defender`), and they pass again with the wiring restored. The pure
client table test also demonstrably discriminates: it failed when the table was
incomplete (station type 73 Transceiver's UnderAttackSound 831 was uncovered)
and passes once every real id is covered.

## Where the event is raised, and why

`DamageStationEvent` is raised inside
`Station.ReceiveDamage`
(`src/Allegiance.Core/Sim/Station.cs`), immediately after the pedestal
invulnerability gate and before the friendly-fire/damage mutation, for any
non-repair (`amount >= 0`), non-pedestal hit, reached through the **launcher's
mission site**:

```csharp
if (amount >= 0.0f && launcher is Ship als && als.Mission is { } am)
    am.Site.DamageStationEvent(this, launcher, type, amount);
```

Rationale:
- Native raises it from `CstationIGC::ReceiveDamage`
  (`src/Igc/stationIGC.cpp:168-169`, `pigc->DamageStationEvent(this, launcher,
  type, amount)`) inside the shield branch, using the station's own mission
  site. This port's `Station` has no mission back-reference, so we reach the
  site through the launcher's mission — the same bridge `Model.Sector` already
  uses for `KillStationEvent` (`Sector.cs:1836-1838`).
- The port's station damage currently flows through exactly **one** real call
  site, `Model/Sector.cs:1791` (direct ordnance hits in
  `ResolveCombatCollisions`); explosions and collisions do not currently damage
  stations in the port, so raising inside `ReceiveDamage` covers every real hit
  and matches native's single raise point.
- Native nests the raise inside the *shield* branch; we raise for any non-repair
  hit so **both** the cross-side team alert **and** the same-side "Watch your
  fire" branch are reachable — the same set of outcomes native produces from the
  seam when friendly fire is enabled. This is the deliberate divergence the brief
  anticipated; a shieldless base also alerts, which is strictly more useful.
- The raise precedes the shield/hull mutation, so the host reads the **pre-hit**
  hull fraction — exactly as native does (line 169 before line 171) — which is
  what makes the `critical < 0.30` threshold meaningful.

## The alert (server) — `BroadcastStationDamaged`

`Server.Combat.cs`, wired in `Server.Lifecycle.cs`. Port of
`FedSrvSiteBase::DamageStationEvent` in the native order:

1. **Gates** (`FedSrv.CPP:12185`): `amount < 1.0f` → nothing; launcher not a
   `Ship` → nothing; `type & c_dmgidNoWarn` (`0x20`, `igc.h:559`) → nothing;
   unseated launcher (`SideId` null) → nothing (`FedSrv.CPP:12187-12188`).
2. **Throttle** (`FedSrv.CPP:12190`): `SimNow - station.LastDamageReport <= 10.0`
   → nothing (and does not reset the stamp, matching native
   `FedSrv.CPP:12231`). Per-station state is `Station.LastDamageReport`, a new
   `double` (public setter), initialized to `NegativeInfinity` so the first hit
   always alerts (native reaches that state because stations exist minutes into
   a server's life).
3. **Cross-side** (`pside != launcherSide`): team `ChatMessage` to the defending
   side's seated peers in the launcher's mission only, with the exact native
   text shapes and `VoiceOver` = `CriticalSound` (fraction `< 0.30`) or
   `UnderAttackSound`. Alliance slot elided (no alliances in the port) — `ct` is
   always `CHAT_TEAM`.
4. **Same-side** (`pside == launcherSide`): private `"Watch your fire"` with
   `droneWatchFireSound` = **1325** (derived: igc.h:456-463 enum from
   `lastDynamicSound = 1000`, sounds.h:377 `DEFSOUND(droneWatchFire)` is the
   325th entry → 1325).

Scoped to the launcher's mission (the attacking ship's mission is the mission
whose sector the base sits in — same bridge as `KillStationEvent`), so a peer on
the defending side id in *another* mission is never alerted.

## Client id→stem table and its sounddef source

`SoundManager.StationAttackVoices` (`src/Allegiance.ClientCore.MonoGame/Audio/SoundManager.cs`),
a `Dictionary<short,string>` next to the existing garrison constants. Covers
**exactly** the ids the real PCore001 station types decode for
`UnderAttackSound`/`CriticalSound` (dumped from PCore001.igc):

- **UnderAttackSound** 820→`vo_attack_garrison`, 821→`vo_attack_outpost`,
  822→`vo_attack_shipyard`, 823→`vo_attack_teleport`, 824→`vo_attack_refinery`,
  825→`vo_attack_expansion`, 826→`vo_attack_supremecy`, 827→`vo_attack_tactical`,
  828→`vo_attack_mine`, 829→`vo_attack_palisade`, 830→`vo_attack_research`,
  831→`dn_vo_attack_transeiver`.
- **CriticalSound** 840/841/842/843/844/845/846/847/848/851 → `vo_critical_station`.

Stem source: `~/Development/Artwork/sounddef.mdl` `<family>AttackedSound`
definitions (garrison `:1220`, outpost `:1221`, shipyard `:1222`, teleport
`:1223`, refinery `:1224`, expansion `:1225`, supremecy `:1226`, tactical
`:1227`, mine `:1228`, palisade `:1229`, research `:1230`, transceiver
`dn_tranAttackedSound` `:74`), each `ImportWave("<stem>")`; and
`garrisonCritical = ImportWave("vo_critical_station")` (`:1233`), which all
families share (incl. `dn_tranCritical` `:75`). PCore001 assigns mines the
refinery id (824) and palisade/research the expansion critical id (845) — core
quirks faithfully reproduced by the single-id table.

The client voice loop (`Game1.Session.ProcessIncomingQuickChatVoices`) plays the
clip whenever a chat's `VoiceOver` is in the table — by sound id alone, not by
caption match (the server's alert text is never a quick-chat caption). The chat
**text** already renders in the log via `ConnectionSession.cs` (`case
ChatMessage.Fmid:` → `ClientWorld.Events.Chats`, the same list the voice loop and
log walk); not restyled.

## Bundled .ogg files

Copied verbatim from `~/Development/Artwork/` into
`src/Allegiance.ClientCore.MonoGame/Content/Sounds/` and registered in
`Content/Content.mgcb` (OggImporter / SoundEffectProcessor / Quality=Best),
same mechanism as the four existing garrison clips:

`vo_attack_garrison.ogg`, `vo_attack_outpost.ogg`, `vo_attack_shipyard.ogg`,
`vo_attack_teleport.ogg`, `vo_attack_refinery.ogg`, `vo_attack_expansion.ogg`,
`vo_attack_supremecy.ogg`, `vo_attack_tactical.ogg`, `vo_attack_mine.ogg`,
`vo_attack_palisade.ogg`, `vo_attack_research.ogg`,
`dn_vo_attack_transeiver.ogg`, `vo_critical_station.ogg` (13 files).

## Tests

- **Wire-level** `StationUnderAttackAlertLiveTests` (3 tests) — two loopback
  clients on different sides; attacker fires a real missile at the defender's
  station through `ResolveCombatCollisions`. Asserts the defender gets the team
  `ChatMessage` (under-attack text + `VoiceOver == UnderAttackSound`), the
  attacker's side gets nothing, a second in-window hit is throttled, and a
  sub-30% pre-hit fraction after the window reopens yields the critical variant
  (`VoiceOver == CriticalSound`, "critically damaged"). Proven red before the fix.
- **Pure client table** `StationAttackVoiceTableTests` (14 tests) in the
  MonoGame tests project — every real PCore001 station `UnderAttackSound` /
  `CriticalSound` id resolves, criticals all map to `vo_critical_station`,
  family ids map to the correct clip, and every referenced stem is a bundled
  asset. (Demonstrated failing on the incomplete table — the 831/Transceiver gap.)

## Build / test status

- Whole-solution build `AllegianceCore.slnx -c Debug`:
  **0 warnings, 0 errors** (via `/home/domingo/.dotnet/dotnet`, not the broken
  snap stub).
- Full `Allegiance.Core.Tests` suite, 3 consecutive runs — see inline status
  below, naming any documented live-wire flakes observed.

### Full-suite runs (Allegiance.Core.Tests, 3573 tests)

Three full runs completed. The only failures in every run were **live-network
loopback timing tests** — and these are **pre-existing environmental flakes on
this machine, NOT P451 regressions**. Concretely:

- None of the flaked test classes reference any P451 symbol
  (`StationDamaged`/`DamageStationEvent`/`BroadcastStationDamaged`/
  `StationAttackVoices`/`LastDamageReport`).
- The **failure set changed between runs** (run 1: 11 failed / 3562 passed;
  run 2: 9 failed / 3564 passed; run 3: 14 failed / 3559 passed), the signature
  of socket/timing flakiness, not a deterministic bug — run 3's set
  (AutoReloadOnEmptyTests, ShipAsteroidCollisionTests, LiveRelaunchTests,
  AsteroidMinedLiveTests, LiveWinConditionTests, …) bore no overlap with run 1's.
- The worst offender was proven to fail **identically on the pristine pre-P451
  tree** (`189b6b47`): `ConquestTerritoryLiveTests.Conquest_win_in_one_mission_
  does_not_leak_to_another` fails 4/4 including fully isolated, and again in a
  detached worktree at `189b6b47` before any P451 change — conclusively a
  pre-existing environmental flake, not a regression.
- The skill's documented flakes (`LiveTurretGunnerBindingTests.Board_binds…`,
  conquest/win-condition live suites, etc.) are among the observed names.

Flaked-in-full-run names observed (live-wire timing): `ConquestTerritoryLiveTests`
(3 tests incl. `Conquest_win_in_one_mission_does_not_leak_to_another`,
`Flag_capture_tips_conquest…`, `Flags_disabled_capture…`),
`GameOverPlayersWireTests.Conquest_win…` (2), `LiveRipcordTests`,
`LiveMoneyTests.Board_refund…`, `SideEliminationLiveTests`,
`LiveTurretGunnerBindingTests.Board_binds…` (known persistent),
`LiveChatSectorWireTests` / other LiveNetwork timing tests as they roll.

**My P451 additions passed in every full run** — `StationUnderAttackAlertLiveTests`
(3) and, in the MonoGame tests project, `StationAttackVoiceTableTests` (14) —
and pass in isolation.

## Scope notes

- Native client-local station **hit sounds** (`clintlib.cpp:3075-3087`) are out
  of scope (possible follow-up), as are builder "under attack" lines, alliances,
  and P450/P452/P453/P454 (separate batch tasks).
- Non-ship launchers (not a `Ship`) do not trigger the alert; in practice every
  base attacker is a ship, and native's text/IsPlayer logic also assumes a ship.
- Client voice playback is a POC path (`PlayVoice`); the defense alert arrives
  with the correct `VoiceOver` id regardless of whether audio is enabled.
