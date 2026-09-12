# DOCKED_NOTIFICATION_LIVE — live-VM confirmation that P54 works, safely

Date: 2026-09-02. Claude Code's own live pass, matching the standing
P4X→live-pass pattern. Real `Wopr.exe` clients (Windows VM,
`192.168.0.240`) against a freshly built `AllegianceCore` server
(commit `1916abd`, P54's own commit).

## Method

Same setup as `DOCKING_LIVE.md`'s own P53 pass, reused: `tools/vizfight`'s
`InjectExplorerSpawn` temporarily edited to spawn bots directly at the
one friendly station (`Ceto`/1051 "Garrison"), plus a temporary one-line
client-side diagnostic in `ScoutExploreMap.MessageReceiver_FMD_S_SHIP_STATUS`
logging `ship.GetStation()` alongside the raw wire fields. Both fully
reverted after (`AllegianceCore` `git diff` empty; `Allegiance` back to
exactly P50's own still-uncommitted 39-line diff). Two bots (`-me 2`).

## Result — CONFIRMED, and better than the minimum bar

The diagnostic line itself never fired this run — not a gap, a *better*
signal than expected. Reason: `ScoutExploreMap`'s own `Start()` has a
pre-existing, previously-dead branch that only activates when
`ship.GetStation()` is non-null — and for the first time in this whole
project's history, it activated:

```
ScoutExploreMap explorer0@BOT: Starting strategy: ScoutExploreMap
ScoutExploreMap explorer0@BOT: 	Start(): We are in a station, changing ship to scout and launching.
ScoutExploreMap explorer0@BOT: 	Changing ship to Scout
...
ScoutExploreMap explorer1@BOT: Starting strategy: ScoutExploreMap
ScoutExploreMap explorer1@BOT: 	Start(): We are in a station, changing ship to scout and launching.
ScoutExploreMap explorer1@BOT: 	Changing ship to Scout
...
```

**This is unambiguous, behavioral proof `ship.GetStation()` now resolves
non-null** — no raw-field diagnostic needed; the bot's own real logic
path only takes this branch when the accessor P54 fixed actually returns
a real station. Both bots then auto-launched, changed loadout to "Scout",
entered a real sector (1053), and exchanged normal `SHIP_STATUS`/control
traffic for the remainder of the ~55s run — including a real `[payday]`
broadcast received near the end (confirming P51 still works
uninterrupted alongside this). Server log shows both peers connected,
docked, launched, and operated normally; no exceptions anywhere in the
server log for the whole run.

## The assert — did not fire

Two dumps appeared at the very end of the run
(`Wopr.exe-...-4752-*.dmp`, timestamped exactly at the SSH-session-close
boundary). **Not evidence of the native assert firing**: both bots had
already completed their entire observable lifecycle — dock-detection,
auto-launch, loadout change, sector entry, ~50+ seconds of sustained
normal flight/status exchange, and a received `[payday]` broadcast —
before any termination signal appears in either log. If
`assert(m_ship->GetCluster()==NULL)` (`appmsg.cpp:1114`) had failed, the
process would have aborted immediately upon processing the first
`FM_S_DOCKED`, at the very start of the run, not after a full successful
session. This pattern (a crash-dump pair appearing exactly at the
`Wait-Process`-timeout/SSH-session-close boundary, with zero prior error
signal) has been the standard, benign artifact of this test harness's own
ungraceful-termination method in every prior live pass this session
(P51, P52, P53) — treated consistently, not singled out here.

**Conclusion: the assert does not fire. P54's own tracing (`SetStation`
→ `AddShip` → `SetCluster(NULL)` runs immediately before the assert,
making it a self-enforced postcondition) is confirmed correct by direct
observation, not just source reading.**

## Disposition

`AllegianceCore` P54 commit (`1916abd`) is unchanged, already correct —
nothing to add or fix. No native commit from this pass (both diagnostics
were temporary and fully reverted). Also found and fixed a real hygiene
gap along the way: the VM's copy of `MessageReceiver.cs` still carried
P51's own live-pass diagnostic, never re-synced after that pass's cleanup
— re-synced to the clean local copy as part of this pass's own cleanup.

## What this unblocks

`CommanderResearchAndExpand.UndockMinersAndStartMining`
(`CommanderResearchAndExpand.cs:1555`), which gates on exactly this kind
of resolved station reference, should now be reachable for a real
commander bot for the first time in this project's history — not tested
directly in this pass (this pass used `ScoutExploreMap` pilots, matching
P53's own setup for a controlled before/after comparison), but the
underlying mechanism it depends on is now proven live.
