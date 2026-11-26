# VR Test 1: review notes for the VR client (2026-11-26)

For ragesaq and the VR work on the `ragesaq/AllegianceCore-public` fork. Written by Claude for Domingo after watching
the "AllegianceCore-VR-Test1" recording (72 s, 1920x1080 at 60 fps, the desktop mirror of one eye) and reading the
fork's `knowledge/vr-hud-design.md` and `Game1.VrHud.cs` on `port/vr-hud-step2` (`d04562fe`). Timestamps refer to the
recording. These are review notes, not decisions: the fork's design note and decision table stay the record.

## Which build this was

The panel still shows the port's ROLL dial ("Current location: Neptune", "-090deg", "ROLL" at about 0:07). Step 2
removes that dial from the VR panel (`HudRenderer.RollOnMainPass`), so the recording is most likely the step 1 plus
layer 2 build (PR #17, merged as `de0c1ce`), with the whole flat HUD on the interim 1 m panel. Please confirm the commit.
If it was step 2, the items under "Fix first" are step 2 bugs, not step 1 leftovers.

## What works

- Rendering in the headset: Neptune, the asteroid field, the carrier and the miners, planets and the sky.
- Targeting brackets with name and range sit on their objects at depth (Miner 03/04, Enemy Scout, Teleport Receiver,
  Base, He1011 at 4 to 6 km).
- Combat: the kill feed ("You killed Enemy Fighter", about 0:30) and the team chat.
- The sector map (Neptune to Mars) and the "[TAB] EXPAND" hint.
- The HUD is fixed to the ship, not the head: looking up (about 0:54 to 1:06) leaves it behind. The slanted
  information panel looks like the cockpit-display direction of layer 4.
- The volumetric explosion (about 0:30 to 0:35) holds up well; Domingo reports it performs very well.

## Fix first

1. **Overlapping elements on the panel** (clearest at about 0:07 to 0:12; also 0:30 to 0:35). In one frame:
   - the ARMOR and SHIELD labels appear twice, a second pair sitting on the THRUST and FUEL gauges;
   - "KB 0%" and a second "KB" line (17% to 21%) appear one above the other under SIG;
   - the selected target's line ("Enemy Fighter: Fig…") runs under the ARMOR and SHIELD labels;
   - a cyan separator line runs through one "16520/32767 | NOT READY" row, so it reads as struck through.

   The two "PW Gat Gun 1" rows and the repeated ammunition rows are probably correct (one row per gun mount); check
   them against the flat HUD at the same window size before treating them as duplicates.

   This matches the open question in `knowledge/vr-hud-design.md` ("What the doubled elements in the milestone 2
   screenshot are"). The recording points at two flat-HUD panes laid over each other on the panel (the centre ring
   group and the own-ship and target status around it), not at a separate cockpit display. Suggested check, in this
   order:
   - take a flat (non-VR) screenshot at the same back-buffer size the VR panel uses, with Classic, and see whether
     the same overlap is there; if it is, it is a flat layout problem at that size, not a VR one;
   - repeat with step 2, where the centre group leaves the panel (`HudRenderer.CentreGroupOnMainPass`) and moves to
     the flight block at infinity; most of the overlap should go with it;
   - add a test that counts, in one VR eye frame, the ring gauges and the ARMOR/SHIELD labels drawn (one each), and
     that the flat frame is unchanged.

## Worth a look in the headset

2. **Panel placement.** In the mirror the panel and the sector map sit low and right and are cut off at the frame
   edge in most shots. The mirror crops, so this may be fine in the headset; if reading the map needs a head tilt,
   try `ALLEGIANCE_VR_HUD_PANEL` with a smaller pitch-down angle and record the values that work.
3. **Close explosions.** At about 0:32 one explosion covers most of the view. Performance is fine. Ignore this if it
   is not uncomfortable; if it is, fade or limit the size only when the explosion is very near the camera, and leave
   the volumetric effect as it is.
4. **Frame timing.** A SteamVR or Oculus frame-timing overlay screenshot during a big fight or explosion would put the
   performance on record in the PR. The 60 fps mirror recording cannot show headset frame times.

## Not VR

- The repeated "[TEAM] Coming home empty" chat line is an upstream bot/miner chatter bug, not part of the VR work.

## For the upstream PR

The folder and test rules from the earlier layout notes still apply. VR stays off by default (`--vr`). The
default `Allegiance.ClientCore.MonoGame.Tests` project takes no OpenXR dependency: pure geometry tests stay there,
and tests that need a runtime or headset go in the Live tier. Send one PR per milestone (ragesaq-012).
