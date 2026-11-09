# P966 — Server trust boundaries: logon re-send flood, claimed map path, reconnect impostor (P963-02/03/05)

Base: master at `1f6e1250` (the P965/P967/P968 merges had already landed P963-01/04/06/07/08/09; this task
closes the remaining three findings of the independent server review
`docs/reports/SERVER_REVIEW_P963.md` — Kimi K3.1 Pro, 2026-11-08; each finding re-verified against master
before fixing, and every native line below re-grepped in `~/Development/Allegiance/src/` at task time).
Worktree `~/allegiance-pi-p966`; nothing committed or pushed.

**Scope:** P963-02 (unbounded pending re-LOGONREQ amplification), P963-03 (player-claimed map escapes the
catalog and artwork root), P963-05 (wrong reconnect token retires the victim + token leak in the log +
name-collision stale-entry edge). Out of scope per the brief: lobby enrollment, rate-limiter redesign, other
dispatch routes.

## 1 — P963-02: pending duplicate LOGONREQ is an unbounded reply/CPU amplifier

### Before

`src/Allegiance.Server/Server.Dispatch.cs:168-186` (master): a peer in the post-connect side-select hold
(`_pendingSideSelect`, P281) that re-sends LOGONREQ gets, **per copy, with no limit**:

1. one full `Console.WriteLine` log line;
2. a guaranteed LOGONACK (`SendPeer` → `FedPacket.Pack`);
3. `SendSideRoster(from)` → `BuildSideRoster(mission)` (`Server.Lobby.cs:5529-5544` on master) — a full
   per-side scan of `_peerShips` plus a lobby-count pass — then a guaranteed SIDE_ROSTER frame (the
   PLAYERINFO burst inside is one-shot latched per recipient, so duplicates did not repeat it).

The review's arithmetic: at 400 seated peers, 6 sides and 256 duplicates in one tick (the per-peer
fair-share inbound limit), that is ~614,000 peer/side predicate evaluations plus 512 guaranteed frames and
256 log lines **in a single tick**, from one connection. The idempotency itself is sound (P646: no second
ship, no new id — the port's robustness over native, which simply asserts the case cannot happen).

**Native:** `FedSrv/FedSrv.CPP:4830-4832` — `pfsPlayer = CFSPlayer::GetPlayerFromConnection(cnxnFrom);` then
`assert (pfm->fmid != FM_C_LOGONREQ); // shouldn't have a pfsPlayer yet if logging on`. Native has no
pending-side-select retry flow at all; there is nothing to depart from, only the port's own extension to
harden.

### After

The re-ack stays idempotent but is **bounded** (`Server.Dispatch.cs:172-223`):

- **Per-peer retry interval** — `PendingSideSelect.LastReackSimTime` (`Server.cs:1075-1079`, anchored at
  the original logon's ack, moved by each served re-ack via `WithReackSimTime`). A duplicate inside
  `PendingSideSelect.ReackRetrySeconds` (1.0 s sim time, `Server.cs:1078`) is dropped with **no reply, no
  roster, no allocation** (`Server.Dispatch.cs:193-202`). A legitimate client retry — real retransmit
  timeouts are never faster than the interval — still gets the SAME ack (same ship id): proven by the test
  below and by the updated `LiveDuplicateLogonTests.Pending_peer_duplicate_LOGONREQ_is_idempotent_same_ack_no_new_id`
  (it now advances the sim clock past the interval before the retry).
- **No log line per excess duplicate** — both the dropped-duplicate and the served-re-ack lines go through
  the existing `_duplicateLogonLog` `PerPeerRateLimiter` (5 emissions per 5 s window per connection, one
  summary line per 100 suppressions — the same gate the seated-peer duplicate branch already uses), so the
  flood cannot force unbounded console output either.
- **Cached roster bytes** — `SideRosterPacket(mission)` (`Server.Lobby.cs:5609-5625`) caches the encoded
  SIDE_ROSTER packet per mission and re-uses it until the roster changes. Invalidation is an epoch counter
  (`InvalidateSideRosterCache()`, `Server.Lobby.cs:5603`) bumped at every roster-affecting event:
  `MarkLobbyDirty` (`Server.MissionParams.cs:596` — the lobby-dirty choke point covers player-left,
  side-change, team-lobby, host edits of names/teams/civs/locks, squads, stages), the seat registration
  `CompleteJoinAndLaunch` (`Server.Lobby.cs:4602` — a reconnect-grace resume re-enters `_peerShips` without
  a lobby-dirty mark), `ApplySideCivilization` (`Server.TeamCiv.cs:148` — the roster records carry the civ
  id) and the `TestSeedPeers` seam. The cache is pruned to the live mission set on insert. The per-recipient
  PLAYERINFO burst and squad relays in `SendSideRoster` (`Server.Lobby.cs:5640-5684`) are **not** cached and
  behave exactly as before.
  - Within one sim tick the roster cannot change between two duplicates (dispatch and every mutation run on
    the sim thread), and across events the epoch guarantees the next send rebuilds; the freshness contract
    of the existing refresh path (`BroadcastSideRosterToPendingPeers`) is unchanged.

Amplification after the fix: 256 duplicates per tick produce **one** rate-limited log burst and **zero**
frames; a flooder gets at most one re-ack per second (and the roster scan at most once per epoch no matter
how many distinct peers re-ack — the 400-flooder case now costs 1 scan per tick per mission instead of
400 per second).

### Tests

- **New** `tests/Allegiance.Core.Tests/P966ServerTrustBoundaryTests.cs`
  - `Duplicate_LOGONREQ_flood_from_a_pending_peer_is_bounded` — 256 duplicates in one tick: 1 LOGONACK, 1
    SIDE_ROSTER (the originals), ≤ 8 log lines (rate-limited; measured 7: 5 emitted + 2 summaries), still
    exactly 1 pending entry, 0 ships; a retry after 1.25 s gets the same ack/ship id; a 50-duplicate burst
    right after a served re-ack is again all dropped (the bound re-arms).
  - `Re_acked_roster_bytes_are_cached_until_the_roster_changes` — 3 pending peers, 7 SIDE_ROSTER sends
    across a seat (logon × 3, re-ack × 3, seat-refresh × 1) produce exactly **2** roster builds
    (`SideRosterBuilds` test seam, `Server.Lobby.cs:5596-5601`); after a peer seats, the next re-ack
    rebuilds once and carries the fresh per-side count (side 0 = 1) — never the pre-seat bytes.
- **Updated** `LiveDuplicateLogonTests.Pending_peer_duplicate_LOGONREQ_is_idempotent_same_ack_no_new_id` —
  advances 1.25 s of sim time before the duplicate so the served re-ack (the test's subject) is
  deterministic under the interval.

### Native comparison

No native rule is departed from: native asserts the duplicate cannot exist (`FedSrv.CPP:4830-4832`). The
port keeps its idempotent extension for real client retries and adds the abuse bound the review asked for.
The P646 contract (one logon per connection — no second ship, no orphaned ids) is unchanged and asserted.

## 2 — P963-03: claimed map path escapes the catalog and artwork root

### Before

`src/Allegiance.Server/Server.Provisioning.cs:88-94` (master): a claimed game's `config.Map` (only
trimmed/non-empty in `PlayerGameProvisioning.cs:111-117`) went straight into
`Path.Combine(_config.ArtPath, config.Map)` + `File.Exists` + assemble. `ResolveCatalogMap`
(`Server.StaticMaps.cs:41-49`) was never consulted for claimed games, so `Map = "../outside/x.igc"` or a
rooted path escaped the artwork root: a **file-existence/parse-result oracle** and the ability to host any
readable valid IGC map outside the advertised catalog.

**Native:** `FedSrv/FedSrv.CPP:234-245` — `VerifyUserMap(szMapFile, nTeams)` requires an exact catalog
match, team count and file name (`nTeams == g.vStaticMapInfo[i].nNumTeams && 0 == strcmp(szMapFile,
g.vStaticMapInfo[i].cbIGCFile)`), over the maps.txt catalog loaded at `FedSrv.CPP:211-231`. Re-grep note:
`VerifyUserMap` is defined-but-uncalled in the shipped tree (dead code from the AllSrv/SQL era — no
reference anywhere in the native sources); it is the wizard-era intent the port now enforces at the server
boundary. The port's own catalog loader already rejected non-basename entries (`Server.StaticMaps.cs:30-36`),
but provisioning bypassed that policy entirely.

### After

`Server.Provisioning.cs:91-119` + `Server.StaticMaps.cs:53-89`:

- **The claim is only ever a catalog KEY.** `TryResolveClaimedCatalogMap(config, out catalogFile)` requires
  an exact entry in the operator's `maps.txt` — `Teams == game.Teams` AND the file name (bare
  `m2xspiderw`, the wizard's `.igc` form, or the friendly name `AA19SpiderW`). No match ⇒ refused
  ("not in this server's catalog for N team(s)"), before any file-system use: `..`, rooted, drive-letter and
  non-catalog claims never reach `Path.Combine`.
- **Basename + canonical containment re-check** — the resolved `catalogFile` must be a relative basename
  (no separators, not rooted — the loader's own rule), and `Path.GetFullPath(Path.Combine(root, file))`
  must stay under `Path.GetFullPath(_config.ArtPath)` with a separator boundary (`underArtRoot`). This is
  defense-in-depth against a hostile/corrupted maps.txt: even that cannot make the server open a file
  outside the artwork root.
- **No path echo** — the refusals name no server path (the lobby's Failed state carries nothing either;
  `CompleteGame(claim.Id, null)`); the operator log carries only the sanitized, truncated claim string
  (`ConsoleLog.Sanitize`, 48 chars) — the old `map '{config.Map}' not found under artPath` line printed the
  raw claim and is gone.

Configured games (`Server.Lifecycle.cs:1393`, operator config) and the map-rotation corpus keep the
existing `ResolveCatalogMap` fallback — the fix applies to the player-claimed boundary only, exactly where
the trust break was.

### Tests

- **New** `P966ServerTrustBoundaryTests.Claimed_map_traversal_rooted_wrong_team_and_non_catalog_are_refused`
  — against the real corpus catalog (`2,25AnniSimple,XXV_simple`): `../XXV_simple.igc`,
  `../../Artwork/XXV_simple.igc`, `/etc/passwd`, `C:\Artwork\XXV_simple.igc`, a 3-team claim on the 2-team
  `XXV_simple`, and `PCore001.igc` (a file that EXISTS under artPath but is a core, not a catalog map — the
  existence/parse oracle) are all refused; the `.igc`, bare and friendly-name catalog forms provision
  (3 player-created missions); the refusal log contains no artwork-root path.
- **Updated** `P871bCreateGameProvisioningLiveTests` — the wizard claim used `NTems = 3` on the 2-team
  catalog map `m2xspiderw.igc`; under the exact file+team gate that is a wrong-team claim (native
  `VerifyUserMap` requires `nNumTeams`), so the wizard parameters now use `NTems = 2` (the wizard's own
  parameters still differ from the configured world via the per-team cap, goals and name; assertions
  updated 3 → 2 sides/teams).

### Native comparison

Port now matches `VerifyUserMap` (`FedSrv.CPP:234-245`): exact file + team count against the static-map
catalog. Parity row added: `docs/parity/lobby-matrix.csv` **LOB-0036** (classification `design` — the
native check is dead code in the shipped tree; the port enforces the intent at the HTTP/claim boundary with
canonical-path hardening the native never needed, being a trusted local UI away). The glossary's map-catalog
entry (P869) now names the claimed-map gate.

## 3 — P963-05: wrong reconnect token retires the victim (+ token in the log, + name-collision stale entry)

Grace is **default off** (`reconnect.enabled`, experimental P813c); everything here is inert unless enabled.

### Before

`src/Allegiance.Server/Server.ReconnectGrace.cs:193-201` (master): a LOGONREQ naming a parked player with a
wrong `ResumeToken` **removed the parked entry and retired the victim's ship**
(`RetireDepartedPeer`) — any unauthenticated third party knowing only the callsign could terminate the
victim's seat during the grace window. The refusal log line printed the **expected token** in full
(`0x{req.ResumeToken:X8} != 0x{parked.Token:X8}`) — the exact secret an impostor needs, handed to whoever
can read the log. (The park and resume-success lines printed the full token too, for the same leak via a
shared console.)

The review also flagged the `nameTaken` branch (`:212-219` on master) as "retires without removing the
parked entry (possible double retirement at expiry)". Re-verification on master: the entry **is** removed —
`_parkedShips.Remove` runs at `:203`, before the nameTaken branch — so no double retirement exists on this
code; the real defect there was the retirement itself on a mere name collision (and the removal order makes
the invariant non-local). Fixed by construction below.

### After

`Server.ReconnectGrace.cs:229-285`:

- **Wrong token ⇒ refuses only the claimant** (`:231-245`): no removal, no retirement, no roster/side
  change — the parked entry and the victim's ship are untouched and stay resumable by the real owner. The
  claimant's LOGONREQ falls through to the fresh-logon path (still subject to all its gates).
- **The token is never logged** — `TokenTag(token)` (`:192-206`): a 16-bit non-reversible FNV-style mix
  ("tag XXXX"), enough to correlate lines, impossible to invert to the 32-bit token. All three token-printing
  lines (wrong-token refusal, park, resume success) now print the tag; the claimant's own value (which the
  claimant sent) stays in the refusal line for diagnosis.
- **name collision ⇒ refusal-as-flag, no retirement** (`:247-272`): the resume is refused while another
  live peer holds the name, but the seat **stays parked for its window** — retirement happens on window
  expiry, game over, or a server-verified replacement, never a collision. Native comparison (the parity
  ruling "dup logon native-when-verified, port refusal as flag", `docs/parity/lobby-matrix.csv` LOB-0033):
  native USEAUTH **removes the old same-name player** so the new logon proceeds
  (`FedSrv.CPP:629`, `RemovePlayerByName(..., QSR_DuplicateLocalLogon)`), while the standalone-private build
  **refuses the claimant** with `DELPOSITIONREQ(DPR_DuplicateLogin)` and leaves the existing player alone
  (`FedSrv.CPP:602-623`). The port refuses the duplicate logon (`ServerConfig.AlwaysRefuseDuplicateLogons`),
  so the refusal arm applies to the collision too — neither player is removed. This also makes the
  single-removal invariant explicit and local: the parked entry is consumed **exactly once** — at the actual
  resume (`:281-284`), the not-resumable fallback (`:262-265`, consumed so expiry cannot act on a dead
  ship), expiry, or game over — never twice, never on a refusal.
- **Resume supersedes a pending hold** (`:281-284`): a peer that was refused for a name collision and went
  pending-side-select has that hold dropped when it later resumes (the old code could not produce the
  pending+parked pair because the wrong-token path destroyed the entry; the pair is now reachable and must
  not leak a pending entry past the resume).
- **Supporting fix — one held seat per (mission, name)** (`TryParkLiveShip`, `:162-180`): parking a same-name
  drop no longer **overwrites** an existing parked entry. The overwrite (pre-existing, reachable without any
  wrong-token claim: A drops and parks as "X", B takes the freed name, B drops and parks) silently replaced
  A's entry and orphaned A's ship — still in its sector, no table entry, never retired. The newcomer's park
  is now refused (it gets the immediate native removal, the grace-off outcome) and the existing seat stays.
  My name-collision test reaches exactly this path when the impostor disconnects.

### Tests

`tests/Allegiance.Core.Tests/P813cReconnectGraceTests.cs`:

- **Rewritten** `Wrong_token_refuses_only_the_claimant_and_the_original_resumes` (was
  `Resume_with_the_wrong_token_is_refused_and_the_ship_retires` — it pinned the old destructive behavior):
  the intruder claims the name with a wrong token (and a wrong version, so the name is not taken by the
  attempt) → parked count 1, ship still in sector, still resumable, `ReconnectRefusedTotal` 1; the console
  capture contains the claimant's token but **not** the victim's (`C0FFEE01`) and does contain the tag; the
  original then reconnects with the right token and **resumes the same ship**.
- **New** `Name_collision_keeps_the_seat_parked_and_the_original_resumes`: the impostor's wrong-token claim
  is refused, then takes the freed name via a validated fresh logon and seats; the original's resume with
  the right token is refused while the name is held (parked count still 1, ship in sector, not retired); the
  impostor leaves (no overwrite of the parked entry — one seat per name); **the original retries and
  resumes the same ship**. Live observers on both sides keep the win-by-elimination tally from ending the
  mission (a parked ship does not keep its side active — only `_peerShips` count).
- The 12 pre-existing P813c tests (default-off, park/neutral controls, expiry, voluntary logoff, resume,
  verified-account resume, death-in-gap, game-over, leadership) are unchanged and green.

## Diff summary

| File | Change |
| --- | --- |
| `src/Allegiance.Server/Server.cs` | `PendingSideSelect.LastReackSimTime` + `ReackRetrySeconds` (P963-02) |
| `src/Allegiance.Server/Server.Dispatch.cs` | bounded re-ack branch: interval gate, rate-limited logs (P963-02) |
| `src/Allegiance.Server/Server.Lobby.cs` | `SideRosterPacket` cache + epoch + `SideRosterBuilds` seam; `SendSideRoster` uses it; bump at `CompleteJoinAndLaunch` (P963-02) |
| `src/Allegiance.Server/Server.MissionParams.cs` | `MarkLobbyDirty` bumps the roster epoch (P963-02) |
| `src/Allegiance.Server/Server.TeamCiv.cs` | `ApplySideCivilization` bumps the roster epoch (P963-02) |
| `src/Allegiance.Server/Server.BroadcastTestSeams.cs` | `TestSeedPeers` bumps the roster epoch (P963-02) |
| `src/Allegiance.Server/Server.StaticMaps.cs` | `TryResolveClaimedCatalogMap` (P963-03) |
| `src/Allegiance.Server/Server.Provisioning.cs` | claimed-map catalog gate + canonical containment + sanitized refusals (P963-03) |
| `src/Allegiance.Server/Server.ReconnectGrace.cs` | fail-closed wrong token; `TokenTag`; name-collision refusal-as-flag; single-removal; pending-hold cleanup; no-overwrite park (P963-05) |
| `tests/Allegiance.Core.Tests/P966ServerTrustBoundaryTests.cs` | new: flood bound, roster cache, map-claim gate |
| `tests/Allegiance.Core.Tests/P813cReconnectGraceTests.cs` | wrong-token test rewritten; name-collision test added; `CaptureConsole` helper |
| `tests/Allegiance.Core.Tests/LiveDuplicateLogonTests.cs` | pending-duplicate test advances past the interval |
| `tests/Allegiance.Core.Tests/P871bCreateGameProvisioningLiveTests.cs` | wizard team count 3 → 2 (catalog-verified map) |
| `GLOSSARY.md` | claimed-map gate; P966 re-ack/roster-cache entry; reconnect-grace trust rules (and the stale "token in LOGONREQ.dwCookie" wording fixed to the P883 `ResumeToken` extension) |
| `docs/parity/lobby-matrix.csv` | new row LOB-0036 (user map catalog verification) |
| `docs/parity/p867-onappmessage.csv` | LOGONREQ row: bounded re-ack noted, tests updated |

## Gate

All numbers from this worktree, `local.env` present (artwork-gated tests ran).
Raw sweep logs/TRX preserved at `~/allegiance-evidence/p966/` (outside the repo, per the evidence-size
rule); the runner's own copy was under `TestResults/tiers-20261109T013853.881216Z/`.

| Gate item | Result |
| --- | --- |
| `dotnet build AllegianceCore.slnx` | **0 warnings / 0 errors** (rebuilt after the last edit) |
| `tools/build-tools.sh` | **PASS** — all 122 tool projects, 0 warnings / 0 errors |
| AOT publish (`src/Allegiance.Server` changed) — `dotnet publish -c Release -r linux-x64 --self-contained -p:PublishAot=true` | **PASS** — native binary 17.2 MB, **0 warnings, no IL2026/IL3050** |
| New/affected tests 3× alone (`P966ServerTrustBoundaryTests`, `P813cReconnectGraceTests`, `LiveDuplicateLogonTests`, `P883MissionCookieTests`, `P914StationDeadlineTests`, `P871bCreateGameProvisioningLiveTests`) | **52/52 passed in each of 3 standalone runs** (12 s per run) |
| One full fast tier — `tools/run-tests.sh --tier fast` | **13,586 cases in 551.6 s: 13,583 passed / 2 failed / 0 skipped / 3 not executed** (the MonoGame project's own skip markers); Fast infrastructure checks PASS. The 2 failures are **pre-existing intermittent families, proven non-task by clean-base controls** (below); 2 SLOW FAST timing exceedances (38.299 s `MissileLockLiveTests`, 10.246 s `P777WarpExitWobbleTests` vs the 10.000 s limit — the same two families under shared-host load) |
| Showcase never-parks 5× vs base rate (`P700ShowcaseLiveTests.Local_showcase_never_parks_with_target_for_five_seconds`, sequential isolated runs) | **5 pass / 0 fail**, walls 287/184/357/253/269 s at host load avg 1.3–3.3, **zero parking violations** — at or better than the recorded base rate (P967: 4/5, arrival-assertion failure). No bots/flight/sim path changed by P966 |
| Parity scorecard + glossary links | `parity-matrix.csv` 700 rows validated (lobby 37/37, 100%); `sim-rules-matrix.csv` 858 rows; `tools/check-glossary-links.py` OK (666 paths); LF-only CSVs (`grep -c $'\r'` = 0) |
| Skipped counts | Fast sweep: **0 skipped**, 3 not executed (MonoGame skip markers — same as P967's sweep); all targeted/new suites: **0 skipped** |
| Public-repo hygiene (self-check of the working-tree diff against `scripts/publish-guard.sh`'s rules) | 0 `/home/<user>` lines outside docs, 0 secret patterns, 0 personal emails, 0 new media |

### The two Fast-sweep failures (non-task, ledgered)

Both were re-run standalone on the changed tree **and on an unmodified checkout of
the same base commit** (`1f6e1250`, built in a scratch worktree, since removed) — the
failure rates match, so neither is P966's:

| Family | Changed tree alone | Clean base alone | Notes |
| --- | --- | --- | --- |
| `LiveSideSelectTests.Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds` | 3 pass / 4 fail (32.15–32.40 vs [28, 31]) | 2 pass / 3 fail (same 32.x) | Existing receipt/simulation-clock boundary (ledgered since P945-era): the auto-seat fires at the deadline; the test's `elapsed` is measured when the FltBot receive thread surfaces `ShipAssignment`, which lags 1–2 sim-seconds under host load. No side-select/auto-seat source changed |
| `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire` | 0 pass / 3 fail (38 s each) | 1 pass / 2 fail (1 s pass / 38 s fails) | Known missing partial-tip receipt (ledgered; same 38.7–38.8 s timeout signature). No missile-lock source changed |

Both are ledgered in `docs/TEST_FLAKES.md` (P966 validation section) with the control
data.

## Footer

**Native intent.** Native Allegiance treats the connection as the trust boundary at logon: an existing
player simply cannot send LOGONREQ (`FedSrv.CPP:4830-4832` — an assertion, because the case is
unconstructible in its flow), and a user-requested game must name a static-map catalog entry exactly —
file and team count (`VerifyUserMap`, `FedSrv.CPP:234-245`) — before a mission exists. A duplicate name at
logon either replaces the old same-name player (USEAUTH, `FedSrv.CPP:629`) or refuses the claimant and
leaves the incumbent alone (standalone-private, `FedSrv.CPP:602-623`). The port's reconnect grace has no
native counterpart (native destroys the player at the drop, `FedSrv.CPP:4549-4566`); its trust rule is
therefore the port's to state, and it now mirrors the incumbent-protecting arm: a failed claim costs the
claimant nothing but the refusal.

**Modern approach.** Treat every client-supplied name/path/token as untrusted input that is only ever
matched against operator-controlled catalogs — never `Path.Combine`d into the file system, never echoed
back, never printed in full (short non-reversible tags instead). Bound every idempotent reply path with a
per-peer retry interval and rate-limited diagnostics, and cache derived broadcast bytes behind an epoch so
a flood costs one rebuild per state change, not one per packet. Make refusal fail-closed for the claimant
and side-effect-free for the incumbent, with exactly-once removal so no path can retire a ship twice, and
one held seat per (mission, name) so no overwrite can orphan a ship.
