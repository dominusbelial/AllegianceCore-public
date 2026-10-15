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
    /// <summary>Native mineFireIGC (igc.h:831).</summary>
    public const int MineFire = 0x20000;
    public const int CoastButton = 8;          // 2*selectedWeaponTwoIGC  ("vector lock")
    public const int BackwardButton = 16;      // 2*coastButtonIGC   thrust back
    public const int ForwardButton = 32;       // 4*coastButtonIGC   thrust forward
    public const int LeftButton = 64;          // 8*coastButtonIGC   thrust left
    public const int RightButton = 128;        // 16*coastButtonIGC  thrust right
    public const int UpButton = 256;           // 32*coastButtonIGC  thrust up
    public const int DownButton = 512;         // 64*coastButtonIGC  thrust down
    public const int AfterburnerButton = 1024; // 128*coastButtonIGC with afterburners

    /// <summary><c>cloakActiveIGC = 512·coastButtonIGC</c> (igc.h:820), "Activating
    /// the cloak" = 2048 (0x800). In the LOW WORD (inside <c>buttonsMaskIGC =
    /// 4095·coastButtonIGC = 0x3FFC</c>), so it is transmitted as part of the
    /// compact <c>stateM</c> on the ship-status broadcasts — the native
    /// mechanism by which a cloaking ship's state reaches observers. Consistent
    /// with this file's AfterburnerButton (1024) and MiningMask (8192), which
    /// also carry native values; the cloak bit slots exactly between them in
    /// the header's order (afterburner → cloak → droneRip → mining, igc.h:814-822).
    /// Driven by <see cref="Ship.UpdateCloak"/>, which derives activation from
    /// this bit like every other toggled control in <see cref="Ship.OnUpdate"/>.
    /// </summary>
    public const int CloakActive = 0x800;    // 512*coastButtonIGC = 2048 (igc.h:820)

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
    /// <summary><c>miningMaskIGC = 2048·coastButtonIGC</c> (igc.h:822,
    /// coastButtonIGC=4) — the "play mine effect" state bit. In the LOW WORD
    /// (inside <c>buttonsMaskIGC = 0x3FFC</c>), so it is transmitted as part of
    /// the compact <c>stateM</c> on the ship status broadcasts — this is the
    /// native mechanism by which a mining ship's state reaches observers (see
    /// <c>shipIGC.cpp:2206-2256</c>, where the mining state machine keeps this
    /// bit set while <c>turnToFace</c> orients the ship). The port stamps it on
    /// the own ship's <c>ServerShipUpdate</c> while its <c>MiningAsteroid</c>
    /// is set (P255); see <c>Wire.OwnShipMiningUpdate</c>.</summary>
    public const int MiningMask = 2048 * CoastButton;          // 8192 = 0x2000
    /// <summary><c>oneWeaponIGC = 2·miningMaskIGC</c> (igc.h:825).</summary>
    public const int OneWeapon = 8192 * 2;                     // 16384
    /// <summary><c>allWeaponsIGC = 2·oneWeaponIGC</c> (igc.h:826) — "every gun fires".</summary>
    public const int AllWeapons = OneWeapon * 2;               // 32768
    /// <summary><c>weaponsMaskIGC = 3·oneWeaponIGC</c> (igc.h:827) — the two weapon buttons (fixed turret guns use <c>weaponsMaskIGC</c>).</summary>
    public const int WeaponsMask = 3 * OneWeapon;

    /// <summary>
    /// P226 — <c>missileFireIGC = 0x10000</c> (igc.h:830), "trying to shoot
    /// missiles" — the next free power-of-two after the weapon-selection slice
    /// (<c>allWeaponsIGC = 0x8000</c>). It heads the enum's HIGH WORD:
    /// <c>mineFireIGC = 2·missileFireIGC = 0x20000</c> (igc.h:831) and
    /// <c>chaffFireIGC = 2·mineFireIGC = 0x40000</c> (igc.h:832) follow it.
    /// <b>Wire caveat (why the player path uses the FIRE_MISSILE message, not
    /// this bit):</b> the header annotates the high word "these are not
    /// transmitted as part of the state mask" (igc.h:829), and the wire
    /// <c>stateM</c> slot is a 16-bit <c>CompactState</c>
    /// (<c>ClientShipUpdate</c>, igc.h:2457-2465) — so 0x10000 cannot cross
    /// the network at all. Native therefore launches a PLAYER's missile via an
    /// inbound <c>FM_CS_FIRE_MISSILE(70)</c> message instead
    /// (<c>FedSrv.CPP:5413-5507</c>), which is the path P226 wires; this bit
    /// is kept as the semantic marker that <see cref="Ship.OnUpdate"/>'s
    /// held/auto-fire leg (the <c>magazineigc.cpp:111</c> NPC-style path)
    /// reads, so a caller that places the bit in <c>StateM</c> locally
    /// (an NPC driver, or a test) arms it. Real player ships reach firing
    /// through <c>HandleFireMissile</c>, not this bit.
    /// </summary>
    public const int MissileFire = 0x10000;
}
