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
    private void HandleTargetCycleKey()
    {
        if (TryOwnPose(out var oPos, out var oOrient, out _))
        {
            var picked = CycleTarget(oPos, oOrient);
            _autoTarget = picked;
            Console.WriteLine(picked is { } pt
                ? $"[monogame] target: {pt.Name} ({pt.Kind}#{pt.Id})"
                : "[monogame] no target in view");
        }
    }

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
            DisengageAutopilot("toggle-off");
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
        _nowSeconds += gameTime.ElapsedGameTime.TotalSeconds;
        // P618 — advance the sim-scaled game clock (wall × live server time
        // scale), clamped to [0,∞) so a scale=0 edge (never produced server-side;
        // MinTimeScale is 0.1) can't run effects backwards. Guarded so a not-yet-
        // connected session (no world) simply advances at full speed.
        double ts = 1.0;
        if (_session.World is { } w) ts = w.TimeScale;
        _gameSeconds += gameTime.ElapsedGameTime.TotalSeconds * Math.Clamp(ts, 0.0, 1.0);
        // P573 — frame-time over-budget fallback for the Volumetric tier (drop a
        // tier once, logged). Skipped during --auto proof runs so a recording
        // renders the requested tier consistently end to end.
        if (_autoPlan is null)
            EnsureVolumetricBudget(gameTime.ElapsedGameTime.TotalSeconds);
        // P508 — keep the own-ship server clock fed and pinging every frame (regardless
        // of lobby/flying state), so a server-converted SHIP_UPDATE time is ready by
        // undock. Cheap: only drains received echoes + sends a ~1 Hz ping.
        PumpServerClock();
        var ks = Keyboard.GetState();

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
        bool back = ks.IsKeyDown(XnaKeys.Back);
        bool goBackEdge = back && !_backKeyWasDown;
        if (goBackEdge
            && _state == ClientState.Flying
            && _session.World.GameOverResult is { }
            && !_chatActive && !_qcMenuOpen)
        {
            HandleGameOverBack();
        }
        _backKeyWasDown = back;

        // Escape: while composing chat it cancels the input line, closes the
        // briefing popup, backs out of quick-chat, or clears the lobby filter
        // — it never quits the game anymore. Domingo hit it by accident
        // mid-flight one too many times; quitting now requires the
        // Alt+F4 combo below, matching the standard OS convention, so a
        // single stray Escape can no longer end the session. Edge-detected
        // so a single held Escape can never re-trigger across frames.
        bool esc = ks.IsKeyDown(XnaKeys.Escape);
        bool backBtn = GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed;
        if ((esc && !_escWasDown) || backBtn)
        {
            if (IsBriefingVisible(out _))
            {
                DismissBriefing();   // P271 — Esc closes the briefing popup, not the game
            }
            else if (_chatActive)
            {
                _chatActive = false;
                _chatBuffer.Clear();
            }
            else if (_qcMenuOpen)
            {
                BackQuickChat();   // back one level, or close if at the top
            }
            else if (_state == ClientState.Lobby && _filterActive)
            {
                ClearFilter();     // P274 — Esc clears the filter, never quits while filtering
            }
            // else: bare Escape with nothing to cancel now does nothing.
        }
        _escWasDown = esc;

        // Alt+F4: the actual quit combo now (replacing bare Escape above).
        // Handled in-app rather than left to the window manager, so it
        // quits reliably regardless of the desktop environment. Edge-
        // detected on F4 so holding it can't re-fire.
        bool altF4 = (ks.IsKeyDown(XnaKeys.LeftAlt) || ks.IsKeyDown(XnaKeys.RightAlt)) && ks.IsKeyDown(XnaKeys.F4);
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
        bool helpKey = ks.IsKeyDown(XnaKeys.H);
        if (helpKey && !_helpKeyWasDown && !_chatActive && !_qcMenuOpen)
        {
            _helpVisible = !_helpVisible;
            Console.WriteLine($"[monogame] help {( _helpVisible ? "ON" : "OFF" )}");
        }
        _helpKeyWasDown = helpKey;

        // P577 — F12 toggles the in-game Credits/Licences screen (the visible
        // CC BY / public-domain attribution for the HD sky's telescope data).
        // Edge-triggered and composing-gated like the H help toggle; scrolling
        // with PageUp/PageDown while it is open.
        bool creditsKey = ks.IsKeyDown(XnaKeys.F12);
        if (creditsKey && !_f12WasDown && !_chatActive && !_qcMenuOpen)
        {
            _creditsVisible = !_creditsVisible;
            Console.WriteLine($"[monogame] credits {( _creditsVisible ? "ON" : "OFF" )}");
        }
        _f12WasDown = creditsKey;
        if (_creditsVisible)
        {
            if (ks.IsKeyDown(XnaKeys.PageDown)) _creditsOverlayRenderer.Scroll(+42f);
            if (ks.IsKeyDown(XnaKeys.PageUp)) _creditsOverlayRenderer.Scroll(-42f);
        }

        // P265 — B scoreboard toggle (mnemonic: BoaRd / scoreBoaRd). B is an
        // unclaimed single letter (grep-verified, P257 KeyBindingTable: the only
        // letters in use are A C D E G H K M N Q R S T V W + modifiers). Edge-
        // triggered and composing-gated like the H help toggle, independent of
        // the starmap/help flags so the prior view resumes on close.
        bool scoreKey = ks.IsKeyDown(XnaKeys.B);
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
        bool pilotKey = ks.IsKeyDown(XnaKeys.P);
        if (pilotKey && !_pilotInfoKeyWasDown && !_chatActive && !_qcMenuOpen)
        {
            _pilotInfoVisible = !_pilotInfoVisible;
            Console.WriteLine($"[monogame] pilotinfo {( _pilotInfoVisible ? "ON" : "OFF" )}");
        }
        _pilotInfoKeyWasDown = pilotKey;

        // P572 — live-proof automation: feed world observations + drain the
        // machine's due actions into the pending slots the handlers below
        // consume, BEFORE any state logic runs this frame.
        FeedAutoProofObservations();

        // Top-level state dispatch: the server browser (default entry) and the
        // transient connecting state don't run any flight logic; only Flying does.
        if (_state != ClientState.Flying)
        {
            if (_state == ClientState.Lobby) UpdateLobby(ks);
            // Connecting is a transient pause while the wire handshake runs;
            // BeginConnect (in Initialize) flips us to SideSelect on success.
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
        (_enterWasDown, _qcRWasDown) = ComputeSharedEdgePrimes(ks);
        _backWasDown                = ks.IsKeyDown(XnaKeys.Back);
        _chatTabKeyWasDown          = ks.IsKeyDown(XnaKeys.Tab);
        _mKeyWasDown                = ks.IsKeyDown(XnaKeys.M);
        _iKeyWasDown                = ks.IsKeyDown(XnaKeys.I);
        _mouseDzKeyWasDown          = ks.IsKeyDown(XnaKeys.D5);
        _mouseLinKeyWasDown         = ks.IsKeyDown(XnaKeys.D6);
        _mouseSensDownKeyWasDown    = ks.IsKeyDown(XnaKeys.D7);
        _mouseSensUpKeyWasDown      = ks.IsKeyDown(XnaKeys.D8);
        _tabKeyWasDown              = ks.IsKeyDown(XnaKeys.Tab);
        _selPrevKeyWasDown          = ks.IsKeyDown(XnaKeys.OemComma);
        _selNextKeyWasDown          = ks.IsKeyDown(XnaKeys.OemPeriod);
        _vKeyWasDown                = ks.IsKeyDown(XnaKeys.V);
        _kKeyWasDown                = ks.IsKeyDown(XnaKeys.K);
        _cKeyWasDown                = ks.IsKeyDown(XnaKeys.C);
        _fCloakWasDown              = ks.IsKeyDown(XnaKeys.F);
        _lKeyWasDown                = ks.IsKeyDown(XnaKeys.L);
        _nKeyWasDown                = ks.IsKeyDown(XnaKeys.N);
        _gfireKeyWasDown            = ks.IsKeyDown(XnaKeys.OemTilde);
        _prevWKeyWasDown            = ks.IsKeyDown(XnaKeys.OemOpenBrackets);
        _nextWKeyWasDown            = ks.IsKeyDown(XnaKeys.OemCloseBrackets);
        _thUpWasDown                = ks.IsKeyDown(XnaKeys.OemPlus);
        _thDownWasDown              = ks.IsKeyDown(XnaKeys.OemMinus);
        _thZeroWasDown              = ks.IsKeyDown(XnaKeys.Back);
        _thFullWasDown              = ks.IsKeyDown(XnaKeys.OemBackslash) || ks.IsKeyDown(XnaKeys.OemPipe);
        _tKeyWasDown                = ks.IsKeyDown(XnaKeys.T);
        _gKeyWasDown                = ks.IsKeyDown(XnaKeys.G);
        _uTargetKeyWasDown          = ks.IsKeyDown(XnaKeys.U);
        _zTargetKeyWasDown          = ks.IsKeyDown(XnaKeys.Z);
        _semicolonNothingKeyWasDown = ks.IsKeyDown(XnaKeys.OemSemicolon);
        _apostropheCenterKeyWasDown = ks.IsKeyDown(XnaKeys.OemQuotes);
        _f11ScreenShotKeyWasDown    = ks.IsKeyDown(XnaKeys.F11);
        _pageDownKeyWasDown         = ks.IsKeyDown(XnaKeys.PageDown);
        _insertKeyWasDown           = ks.IsKeyDown(XnaKeys.Insert);
        _p1TurretKeyWasDown         = ks.IsKeyDown(XnaKeys.D1);
        _p2TurretKeyWasDown         = ks.IsKeyDown(XnaKeys.D2);
        _p3TurretKeyWasDown         = ks.IsKeyDown(XnaKeys.D3);
        _p4TurretKeyWasDown         = ks.IsKeyDown(XnaKeys.D4);
        _xKeyWasDown                = ks.IsKeyDown(XnaKeys.X);
        _acceptKeyWasDown           = ks.IsKeyDown(XnaKeys.Home);
        _rejectKeyWasDown           = ks.IsKeyDown(XnaKeys.End);
        _clearKeyWasDown            = ks.IsKeyDown(XnaKeys.Delete);
    }

    /// <summary>
    /// P523 — the pre-flight→flight EDGE-GATE for the keys the side-select /
    /// ready-check screens share with in-flight EDGE actions. Enter confirms a
    /// side (and opens chat in flight); R randomizes teams on side-select (and
    /// opens quick-chat in flight). If either is held when the mission starts, its
    /// in-flight edge must be primed (marked already-pressed) so the first flight
    /// frame does not fire it. Internal + pure over the keyboard state so a test
    /// can pin the guarantee headlessly (InternalsVisibleTo).
    /// </summary>
    internal static (bool Enter, bool R) ComputeSharedEdgePrimes(KeyboardState ks)
        => (ks.IsKeyDown(XnaKeys.Enter), ks.IsKeyDown(XnaKeys.R));

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

        bool musicKey = ks.IsKeyDown(XnaKeys.J);           // P304 — toggle music (J: Jam)
        if (musicKey && !_musicToggleWasDown && !_chatActive && !_qcMenuOpen)
            _music.Toggle();
        _musicToggleWasDown = musicKey;

        // P445 — music volume moved off the main-row +/= and -/_ keys: those
        // are now the persistent-throttle Up/Down (native's own bindings), the
        // higher-priority binding. Relocated to the free function-row F6 / F5
        // (volume up / down) — F4 (Alt+F4 quit) and F11 (screenshot) are the
        // only F-keys otherwise in use, so F5/F6 are unclaimed. Music volume
        // is the less important of the two bindings that collided.
        bool volUp = ks.IsKeyDown(XnaKeys.F6);            // F6 — music volume up
        if (volUp && !_musicVolUpWasDown && !_chatActive && !_qcMenuOpen)
            _music.VolumeUp();
        _musicVolUpWasDown = volUp;

        bool volDown = ks.IsKeyDown(XnaKeys.F5);          // F5 — music volume down
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
        // Chat: Enter opens the input line; while composing, Enter sends the
        // line via SendChat and Backspace deletes the previous character. Text
        // itself arrives through Window.TextInput (OnTextInput). Enter is
        // ignored while the quick-chat menu is open (its keys are navigation).
        bool enter = ks.IsKeyDown(XnaKeys.Enter);
        if (!_qcMenuOpen && enter && !_enterWasDown)
        {
            if (IsBriefingVisible(out _))
                DismissBriefing();   // P271 — Enter closes the briefing popup, doesn't open chat
            else if (_chatActive) SendChatLine();
            else { _chatActive = true; _chatBuffer.Clear(); }
        }
        _enterWasDown = enter;

        if (_chatActive)
        {
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
            bool chatTab = ks.IsKeyDown(XnaKeys.Tab);
            if (chatTab && !_chatTabKeyWasDown && ChatTargetLogic.CanCycleWhileComposing(_chatBuffer.ToString()))
            {
                _chatTarget = ChatTargetLogic.Next(_chatTarget);
                Console.WriteLine($"[monogame] chat target -> {_chatTarget} ({ChatTargetLogic.ComposeLabel(_chatTarget).Trim()})");
            }
            _chatTabKeyWasDown = chatTab;
        }

        // P217 — quick-chat voice macros. R (free in flight) opens the menu;
        // while it is open every shortcut key navigates instead of flying.
        bool qcKey = ks.IsKeyDown(XnaKeys.R);
        if (!_chatActive && qcKey && !_qcRWasDown && !_qcMenuOpen)
            OpenQuickChat();
        _qcRWasDown = qcKey;

        if (_qcMenuOpen)
            NavigateQuickChat(ks);

        // A modal UI consumes the keyboard: chat composition OR the quick-chat
        // menu both neutralise flight input / toggles / autopilot-steering, so
        // navigating or typing never also thrusts/turns the ship.
        bool composing = _chatActive || _qcMenuOpen;

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
        bool mMouse = ks.IsKeyDown(XnaKeys.M);
        if (!composing && mMouse && !_mKeyWasDown)
        {
            _mouseFlightEnabled = !_mouseFlightEnabled;
            IsMouseVisible = !_mouseFlightEnabled;
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
        bool iInvert = ks.IsKeyDown(XnaKeys.I);
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
        bool dzKey = ks.IsKeyDown(XnaKeys.D5);
        if (!composing && dzKey && !_mouseDzKeyWasDown)
        {
            var p = MouseFlightController.DeadzonePresets;
            int i = Array.FindIndex(p, d => MathF.Abs(d - _mouseFlight.Deadzone) < 0.0005f);
            i = i < 0 ? 0 : (i + 1) % p.Length;
            _mouseFlight.Deadzone = p[i];
            Console.WriteLine($"[monogame] mouse deadzone preset -> {p[i]:0.00} ({(int)(p[i] * 100)}%)");
        }
        _mouseDzKeyWasDown = dzKey;

        bool linKey = ks.IsKeyDown(XnaKeys.D6);
        if (!composing && linKey && !_mouseLinKeyWasDown)
        {
            _mouseFlight.LinearControls = !_mouseFlight.LinearControls;
            Console.WriteLine(_mouseFlight.LinearControls
                ? "[monogame] mouse response: LINEAR"
                : "[monogame] mouse response: QUADRATIC (native default)");
        }
        _mouseLinKeyWasDown = linKey;

        bool sensDown = ks.IsKeyDown(XnaKeys.D7);
        if (!composing && sensDown && !_mouseSensDownKeyWasDown)
        {
            _mouseFlight.MouseSensitivity -= MouseFlightController.MouseSensitivityStep;
            Console.WriteLine($"[monogame] mouse sensitivity DOWN -> {_mouseFlight.MouseSensitivity:F2}");
        }
        _mouseSensDownKeyWasDown = sensDown;

        bool sensUp = ks.IsKeyDown(XnaKeys.D8);
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
        bool tab = ks.IsKeyDown(XnaKeys.Tab);
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
        bool selPrev = ks.IsKeyDown(XnaKeys.OemComma);
        bool selNext = ks.IsKeyDown(XnaKeys.OemPeriod);
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
        bool vCam = ks.IsKeyDown(XnaKeys.V);
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
        ToggleCameraKey(ks, composing, XnaKeys.F4, CameraMode.ExternalOrbit, ref _f4KeyWasDown);
        ToggleCameraKey(ks, composing, XnaKeys.F5, CameraMode.ExternalFlyBy, ref _f5KeyWasDown);
        ToggleCameraKey(ks, composing, XnaKeys.F7, CameraMode.ExternalTarget, ref _f7KeyWasDown);
        ToggleCameraKey(ks, composing, XnaKeys.F10, CameraMode.ExternalBase, ref _f10KeyWasDown);
        ToggleCameraKey(ks, composing, XnaKeys.F11, CameraMode.ExternalStation, ref _f11KeyWasDown);

        // P580 — the director (F2) and spectator-follow (F3) toggles, plus the
        // rule that ANY flight input cancels the director immediately.
        HandleCinematicKeys(ks, composing);
        CancelDirectorOnInput(ks);

        // P612 — the spectator cameras (F8 free cam, F9 orbit cam) and the HUD
        // hide toggle (F1). While a spectator camera is active the ship holds
        // (neutral controls, see the control-send block below) and no stray
        // flight input leaks through.
        HandleSpectatorKeys(ks, composing, gameTime.ElapsedGameTime.TotalSeconds);

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
        bool kSuicide = ks.IsKeyDown(XnaKeys.K);
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
        bool cChaff = ks.IsKeyDown(XnaKeys.C);
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
        bool fCloak = ks.IsKeyDown(XnaKeys.F);
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
        bool lRipcord = ks.IsKeyDown(XnaKeys.L);
        if (!composing && lRipcord && !_lKeyWasDown)
        {
            bool ripcordActive;
            short? selected;
            lock (_session.Gate)
            {
                ripcordActive = _session.World.RipcordActive;
                selected = _selectedSectorId;
            }
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
        bool nMine = ks.IsKeyDown(XnaKeys.N);
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
        // server's lock-rebuild is the real fire-rate limiter. Bound to Left Ctrl —
        // unclaimed by every other binding (W/S/Q/E/Arrows/A/D/Shift/R/M/V/T/G/
        // Enter/Back/Escape/Space/K/C/N/OemTilde/[ /] all taken) and the common
        // flight-sim missile-fire convention (the brief's own suggestion).
        bool missileCtrl = ks.IsKeyDown(XnaKeys.LeftControl);
        if (!composing && missileCtrl)
        {
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastMissileSendMs >= 250)
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
            _lastMissileSendMs = Environment.TickCount64 - 250;
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
        bool gFire = ks.IsKeyDown(XnaKeys.OemTilde);
        if (!composing && gFire && !_gfireKeyWasDown)
        {
            _groupFire = !_groupFire;
            Console.WriteLine(_groupFire
                ? "[monogame] weapons: GROUP FIRE (all)"
                : $"[monogame] weapons: SINGLE (mount {_selectedMount})");
        }
        _gfireKeyWasDown = gFire;

        bool prevW = ks.IsKeyDown(XnaKeys.OemOpenBrackets);
        if (!composing && prevW && !_prevWKeyWasDown)
        {
            _selectedMount = FlightInput.PrevMount(_selectedMount, FixedMountCount());
            Console.WriteLine($"[monogame] selected mount {_selectedMount}");
        }
        _prevWKeyWasDown = prevW;

        bool nextW = ks.IsKeyDown(XnaKeys.OemCloseBrackets);
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
        bool thUp = ks.IsKeyDown(XnaKeys.OemPlus);
        bool thDown = ks.IsKeyDown(XnaKeys.OemMinus);
        bool thZero = ks.IsKeyDown(XnaKeys.Back);
        bool thFull = ks.IsKeyDown(XnaKeys.OemBackslash) || ks.IsKeyDown(XnaKeys.OemPipe);
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
            if (thFull && !_thFullWasDown)
            {
                _throttle = FlightInput.PresetValue(FlightInput.ThrottlePreset.Full);
                Console.WriteLine("[monogame] throttle FULL -> +1 (100%)");
            }
        }
        _thUpWasDown = thUp;
        _thDownWasDown = thDown;
        _thZeroWasDown = thZero;
        _thFullWasDown = thFull;

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
            long diagMs = Environment.TickCount64;
            if (rawDelta != 0 && diagMs - _lastWheelDiagMs >= 250)
            {
                _lastWheelDiagMs = diagMs;
                Console.WriteLine($"[monogame] wheel raw delta {rawDelta:+0;-0} (normalised sign {Math.Sign(FlightInput.NormalizeWheelDelta(rawDelta)):+0;-0})");
            }
            _wheelQueue += (int)FlightInput.NormalizeWheelDelta(rawDelta);
        }
        _lastWheelValue = wheelValue;

        if (!composing)
        {
            long nowMs = Environment.TickCount64;
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

        // P541 — left-click a sector on the star map (mini or expanded) to act on
        // it: ripcord target / teleport / docked view / own-sector no-op / goto.
        // Skipped while chat is composed so typing never trips a map click.
        if (!composing)
            HandleStarmapClick();

        // While composing chat, keyboard flight input is fully neutral — typing a
        // word must never also thrust/pitch/yaw the ship.
        var input = composing
            ? new FlightInputState { Throttle = _throttle }
            : new FlightInputState(
            ThrottleForward: ks.IsKeyDown(XnaKeys.W),
            ThrottleBack:    ks.IsKeyDown(XnaKeys.S),
            StrafeLeft:      ks.IsKeyDown(XnaKeys.Q),
            StrafeRight:     ks.IsKeyDown(XnaKeys.E),
            PitchUp:         ks.IsKeyDown(XnaKeys.Up),
            PitchDown:       ks.IsKeyDown(XnaKeys.Down),
            YawLeft:         ks.IsKeyDown(XnaKeys.Left),
            YawRight:        ks.IsKeyDown(XnaKeys.Right),
            RollLeft:        ks.IsKeyDown(XnaKeys.A),
            RollRight:       ks.IsKeyDown(XnaKeys.D),
            // P220 — afterburner (TK_FireBooster): held, continuous (not
            // edge-triggered), reading either Shift key. Mirrors native's
            // IsTrekKeyDown(TK_FireBooster) sense (WinTrek.cpp:8270-8271);
            // Shift is the recollection default since the native binding
            // table isn't in this checkout (see KEYBINDING_AUDIT_P220.md).
            Afterburner:     ks.IsKeyDown(XnaKeys.LeftShift) || ks.IsKeyDown(XnaKeys.RightShift),
            // P221 — fire (TK_FireWeapon): held, continuous (not edge-triggered)
            // like afterburner, so a held Space keeps firing (native
            // IsTrekKeyDown sense). Bound to Space — unclaimed by every other
            // binding (W/S/Q/E/Up/Down/Left/Right/A/D/Shift/R/M/V/T/G/Enter/
            // Back/Escape all taken). Absent a confirmed native default VK
            // table in this checkout (see KEYBINDING_AUDIT_P220.md), Space is
            // this port's own reasonable choice — the same justification P220
            // used for Shift→afterburner.
            Fire:            ks.IsKeyDown(XnaKeys.Space),
            // P413 — cloak (TK_ToggleCloak): the LATCHED client toggle state set
            // by the F key above (edge-triggered), carried in each frame exactly
            // like GroupFire/SelectedMount; Translate folds it into the outgoing
            // stateM as CloakActive (0x800). Kept on the state struct so the
            // key→stateM wiring is pure and unit-testable.
            Cloak:           _cloakToggled,
            // P225 — the client-local weapon-selection state carried in each
            // frame: group-fire vs the selected fixed mount (see the ` / [ / ]
            // handlers above). Translate folds these into the fire stateM
            // (AllWeapons when group-firing, OneWeapon|mount when not).
            GroupFire:       _groupFire,
            SelectedMount:   _selectedMount,
            // P445 — the client-owned persistent throttle axis, stepped by the
            // = / - keys and the mouse wheel above, sent verbatim as
            // ControlData.Throttle (idle -1 → full +1 cruise).
            Throttle:        _throttle);
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
        if (_mouseFlightEnabled && !composing && !_minimapExpanded && !SpectatorCameraActive)
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
            cmd = cmd with
            {
                Controls = cmd.Controls with { Yaw = outYaw, Pitch = outPitch },
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
        if (!composing && !SpectatorCameraActive)
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
            bool tPress = ks.IsKeyDown(XnaKeys.T);
            if (tPress && !_tKeyWasDown)
                HandleTargetCycleKey();
            _tKeyWasDown = tPress;

            bool gPress = ks.IsKeyDown(XnaKeys.G);
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

            // P415 — friend/foe filtered nearest targeting (the port of native's
            // Targeting family — TK_TargetEnemy/Friendly[Nearest/Prev],
            // trekinput.cpp:1531-1600 → FindTarget common.cpp:470). The pair
            // collapses to ONE cycling key each (first press = nearest, repeats
            // step through MATCHING candidates and wrap), gated behind the same
            // composing check as T. Plain key = ENEMY family, Shift+key =
            // FRIENDLY family (the brief's suggested layering).
            //   U        → nearest enemy ship (repeat = next enemy)
            //   Shift+U  → nearest friendly ship (repeat = next friendly)
            //   Z        → nearest enemy base (station) (repeat = next enemy base)
            //   Shift+Z  → nearest friendly / allied base (repeat = next)
            bool uTarget = ks.IsKeyDown(XnaKeys.U);
            if (uTarget && !_uTargetKeyWasDown)
            {
                bool shift = ks.IsKeyDown(XnaKeys.LeftShift) || ks.IsKeyDown(XnaKeys.RightShift);
                SelectFilteredTargetForKey(
                    shift ? new TargetFilterSpec(TargetRelation.Friendly)
                          : new TargetFilterSpec(TargetRelation.Enemy),
                    shift ? "friendly ship" : "enemy ship");
            }
            _uTargetKeyWasDown = uTarget;

            bool zTarget = ks.IsKeyDown(XnaKeys.Z);
            if (zTarget && !_zTargetKeyWasDown)
            {
                bool shift = ks.IsKeyDown(XnaKeys.LeftShift) || ks.IsKeyDown(XnaKeys.RightShift);
                // P587 — base targeting is now ANY-sector (native c_ttAnyCluster)
                // with the local hull's dockability filter; Shift+Z adds the
                // lifepod rescue-probe search. No "in sector" limitation.
                if (shift) SelectFriendlyBaseForKey();
                else SelectEnemyBaseForKey();
            }
            _zTargetKeyWasDown = zTarget;

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
            bool semiNothing = ks.IsKeyDown(XnaKeys.OemSemicolon);
            if (semiNothing && !_semicolonNothingKeyWasDown)
                ClearTarget();
            _semicolonNothingKeyWasDown = semiNothing;

            bool apostropheCenter = ks.IsKeyDown(XnaKeys.OemQuotes);
            if (apostropheCenter && !_apostropheCenterKeyWasDown)
                SelectCentermostTarget();
            _apostropheCenterKeyWasDown = apostropheCenter;

            // P417 — screenshot (TK_ScrnShot): latch a request; the actual
            // backbuffer capture happens at the end of Draw (Game1.Rendering.cs)
            // so the PNG holds the completed frame. F11 is unclaimed (only F4 is
            // used, for the Alt+F4 quit) and a conventional screenshot key.
            bool f11Shot = ks.IsKeyDown(XnaKeys.F11);
            if (f11Shot && !_f11ScreenShotKeyWasDown)
                _screenshotRequested = true;
            _f11ScreenShotKeyWasDown = f11Shot;

            // P559 — one-shot live-proof hook: ALLEGIANCE_AUTOSHOT=<delayMs>
            // (e.g. 12000) requests ONE real-backbuffer screenshot <delayMs>
            // after the first Flying update, using the exact same
            // CaptureBackbuffer path as F11, so a windowed live proof captures
            // the actual presented backbuffer without keyboard injection.
            if (!_autoShotDone && Environment.GetEnvironmentVariable("ALLEGIANCE_AUTOSHOT") is { Length: > 0 } autoShot)
            {
                if (_autoShotStartMs <= 0)
                    _autoShotStartMs = Environment.TickCount64;
                int delayMs = int.TryParse(autoShot, out var ad) && ad > 0 ? ad : 8000;
                if (Environment.TickCount64 - _autoShotStartMs >= delayMs)
                {
                    // P570 — dismiss the briefing popup first so the real-backbuffer
                    // proof frame isn't obscured by it (the briefing only auto-
                    // dismisses in the offscreen-capture path).
                    if (IsBriefingVisible(out _)) DismissBriefing();
                    _screenshotRequested = true;
                    _autoShotDone = true;
                }
            }

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
            bool pgdn = ks.IsKeyDown(XnaKeys.PageDown);
            if (pgdn && !_pageDownKeyWasDown)
                SelectCommandee(cycle: true);
            _pageDownKeyWasDown = pgdn;

            bool ins = ks.IsKeyDown(XnaKeys.Insert);
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
            bool p1T = ks.IsKeyDown(XnaKeys.D1);
            if (p1T && !_p1TurretKeyWasDown) TryPromoteTurretSeat(1);
            _p1TurretKeyWasDown = p1T;

            bool p2T = ks.IsKeyDown(XnaKeys.D2);
            if (p2T && !_p2TurretKeyWasDown) TryPromoteTurretSeat(2);
            _p2TurretKeyWasDown = p2T;

            bool p3T = ks.IsKeyDown(XnaKeys.D3);
            if (p3T && !_p3TurretKeyWasDown) TryPromoteTurretSeat(3);
            _p3TurretKeyWasDown = p3T;

            bool p4T = ks.IsKeyDown(XnaKeys.D4);
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
            bool xPress = ks.IsKeyDown(XnaKeys.X);
            if (xPress && !_xKeyWasDown && _leaderBotCommand is { } lbCmd)
            {
                _session.SendOrderChange(lbCmd.TargetShipId, Allegiance.Core.Wire.OrderChange.CidGoto, Allegiance.Core.Wire.OrderChange.CmdAccepted);
                Console.WriteLine($"[monogame] leader command: {lbCmd.Label} on bot {lbCmd.TargetShipId} (ORDER_CHANGE c_cidGoto/c_cmdAccepted)");
                _sound?.PlayAutopilotEngage();
            }
            _xKeyWasDown = xPress;

            // P424 — accept / reject / clear a received queued order (native
            // TK_AcceptCommand / TK_RejectCommand / TK_ClearCommand,
            // WinTrek.cpp:10389-10406). The local player's OWN pending queued
            // order arrives via the owner-only QUEUED_COMMAND_UPDATE(215) message
            // (world.MyQueuedCommand); this client has no sim to derive it from,
            // so the server streams it. All three edge-triggered + composing-gated
            // like every key above.
            //   Home    → ACCEPT: promote the pending queued order to the
            //             accepted slot (echoes the pending command id; the
            //             server promotes authoritatively, native
            //             AcceptQueuedCommand, WinTrek.cpp:10626-10712).
            //   End     → REJECT: clear the pending queued order (decline it,
            //             native RejectQueuedCommand, WinTrek.cpp:10583-10623).
            //   Delete  → CLEAR: cancel whatever is already ACCEPTED — distinct
            //             from rejecting a still-queued order (native
            //             TK_ClearCommand → SetAccepted(NULL, c_cidNone)).
            // Keys: Home/End/Delete are unclaimed (grep-verified — no other
            // binding uses any of them), chosen over ,/./ which the starmap nav
            // already claims.
            if (world.MyShipId is { } me)
            {
                bool acc = ks.IsKeyDown(XnaKeys.Home);
                if (acc && !_acceptKeyWasDown)
                {
                    var pend = world.MyQueuedCommand;
                    if (pend is { } p && p.CommandId != Allegiance.Core.Wire.OrderChange.CidNone)
                    {
                        _session.SendOrderChange(me, p.CommandId, Allegiance.Core.Wire.OrderChange.CmdQueued, (IgcObjectType)(-1), -1);
                        Console.WriteLine($"[monogame] ACCEPT queued order commandID={p.CommandId} (ORDER_CHANGE c_cmdQueued promote)");

                        // P594 — accepting a POD-PICKUP offer (CidPickup → a Ship
                        // target = the pod) engages the autopilot to go get them.
                        // Native's TK_ExecuteCommand / AcceptQueuedCommand bExecute
                        // branch (WinTrek.cpp:10626-10712) promotes the order AND
                        // sets the autopilot toward the target; that is exactly the
                        // rescue the player wants from "prepare for recovery" — and
                        // the P594 intercept autopilot now flies the player INTO the
                        // pod at full speed. Home therefore selects the pod and
                        // engages, so the offer and the rescue are one keypress.
                        if (PickupPodIdForQueued(pend) is { } podId)
                        {
                            string podName = "pod";
                            lock (_session.Gate)
                                if (world.Ships.TryGetValue(podId, out var pe)
                                    && !string.IsNullOrEmpty(pe.CharacterName))
                                    podName = pe.CharacterName;
                            EngageAutopilotToward(
                                new AutopilotTarget(TargetKind.Ship, podId, $"{podName} #{podId}"),
                                "pickup accept:");
                        }
                    }
                    else
                    {
                        Console.WriteLine("[monogame] ACCEPT with no pending queued order — nothing sent");
                    }
                }
                _acceptKeyWasDown = acc;

                bool rej = ks.IsKeyDown(XnaKeys.End);
                if (rej && !_rejectKeyWasDown)
                {
                    _session.SendOrderChange(me, Allegiance.Core.Wire.OrderChange.CidNone, Allegiance.Core.Wire.OrderChange.CmdQueued, (IgcObjectType)(-1), -1);
                    Console.WriteLine("[monogame] REJECT queued order (ORDER_CHANGE c_cmdQueued clear)");
                }
                _rejectKeyWasDown = rej;

                bool cls = ks.IsKeyDown(XnaKeys.Delete);
                if (cls && !_clearKeyWasDown)
                {
                    _session.SendOrderChange(me, Allegiance.Core.Wire.OrderChange.CidNone, Allegiance.Core.Wire.OrderChange.CmdAccepted, (IgcObjectType)(-1), -1);
                    Console.WriteLine("[monogame] CLEAR accepted order (ORDER_CHANGE c_cmdAccepted clear)");
                }
                _clearKeyWasDown = cls;
            }
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
            !composing && !SpectatorCameraActive && (
            input.ThrottleForward || input.ThrottleBack || input.StrafeLeft || input.StrafeRight ||
            input.PitchUp || input.PitchDown || input.YawLeft || input.YawRight ||
            input.RollLeft || input.RollRight ||
            mouseManualInput ||
            joystickTurnActive);   // P380 — a real stick deflection past deadzone is manual input too
        if (_autoPilotEngaged && manualFlightInput)
            DisengageAutopilot("manual input");

        // Send controls: while autopilot drives, its steering REPLACES the
        // manual command and the movement buttons stay neutral (stateM=0) — it
        // only uses the analog throttle axis (AutopilotSteering.Throttle), so
        // it does NOT go through FlightInput's keyboard idle-throttle (-1)
        // convention. If the target can no longer be resolved (died/vanished)
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
            sentFireM = UpdateTurretGunner(cmd.Controls, cmd.StateM, (float)gameTime.ElapsedGameTime.TotalSeconds);
            sentControls = cmd.Controls;
            sentStateM = cmd.StateM;
        }
        else
        {
            // Left the turret (or was never on one) — a fresh board must
            // re-seed the aim orientation and re-arm the turret send cadence.
            _turretSeeded = false;
            _turretCadence.Reset();
            if (_autoPilotEngaged && TryGetAutopilotControls(out var apControls))
            {
                sentControls = apControls;
                sentStateM = 0;
                sentFireM = 0;   // autopilot drives with neutral fire
            }
            else
            {
                if (_autoPilotEngaged) DisengageAutopilot("target lost");
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
        if (SpectatorCameraActive)
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
        bool sendArm = !turretGunner;
        _ownShipSendArmed = sendArm;
        if (sendArm)
            _ownSim.Step((float)gameTime.ElapsedGameTime.TotalSeconds, sentControls, sentStateM);

        // Recompute every ship's render pose from its raw last-known
        // ServerShipUpdate — no smoothing/prediction (see PredictedPose's
        // remarks). Locked against the receive thread's concurrent upserts
        // into world.Ships — see ConnectionSession.Gate's doc comment.
        _poses.Clear();
        _damageEmitters.Clear();
        _engineTrailEmitters.Clear();
        _nozzleGlowEmitters.Clear();
        _cloakedShips.Clear();
        _shipModelById.Clear();
        _shipTextureById.Clear();   // P337 — parallel per-ship texture cache
        _shipScaleById.Clear();
        _nativeShipRefById.Clear(); // P566 — parallel per-ship native-art cache
        float updateDt = (float)gameTime.ElapsedGameTime.TotalSeconds;   // P358 — powers the afterburner ramp
        lock (_session.Gate)
        {
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
                // Own ship also drives the P213 engine/ambience mix from its
                // real telemetry.
                if (world.Ships.TryGetValue(me, out var mine) && mine.Latest is { } mineUp)
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
                    _sound?.Update(
                        throttle: mineUp.Controls.Throttle,
                        speed: mineUp.Velocity.Length(),
                        cockpitMode: _cameraMode == CameraMode.Cockpit,
                        afterburnerActive: (mineUp.StateM & Allegiance.Core.Sim.ShipControlState.AfterburnerButton) != 0,
                        deltaSeconds: (float)gameTime.ElapsedGameTime.TotalSeconds,
                        missileLocked: world.MissileLock.IsFullLock);   // P230 — lock beep at full lock

                    // P267 — the hull's maneuvering-thruster (turn) loop, driven
                    // from the local ship's real commanded turn axes (the same
                    // ControlData the engine mix reads), and the hull exterior
                    // loop audible in the chase/exterior view.
                    _sound?.UpdateTurnThruster(
                        Audio.TurnThrusterSound.LevelFor(
                            mineUp.Controls.Yaw, mineUp.Controls.Pitch, mineUp.Controls.Roll));
                    _sound?.UpdateShipExterior(_cameraMode != CameraMode.Cockpit);
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
                if (id == world.MyShipId || ship.IsNotVisible || ship.Latest is not { } up) continue;
                // P448b — resolve this ship's RENDER pose: dead-reckon it to "now"
                // (P448a ShipDeadReckoner, native CalculateShip replay) unless the
                // kill switch is off or the reckoner isn't seeded (falls back to the
                // raw last-known pose). Own-ship posing stays raw (P447); ships with
                // no pose were already excluded above.
                Flight.PredictedPose pose = RemoteShipPose(world, id, up);
                _poses[id] = pose;
                // P297 — any other visible damaged ship vents too (native's
                // per-ship AddDamageParticles scope — not just the local hull).
                // P338 — pass the ship's real rendered radius (0.5 × HullType.Length)
                // so the vent offsets scale with the actual hull, not the old
                // fixed 50-unit stand-in.
                float tl = HullLength(ship.HullId);
                float radius = tl > 0f ? 0.5f * tl : 0f;
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

        // P267 — the station destroyed/captured one-shots and the tech-completion
        // chime, drained from the wire events the world observed this frame.
        UpdateSoundEvents();

        // P578 — eject/death/rescue feedback lines (tips + critical/non-critical
        // message cues) and the eject "gloat camera" swing to the killer,
        // drained from the world's KILL_SHIP/PLAYER_RESCUED queues each frame.
        UpdateFeedback();

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

        // P216 — mission announcer cues, from the same real per-frame mission
        // read. Countdown: the server's SET_START_TIME(14) broadcast is what
        // announces the countdown — the client's Mission.Stage is pinned to
        // StageStarted by the join burst (Server.cs:2138), so it cannot
        // distinguish the brief STAGE_STARTING window; the real "countdown
        // began" signal is that SET_START_TIME receipt, anchored on the world as
        // CountdownStartLocalMs (the same timeStart field native's
        // CheckCountdownSound anchors on, WinTrek.cpp:11144). Fire the
        // countdownstart callout once at the opening, then each per-second tick
        // (countdown10..countdown1) exactly once per distinct seconds-remaining,
        // mirroring native's m_nLastCountdown change-detection (WinTrek.cpp:11150).
        if (world.CountdownStartLocalMs is { } cdStart)
        {
            if (!_countdownStartCuePlayed)
            {
                _sound?.PlayCountdownStart();
                _countdownStartCuePlayed = true;
                _countdownLastSecond = 0;
                Console.WriteLine("[monogame] audio: countdown-start");
            }

            double elapsed = (Environment.TickCount64 - cdStart) / 1000.0;
            int remaining = (int)Math.Ceiling(Audio.CountdownSound.BriefingCountdownSeconds - elapsed);
            if (remaining != _countdownLastSecond)
            {
                _countdownLastSecond = remaining;
                if (Audio.CountdownSound.TickAssetFor(remaining) is { })
                {
                    _sound?.PlayCountdownSecond(remaining);
                    Console.WriteLine($"[monogame] audio: countdown tick {remaining}");
                }
            }
        }

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

        // P217 — receive-side quick-chat voices: play the real voice for any
        // newly-arrived quick-chat (which includes the sender's own server
        // relay echo, so a locally-sent macro is voiced exactly once by this
        // path — never doubled locally at the send site).
        ProcessIncomingQuickChatVoices();

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
    private Flight.PredictedPose RemoteShipPose(ClientWorld world, short id, ServerShipUpdate up)
    {
        if (_remoteShipDeadReckoning &&
            world.TryGetRenderPose(id, Environment.TickCount64 / 1000.0, out var pos, out var ori))
        {
            return new Flight.PredictedPose(pos, ori.Copy());
        }
        return new Flight.PredictedPose(up.Position, up.Orientation);
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
            DisengageAutopilot("arrived");
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
                DisengageAutopilot("docked");
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
                DisengageAutopilot("rescued");
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
                DisengageAutopilot("arrived");
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

    /// <summary>P575 — fallback braking deceleration when the own hull's
    /// Thrust/Mass is unavailable (matches AutopilotBrakeDeceleration).</summary>
    private const float DockApproachFallbackDecel = 55f;

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

            // World-rotated bays from the capgarage plane-0 slots
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
                hull.HasCapability(DataHullTypeIGC.HabmFighter), bays, geom.LandSlots.Count);

            float speed = vel.Length();
            float decel = hull.Mass > 0f ? hull.Thrust / hull.Mass : DockApproachFallbackDecel;
            float distanceRest;
            SimVec positionRest;
            if (speed > 0.1f && decel > 0f)
            {
                distanceRest = (speed * speed) / (2f * decel);
                positionRest = pos + vel * (distanceRest / speed);
            }
            else { distanceRest = 0f; positionRest = pos; }

            float shipRadius = 0.5f * hull.Length;
            float totalRadius = shipRadius + stype.Radius + 10f;

            // Client has no per-axis turn-rate telemetry here → pass 0; the
            // enter gate then reduces to the alignment check (safe: once the nose
            // lines up with the bay entry it accelerates in), a documented
            // client-side simplification of native's rate gate (common.cpp:1301).
            var step = DockApproach.DockStation(
                pos, vel, orient, center, bays, nLand, distanceRest, positionRest,
                totalRadius, rateYaw: 0f, ratePitch: 0f, experimental: false);

            controls = DockApproach.Steer(step, pos, orient, vel, distanceRest);
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
    /// P594 — the pod <b>ship id</b> a pending queued order asks the local
    /// player to pick up, or null when it is not a lifepod pickup offer. Native
    /// <c>AcceptQueuedCommand</c>'s <c>bExecute</c> branch (WinTrek.cpp:10626-
    /// 10712) accepts the queued order AND sets the autopilot toward its target;
    /// this is the pure decision for that target: only a <c>CidPickup</c> whose
    /// target is a <c>Ship</c> (the pod) should engage the intercept autopilot.
    /// Pure + static so the accept→engage wiring is unit-testable without a
    /// live world.
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
            _nowSeconds, Environment.TickCount64);

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

    /// <summary>Clear the autopilot (engaged flag + stored target) with a
    /// console note. Called on G toggle-off, on ANY manual flight input (native
    /// SenseJoystick behaviour), and when the target becomes unresolvable.</summary>
    private void DisengageAutopilot(string reason)
    {
        if (!_autoPilotEngaged && _autoTarget is null) return;
        bool wasEngaged = _autoPilotEngaged;
        _autoPilotEngaged = false;
        _autoTarget = null;
        _autoTargetAtEngage = null;
        Console.WriteLine($"[monogame] autopilot OFF ({reason})");
        if (wasEngaged) _sound?.PlayAutopilotDisengage();
        RefreshWindowTitle();
    }
}
