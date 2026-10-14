using Microsoft.Xna.Framework;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Native WinTrek/WinTrek.cpp:73-78,7424-7544,8469-8604.
/// Uses the native XY sector plane; camera-relative view per P612b.</summary>
public sealed class CommandCamera
{
    public const float MinDistance = 50f, MaxDistance = 20000f, DefaultDistance = 6000f;
    public Vector3 Focus { get; private set; }
    public Vector3 Displacement { get; private set; }
    public float Distance { get; private set; } = DefaultDistance;
    public float Roll { get; private set; }
    public bool TopDown { get; set; }
    public bool Tracking { get; set; }
    private Vector3 BaseBackward => TopDown ? Vector3.UnitZ : new Vector3(0, -MathF.Sqrt(.5f), MathF.Sqrt(.5f));
    private Vector3 BaseUp => TopDown ? Vector3.UnitY : new Vector3(0, MathF.Sqrt(.5f), MathF.Sqrt(.5f));
    private Vector3 Backward => Vector3.Transform(BaseBackward, Matrix.CreateRotationZ(Roll));
    public Vector3 Up => Vector3.Transform(BaseUp, Matrix.CreateRotationZ(Roll));
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Up, Backward));
    public Vector3 Eye => Focus + Backward * Distance;
    public Matrix View() => Matrix.CreateLookAt(Vector3.Zero, -Backward, Up);
    public (Matrix View, Matrix Projection, Vector3 Eye) Matrices(float aspect) =>
        (View(), Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50), aspect, 10, 80000), Eye);

    public void Center(Vector3 position, float radius, bool explicitPosition = false)
    {
        Focus = position;
        Displacement = Vector3.Zero;
        Distance = explicitPosition ? MaxDistance / 2 : MathF.Max(Distance, 5 + 2 * radius);
        ClampFocus();
    }
    public void Follow(Vector3 position, float radius)
    {
        if (!Tracking) return;
        Focus = position + Displacement;
        Distance = MathF.Max(Distance, 5 + 2 * radius);
        ClampFocus();
    }
    public void Step(float dt, float right, float down, float zoom, float roll)
    {
        float delta = MathF.Max(0, dt) * Distance;
        var flatUp = new Vector3(Up.X, Up.Y, 0);
        if (flatUp.LengthSquared() > 1e-8f) flatUp.Normalize();
        var movement = Right * (right * delta) - flatUp * (down * delta);
        Focus += movement;
        Displacement += movement;
        Distance = Math.Clamp(Distance + zoom * delta * 2, MinDistance, MaxDistance);
        Roll = MathHelper.WrapAngle(Roll + roll * MathF.Max(0, dt));
        ClampFocus();
    }
    // Native WinTrek.cpp:10469-10488: 0.1s step times distance times 2.
    public void ZoomWheel(float notches) =>
        Distance = Math.Clamp(Distance * (notches >= 0 ? MathF.Pow(.8f, notches) : MathF.Pow(1.2f, -notches)), MinDistance, MaxDistance);

    private void ClampFocus()
    {
        if (Focus.LengthSquared() > 400000000) Focus = Vector3.Normalize(Focus) * 20000;
    }

    // WinTrek.cpp:7505-7513: track the source ship, same cluster and visible.
    // Modern commander follow is restricted to the commanded teammate and
    // actual known pose; it never creates a synthetic pose for an unseen ship.
    public static ShipEntity? FollowSubject(ClientWorld world, short id, short sector, bool sameSideOnly = true)
    {
        if (!world.Ships.TryGetValue(id, out var ship) || (sameSideOnly && ship.SideId != world.MySideId)
            || ship.Unknown || ship.IsNotVisible) return null;
        if (ship.ParentId >= 0)
        {
            if (!world.Ships.TryGetValue(ship.ParentId, out var carrier)) return null;
            ship = carrier;
        }
        return (!sameSideOnly || ship.SideId == world.MySideId) && ship.SectorId == sector && !ship.Unknown && !ship.IsNotVisible && ship.Latest is not null ? ship : null;
    }
}
