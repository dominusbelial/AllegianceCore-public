# P555 — CRITICAL decode bug: `DataHullTypeIGC.pmEquipment` is read at byte 509, native alignment puts it at 510. Every hull's non-weapon mount masks (shields, chaff, magazines, dispensers, cloaks, packs, afterburners) are garbled

## Why this task exists (found and verified by Claude, 2026-10-05)

`src/Allegiance.Core/Data/DataHullTypeIGC.cs` (`Decode`, ~line 309):
`pmEquipment[i] = ReadInt16LittleEndian(b.Slice(509 + 2 * i));`

**Native layout** (`~/Development/Allegiance/src/Igc/igc.h:1767+`, `struct DataHullTypeIGC`):
… `HitPoints hitPoints` (float) @452, `short hardpointOffset` @456,
`DefenseTypeID defenseType` (unsigned char) @458, **`short capacityMagazine` @460**
(2-byte aligned: the port itself reads 460, not 459), `capacityDispenser` @462,
`capacityChaffLauncher` @464, `PartID preferredPartsTypes[14]` @466–493,
`HullAbilityBitMask habmCapabilities` (short, `igc.h:723-725`) @494,
`char textureName[c_cbFileName = 13]` @496–508, then
**`PartMask pmEquipment[ET_MAX]` (short) is 2-byte aligned → @510**, followed by
`SoundID interiorSound`, `exteriorSound`, … (check every field after it too).

**Proof from PCore001** (shield partMasks: Sm 0x0001, Med 0x0002, Lrg 0x0004,
Med Aslt 0x0010; `pmEquipment[ET_Shield=4]`):

| hull | @509 (today) | @510 (native alignment) |
|---|---|---|
| Scout/Adv Scout (210/285) | 0x011C → Lrg + Med Aslt only | **0x0001 → Sm Shield 1/2/3** |
| Adv Fighter 288 | 0x0104 → Lrg only | **0x0001 → Sm** |
| Bomber 204 | 0x0200 → none | **0x0002 → Med** (bomber 195: 0x0012 → Med + Med Aslt) |
| Cruiser 322 / Destroyer 291 | 0x0400 → none | **0x0004 → Lrg** |

Consequences today: scouts/fighters can't mount their own native preferred
shield (`PreferredPartsTypes` lists `Sm Shield 1`) but can mount capital
shields (the "invincible bots" of P539/P539b, Lrg Shield 2, 3750 strength);
bombers and capitals can mount **no** shield; chaff, magazine, dispenser,
cloak, pack and afterburner mounting are all equally garbled. The server's
purchase/loadout validation (`CanMountPart`, `Server.ShipCommands.cs:~2491`,
P334/P483), the P519 shop and the MCP bot loadout (P539b
`PreferredPartsTypes` filtered by `CanMountPart`) all inherit it.
**P539b's report concluded "native rejects Sm Shield 1 on Adv Scout too, not a
bug". That was circular (it checked the port's own decoded masks).**

## Scope

1. **Fix the decode offset** for `pmEquipment` (→ 510) and **re-derive every
   field after `textureName`** against native alignment (sounds, and
   anything else in `DataHullTypeIGC` past 509). Keep the `Size` and
   hardpoint-stride arithmetic consistent; the existing "exact record size"
   check must still pass for all 9369+ corpus records.
2. **Audit the other IGC decoders for the same class of bug:** any
   `char[N]` with odd N followed by a `short`/`float` (e.g. part types,
   station types, missile/projectile types, civs, developments, treasure
   sets, map records). For each, compare offsets with native alignment. Fix
   any found, with a corpus check.
3. **Corpus-level sanity tests** (failing first):
   - every hull's `PreferredPartsTypes` entries that are equipment parts
     **mount** on that hull (native designers listed them as defaults), for
     every hull in PCore001 (and other cores in `~/Development/Artwork`, if
     present);
   - the shield table above (Scout → Sm, Bomber → Med, Cruiser → Lrg);
   - interceptor/afterburner and chaff/magazine spot checks against their
     preferred parts.
4. **Downstream:** the P539b MCP loadout must now resolve the full native
   preferred loadout, including `Sm Shield 1`, for the Adv Scout. Re-run the
   P539b/P539d unit tests and update their expectations. The shop and launch
   loadout tests too.
5. **Correction notes** (dated) in `docs/reports/P539b_MCP_BOT_REAL_HITS.md`
   (the "not a bug" section) and P483/P334 if they asserted the old masks.

## Explicitly out of scope

- Tech-level design changes (P556).
- Balancing.

## Proof

- Failing-first tests as above (they fail at 509, pass at 510).
- Corpus run: the count of hulls whose preferred equipment parts don't
  mount, before (expect many) vs after (expect 0, or explain each one with
  native data).
- **Live loopback:** a 5v5 `spawn_scenario` shows Adv Scouts launching with
  `Sm Shield 1` (the tool's loadout output + server-side ship shield
  strength 150), and a player buying `Sm Shield 1` in the shop succeeds.
- Report `docs/reports/P555_HULL_MASK_DECODE.md`: offsets table, corpus
  before/after, other decoders audited, downstream changes.

## Verification (common)

- **Failing-first tests** for every fix: the test fails on today's code and passes after.
- **Native fidelity:** cite native file:line for every rule you port. If you find the
  spot-check's reading wrong, prove it with native code and say so.
- Whole-solution build, 0 warnings and 0 errors. Full suite via `scripts/run-tests.sh`
  (P471), 3 consecutive runs **on the final code**. Cite `docs/TEST_FLAKES.md`.
  **Report only numbers you actually measured. Don't re-run a failing suite until it
  comes up clean;** report each run as it happened.
- ⚠️ **Commit as soon as the code and tests are done, BEFORE the 3× suite**, then amend
  with suite numbers. **Include every new file** (`git status --untracked-files=all`).
  Confirm with `git log -1` and a clean `git status`.
- Committed on **`master`**, not pushed. Check with `git branch --show-current`.

## ⚠️ Session rule: never end a turn while waiting

This session runs in one-shot mode: **your session ends the moment you finish a message.** Run long commands (the 3× `scripts/run-tests.sh` suite, live proofs, builds) **in the foreground**, blocking, one after another. **Never** start them in the background and then reply "waiting for it to finish". Two earlier sessions (P529, P542) did exactly that: the session ended mid-suite, the background processes were killed, and the suite results were never recorded. If a command might exceed a tool timeout, run each suite run as its own foreground call and record its numbers before starting the next.
