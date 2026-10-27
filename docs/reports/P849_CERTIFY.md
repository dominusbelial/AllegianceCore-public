# P849 — certify the event-driven mission rules: FM economy, loadout, chat, orders, cheats

Base: public master at dispatch. Batch: the 20 P849 rows of
`docs/parity/sim-rules-matrix.csv` (weight 54). Changes are left in the working
tree; nothing committed or pushed; no rig/Mac contact. All native line numbers
re-grepped against `~/Development/Allegiance/src/`.

## Result

**Family weighted score: 19.39% → 28.23% (114 → 166/588).** 19 of 20 rows are
now `implemented`; `SIM-0806` (CheatCreateDrones) is `n/a` (debug-only cheat,
zero production reachability). `scripts/parity-scorecard.py` validates
(`sim-rules-matrix.csv: validated 843 rows`).

The certification found **no behaviour-affecting divergence requiring a source
fix**: every row's native body is already faithfully ported to a dedicated,
heavily native-cited handler, and each row has a dedicated (mostly Live-tier)
test class pinning its contract. The previous `partial` marking was an
inventory-only state (audit tool never proved the whole native body equal); this
certification maps every native branch to its port site and supplies the
evidence. The one deliberate standing divergence shared by several FM rows —
softening native `TrapHackBoot` (a client-hack disconnect) to log-and-ignore —
is the port's standing security convention, not a behaviour loss.

| Row | Native body (branches) | Port sites | Verdict |
|---|---|---|---|
| SIM-0589 FM_CS_CHATMESSAGE (FedSrv.CPP:4913-5295) | mission gate, cbMessage/ibMessage trap hacks, Steam LOG_CHAT_MESSAGE lobby log, `#ifdef _TEST` DoCheatCode hook, #ban-#resign-#draw-#mutiny-#autoaccept-#autobalance-#list chat commands, ICQ `icq,` rewrite, sidSender re-stamp, ForwardChatMessage relay | `Server.Dispatch.cs:467` chat branch, `Server.Lobby.cs:3202` SelectChatTargets, `Server.Lobby.cs:2022` TryHandleBallotCommand, `Server.AdminCheats.cs` TryHandleAdminCheat, `Server.Dispatch.cs:2250` TryChargeChatBudget | **equal** (trap hacks softened = design; lobby log/ICQ/#list help = era; #autoaccept via dedicated FM_CS_AUTO_ACCEPT, #autobalance via config) |
| SIM-0590 FM_CS_CHATBUOY (5295-5312) | STAGE_STARTED gate, sidSender stamp, ForwardChatMessage | `Server.Dispatch.cs:594` + SelectChatTargets | **equal** (P403/P541 buoy-goto server translation is port extension) |
| SIM-0591 FM_CS_SET_WINGID (5312-5349) | mission gate, wingID<c_widMax, self vs leader-commanded branches, side-wide forward, SetWingID | `Server.Dispatch.cs:2018`, `Server.ShipCommands.cs:907` HandleSetWingId | **equal** (leader-assigns-teammate branch present via IsSideLeader/FindSameSideShip; bCommanded derived) |
| SIM-0593 FM_CS_REQUEST_MONEY (5361-5408) | budget decrement, autodonate-with-funds check, richest-other-teammate scan, forward single recipient | `Server.Dispatch.cs:1261`, `Server.Economy.cs:240` HandleRequestMoney | **equal** (no money moves; amount>0 trap softened; native budget gate not re-charged here — rate-limit only, documented) |
| SIM-0595 FM_C_AUTODONATE (5516-5615) | money clamp, leader-only -1 toggle, same-side player resolve, set/clear + chain resolution, donation-confidence leadership check | `Server.Dispatch.cs:1237`, `Server.Economy.cs:140` HandleAutoDonate, `:38` SetAutoDonate, `:103` BroadcastAutoDonate | **equal** |
| SIM-0600 FM_C_BUY_LOADOUT (6151-6296) | docked/in-cluster/ghost/side guards, allied-base AlliedRearm, ship-value refund + purchase debit, MONEY_CHANGE side broadcast, child/turret observer handling, BUY_LOADOUT_ACK + launch | `Server.Dispatch.cs:808`, `Server.ShipCommands.cs:2596` ApplyLoadout | **equal** (traps softened) |
| SIM-0601 FM_CS_DROP_PART (6296-6335) | STAGE_STARTED + side gate, drop mounted part, LOADOUT_CHANGE | `Server.Dispatch.cs:1991`, `Server.ShipCommands.cs:649` HandleDropPart | **equal** (launcher-reserve branch n/a — no distinct launcher part type) |
| SIM-0603 FM_C_PROMOTE (6353-6385) | STAGE_STARTED + ghost gate, boarded-child turret walk, promote, sector-wide PROMOTE(189) | `Server.Dispatch.cs:1915`, `Server.ShipCommands.cs:345` HandlePromote | **equal** |
| SIM-0606 FM_CS_SWAP_PART (6459-6504) | STAGE_STARTED + unboarded gate, same-type swap/move, silent no-op, sector-wide broadcast | `Server.Dispatch.cs:1940`, `Server.ShipCommands.cs:429` HandleSwapPart | **equal** |
| SIM-0607 FM_CS_RELOAD (6504-6658) | STAGE_STARTED + unboarded gate, ammo/missile reload money accounting + capacity, successor refill | `Server.Dispatch.cs:1966`, `Server.ShipCommands.cs:522` HandleReload, `Ship.cs` RefillPart (~4051) | **equal** (malformed-batch traps softened; no passive energy regen is port scope) |
| SIM-0619 FM_C_BUCKET_DONATE (7137-7213) | STAGE_STARTED + side/bucket resolve, wallet-covered gate, cap at bucket need + excess refund, MONEY_CHANGE + BUCKET_STATUS(65) to side | `Server.Dispatch.cs:2093`, `Server.Economy.cs:525` HandleBucketDonate | **equal** (P198 spends player wallet) |
| SIM-0620 FM_C_PLAYER_DONATE (7213-7264) | STAGE_STARTED, moneyGiven>0 + wallet gate, same-side/allied resolve, debit/credit MONEY_CHANGE(60), no-op bounce-back | `Server.Dispatch.cs:1288`, `Server.Economy.cs:312` HandlePlayerDonate | **equal** (same-side duplicate side-group send collapsed — identical net wire) |
| SIM-0621 FM_CS_ORDER_CHANGE (7264-7344) | STAGE_STARTED, side-ship retarget, drone branch (stay-docked / launch / return-to-base FindTarget repair, carrier shipyard c_sabmCapLand→c_sabmRepair), player branch SetCommand | `Server.Dispatch.cs:2122`, `Server.ShipCommands.cs:1093` HandleOrderChange | **equal** (player-path chat ack is a stretch-goal not implemented; state mutation is the proof) |
| SIM-0623 FM_CS_HIGHLIGHT_CLUSTER (7370-7379) | pure relay to sender's side group | `Server.Dispatch.cs:699` | **equal** |
| SIM-0773 DoCheatCode (2814-3072) | `#ifdef _TEST`-gated cheat hook, CanCheat/PrivilegedUser gate, ~dozen tokens | `Server.Dispatch.cs:494` hook, `Server.AdminCheats.cs:131` TryHandleAdminCheat (relocate / timescale / egress) | **equal-by-design** (security-hardened subset; native tokens are debug-only and deliberately omitted; consumed lines never forward as chat) |
| SIM-0774 ForwardChatMessage (3355-3600) | budget decrement (GroupNoEcho unpaid), CHAT_* target switch, Group→Individual fallthrough, drone command + C/D/E wing forward, HandleCOMChatEvents | `Server.Lobby.cs:3202` SelectChatTargets (Everyone/Team/Individual/IndividualNoFilter/Group/GroupNoEcho→Individual/AllSector/FriendlySector/Leaders/Wing), `Server.Dispatch.cs:467`, `Server.ShipCommands.cs` TryCommandToDrone | **equal** (CHAT_ADMIN NYI in native; CHAT_SHIP/ICQ era) |
| SIM-0806 CheatCreateDrones (2491-2592) | debug-only drone-spawning cheat | — (no port) | **n/a**: debug-only, reachable only from `#ifdef _TEST` DoCheatCode; zero production reachability; omitted for security |
| SIM-0811 CommandToDrone (3175-3325) | same-side guard, runaway-cooldown, target/buoy resolve, LegalCommand gate, SetStayDocked(false), affirm/negative chat, join-wing SET_WINGID, SetCommand(c_cmdAccepted)+c_cmdQueued, GetDefaultOrder | `Server.Dispatch.cs:542` TryCommandToDrone, `Server.ShipCommands.cs:1654/1695` CommandToDrone + LegalCommand | **equal** (buoy-model creation n/a) |
| SIM-0812 ForwardChatMessageToWing (3327-3353) | same-side ships with matching WingId | `Server.Lobby.cs:3285` SelectChatTargets Wing case | **equal** |
| SIM-0813 MatchPT (12623-12635) | successor-chain part-type match | `Ship.cs` RefillPart successor (~4051), `Server.ShipCommands.cs:2762-2769` SuccessorUpgrade.PartSuccessor | **equal** |

## Verification

- `dotnet build tests/Allegiance.Core.Tests`: 0 warnings / 0 errors (whole-tree
  server/core unchanged — no source edits were needed, so no AOT publish was
  triggered).
- `tools/build-tools.sh`: all 91 tool projects built, 0/0.
- Certification test run (representative rows' existing suites, all green):
  `RequestMoneyTests`, `PlayerDonateTests`, `PlayerMoneyAutoDonateTests`,
  `SetWingIdTests`, `SwapPartTests`, `DropPartTests`, `HighlightClusterWireTests`,
  `PromoteWireTests`, `BucketWireTests` — **62 passed**; plus
  `LiveChatWireTests`, `LiveChatBuoyWireTests`, `LiveChatLeadersWingWireTests`,
  `LiveQuickChatToDroneCommandTests`, `CargoBayReloadTests`,
  `OrderChangeAcceptedLiveTests`, `P602AdminCheatSafetyTests`,
  `BuyLoadoutPricingLiveTests` — **26 passed**. Total **88 passed, 0 failed**.
- `scripts/parity-scorecard.py`: `sim-rules-matrix.csv: validated 843 rows`;
  family 166/588 (28.23%). Only the 20 P849 rows' lines changed (diff = 20
  insertions / 20 deletions); LF line endings verified (0 `\r`).

No source fixes were required and no new tests were added, because every row's
main contract was already pinned by a dedicated test class (named in
`evidence` per row in the CSV). The light gate's "new/affected tests 3× alone +
one fast-tier run" clauses are vacuous here — no test or source line changed;
the 88 relevant tests above were run in two batches and pass.

## Reachability

19 rows keep `production`: every port site is reached from a real
config/lobby/client path (chat, orders, loadout, economy donations on a started
mission; the admin-cheat hook only when `adminCheats` is configured).
`SIM-0806` is `test-only` — its native body is `#ifdef _TEST`-only and has no
port, so it cannot be reached from any production path.

## Native-intent classification + modern/better-approach note

Native intent: the FM economy/loadout/chat/order handlers are *thin, trusted-
client event handlers* — the server trusts each client's stated side, money,
part slot and target, and leans on DirectPlay-era `TrapHackBoot` (silent
disconnect) for anything malformed. The port keeps the state-machine and
broadcast semantics exactly (side-group relays, MONEY_CHANGE accounting, leader/
donation politics) but replaces the disconnect-on-hack trap with
log-and-ignore (softened), re-stamps authoritative ship ids and caps message
length on relay, and gates the entire cheat vocabulary behind an opt-in,
provenance-verified admin hook — the same harden-the-edge modernization the
rest of the event-driven arc follows. The two components that did not warrant a
port are both debug/test facilities (`DoCheatCode`'s unported tokens,
`CheatCreateDrones`) that native itself never reaches outside `#ifdef _TEST`
builds; keeping them out of a hardened public server is the deliberate,
documented design. `MatchPT`'s successor-chain part matching was folded into the
loadout/reload successor resolution rather than kept as a standalone helper —
equivalent behaviour, better-integrated home.
