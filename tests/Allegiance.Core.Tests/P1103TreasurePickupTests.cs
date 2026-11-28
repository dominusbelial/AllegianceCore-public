using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

public sealed class P1103TreasurePickupTests
{
    private sealed class Site(int seed) : IIgcSite
    {
        private readonly Random _random = new(seed);
        internal readonly List<(double Time, short Ship, short Treasure, int Draw, bool Accepted)> Calls = [];
        internal Treasure MovedByCallback = null!;

        public bool HitTreasureEvent(double now, IShipIgc picker, ITreasureIgc item)
        {
            var ship = (Ship)picker;
            var treasure = (Treasure)item;
            int draw = _random.Next();
            bool accept = treasure.Code == TreasureCode.Flag
                ? ship.SideId != treasure.Data.TreasureId && ship.Flag == null
                : treasure.Id % 7 != 0 && draw % 9 != 0;
            Calls.Add((now, ship.Id, treasure.Id, draw, accept));
            if (Calls.Count == 1)
                MovedByCallback.Position = ship.Position;
            return accept;
        }
    }

    private static Mission Scene(int seed, int special, Site site)
    {
        var body = new byte[DataConstantsIGC.Size];
        var core = StaticCore.Load(new IgcFile
        {
            Wrapper = IgcWrapper.VersionAndDataSize, Version = 1, Body = body,
            BodyLength = body.Length, BytesConsumed = body.Length,
            Records = [new(IgcObjectType.Constants, body.Length, 0)]
        });
        var cluster = DataClusterIGC.Decode(new byte[DataClusterIGC.Size]);
        var mission = Mission.Assemble(core, MissionMap.FromLists([cluster], [], [], []), [], site);
        var sector = mission.Sectors[0];
        var random = new Random(seed);
        Vector3 Point() => new(random.Next(-600, 601), random.Next(-180, 181), random.Next(-120, 121));
        Vector3 anchor = new(0, 0, 0);
        for (short i = 0; i < 160; i++)
        {
            // Deliberately unequal registration and ID orders. The first two
            // ships share the crossing part, zero-length cash and side 0 flag.
            Vector3 pos = i < 6 ? anchor : Point();
            if (i == 0) pos -= new Vector3(70, 0, 0);
            if (i == 4) pos = new(500, 500, 500);
            if (i == 159 && special == 1) pos = new(float.NaN, 0, 0);
            if (i == 159 && special == 2) pos = new(float.MaxValue, 0, 0);
            var code = i switch
            {
                0 => TreasureCode.Part, 1 => TreasureCode.Flag, 2 => TreasureCode.Cash,
                3 => TreasureCode.Development, 4 => TreasureCode.Powerup,
                _ => (TreasureCode)random.Next(1, 6)
            };
            short id = (short)(1000 - i);
            var data = new DataTreasureIGC([pos.X, pos.Y, pos.Z], [0, 0, 0], 1000, 0,
                id, 0, sector.Id, 1, code, true);
            var tr = new Treasure(id, sector, data, 0, mission);
            if (i == 0) tr.Position = anchor + new Vector3(150, 0, 0); // PreviousPosition is the other endpoint
            if (i == 4) site.MovedByCallback = tr;
            sector.Add(tr);

            if (i >= 36) continue;
            var hull = default(DataHullTypeIGC) with { Length = i < 2 ? 16 : random.Next(4, 100),
                HitPoints = 100, HabmCapabilities = i == 2 ? (short)0 : DataHullTypeIGC.HabmRescue };
            var location = i < 6 ? anchor : Point();
            if (i == 24 && special == 3) location = new(1, float.NaN, 0);
            var velocity = i < 2 ? Vector3.Zero : Point() * 0.3f;
            var ship = new Ship(i, hull, location, velocity, Orientation.Identity)
            {
                SideId = (short)(i % 2), PilotType = i == 3 ? Ship.PilotTypeMiner : Ship.PilotTypePlayer
            };
            mission.PlaceShip(ship, sector);
            if (i == 4) ship.StationId = 42;
            if (i == 5) ship.SideId = null;
        }
        return mission;
    }

    // Independent, pre-P1103 all-pairs algorithm. In particular the rejection
    // is '>' so unordered floating-point comparisons still reach the callback.
    private static List<(short, short)> Reference(Sector sector, Site site, double now, float dt)
    {
        var treasures = sector.Models.OfType<Treasure>().ToList();
        var done = new HashSet<Treasure>();
        var pending = new List<(short, short)>();
        foreach (var ship in sector.Models.OfType<Ship>())
        {
            if (ship.Dead || ship.StationId != null || ship.SideId == null || ship.Mission == null ||
                ship.PilotType < Ship.PilotTypePlayer || !ship.HullType.HasCapability(DataHullTypeIGC.HabmRescue))
                continue;
            float r = Treasure.PickupRadius + 0.5f * ship.HullType.Length;
            foreach (var t in treasures)
            {
                if (done.Contains(t) || t.Code is not (TreasureCode.Cash or TreasureCode.Part or TreasureCode.Flag or TreasureCode.Powerup))
                    continue;
                var end = ship.Position - t.Position;
                var start = ship.Position - ship.CollisionVelocity * dt - t.PreviousPosition;
                var travel = end - start;
                float d = travel.LengthSquared();
                float fraction = d == 0 ? 0 : Math.Clamp(-(start * travel) / d, 0, 1);
                if ((start + travel * fraction).LengthSquared() > r * r) continue;
                if (!site.HitTreasureEvent(now, ship, t)) continue;
                pending.Add((ship.Id, t.Id));
                done.Add(t);
            }
        }
        return pending;
    }

    [Fact]
    public void Seeded_pickups_match_every_callback_answer_draw_and_pending_pair()
    {
        for (int seed = 1; seed <= 32; seed++)
        {
            var oldSite = new Site(seed);
            var newSite = new Site(seed);
            using var oldWorld = Scene(seed, seed % 4, oldSite);
            using var newWorld = Scene(seed, seed % 4, newSite);
            var sector = newWorld.Sectors[0];
            foreach (float dt in new[] { 0.75f, 0, 0.05f })
            {
                var pending = Reference(oldWorld.Sectors[0], oldSite, 10 + dt, dt);
                sector.ResolveTreasurePickups(10 + dt, dt);
                Assert.Equal(oldSite.Calls, newSite.Calls);
                Assert.Equal(pending, sector.DrainTreasurePickups().Select(p => (p.Ship.Id, p.Treasure.Id)));
            }
            Assert.Contains(newSite.Calls, c => c.Ship == 0 && c.Treasure == 1000);
            Assert.Contains(newSite.Calls, c => c.Ship == 0 && c.Treasure == 999 && !c.Accepted);
            Assert.Contains(newSite.Calls, c => c.Ship == 1 && c.Treasure == 999 && c.Accepted);
        }
    }

    [Fact]
    public void Grid_prunes_distant_cells_and_orders_crossing_candidates()
    {
        using var world = Scene(7, 0, new Site(7));
        var grid = new TreasurePickupGrid();
        grid.Treasures.AddRange(world.Sectors[0].Models.OfType<Treasure>().Where(t => t.Code != TreasureCode.Development));
        Assert.True(grid.Build());
        Assert.True(grid.Query(new(0, 0, 0), new(0, 0, 0), 13));
        Assert.Contains(0, grid.Candidates);
        Assert.InRange(grid.Candidates.Count, 1, grid.Treasures.Count - 1);
        Assert.Equal(grid.Candidates.Distinct().Order(), grid.Candidates);
        Assert.False(grid.Query(new(float.NaN, 0, 0), Vector3.Zero, 13));
        Assert.False(grid.Query(new(-50000, -50000, -50000), new(50000, 50000, 50000), 13));
    }
}
