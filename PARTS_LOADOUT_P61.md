# P61 — Real per-part loadout, slice 1 (FM_C_BUY_LOADOUT + real BUY_LOADOUT_ACK)

**Date:** 2026-09-02
**Repo:** `/home/domingo/Development/AllegianceCore`, tip before this work was `P60` (`45a5cbd`).
**Scope:** retire the owning-player `nParts=0` simplification for the
`FM_C_BUY_LOADOUT(4)` → `FM_S_BUY_LOADOUT_ACK(5)` round-trip only.
`ExportShip`/`NewShipInstance`'s observer-side `nParts=0` (a *different*,
5-byte `PartData` struct) is out of scope and untouched.

---

## 1. Where `ExpandedPartData`'s real field layout was found (source, no VM probe needed)

The task offered the Windows-VM compiler probe as a fallback if the layout
couldn't be pinned from source. It could — the struct is defined inline in the
native `igc.h`, so **no probe was needed**.

- **`src/Igc/igc.h:1687-1690`** — the struct itself:
  ```cpp
  struct ExpandedPartData : public PartData
  {
      BytePercentage  fractionMounted;
  };
  ```
  (inside a `#pragma pack(push, 1)` … `#pragma pack(pop)` begun at
  `igc.h:1673`.) So `ExpandedPartData = PartData + one tail byte`.
- **`src/Igc/igc.h:1674-1679`** — the base:
  ```cpp
  struct PartData
  {
      PartID  partID;
      short   amount;
      Mount   mountID;
  };
  ```
- Constituent types (all in `igc.h`): `PartID = ObjectID = short`
  (`igc.h:477,479`), `amount` is a plain `short`, `Mount = char` (`igc.h:841`),
  and `BytePercentage` stores a single `unsigned char m_percentage`
  (`_Utility/Utility.h:1307-1337`, `GetChar()` returns it; `Divisor = 240`).

With `pack(1)` and those widths the byte layout is fixed:
```
offset 0: short       partID      (PartID = short)
offset 2: short       amount
offset 4: char        mountID     (Mount = char)
offset 5: uchar       fractionMounted   (BytePercentage → unsigned char)
= 6 bytes  = sizeof(ExpandedPartData)   ← matches the P28 compiler-confirmed 6
```
This corroborates (rather than replaces) the three already-trusted numbers
(`sizeof(ShipLoadout)=2`, `offsetof(hullID)=0`, `sizeof(ExpandedPartData)=6`).
Encoded declaration-order, little-endian, in
`src/Allegiance.Core/Wire/ExpandedPartData.cs`.

**Cited against the checked-in native tree** at
`/home/domingo/Development/Allegiance/src/Igc/igc.h` (and
`src/_Utility/Utility.h`). No Windows VM was touched.

### Contrast with the 5-byte `PartData` (out of scope)
The README's 5-byte `PartData` (`partID`/`amount`/`mountID`) is exactly the base
above (2+2+1 = 5) — it is what `ExportShip`/`NewShipInstance` use for their own
still-`nParts=0` observer EXPORT tail. That is a **different struct for a
different message** (ship EXPORT to other observers, roadmap Phase 5) and is
deliberately untouched here. This task only builds the 6-byte `ExpandedPartData`
shape for the owning player's own buy/ack round-trip.

---

## 2. What was built

### New codec — `FM_C_BUY_LOADOUT(4)`
`src/Allegiance.Core/Wire/BuyLoadoutRequest.cs`. Wire shape from
`src/Inc/Messages.h:65-68`:
```
DEFINE_FEDMSG(C, BUY_LOADOUT, 4)
  FM_VAR_ITEM(loadout);
  bool  fLaunch;
END_FEDMSG
```
Layout (var descriptor first, then fixed byte, then blob appended, per
`messagecore.h:777-786`): `header(4) + ib(2)+cb(2) + flags byte(1, bit0=fLaunch)
+ loadout blob (hullID short + nParts×ExpandedPartData)` = `9 + 6·nParts` bytes
total; `9` bytes hull-only. Symmetric to the ACK except the request carries only
the single `bool fLaunch` (no `fBoughtEverything`).

### Generalized `Ship` loadout — `src/Allegiance.Core/Sim/Ship.cs`
- New `Parts` (`IReadOnlyList<MountedPart>`) alongside the pre-existing
  functional `Weapons` — the superset loadout.
- New `MountedPart` (`src/Allegiance.Core/Sim/MountedPart.cs`) — captures the
  resolved static-core record, the four wire quantities
  (partID/amount/mountID/fractionMounted), and an optional functional `Weapon`.
- New `MountPart(DataPartTypeIGC, short amount, byte mountId, byte fractionMounted)`
  — the generalized mount path (records into `Parts`, and for `Weapon` subtypes
  also realizes a projectile-based `Weapon` against the mission core, the same
  `MountWeapon` behavior). `MountWeapon` is unchanged for the combat/spawn
  slice (it lacks the outer record's `PartId`, so it stays its pre-P61 self and
  does not register in `Parts`).
- New `ClearLoadout()` so applying a loadout replaces (native
  `ProcessShipLoadout` rebuild) rather than appends.

### Server dispatch — `src/Allegiance.Server/Server.cs`
- `ProcessDatagram` now has an `else if (fmid == BuyLoadoutRequest.Fmid)` case
  (`Server.cs:1216`), following the `Docked`/`35` pattern.
- `ApplyLoadout` (`Server.cs:1388`): resolves the requested hull and parts
  against `mission.Core.HullTypes`/`PartTypes` (both keyed by the short id,
  `StaticCore.cs:148,159`), clears and re-mounts the ship, then replies with a
  real `BUY_LOADOUT_ACK` whose tail is the ship's **actual** mounted parts
  (`nParts = ship.Parts.Count`, byte-for-byte when every requested part mounted).
  Unknown hull → no ACK (report, don't fabricate). A requested hull differing
  from the ship's fixed spawn hull is noted as Phase-2 (no hull hot-swap; the
  ACK echoes the requested hull id). Zero-part requests stay byte-identical to
  the P26/P27/P28 hull-only form (`fBoughtEverything=false`).

### `BuyLoadoutAck.cs`
Extended to carry `Parts` and encode/decode the `ExpandedPartData[]` tail.
`nParts=0` collapses to the exact historical 11-byte message.

### Dumper
`FedMessageCatalog.Ported` + `FedMessageDumper` gain fmid 4 and 5 formatters
(readable `fedmsgdump` traces of the round-trip for the live-VM pass).

---

## 3. Which subtypes get real in-sim behavior vs. inert bookkeeping

| Subtype | This slice |
|---|---|
| **Weapon** | **Real in-sim behavior** — a requested weapon is realized against the mission core (projectile resolved, a functional `Weapon` constructed and added to `Weapons`; `MountedPart.FunctionalWeapon` set). This matches the behavior that already existed for weapons. |
| **Shield / Cloak / Afterburner / Pack** | **Inert bookkeeping** — represented and round-trippable (remembered on the requested slot with the requested quantities, echoed in the ACK), but **no simulation effect**. No damage mitigation, cloaking, afterburner thrust, or cargo. Phase 2 of the roadmap. |

This asymmetry is explicit and accepted for this slice, per the roadmap
(`allegiance-fedmsg-roadmap.md`): slice 1 only needs every subtype
representable/round-trippable; making the non-weapon subtypes functionally
active is Phase 2 (combat-depth) work.

---

## 4. Verification

Baseline first (`git log` tip was `P60` `45a5cbd`); full suite passed 2268
before any change. After the change the suite passed **3 consecutive clean runs**
at **2280 tests, 0 failed** (~36 s each).

New tests:
- `tests/.../Wire/BuyLoadoutRequestWireTests.cs` — round-trip + exact-byte
  checks for `FM_C_BUY_LOADOUT(4)` (hull-only 11 bytes; one-part 17 bytes; flags
  bit0 = `fLaunch`; rejects wrong fmid / malformed loadout cb).
- `tests/.../Wire/BuyLoadoutAckWireTests.cs` — added parts-tail coverage:
  two-part exact bytes + round-trip; empty-parts still emits the historical
  hull-only 11 bytes; rejects a non-whole-part tail.
- `tests/.../LoadoutRoundTripTests.cs` — loopback proof over real TCP:
  1. **Positive:** connect a bot, complete logon, send a real `FM_C_BUY_LOADOUT`
     naming the ship's actual hull + a real weapon part id (pulled from the
     loaded `StaticCore`) on mount 0, and assert the server's
     `BUY_LOADOUT_ACK` decodes to `nParts >= 1` with the exact requested
     `ExpandedPartData` round-tripped byte-for-byte (value-equality on the
     record, plus a re-encode/re-decode stability check). `fBoughtEverything`
     is true (every requested part granted).
  2. **Regression:** a zero-part (hull-only) request still returns a hull-only
     ACK, `nParts=0`, `fBoughtEverything=false`, byte-identical to the logon
     burst's hull-only ACK — the P26/P27/P28 "adopt a hull" path is unchanged.

The positive test also implicitly proves the part was mounted server-side,
because the ACK tail is built from `ship.Parts` after the apply — an `nParts>=1`
ACK can only appear if the ship actually adopted the requested part.

### Known simplification labels (this repo's honesty style)
- A requested part whose `PartId` is not in the core is skipped (logged), not
  silently dropped from the round-trip count; `fBoughtEverything` becomes false.
- A requested hull differing from the ship's fixed spawn hull is not hot-swapped
  (Phase 2); the parts are granted on the current hull and the ACK echoes the
  requested hull id, with a log line saying so.

---

## 5. What the Claude Code live-VM pass should check next

The remaining unverifiable-from-here question is whether a **real native client**
actually presents the bought parts in its ship. This repository's direct-connect
harness drives a headless bot over the TCP socket; it does **not** drive the
native client's in-game buy screen. So the concrete, verifiable next step for a
live-VM pass is:

1. **Does the native client mount the ACKed parts?** Feed the fixed `PCore001`
   with a real `FM_C_BUY_LOADOUT(4)` + observe the `FM_S_BUY_LOADOUT_ACK(5)`
   reaching `appmsg.cpp:3176-3221` → `ProcessShipLoadout`
   (`shipIGC.h:882-899`), and instrument whether the native `CshipIGC` gains a
   mounted part (non-empty `PartData0()..PartDataN(cb)` and
   `GetPart(&partID, &mountID)` resolving) for each ACKed `ExpandedPartData`.
2. **Loadout-buy screen reachability (say plainly):** this project's harness
   cannot reach the native client's purchase UI. Direct-connect drops a client
   straight into a flying ship; the buy screen belongs to the lobby, which a
   direct-connect client never enters. Therefore "does the buy screen see the
   parts mounted?" is **not verifiable through the current harness** — say so
   plainly rather than claiming it. The closest achievable proxy is step 1
   (the ACK handler mutating the client's own ship). If a future harness gains
   lobby/replay UI driving, that would be the check.

Nothing in this slice changed `ExportShip`/`NewShipInstance`'s observer-side
`nParts=0` (deliberately out of scope), `DROP_PART(33)`, `SWAP_PART(34)`,
`LOADOUT_CHANGE(96)`, `ADD_PART(164)`, or the P60 send-race / P59 feed areas.
