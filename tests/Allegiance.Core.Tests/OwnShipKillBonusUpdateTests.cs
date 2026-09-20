using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P298 — <see cref="OwnShipKillBonusUpdate"/>(212), the owner-only per-tick
/// stream carrying the local ship's live Kill Bonus (P278) to the renderer-only
/// MonoGame client (native replicated the same value via PLAYERINFO.fExperience
/// to a local ship replica, appmsg.cpp:2280; this client has no replica, hence
/// the stream — see the message doc). Pure tests: exact wire layout + a
/// real-ship <see cref="OwnShipKillBonusUpdate.FromShip"/> derivation. These run
/// in parallel (nothing here boots a server or opens a socket).
/// </summary>
public class OwnShipKillBonusUpdateTests
{
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        var file = IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    });

    private static Ship NewShip()
    {
        var hull = RepresentativeCore.Value.HullTypes.Values
            .OrderByDescending(h => h.HitPoints).First();
        return new Ship(9900, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
    }

    /// <summary>⭐ Exact 10-byte wire layout: header(4) + float percent @4 +
    /// short streak @8, cbmsg = 10, fmid = 212.</summary>
    [Fact]
    public void Encode_produces_exact_wire_layout()
    {
        var msg = new OwnShipKillBonusUpdate { KillBonusPercent = 16.666666f, Streak = 2 };
        byte[] wire = msg.Encode();

        Assert.Equal(OwnShipKillBonusUpdate.FixedSize, wire.Length);
        Assert.Equal(10, wire.Length);
        Assert.Equal((ushort)10, BitConverter.ToUInt16(wire, 0));               // cbmsg
        Assert.Equal((ushort)212, BitConverter.ToUInt16(wire, 2));              // fmid
        Assert.Equal(16.666666f, BitConverter.ToSingle(wire, 4), 3);            // percent @4
        Assert.Equal((short)2, BitConverter.ToInt16(wire, 8));                  // streak @8
    }

    /// <summary>⭐ Encode/Decode round-trips both fields exactly.</summary>
    [Fact]
    public void Decode_round_trips_values()
    {
        var msg = new OwnShipKillBonusUpdate { KillBonusPercent = 34.615385f, Streak = 9 };
        var dec = OwnShipKillBonusUpdate.Decode(msg.Encode());

        Assert.Equal(34.615385f, dec.KillBonusPercent, 3);
        Assert.Equal((short)9, dec.Streak);
    }

    /// <summary>⭐ Decode rejects a wrong fmid / wrong length (guards the wire
    /// contract like every sibling codec).</summary>
    [Fact]
    public void Decode_rejects_wrong_fmid_or_length()
    {
        byte[] wire = new OwnShipKillBonusUpdate { KillBonusPercent = 10f, Streak = 1 }.Encode();

        // Wrong fmid (copy, then scribble a different one).
        var tampered = (byte[])wire.Clone();
        tampered[2] = 0; tampered[3] = 0;
        Assert.Throws<ArgumentException>(() => OwnShipKillBonusUpdate.Decode(tampered));

        Assert.Throws<ArgumentException>(() => OwnShipKillBonusUpdate.Decode(wire.AsSpan(0, 9)));
    }

    /// <summary>⭐ <c>FromShip</c> lifts the REAL P278 values off a live ship: a
    /// 2-kill streak streams percent 16.67 and streak 2; a fresh ship streams 0
    /// (an honest readout, never NA).</summary>
    [Fact]
    public void FromShip_reflects_real_ship_kill_bonus()
    {
        var ship = NewShip();
        ship.ResetKillBonus();
        Assert.Equal(0f, OwnShipKillBonusUpdate.FromShip(ship).KillBonusPercent);
        Assert.Equal((short)0, OwnShipKillBonusUpdate.FromShip(ship).Streak);

        ship.AddKillBonus();
        ship.AddKillBonus();
        var msg = OwnShipKillBonusUpdate.FromShip(ship);
        Assert.Equal(ship.KillBonusPercent, msg.KillBonusPercent, 3);
        Assert.Equal(16.666666f, msg.KillBonusPercent, 3);
        Assert.Equal((short)2, msg.Streak);
    }
}
