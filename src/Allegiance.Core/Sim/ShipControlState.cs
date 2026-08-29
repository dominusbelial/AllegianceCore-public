using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>
/// Button/state bits, ported from the <c>ShipControlStateIGC</c> enum
/// (<c>src/Igc/igc.h:803-835</c>). Only the movement-relevant bits are
/// declared here; the value arithmetic is copied verbatim from the header so
/// the masks match. Stored in <see cref="Ship.StateM"/>.
/// </summary>
public static class ShipControlState
{
    public const int CoastButton = 8;          // 2*selectedWeaponTwoIGC  ("vector lock")
    public const int BackwardButton = 16;      // 2*coastButtonIGC   thrust back
    public const int ForwardButton = 32;       // 4*coastButtonIGC   thrust forward
    public const int LeftButton = 64;          // 8*coastButtonIGC   thrust left
    public const int RightButton = 128;        // 16*coastButtonIGC  thrust right
    public const int UpButton = 256;           // 32*coastButtonIGC  thrust up
    public const int DownButton = 512;         // 64*coastButtonIGC  thrust down
    public const int AfterburnerButton = 1024; // 128*coastButtonIGC with afterburners

    /// <summary>All six manual-thrust buttons (<c>left|right|up|down|forward|backward</c>), the gate for the manual-control branch (<c>shipIGC.cpp:2965</c>).</summary>
    public const int MoveButtonsMask = LeftButton | RightButton | UpButton | DownButton | ForwardButton | BackwardButton;
}
