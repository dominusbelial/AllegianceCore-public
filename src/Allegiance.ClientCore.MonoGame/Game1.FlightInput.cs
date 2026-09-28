using System.Net.Http;
using System.Net.Http.Json;
using System.Linq;
using System.Text;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
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

    protected override void Update(GameTime gameTime)
    {
        _nowSeconds += gameTime.ElapsedGameTime.TotalSeconds;
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

        // Top-level state dispatch: the server browser (default entry) and the
        // transient connecting state don't run any flight logic; only Flying does.
        if (_state != ClientState.Flying)
        {
            if (_state == ClientState.Lobby) UpdateLobby(ks);
            // Connecting is a transient pause while the wire handshake runs;
            // BeginConnect (in Initialize) flips us to SideSelect on success.
            else if (_state == ClientState.SideSelect) UpdateSideSelect(ks);
            // P282 — pre-flight ready screen once seated, before the mission starts.
            else if (_state == ClientState.ReadyCheck) UpdateReadyCheck(ks);
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

        bool volUp = ks.IsKeyDown(XnaKeys.OemPlus);        // +/= — music volume up
        if (volUp && !_musicVolUpWasDown && !_chatActive && !_qcMenuOpen)
            _music.VolumeUp();
        _musicVolUpWasDown = volUp;

        bool volDown = ks.IsKeyDown(XnaKeys.OemMinus);     // -/_ — music volume down
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
        if (!composing && vCam && !_vKeyWasDown)
        {
            _cameraMode = Rendering.CameraCycler.Next(_cameraMode, IsTurretGunner());
            Console.WriteLine($"[monogame] camera: {_cameraMode}");
        }
        _vKeyWasDown = vCam;

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

        // While composing chat, keyboard flight input is fully neutral — typing a
        // word must never also thrust/pitch/yaw the ship.
        var input = composing
            ? FlightInputState.None
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
            SelectedMount:   _selectedMount);
        var cmd = FlightInput.Translate(input);

        // Raw mouse delta this frame, captured for BOTH the virtual-joystick
        // deflection below and the autopilot's manual-input disengage check
        // (a physical mouse movement past deadzone = manual flight input).
        int mouseDx = 0, mouseDy = 0;
        if (_mouseFlightEnabled && !composing)
        {
            var ms = Mouse.GetState();
            int centerX = GraphicsDevice.Viewport.Width / 2;
            int centerY = GraphicsDevice.Viewport.Height / 2;
            mouseDx = ms.X - centerX;
            mouseDy = ms.Y - centerY;

            var defl = _mouseFlight.Update(mouseDx, mouseDy);
            cmd = cmd with
            {
                Controls = cmd.Controls with { Yaw = defl.Yaw, Pitch = defl.Pitch },
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
                if (_autoPilotEngaged)
                {
                    DisengageAutopilot("toggle-off");
                }
                else
                {
                    // Implicit nearest pick if none selected yet, then engage.
                    if (_autoTarget is not { } tgt && TryOwnPose(out var pPos, out var pOrient, out _))
                        _autoTarget = PickNearestTarget(pPos, pOrient);
                    if (_autoTarget is { } at)
                    {
                        _autoPilotEngaged = true;
                        // M-1 — capture the virtual-joystick baseline at engage
                        // (native's trekJoyStick snapshot, WinTrek.cpp:8283-8289)
                        // so only a deflection *past 2× deadzone from here*
                        // counts as manual input that disengages the autopilot.
                        _autoPilotMouseBaseline = _mouseFlight.Current;
                        _autoTargetAtEngage = ResolveTargetPosition(at, out var atPos) ? atPos : null;
                        Console.WriteLine($"[monogame] autopilot ON → {at.Name} ({at.Kind}#{at.Id})");
                        _sound?.PlayAutopilotEngage();
                        RefreshWindowTitle();
                    }
                }
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
                SelectFilteredTargetForKey(
                    shift ? new TargetFilterSpec(TargetRelation.Friendly, StationOnly: true)
                          : new TargetFilterSpec(TargetRelation.Enemy, StationOnly: true),
                    shift ? "friendly base" : "enemy base");
            }
            _zTargetKeyWasDown = zTarget;

            // P417 — Target Self / Nothing / Center, the small leftover
            // targeting keys (native TK_TargetSelf/Nothing/Center). Edge-
            // triggered and composing-gated like U/Z/T above.
            //   Y      → target self (select your own ship; no command)
            //   ;      → target nothing (clear target + disengage autopilot)
            //   '      → target center (nearest to the reticle/crosshair)
            // Keys: Y is unclaimed in flight (only the ready-check screen uses
            // it, a different state); ; and ' are punctuation unclaimed by every
            // binding (grep-verified; the sole OemSemicolon/OemQuotes-adjacent
            // keys in use are , / . starmap nav and the [+/-] music volume).
            bool yTargetSelf = ks.IsKeyDown(XnaKeys.Y);
            if (yTargetSelf && !_yTargetSelfKeyWasDown)
                SelectSelfTarget();
            _yTargetSelfKeyWasDown = yTargetSelf;

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
            !composing && (
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
        if (_autoPilotEngaged && TryGetAutopilotControls(out var apControls))
        {
            _session.CommandControls(apControls, 0);
        }
        else
        {
            if (_autoPilotEngaged) DisengageAutopilot("target lost");
            _session.CommandControls(cmd.Controls, cmd.StateM);
        }

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
                    // P255 — while the local ship is actively mining a rock, its
                    // rendered facing is overridden to aim at the rock (the
                    // per-tick MiningAsteroidId the server streams), mirroring
                    // native's mining autopilot turn-to-face — plus a gentle
                    // roll "for effect" (native shipIGC.cpp:2251-2253). Applies
                    // in every camera mode via the same pose the camera and hull
                    // draw from; when mining stops (or the rock is gone) the
                    // server-authoritative orientation is used untouched.
                    Orientation poseOrientation = mineUp.Orientation;
                    var rockId = world.MiningAsteroidId;
                    if (rockId != -1 && world.Asteroids.TryGetValue(rockId, out var rockAst))
                        poseOrientation = MiningFacing(mineUp.Orientation, mineUp.Position, rockAst.Position,
                            (float)gameTime.ElapsedGameTime.TotalSeconds);
                    _poses[me] = new Flight.PredictedPose(mineUp.Position, poseOrientation);
                    // P333 — own ship was excluded from model/scale resolution
                    // entirely (only the teammate loop below populated these),
                    // so the local hull always drew the unnormalized fallback
                    // model at scale 1 in every non-cockpit camera mode — the
                    // single most-seen ship in the game, made WORSE (not
                    // better) once every other ship got correctly scaled.
                    // Mirrors the teammate loop's own resolution exactly.
                    _shipModelById[me] = ResolveShipModel(mine.SideId, mine.HullId);
                    _shipTextureById[me] = ResolveShipTexture(mine.SideId, mine.HullId);   // P337
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
                        GatherDamageEmitter(me, mineUp, _myRenderRadius);
                    // P358 — the local ship's engine trail: the afterburner power
                    // ramp updates every frame, but the trail is emitted only
                    // outside the cockpit (the hull/afterburner aren't drawn in
                    // the internal cockpit view), matching the P297 gate.
                    UpdateEngineTrail(me, mineUp, _myRenderRadius, updateDt,
                        _cameraMode != CameraMode.Cockpit);
                    // P365 — the local ship's engine-nozzle glow: gathered only
                    // outside the cockpit (its hull isn't drawn there), matching
                    // the P358 trail's gate. Driven by the real throttle +
                    // afterburner bit, so the glow appears and colour-shifts as
                    // the ship thrusts.
                    UpdateNozzleGlow(me, mineUp, _myRenderRadius, _cameraMode != CameraMode.Cockpit);
                    // P367 — the local ship's navigation strobe lights: gathered
                    // only outside the cockpit (its hull isn't drawn there),
                    // matching the P358 trail / P365 glow gate. Unlike those
                    // thrust-driven effects, strobes are always blinking (driven
                    // purely by elapsed time), so the emitter is recorded whenever
                    // the hull is known and visible.
                    UpdateNavStrobes(me, mineUp, _myRenderRadius, _cameraMode != CameraMode.Cockpit);
                    // P363 — the local ship's side-coloured motion trail: fed
                    // every frame its anchor/right/side-colour, but like the
                    // engine trail only outside the cockpit (the hull isn't
                    // drawn in the internal cockpit view), matching P358/P297.
                    UpdateMotionTrail(me, mineUp, _myRenderRadius, mine.SideId,
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
                _poses[id] = new Flight.PredictedPose(up.Position, up.Orientation);
                // P297 — any other visible damaged ship vents too (native's
                // per-ship AddDamageParticles scope — not just the local hull).
                // P338 — pass the ship's real rendered radius (0.5 × HullType.Length)
                // so the vent offsets scale with the actual hull, not the old
                // fixed 50-unit stand-in.
                float tl = HullLength(ship.HullId);
                GatherDamageEmitter(id, up, tl > 0f ? 0.5f * tl : 0f);
                // P358 — any other visible ship's engine trail too (native's
                // per-ship AddThrustParticles scope — every thrusting ship the
                // client renders, not just the local hull).
                UpdateEngineTrail(id, up, tl > 0f ? 0.5f * tl : 0f, updateDt, visible: true);
                // P365 — any other visible ship's engine-nozzle glow too (native
                // RenderGlow's per-ship scope — every thrusting ship the client
                // renders, not just the local hull).
                UpdateNozzleGlow(id, up, tl > 0f ? 0.5f * tl : 0f, visible: true);
                // P367 — every other visible ship's navigation strobe lights too
                // (native LightsGeo's per-ship scope — every visible ship the
                // client renders, not just the local hull).
                UpdateNavStrobes(id, up, tl > 0f ? 0.5f * tl : 0f, visible: true);
                // P363 — every other visible ship's side-coloured motion trail
                // too (native's per-ship ThingSite scope): fed its anchor/
                // right/side-colour, tinted by ITS OWN side colour (from the
                // roster), so remote and cross-side ships trail in their real
                // team colour, not a hardcoded one.
                UpdateMotionTrail(id, up, tl > 0f ? 0.5f * tl : 0f, ship.SideId, visible: true);
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
                // P333 — per-ship PURE VISUAL scale: normalize the model that
                // will actually be drawn (resolved model or the fallback) to the
                // real hull size. 1 when the hull/model is unknown → no scaling.
                var drawnModel = _shipModelById[id] ?? _fallbackModel;
                _shipScaleById[id] = Rendering.ShipRenderScale.ScaleForLength(
                    HullLength(ship.HullId), drawnModel);
            }
        }

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
        if (!ResolveTargetPosition(tgt, out var targetPos)) return false;
        if (!TryOwnPose(out var pos, out var orient, out var vel)) return false;

        float distance = (targetPos - pos).Length();
        bool targetStationary = _autoTargetAtEngage is { } engagePos
            && (targetPos - engagePos).Length() < 1f;
        if (targetStationary && distance <= AutopilotSteering.AutopilotArrivalRadius)
        {
            DisengageAutopilot("arrived");
            return false;
        }

        // P343 — size the braking-distance term with the ship's REAL max
        // deceleration (HullType.Thrust / HullType.Mass — the throttle-cruise
        // limit ExecuteMove actually achieves) when the shop catalogue resolves
        // the own hull; fall back to AutopilotBrakeDeceleration when it does
        // not (missing/empty catalogue — the server still boots fine, the
        // autopilot just brakes a little earlier than optimal).
        float decel = AutopilotSteering.AutopilotBrakeDeceleration;
        lock (_session.Gate)
        {
            if (_session.World.MyShipId is { } me && _session.World.Ships.TryGetValue(me, out var mine)
                && _shopCatalog is { } cat
                && cat.Hulls.FirstOrDefault(h => h.HullId == mine.HullId) is { Mass: > 0f, Thrust: > 0f } hull)
                decel = hull.Thrust / hull.Mass;
        }

        controls = AutopilotSteering.SteerToward(pos, orient, targetPos, vel, brakeDeceleration: decel);
        return true;
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
                    if (world.Ships.TryGetValue(tgt.Id, out var ship) && ship.Latest is { } up && !ship.IsNotVisible)
                    {
                        pos = up.Position;
                        return true;
                    }
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
            }
        }
        pos = default;
        return false;
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
