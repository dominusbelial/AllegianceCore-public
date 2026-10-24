using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P464 + P517 — the turret-gunner slice of <see cref="Game1"/> (partial class,
/// shares the <c>Game1</c> type so private field access across the split files is
/// unaffected). While the local ship is boarded as a turret gunner this is
/// where aiming and firing happen: it locally integrates the turret orientation
/// from the same look axes flight uses, and emits the
/// <c>ACTIVE_TURRET_UPDATE(99)</c> / <c>INACTIVE_TURRET_UPDATE(100)</c> pair at
/// the control cadence. The flight <c>SHIP_UPDATE</c> send is gated off by the
/// caller in its place.
///
/// <para>P655: ACTIVE_TURRET_UPDATE uses uint server milliseconds, as native
/// clintlib/clintlib.cpp:1892 and Igc/shipIGC.h:1206-1212 do. The shared
/// PING-fed clock already estimates those units; the server converts to seconds
/// for its ±1.5s replay window and enforces monotonic timestamps.</para>
/// </summary>
public sealed partial class Game1
{
    /// <summary>The locally-integrated turret facing (world frame), used both to
    /// drive the turret camera/view and as the reported orientation in
    /// <c>ACTIVE_TURRET_UPDATE</c>. Integrated by <see cref="Flight.TurretLook"/>
    /// — the pure extraction of the server's <c>ExecuteTurretMove</c>.</summary>
    private Orientation _turretOrientation = new();

    /// <summary>Whether <see cref="_turretOrientation"/> has been seeded for the
    /// current gunning episode. Cleared the moment the player leaves the turret,
    /// so a fresh board re-seeds from the (possibly changed) carrier facing.</summary>
    private bool _turretSeeded;

    /// <summary>P464 — the control-cadence send state for the turret loop
    /// (native <c>c_dsecUpdateClient</c> = 0.15 s); see
    /// <see cref="Flight.TurretGunnerCadence"/>.</summary>
    private readonly Flight.TurretGunnerCadence _turretCadence = new();

    /// <summary>P803 — whether the local gunner is holding fire THIS frame (the
    /// same <c>stateM &amp; weaponsMaskIGC</c> trigger that drives the
    /// ACTIVE/INACTIVE send branch below). Native's <c>PartWrapper::IsActive</c>
    /// (<c>consoledata.cpp:1122</c>, <c>m_ppart-&gt;fActive()</c>) reads the
    /// weapon's firing state, and the turret weapon's is exactly this trigger
    /// (<c>clintlib.cpp:1870-1905</c>); the F4 equipment pane reads it while the
    /// gunner is boarded. Reset whenever the gunner loop does not run.</summary>
    private bool _turretFiring;

    /// <summary>
    /// Wire timestamp from the shared PING-fed clock, in milliseconds.
    /// </summary>
    private uint TurretServerTimeMilliseconds()
    {
        return _ownServerClock.ServerTimeMs(Environment.TickCount64);
    }

    /// <summary>
    /// P464 + P517 — the per-frame turret-gunner drive, called from
    /// <see cref="FlightInput.UpdateFlying"/> in place of the flight
    /// <c>CommandControls</c> send whenever the local ship is boarded as a
    /// gunner. Three responsibilities:
    /// <list type="number">
    /// <item><b>Aim</b> — integrate <see cref="_turretOrientation"/> forward by
    /// this frame's delta under the same look axes flight uses
    /// (<paramref name="lookAxes"/>), via <see cref="Flight.TurretLook.Integrate"/>
    /// (the pure <c>ExecuteTurretMove</c> extraction, so turn-rate/arc limits
    /// match the server).</item>
    /// <item><b>Send</b> — at the control cadence (native
    /// <c>c_dsecUpdateClient</c> = 0.15 s), send ACTIVE while the fire button is
    /// held, INACTIVE otherwise — the exact branch native's
    /// <c>BaseClient::SendUpdate</c> makes for a parented (boarded) ship
    /// (<c>clintlib.cpp:1870-1905</c>). An ACTIVE's <c>time</c> comes from the
    /// shared PING-fed server clock (P517), never the tick-index pose slot.</item>
    /// </list>
    /// Returns the fire-<c>stateM</c> contribution for the render/echo pass —
    /// always 0 for a gunner, because the P462 local own-shot echo covers
    /// pilots only (gunner rounds render solely from the server broadcast; out
    /// of scope here).
    /// </summary>
    /// <param name="lookAxes">This frame's commanded control axes (keyboard +
    /// mouse virtual-joystick + joystick), reused as the turret look. Throttle
    /// is overridden to +1 so the aim slew runs at full rate regardless of the
    /// flight throttle.</param>
    /// <param name="flightStateM">This frame's flight <c>stateM</c>; its
    /// <see cref="ShipControlState.WeaponsMask"/> bits are the fire trigger
    /// (Space / joystick fire fold in here), matching native's
    /// <c>stateM &amp; weaponsMaskIGC</c> selection.</param>
    /// <param name="dt">Frame delta, seconds.</param>
    private int UpdateTurretGunner(ControlData lookAxes, int flightStateM, float dt)
    {
        // 1 — seed the local aim once per gunning episode, then integrate.
        // (The server clock needs no per-frame feed here: the shared
        // _ownServerClock is fed from FM_CS_PING echoes in PumpServerClock,
        // which the caller runs every frame — P517.)
        if (!_turretSeeded)
        {
            _turretOrientation = TurretSeedOrientation();
            // P803 proof harness: --auto-turret-yaw DEG swings the turret off the
            // carrier's axis so its tracers cross the screen instead of receding
            // straight down the line of sight.
            if (_autoPlan.AutoTurretYawDegrees != 0f)
                _turretOrientation.Yaw(_autoPlan.AutoTurretYawDegrees * MathF.PI / 180f);
            _turretSeeded = true;
            Console.WriteLine($"[monogame] turret gunner: aim seeded {_turretOrientation.GetForward()}");
        }
        var turretControls = lookAxes with { Throttle = 1f };
        Flight.TurretLook.Integrate(_turretOrientation, turretControls, dt);

        // 2 — the control-cadence send branch (native clintlib.cpp:1870-1905),
        // driven by the pure TurretGunnerCadence (shared with the live test).
        bool fireHeld = (flightStateM & ShipControlState.WeaponsMask) != 0
            // P803 proof harness: --auto-gunner-fire holds the trigger once the
            // carrier is in space (the auto-proof launch clock has started).
            || (_autoPlan.AutoGunnerFire && _auto is { SecondsSinceLaunch: > 3.0 });
        _turretFiring = fireHeld;   // P803 — the F4 pane's IsActive for the assigned weapon
        switch (_turretCadence.Update(_nowSeconds, fireHeld, _ownServerClock.Ready))
        {
            case Flight.TurretSendAction.Active:
            {
                uint t = TurretServerTimeMilliseconds();
                var atu = ClientActiveTurretUpdate.From(t, turretControls, _turretOrientation);
                _session.SendActiveTurret(t, atu);
                Console.WriteLine($"[monogame] turret fire -> ACTIVE_TURRET_UPDATE(99) time={t} " +
                                  $"fw={_turretOrientation.GetForward()} ctrl={turretControls}");
                break;
            }
            case Flight.TurretSendAction.Inactive:
                _session.SendInactiveTurret();
                if (!fireHeld && _ownServerClock.Ready)
                    Console.WriteLine("[monogame] turret idle -> INACTIVE_TURRET_UPDATE(100)");
                break;
        }
        return 0;
    }

    /// <summary>
    /// P464 — the initial facing a freshly-seated gunner aims from, before any
    /// input. The port does not transmit the turret's resting facing, so the
    /// best available stand-in is the gunner's own last-known ship orientation
    /// (its <c>Latest</c> pose); the player immediately corrects it with look
    /// input, and the server is authoritative on the applied facing regardless.
    /// </summary>
    private Orientation TurretSeedOrientation()
    {
        var world = _session.World;
        lock (_session.Gate)
        {
            if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own) && own.Latest is { } up)
                return up.Orientation.Copy();
        }
        return new Orientation();
    }
}
