using Allegiance.Core.Sim;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly List<Buoy.Visual> _drawCommandBuoys = new();

    // Shared scene pass: Classic and Modern use the same GetVisibleF snapshot.
    // Igc/buoyigc.h:67-69; buoyigc.cpp:39-53 (white nondirectional decal,
    // visible radius 20, hidden radius 2, rotation .5 rad/s).
    private void DrawTrainingBuoys(short sector, Vector3 cameraEye, Matrix view, Matrix projection)
    {
        if (_embeddedHost is not { } host) return;
        host.CopyVisibleBuoys(sector, _drawCommandBuoys);
        if (_drawCommandBuoys.Count == 0) return;
        var texture = _projectileSpriteRenderer.TextureFor("buoy");
        if (texture is null) return;
        foreach (var buoy in _drawCommandBuoys)
            _projectileSpriteRenderer.DrawBillboard(PoseConverter.ToXna(buoy.Position) - cameraEye, texture,
                buoy.Radius, (float)(_gameSeconds * .5), Vector3.One, view, projection);
    }
}
