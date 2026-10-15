# P519 — native loadout screen: slot-driven part lists + hull cycling; remove the POC 30 s auto-launch

## Why this task exists

**Domingo's live playtest with P509's test unlock (2026-10-03):** "now the list
is so long i can't find the missiles in time before i'm being auto undocked".
With every side's tech unlocked, the docked shop lists **198 hulls** plus every
part in long flat lists.

**Bug 1: the port auto-launches docked players; native never does.**
`Game1.Panes.cs:~271-281` sends `BUY_LOADOUT(fLaunch)` after
**`DockFallbackMs = 30_000`** (`Game1.cs:~562`). The comment says "an
unattended dock must never stall the **POC**". That's a proof-of-concept
leftover. Native docked players stay docked until they launch. The server's
own auto-launch is **NPC-only** (`Server.Docking.cs:2096-2150`, "the IsNpc
gate is non-negotiable"). Claude disabled it locally in Domingo's play
worktree only (`~/allegiance-play`, uncommitted, "LOCAL PLAY PATCH"
comment). The repo still has it.

**Gap 2: the port's shop is a flat list; native's loadout screen is
slot-driven** (`~/Development/Allegiance/src/WinTrek/load.cpp`, `LoadoutImpl`):
- **Slots:** the hull's equipment slots are shown as `Slot` panes, one per
  `EquipmentType` and mount (`:58-200`). That covers weapons per hardpoint,
  turrets (`ET_Turret`, `:41`, `:96-110`), magazine (missiles), dispenser
  (mines/probes), chaff, shield, cloak, afterburner and cargo. Validity is
  `SlotValidForHull(pht, equipmentType, mount)` (`:112-130`).
- **Parts:** selecting a slot lists **only the parts that fit that slot**,
  for example `:1257-1300` handle dispenser/mine typing. So missiles are
  found by picking the missile (magazine) slot.
- **Hulls:** the player **cycles** through the station's buyable hulls with
  next/previous (`:1574-1610`, `:1807-1830`), using
  `GroupID >= 0 && CanBuy && !IsObsolete`. It's not one long list.

## Bug 3 (HIGHEST PRIORITY, found 2026-10-03 before this task started): every launch strips the ship to a bare hull

Domingo saw a red damage flash right after every undock, on every ship, with no
damage. Root cause from 221's logs plus the code:
- **The client sends no parts.** Every launch logs
  `[monogame] LAUNCH -> BUY_LOADOUT(fLaunch) hull N + 0 part(s)` (client)
  and `[loadout] … BUY_LOADOUT_ACK hull N nParts 0 … fLaunch True` (server).
- **The server rebuilds from that list.** Its `ApplyLoadout`
  (`src/Allegiance.Server/Server.ShipCommands.cs:2337`, clear at
  ~`:2591` `ship.ClearLoadout()`, rebuild at ~`:2593-2598`) is native's
  clear-and-rebuild `PurchaseShipLoadout`. So **the ship launches with no
  shield, weapons, missiles or anything else**.
- **That's the flash:** the dock refill (`Server.Docking.cs:1503-1507`)
  sets shield 1.0, the launch removes the shield, the fraction becomes 0
  (`Server.Broadcast.cs:~469`, unshielded ≡ 0), and `DamageFlashTracker`
  flashes.

**Native:** find what native's loadout screen sends on launch (`load.cpp`
`LoadoutImpl` and its BUY_LOADOUT build from the hangar ship's mounted parts,
plus native's default loadout for a newly selected hull). Cite it. The
player launches with the **hangar ship's current loadout**, or the hull's
default loadout after a hull change, unless they changed it.

**Required:**
- `BUY_LOADOUT` always carries the full intended loadout: the current
  mounted parts (with mounts and amounts), or the default loadout for a newly
  picked hull, plus any edits.
- A pure test, failing first: launching without touching the shop sends
  N > 0 parts equal to the docked ship's parts. Changing the hull sends that
  hull's native default loadout.
- **Live:** after launch, the server ACK shows `nParts > 0`, the ship has a
  shield and weapons, and **there's no red flash on undock**.

## Domingo's explicit layout requirements (2026-10-03, added before this task started)

"i still find it hard to find parts while docked, can we have a separate
tab/window or something for hulls and parts? also we only want to list
compatible parts once a hull is selected"

- **Two separate tabs, or panes the player switches between:** **HULLS**
  and **PARTS**. Each is its own view, not one combined scrolling list.
  Keyboard, pad and touch can switch tabs, with a clearly shown key such as
  Tab or Q/E, and the pad shoulder buttons.
- **The PARTS tab lists only parts compatible with the currently selected
  hull**, using native `SlotValidForHull` and `CanMountPart`, organized by
  that hull's real slots (scope item 2). With no hull selected, or the
  current hull, it shows the current hull's slots. Nothing incompatible is
  ever listed.
- Switching hulls refreshes the PARTS tab immediately. Parts chosen for the
  previous hull that don't fit the new one are dropped or flagged, reusing
  P334's existing hull-swap fit logic.
- The live check (Verification) must show: open PARTS, then the missile
  slot, then only hull-compatible missile launchers.

## Hull preview: a live 3D orbit behind the hull picker (Domingo's choice, 2026-10-03, added before this task started)

Domingo wants the selected hull **visible in the background** while picking
hulls. He chose native's approach over a static image.
- **Native:** the hangar/ready-room ship preview is a **live camera orbit
  around the real 3D model**, `cmExternalOrbit` in
  `~/Development/Allegiance/src/WinTrek/WinTrek.cpp:7186-7191`
  (`Pitch(dt*0.35f)`, `Yaw(dt*0.2f)`, `Roll(dt*0.5f)`). There are no GIFs
  or pre-rendered images in `Artwork/`. Check `load.cpp` for the exact
  loadout-screen use and cite it.
- **Port:**
  - Render the **currently selected hull's actual in-game model**, resolved
    through `HullModelResolver` exactly as in flight, so it includes
    `NoseAlignmentCorrection` and any per-model orientation fix (P520).
  - Orbit it at native's rates, behind the HULLS/PARTS panes with the UI
    readable over it.
  - Cycling hulls swaps the model immediately, with no hitch. Models are
    already loaded and cached in `_shipModels`.
  - A hull with no model asset uses the same fallback as in flight.
- **Performance:** a single model plus a simple light, no space scene. It
  must stay smooth on Android (the P499 head). Measure frame time on desktop
  and in the emulator, and report it.
- **No static images.** Meshy thumbnails aren't used for this.
- **Verification:**
  - a pure test that hull selection drives the preview's model key;
  - an orbit-rate test against native's constants;
  - a live screenshot of the hangar with two different hulls selected,
    for the report.

## Scope

1. **Remove the client auto-launch for players** in the repo
   (`DockFallbackMs` and its check). The player launches only on their own
   action. Keep anything headless capture (`_captureSpec`) or a test
   actually relies on, gated to that mode only, and say what.
2. **Slot-driven loadout.** Rework the docked shop
   (`Rendering/ShopModel.cs`, `ShopLayout.cs`, `ShopCatalog.cs`,
   `Game1.Panes.cs`):
   - show the current hull's real slots (per P437's per-equipment-type mount
     rules and the hull's `CanMountPart` / hardpoints);
   - selecting a slot lists **only the parts valid for that slot and
     mount**: weapons per hardpoint, missile launchers in the magazine slot,
     mine/probe dispensers, chaff, shield, cloak, afterburner, cargo;
   - choosing a part fills that slot.

   Keep P437's mount numbering, P468's prices and greying, P488's
   successor/obsolete handling, and P470's drone filter.
3. **Hull cycling:** next/previous through the station's buyable hulls,
   as native does. Keep a quick way to reach a specific class: optional
   class grouping, or a jump within the cycle by class. If native has
   nothing for this, keep it minimal and explain the choice.
4. **Input:** it must be fully usable by keyboard (desktop), gamepad (P499
   pad mapping for docked screens) and touch (P501's docked tap zones). Update
   those mappings for the new structure.

## Explicitly out of scope

- The investment screen (P487/P495).
- Pricing and tech rules.
- Turret seat boarding (P464 path).

## Verification

- Pure tests (no graphics device), failing first:
  - no auto-launch after 30 s, or ever;
  - the slot list for a real hull matches its real slots;
  - selecting the magazine slot lists only missile launchers;
  - the hull cycle visits exactly the station's buyable, non-obsolete,
    GroupID ≥ 0 hulls, in a stable order.
- A short live check against a server with `testUnlock` on: dock, find and
  mount a missile launcher in under ~3 actions from the hangar screen, and
  launch. Report it.
- Whole-solution build with `/home/domingo/.dotnet/dotnet build`, 0
  warnings and 0 errors. Full suite via `scripts/run-tests.sh` (P471), 3
  consecutive runs. Cite `docs/TEST_FLAKES.md` for any remaining flake.
  **Report only numbers you actually measured.**
- **Commit before you finish**, and confirm it with `git log -1`.

## Deliverables

1. Committed on **`master`**, not pushed. Check with
   `git branch --show-current`.
2. The report `docs/reports/P519_NATIVE_LOADOUT_SCREEN.md`.
