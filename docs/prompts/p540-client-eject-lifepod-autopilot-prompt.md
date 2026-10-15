# P540 — client handles EJECT(77): become the lifepod and autopilot to the nearest rescue point (native clintlib FM_S_EJECT)

## Why this task exists

**Domingo (2026-10-04, live 5v5 on 221):** "lifepod handling on legacy client
was to auto pilot to nearest base?" Then: "port it".

**Native confirmed** (`~/Development/Allegiance/src/clintlib/appmsg.cpp:170-237`,
`case FM_S_EJECT`). For the ejected ship (any ship in the client's cluster):
1. If boarded (`GetParentShip()`), unboard (`SetParentShip(NULL)` +
   `OnBoardShip(pShip, NULL)`). Otherwise the ship "died": terminate all its
   parts, `SetAmmo(0)`, `SetFuel(0)` (`:183-201`).
2. Clear any missile in the cluster targeting it (`:203-212`).
3. `SetBaseHullType(side->GetCivilization()->GetLifepod())`, spin
   `SetCurrentTurnRate(c_axisRoll, pi*2)`, then set the position, velocity and
   orientation from the message (`:215-227`). The comment says it "must match
   fedsrv.cpp". Our server side is `Server.Combat.cs` ~1960-2017, the port of
   `FedSrv.CPP:11944-12006`.
4. **If it's our own ship** (`:229-237`): `SetCookie(pfmEject->cookie)`, then
   ```cpp
   ImodelIGC* pNearestBase = FindTarget(pShip,
       c_ttStation | c_ttFriendly | c_ttAnyCluster | c_ttProbe,
       pShip->GetCommandTarget(c_cmdCurrent), pcluster, &pfmEject->position, NULL,
       c_sabmRescue | c_sabmRescueAny | c_sabmLand);
   pShip->SetCommand(c_cmdCurrent, pNearestBase, c_cidGoto);
   SetAutoPilot(true);
   bInitTrekJoyStick = true;
   PlayNotificationSound(salAutopilotEngageSound, GetShip());
   ```
   That's the nearest friendly **station or rescue probe**, in any cluster,
   that can rescue or land a pod. It becomes the goto target, autopilot
   engages, and the engage sound plays. The pilot can still take over.

**The port today:** the server sends EJECT (`src/Allegiance.Core/Wire/Eject.cs`,
`Fmid = 77`, built by `Eject.ForSurvivor`). **The MonoGame client never
dispatches or handles it.** There's no `Eject` case in
`src/Allegiance.ClientCore/Session/ConnectionSession.cs` (compare `ShipReset`
at `:1008` → `ClientWorld.Apply`, `State/ClientWorld.cs:1185/1250`), and
nothing in `Allegiance.ClientCore.Game`. So when the player is beaten, the
client never becomes the pod locally and never autopilots home.

**Reuse:**
- The player autopilot from P524 (`G`): `_autoPilotEngaged`, `_autoTarget`
  (`AutopilotTarget` record, `Game1.Targeting.cs:49`), the engage path at
  `Game1.Targeting.cs:~677`, the control law via `TryGetAutopilotControls`
  (`Game1.FlightInput.cs:~1495`), mouse-move disengage (`:1452-1460`), and
  `_sound.PlayAutopilotEngage()` (`Audio/SoundManager.cs:437`).
- Station rescue/land capability data: `c_sabmRescue = 0x2000` and friends in
  `src/Allegiance.Core/Data/DataStationTypeIGC.cs:~202-212` (P301 station-side
  pod rescue). Probe rescue capability is in the probe type data. Use the real
  flags, not names.
- The civilization's lifepod hull id (the server already uses it; mirror
  `GetCivilization()->GetLifepod()`).

## Scope

1. **Decode and dispatch EJECT(77)** in `ConnectionSession`, and apply it in
   `ClientWorld`: lifepod hull swap, parts/ammo/fuel cleared, missiles
   untargeted, pose set from the message, roll spin. Faithful to `:183-227`.
2. **Own ship:** cookie update, then pick the target exactly as native's
   `FindTarget` flags describe (friendly; stations **and** probes; any
   cluster; capability Rescue | RescueAny | Land; nearest from the eject
   position, preferring the current command target as native passes it).
   Then **engage the existing P524 autopilot** toward it and play the engage
   sound. A manual input disengages it as usual.
3. The own-ship local simulation, rendering (lifepod model) and HUD must
   reflect the pod immediately (no ship model lingering until some later
   message).

## Explicitly out of scope

- Server-side eject, rescue and lifepod endurance (already ported:
  `Server.Combat.cs`, P279, P301).
- MCP bot lifepod behaviour (P539 does the bot side).
- Boarding/turret UX beyond the unboard branch's state change.

## Verification

- **Failing-first unit tests** (ClientCore / ClientCore.Game test projects):
  - decoding and applying EJECT swaps the hull to the side's lifepod and
    clears parts/ammo/fuel
  - pose and spin come from the message
  - the target pick returns the nearest friendly rescue-capable station (or
    probe) across clusters and ignores enemy or non-rescue stations
  - for our own ship, autopilot engages toward it; for another ship it
    doesn't
- **Live headless proof:** loopback or local server with a real client
  session whose ship gets beaten (an existing live eject test harness, e.g.
  the `LiveInSpaceEjectTests` family, can drive it). Show the client log
  lines: EJECT applied, the autopilot target chosen (name and sector), and
  the pod docking via the server's rescue/land path
  (`[lifepod] … docked Lifepod at station …`).
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0 warnings
  and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3 consecutive
  runs **on the final code**. Cite `docs/TEST_FLAKES.md` for flakes.
  **Report only numbers you actually measured.**
- ⚠️ **Commit as soon as the code, tests and live proof are done, BEFORE the
  3× full-suite run**, then amend with the suite numbers. Confirm with
  `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P540_CLIENT_EJECT_LIFEPOD_AUTOPILOT.md`: native
   citations, the dispatch path, test results and the live proof lines.
