# MINE/PROBE DROP — P224 (TK_DropMine)

## Relationship to P223 / P182

P182 introduced the generic `FIRE_EXPENDABLE(82)` request/response path
(`FireExpendableRequest` 82 / `FireExpendable` 87) plus the construction-only
`DataPartTypeIGC.ChaffLauncher` mount subtype, but `Server.HandleFireExpendable`
was **chaff-only**: its branch refused anything that wasn't a `ChaffLauncher`
with `"mine/probe branch out of scope, ignored"`. P223 wired the chaff **key**
(`C`) in the MonoGame client on top of that chaff-only handler. P224 delivers the
mine/probe half: two more construction-only launcher subtypes, an extended
handler that dispatches by subtype to the existing `Ship.DeployMine` /
`Ship.DeployProbe` (both already implemented since P94/P99), and an `N` key in
the client. Native's own `TK_DropMine` is labeled "Drop Mine **or** Probe"
(`trekinput.cpp:1424`) and uses the exact same generic `FIRE_EXPENDABLE`
mechanism for all three expendables (`clintlib.cpp:3340-3374`).

No commit from P221/P222/P223 was modified; they were only read as pattern
reference.

## Root-cause recap (why a new subtype per expendable)

This port's `Ship.Parts`/`MountPart` mounting store is keyed on
`DataPartTypeIGC.Subtype.EquipmentType`, but the real corpus data for **any**
launcher (chaff/mine/probe alike) is the compact 24-byte `DataLauncherTypeIGC`
shape, which doesn't fit that union. P182 solved this for chaff with a
construction-only synthetic subtype. P224 repeats that exact recipe for mine and
probe.

## EquipmentType decision (documented in code)

Native's **mine** type reports `ET_Dispenser` (`CmineTypeIGC::GetEquipmentType`,
`src/Igc/minetypeigc.h:62-64`) and its **probe** type reports `ET_Dispenser`
too (`CprobeTypeIGC::GetEquipmentType`, `src/Igc/probetypeigc.h:92-94`); the
mounted dispenser's `GetEquipmentType` mirrors its expendable type's
(`src/Igc/dispenserigc.h:45-48`). Chaff is the lone `ET_ChaffLauncher`
(`src/Igc/chafftypeigc.h:66-69`). So:

- `DataPartTypeIGC.MineLauncher(short ExpendableTypeId)` → `EquipmentType.Dispenser`
- `DataPartTypeIGC.ProbeLauncher(short ExpendableTypeId)` → `EquipmentType.Dispenser`
- both construction-only, deliberately **not** added to `Decode`'s size switch
  (no corpus part record exists to decode them), exactly like `ChaffLauncher`.

Because the wire carries only the `EquipmentType` (82's single field), and mine
and probe share `Dispenser`, `HandleFireExpendable` distinguishes them by the
**mounted subtype**, not the equipment type — matching native's single
dispenser-at-mount-0 rule (`FedSrv.CPP:5652`). A ship carrying both mounted is
resolved to whichever occupies mount 0; selecting between the two when both are
mounted is explicitly out of scope (native's real key drops whichever type is
mounted).

## `Server.HandleFireExpendable` (`src/Allegiance.Server/Server.cs:6698`)

The old `if (part.Type.Type is not ChaffLauncher) → refuse` was replaced by a
`switch (part.Type.Type)` with three cases:

| subtype | resolves from | deploys |
|---|---|---|
| `ChaffLauncher` | `mission.Core.ChaffTypes` | `Ship.DeployChaff(...)` + CREATE_CHAFF/spoof flow |
| `MineLauncher` | `mission.Core.MineTypes` | `Ship.DeployMine(type, mission.LastUpdate)` |
| `ProbeLauncher` | `mission.Core.ProbeTypes` | `Ship.DeployProbe(type, mission.LastUpdate)` |

Unknown-type IDs are logged-and-ignored (the port's TrapHackBoot-softening
idiom). The `Amount > 0` gate and the ammo decrement (`FedSrv.CPP:5744-5745`)
are shared across all three cases. Mine/probe carry **no** dedicated instance
message: native sends the single `S FIRE_EXPENDABLE` plus the mine/probe object
exported through the generic `CreateObject/OT_mine|OT_probe` path. Here the
object EXPORT is triggered automatically by `DeployMine`/`DeployProbe` →
`MineCreatedEvent`/`ProbeCreatedEvent` → `BroadcastExportMine`/`BroadcastExportProbe`
(the pre-existing P94/P99 site seams), so the handler only adds the `FE(87)`
echo.

## Broadcast audience decision — SAME-SIDE only for mine/probe (with citation)

The port's own pre-P224 comment on `HandleFireExpendable` claimed chaff was
sector-wide "unlike the same-side-only mine/probe/missile broadcasts". Reading
the live native handler shows the truth is more subtle: native's *single* flush
`SendMessages(GetGroupSectorFlying(pcluster), FM_GUARANTEED, FM_FLUSH)`
(`FedSrv.CPP:5783`) sits **after** the chaff/mine/probe branch and so nominally
covers all three sector-wide. However, this port broadcasts the mine/probe
**object** creation same-side only — `BroadcastExportMine`
(`Server.cs:9739-9743`) / `BroadcastExportProbe` (`Server.cs:9793-9797`): the
layer's own peer always, plus same-side teammates holding its `ExportShip`
(via the `_teammateExportSent` gate), and `BroadcastFireMissile`
(`Server.cs:9538-9546`) uses the identical audience. There is no cross-side
`EXPORT` mechanism in this port, so a cross-side `FE(87)` without the object
would be incoherent. **Therefore the mine/probe `FE(87)` echo follows the port's
established same-side mine/probe broadcast audience** — matching
`BroadcastExportMine`/`BroadcastExportProbe`/`BroadcastFireMissile`, so every
`FE(87)` recipient also receives the mine/probe EXPORT. Chaff keeps its
sector-wide `SendSectorWide` audience unchanged.

The stage's loopback tests pin this: the same-side observer receives
`FE(87)` + `EXPORT(mine|probe)`; the cross-side enemy receives neither.

## Client key — `N` (`src/Allegiance.ClientCore.MonoGame/Game1.cs`)

Edge-triggered `_nKeyWasDown` latch, gated behind the same `composing` check as
the C/K/M/V toggles, sending `SendFireExpendable(EquipmentType.Dispenser)` —
the single mount value mine **and** probe dispensers share. `ConnectionSession.SendFireExpendable`
(P223) is already generic over `EquipmentType`, so it was reused unchanged.

**Key rationale:** every in-flight key is taken — W/S/Q/E/Up/Down/Left/Right/A/D/
Shift (afterburner)/R (quick-chat)/M (mouse-flight)/V (cockpit)/T (autopilot
target)/G (autopilot engage)/Enter/Back/Escape/Space (fire)/K (suicide)/C
(chaff)/`` ` `` (toggle-group-fire)/`]` (next weapon)/`[` (prev weapon). The task
brief suggested `N` (mnemonic "miNe") — adopted. (This port chooses its own keys
because the native default VK table is not in the reference checkout; the same
situation `docs/KEYBINDING_AUDIT_P220.md` documents for every prior binding.)

## Tests (`tests/Allegiance.Core.Tests/ChaffTests.cs`) — ChaffTests 9 → 12

Reuses the existing `SeatThree`/`PumpUntil`/`MountChaffLauncher` harness.

- `Mine_and_probe_launcher_subtypes_mount_under_equipment_type_dispenser` —
  pins `MineLauncher`/`ProbeLauncher` → `EquipmentType.Dispenser`, chaff stays
  `ChaffLauncher`.
- `Live_firing_mounted_mine_launcher_creates_mine_decrements_ammo_and_broadcasts`
  — mounts `MineLauncher`, sends `SendFireExpendable(Dispenser)`, asserts a real
  `Mine` in the sector, ammo 3→2, same-side observer gets `FE(87)`+`EXPORT`;
  cross-side enemy gets neither.
- `Live_firing_mounted_probe_launcher_creates_probe_decrements_ammo_and_broadcasts`
  — the same for `ProbeLauncher`/real `Probe`.

Must-fail-if-removed was proven for **both**: temporarily neutralising the
`MineLauncher` case (no `DeployMine`) reds the mine test ("same-side observer
must receive EXPORT(26)/OT_mine"); the same for `ProbeLauncher`/probe — both
restored afterward.

## Explicitly out of scope (unchanged from the brief)

- `TK_FireMissile` — separate, bigger task (needs a new `StateM` bit + per-tick
  server logic, not this request/response mechanism).
- Weapon-selection UI (P225) — unrelated.
- Selecting mine vs probe when both are mounted — native drops whichever is
  mounted; no selection UI built.
- Native reference source (`~/Development/Allegiance`) — untouched, read-only.

## Live proof (mine)

Booted a real `Allegiance.Server` (temp `--config` on port 6923, `teams:1`) with
a **temporary** mine-launcher mount in `SpawnDefaultShip`, connected the real
MonoGame client on `:0`, and auto-fired `SendFireExpendable(Dispenser)` once
~22 s past connect (Wayland host has no input-injection tool). Server log:

```
[spawn:TEMP] mounted mine launcher (type 12) at mount 0 for live-proof
[fireexpendable] 1 ship 8096 fired mine 12 (id 5464) -> FE(87)+EXPORT(26)/OT_mine to 1 same-side peer(s); dispenser 3 -> 2
[mine] ship 8096 deployed -> EXPORT(26)/OT_mine 12 pos Vector3 { X = 720, Y = 730, Z = 920 } life 5 -> 1 same-side peer(s)
```

A real `Mine` object (id 5464) was created in the sector (later expiring →
`MINE_DESTROYED(84)`), broadcast same-side, and the dispenser ammo dropped 3→2.
Probe is covered by the identical loopback live test (a probe needs the same
launcher-mount override; the task requires live proof for *at least one* of
mine/probe). Both temporary edits (Server.cs spawn mount + Game1 auto-fire) were
**removed before commit** — `git diff` shows `Server.cs` touches only
`HandleFireExpendable`.

## Build / test status

- Whole-solution build (`AllegianceCore.slnx`, Debug): **0 warnings / 0 errors**.
- `Allegiance.Core.Tests` (full suite, baseline 3037 → **3040** with +3):
  run 1 = 3039 pass / 1 fail, run 2 = 3039 pass / 1 fail — the single failure
  both times is the documented pre-existing machine-state flake
  `LiveTurretGunnerBindingTests.Board_binds_gunner_and_active_turret_update_roundtrips_live`
  (persists on the pristine tree, references none of this stage's symbols).
- `Allegiance.ClientCore.Tests`: **54 pass, 0 fail** (unchanged).
- `Allegiance.ClientCore.MonoGame.Tests`: **138 pass, 0 fail** (unchanged).

## Citations (file:line)

- `trekinput.cpp:1424` — native `TK_DropMine` "Drop Mine or Probe" key label.
- `clintlib.cpp:3231-3430` — `BaseClient::FireExpendable` branches on the
  expendable's `GetObjectType()` (chaff/mine/probe) and sends `et =
  pet->GetEquipmentType()`.
- `minetypeigc.h:62-64` / `probetypeigc.h:92-94` — mine/probe → `ET_Dispenser`.
- `chafftypeigc.h:66-69` — chaff → `ET_ChaffLauncher`.
- `dispenserigc.h:45-48` — mounted dispenser `GetEquipmentType` mirrors its
  expendable type.
- `partTypeIGC.cpp:74` — `GetEquipmentTypeName`: `ET_Dispenser` = "mine".
- `FedSrv.CPP:5652,5696,5744-5745,5783` — mount-0 lookup, type branch, ammo
  decrement, single sector-wide flush.
- `Server.cs:6698` (this stage) — subtype dispatch; `9739-9743`/`9793-9797`
  `BroadcastExportMine`/`BroadcastExportProbe` same-side audience;
  `9538-9546` `BroadcastFireMissile`.
- `DataPartTypeIGC.cs:167-216` (this stage) — `MineLauncher`/`ProbeLauncher`
  construction-only subtypes.
