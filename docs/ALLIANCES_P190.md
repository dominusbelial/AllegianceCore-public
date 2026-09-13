# ALLIANCES_P190.md

P190 — team alliances: the mission owner's `CHANGE_ALLIANCE(196)` / the
server's `CHANGE_ALLIANCES(197)`. Port of native `FM_C_CHANGE_ALLIANCE`
(`FedSrv/FedSrv.CPP:7469-7481`) and `CFSMission::UpdateAlliances`
(`FedSrv/fsmission.cpp:5638-5811`) into AllegianceCore. Everything below was
read from the live native source (`/home/domingo/Development/Allegiance`), not
guessed.

Relationship to the roadmap page `allegiance-fedmsg-roadmap.md` §"Alliances"
(2026-09-12): `CHANGE_ALLIANCE`/`CHANGE_ALLIANCES` were in the "genuinely
blocked, needs architectural scoping" bucket. Investigation confirmed alliance
state has **zero simulation coupling anywhere in native** — it is pure
broadcast state consumed only by a client UI team panel (`WinTrek/teampane.cpp:
389`) — so this is the smallest of the five subsystems scoped that session,
effectively a message-wiring task. This stage is that wiring: store +
broadcast is the complete faithful feature (native itself has no sim-side
consumer, so this port has none either — no friendly-fire/targeting/win
changes were invented).

---

## What changed

### Wire codecs (new) — `src/Allegiance.Core/Wire/`
- `ChangeAlliance.cs` — `FM_C_CHANGE_ALLIANCE` (id 196). `{SideID sideID@4,
  SideID sideAlly@6}` = 8 B fixed (`Messages.h:1017-1020`). Both `SideID`s are
  `short`. No padding (natural alignment).
- `ChangeAlliances.cs` — `FM_S_CHANGE_ALLIANCES` (id 197). `{char Allies[6]@4}`
  = 10 B fixed (`Messages.h:1022-1024`). `c_cSidesMax = 6` (`igc.h:507`); the
  per-side value is a signed char holding the side's alliance-group index
  (0..2) or `NA` (-1, wire byte `0xFF`). This follows the same fixed-size
  per-side array convention the port already uses (e.g. `ClusterInfo`'s fixed
  regions).

### Per-side alliance-group state — `src/Allegiance.Core/Model/Side.cs`
- Added `public sbyte? AllyGroup { get; set; }` — the port of native
  `m_misdef.rgfAllies[sideID]` (`fsmission.cpp:5288-5291 SetSideAllies`).
  `null` = no alliance (`NA`); otherwise two sides are allied iff they carry
  the **same** group value (a group id, not a bitmask). Starts `null`. Written
  only by the server's `CHANGE_ALLIANCE` handler. Chosen as a
  direct-field-on-`Side` (nullable `sbyte`) to match this port's existing
  per-side-field convention (`Ready`, `AutoAccept`, `LeaderShipId`).

### Server dispatch — `src/Allegiance.Server/Server.cs`
- New `else if (fmid == ChangeAlliance.Fmid)` branch in the inbound message
  chain. Gates, in order:
  1. sender has a ship, and that ship has a mission;
  2. **owner gate** — `IsMissionOwnerPlayer(sender.Ship, mission)` (P186's
     existing helper) — **deliberate divergence from native**, see below;
  3. **stage gate** — `mission.Stage == MissionStage.NotStarted` (native's one
     real gate, `FedSrv.CPP:7474`);
  4. **range gate** — `sideID`/`sideAlly` each must be `NA` (-1) or in
     `[0, mission.Sides.Count)` (native silently returns on bad ids,
     `fsmission.cpp:5642-5647`; here we log-and-ignore up front).
- On success calls the new `UpdateAlliances(mission, sideID, sideAlly)`, the
  port of native `CFSMission::UpdateAlliances` (`fsmission.cpp:5638-5811`).

### Server helper — `UpdateAlliances` (same file)
Full faithful port of the native merge/clear algorithm (algorithm quoted in
full below), storing the result back on each `Side.AllyGroup` and broadcasting
`CHANGE_ALLIANCES(197)` whole-mission via the existing `SendToMissionPeers`
(the exact whole-mission `_peerShips`-by-mission sweep every other broadcast
uses).

---

## The exact group merge / clear algorithm (native, quoted in full)

Source: `FedSrv/fsmission.cpp:5638-5811` (`CFSMission::UpdateAlliances`). This
is the algorithm ported verbatim to `Server.UpdateAlliances` (modulo the
"active" mapping discussed under divergences below). Note the sub-structure:
input filtering → current-state→bitmask conversion → apply the requested
change → propagate → (≥3 active sides) reset solo teams → stabilize → assign
group ids → validate → save+broadcast.

```c
void CFSMission::UpdateAlliances(SideID sideID,SideID sideAlly)
{
	// filter bad/corrupted values
	if (sideID != NA)
		if (!((sideID>=0) && (sideID<c_cSidesMax))) return;
	if (sideAlly != NA)
		if (!((sideAlly>=0) && (sideAlly<c_cSidesMax))) return;

	// convert alliance groups to bitmasks
	int AlliesMasks[c_cSidesMax];
	for (SideID i = 0; i <  c_cSidesMax ; i++)
	{
		if (GetSideAllies(i) != NA)
		{
			AlliesMasks[i] = 1<<i;
			for (SideID j = 0; j <  c_cSidesMax ; j++)
			{
				if (GetSideAllies(j) == GetSideAllies(i))
					AlliesMasks[i] |= 1<<j;
			}
		}
		else
			AlliesMasks[i] = 0;
	}

	if (sideID != NA) // we got a valid parameter sideID
	{
		if (GetSideActive(sideID) && (sideAlly != NA))
		{
		   // new allies = current allies + sideAlly + itself
		   AlliesMasks[sideID] = AlliesMasks[sideID]|(1<<sideAlly)|(1<<sideID);
		}
		else
			AlliesMasks[sideID]= 0;
	}

	SideID NumSides = GetCountSides();
	int activesides = 0;
	for (SideID i = 0; i <  NumSides ; i++)
	{
		if (!GetSideActive(i)) { AlliesMasks[i] = 0; continue; }
		activesides++;
		if (sideID == NA) continue;
		if (i==sideID) continue;
		if (AlliesMasks[sideID] & (1<<i)) // team sideID says it's allied with i
		{
			char merged = AlliesMasks[sideID] | AlliesMasks[i];
			AlliesMasks[sideID] = merged;
			AlliesMasks[i] = merged;
		}
		else
			AlliesMasks[i] = AlliesMasks[i] & ~(1<<sideID);
	}

	// build the final allied groups
	char Allies[c_cSidesMax]; for (SideID i = 0; i <  c_cSidesMax ; i++) Allies[i] = NA;

	if (activesides >=3) // need at least 3 active teams for alliances
	{
		for (SideID i = 0; i <  NumSides ; i++)
		{
			if (AlliesMasks[i] == (1<<i)) AlliesMasks[i] = 0;  // reset solo teams
		}
		// propagate the alliance till it's stable
		while (true)
		{
			bool stop = true;
			for (SideID i = 0; i <  NumSides ; i++)
			for (SideID j = 0; j <  NumSides ; j++)
			{
				int ai = AlliesMasks[i]; int aj = AlliesMasks[j];
				if (ai & aj)                      // i & j have common allies
				{
					int merged = ai | aj;
					if ((ai != merged) || (aj != merged))
					{ stop = false; AlliesMasks[i] = merged; AlliesMasks[j] = merged; }
				}
			}
			if (stop) break;
		}
		// create the different allied groups
		char groups[c_cAlliancesMax];
		for (int gr=0;gr<c_cAlliancesMax;gr++)  groups[gr]=0;
		int soloteams = 0; int numgroups = 0;
		for (SideID i = 0; i <  NumSides ; i++)
		{
			if (AlliesMasks[i] != 0)
			{
				bool found_group = false;
				for (int g=0;g<c_cAlliancesMax;g++)
				{
					if (AlliesMasks[i] == groups[g]) { Allies[i] = g; found_group = true; }
				}
				if (!found_group)
				{
					groups[numgroups] = AlliesMasks[i];
					Allies[i] = numgroups;
					numgroups++;
				}
			}
			else { Allies[i] = NA; if (GetSideActive(i)) soloteams++; }
		}
		// validation: no group at all OR no solo teams and only 1 group => invalid
		if ((numgroups==0) || ((soloteams==0) && (numgroups==1)))
			for (SideID i = 0; i <  c_cSidesMax ; i++) Allies[i] = NA;
	}

	// here we have valid alliances (in Allies[]) so we save them, broadcast
	// them to everyone and update the IGC sides
	BEGIN_PFM_CREATE(g.fm, pfmUpdateAlliances, S, CHANGE_ALLIANCES)
	END_PFM_CREATE
	for (SideID i = 0; i <  c_cSidesMax ; i++)
	{
		pfmUpdateAlliances->Allies[i] = Allies[i];
		SetSideAllies(i,Allies[i]);
		IsideIGC* side = m_pMission->GetSide(i);
		if (side) side->SetAllies(Allies[i]);
	}
	g.fm.SendMessages(GetGroupMission(), FM_GUARANTEED, FM_FLUSH);
}
```

Constant values used: `NA = -1` (`igc.h:471`), `c_cSidesMax = 6` (`igc.h:507`),
`c_cAlliancesMax = c_cSidesMax/2 = 3` (`igc.h:508`). The **semantics** of the
`Allies[]` group values the wire carries: side `i` is in alliance-group
`Allies[i]` (0..2); `Allies[i] == NA` means "no alliance". Two sides are allied
iff they carry the **same** non-`NA` group id — a group identifier, not a
bitmask (this is what the client UI panel consumes, `teampane.cpp:389`).

---

## Owner-gate divergence from native (deliberate)

Native `FM_C_CHANGE_ALLIANCE` (`FedSrv.CPP:7469-7481`) performs **no owner (or
even leader) check at all** — the only gate is `GetStage() == STAGE_NOTSTARTED`,
and the message's own comment ("sent by game owner") shows native trusts the
client UI to expose the control only to the owner. This port's established
convention (P184's `DELPOSITIONREQ` precedent, P186's `IsMissionOwnerPlayer`
gate) is to correct known native gaps rather than replicate them, so
`CHANGE_ALLIANCE` is additionally gated on `IsMissionOwnerPlayer` here (P186's
helper: sender must be the leader of `Mission.MissionOwnerSideId`). Stated
plainly, this is a hardening **beyond** native, not a faithful replication of
it; the refusal is logged (`[alliances] ... not the mission owner — ... refused
(divergence: native has no owner gate)`).

## Other divergences from this prompt's assumptions (verified against live native)

These are corrections to the task brief's own guesses, each verified against
the live native source rather than taken on faith:

1. **No "only-if-changed" broadcast guard.** The brief guessed native broadcasts
   "whenever the resulting `Allies[]` array actually differs from before." It
   does not. Native calls `SendMessages(GetGroupMission(), FM_GUARANTEED,
   FM_FLUSH)` **unconditionally** at the end of `UpdateAlliances`
   (`fsmission.cpp:5807`), even when the array is unchanged (e.g. a redundant
   `sideID == NA` normalization pass). This port faithfully replicates that:
   `UpdateAlliances` always broadcasts `CHANGE_ALLIANCES` whole-mission. The
   test suite accordingly asserts the broadcast fires on the happy path, the
   clear, and the merge.

2. **Three-sides-into-one-group is invalid without a 4th solo side.** The
   brief's proposed "merge three sides into one ally group" happy-path test
   would, with exactly 3 active sides, hit native's validation rule
   (`fsmission.cpp:5786-5792`): a single group covering **every** active side
   with **zero** solo teams is judged invalid and **cleared to all-NA**. A real
   three-into-one merge is therefore only a valid outcome when a fourth side
   remains solo (numgroups=1, soloteams=1). The merge test here uses a 4-team
   mission: merge sides 0,1,2 → `[0,0,0,NA]` with side 3 solo.

3. **"Active" = every assembled side.** Native's `GetSideActive`/`rgfActive`
   distinguishes active from inactive sides; this port has no inactive-side
   concept — every side in `Mission.Sides` is a real team. So `activesides`
   reduces to `mission.Sides.Count`, the `activesides >= 3` gate to
   `sides.Count >= 3`, and the `GetSideActive(sideID)` true-branch always
   taken for an in-range sideID. Documented so a reader knows the inactive-side
   branch of native's algorithm is intentionally elided, not forgotten.

## Scope boundaries

- **No sim-side consumer** — native has none (the only consumer is the client
  team-panel display), so none was invented. No friendly-fire, targeting, IFF,
  or win-condition change was added based on alliance state.
- `GAME_STATE(155)`'s `nFlags`/`nArtifacts` — untouched (separate
  conquest/territory item, per the roadmap).

## Tests — `tests/Allegiance.Core.Tests/LiveAllianceTests.cs` (8, all live)

- `Alliance_codecs_round_trip_exact_bytes` — both codecs: exact wire width,
  fmid, offsets, and the `NA = 0xFF` wire byte for `CHANGE_ALLIANCES`.
- `Owner_change_alliance_pre_start_sets_and_broadcasts` — owner allying side
  0↔1 while NotStarted stores `AllyGroup` on both sides and broadcasts
  `[0,0,NA,NA]` whole-mission (sender included).
- `Owner_gate_refuses_non_owner_even_team_leader` — another side's leader (a
  non-owner) is refused: no state change, no broadcast. **Must-fail-if-removed:
  disabling the owner gate turns this red** (proved via temporary sabotage).
- `Change_alliance_refused_once_started` — even the owner is refused once
  `Stage == Started`.
- `Change_alliance_out_of_range_refused` — `sideID`/`sideAlly` out of range are
  logged-and-ignored, no broadcast.
- `Side_ally_na_clears_existing_alliance` — `sideAlly == NA` clears side 0's
  alliance; the whole group dissolves and the broadcast reflects the clear.
- `Merge_three_sides_into_one_group_leaves_fourth_solo` — two `CHANGE_ALLIANCE`
  calls fold sides 0,1,2 into one group → `[0,0,0,NA]`, the real multi-side
  merge proof. **Must-fail-if-removed: disabling the merge propagation turns
  this red** (proved via temporary sabotage).
- `Multi_mission_change_alliances_scopes_per_mission` — the `CHANGE_ALLIANCES`
  broadcast and state in mission A never leak to mission B even when both share
  the same numeric side id.

Each new test's discriminating assertions were proved to fail when the
corresponding wiring is removed (owner gate, merge algorithm).

## Verification

- Build: `/home/domingo/.dotnet/dotnet build` clean (0 warnings, 0 errors).
- Suite grew 2929 → **2937** (8 new tests).
- `LiveAllianceTests` (all 8) pass in isolation.
- Full-solution `dotnet test`: **3 consecutive clean runs (runs 18/19/20), all
  2937/2937**, with the discriminant proofs above reverted beforehand. Across
  ~20 full-suite runs, the only failures ever observed were the documented
  environmental flakes — `LiveCombatWireTests.Real_Bot_class_observes_its_own_
  kill_over_the_wire`, `LiveCombatWireTests.Real_two_party_combat_produces_a_
  kill_ship_over_the_wire`, `LiveSuicideShipDeleteTests.Combat_death_produces_
  ship_delete_to_same_audience`, `LiveWinConditionTests.Multi_mission_
  disconnect_scopes_per_mission` — none of which reference any symbol introduced
  here (my symbols appear only in `LiveAllianceTests.cs`), and two of which
  were **proven to fail 2/2 on the pristine tree** (git stash + untracked files
  moved aside, run, restored) — i.e. the same machine-state flakes documented in
  P186/P188/P189, amplified by the concurrently-running Windows VM
  (qemu win11, ~32 GB RAM, 2 cores, ~10% CPU) at load-average ~2.2. No
  non-flake failure was ever observed.
