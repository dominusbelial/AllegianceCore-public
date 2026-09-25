# P378 — Config-Driven "Debug Mode" + 30-Minute Lifepod Oxygen Endurance

## ⚡ To turn debug mode on for your NEXT live test session

Edit `docker/compose.server.json` (the live config) and add a `liveSettings`
section. The simplest form — a 30-minute lifepod oxygen window:

```json
"liveSettings": { "debugMode": true }
```

Drop that object anywhere at the top level (sibling of `"port"`, `"games"`,
etc.). That is the whole change. On boot the server logs the loud line:

```
[boot] *** DEBUG MODE ACTIVE (liveSettings.debugMode=true) — lifepod oxygen endurance = 1800s (30-min testing window; explicit liveSettings.lifepodEnduranceSeconds wins) ***
```

Prefer a different endurance (e.g. a 10-minute "more forgiving live play" run)?
An explicit value always wins over `debugMode`'s default:

```json
"liveSettings": { "lifepodEnduranceSeconds": 600 }
```

Remove the section (or set nothing) and a normal server run is byte-for-byte
identical to before P378 — the real native 300 s (5 min).

---

## What was added

A dedicated, clearly-separated **live/testing settings** section in the server
config — the single, obvious, growable home for runtime/testing-tunable knobs,
distinct from the mission / network / artwork-path config. It is *not* a
flat top-level boolean mixed in with unrelated config, and it is *not* gated
behind a hidden env var or undocumented flag: it is a real, visible,
documented config section.

**`liveSettings` object** (all fields optional):

| Field | Type | Default | Meaning |
|---|---|---|---|
| `debugMode` | bool | `false` | The reusable debug-mode flag. Off by default; loud `[boot]` log when on so it is never silently left on. Named `DebugMode` (not `LifepodDebugMode`) because it is a genuine reusable concept — the next debug convenience gates on this same flag with no second knob. |
| `lifepodEnduranceSeconds` | double? | `null` (unset) | Override of the lifepod oxygen endurance in seconds. `null` = today's exact behaviour (the core's native `FloatConstants[31]` = 300 s). Any explicit value wins regardless of `debugMode`. |

Resolution precedence (Domingo's design, implemented exactly):
1. explicit `lifepodEnduranceSeconds` **always** wins (general-purpose tunable, not debug-only);
2. else `debugMode: true` → `1800` s (30 min);
3. else → `null`, i.e. the engine falls back to the real native 300 s — **completely unchanged**.

Implementation locations:
- `src/Allegiance.Server/ServerConfig.cs` — new `LiveSettingsConfig` class +
  `ServerConfig.LiveSettings` property (default `new()`).
- `src/Allegiance.Server/ServerJsonContext.cs` — registered the new type for the
  source-generated (Native-AOT-safe) serializer.
- `src/Allegiance.Server/Server.Lifecycle.cs` — `Boot` resolves the override via
  `ResolveLifepodEnduranceOverride` and logs the debug state loudly.
- `src/Allegiance.Server/Server.cs` — resolved value stored on the server and
  stamped onto every server-created `Ship` (`SpawnDefaultShip` / `SpawnNpcShip`)
  as `Ship.LifepodEnduranceOverrideSeconds`.

## How it threads through to the oxygen deadline

`Ship` already had the real seam (`Ship.LifepodEnduranceOverrideSeconds`,
`Ship.cs:187`, read by `ChangeHull`/`LifepodEnduranceFraction`), previously only
ever set by tests. This task reuses it rather than duplicating a second
mechanism:

```
config.liveSettings
  → Boot(): ResolveLifepodEnduranceOverride()   (explicit > debugMode:1800 > null)
  → Server._lifepodEnduranceOverrideSeconds
  → stamped on every Ship at spawn (SpawnDefaultShip / SpawnNpcShip)
  → Ship.ChangeHull(lifepodHull) → LifepodTimeToDie = now + endurance
    (the SAME hull-swap the death / eject / rescue paths run)
  → Ship.OnUpdate / LifepodEnduranceFraction use that real deadline
```

Because every server-created ship carries the override and the endurance is
consumed at hull-swap time, **all** lifepod-forming paths (in-space eject,
death-respawn, abandon-ship, teleport-into-lifepod) pick it up automatically —
there is no second call site to keep in sync. P377's countdown display reads
whatever real deadline is active, so it reflects the debug value with no
special-casing.

### Note on absent sections (found & handled)

System.Text.Json does **not** run a property's `= new()` initializer when the
`init` property is absent from the JSON — so `_config.LiveSettings` came back
`null` when no `liveSettings` key was present. `ResolveLifepodEnduranceOverride`
and `Boot` therefore treat a null section exactly like an all-defaults section
(debug off, no override), which is precisely the required "absent = unchanged"
rule. Verified empirically (the no-section boot crashed before the guard; it
boots cleanly after).

## Live verification performed

This is REAL proof against the real server, not just a build — but stated
honestly, I verified the **deadline values** directly rather than waiting the
literal 5 / 30 real minutes, which the task explicitly permits.

**(a) Binary boot, real `docker/compose.server.json`-shaped configs.** Ran the
compiled `Allegiance.Server` binary against real artwork with three configs:

| Config | Boot log result |
|---|---|
| no `liveSettings` (debug OFF) | no debug/endurance line; boots to `[boot] server ready` — unchanged |
| `"liveSettings": { "debugMode": true }` | `*** DEBUG MODE ACTIVE ... = 1800s ... ***` |
| `"liveSettings": { "lifepodEnduranceSeconds": 600 }` | `[boot] liveSettings.lifepodEnduranceSeconds = 600s ... (debug mode off)` |

**(b) In-process live tests through the REAL default spawner** (no injected
spawner), loopback TCP, peer joins, real `Ship.ChangeHull` to the Lifepod hull,
reading the armed deadline (`LifepodTimeToDie − now`) — the exact value the
engine counts down:

```
[oxygen] P378 debug-off: real spawned ship 8096 armed deadline = now+300s  (core FloatConstants[31]=300s) — unchanged from pre-P378.
[oxygen] P378 debug-ON:  real spawned ship 8096 armed deadline = now+1800s (30-min testing window).
[oxygen] P378 explicit 600s: real spawned ship 8096 armed deadline = now+600s.
```

So: debug OFF → pod dies at ~5 min (native). Debug ON → pod survives well past
5 min and dies near ~30 min instead. Explicit value → its value wins. New test
file: `tests/Allegiance.Core.Tests/LiveP378DebugModeTests.cs` (3 Facts, all
green in every run).

## Build & test suite

- Whole-solution build (`dotnet build AllegianceCore.slnx`): **0 warnings, 0 errors**.
- Full `dotnet test` suite run 2×: only the pre-existing documented live-wire /
  seeded-session timing flakes appeared (a different rotating subset each run —
  `LiveTurretGunnerBindingTests` [the persistent documented flake], plus
  `LiveMoneyTests.Board_refund…`, `LiveChatSectorWireTests`,
  `NpcShipFoundationLiveTests`, `GameOverPlayersWireTests`,
  `P366LeaderBotCommandLiveTests`, and a ClientCore seeded-session flake —
  all documented families from earlier P-series reports, none referencing any
  P378 symbol). `LiveP378DebugModeTests` passed 3/3 in every run; the run-2
  flakes re-pass in isolation, confirming they are transient env flakes, not
  regressions.

## Out of scope (respected)

- No other debug convenience was added beyond the 30-minute lifepod timeout —
  only the reusable flag + this one real use.
- The oxygen mechanic itself is untouched — it just gained the override path
  that already existed for exactly this.
- P376/P377 untouched; P377's countdown reads the real deadline automatically.
