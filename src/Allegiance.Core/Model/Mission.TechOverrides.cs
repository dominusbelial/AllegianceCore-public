using Allegiance.Core.Sim;
namespace Allegiance.Core.Model;

public sealed partial class Mission
{
    public TechBitOverrides TechOverrides { get; private set; } = new();

    /// <summary>AdminGame.cpp:312-328: only NotStarted and a native side slot.
    /// Native acknowledges refused calls without changing either mask.</summary>
    public bool OverrideTechBit(int side, int bit, bool setting)
    {
        if (Stage != MissionStage.NotStarted || side is < 0 or >= TechBitOverrides.MaxSides
            || bit is < 0 or >= TechBitOverrides.MaxBits) return false;
        TechOverrides.Set(side, bit, setting);
        return true;
    }

    /// <summary>The port assembles the map before the lobby opens; finish the
    /// native generation tech/bucket step at StartCountdown (fsmission.cpp:1954).
    /// No masks means no mutation of the pre-existing generation path. With masks,
    /// each side's techs and buckets are rebuilt here, so lobby-time tech changes
    /// and anything holding the old bucket objects (e.g. primed bot research plans,
    /// P938) must be (re)built after this call.</summary>
    public void ApplyStartingTechOverrides(bool restoreAuthoredStations = true)
    {
        if (Stage != MissionStage.NotStarted || !TechOverrides.IsAllocated) return;
        foreach (var side in Sides)
        {
            side.DevelopmentTechs = TechOverrides.Apply(side.Id, Research.StartingTechs(side.Civilization));
            side.InitialTechs = side.DevelopmentTechs;
            side.ResetStartingBuckets(Research.AllowDevelopments
                ? BucketCatalog.ForSide(Core, Site, side.InitialTechs,
                    GlobalAttributeSet.FromArray(side.Civilization.GasBaseAttributes),
                    Goals.IsProsperityGame ? Goals.TeamMoney : 1)
                : Array.Empty<Bucket>());
        }
        // Native applies overrides before loading the authored station records
        // (missionigc.cpp:3885-3897,4011-4049). The port already loaded them at
        // boot, so restore those choices before testing their new prerequisites.
        foreach (var record in restoreAuthoredStations ? Map.Stations : [])
            if (GetStation(record.StationId) is { } station
                && Core.StationTypes.TryGetValue(record.StationTypeId, out var authored))
                station.SetBaseStationType(authored);
        ReplaceUnavailableStationTypes();
    }
}
