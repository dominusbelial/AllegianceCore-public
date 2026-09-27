# P408 — ship↔asteroid collision HP pool: deliberate decision

**Verdict: DECISION (option b) — keep the unified additive collision-HP
formula for both collision passes. No behavior change.** The code divergence
the review identified is real and confirmed against native source, but
reproducing it literally would copy a known native inconsistency that the
game's own ship↔ship branch explicitly fixed. The port deliberately uses the
single *corrected* additive formula everywhere. Only a documentation-citation
fix lands.

---

## The divergence (confirmed, high confidence)

`Sector.ResolveShipAsteroidCollision` (`src/Allegiance.Core/Model/Sector.cs:2058`)
sizes the ship's collision HP pool via `CollisionHpPool` (`Sector.cs:2404`):
**hull HP + `ShieldFraction × MaxStrength`** (additive `+=`).

Native differs by branch:

- **Static branch** (ship↔asteroid/station/building-effect),
  `shipIGC.cpp:962-965` — *replacement*, not addition:

  ```cpp
  float   hp1 = GetHitPoints();
  if (m_mountedOthers[ET_Shield])
      hp1 = ((IshieldIGC*)m_mountedOthers[ET_Shield])->GetFraction() *
            ((IshieldIGC*)m_mountedOthers[ET_Shield])->GetMaxStrength();
  ```

  When a shield is mounted, `hp1` becomes `fraction × maxStrength` and the
  hull term is **discarded**.

- **Ship↔ship branch**, `shipIGC.cpp:1147-1151` — *addition* (`+=`), with
  native's own corrective comment:

  ```cpp
  float   hp1 = GetHitPoints();
  if (m_mountedOthers[ET_Shield])
      // terralthra fix, this was an = should be a +=, comment added by mmf
      hp1 += ((IshieldIGC*)m_mountedOthers[ET_Shield])->GetFraction() *
            ((IshieldIGC*)m_mountedOthers[ET_Shield])->GetMaxStrength();
  ```

So the ship↔ship branch *was* the same `=` (replacement) originally, and was
later patched to `+=` ("terralthra fix"). The static branch was never given
that fix and retains the original `=`. The port unified both collision
passes onto the ship↔ship (fixed, additive) formula, applying it to the
asteroid case where native never made the change.

**Consequence (confirmed narrow):** `hp1` only scales the damage dealt *to
the asteroid* (`Sector.cs:2080-2084` — `ast.ReceiveDamage(hp1·damage2/
(hp1+damage2))`). Damage to the *ship itself* is scaled by the rock's `hp2`
(independent of `hp1`) and is therefore identical either way. Net effect: a
shielded ship ramming a rock damages the rock *more* in the port than in
native (because the port's `hp1` includes the hull term native discards).
Low gameplay impact — asteroids are destructible static bodies and the gap
appears only in the rock's damage share.

---

## The decision: option (b) — keep the unified additive formula

Reasoning, plainly:

1. **The behavior is already locked in by an existing regression test.**
   The P392 change wired the additive pool into the asteroid pass, and its
   test (`tests/Allegiance.Core.Tests/ShipAsteroidCollisionTests.cs:327-335`)
   *asserts* the additive signature there — a shielded ram must deal *more*
   damage to the rock than the identical unshielded ram, "exactly as the
   ship-ship pass (CollisionHpPool)". Switching the asteroid pass to native's
   literal shield-only (`=`) form would **break that established, tested
   behavior** and reintroduce an inconsistency the port already resolved.

2. **Native's static-branch behavior is the very bug native itself fixed
   elsewhere.** The `=`→`+=` change in the ship↔ship branch is labelled a
   fix ("terralthra fix, this was an = should be a +="). The static branch's
   shield-only form is that same unremediated bug. Faithful reproduction of
   native *intent* means using the corrected formula; literal reproduction
   would copy a known mistake the maintainers fixed on the other branch.

3. **Consistency and simplicity.** One `CollisionHpPool` helper serves both
   collision passes with one formula. Splitting them (option a) would
   introduce a second, divergent pool calculation whose only effect is to
   reproduce a native inconsistency — more code, more divergence surface,
   no fidelity gain.

4. **Player-visible impact is negligible either way.** Because `hp1` only
   feeds the rock's damage share, the ship's own survival is untouched by
   this choice. The sole behavioral delta is a shielded ship chipping an
   asteroid fractionally more in the port — not worth destabilizing an
   established, tested, simpler formula.

Therefore **no production behavior changes.** The only code delta is the
documentation-citation correction (below).

---

## Documentation citation fix (lands regardless of the decision)

`CollisionHpPool`'s doc comment previously cited only the ship↔ship branch
(`shipIGC.cpp:1147-1158`) — the wrong citation for the asteroid call site
once the helper is understood to serve both. Corrected to cite *both*
accurately and state the divergence decision:

- Cites native's **ship↔ship** branch (`shipIGC.cpp:1147-1151`) as the source
  of the additive `+=` formula (hull + `fraction × maxStrength`).
- Explicitly notes native's **static** branch (`shipIGC.cpp:962-965`) still
  uses the old shield-only (`=`) replacement form, and that the port
  deliberately does **not** reproduce it, pointing to this decision doc.

Additionally, the asteroid call site's inline comment (`Sector.cs:2097`) was
corrected: it previously pointed at `shipIGC.cpp:950-953` (the native
velocity-validation block) as the "no-shield branch"; it now cites
`shipIGC.cpp:962-965` (the actual static-branch HP-pool computation) and
notes the shield-only form is intentionally not reproduced.

## Out of scope — untouched (verified)

- Ship↔ship collision (`Sector.cs:2247-2357`) — confirmed correct (additive
  `+=` matches native's own later fix); untouched.
- All other asteroid-collision handling (reflection, damage split, lifepod
  exemption, mining exclusion, `P254`/`P392` logic) — confirmed correct;
  untouched.
- P368–P407 — untouched.

## Verification performed

- **Whole-solution build** (`AllegianceCore.slnx`, 7 src + 4 test + tools;
  `~/.dotnet/dotnet build AllegianceCore.slnx -c Debug`): **0 warnings,
  0 errors**.
- **Full `Allegiance.Core.Tests` suite, run 2×** (the project exercising
  `ResolveShipAsteroidCollision` / `CollisionHpPool`, including the P254
  ram / P392 shielded-ram / P254-mining exclusion / P254-wire live tests):
  only the pre-existing documented live-wire timing flakes expected; the
  collision-HP-pool tests (which assert the retained additive behavior) pass
  unchanged — confirming the no-behavior-change decision introduced no
  regression.
- No behavior test added: per the brief, option (b) requires no new test;
  the existing P392 test already pins the additive formula in the asteroid
  case.
