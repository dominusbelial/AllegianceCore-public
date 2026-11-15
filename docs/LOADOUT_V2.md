# Loadout V2

Loadout V2 puts hulls, a rotating ship preview and equipment on one screen.
Choosing a slot opens its offers directly below it; hovering an offer shows
stat changes before you mount it. Default, Revert, Buy and Launch use the same
`ShopModel` and server purchase path as the existing loadout screen. Classic
and Modern use the same controls and geometry, with their own palettes.

It is **off by default**, pending playtest approval. Open Options → Visuals →
**Loadout V2** to enable it. The description is “hulls, ship and inline equipment
offers”. The setting persists in the player profile's `visuals` group as
`hangar.loadoutV2`. A temporary launch override is:

```sh
--visuals hangar.loadoutV2=on
```

Dock, then choose Loadout in the hangar. The inventory shortcut also opens it:
**Shift+F4 with Port keys**, **F4 with Classic Allegiance keys**. Keyboard
preset and HUD style are independent. Changing the option while docked switches
the presentation; hidden pickers, hover and hit targets are discarded.

| Device | Controls |
| --- | --- |
| Mouse | Click a hull to select it; click a slot to open/close its picker; hover an offer for deltas and click to mount it. The empty offer clears the slot. Wheel scrolls the column or picker under the pointer. Hover a weapon/turret row to highlight its mount. |
| Keyboard | Tab cycles hulls → slots → actions. Up/Down move focus. Left/Right change hull, except on the action row where arrows move between buttons. Q/E always select previous/next hull. Space chooses; Enter also confirms when the active key preset permits it (Classic Allegiance uses Enter for chat). D applies Default; L buys and launches; U undocks with the actual ship. B/Escape close the picker first, then return to the hangar. |
| Controller | D-pad moves/selects hulls and action focus; A chooses; B closes the picker or returns to the hangar; X cycles focus; shoulders change hull; Y buys and launches. |
| Touch | Tap the visible hulls, slots, offers and action buttons. Back at the top closes the picker first, then returns to the hangar; Undock leaves with the actual ship. Invisible legacy teleport/investment zones are inactive over V2. Touch scrolling/swipe is not implemented; a wheel or keyboard/controller is needed for rows beyond the visible page. |

Keyboard actions are rebindable in Map Keys and Controls. The direct
“focus hulls” and “focus slots” actions ship unbound; digits retain native
custom loadouts: Ctrl+0…9 saves, plain 0…9 restores. Binding a direct focus
action to a plain digit replaces that digit's restore action on V2.

Default selects the hull's native default kit. Revert restores the actual
docked ship. Buy purchases without launching. An unaffordable Buy shows
**Need $X** and can request the shortfall from the team when the shared rules
allow it. Server refusals appear on the status line. Money changes refresh the
price while docked. Launch buys the pending loadout and leaves; **Undock**
leaves with the actual ship without buying pending edits.

The hull/part availability, mount compatibility, hull-switch defaults, price,
refund, money request and purchase acknowledgement rules follow native
`WinTrek/load.cpp`. Native starts a fresh player with a hull-less placeholder;
its first dock restores an affordable default loadout when no saved hull exists.
This port supplies a free spawn hull/weapon instead. Both loadout screens now
receive that actual inventory, and free hardware retains zero trade-in value.

Three columns, inline deltas, keyboard/controller navigation and Revert are
presentation additions. Hardpoint rings use authored CVH mount positions and
turret direction; obscured mounts are dimmed. Missing frames and non-weapon
slots show no invented mount. The native firing cone code is commented out and
PCore001 has no firing-angle limit, so **Aim guide / arc limit unavailable**
is decorative direction guidance, not a simulated firing boundary. Extra stats
marked `~`, including weapon DPS, are estimates; actual output depends on
energy, ammo, target defense and firing conditions.

Known parity gaps retained from P1012/P1017:

- Cargo is not editable in the shop model, although owning-client inventory
  sync carries it.
- Custom loadout slots are kept for the current session, not saved to disk.
- Same-hull native `RestoreLoadout` upgrade/replay behavior is incomplete.
- Server launch only when every requested part was bought, and reuse of parts
  the station no longer sells, remain unported together.
- A free part upgraded to its successor by dock restock can have an inaccurate
  client refund estimate because the wire carries no provenance.

The [integration report](reports/LOADOUT_V2_INTEGRATION_P1018.md) records tests,
walkthrough captures and gate results. The proof tool's
[README](../tools/p1014-ui-proof/README.md) provides local reproduction.
