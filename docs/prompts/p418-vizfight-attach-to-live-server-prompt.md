# P418 — vizfight watches (and drives real action on) the SAME live server the MonoGame client connects to

## Why this task exists

Domingo's actual goal: a real, credible "proof the port works" showcase —
the browser spectator view and a real MonoGame client both showing the
**same live action on the same real server**, side by side. Today
`vizfight`'s `showcase`/`explore`/`roster`/`proof` modes each boot their
**own private, self-contained in-process `Server`** with their own
private bots — completely isolated from whatever real server (e.g.
`192.168.0.221:6073`) a real MonoGame client is actually connected to.
That's why the two never showed the same thing: they were never the
same game.

## Confirmed real building blocks already in this codebase

`Allegiance.ClientCore` (already project-referenced by
`Allegiance.ClientCore.MonoGame`, and reachable from `vizfight` too since
it already references `Allegiance.ClientCore.MonoGame`) has a complete,
**headless, non-rendering** real-network client stack — the exact same
one the MonoGame client itself uses for correctness, so reusing it means
this new mode speaks the real wire protocol, not a simplified
reimplementation:
- `Session/ConnectionSession.cs` — real connect/logon/side-select/
  ready-up over TCP to any host:port.
- `State/ClientWorld.cs` — decoded live world state:
  `Ships` (`IReadOnlyDictionary<short, ShipEntity>`), each with
  position/hull/side/HP-fraction/dead-or-alive (via `ShipEntity.Latest`,
  a `ServerShipUpdate`) — everything `TickFrame`'s JSON schema needs.
- `State/EventLog.cs` — `Kills` (`IReadOnlyList<KillShip>`), the real
  decoded kill messages — exactly the "kill" event type the existing
  frontend (`wwwroot/index.html`) already knows how to render.

`vizfight`'s own `TickFrame`/`SpectatorHub`/web frontend
(`wwwroot/index.html`) are otherwise unchanged and reusable as-is — they
just need their `ships`/`models`/`kill` JSON sourced from a
`ClientWorld` instead of directly from an in-process `Srv`.

## Scope — two coupled halves, both required for the actual goal

### 1. New `attach` mode: a real spectator client, not a private server

Add a mode (e.g. `vizfight attach <host> <port> <webPort>`) that:
connects to the given external server via `ConnectionSession` (a real
peer login — investigate whether a lightweight "just observe, don't
seat a ship" login path exists or is worth adding; if not, seating a
throwaway spectator ship and never flying it is an acceptable fallback,
document the choice), tracks `ClientWorld`, and on the same tick
cadence this port's other modes already use, serializes `ClientWorld`'s
real ship/kill state into the exact same JSON shape `TickFrame` already
produces, broadcasting it through the existing `SpectatorHub` to the
unchanged web frontend.

### 2. Real, visible action on that same real server

The web view is only interesting if something is actually happening on
the real server it's now watching. Get real bot ships flying (and
ideally fighting) on the actual server the MonoGame client is also
connected to:
- Reuse `showcase`'s existing bot-connection machinery
  (`StepShowcase`/`BroadcastShowcaseKills`/the real
  diplomacy→skirmish→flag→economy→vote arc — already built, already
  proven this session) as the starting point, since it's the only
  already-working real-combat choreography in this codebase.
- **Real architectural snag, investigate and decide honestly**: some of
  showcase's scripted beats may currently rely on **direct in-process
  manipulation** of the `Server`/`Mission` objects it owns (forcing
  alliances, precise ship/hull assignment, etc.) rather than real wire
  messages — because today it always owns the server it's choreographing.
  Against an **external** server, none of that direct access exists;
  every beat must go through real client-visible commands only (join,
  ready-up, move/fire requests, chat/alliance requests), exactly as a
  real player's client would. Trace which beats (if any) need this and
  either re-derive them through real wire-only interactions, or — if a
  specific beat is genuinely impossible without direct server access —
  drop or simplify just that beat and say so plainly in the report.
  Don't fake a beat's visible effect without the real underlying action
  actually happening on the server.
- If full narrative parity isn't achievable wire-only, a simpler, honest
  fallback is legitimate and still valuable: real bots that join sides,
  undock, and fly/mine visibly (reusing the already-proven P381/P382
  NPC-autonomy-adjacent behavior or `Allegiance.Bot`'s existing
  primitives) — real, visible, wire-only action is the actual point,
  a full scripted trailer is a nice-to-have on top.

## Explicitly out of scope

- Any change to the real production server's own gameplay logic —
  `attach` mode and its bots are ordinary clients/peers, indistinguishable
  from a real player's connection.
- `vizfight`'s existing self-hosted modes (`explore`/`roster`/`proof`,
  and `showcase`'s own self-hosted path) — keep them working exactly as
  they are; `attach` is a new, additional mode, not a replacement.
- P368-P417 — unrelated, don't touch.

## Verification — real proof, not just build/test

- **Live proof, required, this IS the actual point of the task**: boot
  the real production server (`192.168.0.221:6073`, non-debug,
  `m2xspiderw.igc` — the exact setup already in daily use this session),
  connect a real MonoGame client to it, launch `vizfight attach` pointed
  at the same host:port, and connect real bots to that same server too.
  Confirm: the MonoGame client shows the bots flying/fighting live, and
  the `attach` mode's web page shows the **same real ships, same real
  positions, same real kills** at the same time — genuinely the same
  game, two views. Screenshot or describe both side by side in the
  report.
- A test proving the `ClientWorld`→`TickFrame`-JSON translation is
  correct (ship fields map correctly, a real `KillShip` produces a real
  "kill" event in the output).
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The new `attach` mode + whatever bot-choreography reuse/adaptation
   was needed, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P418_VIZFIGHT_ATTACH_LIVE_SERVER.md` in the repo root:
   the `attach` mode's design, exactly which showcase beats needed
   wire-only re-derivation (or were dropped/simplified, and why), and
   the side-by-side live verification performed.
