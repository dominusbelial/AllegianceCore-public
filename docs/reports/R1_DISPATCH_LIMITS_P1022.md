# P1022 — R1 F1 + F7: relay amplification budget and password/secret guessing limits

Owner findings: **F1** (several relays not charged to the per-peer message budget) and **F7** (game password and the
admin/bot/harness bearer secrets guessable without limit) from Fable's review `R1_REPORT.md`.

Base: Claude's master at dispatch (`dbf2db91`, the R1 review commit that carries the skipped proof tests).
Status: complete in the working tree, **not committed**.

---

## 0. Answers in one paragraph

- **F1.** Seven relays the dispatcher built and fanned out without touching the P654 per-peer budget are now charged
  to that same budget (`TryChargeChatBudget`) before anything is built: POSITIONREQ (both the auto-accept JOIN_SIDE
  leg and the pending POSITIONREQ(40) leg), SET_WINGID, SWAP_PART, PLAYER_DONATE, BUCKET_DONATE, the leader's
  `#autoaccept` / owner's `#autobalance` whole-mission MISSIONDEF, and the owner's SIDE_INACTIVE. **None of them is
  dropped for a legitimate client** and none is silently swallowed: the whole-mission relay is charged, then built
  exactly as before; only a peer that has spent its bucket loses the relay. The brief's second bullet asked for the
  no-op cases to be *dropped outright only if native would not relay them either* — **checked in native, native
  relays all four**, so they are charged and kept (cites and reasoning in §1.3): a same-side POSITIONREQ
  (`fsmission.cpp:4749-4769`: RemovePlayerFromSide then the pending broadcast), a mount-old == mount-new SWAP_PART
  (`FedSrv.CPP:6480-6498`: the same part object, `SetMountID` runs and `ForwardMessage` still fires), a
  self-donation (`FedSrv.CPP:7232-7246`: same-side passes, the wallet nets zero and the MONEY_CHANGE still goes to
  the sender's side group) and a zero BUCKET_DONATE (`FedSrv.CPP:7146-7200`: `moneyGiven >= 0` passes, both
  MONEY_CHANGE(0) and BUCKET_STATUS are sent).
- **F7.** Every server — password-protected or not — now bounds credential guessing. `credentialGuessLimits`
  (`server.json`, **default ON**) gives: 3 failed credentials retire that connection; 5 failures from one source
  address inside a 60 s window start a back-off of 15 s that **doubles** on each re-arm up to 300 s. During a
  back-off the peer's LOGONREQ is dropped **without evaluating the credential** (no comparison, no reply, no
  oracle). The same counting covers the bearer secrets: a claimed admin/harness role with a wrong `CDKey`
  (`AuthenticatePrivileges`) and a wrong bearer presented in front of a known cheat command
  (`Server.AdminCheats.cs`). A **correct credential is never counted and never slowed**; an ordinary client's
  placeholder CD key is not a guess (see §2.3). The per-address table is capped (`maxTrackedAddresses`, 4096) and
  pruned, and the per-connection count is retired with the peer on every release path. Native has **no** throttle
  here (`FedSrv.CPP:648-661`, `:664`): all of F7 is deliberate port hardening.
- **Tests.** The three proof tests are unskipped unchanged (`One_packet_of_POSITIONREQs_…`,
  `One_packet_of_SET_WINGIDs_…`, `Wrong_game_passwords_on_one_connection_are_throttled`) and pass; 23 focused
  tests were added (`P1022RelayBudgetTests`, 8; `P1022CredentialGuessTests`, 15 = 7 pure-guard + 8 wired; exact
  counts in §4). Legitimate play is proven unaffected: a single request still relays exactly once, a
  correct password/secret is admitted immediately, a whole lobby of ordinary clients from one address is admitted,
  and the config-off arm is the old unthrottled path.

---

## 1. F1 — relays that were not charged to the per-peer message budget

### 1.1 Root cause

`TryChargeChatBudget` (`src/Allegiance.Server/Server.Dispatch.cs:2524`, the port of native
`CFSPlayer::DecrementChatBudget`, `FedSrv/fsship.h:455`) is charged by chat (paid unless CHAT_GROUP_NOECHO,
`FedSrv.CPP:3372`), REQUEST_MONEY (`FedSrv.CPP:5370`, already charged at `Server.Economy.cs:264`), buoy chat, ready,
highlight, lock-sides/lock-lobby, AUTO_ACCEPT, and the ballot/QUIT paths. The relays below reached the same
whole-mission/side-wide/sector-wide fan-out with **no charge at all**, so one 1.5 KB packet of 256 copies could
make the sim thread build and queue 256 × N guaranteed frames per tick (`R1_REPORT.md` F1, H-05 amplification).

### 1.2 Fix — charge before the relay is built

Each site charges **before** the state change and before the encode, so a denied charge is a complete no-op (no
half-applied state, no announcement without the mutation):

| Relay | Port site (after the fix) | What is charged |
|---|---|---|
| POSITIONREQ (auto-accept JOIN_SIDE, pending POSITIONREQ(40), QUIT_SIDE from the park, SET_TEAM_LEADER / SET_MISSION_OWNER) | `src/Allegiance.Server/Server.Dispatch.cs:1042-1054` | one paid send per request, before `TryJoinSide` / `BroadcastPositionReq` |
| SET_WINGID (side-wide) | `src/Allegiance.Server/Server.ShipCommands.cs:944-951` | one paid send before the packet is built |
| SWAP_PART (sector-wide) | `src/Allegiance.Server/Server.ShipCommands.cs:414-430` | one paid send before `TrySwapPartWithCargo` mutates |
| PLAYER_DONATE (side-wide MONEY_CHANGE) | `src/Allegiance.Server/Server.Economy.cs:365-377` | one paid send inside the real-transfer branch, before the wallets move (the requester-only bounce-back echo is one frame, not a fan-out, and is not charged) |
| BUCKET_DONATE (side-wide MONEY_CHANGE + BUCKET_STATUS, mission-wide for the team-money tech) | `src/Allegiance.Server/Server.Economy.cs:594-607` | one paid send before `bucket.AddMoney` |
| leader `#autoaccept` MISSIONDEF (+ every JOIN_SIDE the auto-accept sweep seats) | `src/Allegiance.Server/Server.DispatchParity.cs:40-53` | one paid send; a denied charge consumes the line (no mission def, no chat echo) |
| owner `#autobalance` MISSIONDEF | `src/Allegiance.Server/Server.DispatchParity.cs:57-70` | same (same class of flaw, fixed with its sibling) |
| SIDE_INACTIVE (whole mission) | `src/Allegiance.Server/Server.Dispatch.cs:1597-1604` | one paid send before any side/AET state changes |

The effect matches the P654 pattern exactly: the ledger charge is `cost = 20` from `max = 1000` (dedicated
profile; the `native` profile is 100/20), refilled `+5`/tick, so a fresh peer may relay ~50 charged events per
tick burst and a legitimate client (a handful per minute) is never affected. The proof tests measure exactly that:
256 copies → at most `64 × audience` frames; in the headless fixture the budget's fair share produced 200
whole-mission frames for POSITIONREQ and 100 side-wide for SET_WINGID (both ≤ the 256/128 bounds).

### 1.3 Native check for the "drop the no-op cases" bullet ("check native and say")

| Case | Native behaviour (re-grepped) | Decision |
|---|---|---|
| POSITIONREQ naming the side the ship is already seated on | `CFSMission::RequestPosition` removes the player from the current side (`fsmission.cpp:4749-4751` → `RemovePlayerFromSide(QSR_SwitchingSides)`), then either `AddPlayerToSide` (auto-accept/leaderless) or broadcasts POSITIONREQ(40) whole-mission and pends (`fsmission.cpp:4757-4769`). Native **relays** it (twice: QUIT_SIDE + JOIN_SIDE/POSITIONREQ). | **Charged, not dropped** (parity). |
| SWAP_PART with `mountOld == mountNew` | `FedSrv.CPP:6480-6498`: `ppartNew` resolves to the same part, the two `SetMountID` calls end where they started, and the `ForwardMessage(GetGroupSectorFlying(pcluster), …)` block runs unconditionally inside `if (ppart)`. Native **relays** it. | **Charged, not dropped** (parity). |
| PLAYER_DONATE to oneself (`moneyGiven > 0`) | `FedSrv.CPP:7232-7246`: the same-side test passes, both wallets move (net zero) and `prcp = CFSSide::…(own side)` is announced — the sender's side group receives the MONEY_CHANGE. Native **relays** it. | **Charged, not dropped** (parity). |
| BUCKET_DONATE with `moneyGiven == 0` | `FedSrv.CPP:7146-7200`: `TrapHackBoot(moneyGiven >= 0)` passes, `AddMoney(0)` caps at 0, the side-wide MONEY_CHANGE(dMoney=0) and the BUCKET_STATUS both go out. Native **relays** it. | **Charged, not dropped** (parity). |

A PLAYER_DONATE with `moneyGiven <= 0` and a BUCKET_DONATE with a negative amount are already refused outright
before any relay (the port's softened trap, `FedSrv.CPP:7220`, `:7149`) — that is the only "dropped outright" case,
and it exists because native **boots** the client there.

**Siblings checked, left unchanged (stated):** the owner's MISSIONPARAMS whole-mission MISSIONDEF
(`src/Allegiance.Server/Server.MissionParams.cs:230`) has the same shape but is not in F1's list and is owner-only
(one change per settings edit); and the leader's POSITIONACK accept (`Server.Dispatch.cs:1139-1154`) is naturally
bounded — it seats only through `RemovePendingJoinRequest`, so a repeated accept of the same request is a no-op
(there is at most one relay per pending request, and requests are per requester). Both are left alone to stay inside
the finding.

### 1.4 Native parity verdict for F1

**Deliberate port hardening, not parity.** Native charges its chat budget only for chat and REQUEST_MONEY
(`FedSrv.CPP:3372`, `:5370`), so these relays are unbudgeted there too (R1 says the same). Charging them is the
port's P654 policy (H-05/H-24) extended to the remaining fan-outs; the wire for a legitimate client is unchanged
except that an exhausted bucket now drops the relay instead of amplifying. Rows updated in
`docs/parity/p867-onappmessage.csv` (FM_C_POSITIONREQ, FM_CS_SET_WINGID, FM_CS_SWAP_PART, FM_C_PLAYER_DONATE,
FM_C_BUCKET_DONATE, FM_CS_SIDE_INACTIVE, FM_CS_CHATMESSAGE) — the three "equal" rows are now `classified` with the
budget difference named.

### 1.5 F1 tests

- Unskipped, unweakened: `R1ReviewTests.One_packet_of_POSITIONREQs_does_not_broadcast_once_per_copy` (256 copies,
  relays ≤ 64 × 4) and `R1ReviewTests.One_packet_of_SET_WINGIDs_does_not_broadcast_once_per_copy` (≤ 64 × 2).
  Same inputs, same assertions as the review wrote.
- New (`tests/Allegiance.Core.Tests/P1022RelayBudgetTests.cs`, 8 tests): a legitimate POSITIONREQ still relays
  exactly once on the auto-accept leg (JOIN_SIDE ×4) and on the pending leg (POSITIONREQ(40) ×4); and the six
  code-reading cases are each bounded at packet size while still relaying at least once — same-side POSITIONREQ,
  same-mount SWAP_PART, self-donation, zero bucket donation, `#autoaccept` MISSIONDEF, SIDE_INACTIVE.

---

## 2. F7 — password and bearer-secret guessing limits

### 2.1 Root cause

- `src/Allegiance.Server/Server.MissionCookies.cs` `JoinMission` compares the game password and, on a mismatch,
  sends `DELPOSITIONREQ(DPR_BadPassword)` immediately; the only other reaction was the P970 rate-limited log line.
- `src/Allegiance.Server/Server.Dispatch.cs:354-355` (pre-fix) removed the pending entry and kept the connection
  open, so every `LOGONREQ` on it was a fresh attempt — 2000 guesses in one tick in the proof test (125 ms).
- `AuthenticatePrivileges` (`Server.Privileges.cs:24`) compared the admin/harness bearer GUIDs on every validated
  logon, and the admin-chat bootstrap compared a chat line's first token (`Server.AdminCheats.cs:137`), both with no
  counting.
- Native has no throttle anywhere here: the password is compared and refused at the join
  (`FedSrv/FedSrv.CPP:648-661`), the GUID at `:664` (`CanCheat`/`UTL::PrivilegedUser`) — so a limit is a deliberate
  port addition (R1 F7 says the same).

### 2.2 Fix

New `src/Allegiance.Server/CredentialGuessGuard.cs` — the pure counter (no clock of its own, a caller-supplied
`now`, no locking: it is only touched on the sim thread inside the dispatch):

- **per connection**: failures are counted; at `maxFailuresPerConnection` (3) the counter reports
  `CloseConnection` exactly once and the server retires the connection through the existing H-14 path
  (`AfterQueuedSends(() => transport.ClosePeerAfterFlush(peer))`). The count is also kept so a synthetic/queued
  frame on that connection is never evaluated again even if the socket close is not observable.
- **per source address**: `failuresPerWindow` (5) inside `windowSeconds` (60) start a back-off of
  `baseLockoutSeconds` (15), **doubling** on each re-arm and capped at `maxLockoutSeconds` (300). A quiet window
  since the last failure/back-off resets the escalation. Failures are counted across credential kinds; a success
  never resets the address window (a valid credential must not reset a NAT'd address's guessing history).
- **bounded**: the address table is capped at `maxTrackedAddresses` (4096) — expired entries are pruned, then the
  least-recently-touched one is evicted; the per-connection entries are retired with the peer in
  `ReleasePeerWithReason` (`Server.Lifecycle.cs:2251`), the same owner that retires the P654 chat budget.

Wiring (`src/Allegiance.Server/Server.CredentialLimits.cs`, `ResolveCredentialGuessLimits` called at boot from
`Server.Lifecycle.cs:220` and bannered):

| Path | Site | Behaviour |
|---|---|---|
| logon gate | `Server.Dispatch.cs:119-135` | a connection/address in back-off drops the LOGONREQ with no evaluation and no reply (`[lockout]`, rate-limited) |
| game password | `Server.Dispatch.cs:366-389` + `Server.MissionCookies.cs:30-58` | only `DPR_BadPassword` counts (a no-mission/paused refusal is not a guess); a correct password clears the connection's count |
| bearer GUIDs | `Server.Privileges.cs:39-85` | a non-matching bearer counts **only for a peer that claims the role** (admin allow-name, harness prefix); a matching bearer is a success |
| admin chat bootstrap | `Server.AdminCheats.cs:158-183` + `LooksLikeSecretPrefixedCommand` at `:103-118` | a wrong bearer in front of a known cheat token (`relocate`/`timescale`/`egress`) counts; a correct bearer authenticates and counts nothing |
| address provenance | `Server.CredentialLimits.cs` `PeerAddressKey` → `TcpTransport.PeerAddress` (existed), new `UdpTransport.PeerAddress` / `DualTransport.PeerAddress` | the transport's accept-time address, never greeting data; a peer with no resolvable address falls back to its connection id so unrelated peers can never share one lockout bucket |

The server clock is the sim clock (`SimNow`), so a lockout is time-based and never a sleep; the tests drive it by
setting the clock forward (pure-guard tests pass `now` directly).

### 2.3 What a "wrong secret" is (and what is deliberately not counted)

An ordinary client of this port presents a **fixed placeholder** CD key (`Game1.Session.cs` "ABCDEFGHIJKL",
`Bot/Program.cs` "AAAABBBBCCCC"), so counting every non-matching `CDKey` would throttle every player, every bot and
everyone behind one NAT — the exact opposite of "correct credentials are never slowed". A failure is therefore
counted only for a peer that **claims** the role it is guessing:

- admin secret: the name matches `liveSettings.adminCheats.allowNames` (the same `AdminSpectatorAllowed` filter the
  bootstrap uses);
- harness secret: the name carries `liveSettings.testHarness.characterNamePrefix`;
- admin chat: the line is a bearer in front of a recognised cheat token (ordinary chat, and a privileged peer's
  own commands after authenticating, never count).

An **empty** CD key is not an attempt at all. The **bot-role** secret (`BotAuthenticationSecret`) has no name or
prefix claim on the wire, so a wrong bot CD key is indistinguishable from every legitimate client's placeholder —
it is **not** counted, and is stated here as the residual: bounding it needs an explicit bot-role marker in
`LOGONREQ`, which is a protocol change outside this finding. Loopback is **not** exempt from the counting, but since
nothing is recorded on a correct credential, bots, the harness, local skirmish/training and the rig are unaffected.

### 2.4 Settings

`server.json` section `credentialGuessLimits` (validated at boot by `CredentialGuessLimitsConfig.Validate`), present
in `src/Allegiance.Server/server.example.json`, `server.example.linux.json` and `server.example.windows.json`:

```json
"credentialGuessLimits": {
  "enabled": true,
  "maxFailuresPerConnection": 3,
  "failuresPerWindow": 5,
  "windowSeconds": 60,
  "baseLockoutSeconds": 15,
  "maxLockoutSeconds": 300,
  "maxTrackedAddresses": 4096
}
```

Defaults are **on** in code as well (`CredentialGuessLimitsConfig` initialisers), so an absent section bounds
guessing. `enabled=false` restores the unthrottled native path (the config-off control arm is a test). One
rate-limited `[lockout]` line per lockout/close event carries the address, count, window and duration — never the
attempted or the real secret.

### 2.5 Native parity verdict for F7

**Deliberate port hardening, no native equivalent** (`FedSrv.CPP:648-661`, `:664` answer at once; R1 F7 says so).
Domingo's R1 decision: limit guessing on every server, not only password-protected ones. Rows added to
`docs/parity/p970-join-password.csv`: the password guessing limit, the bearer-secret counting, and the bounded
per-address state — all `n/a` against native, classified "era constraint: modern port addition".

### 2.6 F7 tests

- Unskipped, unweakened: `R1ReviewTests.Wrong_game_passwords_on_one_connection_are_throttled` (2000 attempts on
  one connection, ≤ 20 evaluated and answered) — it now answers exactly 3.
- New (`tests/Allegiance.Core.Tests/P1022CredentialGuessTests.cs`, 15 tests):
  - pure guard (`P1022CredentialGuessGuardTests`, 7): five-in-window lock + expiry, a correct credential is never
    counted (and clears only the connection), a second address unaffected, doubling and the cap
    (15/30/60/120/240/300/300), the address table bounded (cap 8, evictions counted), a connection retired exactly
    once at its third failure, config-off counts nothing;
  - wired (`P1022CredentialGuessTests`, 8): wrong passwords close the connection after exactly 3 answers;
    config-off answers all 50; a second address is unaffected while the first is locked; the correct password is
    refused during the back-off and admitted once the clock passes it; a wrong admin-chat secret counts and retires
    the connection; a correct one never counts; a wrong harness bearer at logon locks its address while another
    address with the right secret is admitted; ten ordinary placeholder-CD-key logons from one address are all
    admitted.

### 2.7 One existing test adapted (and why)

`tests/Allegiance.Core.Tests/P970JoinPasswordTests.A_refusal_is_logged_once_with_the_pilot_and_no_password_text`
sends 13 bad passwords in a row from **loopback** and asserts every one of them gets a refusal. With the limits ON
the 6th is (by design) not evaluated — the address is in back-off — so the test's pump timed out in the first Fast
sweep. Its subject is the P970 **log** gate (one line per refusal, neither password printed, repeats rate-limited),
not the guessing limit, which has its own tests here. The minimal, non-weakening change: that one test boots with
`credentialGuessLimits.enabled=false` and every assertion is unchanged (documented in the test's summary). No limit
was raised to make it pass. The other P970 tests (one refusal, then a correct retry) are unaffected and pass with
the limits ON.

The three unskipped proof tests keep the `[SkippableFact]` **shape** (only the `Skip = "R1 finding …"` argument is
gone) because `CleanCloneSkipGuardTests` requires a corpus-backed test to skip cleanly on a clone without the
artwork; they are executed, not skipped, in every run here.

---

## 3. Files changed

Product: `src/Allegiance.Server/CredentialGuessGuard.cs` (new), `src/Allegiance.Server/Server.CredentialLimits.cs`
(new), `Server.Dispatch.cs`, `Server.DispatchParity.cs`, `Server.ShipCommands.cs`, `Server.Economy.cs`,
`Server.MissionCookies.cs`, `Server.Privileges.cs`, `Server.AdminCheats.cs`, `Server.Lifecycle.cs`, `ServerConfig.cs`,
`ServerJsonContext.cs`, `src/Allegiance.Core/Transport/UdpTransport.cs` (`PeerAddress`), `DualTransport.cs`
(`PeerAddress`), the three `server.example*.json` files.

Tests: `tests/Allegiance.Core.Tests/P1022RelayBudgetTests.cs` (new), `P1022CredentialGuessTests.cs` (new),
`R1ReviewTests.cs` (three `Skip`s removed, nothing else).

Docs: this report, `GLOSSARY.md` (new entry), `docs/parity/p867-onappmessage.csv`,
`docs/parity/p970-join-password.csv`.

---

## 4. Test counts and gate

### 4.1 New/unskipped tests, 3× alone

`dotnet test tests/Allegiance.Core.Tests --no-build --filter "FullyQualifiedName~P1022|…One_packet|…Wrong_game"`
run three times back to back on the final tree:

```
RUN 1: Passed! - Failed: 0, Passed: 26, Skipped: 0, Total: 26 (533 ms)
RUN 2: Passed! - Failed: 0, Passed: 26, Skipped: 0, Total: 26 (545 ms)
RUN 3: Passed! - Failed: 0, Passed: 26, Skipped: 0, Total: 26 (539 ms)
```

(The 26 = 8 `P1022RelayBudgetTests` + 15 `P1022CredentialGuessTests` + the 3 unskipped R1 proof tests; the 6 skips
in the same project belong to the other R1 findings, untouched.)

### 4.2 Build and tools

- `dotnet build AllegianceCore.slnx --disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`
  → **Build succeeded, 0 Warning(s), 0 Error(s)**.
- `tools/build-tools.sh` → **all 140 tool projects built, 0 Warning(s), 0 Error(s)**.
- AOT publish (Allegiance.Server changed):
  `dotnet publish src/Allegiance.Server/Allegiance.Server.csproj -c Release -r linux-x64 --self-contained
  -p:PublishAot=true` → succeeded, **no IL2026/IL3050**; native binary produced.

### 4.3 Existing coverage of the touched paths, run and passing

Named classes the change relies on, all inside the Fast sweep below: `P762ChatBudgetTests` (the P654 budget
semantics), `SetWingIdTests`, `SwapPartTests`, `PlayerDonateTests`, `BucketWireTests`, `LivePositionRequestTests`,
`LiveSideInactiveTests`, `LiveChatWireTests`, `LiveTeamLobbyTests`, `P602AdminCheatSafetyTests`,
`P867DispatchCertificationTests` (the dispatch certification — every changed handler), `P970JoinPasswordTests`
(the join gate; see §2.7), `P655*` privilege/identity tests, and the transport suites that exercise the new
`PeerAddress` accessors (`Transport.P895UdpTransportTests`, `Transport.P898*`).

### 4.4 Full Fast tier (one run, on the final tree)

`tools/run-tests.sh --tier fast --no-build`:

```
Allegiance.Update.Tests              55     Passed 55
Allegiance.Lobby.Tests              150     Passed 144, NotExecuted 6
Allegiance.ClientCore.MonoGame.Tests 5475   Passed 5471, NotExecuted 4
Allegiance.ClientCore.Tests         2451    Passed 2451
Allegiance.Core.Tests               6430    Passed 6421, NotExecuted 9
TOTAL                              14561   Passed 14542, FAILED 0, NotExecuted 19
```

**Zero failures** (the 19 `NotExecuted` are Live/Perf-tier rows filtered out of the Fast selection). Calibrated
Fast case limit 11.543 s; five executions exceeded it — `SC2NaniteSupportTests…(civ: 25)` 14.0 s,
`P814GunnerTests.P814b…` 13.1 s, `P968PublisherStreamIndexTests…(400 peers…)` 11.7 s,
`P999CivStrategyTests…(civ: 5)` 20.9 s, `P750NativeAsteroidTypesTests…` 12.2 s — every one an already-ledgered
timing family (`docs/TEST_FLAKES.md`), each retried alone by the runner's quarantine and **passed**: the sweep's
verdict line is `FAIL` on the timing error only, not on any assertion.

An earlier sweep of the same tree found two issues that are now fixed and re-run green above: the clean-clone guard
rejecting the unskipped proof tests' `[Fact]` shape (§2.7) and the P970 log test's 13 loopback refusals (§2.7). The
one assertion flake it reported, `P890OwnAimLoopbackTests.Turning_and_firing_preserve_client_aim` (the `model: "native"` arm,
"skirmish/native sustained aim drift"), passed **4/4 in each of three isolated runs** at load average 0.74 — the
ledgered load flake (`docs/TEST_FLAKES.md`, same signature recorded there), and it passed in the final sweep too.

Evidence (outside the repo, per the lane rule):
`~/allegiance-evidence/P1022/` — `fast-tier2.log`, `tiers-fast2/summary.json` + `summary.txt`, `runs3.log`,
`build.log`, `build-tools.log`, `aot.log`, `scorecard.txt`.

---

## Native intent

Native refuses a wrong game password immediately, at the join, before side selection (`FedSrv/FedSrv.CPP:648-661`,
`Strcmp` → `_Utility/Utility.h:1389-1392`) and compares the admin/harness GUIDs as they arrive
(`FedSrv.CPP:664`, `:5817`); it has **no** counting, delay or lockout for either. Native's one anti-amplification
budget is the per-player chat token bucket (`CFSPlayer::DecrementChatBudget`, `fsship.h:455`,
`FedSrv.CPP:3372`), charged for chat and REQUEST_MONEY only — every POSITIONREQ, SET_WINGID, SWAP_PART,
PLAYER_DONATE, BUCKET_DONATE, `#autoaccept` MISSIONDEF and SIDE_INACTIVE relay in native is unbudgeted, and every
no-op variant of those (same-side request, same-mount swap, self-donation, zero bucket donation) is still relayed.
The port keeps that intent — a legitimate client's relay is never dropped or altered — and adds the two limits R1
asked for as explicit, documented hardening: the P654 budget extended to those relays, and a credential-guessing
budget (per connection, per source address, doubling back-off, bounded) in front of the password and bearer
comparisons.

## Modern approach

Both halves are *state on the server*, not a wire change. F1 is a ledger charge before the encode, so no frame
grows or shrinks and no message id changes; a legitimate burst fits the same 1000/20/+5 bucket that already governs
chat, and the charge is made before mutation so a denied charge can never leave a state change without its
announcement. F7 is a small pure counter (`CredentialGuessGuard`) with a caller-supplied clock: it holds one int
per connection (retired with the peer by the same owner that retires the chat budget) and one entry per source
address (capped at 4096, pruned on expiry, evicted least-recently-touched), so the limiter cannot itself be used to
grow memory. The gate is applied where the oracle is — the logon gate drops the frame before any comparison — and
the discriminator for bearer guesses is the role claim, because the port's clients all send a placeholder CD key;
that keeps bots, the harness, local skirmish/training and the rig byte-identical on the wire, and makes the
behaviour observable only as `[lockout]` operator lines and the configured limits. A test seam injects the source
address for synthetic peers, and the tests drive time by advancing the server clock or passing `now`, never by
sleeping.

Gate evidence: `~/allegiance-evidence/P1022/` (`fast-tier2.log`, `tiers-fast2/summary.json`, `runs3.log`,
`build.log`, `build-tools.log`, `aot.log`, `scorecard.txt`). No report assets are committed.
