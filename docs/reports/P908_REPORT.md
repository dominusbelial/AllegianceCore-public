# P908 — static core text fallback at game load

The server and client now load a native text core when its binary is absent,
through the same importer used by `core2txt`. Text cores advertise **1337**;
present binaries retain priority, including when the adjacent text is malformed.
Changes are left in the working tree; no commits, pushes or remote-machine work.

## Production behavior

`StaticCoreCache.Load` keeps the case-insensitive named-cache lookup ahead of
file access. On a miss it opens the binary directly, without probing text. Only
`FileNotFoundException` / `DirectoryNotFoundException` selects the text folder;
a corrupt or unreadable binary does not select text. Names with or without the
`.igc` suffix resolve to the same on-disk binary/folder convention. Version-only
reads inspect only the binary header or the existence of `Constants.csv`; they
do not parse records or acquire a consumer. Native references, rechecked locally:
`Igc/missionigc.cpp:2390-2431,2637-2675`.

Runtime import requires all 22 native tables, permits header-only empty object
tables, checks column shape and complete constants, rejects invalid/nonfinite
numeric fields, and builds the complete model before adding a cache entry.
Malformed imports surface a table/row/column error rather than exposing a partial
core. This replaces native assertions/uninitialized data on malformed input
(`Igc/missionigc.cpp:593-663`). The optional bit-name tables remain optional,
as in `Igc/missionigc.cpp:451-526,555-558`.

All codecs, table helpers and round-trip orchestration moved into the existing
`Allegiance.Core` library under `src/Allegiance.Core/StaticCoreText/`; there is no
second implementation and no new deployable service. `tools/core2txt/` retains
its CLI and verification wrapper. Runtime and CLI use `ImportBody`; the runtime
adds validation and stamps the native sentinel independently of `.coremeta`'s
original binary version. The existing optional sidecar preserves non-text fields,
record order and padding for exact exported-core semantics. Residue is restored
only into bytes the *current* CSV does not supply, so stale residue cannot
corrupt a longer edited name. Without a sidecar, canonical native import order
and deterministic zero defaults apply. The production sidecar-free test also
exercises named tech/part flags and abilities through an actual server boot.

The cache retains P904's ownership policy: each mission/session leases its core;
only the last consumer retires that revision. Client keys include all local CSV
and `.coremeta` file names, mtimes and sizes; an edit creates an independent
revision while old worlds remain valid. Binary keys and binary-path I/O stay
unchanged. Native ownership: `Igc/missionigc.cpp:3033-3053`. SIM-0789 was not edited.

## Review fix: the real game client

Codex's client path covered only the default on-disk resolver. `Game1.Lifecycle.cs`
replaces that resolver with `ResolveAssetMissionCore`, which looked for the binary
only, so a real player could not join a text core. Claude's review added
`src/Allegiance.ClientCore.Game/Assets/TextCoreAssets.cs`, which imports through
`IAssetSource`, so desktop and Android share the fallback. A player bundle's manifest
still refuses files it does not list. `TextCoreAssetsTests` covers it; no real
Game1 join was run. `ShopCatalog` and the offline asset closure still read their
fixed binary cores: they are startup display catalogues, not mission load.

A 1,500-case mutation fuzz of the importer found only `InvalidDataException` or a
successful load, never another exception type.

## Client decision for version 1337

A client must resolve local version 1337 when the server advertises 1337, normally
by installing the matching text core. A
same-name local binary with a different version is rejected with an explicit
out-of-sync join error; the reverse mismatch is also rejected. This follows
`BaseClient::ResetStaticData`, which compares the local version to the advertised
version before loading (`clintlib/clintlib.cpp:4212-4221`); native rejects the join
on failure (`clintlib/appmsg.cpp:2162-2173`). Binary-first resolution is retained,
so users remove a conflicting binary when installing a text-only core. Ordinary
binary/binary version policy remains outside this task.

1337 is native's placeholder, **not a content identity**. Two different text
folders can both advertise it. This task preserves that behavior rather than
inventing a wire checksum. Operators must distribute matching text folders.

## Row verdicts

All ten authorized rows are implemented and production-reachable. No other
matrix rows, export-only reachability, weights or scorer policy were changed.
The existing P868 audit entry for SIM-0788 was synchronized to its implemented
verdict, as its validator requires.
Native paths below are relative to the native source root; line boundaries were
re-grepped before citation. Port paths are repository-relative.

The test shorthand **named** means
`P908TextCoreTests.Sidecar_free_text_loads_named_tech_part_and_ability_masks_through_production`;
**semantic/cache** means
`Text_fallback_has_binary_semantics_version_1337_and_native_cache_lifetime`;
**join** means `Real_server_boot_and_client_join_use_text_only_core_over_loopback`.
All are in `tests/Allegiance.Core.Tests/P908TextCoreTests.cs`.

| Row | Verdict | Native citation | Port citation | Executable evidence |
|---|---|---|---|---|
| SIM-0686 | implemented / production | `Igc/missionigc.cpp:115-125` | `src/Allegiance.Core/StaticCoreText/CoreTable.cs:265` | named; P870 `TechBitFromToken_resolves_loaded_names_case_insensitively_and_NA_unknown` |
| SIM-0687 | implemented / production | `Igc/missionigc.cpp:128-149` | `src/Allegiance.Core/StaticCoreText/CoreTable.cs:280` | named; P870 `TechsListToBits_zero_hex_and_named_tokens`, `Tech_mask_hex_is_native_msb_first_uppercase_and_rejects_odd_length` |
| SIM-0688 | implemented / production | `Igc/missionigc.cpp:152-206` | `src/Allegiance.Core/StaticCoreText/CoreTable.cs:354` | named; P870 `PartMaskFromToken_digit_atoi_negative_spaces_and_index` |
| SIM-0689 | implemented / production | `Igc/missionigc.cpp:361-374` | `src/Allegiance.Core/StaticCoreText/CoreTable.cs:375` | named; P870 `PartsListToMask_adds_token_bits_including_repeats` |
| SIM-0692 | implemented / production | `Igc/missionigc.cpp:451-486` | `src/Allegiance.Core/StaticCoreText/CoreTable.cs:434` | named; P870 `LoadTechBitsList_reads_csv_skips_header_reuses_last_name_and_missing_is_false` |
| SIM-0693 | implemented / production | `Igc/missionigc.cpp:487-526` | `src/Allegiance.Core/StaticCoreText/CoreTable.cs:459` | named; P870 `LoadPartsBitsList_reads_csv_per_equipment_and_missing_is_false` |
| SIM-0694 | implemented / production | `Igc/missionigc.cpp:547-2313` | `src/Allegiance.Core/StaticCoreText/StaticCoreRoundtrip.cs:183`; `codecs/` in that directory | semantic/cache; join; named; P870 all-100-core byte identity |
| SIM-0785 | implemented / production | `Igc/missionigc.cpp:209-357` | `src/Allegiance.Core/StaticCoreText/CoreTable.cs:611` | named; P870 `AbilitiesListToMask_parses_native_ability_names_per_kind` |
| SIM-0786 | implemented / production | `Igc/missionigc.cpp:530-545` | `src/Allegiance.Core/StaticCoreText/CoreTable.cs:484` | named; P870 `ReadCsv_is_tab_delimited_drops_trailing_empty_field_and_keeps_header` |
| SIM-0788 | implemented / production | `Igc/missionigc.cpp:2390-2678` | `src/Allegiance.Core/Model/StaticCoreCache.cs:30`; `src/Allegiance.Server/Server.Lifecycle.cs:778`; `src/Allegiance.ClientCore/State/ClientMissionCoreCache.cs:12`; `ClientWorld.MissionEntry.cs:30` in that client directory | semantic/cache; join; binary preference/corrupt binary; missing-both; malformed/refusal/recovery; client revision retirement/version mismatch; AOT process proof |

The P870 tests remain in `tests/Allegiance.Core.Tests/P870Core2TxtTests.cs`.
Historical export-only matrix citations retain their original tool paths because
the brief explicitly leaves those rows alone; the current shared implementation
is identified above and in `GLOSSARY.md` and `tools/core2txt/README.md`.

## Scorecard

`python3 scripts/parity-scorecard.py` ran before and after the change. Full receipts:
[before](P908_assets/scorecard-before.txt), [after](P908_assets/scorecard-after.txt).

| Scope | Before | After |
|---|---:|---:|
| Sim weighted | 2092 / 2144 = 97.57% | 2112 / 2144 = 98.51% |
| Sim complete eligible rows | 780 / 805 = 96.89% | 790 / 805 = 98.14% |
| FedSrv mission rules weighted | 730 / 764 = 95.55% | 750 / 764 = 98.17% |
| FedSrv complete eligible rows | 319 / 336 | 329 / 336 |
| Test-only implemented weight | 18 | 0 |
| Offline-tool rows / weight | 6 / 12 | 6 / 12 |

The increase is exactly 20 points: nine existing imported-rule rows (18) become
production-reachable, plus the completed LoadIGCStaticCore row (2). All other
scorecard areas are unchanged.

## Validation and evidence

| Check | Result |
|---|---|
| `dotnet build AllegianceCore.slnx` | PASS, 0 warnings / 0 errors |
| `tools/build-tools.sh` | PASS, all 104 projects, 0 warnings / 0 errors |
| Server Release/linux-x64 Native AOT publish | PASS, 0 warnings; no IL2026/IL3050 |
| Published AOT server + production TCP client, text only | PASS, server/client v1337, 447 hulls, 10 civilizations, 13 clusters, launched player |
| New P908 tests, three standalone cohort runs | PASS, 11 cases each = 33/33, no skips |
| Existing affected P870/P869/P904 tests, three standalone cohort runs | PASS, 62 cases each = 186/186, no skips; all 100 shipped static cores byte-identical each run |
| Showcase never-parks | Required five PASS, 5/5; additional control PASS (6/6 total) |
| One Fast tier | FAIL: 12,805 passed / 4 failed / 3 optional skips; 3 timing overruns; all 9 infrastructure checks and all 10 P908 Fast cases pass |
| Glossary/native inventory/P868 audit/diff checks | PASS |

Small retained receipts: [build/AOT](P908_assets/build-aot-summary.json),
[targeted tests](P908_assets/targeted-summary.json),
[Fast](P908_assets/fast-summary.json),
[three-run isolation](P908_assets/isolation-summary.json),
[showcases](P908_assets/showcase-summary.json). Raw logs, TRX, the AOT binary
and exported proof folder are under worktree-local `.scratch/p908/`; no raw
build/test artifacts belong in committed report assets.

The initial restore hit the sandbox's read-only shared NuGet HTTP cache. All
final builds use `NUGET_HTTP_CACHE_PATH` inside `.scratch/p908/`; no project
restore/security settings were weakened. The first Fast launcher exited before
starting the runner; the corrected launcher performed the only actual sweep.
Physical temporary data remains inside scratch, including the directory-descriptor
alias used to preserve outside-repository fixture paths.

The Fast failures are the already-ledgered launcher receipt and mining receipt
intermittencies (both pass 3/3 alone), ship-status receipt (fail/pass/pass alone,
expected 3 / actual 2), and null wingman `DefendTarget` (fail/fail/fail at the same
line 179 as prior baseline controls). The three timing offenders pass all
assertions alone: SC2 support selection (both civ cases), P814 turret-carrier
selection, and P777 warp-exit physics. These seven methods produced 20 standalone
passes and four failures. The full classifications and durations are in
[TEST_FLAKES.md](../TEST_FLAKES.md). Fast remains **FAIL**; no failing assertion
was weakened and no second full sweep was run.

The five required showcases all reached 24/30 objectives, observed real combat
hits and asserted zero sim-stage failures. Durations were 193.123 / 203.466 /
221.938 / 231.264 / 240.802 s; maximum stopped-with-target interval 1.007 s. This
matches P907/P906/P904's saved 5/5 baseline pass rate, without claiming a controlled
performance A/B. Boot receipts confirm server GC and the required baseline.
There were no concurrent tests/builds from this lane during those runs. An
additional queued control also passed in 300.912 s:
P700 local 15v15: 296.9s, samples=2965, reached=24/30, longest speed<1 with target=1.311s, shots=2092, hits=656, kills=6. Thus all six executions passed; the required first five
remain the comparison sample. The first run's retained boot log also confirms
server GC, resolving the earlier testhost-mode uncertainty.

## Not done and limits

The full Fast gate remains red on the pre-existing cases documented above. No
P908 behavior is intentionally deferred. Native text-version content hashing,
core downloads/distribution, binary/binary version-policy changes, native Steam
integrity checks and Windows SEH wrappers remain outside this brief. No training
or HUD behavior changed, so training/artwork captures are not applicable.

## Native intent

**Design:** choose an existing named core first, prefer binary files, allow a
text-authoring folder for live games, and share its lifetime across consumers.
The port preserves those rules, including 1337 and version-only no-import reads.

**Era constraint:** native's text version is explicitly marked `NYI: Version`
(`Igc/missionigc.cpp:2643,2669-2670`). A modern better approach would distribute a
canonical content hash with the core and compare that identity on join. That
would need a separate wire/distribution change; it is not smuggled into 1337.

**Bugs:** native importer assertions, uninitialized memory, boolean/color reader
mismatches and partial launcher-definition scans are not useful compatibility
requirements. The shared P870 codec rulings already document their writer-intent
handling; this task keeps that implementation and optional residue support,
adds atomic runtime validation and prevents residue from overriding edited CSV
bytes. The modern approach is one typed, validated importer with explicit
ownership, plus an optional lossless authoring sidecar—the implementation here.
