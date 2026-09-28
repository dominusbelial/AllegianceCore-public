using Allegiance.Core.Wire;
using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P416 — pure unit tests for the Promote-Turret seat keys: (1) the outbound
/// wire message encodes the chosen turret mount id exactly (Encode → Decode is
/// byte-faithful, so the mount the pilot's key resolves is the mount the
/// server's <c>HandlePromote</c> promotes at), and (2) the pure seat→mount
/// rule (<see cref="PromoteTurretKey"/>) that maps a seat number (1-based, the
/// key's ordinal) onto the real turret hardpoint on the flown hull, refusing
/// seats the hull doesn't own.
/// </summary>
public sealed class PromoteTurretKeyTests
{
    // ── outbound encode ────────────────────────────────────────────────────

    [Fact]
    public void Outbound_promote_encodes_the_chosen_mount_exactly()
    {
        // A 792-class carrier: turret slots [MaxFixedWeapons=2, MaxWeapons=6).
        short mount = 3;   // seat 2 → mount 2+1
        var encoded = new PromoteRequest { MountidPromoted = mount }.Encode();

        var decoded = PromoteRequest.Decode(encoded);
        Assert.Equal(PromoteRequest.Fmid, decoded.GetFmid());
        // The mount the key resolves is the mount the server promotes at — the
        // whole point of the outbound encode (the P140 handler matches
        // child.TurretId == MountidPromoted).
        Assert.Equal(mount, decoded.MountidPromoted);
    }

    [Fact]
    public void Outbound_encode_roundtrips_the_first_turret_mount()
    {
        var encoded = new PromoteRequest { MountidPromoted = 2 }.Encode();
        var decoded = PromoteRequest.Decode(encoded);
        Assert.Equal((short)2, decoded.MountidPromoted);
    }

    [Fact]
    public void Decode_rejects_a_non_promote_fmid()
    {
        // Decode is keyed to its own fmid and must not silently accept a
        // foreign message (the promote fmid is 190; a BOARD_SHIP is 103).
        var bogus = new BoardShip { SidParent = 7 }.Encode();
        Assert.Throws<ArgumentException>(() => PromoteRequest.Decode(bogus));
    }

    // ── pure seat→mount rule ───────────────────────────────────────────────

    // A turret-capable carrier with MaxFixedWeapons=2, MaxWeapons=6 → turret
    // slots [2,6): seats 1..4 map to mounts 2,3,4,5; a 5th seat is refused.
    private const short MaxFixed = 2;
    private const short MaxWeapons = 6;

    [Fact]
    public void Seat_1_maps_to_the_first_turret_mount()
    {
        // Native: mount = (key - TK_PromoteTurret1) + GetMaxFixedWeapons()
        // (WinTrek.cpp:9591) → seat 1 = the first turret hardpoint = MaxFixedWeapons.
        Assert.Equal((short)2, PromoteTurretKey.MountForSeat(1, MaxFixed, MaxWeapons));
    }

    [Fact]
    public void Seat_N_maps_to_MaxFixedWeapons_plus_N_minus_one()
    {
        Assert.Equal((short)3, PromoteTurretKey.MountForSeat(2, MaxFixed, MaxWeapons));
        Assert.Equal((short)4, PromoteTurretKey.MountForSeat(3, MaxFixed, MaxWeapons));
        Assert.Equal((short)5, PromoteTurretKey.MountForSeat(4, MaxFixed, MaxWeapons));
    }

    [Fact]
    public void Seat_beyond_the_last_turret_is_refused()
    {
        // Four turret slots [2,6) → seat 5 is past the last mount; the caller
        // must send nothing (native's `turret < GetMaxWeapons()` guard).
        Assert.Null(PromoteTurretKey.MountForSeat(5, MaxFixed, MaxWeapons));
    }

    [Fact]
    public void Seat_zero_or_negative_is_refused()
    {
        Assert.Null(PromoteTurretKey.MountForSeat(0, MaxFixed, MaxWeapons));
        Assert.Null(PromoteTurretKey.MountForSeat(-1, MaxFixed, MaxWeapons));
    }

    [Fact]
    public void Hull_with_no_turrets_refuses_every_seat()
    {
        // MaxFixedWeapons == MaxWeapons → no turret range at all (a fighter).
        Assert.Null(PromoteTurretKey.MountForSeat(1, 4, 4));
        Assert.Null(PromoteTurretKey.MountForSeat(2, 4, 4));
    }
}
