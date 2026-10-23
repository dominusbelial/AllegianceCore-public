# Anonymous benchmark events (schema 1, P789 schema 2)

The normative wire contract is [result.schema.json](result.schema.json), JSON Schema draft 7.
It has two alternatives: benchmark results and a later settings follow-up. Every object rejects
unknown properties. UTF-8 request bodies must be at most **32,768 bytes**, including whitespace.
[example-result.json](example-result.json) is an actual source-generated client payload with synthetic hardware.

P789 bumped the schema to **2**: result events may carry three optional fields — `score`
(the Showcase score, `round(ships_in_view_mean × clamp(fps_1pct_low/60,0,1) ×
clamp(tidi_time_weighted_mean/100,0,1) × 10)`), `matchKind` (`showcase-fixed` for the fixed
seed-729/Small/150 s tune Showcase — the only RANKED score — `showcase`, `unranked` or `replay`)
and `scoreNote` (why a score is absent, e.g. headless runs do not render). `schema: 1` events
without these fields remain valid and accepted; `preset` also gained `Grand` (200v200). The
collector's validator is generated from this same schema at build time, so both versions ingest
unchanged. Consent rules are unchanged: nothing leaves the machine without the existing opt-in.

Both events carry `schema: 1`, a random UUIDv4 `eventId` (idempotency key), the random UUIDv4
`installId`, and an ISO-8601 UTC `timestamp`. Wire UUIDs use lowercase. Install ID is generated
and persisted only on first opt-in. Delete resets it, cancels prior uploads and turns sharing off;
a later opt-in creates a new ID. This ID is a deletion capability: keep it private.

| Result field | Meaning |
| --- | --- |
| `hardware` | CPU model, physical cores, logical threads; GPU model, VRAM GiB, driver version; physical RAM GiB; OS family/kernel or OS version; benchmark display width/height in pixels |
| `gameVersion`, `commit` | Build assembly version and source commit (`unknown` for builds without source metadata) |
| `result.mode`, `preset`, `durationSeconds` | P729 workload: skirmish/replay and Small/Medium/Large/Epic/Grand |
| `result.score`, `matchKind`, `scoreNote` | P789 schema 2 — the Showcase score and its ranking kind; optional, v1 events omit them |
| `result.runs`, `median` | Three original runs and P729's selected/scalar-median measurement, with numbers copied without recalculation |
| Run timing fields | `fpsAverage`, `fpsOnePercentLow`, `frameMs`, `cpuMs`, `gpuMs`, `serverTickMs`; distributions contain count, mean, p50, p95, p99, worst in milliseconds |
| Bot fields | `botCount`, `botDecisionCpuMs` in original order, `amortizedCpuMsPerBotSecond`; bot names and worker PIDs are removed |
| `result.bottleneck` | P729's existing classification, unchanged |
| `settingsUsed`, `settingsSuggested` | Numeric/boolean settings only; `settingsUsed` can be null if unmeasured |
| `autoTune` | P729 verification settings, measurement and target-met boolean, preserving up to two checks |

GPU timings and unavailable hardware values are **null**, never fabricated as zero. Headless
results have null graphics timings and may have a 0×0 display. Linux probes `/proc/cpuinfo`,
`/proc/meminfo` and a numeric DRM VRAM capacity where provided by the driver; Windows probes CPU
registry/model, topology and physical memory APIs; macOS uses the named CPU/memory sysctl keys.
Rendered GPU model/driver come from the graphics adapter and GL renderer/version. VRAM can be
null (including unified-memory, NVIDIA and non-Linux drivers without the DRM capacity file).
Physical core/model fields can be null if the platform denies access. No probe reads serial numbers.
The OS version is the platform's OS/kernel version, not a machine-specific distribution description.

Settings keys: `battleSize` (0–3), `difficulty` (0–2), `researchSpeed`, `spectatorDefault`, `fsr`,
`fsrPreset` (0–3), `bloom`, `hdPbr`, `hdTextures`, `frameCap`, `vsync`, `effectsDensity`, `viewDistance`.
Recommendation explanations, free-form diagnostics, worker identities and replay paths are excluded.

`POST /v1/kept` has `resultId` (original event ID), `kept`, and `changedSettings` (an array drawn only
from these setting keys). `kept` must equal `changedSettings.length === 0`. A follow-up is queued
once after three subsequent play sessions or seven elapsed days, whichever the client observes
first. A session is the first successful join or local spectator game in a client launch; benchmark
joins/reconnects do not increment it. Seven-day events require the client to run again; no background
OS task is installed. Only the changed setting names are sent, not their new values. Observation
uses current settings and pauses while a benchmark temporarily changes settings.

## Privacy boundary and local storage

Never serialize the local P729 `BenchmarkResult` directly for upload: it contains `Machine`,
named bots and worker process IDs. `SharePayload` is an explicit allowlist with source-generated
System.Text.Json metadata. Neither event has names/callsigns, paths, usernames, hostnames, serials,
network addresses or arbitrary setting reasons.

The existing P729 local diagnostic remains unchanged. A separate privacy-filtered archive lives at
`ApplicationData/AllegianceCore/benchmark-sharing/results/<eventId>.json`; the durable queue,
consent, install ID and follow-up state live in `benchmark-sharing/sharing.json`. Queue writes use
an atomic replacement. Before opt-in the local preview/archive has `installId: null`; this is a
local draft, not a valid upload. Selecting Yes assigns the ID; **View what's sent** then displays
the exact string queued and uploaded. The collector rejects null install IDs. Local diagnostic
files can contain information excluded from sharing; do not upload them manually as examples.

One ordered background worker performs disk/network work. Uploads use a two-second timeout and
retry with exponential backoff (5 seconds to 6 hours, plus up to 3 seconds jitter), driven by a
30-second game poll. Requests preserve the event ID across retries. Opt-out removes pending
results/follow-ups; an already in-flight request may finish before the ordered opt-out completes.
Deletion jobs survive opt-out/restart and are retried offline. Delete cancels older queued uploads,
waits behind an in-flight upload, then precedes any new-ID uploads. Invalid-body responses are
not retried, but their local archives remain. A failed disk write or malformed state cannot enable
sharing or fail the game. Corrupt state starts with No; prior IDs in a corrupted file may be
unrecoverable, so preserve backups if restoring shared-data deletion access matters.

An endpoint change never redirects a queued result. Opting in to a different collector rotates
the ID and queues deletion at the previously configured collector. An empty endpoint disables
all networking, including queued deletions, until an endpoint is configured again.
