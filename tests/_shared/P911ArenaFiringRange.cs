using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;

namespace Allegiance.TestInfra;

// A controlled range, not an autonomous player or a difficulty benchmark.
// Only aim, placement and the existing RepairRearm action are supplied here.
// Every shot, hit, damage event, death and respawn remains simulation-owned.
public static class P911ArenaFiringRange
{
    public static bool PrepareFrame(TrainingHost host)
    {
        var player = host.PlayerShip;
        player.Position = new Vector3(0, 0, 1000); player.Velocity = Vector3.Zero;
        var enemies = host.Drones.Values.Where(s => !s.Dead && s.SideId == 1 && s.PilotType == 2).ToArray();
        for (int i = 0; i < enemies.Length; i++)
        {
            var enemy = enemies[i];
            if (enemy.Sector != player.Sector)
            { enemy.Sector?.Remove(enemy); host.Mission.PlaceShip(enemy, player.Sector!); }
            enemy.Position = new Vector3(300, i * 300, 1000); enemy.Velocity = Vector3.Zero;
            var away = Orientation.Identity; away.Set(new Vector3(1, 0, 0), new Vector3(0, 0, 1));
            enemy.SetOrientation(away);
        }
        var orientation = Orientation.Identity; orientation.Set(new Vector3(1, 0, 0), new Vector3(0, 0, 1));
        player.SetOrientation(orientation);
        bool resupply = player.Ammo < 5;
        if (resupply) host.RepairRearm(player.Id);
        player.Controls = ControlData.Neutral;
        player.StateM = ShipControlState.AllWeapons | ShipControlState.LeftButton;
        return resupply;
    }
}
