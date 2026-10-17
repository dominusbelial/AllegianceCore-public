using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests.Wire;

public sealed class P713LobbyWireTests
{
    [Fact]
    public void Mission_definition_leaders_use_native_short_array_without_touching_neighbor_fields()
    {
        short[] leaders = [-1, 8100, 8101, -1, 123, 456];
        var bytes = new MissionDef { RgShipIdLeaders = leaders, Stage = 1, ISideMissionOwner = -1 }.Encode();
        Assert.Equal(864, bytes.Length);
        for (int i = 0; i < leaders.Length; i++)
            Assert.Equal(leaders[i], BitConverter.ToInt16(bytes, 814 + 2 * i));
        Assert.Equal(0, bytes[813]); // final station-id byte
        Assert.Equal(0, bytes[826]); // first player count
        Assert.Equal(leaders, MissionDef.Decode(bytes).RgShipIdLeaders);
        // Read an independently authored native-shaped fixture, not an encoder round trip.
        bytes[814] = 0x34; bytes[815] = 0x12;
        Assert.Equal(0x1234, MissionDef.Decode(bytes).RgShipIdLeaders[0]);
    }
}
