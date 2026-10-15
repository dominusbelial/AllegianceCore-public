using Allegiance.ClientCore.State;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>Station event SoundID resolution. Native clintlib/appmsg.cpp:548-551,
/// 674-677 selects the field on the station type, independently of its name/model.
/// The wave table is derived from the bundled sounddef.mdl:76-79,272,1239-1284,
/// 2291-2341 (numeric soundList entry → template → ImportWave, including aliases).
/// Unknown IDs remain silent; they must not announce a different station family.</summary>
public static class StationEventSoundMap
{
    public static bool TryResolve(ClientSoundEvent ev,
        IReadOnlyDictionary<short, DataStationTypeIGC>? stationTypes, out string stem)
    {
        stem = "";
        if (stationTypes is null || !stationTypes.TryGetValue(ev.StationTypeId, out var type)) return false;
        short id = ev.Kind switch
        {
            ClientSoundEventKind.StationDestroyed => type.DestroyedSound,
            ClientSoundEventKind.StationEnemyDestroyed => type.EnemyDestroyedSound,
            ClientSoundEventKind.StationCaptured => type.CapturedSound,
            ClientSoundEventKind.StationEnemyCaptured => type.EnemyCapturedSound,
            _ => (short)-1,
        };
        if (!Stems.TryGetValue(id, out var resolved)) return false;
        stem = resolved;
        return true;
    }

    public static IReadOnlyDictionary<short, string> Stems { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<short, string>(new Dictionary<short, string>
    {
        [860] = "vo_destroy_garrison",
        [861] = "vo_destroy_outpost",
        [862] = "vo_destroy_shipyard",
        [863] = "vo_destroy_teleport",
        [864] = "vo_destroy_refinery",
        [865] = "vo_destroy_expansion",
        [866] = "vo_destroy_supremecy",
        [867] = "vo_destroy_tactical",
        [868] = "vo_destroy_mine",
        [869] = "vo_destroy_palisade",
        [870] = "vo_destroy_research",
        [871] = "dn_vo_destroy_transeiver",
        [880] = "vo_capture_garrison",
        [881] = "vo_capture_outpost",
        [882] = "vo_capture_shipyard",
        [883] = "ef03",
        [884] = "ef03",
        [885] = "vo_capture_expansion",
        [886] = "vo_capture_supremecy",
        [887] = "vo_capture_tactical",
        [888] = "vo_capture_mine",
        [889] = "vo_capture_palisade",
        [890] = "vo_capture_research",
        [891] = "dn_vo_capture_transeiver",
        [900] = "vo_destroy_enemygarrison",
        [901] = "vo_destroy_enemyoutpost",
        [902] = "vo_destroy_enemyshipyard",
        [903] = "vo_destroy_enemyteleport",
        [904] = "vo_destroy_enemyrefinery",
        [905] = "vo_destroy_enemyexpansion",
        [906] = "vo_destroy_enemysupremecy",
        [907] = "vo_destroy_enemytactical",
        [908] = "vo_destroy_enemymine",
        [909] = "vo_destroy_enemypalisade",
        [910] = "vo_destroy_enemyresearch",
        [911] = "dn_vo_destroy_enemytranseiver",
        [920] = "vo_capture_enemygarrison",
        [921] = "vo_capture_enemyoutpost",
        [922] = "vo_capture_enemyshipyard",
        [923] = "ef03",
        [924] = "ef03",
        [925] = "vo_capture_enemyexpansion",
        [926] = "vo_capture_enemysupremecy",
        [927] = "vo_capture_enemytactical",
        [928] = "vo_capture_enemymine",
        [929] = "vo_capture_enemypalisade",
        [930] = "vo_capture_enemyresearch",
        [931] = "dn_vo_capture_enemytranseiver",
    });
}
