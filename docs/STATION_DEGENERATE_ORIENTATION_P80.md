# P80 — fix: safe default orientation for stations with degenerate Up/Forward

**Scope:** fix + unit verification only. Pure `AllegianceCore`-side change.
Live-VM verification **not attempted** (optional per task; see
"Verification" for the recipe and honest note). No VM / native tree
modification. `Miner.cs` (P77), `PlayerInfo.cs` + its test (P78) untouched.
`DataStationIGC.Decode`/`.Encode` untouched (stayed byte-for-byte faithful
codecs — see scope).

---

## TL;DR

`ExportStation.FromStation` (`src/Allegiance.Core/Wire/ExportStation.cs`)
now detects a degenerate orientation — one whose native cross product
`CrossProduct(Forward, Up)` is the zero vector — and substitutes the native
client's own identity fallback `Forward=(0,0,-1)`, `Up=(0,1,0)` before
building the wire message. This eliminates the `Orientation::SetUp` assert
storm P79 root-caused: both Garrison stations in `m2xspiderw.igc` carry
`up=fwd=(0,0,0)`, the server forwarded those bytes unchanged, and the
reference client's assert fired on **every** connection, flooding the exe
directory with `.dmp` files (418 in one 12-run batch) while the process
survived. This is a separate, deterministic, non-fatal bug from the
still-unresolved P79 crash — not conflated here.

Tests: 3 consecutive clean full-suite runs, 2293 passing each.

---

## The fix (`ExportStation.cs`)

`FromStation` (was a one-line pure passthrough) now guards the orientation:

```csharp
public static ExportStation FromStation(DataStationIGC s)
{
    return new() { Station = DegenerateCrossProduct(s.Forward, s.Up)
        ? SafeFallback(s) : s };
}
```

`DegenerateCrossProduct` ports the native `CrossProduct` formula
(`src/zlib/vector.cpp:62-71`) exactly — same float arithmetic and operand
order — computing `rightAxis = CrossProduct(Forward, Up)`:

```csharp
float cx = f[1] * u[2] - u[1] * f[2];
float cy = f[2] * u[0] - u[2] * f[0];
float cz = f[0] * u[1] - u[0] * f[1];
return cx == 0f && cy == 0f && cz == 0f;
```

Substitution fires whenever `rightAxis` is the zero vector (all three
components exactly `0f` — no epsilon, matching the native assert's own
exact `LengthSquared() != 0`). This is **more general than "both inputs are
literally (0,0,0)"**: it also catches any parallel pair such as
`Forward=(1,0,0), Up=(2,0,0)` (cross product genuinely zero, neither input
zero), which is exactly the invariant the native `Orientation::Set` →
`SetUp(CrossProduct(rightAxis, forwardAxis))` needs. Non-length-3 `float[]`
are treated as degenerate (garbage we cannot forward meaningfully).

`SafeFallback` rebuilds the station with only `Forward`/`Up` changed,
preserving `Position`, `Rotation`, and every other field:
`Forward=(0,0,-1)`, `Up=(0,1,0)`. These are the native identity fallback —
derived from `Orientation::Reset()` (`src/zlib/orientation.cpp:307-311`,
identity matrix) read through `GetForward()`/`GetUp()`
(`src/zlib/orientation.h:83-98`): `GetForward()=(-m_r[2][0],-m_r[2][1],
-m_r[2][2])=(-0,-0,-1)=(0,0,-1)` and `GetUp()=(0,1,0)`. So a
degenerate-orientation station now presents to the client exactly as if the
client's own `Orientation::Reset()` had run.

Done only at the wire-construction layer; `DataStationIGC` codec and every
other consumer of a decoded record keep seeing the map's real bytes.

---

## Tests added (`tests/Allegiance.Core.Tests/Wire/ExportStationWireTests.cs`)

Byte-level, matching this file's existing assertion style:

1. `ExportStation_FromStation_DegenerateOrientation_UsesSafeFallbackInWireBytes`
   — the real `m2xspiderw.igc` Garrison values (`Up=Forward=(0,0,0)`,
   `Position=(1120,1560,0)`, StationId 4328) encode with `Forward=(0,0,-1)`
   and `Up=(0,1,0)` at the correct wire offsets (Up at message offset 24-35,
   Forward at 36-47, inside the 88-byte payload at message offset 12), while
   `Position` and the trailing IDs stay byte-exact unchanged.
2. `ExportStation_FromStation_ParallelNonZero_OrientationsAlsoFallBack` —
   `Forward=(1,0,0)`, `Up=(2,0,0)` (parallel, neither zero, cross product
   genuinely zero) also substitutes — proving the check ports the real
   cross-product invariant, not the narrower "both fields literally zero".
3. `ExportStation_FromStation_NormalOrientation_RoundTripsUnchanged` — a
   normal real corpus value (`Up=(0,1,0)`, `Forward=(0.5,0,0)`) passes
   through bit-exact; substitution does not fire for real data, and all
   other fields round-trip unchanged.

---

## Verbatim test output — 3 consecutive clean runs

Each `dotnet test` against
`tests/Allegiance.Core.Tests/Allegiance.Core.Tests.csproj` (net10.0),
`/home/domingo/.dotnet/dotnet` explicit (not the snap stub):

```
Passed!  - Failed:     0, Passed:  2293, Skipped:     0, Total:  2293, Duration: 37 s - Allegiance.Core.Tests.dll (net10.0)   [run 1]
Passed!  - Failed:     0, Passed:  2293, Skipped:     0, Total:  2293, Duration: 36 s - Allegiance.Core.Tests.dll (net10.0)   [run 2]
Passed!  - Failed:     0, Passed:  2293, Skipped:     0, Total:  2293, Duration: 40 s - Allegiance.Core.Tests.dll (net10.0)   [run 3]
```

(The suite was 2290 before this task; +3 new tests → 2293.)

One non-green run during development caught a test-authoring bug in my own
new test (position assertion typo, `Expected: 0 Actual: 1120`) and a
compile fix (unqualified `ExportStation`); those were corrected before the
clean runs above. No production code was involved in those failures.

---

## Live VM verification — not attempted

Optional per task scope ("pure `AllegianceCore`-side, loopback-testable"
verification is sufficient). I chose to rely on the byte-level unit tests +
3 clean full-suite runs rather than spin up a VM run this session. When you
want the end-to-end check:

1. Build `tools/vizfight` Release directly:
   `/home/domingo/.dotnet/dotnet build -c Release tools/vizfight` and run
   `vizfight explore <port>` (must match the port in `Wopr.exe.config`).
2. On the VM, run `C:\Users\domingo\run_2bot.ps1` per the
   `allegiance-windows-build` skill's recipe.
3. Confirm the exe dir's `.dmp` count stays at/near zero across a run that
   previously produced hundreds — the definitive end-to-end proof the server
   no longer broadcasts the degenerate orientation.

If that run shows dumps appearing anyway, the wire bytes are the prime
suspect and the byte-level tests above already pin them — so a delta would
point elsewhere, not at this fix.

---

## Commits

Committed on `master`, not pushed (per standing practice; left for a human
review pass). Commit message cites P79 as the source of the finding and
explains the fix, in this repo's usual one-paragraph detail depth.

```
<commit subject filled by git>
```
