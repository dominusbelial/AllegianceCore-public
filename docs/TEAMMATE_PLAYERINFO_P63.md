# P63 — broadcast a teammate's PLAYERINFO(10) too, not just its EXPORT

Repo: AllegianceCore, branch `master`. Pure server-side broadcast fix, same
shape as P62. No Windows VM / native `Allegiance` repo touched.

## Problem

P62 gave every same-side teammate ship a real `FM_S_EXPORT(26)` client-side
object. But a live pass after P62 found `UndockMinersAndStartMining` still
never completes once real teammate ships exist — no exception in any log,
connection pump keeps working, method's own diagnostic never prints.

Traced to the ground from native source, not guessed:

- `IshipIGCWrapper::GetPlayerInfo()` (`AllegianceInterop/igcWrapper.cpp:1980-1982`)
  returns `gcnew PlayerInfoWrapper((PlayerInfo *) m_instance->GetPrivateData())`.
- A ship's "private data" pointer is set in exactly one place in the whole
  native client: `clintlib/appmsg.cpp:2279`, inside the `FM_S_PLAYERINFO(10)`
  handler — `pPlayerInfo->SetShip(pship)`, which calls
  `pship->SetPrivateData((DWORD)this)` (`clintlib.h:311-314`).
- `AllegianceCore` only ever sent `PLAYERINFO` to the joining peer about its
  OWN ship (`Server.cs` join burst) — same narrow scope `EXPORT` had before
  P62. So after P62 created a teammate ship object, that object never got a
  `PlayerInfo` attached; `GetPlayerInfo()` returned a wrapper around a
  null/garbage native pointer.
- `UndockMinersAndStartMining`'s `p.GetPlayerInfo().LastSeenState()`
  (`CommanderResearchAndExpand.cs:1558`) dereferences it → AccessViolation,
  silently swallowed by `OnAppMessage`'s blanket per-message SEH handler
  (`NativeClient.cpp:29-45`). Explains every symptom precisely.

## Fix (what's in this commit)

In `BroadcastShipStates` (`src/Allegiance.Server/Server.cs`), inside the same
P58/P62 same-side teammate pass, after each pair's real `ExportShip` and
before any `SHIP_STATUS`, broadcast a real `PlayerInfo` (fmid 10) about that
same teammate's ship, once per (recipient, subject) pair, gated by a new
one-shot set `_teammatePlayerInfoSent` (kept separate from P62's
`_teammateExportSent` — two different one-time messages).

Field values match the existing own-player `PLAYERINFO` send exactly except
`ShipId`:
- `ShipId` = teammate's real ship id (the one its EXPORT used) — NOT the
  -1/NA own-ship trick. That NA is a documented dummy-adoption quirk specific
  to the self-referential own ship; there is no pre-existing dummy for a
  teammate, so a real, separate object must be created.
- `CharacterName` = teammate's real logon name (`_peerNames`, P62).
- `ISide` = teammate's own SideId.
- `Money = 0` (unchanged simplification — native `AddPlayerToSide`
  `assert(GetMoney()==0)` at `clintlib.cpp:4109`; NOT wiring the teammate's
  real payday balance).
- Everything else zero/default, as the existing own-player send.

## What's proven at the wire level (by this commit's loopback Bot test)

`Teammate_ship_playerinfo_is_broadcast_once_after_export_before_status`
(`tests/.../ShipStatusServerTests.cs`), two bots forced same-side:

- Observer decodes a `PlayerInfo` naming the subject's REAL ShipId (>0, not
  -1/NA), its real logon CharacterName, Money=0, ISide = subject's own side,
  PilotTypePlayer.
- Ordering: at the moment the first subject SHIP_STATUS is decoded, the
  subject's EXPORT and PLAYERINFO are both already present — EXPORT →
  PLAYERINFO → first status.
- Sent exactly once per (recipient, subject) pair across 350+ ticks (one-shot
  gate holds).
- Existing own-player `PLAYERINFO` send (join burst, `Server.cs:946-954`)
  is unchanged: each peer still gets one naming its OWN real ship id, once.
- Same-side scope only (ShipSpawner seats both same side; nothing cross-side
  is exercised or expected).

Build/test: `2282` passed, clean on 3 consecutive full runs.

## What Claude Code's live-VM pass must confirm next (NOT proven here)

This commit proves the wire message and its ordering. It does NOT run the
native client. Next live-VM pass must confirm:

1. Does a real commander's `GetPlayerInfo()` on a teammate's ship finally
   resolve NON-NULL (no more silent AccessViolation)? The mechanism (this
   PLAYERINFO reaches `appmsg.cpp:2279`'s `SetShip(pship)` → `SetPrivateData`)
   should now attach a real PlayerInfo to the ship EXPORT created.
2. Does `UndockMinersAndStartMining` finally COMPLETE and dispatch a real
   order to a real docked `.Miner`-named ship — the actual payoff this
   roadmap phase exists for?

These depend on native-client decode + strategy wiring and can only be
observed on the live client, so they are deliberately out of this commit's
scope and handed to the VM pass.
