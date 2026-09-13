# P201 — GAIN_FLAG(156): flag pickup, securing, and the real GAME_STATE fix

Stage: P201, landed on `master` (commit follows P200). Ports the native
flag-capture mechanic — pick up an enemy side's flag as a floating treasure,
carry it home, dock to secure it — with a whole-mission `GAIN_FLAG(156)`
announce, and corrects `GAME_STATE(155)`'s always-zero `nFlags`/`nArtifacts`.

---

## 1. Why this task is bigger than "a small treasure-docking notification"

Previous scope treated `GAIN_FLAG` as a side-notice. Investigation this stage
found it is the wire for a real capture mechanic with two legitimate emitters:

1. **Pickup** — `FedSrvSiteBase::HitTreasureEvent`'s `c_tcFlag` case
   (`FedSrv/FedSrv.CPP:13562-13575`): a ship collides with a floating
   `c_tcFlag` treasure, sets its carried-flag state to the treasure's side, and
   announces whole-mission. The ship's flag then follows it until docked.
2. **Secure** — `FedSrvSiteBase::DockWithStationEvent`'s flag block
   (`FedSrv/FedSrv.CPP:13111-13162`): a flag-carrying ship docking at its OWN
   side's station secures the flag — increments the side's `Flags`/`Artifacts`
   counter and announces with `sideidFlag = NA`.

The secure leg is the **direct, real source** of `GAME_STATE(155)`'s
`nFlags`/`nArtifacts` per-side counters. P191 had hardcoded those arrays to
zero on a finding that `IsideIGC::SetFlags`/`SetArtifacts` were "never called
in native." **That finding was incomplete** — they genuinely are called, from
this exact dock-securing handler. This stage both lands `GAIN_FLAG` and
corrects the always-zero fields to report real counters.

## 2. Native citations (verified against the live source this stage)

### Message shape — `Inc/Messages.h:861-866`

```c
DEFINE_FEDMSG(S, GAIN_FLAG, 156)
  SideID        sideidFlag;
  ShipID        shipidRecipient;
  bool          bIsTreasureDocked; // KGJV #118 - extended to notify docking of new tech (if true)
  PartID        parttypeidDocked;  // KGJV #118 - which part was docked, only valid if bIsTreasureDocked is true
END_FEDMSG
```

All of `SideID`/`ShipID`/`PartID` are `short` (`igc.h:477-480`) and `bool` is
one byte. The struct sits **outside** both `#pragma pack(push,1)` regions of
Messages.h (the two regions are at `Messages.h:636-642` and `:917-925`, neither
near this struct), so **default alignment** applies:

```
offset  0: ushort cbmsg
offset  2: ushort fmid = 156
offset  4: short  sideidFlag        (the carried flag's ORIGINAL side; NA on secure)
offset  6: short  shipidRecipient   (the carrier)
offset  8: bool   bIsTreasureDocked (1 byte)
offset  9: pad                      (align the trailing PartID short)
offset 10: short  parttypeidDocked  (only valid if bIsTreasureDocked)
= 12 B fixed
```

### Pickup — `FedSrv/FedSrv.CPP:13562-13575` (+ shipIGC.h:1339-1353)

```c
if (tc == c_tcFlag)
{
    if ((ship->GetFlag() != NA) || (oid == ship->GetSide()->GetObjectID()))
        return false;                                   // can't carry two / can't pick own side's flag
    BEGIN_PFM_CREATE(g.fm, pfmGain, S, GAIN_FLAG) END_PFM_CREATE
    pfmGain->sideidFlag = oid;                          // oid = GetTreasureID() = the ORIGINAL side
    pfmGain->shipidRecipient = ship->GetObjectID();
    pfmGain->bIsTreasureDocked = false;
    g.fm.SendMessages(m_pfsMission->GetGroupRealSides(), FM_GUARANTEED, FM_FLUSH);   // WHOLE-MISSION
    treasure->SetTreasureID(NA);                        // stop respawn
}
// ... ship->HitTreasure(tc, oid, amount) runs (the `else` of part/cash) →
//     IshipIGC::HitTreasure c_tcFlag case (shipIGC.h:1339-1353): m_sidFlag = oid
```

Two refusal gates (both faithfully ported): a ship already carrying a flag
(`GetFlag() != NA`) cannot pick up another; and a ship cannot pick up its own
side's flag (`oid == GetSide()->GetObjectID()`, where `oid` IS the treasure's
side). On refusal native returns `false` → the treasure is **not** destroyed.
On success native returns `true` → `KillTreasureEvent` destroys the treasure +
broadcasts `DESTROY_TREASURE` (whole-mission).

### Secure — `FedSrv/FedSrv.CPP:13111-13162`

```c
SideID sidFlag = pship->GetFlag();
if ((sidFlag != NA) && (pside == psideOld))   // pside = SHIP's side, psideOld = STATION's side
{
    if (sidFlag == SIDE_TEAMLOBBY)            // -2: a neutral artifact pickup
        { /* AddArtifact */ pside->SetArtifacts(pside->GetArtifacts() + 1); ...chat... }
    else                                      // a real enemy-side flag
        { /* AddFlag */    pside->SetFlags(pside->GetFlags() + 1);     ...chat... }
    pship->SetFlag(NA);                       // :13149 clear the carried flag
    BEGIN_PFM_CREATE(g.fm, pfmGain, S, GAIN_FLAG) END_PFM_CREATE
    pfmGain->sideidFlag = NA;                 // :13153
    pfmGain->shipidRecipient = pship->GetObjectID();
    pfmGain->bIsTreasureDocked = false;
    g.fm.SendMessages(m_pfsMission->GetGroupRealSides(), FM_GUARANTEED, FM_FLUSH);  // :13157 — WHOLE-MISSION
    IsideIGC* psideWin = m_pfsMission->CheckForVictoryByFlags(pside, sidFlag);      // separate win mode — out of scope
    RespawnFlag(sidFlag, pside);                                                    // respawns the flag — deferred (no spawn system)
}
```

**Broadcast-scope citation (the prompt asked to pin this):** the secure
`GAIN_FLAG` is sent to `m_pfsMission->GetGroupRealSides()` at
`FedSrv.CPP:13157` — **whole-mission**, exactly like the pickup (`:13573`).
Not side-wide. Both this port's `BroadcastGainFlag` calls sweep the acting
mission's peers.

`SIDE_TEAMLOBBY = -2` (`Igc/igc.h:514`); `pside == psideOld` means
ship's-side == station's-side (docking at your own side).

### Join-sync — `FedSrv/fsmission.cpp:740-762`

Right after `SendMissionInfo`, a newly-joined player is told about every
currently flag-carrying ship via one `GAIN_FLAG(sideidFlag = carried side,
shipidRecipient = carrier, bIsTreasureDocked = false)` per carrier, sent to the
joining player's OWN connection.

### Counter types — `Igc/sideigc.h:542-557,575-576`

`m_data.nFlags`/`m_data.nArtifacts` are `short`s, default 0. **These are the
fields P191 wrongly assumed dead.** They are incremented by the dock-secure
handler above.

## 3. This port's changes

| Area | Change |
|---|---|
| `Wire/GainFlag.cs` (new) | `GAIN_FLAG(156)` codec, `FixedSize = 12`, decode includes the tech-docking fields. |
| `Sim/Ship.cs` | `public short? Flag` — port of `m_sidFlag`/`GetFlag()`; `null` = NA. |
| `Model/Side.cs` | `public short Flags`, `public short Artifacts` (native short); `public const short ArtifactSentinelFlag = -2` (SIDE_TEAMLOBBY). |
| `Model/Sector.cs` | `ResolveTreasurePickups` recognizes `TreasureCode.Flag` with the two native refusal gates (a refusing pickup is simply not queued → treasure survives). |
| `Server.cs` | `PerformTreasurePickup` gains the flag branch (set `Ship.Flag`, whole-mission `GAIN_FLAG(sideidFlag=original side,…)`, then `BroadcastTreasureDestroyed`). Added `BroadcastGainFlag` (mission-scoped whole-mission send). `OnShipDocked` gains the secure leg. Join-sync sends a `GAIN_FLAG` per carrier. `GameState.ForMission` fills `NFlags`/`NArtifacts` from `Side.Flags`/`Side.Artifacts`. |
| `ServerHost.cs` | `ShipDocked` hook widened to `Action<Ship, short?>` carrying the station's side (so the server can apply the `pside == psideOld` gate; the station side is in scope at the dock site). |
| `Wire/GameState.cs` | Class remark + `ForMission` corrected: `nFlags`/`nArtifacts` carry real counters (the P201 correction), not P191's always-zero filler. |

## 4. How this port represents a team-lobby side (artifact vs flag branch)

The port has **no team-lobby side** — it creates real sides only (id ≥ 0).
Native's artifact branch keys on a carried value of `SIDE_TEAMLOBBY` (`-2`), a
neutral pickup rather than a captured enemy flag. In this port a flag treasure
carries a **real side's** object id as its value, so `-2` can never appear as a
carried flag → **the artifact branch is unreachable; only the real-enemy-flag
branch is exercised.** It is kept structurally faithful via
`Side.ArtifactSentinelFlag = -2`, so the branch is a one-value change if a
team-lobby concept is ever added. The report calls this out rather than
silently dropping the branch.

## 5. Divergences from the prompt's assumptions (all verified, not guessed)

1. **Secure broadcast scope — pinned.** The prompt flagged the
   `DockWithStationEvent` broadcast scope as "not fully pinned." Verified:
   whole-mission `GetGroupRealSides()` (`FedSrv.CPP:13157`), matching the
   pickup. Both broadcasts are mission-scoped in the port.
2. **`CheckForVictoryByFlags` after secure** (`FedSrv.CPP:13159`) is a separate
   flag-based *win-condition* mode native runs post-secure. Not in the task's
   enumerated scope (no flag-win mode in this port's mission params) — deferred,
   stated plainly.
3. **`RespawnFlag` after secure** (`FedSrv.CPP:13162`) respawns the secured
   flag. Deferred: this port has no treasure spawn/expiry system (P123), so the
   respawn leg is out of scope (consistent with the deferred out-of-bounds
   respawn path).
4. **Pickup also fires `DESTROY_TREASURE`.** Native returns `true` from
   `HitTreasureEvent` for a flag pickup → `KillTreasureEvent` destroys the
   treasure whole-mission. The port's flag branch calls
   `BroadcastTreasureDestroyed` (matching the prompt's "remove the treasure
   matching the DESTROY_TREASURE pattern").
5. **`OnShipDocked` needed the station's side.** The native gate is
   `pside == psideOld` where `psideOld` is the station's side; the port's
   dock seam therefore carries `ds.Station.SideId` into the hook
   (`Action<Ship, short?>`), a small signature widening.
6. **Team-lobby absent** (see §4).

## 6. Tests added (`tests/Allegiance.Core.Tests/LiveGainFlagTests.cs`,
`Wire/GainFlagWireTests.cs`)

- Pickup: rescue ship on side 0 collides with side 1's flag treasure → `Ship.Flag
  == 1`, treasure removed, `GAIN_FLAG(1, carrier, false, 0)` whole-mission to all
  three peers, `DESTROY_TREASURE` also fires.
- Refusals: own-side flag treasure is not picked up (treasure survives, no
  broadcast); a second flag while already carrying one is refused.
- **Secure + GameState (the key proof):** a flag-carrying ship docking at its own
  side's station clears `Ship.Flag`, increments `Side.Flags`, broadcasts
  `GAIN_FLAG(NA, …)` — and `GameState.ForMission(...).NFlags[ownSide] == 1`, not
  zero. Removing the GameState wiring makes this test fail (proved).
- Wrong-side dock: live — a flag carrier at a hostile station never docks/secures;
  and surgically — the private `OnShipDocked` gate rejects a differing station
  side and secures on a match (reflection).
- Join-sync: a player joining a mission with an existing carrier receives a
  `GAIN_FLAG` for it.
- Multi-mission scoping: a pickup in mission A reaches only mission A's peer;
  mission B's peer hears nothing (the `_peerShips` sweep is filtered by
  `Ship.Mission == acting mission`).
- Wire: `GainFlag` round-trips pickup/secure/tech-recovery shapes and emits the
  exact 12 declared bytes.

All three must-fail-if-removed proofs were run live: the GameState counter, the
pickup `GAIN_FLAG`, and the own-side refusal gate each turned their test red
when reverted.

## 7. Verification

- Build: clean (Core + Server + tests, `/home/domingo/.dotnet/dotnet build`).
- Full suite: **3011 passed** (baseline 3001 + 10 new), three fully-clean runs;
  one intermediate run showed two of the seven documented machine-state flakes
  (`LiveCombatWireTests.Real_Bot_class_observes_its_own_kill_over_the_wire`,
  `LiveInSpaceEjectTests.Already_a_lifepod_is_truly_killed_not_re_ejected`).
  The eject flake was green in isolation; the combat flake failed 3/3 in
  isolation **on the pristine tree** (git stash -u), confirming it is not a P201
  regression (it references none of this stage's symbols).
- P191 conquest/territory + GameState tests re-run green (13/13) — the `nFlags`
  correction does not regress the conquest/territory percentages those tests
  cover; the conquest scenario secures no flag so `NFlags` stays 0 there.
