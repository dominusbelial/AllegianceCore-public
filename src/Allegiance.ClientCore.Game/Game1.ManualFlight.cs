using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    // Shared desktop/browser sampling. Training supplies its existing two overrides.
    // Key identities and the native control mapping remain in ActionMap/FlightInput.
    private FlightInputState SampleManualFlightInput(KeyboardState ks, bool strafeUp, bool booster)
        => new FlightInputState(
            ThrottleForward: ActionDown(ks, "ThrustForward"),
            ThrottleBack:    ActionDown(ks, "ThrustBackward"),
            StrafeLeft:      ActionDown(ks, "ThrustLeft"),
            StrafeRight:     ActionDown(ks, "ThrustRight"),
            // P624 — vertical strafe (TK_ThrustUp/Down → UpButton/DownButton).
            // _fkExerciseStrafe is the proof-only override (ALLEGIANCE_FLIGHTKEYS_PROOF)
            // that drives strafe-up through the real send path without a physical key.
            StrafeUp:        strafeUp,
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
            // SPACE on the native layout, Shift on the Port preset (P941).
            // Basic Flight also accepts the prompted native SPACE (P940).
            Afterburner:     booster,
            // P221 — fire (TK_FireWeapon): held, continuous (not edge-triggered)
            // like afterburner, so a held Space keeps firing (native
            // IsTrekKeyDown sense). Bound to Space — unclaimed by every other
            // binding (W/S/Q/E/Up/Down/Left/Right/A/D/Shift/R/M/V/T/G/Enter/
            // Back/Escape all taken). Absent a confirmed native default VK
            // table in this checkout (see KEYBINDING_AUDIT_P220.md), Space is
            // this port's own reasonable choice — the same justification P220
            // used for Shift→afterburner.
            Fire:            ActionDown(ks, "FireWeapon"),
            // P886b — fire missile (TK_FireMissile, Left Ctrl / mouse-middle): held
            // like Fire. Translate folds it into the local stateM as the
            // missileFireIGC bit (the training MissileFire gate); the actual launch
            // rides the FIRE_MISSILE(70) wire request handled above.
            MissileFire:     ActionDown(ks, "FireMissile"),
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
            Throttle:        _throttle);
}
