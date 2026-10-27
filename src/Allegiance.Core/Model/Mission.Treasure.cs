using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

public sealed partial class Mission
{
    public TreasureParameters TreasureParameters { get; set; } = new();

    // FedSrv/fsmission.cpp:2516 reads c_fcidSecondsBetweenPaydays (igc.h:65).
    private double PaydayInterval => Core.Constants.FloatConstants is { Length: > 6 } fc
        && float.IsFinite(fc[6]) && fc[6] > 0 ? fc[6] : SecondsBetweenPaydays;

    private void RegenerateTreasures(double now, double elapsed)
    {
        foreach (var sector in Sectors)
        {
            bool home = sector.Cluster.HomeSector;
            double pending = sector.PendingTreasures + elapsed * (home
                ? TreasureParameters.PlayerSectorTreasureRate : TreasureParameters.NeutralSectorTreasureRate);
            int count = (int)pending;
            sector.PendingTreasures = pending - count;
            short set = home ? TreasureParameters.PlayerRegenerateSet : TreasureParameters.NeutralRegenerateSet;
            for (int i = 0; i < count; i++) GenerateTreasure(now, sector, set);
        }
    }

    /// <summary>Weighted core-table selection and native placement/lifetime rules
    /// (Igc/missionigc.cpp:4125-4214; Igc/treasuresetigc.h:100-117).</summary>
    public Treasure? GenerateTreasure(double now, Sector sector, short setId, Random? random = null)
    {
        random ??= Random.Shared;
        TreasureCode code;
        short payload;
        if (setId >= 0)
        {
            if (!Core.TreasureSets.TryGetValue(setId, out var set)) return null;
            int total = set.Entries.Sum(e => (int)e.Chance);
            if (total == 0) return null;
            int choice = random.Next(1, total + 1);
            var entry = set.Entries.First(e => (choice -= e.Chance) <= 0);
            code = entry.Code;
            payload = unchecked((short)entry.TreasureId);
        }
        else if (setId == -1) return null;
        else { code = TreasureCode.Flag; payload = Side.ArtifactSentinelFlag; }

        if (code == TreasureCode.Cash && (!AllowDevelopments || !TreasureParameters.AllowDevelopments)) return null;

        // Native uses these fixed placement constants, rather than the mutable
        // universe-boundary float slots (missionigc.cpp:2685-2686,4158-4169).
        float radius = 5000f;
        Vector3 position;
        int tries = code == TreasureCode.Flag ? int.MaxValue : 3;
        while (true)
        {
            float z = (float)(2 * random.NextDouble() - 1);
            float angle = (float)(2 * Math.PI * random.NextDouble());
            float xy = MathF.Sqrt(Math.Max(0, 1 - z * z));
            float distance = radius * MathF.Pow(0.001f + 0.999f * (float)random.NextDouble(), 1f / 3f);
            position = new Vector3(xy * MathF.Cos(angle) * distance, xy * MathF.Sin(angle) * distance, z * distance * 0.2f);
            if (!TooClose(position)) break;
            radius *= 1.05f;
            if (--tries == 0) return null;
        }

        // Initialize's amount normalization (treasureIGC.cpp:46-92).
        short amount = -1;
        if (code == TreasureCode.Cash) amount = payload;
        else if (code == TreasureCode.Part)
        {
            if (Core.Launchers.TryGetValue(payload, out var launcher))
                amount = (short)random.Next(1, launcher.Amount + 1);
            else if (Core.PartTypes.TryGetValue(payload, out var part))
                amount = part.Type is DataPartTypeIGC.Pack pack
                    ? (short)random.Next(pack.Amount / 2, pack.Amount + 1) : (short)0;
            else return null;
        }
        var data = new DataTreasureIGC([position.X, position.Y, position.Z], [0, 0, 0],
            code == TreasureCode.Flag ? 10f * 24 * 3600 : 300f + 300f * (float)random.NextDouble(),
            (uint)(now * 1000), NextObjectId(), payload, sector.Id, amount, code, true);
        var treasure = new Treasure(data.ObjectId, sector, data, now, this);
        sector.Add(treasure);
        Site.TreasureGeneratedEvent(treasure);
        return treasure;

        bool TooClose(Vector3 p)
        {
            foreach (var model in sector.Models)
            {
                float r = model switch
                {
                    Ship ship => 500f + 0.5f * ship.HullType.Length,
                    Treasure => 25f + Treasure.PickupRadius,
                    // DataMineTypeIGC shadows DataObjectIGC.radius. Native
                    // mineigc.cpp:103-106 sets the model radius from the tail
                    // field (minetypeigc.h:104-106), not the uninitialized base.
                    Mine mine when code == TreasureCode.Flag && payload == Side.ArtifactSentinelFlag
                        => 25f + mine.MineType.MineRadius,
                    // Other treasure codes keep their existing behavior in this artifact slice.
                    Mine mine => 25f + mine.MineType.Radius,
                    Probe probe => 25f + probe.ProbeType.Radius,
                    IOrdnance ordnance => 25f + ordnance.Radius,
                    _ => 25f,
                };
                if ((model.Position - p).LengthSquared() < 10 * r * r) return true;
            }
            foreach (var asteroid in sector.Mineable)
                if (Near(TreasureMotion.Vector(asteroid.Data.Position), asteroid.Radius)) return true;
            foreach (var station in sector.StationRuntimes)
                if (Near(station.Center, station.Radius)) return true;
            foreach (var warp in sector.Warps)
                if (Near(TreasureMotion.Vector(warp.Warp.Position), warp.Warp.Radius)) return true;
            return false;
            bool Near(Vector3 other, float r) => (other - p).LengthSquared() < 10 * (25 + r) * (25 + r);
        }
    }

    /// <summary>FedSrv/FedSrv.CPP:13847-13868: choose any neutral cluster;
    /// despite its comment, native does not exclude clusters already containing flags.</summary>
    public Treasure? RespawnArtifact(double now, Random? random = null)
    {
        random ??= Random.Shared;
        var neutral = Sectors.Where(s => !s.Cluster.HomeSector).ToArray();
        return neutral.Length == 0 ? null : GenerateTreasure(now, neutral[random.Next(neutral.Length)], -2, random);
    }

    /// <summary>Initial population for generated missions (Igc/common.cpp:3081-3107).</summary>
    public void PopulateTreasures(double now, int homeCount, int neutralCount, bool artifacts)
    {
        foreach (var sector in Sectors)
        {
            bool home = sector.Cluster.HomeSector;
            if (!home && artifacts) GenerateTreasure(now, sector, -2);
            int count = home ? homeCount : neutralCount;
            short set = home ? TreasureParameters.PlayerStartSet : TreasureParameters.NeutralStartSet;
            for (int i = 0; i < count; i++) GenerateTreasure(now, sector, set);
        }
    }
}
