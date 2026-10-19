using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Flight;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private IReadOnlyList<LastBaseHudView> _lastBaseHud = [];

    private void ComputeLastBaseHud(Matrix view, Matrix proj, Vector3 cameraEye)
    {
        lock (_session.Gate)
        {
            var world = _session.World;
            var views = new List<LastBaseHudView>();
            foreach (var objective in world.LastBaseObjectives(LocalSpectating || SpectatorCameraActive))
            {
                var target = objective.Target;
                var position = new Allegiance.Core.Collision.Vector3(target.X, target.Y, target.Z);
                string marker = target.Name;
                // Sector coordinates are local. Guide through a real next-hop aleph until arrival,
                // instead of pointing at an unrelated coordinate in the current sector.
                if (world.MyShipId is not { } me || !world.Ships.TryGetValue(me, out var own)) continue;
                bool reachable = true;
                if (own.SectorId != target.ClusterId)
                {
                    reachable = TryResolveClusterWaypoint(world, target.ClusterId, out position);
                    marker = $"To {target.Name} (aleph)";
                }
                TargetSelectionView? bracket = null;
                Vector2? bearing = null;
                if (reachable)
                {
                    var point = PoseConverter.ToXna(position);
                    if (ProjectToScreen(point, view, proj, cameraEye, out var screen))
                        bracket = new TargetSelectionView(screen, false, marker, IsEnemy: !objective.Defending);
                    var rel = point - cameraEye;
                    var clip = Vector4.Transform(new Vector4(rel, 0), view * proj);
                    var dir = MathF.Abs(clip.W) > 1e-6f ? new Vector2(clip.X / clip.W, -clip.Y / clip.W) : Vector2.UnitY;
                    if (dir.LengthSquared() < 1e-6f) dir = Vector2.UnitY;
                    dir.Normalize();
                    bearing = dir;
                }
                views.Add(new(objective.Text, bracket, bearing, objective.Defending ? "DEFEND" : "ATTACK"));
            }
            _lastBaseHud = views;
        }
    }
}
