# Training Framework — architecture (TR-1)

This document describes the client-side training **framework** ported in TR-1: the
goal/condition/action script engine, the `TrainingMission` lifecycle, the control masks, and the
`ITrainingSite` bridge that lets TR-2 bind it to the embedded single-player host and the MonoGame
client. It explains how the pieces fit together and exactly what TR-2 has left to do.

The port lives in `src/Allegiance.ClientCore/Training/` (namespace `Allegiance.ClientCore.Training`)
and is a faithful translation of the native `~/Development/Allegiance/src/training/` C++ library.
Every class cites its native source `file:line` in its XML doc comment.

---

## Why a separate framework library

The original game's training missions are small **data files** (`mission2.cpp`..`mission10.cpp`,
600-1200 lines each) built on a shared script engine of ~150 tiny classes: `Goal`/`GoalList`,
`Condition` (+ and/or/not/true/false/periodic/ntimes/elapsed-time/proxy), `Action`
(+ actionlist/conditional/one-time/proxy/no-op/reset), plus game-facing `Get*Condition`/
`Create*Action`/`Set*Action`. TR-1 ports that engine as a host-agnostic library so missions can run
against **any** game binding — the embedded host in TR-2 or a test fake now.

The key design decision: native ties every condition/action to `trekClient` and the `ImissionIGC`
world globals. The port instead injects a narrow **`ITrainingSite`** interface, which is the single
seam TR-2 fills with the embedded host + client. Everything that does not touch the game (the whole
evaluation graph) is pure C# and testable with a fake site.

---

## Layout

```
src/Allegiance.ClientCore/Training/
├── ConditionAndAction.cs        Condition + Action bases (training/Condition.h, Action.h)
├── TrainingTarget.cs            TargetRef + ObjectType + ITrainingObject (TypeIDTarget, modelIGC)
├── ITrainingSite.cs             the game bridge (see below)
├── TrainingControlState.cs      the control masks (Training.cpp:28-34)
├── TrainingController.cs        ApproveControls/ApproveActions + the IsTraining flag (Training.cpp)
├── BooleanConditions.cs         AndCondition, OrCondition, Predicate/NotCondition,
│                                TrueCondition, FalseCondition
├── TimeConditions.cs            PeriodicCondition, NTimesCondition, ElapsedTimeCondition
├── ListsAndProxy.cs             ConditionList, ActionList, ProxyCondition
├── Goal.cs / GoalList.cs        the objective state machine
├── CompositeActions.cs          ConditionalAction, OneTimeAction, ProxyAction, NoAction
├── ControlActions.cs            SetControlConstraintsAction, SetControlsAction, TurnToAction
├── GeometricConditions.cs       ObjectWithinRadiusCondition, ObjectMovingTowardsCondition,
│                                ObjectPointingAtCondition
├── SiteConditions.cs            GetKeyCondition, GetChatCondition, GetControlActiveCondition,
│                                GetStateBitsCondition, GetAutopilotCondition, GetTargetCondition,
│                                GetShipIsStoppedCondition
├── SiteActions.cs               MessageAction, PlaySoundAction, SoundFinishedCondition,
│                                SetDisplayModeAction, SetHudOverlayAction, SetAutopilotAction
├── CreationActions.cs           CreateWaypointAction, CreateProbeAction, CreateDroneAction,
│                                SetCommandAction
├── ResetAction.cs               ResetAction (native body disabled)
├── TrainingMission.cs           the abstract TrainingMission base + RespawnData
├── TrainingMissionIds.cs        mission ids, keys, command ids, camera modes, overlay ids, tm_2 sounds
└── Mission2.cs                  Mission 2 (Basic Flight) — a GoalList data file
```

---

## Evaluation semantics (the parts that must be exactly right)

The whole engine is a **pull model**: the mission calls `Frame()` once per tick; `Frame` calls
`Evaluate()` on the active goal tree. Everything else is a consequence of three rules:

1. **`Goal`** (`goal.cpp:22-107`): `Start()` skips the goal if its success condition is already true
   (line 40) or if its skip-condition is true (line 43); otherwise it runs the start actions and
   starts the constraints. `Evaluate()` is complete when the success condition is true, and it always
   walks the constraints afterwards (lines 79-90) because constraints carry actions.
2. **`GoalList`** (`goallist.cpp:37-92`): `Start()` advances past any goal that *starts* complete and
   stops at the first incomplete one. `Evaluate()` completes the current goal, `Stop()`s it, advances,
   and `Start()`s the next — recursing through rule 1.
3. **`ConditionalAction`** (`conditionalaction.cpp:21-87`): the only way actions are sequenced by
   gameplay, in two flavours — *immediate* (act when the condition is true, same pass) and *delayed*
   (act on the pass *after* the condition was true). The `m_bRunOnce` flag (header line 35) stops
   repeat fires.

Timing primitives (`periodiccondition`, `ntimescondition`, `elapsedtimecondition`) read a monotonic
clock from the site (`ITrainingSite.TimeSeconds`) instead of the native `Time::Now()`. The
`ElapsedTimeCondition(f)` macro is reproduced exactly as `NTimesCondition(PeriodicCondition(True,f),2,true)`
so it fires once shortly after `f` seconds.

---

## `ITrainingSite` — the game seam

`ITrainingSite` is the C# analogue of the native training code's reliance on `trekClient` +
`ImissionIGC`. It exposes only what the framework needs:

| Group | Members |
|---|---|
| Clock | `TimeSeconds` |
| Player / objects | `PlayerShip`, `Resolve(TargetRef)` |
| Ship state | `GetAutopilot`, `GetStateM`, `GetShipControls`, `GetInputControls`, `GetCommandTarget` |
| Player events | `ConsumeKey`, `RecordKeyPress`, `ConsumeChat`, `RecordChat` |
| World creation | `CreateWaypoint`, `CreateProbe`, `CreateDrone`, `PlaceDrone`, `IssueCommand` |
| Ship/camera/HUD | `SetAutopilot`, `SetDisplayMode`, `SetHudOverlay`, `TurnToFace` |
| Output | `ShowMessage`, `PlaySound`, `IsSoundPlaying`, `StopSound` |

Note the **control masks are deliberately not on the site**: native keeps them in module globals
(`Training.cpp:28-34`) that the flight-input path reads and the actions write, so they live in the
static `TrainingControlState` and are applied by `TrainingController.ApproveControls/ApproveActions`.

### How TR-2 binds it

1. `TrainingController.BeginMission()` on mission start (resets the masks, arms `IsTraining`),
   `EndMission()` on teardown.
2. In the flight-input path, apply the masks only when `TrainingController.IsTraining`:
   `flightControlData = TrainingController.ApproveControls(rawControlData)` and
   `actionMask = TrainingController.ApproveActions(rawActionMask)`.
3. Implement `ITrainingSite` against the embedded host's `ImissionIGC` + the MonoGame client
   (resolve `TargetRef`s to live models, forward key/chat events, drive messages/sounds/waypoints,
   and provide `TurnToFace` from the ship simulator).
4. Construct a `Mission2(site)`, call `Start()`, then `Frame()` each game tick; when `Frame()` returns
   `true` the mission has terminated and the host tears it down.
5. Route player input through `mission.RecordKeyPress(key)` / `mission.RecordChat(recipient)` and the
   death/landing hooks (`ShipDied`, `ShipLanded`, `ShipKilled`, `KillStationEvent`, `RestoreShip`).

---

## Control masks

`TrainingControlState` holds `AllowableControlScalars[4]`, `InputControls`, `SetInputControls[4]`,
`AllowableActionMask`, `SetInputAction` — the five native globals. `TrainingController.ApproveControls`
translates `Training.cpp:253-284` (scale turn axes, translate the throttle to 0..1 before scaling and
back, then override forced axes), and `ApproveActions` translates `Training.cpp:287-300` (AND allowed,
OR forced). The action classes that write these (`SetControlConstraintsAction`, `SetControlsAction`,
`TurnToAction`) behave exactly like native.

---

## Class map — native → port

See `docs/reports/TR1_TRAINING_FRAMEWORK.md` for the full per-class map with citations and the
deferred-items list. Everything in `src/Allegiance.ClientCore/Training/` that derives from
`Condition`/`Action` or the `TrainingMission`/`TrainingController`/`TrainingControlState` singletons is
a 1:1 port; mission-specific game bindings (host, camera, sim) are TR-2.
