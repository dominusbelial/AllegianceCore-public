# Browser generation hosting notes

These notes describe the intended static-file cache contract for a future host.
P1053/P1054 change publication, browser startup and visual delivery only; it does not configure a host,
CDN, TLS, CSP, lobby `/play/` route, or compose service.

[`scripts/publish-browser-flight.sh`](../scripts/publish-browser-flight.sh) finalizes a complete local generation with
[`tools/p1053-browser-publish.py`](../tools/p1053-browser-publish.py). The .NET SDK's fingerprinted WASM/runtime files
keep their integrity names; remaining boot, application JS/CSS, KNI interop and
notices receive SHA-256-derived names. `menu.html`/`index.html` embed the generated
map for runtime imports. Boot preloads activate after the existing WebAssembly /
WebGL capability check, before the ordered interop script evaluation. The title
and progress shell exist in static HTML before .NET starts.

Initial staging contains the two menu font bakes and click sound. Join reads
and verifies the complete manifest, prepares the Brotli decoder, and retires
obsolete cache entries before requesting a fresh ticket. Menu-only staging
preserves previous complete join entries; per-file checks and the existing
64 MiB/1,024-entry cache limits still apply.

P1054's `sector-manifest.json` maps the recorded map/seed and sector IDs to
content-addressed original visual files. Join stages only the gameplay base and
entry visuals before the ticket. The four entry asteroid surfaces initially use
the tested 256² copies; their original 2048² files have highest delivery priority
after launch. Sector entry and speculative target/starmap prefetch stage visuals
without awaiting them in simulation or input. Missing sky textures leave the
normal clear/stars/sun components; missing remote rock textures use the existing
lit-solid geometry. Completed textures replace them after bounded row preparation.
Progress and retry stay non-modal. The sector index's hashes are retained when
committing the base manifest, so an otherwise warm base commit does not purge
streamed visuals. Bounded eviction still applies after a larger sector tour;
zero-byte warm reload is qualified for the fitting base + entry working set.

## Developer/proof switch (P1059)

The flight toolbar has three controls that exist for the proof harnesses, not for a player:
**View nearby asteroid** (`#flight-rock`, P1054 screenshots), the typed-coordinate **Waypoint**
form (`#flight-waypoint`, `#waypoint-x/y/z`, `#waypoint-go`), and the authoritative
**`#online-pose`** readout. They are hidden from players and kept for developers/harnesses —
nothing is deleted, and every game method behind them (`Game1.ToggleRockView`,
`Game1.FlyToWaypoint`, the `_pose` snapshot) stays.

One switch, resolved by `wwwroot/dev-switch.js` before the .NET runtime starts:

| Way to set it | Effect |
|---|---|
| `?dev=1` on `menu.html` | renders the developer/proof controls for that visit, and keeps them on for later visits (`localStorage['allegiance.dev']='1'`) |
| `localStorage['allegiance.dev'] = '1'` | renders them for every visit until it is cleared |
| `?dev=0` | turns the switch off again (stores `'0'`, overriding a stored `'1'`) |
| `window.allegianceDev.setEnabled(true\|false)` | switches it live without a reload |

The default is **off**: with it off those controls are not rendered at all (not merely
CSS-hidden), so they cannot be clicked or tabbed to. They have no keyboard binding, so there is no
key path to disable. The switch only changes which page controls render: it touches no game rule,
wire message, server check, limit or privilege, and the shared desktop client does not load it.
Numerically nothing changes for a player — the game methods and their native behaviour are
identical in both states.

Harnesses that click a developer/proof control open the page with `?dev=1`
(`tools/p1059-browser-devcases.cjs` for the switch itself; `tools/p1051-browser-flight.cjs`,
`p1051-browser-security.cjs`, `p1048b-browser-join.cjs`, `p1053-browser-polish.cjs`,
`p1054-browser-sectors.cjs` and `p1058-browser-launch.cjs`). Classification and native citations:
`docs/reports/BROWSER_DEV_CONTROLS_P1059.md`.

Intended headers for a future host (this story changes no host, CSP, CDN or lobby
`/play/` serving):

| File class | Intended Cache-Control |
|---|---|
| HTML entry points; `*-manifest.json`, receipts and other mutable JSON metadata | `no-store` |
| Content-addressed JS/CSS/WASM, SDK fingerprinted `_framework` files, `packs/<sha256>`, hashed `art/` and notices | `public, max-age=31536000, immutable` |
| Any remaining stable-name compatibility file | `no-cache` |

Keep MIME types, integrity validation, same-origin credentials and matching
compression headers intact. Publish complete generations atomically and retain
old immutable files while old tabs can reference them. The local proof proxy
continues to send `no-cache` for every HTTP file; warm zero pack transfer is
provided by the existing verified browser Cache API, independently of these
intended hosting headers. No compose service, port, store or environment variable
is added.
