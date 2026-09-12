using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P155 (NPC autopilot foundation) — live loopback proof, via the real
/// in-process server and <see cref="Srv.SpawnNpcShip"/>, that an NPC ship with
/// <see cref="Ship.PlanTarget"/> set to a same-sector station is the FIRST NPC
/// in this port observed to MOVE under its own decision: it turns toward the
/// target (simplified proportional yaw/pitch) and thrusts (two-zone distance
/// throttle), measurably closing the gap, and settles near the target without
/// runaway overshoot/oscillation. The harness mirrors P147's
/// <c>NpcShipFoundationLiveTests</c> (booted server + one seated bot, no
/// Windows VM); the mission is advanced with the server's own deterministic
/// <see cref="Srv.TickSim"/> (one 0.05 s chunk each).
///
/// Three proofs:
///   1. Scope-3 movement — a flying <c>SpawnNpcShip</c> NPC with PlanTarget set
///      to a real same-sector station gets measurably closer over real ticks.
///   2. Scope-3 arrival — the same primitive settles near the target and does
///      not overshoot/oscillate forever (a reasonable settling check).
///   3. Scope-4 end-to-end — a damaged NPC's P151 take-cover sets PlanTarget,
///      and this task's autopilot then actually MOVES it there: the first time
///      this arc's own decisions become observable, not just recorded.
/// </summary>
[Collection("LiveNetwork")]
public sealed class NpcAutopilotLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "NPC Autopilot Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 993u, DwCookie = 0x993u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99200000000000003ul, DrmHash = "npcap",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Mission Mission = null!;
        public Sector Sector = null!;
        public Ship Peer = null!;
        public FltBot Bot = null!;
        public short PeerSide;

        public void Dispose()
        {
            Bot?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatOne()
    {
        var seat = new Seated();
        seat.Server = new Srv();
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            seat.Mission = mission;
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        var port = seat.Server.BoundPort;

        var bot = new FltBot();
        try
        {
            bot.Connect(port, "p155_npc");
            bot.SendLogonRequest(Request("p155_npc"));
            PumpUntil(seat.Server, () => bot.ShipAssignment is not null, 4000);
            Assert.True(bot.ShipAssignment is not null, "bot must be assigned a ship");
            var peer = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
            peer.Velocity = Vector3.Zero;
            seat.Peer = peer;
            seat.PeerSide = peer.SideId ?? throw new InvalidOperationException("peer must have a side");
            seat.Bot = bot;
            return seat;
        }
        catch
        {
            bot.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    /// <summary>Build a genuine same-sector <see cref="DataStationIGC"/> target at
    /// the given position carrying a land-capable type.</summary>
    private static DataStationIGC Target(Sector sector, short side, short id, float x, float y, float z)
    {
        var core = Core();
        var type = core.StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmLand) != 0);
        return new DataStationIGC(
            Position: [x, y, z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: side, StationId: id,
            StationTypeId: (short)type.StationTypeId, BpHull: 1, BpShield: 1, Name: $"BASE_{id}");
    }

    private static DataStationTypeIGC LandType() =>
        Core().StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmLand) != 0);

    private static float Dist(Ship s, DataStationIGC t) =>
        (new Vector3(t.Position[0], t.Position[1], t.Position[2]) - s.Position).Length();

    private static void SetFraction(Ship ship, float fraction)
        => typeof(Ship).GetField("_fraction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ship, fraction);

    private static void SetShieldFraction(Ship ship, float fraction)
        => typeof(Ship).GetField("_shieldFraction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ship, fraction);

    /// <summary>⭐ Scope-3 movement. A flying NPC spawned via
    /// <see cref="Srv.SpawnNpcShip"/> with PlanTarget set to a real same-sector
    /// station gets measurably closer to it over many real server ticks — the
    /// first NPC in this port observed to move under its own decision.</summary>
    [Fact]
    public void Flying_npc_with_plan_target_moves_measurably_closer_to_station()
    {
        using var seat = SeatOne();
        var npcPos = new Vector3(952f, 936f, 959f);
        var npc = seat.Server.SpawnNpcShip(seat.Mission, seat.Sector, seat.PeerSide, npcPos);
        var target = Target(seat.Sector, seat.PeerSide, id: 4211, x: 320f, y: 986f, z: 969f);
        npc.PlanTarget = target;
        npc.PlanCommandId = OrderChange.CidGoto;

        float d0 = Dist(npc, target);
        Assert.True(d0 > 250f, "initial separation should be substantial");

        // 5000 ticks × 0.05 s = 250 simulated seconds — ample to accelerate,
        // approach, brake and close the gap from a standstill.
        for (int i = 0; i < 5000; i++) seat.Server.TickSim();
        float d1 = Dist(npc, target);

        Assert.True(d1 < d0 - 200f, $"NPC must close a lot: d0={d0:F1} d1={d1:F1}");
        Assert.True(npc.Position != npcPos, "the NPC must have physically left its spawn point");
        Console.WriteLine($"P155-APPROACH: flying NPC {npc.Id} with PlanTarget→station {target.StationId}: d0={d0:F1} → d1={d1:F1} (measured closer, real server ticks)");
    }

    /// <summary>⭐ Scope-3 arrival. From a moderate separation the same primitive
    /// reaches the target's vicinity and settles: the final distance is small,
    /// and a trailing window shows no runaway overshoot/re-expansion (a
    /// reasonable settling check, not pixel-perfect).</summary>
    [Fact]
    public void Flying_npc_reaches_and_settles_near_target_without_runaway_oscillation()
    {
        using var seat = SeatOne();
        var npcPos = new Vector3(898f, 924f, 949f);
        var npc = seat.Server.SpawnNpcShip(seat.Mission, seat.Sector, seat.PeerSide, npcPos);
        var target = Target(seat.Sector, seat.PeerSide, id: 4231, x: 545f, y: 997f, z: 979f);
        npc.PlanTarget = target;
        npc.PlanCommandId = OrderChange.CidGoto;

        float d0 = Dist(npc, target);
        Assert.True(d0 > 150f, "moderate initial separation for an arrival demo");

        // 7000 ticks × 0.05 s = 350 simulated seconds: reach the vicinity and settle.
        for (int i = 0; i < 7000; i++) seat.Server.TickSim();
        float dSettled = Dist(npc, target);

        // Arrived: comfortably inside a small multiple of the arrival radius (60 u).
        Assert.True(dSettled < 200f, $"must settle near the target: d={dSettled:F1}");

        // No runaway: over a further trailing window the distance stays bounded
        // (the ship does not blow back out past the target and thrash forever).
        float maxTail = 0f, minTail = float.MaxValue;
        for (int i = 0; i < 2000; i++)
        {
            seat.Server.TickSim();
            float d = Dist(npc, target);
            maxTail = MathF.Max(maxTail, d);
            minTail = MathF.Min(minTail, d);
        }
        Assert.True(maxTail < 280f, $"no runaway oscillation: trailing max dist {maxTail:F1}");

        Console.WriteLine($"P155-ARRIVAL: NPC {npc.Id} settled: d0={d0:F1} → dSettled={dSettled:F1} " +
                          $"(trailing min={minTail:F1} max={maxTail:F1}, no runaway); arrival radius=60 (simplified heuristic)");
    }

    /// <summary>⭐ Scope-4 end-to-end. A damaged NPC's P151 take-cover sets
    /// PlanTarget on a tick; this task's autopilot then ACTUALLY MOVES it toward
    /// that station over subsequent ticks — the first time P151's decision
    /// becomes observable as real ship movement, not just a recorded reference.</summary>
    [Fact]
    public void Damaged_npc_take_cover_now_produces_observable_movement()
    {
        using var seat = SeatOne();
        var npcPos = new Vector3(878f, 916f, 941f);
        var npc = seat.Server.SpawnNpcShip(seat.Mission, seat.Sector, seat.PeerSide, npcPos);

        // Damage it so P151's take-cover trigger fires (shipIGC.h:2230-2247).
        SetFraction(npc, 0.4f);
        SetShieldFraction(npc, 0.0f);

        // A real friendly land-capable station IN the same sector for it to flee to.
        var stationData = Target(seat.Sector, seat.PeerSide, id: 4251, x: 392f, y: 921f, z: 943f);
        seat.Mission.PlaceStation(new Station(stationData, LandType()), seat.Sector);

        // First tick: the take-cover decision runs (P151 block at the end of
        // OnUpdate) and sets PlanTarget — this is the OLD observable point.
        seat.Server.TickSim();
        Assert.NotNull(npc.PlanTarget);
        Assert.Equal(OrderChange.CidGoto, npc.PlanCommandId);
        float d0 = Dist(npc, npc.PlanTarget!.Value);

        // Subsequent ticks: THIS task's autopilot turns the recorded decision
        // into real, observable movement toward the hiding station.
        for (int i = 0; i < 5000; i++) seat.Server.TickSim();
        float d1 = Dist(npc, npc.PlanTarget.Value);

        Assert.True(d1 < d0 - 150f, $"take-cover must now produce real movement: d0={d0:F1} d1={d1:F1}");
        Console.WriteLine($"P155-TAKECOVER-E2E: damaged NPC {npc.Id} took cover at station {npc.PlanTarget.Value.StationId}: " +
                          $"d0={d0:F1} → d1={d1:F1} (P151 decision now observable as movement)");
    }
}
