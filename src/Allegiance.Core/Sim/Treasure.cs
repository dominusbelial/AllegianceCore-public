using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>A live pickup following the native analytical drift and expiry rules
/// (Igc/treasureIGC.cpp:18,95-116,193-197,215-277).</summary>
public sealed class Treasure : Model
{
    /// <summary>Original trajectory, exported unchanged, with creation time in server milliseconds.
    /// Clients evaluate the same curve; native exports m_data (treasureIGC.cpp:268-277).</summary>
    public DataTreasureIGC Data { get; }
    public Sector OwnerSector { get; }
    public Mission? OwnerMission { get; }
    public double CreatedAt { get; }
    public double ExpireAt { get; }
    public TreasureCode Code => Data.TreasureCode;
    public short Amount => Data.Amount;
    public const float PickupRadius = 5f; // treasureIGC.cpp:193
    public Vector3 Velocity { get; private set; }
    public Vector3 PreviousPosition { get; private set; }
    public override Vector3 CollisionVelocity => _sweepVelocity;
    private Vector3 _sweepVelocity;
    public override bool CollisionStatic => Velocity == Vector3.Zero;

    public Treasure(short id, Sector sector, DataTreasureIGC data, double now, Mission? mission = null)
        : base(id, TreasureMotion.Vector(data.P0))
    {
        OwnerSector = sector ?? throw new ArgumentNullException(nameof(sector));
        OwnerMission = mission;
        CreatedAt = data.CreateNow ? now : data.Time0 / 1000.0;
        Data = data with { Time0 = data.CreateNow ? (uint)(now * 1000) : data.Time0, CreateNow = false };
        ExpireAt = CreatedAt + data.Lifespan;
        Position = TreasureMotion.PositionAt(TreasureMotion.Vector(data.P0), TreasureMotion.Vector(data.V0), now - CreatedAt);
        PreviousPosition = Position;
        Velocity = TreasureMotion.VelocityAt(TreasureMotion.Vector(data.V0), now - CreatedAt);
        _sweepVelocity = Velocity;
        InitClock(now);
    }

    protected override void OnUpdate(double now, double dt)
    {
        if (now >= ExpireAt)
        {
            OwnerSector.Remove(this);
            OwnerMission?.Site.TreasureDestroyedEvent(this);
            return;
        }
        PreviousPosition = Position;
        Position = TreasureMotion.PositionAt(TreasureMotion.Vector(Data.P0), TreasureMotion.Vector(Data.V0), now - CreatedAt);
        Velocity = TreasureMotion.VelocityAt(TreasureMotion.Vector(Data.V0), now - CreatedAt);
        _sweepVelocity = (Position - PreviousPosition) / (float)dt;
    }
}
