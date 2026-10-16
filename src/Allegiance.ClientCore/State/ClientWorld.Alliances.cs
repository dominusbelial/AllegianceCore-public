using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    private readonly sbyte[] _allianceGroups = Enumerable.Repeat((sbyte)-1, ChangeAlliances.SideAlliesMax).ToArray();

    // clintlib/appmsg.cpp:2554-2569; Igc/igc.h:4297-4302.
    private void ApplyAlliances(ChangeAlliances message) => Array.Copy(message.Allies, _allianceGroups, _allianceGroups.Length);
    public bool HasAllies(short side) => side >= 0 && side < _allianceGroups.Length && _allianceGroups[side] >= 0;
    public bool AreAllied(short a, short b) => a != b && HasAllies(a) && HasAllies(b)
        && _allianceGroups[a] == _allianceGroups[b];
}
