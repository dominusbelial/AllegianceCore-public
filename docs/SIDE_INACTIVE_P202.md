# P202 — SIDE_INACTIVE(68): pre-game roster shrinking

**Stage:** P202 · **Branch:** master · **Prior top commit:** `afb17ed` (P201)
**Suite:** 3011 → **3019** (+8: `LiveSideInactiveTests`)

Port of native `FM_CS_SIDE_INACTIVE` (`FedSrv/FedSrv.CPP:7379-7425`), the
pre-game roster-shrinking toggle: the **mission owner** marks an *empty* side
inactive/active before the mission starts, keeping the mission-wide
`AllowEmptyTeams` (AET) parameter in sync (ON iff at least one side is
inactive).

---

## What changed

**New wire codec** — `src/Allegiance.Core/Wire/SideInactive.cs`
(`FM_CS_SIDE_INACTIVE`, id 68, `Inc/Messages.h:532-536`). Verified against
the live `Messages.h`: this message sits **outside** the file's two
`#pragma pack(push, 1)` regions (lines 636-642 and 917-925), so it takes the
**natural MSVC alignment** of its members — the same family as the P186
`LockSides`/`LockLobby` codecs. Layout:

```
offset 0: ushort cbmsg
offset 2: ushort fmid = 68
offset 4: short  sideID      (SideID = ObjectID = short, igc.h:491)
offset 6: bool   bActive
offset 7: bool   bChangeAET
offset 8: bool   bAET
offset 9: 1 pad byte         (struct aligned to 2)
= 10 B fixed
```

The client request carries only `{sideID, bActive}`; `bChangeAET`/`bAET` are
filled by the **server's** reply (`FedSrv.CPP:7387-7414`).

**`Side.Active`** (`bool`, default `true`) — `src/Allegiance.Core/Model/Side.cs`
(the port of native `m_misdef.rgfActive[sideID]`,
`fsmission.cpp:5300-5306`; seeded true at `fsmission.cpp:59,3503`).

**`Mission.AllowEmptyTeams`** (`bool`, default `false`) —
`src/Allegiance.Core/Model/Mission.cs` (the port of
`m_misdef.misparms.bAllowEmptyTeams`).

**`SIDE_INACTIVE(CS, 68)` dispatch** — `src/Allegiance.Server/Server.cs`,
inserted after the `LOCK_SIDES` handler (P186 pattern: decode → peer →
mission → owner → stage gates, then act). Ports `FedSrv.CPP:7379-7425`
exactly:

1. **Owner gate** — `IsMissionOwnerPlayer` (P186), port of native
   `TrapHackBoot(pfsMission->GetOwner() == pfsPlayer)`, softened to
   log-and-refuse per port convention.
2. **Stage gate** — `Mission.Stage == NotStarted` only
   (`TrapHackBoot(pfsMission->GetStage() == STAGE_NOTSTARTED)`).
3. **`bObjectModelCreated` gate — OMITTED** (stated, not fabricated; see
   below).
4. **Empty-side re-check** — if the named side has no seated player
   (peer whose ship is in this mission and on that side — the same
   `_peerShips` presence query P178's win-check uses), set `Side.Active` to
   the requested value; **otherwise silently revert** `bActive` to the side's
   real current value (native's defensive re-check,
   `FedSrv.CPP:7387-7390` — not a refusal).
5. **AET recompute** — `mustChangeAET = (allActive && aet) ||
   (!allActive && !aet)`; when true, flip `Mission.AllowEmptyTeams` and fill
   `bChangeAET=true` / `bAET=new`, else `bChangeAET=false`
   (`FedSrv.CPP:7395-7414`).
6. **Forward whole-mission** the (possibly-corrected) message
   (`SendToMissionPeers`, port of `ForwardMessage(GetGroupMission(), ...,
   FM_GUARANTEED)`, `FedSrv.CPP:7416`).
7. **`UpdateAlliances(sid, NA)`** (P190) — clears that side's alliance
   membership (`FedSrv.CPP:7424-7425`).

**Bot** — `src/Allegiance.Bot/Bot.cs`: added a `SideInactiveBroadcasts`
accumulator (named differently from its element type to avoid the P186
CS1061 shadowing pitfall), its clear in `ResetLeadershipSignals`, a receive
`case SideInactive.Fmid`, and a `SendSideInactive(sideId, bActive)` helper.

**`UpdateAlliances` faithfulness wiring** — `src/Allegiance.Server/Server.cs`
(lines ~5168-5258). Verified against live native `fsmission.cpp:5638-5811`:
native `UpdateAlliances` consumes `GetSideActive(i)` in **three** places —
the new-allies gate (`fsmission.cpp:5675`), the propagate-loop that clears an
inactive side's mask and skips counting it (`fsmission.cpp:5691`), and the
solo-team count (`fsmission.cpp:5783`). Now that `Side.Active` is a real
field, all three are wired faithfully; since every side defaults to `Active`,
this is behavior-preserving for all pre-existing missions/tests (the P190
alliance suite passes unchanged).

---

## Scope-boundary confirmation (explicit re-checks)

- **Independent of P178's win-detection.** `CheckForVictoryByInactiveSides`
  (`Server.cs:1317`) is **untouched** — it still detects side-emptiness via
  connected-peer presence (`_peerShips`) and never reads `Side.Active`.
  Re-ran `LiveWinConditionTests` (all green) to confirm the new field does
  not interact with elimination detection.
- **Independent of P187's resign path.** `ApplyResign` / `DeactivateSide`
  were not touched; the in-game forced-elimination flow
  (`fsmission.cpp:5340-5380`) remains out of scope. Re-ran
  `BallotVotingLiveTests` (all green).
- `UpdateAlliances` *does* now read `Side.Active` (its native
  `GetSideActive` calls) — this is in-scope: `UpdateAlliances` is part of
  this feature (the `#ALLY` hook is step 7 of the handler) and the port had
  previously stubbed all-sides-active with a stated caveat; that caveat is now
  real.

---

## `bObjectModelCreated` omission decision

Native gate: `TrapHackBoot(!pfsMission->GetMissionDef()->misparms.
bObjectModelCreated)` (`FedSrv.CPP:7381`). `bObjectModelCreated` is a
`MissionParams` field marking whether the mission's sim objects have already
been generated. Native builds the object model on a later lobby-driven
"generate" trigger, so there is a live window (mission created, objects not
yet built) where the flag is **false** and the owner toggle is refused.

This port's `Mission.Assemble` builds the whole object model **once at boot**
— there is no separate "generate" moment, so there is **no reachable state**
where an equivalent flag would be false. Per the task brief, this gate is
therefore **skipped with a stated comment** in the handler (and documented
here), and **no** `bObjectModelCreated`-equivalent flag is fabricated.

---

## Divergences from this prompt's assumptions (verified against live source)

None material. The brief's quotes of `FedSrv.CPP:7379-7425` and the native
message struct were re-verified verbatim against the live checkout. The one
expansion beyond the brief's explicit text was the faithful wiring of
`Side.Active` into the three `GetSideActive` sites of the existing
`UpdateAlliances` port (see above) — justified because `UpdateAlliances` is
this feature's `#ALLY` hook, its port previously documented the
all-sides-active simplification as provisional, and the native source wires
them together. Behavior is preserved for all-active configurations.

---

## Verification

- Build clean: `Allegiance.Core`, `Allegiance.Server`, `Allegiance.Bot`,
  `Allegiance.Core.Tests` — 0 warnings / 0 errors.
- **New tests** (8, `LiveSideInactiveTests`): codec round-trip; owner
  deactivates empty side → `Side.Active=false` + `AllowEmptyTeams=true` +
  whole-mission broadcast with real `bChangeAET/bAET`; deactivating an
  occupied side is **silently reverted** (not refused); reactivating the last
  inactive side flips AET back to `false`; non-owner (even a side leader)
  refused; refused once `Started`; deactivating clears the side's alliance
  membership via `UpdateAlliances`; multi-mission broadcast scoping. All 8
  pass.
- **Must-fail-if-removed proofs:** (a) neutralizing the `side.Active`
  assignment → the happy-path test fails red; (b) honoring the request even
  for an occupied side → the silent-revert test fails red. Both restored.
- **3 full-suite runs** (3019 tests): run 1 → 2 failures, both
  `LiveCombatWireTests` (`Real_Bot_class_observes_its_own_kill_over_the_wire`,
  `Real_two_party_combat_produces_a_kill_ship_over_the_wire`) — the documented
  machine-state/socket-exhaustion flakes: `Real_two_party_combat` passed on
  the pristine tree and both pass clean in runs 2 & 3. Runs 2 & 3: **3019/3019
  green**.
- **Regression re-checks:** `LiveWinConditionTests` and `BallotVotingLiveTests`
  re-run green (combined with `LiveAllianceTests` + new suite: 37/37).
