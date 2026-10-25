# P818 — composer individual/group recipients, so radar command subjects work (HUD row 433)

> **Claude review (2026-10-24).** The captures showed every native radar bracket sitting half a quadrant down-right
> of its contact — a HUD-12 bug, not P818's: native `DrawExpandedBlip` draws each quadrant CENTRED on its exploded
> corner (`radarimage.cpp:273-276`), the port drew its top-left there. Fixed (`RadarBracketRenderer.ExpandedBlipQuadrantTopLeft`,
> test) and **all ten assets were retaken** with the same recipe after the fix (Classic group subjects 8101+8097,
> Modern group subjects 8099+8103+8097; the crops show 8097 and its neighbours with the bracket centred on the contact).

Branch `pi/p818`, base public master `041e23a0` (P805). **No commits — all changes are in the working tree.**
Report assets: `docs/reports/P818_assets/` (Classic+Modern pairs, 432 KB); raw frames, the client/server logs and the
HUD-style files that drove each run: `~/allegiance-evidence/P818/` (outside the repo).

Native reference: `~/Development/Allegiance/src/` (`WinTrek/console.cpp`, `WinTrek/radarimage.cpp`,
`clintlib/clintlib.cpp`, `FedSrv/FedSrv.CPP`). Every `file:line` below was re-grepped in this checkout at P818 start.

---

## 1. The gap this closes

Parity row **`hud | Radar command subjects`** (`WinTrek/radarimage.cpp:340`) was `partial`: `c_maskSubject` is set only
for a ship that is in the console's current **compose subject list** —

```cpp
const ShipListIGC*  psubjects;
if (bRangeLabels) psubjects = NULL;
else              psubjects = GetWindow()->GetConsoleImage()->GetSubjects();   // radarimage.cpp:570-574
...
if (psubjects && (type == OT_ship) && psubjects->find((IshipIGC*)pmodel))
    maskBrackets |= c_maskSubject;                                            // radarimage.cpp:651
```

— and that list is `m_pchsCurrent->m_shipsSubject`, non-null in **every compose state except NotComposing and Shell**
(`console.cpp:2327-2333`). The port modelled the channel recipients (Team/Everyone/Wing/FriendlySector) but had no
**individual** or **group** recipient at all, so those two lists never existed and those ships never got a bracket.

## 2. What was implemented

### 2.1 The model — native's `ChatSubject` ship list (`Rendering/ChatSubjects.cs`, new)

`ChatSubjects` is the port of native's `ChatSubject` struct (`console.cpp:66-267`): the recipient **and** its ship list
in one object, matched rule for rule.

| Native rule | Cite | Port |
|---|---|---|
| `m_chsChat(CHAT_TEAM)` — recipient + list are members that survive `QuitComposing` | `console.cpp:537,644,83-86` | `ChatSubjects` instance is a `Game1` readonly field; `_chatTarget` is now a read-only view of `Target` |
| `AddSubject` dedupes and puts the player's **own** ship first | `console.cpp:98-111` | `AddSubject(shipId, selfId)` |
| `SetRecipient(CHAT_INDIVIDUAL, oid, pship)`; no mission ⇒ Team | `console.cpp:299-309` | `SetIndividual(shipId, selfId, inMission)` |
| `SetRecipient(CHAT_GROUP, …, pships)`; 1 live ship ⇒ Individual, 0 ⇒ Team | `console.cpp:278-298` | `SetGroup(ids, selfId, inMission)` |
| `ToggleSubject` — shift-click add/remove, **the last ship can never leave** | `console.cpp:123-158` | `ToggleSubject(shipId, selfId)` |
| `Reset(bStart)` — drop ships that left the mission, re-derive Individual/Group/Team, and on a compose start turn an Individual that is *yourself or a drone* into Team | `console.cpp:227-267` | `Reset(starting, selfId, inMission, playerPiloted)` |
| `PurgeSubjects` on every `SetRecipient` | `console.cpp:88-96,274` | `PurgeSubjects()` / `SetChannel(target)` |

`inMission`/`playerPiloted` are passed in by the client (`Game1.ChatSubjects.cs`: `ShipInMission` = present in the world
and not `Unknown`; `ShipPlayerPiloted` = `PLAYERINFO.PilotType >= c_ptPlayer`, i.e. the port's stand-in for native's
`GetPilotType()`), so the model itself stays headless and unit-testable.

### 2.2 How they are picked and shown (the keys/clicks)

| Native input | Cite | Port |
|---|---|---|
| **LEFT click** on a ship ⇒ `PickShip` ⇒ `SetRecipient(CHAT_INDIVIDUAL)`; starts the `MouseCommand` compose when none is open | `console.cpp:2155-2185,1799-1812` | `Game1.MousePick.ActOnLeftPick` → `ApplyLeftPickSubjects` (the existing P587 pointer pick) |
| **SHIFT + left click** ⇒ `ToggleSubject` | `console.cpp:2167-2170` | same path, `CommandSelectionAdd` (LeftShift) held |
| **Left drag** over friendly ships ⇒ `SetRecipient(CHAT_GROUP, …, &ships)` | `console.cpp:2100-2147` | `SelectCommandRectangle` → `ApplyGroupSubjects` (the existing P784 drag) |
| Recipient bar **“Me”** column ⇒ `PickShip(own ship)` + `SetRecipient(CHAT_INDIVIDUAL, NA, own)` | `console.cpp:1144-1149` | `TryCommandBarPick` → `ApplyRecipientButton(Individual)` |
| A click that picks **no** ship quits the compose | `console.cpp:2180-2184` | `ActOnLeftPick` empty-click branch |
| The mouse handler is **not** gated by the compose state (only Shell is excluded) | `console.cpp:2036` | `Game1.FlightInput`: the command view now feeds `HandleFlightMouseButtons` unless the compose is Shell |
| Compose panel recipient box: the **picked ship's name** / the literal **“Group”** | `console.cpp:1345-1346,1381-1386` | `ChatTargetLogic.ResolvedLabel`/`ComposeLabel` (+ `Game1.Rendering.BuildChatModel` resolves the picked ship) |
| Compose **start** runs `Reset(true)` | `console.cpp:1656` | `ResetChatSubjectOnStart()` on the Enter/StartChat path |

### 2.3 What they send — the existing chat wire, no new fields (`Rendering/ChatSendPlan.cs`, new)

Native `SendChatInternal` sends **one chat per subject** for a group, first `CHAT_GROUP`, the rest `CHAT_GROUP_NOECHO`
(`console.cpp:1448-1507`, group loop `:1470-1491`), and refuses to put an *Individual to yourself* on the wire
(`clintlib.cpp:2422-2426`); the sender's own history gets the line from native's local echo
(`clintlib.cpp:2463-2470`). `SendChatLine` now builds that plan (`ChatSendPlan.Plan`) and sends exactly those messages
with the *existing* `FM_CS_CHATMESSAGE(23)` fields — `chatTarget` + `oidRecipient`; the server already rewrites
`CHAT_GROUP`/`CHAT_GROUP_NOECHO` to `CHAT_INDIVIDUAL` on `oidRecipient` (`FedSrv.CPP:3531-3536`, ported in
`Server.Lobby.SelectChatTargets` / `Server.Callbacks.SendSiteChat`). Nothing new on the wire.

The one visible-side effect: an Individual/Group line addressed to **someone else** is relayed only to that peer, so the
composer posts the sender's own copy (`ClientWorld.PostOwnChatLine`, the port of native's local `ReceiveChat` the pane
colours as an own line ×0.75, `trekigc.cpp:4736-4741`). When the player's own ship is one of the subjects, the server's
relayed copy is the one echo and no local line is added — the player always sees the line exactly once.

### 2.4 The radar (`Rendering/RadarOverlayRules.cs`, `Game1.Rendering.cs`)

* `IsSubject` gained the two list recipients: `CHAT_INDIVIDUAL`/`CHAT_GROUP` ⇒ `subjectShips.Contains(shipId)`, pure
  membership like native's `psubjects->find(pship)` (`radarimage.cpp:651`) — no side/sector condition.
* The **gate** is now native's: `subjectsApply` is true for every compose state except NotComposing and Shell (chat,
  quick, mouse-command, command) instead of only the keyboard chat (`console.cpp:2327-2333`), still only in the command
  camera (`radarimage.cpp:570-574`).
* The **list the radar reads** follows native's `m_pchsCurrent` split: the chat/quick states read `m_chsChat`
  (`_chatRecipient`), the mouse-command/command states read `m_chsCommand` — the port's existing command selection
  (`_commandee`/`_commandGroup`, P784) — so a picked command recipient is bracketed too.
* Bracket art/size unchanged: the native `subjectbmp` quadrants at native `radiusBracket = 1.05·GetScreenRadius`
  (`radarimage.cpp:307-319,341-342`; HUD-12/13).

## 3. Native-behaviour classification (design / era constraint / bug)

* **design** — the whole `ChatSubject` model: recipient + ship list, `SetRecipient` decay rules (1 ⇒ Individual,
  0 ⇒ Team, missing mission ⇒ Team), `AddSubject` ordering, the drag rectangle ⇒ Group, shift-click toggle, the
  recipient box wording (“Group”, the picked ship's name), `GetSubjects()` being alive in every compose state, and
  “a click that picks nothing quits the compose”.
* **design (guard)** — `ToggleSubject` refusing to remove the **last** ship (`console.cpp:128-133`): without it a toggle
  would silently turn a group chat into a Team chat.
* **design** — `Reset(true)` turning *yourself or a drone* into Team (`console.cpp:255-266`): “you can't chat to
  yourself”, and a drone recipient has no chat pane.
* **era constraint** — `CHAT_GROUP`/`CHAT_GROUP_NOECHO` as client-only targets that the server rewrites to
  `CHAT_INDIVIDUAL` (`FedSrv.CPP:3531-3536`): a protocol convention from the 1999 client's per-subject fan-out. The port
  keeps it verbatim instead of inventing a multi-recipient field.
* **era constraint** — the client-side echo (`clintlib.cpp:2463-2470`): the server never echoes an individual send back,
  so every client has to add its own copy. The port needs it only for the addressed-elsewhere cases (its other channels
  are relayed back to the sender).
* **era constraint** — `AddSubject`'s “Hack so that the client's ship is always at the front of the list (so that it will
  correctly echo the chat in the chat history)” (`console.cpp:104-109`): an ordering workaround, kept because the echo
  rule above reads it.
* **era constraint** — `psubjects` existing only when range labels are OFF, i.e. in the command camera
  (`radarimage.cpp:570-574`): the subject brackets share the command-view radar pass. Both HUD styles draw through the
  same `RadarBracketRenderer`, so the restriction holds in Classic and Modern alike.
* **bug (native quirk, NOT copied)** — `OnRecipientButton(c_iRecipientMe)` calls `PickShip(trekClient.GetShip())`
  *before* `StartComposing(c_csComposeChat, …)` (`console.cpp:1144-1148`). Since `PickShip` itself starts a
  `MouseCommand` compose when none is open, a native “Me” press from NotComposing lands in **MouseCommand**, not Chat —
  an artefact of the Imago #151 patch. The port keeps its compose machine's documented behaviour (`RecipientButton`
  from NotComposing starts **Chat**, `ComposeMachine`) and only takes the column's *meaning* (own ship ⇒ Individual).
* **bug (native inconsistency, NOT copied)** — `PickShip(pcluster, point)` accepts **any** visible ship as an individual
  chat recipient while the command pick `ResolvePick` filters to own-side ships (`console.cpp:1666-1696` vs
  `:1874-1877`). The port's pointer pick is own-side for both (P587), so an enemy cannot become an Individual chat
  subject. Recorded as a residual below.

## 4. Residuals (stated, not hidden)

1. **`CHAT_SHIP` (turret / child-ship recipient) is still not a compose target.** Native's `SetRecipient(CHAT_SHIP)`
   adds the source ship plus its child ships (`console.cpp:310-327`) and `CycleChatTarget` reaches it when the player has
   children (`console.cpp:2653-2659`). The port's Tab cycle has skipped `Ship` since P456 (`ChatTargetLogic`), so that
   list is never built. Out of scope here (individual/group only); the radar handles it the moment the composer can hold
   it.
2. **Enemy individual recipients are unreachable** (see the quirk above): the port's pick is own-side.
3. **The rectangle selects own-side ships only, excluding the player's own ship** (the P784 `ResolveCommandRectangle`),
   where native also accepts allies and can catch the own ship (`console.cpp:2120-2134`). With no alliance system in the
   port the ally part is moot; the self exclusion is a visible-but-harmless difference (a self subject only affects the
   local echo ordering).
4. In **Modern**, the compose line's recipient label is partly overprinted by the command pane's `WING` combo in the
   command view (pre-existing HUD-10 layout, untouched by P818); the label is fully legible in Classic. The subject
   brackets — the row's subject — are identical in both styles.

## 5. Screenshots — real window, Classic + Modern, individual + group

Produced with `scripts/live-proof.sh` (real windowed `Allegiance.ClientCore.MonoGame`, 2560×1440 presented backbuffer,
never `--capture`), a real local `Allegiance.Server` (`m2xspiderw.igc`), MCP bots
(`spawn_scenario {"size":3,"mode":"march","staging":false,"target_sector":"Plato"}`) and the new proof harness
`ALLEGIANCE_P818_PROOF=1` (`src/Allegiance.ClientCore.Game/Game1.ChatSubjectProof.cs`). The harness opens the command
view + a chat compose, then **replays a real LEFT click and a real LEFT drag through the production handler**
(`HandlePointerPickClick` with a synthesized `MouseState` aimed at a ship's own projected screen point), and captures a
real backbuffer frame per selection. `ALLEGIANCE_HUD_SETTINGS` pointed at a `hud.json` with `Style` Classic /
Modern (`~/allegiance-evidence/P818/hud-*.json`).

Commands (Classic; Modern identical with the other HUD file and `--out`):

```
ALLEGIANCE_HUD_SETTINGS=$PWD/.scratch/p818/hud-classic.json ALLEGIANCE_P818_PROOF=1 scripts/live-proof.sh \
  --art /home/domingo/Development/Artwork --auto-side 1 --client-extra '--auto-launch' \
  --bots 'spawn_scenario {"size":3,"mode":"march","staging":false,"target_sector":"Plato"}' \
  --exit-after 80 --out .scratch/p818/classic
```

The client log proves what the radar did with each selection (`[p818-proof]` lines; every image below was opened with
the read tool and described from what is visible):

| Asset | What is visible |
|---|---|
| [`classic-individual.webp`](P818_assets/classic-individual.webp) | Classic, command view (`COMMAND: THREE QUARTER`, `RECIPIENTS: 1`). The compose line reads **`[Ship 8097]`** and the chat text `regroup`. Ship **8097** (right of the Plato garrison) carries the native subject bracket (4 corner strokes); the other same-side ships (`mcp-b002-7624`, `mcp-b003-3769`) carry **none**. Log: `recipient Individual subjects [8097] … 1 subject contact(s) of 4 ship contact(s)`. |
| [`classic-individual-bracket.webp`](P818_assets/classic-individual-bracket.webp) | 3× crop of that bracket: the `subjectbmp` square around `Ship 8097` with its radar icon and hull bar. |
| [`classic-group.webp`](P818_assets/classic-group.webp) | Classic, `RECIPIENTS: 3`, compose line **`[GROUP]`** + `regroup`. **Three** ships bracketed: the top contact (under the command summary text), `Ship 8097` beside the garrison, and `mcp-b001-7626` at the bottom. Log: `recipient Group subjects [8099,8101,8097] … 3 subject contact(s) of 4`. |
| [`classic-group-bracket-a.webp`](P818_assets/classic-group-bracket-a.webp) | 3× crop: `mcp-b001-7626` bracketed. |
| [`classic-group-bracket-b.webp`](P818_assets/classic-group-bracket-b.webp) | 3× crop: `Ship 8097` bracketed (the same ship as the individual shot, now one of three group subjects). |
| [`modern-individual.webp`](P818_assets/modern-individual.webp) | Modern, `RECIPIENTS: 1`, `RADAR All`: `Ship 8097` bracketed; `mcp-b003-1676`, `mcp-b005-3028`, `mcp-b001-8532` unbracketed. Compose text `regroup` visible at the bottom (the recipient label is behind the Modern command pane's WING combo — residual 4). Log: `1 subject contact(s) of 4 ship contact(s)`. |
| [`modern-individual-bracket.webp`](P818_assets/modern-individual-bracket.webp) | 3× crop of the Modern bracket. |
| [`modern-group.webp`](P818_assets/modern-group.webp) | Modern, `RECIPIENTS: 2`: **two** brackets — `mcp-b003-1676` (top) and `Ship 8097` (beside the garrison); `[GROUP] regroup` partly visible at the bottom. Log: `recipient Group subjects [8101,8097] … 2 subject contact(s) of 4`. |
| [`modern-group-bracket-a.webp`](P818_assets/modern-group-bracket-a.webp) / [`…-b.webp`](P818_assets/modern-group-bracket-b.webp) | 3× crops of both Modern group brackets. |

Raw frames, `client.out`, `server.out`, `mcp_out.log` and the two `hud.json` files live in `~/allegiance-evidence/P818/`
(`classic/`, `modern/`).

## 6. Tests

* **New — `tests/Allegiance.ClientCore.MonoGame.Tests/P818ChatSubjectsTests.cs`** (18 cases, all headless):
  * model: `SetIndividual`/`SetGroup` decay, own-ship-first ordering, `ToggleSubject` (add/remove/last-ship guard),
    `Reset` dropping dead subjects and the self/drone ⇒ Team conversion on compose start, `SetChannel` purging;
  * wire plan: group = first `CHAT_GROUP` + rest `CHAT_GROUP_NOECHO` with one echo, `LocalOnly` for Individual-to-self,
    channel targets single-and-no-local-echo;
  * radar: `IsSubject` Individual/Group membership (and never a fabricated subject), still gated to the command camera;
  * compose label: `[GROUP]`, the picked ship's name, `[ME]` fallback;
  * **two real-TCP tests** (`P818ChatSubjectsWireTests`) driving the production `SendChatLine`
    (P757 production-seam idiom): a group recipient emits exactly `CHAT_GROUP(subject1)` + `CHAT_GROUP_NOECHO(subject2)`
    with `c_cidNone` and one local echo; an individual recipient emits one `CHAT_INDIVIDUAL` addressed to the **picked**
    ship.
* **Edited:**
  * `Hud12DataAndArtTests` — its assertion that Individual/Group contribute no subjects (the old model) now pins the
    list-membership rule instead.
  * `P757ChatPaneTests`, `P761StartChatComposeTests`, `P784ComposeTests`, `P787BShopHoverAndPickTests` — the headless
    `Game1` helpers seeded the old `_chatTarget` field; they now seed the new `_chatRecipient` (`ChatSubjects`) and read
    `.Target`. No assertion changed.
* All the above (18 + 106 affected) **pass 3× alone** (see §7).

## 7. Verification

| Gate | Command | Result |
|---|---|---|
| Whole-solution build | `~/.dotnet/dotnet build AllegianceCore.slnx` | **Build succeeded, 0 warnings, 0 errors** |
| Tools build | `tools/build-tools.sh` | `build-tools: all 76 tool projects built` (0/0) |
| AOT publish | not applicable | no `src/Allegiance.Server` / `src/Allegiance.Core` change (ClientCore + ClientCore.Game + tests only) |
| New tests ×3 alone | `dotnet test … --filter FullyQualifiedName~P818ChatSubjects` | **18/18 passed** three times (41–60 ms) |
| Affected tests ×3 alone | `… --filter Hud12DataAndArtTests\|ChatTargetLogicTests\|P784ComposeTests\|P757ChatPaneTests\|P761StartChatComposeTests\|P787BShopHoverAndPickTests` | **106/106 passed** three times (~0.9 s) |
| Fast tier (once) | `tools/run-tests.sh --tier fast` | **PASS — `fast: 10369 tests, 287.9s, PASS`** (Lobby 42; MonoGame 3439 + 3 not-executed; ClientCore 1771 + 2 quarantine; Core 5102 + 10 quarantine) |
| Parity CSVs | `python3 scripts/parity-scorecard.py` | `parity-matrix.csv: validated 670 rows`, `sim-rules-matrix.csv: validated 473 rows`; **`hud 100.00% (327/327)`**, TOTAL 99.42% (1815.332/1826.000) |
| Glossary links | `python3 tools/check-glossary-links.py` | `OK (121 distinct paths checked)` |

Nothing failed on master for these tests: the four headless-helper edits are compile-time (a missing field), and the new
test file does not compile against master at all (`ChatSubjects`/`ChatSendPlan`/`IsSubject`'s new parameters don't exist
there).

## 8. Parity rows touched

* **`hud | Radar command subjects` (row 433) → `done`.** The reason now names the model, every selection input, the
  `m_pchsCurrent` split of which list the radar reads, the send loop, and both residuals (`CHAT_SHIP`, own-side-only
  picking). This is the row the brief targets; `hud` is now 100% weighted (327/327).
* **`hud | Chat composition and recipient` (row 454)** — appended: the compose recipient is now the full `ChatSubject`
  (ship list, picked-name/Group label, one chat per subject).
* **`commander | Team chat / Compose recipient and team rendering` (row 611)** — appended: the Individual/Group ship
  lists and the per-subject `CHAT_GROUP`/`CHAT_GROUP_NOECHO` send.

`docs/parity/*.csv` written with LF endings (`grep -c $'\r'` = 0) and validated by the scorecard.

## 9. Files changed

New: `src/Allegiance.ClientCore.Game/Rendering/ChatSubjects.cs`,
`src/Allegiance.ClientCore.Game/Rendering/ChatSendPlan.cs`, `src/Allegiance.ClientCore.Game/Game1.ChatSubjects.cs`,
`src/Allegiance.ClientCore.Game/Game1.ChatSubjectProof.cs`,
`tests/Allegiance.ClientCore.MonoGame.Tests/P818ChatSubjectsTests.cs`, `docs/reports/P818_RADAR_COMMAND_SUBJECTS.md`,
`docs/reports/P818_assets/`.

Modified: `Game1.cs` (`_chatRecipient`), `Game1.FlightInput.cs` (Tab cycle purge, compose-start `Reset(true)`, the
compose-independent command-view mouse gate, proof hook), `Game1.MousePick.cs` (pick ⇒ Individual / shift ⇒ toggle /
empty click quits), `Game1.Commanding.cs` (rectangle ⇒ Group, recipient bar ⇒ channel/Me), `Game1.Session.cs`
(`SendChatLine` = native's send loop), `Game1.StarmapClick.cs`, `Game1.Rendering.cs` (radar subject list/target/gate +
compose label), `Rendering/RadarOverlayRules.cs`, `Rendering/ChatTargetLogic.cs`,
`src/Allegiance.ClientCore/State/ClientWorld.cs` (`PostOwnChatLine`), `GLOSSARY.md` (new “Chat composer / compose
recipients” entry), `docs/parity/parity-matrix.csv`, and the four headless-helper test files.

## 10. Unfinished

* `CHAT_SHIP` (turret/child-ship) recipient — not ported (see §4.1); needs the Tab cycle and the turret child-ship list.
* An enemy cannot be an Individual chat subject in the port (own-side-only pointer pick, §4.2/§4.3) — that is the P587
  pick's rule, not this task's.
* Modern's compose recipient label is partially overprinted by the command pane art in the command view (pre-existing
  HUD-10 layout, §4.4).
* The proof harness (`ALLEGIANCE_P818_PROOF`) is kept for re-capturing, exactly like the P624/P626 proofs.
