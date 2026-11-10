# Bot decision corpus

`recorded.json` is the P987 **recorded-input** corpus. Each version-2 item has a
stable content-hash id, a scenario (`seeded-2side` or `seeded-3side`), a family
and decision name, and one canonical input snapshot plus its recorded output.
It is not a whole-match trace and contains no wall-clock timeline.

The older six JSON files are P986's small hand-built helper cases. They remain
useful unit goldens, but are not represented as real-match coverage.

## Re-record and regenerate

From the repository root, with `local.env` pointing at the artwork/core and the
native source used by the ordinary test fixtures:

```bash
UPDATE_CORPUS=1 tools/p987-bot-decision-corpus/record.sh
```

The script runs the two recording theories in separate bounded processes,
writes raw JSONL and TRX/logs only under `.scratch/p987/`, then runs the replay
with `P987_RAW_PATHS` pointing at those two raw directories. The replay's
`UPDATE_CORPUS=1` branch dedupes exact records, preserves distinct outputs,
then selects at most 16 items per scenario/family/decision by content hash,
and rewrites `recorded.json` (with LF
line endings). The corpus test enforces the 2 MiB ceiling. To regenerate from
retained raw recordings without rerunning a match, set `UPDATE_CORPUS=1` and
`P987_RAW_PATHS` to a semicolon-separated list of JSONL files or directories,
then run `Recorded_inputs_replay_to_the_same_outputs`.

Normal verification needs no environment variables:

```bash
$HOME/.dotnet/dotnet test tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj \
  --filter FullyQualifiedName~P987RecordedBotDecisionCorpusTests
```

The tests require artwork because loadout and strike decisions read the real
`PCore001.igc`; they carry `[Trait("Requires", "Artwork")]` and skip cleanly on
artwork-free CI.

## When is a diff a real behaviour change?

Replaying the **same stored input** must reproduce the stored output byte for
byte. A replay diff after a code edit is therefore a real change in that pure
decision core (or in its static-core data), never a thread/timing difference.
Inspect the readable expected/actual output diff and the accompanying input;
do not regenerate merely to make such a failure green.

Re-recording samples a fresh non-bit-deterministic match, so its corpus diff
can instead mean that the bots encountered different inputs. Review those
input changes separately from output changes for identical inputs. Stable ids
change when an input or output changes; they never encode wall time, a run
number or a transport peer identity. Raw traces are scratch evidence and are
never committed.
