# Bot base defence report

Implemented `FIX_BRIEF.md` on `codex/base-defense`, based on `22d024b8`.
Changes remain in the working tree; no commits or pushes were made. Production
changes are confined to `tools/allegiance-mcp`. No server, transport, client-core,
or march staging/release implementation was changed. Tests and this report are
the only changes outside the bot tool.

## Changes and proofs

All new tests are in `tests/Allegiance.ClientCore.Tests/BotBaseDefenseTests.cs`.
They extend the existing `BotFixRegressionTests` socket-free fixtures through
the partial declaration at `tests/Allegiance.ClientCore.Tests/BotFixRegressionTests.cs:18`.
Tests apply real client wire messages and use real hull classification; command
sends terminate at a closed test queue without opening a socket.

| Change | Location in the final working tree | Test |
| --- | --- | --- |
| Read normal and critical native team alerts once per receiving client, matching an exported friendly station and its sector. Ignore unrelated/public/player chat and share incidents only within the same mission and side. Mission cookies identify peers in the same game even if their local generation counters differ. | `tools/allegiance-mcp/BotManager.Defense.cs:37`, `:51`; loop integration at `tools/allegiance-mcp/BotManager.cs:1618` | `DefenseAlert_BoundedNearestWing_ResumesOriginalRouteAfterQuietAndMinimum` (`:53`), `DefenseRepeatedAlerts_ExtendSameWing_AndRespectSideCapAcrossBases` (`:93`), `DefenseObservations_IgnoreHiddenEnemiesAndUnrelatedChat_AndExpireOnRotation` (`:232`). |
| Select one bounded defender group per incident. Only fighter/interceptor roles with an actual fighter hull qualify; bombers, scouts, pods, lost peers, incomplete objectives, arenas and scenario-owned bots are not recalled. Rank reachable fighters by warp hops, then distance to a station within the home sector, then ship ID. Count existing assignments across bases against the side's budget. | `tools/allegiance-mcp/BotManager.Defense.cs:41`, `:46`, `:99`, `:130` | Bounded-nearest test selects three of twenty bots, including an interceptor and excluding the closer bomber; repeated-alert test caps two defenders across two bases; `DefenseAlert_DoesNotPullOtherRoles` (`:224`, bomber/scout cases). Existing lost-bot and pod regressions also pass. |
| Repeated alerts extend the existing incident instead of recruiting a new wing. Clear after both the quiet window and minimum deployment expire. A delayed world/objective sync still receives the full minimum deployment from assignment. Station removal, side/mission change and an ineligible life cancel stale detours. | `tools/allegiance-mcp/BotManager.Defense.cs:88`, `:113`, `:144`; new-life cleanup at `tools/allegiance-mcp/BotManager.Lifecycle.cs:63`, `:129` | Bounded-nearest and repeated-alert tests; `DefenseDelayedWorldSync_GetsFullMinimumDeployment` (`:190`); observation/rotation test; existing two-death relaunch and mission-identity regressions. |
| Defend locally before an outward warp, including immediately after relaunch. Use only this client's visible co-located enemies, received friendly-asset hits, or enemies near an exported friendly station. Local combat scouts/capitals may defend too; they are not recalled. Hidden, dead, gone and neutral ships are excluded, and the existing miner exclusion remains effective. | `tools/allegiance-mcp/BotManager.Defense.cs:154`, `:176`; before-travel interception at `tools/allegiance-mcp/BotManager.cs:4351` | `DefenseRelaunch_EngagesVisibleBaseAttackerBeforeLeaving_AndPrioritizesBomber` (`:121`), observation/hidden-enemy test. |
| Prioritise bombers and other station-threatening classes using the existing selector and engager caps, then other observed attackers. Suspend an accepted ship order during the immediate defensive engagement and retain it for resumption. | `tools/allegiance-mcp/BotManager.cs:2586`, `:2742` | Relaunch test chooses a farther bomber over a nearer fighter; existing target-spread/focus-fire and accepted-order decision tests pass. |
| Travelling fighters answer a received hit on themselves or a friendly asset, then resume travel when the visible attacker/window clears. Received damage is tracked without consuming an extra hit stream or changing combat metrics. Travelling bombers retain their existing objective and attacker-response path. | `tools/allegiance-mcp/BotManager.cs:3112`, `:3151`; `tools/allegiance-mcp/BotManager.Defense.cs:176` | `DefenseTravellingFighter_AnswersReceivedHitOnSelfOrFriendlyAsset_ThenResumes` (`:141`, two cases); existing `Bomber_SelectsOnlyEnemyStationsInDestination_AndReactsOnlyToVisibleAttackers` and combat-metric tests. |
| Route a defender home using its known graph and normal warp controls without overwriting its objective path, assigned route, arrival history or staging phase. Retain warp-hop telemetry and exit clearance. After clearing, an already-arrived defender routes back to its objective instead of fighting indefinitely in the home sector. | `tools/allegiance-mcp/BotManager.Defense.cs:144`, `:203`; `tools/allegiance-mcp/BotManager.cs:4296`, `:4396` | `DefenseDetour_FliesHomeWarp_PreservesObjectivePath_AndRecordsArrival` (`:203`); `DefenseAfterArrival_ReturnsToOriginalObjective_AndPreservesStaging` (`:172`); bounded-nearest test resumes staging. Existing staging, wing synchronisation and routing tests pass. |
| Expose `state: "defending"` and `defenseSector` in `list_active_bots`, preserving the original role, behaviour and objective fields. Include local defenders as well as recalled fighters. | `tools/allegiance-mcp/BotManager.cs:4655`, `:4777`, `:4797`; `tools/allegiance-mcp/McpTools.cs:219` | Bounded-nearest test checks the production `ActiveBots()` view. |
| Default-on configuration and an A/B disable switch. | `tools/allegiance-mcp/BotManager.Defense.cs:11` | `DefenseDisableSwitch_RestoresTravelPastThreats_AndIgnoresAlerts` (`:159`); `DefenseSettings_DefaultsClampAndInvalidValues` (`:246`). |

## Defaults and reasons

| Setting | Default | Reason |
| --- | --- | --- |
| `ALLEGIANCE_BOT_DEFEND` | Enabled; `0` or `off` disables the new response. Read once per manager. | Immediate default-on fix, with a process-level A/B switch. Existing bomber self-defence remains available when disabled. |
| `ALLEGIANCE_BOT_DEFEND_SHARE` | `0.15`, a fraction clamped to `[0, 1]`; invalid/non-finite input falls back to `0.15`. | Recall roughly 15% of the side's available combat bots, rounded up, preserving most of the offensive march. Only eligible fighters/interceptors fill that budget. |
| `ALLEGIANCE_BOT_DEFEND_CAP` | `30`, configurable in `[0, 200]`. | A 200-bot side recalls at most thirty; larger fleets cannot recall an unbounded group. The cap covers simultaneous assignments across friendly bases. |
| Quiet window | 20 seconds without a new alert or observed station-sector threat. | Covers two native ten-second alert intervals and brief gaps in visibility. |
| Minimum deployment | 30 seconds from assignment. | Prevents immediate reversals and gives a defender time to travel. Repeated alerts refresh the quiet timer rather than resetting recruitment. |
| Station proximity | 2,500 world units from a friendly station. | A conservative local guard area; an alert or received hit also identifies threats beyond that radius. |
| Received-hit threat window | Existing 8 seconds. | Retains the established attacker-response timing. |

The recall quota applies to assigned defenders. Bots already in an attacked
station sector defend in place regardless of that quota. Any friendly exported
station can raise an incident, so the response also protects outposts. There is
no server-side information access, relocation, NPC undock or new wire message.
Each fighter targets only enemies visible in its own client's current sector.

## Validation

- `dotnet build AllegianceCore.slnx --no-restore -m:1` passed: **0 warnings,
  0 errors**. Final build log: `/tmp/bot-defense-build.log`.
- **181 socket-free cases passed, 0 failures, 0 skips**, including all **13 new
  defence cases** and the existing 37 bot-fix regressions. Broader coverage
  includes target selection, bomber routing, staging, pod home selection,
  accepted-order translation, combat metrics, dodge, client events and
  decision snapshots. Log: `/tmp/bot-defense-broad-tests.log`.
- Ordinary `dotnet test` was attempted but VSTest's own TCP listener fails with
  `SocketException (13): Permission denied` before executing tests. A temporary
  reflection runner in `/tmp/bot-defense-probes/Program.cs` executed the xUnit
  facts and inline theory cases directly, without sockets. Its build also
  passed with zero warnings/errors; it is not a repository change.
- The initially missing NuGet assets were restored from the existing package
  cache. The MonoGame tool was restored from the same cache. No package versions
  or repository build policy were changed. Offline restore used
  `--source /home/domingo/.nuget/packages -p:NuGetAudit=false --force -m:1`,
  `NUGET_PACKAGES=/home/domingo/.nuget/packages`, and a temporary
  `DOTNET_CLI_HOME=/tmp/bot-defense-dotnet`.
- `git diff --check` passed. Protected production paths were checked; no
  server, transport or client source changes, commits or pushes were made.

Outside this sandbox, the new tests can be run normally with:

```sh
dotnet test tests/Allegiance.ClientCore.Tests/Allegiance.ClientCore.Tests.csproj \
  --no-restore --filter 'FullyQualifiedName~BotFixRegressionTests.Defense'
```

## Left undone

No requested implementation item is left unfinished. The live 200v200 march,
network weapon delivery/station damage, and fleet-scale decision cadence were
not exercised in this socket-restricted environment. Those remain rig
validation, including the `ALLEGIANCE_BOT_DEFEND=0` comparison. The passing
socket-free tests verify actual controller decisions and wire-applied client
state, rather than claiming a live battle outcome.
