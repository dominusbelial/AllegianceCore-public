using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// HUD-20 item 5 — once drones receive PLAYERINFO, the rig's bot driver can no
/// longer identify a drone by "Player is null". Native tells them apart with
/// PLAYERINFO's <c>dtidDrone</c> (NA = human;
/// <c>FedSrv/FedSrv.CPP:2088-2091</c>). This is the wire loopback: encode a
/// human and a drone PLAYERINFO, decode them through
/// <see cref="ClientWorld.Apply(PlayerInfo)"/> and assert
/// <see cref="BotManager.IsDrone"/> classifies each correctly.
/// </summary>
public class Hud20DroneDetectionTests
{
    private static ShipEntity Receive(ClientWorld world, PlayerInfo info)
    {
        // Full codec loop: the same bytes the server puts on the wire come back
        // through the decoder the client uses.
        var decoded = PlayerInfo.Decode(info.Encode());
        world.Apply(decoded);
        return world.Ships[decoded.ShipId];
    }

    [Fact]
    public void Drone_and_human_both_with_playerinfo_are_classified_by_dtidDrone()
    {
        var world = new ClientWorld();
        var human = Receive(world, new PlayerInfo
        {
            ShipId = 61, ISide = 0, CharacterName = "pilot",
            DtidDrone = PlayerInfo.NA, PilotType = 10,
        });
        var drone = Receive(world, new PlayerInfo
        {
            ShipId = 62, ISide = 0, CharacterName = "drone",
            DtidDrone = 40, PilotType = 0,
        });

        Assert.False(BotManager.IsDrone(human), "a PLAYERINFO with dtidDrone == NA is a human");
        Assert.True(BotManager.IsDrone(drone), "a PLAYERINFO with a real drone type id is a drone");
    }

    [Fact]
    public void A_ship_without_playerinfo_yet_still_classifies_as_a_drone()
    {
        // Preserves today's rig behaviour for the window before the seed lands.
        var world = new ClientWorld();
        world.Apply(new ExportShip { ShipId = 63, HullId = 15, SideId = 0, CharacterName = "pre-seed" });
        Assert.True(BotManager.IsDrone(world.Ships[63]));
    }
}
