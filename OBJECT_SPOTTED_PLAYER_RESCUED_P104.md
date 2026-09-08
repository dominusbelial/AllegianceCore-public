# OBJECT_SPOTTED_PLAYER_RESCUED_P104 — wire layer only

Date: 2026-09-08. Branch: `master`. Prior head: `2c50e3e` (P103) + this slice.

P104 adds the wire codecs for `FM_S_OBJECT_SPOTTED` (6) and
`FM_S_PLAYER_RESCUED` (16). **Wire only** — same "new codec, defer the
trigger" discipline used by P61/P89/P93/P98. Neither trigger is wired;
both depend on mechanics this port does not yet have (a scanning/detection
system, and the rescue-probe capability). This slice only defines the
message shapes.

## Source of truth

`src/Inc/Messages.h:76-81` (object spotted) and `src/Inc/Messages.h:163-166`
(player rescued), quoted in full:

```
DEFINE_FEDMSG(S, OBJECT_SPOTTED, 6)
  ObjectType    otObject;
  ObjectID      oidObject;
  ObjectType    otSpotter;
  ObjectID      oidSpotter;
END_FEDMSG                                  // Messages.h:76-81

DEFINE_FEDMSG(S, PLAYER_RESCUED, 16)
  ShipID    shipIDRescuer;
  ShipID    shipIDRescuee;
END_FEDMSG                                  // Messages.h:163-166
```

**Field widths.** Both `ObjectType` members are the *native* typedef
`typedef short ObjectType` (`src/Igc/igc.h:115`), and `ObjectID` is
`typedef short ObjectID` (`igc.h:477`); `ShipID` is likewise a short. The
4-byte `[v1_enum]` int32 is **only** the EXPORT envelope's `objecttype`
discriminator, unrelated to this message's members — exactly the distinction
`DataProbeExport.cs:50-54` draws for its own `otTarget`. Consequence: no
4-byte-enum alignment gap, a clean back-to-back run of shorts. (This mirrors
the P98 correction where the brief initially assumed a 4-byte enum.)

## Byte layout — ObjectSpotted = 12 B

```
  offset  0: ushort cbmsg
  offset  2: ushort fmid = 6
  offset  4: short  otObject    (ObjectType, igc.h:115)
  offset  6: short  oidObject   (ObjectID,  igc.h:477)
  offset  8: short  otSpotter   (ObjectType, igc.h:115)
  offset 10: short  oidSpotter  (ObjectID,  igc.h:477)
  = 12 B fixed, no var items → cbmsg = 12
```

Arithmetic: hdr(4) + 4×(short 2) = 4+8 = 12. No alignment gaps: all four
members are 2-byte shorts sitting back-to-back at 4-11, no member wider
than 2.

## Byte layout — PlayerRescued = 8 B

```
  offset  0: ushort cbmsg
  offset  2: ushort fmid = 16
  offset  4: short  shipIDRescuer  (ShipID)
  offset  6: short  shipIDRescuee  (ShipID)
  = 8 B fixed, no var items → cbmsg = 8
```

Arithmetic: hdr(4) + 2×(short 2) = 4+4 = 8. No alignment gaps: both 2-byte
ShipIDs sit back-to-back at 4-7, no member wider than 2.

## Real native senders (cited; not implemented)

Both messages' triggers are out of scope here, but recorded for honesty.

- `OBJECT_SPOTTED` (6): emitted by `FedSrv/FedSrv.CPP` while traversing a
  side's vision system, twice per sighting — the ally branch at
  `FedSrv.CPP:11015` (`pfmObjectSpotted->otObject/oidObject = pmodel`,
  `otSpotter/oidSpotter = s`) and the spotting side's own branch at
  `FedSrv.CPP:11058` (same assignments at `FedSrv.CPP:11060-11063`). It is
  gated on `bAllySent` / `bSent` and the model type being a station, warp or
  special/helium asteroid (`FedSrv.CPP:11009-11011, 11035-11037`). This
  trigger requires the port to have a scanning/detection system — the same
  gap already named in Phase 2 for probes (`Detected = true // no scanning
  system yet`). Not wired.
- `PLAYER_RESCUED` (16): emitted by the server's rescue handler
  (`FedSrv.CPP:13052`), which awards rescue/picked-up achievements
  (`AddRescue`, `AwardPodPickup`, `AwardGetRescued`, `FedSrv.CPP:13039-13051`)
  then writes `shipIDRescuer = pshipRescuer->GetObjectID()` and
  `shipIDRescuee = pshipRescued->GetObjectID()` (`FedSrv.CPP:13054-13055`).
  Its real trigger is the rescue-probe capability
  (`c_habmRescue`/`c_habmLifepod`) — seen but not acted on during Phase 2's
  probe work. Not wired.

## Files

- `src/Allegiance.Core/Wire/ObjectSpotted.cs` — `IFedMessage` codec, fmid 6,
  12 B fixed, four fields. `Encode`/`Decode` matching the established shape
  (`MineDestroyed.cs`/`ProbeDestroyed.cs` template).
- `src/Allegiance.Core/Wire/PlayerRescued.cs` — `IFedMessage` codec, fmid 16,
  8 B fixed, two fields. Same shape.
- `tests/Allegiance.Core.Tests/Wire/ObjectSpottedPlayerRescuedWireTests.cs` —
  12 new tests: fixed-size asserts, field-by-field round-trips, byte-exact
  expected buffers via `ByteBuf`, and decode-side rejection (wrong fmid,
  too-short buffer, mismatched cbmsg) for each.

## Verification

No VM. Pure round-trip + byte-level tests, no sockets, no server.

- 3 consecutive clean `dotnet test` runs.
- Test count going in: **2415** (baseline 2403 + 12 new). Measured on the
  final clean run below; baseline derived as the suite minus the 12 new
  tests this slice adds.

## Out of scope (future trigger-wiring tasks)

- Any server-side trigger/handler/broadcast for either message.
- Scanning/detection system (`c_mtScanner`) — the prerequisite for a real
  `OBJECT_SPOTTED` trigger.
- Rescue-probe capability (`c_habmRescue`/`c_habmLifepod`) — the
  prerequisite for a real `PLAYER_RESCUED` trigger.
- No change to existing probe/ship code.
