# Security Policy

## Reporting a vulnerability

Please report vulnerabilities privately through **GitHub's private
vulnerability reporting** (Security Advisories) on this repository. There is
**no** supported email channel — please do not send security reports by email
or seek out any personal contact address.

- Go to the repository's **Security** tab → **Report a vulnerability**.
- Include a description of the issue, the affected component/file, and, if
  possible, a minimal reproduction.

You will receive an acknowledgement. Because this project is maintained in
spare time, please allow time for a response.

## Scope

The server is a **LAN / demo-grade game server**, not a hardened internet
service. Its threat model is: casual abuse / accidents among a small group of
trusted players on a local network. It is **not** designed to resist a hostile
attacker on the public internet, and you should not expose it that way.

## Known non-hardened areas

These are intentional trade-offs for a demo/experimental server. They are
documented here and in [`docs/GUIDE.md`](docs/GUIDE.md) so operators can make
an informed call — not silently "fixed" by pretending the server is hardened.

- **Admin cheats are name-based.** `liveSettings.adminCheats` (off by
  default) trusts self-declared character names (including prefix wildcards
  such as `mcp-*`). A name is client-supplied and spoofable. Mitigations:
  never enable admin cheats on a public/shared server (the server prints a
  loud boot warning when it is enabled on all interfaces), and optionally set
  `adminCheats.secret`, in which case an allow-listed connection is trusted
  only when its cheat line presents the secret as its first token.
- **Lobby register/delete is unauthenticated.** The lobby's `DELETE` uses a
  query-string token and registration is open. This is fine for a directory of
  LAN servers; do not treat it as an ACL.
- **No TLS.** The client↔server transport (TCP 6073) and the lobby HTTP
  endpoints are plaintext. Game data and logons are not encrypted in transit.
- **Client-reported data is trusted where applicable.** Certain fields come
  from the client and are not independently re-validated beyond what the
  simulation itself enforces.

## Safe deployment guidance

- Bind the server to a loopback or trusted private address when you can; the
  default server binds `0.0.0.0` (all interfaces).
- Keep `debugMode`, `testHarness`, `testUnlock` and `adminCheats` **off** for
  anything you care about. They exist for live testing and are off by default.
- Do not forward the game/lobby ports to the public internet.
