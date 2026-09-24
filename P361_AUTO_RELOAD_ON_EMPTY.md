# P361 — Auto-trigger reload when ammo hits zero (sim mechanic)

Status: landed on `master` (Domingo pushes). Sim-only; the HUD display of the
reload/weapon status is P362 (queued behind this task).

---

## 1. The real native mechanism (confirmed)

Native has NO standalone "ammo slowly regenerates over time" system. Instead,
hitting exactly zero ammo/fuel automatically triggers the **same reload
mechanism the manual reload key uses**, rate-limited by a real debounce timer.

### `CshipIGC::SetAmmo(short newVal)` — `src/Igc/shipIGC.h:597-621`

```cpp
virtual void SetAmmo(short newVal)
{
    short maxAmmo = m_myHullType.GetMaxAmmo();
    if (newVal > maxAmmo) newVal = maxAmmo;

    short oldAmmo = m_ammo;
    m_ammo = newVal;

    if ((newVal == 0) && (oldAmmo > 0) && (GetMyLastUpdate() >= m_timeReloadAmmo))
    {
        m_timeReloadAmmo = GetMyLastUpdate() + 0.5f / GetMyMission()->GetFloatConstant(c_fcidMountRate);
        IIgcSite* pigc = GetMyMission()->GetIgcSite();
        if (!pigc->Reload(this, NULL, ET_Weapon))
            pigc->PostNotificationText(this, false, "Ammo depleted.");
        else
        {
            pigc->PlayNotificationSound(salReloadingAmmoSound, this);
            pigc->PlayNotificationSound(startReloadSound, this);
            pigc->PostNotificationText(this, false, "Reloading ammo...");
        }
    }
}
```

Key facts (all verified in the live tree):

- **Trigger**: only on the `>0 → exactly 0` transition (`newVal == 0 &&
  oldAmmo > 0`), and only once the debounce deadline has elapsed
  (`GetMyLastUpdate() >= m_timeReloadAmmo`).
- **Debounce arming** (shipIGC.h:608): `m_timeReloadAmmo = GetMyLastUpdate() +
  0.5f / GetFloatConstant(c_fcidMountRate)`. `c_fcidMountRate = 10`
  (`igc.h:69`). This is a **rate limit on how often the auto-reload ATTEMPT
  can fire**, not the reload duration itself. Critically it is armed
  **unconditionally on the zero-transition, BEFORE the attempt** — so even a
  failed "Ammo depleted." attempt spaces out retries.
- **Same callback as the manual key**: `pigc->Reload(this, NULL, ET_Weapon)`
  is the exact `IIgcSite::Reload` the manual reload key (`TK_Reload`,
  `WinTrek.cpp:9687`, also passing `NULL` for the weapon-ammo case) calls.
  It pulls a spare pack from the ship's **own cargo hold**.
- **Real success/failure**: success → "Reloading ammo..." + reload sounds;
  failure (nothing in cargo) → "Ammo depleted." — a real, correct failure
  case, not a silent no-op.

### `CshipIGC::SetFuel(float newVal)` — `shipIGC.h:627-648` (read in full)

Faithful mirror of `SetAmmo`:

```cpp
if ((newVal == 0.0f) && (oldFuel > 0.0f) && (GetMyLastUpdate() >= m_timeReloadFuel))
{
    m_timeReloadFuel = GetMyLastUpdate() + 0.5f / GetMyMission()->GetFloatConstant(c_fcidMountRate);
    IIgcSite* pigc = GetMyMission()->GetIgcSite();
    if (!pigc->Reload(this, NULL, ET_Afterburner))
        pigc->PostNotificationText(this, false, "Fuel depleted.");
    else
    {
        pigc->PlayNotificationSound(salReloadingFuelSound, this);
        pigc->PlayNotificationSound(startReloadSound, this);
        pigc->PostNotificationText(this, false, "Reloading fuel...");
    }
}
```

Structurally identical to `SetAmmo` — same debounce
(`0.5f / FloatConstants[c_fcidMountRate]`), same conditional
`IIgcSite::Reload(this, NULL, ET_Afterburner)` call, same success/failure
notification split.

### The underlying `IIgcSite::Reload` — `clintlib.cpp:3425-3556`

`BaseClient::Reload(pship, plauncher, ET_Weapon)` scans the cargo hold (mount
-1..-`c_maxCargo`) for the **last** `c_packAmmo` pack, then transfers either
just enough to reach `Hull.MaxAmmo` (partial, leaving the remainder in the
pack) or the **whole pack** (`NA` = -1, terminating the pack), repeating while
the pool is below max. This is the exact loop the port's P192 machinery already
encodes on the server side.

### What this port already had (P192) vs. what was missing

P192 (`Ship.TryApplyReloadEntry`, the server half of `FM_CS_RELOAD(129)`,
`FedSrv.CPP:6504-6653`) already implements the cargo→pool transfer for the
**manual** case (client enumerates cargo → sends RELOAD entries → server applies
per-entry and broadcasts `RELOAD(129)` sector-wide). **Missing was the
automatic trigger**: nothing called that same reload when a ship's ammo hit
zero. This task adds only the automatic zero-triggered case; the manual path is
untouched.

---

## 2. How it was wired into this port's existing P192 architecture

The port's reload is a **server-side cargo→pool transfer** driven by the P192
per-entry applicator. The auto-reload on empty is fundamentally a **server-side
sim event** (a weapon fires the pool to zero inside the mission tick), so it
adapts the manual wire-round-trip's two halves faithfully:

**Trigger — `src/Allegiance.Core/Sim/Weapon.cs`**
Inside `Weapon.Update`'s fire loop, the burst that drives the pool from `>0` to
`0` now calls `_ship.TryAutoReloadAmmo(now)`:

```csharp
short ammoStart = ammo;                                   // pre-burst pool
...
_ship.Ammo = (short)(ammo > 0 ? ammo : 0);
if (ammoStart > 0 && ammo == 0 && _type.CAmmoPerShot != 0) // native >0→0 transition
    _ship.TryAutoReloadAmmo(now);
if (ammo == 0 && _type.CAmmoPerShot != 0) fSelected = false;
```

Guarded by `CAmmoPerShot != 0` (energy-only weapons never consume ammo) and by
the weapon already being unable to re-fire a zero pool — so the attempt cannot
spam every tick even before the debounce.

**Core mechanic — `src/Allegiance.Core/Sim/Ship.cs`, `TryAutoReloadAmmo(double now)`**
Ports the `SetAmmo` zero-transition block:

- **Real debounce**: a `_timeReloadAmmo` deadline, armed unconditionally on
  entry (`_timeReloadAmmo = now + ReloadDebounceInterval()`), exactly as native
  arms `m_timeReloadAmmo` before the attempt. `ReloadDebounceInterval()`
  reads the loaded core's `FloatConstants[c_fcidMountRate = 10]` (`0.5 /
  rate`), following the same "read the loaded core constant, don't hardcode the
  wiki number" pattern P279/P215 use; 0.5 s fallback if the core is absent.
  Attempts before the deadline return `NotAttempted` (no re-fire every tick).
- **Reuses the P192 applicator**: the cargo→pool transfer runs through the
  **same** `TryApplyReloadEntry` the manual `FM_CS_RELOAD(129)` handler calls,
  choosing entries to mirror `BaseClient::Reload`'s ET_Weapon scan (last ammo
  pack; whole-pack `NA` when it fits under `Hull.MaxAmmo`, partial otherwise;
  repeat while below max). No new reload machinery was built.
- **Real success/failure**: returns/records an `AutoReloadOutcome`
  (`Reloaded` = a spare was consumed, "Reloading ammo..."; `Depleted` = no
  ammo pack in cargo, "Ammo depleted."), exposed as
  `Ship.LastAutoReloadOutcome` + `Ship.LastAutoReloadAt` — the state P362's HUD
  will need to show the two distinct texts.

**Wire relay — `IIgcSite.AutoReloadTriggered` seam**
Because a real auto-reload is a server-side event (unlike the human manual key,
which is a client→server round-trip), the outcome is surfaced synchronously to
the mission site: `Ship.TryAutoReloadAmmo` raises
`Mission.Site.AutoReloadTriggered(ship, entries, success)`. `ServerHost`
bridges it via an `AutoReload` hook (the established `ProjectileCreated`/
`LoadoutChange` seam pattern), and `Server`'s `BroadcastAutoReload` (wired in
`Server.Lifecycle.cs`) relays the applied entries sector-wide as
`RELOAD(129)` with `shipID` filled in **on success** — the exact relay leg the
manual handler produces (`FedSrv.CPP:6651-6652`). On failure there is nothing
to relay (the ship stayed at zero), so no `RELOAD` is sent; the depleted
outcome is still observable via `LastAutoReloadOutcome`.

**Fuel**: native `SetFuel` is a faithful mirror, but this port deliberately
tracks **no runtime fuel pool** (the pervasive "fuel is a documented no-op" in
`Ship.cs` — there is no fuel field that ever transitions to zero), so the fuel
half of the auto-reload has no live trigger here. The `TryAutoReloadAmmo`
machinery (debounce + cargo-scan + `TryApplyReloadEntry`) is structurally
identical to what an `ET_Afterburner`/`c_packFuel` twin would need, but nothing
calls it because fuel is not simulated. Documented, not built.

### Manual path unchanged
The manual `FM_CS_RELOAD(129)` handler (`Server.HandleReload` →
`Ship.TryApplyReloadEntry`) and the `SwapPart`/cargo-hold code are untouched;
the P192 `CargoBayReloadTests` suite still passes (see §4).

---

## 3. Files changed

- `src/Allegiance.Core/Sim/Ship.cs` — `_timeReloadAmmo` field, `MountRateConstantIndex=10`,
  `ReloadDebounceInterval()`, `TryAutoReloadAmmo(double)`, `AutoReloadOutcome`
  enum, `LastAutoReloadOutcome`/`LastAutoReloadAt`.
- `src/Allegiance.Core/Sim/Weapon.cs` — zero-transition auto-reload trigger in the fire loop.
- `src/Allegiance.Core/Sim/IIgcSite.cs` — `AutoReloadTriggered` default seam method.
- `src/Allegiance.Core/Sim/RecordingIgcSite.cs` — wraps the new seam.
- `src/Allegiance.Server/ServerHost.cs` — `AutoReload` hook + interface impl.
- `src/Allegiance.Server/Server.Combat.cs` — `BroadcastAutoReload` (sector-wide RELOAD relay).
- `src/Allegiance.Server/Server.Lifecycle.cs` — wires `_host.AutoReload = BroadcastAutoReload`.
- `tests/Allegiance.Core.Tests/AutoReloadOnEmptyTests.cs` — new P361 tests.

---

## 4. Verification

### Whole-solution build
`dotnet build -c Debug` → **0 warnings, 0 errors**.

### New P361 tests (`AutoReloadOnEmptyTests`, 4/4 pass)
1. `Firing_ammo_to_zero_auto_reloads_from_cargo_spare` — a real decoded-core
   weapon fires a ship's pool to zero through the authoritative mission tick;
   the cargo spare is consumed and ammo returns > 0 with **no manual input**
   (`LastAutoReloadOutcome == Reloaded`, cargo slot cleared).
2. `Firing_ammo_to_zero_with_empty_cargo_stays_depleted` — same drive with an
   empty cargo hold: no crash, ammo stays 0, `LastAutoReloadOutcome == Depleted`
   (distinct from `Reloaded`, as P362 needs).
3. `Auto_reload_respects_the_debounce_rate_limit` — a second zero-transition at
   the same instant is `NotAttempted`; after the real `0.5/c_fcidMountRate`
   interval elapses it attempts again.
4. `Auto_reload_relays_RELOAD129_sector_wide` — **live-wire proof**: over real
   loopback TCP, a real server-side auto-reload on a bot's ship relays the
   applied `RELOAD(129)` entries sector-wide with `shipID` filled in to a
   same-sector observer bot; the 6-round cargo spare drains entirely into the
   pool (`ship.Ammo == 6`, cargo slot cleared).

### Manual path (P192) regression
`CargoBayReloadTests` (9) and `RepairAndRefuelTests` (9) still pass unchanged —
the auto-reload only adds a trigger, it does not alter the manual
`FM_CS_RELOAD` path. (The only failures seen in the cargo/live-wire suites are
the pre-existing documented `SeatThree`-style boot timing flakes — `Single`
racing the ship registration — which vanish on re-run.)

### Full test suite
`dotnet test` (whole solution) run ≥ 2×: only the pre-existing documented
live-wire timing flakes are expected (see §4 of the landing report).
