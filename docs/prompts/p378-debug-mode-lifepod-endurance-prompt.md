# P378 — a real, config-driven "debug mode" (first use: 30-minute lifepod endurance for testing)

## Why this task exists

Domingo asked directly: "we need to have some kind of debug mode,
while active the timeout would be 30 minutes so i can test stuff
properly" — the real 5-minute lifepod oxygen timer (P279, faithfully
ported from native) makes repeated live rescue/collision testing this
session tedious. Wants a genuine, reusable debug-mode toggle, not a
one-off hack — this is explicitly the first of possibly more debug
conveniences, so build the mechanism generally.

**A real seam already exists and should be reused, not duplicated**:
`src/Allegiance.Core/Sim/Ship.cs:187`,
`public double? LifepodEnduranceOverrideSeconds { get; set; }` — a
per-ship override already read by the real endurance calculation
(`Ship.cs:218-221`), currently only ever set by tests. This task wires
a real runtime debug-mode flag to it, rather than inventing a second
mechanism.

## Scope

1. **Add a real, explicit, dedicated "live settings" section to the
   server's config surface** — check `docker/compose.server.json` and
   however `Server`/`ServerHost` already reads its config (`Server.cs`'s
   startup/config-loading code) for the established pattern. Domingo
   explicitly asked for this to be its own clearly-separated section
   (e.g. a `"liveSettings": { "debugMode": true, ... }` object), not a
   flat top-level boolean mixed in with unrelated config — the point is
   a single, obvious, growable place for runtime/testing-tunable
   settings as more get added later, distinct from the mission/network/
   artwork-path config that's already there. `debugMode` is the first
   real field in it. Off by default. Do NOT gate this behind a magic
   env var or an undocumented flag — it must be a real, visible,
   documented config section.
2. **Make the lifepod endurance itself a real, optional `liveSettings`
   field (e.g. `lifepodEnduranceSeconds`), not a value hardcoded only
   to the debug-mode boolean** — Domingo's own follow-up made this
   explicit: "leave the hardcoded 5 minute as default if no setting is
   provided." So the real rule is:
   - No `liveSettings`/no `lifepodEnduranceSeconds` field present at
     all → behavior is BYTE-FOR-BYTE IDENTICAL to today: the real
     native 300s (5 min) from `FloatConstants[c_fcidLifepodEndurance]`,
     completely unchanged. This must never regress for a normal server
     run with no config changes.
   - `liveSettings.lifepodEnduranceSeconds` explicitly set → use that
     value instead (e.g. 1800 for a 30-min debug session, or 600 for a
     10-min "normal but more forgiving live play" session — this is a
     real general-purpose tunable, not just a debug-mode-only value).
   - `debugMode: true` alone (with no explicit
     `lifepodEnduranceSeconds`) should still give a sensible generous
     default for testing (1800s/30 min) — but an explicit
     `lifepodEnduranceSeconds` value, if present, wins regardless of
     `debugMode`.
   Find the real spawn/hull-swap path this applies to (`Ship.cs`'s own
   constructor/hull-swap logic, ~line 205-227, already reads
   `LifepodEnduranceOverrideSeconds`) and thread the resolved value
   through consistently, not just at one call site.
3. **Design the flag as a genuine reusable "debug mode" concept**, not
   a single-purpose "lifepod timeout" toggle — name it and structure it
   so a future debug convenience (Domingo explicitly said "so i can
   test stuff properly," implying more may follow) can gate on the same
   flag without a second config knob. Don't over-engineer a whole
   framework for hypothetical future uses either — a simple, clearly-
   named boolean read from one place is enough; just don't name it
   `LifepodDebugMode` when `DebugMode` is the honest, reusable name.
4. Log plainly when debug mode is active at boot (matching this
   project's existing convention of loud `[boot]`-style startup
   logging) so it's never silently left on by accident.

## Explicitly out of scope

- Any OTHER specific debug convenience beyond the 30-minute lifepod
  timeout — this task only wires the flag + this one real use, not a
  speculative list of future debug features.
- Redesigning the oxygen mechanic itself — untouched, just given an
  optional override path already built for exactly this.
- P376/P377 — separate, don't touch (P377's own lifepod-countdown
  display work should read whatever real deadline is active, debug or
  not, automatically — no special-casing needed there if P377 reads
  the real deadline value correctly).

## Verification — real proof, not just build/test

- **Live proof, required**: start the server with debug mode OFF,
  confirm a lifepod still dies at the real 5-minute mark (or close
  enough to verify via log timestamps). Restart with debug mode ON,
  confirm a lifepod now survives well past 5 minutes and dies close to
  the real 30-minute mark instead (checking the actual `[oxygen]`
  deadline log line's value is sufficient — you don't need to
  literally wait 30 real minutes if the deadline value itself is
  directly verifiable from the log/state, but state clearly in the
  report which you actually did).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite (`/home/domingo/.dotnet/dotnet test`), run at least
  2x: only the pre-existing documented live-wire timing flakes are
  expected.

## Deliverables

1. The debug-mode flag + 30-minute lifepod override, committed on
   `master` (not pushed — Domingo pushes). Do not create a side
   branch.
2. Report doc `P378_DEBUG_MODE.md` in the repo root: the exact config
   surface added, how it threads through to the lifepod endurance
   override, and the live verification performed (debug on vs off).
3. Tell Domingo directly, in the report's opening lines, the exact
   config change needed to turn debug mode on for the next live test
   session (e.g. the exact JSON field/value to set in
   `docker/compose.server.json`).
