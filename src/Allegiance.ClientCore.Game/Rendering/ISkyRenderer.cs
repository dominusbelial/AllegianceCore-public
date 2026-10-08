using Allegiance.Core;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P577 — the common surface of the per-sector sky renderers so the aleph
/// destination-glimpse (P576-B) can render either tier. Both the native tier
/// (<see cref="NativeSkyRenderer"/>, seeded stars + native dome + native
/// planet) and the HD tier (<see cref="HdSkyRenderer"/>, NASA star cubemap +
/// HD nebula + HD planet) switch sky on <see cref="SetCluster"/> and draw it
/// camera-centred behind everything on <see cref="Draw"/>.
/// </summary>
public interface ISkyRenderer : IDisposable
{
    /// <summary>Switch the sky to a cluster's data (called on sector change).</summary>
    void SetCluster(DataClusterIGC cluster);

    /// <summary>Draw the sky behind everything for the flight camera.</summary>
    void Draw(Matrix view, Matrix projection, Vector3 cameraEye);
}
