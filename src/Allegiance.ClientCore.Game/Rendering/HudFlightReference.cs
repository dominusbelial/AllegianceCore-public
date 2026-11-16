using Allegiance.Core.Sim;
using Xna = Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P1036 — a presentation-only flight-reference snapshot for the Jet skin
/// (P972 §4.2). It carries exactly the data the client already owns for the
/// chase/cockpit cameras and the native velocity indicator, converted once at
/// the model-adapter boundary:
/// <list type="bullet">
/// <item><see cref="Orientation"/> — the own (source) ship's attitude as a
/// quaternion, built from the same <see cref="Orientation"/> basis
/// <c>Game1</c> tracks for the cameras and <c>HudFrame.RollDegrees</c>;</item>
/// <item><see cref="Velocity"/> — the own source ship's world velocity, the
/// same vector native projects for <c>directionbmp</c>
/// (<c>WinTrek/indicator.cpp:130</c>) and the same one the speed readout uses;</item>
/// <item><see cref="SectorPlaneNormal"/> — the sector reference plane's normal.
/// Native's command view draws its grid in the world XY plane at <c>z = 0</c>
/// (<c>WinTrek/cmdview.cpp:17-115</c>, the <c>zGrid</c> the vertices are placed
/// on; <c>WinTrek/WinTrek.cpp:3099</c> constructs it with <c>zGrid = 0.0f</c>)
/// and drops every contact's foot line down to that plane
/// (<c>WinTrek/cmdview.cpp:164</c>: the drop-line colour is chosen by
/// <c>vecPosition.Z() &gt; m_zGrid</c>). That artificial plane — not a planetary
/// horizon — is the only attitude reference Jet uses; the port's
/// <see cref="CommandGridRenderer"/> keeps the same geometry
/// (<c>CommandGridRenderer.cs:35-49</c>).</item>
/// </list>
/// <para><b>Null when unavailable</b> (<see cref="HudContext.FlightReference"/>
/// stays null while docked, dead, in the command view without an own ship, or
/// before the first own-ship update): Jet then draws no ladder and reports the
/// heading as unavailable rather than inventing attitude. The renderer never
/// reads the live world — this record is the whole input.</para>
/// <para>Nothing here changes flight, cameras, wire data or simulation: it is a
/// copy of values that already reach the HUD.</para>
/// </summary>
public readonly record struct HudFlightReference(
    Xna.Quaternion Orientation,
    Xna.Vector3 Velocity,
    Xna.Vector3 SectorPlaneNormal)
{
    /// <summary>Native world-up: the sector plane's normal (<c>WinTrek/WinTrek.cpp:3099</c>
    /// passes <c>zGrid = 0.0f</c> into the XY grid of <c>WinTrek/cmdview.cpp:17-115</c>,
    /// so the plane is <c>z = 0</c> and its normal is world +Z).</summary>
    public static Xna.Vector3 SectorUp => new(0f, 0f, 1f);

    /// <summary>The snapshot's forward axis — the ship's nose in world space
    /// (<see cref="Orientation.GetForward"/>, the negation of the stored
    /// backward row).</summary>
    public Xna.Vector3 Forward => Xna.Vector3.Transform(-Xna.Vector3.UnitZ, Orientation);

    /// <summary>The snapshot's up axis in world space (<see cref="Orientation.GetUp"/>).</summary>
    public Xna.Vector3 Up => Xna.Vector3.Transform(Xna.Vector3.UnitY, Orientation);

    /// <summary>The snapshot's right axis in world space (<see cref="Orientation.GetRight"/>).</summary>
    public Xna.Vector3 Right => Xna.Vector3.Transform(Xna.Vector3.UnitX, Orientation);

    /// <summary>True when the reference carries a usable, essentially unit
    /// attitude quaternion (a stale/zero quaternion is rejected rather than
    /// producing a random heading).</summary>
    public bool HasAttitude
    {
        get
        {
            float lengthSquared = Orientation.LengthSquared();
            return lengthSquared > 0.5f && lengthSquared < 1.5f;
        }
    }

    /// <summary>
    /// Build the snapshot from the sim pose the client already tracks for the
    /// cameras (rows = right, up, backward; <see cref="PoseConverter"/> maps them
    /// to the XNA row-vector matrix). Velocity is converted with
    /// <see cref="PoseConverter.ToXna"/> because the client's render space uses
    /// the sim axes as-is.
    /// </summary>
    public static HudFlightReference FromSim(in Orientation orientation, in SimVec velocity, Xna.Vector3 planeNormal)
    {
        var matrix = PoseConverter.ToXnaRotation(orientation);
        return new HudFlightReference(
            Xna.Quaternion.CreateFromRotationMatrix(matrix),
            PoseConverter.ToXna(velocity),
            planeNormal);
    }
}
