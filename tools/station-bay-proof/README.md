# Station bay proof (P701)

The client inspector supplies the real backbuffer screenshots. This directory
also contains an independent MDLX inventory, a GPU audit, and tools to assemble
the screenshots. Run from the repository root with `ALLEGIANCE_ARTWORK_PATH` set.

```sh
dotnet build tools/station-bay-proof
# Loads, uploads and renders all 81 PCore001 station models / 207 groups; verifies all 105 texture readbacks.
dotnet run --project tools/station-bay-proof --no-build -- /tmp/station-gpu-audit.csv

# Every station type, original inspector workflow:
dotnet run --project src/Allegiance.ClientCore.MonoGame --no-build -- \
  --inspect-stations --inspect-angles 4 --inspect-light-time 0.1

# One type or all variants of a model; centred close view with native lights:
dotnet run --project src/Allegiance.ClientCore.MonoGame --no-build -- \
  --inspect-stations --inspect-station 110 --inspect-angles 4 \
  --inspect-native-only --inspect-dist 1000 --inspect-light-time 0.1
```

Omit `--inspect-light-time` to animate from the live inspector clock. Screenshots
land in `screenshots/` below the process working directory. `--inspect-station`
accepts a numeric station type id or an exact model stem. Add `--inspect-pitch 70`
for an overhead view into vertically entered bays, or a negative elevation to
look into bays on the underside.

The checked-in P701 oracle was extracted with the original Python MDLX reader
(`mdl_parse.py`), independently of the C# parser. Refresh it and the group table:

```sh
python3 tools/station-bay-proof/audit.py \
  --reference-reader /path/to/original/python/reference \
  --artwork "$ALLEGIANCE_ARTWORK_PATH"
```

The original reader supplies the MDLX stack decoder; `audit.py` independently
walks texture scopes and reads every native 48-byte LightData. The station list
comes from the existing P615 PCore001 inventory; the xUnit test checks it against
the current core so newly referenced models cannot silently escape the audit.

```sh
python3 tools/station-bay-proof/compare.py \
  --before /path/to/before/screenshots --after /path/to/after/screenshots \
  --output docs/reports/P701_assets/stations
```

`compare.py` preserves the three settled matching camera views (yaw 0/90/180)
from the existing inspector sweep. Its initial angle-0 frame still uses the
preceding pose, so that frame is excluded. Before P701, PNG CRCs were invalid;
the assembler repairs only checksum bytes in memory, preserving pixel payloads.

For the blink proof, run the one-type close-view command in separate working
directories named `0.000`, `0.025`, `0.050`, `0.075`, `0.100`, `0.125`, `0.150`,
and `0.500`, passing each directory's time as `--inspect-light-time`. Then:

```sh
python3 tools/station-bay-proof/pulse.py \
  --captures /path/to/phase-directories --output docs/reports/P701_assets
```

The GIF uses real frame samples and the native 1.25-second cycle. The peak/off
PNGs retain the full backbuffer; pixel differences are measured below the HUD.
Python assembly tools require Pillow. The GPU audit needs an available DesktopGL
display (the same requirement as the real station inspector).
