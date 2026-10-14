# Automatic next match (P660)

The dedicated server starts another match automatically after a game ends. Every connected player stays connected and receives a fresh ship on their previous side. An unseated player is assigned to the least populated side. Existing teams retain their seats and leaders.

Configure this in `server.json`:

```json
{
  "liveSettings": {
    "autoNextMatch": {
      "enabled": true,
      "intermissionSeconds": 30
    }
  }
}
```

These are the defaults even when the section is absent. `intermissionSeconds` accepts a finite value from 0 through 3600. The scoreboard remains visible during this timer. Ready packets cannot shorten it. At its end, readiness is completed automatically and the normal 15-second briefing countdown runs before the match starts. Joining during either countdown receives its remaining deadline.

Both timers use **simulation seconds**. At time scale 0.25, a 30-second intermission takes about 120 wall seconds; under automatic dilation the wall duration varies with the live scale. The client displays the server's remaining simulation time, corrected by server clock samples.

Set `enabled` to `false` to keep the previous ready-up behavior: migrated players retain their ready flags, and unready players must ready up. The older per-game `autoRestart` / `restartCountdownSeconds` setting remains an independent opt-in when this policy is disabled. When the new policy is enabled, its intermission length takes precedence over the legacy timer.

The native basis is `FedSrv/fsmission.cpp:3539-3546` (restart `SET_START_TIME` while NotStarted), `:1926-1976` (briefing and Starting), `:2604-2622` (retain players via reset), and `WinTrek/gameoverscreen.cpp:630-666` (scoreboard timer and automatic team-screen handoff). Default enablement and auto-completing readiness are dedicated-server policy choices. Clock initialization follows `Igc/missionigc.cpp:3055-3059`; the ten-minute guard is preserved.

Verification and measurements: [P660 report](reports/P660_NEXT_MATCH.md).
