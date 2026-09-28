# P414 — Ripcord request key (TK_Ripcord)

Status: IMPLEMENTED on `master` (unpushed; Domingo pushes).

## Summary

Native's `TK_Ripcord` ("Ripcord (teleport)") had a complete, already-implemented
server-side state machine (P111-P113 activation/countdown/teleport, P172 NPC
travel, P405 the countdown maneuver/thrust freeze) — but nothing in the client
ever sent the `FM_C_RIPCORD_REQUEST(184)` that starts it, and nothing decoded
the three response messages. This stage wires the whole client side:

- a **key** (L) that sends a real `RipcordRequest` for the sector selected on
  the expanded starmap (reusing P403's `_selectedSectorId`), or cancels an
  active countdown;
- **inbound decode + handling** for `RipcordActivate(185)`, `RipcordDenied(186)`
  and `RipcordAborted(187)` in `ConnectionSession` → `ClientWorld`;
- a **HUD tag** that surfaces the active countdown and self-expires when the
  teleport lands, plus the already-loaded `vo_sal_ripcordaborted` sound cue on
  cancel.

No server / sim code changed (the state machine was already correct).

## The key chosen: **L**

`Rendering/KeyBindingTable.cs` was audited for unbound single letters in flight.
Nearly every single letter is already taken (W/S/Q/E/Arrows/A/D/Shift/R/M/V/
T/G/Enter/Back/Escape/Space/K/C/N/`/[/]/X/F/I/P/B/H/J/Y/O). The only truly free
letters were `L` and `U` — both bound only in the **docked shop** screen
(L = Launch, U = Undock, `Game1.Panes.cs`), a separate input context. `L` was
chosen as a loose mnemonic for "ripcorLd" and added to the help overlay next to
the other defensive/escape commands (C chaff, F cloak):

```
C   Drop chaff [TK_DropChaff]
F   Toggle cloak [TK_ToggleCloak]
L   Ripcord to selected starmap sector / cancel [TK_Ripcord]
N   Drop mine / probe [TK_DropMine]
```

`L` is dual-use (docked Launch vs in-flight ripcord) in separate screens — the
same pattern the port already accepts for `R` and `F`.

## No-selection / cancel semantics (decided + documented)

The pure rule lives in `Flight/RipcordKey.cs` (`RipcordKey.RequestFor`), the
exact same precedence the server's `RequestRipcordCore` (native
`FedSrv.CPP:13319-13378`) resolves:

1. **Active ripcord** → send `SidRipcord = NA` to **cancel**. The key doubles as
   the cancel key (a cancel is the same command natively), matching the brief's
   item 2. It cancels regardless of any starmap selection still held.
2. **No active ripcord + a sector selected** → send that sector to **initiate**.
3. **No active ripcord + no sector selected** → send **nothing**; surface a
   bare system-line hint `Ripcord: select a sector on the starmap (, / .) first.`
   (Decision: sending `NA` here would be a benign server no-op anyway —
   `RequestRipcordCore` with a null target and no active ripcord just returns —
   so the informative hint is strictly better than a silent message.)

These are the semantics the report documents as the deliverable's "decision".

## Response handling (item 3)

`ConnectionSession` now decodes the three fmids into `ClientWorld` (previously
unknown fmids were silently dropped), and `ClientWorld` reacts:

- **`RipcordActivate(185)`** — when the message names **our own ship**, sets
  `RipcordActive = true`, records `RipcordTargetSectorId` (the REQUESTED sector,
  `sidRipcord`) and an activation timestamp. This drives the HUD tag; the key
  handler reads `RipcordActive` to decide initiate-vs-cancel.
- **`RipcordDenied(186)`** — requester-only; surfaced as a **bare system chat
  line** ("Ripcord denied - no ripcord destination in that sector.") via the
  same convention the P230 missile-lock tips use (sender NA → no player prefix),
  so a denied request is **never silently dropped**.
- **`RipcordAborted(187)`** — when it names our own ship, clears the ripcord
  state and queues a single **`RipcordAborted` sound event**, which `Game1`'s
  audio drain turns into the real `vo_sal_ripcordaborted` VO cue
  (`salRipcordAbortedSound`, sounddef.mdl:351 — the asset the brief flagged as
  pre-existing/unused).

## HUD surface

While `RipcordActive` is true the HUD draws a bright-cyan tag
`RIPCORDING TO <sector>` (resolving the sector id → the real cluster name from
the EXPORT table, the same source the starmap pane uses), placed beside the
existing MINING / CLOAKED activity tags. Because the **server's ripcord teleport
is silent on the wire** (no SET_CLUSTER is sent to the ripper on `UseRipcord` —
out of scope to change, and this stage deliberately touched no server code), the
tag **self-expires** in `Game1.Rendering.RipcordStatusLabel` once the own hull's
`RipcordSpeed` seconds (the exact value the server counts down,
`Ship.ResetRipcordTimeLeft`) have elapsed since activation; a `RIPCORD_ABORTED`
clears it sooner.

Judgment call (noted, not over-built): a full visual countdown ring/bar with
per-second `countdown10..1` ticks was **not** added. The countdown duration is
hull-derived (`RipcordSpeed`) and the client already resolves it for expiry, so
a ticking ring is feasible — but the brief asked for a reasonable, simple reuse
of existing text conventions, and the mission-start countdown (P216) is a
separate one-shot path. A dedicated countdown widget is left as a clearly-warranted
future enhancement; the tag + sound cover the required surface.

## Verification (all real)

### Whole-solution build
`AllegianceCore.slnx` Debug: **0 warnings, 0 errors**.

### Unit test — outbound encode + key rule (`RipcordRequestTests`, +4)
- `Outbound_request_encodes_the_selected_sector_id_exactly` — the sector the
  pilot picks on the starmap is byte-faithfully the `sidRipcord` the server
  receives (Encode → Decode round-trip, fmid + sector asserted).
- `Outbound_cancel_request_encodes_NA` — cancel is `NA`.
- `Decode_rejects_a_non_ripcord_request_fmid` — defensive.
- `RipcordKey.RequestFor` — active→NA / selected→sector / neither→null (the
  documented no-selection semantics).

### Live proof (`RipcordRequestLiveTests`, +1, real in-process server + real
`ConnectionSession` pilot driving the exact L-key method)
Driven the way P413 proved the cloak key: the pilot sends `SendRipcordRequest`
(the method the L-key handler calls) over the real TCP transport to a real
server with injected ripcord anchors (XXV_simple: fly 1052, anchored 1054/1055,
deny 1056). Logged:

```
[ripcord] ship 8098 FIRST activation anchor (ot=1 oid=8290) -> sector 1054 RIPCORD_ACTIVATE(185) sector 1052 wide
RIPCORD-P414-LIVE: pilot 8098 key -> sector 1054: server countdown armed, teleport landed
RIPCORD-P414-LIVE: request -> sector 1056 (no anchor) surfaced 'Ripcord denied'
[ripcord] ship 8098 FIRST activation anchor (ot=1 oid=8291) -> sector 1055 RIPCORD_ACTIVATE(185) sector 1054 wide
[ripcord] ship 8098 cancels -> RIPCORD_ABORTED(187) sector 1054 wide
RIPCORD-P414-LIVE: pilot 8098 cancel (NA) -> RIPCORD_ABORTED cleared state + queued ripcord-aborted sound (1 sound events drained)
```

Proof coverage, mapped to the brief's required live proofs:
- **Outbound request + countdown starts**: the server logs a real
  `RIPCORD_ACTIVATE` for our request, and the pilot's world decodes
  `RipcordActive = true`, `RipcordTargetSectorId = 1054`.
- **Freeze + teleport**: the countdown (armed via the pilot's key) is nudged to
  ~0 and the teleport fires — the server ship relocates into the target sector
  (the P405 freeze itself is P405's separately-proven live test; the teleport on
  completion is proven here server-side).
- **Denied, surfaced**: requesting sector 1056 (no anchor) surfaces the
  "Ripcord denied" system line in the pilot's world — not silently dropped.
- **Cancel + aborted sound**: while active, `NA` produces `RIPCORD_ABORTED`,
  clears the client state, and queues exactly one `RipcordAborted` sound event.

**Must-fail-if-removed proven**: with the `Send(FedPacket…)` line in
`ConnectionSession.SendRipcordRequest` disabled, the live test goes **RED** at
the initiate assertion (no countdown ever arms). Restored → green.

### Full suite (run 2x)
- Run 1: Core.Tests 7 flaky / ClientCore 0 / MonoGame 782/782 / Lobby 12/12.
- Run 2: Core.Tests 7 flaky (a *different* set) / ClientCore 3 flaky /
  MonoGame 782/782 / Lobby 12/12.

All failures are the repo's documented **rotating live-wire timing / socket-
contention flakes** (LiveTurretGunner persistent machine-state; rotating
LiveRipcordTests / LiveMoney / LiveWinCondition / LiveChatSector / LiveCombat;
ClientCore live-network multiparty tests), present on the pristine tree with
different sets each run. Spot-checks in **true isolation** are green (e.g.
`LiveRipcordTests.Cancel_while_active_aborts_sector_wide` and `Re_target_…
_requester_only` pass alone; `FullSessionKillIntegrationTests` passes 2/3 in
isolation, flapping with wall-clock timing on the pristine tree too — a machine-
state/timing flake, not a regression). No failing test references any P414
symbol, and P414 touched no server / Bot / Core.Tests code. The P414-owned
suite (`Allegiance.ClientCore.MonoGame.Tests`) is **fully green both runs**.

## Files changed (P414)
- `src/Allegiance.Core/Wire/RipcordRequest.cs` — added `public const short NA = -1;`
  (additive; Encode/Decode unchanged).
- `src/Allegiance.ClientCore/Session/ConnectionSession.cs` — `SendRipcordRequest(short)`
  + 3 inbound decode cases (Activate/Denied/Aborted).
- `src/Allegiance.ClientCore/State/ClientWorld.cs` — `RipcordActive`,
  `RipcordTargetSectorId`, `RipcordActiveSinceLocalMs`, `ClearRipcord()`,
  `PostSystemLine()`, 3 `Apply` handlers, Reset/SetCluster clearing.
- `src/Allegiance.ClientCore/State/ClientSoundEvent.cs` — `RipcordAborted` kind.
- `src/Allegiance.ClientCore.MonoGame/Audio/SoundManager.cs` — loads + plays
  `vo_sal_ripcordaborted` (`PlayRipcordAborted`).
- `src/Allegiance.ClientCore.MonoGame/Game1.Audio.cs` — drain the RipcordAborted
  event into the audio manager.
- `src/Allegiance.ClientCore.MonoGame/Flight/RipcordKey.cs` — the pure key rule.
- `src/Allegiance.ClientCore.MonoGame/Game1.FlightInput.cs` — the L key handler.
- `src/Allegiance.ClientCore.MonoGame/Game1.cs` — `_lKeyWasDown`.
- `src/Allegiance.ClientCore.MonoGame/Game1.Rendering.cs` — `RipcordStatusLabel()`
  + hull-duration self-expiry, passed to the HUD.
- `src/Allegiance.ClientCore.MonoGame/Rendering/HudRenderer.cs` — `ripcordStatus`
  parameter + `DrawRipcord`.
- `src/Allegiance.ClientCore.MonoGame/Rendering/KeyBindingTable.cs` — L row.
- `src/Allegiance.ClientCore.MonoGame/Content/Sounds/vo_sal_ripcordaborted.ogg`
  + `Content.mgcb` block.
- `tests/Allegiance.ClientCore.MonoGame.Tests/RipcordRequestTests.cs` (new).
- `tests/Allegiance.ClientCore.MonoGame.Tests/RipcordRequestLiveTests.cs` (new).

Unrelated pre-existing local edits (`tools/vizfight/Program.cs`,
`docker/compose.server.json`) left unstaged — not part of P414.
