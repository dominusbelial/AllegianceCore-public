# R1 lobby limits — P1024 (F9–F13)

All five findings are fixed. Their six proof-test methods (seven cases) are unskipped and pass with the same inputs
and the same assertions (one proof test's fill loop is spread over accounts so the new per-account allowance can
coexist with it — explained in §2.2). Changes are left uncommitted in the working tree; nothing was pushed or
stashed.

Files touched: `src/Allegiance.Core/Identity/JoinTokens.cs`, `src/Allegiance.Lobby/Identity/IdentityStore.cs`,
`src/Allegiance.Server/RegisteredCallsignCache.cs`, `src/Allegiance.Lobby/Games/GameCreationStore.cs`,
`src/Allegiance.Lobby/NatObservationStore.cs`, `src/Allegiance.Lobby/Chat/DirectoryChat.cs`,
`src/Allegiance.Lobby/ServerRegistry.cs`; tests `tests/Allegiance.Lobby.Tests/R1LobbyReviewTests.cs` (six skips
removed, one fill loop spread), `tests/Allegiance.Lobby.Tests/P1024LobbyLimitsTests.cs` (new, 37 cases),
`tests/Allegiance.Core.Tests/P1024ReservedCallsignSkeletonTests.cs` (new, 2 cases); docs this report,
`docs/parity/p1024-lobby-limits.csv`, `GLOSSARY.md`.

---

## 1. F9 — a callsign that renders like a registered one

### 1.1 Root cause (as found)

- `src/Allegiance.Core/Identity/JoinTokens.cs:52-53` (`ValidCallsign`, before this fix) accepted every character in
  `' '..'\u00ff'` that was not a control character. Two invisible families live in that window: the soft hyphen
  U+00AD (category `Format`) and the no-break space U+00A0 (category `SpaceSeparator`). `Ad<U+00AD>min` and
  `Admin<U+00A0>` therefore passed, and rendered exactly like `Admin`.
- `src/Allegiance.Lobby/Identity/IdentityStore.cs:98,104,117` wrote `callsign.ToUpperInvariant()` as the uniqueness
  key, so a lookalike was a different key and became a second account.
- `src/Allegiance.Server/RegisteredCallsignCache.cs:102,113` did the same bare upper-case compare, so the anonymous
  logon guard could be bypassed with the same characters (`Admin<U+00A0>` is not a control/Format char, so the
  game server's `HasIllegalName` accepted it, `Server.Dispatch.cs:2550-2557`).
- Attack end state: the lobby signs a join token for the lookalike and the server adopts the token's callsign after
  its own name check (`Server.Dispatch.cs:127,225`).

### 1.2 Fix

One rule, two halves, one shared key:

1. **`ValidCallsign` is now what may be REGISTERED** (`JoinTokens.cs:77-84`): 1–24 chars, first/last not a space,
   printable Latin-1, no control character, no whitespace other than the ordinary space (so U+00A0 and U+0085 are
   out) and no `UnicodeCategory.Format` character (so U+00AD and the whole zero-width/bidi family are out).
2. **`CallsignSkeleton`** (`JoinTokens.cs:110-136`) is the uniqueness key: drop invisibles, collapse any run of
   whitespace to one space, trim, then `ToUpperInvariant`. It is used by `IdentityStore.CreateAccount` /
   `CreatePasskeyAccount` / `ExternalAccount` and by `RegisteredCallsignCache` for both the snapshot and the
   membership test (`RegisteredCallsignCache.cs:108,121`), so the lobby's account table and the game server's
   reserved-name guard can never disagree.
3. **`IsSeatableCallsign`** (`JoinTokens.cs:93-97`) is what a join token may CARRY, and both `Issue`
   (`JoinTokens.cs:132-147`) and `Verify` (`JoinTokens.cs:160`) use it.
4. **A pre-rule database is covered too.** Rows written before this fix still hold the old upper-case-only
   `normalized` key, so the unique column alone would miss them. `IdentityStore.UpholdSkeleton`
   (called from `CreateAccount`, `CreatePasskeyAccount` and `ExternalAccount`, and exposed as
   `CallsignTaken`) scans the stored callsigns with the same skeleton; when a pre-rule row owns the skeleton its key is
   upgraded in place, so the insert is then refused by the unique column exactly as every duplicate was before this
   change — the identity endpoints keep reporting 409 "already registered" (the existing
   `P808StoreTests`/`P808IdentityTests` duplicate expectations still hold). Registration is rare and rate limited, so
   the linear scan is cheap; concurrent exact duplicates are still arbitrated by the unique column.

**What happens to a name stored before this rule** (the question the brief asks): it keeps its exact bytes, keeps its
row and its unique key, and **still logs in**. `IsSeatableCallsign` is deliberately looser than `ValidCallsign` — it
only requires what the wire and the roster can hold (24 printable Latin-1 bytes without controls) — because native
never re-validated the ticket's canonical name, it seated it as-is (`FedSrv/FedSrv.CPP:5877-5878`). So a legacy
`Ad<U+00AD>min` account still authenticates, still receives a join token, and the token still verifies with the name
unchanged (`P1024CallsignTests.A_name_stored_before_the_rule_still_logs_in_and_still_blocks_a_new_lookalike`). What
it cannot do is produce a *new* lookalike: its skeleton is `ADMIN`, so registering `Admin` now fails the unique-key
constraint (and a pre-rule key is upgraded to the skeleton by `UpholdSkeleton` on the attempt), and the game server
reserves that skeleton for it. Only login of the stored account is grandfathered; registration is not.

### 1.3 Native and parity verdict

**Deliberate port hardening** (native's club server is not in the reference tree; the name was a unique club byte
string and the lobby never re-checked it). Citations re-read: `Inc/ClubMessages.h:42-45` (LOGON_CLUB carries the
`CharacterName`), `FedSrv/FedSrv.CPP:5877-5878` (the ticket's canonical name/ID replace the asserted one). The 24-byte
window is native's `c_cbName` rule the port already had (`_Utility/Utility.h:59,64` per `Server.Dispatch.cs:138-152`).
No native behaviour changes; a name that passes `ValidCallsign` renders what it is.

### 1.4 Tests

- Unskipped, unweakened: `R1LobbyReviewTests.A_callsign_that_renders_like_a_registered_one_is_refused` (both
  `Ad\u00admin` and `Admin\u00a0`).
- New `P1024CallsignTests` (20 cases): the registration rule per character family; skeleton strip/collapse/case;
  every variant of a registered name refused while ordinary and case-insensitive names behave as before; whitespace
  runs colliding; a legacy row stored with the OLD upper-case key still logging in and still blocking a new
  lookalike of itself (via `CallsignTaken`/`UpholdSkeleton`) while a genuinely different name registers normally.
- New `P1024ReservedCallsignSkeletonTests` (2 cases): the server cache matches every lookalike variant (trailing
  U+00A0, soft hyphen, surrounding spaces, zero-width space) while genuinely different names stay free — the
  anonymous-logon path can no longer take a registered name.

## 2. F10 — game-creation requests never expire

### 2.1 Root cause (as found)

`src/Allegiance.Lobby/Games/GameCreationStore.cs:99` counted every non-cancelled request against `MaxRequests` (128)
and nothing ever removed one; `Cancel` only flipped a state (`:136-145`). One account, or simply 128 legitimate
games, filled the directory for the life of the process, and every later `POST /api/games` answered 400 "too many
game requests".

### 2.2 Fix

`GameCreationStore.cs`:

- `Prune()` (`:181-187`) drops cancelled rows, rows older than their state's age, and rows whose listing has
  disappeared (`_registry.Get(serverId) is null`). It runs under the lock in `Create`, `UpdateParams`, `Cancel`,
  `QueuedFor`, `Claim`, `Complete` and `All`, so no stale row can count against the cap and the relay feedback never
  shows a dead request. The list is capped at `MaxRequests` anyway, so memory stays bounded even if nobody reads it.
- **Age/state bounds**: `QueuedMaxAge` 10 min (a queued request nobody claimed), `ClaimedMaxAge` 5 min (a claim whose
  mission never reported ready), `FinishedMaxAge` 1 h (ready/failed feedback rows).
- **Listing disappearance**: the store now takes the directory (`(TimeProvider time, ServerRegistry? registry = null)`,
  `:78-82`); DI supplies it, store-only tests can omit it. Native forgot a mission when the server reported
  MISSION_GONE (`lobby/server.cpp:197-205`, `lobby/mission.cpp:34-45`), not on a timer, so both mechanisms are used.
- **Cancelled rows removed**: `Cancel` (`:215-224`) removes the row outright.
- **Small per-account cap**: `MaxLiveRequestsPerAccount = 4` in-flight (queued or claimed) requests per account
  (`:45`, enforced at `:148-154`), error "too many live game requests for this account". Finished (ready/failed) rows
  do not count, so a player who has created several games in the hour is not blocked from creating another.

**One proof-test fixture change (and why).** `Stale_game_requests_do_not_block_new_games_forever` filled all 128
slots from a single account and asserted each create succeeded. With a small per-account allowance that expectation
cannot hold — 128 from one account is exactly the abuse the cap closes. The test now fills the same 128 requests with
the same parameters in the same burst, spread over the 32 accounts a real directory would have (four each), then
jumps 24 hours, refreshes the listing and asserts the new create succeeds. Scenario, timing, listing refresh and the
final assertion are unchanged; no limit was raised to make it pass.

Native citation for the missing rule is native itself: `lobby/client.cpp:463` —
"TODO: check up on this mission later to make sure that we got a lobbymissioninfo from the server".

### 2.3 Native and parity verdict

**Port hardening closing an acknowledged native gap.** The create relay, ack/nack and cookie mapping are native
(`lobby/client.cpp:390-459`, `lobby/messageslc.h:54-66`); expiry, listing-drop and the per-account allowance have no
native equivalent (the TODO above, plus the native lobby only removed a mission on its own MISSION_GONE message).

### 2.4 Tests

- Unskipped: `Stale_game_requests_do_not_block_new_games_forever` (fixture spread as above).
- New `P1024GameRequestTests` (6 cases): allowance boundary and expiry freeing it (with the host still
  heartbeating); cancel/ready freeing it; a disappeared listing dropping rows; claimed/finished age expiry; the
  directory-wide cap still enforced across accounts; and the HTTP create endpoint answering 400 with the
  per-account reason.

## 3. F11 — the STUN responder spent its global budget first

### 3.1 Root cause (as found)

`src/Allegiance.Lobby/NatObservationStore.cs:30-34`: `Take(ref _global, …512/s)` ran **before** the per-source
`Take(ref window, …16/s)`, so a source's refused requests consumed global tokens. 512 requests from one address in a
second emptied the shared bucket and nobody else got a reply (the proof test saw 0 bytes instead of 52).

### 3.2 Fix

`NatObservationStore.cs:45-53`: the per-source window is charged first; the shared bucket is charged only when the
request will be answered (`if (!allowed || !Take(ref _global, …)) return 0;`). The named limits are now constants:
`GlobalPerSecond = 512`, `SourcePerSecond = 16`, `MaxSources = 4096`, `MaxObservations = 8192`, window/retention
widths (`:12-26`). Behaviour, receipts and expiry are otherwise untouched.

### 3.3 Native and parity verdict

**Port hardening of a port-only mechanism.** Native has no STUN (`_Utility/DirectPlayTransport.cpp:208-217,295-309`
pick a DirectPlay transport); the opt-in binding responder is the port's NAT extension (P936). Ordering the two
budgets keeps discovery working, which is what P936 promised.

### 3.4 Tests

- Unskipped: `One_noisy_source_cannot_exhaust_the_global_STUN_budget` (600 noisy requests, then a second source gets
  52 bytes).
- New `P1024StunBudgetTests` (3 cases): a noisy source's refusals spend no global budget (two other sources both
  answered); one source's allowance recovers exactly at the 1-second window; the 512/s shared bucket still bounds
  answered requests and reopens after the window.
- Existing `P936NatSignalingTests.UDP_responder_rejects_bad_packets_and_limits_each_source_and_expires_receipts`
  (16/s per source, second source unaffected, receipt expiry) passes unchanged.

## 4. F12 — chat mutes and the channel table

### 4.1 Root cause (as found)

- `src/Allegiance.Lobby/Chat/DirectoryChat.cs:112-124`: `Mute` stored whatever string the caller sent (up to the
  16 KB body cap), 256 per account, never pruned and never bounded across accounts. One free account could pin
  megabytes for the life of the process.
- `DirectoryChat.cs:76-81`: the channel set was capped at 512 and never pruned; 511 made-up `server:…` names filled
  it for good, so every real server's channel was refused with "too many channels" until restart.

### 4.2 Fix

`DirectoryChat.cs`:

- **Mute targets are account ids**: `IsAccountId` (`:77`) requires exactly 32 hex digits — the form this lobby mints
  (`Guid.ToString("N")`, `IdentityStore`). `Mute`/`Unmute`/`Muted` reject anything else, normalize case, keep the
  self-mute refusal and stay idempotent (`:155-196`).
- **Bounded in total**: `MaxMutedPerAccount = 256` (unchanged), `MaxMuteEntries = 8192` rows across the directory and
  `MaxMutedAccounts = 512` lists (`:36-44`), tracked by `_muteEntries`. A full table refuses new rows, and unmuting
  frees exactly one. No pruning is possible (a native mute has no expiry either), so the bound is the protection.
- **Channels cannot be locked out**: `PruneEmptyChannelsLocked` (`:121-131`) runs on every post that finds the table
  at its cap and removes channels with no line left in the bounded history. Because history is capped at
  `MaxHistory = 500` and the table at `MaxChannels = 512`, at least twelve slots are always free for real channels;
  only names leave, never lines.

The registry-aware alternative the brief offered ("`server:<id>` only for ids in the registry") was not needed: the
history-based eviction makes the lock-out impossible without coupling the chat store to the directory, and it also
covers channels whose server deregistered normally.

### 4.3 Native and parity verdict

**Port hardening.** Native's chat relay is in-mission (`clintlib/appmsg.cpp:2640-2665`) and a mute is a boolean on a
real player entry (`WinTrek/WinTrek.cpp:3731-3734`, `WinTrek/trekigc.cpp:4492-4495`); there is no directory channel
table and no mute table to fill. The port kept the relay's own-echo suppression and per-sender mute semantics and
bounded the two tables it introduced.

### 4.4 Tests

- Unskipped: `Mute_accepts_only_an_account_id` (8000-character id → 400) and
  `Made_up_channels_cannot_lock_out_a_real_servers_channel` (600 made-up channels, then a real channel posts).
- New `P1024ChatLimitTests` (4 cases): mute id validation, idempotence and case; the directory-wide mute bound
  (8192 rows exactly, then refused; unmute frees one); the channel table staying below its cap with history intact
  and the newest junk channel still readable; mute suppressing a sender for that reader only.
- Existing `P871ChatAndGamesTests` (authentication, relay/no-echo, mute suppression and self-mute refusal) passes
  unchanged.

## 5. F13 — absurd listing counters break the stats endpoint

### 5.1 Root cause (as found)

`src/Allegiance.Lobby/ServerRegistry.cs:372` summed `live.Sum(l => l.PlayerCount)` with overflow checking, while
registration/heartbeat only floored the counters at zero (`:108-109,123-124,154-155,167-168`). Two registrations
with `playerCount = int.MaxValue` made `GET /api/servers/stats` throw (500) for as long as they were kept alive, and
the public list showed the absurd counts.

### 5.2 Fix

`ServerRegistry.cs`:

- **Clamp on register and refresh**: `ClampCount` (`:456`) caps `PlayerCount` at `MaxPlayerCount = 2047` and
  `MaxPlayers` at `MaxPlayersCap = 2047` (native's 11-bit LOBBYMISSIONINFO fields), `CurrentGames` and `MaxGames` at
  `MaxGamesPerServer = 1024` (native's int game counts, default 20); mission rows are clamped the same way
  (`ToMissions`). Negative input floors at zero.
- **Sums cannot overflow**: `SumSaturated` (`:407-413`) accumulates in `long` and saturates at int range; every stats
  sum uses it (`:390-406`), including the hidden paused/empty counters.

The caps are named constants in the registry (no `server.json` key, no env var, no compose change — defaults are
code constants and there is nothing for an operator to tune safely).

### 5.3 Native and parity verdict

**Port hardening** for the sum, **native-derived width** for the player caps: `lobby/messagesall.h:39-41` declares
`nNumPlayers`, `nNumNoatPlayers` and `nMaxPlayersPerGame` as 11-bit fields (2047 max), so no native mission ever
advertised more; `lobby/server.cpp:463` and `lobby/server.h:126-127,166-167` keep `m_iMaxGames` (20 default) and
`m_iCurGames` as ints. Native had no HTTP stats endpoint, hence no sum to overflow.

### 5.4 Tests

- Unskipped: `Absurd_player_counts_do_not_break_the_stats_endpoint` (two `playerCount = int.MaxValue` registrations,
  stats 200).
- New `P1024ListingCounterTests` (4 cases): clamp on register and on refresh (including negative), ordinary counts
  untouched (7/64/2/20 and percent load 0), stats sums at 2 × 2047 for absurd listings, and the HTTP stats endpoint
  answering 200 with the clamped totals.
- Existing `P894LobbyTests` (`Stats_report_servers_missions_players_and_load`,
  `Game_counts_and_percent_load_follow_native_c_GameFactor`) and `RegistrationTests` pass unchanged.

---

## 6. Test counts

### 6.1 Unskipped and new tests, alone

- `dotnet test tests/Allegiance.Lobby.Tests --filter "FullyQualifiedName~R1LobbyReviewTests"` —
  **7 passed / 0 failed / 0 skipped** (F9 counts two theory cases; F10, F11, F12a, F12b, F13).
- Same project, filter `FullyQualifiedName~R1LobbyReviewTests|FullyQualifiedName~P1024` — **44 passed / 0 failed /
  0 skipped**, three consecutive runs (44/0/0 each).
- `dotnet test tests/Allegiance.Core.Tests --filter "FullyQualifiedName~P1024"` — **2 passed / 0 failed / 0 skipped**,
  three consecutive runs (2/0/0 each).
- Whole `tests/Allegiance.Lobby.Tests` project, three consecutive runs: **191 passed / 0 failed / 3 skipped** (32 s,
  31 s, 31 s). The three skips are the PostgreSQL-only store tests, which need a database container (skipped on
  master too).

### 6.2 Existing classes covering the touched paths (run as part of the whole-project runs above)

`P871ChatAndGamesTests` (chat relay, mute, create/cancel/claim), `P871bJsonContractTests` (games JSON contract),
`P808StoreTests` and `P808cReservedCallsignTests` (callsign rules and the registered-names read),
`P808IdentityTests` (tokens and passkeys), `P894LobbyTests` (listing fields, game counts/percent load, stats),
`RegistrationTests`, `ExpiryTests`, `HeartbeatTests`, `DeregisterTests`, `P761PlayerListTests`,
`P806ServerListLoopbackTests` (real client filter/sort against the real directory), `P936NatSignalingTests` and
`P936InternetSessionTests` (STUN observations, player-hosted registration), `P970ListingPasswordTests`.

---

## 7. Gate

The report was written before the gate (values below filled in after execution). NuGet audit stayed enabled, no
package was changed, and every build/test used the lane's build-server workaround
(`--disable-build-servers -nodeReuse:false -p:UseSharedCompilation=false`; for `tools/build-tools.sh` the same
settings were passed as `UseSharedCompilation=false MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0`).

| Check | Result |
| --- | --- |
| `dotnet build AllegianceCore.slnx` (final tree) | **0 warnings / 0 errors** |
| `tools/build-tools.sh` | **all 140 tool projects built**, three times, the last on the final tree |
| AOT publish (`src/Allegiance.Server`, `-c Release -r linux-x64 --self-contained -p:PublishAot=true`) | **succeeded**, 0 warnings, **no IL2026/IL3050** (21.9 MB native binary) — required because `Allegiance.Core` and `Allegiance.Server` changed |
| `python3 tools/check-glossary-links.py` | passed, **877 distinct paths** |
| `python3 scripts/parity-scorecard.py docs/parity/p1024-lobby-limits.csv` | validated **6 rows**, 100.00% (16/16 weighted) |
| New/unskipped tests alone ×3 | 44/0/0 and 2/0/0 each run (§6.1) |
| Whole `Allegiance.Lobby.Tests` ×3 | 191/0/3 each run |
| `tools/run-tests.sh --tier fast`, final tree | **14,651 tests, 578.7 s**, calibrated Fast limit **13.113 s** (P744 factor 1.311), infrastructure checks **9/0/0** |

Fast-tier per project (final sweep): Lobby **188/0/0**, Update **55/0/0**, ClientCore.MonoGame **5471 passed / 4 not
executed**, ClientCore **2451/0/0**, Core **6471 passed / 4 failed / 7 not executed**; quarantine ClientCore 2/0/0
and Core 17/0/0. The runner's overall exit was **FAIL** from those four Core failures plus six calibrated-limit
offenders that all passed. An earlier full sweep of the same tree (before the last `IdentityStore` hardening pass,
607.5 s, limit 12.537 s) failed five *different* Core tests plus four timing offenders. All **nine** distinct failed
methods were re-run in their own process three times and passed **1/0/0** every time — load flakes on this shared
host, where other lanes run suites concurrently; none touches a file this story changed. Ledgered in
`docs/TEST_FLAKES.md` (§"2026-11-15 — P1024 lobby limits").

Final-sweep failures (all 3/3 alone afterwards):

- `WarpTransitServerTests.ShipInsideWarp_GetsTransited_AndPeerSeesSectorChange`
- `StationCombatLiveTests.Lethal_hit_destroys_station_ejects_docked_ship_and_broadcasts_destroy_to_whole_mission`
- `P739TimerLiveTests.Warp_bomb_notice_matches_captured_blast_deadline_and_stays_in_mission` (delay 5 and delay 7)

Earlier-sweep failures (all 3/3 alone afterwards):

- `P467E2eGunDamageDevLiveTests.Completing_dev_143_raises_gun_damage_in_combat`
- `MissileCombatTests.Cross_side_missile_against_a_mounted_shield_absorbs_then_leaks_exactly_like_a_weapon`
- `GameOverPlayersWireTests.Rejoined_player_accumulates_time_across_stints_in_roster`
- `BotFlightTests.Bot_trajectory_matches_independent_ExecuteMove_rollout`
- `AsteroidMinedLiveTests.No_broadcast_fires_once_miner_hits_capacity`

Timing-only offenders across both sweeps (`P796VariedLoadoutTests…civ: 35` 16.3–17.0 s, `P814GunnerTests.P814b…`
17.7–19.1 s, `SC2NaniteSupportTests.Support_selection…civ: 25` 18.1–18.3 s,
`P750NativeAsteroidTypesTests.Research_showcase_rocks…` 15.8 s, `StationCombatLiveTests…` 19.2 s,
`P968PublisherStreamIndexTests.Reliable_backlog…(peers: 400)` 13.6 s, `P999CivStrategyTests.Every_profile…civ: 5`
15.7 s) all passed. No limit, tier or assertion was relaxed.

Environment notes: Docker is not installed, so `docker compose config` was not run — this change adds no service,
store, port or environment variable (all limits are named code constants), so `docker-compose.yml` is untouched.
No HUD/training behaviour changed. The task brief (`P1024_BRIEF.md`) is untracked and not part of the change set.
Raw sweep/build/AOT logs, both tier summaries and the failed project logs are kept outside the repository in
`~/allegiance-evidence/P1024/`; task scratch (`wt-p1024/.scratch/p1024/`, including the 74 MB AOT output) was
deleted. Every process started for this story has finished.

**Native intent:** a callsign is a unique, printable 24-character pilot name the lobby seated as-is and the game
server adopted from the join ticket; a created mission existed only while the server kept it and disappeared with
MISSION_GONE; the lobby never advertised more players than its 11-bit mission population field could hold; a mute
was a per-player flag on a real roster entry.

**Modern approach:** one shared callsign skeleton for the account table and the reserved-name cache (legacy names
grandfathered for login only), state/age/listing-aware create-request expiry with a small per-account allowance,
per-source-first STUN budgeting, account-id-only bounded mutes with history-backed channel eviction, and clamped
listing counters with overflow-safe stats sums — all as named constants with focused boundary tests.

