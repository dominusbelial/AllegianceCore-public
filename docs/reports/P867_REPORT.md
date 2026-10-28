# P867 — OnAppMessage per-case certification

SIM-0584 is implemented as a completed **case audit with explicit classifications**, not a claim that the managed server reproduces every legacy transport detail. The [case table](../parity/p867-onappmessage.csv) covers all **49 active FM_C/FM_CS labels** in `FedSrv/FedSrv.CPP:4798-7827`. Each entry records sender, stage, ownership and TrapHack guards; effects and notifications; current port dispatch/handler citations; equal/classified result; narrower certification row; and tests. Fourteen cases are equal under the shared transport profile below; 35 include an explicit design, era-constraint or native-bug classification. Existing narrower tests are reused where permitted by the brief.

The commented-out `FM_C_BANDWIDTH` label at `FedSrv.CPP:7731-7740` is disabled native code, not a missing implementation. PLAYER_READY and FORCE_TEAM_READY are distinct labels sharing one body. No native switch label is silently omitted.

Work stays in the working tree. No commits, pushes, remote hosts, rig access, synthetic load, new service/configuration/environment variable, or media were involved. Broadcast construction and the parallel P863 lane's files were not edited.

## Implementation and branch evidence

The [new dispatcher tests](../../tests/Allegiance.Core.Tests/P867DispatchCertificationTests.cs) exercise real inbound packet decoding and server dispatch, asserting state changes and exact recipient sets through the existing send-capture seam. They contain 51 argument cases. Their isolated worlds include own-side, enemy-side, team-lobby and different-mission peers, so recipient assertions discriminate between the native groups. The inventory checker is **not** behavioral proof; it only validates native case coverage, classifications, source citations and narrower-row references.

| Native branch | Change | New test methods (P867DispatchCertificationTests) |
|---|---|---|
| `FedSrv.CPP:4918`, `5127-5230` | Restore mission gate and owner lobby ban, started leader autoaccept, all four owner autobalance settings and private list reply. Settings changes announce the current MISSIONDEF; system replies have sender NA. | `Chat_from_a_ship_without_a_mission_has_no_effect_or_relay`, `Ban_chat_is_owner_only_and_removes_only_a_named_lobby_player`, `Autoaccept_chat_has_native_stage_and_leader_gates`, `Autobalance_chat_updates_policy_and_advertisement`, `Autobalance_and_list_respect_native_sender_and_stage_gates` |
| `Inc/Messages.h:311-317`, `FedSrv.CPP:5202,5219` | MISSIONDEF encodes/decodes its six side-tail arrays at native offsets 826–861. Current player counts, autoaccept, ready, forced-ready, active and alliance values are sent; current lock/AET bits accompany the params snapshot. Both autoaccept toggle directions are observable. | `Autoaccept_chat_announces_the_true_tail_including_a_toggle_back_on` (independent byte offsets and decode/re-encode) |
| `FedSrv.CPP:5323-5324` | Keep only native's upper wing bound: negative/no-wing values are accepted. Range validation precedes target resolution. | `Set_wing_uses_the_native_upper_bound_only` |
| `FedSrv.CPP:6057-6082` | Both older and newer version refusals set `fRetry=false`. | `Version_mismatch_is_not_retryable` |
| `FedSrv.CPP:6102,6148` | Team-lobby treasure acknowledgement consumes its offer without mounting or dropping it. | `Team_lobby_treasure_ack_clears_offer_without_mount_or_drop` |
| `FedSrv.CPP:6374-6377` | Promotion uses flying-sector recipients when flying, own-side recipients when docked. | `Promote_routes_by_flying_or_docked_state` |
| `FedSrv.CPP:6388-6389,6405-6409` | Pregame boarding refusal is silent; a ghost parent cannot be boarded. A failed actual attempt still sends requester-only BOARD_NACK. | `Board_ship_before_start_is_silent_but_missing_parent_after_start_nacks`, `Boarding_a_ghost_parent_nacks_without_changing_seats` |
| `FedSrv.CPP:6429-6446` | VIEW_CLUSTER uses actual SeenBySide, not ownership. Resolve NPC and static targets as well as player peers. | `View_cluster_uses_actual_visibility_instead_of_ownership` |
| `FedSrv.CPP:6733-6744` | Recheck an accepted position request before removing it; refusal preserves it. Rejection removes the player's current request even if its destination changed. | `Position_ack_rechecks_before_removing_pending_request`, `Position_ack_rejection_clears_a_request_that_moved_to_another_side` |
| `FedSrv.CPP:6920-6930,6940-6945` | Retain native leader-before-side-match guard order and locked-open gates for AUTO_ACCEPT/LOCK_LOBBY. | `Locked_open_games_ignore_autoaccept_and_lock_lobby`; existing leader/forged-side tests remain evidence |
| `FedSrv.CPP:6972-6982`, `fsmission.cpp:5069-5118` | RANDOMIZE_TEAMS balances only in 0x7ffe mode; otherwise disable autoaccept and flush non-leaders to the team lobby, retaining leaders and ignoring LockSides. MISSIONPARAMS/CHAT balance changes reach the live mode consumer. | `Randomize_flushes_non_leaders_and_disables_autoaccept_in_non_balance_mode`, `Mission_params_balance_mode_controls_the_next_randomize_request`; existing RandomizeTeamsLiveTests cover balance/owner/stage/multi-mission branches |
| `FedSrv.CPP:7017-7025` | PLAYER_READY reaches the whole mission, including enemies and the team lobby, without reaching another mission. | `Player_ready_reaches_enemy_and_lobby_but_never_another_mission` |
| `FedSrv.CPP:7063-7077` | Teleport accepts a restart-capable allied base, continuing to refuse an enemy base. | `Teleport_accepts_an_allied_restart_station_and_refuses_an_enemy` |
| `FedSrv.CPP:7290-7338` | Drone stay/launch/return branches send native acknowledgements; return announces to its side. Carrier return prefers a shipyard, falls back to repair, and searches across clusters. Generic current/accepted player slots resolve static targets across the mission and reject team-lobby mutation. | `Drone_orders_acknowledge_stay_launch_and_return`, `Return_to_base_selects_carrier_shipyard_or_repair_across_clusters`, `Player_command_resolves_remote_static_targets_for_current_and_accepted_slots` |
| `FedSrv.CPP:7370-7376` | HIGHLIGHT_CLUSTER is scoped to the sender's own mission and side, with no invented stage/cluster gate. | `Highlight_is_scoped_to_the_senders_mission_and_side` |
| `FedSrv.CPP:7597-7622`, `fsmission.cpp:4375-4432` | Manual START_GAME calls native's balance-only FAllReady, including min/max/imbalance. Side Ready flags do not replace it: native returns before its following ready loop. | `Start_game_uses_native_balance_policy`; updated live countdown test verifies balanced unready sides can start |
| `FedSrv.CPP:7717-7721` | A carried flag **or artifact** blocks ripcord, including cancellation, before the hull gate. | `Flag_or_artifact_carrier_cannot_activate_or_cancel_ripcord` |
| `FedSrv.CPP:7749-7765,7794-7808` | Include the requester itself in a full ping roster, preserving debounce and marker-only replies. | `Ping_roster_includes_requester_and_debounces_to_marker` |
| `FedSrv.CPP:7811-7814` | Unsupported messages from a live player emit the existing rate-limited structured TrapHack boot-intent event while keeping the port's log-and-ignore policy. | `Unknown_message_from_player_emits_the_native_trap_record` |

Four existing fixture expectations were corrected to exercise native behavior: manual START_GAME no longer expects unready flags to veto a balanced game; the drone “no station” fixture now removes stations from **all** clusters, because native searches any cluster; P740 requires the requester's own PINGDATA entry; and the Over-stage boarding fixture proves silent refusal using a following PING in the same ordered packet. The last fixture keeps its positive Started-stage boarding control and no-parent assertion. No production deadline was loosened.

## Shared preamble, default and epilogue

- `FedSrv.CPP:4810-4828`: native Windows performance counters, one-second DirectPlay queue sampling, debug timers and AGC telemetry are **era constraints**. ServerHost retains managed transport receive counters/trace and transport activity/liveness. Its packet counters count transport packets rather than pretending native's callback-per-message diagnostic counts are identical. These metrics do not change game rules.
- `FedSrv.CPP:4830-4848`: player/mission lookup, duplicate-logon protection and unknown-sender behavior are reviewed separately from each case. **Design:** the port has explicit validated pending side selection, pre-logon PING/REQPINGDATA probes and authenticated showcase controls. Per-case ship/mission checks protect gameplay handlers; native's AGC unknown-player event is replaced by bounded managed refusal logs. A native assertion does not crash the managed server on duplicate logon. TCP activity tracking replaces `ResetAbsentCount`.
- **Design:** framing/CRC and bounded decode, re-encoding fixed messages, sender-ID correction, finite-value checks, reply/relay budgets, single-player pause and scripted flight are established managed-protocol defenses/extensions. These apply in addition to the per-case native conditions, and are not presented as native branches.
- `FedSrv.CPP:7811-7814`: native default is TrapHackBoot(false). The port now records that same failed check for an existing player, and rate-limits it. **Design:** P855's established soft-boot policy logs and ignores rather than disconnecting. Other TrapHackBoot/BootNoPlayer sites use that shared policy; native memory-layout checks become bounded managed decoding.
- `FedSrv.CPP:7819-7827`: native asserts its shared outbox is empty, stops its timer and returns S_OK. **Design/era constraint:** this dispatcher publishes explicit recipient packets rather than sharing an ambiguous mutable native outbox; exceptions/refusals cannot flush a previous message to a new audience. COM HRESULT/debug timing has no managed gameplay counterpart.

Specific retained differences are visible **per case** in the CSV, including client-authoritative missile batches versus server-derived launches, server-owned player command queues, client-driven spectator view versus native SetCluster subscription, assembled mission topology/growth limits, and admission without native banned-side/last-side masks. WOPR bot-auth takeover/money-grab/zero-countdown branches, Zone/Steam/SQL/ICQ plumbing and AGC are explicitly era-classified. Native missing-pointer dereferences, the BOARD_NACK no-assignment typo, alliance permission gaps and DELPOSITIONREQ's sender/target confusion are classified as native bugs with the port behavior identified. They are not mislabeled “equal.”

No unrelated partial helper row was upgraded. In particular SIM-0608's broader departure-helper certification remains partial, and no test-only/config-only row was promoted to production reachability by this audit.

## Verification

All required checks and failure rechecks are complete. The final solution build has **0 warnings / 0 errors**, all **91 standalone tool projects** build, and the Release linux-x64 self-contained **NativeAOT publish passes without IL2026/IL3050 warnings**. The one Fast sweep recorded 11,320 passed, 3 failed and 3 optional skips in 336.7 s. All 51 new cases passed. The two deterministic old fixture expectations above failed 3/3 before correction and passed 3/3 afterward. The untouched HUD zero-allocation method observed 760 bytes in the sweep and passed 3/3 exact-method invocations with source and limit unchanged; its root cause is unproven and it is ledgered under the lane rule. Both Fast timing offenders were the obsolete boarding NACK wait and its runner retry; the corrected ordered-marker fixture is below the Fast limit. No second Fast sweep was run. See [the flake/fixture ledger](../TEST_FLAKES.md). Raw build/test logs and TRX are retained under [ignored local evidence](../../.scratch/p867/) because this report links to them; no raw archives or generated binaries are added to report assets. The final small JSON summary is saved beside the scorecards.

| Check | Result |
|---|---|
| New dispatcher tests | 28 methods / 51 argument cases, each method run alone 3×: **153 case passes**; final notification-field followup also passes the entire 51-case class 3× |
| Existing affected tests | **16 classes, 318 case passes** over 3 class invocations each; ballot notification followup 3×; both corrected fixture methods pass 3× |
| Required showcase cohort | **4/5 passed**; run 5 failed the second-match “lifepod observed by its controller” wait after 437.7 s |
| Showcase failure recheck | **3/3 exact-method invocations pass**, unchanged P730 source/assertions/deadlines/settings; **7/8 overall** |
| Fast tier | Exactly **one** full sweep; **11,320 pass / 3 fail / 3 optional skips**; every failure has 3 exact-method rechecks and a ledger entry |
| Structural checks | Case inventory/citations, parity scorecard, glossary paths, LF CSVs and `git diff --check` pass; added-code privacy/secret-pattern scan passes; no new media |

The showcase observation is recorded under the lane's isolated-recheck rule, with its cause **unproven**. It completed match 1 and continued running match 2 before the controller-pod assertion failed. For baseline context, the existing ledger records **7/8** on pre-P867 merge `47d1ac8a`, **8/8** after the later select-and-kill-lock fixture correction, and **5/5** in P832g. The select/kill fix is already present in this checkout; the recurrence does not establish the same root cause. These historical cohorts are not a paired experiment at base `b37b4efe`. No P730 or bot-strategy code was changed to obtain the rechecks.

The [small verification receipt](P867_assets/verification.json) retains every gate result, new-test counts, failure rechecks, measured host loads and all eight showcase runs, including per-match shots/hits/kills/pods/mining/research progress. Builds use `~/.dotnet/dotnet`. Scratch-only `TMPDIR`/`XDG_CONFIG_HOME` and a redirected NuGet HTTP cache keep environment writes inside the worktree; package auditing was not disabled. The logical `/proc` TMPDIR alias avoids the already-ledgered external-install ancestor/local.env issue. The final corrected-fixture checks initially used separate scratch outputs while the showcase process retained its loaded assemblies; the final whole-solution build also compiled those corrected fixtures in the normal outputs.

The before/after scorecards validate 684 master rows and 851 simulation rows. [Before](P867_assets/scorecard-before.txt), [after](P867_assets/scorecard-after.txt):

| Scorecard | Before | After |
|---|---:|---:|
| Simulation weighted total | 94.80% (1804/1903) | 94.90% (1806/1903) |
| Simulation completed/eligible | 640/687 (93.16%) | 641/687 (93.30%) |
| FedSrv event-driven weighted | 85.57% (516/603) | 85.90% (518/603) |
| Master parity weighted | 99.91% (1856.332/1858) | unchanged |

The +2 weight is SIM-0584 alone. Existing narrower rows receive updated citations/evidence where a bug was corrected; their statuses and reachability are retained.

| Row | Native cite | Before → after | Tests |
|---|---|---|---|
| SIM-0584 | `FedSrv/FedSrv.CPP:4798-7827` | partial → implemented (49 equal-or-classified cases) | `P867DispatchCertificationTests` (51 cases); existing SIM-0585–0633 tests named per case; `tools/p867-dispatch-audit.py` validates inventory/citations only |

**Native-intent classification:** the gameplay guards, effects and audiences changed above are native design rules; their missing/stale port implementations were port bugs. Legacy platform/authentication branches are era constraints; retained authority/admission/view/topology differences are explicit port design classifications. Native bugs are identified individually in the case table. No whole switch is marked n/a.

**Modern/better approach:** keep the authoritative simulation, bounded typed packets and signed identity, while expressing native guards and recipient groups in shared server state. Preserve this explicit case ledger and executable branch tests so a future transport or spectator redesign cannot silently turn a known departure into an equality claim. The table is intentionally more precise than a single dispatch-coverage percentage.
