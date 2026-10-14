# Native training mission scripts — full verbatim script (Mission 2 "Basic Flight" + Mission 5 "Command View")

Source: `~/Development/Allegiance/src/training/mission2.cpp` and `mission5.cpp`.
No separate voice-over asset files (`.wav`/`.ogg`/etc.) exist anywhere in the
native source tree — confirmed by an exhaustive search of the whole repo.
Every training line is instead spoken by a numbered `SoundID` constant
(e.g. `tm_2_01Sound`) resolved by the compiled game's own resource bank, but
**the exact script text is preserved as a comment directly above each
`PlaySoundGoal`/`PlaySoundAction` call** — so this doc reconstructs the full
verbatim training script straight from source, with no audio needed.

Each mission is a `Goal`/`GoalList` tree (`Training::Mission2`/`Mission5`,
subclasses of `TrainingMission`) built from `CreateGoal01..NN()` methods.
Below, each numbered line is `tm_<mission>_<NN>[r]Sound` — the `r` suffix
marks a line that plays while *waiting on a player action* (a "response"
cue) rather than a plain scripted line. Mechanical notes in *italics*
describe the actual gating/action code around a line where it's load-bearing
(e.g. the control-lock sequence), not just narration.

---

## Mission 2 — "Basic Flight" (`c_TM_2_Basic_Flight`, start sector 1010)

*Before `tm_2_01`: on load, a `SetControlConstraintsAction` calls
`DisableInputAction(0xffffffff)` (every input) and zeroes yaw/pitch/roll/
throttle scale to 0.0 — full control lockout, confirmed at
`mission2.cpp:157-163`. This is the literal "flight instructor blocks your
controls" moment.*

**tm_2_01** — In this mission, you will receive a basic introduction to your
controls and heads-up display and do a little bit of flying.

*(camera zooms to cockpit view, `SetDisplayModeAction(cmCockpit)`)*

**tm_2_02** — For now, your flight controls are disabled; so don't do
anything until I tell you to.

**tm_2_03** — First, I'll explain the peripheral radar system. This system
highlights objects of interest in space by placing icons to represent them
on your screen. When an object is in your field of view, its icon is
located appropriately on your screen. When the object is out of your field
of view, its icon rides in a gutter at the edge of your screen.

**tm_2_04** — Look out in front of your ship right now. You should see a
small craft with a purple icon near it. That's me. This icon indicates my
ship's class. Beside it is the name of the pilot, me. The number just under
the icon is my distance from you, measured in meters.

**tm_2_05** — Watch how the icon moves as I approach you. I am going to fly
over your head. Pay attention to how the icon travels as my ship approaches
the edge of your screen and then moves out of your field of view.

*(the instructor's ship — "the commander" — flies a scripted waypoint pass
over the player, `CreateWaypointAction` + `SetCommandAction(..., c_cidGoto)`)*

**tm_2_06** — Notice that the icon remains on the screen, in a gutter. You
can find me again by turning your ship toward my icon on the edge of the
screen. I am above you and to your right. Now your craft will turn to show
you how it works.

*(the player's own ship is scripted to auto-turn toward the instructor via
`TurnToAction`, then controls are reset with a plain `SetControlsAction`)*

**tm_2_07** — You'll get the chance to use the radar later.

**tm_2_08** — Now I'm going to tell you about your ship controls. You can
change the orientation of your craft using your joystick or keyboard. I'm
going to release the controls now, so you can try them out while I explain
them.

*(controls released: `ScaleInputControl` for yaw/pitch/roll set back to
`1.0`, `mission2.cpp:298-304` — the actual "unblock" moment for turning,
though throttle is still held at 0 at this point)*

> *(commented out in source, "entirely unnecessary — LANS"): tm_2_09
> (joystick stick-direction explanation) / tm_2_10 (keyboard arrow-key
> explanation) — present in the script but disabled, not currently played.*

**tm_2_11** — Now I want you to practice turning the ship about.

**tm_2_11r** *(waits for SPACEBAR)* — When you are comfortable with the
operation of your craft, press the SPACEBAR to proceed.

**tm_2_12** — Good. Now you know how to turn the craft.

**tm_2_13** — Next, you're going to move this interceptor around a bit and
get used to the throttle. I'm going to disable turning for a moment, but
the throttle will be active so you can experiment with it while I talk.

*(yaw/pitch/roll scaled back to 0.0, throttle scaled to 1.0 —
`mission2.cpp:391-396`)*

**tm_2_14** *(joystick connected)* — If your joystick has a throttle
control, you can use that to finely adjust your throttle.

**tm_2_15** *(no joystick)* — You can use the keyboard to set the throttle.
The BACKSLASH key is full throttle, and BACKSPACE is stop. You can use the
RIGHT BRACKET key for 66% throttle and the LEFT BRACKET key for 33%
throttle.

**tm_2_16** — Now I want you to fly forward at full throttle.

**tm_2_16r** *(waits for throttle axis == 1.0)* — Adjust the throttle
control full forward on your joystick, or press the BACKSLASH key.

**tm_2_17** — Good.

**tm_2_18** — You can see the precise setting of your throttle by looking at
this gauge.

**tm_2_18r** *(waits for throttle axis != 1.0)* — Try adjusting the
throttle to see how this gauge changes.

**tm_2_19** — Now you know how to adjust the throttle and read a gauge on
your heads-up display. Now I'm going to go over some of the other items on
your display. Your controls will be disabled while I talk about these
things, so just pay attention.

**tm_2_20** — When you are moving, the velocity indicator will tell you how
fast you are moving. The indicator reads in meters per second, or M.P.S.

*(throttle forced to 0.33 via `SetControlsAction`, own ship yaw/pitch also
scripted briefly to demonstrate)*

**tm_2_21** — Notice the motion indicator in the center of your screen. This
indicator tells you which way you are moving. In space, there is nothing to
stop your ship from sliding sideways. When you turn, it takes a moment for
the ship's engines to compensate for your velocity. As a result, you won't
always be facing the same direction you are moving. This indicator will
show you the direction that your craft is traveling. Now your ship will
turn a bit to demonstrate.

**tm_2_22** — Got it? — Good.

**tm_2_23** — I'm going to activate your controls again, and I want you to
practice turning and adjusting your throttle.

*(full unlock: yaw/pitch/roll/throttle all scaled back to 1.0 —
`mission2.cpp:609-613`)*

**tm_2_24** — Notice how the motion indicator floats around the screen as
you turn and that you don't stop immediately when you throttle down.

**tm_2_24r** *(waits for SPACEBAR)* — When you are comfortable with the
operation of your ship, press the SPACEBAR to proceed.

**tm_2_25** — Okay. That wasn't so bad was it? You're doing good — You've
learned the basics of space flight.

**tm_2_26** — I'm going to introduce you to the booster now.

**tm_2_27** — This shaking and rumbling is your booster firing.

*(booster fired for the player automatically via a scripted
`SetControlsAction` with `afterburnerButtonIGC`)*

**tm_2_28** — Notice your speed.

**tm_2_29** — Notice how the fuel gauge drops when using the boosters.

**tm_2_30** — You carry a limited supply of fuel on the ship, and it
doesn't regenerate, so make sure you watch your fuel gauge when firing the
boosters.

**tm_2_31** — Now I want you to give it a try.

**tm_2_32** *(joystick connected, waits for the booster button state)** —
Activate the booster with your joystick by pressing and holding button 3.

**tm_2_33** *(no joystick — line itself is commented out/disabled in
source, only the on-screen text message plays)* — On the keyboard, press
and hold the TAB key to fire your boosters. *(on-screen text actually says
"...hold the SPACE key", a documented mismatch with this disabled voice
line, `mission2.cpp:743-748`.)*

**tm_2_34** — Good!

**tm_2_35** — I cut the boosters there because I didn't want to get too far
away from home. The boosters can get you up to speed very fast and can even
take you over the top handling speed for your craft. You have to be careful
where you point the ship when you use the boosters, because you might not
be able turn or stop fast enough to avoid obstacles.

**tm_2_36** — All right, Cadet, you've made it through your introductory
flight! Excellent.

**tm_2_37** — Let's go home now. I'm switching you over to autopilot, so
you can look over the gauges and such in the heads-up display. Enjoy the
ride.

*(ship is placed on scripted autopilot back to the home station,
`SetAutopilotAction` + periodic re-assertion every 2s)*

**tm_2_38** — I'm going to go back to the outpost and land in a green
hangar bay. The red bays are the launching points. Make sure you never try
to land in a red hangar, or you'll be a trophy on somebody's canopy.

*(mission ends once the ship lands — `Mission2::ShipLanded()` calls
`Terminate()`)*

---

## Mission 5 — "Command View" (`c_TM_5_Command_View`, start sector 1050)

*Setup: the player is docked at station 1050 (Neptune) with the camera in
`vmCommand` view mode (`SetViewMode(TrekWindow::vmCommand)`,
`EnableDisplacementCommandView(true)`) — a top-down/tactical map view, not
cockpit. The mission spawns three "Tower Layer" builder drones, four miner
drones (two at Neptune, two at Mars — code comment says two, `m_miner2/3/4`
are all created at Mars per the actual `CreateDroneAction` calls), and a
"Stealth Fighter" wingman, all initially told `c_cidDoNothing`. This is the
tutorial for issuing orders to your own side's utility/combat ships without
piloting them yourself — directly answers "flying as required by my
commander" from the player's perspective of being the one giving orders.*

**tm_5_01** — You can move your view around by moving the mouse to the edge
of the screen. You can also adjust the field of view using the PLUS and
MINUS keys. Using the targeting keys you learned in training mission 3 is
also good practice, because that will move your view to the targeted
object.

**tm_5_02** *(waits for SPACEBAR)* — Practice manipulating your view, and
then press the SPACEBAR when you are ready to proceed.

**tm_5_03** — You can buy items like towers and mines to defend your bases
and block off alephs. There are three tower layers waiting for orders here.
We want to deploy the towers to defend the aleph from incoming enemies.

**tm_5_03r** *(waits for a tower layer to be picked)* — First, left-click a
tower layer.

**tm_5_04** — Now, left-click the Build command. If you click and drag when
ordering towers around, you can set the height of the deployment position
off the horizontal plane.

**tm_5_04r** *(waits for a Build command)* — Right-click in space and drag
to indicate where you want the tower to go.

**tm_5_05** — Now order all the towers to deploy in the same area. We don't
want any enemies coming in.

**tm_5_05r** *(waits for all three Build commands)* — Order all the towers
to defend aleph 'Mars'.

**tm_5_06** — Good job! You can watch those guys build if you want.

**tm_5_06r** *(waits for the player to switch sector view to Mars)* — When
you are ready, left-click sector Mars in the Map display to change the
sector you are viewing.

**tm_5_07** — Now you are looking at sector Mars. There is a friendly base
here with several friendly craft: three miners and a stealth fighter on
patrol. They are waiting for further orders. First, identify a miner and
tell it to go mine Helium3. Start by using the mouse to left-click it.

**tm_5_07r** *(waits for a miner to be picked)* — Left-click on a miner.

**tm_5_08** — Now order the miner to go mine a Helium3 asteroid. You can
find the asteroid by looking for one whose name starts with He and looks
like a bright white rock icon, or you can use the mouse to point at
asteroids until the cursor changes to the Mine icon. You can left-click the
Mine command and then right-click the Helium3 asteroid.

**tm_5_08r** *(waits for a Mine command)* — Right-click on the Helium
asteroid you want to mine.

**tm_5_09** — Nicely done. Miners will leave an asteroid they are working
at when another miner approaches. If this happens, the miner may not be at
full capacity when it leaves, and your paydays will be smaller. It is more
efficient to have different miners at different asteroids, rather than
ganging them up on a single asteroid. *(This is the native "crowding"
behavior — `shipIGC.cpp:2153-2202` — that this port's `Sector.ResolveMining`
explicitly documents as a deliberate, not-yet-ported simplification. This
line is direct native confirmation that it's a real, player-visible
mechanic, not an edge case.)*

**tm_5_10** *(waits for all three miners mining)* — Get all the miners
working.

**tm_5_11** — Perfect. To speed up mining, you can build Outposts and other
buildings to shorten the round-trip for miners. If the miners have to go to
a different sector to mine, that can make a big difference.

**tm_5_12** — Attention, Cadet, new contact: an enemy scout. In some cases,
utility craft like miners will run home for protection, but you can tell
them to hide by giving them orders to go to a base. You left-click the
miners, then left-click the Go To command, and then right-click the base.

*(an "Enemy Scout" drone is spawned at this point, signature set to
effectively infinite so it's always visible/trackable for the tutorial)*

**tm_5_13** — Most enemy ships can be considered a threat to your miners
unless you have plenty of defending pilots — which you don't. So let's get
those miners to shelter — send all three of them to the base.

**tm_5_13r** *(waits for all three Goto commands)* — Send all three of your
miners to base for safety.

**tm_5_14** — Good! Utility ships like miners and constructors are drones.
They will always accept your orders. *(Direct native confirmation of the
port's own `Ship.IsNpc`/drone-always-obeys assumption — no acceptance-roll
mechanic exists for drone orders, matching this port's design.)*

**tm_5_15** — Now let's talk about what orders you can give combat pilots.
You give commands to pilots the same way you give commands to utility
craft. Pilots are real people, though, so they may not always accept the
orders you give.

**tm_5_15r** *(waits for the stealth fighter to attack)* — Give that
stealth fighter an order to attack the enemy scout.

**tm_5_16** — Watch what happens.

*(mission waits for either ship to be destroyed)*

**tm_5_17** — Sometimes you may want to give an order to a pilot but you
don't know where he is.

**tm_5_18** — This is the Team display. Here, you can select a pilot in the
list, and then click the "Take Me To" button to jump to where he is. Let's
find where Miner 01 is right now.

*(opens the Team pane, `ShowPaneAction(ofExpandedTeam)` — F6 window)*

**tm_5_18r** *(waits for the view to jump to Miner 01's sector)* — Select
Miner 01 in the Team display, and click the "Take Me To" button.

**tm_5_19** — Notice that you are now viewing the sector that Miner 01 is
in, and you are prepared to give it a command.

**tm_5_20** — The Team display is very helpful for keeping track of utility
ships and getting the status of your team members. If a utility ship is
docked, you can use that screen to send them back to work. You can press
the F6 key to bring up the Team display —

*(Mission5.cpp ends here, mid-sentence per the source comment block — the
script cuts off at tm_5_20; this is the last line in the file.)*

**Direct relevance to P381/P382 (docked-drone auto-launch)**: `tm_5_20` is
native's own explicit statement that a *player* can manually command a
docked utility ship "back to work" from the Team display (F6) — a
player-facing manual trigger for exactly the transition P381 automates on a
timer instead. Confirms both mechanisms are real and native: the automatic
`OkToLaunch` cluster-tick pass (what P381 ports) AND a manual
commander-issued override via the Team display (a separate, not-yet-scoped
UI feature this port doesn't have yet — worth a future task if Domingo
wants direct manual control over docked utility ships beyond the P148/150/
152/153/154 `ORDER_CHANGE` commands already ported).
