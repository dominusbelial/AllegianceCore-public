# Browser generation hosting notes

These notes describe the intended static-file cache contract for a future host.
P1053 changes publication and browser startup only; it does not configure a host,
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
