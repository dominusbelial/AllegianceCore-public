# Optional anonymous benchmark sharing

Allegiance can share benchmark measurements to improve recommended settings and identify
performance regressions. **Sharing defaults to No**, is remembered, and can be changed in
Options → Settings → Anonymous benchmark sharing. If no collector is configured, these controls
are hidden and no requests are made. The game works offline and sharing never waits on the game thread.

After a benchmark, the prompt is: “Share these results to help improve the recommendations?”
Press S to open its controls, Y for Yes, N for No, or V for **View what's sent**. Before first opt-in,
the JSON draft has a null install ID. Yes creates the random ID; V then shows the exact queued
JSON. You can inspect the local archive/queue as well. Up/Down/PageUp/PageDown scroll the viewer; Left/Right selects queued result or follow-up JSON.

We send CPU model/core/thread counts, GPU model/VRAM/driver, RAM capacity, OS family/version,
display resolution, benchmark FPS/frame/server/bot timings, workload size, settings used/suggested,
and game version/commit. Unavailable measurements are null. We use a random install ID created
only when you opt in. After three subsequent play sessions or seven days, a small follow-up says
whether you kept the suggestion and lists names of settings you changed. It contains no new
setting values. See [the exact schema](RESULT_SCHEMA.md).

We do **not** collect pilot names/callsigns, usernames, hostnames, file paths, serial numbers,
or IP addresses in the stored benchmark data. The network provider necessarily handles your IP
to deliver a request. The collector uses it transiently for rate limiting, hashes a minute-scoped
key, and writes no IP or request headers to D1 or application logs. Worker invocation logging is
disabled. Deployment operators must also keep request logging/Logpush disabled for this service;
Cloudflare's own infrastructure handling remains subject to its policies.

Shared data is **private**. Only administrators with a secret bearer token can export raw results
or view aggregates. There is no public results page. Raw results and follow-ups are retained for
24 months from receipt, then removed by a daily retention job. Expired rendered FPS samples
become anonymous CPU/GPU class/preset histograms (FPS rounded to 0.1) retained indefinitely.
These aggregates contain no install/event IDs or individual hardware models.

**Delete my shared data** (D in sharing settings) queues deletion of raw results and follow-ups
for the current install ID, turns sharing off, and resets the ID. If offline it retries on later
launches. The old ID remains only in the pending local delete request until it succeeds. A later
opt-in creates a different ID. Anonymous aggregates cannot be associated with your ID and remain.
Losing the local install ID prevents identifying your prior data; deletion does not recover IDs
from another installation or a removed configuration file.

Turning sharing off cancels pending result/follow-up uploads; it does not delete already shared
rows. An upload already in flight may finish. Use Delete to remove shared rows. Results always
remain locally; existing P729 diagnostics are separate and may include a machine name and local
process/bot diagnostics. They are never uploaded by this feature. You may delete local result
files yourself. Back up the random install ID first if you still need to delete remote rows.
