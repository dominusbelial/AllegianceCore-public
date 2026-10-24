using System.Net.Http;
using System.Net.Http.Json;
using System.Linq;
using System.Text;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.QuickChat;
using Allegiance.ClientCore.MonoGame.Capture;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using NumericsVec2 = System.Numerics.Vector2;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// The flightinput slice of <see cref="Game1"/> (P384 stage-1 split from <c>Game1.cs</c>).
/// Partial class — shares the <c>Game1</c> type, so private field/method access
/// across the split files is unaffected.
/// </summary>
public sealed partial class Game1
{


    /// <summary>
    /// P380 — the live MonoGame joystick state for the first connected joystick,
    /// or <c>null</c> when no joystick is connected (or the platform reports the
    /// joystick API unsupported). The connected index is cached so the cheap
    /// path is a single <c>GetState</c>; if it stops reporting connected
    /// (hot-unplug), the cache is dropped and the list re-scanned so a
    /// reconnect is picked up next frame. When it returns <c>null</c> the
    /// keyboard-only flight path is untouched.
    /// </summary>
    private JoystickState? CurrentJoystickState()
    {
        if (!Joystick.IsSupported)
        {
            _joystickIndex = null;
            return null;
        }

        if (_joystickIndex is int cached && Joystick.GetCapabilities(cached).IsConnected)
            return Joystick.GetState(cached);

        _joystickIndex = null;
        for (int i = 0; i <= Math.Max(Joystick.LastConnectedIndex, 0); i++)
        {
            if (Joystick.GetCapabilities(i).IsConnected)
            {
                _joystickIndex = i;
                return Joystick.GetState(i);
            }
        }
        return null;
    }

    /// <summary>P380 — whether the given joystick button is currently pressed.</summary>
    private static bool IsJoystickButtonPressed(ButtonState[] buttons, int index) =>
        index >= 0 && index < buttons.Length && buttons[index] == ButtonState.Pressed;

    // ── P499 — Bluetooth gamepad input (Android head + any desktop pad) ──────
    //
    // The MonoGame GamePad API is read once per frame from PlayerIndex.One.
    // Analog sticks are folded into the flight ControlData through the SAME
    // JoystickFlightController the P380 joystick path uses (deadzone/rescale +
    // throttle latch come free); buttons reuse the keyboard's fire/booster/
    // throttle-step semantics. A connected pad is used whenever the USB
    // joystick did not take the turn axes.

    /// <summary>Edge-latch flags for the pad shoulder throttle steps (RB up /
    /// LB down), mirroring the = / - key edge-detection so holding a shoulder
    /// button steps once, never per-frame.</summary>
    private bool _padRbWasDown, _padLbWasDown;

    /// <summary>P499 — the live MonoGame gamepad state for PlayerIndex.One, or
    /// <c>null</c> when no pad is connected. Read fresh each frame (the cheap
    /// single-GetState path, exactly like the joystick cache).</summary>
    private GamePadState? CurrentPadState()
    {
        try
        {
            var gp = GamePad.GetState(PlayerIndex.One);
            return gp.IsConnected ? gp : null;
        }
        catch
        {
            // Some headless/remote hosts throw on a missing pad; degrade to none.
            return null;
        }
    }

    /// <summary>
    /// P499 — post-Translate gamepad override over a keyboard-based command,
    /// the same shape as the P380 joystick block above: a connected pad's
    /// analog sticks override the turn axes (when outside deadzone) and latch
    /// the throttle; its buttons OR into stateM alongside the keyboard (fire /
    /// booster, never replace); its shoulder buttons step the P445 throttle
    /// axis exactly like the = / - keys. Returns the (possibly unchanged)
    /// command.
    /// </summary>
    private FlightCommand ApplyGamePadFlight(FlightCommand cmd)
    {
        var pad = CurrentPadState();
        if (pad is not { } gp || !gp.IsConnected)
            return cmd;

        var raw = GamePadFlightRoles.Gather(
            gp.ThumbSticks.Left.X, gp.ThumbSticks.Left.Y,
            gp.ThumbSticks.Right.X, gp.ThumbSticks.Right.Y,
            GamePadFlightRoles.DefaultLayout);
        var gpr = _gamePadFlight.Update(raw);
        cmd = cmd with
        {
            Controls = cmd.Controls with
            {
                Yaw = gpr.YawActive ? gpr.Yaw : cmd.Controls.Yaw,
                Pitch = gpr.PitchActive ? gpr.Pitch : cmd.Controls.Pitch,
                Roll = gpr.RollActive ? gpr.Roll : cmd.Controls.Roll,
                Throttle = gpr.ThrottleOverride ?? cmd.Controls.Throttle,
            },
        };

        // Buttons: A (or right trigger) fires, B (or left trigger) boosts — the
        // same P221/P220 held-continuous semantics as Space / Shift. Use
        // FireContribution so group-fire/single-weapon selection still applies.
        // (MonoGame: face buttons live on gp.Buttons, triggers on gp.Triggers.)
        var b = gp.Buttons;
        if (b.A == ButtonState.Pressed || gp.Triggers.Right > 0.5f)
            cmd = cmd with { StateM = cmd.StateM | FlightInput.FireContribution(true, _groupFire, _selectedMount) };
        if (b.B == ButtonState.Pressed || gp.Triggers.Left > 0.5f)
            cmd = cmd with { StateM = cmd.StateM | ShipControlState.AfterburnerButton };

        // Shoulder throttle steps (RB up / LB down) — edge-detected like the
        // = / - keys so holding never re-fires, stepping the same P445 axis.
        if (b.RightShoulder == ButtonState.Pressed && !_padRbWasDown)
        {
            _throttle = FlightInput.StepThrottleUp(_throttle);
            Console.WriteLine($"[monogame] pad RB -> throttle {_throttle:F2}");
        }
        if (b.LeftShoulder == ButtonState.Pressed && !_padLbWasDown)
        {
            _throttle = FlightInput.StepThrottleDown(_throttle);
            Console.WriteLine($"[monogame] pad LB -> throttle {_throttle:F2}");
        }
        _padRbWasDown = b.RightShoulder == ButtonState.Pressed;
        _padLbWasDown = b.LeftShoulder == ButtonState.Pressed;

        return cmd;
    }

    /// <summary>
    /// P501 — the T target-cycle action, extracted from the key handler so the
    /// touch TargetCycle button and the T key share ONE implementation. Cycles to
    /// the next in-view candidate, nearest-to-crosshair first (ships except
    /// self/dead take priority; stations/asteroids only when no ship is in view).
    /// </summary>
    private void HandleTargetCycleKey() => SelectRemainderTarget("Target", StepDirection.Next);

    /// <summary>
    /// P501 — the G autopilot toggle action, extracted from the key handler so
    /// the touch Autopilot button and the G key share ONE implementation. Toggles
    /// the target autopilot: engaged → disengage; else pick/engage toward the
    /// selected target (re-picking a self target as in P505). Does NOT include the
    /// G key's starmap-"goto" branch (keyboard-only, needs the expanded map).
    /// </summary>
    private void ToggleAutopilotForTarget()
    {
        if (_autoPilotEngaged)
        {
            DisengageAutopilot(AutopilotDisengageReason.ToggleOff);
            return;
        }

        var world = _session.World;
        short? ownId;
        lock (_session.Gate) ownId = world.MyShipId;
        if (_autoTarget is { } sel && ownId is { } myOwnId && !IsUsableAutopilotTarget(sel, myOwnId))
        {
            _autoTarget = TryOwnPose(out var rPos, out var rOrient, out _) ? PickNearestTarget(rPos, rOrient) : null;
            Console.WriteLine(_autoTarget is { } rp
                ? $"[monogame] autopilot: refusing self target; re-picked {rp.Name} ({rp.Kind}#{rp.Id})"
                : "[monogame] autopilot: refusing self target; no real target in view");
        }
        // Implicit nearest pick if none selected yet, then engage.
        if (_autoTarget is not { } tgt && TryOwnPose(out var pPos, out var pOrient, out _))
            _autoTarget = PickNearestTarget(pPos, pOrient);
        if (_autoTarget is { } at)
        {
            EngageAutopilotToward(at, "");
        }
    }

    /// <summary>
    /// P499 — minimal digital navigation read for the docked shop / teleport /
    /// invest panes: the pad's DPAD + face buttons are projected onto the same
    /// Up / Down / Select / Back verbs the keyboard already handles (the pane
    /// handlers merge these with their key checks), so a Bluetooth pad can
    /// browse and launch at least minimally without a keyboard. Also exposed
    /// for the side-select / ready-check screens. True only for connected pads;
    /// all-false when none is present (no behavioural change on desktop
    /// without a pad).
    /// </summary>
    public readonly record struct PadNav(
        bool Up, bool Down, bool Left, bool Right,
        bool Accept, bool Back, bool Extra, bool Extra2 = false,
        bool LeftShoulder = false, bool RightShoulder = false)
    {
        /// <summary>P501 — the X face button (mission-owner force-ready on the
        /// ready-check screen).</summary>
        public bool X => Extra2;
    }

    /// <summary>P499 — snapshot the current pad's DPAD/face/start-back buttons
    /// for pane navigation. <see cref="PadNav.Extra"/> maps to Y (the pane
    /// handlers can use it as an alternate confirm/toggle where useful);
    /// <see cref="PadNav.Extra2"/> maps to X.</summary>
    private PadNav GamePadNavNow()
    {
        var pad = CurrentPadState();
        if (pad is not { } gp || !gp.IsConnected)
            return new PadNav();
        var b = gp.Buttons;
        // MonoGame 3.8: the DPad is a separate GamePadDPad struct on the state.
        var dp = gp.DPad;
        return new PadNav(
            Up: dp.Up == ButtonState.Pressed,
            Down: dp.Down == ButtonState.Pressed,
            Left: dp.Left == ButtonState.Pressed,
            Right: dp.Right == ButtonState.Pressed,
            Accept: b.A == ButtonState.Pressed,
            Back: b.B == ButtonState.Pressed,
            Extra: b.Y == ButtonState.Pressed,
            Extra2: b.X == ButtonState.Pressed,
            // P519 — the shoulder buttons switch the docked HULLS/PARTS tab.
            LeftShoulder: b.LeftShoulder == ButtonState.Pressed,
            RightShoulder: b.RightShoulder == ButtonState.Pressed);
    }

    protected override void Update(GameTime gameTime)
    {
        using var frameUpdate = new Diagnostics.ClientFrameTelemetry.UpdateScope(_frameTelemetry);
        UpdateSkirmish(Keyboard.GetState());
        if (UpdateBenchmark(ref gameTime)) { base.Update(gameTime); return; }
        // P789 — the always-on session recorder: one O(1), zero-allocation
        // record per frame in every match (pilot and spectator). No locks, no
        // I/O — see Game1.SessionPerformance.cs for the threading contract.
        UpdateSessionPerformance();
        if (LocalSpectating)
        {
            UpdateLocalSpectator(gameTime, Keyboard.GetState());
            base.Update(gameTime);
            return;
        }
        // TR-2 — pump the active training mission every frame (native TrainingMission::Frame).
        UpdateTraining(gameTime.ElapsedGameTime.TotalSeconds);
        _sound?.UpdateAudioFrame((float)gameTime.ElapsedGameTime.TotalSeconds);
        UpdateSoundEvents();
        _flightGc.SetFlying(_state == ClientState.Flying && !_dockedActive
            && !_graphicsSettingsVisible && !_keyBindVisible);
        // Also runs in dock/lobby/disconnect states, so the loop cannot linger.
        // P789 — the throttled (≤10 Hz) ship-count sample rides this EXISTING
        // per-frame lock; the recorder never takes _session.Gate itself.
        lock (_session.Gate)
        {
            _sound?.UpdateShipWarnings(_session.World.ShipWarnings.Mask, _session.World.ShipWarnings.TakeRisingEdges());
            SampleSessionPerformancePilotLocked(_session.World);
        }
        _nowSeconds += gameTime.ElapsedGameTime.TotalSeconds;
        UpdateConsoleOverlay();   // HUD-10 — native overlay-mask change + pane slides
        // P618 — advance the sim-scaled game clock (wall × live server time
        // scale), including the embedded single-player pause at scale=0.
        // Clamp prevents malformed rates from running effects backwards. A not-yet-
        // connected session (no world) simply advances at full speed.
        double ts = 1.0;
        if (_session.World is { } w) ts = w.TimeScale;
        _gameSeconds += gameTime.ElapsedGameTime.TotalSeconds * Math.Clamp(ts, 0.0, 1.0);
        // P573 — frame-time over-budget fallback for the Volumetric tier (drop a
        // tier once, logged). Skipped during --auto proof runs so a recording
        // renders the requested tier consistently end to end.
        if (_autoPlan is null)
            EnsureVolumetricBudget(gameTime.ElapsedGameTime.TotalSeconds);
        // P508 — keep the own-ship server clock fed in every state so a
        // server-converted SHIP_UPDATE time is ready by undock. Bootstrap once
        // per connection; subsequent echoes come from the session keep-alive.
        PumpServerClock();
        var world = _session.World;
        UpdateMissionCatalog();
        UpdateCommanderBuildOrders();
        if (_nextMatchGeneration != world.MissionGeneration)
        {
            _nextMatchGeneration = world.MissionGeneration;
            _briefingDismissed = false;
            _missionCountdownSound.Reset();
            _gameOverCuePlayed = false;
            _ownReady = false;
            // P789 — a rotated next match is a NEW match: mark the recorder
            // restart so each match's numbers (and score) stay per-match. The
            // restart itself waits until the game-over panel is gone.
            if (_sessionRecording) _perfRestartPending = true;
        }
        // P660: the server holds flight through intermission and briefing.
        // Stay on the scoreboard during intermission, then show ReadyCheck's
        // normal briefing until the authoritative Started transition releases it.
        if (_state == ClientState.Flying && world.MissionGeneration > 0
            && world.Mission?.Stage is (int)MissionStage.NotStarted or (int)MissionStage.Starting)
            _state = ClientState.ReadyCheck;
        var ks = _benchmark is null ? Keyboard.GetState() : new KeyboardState();
        if (UpdateGraphicsSettings(ks))
        {
            // A modal settings panel must release the last movement/fire command.
            if (_state == ClientState.Flying && !_dockedActive)
            {
                var neutral = new ControlData(0f, 0f, 0f, -1f);
                if (IsTurretGunner()) UpdateTurretGunner(neutral, 0, (float)gameTime.ElapsedGameTime.TotalSeconds);
                else _session.CommandControls(neutral);
            }
            base.Update(gameTime);
            return;
        }

        if (_skirmish?.Paused == true)
        {
            bool escape = ActionDown(ks, "MainMenu");
            if (escape && !_escWasDown) ToggleMainMenu();
            _escWasDown = escape;
            UpdateMainMenu(ks);
            base.Update(gameTime);
            return;
        }

        // appmsg.cpp:2501-2528: mission boot/quit returns to the browser.
        if (_state is ClientState.Flying or ClientState.ReadyCheck or ClientState.SideSelect
            && world.OwnMissionDeparture is { } departure)
        {
            _state = ClientState.Lobby;
            // P758 — native OnQuitMission's QSR_* → strMessage switch
            // (trekigc.cpp:5265-5335); message-less reasons fall back to a plain
            // "You left the mission." lobby line.
            _lobbyError = ClientWorld.OwnBootMessage(departure.Reason) ?? "You left the mission.";
            _session.Dispose();
        }

        // P320 — session-loss detection (see ConnectionSession.SessionLost). The
        // transport reports the established connection to the server dropped
        // (server crash / network hiccup). Bail out of live play BEFORE any state
        // logic runs: stop sending controls (the socket is gone — Send is a no-op
        // now) and flip to the disconnect screen instead of silently rendering a
        // frozen world. Latched so this runs exactly once; Esc still quits.
        if (_session.Lost && !_disconnectTransitioned)
        {
            _disconnectTransitioned = true;
            Console.WriteLine("[monogame] SESSION LOST — connection to server dropped; controls disabled");
            if (_state is ClientState.Flying or ClientState.ReadyCheck or ClientState.SideSelect)
            {
                _state = ClientState.Disconnected;
            }
            else if (_state == ClientState.Connecting)
            {
                // Dropped before ever establishing in-game play — revert to the
                // server browser like any join failure, not to a disconnect screen.
                _state = ClientState.Lobby;
                _lobbyError = "Connection was lost before the game could be entered.";
            }
        }

        // P481 — the game-over screen's Back action (native
        // GameOverScreen::OnButtonBack, gameoverscreen.cpp:609-625). The port's
        // game-over panel is keyboard-driven (Wayland, no reliable mouse), so
        // Backspace is the "Back" key, edge-detected and gated to when the
        // game-over panel is actually showing (and not while composing chat /
        // in the quick-chat menu, where Backspace deletes text). On stage Over
        // it leaves the mission (LOG OFF → server browser); otherwise it clears
        // the panel and enters the team lobby (ready-check for a rotated next
        // game, side-select for an eliminated side).
        _menuConsumedFrame = _mainMenuVisible;
        bool back = ActionDown(ks, "GameOverBack");
        bool goBackEdge = back && !_gameOverBackWasDown;
        if (goBackEdge
            && _state is ClientState.Flying or ClientState.ReadyCheck
            && _session.World.GameOverResult is { }
            && !_chatActive && !_qcMenuOpen)
        {
            HandleGameOverBack();
        }
        _gameOverBackWasDown = back;

        // P752 — the game-over tables' column-header clicks (native
        // OnButtonBarStats/OnButtonBarStatsSide, gameoverscreen.cpp:840-874,
        // 922-956): clicking a data column sorts by it (descending), clicking
        // the same column again reverses it, and the position column re-applies
        // the current sort. The hit rects come from the renderer's last draw so
        // a click always lands where the header was painted.
        if (_state is ClientState.Flying or ClientState.ReadyCheck
            && _session.World.GameOverResult is { }
            && !_chatActive && !_qcMenuOpen)
        {
            HandleGameOverTableClick();
        }

        if (HandleHangarQuitInput(ks, _chatActive || _qcMenuOpen))
        {
            // The native quit popup owns Enter/Escape before the normal menu.
            _escWasDown = ActionDown(ks, "MainMenu");
            base.Update(gameTime);
            return;
        }

        // Escape: while composing chat it cancels the input line, closes the
        // briefing popup, backs out of quick-chat, or clears the lobby filter
        // — it never quits the game anymore. Domingo hit it by accident
        // mid-flight one too many times; quitting now requires the
        // Alt+F4 combo below, matching the standard OS convention, so a
        // single stray Escape can no longer end the session. Edge-detected
        // so a single held Escape can never re-trigger across frames.
        bool esc = ActionDown(ks, "CancelChat") || ActionDown(ks, "MainMenu");
        // Gamepad Back is edge-detected like Escape so one held press closes
        // one pane instead of walking down the whole chain frame by frame.
        bool backDown = GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed;
        bool backBtn = backDown && !_padBackWasDown;
        _padBackWasDown = backDown;
        if ((esc && !_escWasDown) || backBtn)
        {
            // UX-1 — the docked-screen panes join the Escape chain. Investment
            // closes like native PurchasesPaneImpl::OnButtonBack (loadout.cpp:916-920,
            // TurnOffOverlayFlags(ofInvestment)); teleport like TeleportPane::OnButtonBack
            // (teleportpane.cpp:363-366); the loadout/shop like LoadoutImpl::OnButtonBack
            // (load.cpp:1533-1536, SetViewMode(vmHangar) — the port's _hangarOnly hangar).
            bool investmentOpen = _investActive;
            bool teleportOpen = _teleportActive;
            bool loadoutOpen = _dockedActive && _cameraMode != CameraMode.Command && !_hangarOnly && _shopModel is not null;
            var cancel = MainMenuRules.Escape(IsBriefingVisible(out _),
                // P784 — every live compose state owns Escape (native console
                // VK_ESCAPE console.cpp:2375-2385; the mouse-command selection
                // is quit as an adaptation of OnSwitchViewMode's cleanup).
                _chatActive || LiveComposeState is NativeComposeState.Command or NativeComposeState.MouseCommand,
                _qcMenuOpen,
                _keyBindVisible, _helpVisible, _creditsVisible, ActionDown(ks, "MainMenu") || backBtn,
                _cameraMode == CameraMode.ExternalOverride || _state == ClientState.Connecting || _session.World.GameOverResult is not null,
                investment: investmentOpen, teleport: teleportOpen, loadout: loadoutOpen,
                mainMenuOpen: _mainMenuVisible, playerPings: _playerPingsVisible);
            if (cancel == EscapeUiAction.None && _state == ClientState.Lobby)
            {
                // P761 — the server browser's popups back out before the filter:
                // the find-player popup, then the details panel, then the filter.
                if (_findPlayerActive) { _findPlayerActive = false; _findBuffer.Clear(); }
                else if (_detailsActive) _detailsActive = false;
                else if (_filterActive) ClearFilter();
            }
            switch (cancel)
            {
                case EscapeUiAction.Briefing: DismissBriefing(); break;
                case EscapeUiAction.Chat:
                    // P784 — native VK_ESCAPE (console.cpp:2375-2385): Command
                    // steps BACK to Chat with the typed text cleared; every
                    // other compose state quits composing.
                    if (LiveComposeState == NativeComposeState.Command)
                        SetComposeState(NativeComposeState.Chat);
                    else
                        SetComposeState(NativeComposeState.NotComposing);
                    break;
                case EscapeUiAction.QuickChat: BackQuickChat(); break;
                case EscapeUiAction.Controls: _keyBindVisible = false; break;
                case EscapeUiAction.Help: _helpVisible = false; break;
                case EscapeUiAction.CloseMainMenu: if (_mainMenuVisible) ToggleMainMenu(); break;
                case EscapeUiAction.PlayerPings: _playerPingsVisible = false; break;
                case EscapeUiAction.Investment:
                    _investActive = false;
                    Console.WriteLine("[monogame] investment pane -> closed by Escape");
                    break;
                case EscapeUiAction.Teleport:
                    _teleportActive = false;
                    Console.WriteLine("[monogame] teleport pane -> closed by Escape");
                    break;
                case EscapeUiAction.Loadout:
                    _hangarOnly = true;
                    Console.WriteLine("[monogame] loadout -> hangar by Escape");
                    break;
                case EscapeUiAction.Credits: _creditsVisible = false; break;
                case EscapeUiAction.ToggleMainMenu:
                    if (_state == ClientState.Lobby && _filterActive) { ClearFilter(); break; }
                    ToggleMainMenu();
                    break;
                case EscapeUiAction.None:
                    // P761 — Esc cancels a pending profile edit (native's Apply
                    // is the only commit; backing out leaves the stored text).
                    if (_pilotProfile.Editing) _pilotProfile.Cancel();
                    break;
            }
        }
        _escWasDown = esc;

        // Alt+F4: the actual quit combo now (replacing bare Escape above).
        // Handled in-app rather than left to the window manager, so it
        // quits reliably regardless of the desktop environment. Edge-
        // detected on F4 so holding it can't re-fire.
        bool altF4 = KeyOf("Quit").Matches(ks);
        if (altF4 && !_altF4WasDown)
        {
            Exit();
        }
        _altF4WasDown = altF4;

        // P257 — H keybinding-reference overlay. Edge-triggered like the other
        // toggles (holding it never re-fires), available in every state (lobby
        // and flight — the panel lists both flight and lobby bindings), and
        // gated behind chat/menu composition so typing never flips it. It is an
        // independent flag: toggling it on/off never disturbs the starmap, HUD
        // or chat-log state, which all resume unchanged when it closes.
        // Rebound from F1 (P258.1, live 2026-09-18): F1 is intercepted by
        // firmware/OS shortcuts on some keyboards before it ever reaches the
        // game — confirmed live, Domingo's own hardware never saw the keydown
        // at all. H (mnemonic: Help) is unclaimed and a normal key every
        // keyboard delivers.
        bool helpKey = ActionDown(ks, "Help");
        if (helpKey && !_helpKeyWasDown && !_chatActive && !_qcMenuOpen)
            ToggleHelpOverlay();
        _helpKeyWasDown = helpKey;

        // P757 — while the help overlay is open it owns the keyboard like the
        // native help pane (effect/help.cpp): the menu-cursor actions walk the
        // manual's topics, the chat-delete key (Backspace) goes Back through
        // the topic history, and the chat page keys scroll the page (native nav
        // pane + Back button + scroll bars). All routed through the live action
        // map, so a rebind is honoured (P625b discipline — no raw literals).
        if (_helpVisible)
        {
            bool hUp = ActionDown(ks, "MenuUp");
            bool hDown = ActionDown(ks, "MenuDown");
            if (hUp && !_helpNavUpWasDown) _helpManual.MoveTopicSelection(-1);
            if (hDown && !_helpNavDownWasDown) _helpManual.MoveTopicSelection(+1);
            _helpNavUpWasDown = hUp; _helpNavDownWasDown = hDown;
            bool hBack = ActionDown(ks, "ChatDelete");
            if (hBack && !_helpNavBackWasDown) _helpManual.Back();
            _helpNavBackWasDown = hBack;
            bool hPgUp = ActionDown(ks, "ChatPageUp");
            bool hPgDn = ActionDown(ks, "ChatPageDown");
            if (hPgUp && !_helpNavPgUpWasDown) _helpManual.ScrollBy(-8, 16);
            if (hPgDn && !_helpNavPgDnWasDown) _helpManual.ScrollBy(+8, 16);
            _helpNavPgUpWasDown = hPgUp; _helpNavPgDnWasDown = hPgDn;
        }

        // P577 — F12 toggles the in-game Credits/Licences screen (the visible
        // CC BY / public-domain attribution for the HD sky's telescope data).
        // Edge-triggered and composing-gated like the H help toggle; scrolling
        // with PageUp/PageDown while it is open.
        bool creditsKey = ActionDown(ks, "Credits");
        if (creditsKey && !_f12WasDown && !_chatActive && !_qcMenuOpen)
        {
            _creditsVisible = !_creditsVisible;
            Console.WriteLine($"[monogame] credits {( _creditsVisible ? "ON" : "OFF" )}");
        }
        _f12WasDown = creditsKey;
        if (_creditsVisible)
        {
            if (ActionDown(ks, "CreditsScrollDown")) _creditsOverlayRenderer.Scroll(+42f);
            if (ActionDown(ks, "CreditsScrollUp")) _creditsOverlayRenderer.Scroll(-42f);
        }

        // P625 — Controls (rebind) popup. F6 opens/closes it (action
        // OpenKeyBindings, resolved from the live map so a rebind of it works).
        // It is independent of the help overlay and gated behind composition so
        // typing can't flip it.
        bool openKeys = ActionDown(ks, "OpenKeyBindings");
        if (openKeys && !_keyBindOpenKeyWasDown && !_chatActive && !_qcMenuOpen)
            ToggleKeyBindings();
        _keyBindOpenKeyWasDown = openKeys;
        if (_keyBindVisible)
            UpdateKeyBindingPopup(ks);

        UpdateMainMenu(ks);
        HandleConsoleKeys(ks, _chatActive || _qcMenuOpen || _keyBindVisible || _mainMenuVisible,
            (float)gameTime.ElapsedGameTime.TotalSeconds);

        HandleCommandKeys(ks, _chatActive || _qcMenuOpen || _keyBindVisible || _mainMenuVisible || _investActive,
            (float)gameTime.ElapsedGameTime.TotalSeconds);

        // P265 — B scoreboard toggle (mnemonic: BoaRd / scoreBoaRd). B is an
        // unclaimed single letter (grep-verified, P257 KeyBindingTable: the only
        // letters in use are A C D E G H K M N Q R S T V W + modifiers). Edge-
        // triggered and composing-gated like the H help toggle, independent of
        // the starmap/help flags so the prior view resumes on close.
        bool scoreKey = ActionDown(ks, "Scoreboard");
        // M-2 — also gate on !_dockedActive: while the docked/teleport pane is
        // open, B is bound to "back to hangar" (HandleDockedScreen), so a single
        // press must close the pane only, not ALSO flip the scoreboard.
        if (scoreKey && !_scoreboardKeyWasDown && ScoreboardToggleAllowed(_chatActive, _qcMenuOpen, _dockedActive))
        {
            _scoreboardVisible = !_scoreboardVisible;
            Console.WriteLine($"[monogame] scoreboard {( _scoreboardVisible ? "ON" : "OFF" )}");
        }
        _scoreboardKeyWasDown = scoreKey;

        // P275 — P Pilot Info toggle (mnemonic: Pilot). P is an unclaimed single
        // letter (grep-verified, P257 KeyBindingTable: no P binding anywhere in
        // Game1; in-use singles are A C D E F G H K L M N O Q R S T U V W + B).
        // Edge-triggered and composing-gated like the H/B toggles, independent of
        // the scoreboard/starmap/help flags so the prior view resumes on close.
        bool pilotKey = ActionDown(ks, "PilotInfo");
        if (pilotKey && !_pilotInfoKeyWasDown && !_chatActive && !_qcMenuOpen)
        {
            _pilotInfoVisible = !_pilotInfoVisible;
            if (!_pilotInfoVisible) _pilotProfile.Cancel();   // closing the sheet aborts a pending edit
            Console.WriteLine($"[monogame] pilotinfo {( _pilotInfoVisible ? "ON" : "OFF" )}");
        }
        _pilotInfoKeyWasDown = pilotKey;

        // P761 — the character sheet's profile editor (native CharInfoScreen's
        // Edit/Apply pair, CharInfoScreen.cpp:837-880): Ctrl+P toggles edit→
        // apply; while editing, Enter applies and Esc cancels (both handled in
        // UpdateFlying / the Escape chain), typing goes through OnTextInput.
        // Gated on the sheet being visible and no modal owning the keys — a
        // disabled Apply does nothing natively either.
        bool profileKey = ActionDown(ks, "PilotProfileEdit");
        if (profileKey && !_pilotProfileKeyWasDown && _pilotInfoVisible && !_chatActive && !_qcMenuOpen
            && !_keyBindVisible && !_mainMenuVisible && !_menuConsumedFrame)
        {
            if (_pilotProfile.Editing)
            {
                string? applied = _pilotProfile.Apply();
                if (applied is not null)
                {
                    var prefs = IntroPreferences.Load();
                    prefs.PilotDescription = applied;
                    prefs.Save();
                }
                Console.WriteLine($"[monogame] pilot profile applied ({applied?.Length ?? 0} chars)");
            }
            else
            {
                _pilotProfile.BeginEdit();
                Console.WriteLine("[monogame] pilot profile editing");
            }
        }
        _pilotProfileKeyWasDown = profileKey;

        // P572 — live-proof automation: feed world observations + drain the
        // machine's due actions into the pending slots the handlers below
        // consume, BEFORE any state logic runs this frame.
        FeedAutoProofObservations();

        UpdateMissionCountdownSound(world);

        // Top-level state dispatch: the server browser (default entry) and the
        // transient connecting state don't run any flight logic; only Flying does.
        if (_state != ClientState.Flying)
        {
            if (_state == ClientState.Lobby) UpdateLobby(ks);
            else if (_state == ClientState.ConnectFailed) UpdateConnectFailed(ks);
            // P731 — the intro screen (no command-line args).
            else if (_state == ClientState.Intro) UpdateIntro(ks);
            // P757 — the splash screen (native splashscreen.cpp): times out to
            // the intro; any key/click skips straight there.
            else if (_state == ClientState.Splash) UpdateSplash(ks);
            else if (_state == ClientState.SideSelect) UpdateSideSelect(ks);
            // P282 — pre-flight ready screen once seated, before the mission starts.
            else if (_state == ClientState.ReadyCheck)
            {
                UpdateReadyCheck(ks);
                // P445 — the ReadyCheck→Flying handoff IS the initial launch
                // into flight (all four handoff sites funnel through here);
                // reset the persistent throttle to full (+1), native's on-line
                // default. Undock-from-dock resets separately in
                // HandleDockedScreen (state stays Flying there).
                // P523 — also prime the in-flight edge latches so a key held
                // across the pre-flight→flight transition can't fire its
                // in-flight EDGE action on the first flight frame (the ready
                // key Y leaked and selected self — see PrimeInFlightKeyLatches).
                if (_state == ClientState.Flying)
                {
                    _throttle = FlightInput.DefaultThrottle;
                    // P624 — per-launch reset of the new flight-pack state:
                    // vector lock off (native coast resets with the ship), radar
                    // back to Default (native loads the persisted RadarLOD, default
                    // All; the port starts each sortie on Default), head snapped to
                    // forward.
                    _coastToggled = false;
                    _radarMode = Rendering.RadarMode.Default;
                    _headLookYaw = 0f;
                    PrimeInFlightKeyLatches(ks);
                }
            }
            // P565 — the offline hull inspector: its own input/step loop, no flight logic.
            else if (_state == ClientState.Inspecting)
            {
                UpdateInspector(gameTime, ks);
            }
            base.Update(gameTime);
            return;
        }

        UpdateFlying(ks, gameTime);

        // P304 — background music. Runs every flying frame AFTER UpdateFlying
        // (whose docked screen early-returns), so the music subsystem keeps
        // being driven in both the flying and docked states — exactly where the
        // flight↔station switch must happen. Edge-triggered, composing-gated
        // keybindings (J toggle, + / - volume) land here too.
        UpdateMusic(ks, gameTime);

        base.Update(gameTime);
    }

    /// <summary>
    /// P764 — the help-overlay toggle, extracted from the flight-input edge
    /// branch so the headless test can pin the cue: native <c>OnHelp</c> plays
    /// <c>positiveButtonClickSound</c> on EVERY toggle, open and close
    /// (WinTrek.cpp:9267-9269) — SIM-0285.
    /// </summary>
    private void ToggleHelpOverlay()
    {
        _helpVisible = !_helpVisible;
        if (_helpVisible) _helpManual = new Rendering.HelpManualModel();   // hlpStart on open
        UiSounds?.PlayPositiveClick();
        Console.WriteLine($"[monogame] help {( _helpVisible ? "ON" : "OFF" )}");
    }

    /// <summary>
    /// P523 — the pre-flight→flight edge-latch guard. The side-select and
    /// ready-check screens maintain their OWN edge latches (<c>_ssEnterWasDown</c>,
    /// <c>_rcYWasDown</c>, …) every pre-flight frame, but the IN-FLIGHT edge
    /// latches below are only written once <see cref="UpdateFlying"/> runs — so
    /// on the very first flight frame they are all still <c>false</c>, and a key
    /// physically held across the ReadyCheck→Flying transition (e.g. Enter/R
    /// confirming a side or readying) would be read as a fresh press and fire
    /// its in-flight edge action (open chat, open quick-chat, …). This is the
    /// leak that let the ready key Y select the player's own ship (target-self
    /// was bound to Y, P417). Priming each in-flight latch from the CURRENT key
    /// state marks a held key as "already pressed", so no spurious edge fires on
    /// the first flight frame; a key not held is primed <c>false</c> and behaves
    /// normally on its next real press. Call it exactly at the ReadyCheck→Flying
    /// handoff (alongside the P445 throttle reset), where every pre-flight→flight
    /// entry funnels.
    /// </summary>
    private void PrimeInFlightKeyLatches(KeyboardState ks)
    {
        PrimeFlightParts(ks);
        (_enterWasDown, _qcRWasDown) = ComputeSharedEdgePrimes(ks);
        // The chat-delete Backspace is TEXT ENTRY (allow-listed raw key);
        // every other latch below tracks the ACTION (resolved binding).
        _backKeyWasDown                = ks.IsKeyDown(XnaKeys.Back);
        _gameOverBackWasDown           = ActionDown(ks, "GameOverBack");
        _chatTabKeyWasDown             = ActionDown(ks, "CycleChatTarget");
        _mKeyWasDown                   = ActionDown(ks, "ToggleMouse");
        _iKeyWasDown                   = ActionDown(ks, "InvertMouse");
        _mouseDzKeyWasDown             = ActionDown(ks, "MouseDeadzone");
        _mouseLinKeyWasDown            = ActionDown(ks, "MouseResponseCurve");
        _mouseSensDownKeyWasDown       = ActionDown(ks, "MouseSensitivityDown");
        _mouseSensUpKeyWasDown         = ActionDown(ks, "MouseSensitivityUp");
        _tabKeyWasDown                 = ActionDown(ks, "ToggleStarmap");
        _selPrevKeyWasDown             = ActionDown(ks, "SelectSectorPrev");
        _selNextKeyWasDown             = ActionDown(ks, "SelectSectorNext");
        _vKeyWasDown                   = ActionDown(ks, "CycleView");
        _viewMissileWasDown = ActionDown(ks, "ViewMissile");
        _viewChaseWasDown = ActionDown(ks, "ViewChase");
        _viewTurretWasDown = ActionDown(ks, "ViewTurret");
        _kKeyWasDown                = ActionDown(ks, "Suicide");
        _cKeyWasDown                = ActionDown(ks, "DropChaff");
        _fCloakWasDown              = ActionDown(ks, "ToggleCloak");
        _lKeyWasDown                = ActionDown(ks, "Ripcord");
        _nKeyWasDown                = ActionDown(ks, "DropMine");
        _gfireKeyWasDown            = ActionDown(ks, "ToggleGroupFire");
        _prevWKeyWasDown            = ActionDown(ks, "PrevWeapon");
        _nextWKeyWasDown            = ActionDown(ks, "NextWeapon");
        _thUpWasDown                = ActionDown(ks, "ThrottleUp");
        _thDownWasDown              = ActionDown(ks, "ThrottleDown");
        _thZeroWasDown              = ActionDown(ks, "ThrottleZero");
        _thFullWasDown              = ThrottleFullDown(ks);
        _th33WasDown                = ActionDown(ks, "Throttle33");
        _th66WasDown                = ActionDown(ks, "Throttle66");
        _targetPressEdges.Prime(InputActionCatalog.Definitions
            .Where(a => a.Name.StartsWith("Target", StringComparison.Ordinal) || a.Name == "OccupyNextTurret")
            .Select(a => (a.Name, ActionDown(ks, a.Name))));
        _semicolonNothingKeyWasDown = ActionDown(ks, "TargetNothing");
        _apostropheCenterKeyWasDown = ActionDown(ks, "TargetCenter");
        _shotKeyWasDown             = ActionDown(ks, "ScreenShot");
        _pageDownKeyWasDown         = ActionDown(ks, "CycleCommandee");
        _insertKeyWasDown           = ActionDown(ks, "IssueBuild");
        _p1TurretKeyWasDown         = ActionDown(ks, "PromoteTurret1");
        _p2TurretKeyWasDown         = ActionDown(ks, "PromoteTurret2");
        _p3TurretKeyWasDown         = ActionDown(ks, "PromoteTurret3");
        _p4TurretKeyWasDown         = ActionDown(ks, "PromoteTurret4");
        _xKeyWasDown                = ActionDown(ks, "LeaderCommand");
        _commandActionInput.Prime(ks, KeyBindingStore.Instance.Map);
        // P624 — prime the new flight-pack edge latches (vector lock, radar cycle)
        // so a key held across the pre-flight→flight transition can't fire once.
        _lockVectorKeyWasDown       = ActionDown(ks, "LockVector");
        _cycleRadarKeyWasDown       = ActionDown(ks, "CycleRadar");
        // P626 — prime the chat-scroll edge latches too (ChatPageUp/Down,
        // PrevCommMsg/NextCommMsg) so a held page key can't fire on the first frame.
        _chatPgUpWasDown            = ActionDown(ks, "ChatPageUp");
        _chatPgDnWasDown            = ActionDown(ks, "ChatPageDown");
        _chatMsgUpWasDown           = ActionDown(ks, "PrevCommMsg");
        _chatMsgDnWasDown           = ActionDown(ks, "NextCommMsg");
        _voteYesWasDown             = ActionDown(ks, "VoteYes");
        _voteNoWasDown              = ActionDown(ks, "VoteNo");
        // P626 — prime the in-flight cargo key edge latches (selection cycle,
        // drop-cargo, reload) so a held key can't fire on the first frame.
        _nextCargoWasDown           = ActionDown(ks, "NextCargo");
        _nextPartWasDown            = ActionDown(ks, "NextPart");
        _prevPartWasDown            = ActionDown(ks, "PrevPart");
        _dropCargoWasDown           = ActionDown(ks, "DropCargo");
        _reloadWasDown              = ActionDown(ks, "Reload");
    }

    // WinTrek.cpp:10939-10962: zero > 33 > 66 > full. Presets share
    // the port's rebindable, edge-triggered keyboard and composing guard.
    private void ApplyThrottleKeys(KeyboardState ks, bool composing)
    {
        bool thUp = ActionDown(ks, "ThrottleUp");
        bool thDown = ActionDown(ks, "ThrottleDown");
        bool thZero = ActionDown(ks, "ThrottleZero");
        bool thFull = ThrottleFullDown(ks);
        bool th33 = ActionDown(ks, "Throttle33");
        bool th66 = ActionDown(ks, "Throttle66");
        if (!composing)
        {
            if (thUp && !_thUpWasDown)
            {
                _throttle = FlightInput.StepThrottleUp(_throttle);
                Console.WriteLine($"[monogame] throttle UP -> {_throttle:F2} ({FlightInput.CruiseFraction(_throttle) * 100f:F0}%)");
            }
            if (thDown && !_thDownWasDown)
            {
                _throttle = FlightInput.StepThrottleDown(_throttle);
                Console.WriteLine($"[monogame] throttle DOWN -> {_throttle:F2} ({FlightInput.CruiseFraction(_throttle) * 100f:F0}%)");
            }
            if (thZero && !_thZeroWasDown)
            {
                _throttle = FlightInput.PresetValue(FlightInput.ThrottlePreset.Zero);
                Console.WriteLine("[monogame] throttle ZERO -> stop (-1)");
            }
            else if (th33 && !_th33WasDown)
            {
                _throttle = FlightInput.PresetValue(FlightInput.ThrottlePreset.ThirtyThree);
                _joystickFlight.Reset(); // native joyThrottle=false; next sample keeps keyboard authority
            }
            else if (th66 && !_th66WasDown)
            {
                _throttle = FlightInput.PresetValue(FlightInput.ThrottlePreset.SixtySix);
                _joystickFlight.Reset();
            }
            else if (thFull && !_thFullWasDown)
            {
                _throttle = FlightInput.PresetValue(FlightInput.ThrottlePreset.Full);
                Console.WriteLine("[monogame] throttle FULL -> +1 (100%)");
            }
        }
        _thUpWasDown = thUp;
        _thDownWasDown = thDown;
        _thZeroWasDown = thZero;
        _thFullWasDown = thFull;
        _th33WasDown = th33;
        _th66WasDown = th66;

    }

    /// <summary>P624 — is the ThrottleFull action held? The action's alternate
    /// chord covers the ISO/EU backslash scancode (OemPipe — the same physical
    /// "\\" key the catalogue binds OemBackslash to, folded into the action map
    /// as a native alternate in InputActionCatalog), so no hard-coded key
    /// remains. Rebinding ThrottleFull moves both.</summary>
    private bool ThrottleFullDown(KeyboardState ks)
        => ActionDown(ks, "ThrottleFull");

    /// <summary>P624 — resolve the cruise throttle value that matches the current
    /// autopilot/target's speed (native GetThrottle, WinTrek.cpp:122-134) for the
    /// held TK_MatchSpeed key. A Ship target uses its live velocity; a station /
    /// asteroid / warp is stationary (speed 0 → throttle −1, i.e. stop). False
    /// when there is no target or the own hull max speed isn't known yet.</summary>
    private bool TryMatchSpeedThrottle(out float throttle)
    {
        throttle = 0f;
        if (_autoTarget is not { } t) return false;
        if (!_ownSim.HaveHull || _ownSim.MaxSpeed is not { } myMax || myMax <= 0f) return false;
        float targetSpeed;
        lock (_session.Gate)
        {
            switch (t.Kind)
            {
                case TargetKind.Ship:
                    if (!_session.World.Ships.TryGetValue(t.Id, out var s) || s.Latest is not { } up)
                        return false;
                    targetSpeed = up.Velocity.Length();
                    break;
                case TargetKind.Station:
                case TargetKind.Asteroid:
                case TargetKind.Warp:
                    targetSpeed = 0f;
                    break;
                default:
                    return false;
            }
        }
        throttle = FlightInput.MatchSpeedThrottle(targetSpeed, myMax);
        return true;
    }

    /// <summary>
    /// P523 — the pre-flight→flight EDGE-GATE for the keys the side-select /
    /// ready-check screens share with in-flight EDGE actions. Enter confirms a
    /// side (and opens chat in flight); R randomizes teams on side-select (and
    /// opens quick-chat in flight). If either is held when the mission starts, its
    /// in-flight edge must be primed (marked already-pressed) so the first flight
    /// frame does not fire it. Internal + pure over the keyboard state so a test
    /// can pin the guarantee headlessly (InternalsVisibleTo). Resolved through
    /// the LIVE action map (StartChat / QuickChat), so a rebind is honoured.
    /// </summary>
    internal static (bool Enter, bool R) ComputeSharedEdgePrimes(KeyboardState ks)
    {
        var map = Rendering.KeyBindingStore.Instance.Map;
        return (map.IsDown(ks, "StartChat"), map.IsDown(ks, "QuickChat"));
    }

    /// <summary>
    /// P304 — the per-frame music drive (see the call site in <see cref="Update"/>).
    /// Handles the three edge-triggered keybindings (J toggle music, +/− volume)
    /// — gated off while composing chat so typing never flips the music — then
    /// feeds the live docked state into the music subsystem so it switches
    /// between the flight and station beds. Docked state is read with the exact
    /// real expression the rest of the client uses
    /// (<c>ShipEntity.StationId != -1</c>, see <c>HandleDockedScreen</c>), not a
    /// re-derived copy.
    /// </summary>
    private void UpdateMusic(KeyboardState ks, GameTime gameTime)
    {
        if (_music is null) return;

        // P304 — toggle music (J: Jam); P625 — read through the live action map.
        bool musicKey = KeyOf("ToggleMusic").Matches(ks);
        if (musicKey && !_musicToggleWasDown && !_chatActive && !_qcMenuOpen)
            _music.Toggle();
        _musicToggleWasDown = musicKey;

        // P445 — music volume moved off the main-row +/= and -/_ keys: those
        // are now the persistent-throttle Up/Down (native's own bindings), the
        // higher-priority binding. P625 — per Domingo 2026-10-12 "native wins
        // the F-keys": the F-row now belongs to the native external cameras, so
        // music volume moved OFF the F-row entirely to a free Ctrl chord
        // (Ctrl + = up / Ctrl + - down; defaultinputmap.mdl itself binds no
        // music-volume keys). The action map owns these bindings so a rebind
        // takes effect here.
        bool volUp = KeyOf("MusicVolumeUp").Matches(ks);        // Ctrl + = — music volume up
        if (volUp && !_musicVolUpWasDown && !_chatActive && !_qcMenuOpen)
            _music.VolumeUp();
        _musicVolUpWasDown = volUp;

        bool volDown = KeyOf("MusicVolumeDown").Matches(ks);    // Ctrl + - — music volume down
        if (volDown && !_musicVolDownWasDown && !_chatActive && !_qcMenuOpen)
            _music.VolumeDown();
        _musicVolDownWasDown = volDown;

        // Dock state from the real client world, under the same receive-thread
        // lock as the rest of the per-frame world read.
        var world = _session.World;
        bool docked = false;
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me &&
                world.Ships.TryGetValue(me, out var ent) &&
                ent.StationId != -1)
                docked = true;
        }

        _music.Update(docked);
    }

    /// <summary>P761 — the compose gate the flight-input build applies: while a
    /// modal surface (chat compose, quick-chat menu, main menu, ...) is up the
    /// keyboard flight state is fully neutral — only the persistent throttle
    /// carries through — so typing a word can never also thrust/pitch/yaw the
    /// ship. Extracted from <see cref="UpdateFlying"/>'s inline ternary so the
    /// handler-level test pins it.</summary>
    internal static FlightInputState GateCompose(bool composing, FlightInputState live)
        => composing ? new FlightInputState { Throttle = live.Throttle } : live;

    /// <summary>
    /// P757 — the chat compose/send/focus flow, extracted from
    /// <see cref="UpdateFlying"/> as the production seam the interface tests
    /// drive (the P748 <c>HandleHangarQuitInput</c> discipline). Native rules:
    /// <c>TK_StartChat</c> opens the composer and removes focus from flight
    /// input (<c>m_pconsoleImage-&gt;StartChat(); m_ptrekInput-&gt;SetFocus(false)</c>,
    /// WinTrek.cpp:9441-9446 — the port's equivalent is the composing gate in
    /// <see cref="UpdateFlying"/>); while composing, Enter sends the line via
    /// <see cref="SendChatLine"/> and Backspace deletes the previous character;
    /// Tab cycles the recipient while the buffer is empty (native
    /// <c>ConsoleImage::OnTab</c>, console.cpp:2747-2755). Text itself arrives
    /// through Window.TextInput (<see cref="OnTextInput"/>). Enter is ignored
    /// while the quick-chat menu is open (its keys are navigation).
    /// <para>P761 — the same handler also drives the character-sheet profile
    /// editor's Enter (apply) / Backspace (delete) edges, and both surfaces are
    /// composing gates, so neither typing a chat line nor typing a description
    /// can fly the ship.</para>
    /// </summary>
    private void HandleChatCompose(KeyboardState ks)
    {
        // P761 — the character-sheet profile editor owns Enter (apply) and
        // Backspace (delete) while it is up: run its edges before the chat
        // machine could act on the same keys, and the chat path below is gated
        // off while it is editing. The gate reads the FRAME-START state: an
        // Apply on this very Enter press must not let the same press through to
        // chat (which would open the composer behind the now-closed editor).
        bool profileWasEditing = _pilotProfile.Editing;
        if (profileWasEditing)
        {
            bool pEnter = ActionDown(ks, "StartChat");
            if (pEnter && !_pilotProfileEnterWasDown)
            {
                string? applied = _pilotProfile.Apply();
                if (applied is not null)
                {
                    var prefs = IntroPreferences.Load();
                    prefs.PilotDescription = applied;
                    prefs.Save();
                }
                Console.WriteLine($"[monogame] pilot profile applied ({applied?.Length ?? 0} chars)");
            }
            _pilotProfileEnterWasDown = pEnter;

            // TEXT ENTRY: Backspace edits the draft (the same allow-listed raw
            // key the chat line uses).
            bool pBack = ks.IsKeyDown(XnaKeys.Back);
            if (pBack && !_pilotProfileBackWasDown) _pilotProfile.Backspace();
            _pilotProfileBackWasDown = pBack;
        }
        else
        {
            _pilotProfileEnterWasDown = false;
            _pilotProfileBackWasDown = false;
        }

        bool enter = ActionDown(ks, "StartChat");
        if (!_mainMenuVisible && !_menuConsumedFrame && !_keyBindVisible && !_qcMenuOpen && !profileWasEditing && enter && !_enterWasDown)
        {
            if (IsBriefingVisible(out _))
                DismissBriefing();   // P271 — Enter closes the briefing popup, doesn't open chat
            else if (LiveComposeState != NativeComposeState.NotComposing)
            {
                // P784 — native OnChar VK_RETURN (console.cpp:2359-2368): an
                // empty buffer quits; text sends through the state's send path
                // and every send ends in QuitComposing (console.cpp:1618-1620).
                if (_chatBuffer.Length == 0)
                    SetComposeState(NativeComposeState.NotComposing);
                else if (LiveComposeState == NativeComposeState.Shell)
                {
                    // Shell compose does NOT send chat — it runs
                    // ParseShellCommand (console.cpp:1612-1614).
                    RunShellCommand(_chatBuffer.ToString());
                    SetComposeState(NativeComposeState.NotComposing);
                }
                else
                {
                    SendChatLine();
                    SetComposeState(NativeComposeState.NotComposing);
                }
            }
            else
            {
                // SIM-0319 — native StartChat() plays chatKeySound before it
                // opens the composer (console.cpp:2423; sounddef.mdl:324 text wave).
                if (ChatKeySound.OnStartChat())
                    _sound?.PlayChatKey();
                SetComposeState(NativeComposeState.Chat);   // console.cpp:2423-2431
            }
        }
        _enterWasDown = enter;

        if (_chatActive)
        {
            // Backspace deletes the previous character — TEXT ENTRY, so it is an
            // allow-listed raw key (not a game command; it only edits the line).
            bool back = ks.IsKeyDown(XnaKeys.Back);
            if (back && !_backWasDown && _chatBuffer.Length > 0)
                _chatBuffer.Length -= 1;
            _backWasDown = back;

            // P456 — Tab cycles the recipient while the compose buffer is empty
            // (or holds only a leading space), mirroring native's OnTab
            // (console.cpp:2747-2755): CycleChatTarget with *pcc=='\0'||' '. With
            // typed text, native does name completion instead — out of scope
            // (P456b), so Tab is a no-op then. Edge-detected with its own flag so
            // holding Tab never re-fires, and separate from the minimap-toggle
            // flag below (mutually exclusive via the composing gate).
            bool chatTab = ActionDown(ks, "CycleChatTarget");
            if (chatTab && !_chatTabKeyWasDown && ChatTargetLogic.CanCycleWhileComposing(_chatBuffer.ToString()))
            {
                _chatTarget = ChatTargetLogic.Next(_chatTarget);
                // P784 — the channel-filter mode's channel set follows the
                // compose recipient (native AddChannel rebuilds the view,
                // chatpane.cpp:340-345), so a mid-compose Tab cycle re-applies
                // it while the pane filter is in its channel mode.
                if (_chatPaneFilterMode == 2) ApplyChatPaneFilterMode();
                Console.WriteLine($"[monogame] chat target -> {_chatTarget} ({ChatTargetLogic.ComposeLabel(_chatTarget).Trim()})");
            }
            _chatTabKeyWasDown = chatTab;
        }
    }

    /// <summary>P784 — the port of native <c>SetComposeState</c>
    /// (console.cpp:1072-1091): writes the live compose state and keeps
    /// <c>_chatActive</c> ("a keyboard composer owns typing") in sync — Chat
    /// and Shell are the typing states (native IsTakingKeystrokes accepts
    /// characters from both, console.cpp:2335-2338). Leaving a typing state
    /// clears the typed text, like native <c>QuitComposing</c> and
    /// <c>StartComposing</c> (<c>m_strTypedText.SetEmpty()</c>,
    /// console.cpp:1641,1659).</summary>
    private void SetComposeState(NativeComposeState next)
    {
        _composeState = next;
        _chatActive = next is NativeComposeState.Chat or NativeComposeState.Shell;
        if (!_chatActive) _chatBuffer.Clear();
    }

    /// <summary>P784 — the shell compose's send path: native
    /// <c>SendChat</c> routes a Shell-state line to
    /// <c>trekClient.ParseShellCommand</c> instead of the chat relay
    /// (console.cpp:1612-1614); the shipped shell commands are
    /// <c>!mute &lt;player&gt;</c> / <c>!unmute &lt;player&gt;</c> by character
    /// name (clintlib/clintlib.cpp:2277-2296) and an unrecognised line is
    /// silently dropped (the <c>bCommand</c> result is ignored). The port's
    /// mute store is <see cref="Allegiance.ClientCore.State.ClientWorld.SetPilotMuted"/>.</summary>
    private void RunShellCommand(string line)
    {
        lock (_session.Gate)
        {
            var result = ShellCommands.Parse(line, _session.World.Ships);
            if (!result.Handled)
            {
                Console.WriteLine($"[monogame] shell (dropped, native unknown shell line) >> {line}");
                return;
            }
            _session.World.SetPilotMuted(result.ShipId, result.Mute);
            Console.WriteLine($"[monogame] shell >> {(result.Mute ? "!mute" : "!unmute")} {result.Name} (ship {result.ShipId})");
        }
    }

    /// <summary>
    /// The in-flight state (previously the whole of <see cref="Update"/>): chat
    /// composition, flight controls, autopilot, remote prediction and the
    /// minimal auto-start. All keyboard flight input is gated off while a chat
    /// line is being composed, so typing a word can never also thrust/turn the
    /// ship; the autopilot (once engaged) may keep steering, since it is not a
    /// keystroke-derived input.
    /// </summary>
    private void UpdateFlying(KeyboardState ks, GameTime gameTime)
    {
        // Chat compose/send/focus (native TK_StartChat, WinTrek.cpp:9441-9446,
        // + UpdateComposedChat, console.cpp:1282-1338): P757 extracted into
        // <see cref="HandleChatCompose"/> (the same production-seam discipline
        // the P748 hangar keys use) so the flow is drivable headlessly;
        // UpdateFlying remains the only flight caller.
        HandleChatCompose(ks);

        // P217 — quick-chat voice macros. R (free in flight) opens the menu;
        // while it is open every shortcut key navigates instead of flying.
        bool qcKey = ActionDown(ks, "QuickChat");
        if (!_chatActive && qcKey && !_qcRWasDown && !_qcMenuOpen)
            OpenQuickChat();
        _qcRWasDown = qcKey;

        if (_qcMenuOpen)
            NavigateQuickChat(ks);

        // P626 — chat buffer scroll (TK_PrevCommMsg/NextCommMsg/ChatPageUp/Down).
        // Edge-triggered, applies whenever the chat pane is showing (flight, not
        // inside the quick-chat menu, and not while the help manual owns the
        // page keys). Steps/page are clamped against the live
        // log length by ChatScroll. A freshly-arrived line snaps a scrolled-back
        // view to the live edge (offset -> 0) so the pane never lingers on stale
        // history over new activity.
        if (!_qcMenuOpen && !_helpVisible)
        {
            // P757 — one-shot: seed the pane's channel-filter mode from the
            // saved Game Options preference (native m_bPlayerChatsOnly default).
            if (!_chatPaneFilterSeeded)
            {
                _chatPaneFilterSeeded = true;
                if (IntroPreferences.Load().ChatFilterPlayersOnly)
                {
                    _chatPaneFilterMode = 1;
                    ApplyChatPaneFilterMode();
                }
            }
            // P757 — the chat pane's history view is the ChatPaneModel now
            // (native chatpane.cpp): new lines scroll only while autoscroll is
            // on, a paused pane keeps its content (offset grows with the log),
            // and the ten-second resume timer runs on the game clock.
            int chatCount = FilteredChatCount();
            if (chatCount != _lastChatCount)
            {
                _chatPane.OnMessagesAppended(chatCount - _lastChatCount, chatCount, ChatRenderer.MaxVisible);
                _lastChatCount = chatCount;
            }
            _chatPane.Tick(_nowSeconds);

            bool pgUp = ActionDown(ks, "ChatPageUp");
            if (pgUp && !_chatPgUpWasDown) _chatPane.PageUp(chatCount, ChatRenderer.MaxVisible, _nowSeconds);
            _chatPgUpWasDown = pgUp;
            bool pgDn = ActionDown(ks, "ChatPageDown");
            if (pgDn && !_chatPgDnWasDown) _chatPane.PageDown(chatCount, ChatRenderer.MaxVisible, _nowSeconds);
            _chatPgDnWasDown = pgDn;
            bool msgUp = ActionDown(ks, "PrevCommMsg");
            if (msgUp && !_chatMsgUpWasDown) _chatPane.ScrollBy(+1, chatCount, ChatRenderer.MaxVisible, _nowSeconds);
            _chatMsgUpWasDown = msgUp;
            bool msgDn = ActionDown(ks, "NextCommMsg");
            if (msgDn && !_chatMsgDnWasDown) _chatPane.ScrollBy(-1, chatCount, ChatRenderer.MaxVisible, _nowSeconds);
            _chatMsgDnWasDown = msgDn;

            // P757 — chat-pane channel filter (Alt+C): cycle All → Players →
            // Channel. Every state is a native configuration of the same
            // ChatPassesFilter machinery (chatpane.cpp:299-358): Players = the
            // native m_bPlayerChatsOnly default; Channel = the lobby pane's
            // [compose channel, Individual, IndividualNoFilter, Admin] set
            // (teamscreen.cpp:3085-3101).
            bool filterKey = ActionDown(ks, "ChatPaneFilter");
            if (filterKey && !_chatPaneFilterWasDown)
            {
                _chatPaneFilterMode = (_chatPaneFilterMode + 1) % 3;
                ApplyChatPaneFilterMode();
                Console.WriteLine($"[monogame] chat filter -> {ChatPaneFilterLabel()}");
            }
            _chatPaneFilterWasDown = filterKey;
        }

        // A modal UI consumes the keyboard: chat composition OR the quick-chat
        // menu both neutralise flight input / toggles / autopilot-steering, so
        // navigating or typing never also thrusts/turns the ship. P757 — the
        // help overlay joins them while open (the native help pane owns the
        // keyboard, effect/help.cpp). P761 — the character-sheet profile editor
        // is a composing surface too (typing the description must not fly).
        bool composing = _chatActive || _qcMenuOpen || _keyBindVisible || _mainMenuVisible || _menuConsumedFrame || _playerPingsVisible || _helpVisible || _pilotProfile.Editing;
        // Investment is modal in flight and command view; ordinary docked
        // command acceptance below must still run when this overlay is closed.
        if ((_investActive || ActionDown(ks, "ToggleInvestmentPane")) && HandleInvestmentScreen(ks, composing)) return;
        // P752 — the team pane's selected-player controls (donate/stop/take-me-to/
        // accept). Runs after the (modal) investment pane so the two verb sets
        // can never double-fire.
        HandleTeamPaneControls(ks, composing);
        HandleRemainderTargetKeys(ks, composing);
        HandleCommandActionKeys(ks, composing);
        HandleP623TargetingKeys(ks, composing);
        // Native SetViewToCombatMode also works while docked: close the panes
        // and return to the hangar (WinTrek.cpp:9357-9385).
        ToggleCameraKey(ks, composing, "ViewMissile", CameraMode.ExternalMissile, ref _viewMissileWasDown);
        ToggleCameraKey(ks, composing, "ViewChase", CameraMode.Chase, ref _viewChaseWasDown);
        ToggleCameraKey(ks, composing || !IsTurretGunner(), "ViewTurret", CameraMode.ExternalTurret, ref _viewTurretWasDown);

        // P759 — the runtime LOD slider commands (native WinTrek.cpp:9448-9453,
        // 10415-10424). Edge-triggered + composing-gated like every discrete
        // in-flight key; they drive the pure LodControl and the renderer copies
        // its Bias into _shipLodProfile before drawing ships.
        bool lodToggle = ActionDown(ks, "ToggleLODSlider");
        if (!composing && lodToggle && !_lodToggleWasDown) _lodControl.ToggleVisible();
        _lodToggleWasDown = lodToggle;
        bool lodUp = ActionDown(ks, "LODUp");
        if (!composing && lodUp && !_lodUpWasDown) _lodControl.PageUp();
        _lodUpWasDown = lodUp;
        bool lodDown = ActionDown(ks, "LODDown");
        if (!composing && lodDown && !_lodDownWasDown) _lodControl.PageDown();
        _lodDownWasDown = lodDown;

        // P626 — vote YES/NO (TK_VoteYes/TK_VoteNo, native WinTrek.cpp:10377-10387
        // trekClient.Vote(bAgree)). Edge-triggered + composing-gated like every
        // other in-flight key. Reads the active ballot (P187) and casts a real
        // FM_C_VOTE(19) via ConnectionSession.SendVote; with no ballot in flight
        // it surfaces a hint instead of silently doing nothing.
        bool vy = ActionDown(ks, "VoteYes");
        if (!composing && vy && !_voteYesWasDown) CastVote(agree: true);
        _voteYesWasDown = vy;
        bool vn = ActionDown(ks, "VoteNo");
        if (!composing && vn && !_voteNoWasDown) CastVote(agree: false);
        _voteNoWasDown = vn;

        // P626 — in-flight cargo: selection cycle (TK_NextCargo / TK_NextPart /
        // TK_PrevPart), drop-cargo (TK_DropCargo) and reload (TK_Reload).
        // Edge-triggered + composing-gated like every key above. Selection
        // cycling advances the same native "selected cargo part" (next = forward
        // through the bay, prev = backward, wrapping); DropCargo drops the
        // selected cargo part into space as a treasure; Reload tops up pools /
        // launchers from the whole cargo hold. All three are no-ops with an
        // empty hold (the helpers surface a hint), matching native's guards
        // (TK_DropCargo requires a cargo part, flyingF(); WinTrek.cpp:9698).
        bool nc = ActionDown(ks, "NextCargo");
        if (!composing && nc && !_nextCargoWasDown) CycleSelectedCargo(next: true);
        _nextCargoWasDown = nc;
        bool np = ActionDown(ks, "NextPart");
        if (!composing && np && !_nextPartWasDown) CycleInventory(next: true);
        _nextPartWasDown = np;
        bool pp = ActionDown(ks, "PrevPart");
        if (!composing && pp && !_prevPartWasDown) CycleInventory(next: false);
        _prevPartWasDown = pp;
        bool dc = ActionDown(ks, "DropCargo");
        if (!composing && dc && !_dropCargoWasDown) TryDropCargo();
        _dropCargoWasDown = dc;
        bool rl = ActionDown(ks, "Reload");
        if (!composing && rl && !_reloadWasDown) TryReload();
        _reloadWasDown = rl;

        // P268 — docked/hangar loadout screen. When the local ship is docked
        // (ShipEntity.StationId != -1 — the server's real DockedNotify/SHIP_STATUS
        // latch), this replaces flight (and the old automatic undock): the player
        // shops the loadout and launches. Running first and returning means no
        // flight control / weapon / toggles apply while docked.
        if (HandleDockedScreen(ks, composing))
            return;


        // P572 — --auto-goto / --auto-target / --auto-dock-after (only airborne;
        // the docked branch above consumed this frame instead).
        ApplyAutoProofFlying();

        // Virtual-joystick mouse-flight toggle (M), edge-triggered to mirror the
        // original's TK_ToggleMouse key (not a held-until-repeat behaviour).
        bool mMouse = ActionDown(ks, "ToggleMouse");
        if (!composing && mMouse && !_mKeyWasDown)
        {
            _mouseFlightEnabled = !_mouseFlightEnabled;
            IsMouseVisible = CommandCameraActive || _minimapExpanded || !_mouseFlightEnabled;
            if (_mouseFlightEnabled)
                _mouseFlight.Enable();      // arm the just-enabled skip
            else
                _mouseFlight.Reset();       // zero the stored deflection (SetEnabled(false))
            Console.WriteLine($"[monogame] mouse flight {( _mouseFlightEnabled ? "ON" : "OFF" )}");
        }
        _mKeyWasDown = mMouse;

        // P342 — invert mouse pitch (I), edge-triggered exactly like the M
        // toggle above and gated behind the same !composing check so typing in
        // chat never flips it. Works whether or not mouse-flight is currently
        // enabled — the flag just sits on the controller until Update runs.
        // Session-scoped: no settings-persistence system exists yet, so this
        // resets to normal on every restart. I is unclaimed (the binding table
        // reserves W/S/Q/E/Arrows/A/D/Shift/R/M/V/T/G/K/C/N/OemTilde/[ /]/LeftCtrl/
        // Tab/, /./Enter/Back/Escape/Space and more; I is the natural mnemonic).
        bool iInvert = ActionDown(ks, "InvertMouse");
        if (!composing && iInvert && !_iKeyWasDown)
        {
            _mouseFlight.InvertPitch = !_mouseFlight.InvertPitch;
            Console.WriteLine($"[monogame] mouse pitch {(_mouseFlight.InvertPitch ? "INVERTED" : "NORMAL")}");
        }
        _iKeyWasDown = iInvert;

        // P506 — mouse-flight RESPONSE-CURVE settings (native's menu items,
        // WinTrek.cpp:4534 / AdjustMouseSens :5512; this keyboard port has no
        // menu, so the digit row D5-D8 — free, grep-verified — carries them).
        // All edge-triggered + composing-gated like every other toggle here.
        //   D5  → cycle the deadzone preset Smallest(0.04)/Small(0.1)/Large(0.3)
        //   D6  → toggle linear vs quadratic response (native default quadratic)
        //   D7  → mouse sensitivity −0.01   D8 → mouse sensitivity +0.01
        //         (native range [0.1, 2.0], step c_fMouseSensDelta)
        bool dzKey = ActionDown(ks, "MouseDeadzone");
        if (!composing && dzKey && !_mouseDzKeyWasDown)
        {
            var p = MouseFlightController.DeadzonePresets;
            int i = Array.FindIndex(p, d => MathF.Abs(d - _mouseFlight.Deadzone) < 0.0005f);
            i = i < 0 ? 0 : (i + 1) % p.Length;
            _mouseFlight.Deadzone = p[i];
            Console.WriteLine($"[monogame] mouse deadzone preset -> {p[i]:0.00} ({(int)(p[i] * 100)}%)");
        }
        _mouseDzKeyWasDown = dzKey;

        bool linKey = ActionDown(ks, "MouseResponseCurve");
        if (!composing && linKey && !_mouseLinKeyWasDown)
        {
            _mouseFlight.LinearControls = !_mouseFlight.LinearControls;
            Console.WriteLine(_mouseFlight.LinearControls
                ? "[monogame] mouse response: LINEAR"
                : "[monogame] mouse response: QUADRATIC (native default)");
        }
        _mouseLinKeyWasDown = linKey;

        bool sensDown = ActionDown(ks, "MouseSensitivityDown");
        if (!composing && sensDown && !_mouseSensDownKeyWasDown)
        {
            _mouseFlight.MouseSensitivity -= MouseFlightController.MouseSensitivityStep;
            Console.WriteLine($"[monogame] mouse sensitivity DOWN -> {_mouseFlight.MouseSensitivity:F2}");
        }
        _mouseSensDownKeyWasDown = sensDown;

        bool sensUp = ActionDown(ks, "MouseSensitivityUp");
        if (!composing && sensUp && !_mouseSensUpKeyWasDown)
        {
            _mouseFlight.MouseSensitivity += MouseFlightController.MouseSensitivityStep;
            Console.WriteLine($"[monogame] mouse sensitivity UP -> {_mouseFlight.MouseSensitivity:F2}");
        }
        _mouseSensUpKeyWasDown = sensUp;

        // P249 — whole-mission starmap overlay toggle (Tab), edge-triggered like
        // the M/V toggles above and gated behind the same composing check so
        // typing in chat / navigating the quick-chat menu never flips it. Tab is
        // unclaimed by every other binding (W/S/Q/E/Arrows/A/D/Shift/R/M/V/T/G/
        // Enter/Back/Escape/Space/K/C/N/OemTilde/[ /]/LeftCtrl all taken) and is
        // the natural native-adjacent overlay key (the audit blessed Tab or M for
        // an overview toggle; M is already TK_ToggleMouse, so Tab it is).
        bool tab = ActionDown(ks, "ToggleStarmap");
        if (!composing && tab && !_tabKeyWasDown)
        {
            _minimapExpanded = !_minimapExpanded;
            Console.WriteLine($"[monogame] minimap {( _minimapExpanded ? "EXPANDED" : "collapsed" )}");
        }
        _tabKeyWasDown = tab;

        // P272 — starmap cluster selection (info pane). ',' / '.' cycle the
        // selected sector back/forward WHILE the starmap is EXPANDED (the info
        // pane is not drawn in the compact corner mini-map), edge-triggered
        // like every other toggle and gated behind the same composing check so
        // typing in chat never cycles it. Keyboard cycling is chosen over mouse
        // hover: there is no map-cursor picking wired into the MonoGame flight
        // controls (the mouse is the M virtual-joystick steering axis, not a
        // picker), so a dedicated key pair is the established starmap-navigation
        // idiom here (Tab expands/collapses the map). ',' and '.' are the one
        // free, natural prev/next pair — every other key is claimed (arrows are
        // yaw, brackets are weapon cycle, OemTilde is group fire, M/V/K/C/N/T/G
        // all taken). First press with no selection starts at the player's sector.
        bool selPrev = ActionDown(ks, "SelectSectorPrev");
        bool selNext = ActionDown(ks, "SelectSectorNext");
        if (!composing && _minimapExpanded)
        {
            if (selPrev && !_selPrevKeyWasDown)
                CycleSelectedSector(delta: -1);
            if (selNext && !_selNextKeyWasDown)
                CycleSelectedSector(delta: +1);
        }
        _selPrevKeyWasDown = selPrev;
        _selNextKeyWasDown = selNext;

        // Camera mode cycle (V), edge-triggered like the M toggle — press to
        // advance through the modes available to the pilot's current state
        // (Cockpit → Chase → ExternalTurret → ExternalMissile, P295); holding V
        // does not re-fire. The external-turret view is offered only while
        // actually gunning (CameraCycler skips it otherwise). 'V' is unused by
        // FlightInput (WASD, arrows, Q/E, A/D, Escape and M are all taken), and
        // "View" is a mnemonic.
        bool vCam = ActionDown(ks, "CycleView");
        if (!composing && vCam && !_vKeyWasDown && !SpectatorCameraActive)
        {
            _cameraMode = Rendering.CameraCycler.Next(_cameraMode, IsTurretGunner());
            Console.WriteLine($"[monogame] camera: {_cameraMode}");
        }
        _vKeyWasDown = vCam;

        // P580 — native default F-key bindings (F4/F5/F7/F10/F11) for the ported
        // external modes; each toggles BACK to the cockpit view when already in
        // that mode, exactly like native's
        // SetViewToCombatMode((GetCameraMode()!=X)?X:Cockpit)
        // (WinTrek.cpp:9802-9812, 9816-9820, 9827-9835, 9867-9868). Choosing a
        // manual camera also cancels the director.
        ToggleCameraKey(ks, composing, "ViewExternalOrbit", CameraMode.ExternalOrbit, ref _f4KeyWasDown);
        ToggleCameraKey(ks, composing, "ViewFlyby", CameraMode.ExternalFlyBy, ref _f5KeyWasDown);
        ToggleCameraKey(ks, composing, "ViewTarget", CameraMode.ExternalTarget, ref _f7KeyWasDown);
        ToggleCameraKey(ks, composing, "ViewBase", CameraMode.ExternalBase, ref _f10KeyWasDown);
        ToggleCameraKey(ks, composing, "ViewExternalStation", CameraMode.ExternalStation, ref _f11KeyWasDown);

        // P580 — the director (F2) and spectator-follow (F3) toggles, plus the
        // rule that ANY flight input cancels the director immediately.
        HandleCinematicKeys(ks, composing);
        CancelDirectorOnInput(ks);

        // P612 — the spectator cameras (F8 free cam, F9 orbit cam) and the HUD
        // hide toggle (F1). While a spectator camera is active the ship holds
        // (neutral controls, see the control-send block below) and no stray
        // flight input leaks through.
        HandleSpectatorKeys(ks, composing, gameTime.ElapsedGameTime.TotalSeconds);

        if (_cameraMode == CameraMode.Command && !composing)
            HandleFlightMouseButtons(Mouse.GetState());

        // P222 — suicide (TK_Suicide): edge-triggered like the M/V toggles above
        // (press once per key-down, never per-frame while held). Gated behind the
        // same composing check so typing in chat / navigating the quick-chat menu
        // never triggers it. The server's HandleSuicide re-runs the exact combat-
        // kill path (KillShipEvent, launcher NULL), so a pressed key produces the
        // same KILL_SHIP death a combat death does — no confirmation dialog, no
        // hold-to-fire, no cooldown (native's own TK_Suicide is a plain instant
        // command). Bound to K — free in flight (W/S/Q/E/Arrows/A/D/Shift/R/M/V/
        // T/G/Enter/Back/Escape/Space all taken) and the natural mnemonic for
        // "kill".
        bool kSuicide = ActionDown(ks, "Suicide");
        if (!composing && kSuicide && !_kKeyWasDown)
        {
            _session.SendSuicide();
            Console.WriteLine("[monogame] suicide");
        }
        _kKeyWasDown = kSuicide;

        // P223 — drop chaff (TK_DropChaff): edge-triggered like the M/V/K toggles
        // above (press once per key-down, never per-frame while held), gated behind
        // the same composing check so typing in chat / navigating the quick-chat
        // menu never drops chaff. The server's HandleFireExpendable (Server.cs:6658)
        // resolves the mounted chaff launcher at mount 0, decrements its ammo, and
        // broadcasts FE(87)+CREATE_CHAFF(109)[+MISSILE_SPOOFED]+END_SPOOFING to the
        // sector — a plain instant command like native's TK_DropChaff. Bound to C —
        // free in flight (W/S/Q/E/Arrows/A/D/Shift/R/M/V/T/G/Enter/Back/Escape/Space/
        // K all taken) and the natural mnemonic for "chaff".
        bool cChaff = ActionDown(ks, "DropChaff");
        if (!composing && cChaff && !_cKeyWasDown)
        {
            _session.SendFireExpendable(EquipmentType.ChaffLauncher);
            Console.WriteLine("[monogame] drop chaff");
        }
        _cKeyWasDown = cChaff;

        // P413 — cloak toggle (TK_ToggleCloak, native WinTrek.cpp:9468-9471):
        // edge-triggered like the C chaff key above — a press flips the latched
        // _cloakToggled state, which Translate folds into the outgoing control
        // stateM as ShipControlState.CloakActive (0x800) every frame until toggled
        // back off. The server does the real work (Ship.UpdateCloak gates on a
        // mounted ET_Cloak part + not docked/boarded/ripcording and drains
        // energy). Bound to F — free in flight (W/S/Q/E/Arrows/A/D/Shift/R/M/V/
        // T/G/Enter/Back/Escape/Space/K/C/N/OemTilde/[ /]/X all taken; F is only
        // the LOBBY server-filter key, a separate screen) and adjacent to C
        // (chaff) — both defensive countermeasures, the same "near related
        // functions" placement the C chaff binding uses.
        bool fCloak = ActionDown(ks, "ToggleCloak");
        if (!composing && fCloak && !_fCloakWasDown)
        {
            _cloakToggled = !_cloakToggled;
            Console.WriteLine(_cloakToggled
                ? "[monogame] cloak ON (CloakActive)"
                : "[monogame] cloak OFF");
        }
        _fCloakWasDown = fCloak;

        // P414 — ripcord key (TK_Ripcord): edge-triggered like the F/C/K keys
        // above and gated behind the same composing check. Reuses the P403
        // starmap sector selection (_selectedSectorId) as the destination —
        // native's real player flow is "pick a destination, then ripcord
        // there", which a selected sector already is in this port. The pure
        // rule (RipcordKey.RequestFor) resolves the press: an ACTIVE ripcord →
        // send NA to cancel; a sector selected → send that sector to start the
        // countdown; neither → no wire message, surface a "select a sector"
        // system-line hint. Bound to L — free in flight (every single letter is
        // taken elsewhere; L is only the docked-shop "Launch" key, a separate
        // screen, the same dual-use pattern R/F already accept) and a loose
        // mnemonic for "ripcorLd".
        bool lRipcord = ActionDown(ks, "Ripcord");
        if (!composing && lRipcord && !_lKeyWasDown)
        {
            bool ripcordActive;
            short? selected;
            lock (_session.Gate)
            {
                ripcordActive = _session.World.RipcordActive;
                selected = _selectedSectorId;
            }
            // P723 — SIM-0287: native plays salShipCantRipcordSound and REFUSES
            // to ripcord when the local hull structurally cannot (TK_Ripcord,
            // WinTrek.cpp:9484 — GetBaseHullType()->HasCapability(c_habmNoRipcord)).
            // Mirror that gate before sending anything, so an incapable hull gets
            // its dedicated VO instead of a doomed server request.
            bool cantRipcord;
            lock (_session.Gate)
                cantRipcord = _session.World.MyShipId is { } rme
                    && _session.World.Ships.TryGetValue(rme, out var rship)
                    && _shopCatalog?.Hulls.FirstOrDefault(h => h.HullId == rship.HullId) is { } rhull
                    && rhull.HasCapability(DataHullTypeIGC.HabmNoRipcord);
            if (cantRipcord)
            {
                _sound?.PlayShipCantRipcord();
                Console.WriteLine("[monogame] ripcord: hull cannot ripcord (c_habmNoRipcord)");
            }
            else
            {
                short? target = Flight.RipcordKey.RequestFor(ripcordActive, selected);
                if (target is { } sid)
                {
                    _session.SendRipcordRequest(sid);
                    Console.WriteLine(sid == Allegiance.Core.Wire.RipcordRequest.NA
                        ? "[monogame] ripcord cancel (NA)"
                        : $"[monogame] ripcord request -> sector {sid}");
                }
                else
                {
                    lock (_session.Gate)
                        _session.World.PostSystemLine("Ripcord: select a sector on the starmap (, / .) first.");
                    Console.WriteLine("[monogame] ripcord: no sector selected, nothing to cancel");
                }
            }
        }
        _lKeyWasDown = lRipcord;

        // P224 — mine/probe drop (TK_DropMine, native's "Drop Mine or Probe",
        // trekinput.cpp:1424): edge-triggered like the C chaff key above and gated
        // behind the same composing check. It sends FIRE_EXPENDABLE(82) with
        // EquipmentType.Dispenser — the single mount value native's mine AND probe
        // dispensers share (CmineTypeIGC/CprobeTypeIGC both report ET_Dispenser,
        // minetypeigc.h:62-64 / probetypeigc.h:92-94) — and the server deploys
        // whichever of the two is mounted at mount 0 (HandleFireExpendable's subtype
        // dispatch, Server.cs:6698). Bound to N — free in flight (W/S/Q/E/Arrows/A/D/
        // Shift/R/M/V/T/G/Enter/Back/Escape/Space/K/C/OemTilde/[ /] all taken) and the
        // natural mnemonic for "miNe".
        bool nMine = ActionDown(ks, "DropMine");
        if (!composing && nMine && !_nKeyWasDown)
        {
            _session.SendFireExpendable(EquipmentType.Dispenser);
            Console.WriteLine("[monogame] drop mine/probe");
        }
        _nKeyWasDown = nMine;

        // P226 — fire missile (TK_FireMissile): HELD/continuous like afterburner
        // (Shift) and weapon fire (Space) — not edge-triggered — so holding the key
        // keeps requesting launches. It sends the FM_CS_FIRE_MISSILE(70) launch
        // request (ConnectionSession.SendFireMissile): native declares FIRE_MISSILE
        // a bidirectional CS message whose high-word missileFireIGC state bit can't
        // cross the 16-bit wire stateM (igc.h:829,2457-2465), so the wire request is
        // the player-launch path and the server fires authoritatively against its own
        // CommandTarget lock. A ~250 ms throttle bounds chatter while held; the
        // magazine's load deadline is the fire-rate limiter. Bound to Left Ctrl —
        // unclaimed by every other binding (W/S/Q/E/Arrows/A/D/Shift/R/M/V/T/G/
        // Enter/Back/Escape/Space/K/C/N/OemTilde/[ /] all taken) and the common
        // flight-sim missile-fire convention (the brief's own suggestion).
        bool missileCtrl = ActionDown(ks, "FireMissile");
        if (!composing && missileCtrl)
        {
            long nowMs = ClientNowMilliseconds;
            if (nowMs - _lastMissileSendMs >= 250 && _session.World.MissileLock.IsReady)
            {
                _lastMissileSendMs = nowMs;
                _session.SendFireMissile();
                Console.WriteLine("[monogame] fire missile (Left Ctrl held) -> FIRE_MISSILE(70)");
            }
        }
        else
        {
            // Re-arm the throttle promptly on release so a fresh hold starts
            // immediately; keep the edge cheap.
            _lastMissileSendMs = ClientNowMilliseconds - 250;
        }

        // P225 — single-weapon selection. Edge-triggered like the M/V/K/C
        // toggles above (press once per key-down, never per-frame while held),
        // gated behind the same composing check so typing in chat / navigating
        // the quick-chat menu never re-targets the fire mode.
        //   ` (backtick) → TK_ToggleGroupFire: flip between group-fire (all
        //     weapons, the P221 default) and single-weapon (OneWeapon — the
        //     selected fixed mount). The backtick is a punctuation key distinct
        //     from the [ / ] cycle keys, unclaimed by every other binding
        //     (W/S/Q/E/Up/Down/Left/Right/A/D/Shift/R/M/V/T/G/Enter/Back/
        //     Escape/Space/K/C all taken), and reads naturally as a "fire-mode"
        //     toggle.
        //   [ / ] → TK_PrevWeapon / TK_NextWeapon: step _selectedMount backward
        //     / forward through the fixed-mount selection range (wrapping at
        //     FixedMountCount()). The bracket pair is the task-blessed mnemonic
        //     for "cycle the selection". The selection is advanced even while
        //     group-fire is ON so it is already in place the moment group-fire
        //     is toggled off (it simply has no effect until then).
        UpdateFlightParts(ks, composing);
        bool gFire = ActionDown(ks, "ToggleGroupFire");
        if (!composing && gFire && !_gfireKeyWasDown)
        {
            _groupFire = !_groupFire;
            // P719 — SIM-0288/0289: native plays groupWeaponsSound /
            // ungroupWeaponsSound on TK_ToggleGroupFire (WinTrek.cpp:9529,9531).
            _sound?.PlayGroupFire(_groupFire);
            Console.WriteLine(_groupFire
                ? "[monogame] weapons: GROUP FIRE (all)"
                : $"[monogame] weapons: SINGLE (mount {_selectedMount})");
        }
        _gfireKeyWasDown = gFire;

        bool prevW = ActionDown(ks, "PrevWeapon");
        if (!composing && prevW && !_prevWKeyWasDown)
        {
            _selectedMount = FlightInput.PrevMount(_selectedMount, FixedMountCount());
            Console.WriteLine($"[monogame] selected mount {_selectedMount}");
        }
        _prevWKeyWasDown = prevW;

        bool nextW = ActionDown(ks, "NextWeapon");
        if (!composing && nextW && !_nextWKeyWasDown)
        {
            _selectedMount = FlightInput.NextMount(_selectedMount, FixedMountCount());
            Console.WriteLine($"[monogame] selected mount {_selectedMount}");
        }
        _nextWKeyWasDown = nextW;

        // P445 — persistent-throttle step and preset keys (native TK_ThrottleUp
        // /ThrottleDown/ThrottleZero/ThrottleFull, WinTrek.cpp:10515-10960),
        // edge-triggered like every other key here so a held key steps exactly
        // once per press. Keys are native's own bindings: = (OemPlus) / -
        // (OemMinus) step Up/Down by ±0.2, Backspace (Back) → Zero (-1) and
        // Backslash → Full (+1). The Backslash full key is bound to BOTH
        // MonoGame keys that map to the physical "\" key: OemPipe (the US-layout
        // backslash, where the physical key emits that scancode) AND
        // OemBackslash (the extra ISO/EU backslash) — so "full" is reachable on
        // any layout. Backspace is only otherwise the chat-delete key (inside
        // the chat composer, which sets composing=true and gates all of this
        // off). 33%/66% presets are deliberately left UNBOUND — native's
        // defaultinputmap.mdl binds neither. Latches are updated every frame
        // (outside the composing gate) exactly like the M/V toggles, so a key
        // released mid-composition can't misfire an edge.
        ApplyThrottleKeys(ks, composing);

        // P445 — mouse wheel steps the throttle the same way the = / - keys do
        // (native's mouseButtonCommandMap binds wheel-up→ThrottleUp and
        // wheel-down→ThrottleDown), paced by native's WheelDelay default.
        // ScrollWheelValue is cumulative in notches of WheelNotch (120) per
        // detent; we queue the accumulated notches and apply at most one step
        // per WheelStepIntervalMs (0.022 s — delay[2] at the default WheelDelay
        // preference of 2, WinTrek.cpp:3052, 8178) so fast scrolling can't
        // blast the whole queue in a single frame. Composing (chat) yields the
        // wheel to nothing else, but we keep it inert there to match the keys.
        // Mouse.GetState() is read every frame so the wheel base stays current
        // even while composing; the delta is only QUEUED/APPLIED when not
        // composing (chat/game menus never spin the throttle).
        //
        // P504 — the per-frame delta is normalised by FlightInput.WheelSignNormalizer
        // before queuing, correcting the sign inversion the Linux/Wayland wheel
        // delivery exhibits (see that member for the evidence) so that a real
        // scroll-DOWN reliably steps the throttle DOWN. The RAW (pre-normalisation)
        // delta is also logged (rate-limited to ~4 Hz while scrolling) so the
        // next live run captures exactly what the platform delivers for each
        // scroll direction — the evidence the P504 brief's step 1 requires.
        int wheelValue = Mouse.GetState().ScrollWheelValue;
        if (_lastWheelValue is { } last)
        {
            int rawDelta = wheelValue - last;
            long diagMs = ClientNowMilliseconds;
            if (rawDelta != 0 && diagMs - _lastWheelDiagMs >= 250)
            {
                _lastWheelDiagMs = diagMs;
                Console.WriteLine($"[monogame] wheel raw delta {rawDelta:+0;-0} (normalised sign {Math.Sign(FlightInput.NormalizeWheelDelta(rawDelta)):+0;-0})");
            }
            _wheelQueue += (int)FlightInput.NormalizeWheelDelta(rawDelta);
        }
        _lastWheelValue = wheelValue;

        if (_cameraMode == CameraMode.Command)
        {
            if (!composing && !_ptrLeftDown && !ActionDown(ks, "ZoomIn") && !ActionDown(ks, "ZoomOut") && _wheelQueue != 0) _commandCamera.ZoomWheel((float)_wheelQueue / FlightInput.WheelNotch);
            _wheelQueue = 0;
        }
        else if (!composing)
        {
            long nowMs = ClientNowMilliseconds;
            while (_wheelQueue >= FlightInput.WheelNotch && nowMs - _lastWheelStepMs >= WheelStepIntervalMs)
            {
                _throttle = FlightInput.StepThrottleUp(_throttle);
                _wheelQueue -= FlightInput.WheelNotch;
                _lastWheelStepMs = nowMs;
                Console.WriteLine($"[monogame] throttle wheel UP -> {_throttle:F2}");
            }
            while (_wheelQueue <= -FlightInput.WheelNotch && nowMs - _lastWheelStepMs >= WheelStepIntervalMs)
            {
                _throttle = FlightInput.StepThrottleDown(_throttle);
                _wheelQueue += FlightInput.WheelNotch;
                _lastWheelStepMs = nowMs;
                Console.WriteLine($"[monogame] throttle wheel DOWN -> {_throttle:F2}");
            }
        }

        // P624 — MATCH SPEED (TK_MatchSpeed, HELD, native IsTrekKeyDown
        // WinTrek.cpp:10966-10974): while held (and a target exists) the
        // persistent throttle is forced to match the current target's speed via
        // native's GetThrottle (WinTrek.cpp:122-134) — a stationary target pulls
        // it to −1 (stop), a target at/above our max speed to +1 (full). Sent
        // verbatim as ControlData.Throttle each frame, so own-ship prediction
        // and the server's cruise branch agree. Disabled while composing.
        if (!composing && ActionDown(ks, "MatchSpeed") && TryMatchSpeedThrottle(out float matched))
        {
            _throttle = matched;
            Console.WriteLine($"[monogame] match speed -> throttle {matched:F2} ({FlightInput.CruiseFraction(_throttle) * 100f:F0}%)");
        }

        // P624 — VECTOR LOCK (TK_LockVector, LATCHED toggle, native XOR
        // coastButtonIGC WinTrek.cpp:9458-9464): press toggles the coast bit,
        // folded into stateM via input.VectorLock below (ShipControlState.CoastButton).
        bool lockVec = ActionDown(ks, "LockVector");
        if (!composing && lockVec && !_lockVectorKeyWasDown)
        {
            _coastToggled = !_coastToggled;
            // P723 — SIM-0286: native plays vectorLockSound on every LockVector
            // key press (TK_LockVector, WinTrek.cpp:9463; wave "vector").
            _sound?.PlayVectorLock();
            Console.WriteLine(_coastToggled
                ? "[monogame] vector lock ON (coast)"
                : "[monogame] vector lock OFF");
        }
        _lockVectorKeyWasDown = lockVec;

        // P624 — LOOK CAMERAS (TK_View*, HELD, native SetHeadOrientation
        // WinTrek.cpp:9733-9791): only in cockpit/chase. Whichever look key is
        // held sets the absolute head yaw; releasing all of them snaps back to
        // forward (native's js.hat=0 default). Non-look/modes handled in
        // ComputeFlightCamera (resets the head on mode change).
        if (!composing && (_cameraMode == CameraMode.Cockpit || _cameraMode == CameraMode.Chase))
        {
            float head = 0f;
            if (ActionDown(ks, "ViewRear")) head = Rendering.HeadLook.Rear;
            else if (ActionDown(ks, "ViewFrontLeft")) head = Rendering.HeadLook.FrontLeft;
            else if (ActionDown(ks, "ViewFrontRight")) head = Rendering.HeadLook.FrontRight;
            else if (ActionDown(ks, "ViewLeft")) head = Rendering.HeadLook.Left;
            else if (ActionDown(ks, "ViewRight")) head = Rendering.HeadLook.Right;
            else if (ActionDown(ks, "ViewRearLeft")) head = Rendering.HeadLook.RearLeft;
            else if (ActionDown(ks, "ViewRearRight")) head = Rendering.HeadLook.RearRight;
            _headLookYaw = head;
        }

        // P624 — RADAR MODE (TK_CycleRadar, edge, native WinTrek.cpp:9955-9975):
        // cycle All → Default → Target → Minimal.
        bool cycRadar = ActionDown(ks, "CycleRadar");
        if (!composing && cycRadar && !_cycleRadarKeyWasDown)
        {
            _radarMode = Rendering.RadarLod.Next(_radarMode);
            Console.WriteLine($"[monogame] radar mode: {Rendering.RadarLod.Label(_radarMode)}");
        }
        _cycleRadarKeyWasDown = cycRadar;

        // P541 — left-click a sector on the star map (mini or expanded) to act on
        // it: ripcord target / teleport / docked view / own-sector no-op / goto.
        // Skipped while chat is composed so typing never trips a map click.
        // P752 — the expanded map also tracks hover + the info pane's expand
        // button first (native MouseMove + SectorInfoPane::Button).
        if (!composing)
        {
            if (HandleStarmapHoverAndExpandClick())
            {
                // The click landed on the expand button; still refresh the
                // button-edge latch so it is not reprocessed next frame.
                var m = Mouse.GetState();
                _mapLeftButtonWasDown = m.LeftButton == ButtonState.Pressed;
                _mapRightButtonWasDown = m.RightButton == ButtonState.Pressed;
            }
            else
            {
                HandleStarmapClick();
            }
        }

        // While composing chat, keyboard flight input is fully neutral — typing a
        // word must never also thrust/pitch/yaw the ship. (P761 — the gate itself
        // is the internal static FlightInput.GateCompose, pinned by test.)
        var input = GateCompose(composing, new FlightInputState(
            ThrottleForward: ActionDown(ks, "ThrustForward"),
            ThrottleBack:    ActionDown(ks, "ThrustBackward"),
            StrafeLeft:      ActionDown(ks, "ThrustLeft"),
            StrafeRight:     ActionDown(ks, "ThrustRight"),
            // P624 — vertical strafe (TK_ThrustUp/Down → UpButton/DownButton).
            // _fkExerciseStrafe is the proof-only override (ALLEGIANCE_FLIGHTKEYS_PROOF)
            // that drives strafe-up through the real send path without a physical key.
            StrafeUp:        _fkExerciseStrafe || ActionDown(ks, "ThrustUp"),
            StrafeDown:      ActionDown(ks, "ThrustDown"),
            PitchUp:         _cameraMode != CameraMode.External && ActionDown(ks, "PitchUp"),
            PitchDown:       _cameraMode != CameraMode.External && ActionDown(ks, "PitchDown"),
            YawLeft:         _cameraMode != CameraMode.External && ActionDown(ks, "YawLeft"),
            YawRight:        _cameraMode != CameraMode.External && ActionDown(ks, "YawRight"),
            RollLeft:        _cameraMode != CameraMode.External && ActionDown(ks, "RollLeft"),
            RollRight:       _cameraMode != CameraMode.External && ActionDown(ks, "RollRight"),
            // P761 — TK_RollModifier: while held, FlightInput.Translate routes the
            // yaw-axis contribution to roll (and zeroes yaw); the mouse block
            // below routes the mouse X deflection the same way.
            RollModifier:    _cameraMode != CameraMode.External && ActionDown(ks, "RollModifier"),
            // P220 — afterburner (TK_FireBooster): held, continuous (not
            // edge-triggered), reading either Shift key. Mirrors native's
            // IsTrekKeyDown(TK_FireBooster) sense (WinTrek.cpp:8270-8271);
            // Shift is the recollection default since the native binding
            // table isn't in this checkout (see KEYBINDING_AUDIT_P220.md).
            Afterburner:     ActionDown(ks, "FireBooster") || _boosterToggled,
            // P221 — fire (TK_FireWeapon): held, continuous (not edge-triggered)
            // like afterburner, so a held Space keeps firing (native
            // IsTrekKeyDown sense). Bound to Space — unclaimed by every other
            // binding (W/S/Q/E/Up/Down/Left/Right/A/D/Shift/R/M/V/T/G/Enter/
            // Back/Escape all taken). Absent a confirmed native default VK
            // table in this checkout (see KEYBINDING_AUDIT_P220.md), Space is
            // this port's own reasonable choice — the same justification P220
            // used for Shift→afterburner.
            Fire:            ActionDown(ks, "FireWeapon"),
            // P413 — cloak (TK_ToggleCloak): the LATCHED client toggle state set
            // by the F key above (edge-triggered), carried in each frame exactly
            // like GroupFire/SelectedMount; Translate folds it into the outgoing
            // stateM as CloakActive (0x800). Kept on the state struct so the
            // key→stateM wiring is pure and unit-testable.
            Cloak:           _cloakToggled,
            // P624 — vector lock: the LATCHED client toggle set by the L key
            // above, folded into stateM as CoastButton (native coastButtonIGC).
            VectorLock:      _coastToggled,
            // P225 — the client-local weapon-selection state carried in each
            // frame: group-fire vs the selected fixed mount (see the ` / [ / ]
            // handlers above). Translate folds these into the fire stateM
            // (AllWeapons when group-firing, OneWeapon|mount when not).
            GroupFire:       _groupFire,
            SelectedMount:   _selectedMount,
            // P445 — the client-owned persistent throttle axis, stepped by the
            // = / - keys and the mouse wheel above, sent verbatim as
            // ControlData.Throttle (idle -1 → full +1 cruise).
            Throttle:        _throttle));
        var cmd = FlightInput.Translate(input);

        // Raw mouse delta this frame, captured for BOTH the virtual-joystick
        // deflection below and the autopilot's manual-input disengage check
        // (a physical mouse movement past deadzone = manual flight input).
        int mouseDx = 0, mouseDy = 0;
        // P541 — while the expanded starmap (Tab) is up, the mouse becomes a free
        // cursor over the map (native's expanded SectorInfoPane is a cursor-mode
        // overlay): it stops being the steering axis and its position is meaningful
        // for hit-testing, so a left-click can pick a sector. The collapsed mini-map
        // keeps mouse-flight steering (the default M-off free cursor also lets the
        // mini-map be clicked when the steering axis is not enabled).
        if (_mouseFlightEnabled && !composing && !_minimapExpanded && !SpectatorCameraActive && !CommandCameraActive)
        {
            var ms = Mouse.GetState();
            int centerX = GraphicsDevice.Viewport.Width / 2;
            int centerY = GraphicsDevice.Viewport.Height / 2;
            mouseDx = ms.X - centerX;
            mouseDy = ms.Y - centerY;

            var defl = _mouseFlight.Update(mouseDx, mouseDy);

            // P506 — pass the integrated deflection through native's joystick
            // response curve (deadzone + rescale + quadratic) before it becomes
            // ControlData yaw/pitch. The deadzone (default 0.1) zeroes small
            // hand movements around centre so the ship can be held steady —
            // Domingo's 2026-10-02 playtest complaint. Shares the P380
            // MapJoystick deadzone/rescale; see MouseFlightController.MapResponse.
            var (outYaw, outPitch) = MouseFlightController.MapResponse(
                defl, _mouseFlight.Deadzone, _mouseFlight.LinearControls);
            // P761 — TK_RollModifier (WinTrek.cpp:10828-10834): while held the
            // mouse X deflection feeds ROLL and yaw is 0; released, X → yaw and
            // roll stays with the roll keys.
            bool rollMod = ActionDown(ks, "RollModifier");
            cmd = cmd with
            {
                Controls = cmd.Controls with
                {
                    Yaw = rollMod ? 0f : outYaw,
                    Pitch = outPitch,
                    Roll = rollMod ? outYaw : cmd.Controls.Roll,
                },
            };

            Mouse.SetPosition(centerX, centerY);
        }

        // P380 — real analog joystick flight input, as a post-Translate override
        // exactly like the mouse virtual-joystick above: keyboard stays the always-
        // present base layer; a connected joystick's axes override the ControlData
        // turn axes (and latch the throttle) when outside the deadzone, and its
        // buttons OR into stateM alongside the keyboard (fire/booster coexist, never
        // replace — native's unified IsTrekKeyDown(bReadKeyboard) sense). There is no
        // camera-orbit/free-look mode in this port (all four camera views fly the ship
        // with the arrow keys; see the P380 report on native's bInternalCamera gate),
        // so the joystick axes are read in every camera mode, as native does.
        bool joystickTurnActive = false;
        if (!composing)
        {
            var jsState = CurrentJoystickState();
            if (jsState is { IsConnected: true } jst)
            {
                var raw = JoystickFlightController.GatherRoles(jst.Axes, JoystickAxisLayout.DjiFpvRc3);
                var jsr = _joystickFlight.Update(raw);
                cmd = cmd with
                {
                    Controls = cmd.Controls with
                    {
                        // A turn axis only overrides the keyboard base layer when it
                        // is outside the deadzone (active); a centered stick leaves the
                        // keyboard arrow value standing, exactly the task-prescribed
                        // "override when outside deadzone" shape.
                        Yaw = jsr.YawActive ? jsr.Yaw : cmd.Controls.Yaw,
                        Pitch = jsr.PitchActive ? jsr.Pitch : cmd.Controls.Pitch,
                        Roll = jsr.RollActive ? jsr.Roll : cmd.Controls.Roll,
                        // Latched throttle: null keeps the keyboard/idle (-1) value.
                        Throttle = jsr.ThrottleOverride ?? cmd.Controls.Throttle,
                    },
                };
                joystickTurnActive = jsr.TurnAxesActive;

                // Merge joystick buttons with the keyboard's (native's
                // IsTrekKeyDown(TK_*, bReadKeyboard) unified check): fire uses the
                // same group/single-weapon selection as the Space key, booster the
                // same AfterburnerButton bit as Shift.
                if (IsJoystickButtonPressed(jst.Buttons, JoystickFlightController.FireButton))
                    cmd = cmd with { StateM = cmd.StateM | FlightInput.FireContribution(true, _groupFire, _selectedMount) };
                if (IsJoystickButtonPressed(jst.Buttons, JoystickFlightController.BoosterButton))
                    cmd = cmd with { StateM = cmd.StateM | ShipControlState.AfterburnerButton };
            }
        }

        // P499/P501 — gamepad OR touch flight input, the same post-Translate override
        // shape as the P380 joystick above. PRECEDENCE (documented): an active
        // gamepad wins (touch overlay hidden, touch ignored); otherwise TOUCH takes
        // the analog axes; on desktop with no pad and no touch the keyboard base
        // layer stands untouched (behaviour unchanged). Runs only when the USB-
        // joystick path did NOT take the turn axes, so the analog sources never
        // fight. Shoulder/button throttle steps reuse the P445 StepThrottleUp/Down
        // rules of the = / - keys (see ApplyGamePadFlight).
        if (!composing && !joystickTurnActive)
        {
            bool padConnected = CurrentPadState() is { IsConnected: true };
            if (padConnected)
                cmd = ApplyGamePadFlight(cmd);
            else if (TouchAvailable())
                cmd = ApplyTouchFlight(cmd);
        }

        // P587 — MOUSE BUTTONS in flight, mode-dependent (native TK_ToggleMouse,
        // WinTrek.cpp:9289-9298): with the virtual joystick ON the buttons FIRE
        // the ship (left = main weapon held, middle = missile held, right =
        // target-centre — the decoded mouseButtonCommandMap); with it OFF the
        // mouse is a free POINTER and clicks pick (left = own-side ships →
        // commandee, right = command targets incl. stations → target+autopilot).
        // The returned fire bits OR into the command exactly like the joystick
        // buttons above. Disabled while composing so typing never trips a click.
        if (!composing && !SpectatorCameraActive && !CommandCameraActive)
        {
            int mouseFire = HandleFlightMouseButtons(Mouse.GetState());
            if (mouseFire != 0)
                cmd = cmd with { StateM = cmd.StateM | mouseFire };
        }

        var world = _session.World;

        // P212 — player autopilot ("click-and-go"), edge-triggered like the
        // M/V toggles above so holding a key never re-fires. Skipped while chat
        // is composed so typing can't re-target or toggle the autopilot.
        //   T  → cycle to the next in-view candidate, nearest-to-crosshair first
        //        (ships except self/dead take priority; stations/asteroids only
        //        when no ship is in view). Repeated presses step through every
        //        candidate rather than re-picking the same nearest one.
        //   G  → toggle autopilot on/off toward the selected target.
        if (!composing)
        {
            bool gPress = ActionDown(ks, "ToggleAutopilot");
            if (gPress && !_gKeyWasDown)
            {
                // P403 — with the starmap EXPANDED (Tab) and a sector selected (,/.),
                // G is the native "goto" key: order your OWN ship to fly to the selected
                // sector (sent as a CHATBUOY c_buoyCluster order; the server turns it
                // into a real autopilot move). Collapse the map so the pilot watches the
                // flight. In normal flight G keeps its existing target-autopilot meaning.
                if (_minimapExpanded && _selectedSectorId is { } gotoCluster)
                {
                    IssueStarmapGoto(gotoCluster);
                    _minimapExpanded = false;
                    _gKeyWasDown = gPress;
                    return;
                }
                ToggleAutopilotForTarget();
            }
            _gKeyWasDown = gPress;

            // P623 — class-filtered targeting family (TK_Target{Enemy|Friendly}
            // {Bomber|Fighter|Miner|Constructor|Lifepod|MajorBase}[Nearest|Prev]
            // + TK_TargetHostile). Native defaults: O/I/K/H/J × plain/Shift/Ctrl =
            // next/nearest/prev (defaultinputmap.mdl); unbound commands are wired
            // here too so a F6 rebind works. See HandleP623TargetingKeys.
            // Dispatched above even while composing, to consume held press edges.

            // P417 — Target Nothing / Center, the small leftover targeting keys
            // (native TK_TargetNothing/Center). Edge-triggered and composing-
            // gated like U/Z/T above.
            //   ;      → target nothing (clear target + disengage autopilot)
            //   '      → target center (nearest to the reticle/crosshair)
            //   (Target self — TK_TargetSelf — is deliberately NOT bound here:
            //   native's defaultinputmap.mdl leaves TK_TargetSelf UNBOUND by
            //   default, and binding it to Y conflicted with the P282 ready key
            //   (Y), letting the ready press leak into flight and select the
            //   player's own ship — see P523.)
            // Keys: ; and ' are punctuation unclaimed by every binding
            // (grep-verified; the sole OemSemicolon/OemQuotes-adjacent
            // keys in use are , / . starmap nav and the [+/-] music volume).
            bool semiNothing = ActionDown(ks, "TargetNothing");
            if (semiNothing && !_semicolonNothingKeyWasDown)
                ClearTarget();
            _semicolonNothingKeyWasDown = semiNothing;

            bool apostropheCenter = ActionDown(ks, "TargetCenter");
            if (apostropheCenter && !_apostropheCenterKeyWasDown)
                SelectCentermostTarget();
            _apostropheCenterKeyWasDown = apostropheCenter;

            // P417 — screenshot (TK_ScrnShot): latch a request; the actual
            // backbuffer capture happens at the end of Draw (Game1.Rendering.cs)
            // so the PNG holds the completed frame. P625 — per Domingo
            // 2026-10-12 "native wins the F-keys": F11 is now the external
            // station camera, so screenshot moved to PrintScreen (native's own
            // TK_ScrnShot default binding, defaultinputmap.mdl line 108).
            // Read through the live action map so a rebind takes effect.
            bool shot = KeyOf("ScreenShot").Matches(ks);
            if (shot && !_shotKeyWasDown)
                _screenshotRequested = true;
            _shotKeyWasDown = shot;

            // P559 — one-shot live-proof hook: ALLEGIANCE_AUTOSHOT=<delayMs>
            // (e.g. 12000) requests ONE real-backbuffer screenshot <delayMs>
            // after the first Flying update, using the exact same
            // CaptureBackbuffer path as F11, so a windowed live proof captures
            // the actual presented backbuffer without keyboard injection.
            if (!_autoShotDone && Environment.GetEnvironmentVariable("ALLEGIANCE_AUTOSHOT") is { Length: > 0 } autoShot)
            {
                if (_autoShotStartMs <= 0)
                    _autoShotStartMs = ClientNowMilliseconds;
                int delayMs = int.TryParse(autoShot, out var ad) && ad > 0 ? ad : 8000;
                if (ClientNowMilliseconds - _autoShotStartMs >= delayMs)
                {
                    // P570 — dismiss the briefing popup first so the real-backbuffer
                    // proof frame isn't obscured by it (the briefing only auto-
                    // dismisses in the offscreen-capture path).
                    if (IsBriefingVisible(out _)) DismissBriefing();
                    _screenshotRequested = true;
                    _autoShotDone = true;
                }
            }

            // P625 — real-window auto-proof of the Controls popup + live help
            // (ALLEGIANCE_KEYMAP_PROOF=1 drives the scripted rebind sequence so
            // live-proof.sh can photograph the real backbuffer).
            if (Environment.GetEnvironmentVariable("ALLEGIANCE_KEYMAP_PROOF") == "1")
                RunKeymapProof(gameTime.ElapsedGameTime.TotalSeconds);

            // P624 — real-window auto-proof of the flight-keys pack (look cameras,
            // radar modes, and the vector-lock / match-speed / vertical-strafe
            // commands) so live-proof.sh can photograph the real backbuffer and
            // the server log's [ctrlak] reject count can be checked.
            if (Environment.GetEnvironmentVariable("ALLEGIANCE_FLIGHTKEYS_PROOF") == "1")
                RunFlightKeysProof(gameTime.ElapsedGameTime.TotalSeconds);

            // P626 — real-window auto-proof of the in-flight utility pack (help
            // overlay with the new commands, chat paging, cargo selection) so
            // live-proof.sh can photograph the real backbuffer.
            if (Environment.GetEnvironmentVariable("ALLEGIANCE_INFLIGHT_PROOF") == "1")
                RunInFlightUtilityProof(gameTime.ElapsedGameTime.TotalSeconds);

            // P425 — command a same-side ship/drone (e.g. a Builder) from the
            // MonoGame client. Commander needs TWO selections at once — WHO to
            // command (the commandee, distinct from the target reticle) and WHAT
            // to order them AT (the existing _autoTarget). Edge-triggered and
            // composing-gated like every key above.
            //   PageDown → cycle the COMMANDEE through same-side ships (nearest
            //              first, wraps; first press = nearest). Reuses the P415
            //              friendly-ship filter machinery (CollectTargetCandidates
            //              + FilterCandidates + StepCycle) into the separate
            //              _commandee slot — no new candidate-gathering system.
            //   Insert   → ISSUE ORDER: send the real ORDER_CHANGE (ShipID =
            //              commandee, CommandId = resolved from the commandee's
            //              role + the target's kind — c_cidBuild for a
            //              Builder-role commandee at an asteroid target). The
            //              server re-validates with its real legality gate and
            //              refuses (surfaced as a private "Negative." chat) if
            //              the request is illegal. Console feedback on what was
            //              sent (or why nothing was sent).
            // Keys: PageDown/Insert are unclaimed (grep-verified — no binding
            // uses either; PageUp/F5 are also free but PageDown reads naturally
            // as "reach down the roster" and Insert as "dispatch the order").
            bool pgdn = ActionDown(ks, "CycleCommandee");
            if (pgdn && !_pageDownKeyWasDown)
                SelectCommandee(cycle: true);
            _pageDownKeyWasDown = pgdn;

            bool ins = ActionDown(ks, "IssueBuild");
            if (ins && !_insertKeyWasDown)
                TryIssueOrderToCommandee();
            _insertKeyWasDown = ins;

            // P416 — Promote-Turret seat keys (TK_PromoteTurret1-4, native
            // WinTrek.cpp:9583-9601): the captain's hand-off. Digit N promotes
            // the boarded gunner riding turret seat N of the ship you're flying
            // (mount = (N-1) + MaxFixedWeapons) into your command seat in place —
            // the server does the real role-swap (P140 HandlePromote →
            // Ship.Promote, demoting you into the vacated mount). Only meaningful
            // when your ship actually HAS a boarded gunner in that seat; a mount
            // the flown hull doesn't own is refused client-side (no wire message),
            // mirroring native's own `turret < pht->GetMaxWeapons()` guard
            // (WinTrek.cpp:9593). Edge-triggered like every other key here so
            // holding a digit never re-fires. Digits 1-4 are unclaimed in flight
            // (KeyBindingTable audit; the only digit use is the docked-shop "buy"
            // affordance, a separate screen).
            bool p1T = ActionDown(ks, "PromoteTurret1");
            if (p1T && !_p1TurretKeyWasDown) TryPromoteTurretSeat(1);
            _p1TurretKeyWasDown = p1T;

            bool p2T = ActionDown(ks, "PromoteTurret2");
            if (p2T && !_p2TurretKeyWasDown) TryPromoteTurretSeat(2);
            _p2TurretKeyWasDown = p2T;

            bool p3T = ActionDown(ks, "PromoteTurret3");
            if (p3T && !_p3TurretKeyWasDown) TryPromoteTurretSeat(3);
            _p3TurretKeyWasDown = p3T;

            bool p4T = ActionDown(ks, "PromoteTurret4");
            if (p4T && !_p4TurretKeyWasDown) TryPromoteTurretSeat(4);
            _p4TurretKeyWasDown = p4T;

            // P366 — team-leader bot dock/launch command. Edge-triggered X key:
            // when the local player is the team leader AND the current T-selected
            // target is a same-side bot, issue the ORDER_CHANGE that commands that
            // bot to dock (flying) or launch (docked) — mirroring native's
            // leader-only drone context action (WinTrek.cpp contextDockDrone,
            // gated on IsTeamLeader at WinTrek.cpp:4349-4368). The label field of
            // _leaderBotCommand carries the current intent ("DOCK"/"LAUNCH");
            // both send commandID=c_cidGoto + command=c_cmdAccepted, which the
            // server's dock/launch arms act on. A non-leader never has
            // _leaderBotCommand set, so this key does nothing for them — no UI,
            // no wire — matching native's gate.
            bool xPress = ActionDown(ks, "LeaderCommand");
            if (xPress && !_xKeyWasDown && _leaderBotCommand is { } lbCmd)
            {
                _session.SendOrderChange(lbCmd.TargetShipId, Allegiance.Core.Wire.OrderChange.CidGoto, Allegiance.Core.Wire.OrderChange.CmdAccepted);
                Console.WriteLine($"[monogame] leader command: {lbCmd.Label} on bot {lbCmd.TargetShipId} (ORDER_CHANGE c_cidGoto/c_cmdAccepted)");
                _sound?.PlayAutopilotEngage();
            }
            _xKeyWasDown = xPress;

            // P424 — accept / reject / clear a received queued order (native
        }

        // Native behaviour (WinTrek.cpp's SenseJoystick): a real manual
        // deflection past the deadzone disengages autopilot before that frame's
        // input is applied. Detect it from the raw key state and the INTEGRATED
        // mouse-joystick deflection — not the computed cmd, which the autopilot
        // would otherwise be masking anyway. The mouse path compares the live
        // deflection against the engage-time baseline (M-1) with native's
        // 2×-deadzone threshold, so ordinary hand tremor (tiny cursor deltas
        // that barely nudge the integrated deflection) no longer dumps the
        // autopilot; only a meaningful virtual-stick move does. The keyboard
        // side is binary (keys pressed/unpressed) and stays a raw check.
        bool mouseManualInput = _mouseFlightEnabled
            && _mouseFlight.DeflectionMovedPast(_autoPilotMouseBaseline, MouseFlightController.AutopilotDisengageThreshold);
        bool manualFlightInput =
            !composing && !SpectatorCameraActive && !CommandCameraActive && (
            input.ThrottleForward || input.ThrottleBack || input.StrafeLeft || input.StrafeRight ||
            input.PitchUp || input.PitchDown || input.YawLeft || input.YawRight ||
            input.RollLeft || input.RollRight ||
            mouseManualInput ||
            joystickTurnActive);   // P380 — a real stick deflection past deadzone is manual input too
        if (_autoPilotEngaged && manualFlightInput)
            DisengageAutopilot(AutopilotDisengageReason.ManualInput);

        // Send controls: while autopilot drives, its steering REPLACES the
        // manual command; station docking also supplies native directional
        // thrusters while turning. These controls bypass FlightInput's
        // keyboard idle-throttle convention. If the target can no longer be resolved (died/vanished)
        // disengage and fall back to the manual command.
        //
        // P464 — while seated as a turret gunner, NO flight SHIP_UPDATE is
        // sent at all: the gunner drives the turret via
        // UpdateTurretGunner (ACTIVE/INACTIVE_TURRET_UPDATE), and sending a
        // normal CommandControls would fight the boarded state (native's
        // BaseClient::SendUpdate picks the parented branch and never sends
        // SHIP_UPDATE for a boarded ship, clintlib.cpp:1818-1905).
        // P447b — capture the EXACT command this frame actually sends, so the predictor
        // steps with the same (controls, stateM) that went through CommandControls
        // (manual or autopilot) — see the Step call after the block.
        ControlData sentControls;
        int sentStateM;
        int sentFireM;
        bool turretGunner = IsTurretGunner();
        if (turretGunner)
        {
            sentFireM = UpdateTurretGunner(CommandCameraActive ? new ControlData(0, 0, 0, -1) : cmd.Controls, CommandCameraActive ? 0 : cmd.StateM, (float)gameTime.ElapsedGameTime.TotalSeconds);
            sentControls = cmd.Controls;
            sentStateM = cmd.StateM;
        }
        else
        {
            // Left the turret (or was never on one) — a fresh board must
            // re-seed the aim orientation and re-arm the turret send cadence.
            _turretSeeded = false;
            _turretCadence.Reset();
            _autopilotDeltaSeconds = (float)gameTime.ElapsedGameTime.TotalSeconds;
            if (_autoPilotEngaged && TryGetAutopilotControls(out var apControls))
            {
                sentControls = apControls;
                sentStateM = _autopilotMovementButtons;
                sentFireM = 0;   // autopilot drives with neutral fire
            }
            else
            {
                if (_autoPilotEngaged) DisengageAutopilot(AutopilotDisengageReason.TargetLost);
                sentControls = cmd.Controls;
                sentStateM = cmd.StateM;
                sentFireM = cmd.StateM;
            }
        }

        // P612 — while a spectator camera is active, the player's ship HOLDS:
        // send neutral controls (zero steering, throttle at stop) so no stray
        // WASD/mouse/joystick input leaks through to it (the brief's "no stray
        // inputs"). The local sim steps with these same neutral controls, so
        // the ship coasts to a stop / stays where it is, exactly native's
        // released-stick behaviour.
        if (SpectatorCameraActive || (CommandCameraActive && !_autoPilotEngaged))
        {
            sentControls = new ControlData(0f, 0f, 0f, -1f);
            sentStateM = 0;
            sentFireM = 0;
        }

        // P508 — fly the own ship LOCALLY with the EXACT controls the client sends
        // this frame (manual OR autopilot): the local simulation is the authority and
        // its pose is what renders and what the native SHIP_UPDATE reports. A control
        // change moves the local render pose on the same frame (no round trip).
        // Skipped while a turret gunner is seated: no SHIP_UPDATE is sent then, so the
        // sim is held (inert) — the gunner's view is the locally-integrated turret facing.
        //
        // TR-2 — while a training mission is running, the mission's control masks gate the
        // outgoing controls exactly as native's flight input does (ApproveControls/
        // ApproveActions, Training.cpp:253-300, called behind IsTraining). Applied here, on
        // the final command (after autopilot/spectator overrides), so the SAME masked value
        // feeds the local sim and the wire — and the simulated pose reflects the training
        // constraints. Inert when no mission is running (TrainingController.IsTraining false).
        if (Allegiance.ClientCore.Training.TrainingController.IsTraining)
        {
            sentControls = Allegiance.ClientCore.Training.TrainingController.ApproveControls(sentControls);
            sentStateM = Allegiance.ClientCore.Training.TrainingController.ApproveActions(sentStateM);
            sentFireM = sentStateM;
        }

        // HUD-8 — stage the exact command this frame sent so DrawHud's snapshot
        // can feed the native joystick turning indicator (jsValues[c_axisYaw]/
        // [c_axisPitch], indicator.cpp:154-177) without re-reading any input.
        _lastSentControls = sentControls;

        bool sendArm = !turretGunner;
        _ownShipSendArmed = sendArm;
        if (sendArm)
            StepOwnShipSimulation(gameTime.ElapsedGameTime.TotalSeconds, sentControls, sentStateM);

        // Recompute every ship's render pose from its raw last-known
        // ServerShipUpdate — no smoothing/prediction (see PredictedPose's
        // remarks). Locked against the receive thread's concurrent upserts
        // into world.Ships — see ConnectionSession.Gate's doc comment.
        GatherRenderPoses(gameTime, sentFireM);

        // P508 — send the native SHIP_UPDATE at the 0.15 s cadence from the current
        // local simulation pose + the adopted cookie + the server-converted time. Runs
        // after the pose gather (the gate above just closed) so a just-applied
        // SHIP_RESET / SET_CLUSTER seed + cookie are reflected; the server-clock feed
        // also happened under the gate. No-op while frozen (docked/dead/ripcord/warp),
        // unseeded, or no server clock yet.
        MaybeSendOwnShipUpdate();

        // P363 — drop ribbons for ships that weren't in this frame's pose gather
        // (dead / left the visible set), matching native tearing a destroyed
        // ship's trail site down at once.
        _motionTrail.PruneUntouched();

        // P232 — weapon-fire + projectile in-flight ambient sound, driven from
        // the local ship's perspective this frame (see the method for details).
        UpdateProjectileSounds();

        // P236 — the three missile sound cues (launch one-shot, in-flight ambient
        // loop, tiered explosion one-shot), driven from the live in-sector missile
        // set this frame (see the method for details).
        UpdateMissileSounds();

        // P264 — the incoming-missile warning loop, driven from the server-streamed
        // "an enemy missile is tracking YOU" state this frame (see the method).
        UpdateMissileWarning();

        // P260 — the two probe sound cues (deploy one-shot on the local ship's own
        // drop, and the scanning ambient loop while an own-side probe is active in
        // the current sector), driven from the live in-sector probe set this frame
        // (see the method for details).
        UpdateProbeSounds();

        // P237 — the warp ambient loop (alephloop), driven from the live in-sector
        // warps within audible range this frame (see the method for details).
        UpdateWarpSounds();

        // P267 — the station exterior ambient loop (shipout), driven from the
        // live in-sector stations within audible range this frame (see the method).
        UpdateStationSounds();

        // P711 — the ship low-status alarm edge detection (SIM-0361..0366,
        // trekigc.cpp:1264-1275) and the ripcord-active + mining loop gates
        // (SIM-0368/0370, :1287/:1293). Records the one-shot events drained
        // below and replays the two loop gates against the audio manager.
        UpdateShipStatusAlarms();

        // P623 — VICTIM_DAMAGED(217) attributions drained into the local ship's
        // TK_TargetHostile damage window (who hurt me and by how much).
        UpdateHostileTracking();

        // P578 — eject/death/rescue feedback lines (tips + critical/non-critical
        // message cues) and the eject "gloat camera" swing to the killer,
        // drained from the world's KILL_SHIP/PLAYER_RESCUED queues each frame.
        UpdateFeedback();

        // P760b/SIM-0197 — killed-current-target retargeting, drained from the
        // world's KILL_SHIP queue each frame on the game thread (the receive
        // thread only enqueues; the retarget reads the own pose and mutates
        // Game1's target/autopilot state).
        UpdateTargetKilled();

        // P463 — the four projectile hit sounds (my/other × shield/hull), drained
        // from the same PROJECTILE_HULL_HIT(213) wire stream the spark renderer
        // consumes, at most one cue per target per frame.
        UpdateHitSounds();

        // P463c — collisions (COLLISION_EVENT(216)): the positional collision
        // one-shot once per collision in the local sector, plus P449b's camera
        // jiggle feed when the local ship is a party.
        UpdateCollisionSounds();

        // P574 — world explosions (EXPLOSION_EVENT(207)): the native tiered
        // positional sound for every ship/station/asteroid/mine blast in the
        // local sector (missile detonations excluded — already voiced above).
        UpdateExplosionSounds();

        // P282 — ready-up is NO LONGER automatic here. The old hack sent
        // SendPlayerReady(true) unconditionally the instant the ship existed,
        // which lied to the server's P180 auto-start gate. Today the player
        // toggles ready on the pre-flight READY-CHECK screen (UpdateReadyCheck);
        // once every occupied side is aggregate-ready the server starts the
        // countdown and UpdateReadyCheck hands off to this Flying state. Launch
        // (undock) is likewise not automatic — when docked the hangar/loadout
        // screen (P268) takes over and the player launches via BUY_LOADOUT
        // (fLaunch=true) or the fallback timer.

        // trekigc.cpp:1016-1075: own-ship ripcord voice uses the same
        // countdown waves, with the native -7 dB gain (sounddef.mdl:353-362).
        // Mission countdown audio is handled once by UpdateMissionCountdownSound.
        float ripcordLeft = _ripcordCountdownSound.TimeLeft(world.RipcordActive,
            world.RipcordActiveSinceLocalMs, ClientNowMilliseconds,
            RipcordDurationSeconds(world), world.TimeScale);
        if (_ripcordCountdownSound.Update(world.RipcordActive, ripcordLeft) is { } ripcordSecond)
            _sound?.PlayCountdownSecond(ripcordSecond, Audio.RipcordCountdownSound.Gain);

        // Game-over: fire once on the real transition of world.Mission.Stage
        // into Over — driven by the server's GAME_OVER(62) broadcast →
        // ClientWorld.Apply(GameOver) — reusing the exact same world.Mission /
        // stage read as the auto-undock above (no second notion of "ended").
        if (world.Mission is { } gmi && gmi.Stage == (int)MissionStage.Over && !_gameOverCuePlayed)
        {
            _gameOverCuePlayed = true;
            _sound?.PlayGameOver();
            Console.WriteLine("[monogame] audio: game-over");
        }

        base.Update(gameTime);
    }

    /// <summary>P448b — resolve a REMOTE ship's render pose for this frame. When the
    /// kill switch is on and the ship's P448a dead-reckoner is seeded, this is the
    /// real flight model replayed from the last update to "now" (native CalculateShip),
    /// so a turning/thrusting ship keeps moving between packets instead of stepping at
    /// 20 Hz and lagging one packet behind. Returns a COPY of the pose: the reckoner's
    /// orientation scratch mutates on every PoseAt call, and this pose is shared this
    /// frame by the hull draw, camera/radar/targeting projections and every attached
    /// effect, so each ship gets one stable orientation object. Falls back to the raw
    /// last-known pose when the kill switch is off or the reckoner isn't seeded (a
    /// ship with no pose was already excluded by the gather guard).</summary>
    /// <summary>P803 — the carrier a boarded gunner rides this frame (-1 = not
    /// boarded): its hull is hidden in the turret cockpit view like the own hull.</summary>
    private short _turretCarrierId = -1;

    private void GatherRenderPoses(GameTime gameTime, int sentFireM = 0)
    {
        var world = _session.World;
        _poses.Clear();
        _turretCarrierId = -1;
        _teleportEmitterCount = 0;   // P781 — rebuilt every frame below (preallocated array)
        _damageEmitters.Clear();
        _engineTrailEmitters.Clear();
        _nozzleGlowEmitters.Clear();
        // Nav strobes are re-gathered per visible ship below like every other
        // hull effect. Without this clear, a ship that died, docked, warped out or
        // left view kept its last strobes forever — frozen lights piling up at
        // battle spots and alephs (2026-10-15, Domingo in clash48/49).
        _navStrobeEmitters.Clear();
        _cloakedShips.Clear();
        _shipModelById.Clear();
        _shipTextureById.Clear();   // P337 — parallel per-ship texture cache
        _shipScaleById.Clear();
        _nativeShipRefById.Clear(); // P566 — parallel per-ship native-art cache
        float updateDt = (float)gameTime.ElapsedGameTime.TotalSeconds;   // P358 — powers the afterburner ramp
        lock (_session.Gate)
        {
            short? viewSector = LocalSpectating ? _skirmishCamera.SectorId : world.MyShipId is { } viewId && world.Ships.TryGetValue(viewId, out var viewShip)
                ? RenderSector(viewShip.SectorId) : null;
            if (_flightEffects.BeginView(world.SectorGeneration, viewSector))
            {
                _debrisGeo.Clear();
                _shipThrustPower.Clear();
                _lastShipSector.Clear();
                _warpRenderer?.ResetTransits();
                _pendingTeleportBurst = null;
                _pendingHullHits.Clear();
                _lastSparkPose.Clear();          // P781 — frame-velocity history is per-view
                _ripcordSpeedByHull.Clear();     // P781 — re-resolve hull ripcord speeds per view
            }

            // P373 — drain the one-shot "new contact" (first-sighting) cues
            // into the audio manager. They were recorded on the wire-presence
            // edge under this same gate, so each genuine new contact plays
            // exactly once (enemy vs friendly each get their own real cue).
            foreach (var cue in world.TakeNewContactCues())
                _sound?.PlayNewContact(cue.IsEnemy);

            // P374 — drain the one-shot sector-threat cues (a warning increase
            // and/or a sector-secured/lost transition) into the audio manager.
            // Recorded on the real ClusterWarningUpdate edge under this same
            // gate, so each genuine threat transition plays exactly once.
            foreach (var cue in world.TakeClusterWarningCues())
                _sound?.PlaySectorAlert(cue);

            if (world.MyShipId is { } me)
            {
                // P803 — a boarded gunner/observer has no ship of its own to fly:
                // native's gunner controls only aim the turret, and its view rides
                // the carrier (GetSourceShip). Running the own-ship flight sim here
                // let the gunner's inputs fly an unseated copy far away from the
                // carrier ("decoupled", seen live 2026-10-24); the scene pose is
                // the carrier's instead.
                if (world.Ships.TryGetValue(me, out var boardedSelf) && boardedSelf.ParentId >= 0
                    && world.Ships.TryGetValue(boardedSelf.ParentId, out var carrierEnt))
                {
                    _turretCarrierId = boardedSelf.ParentId;
                    if (carrierEnt.Latest is { } carrierUp)
                        _poses[me] = new Flight.PredictedPose(carrierUp.Position, carrierUp.Orientation);
                }
                // Own ship also drives the P213 engine/ambience mix from its
                // real telemetry.
                else if (world.Ships.TryGetValue(me, out var mine) && mine.Latest is { } mineUp)
                {
                    // P508 — wire the own-ship local simulation for this frame BEFORE
                    // reading the render pose: feed the server clock, seed/re-seed the
                    // sim (first sight + SHIP_RESET) and apply the hard-transition
                    // freeze/resume. Runs under this gate.
                    UpdateOwnShipSimulation(world, mine, mineUp);

                    // P508 — the own ship's render pose IS the local simulation (the
                    // client flies its own ship; no reconciliation/snap). When the debug
                    // switch is off (--noPrediction) or no hull is resolved yet, render
                    // the raw last ServerShipUpdate pose. P255's mining-facing override
                    // is applied ON TOP of whichever pose, so the mining autopilot's
                    // turn-to-face still works exactly as before.
                    bool predActive = _ownShipPrediction && _ownSim.HaveHull;
                    SimVec ownPos = predActive ? _ownSim.Position : mineUp.Position;
                    Orientation ownOri = predActive ? _ownSim.Orientation : mineUp.Orientation;
                    Orientation poseOrientation = ownOri;
                    var rockId = world.MiningAsteroidId;
                    if (rockId != -1 && world.Asteroids.TryGetValue(rockId, out var rockAst))
                        poseOrientation = MiningFacing(ownOri, ownPos, rockAst.Position,
                            (float)gameTime.ElapsedGameTime.TotalSeconds);
                    _poses[me] = new Flight.PredictedPose(ownPos, poseOrientation);
                    // P781 — the own ship sprays lead-up sparks too (native runs
                    // AddTeleportParticles for every visible ship, own included,
                    // thinggeo.cpp:503-528), but only while its hull is drawn:
                    // native's render mask hides the own hull in the internal
                    // cockpit view (SetVisible / SetVisibleShip,
                    // trekigc.cpp:1646-1659; m_bVisibleShip gates
                    // AddTeleportParticles, thinggeo.cpp:515-527), the same gate
                    // the port already uses for the P297/P358/P365 own-ship
                    // effects below.
                    if (_cameraMode != CameraMode.Cockpit)
                    {
                        float ownRipT = world.RipcordActive
                            ? Allegiance.ClientCore.Flight.RipcordLeadup.TimeUntilTeleport(world.RipcordActiveSinceLocalMs,
                                ClientNowMilliseconds, RemoteRipcordDuration(world, mine), world.TimeScale)
                            : -1f;
                        GatherTeleportEmitter(world, me, ownPos,
                            predActive ? _ownSim.Velocity : mineUp.Velocity, ownOri,
                            _myRenderRadius > 0f ? _myRenderRadius : Rendering.WarpTransitParticles.ShipRadiusDefault,
                            ownRipT, viewSector ?? -1, updateDt);
                    }
                    // P462 — emit the local own-shot echo at the trigger frame,
                    // read through the renderer's own-ship pose (_poses[me] — the
                    // P447 prediction seam), and play one fire one-shot per round
                    // emitted. Each server echo of these rounds is de-duped, so
                    // each shot renders and sounds exactly once.
                    EmitOwnShots(mine, mineUp, _poses[me], sentFireM);
                    // P333 — own ship was excluded from model/scale resolution
                    // entirely (only the teammate loop below populated these),
                    // so the local hull always drew the unnormalized fallback
                    // model at scale 1 in every non-cockpit camera mode — the
                    // single most-seen ship in the game, made WORSE (not
                    // better) once every other ship got correctly scaled.
                    // Mirrors the teammate loop's own resolution exactly.
                    _shipModelById[me] = ResolveShipModel(mine.SideId, mine.HullId);
                    _shipTextureById[me] = ResolveShipTexture(mine.SideId, mine.HullId);   // P337
                    if (_shipsNative)                                                        // P566
                        _nativeShipRefById[me] = ResolveNativeShip(mine.HullId);
                    var myDrawnModel = _shipModelById[me] ?? _fallbackModel;
                    _shipScaleById[me] = Rendering.ShipRenderScale.ScaleForLength(
                        HullLength(mine.HullId), myDrawnModel);
                    // P338 — the local ship's real rendered radius (0.5 × Length),
                    // fed to the chase camera's radius-adaptive framing below.
                    float myLen = HullLength(mine.HullId);
                    _myRenderRadius = myLen > 0f ? 0.5f * myLen : 0f;
                    // P297 — the damage-debris smoke stream (native
                    // AddDamageParticles). Like native (WinTrek.cpp:6616-6621,
                    // which hides the local ship's damage display in the
                    // internal/cockpit view and shows it in external views), the
                    // local ship only vents in non-cockpit camera modes.
                    if (_cameraMode != CameraMode.Cockpit)
                        GatherDamageEmitter(me, mineUp.HullFraction, ownPos, mineUp.Velocity, ownOri, _myRenderRadius);
                    // P358 — the local ship's engine trail: the afterburner power
                    // ramp updates every frame, but the trail is emitted only
                    // outside the cockpit (the hull/afterburner aren't drawn in
                    // the internal cockpit view), matching the P297 gate.
                    UpdateEngineTrail(me, (mineUp.StateM & Allegiance.Core.Sim.ShipControlState.AfterburnerButton) != 0,
                        ownPos, mineUp.Velocity, ownOri, _myRenderRadius, updateDt,
                        _cameraMode != CameraMode.Cockpit);
                    // P365 — the local ship's engine-nozzle glow: gathered only
                    // outside the cockpit (its hull isn't drawn there), matching
                    // the P358 trail's gate. Driven by the real throttle +
                    // afterburner bit, so the glow appears and colour-shifts as
                    // the ship thrusts.
                    UpdateNozzleGlow(me, mineUp.StateM, mineUp.Controls.Throttle, ownPos, ownOri,
                        _myRenderRadius, _cameraMode != CameraMode.Cockpit);
                    // P367 — the local ship's navigation strobe lights: gathered
                    // only outside the cockpit (its hull isn't drawn there),
                    // matching the P358 trail / P365 glow gate. Unlike those
                    // thrust-driven effects, strobes are always blinking (driven
                    // purely by elapsed time), so the emitter is recorded whenever
                    // the hull is known and visible.
                    UpdateNavStrobes(me, ownPos, ownOri, _myRenderRadius, _cameraMode != CameraMode.Cockpit);
                    // P363 — the local ship's side-coloured motion trail: fed
                    // every frame its anchor/right/side-colour, but like the
                    // engine trail only outside the cockpit (the hull isn't
                    // drawn in the internal cockpit view), matching P358/P297.
                    UpdateMotionTrail(me, ownPos, ownOri, _myRenderRadius, mine.SideId,
                        _cameraMode != CameraMode.Cockpit);
                    // P721 — resolve the local hull class (fighter vs capital) and
                    // the sidethrust gate (native PlaySidethrust() = backward|left|
                    // right buttons held, trekigc.cpp:953-956) for the per-hull loop
                    // beds and the new sidethrust loop (SIM-0372..0383).
                    bool capitalHull = _hullById.TryGetValue(mine.HullId, out var ownHull)
                        && !ownHull.HasCapability(DataHullTypeIGC.HabmFighter);
                    bool sidethrustActive = (mineUp.StateM & (
                        Allegiance.Core.Sim.ShipControlState.BackwardButton
                        | Allegiance.Core.Sim.ShipControlState.LeftButton
                        | Allegiance.Core.Sim.ShipControlState.RightButton)) != 0;
                    _sound?.Update(
                        throttle: mineUp.Controls.Throttle,
                        speed: mineUp.Velocity.Length(),
                        cockpitMode: _cameraMode == CameraMode.Cockpit,
                        afterburnerActive: (mineUp.StateM & Allegiance.Core.Sim.ShipControlState.AfterburnerButton) != 0,
                        deltaSeconds: (float)gameTime.ElapsedGameTime.TotalSeconds,
                        missileLocked: world.MissileLock.IsFullLock,   // P230 — lock beep at full lock
                        capitalHull: capitalHull,
                        sidethrustActive: sidethrustActive);

                    // P267/P756 — the hull's maneuvering-thruster (turn) loop,
                    // driven from the local ship's real commanded turn axes (the
                    // same ControlData the engine mix reads). Native keeps
                    // separate interior/exterior instances and mutes the wrong
                    // bus by camera view; the port plays the interior twin only
                    // in cockpit view and the exterior twin only in chase/
                    // exterior view, each resolved from the hull type's SoundID.
                    bool cockpitView = _cameraMode == CameraMode.Cockpit;
                    bool exteriorView = !cockpitView;
                    float turnLevel = Audio.TurnThrusterSound.LevelFor(
                        mineUp.Controls.Yaw, mineUp.Controls.Pitch, mineUp.Controls.Roll);
                    _sound?.UpdateTurnThruster(
                        turnLevel,
                        capitalHull: capitalHull,
                        cockpitView: cockpitView,
                        interiorSoundId: ownHull.ManuveringThrusterInteriorSound);
                    _sound?.UpdateTurnThrusterExterior(
                        turnLevel,
                        exteriorView: exteriorView,
                        capitalHull: capitalHull,
                        exteriorSoundId: ownHull.ManuveringThrusterExteriorSound);
                    _sound?.UpdateShipExterior(exteriorView);
                }
            }
            foreach (var (id, ship) in world.Ships)
            {
                // P344/P370 — exclude dead AND out-of-view ships from the render
                // pose gather exactly like CollectTargetCandidates/:5565 already
                // do for targeting. A dead, docked or warped-to-another-sector
                // ship keeps its last non-null Latest, so without this it sailed
                // through and was drawn frozen forever (the "ghost ship"). The
                // top-level guard also covers the damage-emitter gather,
                // cloak-visual tracking and model/texture/scale resolution that
                // share this loop body.
                if (id == world.MyShipId || viewSector is not { } sector
                    || !ship.CanDrawInSector(sector) || ship.Latest is not { } up) continue;
                // P448b — resolve this ship's RENDER pose: dead-reckon it to "now"
                // (P448a ShipDeadReckoner, native CalculateShip replay) unless the
                // kill switch is off or the reckoner isn't seeded (falls back to the
                // raw last-known pose). Own-ship posing stays raw (P447); ships with
                // no pose were already excluded above.
                Flight.PredictedPose pose = RemoteShipPose(world, id, up, ship);
                _poses[id] = pose;
                // P803 — a boarded gunner rides its carrier's smoothed render pose.
                if (world.MyShipId is { } meRide && world.Ships.TryGetValue(meRide, out var rider) && rider.ParentId == id)
                    _poses[meRide] = pose;
                // P297 — any other visible damaged ship vents too (native's
                // per-ship AddDamageParticles scope — not just the local hull).
                // P338 — pass the ship's real rendered radius (0.5 × HullType.Length)
                // so the vent offsets scale with the actual hull, not the old
                // fixed 50-unit stand-in.
                float tl = HullLength(ship.HullId);
                float radius = tl > 0f ? 0.5f * tl : 0f;
                // P781 — lead-up sparks for every ripcording/closing remote ship
                // whose hull is drawn (the CanDrawInSector guard above IS the
                // model pass's hull-draw gate). The ripcord timer comes from the
                // per-ship client state (see RemoteShipRipcording); the aleph
                // timer is native's per-frame nearest-warp ETA (WinTrek.cpp:7783-7802).
                float ripT = world.TryGetRemoteRipcord(id, out long ripStart)
                    ? Allegiance.ClientCore.Flight.RipcordLeadup.TimeUntilTeleport(ripStart, ClientNowMilliseconds,
                        RemoteRipcordDuration(world, ship), world.TimeScale)
                    : -1f;
                GatherTeleportEmitter(world, id, pose.Position, up.Velocity, pose.Orientation,
                    radius > 0f ? radius : Rendering.WarpTransitParticles.ShipRadiusDefault,
                    ripT, viewSector.Value, updateDt);
                // P628 — capture the per-ship LOD inputs under the Gate so the
                // Draw loop reads them lock-free: the rendered radius (the
                // ProjectedPixelRadius metric) and the real side colour (the
                // far-tier sprite tint; native pside->GetColor(),
                // radarimage.cpp:682-685).
                _shipRadiusById[id] = radius;
                if (world.TryGetSideColor(ship.SideId, out float sr, out float sg, out float sb))
                    _shipSideRgbById[id] = (sr, sg, sb);
                // P448b — every effect attached to the hull (damage debris, engine
                // trail, nozzle glow, nav strobes, motion trail) is fed the SAME
                // RENDER pose so nothing detaches from the drawn hull. Wire-driven
                // inputs (velocity, hull fraction, stateM, throttle) stay wire values.
                GatherDamageEmitter(id, up.HullFraction, pose.Position, up.Velocity, pose.Orientation, radius);
                UpdateEngineTrail(id, (up.StateM & Allegiance.Core.Sim.ShipControlState.AfterburnerButton) != 0,
                    pose.Position, up.Velocity, pose.Orientation, radius, updateDt, visible: true);
                UpdateNozzleGlow(id, up.StateM, up.Controls.Throttle, pose.Position, pose.Orientation, radius, visible: true);
                UpdateNavStrobes(id, pose.Position, pose.Orientation, radius, visible: true);
                UpdateMotionTrail(id, pose.Position, pose.Orientation, radius, ship.SideId, visible: true);
                // P270 — remember which of these render poses is a cloaked ship
                // (the CloakActive stateM bit) so Draw alpha-blends it. The
                // server already excluded any undetectable enemy, so anything
                // here is a ship this client's side legitimately sees (a
                // teammate, or an enemy close enough to detect it).
                if (Flight.CloakVisual.IsCloaked(up.StateM))
                    _cloakedShips.Add(id);
                // P288 — resolve this ship's per-faction/class content model
                // under the same lock (civ from side roster, class from hull).
                _shipModelById[id] = ResolveShipModel(ship.SideId, ship.HullId);
                _shipTextureById[id] = ResolveShipTexture(ship.SideId, ship.HullId);   // P337
                if (_shipsNative)                                                        // P566
                    _nativeShipRefById[id] = ResolveNativeShip(ship.HullId);
                // P333 — per-ship PURE VISUAL scale: normalize the model that
                // will actually be drawn (resolved model or the fallback) to the
                // real hull size. 1 when the hull/model is unknown → no scaling.
                var drawnModel = _shipModelById[id] ?? _fallbackModel;
                _shipScaleById[id] = Rendering.ShipRenderScale.ScaleForLength(
                    HullLength(ship.HullId), drawnModel);
            }

            // P448b — periodically report the re-seed correction statistics so the
            // typical dead-reckoner jump at each update is observable live (item 4).
            // Logged at most once every ~2 s; the world accumulates and resets them
            // under this same gate, so the poll is atomic with the pose gather.
            if (_remoteShipDeadReckoning && _nowSeconds - _remoteCorrectionLoggedAt >= 2.0)
            {
                _remoteCorrectionLoggedAt = _nowSeconds;
                var (cnt, mean, mx) = world.TakeRemoteCorrectionStats();
                if (cnt > 0)
                    Console.WriteLine($"[P448b] dead-reckon re-seed corrections: n={cnt} mean={mean:0.000}m max={mx:0.000}m");
            }
        }

    }

    /// <summary>P781 — gather one drawn ship's lead-up spark emitter into the
    /// preallocated array for this frame's render pass: select the teleport
    /// timer (native thinggeo.cpp:620-625 — the ripcord timer, unless the aleph
    /// crossing is sooner), derive the frame velocity from the previous
    /// rendered pose (native vecFrameVelocity, thinggeo.cpp:506-512; falls back
    /// to the wire velocity on the first frame), and record nothing when the
    /// selected timer is negative (no sparks before the final 3 s). The pose
    /// history is maintained even when no sparks emit, so a lead-up that starts
    /// later already has a previous pose. Called under the world gate from the
    /// pose pass; no allocation (the nearest-warp walk iterates the world's
    /// dictionaries directly; <see cref="_lastSparkPose"/> is reused).
    /// </summary>
    private void GatherTeleportEmitter(ClientWorld world, short id,
        SimVec position, SimVec velocity, Orientation orientation, float radius,
        float ripcordTimeLeft, short viewSector, float frameDt)
    {
        // Frame velocity first (also keeps the history warm for future lead-ups).
        SimVec frameVelocity;
        if (frameDt > 1e-5f && _lastSparkPose.TryGetValue(id, out var prev))
            frameVelocity = (position - prev) / frameDt;
        else
            frameVelocity = velocity;   // first frame: the wire velocity stand-in
        _lastSparkPose[id] = position;

        // Native FindTarget(pship, c_ttWarp | c_ttNearest | c_ttAllSides)
        // (WinTrek.cpp:7792): the nearest aleph in the ship's cluster by
        // centre distance. The view sector's aleph centres are cached (static
        // map data) so this walk never enumerates the world's
        // IReadOnlyDictionary (a boxed enumerator per ship per frame).
        RefreshViewWarpCache(world, viewSector);
        float alephEta = -1f;
        SimVec bestWarp = default;
        float bestDsq = float.MaxValue;
        for (int i = 0; i < _viewWarpCentres.Count; i++)
        {
            var wp = _viewWarpCentres[i];
            float dx = wp.X - position.X, dy = wp.Y - position.Y, dz = wp.Z - position.Z;
            float dsq = dx * dx + dy * dy + dz * dz;
            if (dsq < bestDsq) { bestDsq = dsq; bestWarp = wp; }
        }
        if (bestDsq != float.MaxValue)
            alephEta = Allegiance.ClientCore.Flight.RipcordLeadup.AlephEta(position, velocity, bestWarp);

        float t = Allegiance.ClientCore.Flight.RipcordLeadup.SelectTeleportTime(ripcordTimeLeft, alephEta);
        if (t < 0f) return;   // thinggeo.cpp:625 — nothing happens before the final 3 s
        if (_teleportEmitterCount >= _teleportEmitters.Length) return;   // 64 emitters is far above any drawn-ship count

        _teleportEmitters[_teleportEmitterCount++] = new Rendering.TeleportEmitter(
            id, position, frameVelocity,
            orientation.GetForward(), orientation.GetRight(), radius, t);
    }

    /// <summary>P781 — (re)build the view sector's aleph-centre cache when the
    /// view sector or the world's warp count changes (alephs are static map
    /// data; a count change covers a late cluster-info load). Allocation only
    /// on those edges, never per frame.</summary>
    private void RefreshViewWarpCache(ClientWorld world, short viewSector)
    {
        int count = world.Warps.Count;
        if (viewSector == _viewWarpSector && count == _viewWarpWorldCount) return;
        _viewWarpSector = viewSector;
        _viewWarpWorldCount = count;
        _viewWarpCentres.Clear();
        foreach (var kv in world.Warps)
            if (kv.Value.ClusterId == viewSector)
                _viewWarpCentres.Add(new SimVec(kv.Value.Position[0], kv.Value.Position[1], kv.Value.Position[2]));
    }

    private Flight.PredictedPose RemoteShipPose(ClientWorld world, short id, ServerShipUpdate up, ShipEntity ship)
    {
        // P781 — a remote ship with live ripcord state dead-reckons with the
        // native frozen flight model (pinned turn rates, drag-only): native sets
        // the client-side ripcord model for ANY ripcording ship
        // (appmsg.cpp:950-962) and runs the frozen ExecuteShipMove on it
        // (shipIGC.cpp:2885-3037). Duration comes from the per-(hull, side)
        // cache; the per-ship client state is cleared by ABORTED / KILL_SHIP /
        // SHIP_DELETE / reset and expires through RipcordLeadup's safety grace.
        bool ripcording = RemoteShipRipcording(world, id, ship);
        if (_remoteShipDeadReckoning &&
            world.TryGetRenderPose(id, ClientNowMilliseconds / 1000.0, out var pos, out var ori, ripcording))
        {
            return new Flight.PredictedPose(pos, ori.Copy());
        }
        return new Flight.PredictedPose(up.Position, up.Orientation);
    }

    /// <summary>P781 — whether a remote ship is currently inside a ripcord
    /// countdown (per-ship client state + the shared TiDi rule). The wire's
    /// <c>bIsRipcording</c> is deliberately NOT read: the server always sends
    /// false today (Server.Broadcast.cs:1022,1256,1401) and honouring it would
    /// wipe live state on every teammate SSU (P781b fixes both ends).</summary>
    private bool RemoteShipRipcording(ClientWorld world, short id, ShipEntity ship)
    {
        if (!world.TryGetRemoteRipcord(id, out long start)) return false;
        return Allegiance.ClientCore.Flight.RipcordLeadup.IsRipcording(start, ClientNowMilliseconds,
            RemoteRipcordDuration(world, ship), world.TimeScale);
    }

    /// <summary>P781 — a ship's ripcord countdown duration, exactly
    /// <see cref="RipcordDurationSecondsFor"/> (hull <c>RipcordSpeed</c> /
    /// side <c>c_gaRipcordTime</c>, shipIGC.cpp:4665), but with the hull's
    /// <c>RipcordSpeed</c> cached per hull id so the per-frame pose pass never
    /// walks the shop catalogue per ship (a 50-ripper scene would re-scan it
    /// 50x per frame). The side GA is read live every call (it can change with
    /// research mid-view). Only resolved hulls are cached, so a not-yet-loaded
    /// catalogue retries; cleared on BeginView.</summary>
    private float RemoteRipcordDuration(ClientWorld world, ShipEntity ship)
    {
        if (!_ripcordSpeedByHull.TryGetValue(ship.HullId, out float ripcordSpeed))
        {
            if (_shopCatalog is not { } catalog) return 0f;
            ripcordSpeed = 0f;
            foreach (var h in catalog.Hulls)
                if (h.HullId == ship.HullId) { ripcordSpeed = h.RipcordSpeed; break; }
            if (ripcordSpeed <= 0f) return 0f;
            _ripcordSpeedByHull[ship.HullId] = ripcordSpeed;
        }
        float ga = world.GlobalAttributesFor(ship.SideId)
            .Get(Allegiance.Core.Model.GlobalAttributeSet.RipcordTime);
        return ga > 0f ? ripcordSpeed / ga : ripcordSpeed;
    }

    /// <summary>
    /// P366 — resolve the leader-only bot dock/launch command for the current
    /// frame, or null when it should NOT be offered. Delegates the pure decision
    /// to <see cref="LeaderBotCommandResolver"/> (testable); all the gating
    /// mirrors native (WinTrek.cpp:4349-4368) — leader-only, selected target is
    /// a same-side bot — so a non-leader sees nothing at all, not a disabled
    /// button.</summary>
    private LeaderBotCommand? ResolveLeaderBotCommand(ClientWorld world)
    {
        short? selId = _autoTarget is { Kind: TargetKind.Ship } t ? t.Id : null;
        return LeaderBotCommandResolver.Resolve(world, selId);
    }

    private int _autopilotMovementButtons;
    private float _autopilotDeltaSeconds = .033f;

    /// <summary>Compute the autopilot's per-frame steering toward the selected
    /// target (re-read every frame, so a moving target is followed), or return
    /// false if there is no resolvable target / own pose. Also disengages once
    /// the ship reaches <see cref="AutopilotSteering.AutopilotArrivalRadius"/> of
    /// a STATIONARY target — SteerToward has no terminal state of its own (it
    /// just brakes hard and keeps re-facing), which is fine for a moving target
    /// but for a parked one the ship's own coast/overshoot made the direction
    /// vector flip rapidly at point-blank range, spinning it in a tight endless
    /// orbit around the target instead of ever settling — live-observed
    /// flying at a frozen vizfight roster bot. A moving target has no such
    /// terminal state (you'd never "arrive" at a ship still under way), so the
    /// check only fires when the target hasn't moved since the autopilot was
    /// engaged.</summary>
    private bool TryGetAutopilotControls(out ControlData controls)
    {
        _autopilotMovementButtons = 0;
        controls = ControlData.Neutral;
        if (_autoTarget is not { } tgt) return false;

        // P541 — a cluster "goto" is complete the moment the own ship is IN the
        // target sector (native deletes the c_buoyCluster buoy "as soon as we are
        // in the cluster", FedSrv.CPP:3302). Disengage here — there is no in-
        // sector position to reach (the waypoint resolver already reports arrival
        // by returning false, but we make it an explicit, named disengage so the
        // autopilot HUD/sound settle exactly once).
        if (tgt.Kind == TargetKind.Cluster && IsInTargetCluster(tgt.Id))
        {
            DisengageAutopilot(AutopilotDisengageReason.Arrived);
            return false;
        }

        if (!TryOwnPose(out var pos, out var orient, out var vel)) return false;

        // P540b — cross-sector steering decision. The target's SECTOR is resolved
        // separately from its raw position: a station/probe/ship in another
        // sector must be approached THROUGH WARPS (steer at the next warp mouth
        // toward its sector), never by flying at its foreign-sector coordinates
        // as if local — the P540 eject bug. For a same-sector (or unknown)
        // target we resolve its real local position and use the pre-P540b law.
        short ownSector = OwnSectorIdOr(-1);
        short? targetSector = ResolveTargetSector(tgt);
        SimVec? targetLocal = null;
        if (targetSector is null || targetSector.Value == ownSector)
        {
            if (!ResolveTargetPosition(tgt, out var tp)) return false;
            targetLocal = tp;
        }

        List<Flight.StarmapGotoRouter.AllWarp>? warps = null;
        if (targetSector is { } ts && ts != ownSector)
        {
            lock (_session.Gate)
                warps = AllWarps(_session.World);
        }
        IReadOnlyList<Flight.StarmapGotoRouter.AllWarp> warpSource =
            warps is { } wl ? wl : Array.Empty<Flight.StarmapGotoRouter.AllWarp>();
        if (!TryResolveAutopilotGoal(ownSector, targetSector, targetLocal,
                                     warpSource,
                                     out var goal, out var routed))
            return false;

        // P575 — a same-sector DOCKABLE station target uses the shared native
        // bay approach (DockApproach) instead of steering at the station centre
        // (which grinds on the outer ring when the line misses a bay). It stays
        // engaged until the server actually docks the ship — the 60 u "arrived"
        // disengage below is intentionally skipped for it (native has no
        // arrival-radius terminal for a station approach; it keeps entering and
        // the server docks it, common.cpp:1505-1545 + SetControls). A non-
        // dockable target (no bay of the hull's class, wrong/unfriendly side)
        // falls through to the unchanged centre/goto — the native no-bay "power
        // glide in".
        if (!routed && tgt.Kind == TargetKind.Station &&
            TryComputeDockSteer(tgt, pos, orient, vel, out var dockControls))
        {
            // Once the ship is actually docked (server's real docked latch,
            // ShipEntity.StationId != -1), the approach is complete: settle the
            // autopilot exactly once, like every other terminal.
            if (IsOwnShipDocked())
            {
                DisengageAutopilot(AutopilotDisengageReason.Docked);
                return false;
            }
            controls = dockControls;
            return true;
        }

        // P594 — native "mmf pod pickup" goto (common.cpp:1430-1446): when the
        // autopilot's same-sector SHIP target is a friendly lifepod, OR the own
        // ship IS a lifepod converging on a same-sector ship (a rescuer),
        // steer at the INTERCEPT point at FULL SPEED (c_gpmFast), skip the pod
        // as a dodge obstacle (pmodelSkip = the pod — the target is already the
        // dodge skip via SkipGoalFor), and NEVER apply the 60 u "arrived"
        // disengage (native has no arrival-radius terminal for a pod pickup; it
        // keeps flying INTO the pod). The engagement ends only when the server
        // PLAYER_RESCUED fires — the pod vanishes/docks (target pod) or the own
        // pod is docked (rescued) — at which point we disengage "rescued".
        if (!routed && tgt.Kind == TargetKind.Ship
            && ResolvePodPickup(tgt, out var podStillValid, out var podVel, out var ownMaxSpeed))
        {
            if (!podStillValid)
            {
                // Pod rescued (PLAYER_RESCUED → the pod is gone or docked) or
                // the own pod was rescued into a docked state: end the autopilot
                // engagement exactly once, like every other terminal. (Without
                // this the stale pod target would leave the autopilot hung, since
                // ResolveTargetPosition above returns false without disengaging.)
                DisengageAutopilot(AutopilotDisengageReason.Rescued);
                return false;
            }
            controls = AutopilotSteering.PodPickup(pos, orient, goal, podVel, ownMaxSpeed);
            return true;
        }

        // Same-sector arrival/disengage law (existing, unchanged for same-sector
        // targets). A routed cross-sector goal is a warp mouth — distance to the
        // raw target is meaningless until the ship reaches its sector, so the
        // arrival disengage is only evaluated for same-sector (non-routed) goals.
        if (!routed)
        {
            float distance = (goal - pos).Length();
            bool targetStationary = _autoTargetAtEngage is { } engagePos
                && (goal - engagePos).Length() < 1f;
            if (targetStationary && distance <= AutopilotSteering.AutopilotArrivalRadius)
            {
                DisengageAutopilot(AutopilotDisengageReason.Arrived);
                return false;
            }
        }

        // P343/P446 — size the braking-distance term with the ship's REAL max
        // deceleration (HullType.Thrust / HullType.Mass — the throttle-cruise
        // limit ExecuteMove actually achieves) when the shop catalogue resolves
        // the own hull; fall back to AutopilotBrakeDeceleration when it does
        // not (missing/empty catalogue — the server still boots fine, the
        // autopilot just brakes a little earlier than optimal). P446 scales
        // the thrust by the LOCAL side's c_gaThrust factor so the braking
        // estimate tracks a GA-boosted engine exactly as the server sim does.
        // P524 — the same resolved hull + GA thrust also feeds the dodge
        // mover's acceleration (native Thrust/Mass, common.cpp:1707) so the
        // imminence gate matches the server's GA-scaled EffectiveThrust().
        float decel = AutopilotSteering.AutopilotBrakeDeceleration;
        ClientAutopilotDodge.MoverHull? ownMoverHull = null;
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is { } me && _session.World.Ships.TryGetValue(me, out var mine)
                && _shopCatalog is { } cat
                && cat.Hulls.FirstOrDefault(h => h.HullId == mine.HullId) is { Mass: > 0f, Thrust: > 0f } hull)
            {
                float gaThrust = 1.0f;   // identity when the local side is unknown
                if (_session.World.MySideId is { } mySide)
                    gaThrust = _session.World.GlobalAttributesFor(mySide).Get(GlobalAttributeSet.Thrust);
                decel = (hull.Thrust * gaThrust) / hull.Mass;
                ownMoverHull = new ClientAutopilotDodge.MoverHull(hull.Length, hull.Thrust * gaThrust, hull.Mass);
            }
        }

        // P524 — native GotoPlan::SetControls' "first priority ... dodge"
        // (common.cpp:2000-2018): when an imminent collision with a same-cluster
        // asteroid / station / known ship is found, steer to the shared dodge
        // law's goal instead of the obstacle-blind straight line — the previous
        // G behaviour (P489's named gap). The current target is the pmodelSkip,
        // so the autopilot never dodges its own destination (scope item 2: a
        // station you're flying to, an asteroid you're mining). No imminent
        // collision → the unchanged law flies straight and arrives exactly as
        // before.
        SimVec steerPos = goal;
        if (ownMoverHull is { } moverHull
            && TryGetAutopilotDodge(pos, vel, moverHull, SkipGoalFor(tgt), out var dodgeGoal))
            steerPos = dodgeGoal;

        // P540b — a ROUTED (cross-sector warp) goal must be flown THROUGH at cruise
        // speed, not braked to a stop at the mouth (else a small hull re-crosses the
        // aleph and oscillates): pursue the warp. A same-sector destination target
        // keeps the normal braking law and stops at it.
        if (routed)
            controls = AutopilotSteering.Pursue(pos, orient, steerPos);
        else
            controls = AutopilotSteering.SteerToward(pos, orient, steerPos, vel, brakeDeceleration: decel);
        return true;
    }

    /// <summary>P575 — per-station-type landing-bay geometry cache for the
    /// player dock autopilot (mirrors the server's <c>StationLandingBayCache</c>
    /// shape, resolved from the type's real <c>.cvh</c>). Keyed by
    /// <c>StationTypeId</c>; null = no bays.</summary>
    private readonly Dictionary<short, Allegiance.Core.Collision.HullLandingBayGeometry?> _dockBaysByStationType = new();

    /// <summary>P575 — cached station landing-bay geometry from the type's real
    /// <c>.cvh</c> under the artwork root via the SHARED reconstructor (the same
    /// source the server's dock gate consumes), keyed by <c>StationTypeId</c>.
    /// Any failure degrades to null → the autopilot falls through to the plain
    /// centre/goto (native no-bay power glide), never a crash — the client's
    /// established degrade-not-fail asset convention (MuzzleOffsetsFor).</summary>
    private Allegiance.Core.Collision.HullLandingBayGeometry? StationDockGeometry(DataStationTypeIGC type)
    {
        if (_dockBaysByStationType.TryGetValue(type.StationTypeId, out var cached)) return cached;
        Allegiance.Core.Collision.HullLandingBayGeometry? g = null;
        try
        {
            var rel = type.ModelName + ".cvh";
            if (_assets.Exists(rel))
            {
                var model = Allegiance.Core.Collision.CvhReader.Parse(Assets.AssetSources.ReadAllText(_assets, rel));
                var rec = Allegiance.Core.Collision.HullLandingBay.ReconstructStation(model, type.Radius);
                g = rec.LandSlots.Count == 0 ? null : rec;
            }
        }
        catch { g = null; }
        _dockBaysByStationType[type.StationTypeId] = g;
        return g;
    }

    /// <summary>P575 — is the own ship physically docked (the server's real
    /// docked latch, <c>ShipEntity.StationId != -1</c>)? The terminal for the
    /// dock-approach autopilot (it has no arrival-radius disengage).</summary>
    private bool IsOwnShipDocked()
    {
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is { } me && _session.World.Ships.TryGetValue(me, out var ent))
                return ent.StationId != -1;
        }
        return false;
    }

    /// <summary>
    /// P575 — compute the shared native bay-approach <see cref="ControlData"/>
    /// for a same-sector STATION autopilot target, mirroring the server NPC path
    /// (<c>Ship.TryDockApproach</c>): resolve the own hull's dockability
    /// (<c>SabmLand</c>/<c>SabmCapLand</c>), the station's real bay geometry, the
    /// best bay and entry phase via <see cref="DockApproach"/>, then steer.
    /// Returns <c>false</c> when the target is not a friendly same-side dockable
    /// station with bays → the caller uses the plain centre/goto (native no-bay
    /// "power glide in").
    /// </summary>
    private bool TryComputeDockSteer(AutopilotTarget tgt, in SimVec pos, in Orientation orient, in SimVec vel, out ControlData controls)
    {
        controls = ControlData.Neutral;
        if (_shopCatalog is not { } cat) return false;

        lock (_session.Gate)
        {
            if (_session.World.MyShipId is not { } me) return false;
            if (!_session.World.Ships.TryGetValue(me, out var mine)) return false;
            if (!_session.World.Stations.TryGetValue(tgt.Id, out var station)) return false;

            // Friendly (same-side) gate: never bay-approach an enemy/wrong-side
            // station — the server would refuse to dock and the ship would grind.
            if (_session.World.MySideId is { } mySide && station.SideId != mySide) return false;

            DataHullTypeIGC? ownHull = null;
            foreach (var h in cat.Hulls)
                if (h.HullId == mine.HullId) { ownHull = h; break; }
            if (ownHull is not { } hull) return false;

            if (cat.StationTypes is not { } stationTypes) return false;
            if (!stationTypes.TryGetValue(station.StationTypeId, out var stype)) return false;

            // Ability gate — same need the server's dock dispatch applies
            // (Sector.cs:3563-3566): fighters SabmLand, others SabmCapLand.
            short need = hull.HasCapability(DataHullTypeIGC.HabmFighter)
                ? DataStationTypeIGC.SabmLand
                : DataStationTypeIGC.SabmCapLand;
            if (!stype.HasCapability(need)) return false;

            var geom = StationDockGeometry(stype);
            if (geom is null || geom.LandSlots.Count == 0) return false;

            var so = new Orientation();
            if (station.Forward is { Length: 3 } f && station.Up is { Length: 3 } u)
                so.Set(Vec(f), Vec(u));
            SimVec center = Vec(station.Position);

            // World-rotated bays from the landing slots (capital prefix, then fighters)
            // (native centers[i] = GetLandPosition(i,0)*itsOrientation).
            var bays = new List<DockApproach.DockBay>(geom.LandSlots.Count);
            foreach (var slot in geom.LandSlots)
            {
                var p0 = slot.Planes[0];
                bays.Add(new DockApproach.DockBay(
                    p0.Position * so,
                    p0.Direction.Normalize() * so));
            }
            int nLand = DockApproach.StationBaysForClass(
                hull.HasCapability(DataHullTypeIGC.HabmFighter), bays, geom.CapLandSlots);

            float speed = vel.Length();
            var attributes = _session.World.GlobalAttributesFor(_session.World.MySideId ?? -1);
            float distanceRest = DockApproach.StoppingDistance(speed, hull.Thrust * attributes.Get(GlobalAttributeSet.Thrust),
                hull.Mass, hull.Speed * attributes.Get(GlobalAttributeSet.MaxSpeed), hull.BackMultiplier);
            SimVec positionRest = speed > .1f ? pos + vel * (distanceRest / speed) : pos;

            float shipRadius = 0.5f * hull.Length;
            float totalRadius = shipRadius + stype.Radius + 10f;

            // Native waits for angular motion to settle before entering
            // (Igc/common.cpp:1301). Prediction supplies the current local rates;
            // otherwise use the same rates carried by the latest server update.
            var rates = _ownShipPrediction && _ownSim.HaveHull
                ? _ownSim.TurnRates : mine.Latest?.TurnRates;
            var step = DockApproach.DockStation(
                pos, vel, orient, center, bays, nLand, distanceRest, positionRest,
                totalRadius, rates?[ControlData.AxisYaw] ?? 0f, rates?[ControlData.AxisPitch] ?? 0f, experimental: false,
                bayPreference: hull.HasCapability(DataHullTypeIGC.HabmLifepod) ? mine.ShipId : 0);

            // Match StepOwnShipSimulation: native angular control uses sim
            // seconds, including the server's time-dilation factor.
            float simDt = (_autopilotDeltaSeconds > 0 ? _autopilotDeltaSeconds : .033f)
                * (float)_session.World.TimeScale;
            controls = DockApproach.Steer(step, pos, orient, vel, distanceRest,
                dt: simDt, turnState: DockApproach.TurnState.FromHull(hull, attributes,
                    rates?[ControlData.AxisYaw] ?? 0f, rates?[ControlData.AxisPitch] ?? 0f));
            _autopilotMovementButtons = DockApproach.MovementButtons(step, pos, orient, vel,
                DockApproach.TurnState.FromHull(hull, attributes,
                    rates?[ControlData.AxisYaw] ?? 0f, rates?[ControlData.AxisPitch] ?? 0f));
            return true;
        }
    }

    /// <summary>P524 — the player autopilot's shared-dodge pre-empt: feed the
    /// SHARED native dodge law (<see cref="ClientAutopilotDodge"/>, the same
    /// canonical feed the P489 client bots use) from the client's own
    /// <c>ClientWorld</c> — same-cluster asteroids, stations and known
    /// same-side ships — skipping the current target, so an imminent collision
    /// steers the <c>G</c> autopilot around the obstacle exactly like the server
    /// NPC autopilot's <c>Ship.TryComputeDodgeGoal</c>.</summary>
    private bool TryGetAutopilotDodge(in SimVec pos, in SimVec vel,
        ClientAutopilotDodge.MoverHull moverHull, ClientAutopilotDodge.SkipGoal? skip, out SimVec goal)
    {
        goal = default;
        var world = _session.World;
        lock (_session.Gate)
        {
            if (world.MyShipId is not { } me) return false;
            short mySide = world.MySideId ?? -1;
            if (_shopCatalog is not { } cat) return false;
            return ClientAutopilotDodge.TryComputeDodgeGoal(
                world, me, mySide, pos, vel, moverHull,
                dodgeStations: true,
                id => cat.Hulls.FirstOrDefault(h => h.HullId == id) is { } hh ? hh.Length : (float?)null,
                tid => cat.StationTypes is { } stt && stt.TryGetValue(tid, out var st) && st.Radius > 0f
                    ? st.Radius : (float?)null,
                skip, out goal);
        }
    }

    /// <summary>P524 — map the current autopilot target to the shared
    /// <see cref="ClientAutopilotDodge.SkipGoal"/> (native <c>pmodelSkip</c>) so
    /// the autopilot doesn't dodge its own destination — the station it is
    /// flying to, the ship it is following, the asteroid it is mining. A warp
    /// target needs no skip (warps are not dodge obstacles).</summary>
    private static ClientAutopilotDodge.SkipGoal? SkipGoalFor(AutopilotTarget tgt)
        => tgt.Kind switch
        {
            TargetKind.Ship => new ClientAutopilotDodge.SkipGoal(ShipId: tgt.Id),
            TargetKind.Station => new ClientAutopilotDodge.SkipGoal(StationId: tgt.Id),
            TargetKind.Asteroid => new ClientAutopilotDodge.SkipGoal(AsteroidId: tgt.Id),
            _ => null,   // Warp — not a dodge obstacle
        };

    /// <summary>
    /// P594 — read the ship id of a queued pickup target. This only identifies
    /// the requested model. P675 validates legality in ClientWorld.Orders and
    /// handles Accept versus Execute independently in CommandActionInput.
    /// Native pickup branch: WinTrek.cpp:10666-10679.
    /// </summary>
    internal static short? PickupPodIdForQueued(QueuedCommandState? qc)
    {
        if (qc is { } q && q.CommandId == Allegiance.Core.Wire.OrderChange.CidPickup
            && q.TargetType == IgcObjectType.Ship)
            return q.TargetId;
        return null;
    }

    /// <summary>
    /// P594 — decide whether the current autopilot SHIP target is a native
    /// "pod pickup" scenario and, if so, supply the live parameters for
    /// <see cref="AutopilotSteering.PodPickup"/>. Native resolves the case by
    /// WAYPOINT kind (<c>Waypoint::GetGotoPosition</c>, case <c>OT_ship</c>,
    /// <c>common.cpp:1430-1446</c>) and fires when <b>either</b> the piloted
    /// ship or the target is a lifepod (<c>c_habmLifepod</c>): a rescuer diving
    /// on a friendly pod, or a pod converging on a rescuer. Returns <c>false</c>
    /// when neither is a pod (not a pickup — fall through to the ordinary law).
    /// Fills <paramref name="podVel"/> (the target's live world velocity,
    /// native <c>dv = m_pmodelTarget-&gt;GetVelocity()</c>),
    /// <paramref name="ownMaxSpeed"/> (the piloted ship's <c>GetMaxSpeed()</c>,
    /// native <c>common.cpp:1444</c>), and
    /// <paramref name="podStillValid"/> — whether the pod side of the
    /// engagement is still a live, present, undocked lifepod. A <c>false</c>
    /// validity means the pod was rescued (server <c>PLAYER_RESCUED</c> docks /
    /// removes it) or the own pod was rescued into a docked state, which is the
    /// native disengage signal for a pod pickup (the pod is gone).
    /// </summary>
    private bool ResolvePodPickup(AutopilotTarget tgt, out bool podStillValid,
        out SimVec podVel, out float ownMaxSpeed)
    {
        podVel = default; ownMaxSpeed = 0f; podStillValid = false;
        var world = _session.World;
        lock (_session.Gate)
        {
            bool targetIsPod = world.Ships.TryGetValue(tgt.Id, out var targetShip)
                && (ShipRoleBits(targetShip.HullId) & DataHullTypeIGC.HabmLifepod) != 0;
            bool ownIsPod = world.MyShipId is { } me
                && world.Ships.TryGetValue(me, out var ownShip)
                && (ShipRoleBits(ownShip.HullId) & DataHullTypeIGC.HabmLifepod) != 0;
            if (!targetIsPod && !ownIsPod) return false;   // not a pod pickup

            // Validity of the pod side of the engagement (native: disengage once
            // the pod is gone — PLAYER_RESCUED docks/removes it).
            if (targetIsPod)
                podStillValid = world.Ships.TryGetValue(tgt.Id, out var tp)
                    && tp.StationId < 0
                    && (ShipRoleBits(tp.HullId) & DataHullTypeIGC.HabmLifepod) != 0;
            else // ownIsPod → the own pod must still be adrift to keep converging on the rescuer
                podStillValid = world.MyShipId is { } me2
                    && world.Ships.TryGetValue(me2, out var op) && op.StationId < 0;

            // The TARGET's live velocity (the pod's when diving on it; the
            // rescuer's when the own pod is the pursuer).
            if (world.Ships.TryGetValue(tgt.Id, out var ts) && ts.Latest is { } tu)
                podVel = new SimVec(tu.Velocity.X, tu.Velocity.Y, tu.Velocity.Z);

            // Pursuer max speed = the PILOTED ship's GetMaxSpeed (native
            // common.cpp:1444, pship->GetHullType()->GetMaxSpeed()).
            if (world.MyShipId is { } ownId && world.Ships.TryGetValue(ownId, out var os)
                && _shopCatalog is { } cat)
            {
                foreach (var h in cat.Hulls)
                    if (h.HullId == os.HullId) { ownMaxSpeed = h.Speed; break; }
            }
            return true;
        }
    }

    /// <summary>Re-resolve a stored target's live world position. False when the
    /// object is gone, or the ship has died (you cannot steer at a corpse).</summary>
    private bool ResolveTargetPosition(AutopilotTarget tgt, out SimVec pos)
    {
        var world = _session.World;
        lock (_session.Gate)
        {
            switch (tgt.Kind)
            {
                case TargetKind.Ship:
                    // P523 — anchor to the pose the renderer drew THIS frame
                    // (_poses: own local sim for self, P448b dead-reckoned for
                    // remotes), not the raw 20 Hz ServerShipUpdate pose — else
                    // the bracket/label/bearing jitter around the smooth hull.
                    if (ResolveShipAnchor(_poses, world.Ships, tgt.Id, out pos))
                        return true;
                    break;
                case TargetKind.Station:
                    if (world.Stations.TryGetValue(tgt.Id, out var st))
                    {
                        pos = Vec(st.Position);
                        return true;
                    }
                    break;
                case TargetKind.Asteroid:
                    if (world.Asteroids.TryGetValue(tgt.Id, out var ast))
                    {
                        pos = Vec(ast.Position);
                        return true;
                    }
                    break;
                case TargetKind.Treasure:
                    if (world.Treasures.TryGetValue(tgt.Id, out var treasure))
                    {
                        pos = treasure.Position;
                        return true;
                    }
                    break;
                case TargetKind.Warp:
                    if (world.Warps.TryGetValue(tgt.Id, out var wp))
                    {
                        pos = Vec(wp.Position);
                        return true;
                    }
                    break;
                case TargetKind.Probe:
                    // P540 — a rescue probe can be the eject autopilot's target
                    // (native's c_ttProbe in the eject FindTarget); probes never
                    // move, so the live world position is the single stored one.
                    if (world.Probes.TryGetValue(tgt.Id, out var pb))
                    {
                        pos = pb.Position;
                        return true;
                    }
                    break;
                case TargetKind.Cluster:
                    // P541 — a cluster "goto" target resolves to the live
                    // waypoint the client autopilot must steer toward right now:
                    // the connecting warp's mouth toward the next route hop (or
                    // arrival, handled by the caller before this is reached).
                    if (TryResolveClusterWaypoint(world, tgt.Id, out pos))
                        return true;
                    break;
            }
        }
        pos = default;
        return false;
    }

    /// <summary>
    /// P541 — resolve the client autopilot's current steering waypoint for a
    /// cross-cluster "goto" to <paramref name="targetCluster"/>: the position of
    /// the warp in the ship's CURRENT sector that leads toward the target, found
    /// by routing over the REAL warp table (<c>world.Warps</c>) via the pure
    /// <see cref="Flight.StarmapGotoRouter"/> (BFS over actual warp connectivity —
    /// so the next hop always has a real warp). Returns false when already in the
    /// target sector (arrival — the caller disengages) or when unreachable.
    /// Re-resolved every frame so the waypoint advances as the ship transits.
    /// </summary>
    private bool TryResolveClusterWaypoint(ClientWorld world, short targetCluster, out SimVec pos)
    {
        pos = default;
        if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)) return false;
        short cur = own.SectorId;
        if (cur == targetCluster) return false;              // arrived

        if (_shopCatalog?.Hulls.FirstOrDefault(h => h.HullId == own.HullId)
            is { } podHull && podHull.HasCapability(DataHullTypeIGC.HabmLifepod) && own.Latest is { } pose)
        {
            SimVec? destination = _autoTarget is { Kind: TargetKind.Station } target
                && world.Stations.TryGetValue(target.Id, out var station) ? Vec(station.Position) : null;
            if (PodReturnNavigation.NextWarp(world.Warps, cur, targetCluster, pose.Position, destination) is not { } mouth)
                return false;
            pos = Vec(mouth.Position);
            return true;
        }

        var warps = AllWarps(world);

        if (!Flight.StarmapGotoRouter.TryNextWaypointFromWarps(cur, targetCluster, warps, out var wp))
            return false;
        pos = new SimVec(wp.X, wp.Y, wp.Z);
        return true;
    }

    /// <summary>P541 — is the own ship currently in <paramref name="clusterId"/>?
    /// The arrival predicate for the cluster-goto autopilot (the own ship is
    /// deemed "at" a cluster once it is physically in its sector).</summary>
    private bool IsInTargetCluster(short clusterId)
    {
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is { } me && _session.World.Ships.TryGetValue(me, out var own))
                return own.SectorId == clusterId;
        }
        return false;
    }

    /// <summary>P540b — the own ship's current sector id, or
    /// <paramref name="fallback"/> when the own ship is unknown.</summary>
    private short OwnSectorIdOr(short fallback)
    {
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is { } me && _session.World.Ships.TryGetValue(me, out var own))
                return own.SectorId;
        }
        return fallback;
    }

    /// <summary>P540b — flatten the world's warp table into the router's
    /// ground-truth records (mouth position, source sector, DESTINATION SECTOR),
    /// resolving the destination from the warp's PARTNER. A warp's raw
    /// <c>DestinationId</c> is the id of its PARTNER warp (native
    /// <c>warpIGC.h:129-141</c>; <c>Mission.cs:519</c>), NOT the destination
    /// sector — routing by the raw id aimed at a phantom sector the server never
    /// transits to (the P540b oscillation). The partner warp's <c>ClusterId</c>
    /// is the destination sector, exactly the resolution the server's transit
    /// performs and the MCP bot's <c>TryBuildWarpDestMap</c> uses. The same shape
    /// P541's <see cref="TryResolveClusterWaypoint"/> used; shared so the cluster-
    /// goto and the cross-sector autopilot route over the SAME data.</summary>
    private static List<Flight.StarmapGotoRouter.AllWarp> AllWarps(ClientWorld world)
    {
        var warps = new List<Flight.StarmapGotoRouter.AllWarp>();
        foreach (var w in world.Warps.Values)
        {
            if (w.Position is not { Length: 3 }) continue;
            // P540c — destination sector from the PARTNER warp (never guess):
            // if the partner warp isn't known yet, skip this edge rather than
            // feed a warp id in as a sector id (which aimed at a phantom sector
            // the server never transits to). Native warpIGC.h:129-141.
            if (WarpDestinationResolver.DestinationSector(world.Warps, w.WarpId) is not { } destSector) continue;
            warps.Add(new Flight.StarmapGotoRouter.AllWarp(
                new System.Numerics.Vector3(w.Position[0], w.Position[1], w.Position[2]),
                w.ClusterId, destSector));
        }
        return warps;
    }

    /// <summary>P540b — the sector (cluster) an autopilot target lives in, per
    /// kind. Resolved separately from the target's RAW position because the
    /// autopilot must never fly at another sector's coordinates as if local
    /// (the P540 eject bug): callers compare this against the own ship's sector
    /// to decide whether to route through warps.</summary>
    private short? ResolveTargetSector(AutopilotTarget tgt)
    {
        var world = _session.World;
        lock (_session.Gate)
        {
            return tgt.Kind switch
            {
                TargetKind.Ship => world.Ships.TryGetValue(tgt.Id, out var sh) ? sh.SectorId : (short?)null,
                TargetKind.Station => world.Stations.TryGetValue(tgt.Id, out var st) ? st.ClusterId : null,
                TargetKind.Asteroid => world.Asteroids.TryGetValue(tgt.Id, out var ast) ? ast.ClusterId : null,
                TargetKind.Warp => world.Warps.TryGetValue(tgt.Id, out var wp) ? wp.ClusterId : null,
                TargetKind.Probe => world.Probes.TryGetValue(tgt.Id, out var pb) ? pb.ClusterId : null,
                TargetKind.Cluster => tgt.Id,
                TargetKind.Treasure => world.Treasures.TryGetValue(tgt.Id, out var treasure) ? treasure.ClusterId : null,
                _ => null,
            };
        }
    }

    /// <summary>P416 — resolve turret seat <paramref name="seat"/> (1-based) on
    /// the flown ship's own hull to a wire mount id and send the promote. If the
    /// flown hull has no such turret slot (or the own hull is unknown to the shop
    /// catalogue), send nothing and surface a system-line hint instead of a
    /// meaningless wire message — mirroring native's guard `turret &lt;
    /// pht-&gt;GetMaxWeapons()` (WinTrek.cpp:9593). The pure seat→mount mapping
    /// lives in <see cref="Flight.PromoteTurretKey"/>.</summary>
    private void TryPromoteTurretSeat(int seat)
    {
        short? mount = null;
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is { } me &&
                _session.World.Ships.TryGetValue(me, out var own))
            {
                if (_shopCatalog is { } cat)
                    foreach (var h in cat.Hulls)
                        if (h.HullId == own.HullId)
                        {
                            mount = Flight.PromoteTurretKey.MountForSeat(seat, h.MaxFixedWeapons, h.MaxWeapons);
                            break;
                        }
            }
        }

        if (mount is { } m)
        {
            _session.SendPromote(m);
            Console.WriteLine($"[monogame] promote turret seat {seat} -> mount {m} (PROMOTE 190)");
        }
        else
        {
            lock (_session.Gate)
                _session.World.PostSystemLine($"Promote seat {seat}: your ship has no such turret mount.");
            Console.WriteLine($"[monogame] promote seat {seat}: no turret slot on own hull (or hull unknown) — no wire message");
        }
    }

    /// <summary>
    /// P462 — emit the local own-shot echo for the current frame and play one
    /// fire one-shot per round emitted. Called every flight frame (from the same
    /// gate-locked pose pass that sets <c>_poses[me]</c>), but does nothing when
    /// no fire bits were actually sent this frame (trigger up, or the autopilot
    /// driving with neutral fire), the loadout/catalogue is unresolved, or the
    /// energy/ammo gate blocks. <paramref name="pose"/> is the renderer's
    /// own-ship pose (<c>_poses[me]</c> — the P447 prediction seam); velocity and
    /// the energy/ammo fractions come from the latest server-reported update.
    /// </summary>
    private void EmitOwnShots(ShipEntity mine, ServerShipUpdate mineUp, Flight.PredictedPose pose, int fireStateM)
    {
        EnsureOwnEchoHook();
        if (fireStateM == 0)
        {
            // Fire released (or autopilot driving neutral) → drop every burst loop
            // so a held-trigger loop doesn't linger after the trigger lifts.
            if (_burstingMounts.Count > 0 || _prevFiringMounts.Count > 0)
            {
                _sound?.StopAllWeaponBursts();
                _burstingMounts.Clear();
                _prevFiringMounts.Clear();
            }
            return;
        }

        var cat = _shopCatalog;
        if (cat is null || mine.Loadout is not { Count: > 0 } lo) return;

        // P465c — the local ship's real hull record (ModelName/Length/Hardpoints)
        // from the shop catalogue, so its per-mount weapon-muzzle offsets can be
        // resolved from the real .cvh. Missing hull → no offsets (centre tracer).
        DataHullTypeIGC myHull = default;
        foreach (var h in cat.Hulls)
            if (h.HullId == mine.HullId) { myHull = h; break; }
        var muzzles = myHull.Hardpoints.Length > 0 ? MuzzleOffsetsFor(myHull) : null;

        // Effective max energy = hull.MaxEnergy × the side's c_gaMaxEnergy
        // factor — the same GA-scaled ceiling the server uses
        // (Ship.EffectiveMaxEnergy, Ship.cs:739). Used only to translate the
        // energy-per-shot cost into the 0..1 wire-fraction floor for the gate.
        float effMaxEnergy = 0f;
        foreach (var h in cat.Hulls)
            if (h.HullId == mine.HullId && h.MaxEnergy > 0f)
            {
                float ga = 1.0f;
                if (_session.World.MySideId is { } mySide)
                    ga = _session.World.GlobalAttributesFor(mySide).Get(GlobalAttributeSet.MaxEnergy);
                effMaxEnergy = h.MaxEnergy * ga;
                break;
            }

        // Resolve each mounted weapon against the loaded core → the minimal
        // spec the echo needs (cadence, energy/ammo cost, projectile physics).
        var specs = new List<Flight.OwnShotWeapon>(lo.Count);
        foreach (var p in lo)
        {
            DataPartTypeIGC part = default;
            bool found = false;
            foreach (var pt in cat.Parts) if (pt.PartId == p.PartId) { part = pt; found = true; break; }
            if (!found || part.Type is not DataPartTypeIGC.Weapon w) continue;
            if (cat.ProjectileTypes is null || !cat.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var ptp)) continue;
            float minEnergyFrac = (w.EnergyPerShot > 0f && effMaxEnergy > 0f) ? w.EnergyPerShot / effMaxEnergy : 0f;
            specs.Add(new Flight.OwnShotWeapon(
                p.MountId, w.ProjectileTypeId, w.DtimeBurst, w.EnergyPerShot,
                w.CAmmoPerShot, w.Dispersion, ptp.Speed, ptp.Lifespan, ptp.AbsoluteF, minEnergyFrac,
                w.SingleShotSound, w.BurstSound,
                muzzles is not null && (uint)p.MountId < (uint)muzzles.Length ? muzzles[p.MountId] : default));
        }
        if (specs.Count == 0) return;

        var ctx = new Flight.OwnShotContext(
            pose.Position, pose.Orientation, mineUp.Velocity,
            mineUp.EnergyFraction, mineUp.AmmoFraction,
            _nowSeconds, ClientNowMilliseconds);

        var rounds = _ownShotEcho.Update(specs, new Flight.OwnShotFireState(fireStateM), ctx);

        // P465 — per-weapon sound for the emitted rounds. Native plays a single-shot
        // when a weapon fires and is not burst-firing, and a looping burst sound
        // while it sustains fire (trekigc.cpp:999-1012). Mirror that per mount:
        // a mount that fires on two consecutive frames is "bursting" (held
        // trigger) → a looping burst wave plays and no single-shot; a fresh tap
        // (fires after a gap) stays a single-shot of that weapon's own wave.
        if (_sound is { } snd)
        {
            var firing = new HashSet<int>();
            foreach (var r in rounds) firing.Add(r.MountId);

            var nowBursting = new HashSet<int>();
            foreach (var m in firing)
                if (_burstingMounts.Contains(m) || _prevFiringMounts.Contains(m))
                    nowBursting.Add(m);

            // single-shot for mounts firing this frame but not yet bursting
            foreach (var r in rounds)
            {
                if (nowBursting.Contains(r.MountId)) continue;
                snd.PlayWeaponFire(r.SingleShotSoundId);
                Console.WriteLine($"[monogame] audio: weapon fire (P465 single-shot) mount={r.MountId} stem={Audio.WeaponSoundMap.SingleShotStem(r.SingleShotSoundId)}");
            }
            // start/continue the bursting mounts' loops (StartWeaponBurst is idempotent)
            foreach (var r in rounds)
                if (nowBursting.Contains(r.MountId))
                    snd.StartWeaponBurst(r.MountId, r.BurstSoundId);

            // stop loops for mounts that stopped firing
            foreach (var m in _burstingMounts.ToList())
                if (!firing.Contains(m)) { snd.StopWeaponBurst(m); _burstingMounts.Remove(m); }

            _prevFiringMounts.Clear();
            _prevFiringMounts.UnionWith(firing);
            foreach (var m in nowBursting) _burstingMounts.Add(m);
        }
    }

    /// <summary>
    /// P465c — the per-mount weapon-muzzle (hardpoint) offsets for a hull, in
    /// the ship's local frame, cached once per hull id. Resolved from the hull's
    /// real <c>.cvh</c> under the artwork root via the SHARED
    /// <see cref="Allegiance.Core.Collision.HullWeaponPositions.Compute"/> (the
    /// exact same code the server's <c>WeaponMuzzleCache</c> uses), so the P462
    /// local echo leaves the same guns the server's real rounds leave — the two
    /// paths cannot drift. Any failure (missing file, malformed cvh, absent hull)
    /// degrades to all-zero offsets (the historic ship-centre tracer), never a
    /// crash, mirroring the client's degrade-not-fail asset convention.
    /// </summary>
    private Allegiance.Core.Collision.Vector3[] MuzzleOffsetsFor(DataHullTypeIGC hull)
    {
        if (_muzzleOffsetsById.TryGetValue(hull.HullId, out var cached)) return cached;

        Allegiance.Core.Collision.Vector3[] offsets;
        try
        {
            var rel = hull.ModelName + ".cvh";
            var model = _assets.Exists(rel)
                ? Allegiance.Core.Collision.CvhReader.Parse(Assets.AssetSources.ReadAllText(_assets, rel))
                : null;
            offsets = Allegiance.Core.Collision.HullWeaponPositions.Compute(hull, model);
        }
        catch
        {
            offsets = new Allegiance.Core.Collision.Vector3[hull.Hardpoints.Length];
        }
        _muzzleOffsetsById[hull.HullId] = offsets;
        return offsets;
    }

    /// <summary>
    /// P462 — ensure the client's own-shot echo is installed as the world's
    /// <c>PROJECTILE_INSTANCE</c> de-dup predicate (idempotent; set once so each
    /// of our server echoes is dropped rather than re-rendered/re-sounded).
    /// </summary>
    private void EnsureOwnEchoHook()
    {
        if (_session.World.OwnEchoDropPredicate is null)
            _session.World.OwnEchoDropPredicate = _ownShotEcho.ShouldDropServerEcho;
    }

    /// <summary>Native-autopilot sound transition classification.
    /// Only <see cref="ToggleOff"/> and <see cref="ManualInput"/> play
    /// <c>salAutopilotDisengageSound</c> in native (WinTrek/WinTrek.cpp:8315,
    /// :10365). The other reasons are silent transitions:
    /// target lost/arrived/docked/rescued/spectating/target nothing
    /// (clintlib/appmsg.cpp:2045 boards as passenger silently; TK_TargetNothing
    /// is SetTarget(NULL, c_cidNone) at WinTrek/WinTrek.cpp:10446-10449 and
    /// plays no autopilot sound). The commented-out order-accept disengage at
    /// WinTrek/WinTrek.cpp:8877-8898 (sound at :8895) must also stay silent.</summary>
    internal enum AutopilotDisengageReason
    {
        ToggleOff,
        ManualInput,
        TargetLost,
        Arrived,
        Docked,
        Rescued,
        Spectating,
        TargetNothing
    }

    /// <summary>P756/SIM-0276 — the rule for whether an autopilot disengage transition
    /// should produce the <c>salAutopilotDisengageSound</c> cue. Native plays it
    /// only when the autopilot was engaged and the transition is a manual flight
    /// input (WinTrek/WinTrek.cpp:8315) or the autopilot toggle key
    /// (WinTrek/WinTrek.cpp:10365). All other off transitions are silent.
    /// Exposed internally so the MonoGame test suite can pin the rule without
    /// instantiating the full <see cref="Game1"/>.</summary>
    internal static bool ShouldPlayAutopilotDisengageCue(bool wasEngaged, AutopilotDisengageReason reason)
        => wasEngaged && reason is AutopilotDisengageReason.ToggleOff or AutopilotDisengageReason.ManualInput;

    /// <summary>Clear the autopilot (engaged flag + stored target) with a
    /// console note. Called by every autopilot-off transition (see
    /// <see cref="AutopilotDisengageReason"/>); only ToggleOff and ManualInput
    /// play the cue (WinTrek.cpp:8315,10365).</summary>
    private void DisengageAutopilot(AutopilotDisengageReason reason)
    {
        if (!_autoPilotEngaged && _autoTarget is null) return;
        bool wasEngaged = _autoPilotEngaged;
        _autoPilotEngaged = false;
        _autoTarget = null;
        _autoTargetAtEngage = null;
        Console.WriteLine($"[monogame] autopilot OFF ({reason})");
        if (ShouldPlayAutopilotDisengageCue(wasEngaged, reason)) _sound?.PlayAutopilotDisengage();
        RefreshWindowTitle();
    }

    // Native CheckCountdownSound (WinTrek.cpp:11137-11159) runs while the
    // briefing holds the player on the team screen, as well as in flight.
    private void UpdateMissionCountdownSound(Allegiance.ClientCore.State.ClientWorld world)
    {
        Audio.MissionCountdownCue cue;
        lock (_session.Gate) cue = _missionCountdownSound.Update(world);
        if (cue.Start)
        {
            _sound?.PlayCountdownStart();
            Console.WriteLine("[monogame] audio: countdown-start");
        }
        // P711 — coarse mission-clock milestone VO (SIM-0300..0303, vo_time_*),
        // announced at 10/5/1 min and 30 s before the per-second tail.
        if (cue.Milestone is { } milestone)
        {
            _sound?.PlayCountdownMilestone(milestone);
            Console.WriteLine($"[monogame] audio: countdown milestone {milestone}");
        }
        if (cue.Second is { } second)
        {
            _sound?.PlayCountdownSecond(second);
            Console.WriteLine($"[monogame] audio: countdown tick {second}");
        }
    }

}
