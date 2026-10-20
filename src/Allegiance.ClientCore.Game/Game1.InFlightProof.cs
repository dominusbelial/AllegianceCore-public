namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P626 — real-window proof of the in-flight utility pack, driven by
/// ALLEGIANCE_INFLIGHT_PROOF=1 so scripts/live-proof.sh can photograph the REAL
/// client backbuffer (mirrors the P624 / P625 proof hooks).
///
/// Sequence (each step ~1.6 s): baseline → chat pane paged back (system lines
/// posted then ChatPageUp, showing the "(OLDER)" scroll window) → cargo
/// selection cycle → help overlay ON (left on for the remaining autoshots, so
/// the later real-window frames show every new P626 command and the two new
/// help categories).
/// </summary>
public sealed partial class Game1
{
    private bool _inflightProofStarted, _inflightProofDone;
    private double _inflightProofElapsed;
    private int _inflightProofStep;

    private void RunInFlightUtilityProof(double dtSec)
    {
        if (_inflightProofDone) return;
        _inflightProofElapsed += dtSec;

        if (!_inflightProofStarted)
        {
            _inflightProofStarted = true;
            _inflightProofElapsed = 0;
            _inflightProofStep = 0;
            SayInflightShot("inflight pack start (baseline)");
            return;
        }

        const double stepDt = 1.6;
        if (_inflightProofElapsed < stepDt) return;
        _inflightProofElapsed = 0;
        _inflightProofStep++;

        switch (_inflightProofStep)
        {
            case 1:
                // Build a scrollable chat backlog, then page it back so the
                // "(OLDER - PgDn to newest)" scroll window renders.
                for (int i = 0; i < 14; i++)
                {
                    lock (_session.Gate)
                        _session.World.PostSystemLine($"Proof system line {i + 1}");
                }
                int count;
                lock (_session.Gate) count = _session.World.Events.Chats.Count;
                _chatPane.PageUp(count, Rendering.ChatRenderer.MaxVisible, _nowSeconds);
                Console.WriteLine("[inflight-proof] chat paged back (older)");
                SayInflightShot("chat paged back");
                break;
            case 2:
                // Cargo selection cycle (NextCargo semantics) — with a populated
                // hold this highlights the selected cargo part in the HUD readout.
                CycleSelectedCargo(next: true);
                SayInflightShot("cargo selection");
                break;
            case 3:
                // Help overlay (H) — shows every new P626 command + the two new
                // help categories (INVENTORY / CARGO, VOTE / COMMAND). Left ON
                // so the remaining autoshots capture it.
                _chatPane.ScrollToBottom();
                _helpVisible = true;
                // P757 — the H overlay now opens the manual; jump straight to the
                // Controls topic so this proof still captures the full key
                // reference columns (the P626 commands + help categories).
                _helpManual = new Rendering.HelpManualModel();
                _helpManual.GoTo("hlpControls");
                Console.WriteLine("[inflight-proof] help overlay ON (stays on)");
                SayInflightShot("help overlay (H)");
                break;
            case 4:
                _inflightProofDone = true;
                Console.WriteLine("[inflight-proof] done (help overlay left visible)");
                break;
        }
    }

    private void SayInflightShot(string what)
    {
        _screenshotRequested = true;
        Console.WriteLine($"[inflight-proof] shot: {what}");
    }
}
