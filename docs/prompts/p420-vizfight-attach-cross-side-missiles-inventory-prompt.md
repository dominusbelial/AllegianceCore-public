# P420 — vizfight `attach`: a real cross-side rendezvous, real missiles, real full-inventory usage

## Why this task exists, and the real constraint it has to solve first

Domingo's ask: *"we want missiles and we actually want the bots to use
everything on the inventory"* — then, when told this needed real
planning: *"take your time to plan the script, it will be a showcase
too."*

**The actual blocker, traced this session**: `attach`'s whole fleet
(P418) deliberately seats every bot on the **same** side, specifically
because an opposing side's ships undock into *their own* side's home
sector — a different cluster the spectator never sees (documented
honestly in P418's own report). That design choice has a real
consequence beyond suppressing kills: `Ship.UpdateMissileLock`
(`src/Allegiance.Core/Sim/Ship.cs:3152-3164`) hard-gates
`if (target.SideId == sideId) { MissileLock = 0f; return; }` — a
missile can **never** lock onto a same-side ship, full stop, checked
before the missile type's own lock-time math even runs. So "add missile
firing" on top of today's same-side fleet would just send requests that
the server's real, authoritative gate (`HandleFireMissile` →
`Ship.TryFireMissile`, `Server.ShipCommands.cs:832-880`) silently
refuses forever — not a real feature, just inert traffic.

**The real fix, and why it's worth doing properly**: give the
choreography a genuine second side that starts at its own real home
base and **flies there** — real wire-only movement through real warps,
the same mechanism every ship in this game already uses — to rendezvous
in the spectator's cluster. Once two real sides are genuinely
co-located, missile lock becomes real (a genuine enemy target exists),
**and** P418's other documented limitation (no real kills, only
projectile-spray, because friendly-fire suppresses same-side damage)
is resolved as a direct side effect — real cross-side hits become
lethal too. This is a bigger, better showcase than just adding a
missile button.

## Confirmed real primitives to build on

- **Missile firing** — no client-side send method exists yet.
  `Bot.cs` decodes inbound `FireMissile` but has no `SendFireMissile`.
  Add one mirroring `SendFireExpendable`'s exact shape
  (`Bot.cs:1424-1427`) — a `FM_CS_FIRE_MISSILE(70)` request; the
  server ignores client-supplied target/type and fires against its own
  authoritative `Ship.CommandTarget` (`Server.ShipCommands.cs:832-880`),
  so the client's job is just: have a mounted `ET_Magazine` launcher
  with ammo, have a real cross-side `CommandTarget` set, and request
  periodically — the server's gate does the rest.
- **Full inventory, already-real primitives to reuse**:
  `SendFireExpendable(EquipmentType.ChaffLauncher)` (`Bot.cs:1424`,
  already real, P182) for chaff; `ShipControlState.AfterburnerButton`
  (a real StateM bit, real thrust boost + real fuel drain since P412)
  via `CommandControls`; `ShipControlState.CloakActive = 0x800`
  (`Sim/ShipControlState.cs:33`, real cloak since P413) via
  `CommandControls`. `EquipmentType` (`Data/DataPartTypeIGC.cs:9-19`):
  `ChaffLauncher=0, Weapon=1, Magazine=2, Dispenser=3, Shield=4,
  Cloak=5, Pack=6, Afterburner=7` — use these exact values when
  building bot loadouts, don't guess.
- **Cross-cluster pathing** — `ClientCore/State/ClusterGraph.cs`
  already exposes the raw real topology (`Clusters`, `Warps`) this
  port's client already decodes from `CLUSTERINFO`, but has **no**
  pathfinding method yet (checked: only the two raw lists). A real
  shortest-path (BFS over `Warps` as edges between `Clusters`) needs to
  be added — small, self-contained, and genuinely reusable beyond this
  task (P403's starmap goto-command could arguably use it too, though
  that's out of scope here — just don't build something so narrow it
  can't be reused).
- **Real warp transit** — ships in this game (NPCs and players alike)
  already transit warps purely by flying into a warp's trigger radius
  under ordinary `ControlData` (confirmed this session watching real
  NPC miners cross sectors via ordinary flight, no special "warp
  request" message). Steering a bot toward a real warp's real position
  and holding forward throttle is sufficient — no new wire message
  needed for the transit itself, just real point-toward-target heading
  math (reuse/extend whatever P419 already built for its leash steering
  term, if P419 has landed by the time this starts — check its report
  first).

## Scope

1. **Add real BFS pathfinding to `ClusterGraph`** (or a small helper
   alongside it) — shortest sequence of clusters/warps from a given
   start cluster to a target cluster.
2. **A genuine second side in the choreography**: some bots
   `SendPositionReq` onto the real opposing side, ready up, and launch
   at *their own* real home station/cluster (not the spectator's) —
   exactly like a real player joining that side would. Then steer them,
   real warp by real warp, along the BFS path to the spectator's
   cluster. Narrate real progress through `AttachEvent` (e.g. "Bravo
   contingent crosses into $sector — N warps remaining").
3. **Real missile fire**: once a rendezvoused enemy contingent has a
   real `CommandTarget` on the opposing side in range, periodically
   send the new `SendFireMissile` (respecting a sane real cadence — not
   spamming every tick; check `Ship.TryFireMissile`'s real cooldown/lock
   shape and pace requests sensibly around it).
4. **Real full-inventory usage in the phase cycle**: expand bot
   loadouts (`SendBuyLoadout`'s `parts` list) to actually carry a
   weapon + missile launcher + ammo + chaff dispenser + afterburner +
   cloak (per-ship-class budget permitting — check the real hull's
   mount capacity, don't over-mount). Weave real usage beats into
   whatever phase cycle exists (P419's, if landed, or a sane cycle if
   not): attackers afterburner-boost on an engagement pass, defenders
   pop chaff and/or cloak when a missile is inbound or as a periodic
   evasive beat, everyone fires missiles at range per point 3.
5. **Real kills are now possible — verify honestly, don't force them.**
   Once cross-side, real gun/missile hits are genuinely lethal. Don't
   script a fake kill; let real combat produce real kills (or not) and
   report what actually happened live.

## Explicitly out of scope

- Any server-side gameplay change — everything here is real client/bot
  wire traffic against the unmodified server, exactly like P418.
- Building a general-purpose pathfinding library beyond what this task
  needs — a focused BFS is enough, don't over-engineer.
- P368-P419 — unrelated, don't touch. **Read P419's own report first**
  if it has landed by the time this starts, and build on its phase-cycle
  shape rather than inventing a second one.

## Verification — real proof, not just build/test

- A test for the new BFS pathfinder (correct shortest path on a known
  small real topology, e.g. Ganymede→Alderan→Organia from this
  session's own well-understood m2xspiderw map).
- A test proving `SendFireMissile`'s wire encoding is correct.
- **Live proof, required, against the real production server**: run
  `attach` and confirm — the opposing contingent genuinely launches at
  its own home base (different starting cluster, visible in server
  logs), genuinely transits real warps to reach the spectator's
  cluster, and once there, real missiles fire (visible in the `models`
  array / server `[firemissile]` log lines) and real chaff/afterburner/
  cloak usage is visible (server-side energy/fuel/ammo logs already
  emitted by the existing mechanics). Report honestly whether real
  kills occurred during the verification run — either outcome is fine,
  just don't misrepresent it.
- Whole-solution build: 0 warnings, 0 errors.
- Full test suite, run at least 2x: only the pre-existing documented
  live-wire timing flakes expected.

## Deliverables

1. The feature, committed on `master` (not pushed — Domingo pushes).
2. Report doc `P420_VIZFIGHT_ATTACH_CROSS_SIDE_MISSILES.md` in the repo
   root: the pathfinding approach, the rendezvous choreography, which
   inventory items got real usage beats, and the live verification
   performed (including the honest kill-count outcome).
