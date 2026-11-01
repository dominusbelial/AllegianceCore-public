using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Training;

namespace Allegiance.SinglePlayer;

public sealed partial class TrainingHost
{
    // clintlib/clintlib.cpp:2374-2417: offline friendly NPC orders go directly
    // to both slots. Do not use the network server's different default-order policy.
    public bool HandleTrainingDroneChat(ChatMessage chat)
    {
        lock (_simGate)
        {
            var drone = ResolveShip(chat.OidRecipient);
            if (drone is null || drone.Mission is null || drone.PilotType >= Ship.PilotTypePlayer
                || drone.SideId != PlayerShip.SideId || chat.CommandID == OrderChange.CidNone) return false;
            var target = Mission.GetModel((IgcObjectType)chat.OtTarget, chat.OidTarget);
            short command = chat.CommandID;
            if (command == OrderChange.CidDefault && target is not null)
            {
                command = OrderChange.CidGoto;
                if (chat.ChatTarget == (int)ChatTarget.Individual)
                {
                    command = drone.PilotType switch
                    {
                        Ship.PilotTypeMiner when target is Asteroid rock && rock.CanMineHe3 => OrderChange.CidMine,
                        Ship.PilotTypeBuilder when target is Asteroid rock && drone.BuilderStationType is { } type
                            && rock.Data.HasCapability(type.AabmBuild) => OrderChange.CidBuild,
                        Ship.PilotTypeLayer when target is Buoy => OrderChange.CidBuild,
                        Ship.PilotTypeWingman when target is Buoy => OrderChange.CidGoto,
                        Ship.PilotTypeWingman => TargetSide(target) == drone.SideId ? OrderChange.CidDefend : OrderChange.CidAttack,
                        _ => OrderChange.CidGoto,
                    };
                }
            }
            var typeOf = (IgcObjectType)chat.OtTarget;
            int? Id(IgcObjectType kind) => target is not null && typeOf == kind ? chat.OidTarget : null;
            foreach (int slot in new[] { TrainingMissionIds.CommandCurrent, TrainingMissionIds.CommandAccepted })
                IssueCommand(drone.Id, slot, Id(IgcObjectType.Ship), Id(IgcObjectType.Buoy), Id(IgcObjectType.Station),
                    command, Id(IgcObjectType.Asteroid), Id(IgcObjectType.Warp), Id(IgcObjectType.Probe),
                    Id(IgcObjectType.Mine), Id(IgcObjectType.Treasure));
            chat.CommandID = command;
            return true;
        }
    }

    private static short? TargetSide(IModelIgc target) => target switch
    {
        Ship ship => ship.SideId, Station station => station.SideId, Probe probe => probe.SideId,
        Mine mine => mine.SideId,
        EquipmentTarget { Body: Station station } => station.SideId, _ => null,
    };
}
