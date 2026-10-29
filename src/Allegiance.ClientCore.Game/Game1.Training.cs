using Allegiance.ClientCore.Training;
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
    private TrainingSession? _trainingSession;
    private TrainingHost? _embeddedHost;
    private string _trainingError = "";
    /// <summary>Seconds left for the loopback join to replicate the player ship before the
    /// pending mission start gives up (BeginTrainingMission → UpdateTraining).</summary>
    private double _trainingStartDeadline;
    private const double TrainingStartTimeoutSeconds = 20.0;
    private double _trainingClock;
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

        var facilities = new ClientTrainingFacilities
        {
            World = () => _session.World,
            Session = _session,
            ClockSeconds = () => _trainingClock,
            OwnOrientation = () =>
            {
                if (!_ownSim.HaveHull) return null;
                var o = _ownSim.Orientation;
                return (F: ToSys(o.GetForward()), U: ToSys(o.GetUp()), R: ToSys(o.GetRight()));
            },
            Host = _embeddedHost,
            PlaySound = soundId => PlayTrainingSound(missionId, soundId),
            IsSoundPlaying = handle => _trainingSounds.TryGetValue(handle, out var sound)
                && sound.State == Microsoft.Xna.Framework.Audio.SoundState.Playing,
            StopSound = StopTrainingSound,
        };

        var site = new ClientTrainingSite(facilities);
        var mission = missionId switch
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
        // Bind the active mission to the embedded host so Mission 6's kill goals read the real
        // kill-credit events the server raises (ShipKilled routing).
        _embeddedHost.SetActiveMission(mission);
        var session = new TrainingSession(site, mission);

        TrainingController.BeginMission();
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

    /// <summary>Per-frame pump of the active training mission (native TrainingMission::Frame).
    /// The mission clock runs on <see cref="TrainingClock"/> (native's static Time class):
    /// TK_PauseTM freezes it and the embedded host (zlib/ztime.cpp:15-49), the debug
    /// shift scales it. On completion native goes to the post-training slideshow
    /// (trainingmission.cpp:110-115) — or straight back to the training screen when the
    /// skip flag is set — and unpauses the clock (trainingmission.cpp:123-125).</summary>
    private void UpdateTraining(double dt)
    {
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
        _trainingClock = TrainingClock.Advance(dt);
        if (_embeddedHost is { } host) host.Paused = TrainingClock.IsPaused;
        if (session.Advance())
        {
            Console.WriteLine("[training] mission complete");
            ClearTrainingSounds();
            TrainingController.EndMission();
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
        Allegiance.ClientCore.Training.TrainingController.EndMission();
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
