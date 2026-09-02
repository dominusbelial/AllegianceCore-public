# CLUSTERINFO — live proof: the real root cause, found and fixed (Claude Code's own pass)

Date: 2026-09-01. Follow-up to P47 (`8b52b76`/`2e41094`) and P48
(`4cc226e`, diagnosis-only — its own carried-in hypothesis correctly
refuted by careful source reading, but the true cause needed the VM P48
itself didn't have). This is that live pass, done directly (matching the
standing P41→P42/P45,46→live-pass/P47→P48 division of labor).

**Headline verdict: FIXED. A real `Wopr.exe` bot's `ScoutExploreMap`
strategy now genuinely receives `FM_S_CLUSTERINFO(200)` and engages its
smart target-selection logic — confirmed live, verbatim:**
```
ScoutExploreMap explorer0@BOT: 		Unexplored Warp found: Arrokoth
ScoutExploreMap explorer0@BOT: 		Found target warp, going to Arrokoth
```
The first target-selection cycle after join still falls back to random
(expected — before the periodic resend has had time to land); every cycle
after that engages the real smart path.

## 1. P48's own hypothesis was correctly refuted — and it was right to stop there

P48 traced the native receive path in full via source reading and
correctly concluded the 2048-byte `m_rgbbuffAlloc` buffer (`Inc/
messagecore.h:636`) cannot be the cause — it's outgoing-construction-only,
never touches the receive path, and even there the real allocation is
`cbmsg`-sized, not 2048-bounded. It declined to guess further and handed
back a precise, ordered diagnostic list for the live pass. That refusal to
force an unconfirmed fix was correct — the real cause was different from
anything visible in source alone.

## 2. The real cause, found via live instrumentation

An unconditional diagnostic (`Console.WriteLine` on every raw `fmid`
received, before any filtering) placed in `MessageReceiver.OnAppMessage`
(`Allegiance/src/Wopr/Wopr/MessageReceiver.cs`) showed, across a whole
20-second isolated single-bot trial: **`OnAppMessage` only ever received
fmid 78 (`SINGLE_SHIP_UPDATE`) and fmid 90 (`SHIP_STATUS`) — never once
200 (`CLUSTERINFO`), and never any of the one-time join-burst messages
(`MISSIONDEF`, `PLAYERINFO`, `EXPORT`, `CLUSTERINFO`, `MISSION_STAGE`)
either.**

The cause: `StrategyBase.Attach()` (`Allegiance/src/Wopr/Wopr/Strategies/
StrategyBase.cs:89-90`) is what wires `client.OnAppMessage +=
_messageReceiver.OnAppMessage` — and `Attach()` is only called from
`TeamDirector.cs`'s direct-connect flow **after** the ship is already
assigned, which itself is confirmed via a `SendAndReceiveUpdate()` polling
loop that runs *before* the managed subscription exists
(`TeamDirector.cs`, the "Direct connect: waiting for ship assignment"
block). So the entire join burst — including `CLUSTERINFO`, sent exactly
once, early — is processed by the native client's own internal pump
*before* `MessageReceiver.OnAppMessage` is ever listening. It is
structurally, unconditionally too late, every single time, for every bot.

**This is not a new bug pattern — it's the exact same one `AllegianceCore`
already solved for `SHIP_STATUS`, documented in this repo's own code**:
`Server.cs`'s `BroadcastShipStates` doc comment already says, verbatim,
*"the periodic path exists because StrategyBase.cs:90 wires the client's
MessageReceiver only on strategy attach, which races the join burst."*
P47 simply didn't apply the same, already-established fix to the new
message it added.

## 3. The fix (AllegianceCore, `src/Allegiance.Server/Server.cs`)

Mirrors `SHIP_STATUS`'s own periodic-refresh mechanism exactly:
- `_lastClusterInfoTick` (per-peer) + a cached `_clusterInfoBytes` (mission-
  wide, encoded once — `CLUSTERINFO` doesn't vary per peer or change during
  a mission, unlike `SHIP_STATUS`).
- `ClusterInfoRefreshTicks = 200` (10 s at 20 upd/s) — longer than
  `SHIP_STATUS`'s 2 s interval since `CLUSTERINFO` is static, 4204 B
  (vs `SHIP_STATUS`'s ~24 B), and only needs to land *once* after the
  managed subscription exists, not track live state.
- Resend logic added to `BroadcastShipStates`'s existing per-tick,
  per-peer loop, same structure as the `SHIP_STATUS` refresh block right
  above it.

Build clean, **2235/2235 × 3 consecutive test runs** (one flake seen on
an early run, confirmed pre-existing and unrelated by reproducing it
*without* this change too — reverted-and-reran, same intermittent
failure, so not caused by this fix).

## 4. A real, separate bug also found and fixed (native, defensive only)

While tracing this, found and fixed a genuine (if ultimately unrelated to
this specific symptom) hazard in the same function: `MessageReceiver.cs`'s
own debug logging (`File.AppendAllText(@"c:\1\Logs\" + _playerName +
"_messages.txt", ...)`, line 44) runs inside the *same* `try` block as the
entire dispatch `switch`, with zero synchronization — a genuine
`IOException` race under concurrent access (confirmed directly in every
bot's own `OnAppMessageExceptions.txt` log from earlier multi-process
stress testing this session), which would silently abort dispatch for
whatever message triggered it. Wrapped in its own try/catch so a logging
failure can never block real message processing. This is real and worth
keeping, but — confirmed by testing in full isolation — is **not** what
was blocking `CLUSTERINFO` specifically; that was purely the subscription-
timing race in §2.

## 5. Repos after this pass

- `AllegianceCore`: the periodic-resend fix, committed and pushed (see
  commit hash in `git log`). 2235/2235 × 3.
- `Allegiance` (native): the `MessageReceiver.cs` try/catch hardening,
  committed (defensive fix, real bug, kept per this project's own
  precedent for small well-understood native fixes found via live
  diagnosis — P33/P36/P37). All temporary diagnostic instrumentation
  (`[P47DIAG]`/`[P48DIAG2]` prints, the raw-fmid unconditional log) fully
  reverted before this commit — only the two real fixes remain.

## 6. What this closes

The full arc — P37 (first real flight) → P41/P42 (warp-transit) →
P43-P46 (spectator visualizers) → P47 (CLUSTERINFO wire message) → P48
(diagnosis, correctly refuted its own hypothesis) → this pass (real cause
found live, fixed, confirmed) — closes with real, autonomous, *smart*
WOPR AI exploration now genuinely working end-to-end for the first time in
this project: a real bot, real map data, real informed navigation
decisions, not random guessing.
