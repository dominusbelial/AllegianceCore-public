# P425 — command a builder drone (or any owned NPC) from the MonoGame client: minimal 2-target order UI

## Why this task exists

Domingo, after P422 landed the real Build command: *"how do I control the
builder?"* Traced the real mechanic: Builder ships are never player-piloted
(native always spawns them as command-driven drones, `CreateStockDrone`,
`FedSrv.CPP:2220,11351`) — controlling one means commanding it, not flying
it. Investigated further and found the commanding mechanism **already
exists and is more general than P424's investigation found**: this task
is the minimal client-side hookup to actually use it, not a new mechanic.

## Confirmed real mechanic already working, end to end, server-side

`Server.ShipCommands.cs:1054-1069` (`HandleOrderChange`):
```csharp
var target = FindSameSideShip(req.ShipID, ship.SideId, mission) ?? ship;
```
The `OrderChange` wire message already carries a real `ShipID` field
letting the sender command **any same-side ship**, including NPCs/drones
— not just their own ship (falls back to the sender's own ship only when
`ShipID` doesn't resolve). `OrderChange.CidBuild` is **already a fully
working case** (`Server.ShipCommands.cs:1266-1270`, `HandlePlayerBuildOrder`,
landed in P422): send `ShipID` = the drone you're commanding,
`CommandId = CidBuild`, `ObjectType = Asteroid`, `ObjectID` = the target
rock, and the server does the rest — real legality gate, real timed
construction, real station on completion (all proven live in P422).

**This is the *direct* command path** (NPCs obey immediately, no
accept/reject) — distinct from the *queued* path P414/P424 built for
commanding other real players (who get to accept/reject/ignore, since
they have free will and an NPC doesn't). Don't conflate the two or route
this through the queued/accept-reject machinery — a drone should obey
directly, exactly as native does.

## Confirmed gap

Nothing in `Allegiance.ClientCore.MonoGame` sends `OrderChange` with a
non-self `ShipID` at all — there is no concept client-side of "select
which of my side's ships/drones to command," only the existing
target-reticle (what *I* am looking at). Commanding a drone needs
**two** selections at once (who to command, what to order them at),
which this port's current single-target system doesn't represent.

## Scope

1. Add a minimal "commandee" selection distinct from the existing
   target reticle — reuse the existing same-side-ship targeting
   machinery (the `T`-key cycle already sees same-side ships; the P415
   friendly-nearest keys are a natural fit too) rather than building a
   new candidate-gathering system. Investigate which existing key/state
   is the most natural "this is who I'm commanding" selection before
   adding a new one from scratch.
2. With a commandee selected (a real NPC/drone on your own side) and a
   real target selected (an asteroid, for the Build case — but keep
   this general enough to carry other `CommandId`s later, don't hardcode
   Build-only if a general "issue order" key is barely more work),
   press a key to send the real `OrderChange` message
   (`ShipID` = commandee, `CommandId` = whatever's appropriate for the
   current target type — `CidBuild` for an asteroid target with a
   Builder-role commandee, mirroring `HandlePlayerBuildOrder`'s own real
   gate so an illegal request is refused exactly as it already is
   server-side).
3. Real, minimal feedback: confirm (HUD text or console line, matching
   this port's existing conventions) when an order is sent, and surface
   the server's real refusal reasons if the gate rejects it (don't leave
   the player guessing why nothing happened).

## Explicitly out of scope

- The full native mouse-driven Command-console UI (squad roster,
  drag-order-onto-ship, etc.) — this task is the minimal keyboard
  interim, not the real console panel.
- The queued-order/accept-reject flow for commanding **other real
  players** (P414/P424, already correct) — this task is specifically
  about direct NPC/drone commanding, a different real code path.
- Any new order type beyond what's needed to command a Builder (Build)
  — if a Miner/Goto order falls out naturally at near-zero extra cost
  because the same `OrderChange` plumbing already handles them, note it
  plainly; don't go build a full order-type catalog.
- P368-P424 — unrelated, don't touch.

## Verification — real proof, not just build/test

- A test proving the client sends the correct real `OrderChange` wire
  shape (`ShipID`, `CommandId`, `ObjectType`, `ObjectID`) for a
  commandee+target pair.
- **Live proof, required**: with a real Builder-piloted drone present
  (spawn one the same way P422's own live proof did, or via whatever
  real in-game path produces one), select it as commandee, select a
  real buildable asteroid as target, press the new key, and confirm the
  full P422 arc plays out — approach, construction effect, drone
  consumed, real new station created — driven entirely from the
  MonoGame client this time, not a test harness.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The feature, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P425_COMMAND_OWNED_DRONE_BUILD_ORDER.md` in the repo
   root: the commandee-selection mechanism chosen, the key(s) added,
   and the live verification performed.
