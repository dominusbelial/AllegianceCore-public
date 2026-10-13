using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P624 — real-window proof of the flight-keys pack, driven by
/// ALLEGIANCE_FLIGHTKEYS_PROOF=1 so scripts/live-proof.sh can photograph the
/// REAL client backbuffer. Mirrors the P625 keymap proof pattern
/// (Game1.KeyBind.cs, RunKeymapProof).
///
/// Sequence (each step ~1.6 s, one real backbuffer screenshot per step):
///   cockpit-forward → the seven look directions (ViewRight, ViewRear,
///   ViewLeft, ViewFrontLeft, ViewFrontRight, ViewRearLeft, ViewRearRight) →
///   the four radar modes (All/Default/Target/Minimal) → an exercise phase
///   that ENGAGES vector lock (coast), a match-speed throttle set and vertical
///   strafe-up through the real send path (so the server's reconciliation is
///   exercised — the no-SHIP_RESET storm check reads server.out's [ctrlak]
///   reject count) → reset + done.
/// </summary>
public sealed partial class Game1
{
    private bool _fkProofStarted, _fkProofDone;
    private double _fkProofElapsed;
    private int _fkProofStep;
    /// <summary>Proof-only override that forces the vertical-strafe-up bit
    /// through the real input construction (no physical key needed).</summary>
    private bool _fkExerciseStrafe;

    private void RunFlightKeysProof(double dtSec)
    {
        if (_fkProofDone) return;
        _fkProofElapsed += dtSec;

        if (!_fkProofStarted)
        {
            _fkProofStarted = true;
            _fkProofElapsed = 0;
            _fkProofStep = 0;
            _cameraMode = CameraMode.Cockpit;
            _headLookYaw = 0f;
            SayShot("cockpit forward (baseline)");
            return;
        }

        const double stepDt = 1.6;
        if (_fkProofElapsed < stepDt) return;
        _fkProofElapsed = 0;
        _fkProofStep++;

        switch (_fkProofStep)
        {
            // look cameras (held views)
            case 1: _headLookYaw = HeadLook.Right;         SayShot("look right (D4)"); break;
            case 2: _headLookYaw = HeadLook.Rear;          SayShot("look rear (D7)"); break;
            case 3: _headLookYaw = HeadLook.Left;          SayShot("look left (D3)"); break;
            case 4: _headLookYaw = HeadLook.FrontLeft;     SayShot("look front-left (D1)"); break;
            case 5: _headLookYaw = HeadLook.FrontRight;    SayShot("look front-right (D2)"); break;
            case 6: _headLookYaw = HeadLook.RearLeft;      SayShot("look rear-left (D6)"); break;
            case 7: _headLookYaw = HeadLook.RearRight;     SayShot("look rear-right (D8)"); break;
            // radar modes
            case 8:  _headLookYaw = 0f; _radarMode = RadarMode.All;     SayShot("radar All"); break;
            case 9:  _radarMode = RadarMode.Default;                     SayShot("radar Default"); break;
            case 10: _radarMode = RadarMode.Target;                      SayShot("radar Target"); break;
            case 11: _radarMode = RadarMode.Minimal;                     SayShot("radar Minimal"); break;
            // exercise the server-facing flight commands (no-rejection check)
            case 12:
                _radarMode = RadarMode.Default;
                _cameraMode = CameraMode.Cockpit;
                _coastToggled = true;                  // vector lock (CoastButton) on the wire
                _fkExerciseStrafe = true;              // vertical strafe-up bit on the wire
                if (TryMatchSpeedThrottle(out float mt))
                {
                    _throttle = mt;                    // match-speed throttle on the wire
                    Console.WriteLine($"[fk-proof] match speed engaged: throttle {mt:F2}");
                }
                Console.WriteLine("[fk-proof] vector lock ON + strafe up engaged (watch server [ctrlak])");
                SayShot("vector-lock + strafe-up + match engaged");
                break;
            case 13:
                _coastToggled = false;
                _fkExerciseStrafe = false;
                _headLookYaw = 0f;
                _fkProofDone = true;
                Console.WriteLine("[fk-proof] done (12 real frames requested)");
                break;
        }
    }

    private void SayShot(string what)
    {
        _screenshotRequested = true;
        Console.WriteLine($"[fk-proof] shot: {what}");
    }
}
