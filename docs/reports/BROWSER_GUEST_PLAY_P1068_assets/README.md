# P1068 proof artifacts

`browser-guest-summary.json` summarises the post-merge workstation Chromium run (Claude review,
2026-11-21): independent-context guest names, duplicate/reserved refusals, reroll/retry, one-click
lobby entry, ordinary menu choice, disabled offers and capture sizes. The full request log and
console stream are reduced to counts.

The JPEGs (quality 80) are from that run and were reviewed by Claude:

- `guest-disclosure.jpg` — the "Playing as a guest" panel before joining: the three disclosure
  paragraphs, the editable callsign (`Pilot-` + 10 random symbols), Use this name / Roll a new
  name / Forget guest identity.
- `guest-flight-classic.jpg`, `guest-flight-modern.jpg` — flight in each HUD style with the
  persistent `Guest · Pilot-…` label and the `Sign in / create account` link under the canvas.
