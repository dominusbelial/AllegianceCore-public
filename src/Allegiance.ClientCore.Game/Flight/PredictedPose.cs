using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>A world-space pose (position + orientation) for a ship at render
/// time. Every ship — own and remote alike — is rendered from its raw
/// last-known <c>ServerShipUpdate</c>, no smoothing/extrapolation: an earlier
/// predictor (blend-based smoothing between updates) was tried and made
/// remote ships visibly worse — flashing/teleporting/ghosting, confirmed live
/// with up to 21 ships in flight — than simply rendering the raw 20Hz data,
/// so it was removed rather than kept as unused complexity.</summary>
public readonly record struct PredictedPose(Vector3 Position, Orientation Orientation);
