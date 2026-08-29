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

    // ── weapon-selection bits (ShipControlStateIGC, igc.h:803-827) ─────────
    // The movement slice declared only the movement-relevant bits above; the
    // weapon slice adds the weapon-selection half so CweaponIGC's
    // fIsWeaponSelected (weaponIGC.h:236-254) can be ported faithfully. Value
    // arithmetic copied verbatim: coastButtonIGC=4 → miningMaskIGC=8192 →
    // oneWeaponIGC=16384 → allWeaponsIGC=32768; selectedWeaponMask = 3.
    /// <summary><c>selectedWeaponOneIGC</c> (igc.h:805).</summary>
    public const int SelectedWeaponOne = 1;
    /// <summary><c>selectedWeaponTwoIGC</c> (igc.h:806).</summary>
    public const int SelectedWeaponTwo = 2;
    /// <summary><c>selectedWeaponMaskIGC = (2·selectedWeaponTwo) − 1</c> (igc.h:807) — the two selection bits.</summary>
    public const int SelectedWeaponMask = SelectedWeaponTwo * 2 - 1;
    /// <summary><c>selectedWeaponShiftIGC = 0</c> (igc.h:808).</summary>
    public const int SelectedWeaponShift = 0;
    /// <summary><c>oneWeaponIGC = 2·miningMaskIGC</c> (igc.h:825).</summary>
    public const int OneWeapon = 8192 * 2;                     // 16384
    /// <summary><c>allWeaponsIGC = 2·oneWeaponIGC</c> (igc.h:826) — "every gun fires".</summary>
    public const int AllWeapons = OneWeapon * 2;               // 32768
    /// <summary><c>weaponsMaskIGC = 3·oneWeaponIGC</c> (igc.h:827) — the two weapon buttons (fixed turret guns use <c>weaponsMaskIGC</c>).</summary>
    public const int WeaponsMask = 3 * OneWeapon;
}
