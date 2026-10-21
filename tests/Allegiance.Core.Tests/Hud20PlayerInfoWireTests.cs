using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// HUD-20 — PLAYERINFO(10) now carries the fields native fills in
/// <c>SendPlayerInfo</c>: <c>fExperience</c> at offset 48
/// (<c>FedSrv/FedSrv.CPP:2084</c>, <c>Inc/Messages.h:109</c>) and
/// <c>dtidDrone</c> at offset 58 (<c>FedSrv.CPP:2088-2091</c>: NA = human).
/// The encoder previously wrote literals 0 for both, so a per-target kill bonus
/// and drone detection were impossible.
/// </summary>
public class Hud20PlayerInfoWireTests
{
    [Fact]
    public void Encode_writes_fExperience_at_offset_48_and_round_trips()
    {
        var info = new PlayerInfo
        {
            CharacterName = "Pilot",
            ISide = 3,
            ShipId = 42,
            NKills = 5,
            NDeaths = 2,
            FExperience = 3f,
            DtidDrone = PlayerInfo.NA,
        };

        byte[] bytes = info.Encode();

        // The P31 probe offsets: fExperience @48 (Messages.h:109).
        Assert.Equal(3f, BitConverter.Int32BitsToSingle(BitConverter.ToInt32(bytes, 48)));
        var decoded = PlayerInfo.Decode(bytes);
        Assert.Equal(3f, decoded.FExperience);
        Assert.Equal(PlayerInfo.NA, decoded.DtidDrone);
        Assert.Equal(42, decoded.ShipId);
        Assert.Equal(3, decoded.ISide);
    }

    [Fact]
    public void Encode_round_trips_the_drone_type_id_for_a_drone()
    {
        // Native: NA = human, the drone type id for a drone (FedSrv.CPP:2088-2091).
        var info = new PlayerInfo { ShipId = 7, ISide = 1, DtidDrone = 40, PilotType = Ship_PilotTypeMiner };
        var decoded = PlayerInfo.Decode(info.Encode());
        Assert.Equal((short)40, decoded.DtidDrone);
        Assert.Equal((byte)0, decoded.PilotType);
        Assert.NotEqual(PlayerInfo.NA, decoded.DtidDrone);
    }

    [Fact]
    public void Message_layout_is_unchanged()
    {
        // The field slots were already reserved; HUD-20 only fills them.
        Assert.Equal(68, PlayerInfo.MessageSize);
        Assert.Equal(68, new PlayerInfo().Encode().Length);
    }

    /// <summary><c>c_ptMiner</c> (Igc/igc.h); the port's
    /// <c>Ship.PilotTypeMiner</c> is 0. Kept as a literal so this wire test does
    /// not depend on the sim assembly.</summary>
    private const byte Ship_PilotTypeMiner = 0;
}
