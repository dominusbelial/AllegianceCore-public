using System.Buffers.Binary;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

public sealed class P663TreasureParityTests
{
    private sealed class Site : IIgcSite
    {
        public readonly List<Treasure> Generated = [];
        public readonly List<Treasure> Destroyed = [];
        public void TreasureGeneratedEvent(Treasure t) => Generated.Add(t);
        public void TreasureDestroyedEvent(Treasure t) => Destroyed.Add(t);
    }

    private static (Mission Mission, Site Site) Domain(float payday = 5)
    {
        // Two synthetic loaded-core tables: cash 101 and a 9:1 powerup/cash table.
        var bytes = new byte[DataConstantsIGC.Size + 36 + 40];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(6 * 4), payday);
        int offset = DataConstantsIGC.Size;
        void Set(int at, short id, params (ushort Payload, TreasureCode Code, byte Chance)[] entries)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at + 26), id);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(at + 28), (short)entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                int e = at + 32 + 4 * i;
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(e), entries[i].Payload);
                bytes[e + 2] = (byte)entries[i].Code;
                bytes[e + 3] = entries[i].Chance;
            }
        }
        Set(offset, 11, (101, TreasureCode.Cash, 1));
        Set(offset + 36, 12, (1, TreasureCode.Powerup, 9), (202, TreasureCode.Cash, 1));
        var core = StaticCore.Load(new IgcFile
        {
            Wrapper = IgcWrapper.VersionAndDataSize, Version = 1, Body = bytes,
            BodyLength = bytes.Length, BytesConsumed = bytes.Length,
            Records = [new(IgcObjectType.Constants, DataConstantsIGC.Size, 0),
                new(IgcObjectType.TreasureSet, 36, offset), new(IgcObjectType.TreasureSet, 40, offset + 36)]
        });
        var cluster = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = 1, HomeSector = true };
        var site = new Site();
        var m = Mission.Assemble(core, MissionMap.FromLists([cluster, cluster with { ClusterId = 2, HomeSector = false },
            cluster with { ClusterId = 3, HomeSector = false }], [], [], []), [], site);
        m.TreasureParameters = new() { PlayerRegenerateSet = 11, NeutralRegenerateSet = 12,
            PlayerSectorTreasureRate = 1f / 120, NeutralSectorTreasureRate = 1f / 60 };
        m.Stage = MissionStage.Started;
        m.ApplyStartingMoneyAndInitialPayday(0);
        return (m, site);
    }

    [Fact]
    public void PaydayWindow_AccruesFractionalHomeAndNeutralRates_FromMissionAndCore()
    {
        var (m, site) = Domain();
        // Core cadence is 5s. No per-frame or before-payday generation.
        for (int i = 1; i <= 719; i++) m.Update(i * 0.25);
        Assert.Single(site.Generated, t => t.OwnerSector.Id == 1);
        Assert.Equal(2, site.Generated.Count(t => t.OwnerSector.Id == 2));
        m.Update(180);
        Assert.Equal(7, site.Generated.Count); // home 1 + two neutral sectors × 3
        Assert.Equal(101, site.Generated.Single(t => t.OwnerSector.Id == 1).Amount);
        Assert.InRange(m.Sectors[0].PendingTreasures, 0.49999, 0.50001);
        Assert.InRange(m.Sectors[1].PendingTreasures, 0, 0.00001);
        Assert.All(site.Generated, t => { Assert.Equal(Vector3.Zero, t.Velocity); Assert.InRange(t.Data.Lifespan, 300, 600); });
    }

    [Fact]
    public void IrregularPayday_UsesNativeOverdueElapsedAndRetainsRemainder()
    {
        var (m, _) = Domain();
        m.Update(8); // first payday at 5, dt = 5 + 3
        Assert.Equal(8d / 60, m.Sectors[1].PendingTreasures, 6);
        m.Update(10); // native keeps phase: dt = 5, not 2
        Assert.Equal(13d / 60, m.Sectors[1].PendingTreasures, 6);
        m.Stage = MissionStage.NotStarted;
        m.Update(15);
        Assert.Equal(13d / 60, m.Sectors[1].PendingTreasures, 6);
    }

    [Fact]
    public void Generator_UsesWeightedCoreEntries_AndNativePlacementAndSuppression()
    {
        var (m, _) = Domain();
        var sector = m.Sectors[0];
        var random = new Random(663);
        int powerups = 0;
        for (int i = 0; i < 1000; i++)
        {
            var t = Assert.IsType<Treasure>(m.GenerateTreasure(0, sector, 12, random));
            if (t.Code == TreasureCode.Powerup) powerups++;
            else { Assert.Equal(TreasureCode.Cash, t.Code); Assert.Equal(202, t.Amount); }
            var p = t.Position;
            double r = Math.Sqrt(p.X * p.X + p.Y * p.Y + 25 * p.Z * p.Z);
            Assert.InRange(r, 499.99, 5000.01);
            sector.Remove(t);
        }
        Assert.InRange(powerups, 865, 935);
        Assert.Null(m.GenerateTreasure(0, sector, -1));
        Assert.Null(m.GenerateTreasure(0, sector, 99));
        m.TreasureParameters = new() { AllowDevelopments = false };
        Assert.Null(m.GenerateTreasure(0, sector, 11));
    }

    [Fact]
    public void ArtifactRespawn_SelectsNeutralSectors_AndInitialPopulationUsesStartSets()
    {
        var (m, _) = Domain();
        m.TreasureParameters = new() { PlayerStartSet = 11, NeutralStartSet = 12 };
        m.PopulateTreasures(0, 2, 3, artifacts: true);
        Assert.Equal(2, m.Sectors[0].Models.OfType<Treasure>().Count());
        Assert.Equal(4, m.Sectors[1].Models.OfType<Treasure>().Count());
        var random = new Random(663);
        var selected = new HashSet<short>();
        for (int i = 0; i < 30; i++)
        {
            var t = Assert.IsType<Treasure>(m.RespawnArtifact(0, random));
            Assert.False(t.OwnerSector.Cluster.HomeSector);
            Assert.Equal(TreasureCode.Flag, t.Code);
            Assert.Equal(Side.ArtifactSentinelFlag, t.Data.TreasureId);
            Assert.Equal(864000, t.Data.Lifespan);
            selected.Add(t.OwnerSector.Id);
            t.OwnerSector.Remove(t);
        }
        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void TrajectoryExport_PreservesOriginalMilliseconds_AndInitialCollisionVelocity()
    {
        var (m, _) = Domain();
        var sector = m.Sectors[0];
        // 1001 / 1000.0 * 1000 truncates to 1000: never round-trip export time
        // through seconds when the original server millisecond value is available.
        var data = new DataTreasureIGC([0, 0, 0], [100, 0, 0], 30, 1001, 77, 101, sector.Id, 101, TreasureCode.Cash, false);
        var t = new Treasure(77, sector, data, 1.001, m);
        Assert.Equal(1001u, t.Data.Time0);
        Assert.Equal(new Vector3(100, 0, 0), t.CollisionVelocity);
        Assert.Equal(31.001, t.ExpireAt, 6);
    }

    [Fact]
    public void OrdinaryPlacement_GivesUpAfterThreeBlockedAttempts()
    {
        var (m, _) = Domain();
        var sector = m.Sectors[0];
        var hull = DataHullTypeIGC.Decode(new byte[DataHullTypeIGC.Size]) with { Length = 40000 };
        var blocker = new Ship(50, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        m.PlaceShip(blocker, sector);
        Assert.Null(m.GenerateTreasure(0, sector, 11, new Random(663)));
        Assert.Empty(sector.Models.OfType<Treasure>());
    }

    private sealed class EntryRandom(int choice) : Random(663)
    {
        private bool _first = true;
        public override int Next(int minValue, int maxValue)
        {
            if (!_first) return base.Next(minValue, maxValue);
            _first = false;
            Assert.InRange(choice, minValue, maxValue - 1);
            return choice;
        }
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void LoadedCorePartTables_GenerateNativeLauncherPackAndHardwareQuantities()
    {
        Allegiance.TestInfra.Require.Artwork();
        var file = IgcReader.ParseFile(Corpus.Files.First(p => Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase)));
        var original = StaticCore.Load(file);
        // Stock PCore tables do not select all three part categories. Add a
        // loaded table referencing real PCore types to exercise custom cores too.
        short[] partIds = [original.Launchers.Keys.First(),
            original.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Pack).PartId,
            original.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon).PartId];
        var body = new byte[file.Body.Length + 44];
        file.Body.Span.CopyTo(body);
        int offset = file.Body.Length;
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(offset + 26), 31000);
        BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(offset + 28), 3);
        for (int i = 0; i < partIds.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(offset + 32 + i * 4), partIds[i]);
            body[offset + 34 + i * 4] = (byte)TreasureCode.Part;
            body[offset + 35 + i * 4] = 1;
        }
        var core = StaticCore.Load(new IgcFile
        {
            Wrapper = file.Wrapper, Version = file.Version, Body = body,
            BodyLength = body.Length, BytesConsumed = body.Length,
            Records = file.Records.Concat([new IgcRecord(IgcObjectType.TreasureSet, 44, offset)]).ToArray()
        });
        var (template, _) = Domain();
        var m = Mission.Assemble(core, template.Map, [], new Site());
        bool launchers = false, packs = false, hardware = false;
        foreach (var set in core.TreasureSets.Values)
        {
            int choice = 1;
            foreach (var entry in set.Entries)
            {
                if (entry.Chance > 0 && entry.Code == TreasureCode.Part)
                {
                    short id = unchecked((short)entry.TreasureId);
                    var t = Assert.IsType<Treasure>(m.GenerateTreasure(0, m.Sectors[0], set.TreasureSetId, new EntryRandom(choice)));
                    Assert.Equal(id, t.Data.TreasureId);
                    if (core.Launchers.TryGetValue(id, out var launcher))
                    {
                        launchers = true;
                        Assert.InRange(t.Amount, 1, launcher.Amount);
                    }
                    else if (core.PartTypes[id].Type is DataPartTypeIGC.Pack pack)
                    {
                        packs = true;
                        Assert.InRange(t.Amount, pack.Amount / 2, pack.Amount);
                    }
                    else { hardware = true; Assert.Equal(0, t.Amount); }
                    t.OwnerSector.Remove(t);
                }
                choice += entry.Chance;
            }
        }
        Assert.True(launchers && packs && hardware, "real core tables must exercise every native part-amount branch");
    }

    [Theory]
    [InlineData(0.25, 24.6875, 97.5)]
    [InlineData(2, 180, 80)]
    [InlineData(7, 455, 30)]
    [InlineData(10, 500, 0)]
    [InlineData(13, 500, 0)]
    public void Drift_MatchesNativeTenUnitDeceleration(double elapsed, double distance, double speed)
    {
        var (m, _) = Domain();
        var sector = m.Sectors[0];
        var t = new Treasure(77, sector, new([10, 20, 30], [60, 80, 0], 30, 0, 77, 101, sector.Id, 101, TreasureCode.Cash, true), 25, m);
        sector.Add(t);
        t.Update(25 + elapsed);
        Assert.Equal(10 + 0.6 * distance, t.Position.X, 3);
        Assert.Equal(20 + 0.8 * distance, t.Position.Y, 3);
        Assert.Equal(speed, t.Velocity.Length(), 3);
        Assert.Equal(speed == 0, t.CollisionStatic);
        Assert.Equal(0.6 * distance / elapsed, t.CollisionVelocity.X, 3);
        Assert.Equal(25000u, t.Data.Time0);
        Assert.False(t.Data.CreateNow);
    }

    [Fact]
    public void LateExport_AndDifferentTickPartitionsReachSameRestingPose_ThenExpire()
    {
        var (m, site) = Domain();
        var sector = m.Sectors[0];
        var data = new DataTreasureIGC([0, 0, 0], [100, 0, 0], 30, 10000, 77, 101, sector.Id, 101, TreasureCode.Cash, false);
        var a = new Treasure(77, sector, data, 10, m);
        var b = new Treasure(78, sector, data, 16, m);
        a.Update(16);
        Assert.Equal(a.Position, b.Position);
        for (int i = 1; i <= 45; i++) a.Update(16 + i * 0.2);
        b.Update(25);
        Assert.Equal(new Vector3(500, 0, 0), a.Position);
        Assert.Equal(a.Position, b.Position);
        sector.Add(a);
        a.Update(40);
        Assert.DoesNotContain(a, sector.Models);
        Assert.Contains(a, site.Destroyed);
    }

    [Fact]
    public void MovingLoot_SweepsAcrossPicker_WhenBothEndpointsAreOutsidePickupRadius()
    {
        var (m, _) = Domain();
        var sector = m.Sectors[0];
        var hull = DataHullTypeIGC.Decode(new byte[DataHullTypeIGC.Size]) with
        { Length = 10, HitPoints = 100, Mass = 1, MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1], HabmCapabilities = DataHullTypeIGC.HabmRescue };
        var ship = new Ship(50, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        m.PlaceShip(ship, sector);
        var t = new Treasure(77, sector, new([-20, 0, 0], [400, 0, 0], 30, 0, 77, 101, sector.Id, 101, TreasureCode.Cash, true), 0, m);
        sector.Add(t);
        sector.Update(0.1);
        Assert.True(t.Position.X > 10);
        Assert.Contains(sector.DrainTreasurePickups(), pair => pair.Ship == ship && pair.Treasure == t);
    }
}
