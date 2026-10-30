using Allegiance.ClientCore.Training;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Microsoft.Xna.Framework.Input;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// TR-2 — the Game1 slice that drives a native training mission against the live client.
/// <see cref="BeginTrainingMission"/> starts the mission (Basic Flight / Basic Weaponry / Enemy
/// Engagement in TR-3) on the training site bound to the real session (see
/// <see cref="ClientTrainingSite"/>); <see cref="UpdateTraining"/> pumps the mission each frame and
/// tears it down on completion; the HUD overlay routed by <see cref="SetHudOverlayAction"/> is rendered
/// by the flight HUD. Inert with no session.
/// </summary>
public sealed partial class Game1
{
    /// <summary>Native approves manual joystick input (WinTrek.cpp:8381-8382), then
    /// PlotShipMove supplies autopilot controls independently (Igc/shipIGC.cpp:2061-2068).
    /// M4's locked-input waypoint demonstration must still fly.</summary>
    internal static Flight.FlightCommand ComposeTrainingFlight(Flight.FlightCommand manual,
        Flight.FlightCommand? autopilot)
    {
        var approved = new Flight.FlightCommand(TrainingController.ApproveControls(manual.Controls),
            TrainingController.ApproveActions(manual.StateM));
        return autopilot ?? approved;
    }

    private TrainingSession? _trainingSession;
    private TrainingHost? _embeddedHost;
    private string _trainingError = "";
    /// <summary>Seconds left for the loopback join to replicate the player ship before the
    /// pending mission start gives up (BeginTrainingMission → UpdateTraining).</summary>
    private double _trainingStartDeadline;
    private const double TrainingStartTimeoutSeconds = 20.0;
    private double _trainingClock;
    private float _trainingFrameDelta = 1f / 60f;
    private int _trainingMissionId;
    private int _nextTrainingSound;
    private readonly Dictionary<int, Microsoft.Xna.Framework.Audio.SoundEffectInstance> _trainingSounds = new();

    private int PlayTrainingSound(int missionId, int soundId)
    {
        string? stem = TrainingSoundCatalog.Stem(missionId, soundId);
        Console.WriteLine($"[training] voice {stem ?? soundId.ToString()} (mission {missionId})");
        var instance = _sound?.PlaySlideSound(stem);
        if (instance is null) return -1;
        // Release finished message voices too (they have no SoundFinishedCondition).
        foreach (int key in _trainingSounds.Where(kv => kv.Value.State == Microsoft.Xna.Framework.Audio.SoundState.Stopped).Select(kv => kv.Key).ToArray())
            StopTrainingSound(key);
        int handle = ++_nextTrainingSound;
        _trainingSounds.Add(handle, instance);
        return handle;
    }

    private void StopTrainingSound(int handle)
    {
        if (!_trainingSounds.Remove(handle, out var sound)) return;
        sound.Stop();
        sound.Dispose();
    }

    private void ClearTrainingSounds()
    {
        foreach (int handle in _trainingSounds.Keys.ToArray()) StopTrainingSound(handle);
    }

    /// <summary>P785 — the slideshow currently showing (pre-mission, post-mission or
    /// the Introduction screen itself). Null when none.</summary>
    private SlideshowModel? _trainingSlideshow;

    /// <summary>P785 — the current slide's playing clip (native m_pSoundInstance,
    /// Slideshow.cpp:105): when it finishes the 1 s advance timer arms.</summary>
    private Microsoft.Xna.Framework.Audio.SoundEffectInstance? _slideSound;

    /// <summary>P785 — the training launch drives side-select/ready-check automatically
    /// (the embedded-host mission is already Stage=Started; native StartTraining goes
    /// straight to ScreenIDCombat, WinTrek.cpp:7401-7421).</summary>
    private bool _trainingAutoJoin;
    private bool _trainingSideRequested;

    /// <summary>Which dismiss behaviour the active slideshow carries (native's
    /// TrainingSlideshow vs PostTrainingSlideshow subclasses).</summary>
    private bool _trainingSlideshowIsPost;

    // P785 — whether a key/click was down last frame (the slideshow advances on the edge).
    private bool _slideInputHeld;

    /// <summary>P785 — native DoQuitMission skips the post slideshow
    /// (WinTrek.cpp:3559-3566 SetSkipPostSlideshow → trainingscreen); the port's
    /// completion path honours the same flag (trainingmission.cpp:110-115).</summary>
    private bool _trainingSkipPostSlideshow;

    // P785 — edge latches for the six training keys (UpdateTrainingKeys).
    private bool _pauseTmWasDown, _finishTmWasDown, _skipTmGoalWasDown,
        _tmClockUpWasDown, _tmClockDownWasDown, _tmClockResetWasDown;

    /// <summary>P785 — the ALLEGIANCE_TRAINING_OVERLAY proof hook ran once.</summary>
    private bool _trainingOverlayProofSeeded;
    /// <summary>The ALLEGIANCE_TRAINING_START proof hook ran once.</summary>
    private bool _trainingStartProofSeeded;

    /// <summary>Whether a training mission is currently running on this client.</summary>
    private bool TrainingActive => _trainingSession is { Finished: false };

    /// <summary>Begin a native training mission against the embedded host. TM1
    /// (Introduction) never starts a mission: native routes it to the tm_1_introduction
    /// post-slideshow (trainingscreen.cpp:182-185; Training.cpp:85-89 starts no mission
    /// for it).</summary>
    private void BeginTrainingMission(int missionId)
    {
        if (missionId == Tm1Introduction)
        {
            BeginTrainingSlideshow(TrainingSlides.Introduction, isPost: true, missionId);
            return;
        }
        _trainingError = "";
        if (missionId is not (Tm2BasicFlight or Tm3BasicWeaponry or Tm4EnemyEngagement
            or Tm5CommandView or Tm6PracticeArena or Tm8Nanite or Tm10FreeFlight))
        {
            _trainingError = "That mission isn't playable yet; it's coming soon.";
            Console.WriteLine($"[training] {_trainingError}");
            return;
        }
        // The embedded training host realises the mission's universe/creation actions (TR-2b). The
        // variant selects the native map/hull/start-sector (M2/M3: training_1/1010; M4: training_3/1030;
        // M5: training_5/1050; M6+M10: training_6/2080; M8: training_nan/1051).
        var variant = missionId switch
        {
            Tm2BasicFlight => TrainingHost.BasicFlight,
            Tm3BasicWeaponry => TrainingHost.BasicWeaponry,
            Tm4EnemyEngagement => TrainingHost.EnemyEngagement,
            Tm5CommandView => TrainingHost.CommandView,
            Tm6PracticeArena => TrainingHost.PracticeArena,
            Tm8Nanite => TrainingHost.Nanite,
            Tm10FreeFlight => TrainingHost.FreeFlight,
            _ => TrainingHost.BasicFlight,
        };
        // P749 — a missing training core/map (or any start-time asset fault) must
        // NOT kill the app: it returns to the chooser with a clear message and
        // points the player at the log. Never leaves a half-built host behind.
        var started = TryStartTraining(variant, v => TrainingHost.Start(v));
        if (started.Error is not null)
        {
            _embeddedHost = null;
            _trainingError = started.Error;
            Diagnostics.CrashLog.SetMode("training-error");
            return;
        }
        _embeddedHost = started.Host!;
        Diagnostics.CrashLog.SetMode($"training:{variant.GameName}");
        _embeddedHost.StartBackgroundTicker();

        var facilities = CreateTrainingFacilities(missionId);

        var site = new ClientTrainingSite(facilities);
        var mission = CreateTrainingMission(missionId, site);
        // Bind the active mission to the embedded host so Mission 6's kill goals read the real
        // kill-credit events the server raises (ShipKilled routing).
        _embeddedHost.SetActiveMission(mission);
        var session = new TrainingSession(site, mission);

        TrainingController.BeginMission();
        TrainingOwnState.Reset();
        TrainingOverlay.Current = 0;
        _trainingInputGate = new TrainingInputGate(session.RecordKeyPress);
        Rendering.KeyBindingStore.Instance.Map.ActionFilter = _trainingInputGate.Approves;
        // The mission's CreateMission reads the player's ship, which only exists once the
        // loopback join below has replicated it — native runs StartMission →
        // SetupShipAndCamera before the mission script (WinTrek.cpp:7401-7421). Starting it
        // here crashed every mission that touches the player ship at creation ("no player
        // ship in the replicated world", mini client 2026-10-27); UpdateTraining starts it
        // on the first frame the ship is there.
        _trainingSession = session;
        _trainingStartDeadline = TrainingStartTimeoutSeconds;
        _trainingMissionId = missionId;
        _trainingSkipPostSlideshow = false;
        _trainingClock = TrainingClock.Advance(0);

        // Native marks training as begun so the first-run suggestion stops appearing
        // (Ui.ShowStartupTrainingSuggestion cleared, introscreen.cpp:1055 / trainingscreen.cpp:169).
        var prefs = IntroPreferences.Load();
        if (prefs.ShowStartupTrainingSuggestion)
        {
            prefs.ShowStartupTrainingSuggestion = false;
            prefs.Save();
        }

        _returnToIntroOnFailure = true;
        Console.WriteLine($"[training] Mission {missionId} ({variant.GameName}) started on the embedded host");

        // P785 — the real-client launch (native StartTraining, WinTrek.cpp:7401-7421:
        // StartMission → screen(ScreenIDCombat) → SetupShipAndCamera). The client joins
        // its own embedded host exactly like a skirmish (loopback connect), so the
        // lesson runs against the live client: real session, real flight input, real
        // HUD. The training host's mission is already Stage=Started, so side-select
        // auto-seats and ready-check releases straight into flight.
        _trainingAutoJoin = true;
        _trainingSideRequested = false;
        _ownReady = true;
        _disconnectTransitioned = false;
        _readyConfirmedShip = null;
        _nextMatchGeneration = _session.World.MissionGeneration;
        BeginConnect("127.0.0.1", _embeddedHost.Port);
    }

    internal ClientTrainingFacilities CreateTrainingFacilities(int missionId) => new ClientTrainingFacilities
        {
            World = () => _session.World,
            Session = _session,
            ClockSeconds = () => _trainingClock,
            FrameDeltaSeconds = () => _trainingFrameDelta,
            OwnOrientation = () =>
            {
                if (!_ownSim.HaveHull) return null;
                var o = _ownSim.Orientation;
                return (F: ToSys(o.GetForward()), U: ToSys(o.GetUp()), R: ToSys(o.GetRight()));
            },
            // P886b — the player's authoritative local-sim position (P508), so the training
            // geometric conditions (pointing/range) read the truth instead of the lagged
            // replicated broadcast (the real-window pointing-goal flake).
            OwnPosition = () => _ownSim.HaveHull ? ToSys(_ownSim.Position) : null,
            // P881a — the own-ship simulator's real hull kinematics so TurnToAction's
            // turnToFace (Igc/common.cpp:152-252) actually steers the player during the
            // scripted demonstrations (M2 goal 2, M3 goals 5/7/8).
            OwnHullKinematics = () => _ownSim.TurnHull,
            Host = _embeddedHost,
            SetupTraining = SetupTrainingView,
            // P881a — training orders for the own ship use the real client ORDER_CHANGE path.
            OwnOrder = SendTrainingOwnOrder,
            OwnAutopilot = SetTrainingAutopilot,
            SetCameraMode = mode => _cameraMode = TrainingCameraMode(mode),
            ShowPane = mask => ShowTrainingPane(mask),
            DeathCamera = (focus, radius) =>
            {
                _trainingDeadCamera = _cameraMode;
                _trainingDeathCameraActive = true;
                RequestOverrideCamera(new(focus.X, focus.Y, focus.Z), radius, 5);
            },
            RestoreCamera = () =>
            {
                _cameraMode = _trainingDeadCamera;
                _overrideTarget = null;
                _externalCamAlignedFor = null;
                _trainingDeathCameraActive = false;
            },
            PlaySound = soundId => PlayTrainingSound(missionId, soundId),
            IsSoundPlaying = handle => _trainingSounds.TryGetValue(handle, out var sound)
                && sound.State == Microsoft.Xna.Framework.Audio.SoundState.Playing,
            StopSound = StopTrainingSound,
    };

    /// <summary>
    /// P881a — a training <c>SetCommandAction</c> aimed at the player's own ship goes through the
    /// same real ORDER_CHANGE the mouse pick and order menu use (WinTrek.cpp:8820-8854): the
    /// client's command slots and the server's <c>Ship.CommandTarget</c> move together, so the
    /// script's autopilot target and the radar selection agree without any training protocol.
    /// </summary>
    private void SendTrainingOwnOrder(int command, TargetRef? target, int commandId)
    {
        if (_session.World.MyShipId is not { } myId) return;
        // Queued orders originate at the commander/host and await the player's real accept key.
        if (command == TrainingMissionIds.CommandQueued) return;
        short slot = command == TrainingMissionIds.CommandAccepted
            ? OrderChange.CmdAccepted : OrderChange.CmdCurrent;
        if (target is { } t && TrainingTargetObjectType(t.Type) is { } ot)
            _session.SendOrderChange(myId, (short)commandId, slot, ot, (short)t.Id);
        else
            _session.SendOrderChange(myId, OrderChange.CidNone, slot, (IgcObjectType)(-1), -1); // SetTarget(NULL)
    }

    /// <summary>Training <see cref="ObjectType"/> → wire <see cref="IgcObjectType"/> (igc.h:116-129).</summary>
    private static IgcObjectType? TrainingTargetObjectType(ObjectType type) => type switch
    {
        ObjectType.Ship => IgcObjectType.Ship,
        ObjectType.Station => IgcObjectType.Station,
        ObjectType.Asteroid => IgcObjectType.Asteroid,
        ObjectType.Probe => IgcObjectType.Probe,
        ObjectType.Buoy => IgcObjectType.Buoy,
        ObjectType.Warp => IgcObjectType.Warp,
        _ => null,
    };

    /// <summary>
    /// P881a — the training <c>SetAutopilotAction</c> binding: the client flies its own ship
    /// (P508 owns the pose), so the script engages the SAME native autopilot the G key uses
    /// (DockApproach bay entry for a station, warp routing for another sector) toward the
    /// order the script just issued. Completion is the server's real dock latch.
    /// </summary>
    private void SetTrainingAutopilot(TargetRef? target, bool on)
    {
        if (!on)
        {
            if (_autoPilotEngaged) DisengageAutopilot(AutopilotDisengageReason.ToggleOff);
            return;
        }
        if (target is not { } t) return;
        TargetKind? kind = t.Type switch
        {
            ObjectType.Ship => TargetKind.Ship,
            ObjectType.Station => TargetKind.Station,
            ObjectType.Probe => TargetKind.Probe,
            ObjectType.Warp => TargetKind.Warp,
            ObjectType.Buoy => TargetKind.Buoy,
            _ => null,
        };
        if (kind is not { } k) return;
        // The goal constraint repeats SetAutopilot(true), a native idempotent setter
        // (shipIGC.cpp:3974-3978). Preserve the existing planner instead of restarting it.
        if (_autoPilotEngaged && _autoTarget is { } active && active.Kind == k && active.Id == t.Id) return;
        string name = k switch
        {
            TargetKind.Station => _session.World.Stations.TryGetValue((short)t.Id, out var st) ? st.Name : $"station {t.Id}",
            TargetKind.Ship => _session.World.Ships.TryGetValue((short)t.Id, out var sh) ? (sh.CharacterName ?? $"ship {t.Id}") : $"ship {t.Id}",
            TargetKind.Probe => $"probe {t.Id}",
            _ => $"target {t.Id}",
        };
        EngageAutopilotToward(new AutopilotTarget(k, (short)t.Id, name, (short)TrainingMissionIds.CmdGoto, UseDefault: false),
            "training: autopilot");
    }

    /// <summary>
    /// P881a — <c>ShowPaneAction</c>: native turns every pane off then turns the requested
    /// overlay flags on (showpaneaction.cpp:29-38). Maps the training pane masks to the console
    /// pane state both HUD styles render.
    /// </summary>
    private void ShowTrainingPane(int paneMask)
    {
        _consolePanes = new Rendering.ConsolePaneState(
            GameState: false,
            Inventory: (paneMask & TrainingMissionIds.PaneInventory) != 0,
            Nav: false,
            Team: (paneMask & TrainingMissionIds.PaneExpandedTeam) != 0
                ? Rendering.TeamConsoleSize.Expanded : Rendering.TeamConsoleSize.Hidden);
        _minimapExpanded = _consolePanes.Nav;
        _teamPaneVisible = _consolePanes.Team != Rendering.TeamConsoleSize.Hidden;
    }

    /// <summary>Map a native TrekWindow camera id (<c>Treki.h:108-126</c>) to the port's
    /// <see cref="Rendering.CameraMode"/> enum. The port enum is NOT in native order — <c>Command=0</c>,
    /// <c>Cockpit=5</c> (<c>CameraMode.cs</c>) — so a raw cast of the native id (e.g. native
    /// <c>cmCockpit=0</c> → <c>(CameraMode)0 = Command</c>) silently put every training mission into the
    /// command view, whose <c>CommandCameraActive</c> path (<c>Game1.FlightInput.cs:2431</c>) freezes the
    /// flight controls. This is the native-id → port-enum mapping <c>SetDisplayModeAction</c> must use.</summary>
    internal static Rendering.CameraMode TrainingCameraMode(int nativeCamera) => nativeCamera switch
    {
        TrainingMissionIds.CameraCockpit => Rendering.CameraMode.Cockpit,
        TrainingMissionIds.CameraExternalOrbit => Rendering.CameraMode.ExternalOrbit,
        TrainingMissionIds.CameraExternalStation => Rendering.CameraMode.ExternalStation,
        _ => Rendering.CameraMode.Cockpit,
    };

    internal static TrainingMission CreateTrainingMission(int missionId, ITrainingSite site) => missionId switch
        {
            Tm2BasicFlight => (TrainingMission)new Mission2(site),
            Tm3BasicWeaponry => new Mission3(site),
            Tm4EnemyEngagement => new Mission4(site),
            Tm5CommandView => new Mission5(site),
            Tm6PracticeArena => new Mission6(site),
            Tm8Nanite => new Mission8(site),
            Tm10FreeFlight => new Mission10(site),
            _ => throw new InvalidOperationException(),
    };

    /// <summary>Per-frame pump of the active training mission (native TrainingMission::Frame).
    /// The mission clock runs on <see cref="TrainingClock"/> (native's static Time class):
    /// TK_PauseTM freezes it and the embedded host (zlib/ztime.cpp:15-49), the debug
    /// shift scales it. On completion native goes to the post-training slideshow
    /// (trainingmission.cpp:110-115) — or straight back to the training screen when the
    /// skip flag is set — and unpauses the clock (trainingmission.cpp:123-125).</summary>
    private void UpdateTraining(double dt)
    {
        // P886 — the training autoproof (inert unless ALLEGIANCE_TRAIN_PROOF=1): drives the
        // proof-only input seam every frame so the same frame's flight input reads the synthetic
        // keys/state. Runs before the mission pump, like native's input feeding the mission.
        ApplyTrainingProof();
        if (_trainingSession is not { } session) return;
        if (!session.Started)
        {
            if (session.Site.HasPlayerShip)
            {
                session.Start();
                Console.WriteLine($"[training] mission {_trainingMissionId} script started (player ship replicated)");
            }
            else
            {
                _trainingStartDeadline -= dt;
                if (_trainingStartDeadline <= 0)
                    AbortTrainingStart("The training mission could not start: your ship never arrived from the training host. See the log.");
                return;
            }
        }
        _trainingFrameDelta = (float)Math.Clamp(dt, 0.0, 0.25);
        _trainingClock = TrainingClock.Advance(dt);
        if (_embeddedHost is { } host) host.Paused = TrainingClock.IsPaused;
        if (session.Advance())
        {
            Console.WriteLine($"[training] mission complete; final goal={session.Mission.ActiveGoalPath}; survivors={_embeddedHost?.Drones.Values.Count(d => !d.Dead && d.Fraction > 0 && d.Sector is not null)}");
            ClearTrainingSounds();
            TrainingOwnState.Reset();
            TrainingController.EndMission();
            Rendering.KeyBindingStore.Instance.Map.ActionFilter = null;
            _trainingInputGate = null;
            _trainingDeathCameraActive = false;
            if (TrainingClock.IsPaused) TrainingClock.Continue();   // trainingmission.cpp:123-125
            _embeddedHost?.Dispose();
            _embeddedHost = null;
            _trainingSession = null;
            _trainingError = "";
            // Leave the embedded-host match like any session exit (the skirmish path
            // disposes the session; Connect() re-establishes on the next launch).
            _session.Dispose();
            _trainingAutoJoin = false;
            if (_trainingSkipPostSlideshow)
            {
                ReturnToTrainingScreen();
            }
            else
            {
                var name = TrainingScreenModel.NameOf(_trainingMissionId);
                BeginTrainingSlideshow(TrainingSlides.PostMission(_trainingMissionId, name),
                    isPost: true, _trainingMissionId);
            }
        }
    }

    /// <summary>P785 — open a training slideshow (native CreateTrainingSlideshow /
    /// CreatePostTrainingSlideshow, WinTrek.cpp:2473-2510). <paramref name="isPost"/>
    /// selects the dismiss behaviour: post slideshows return to the training screen
    /// (PostTrainingSlideshow::Dismiss, PostTrainingSlideshow.cpp:25-29); the pre
    /// slideshow shows the "Training simulation initiated..." box and starts the
    /// mission (TrainingSlideshow::Dismiss, TrainingSlideshow.cpp:30-50).</summary>
    private void BeginTrainingSlideshow(SlideshowModel slideshow, bool isPost, int missionId)
    {
        _trainingSlideshow = slideshow;
        _trainingSlideshowIsPost = isPost;
        _trainingMissionId = missionId;
        slideshow.Start();
        _slideInputHeld = true;
        _slideSound = _sound?.PlaySlideSound(slideshow.Current?.SoundStem);
        _introView = IntroView.TrainingSlideshow;
        _state = ClientState.Intro;
        Console.WriteLine($"[training] slideshow {(isPost ? "post" : "pre")} for mission {missionId} ({slideshow.Count} slides)");
    }

    /// <summary>P785 — native Slideshow::NextSlide starts each slide's sound
    /// (Slideshow.cpp:105, SIM-0272) — see <see cref="Audio.SoundManager.PlaySlideSound"/>.
    /// </summary>
    /// (Slideshow.cpp:123-146), any key advances immediately (:147-150,179-183), and
    /// dismissal routes per the subclass (start the mission / back to the training
    /// screen).</summary>
    private void UpdateTrainingSlideshow(KeyboardState ks)
    {
        if (_trainingSlideshow is not { } slide) return;
        // The current clip finished → arm the 1 s advance timer (Slideshow.cpp:127-136).
        if (_slideSound is { State: Microsoft.Xna.Framework.Audio.SoundState.Stopped })
        {
            _slideSound.Dispose();
            _slideSound = null;
            slide.OnSoundFinished();
        }
        slide.Tick(1.0 / 60.0);   // timer path
        bool advanced = false;
        // Native advances on a key-DOWN event or a click (Slideshow.cpp:147-150,
        // 179-183), not while a key is held: latch the edge, and start latched so the
        // Enter that opened the slideshow does not skip slide 1.
        bool anyDown = ks.GetPressedKeyCount() > 0
            || Microsoft.Xna.Framework.Input.Mouse.GetState().LeftButton == ButtonState.Pressed;
        bool anyKey = anyDown && !_slideInputHeld;
        _slideInputHeld = anyDown;
        if (anyKey && !slide.Dismissed)
        {
            slide.OnKey();
            advanced = true;
        }
        if (advanced && !slide.Dismissed)
        {
            _slideSound?.Stop();
            _slideSound?.Dispose();
            _slideSound = _sound?.PlaySlideSound(slide.Current?.SoundStem);
        }
        if (slide.Dismissed)
        {
            _slideSound?.Stop();
            _slideSound?.Dispose();
            _slideSound = null;
            bool wasPost = _trainingSlideshowIsPost;
            int missionId = _trainingMissionId;
            _trainingSlideshow = null;
            if (wasPost)
            {
                ReturnToTrainingScreen();
            }
            else
            {
                // TrainingSlideshow::Dismiss (TrainingSlideshow.cpp:30-50): the
                // "Training simulation initiated..." box, then the mission — which
                // connects the client to the embedded host (native screen(ScreenIDCombat)).
                Console.WriteLine("[training] Training simulation initiated...");
                BeginTrainingMission(missionId);
                if (_trainingError != "") _introView = IntroView.Training;   // asset fault → back to the list
            }
        }
    }

    /// <summary>Native OnButtonBack destination (trainingscreen.cpp:160-164) and the
    /// post-slideshow Dismiss destination (PostTrainingSlideshow.cpp:24): the training
    /// screen — the port's IntroView.Training (ScreenIDTrainScreen).</summary>
    /// <summary>The pending mission start timed out (no player ship from the embedded host):
    /// tear the training session down like a completed one and return to the chooser with
    /// a message instead of crashing (P749 rule: a training start fault never kills the app).</summary>
    private void AbortTrainingStart(string error)
    {
        Console.WriteLine($"[training] {error}");
        ClearTrainingSounds();
        TrainingOwnState.Reset();
        Allegiance.ClientCore.Training.TrainingController.EndMission();
        Rendering.KeyBindingStore.Instance.Map.ActionFilter = null;
        _trainingInputGate = null;
        _trainingDeathCameraActive = false;
        if (TrainingClock.IsPaused) TrainingClock.Continue();
        _embeddedHost?.Dispose();
        _embeddedHost = null;
        _trainingSession = null;
        _session.Dispose();
        _trainingAutoJoin = false;
        _trainingError = error;
        Diagnostics.CrashLog.SetMode("training-error");
        ReturnToTrainingScreen();
    }

    private void ReturnToTrainingScreen()
    {
        _introView = IntroView.Training;
        _state = ClientState.Intro;
    }

    /// <summary>P785 — the six training keys, routed from the flight input exactly as
    /// native's key filter does (WinTrek.cpp:8753-8769): while a mission is running
    /// every one of these presses goes to Training::RecordKeyPress first; a key the
    /// mission rejects is swallowed. Native binds CommandPauseTM to Ctrl+Pause,
    /// CommandFinishTM to End, the clock keys to Ctrl+F10/F11/F12 and CommandSkipTMGoal
    /// to numpad-decimal (defaultinputmap.mdl keyCommandMap).</summary>
    private void UpdateTrainingKeys(KeyboardState ks)
    {
        if (!Allegiance.ClientCore.Training.TrainingController.IsTraining) return;
        if (_trainingInputGate is { } gate)
        {
            gate.Update(ks, Rendering.KeyBindingStore.Instance.Map);
            return;
        }
        RouteTrainingKey(ks, "PauseTM", KeyPauseTm, ref _pauseTmWasDown);
        RouteTrainingKey(ks, "FinishTM", KeyCommandFinishTm, ref _finishTmWasDown);
        RouteTrainingKey(ks, "SkipTMGoal", KeySkipTmGoal, ref _skipTmGoalWasDown);
        RouteTrainingKey(ks, "IncreaseTMClockSpeed", KeyIncreaseTMClockSpeed, ref _tmClockUpWasDown);
        RouteTrainingKey(ks, "DecreaseTMClockSpeed", KeyDecreaseTMClockSpeed, ref _tmClockDownWasDown);
        RouteTrainingKey(ks, "ResetTMClockSpeed", KeyResetTMClockSpeed, ref _tmClockResetWasDown);
    }

    private void RouteTrainingKey(KeyboardState ks, string action, int trekKey, ref bool wasDown)
    {
        bool down = ActionDown(ks, action);
        if (down && !wasDown)
            RecordTrainingKey(trekKey);   // consumed/rejected inside the mission framework
        wasDown = down;
    }

    /// <summary>Route a real key press from the flight input into the active mission (RecordKeyPress,
    /// trainingmission.cpp:342-413). Returns whether the press was consumed.</summary>
    private bool RecordTrainingKey(int key)
    {
        return _trainingSession is { } session && session.RecordKeyPress(key);
    }

    private static System.Numerics.Vector3 ToSys(Allegiance.Core.Collision.Vector3 v)
        => new(v.X, v.Y, v.Z);

    private TrainingInputGate? _trainingInputGate;
    private Rendering.CameraMode _trainingDeadCamera;
    private bool _trainingDeathCameraActive;

    // The ready-to-flight handoff runs after UpdateTraining. Preserve native
    // training setup rather than overwriting it with multiplayer launch defaults
    // (training/Training.cpp:95-96; training/trainingmission.cpp:142-149).
    private void ResetInitialFlightState()
    {
        _throttle = TrainingActive ? -1 : Flight.FlightInput.DefaultThrottle;
        _radarMode = TrainingActive ? Rendering.RadarMode.All : Rendering.RadarMode.Default;
        _coastToggled = false;
        _headLookYaw = 0;
        TrainingRadarLod.Current = (int)_radarMode;   // P880a GetRadarLODCondition reads the live mode
    }

    // Shared by Classic and Modern: training/trainingmission.cpp:142-149,
    // training/mission5.cpp:98-106; no style-specific behavior.
    private void SetupTrainingView(int missionId)
    {
        _cameraMode = missionId == 5 ? Rendering.CameraMode.Command : Rendering.CameraMode.ExternalOrbit;
        _radarMode = Rendering.RadarMode.All;
        TrainingRadarLod.Current = (int)_radarMode;
        _consolePanes = default;
        _minimapExpanded = _teamPaneVisible = _investActive = _teleportActive = false;
        _throttle = -1;
    }

    /// <summary>
    /// P749 — guarded training-host start. Runs <paramref name="start"/> and,
    /// on any fault (missing core/map, unreadable asset, boot failure), returns
    /// a clear player-facing message instead of letting the exception kill the
    /// app. The host is left null on failure so the caller returns to the
    /// chooser. Internal so tests can drive the failure path with a throwing
    /// starter (no GraphicsDevice required).
    /// </summary>
    internal static (TrainingHost? Host, string? Error) TryStartTraining(
        TrainingHost.TrainingVariant variant, Func<TrainingHost.TrainingVariant, TrainingHost> start)
    {
        try
        {
            return (start(variant), null);
        }
        catch (Exception ex)
        {
            string error =
                $"Training files are missing from this install: {variant.Map}. Reinstall or report this log.";
            Console.WriteLine($"[training] {error} ({ex.GetType().Name}: {ex.Message})");
            Console.WriteLine("[training] full details in the crash/client log (see docs/reports/P749_CRASH_SAFETY.md)");
            return (null, error);
        }
    }
}
