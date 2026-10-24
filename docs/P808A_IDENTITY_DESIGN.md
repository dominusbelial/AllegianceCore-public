# P808a: lobby-issued player identity

The lobby owns immutable random account IDs and case-insensitively unique callsigns.
A callsign is 1–24 printable Latin-1 characters, fitting native `c_cbName`
and the native Latin-1 name fields. Outer spaces are excluded to avoid the
wire encoder trimming a signed canonical name.
Anonymous players remain available according to each game server's policy.

## Trust and protocol

A browser registers or authenticates a discoverable, user-verified WebAuthn passkey.
The lobby validates origin, relying-party ID, challenge, signature and authenticator
counter using Fido2, then creates a revocable, hashed, expiring session. GitHub,
Google and Steam are alternative identity proofs only when explicitly configured;
provider subject IDs are unique, never linked by an unverified email or callsign.
The first provider login must choose an unused callsign. No password or test login
endpoint is deployed.

The game starts a five-minute device authorization, shows its short user code and
approval address, and polls with a separate 256-bit secret at five-second intervals.
An authenticated browser explicitly approves the displayed purpose. The short code
alone never authorizes polling. Approval and redemption are atomic and single use.
The game holds its session in memory; restart requires sign-in, and sign-out revokes
that session. Browser sign-out revokes its own session independently.

Before joining a listed server the client requests a 60-second ES256 token, scoped
to that server's persistent identity. Claims bind issuer, audience, subject,
callsign, issue/expiry time and unique token ID. The existing opaque
`FM_C_LOGONREQ.ZoneTicket` carries the UTF-8 compact token. `LOGONACK` carries the
accepted canonical callsign or rejection; subsequent native player-info messages
already carry the canonical name. The stable account ID remains server-side: no
new native game-message field is invented. Native Club/Zone trust is the precedent
(`Inc/ClubMessages.h:42-58`, `lobby/messageslc.h:105-119`,
`FedSrv/FedSrv.CPP:5824-5904`). Passkeys, sessions, modern crypto and HTTP device
approval replace obsolete Zone services; they are intentional modern mechanisms.

Game servers pin a lobby issuer/public key and their audience in configuration.
Verification is offline and fail closed for supplied invalid credentials. A server
without identity configured retains legacy anonymous operation, including opaque
legacy ZoneTickets. An identity-configured server allows absent tickets only when
`AllowAnonymous` is true. Token IDs are consumed once per process until expiration;
a duplicate pending logon reuses its existing ACK. A verified account's callsign
replaces caller-supplied text before duplicate-name checks, privilege checks or
allocation. Anonymous ACKs explicitly say unverified; `FValidated` continues to mean
accepted logon, not account verification. This avoids changing native field meaning.

## Server enrollment

The operator opts into enrollment. The server obtains a device authorization of
purpose `server`; only a browser account in the lobby's configured operator ID
allowlist can approve it. Redemption yields a durable server ID and secret saved
in a private file. Registration proves that secret; the lobby sets verification
metadata itself. Listings remain leases in memory, while enrollment survives lobby
and server restart. Clients request join tokens only for live verified listings.
Anonymous servers cannot self-assert verification. The server public key is pinned
by deployment configuration, never accepted from an arbitrary directory response.

## Persistence and deployment

A small versioned SQL migration creates accounts, credentials, external subjects,
sessions, device grants, challenges and server credentials. SQLite is the default;
PostgreSQL uses the same parameterized SQL and constraints. Expired ephemeral rows
are pruned during authorization writes. Database unique constraints and conditional
updates enforce cross-request races; migrations are serialized at startup.
No separate app is needed. SQLite and signing keys live in a named lobby volume;
server enrollment has its own volume. PostgreSQL has an optional compose override,
healthcheck and a dependency. Migrations run before readiness.

The signing key is persisted with owner-only file permissions; operators may mount
an existing PKCS#8 key. Only its public SPKI is exposed. Production requires HTTPS
at the public lobby origin; localhost HTTP supports local passkey testing.
Cookie sessions are HttpOnly, SameSite=Strict and Secure for HTTPS. Auth endpoints
are rate-limited by source IP, require same-origin JSON browser writes, never put
secrets in URLs and return no-store responses. No callsign/profile editing, account
recovery, career statistics, squads or ranking are part of this slice.

## Verification strategy

Exercise real HTTP passkey registration and assertion with a virtual authenticator,
then device approval/polling, token issuance, persistence, case-insensitive name
collisions and operator enrollment. Test invalid proofs, wrong origin/challenge,
expiry, replay, revocation and concurrent redemption. Drive actual loopback TCP
logons to prove valid canonical binding, forged/expired refusal and anonymous
policy. Shared intro UI and directory labels are tested and captured with Classic
and Modern. Build the solution and tools, publish the server with Native AOT, run
new tests three times alone and the Fast tier once. Validate both compose variants
and smoke fresh-volume startup. Record exact evidence and limitations in the task
report rather than claiming unexecuted checks.

## Sources and classification

The unlicensed stellarallegiance design and ADR were read for ideas only; no source
was copied: https://github.com/wivuu/stellarallegiance/blob/master/docs/LOBBY-ACCOUNTS-AND-RANKING.md
and https://github.com/wivuu/stellarallegiance/blob/master/docs/adr/0001-public-lobby-is-its-own-identity-issuer.md.
The Fido2 library is a licensed dependency, not an implementation of WebAuthn
cryptography in the lobby: https://github.com/passwordless-lib/fido2-net-lib.
Native authenticated identity and canonical names are design rules; encrypted Zone
tickets and proprietary providers are era constraints. Trusting an asserted name
or treating a listing lease token as operator approval would be port security bugs.
