using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P1041 — scripted Options-screen proof (<c>ALLEGIANCE_P1041_PROOF=1</c>): opens the
/// in-game Graphics/HUD panel on the Jet variant row and captures REAL backbuffer
/// frames (green, amber, green again) so the selector's row, its neighbour skin row
/// and the palette swap are evidence rather than an assertion. The panel is drawn in
/// <c>EndDraw</c>, AFTER the ordinary autoshot flush in <c>Draw</c>, so this proof latches
/// the shot and flushes it from <see cref="FlushP1041ProofShot"/> once the panel is
/// really on the backbuffer (the same mechanism as the P948 mouse-row proof).
/// <para>Presentation only: it touches the panel cursor, the queued palette request and
/// the capture latch. It changes no simulated value.</para>
/// </summary>
public sealed partial class Game1
{
    private bool _p1041ProofStarted;
    private bool _p1041ProofDone;
    private bool _p1041ShotPending;
    private double _p1041ProofElapsed;
    private double _p1041ProofAirborne;
    private double _p1041ProofWait;
    private int _p1041ProofStep;

    private void RunP1041Proof(double dtSec)
    {
        if (_p1041ProofDone) return;
        // Only a real flight frame counts (the panel is drawn in flight).
        _p1041ProofAirborne = _state == ClientState.Flying && !_dockedActive ? _p1041ProofAirborne + dtSec : 0;
        _p1041ProofWait += dtSec;
        if (!_p1041ProofStarted && _p1041ProofAirborne < 1.0 && _p1041ProofWait < 100.0) return;
        _p1041ProofElapsed += dtSec;

        void Shot(string name)
        {
            _autoShotName = "p1041_" + name;
            _p1041ShotPending = true;
            Console.WriteLine($"[p1041-proof] shot {_autoShotName}");
        }

        if (!_p1041ProofStarted)
        {
            _p1041ProofStarted = true;
            _p1041ProofElapsed = 0;
            _graphicsSettingsVisible = true;
            _graphicsFirstRow = 0;
            bool paletteRow = JetPaletteRowVisible();
            // The variant row (7) with its skin neighbour (6); without it the skin row.
            _graphicsRow = paletteRow ? GraphicsRowPalette : GraphicsRowFor(6);
            Shot(paletteRow
                ? $"options_{ActiveJetPalette()}_skin"
                : $"options_{_hudSkins!.Active.Renderer.Id}_off");
            Console.WriteLine($"[p1041-proof] panel open: active skin '{_hudSkins!.Active.Renderer.Id}', "
                + $"palette '{ActiveJetPalette() ?? "n/a"}', variant row {(paletteRow ? "visible" : "hidden")}, "
                + $"cursor {_graphicsRow} of {PanelRowCount} rows");
            _p1041ProofStep = paletteRow ? 1 : 4;
            return;
        }

        switch (_p1041ProofStep)
        {
            case 1:
                if (_p1041ProofElapsed > 2.0)
                {
                    // Right on the variant row: queued, published by CommitHudSkinChange
                    // at this frame's update boundary, then saved and announced. The shot
                    // waits one frame so the applied panel AND the toast are on screen.
                    _graphicsRow = GraphicsRowPalette;
                    UpdateGraphicsSettings(new KeyboardState(Keys.Right));
                    _graphicsPrevious = new KeyboardState(Keys.Right);
                    _p1041ProofElapsed = 0;
                    _p1041ProofStep = 5;
                }
                break;
            case 5:
                if (_p1041ProofElapsed > 0.4)
                {
                    Shot($"options_{ActiveJetPalette()}_toast");
                    Console.WriteLine($"[p1041-proof] after Right (applied): palette '{ActiveJetPalette()}', "
                        + $"toast '{_hudSkinNotice}'");
                    _p1041ProofElapsed = 0;
                    _p1041ProofStep = 6;
                }
                break;
            case 6:
                if (_p1041ProofElapsed > 2.0)
                {
                    _graphicsRow = GraphicsRowPalette;
                    UpdateGraphicsSettings(new KeyboardState(Keys.Left));
                    _graphicsPrevious = new KeyboardState(Keys.Left);
                    _p1041ProofElapsed = 0;
                    _p1041ProofStep = 7;
                }
                break;
            case 7:
                if (_p1041ProofElapsed > 0.4)
                {
                    Shot($"options_{ActiveJetPalette()}_toast2");
                    Console.WriteLine($"[p1041-proof] after Left (applied): palette '{ActiveJetPalette()}', "
                        + $"toast '{_hudSkinNotice}'");
                    _p1041ProofElapsed = 0;
                    _p1041ProofStep = 3;
                }
                break;
            case 3:
                if (_p1041ProofElapsed > 2.0)
                {
                    _graphicsSettingsVisible = false;
                    _p1041ProofDone = true;
                    Console.WriteLine("[p1041-proof] done (three real panel frames queued)");
                }
                break;
            case 4:
                if (_p1041ProofElapsed > 2.0)
                {
                    _graphicsSettingsVisible = false;
                    _p1041ProofDone = true;
                    Console.WriteLine("[p1041-proof] done (one real panel frame queued; no variant row)");
                }
                break;
        }
    }

    /// <summary>Called from <c>EndDraw</c> after the settings panel is drawn: flush the
    /// latched frame so it really contains the panel.</summary>
    private void FlushP1041ProofShot()
    {
        if (!_p1041ShotPending) return;
        _p1041ShotPending = false;
        _autoShotScheduled = true;
        FlushScheduledAutoshot();
    }
}
