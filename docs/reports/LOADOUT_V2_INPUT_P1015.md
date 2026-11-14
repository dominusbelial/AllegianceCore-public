# P1015 — Loadout V2: input (mouse, keyboard, controller)

With `visuals.hangar.loadoutV2` on, the one-screen loadout is now playable by
mouse, keyboard and controller, and only its own drawn rectangles take input.
With the flag off (the default) the old loadout's input is unchanged. Changes
are in the working tree on `claude/p1015` (base `30e17489`); nothing is
committed or pushed.

**Three things need a decision** — see [Open decisions](#open-decisions):
plain `1`/`2` versus native's custom-loadout restore, Enter versus chat, and
the two controller buttons that could not follow the brief literally.

## Finding

P1014 drew the V2 screen but left input on the old screen: the old hover bands
moved the old shop cursor under the V2 picture, touch taps hit the old row
bands, and the old Tab / arrows / Space walked the hidden three-page shop.

## Root cause

All three old input paths were keyed on "docked, loadout view" only, never on
which screen was drawn (paths under `src/Allegiance.ClientCore.Game/`):

| Old path | Where (before this change) |
| --- | --- |
| Mouse hover bands → `shop.MoveCursor` | `Game1.Panes.cs:739-751` `HandleShopMouseHover` (`DockedTouchZones.RowIndex`) |
| Touch tabs / launch / undock / row bands | `Game1.Panes.cs:833-881` in `HandleDockedTouch` |
| Shop keys and pad (cursor, tab, select, back, Q/E) | `Game1.Panes.cs:267-331` in `HandleDockedScreen` |
| V2 hover read the raw mouse with no gate | `Game1.LoadoutV2.cs:94-96` |

## Fix

- **`Rendering/LoadoutV2Input.cs` (new, pure)** — the input router. Mouse,
  key-table verbs and pad buttons go in; `LoadoutV2Model` actions come out
  (`SelectHull`, `NextHull`, `PreviousHull`, `OpenSlotFor`, `CloseSlot`,
  `ChooseOffer`, `ClearSlot`, `ApplyDefault`, `Revert`). It holds only view
  state: the focus column (hulls / slots / buttons), the focused rows, three
  scroll offsets and the hover. Buy, Launch and Back are returned as requests.
- **`Game1.LoadoutV2Input.cs` (new partial)** — reads the devices, applies the
  gates, and forwards the requests to the old screen's own `BuyFromHangar`,
  `LaunchFromHangar` and back-to-hangar paths. No wire change.
- **`Game1.Panes.cs`** — while the V2 screen is the one drawn
  (`LoadoutV2Shown`), the old hover bands, the old touch rows and the old shop
  keys are skipped. Undock (`U`) and the custom-loadout digits still run.
- **`Game1.FlightInput.cs`** — Escape closes an open V2 picker before it leaves
  the screen (one added line in the existing Escape chain).
- **`Game1.LoadoutV2.cs`** — three call sites changed: the presentation takes
  the router (focus) and the hint, the layout takes the router's scroll
  offsets, and the renderer's hover comes from the gated router.
- **`Rendering/LoadoutV2Presentation.cs`** — optional router parameter (focus,
  open picker, button focus, hint). P1014 callers without it behave as before.
- **`Rendering/LoadoutV2Renderer.cs`** — footer hint text and button focus
  only, as the brief allowed.
- **`Rendering/InputActionCatalog.cs`** — three new rebindable actions.

### The input map

Every key is a key-table action, so all of it is rebindable in the F6 screen.

| Input | Action | Key-table action |
| --- | --- | --- |
| `1` / `2` | focus hull column / slot column | `LoadoutFocusHulls`, `LoadoutFocusSlots` (new) |
| Tab · pad X | next focus: hulls → slots → buttons | `ShopTabSwitch` (existing) |
| Up / Down · D-pad | move the focus (inside the picker while one is open) | `MenuUp`, `MenuDown` |
| Left / Right · D-pad | previous / next hull; on the button row they walk the buttons | `MenuLeft`, `MenuRight` |
| Q / E · pad shoulders | previous / next hull, from anywhere | `ShopCycleHullPrev`, `ShopCycleHullNext` |
| Space · Enter · pad A | select hull / open slot / choose offer / press button | `MenuSelect`, `MenuConfirm` |
| Esc | close the picker; with none open, back to the hangar | existing Escape chain |
| B · pad B | close the picker; with none open, back to the hangar | `MenuBack` |
| `D` | default loadout | `LoadoutDefault` (new) |
| L · pad Y | launch | `ShopLaunch` |
| U | undock without buying | `ShopUndock` (unchanged code path) |
| Ctrl+0..9 / 0..9 | save / restore a custom loadout | native raw digits, as before |
| Mouse | hover; click hull, slot (toggle), offer, Default / Revert / Buy / Launch | — |
| Wheel | scrolls the column under the pointer (hulls, slots or picker) | — |
| Touch tap | same targets as a click | — |

The footer hint is built from the live table (`LoadoutV2Input.Hint`), so a
rebind shows immediately. It reads, on a fresh profile:
`1/2 hulls/slots | Tab next | Up/Down move | Left/Right hull | Space choose | D default | B back | L launch`.

Scrolling: the three offsets are separate, and the router stores the values
`LoadoutV2Layout` clamps, so wheeling past an end stores no hidden travel.
Wheeling the slot column past the range that keeps an open picker's owner in
view closes the picker first; if the closed list has nowhere further to
scroll, the picker stays open. Keyboard focus scrolls its row into view.

### Gating (P1006)

`LoadoutV2InputLive` is false — nothing reacts and the hover is dropped — while
any of these holds: command camera, investment or teleport pane, chat entry
(`composing` and `_chatActive`), quick chat, help, main menu, key-binding
screen, graphics settings, credits, player pings, team pane, either quit
confirmation, or a launch already sent. This is the union of the gates the old
loadout's keys, hover and touch used. The router keeps observing held keys,
the button and the wheel while gated, so nothing held fires when a gate lifts.

## Native citations

Re-grepped in the native source before citing (`WinTrek/load.cpp`):

| Rule | Native |
| --- | --- |
| Slot click opens that slot's part menu | `:244-252` `Slot::Button`, `:1380-1384` `SlotClicked` → `BuyPartMenu` |
| Menu pick mounts or empties, then closes | `:1863-1903` `OnMenuCommand`; empty item `:1704` |
| Next / Previous hull | `:1568-1637` `GetNextHullType`, `NextHull`, `OnButtonNext/Previous` |
| Default | `:1639-1648` `OnButtonDefault` |
| Launch / Buy / Back | `:1498-1509`, `:1511-1531`, `:1533-1538` |
| Mouse entering a slot cues `mouseoverSound` | `:233-237` |
| Custom loadouts, and no keys while the console takes keystrokes | `:2219-2255` (guard `:2221`) |
| Command view replaces the hangar/loadout screen | `WinTrek/WinTrek.cpp:6706-6740` |

Native's loadout is mouse-driven; its only key handler is the custom-loadout
digits. Keyboard and pad navigation, the wheel and Revert are port additions.
Parity rows: `docs/parity/p1015-loadout-input.csv` (9 rows, validated; 8
implemented, 1 partial — the digit conflict below).

## Open decisions

1. **Plain `1` / `2` versus native restore.** Native restores custom loadout N
   on a plain digit (`load.cpp:2236-2247`). The brief assigns plain `1`/`2` to
   column focus. On the V2 screen a plain digit bound to a focus action now
   focuses and does **not** restore; `0` and `3`–`9` still restore, Ctrl+0..9
   still saves all ten, and rebinding the two focus actions gives the native
   restore back on `1`/`2`. This is the one behaviour deviation, recorded as
   `partial` in the parity file. If native wins, the fix is to change the two
   default keys in `InputActionCatalog.cs`.
2. **Enter.** Enter is `StartChat` in both key layouts and chat is handled
   before the docked screen (`Game1.FlightInput.cs:1168`), so Enter opens chat
   on the V2 screen exactly as on the old one. The router accepts
   `MenuConfirm` (Enter) alongside `MenuSelect` (Space), but in practice Space
   is the choose key; the hint says Space. Making Enter choose would mean
   changing the global chat key behaviour, which I left alone.
3. **Controller.** The existing docked mapping (`Game1.Panes.cs`, P499/P519)
   is D-pad up/down = cursor, D-pad left/right = hull, A = select, B = back,
   Y = launch, shoulders = HULLS/PARTS tab. I followed it, with the shoulders
   moved to previous/next hull as the brief asked. Two points differ from the
   brief's wording:
   - There is no Start mapping anywhere in the docked screens and no Buy
     button on the pad today, so **Buy on the pad is the Buy button**: X steps
     the focus to the button row, then D-pad + A.
   - With the shoulders on hulls, the pad had no way to reach the slot column,
     so **X steps the focus** (hulls → slots → buttons). X was unused while
     docked.

## Tests

New, in `tests/Allegiance.ClientCore.MonoGame.Tests/`:

- **`P1015LoadoutInputTests.cs`** — input sequences through the pure router on
  real PCore001 data, each for Classic and Modern at 1280×720 and 1920×1080:
  hull click and locked-hull refusal; slot click / click-again / offer / empty
  row; Default, Revert, Buy and Launch buttons; wheel per column with clamping;
  picker closing before its owner scrolls away; hover and its one-shot cue;
  touch tap; the full keyboard walk; Left/Right/Q/E against a twin
  `ShopModel.CycleHull`; `D` against a twin `ApplyDefaultLoadout`; Tab to the
  buttons; focus scrolling into view; held keys and the gate; the pad; rebinds
  and the hint. Artwork tests skip cleanly without the corpus.
- **`P1015LoadoutScreenRoutingTests.cs`** — the production
  `Game1.HandleDockedScreen` frame, headless, both styles: flag off runs the
  old keys and never creates a router; flag on leaves the old cursor, tab and
  hover band dead and routes keys, clicks and the hover cue to V2; sixteen
  gates each block keys, click, wheel, tap and Escape; the pad snapshot; Escape
  closing the picker first; custom-loadout save/restore and the `1`/`2` rule.
- `P625bActionMapGuardTests.cs` — the raw-key guard now also scans
  `Game1.LoadoutV2Input.cs`; its two Ctrl reads are allow-listed like P764's.

Counts (Passed/Failed/Skipped), with the artwork corpus present:

| Run | Result |
| --- | --- |
| New P1015 tests alone, three runs | 96/0/0 each (288/0/0) |
| New + changed + related (key table, help overlay, P1006, P1014, P764, V2 model/layout), three runs | 385/0/0 each |

One related test did fail on the first of those runs and was mine: I had added
an optional parameter to `HandleCustomLoadoutKeys`, which
`P764UiSoundSeamTests` calls by reflection. The method's signature is restored
(it now asks `LoadoutV2Shown` itself) and the three runs above are after that fix.

## Real-window proof

`tools/p1014-ui-proof` was rewritten for V2 input (its P1014 flag-on script
drove V2 through the old keys, which no longer reach it). It runs production
`Game1` against a loopback server under Xvfb with software GL, moves the
scripted pointer, wheel and real key states, and asserts each step.

| Run | Checks (Passed/Failed) |
| --- | --- |
| Classic 1920×1080, flag on | 31/0 |
| Modern 1920×1080, flag on | 31/0 |
| Classic 1280×720, flag on | 31/0 |
| Modern 1280×720, flag on | 31/0 |
| Classic 1280×720, `--off` | 8/0 |
| Modern 1280×720, `--off` | 8/0 |

The flag-on script clicks a hull, opens a slot, closes it, reopens it, picks an
offer, wheels the hull list to the end and back, uses `2` / arrows / Space /
Esc / Left / Right / `1` / `D` / Tab, the Default button and Revert, and
finally Esc back to the hangar. The `--off` script drives the old shop with
Down, Tab, Space and B and checks that no V2 frame or router exists.

Eight PNGs are retained, all viewed by me (the other eight stayed in scratch):

| Step | Classic 1920×1080 | Modern 1280×720 |
| --- | --- | --- |
| Slot clicked, offer hovered | [1](P1015_assets/classic-1920x1080-1-click-slot-picker.png) | [1](P1015_assets/modern-1280x720-1-click-slot-picker.png) |
| Offer mounted, hull list wheeled to the end | [2](P1015_assets/classic-1920x1080-2-offer-mounted-wheel-scrolled.png) | [2](P1015_assets/modern-1280x720-2-offer-mounted-wheel-scrolled.png) |
| Picker opened and focused by keyboard | [3](P1015_assets/classic-1920x1080-3-keys-picker-focus.png) | [3](P1015_assets/modern-1280x720-3-keys-picker-focus.png) |
| Default button pressed (button focus) | [4](P1015_assets/classic-1920x1080-4-default-button.png) | [4](P1015_assets/modern-1280x720-4-default-button.png) |

What the images show: the hovered row and the keyboard-focused row are
outlined, the open slot's picker is inline under its owner, the hull counter
reads `7-25 / 25` (Classic) and `17-25 / 25` (Modern) after the wheel, the slot
shows the newly mounted part, and the footer shows the live key map. Nothing is
clipped or overlapping in either style.

## Gate

| Step | Result |
| --- | --- |
| `dotnet build AllegianceCore.slnx` | 0 warnings, 0 errors |
| `tools/build-tools.sh` | all 140 tool projects built |
| New / changed tests 3× alone | 96/0/0 ×3; 385/0/0 ×3 with related suites |
| `tools/run-tests.sh --tier fast` (one run) | **FAIL**: 14,469 cases, 14,459 passed / 3 failed / 7 not executed, plus 6 slow cases |
| `scripts/parity-scorecard.py docs/parity/p1015-loadout-input.csv` | validated 9 rows |
| `tools/check-glossary-links.py` | OK |
| Real-window proof, six runs, after the final build | 140/0 checks; the eight retained PNGs reproduced byte-identical |

The fast suite's failures are not in this change: all 96 P1015 cases passed in
the sweep, and none of the failing or slow tests touches loadout, input or the
key table. Each was run three times alone and is ledgered in
`docs/TEST_FLAKES.md` ("P1015 loadout input qualification"):

- `P902SoundTemplateTests.Asr_and_cache_hit_mix_math_allocate_nothing_after_warmup`
  and `MissileBroadcastTests.Directly_fired_missile_broadcasts_create_to_teammate_and_destroy_to_whole_mission`
  pass 3/3 alone (known intermittents).
- `MissileLockLiveTests.MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire`
  fails 3/3 alone, exactly as the P1014 ledger entry records for master.
- The six slow cases (four methods) pass alone at 3–5 s.

Not run: the training Live gate (`tools/run-training-tests.sh`); this is not a
training change and the flag-off docked path is unchanged. `docker compose
config` does not apply (no new service, port or env var; Docker is absent in
the lane). `scripts/publish-guard.sh` checks commits, and there are none; I
scanned the working-tree changes for home paths and e-mail addresses (none).
The eight new PNGs need the usual media review before a push. The brief file
is removed from the tree.

## Hand-offs

- **P1016 (hardpoint highlight).** The hovered and focused slot are available
  without new input code: `LoadoutV2InputRouter.HoverIn(layout)` (an element
  of kind `Slot` with the model row index) and
  `LoadoutV2Presentation.SlotFocus`. In `Game1.LoadoutV2.cs` I changed three
  call sites inside `DrawLoadoutV2` (the `Build`, `Layout` and `Draw` calls);
  the rest of the method is untouched, so a merge should be small. In the
  renderer I changed only the footer hint line and the button hover test.
- **P1017 (buy/launch messages).** Buy and Launch arrive as
  `LoadoutV2Request` in `Game1.ApplyLoadoutV2Request` and call
  `BuyFromHangar` / `LaunchFromHangar` unchanged. The model's `BlockReason`
  already carries the locked-hull and does-not-fit refusals; nothing new is
  displayed for a refused Buy or Launch.
- **P1018 (integration).**
  - Touch: taps hit the V2 rectangles, but the two top-right pane toggles from
    `DockedTouchZones` (teleport, investment) are still live on the docked
    screen for touch devices and are not drawn by V2. I left them because they
    are not loadout rows and are the only touch route to those panes; V2 also
    has no on-screen Back or Undock button for touch.
  - The hint does not list Q/E, Esc, U or the custom-loadout digits (width).
  - `tools/p1014-ui-proof/compare.py` and the P1014 report's "default / offers
    / need" captures describe the previous script; the P1014 PNGs are kept.
- **Seen, not changed.** At 1280×720 the slot column's counter reads
  `1-5 / 6` while a picker is open (P1014 overflow label counts picker rows).

Native intent: the loadout is operated by clicking — a slot opens its part
menu, a pick mounts and closes it, Next/Previous walk the buyable hulls, and
Default, Buy, Launch and Back are buttons — with the screen dead while the
command view or the chat line owns input.

Modern approach: one pure router turns mouse, rebindable keys and the pad into
those same model actions on a single screen, with a visible focus, per-column
wheel scrolling and the old screen's own buy, launch and back paths.
