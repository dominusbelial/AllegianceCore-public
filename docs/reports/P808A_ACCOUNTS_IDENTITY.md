# P808a — lobby-issued player accounts and trusted joins

Implemented on `codex/p808a`, starting from public `650eb6a5` (P804), rather than
brief reference `6ef6d9bf`. No commits, pushes, stashes or remote-host operations.
The changes are left in the working tree. Design was written before implementation:
[identity design](../P808A_IDENTITY_DESIGN.md).

The lobby now persists accounts, passkeys, external provider subjects, expiring
sessions, device authorizations and approved game-server credentials. SQLite is the
default; PostgreSQL uses the same versioned SQL migration. The desktop intro offers
F2 sign-in/cancel/sign-out, shows the device code and browser address, and displays
the canonical callsign. Game servers validate short-lived signed tickets offline,
bind account IDs and names, reject invalid credentials and support configurable
anonymous joins. Directory trust comes from operator-approved server enrollment.

## Native rules, classification and data flow

Native source citations were re-read locally. The brief's `TOKEN` is actually
`lobby/messagesls.h:79-81`, not `messageslc.h`. Native game protocol layouts remain
unchanged; HTTP account endpoints are the modern replacement for Club/lobby auth.

| Row | Native rule and classification | Before → after; executable path | Data reaching client and verification |
|---|---|---|---|
| Account identity | **Design:** Club logon carries character name/ticket and returns member ID, `Inc/ClubMessages.h:42-58`; authenticated game logon takes name/account ID from issuer, `FedSrv/FedSrv.CPP:5877-5878`. | Free-text names, no account store → durable random account ID and case-insensitively unique callsign. `IdentityStore.cs:44,78`, `PasskeyService.cs:39`; transactional registration and unique DB constraints. | Modern HTTP account/session result corresponds to native Club ACK identity; no extra game-message field. `P808IdentityTests.Passkey_account_is_stable_unique_persistent_and_requires_real_proof`, `P808PostgresTests`; both pass on the port, baseline endpoints are 404. |
| Proof and browser approval | **Era constraint:** proprietary encrypted Zone proof, `Inc/ClubMessages.h:42-58`, `lobby/messageslc.h:105-119`; modern replacement is passkeys plus explicit device authorization. | No authentication → WebAuthn origin/RP/challenge/signature/UV validation and browser approval with a separate high-entropy polling secret. `PasskeyService.cs:22-76`, `IdentityEndpoints.cs:19-83`, `IdentityStore.cs:127-157`. | Code/URL and account reach the shared intro through `LobbyIdentityClient` and `Game1.Identity.cs:15`. Real WebAuthn HTTP tests reject forgery and wrong origin; browser/CDP virtual authenticator and Classic/Modern window captures below. Baseline has no endpoints. |
| Trusted issuer and ticket validity | **Design:** native checks expected issuer, current entitlement and invalid tickets, `FedSrv/FedSrv.CPP:5834-5864`. **Era constraint:** encrypted Zone ticket replaced by ES256. | ZoneTicket decoded but ignored → pinned public-key verification of issuer, server audience, canonical account/name, issue/expiry times and token ID. `JoinTokens.cs:20-52`, `Server.Identity.cs:38-66`. | Signed UTF-8 compact token travels in existing `FM_C_LOGONREQ.ZoneTicket`, `Inc/Messages.h:36-51`. `P808IdentityLoopbackTests` rejects forged, expired, wrong-audience and malformed tickets on actual TCP; HTTP test rejects wrong issuer. Baseline probe accepts a forged ticket. |
| Canonical name/ID binding | **Design:** trust ticket name/ID, `FedSrv/FedSrv.CPP:5877-5904`; ACK already carries name/reason, `Inc/Messages.h:53-59`. | Asserted name seats player → `Server.Dispatch` calls `BindIdentity` before duplicate-name checks, privilege processing or allocation. Stable ID lives in `_peerIdentities` until disconnect. | Canonical name uses existing ACK and subsequent PLAYERINFO; account ID remains server-side, accessible by connection. No invented PLAYERINFO ID/verified bit. Loopback proves spoof replacement, account ID, reserved ship stability on duplicate pending logon, canonical roster name and replay refusal. |
| Anonymous policy | **Design:** standalone accepts supplied names; authenticated branch requires proof, `FedSrv/FedSrv.CPP:5831-5835,5870-5897`. | All servers accept free text → no identity block preserves legacy standalone operation; configured identity uses `AllowAnonymous` for absent tickets and rejects supplied invalid tickets. `Server.Identity.cs:41-51,68`. | Intro and directory label unverified identity; existing ACK reason says `(verified)` or `(unverified)`. `FValidated` keeps its native meaning of accepted logon. Loopback tests both policy settings. Existing handshake test now expects the explicit unverified suffix. |
| Server authorization | **Design precedent:** server announces to lobby and receives authorization token/NACK, `lobby/messagesls.h:34-47,79-81,116-119`. **Modern replacement:** operator-approved device enrollment. | A lease token proves only listing ownership → enrolled stable server ID/secret proves approval. `LobbyHeartbeat.EnrollAsync`, `IdentityStore.VerifyServer`, `LobbyEndpoints.cs:25-27`. Credentials persist privately and are reused after restart. | Modern directory HTTP metadata carries verified status and server audience. Arbitrary `verified=true` is ignored; forged server secrets return 401. `P808ServerEnrollmentTests` exercises source-generated heartbeat enrollment, credential permissions and restart reuse; real Compose/browser enrollment also passed. |
| Wire compatibility | **Design:** opaque ticket slot and existing ACK name/reason, `Inc/Messages.h:36-59`. | Placeholder ticket in desktop request → `Game1.Session.cs:999-1009` requests an audience-specific ticket when signed in and joining a verified listing; anonymous desktop requests omit the ticket. | No fmid, fixed layout, IGC-derived value or protocol version added. New heartbeat fields are omitted when null; the existing byte-identical anonymous-heartbeat test passes. Baseline has the ticket slot but ignores its authority. |
| Provider infrastructure | **Era constraint:** native Steam ticket validation, `lobby/CSteamValidation.cpp:15-53,84-94`; proprietary Club infrastructure, `Inc/ClubMessages.h:42-58`. | No providers → configured GitHub/Google/Steam middleware, off by default, and separate unique provider subject mappings. `ExternalProviders.cs`, `IdentityStore.ExternalAccount`. | Browser redirects/callbacks replace proprietary login UI. Disabled-provider and subject-isolation tests pass. Live Google/GitHub/Steam consent requires operator-owned credentials and was not exercised; native Steam wire-ticket validation is not claimed. This parity row stays **partial**. |

Callsigns fit native's 24-byte Latin-1 name field (`_Utility/Utility.h:60,64`):
1–24 printable Latin-1 characters. Outer spaces are excluded because the existing
wire encoder trims trailing spaces; control rejection follows the port's existing
name hygiene. Tests include one-character and accented names, 24/25-byte boundaries,
controls and unencodable characters. Browser input is trimmed before registration.

No native defect is deliberately reproduced. Trusting a caller-supplied account
name, allowing a self-asserted verified listing, or treating a listing lease as
operator approval would be **security bugs**. Native private duplicate-name
rejection remains; native USEAUTH's old-session takeover is not introduced here
(`FedSrv/FedSrv.CPP:600-629`, existing duplicate-logon defenses). A future takeover
policy should compare authenticated account IDs rather than untrusted names.

The unlicensed stellarallegiance [design](https://github.com/wivuu/stellarallegiance/blob/master/docs/LOBBY-ACCOUNTS-AND-RANKING.md)
and [ADR 0001](https://github.com/wivuu/stellarallegiance/blob/master/docs/adr/0001-public-lobby-is-its-own-identity-issuer.md)
were read for ideas only; no source code was copied. Fido2 is a licensed NuGet
dependency. Steam middleware's transitive AngleSharp was explicitly pinned to
patched 1.5.0 rather than suppressing [GHSA-pgww-w46g-26qg](https://github.com/advisories/GHSA-pgww-w46g-26qg).

## Deployment and security

[Docker deployment instructions](../../docker/README.md) cover bootstrap and all
configuration. No new application/library was needed: account services extend
`Allegiance.Lobby`; the shared token contract extends Core. The one-off real-window
harness is under `tools/p808a-identity-proof` and is built by the tools gate.

Both Compose variants validate and boot from fresh named volumes. The trial now
fetches a pinned, checksum-verified public native core/map into `trial-artwork`;
there is no private artwork or generated media in the image/build context. SQLite
runs migrations before readiness. PostgreSQL adds its own persistent volume,
healthcheck and a healthy dependency. Server identity has a separate volume.
Most importantly, game servers mount **only** `lobby-trust`, containing the public
PEM; they cannot read the lobby's private-key/database volume.

ES256 uses a persisted P-256 PKCS#8 private key (0600 on Unix), a fixed allowed
algorithm and a maximum 2-KiB ticket. Enrollment files are also created 0600.
Database sessions/server secrets and device/challenge secrets are hashed; the
short user code cannot redeem a session. Passkey registration is transactional;
challenge consumption, approval and grant redemption use conditional SQL writes.
Unique constraints enforce callsign/provider/credential uniqueness across requests.

Join tokens live 60 seconds. They bind issuer and server audience and have unique
IDs. Each server process consumes a token ID once until expiry; duplicate pending
logon resends the existing ACK. The replay cache is process-local, so restarting a
server permits replay of a still-unexpired ticket. The native transport is not
made confidential by this work: an on-path attacker can attempt preplay. A modern
next step is encrypted transport/channel binding and durable or distributed replay
tracking if multi-process game hosting is introduced.

Device grants/challenges expire after five minutes, sessions after 12 hours.
Polling is limited to one attempt per five seconds; browser/game sessions are
separate. Sign-out revokes the relevant session; already issued tickets can remain
usable until their short expiry, and accepted matches continue. Desktop sessions
stay in memory and require sign-in after restarting the client. Redemption is
single-use: if its response is lost, start authorization again.

Identity routes have a 30-request/minute source-IP limit plus the existing global
120/minute limiter; polling also has a database-enforced interval. Browser JSON
writes validate origin, cookie sessions are HttpOnly/SameSite Strict/Secure for
HTTPS, responses use no-store, and pages use safe DOM text with CSP/frame/referrer
headers. Only configured proxies can supply forwarded address/scheme. Production
requires an HTTPS public origin; localhost HTTP is supported for local passkeys.
For larger shared NATs, per-device/session quotas would scale better than the
initial conservative IP limit. Back up the database and keys; key overlap,
account recovery, linking additional passkeys/providers, revocation administration,
career statistics, ranking and squads are not part of this slice.

Servers without identity configuration retain opaque legacy-ticket compatibility.
Identity-configured servers reject invalid nonempty tickets even when anonymous
play is allowed; anonymous clients must omit the ticket. The desktop now does so.
No authenticated IDs/verification flags are invented for other pilots' native
PLAYERINFO messages. Trust labels describe the local account and directory entry;
the server ACK states whether that actual join was verified.

## Tests and baseline evidence

Twenty new cases (14 lobby/store/PostgreSQL, four Core/server and two shared-style
UI) passed three times each. Expanded affected-test groups passed **14 / 24 / 22**
cases per run, three runs each, with no skips and PostgreSQL enabled. Cases include
real ES256 WebAuthn attestation/assertion verification over HTTP, invalid origins
and signatures, durable identity/key reuse, callsign/provider uniqueness, challenge
expiry, device interval/approval/expiry/concurrent redemption, session revocation,
token issue/verify/expiry, operator authorization, listing-spoof rejection, native
wire binding, replay refusal, both anonymous policies and server enrollment reuse.

A read-only `git archive` of starting HEAD was built in scratch. Its device,
passkey-options and join-token routes returned **404**. A real baseline TCP probe
with `ZoneTicket="forged.expired.token"` was accepted as **AssertedName**. Those are
the regressions the new HTTP and loopback assertions expose; tests referencing new
identity types cannot compile on master without the implementation. Existing
anonymous heartbeat serialization and duplicate-logon defenses remain passing.

## Gate results

Final gate numbers and the compact machine-readable record are in
[verification.json](P808A_assets/verification.json). Raw logs/TRX remain under
`/home/domingo/allegiance-codex-p808a/.scratch/evidence/`; the sandbox does not allow
writing the lane's suggested external evidence directory. Bulk scratch is removed
at completion; these raw evidence files are retained because this report links to them.

- Solution build: **0 warnings / 0 errors**.
- `tools/build-tools.sh --drop-bins`: **75 projects**, all built with **0 warnings / 0 errors**.
- Server Native AOT publish: **linux-x64 passed**, no IL2026/IL3050 diagnostics; actual AOT server also ran in both Compose smokes.
- New/affected test groups: **14 / 24 / 22 passed**, **3/3** each; PostgreSQL test included, no skips.
- One Fast-tier run: **10,241 cases**, **10,231 passed / 7 failed / 3 not executed**, **300.7 seconds**; infrastructure **9/9 passed**. This aggregate gate is **not green**.
- Exact HUD allocation test: **3/3 isolated pass**. Exact profile-editor test with writable XDG configuration: **3/3 isolated pass**.
- Both Compose configurations: **valid**. Both fresh-volume stacks: **started**. PostgreSQL migration version **1** observed; SQLite enrollment survived lobby/server recreation with the same server identity.
- Glossary: **110 paths valid**. Parity CSV validators, LF check, working-tree privacy/secret-pattern scan and `git diff --check`: **passed**.

The Fast run was not repeated. Its five path-fixture failures are the already
ledgered HUD-15/HUD-12/SC2 ancestor-`local.env` baseline when TMPDIR is inside a
worktree. The HUD allocation failure is the P787a flake (1,832 B against <1,024 B).
The profile-editor failure attempted to restore an existing file in the sandbox's
read-only home directory. Isolated recovery uses a worktree-owned XDG config path;
no unrelated production assertions or test behavior were weakened.

## Real-window evidence

The headed Chromium flow used its CDP virtual authenticator with resident keys,
user verification and real browser WebAuthn calls. This verifies browser/library
integration, not a physical hardware authenticator. The harness opens the actual
Game1 window and injects one F2 key state into the production handler; approval
and polling are real HTTP. Screenshots use the production backbuffer flush.
The sandbox's default Firefox launcher cannot create its profile, so approval was
performed in the controlled headed Chromium window using the displayed URL.

Every cited image was opened and visually inspected. Intro rendering is shared,
so Classic and Modern intentionally show the same account controls and typography.

| Evidence | What is actually visible |
|---|---|
| [Browser passkey account](P808A_assets/browser-passkey.png) | Signed-in callsign, stable account ID, browser sign-out and device-code entry. |
| [Operator approval](P808A_assets/browser-server-approval.png) | Reviewed game-server purpose/name, short code and explicit approval button. |
| [Classic pending](P808A_assets/intro-classic-pending.png), [Modern pending](P808A_assets/intro-modern-pending.png) | Main menu, code and localhost approval address, anonymous pilot explicitly unverified. These are separate pending authorizations. |
| [Classic approved](P808A_assets/intro-classic-signed-in.png), [Modern approved](P808A_assets/intro-modern-signed-in.png) | Signed-in status, canonical VisualP808 callsign marked verified and F2 sign-out affordance. |
| [Classic browser approval](P808A_assets/browser-player-approval-classic.png), [Modern browser approval](P808A_assets/browser-player-approval-modern.png) | Reviewed **game sign-in** purpose and approval button for each approved game session. |
| Server-browser trust markers | The `[verified]`/`[unverified]` row prefixes are covered by `P808IdentityScreenTests`. The original directory captures were removed at merge: they predated P806's eight-column sort header and no longer matched the screen. |

Visual review caught trust text being clipped at the end of long directory rows;
trust now appears at the start of shared row formatting. The directory harness also
waits for the list layout to settle before capturing. Final affected UI cases were
rerun 3/3 after that adjustment. No second Fast-tier invocation was made.

New media is below the task's 2-MiB evidence budget and requires Domingo's normal
review before the dispatcher's eventual push. No media was pushed by this lane.

## Parity tracking and handoff

[Identity matrix](../parity/p808a-identity.csv): seven executable/tested rules done,
one provider/native-Steam infrastructure row partial. The master matrix's obsolete
“Zone Club screen (online account) = n/a” was changed to **partial**: identity now
exists, while full career/squad Club screens remain P808b/c. Glossary links and
both matrices validate; CSV line endings remain LF.

Operational handoff: create a browser account, set its displayed ID in the lobby's
operator allowlist, approve the server's printed code, then use F2 in the desktop
intro. Optional provider credentials and production TLS are operator configuration;
no secrets are committed. Test stacks are stopped and disposable task volumes
removed at completion. Source changes, report and small evidence remain uncommitted.
